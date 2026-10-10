// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { computed, defineComponent, Fragment, h, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import { refreshSpotlight, searchSpotlight, type SpotlightHost } from '@/lib/spotlight'
import { setHostAccessStatus } from '@/lib/hostAccess'
import {
  pendingAgentId, pendingConversationId, pendingModeId, pendingProjectId, pendingPromptId,
  pendingWorkspaceFile, pendingSettingsSection, pendingModelProviderId, pendingToolId,
  requestAgent, requestConversation, requestMode, requestProject, requestPrompt, requestTool, requestWorkspaceFile,
} from '@/lib/pageRequests'

const mocks = vi.hoisted(() => ({ home: null as any, code: null as any, listAgents: vi.fn(), error: vi.fn(), push: vi.fn() }))
vi.mock('@/controllers/HomeController', () => ({ get homeController() { return mocks.home } }))
vi.mock('@/controllers/CodeController', () => ({ get codeController() { return mocks.code } }))
vi.mock('vue-router', () => ({ useRouter: () => ({ push: mocks.push }), useRoute: () => ({ query: {}, params: {} }), onBeforeRouteLeave: vi.fn() }))
vi.mock('@/lib/uiEngine', () => ({ useUiePage: () => ({ ready: Promise.resolve() }) }))
vi.mock('@/composables/useHomeEntrance', async () => {
  const { ref } = await import('vue')
  return { useHomeEntrance: () => ({ contentReady: ref(true), phase: ref('ready') }) }
})
vi.mock('@tinadec/ui', async () => ({ UieShell: (await import('vue')).defineComponent({ render: () => null }) }))
vi.mock('@/components/AppHeader.vue', async () => ({ default: (await import('vue')).defineComponent({ render: () => null }) }))
vi.mock('@/components/CommandPaletteButton.vue', async () => ({ default: (await import('vue')).defineComponent({ render: () => null }) }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({
  items: { value: [] }, notify: { error: mocks.error, success: vi.fn(), warning: vi.fn(), info: vi.fn() },
  status: { error: vi.fn() }, banner: { error: vi.fn() }, confirm: vi.fn(async () => true), dismiss: vi.fn(), dismissByKey: vi.fn(),
}) }))
vi.mock('@/components/ui', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>()
  const { defineComponent, h } = await import('vue')
  const text = defineComponent({ setup: (_, { slots }) => () => h('span', null, slots.default?.()) })
  return { ...actual, UiBadge: text, UiLabel: text, UiSkeleton: text }
})
const tool = { id: 'read_file', display_name: 'Read file', source: 'native', domain: 'file', risk: 'low', capabilities: [], requires_approval: false, execute_endpoint: '' }
vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return { ...actual, api: new Proxy({}, { get(_target, key) {
  if (key === 'listAgents') return mocks.listAgents
  if (key === 'getAgent') return vi.fn(async (id: string) => ({ id, description: `Profile ${id}`, system_prompt: '', capabilities: [], tool_scope: [], revision: 1 }))
  if (key === 'listAgentModeTopologies') return vi.fn(async () => [{ id: 'mode-a', display_name: 'First mode' }, { id: 'mode-b', display_name: 'Target mode' }])
  if (key === 'getAgentModeTopology') return vi.fn(async (id: string) => ({ id, nodes: [], edges: [], revision: 1 }))
  if (key === 'listPromptFragments') return actual.api.listPromptFragments
  if (key === 'getHarnessManifest') return vi.fn(async () => ({ tools: [tool], design_notes: [], runtime: 'native', ownership_model: 'core' }))
  if (key === 'searchTools') return actual.api.searchTools
  if (typeof key === 'string' && key.startsWith('list')) return vi.fn(async () => [])
  if (key === 'getToolLayerReadiness' || key === 'getModelReadiness' || key === 'getModelCatalogReadiness' || key === 'getPromptFragmentEffectiveness') return vi.fn(async () => null)
  return vi.fn(async () => ({}))
  } }) }
})

import HomePage from './HomePage.vue'
import CodePage from './CodePage.vue'
import SettingsPage from './SettingsPage.vue'

