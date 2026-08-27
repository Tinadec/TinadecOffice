// @vitest-environment happy-dom

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'

const mocks = vi.hoisted(() => ({
  createAgentDraft: vi.fn(),
  listAgents: vi.fn(),
  getAgent: vi.fn(),
  getAgentPack: vi.fn(),
}))

vi.mock('../api', () => ({
  api: new Proxy({}, {
    get(_target, property) {
      if (property === 'createAgentDraft') return mocks.createAgentDraft
      if (property === 'listAgents') return mocks.listAgents
      if (property === 'getAgent') return mocks.getAgent
      if (property === 'getAgentPack') return mocks.getAgentPack
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
  UiBadge: { template: '<span><slot /></span>' },
  UiLabel: { template: '<label><slot /></label>' },
  UiSkeleton: { template: '<span />' },
  UiSwitch: { inheritAttrs: false, template: '<button v-bind="$attrs" />' },
  UiDropdownMenu: { template: '<div><slot /></div>' },
}))

vi.mock('@/agentPacks/officeAgentPackBootstrap', async () => {
  const { ref } = await vi.importActual<typeof import('vue')>('vue')
  return {
    officeAgentPackState: ref({
      phase: 'up_to_date',
      preview: null,
      active_version: '0.1.0',
      error: null,
      checked_at: Date.now(),
    }),
    installOrUpgradeOfficeAgentPack: vi.fn(),
    refreshOfficeAgentPack: vi.fn(),
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
  source_key: 'tinadec.office.agent-pack:meeting',
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
      getAppConfig: vi.fn().mockResolvedValue({ gateway_url: 'http://127.0.0.1:48730' }),
      minimizeWindow: vi.fn(),
      maximizeWindow: vi.fn(),
      closeWindow: vi.fn(),
    },
  })
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false })))
})

afterEach(() => {
  document.body.innerHTML = ''
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe('SettingsPage OfficeAgentPack clone flow', () => {
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

    const packCloneButton = wrapper.findAll('[data-testid="office-agent-pack-status"] button')
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
