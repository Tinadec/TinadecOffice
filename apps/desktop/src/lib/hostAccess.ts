import { computed, shallowRef } from 'vue'
import { ApiError } from './apiError'
import i18n from '@/i18n'
import { isElectronHost, readHostStatus, type HostConnectionStatus } from './hostConnection'

const status = shallowRef<HostConnectionStatus | null>(null)
const gatewayConnectionTracked = shallowRef(false)
const gatewayConnected = shallowRef(true)
let revision = 0
const accessInvalidators: Array<() => void> = []

function computeCanAccessBackend(
  currentStatus: HostConnectionStatus | null,
  tracked: boolean,
  connected: boolean,
): boolean {
  return (!currentStatus || currentStatus.state === 'ready') && (!tracked || connected)
}

const canAccessBackend = computed(() =>
  computeCanAccessBackend(status.value, gatewayConnectionTracked.value, gatewayConnected.value),
)

/** Derived read caches subscribe here instead of polling the gate on every hit. */
export function onHostAccessChanged(callback: () => void): void { accessInvalidators.push(callback) }

/** The gate state a cached read was produced under, for cheap staleness checks. */
export function hostAccessState(): string | null { return status.value?.state ?? null }

export function setHostAccessStatus(next: HostConnectionStatus): void {
  const previous = status.value
  const prevAccess = computeCanAccessBackend(previous, gatewayConnectionTracked.value, gatewayConnected.value)
  revision++
  status.value = next
  const nextAccess = computeCanAccessBackend(next, gatewayConnectionTracked.value, gatewayConnected.value)
  // Same state arriving again is the normal per-request revalidation; only a real move
  // (revoked, restart required, preview, auth_required, or effective access transition) may retire what was already read.
  if (previous?.state === next.state && previous?.error?.code === next.error?.code && prevAccess === nextAccess) return
  for (const invalidate of accessInvalidators) invalidate()
}

/**
 * Bridges `useConnection` public Gateway liveness into `canAccessBackend`.
 * Isolated component unit tests that never start `useConnection` keep `(false, true)`
 * so `canAccessBackend` remains `true` when `status.value` is `null` or `'ready'`.
 */
export function setGatewayConnectionLiveness(tracked: boolean, connected: boolean): void {
  const prevAccess = computeCanAccessBackend(status.value, gatewayConnectionTracked.value, gatewayConnected.value)
  gatewayConnectionTracked.value = tracked
  gatewayConnected.value = connected
  const nextAccess = computeCanAccessBackend(status.value, tracked, connected)
  if (prevAccess !== nextAccess) {
    for (const invalidate of accessInvalidators) invalidate()
  }
}

/** Test-only: reset host access status and gateway liveness tracking. */
export function resetHostAccessForTests(): void {
  revision++
  status.value = null
  gatewayConnectionTracked.value = false
  gatewayConnected.value = true
}

export function useHostAccess() {
  return {
    status,
    isPreview: computed(() => status.value?.state === 'preview'),
    isAuthRequired: computed(() => status.value?.state === 'auth_required'),
    isElectron: computed(() => isElectronHost() && !status.value?.webAuth),
    canAccessBackend,
    reason: computed(() => status.value?.state === 'preview'
      ? i18n.global.t('host.previewReason')
      : status.value?.state === 'auth_required'
        ? i18n.global.t('host.authRequiredReason')
        : status.value?.error?.message ?? i18n.global.t('host.verifyingReason')),
  }
}

/** Capture the storage identity before awaiting this gate. No credential crosses IPC. */
export async function assertHostAccess(path: string, signal?: AbortSignal): Promise<void> {
  signal?.throwIfAborted()
  if (/^\/api\/v1\/(health|host-challenge|web-auth\/(?:status|login|logout|oidc\/))/.test(path)) return
  const host = typeof window === 'undefined' ? undefined : window.tinadec
  // Standalone injected transports have their own authentication; an existing preload does not.
  if (!host) return
  if (status.value?.state === 'restart_required') throwHostUnavailable(status.value)
  // A preload that pushes host status already keeps this gate current: main broadcasts every
  // change and useConnection publishes it through setHostAccessStatus. Re-asking per request
  // spends an IPC round trip on an answer we were just told, so only bridges that cannot push
  // are polled. A revoked host still refuses here — the published state is what refused.
  const pushed = typeof host.onHostStatusChanged === 'function' ? status.value : null
  if (!pushed) {
    const readRevision = revision
    const snapshot = await readHostStatus(host)
    signal?.throwIfAborted()
    if (readRevision === revision) setHostAccessStatus(snapshot)
  }
  const current = status.value ?? pushed
  if (current?.state === 'ready') return
  if (!current) {
    throw new ApiError(i18n.global.t('host.verifyingReason'), 503, {
      code: 'host_unavailable', detail: i18n.global.t('host.verifyingReason'),
      category: 'environment_unavailable', retryable: true, actions: ['retry'],
    })
  }
  throwHostUnavailable(current)
}

function throwHostUnavailable(current: HostConnectionStatus): never {
  if (current.state === 'auth_required') {
    const message = current.error?.message ?? i18n.global.t('host.authRequiredReason')
    throw new ApiError(message, 401, {
      code: 'web_auth_required', detail: message, category: 'user_action_required', retryable: false, actions: ['retry'],
    })
  }
  const preview = current.state === 'preview'
  const code = preview ? 'desktop_host_required' : current.error?.code ?? 'host_unavailable'
  const message = preview ? i18n.global.t('host.previewAction') : current.error?.message ?? i18n.global.t('host.notReady')
  throw new ApiError(message, preview ? 403 : 503, {
    code, detail: message, category: 'environment_unavailable', retryable: false,
    actions: preview || current.state === 'restart_required' ? [] : ['retry'],
  })
}
