import i18n from '@/i18n'

export type WebAuthProviderId = 'local_password' | 'official_oidc' | 'cloudflare_access'

export interface WebAuthPrincipalDto {
  user_id: string
  tenant_id: string
  roles: string[]
  auth_method: string
  display_name?: string
  email?: string
  avatar_url?: string
}

export interface WebAuthStatusSnapshot {
  authenticated: boolean
  auth_required: boolean
  tier: 'electron' | 'loopback' | 'remote'
  principal: WebAuthPrincipalDto | null
  available_providers: WebAuthProviderId[]
  allow_loopback_auto_login: boolean
  password_configured: boolean
  oidc_enabled: boolean
  cloudflare_access_enabled: boolean
  trust_proxy: 'false' | 'true' | 'cloudflare'
}

/** Public host readiness snapshot. Credentials never cross this boundary. */
export interface HostConnectionStatus {
  state: 'checking' | 'ready' | 'unavailable' | 'rejected' | 'preview' | 'restart_required' | 'auth_required'
  managed: boolean
  checked_at?: string
  error?: { code: string; message: string }
  webAuth?: WebAuthStatusSnapshot
}

export interface HostStatusBridge {
  isWebShim?: boolean
  gatewayUrl?: () => string
  getHostStatus?: () => Promise<HostConnectionStatus>
  retryHostConnection?: () => Promise<HostConnectionStatus>
}

export interface ServerDirectoryPickerOptions {
  title?: string
  multiple?: boolean
  initialPath?: string
}

export type ServerDirectoryPickerHandler = (options: ServerDirectoryPickerOptions) => Promise<string[]>

let directoryPickerHandler: ServerDirectoryPickerHandler | null = null

export function registerServerDirectoryPicker(handler: ServerDirectoryPickerHandler | null): void {
  directoryPickerHandler = handler
}

export async function openServerDirectoryPicker(options: ServerDirectoryPickerOptions = {}): Promise<string[]> {
  if (!directoryPickerHandler) return []
  return directoryPickerHandler(options)
}

export const WEB_PROXY_UPLOAD_LIMIT_BYTES = 100 * 1024 * 1024

export function isElectronHost(): boolean {
  if (typeof window === 'undefined') return false
  const bridge = (window as unknown as { tinadec?: HostStatusBridge }).tinadec
  return Boolean(bridge && !bridge.isWebShim)
}

/** Resolve a protocol-aware WebSocket URL (ws:// or wss://) from an HTTP(S) base URL. */
export function toWebSocketUrl(baseHttpUrl: string, path: string): string {
  const base = (baseHttpUrl || (typeof window !== 'undefined' ? window.location.origin : 'http://127.0.0.1:48730')).trim()
  const url = new URL(path, base)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return url.toString()
}

export function getWebUploadLimitWarning(fileSizeBytes: number): string | null {
  if (isElectronHost() && typeof window !== 'undefined' && !(window as unknown as { tinadec?: HostStatusBridge }).tinadec?.isWebShim) {
    return null
  }
  if (fileSizeBytes > WEB_PROXY_UPLOAD_LIMIT_BYTES) {
    return i18n.global.t('webAuth.uploadLimitWarning')
  }
  return null
}

function restartRequired(): HostConnectionStatus {
  return { state: 'restart_required', managed: true, error: {
    code: 'desktop_restart_required',
    message: i18n.global.t('host.restartRequired'),
  } }
}

