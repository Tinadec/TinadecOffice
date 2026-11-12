const { BrowserWindow } = require('electron');
const path = require('node:path');
const { appBundleUrl } = require('./appBundle.cjs');
const { attachExternalLinkGuards } = require('./externalLinks.cjs');
const { registerTrustedHostWindow } = require('./trustedHostRequests.cjs');

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);

let denvWindow = null;

async function createDenvWindow() {
  if (denvWindow && !denvWindow.isDestroyed()) {
    if (denvWindow.isMinimized()) denvWindow.restore();
    denvWindow.show();
    denvWindow.focus();
    return denvWindow;
  }

  const win = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 980,
    minHeight: 640,
    backgroundColor: '#15121a',
    title: 'TinadecDenv',
    icon: path.join(__dirname, '..', isDev ? 'public' : 'dist', 'tinadec.ico'),
    titleBarStyle: 'hidden',
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  denvWindow = win;
  registerTrustedHostWindow(win);
  attachExternalLinkGuards(win.webContents);

  win.once('ready-to-show', () => {
    win.show();
  });

  win.on('closed', () => {
    if (denvWindow === win) denvWindow = null;
  });

  win.webContents.on('render-process-gone', (_event, details) => {
    console.error(`[denv] Renderer exited (${details.reason})`);
    if (!win.isDestroyed()) win.destroy();
  });

  win.webContents.on('did-fail-load', (_event, errorCode, errorDescription, _url, isMainFrame) => {
    if (!isMainFrame || errorCode === -3) return;
    console.error(`[denv] Failed to load (code ${errorCode}): ${errorDescription}`);
    if (!win.isDestroyed()) win.destroy();
  });

  try {
    if (isDev) {
      await win.loadURL(`${process.env.VITE_DEV_SERVER_URL}?splash=0#/denv`);
    } else {
      await win.loadURL(appBundleUrl({ hash: '/denv', query: { splash: '0' } }));
    }
  } catch (error) {
    console.error('[denv] Load failed:', error.message);
    if (!win.isDestroyed()) win.destroy();
  }

  return win.isDestroyed() ? null : win;
}

function getDenvWindow() {
  return denvWindow;
}

function closeDenvWindow() {
  if (denvWindow && !denvWindow.isDestroyed()) denvWindow.close();
}

module.exports = {
  createDenvWindow,
  getDenvWindow,
  closeDenvWindow,
};
