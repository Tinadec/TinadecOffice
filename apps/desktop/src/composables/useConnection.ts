import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import i18n from '@/i18n'
import {
  setGatewayConnectionLiveness,
  setHostAccessStatus,
  useHostAccess,
} from '@/lib/hostAccess'
import {
  localizeHostStatus,
  readHostStatus,
  retryHostStatus,
  type HostConnectionStatus,
} from '@/lib/hostConnection'
import {
  dismissByCategory,
  setGlobalConnectionBlocked,
  useNotifications,
} from '@/composables/useNotifications'

/** Public liveness and private host readiness are separate. Only both admit business work. */
export type ConnectionState =
  | 'connecting'
  | 'connected'
  | 'timeout'
  | 'disconnected'
  | 'host_unavailable'
  | 'host_rejected'
  | 'host_restart_required'
  | 'preview'

export const CONNECTION_TIMEOUT_MS = 30_000
export const CONNECTION_POLL_INTERVAL_MS = 1_500
export const CONNECTION_CHECKING_GRACE_MS = 1_200
export const CONNECTION_BANNER_KEY = 'backend-connection'

const connectionState = ref<ConnectionState>('connecting')
const hostStatus = ref<HostConnectionStatus>({ state: 'checking', managed: true })
const businessReady = computed(
  () => connectionState.value === 'connected' && hostStatus.value.state === 'ready',
)

let timeoutHandle: ReturnType<typeof setTimeout> | undefined
let pollHandle: ReturnType<typeof setInterval> | undefined
let watchHandle: ReturnType<typeof setInterval> | undefined
let checkingGraceHandle: ReturnType<typeof setTimeout> | undefined
let unsubscribeHost: (() => void) | undefined
let pendingProbe: Promise<boolean> | undefined
let pendingRetry: Promise<boolean> | undefined
let started = false
let generation = 0
let hostRevision = 0

watch(
  connectionState,
  (state) => {
    if (started) {
      setGatewayConnectionLiveness(true, state === 'connected')
    }
  },
  { flush: 'sync' },
)

function clearStartupTimers() {
  clearTimeout(timeoutHandle)
  clearInterval(pollHandle)
  timeoutHandle = undefined
  pollHandle = undefined
}

export function clearConnectionNotificationTimers(): void {
  if (checkingGraceHandle !== undefined) {
    clearTimeout(checkingGraceHandle)
    checkingGraceHandle = undefined
  }
}

function markConnected() {
  connectionState.value = 'connected'
  if (started) setGatewayConnectionLiveness(true, true)
  clearStartupTimers()
  startHealthWatch()
}

function applyHostStatus(next: HostConnectionStatus) {
  hostRevision++
  hostStatus.value = next
  setHostAccessStatus(next)
  if (next.state === 'ready') return
  connectionState.value =
    next.state === 'preview'
      ? 'preview'
      : next.state === 'restart_required'
        ? 'host_restart_required'
        : next.state === 'rejected'
          ? 'host_rejected'
          : next.state === 'unavailable'
            ? 'host_unavailable'
            : connectionState.value === 'connected'
              ? 'disconnected'
              : 'connecting'
  if (started) {
    setGatewayConnectionLiveness(true, false)
  }
  if (next.state !== 'checking') {
    clearStartupTimers()
    if (next.state === 'preview' || next.state === 'restart_required') {
      clearInterval(watchHandle)
      watchHandle = undefined
    } else {
      startHealthWatch()
    }
  }
}

function bridge() {
  return typeof window === 'undefined' ? undefined : window.tinadec
}

function probe(refreshHost = true): Promise<boolean> {
  if (hostStatus.value.state === 'restart_required') return Promise.resolve(false)
  if (pendingProbe) return pendingProbe
  const epoch = generation
  const revision = hostRevision
  pendingProbe = (async () => {
    try {
      const host = bridge()
      if (refreshHost) {
        const next = await readHostStatus(host)
        if (epoch !== generation) return false
        // A newer main-process event outranks an older IPC read.
        if (revision === hostRevision) applyHostStatus(next)
      }
      if (hostStatus.value.state !== 'ready') return false
      const authenticatedRevision = hostRevision
      await api.health()
      if (
        epoch !== generation ||
        authenticatedRevision !== hostRevision ||
        hostStatus.value.state !== 'ready'
      ) {
        return false
      }
      markConnected()
      return true
    } catch {
      if (epoch === generation && connectionState.value === 'connected') {
        connectionState.value = 'disconnected'
        if (started) setGatewayConnectionLiveness(true, false)
      }
      return false
    }
  })().finally(() => {
    if (epoch === generation) pendingProbe = undefined
  })
  return pendingProbe
}

