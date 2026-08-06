const { BrowserWindow, screen } = require('electron');
const path = require('node:path');
const fs = require('node:fs');

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);
const MAX_STATE_BYTES = 1024 * 1024;
const LEGACY_STATE_FILE = path.join(process.env.APPDATA || process.env.USERPROFILE || '.', '.tinadec-panel-layout.json');
const LEGACY_CARD_TYPES = Object.freeze({
  git: 'home.git',
  approval: 'home.approvals',
  orchestration: 'home.orchestration',
  events: 'home.events',
  doctor: 'home.doctor',
  preview: 'tool.browser',
  agent: 'home.agent',
  terminal: 'home.terminal',
});

const panelWindows = new Map();
let detachedStateFile = null;
let legacyStateFile = LEGACY_STATE_FILE;

function isRecord(value) {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function safeState(value) {
  if (!isRecord(value)) return {};
  try {
    const serialized = JSON.stringify(value);
    if (Buffer.byteLength(serialized, 'utf8') > MAX_STATE_BYTES) return {};
    return JSON.parse(serialized);
  } catch {
    return {};
  }
}

function validPageId(value) {
  return ['home', 'settings', 'market', 'code', 'debug'].includes(value);
}

function validSlotId(value) {
  return ['left', 'center', 'right'].includes(value);
}

function normalizeWorkbenchPayload(value, fallback = {}) {
  const embedded = isRecord(value?.__workbench) ? value.__workbench : value;
  const card = isRecord(embedded?.card) ? embedded.card : {};
  const legacyType = typeof fallback.type === 'string' ? fallback.type : '';
  const type = typeof card.type === 'string' && card.type
    ? card.type
    : LEGACY_CARD_TYPES[legacyType] || legacyType;
  const id = typeof card.id === 'string' && card.id
    ? card.id
    : typeof fallback.tabId === 'string' && fallback.tabId
      ? fallback.tabId
      : `detached:${Date.now()}`;
  if (!type || !id) return null;
  const rawPlacement = isRecord(embedded?.returnPlacement) ? embedded.returnPlacement : {};
  return {
    version: 1,
    card: {
      id,
      type,
      state: safeState(card.state ?? (isRecord(value?.__workbench) ? embedded.card?.state : value)),
    },
    pageId: validPageId(embedded?.pageId) ? embedded.pageId : 'home',
    title: typeof embedded?.title === 'string' && embedded.title
      ? embedded.title
      : typeof fallback.title === 'string' && fallback.title
        ? fallback.title
        : type,
    returnPlacement: {
      slotId: validSlotId(rawPlacement.slotId) ? rawPlacement.slotId : 'right',
      stackId: rawPlacement.stackId === 'secondary' ? 'secondary' : 'primary',
      index: Number.isInteger(rawPlacement.index) && rawPlacement.index >= 0 ? rawPlacement.index : 0,
    },
  };
}

function configurePanelWindowStore(filePath, options = {}) {
  detachedStateFile = path.resolve(filePath);
  if (typeof options.legacyFilePath === 'string' && options.legacyFilePath) {
    legacyStateFile = path.resolve(options.legacyFilePath);
  }
}

function stateFile() {
  return detachedStateFile || path.join(process.env.APPDATA || process.env.USERPROFILE || '.', 'TinadecOffice', 'detached-cards.json');
}

function atomicWriteJson(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const temporary = `${filePath}.${process.pid}.tmp`;
  try {
    fs.writeFileSync(temporary, JSON.stringify(value), 'utf8');
    fs.renameSync(temporary, filePath);
  } finally {
    if (fs.existsSync(temporary)) fs.rmSync(temporary, { force: true });
  }
}

function migrateLegacyPanel(panel) {
  if (!isRecord(panel)) return null;
  const workbench = normalizeWorkbenchPayload(panel.state, panel);
  if (!workbench) return null;
  return { workbench, bounds: isRecord(panel.bounds) ? panel.bounds : {} };
}

function loadPersistedLayout() {
  try {
    const raw = fs.readFileSync(stateFile(), 'utf8');
    if (Buffer.byteLength(raw, 'utf8') > MAX_STATE_BYTES) return [];
    const parsed = JSON.parse(raw);
    if (parsed?.version === 1 && Array.isArray(parsed.windows)) {
      return parsed.windows.filter((entry) => isRecord(entry) && normalizeWorkbenchPayload(entry.workbench));
    }
  } catch {
    // Fall through to the one-time legacy import.
  }

  try {
    const parsed = JSON.parse(fs.readFileSync(legacyStateFile, 'utf8'));
    const migrated = Array.isArray(parsed?.panels) ? parsed.panels.map(migrateLegacyPanel).filter(Boolean) : [];
    if (migrated.length > 0) {
      atomicWriteJson(stateFile(), { version: 1, windows: migrated, savedAt: Date.now(), migratedFrom: legacyStateFile });
    }
    return migrated;
  } catch {
    return [];
  }
}

function savePersistedLayout(states) {
  try {
    atomicWriteJson(stateFile(), { version: 1, windows: states, savedAt: Date.now() });
  } catch {
    // Detached-window persistence is best effort.
  }
}

function collectPanelStates() {
  const states = [];
  for (const entry of panelWindows.values()) {
    if (!entry.window || entry.window.isDestroyed()) continue;
    states.push({ workbench: entry.workbench, bounds: entry.window.getBounds() });
  }
  return states;
}

function persistPanelStatesForQuit() {
  savePersistedLayout(collectPanelStates());
}

function getMainWindow() {
  const windows = BrowserWindow.getAllWindows();
  return windows.find((window) => !window.isDestroyed() && window._isTinadecMain)
    || windows.find((window) => !window.isDestroyed() && !panelWindows.has(window.id))
    || null;
}

function tagMainWindow(win) {
  win._isTinadecMain = true;
}

function visibleBounds(options = {}) {
  const cursor = screen.getCursorScreenPoint();
  const width = Number.isFinite(options.width) ? Math.max(300, Math.min(1600, options.width)) : 440;
  const height = Number.isFinite(options.height) ? Math.max(400, Math.min(1200, options.height)) : 640;
  let x = Number.isFinite(options.x) ? options.x : Math.round(cursor.x - width / 2);
  let y = Number.isFinite(options.y) ? options.y : Math.round(cursor.y - 30);
  const displays = screen.getAllDisplays();
  const display = displays.find((item) => {
    const area = item.workArea;
    return x >= area.x - 100 && x <= area.x + area.width - 100 && y >= area.y - 50 && y <= area.y + area.height - 100;
  }) || displays[0];
  if (display) {
    const area = display.workArea;
    x = Math.max(area.x + 10, Math.min(x, area.x + area.width - width - 10));
    y = Math.max(area.y + 10, Math.min(y, area.y + area.height - height - 10));
  }
  return { x, y, width, height };
}

function buildPanelHashPath(windowId) {
  if (!Number.isInteger(windowId) || windowId <= 0) throw new Error('Invalid detached window id.');
  return `/panel?windowId=${encodeURIComponent(String(windowId))}`;
}

async function createWorkbenchCardWindow(payload, options = {}) {
  const workbench = normalizeWorkbenchPayload(payload);
  if (!workbench) throw new Error('Invalid detached Workbench card payload.');
  const bounds = visibleBounds(options);
  const win = new BrowserWindow({
    ...bounds,
    minWidth: 300,
    minHeight: 400,
    backgroundColor: '#0d1117',
    title: workbench.title || 'Panel',
    icon: path.join(__dirname, '..', isDev ? 'public' : 'dist', 'tinadec.ico'),
    frame: false,
    autoHideMenuBar: true,
    show: false,
    resizable: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: false,
    },
  });
  const windowId = win.id;
  const entry = { window: win, workbench, reattaching: false };
  panelWindows.set(windowId, entry);
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  let shown = false;
  let showFallbackTimer = null;
  const showWindow = () => {
    if (shown || win.isDestroyed()) return;
    shown = true;
    if (showFallbackTimer) clearTimeout(showFallbackTimer);
    win.show();
  };
  win.once('ready-to-show', () => {
    showWindow();
    if (isDev && process.env.TINADEC_PANEL_DEVTOOLS === '1') win.webContents.openDevTools({ mode: 'detach' });
  });
  win.webContents.on('did-fail-load', showWindow);
  showFallbackTimer = setTimeout(showWindow, 5000);

  const hashPath = buildPanelHashPath(windowId);
  if (isDev) {
    await win.loadURL(`${process.env.VITE_DEV_SERVER_URL}?splash=0#${hashPath}`).catch((error) => console.error('[panelWindow] loadURL error:', error.message));
  } else {
    await win.loadFile(path.join(__dirname, '..', 'dist', 'index.html'), { hash: hashPath, query: { splash: '0' } })
      .catch((error) => console.error('[panelWindow] loadFile error:', error.message));
  }

  let saveTimer = null;
  const scheduleSave = () => {
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = setTimeout(() => savePersistedLayout(collectPanelStates()), 350);
  };
  win.on('move', scheduleSave);
  win.on('resize', scheduleSave);
  win.on('closed', () => {
    panelWindows.delete(windowId);
    if (saveTimer) clearTimeout(saveTimer);
    if (showFallbackTimer) clearTimeout(showFallbackTimer);
    savePersistedLayout(collectPanelStates());
    if (!entry.reattaching && !options.skipNotify) {
      const main = getMainWindow();
      if (main && !main.isDestroyed()) {
        main.webContents.send('panel:closed', {
          windowId,
          tabId: workbench.card.id,
          type: workbench.card.type,
          title: workbench.title,
          state: workbench.card.state,
          workbench,
        });
      }
    }
  });

  if (!options.skipNotify) {
    const main = getMainWindow();
    if (main && !main.isDestroyed()) {
      main.webContents.send('panel:detached', {
        windowId,
        tabId: workbench.card.id,
        type: workbench.card.type,
        title: workbench.title,
        workbench,
      });
    }
  }
  return { windowId, tabId: workbench.card.id };
}