const projects = [{ id: 'project-a', name: 'First', path: 'C:/first' }, { id: 'project-b', name: 'Target', path: 'C:/target' }]
const fragments = ['prompt-a', 'prompt-b'].map(id => ({
  id, title: id, key: `fragment.${id}`, enabled: false, scope: 'global', category: 'system', content: `Content ${id}`,
  priority: 1, is_builtin: false, created_at: '', updated_at: '',
}))
const wrappers: ReturnType<typeof mount>[] = []
const transition = defineComponent({ inheritAttrs: false, setup: (_, { slots }) => () => h(Fragment, null, slots.default?.()) })
const global = () => ({
  plugins: [createPinia(), createI18n({ legacy: false, locale: 'en', messages: { en } })],
  stubs: {
    PersonalSection: true, AgentTopologyCanvas: true, AgentPacksPanel: true, AgentModeCanvas: true, GovernanceRolesPanel: true,
    CodeViewer: true, CodeEditor: true, SearchPanel: true, FileTreePanel: true, PatchPreview: true,
    Teleport: true, Transition: transition, transition,
  },
})

beforeEach(() => {
  vi.clearAllMocks()
  localStorage.clear()
  refreshSpotlight()
  vi.stubGlobal('fetch', vi.fn(async (url) => new Response(JSON.stringify(String(url).includes('/prompt-fragments') ? fragments : ['read_file', 'read_file_other'].map(id => ({
    id, description: 'Read file', requires_approval: false, input_schema: {}, risk: 'low', mutates_workspace: false,
    retry_safety: 'safe', confirmation_fields: [],
  }))), { headers: { 'content-type': 'application/json' } })))
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { gatewayUrl: () => 'http://127.0.0.1:48730', getAppConfig: vi.fn(async () => ({})), getHostStatus: vi.fn(async () => ({ state: 'ready', managed: true })) } })
  setHostAccessStatus({ state: 'ready', managed: true })
  mocks.listAgents.mockResolvedValue(['agent-a', 'agent-b'].map(id => ({ id, slug: id, display_name: id, layer: 'execution', role: 'worker', enabled: true, source_kind: 'custom', status: 'published', configured_strategy: {} })))
  mocks.home = { projects: ref([]), sessions: ref([]), busy: ref(false), selectedProjectId: ref(null), selectedSessionId: ref(null) }
  mocks.home.start = vi.fn(() => { mocks.home.busy.value = true })
  mocks.home.viewMode = ref('flat')
  mocks.home.setViewMode = vi.fn(mode => { mocks.home.viewMode.value = mode })
  mocks.home.setSelectedProject = vi.fn(id => { mocks.home.selectedProjectId.value = id })
  mocks.home.setSelectedSession = vi.fn(id => { mocks.home.selectedSessionId.value = id })
  mocks.home.refreshProjectsAndSessions = vi.fn(async () => undefined)
  mocks.code = { projects: ref([]), selectedProjectId: ref(null), activeTab: ref(null), activeTabPath: ref(null), openTabs: ref([]), busy: ref(false), showSearchPanel: ref(false), showPatchPanel: ref(false) }
  mocks.code.currentProject = computed(() => mocks.code.projects.value.find((item: any) => item.id === mocks.code.selectedProjectId.value))
  mocks.code.start = vi.fn(() => { mocks.code.busy.value = true })
  mocks.code.setProject = vi.fn(id => { mocks.code.selectedProjectId.value = id; mocks.code.openTabs.value = [] })
  mocks.code.handleFileSelect = vi.fn(path => { mocks.code.openTabs.value = [{ path, mode: 'view' }] })
  mocks.code.loadProjects = vi.fn(async () => undefined)
})
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  for (const target of [pendingAgentId, pendingConversationId, pendingModeId, pendingProjectId, pendingPromptId, pendingWorkspaceFile, pendingSettingsSection, pendingModelProviderId, pendingToolId]) target.value = null
  vi.unstubAllGlobals()
})

