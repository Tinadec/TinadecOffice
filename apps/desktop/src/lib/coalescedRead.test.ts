import { afterEach, describe, expect, it, vi } from 'vitest'
import { createCoalescedRead } from './coalescedRead'
afterEach(() => vi.useRealTimers())
describe('coalesced reads', () => {
  it('collapses a burst and keeps one follow-up while in flight', async () => {
    vi.useFakeTimers()
    let finish!: () => void
    const read = vi.fn().mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve })).mockResolvedValue(undefined)
    const queue = createCoalescedRead(read)
    const first = Array.from({ length: 100 }, () => queue.request('activity'))
    await vi.advanceTimersByTimeAsync(40)
    expect(read).toHaveBeenCalledTimes(1)
    const next = Array.from({ length: 100 }, () => queue.request('messages', 'activity'))
    await vi.advanceTimersByTimeAsync(40)
    expect(read).toHaveBeenCalledTimes(1)
    finish(); await Promise.all(first); await vi.advanceTimersByTimeAsync(40); await Promise.all(next)
    expect(read).toHaveBeenCalledTimes(2)
    expect([...read.mock.calls[1]![0]]).toEqual(['messages', 'activity'])
  })
  it('starts the new owner without waiting for an uncancellable old read', async () => {
    vi.useFakeTimers()
    let finish!: () => void
    const read = vi.fn().mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve })).mockResolvedValue(undefined)
    const queue = createCoalescedRead(read)
    const old = queue.request('old'); await vi.advanceTimersByTimeAsync(40)
    queue.reset()
    const latest = queue.request('latest'); await vi.advanceTimersByTimeAsync(40); await latest
    expect(read).toHaveBeenCalledTimes(2)
    finish(); await old
  })
})