/** A missing local IPC contract is not a backend network or identity failure. */
export function hostBridgeFailure(error: unknown): HostConnectionStatus {
  const message = error instanceof Error ? error.message : typeof error === 'string' ? error : ''
  if (/No handler registered for ['"]tinadec:host-(?:status|retry)['"](?:\s|$)/.test(message)) return restartRequired()
  return { state: 'unavailable', managed: true, error: {
    code: 'host_bridge_unavailable', message: i18n.global.t('host.bridgeUnavailable'),
  } }
}

/** The main process names host failures by code; their wording is this window's language. */
const HOST_ERROR_KEYS: Record<string, string> = {
  host_identity_rejected: 'host.identityRejected',
  host_identity_unavailable: 'host.identityUnavailable',
  host_connection_stopped: 'host.connectionStopped',
  web_auth_required: 'host.authRequiredReason',
}
export function localizeHostStatus(status: HostConnectionStatus): HostConnectionStatus {
  const key = status.error ? HOST_ERROR_KEYS[status.error.code] : undefined
  return key && status.error ? { ...status, error: { ...status.error, message: i18n.global.t(key) } } : status
}

export function resolveWebGatewayBase(): string {
  if (typeof window !== 'undefined' && window.location?.protocol?.startsWith('http')) {
    return window.location.origin
  }
  if (typeof window !== 'undefined') {
    const bridge = (window as unknown as { tinadec?: HostStatusBridge }).tinadec
    if (typeof bridge?.gatewayUrl === 'function') return bridge.gatewayUrl()
  }
  return 'http://127.0.0.1:48730'
}

function upgradeBrowserPreviewBridge(): void {
  if (typeof window === 'undefined') return
  const tinadec = (window as unknown as { tinadec?: Record<string, unknown> }).tinadec
  if (!tinadec || tinadec.isWebShim) return
  tinadec.openProjectDialog = async () => {
    const picked = await openServerDirectoryPicker({
      multiple: false,
      title: i18n.global.t('webDirPicker.selectProjectTitle'),
    })
    return picked[0] ?? null
  }
  tinadec.selectWorkspaceFolders = (title?: string) =>
    openServerDirectoryPicker({ multiple: true, title: title || i18n.global.t('webDirPicker.selectWorkspaceTitle') })
  tinadec.selectCompanionAvatarDirectory = async (title?: string) => {
    const picked = await openServerDirectoryPicker({
      multiple: false,
      title: title || i18n.global.t('webDirPicker.selectAvatarTitle'),
    })
    return picked[0] ?? null
  }
  tinadec.detachPanel = (tabId: string, type: string, title: string, state: Record<string, unknown>) => {
    const params = new URLSearchParams({
      tabId,
      type,
      title,
      state: JSON.stringify(state ?? {}),
    })
    window.open(`#/panel?${params.toString()}`, '_blank')
    return Promise.resolve(null)
  }
}

export async function probeWebAuthStatus(baseUrl?: string): Promise<HostConnectionStatus | null> {
  if (typeof window === 'undefined' || typeof fetch !== 'function') return null
  if (!window.location?.protocol?.startsWith('http') && !baseUrl) return null
  const origin = baseUrl ?? resolveWebGatewayBase()
  try {
    const response = await fetch(`${origin}/api/v1/web-auth/status`, {
      credentials: 'include',
      headers: { accept: 'application/json' },
    })
    if (!response.ok) return null
    const data = (await response.json()) as WebAuthStatusSnapshot
    if (typeof data?.authenticated !== 'boolean') return null
    if (data.authenticated) {
      upgradeBrowserPreviewBridge()
      return {
        state: 'ready',
        managed: true,
        checked_at: new Date().toISOString(),
        webAuth: data,
      }
    }
    return {
      state: 'auth_required',
      managed: true,
      checked_at: new Date().toISOString(),
      webAuth: data,
      error: {
        code: 'web_auth_required',
        message: i18n.global.t('host.authRequiredReason'),
      },
    }
  } catch {
    return null
  }
}

/** Missing methods on an existing preload must fail closed; a bare browser probes Gateway web-auth. */
export async function readHostStatus(bridge?: HostStatusBridge): Promise<HostConnectionStatus> {
  if (!bridge) {
    const webStatus = await probeWebAuthStatus()
    return webStatus ?? { state: 'preview', managed: false }
  }
  if (typeof bridge.getHostStatus !== 'function') return restartRequired()
  try {
    const initial = localizeHostStatus(await bridge.getHostStatus())
    // When running in a regular browser via Vite dev (previewShim) or webShim, probe Gateway web-auth.
    if (!bridge.isWebShim && initial.state === 'preview') {
      const webStatus = await probeWebAuthStatus()
      if (webStatus) return webStatus
    }
    return initial
  } catch (error) {
    return hostBridgeFailure(error)
  }
}

export async function retryHostStatus(bridge?: HostStatusBridge): Promise<HostConnectionStatus> {
  if (!bridge) {
    const webStatus = await probeWebAuthStatus()
    return webStatus ?? { state: 'preview', managed: false }
  }
  if (typeof bridge.retryHostConnection !== 'function') return restartRequired()
  try {
    const initial = localizeHostStatus(await bridge.retryHostConnection())
    if (!bridge.isWebShim && initial.state === 'preview') {
      const webStatus = await probeWebAuthStatus()
      if (webStatus) return webStatus
    }
    return initial
  } catch (error) {
    return hostBridgeFailure(error)
  }
}
