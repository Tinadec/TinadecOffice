const { randomUUID } = require('node:crypto');

/**
 * Main-process proxy for a session partner window. Like the companion window, a session pet
 * window is not a trusted host: it holds no host-control credential and gets no business IPC.
 * Its approval-bubble work goes through this one channel, which pins the origin, injects the
 * credential in the main process only, and refuses every path outside the approval whitelist plus
 * the two reads its own session needs (message list for the chat bubble, run list for the status
 * badge). The window may surface a pending approval and let the user click approve/reject, but it
 * can never reach a tool, a file, or another session's data.
 *
 * Every failure is a `{status, body}` value; no credential or upstream error text escapes.
 */

const GUID = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';

const routes = [
  // The list must be filtered to the caller's own session: an unfiltered read would expose every
  // pending approval in the storage scope. `session_id` alone does not achieve that — Core applies
  // it to approval rows only and appends policy parks from the permission ledger, which has no
  // session column — so the rows are also narrowed below before they reach the window.
  { method: 'GET', pattern: '^/api/v1/approvals$', requiresSessionQuery: true, ownSessionRows: true },
  // Deciding an approval is scoped to the caller's session below: the path carries no session id,
  // so the row is looked up in the caller's own list first and refused unless it appears there.
  // The single-approval read is deliberately absent: the Gateway serves no such route (only the
  // list and the decision), so probing it could only ever answer 404.
  { method: 'POST', pattern: `^/api/v1/approvals/(${GUID})/decision$`, approvalScoped: true },
  // Session-scoped: the window may read only its own session. The session id is captured from the
  // path so the caller can compare it with the sender's sessionKey (set at window creation, never
  // from renderer input).
  { method: 'GET', pattern: `^/api/v1/sessions/(${GUID})/messages$`, sessionScoped: true },
  { method: 'GET', pattern: `^/api/v1/sessions/(${GUID})/runs$`, sessionScoped: true },
].map(route => ({ ...route, pattern: new RegExp(route.pattern, 'i') }));

// Query keys are per route. A key a route does not need is refused rather than forwarded, so a
// future route cannot be probed by smuggling filters through the proxy.
const approvalQueryKeys = new Set(['status', 'session_id', 'limit']);
const sessionQueryKeys = new Set();
const allowedMethods = new Set(['GET', 'POST']);
const maxQueryLength = 1024;

function matchRoute(method, pathname) {
  for (const route of routes) {
    if (route.method !== method) continue;
    const match = route.pattern.exec(pathname);
    if (match) return { route, sessionId: route.sessionScoped ? match[1] : null, approvalId: route.approvalScoped ? match[1] : null };
  }
  return null;
}

function parsePath(value) {
  if (typeof value !== 'string' || !value.startsWith('/api/v1/')) return null;
  if (value.includes('://') || value.includes('#') || value.length > 2048) return null;
  if (/[\s\r\n]/.test(value)) return null;
  const marker = value.indexOf('?');
  const pathname = marker < 0 ? value : value.slice(0, marker);
  const search = marker < 0 ? '' : value.slice(marker + 1);
  if (search.length > maxQueryLength) return null;
  let decoded;
  try { decoded = decodeURIComponent(pathname); } catch { return null; }
  if (decoded.includes('..')) return null;
  return { pathname, search };
}

function assertQueryKeys(search, allowed) {
  if (!search) return true;
  const seen = new Set();
  for (const key of new URLSearchParams(search).keys()) {
    // A repeated key cannot be forwarded verbatim-safe: the session pin below reads the first
    // value, while the Gateway resolves duplicates to the last one, so
    // `session_id=<own>&session_id=<other>` would pass the pin and still be answered for <other>.
    if (seen.has(key)) return false;
    if (!allowed.has(key)) return false;
    seen.add(key);
  }
  return true;
}

