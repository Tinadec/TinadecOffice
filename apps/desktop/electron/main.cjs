const electron = require('electron');

// Fail fast when Electron was launched in Node mode (ELECTRON_RUN_AS_NODE is set).
// In that mode require('electron') only resolves to the electron.exe path string,
// so app/BrowserWindow/protocol are all undefined and the first protocol call
// throws "Cannot read properties of undefined" with no hint about the cause.
if (typeof electron !== 'object' || !electron.app) {
  console.error([
    '[tinadec] Electron 运行在 Node 模式（环境中存在 ELECTRON_RUN_AS_NODE）。',
    '[tinadec] 该模式下主进程 API（app/BrowserWindow/protocol/IPC）不可用，窗口无法创建。',
    '[tinadec] 修复：移除该环境变量后重试。',
    '[tinadec]   PowerShell:  Remove-Item Env:\\ELECTRON_RUN_AS_NODE',
    '[tinadec]   cmd:         set ELECTRON_RUN_AS_NODE=',
    '[tinadec]   bash:        unset ELECTRON_RUN_AS_NODE',
    '[tinadec] 提示：通过 `npm run dev` 启动时，scripts/dev.mjs 已自动剔除该变量。',
  ].join('\n'));
  process.exit(1);
}

const { app, BrowserWindow, clipboard, dialog, ipcMain, protocol, screen, shell } = electron;
const path = require('node:path');
const { configureElectronStorage } = require('./storagePaths.cjs');
const { createLogSink } = require('./logSink.cjs');
// Startup failures show before any window exists, so there is no interface language to follow yet:
// they say it in both languages, like the renderer's crash fallback.
const hostPaths = (() => {
  try { return configureElectronStorage(app); }
  catch (error) { dialog.showErrorBox('桌面存储配置错误 / Desktop storage configuration error', error.message); app.exit(1); throw error; }
})();
const hostLogSink = (() => {
  try { return createLogSink(hostPaths.hostLogs); }
  catch (error) { dialog.showErrorBox('日志配置错误 / Logging configuration error', error.message); app.exit(1); throw error; }
})();
for (const level of ['log', 'info', 'warn', 'error']) {
  const original = console[level].bind(console);
  console[level] = (...values) => {
    original(...values);
    try { hostLogSink.write('electron', `${new Date().toISOString()} ${level} ${require('node:util').format(...values)}\n`); } catch { /* stderr remains available if disk writes fail */ }
  };
}
const { loadAppConfig, resetGatewayUrl, saveGatewayUrl, saveUserStorageRoot, saveDebugStudioEnabled } = require('./appConfig.cjs');
const { discoverServices } = require('./serviceDiscovery.cjs');
const { ensureLocalServices, stopLocalServices, canonicalLocalGatewayUrl } = require('./serviceManager.cjs');
const { createHostControl } = require('./hostControl.cjs');
const { verifyManagedHost } = require('./hostIdentity.cjs');
const { createHostConnection } = require('./hostConnection.cjs');
let trustedHostReady = false;
let hostConnection;
let shuttingDown = false;
const hostControl = createHostControl({ startupToken: process.env.TINADEC_HOST_CONTROL_TOKEN, isTrustedHost: () => trustedHostReady });
// The development orchestrator can share a launch credential; keep it out of later renderer/terminal children.
delete process.env.TINADEC_HOST_CONTROL_TOKEN;
const { initializeTrustedHostRequests, registerTrustedHostWindow, isTrustedHostSender, isTrustedHostDocumentSender } = require('./trustedHostRequests.cjs');
initializeTrustedHostRequests({
  token: hostControl.serviceToken,
  devServerUrl: process.env.VITE_DEV_SERVER_URL,
  isManagedHost: () => trustedHostReady && Boolean(canonicalLocalGatewayUrl(process.env.TINADEC_RESOLVED_GATEWAY_URL)),
});
const layoutStore = require('./layoutStore.cjs');
const { createDebugStudioWindow, getDebugStudioWindow } = require('./debug-studio.cjs');
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
  getMainWindow,
  tagMainWindow,
} = require('./panelWindow.cjs');
const {
  registerTerminalIpc,
  destroyAllTerminals,
} = require('./terminalManager.cjs');
const {
  createPetWindow,
  closePetWindow,
  closePetWindowForPet,
  closeAllPetWindows,
  closeCurrentPetWindow,
  getCurrentPetWindowPet,
  getPetWindowPet,
  listPetWindows,
  setCurrentPetWindowBounds,
  setCurrentPetWindowClickThrough,
} = require('./petWindow.cjs');
const {
  downloadPet,
  fetchCatalog,
  fetchPetPreview,
  listDownloaded,
  openPetFolder,
  removePet,
  setEnabled,
} = require('./petStore.cjs');
const {
  APP_SCHEME_PRIVILEGES,
  appBundleUrl,
  registerAppBundleProtocol,
} = require('./appBundle.cjs');
const {
  MEDIA_SCHEME_PRIVILEGES,
  registerLocalMediaProtocol,
  sourceToMediaPath,
} = require('./localMedia.cjs');
const { attachExternalLinkGuards } = require('./externalLinks.cjs');

