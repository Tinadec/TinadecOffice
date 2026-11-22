// Pure TypeScript Figma-style Smart Guides, Multi-Select Alignment, Distribution,
// and Bounding Section Calculation.

export interface SpatialRect {
  id: string
  x: number
  y: number
  width: number
  height: number
}

export interface SmartGuideLine {
  orientation: 'vertical' | 'horizontal'
  position: number
  start: number
  end: number
  kind: 'edge' | 'center' | 'spacing'
  label?: string
}

export interface SmartGuidesResult {
  x: number
  y: number
  dx: number
  dy: number
  snappedX: boolean
  snappedY: boolean
  guides: SmartGuideLine[]
}

export type SpatialAlignMode = 'left' | 'centerX' | 'right' | 'top' | 'centerY' | 'bottom'
export type SpatialDistributeAxis = 'horizontal' | 'vertical'

interface EnrichedRect extends SpatialRect {
  right: number
  bottom: number
  cx: number
  cy: number
}

interface AxisSnapCandidate {
  delta: number
  priority: number
}

const EPS = 1e-6

function isValidRect(r: SpatialRect): boolean {
  return (
    Boolean(r)
    && typeof r.id === 'string'
    && Number.isFinite(r.x)
    && Number.isFinite(r.y)
    && Number.isFinite(r.width)
    && Number.isFinite(r.height)
    && r.width > 0
    && r.height > 0
  )
}

function enrichRect(r: SpatialRect): EnrichedRect {
  return {
    ...r,
    right: r.x + r.width,
    bottom: r.y + r.height,
    cx: r.x + r.width / 2,
    cy: r.y + r.height / 2,
  }
}

function formatGapLabel(gap: number): string {
  const rounded = Math.round(gap * 100) / 100
  return String(rounded)
}

function pickBestCandidate(candidates: AxisSnapCandidate[], threshold: number): number | null {
  let best: AxisSnapCandidate | null = null
  for (const c of candidates) {
    const abs = Math.abs(c.delta)
    if (abs > threshold + EPS) continue
    if (!best) {
      best = c
      continue
    }
    const bestAbs = Math.abs(best.delta)
    if (abs < bestAbs - EPS) {
      best = c
    } else if (Math.abs(abs - bestAbs) <= EPS) {
      if (c.priority < best.priority || (c.priority === best.priority && c.delta < best.delta)) {
        best = c
      }
    }
  }
  return best ? best.delta : null
}

function mergeGuideLine(target: SmartGuideLine[], incoming: SmartGuideLine): void {
  const start = Math.min(incoming.start, incoming.end)
  const end = Math.max(incoming.start, incoming.end)
  for (const existing of target) {
    if (
      existing.orientation === incoming.orientation
      && existing.kind === incoming.kind
      && Math.abs(existing.position - incoming.position) <= EPS
    ) {
      if (incoming.kind === 'spacing') {
        // Spacing segments stay per-gap; only deduplicate identical intervals.
        if (Math.abs(existing.start - start) <= EPS && Math.abs(existing.end - end) <= EPS) {
          return
        }
        continue
      }
      existing.start = Math.min(existing.start, start)
      existing.end = Math.max(existing.end, end)
      return
    }
  }
  target.push({ ...incoming, start, end })
}

function bandOverlaps(aStart: number, aEnd: number, bStart: number, bEnd: number, slack: number): boolean {
  return Math.min(aEnd, bEnd) + slack >= Math.max(aStart, bStart)
}

/**
 * Computes edge (`left`/`right`/`top`/`bottom`), center (`centerX`/`centerY`),
 * and equal-gap spacing snap offsets plus visual guide coordinates within `threshold` world px.
 */
