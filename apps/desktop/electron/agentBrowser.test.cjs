const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
  createAgentBrowserManager,
  isDialogSafeMethod,
  MAX_CONSOLE_ERRORS,
  PARTITION,
  TOOLBAR_HEIGHT,
} = require('./agentBrowser.cjs');

let nextId = 100;

class FakeDebugger {
  constructor(webContents) {
    this.webContents = webContents;
    this.attached = false;
    this.sent = [];
    this.messageHandlers = [];
    this.detachHandlers = [];
    this.failAttach = null;
  }

  attach() {
    if (this.failAttach) throw new Error(this.failAttach);
    if (this.attached) throw new Error('Debugger is already attached to the target');
    this.attached = true;
  }

  detach() {
    this.attached = false;
    for (const handler of this.detachHandlers) handler({}, 'target closed');
  }

  on(event, handler) {
    if (event === 'message') this.messageHandlers.push(handler);
    if (event === 'detach') this.detachHandlers.push(handler);
  }

  async sendCommand(method, params, sessionId) {
    this.sent.push(sessionId ? [method, params, sessionId] : [method, params]);
    if (this.customSend) return this.customSend(method, params, sessionId);
    if (method === 'Page.getNavigationHistory') return { entries: [{ id: 1 }, { id: 2 }], currentIndex: 1 };
    if (method === 'Page.captureScreenshot') return { data: Buffer.from('png-bytes').toString('base64') };
    return { ok: true };
  }

  emitMessage(method, params) {
    for (const handler of this.messageHandlers) handler({}, method, params, undefined);
  }
}

class FakeSession {
  constructor() {
    this.permissionHandler = null;
    this.handlers = new Map();
  }

  setPermissionRequestHandler(handler) { this.permissionHandler = handler; }

  on(event, handler) { this.handlers.set(event, handler); }
}

class FakeWebContents {
  constructor() {
    this.id = nextId++;
    this.debugger = new FakeDebugger(this);
    this.session = new FakeSession();
    this.handlers = new Map();
    this.sent = [];
    this.windowOpenHandler = null;
  }

  on(event, handler) {
    if (!this.handlers.has(event)) this.handlers.set(event, []);
    this.handlers.get(event).push(handler);
  }

  once(event, handler) { this.on(event, handler); }

  async loadURL(url) { this.loadedUrl = url; }

  send(channel, payload) { this.sent.push([channel, payload]); }

  setWindowOpenHandler(handler) { this.windowOpenHandler = handler; }

  close() { this.closed = true; }

  emit(event, ...args) {
    for (const handler of this.handlers.get(event) ?? []) handler(...args);
  }
}

class FakeWebContentsView {
  static created = [];
  static failNextAttach = false;

  constructor(options) {
    this.options = options;
    this.webContents = new FakeWebContents();
    if (FakeWebContentsView.failNextAttach) {
      this.webContents.debugger.failAttach = 'Debugger is already attached to the target';
      FakeWebContentsView.failNextAttach = false;
    }
    this.visible = true;
    this.bounds = null;
    FakeWebContentsView.created.push(this);
  }

  setBounds(bounds) { this.bounds = bounds; }

  setVisible(visible) { this.visible = visible; }
}

class FakeBrowserWindow {
  static created = [];

  constructor(options) {
    this.options = options;
    this.webContents = new FakeWebContents();
    this.handlers = new Map();
    this.children = [];
    this.destroyed = false;
    this.shown = false;
    this.contentView = {
      children: this.children,
      addChildView: (view) => { this.children.push(view); },
      removeChildView: (view) => {
        const index = this.children.indexOf(view);
        if (index >= 0) this.children.splice(index, 1);
      },
    };
    FakeBrowserWindow.created.push(this);
  }

  on(event, handler) {
    if (!this.handlers.has(event)) this.handlers.set(event, []);
    this.handlers.get(event).push(handler);
  }

  once(event, handler) { this.on(event, handler); }

