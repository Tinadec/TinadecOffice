import type { Node, Edge } from '@vue-flow/core'

/**
 * Shared Vue Flow plumbing for read-only run visualization.
 *
 * Extracted from AgentModeCanvas so the Workbench lane canvas reuses the same
 * layout math and styling hooks without inheriting editor concerns (drag,
 * connect, validation) that a frozen run must not offer.
 */

export interface LaneLayoutNodeInput {
  id: string
  lane: 'operation' | 'execution'
  /** Depth within the lane; drives horizontal position. */
  depth: number
  /** Stable ordering key inside a depth column. */
  order: number
}

export interface LaneLayoutOptions {
  columnWidth?: number
  rowHeight?: number
  operationOffsetY?: number
  executionOffsetY?: number
}

export const DEFAULT_LANE_LAYOUT: Required<LaneLayoutOptions> = {
  columnWidth: 230,
  rowHeight: 96,
  operationOffsetY: 40,
  executionOffsetY: 260,
}

/**
 * Deterministic two-lane grid layout. The operation lane is one row; the
 * execution lane lays planner/workers out by dependency depth.
 */
export function layoutLanes(
  inputs: LaneLayoutNodeInput[],
  options: LaneLayoutOptions = {},
): Record<string, { x: number; y: number }> {
  const opts = { ...DEFAULT_LANE_LAYOUT, ...options }
  const positions: Record<string, { x: number; y: number }> = {}
  for (const input of inputs) {
    if (input.lane === 'operation') {
      positions[input.id] = { x: input.order * opts.columnWidth, y: opts.operationOffsetY }
    } else {
      positions[input.id] = {
        x: input.depth * opts.columnWidth + 40,
        y: opts.executionOffsetY + input.order * opts.rowHeight * 0.4,
      }
    }
  }
  return positions
}

const LANE_EDGE_COLORS = {
  operation: 'var(--border-muted, #2a3140)',
  lineage: 'var(--accent-info, #4a9eff)',
  task: 'var(--text-secondary, #8b95a5)',
} as const

export function makeEdge(
  id: string,
  source: string,
  target: string,
  kind: keyof typeof LANE_EDGE_COLORS,
  label?: string,
): Edge {
  return {
    id,
    source,
    target,
    label,
    animated: kind === 'lineage',
    style: { stroke: LANE_EDGE_COLORS[kind] },
  }
}

export function toFlowNodes(
  entries: Array<{ id: string; position: { x: number; y: number }; data: unknown; type?: string }>,
): Node[] {
  return entries.map((entry) => ({
    id: entry.id,
    type: entry.type ?? 'default',
    position: entry.position,
    data: entry.data,
    draggable: false,
    connectable: false,
    selectable: true,
  }))
}
