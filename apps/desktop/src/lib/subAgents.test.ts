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
