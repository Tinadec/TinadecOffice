// companionWindow.cjs owns the window's size, and the rule that keeps being questioned is:
// switching to `immersive` must NOT grow the window. That rule lives in this module, not in the
// page, so it is tested here against a BrowserWindow double instead of through the renderer.
const test = require('node:test');
const assert = require('node:assert/strict');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const Module = require('node:module');

const WORK_AREA = { x: 0, y: 0, width: 1920, height: 1040 };
const DISPLAY = { id: 1, workArea: WORK_AREA };

/** Every size the module asks for, in order. */
const resizes = [];

class FakeWindow {
  constructor(options) {
    this.bounds = {
      x: options.x ?? 0,
      y: options.y ?? 0,
      width: options.width ?? 0,
      height: options.height ?? 0,
    };
    this.destroyed = false;
    this.handlers = new Map();
    this.webContents = {
      id: 7,
      send() {},
      isDestroyed: () => false,
      on() {},
      setWindowOpenHandler() {},
      getURL: () => 'app://index.html',
    };
    FakeWindow.last = this;
  }

  setAlwaysOnTop() {}

  setVisibleOnAllWorkspaces() {}

  on(event, handler) {
    this.handlers.set(event, handler);
  }

  once() {}

  showInactive() {}

  loadURL() {
    return Promise.resolve();
  }

  isDestroyed() {
    return this.destroyed;
  }

  isResizable() {
    return false;
  }

  getBounds() {
    return { ...this.bounds };
  }

  setBounds(next) {
    this.bounds = { x: next.x, y: next.y, width: next.width, height: next.height };
    resizes.push({ ...this.bounds });
  }

  close() {
    if (this.destroyed) return;
    this.destroyed = true;
    this.handlers.get('closed')?.();
  }

  static getFocusedWindow() {
    return null;
  }
}

const electronFake = {
  BrowserWindow: FakeWindow,
  screen: {
    getAllDisplays: () => [DISPLAY],
    getPrimaryDisplay: () => DISPLAY,
    on() {},
  },
  shell: { openExternal: async () => {} },
};

const originalLoad = Module._load;
Module._load = function load(request, parent, isMain) {
  if (request === 'electron') return electronFake;
  return originalLoad.call(this, request, parent, isMain);
};

const userData = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-companion-'));
require('./companionLayoutStore.cjs').__setUserDataDir(userData);
const companion = require('./companionWindow.cjs');
const geometry = require('./companionGeometry.cjs');

/** A window in a known state: freshly created, therefore collapsed, at the default style. */
async function freshWindow() {
  companion.closeCompanionWindow();
  // The module keeps the style and reveal between windows, so reset them instead of inheriting
  // whatever the previous test left behind.
  companion.setCapsuleStyle(geometry.DEFAULT_CAPSULE_STYLE);
  companion.setCapsuleReveal(geometry.DEFAULT_CAPSULE_REVEAL);
  await companion.createCompanionWindow();
  return FakeWindow.last;
}

test.after(() => {
  companion.closeCompanionWindow();
  Module._load = originalLoad;
  fs.rmSync(userData, { recursive: true, force: true });
});

test('the capsule is the only shape with its own size', async () => {
  const win = await freshWindow();
  assert.deepEqual(
    { width: win.bounds.width, height: win.bounds.height },
    { width: geometry.PEEK_WIDTH, height: geometry.CAPSULE_HEIGHT },
  );

  companion.setMode('panel');
  assert.deepEqual(
    { width: win.bounds.width, height: win.bounds.height },
    { width: geometry.PANEL.width, height: geometry.PANEL.height },
  );
});

test('immersive is the panel footprint, never a bigger window', async () => {
  const win = await freshWindow();

  companion.setMode('panel');
  const panel = { ...win.bounds };
  const before = resizes.length;

  companion.setMode('immersive');

  assert.equal(resizes.length, before + 1, 'the switch re-applies bounds exactly once');
  assert.deepEqual(win.bounds, panel, 'opening the call screen must not resize the window');
  // Nor does it move it: the vertical anchor survives, so the panel does not jump.
  assert.equal(win.bounds.y, panel.y);

  // Going back is the same rect again, and collapsing still returns to the capsule.
  companion.setMode('panel');
  assert.deepEqual(win.bounds, panel);
  companion.setMode('capsule');
  assert.equal(win.bounds.width, geometry.PEEK_WIDTH);
  assert.equal(win.bounds.height, geometry.CAPSULE_HEIGHT);
});

test('the style decides the collapsed width, and the reveal only moves it', async () => {
  const win = await freshWindow();
  assert.equal(win.bounds.width, geometry.PEEK_WIDTH);
  // The peek hangs past the edge: only the stored reveal percentage of its width is on screen.
  const peekHidden = Math.round(geometry.PEEK_WIDTH * (1 - geometry.DEFAULT_CAPSULE_REVEAL / 100));
  assert.equal(win.bounds.x, WORK_AREA.width - geometry.PEEK_WIDTH + peekHidden);

  // A different reveal keeps the same window and only parks it further out.
  companion.setCapsuleReveal(10);
  assert.equal(win.bounds.width, geometry.PEEK_WIDTH);
  assert.equal(win.bounds.x, WORK_AREA.width - Math.round(geometry.PEEK_WIDTH * 0.1));
  companion.setCapsuleReveal(100);
  assert.equal(win.bounds.x, WORK_AREA.width - geometry.PEEK_WIDTH);

  // The grip only has to hold a handle, and it sits entirely on screen whatever the reveal says.
  companion.setCapsuleStyle('grip');
  assert.equal(win.bounds.width, geometry.GRIP_WIDTH);
  assert.equal(win.bounds.height, geometry.CAPSULE_HEIGHT);
  assert.equal(win.bounds.x, WORK_AREA.width - geometry.GRIP_WIDTH);

  // Switching back restores both the width and the stored reveal.
  companion.setCapsuleStyle('peek');
  assert.equal(win.bounds.width, geometry.PEEK_WIDTH);
  assert.equal(win.bounds.x, WORK_AREA.width - geometry.PEEK_WIDTH);
  assert.equal(companion.snapshot().capsuleStyle, 'peek');
  assert.equal(companion.snapshot().capsuleReveal, 100);
});

test('a mode the geometry does not know falls back to the capsule', async () => {
  const win = await freshWindow();

  companion.setMode('panel');
  companion.setMode('fullscreen');
  assert.equal(win.bounds.width, geometry.PEEK_WIDTH);
  assert.equal(win.bounds.height, geometry.CAPSULE_HEIGHT);
});
