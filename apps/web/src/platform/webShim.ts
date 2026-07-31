/**
 * webShim.ts — Browser implementation of the `window.tinadec` global.
 *
 * Installed BEFORE the desktop renderer's main.ts so that every call site
 * sees a valid API object. All Electron-only capabilities degrade to
 * safe no-ops or empty values.
 *
 * DO NOT modify this file to add new platform features — keep all platform
 * difference concentrated here.
 */

const noop = () => {}

function rejectNotAvailable(reason: string): Promise<never> {
  return Promise.reject(new Error(reason))
}

// ponytail: terminal is intentionally left undefined so useTerminal.ts
// isTerminalAvailable() returns false and the UI shows terminal-unavailable.
// If terminal is ever defined here, every call site that does `window.tinadec.terminal.*`
// without a guard would execute — that is a larger surface than just hiding the UI.

;(window as any).tinadec = {
  // -- Gateway / config --
  gatewayUrl: () => window.location.origin,

  getAppConfig: () =>
    Promise.resolve({
      gateway_url: window.location.origin,
      source: 'environment' as const,
      managed: true,
    }),

  saveGatewayUrl: () =>
    rejectNotAvailable('Gateway URL is managed by the web deployment.'),

  resetGatewayUrl: () =>
    rejectNotAvailable('Gateway URL is managed by the web deployment.'),

  restartApp: () => {
    window.location.reload()
    return Promise.resolve()
  },

  // -- Window chrome (no-ops in browser) --
  minimizeWindow: noop,
  maximizeWindow: noop,
  closeWindow: noop,

  // -- Project dialog --
  openProjectDialog: () => Promise.resolve(null),

  // -- Debug Studio --
  openDebugStudio: () => {
    window.open('#/debug-studio', '_blank')
    return Promise.resolve(true)
  },

  // -- Pets (local-only transparent OS windows, not available in web) --
  pets: {
    list: () => Promise.resolve([]),
    listDownloaded: () => Promise.resolve([]),
    fetchCatalog: () => Promise.resolve([]),
    getCurrent: () => Promise.resolve(null),
    getWindowPet: () => Promise.resolve(null),
    create: () => rejectNotAvailable('Pets are not available in the web version.'),
    close: () => rejectNotAvailable('Pets are not available in the web version.'),
    setCurrentBounds: () => rejectNotAvailable('Pets are not available in the web version.'),
    setCurrentClickThrough: () => rejectNotAvailable('Pets are not available in the web version.'),
    closeCurrent: () => rejectNotAvailable('Pets are not available in the web version.'),
    onChanged: () => noop,
    download: () => rejectNotAvailable('Pets are not available in the web version.'),
    setEnabled: () => rejectNotAvailable('Pets are not available in the web version.'),
    openFolder: () => rejectNotAvailable('Pets are not available in the web version.'),
    remove: () => rejectNotAvailable('Pets are not available in the web version.'),
  },

  // -- Terminal (left undefined — see note above) --

  // -- Detachable panels (no-ops in browser) --
  detachPanel: () => Promise.resolve(null),
  reattachPanel: () => rejectNotAvailable('Panel windows are not available in the web version.'),
  closePanelWindow: noop,
  focusPanelWindow: noop,
  getPanelWindows: () => Promise.resolve([]),
  getCursorScreen: () => Promise.resolve({ x: 0, y: 0 }),
  getMainBounds: () => Promise.resolve(null),

  // -- Theme broadcast (no other windows to notify) --
  broadcastTheme: noop,

  // -- Status notification broadcast (no-op for stage 1) --
  broadcastStatusNotification: noop,
  onStatusNotification: () => noop,

  // -- Panel lifecycle listeners (must return unsubscribe function) --
  onPanelDetached: () => noop,
  onPanelReattach: () => noop,
  onPanelClosed: () => noop,
  onPanelThemeChanged: () => noop,
}