  emit(event, ...args) {
    for (const handler of this.handlers.get(event) ?? []) handler(...args);
  }

  loadURL(url) { this.loadedUrl = url; return Promise.resolve(); }

  getContentSize() { return [1000, 800]; }

  showInactive() { this.shown = true; }

  show() { this.shown = true; this.focused = true; }

  focus() { this.focused = true; }

  isMinimized() { return false; }

  restore() {}

  isDestroyed() { return this.destroyed; }

  close() {
    if (this.destroyed) return;
    this.destroyed = true;
    this.emit('closed');
  }

  destroy() { this.close(); }
}

function createManager(t) {
  FakeWebContentsView.created = [];
  FakeBrowserWindow.created = [];
  FakeWebContentsView.failNextAttach = false;
  const stateDir = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-agent-browser-'));
  const manager = createAgentBrowserManager({
    BrowserWindow: FakeBrowserWindow,
    WebContentsView: FakeWebContentsView,
    shell: { openExternal: async () => {} },
    app: { getLocale: () => 'en' },
    shellUrl: () => 'app://bundle/agent-browser.html',
    preloadPath: 'preload.cjs',
    stateDir,
    stringsForLocale: () => ({ title: 'Agent Browser', back: 'Back' }),
    logger: { info() {}, warn() {}, error() {} },
    randomUUID: () => 'fixed',
  });
  t.after(() => {
    manager.closeAll();
    fs.rmSync(stateDir, { recursive: true, force: true });
  });
  return { manager, stateDir };
}

async function openSessionWithTab(t, url = 'https://example.com/a') {
  const { manager, stateDir } = createManager(t);
  const opened = await manager.openTab('s1', url);
  return { manager, stateDir, opened, tab: opened.tab, session: manager._stateFor('s1') };
}

test('opens a satellite window with one tab and navigates it', async (t) => {
  const { opened, tab, session } = await openSessionWithTab(t);
  assert.equal(session.control, 'agent');
  assert.equal(session.tabs.length, 1);
  assert.equal(session.tabs[0].active, true);
  assert.equal(tab.url, 'https://example.com/a');
  assert.equal(session.active_tab, opened.tab.id);
  assert.equal(FakeBrowserWindow.created.length, 1);
  assert.equal(FakeBrowserWindow.created[0].children.length, 1);
  assert.equal(FakeBrowserWindow.created[0].children[0].options.webPreferences.partition, PARTITION);
});

test('creates, selects and hides tabs with WebContentsView.setVisible', async (t) => {
  const { manager } = createManager(t);
  const first = await manager.openTab('s1', 'https://example.com/a');
  const second = await manager.openTab('s1', 'https://example.com/b');
  const state = manager._stateFor('s1');
  assert.equal(state.tabs.length, 2);
  assert.equal(state.active_tab, second.tab.id);
  assert.equal(FakeWebContentsView.created[0].visible, false);
  assert.equal(FakeWebContentsView.created[1].visible, true);
  const selected = manager.selectTab('s1', first.tab.id);
  assert.equal(selected.state.active_tab, first.tab.id);
  assert.equal(FakeWebContentsView.created[0].visible, true);
  assert.equal(FakeWebContentsView.created[1].visible, false);
});

test('sends CDP through the attached debugger and refuses after user takeover', async (t) => {
  const { manager } = createManager(t);
  const opened = await manager.openTab('s1', 'https://example.com/a');
  await manager.cdp('s1', opened.tab.id, 'Page.reload', {});
  assert.deepEqual(
    FakeWebContentsView.created[0].webContents.debugger.sent.map(([method]) => method),
    ['Page.enable', 'DOM.enable', 'Runtime.enable', 'Accessibility.enable', 'Page.setInterceptFileChooserDialog', 'Page.reload'],
  );
  manager.setControl('s1', 'user');
  await assert.rejects(
    () => manager.cdp('s1', opened.tab.id, 'Page.reload', {}),
    (error) => error.code === 'browser_user_in_control',
  );
});

