/**
 * webShim.ts — Browser implementation of the `window.tinadec` global.
 *
 * Installed BEFORE the desktop renderer's main.ts so that every call site
 * sees a valid API object. Web mode connects to the same-origin Gateway BFF
 * using 3-tier admission (loopback auto-login or mandatory remote auth) and
 * delegates host folder picking to ServerDirectoryPickerModal.
 */
import i18n from '@/i18n'
import { encodeMediaPathToken } from '@/composables/useBackground'
import {
  openServerDirectoryPicker,
  probeWebAuthStatus,
  resolveWebGatewayBase,
  type HostConnectionStatus,
} from '@/lib/hostConnection'

const noop = () => {}

function rejectNotAvailable(reasonKey: string): Promise<never> {
  return Promise.reject(new Error(i18n.global.t(reasonKey)))
}

async function resolveWebHostStatus(): Promise<HostConnectionStatus> {
  const probed = await probeWebAuthStatus(window.location.origin)
  if (probed) return probed
  return {
    state: 'unavailable',
    managed: true,
    error: {
      code: 'host_unavailable',
      message: i18n.global.t('host.notReady'),
    },
  }
}

function webAppConfig() {
  return {
    gateway_url: window.location.origin,
    source: 'environment' as const,
    managed: true,
    debug_studio_enabled: localStorage.getItem('tinadec.web.debug-studio-enabled') === 'true',
    close_to_tray: false,
    companion_mode_enabled: false,
  }
}

