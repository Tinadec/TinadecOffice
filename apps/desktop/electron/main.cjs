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
const {
  registerTerminalIpc,
  destroyAllTerminals,
} = require('./terminalManager.cjs');

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);

// When GPU compositing is unavailable (e.g. sandboxed environments that
// block disk cache), transparent windows fail to render.  Setting
// TINADEC_DISABLE_TRANSPARENCY=1 falls back to an opaque frameless window
// with a solid background so the UI remains fully visible.
const noTransparency = process.env.TINADEC_DISABLE_TRANSPARENCY === '1';

async function createWindow() {
  const win = new BrowserWindow({
    width: 1440,
    height: 920,
    minWidth: 1120,
    minHeight: 720,
    // transparent: true lets CSS border-radius on <html> create
    // truly transparent rounded corners for the custom-drawn window.
    transparent: !noTransparency,
    backgroundColor: noTransparency ? '#1e1e2e' : '#00000000',
    title: 'TinadecOffice',
    icon: path.join(__dirname, '..', isDev ? 'public' : 'dist', 'tinadec-logo.png'),
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

  // Tag this window as the main TinadecOffice window so panelWindow.cjs
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

// --- Background File Selection IPC ---
ipcMain.handle('tinadec:select-background-file', async (event, type) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win) return null;
  
  let filters = [];
  
  switch (type) {
    case 'image':
      filters = [
        { name: 'Images', extensions: ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg'] },
        { name: 'All Files', extensions: ['*'] }
      ];
      break;
    case 'video':
      filters = [
        { name: 'Videos', extensions: ['mp4', 'webm', 'ogg', 'mov', 'avi'] },
        { name: 'All Files', extensions: ['*'] }
      ];
      break;
    default:
      filters = [
        { name: 'All Files', extensions: ['*'] }
      ];
  }
  
  const result = await dialog.showOpenDialog(win, {
    properties: ['openFile'],
    title: 'Select Background File',
    filters: filters
  });
  
  if (result.canceled || result.filePaths.length === 0) {
    return null;
  }
  
  return result.filePaths[0];
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

// Register terminal IPC handlers
registerTerminalIpc();

// Persist panel states before quit and clean up terminals
app.on('before-quit', () => {
  destroyAllTerminals();
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