test('records a JavaScript dialog and blocks commands until it is handled', async (t) => {
  const { manager } = createManager(t);
  const opened = await manager.openTab('s1', 'https://example.com/a');
  const view = FakeWebContentsView.created[0];
  view.webContents.debugger.emitMessage('Page.javascriptDialogOpening', {
    type: 'alert',
    message: 'hello',
    url: 'https://example.com/a',
  });
  assert.equal(manager._stateFor('s1').dialog.message, 'hello');
  await assert.rejects(
    () => manager.cdp('s1', opened.tab.id, 'Page.reload', {}),
    (error) => error.code === 'browser_dialog_open',
  );
  await manager.handleDialog('s1', true);
  assert.equal(manager._stateFor('s1').dialog, null);
  assert.equal(
    FakeWebContentsView.created[0].webContents.debugger.sent.some(([method]) => method === 'Page.handleJavaScriptDialog'),
    true,
  );
});

test('records popups and permission denials as hard-stop notices', async (t) => {
  const { manager } = createManager(t);
  await manager.openTab('s1', 'https://example.com/a');
  const view = FakeWebContentsView.created[0];
  const result = view.webContents.windowOpenHandler({ url: 'https://popup.example' });
  assert.equal(result.action, 'deny');
  assert.equal(
    manager._stateFor('s1').notices.some((entry) => entry.code === 'browser_popup_blocked'),
    true,
  );
  view.webContents.session.permissionHandler(view.webContents, 'geolocation', () => {});
  assert.equal(
    manager._stateFor('s1').notices.some((entry) => entry.code === 'browser_permission_denied'),
    true,
  );
});

test('closes the window when the last tab closes', async (t) => {
  const { manager } = createManager(t);
  const opened = await manager.openTab('s1', 'https://example.com/a');
  manager.closeTab('s1', opened.tab.id);
  const state = manager._stateFor('s1');
  assert.equal(state.tabs.length, 0);
  assert.equal(state.active_tab, null);
  assert.equal(FakeBrowserWindow.created[0].destroyed, true);
});

test('writes a screenshot to the state directory and returns its path', async (t) => {
  const { manager, stateDir } = createManager(t);
  const opened = await manager.openTab('s1', 'https://example.com/a');
  const shot = await manager.screenshot('s1', opened.tab.id, false, path.join(stateDir, 'shots'));
  assert.equal(fs.existsSync(shot.path), true);
  assert.equal(fs.statSync(shot.path).size, Buffer.from('png-bytes').length);
});

test('validates shell senders and drives takeover/handback', async (t) => {
  const { manager } = createManager(t);
  const opened = await manager.openTab('s1', 'https://example.com/a');
  const shellSender = { id: FakeBrowserWindow.created[0].webContents.id };
  assert.equal(manager.shellState({ id: 99999 }).ok, false);
  const taken = await manager.shellCommand(shellSender, { type: 'takeover' });
  assert.equal(taken.ok, true);
  assert.equal(manager._stateFor('s1').control, 'user');
  const handed = await manager.shellCommand(shellSender, { type: 'handback' });
  assert.equal(handed.ok, true);
  assert.equal(manager._stateFor('s1').control, 'agent');
  assert.equal(manager._stateFor('s1').active_tab, opened.tab.id);
});

test('maps an already-attached debugger to browser_debugger_unavailable', async (t) => {
  const { manager } = createManager(t);
  FakeWebContentsView.failNextAttach = true;
  await assert.rejects(
    () => manager.openTab('s1', 'https://example.com/a'),
    (error) => error.code === 'browser_debugger_unavailable',
  );
});

test('exposes the fixed toolbar and partition facts', () => {
  assert.equal(TOOLBAR_HEIGHT, 48);
  assert.equal(PARTITION, 'persist:tinadec-agent-browser');
});

