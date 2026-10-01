import { describe, it, expect, beforeEach } from 'vitest'
import { createUie, __resetUieForTests, type UieStoreOptions } from './useUie'
import { makeRegistry } from '../engine/__testUtils'
import { createCardRegistry } from '../engine/registry'
import { createLayerStore } from '../engine/persistence/layerStore'
import type { LayoutStorageBlob } from '../engine/persistence/types'

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

// Mirror the route pages: every UIE route enters through `useUiePage(page)`,
// which calls `showPage(page)` on mount.
function enterPage(uie: ReturnType<typeof createUie>, page: 'home' | 'market') {
  uie.showPage(page)
}

function memoryLayerStore(initial: LayoutStorageBlob | null = null) {
  let blob = initial
  return createLayerStore({
    async load() { return blob },
    async save(payload) { blob = payload; return true },
  })
}

describe('useUie page preset switching (market → home navigation)', () => {
  beforeEach(() => {
    __resetUieForTests()
  })

  it('cold-start home stays on the home layout (no preset clobber)', () => {
    const opts: UieStoreOptions = { registry: makeFullRegistry() }
    const uie = createUie(opts)
    enterPage(uie, 'home')
    expect(uie.pageId.value).toBe('home')
    // Home preset has the chat card in the immersive center column.
    expect(uie.snapshot.value.columns.center.surfaceMode).toBe('immersive')
    expect(uie.snapshot.value.pageId).toBe('home')
  })

  it('entering market switches the singleton snapshot to the market layout', () => {
    const opts: UieStoreOptions = { registry: makeFullRegistry() }
    const uie = createUie(opts)
    enterPage(uie, 'market')
    expect(uie.pageId.value).toBe('market')
    expect(uie.snapshot.value.columns.center.surfaceMode).toBe('float')
    // Market cards present, home chat card gone.
    const cards = Object.values(uie.snapshot.value.cards).map((c) => c.descriptorId)
    expect(cards).toContain('marketCatalog')
    expect(cards).not.toContain('chat')
  })

  it('returning home re-applies the home layout (fixes "stuck on market")', () => {
    const opts: UieStoreOptions = { registry: makeFullRegistry() }
    const uie = createUie(opts)
    enterPage(uie, 'market')
    enterPage(uie, 'home')
    // The regression: without HomePage's showPage('home') entry, pageId
    // stays 'market' and the shell renders market columns.
    expect(uie.pageId.value).toBe('home')
    expect(uie.snapshot.value.pageId).toBe('home')
    expect(uie.snapshot.value.columns.center.surfaceMode).toBe('immersive')
    const cards = Object.values(uie.snapshot.value.cards).map((c) => c.descriptorId)
    expect(cards).toContain('chat')
    expect(cards).not.toContain('marketCatalog')
  })

  it('round-tripping market keeps the customised home layout', async () => {
    const store = memoryLayerStore()
    await store.hydrate()
    const uie = createUie({ registry: makeFullRegistry(), persistence: { store } })
    await Promise.resolve()
    uie.dispatch({
      command: { type: 'resizeColumn', scope: uie.scope.value, slotId: 'left', width: 333 },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
    const customised = uie.snapshot.value.columns.left.width
    expect(customised).toBe(333)
    enterPage(uie, 'market')
    enterPage(uie, 'home')
    expect(uie.pageId.value).toBe('home')
    expect(uie.snapshot.value.columns.left.width).toBe(customised)
  })

  it('cold deep link hydrates the page being shown, not the initial home page', async () => {
    const seeded = createUie({ registry: makeFullRegistry() })
    seeded.showPage('market')
    const market = seeded.snapshot.value
    const store = memoryLayerStore({ version: 1, pageByPageId: { market } } as LayoutStorageBlob)
    __resetUieForTests()
    const uie = createUie({ registry: makeFullRegistry(), persistence: { store } })
    enterPage(uie, 'market') // route mounts before the async disk read resolves
    await new Promise((r) => setTimeout(r, 0))
    expect(uie.pageId.value).toBe('market')
    expect(uie.snapshot.value.cards).toEqual(market.cards)
  })
})

describe('useUie per-project layouts', () => {
  beforeEach(() => {
    __resetUieForTests()
  })

  function setup(initial: LayoutStorageBlob | null = null) {
    let saved: LayoutStorageBlob | null = initial
    const store = createLayerStore({
      async load() { return saved },
      async save(payload) { saved = payload; return true },
    })
    const uie = createUie({ registry: makeFullRegistry(), persistence: { store } })
    const resizeLeft = (width: number) => uie.dispatch({
      command: { type: 'resizeColumn', scope: uie.scope.value, slotId: 'left', width },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
    const leftWidth = () => uie.snapshot.value.columns.left.width
    const blob = () => { store.flush(); return saved }
    return { uie, store, resizeLeft, leftWidth, blob }
  }

  it('remembers a separate home layout for each project', async () => {
    const { uie, store, resizeLeft, leftWidth } = setup()
    await store.hydrate()
    const presetWidth = leftWidth()

    uie.setActiveProjectId('p1')
    expect(uie.scope.value).toEqual({ kind: 'workspace-page', projectId: 'p1', pageId: 'home' })
    resizeLeft(333)

    uie.setActiveProjectId('p2')
    expect(leftWidth()).toBe(presetWidth)
    resizeLeft(301)

    uie.setActiveProjectId('p1')
    expect(leftWidth()).toBe(333)
    uie.setActiveProjectId('p2')
    expect(leftWidth()).toBe(301)
  })

  it('a project without its own layout inherits the page-wide one without forking it', async () => {
    const { uie, store, resizeLeft, leftWidth, blob } = setup()
    await store.hydrate()
    resizeLeft(345) // no workspace selected -> page-wide layout

    uie.setActiveProjectId('p1')
    expect(leftWidth()).toBe(345)
    expect(blob()?.workspaceByKey ?? {}).toEqual({})

    uie.setActiveProjectId(null)
    resizeLeft(350)
    uie.setActiveProjectId('p1')
    expect(leftWidth()).toBe(350) // still following the page-wide layout
  })

  it('keeps market page-wide regardless of the active project', async () => {
    const { uie, store, resizeLeft, leftWidth } = setup()
    await store.hydrate()
    uie.setActiveProjectId('p1')
    uie.showPage('market')
    expect(uie.scope.value).toEqual({ kind: 'page', pageId: 'market' })
    resizeLeft(310)
    const edited = leftWidth()

    uie.setActiveProjectId('p2')
    expect(uie.pageId.value).toBe('market')
    expect(leftWidth()).toBe(edited)
  })

  it('never lets undo cross into another project layout', async () => {
    const { uie, store, resizeLeft } = setup()
    await store.hydrate()
    uie.setActiveProjectId('p1')
    resizeLeft(333)
    expect(uie.canUndo.value).toBe(true)

    uie.setActiveProjectId('p2')
    expect(uie.canUndo.value).toBe(false)
    expect(uie.undo()).toBe(false)
  })

  it('hydration resolves the active project layout', async () => {
    const seed = setup()
    await seed.store.hydrate()
    seed.uie.setActiveProjectId('p1')
    seed.resizeLeft(333)
    const persisted = seed.blob()

    __resetUieForTests()
    const { uie, store, leftWidth } = setup(persisted)
    uie.setActiveProjectId('p1') // project selected before the disk read resolves
    await store.hydrate()
    await new Promise((r) => setTimeout(r, 0))
    expect(leftWidth()).toBe(333)
  })
})