function startHealthWatch() {
  if (watchHandle !== undefined || !started) return
  watchHandle = setInterval(() => {
    void probe()
  }, CONNECTION_POLL_INTERVAL_MS * 4)
}

export function retryConnection(): Promise<boolean> {
  if (hostStatus.value.state === 'restart_required') return Promise.resolve(false)
  if (pendingRetry) return pendingRetry
  const epoch = generation
  const revision = hostRevision
  pendingRetry = (async () => {
    try {
      const host = bridge()
      const next = await retryHostStatus(host)
      if (epoch !== generation) return false
      // A main-process event published during retry is newer than its IPC receipt.
      if (revision === hostRevision) applyHostStatus(next)
      if (hostStatus.value.state !== 'ready') return false
      // An older public probe must settle before the explicit authenticated retry.
      if (pendingProbe) await pendingProbe
      return await probe(false)
    } catch {
      if (epoch === generation && revision === hostRevision) {
        applyHostStatus({
          state: 'unavailable',
          managed: true,
          error: {
            code: 'host_connection_unavailable',
            message: i18n.global.t('host.connectionCheckFailed'),
          },
        })
      }
      return false
    }
  })().finally(() => {
    if (epoch === generation) pendingRetry = undefined
  })
  return pendingRetry
}

/**
 * Single projector mapping all host & Gateway states onto the sole
 * `CONNECTION_BANNER_KEY = 'backend-connection'` Dynamic Island status capsule.
 */
export function syncConnectionNotifications(options: { isAuthRequired?: boolean } = {}): void {
  const { items, status, dismissByKey } = useNotifications()
  const { status: hostAccessStatus } = useHostAccess()
  const t = i18n.global.t

  const state = connectionState.value
  const currentHost = hostStatus.value
  const authRequired =
    options.isAuthRequired ??
    (currentHost.state === 'auth_required' || hostAccessStatus.value?.state === 'auth_required')

  if (authRequired) {
    clearConnectionNotificationTimers()
    setGlobalConnectionBlocked(true)
    dismissByKey(CONNECTION_BANNER_KEY)
    return
  }

  if (businessReady.value) {
    clearConnectionNotificationTimers()
    setGlobalConnectionBlocked(false)
    dismissByKey(CONNECTION_BANNER_KEY)
    dismissByCategory('connection')
    return
  }

  // While host or gateway is not ready, suppress derived connection error avalanches.
  setGlobalConnectionBlocked(true)

  if (state === 'preview' || currentHost.state === 'preview') {
    clearConnectionNotificationTimers()
    status.info({
      key: CONNECTION_BANNER_KEY,
      category: 'connection',
      broadcast: true,
      source: 'desktop',
      title: t('hostBanner.previewTitle'),
      message: t('hostBanner.previewText'),
    })
    return
  }

  if (state === 'host_restart_required' || currentHost.state === 'restart_required') {
    clearConnectionNotificationTimers()
    const development = import.meta.env.DEV
    const host = bridge()
    const restartAvailable = !development && typeof host?.restartApp === 'function'
    const guidance = development
      ? t('hostBanner.developerRestartText')
      : !restartAvailable
        ? t('hostBanner.manualRestartText')
        : undefined
    status.error({
      key: CONNECTION_BANNER_KEY,
      category: 'connection',
      broadcast: true,
      source: 'desktop',
      title: t('hostBanner.restartTitle'),
      message: t('hostBanner.restartText'),
      details: guidance,
      action: restartAvailable
        ? {
            label: t('hostBanner.restart'),
            run: async () => {
              await host!.restartApp()
            },
          }
        : undefined,
    })
    return
  }

  if (state === 'host_rejected' || currentHost.state === 'rejected') {
    clearConnectionNotificationTimers()
    status.error({
      key: CONNECTION_BANNER_KEY,
      category: 'connection',
      broadcast: true,
      source: 'desktop',
      title: t('hostBanner.rejectedTitle'),
      message: t('hostBanner.rejectedText'),
      details: currentHost.error?.message,
      action: { label: t('hostBanner.retry'), run: () => retryConnection() },
    })
    return
  }

  if (state === 'host_unavailable' || currentHost.state === 'unavailable') {
    clearConnectionNotificationTimers()
    status.error({
      key: CONNECTION_BANNER_KEY,
      category: 'connection',
      broadcast: true,
      source: 'desktop',
      title: t('hostBanner.unavailableTitle'),
      message: t('hostBanner.unavailableText'),
      details: currentHost.error?.message,
      action: { label: t('hostBanner.retry'), run: () => retryConnection() },
    })
    return
  }

  if (state === 'timeout') {
    clearConnectionNotificationTimers()
    status.error({
      key: CONNECTION_BANNER_KEY,
      category: 'connection',
      broadcast: true,
      source: 'gateway',
      title: t('app.backendNotConnected'),
      message: t('app.backendNotConnectedMessage'),
      action: { label: t('app.retryConnection'), run: () => retryConnection() },
    })
    return
  }

  if (state === 'disconnected') {
    clearConnectionNotificationTimers()
    status.error({
      key: CONNECTION_BANNER_KEY,
      category: 'connection',
      broadcast: true,
      source: 'gateway',
      title: t('app.backendDisconnected'),
      message: t('app.backendDisconnectedMessage'),
      action: { label: t('app.retryConnection'), run: () => retryConnection() },
    })
    return
  }

  // `connecting` / `checking`: keep fast startups silent during the 1200ms grace period.
  const alreadyVisible = items.value.some((candidate) => candidate.key === CONNECTION_BANNER_KEY)
  if (alreadyVisible) {
    clearConnectionNotificationTimers()
    status.info({
      key: CONNECTION_BANNER_KEY,
      category: 'connection',
      broadcast: true,
      source: 'gateway',
      title: t('app.backendNotConnected'),
      message: t('hostBanner.checking'),
      action: { label: t('app.retryConnection'), run: () => retryConnection() },
    })
    return
  }

  if (checkingGraceHandle === undefined) {
    const scheduledEpoch = generation
    checkingGraceHandle = setTimeout(() => {
      checkingGraceHandle = undefined
      if (scheduledEpoch !== generation) return
      const nowAuthRequired =
        hostStatus.value.state === 'auth_required' ||
        hostAccessStatus.value?.state === 'auth_required'
      if (businessReady.value || nowAuthRequired || connectionState.value !== 'connecting') return
      status.info({
        key: CONNECTION_BANNER_KEY,
        category: 'connection',
        broadcast: true,
        source: 'gateway',
        title: t('app.backendNotConnected'),
        message: t('hostBanner.checking'),
        action: { label: t('app.retryConnection'), run: () => retryConnection() },
      })
    }, CONNECTION_CHECKING_GRACE_MS)
  }
}

