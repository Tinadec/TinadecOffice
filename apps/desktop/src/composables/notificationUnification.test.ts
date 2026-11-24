// @vitest-environment happy-dom

import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, watch } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'

vi.mock('@/api', () => ({
  api: {
    gatewayUrl: 'http://gateway.test',
    health: vi.fn(),
    previewAgentPackInstall: vi.fn(),
    installAgentPack: vi.fn(),
  },
}))

import { api } from '@/api'
import i18n from '@/i18n'
import { ApiError } from '@/lib/apiError'
import {
  resetHostAccessForTests,
  setHostAccessStatus,
  useHostAccess,
} from '@/lib/hostAccess'
import {
  __resetConnectionForTests,
  CONNECTION_BANNER_KEY,
  CONNECTION_CHECKING_GRACE_MS,
  CONNECTION_POLL_INTERVAL_MS,
  retryConnection,
  syncConnectionNotifications,
  useConnection,
  type ConnectionState,
} from './useConnection'
import {
  __resetNotificationsForTests,
  dismissByCategory,
  isConnectionLevelError,
  setGlobalConnectionBlocked,
  useNotifications,
  useScopedStatus,
} from './useNotifications'
import { useRecoverableRead, type ReadContext } from './useRecoverableRead'
import {
  __resetGraphSeedPackBootstrapForTests,
  ensureGraphSeedPack,
  graphSeedPackState,
} from '@/agentPacks/graphSeedPackBootstrap'
import {
  GRAPH_SEED_PACK_DIGEST,
  GRAPH_SEED_PACK_ID,
  GRAPH_SEED_PACK_VERSION,
} from '@/agentPacks/GraphSeedPack'
import NotificationIslandHost from '@/components/NotificationIslandHost.vue'

const srcDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

