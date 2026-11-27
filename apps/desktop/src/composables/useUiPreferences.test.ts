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
  mergeAppearancePreferences,
  normalizeAppearancePreferences,
  UI_PANEL_PRESET_DEFAULTS,
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
    appearance: mergeAppearancePreferences(DEFAULT_UI_APPEARANCE_PREFERENCES, patch?.appearance),
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
        panel_preset: 'solid',
        panel_opacity: 100,
        panel_blur: 0,
        panel_saturation: 100,
      }),
      interaction: expect.objectContaining({
        spatial_promote_mode: 'parent_children',
      }),
    })
    expect(store.appearance.theme).toBe('light')
    expect(store.appearance.panel_effect).toBe('solid')
    expect(store.appearance.panel_preset).toBe('solid')
    expect(store.interaction.spatial_promote_mode).toBe('parent_children')

    apiMocks.patchUiPreferences.mockClear()
    await store.refresh()
    expect(apiMocks.patchUiPreferences).not.toHaveBeenCalled()
  })

  it('normalizes canonical presets, clamps custom ranges (0..100, 0..32, 100..200), and derives legacy panel_effect fallbacks', () => {
    // Legacy payloads omitting the 4 new fields derive canonical presets
    expect(
      normalizeAppearancePreferences({
        theme: 'dark',
        accent: 'monochrome',
        custom_accent_hex: '#18181b',
        panel_effect: 'solid',
        workspace_view: 'grid',
        workspace_folder_tone: 'amber',
      }),
    ).toMatchObject({
      panel_effect: 'solid',
      panel_preset: 'solid',
      panel_opacity: 100,
      panel_blur: 0,
      panel_saturation: 100,
    })

    expect(
      normalizeAppearancePreferences({
        panel_effect: 'blur',
      }),
    ).toMatchObject({
      panel_effect: 'blur',
      panel_preset: 'frosted',
      panel_opacity: 68,
      panel_blur: 18,
      panel_saturation: 160,
    })

    expect(
      normalizeAppearancePreferences({
        panel_effect: 'translucent',
      }),
    ).toMatchObject({
      panel_effect: 'translucent',
      panel_preset: 'translucent',
      panel_opacity: 76,
      panel_blur: 0,
      panel_saturation: 100,
    })

    // Named presets expand canonical optical parameters
    for (const preset of ['solid', 'frosted', 'crystal', 'mica', 'translucent'] as const) {
      expect(normalizeAppearancePreferences({ panel_preset: preset })).toMatchObject({
        panel_preset: preset,
        ...UI_PANEL_PRESET_DEFAULTS[preset],
      })
    }

    // Custom clamping: opacity 0..100, blur 0..32, saturation 100..200
    expect(
      normalizeAppearancePreferences({
        panel_preset: 'custom',
        panel_effect: 'blur',
        panel_opacity: 145,
        panel_blur: 48,
        panel_saturation: 260,
      }),
    ).toMatchObject({
      panel_preset: 'custom',
      panel_effect: 'blur',
      panel_opacity: 100,
      panel_blur: 32,
      panel_saturation: 200,
    })

    expect(
      normalizeAppearancePreferences({
        panel_preset: 'custom',
        panel_effect: 'blur',
        panel_opacity: -15,
        panel_blur: -4,
        panel_saturation: 40,
      }),
    ).toMatchObject({
      panel_preset: 'custom',
      panel_effect: 'blur',
      panel_opacity: 0,
      panel_blur: 0,
      panel_saturation: 100,
    })
  })

  it('bridges useTheme, usePanelStyles (including presets & custom sliders), spatial board promotion, and dataset.reducedMotion bidirectionally without feedback loops', async () => {
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
      panel_preset: 'crystal',
      workspace_view: 'list',
      workspace_folder_tone: 'teal',
    })
    expect(apiMocks.patchUiPreferences).toHaveBeenCalledTimes(1)
    expect(themeCtl.theme.value).toBe('light')
    expect(panelCtl.panelStyle.value.effect).toBe('blur')
    expect(panelCtl.panelStyle.value.opacity).toBe(48)
    expect(panelCtl.panelStyle.value.blur).toBe(24)
    expect(store.appearance.panel_preset).toBe('crystal')
    expect(store.appearance.panel_saturation).toBe(180)
    expect(store.appearance.workspace_view).toBe('list')
    expect(store.appearance.workspace_folder_tone).toBe('teal')

    await store.updateAppearance({
      panel_effect: 'solid',
    })
    expect(panelCtl.panelStyle.value.effect).toBe('opaque')
    expect(store.appearance.panel_preset).toBe('solid')

    await store.updateInteraction({
      spatial_promote_mode: 'parent_children',
      reduced_motion: 'on',
    })
    expect(readSpatialPromotionPreference().mode).toBe('parent_with_children')
    expect(document.documentElement.dataset.reducedMotion).toBe('true')

    await store.updateInteraction({ reduced_motion: 'off' })
    expect(document.documentElement.dataset.reducedMotion).toBe('false')

    // Reverse direction: SpatialSelectionBar / usePanelStyles -> Store & PATCH
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

  it('applies live SSE preferences.updated frames (including legacy and preset payloads) without echoing PATCH back to the server', async () => {
    localStorage.setItem(UI_PREFERENCES_MIGRATED_KEY, '1')
    setHostAccessStatus({ state: 'ready', managed: true })

    const store = useUiPreferences()
    const panelCtl = usePanelStyles()
    await flushPromises()
    apiMocks.patchUiPreferences.mockClear()

    // Legacy SSE payload omitting the 4 new fields derives frosted defaults for blur
    emitSsePreferencesUpdated({
      version: 1,
      file_path: 'C:/Users/demo/.tinadec/preferences.toml',
      content_hash: 'hash-sse-2',
      appearance: {
        theme: 'dark',
        accent: 'monochrome',
        custom_accent_hex: '#18181b',
        panel_effect: 'blur',
        workspace_view: 'list',
        workspace_folder_tone: 'coral',
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
    expect(store.appearance.panel_effect).toBe('blur')
    expect(store.appearance.panel_preset).toBe('frosted')
    expect(store.appearance.panel_opacity).toBe(68)
    expect(store.appearance.panel_blur).toBe(18)
    expect(store.appearance.panel_saturation).toBe(160)
    expect(panelCtl.panelStyle.value.effect).toBe('blur')
    expect(panelCtl.panelStyle.value.opacity).toBe(68)
    expect(panelCtl.panelStyle.value.blur).toBe(18)
    expect(store.interaction.workspace_drop_action).toBe('ask')
    expect(document.documentElement.dataset.reducedMotion).toBe('true')
    expect(store.contentHash.value).toBe('hash-sse-2')
    expect(apiMocks.patchUiPreferences).not.toHaveBeenCalled()
  })

  it('falls back cleanly when offline and restores defaults via PUT on resetPreferences', async () => {
    setHostAccessStatus({ state: 'preview', managed: false })
    const store = useUiPreferences()

    await store.updateAppearance({
      workspace_folder_tone: 'sky',
      workspace_view: 'list',
      panel_preset: 'mica',
    })
    expect(store.appearance.workspace_folder_tone).toBe('sky')
    expect(store.appearance.workspace_view).toBe('list')
    expect(store.appearance.panel_preset).toBe('mica')
    expect(store.appearance.panel_opacity).toBe(82)
    expect(store.appearance.panel_blur).toBe(14)
    expect(store.appearance.panel_saturation).toBe(130)
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
    expect(store.appearance.panel_preset).toBe('translucent')
  })

  it('skips opening SSE when streamEvents is false and syncs via BroadcastChannel', async () => {
    const originalBc = globalThis.BroadcastChannel
    const channels: Array<{ name: string; onmessage: ((ev: MessageEvent) => void) | null; postMessage: (d: unknown) => void; close: () => void }> = []
    class FakeBroadcastChannel {
      name: string
      onmessage: ((ev: MessageEvent) => void) | null = null
      constructor(name: string) {
        this.name = name
        channels.push(this)
      }
      postMessage(data: unknown) {
        for (const ch of channels) {
          if (ch !== this && ch.name === this.name && ch.onmessage) {
            ch.onmessage({ data } as MessageEvent)
          }
        }
      }
      close() {}
    }
    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel)
    try {
      const store = useUiPreferences({ streamEvents: false })
      await flushPromises()
      expect(sseState.listeners.size).toBe(0)

      const peer = new FakeBroadcastChannel('tinadec:ui-preferences')
      peer.postMessage({
        type: 'ui-preferences-sync',
        doc: {
          version: 1,
          appearance: { ...DEFAULT_UI_APPEARANCE_PREFERENCES, theme: 'light' },
          interaction: { ...DEFAULT_UI_INTERACTION_PREFERENCES },
          content_hash: 'bc-sync-1',
        },
      })
      expect(store.appearance.theme).toBe('light')
      expect(store.contentHash.value).toBe('bc-sync-1')
    } finally {
      if (originalBc) vi.stubGlobal('BroadcastChannel', originalBc)
      else vi.unstubAllGlobals()
    }
  })
})
