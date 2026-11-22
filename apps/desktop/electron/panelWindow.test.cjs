const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const Module = require('node:module');

const DISPLAY = { id: 1, workArea: { x: 0, y: 0, width: 1920, height: 1040 } };

class FakePanelWindow {
  static nextId = 1;
  static instances = [];

  constructor(options) {
    this.id = FakePanelWindow.nextId++;
    this.options = options;
    this.destroyed = false;
    this.shown = false;
    this.handlers = new Map();
    this.onceHandlers = new Map();
    this.sentMessages = [];
    this.webContents = {
      id: this.id + 100,
      session: electronFake.session.defaultSession,
      send: (channel, payload) => {
        this.sentMessages.push({ channel, payload });
      },
      isDestroyed: () => this.destroyed,
      on() {},
      setWindowOpenHandler() {},
      openDevTools() {},
      getURL: () => 'app://bundle/index.html',
      mainFrame: { routingId: 1, parent: null },
    };
    FakePanelWindow.instances.push(this);
  }

  on(event, handler) {
    const list = this.handlers.get(event) ?? [];
    list.push(handler);
    this.handlers.set(event, list);
  }

  once(event, handler) {
    this.onceHandlers.set(event, handler);
  }

  emit(event, ...args) {
    const onceHandler = this.onceHandlers.get(event);
    if (onceHandler) {
      this.onceHandlers.delete(event);
      onceHandler(...args);
    }
    for (const handler of this.handlers.get(event) ?? []) {
      handler(...args);
    }
  }

  show() {
    this.shown = true;
  }

  focus() {}
  restore() {}
  isMinimized() { return false; }
  isDestroyed() { return this.destroyed; }

  getBounds() {
    return {
      x: this.options.x ?? 0,
      y: this.options.y ?? 0,
      width: this.options.width ?? 440,
      height: this.options.height ?? 640,
    };
  }

  loadURL() {
    this.emit('ready-to-show');
    return Promise.resolve();
  }

  close() {
    if (this.destroyed) return;
    this.destroyed = true;
    this.emit('closed');
  }

  static getAllWindows() {
    return FakePanelWindow.instances.filter((w) => !w.destroyed);
  }
}

const electronFake = {
  BrowserWindow: FakePanelWindow,
  screen: {
    getCursorScreenPoint: () => ({ x: 600, y: 400 }),
    getAllDisplays: () => [DISPLAY],
  },
  ipcMain: { on() {}, handle() {} },
  shell: { openExternal: async () => {} },
  session: { defaultSession: { webRequest: { onBeforeSendHeaders() {} } } },
};

const originalLoad = Module._load;
Module._load = function load(request, parent, isMain) {
  if (request === 'electron') return electronFake;
  return originalLoad.call(this, request, parent, isMain);
};

const { initializeTrustedHostRequests } = require('./trustedHostRequests.cjs');
initializeTrustedHostRequests({ token: 't'.repeat(64), isManagedHost: () => false });
const panelWindow = require('./panelWindow.cjs');

test.after(() => {
  panelWindow.closeAllPanelWindows();
  Module._load = originalLoad;
});

test('buildMainWindowOptions configures darwin native hidden chrome and traffic lights without .ico', () => {
  const darwinOpts = panelWindow.buildMainWindowOptions('darwin', {
    electronDir: '/app/electron',
    isDev: true,
  });

  assert.equal(darwinOpts.titleBarStyle, 'hidden');
  assert.deepEqual(darwinOpts.trafficLightPosition, { x: 20, y: 20 });
  assert.equal('icon' in darwinOpts, false, 'darwin main window must not pass .ico');
  assert.equal('frame' in darwinOpts, false);
  assert.equal('autoHideMenuBar' in darwinOpts, false);
  assert.equal(darwinOpts.webPreferences.contextIsolation, true);
  assert.equal(darwinOpts.webPreferences.sandbox, true);
});

test('buildMainWindowOptions keeps win32 and linux options intact', () => {
  const winOpts = panelWindow.buildMainWindowOptions('win32', {
    electronDir: path.join('C:', 'app', 'electron'),
    isDev: false,
  });

  assert.equal(winOpts.titleBarStyle, 'hidden');
  assert.equal('trafficLightPosition' in winOpts, false);
  assert.equal(winOpts.autoHideMenuBar, true);
  assert.ok(winOpts.icon.endsWith('tinadec.ico'));
});

test('buildPanelWindowOptions replaces frame:false with hidden titlebar and traffic lights on darwin', () => {
  const darwinPanel = panelWindow.buildPanelWindowOptions({
    width: 480,
    height: 680,
    x: 120,
    y: 90,
    title: 'Git',
    platform: 'darwin',
    electronDir: '/app/electron',
    isDev: true,
  });

  assert.equal(darwinPanel.titleBarStyle, 'hidden');
  assert.deepEqual(darwinPanel.trafficLightPosition, { x: 14, y: 12 });
  assert.equal('frame' in darwinPanel, false, 'darwin panel must omit frame:false so traffic lights render');
  assert.equal('icon' in darwinPanel, false, 'darwin panel must not pass .ico');
  assert.equal(darwinPanel.width, 480);
  assert.equal(darwinPanel.height, 680);
  assert.equal(darwinPanel.title, 'Git');
});

