// companionWindow.cjs — the floating companion widget: one frameless window, two states.
//
// Collapsed, it is a sleek 76x76 floating avatar capsule docked magnetically to the left or right
// edge of whichever display the user placed it on. Expanded, the same window opens inward into a
// 348x540 frosted-glass island panel. Both shapes support free 2D multi-display dragging and snap
// onto the nearest display edge on release.
//
// Authority split: the renderer owns the visuals and pointer gestures; this module owns screen
// geometry, multi-display magnetic snapping, persistence, and blur auto-collapse. The window is
// strictly frameless (`frame: false`) on ALL platforms (win32, darwin, linux).

const { BrowserWindow, screen } = require('electron');
const path = require('node:path');
const { appBundleUrl } = require('./appBundle.cjs');
const { attachExternalLinkGuards } = require('./externalLinks.cjs');
const geometry = require('./companionGeometry.cjs');
const layoutStore = require('./companionLayoutStore.cjs');

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);
const noTransparency = process.env.TINADEC_DISABLE_TRANSPARENCY === '1';
// A blur fires before the OS has handed focus to its new owner, so the collapse check waits a
// beat: clicking a TinadecOffice window blurs the panel first, and that window must win the race.
const BLUR_SETTLE_MS = 120;
const PERSIST_DELAY_MS = 200;

let win = null;
let side = 'right';
let centerY = null;
let displayId = null;
/** Pure 2-state footprint: `'capsule'` | `'panel'`. Legacy `'immersive'` coerces to `'panel'`. */
let mode = 'capsule';
/** Retained safely for `main.cjs` / `appConfig` IPC compatibility. */
let capsuleStyle = geometry.DEFAULT_CAPSULE_STYLE;
let capsuleReveal = geometry.DEFAULT_CAPSULE_REVEAL;
let isDragging = false;
let saveTimer = null;
let blurTimer = null;
let pendingPersist = Promise.resolve();
let displayListenersAttached = false;

function companionWindowUrl() {
  const hash = '/companion';
  if (isDev) return `${process.env.VITE_DEV_SERVER_URL}?splash=0#${hash}`;
  return appBundleUrl({ hash, query: { splash: '0' } });
}

function buildCompanionWindowOptions(bounds, platform = process.platform) {
  const options = {
    ...bounds,
    title: 'Tinadec Companion',
    // Strictly frameless floating widget on ALL OSes — never enable titleBarStyle or traffic lights.
    frame: false,
    transparent: !noTransparency,
    backgroundColor: noTransparency ? '#1e1e2e' : '#00000000',
    hasShadow: false,
    thickFrame: false,
    resizable: false,
    maximizable: false,
    minimizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    alwaysOnTop: true,
    show: false,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  };
  if (platform !== 'darwin') {
    options.icon = path.join(__dirname, '..', isDev ? 'public' : 'dist', 'tinadec.ico');
  }
  return options;
}

function displayFor() {
  if (Number.isInteger(displayId)) {
    const stored = screen.getAllDisplays().find((entry) => entry.id === displayId);
    if (stored) return stored;
  }
  return screen.getPrimaryDisplay();
}

function currentBounds() {
  const area = displayFor().workArea;
  const anchor = centerY ?? area.height / 2;
  if (mode === 'capsule') {
    return geometry.collapsedBounds(area, side, anchor);
  }
  return geometry.panelBounds(area, side, anchor);
}

// `expanded` is kept as a derived field alongside `mode` for renderer and test compatibility.
function snapshot() {
  return {
    open: Boolean(win && !win.isDestroyed()),
    mode,
    expanded: mode !== 'capsule',
    side,
    displayId,
    centerY,
    capsuleStyle,
    capsuleReveal,
  };
}

function broadcastState() {
  if (win && !win.isDestroyed() && !win.webContents.isDestroyed()) {
    win.webContents.send('tinadec:companion-state-changed', snapshot());
  }
}

function queuePersist(fn) {
  const run = () => Promise.resolve().then(fn);
  pendingPersist = pendingPersist.then(run, run);
  return pendingPersist;
}

/** Wait for all queued layout persistence writes/renames to settle cleanly. */
function flushPersistence() {
  return pendingPersist;
}

