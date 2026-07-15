const { BrowserWindow, screen, ipcMain } = require('electron');
const path = require('node:path');
const fs = require('node:fs');

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);

/**
 * Map of windowId -> { window, tabId, type, title, state }
 */
const panelWindows = new Map();

/**
 * Path for persisting panel window layout to disk.
 */
const STATE_FILE = path.join(
  process.env.APPDATA || process.env.HOME || '.',
  '.tinadec-panel-layout.json'
);

/**
 * Read persisted panel layout from disk.
 * @returns {Array} persisted states array
 */
function loadPersistedLayout() {
  try {
    const raw = fs.readFileSync(STATE_FILE, 'utf-8');
    const data = JSON.parse(raw);
    if (Array.isArray(data.panels)) return data.panels;
    return [];
  } catch {
    return [];
  }
}

/**
 * Write panel layout to disk.
 * @param {Array} states
 */
function savePersistedLayout(states) {
  try {
    const data = JSON.stringify({ panels: states, savedAt: Date.now() });
    fs.writeFileSync(STATE_FILE, data, 'utf-8');
  } catch {
    // Best-effort persistence; ignore errors
  }
}

/**
 * Collect current panel window states for persistence.
 * @returns {Array} array of panel state objects
 */
function collectPanelStates() {
  const states = [];
  for (const [id, entry] of panelWindows) {
    if (entry.window && !entry.window.isDestroyed()) {
      const bounds = entry.window.getBounds();
      states.push({
        windowId: id,
        tabId: entry.tabId,
        type: entry.type,
        title: entry.title,
        state: entry.state,
        bounds,
      });
    }
  }
  return states;
}

/**
 * Persist current panel states to disk before quit.
 */
function persistPanelStatesForQuit() {
  const states = collectPanelStates();
  savePersistedLayout(states);
}

/**
 * Restore persisted panel windows from disk.
 * Called by main.cjs after the main window is ready.
 * @param {BrowserWindow} mainWindow
 */
async function restorePersistedPanels(mainWindow) {
  const states = loadPersistedLayout();
  if (states.length === 0) return;
  for (const state of states) {
    await createPanelWindow(state.tabId, state.type, state.title, state.state, {
      x: state.bounds?.x,
      y: state.bounds?.y,
      width: state.bounds?.width,
      height: state.bounds?.height,
      skipNotify: true,
      skipPersist: true,
    });
  }
}

/**
 * Get the main window. The main window is tagged with `isTinadecMain = true`
 * in its webPreferences custom metadata, or falls back to the first window
 * not tracked in panelWindows.
 * @returns {BrowserWindow|null}
 */
function getMainWindow() {
  const windows = BrowserWindow.getAllWindows();
  // First try to find the tagged main window
  for (const w of windows) {
    if (w.isDestroyed()) continue;
    if (w._isTinadecMain) return w;
  }
  // Fallback: return first non-panel window
  for (const w of windows) {
    if (!panelWindows.has(w.id) && !w.isDestroyed()) {
      return w;
    }
  }
  return null;
}

/**
 * Tag a BrowserWindow as the main TinadecOffice window so getMainWindow() can
 * reliably find it even when the Debug Studio window is also open.
 * @param {BrowserWindow} win
 */
function tagMainWindow(win) {
  win._isTinadecMain = true;
}

/**
 * Create a detached panel window.
 *
 * @param {string} tabId - Unique tab identifier
 * @param {string} type - Panel type (git, approval, events, etc.)
 * @param {string} title - Window title
 * @param {object} state - Tab state (sessionId, url, etc.)
 * @param {object} options - Window bounds options
 * @param {number} [options.x] - Window x position
 * @param {number} [options.y] - Window y position
 * @param {number} [options.width] - Window width
 * @param {number} [options.height] - Window height
 * @param {boolean} [options.skipNotify] - Skip notifying the main window
 * @param {boolean} [options.skipPersist] - Skip persistence on close
 * @returns {Promise<{windowId: number, tabId: string}>}
 */
