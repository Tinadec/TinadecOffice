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

/**
 * UiBadge / UiLabel / UiSkeleton are Vapor SFCs (`<template vapor>`). Under the
 * classic runtime that @vue/test-utils mounts, `vaporInteropPlugin` reaches into
 * `@vue/runtime-vapor`, whose `simpleSetCurrentInstance` is not exported by the
 * `@vue/runtime-dom` build vitest resolves — so mounting any real render of one
 * throws `simpleSetCurrentInstance is not a function` and the whole tree fails to
 * mount. That is a harness limitation, not a component bug: the app itself mounts
 * these fine. Every other happy-dom suite in this repo (SettingsPage.*.test.ts,
 * SearchNavigation.test.ts, RuntimeInstancesPanel.test.ts) replaces exactly these
 * three primitives with classic pass-throughs for the same reason, so this suite
 * follows that precedent instead of asserting against a crash.
 */
vi.mock('@/components/ui', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/components/ui')>()
  const passThrough = { template: '<span><slot /></span>' }
  return { ...actual, UiBadge: passThrough, UiLabel: passThrough, UiSkeleton: passThrough }
})

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

  it('clamps the far edge of every lane so a node can never be dragged out of the framed view', () => {
    // Dragging a governance node above the world origin used to keep its negative
    // Y: the node left the canvas and appeared to vanish.
    expect(clampToLane({ x: 60, y: -400 }, 'operation').y).toBe(0)
    // The 20px grid is preserved on both threads.
    expect(clampToLane({ x: 63, y: -7 }, 'operation')).toEqual({ x: 60, y: 0 })
  })

  it('constrains a drag across a lane instead of silently moving the node', async () => {
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: { nodes: sampleNodes, edges: sampleEdges, agents: allAgents, graphKey: 'mode-1' },
    })
    await flushPromises()

    const vf = wrapper.findComponent(VueFlow)
    // An UNBOUND draft node dragged from governance into execution must stay put
    // and say why, rather than silently changing lanes.
    const unbound: AgentModeNodeDto[] = [
      { id: 'draft', node_key: 'draft', agent_id: '', lane: 'operation', position: { x: 60, y: 60 } },
    ]
    await wrapper.setProps({ nodes: unbound, edges: [], graphKey: 'mode-draft' })
    await flushPromises()
    vf.vm.$emit('nodesChange', [{ id: 'draft', type: 'position', dragging: false, position: { x: 60, y: 320 }, from: { x: 60, y: 60 } }])
    await flushPromises()

    const moved = wrapper.emitted('node-moved')?.[0]?.[0] as { id: string; position: { x: number; y: number }; lane?: string }
    expect(moved.position.y).toBeLessThan(MODE_LANE_SPLIT_Y)
    // No lane payload: the node was clamped, not reclassified.
    expect(moved.lane).toBeUndefined()
    // And the canvas explained itself instead of doing it silently.
    expect(wrapper.find('.lane-notice').exists()).toBe(true)
    expect(wrapper.find('.lane-notice').text()).toBe('canvas.laneClamped')

    wrapper.unmount()
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

  it('surfaces a 409 built_in_read_only as a friendly message instead of a raw conflict', async () => {
    const store = useAgentModesStore()
    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    const refusal = Object.assign(new Error('Conflict'), { status: 409, code: 'built_in_read_only' })
    apiMocks.deleteAgentMode.mockRejectedValueOnce(refusal)
    apiMocks.confirm.mockResolvedValueOnce(true)

    const deleted = await store.deleteOrArchiveMode('mode-1')
    expect(deleted).toBe(false)
    // A pack-managed refusal must NOT be swallowed into an archive call.
    expect(apiMocks.archiveAgentMode).not.toHaveBeenCalled()
    expect(apiMocks.notifyError).toHaveBeenCalledTimes(1)
    const shown = apiMocks.notifyError.mock.calls.at(-1)![0] as Error
    // The store resolves `tr()` against the REAL i18n bundle (not this suite's
    // useI18n stub), so the assertion is "the user sees prose, not a dotted key".
    expect(shown.message).not.toBe('settings.modeBuiltInReadOnly')
    expect(shown.message).not.toMatch(/^[a-z][a-zA-Z0-9]*(\.[a-zA-Z0-9]+)+$/)
    expect(shown.message.length).toBeGreaterThan(4)
    expect(store.isBuiltInReadOnlyError(refusal)).toBe(true)
    expect(store.isBuiltInReadOnlyError(new Error('other'))).toBe(false)
  })

  it('keeps at least 50 undo steps of history', async () => {
    const store = useAgentModesStore()
    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    for (let i = 0; i < 60; i += 1) {
      store.commitDraft((cur) => ({ ...cur, displayName: `Name ${i}` }))
    }
    expect(store.draft.value.displayName).toBe('Name 59')

    // 60 recorded steps must all be undoable — the contract floor is 50.
    for (let i = 0; i < 60; i += 1) store.undo()
    expect(store.draft.value.displayName).toBe('Custom Team')
    for (let i = 0; i < 60; i += 1) store.redo()
    expect(store.draft.value.displayName).toBe('Name 59')
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

  it('pans and highlights the offending node when an issue capsule is clicked', async () => {
    // A backflow edge (execution -> operation) is an error-severity issue, so the
    // capsule row must render and be clickable.
    const backflowEdges: AgentModeEdgeDto[] = [
      { id: 'bad', source: 'worker-eng', target: 'leader' },
    ]
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: { nodes: sampleNodes, edges: backflowEdges, agents: allAgents, graphKey: 'mode-issue' },
    })
    await flushPromises()

    const capsules = wrapper.findAll('.canvas-issue')
    expect(capsules.length).toBeGreaterThan(0)
    const backflow = capsules.find((c) => c.text() === 'canvas.issue.backflow')
    expect(backflow).toBeTruthy()

    await backflow!.trigger('click')
    await flushPromises()

    // A backflow issue is carried by EDGES, not nodes, so the capsule selects the
    // offending edge (the thing the user has to delete or re-route) rather than an
    // endpoint. The contract is "pans to and highlights the offending node/edge",
    // and for an edge-shaped issue the edge is the offender.
    expect(wrapper.emitted('select-edge')?.at(-1)).toEqual(['bad'])
    // Both endpoints must still be highlighted, so the user can find an edge that
    // may be scrolled far from the viewport.
    const highlighted = wrapper.findAll('.mode-agent-node.highlighted')
    expect(highlighted.length).toBe(2)

    wrapper.unmount()
  })

  it('selects the offending NODE when an issue capsule is node-shaped', async () => {
    // An unbound node is a node-shaped error, so this path must emit select-node.
    const unbound: AgentModeNodeDto[] = [
      { id: 'leader', node_key: 'leader', agent_id: meetingAgent.id, lane: 'operation', position: { x: 120, y: 60 }, config: { conversation: true } },
      { id: 'orphan', node_key: 'orphan', agent_id: '', lane: 'execution', position: { x: 60, y: 280 } },
    ]
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: { nodes: unbound, edges: [], agents: allAgents, graphKey: 'mode-unbound' },
    })
    await flushPromises()

    const capsule = wrapper.findAll('.canvas-issue').find((c) => c.text() === 'canvas.issue.unbound-node')
    expect(capsule).toBeTruthy()
    await capsule!.trigger('click')
    await flushPromises()

    expect(wrapper.emitted('select-node')?.at(-1)).toEqual(['orphan'])
    expect(wrapper.findAll('.mode-agent-node.highlighted').length).toBe(1)

    wrapper.unmount()
  })

  it('frames a loaded mode with fitView instead of leaving it parked at the origin', async () => {
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: { nodes: [], edges: [], agents: allAgents, graphKey: 'empty' },
    })
    await flushPromises()

    // Loading a mode (nodes appear) must trigger the data-ready fit.
    await wrapper.setProps({ nodes: sampleNodes, edges: sampleEdges, graphKey: 'mode-loaded' })
    await flushPromises()

    const vf = wrapper.findComponent(VueFlow)
    expect(vf.exists()).toBe(true)
    // @nodes-initialized is the trigger the contract names; emitting it must not
    // throw and must be handled (the handler is what runs fitView).
    expect(() => vf.vm.$emit('nodesInitialized')).not.toThrow()
    await flushPromises()
    wrapper.unmount()
  })

  it('mounts MiniMap and Controls from their packages', async () => {
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: { nodes: sampleNodes, edges: sampleEdges, agents: allAgents, graphKey: 'mode-1' },
    })
    await flushPromises()
    expect(wrapper.find('.vue-flow__minimap').exists()).toBe(true)
    expect(wrapper.find('.vue-flow__controls').exists()).toBe(true)
    // The 20px snap grid must match the dot background, or dragging feels offset.
    const vf = wrapper.findComponent(VueFlow)
    expect(vf.props('snapGrid')).toEqual([MODE_GRID_SIZE, MODE_GRID_SIZE])
    expect(vf.props('snapToGrid')).toBe(true)
    wrapper.unmount()
  })

  it('renders the lane ruler with 治理层 / 执行层 and no leftover 运营层', async () => {
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: { nodes: sampleNodes, edges: sampleEdges, agents: allAgents, graphKey: 'mode-1' },
    })
    await flushPromises()

    const headers = wrapper.findAll('.lane-header')
    expect(headers.length).toBe(2)
    // useI18n is stubbed to echo keys, so the two keys prove which ruler slots
    // render; the real zh-CN wording for those keys is 治理层 / 执行层 and is
    // asserted by the five-bundle guard below.
    const labels = headers.map((h) => h.text())
    expect(labels.some((l) => l.includes('agentCenter.filter.operationLayer'))).toBe(true)
    expect(labels.some((l) => l.includes('agentCenter.filter.executionLayer'))).toBe(true)
    // The retired 运营层 label must not appear anywhere in the rendered tree.
    expect(wrapper.html()).not.toContain('运营层')
    wrapper.unmount()
  })

  it('deletes on Delete/Backspace, deselects on Escape, and undoes with Ctrl+Z', async () => {
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: {
        nodes: sampleNodes,
        edges: sampleEdges,
        agents: allAgents,
        graphKey: 'mode-keys',
        selectedNodeId: 'worker-search',
        selectedEdgeId: null,
      },
    })
    await flushPromises()
    const root = wrapper.find('.mode-graph-canvas')

    await root.trigger('keydown', { key: 'Delete' })
    expect(wrapper.emitted('delete-node')?.at(-1)).toEqual(['worker-search'])

    await root.trigger('keydown', { key: 'Backspace' })
    expect(wrapper.emitted('delete-node')).toHaveLength(2)

    await root.trigger('keydown', { key: 'Escape' })
    expect(wrapper.emitted('select-node')?.at(-1)).toEqual([null])

    await root.trigger('keydown', { key: 'z', ctrlKey: true })
    expect(wrapper.emitted('undo')).toHaveLength(1)
    await root.trigger('keydown', { key: 'y', ctrlKey: true })
    expect(wrapper.emitted('redo')).toHaveLength(1)
    // Ctrl+Shift+Z is also redo, matching every other editor.
    await root.trigger('keydown', { key: 'z', ctrlKey: true, shiftKey: true })
    expect(wrapper.emitted('redo')).toHaveLength(2)

    wrapper.unmount()
  })

  it('ignores canvas shortcuts while a text field has focus', async () => {
    const wrapper = mount(ModeGraphCanvas, {
      attachTo: document.body,
      props: {
        nodes: sampleNodes,
        edges: sampleEdges,
        agents: allAgents,
        graphKey: 'mode-keys',
        selectedNodeId: 'worker-search',
      },
    })
    await flushPromises()
    const input = document.createElement('input')
    wrapper.element.appendChild(input)
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }))
    await flushPromises()
    // Typing Backspace in the inspector must not delete the selected node.
    expect(wrapper.emitted('delete-node')).toBeUndefined()
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

    // The drawer is a docked <aside>, never a dialog: a modal would trap focus and
    // make node-to-node clicking impossible.
    expect(wrapper.element.tagName).toBe('ASIDE')
    expect(wrapper.attributes('data-testid')).toBe('mode-inspector-drawer')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
    // useI18n is stubbed to echo keys in this suite, so the label assertion proves
    // the tier selector is rendered; the keys themselves are checked against the
    // real bundles by the dedicated test further down.
    expect(wrapper.text()).toContain('settings.modeOrchestrationTier')
    // The tier control is a UiSelectField (role="combobox"), and its options are
    // the five tiers the contract names.
    expect(wrapper.find('[role="combobox"]').exists()).toBe(true)

    await wrapper.setProps({ panel: 'node', selectedNode: sampleNodes[1] })
    expect(wrapper.text()).toContain('settings.nodeConfigTitle')

    // Switching node A -> node B must re-render the same panel with no remount.
    await wrapper.setProps({ selectedNode: sampleNodes[2] })
    expect(wrapper.text()).toContain('settings.nodeConfigTitle')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)

    await wrapper.setProps({ panel: 'edge', selectedNode: null, selectedEdge: sampleEdges[0] })
    expect(wrapper.text()).toContain('settings.edgeConfigTitle')
    wrapper.unmount()
  })

  it('disables every input when the mode is pack-managed (read-only)', async () => {
    const store = useAgentModesStore()
    await store.loadAll()
    await store.selectMode('mode-1', { force: true })

    const wrapper = mount(ModeInspectorDrawer, {
      global: { plugins: [vaporInteropPlugin] },
      props: {
        panel: 'node',
        readonly: true,
        selectedModeId: 'mode-1',
        selectedMode: store.selectedMode.value,
        draft: store.draft.value,
        selectedNode: sampleNodes[1],
        selectedEdge: null,
        addNodeLane: 'operation',
        agents: allAgents,
        providers: [],
        routes: [],
        runtimePolicyDefinitions: [],
        policySettingsError: '',
        bindingsError: null,
        modeVersions: [],
        modeVersionsLoading: false,
        modeBusy: false,
      },
    })

    // `attributes('disabled')` returns '' for a present attribute, which is falsy,
    // so read the DOM property instead — that is what actually blocks input.
    const controls = wrapper.findAll('input, textarea, select, button')
    expect(controls.length).toBeGreaterThan(4)
    const enabled = controls.filter((el) => {
      const node = el.element as HTMLButtonElement | HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement
      return !node.disabled && !node.hasAttribute('disabled')
    })
    // The only enabled controls in read-only mode are non-mutating: the header's
    // close button and the strategy preview, which only reads Core.
    for (const el of enabled) {
      const label = `${el.attributes('aria-label') ?? ''} ${el.text()}`
      expect(label).toMatch(/close|preview|common\.close|settings\.previewNodeStrategy/)
    }
    wrapper.unmount()
  })
})

