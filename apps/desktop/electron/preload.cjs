const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('tinadec', {
  gatewayUrl: () => process.env.TINADEC_GATEWAY_URL ?? 'http://127.0.0.1:48730',
  openProjectDialog: () => ipcRenderer.invoke('tinadec:open-project'),
  minimizeWindow: () => ipcRenderer.send('tinadec:minimize'),
  maximizeWindow: () => ipcRenderer.send('tinadec:maximize'),
  closeWindow: () => ipcRenderer.send('tinadec:close'),
  openDebugStudio: () => ipcRenderer.invoke('tinadec:open-debug-studio'),

  // --- Local Pet Window API ---
  pets: {
    create: (petId) => ipcRenderer.invoke('tinadec:pet-create', petId),
    close: (instanceId) => ipcRenderer.invoke('tinadec:pet-close', instanceId),
    list: () => ipcRenderer.invoke('tinadec:pet-list'),
    getWindowPet: (instanceId) => ipcRenderer.invoke('tinadec:pet-window-pet', instanceId),
    fetchCatalog: (force = false) => ipcRenderer.invoke('tinadec:pet-catalog', force),
    download: (slug) => ipcRenderer.invoke('tinadec:pet-download', slug),
    listDownloaded: () => ipcRenderer.invoke('tinadec:pet-downloaded'),
    setEnabled: (slug, enabled) => ipcRenderer.invoke('tinadec:pet-enabled', slug, enabled),
    openFolder: (slug) => ipcRenderer.invoke('tinadec:pet-open-folder', slug),
    remove: (slug) => ipcRenderer.invoke('tinadec:pet-remove', slug),
  },
  
  // --- Background File Selection API ---
  selectBackgroundFile: (type) => ipcRenderer.invoke('tinadec:select-background-file', type),

  // --- Terminal API ---
  terminal: {
    create: (options) => ipcRenderer.invoke('terminal:create', options),
    write: (id, data) => ipcRenderer.send('terminal:write', id, data),
    resize: (id, cols, rows) => ipcRenderer.send('terminal:resize', id, cols, rows),
    destroy: (id) => ipcRenderer.send('terminal:destroy', id),
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
