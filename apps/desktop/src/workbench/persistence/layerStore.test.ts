import { describe, it, expect, beforeEach, vi } from 'vitest'
import { createLayerStore } from './layerStore'
import type { LayoutAdapter, LayoutStorageBlob } from './types'
import type { WorkbenchLayoutSnapshot } from '../types'
import { buildPreset } from '../presets'

function makeAdapter(initial: LayoutStorageBlob | null = null): { adapter: LayoutAdapter; saved: LayoutStorageBlob[] } {
  let blob = initial
  const saved: LayoutStorageBlob[] = []
  return {
    adapter: {
      async load() { return blob },
      async save(payload) { blob = payload; saved.push(payload); return true },
    },
    saved,
  }
}

function homePreset(): WorkbenchLayoutSnapshot {
  return buildPreset('home', { nextInstanceId: () => `p-${Math.random()}` })
}

describe('layer store', () => {
  it('resolveSnapshot returns null when nothing stored', async () => {
    const { adapter } = makeAdapter()
    const store = createLayerStore(adapter)
    await store.hydrate()
    expect(store.resolveSnapshot('home', null)).toBeNull()
  })

  it('saveSnapshot writes to workspace scope when a project is active', async () => {
    const { adapter, saved } = makeAdapter()
    const store = createLayerStore(adapter)
    await store.hydrate()
    const snap = homePreset()
    store.saveSnapshot(snap, 'proj-1')
    store.flush()
    // Debounced — flush triggers the save.
    expect(saved.length).toBe(1)
    const ws = saved[0].workspaceByKey?.['proj-1:home']
    expect(ws?.pageId).toBe('home')
  })

  it('resolveSnapshot prefers workspace > page > global', async () => {
    const pageSnap = homePreset()
    pageSnap.focusedCardId = 'PAGE'
    const globalSnap = homePreset()
    globalSnap.focusedCardId = 'GLOBAL'
    const wsSnap = homePreset()
    wsSnap.focusedCardId = 'WS'

    const blob: LayoutStorageBlob = {
      version: 1,
      pageByPageId: { home: pageSnap },
      globalByPage: { home: globalSnap },
      workspaceByKey: { 'proj-1:home': wsSnap },
    }
    const { adapter } = makeAdapter(blob)
    const store = createLayerStore(adapter)
    await store.hydrate()

    expect(store.resolveSnapshot('home', 'proj-1')?.focusedCardId).toBe('WS')
    expect(store.resolveSnapshot('home', null)?.focusedCardId).toBe('PAGE')
  })

  it('falls back to global when page missing', async () => {
    const globalSnap = homePreset()
    globalSnap.focusedCardId = 'GLOBAL'
    const blob: LayoutStorageBlob = { version: 1, globalByPage: { home: globalSnap } }
    const { adapter } = makeAdapter(blob)
    const store = createLayerStore(adapter)
    await store.hydrate()
    expect(store.resolveSnapshot('home', null)?.focusedCardId).toBe('GLOBAL')
  })

  it('saveAsGlobal stores per-page and saveAsPage stores page-wide', async () => {
    const { adapter, saved } = makeAdapter()
    const store = createLayerStore(adapter)
    await store.hydrate()
    const snap = homePreset()
    store.saveAsGlobal(snap)
    store.flush()
    expect(saved[0].globalByPage?.home).toBeTruthy()

    store.saveAsPage(snap)
    store.flush()
    expect(saved[1].pageByPageId?.home).toBeTruthy()
  })

  it('resetWorkspace deletes the workspace override', async () => {
    const wsSnap = homePreset()
    const blob: LayoutStorageBlob = { version: 1, workspaceByKey: { 'proj-1:home': wsSnap } }
    const { adapter, saved } = makeAdapter(blob)
    const store = createLayerStore(adapter)
    await store.hydrate()
    expect(store.resolveSnapshot('home', 'proj-1')).toBeTruthy()
    store.resetWorkspace('home', 'proj-1')
    store.flush()
    expect(store.resolveSnapshot('home', 'proj-1')).toBeNull()
    expect(saved[0].workspaceByKey?.['proj-1:home']).toBeUndefined()
  })

  it('debounces saves', async () => {
    vi.useFakeTimers()
    try {
      const { adapter, saved } = makeAdapter()
      const store = createLayerStore(adapter)
      await store.hydrate()
      store.saveSnapshot(homePreset(), null)
      store.saveSnapshot(homePreset(), null)
      // Before debounce elapses, no save happened.
      expect(saved.length).toBe(0)
      vi.advanceTimersByTime(500)
      // Debounced save fired once with the latest state.
      expect(saved.length).toBe(1)
    } finally {
      vi.useRealTimers()
    }
  })
})