async function createPanelWindow(tabId, type, title, state = {}, options = {}) {
  const payload = normalizeWorkbenchPayload(state, { tabId, type, title });
  return createWorkbenchCardWindow(payload, options);
}

function getPanelWindowState(sender) {
  const win = BrowserWindow.fromWebContents(sender);
  const entry = win ? panelWindows.get(win.id) : null;
  if (!entry || entry.window.webContents.id !== sender.id) return null;
  return { windowId: win.id, workbench: entry.workbench };
}

function updatePanelWindowState(sender, state) {
  const win = BrowserWindow.fromWebContents(sender);
  const entry = win ? panelWindows.get(win.id) : null;
  if (!entry || entry.window.webContents.id !== sender.id || !isRecord(state)) return false;
  entry.workbench = {
    ...entry.workbench,
    card: { ...entry.workbench.card, state: safeState(state) },
  };
  savePersistedLayout(collectPanelStates());
  return true;
}

function reattachPanelWindow(windowId, tabId, type, title, state) {
  const entry = panelWindows.get(windowId);
  if (!entry) return false;
  if (isRecord(state)) updatePanelWindowState(entry.window.webContents, state);
  entry.reattaching = true;
  const main = getMainWindow();
  if (main && !main.isDestroyed()) {
    main.webContents.send('panel:reattach', {
      tabId: entry.workbench.card.id || tabId,
      type: entry.workbench.card.type || type,
      title: entry.workbench.title || title,
      state: entry.workbench.card.state,
      workbench: entry.workbench,
    });
  }
  if (!entry.window.isDestroyed()) entry.window.close();
  savePersistedLayout(collectPanelStates());
  return true;
}

