import { computed, readonly, ref, watch } from 'vue'
import { useHostAccess } from '@/lib/hostAccess'

export type TinadecPlatform = 'darwin' | 'win32' | 'linux' | 'web'

const platformRef = ref<TinadecPlatform>('web')
const isMacRef = ref(false)
const isElectronMacRef = ref(false)
const fullScreenRef = ref(false)
const ipcFullScreenRef = ref(false)

function normalizePlatformParam(raw: string | null): TinadecPlatform | null {
  if (!raw) return null
  const normalized = raw.trim().toLowerCase()
  if (normalized === 'darwin' || normalized === 'mac' || normalized === 'macos') return 'darwin'
  if (normalized === 'win32' || normalized === 'win' || normalized === 'windows') return 'win32'
  if (normalized === 'linux') return 'linux'
  if (normalized === 'web') return 'web'
  return null
}

function normalizeFullScreenParam(raw: string | null): boolean | null {
  if (!raw) return null
  const normalized = raw.trim().toLowerCase()
  if (normalized === 'true' || normalized === '1') return true
  if (normalized === 'false' || normalized === '0') return false
  return null
}

function readUrlPlatformOverrides(): { platform: TinadecPlatform | null; fullScreen: boolean | null } {
  if (typeof window === 'undefined' || !window.location) {
    return { platform: null, fullScreen: null }
  }
  const searchParams = new URLSearchParams(window.location.search)
  const hash = window.location.hash ?? ''
  const hashQueryIdx = hash.indexOf('?')
  const hashParams = hashQueryIdx >= 0 ? new URLSearchParams(hash.slice(hashQueryIdx + 1)) : null

  const platform =
    normalizePlatformParam(searchParams.get('platform')) ??
    (hashParams ? normalizePlatformParam(hashParams.get('platform')) : null)
  const fullScreen =
    normalizeFullScreenParam(searchParams.get('fullscreen')) ??
    (hashParams ? normalizeFullScreenParam(hashParams.get('fullscreen')) : null)

  return { platform, fullScreen }
}

function detectNavigatorMac(): boolean {
  if (typeof navigator === 'undefined') return false
  const uaDataPlatform = (navigator as Navigator & { userAgentData?: { platform?: string } }).userAgentData?.platform ?? ''
  const source = `${uaDataPlatform} ${navigator.platform ?? ''} ${navigator.userAgent ?? ''}`
  return /mac|iphone|ipad/i.test(source)
}

export function syncPlatform(): void {
  const { status: hostStatus } = useHostAccess()
  const overrides = readUrlPlatformOverrides()

  const bridge =
    typeof window !== 'undefined'
      ? (window.tinadec as (typeof window.tinadec & { isWebShim?: boolean }) | undefined)
      : undefined
  const hasRealElectronBridge = Boolean(
    bridge &&
      !bridge.isWebShim &&
      hostStatus.value?.state !== 'preview' &&
      !hostStatus.value?.webAuth,
  )

  let nextPlatform: TinadecPlatform
  let nextIsElectronMac: boolean
  let nextIsMac: boolean

  if (overrides.platform) {
    nextPlatform = overrides.platform
    nextIsElectronMac = overrides.platform === 'darwin'
    nextIsMac =
      overrides.platform === 'darwin'
        ? true
        : overrides.platform === 'web'
          ? detectNavigatorMac()
          : false
  } else if (hasRealElectronBridge) {
    const rawBridgePlatform = typeof bridge?.platform === 'string' ? bridge.platform.toLowerCase() : ''
    nextPlatform =
      rawBridgePlatform === 'darwin'
        ? 'darwin'
        : rawBridgePlatform === 'linux'
          ? 'linux'
          : 'win32'
    nextIsElectronMac = nextPlatform === 'darwin'
    nextIsMac = nextPlatform === 'darwin'
  } else {
    nextPlatform = 'web'
    nextIsElectronMac = false
    nextIsMac = detectNavigatorMac()
  }

  const nextFullScreen = overrides.fullScreen !== null ? overrides.fullScreen : ipcFullScreenRef.value

  if (platformRef.value !== nextPlatform) platformRef.value = nextPlatform
  if (isMacRef.value !== nextIsMac) isMacRef.value = nextIsMac
  if (isElectronMacRef.value !== nextIsElectronMac) isElectronMacRef.value = nextIsElectronMac
  if (fullScreenRef.value !== nextFullScreen) fullScreenRef.value = nextFullScreen

  if (typeof document !== 'undefined' && document.documentElement) {
    const root = document.documentElement
    const fullScreenAttr = nextFullScreen ? 'true' : 'false'
    if (root.dataset.platform !== nextPlatform) root.dataset.platform = nextPlatform
    if (root.dataset.fullscreen !== fullScreenAttr) root.dataset.fullscreen = fullScreenAttr
  }
}

/**
 * Initialize window-level platform & full-screen synchronization once in App.vue.
 * Returns a cleanup callback for unmounting.
 */
export function initPlatform(): () => void {
  syncPlatform()
  const { status: hostStatus } = useHostAccess()
  const stopHostWatch = watch(hostStatus, () => syncPlatform())

  let unsubscribeIpcFullScreen: (() => void) | undefined
  let disposed = false

  if (typeof window !== 'undefined') {
    const bridge = window.tinadec
    if (typeof bridge?.isFullScreen === 'function') {
      void Promise.resolve(bridge.isFullScreen())
        .then((fs) => {
          if (disposed) return
          ipcFullScreenRef.value = Boolean(fs)
          syncPlatform()
        })
        .catch(() => {})
    }
    if (typeof bridge?.onFullScreenChanged === 'function') {
      unsubscribeIpcFullScreen = bridge.onFullScreenChanged((fs) => {
        if (disposed) return
        ipcFullScreenRef.value = Boolean(fs)
        syncPlatform()
      })
    }
    window.addEventListener('popstate', syncPlatform)
    window.addEventListener('hashchange', syncPlatform)
  }

  return () => {
    disposed = true
    stopHostWatch()
    unsubscribeIpcFullScreen?.()
    if (typeof window !== 'undefined') {
      window.removeEventListener('popstate', syncPlatform)
      window.removeEventListener('hashchange', syncPlatform)
    }
  }
}

export function usePlatform() {
  syncPlatform()
  return {
    platform: readonly(platformRef),
    isMac: computed(() => isMacRef.value),
    isElectronMac: computed(() => isElectronMacRef.value),
    isFullScreen: computed(() => fullScreenRef.value),
    modKey: computed(() => (isMacRef.value ? '⌘' : 'Ctrl+')),
    syncPlatform,
  }
}

export function __resetPlatformForTests(): void {
  ipcFullScreenRef.value = false
  syncPlatform()
}
