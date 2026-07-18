const { BrowserWindow } = require('electron');
const path = require('node:path');

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);

let debugStudioWindow = null;

/**
 * Create and return the Agent Debug Studio window.
 * This is a separate BrowserWindow from the main TinadecOffice window.
 */
async function createDebugStudioWindow() {
  if (debugStudioWindow && !debugStudioWindow.isDestroyed()) {
    if (debugStudioWindow.isMinimized()) debugStudioWindow.restore();
    debugStudioWindow.show();
    debugStudioWindow.focus();
    return debugStudioWindow;
  }

  const win = new BrowserWindow({
    width: 1400,
    height: 900,
    minWidth: 900,
    minHeight: 600,
    backgroundColor: '#0d1117',
    title: 'Agent Debug Studio',
    icon: path.join(__dirname, '..', isDev ? 'public' : 'dist', 'tinadec.ico'),
    titleBarStyle: 'hidden',
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
  debugStudioWindow = win;

  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  win.once('ready-to-show', () => {
    win.show();
    if (isDev) {
      win.webContents.openDevTools({ mode: 'detach' });
    }
  });

  win.on('closed', () => {
    if (debugStudioWindow === win) debugStudioWindow = null;
  });

  win.webContents.on('render-process-gone', (_event, details) => {
    console.error(`[debug-studio] Renderer exited (${details.reason})`);
    if (!win.isDestroyed()) win.destroy();
  });

  win.webContents.on('did-fail-load', (_event, errorCode, errorDescription, _url, isMainFrame) => {
    if (!isMainFrame || errorCode === -3) return;
    console.error(`[debug-studio] Failed to load (code ${errorCode}): ${errorDescription}`);
    if (!win.isDestroyed()) win.destroy();
  });

  // Load the Debug Studio page (uses hash routing).
  // ?splash=0 tells App.vue to skip the startup splash + main-rise animation —
  // child windows must not replay the first-launch sequence.
  try {
    if (isDev) {
      await win.loadURL(`${process.env.VITE_DEV_SERVER_URL}?splash=0#/debug-studio`);
    } else {
      await win.loadFile(path.join(__dirname, '..', 'dist', 'index.html'), {
        hash: '/debug-studio',
        query: { splash: '0' }
      });
    }
  } catch (error) {
    console.error('[debug-studio] Load failed:', error.message);
    if (!win.isDestroyed()) win.destroy();
  }

  return win.isDestroyed() ? null : win;
}

/**
 * Get the current Debug Studio window (may be null if closed).
 */
function getDebugStudioWindow() {
  return debugStudioWindow;
}

module.exports = {
  createDebugStudioWindow,
  getDebugStudioWindow
};
