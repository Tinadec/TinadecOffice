import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useHostAccess } from '@/lib/hostAccess'
import { isAbortError } from '@/lib/isAbortError'
import { useErrorState } from './useErrorState'

export interface ReadContext {
  signal: AbortSignal
  isCurrent: () => boolean
}

/** A mounted screen owns its read, cancellation and host-recovery subscription. */
export function useRecoverableRead(
  read: (context: ReadContext) => Promise<void>,
  options: { onFailure?: (error: unknown) => void; onSuccess?: () => void } = {},
) {
  const { canAccessBackend } = useHostAccess()
  const loading = ref(false)
  const loaded = ref(false)
  const failure = useErrorState()
  let generation = 0
  let disposed = false
  let controller: AbortController | null = null
  let pending: Promise<void> | null = null

  function cancel() {
    ++generation
    controller?.abort()
    controller = null
    pending = null
    loading.value = false
  }

  function refresh(): Promise<void> {
    if (disposed || !canAccessBackend.value) return Promise.resolve()
    if (pending) return pending
    const current = ++generation
    const request = new AbortController()
    controller = request
    loading.value = true
    const isCurrent = () => !disposed && current === generation && !request.signal.aborted && canAccessBackend.value
    pending = Promise.resolve().then(() => { if (isCurrent()) return read({ signal: request.signal, isCurrent }) }).then(() => {
      if (!isCurrent()) return
      loaded.value = true
      failure.clear()
      options.onSuccess?.()
    }).catch(error => {
      if (!isCurrent() || isAbortError(error)) return
      failure.set(error)
      options.onFailure?.(error)
    }).finally(() => {
      if (current === generation) { pending = null; controller = null; loading.value = false }
    })
    return pending
  }

  watch(canAccessBackend, ready => { if (ready) void refresh(); else cancel() }, { flush: 'sync' })
  onMounted(() => { void refresh() })
  onBeforeUnmount(() => { disposed = true; cancel() })
  return { refresh, loading, loaded, failure }
}
