// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { __resetThemeForTests, useTheme } from './useTheme'
import { __resetPanelStylesForTests, usePanelStyles } from './usePanelStyles'
import { resetHostAccessForTests, setHostAccessStatus } from '@/lib/hostAccess'
import {
  readSpatialPromotionPreference,
  saveSpatialPromotionPreference,
} from '@/components/board/useProjectBoardView'

const sseState = vi.hoisted(() => ({
  listeners: new Map<string, Set<EventListener>>(),
  closed: false,
}))

const apiMocks = vi.hoisted(() => ({
  getUiPreferences: vi.fn(),
  putUiPreferences: vi.fn(),
  patchUiPreferences: vi.fn(),
  uiPreferencesEventsUrl: vi.fn(() => 'http://127.0.0.1:48730/api/v1/preferences/events'),
}))

vi.mock('@/lib/scopedEventSource', () => {
  class MockScopedEventSource {
    url: string
    constructor(url: string) {
      this.url = url
      sseState.closed = false
    }
    addEventListener(type: string, listener: EventListenerOrEventListenerObject | null) {
      if (!listener) return
      const fn = typeof listener === 'function' ? listener : (e: Event) => listener.handleEvent(e)
      let bucket = sseState.listeners.get(type)
      if (!bucket) {
        bucket = new Set()
        sseState.listeners.set(type, bucket)
      }
      bucket.add(fn)
    }
    removeEventListener(type: string, listener: EventListenerOrEventListenerObject | null) {
      if (!listener || typeof listener !== 'function') return
      sseState.listeners.get(type)?.delete(listener)
    }
    close() {
      sseState.closed = true
      sseState.listeners.clear()
    }
  }
  return { ScopedEventSource: MockScopedEventSource }
})

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      getUiPreferences: apiMocks.getUiPreferences,
      putUiPreferences: apiMocks.putUiPreferences,
      patchUiPreferences: apiMocks.patchUiPreferences,
      uiPreferencesEventsUrl: apiMocks.uiPreferencesEventsUrl,
    },
    getUiPreferences: apiMocks.getUiPreferences,
    putUiPreferences: apiMocks.putUiPreferences,
    patchUiPreferences: apiMocks.patchUiPreferences,
    uiPreferencesEventsUrl: apiMocks.uiPreferencesEventsUrl,
  }
})

import {
  __resetUiPreferencesForTests,
  DEFAULT_UI_APPEARANCE_PREFERENCES,
  DEFAULT_UI_INTERACTION_PREFERENCES,
  UI_PREFERENCES_MIGRATED_KEY,
  useUiPreferences,
} from './useUiPreferences'

function emitSsePreferencesUpdated(payload: unknown): void {
  const listeners = sseState.listeners.get('preferences.updated')
  if (!listeners) return
  const event = new MessageEvent('preferences.updated', {
    data: JSON.stringify(payload),
  })
  for (const listener of listeners) {
    listener(event)
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  sseState.listeners.clear()
  localStorage.clear()
  delete document.documentElement.dataset.reducedMotion
  __resetUiPreferencesForTests()
  __resetThemeForTests()
  __resetPanelStylesForTests()
  resetHostAccessForTests()

  apiMocks.getUiPreferences.mockResolvedValue({
    version: 1,
    appearance: { ...DEFAULT_UI_APPEARANCE_PREFERENCES },
    interaction: { ...DEFAULT_UI_INTERACTION_PREFERENCES },
    file_path: 'C:/Users/demo/.tinadec/preferences.toml',
    content_hash: 'hash-initial',
  })
  apiMocks.patchUiPreferences.mockImplementation(async (patch) => ({
    version: 1,
    appearance: {
      ...DEFAULT_UI_APPEARANCE_PREFERENCES,
      ...(patch?.appearance ?? {}),
    },
    interaction: {
      ...DEFAULT_UI_INTERACTION_PREFERENCES,
      ...(patch?.interaction ?? {}),
    },
    file_path: 'C:/Users/demo/.tinadec/preferences.toml',
    content_hash: 'hash-patched',
  }))
  apiMocks.putUiPreferences.mockImplementation(async (input) => ({
    version: 1,
    appearance: { ...input.appearance },
    interaction: { ...input.interaction },
    file_path: 'C:/Users/demo/.tinadec/preferences.toml',
    content_hash: 'hash-put',
  }))
})

afterEach(() => {
  __resetUiPreferencesForTests()
  __resetThemeForTests()
  __resetPanelStylesForTests()
  resetHostAccessForTests()
  localStorage.clear()
})

