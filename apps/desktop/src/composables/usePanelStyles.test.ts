import { Window } from 'happy-dom'
import { effectScope } from 'vue'
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'

const STORAGE_KEY = 'tinadec-panel-style'
let testWindow: Window
let panelStylesModule: typeof import('./usePanelStyles')

async function loadPanelStyles() {
  return panelStylesModule
}

describe('usePanelStyles persistence', () => {
  beforeAll(async () => {
    testWindow = new Window({ url: 'http://127.0.0.1:5173' })
    vi.stubGlobal('window', testWindow)
    vi.stubGlobal('document', testWindow.document)
    vi.stubGlobal('localStorage', testWindow.localStorage)
    vi.stubGlobal('Storage', testWindow.Storage)
    vi.stubGlobal('StorageEvent', testWindow.StorageEvent)
    panelStylesModule = await import('./usePanelStyles')
  })

  beforeEach(() => {
    panelStylesModule.__resetPanelStylesForTests()
    testWindow.localStorage.clear()
  })

  afterAll(() => {
    testWindow.close()
    vi.unstubAllGlobals()
  })

  it('persists material changes synchronously', async () => {
    const { usePanelStyles } = await loadPanelStyles()
    const { updatePanelStyle } = usePanelStyles()

    updatePanelStyle({ effect: 'blur', opacity: 73, blur: 11 })

    expect(JSON.parse(testWindow.localStorage.getItem(STORAGE_KEY) ?? 'null')).toEqual({
      effect: 'blur',
      opacity: 73,
      blur: 11,
    })
  })

  it('keeps persisting after the initializing component scope is disposed', async () => {
    const { usePanelStyles } = await loadPanelStyles()
    const initializingScope = effectScope()

    initializingScope.run(() => usePanelStyles())
    initializingScope.stop()

    usePanelStyles().updatePanelStyle({ effect: 'blur', opacity: 68, blur: 13 })

    expect(JSON.parse(testWindow.localStorage.getItem(STORAGE_KEY) ?? 'null')).toEqual({
      effect: 'blur',
      opacity: 68,
      blur: 13,
    })
  })

  it('restores all persisted material fields after module initialization', async () => {
    testWindow.localStorage.setItem(STORAGE_KEY, JSON.stringify({
      effect: 'blur',
      opacity: 64,
      blur: 15,
    }))

    const { usePanelStyles } = await loadPanelStyles()

    expect(usePanelStyles().panelStyle.value).toEqual({
      effect: 'blur',
      opacity: 64,
      blur: 15,
    })
  })

  it('merges missing fields and normalizes invalid persisted values', async () => {
    testWindow.localStorage.setItem(STORAGE_KEY, JSON.stringify({
      effect: 'glass',
      opacity: 140,
    }))

    const { usePanelStyles } = await loadPanelStyles()
    const { panelStyle } = usePanelStyles()

    expect(panelStyle.value).toEqual({
      effect: 'opaque',
      opacity: 100,
      blur: 8,
    })
    expect(JSON.parse(testWindow.localStorage.getItem(STORAGE_KEY) ?? 'null')).toEqual(panelStyle.value)
  })

  it('normalizes runtime updates before persisting them', async () => {
    const { usePanelStyles } = await loadPanelStyles()
    const { panelStyle, updatePanelStyle } = usePanelStyles()

    updatePanelStyle({ opacity: -10, blur: Number.POSITIVE_INFINITY })

    expect(panelStyle.value).toEqual({
      effect: 'opaque',
      opacity: 0,
      blur: 8,
    })
    expect(JSON.parse(testWindow.localStorage.getItem(STORAGE_KEY) ?? 'null')).toEqual(panelStyle.value)
  })
})