const DIST_DIR = path.join(__dirname, '..', 'dist');

protocol.registerSchemesAsPrivileged([
  {
    scheme: 'tinadec-pet-preview',
    privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true },
  },
  { scheme: 'app', privileges: APP_SCHEME_PRIVILEGES },
  { scheme: 'tinadec-media', privileges: MEDIA_SCHEME_PRIVILEGES },
]);

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);

if (process.platform === 'win32') {
  app.setAppUserModelId('com.tinadec.office');
}

function appConfigFile() {
  return hostPaths.desktopConfig;
}

async function createWindow() {
  const win = new BrowserWindow({
    width: 1440,
    height: 920,
    minWidth: 1120,
    minHeight: 720,
    backgroundColor: '#1e1e2e',
    title: 'TinadecOffice',
    icon: path.join(__dirname, '..', isDev ? 'public' : 'dist', 'tinadec.ico'),
    titleBarStyle: 'hidden',
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      // Same-origin policy stays on. The packaged bundle is served from `app://bundle`
      // instead of `file://` (see appBundle.cjs), which is what used to force this off:
      // without it any page the preview panel embeds could reach this window's
      // contextBridge surface, including the terminal IPC that spawns processes.
    }
  });

  // Tag this window as the main TinadecOffice window so panelWindow.cjs
  // can reliably distinguish it from the Debug Studio window.
  tagMainWindow(win);
  registerTrustedHostWindow(win);

  attachExternalLinkGuards(win.webContents);

  win.once('ready-to-show', () => {
    win.show();
    if (isDev) {
      win.webContents.openDevTools({ mode: 'detach' });
    }
  });

  if (isDev) {
    await win.loadURL(process.env.VITE_DEV_SERVER_URL);
  } else {
    await win.loadURL(appBundleUrl());
  }

  // Restore any persisted panel windows after the main window is ready
  setTimeout(() => {
    restorePersistedPanels(win).catch(() => {});
  }, 800);

  return win;
}

ipcMain.handle('tinadec:select-workspace-folders', async (event, title) => {
  return require('./workspaceFolders.cjs').selectWorkspaceFolders(event, { mainWindow: getMainWindow(), trusted: isTrustedHostSender, dialog }, title);
});

function appConfigSnapshot() {
  return { ...loadAppConfig(appConfigFile()), storage: {
  root: hostPaths.root, source: hostPaths.source, bootstrap_config: hostPaths.desktopConfig,
  managed: hostPaths.source === 'environment', local_services: Boolean(canonicalLocalGatewayUrl(process.env.TINADEC_RESOLVED_GATEWAY_URL)),
  } };
}
// Readiness recovery needs the trusted document, even while business authorization is unavailable.
ipcMain.handle('tinadec:host-status', (event) => {
  if (!isTrustedHostDocumentSender(event)) throw new Error('Host status requires a trusted host page.');
  return hostConnection?.snapshot() ?? { state: 'checking', managed: true };
});
ipcMain.handle('tinadec:host-retry', (event) => {
  if (getMainWindow()?.webContents !== event.sender || !isTrustedHostDocumentSender(event)) throw new Error('Host retry requires the trusted main host page.');
  return hostConnection?.retry() ?? { state: 'checking', managed: true };
});
function publishHostStatus(snapshot) {
  trustedHostReady = snapshot.managed && snapshot.state === 'ready';
  for (const win of BrowserWindow.getAllWindows()) {
    const contents = win.webContents;
    if (!win.isDestroyed() && !contents.isDestroyed()
      && isTrustedHostDocumentSender({ sender: contents, senderFrame: contents.mainFrame })) {
      contents.send('tinadec:host-status-changed', snapshot);
    }
  }
}

