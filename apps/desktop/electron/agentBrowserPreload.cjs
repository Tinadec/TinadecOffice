/**
 * The agent browser shell's only bridge. It is loaded only into the local shell window;
 * the remote page lives in a separate WebContentsView with no preload at all.
 */
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('tinadecBrowser', {
  getState: () => ipcRenderer.invoke('browserShell:state'),
  command: (payload) => ipcRenderer.invoke('browserShell:command', payload),
  onState: (callback) => {
    const handler = (_event, state) => callback(state);
    ipcRenderer.on('browserShell:state-changed', handler);
    return () => ipcRenderer.removeListener('browserShell:state-changed', handler);
  },
});
