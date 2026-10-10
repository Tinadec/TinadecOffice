// @vitest-environment happy-dom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'

const mocks = vi.hoisted(() => ({
  createAgentDraft: vi.fn(),
  listAgents: vi.fn(),
  getAgent: vi.fn(),
  getAgentPack: vi.fn(),
  listAgentPacks: vi.fn(),
  getWorkspaceDefaults: vi.fn(),
  setAgentPackEnabled: vi.fn(),
  adoptAgentPackDefaults: vi.fn(),
  purgeAgentPack: vi.fn(),
  installOrUpgradeGraphSeedPack: vi.fn(),
  refreshGraphSeedPack: vi.fn(),
}))

vi.mock('../api', () => ({
  api: new Proxy({}, {
    get(_target, property) {
      if (property === 'createAgentDraft') return mocks.createAgentDraft
      if (property === 'listAgents') return mocks.listAgents
      if (property === 'getAgent') return mocks.getAgent
      if (property === 'getAgentPack') return mocks.getAgentPack
      if (property === 'listAgentPacks') return mocks.listAgentPacks
      if (property === 'getWorkspaceDefaults') return mocks.getWorkspaceDefaults
      if (property === 'setAgentPackEnabled') return mocks.setAgentPackEnabled
      if (property === 'adoptAgentPackDefaults') return mocks.adoptAgentPackDefaults
      if (property === 'purgeAgentPack') return mocks.purgeAgentPack
      if (property === 'getHarnessManifest') return vi.fn().mockResolvedValue({ tools: [] })
      if (property === 'getToolLayerReadiness'
        || property === 'getModelReadiness'
        || property === 'getModelCatalogReadiness') return vi.fn().mockResolvedValue(null)
      if (typeof property === 'string' && property.startsWith('list')) return vi.fn().mockResolvedValue([])
      if (property === 'searchTools') return vi.fn().mockResolvedValue([])
      return vi.fn().mockResolvedValue({ ok: true })
    },
  }),
}))

vi.mock('@/components/ui', () => ({
  UiButton: { inheritAttrs: false, template: '<button v-bind="$attrs"><slot /></button>' },
  UiCard: { template: '<div><slot /></div>' },
  UiInput: { inheritAttrs: false, template: '<input v-bind="$attrs" />' },
  // SettingsPage lands on the personal section, whose hero renders a UiAvatar and
  // whose profile dialog renders a UiTextarea; a partial mock that omits either
  // makes the mount throw "No export is defined on the mock".
  UiAvatar: { template: '<div><slot /></div>' },
  UiTextarea: { inheritAttrs: false, template: '<textarea v-bind="$attrs" />' },
  UiBadge: { template: '<span><slot /></span>' },
  UiLabel: { template: '<label><slot /></label>' },
  UiSkeleton: { template: '<span />' },
  UiSwitch: { inheritAttrs: false, template: '<button v-bind="$attrs" />' },
  UiDropdownMenu: { template: '<div><slot /></div>' },
}))

vi.mock('@/agentPacks/graphSeedPackBootstrap', async () => {
  const { ref } = await vi.importActual<typeof import('vue')>('vue')
  return {
    graphSeedPackState: ref({
      phase: 'up_to_date',
      preview: null,
      active_version: '0.1.0',
      error: null,
      checked_at: Date.now(),
    }),
    installOrUpgradeGraphSeedPack: mocks.installOrUpgradeGraphSeedPack,
    refreshGraphSeedPack: mocks.refreshGraphSeedPack,
  }
})

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn(), back: vi.fn() }),
  onBeforeRouteLeave: vi.fn(),
  useRoute: () => ({ query: {}, params: {} }),
}))

vi.mock('vue-i18n', () => ({
  useI18n: () => ({
    t: (key: string) => key,
    locale: { value: 'en' },
  }),
}))

import SettingsPage from './SettingsPage.vue'
import { resolveConfirmation, useNotifications } from '@/composables/useNotifications'
import { setHostAccessStatus } from '@/lib/hostAccess'
import { ApiError } from '@/lib/apiError'

const customAgent = {
  id: 'custom-agent-id',
  slug: 'custom_helper',
  display_name: 'Custom Helper',
  layer: 'operation',
  role: 'custom',
  source_kind: 'custom',
  source_key: 'custom_helper',
  managed: false,
  writable: true,
  enabled: true,
  status: 'published',
  revision: 1,
  version: 1,
  configured_strategy: { kind: 'inherit' },
  mode_usages: [],
  effective_previews: {},
  recent_invocation: null,
  updated_at: '2026-08-25T12:00:00Z',
}

