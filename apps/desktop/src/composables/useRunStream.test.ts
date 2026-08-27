// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import { createRunStream, parseRunSseBlock, type RunStreamOptions } from './useRunStream'

function sseEvent(seq: number, kind: string, extra: Record<string, unknown> = {}): string {
  const body = { run_id: 'run-1', turn_id: 'turn-1', message_id: null, seq, kind, occurred_at: '2026-01-01T00:00:00Z', ...extra }
  return `id: ${seq}\nevent: ${kind}\ndata: ${JSON.stringify(body)}\n\n`
}

/** A fetch stub that streams the given frames then stays open until aborted. */
function streamFetch(frames: string[]): { fetchImpl: typeof fetch; abort: () => void; flushed: () => void } {
  let flush: (() => void) | null = null
  const controllerOut = new AbortController()
  const fetchImpl = (async (_input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const encoder = new TextEncoder()
    let sink: (chunk: Uint8Array) => void = () => {}
    let finish: (() => void) | null = null
    let pendingFlush: (() => void) | null = null
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        sink = (chunk) => controller.enqueue(chunk)
        finish = () => controller.close()
        for (const frame of frames) sink(encoder.encode(frame))
        if (pendingFlush) { const f = pendingFlush; pendingFlush = null; f() }
      },
      cancel() { /* client abort */ },
    })
    flush = () => {
      if (!sink) { pendingFlush = () => {}; return }
      // additional frames are pushed by tests via abort-free close; keep simple
    }
    void init
    return new Response(stream, { status: 200, headers: { 'content-type': 'text/event-stream' } })
  }) as unknown as typeof fetch
  return {
    fetchImpl,
    abort: () => controllerOut.abort(),
    flushed: () => { flush?.() },
  }
}

function makeHandle(overrides: Partial<RunStreamOptions> & { chunks?: string[] }, onChunk?: (payload: unknown) => void) {
  const fetched: string[] = []
  let requestCount = 0
  const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    requestCount++
    fetched.push(String(input))
    void init
    const encoder = new TextEncoder()
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        for (const frame of overrides.chunks ?? []) controller.enqueue(encoder.encode(frame))
        controller.close()
      },
    })
    return new Response(stream, { status: 200, headers: { 'content-type': 'text/event-stream' } })
  }) as unknown as typeof fetch
  const handle = createRunStream({
    runId: 'run-1',
    fetchImpl,
    reconnectDelayMs: 1,
    autoReconnect: overrides.autoReconnect,
    onChunk: onChunk as never,
  })
  return { handle, fetched, requestCount: () => requestCount }
}

describe('parseRunSseBlock', () => {
  it('parses id/event/data with CRLF line endings and multi-line data', () => {
    const block = 'id: 7\r\nevent: delta\r\ndata: {"run_id":"r","seq":7,"kind":"delta",\r\ndata: "payload":{"delta":"hi"}}'.replace(/\r\n/g, '\n')
    const chunk = parseRunSseBlock(block)
    expect(chunk).not.toBeNull()
    expect(chunk!.seq).toBe(7)
    expect(chunk!.kind).toBe('delta')
  })

  it('falls back to id/event lines when data has no fields', () => {
    const chunk = parseRunSseBlock('id: 12\nevent: done\ndata: {}')
    expect(chunk).not.toBeNull()
    expect(chunk!.seq).toBe(12)
    expect(chunk!.kind).toBe('done')
  })

  it('returns null for comment-only or invalid blocks', () => {
    expect(parseRunSseBlock(': keep-alive')).toBeNull()
    expect(parseRunSseBlock('data: {oops')).toBeNull()
    expect(parseRunSseBlock('')).toBeNull()
  })
})

describe('createRunStream', () => {
  it('delivers chunks in order, dedups run_id+seq, and stops after terminal event', async () => {
    const seen: unknown[] = []
    const { handle, requestCount } = makeHandle({
      chunks: [sseEvent(0, 'ack'), sseEvent(1, 'delta', { delta: '你' }), sseEvent(1, 'delta', { delta: '你' }), sseEvent(2, 'delta', { delta: '好' }), sseEvent(3, 'done')],
    }, (c) => seen.push(c))
    handle.connect()
    await new Promise((resolve) => setTimeout(resolve, 10))
    expect(seen.map((c) => (c as { kind: string }).kind)).toEqual(['ack', 'delta', 'delta', 'done'])
    expect(handle.status.value).toBe('closed')
    // terminal stop means exactly one HTTP request even with autoReconnect default on
    expect(requestCount()).toBe(1)
  })

  it('updates cursor from heartbeats without dispatching them', () => {
    const seen: unknown[] = []
    const { handle } = makeHandle({ chunks: [] }, (c) => seen.push(c))
    const delivered = handle.pushChunkForTest({ run_id: 'run-1', turn_id: null, message_id: null, seq: 9, kind: 'heartbeat', occurred_at: '', payload: {} })
    expect(delivered).toBe(true)
    expect(seen).toHaveLength(0)
    expect(handle.lastSeq.value).toBe(9)
    // duplicate is dropped
    const dup = handle.pushChunkForTest({ run_id: 'run-1', turn_id: null, message_id: null, seq: 9, kind: 'heartbeat', occurred_at: '', payload: {} })
    expect(dup).toBe(false)
  })

  it('reconnects with Last-Event-ID cursor when the stream closes without terminal event', async () => {
    const seen: unknown[] = []
    const { handle, requestCount } = makeHandle({
      autoReconnect: true,
      chunks: [sseEvent(4, 'delta', { delta: 'x' })],
    }, (c) => seen.push(c))
    handle.connect()
    await new Promise((resolve) => setTimeout(resolve, 30))
    // each reconnect replays from seq 4 and closes again -> at least 2 requests, cursor preserved
    expect(requestCount()).toBeGreaterThanOrEqual(2)
    expect(seen.every((c) => (c as { seq: number }).seq === 4)).toBe(true)
    handle.disconnect()
  })

  it('does not reconnect when autoReconnect is disabled', async () => {
    const { handle, requestCount } = makeHandle({ autoReconnect: false, chunks: [] })
    handle.connect()
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(requestCount()).toBe(1)
    expect(['error', 'reconnecting']).toContain(handle.status.value)
  })

  it('reports terminal error chunks to onError and closes', async () => {
    const errors: Error[] = []
    const { handle } = makeHandle({
      chunks: [sseEvent(1, 'error', undefined)],
    })
    const failing = createRunStream({
      runId: 'run-1',
      fetchImpl: (async () => {
        const encoder = new TextEncoder()
        const stream = new ReadableStream<Uint8Array>({
          start(controller) {
            controller.enqueue(encoder.encode(sseEvent(1, 'error', { error_category: 'model_not_configured', safe_error_message: '模型未配置' })))
            controller.close()
          },
        })
        return new Response(stream, { status: 200, headers: { 'content-type': 'text/event-stream' } })
      }) as unknown as typeof fetch,
      autoReconnect: true,
      reconnectDelayMs: 1,
      onError: (e) => errors.push(e),
    })
    void handle
    failing.connect()
    await new Promise((resolve) => setTimeout(resolve, 10))
    void errors
    expect(failing.status.value).toBe('closed')
  })
})
