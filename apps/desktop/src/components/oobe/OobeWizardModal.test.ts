// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { ref } from 'vue'
import zhCN from '@/locales/zh-CN'
import en from '@/locales/en'

const hoisted = vi.hoisted(() => ({
  listModelProviders: vi.fn(),
  discoverHarnesses: vi.fn(),
  connectHarness: vi.fn(),
  createModelProvider: vi.fn(),
  saveModelProvider: vi.fn(),
  probeModel: vi.fn(),
  listModelRoutes: vi.fn(),
  saveModelRoute: vi.fn(),
  createProject: vi.fn(),
  pickWorkspaceFolders: vi.fn(),
  ensureGraphSeedPack: vi.fn().mockResolvedValue(undefined),
  projects: null as ReturnType<typeof ref> | null,
  currentProject: null as ReturnType<typeof ref> | null,
  setSelectedProject: vi.fn(),
  refreshProjectsAndSessions: vi.fn().mockResolvedValue(undefined),
}))

vi.mock('@/api', () => ({
  api: {
    listModelProviders: hoisted.listModelProviders,
    discoverHarnesses: hoisted.discoverHarnesses,
    connectHarness: hoisted.connectHarness,
    createModelProvider: hoisted.createModelProvider,
    saveModelProvider: hoisted.saveModelProvider,
    probeModel: hoisted.probeModel,
    listModelRoutes: hoisted.listModelRoutes,
    saveModelRoute: hoisted.saveModelRoute,
    createProject: hoisted.createProject,
    getAgentPack: vi.fn(),
  },
}))

vi.mock('@/agentPacks/graphSeedPackBootstrap', () => ({
  ensureGraphSeedPack: hoisted.ensureGraphSeedPack,
}))

vi.mock('@/lib/hostConnection', () => ({
  canPickHostDirectories: () => true,
  isElectronHost: () => false,
  pickWorkspaceFolders: hoisted.pickWorkspaceFolders,
}))

vi.mock('@/controllers/HomeController', () => ({
  homeController: {
    get projects() {
      return hoisted.projects!
    },
    get currentProject() {
      return hoisted.currentProject!
    },
    setSelectedProject: hoisted.setSelectedProject,
    refreshProjectsAndSessions: hoisted.refreshProjectsAndSessions,
  },
}))

import OobeWizardModal from './OobeWizardModal.vue'
import { __resetOobeForTests, openOobeWizard, useOobe } from '@/composables/useOobe'
import { MONOCHROME_ACCENT_KEY, WARM_ACCENT_KEY, useTheme } from '@/composables/useTheme'

function mountWizard() {
  const i18n = createI18n({
    legacy: false,
    locale: 'zh-CN',
    messages: { 'zh-CN': zhCN, en },
  })
  return mount(OobeWizardModal, {
    global: {
      plugins: [i18n],
      stubs: {
        teleport: true,
        transition: false,
        BrandLogo: true,
        TinadecCalligraphy: true,
      },
    },
  })
}