;(window as any).tinadec = {
  isWebShim: true,

  // -- Gateway / config / host admission --
  gatewayUrl: () => window.location.origin,
  getHostStatus: () => resolveWebHostStatus(),
  retryHostConnection: () => resolveWebHostStatus(),
  onHostStatusChanged: () => noop,

  getAppConfig: () => Promise.resolve(webAppConfig()),

  saveGatewayUrl: () => rejectNotAvailable('web.gatewayManaged'),
  resetGatewayUrl: () => rejectNotAvailable('web.gatewayManaged'),

  saveDebugStudioEnabled: (enabled: boolean) => {
    localStorage.setItem('tinadec.web.debug-studio-enabled', String(enabled))
    return Promise.resolve({ debug_studio_enabled: enabled })
  },
  onDebugStudioEnabledChanged: () => noop,
  saveCloseToTray: (enabled: boolean) => Promise.resolve({ close_to_tray: enabled }),
  onCloseToTrayChanged: () => noop,
  saveLaunchAtLogin: (enabled: boolean) => Promise.resolve({ launch_at_login: enabled }),
  onLaunchAtLoginChanged: () => noop,
  saveCompanionSpeakReplies: (enabled: boolean) => Promise.resolve({ companion_speak_replies: enabled }),
  onCompanionSpeakRepliesChanged: () => noop,
  saveCompanionMode: (enabled: boolean) => Promise.resolve({ companion_mode_enabled: enabled }),

  setInterfaceLocale: noop,

  restartApp: () => {
    window.location.reload()
    return Promise.resolve()
  },

  // -- Window chrome (hidden in web via !isElectron; kept safe no-ops for callers) --
  minimizeWindow: noop,
  maximizeWindow: noop,
  closeWindow: noop,

  // -- Server-side Directory Pickers (backed by GET /api/v1/fs/browse) --
  openProjectDialog: async () => {
    const picked = await openServerDirectoryPicker({
      mode: 'directory',
      multiple: false,
      title: i18n.global.t('webDirPicker.selectProjectTitle'),
    })
    return picked[0] ?? null
  },

  selectWorkspaceFolders: (title?: string) =>
    openServerDirectoryPicker({
      mode: 'directory',
      multiple: true,
      title: title || i18n.global.t('webDirPicker.selectWorkspaceTitle'),
    }),

  selectCompanionAvatarDirectory: async (title?: string) => {
    const picked = await openServerDirectoryPicker({
      mode: 'directory',
      multiple: false,
      title: title || i18n.global.t('webDirPicker.selectAvatarTitle'),
    })
    return picked[0] ?? null
  },

  pathForFile: () => '',
  importWorkspaceFiles: () => rejectNotAvailable('web.filesDropHint'),
  selectBackgroundFile: async (
    type: 'image' | 'video' | string,
    labels?: { title?: string; image?: string; video?: string; all?: string },
  ) => {
    const acceptExtensions = type === 'video'
      ? ['.mp4', '.webm', '.mov', '.m4v', '.ogg']
      : ['.png', '.jpg', '.jpeg', '.webp', '.gif', '.avif', '.bmp']
    const picked = await openServerDirectoryPicker({
      mode: 'file',
      multiple: false,
      acceptExtensions,
      title: labels?.title || i18n.global.t('settings.bgPickTitle'),
    })
    const chosen = picked[0]
    if (!chosen) return null
    if (/^https?:\/\//i.test(chosen) || chosen.startsWith('data:') || chosen.startsWith('blob:')) {
      return chosen
    }
    return `${resolveWebGatewayBase()}/api/v1/fs/media?token=${encodeMediaPathToken(chosen.replace(/\\/g, '/'))}`
  },

  // -- Web Auth state bridge --
  openLogin: async () => null,
  logout: async () => {
    await fetch(`${window.location.origin}/api/v1/web-auth/logout`, {
      method: 'POST',
      credentials: 'include',
    }).catch(() => undefined)
    window.location.reload()
    return { loggedIn: false }
  },
  getAuthState: async () => {
    const status = await probeWebAuthStatus(window.location.origin)
    const principal = status?.webAuth?.principal
    if (status?.state === 'ready' && principal) {
      return {
        loggedIn: true,
        nickname: principal.display_name || principal.user_id,
        avatar_url: principal.avatar_url || '',
      }
    }
    return { loggedIn: false }
  },
  updateProfile: () => rejectNotAvailable('web.profileDesktopOnly'),

  // -- Debug Studio --
  openDebugStudio: () => {
    window.open('#/debug-studio', '_blank')
    return Promise.resolve(true)
  },

  // -- Pets (Electron-only transparent OS window; hidden/no-op in Web) --
  pets: {
    list: () => Promise.resolve([]),
    listDownloaded: () => Promise.resolve([]),
    fetchCatalog: () => Promise.resolve([]),
    getCurrent: () => Promise.resolve(null),
    getWindowPet: () => Promise.resolve(null),
    create: () => rejectNotAvailable('web.petsUnavailable'),
    close: () => rejectNotAvailable('web.petsUnavailable'),
    setCurrentBounds: () => rejectNotAvailable('web.petsUnavailable'),
    setCurrentClickThrough: () => rejectNotAvailable('web.petsUnavailable'),
    closeCurrent: () => rejectNotAvailable('web.petsUnavailable'),
    onChanged: () => noop,
    download: () => rejectNotAvailable('web.petsUnavailable'),
    setEnabled: () => rejectNotAvailable('web.petsUnavailable'),
    openFolder: () => rejectNotAvailable('web.petsUnavailable'),
    remove: () => rejectNotAvailable('web.petsUnavailable'),
  },

  // -- Detachable panels (in Web mode, opens a new browser tab) --
  detachPanel: (tabId: string, type: string, title: string, state: Record<string, unknown>) => {
    const params = new URLSearchParams({
      tabId,
      type,
      title,
      state: JSON.stringify(state ?? {}),
    })
    window.open(`#/panel?${params.toString()}`, '_blank')
    return Promise.resolve(null)
  },
  reattachPanel: () => rejectNotAvailable('web.panelsUnavailable'),
  closePanelWindow: noop,
  focusPanelWindow: noop,
  getPanelWindows: () => Promise.resolve([]),
  getCursorScreen: () => Promise.resolve({ x: 0, y: 0 }),
  getMainBounds: () => Promise.resolve(null),

  broadcastTheme: noop,
  clipboardReadText: undefined,
  clipboardWriteText: undefined,
  broadcastStatusNotification: noop,
  onStatusNotification: () => noop,
  onPanelDetached: () => noop,
  onPanelReattach: () => noop,
  onPanelClosed: () => noop,
  onPanelThemeChanged: () => noop,

  layout: {
    load: () => Promise.resolve(null),
    save: () => Promise.resolve({ ok: false }),
  },
}
