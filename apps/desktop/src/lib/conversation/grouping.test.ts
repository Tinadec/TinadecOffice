import { describe, expect, it } from 'vitest'
import { applyEvents, createFeedState } from './reducer'
import { GROUP_TEXT_MAX_CHARS, groupRun, isCompactAnswer, type ActivityGroupNode } from './grouping'
import { EventScript } from './fixtures/builder'
import { compactedRun, exploreEditShell, parallelSubAgents, pendingApproval, simpleAnswer } from './fixtures'

function grouped(events: Parameters<typeof applyEvents>[1], live = false) {
  const state = createFeedState()
  applyEvents(state, events)
  return groupRun(state.runs.get('run-1')!, live)
}

describe('groupRun', () => {
  it('folds adjacent quiet steps and keeps edits and commands on their own lines', () => {
    const { nodes } = grouped(exploreEditShell().events)
    expect(nodes.map((n) => n.type)).toEqual(['group', 'tool', 'tool'])
    const group = nodes[0] as ActivityGroupNode
    expect(group.counts).toEqual({ files: 2, searches: 1, lists: 1, thoughts: 1 })
    expect(group.thinkingOnly).toBe(false)
    expect(nodes.slice(1).map((n) => n.type === 'tool' && n.entry.toolId)).toEqual(['replace_lines', 'shell'])
  })

  it('leaves a single quiet step on its own and labels a thoughts-only group', () => {
    expect(grouped(simpleAnswer().events).nodes.map((n) => n.type)).toEqual(['reasoning'])
    const s = new EventScript().thought('run-1', 'a', 'one').thought('run-1', 'b', 'two')
    const [group] = grouped(s.events).nodes as ActivityGroupNode[]
    expect(group!.thinkingOnly).toBe(true)
    expect(group!.thinkingMs).toBeGreaterThan(0)
  })

  it('a failed read is loud', () => {
    const s = new EventScript().toolCall('run-1', 'a', 'read_file', { resource_path: '/w/a' }).toolDone('run-1', 'a', 'read_file', null, false)
      .toolCall('run-1', 'b', 'read_file', { resource_path: '/w/b' }).toolDone('run-1', 'b', 'read_file')
    expect(grouped(s.events).nodes.map((n) => n.type)).toEqual(['tool', 'tool'])
  })

  it('keeps the open end of a live turn exploring, and only that one', () => {
    const s = new EventScript().toolCall('run-1', 'a', 'read_file', { resource_path: '/w/a' }).toolDone('run-1', 'a', 'read_file')
      .toolCall('run-1', 'b', 'read_file', { resource_path: '/w/b' })
    expect((grouped(s.events, true).nodes[0] as ActivityGroupNode).exploring).toBe(true)
    expect((grouped(s.events, false).nodes[0] as ActivityGroupNode).exploring).toBe(false)
  })

  it('lifts a pending approval out of the flow so it cannot be folded away', () => {
    const { nodes, approvals } = grouped(pendingApproval().events)
    expect(nodes).toEqual([])
    expect(approvals.map((a) => [a.entry.id, a.requester])).toEqual([['e-1', null]])
  })

  it('groups parallel dispatches into one agent group, recursing into each sub-agent', () => {
    const result = grouped(parallelSubAgents().events, true)
    expect(result.nodes.map((n) => n.type)).toEqual(['reasoning', 'agents', 'wait'])
    const agents = result.nodes[1]!
    if (agents.type !== 'agents') throw new Error('expected agents')
    expect(agents.agents.map((a) => [a.agent.handle, a.status])).toEqual([['search#1', 'running'], ['global_engineering#1', 'awaiting_approval']])
    const research = agents.agents[0]!
    expect(research.nodes.map((n) => n.type)).toEqual(['group'])
    expect(research.latest?.kind).toBe('tool')
    // The sub-agent's approval rises to the turn and names who asked.
    expect(result.approvals.map((a) => [a.entry.id, a.requester?.handle])).toEqual([['e-2', 'global_engineering#1']])
    expect(result.running).toBe(2)
    expect(result.agents.map((a) => a.agent.handle)).toEqual(['search#1', 'global_engineering#1'])
  })

  it('shows markers and only the supervision verdicts a reader must see', () => {
    const s = new EventScript()
    s.emit('run-1', 'supervision.completed', { decision: 'pass', reasons: [] })
    s.emit('run-1', 'supervision.completed', { decision: 'revise', reasons: ['tests missing'] })
    expect(grouped(s.events).nodes.map((n) => n.type === 'supervision' && n.entry.decision)).toEqual(['revise'])
    expect(grouped(compactedRun().events).nodes.map((n) => n.type)).toEqual(['reasoning', 'marker'])
  })
})

describe('isCompactAnswer', () => {
  it('compacts short interim answers and never the last one', () => {
    expect(isCompactAnswer('Let me check the tests first.', 0, 2)).toBe(true)
    expect(isCompactAnswer('Done.', 1, 2)).toBe(false)
    expect(isCompactAnswer('Done.', 0, 1)).toBe(false)
  })

  it('keeps a long interim answer whole, counting characters not bytes', () => {
    expect(isCompactAnswer('好'.repeat(GROUP_TEXT_MAX_CHARS), 0, 2)).toBe(true)
    expect(isCompactAnswer('x'.repeat(GROUP_TEXT_MAX_CHARS + 1), 0, 2)).toBe(false)
    expect(isCompactAnswer(`  ${'x'.repeat(GROUP_TEXT_MAX_CHARS)}  \n`, 0, 2)).toBe(true)
  })
})

describe('isCompactAnswer and rich answers', () => {
  it('never folds a diagram, image, table, code or math, however short', () => {
    for (const content of [
      '```mermaid\nflowchart LR\n  A --> B\n```',
      '![chart](https://example.test/a.png)',
      '| a | b |\n| - | - |\n| 1 | 2 |',
      '~~~\nnpm test\n~~~',
      '$$\na^2\n$$',
      'See <img src="x.png">',
    ]) expect(isCompactAnswer(content, 0, 2), content).toBe(false)
    expect(isCompactAnswer('Checking `useResults` callers | then the tests.', 0, 2)).toBe(true)
  })
})