const managedAgent = {
  id: 'managed-meeting-id',
  slug: 'meeting',
  display_name: 'Managed Meeting',
  layer: 'operation',
  role: 'session_coordinator',
  source_kind: 'pack',
  source_key: 'tinadec.graph.seed-pack:meeting',
  managed: true,
  // Pack ownership is authoritative even when an older projection reports false.
  writable: false,
  enabled: true,
  status: 'published',
  revision: 1,
  version: 1,
  configured_strategy: { kind: 'inherit' },
  mode_usages: [],
  effective_previews: {},
  recent_invocation: null,
  updated_at: '2026-08-25T12:00:00Z',
}

const customAgentDefinition = {
  id: 'custom-agent-id',
  slug: 'custom_helper',
  display_name: 'Custom Helper',
  name: 'Custom Helper',
  layer: 'operation',
  role: 'custom',
  description: 'Custom agent',
  model_route_purpose: 'chat',
  model_strategy: { kind: 'inherit' },
  tool_scope: [],
  capabilities: [],
  system_prompt: 'Custom prompt',
  enabled: true,
  status: 'published',
  version: 1,
  revision: 1,
  updated_at: '2026-08-25T12:00:00Z',
}

const managedAgentDefinition = {
  id: 'managed-meeting-id',
  slug: 'meeting',
  display_name: 'Managed Meeting',
  name: 'Managed Meeting',
  layer: 'operation',
  role: 'session_coordinator',
  description: 'Managed meeting agent',
  model_route_purpose: 'chat',
  model_strategy: { kind: 'inherit' },
  tool_scope: ['*'],
  capabilities: ['user.respond'],
  system_prompt: 'Managed prompt',
  enabled: true,
  status: 'published',
  version: 1,
  revision: 1,
  updated_at: '2026-08-25T12:00:00Z',
}

beforeEach(() => {
  setActivePinia(createPinia())
  mocks.createAgentDraft.mockReset().mockResolvedValue({ id: 'clone-id' })
  mocks.listAgents.mockReset().mockResolvedValue([customAgent, managedAgent])
  mocks.getAgent.mockReset().mockImplementation((agentId: string) =>
    Promise.resolve(agentId === managedAgent.id ? managedAgentDefinition : customAgentDefinition))
  mocks.getAgentPack.mockReset().mockResolvedValue({
    resources: [{
      kind: 'agent',
      resource_key: 'meeting',
      logical_entity_id: managedAgent.id,
      version_id: 'managed-meeting-version-id',
      content_hash: 'hash',
      disposition: 'reused',
    }],
  })
  mocks.listAgentPacks.mockReset().mockResolvedValue([])
  mocks.getWorkspaceDefaults.mockReset().mockResolvedValue({ default_mode_version_id: null })
  mocks.setAgentPackEnabled.mockReset().mockResolvedValue({ ok: true })
  mocks.adoptAgentPackDefaults.mockReset().mockResolvedValue({ ok: true })
  mocks.purgeAgentPack.mockReset().mockResolvedValue({ pack_id: 'x', revision: 2, deleted: {} })
  mocks.installOrUpgradeGraphSeedPack.mockReset()
  mocks.refreshGraphSeedPack.mockReset()

  const stored = new Map<string, string>()
  const storage = {
    getItem: (key: string) => stored.get(key) ?? null,
    setItem: (key: string, value: string) => void stored.set(key, value),
    removeItem: (key: string) => void stored.delete(key),
    clear: () => void stored.clear(),
  }
  Object.defineProperty(window, 'localStorage', { configurable: true, value: storage })
  vi.stubGlobal('localStorage', storage)

  Object.defineProperty(window, 'tinadec', {
    configurable: true,
    value: {
      gatewayUrl: () => 'http://127.0.0.1:48730',
      getHostStatus: vi.fn().mockResolvedValue({ state: 'ready', managed: true }),
      getAppConfig: vi.fn().mockResolvedValue({ gateway_url: 'http://127.0.0.1:48730' }),
      minimizeWindow: vi.fn(),
      maximizeWindow: vi.fn(),
      closeWindow: vi.fn(),
    },
  })
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false })))
  setHostAccessStatus({ state: 'ready', managed: true })
})

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('SettingsPage GraphSeedPack clone flow', () => {
  it('opens a Pack-managed agent and clones it through the existing custom-agent flow', async () => {
    const transitionStub = { template: '<slot />' }
    const wrapper = mount(SettingsPage, {
      global: {
        stubs: {
          Transition: transitionStub,
          transition: transitionStub,
          GeneralSection: true,
        },
      },
    })
    await flushPromises()

    const agentCenterNav = wrapper.findAll('.settings-nav-item')
      .find((item) => item.text().includes('settings.agentCenter'))
    expect(agentCenterNav).toBeDefined()
    await agentCenterNav!.trigger('click')
    await flushPromises()

    if (!wrapper.find('.agent-detail-panel').exists()) {
      throw new Error(`Agent Center did not render: ${wrapper.find('.settings-content').text().slice(0, 500)}`)
    }
    expect(wrapper.find('.agent-detail-panel').text()).toContain('Custom Helper')

    const packCloneButton = wrapper.findAll('[data-testid="graph-seed-pack-status"] button')
      .find((button) => button.text().includes('agentPack.cloneAction'))
    expect(packCloneButton).toBeDefined()
    await packCloneButton!.trigger('click')
    await flushPromises()

    const inspector = wrapper.find('.agent-detail-panel')
    expect(inspector.text()).toContain('Managed Meeting')
    expect(inspector.text()).toContain('settings.builtInCloneHint')

    const cloneButton = inspector.findAll('button')
      .find((button) => button.text().includes('settings.cloneAgent'))
    expect(cloneButton).toBeDefined()
    await cloneButton!.trigger('click')
    await flushPromises()

    expect(mocks.createAgentDraft).toHaveBeenCalledWith(expect.objectContaining({
      display_name: 'Managed Meeting (copy)',
      layer: 'operation',
      role: 'session_coordinator',
      tool_scope: ['*'],
      capabilities: ['user.respond'],
      system_prompt: 'Managed prompt',
      enabled: true,
    }))

    wrapper.unmount()
  })
})

