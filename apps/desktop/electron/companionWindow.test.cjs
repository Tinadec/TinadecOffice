// companionWindow.cjs owns window options, pure 2-state sizing, free 2D multi-display drag,
// magnetic edge snapping, and persistence. Tested against a BrowserWindow + screen double.
const test = require('node:test');
const assert = require('node:assert/strict');
const os = require('node:os');
const path = require('node:path');
const fs = require('node:fs');
const Module = require('node:module');

const PRIMARY_AREA = { x: 0, y: 0, width: 1920, height: 1040 };
const SECONDARY_AREA = { x: 1920, y: 0, width: 1600, height: 900 };
const PRIMARY_DISPLAY = { id: 1, workArea: PRIMARY_AREA };
const SECONDARY_DISPLAY = { id: 2, workArea: SECONDARY_AREA };

let activeDisplays = [PRIMARY_DISPLAY];
const resizes = [];
const sentEvents = [];

class FakeWindow {
  constructor(options) {
    this.options = options;
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
      send(channel, payload) {
        sentEvents.push({ channel, payload });
      },
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
    getAllDisplays: () => activeDisplays,
    getPrimaryDisplay: () => activeDisplays[0],
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
const layoutStore = require('./companionLayoutStore.cjs');
layoutStore.__setUserDataDir(userData);
const companion = require('./companionWindow.cjs');
const geometry = require('./companionGeometry.cjs');

async function freshWindow(displays = [PRIMARY_DISPLAY]) {
  companion.closeCompanionWindow();
  await companion.flushPersistence();
  activeDisplays = displays;
  resizes.length = 0;
  sentEvents.length = 0;
  try { fs.rmSync(layoutStore.layoutPath(), { force: true }); } catch { /* ignore */ }
  await companion.createCompanionWindow();
  return FakeWindow.last;
}

test.after(async () => {
  companion.closeCompanionWindow();
  await companion.flushPersistence();
  Module._load = originalLoad;
  fs.rmSync(userData, { recursive: true, force: true });
});

test('remains a frameless floating widget on ALL platforms (win32, darwin, linux)', () => {
  const sampleBounds = { x: 1844, y: 482, width: 76, height: 76 };
  for (const platform of ['win32', 'darwin', 'linux']) {
    const opts = companion.buildCompanionWindowOptions(sampleBounds, platform);
    assert.equal(opts.frame, false, `${platform} must keep frame: false`);
    assert.equal(opts.titleBarStyle, undefined, `${platform} must never enable titleBarStyle`);
    assert.equal(opts.trafficLightPosition, undefined, `${platform} must never configure traffic lights`);
    assert.equal(opts.alwaysOnTop, true);
    assert.equal(opts.skipTaskbar, true);
    assert.equal(opts.resizable, false);
  }
});

test('enforces pure 2-state footprints (capsule 76x76 <-> panel 348x540) and coerces legacy immersive to panel', async () => {
  const win = await freshWindow();
  assert.deepEqual(
    { width: win.bounds.width, height: win.bounds.height },
    { width: geometry.CAPSULE.width, height: geometry.CAPSULE.height },
  );
  assert.equal(companion.snapshot().mode, 'capsule');
  assert.equal(companion.snapshot().expanded, false);

  companion.setMode('immersive');
  assert.equal(companion.snapshot().mode, 'panel');
  assert.equal(companion.snapshot().expanded, true);
  assert.deepEqual(
    { width: win.bounds.width, height: win.bounds.height },
    { width: geometry.PANEL.width, height: geometry.PANEL.height },
  );

  companion.setMode('fullscreen');
  assert.equal(companion.snapshot().mode, 'capsule');
  assert.deepEqual(
    { width: win.bounds.width, height: win.bounds.height },
    { width: geometry.CAPSULE.width, height: geometry.CAPSULE.height },
  );
});

test('supports free 2D multi-display dragMove and magnetic edge snap + persistence on dragEnd', async () => {
  const win = await freshWindow([PRIMARY_DISPLAY, SECONDARY_DISPLAY]);

  // Drag freely in 2D across the primary display
  companion.dragMove(620, 310);
  assert.deepEqual(win.bounds, {
    x: 620 - 38,
    y: 310 - 38,
    width: 76,
    height: 76,
  });

  // Drag onto the secondary monitor and release near its right edge
  companion.dragMove(3200, 420);
  assert.deepEqual(win.bounds, {
    x: 3200 - 38,
    y: 420 - 38,
    width: 76,
    height: 76,
  });

  const snapped = companion.dragEnd(3300, 420);
  assert.equal(snapped.displayId, 2);
  assert.equal(snapped.side, 'right');
  assert.equal(snapped.centerY, 420);
  assert.deepEqual(win.bounds, {
    x: SECONDARY_AREA.x + SECONDARY_AREA.width - 76,
    y: 420 - 38,
    width: 76,
    height: 76,
  });

  // Broadcasted state matches snapped anchor
  const lastBroadcast = sentEvents.at(-1);
  assert.equal(lastBroadcast.channel, 'tinadec:companion-state-changed');
  assert.equal(lastBroadcast.payload.displayId, 2);
  assert.equal(lastBroadcast.payload.side, 'right');

  // Persisted to companion-layout.json
  await companion.flushPersistence();
  const stored = await layoutStore.load();
  assert.deepEqual(stored, { side: 'right', centerY: 420, displayId: 2 });

  // Drag panel onto left half of primary display -> snaps to left edge of primary
  companion.expandCompanionWindow();
  assert.equal(win.bounds.width, geometry.PANEL.width);
  companion.dragMove(300, 500);
  assert.equal(win.bounds.x, 300 - Math.round(geometry.PANEL.width / 2));
  const leftSnapped = companion.dragEnd(250, 500);
  assert.equal(leftSnapped.displayId, 1);
  assert.equal(leftSnapped.side, 'left');
  assert.equal(win.bounds.x, 0);
  assert.equal(win.bounds.width, geometry.PANEL.width);
  // Wait deterministically for the serialized atomic write + rename to settle.
  await companion.flushPersistence();
  assert.deepEqual(await layoutStore.load(), { side: 'left', centerY: 500, displayId: 1 });
});

test('keeps legacy setCapsuleStyle / setCapsuleReveal / moveTo / setSide exports working safely', async () => {
  const win = await freshWindow();
  const initialBounds = { ...win.bounds };

  assert.equal(companion.setCapsuleStyle('grip').capsuleStyle, 'grip');
  assert.equal(companion.setCapsuleReveal(10).capsuleReveal, 10);
  // Capsule footprint stays at the unified 76x76 floating size.
  assert.deepEqual(win.bounds, initialBounds);

  companion.setSide('left');
  assert.equal(companion.snapshot().side, 'left');
  assert.equal(win.bounds.x, 0);

  companion.moveTo(200);
  assert.equal(win.bounds.y, 200);
});
