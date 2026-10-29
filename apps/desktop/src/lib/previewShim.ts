// previewShim.ts — Minimal `window.tinadec` for bare-browser dev previews.
//
// When the renderer runs without the Electron preload (e.g. `vite` alone, the
// MCP/vue preview pane), `window.tinadec` is undefined and page components that
// touch it at setup time (Settings pets, Home project dialog) crash. This shim
// provides a safe no-op surface so the UI renders and IPC calls degrade.
//
// It is installed in main.ts ONLY when `window.tinadec` is already absent. In the
// real Electron app the preload owns `window.tinadec` and this shim is skipped.

import { setHostAccessStatus } from './hostAccess'
import i18n from '@/i18n'
const noop = () => {}

function rejectNotAvailable(reason: string): Promise<never> {
  return Promise.reject(new Error(reason))
}

export function installPreviewShimIfNeeded(): void {
  if (typeof window === 'undefined') return
  const w = window as unknown as { tinadec?: unknown }
  if (w.tinadec) return

  // Preview-only UI state never writes the real user's desktop.toml.
  const previewDebugKey = 'tinadec.preview.debug-studio-enabled'
  const previewCloseToTrayKey = 'tinadec.preview.close-to-tray'
  const previewCompanionModeKey = 'tinadec.preview.companion-mode-enabled'
  function previewDebugEnabled(): boolean { return localStorage.getItem(previewDebugKey) === 'true' }
  function previewCloseToTray(): boolean { return localStorage.getItem(previewCloseToTrayKey) === 'true' }
  function previewCompanionMode(): boolean { return localStorage.getItem(previewCompanionModeKey) === 'true' }

  // A browser tab cannot move or resize an OS window, but the page still needs a consistent
  // shape/state to exercise: keep it in memory and notify the same way the main process does.
  let companionState = { open: true, expanded: false, side: 'right' as 'left' | 'right' }
  const companionListeners = new Set<(state: typeof companionState) => void>()
  function updateCompanion(next: Partial<typeof companionState>): typeof companionState {
    companionState = { ...companionState, ...next }
    for (const listener of companionListeners) listener({ ...companionState })
    return { ...companionState }
  }

  const hostStatus = { state: 'preview' as const, managed: false, error: { code: 'desktop_host_required', message: i18n.global.t('host.previewShim') } }
  setHostAccessStatus(hostStatus)
  w.tinadec = {
    getHostStatus: () => Promise.resolve(hostStatus),
    onHostStatusChanged: () => noop,
    retryHostConnection: () => Promise.resolve(hostStatus),
    storageAction: () => rejectNotAvailable(hostStatus.error.message),
    gatewayUrl: () => 'http://127.0.0.1:48730',
    getAppConfig: () => Promise.resolve({ gateway_url: 'http://127.0.0.1:48730', source: 'environment', managed: true, debug_studio_enabled: previewDebugEnabled(), close_to_tray: previewCloseToTray(), companion_mode_enabled: previewCompanionMode() }),
    saveDebugStudioEnabled: (enabled: boolean) => {
      localStorage.setItem(previewDebugKey, String(enabled))
      return Promise.resolve({ debug_studio_enabled: enabled })
    },
    onDebugStudioEnabledChanged: (callback: () => void) => {
      const listener = (event: StorageEvent) => { if (event.key === previewDebugKey) callback() }
      window.addEventListener('storage', listener)
      return () => window.removeEventListener('storage', listener)
    },
    // A browser tab has no tray, so the preference is stored locally instead of asking a main process.
    saveCloseToTray: (enabled: boolean) => {
      localStorage.setItem(previewCloseToTrayKey, String(enabled))
      return Promise.resolve({ close_to_tray: enabled })
    },
    onCloseToTrayChanged: () => noop,
    saveCompanionMode: (enabled: boolean) => {
      localStorage.setItem(previewCompanionModeKey, String(enabled))
      return Promise.resolve({ companion_mode_enabled: enabled })
    },
    companion: {
      // The browser preview has no main-process proxy and no host credential: say so, never fake data.
      api: () => Promise.resolve({ status: 503, body: { code: 'desktop_host_required', message: i18n.global.t('host.previewShim') } }),
      getState: () => Promise.resolve({ ...companionState }),
      expand: () => Promise.resolve(updateCompanion({ expanded: true })),
      collapse: () => Promise.resolve(updateCompanion({ expanded: false })),
      moveTo: () => Promise.resolve({ ...companionState }),
      setSide: (side: 'left' | 'right') => Promise.resolve(updateCompanion({ side })),
      onStateChanged: (callback: (state: typeof companionState) => void) => {
        companionListeners.add(callback)
        return () => { companionListeners.delete(callback) }
      },
    },
    setInterfaceLocale: noop,
    saveGatewayUrl: () => rejectNotAvailable('preview'),
    resetGatewayUrl: () => rejectNotAvailable('preview'),
    restartApp: () => { window.location.reload(); return Promise.resolve() },
    selectWorkspaceFolders: () => rejectNotAvailable(hostStatus.error.message),
    // A browser tab cannot read a dropped file's real path and has no main process to copy with.
    // The shim mirrors the preload's declared surface so a preview never has to rely on every
    // consumer remembering optional chaining: an unresolved path is the empty string (the drop
    // resolves to nothing) and a copy request is refused with the same sentence the host gate
    // uses, which is what a host without a main process means.
    pathForFile: () => '',
    importWorkspaceFiles: () => rejectNotAvailable(hostStatus.error.message),
    minimizeWindow: noop,
    maximizeWindow: noop,
    closeWindow: noop,
    openDebugStudio: () => {
      if (!previewDebugEnabled()) return Promise.resolve(false)
      window.location.hash = '/debug-studio'
      return Promise.resolve(true)
    },
    openLogin: () => Promise.resolve(null),
    logout: () => Promise.resolve({ loggedIn: false }),
    getAuthState: () => Promise.resolve({ loggedIn: false }),
    updateProfile: () => Promise.reject(new Error('Profile changes require the desktop shell.')),
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

    // TinadecUIE layout (in-memory no-op in preview; Electron IPC in real app).
    layout: {
      load: () => Promise.resolve(null),
      save: () => Promise.resolve({ ok: false }),
    },
  }
}
