import { describe, expect, it } from 'vitest'
import { groupStatus, subAgentGroups } from './subAgents'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

function step(over: Partial<ThinkingStep> & { id: string }): ThinkingStep {
  return { type: 'reasoning', title: 'search#1', description: '', timestamp: '', durationMs: null, ...over } as ThinkingStep
}

function call(over: Partial<ToolCall> & { id: string }): ToolCall {
  return {
    toolId: 'read_file', toolName: 'read_file', status: 'completed', startedAt: null, completedAt: null,
    durationMs: null, argsSummary: '', resultSummary: null, requiresApproval: false, approvalId: null,
    evidence: [], seq: 1, risk: 'low', ...over,
  }
}

describe('subAgentGroups', () => {
  it('leaves the lead agent activity on the spine when no instance claimed it', () => {
    const split = subAgentGroups([step({ id: 'r1', handle: null, taskId: null })], [])
    expect(split.spine).toHaveLength(1)
    expect(split.groups).toEqual([])
  })

  it('lifts attributed steps onto their own group', () => {
    const split = subAgentGroups([
      step({ id: 'r1', seq: 1, handle: null }),
      step({ id: 'r2', seq: 2, handle: 'search#1', taskId: 't1' }),
      step({ id: 'r3', seq: 3, handle: 'search#1', taskId: 't1' }),
    ], [])
    expect(split.spine.map((s) => s.id)).toEqual(['r1'])
    expect(split.groups).toHaveLength(1)
    expect(split.groups[0].steps.map((s) => s.id)).toEqual(['r2', 'r3'])
  })

  /** The reason handles exist: two instances of one role must not merge into one block. */
  it('keeps two parallel instances of the same role apart', () => {
    const split = subAgentGroups([
      step({ id: 'a', seq: 1, handle: 'search#1', taskId: 't1' }),
      step({ id: 'b', seq: 2, handle: 'search#2', taskId: 't2' }),
    ], [])
    expect(split.groups.map((g) => g.handle)).toEqual(['search#1', 'search#2'])
  })

  it('orders groups by their earliest step, not by first appearance', () => {
    const split = subAgentGroups([
      step({ id: 'b', seq: 9, handle: 'search#2', taskId: 't2' }),
      step({ id: 'a', seq: 3, handle: 'search#1', taskId: 't1' }),
    ], [])
    expect(split.groups.map((g) => g.handle)).toEqual(['search#1', 'search#2'])
  })

  it('falls back to the handle as the key when Core sent no task id', () => {
    const split = subAgentGroups([step({ id: 'r1', handle: 'search#1', taskId: null })], [])
    expect(split.groups).toHaveLength(1)
    expect(split.groups[0].key).toBe('search#1')
  })

  it('reads the agent slug off the assignment step so a card can name the role', () => {
    const split = subAgentGroups([
      step({ id: 'a', seq: 1, handle: 'search#1', taskId: 't1', details: { slug: 'search' } }),
      step({ id: 'b', seq: 2, handle: 'search#1', taskId: 't1' }),
    ], [])
    expect(split.groups[0].slug).toBe('search')
  })

  it('reports a group as running while any of its steps is running', () => {
    const [group] = subAgentGroups([step({ id: 'a', handle: 'search#1', taskId: 't1', status: 'running' })], []).groups
    expect(groupStatus(group!)).toBe('running')
  })

  it('reports a group as failed when a step failed', () => {
    const [group] = subAgentGroups([
      step({ id: 'a', seq: 1, handle: 'search#1', taskId: 't1', status: 'completed' }),
      step({ id: 'b', seq: 2, handle: 'search#1', taskId: 't1', status: 'failed' }),
    ], []).groups
    expect(groupStatus(group!)).toBe('failed')
  })

  it('reports a group as done once nothing is running', () => {
    const [group] = subAgentGroups([step({ id: 'a', handle: 'search#1', taskId: 't1', status: 'completed' })], []).groups
    expect(groupStatus(group!)).toBe('done')
  })
})

/**
 * A call names its task but not its handle — the timeline route projects `task_id`, and the handle
 * is what a card is keyed by. The steps are where a task's handle is known, so calls are placed
 * through them. Attributed calls belong to the card; the rest stay on the turn's spine.
 */
describe('subAgentGroups call ownership', () => {
  const assigned = step({ id: 'a', seq: 1, handle: 'search#1', taskId: 't1' })

  it('gives a call to the instance whose task it names', () => {
    const split = subAgentGroups([assigned], [call({ id: 'c1', taskId: 't1' })])
    expect(split.ownCalls).toEqual([])
    expect(split.groups[0]!.calls.map((c) => c.id)).toEqual(['c1'])
  })

  it('leaves a call on the spine when its task named no handle', () => {
    // Guessing which subagent it belonged to would attribute work to the wrong instance.
    const split = subAgentGroups([assigned], [call({ id: 'c1', taskId: 't-unknown' })])
    expect(split.ownCalls.map((c) => c.id)).toEqual(['c1'])
    expect(split.groups[0]!.calls).toEqual([])
  })

  it('leaves an unattributed call on the spine', () => {
    const split = subAgentGroups([assigned], [call({ id: 'c1', taskId: null })])
    expect(split.ownCalls.map((c) => c.id)).toEqual(['c1'])
  })

  /** The invariant worth guarding: a decision must never move behind a fold. */
  it('keeps a waiting approval on the spine even when its instance is known', () => {
    const split = subAgentGroups([assigned], [call({ id: 'c1', taskId: 't1', status: 'waiting_approval', approvalId: 'ap1' })])
    expect(split.ownCalls.map((c) => c.id)).toEqual(['c1'])
    expect(split.groups[0]!.calls).toEqual([])
  })
})
