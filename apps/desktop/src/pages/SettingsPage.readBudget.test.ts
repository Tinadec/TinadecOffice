// @vitest-environment happy-dom
// Read budget for the Agent Center: opening it must cost one directory read plus the
// definition of the agent actually being configured — not a definition per agent. The
// assertions are upper bounds on transports and on serial definition waves, so a different
// implementation that stays inside the budget still passes.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'

const mocks = vi.hoisted(() => ({
  listAgents: vi.fn(),
  getAgent: vi.fn(),
  getAgentPack: vi.fn(),
  listAgentPacks: vi.fn(),
  getWorkspaceDefaults: vi.fn(),
  resetDirectory: vi.fn(),
}))

vi.mock('../api', () => ({
  api: new Proxy({}, {
    get(_target, property) {
      if (property === 'listAgents') return mocks.listAgents
      if (property === 'getAgent') return mocks.getAgent
      if (property === 'getAgentPack') return mocks.getAgentPack
      if (property === 'listAgentPacks') return mocks.listAgentPacks
      if (property === 'getWorkspaceDefaults') return mocks.getWorkspaceDefaults
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
  UiTextarea: { inheritAttrs: false, template: '<textarea v-bind="$attrs" />' },
  UiBadge: { template: '<span><slot /></span>' },
  UiLabel: { template: '<label><slot /></label>' },
  UiSkeleton: { template: '<span />' },
  UiSwitch: { inheritAttrs: false, template: '<button v-bind="$attrs" />' },
  UiDropdownMenu: { template: '<div><slot /></div>' },
  UiSheet: { props: ['open'], template: '<div v-if="open"><slot /></div>' },
  UiCheckbox: { inheritAttrs: false, template: '<input type="checkbox" v-bind="$attrs" />' },
  UiSelectField: {
    props: ['value', 'modelValue', 'options', 'disabled'],
    emits: ['update:modelValue', 'update:value', 'change'],
    template: '<span class="ui-select" />',
  },
}))

vi.mock('@/agentPacks/graphSeedPackBootstrap', async () => {
  const { ref } = await vi.importActual<typeof import('vue')>('vue')
  return {
    graphSeedPackState: ref({ phase: 'up_to_date', preview: null, active_version: '0.1.0', error: null, checked_at: Date.now() }),
    installOrUpgradeGraphSeedPack: vi.fn(),
    refreshGraphSeedPack: vi.fn(),
  }
})

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn(), back: vi.fn() }),
  onBeforeRouteLeave: vi.fn(),
  useRoute: () => ({ query: {}, params: {} }),
}))

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key, te: () => false, locale: { value: 'en' } }),
}))

import SettingsPage from './SettingsPage.vue'
import { setHostAccessStatus } from '@/lib/hostAccess'

const DIRECTORY_SIZE = 20

function directoryRow(index: number) {
  const id = `agent-${index}`
  return {
    id, slug: `agent_${index}`, display_name: `Agent ${index}`,
    layer: index % 2 === 0 ? 'operation' : 'execution', role: 'worker',
    source_kind: index === 0 ? 'custom' : 'built_in', managed: index !== 0,
    writable: true, enabled: true, status: 'published', revision: 1, version: 1,
    configured_strategy: { kind: 'inherit' },
    mode_usages: [], effective_previews: {}, recent_invocation: null,
    updated_at: '2026-10-18T00:00:00Z',
  }
}

function definition(id: string) {
  return {
    id, slug: id, display_name: `Agent ${id}`, name: `Agent ${id}`,
    layer: 'operation', role: 'worker', description: `Description ${id}`,
    model_route_purpose: 'chat', model_strategy: { kind: 'inherit' },
    tool_scope: ['read_file'], capabilities: ['user.respond'],
    system_prompt: `Prompt ${id}`, enabled: true, status: 'published', version: 1, revision: 1,
    updated_at: '2026-10-18T00:00:00Z',
  }
}

