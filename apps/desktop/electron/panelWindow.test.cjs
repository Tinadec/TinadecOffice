const assert = require('node:assert/strict');
const fs = require('node:fs');
const Module = require('node:module');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

class FakeWebContents {
  static nextId = 100;

  constructor(owner) {
    this.owner = owner;
    this.id = FakeWebContents.nextId++;
    this.sent = [];
    this.handlers = new Map();
  }

  setWindowOpenHandler(handler) { this.windowOpenHandler = handler; }
  openDevTools() {}
  on(event, handler) {
    const handlers = this.handlers.get(event) || [];
    handlers.push(handler);
    this.handlers.set(event, handlers);
  }
  send(channel, data) { this.sent.push({ channel, data }); }
}

class FakeBrowserWindow {
  static nextId = 1;
  static windows = [];

  static getAllWindows() { return this.windows.filter((window) => !window.isDestroyed()); }
  static fromWebContents(contents) { return contents?.owner ?? null; }

  constructor(options = {}) {
    this.id = FakeBrowserWindow.nextId++;
    this.options = options;
    this.bounds = {
      x: options.x ?? 0,
      y: options.y ?? 0,
      width: options.width ?? 800,
      height: options.height ?? 600,
    };
    this.destroyed = false;
    this.handlers = new Map();
    this.webContents = new FakeWebContents(this);
    FakeBrowserWindow.windows.push(this);
  }

  on(event, handler) { this.addHandler(event, handler, false); }
  once(event, handler) { this.addHandler(event, handler, true); }
  addHandler(event, handler, once) {
    const handlers = this.handlers.get(event) || [];
    handlers.push({ handler, once });
    this.handlers.set(event, handlers);
  }
  emit(event, ...args) {
    const handlers = this.handlers.get(event) || [];
    this.handlers.set(event, handlers.filter((entry) => !entry.once));
    for (const entry of handlers) entry.handler(...args);
  }
  async loadURL(url) {
    this.loadedUrl = url;
    this.emit('ready-to-show');
  }
  async loadFile(file, options) {
    this.loadedFile = { file, options };
    this.emit('ready-to-show');
  }
  close() {
    if (this.destroyed) return;
    this.destroyed = true;
    this.emit('closed');
  }
  getBounds() { return { ...this.bounds }; }
  isDestroyed() { return this.destroyed; }
  isMinimized() { return false; }
  restore() {}
  focus() { this.focused = true; }
  show() { this.visible = true; }
}

const originalDevServer = process.env.VITE_DEV_SERVER_URL;
process.env.VITE_DEV_SERVER_URL = 'http://127.0.0.1:5173/';
const originalLoad = Module._load;
Module._load = function load(request, parent, isMain) {
  if (request === 'electron') {
    return {
      BrowserWindow: FakeBrowserWindow,
      screen: {
        getCursorScreenPoint: () => ({ x: 600, y: 400 }),
        getAllDisplays: () => [{ workArea: { x: 0, y: 0, width: 1920, height: 1080 } }],
      },
    };
  }
  return originalLoad.call(this, request, parent, isMain);
};
const panelWindow = require('./panelWindow.cjs');
Module._load = originalLoad;
if (originalDevServer === undefined) delete process.env.VITE_DEV_SERVER_URL;
else process.env.VITE_DEV_SERVER_URL = originalDevServer;

function payload(state = { url: 'https://example.com' }) {
  return {
    version: 1,
    card: { id: 'home:browser:1', type: 'tool.browser', state },
    pageId: 'home',
    title: 'Browser',
    returnPlacement: { slotId: 'center', stackId: 'secondary', index: 2 },
  };
}

