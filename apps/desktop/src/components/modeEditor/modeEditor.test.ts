// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ref, vaporInteropPlugin } from 'vue'
import { VueFlow } from '@vue-flow/core'
import type { AgentDefinitionDto, AgentModeEdgeDto, AgentModeNodeDto, AgentModeTopologyDto } from '@/api'
import {
  clampToLane,
  dagreLayoutMode,
  findFreeDagreSlot,
  MODE_GRID_SIZE,
  MODE_LANE_SPLIT_Y,
  MODE_NODE_HEIGHT,
  MODE_NODE_WIDTH,
} from './modeDagreLayout'
import ModeGraphCanvas from './ModeGraphCanvas.vue'
import ModeInspectorDrawer from './ModeInspectorDrawer.vue'

const apiMocks = vi.hoisted(() => ({
  listAgentModeTopologies: vi.fn(),
  getAgentModeTopology: vi.fn(),
  listAgentDefinitions: vi.fn(),
  listAgentModeRuntimePolicies: vi.fn(),
  listModelProviders: vi.fn(),
  listModelRoutes: vi.fn(),
  getHarnessManifest: vi.fn(),
  getWorkspaceDefaults: vi.fn(),
  updateWorkspaceDefaultsDraft: vi.fn(),
  publishWorkspaceDefaults: vi.fn(),
  createAgentModeDraft: vi.fn(),
  updateAgentModeDraft: vi.fn(),
  publishAgentMode: vi.fn(),
  cloneAgentMode: vi.fn(),
  deleteAgentMode: vi.fn(),
  archiveAgentMode: vi.fn(),
  listAgentModeVersions: vi.fn(),
  previewModelResolution: vi.fn(),
  confirm: vi.fn(async () => true),
  notifySuccess: vi.fn(),
  notifyError: vi.fn(),
}))

vi.mock('vue-i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key, te: () => false, locale: ref('zh-CN') }),
}))

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    api: new Proxy(actual.api, {
      get(target, prop) {
        if (typeof prop === 'string' && prop in apiMocks) {
          return apiMocks[prop as keyof typeof apiMocks]
        }
        return Reflect.get(target, prop)
      },
    }),
  }
})

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: {
      success: apiMocks.notifySuccess,
      error: apiMocks.notifyError,
      warning: vi.fn(),
      info: vi.fn(),
    },
    confirm: apiMocks.confirm,
    status: { error: vi.fn(), warning: vi.fn() },
    dismissByKey: vi.fn(),
  }),
}))

import { setHostAccessStatus } from '@/lib/hostAccess'
import { __resetAgentModesStoreForTests, useAgentModesStore } from '@/composables/useAgentModesStore'
import TeamsTab from '@/settings/agentCenter/TeamsTab.vue'
import AgentModesPanel from '@/settings/sections/AgentModesPanel.vue'

function makeAgent(
  id: string,
  slug: string,
  layer: 'operation' | 'execution',
  tools: string[] = ['read_file'],
  extra: Partial<AgentDefinitionDto> = {},
): AgentDefinitionDto {
  return {
    id,
    slug,
    name: slug,
    display_name: slug,
    layer,
    role: slug,
    agent_type: slug,
    description: `${slug} role`,
    allowed_tools: tools,
    tool_scope: tools,
    capabilities: [],
    model_strategy: { kind: 'inherit' },
    enabled: true,
    status: 'published',
    revision: 1,
    version: 1,
    ...extra,
  }
}

const meetingAgent = makeAgent('a-meeting', 'meeting', 'operation', [], {
  display_name: 'Meeting Agent',
  i18n: { 'zh-CN': { display_name: '会议智能体' }, en: { display_name: 'Meeting Agent' } },
})
const reviewerAgent = makeAgent('a-gov', 'governance_reviewer', 'operation', ['org_read'])
const searchAgent = makeAgent('a-search', 'search', 'execution', ['read_file', 'file_search'])
const codeAgent = makeAgent('a-eng', 'global_engineering', 'execution', ['read_file', 'write_file', 'shell'])
const allAgents = [meetingAgent, reviewerAgent, searchAgent, codeAgent]

