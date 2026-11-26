// @vitest-environment happy-dom
import { afterEach, expect, it, vi } from 'vitest'
import { ScopedEventSource } from './scopedEventSource'
import { setSelectedStorage } from './storageScope'
import { setHostAccessStatus } from './hostAccess'

const originalHost = window.tinadec
const ready = { state: 'ready' as const, managed: true }
const RUN_A = 'a'.repeat(32)
const RUN_B = 'b'.repeat(32)
afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: originalHost })
  setHostAccessStatus(ready)
})
it('reconnects with the original storage id and replay cursor', async () => {
  vi.useFakeTimers()
  const getHostStatus = vi.fn(async () => ready)
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus } })
  const calls: RequestInit[] = []
  const fakeFetch = (async (_url: RequestInfo | URL, init?: RequestInit) => {
    calls.push(init ?? {})
    return new Response(`id: ${RUN_A}:9\nevent: tool.execution.completed\ndata: {"id":"result"}\n\n`, { headers: { 'content-type': 'text/event-stream' } })
  }) as typeof fetch
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'old-scope', fakeFetch)
  const received: string[] = []
  source.addEventListener('tool.execution.completed', event => received.push((event as MessageEvent).data))
  await vi.advanceTimersByTimeAsync(0)
  setSelectedStorage('new-scope')
  await vi.advanceTimersByTimeAsync(1000)
  source.close()
  expect(received.length).toBeGreaterThanOrEqual(1)
  expect(calls).toHaveLength(2)
  expect(getHostStatus).toHaveBeenCalledTimes(2)
  for (const call of calls) expect(new Headers(call.headers).get('x-tinadec-storage-id')).toBe('old-scope')
  expect(new Headers(calls[1]?.headers).get('last-event-id')).toBe(`${RUN_A}:9`)
})
it('checks admission before the first fetch and refuses preview retries', async () => {
  vi.useFakeTimers()
  const getHostStatus = vi.fn(async () => ({ state: 'preview' as const, managed: false }))
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus } })
  // A preview bridge probes the Gateway's own web-auth status before admission
  // (hostConnection.readHostStatus -> probeWebAuthStatus). Stub it: an unstubbed probe is real
  // network I/O that never settles under fake timers, which hides the retry this test is about.
  const probeFetch = vi.fn<typeof fetch>(async () => new Response('', { status: 503 }))
  vi.stubGlobal('fetch', probeFetch)
  const fakeFetch = vi.fn<typeof fetch>()
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch)
  const errors = vi.fn(); source.onerror = errors
  await vi.advanceTimersByTimeAsync(0)
  await vi.advanceTimersByTimeAsync(1000)
  source.close()
  expect(getHostStatus).toHaveBeenCalledTimes(2)
  expect(fakeFetch).not.toHaveBeenCalled()
  expect(errors).toHaveBeenCalledTimes(2)
  // Only the pre-admission web-auth probe may reach the network; the event stream must not.
  for (const [input] of probeFetch.mock.calls) expect(String(input)).toContain('/api/v1/web-auth/status')
})
it('rechecks revoked host access on reconnect and preserves the cursor for recovery', async () => {
  vi.useFakeTimers()
  const getHostStatus = vi.fn().mockResolvedValueOnce(ready)
    .mockResolvedValueOnce({ state: 'unavailable', managed: true, error: { code: 'host_unavailable', message: 'Host disconnected' } })
    .mockResolvedValueOnce(ready)
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus } })
  const fakeFetch = vi.fn<typeof fetch>().mockImplementation(async () => new Response(`id: ${RUN_A}:9\ndata: {"id":"result"}\n\n`))
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'original-scope', fakeFetch)
  await vi.advanceTimersByTimeAsync(0)
  expect(fakeFetch).toHaveBeenCalledOnce()
  await vi.advanceTimersByTimeAsync(1000)
  expect(fakeFetch).toHaveBeenCalledOnce()
  await vi.advanceTimersByTimeAsync(1000)
  source.close()
  expect(getHostStatus).toHaveBeenCalledTimes(3)
  expect(fakeFetch).toHaveBeenCalledTimes(2)
  const recovered = fakeFetch.mock.calls[1]?.[1]
  expect(new Headers(recovered?.headers).get('x-tinadec-storage-id')).toBe('original-scope')
  expect(new Headers(recovered?.headers).get('last-event-id')).toBe(`${RUN_A}:9`)
})
it('never fetches after closing while host admission is pending', async () => {
  vi.useFakeTimers()
  let finish!: (status: typeof ready) => void
  const getHostStatus = vi.fn(() => new Promise(resolve => { finish = resolve }))
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus } })
  const fakeFetch = vi.fn<typeof fetch>()
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch)
  source.close(); finish(ready)
  await vi.advanceTimersByTimeAsync(2000)
  expect(getHostStatus).toHaveBeenCalledOnce()
  expect(fakeFetch).not.toHaveBeenCalled()
  expect(source.readyState).toBe(2)
})
it('cancels a late fetch body after closing without dispatching or reconnecting', async () => {
  vi.useFakeTimers()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus: async () => ready } })
  let finish!: (response: Response) => void
  const fakeFetch = vi.fn<typeof fetch>(() => new Promise(resolve => { finish = resolve }))
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch)
  const received = vi.fn(); source.onmessage = received
  await vi.advanceTimersByTimeAsync(0)
  const signal = fakeFetch.mock.calls[0]?.[1]?.signal
  source.close()
  const cancel = vi.fn()
  finish(new Response(new ReadableStream({
    start(controller) { controller.enqueue(new TextEncoder().encode('data: {"late":true}\n\n')) },
    cancel,
  })))
  await vi.advanceTimersByTimeAsync(2000)
  expect(signal?.aborted).toBe(true)
  expect(cancel).toHaveBeenCalledOnce()
  expect(received).not.toHaveBeenCalled()
  expect(fakeFetch).toHaveBeenCalledOnce()
})
it('dispatches CRLF frames whose delimiter is split across reads', async () => {
  const encoder = new TextEncoder()
  const stream = new ReadableStream<Uint8Array>({ start(controller) {
    controller.enqueue(encoder.encode(`id: ${RUN_A}:2\r\ndata: {"message":"hello"}\r`))
    controller.enqueue(encoder.encode('\n\r\n'))
  } })
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', (async () => new Response(stream)) as typeof fetch)
  const received: string[] = []
  source.onmessage = event => received.push(event.data)
  for (let i = 0; i < 8; i++) await Promise.resolve()
  source.close()
  expect(received).toEqual(['{"message":"hello"}'])
})
it('calls the injected fetch without binding it to the event source', async () => {
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus: async () => ready } })
  let receiver: unknown
  const fakeFetch = function (this: unknown) {
    receiver = this
    return Promise.resolve(new Response(`id: ${RUN_A}:1\nevent: worker.completed\ndata: {"ok":true}\n\n`))
  } as unknown as typeof fetch
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch)
  const received = vi.fn()
  source.addEventListener('worker.completed', received)
  for (let i = 0; i < 12; i++) await Promise.resolve()
  source.close()
  expect(receiver).not.toBe(source)
  expect(received).toHaveBeenCalledOnce()
})
it('drops a silent half-open stream after the idle timeout and resumes from its cursor', async () => {
  vi.useFakeTimers()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus: vi.fn(async () => ready) } })
  const calls: RequestInit[] = []
  // The first response sends one frame and then nothing, ever: no heartbeat, no close.
  const fakeFetch = (async (_url: RequestInfo | URL, init?: RequestInit) => {
    calls.push(init ?? {})
    const signal = init?.signal
    return new Response(new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode(`id: ${RUN_A}:7\ndata: {"id":"first"}\n\n`))
        signal?.addEventListener('abort', () => controller.error(new DOMException('aborted', 'AbortError')))
      },
    }))
  }) as typeof fetch
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch, 3000)
  await vi.advanceTimersByTimeAsync(2900)
  expect(calls).toHaveLength(1)
  await vi.advanceTimersByTimeAsync(100 + 1000)
  source.close()
  expect(calls).toHaveLength(2)
  expect(new Headers(calls[1]?.headers).get('last-event-id')).toBe(`${RUN_A}:7`)
})
it('merges scalar frames from two runs into one resume vector without a heartbeat', async () => {
  vi.useFakeTimers()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus: vi.fn(async () => ready) } })
  const calls: RequestInit[] = []
  // No heartbeat between the frames: the client must accumulate both runs from scalar ids alone,
  // otherwise a reconnect replays the run it did not last hear from.
  const fakeFetch = (async (_url: RequestInfo | URL, init?: RequestInit) => {
    calls.push(init ?? {})
    return new Response(
      `id: ${RUN_A}:1\ndata: {"run":"a"}\n\n` +
      `id: ${RUN_B}:1\ndata: {"run":"b"}\n\n` +
      `id: ${RUN_A}:2\ndata: {"run":"a"}\n\n`,
      { headers: { 'content-type': 'text/event-stream' } })
  }) as typeof fetch
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch)
  await vi.advanceTimersByTimeAsync(0)
  await vi.advanceTimersByTimeAsync(1000)
  source.close()
  expect(calls).toHaveLength(2)
  const resumed = new Headers(calls[1]?.headers).get('last-event-id') ?? ''
  expect(resumed.split(',').sort()).toEqual([`${RUN_A}:2`, `${RUN_B}:1`].sort())
})
it('replaces the whole vector when a heartbeat carries one', async () => {
  vi.useFakeTimers()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus: vi.fn(async () => ready) } })
  const calls: RequestInit[] = []
  const RUN_C = 'c'.repeat(32)
  const fakeFetch = (async (_url: RequestInfo | URL, init?: RequestInit) => {
    calls.push(init ?? {})
    return new Response(
      `id: ${RUN_A}:9\ndata: {"run":"a"}\n\n` +
      `id: ${RUN_B}:4,${RUN_C}:2\nevent: heartbeat\ndata: {"after_seq":4,"cursor":"${RUN_B}:4,${RUN_C}:2"}\n\n`,
      { headers: { 'content-type': 'text/event-stream' } })
  }) as typeof fetch
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch)
  await vi.advanceTimersByTimeAsync(0)
  await vi.advanceTimersByTimeAsync(1000)
  source.close()
  // The heartbeat is authoritative: a run it omits is no longer part of the session's vector.
  expect(new Headers(calls[1]?.headers).get('last-event-id')).toBe(`${RUN_B}:4,${RUN_C}:2`)
})
it('drops a rejected cursor instead of retrying with it forever', async () => {
  vi.useFakeTimers()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus: vi.fn(async () => ready) } })
  const calls: RequestInit[] = []
  const fakeFetch = (async (_url: RequestInfo | URL, init?: RequestInit) => {
    calls.push(init ?? {})
    if (calls.length === 1) return new Response(`id: ${RUN_A}:3\ndata: {"run":"a"}\n\n`)
    if (calls.length === 2) return new Response('{"code":"INVALID_EVENT_CURSOR"}', { status: 400 })
    return new Response('data: {"run":"a"}\n\n')
  }) as typeof fetch
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch)
  await vi.advanceTimersByTimeAsync(0)
  await vi.advanceTimersByTimeAsync(1000)
  expect(new Headers(calls[1]?.headers).get('last-event-id')).toBe(`${RUN_A}:3`)
  await vi.advanceTimersByTimeAsync(1000)
  source.close()
  expect(calls).toHaveLength(3)
  expect(new Headers(calls[2]?.headers).get('last-event-id')).toBeNull()
})
it('applies exponential backoff on repeated connection failures and normalizes split CRLF chunks', async () => {
  vi.useFakeTimers()
  vi.spyOn(Math, 'random').mockReturnValue(0.5)
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus: vi.fn(async () => ready) } })
  let calls = 0
  const encoder = new TextEncoder()
  const fakeFetch = vi.fn<typeof fetch>(async () => {
    calls++
    if (calls < 4) return new Response('unavailable', { status: 503 })
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoder.encode('event: ping\r'))
        controller.enqueue(encoder.encode('\ndata: {"ok":true}\r\n\r\n'))
        controller.close()
      },
    })
    return new Response(body, { headers: { 'content-type': 'text/event-stream' } })
  })
  const source = new ScopedEventSource('http://gateway.local/api/v1/events', 'scope', fakeFetch)
  const received: string[] = []
  source.addEventListener('ping', event => received.push((event as MessageEvent).data))

  await vi.advanceTimersByTimeAsync(0)
  expect(calls).toBe(1) // attempt 0 -> 1000ms
  await vi.advanceTimersByTimeAsync(1000)
  expect(calls).toBe(2) // attempt 1 -> 1000ms
  await vi.advanceTimersByTimeAsync(1000)
  expect(calls).toBe(3) // attempt 2 -> 2000ms (with random=0.5 -> factor 1.0)
  await vi.advanceTimersByTimeAsync(1500)
  expect(calls).toBe(3) // still backing off before 2000ms
  await vi.advanceTimersByTimeAsync(500)
  expect(calls).toBe(4)
  expect(received).toEqual(['{"ok":true}'])
  source.close()
})
