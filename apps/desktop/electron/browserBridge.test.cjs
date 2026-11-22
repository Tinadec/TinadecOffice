const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
  ALLOWED_CDP_METHODS,
  createBrowserBridge,
  DIALOG_SAFE_METHOD,
  MAX_BODY_BYTES,
} = require('./browserBridge.cjs');

function createFakeAgentBrowser(overrides = {}) {
  const state = {
    session_id: 's1',
    control: 'agent',
    tabs: [{ id: 'tab-1', url: 'about:blank', title: '', loading: false, active: true }],
    active_tab: 'tab-1',
    dialog: null,
    notices: [],
    console_errors: [],
  };
  const calls = [];
  let active = 0;
  let maxActive = 0;
  const api = {
    calls,
    state,
    get maxActive() { return maxActive; },
    getState: async (sessionId) => ({
      ...state,
      session_id: sessionId,
      notices: [...state.notices],
      console_errors: [...(state.console_errors ?? [])],
    }),
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
    cdp: async (sessionId, tabId, method, params, options) => {
      calls.push({ op: 'cdp', sessionId, tabId, method, params, options });
      active += 1;
      maxActive = Math.max(maxActive, active);
      try {
        await new Promise((resolve) => setTimeout(resolve, 30));
        return { method, params, ...(options?.cdpSessionId ? { cdpSessionId: options.cdpSessionId } : {}) };
      } finally {
        active -= 1;
      }
    },
    screenshot: async (sessionId, tabId) => {
      calls.push({ op: 'screenshot', sessionId, tabId });
      return { path: path.join('shots', 'x.png'), bytes: 12 };
    },
    capturePreview: async (sessionId, options) => {
      calls.push({ op: 'capturePreview', sessionId, options });
      return {
        space_id: sessionId,
        session_id: sessionId,
        control: state.control,
        tab_id: options?.tabId ?? state.active_tab,
        url: state.tabs[0]?.url ?? '',
        title: state.tabs[0]?.title ?? '',
        dialog: state.dialog,
        data_url: 'data:image/png;base64,ZmFrZQ==',
        captured_at: '2026-11-22T00:00:00.000Z',
        console_errors: [...(state.console_errors ?? [])],
      };
    },
    listSpaces: () => [{ space_id: state.session_id, control: state.control, tab_count: state.tabs.length }],
    handleDialog: async (sessionId, accept, promptText) => {
      calls.push({ op: 'handleDialog', sessionId, accept, promptText });
      state.dialog = null;
      return { ...state };
    },
    handOff: (sessionId, options) => {
      calls.push({ op: 'handOff', sessionId, options });
      if (state.control === 'finished') {
        const err = new Error('finished');
        err.code = 'browser_task_space_finished';
        throw err;
      }
      state.control = 'user';
      return { ...state, session_id: sessionId };
    },
    takeOver: (sessionId) => {
      calls.push({ op: 'takeOver', sessionId });
      if (state.control === 'finished') {
        const err = new Error('finished');
        err.code = 'browser_task_space_finished';
        throw err;
      }
      state.control = 'agent';
      return { ...state, session_id: sessionId };
    },
    finish: (sessionId, options) => {
      calls.push({ op: 'finish', sessionId, options });
      state.control = 'finished';
      const keepSet = new Set(Array.isArray(options?.keep) ? options.keep : []);
      const kept = state.tabs.filter((tab) => keepSet.has(tab.id)).map((tab) => tab.id);
      const closed = state.tabs.filter((tab) => !keepSet.has(tab.id)).map((tab) => tab.id);
      state.tabs = state.tabs.filter((tab) => keepSet.has(tab.id));
      return { finished: true, control: 'finished', kept_tabs: kept, closed_tabs: closed, state: { ...state } };
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

test('DIALOG_SAFE_METHOD blocks renderer CDP calls while allowing Target.* and dialog handling', async (t) => {
  assert.equal(DIALOG_SAFE_METHOD('Page.handleJavaScriptDialog'), true);
  assert.equal(DIALOG_SAFE_METHOD('Target.getTargets'), true);
  assert.equal(DIALOG_SAFE_METHOD('Page.setDownloadBehavior'), true);
  assert.equal(DIALOG_SAFE_METHOD('Page.getFrameTree'), false);
  assert.equal(DIALOG_SAFE_METHOD('DOM.getDocument'), false);
  assert.equal(DIALOG_SAFE_METHOD('Accessibility.getFullAXTree'), false);

  const { bridge, agentBrowser } = startBridge(t);
  const info = await bridge.start();
  agentBrowser.state.dialog = { type: 'prompt', message: 'Enter code' };

  for (const method of ['Page.getFrameTree', 'DOM.getDocument', 'Accessibility.getFullAXTree']) {
    const blocked = await post(info, { op: 'cdp', session_id: 's1', method, params: {} });
    assert.equal(blocked.json.ok, false);
    assert.equal(blocked.json.code, 'browser_dialog_open');
    assert.equal(blocked.json.pending_dialog.message, 'Enter code');
  }

  const targetOk = await post(info, { op: 'cdp', session_id: 's1', method: 'Target.getTargets', params: {} });
  assert.equal(targetOk.json.ok, true);

  const dialogOp = await post(info, { op: 'dialog', session_id: 's1', accept: true, prompt_text: '1234' });
  assert.equal(dialogOp.json.ok, true);
  assert.equal(agentBrowser.state.dialog, null);
});

test('allows DOM.setFileInputFiles with validated file paths', async (t) => {
  assert.equal(ALLOWED_CDP_METHODS.has('DOM.setFileInputFiles'), true);
  const { bridge } = startBridge(t);
  const info = await bridge.start();

  const bad = await post(info, {
    op: 'cdp',
    session_id: 's1',
    method: 'DOM.setFileInputFiles',
    params: { files: ['bad\0path.txt'], backendNodeId: 12 },
  });
  assert.equal(bad.json.code, 'browser_invalid_params');

  const ok = await post(info, {
    op: 'cdp',
    session_id: 's1',
    method: 'DOM.setFileInputFiles',
    params: { files: ['C:/workspace/report.pdf'], backendNodeId: 12 },
  });
  assert.equal(ok.json.ok, true);
});

test('supports handoff, mirror, takeover, finish lifecycle ops and blocks resuming finished spaces', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  agentBrowser.state.console_errors = [{ seq: 1, level: 'error', message: 'Uncaught Error' }];
  const info = await bridge.start();

  const handoff = await post(info, { op: 'handoff', session_id: 's1', reason: '2fa' });
  assert.equal(handoff.json.ok, true);
  assert.equal(handoff.json.data.control, 'user');
  assert.equal(handoff.json.data.console_errors[0].message, 'Uncaught Error');

  // Read-only mirror works even while user is in control.
  const mirror = await post(info, { op: 'mirror', session_id: 's1' });
  assert.equal(mirror.json.ok, true);
  assert.equal(mirror.json.data.data_url, 'data:image/png;base64,ZmFrZQ==');

  // Takeover returns control to agent.
  const takeover = await post(info, { op: 'takeover', session_id: 's1' });
  assert.equal(takeover.json.ok, true);
  assert.equal(takeover.json.data.control, 'agent');

  // Finish transitions to terminal state and cleans up tabs.
  const finished = await post(info, { op: 'finish', session_id: 's1', keep: ['tab-1'] });
  assert.equal(finished.json.ok, true);
  assert.equal(finished.json.data.control, 'finished');
  assert.deepEqual(finished.json.data.kept_tabs, ['tab-1']);

  const resumeBlocked = await post(info, { op: 'takeover', session_id: 's1' });
  assert.equal(resumeBlocked.json.ok, false);
  assert.equal(resumeBlocked.json.code, 'browser_task_space_finished');
});

test('routes task_space isolation and forwards optional cdp_session_id', async (t) => {
  const { bridge, agentBrowser } = startBridge(t);
  const info = await bridge.start();

  const res = await post(info, {
    op: 'cdp',
    session_id: 's1',
    task_space: 'worker-a',
    cdp_session_id: 'oopif-9',
    method: 'DOM.getDocument',
    params: {},
  });
  assert.equal(res.json.ok, true);
  const call = agentBrowser.calls.find((entry) => entry.op === 'cdp');
  assert.equal(call.sessionId, 's1::worker-a');
  assert.equal(call.options.cdpSessionId, 'oopif-9');
});
