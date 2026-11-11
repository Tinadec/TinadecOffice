import { DEFAULT_LANE_LAYOUT } from './flowShared'

export type ModeLane = 'operation' | 'execution'

export interface ModeLayoutNode {
  id: string
  lane: ModeLane
}

export interface ModeLayoutEdgeLink {
  id?: string
  source: string
  target: string
}

export interface ModeLayoutOptions {
  originX?: number
  operationY?: number
  executionY?: number
  columnGap?: number
  rowGap?: number
}

export interface ModeLayoutResult {
  positions: Record<string, { x: number; y: number }>
  layerOf: Record<string, number>
  rowOf: Record<string, number>
  /** Edge ids removed so the layered pass could run; cycles stay visible as validation errors. */
  backEdges: string[]
}

export interface PositionedModeNode {
  id: string
  lane: ModeLane
  position: { x: number; y: number }
}

const DEFAULTS = {
  originX: 60,
  operationY: DEFAULT_LANE_LAYOUT.operationOffsetY,
  executionY: DEFAULT_LANE_LAYOUT.executionOffsetY,
  columnGap: 240,
  rowGap: 120,
}

/** Code-unit order, so the same graph always produces the same layout on every machine. */
function compareIds(left: string, right: string): number {
  if (left === right) return 0
  return left < right ? -1 : 1
}

function resolveOptions(options: ModeLayoutOptions): Required<ModeLayoutOptions> {
  return {
    originX: options.originX ?? DEFAULTS.originX,
    operationY: options.operationY ?? DEFAULTS.operationY,
    executionY: options.executionY ?? DEFAULTS.executionY,
    columnGap: options.columnGap ?? DEFAULTS.columnGap,
    rowGap: options.rowGap ?? DEFAULTS.rowGap,
  }
}

interface PendingEdge {
  id: string
  source: string
  target: string
}

/**
 * Suggests coordinates for a mode graph: operation nodes share one row, execution
 * nodes are laid out in dependency layers. It never moves a node the user placed
 * by hand — callers decide whether to apply the suggestion.
 */
