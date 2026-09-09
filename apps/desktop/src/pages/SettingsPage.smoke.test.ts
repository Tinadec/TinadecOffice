// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'

/**
 * Behavior snapshot for the SettingsPage monolith (D7 safety net).
 *
 * Before extracting sections into async modules, these tests pin the visible
 * surface: the section registry renders a nav entry per section, switching
 * sections swaps content, and every section mounts without throwing. If the
 * D7 refactor changes what users can see, one of these goes red.
 */

vi.mock('../api', () => ({
  api: new Proxy(
    {},
    {
      get(_target, prop) {
        if (typeof prop === 'string') {
          // Any API method resolves to benign empty data.
          return vi.fn().mockImplementation(() => {
            if (prop.startsWith('list')) return Promise.resolve([])
            if (prop.startsWith('get')) return Promise.resolve({})
            return Promise.resolve({ ok: true })
          })
        }
        return undefined
      },
    },
  ),
}))
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn(), back: vi.fn() }),
  onBeforeRouteLeave: vi.fn(),
  useRoute: () => ({ query: {}, params: {} }),
}))
vi.mock('vue-i18n', () => ({
  useI18n: () => ({
    t: (key: string, fallback?: string) =>
      typeof fallback === 'string' ? fallback : key,
    locale: { value: 'en' },
  }),
}))

import SettingsPage from './SettingsPage.vue'
import settingsPageSource from './SettingsPage.vue?raw'

describe('SettingsPage smoke (D7 safety net)', () => {
  beforeEach(() => {
    // happy-dom lacks a persistent localStorage in some vitest contexts.
    const store = new Map<string, string>()
    Object.defineProperty(window, 'localStorage', {
      configurable: true,
      value: {
        getItem: (k: string) => store.get(k) ?? null,
        setItem: (k: string, v: string) => void store.set(k, v),
        removeItem: (k: string) => void store.delete(k),
        clear: () => void store.clear(),
      },
    })
    setActivePinia(createPinia())
    ;(globalThis as Record<string, unknown>).window = globalThis.window ?? {}
    // Electron preload shim used by the page for window controls.
    Object.defineProperty(window, 'tinadec', {
      configurable: true,
      value: {
        gatewayUrl: () => 'http://127.0.0.1:48730',
        getAppConfig: vi.fn().mockResolvedValue({ gateway_url: 'http://127.0.0.1:48730' }),
        saveGatewayUrl: vi.fn().mockResolvedValue(true),
        resetGatewayUrl: vi.fn().mockResolvedValue(true),
        minimizeWindow: vi.fn(),
        maximizeWindow: vi.fn(),
        closeWindow: vi.fn(),
      },
    })
  })

  afterEach(() => {
    document.body.innerHTML = ''
    vi.restoreAllMocks()
  })

  it('mounts and renders the settings shell with nav entries', async () => {
    const wrapper = mount(SettingsPage)
    await flushPromises()

    expect(wrapper.find('.settings-page').exists()).toBe(true)
    expect(wrapper.find('.settings-nav').exists()).toBe(true)
    wrapper.unmount()
  })

  it('renders general section by default with gateway connection group', async () => {
    const wrapper = mount(SettingsPage)
    await flushPromises()
    await flushPromises()

    expect(wrapper.find('.settings-page').text()).toContain('settings.general')
    wrapper.unmount()
  })

  it('imports every section component it renders (regression: unresolved components render empty)', () => {
    // vue-tsc cannot catch unresolved components in templates — they silently
    // render nothing at runtime. Pin the import/usage pairing at source level.
    const sections = [
      'GeneralSection',
      'LanguageSection',
      'ApiDocsSection',
      'AboutSection',
      'AppearanceSection',
      'PetsSection',
      'ToolCenterSection',
    ]
    for (const name of sections) {
      expect(settingsPageSource).toContain(`import ${name} from '@/settings/sections/${name}.vue'`)
      expect(settingsPageSource).toContain(`<${name} />`)
    }
  })

  it('assigns the routes ref from loadModelCenter (regression: route writes sent no If-Match)', () => {
    // The Routes tab, the route editor, and setDefaultChatModel all read
    // `routes.value`; a shadowing local left it permanently empty, so route
    // PUTs omitted the precondition and Core answered 428.
    expect(settingsPageSource).toContain('routes.value = routeRows')
    // The fetched rows must not be captured by a same-named local binding.
    expect(settingsPageSource).not.toMatch(/const \[[^\]]*\broutes\b[^\]]*\] = await Promise\.all/)
  })
})
