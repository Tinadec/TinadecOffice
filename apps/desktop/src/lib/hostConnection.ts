import i18n from '@/i18n'

/** Public host readiness snapshot. Credentials never cross this boundary. */
export interface HostConnectionStatus {
  state: 'checking' | 'ready' | 'unavailable' | 'rejected' | 'preview' | 'restart_required'
  managed: boolean
  checked_at?: string
  error?: { code: string; message: string }
}

export interface HostStatusBridge {
  getHostStatus?: () => Promise<HostConnectionStatus>
  retryHostConnection?: () => Promise<HostConnectionStatus>
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
}
export function localizeHostStatus(status: HostConnectionStatus): HostConnectionStatus {
  const key = status.error ? HOST_ERROR_KEYS[status.error.code] : undefined
  return key && status.error ? { ...status, error: { ...status.error, message: i18n.global.t(key) } } : status
}

/** Missing methods on an existing preload must fail closed; a bare browser is preview. */
export async function readHostStatus(bridge?: HostStatusBridge): Promise<HostConnectionStatus> {
  if (!bridge) return { state: 'preview', managed: false }
  if (typeof bridge.getHostStatus !== 'function') return restartRequired()
  try { return localizeHostStatus(await bridge.getHostStatus()) } catch (error) { return hostBridgeFailure(error) }
}

export async function retryHostStatus(bridge?: HostStatusBridge): Promise<HostConnectionStatus> {
  if (!bridge) return { state: 'preview', managed: false }
  if (typeof bridge.retryHostConnection !== 'function') return restartRequired()
  try { return localizeHostStatus(await bridge.retryHostConnection()) } catch (error) { return hostBridgeFailure(error) }
}