export function layoutMode(
  nodes: ModeLayoutNode[],
  edges: ModeLayoutEdgeLink[],
  options: ModeLayoutOptions = {},
): ModeLayoutResult {
  const opts = resolveOptions(options)
  const laneOf = new Map<string, ModeLane>()
  for (const node of nodes) {
    if (!laneOf.has(node.id)) laneOf.set(node.id, node.lane)
  }
  const ids = [...laneOf.keys()].sort(compareIds)

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
    candidates.push({ id: edge.id ?? `${edge.source}->${edge.target}`, source: edge.source, target: edge.target })
  }
  candidates.sort((left, right) => compareIds(left.id, right.id) || compareIds(left.source, right.source) || compareIds(left.target, right.target))

  const outgoing = new Map<string, string[]>(ids.map((id) => [id, []]))
  const edgeByPair = new Map<string, PendingEdge>()
  for (const edge of candidates) {
    outgoing.get(edge.source)!.push(edge.target)
    edgeByPair.set(pairKey(edge.source, edge.target), edge)
  }
  for (const targets of outgoing.values()) targets.sort(compareIds)

  // A cycle cannot be layered; peel DFS back edges first and report them rather
  // than silently dropping the nodes that participate in the loop.
  const removed = new Set<string>()
  const backEdges: string[] = []
  const state = new Map<string, 1 | 2>()
  const visit = (id: string): void => {
    state.set(id, 1)
    for (const target of outgoing.get(id) ?? []) {
      const edge = edgeByPair.get(pairKey(id, target))
      if (!edge || removed.has(edge.id)) continue
      const targetState = state.get(target)
      if (targetState === 1) {
        removed.add(edge.id)
        backEdges.push(edge.id)
        continue
      }
      if (targetState === undefined) visit(target)
    }
    state.set(id, 2)
  }
  for (const id of ids) if (state.get(id) === undefined) visit(id)
  backEdges.sort(compareIds)

  const kept = candidates.filter((edge) => !removed.has(edge.id))
  const indegree = new Map<string, number>(ids.map((id) => [id, 0]))
  const keptOutgoing = new Map<string, string[]>(ids.map((id) => [id, []]))
  for (const edge of kept) {
    indegree.set(edge.target, (indegree.get(edge.target) ?? 0) + 1)
    keptOutgoing.get(edge.source)!.push(edge.target)
  }

  const layerOf = new Map<string, number>(ids.map((id) => [id, 0]))
  const queue = ids.filter((id) => (indegree.get(id) ?? 0) === 0)
  const processed = new Set<string>()
  while (queue.length > 0) {
    const id = queue.shift()!
    processed.add(id)
    for (const target of keptOutgoing.get(id) ?? []) {
      layerOf.set(target, Math.max(layerOf.get(target) ?? 0, (layerOf.get(id) ?? 0) + 1))
      const next = (indegree.get(target) ?? 0) - 1
      indegree.set(target, next)
      if (next === 0) {
        queue.push(target)
        queue.sort(compareIds)
      }
    }
  }
  // The DFS peel above makes this unreachable for well-formed input; keep a
  // deterministic fallback instead of dropping unprocessed nodes.
  for (const id of ids) if (!processed.has(id)) processed.add(id)

  const parents = new Map<string, string[]>(ids.map((id) => [id, []]))
  for (const edge of kept) parents.get(edge.target)!.push(edge.source)

  const rowOf = new Map<string, number>()
  const operationIds = ids.filter((id) => laneOf.get(id) === 'operation')
  operationIds.forEach((id, row) => rowOf.set(id, row))

  const executionByLayer = new Map<number, string[]>()
  for (const id of ids) {
    if (laneOf.get(id) !== 'execution') continue
    const layer = layerOf.get(id) ?? 0
    const bucket = executionByLayer.get(layer) ?? []
    bucket.push(id)
    executionByLayer.set(layer, bucket)
  }
  const executionParentRow = (id: string): number => {
    const parentRows = (parents.get(id) ?? [])
      .filter((parent) => laneOf.get(parent) === 'execution')
      .map((parent) => rowOf.get(parent))
      .filter((row): row is number => row !== undefined)
    if (parentRows.length === 0) return Number.MAX_SAFE_INTEGER
    return parentRows.reduce((sum, row) => sum + row, 0) / parentRows.length
  }
  for (const layer of [...executionByLayer.keys()].sort((left, right) => left - right)) {
    const bucket = executionByLayer.get(layer)!
    bucket.sort((left, right) => executionParentRow(left) - executionParentRow(right) || compareIds(left, right))
    bucket.forEach((id, row) => rowOf.set(id, row))
  }

  const positions: Record<string, { x: number; y: number }> = {}
  const layerRecord: Record<string, number> = {}
  const rowRecord: Record<string, number> = {}
  for (const id of ids) {
    const lane = laneOf.get(id)!
    const layer = lane === 'operation' ? 0 : (layerOf.get(id) ?? 0)
    const row = rowOf.get(id) ?? 0
    layerRecord[id] = layer
    rowRecord[id] = row
    positions[id] = {
      x: opts.originX + (lane === 'operation' ? row : layer) * opts.columnGap,
      y: lane === 'operation' ? opts.operationY : opts.executionY + row * opts.rowGap,
    }
  }
  return { positions, layerOf: layerRecord, rowOf: rowRecord, backEdges }
}

const NODE_CLEARANCE_X = 200
const NODE_CLEARANCE_Y = 100

/** First free slot in a lane, so a new node never lands on top of an existing one. */
export function findFreeSlot(
  nodes: PositionedModeNode[],
  lane: ModeLane,
  options: ModeLayoutOptions = {},
): { x: number; y: number } {
  const opts = resolveOptions(options)
  const occupied = (x: number, y: number) => nodes.some((node) =>
    Math.abs(node.position.x - x) < NODE_CLEARANCE_X
    && Math.abs(node.position.y - y) < NODE_CLEARANCE_Y)
  for (let row = 0; row < 20; row += 1) {
    for (let column = 0; column < 8; column += 1) {
      const x = opts.originX + column * opts.columnGap
      const y = lane === 'operation' ? opts.operationY : opts.executionY + row * opts.rowGap
      if (!occupied(x, y)) return { x, y }
    }
  }
  return {
    x: opts.originX + 8 * opts.columnGap,
    y: lane === 'operation' ? opts.operationY : opts.executionY + 20 * opts.rowGap,
  }
}
