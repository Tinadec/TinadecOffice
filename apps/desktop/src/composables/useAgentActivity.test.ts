// @vitest-environment happy-dom
// api.ts reads `window.tinadec?.gatewayUrl?.()` at module top level, so this suite
// needs the same DOM-ish global as api.test.ts.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { effectScope, ref, nextTick, type EffectScope } from 'vue'
import { api } from '@/api'
import { useAgentActivity } from './useAgentActivity'

/**
 * Core writes NAMED SSE frames, and the browser only dispatches a named frame to a
 * listener registered for exactly that name. These tests drive the real frames
 * through the real `api.connectEvents` subscription, so a name that drifts out of
 * sync — the defect that made tool outcomes, approval decisions, and run failures
 * permanently invisible — fails here instead of silently at runtime.
 */
class FakeEventSource {
  static instances: FakeEventSource[] = []
  onmessage: ((ev: MessageEvent) => void) | null = null
  listeners = new Map<string, EventListener[]>()
  closed = false
  constructor(public url: string) {
    FakeEventSource.instances.push(this)
  }
  addEventListener(type: string, listener: EventListener) {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener])
  }
  close() {
    this.closed = true
  }
  emit(type: string, payload: Record<string, unknown>, seq = 1, ts = '2026-09-17T00:00:00Z') {
    const frame = {
      version: '1.0',
      event_id: `e${seq}`,
      event_type: type,
      timestamp: ts,
      session_id: 's-1',
      run_id: 'r-1',
      payload: { sequence: seq, ...payload },
    }
    const event = new MessageEvent(type, { data: JSON.stringify(frame), lastEventId: String(seq) })
    for (const listener of this.listeners.get(type) ?? []) listener(event)
  }
}

describe('useAgentActivity event wiring', () => {
  let scope: EffectScope

  beforeEach(() => {
    FakeEventSource.instances = []
    vi.stubGlobal('EventSource', FakeEventSource)
    // The timeline read is a separate concern (and a network call); the wiring under
    // test is the SSE one.
    vi.spyOn(api, 'listToolExecutions').mockResolvedValue([])
    scope = effectScope()
  })

  afterEach(() => {
    scope.stop()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  async function mount() {
    const sessionId = ref<string | null>(null)
    const harness = scope.run(() => useAgentActivity(sessionId))!
    sessionId.value = 's-1'
    await nextTick()
    const source = FakeEventSource.instances[0]
    expect(source, 'the composable must subscribe once a session is selected').toBeDefined()
    return { harness, source }
  }

  it('turns a fed-back tool failure into a visible reasoning step', async () => {
    const { harness, source } = await mount()

    source.emit('tool.execution.failed', {
      tool_id: 'write_file',
      error_category: 'not_approved',
    }, 7)

    const step = harness.thinkingSteps.value.find((item) => item.type === 'tool')
    expect(step, 'a failed dispatch must appear in the reasoning trail').toBeDefined()
    expect(step!.description).toContain('write_file')
    // The category is what the model acted on, so it must reach the user too.
    expect(step!.description).toContain('not_approved')
    expect(harness.progressEvents.value.some((event) => event.type === 'tool.execution.failed')).toBe(true)
  })

  it('reports an embedded tool failure inside a completed dispatch', async () => {
    const { harness, source } = await mount()

    source.emit('tool.execution.completed', {
      tool_id: 'write_file',
      tool_success: false,
    }, 3)

    const progress = harness.progressEvents.value.find((event) => event.type === 'tool.execution.completed')
    expect(progress).toBeDefined()
    // A dispatch that completes but reports failure is NOT a success to the reader.
    expect(progress!.message).toContain('失败')
  })

  it('surfaces a terminal run failure as an error state', async () => {
    const { harness, source } = await mount()

    source.emit('run.failed', {
      error_category: 'provider_server_error',
      message: 'The model provider returned a server error.',
    }, 11)

    expect(harness.activity.value.status).toBe('error')
    const step = harness.thinkingSteps.value.find((item) => item.type === 'run')
    expect(step).toBeDefined()
    expect(step!.description).toContain('provider_server_error')
  })

  it('marks the assigned worker and its dispatch reason', async () => {
    const { harness, source } = await mount()

    source.emit('worker.assigned', {
      agent_slug: 'global_engineering',
      reason: 'spawnable_whitelist',
    }, 5)

    expect(harness.activity.value.activeAgentName).toBe('global_engineering')
    const progress = harness.progressEvents.value.find((event) => event.type === 'worker.assigned')
    expect(progress!.message).toContain('global_engineering')
    // The reason is the diagnosis surface for a wrong dispatch.
    expect(progress!.message).toContain('spawnable_whitelist')
  })

  it('shows a blocked worker as unfinished work instead of completion', async () => {
    const { harness, source } = await mount()

    source.emit('worker.blocked', {
      agent_slug: 'global_engineering',
      status: 'blocked',
      summary: 'Task not completed: write_file was unavailable.',
    }, 6)

    expect(harness.activity.value.status).toBe('working')
    const step = harness.thinkingSteps.value.find((item) => item.id === '6-worker-blocked')
    expect(step).toBeDefined()
    expect(step!.title).toContain('未完成')
    expect(step!.description).toContain('Task not completed')
    expect(harness.progressEvents.value.some((event) => event.type === 'worker.blocked')).toBe(true)
  })

  it('makes the persisted-evidence final-response fallback visible', async () => {
    const { harness, source } = await mount()

    source.emit('meeting.response_fallback', {
      error_category: 'provider_server_error',
      retry_count: 5,
    }, 12)

    const step = harness.thinkingSteps.value.find((item) => item.id === '12-meeting-fallback')
    expect(step).toBeDefined()
    expect(step!.description).toContain('provider_server_error')
    expect(harness.progressEvents.value.some((event) => event.type === 'meeting.response_fallback')).toBe(true)
  })

  it('drives the waiting-approval state from the real approval.requested payload', async () => {
    const { harness, source } = await mount()

    source.emit('approval.requested', {
      approval_id: 'a-1',
      tool_id: 'write_file',
      risk: 'high',
    }, 9)

    expect(harness.activity.value.status).toBe('waiting_approval')
    const progress = harness.progressEvents.value.find((event) => event.type === 'approval.requested')
    expect(progress!.message).toContain('write_file')
  })

  it('resolves the decision from approval.decided with the PDP outcome vocabulary', async () => {
    const { harness, source } = await mount()

    source.emit('approval.requested', { approval_id: 'a-1', tool_id: 'write_file' }, 9)
    expect(harness.activity.value.status).toBe('waiting_approval')

    // The PDP spells an approval 'allowed', the approval layer spells it 'approved';
    // both must land as an approval.
    source.emit('approval.decided', { approval_id: 'a-1', outcome: 'allowed' }, 10)
    expect(harness.activity.value.status).toBe('working')
    expect(harness.progressEvents.value.some((event) => event.message.includes('审批已通过'))).toBe(true)
  })

  it('ignores an event name Core never emits', async () => {
    const { harness, source } = await mount()

    expect(() => source.emit('project.created', { id: 'p-1' }, 2)).not.toThrow()
    expect(harness.thinkingSteps.value).toHaveLength(0)
    expect(harness.progressEvents.value).toHaveLength(0)
  })
})