export function computeSmartGuides(
  moving: SpatialRect,
  others: readonly SpatialRect[],
  threshold = 6,
): SmartGuidesResult {
  if (!isValidRect(moving) || !Number.isFinite(threshold) || threshold < 0) {
    return {
      x: moving?.x ?? 0,
      y: moving?.y ?? 0,
      dx: 0,
      dy: 0,
      snappedX: false,
      snappedY: false,
      guides: [],
    }
  }

  const peers = others.filter(o => isValidRect(o) && o.id !== moving.id).map(enrichRect)
  if (peers.length === 0) {
    return {
      x: moving.x,
      y: moving.y,
      dx: 0,
      dy: 0,
      snappedX: false,
      snappedY: false,
      guides: [],
    }
  }

  const m = enrichRect(moving)
  const xCandidates: AxisSnapCandidate[] = []
  const yCandidates: AxisSnapCandidate[] = []

  // 1. Edge & Center candidates
  for (const p of peers) {
    // X edges (priority 0) & center (priority 1)
    xCandidates.push(
      { delta: p.x - m.x, priority: 0 },
      { delta: p.right - m.x, priority: 0 },
      { delta: p.x - m.right, priority: 0 },
      { delta: p.right - m.right, priority: 0 },
      { delta: p.cx - m.cx, priority: 1 },
    )
    // Y edges (priority 0) & center (priority 1)
    yCandidates.push(
      { delta: p.y - m.y, priority: 0 },
      { delta: p.bottom - m.y, priority: 0 },
      { delta: p.y - m.bottom, priority: 0 },
      { delta: p.bottom - m.bottom, priority: 0 },
      { delta: p.cy - m.cy, priority: 1 },
    )
  }

  // 2. Equal-gap spacing candidates (priority 2)
  const rowPeers = peers
    .filter(p => bandOverlaps(p.y, p.bottom, m.y, m.bottom, Math.max(m.height, 48)))
    .sort((a, b) => a.x - b.x || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))

  for (let i = 0; i < rowPeers.length; i++) {
    const a = rowPeers[i]
    for (let j = i + 1; j < rowPeers.length; j++) {
      const b = rowPeers[j]
      if (b.x < a.right - EPS) continue
      // Check no intermediate rowPeer sits strictly inside (a.right, b.x)
      let obstructed = false
      for (let k = i + 1; k < j; k++) {
        const mid = rowPeers[k]
        if (mid.right > a.right + EPS && mid.x < b.x - EPS) {
          obstructed = true
          break
        }
      }
      if (obstructed) continue

      const gapAB = b.x - a.right
      if (gapAB >= 0) {
        // [a] --gap-- [b] --gap-- [moving]
        xCandidates.push({ delta: b.right + gapAB - m.x, priority: 2 })
        // [moving] --gap-- [a] --gap-- [b]
        xCandidates.push({ delta: a.x - gapAB - m.width - m.x, priority: 2 })
      }
      // [a] --gap-- [moving] --gap-- [b]
      const innerAvail = b.x - a.right - m.width
      if (innerAvail > 0) {
        const innerGap = innerAvail / 2
        xCandidates.push({ delta: a.right + innerGap - m.x, priority: 2 })
      }
    }
  }

  const colPeers = peers
    .filter(p => bandOverlaps(p.x, p.right, m.x, m.right, Math.max(m.width, 48)))
    .sort((a, b) => a.y - b.y || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))

  for (let i = 0; i < colPeers.length; i++) {
    const a = colPeers[i]
    for (let j = i + 1; j < colPeers.length; j++) {
      const b = colPeers[j]
      if (b.y < a.bottom - EPS) continue
      let obstructed = false
      for (let k = i + 1; k < j; k++) {
        const mid = colPeers[k]
        if (mid.bottom > a.bottom + EPS && mid.y < b.y - EPS) {
          obstructed = true
          break
        }
      }
      if (obstructed) continue

      const gapAB = b.y - a.bottom
      if (gapAB >= 0) {
        // [a] --gap-- [b] --gap-- [moving]
        yCandidates.push({ delta: b.bottom + gapAB - m.y, priority: 2 })
        // [moving] --gap-- [a] --gap-- [b]
        yCandidates.push({ delta: a.y - gapAB - m.height - m.y, priority: 2 })
      }
      // [a] --gap-- [moving] --gap-- [b]
      const innerAvail = b.y - a.bottom - m.height
      if (innerAvail > 0) {
        const innerGap = innerAvail / 2
        yCandidates.push({ delta: a.bottom + innerGap - m.y, priority: 2 })
      }
    }
  }

  const bestDx = pickBestCandidate(xCandidates, threshold)
  const bestDy = pickBestCandidate(yCandidates, threshold)
  const dx = bestDx ?? 0
  const dy = bestDy ?? 0
  const snappedX = bestDx !== null
  const snappedY = bestDy !== null
  const snapped = enrichRect({
    id: moving.id,
    x: moving.x + dx,
    y: moving.y + dy,
    width: moving.width,
    height: moving.height,
  })

  const guides: SmartGuideLine[] = []

  if (snappedX) {
    for (const p of peers) {
      const spanStart = Math.min(snapped.y, p.y)
      const spanEnd = Math.max(snapped.bottom, p.bottom)

      for (const mx of [snapped.x, snapped.right]) {
        for (const px of [p.x, p.right]) {
          if (Math.abs(mx - px) <= EPS) {
            mergeGuideLine(guides, {
              orientation: 'vertical',
              position: px,
              start: spanStart,
              end: spanEnd,
              kind: 'edge',
            })
          }
        }
      }

      if (Math.abs(snapped.cx - p.cx) <= EPS) {
        mergeGuideLine(guides, {
          orientation: 'vertical',
          position: p.cx,
          start: spanStart,
          end: spanEnd,
          kind: 'center',
        })
      }
    }

    // Horizontal equal-spacing guides at snapped position
    const finalRow = [
      ...peers.filter(p => bandOverlaps(p.y, p.bottom, snapped.y, snapped.bottom, Math.max(snapped.height, 48))),
      snapped,
    ].sort((a, b) => a.x - b.x || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))

    for (let i = 0; i + 2 < finalRow.length; i++) {
      const r0 = finalRow[i]
      const r1 = finalRow[i + 1]
      const r2 = finalRow[i + 2]
      if (r0.id !== snapped.id && r1.id !== snapped.id && r2.id !== snapped.id) continue
      const g1 = r1.x - r0.right
      const g2 = r2.x - r1.right
      if (g1 > EPS && Math.abs(g1 - g2) <= EPS) {
        const midY = (Math.max(r0.y, r1.y, r2.y) + Math.min(r0.bottom, r1.bottom, r2.bottom)) / 2
        const label = formatGapLabel(g1)
        mergeGuideLine(guides, {
          orientation: 'horizontal',
          position: midY,
          start: r0.right,
          end: r1.x,
          kind: 'spacing',
          label,
        })
        mergeGuideLine(guides, {
          orientation: 'horizontal',
          position: midY,
          start: r1.right,
          end: r2.x,
          kind: 'spacing',
          label,
        })
      }
    }
  }

  if (snappedY) {
    for (const p of peers) {
      const spanStart = Math.min(snapped.x, p.x)
      const spanEnd = Math.max(snapped.right, p.right)

      for (const my of [snapped.y, snapped.bottom]) {
        for (const py of [p.y, p.bottom]) {
          if (Math.abs(my - py) <= EPS) {
            mergeGuideLine(guides, {
              orientation: 'horizontal',
              position: py,
              start: spanStart,
              end: spanEnd,
              kind: 'edge',
            })
          }
        }
      }

      if (Math.abs(snapped.cy - p.cy) <= EPS) {
        mergeGuideLine(guides, {
          orientation: 'horizontal',
          position: p.cy,
          start: spanStart,
          end: spanEnd,
          kind: 'center',
        })
      }
    }

    // Vertical equal-spacing guides at snapped position
    const finalCol = [
      ...peers.filter(p => bandOverlaps(p.x, p.right, snapped.x, snapped.right, Math.max(snapped.width, 48))),
      snapped,
    ].sort((a, b) => a.y - b.y || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))

    for (let i = 0; i + 2 < finalCol.length; i++) {
      const r0 = finalCol[i]
      const r1 = finalCol[i + 1]
      const r2 = finalCol[i + 2]
      if (r0.id !== snapped.id && r1.id !== snapped.id && r2.id !== snapped.id) continue
      const g1 = r1.y - r0.bottom
      const g2 = r2.y - r1.bottom
      if (g1 > EPS && Math.abs(g1 - g2) <= EPS) {
        const midX = (Math.max(r0.x, r1.x, r2.x) + Math.min(r0.right, r1.right, r2.right)) / 2
        const label = formatGapLabel(g1)
        mergeGuideLine(guides, {
          orientation: 'vertical',
          position: midX,
          start: r0.bottom,
          end: r1.y,
          kind: 'spacing',
          label,
        })
        mergeGuideLine(guides, {
          orientation: 'vertical',
          position: midX,
          start: r1.bottom,
          end: r2.y,
          kind: 'spacing',
          label,
        })
      }
    }
  }

  return {
    x: snapped.x,
    y: snapped.y,
    dx,
    dy,
    snappedX,
    snappedY,
    guides,
  }
}

