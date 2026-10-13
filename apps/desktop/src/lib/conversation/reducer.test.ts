import { describe, expect, it } from 'vitest'
import { applyEvent, applyEvents, createFeedState, OUTPUT_LINE_LIMIT, type ReasoningEntry, type ToolEntry } from './reducer'
import { EventScript } from './fixtures/builder'
import { exploreEditShell, interruptedRun, parallelSubAgents, pendingApproval } from './fixtures'

function hydrate(events: Parameters<typeof applyEvents>[1]) {
  const state = createFeedState()
  applyEvents(state, events)
  return state
}

describe('reasoning', () => {
  it('keeps the whole thought, not a tail, and closes it on completion', () => {
    const long = 'x'.repeat(5000)
    const s = new EventScript().thought('run-1', 'resp-1', long)
    const entry = hydrate(s.events).runs.get('run-1')!.entries[0] as ReasoningEntry
    expect(entry.kind).toBe('reasoning')
    expect(entry.text).toBe(long)
    expect(entry.status).toBe('completed')
    expect(entry.endedAt).not.toBeNull()
  })

  it('ignores answer-only model calls and an interrupted call ends rather than fails', () => {
    const s = new EventScript()
    s.emit('run-1', 'model.output.started', { response_id: 'r', channel: null })
    s.emit('run-1', 'model.output.completed', { response_id: 'r', channel: null })
    expect(hydrate(s.events).runs.get('run-1')?.entries ?? []).toHaveLength(0)
    const t = new EventScript()
    t.emit('run-1', 'model.output.delta', { response_id: 'r', channel: 'reasoning', delta: 'hm' })
    t.emit('run-1', 'model.output.interrupted', { response_id: 'r' })
    expect((hydrate(t.events).runs.get('run-1')!.entries[0] as ReasoningEntry).status).toBe('interrupted')
  })
})

describe('tools', () => {
  it('carries the sentence data, assembles shell output across chunks and settles', () => {
    const run = hydrate(exploreEditShell().events).runs.get('run-1')!
    const shell = run.byId.get('e-6') as ToolEntry
    expect(shell).toMatchObject({ toolId: 'shell', command: 'npm test', status: 'completed', exitCode: 0, toolSuccess: true })
    expect(shell.output).toEqual(['PASS a.test.ts', 'PASS b.test.ts'])
    expect(shell.terminalSessionId).toBe('t-1')
    const search = run.byId.get('e-1') as ToolEntry
    expect(search).toMatchObject({ query: 'useResults', resourcePath: '/w/src' })
  })

  it('keeps a bounded output tail and counts what it dropped', () => {
    const s = new EventScript().toolCall('run-1', 'e', 'shell', { command: 'yes' })
    s.emit('run-1', 'terminal.stdout', { execution_id: 'e', data: Array.from({ length: OUTPUT_LINE_LIMIT + 50 }, (_, i) => `line ${i}`).join('\n') + '\n' })
    const entry = hydrate(s.events).runs.get('run-1')!.byId.get('e') as ToolEntry
    expect(entry.output).toHaveLength(OUTPUT_LINE_LIMIT)
    expect(entry.output[0]).toBe('line 50')
    expect(entry.outputDropped).toBe(50)
  })

  it('follows Core\'s approval fold, including a PDP park decided by its permission id', () => {
    const fixture = pendingApproval()
    const state = hydrate(fixture.events)
    const entry = state.runs.get('run-1')!.byId.get('e-1') as ToolEntry
    expect(entry).toMatchObject({ status: 'awaiting_approval', approvalId: 'p-1', risk: 'high' })

    const s = new EventScript()
    s.emit('run-1', 'governance.permission_decided', { permission_request_id: 'p-1', outcome: 'allowed' })
    applyEvents(state, s.events.map((e) => ({ ...e, seq: 100 + e.seq })))
    expect(entry.status).toBe('running')
  })

  it('a rejection fails the call; a retryable failure keeps it running', () => {
    const s = new EventScript().toolCall('run-1', 'a', 'write_file', { requires_approval: true }).toolCall('run-1', 'b', 'shell')
    s.emit('run-1', 'approval.requested', { approval_id: 'p', execution_id: 'a' })
    s.emit('run-1', 'approval.decided', { approval_id: 'p', decision: 'rejected' })
    s.emit('run-1', 'tool.execution.failed', { execution_id: 'b', category: 'timeout', retryable: true })
    const run = hydrate(s.events).runs.get('run-1')!
    expect(run.byId.get('a')).toMatchObject({ status: 'failed', errorCategory: 'approval_rejected' })
    expect(run.byId.get('b')).toMatchObject({ status: 'running', errorCategory: 'timeout', endedAt: null })
  })

  it('records changed paths observed after a mutating call', () => {
    const s = new EventScript().toolCall('run-1', 'e', 'shell', { command: 'npm run fmt' })
    s.emit('run-1', 'workspace.changes_observed', { execution_id: 'e', paths: ['/w/a.ts', '/w/b.ts'], changed_count: 2 })
    expect((hydrate(s.events).runs.get('run-1')!.byId.get('e') as ToolEntry).changedPaths).toEqual(['/w/a.ts', '/w/b.ts'])
  })
})