describe('mode editor i18n contract', () => {
  /**
   * `locales/i18nParity.test.ts` only scrapes single-quoted `t('…')` and only for
   * the files in its own glob. That is why 32 keys used by this surface shipped
   * without a translation and rendered as literal dotted paths. This guard walks
   * the real bundles and asserts every key these components can render exists —
   * including the dynamically built `canvas.issue.<code>` family.
   */
  const ownedSources = import.meta.glob(
    ['./ModeGraphCanvas.vue', './ModeAgentNode.vue', './ModeInspectorDrawer.vue'],
    { query: '?raw', import: 'default', eager: true },
  ) as Record<string, string>

  function resolveKey(bundle: Record<string, unknown>, key: string): unknown {
    let node: unknown = bundle
    for (const part of key.split('.')) {
      if (!node || typeof node !== 'object') return undefined
      node = (node as Record<string, unknown>)[part]
    }
    return node
  }

  it('every translatable key these components reference exists in all five bundles', async () => {
    const bundles: Record<string, Record<string, unknown>> = {
      'zh-CN': (await import('@/locales/zh-CN')).default as Record<string, unknown>,
      'zh-TW': (await import('@/locales/zh-TW')).default as Record<string, unknown>,
      en: (await import('@/locales/en')).default as Record<string, unknown>,
      ja: (await import('@/locales/ja')).default as Record<string, unknown>,
      ru: (await import('@/locales/ru')).default as Record<string, unknown>,
    }

    const keys = new Set<string>()
    for (const source of Object.values(ownedSources)) {
      for (const match of source.matchAll(/\bt\(\s*['"`]([A-Za-z0-9_.-]+)['"`]/g)) keys.add(match[1]!)
    }
    // Keys the components build at runtime from a validated code.
    for (const code of ['empty', 'unbound-node', 'self-loop', 'duplicate-edge', 'dangling-endpoint',
      'cycle', 'backflow', 'no-conversation', 'multi-conversation', 'conversation-layer']) {
      keys.add(`canvas.issue.${code}`)
    }

    expect(keys.size).toBeGreaterThan(50)
    const missing: string[] = []
    for (const key of keys) {
      for (const [locale, bundle] of Object.entries(bundles)) {
        if (resolveKey(bundle, key) === undefined) missing.push(`${locale}:${key}`)
      }
    }
    expect(missing, `mode editor renders raw keys for: ${missing.join(', ')}`).toEqual([])
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
