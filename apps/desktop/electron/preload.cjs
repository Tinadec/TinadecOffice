const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('tinadec', {
  gatewayUrl: () => process.env.TINADEC_GATEWAY_URL ?? 'http://127.0.0.1:48730',
  openProjectDialog: () => ipcRenderer.invoke('tinadec:open-project'),
  minimizeWindow: () => ipcRenderer.send('tinadec:minimize'),
  maximizeWindow: () => ipcRenderer.send('tinadec:maximize'),
  closeWindow: () => ipcRenderer.send('tinadec:close'),
  openDebugStudio: () => ipcRenderer.invoke('tinadec:open-debug-studio'),

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
