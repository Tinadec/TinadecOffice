import type {
  ColumnGeometry,
  SplitGeometry,
  UieColumn,
  UieContainerSize,
  UieDockGeometry,
  UieDockNode,
  UieGeometry,
  UieLayoutSnapshot,
  UieSlotId,
} from './types'
import { COLLAPSED_COLUMN_WIDTH, DOCK_DIVIDER, MAX_DOCK_COLUMN_WIDTH, MIN_DOCK_PANE_HEIGHT, MIN_DOCK_PANE_WIDTH } from './types'

// ---------------------------------------------------------------------------
// Constraint solver — deterministic geometry computation.
//
// computeGeometry(container, snapshot) => UieGeometry
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
export const MIN_CENTER_WIDTH = 320
/**
 * When the right feature panel is visible, the chat keeps at least this width
 * so the composer (mode/permission toolbar) stays on one row and never wraps.
 */
export const MIN_CHAT_COMFORT_WIDTH = 560
/**
 * A right panel overlaying the chat may cover it, but never fully — at least
 * this many pixels of the chat stay visible so adjustments remain possible.
 */
export const MIN_OVERLAY_STRIP = 300

function effectiveWidth(col: { width: number; collapsed: boolean }): number {
  return col.collapsed ? COLLAPSED_COLUMN_WIDTH : col.width
}

// ---------------------------------------------------------------------------
// Dock flattening — recursively lay a column's dock split tree out into
// column-relative pane rects + divider hit-areas.
// ---------------------------------------------------------------------------

interface DockFlattenResult {
  panes: UieDockGeometry['panes']
  dividers: UieDockGeometry['dividers']
  degradedPanes: string[]
}

function flattenDockNode(
  node: UieDockNode,
  rect: { x: number; y: number; width: number; height: number },
  out: DockFlattenResult,
): void {
  if (node.kind === 'pane') {
    out.panes.push({
      paneId: node.paneId,
      x: rect.x,
      y: rect.y,
      width: rect.width,
      height: rect.height,
      degraded: false,
    })
    return
  }

  const gutter = DOCK_DIVIDER
  if (node.dir === 'row') {
    const aWidth = Math.round((rect.width - gutter) * node.ratio)
    const aRect = { x: rect.x, y: rect.y, width: aWidth, height: rect.height }
    const bRect = { x: rect.x + aWidth + gutter, y: rect.y, width: rect.width - aWidth - gutter, height: rect.height }
    flattenDockNode(node.a, aRect, out)
    flattenDockNode(node.b, bRect, out)
    out.dividers.push({
      splitId: node.splitId,
      dir: 'row',
      x: rect.x + aWidth,
      y: rect.y,
      width: gutter,
      height: rect.height,
    })
  } else {
    const aHeight = Math.round((rect.height - gutter) * node.ratio)
    const aRect = { x: rect.x, y: rect.y, width: rect.width, height: aHeight }
    const bRect = { x: rect.x, y: rect.y + aHeight + gutter, width: rect.width, height: rect.height - aHeight - gutter }
    flattenDockNode(node.a, aRect, out)
    flattenDockNode(node.b, bRect, out)
    out.dividers.push({
      splitId: node.splitId,
      dir: 'column',
      x: rect.x,
      y: rect.y + aHeight,
      width: rect.width,
      height: gutter,
    })
  }
}

/**
 * Flatten a dock split tree into pane rects + dividers (column-relative).
 * When a pane is smaller than the dock minimums, it is visually degraded out
 * (hidden) — mirroring the existing split-degradation: the surviving sibling
 * absorbs the space, and no divider is produced for the affected split.
 *
 * The column's single main pane (hosts homePicker + the collapse button) is
 * never degraded: whatever remains of the column stays alive around it, so the
 * panel can never go visually blank no matter how narrow the column gets.
 */
