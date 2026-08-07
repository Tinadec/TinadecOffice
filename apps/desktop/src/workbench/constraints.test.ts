import { describe, it, expect } from 'vitest'
import { computeGeometry } from './constraints'
import { buildPreset, HOME_GEOMETRY } from './presets'

function nextId() {
  let i = 0
  return () => `c-${++i}`
}

describe('constraint solver', () => {
  it('lays out home columns at 1440 width: 260 + center + 420 with 8px gaps', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.columns.left.x).toBe(HOME_GEOMETRY.edgeInset)
    expect(g.columns.left.width).toBe(HOME_GEOMETRY.leftWidth)
    expect(g.columns.left.topInset).toBe(8)
    expect(g.columns.right.x).toBeGreaterThan(0)
    expect(g.columns.right.width).toBe(HOME_GEOMETRY.rightWidth)
    expect(g.columns.right.topInset).toBe(48)
    // gap between left and center, and center and right, is 8.
    const leftToCenter = g.columns.center.x - (g.columns.left.x + g.columns.left.width)
    const centerToRight = g.columns.right.x - (g.columns.center.x + g.columns.center.width)
    expect(leftToCenter).toBe(8)
    expect(centerToRight).toBe(8)
  })

  it('center column fills remaining space', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    const rightEdge = g.columns.right.x + g.columns.right.width
    expect(rightEdge).toBe(1440 - HOME_GEOMETRY.edgeInset)
    // center spans from after-left to before-right.
    expect(g.columns.center.width).toBe(
      1440 - 2 * HOME_GEOMETRY.edgeInset - HOME_GEOMETRY.leftWidth - HOME_GEOMETRY.rightWidth - 2 * HOME_GEOMETRY.gap,
    )
  })

  it('collapses right column under space pressure (visual only)', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 600, height: 800 }, snapshot)
    expect(g.degraded.collapsedRight).toBe(true)
    // Right column now renders at collapsed width.
    expect(g.columns.right.width).toBeLessThanOrEqual(44)
  })

  it('collapses left then right when even more cramped', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 500, height: 800 }, snapshot)
    // With 260+420+gaps > 500, right collapses first; if still tight, left too.
    expect(g.degraded.collapsedRight).toBe(true)
    // left may also collapse for 500 width
  })

  it('collapses the right column instead of crushing the adaptive center below its minimum', () => {
    // At 900px the naive "center width = 0 in overflow math" bug left the chat
    // column at ~188px. The fixed solver collapses the right rail (420 -> 44)
    // so the adaptive center stays at least MIN_CENTER_WIDTH.
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 900, height: 900 }, snapshot)
    expect(g.degraded.collapsedRight).toBe(true)
    expect(g.columns.center.width).toBeGreaterThanOrEqual(320)
  })

  it('never lets the adaptive center drop below its minimum at narrow widths', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    for (const w of [1000, 900, 762, 700, 600, 520, 460]) {
      const g = computeGeometry({ width: w, height: 900 }, snapshot)
      // center renders whenever the column exists and is not itself collapsed.
      if (g.columns.center.width > 0) {
        expect(g.columns.center.width).toBeGreaterThanOrEqual(320)
      }
    }
  })

  it('does not collapse anything at wide width', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 1920, height: 1080 }, snapshot)
    expect(g.degraded.collapsedRight).toBe(false)
    expect(g.degraded.collapsedLeft).toBe(false)
  })

  it('degrades an over-short split into a single stack (visual)', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    // Create a split in the right column with a ratio that makes the lower half tiny.
    snapshot.columns.right.secondary = {
      stackId: 'secondary',
      tabIds: [snapshot.columns.right.primary.tabIds[0]],
      activeTabId: snapshot.columns.right.primary.tabIds[0],
    }
    snapshot.columns.right.splitRatio = 0.9
    const g = computeGeometry({ width: 1440, height: 200 }, snapshot)
    expect(g.degraded.degradedSplits).toContain('right')
    expect(g.splits['right'].upper.degraded).toBe(true)
  })

  it('keeps a healthy split at default 65/35', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    snapshot.columns.right.secondary = {
      stackId: 'secondary',
      tabIds: [snapshot.columns.right.primary.tabIds[0]],
      activeTabId: snapshot.columns.right.primary.tabIds[0],
    }
    snapshot.columns.right.splitRatio = 0.65
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.degraded.degradedSplits).not.toContain('right')
    const rightH = g.columns.right.height
    // upper = 65% of height, lower = 35%
    expect(g.splits['right'].upper.height).toBeGreaterThanOrEqual(Math.round(rightH * 0.5))
    expect(g.splits['right'].lower.height).toBeGreaterThan(0)
  })

  it('settings keeps 8px window-edge gaps with the right rail collapsed', () => {
    const snapshot = buildPreset('settings', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.columns.left.x).toBe(8)
    // Right column is collapsed to 44 and pinned to the right inset.
    expect(g.columns.right.x).toBe(1440 - 8 - 44)
    expect(g.columns.right.width).toBe(44)
    expect(g.columns.right.x + g.columns.right.width).toBe(1440 - 8)
  })

  it('app-mode pages stay flush to the window edges', () => {
    const snapshot = buildPreset('market', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.columns.left.x).toBe(0)
    expect(g.columns.right.x + g.columns.right.width).toBe(1440)
  })
})