const sampleNodes: AgentModeNodeDto[] = [
  {
    id: 'leader',
    node_key: 'leader',
    agent_id: meetingAgent.id,
    agent_definition_id: meetingAgent.id,
    lane: 'operation',
    label: '会议智能体',
    position: { x: 120, y: 60 },
    config: { conversation: true },
  },
  {
    id: 'worker-search',
    node_key: 'worker-search',
    agent_id: searchAgent.id,
    agent_definition_id: searchAgent.id,
    lane: 'execution',
    label: 'Search',
    position: { x: 60, y: 280 },
  },
  {
    id: 'worker-eng',
    node_key: 'worker-eng',
    agent_id: codeAgent.id,
    agent_definition_id: codeAgent.id,
    lane: 'execution',
    label: 'Engineering',
    position: { x: 320, y: 280 },
  },
]

const sampleEdges: AgentModeEdgeDto[] = [
  { id: 'e1', source: 'leader', target: 'worker-search' },
  { id: 'e2', source: 'worker-search', target: 'worker-eng' },
]

function makeModeDetail(overrides: Partial<AgentModeTopologyDto> = {}): AgentModeTopologyDto {
  return {
    id: 'mode-1',
    slug: 'custom-team',
    display_name: 'Custom Team',
    description: 'Team description',
    instructions: 'Follow evidence',
    orchestration_tier: 'deterministic',
    status: 'draft',
    managed: false,
    pack_managed: false,
    revision: 2,
    latest_published_mode_version_id: 'ver-1',
    runtime_policy: { kind: 'structured_investigation', version: 1, features: ['evidence_gate'] },
    canvas_layout: { zoom: 1 },
    nodes: sampleNodes,
    edges: sampleEdges,
    bindings: [
      { node_key: 'leader', tool_switches: { '*': false } },
      { node_key: 'worker-search', envelope: { resources: { read: [''] } } },
      { node_key: 'custom-hook', tool_switches: { read_file: true } },
    ],
    ...overrides,
  }
}

