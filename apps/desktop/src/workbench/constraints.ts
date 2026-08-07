import type {
  ColumnGeometry,
  SplitGeometry,
  WorkbenchContainerSize,
  WorkbenchGeometry,
  WorkbenchLayoutSnapshot,
  WorkbenchSlotId,
} from './types'
import { COLLAPSED_COLUMN_WIDTH } from './types'

// ---------------------------------------------------------------------------
// Constraint solver — deterministic geometry computation.
//
// computeGeometry(container, snapshot) => WorkbenchGeometry
//   - lays out columns by columnOrder with their widths, gaps, top insets.
//   - the center column is adaptive: it fills the space left between the side
//     columns, but NEVER below a minimum so the chat area stays usable.
//   - under space pressure: visually collapse the right column first, then the
//     left, so the center keeps its minimum. This is a VISUAL-only degradation
//     — never written back to the layout.
//   - if a split stack is too short, degrade it to a single stack (visual only).
// ---------------------------------------------------------------------------

const BOTTOM_INSET = 8
/** The adaptive center column must stay at least this wide (chat usability). */
const MIN_CENTER_WIDTH = 320

function effectiveWidth(col: { width: number; collapsed: boolean }): number {
  return col.collapsed ? COLLAPSED_COLUMN_WIDTH : col.width
}

export function computeGeometry(
  container: WorkbenchContainerSize,
  snapshot: WorkbenchLayoutSnapshot,
): WorkbenchGeometry {
  const columns: Record<WorkbenchSlotId, ColumnGeometry> = {} as Record<WorkbenchSlotId, ColumnGeometry>
  const splits: Record<string, SplitGeometry> = {}
  const degraded: WorkbenchGeometry['degraded'] = {
    collapsedRight: false,
    collapsedLeft: false,
    degradedSplits: [],
  }

  const gap = snapshot.gap
  const inset = snapshot.edgeInset
  const slots = snapshot.columnOrder.filter((s) => snapshot.columns[s])
  const hasCenter = slots.includes('center')
  const availableWidth = container.width - 2 * inset

  // Collapse budget: keep the center (if present) at its minimum, so side
  // columns must fit in whatever remains. Work out how much room the side
  // columns need, and collapse them (right first, then left) until it fits.
  const sideSlots = slots.filter((s) => s !== 'center')
  // The center is adaptive: if present, reserve MIN_CENTER_WIDTH for it (plus
  // its share of the gaps). If absent, its width is zero.
  const centerReserved = hasCenter ? MIN_CENTER_WIDTH : 0
  const sideGaps = Math.max(0, sideSlots.length - 1) * gap
  const reserved = centerReserved + sideGaps

  // Collapse right first, then left, while the side columns still don't fit.
  const collapsedSet = new Set<WorkbenchSlotId>()
  const sideWidths = new Map<WorkbenchSlotId, number>()
  for (const s of sideSlots) sideWidths.set(s, effectiveWidth(snapshot.columns[s]))

  if (reserved + sum(sideWidths) > availableWidth) {
    const right = sideSlots.find((s) => s === 'right')
    if (right && !snapshot.columns[right].collapsed) {
      collapsedSet.add(right)
      sideWidths.set(right, COLLAPSED_COLUMN_WIDTH)
      degraded.collapsedRight = true
    }
  }
  if (reserved + sum(sideWidths) > availableWidth) {
    const left = sideSlots.find((s) => s === 'left')
    if (left && !snapshot.columns[left].collapsed) {
      collapsedSet.add(left)
      sideWidths.set(left, COLLAPSED_COLUMN_WIDTH)
      degraded.collapsedLeft = true
    }
  }

  function sum(m: Map<WorkbenchSlotId, number>): number {
    let total = 0
    for (const v of m.values()) total += v
    return total
  }

  // Lay out columns.
  let cursorX = inset
  const columnGeoms: Record<WorkbenchSlotId, ColumnGeometry> = {} as Record<WorkbenchSlotId, ColumnGeometry>

  for (let i = 0; i < slots.length; i++) {
    const slotId = slots[i]
    const col = snapshot.columns[slotId]
    const isLast = i === slots.length - 1
    const isCollapsedVisual = collapsedSet.has(slotId)

    // Center is adaptive: fills the space between the (possibly collapsed) side
    // columns, but never below MIN_CENTER_WIDTH.
    let width = isCollapsedVisual ? COLLAPSED_COLUMN_WIDTH : effectiveWidth(col)
    if (slotId === 'center' && !isCollapsedVisual) {
      const usedLeft = columnGeoms.left ? columnGeoms.left.x + columnGeoms.left.width + gap : 0
      const rightX = columnGeoms.right
        ? columnGeoms.right.x
        : container.width - inset - (collapsedSet.has('right') ? COLLAPSED_COLUMN_WIDTH : effectiveWidth(snapshot.columns.right ?? { width: 0, collapsed: false }))
      width = Math.max(MIN_CENTER_WIDTH, rightX - gap - usedLeft)
    }

    const height = container.height - col.topInset - BOTTOM_INSET
    const y = col.topInset
    columnGeoms[slotId] = {
      slotId,
      x: cursorX,
      y,
      width,
      height,
      effectiveWidth: width,
      topInset: col.topInset,
    }
    cursorX += width + (isLast ? 0 : gap)
  }

  // Assign back into the returned shape.
  for (const slotId of slots) {
    columns[slotId] = columnGeoms[slotId]
  }

  // Splits.
  for (const slotId of slots) {
    const col = snapshot.columns[slotId]
    const geom = columnGeoms[slotId]
    if (!col.secondary || col.splitRatio == null) continue

    const ratio = col.splitRatio
    const dividerY = geom.y + Math.round(geom.height * ratio)
    // Degrade if either half is too small (< 80px).
    const minHalf = 80
    const upperH = dividerY - geom.y
    const lowerH = geom.y + geom.height - dividerY
    const tooSmall = upperH < minHalf || lowerH < minHalf

    if (tooSmall) {
      degraded.degradedSplits.push(slotId)
      // Degrade visually into a single stack: upper fills the column, lower is empty.
      splits[slotId] = {
        slotId,
        dividerY: geom.y + geom.height,
        upper: { x: geom.x, y: geom.y, width: geom.width, height: geom.height, degraded: true },
        lower: { x: geom.x, y: geom.y + geom.height, width: geom.width, height: 0, degraded: true },
      }
      continue
    }

    splits[slotId] = {
      slotId,
      dividerY,
      upper: { x: geom.x, y: geom.y, width: geom.width, height: upperH, degraded: false },
      lower: { x: geom.x, y: dividerY, width: geom.width, height: lowerH, degraded: false },
    }
  }

  return { columns, splits, degraded }
}
