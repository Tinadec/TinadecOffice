const { contextBridge, ipcRenderer, webUtils } = require('electron');

// `webUtils.getPathForFile` exists from Electron 32; the optional call keeps a preload loaded by an
// older runtime from throwing at require time and taking the whole bridge down.
const electronWebUtils = webUtils ?? null;

contextBridge.exposeInMainWorld('tinadec', {
  gatewayUrl: () => process.env.TINADEC_RESOLVED_GATEWAY_URL ?? process.env.TINADEC_GATEWAY_URL ?? 'http://127.0.0.1:48730',
  getAppConfig: () => ipcRenderer.invoke('tinadec:app-config'),
  getHostStatus: () => ipcRenderer.invoke('tinadec:host-status'),
  retryHostConnection: () => ipcRenderer.invoke('tinadec:host-retry'),
  onHostStatusChanged: (callback) => {
    const handler = (_event, status) => callback(status);
    ipcRenderer.on('tinadec:host-status-changed', handler);
    return () => ipcRenderer.removeListener('tinadec:host-status-changed', handler);
  },
  setStorageWritePolicy: (storageId, allow) => ipcRenderer.invoke('tinadec:storage-write-policy', storageId, allow),
  storageAction: (storageId, action, input) => ipcRenderer.invoke('tinadec:storage-action', storageId, action, input),
  saveUserStorageRoot: (root) => ipcRenderer.invoke('tinadec:user-storage-root-save', root),
  saveGatewayUrl: (gatewayUrl) => ipcRenderer.invoke('tinadec:gateway-url-save', gatewayUrl),
  resetGatewayUrl: () => ipcRenderer.invoke('tinadec:gateway-url-reset'),
  discoverServices: () => ipcRenderer.invoke('tinadec:discover-services'),
  restartApp: () => ipcRenderer.invoke('tinadec:restart'),
  selectWorkspaceFolders: (title) => ipcRenderer.invoke('tinadec:select-workspace-folders', title),
  importWorkspaceFiles: (input) => ipcRenderer.invoke('tinadec:import-workspace-files', input),
  // Synchronous by necessity: the absolute path of a dropped file is only reachable during the
  // drop handler, and a Promise could not be awaited before the browser clears the FileList.
  // Empty when Electron gives no path (a synthetic File, a detached window without the bridge).
  pathForFile: (file) => {
    try { return electronWebUtils?.getPathForFile?.(file) ?? ''; } catch { return ''; }
  },
  minimizeWindow: () => ipcRenderer.send('tinadec:minimize'),
  maximizeWindow: () => ipcRenderer.send('tinadec:maximize'),
  closeWindow: () => ipcRenderer.send('tinadec:close'),
  openDebugStudio: () => ipcRenderer.invoke('tinadec:open-debug-studio'),
  saveDebugStudioEnabled: (enabled) => ipcRenderer.invoke('tinadec:debug-studio-enabled-save', enabled),
  // --- Logto OIDC login ---
  openLogin: () => ipcRenderer.invoke('tinadec:open-login'),
  logout: () => ipcRenderer.invoke('tinadec:logout'),
  getAuthState: () => ipcRenderer.invoke('tinadec:get-auth-state'),
  updateProfile: (patch) => ipcRenderer.invoke('tinadec:update-profile', patch),
  onDebugStudioEnabledChanged: (callback) => {
    const handler = () => callback();
    ipcRenderer.on('tinadec:debug-studio-enabled-changed', handler);
    return () => ipcRenderer.removeListener('tinadec:debug-studio-enabled-changed', handler);
  },

  // --- Window and tray preferences ---
  saveCloseToTray: (enabled) => ipcRenderer.invoke('tinadec:close-to-tray-save', enabled),
  onCloseToTrayChanged: (callback) => {
    const handler = (_event, value) => callback(value);
    ipcRenderer.on('tinadec:close-to-tray-changed', handler);
    return () => ipcRenderer.removeListener('tinadec:close-to-tray-changed', handler);
  },
  /** Lets the main process draw the tray menu in the interface language this window is showing. */
  setInterfaceLocale: (locale) => ipcRenderer.send('tinadec:interface-locale', locale),

  // --- Companion mode (docked capsule / panel) ---
  saveCompanionMode: (enabled) => ipcRenderer.invoke('tinadec:companion-mode-save', enabled),
  saveCompanionCapsuleStyle: (style) => ipcRenderer.invoke('tinadec:companion-capsule-style-save', style),
  saveCompanionCapsuleReveal: (reveal) => ipcRenderer.invoke('tinadec:companion-capsule-reveal-save', reveal),
  // The companion page is the only caller that succeeds here; the main process checks the sender.
  companion: {
    api: (method, path, body) => ipcRenderer.invoke('tinadec:companion-api', { method, path, body }),
    getState: () => ipcRenderer.invoke('tinadec:companion-state'),
    expand: () => ipcRenderer.invoke('tinadec:companion-expand'),
    collapse: () => ipcRenderer.invoke('tinadec:companion-collapse'),
    moveTo: (top) => ipcRenderer.invoke('tinadec:companion-move', top),
    setSide: (side) => ipcRenderer.invoke('tinadec:companion-side', side),
    setMode: (mode) => ipcRenderer.invoke('tinadec:companion-mode', mode),
    onStateChanged: (callback) => {
      const handler = (_event, state) => callback(state);
      ipcRenderer.on('tinadec:companion-state-changed', handler);
      return () => ipcRenderer.removeListener('tinadec:companion-state-changed', handler);
    },
  },

  // --- TinadecUIE layout persistence ---
  layout: {
    load: () => ipcRenderer.invoke('tinadec:layout-load'),
    save: (payload) => ipcRenderer.invoke('tinadec:layout-save', payload),
  },

  // --- Local Pet Window API ---
  pets: {
    create: (petId) => ipcRenderer.invoke('tinadec:pet-create', petId),
    close: (instanceId) => ipcRenderer.invoke('tinadec:pet-close', instanceId),
    list: () => ipcRenderer.invoke('tinadec:pet-list'),
    getWindowPet: (instanceId) => ipcRenderer.invoke('tinadec:pet-window-pet', instanceId),
    getCurrent: () => ipcRenderer.invoke('tinadec:pet-current'),
    setCurrentBounds: (bounds) => ipcRenderer.invoke('tinadec:pet-current-bounds', bounds),
    setCurrentClickThrough: (enabled) => ipcRenderer.invoke('tinadec:pet-current-click-through', enabled),
    closeCurrent: () => ipcRenderer.invoke('tinadec:pet-current-close'),
    onChanged: (callback) => {
      const handler = (_event, pet) => callback(pet);
      ipcRenderer.on('tinadec:pet-changed', handler);
      return () => ipcRenderer.removeListener('tinadec:pet-changed', handler);
    },
    fetchCatalog: (force = false) => ipcRenderer.invoke('tinadec:pet-catalog', force),
    download: (slug) => ipcRenderer.invoke('tinadec:pet-download', slug),
    listDownloaded: () => ipcRenderer.invoke('tinadec:pet-downloaded'),
    setEnabled: (slug, enabled) => ipcRenderer.invoke('tinadec:pet-enabled', slug, enabled),
    openFolder: (slug) => ipcRenderer.invoke('tinadec:pet-open-folder', slug),
    remove: (slug) => ipcRenderer.invoke('tinadec:pet-remove', slug),
    // Agent avatars: the pool, the per-agent preference and the per-instance decision.
    listAvatars: () => ipcRenderer.invoke('tinadec:pet-avatars'),
    loadAvatarState: () => ipcRenderer.invoke('tinadec:pet-avatar-state'),
    setAgentAvatar: (agentId, preference) => ipcRenderer.invoke('tinadec:pet-avatar-preference', agentId, preference),
    saveInstanceAvatar: (input) => ipcRenderer.invoke('tinadec:pet-avatar-instance', input),
    releaseInstanceAvatars: (instanceKeys) => ipcRenderer.invoke('tinadec:pet-avatar-release', instanceKeys),
    onAvatarsChanged: (callback) => {
      const handler = () => callback();
      ipcRenderer.on('tinadec:pet-avatars-changed', handler);
      return () => ipcRenderer.removeListener('tinadec:pet-avatars-changed', handler);
    },
  },
  
  // --- Background File Selection API ---
  selectBackgroundFile: (type, text) => ipcRenderer.invoke('tinadec:select-background-file', type, text),

  // --- Background Image Read API (Monet color extraction) ---
  readImageAsDataUrl: (source) => ipcRenderer.invoke('tinadec:read-image-data-url', source),

  // --- Clipboard API (selection context menu) ---
  clipboardReadText: () => ipcRenderer.invoke('tinadec:clipboard-read-text'),
  clipboardWriteText: (text) => ipcRenderer.invoke('tinadec:clipboard-write-text', text),

  // --- Terminal API ---
  terminal: {
    create: (options) => ipcRenderer.invoke('terminal:create', options),
    write: (id, data) => ipcRenderer.send('terminal:write', id, data),
    resize: (id, cols, rows) => ipcRenderer.send('terminal:resize', id, cols, rows),
    destroy: (id) => ipcRenderer.send('terminal:destroy', id),
    snapshot: (id) => ipcRenderer.invoke('terminal:snapshot', id),
    getShells: () => ipcRenderer.invoke('terminal:get-shells'),
    list: () => ipcRenderer.invoke('terminal:list'),
    onData: (id, callback) => {
      const channel = `terminal:data:${id}`;
      const handler = (_e, data) => callback(data);
      ipcRenderer.on(channel, handler);
      return () => ipcRenderer.removeListener(channel, handler);
    },
    onExit: (id, callback) => {
      const channel = `terminal:exit:${id}`;
      const handler = (_e, info) => callback(info.exitCode, info.signal);
      ipcRenderer.on(channel, handler);
      return () => ipcRenderer.removeListener(channel, handler);
    },
  },

  // --- Detached Panel Window API ---
  detachPanel: (tabId, type, title, state) =>
    ipcRenderer.invoke('tinadec:detach-panel', tabId, type, title, state),
  reattachPanel: (tabId, type, title, state) =>
    ipcRenderer.invoke('tinadec:reattach-panel', tabId, type, title, state),
  closePanelWindow: (windowId) =>
    ipcRenderer.send('tinadec:close-panel-window', windowId),
  focusPanelWindow: (windowId) =>
    ipcRenderer.send('tinadec:focus-panel-window', windowId),
  getPanelWindows: () =>
    ipcRenderer.invoke('tinadec:get-panel-windows'),
  getCursorScreen: () =>
    ipcRenderer.invoke('tinadec:get-cursor-screen'),
  getMainBounds: () =>
    ipcRenderer.invoke('tinadec:get-main-bounds'),
  broadcastTheme: (theme, accentColor) =>
    ipcRenderer.send('tinadec:broadcast-theme', theme, accentColor),

  // --- Status Notification Broadcast API ---
  broadcastStatusNotification: (payload) =>
    ipcRenderer.send('tinadec:broadcast-status-notification', payload),
  onStatusNotification: (callback) => {
    const handler = (_e, payload) => callback(payload);
    ipcRenderer.on('tinadec:status-notification', handler);
    return () => ipcRenderer.removeListener('tinadec:status-notification', handler);
  },

  // --- Panel event listeners (main window side) ---
  onPanelDetached: (callback) => {
    const handler = (_e, data) => callback(data);
    ipcRenderer.on('panel:detached', handler);
    return () => ipcRenderer.removeListener('panel:detached', handler);
  },
  onPanelReattach: (callback) => {
    const handler = (_e, data) => callback(data);
    ipcRenderer.on('panel:reattach', handler);
    return () => ipcRenderer.removeListener('panel:reattach', handler);
  },
  onPanelClosed: (callback) => {
    const handler = (_e, data) => callback(data);
    ipcRenderer.on('panel:closed', handler);
    return () => ipcRenderer.removeListener('panel:closed', handler);
  },
  onPanelThemeChanged: (callback) => {
    const handler = (_e, data) => callback(data);
    ipcRenderer.on('panel:theme-changed', handler);
    return () => ipcRenderer.removeListener('panel:theme-changed', handler);
  },
});