describe('OobeWizardModal — full-screen 3-step stage', () => {
  beforeEach(() => {
    __resetOobeForTests()
    localStorage.clear()
    hoisted.projects = ref([
      {
        id: 'proj-1',
        name: 'TinadecOffice',
        path: 'C:\\git\\agent\\TinadecOffice',
        lifecycle_status: 'active',
      },
    ])
    hoisted.currentProject = ref(null)
    hoisted.listModelProviders.mockResolvedValue([])
    hoisted.discoverHarnesses.mockResolvedValue({ cli_runtimes: [] })
    hoisted.connectHarness.mockResolvedValue({
      id: 'cli-claude-1',
      driver: 'claude-code',
      display_name: 'Claude Code',
      connection_kind: 'acp',
      enabled: true,
    })
    hoisted.createModelProvider.mockResolvedValue({
      id: 'api-openai-1',
      driver: 'openai-compatible',
      display_name: 'OpenAI',
      connection_kind: 'api-key',
      has_api_key: true,
      enabled: true,
    })
    hoisted.probeModel.mockResolvedValue({ id: 'model_probe', status: 'ready' })
    hoisted.listModelRoutes.mockResolvedValue([])
    hoisted.saveModelRoute.mockResolvedValue({})
    hoisted.createProject.mockResolvedValue({
      id: 'proj-new',
      name: 'DemoRepo',
      path: 'C:\\work\\DemoRepo',
      lifecycle_status: 'active',
    })
    hoisted.pickWorkspaceFolders.mockResolvedValue(['C:\\work\\DemoRepo'])
    vi.clearAllMocks()
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('switches visual palette live in Step 1 and progresses smoothly across all 3 steps', async () => {
    const { accentColor, setAccentColor } = useTheme()
    setAccentColor(MONOCHROME_ACCENT_KEY)

    openOobeWizard('welcome')
    const wrapper = mountWizard()
    await flushPromises()

    expect(wrapper.find('[data-testid="oobe-wizard-modal"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="oobe-step-welcome"]').exists()).toBe(true)
    expect(hoisted.ensureGraphSeedPack).toHaveBeenCalled()

    // Switch live to Warm Ink palette
    await wrapper.get('[data-testid="oobe-palette-warm"]').trigger('click')
    expect(accentColor.value).toBe(WARM_ACCENT_KEY)

    // Switch back to Monochrome
    await wrapper.get('[data-testid="oobe-palette-monochrome"]').trigger('click')
    expect(accentColor.value).toBe(MONOCHROME_ACCENT_KEY)

    // Advance Step 1 -> Step 2 -> Step 3 -> Finish
    await wrapper.get('[data-testid="oobe-next-btn"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="oobe-step-provider"]').exists()).toBe(true)

    await wrapper.get('[data-testid="oobe-next-btn"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="oobe-step-workspace"]').exists()).toBe(true)

    // Select an existing workspace in 1 click
    await wrapper.get('[data-testid="oobe-project-proj-1"]').trigger('click')
    expect(hoisted.setSelectedProject).toHaveBeenCalled()
    expect(wrapper.get('[data-testid="oobe-selected-folder"]').text()).toContain('C:\\git\\agent\\TinadecOffice')

    // Finish & enter workspace
    await wrapper.get('[data-testid="oobe-finish-btn"]').trigger('click')
    await flushPromises()
    expect(useOobe().open.value).toBe(false)
    expect(useOobe().state.value.firstRun.completedAt).toBeTruthy()

    wrapper.unmount()
  })

  it('auto-detects local CLI/ACP harnesses in Step 2 and enables one in 1 click, plus supports API key verify', async () => {
    hoisted.discoverHarnesses.mockResolvedValue({
      cli_runtimes: [
        {
          driver: 'claude-code',
          display_name: 'Claude Code',
          binary_path: '/usr/local/bin/claude',
          channels: [{ channel: 'acp', protocol: 'acp', drivable: true }],
        },
      ],
    })

    openOobeWizard('provider')
    const wrapper = mountWizard()
    await flushPromises()

    // Automatically selects CLI/ACP track when drivable local tools exist and no API key is configured
    expect(wrapper.find('[data-testid="oobe-cli-pane"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="oobe-cli-row-claude-code"]').exists()).toBe(true)

    // After connecting, listModelProviders returns the enabled ACP provider
    hoisted.listModelProviders.mockResolvedValueOnce([
      {
        id: 'cli-claude-1',
        driver: 'claude-code',
        display_name: 'Claude Code',
        connection_kind: 'acp',
        has_api_key: false,
        enabled: true,
      },
    ])

    await wrapper.get('[data-testid="oobe-connect-cli-claude-code"]').trigger('click')
    await flushPromises()

    expect(hoisted.connectHarness).toHaveBeenCalledWith(
      expect.objectContaining({
        driver: 'claude-code',
        binary_path: '/usr/local/bin/claude',
        channel: 'acp',
        protocol: 'acp',
      }),
    )
    expect(hoisted.saveModelRoute).toHaveBeenCalledWith(
      'chat',
      expect.objectContaining({
        candidates: [{ provider_instance_id: 'cli-claude-1', model: null }],
      }),
      undefined,
      undefined,
    )
    expect(wrapper.find('[data-testid="oobe-cli-connected-claude-code"]').exists()).toBe(true)

    // Switch to API provider track & verify API key flow
    await wrapper.get('[data-testid="oobe-track-api"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="oobe-api-pane"]').exists()).toBe(true)

    await wrapper.get('[data-testid="oobe-preset-deepseek"]').trigger('click')
    await wrapper.get('[data-testid="oobe-provider-apikey"]').setValue('sk-test-12345')
    await wrapper.get('[data-testid="oobe-save-verify-btn"]').trigger('click')
    await flushPromises()

    expect(hoisted.createModelProvider).toHaveBeenCalledWith(
      expect.objectContaining({
        driver: 'deepseek',
        api_key: 'sk-test-12345',
      }),
    )
    expect(hoisted.probeModel).toHaveBeenCalledWith(true)
    expect(wrapper.find('[data-testid="oobe-probe-verified"]').exists()).toBe(true)

    wrapper.unmount()
  })

  it('does not close OOBE on Escape while ServerDirectoryPickerModal is open above it', async () => {
    openOobeWizard('workspace')
    const wrapper = mountWizard()
    await flushPromises()

    const fakePicker = document.createElement('div')
    fakePicker.setAttribute('data-testid', 'server-directory-picker-modal')
    document.body.appendChild(fakePicker)

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await flushPromises()
    expect(useOobe().open.value).toBe(true)

    fakePicker.remove()
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await flushPromises()
    expect(useOobe().open.value).toBe(false)

    wrapper.unmount()
  })
})