export function useConnection() {
  async function start() {
    if (started) return
    started = true
    setGatewayConnectionLiveness(true, connectionState.value === 'connected')
    const epoch = generation
    const host = bridge()
    unsubscribeHost = host?.onHostStatusChanged?.((pushed) => {
      if (epoch !== generation) return
      const next = localizeHostStatus(pushed)
      applyHostStatus(next)
      if (next.state === 'ready') {
        // Let any older read settle, then verify public liveness against the new snapshot.
        void (async () => {
          if (pendingProbe) await pendingProbe
          if (epoch === generation) await probe(false)
        })()
      }
    })
    timeoutHandle = setTimeout(() => {
      if (connectionState.value !== 'connecting') return
      connectionState.value = 'timeout'
      setGatewayConnectionLiveness(true, false)
      clearStartupTimers()
      startHealthWatch()
    }, CONNECTION_TIMEOUT_MS)
    await probe()
    if (epoch === generation && connectionState.value === 'connecting') {
      pollHandle = setInterval(() => {
        void probe()
      }, CONNECTION_POLL_INTERVAL_MS)
    }
  }
  return {
    connectionState,
    hostStatus,
    businessReady,
    start,
    retryConnection,
    syncConnectionNotifications,
    clearConnectionNotificationTimers,
  }
}

/** Test-only: reset singleton state and invalidate all late reads. */
export function __resetConnectionForTests() {
  generation++
  clearStartupTimers()
  clearConnectionNotificationTimers()
  clearInterval(watchHandle)
  watchHandle = undefined
  unsubscribeHost?.()
  unsubscribeHost = undefined
  pendingProbe = undefined
  pendingRetry = undefined
  started = false
  hostRevision = 0
  connectionState.value = 'connecting'
  hostStatus.value = { state: 'checking', managed: true }
  setGatewayConnectionLiveness(false, true)
}
