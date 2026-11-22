import { describe, expect, it } from 'vitest'
import {
  alignRects,
  computeBoundingSectionRect,
  computeSmartGuides,
  distributeRects,
  type SpatialRect,
} from './spatialSmartGuides'

describe('spatialSmartGuides', () => {
  it('snaps moving rect to left/right/top/bottom edges and center lines within threshold', () => {
    const others: SpatialRect[] = [
      { id: 'peer-1', x: 100, y: 200, width: 200, height: 120 },
      { id: 'peer-2', x: 450, y: 500, width: 160, height: 100 },
    ]

    // Left edge within 4px (104 -> 100) and centerY within 3px (peer-1 cy=260, moving h=80 -> y=223, cy=263 -> y=220)
    const snapped = computeSmartGuides(
      { id: 'moving', x: 104, y: 223, width: 140, height: 80 },
      others,
      6,
    )
    expect(snapped.snappedX).toBe(true)
    expect(snapped.snappedY).toBe(true)
    expect(snapped.x).toBe(100)
    expect(snapped.y).toBe(220)
    expect(snapped.dx).toBe(-4)
    expect(snapped.dy).toBe(-3)
    expect(snapped.guides).toContainEqual({
      orientation: 'vertical',
      position: 100,
      start: 200,
      end: 320,
      kind: 'edge',
    })
    expect(snapped.guides).toContainEqual({
      orientation: 'horizontal',
      position: 260,
      start: 100,
      end: 300,
      kind: 'center',
    })

    // Outside threshold -> untouched, zero guides
    const unsnapped = computeSmartGuides(
      { id: 'moving', x: 112, y: 255, width: 140, height: 80 },
      others,
      6,
    )
    expect(unsnapped).toEqual({
      x: 112,
      y: 255,
      dx: 0,
      dy: 0,
      snappedX: false,
      snappedY: false,
      guides: [],
    })
  })

  it('computes equal-gap spacing snaps and visual spacing guide segments (after, before, and between)', () => {
    const rowPeers: SpatialRect[] = [
      { id: 'a', x: 0, y: 100, width: 100, height: 100 },
      { id: 'b', x: 150, y: 100, width: 100, height: 100 }, // 50px gap
    ]

    // 1. After pair: target x = 250 + 50 = 300
    const afterSnap = computeSmartGuides(
      { id: 'm', x: 296, y: 100, width: 100, height: 100 },
      rowPeers,
      6,
    )
    expect(afterSnap.x).toBe(300)
    const spacingGuides = afterSnap.guides.filter(g => g.kind === 'spacing')
    expect(spacingGuides).toEqual([
      { orientation: 'horizontal', position: 150, start: 100, end: 150, kind: 'spacing', label: '50' },
      { orientation: 'horizontal', position: 150, start: 250, end: 300, kind: 'spacing', label: '50' },
    ])

    // 2. Between wide pair: a=[0..100], c=[320..420], moving width=100 -> equal 60px gaps at x=160
    const betweenSnap = computeSmartGuides(
      { id: 'm', x: 164, y: 102, width: 100, height: 100 },
      [
        { id: 'a', x: 0, y: 100, width: 100, height: 100 },
        { id: 'c', x: 320, y: 100, width: 100, height: 100 },
      ],
      6,
    )
    expect(betweenSnap.x).toBe(160)
    expect(betweenSnap.y).toBe(100)
    expect(betweenSnap.guides.filter(g => g.kind === 'spacing')).toEqual([
      { orientation: 'horizontal', position: 150, start: 100, end: 160, kind: 'spacing', label: '60' },
      { orientation: 'horizontal', position: 150, start: 260, end: 320, kind: 'spacing', label: '60' },
    ])

    // 3. Vertical column equal spacing: top=[0..80], mid=[120..200] (40px gap) -> target y = 240
    const verticalSnap = computeSmartGuides(
      { id: 'm', x: 50, y: 243, width: 120, height: 80 },
      [
        { id: 't', x: 50, y: 0, width: 120, height: 80 },
        { id: 'mid', x: 50, y: 120, width: 120, height: 80 },
      ],
      6,
    )
    expect(verticalSnap.y).toBe(240)
    expect(verticalSnap.guides.filter(g => g.kind === 'spacing')).toEqual([
      { orientation: 'vertical', position: 110, start: 80, end: 120, kind: 'spacing', label: '40' },
      { orientation: 'vertical', position: 110, start: 200, end: 240, kind: 'spacing', label: '40' },
    ])
  })

  it('aligns rectangles across all 6 modes', () => {
    const rects: SpatialRect[] = [
      { id: 'r1', x: 20, y: 40, width: 100, height: 60 },
      { id: 'r2', x: 180, y: 140, width: 200, height: 100 }, // maxX=380, maxY=240 -> midX=200, midY=140
    ]

    expect(alignRects([], 'left')).toEqual([])
    expect(alignRects([rects[0]], 'right')).toEqual([{ id: 'r1', x: 20, y: 40 }])

    expect(alignRects(rects, 'left')).toEqual([
      { id: 'r1', x: 20, y: 40 },
      { id: 'r2', x: 20, y: 140 },
    ])
    expect(alignRects(rects, 'centerX')).toEqual([
      { id: 'r1', x: 150, y: 40 },
      { id: 'r2', x: 100, y: 140 },
    ])
    expect(alignRects(rects, 'right')).toEqual([
      { id: 'r1', x: 280, y: 40 },
      { id: 'r2', x: 180, y: 140 },
    ])
    expect(alignRects(rects, 'top')).toEqual([
      { id: 'r1', x: 20, y: 40 },
      { id: 'r2', x: 180, y: 40 },
    ])
    expect(alignRects(rects, 'centerY')).toEqual([
      { id: 'r1', x: 20, y: 110 },
      { id: 'r2', x: 180, y: 90 },
    ])
    expect(alignRects(rects, 'bottom')).toEqual([
      { id: 'r1', x: 20, y: 180 },
      { id: 'r2', x: 180, y: 140 },
    ])
  })

  it('distributes 3+ rectangles with equal inter-item gaps horizontally and vertically', () => {
    // Unsorted input order: total span [0..500], widths 100 + 80 + 120 = 300 -> remaining 200 / 2 = 100px gap
    const hRects: SpatialRect[] = [
      { id: 'mid', x: 110, y: 20, width: 80, height: 50 },
      { id: 'right', x: 380, y: 30, width: 120, height: 50 },
      { id: 'left', x: 0, y: 10, width: 100, height: 50 },
    ]
    const distributedH = distributeRects(hRects, 'horizontal')
    expect(distributedH).toEqual([
      { id: 'mid', x: 200, y: 20 },
      { id: 'right', x: 380, y: 30 },
      { id: 'left', x: 0, y: 10 },
    ])

    // Vertical span [10..410], heights 60 + 100 + 80 = 240 -> remaining 160 / 2 = 80px gap
    const vRects: SpatialRect[] = [
      { id: 'v1', x: 10, y: 10, width: 100, height: 60 },
      { id: 'v2', x: 20, y: 90, width: 100, height: 100 },
      { id: 'v3', x: 30, y: 330, width: 100, height: 80 },
    ]
    expect(distributeRects(vRects, 'vertical')).toEqual([
      { id: 'v1', x: 10, y: 10 },
      { id: 'v2', x: 20, y: 150 },
      { id: 'v3', x: 30, y: 330 },
    ])

    expect(distributeRects(vRects.slice(0, 2), 'vertical')).toEqual([
      { id: 'v1', x: 10, y: 10 },
      { id: 'v2', x: 20, y: 90 },
    ])
  })

  it('computes bounding Section rectangles with symmetric padding and header clearance', () => {
    const rects: SpatialRect[] = [
      { id: 'a', x: -100, y: 50, width: 200, height: 100 },
      { id: 'b', x: 200, y: 120, width: 150, height: 180 }, // minX=-100, maxX=350 (w=450), minY=50, maxY=300 (h=250)
    ]
    expect(computeBoundingSectionRect(rects)).toEqual({
      x: -136,
      y: -26,
      width: 522,
      height: 362,
    })
    expect(computeBoundingSectionRect(rects, 24, 32)).toEqual({
      x: -124,
      y: -6,
      width: 498,
      height: 330,
    })
    expect(computeBoundingSectionRect([]).width).toBeGreaterThan(0)
  })
})
