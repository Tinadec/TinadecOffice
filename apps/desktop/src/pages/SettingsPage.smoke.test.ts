// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
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
vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({
    t: (key: string, fallback?: string) =>
      typeof fallback === 'string' ? fallback : key,
    te: () => false,
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

  it('opens on the personal section, with general still one click away in the nav', async () => {
    const wrapper = mount(SettingsPage)
    await flushPromises()
    await flushPromises()

    const text = wrapper.find('.settings-page').text()
    expect(text).toContain('settings.personal')
    expect(text).toContain('settings.general')
    // Personal is the first nav entry and the active one on a fresh mount.
    const navItems = wrapper.findAll('.settings-nav-item')
    expect(navItems[0].text()).toContain('settings.personal')
    expect(navItems[0].classes()).toContain('active')
    wrapper.unmount()
  })

  it('imports every section component it renders (regression: unresolved components render empty)', () => {
    // vue-tsc cannot catch unresolved components in templates — they silently
    // render nothing at runtime. Pin the import/usage pairing at source level.
    const sections = [
      'PersonalSection',
      'GeneralSection',
      'ApiDocsSection',
      'AboutSection',
      'AppearanceSection',
      'PetsSection',
      'ToolCenterSection',
      // The Agent Center panels were extracted rather than written here, so they
      // are exactly as easy to orphan as a section.
      'AgentModesPanel',
      'PromptEngineeringMerged',
      'RuntimeInstancesPanel',
      'AgentPacksPanel',
    ]
    for (const name of sections) {
      expect(settingsPageSource).toContain(`import ${name} from '@/settings/sections/${name}.vue'`)
      // `<Name` rather than `<Name />`: a panel that declares props never closes its tag.
      expect(settingsPageSource).toContain(`<${name}`)
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

  it('gives each harness channel a section, and states what this build can do there', async () => {
    // Before the channel split the section list was `'CLI'` / `'ACP'` literals with no terminal
    // section, so a provider stored on `tui` had nowhere to appear and the channel names shipped
    // untranslated.
    //
    // Pinned against the component source, not a mounted wrapper: the whole settings stage sits
    // inside `<Transition mode="out-in">`, and the transition stub @vue/test-utils installs by
    // default does not forward its slot, so every pane renders as an empty DOM here — a wrapper
    // assertion would pass against nothing. Overriding the stub is not available either: this repo's
    // tests use Vue's runtime-only build, so a `{ template }` stub cannot compile and the mount throws.
    const sections = settingsPageSource.match(
      /const modelCenterSections = computed\(\(\) => \[([\s\S]*?)\n\]\)/,
    )?.[1]
    expect(sections, 'modelCenterSections declaration').toBeTruthy()

    // One tab per channel, each labelled through the locale bundle and counted from its own bucket,
    // so a provider stored on `tui` cannot silently fall into the CLI count.
    for (const [key, label, bucket] of [
      ['cli', 'settings.centerCli', 'cli_runtimes'],
      ['tui', 'settings.centerTui', 'tui_runtimes'],
      ['acp', 'settings.centerAcp', 'acp_runtimes'],
    ]) {
      expect(sections, `section '${key}' entry`).toContain(`key: '${key}' as const`)
      expect(sections, `label of '${key}'`).toContain(`t('${label}')`)
      expect(sections, `count of '${key}'`).toContain(bucket)
    }
    // A literal 'CLI'/'ACP' tab would satisfy the loop above by accident, so pin that it is gone.
    expect(settingsPageSource).not.toMatch(/label: '(CLI|ACP)'/)

    const pane = (key: string) => {
      const start = settingsPageSource.indexOf(`v-if="modelCenterSection === '${key}'"`)
      expect(start, `pane for '${key}'`).toBeGreaterThan(-1)
      const next = settingsPageSource.indexOf('<section v-if="modelCenterSection ===', start + 1)
      return settingsPageSource.slice(start, next === -1 ? settingsPageSource.length : next)
    }

    // The ACP pane states the permission consequence before the user connects, because Core answers
    // every `session/request_permission` by refusing it — see ControlPlaneService's ACP branch.
    expect(pane('acp')).toContain('acp-permission-notice')
    expect(pane('acp')).toContain(`t('settings.acpPermissionWarning')`)

    // The terminal channel is an inventory in this build. A start affordance here would be a control
    // that cannot work, which is the failure class the read-only notice exists to prevent.
    expect(pane('tui')).toContain('acp-permission-notice')
    expect(pane('tui')).toContain(`t('settings.tuiBackendUnavailable')`)
    expect(pane('tui')).not.toMatch(/@click="(start|run|connect)Tui/)

    // The shell still mounts, so a syntax error introduced by this work fails here as well.
    const wrapper = mount(SettingsPage)
    await flushPromises()
    await flushPromises()
    const modelNav = wrapper.findAll('.settings-nav-item').find((item) => item.text().includes('settings.model'))
    expect(modelNav, 'model center nav entry').toBeDefined()
    wrapper.unmount()
  })

  it('keeps the channel copy in both locale bundles', () => {
    // SettingsPage is not registered in i18nParity's panelSources, so a key added to the page and not
    // to one bundle would render as a dotted string in exactly one language.
    const keys = [
      'centerCli', 'centerTui', 'centerAcp',
      'channelCli', 'channelTui', 'channelAcp',
      'quickConnectChannel', 'channelNotDrivable', 'channelNotDrivableDefault',
      'acpPermissionWarning', 'tuiRuntimeHint', 'noTuiRuntimes', 'tuiBackendUnavailable',
      'harnessProviderInstance', 'protocolUnknown',
      'protocolAcp', 'protocolOpencodeServe', 'protocolHeadlessCli', 'protocolTui',
    ]
    // `process.cwd()` rather than `import.meta.url`: this module also imports a `?raw` sibling, and the
    // id Vitest gives it then resolves relative to the drive root instead of the package.
    for (const locale of ['en', 'zh-CN']) {
      const source = readFileSync(resolve(process.cwd(), 'src', 'locales', `${locale}.ts`), 'utf8')
      const missing = keys.filter((key) => !new RegExp(`^\\s{4}${key}:`, 'm').test(source))
      expect(missing, `settings.* keys missing from ${locale}`).toEqual([])
    }
  })

})