async function createPanelWindow(tabId, type, title, state = {}, options = {}) {
  const cursor = screen.getCursorScreenPoint();
  const width = options.width ?? 440;
  const height = options.height ?? 640;

  // Clamp position to visible screen bounds
  let x = options.x ?? Math.round(cursor.x - width / 2);
  let y = options.y ?? Math.round(cursor.y - 30);

  // Ensure window appears within a visible display
  const displays = screen.getAllDisplays();
  const visibleDisplay = displays.find((d) => {
    const wa = d.workArea;
    return x >= wa.x - 100 && x <= wa.x + wa.width - 100 &&
           y >= wa.y - 50 && y <= wa.y + wa.height - 100;
  }) ?? displays[0];

  if (visibleDisplay) {
    const wa = visibleDisplay.workArea;
    x = Math.max(wa.x + 10, Math.min(x, wa.x + wa.width - width - 10));
    y = Math.max(wa.y + 10, Math.min(y, wa.y + wa.height - height - 10));
  }

  const win = new BrowserWindow({
    width,
    height,
    minWidth: 300,
    minHeight: 400,
    x,
    y,
    backgroundColor: '#0d1117',
    title: title || 'Panel',
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

  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  // Register the window in our tracking Map BEFORE loading so it's
  // tracked even if the load fails.
  const windowId = win.id;
  const entry = { window: win, tabId, type, title, state, skipPersist: options.skipPersist || false };
  panelWindows.set(windowId, entry);

  // CRITICAL: Register 'ready-to-show' BEFORE calling loadURL/loadFile.
  // In Electron, ready-to-show fires when the renderer process has loaded
  // the page and is ready to display. The loadURL() promise resolves AFTER
  // the page is loaded, so ready-to-show has already fired by then.
  // Registering the handler after loadURL means we miss the event entirely
  // and win.show() is never called — the window stays invisible.
  let hasShown = false;
  win.once('ready-to-show', () => {
    if (hasShown) return;
    hasShown = true;
    win.show();
    if (isDev && process.env.TINADEC_PANEL_DEVTOOLS === '1') {
      win.webContents.openDevTools({ mode: 'detach' });
    }
  });

  // Handle load failures: show the window anyway so the user sees something
  // instead of an invisible window.
  win.webContents.on('did-fail-load', (_e, errorCode, errorDescription) => {
    console.error(`[panelWindow] Failed to load panel URL (code ${errorCode}): ${errorDescription}`);
    if (!hasShown) {
      hasShown = true;
      win.show();
    }
  });

  // Timeout fallback: if the window hasn't shown after 5 seconds, force-show it.
  // This handles edge cases where ready-to-show never fires.
  setTimeout(() => {
    if (!hasShown && !win.isDestroyed()) {
      console.warn('[panelWindow] Window did not show within 5s timeout, force-showing');
      hasShown = true;
      win.show();
    }
  }, 5000);

  // Build hash route with query params for the detached panel
  const query = new URLSearchParams({
    detached: 'true',
    tabId: String(tabId),
    type: String(type),
    title: String(title || ''),
    state: JSON.stringify(state),
  });

  const hashPath = `/panel?${query.toString()}`;

  // Load the URL — do NOT await before registering ready-to-show.
  // The loadURL promise resolves after ready-to-show has already fired.
  if (isDev) {
    await win.loadURL(`${process.env.VITE_DEV_SERVER_URL}#${hashPath}`).catch((err) => {
      console.error('[panelWindow] loadURL error:', err.message);
    });
  } else {
    await win.loadFile(path.join(__dirname, '..', 'dist', 'index.html'), {
      hash: hashPath,
    }).catch((err) => {
      console.error('[panelWindow] loadFile error:', err.message);
    });
  }

  // Save layout when window is moved or resized (debounced)
  let boundsSaveTimer = null;
  const saveBounds = () => {
    if (boundsSaveTimer) clearTimeout(boundsSaveTimer);
    boundsSaveTimer = setTimeout(() => {
      const currentStates = collectPanelStates();
      savePersistedLayout(currentStates);
    }, 500);
  };

  win.on('move', saveBounds);
  win.on('resize', saveBounds);

  win.on('closed', () => {
    panelWindows.delete(windowId);
    if (boundsSaveTimer) clearTimeout(boundsSaveTimer);

    // Persist remaining windows
    const remaining = collectPanelStates();
    savePersistedLayout(remaining);

    // Notify main window that a panel was closed
    // Only send if this wasn't a reattach (reattach handles its own notification)
    if (!options.skipNotify && !entry._reattaching) {
      const main = getMainWindow();
      if (main && !main.isDestroyed()) {
        main.webContents.send('panel:closed', { windowId, tabId, type, title, state });
      }
    }
  });

  // Notify main window that a panel was detached
  if (!options.skipNotify) {
    const main = getMainWindow();
    if (main && !main.isDestroyed()) {
      main.webContents.send('panel:detached', { windowId, tabId, type, title });
    }
  }

  return { windowId, tabId };
}

/**
 * Reattach a panel window: notify the main window to re-add the tab,
 * then close the panel window WITHOUT triggering the panel:closed event.
 *
 * @param {number} windowId - The panel window id to reattach
 * @param {string} tabId
 * @param {string} type
 * @param {string} title
 * @param {object} state
 */
function reattachPanelWindow(windowId, tabId, type, title, state) {
  const entry = panelWindows.get(windowId);
  if (entry) {
    // Mark as reattaching so the 'closed' handler doesn't send panel:closed
    entry._reattaching = true;
  }

  // Notify the main window to re-add the tab
  const main = getMainWindow();
  if (main && !main.isDestroyed()) {
    main.webContents.send('panel:reattach', { tabId, type, title, state });
  }

  // Close the panel window
  if (entry && entry.window && !entry.window.isDestroyed()) {
    entry.window.close();
  }

  // Update persisted layout
  const remaining = collectPanelStates();
  savePersistedLayout(remaining);
}

/**
 * Close a specific panel window by windowId.
 * @param {number} windowId
 */
function closePanelWindow(windowId) {
  const entry = panelWindows.get(windowId);
  if (entry && entry.window && !entry.window.isDestroyed()) {
    entry.window.close();
  }
}

/**
 * Close all panel windows.
 */
function closeAllPanelWindows() {
  for (const [, entry] of panelWindows) {
    if (entry.window && !entry.window.isDestroyed()) {
      entry.window.close();
    }
  }
  panelWindows.clear();
}

/**
 * Get info about all open panel windows.
 * @returns {Array}
 */
function getAllPanelWindows() {
  const result = [];
  for (const [id, entry] of panelWindows) {
    result.push({
      windowId: id,
      tabId: entry.tabId,
      type: entry.type,
      title: entry.title,
    });
  }
  return result;
}

/**
 * Focus a specific panel window by windowId.
 * @param {number} windowId
 */
function focusPanelWindow(windowId) {
  const entry = panelWindows.get(windowId);
  if (entry && entry.window && !entry.window.isDestroyed()) {
    if (entry.window.isMinimized()) {
      entry.window.restore();
    }
    entry.window.focus();
  }
}

/**
 * Send a message to all panel windows (e.g. theme change).
 * @param {string} channel
 * @param {*} data
 */
function broadcastToPanels(channel, data) {
  for (const [, entry] of panelWindows) {
    if (entry.window && !entry.window.isDestroyed()) {
      entry.window.webContents.send(channel, data);
    }
  }
}

module.exports = {
  createPanelWindow,
  closePanelWindow,
  closeAllPanelWindows,
  getAllPanelWindows,
  focusPanelWindow,
  collectPanelStates,
  persistPanelStatesForQuit,
  restorePersistedPanels,
  reattachPanelWindow,
  broadcastToPanels,
  tagMainWindow,
  getMainWindow,
  loadPersistedLayout,
  savePersistedLayout,
};