test('isolates parallel TaskSpaces keyed by (sessionId / taskSpace) without focus theft', async (t) => {
  const { manager } = createManager(t);
  const spaceA = await manager.openTab('sess-1', 'https://example.com/a', { taskSpace: 'research' });
  const spaceB = await manager.openTab('sess-1', 'https://example.com/b', { taskSpace: 'checkout' });

  assert.equal(FakeBrowserWindow.created.length, 2);
  assert.equal(FakeBrowserWindow.created[0].focused, undefined);
  assert.equal(FakeBrowserWindow.created[1].focused, undefined);

  manager.handOff('sess-1', { taskSpace: 'research', reason: '2fa' });
  assert.equal(manager._stateFor('sess-1::research').control, 'user');
  assert.equal(manager._stateFor('sess-1::checkout').control, 'agent');

  await assert.rejects(
    () => manager.cdp('sess-1::research', spaceA.tab.id, 'Page.reload', {}),
    (err) => err.code === 'browser_user_in_control',
  );
  const okB = await manager.cdp('sess-1::checkout', spaceB.tab.id, 'Page.reload', {});
  assert.equal(okB.ok, true);

  const spaces = manager.listSpaces();
  assert.equal(spaces.length, 2);
  assert.deepEqual(spaces.map((s) => s.task_space).sort(), ['checkout', 'research']);
});

test('enforces handOff <-> takeOver and terminal finished state with keep tab cleanup', async (t) => {
  const { manager } = createManager(t);
  const tab1 = await manager.openTab('s1', 'https://example.com/1');
  const tab2 = await manager.openTab('s1', 'https://example.com/2');

  const handed = manager.handOff('s1', { reason: 'login' });
  assert.equal(handed.control, 'user');
  assert.equal(handed.handoff_reason, 'login');

  const resumed = manager.takeOver('s1');
  assert.equal(resumed.control, 'agent');
  assert.equal(resumed.handoff_reason, null);

  const finished = manager.finish('s1', { keep: [tab1.tab.id] });
  assert.equal(finished.finished, true);
  assert.equal(finished.control, 'finished');
  assert.deepEqual(finished.kept_tabs, [tab1.tab.id]);
  assert.deepEqual(finished.closed_tabs, [tab2.tab.id]);
  assert.equal(FakeBrowserWindow.created[0].destroyed, false);
  assert.equal(manager._stateFor('s1').tabs.length, 1);

  assert.throws(() => manager.takeOver('s1'), (err) => err.code === 'browser_task_space_finished');
  assert.throws(() => manager.handOff('s1'), (err) => err.code === 'browser_task_space_finished');
  await assert.rejects(
    () => manager.cdp('s1', tab1.tab.id, 'Page.reload', {}),
    (err) => err.code === 'browser_task_space_finished',
  );
  await assert.rejects(
    () => manager.openTab('s1', 'https://example.com/3'),
    (err) => err.code === 'browser_task_space_finished',
  );
});

test('finish({ keep: [] }) closes the window and retains terminal tombstone', async (t) => {
  const { manager } = createManager(t);
  await manager.openTab('s-term', 'https://example.com/1');
  const finished = manager.finish('s-term', { keep: [] });
  assert.equal(finished.kept_tabs.length, 0);
  assert.equal(finished.closed_tabs.length, 1);
  assert.equal(FakeBrowserWindow.created[0].destroyed, true);
  assert.equal(manager._stateFor('s-term').control, 'finished');
  assert.throws(() => manager.takeOver('s-term'), (err) => err.code === 'browser_task_space_finished');
});