beforeEach(() => {
  vi.clearAllMocks()
  __resetAgentModesStoreForTests()
  setHostAccessStatus({ state: 'ready', managed: true })

  class ResizeObserverStub {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  ;(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub

  apiMocks.listAgentDefinitions.mockResolvedValue(allAgents)
  apiMocks.listAgentModeTopologies.mockResolvedValue([makeModeDetail()])
  apiMocks.getAgentModeTopology.mockResolvedValue(makeModeDetail())
  apiMocks.listAgentModeRuntimePolicies.mockResolvedValue([
    {
      kind: 'structured_investigation',
      version: 1,
      required_core_capability: 'structured_investigation_v1',
      features: ['evidence_gate', 'timeline'],
      required_features: ['evidence_gate'],
      dependencies: [],
    },
  ])
  apiMocks.listModelProviders.mockResolvedValue([])
  apiMocks.listModelRoutes.mockResolvedValue([{ id: 'r1', purpose: 'coding', candidates: [], updated_at: '' }])
  apiMocks.getHarnessManifest.mockResolvedValue({ tools: [{ id: 'read_file' }, { id: 'write_file' }] })
  apiMocks.getWorkspaceDefaults.mockResolvedValue({
    default_agent_id: codeAgent.id,
    default_agent_definition_id: codeAgent.id,
    default_agent_version_id: 'agent-v9',
    default_agent_mode_id: null,
    default_mode_version_id: null,
    default_prompt_pipeline_id: 'prompt-p1',
    default_prompt_version_id: 'prompt-v4',
    revision: 5,
  })
  apiMocks.updateWorkspaceDefaultsDraft.mockResolvedValue({ revision: 6 })
  apiMocks.publishWorkspaceDefaults.mockResolvedValue({ revision: 7 })
  apiMocks.updateAgentModeDraft.mockImplementation(async (id, body) => makeModeDetail({
    id,
    ...body,
    nodes: (body.nodes ?? []).map((n: any) => ({ ...n, id: n.node_key, agent_id: n.agent_definition_id, lane: n.layer })),
    edges: (body.edges ?? []).map((e: any) => ({ ...e, id: e.edge_key, source: e.source_node_key, target: e.target_node_key })),
    revision: 3,
  }))
  apiMocks.cloneAgentMode.mockResolvedValue(makeModeDetail({ id: 'mode-cloned', display_name: 'Custom Team Copy', revision: 1 }))
  apiMocks.deleteAgentMode.mockResolvedValue(undefined)
  apiMocks.listAgentModeVersions.mockResolvedValue([])
})

afterEach(() => {
  document.body.innerHTML = ''
})

describe('modeDagreLayout', () => {
  it('places operation nodes above MODE_LANE_SPLIT_Y and execution nodes below on a 20px grid without overlaps', () => {
    const nodes = [
      { id: 'leader', lane: 'operation' as const },
      { id: 'gov', lane: 'operation' as const },
      { id: 'search', lane: 'execution' as const },
      { id: 'eng', lane: 'execution' as const },
      { id: 'review', lane: 'execution' as const },
    ]
    const edges = [
      { source: 'leader', target: 'search' },
      { source: 'leader', target: 'eng' },
      { source: 'search', target: 'review' },
      { source: 'eng', target: 'review' },
    ]

    const result = dagreLayoutMode(nodes, edges)
    expect(result.backEdges).toEqual([])
    expect(Object.keys(result.positions)).toHaveLength(5)

    for (const n of nodes) {
      const pos = result.positions[n.id]!
      expect(pos.x % MODE_GRID_SIZE).toBe(0)
      expect(pos.y % MODE_GRID_SIZE).toBe(0)
      if (n.lane === 'operation') {
        expect(pos.y + MODE_NODE_HEIGHT).toBeLessThanOrEqual(MODE_LANE_SPLIT_Y)
      } else {
        expect(pos.y).toBeGreaterThanOrEqual(MODE_LANE_SPLIT_Y)
      }
    }

    // Pipeline dependency puts review strictly below search & eng
    expect(result.positions.review!.y).toBeGreaterThan(result.positions.search!.y)
    expect(result.positions.review!.y).toBeGreaterThan(result.positions.eng!.y)

    // No two nodes collide
    const rects = nodes.map((n) => ({ id: n.id, ...result.positions[n.id]! }))
    for (let i = 0; i < rects.length; i += 1) {
      for (let j = i + 1; j < rects.length; j += 1) {
        const a = rects[i]!
        const b = rects[j]!
        const overlapsX = Math.abs(a.x - b.x) < MODE_NODE_WIDTH
        const overlapsY = Math.abs(a.y - b.y) < MODE_NODE_HEIGHT
        expect(overlapsX && overlapsY).toBe(false)
      }
    }
  })

  it('keeps edge-less execution nodes in the execution band and peels cycles deterministically', () => {
    const result = dagreLayoutMode(
      [
        { id: 'op', lane: 'operation' },
        { id: 'a', lane: 'execution' },
        { id: 'b', lane: 'execution' },
      ],
      [
        { id: 'e1', source: 'a', target: 'b' },
        { id: 'e2', source: 'b', target: 'a' },
      ],
    )
    expect(result.backEdges).toHaveLength(1)
    expect(result.positions.op!.y).toBeLessThan(MODE_LANE_SPLIT_Y)
    expect(result.positions.a!.y).toBeGreaterThanOrEqual(MODE_LANE_SPLIT_Y)
    expect(result.positions.b!.y).toBeGreaterThanOrEqual(MODE_LANE_SPLIT_Y)
    expect(result.positions.a).not.toEqual(result.positions.b)
  })

  it('clamps lane drags and finds free collision-free slots', () => {
    const clampedOp = clampToLane({ x: 105, y: 310 }, 'operation')
    expect(clampedOp.x).toBe(100)
    expect(clampedOp.y + MODE_NODE_HEIGHT).toBeLessThanOrEqual(MODE_LANE_SPLIT_Y)

    const clampedEx = clampToLane({ x: 112, y: 40 }, 'execution')
    expect(clampedEx.x).toBe(120)
    expect(clampedEx.y).toBeGreaterThanOrEqual(MODE_LANE_SPLIT_Y)

    const slot = findFreeDagreSlot([{ id: 'n1', lane: 'execution', position: { x: 60, y: 280 } }], 'execution')
    expect(slot.y).toBeGreaterThanOrEqual(MODE_LANE_SPLIT_Y)
    expect(slot).not.toEqual({ x: 60, y: 280 })
  })
})

describe('useAgentModesStore', () => {
  it('loads full AgentDefinitionDto tools so code workers keep write: true in teamDraft', async () => {
    const store = useAgentModesStore()
    apiMocks.getAgentModeTopology.mockResolvedValueOnce(makeModeDetail({
      nodes: [sampleNodes[0]!],
      edges: [],
      bindings: [
        { node_key: 'leader', tool_switches: { '*': false } },
        { agent_ref: 'global_engineering' },
      ],
    }))

    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    expect(store.agents.value.find((a) => a.id === codeAgent.id)?.allowed_tools).toContain('write_file')
    expect(store.teamDraft.value.memberScopes[codeAgent.id]).toEqual({ read: true, write: true })
  })

  it('tracks dirty state across mode and team edits and protects navigation via discardOrConfirm', async () => {
    const store = useAgentModesStore()
    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    expect(store.isDirty.value).toBe(false)
    store.commitDraft((cur) => ({ ...cur, displayName: 'Renamed Mode', orchestrationTier: 'self_dispatch' }))
    expect(store.modeDirty.value).toBe(true)
    expect(store.isDirty.value).toBe(true)

    apiMocks.confirm.mockResolvedValueOnce(false)
    expect(await store.discardOrConfirm()).toBe(false)
    expect(store.draft.value.displayName).toBe('Renamed Mode')

    apiMocks.confirm.mockResolvedValueOnce(true)
    expect(await store.discardOrConfirm()).toBe(true)
    expect(store.isDirty.value).toBe(false)
    expect(store.draft.value.displayName).toBe('Custom Team')
  })

  it('undoes and redoes both graph changes and mode metadata/policy edits', async () => {
    const store = useAgentModesStore()
    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    store.commitDraft((cur) => ({ ...cur, displayName: 'V2 Name' }))
    store.commitDraft((cur) => ({
      ...cur,
      edges: [...cur.edges, { id: 'e3', source: 'leader', target: 'worker-eng' }],
    }))
    expect(store.draft.value.edges).toHaveLength(3)
    expect(store.draft.value.displayName).toBe('V2 Name')

    store.undo()
    expect(store.draft.value.edges).toHaveLength(2)
    expect(store.draft.value.displayName).toBe('V2 Name')

    store.undo()
    expect(store.draft.value.displayName).toBe('Custom Team')

    store.redo()
    expect(store.draft.value.displayName).toBe('V2 Name')
  })

  it('preserves all WorkspaceDefaults fields when setting a default mode', async () => {
    const store = useAgentModesStore()
    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    await store.setAsDefaultMode('mode-1')

    expect(apiMocks.updateWorkspaceDefaultsDraft).toHaveBeenCalledWith(
      {
        default_agent_id: codeAgent.id,
        default_agent_definition_id: codeAgent.id,
        default_agent_version_id: 'agent-v9',
        default_agent_mode_id: 'mode-1',
        default_mode_version_id: 'ver-1',
        default_prompt_pipeline_id: 'prompt-p1',
        default_prompt_version_id: 'prompt-v4',
      },
      5,
    )
    expect(apiMocks.publishWorkspaceDefaults).toHaveBeenCalledWith(6)
  })

  it('clones and deletes custom modes cleanly via API lifecycle methods', async () => {
    const store = useAgentModesStore()
    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    const cloned = await store.cloneMode('mode-1')
    expect(apiMocks.cloneAgentMode).toHaveBeenCalledWith('mode-1', expect.any(Object))
    expect(cloned?.id).toBe('mode-cloned')

    apiMocks.confirm.mockResolvedValueOnce(true)
    const deleted = await store.deleteOrArchiveMode('mode-1')
    expect(apiMocks.deleteAgentMode).toHaveBeenCalledWith('mode-1', '2')
    expect(deleted).toBe(true)
  })
})

describe('ModeGraphCanvas & ModeInspectorDrawer', () => {
  it('renders viewport-fixed swimlanes, crisp 224px nodes without duplicate labels, and validates connections', async () => {
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: {
        nodes: sampleNodes,
        edges: [sampleEdges[0]!],
        agents: allAgents,
        graphKey: 'mode-1',
      },
    })
    await flushPromises()

    expect(wrapper.find('[data-flow-id="agent-mode-editor"]').exists()).toBe(true)
    expect(wrapper.findComponent(VueFlow).props('id')).toBe('agent-mode-editor')
    expect(wrapper.findComponent(VueFlow).props('edgesUpdatable')).toBe(true)
    expect(wrapper.text()).toContain('agentCenter.filter.operationLayer')
    expect(wrapper.text()).toContain('agentCenter.filter.executionLayer')
    // Leader agentName in zh-CN is 会议智能体 and custom label is also 会议智能体 -> no duplicate subtitle
    const leaderCard = wrapper.find('[data-node-id="leader"]')
    expect(leaderCard.exists()).toBe(true)
    expect(leaderCard.find('.node-subtitle').exists()).toBe(false)
    expect(leaderCard.find('.node-crown-badge').exists()).toBe(true)

    // Legal cross-lane connection
    const vm = wrapper.vm as unknown as { connect: (c: { source: string; target: string }) => void }
    vm.connect({ source: 'leader', target: 'worker-eng' })
    expect(wrapper.emitted('connect')?.[0]).toEqual([{ source: 'leader', target: 'worker-eng' }])

    // Illegal backflow blocked
    vm.connect({ source: 'worker-eng', target: 'leader' })
    await flushPromises()
    expect(wrapper.text()).toContain('canvas.issue.backflow')

    // Illegal cycle blocked
    vm.connect({ source: 'worker-search', target: 'worker-eng' })
    await flushPromises()
    expect(wrapper.emitted('connect')).toHaveLength(2)

    wrapper.unmount()
  })

  it('commits arrow-key nodes-change (dragging: false) without jittering on live drag ticks', async () => {
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: {
        nodes: sampleNodes,
        edges: sampleEdges,
        agents: allAgents,
        graphKey: 'mode-1',
      },
    })
    await flushPromises()

    const vf = wrapper.findComponent(VueFlow)
    // Live drag tick (dragging: true) must NOT emit node-moved
    vf.vm.$emit('nodesChange', [{ id: 'worker-eng', type: 'position', dragging: true, position: { x: 340, y: 300 }, from: { x: 320, y: 280 } }])
    expect(wrapper.emitted('node-moved')).toBeUndefined()

    // Arrow-key movement (dragging: false with position) MUST emit snapped node-moved
    vf.vm.$emit('nodesChange', [{ id: 'worker-eng', type: 'position', dragging: false, position: { x: 341, y: 302 }, from: { x: 320, y: 280 } }])
    expect(wrapper.emitted('node-moved')?.[0]).toEqual([{ id: 'worker-eng', position: { x: 340, y: 300 } }])

    // Dragging execution node into operation band clamps it back into execution band
    vf.vm.$emit('nodesChange', [{ id: 'worker-eng', type: 'position', dragging: false, position: { x: 340, y: 40 }, from: { x: 340, y: 300 } }])
    const clampedEmit = wrapper.emitted('node-moved')?.[1]?.[0] as { id: string; position: { x: number; y: number } }
    expect(clampedEmit.position.y).toBeGreaterThanOrEqual(MODE_LANE_SPLIT_Y)

    wrapper.unmount()
  })

  it('switches ModeInspectorDrawer cleanly between overview, node, and edge without blocking modals', async () => {
    const store = useAgentModesStore()
    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    const wrapper = mount(ModeInspectorDrawer, {
      global: { plugins: [vaporInteropPlugin] },
      props: {
        panel: 'overview',
        selectedModeId: 'mode-1',
        selectedMode: store.selectedMode.value,
        draft: store.draft.value,
        selectedNode: null,
        selectedEdge: null,
        addNodeLane: 'operation',
        agents: allAgents,
        providers: [],
        routes: [{ id: 'r1', purpose: 'coding', candidates: [], updated_at: '' }],
        runtimePolicyDefinitions: store.runtimePolicyDefinitions.value,
        policySettingsError: '',
        bindingsError: null,
        modeVersions: [],
        modeVersionsLoading: false,
        modeBusy: false,
      },
    })

    expect(wrapper.text()).toContain('settings.modeOverview')
    expect(wrapper.text()).toContain('settings.modeOrchestrationTier')

    await wrapper.setProps({ panel: 'node', selectedNode: sampleNodes[1] })
    expect(wrapper.text()).toContain('settings.nodeConfigTitle')
    expect(wrapper.text()).toContain('settings.nodeDutyField')

    await wrapper.setProps({ panel: 'edge', selectedNode: null, selectedEdge: sampleEdges[0] })
    expect(wrapper.text()).toContain('settings.edgeConfigTitle')
    wrapper.unmount()
  })
})

