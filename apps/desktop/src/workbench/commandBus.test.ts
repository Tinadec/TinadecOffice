import { describe, it, expect, beforeEach } from 'vitest'
import { createCommandBus } from './commandBus'
import { buildPreset } from './presets'
import { makeRegistry } from './__testUtils'
import type { CardRegistry } from './registry'
import type { WorkbenchCommand } from './commands'

describe('command bus', () => {
  let reg: CardRegistry
  beforeEach(() => {
    reg = makeRegistry()
  })

  function makeBus(initial?: ReturnType<typeof buildPreset>) {
    const preset = initial ?? buildPreset('home', { nextInstanceId: () => `bus-${Math.random()}` })
    let changed = 0
    const bus = createCommandBus(preset, { registry: reg, onChanged: () => { changed++ } })
    return { bus, preset, getChanged: () => changed }
  }

  const homeScope = { kind: 'page', pageId: 'home' } as const

  function openBrowser(snapshotRev: number): WorkbenchCommand {
    // browser is a non-singleton card (can actually open a new instance).
    return { type: 'openCard', scope: homeScope, descriptorId: 'browser', slotId: 'right' }
  }

  it('rejects ai source via the bus', () => {
    const { bus } = makeBus()
    const ok = bus.dispatch({ command: openBrowser(bus.getSnapshot().revision), source: 'ai', expectedRevision: bus.getSnapshot().revision })
    expect(ok).toBe(false)
    expect(bus.canUndo()).toBe(false)
  })

  it('rejects on revision mismatch via the bus', () => {
    const { bus } = makeBus()
    const ok = bus.dispatch({ command: openBrowser(999), source: 'user', expectedRevision: 999 })
    expect(ok).toBe(false)
  })

  it('applies a valid command and records undo', () => {
    const { bus, getChanged } = makeBus()
    const ok = bus.dispatch({ command: openBrowser(bus.getSnapshot().revision), source: 'user', expectedRevision: bus.getSnapshot().revision })
    expect(ok).toBe(true)
    expect(bus.canUndo()).toBe(true)
    expect(getChanged()).toBe(1)
    const browsers = Object.values(bus.getSnapshot().cards).filter((c) => c.descriptorId === 'browser')
    // preset has 1 browser; opened a second.
    expect(browsers.length).toBe(2)
  })

  it('undo restores the previous snapshot and redo re-applies', () => {
    const { bus } = makeBus()
    const rev0 = bus.getSnapshot().revision
    bus.dispatch({ command: openBrowser(rev0), source: 'user', expectedRevision: rev0 })
    const afterOpen = bus.getSnapshot()
    const browserCount = Object.values(afterOpen.cards).filter((c) => c.descriptorId === 'browser').length
    expect(browserCount).toBeGreaterThan(1)

    const undone = bus.undo()
    expect(undone).toBeTruthy()
    expect(undone!.revision).toBe(rev0)
    expect(Object.values(undone!.cards).filter((c) => c.descriptorId === 'browser').length).toBe(1)
    expect(bus.canRedo()).toBe(true)

    const redone = bus.redo()
    expect(redone).toBeTruthy()
    expect(redone!.revision).toBe(afterOpen.revision)
    expect(Object.values(redone!.cards).filter((c) => c.descriptorId === 'browser').length).toBe(2)
  })

  it('coalesces drag resizes sharing a gestureId into one undo record', () => {
    const { bus } = makeBus()
    const initialRightWidth = bus.getSnapshot().columns.right.width
    const dispatchResize = (w: number) =>
      bus.dispatch(
        { command: { type: 'resizeColumn', scope: homeScope, slotId: 'right', width: w }, source: 'user', expectedRevision: bus.getSnapshot().revision },
        { gestureId: 'drag-1' },
      )
    dispatchResize(300)
    dispatchResize(350)
    dispatchResize(400)
    expect(bus.canUndo()).toBe(true)
    // One undo should snap back to the pre-drag width (420), not 400.
    const undone = bus.undo()
    expect(undone).toBeTruthy()
    expect(undone!.columns.right.width).toBe(initialRightWidth)
  })

  it('resetScope produces a snapshot (no-op change but dispatchable)', () => {
    const { bus } = makeBus()
    const ok = bus.dispatch({ command: { type: 'resetScope', scope: homeScope }, source: 'user', expectedRevision: bus.getSnapshot().revision })
    expect(ok).toBe(true)
  })

  it('setSnapshot replaces the snapshot and clears redo', () => {
    const { bus } = makeBus()
    bus.dispatch({ command: openBrowser(bus.getSnapshot().revision), source: 'user', expectedRevision: bus.getSnapshot().revision })
    bus.undo()
    expect(bus.canRedo()).toBe(true)
    const fresh = buildPreset('home', { nextInstanceId: () => 'x' })
    bus.setSnapshot(fresh)
    expect(bus.canRedo()).toBe(false)
    expect(bus.getSnapshot().revision).toBe(fresh.revision)
  })
})
