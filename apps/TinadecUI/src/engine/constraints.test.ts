import { describe, it, expect } from 'vitest'
import { computeGeometry, flattenDock, maxOverlayColumnWidth } from './constraints'
import { buildPreset, HOME_GEOMETRY } from './presets'
import type { UieDockNode, UieDockPane } from './types'

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

  it('overlays the right feature column instead of crushing the adaptive center below comfort at narrow widths', () => {
    // At 900px the panel (420) can't sit beside a comfortable center, so the
    // solver floats it over the chat (window-stacking) and the center keeps
    // MIN_CHAT_COMFORT_WIDTH — the composer never wraps.
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 900, height: 900 }, snapshot)
    expect(g.degraded.overlayRight).toBe(true)
    expect(g.degraded.collapsedRight).toBe(false)
    expect(g.columns.center.width).toBeGreaterThanOrEqual(560)
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
    const snapshot = buildPreset('code', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.columns.left.x).toBe(0)
    expect(g.columns.right.x + g.columns.right.width).toBe(1440)
  })
})

describe('dock flattening', () => {
  function pane(paneId: string, tabIds: string[], main = false): UieDockPane {
    return { kind: 'pane', paneId, main, tabIds, activeTabId: tabIds[0] ?? null }
  }

  function homeWithDock(dock: UieDockNode) {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    snapshot.columns.right.dock = dock
    snapshot.columns.right.primary.tabIds = []
    return snapshot
  }

  it('single pane fills the column', () => {
    const snapshot = homeWithDock(pane('main', ['x'], true))
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    const dock = g.docks['right']
    expect(dock.panes).toHaveLength(1)
    expect(dock.panes[0]).toMatchObject({ paneId: 'main', x: 0, y: 0 })
    expect(dock.panes[0].width).toBe(g.columns.right.width)
    expect(dock.panes[0].height).toBe(g.columns.right.height)
    expect(dock.dividers).toHaveLength(0)
  })

  it('row split lays out left/right panes with a vertical divider', () => {
    const snapshot = homeWithDock({
      kind: 'split', splitId: 's', dir: 'row', ratio: 0.5,
      a: pane('a', ['x']), b: pane('b', ['y']),
    })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    const dock = g.docks['right']
    const pa = dock.panes.find((p) => p.paneId === 'a')!
    const pb = dock.panes.find((p) => p.paneId === 'b')!
    expect(pa.x).toBe(0)
    expect(pa.y).toBe(0)
    expect(pb.x).toBeGreaterThan(pa.x)
    expect(pb.y).toBe(0)
    // Divider sits between them, 4px wide.
    const div = dock.dividers[0]
    expect(div.splitId).toBe('s')
    expect(div.dir).toBe('row')
    expect(div.width).toBe(4)
    expect(div.x).toBe(pa.x + pa.width)
    expect(div.y).toBe(0)
    expect(div.height).toBe(g.columns.right.height)
    // Panes do not overlap and leave the divider gap.
    expect(pb.x - (pa.x + pa.width)).toBe(4)
  })

  it('column split lays out top/bottom panes with a horizontal divider', () => {
    const snapshot = homeWithDock({
      kind: 'split', splitId: 's', dir: 'column', ratio: 0.65,
      a: pane('top', ['x']), b: pane('bot', ['y']),
    })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    const dock = g.docks['right']
    const pt = dock.panes.find((p) => p.paneId === 'top')!
    const pb = dock.panes.find((p) => p.paneId === 'bot')!
    expect(pt.x).toBe(0)
    expect(pt.y).toBe(0)
    expect(pb.y).toBeGreaterThan(pt.y)
    const div = dock.dividers[0]
    expect(div.dir).toBe('column')
    expect(div.height).toBe(4)
    expect(div.y).toBe(pt.y + pt.height)
    expect(pb.y - (pt.y + pt.height)).toBe(4)
  })

  it('mixed row+column tree flattens all panes and dividers', () => {
    const snapshot = homeWithDock({
      kind: 'split', splitId: 'r', dir: 'column', ratio: 0.5,
      a: {
        kind: 'split', splitId: 'c', dir: 'row', ratio: 0.5,
        a: pane('a', ['x']), b: pane('b', ['y']),
      },
      b: pane('c', ['z']),
    })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    const dock = g.docks['right']
    expect(dock.panes).toHaveLength(3)
    expect(dock.dividers).toHaveLength(2)
    const dividerDirs = dock.dividers.map((d) => d.dir).sort()
    expect(dividerDirs).toEqual(['column', 'row'])
  })

  it('degrades a pane below the minimum (visual only)', () => {
    const snapshot = homeWithDock({
      kind: 'split', splitId: 's', dir: 'column', ratio: 0.05,
      a: pane('tiny', ['x']), b: pane('big', ['y']),
    })
    // Column height ~864px → a is ~43px (< MIN_DOCK_PANE_HEIGHT=120) → degraded.
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    const dock = g.docks['right']
    expect(dock.degradedPanes).toContain('tiny')
    // Degraded split hides its divider.
    expect(dock.dividers).toHaveLength(0)
    expect(dock.panes.find((p) => p.paneId === 'tiny')?.degraded).toBe(true)
  })

  it('does not degrade healthy panes', () => {
    const snapshot = homeWithDock({
      kind: 'split', splitId: 's', dir: 'row', ratio: 0.5,
      a: pane('a', ['x']), b: pane('b', ['y']),
    })
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.docks['right'].degradedPanes).toEqual([])
    expect(g.docks['right'].dividers).toHaveLength(1)
  })

  it('emits empty dock geometry when the column is collapsed', () => {
    const snapshot = homeWithDock({
      kind: 'split', splitId: 's', dir: 'row', ratio: 0.5,
      a: pane('a', ['x']), b: pane('b', ['y']),
    })
    snapshot.columns.right.collapsed = true
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.docks['right'].panes).toHaveLength(0)
    expect(g.docks['right'].dividers).toHaveLength(0)
  })

  it('flattenDock is a pure column-relative layout', () => {
    const flat = flattenDock({ width: 420, height: 800 }, {
      kind: 'split', splitId: 's', dir: 'row', ratio: 0.5,
      a: pane('a', ['x']), b: pane('b', ['y']),
    })
    expect(flat.panes[0]).toMatchObject({ x: 0, y: 0 })
    expect(flat.panes[0].width).toBe((420 - 4) / 2)
    expect(flat.panes[1].width).toBe((420 - 4) / 2)
  })
})