test('validates detached payloads and keeps serialized state out of the URL', () => {
  assert.equal(panelWindow.normalizeWorkbenchPayload({}), null);
  assert.deepEqual(panelWindow.normalizeWorkbenchPayload(payload()), payload());
  assert.equal(panelWindow.buildPanelHashPath(17), '/panel?windowId=17');
  assert.throws(() => panelWindow.buildPanelHashPath(0), /Invalid/);

  const oversized = panelWindow.normalizeWorkbenchPayload(payload({ value: 'x'.repeat(panelWindow.MAX_STATE_BYTES + 1) }));
  assert.deepEqual(oversized.card.state, {});
  const hash = panelWindow.buildPanelHashPath(17);
  assert.equal(hash.includes('state='), false);
  assert.equal(hash.includes('tool.browser'), false);
});

test('migrates legacy detached panels to versioned card records', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-detached-migrate-'));
  const stateFile = path.join(root, 'userData', 'detached-cards.json');
  const legacyFile = path.join(root, '.tinadec-panel-layout.json');
  try {
    fs.writeFileSync(legacyFile, JSON.stringify({
      panels: [{ tabId: 'legacy-preview', type: 'preview', title: 'Preview', state: { url: 'https://legacy.example' }, bounds: { x: 12, y: 18, width: 500, height: 620 } }],
    }), 'utf8');
    panelWindow.configurePanelWindowStore(stateFile, { legacyFilePath: legacyFile });
    const migrated = panelWindow.loadPersistedLayout();
    assert.equal(migrated.length, 1);
    assert.equal(migrated[0].workbench.card.id, 'legacy-preview');
    assert.equal(migrated[0].workbench.card.type, 'tool.browser');
    assert.deepEqual(migrated[0].workbench.card.state, { url: 'https://legacy.example' });
    assert.equal(JSON.parse(fs.readFileSync(stateFile, 'utf8')).version, 1);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('restores, scopes state IPC to the owning renderer, and reattaches the same card', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-detached-restore-'));
  const stateFile = path.join(root, 'userData', 'detached-cards.json');
  const legacyFile = path.join(root, 'legacy.json');
  try {
    panelWindow.configurePanelWindowStore(stateFile, { legacyFilePath: legacyFile });
    panelWindow.savePersistedLayout([{ workbench: payload(), bounds: { x: 40, y: 50, width: 640, height: 700 } }]);
    assert.equal(panelWindow.loadPersistedLayout()[0].workbench.card.id, 'home:browser:1');

    const main = new FakeBrowserWindow({ width: 1200, height: 800 });
    panelWindow.tagMainWindow(main);
    await panelWindow.restorePersistedPanels();
    const detached = FakeBrowserWindow.windows.find((window) => window !== main && !window.isDestroyed());
    assert.ok(detached);
    assert.match(detached.loadedUrl, /#\/panel\?windowId=\d+$/);
    assert.equal(detached.loadedUrl.includes('state='), false);
    assert.equal(detached.loadedUrl.includes('tool.browser'), false);

    assert.equal(panelWindow.getPanelWindowState(main.webContents), null);
    assert.equal(panelWindow.updatePanelWindowState(main.webContents, { url: 'https://blocked.example' }), false);
    assert.equal(panelWindow.getPanelWindowState(detached.webContents).workbench.card.id, 'home:browser:1');
    assert.equal(panelWindow.updatePanelWindowState(detached.webContents, { url: 'https://updated.example' }), true);
    assert.equal(panelWindow.loadPersistedLayout()[0].workbench.card.state.url, 'https://updated.example');

    assert.equal(panelWindow.reattachPanelWindow(detached.id, '', '', '', { url: 'https://reattached.example' }), true);
    const message = main.webContents.sent.find((entry) => entry.channel === 'panel:reattach');
    assert.ok(message);
    assert.equal(message.data.workbench.card.id, 'home:browser:1');
    assert.equal(message.data.workbench.pageId, 'home');
    assert.deepEqual(message.data.workbench.returnPlacement, { slotId: 'center', stackId: 'secondary', index: 2 });
    assert.deepEqual(message.data.state, { url: 'https://reattached.example' });
    assert.equal(detached.isDestroyed(), true);
    assert.deepEqual(panelWindow.collectPanelStates(), []);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
