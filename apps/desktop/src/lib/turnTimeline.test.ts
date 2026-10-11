import { describe, expect, it } from 'vitest'
import { attentionRows, detailRows, turnRows } from './turnTimeline'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

function call(over: Partial<ToolCall> & { id: string }): ToolCall {
  return {
    toolId: 'read_file', toolName: 'read_file', status: 'completed', startedAt: null, completedAt: null,
    durationMs: null, argsSummary: '', resultSummary: null, requiresApproval: false, approvalId: null,
    evidence: [], seq: 1, risk: 'low', ...over,
  }
}

function step(over: Partial<ThinkingStep> & { id: string }): ThinkingStep {
  return { type: 'run_started', title: '', description: '', timestamp: '', durationMs: null, ...over } as ThinkingStep
}

describe('turnRows ordering', () => {
  it('interleaves steps and calls by journal sequence', () => {
    const rows = turnRows(
      [step({ id: 's1', seq: 1 }), step({ id: 's2', seq: 3 })],
      [call({ id: 'c1', seq: 2, toolId: 'write_file' })],
    )
    expect(rows.map((row) => row.kind)).toEqual(['thinking', 'tool', 'thinking'])
  })

  it('falls back to timestamps when rows predate the sequence field', () => {
    const rows = turnRows(
      [step({ id: 's1', timestamp: '2026-01-01T00:00:02Z' })],
      [call({ id: 'c1', startedAt: '2026-01-01T00:00:01Z', toolId: 'write_file' })],
    )
    expect(rows.map((row) => row.kind)).toEqual(['tool', 'thinking'])
  })
})

describe('turnRows reasoning blocks', () => {
  it('keeps provider reasoning in its own block instead of merging lifecycle steps', () => {
    const rows = turnRows(
      [
        step({ id: 's1', seq: 1, type: 'run_started' }),
        step({ id: 'r1', seq: 2, type: 'reasoning', description: '想一想' }),
        step({ id: 's2', seq: 3, type: 'step_result' }),
      ],
      [],
    )
    expect(rows).toHaveLength(3)
    expect(rows.every((row) => row.kind === 'thinking')).toBe(true)
  })

  it('merges consecutive orchestration steps into one block', () => {
    const rows = turnRows(
      [step({ id: 's1', seq: 1 }), step({ id: 's2', seq: 2 }), step({ id: 's3', seq: 3 })],
      [],
    )
    expect(rows).toHaveLength(1)
    expect(rows[0].kind === 'thinking' && rows[0].steps).toHaveLength(3)
  })
})

describe('turnRows layering', () => {
  it('folds consecutive quiet steps into one group', () => {
    const rows = turnRows([], [
      call({ id: 'c1', seq: 1, toolId: 'read_file' }),
      call({ id: 'c2', seq: 2, toolId: 'ls' }),
      call({ id: 'c3', seq: 3, toolId: 'file_search' }),
    ])
    expect(rows).toHaveLength(1)
    expect(rows[0].kind).toBe('toolGroup')
    expect(rows[0].kind === 'toolGroup' && rows[0].calls).toHaveLength(3)
  })

  it('breaks the fold when a mutating step lands between quiet ones', () => {
    const rows = turnRows([], [
      call({ id: 'c1', seq: 1, toolId: 'read_file' }),
      call({ id: 'c2', seq: 2, toolId: 'write_file' }),
      call({ id: 'c3', seq: 3, toolId: 'read_file' }),
    ])
    expect(rows.map((row) => row.kind)).toEqual(['toolGroup', 'tool', 'toolGroup'])
  })

  /** The reason this layering exists: a decision must never be folded or hidden. */
  it('lifts a waiting approval out of the fold entirely', () => {
    const rows = turnRows([], [
      call({ id: 'c1', seq: 1, toolId: 'read_file' }),
      call({ id: 'c2', seq: 2, toolId: 'read_file', status: 'waiting_approval', approvalId: 'a1' }),
      call({ id: 'c3', seq: 3, toolId: 'read_file' }),
    ])
    expect(rows.map((row) => row.kind)).toEqual(['toolGroup', 'pending', 'toolGroup'])
    expect(attentionRows(rows)).toHaveLength(1)
    expect(detailRows(rows).some((row) => row.kind === 'pending')).toBe(false)
  })

  it('gives a failed quiet step its own row rather than folding it', () => {
    const rows = turnRows([], [
      call({ id: 'c1', seq: 1, toolId: 'read_file' }),
      call({ id: 'c2', seq: 2, toolId: 'read_file', status: 'failed' }),
    ])
    expect(rows.map((row) => row.kind)).toEqual(['toolGroup', 'tool'])
  })

  it('gives a running mutating step its own row', () => {
    const rows = turnRows([], [call({ id: 'c1', seq: 1, toolId: 'shell', status: 'running' })])
    expect(rows[0].kind).toBe('tool')
  })

  it('returns nothing for a turn with no activity', () => {
    expect(turnRows([], [])).toEqual([])
  })
})