/** Persist the current `{ displayId, side, centerY }` anchor immediately (serialized). */
function saveAnchor(anchor = { side, centerY, displayId: displayFor().id }) {
  clearTimeout(saveTimer);
  saveTimer = null;
  const payload = {
    side: geometry.normalizeSide(anchor.side),
    centerY: Number.isFinite(anchor.centerY) ? anchor.centerY : null,
    displayId: Number.isInteger(anchor.displayId) ? anchor.displayId : displayFor().id,
  };
  return queuePersist(() =>
    layoutStore.save(payload).catch((error) => {
      console.warn('[companion] failed to persist layout:', error.message);
    }),
  );
}

function schedulePersist() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => {
    saveTimer = null;
    const current = win;
    if (!current || current.isDestroyed()) return;
    void saveAnchor();
  }, PERSIST_DELAY_MS);
}

function applyBounds() {
  if (win && !win.isDestroyed()) win.setBounds(currentBounds());
}

function inWorkArea() {
  return displayFor().workArea;
}

function currentShapeHeight(area) {
  return mode === 'capsule' ? geometry.CAPSULE_HEIGHT : geometry.panelSize(area).height;
}

/**
 * Legacy vertical-only move fallback: places window top at `top` in screen coordinates.
 */
function moveTo(top) {
  if (!Number.isFinite(top)) return snapshot();
  const area = inWorkArea();
  const height = currentShapeHeight(area);
  centerY = geometry.clampCenter(geometry.centerFromWindowTop(top, area, height), area, height);
  applyBounds();
  schedulePersist();
  return snapshot();
}

/**
 * Free 2D X+Y drag across all displays for either `'capsule'` or `'panel'`.
 */
function dragMove(screenX, screenY) {
  if (!win || win.isDestroyed() || !Number.isFinite(screenX) || !Number.isFinite(screenY)) {
    return snapshot();
  }
  isDragging = true;
  clearTimeout(blurTimer);
  blurTimer = null;
  const displays = screen.getAllDisplays();
  const nextBounds = geometry.dragBounds(screenX, screenY, displays, mode);
  if (nextBounds) win.setBounds(nextBounds);
  return snapshot();
}

/**
 * Finish a 2D drag: magnetically snap to the nearest left/right edge on the containing/nearest
 * display, persist `{ displayId, side, centerY }` via `saveAnchor`, and broadcast state.
 */
function dragEnd(screenX, screenY) {
  isDragging = false;
  if (!win || win.isDestroyed()) return snapshot();
  const displays = screen.getAllDisplays();
  let targetX = screenX;
  let targetY = screenY;
  if (!Number.isFinite(targetX) || !Number.isFinite(targetY)) {
    const current = win.getBounds();
    targetX = current.x + current.width / 2;
    targetY = current.y + current.height / 2;
  }
  const snapped = geometry.snapToNearestEdge(targetX, targetY, displays);
  displayId = snapped.displayId;
  side = snapped.side;
  centerY = snapped.centerY;
  applyBounds();
  void saveAnchor({ displayId, side, centerY });
  broadcastState();
  return snapshot();
}

/**
 * Compatibility no-op setter kept for `main.cjs` / `appConfig` IPC channels.
 * The redesigned floating capsule uses a unified 76x76 floating avatar footprint.
 */
function setCapsuleStyle(next) {
  const normalized = geometry.normalizeCapsuleStyle(next);
  if (normalized === capsuleStyle) return snapshot();
  capsuleStyle = normalized;
  broadcastState();
  return snapshot();
}

/**
 * Compatibility no-op setter kept for `main.cjs` / `appConfig` IPC channels.
 */
function setCapsuleReveal(next) {
  const normalized = geometry.normalizeCapsuleReveal(next);
  if (normalized === capsuleReveal) return snapshot();
  capsuleReveal = normalized;
  broadcastState();
  return snapshot();
}

/**
 * Switch strictly between `'capsule'` and `'panel'`. Legacy `'immersive'` coerces to `'panel'`.
 * The vertical anchor survives every transition.
 */
