/**
 * The agent browser bridge.
 *
 * Owns the only control surface between the .NET tool process and the Electron-owned browser.
 * It is deliberately tiny and boring: loopback-only HTTP, one random bearer token written to a
 * protected file, a strict CDP method allowlist, and per-session serialization. The semantic
 * layer (a11y snapshot, ref registry, action resolution, waits) stays in TinadecTools; this file
 * never grows a "click" or "snapshot" command of its own.
 *
 * Why a file instead of an environment variable: `TinadecTools/Runtime/TerminalRunner.cs` and
 * some other helpers spawn children that inherit the whole environment. A token in the env would
 * be readable by the agent's own shell commands. The file lives under
 * `<officeRoot>/state/agent-browser/bridge.json`, which the tool process already knows through
 * `TINADEC_HOME`.
 */

const http = require('node:http');
const crypto = require('node:crypto');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');

const BRIDGE_VERSION = 1;
const MAX_BODY_BYTES = 64 * 1024;
const MAX_AUDIT_BYTES = 5 * 1024 * 1024;
const SESSION_ID = /^[A-Za-z0-9._:-]{1,200}$/;
const TAB_ID = /^[A-Za-z0-9._:-]{1,80}$/;

/** Methods the .NET tools are allowed to send. Everything else is refused, not forwarded. */
const ALLOWED_CDP_METHODS = new Set([
  'Page.enable',
  'Page.disable',
  'Page.navigate',
  'Page.reload',
  'Page.getFrameTree',
  'Page.getNavigationHistory',
  'Page.navigateToHistoryEntry',
  'Page.handleJavaScriptDialog',
  'Page.setInterceptFileChooserDialog',
  'DOM.enable',
  'DOM.disable',
  'DOM.getDocument',
  'DOM.querySelector',
  'DOM.querySelectorAll',
  'DOM.describeNode',
  'DOM.resolveNode',
  'DOM.pushNodesByBackendIdsToFrontend',
  'DOM.getBoxModel',
  'DOM.scrollIntoViewIfNeeded',
  'DOM.focus',
  'Runtime.enable',
  'Runtime.disable',
  'Runtime.evaluate',
  'Runtime.callFunctionOn',
  'Runtime.releaseObject',
  'Accessibility.enable',
  'Accessibility.disable',
  'Accessibility.getFullAXTree',
  'Accessibility.getPartialAXTree',
  'Accessibility.getRootAXNode',
  'Input.dispatchMouseEvent',
  'Input.dispatchKeyEvent',
  'Input.insertText',
  'Target.getTargets',
  'Target.attachToTarget',
  'Target.detachFromTarget',
]);

/**
 * Defense in depth for the two methods that carry a script. The tool process is the capability
 * holder (a model can never pass a script through a tool parameter), but a compromised tool
 * process should not get cookies/storage/network "for free". The .NET side also has a structural
 * test that no model parameter reaches these method bodies.
 */
const BLOCKED_SCRIPT_TOKENS = [
  'document.cookie',
  'localstorage',
  'sessionstorage',
  'indexeddb',
  'xmlhttprequest',
  'websocket',
  'fetch(',
  'eval(',
  'new function',
  'require(',
  'process.',
];

/** Metadata endpoints that must never be reachable even through a literal hostname/IP. */
const BLOCKED_HOSTS = new Set([
  'metadata.google.internal',
  'metadata.goog',
  '169.254.169.254',
  '[fd00:ec2::254]',
]);

const OPS = new Set([
  'cdp',
  'tabs/open',
  'tabs/list',
  'tabs/select',
  'tabs/close',
  'screenshot',
  'dialog',
  'state',
]);

const READ_ONLY_OPS = new Set(['state', 'tabs/list']);

function nowIso(now) {
  return (now ? now() : new Date()).toISOString();
}