/** Transport counters: how many definitions, in how many serial waves. */
const stats = { definitionCalls: 0, definitionWaves: 0, maxConcurrent: 0, live: 0 }
function resetStats() { stats.definitionCalls = 0; stats.definitionWaves = 0; stats.maxConcurrent = 0; stats.live = 0 }

async function settle(times = 16) { for (let index = 0; index < times; index++) await flushPromises() }

beforeEach(() => {
  setActivePinia(createPinia())
  resetStats()
  mocks.listAgents.mockReset().mockResolvedValue(Array.from({ length: DIRECTORY_SIZE }, (_, index) => directoryRow(index)))
  mocks.getAgent.mockReset().mockImplementation(async (agentId: string) => {
    stats.definitionCalls++
    stats.live++
    if (stats.live === 1) stats.definitionWaves++
    stats.maxConcurrent = Math.max(stats.maxConcurrent, stats.live)
    await new Promise(resolve => setTimeout(resolve, 0))
    stats.live--
    return definition(agentId)
  })
  mocks.getAgentPack.mockReset().mockResolvedValue({ resources: [] })
  mocks.listAgentPacks.mockReset().mockResolvedValue([])
  mocks.getWorkspaceDefaults.mockReset().mockResolvedValue({ default_mode_version_id: null })

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
      minimizeWindow: vi.fn(), maximizeWindow: vi.fn(), closeWindow: vi.fn(),
    },
  })
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false })))
  setHostAccessStatus({ state: 'ready', managed: true })
})

afterEach(() => { document.body.innerHTML = ''; vi.restoreAllMocks(); vi.unstubAllGlobals() })

async function openCenter() {
  const transition = { template: '<slot />' }
  const wrapper = mount(SettingsPage, { global: { stubs: { Transition: transition, transition, GeneralSection: true } } })
  await flushPromises()
  await wrapper.findAll('.settings-nav-item').find(item => item.text().includes('settings.agentCenter'))!.trigger('click')
  await flushPromises()
  // The agent center now opens on teams; this budget is about the agents directory.
  await wrapper.findAll('.ac-subtab').find(item => item.text().includes('settings.agents'))!.trigger('click')
  await flushPromises()
  return wrapper
}

describe('Agent Center read budget', () => {
  it('paints the directory before any definition and reads only the configured agent', async () => {
    const wrapper = await openCenter()
    // The directory is on screen from the directory read alone.
    expect(wrapper.findAll('.agent-card')).toHaveLength(DIRECTORY_SIZE)
    await settle()
    expect(wrapper.findAll('.agent-card')).toHaveLength(DIRECTORY_SIZE)
    // Budget: one definition for the configured agent, one wave, nothing else in flight.
    expect(stats.definitionCalls).toBeLessThanOrEqual(1)
    expect(stats.definitionWaves).toBeLessThanOrEqual(1)
    expect(stats.maxConcurrent).toBeLessThanOrEqual(1)
    expect(wrapper.get('.agent-detail-panel').text()).toContain('Agent 0')
    expect((wrapper.get('textarea.prompt-editor').element as HTMLTextAreaElement).value).toBe('Prompt agent-0')
    wrapper.unmount()
  })

  it('reads a definition on demand when another agent is configured', async () => {
    const wrapper = await openCenter()
    await settle()
    const before = stats.definitionCalls
    // The rail is split by layer, so pick the card by its row text rather than by DOM order.
    const card = wrapper.findAll('.agent-card').find(item => /Agent 1(?!\d)/.test(item.text()))
    await card!.get('.agent-card-select').trigger('click')
    await settle()
    expect(stats.definitionCalls).toBeLessThanOrEqual(before + 1)
    expect(mocks.getAgent).toHaveBeenLastCalledWith('agent-1')
    expect((wrapper.get('textarea.prompt-editor').element as HTMLTextAreaElement).value).toBe('Prompt agent-1')
    wrapper.unmount()
  })
})