test('buildPanelWindowOptions preserves frameless win32/linux options unchanged', () => {
  const winPanel = panelWindow.buildPanelWindowOptions({
    width: 440,
    height: 640,
    x: 100,
    y: 80,
    title: 'Terminal',
    platform: 'win32',
    electronDir: path.join('C:', 'app', 'electron'),
    isDev: true,
  });

  assert.equal(winPanel.frame, false);
  assert.equal(winPanel.autoHideMenuBar, true);
  assert.equal('titleBarStyle' in winPanel, false);
  assert.equal('trafficLightPosition' in winPanel, false);
  assert.ok(winPanel.icon.endsWith('tinadec.ico'));
});

test('createPanelWindow emits tinadec:fullscreen-changed on enter and leave full-screen', async () => {
  const { windowId } = await panelWindow.createPanelWindow('tab-fs', 'git', 'Git Panel', {}, {
    skipNotify: true,
    skipPersist: true,
  });
  const created = FakePanelWindow.instances.find((w) => w.id === windowId);
  assert.ok(created, 'panel window instance created');

  created.emit('enter-full-screen');
  created.emit('leave-full-screen');

  assert.deepEqual(created.sentMessages, [
    { channel: 'tinadec:fullscreen-changed', payload: true },
    { channel: 'tinadec:fullscreen-changed', payload: false },
  ]);

  created.close();
  // Emitting after close is a safe no-op.
  created.emit('enter-full-screen');
  assert.equal(created.sentMessages.length, 2);
});

test('buildDarwinApplicationMenuTemplate includes standard app, edit, view, and window roles', () => {
  const menu = panelWindow.buildDarwinApplicationMenuTemplate({ appName: 'TinadecOffice', isDev: false });
  assert.deepEqual(
    menu.map((section) => section.role),
    ['appMenu', 'editMenu', 'viewMenu', 'windowMenu'],
  );

  const editRoles = menu[1].submenu.filter((item) => item.role).map((item) => item.role);
  for (const role of ['undo', 'redo', 'cut', 'copy', 'paste', 'selectAll']) {
    assert.ok(editRoles.includes(role), `editMenu includes ${role}`);
  }

  const viewRoles = menu[2].submenu.filter((item) => item.role).map((item) => item.role);
  assert.ok(viewRoles.includes('togglefullscreen'), 'viewMenu includes togglefullscreen');
});

test('defaultPanelDimensions and createPanelWindow compute comfortable type-aware dimensions and honor saved bounds', async () => {
  for (const wideType of ['git', 'codeEditor', 'browser', 'orchestration']) {
    assert.deepEqual(panelWindow.defaultPanelDimensions(wideType), { width: 760, height: 680 });
  }
  for (const mediumType of ['approval', 'events', 'doctor', 'organization']) {
    assert.deepEqual(panelWindow.defaultPanelDimensions(mediumType), { width: 560, height: 640 });
  }
  assert.deepEqual(panelWindow.defaultPanelDimensions('terminal'), { width: 600, height: 640 });

  const { windowId: gitWinId } = await panelWindow.createPanelWindow('tab-git', 'git', 'Git', {}, {
    skipNotify: true,
    skipPersist: true,
  });
  const gitWin = FakePanelWindow.instances.find((w) => w.id === gitWinId);
  assert.deepEqual({ width: gitWin.options.width, height: gitWin.options.height }, { width: 760, height: 680 });
  gitWin.close();

  const { windowId: approvalWinId } = await panelWindow.createPanelWindow('tab-app', 'approval', 'Approvals', {}, {
    skipNotify: true,
    skipPersist: true,
  });
  const approvalWin = FakePanelWindow.instances.find((w) => w.id === approvalWinId);
  assert.deepEqual({ width: approvalWin.options.width, height: approvalWin.options.height }, { width: 560, height: 640 });
  approvalWin.close();

  // Saved user bounds take precedence over type defaults (clamped within workArea).
  const { windowId: customWinId } = await panelWindow.createPanelWindow('tab-custom', 'git', 'Git Custom', {}, {
    width: 880,
    height: 740,
    x: 150,
    y: 110,
    skipNotify: true,
    skipPersist: true,
  });
  const customWin = FakePanelWindow.instances.find((w) => w.id === customWinId);
  assert.deepEqual(
    { width: customWin.options.width, height: customWin.options.height, x: customWin.options.x, y: customWin.options.y },
    { width: 880, height: 740, x: 150, y: 110 },
  );
  customWin.close();
});
