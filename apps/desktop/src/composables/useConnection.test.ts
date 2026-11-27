import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'

vi.mock('@/api', () => ({
  api: {
    health: vi.fn(),
  },
}))

import { api } from '@/api'
import {
  useConnection,
  __resetConnectionForTests,
  CONNECTION_TIMEOUT_MS,
  CONNECTION_POLL_INTERVAL_MS,
  retryConnection,
} from './useConnection'

describe('useConnection', () => {
  let hostListener: ((status: import('@/lib/hostConnection').HostConnectionStatus) => void) | undefined
  let getHostStatus: ReturnType<typeof vi.fn>
  let retryHost: ReturnType<typeof vi.fn>
  beforeEach(() => {
    __resetConnectionForTests()
    vi.useFakeTimers()
    vi.clearAllMocks()
    hostListener = undefined
    getHostStatus = vi.fn().mockResolvedValue({ state: 'ready', managed: true })
    retryHost = vi.fn().mockResolvedValue({ state: 'ready', managed: true })
    vi.stubGlobal('window', { tinadec: {
      getHostStatus,
      retryHostConnection: retryHost,
      onHostStatusChanged: (callback: typeof hostListener) => { hostListener = callback; return () => { hostListener = undefined } },
    } })
  })

  afterEach(() => {
    __resetConnectionForTests()
    vi.unstubAllGlobals()
    vi.useRealTimers()
  })

  it('starts in connecting state', () => {
    const { connectionState } = useConnection()
    expect(connectionState.value).toBe('connecting')
  })

  it('exits startup immediately for a missing host IPC and stops ineffective retries', async () => {
    getHostStatus.mockRejectedValue(new Error("Error invoking remote method 'tinadec:host-status': Error: No handler registered for 'tinadec:host-status'"))
    const { start, connectionState, hostStatus, businessReady } = useConnection()
    await start()
    expect(connectionState.value).toBe('host_restart_required')
    expect(hostStatus.value.error?.code).toBe('desktop_restart_required')
    expect(businessReady.value).toBe(false)
    await vi.advanceTimersByTimeAsync(CONNECTION_TIMEOUT_MS * 3)
    expect(await retryConnection()).toBe(false)
    expect(connectionState.value).toBe('host_restart_required')
    expect(getHostStatus).toHaveBeenCalledTimes(1)
    expect(retryHost).not.toHaveBeenCalled()
    expect(api.health).not.toHaveBeenCalled()
  })
  it('revokes a previously ready host when the local status bridge fails', async () => {
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { start, connectionState, businessReady } = useConnection()
    await start()
    expect(businessReady.value).toBe(true)
    getHostStatus.mockRejectedValue(new Error('private-local-ipc-failure'))
    await vi.advanceTimersByTimeAsync(CONNECTION_POLL_INTERVAL_MS * 4)
    expect(connectionState.value).toBe('host_unavailable')
    expect(businessReady.value).toBe(false)
    getHostStatus.mockResolvedValue({ state: 'ready', managed: true })
    await vi.advanceTimersByTimeAsync(CONNECTION_POLL_INTERVAL_MS * 4)
    expect(businessReady.value).toBe(true)
  })
  it('classifies a missing manual retry handler without converting it to preview', async () => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true })
    retryHost.mockRejectedValue(new Error("Error invoking remote method 'tinadec:host-retry': Error: No handler registered for 'tinadec:host-retry'"))
    const { start, connectionState } = useConnection()
    await start()
    expect(await retryConnection()).toBe(false)
    expect(connectionState.value).toBe('host_restart_required')
    await vi.advanceTimersByTimeAsync(CONNECTION_TIMEOUT_MS)
    expect(retryHost).toHaveBeenCalledTimes(1)
    expect(getHostStatus).toHaveBeenCalledTimes(1)
  })

  it('transitions to connected when first health probe succeeds', async () => {
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { connectionState, start } = useConnection()
    await start()
    expect(connectionState.value).toBe('connected')
  })

  it('stays connecting when first probe fails, then connected on retry', async () => {
    let calls = 0
    vi.mocked(api.health).mockImplementation(async () => {
      calls++
      if (calls < 2) throw new Error('Cannot connect to backend')
      return { status: 'ok' }
    })
    const { connectionState, start } = useConnection()
    await start()
    expect(connectionState.value).toBe('connecting')

    // Advance past one poll interval; flush the async probe
    await vi.advanceTimersByTimeAsync(CONNECTION_POLL_INTERVAL_MS)
    expect(connectionState.value).toBe('connected')
  })

  it('transitions to timeout after 30s of failed probes', async () => {
    vi.mocked(api.health).mockRejectedValue(new Error('Cannot connect to backend'))
    const { connectionState, start } = useConnection()
    await start()
    expect(connectionState.value).toBe('connecting')

    await vi.advanceTimersByTimeAsync(CONNECTION_TIMEOUT_MS)
    expect(connectionState.value).toBe('timeout')
  })

  it('start() is idempotent (calling twice does not restart polling)', async () => {
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { start } = useConnection()
    await start()
    // Second call should be a no-op (started flag guard)
    await start()
    expect(api.health).toHaveBeenCalledTimes(1)
  })

  it('timeout does not override connected state', async () => {
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { connectionState, start } = useConnection()
    await start()
    // Already connected; advancing past timeout should not change state
    await vi.advanceTimersByTimeAsync(CONNECTION_TIMEOUT_MS)
    expect(connectionState.value).toBe('connected')
  })

  it('healthy services do not admit business requests when host authentication is unavailable', async () => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true, error: { code: 'host_identity_unavailable', message: 'retry' } })
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { connectionState, businessReady, start } = useConnection()
    await start()
    expect(connectionState.value).toBe('host_unavailable')
    expect(businessReady.value).toBe(false)
    expect(api.health).not.toHaveBeenCalled()
  })
  it('preview exits the splash without probing user services', async () => {
    getHostStatus.mockResolvedValue({ state: 'preview', managed: false })
    const { connectionState, businessReady, start } = useConnection()
    await start()
    expect(connectionState.value).toBe('preview')
    expect(businessReady.value).toBe(false)
    await vi.advanceTimersByTimeAsync(CONNECTION_TIMEOUT_MS * 2)
    expect(api.health).not.toHaveBeenCalled()
  })
  it('host recovery event admits work only after public liveness succeeds', async () => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true })
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { connectionState, start } = useConnection()
    await start()
    hostListener?.({ state: 'ready', managed: true })
    await vi.waitFor(() => expect(connectionState.value).toBe('connected'))
    hostListener?.({ state: 'rejected', managed: true })
    expect(connectionState.value).toBe('host_rejected')
  })
  it('manual retry invokes real host recovery once even under repeated clicks', async () => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true })
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { start, businessReady } = useConnection()
    await start()
    const first = retryConnection(); const second = retryConnection()
    expect(first).toBe(second)
    expect(await first).toBe(true)
    expect(retryHost).toHaveBeenCalledTimes(1)
    expect(businessReady.value).toBe(true)
  })
  it.each([
    ['rejected', 'host_rejected'],
    ['restart_required', 'host_restart_required'],
  ] as const)('a late ready retry receipt cannot override a newer %s event', async (state, expected) => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true })
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    let release!: (status: import('@/lib/hostConnection').HostConnectionStatus) => void
    retryHost.mockImplementation(() => new Promise(resolve => { release = resolve }))
    const { start, connectionState, hostStatus, businessReady } = useConnection()
    await start()
    const retry = retryConnection()
    hostListener?.({ state, managed: true })
    release({ state: 'ready', managed: true })
    expect(await retry).toBe(false)
    expect(connectionState.value).toBe(expected)
    expect(hostStatus.value.state).toBe(state)
    expect(businessReady.value).toBe(false)
    expect(api.health).not.toHaveBeenCalled()
    expect(retryHost).toHaveBeenCalledOnce()
  })
  it('uses a newer ready event rather than a stale unavailable retry receipt', async () => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true })
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    let release!: (status: import('@/lib/hostConnection').HostConnectionStatus) => void
    retryHost.mockImplementation(() => new Promise(resolve => { release = resolve }))
    const { start, connectionState, hostStatus, businessReady } = useConnection()
    await start()
    const retry = retryConnection()
    hostListener?.({ state: 'ready', managed: true })
    release({ state: 'unavailable', managed: true })
    expect(await retry).toBe(true)
    expect(connectionState.value).toBe('connected')
    expect(hostStatus.value.state).toBe('ready')
    expect(businessReady.value).toBe(true)
  })
  it('does not replace a newer restart requirement with a failed retry receipt', async () => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true })
    let reject!: (error: Error) => void
    retryHost.mockImplementation(() => new Promise((_resolve, fail) => { reject = fail }))
    const { start, connectionState, hostStatus, businessReady } = useConnection()
    await start()
    const retry = retryConnection()
    hostListener?.({ state: 'restart_required', managed: true })
    reject(new Error('Local IPC failed'))
    expect(await retry).toBe(false)
    expect(connectionState.value).toBe('host_restart_required')
    expect(hostStatus.value.state).toBe('restart_required')
    expect(businessReady.value).toBe(false)
    expect(api.health).not.toHaveBeenCalled()
  })
  it('a late readiness read cannot override a newer rejected event', async () => {
    let release!: (value: import('@/lib/hostConnection').HostConnectionStatus) => void
    getHostStatus.mockImplementation(() => new Promise(resolve => { release = resolve }))
    const { start, connectionState } = useConnection()
    const starting = start()
    hostListener?.({ state: 'rejected', managed: true })
    release({ state: 'ready', managed: true })
    await starting
    expect(connectionState.value).toBe('host_rejected')
    expect(api.health).not.toHaveBeenCalled()
  })
  it('a late health response cannot restore a revoked host', async () => {
    let release!: (value: { status: string }) => void
    vi.mocked(api.health).mockImplementation(() => new Promise(resolve => { release = resolve }))
    const { start, connectionState } = useConnection()
    const starting = start()
    await vi.waitFor(() => expect(api.health).toHaveBeenCalled())
    hostListener?.({ state: 'unavailable', managed: true })
    release({ status: 'ok' })
    await starting
    expect(connectionState.value).toBe('host_unavailable')
  })
  it('periodic health watch automatically retries an unavailable host and recovers', async () => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true, error: { code: 'host_identity_unavailable', message: 'retry' } })
    retryHost.mockResolvedValueOnce({ state: 'ready', managed: true })
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { start, connectionState, businessReady } = useConnection()
    await start()
    expect(connectionState.value).toBe('host_unavailable')
    expect(retryHost).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(CONNECTION_POLL_INTERVAL_MS * 4)
    expect(retryHost).toHaveBeenCalledTimes(1)
    expect(connectionState.value).toBe('connected')
    expect(businessReady.value).toBe(true)
  })

  it('backs off repeated unavailable host health-watch polls and resets after recovery', async () => {
    getHostStatus.mockResolvedValue({ state: 'unavailable', managed: true, error: { code: 'host_unavailable', message: 'busy' } })
    retryHost.mockResolvedValue({ state: 'unavailable', managed: true, error: { code: 'host_unavailable', message: 'busy' } })
    const { start, connectionState } = useConnection()
    await start()
    expect(connectionState.value).toBe('host_unavailable')
    const baseMs = CONNECTION_POLL_INTERVAL_MS * 4 // 6000ms
    const initialCalls = getHostStatus.mock.calls.length

    // First watch at +6000ms increments consecutive failures from 1 -> 2 (backoff 12000ms, NOT 1 -> 3 = 24000ms)
    await vi.advanceTimersByTimeAsync(baseMs)
    const afterFirstWatch = getHostStatus.mock.calls.length
    expect(afterFirstWatch).toBeGreaterThan(initialCalls)
    await vi.advanceTimersByTimeAsync(baseMs)
    expect(getHostStatus.mock.calls.length).toBe(afterFirstWatch) // still backed off at +6000ms

    // If probe() had double-incremented (1 -> 3), the second watch would wait 24000ms and stay uncalled at +12000ms.
    getHostStatus.mockResolvedValue({ state: 'ready', managed: true })
    retryHost.mockResolvedValue({ state: 'ready', managed: true })
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    await vi.advanceTimersByTimeAsync(baseMs) // total +12000ms fires exact 2x step
    expect(connectionState.value).toBe('connected')
  })
})