function setMode(next) {
  const normalized = geometry.normalizeMode(next);
  if (normalized === mode) return snapshot();
  mode = normalized;
  clearTimeout(blurTimer);
  blurTimer = null;
  applyBounds();
  broadcastState();
  return snapshot();
}

function setSide(nextSide) {
  const normalized = geometry.normalizeSide(nextSide);
  if (normalized === side) return snapshot();
  side = normalized;
  applyBounds();
  schedulePersist();
  broadcastState();
  return snapshot();
}

function setExpanded(next) {
  return setMode(next ? 'panel' : 'capsule');
}

/**
 * Collapse an open panel once focus has left every TinadecOffice window (unless dragging).
 */
function scheduleCollapseCheck() {
  clearTimeout(blurTimer);
  blurTimer = setTimeout(() => {
    blurTimer = null;
    if (isDragging || mode === 'capsule' || !win || win.isDestroyed()) return;
    if (BrowserWindow.getFocusedWindow()) return;
    setExpanded(false);
  }, BLUR_SETTLE_MS);
}

function handleDisplaysChanged() {
  if (!win || win.isDestroyed()) return;
  const known = screen.getAllDisplays().some((entry) => entry.id === displayId);
  if (!known) displayId = screen.getPrimaryDisplay().id;
  applyBounds();
  schedulePersist();
}

function attachDisplayListeners() {
  if (displayListenersAttached) return;
  displayListenersAttached = true;
  screen.on('display-added', handleDisplaysChanged);
  screen.on('display-removed', handleDisplaysChanged);
  screen.on('display-metrics-changed', handleDisplaysChanged);
}

/** Create the capsule (collapsed). Reuses the existing window when one is already open. */
async function createCompanionWindow() {
  if (win && !win.isDestroyed()) {
    win.showInactive();
    return snapshot();
  }
  const stored = await layoutStore.load();
  side = geometry.normalizeSide(stored?.side);
  centerY = Number.isFinite(stored?.centerY) ? stored.centerY : null;
  displayId = Number.isInteger(stored?.displayId) ? stored.displayId : null;
  mode = 'capsule';
  isDragging = false;
  attachDisplayListeners();

  const created = new BrowserWindow(buildCompanionWindowOptions(currentBounds(), process.platform));
  win = created;
  created.setAlwaysOnTop(true, process.platform === 'darwin' ? 'floating' : 'normal');
  if (process.platform === 'darwin') created.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  attachExternalLinkGuards(created.webContents);
  created.on('blur', scheduleCollapseCheck);
  created.on('closed', () => {
    clearTimeout(blurTimer);
    clearTimeout(saveTimer);
    blurTimer = null;
    saveTimer = null;
    isDragging = false;
    if (win === created) win = null;
  });
  created.once('ready-to-show', () => {
    // The capsule must not steal focus from whatever the user was doing at launch.
    if (!created.isDestroyed()) created.showInactive();
  });

  await created.loadURL(companionWindowUrl());
  return snapshot();
}

function closeCompanionWindow() {
  clearTimeout(blurTimer);
  clearTimeout(saveTimer);
  blurTimer = null;
  saveTimer = null;
  isDragging = false;
  mode = 'capsule';
  if (win && !win.isDestroyed()) win.close();
  win = null;
}

/** Show or hide the whole companion when the settings switch flips. */
async function applyCompanionMode(enabled) {
  if (enabled) return createCompanionWindow();
  closeCompanionWindow();
  return snapshot();
}

function isCompanionWindowSender(sender) {
  return Boolean(win && !win.isDestroyed() && win.webContents.id === sender.id);
}

function expandCompanionWindow() {
  return setExpanded(true);
}

function collapseCompanionWindow() {
  return setExpanded(false);
}

module.exports = {
  applyCompanionMode,
  buildCompanionWindowOptions,
  closeCompanionWindow,
  collapseCompanionWindow,
  createCompanionWindow,
  dragEnd,
  dragMove,
  expandCompanionWindow,
  flushPersistence,
  isCompanionWindowSender,
  moveTo,
  saveAnchor,
  scheduleCollapseCheck,
  setCapsuleReveal,
  setCapsuleStyle,
  setMode,
  setSide,
  snapshot,
};
