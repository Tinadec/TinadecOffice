import { describe, it, expect, beforeEach } from 'vitest'
import { createUie, __resetUieForTests, type UieStoreOptions } from './useUie'
import { makeRegistry } from '../engine/__testUtils'
import { createCardRegistry } from '../engine/registry'

// A registry with both home and market cards so page presets can be applied.
function makeFullRegistry() {
  const reg = createCardRegistry()
  reg.register({
    type: 'nav', component: {} as never, minWidth: 220, minHeight: 120,
    singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '项目',
  })
  reg.register({
    type: 'chat', component: {} as never, minWidth: 320, minHeight: 200,
    singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '聊天',
  })
  reg.register({
    type: 'marketFilter', component: {} as never, minWidth: 240, minHeight: 160,
    singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '筛选',
  })
  reg.register({
    type: 'marketCatalog', component: {} as never, minWidth: 280, minHeight: 160,
    singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '目录',
  })
  reg.register({
    type: 'marketDetail', component: {} as never, minWidth: 280, minHeight: 160,
    singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '详情',
  })
  return reg
}

// Mirror the pages' preset-application guards:
//   MarketPage:  if (wb.pageId.value !== 'market') wb.applyPreset('market')
//   HomePage:    if (wb.pageId.value !== 'home') wb.applyPreset('home')
// This is the exact navigation sequence: cold-start home → market → back home.
function enterPage(wb: ReturnType<typeof createUie>, page: 'home' | 'market') {
  if (wb.pageId.value !== page) {
    wb.applyPreset(page)
  }
}

describe('useUie page preset switching (market → home navigation)', () => {
  beforeEach(() => {
    __resetUieForTests()
  })

  it('cold-start home stays on the home layout (no preset clobber)', () => {
    const opts: UieStoreOptions = { registry: makeFullRegistry() }
    const wb = createUie(opts)
    enterPage(wb, 'home')
    expect(wb.pageId.value).toBe('home')
    // Home preset has the chat card in the immersive center column.
    expect(wb.snapshot.value.columns.center.surfaceMode).toBe('immersive')
    expect(wb.snapshot.value.pageId).toBe('home')
  })

  it('entering market switches the singleton snapshot to the market layout', () => {
    const opts: UieStoreOptions = { registry: makeFullRegistry() }
    const wb = createUie(opts)
    enterPage(wb, 'market')
    expect(wb.pageId.value).toBe('market')
    expect(wb.snapshot.value.columns.center.surfaceMode).toBe('float')
    // Market cards present, home chat card gone.
    const cards = Object.values(wb.snapshot.value.cards).map((c) => c.descriptorId)
    expect(cards).toContain('marketCatalog')
    expect(cards).not.toContain('chat')
  })

  it('returning home re-applies the home layout (fixes "stuck on market")', () => {
    const opts: UieStoreOptions = { registry: makeFullRegistry() }
    const wb = createUie(opts)
    enterPage(wb, 'market')
    enterPage(wb, 'home')
    // The regression: without HomePage's applyPreset('home') guard, pageId
    // stays 'market' and the shell renders market columns.
    expect(wb.pageId.value).toBe('home')
    expect(wb.snapshot.value.pageId).toBe('home')
    expect(wb.snapshot.value.columns.center.surfaceMode).toBe('immersive')
    const cards = Object.values(wb.snapshot.value.cards).map((c) => c.descriptorId)
    expect(cards).toContain('chat')
    expect(cards).not.toContain('marketCatalog')
  })
})