describe('search navigation through the shipping pages', () => {
  it('opens a space search result in its own view without rendering it in the flat shell', async () => {
    mocks.home.sessions.value = [{ id: 'space-target', project_id: 'project-a', view_mode: 'space' }]
    requestConversation('space-target')
    const wrapper = mount(HomePage, { global: global() })
    wrappers.push(wrapper)
    await flushPromises()
    expect(mocks.home.setViewMode).toHaveBeenCalledWith('space')
    expect(mocks.home.setSelectedSession).toHaveBeenCalledWith('space-target')
    expect(mocks.push).toHaveBeenCalledWith('/space')
    expect(wrapper.findComponent({ name: 'UieShell' }).exists()).toBe(false)
  })
  it('keeps the requested project after cold initialization and opens a conversation in its owning project', async () => {
    requestProject('project-b')
    wrappers.push(mount(HomePage, { global: global() }))
    await flushPromises()
    expect(mocks.home.setSelectedProject).not.toHaveBeenCalled()
    mocks.home.projects.value = projects
    mocks.home.selectedProjectId.value = 'project-a'
    mocks.home.sessions.value = [{ id: 'session-b', project_id: 'project-b' }, { id: 'free-session' }]
    mocks.home.busy.value = false
    await flushPromises()
    expect(mocks.home.selectedProjectId.value).toBe('project-b')
    requestConversation('free-session')
    await flushPromises()
    expect(mocks.home.selectedProjectId.value).toBe(null)
    expect(mocks.home.selectedSessionId.value).toBe('free-session')
    expect(pendingConversationId.value).toBe(null)
    expect(mocks.error).not.toHaveBeenCalled()
  })

  it('waits for the file project, then opens its actual tab and handles another result on the same page', async () => {
    requestWorkspaceFile({ path: 'C:/target/a.ts', projectId: 'project-b' })
    wrappers.push(mount(CodePage, { global: global() }))
    await flushPromises()
    expect(mocks.code.handleFileSelect).not.toHaveBeenCalled()
    mocks.code.projects.value = projects
    mocks.code.selectedProjectId.value = 'project-a'
    mocks.code.busy.value = false
    await flushPromises()
    expect(mocks.code.setProject).toHaveBeenCalledWith('project-b')
    expect(mocks.code.handleFileSelect).toHaveBeenCalledWith('C:/target/a.ts')
    requestWorkspaceFile({ path: 'C:/first/b.ts', projectId: 'project-a' })
    await flushPromises()
    expect(mocks.code.selectedProjectId.value).toBe('project-a')
    expect(mocks.code.openTabs.value[0].path).toBe('C:/first/b.ts')
    requestWorkspaceFile({ path: 'gone.ts', projectId: 'removed-project' })
    await flushPromises()
    expect(mocks.error).toHaveBeenCalledOnce()
    expect(mocks.code.loadProjects).toHaveBeenCalledOnce()
    expect(mocks.code.handleFileSelect).toHaveBeenCalledTimes(2)
  })

  it('refreshes a stale roster once and keeps a newer navigation request ahead of the old read', async () => {
    wrappers.push(mount(HomePage, { global: global() }))
    mocks.home.projects.value = projects
    mocks.home.busy.value = false
    await flushPromises()
    let complete!: () => void
    mocks.home.refreshProjectsAndSessions.mockImplementationOnce(() => new Promise<void>(resolve => {
      complete = () => { mocks.home.projects.value = [...projects, { id: 'project-c' }, { id: 'project-d' }]; resolve() }
    }))
    requestProject('project-c')
    await flushPromises()
    expect(mocks.home.refreshProjectsAndSessions).toHaveBeenCalledOnce()
    requestProject('project-d')
    await flushPromises()
    complete()
    await flushPromises()
    expect(mocks.home.setSelectedProject).toHaveBeenCalledWith('project-d')
    expect(mocks.home.setSelectedProject).not.toHaveBeenCalledWith('project-c')
    expect(mocks.error).not.toHaveBeenCalled()
    requestProject('deleted-project')
    await flushPromises()
    expect(mocks.home.refreshProjectsAndSessions).toHaveBeenCalledTimes(2)
    expect(mocks.error).toHaveBeenCalledOnce()
    await flushPromises()
    expect(mocks.home.refreshProjectsAndSessions).toHaveBeenCalledTimes(2)
  })

  it('selects the exact agent, mode, prompt and tool in their real settings surfaces', async () => {
    let complete!: (agents: unknown[]) => void
    mocks.listAgents.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
    requestAgent('agent-b')
    const wrapper = mount(SettingsPage, { global: global() })
    wrappers.push(wrapper)
    await flushPromises()
    complete(['agent-a', 'agent-b'].map(id => ({ id, slug: id, display_name: id, layer: 'execution', role: 'worker', enabled: true, source_kind: 'custom', status: 'published', configured_strategy: {} })))
    await flushPromises()
    expect(wrapper.get('.agent-card.active').text()).toContain('agent-b')
    expect(wrapper.get('.agent-detail-panel').text()).toContain('agent-b')
    requestMode('mode-b')
    await flushPromises()
    expect(wrapper.get('.agent-modes-panel [aria-selected="true"]').text()).toContain('Target mode')
    requestPrompt('prompt-b')
    await flushPromises()
    expect(wrapper.get('.pe-fragment-card.active').text()).toContain('prompt-b')
    expect((wrapper.get('.pe-content-textarea').element as HTMLTextAreaElement).value).toBe('Content prompt-b')
    requestTool('read_file')
    // The Tools workspace loads its overview lazily after the host section mounts.
    await vi.waitFor(async () => {
      await flushPromises()
      expect(wrapper.findAll('.tool-discovery-card')).toHaveLength(1)
    }, { timeout: 5000 })
    expect(wrapper.get('.tool-discovery-card').text()).toContain('read_file')
    expect(pendingToolId.value).toBe(null)
  })

  it('opens a fragment returned by search in the actual Settings fragment detail', async () => {
    const host: SpotlightHost = {
      navigate: vi.fn(), navigateSettings: vi.fn(), openSession: vi.fn(), openProject: vi.fn(), openAgent: vi.fn(),
      openMode: vi.fn(), openPrompt: requestPrompt, openTool: vi.fn(), selectProvider: vi.fn(), openWorkspacePath: vi.fn(),
      loadedSessions: () => [], workspaceRoot: () => '',
    }
    const results = await searchSpotlight('Content prompt-b', host, key => key, [])
    const item = results.find(group => group.kind === 'prompt')?.items[0]
    expect(item?.label).toBe('prompt-b')
    item!.action()
    const wrapper = mount(SettingsPage, { global: global() })
    wrappers.push(wrapper)
    await flushPromises()
    expect(wrapper.get('.pe-fragment-card.active').text()).toContain('prompt-b')
    expect((wrapper.get('.pe-content-textarea').element as HTMLTextAreaElement).value).toBe('Content prompt-b')
    expect(mocks.error).not.toHaveBeenCalled()
    const requests = vi.mocked(fetch).mock.calls.map(([url]) => String(url))
    expect(requests.some(url => url.endsWith('/api/v1/prompt-fragments'))).toBe(true)
    expect(requests.some(url => url.includes('/prompt-pipelines'))).toBe(false)
  })

  it('refreshes an already mounted Settings catalog for a search hit and prevents an old refresh from replacing a newer selection', async () => {
    const wrapper = mount(SettingsPage, { global: global() })
    wrappers.push(wrapper)
    await flushPromises()
    const directory = (ids: string[]) => ids.map(id => ({
      id, slug: id, display_name: id, layer: 'execution', role: 'worker', enabled: true, source_kind: 'custom',
      status: 'published', configured_strategy: { kind: 'inherit' }, managed: false, writable: true,
      revision: 1, version: 1, mode_usages: [], effective_previews: {},
    }))
    mocks.listAgents.mockResolvedValueOnce(directory(['agent-a', 'agent-b', 'new-agent-c']))
    const host: SpotlightHost = {
      navigate: vi.fn(), navigateSettings: vi.fn(), openSession: vi.fn(), openProject: vi.fn(), openAgent: requestAgent,
      openMode: vi.fn(), openPrompt: vi.fn(), openTool: vi.fn(), selectProvider: vi.fn(), openWorkspacePath: vi.fn(),
      loadedSessions: () => [], workspaceRoot: () => '',
    }
    const results = await searchSpotlight('new-agent-c', host, key => key, [])
    const hit = results.find(group => group.kind === 'agent')?.items[0]
    expect(hit?.label).toBe('new-agent-c')
    let complete!: (ids: string[]) => void
    mocks.listAgents.mockImplementationOnce(() => new Promise(resolve => {
      complete = ids => resolve(directory(ids))
    }))
    hit!.action()
    await flushPromises()
    complete(['agent-a', 'agent-b', 'new-agent-c'])
    await flushPromises()
    expect(wrapper.get('.agent-card.active').text()).toContain('new-agent-c')
    expect(mocks.listAgents).toHaveBeenCalledTimes(3) // Initial catalog, search catalog, one consumer refresh.
    mocks.listAgents.mockImplementationOnce(() => new Promise(resolve => {
      complete = ids => resolve(directory(ids))
    }))
    requestAgent('new-agent-d')
    await flushPromises()
    requestAgent('agent-b')
    await flushPromises()
    complete(['agent-a', 'agent-b', 'new-agent-c', 'new-agent-d'])
    await flushPromises()
    expect(wrapper.get('.agent-card.active').text()).toContain('agent-b')
    expect(wrapper.get('.agent-detail-panel').text()).toContain('agent-b')
    expect(mocks.listAgents).toHaveBeenCalledTimes(4)
    expect(mocks.error).not.toHaveBeenCalled()
  })
})