function serializeBody(body, maxBodyBytes) {
  if (body === undefined) return { ok: true, text: undefined };
  if (body === null || typeof body !== 'object' || Array.isArray(body)) return { ok: false };
  let text;
  try { text = JSON.stringify(body); } catch { return { ok: false }; }
  if (typeof text !== 'string' || Buffer.byteLength(text, 'utf8') > maxBodyBytes) return { ok: false };
  return { ok: true, text };
}

function invalid(message) {
  return { status: 400, body: { code: 'invalid_request', message } };
}

function createPetAgentApiBridge({
  baseUrl,
  token,
  isSessionPartnerWindowSender,
  sessionIdForSender,
  storageIdForSender,
  fetchImpl = globalThis.fetch,
  timeoutMs = 15000,
  maxBodyBytes = 16 * 1024,
} = {}) {
  if (typeof isSessionPartnerWindowSender !== 'function') throw new Error('A session partner window sender check is required.');
  if (typeof fetchImpl !== 'function') throw new Error('A fetch implementation is required.');
  const origin = typeof baseUrl === 'string' ? baseUrl.replace(/\/+$/, '') : '';

  /**
   * The storage scope is fixed when the window opens, by the trusted renderer, and is never taken
   * from the window's own request. A session in a project lives in that project's store; a read
   * without this header resolves in the user store, where the session does not exist, so the
   * window would show an empty chat that never explains itself.
   */
  const storageHeader = (sender) => ({
    'x-tinadec-storage-id': typeof storageIdForSender === 'function' ? storageIdForSender(sender) || 'user' : 'user',
  });

  return async function bridge(event, payload) {
    if (!isSessionPartnerWindowSender(event?.sender)) {
      throw new Error('Pet agent API IPC is only available to a session partner window.');
    }
    // Resolved once, from the sender's window entry, so every upstream read below carries the
    // same scope this window was opened in.
    const scope = storageHeader(event.sender);
    if (!origin || typeof token !== 'string' || token.length === 0) {
      return { status: 503, body: { code: 'host_not_ready' } };
    }
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
      return invalid('A pet agent request payload is required.');
    }
    const method = payload.method;
    if (!allowedMethods.has(method)) return invalid('Unsupported pet agent request method.');
    const path = parsePath(payload.path);
    if (!path) return invalid('Path is not allowed for the session partner window.');
    const matched = matchRoute(method, path.pathname);
    if (!matched) return invalid('Path is not allowed for the session partner window.');
    const allowedKeys = matched.route.sessionScoped ? sessionQueryKeys : approvalQueryKeys;
    if (!assertQueryKeys(path.search, allowedKeys)) {
      return invalid('Path is not allowed for the session partner window.');
    }
    // Session reads and the approval list are pinned to the window's own session. Without this the
    // whitelist would be a "read any session's chat/approvals" grant, which is exactly the
    // cross-session access this proxy exists to prevent.
    const ownSessionId = typeof sessionIdForSender === 'function' ? sessionIdForSender(event.sender) : null;
    const sameSession = (value) => Boolean(ownSessionId) && typeof value === 'string'
      && ownSessionId.toLowerCase() === value.toLowerCase();
    if (matched.sessionId && !sameSession(matched.sessionId)) {
      return invalid('Path is not allowed for the session partner window.');
    }
    const requestedSession = path.search ? new URLSearchParams(path.search).get('session_id') : null;
    if (requestedSession !== null && !sameSession(requestedSession)) {
      return invalid('Path is not allowed for the session partner window.');
    }
    if (matched.route.requiresSessionQuery && requestedSession === null) {
      return invalid('Approvals must be read for the window’s own session.');
    }
    // Core's approval list mixes approval rows (which carry a session id) with policy parks (which
    // do not), and `?session_id=` is applied to the former only. A park's only binding is its run,
    // so ownership is decided here, over the rows this window could ever be shown. The run read is
    // paid only when the list actually contains a park.
    const ownApprovals = async () => {
      if (!ownSessionId) return { ok: false, result: invalid('Approval is not available to this session partner window.') };
      let rows;
      try {
        const list = await fetchImpl(
          `${origin}/api/v1/approvals?status=pending&session_id=${encodeURIComponent(ownSessionId)}`,
          {
            method: 'GET',
            headers: { accept: 'application/json', 'x-tinadec-host-control': token, ...scope },
            signal: AbortSignal.timeout(timeoutMs),
            redirect: 'error',
          });
        if (list.status < 200 || list.status >= 300) return { ok: false, result: invalid('Approval is not available to this session partner window.') };
        rows = JSON.parse(await list.text());
      } catch {
        return { ok: false, result: { status: 502, body: { code: 'gateway_unreachable' } } };
      }
      if (!Array.isArray(rows)) return { ok: false, result: invalid('Approval is not available to this session partner window.') };
      const parked = rows.some((row) => typeof row?.session_id !== 'string' && typeof row?.run_id === 'string');
      let runIds = [];
      if (parked) {
        try {
          const runs = await fetchImpl(`${origin}/api/v1/sessions/${encodeURIComponent(ownSessionId)}/runs`, {
            method: 'GET',
            headers: { accept: 'application/json', 'x-tinadec-host-control': token, ...scope },
            signal: AbortSignal.timeout(timeoutMs),
            redirect: 'error',
          });
          if (runs.status < 200 || runs.status >= 300) return { ok: false, result: invalid('Approval is not available to this session partner window.') };
          const runBody = JSON.parse(await runs.text());
          runIds = Array.isArray(runBody) ? runBody.map((entry) => String(entry?.id ?? '').toLowerCase()) : [];
        } catch {
          return { ok: false, result: { status: 502, body: { code: 'gateway_unreachable' } } };
        }
      }
      const owned = rows.filter((row) => typeof row?.session_id === 'string'
        ? sameSession(row.session_id)
        : typeof row?.run_id === 'string' && runIds.includes(row.run_id.toLowerCase()));
      return { ok: true, rows: owned };
    };

    // An approval id in the path is not proof of ownership: it has to be one of the rows this
    // window may see, so a forged id cannot decide another session's approval.
    if (matched.approvalId) {
      const owned = await ownApprovals();
      if (!owned.ok) return owned.result;
      const wanted = matched.approvalId.toLowerCase();
      if (!owned.rows.some((row) => String(row?.id ?? '').toLowerCase() === wanted)) {
        return invalid('Approval is not available to this session partner window.');
      }
    }

    const body = serializeBody(payload.body, maxBodyBytes);
    if (!body.ok) return invalid('Request body must be a JSON object within the size limit.');
    if (body.text !== undefined && method === 'GET') return invalid('GET requests cannot carry a body.');

    // Return the narrowed list rather than Core's: a park would otherwise reach a window that has
    // no business seeing it.
    if (matched.route.ownSessionRows) {
      const owned = await ownApprovals();
      return owned.ok ? { status: 200, body: owned.rows } : owned.result;
    }

    const target = `${origin}${path.pathname}${path.search ? `?${path.search}` : ''}`;
    let response;
    try {
      response = await fetchImpl(target, {
        method,
        headers: {
          accept: 'application/json',
          'content-type': 'application/json',
          'x-tinadec-host-control': token,
          'x-request-id': randomUUID(),
          ...scope,
        },
        ...(body.text === undefined ? {} : { body: body.text }),
        signal: AbortSignal.timeout(timeoutMs),
        redirect: 'error',
      });
    } catch {
      return { status: 502, body: { code: 'gateway_unreachable' } };
    }

    let text;
    try {
      text = await response.text();
    } catch {
      return { status: 502, body: { code: 'gateway_unreachable' } };
    }
    if (typeof text !== 'string' || text.length === 0) {
      return { status: response.status, body: null };
    }
    try {
      return { status: response.status, body: JSON.parse(text) };
    } catch {
      return { status: response.status, body: { code: 'invalid_upstream_response' } };
    }
  };
}

module.exports = { createPetAgentApiBridge };