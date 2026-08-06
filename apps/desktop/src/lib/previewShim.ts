// previewShim.ts — Minimal `window.tinadec` for bare-browser dev previews.
//
// When the renderer runs without the Electron preload (e.g. `vite` alone, the
// MCP/vue preview pane), `window.tinadec` is undefined and page components that
// touch it at setup time (Settings pets, Home project dialog) crash. This shim
// provides a safe no-op surface so the UI renders and IPC calls degrade.
//
// It is installed in main.ts ONLY when `window.tinadec` is already absent. In the
// real Electron app the preload owns `window.tinadec` and this shim is skipped.

const noop = () => {}

function rejectNotAvailable(reason: string): Promise<never> {
  return Promise.reject(new Error(reason))
}

export function installPreviewShimIfNeeded(): void {
  if (typeof window === 'undefined') return
  const w = window as unknown as { tinadec?: unknown }
  if (w.tinadec) return

  w.tinadec = {
    gatewayUrl: () => 'http://127.0.0.1:48730',
    getAppConfig: () => Promise.resolve({ gateway_url: 'http://127.0.0.1:48730', source: 'environment', managed: true }),
    saveGatewayUrl: () => rejectNotAvailable('preview'),
    resetGatewayUrl: () => rejectNotAvailable('preview'),
    restartApp: () => { window.location.reload(); return Promise.resolve() },
    openProjectDialog: () => Promise.resolve(null),
    minimizeWindow: noop,
    maximizeWindow: noop,
    closeWindow: noop,
    openDebugStudio: () => Promise.resolve(false),
    selectBackgroundFile: () => Promise.resolve(null),

    // Pets (no-op in preview).
    pets: {
      list: () => Promise.resolve([]),
      listDownloaded: () => Promise.resolve([]),
      fetchCatalog: () => Promise.resolve([]),
      getCurrent: () => Promise.resolve(null),
      getWindowPet: () => Promise.resolve(null),
      create: () => rejectNotAvailable('preview'),
      close: () => rejectNotAvailable('preview'),
      setCurrentBounds: () => rejectNotAvailable('preview'),
      setCurrentClickThrough: () => rejectNotAvailable('preview'),
      closeCurrent: () => rejectNotAvailable('preview'),
      onChanged: () => noop,
      download: () => rejectNotAvailable('preview'),
      setEnabled: () => rejectNotAvailable('preview'),
      openFolder: () => rejectNotAvailable('preview'),
      remove: () => rejectNotAvailable('preview'),
    },

    // Detach (no-op in preview).
    detachPanel: () => Promise.resolve(null),
    reattachPanel: () => rejectNotAvailable('preview'),
    closePanelWindow: noop,
    focusPanelWindow: noop,
    getPanelWindows: () => Promise.resolve([]),
    getCursorScreen: () => Promise.resolve({ x: 0, y: 0 }),
    getMainBounds: () => Promise.resolve(null),
    broadcastTheme: noop,
    broadcastStatusNotification: noop,
    onStatusNotification: () => noop,
    onPanelDetached: () => noop,
    onPanelReattach: () => noop,
    onPanelClosed: () => noop,
    onPanelThemeChanged: () => noop,

    // Workbench layout (in-memory no-op in preview; Electron IPC in real app).
    layout: {
      load: () => Promise.resolve(null),
      save: () => Promise.resolve({ ok: false }),
    },
  }
}
