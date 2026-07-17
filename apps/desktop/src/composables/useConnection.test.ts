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
} from './useConnection'

describe('useConnection', () => {
  beforeEach(() => {
    __resetConnectionForTests()
    vi.useFakeTimers()
    vi.clearAllMocks()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('starts in connecting state', () => {
    const { connectionState } = useConnection()
    expect(connectionState.value).toBe('connecting')
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
})