describe('SettingsPage Agent Center read recovery', () => {
  async function openCenter() {
    const transition = { template: '<slot />' }
    const wrapper = mount(SettingsPage, { global: { stubs: { Transition: transition, transition, GeneralSection: true } } })
    await flushPromises()
    await wrapper.findAll('.settings-nav-item').find(item => item.text().includes('settings.agentCenter'))!.trigger('click')
    await flushPromises()
    return wrapper
  }

  it('waits for the host and reads automatically after authentication recovers', async () => {
    setHostAccessStatus({ state: 'checking', managed: true })
    const wrapper = await openCenter()
    expect(mocks.listAgents).not.toHaveBeenCalled()
    expect(wrapper.text()).not.toContain('settings.noAgents')
    setHostAccessStatus({ state: 'ready', managed: true })
    await flushPromises()
    expect(mocks.listAgents).toHaveBeenCalledTimes(1)
    expect(wrapper.get('.agent-detail-panel').text()).toContain('Custom Helper')
    expect(mocks.installOrUpgradeGraphSeedPack).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows a classified read failure instead of an empty directory, then merges retry requests', async () => {
    mocks.listAgents.mockRejectedValueOnce(new ApiError('Offline fixture', 0, {
      code: 'backend_network_unavailable', category: 'retryable', retryable: true, actions: ['retry'], trace_id: 'fixture-read',
    }))
    const wrapper = await openCenter()
    expect(wrapper.get('[data-testid="agent-center-read-failure"]').text()).toContain('backend_network_unavailable')
    expect(wrapper.get('[data-testid="agent-center-read-failure"]').text()).toContain('fixture-read')
    expect(wrapper.text()).not.toContain('settings.noAgents')
    const retry = wrapper.get('[data-testid="agent-center-read-failure"] button')
    await Promise.all([retry.trigger('click'), retry.trigger('click')])
    await flushPromises()
    expect(mocks.listAgents).toHaveBeenCalledTimes(2)
    expect(wrapper.find('[data-testid="agent-center-read-failure"]').exists()).toBe(false)
    expect(wrapper.findAll('.agent-card')).toHaveLength(2)
    expect(mocks.installOrUpgradeGraphSeedPack).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows an empty directory only after a successful empty read', async () => {
    mocks.listAgents.mockResolvedValueOnce([])
    const wrapper = await openCenter()
    expect(wrapper.find('[data-testid="agent-center-read-failure"]').exists()).toBe(false)
    expect(wrapper.findAll('.agent-card')).toHaveLength(0)
    expect(wrapper.get('.center-empty-state-prominent').text()).toContain('settings.noAgents')
    wrapper.unmount()
  })

  it('keeps the successful directory when a later refresh fails', async () => {
    const wrapper = await openCenter()
    mocks.listAgents.mockRejectedValueOnce(new Error('Refresh offline'))
    await wrapper.get('.agent-center-merged > .center-command-bar button').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('.agent-card')).toHaveLength(2)
    expect(wrapper.get('[data-testid="agent-center-read-failure"]').text()).toContain('Refresh offline')
    expect(wrapper.text()).not.toContain('settings.noAgents')
    wrapper.unmount()
  })

  it('keeps a row with failed details and blocks editing an invented empty definition', async () => {
    mocks.getAgent.mockImplementation(async id => {
      if (id === customAgent.id) throw new Error('Definition offline')
      return managedAgentDefinition
    })
    const wrapper = await openCenter()
    expect(wrapper.findAll('.agent-card')).toHaveLength(2)
    expect(wrapper.get('[data-testid="agent-definition-read-failure"]').text()).toContain('Definition offline')
    expect(wrapper.find('.agent-detail-panel').exists()).toBe(false)
    expect(wrapper.get('[data-testid="agent-center-partial-failure"]').text()).toContain('Custom Helper')
    mocks.getAgent.mockImplementation(async id => id === customAgent.id ? customAgentDefinition : managedAgentDefinition)
    await wrapper.get('[data-testid="agent-definition-read-failure"] button').trigger('click')
    await flushPromises()
    expect(wrapper.get('.agent-detail-panel').text()).toContain('Custom Helper')
    expect(wrapper.find('[data-testid="agent-definition-read-failure"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('preserves an unsaved prompt through reconnection and read refresh', async () => {
    const wrapper = await openCenter()
    await wrapper.get('textarea.prompt-editor').setValue('Unsaved user draft')
    setHostAccessStatus({ state: 'unavailable', managed: true })
    setHostAccessStatus({ state: 'ready', managed: true })
    await flushPromises()
    expect(mocks.listAgents).toHaveBeenCalledTimes(2)
    expect((wrapper.get('textarea.prompt-editor').element as HTMLTextAreaElement).value).toBe('Unsaved user draft')
    expect(mocks.createAgentDraft).not.toHaveBeenCalled()
    expect(mocks.installOrUpgradeGraphSeedPack).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('ignores the cancelled response from the previous connection', async () => {
    let finish!: (directory: typeof customAgent[]) => void
    mocks.listAgents.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const wrapper = await openCenter()
    const oldSignal = mocks.listAgents.mock.calls[0]![0].signal as AbortSignal
    setHostAccessStatus({ state: 'unavailable', managed: true })
    expect(oldSignal.aborted).toBe(true)
    setHostAccessStatus({ state: 'ready', managed: true })
    await flushPromises()
    expect(wrapper.findAll('.agent-card')).toHaveLength(2)
    finish([])
    await flushPromises()
    expect(wrapper.findAll('.agent-card')).toHaveLength(2)
    expect(wrapper.text()).not.toContain('settings.noAgents')
    wrapper.unmount()
  })

  it('does not replace a dirty editor when its agent temporarily leaves the directory', async () => {
    const wrapper = await openCenter()
    await wrapper.get('textarea.prompt-editor').setValue('Unsaved user draft')
    mocks.listAgents.mockResolvedValueOnce([managedAgent])
    setHostAccessStatus({ state: 'unavailable', managed: true })
    setHostAccessStatus({ state: 'ready', managed: true })
    await flushPromises()
    expect(wrapper.find('[data-testid="agent-definition-read-failure"]').exists()).toBe(true)
    expect(wrapper.find('.agent-detail-panel').exists()).toBe(false)
    // When the same identity reappears, recovery must show the retained draft.
    await wrapper.get('[data-testid="agent-definition-read-failure"] button').trigger('click')
    await flushPromises()
    expect((wrapper.get('textarea.prompt-editor').element as HTMLTextAreaElement).value).toBe('Unsaved user draft')
    expect(mocks.createAgentDraft).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})

describe('SettingsPage installed Agent Pack inventory', () => {
  async function mountAgentCenter() {
    const transitionStub = { template: '<slot />' }
    const wrapper = mount(SettingsPage, {
      global: {
        stubs: {
          Transition: transitionStub,
          transition: transitionStub,
          GeneralSection: true,
        },
      },
    })
    await flushPromises()
    const agentCenterNav = wrapper.findAll('.settings-nav-item')
      .find((item) => item.text().includes('settings.agentCenter'))
    expect(agentCenterNav).toBeDefined()
    await agentCenterNav!.trigger('click')
    await flushPromises()
    return wrapper
  }

  it('lists every installed pack with enable, uninstall and set-default controls', async () => {
    mocks.listAgentPacks.mockResolvedValue([
      {
        pack_id: 'tinadec.graph.seed-pack',
        owner: 'tinadec',
        name: 'GraphSeedPack',
        status: 'active',
        active_version: '2.1.0',
        integrity_digest: 'b'.repeat(64),
        revision: 3,
        installed_at: '2026-09-13T12:00:00Z',
        updated_at: '2026-09-13T12:00:00Z',
        default_mode_version_id: 'mv-active',
      },
      {
        pack_id: 'acme.review.seed-pack',
        owner: 'acme',
        name: 'AcmePack',
        status: 'disabled',
        active_version: '1.0.0',
        integrity_digest: 'c'.repeat(64),
        revision: 1,
        installed_at: '2026-09-13T12:00:00Z',
        updated_at: '2026-09-13T12:00:00Z',
        default_mode_version_id: null,
      },
    ])
    mocks.getWorkspaceDefaults.mockResolvedValue({ default_mode_version_id: 'mv-active' })

    const wrapper = await mountAgentCenter()

    const inventory = wrapper.find('[data-testid="installed-agent-packs"]')
    expect(inventory.text()).toContain('tinadec.graph.seed-pack')
    expect(inventory.text()).toContain('acme.review.seed-pack')
    expect(inventory.text()).not.toContain('agentPack.installedEmpty')
    // 工作区默认由 graph 包提供：它带「工作区默认」徽章且没有「设为默认」按钮；
    // 被禁用的包显示禁用徽章。
    expect(inventory.text()).toContain('agentPack.activeBadge')
    expect(inventory.text()).toContain('agentPack.disabledBadge')
    expect(wrapper.find('[data-testid="agent-pack-adopt-tinadec.graph.seed-pack"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="agent-pack-adopt-acme.review.seed-pack"]').exists()).toBe(true)

    await wrapper.find('[data-testid="agent-pack-toggle-acme.review.seed-pack"]').trigger('click')
    await flushPromises()
    // 被禁用的包点击后请求「启用」。
    expect(mocks.setAgentPackEnabled).toHaveBeenCalledWith('acme.review.seed-pack', true)

    await wrapper.find('[data-testid="agent-pack-adopt-acme.review.seed-pack"]').trigger('click')
    await flushPromises()
    expect(mocks.adoptAgentPackDefaults).toHaveBeenCalledWith('acme.review.seed-pack')

    wrapper.unmount()
  })

  it('uninstall asks for confirmation and only purges after it is granted', async () => {
    mocks.listAgentPacks.mockResolvedValue([
      {
        pack_id: 'tinadec.graph.seed-pack',
        owner: 'tinadec',
        name: 'GraphSeedPack',
        status: 'active',
        active_version: '2.1.0',
        integrity_digest: 'b'.repeat(64),
        revision: 3,
        installed_at: '2026-09-13T12:00:00Z',
        updated_at: '2026-09-13T12:00:00Z',
        default_mode_version_id: null,
      },
    ])

    const wrapper = await mountAgentCenter()
    await wrapper.find('[data-testid="agent-pack-uninstall-tinadec.graph.seed-pack"]').trigger('click')
    await flushPromises()

    // 破坏性操作必须先弹确认：确认前一个字节都不删，且文案明确点出"运行历史一起删"。
    const pending = useNotifications().currentConfirmation.value
    expect(pending).not.toBeNull()
    expect(pending!.title).toContain('agentPack.uninstallTitle')
    expect(pending!.details).toContain('agentPack.uninstallDetails')
    expect(mocks.purgeAgentPack).not.toHaveBeenCalled()

    resolveConfirmation(useNotifications().currentConfirmation.value!.id, true)
    await flushPromises()
    // revision 作为 If-Match 守卫一起发出，避免删掉已经变过的包。
    expect(mocks.purgeAgentPack).toHaveBeenCalledWith('tinadec.graph.seed-pack', 3)

    wrapper.unmount()
  })

  it('shows a listing failure rather than an empty inventory when the route rejects', async () => {
    mocks.listAgentPacks.mockRejectedValue(new Error('404 not found'))

    const wrapper = await mountAgentCenter()

    const inventory = wrapper.find('[data-testid="installed-agent-packs"]')
    expect(inventory.exists()).toBe(true)
    expect(inventory.text()).not.toContain('agentPack.installedEmpty')
    expect(wrapper.text()).toContain('404 not found')
    expect(wrapper.find('[data-testid="retired-pack-notice"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="graph-seed-pack-status"]').exists()).toBe(true)

    wrapper.unmount()
  })
})
