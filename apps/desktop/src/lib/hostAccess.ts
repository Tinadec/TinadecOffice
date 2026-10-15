import { computed, shallowRef } from 'vue'
import { ApiError } from './apiError'
import i18n from '@/i18n'
import { readHostStatus, type HostConnectionStatus } from './hostConnection'

const status = shallowRef<HostConnectionStatus | null>(null)
let revision = 0
export function setHostAccessStatus(next: HostConnectionStatus): void { revision++; status.value = next }
export function useHostAccess() {
  return {
    status,
    isPreview: computed(() => status.value?.state === 'preview'),
    canAccessBackend: computed(() => !status.value || status.value.state === 'ready'),
    reason: computed(() => status.value?.state === 'preview'
      ? i18n.global.t('host.previewReason')
      : status.value?.error?.message ?? i18n.global.t('host.verifyingReason')),
  }
}

/** Capture the storage identity before awaiting this gate. No credential crosses IPC. */
export async function assertHostAccess(path: string, signal?: AbortSignal): Promise<void> {
  signal?.throwIfAborted()
  if (/^\/api\/v1\/(health|host-challenge)(?:[/?]|$)/.test(path)) return
  const host = typeof window === 'undefined' ? undefined : window.tinadec
  // Standalone injected transports have their own authentication; an existing preload does not.
  if (!host) return
  if (status.value?.state === 'restart_required') throwHostUnavailable(status.value)
  const readRevision = revision
  const snapshot = await readHostStatus(host)
  signal?.throwIfAborted()
  if (readRevision === revision) setHostAccessStatus(snapshot)
  const current = status.value ?? snapshot
  if (current.state === 'ready') return
  throwHostUnavailable(current)
}

function throwHostUnavailable(current: HostConnectionStatus): never {
  const preview = current.state === 'preview'
  const code = preview ? 'desktop_host_required' : current.error?.code ?? 'host_unavailable'
  const message = preview ? i18n.global.t('host.previewAction') : current.error?.message ?? i18n.global.t('host.notReady')
  throw new ApiError(message, preview ? 403 : 503, {
    code, detail: message, category: 'environment_unavailable', retryable: false,
    actions: preview || current.state === 'restart_required' ? [] : ['retry'],
  })
}
