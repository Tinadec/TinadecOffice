// @vitest-environment happy-dom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'

vi.mock('@/api', () => ({
  api: {
    gatewayUrl: 'http://gateway.test',
    health: vi.fn(),
  },
}))

import i18n from '@/i18n'
import { resetHostAccessForTests } from '@/lib/hostAccess'
import {
  __resetStartupSplashGateForTests,
  armStartupSplashGate,
  openStartupSplashGate,
  SPLASH_CURTAIN_LEAD_IN_MS,
  SPLASH_MAX_WAIT_MS,
  splashFallbackTriggered,
  splashGateOpen,
} from '@/lib/startupSplashGate'
import {
  __resetConnectionForTests,
  CONNECTION_BANNER_KEY,
  syncConnectionNotifications,
  useConnection,
} from '@/composables/useConnection'
import { __resetNotificationsForTests, useNotifications } from '@/composables/useNotifications'
import { useHomeEntrance } from '@/composables/useHomeEntrance'

describe('Startup splash gate & unveiling choreography', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => {
      return setTimeout(() => cb(performance.now()), 16) as unknown as number
    })
    vi.stubGlobal('cancelAnimationFrame', (id: number) => {
      clearTimeout(id as unknown as ReturnType<typeof setTimeout>)
    })
    document.documentElement.dataset.reducedMotion = 'false'
    __resetStartupSplashGateForTests()
    __resetNotificationsForTests()
    __resetConnectionForTests()
    resetHostAccessForTests()
  })

  afterEach(() => {
    delete document.documentElement.dataset.reducedMotion
    __resetStartupSplashGateForTests()
    __resetNotificationsForTests()
    __resetConnectionForTests()
    resetHostAccessForTests()
    vi.unstubAllGlobals()
    vi.useRealTimers()
  })

  it('uses a 15s cold-start splash fallback deadline', () => {
    expect(SPLASH_MAX_WAIT_MS).toBe(15_000)
  })

  it('holds Home entrance in preparing until splash gate opens, waits 140ms lead-in, and skips wait on route return', async () => {
    armStartupSplashGate()
    expect(splashGateOpen.value).toBe(false)

    const readyCalls: number[] = []
    let resolveLayout!: () => void
    const layoutReady = new Promise<void>((resolve) => {
      resolveLayout = resolve
    })

    let entrancePhase!: ReturnType<typeof useHomeEntrance>['phase']
    const wrapper1 = mount(
      defineComponent({
        setup() {
          const root = ref<HTMLElement | null>(null)
          const { phase } = useHomeEntrance(root, layoutReady, () => {
            readyCalls.push(Date.now())
          })
          entrancePhase = phase
          return () => null
        },
      }),
    )

    resolveLayout()
    await flushPromises()
    // Two rAF frames prepare the frame, then onReady fires while still holding in preparing.
    await vi.advanceTimersByTimeAsync(40)
    await flushPromises()

    expect(readyCalls).toHaveLength(1)
    expect(entrancePhase.value).toBe('preparing')

    // Backend connects -> splash gate opens, AppSplash lifts, Home holds briefly for curtain lead-in.
    openStartupSplashGate('ready')
    await flushPromises()
    expect(splashGateOpen.value).toBe(true)
    expect(splashFallbackTriggered.value).toBe(false)
    expect(entrancePhase.value).toBe('preparing')

    await vi.advanceTimersByTimeAsync(SPLASH_CURTAIN_LEAD_IN_MS - 1)
    expect(entrancePhase.value).toBe('preparing')

    await vi.advanceTimersByTimeAsync(1)
    await flushPromises()
    expect(['entering', 'settled']).toContain(entrancePhase.value)

    wrapper1.unmount()

    // Returning to Home later must not wait on the startup splash gate or lead-in delay.
    let entrancePhase2!: ReturnType<typeof useHomeEntrance>['phase']
    const wrapper2 = mount(
      defineComponent({
        setup() {
          const root = ref<HTMLElement | null>(null)
          const { phase } = useHomeEntrance(root, Promise.resolve(), () => {
            readyCalls.push(Date.now())
          })
          entrancePhase2 = phase
          return () => null
        },
      }),
    )

    await flushPromises()
    await vi.advanceTimersByTimeAsync(40)
    await flushPromises()
    expect(readyCalls).toHaveLength(2)
    expect(['entering', 'settled']).toContain(entrancePhase2.value)
    wrapper2.unmount()
  })

  it('shows connecting-to-backend immediately in Dynamic Island on 15s fallback and clears on connect', () => {
    armStartupSplashGate()
    const n = useNotifications()
    const { connectionState, hostStatus } = useConnection()

    // Case 1: Slow host still connecting when 15s expires.
    connectionState.value = 'connecting'
    hostStatus.value = { state: 'checking', managed: true }
    openStartupSplashGate('fallback')
    expect(splashFallbackTriggered.value).toBe(true)

    syncConnectionNotifications({ coldStartConnecting: true })
    expect(n.items.value).toHaveLength(1)
    expect(n.items.value[0]?.key).toBe(CONNECTION_BANNER_KEY)
    expect(n.items.value[0]?.title).toBe(i18n.global.t('app.connectingBackend'))

    // Case 2: Host verification reported temporary unavailability during Core cold compile.
    connectionState.value = 'host_unavailable'
    hostStatus.value = { state: 'unavailable', managed: true }
    syncConnectionNotifications({ coldStartConnecting: true })
    expect(n.items.value).toHaveLength(1)
    expect(n.items.value[0]?.title).toBe(i18n.global.t('app.connectingBackend'))

    // Once backend + host connect, the Dynamic Island status capsule dismisses cleanly.
    connectionState.value = 'connected'
    hostStatus.value = { state: 'ready', managed: true }
    syncConnectionNotifications({ coldStartConnecting: false })
    expect(n.items.value).toHaveLength(0)
  })
})
