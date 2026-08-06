import { describe, it, expect, beforeEach } from 'vitest'
import { reduce, findInstanceLocation } from './reducer'
import type { ReduceContext } from './reducer'
import { createEmptySnapshot } from './reducer'
import type { WorkbenchLayoutSnapshot } from './types'
import { makeRegistry, makeContext, makeCard } from './__testUtils'
import { createCardRegistry } from './registry'
import type { WorkbenchCommand } from './commands'

describe('workbench reducer', () => {
  let ctx: ReduceContext
  let reg: ReturnType<typeof makeRegistry>

  beforeEach(() => {
    reg = makeRegistry()
    ctx = makeContext(reg)
  })

  function dispatch(snapshot: WorkbenchLayoutSnapshot, cmd: WorkbenchCommand, rev = snapshot.revision) {
    return reduce(snapshot, { command: cmd, source: 'user', expectedRevision: rev }, ctx)
  }

  it('rejects ai source', () => {
    const snap = createEmptySnapshot('home')
    const out = reduce(snap, { command: { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git' }, source: 'ai', expectedRevision: 1 }, ctx)
    expect(out.ok).toBe(false)
    if (!out.ok) expect(out.error.code).toBe('source_rejected')
  })

  it('rejects internal commands from external dispatch', () => {
    const snap = createEmptySnapshot('home')
    const out = reduce(snap, { command: { type: '__restoreSnapshot', scope: { kind: 'page', pageId: 'home' }, snapshot: snap }, source: 'user', expectedRevision: 1 }, ctx)
    expect(out.ok).toBe(false)
    if (!out.ok) expect(out.error.code).toBe('internal_command')
  })

  it('rejects on revision mismatch', () => {
    const snap = createEmptySnapshot('home')
    const out = reduce(snap, { command: { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git' }, source: 'user', expectedRevision: 99 }, ctx)
    expect(out.ok).toBe(false)
    if (!out.ok) expect(out.error.code).toBe('revision_mismatch')
  })

  it('openCard adds a card and returns closeCard inverse', () => {
    const snap = createEmptySnapshot('home')
    const out = dispatch(snap, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git' })
    expect(out.ok).toBe(true)
    if (!out.ok) return
    const { next, inverse } = out.result
    expect(next.revision).toBe(2)
    const opened = Object.values(next.cards).find((c) => c.descriptorId === 'git')
    expect(opened).toBeTruthy()
    expect(inverse.type).toBe('closeCard')
    if (inverse.type === 'closeCard') expect(inverse.instanceId).toBe(opened?.id)
  })

  it('singleton openCard reuses existing instance (does not duplicate)', () => {
    const snap = createEmptySnapshot('home')
    const r1 = dispatch(snap, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'chat', slotId: 'right' })
    if (!r1.ok) throw new Error('first open failed')
    const afterFirst = r1.result.next
    const chatCount1 = Object.values(afterFirst.cards).filter((c) => c.descriptorId === 'chat').length
    expect(chatCount1).toBe(1)

    const r2 = dispatch(afterFirst, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'chat', slotId: 'right' }, afterFirst.revision)
    expect(r2.ok).toBe(true)
    if (!r2.ok) return
    const chatCount2 = Object.values(r2.result.next.cards).filter((c) => c.descriptorId === 'chat').length
    expect(chatCount2).toBe(1)
  })

  it('closeCard removes a card and its inverse reopens it at the same position', () => {
    const snap = createEmptySnapshot('home')
    const r1 = dispatch(snap, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git' })
    if (!r1.ok) throw new Error('open failed')
    const afterOpen = r1.result.next
    const openedId = Object.values(afterOpen.cards).find((c) => c.descriptorId === 'git')!.id
    const loc = findInstanceLocation(afterOpen, openedId)!

    const r2 = dispatch(afterOpen, { type: 'closeCard', scope: { kind: 'page', pageId: 'home' }, instanceId: openedId }, afterOpen.revision)
    if (!r2.ok) throw new Error('close failed')
    const afterClose = r2.result.next
    expect(afterClose.cards[openedId]).toBeUndefined()

    // Replay inverse (openCard) against the closed snapshot.
    const inverse = r2.result.inverse as Extract<WorkbenchCommand, { type: 'openCard' }>
    const r3 = dispatch(afterClose, inverse, afterClose.revision)
    if (!r3.ok) throw new Error('inverse open failed')
    const restored = r3.result.next
    expect(restored.cards[openedId]).toBeTruthy()
    const restoredLoc = findInstanceLocation(restored, openedId)
    expect(restoredLoc?.slotId).toBe(loc.slotId)
    expect(restoredLoc?.stackId).toBe(loc.stackId)
    expect(restoredLoc?.index).toBe(loc.index)
  })

  it('moveCard moves between slots and its inverse restores origin', () => {
    const snap = createEmptySnapshot('home')
    const r1 = dispatch(snap, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git', slotId: 'right' })
    if (!r1.ok) throw new Error('open failed')
    const afterOpen = r1.result.next
    const openedId = Object.values(afterOpen.cards).find((c) => c.descriptorId === 'git')!.id

    const r2 = dispatch(afterOpen, { type: 'moveCard', scope: { kind: 'page', pageId: 'home' }, instanceId: openedId, toSlotId: 'left' }, afterOpen.revision)
    if (!r2.ok) throw new Error('move failed')
    const afterMove = r2.result.next
    expect(findInstanceLocation(afterMove, openedId)?.slotId).toBe('left')

    const inverse = r2.result.inverse as Extract<WorkbenchCommand, { type: 'moveCard' }>
    const r3 = dispatch(afterMove, inverse, afterMove.revision)
    if (!r3.ok) throw new Error('inverse move failed')
    expect(findInstanceLocation(r3.result.next, openedId)?.slotId).toBe('right')
  })

  it('swapColumns swaps column contents and is self-inverse', () => {
    const snap = createEmptySnapshot('home')
    // Open one card in each of left/right to make columns distinguishable.
    dispatch(snap, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git', slotId: 'left' })
    // mutate via dispatch chain to get a valid snapshot
    let cur = snap
    const open = (cmd: Extract<WorkbenchCommand, { type: 'openCard' }>) => {
      const o = dispatch(cur, cmd, cur.revision)
      if (!o.ok) throw new Error('open failed')
      cur = o.result.next
    }
    open({ type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git', slotId: 'left' })
    open({ type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'browser', slotId: 'right' })

    const leftIdsBefore = cur.columns.left.primary.tabIds.slice()
    const rightIdsBefore = cur.columns.right.primary.tabIds.slice()
    expect(leftIdsBefore.length).toBeGreaterThan(0)
    expect(rightIdsBefore.length).toBeGreaterThan(0)

    const sw = dispatch(cur, { type: 'swapColumns', scope: { kind: 'page', pageId: 'home' }, a: 'left', b: 'right' }, cur.revision)
    if (!sw.ok) throw new Error('swap failed')
    const afterSwap = sw.result.next
    expect(afterSwap.columns.left.primary.tabIds).toEqual(rightIdsBefore)
    expect(afterSwap.columns.right.primary.tabIds).toEqual(leftIdsBefore)

    const inv = sw.result.inverse as Extract<WorkbenchCommand, { type: 'swapColumns' }>
    const back = dispatch(afterSwap, inv, afterSwap.revision)
    if (!back.ok) throw new Error('swap-back failed')
    expect(back.result.next.columns.left.primary.tabIds).toEqual(leftIdsBefore)
    expect(back.result.next.columns.right.primary.tabIds).toEqual(rightIdsBefore)
  })

  it('splitStack creates a secondary stack and mergeStack restores', () => {
    const snap = createEmptySnapshot('home')
    const r1 = dispatch(snap, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'git', slotId: 'right' })
    if (!r1.ok) throw new Error('open failed')
    const r2 = dispatch(r1.result.next, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'approval', slotId: 'right' }, r1.result.next.revision)
    if (!r2.ok) throw new Error('open2 failed')
    const afterOpen = r2.result.next

    const sp = dispatch(afterOpen, { type: 'splitStack', scope: { kind: 'page', pageId: 'home' }, slotId: 'right', ratio: 0.65 }, afterOpen.revision)
    if (!sp.ok) throw new Error('split failed')
    const afterSplit = sp.result.next
    expect(afterSplit.columns.right.secondary).toBeTruthy()
    expect(afterSplit.columns.right.splitRatio).toBe(0.65)
    expect(afterSplit.columns.right.secondary!.tabIds.length).toBeGreaterThan(0)

    const inv = sp.result.inverse as Extract<WorkbenchCommand, { type: 'mergeStack' }>
    const mg = dispatch(afterSplit, inv, afterSplit.revision)
    if (!mg.ok) throw new Error('merge failed')
    expect(mg.result.next.columns.right.secondary).toBeNull()
  })

  it('settings cards reject move/close/split (locked + non-movable)', () => {
    // Build a settings-like snapshot with locked slots.
    const snap = createEmptySnapshot('settings')
    const reg2 = createCardRegistry()
    reg2.register(makeCard('settingsNav', { singleton: true, movable: false, closable: false, detachable: false }))
    reg2.register(makeCard('settingsContent', { singleton: true, movable: false, closable: false, detachable: false }))
    const lockedCtx: ReduceContext = { ...ctx, registry: reg2, lockedSlots: new Set(['left', 'center']) }

    const openNav = reduce(snap, { command: { type: 'openCard', scope: { kind: 'page', pageId: 'settings' }, descriptorId: 'settingsNav', slotId: 'left' }, source: 'user', expectedRevision: 1 }, lockedCtx)
    expect(openNav.ok).toBe(false) // cannot open non-movable into locked column
  })

  it('rejects unknown descriptor on openCard', () => {
    const snap = createEmptySnapshot('home')
    const out = dispatch(snap, { type: 'openCard', scope: { kind: 'page', pageId: 'home' }, descriptorId: 'nope' })
    expect(out.ok).toBe(false)
    if (!out.ok) expect(out.error.code).toBe('rejected')
  })
})
