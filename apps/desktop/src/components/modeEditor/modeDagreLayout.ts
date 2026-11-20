import dagre from '@dagrejs/dagre'

export type ModeLane = 'operation' | 'execution'

export const MODE_NODE_WIDTH = 224
export const MODE_NODE_HEIGHT = 112
export const MODE_GRID_SIZE = 20
export const MODE_LANE_SPLIT_Y = 240
export const MODE_OPERATION_DEFAULT_Y = 60
export const MODE_EXECUTION_START_Y = 280

const HORIZONTAL_STEP = 260
const VERTICAL_STEP = 180
const ORIGIN_X = 60
const LANE_BOUNDARY_PADDING = 8

export interface ModeDagreNodeInput {
  id: string
  lane: ModeLane
}

export interface ModeDagreEdgeInput {
  id?: string
  source: string
  target: string
}

export interface ModeDagreLayoutOptions {
  originX?: number
  operationY?: number
  executionStartY?: number
  horizontalGap?: number
  verticalGap?: number
}

export interface ModeDagreLayoutResult {
  positions: Record<string, { x: number; y: number }>
  backEdges: string[]
}

export interface PositionedModeDagreNode {
  id: string
  lane: ModeLane
  position: { x: number; y: number }
}

function compareIds(left: string, right: string): number {
  if (left === right) return 0
  return left < right ? -1 : 1
}

export function snapToGrid(value: number, grid = MODE_GRID_SIZE): number {
  if (!Number.isFinite(value)) return 0
  return Math.round(value / grid) * grid
}

export function snapPosition(pos: { x: number; y: number }, grid = MODE_GRID_SIZE): { x: number; y: number } {
  return {
    x: snapToGrid(pos.x, grid),
    y: snapToGrid(pos.y, grid),
  }
}

export function laneForY(y: number): ModeLane {
  return y < MODE_LANE_SPLIT_Y ? 'operation' : 'execution'
}

/**
 * Clamps a node position cleanly to its lane's world Y band and snaps to the
 * uniform 20px grid so dragged nodes never cross into the opposite swimlane.
 */
export function clampToLane(pos: { x: number; y: number }, lane: ModeLane): { x: number; y: number } {
  const snappedX = snapToGrid(pos.x)
  const snappedY = snapToGrid(pos.y)
  if (lane === 'operation') {
    const maxOperationY = snapToGrid(MODE_LANE_SPLIT_Y - MODE_NODE_HEIGHT - LANE_BOUNDARY_PADDING)
    return {
      x: snappedX,
      y: Math.min(snappedY, maxOperationY),
    }
  }
  const minExecutionY = snapToGrid(MODE_LANE_SPLIT_Y + MODE_GRID_SIZE)
  return {
    x: snappedX,
    y: Math.max(snappedY, minExecutionY),
  }
}

interface PendingEdge {
  id: string
  source: string
  target: string
}

/**
 * Computes a collision-free two-swimlane DAG layout using `@dagrejs/dagre`.
 * Operation (Governance) nodes always stay in the top band (`y < MODE_LANE_SPLIT_Y`),
 * Execution nodes flow top-to-bottom below (`y >= MODE_LANE_SPLIT_Y`), and cycles
 * are peeled deterministically while reporting `backEdges`.
 */
