// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, ref, type EffectScope } from 'vue'
import { api } from '@/api'
import { useAgentActivity } from './useAgentActivity'

class FakeEventSource {
  static instances: FakeEventSource[] = []
  listeners = new Map<string, EventListener[]>()

  constructor(public url: string) {
    FakeEventSource.instances.push(this)
  }

  addEventListener(type: string, listener: EventListener) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }

  close() {}

  emit(type: string, payload: Record<string, unknown>, seq = 1) {
    const frame = {
      version: '1.0',
      event_id: `e${seq}`,
      event_type: type,
      timestamp: '2026-09-18T00:00:00Z',
      session_id: 's-1',
      run_id: 'r-1',
      payload: { sequence: seq, ...payload },
    }
    const event = new MessageEvent(type, { data: JSON.stringify(frame), lastEventId: String(seq) })
    for (const listener of this.listeners.get(type) ?? []) listener(event)
  }
}

describe('useAgentActivity task graph projection', () => {
  let scope: EffectScope

  beforeEach(() => {
    FakeEventSource.instances = []
    vi.stubGlobal('EventSource', FakeEventSource)
    vi.spyOn(api, 'listToolExecutions').mockResolvedValue([])
    scope = effectScope()
  })

  afterEach(() => {
    scope.stop()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('uses the durable task_count when task_graph.created omits inline nodes', async () => {
    const sessionId = ref<string | null>(null)
    const harness = scope.run(() => useAgentActivity(sessionId))!
    sessionId.value = 's-1'
    await nextTick()
    const source = FakeEventSource.instances[0]
    expect(source).toBeDefined()

    source.emit('task_graph.created', {
      task_count: 1,
      task_keys: ['responses-write-proof'],
    }, 6)

    expect(harness.activity.value.totalNodes).toBe(1)
    expect(harness.thinkingSteps.value.find((item) => item.id === '6-graph')?.description)
      .toContain('1 个任务节点')
    expect(harness.progressEvents.value.find((event) => event.type === 'task_graph.created')?.message)
      .toContain('1 个任务节点')
  })
})
