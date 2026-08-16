import { describe, it, expect } from 'vitest'
import { repairLayout, type RepairContext } from './repair'
import { makeRegistry } from './__testUtils'
import { buildPreset } from './presets'
import { createEmptySnapshot, collectDockPanes } from './reducer'

function makeCtx(): RepairContext {
  return { registry: makeRegistry(), preset: { nextInstanceId: () => `repair-${Math.random()}` } }
}

describe('repairLayout', () => {
  it('falls back to built-in preset for null/non-object input', () => {
    const ctx = makeCtx()
    const out = repairLayout(null, ctx)
    expect(out.pageId).toBe('home')
    expect(Object.keys(out.cards).length).toBeGreaterThan(0)
  })

  it('falls back to built-in preset for wrong version', () => {
    const ctx = makeCtx()
    const out = repairLayout({ version: 99, pageId: 'home' }, ctx)
    expect(out.pageId).toBe('home')
  })

  it('falls back to preset (never blank) for garbage columns', () => {
    const ctx = makeCtx()
    const out = repairLayout({ version: 1, pageId: 'home', columns: null, cards: null }, ctx)
    // repairs to non-empty columns
    expect(out.columns).toBeTruthy()
  })

  it('drops cards with unknown descriptorId', () => {
    const ctx = makeCtx()
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 260, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: ['a'], activeTabId: 'a' }, secondary: null, splitRatio: null },
          center: { width: 600, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          right: { width: 420, collapsed: false, surfaceMode: 'float', topInset: 48, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
        },
        cards: { a: { id: 'a', descriptorId: 'totally_unknown', title: 'x' } },
        focusedCardId: 'a',
        gap: 8,
      },
      ctx,
    )
    expect(out.cards['a']).toBeUndefined()
    expect(out.columns.left.primary.tabIds).toEqual([])
  })

  it('keeps known cards and drops duplicate singletons (keep first)', () => {
    const ctx = makeCtx()
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 260, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: ['a', 'b'], activeTabId: 'a' }, secondary: null, splitRatio: null },
          center: { width: 600, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          right: { width: 420, collapsed: false, surfaceMode: 'float', topInset: 48, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
        },
        cards: {
          // chat is singleton — two entries should collapse to one
          a: { id: 'a', descriptorId: 'chat', title: 'chat a' },
          b: { id: 'b', descriptorId: 'chat', title: 'chat b' },
        },
        focusedCardId: null,
        gap: 8,
      },
      ctx,
    )
    const chats = Object.values(out.cards).filter((c) => c.descriptorId === 'chat')
    expect(chats.length).toBe(1)
    expect(out.columns.left.primary.tabIds).toEqual(['a'])
  })

  it('clamps illegal sizes', () => {
    const ctx = makeCtx()
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 99999, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          center: { width: -5, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          right: { width: 420, collapsed: false, surfaceMode: 'float', topInset: 48, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
        },
        cards: {},
        focusedCardId: null,
        gap: 8,
      },
      ctx,
    )
    expect(out.columns.left.width).toBeLessThanOrEqual(2000)
    expect(out.columns.center.width).toBeGreaterThanOrEqual(160)
  })

  it('never produces a blank window even for total garbage', () => {
    const ctx = makeCtx()
    const out = repairLayout({ completely: 'garbage' }, ctx)
    expect(out).toBeTruthy()
    expect(out.version).toBe(1)
    // Should have at least some cards (preset fallback for home).
    expect(Object.keys(out.cards).length).toBeGreaterThan(0)
  })

  it('defaults legacy float-page JSON to an 8px edge inset', () => {
    const ctx = makeCtx()
    // Legacy snapshot: no edgeInset field.
    const home = repairLayout({ version: 1, pageId: 'home', columns: {}, cards: {} }, ctx)
    expect(home.edgeInset).toBe(8)
    const settings = repairLayout({ version: 1, pageId: 'settings', columns: {}, cards: {} }, ctx)
    expect(settings.edgeInset).toBe(8)
    const market = repairLayout({ version: 1, pageId: 'market', columns: {}, cards: {} }, ctx)
    expect(market.edgeInset).toBe(8)
  })

  it('defaults legacy app-page JSON to a flush edge inset', () => {
    const ctx = makeCtx()
    const code = repairLayout({ version: 1, pageId: 'code', columns: {}, cards: {} }, ctx)
    expect(code.edgeInset).toBe(0)
    const debug = repairLayout({ version: 1, pageId: 'debug', columns: {}, cards: {} }, ctx)
    expect(debug.edgeInset).toBe(0)
  })

  it('preserves a valid edgeInset and falls back to page default for garbage', () => {
    const ctx = makeCtx()
    const kept = repairLayout({ version: 1, pageId: 'home', columns: {}, cards: {}, edgeInset: 12 }, ctx)
    expect(kept.edgeInset).toBe(12)
    // Garbage values fall back to the page default (home = 8).
    const badString = repairLayout({ version: 1, pageId: 'home', columns: {}, cards: {}, edgeInset: 'x' }, ctx)
    expect(badString.edgeInset).toBe(8)
    const negative = repairLayout({ version: 1, pageId: 'home', columns: {}, cards: {}, edgeInset: -5 }, ctx)
    expect(negative.edgeInset).toBe(8)
    const tooBig = repairLayout({ version: 1, pageId: 'home', columns: {}, cards: {}, edgeInset: 99 }, ctx)
    expect(tooBig.edgeInset).toBe(8)
  })

  it('preserves an immersive surface mode and clamps anything else to float', () => {
    const ctx = makeCtx()
    const immersive = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 260, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          center: { width: 0, collapsed: false, surfaceMode: 'immersive', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          right: { width: 420, collapsed: false, surfaceMode: 'float', topInset: 48, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
        },
        cards: {},
        focusedCardId: null,
        gap: 8,
      },
      ctx,
    )
    expect(immersive.columns.center.surfaceMode).toBe('immersive')

    // Unknown value degrades to the safe float fallback.
    const garbled = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 260, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          center: { width: 0, collapsed: false, surfaceMode: 'weird', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          right: { width: 420, collapsed: false, surfaceMode: 'float', topInset: 48, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
        },
        cards: {},
        focusedCardId: null,
        gap: 8,
      },
      ctx,
    )
    expect(garbled.columns.center.surfaceMode).toBe('float')
  })

  it('migrates a persisted home layout so the chat column is immersive', () => {
    const ctx = makeCtx()
    // A layout saved before the immersive mode existed: center column hosts the
    // chat card but still says 'float'. Repair must force it immersive.
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 260, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: ['n'], activeTabId: 'n' }, secondary: null, splitRatio: null },
          center: { width: 0, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: ['c'], activeTabId: 'c' }, secondary: null, splitRatio: null },
          right: { width: 420, collapsed: false, surfaceMode: 'float', topInset: 48, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
        },
        cards: {
          n: { id: 'n', descriptorId: 'nav', title: '项目' },
          c: { id: 'c', descriptorId: 'chat', title: '聊天' },
        },
        focusedCardId: 'c',
        gap: 8,
      },
      ctx,
    )
    // The chat column becomes immersive; the non-chat columns stay float.
    expect(out.columns.center.surfaceMode).toBe('immersive')
    expect(out.columns.left.surfaceMode).toBe('float')
    expect(out.columns.right.surfaceMode).toBe('float')
  })

  it('keeps an explicitly persisted immersive home column immersive', () => {
    const ctx = makeCtx()
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 260, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          center: { width: 0, collapsed: false, surfaceMode: 'immersive', topInset: 8, primary: { stackId: 'primary', tabIds: ['c'], activeTabId: 'c' }, secondary: null, splitRatio: null },
          right: { width: 420, collapsed: false, surfaceMode: 'float', topInset: 48, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
        },
        cards: { c: { id: 'c', descriptorId: 'chat', title: '聊天' } },
        focusedCardId: 'c',
        gap: 8,
      },
      ctx,
    )
    expect(out.columns.center.surfaceMode).toBe('immersive')
  })

  it('normalizes grid coordinates and props on cards', () => {
    const ctx = makeCtx()
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columns: {
          center: { width: 0, collapsed: false, surfaceMode: 'immersive', topInset: 8, primary: { stackId: 'primary', tabIds: ['c'], activeTabId: 'c' }, secondary: null, splitRatio: null, dock: null },
        },
        cards: {
          c: { id: 'c', descriptorId: 'chat', title: '聊天', x: -5, y: 3.8, w: 10, h: 0, static: true },
        },
      },
      ctx,
    )
    const card = out.cards['c']
    expect(card).toBeTruthy()
    expect(card.x).toBe(0)
    expect(card.y).toBe(4)
    expect(card.w).toBe(10)
    expect(card.h).toBe(1)
    expect(card.static).toBe(true)
  })

  it('repairs a dock tree and forces exactly one main pane with homePicker first', () => {
    const ctx = makeCtx()
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 260, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          center: { width: 0, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null },
          right: {
            width: 640,
            collapsed: false,
            surfaceMode: 'float',
            topInset: 48,
            primary: { stackId: 'primary', tabIds: ['hp'], activeTabId: 'hp' },
            secondary: null,
            splitRatio: null,
            dock: {
              kind: 'split',
              splitId: 's1',
              dir: 'row',
              ratio: 2, // out of range → clamped
              a: {
                kind: 'split',
                splitId: 's2',
                dir: 'column',
                ratio: 0.5,
                a: { kind: 'pane', paneId: 'p-side', main: false, tabIds: ['appr'], activeTabId: 'appr' },
                b: { kind: 'pane', paneId: 'p-main-dup', main: true, tabIds: ['git', 'hp'], activeTabId: 'git' }, // duplicate main
              },
              b: { kind: 'pane', paneId: 'p-dup-main', main: true, tabIds: ['hp'], activeTabId: 'hp' }, // duplicate main
            },
          },
        },
        cards: {
          hp: { id: 'hp', descriptorId: 'homePicker', title: 'Home' },
          git: { id: 'git', descriptorId: 'git', title: 'Git' },
          appr: { id: 'appr', descriptorId: 'approval', title: '审批' },
        },
        focusedCardId: 'git',
        gap: 8,
      },
      ctx,
    )
    const dock = out.columns.right.dock!
    expect(dock).not.toBeNull()
    // primary/secondary are cleared while dock is present.
    expect(out.columns.right.primary.tabIds).toEqual([])
    // Ratio clamped to [0.1, 0.9].
    expect(dock.kind === 'split' && dock.ratio).toBe(0.9)
    // Exactly one main pane.
    const panes = collectDockPanes(dock)
    expect(panes.filter((p) => p.main)).toHaveLength(1)
    const main = panes.find((p) => p.main)!
    // homePicker forced to the front of the main pane.
    expect(main.tabIds[0]).toBe('hp')
  })

  it('drops unknown card references and dedupes dock tabIds', () => {
    const ctx = makeCtx()
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columns: {
          right: {
            width: 640,
            collapsed: false,
            surfaceMode: 'float',
            topInset: 48,
            primary: { stackId: 'primary', tabIds: [], activeTabId: null },
            secondary: null,
            splitRatio: null,
            dock: {
              kind: 'pane',
              paneId: 'p-main',
              main: true,
              tabIds: ['hp', 'hp', 'git', 'missing'],
              activeTabId: 'hp',
            },
          },
        },
        cards: {
          hp: { id: 'hp', descriptorId: 'homePicker', title: 'Home' },
          git: { id: 'git', descriptorId: 'git', title: 'Git' },
        },
        focusedCardId: null,
      },
      ctx,
    )
    const dock = out.columns.right.dock
    expect(dock).not.toBeNull()
    if (dock && dock.kind === 'pane') {
      expect(dock.tabIds).toEqual(['hp', 'git'])
    }
  })

  it('falls back to the built-in preset when cards are orphaned (corrupt snapshot)', () => {
    const ctx = makeCtx()
    // A snapshot where cards exist but no stack/dock references them — the
    // signature of a corrupted layout file. Repair must recover, never blank.
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columnOrder: ['left', 'center', 'right'],
        columns: {
          left: { width: 289, collapsed: false, surfaceMode: 'float', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null, dock: null },
          center: { width: 160, collapsed: false, surfaceMode: 'immersive', topInset: 8, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null, dock: null },
          right: { width: 281, collapsed: false, surfaceMode: 'float', topInset: 48, primary: { stackId: 'primary', tabIds: [], activeTabId: null }, secondary: null, splitRatio: null, dock: null },
        },
        cards: {
          n: { id: 'n', descriptorId: 'nav', title: '项目' },
          c: { id: 'c', descriptorId: 'chat', title: '聊天' },
          h: { id: 'h', descriptorId: 'homePicker', title: 'Home' },
          a: { id: 'a', descriptorId: 'agent', title: 'Agent' },
        },
        focusedCardId: 'a',
        gap: 8,
        edgeInset: 8,
      },
      ctx,
    )
    // Recovered to the home preset: every card referenced, columns populated.
    const refs = new Set<string>()
    for (const slot of Object.values(out.columns)) {
      for (const st of [slot.primary, slot.secondary]) if (st) st.tabIds.forEach((id) => refs.add(id))
    }
    expect(Object.keys(out.cards).length).toBeGreaterThan(0)
    for (const id of Object.keys(out.cards)) expect(refs.has(id)).toBe(true)
    // The home preset hosts homePicker in the right column.
    expect(out.columns.right.primary.tabIds.some((id) => out.cards[id].descriptorId === 'homePicker')).toBe(true)
  })

  it('falls back to stacks when the dock is unusable', () => {
    const ctx = makeCtx()
    const out = repairLayout(
      {
        version: 1,
        pageId: 'home',
        columns: {
          right: {
            width: 640,
            collapsed: false,
            surfaceMode: 'float',
            topInset: 48,
            primary: { stackId: 'primary', tabIds: ['hp'], activeTabId: 'hp' },
            secondary: null,
            splitRatio: null,
            dock: { kind: 'nonsense' },
          },
        },
        cards: { hp: { id: 'hp', descriptorId: 'homePicker', title: 'Home' } },
        focusedCardId: null,
      },
      ctx,
    )
    expect(out.columns.right.dock).toBeNull()
    expect(out.columns.right.primary.tabIds).toEqual(['hp'])
  })
})