ipcMain.handle('tinadec:app-config', appConfigSnapshot);
ipcMain.handle('tinadec:gateway-url-save', (_event, gatewayUrl) => saveGatewayUrl(appConfigFile(), gatewayUrl));
ipcMain.handle('tinadec:gateway-url-reset', () => resetGatewayUrl(appConfigFile()));
ipcMain.handle('tinadec:storage-write-policy', (event, storageId, allow) => {
  if (getMainWindow()?.webContents !== event.sender || !isTrustedHostSender(event)) throw new Error('Storage policy changes require the trusted main host page.');
  return hostControl.setStorageWritePolicy(process.env.TINADEC_RESOLVED_GATEWAY_URL, storageId, allow);
});
ipcMain.handle('tinadec:storage-action', (event, storageId, action, input) => {
  if (getMainWindow()?.webContents !== event.sender || !isTrustedHostSender(event)) throw new Error('Storage changes require the trusted main host page.');
  return hostControl.storageAction(process.env.TINADEC_RESOLVED_GATEWAY_URL, storageId, action, input);
});
ipcMain.handle('tinadec:user-storage-root-save', (event, root) => {
  if (getMainWindow()?.webContents !== event.sender || !isTrustedHostSender(event)) throw new Error('User storage changes require the trusted main host page.');
  return saveUserStorageRoot(appConfigFile(), root);
});
ipcMain.handle('tinadec:discover-services', () =>
  discoverServices({ currentGatewayUrl: process.env.TINADEC_RESOLVED_GATEWAY_URL })
);
ipcMain.handle('tinadec:restart', () => {
  app.relaunch();
  app.exit(0);
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
ipcMain.handle('tinadec:debug-studio-enabled-save', (event, enabled) => {
  if (getMainWindow()?.webContents !== event.sender || !isTrustedHostSender(event)) {
    throw new Error('Debug Studio changes require the trusted main host page.');
  }
  saveDebugStudioEnabled(appConfigFile(), enabled);
  const config = appConfigSnapshot();
  for (const win of BrowserWindow.getAllWindows()) {
    if (!win.isDestroyed() && !win.webContents.isDestroyed()) {
      win.webContents.send('tinadec:debug-studio-enabled-changed');
    }
  }
  if (!config.debug_studio_enabled) {
    const debugWin = getDebugStudioWindow();
    if (debugWin && !debugWin.isDestroyed()) debugWin.close();
  }
  return config;
});
ipcMain.handle('tinadec:open-debug-studio', async (event) => {
  if (getMainWindow()?.webContents !== event.sender || !isTrustedHostSender(event)) return false;
  if (!loadAppConfig(appConfigFile()).debug_studio_enabled) return false;
  return Boolean(await createDebugStudioWindow());
});

// --- TinadecUIE layout persistence IPC ---
// The renderer owns layout semantics; the main process is a thin validated store.
ipcMain.handle('tinadec:layout-load', async () => {
  try {
    return await layoutStore.load();
  } catch (err) {
    console.warn('[main] layout-load failed:', err.message);
    return null;
  }
});
ipcMain.handle('tinadec:layout-save', async (_event, payload) => {
  try {
    await layoutStore.save(payload);
    return { ok: true };
  } catch (err) {
    console.warn('[main] layout-save failed:', err.message);
    return { ok: false, error: err.message };
  }
});

// --- Local pet window IPC ---
ipcMain.handle('tinadec:pet-create', async (_event, petId) => createPetWindow(petId));
ipcMain.handle('tinadec:pet-close', async (_event, instanceId) => closePetWindow(instanceId));
ipcMain.handle('tinadec:pet-list', async () => listPetWindows());
ipcMain.handle('tinadec:pet-window-pet', async (_event, instanceId) => getPetWindowPet(instanceId));
ipcMain.handle('tinadec:pet-current', async (event) => getCurrentPetWindowPet(event.sender));
ipcMain.handle('tinadec:pet-current-bounds', async (event, bounds) => setCurrentPetWindowBounds(event.sender, bounds));
ipcMain.handle('tinadec:pet-current-click-through', async (event, enabled) => setCurrentPetWindowClickThrough(event.sender, enabled));
ipcMain.handle('tinadec:pet-current-close', async (event) => {
  const pet = await closeCurrentPetWindow(event.sender);
  if (pet) {
    for (const win of BrowserWindow.getAllWindows()) {
      if (!win.isDestroyed() && win.webContents.id !== event.sender.id) {
        win.webContents.send('tinadec:pet-changed', { slug: pet.slug, enabled: pet.enabled });
      }
    }
  }
  return Boolean(pet);
});
ipcMain.handle('tinadec:pet-catalog', async (_event, force) => {
  const pets = await fetchCatalog(Boolean(force));
  return pets.map((pet) => ({
    slug: pet.slug,
    displayName: pet.displayName,
    kind: pet.kind,
    submittedBy: pet.submittedBy,
    previewUrl: `tinadec-pet-preview://pet/${encodeURIComponent(pet.slug)}`,
  }));
});
ipcMain.handle('tinadec:pet-download', async (_event, slug) => downloadPet(slug));
ipcMain.handle('tinadec:pet-downloaded', async () => listDownloaded());
ipcMain.handle('tinadec:pet-enabled', async (_event, slug, enabled) => {
  const pet = await setEnabled(slug, enabled);
  if (pet.enabled) await createPetWindow(pet.slug);
  else closePetWindowForPet(pet.slug);
  return pet;
});
ipcMain.handle('tinadec:pet-open-folder', async (_event, slug) => {
  const result = await shell.openPath(await openPetFolder(slug));
  if (result) throw new Error(result);
  return true;
});
ipcMain.handle('tinadec:pet-remove', async (_event, slug) => {
  closePetWindowForPet(slug);
  return removePet(slug);
});

// --- Background File Selection IPC ---
// The dialog's wording comes from the renderer, where the locale bundles live; without it the OS
// default title and the bare extension lists stand in, so no language is spelled out here.
const BACKGROUND_EXTENSIONS = {
  image: ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg'],
  video: ['mp4', 'webm', 'ogg', 'mov', 'avi'],
};
const dialogText = (value) => (typeof value === 'string' && value.trim() && value.length <= 200 ? value : undefined);
ipcMain.handle('tinadec:select-background-file', async (event, type, text) => {
  const win = BrowserWindow.fromWebContents(event.sender);
  if (!win) return null;

  const extensions = BACKGROUND_EXTENSIONS[type];
  const filters = [
    ...(extensions ? [{ name: dialogText(text?.[type]) ?? extensions.join(', '), extensions }] : []),
    { name: dialogText(text?.all) ?? '*', extensions: ['*'] },
  ];

  const result = await dialog.showOpenDialog(win, {
    properties: ['openFile'],
    title: dialogText(text?.title),
    filters,
  });

  if (result.canceled || result.filePaths.length === 0) {
    return null;
  }

  return result.filePaths[0];
});

// --- Clipboard IPC ---
// The selection context menu reads/writes the clipboard through the main
// process: navigator.clipboard.readText() requires a permission grant and a
// secure context, which the dev renderer's http origin does not reliably have.
ipcMain.handle('tinadec:clipboard-read-text', () => clipboard.readText());
ipcMain.handle('tinadec:clipboard-write-text', (_event, text) => {
  if (typeof text !== 'string') return false;
  clipboard.writeText(text);
  return true;
});

// --- Background Image Read IPC (for Monet color extraction) ---
// The renderer runs on an http origin in dev, so canvas getImageData() on a
// file:// image would be tainted; the main process reads the bytes instead.
const IMAGE_MIME = {
  jpg: 'image/jpeg', jpeg: 'image/jpeg', png: 'image/png',
  gif: 'image/gif', webp: 'image/webp', bmp: 'image/bmp', svg: 'image/svg+xml'
};
const MAX_BACKGROUND_IMAGE_BYTES = 50 * 1024 * 1024;

ipcMain.handle('tinadec:read-image-data-url', async (_event, source) => {
  try {
    // The background setting holds a `tinadec-media://` URL now (see localMedia.cjs), so the
    // pixels come from the path behind that URL. Raw paths and file:// URLs are still
    // accepted so a value persisted by an older build keeps working.
    const filePath = sourceToMediaPath(String(source ?? ''));
    if (!filePath) return null;
    const ext = path.extname(filePath).slice(1).toLowerCase();
    const mime = IMAGE_MIME[ext];
    if (!mime) return null;
    const fs = require('node:fs/promises');
    const stat = await fs.stat(filePath);
    if (!stat.isFile() || stat.size > MAX_BACKGROUND_IMAGE_BYTES) return null;
    const data = await fs.readFile(filePath);
    return `data:${mime};base64,${data.toString('base64')}`;
  } catch {
    return null;
  }
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

// Broadcast status notification to all non-pet, non-sender windows
ipcMain.on('tinadec:broadcast-status-notification', (event, payload) => {
  // Validate payload shape minimally — drop malformed messages silently
  if (!payload || typeof payload !== 'object') return;
  if (payload.op !== 'raise' && payload.op !== 'clear') return;
  if (typeof payload.key !== 'string' || !payload.key) return;

  const senderId = event.sender.id;
  const channel = 'tinadec:status-notification';

  // Forward to main window (exclude sender)
  const mainWin = getMainWindow();
  if (mainWin && !mainWin.isDestroyed() && mainWin.webContents.id !== senderId) {
    mainWin.webContents.send(channel, payload);
  }

  // Forward to Debug Studio window (exclude sender)
  const debugWin = getDebugStudioWindow();
  if (debugWin && !debugWin.isDestroyed() && debugWin.webContents.id !== senderId) {
    debugWin.webContents.send(channel, payload);
  }

  // Forward to all detached panel windows (exclude sender via BrowserWindow.fromId)
  const panelInfos = getAllPanelWindows();
  for (const info of panelInfos) {
    const win = BrowserWindow.fromId(info.windowId);
    if (win && !win.isDestroyed() && win.webContents.id !== senderId) {
      win.webContents.send(channel, payload);
    }
  }
});

// Register terminal IPC handlers. Output is delivered only to windows that can host
// a terminal view; pet windows and Debug Studio have none and used to receive every
// chunk of the user's shell because delivery broadcast to all windows.
registerTerminalIpc({
  hostFilter: () => BrowserWindow.getAllWindows().filter(
    (w) => !w.isDestroyed() && (w._isTinadecMain || w._isTinadecPanel),
  ),
});

// Persist panel states before quit and clean up terminals
app.on('before-quit', () => {
  shuttingDown = true;
  trustedHostReady = false;
  hostConnection?.stop();
  void stopLocalServices().catch(() => {});
  destroyAllTerminals();
  persistPanelStatesForQuit();
  closeAllPetWindows();
});

app.whenReady().then(async () => {
  let gatewayUrl;
  try { gatewayUrl = loadAppConfig(appConfigFile()).gateway_url; }
  catch (error) {
    dialog.showErrorBox('TinadecOffice', `无法读取 TOML 配置 / Cannot read the TOML configuration\n${appConfigFile()}\n${error.message}`);
    app.quit(); return;
  }
  process.env.TINADEC_RESOLVED_GATEWAY_URL = gatewayUrl;
  const managed = Boolean(canonicalLocalGatewayUrl(gatewayUrl));
  let localServicesInitialized = false;
  hostConnection = createHostConnection({
    managed,
    // Packaged service startup can take up to two bounded 90-second waits.
    verificationTimeoutMs: app.isPackaged ? 200_000 : 10_000,
    onStatusChange: publishHostStatus,
    verify: async ({ signal }) => {
      signal.throwIfAborted();
      if (!managed) return true;
      if (!localServicesInitialized) {
        await ensureLocalServices({
          isPackaged: app.isPackaged,
          gatewayUrl,
          resourcesPath: process.resourcesPath,
          userStorageRoot: hostPaths.root,
          hostControlToken: hostControl.serviceToken,
          localAppDataPath: process.env.LOCALAPPDATA,
          signal,
        });
        signal.throwIfAborted();
        localServicesInitialized = true;
      }
      signal.throwIfAborted();
      return verifyManagedHost(hostControl.serviceToken, { signal });
    },
  });
  // A temporary startup failure remains recoverable after the page opens.
  const initialHost = await hostConnection.start();
  if (shuttingDown) return;
  if (initialHost.error) console.error('[tinadec] local host unavailable:', initialHost.error.code);

  registerAppBundleProtocol({ protocol, distDir: DIST_DIR });
  registerLocalMediaProtocol({ protocol });
  protocol.handle('tinadec-pet-preview', async (request) => {
    try {
      const url = new URL(request.url);
      if (url.hostname !== 'pet') return new Response(null, { status: 404 });
      const slug = decodeURIComponent(url.pathname.replace(/^\/+/, ''));
      const preview = await fetchPetPreview(slug);
      return new Response(preview.buffer, {
        headers: {
          'Content-Type': preview.mime,
          'Cache-Control': 'public, max-age=3600',
        },
      });
    } catch {
      return new Response(null, {
        status: 404,
        headers: { 'Cache-Control': 'no-store' },
      });
    }
  });
  await createWindow();
  const downloadedPets = await listDownloaded().catch(() => []);
  await Promise.all(downloadedPets.filter((pet) => pet.enabled).map((pet) => createPetWindow(pet.slug).catch(() => undefined)));

  app.on('activate', async () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      await createWindow();
    }
  });
});

app.on('window-all-closed', () => {
  closeAllPanelWindows();
  closeAllPetWindows();
  if (process.platform !== 'darwin') {
    app.quit();
  }
});