describe('sub-agents', () => {
  it('builds the task directory from dispatch + assignment and anchors each with a dispatch entry', () => {
    const run = hydrate(parallelSubAgents().events).runs.get('run-1')!
    expect([...run.tasks.values()].map((t) => [t.handle, t.title, t.status])).toEqual([
      ['search#1', 'Research the callers of useResults', 'running'],
      ['global_engineering#1', 'Implement the fix', 'running'],
    ])
    expect(run.entries.filter((e) => e.kind === 'dispatch')).toHaveLength(2)
  })

  it('adopts a dispatch whose id Core replaced, matching by task key', () => {
    const s = new EventScript()
    s.emit('run-1', 'task.dispatched', { dispatch_id: 'dup', task_key: 'k', title: 'T', agent: 'search' })
    s.emit('run-1', 'worker.assigned', { task_id: 'real', task_key: 'k', handle: 'search#1', agent_slug: 'search', reason: 'coordinator_assigned' })
    s.emit('run-1', 'worker.completed', { task_id: 'real', task_key: 'k', status: 'completed', summary: 'Found 3 callers' })
    const run = hydrate(s.events).runs.get('run-1')!
    expect([...run.tasks.keys()]).toEqual(['real'])
    expect(run.tasks.get('real')).toMatchObject({ title: 'T', status: 'completed', summary: 'Found 3 callers' })
    expect(run.byId.get('dispatch:real')).toMatchObject({ taskId: 'real', synthetic: false })
  })

  it('marks the solo master as the run itself and gives planner tasks a synthetic anchor', () => {
    const s = new EventScript()
    s.emit('run-1', 'worker.assigned', { task_id: 'solo', task_key: 'goal', agent_slug: 'meeting', reason: 'solo_master' })
    s.emit('run-1', 'worker.assigned', { task_id: 'planned', task_key: 'step', handle: 'search#1', agent_slug: 'search', reason: 'specialist_tool_match' })
    const run = hydrate(s.events).runs.get('run-1')!
    expect(run.tasks.get('solo')!.isMain).toBe(true)
    expect(run.byId.has('dispatch:solo')).toBe(false)
    expect(run.byId.get('dispatch:planned')).toMatchObject({ synthetic: true })
  })

  it('replaces a plan in place and keeps the previous version', () => {
    const s = new EventScript()
    s.emit('run-1', 'plan.updated', { task_id: 't', handle: 'search#1', steps: [{ step: 'A', status: 'in_progress' }] })
    s.emit('run-1', 'plan.updated', { task_id: 't', handle: 'search#1', steps: [{ step: 'A', status: 'completed' }, { step: 'B', status: 'pending' }], explanation: 'B is needed' })
    const plans = hydrate(s.events).runs.get('run-1')!.entries.filter((e) => e.kind === 'plan')
    expect(plans).toHaveLength(1)
    expect(plans[0]).toMatchObject({ steps: [{ step: 'A', status: 'completed' }, { step: 'B', status: 'pending' }], previous: [{ step: 'A', status: 'in_progress' }], explanation: 'B is needed' })
  })
})

describe('markers, dedup and versions', () => {
  it('reads an interrupt request and its cut as one marker on the target run', () => {
    const markers = hydrate(interruptedRun().events).runs.get('run-1')!.entries.filter((e) => e.kind === 'marker')
    expect(markers.map((m) => m.kind === 'marker' && m.type)).toEqual(['interrupted'])
  })

  it('records why a run failed', () => {
    const s = new EventScript()
    s.emit('run-1', 'run.failed', { run_id: 'run-1', error_category: 'model_unavailable', summary: 'Provider down' })
    const run = hydrate(s.events).runs.get('run-1')!
    expect(run.failure).toEqual({ category: 'model_unavailable', message: 'Provider down' })
  })

  it('drops replay/live overlap by run + seq + type, without comparing sequences across runs', () => {
    const state = createFeedState()
    const s = new EventScript().toolCall('run-1', 'e', 'read_file', { resource_path: '/w/a' }).toolCall('run-2', 'f', 'read_file', { resource_path: '/w/b' })
    applyEvents(state, s.events)
    const version = state.runs.get('run-1')!.version
    expect(applyEvent(state, s.events[0]!)).toBeNull()
    expect(state.runs.get('run-1')!.version).toBe(version)
    expect(state.runs.get('run-2')!.entries).toHaveLength(1)
    expect(s.events[0]!.seq).toBe(s.events[1]!.seq)
  })

  it('ignores events outside the feed and events without a run', () => {
    const state = createFeedState()
    const s = new EventScript()
    const unknown = s.emit('run-1', 'something.else', {})
    expect(applyEvent(state, unknown)).toBeNull()
    expect(applyEvent(state, { ...unknown, type: 'run.failed', run_id: null, payload: {} })).toBeNull()
  })

  it('hydrates 2000 events in one batch well under 300ms', () => {
    const s = new EventScript()
    for (let i = 0; i < 400; i += 1) {
      s.thought('run-1', `r-${i}`, 'think '.repeat(4))
      s.toolCall('run-1', `e-${i}`, i % 3 ? 'read_file' : 'shell', { resource_path: `/w/f${i}.ts`, command: 'npm test' }).toolDone('run-1', `e-${i}`, 'read_file')
    }
    expect(s.events.length).toBeGreaterThanOrEqual(2000)
    const state = createFeedState()
    const started = performance.now()
    const touched = applyEvents(state, s.events)
    const elapsed = performance.now() - started
    expect(touched.size).toBe(1)
    expect(elapsed).toBeLessThan(300)
  })
})
