// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  isCompanionEventBroadcast,
  COMPANION_ACTIVITY_EVENT,
  COMPANION_RUN_EVENT,
  COMPANION_SETTINGS_EVENT,
} from '@/events/companionEventTypes'

/**
 * The feed transport. These cases pin the contract with Core's stream
 * (`TinadecCore/AspNetCore/Endpoints/CompanionEventEndpoints.cs`): three named frames, `id:` carrying
 * the row sequence, `data:` carrying the row, and a `heartbeat` frame carrying `after_seq`.
 */

const source = vi.hoisted(() => ({
  instances: [] as Array<{
    url: string
    storageId: string
    listeners: Map<string, (event: MessageEvent) => void>
    closed: boolean
    emit(type: string, data: string): void
    close(): void
  }>,
}))

vi.mock('./scopedEventSource', () => ({
  ScopedEventSource: class {
    onmessage: ((event: MessageEvent) => void) | null = null
    onerror: ((event: Event) => void) | null = null
    readonly listeners = new Map<string, (event: MessageEvent) => void>()
    closed = false
    constructor(readonly url: string, readonly storageId: string) { source.instances.push(this) }
    addEventListener(type: string, listener: (event: MessageEvent) => void) { this.listeners.set(type, listener) }
    removeEventListener(type: string) { this.listeners.delete(type) }
    emit(type: string, data: string) { this.listeners.get(type)?.(new MessageEvent(type, { data })) }
    close() { this.closed = true }
  },
}))

const { connectCompanionEvents, companionStreamSupported, subscribeCompanionEventRelay } =
  await import('./companionApi')

function latest() {
  const instance = source.instances.at(-1)
  if (!instance) throw new Error('no stream was opened')
  return instance
}

beforeEach(() => {
  source.instances.length = 0
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('companion journal subscription', () => {
  it('opens one stream on the companion feed at the user scope with the starting cursor', () => {
    connectCompanionEvents({ onNotice: () => {} }, 12)
    const stream = latest()
    expect(stream.url).toContain('/api/v1/companion-events?after_seq=12')
    // Core pins companion routes to the user scope; inventing a project header would be refused.
    expect(stream.storageId).toBe('user')
    for (const type of [COMPANION_ACTIVITY_EVENT, COMPANION_RUN_EVENT, COMPANION_SETTINGS_EVENT]) {
      expect(stream.listeners.has(type)).toBe(true)
    }
  })

  it('projects a row to its event name, kind, owner and run', () => {
    const notices: unknown[] = []
    connectCompanionEvents({ onNotice: (notice) => notices.push(notice) })
    latest().emit(COMPANION_RUN_EVENT, JSON.stringify({
      seq: 9, id: 'row-9', kind: 'run_failed', companion_id: 'c-1',
      conversation_id: 'conv-1', summary: 'failed', detail: null,
      payload: { run_id: 'run-1', error_code: 'companion_model_unavailable' },
      created_at: '2026-11-08T00:00:00Z',
    }))

    expect(notices).toEqual([{
      type: COMPANION_RUN_EVENT, seq: 9, kind: 'run_failed',
      companionId: 'c-1', conversationId: 'conv-1', runId: 'run-1',
    }])
  })

  it('advances the cursor from frames and from the heartbeat', () => {
    const cursors: number[] = []
    const handle = connectCompanionEvents({
      onNotice: () => {},
      onHeartbeat: (cursor) => cursors.push(cursor),
    })
    latest().emit(COMPANION_ACTIVITY_EVENT, JSON.stringify({ seq: 4, kind: 'message_sent' }))
    latest().emit('heartbeat', JSON.stringify({ after_seq: 7 }))
    expect(handle.cursor).toBe(7)
    expect(cursors).toEqual([7])
  })

  it('ignores a malformed frame instead of breaking the feed', () => {
    const notices: unknown[] = []
    const handle = connectCompanionEvents({ onNotice: (notice) => notices.push(notice) })
    latest().emit(COMPANION_ACTIVITY_EVENT, '{not json')
    expect(notices).toEqual([])
    expect(handle.cursor).toBe(0)
  })

  it('closes the underlying stream', () => {
    const handle = connectCompanionEvents({ onNotice: () => {} })
    handle.close()
    expect(latest().closed).toBe(true)
  })

  it('reports the transport as unavailable where EventSource does not exist', () => {
    vi.stubGlobal('EventSource', undefined)
    expect(companionStreamSupported()).toBe(false)
  })
})

describe('companion journal relay', () => {
  it('accepts only the shape the main window publishes', () => {
    expect(isCompanionEventBroadcast({
      type: 'notice',
      notice: {
        type: COMPANION_SETTINGS_EVENT, seq: 3, kind: 'settings_changed',
        companionId: null, conversationId: null, runId: null,
      },
    })).toBe(true)

    for (const bad of [
      null,
      {},
      { type: 'notice' },
      { type: 'notice', notice: { type: 'companion.unknown', seq: 1, kind: null, companionId: null, conversationId: null, runId: null } },
      { type: 'notice', notice: { type: COMPANION_ACTIVITY_EVENT, seq: '1', kind: null, companionId: null, conversationId: null, runId: null } },
      { type: 'notice', notice: { type: COMPANION_ACTIVITY_EVENT, seq: 1, kind: 7, companionId: null, conversationId: null, runId: null } },
    ]) {
      expect(isCompanionEventBroadcast(bad)).toBe(false)
    }
  })

  it('returns a no-op unsubscribe where BroadcastChannel does not exist', () => {
    vi.stubGlobal('BroadcastChannel', undefined)
    expect(() => subscribeCompanionEventRelay(() => {})()).not.toThrow()
  })
})