export function flattenDock(
  container: { width: number; height: number },
  dock: UieDockNode,
): { panes: UieDockGeometry['panes']; dividers: UieDockGeometry['dividers']; degradedPanes: string[] } {
  const out: DockFlattenResult = { panes: [], dividers: [], degradedPanes: [] }
  flattenDockNode(dock, { x: 0, y: 0, width: container.width, height: container.height }, out)

  // Collect the main pane id so the visibility pass can exempt it.
  const mainIds = new Set<string>()
  function collectMain(node: UieDockNode): void {
    if (node.kind === 'pane') {
      if (node.main) mainIds.add(node.paneId)
    } else {
      collectMain(node.a)
      collectMain(node.b)
    }
  }
  collectMain(dock)

  // Degrade: drop dividers that belong to splits whose subtree contains a
  // too-small pane. We recompute visibility bottom-up per split: a split is
  // "collapsed" if either child branch fails its minimum along that axis.
  // The main pane is exempt — the column always stays alive around it.
  const visibleSplits = new Set<string>()
  const degradedSet = new Set<string>()
  function markVisible(node: UieDockNode, rect: { width: number; height: number }): boolean {
    if (node.kind === 'pane') {
      const isMain = mainIds.has(node.paneId)
      const ok = isMain || (rect.width >= MIN_DOCK_PANE_WIDTH && rect.height >= MIN_DOCK_PANE_HEIGHT)
      if (!ok) {
        degradedSet.add(node.paneId)
        out.degradedPanes.push(node.paneId)
        const g = out.panes.find((p) => p.paneId === node.paneId)
        if (g) g.degraded = true
      }
      return ok
    }
    // Recompute child rects for the visibility pass.
    const gutter = DOCK_DIVIDER
    let ok: boolean
    if (node.dir === 'row') {
      const aWidth = Math.round((rect.width - gutter) * node.ratio)
      const aOk = markVisible(node.a, { width: aWidth, height: rect.height })
      const bOk = markVisible(node.b, { width: rect.width - aWidth - gutter, height: rect.height })
      ok = aOk && bOk
    } else {
      const aHeight = Math.round((rect.height - gutter) * node.ratio)
      const aOk = markVisible(node.a, { width: rect.width, height: aHeight })
      const bOk = markVisible(node.b, { width: rect.width, height: rect.height - aHeight - gutter })
      ok = aOk && bOk
    }
    if (ok) visibleSplits.add(node.splitId)
    return ok
  }
  markVisible(dock, container)
  out.dividers = out.dividers.filter((d) => visibleSplits.has(d.splitId))

  // Absorb: any surviving pane whose sibling branch fully degraded expands into
  // that branch's space, so the kept content fills the column instead of leaving
  // a dead half. Rects are keyed by paneId — re-walking a subtree overwrites the
  // stale half-rects, so the array keeps its original (tree) order.
  const absorbed = new Map<string, { x: number; y: number; width: number; height: number }>()
  function absorb(
    node: UieDockNode,
    rect: { x: number; y: number; width: number; height: number },
  ): boolean {
    if (node.kind === 'pane') {
      if (degradedSet.has(node.paneId)) return false
      absorbed.set(node.paneId, rect)
      return true
    }
    const gutter = DOCK_DIVIDER
    if (node.dir === 'row') {
      const aWidth = Math.round((rect.width - gutter) * node.ratio)
      const aRect = { x: rect.x, y: rect.y, width: aWidth, height: rect.height }
      const bRect = { x: rect.x + aWidth + gutter, y: rect.y, width: rect.width - aWidth - gutter, height: rect.height }
      const aOk = absorb(node.a, aRect)
      const bOk = absorb(node.b, bRect)
      if (aOk && !bOk) return absorb(node.a, rect)
      if (bOk && !aOk) return absorb(node.b, rect)
      return aOk || bOk
    }
    const aHeight = Math.round((rect.height - gutter) * node.ratio)
    const aRect = { x: rect.x, y: rect.y, width: rect.width, height: aHeight }
    const bRect = { x: rect.x, y: rect.y + aHeight + gutter, width: rect.width, height: rect.height - aHeight - gutter }
    const aOk = absorb(node.a, aRect)
    const bOk = absorb(node.b, bRect)
    if (aOk && !bOk) return absorb(node.a, rect)
    if (bOk && !aOk) return absorb(node.b, rect)
    return aOk || bOk
  }
  absorb(dock, { x: 0, y: 0, width: container.width, height: container.height })
  for (const p of out.panes) {
    const r = absorbed.get(p.paneId)
    if (r) {
      p.x = r.x
      p.y = r.y
      p.width = r.width
      p.height = r.height
    }
  }

  return out
}

