import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, ref } from 'vue'
import type { EventEnvelope } from '@/api'
import { EventScript } from '@/lib/conversation/fixtures/builder'

const bus = vi.hoisted(() => ({
  handler: null as null | ((event: EventEnvelope) => void),
  heartbeat: null as null | (() => void),
  unsubscribed: 0,
}))

vi.mock('@/lib/sessionEventBus', () => ({
  subscribeToSessionEvents: (handler: (event: EventEnvelope) => void, options: { onHeartbeat?: () => void }) => {
    bus.handler = handler
    bus.heartbeat = options.onHeartbeat ?? null
    return () => { bus.unsubscribed += 1 }
  },
}))

const { useConversationFeed } = await import('./useConversationFeed')

describe('useConversationFeed', () => {
  let frames: (() => void)[]
  const schedule = (callback: () => void) => { frames.push(callback) }
  const flushFrames = () => { const run = frames; frames = []; run.forEach((f) => f()) }

  beforeEach(() => {
    frames = []
    bus.unsubscribed = 0
    vi.useFakeTimers()
  })
  afterEach(() => vi.useRealTimers())

  function mount(session = 'session-1') {
    const sessionId = ref<string | null>(session)
    const scope = effectScope()
    const feed = scope.run(() => useConversationFeed(sessionId, { schedule, hydrationTimeoutMs: 2000 }))!
    return { sessionId, scope, feed }
  }

  it('publishes the whole initial replay once, on the post-replay heartbeat', () => {
    const { feed } = mount()
    const s = new EventScript()
    for (let i = 0; i < 50; i += 1) s.toolCall('run-1', `e-${i}`, 'read_file', { resource_path: `/w/${i}` })
    const before = feed.revision.value
    s.events.forEach((event) => bus.handler!(event))
    expect(frames).toHaveLength(0)
    expect(feed.state.value.runs.size).toBe(0)
    bus.heartbeat!()
    expect(feed.hydrated.value).toBe(true)
    expect(feed.state.value.runs.get('run-1')!.entries).toHaveLength(50)
    // One batch publish plus the hydration flip.
    expect(feed.revision.value - before).toBe(2)
  })

  it('batches live events into one publish per frame', () => {
    const { feed } = mount()
    bus.heartbeat!()
    const revision = feed.revision.value
    const s = new EventScript()
    s.toolCall('run-1', 'a', 'read_file').toolCall('run-1', 'b', 'read_file').toolCall('run-1', 'c', 'read_file')
    s.events.forEach((event) => bus.handler!(event))
    expect(frames).toHaveLength(1)
    expect(feed.revision.value).toBe(revision)
    flushFrames()
    expect(feed.revision.value).toBe(revision + 1)
    expect(feed.state.value.runs.get('run-1')!.entries).toHaveLength(3)
  })

  it('hydrates anyway when no heartbeat arrives', () => {
    const { feed } = mount()
    bus.handler!(new EventScript().emit('run-1', 'tool.execution.requested', { execution_id: 'x', tool_id: 'ls' }))
    vi.advanceTimersByTime(2000)
    expect(feed.hydrated.value).toBe(true)
    expect(feed.state.value.runs.get('run-1')!.entries).toHaveLength(1)
  })

  it('starts over on a session switch and drops a batch queued for the old session', async () => {
    const { feed, sessionId } = mount()
    bus.heartbeat!()
    bus.handler!(new EventScript('session-1').emit('run-1', 'tool.execution.requested', { execution_id: 'x', tool_id: 'ls' }))
    sessionId.value = 'session-2'
    await nextTick()
    flushFrames()
    expect(feed.hydrated.value).toBe(false)
    expect(feed.state.value.runs.size).toBe(0)
    // A frame that names another session never enters this feed.
    bus.handler!(new EventScript('session-1').emit('run-9', 'tool.execution.requested', { execution_id: 'y', tool_id: 'ls' }))
    bus.heartbeat!()
    expect(feed.state.value.runs.size).toBe(0)
  })

  it('unsubscribes when its scope ends', () => {
    const { scope } = mount()
    scope.stop()
    expect(bus.unsubscribed).toBe(1)
  })
})