test('allows DIALOG_SAFE_METHOD while dialog is open and aborts inflight renderer CDP', async (t) => {
  assert.equal(isDialogSafeMethod('Page.handleJavaScriptDialog'), true);
  assert.equal(isDialogSafeMethod('Target.getTargets'), true);
  assert.equal(isDialogSafeMethod('Page.setDownloadBehavior'), true);
  assert.equal(isDialogSafeMethod('Page.getFrameTree'), false);
  assert.equal(isDialogSafeMethod('DOM.getDocument'), false);
  assert.equal(isDialogSafeMethod('Accessibility.getFullAXTree'), false);

  const { manager } = createManager(t);
  const opened = await manager.openTab('s1', 'https://example.com/a');
  const view = FakeWebContentsView.created[0];

  // Simulate an inflight renderer CDP call hanging when a JS alert opens.
  view.webContents.debugger.customSend = (method) => {
    if (method === 'DOM.getDocument') return new Promise(() => {});
    return { ok: true };
  };
  const inflight = manager.cdp('s1', opened.tab.id, 'DOM.getDocument', {});
  view.webContents.debugger.emitMessage('Page.javascriptDialogOpening', {
    type: 'confirm',
    message: 'Are you sure?',
    url: 'https://example.com/a',
  });
  await assert.rejects(() => inflight, (err) => err.code === 'browser_dialog_open');

  // Target.* remains allowed while dialog is open.
  const targets = await manager.cdp('s1', opened.tab.id, 'Target.getTargets', {});
  assert.equal(targets.ok, true);

  // Page.javascriptDialogClosed clears dialog state.
  view.webContents.debugger.emitMessage('Page.javascriptDialogClosed', { result: true });
  assert.equal(manager._stateFor('s1').dialog, null);
});

test('ring-buffers console_errors from console-message, Runtime.exceptionThrown, and console.error', async (t) => {
  const { manager } = createManager(t);
  await manager.openTab('s1', 'https://example.com/a');
  const view = FakeWebContentsView.created[0];

  view.webContents.emit('console-message', {}, 3, 'Boom error', 42, 'https://example.com/app.js');
  view.webContents.debugger.emitMessage('Runtime.exceptionThrown', {
    exceptionDetails: {
      text: 'Uncaught',
      lineNumber: 12,
      url: 'https://example.com/app.js',
      exception: { description: 'TypeError: x is not a function' },
    },
  });
  for (let i = 0; i < MAX_CONSOLE_ERRORS + 10; i += 1) {
    view.webContents.debugger.emitMessage('Runtime.consoleAPICalled', {
      type: 'error',
      args: [{ value: `err-${i}` }],
    });
  }

  const state = manager._stateFor('s1');
  assert.equal(state.console_error_seq, MAX_CONSOLE_ERRORS + 12);
  assert.equal(state.console_errors.length, 20);
  assert.equal(state.console_errors[state.console_errors.length - 1].message, `err-${MAX_CONSOLE_ERRORS + 9}`);
});

test('captures live mirror preview even under user control and focuses window via openWindow', async (t) => {
  const { manager } = createManager(t);
  const events = [];
  const off = manager.onStateChange((payload) => events.push(payload));
  t.after(off);

  const opened = await manager.openTab('s1', 'https://example.com/a');
  const view = FakeWebContentsView.created[0];
  view.webContents.capturePage = async () => ({
    toDataURL: () => 'data:image/png;base64,ZmFrZQ==',
  });
  manager.handOff('s1', { reason: 'captcha' });

  const preview = await manager.capturePreview('s1');
  assert.equal(preview.control, 'user');
  assert.equal(preview.tab_id, opened.tab.id);
  assert.equal(preview.data_url, 'data:image/png;base64,ZmFrZQ==');

  manager.openWindow('s1');
  assert.equal(FakeBrowserWindow.created[0].focused, true);
  assert.ok(events.length > 0);
});

test('forwards optional cdpSessionId to debugger.sendCommand for OOPIF child targets', async (t) => {
  const { manager } = createManager(t);
  const opened = await manager.openTab('s1', 'https://example.com/a');
  await manager.cdp('s1', opened.tab.id, 'DOM.getDocument', {}, { cdpSessionId: 'child-session-42' });
  const last = FakeWebContentsView.created[0].webContents.debugger.sent.at(-1);
  assert.deepEqual(last, ['DOM.getDocument', {}, 'child-session-42']);
});
