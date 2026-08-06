import { describe, expect, it } from 'vitest'
import { createWorkbenchPreset } from './presets'
import { reduceWorkbenchLayout } from './reducer'

describe('reduceWorkbenchLayout', () => {
  it('moves a card into a secondary stack without losing its instance', () => {
    const initial = createWorkbenchPreset('home')
    const result = reduceWorkbenchLayout(initial, {
      source: 'user',
      expectedRevision: 0,
      command: {
        kind: 'moveCard',
        cardId: 'home:tools',
        slotId: 'center',
        stackId: 'secondary',
      },
    })

    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.state.cards['home:tools']).toEqual(initial.cards['home:tools'])
    expect(result.state.columns.right.primary.cardIds).toEqual([])
    expect(result.state.columns.center.secondary?.cardIds).toEqual(['home:tools'])
    expect(result.state.revision).toBe(1)
  })

  it('swaps logical columns and can restore the previous snapshot', () => {
    const initial = createWorkbenchPreset('home')
    const swapped = reduceWorkbenchLayout(initial, {
      source: 'user',
      expectedRevision: 0,
      command: { kind: 'swapColumns', first: 'left', second: 'right' },
    })
    expect(swapped.ok).toBe(true)
    if (!swapped.ok) return
    expect(swapped.state.columnOrder).toEqual(['right', 'center', 'left'])

    const restored = reduceWorkbenchLayout(swapped.state, {
      source: 'user',
      expectedRevision: 1,
      command: swapped.inverse,
    })
    expect(restored.ok).toBe(true)
    if (!restored.ok) return
    expect(restored.state.columnOrder).toEqual(initial.columnOrder)
    expect(restored.state.cards).toEqual(initial.cards)
  })

  it('rejects stale and AI-originated commands', () => {
    const initial = createWorkbenchPreset('home')
    expect(reduceWorkbenchLayout(initial, {
      source: 'user',
      expectedRevision: 2,
      command: { kind: 'collapseColumn', slotId: 'right', collapsed: true },
    })).toMatchObject({ ok: false, error: 'Layout revision is stale.' })
    expect(reduceWorkbenchLayout(initial, {
      source: 'ai',
      expectedRevision: 0,
      command: { kind: 'collapseColumn', slotId: 'right', collapsed: true },
    })).toMatchObject({ ok: false, error: 'AI layout commands are not enabled.' })
  })

  it('keeps settings fixed while allowing its navigation width to change', () => {
    const initial = createWorkbenchPreset('settings')
    const move = reduceWorkbenchLayout(initial, {
      source: 'user',
      expectedRevision: 0,
      command: {
        kind: 'moveCard',
        cardId: 'settings:content',
        slotId: 'right',
        stackId: 'primary',
      },
    })
    expect(move).toMatchObject({ ok: false, error: 'This workbench preset has a fixed layout.' })

    const resize = reduceWorkbenchLayout(initial, {
      source: 'user',
      expectedRevision: 0,
      command: { kind: 'resizeColumn', slotId: 'left', width: 312 },
    })
    expect(resize.ok).toBe(true)
    if (resize.ok) expect(resize.state.columns.left.width).toBe(312)
  })

  it('destroys card state only when the card is explicitly closed', () => {
    const initial = createWorkbenchPreset('home')
    initial.cards['home:tools'].state = { url: 'https://example.com' }
    const result = reduceWorkbenchLayout(initial, {
      source: 'user',
      expectedRevision: 0,
      command: { kind: 'closeCard', cardId: 'home:tools' },
    })
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.state.cards['home:tools']).toBeUndefined()
    expect(result.state.columns.right.primary.cardIds).toEqual([])
  })

  it('moves the browser below chat at the default 65/35 split without changing card state', () => {
    const initial = createWorkbenchPreset('home')
    initial.cards['home:chat'].state = { draft: 'unfinished message', scrollTop: 480 }
    const opened = reduceWorkbenchLayout(initial, {
      source: 'user',
      expectedRevision: 0,
      command: {
        kind: 'openCard',
        card: { id: 'home:browser:1', type: 'tool.browser', state: { url: 'https://example.com' } },
        slotId: 'right',
        stackId: 'primary',
      },
    })
    expect(opened.ok).toBe(true)
    if (!opened.ok) return

    const moved = reduceWorkbenchLayout(opened.state, {
      source: 'user',
      expectedRevision: 1,
      command: {
        kind: 'moveCard',
        cardId: 'home:browser:1',
        slotId: 'center',
        stackId: 'secondary',
      },
    })
    expect(moved.ok).toBe(true)
    if (!moved.ok) return
    expect(moved.state.columns.center.splitRatio).toBe(0.65)
    expect(moved.state.columns.center.primary.cardIds).toEqual(['home:chat'])
    expect(moved.state.columns.center.secondary?.cardIds).toEqual(['home:browser:1'])
    expect(moved.state.cards['home:chat'].state).toEqual({ draft: 'unfinished message', scrollTop: 480 })
    expect(moved.state.cards['home:browser:1'].state).toEqual({ url: 'https://example.com' })
  })

  it('reorders tabs and moves a whole stack through the same command surface', () => {
    const initial = createWorkbenchPreset('home')
    const opened = reduceWorkbenchLayout(initial, {
      source: 'user',
      expectedRevision: 0,
      command: {
        kind: 'openCard',
        card: { id: 'home:git', type: 'home.git', state: {} },
        slotId: 'right',
        stackId: 'primary',
      },
    })
    expect(opened.ok).toBe(true)
    if (!opened.ok) return
    const reordered = reduceWorkbenchLayout(opened.state, {
      source: 'user',
      expectedRevision: 1,
      command: { kind: 'moveCard', cardId: 'home:git', slotId: 'right', stackId: 'primary', index: 0 },
    })
    expect(reordered.ok).toBe(true)
    if (!reordered.ok) return
    expect(reordered.state.columns.right.primary.cardIds).toEqual(['home:git', 'home:tools'])

    const movedStack = reduceWorkbenchLayout(reordered.state, {
      source: 'user',
      expectedRevision: 2,
      command: {
        kind: 'moveStack',
        fromSlotId: 'right',
        fromStackId: 'primary',
        toSlotId: 'left',
        toStackId: 'primary',
      },
    })
    expect(movedStack.ok).toBe(true)
    if (!movedStack.ok) return
    expect(movedStack.state.columns.left.primary.cardIds).toEqual(['home:git', 'home:tools'])
    expect(movedStack.state.columns.right.primary.cardIds).toEqual(['home:navigation'])
  })
})
