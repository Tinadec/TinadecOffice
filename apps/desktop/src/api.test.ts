// @vitest-environment happy-dom
// api.ts reads `window.tinadec?.gatewayUrl?.()` at module top level, so tests need a DOM-ish global.
import { describe, expect, it, afterEach, vi } from 'vitest'
import { api, normalizeEventEnvelope, type EventEnvelope, type ModelStreamChunkDto } from './api'

describe('normalizeEventEnvelope', () => {
  it('maps the Core wire shape (event_type/timestamp/version + payload.sequence)', () => {
    const raw = {
      version: '1.0',
      event_id: 'e1',
      event_type: 'message.created',
      timestamp: '2026-08-27T00:00:00Z',
      session_id: 's-1',
      run_id: 'r-1',
      payload: { sequence: 7, content: 'hi' },
    }
    const event = normalizeEventEnvelope(raw)
    expect(event.type).toBe('message.created')
    expect(event.seq).toBe(7)
    expect(event.ts).toBe('2026-08-27T00:00:00Z')
    expect(event.v).toBe('1.0')
    expect(event.payload).toEqual({ sequence: 7, content: 'hi' })
    // Core-only fields pass through untouched.
    expect((event as unknown as Record<string, unknown>).event_id).toBe('e1')
    expect((event as unknown as Record<string, unknown>).run_id).toBe('r-1')
  })

  it('unwraps the durable replay business payload while retaining journal metadata', () => {
    const event = normalizeEventEnvelope({
      version: '1.0',
      event_type: 'task_graph.created',
      timestamp: '2026-09-18T00:00:00Z',
      payload: {
        sequence: 6,
        summary: '1 task(s) planned.',
        severity: 'info',
        payload: {
          run_id: 'r-1',
          plan_revision: 1,
          task_count: 1,
          task_keys: ['responses-write-proof'],
        },
      },
    })

    expect(event.seq).toBe(6)
    expect(event.payload).toMatchObject({
      sequence: 6,
      summary: '1 task(s) planned.',
      severity: 'info',
      run_id: 'r-1',
      task_count: 1,
      task_keys: ['responses-write-proof'],
    })
  })

  it('passes the legacy/mock top-level shape (type/seq/ts) through unchanged', () => {
    const legacy = { v: '1.0', type: 'run.started', seq: 3, ts: '2026-01-01T00:00:00Z', request_id: 'req', trace_id: 'tr', capabilities: ['a'], payload: { sequence: 99 } }
    const event = normalizeEventEnvelope(legacy)
    expect(event.type).toBe('run.started')
    expect(event.seq).toBe(3)
    expect(event.ts).toBe('2026-01-01T00:00:00Z')
    expect(event.request_id).toBe('req')
    expect(event.trace_id).toBe('tr')
    expect(event.capabilities).toEqual(['a'])
  })

  it('falls back to lastEventId when payload.sequence is missing, and to 0 when both are', () => {
    const raw = { event_type: 'task.assigned', timestamp: '2026-08-27T00:00:00Z', payload: {} }
    expect(normalizeEventEnvelope(raw, '9').seq).toBe(9)
    expect(normalizeEventEnvelope(raw).seq).toBe(0)
  })

  it('coerces string sequence values and rejects non-numeric ones', () => {
    expect(normalizeEventEnvelope({ payload: { sequence: '3' } }).seq).toBe(3)
    expect(normalizeEventEnvelope({ payload: { sequence: 'abc' } }).seq).toBe(0)
    expect(normalizeEventEnvelope({ payload: { sequence: 'abc' } }, '5').seq).toBe(5)
  })

  it('replaces malformed payloads (null/array/string) with an empty object', () => {
    expect(normalizeEventEnvelope({ payload: null }).payload).toEqual({})
    expect(normalizeEventEnvelope({ payload: [1, 2] }).payload).toEqual({})
    expect(normalizeEventEnvelope({ payload: 'oops' }).payload).toEqual({})
  })

  it('never returns undefined type/seq/ts/v even for an empty object', () => {
    const event = normalizeEventEnvelope({})
    expect(event.type).toBe('unknown')
    expect(event.v).toBe('1.0')
    expect(event.seq).toBe(0)
    expect(typeof event.ts).toBe('string')
    expect(event.request_id).toBe('')
    expect(event.trace_id).toBe('')
    expect(event.capabilities).toEqual([])
  })
})

