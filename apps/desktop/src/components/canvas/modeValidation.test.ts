import { describe, expect, it } from 'vitest'
import { hasBlockingModeIssue, validateMode, type ModeValidationEdge, type ModeValidationNode } from './modeValidation'

const leader: ModeValidationNode = {
  id: 'leader',
  lane: 'operation',
  agent_id: 'agent-leader',
  config: { conversation: true },
}
const worker = (id: string): ModeValidationNode => ({ id, lane: 'execution', agent_id: `agent-${id}` })
const edge = (source: string, target: string, id = `${source}->${target}`): ModeValidationEdge => ({ id, source, target })

function codes(nodes: ModeValidationNode[], edges: ModeValidationEdge[], requireConversation = false) {
  return validateMode(nodes, edges, { requireConversation }).map((issue) => issue.code)
}

describe('validateMode', () => {
  it('flags an empty graph but does not block a draft save', () => {
    const issues = validateMode([], [])
    expect(issues.map((issue) => issue.code)).toEqual(['empty', 'no-conversation'])
    expect(hasBlockingModeIssue(issues)).toBe(false)
  })

  it('accepts a well-formed team topology', () => {
    const issues = validateMode([leader, worker('w1')], [edge('leader', 'w1')])
    expect(issues).toEqual([])
    expect(hasBlockingModeIssue(issues)).toBe(false)
  })

  it('requires exactly one conversation identity for publish', () => {
    const nodes = [{ ...leader, config: null }, worker('w1')]
    const issues = validateMode(nodes, [edge('leader', 'w1')], { requireConversation: true })
    expect(issues).toContainEqual({ code: 'no-conversation', severity: 'error' })
    expect(hasBlockingModeIssue(issues)).toBe(true)
  })

  it('rejects two conversation identities', () => {
    const second = { ...worker('meeting-2'), lane: 'operation' as const, config: { conversation: true } }
    expect(codes([leader, second], [], true)).toContain('multi-conversation')
  })

  it('rejects a conversation marker on the execution layer', () => {
    const misplaced = { ...worker('meeting'), config: { conversation: true } }
    expect(codes([misplaced], [], true)).toContain('conversation-layer')
  })

  it('rejects self loops', () => {
    expect(codes([worker('a')], [edge('a', 'a')])).toContain('self-loop')
  })

  it('rejects duplicate edges', () => {
    expect(codes([worker('a'), worker('b')], [edge('a', 'b', 'e1'), edge('a', 'b', 'e2')])).toContain('duplicate-edge')
  })

  it('rejects edges whose endpoint no longer exists', () => {
    expect(codes([worker('a')], [edge('a', 'gone')])).toContain('dangling-endpoint')
  })

  it('rejects cycles', () => {
    expect(codes([worker('a'), worker('b'), worker('c')], [edge('a', 'b'), edge('b', 'c'), edge('c', 'a')])).toContain('cycle')
  })

  it('rejects backflow from execution to operation', () => {
    expect(codes([leader, worker('a')], [edge('a', 'leader')])).toContain('backflow')
  })

  it('rejects nodes without a bound agent', () => {
    const unbound = { id: 'empty', lane: 'execution' as const, agent_id: '' }
    expect(codes([unbound], [])).toContain('unbound-node')
  })

  it('reports the same issue order for the same graph', () => {
    const nodes = [worker('a'), worker('b'), leader]
    const edges = [edge('a', 'b'), edge('b', 'a'), edge('a', 'a')]
    expect(validateMode(nodes, edges)).toEqual(validateMode([...nodes].reverse(), [...edges].reverse()))
  })
})
