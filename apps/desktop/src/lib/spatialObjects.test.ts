import { describe, expect, it } from 'vitest'
import { projectSpatialObjects, projectSpatialRelations, type SpatialObject } from './spatialObjects'
import type { ApprovalDto, MessageDto, OrchestrationSnapshotDto, SessionTopologyDto, TaskNodeDto, TopologyRunDto, TopologyTaskDto } from '@/api'
import type { ThinkingStep, ToolCall, TurnActivity } from '@/composables/useAgentActivity'

const input = {
  sessionId: 's', messages: [] as MessageDto[], topology: null as SessionTopologyDto | null,
  orchestration: null, turns: {}, streamingReply: '',
}
const message = (id: string, role: 'user' | 'assistant', content: string, run_id?: string, session_id = 's'): MessageDto =>
  ({ id, sequence: 1, session_id, role, content, created_at: '', attachments: [], run_id: run_id ?? null })
const task = (task_id: string, extra: Partial<TopologyTaskDto> = {}): TopologyTaskDto =>
  ({ task_id, task_key: task_id, title: `Work ${task_id}`, status: 'pending', dependencies: [], write_scope: [], ...extra })
const run = (run_id: string, tasks: TopologyTaskDto[] = [], extra: Partial<TopologyRunDto> = {}): TopologyRunDto =>
  ({ run_id, status: 'running', tasks, instances: [], tasks_truncated: false, instances_truncated: false, ...extra })
const topology = (...runs: TopologyRunDto[]): SessionTopologyDto => ({
  session_id: 's', runs, leases: [], members: [], runs_truncated: false,
  leases_truncated: false, members_truncated: false, generated_at: '',
})
const snapshot = (nodes: TaskNodeDto[] = []): OrchestrationSnapshotDto => ({
  run: { id: 'r', session_id: 's', status: 'executing', summary: 'Not the user request', created_at: '', updated_at: '' },
  nodes, lanes: [], assignments: [], step_results: [], context_packs: [], supervision_findings: [],
})
const node = (id: string, extra: Partial<TaskNodeDto> = {}): TaskNodeDto => ({
  id, run_id: 'r', session_id: 's', graph_id: 'g', title: `Work ${id}`, description: '', status: 'pending',
  lane_key: 'main', priority: 0, risk: 'low', success_criteria: [], dependencies: [], required_capabilities: [],
  created_at: '', updated_at: '', ...extra,
})
const tool = (id: string, extra: Partial<ToolCall> = {}): ToolCall => ({
  id, toolId: 'read_file', toolName: 'Read file', status: 'completed', startedAt: null, completedAt: null,
  durationMs: null, argsSummary: '', resultSummary: null, requiresApproval: false, approvalId: null,
  evidence: [], seq: 1, risk: 'low', ...extra,
})
const plan: ThinkingStep = { id: 'p', type: 'plan', title: 'Working plan', description: 'Inspect first', timestamp: '', durationMs: null }

const get = (objects: SpatialObject[], id: string) => {
  const object = objects.find(o => o.id === id)
  expect(object, id).toBeDefined()
  return object!
}

