const { app, BrowserWindow, dialog, ipcMain, screen } = require('electron');
const path = require('node:path');
const { createDebugStudioWindow } = require('./debug-studio.cjs');
const {
  createPanelWindow,
  closePanelWindow,
  closeAllPanelWindows,
  getAllPanelWindows,
  focusPanelWindow,
  persistPanelStatesForQuit,
  restorePersistedPanels,
  reattachPanelWindow,
  broadcastToPanels,
  tagMainWindow,
} = require('./panelWindow.cjs');

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);

async function createWindow() {
  const win = new BrowserWindow({
    width: 1440,
    height: 920,
    minWidth: 1120,
    minHeight: 720,
    backgroundColor: '#0d1117',
    title: 'TinadecCode',
    frame: false,
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: false
    }
  });

  // Tag this window as the main TinadecCode window so panelWindow.cjs
  // can reliably distinguish it from the Debug Studio window.
  tagMainWindow(win);

  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  win.once('ready-to-show', () => {
    win.show();
    if (isDev) {
      win.webContents.openDevTools({ mode: 'detach' });
    }
  });

  if (isDev) {
    await win.loadURL(process.env.VITE_DEV_SERVER_URL);
  } else {
    await win.loadFile(path.join(__dirname, '..', 'dist', 'index.html'));
  }

  // Restore any persisted panel windows after the main window is ready
  setTimeout(() => {
    restorePersistedPanels(win).catch(() => {});
  }, 800);

  return win;
}

ipcMain.handle('tinadec:open-project', async () => {
  const result = await dialog.showOpenDialog({
    properties: ['openDirectory'],
    title: 'Open project'
  });

  if (result.canceled || result.filePaths.length === 0) {
    return null;
  }

  return result.filePaths[0];
});

ipcMain.on('tinadec:minimize', (event) => {
  BrowserWindow.fromWebContents(event.sender)?.minimize();
});

ipcMain.on('tinadec:maximize', (event) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win) return;
  if (win.isMaximized()) {
    win.unmaximize();
  } else {
    win.maximize();
  }
});

ipcMain.on('tinadec:close', (event) => {
  BrowserWindow.fromWebContents(event.sender)?.close();
});

// --- Agent Debug Studio IPC ---
ipcMain.handle('tinadec:open-debug-studio', async () => {
  await createDebugStudioWindow();
  return true;
});

// --- Detached Panel Window IPC ---

// Detach a tab into a new floating window
ipcMain.handle('tinadec:detach-panel', async (event, tabId, type, title, state) => {
  const result = await createPanelWindow(tabId, type, title, state || {});
  return result;
});

// Reattach a panel window back to the main window (called from the panel window)
// This uses the sender's window id to find the panel entry, notify the main
// window to re-add the tab, then close the panel window cleanly.
ipcMain.handle('tinadec:reattach-panel', async (event, tabId, type, title, state) => {
  const senderWin = BrowserWindow.fromWebContents(event.sender);
  if (senderWin) {
    reattachPanelWindow(senderWin.id, tabId, type, title, state);
  }
  return true;
});

// Close a specific panel window by windowId
ipcMain.on('tinadec:close-panel-window', (event, windowId) => {
  closePanelWindow(windowId);
});

// Focus a specific panel window by windowId
ipcMain.on('tinadec:focus-panel-window', (event, windowId) => {
  focusPanelWindow(windowId);
});

// Get list of all open panel windows
ipcMain.handle('tinadec:get-panel-windows', async () => {
  return getAllPanelWindows();
});

// Get cursor screen position (for drag detection)
ipcMain.handle('tinadec:get-cursor-screen', async () => {
  const cursor = screen.getCursorScreenPoint();
  return { x: cursor.x, y: cursor.y };
});

// Get the main window bounds (for drag-out detection)
ipcMain.handle('tinadec:get-main-bounds', async () => {
  const windows = BrowserWindow.getAllWindows();
  for (const w of windows) {
    if (w._isTinadecMain && !w.isDestroyed()) {
      return w.getBounds();
    }
  }
  return null;
});

// Broadcast theme change to all panel windows
ipcMain.on('tinadec:broadcast-theme', (event, theme, accentColor) => {
  broadcastToPanels('panel:theme-changed', { theme, accentColor });
});

// Persist panel states before quit
app.on('before-quit', () => {
  persistPanelStatesForQuit();
});

app.whenReady().then(async () => {
  await createWindow();

  app.on('activate', async () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      await createWindow();
    }
  });
});

app.on('window-all-closed', () => {
  closeAllPanelWindows();
  if (process.platform !== 'darwin') {
    app.quit();
  }
});
