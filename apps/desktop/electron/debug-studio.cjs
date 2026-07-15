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
    debugStudioWindow.focus();
    return debugStudioWindow;
  }

  debugStudioWindow = new BrowserWindow({
    width: 1400,
    height: 900,
    minWidth: 900,
    minHeight: 600,
    backgroundColor: '#0d1117',
    title: 'Agent Debug Studio',
    icon: path.join(__dirname, '..', isDev ? 'public' : 'dist', 'tinadec.ico'),
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

  debugStudioWindow.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  debugStudioWindow.once('ready-to-show', () => {
    debugStudioWindow.show();
    if (isDev) {
      debugStudioWindow.webContents.openDevTools({ mode: 'detach' });
    }
  });

  // Load the Debug Studio page (uses hash routing)
  if (isDev) {
    await debugStudioWindow.loadURL(`${process.env.VITE_DEV_SERVER_URL}#/debug-studio`);
  } else {
    await debugStudioWindow.loadFile(path.join(__dirname, '..', 'dist', 'index.html'), {
      hash: '/debug-studio'
    });
  }

  debugStudioWindow.on('closed', () => {
    debugStudioWindow = null;
  });

  return debugStudioWindow;
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