describe('spatial runtime projection', () => {
  it('keeps a new space empty until actual activity arrives', () => {
    expect(projectSpatialObjects(input)).toEqual([])
  })

  it('distinguishes submitted history from the real editable pending queue', () => {
    const result = projectSpatialObjects({
      ...input, topology: topology(run('r')),
      messages: [message('history', 'user', 'old'), message('unknown', 'user', 'submitted', 'unknown-run'), message('m', 'user', 'running', 'r')],
      queuedMessages: [{ id: 'q', content: 'first draft' }, { id: 'q', content: 'updated draft' }],
    })
    expect(get(result, 'meeting:s').queue).toEqual([
      { id: 'history', content: 'old', status: 'unassociated' },
      { id: 'unknown', content: 'submitted', status: 'unknown', runId: 'unknown-run' },
      { id: 'm', content: 'running', status: 'running', runId: 'r' },
      { id: 'q', content: 'updated draft', status: 'queued', editable: true },
    ])
    expect(result.filter(o => o.kind === 'meeting')).toHaveLength(1)
    expect(result.some(o => o.kind === 'message')).toBe(false)
  })

  it('uses the controller queue as authority when an optimistic history id overlaps', () => {
    const result = projectSpatialObjects({ ...input, messages: [message('q', 'user', 'old')], queuedMessages: [{ id: 'q', content: 'edited' }] })
    expect(get(result, 'meeting:s').queue).toEqual([{ id: 'q', content: 'edited', status: 'queued', editable: true }])
  })

  it('does not infer completed run status from an answer or queued status from its absence', () => {
    const result = projectSpatialObjects({ ...input, messages: [message('u', 'user', 'question', 'r'), message('a', 'assistant', 'answer', 'r')] })
    expect(get(result, 'tasks:r').status).toBe('unknown')
    expect(get(result, 'meeting:s').queue?.[0].status).toBe('unknown')
    expect(get(result, 'result:r').status).toBe('unknown')
  })

  it('creates run summaries with stable legacy ids even when there are no tasks', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(run('r'), run('r2')) })
    expect(result.map(o => o.id)).toEqual(['meeting:s', 'tasks:r', 'tasks:r2'])
    for (const id of ['r', 'r2']) expect(get(result, `tasks:${id}`)).toMatchObject({ kind: 'run', runId: id, groupId: id, body: '', rows: [] })
    expect(get(result, 'tasks:r').title).toBeUndefined()
    expect(get(result, 'meeting:s').status).toBeUndefined()
    expect(get(result, 'meeting:s').body).toBe('')
  })

  it('retains task identity and exposes actual titles, summaries and separate instance ownership', () => {
    const source = topology(run('r', [task('a', { title: 'Build feature', handle: 'worker#1', agent_slug: 'worker', worker_instance_id: 'i1', result_summary: 'Built files' }), task('b', { agent_slug: 'reviewer' })]))
    const before = projectSpatialObjects({ ...input, topology: source })
    expect(get(before, 'task:r:a')).toMatchObject({ title: 'Build feature', body: 'Built files', owner: 'worker#1', instanceId: 'i1', taskId: 'a', runId: 'r' })
    expect(get(before, 'task:r:b')).toMatchObject({ title: 'Work b', body: '', owner: 'reviewer' })
    expect(get(before, 'task:r:b').instanceId).toBeUndefined()
    expect(get(before, 'tasks:r').rows?.map(r => r.id)).toEqual(['task:r:a', 'task:r:b'])
    source.runs[0].tasks[0].status = 'completed'
    const after = projectSpatialObjects({ ...input, topology: source })
    expect(after.map(o => o.id)).toEqual(before.map(o => o.id))
    expect(get(after, 'task:r:a').status).toBe('completed')
  })

  it('resolves dependency ids and keys only inside their run without merging equal handles', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(
      run('r', [
        task('a', { task_key: 'build', handle: 'worker#1', worker_instance_id: 'i1', status: 'completed' }),
        task('b', { task_key: 'check', handle: 'worker#1', worker_instance_id: 'i2', status: 'running' }),
        task('c', { dependencies: ['a', 'build', 'check', 'other-only', 'missing', 'missing'] }),
      ]),
      run('r2', [task('a', { task_key: 'build', handle: 'worker#1', worker_instance_id: 'i3' }), task('other-only'), task('c', { dependencies: ['build'] })]),
    ) })
    expect(get(result, 'task:r:c')).toMatchObject({
      dependencyIds: ['task:r:a', 'task:r:b'], unresolvedDependencies: ['other-only', 'missing'], waitingFor: ['Work b'],
    })
    expect(get(result, 'task:r2:c').dependencyIds).toEqual(['task:r2:a'])
    expect(['task:r:a', 'task:r:b', 'task:r2:a'].map(id => get(result, id).instanceId)).toEqual(['i1', 'i2', 'i3'])
    const relations = projectSpatialRelations(result)
    expect(relations.map(r => [r.source, r.target])).toEqual([
      ['task:r:a', 'task:r:c'], ['task:r:b', 'task:r:c'], ['task:r2:a', 'task:r2:c'],
    ])
    expect(relations.every(r => r.kind === 'dependency' && r.sourceField === 'dependencies')).toBe(true)
  })

  it('leaves ambiguous dependency keys unresolved but gives exact ids precedence', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(run('r', [
      task('a', { task_key: 'same' }), task('b', { task_key: 'same' }), task('same'), task('c', { dependencies: ['same', 'unknown'] }),
      task('d', { task_key: 'ambiguous' }), task('e', { task_key: 'ambiguous' }), task('f', { dependencies: ['ambiguous'] }),
    ])) })
    expect(get(result, 'task:r:c').dependencyIds).toEqual(['task:r:same'])
    expect(get(result, 'task:r:f')).toMatchObject({ dependencyIds: [], unresolvedDependencies: ['ambiguous'] })
  })

  it('preserves real cycles without manufacturing meeting, run or ownership relations', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(run('r', [task('a', { dependencies: ['b'] }), task('b', { dependencies: ['a'] })])), hasGitChanges: true })
    expect(projectSpatialRelations(result).map(r => [r.source, r.target])).toEqual([['task:r:b', 'task:r:a'], ['task:r:a', 'task:r:b']])
    expect(result.some(o => 'parentIds' in o)).toBe(false)
  })

  it('combines every answer in message order per run, never in the meeting', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(run('r'), run('r2', [], { status: 'completed' })), messages: [
      message('u1', 'user', 'Build it\nDetailed input', 'r'), message('a1', 'assistant', 'Part one', 'r'),
      message('u2', 'user', 'Other request', 'r2'), message('a2', 'assistant', 'Other answer', 'r2'),
      message('u3', 'user', 'Additional constraint', 'r'), message('a3', 'assistant', 'Part two', 'r'),
    ] })
    expect(get(result, 'tasks:r')).toMatchObject({ title: 'Build it', body: 'Build it\nDetailed input\n\nAdditional constraint' })
    expect(get(result, 'tasks:r2').body).toBe('Other request')
    expect(get(result, 'result:r')).toMatchObject({ body: 'Part one\n\nPart two', runId: 'r', status: 'running' })
    expect(get(result, 'result:r2')).toMatchObject({ body: 'Other answer', runId: 'r2', status: 'completed' })
    expect(get(result, 'meeting:s').body).toBe('')
  })

  it('preserves unassociated assistant history without applying the current run status', () => {
    const result = projectSpatialObjects({ ...input, messages: [message('a', 'assistant', 'Old answer'), message('a2', 'assistant', 'More history')], topology: topology(run('active')), streamingRunId: 'active', streamingReply: 'New preview' })
    expect(get(result, 'meeting:s').body).toBe('Old answer\n\nMore history')
    expect(get(result, 'meeting:s').status).toBeUndefined()
    expect(get(result, 'result:active').body).toBe('New preview')
  })

  it('keeps parallel previews separate and lets persisted answers beat stale previews even for active runs', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(run('r'), run('r2'), run('r3')),
      messages: [message('a', 'assistant', 'Final answer', 'r')], streamingRunId: 'r', streamingReply: 'Stale global',
      streamingReplies: { r: 'Stale per-run', r2: 'Parallel preview', r3: 'Third preview', foreign: 'Not a session run' },
    })
    expect(get(result, 'result:r').body).toBe('Final answer')
    expect(get(result, 'result:r2').body).toBe('Parallel preview')
    expect(get(result, 'result:r3').body).toBe('Third preview')
    expect(result.some(o => o.runId === 'foreign')).toBe(false)
  })

  it('uses legacy streaming only with a trusted explicit run and respects an empty per-run preview', () => {
    expect(projectSpatialObjects({ ...input, streamingReply: 'Unscoped' })).toEqual([])
    expect(projectSpatialObjects({ ...input, streamingRunId: 'unknown', streamingReply: 'Not proof of session' })).toEqual([])
    const source = topology(run('r'), run('r2'))
    const result = projectSpatialObjects({ ...input, topology: source, streamingRunId: 'r2', streamingReply: 'Scoped' })
    expect(result.filter(o => o.kind === 'result').map(o => o.id)).toEqual(['result:r2'])
    expect(get(result, 'result:r2').body).toBe('Scoped')
    expect(projectSpatialObjects({ ...input, topology: source, streamingRunId: 'r2', streamingReply: 'Stale', streamingReplies: { r2: '' } }).some(o => o.kind === 'result')).toBe(false)
  })

  it('accepts only fallback nodes with exact run and session ownership and deduplicates them', () => {
    const valid = node('a')
    const result = projectSpatialObjects({ ...input, orchestration: snapshot([
      valid, valid, node('b', { dependencies: ['a'] }), node('other-run', { run_id: 'r2' }), node('other-session', { session_id: 'other' }),
      node('no-run', { run_id: undefined } as unknown as Partial<TaskNodeDto>), node('no-session', { session_id: undefined } as unknown as Partial<TaskNodeDto>),
    ]) })
    expect(result.filter(o => o.kind === 'task').map(o => o.id)).toEqual(['task:r:a', 'task:r:b'])
    expect(get(result, 'tasks:r').rows).toHaveLength(2)
    expect(get(result, 'task:r:a').owner).toBeUndefined()
    expect(get(result, 'tasks:r').body).toBe('')
    expect(get(result, 'tasks:r').title).toBeUndefined()
    expect(get(result, 'meeting:s').body).toBe('')
    expect(projectSpatialRelations(result).map(r => [r.source, r.target])).toEqual([['task:r:a', 'task:r:b']])
  })

  it('takes a matching topology run as authority rather than supplementing its partial tasks from the snapshot', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(run('r', [], { status: 'completed', tasks_truncated: true })), orchestration: snapshot([node('stale')]) })
    expect(get(result, 'tasks:r')).toMatchObject({ status: 'completed', rows: [], truncated: true })
    expect(result.some(o => o.kind === 'task')).toBe(false)
  })

  it('associates a user message by the snapshot pointer but never overrides an explicit different run id', () => {
    const orchestration = snapshot()
    orchestration.run!.user_message_id = 'u'
    const inferred = projectSpatialObjects({ ...input, orchestration, messages: [message('u', 'user', 'Request')] })
    expect(get(inferred, 'tasks:r').body).toBe('Request')
    expect(get(inferred, 'meeting:s').queue?.[0]).toMatchObject({ runId: 'r', status: 'executing' })
    const explicit = projectSpatialObjects({ ...input, orchestration, messages: [message('u', 'user', 'Other request', 'r2')] })
    expect(get(explicit, 'tasks:r').body).toBe('')
    expect(get(explicit, 'tasks:r2').body).toBe('Other request')
  })

  it('isolates late messages, topology, snapshots, activity and approvals from another session', () => {
    const orchestration = snapshot([node('secret', { session_id: 'other' })])
    orchestration.run!.session_id = 'other'
    const result = projectSpatialObjects({ ...input, orchestration, topology: { ...topology(run('r')), session_id: 'other' },
      messages: [message('u', 'user', 'Private request', 'r', 'other'), message('a', 'assistant', 'Private answer', 'r', 'other')],
      turns: { r: { runId: 'r', thinkingSteps: [plan], toolCalls: [tool('g', { toolId: 'git_status' })] } },
      streamingRunId: 'r', streamingReply: 'Private preview', approvals: [{ id: 'a', session_id: 'other' } as ApprovalDto],
    })
    expect(result).toEqual([])
  })

  it('filters mismatched turn and tool run ids and does not guess their task or owner', () => {
    const turns: Record<string, TurnActivity> = {
      r: { runId: 'r', thinkingSteps: [plan], toolCalls: [tool('valid', { runId: 'r' }), tool('legacy'), tool('wrong', { runId: 'r2', toolId: 'git_status' })] },
      r2: { runId: 'r3', thinkingSteps: [plan], toolCalls: [tool('bad-bucket')] },
      unknown: { runId: 'unknown', thinkingSteps: [plan], toolCalls: [tool('unknown')] },
    }
    const result = projectSpatialObjects({ ...input, topology: topology(run('r', [task('a', { handle: 'worker#1' })]), run('r2')), turns })
    const activities = result.filter(o => o.kind === 'tool' || o.kind === 'plan')
    expect(activities.map(o => o.id)).toEqual(['plan:r:p', 'tool:r:valid', 'tool:r:legacy'])
    for (const object of activities) {
      expect(object).toMatchObject({ runId: 'r', groupId: 'r' })
      expect(object.owner).toBeUndefined()
      expect(object.taskId).toBeUndefined()
      expect(object.instanceId).toBeUndefined()
    }
    expect(result.some(o => o.kind === 'git')).toBe(false)
  })

  it('does not treat an explicitly empty activity run id as a legacy missing id', () => {
    const source = topology(run('r'))
    const mismatchedTurn = projectSpatialObjects({ ...input, topology: source, turns: { r: { runId: '', thinkingSteps: [plan], toolCalls: [tool('t')] } } })
    expect(mismatchedTurn.some(o => o.kind === 'plan' || o.kind === 'tool')).toBe(false)
    const mismatchedTool = projectSpatialObjects({ ...input, topology: source, turns: { r: { toolCalls: [tool('t', { runId: '' })] } } })
    expect(mismatchedTool.some(o => o.kind === 'tool')).toBe(false)
  })

  it('keeps legacy activities for a message-established run but does not treat orphan buckets as session evidence', () => {
    const turns = { r: { thinkingSteps: [plan], toolCalls: [tool('t')] } }
    expect(projectSpatialObjects({ ...input, turns })).toEqual([])
    const result = projectSpatialObjects({ ...input, turns, messages: [message('u', 'user', 'Request', 'r')] })
    expect(get(result, 'plan:r:p')).toMatchObject({ runId: 'r', groupId: 'r' })
    expect(get(result, 'tool:r:t')).toMatchObject({ runId: 'r', groupId: 'r' })
  })

  it('projects shared Git changes without waiting for any tool and keeps scoped approvals', () => {
    const approval = { id: 'a', session_id: 's' } as ApprovalDto
    const result = projectSpatialObjects({ ...input, hasGitChanges: true, approvals: [approval] })
    expect(result).toEqual([{ id: 'git:s', groupId: 'overview', kind: 'git' }, { id: 'approval:s', groupId: 'overview', kind: 'approval' }])
    expect(projectSpatialRelations(result)).toEqual([])
    expect(projectSpatialObjects({ ...input, approvals: [{ ...approval, session_id: 'other' }] })).toEqual([])
  })

  it('projects Git tools as shared current-project data rather than a run-owned result', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(run('r')), turns: { r: { toolCalls: [tool('g', { toolId: 'git_status' })] } } })
    expect(get(result, 'git:s')).toEqual({ id: 'git:s', groupId: 'overview', kind: 'git' })
    expect(projectSpatialRelations(result)).toEqual([])
  })

  it('marks missing dependencies, cycles and downstream nodes as unverified without labelling all as cycles', () => {
    const result = projectSpatialObjects({ ...input, topology: topology(run('r', [
      task('ready'), task('missing', { dependencies: ['absent'] }), task('downstream', { dependencies: ['missing'] }),
      task('cycle-a', { dependencies: ['cycle-b'] }), task('cycle-b', { dependencies: ['cycle-a'] }),
    ])) })
    expect(get(result, 'task:r:ready').dependencyUnverified).toBeUndefined()
    for (const id of ['missing', 'downstream', 'cycle-a', 'cycle-b']) expect(get(result, `task:r:${id}`).dependencyUnverified).toBe(true)
  })

  it('surfaces source truncation separately from title abbreviation and retains full request text', () => {
    const content = '目标'.repeat(70) + '\nFull second line'
    const source = topology(run('r', [], { tasks_truncated: true }), run('r2', [], { instances_truncated: true }), run('r3'))
    source.runs_truncated = true
    const result = projectSpatialObjects({ ...input, topology: source, messages: [message('u', 'user', content, 'r3')] })
    expect(get(result, 'meeting:s').truncated).toBe(true)
    expect(get(result, 'tasks:r').truncated).toBe(true)
    expect(get(result, 'tasks:r2').truncated).toBe(true)
    expect(get(result, 'tasks:r3').truncated).toBe(false)
    expect(get(result, 'tasks:r3').body).toBe(content)
    expect(Array.from(get(result, 'tasks:r3').title!)).toHaveLength(80)
    expect(get(result, 'tasks:r3').title?.endsWith('…')).toBe(true)
    expect(get(result, 'tasks:r3').title).not.toContain('\n')
  })
})

describe('spatial dependency relations', () => {
  it('rejects absent, non-task and cross-run endpoints even with caller-supplied dependencies', () => {
    const objects: SpatialObject[] = [
      { id: 'meeting:s', groupId: 's', kind: 'meeting', runId: 'r' },
      { id: 'task:r:a', groupId: 'r', runId: 'r', kind: 'task' },
      { id: 'task:r2:a', groupId: 'r2', runId: 'r2', kind: 'task' },
      { id: 'task:r:b', groupId: 'r', runId: 'r', kind: 'task', dependencyIds: ['task:r:a', 'task:r:a', 'task:r2:a', 'meeting:s', 'missing'] },
      { id: 'task:unknown', groupId: 'r', kind: 'task', dependencyIds: ['task:r:a'] },
      { id: 'plan:r:p', groupId: 'r', runId: 'r', kind: 'plan', dependencyIds: ['task:r:a'] },
    ]
    expect(projectSpatialRelations(objects)).toEqual([{
      id: 'dependency:task:r:a->task:r:b', source: 'task:r:a', target: 'task:r:b', kind: 'dependency', sourceField: 'dependencies',
    }])
  })
})