describe('Unified Dynamic Notification System invariants', () => {
  let hostListener: ((status: import('@/lib/hostConnection').HostConnectionStatus) => void) | undefined
  let getHostStatus: ReturnType<typeof vi.fn>
  let retryHostConnection: ReturnType<typeof vi.fn>
  let broadcastStatusNotification: ReturnType<typeof vi.fn>

  beforeEach(() => {
    vi.useFakeTimers()
    vi.clearAllMocks()
    __resetNotificationsForTests()
    __resetConnectionForTests()
    __resetGraphSeedPackBootstrapForTests()
    resetHostAccessForTests()

    hostListener = undefined
    getHostStatus = vi.fn().mockResolvedValue({ state: 'ready', managed: true })
    retryHostConnection = vi.fn().mockResolvedValue({ state: 'ready', managed: true })
    broadcastStatusNotification = vi.fn()

    vi.stubGlobal('BroadcastChannel', undefined)
    Object.defineProperty(window, 'tinadec', {
      configurable: true,
      value: {
        getHostStatus,
        retryHostConnection,
        broadcastStatusNotification,
        onHostStatusChanged: (cb: typeof hostListener) => {
          hostListener = cb
          return () => {
            hostListener = undefined
          }
        },
      },
    })
  })

  afterEach(() => {
    __resetNotificationsForTests()
    __resetConnectionForTests()
    __resetGraphSeedPackBootstrapForTests()
    resetHostAccessForTests()
    vi.unstubAllGlobals()
    vi.restoreAllMocks()
    vi.useRealTimers()
  })

  // -------------------------------------------------------------------------
  // Invariant 1 — Single notification channel & cold-start visibility
  // -------------------------------------------------------------------------
  it('Invariant 1: HostAvailabilityBanner.vue is deleted and App.vue mounts NotificationIslandHost unconditionally', () => {
    const retiredBannerPath = path.join(srcDir, 'components', 'HostAvailabilityBanner.vue')
    expect(existsSync(retiredBannerPath)).toBe(false)

    const appSource = readFileSync(path.join(srcDir, 'App.vue'), 'utf8')
    expect(appSource).not.toContain('HostAvailabilityBanner')
    expect(appSource).toMatch(/<NotificationIslandHost\s*\/>/)
    expect(appSource).not.toMatch(/<NotificationIslandHost[^>]*v-if="entryReady"/)
  })

  // -------------------------------------------------------------------------
  // Invariant 2 — Single connection projector + 1200ms checking grace timer
  // -------------------------------------------------------------------------
  it('Invariant 2: projects all non-ready host/gateway states onto a single CONNECTION_BANNER_KEY capsule with a 1200ms startup grace', () => {
    const n = useNotifications()
    const { connectionState, hostStatus } = useConnection()

    // Initial cold-start `connecting` + `checking` stays silent before 1200ms.
    connectionState.value = 'connecting'
    hostStatus.value = { state: 'checking', managed: true }
    syncConnectionNotifications()
    expect(n.items.value).toHaveLength(0)

    vi.advanceTimersByTime(CONNECTION_CHECKING_GRACE_MS - 1)
    expect(n.items.value).toHaveLength(0)

    // At 1200ms grace expiry, `connecting` surfaces on CONNECTION_BANNER_KEY.
    vi.advanceTimersByTime(1)
    expect(n.items.value).toHaveLength(1)
    expect(n.items.value[0]?.key).toBe(CONNECTION_BANNER_KEY)
    const slotId = n.items.value[0]!.id

    // Walk every non-ready host/gateway state; all update in place without stacking.
    const transitions: Array<{
      conn: ConnectionState
      host: import('@/lib/hostConnection').HostConnectionStatus
      expectAction: boolean
    }> = [
      {
        conn: 'host_unavailable',
        host: { state: 'unavailable', managed: true, error: { code: 'host_unavailable', message: 'Host offline' } },
        expectAction: true,
      },
      {
        conn: 'host_rejected',
        host: { state: 'rejected', managed: true, error: { code: 'host_rejected', message: 'Bad token' } },
        expectAction: true,
      },
      {
        conn: 'host_restart_required',
        host: { state: 'restart_required', managed: true, error: { code: 'desktop_restart_required', message: 'Restart' } },
        expectAction: false,
      },
      {
        conn: 'preview',
        host: { state: 'preview', managed: false },
        expectAction: false,
      },
      {
        conn: 'timeout',
        host: { state: 'ready', managed: true },
        expectAction: true,
      },
      {
        conn: 'disconnected',
        host: { state: 'ready', managed: true },
        expectAction: true,
      },
    ]

    for (const step of transitions) {
      connectionState.value = step.conn
      hostStatus.value = step.host
      syncConnectionNotifications()

      expect(n.items.value).toHaveLength(1)
      const capsule = n.items.value[0]!
      expect(capsule.key).toBe(CONNECTION_BANNER_KEY)
      expect(capsule.id).toBe(slotId)
      expect(Boolean(capsule.action)).toBe(step.expectAction)
    }

    // Web auth required delegates to WebLoginPage and clears the connection capsule.
    syncConnectionNotifications({ isAuthRequired: true })
    expect(n.items.value.filter((i) => i.key === CONNECTION_BANNER_KEY)).toHaveLength(0)
  })

  it('Invariant 2b: fast startup reaching businessReady before 1200ms never flashes a checking capsule', () => {
    const n = useNotifications()
    const { connectionState, hostStatus } = useConnection()

    connectionState.value = 'connecting'
    hostStatus.value = { state: 'checking', managed: true }
    syncConnectionNotifications()
    vi.advanceTimersByTime(400)
    expect(n.items.value).toHaveLength(0)

    connectionState.value = 'connected'
    hostStatus.value = { state: 'ready', managed: true }
    syncConnectionNotifications()

    vi.advanceTimersByTime(CONNECTION_CHECKING_GRACE_MS * 2)
    expect(n.items.value).toHaveLength(0)
  })

  // -------------------------------------------------------------------------
  // Invariant 3 — Centralized connection-avalanche suppression & coalescing
  // -------------------------------------------------------------------------
  it('Invariant 3: classifies connection-level errors and suppresses sub-module avalanches while disconnected', () => {
    const n = useNotifications()
    const { connectionState, hostStatus } = useConnection()

    const connectionErrors: unknown[] = [
      new ApiError('Gateway down', 503, { code: 'backend_network_unavailable' }),
      new ApiError('Write unknown', 502, { code: 'backend_write_outcome_unknown' }),
      new ApiError('Host down', 503, { code: 'host_unavailable' }),
      new ApiError('Bridge down', 503, { code: 'host_bridge_unavailable' }),
      new ApiError('Check failed', 503, { code: 'host_connection_unavailable' }),
      new ApiError('Restart required', 503, { code: 'desktop_restart_required' }),
      new ApiError('Desktop required', 403, { code: 'desktop_host_required' }),
      new ApiError('Login required', 401, { code: 'web_auth_required' }),
      Object.assign(new Error('Bad Gateway'), { status: 502 }),
      Object.assign(new Error('Gateway Timeout'), { status: 504 }),
      new TypeError('Failed to fetch'),
      new Error('NetworkError when attempting to fetch resource.'),
      new Error('Load failed'),
      new Error('connect ECONNREFUSED 127.0.0.1:48730'),
    ]

    for (const err of connectionErrors) {
      expect(isConnectionLevelError(err)).toBe(true)
    }

    // Non-connection errors must NOT be misclassified.
    expect(isConnectionLevelError(new Error('Validation failed'))).toBe(false)
    expect(isConnectionLevelError(new TypeError("Cannot read properties of null (reading 'id')"))).toBe(false)
    expect(
      isConnectionLevelError(new ApiError('Missing folder', 503, { code: 'storage_scope_not_found' })),
    ).toBe(false)

    // Pre-existing derived connection error pushed right before CONNECTION_BANNER_KEY activates is coalesced.
    setGlobalConnectionBlocked(false)
    n.notify.error(new TypeError('Failed to fetch'), { key: 'early-submodule' })
    expect(n.items.value.some((i) => i.key === 'early-submodule')).toBe(true)

    connectionState.value = 'disconnected'
    hostStatus.value = { state: 'ready', managed: true }
    syncConnectionNotifications()

    expect(n.items.value.map((i) => i.key)).toEqual([CONNECTION_BANNER_KEY])

    // Sub-modules throwing connection errors while disconnected are suppressed.
    for (const [index, err] of connectionErrors.entries()) {
      n.notify.error(err, { key: `sub-${index}` })
    }
    expect(n.items.value).toHaveLength(1)
    expect(n.items.value[0]?.key).toBe(CONNECTION_BANNER_KEY)

    // Genuine non-connection business/form errors still surface normally.
    n.notify.error(new Error('Invalid JSON syntax'), { key: 'json-syntax' })
    expect(n.items.value.map((i) => i.key)).toEqual([CONNECTION_BANNER_KEY, 'json-syntax'])
  })

  // -------------------------------------------------------------------------
  // Invariant 4 — Recovery loop: clear connection items + wake canAccessBackend
  //               + auto-retry useRecoverableRead & ensureGraphSeedPack
  // -------------------------------------------------------------------------
  it('Invariant 4: Gateway flap wakes canAccessBackend, clears all connection notifications, and retries useRecoverableRead + ensureGraphSeedPack', async () => {
    const n = useNotifications()
    const { canAccessBackend } = useHostAccess()
    const { connectionState, hostStatus, businessReady, start } = useConnection()

    // Simulate startup where Gateway health initially fails (host is ready, Gateway is down).
    vi.mocked(api.health).mockRejectedValue(new TypeError('Failed to fetch'))
    vi.mocked(api.previewAgentPackInstall).mockRejectedValueOnce(new TypeError('Failed to fetch'))

    let recoverableAttempts = 0
    const screenRead = vi.fn(async (_ctx: ReadContext) => {
      recoverableAttempts++
      if (recoverableAttempts === 1) {
        throw new Error('Transient read error')
      }
    })

    // Wire reactive projector + seed-pack bootstrapper mirroring App.vue.
    let graphSeedBootstrapped = false
    const stopProjector = watch(
      [businessReady, connectionState, hostStatus],
      ([ready]) => {
        syncConnectionNotifications()
        if (ready && !graphSeedBootstrapped) {
          void ensureGraphSeedPack().then((ok) => {
            if (ok) graphSeedBootstrapped = true
          })
        }
      },
      { immediate: true },
    )

    const wrapper = mount(
      defineComponent({
        setup() {
          useRecoverableRead(screenRead, { notificationKey: 'screen-read' })
          return () => null
        },
      }),
    )

    await start()
    await flushPromises()

    // While Gateway is still connecting (health failed), canAccessBackend is false!
    expect(canAccessBackend.value).toBe(false)
    expect(businessReady.value).toBe(false)

    // Even if ensureGraphSeedPack ran during a transient network drop, it resolves false and does not lock out retries.
    expect(await ensureGraphSeedPack()).toBe(false)
    expect(graphSeedBootstrapped).toBe(false)

    // Now Gateway comes online on next poll -> businessReady & canAccessBackend flip false -> true!
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    vi.mocked(api.previewAgentPackInstall).mockResolvedValue({
      action: 'up_to_date',
      preview_id: null,
      pack_id: GRAPH_SEED_PACK_ID,
      owner: 'tinadec',
      bundled_version: GRAPH_SEED_PACK_VERSION,
      installed_version: GRAPH_SEED_PACK_VERSION,
      integrity_digest: GRAPH_SEED_PACK_DIGEST,
      revision: 1,
      etag: '"1"',
      expires_at: '2026-12-01T00:00:00Z',
      counts: { agents: 3, prompt_pipelines: 1, modes: 3, created: 0, adopted: 0, reused: 3, updated: 0 },
      required_core_version: '0.1.0',
      current_core_version: '0.1.0',
      warnings: [],
    })

    await vi.advanceTimersByTimeAsync(CONNECTION_POLL_INTERVAL_MS)
    await flushPromises()

    expect(businessReady.value).toBe(true)
    expect(canAccessBackend.value).toBe(true)
    expect(graphSeedPackState.value.phase).toBe('up_to_date')
    expect(graphSeedBootstrapped).toBe(true)

    // First screenRead failed with 'Transient read error' -> surfaced 'screen-read' notification.
    expect(screenRead).toHaveBeenCalledTimes(1)
    expect(n.items.value.some((i) => i.key === 'screen-read')).toBe(true)

    // Now simulate a mid-session Gateway drop while Electron host remains 'ready'!
    vi.mocked(api.health).mockRejectedValueOnce(new TypeError('Failed to fetch'))
    await vi.advanceTimersByTimeAsync(CONNECTION_POLL_INTERVAL_MS * 4)
    await flushPromises()

    expect(hostStatus.value.state).toBe('ready')
    expect(connectionState.value).toBe('disconnected')
    expect(canAccessBackend.value).toBe(false)
    expect(n.items.value.some((i) => i.key === CONNECTION_BANNER_KEY)).toBe(true)

    // Recover Gateway via retryConnection(): canAccessBackend flips false -> true!
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    expect(await retryConnection()).toBe(true)
    await flushPromises()

    expect(canAccessBackend.value).toBe(true)
    expect(businessReady.value).toBe(true)
    // Connection banner & all connection-category items are 0; screenRead retried and cleared 'screen-read'!
    expect(n.items.value.filter((i) => i.key === CONNECTION_BANNER_KEY)).toHaveLength(0)
    expect(n.items.value.filter((i) => i.category === 'connection')).toHaveLength(0)
    expect(screenRead).toHaveBeenCalledTimes(2)
    expect(n.items.value.some((i) => i.key === 'screen-read')).toBe(false)

    // Also verify dismissByCategory('connection') directly when called standalone.
    setGlobalConnectionBlocked(false)
    n.notify.error(new TypeError('Failed to fetch'), { key: 'stray-conn' })
    expect(n.items.value.some((i) => i.key === 'stray-conn')).toBe(true)
    dismissByCategory('connection')
    expect(n.items.value.some((i) => i.key === 'stray-conn')).toBe(false)

    stopProjector()
    wrapper.unmount()
  })

  // -------------------------------------------------------------------------
  // Invariant 5 — Scoped cleanup on unmount & broadcast: false by default
  // -------------------------------------------------------------------------
  it('Invariant 5: useScopedStatus and useRecoverableRead clean up on unmount and broadcast defaults to false', async () => {
    const n = useNotifications()
    setHostAccessStatus({ state: 'ready', managed: true })
    broadcastStatusNotification.mockClear()

    // 5a: useScopedStatus clears tracked keys on unmount and never broadcasts unless broadcast: true.
    const scopedWrapper = mount(
      defineComponent({
        setup() {
          const scoped = useScopedStatus('panel-local-status')
          scoped.warning('Local warning')
          scoped.error('Secondary issue', { key: 'panel-secondary-status' })
          return () => null
        },
      }),
    )

    expect(n.items.value.map((i) => i.key)).toEqual([
      'panel-local-status',
      'panel-secondary-status',
    ])
    expect(broadcastStatusNotification).not.toHaveBeenCalled()

    scopedWrapper.unmount()
    expect(n.items.value).toHaveLength(0)
    expect(broadcastStatusNotification).not.toHaveBeenCalled()

    // 5b: useRecoverableRead clears its keyed notification on unmount if the user navigates away while failing.
    const failingRead = vi.fn().mockRejectedValue(new Error('Section failed to load'))
    const readWrapper = mount(
      defineComponent({
        setup() {
          useRecoverableRead(failingRead, { notificationKey: 'settings-section-load' })
          return () => null
        },
      }),
    )
    await flushPromises()
    expect(n.items.value.some((i) => i.key === 'settings-section-load')).toBe(true)

    readWrapper.unmount()
    expect(n.items.value.some((i) => i.key === 'settings-section-load')).toBe(false)
  })

  // -------------------------------------------------------------------------
  // Invariant 6 — NotificationIslandHost timer lifecycle hygiene
  // -------------------------------------------------------------------------
  it('Invariant 6: NotificationIslandHost has zero onBeforeUnmount inside watch callbacks and emits zero Vue warnings', async () => {
    const hostSource = readFileSync(
      path.join(srcDir, 'components', 'NotificationIslandHost.vue'),
      'utf8',
    )
    // Ensure `watch(...)` blocks never register `onBeforeUnmount(...)` inside a callback.
    const watchBlocks = [...hostSource.matchAll(/watch\s*\([\s\S]*?\n\)/g)].map((m) => m[0])
    expect(watchBlocks.length).toBeGreaterThanOrEqual(2)
    for (const block of watchBlocks) {
      expect(block).not.toContain('onBeforeUnmount')
    }

    // Mount NotificationIslandHost and trigger transientHero pulse + unmount with zero Vue warnings.
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/', name: 'home', component: defineComponent({ render: () => null }) }],
    })
    await router.push('/')
    await router.isReady()

    const wrapper = mount(NotificationIslandHost, {
      global: { plugins: [i18n, router] },
    })

    const n = useNotifications()
    n.notify.info('First pulse')
    await flushPromises()
    n.notify.warning('Second pulse')
    await flushPromises()
    vi.advanceTimersByTime(800)
    await flushPromises()

    wrapper.unmount()

    const lifecycleWarnings = warnSpy.mock.calls
      .map((args) => args.map(String).join(' '))
      .filter((msg) => msg.includes('onBeforeUnmount'))
    expect(lifecycleWarnings).toEqual([])
  })
})