describe('useUiPreferences unified store', () => {
  it('migrates legacy localStorage preferences once onto a fresh server preferences.toml without re-patching', async () => {
    localStorage.setItem('tinadec-theme', 'light')
    localStorage.setItem('tinadec-accent-color', 'warm')
    localStorage.setItem('tinadec-panel-style', JSON.stringify({ effect: 'opaque', opacity: 80, blur: 8 }))
    localStorage.setItem(
      'tinadec.projectBoard.spatialPromotePref.v1',
      JSON.stringify({ mode: 'parent_with_children', target_status: 'backlog' }),
    )

    setHostAccessStatus({ state: 'ready', managed: true })
    const store = useUiPreferences()
    await flushPromises()

    expect(localStorage.getItem(UI_PREFERENCES_MIGRATED_KEY)).toBe('1')
    expect(apiMocks.patchUiPreferences).toHaveBeenCalledTimes(1)
    expect(apiMocks.patchUiPreferences).toHaveBeenCalledWith({
      appearance: expect.objectContaining({
        theme: 'light',
        accent: 'warm',
        panel_effect: 'solid',
      }),
      interaction: expect.objectContaining({
        spatial_promote_mode: 'parent_children',
      }),
    })
    expect(store.appearance.theme).toBe('light')
    expect(store.appearance.panel_effect).toBe('solid')
    expect(store.interaction.spatial_promote_mode).toBe('parent_children')

    apiMocks.patchUiPreferences.mockClear()
    await store.refresh()
    expect(apiMocks.patchUiPreferences).not.toHaveBeenCalled()
  })

  it('bridges useTheme, usePanelStyles, spatial board promotion, and dataset.reducedMotion bidirectionally without feedback loops', async () => {
    localStorage.setItem(UI_PREFERENCES_MIGRATED_KEY, '1')
    setHostAccessStatus({ state: 'ready', managed: true })

    const store = useUiPreferences()
    const themeCtl = useTheme()
    const panelCtl = usePanelStyles()
    await flushPromises()
    apiMocks.patchUiPreferences.mockClear()

    // Store -> Theme / Panel / Spatial / Reduced Motion
    await store.updateAppearance({
      theme: 'light',
      panel_effect: 'solid',
      workspace_view: 'list',
      workspace_folder_tone: 'teal',
    })
    expect(apiMocks.patchUiPreferences).toHaveBeenCalledTimes(1)
    expect(themeCtl.theme.value).toBe('light')
    expect(panelCtl.panelStyle.value.effect).toBe('opaque')
    expect(store.appearance.workspace_view).toBe('list')
    expect(store.appearance.workspace_folder_tone).toBe('teal')

    await store.updateInteraction({
      spatial_promote_mode: 'parent_children',
      reduced_motion: 'on',
    })
    expect(readSpatialPromotionPreference().mode).toBe('parent_with_children')
    expect(document.documentElement.dataset.reducedMotion).toBe('true')

    await store.updateInteraction({ reduced_motion: 'off' })
    expect(document.documentElement.dataset.reducedMotion).toBe('false')

    // Reverse direction: SpatialSelectionBar / useTheme -> Store & PATCH
    apiMocks.patchUiPreferences.mockClear()
    saveSpatialPromotionPreference({ mode: 'split_many', target_status: 'ready' })
    await flushPromises()
    expect(store.interaction.spatial_promote_mode).toBe('split')
    expect(apiMocks.patchUiPreferences).toHaveBeenCalledTimes(1)
    expect(apiMocks.patchUiPreferences).toHaveBeenCalledWith({
      interaction: { spatial_promote_mode: 'split' },
    })

    apiMocks.patchUiPreferences.mockClear()
    panelCtl.setPanelEffect('blur')
    await flushPromises()
    expect(store.appearance.panel_effect).toBe('blur')
    expect(apiMocks.patchUiPreferences).toHaveBeenCalledTimes(1)
  })

  it('applies live SSE preferences.updated frames without echoing PATCH back to the server', async () => {
    localStorage.setItem(UI_PREFERENCES_MIGRATED_KEY, '1')
    setHostAccessStatus({ state: 'ready', managed: true })

    const store = useUiPreferences()
    await flushPromises()
    apiMocks.patchUiPreferences.mockClear()

    emitSsePreferencesUpdated({
      version: 1,
      file_path: 'C:/Users/demo/.tinadec/preferences.toml',
      content_hash: 'hash-sse-2',
      appearance: {
        ...DEFAULT_UI_APPEARANCE_PREFERENCES,
        workspace_view: 'list',
        workspace_folder_tone: 'coral',
        panel_effect: 'blur',
      },
      interaction: {
        ...DEFAULT_UI_INTERACTION_PREFERENCES,
        workspace_drop_action: 'ask',
        reduced_motion: 'on',
      },
    })
    await flushPromises()

    expect(store.appearance.workspace_view).toBe('list')
    expect(store.appearance.workspace_folder_tone).toBe('coral')
    expect(store.interaction.workspace_drop_action).toBe('ask')
    expect(document.documentElement.dataset.reducedMotion).toBe('true')
    expect(store.contentHash.value).toBe('hash-sse-2')
    expect(apiMocks.patchUiPreferences).not.toHaveBeenCalled()
  })

  it('falls back cleanly when offline and restores defaults via PUT on resetPreferences', async () => {
    setHostAccessStatus({ state: 'preview', managed: false })
    const store = useUiPreferences()

    await store.updateAppearance({ workspace_folder_tone: 'sky', workspace_view: 'list' })
    expect(store.appearance.workspace_folder_tone).toBe('sky')
    expect(store.appearance.workspace_view).toBe('list')
    expect(apiMocks.patchUiPreferences).not.toHaveBeenCalled()

    setHostAccessStatus({ state: 'ready', managed: true })
    await flushPromises()
    await store.resetPreferences()

    expect(apiMocks.putUiPreferences).toHaveBeenCalledWith({
      reset_defaults: true,
      appearance: DEFAULT_UI_APPEARANCE_PREFERENCES,
      interaction: DEFAULT_UI_INTERACTION_PREFERENCES,
    })
    expect(store.appearance.workspace_folder_tone).toBe('amber')
    expect(store.appearance.workspace_view).toBe('grid')
  })
})
