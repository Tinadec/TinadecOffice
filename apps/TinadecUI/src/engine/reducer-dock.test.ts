import { describe, it, expect, beforeEach } from 'vitest'
import { reduce } from './reducer'
import type { ReduceContext, InstanceLocation } from './reducer'
import { findInstanceLocation, collectDockPanes, collectDockTabIds } from './reducer'
import type { UieLayoutSnapshot } from './types'
import { makeRegistry, makeContext, makeEmptySnapshot, makeCard } from './__testUtils'
import { createCardRegistry, type CardRegistry } from './registry'
import type { UieCommand } from './commands'

describe('uie dock reducer', () => {
  let reg: CardRegistry
  let ctx: ReduceContext
  let dockCounter = 0

  beforeEach(() => {
    reg = makeRegistry()
    dockCounter = 0
    ctx = {
      ...makeContext(reg),
      nextDockId: () => `dp-${++dockCounter}`,
      nextDockSplitId: () => `ds-${++dockCounter}`,
    }
  })

  function dispatch(snapshot: UieLayoutSnapshot, cmd: UieCommand, rev = snapshot.revision) {
    return reduce(snapshot, { command: cmd, source: 'user', expectedRevision: rev }, ctx)
  }

  /** Home-shaped snapshot: right column hosts [homePicker, git, approval]. */
  function homeWithRightPanel(): UieLayoutSnapshot {
    const snap = makeEmptySnapshot('home')
    const hp = { id: 'hp', descriptorId: 'homePicker', title: 'Home' }
    const git = { id: 'git', descriptorId: 'git', title: 'Git' }
    const appr = { id: 'appr', descriptorId: 'approval', title: '审批' }
    snap.cards = { hp: hp, git: git, appr: appr }
    snap.columns.right = {
      ...snap.columns.right,
      width: 420,
      primary: { stackId: 'primary', tabIds: ['hp', 'git', 'appr'], activeTabId: 'git' },
    }
    return snap
  }

  function rightDock(snap: UieLayoutSnapshot) {
    return snap.columns.right.dock!
  }

  function paneIds(snap: UieLayoutSnapshot): string[] {
    return collectDockPanes(rightDock(snap)).map((p) => p.paneId)
  }

  it('first splitDockPane folds stacks into a main pane and creates a dock', () => {
    const snap = homeWithRightPanel()
    const out = dispatch(snap, {
      type: 'splitDockPane',
      scope: { kind: 'page', pageId: 'home' },
      slotId: 'right',
      paneId: 'whatever', // no dock yet — target is the folded main pane
      dir: 'column',
      place: 'end',
      instanceId: 'git',
    })
    expect(out.ok).toBe(true)
    if (!out.ok) return
    const { next } = out.result
    const dock = rightDock(next)
    const panes = collectDockPanes(dock)
    expect(panes.length).toBe(2)
    const main = panes.find((p) => p.main)!
    const newPane = panes.find((p) => !p.main)!
    expect(main.tabIds).toEqual(['hp', 'appr']) // git removed into the new pane
    expect(newPane.tabIds).toEqual(['git'])
    expect(dock.kind).toBe('split')
    if (dock.kind === 'split') {
      expect(dock.dir).toBe('column')
      // place 'end' → main above (a), new pane below (b)
      expect(dock.b).toBe(newPane)
      expect(dock.a).toBe(main)
    }
    // Stacks are cleared (mutual exclusion).
    expect(next.columns.right.primary.tabIds).toEqual([])
    expect(next.columns.right.secondary).toBeNull()
    // Column width unchanged for a column split.
    expect(next.columns.right.width).toBe(420)
    // Inverse is best-effort mergeDockPane on the new pane.
    expect(out.result.inverse.type).toBe('mergeDockPane')
  })

  it('first row split widens the column toward the initial split width', () => {
    const snap = homeWithRightPanel()
    const out = dispatch(snap, {
      type: 'splitDockPane',
      scope: { kind: 'page', pageId: 'home' },
      slotId: 'right',
      paneId: 'main',
      dir: 'row',
      place: 'start',
      instanceId: 'git',
    })
    expect(out.ok).toBe(true)
    if (!out.ok) return
    expect(out.result.next.columns.right.width).toBe(480)
  })

  it('nested mixed splits build a correct binary tree', () => {
    const snap = homeWithRightPanel()
    // 1) git down → column split (main above, git below).
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const mainId = paneIds(cur).find((id) => collectDockPanes(rightDock(cur)).find((p) => p.paneId === id)?.main)!

    // 2) approval left of main → row split around main.
    const r2 = dispatch(cur, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: mainId, dir: 'row', place: 'start', instanceId: 'appr',
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    cur = r2.result.next

    const dock = rightDock(cur)
    expect(dock.kind).toBe('split')
    if (dock.kind !== 'split') return
    // root = column split: a = row split(approval | main), b = git pane
    expect(dock.dir).toBe('column')
    const a = dock.a
    expect(a.kind).toBe('split')
    if (a.kind === 'split') {
      expect(a.dir).toBe('row')
      expect(collectDockTabIds(a.a)).toEqual(['appr'])
      expect(collectDockTabIds(a.b)).toEqual(['hp'])
    }
    expect(collectDockTabIds(dock.b)).toEqual(['git'])
    expect(paneIds(cur).length).toBe(3)
  })

  it('rejects splitting a single-card pane into itself', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const newPaneId = paneIds(cur).find((id) => collectDockPanes(rightDock(cur)).find((p) => p.paneId === id)?.tabIds.includes('git'))!

    // Try to split the git pane into itself — single card, must be rejected.
    const r2 = dispatch(cur, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: newPaneId, dir: 'column', place: 'end', instanceId: 'git',
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    expect(r2.result.changed).toBe(false)
  })

  it('rejects dragging homePicker (movable:false)', () => {
    // Real registry marks homePicker movable:false — build one locally.
    const reg2 = makeRegistry()
    const descs = reg2.list().filter((d) => d.type !== 'homePicker')
    // Rebuild a registry with a non-movable homePicker.
    const reg3 = createCardRegistry()
    for (const d of descs) reg3.register(d)
    reg3.register(makeCard('homePicker', { singleton: true, movable: false, closable: false }))
    const ctx2: ReduceContext = {
      ...makeContext(reg3),
      nextDockId: () => `dp-${++dockCounter}`,
      nextDockSplitId: () => `ds-${++dockCounter}`,
    }
    const snap = homeWithRightPanel()
    const out = reduce(snap, {
      command: {
        type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
        slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'hp',
      },
      source: 'user',
      expectedRevision: snap.revision,
    }, ctx2)
    expect(out.ok).toBe(false)
  })

  it('removes an empty non-main pane when its last card moves away', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const mainId = paneIds(cur).find((id) => collectDockPanes(rightDock(cur)).find((p) => p.paneId === id)?.main)!

    // Move git back into the main pane (center merge) — its pane empties &
    // is removed, collapsing the dock back to a single stack.
    const r2 = dispatch(cur, {
      type: 'moveCardToDockPane', scope: { kind: 'page', pageId: 'home' },
      instanceId: 'git', toPaneId: mainId,
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    const next = r2.result.next
    // Dock normalized back to a stack hosting every card; the dropped card activates.
    expect(next.columns.right.dock).toBeNull()
    expect(next.columns.right.primary.tabIds).toEqual(['hp', 'appr', 'git'])
    expect(next.columns.right.primary.activeTabId).toBe('git')
  })

  it('mergeDockPane merges a pane back into main and normalizes to a stack', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const newPaneId = paneIds(cur).find((id) => collectDockPanes(rightDock(cur)).find((p) => p.paneId === id)?.tabIds.includes('git'))!

    const r2 = dispatch(cur, {
      type: 'mergeDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: newPaneId,
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    const next = r2.result.next
    // Only main remains → normalized back to a stack.
    expect(next.columns.right.dock).toBeNull()
    expect(next.columns.right.primary.tabIds).toEqual(['hp', 'appr', 'git'])
    expect(next.columns.right.primary.activeTabId).toBe('hp') // main active unchanged
  })

  it('rejects merging the main pane itself', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const mainId = paneIds(cur).find((id) => collectDockPanes(rightDock(cur)).find((p) => p.paneId === id)?.main)!
    const r2 = dispatch(cur, {
      type: 'mergeDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: mainId,
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    expect(r2.result.changed).toBe(false)
  })

  it('mergeDockColumn restores a single stack atomically', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const mainId = paneIds(cur).find((id) => collectDockPanes(rightDock(cur)).find((p) => p.paneId === id)?.main)!
    const r2 = dispatch(cur, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: mainId, dir: 'row', place: 'end', instanceId: 'appr',
    }, cur.revision)
    if (!r2.ok) throw new Error('second split failed')
    cur = r2.result.next
    expect(paneIds(cur).length).toBe(3)

    const r3 = dispatch(cur, {
      type: 'mergeDockColumn', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right',
    }, cur.revision)
    expect(r3.ok).toBe(true)
    if (!r3.ok) return
    const next = r3.result.next
    expect(next.columns.right.dock).toBeNull()
    expect(next.columns.right.primary.tabIds).toEqual(['hp', 'appr', 'git'])
  })

  it('resizeDockSplit clamps the ratio', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
      splitId: 'ds0', paneId2: 'dp-new',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    // Root split got the explicit id ds0.
    const r2 = dispatch(cur, {
      type: 'resizeDockSplit', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', splitId: 'ds0', ratio: 1.5,
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    const dock = rightDock(r2.result.next)
    expect(dock.kind === 'split' && dock.ratio).toBe(0.9)
  })

  it('openCard lands in the main pane of a dock column', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const mainId = paneIds(cur).find((id) => collectDockPanes(rightDock(cur)).find((p) => p.paneId === id)?.main)!

    const r2 = dispatch(cur, {
      type: 'openCard', scope: { kind: 'page', pageId: 'home' },
      descriptorId: 'terminal', slotId: 'right',
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    const next = r2.result.next
    const term = Object.values(next.cards).find((c) => c.descriptorId === 'terminal')!
    const loc = findInstanceLocation(next, term.id) as InstanceLocation
    expect(loc.paneId).toBe(mainId)
    // Explicit paneId targets that pane.
    const r3 = dispatch(next, {
      type: 'openCard', scope: { kind: 'page', pageId: 'home' },
      descriptorId: 'browser', slotId: 'right', paneId: collectDockPanes(rightDock(next)).find((p) => p.tabIds.includes('git'))!.paneId,
    }, next.revision)
    if (!r3.ok) throw new Error('explicit pane open failed')
    const brow = Object.values(r3.result.next.cards).find((c) => c.descriptorId === 'browser')!
    const loc2 = findInstanceLocation(r3.result.next, brow.id)!
    expect(loc2.paneId).not.toBe(mainId)
  })

  it('closeCard works inside a dock pane and normalizes an emptied pane', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next

    const r2 = dispatch(cur, {
      type: 'closeCard', scope: { kind: 'page', pageId: 'home' },
      instanceId: 'git',
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    const next = r2.result.next
    expect(next.cards['git']).toBeUndefined()
    // Git's pane was its only card → removed → only main remains → stack.
    expect(next.columns.right.dock).toBeNull()
    expect(next.columns.right.primary.tabIds).toEqual(['hp', 'appr'])
  })

  it('activateCard activates within the owning pane', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const mainId = paneIds(cur).find((id) => collectDockPanes(rightDock(cur)).find((p) => p.paneId === id)?.main)!

    const r2 = dispatch(cur, {
      type: 'activateCard', scope: { kind: 'page', pageId: 'home' },
      instanceId: 'appr',
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    const main = collectDockPanes(rightDock(r2.result.next)).find((p) => p.paneId === mainId)!
    expect(main.activeTabId).toBe('appr')
    // The git pane's activeTabId is untouched.
    const gitPane = collectDockPanes(rightDock(r2.result.next)).find((p) => p.tabIds.includes('git'))!
    expect(gitPane.activeTabId).toBe('git')
  })

  it('never clears the main pane via closeCard (defensive guard)', () => {
    const snap = makeEmptySnapshot('home')
    const hp = { id: 'hp', descriptorId: 'homePicker', title: 'Home' }
    snap.cards = { hp }
    snap.columns.right = {
      ...snap.columns.right,
      primary: { stackId: 'primary', tabIds: [], activeTabId: null },
      dock: {
        kind: 'pane',
        paneId: 'main',
        main: true,
        tabIds: ['hp'],
        activeTabId: 'hp',
      },
    }
    // Direct dock close of the only main card is guarded.
    const out = dispatch(snap, {
      type: 'closeCard', scope: { kind: 'page', pageId: 'home' },
      instanceId: 'hp',
    })
    expect(out.ok).toBe(true)
    if (!out.ok) return
    expect(out.result.changed).toBe(false)
  })

  it('splitStack/mergeStack/resizeSplit are rejected on a dock column', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'column', place: 'end', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next

    for (const cmd of [
      { type: 'splitStack', scope: { kind: 'page', pageId: 'home' }, slotId: 'right' },
      { type: 'mergeStack', scope: { kind: 'page', pageId: 'home' }, slotId: 'right' },
      { type: 'resizeSplit', scope: { kind: 'page', pageId: 'home' }, slotId: 'right', ratio: 0.5 },
    ] as const) {
      const r = dispatch(cur, cmd as UieCommand, cur.revision)
      expect(r.ok).toBe(true)
      if (!r.ok) return
      expect(r.result.changed).toBe(false)
    }
  })

  it('resizeColumn clamps to the dock ceiling', () => {
    const snap = homeWithRightPanel()
    const r1 = dispatch(snap, {
      type: 'splitDockPane', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', paneId: 'x', dir: 'row', place: 'start', instanceId: 'git',
    })
    if (!r1.ok) throw new Error('first split failed')
    let cur = r1.result.next
    const r2 = dispatch(cur, {
      type: 'resizeColumn', scope: { kind: 'page', pageId: 'home' },
      slotId: 'right', width: 5000,
    }, cur.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    expect(r2.result.next.columns.right.width).toBe(1040)
  })
})