describe('connectEvents normalization', () => {
  class FakeEventSource {
    static instances: FakeEventSource[] = []
    onmessage: ((ev: MessageEvent) => void) | null = null
    listeners = new Map<string, EventListener[]>()
    closed = false
    constructor(public url: string) { FakeEventSource.instances.push(this) }
    addEventListener(type: string, listener: EventListener) {
      this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
    }
    close() { this.closed = true }
    emit(type: string, data: string, lastEventId = '') {
      const ev = new MessageEvent(type, { data, lastEventId })
      if (type === 'message') this.onmessage?.(ev)
      for (const listener of this.listeners.get(type) ?? []) listener(ev)
    }
  }

  it('delivers normalized envelopes for the real Core event names and swallows malformed frames', () => {
    FakeEventSource.instances = []
    vi.stubGlobal('EventSource', FakeEventSource)
    try {
      const received: EventEnvelope[] = []
      api.connectEvents('s-1', (event) => received.push(event))
      const source = FakeEventSource.instances[0]
      expect(source.url).toContain('/api/v1/events?session_id=s-1')

      // Core emits NAMED frames; the browser drops any named frame with no listener
      // of that name. These are the names that were missing, which is why tool
      // outcomes, approval decisions, and run failures never reached the UI.
      for (const name of [
        'tool.execution.requested',
        'tool.execution.completed',
        'tool.execution.failed',
        'tool.execution.outcome_unknown',
        'worker.assigned',
        'worker.failed',
        'approval.decided',
        'run.failed',
      ]) {
        expect(source.listeners.has(name), `missing listener for ${name}`).toBe(true)
      }
      // A name Core has never produced must not be subscribed to: a dead listener is
      // what hides the fact that a real fact has no rendering.
      expect(source.listeners.has('project.created')).toBe(false)

      // Core wire shape on a real named event.
      source.emit('tool.execution.failed', JSON.stringify({
        version: '1.0', event_id: 'e1', event_type: 'tool.execution.failed',
        timestamp: '2026-08-27T00:00:00Z', session_id: 's-1', run_id: 'r-1',
        payload: { sequence: 12, tool_id: 'write_file', error_category: 'not_approved' },
      }), '12')
      // Malformed JSON must not throw (would trip the renderer crash overlay).
      expect(() => source.emit('run.failed', '{not json')).not.toThrow()

      expect(received).toHaveLength(1)
      expect(received[0].type).toBe('tool.execution.failed')
      expect(received[0].seq).toBe(12)
      expect(received[0].ts).toBe('2026-08-27T00:00:00Z')
    } finally {
      vi.unstubAllGlobals()
    }
  })
})