/**
 * Aligns multiple rectangles along their collective selection bounds.
 */
export function alignRects(
  rects: readonly SpatialRect[],
  mode: SpatialAlignMode,
): Array<{ id: string; x: number; y: number }> {
  if (rects.length === 0) return []
  if (rects.length === 1) return [{ id: rects[0].id, x: rects[0].x, y: rects[0].y }]

  const minX = Math.min(...rects.map(r => r.x))
  const maxX = Math.max(...rects.map(r => r.x + r.width))
  const midX = (minX + maxX) / 2
  const minY = Math.min(...rects.map(r => r.y))
  const maxY = Math.max(...rects.map(r => r.y + r.height))
  const midY = (minY + maxY) / 2

  return rects.map((r) => {
    switch (mode) {
      case 'left':
        return { id: r.id, x: minX, y: r.y }
      case 'centerX':
        return { id: r.id, x: midX - r.width / 2, y: r.y }
      case 'right':
        return { id: r.id, x: maxX - r.width, y: r.y }
      case 'top':
        return { id: r.id, x: r.x, y: minY }
      case 'centerY':
        return { id: r.id, x: r.x, y: midY - r.height / 2 }
      case 'bottom':
        return { id: r.id, x: r.x, y: maxY - r.height }
    }
  })
}

