// @vitest-environment happy-dom
// api.ts reads `window.tinadec?.gatewayUrl?.()` at module top level, so tests need a DOM-ish global.
import { describe, expect, it, vi } from 'vitest'
import { api, normalizeEventEnvelope, type EventEnvelope } from './api'

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

  it('delivers normalized envelopes for named Core events and swallows malformed frames', () => {
    FakeEventSource.instances = []
    vi.stubGlobal('EventSource', FakeEventSource)
    try {
      const received: EventEnvelope[] = []
      api.connectEvents('s-1', (event) => received.push(event))
      const source = FakeEventSource.instances[0]
      expect(source.url).toContain('/api/v1/events?session_id=s-1')

      // Core wire shape on a named event the UI subscribes to.
      source.emit('message.created', JSON.stringify({
        version: '1.0', event_id: 'e1', event_type: 'message.created',
        timestamp: '2026-08-27T00:00:00Z', session_id: 's-1', run_id: 'r-1',
        payload: { sequence: 12 },
      }), '12')
      // Malformed JSON must not throw (would trip the renderer crash overlay).
      expect(() => source.emit('run.started', '{not json')).not.toThrow()

      expect(received).toHaveLength(1)
      expect(received[0].type).toBe('message.created')
      expect(received[0].seq).toBe(12)
      expect(received[0].ts).toBe('2026-08-27T00:00:00Z')
    } finally {
      vi.unstubAllGlobals()
    }
  })
})
