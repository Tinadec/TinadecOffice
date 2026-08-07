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
//   - under space pressure: visually collapse the right column first, then the
//     left. This is a VISUAL-only degradation — never written back to the layout.
//   - if a split stack is too short, degrade it to a single stack (visual only).
// ---------------------------------------------------------------------------

const BOTTOM_INSET = 8

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

  // Total width of all columns at their persisted (non-collapsed) widths.
  const totalWidths = slots.reduce((sum, s) => sum + effectiveWidth(snapshot.columns[s]), 0)
  const totalGaps = Math.max(0, slots.length - 1) * gap
  const needed = totalWidths + totalGaps
  // Width available inside the window-edge insets.
  const availableWidth = container.width - 2 * inset
  const overflow = needed - availableWidth

  // Under pressure, collapse right then left (visual only).
  let collapsedSet = new Set<WorkbenchSlotId>()
  if (overflow > 0) {
    const right = slots.find((s) => s === 'right')
    if (right && !snapshot.columns[right].collapsed) {
      collapsedSet.add(right)
      degraded.collapsedRight = true
    }
  }
  // Recompute overflow after collapsing right.
  const widthsAfterRight = slots.map((s) =>
    collapsedSet.has(s) ? COLLAPSED_COLUMN_WIDTH : effectiveWidth(snapshot.columns[s]),
  )
  const totalAfterRight = widthsAfterRight.reduce((a, b) => a + b, 0) + totalGaps
  if (totalAfterRight > availableWidth) {
    const left = slots.find((s) => s === 'left')
    if (left && !snapshot.columns[left].collapsed) {
      collapsedSet.add(left)
      degraded.collapsedLeft = true
    }
  }

  // Lay out columns.
  let cursorX = inset
  const columnGeoms: Record<WorkbenchSlotId, ColumnGeometry> = {} as Record<WorkbenchSlotId, ColumnGeometry>

  for (let i = 0; i < slots.length; i++) {
    const slotId = slots[i]
    const col = snapshot.columns[slotId]
    const isLast = i === slots.length - 1
    const w = effectiveWidth(col)
    const isCollapsedVisual = collapsedSet.has(slotId)

    let width = isCollapsedVisual ? COLLAPSED_COLUMN_WIDTH : w
    // Center column is adaptive: it fills whatever remains between the left and
    // right columns (including the gap to each neighbor).
    if (slotId === 'center' && !isCollapsedVisual) {
      const usedLeft = columnGeoms.left ? columnGeoms.left.x + columnGeoms.left.width + gap : 0
      const rightX = snapshot.columns.right
        ? (columnGeoms.right?.x ?? (container.width - inset - (collapsedSet.has('right') ? COLLAPSED_COLUMN_WIDTH : effectiveWidth(snapshot.columns.right))))
        : container.width - inset
      width = Math.max(0, rightX - gap - usedLeft)
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