async function restorePersistedPanels() {
  for (const state of loadPersistedLayout()) {
    const workbench = normalizeWorkbenchPayload(state.workbench);
    if (!workbench) continue;
    await createWorkbenchCardWindow(workbench, { ...state.bounds, skipNotify: true });
  }
}

function closePanelWindow(windowId) {
  const entry = panelWindows.get(Number(windowId));
  if (entry && !entry.window.isDestroyed()) entry.window.close();
}

function closeAllPanelWindows() {
  for (const entry of panelWindows.values()) if (!entry.window.isDestroyed()) entry.window.close();
  panelWindows.clear();
}

function getAllPanelWindows() {
  return [...panelWindows.entries()].map(([windowId, entry]) => ({
    windowId,
    tabId: entry.workbench.card.id,
    type: entry.workbench.card.type,
    title: entry.workbench.title,
    pageId: entry.workbench.pageId,
  }));
}

function focusPanelWindow(windowId) {
  const entry = panelWindows.get(Number(windowId));
  if (!entry || entry.window.isDestroyed()) return;
  if (entry.window.isMinimized()) entry.window.restore();
  entry.window.focus();
}

function broadcastToPanels(channel, data) {
  for (const entry of panelWindows.values()) {
    if (!entry.window.isDestroyed()) entry.window.webContents.send(channel, data);
  }
}

module.exports = {
  LEGACY_CARD_TYPES,
  LEGACY_STATE_FILE,
  MAX_STATE_BYTES,
  buildPanelHashPath,
  normalizeWorkbenchPayload,
  configurePanelWindowStore,
  createWorkbenchCardWindow,
  createPanelWindow,
  closePanelWindow,
  closeAllPanelWindows,
  getAllPanelWindows,
  focusPanelWindow,
  collectPanelStates,
  persistPanelStatesForQuit,
  restorePersistedPanels,
  reattachPanelWindow,
  getPanelWindowState,
  updatePanelWindowState,
  broadcastToPanels,
  tagMainWindow,
  getMainWindow,
  loadPersistedLayout,
  savePersistedLayout,
};