describe('invokeStream compat', () => {
  interface Call {
    url: string
    init?: RequestInit
  }

  const calls: Call[] = []

  function sseBody(frames: string): Response {
    return new Response(frames, {
      status: 200,
      headers: { 'content-type': 'text/event-stream' },
    })
  }

  /**
   * The admission receipt, then a run stream written the way Core writes it: a
   * comment heartbeat, one CRLF-framed delta whose text sits next to `kind`, and a
   * terminal frame with no trailing blank line because the response ends there.
   */
  function stubGateway(frames: string) {
    calls.length = 0
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      calls.push({ url, init })
      if (init?.method === 'POST') {
        return new Response(JSON.stringify({ run_id: 'r-1', stream_cursor: 3 }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        })
      }
      return sseBody(frames)
    }))
  }

  async function until(predicate: () => boolean): Promise<void> {
    for (let attempt = 0; attempt < 200 && !predicate(); attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 0))
    }
    if (!predicate()) throw new Error('the stream never reached the expected state')
  }

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('carries the admission cursor into the stream request', async () => {
    stubGateway('id: 4\nevent: done\ndata: {"run_id":"r-1","kind":"done","seq":4}')
    const chunks: Array<{ kind: string }> = []
    const controller = api.invokeStreamWithAdmission(
      's-1',
      { content: '写个提交信息', client_message_id: 'c-1', permission_mode: 'default' },
      (chunk) => chunks.push(chunk as unknown as { kind: string }),
    )
    await until(() => chunks.length > 0)
    expect(calls.map((call) => call.url)).toEqual([
      expect.stringContaining('/api/v1/sessions/s-1/interactions'),
      expect.stringContaining('/api/v1/runs/r-1/stream?after_seq=3'),
    ])
    controller.abort()
  })

  it('delivers the delta text the panel actually reads', async () => {
    // Core puts the text beside `kind`; parseRunSseBlock keeps the frame in
    // `payload`; ModelStreamChunkDto spells it at the top level. Before the readers were
    // merged this compat path built a chunk without that field, so the AI commit-message
    // panel streamed a full reply into `chunk.delta === undefined` and showed nothing.
    stubGateway(
      ': heartbeat\n\n'
        + 'id: 4\r\nevent: delta\r\ndata: {"run_id":"r-1","kind":"delta","seq":4,"delta":"feat: 一条"}\r\n\r\n',
    )
    const chunks: ModelStreamChunkDto[] = []
    const controller = api.invokeStream('s-1', '写个提交信息', (chunk) => chunks.push(chunk))
    await until(() => chunks.some((chunk) => chunk.delta))
    const delta = chunks.find((chunk) => chunk.kind === 'delta')
    expect(delta?.delta).toBe('feat: 一条')
    // Heartbeats are transport noise and never reach a consumer.
    expect(chunks.map((chunk) => chunk.kind)).toEqual(['delta'])
    controller.abort()
  })

  it('does not lose the last frame when the response ends without a blank line', async () => {
    stubGateway('id: 7\nevent: done\ndata: {"run_id":"r-1","kind":"done","seq":7,"finish_reason":"stop"}')
    const chunks: ModelStreamChunkDto[] = []
    const errors: Error[] = []
    const controller = api.invokeStream('s-1', '内容', (chunk) => chunks.push(chunk), (error) => errors.push(error))
    await until(() => chunks.length > 0)
    expect(chunks[0].kind).toBe('done')
    expect(chunks[0].finish_reason).toBe('stop')
    controller.abort()
  })

  it('stops the durable reader when the caller aborts', async () => {
    stubGateway('id: 4\nevent: delta\ndata: {"run_id":"r-1","kind":"delta","seq":4,"delta":"x"}\n\n')
    const chunks: ModelStreamChunkDto[] = []
    const controller = api.invokeStream('s-1', '内容', (chunk) => chunks.push(chunk))
    await until(() => chunks.length > 0)
    const streamCall = calls[calls.length - 1]
    expect(streamCall.init?.signal?.aborted).toBe(false)
    controller.abort()
    expect(streamCall.init?.signal?.aborted).toBe(true)
  })

  it('sends the identity the durable endpoint accepts, and none it rejects', async () => {
    stubGateway('id: 4\nevent: done\ndata: {"run_id":"r-1","kind":"done","seq":4}')
    const controller = api.invokeStreamWithAdmission(
      's-1',
      { content: 'x', client_message_id: 'c-9', mode_version_id: 'm-1', permission_mode: 'default', expected_context_revision: 2 },
      () => {},
    )
    await until(() => calls.length >= 2)
    const sent = JSON.parse(String(calls[0].init?.body)) as Record<string, unknown>
    expect(sent).toEqual({
      content: 'x',
      client_message_id: 'c-9',
      dispatch_mode: 'parallel',
      mode_version_id: 'm-1',
      expected_context_revision: 2,
    })
    // agent_mode is the retired six-value enum: Core answers 400 unknown_field for it.
    expect(sent.agent_mode).toBeUndefined()
    controller.abort()
  })
})


describe('market catalog query string', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  /**
   * The catalog used to be requested as `?query=`, a parameter neither Core nor the gateway
   * reads, so the search box narrowed nothing end-to-end while every mocked test passed. The
   * name is the whole assertion; the envelope is the other half of the same fix.
   */
  it('sends the search term under the name Core reads, and unwraps the page', async () => {
    const urls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input))
      return new Response(JSON.stringify({
        items: [],
        total_available: 0,
        has_more: false,
      }), { status: 200, headers: { 'content-type': 'application/json' } })
    }))

    const page = await api.listMarketCatalog({ kind: 'all', q: 'git hub', source_id: 'src-1', offset: 50 })

    const url = urls[0]!
    expect(url).toContain('q=git+hub')
    expect(url).toContain('source_id=src-1')
    expect(url).toContain('offset=50')
    expect(url).not.toContain('query=')
    // kind=all is the UI's "no filter", not a kind Core stores.
    expect(url).not.toContain('kind=')
    expect(page.items).toEqual([])
    expect(page.total_available).toBe(0)
  })

  it('asks for nothing when there is nothing to filter by', async () => {
    const urls: string[] = []
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input))
      return new Response(JSON.stringify({ items: [], total_available: 0, has_more: false }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      })
    }))

    await api.listMarketCatalog()
    expect(urls[0]).not.toContain('?')
  })
})
