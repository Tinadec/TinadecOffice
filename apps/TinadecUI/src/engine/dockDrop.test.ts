import { describe, it, expect } from 'vitest'
import { resolveDockDrop, dropZoneToSplit } from './dockDrop'
import type { DockPaneHitRect } from './dockDrop'

const PANES: DockPaneHitRect[] = [
  { paneId: 'main', x: 0, y: 0, width: 400, height: 600 },
  { paneId: 'side', x: 400, y: 0, width: 200, height: 600 },
]

describe('resolveDockDrop', () => {
  it('resolves left edge to a left split', () => {
    expect(resolveDockDrop({ x: 5, y: 300 }, PANES)).toEqual({ paneId: 'main', zone: 'left' })
  })

  it('resolves right edge to a right split', () => {
    expect(resolveDockDrop({ x: 390, y: 300 }, PANES)).toEqual({ paneId: 'main', zone: 'right' })
  })

  it('resolves top edge to a top split', () => {
    expect(resolveDockDrop({ x: 200, y: 5 }, PANES)).toEqual({ paneId: 'main', zone: 'top' })
  })

  it('resolves bottom edge to a bottom split', () => {
    expect(resolveDockDrop({ x: 200, y: 590 }, PANES)).toEqual({ paneId: 'main', zone: 'bottom' })
  })

  it('resolves the interior to center (merge)', () => {
    expect(resolveDockDrop({ x: 200, y: 300 }, PANES)).toEqual({ paneId: 'main', zone: 'center' })
  })

  it('resolves the topmost pane by rendering order', () => {
    // Overlapping panes: the later one wins.
    const overlapping = [
      { paneId: 'a', x: 0, y: 0, width: 300, height: 300 },
      { paneId: 'b', x: 100, y: 100, width: 200, height: 200 },
    ]
    expect(resolveDockDrop({ x: 150, y: 150 }, overlapping)).toEqual({ paneId: 'b', zone: 'center' })
  })

  it('returns null outside every pane', () => {
    expect(resolveDockDrop({ x: 700, y: 700 }, PANES)).toBeNull()
    expect(resolveDockDrop({ x: -1, y: 300 }, PANES)).toBeNull()
  })

  it('skips zero-size panes', () => {
    const withEmpty = [...PANES, { paneId: 'ghost', x: 0, y: 0, width: 0, height: 0 }]
    expect(resolveDockDrop({ x: 200, y: 300 }, withEmpty)).toEqual({ paneId: 'main', zone: 'center' })
  })

  it('honors a custom edge threshold', () => {
    // 50% threshold → interior narrows to the exact center line.
    expect(resolveDockDrop({ x: 100, y: 300 }, PANES, 0.5)).toEqual({ paneId: 'main', zone: 'left' })
    expect(resolveDockDrop({ x: 200, y: 300 }, PANES, 0.5)).toEqual({ paneId: 'main', zone: 'center' })
  })
})

describe('dropZoneToSplit', () => {
  it('maps zones to dir+place', () => {
    expect(dropZoneToSplit('left')).toEqual({ dir: 'row', place: 'start' })
    expect(dropZoneToSplit('right')).toEqual({ dir: 'row', place: 'end' })
    expect(dropZoneToSplit('top')).toEqual({ dir: 'column', place: 'start' })
    expect(dropZoneToSplit('bottom')).toEqual({ dir: 'column', place: 'end' })
    // Center is used only for merge; the split mapping is a UI convenience.
    expect(dropZoneToSplit('center').dir).toBe('column')
  })
})
