import { describe, expect, it } from 'vitest'
import { findFreeSlot, layoutMode, type ModeLayoutNode, type PositionedModeNode } from './modeLayout'

const node = (id: string, lane: 'operation' | 'execution'): ModeLayoutNode => ({ id, lane })
const edge = (source: string, target: string, id = `${source}->${target}`) => ({ id, source, target })

function positionOf(result: ReturnType<typeof layoutMode>, id: string) {
  return result.positions[id]
}

describe('layoutMode', () => {
  it('returns nothing for an empty graph', () => {
    const result = layoutMode([], [])
    expect(result.positions).toEqual({})
    expect(result.backEdges).toEqual([])
  })

  it('places operation nodes in one row without overlap', () => {
    const result = layoutMode([node('leader', 'operation'), node('ops', 'operation')], [])
    expect(positionOf(result, 'leader')).toEqual({ x: 60, y: 40 })
    expect(positionOf(result, 'ops')).toEqual({ x: 300, y: 40 })
  })

  it('places an execution member in the lower lane to the right of its leader', () => {
    const result = layoutMode([node('leader', 'operation'), node('worker', 'execution')], [edge('leader', 'worker')])
    expect(positionOf(result, 'leader')).toEqual({ x: 60, y: 40 })
    expect(positionOf(result, 'worker')).toEqual({ x: 300, y: 260 })
  })

  it('keeps edge-less execution nodes in the lower lane instead of stacking them on the leader', () => {
    const result = layoutMode([node('leader', 'operation'), node('a', 'execution'), node('b', 'execution')], [])
    expect(positionOf(result, 'leader')).toEqual({ x: 60, y: 40 })
    expect(positionOf(result, 'a')).toEqual({ x: 60, y: 260 })
    expect(positionOf(result, 'b')).toEqual({ x: 60, y: 380 })
  })

  it('uses dependency depth for pipeline columns', () => {
    const nodes = [node('leader', 'operation'), node('a', 'execution'), node('b', 'execution')]
    const result = layoutMode(nodes, [edge('leader', 'a'), edge('a', 'b')])
    expect(positionOf(result, 'a')).toEqual({ x: 300, y: 260 })
    expect(positionOf(result, 'b')).toEqual({ x: 540, y: 260 })
  })

  it('puts diamond branches on the same layer with distinct rows', () => {
    const nodes = [node('leader', 'operation'), node('a', 'execution'), node('b', 'execution'), node('c', 'execution')]
    const result = layoutMode(nodes, [
      edge('leader', 'a'),
      edge('leader', 'b'),
      edge('a', 'c'),
      edge('b', 'c'),
    ])
    expect(positionOf(result, 'a')?.x).toBe(300)
    expect(positionOf(result, 'b')?.x).toBe(300)
    expect(positionOf(result, 'a')?.y).not.toBe(positionOf(result, 'b')?.y)
    expect(positionOf(result, 'c')?.x).toBe(540)
  })

  it('breaks a cycle deterministically and still positions every node', () => {
    const nodes = [node('a', 'execution'), node('b', 'execution'), node('c', 'execution')]
    const result = layoutMode(nodes, [edge('a', 'b'), edge('b', 'c'), edge('c', 'a')])
    expect(result.backEdges).toHaveLength(1)
    expect(Object.keys(result.positions).sort()).toEqual(['a', 'b', 'c'])
    const unique = new Set(Object.values(result.positions).map((position) => `${position.x}:${position.y}`))
    expect(unique.size).toBe(3)
  })

  it('ignores dangling and self edges', () => {
    const result = layoutMode([node('a', 'execution')], [edge('a', 'a'), edge('a', 'missing')])
    expect(result.backEdges).toEqual([])
    expect(positionOf(result, 'a')).toEqual({ x: 60, y: 260 })
  })

  it('is independent of input order', () => {
    const nodes = [node('leader', 'operation'), node('a', 'execution'), node('b', 'execution'), node('c', 'execution')]
    const edges = [edge('leader', 'a'), edge('leader', 'b'), edge('a', 'c'), edge('b', 'c')]
    const forward = layoutMode(nodes, edges)
    const reversed = layoutMode([...nodes].reverse(), [...edges].reverse())
    expect(reversed).toEqual(forward)
  })

  it('never produces duplicate coordinates for a realistic team graph', () => {
    const nodes = [node('leader', 'operation'), node('m1', 'execution'), node('m2', 'execution'), node('m3', 'execution')]
    const result = layoutMode(nodes, [edge('leader', 'm1'), edge('m1', 'm2'), edge('m2', 'm3')])
    const unique = new Set(Object.values(result.positions).map((position) => `${position.x}:${position.y}`))
    expect(unique.size).toBe(nodes.length)
  })
})

describe('findFreeSlot', () => {
  const positioned = (...entries: Array<[string, 'operation' | 'execution', number, number]>): PositionedModeNode[] =>
    entries.map(([id, lane, x, y]) => ({ id, lane, position: { x, y } }))

  it('skips the operation slot already in use', () => {
    expect(findFreeSlot(positioned(['leader', 'operation', 60, 40]), 'operation')).toEqual({ x: 300, y: 40 })
  })

  it('leaves the other lane untouched', () => {
    const slot = findFreeSlot(positioned(['worker', 'execution', 300, 260]), 'execution')
    expect(slot.y).toBeGreaterThanOrEqual(260)
    expect(slot).not.toEqual({ x: 300, y: 260 })
  })

  it('falls back to a deterministic free slot when the grid is crowded', () => {
    const crowded = positioned(
      ['a', 'execution', 60, 260],
      ['b', 'execution', 60, 380],
      ['c', 'execution', 300, 260],
      ['d', 'execution', 300, 380],
    )
    const first = findFreeSlot(crowded, 'execution')
    expect(findFreeSlot(crowded, 'execution')).toEqual(first)
  })
})