/**
 * Distributes 3+ rectangles with equal inter-item gaps along the chosen axis
 * while keeping outer span anchors stable.
 */
export function distributeRects(
  rects: readonly SpatialRect[],
  axis: SpatialDistributeAxis,
): Array<{ id: string; x: number; y: number }> {
  if (rects.length <= 2) {
    return rects.map(r => ({ id: r.id, x: r.x, y: r.y }))
  }

  const placed = new Map<string, { x: number; y: number }>()

  if (axis === 'horizontal') {
    const sorted = [...rects].sort(
      (a, b) => a.x - b.x || a.x + a.width / 2 - (b.x + b.width / 2) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0),
    )
    const spanStart = Math.min(...sorted.map(r => r.x))
    const spanEnd = Math.max(...sorted.map(r => r.x + r.width))
    const totalWidth = sorted.reduce((sum, r) => sum + r.width, 0)
    const gap = (spanEnd - spanStart - totalWidth) / (sorted.length - 1)
    let cursor = spanStart
    for (const r of sorted) {
      placed.set(r.id, { x: cursor, y: r.y })
      cursor += r.width + gap
    }
  } else {
    const sorted = [...rects].sort(
      (a, b) => a.y - b.y || a.y + a.height / 2 - (b.y + b.height / 2) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0),
    )
    const spanStart = Math.min(...sorted.map(r => r.y))
    const spanEnd = Math.max(...sorted.map(r => r.y + r.height))
    const totalHeight = sorted.reduce((sum, r) => sum + r.height, 0)
    const gap = (spanEnd - spanStart - totalHeight) / (sorted.length - 1)
    let cursor = spanStart
    for (const r of sorted) {
      placed.set(r.id, { x: r.x, y: cursor })
      cursor += r.height + gap
    }
  }

  return rects.map((r) => {
    const next = placed.get(r.id)
    return { id: r.id, x: next ? next.x : r.x, y: next ? next.y : r.y }
  })
}

/**
 * Computes a Section frame bounding box enclosing `rects` with symmetric padding
 * and top header clearance.
 */
export function computeBoundingSectionRect(
  rects: readonly SpatialRect[],
  padding = 36,
  headerHeight = 40,
): { x: number; y: number; width: number; height: number } {
  const safePadding = Number.isFinite(padding) ? Math.max(0, padding) : 36
  const safeHeader = Number.isFinite(headerHeight) ? Math.max(0, headerHeight) : 40
  if (rects.length === 0) {
    return {
      x: 0,
      y: 0,
      width: 320 + safePadding * 2,
      height: 200 + safePadding * 2 + safeHeader,
    }
  }

  const minX = Math.min(...rects.map(r => r.x))
  const minY = Math.min(...rects.map(r => r.y))
  const maxX = Math.max(...rects.map(r => r.x + r.width))
  const maxY = Math.max(...rects.map(r => r.y + r.height))

  return {
    x: minX - safePadding,
    y: minY - safePadding - safeHeader,
    width: Math.max(1, maxX - minX + safePadding * 2),
    height: Math.max(1, maxY - minY + safePadding * 2 + safeHeader),
  }
}
