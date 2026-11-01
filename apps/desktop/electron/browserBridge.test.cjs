const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createBrowserBridge, MAX_BODY_BYTES } = require('./browserBridge.cjs');

function createFakeAgentBrowser(overrides = {}) {
  const state = {
    session_id: 's1',
    control: 'agent',
    tabs: [{ id: 'tab-1', url: 'about:blank', title: '', loading: false, active: true }],
    active_tab: 'tab-1',
    dialog: null,
    notices: [],
  };
  const calls = [];
  let active = 0;
  let maxActive = 0;
  const api = {
    calls,
    state,
    get maxActive() { return maxActive; },
    getState: async (sessionId) => ({ ...state, session_id: sessionId, notices: [...state.notices] }),
    openTab: async (sessionId, url) => {
      calls.push({ op: 'openTab', sessionId, url });
      state.tabs = [{ id: 'tab-1', url: url ?? 'about:blank', title: '', loading: false, active: true }];
      state.active_tab = 'tab-1';
      return { tab: { ...state.tabs[0] }, state };
    },
    selectTab: async (sessionId, tabId) => {
      calls.push({ op: 'selectTab', sessionId, tabId });
      return { tab: { id: tabId }, state };
    },
    closeTab: async (sessionId, tabId) => {
      calls.push({ op: 'closeTab', sessionId, tabId });
      state.tabs = [];
      state.active_tab = null;
      return { closed: tabId, state };
    },
    cdp: async (sessionId, tabId, method, params) => {
      calls.push({ op: 'cdp', sessionId, tabId, method, params });
      active += 1;
      maxActive = Math.max(maxActive, active);
      try {
        await new Promise((resolve) => setTimeout(resolve, 30));
        return { method, params };
      } finally {
        active -= 1;
      }
    },
    screenshot: async (sessionId, tabId) => {
      calls.push({ op: 'screenshot', sessionId, tabId });
      return { path: path.join('shots', 'x.png'), bytes: 12 };
    },
    handleDialog: async (sessionId, accept) => {
      calls.push({ op: 'handleDialog', sessionId, accept });
      state.dialog = null;
      return { ...state };
    },
    setControl: (sessionId, mode) => {
      state.control = mode;
      return { ...state };
    },
    ...overrides,
  };
  return api;
}

function startBridge(t, overrides) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-bridge-'));
  const agentBrowser = createFakeAgentBrowser(overrides);
  const bridge = createBrowserBridge({
    agentBrowser,
    officeRoot: root,
    logger: { info() {}, warn() {}, error() {} },
  });
  t.after(async () => {
    await bridge.stop();
    fs.rmSync(root, { recursive: true, force: true });
  });
  return { bridge, agentBrowser, root };
}

async function post(info, body, { token = info.token, origin } = {}) {
  const headers = { 'content-type': 'application/json' };
  if (token !== null) headers.authorization = `Bearer ${token}`;
  if (origin) headers.origin = origin;
  const response = await fetch(`${info.url}/`, {
    method: 'POST',
    headers,
    body: typeof body === 'string' ? body : JSON.stringify(body),
  });
  return { status: response.status, json: await response.json().catch(() => null) };
}

test('start writes a protected token file and stop removes it', async (t) => {
  const { bridge, root } = startBridge(t);
  const info = await bridge.start();
  assert.ok(fs.existsSync(info.bridgeFile), 'token file exists');
  const payload = JSON.parse(fs.readFileSync(info.bridgeFile, 'utf8'));
  assert.equal(payload.url, info.url);
  assert.equal(payload.token, info.token);
  assert.match(payload.url, /^http:\/\/127\.0\.0\.1:\d+$/);
  assert.equal(payload.pid, process.pid);
  assert.equal(payload.version, 1);

  await bridge.stop();
  assert.equal(fs.existsSync(info.bridgeFile), false, 'token file removed on stop');
  assert.equal(fs.existsSync(root), true);
});

test('rejects a missing or wrong bearer token', async (t) => {
  const { bridge } = startBridge(t);
  const info = await bridge.start();
  assert.equal((await post(info, { op: 'state', session_id: 's1' }, { token: null })).status, 401);
  assert.equal((await post(info, { op: 'state', session_id: 's1' }, { token: 'nope' })).status, 401);
});

test('rejects a browser-originated request even with a valid token', async (t) => {
  const { bridge } = startBridge(t);
  const info = await bridge.start();
  const result = await post(info, { op: 'state', session_id: 's1' }, { origin: 'https://evil.example' });
  assert.equal(result.status, 403);
  assert.equal(result.json.code, 'browser_origin_denied');
});

test('refuses unknown operations and malformed sessions', async (t) => {
  const { bridge } = startBridge(t);
  const info = await bridge.start();
  assert.equal((await post(info, { op: 'bogus', session_id: 's1' })).json.code, 'browser_bridge_bad_op');
  assert.equal((await post(info, { op: 'state', session_id: '../x' })).json.code, 'browser_session_invalid');
});