export function dagreLayoutMode(
  nodes: ModeDagreNodeInput[],
  edges: ModeDagreEdgeInput[],
  options: ModeDagreLayoutOptions = {},
): ModeDagreLayoutResult {
  const originX = options.originX ?? ORIGIN_X
  const operationY = snapToGrid(options.operationY ?? MODE_OPERATION_DEFAULT_Y)
  const executionStartY = snapToGrid(options.executionStartY ?? MODE_EXECUTION_START_Y)
  const stepX = snapToGrid(MODE_NODE_WIDTH + (options.horizontalGap ?? (HORIZONTAL_STEP - MODE_NODE_WIDTH)))
  const stepY = snapToGrid(MODE_NODE_HEIGHT + (options.verticalGap ?? (VERTICAL_STEP - MODE_NODE_HEIGHT)))

  const laneOf = new Map<string, ModeLane>()
  for (const node of nodes) {
    if (!laneOf.has(node.id)) laneOf.set(node.id, node.lane)
  }
  const ids = [...laneOf.keys()].sort(compareIds)
  if (ids.length === 0) return { positions: {}, backEdges: [] }

  const pairKey = (source: string, target: string) => `${source}\u0000${target}`
  const seenPairs = new Set<string>()
  const candidates: PendingEdge[] = []
  for (const edge of edges) {
    if (!edge.source || !edge.target) continue
    if (!laneOf.has(edge.source) || !laneOf.has(edge.target)) continue
    if (edge.source === edge.target) continue
    const pair = pairKey(edge.source, edge.target)
    if (seenPairs.has(pair)) continue
    seenPairs.add(pair)
    candidates.push({
      id: edge.id ?? `${edge.source}->${edge.target}`,
      source: edge.source,
      target: edge.target,
    })
  }
  candidates.sort((a, b) =>
    compareIds(a.id, b.id) || compareIds(a.source, b.source) || compareIds(a.target, b.target),
  )

  const outgoing = new Map<string, string[]>(ids.map((id) => [id, []]))
  const edgeByPair = new Map<string, PendingEdge>()
  for (const edge of candidates) {
    outgoing.get(edge.source)!.push(edge.target)
    edgeByPair.set(pairKey(edge.source, edge.target), edge)
  }
  for (const targets of outgoing.values()) targets.sort(compareIds)

  const removed = new Set<string>()
  const backEdges: string[] = []
  const visitState = new Map<string, 1 | 2>()
  const peel = (id: string): void => {
    visitState.set(id, 1)
    for (const target of outgoing.get(id) ?? []) {
      const edge = edgeByPair.get(pairKey(id, target))
      if (!edge || removed.has(edge.id)) continue
      const targetState = visitState.get(target)
      if (targetState === 1) {
        removed.add(edge.id)
        backEdges.push(edge.id)
        continue
      }
      if (targetState === undefined) peel(target)
    }
    visitState.set(id, 2)
  }
  for (const id of ids) {
    if (visitState.get(id) === undefined) peel(id)
  }
  backEdges.sort(compareIds)

  const keptEdges = candidates.filter((edge) => !removed.has(edge.id))

  const graph = new dagre.graphlib.Graph({ multigraph: false, compound: false })
  graph.setGraph({
    rankdir: 'TB',
    nodesep: Math.max(36, stepX - MODE_NODE_WIDTH),
    ranksep: Math.max(48, stepY - MODE_NODE_HEIGHT),
    marginx: originX,
    marginy: operationY,
    ranker: 'network-simplex',
  })
  graph.setDefaultEdgeLabel(() => ({}))

  for (const id of ids) {
    graph.setNode(id, { width: MODE_NODE_WIDTH, height: MODE_NODE_HEIGHT })
  }
  for (const edge of keptEdges) {
    graph.setEdge(edge.source, edge.target)
  }

  dagre.layout(graph)

  const dagreX = new Map<string, number>()
  for (const id of ids) {
    const laidOut = graph.node(id)
    const rawX = laidOut && Number.isFinite(laidOut.x) ? laidOut.x - MODE_NODE_WIDTH / 2 : originX
    dagreX.set(id, Math.max(originX, snapToGrid(rawX)))
  }

  const positions: Record<string, { x: number; y: number }> = {}

  const operationIds = ids
    .filter((id) => laneOf.get(id) === 'operation')
    .sort((a, b) => (dagreX.get(a)! - dagreX.get(b)!) || compareIds(a, b))

  let nextOpX = originX
  for (const id of operationIds) {
    const x = Math.max(nextOpX, dagreX.get(id) ?? originX)
    positions[id] = clampToLane({ x, y: operationY }, 'operation')
    nextOpX = x + stepX
  }

  // Compute DAG rank purely within execution edges so execution roots always
  // start in the first execution row regardless of whether they connect to operation nodes.
  const executionIds = ids.filter((id) => laneOf.get(id) === 'execution')
  const exIndegree = new Map<string, number>(executionIds.map((id) => [id, 0]))
  const exOutgoing = new Map<string, string[]>(executionIds.map((id) => [id, []]))
  const exRank = new Map<string, number>(executionIds.map((id) => [id, 0]))

  for (const edge of keptEdges) {
    if (laneOf.get(edge.source) === 'execution' && laneOf.get(edge.target) === 'execution') {
      exOutgoing.get(edge.source)!.push(edge.target)
      exIndegree.set(edge.target, (exIndegree.get(edge.target) ?? 0) + 1)
    }
  }

  const queue = executionIds.filter((id) => (exIndegree.get(id) ?? 0) === 0)
  while (queue.length > 0) {
    const id = queue.shift()!
    for (const target of exOutgoing.get(id) ?? []) {
      exRank.set(target, Math.max(exRank.get(target) ?? 0, (exRank.get(id) ?? 0) + 1))
      const remaining = (exIndegree.get(target) ?? 0) - 1
      exIndegree.set(target, remaining)
      if (remaining === 0) {
        queue.push(target)
        queue.sort(compareIds)
      }
    }
  }

  const executionByRank = new Map<number, string[]>()
  for (const id of executionIds) {
    const rank = exRank.get(id) ?? 0
    const bucket = executionByRank.get(rank) ?? []
    bucket.push(id)
    executionByRank.set(rank, bucket)
  }

  for (const rank of [...executionByRank.keys()].sort((a, b) => a - b)) {
    const bucket = executionByRank.get(rank)!
    bucket.sort((a, b) => (dagreX.get(a)! - dagreX.get(b)!) || compareIds(a, b))
    const y = executionStartY + rank * stepY
    let nextExX = originX
    for (const id of bucket) {
      const x = Math.max(nextExX, dagreX.get(id) ?? originX)
      positions[id] = clampToLane({ x, y }, 'execution')
      nextExX = x + stepX
    }
  }

  return { positions, backEdges }
}

/** Finds the first grid-snapped, collision-free slot in the requested swimlane. */
export function findFreeDagreSlot(
  nodes: PositionedModeDagreNode[],
  lane: ModeLane,
): { x: number; y: number } {
  const occupied = (x: number, y: number) => nodes.some((node) =>
    Math.abs(node.position.x - x) < MODE_NODE_WIDTH + 20
    && Math.abs(node.position.y - y) < MODE_NODE_HEIGHT + 20)

  const maxRows = lane === 'operation' ? 1 : 20
  for (let row = 0; row < maxRows; row += 1) {
    for (let col = 0; col < 12; col += 1) {
      const x = snapToGrid(ORIGIN_X + col * HORIZONTAL_STEP)
      const y = lane === 'operation'
        ? MODE_OPERATION_DEFAULT_Y
        : snapToGrid(MODE_EXECUTION_START_Y + row * VERTICAL_STEP)
      if (!occupied(x, y)) return clampToLane({ x, y }, lane)
    }
  }
  return clampToLane({
    x: snapToGrid(ORIGIN_X + 12 * HORIZONTAL_STEP),
    y: lane === 'operation' ? MODE_OPERATION_DEFAULT_Y : snapToGrid(MODE_EXECUTION_START_Y + 20 * VERTICAL_STEP),
  }, lane)
}