describe('TeamsTab & AgentModesPanel integration', () => {
  it('shows + New Team in rail header and blocks Save Draft on complex topologies', async () => {
    const complexDetail = makeModeDetail({
      nodes: [
        sampleNodes[0]!,
        { id: 'gov-2', node_key: 'gov-2', agent_id: reviewerAgent.id, lane: 'operation', position: { x: 360, y: 60 } },
      ],
      edges: [],
    })
    apiMocks.listAgentModeTopologies.mockResolvedValue([complexDetail])
    apiMocks.getAgentModeTopology.mockResolvedValue(complexDetail)

    const wrapper = mount(TeamsTab, {
      global: { plugins: [vaporInteropPlugin] },
      props: { canAccessBackend: true },
    })
    await flushPromises()
    await flushPromises()

    expect(wrapper.find('[data-testid="team-new-btn"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('settings.teamComplexShape')

    const saveBtn = wrapper.findAll('button').find((b) => b.text().includes('settings.teamSaveDraft'))
    expect(saveBtn?.attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })

  it('mounts AgentModesPanel with docked inspector, zero UiSheet overlays, and no GovernanceRolesPanel', async () => {
    const wrapper = mount(AgentModesPanel, {
      attachTo: document.body,
      global: { plugins: [vaporInteropPlugin] },
    })
    await flushPromises()
    await flushPromises()

    expect(wrapper.find('.agent-modes-panel [aria-selected="true"]').text()).toContain('Custom Team')
    expect(wrapper.find('[data-testid="mode-inspector-drawer"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="gov-roles-panel"]').exists()).toBe(false)
    expect(wrapper.findAll('[role="dialog"]')).toHaveLength(0)

    wrapper.unmount()
  })
})