test('refuses CDP methods outside the allowlist without calling the manager', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  const info = await bridge.start();
  const result = await post(info, { op: 'cdp', session_id: 's1', method: 'Browser.getVersion', params: {} });
  assert.equal(result.json.ok, false);
  assert.equal(result.json.code, 'browser_method_denied');
  assert.equal(agentBrowser.calls.filter((call) => call.op === 'cdp').length, 0);
});

test('refuses javascript: navigation and metadata hosts', async (t) => {
  const { bridge } = startBridge(t);
  const info = await bridge.start();
  const js = await post(info, { op: 'cdp', session_id: 's1', method: 'Page.navigate', params: { url: 'javascript:alert(1)' } });
  assert.equal(js.json.code, 'browser_navigation_blocked');
  const meta = await post(info, {
    op: 'cdp',
    session_id: 's1',
    method: 'Page.navigate',
    params: { url: 'http://169.254.169.254/latest/meta-data' },
  });
  assert.equal(meta.json.code, 'browser_navigation_blocked');
});

test('refuses script bodies that reach cookies or storage', async (t) => {
  const { bridge } = startBridge(t);
  const info = await bridge.start();
  const result = await post(info, {
    op: 'cdp',
    session_id: 's1',
    method: 'Runtime.evaluate',
    params: { expression: 'document.cookie' },
  });
  assert.equal(result.json.code, 'browser_script_blocked');
});

test('returns the CDP result together with hard-stop notices', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  agentBrowser.state.notices = [{ code: 'browser_popup_blocked', url: 'https://popup.example' }];
  const info = await bridge.start();
  const result = await post(info, { op: 'cdp', session_id: 's1', method: 'Page.reload', params: {} });
  assert.equal(result.json.ok, true);
  assert.equal(result.json.data.result.method, 'Page.reload');
  assert.equal(result.json.data.notices[0].code, 'browser_popup_blocked');
});

test('hard-stops every agent operation while the user has control', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  const info = await bridge.start();
  agentBrowser.state.control = 'user';
  const blocked = await post(info, { op: 'cdp', session_id: 's1', method: 'Page.reload', params: {} });
  assert.equal(blocked.json.code, 'browser_user_in_control');
  const read = await post(info, { op: 'state', session_id: 's1' });
  assert.equal(read.json.ok, true);
  assert.equal(read.json.data.control, 'user');
});

test('blocks ordinary commands while a JavaScript dialog is open', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  const info = await bridge.start();
  agentBrowser.state.dialog = { type: 'alert', message: 'hi' };
  const blocked = await post(info, { op: 'cdp', session_id: 's1', method: 'Page.reload', params: {} });
  assert.equal(blocked.json.code, 'browser_dialog_open');
  const handled = await post(info, {
    op: 'cdp',
    session_id: 's1',
    method: 'Page.handleJavaScriptDialog',
    params: { accept: true },
  });
  assert.equal(handled.json.ok, true);
});

test('refuses an oversized request body with 413', async (t) => {
  const { bridge } = startBridge(t);
  const info = await bridge.start();
  const result = await post(info, { op: 'state', session_id: 's1', padding: 'x'.repeat(MAX_BODY_BYTES + 1024) });
  assert.equal(result.status, 413);
  assert.equal(result.json.code, 'request_too_large');
});

test('serializes commands inside one session', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  const info = await bridge.start();
  await Promise.all([
    post(info, { op: 'cdp', session_id: 's1', method: 'Page.reload', params: {} }),
    post(info, { op: 'cdp', session_id: 's1', method: 'Page.reload', params: {} }),
  ]);
  assert.equal(agentBrowser.maxActive, 1, 'the bridge never runs two commands for one session at once');
});

test('opens a tab through the lifecycle op', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  const info = await bridge.start();
  const result = await post(info, { op: 'tabs/open', session_id: 's1', url: 'https://example.com/docs' });
  assert.equal(result.json.ok, true);
  assert.equal(result.json.data.tab.url, 'https://example.com/docs');
  assert.equal(agentBrowser.calls.filter((call) => call.op === 'openTab').length, 1);
});

test('reports browser_host_unavailable through a coded error from the manager', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  const info = await bridge.start();
  agentBrowser.cdp = async () => {
    const error = new Error('host gone');
    error.code = 'browser_host_unavailable';
    error.permanent = true;
    throw error;
  };
  const result = await post(info, { op: 'cdp', session_id: 's1', method: 'Page.reload', params: {} });
  assert.equal(result.json.ok, false);
  assert.equal(result.json.code, 'browser_host_unavailable');
  assert.equal(result.json.permanent, true);
});
