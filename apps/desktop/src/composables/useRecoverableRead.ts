import {
  getCurrentInstance,
  getCurrentScope,
  onBeforeUnmount,
  onMounted,
  onScopeDispose,
  ref,
  watch,
} from 'vue'
import { useHostAccess } from '@/lib/hostAccess'
import { isAbortError } from '@/lib/isAbortError'
import { useErrorState } from './useErrorState'
import {
  getErrorActionLabel,
  isConnectionLevelError,
  useNotifications,
} from './useNotifications'

export interface ReadContext {
  signal: AbortSignal
  isCurrent: () => boolean
}

export interface RecoverableReadOptions {
  /**
   * Optional notification key owned by this screen read. When provided:
   * - failures emit a dismissible `notify.error` tagged with `key` and
   *   `category: isConnectionLevelError(err) ? 'connection' : 'general'` (unless `onFailure` overrides);
   * - `dismissByKey(key)` runs automatically on successful read, on `canAccessBackend`
   *   recovery retry, and when the owning component/effect scope disposes.
   */
  notificationKey?: string
  notificationSource?: string
  notificationTitle?: string
  retryLabel?: string
  onCleanup?: () => void
  onFailure?: (error: unknown) => void
  onSuccess?: () => void
  /**
   * Read only once something actually asks for it. Use this for a screen that is mounted as
   * part of a larger page but is not the section the user is looking at: mounting it must not
   * spend the round trips, and the caller drives the first read with `refresh()`.
   */
  lazy?: boolean
}

/**
 * A mounted screen owns its read, cancellation and host-recovery subscription.
 *
 * Read on mount by default. `lazy` moves the first read to whoever calls `refresh()` — that
 * is the difference between "the page is open" and "the user is looking at this".
 */
export function useRecoverableRead(
  read: (context: ReadContext) => Promise<void>,
  options: RecoverableReadOptions = {},
) {
  const { canAccessBackend } = useHostAccess()
  const { notify, dismissByKey } = useNotifications()
  const loading = ref(false)
  const loaded = ref(false)
  const failure = useErrorState()
  let generation = 0
  let disposed = false
  let controller: AbortController | null = null
  let pending: Promise<void> | null = null
  /**
   * Set by the first `refresh()`, including one that could not run because the host was not
   * verified yet. A lazy read must not wake on mount, but it must still recover a first read
   * that was asked for while the host was down.
   */
  let requested = false

  function clearOwnedNotification() {
    if (options.notificationKey) {
      dismissByKey(options.notificationKey)
    }
  }

  function cancel() {
    ++generation
    controller?.abort()
    controller = null
    pending = null
    loading.value = false
  }

  function refresh(): Promise<void> {
    requested = true
    if (disposed || !canAccessBackend.value) return Promise.resolve()
    if (pending) return pending
    const current = ++generation
    const request = new AbortController()
    controller = request
    loading.value = true
    const isCurrent = () =>
      !disposed && current === generation && !request.signal.aborted && canAccessBackend.value
    pending = Promise.resolve()
      .then(() => {
        if (isCurrent()) return read({ signal: request.signal, isCurrent })
      })
      .then(() => {
        if (!isCurrent()) return
        loaded.value = true
        failure.clear()
        clearOwnedNotification()
        options.onSuccess?.()
      })
      .catch((error) => {
        if (!isCurrent() || isAbortError(error)) return
        failure.set(error)
        if (options.onFailure) {
          options.onFailure(error)
        } else if (options.notificationKey) {
          notify.error(error, {
            key: options.notificationKey,
            category: isConnectionLevelError(error) ? 'connection' : 'general',
            title: options.notificationTitle,
            source: options.notificationSource,
            action: {
              label: options.retryLabel ?? getErrorActionLabel('retry'),
              run: () => refresh(),
            },
          })
        }
      })
      .finally(() => {
        if (current === generation) {
          pending = null
          controller = null
          loading.value = false
        }
      })
    return pending
  }

  // A lazy read still has to survive a reconnection *after* it was first asked for; before
  // that, host recovery has nothing to recover and must not wake it.
  watch(
    canAccessBackend,
    (ready) => {
      if (!ready) {
        cancel()
        return
      }
      clearOwnedNotification()
      if (requested || !options.lazy) void refresh()
    },
    { flush: 'sync' },
  )

  const dispose = () => {
    if (disposed) return
    disposed = true
    cancel()
    clearOwnedNotification()
    options.onCleanup?.()
  }

  if (getCurrentInstance()) {
    if (!options.lazy) {
      onMounted(() => {
        void refresh()
      })
    }
    onBeforeUnmount(dispose)
  } else if (getCurrentScope()) {
    if (!options.lazy) {
      void refresh()
    }
    onScopeDispose(dispose)
  }

  return { refresh, loading, loaded, failure }
}