function sanitizeSessionId(value) {
  const id = typeof value === 'string' ? value.trim() : '';
  return SESSION_ID.test(id) ? id : null;
}

function sanitizeTabId(value) {
  if (value === undefined || value === null || value === '') return null;
  const id = typeof value === 'string' ? value.trim() : '';
  return TAB_ID.test(id) ? id : null;
}

function parseHttpUrl(raw) {
  if (typeof raw !== 'string' || raw.length > 4096) return null;
  let url;
  try {
    url = new URL(raw);
  } catch {
    return null;
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
  if (url.username || url.password) return null;
  if (!url.hostname) return null;
  return url;
}

function blockedHost(url) {
  const host = url.hostname.toLowerCase();
  return BLOCKED_HOSTS.has(host) || host.startsWith('169.254.');
}

function scriptIsBlocked(script) {
  if (typeof script !== 'string' || script.length > 32 * 1024) return true;
  const lower = script.toLowerCase();
  return BLOCKED_SCRIPT_TOKENS.some((token) => lower.includes(token));
}

function numberInRange(value, min, max) {
  return typeof value === 'number' && Number.isFinite(value) && value >= min && value <= max;
}

function fail(code, permanent, message) {
  return { ok: false, code, permanent: permanent !== false, message: message ?? code };
}

function bridgeError(error) {
  const code = typeof error?.code === 'string' ? error.code : 'browser_bridge_error';
  const permanent = typeof error?.permanent === 'boolean' ? error.permanent : true;
  return fail(code, permanent, error?.message);
}

function createBrowserBridge({
  agentBrowser,
  officeRoot,
  logger = console,
  randomBytes = crypto.randomBytes,
  now = () => new Date(),
  httpServerFactory = http.createServer,
}) {
  if (!agentBrowser || typeof agentBrowser.getState !== 'function') {
    throw new TypeError('createBrowserBridge requires agentBrowser');
  }
  if (typeof officeRoot !== 'string' || !path.isAbsolute(officeRoot)) {
    throw new TypeError('createBrowserBridge requires an absolute officeRoot');
  }

  const stateDir = path.join(officeRoot, 'state', 'agent-browser');
  const bridgeFile = path.join(stateDir, 'bridge.json');
  const auditFile = path.join(stateDir, 'audit.jsonl');
  const shotsDir = path.join(stateDir, 'shots');
  const token = randomBytes(32).toString('base64url');

  let server = null;
  let port = null;
  let stopped = false;
  const gates = new Map();

  // ---------------------------------------------------------------- files

  function ensureStateDir() {
    fs.mkdirSync(stateDir, { recursive: true, mode: 0o700 });
  }

  function writeTokenFile() {
    ensureStateDir();
    const payload = {
      version: BRIDGE_VERSION,
      url: `http://127.0.0.1:${port}`,
      token,
      pid: process.pid,
      created_at: nowIso(now),
    };
    const tmp = `${bridgeFile}.${process.pid}.tmp`;
    fs.writeFileSync(tmp, JSON.stringify(payload), { mode: 0o600 });
    fs.renameSync(tmp, bridgeFile);
  }

  function removeTokenFile() {
    try { fs.rmSync(bridgeFile, { force: true }); } catch { /* best effort */ }
  }

  function audit(entry) {
    try {
      ensureStateDir();
      try {
        if (fs.statSync(auditFile).size > MAX_AUDIT_BYTES) fs.renameSync(auditFile, `${auditFile}.1`);
      } catch { /* no file yet */ }
      fs.appendFileSync(auditFile, JSON.stringify({ at: nowIso(now), ...entry }) + '\n');
    } catch (error) {
      logger.warn?.('[agent-browser] audit write failed:', error?.message ?? error);
    }
  }

  // ---------------------------------------------------------------- auth

  function authorize(header) {
    if (typeof header !== 'string' || !header.startsWith('Bearer ')) return false;
    const given = Buffer.from(header.slice('Bearer '.length).trim(), 'utf8');
    const expected = Buffer.from(token, 'utf8');
    return given.length === expected.length && crypto.timingSafeEqual(given, expected);
  }

  // ---------------------------------------------------------------- serialization

  function exclusive(key, fn) {
    const previous = gates.get(key) ?? Promise.resolve();
    const run = previous.catch(() => {}).then(fn);
    const tracked = run.finally(() => {
      if (gates.get(key) === tracked) gates.delete(key);
    });
    gates.set(key, tracked);
    return run;
  }

  // ---------------------------------------------------------------- validation

  function validateCdp(method, params) {
    if (typeof method !== 'string' || !ALLOWED_CDP_METHODS.has(method)) {
      return fail('browser_method_denied', false, `CDP method is not allowed: ${method}`);
    }
    if (params !== undefined && (params === null || typeof params !== 'object' || Array.isArray(params))) {
      return fail('browser_invalid_params', false);
    }
    const value = params ?? {};
    if (method === 'Page.navigate') {
      const url = parseHttpUrl(value.url);
      if (!url) return fail('browser_navigation_blocked', false);
      if (blockedHost(url)) return fail('browser_navigation_blocked', false);
      value.url = url.toString();
    }
    if (method === 'Runtime.evaluate' && scriptIsBlocked(value.expression)) {
      return fail('browser_script_blocked', false);
    }
    if (method === 'Runtime.callFunctionOn' && scriptIsBlocked(value.functionDeclaration)) {
      return fail('browser_script_blocked', false);
    }
    if (method === 'Input.insertText') {
      if (typeof value.text !== 'string' || value.text.length > 32 * 1024) {
        return fail('browser_invalid_params', false);
      }
    }
    if (method === 'Input.dispatchMouseEvent') {
      if (!numberInRange(value.x, -100_000, 100_000) || !numberInRange(value.y, -100_000, 100_000)) {
        return fail('browser_invalid_params', false);
      }
      if (!['mouseMoved', 'mousePressed', 'mouseReleased', 'mouseWheel'].includes(value.type)) {
        return fail('browser_invalid_params', false);
      }
      if (value.type === 'mouseWheel' && (!numberInRange(value.deltaX ?? 0, -100_000, 100_000)
        || !numberInRange(value.deltaY ?? 0, -100_000, 100_000))) {
        return fail('browser_invalid_params', false);
      }
    }
    if (method === 'Input.dispatchKeyEvent') {
      if (typeof value.type !== 'string' || typeof value.key !== 'string' || value.key.length > 128) {
        return fail('browser_invalid_params', false);
      }
      if (value.text !== undefined && (typeof value.text !== 'string' || value.text.length > 32 * 1024)) {
        return fail('browser_invalid_params', false);
      }
    }
    if (method === 'Page.handleJavaScriptDialog' && typeof value.accept !== 'boolean') {
      return fail('browser_invalid_params', false);
    }
    if (value.promptText !== undefined && (typeof value.promptText !== 'string' || value.promptText.length > 4096)) {
      return fail('browser_invalid_params', false);
    }
    return null;
  }

  async function withNotices(sessionId, data) {
    const after = await agentBrowser.getState(sessionId);
    return {
      ok: true,
      data: {
        ...(data && typeof data === 'object' ? data : {}),
        notices: Array.isArray(after?.notices) ? after.notices.slice(-5) : [],
        notice_seq: Number.isFinite(after?.notice_seq) ? after.notice_seq : 0,
      },
    };
  }

  // ---------------------------------------------------------------- dispatch

  async function dispatch(body) {
    if (!body || typeof body !== 'object' || Array.isArray(body)) return fail('browser_bridge_bad_request', false);
    const op = body.op;
    if (!OPS.has(op)) return fail('browser_bridge_bad_op', false);
    const sessionId = sanitizeSessionId(body.session_id);
    if (!sessionId) return fail('browser_session_invalid', false);

    const state = await agentBrowser.getState(sessionId);
    const readOnly = READ_ONLY_OPS.has(op);

    if (!readOnly && state?.control === 'user') return fail('browser_user_in_control', true);

    if (op === 'state') return { ok: true, data: state };
    if (op === 'tabs/list') return { ok: true, data: state?.tabs ?? [] };

    return exclusive(sessionId, async () => {
      try {
        if (op === 'tabs/open') {
          const url = body.url === undefined || body.url === null || body.url === '' ? null : parseHttpUrl(body.url);
          if (body.url && !url) return fail('browser_navigation_blocked', false);
          if (url && blockedHost(url)) return fail('browser_navigation_blocked', false);
          const tab = await agentBrowser.openTab(sessionId, url ? url.toString() : null);
          audit({ session_id: sessionId, tab_id: tab?.tab?.id, op, url: url ? url.toString() : undefined });
          return withNotices(sessionId, tab);
        }
        if (op === 'tabs/select') {
          const tabId = sanitizeTabId(body.tab_id);
          if (!tabId) return fail('browser_tab_invalid', false);
          const tab = await agentBrowser.selectTab(sessionId, tabId);
          audit({ session_id: sessionId, tab_id: tabId, op });
          return withNotices(sessionId, tab);
        }
        if (op === 'tabs/close') {
          const tabId = sanitizeTabId(body.tab_id);
          if (!tabId) return fail('browser_tab_invalid', false);
          const result = await agentBrowser.closeTab(sessionId, tabId);
          audit({ session_id: sessionId, tab_id: tabId, op });
          return withNotices(sessionId, result);
        }
        if (op === 'dialog') {
          if (typeof body.accept !== 'boolean') return fail('browser_invalid_params', false);
          if (body.prompt_text !== undefined && (typeof body.prompt_text !== 'string' || body.prompt_text.length > 4096)) {
            return fail('browser_invalid_params', false);
          }
          const result = await agentBrowser.handleDialog(sessionId, body.accept, body.prompt_text);
          audit({ session_id: sessionId, op, code: result?.ok === false ? result.code : undefined });
          return result?.ok === false ? result : { ok: true, data: result };
        }
        if (op === 'screenshot') {
          const tabId = sanitizeTabId(body.tab_id) ?? state?.active_tab ?? null;
          if (!tabId) return fail('browser_not_started', false);
          if (state?.dialog) return fail('browser_dialog_open', true);
          const result = await agentBrowser.screenshot(sessionId, tabId, body.full_page === true, shotsDir);
          audit({ session_id: sessionId, tab_id: tabId, op });
          return withNotices(sessionId, result);
        }

        // op === 'cdp'
        const tabId = sanitizeTabId(body.tab_id) ?? state?.active_tab ?? null;
        if (!tabId) return fail('browser_not_started', true);
        const invalid = validateCdp(body.method, body.params);
        if (invalid) return invalid;
        if (state?.dialog && body.method !== 'Page.handleJavaScriptDialog') {
          return fail('browser_dialog_open', true);
        }
        const result = await agentBrowser.cdp(sessionId, tabId, body.method, body.params ?? {});
        audit({
          session_id: sessionId,
          tab_id: tabId,
          op,
          method: body.method,
          url: body.method === 'Page.navigate' ? body.params?.url : undefined,
        });
        return withNotices(sessionId, { result: result ?? {} });
      } catch (error) {
        const result = bridgeError(error);
        audit({ session_id: sessionId, op, code: result.code });
        return result;
      }
    });
  }

  // ---------------------------------------------------------------- HTTP

  function readBody(req) {
    return new Promise((resolve, reject) => {
      const chunks = [];
      let size = 0;
      let tooLarge = false;
      req.on('data', (chunk) => {
        size += chunk.length;
        if (size > MAX_BODY_BYTES) {
          // Keep draining instead of destroying the socket, so the client receives the 413
          // instead of a bare ECONNRESET. The chunks are discarded.
          tooLarge = true;
          chunks.length = 0;
          return;
        }
        chunks.push(chunk);
      });
      req.on('end', () => {
        if (tooLarge) {
          const error = new Error('request body too large');
          error.code = 'request_too_large';
          reject(error);
          return;
        }
        const raw = Buffer.concat(chunks).toString('utf8');
        if (!raw) return resolve({});
        try { resolve(JSON.parse(raw)); } catch (error) { reject(Object.assign(error, { code: 'bad_json' })); }
      });
      req.on('error', reject);
    });
  }

  function send(res, status, body) {
    if (res.headersSent || res.destroyed) return;
    const payload = JSON.stringify(body ?? {});
    res.writeHead(status, {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': 'no-store',
      'content-length': Buffer.byteLength(payload),
    });
    res.end(payload);
  }

  async function handle(req, res) {
    if (req.headers.origin) return send(res, 403, fail('browser_origin_denied', false));
    if (req.method === 'GET' && req.url === '/health') {
      if (!authorize(req.headers.authorization)) return send(res, 401, fail('browser_unauthorized', false));
      return send(res, 200, { ok: true, data: { version: BRIDGE_VERSION, port } });
    }
    if (req.method !== 'POST') return send(res, 405, fail('browser_method_not_allowed', false));
    if (!authorize(req.headers.authorization)) return send(res, 401, fail('browser_unauthorized', false));
    let body;
    try { body = await readBody(req); }
    catch (error) {
      return send(res, error?.code === 'request_too_large' ? 413 : 400, fail(error?.code ?? 'browser_bad_request', false));
    }
    try {
      const result = await dispatch(body);
      send(res, 200, result);
    } catch (error) {
      logger.error?.('[agent-browser] bridge dispatch failed:', error?.message ?? error);
      send(res, 500, fail('browser_bridge_internal', false));
    }
  }

  // ---------------------------------------------------------------- lifecycle

  async function start() {
    if (server) return info();
    stopped = false;
    ensureStateDir();
    server = httpServerFactory((req, res) => { void handle(req, res); });
    server.headersTimeout = 10_000;
    server.requestTimeout = 120_000;
    await new Promise((resolve, reject) => {
      const onError = (error) => { server?.removeListener('listening', onListening); reject(error); };
      const onListening = () => { server?.removeListener('error', onError); resolve(); };
      server.once('error', onError);
      server.once('listening', onListening);
      server.listen(0, '127.0.0.1');
    });
    port = server.address().port;
    writeTokenFile();
    logger.info?.(`[agent-browser] bridge listening on http://127.0.0.1:${port}`);
    return info();
  }

  async function stop() {
    if (!server) { removeTokenFile(); return; }
    const current = server;
    server = null;
    stopped = true;
    try { current.closeAllConnections?.(); } catch { /* not available */ }
    await new Promise((resolve) => current.close(() => resolve()));
    removeTokenFile();
  }

  /** `before-quit` cannot await; close the listener and drop the token synchronously. */
  function stopSync() {
    if (server) {
      try { server.closeAllConnections?.(); } catch { /* not available */ }
      try { server.close(); } catch { /* already closed */ }
      server = null;
    }
    stopped = true;
    removeTokenFile();
  }

  function info() {
    return { url: port ? `http://127.0.0.1:${port}` : null, port, token, bridgeFile, auditFile };
  }

  return {
    start,
    stop,
    stopSync,
    dispatch,
    authorize,
    info,
    get stopped() { return stopped; },
    // Exported for tests; production callers use start()/stop().
    handle,
  };
}

module.exports = {
  ALLOWED_CDP_METHODS,
  BLOCKED_SCRIPT_TOKENS,
  BRIDGE_VERSION,
  MAX_BODY_BYTES,
  createBrowserBridge,
};
