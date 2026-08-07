import { describe, it, expect } from 'vitest'
import { repairLayout, type RepairContext } from './repair'
import { makeRegistry } from './__testUtils'
import { buildPreset } from './presets'
import { createEmptySnapshot } from './reducer'

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

  it('defaults legacy home/settings JSON to an 8px edge inset', () => {
    const ctx = makeCtx()
    // Legacy snapshot: no edgeInset field.
    const home = repairLayout({ version: 1, pageId: 'home', columns: {}, cards: {} }, ctx)
    expect(home.edgeInset).toBe(8)
    const settings = repairLayout({ version: 1, pageId: 'settings', columns: {}, cards: {} }, ctx)
    expect(settings.edgeInset).toBe(8)
  })

  it('defaults legacy app-page JSON to a flush edge inset', () => {
    const ctx = makeCtx()
    const market = repairLayout({ version: 1, pageId: 'market', columns: {}, cards: {} }, ctx)
    expect(market.edgeInset).toBe(0)
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
})
