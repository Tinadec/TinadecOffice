import type { EventEnvelope } from '@/api'

/**
 * Builds session event sequences shaped exactly like Core's emitters (payload keys from
 * `FullDuplexRunEngine.cs` / `ToolDispatcher.cs`). Sequences are allocated per run and
 * timestamps advance one second per event unless `at` moves them.
 */
export class EventScript {
  private readonly seqByRun = new Map<string, number>()
  private clock: number
  readonly events: EventEnvelope[] = []

  constructor(readonly sessionId = 'session-1', start = Date.parse('2026-10-13T09:00:00Z')) {
    this.clock = start
  }

  /** Advances the clock by `seconds` before the next event. */
  wait(seconds: number): this {
    this.clock += seconds * 1000
    return this
  }

  emit(runId: string, type: string, payload: Record<string, unknown> = {}): EventEnvelope {
    const seq = (this.seqByRun.get(runId) ?? 0) + 1
    this.seqByRun.set(runId, seq)
    this.clock += 1000
    const event: EventEnvelope = {
      v: '1.0',
      type,
      run_id: runId,
      session_id: this.sessionId,
      request_id: '',
      trace_id: '',
      seq,
      ts: new Date(this.clock).toISOString(),
      capabilities: [],
      payload: { sequence: seq, ...payload },
    }
    this.events.push(event)
    return event
  }

  thought(runId: string, responseId: string, text: string, who: { task_id?: string | null; handle?: string | null; agent_name?: string } = {}): this {
    const base = { run_id: runId, response_id: responseId, agent_name: who.agent_name ?? 'meeting', task_id: who.task_id ?? null, handle: who.handle ?? null }
    this.emit(runId, 'model.output.started', { ...base, channel: null, delta: null })
    for (const piece of text.match(/[\s\S]{1,24}/g) ?? []) this.emit(runId, 'model.output.delta', { ...base, channel: 'reasoning', delta: piece })
    this.emit(runId, 'model.output.completed', { ...base, channel: null, delta: null })
    return this
  }

  toolCall(runId: string, executionId: string, toolId: string, facts: {
    task_id?: string | null
    resource_path?: string
    command?: string
    query?: string
    requires_approval?: boolean
    risk?: string
  } = {}): this {
    this.emit(runId, 'tool.execution.requested', {
      execution_id: executionId,
      task_id: facts.task_id ?? null,
      agent_instance_id: 'instance-1',
      tool_id: toolId,
      requires_approval: facts.requires_approval ?? false,
      mutates_workspace: false,
      risk: facts.risk ?? 'low',
      lane_key: null,
      arguments_summary: facts.resource_path ? `${toolId} → ${facts.resource_path}` : facts.command ? `${toolId} → \`${facts.command}\`` : toolId,
      resource_path: facts.resource_path ?? null,
      command: facts.command ?? null,
      cwd: facts.command ? '/w' : null,
      query: facts.query ?? null,
    })
    return this
  }

  toolDone(runId: string, executionId: string, toolId: string, taskId: string | null = null, toolSuccess: boolean | null = true): this {
    this.emit(runId, 'tool.execution.completed', { execution_id: executionId, task_id: taskId, tool_id: toolId, attempt: 1, tool_success: toolSuccess })
    return this
  }
}