describe('right-panel overlay', () => {
  it('overlays the feature panel when it can no longer sit beside a comfortable center', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    snapshot.columns.right.width = 700
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.degraded.overlayRight).toBe(true)
    expect(g.degraded.collapsedRight).toBe(false)
    // The chat keeps its comfort width; the panel floats flush to the right edge.
    expect(g.columns.center.width).toBe(560)
    expect(g.columns.right.overlay).toBe(true)
    expect(g.columns.right.x).toBe(1440 - 8 - 700)
  })

  it('keeps the panel side-by-side when it fits beside the comfortable center', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    snapshot.columns.right.width = 500
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.degraded.overlayRight).toBe(false)
    expect(g.columns.center.width).toBe(1424 - 260 - 500 - 16)
    expect(g.columns.right.overlay).toBeUndefined()
  })

  it('never covers the chat fully — clamps the overlaid panel to the visible strip', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    snapshot.columns.right.width = 1200
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    // available 1424 − left 260 − gap 8 − strip 300 = 856 max rendered.
    expect(g.degraded.overlayRight).toBe(true)
    expect(g.columns.right.width).toBe(856)
    // The visible chat = from the center's left edge to the panel's left edge.
    const strip = g.columns.right.x - g.columns.center.x
    expect(strip).toBe(300)
  })

  it('rails instead of overlaying when the window cannot hold comfortable chat + left column', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    const g = computeGeometry({ width: 600, height: 800 }, snapshot)
    expect(g.degraded.overlayRight).toBe(false)
    expect(g.degraded.collapsedRight).toBe(true)
  })

  it('never overlays an app-mode (non-feature) right column', () => {
    const snapshot = buildPreset('code', { nextInstanceId: nextId() })
    snapshot.columns.right.width = 1200
    const g = computeGeometry({ width: 1440, height: 920 }, snapshot)
    expect(g.degraded.overlayRight).toBe(false)
  })
})

describe('maxOverlayColumnWidth', () => {
  it('home@1440 leaves a 300px chat strip: 1424 − 260 − 8 − 300 = 856', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    expect(maxOverlayColumnWidth({ width: 1440, height: 920 }, snapshot)).toBe(856)
  })

  it('home@1120 leaves a 300px strip: 1104 − 260 − 8 − 300 = 536', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    expect(maxOverlayColumnWidth({ width: 1120, height: 920 }, snapshot)).toBe(536)
  })

  it('caps at MAX_DOCK_COLUMN_WIDTH on very wide windows', () => {
    const snapshot = buildPreset('home', { nextInstanceId: nextId() })
    expect(maxOverlayColumnWidth({ width: 2560, height: 920 }, snapshot)).toBe(1040)
  })
})
