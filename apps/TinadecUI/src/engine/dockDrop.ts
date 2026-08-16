import { DOCK_DROP_EDGE } from './types'

// ---------------------------------------------------------------------------
// Dock drop resolution — a pure function mapping a pointer position over a
// set of pane rects to a split/merge target.
//
// Mirrors dockview's calculateQuadrantAsPercentage: the outer ~20% band of a
// pane along an edge is a split direction, the interior is a merge (center).
// The UI maps `left/right` -> row split, `top/bottom` -> column split.
// ---------------------------------------------------------------------------

export type DockDropZone = 'left' | 'right' | 'top' | 'bottom' | 'center'

export interface DockDropTarget {
  /** The pane being hovered. */
  paneId: string
  zone: DockDropZone
}

export interface DockPaneHitRect {
  paneId: string
  x: number
  y: number
  width: number
  height: number
}

export interface DockPoint {
  x: number
  y: number
}

function contains(rect: DockPaneHitRect, p: DockPoint): boolean {
  return (
    p.x >= rect.x &&
    p.x <= rect.x + rect.width &&
    p.y >= rect.y &&
    p.y <= rect.y + rect.height
  )
}

/**
 * Resolve a pointer position to a dock target. Returns null when the pointer
 * is outside every pane rect. `edge` is the split-band fraction (0.2 default).
 * Panes are expected not to overlap (the dock lays them out side by side).
 */
export function resolveDockDrop(
  point: DockPoint,
  panes: DockPaneHitRect[],
  edge: number = DOCK_DROP_EDGE,
): DockDropTarget | null {
  // Rendering order = hit order (later panes draw on top; last wins).
  let hit: DockPaneHitRect | null = null
  for (const pane of panes) {
    if (pane.width <= 0 || pane.height <= 0) continue
    if (contains(pane, point)) hit = pane
  }
  if (!hit) return null

  const xp = (100 * (point.x - hit.x)) / hit.width
  const yp = (100 * (point.y - hit.y)) / hit.height
  const threshold = edge * 100

  if (xp < threshold) return { paneId: hit.paneId, zone: 'left' }
  if (xp > 100 - threshold) return { paneId: hit.paneId, zone: 'right' }
  if (yp < threshold) return { paneId: hit.paneId, zone: 'top' }
  if (yp > 100 - threshold) return { paneId: hit.paneId, zone: 'bottom' }
  return { paneId: hit.paneId, zone: 'center' }
}

/** UI mapping: a drop zone -> split direction + placement. */
export function dropZoneToSplit(zone: DockDropZone): { dir: 'row' | 'column'; place: 'start' | 'end' } {
  switch (zone) {
    case 'left':
      return { dir: 'row', place: 'start' }
    case 'right':
      return { dir: 'row', place: 'end' }
    case 'top':
      return { dir: 'column', place: 'start' }
    case 'bottom':
      return { dir: 'column', place: 'end' }
    case 'center':
      return { dir: 'column', place: 'start' }
  }
}
