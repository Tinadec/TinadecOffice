import { shallowRef } from 'vue'
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
  isPreviewShim?: boolean
  gatewayUrl?: () => string
  getHostStatus?: () => Promise<HostConnectionStatus>
  retryHostConnection?: () => Promise<HostConnectionStatus>
}

export interface ServerDirectoryPickerOptions {
  title?: string
  mode?: 'directory' | 'file'
  multiple?: boolean
  acceptExtensions?: string[]
  initialPath?: string
  nativeFallback?: () => Promise<string[]>
}

export interface PickHostFilesOptions {
  title?: string
  multiple?: boolean
  acceptExtensions?: string[]
  initialPath?: string
  nativeFallback?: () => Promise<string[]>
}

export type ServerDirectoryPickerHandler = (options: ServerDirectoryPickerOptions) => Promise<string[]>

const directoryPickerHandler = shallowRef<ServerDirectoryPickerHandler | null>(null)
const bridgeWrappedFns = new WeakSet<Function>()

function getWindowTinadec(): (HostStatusBridge & Record<string, unknown>) | undefined {
  if (typeof window === 'undefined') return undefined
  return (window as unknown as { tinadec?: HostStatusBridge & Record<string, unknown> }).tinadec
}

function encodeWebMediaToken(filePath: string): string {
  const bytes = new TextEncoder().encode(filePath)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function registerServerDirectoryPicker(handler: ServerDirectoryPickerHandler | null): void {
  directoryPickerHandler.value = handler
}

export function hasServerDirectoryPicker(): boolean {
  return directoryPickerHandler.value !== null
}

export async function openServerDirectoryPicker(options: ServerDirectoryPickerOptions = {}): Promise<string[]> {
  if (!directoryPickerHandler.value) return []
  return directoryPickerHandler.value(options)
}

export const WEB_PROXY_UPLOAD_LIMIT_BYTES = 100 * 1024 * 1024

export function isElectronHost(): boolean {
  if (typeof window === 'undefined') return false
  const bridge = getWindowTinadec()
  return Boolean(bridge && !bridge.isWebShim && !bridge.isPreviewShim)
}

/** True only when `fn` is a real Electron preload native dialog (never webShim/previewShim/wrapped). */
export function isNativeElectronDialogFn(fn: unknown): fn is (...args: any[]) => Promise<any> {
  if (typeof fn !== 'function') return false
  const bridge = getWindowTinadec()
  if (!bridge || !isElectronHost() || bridge.isPreviewShim || bridge.isWebShim) return false
  return !bridgeWrappedFns.has(fn)
}

export function canPickHostDirectories(): boolean {
  if (hasServerDirectoryPicker()) return true
  const bridge = getWindowTinadec()
  if (!bridge || bridge.isPreviewShim) return false
  return typeof bridge.selectWorkspaceFolders === 'function'
    || typeof bridge.selectCompanionAvatarDirectory === 'function'
}

export async function pickWorkspaceFolders(
  title?: string,
  options?: { multiple?: boolean; initialPath?: string },
): Promise<string[]> {
  const resolvedTitle = title || i18n.global.t('webDirPicker.selectWorkspaceTitle')
  const bridge = getWindowTinadec()
  const rawNative = bridge?.selectWorkspaceFolders
  const nativeFallback = isNativeElectronDialogFn(rawNative)
    ? async () => {
        const res = await (rawNative as (t?: string) => Promise<string[] | null | undefined>)(resolvedTitle)
        return Array.isArray(res) ? res.filter((item): item is string => typeof item === 'string' && item.length > 0) : []
      }
    : undefined

  if (hasServerDirectoryPicker()) {
    return openServerDirectoryPicker({
      title: resolvedTitle,
      mode: 'directory',
      multiple: options?.multiple ?? true,
      initialPath: options?.initialPath,
      nativeFallback,
    })
  }

  if (typeof rawNative === 'function' && !bridge?.isPreviewShim) {
    const res = await (rawNative as (t?: string) => Promise<string[] | null | undefined>)(resolvedTitle)
    return Array.isArray(res) ? res : []
  }
  return []
}

export async function pickProjectDirectory(
  title?: string,
  options?: { initialPath?: string },
): Promise<string | null> {
  const resolvedTitle = title || i18n.global.t('webDirPicker.selectProjectTitle')
  const bridge = getWindowTinadec()
  const rawOpenProject = bridge?.openProjectDialog
  const rawSelectFolders = bridge?.selectWorkspaceFolders
  const nativeFallback = isNativeElectronDialogFn(rawOpenProject)
    ? async () => {
        const res = await (rawOpenProject as () => Promise<string | null | undefined>)()
        return typeof res === 'string' && res.length > 0 ? [res] : []
      }
    : isNativeElectronDialogFn(rawSelectFolders)
      ? async () => {
          const res = await (rawSelectFolders as (t?: string) => Promise<string[] | null | undefined>)(resolvedTitle)
          const first = Array.isArray(res) ? res.find((item): item is string => typeof item === 'string' && item.length > 0) : undefined
          return first ? [first] : []
        }
      : undefined

  if (hasServerDirectoryPicker()) {
    const picked = await openServerDirectoryPicker({
      title: resolvedTitle,
      mode: 'directory',
      multiple: false,
      initialPath: options?.initialPath,
      nativeFallback,
    })
    return picked[0] ?? null
  }

  if (typeof rawOpenProject === 'function' && !bridge?.isPreviewShim) {
    const res = await (rawOpenProject as () => Promise<string | null | undefined>)()
    return res ?? null
  }
  if (typeof rawSelectFolders === 'function' && !bridge?.isPreviewShim) {
    const res = await (rawSelectFolders as (t?: string) => Promise<string[] | null | undefined>)(resolvedTitle)
    return Array.isArray(res) ? (res[0] ?? null) : null
  }
  return null
}

export async function pickCompanionAvatarDirectory(
  title?: string,
  options?: { initialPath?: string },
): Promise<string | null> {
  const resolvedTitle = title || i18n.global.t('webDirPicker.selectAvatarTitle')
  const bridge = getWindowTinadec()
  const rawNative = bridge?.selectCompanionAvatarDirectory
  const nativeFallback = isNativeElectronDialogFn(rawNative)
    ? async () => {
        const res = await (rawNative as (t?: string) => Promise<string | null | undefined>)(resolvedTitle)
        return typeof res === 'string' && res.length > 0 ? [res] : []
      }
    : undefined

  if (hasServerDirectoryPicker()) {
    const picked = await openServerDirectoryPicker({
      title: resolvedTitle,
      mode: 'directory',
      multiple: false,
      initialPath: options?.initialPath,
      nativeFallback,
    })
    return picked[0] ?? null
  }

  if (typeof rawNative === 'function' && !bridge?.isPreviewShim) {
    const res = await (rawNative as (t?: string) => Promise<string | null | undefined>)(resolvedTitle)
    return res ?? null
  }
  return null
}

export async function pickHostFiles(options: PickHostFilesOptions = {}): Promise<string[]> {
  const safeNativeFallback = isElectronHost() && typeof options.nativeFallback === 'function'
    ? options.nativeFallback
    : undefined

  if (hasServerDirectoryPicker()) {
    return openServerDirectoryPicker({
      title: options.title || i18n.global.t('webDirPicker.selectFileTitle'),
      mode: 'file',
      multiple: Boolean(options.multiple),
      acceptExtensions: options.acceptExtensions,
      initialPath: options.initialPath,
      nativeFallback: safeNativeFallback,
    })
  }

  if (typeof options.nativeFallback === 'function') {
    const res = await options.nativeFallback()
    return Array.isArray(res) ? res : []
  }
  return []
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
  const tinadec = getWindowTinadec()
  if (!tinadec || tinadec.isWebShim) return
  const openProjectDialog = async () => {
    const picked = await openServerDirectoryPicker({
      mode: 'directory',
      multiple: false,
      title: i18n.global.t('webDirPicker.selectProjectTitle'),
    })
    return picked[0] ?? null
  }
  const selectWorkspaceFolders = (title?: string) =>
    openServerDirectoryPicker({
      mode: 'directory',
      multiple: true,
      title: title || i18n.global.t('webDirPicker.selectWorkspaceTitle'),
    })
  const selectCompanionAvatarDirectory = async (title?: string) => {
    const picked = await openServerDirectoryPicker({
      mode: 'directory',
      multiple: false,
      title: title || i18n.global.t('webDirPicker.selectAvatarTitle'),
    })
    return picked[0] ?? null
  }
  const selectBackgroundFile = async (
    type: string,
    labels?: { title?: string },
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
    return `${resolveWebGatewayBase()}/api/v1/fs/media?token=${encodeWebMediaToken(chosen.replace(/\\/g, '/'))}`
  }
  const detachPanel = (tabId: string, type: string, title: string, state: Record<string, unknown>) => {
    const params = new URLSearchParams({
      tabId,
      type,
      title,
      state: JSON.stringify(state ?? {}),
    })
    window.open(`#/panel?${params.toString()}`, '_blank')
    return Promise.resolve(null)
  }

  bridgeWrappedFns.add(openProjectDialog)
  bridgeWrappedFns.add(selectWorkspaceFolders)
  bridgeWrappedFns.add(selectCompanionAvatarDirectory)
  bridgeWrappedFns.add(selectBackgroundFile)

  try {
    tinadec.openProjectDialog = openProjectDialog
    tinadec.selectWorkspaceFolders = selectWorkspaceFolders
    tinadec.selectCompanionAvatarDirectory = selectCompanionAvatarDirectory
    tinadec.selectBackgroundFile = selectBackgroundFile
    tinadec.detachPanel = detachPanel
  } catch {
    // Electron contextBridge freezes window.tinadec; never throw on assignment.
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