export function computeGeometry(
  container: UieContainerSize,
  snapshot: UieLayoutSnapshot,
): UieGeometry {
  const columns: Record<UieSlotId, ColumnGeometry> = {} as Record<UieSlotId, ColumnGeometry>
  const splits: Record<string, SplitGeometry> = {}
  const degraded: UieGeometry['degraded'] = {
    collapsedRight: false,
    collapsedLeft: false,
    overlayRight: false,
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
  const sideGaps = Math.max(0, sideSlots.length - 1) * gap
  const leftCol = sideSlots.includes('left') ? snapshot.columns.left : null
  const rightCol = sideSlots.includes('right') ? snapshot.columns.right : null
  const leftW = leftCol ? effectiveWidth(leftCol) : 0
  const rightRaw = rightCol ? effectiveWidth(rightCol) : 0

  // Window-stacking overlay. When the user makes the right feature panel wider
  // than the center's comfort width allows side-by-side, the panel floats over
  // the chat (like stacked windows) instead of squeezing it: the center keeps
  // MIN_CHAT_COMFORT_WIDTH (the composer never wraps) and the panel covers at
  // most all-but-MIN_OVERLAY_STRIP of it.
  const rightFeature =
    rightCol !== null && !rightCol.collapsed && columnHostsHomePicker(snapshot, rightCol)
  const comfortFit = availableWidth - leftW - MIN_CHAT_COMFORT_WIDTH - sideGaps
  const overlayMax = Math.max(0, availableWidth - leftW - sideGaps - MIN_OVERLAY_STRIP)
  const overlayRight =
    rightFeature &&
    hasCenter &&
    rightRaw > comfortFit &&
    leftW + MIN_CHAT_COMFORT_WIDTH + sideGaps <= availableWidth
  degraded.overlayRight = overlayRight

  const collapsedSet = new Set<UieSlotId>()
  const sideWidths = new Map<UieSlotId, number>()
  for (const s of sideSlots) sideWidths.set(s, effectiveWidth(snapshot.columns[s]))

  if (overlayRight) {
    // Keep the persisted width but clamp so at least MIN_OVERLAY_STRIP of the
    // chat stays visible (the panel never covers the composer fully).
    sideWidths.set('right', Math.min(rightRaw, Math.min(MAX_DOCK_COLUMN_WIDTH, overlayMax)))
  } else {
    // Collapse right first, then left, while the side columns still don't fit.
    const reserved = (hasCenter ? MIN_CENTER_WIDTH : 0) + sideGaps
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
  }

  function sum(m: Map<UieSlotId, number>): number {
    let total = 0
    for (const v of m.values()) total += v
    return total
  }

  // Lay out columns.
  let cursorX = inset
  const columnGeoms: Record<UieSlotId, ColumnGeometry> = {} as Record<UieSlotId, ColumnGeometry>

  for (let i = 0; i < slots.length; i++) {
    const slotId = slots[i]
    const col = snapshot.columns[slotId]
    const isLast = i === slots.length - 1
    const isCollapsedVisual = collapsedSet.has(slotId)

    // Center is adaptive: fills the space between the (possibly collapsed) side
    // columns, but never below MIN_CENTER_WIDTH. In overlay the center keeps
    // MIN_CHAT_COMFORT_WIDTH so the composer stays wide enough to never wrap.
    let width = isCollapsedVisual ? COLLAPSED_COLUMN_WIDTH : effectiveWidth(col)
    if (slotId === 'right' && overlayRight) width = sideWidths.get('right') ?? width
    if (slotId === 'center' && !isCollapsedVisual) {
      if (overlayRight) {
        width = MIN_CHAT_COMFORT_WIDTH
      } else {
        const usedLeft = columnGeoms.left ? columnGeoms.left.x + columnGeoms.left.width + gap : 0
        const rightX = columnGeoms.right
          ? columnGeoms.right.x
          : container.width - inset - (collapsedSet.has('right') ? COLLAPSED_COLUMN_WIDTH : effectiveWidth(snapshot.columns.right ?? { width: 0, collapsed: false }))
        width = Math.max(MIN_CENTER_WIDTH, rightX - gap - usedLeft)
      }
    }

    const height = container.height - col.topInset - BOTTOM_INSET
    const y = col.topInset
    const geom: ColumnGeometry = {
      slotId,
      x: cursorX,
      y,
      width,
      height,
      effectiveWidth: width,
      topInset: col.topInset,
    }
    if (slotId === 'right' && overlayRight) {
      // Float over the chat, flush to the window's right edge.
      geom.x = container.width - inset - width
      geom.overlay = true
    }
    columnGeoms[slotId] = geom
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

  // Docks: flatten each column's dock tree (only when the column is visible
  // and not collapsed — collapsed/visually-degraded columns show the rail).
  const docks = {} as Record<UieSlotId, UieDockGeometry>
  for (const slotId of slots) {
    const col = snapshot.columns[slotId]
    const geom = columnGeoms[slotId]
    const collapsedVisually = col.collapsed || collapsedSet.has(slotId)
    if (!col.dock || collapsedVisually) {
      docks[slotId] = { slotId, panes: [], dividers: [], degradedPanes: [], collapsible: false }
      continue
    }
    const flat = flattenDock({ width: geom.width, height: geom.height }, col.dock)
    docks[slotId] = {
      slotId,
      panes: flat.panes,
      dividers: flat.dividers,
      degradedPanes: flat.degradedPanes,
      // Flag for the UI: the whole dock tree fits at ≥2 panes, so the
      // "restore single panel" affordance stays available.
      collapsible: flat.panes.filter((p) => !p.degraded).length > 1,
    }
  }

  return { columns, splits, docks, degraded }
}

/** Recursively collect a column's tab instance ids (stacks + dock panes). */
function columnTabIds(col: UieColumn): string[] {
  const ids = [...(col.primary?.tabIds ?? [])]
  if (col.secondary) ids.push(...col.secondary.tabIds)
  if (col.dock) {
    const walk = (node: UieDockNode): void => {
      if (node.kind === 'pane') ids.push(...node.tabIds)
      else {
        walk(node.a)
        walk(node.b)
      }
    }
    walk(col.dock)
  }
  return ids
}

/** True when a column hosts the pinned homePicker card (the feature panel). */
function columnHostsHomePicker(snapshot: UieLayoutSnapshot, col: UieColumn): boolean {
  return columnTabIds(col).some((id) => snapshot.cards[id]?.descriptorId === 'homePicker')
}

/**
 * Widest the right feature panel may be dragged while leaving at least
 * MIN_OVERLAY_STRIP of the chat visible when it overlays. The UI clamps
 * dock-column drag resizing to this: widening can slide the panel into
 * window-stacking overlay, but never covers the composer fully.
 */
export function maxOverlayColumnWidth(
  container: UieContainerSize,
  snapshot: UieLayoutSnapshot,
): number {
  const gap = snapshot.gap
  const availableWidth = container.width - 2 * snapshot.edgeInset
  const slots = snapshot.columnOrder.filter((s) => snapshot.columns[s])
  const sideSlots = slots.filter((s) => s !== 'center')
  const sideGaps = Math.max(0, sideSlots.length - 1) * gap
  const leftW = sideSlots.includes('left') ? effectiveWidth(snapshot.columns.left) : 0
  return Math.max(
    0,
    Math.min(MAX_DOCK_COLUMN_WIDTH, availableWidth - leftW - sideGaps - MIN_OVERLAY_STRIP),
  )
}
