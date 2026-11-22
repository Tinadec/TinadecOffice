// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { VueFlow } from '@vue-flow/core'
import en from '../locales/en'
import zhCN from '../locales/zh-CN'
import OrchestrationTab from './OrchestrationTab.vue'
import ToolExecutionTimeline from './tools/ToolExecutionTimeline.vue'
import { layoutRuntimeTopology, buildRuntimeTopology } from './OrchestrationMiniGraph.vue'
import type {
  ContextPackDto,
  ModelInvocationDto,
  OrchestrationSnapshotDto,
  ToolExecutionTimelineItemDto,
  TopologyRunDto,
} from '../api'

/**
 * The tab reads one thing itself: the run's model-invocation audit (the sibling tool panels fetch
 * too, which is this subtree's established idiom). Everything else stays props, so the stubs below
 * keep the store-dependent children out of these cases.
 */
const listModelInvocations = vi.fn()
vi.mock('../api', () => ({
  api: { listModelInvocations: (...args: unknown[]) => listModelInvocations(...args) },
}))

function invocation(over: Partial<ModelInvocationDto> = {}): ModelInvocationDto {
  return {
    id: 'inv-1',
    call_id: 'call-1',
    attempt: 1,
    session_id: 'session-1',
    run_id: 'run-1',
    agent_definition_id: 'agent-1',
    agent_version_id: 'version-1',
    mode_version_id: 'mode-1',
    strategy_source: 'route',
    provider_instance_id: 'prov-1',
    provider_version_id: 'provver-1',
    model: 'gpt-test',
    protocol: 'openai',
    fallback_position: 0,
    status: 'succeeded',
    started_at: '2026-09-22T00:00:00Z',
    input_tokens: 100,
    output_tokens: 40,
    total_tokens: 140,
    ...over,
  }
}

function emptyUsagePage() {
  return { items: [] as ModelInvocationDto[] }
}

afterEach(() => {
  listModelInvocations.mockReset()
  listModelInvocations.mockResolvedValue(emptyUsagePage())
})

/**
 * The child panels are not what these cases are about, and two of them reach for stores.
 */
const stubs = {
  DeclaredGraphCanvas: true,
  ToolExecutionTimeline: true,
  ToolCatalogBrowser: true,
  ToolStatsDashboard: true,
}

function pack(over: Partial<ContextPackDto> = {}): ContextPackDto {
  return {
    id: 'ctx-1',
    run_id: 'run-1',
    evidence_count: 2,
    estimated_tokens: 3120,
    token_budget: 8192,
    sources: ['workspace_instructions', 'session_history'],
    created_at: '2026-09-22T00:00:00Z',
    ...over,
  }
}

function snapshot(contextPacks: ContextPackDto[]): OrchestrationSnapshotDto {
  return {
    // The detail panel behind which the packs are listed is gated on a run, not on the snapshot
    // object, so a fixture without one renders the empty state and proves nothing.
    run: {
      id: 'run-1',
      session_id: 'session-1',
      status: 'completed',
      summary: 'Planned two tasks.',
      created_at: '2026-09-22T00:00:00Z',
      updated_at: '2026-09-22T00:01:00Z',
    },
    nodes: [],
    lanes: [],
    assignments: [],
    step_results: [],
    context_packs: contextPacks,
    supervision_findings: [],
  }
}

function mountTab(contextPacks: ContextPackDto[]) {
  return mount(OrchestrationTab, {
    props: { snapshot: snapshot(contextPacks), toolExecutions: [], tools: [] },
    global: { stubs, plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] },
  })
}

describe('OrchestrationTab context packs', () => {
  it('names the evidence a pack carried, which is the only way to see what was left out', () => {
    const wrapper = mountTab([pack()])
    const row = wrapper.get('[data-testid="context-pack-row"]')

    expect(row.text()).toContain('workspace_instructions')
    expect(row.text()).toContain('session_history')
    expect(row.text()).toContain('3120 / 8192')
  })

  it('prints no NaN when the wire has nothing like a compression ratio', () => {
    // The projection in Core emits evidence count, tokens and budget. A ratio the server never
    // produced rendered as `Math.round(undefined * 100)` — every row of this panel said "NaN%".
    const wrapper = mountTab([pack()])

    expect(wrapper.get('[data-testid="context-pack-row"]').text()).toContain('2 evidence')
    expect(wrapper.text()).not.toContain('NaN')
  })

  it('says the source list is unknown for an older event rather than claiming the pack was empty', () => {
    const wrapper = mountTab([pack({ sources: [] })])
    const row = wrapper.get('[data-testid="context-pack-row"]')

    expect(row.text()).toContain('unknown here rather than empty')
    expect(row.find('[data-testid="context-pack-sources"]').exists()).toBe(false)
  })

  it('prices each source and collapses the ones that contributed several items', () => {
    const row = mountTab([pack({
      sources: ['workspace_instructions', 'session_history', 'reviewed_memory', 'reviewed_memory'],
      source_tokens: [
        { source: 'workspace_instructions', tokens: 900 },
        { source: 'session_history', tokens: 40 },
        { source: 'reviewed_memory', tokens: 7 },
        { source: 'reviewed_memory', tokens: 5 },
      ],
    })]).get('[data-testid="context-pack-row"]')

    const sources = row.findAll('[data-testid="context-pack-source"]').map((tag) => tag.text())
    expect(sources).toContain('workspace_instructions · 900')
    // Two items from one source must not render as two prices for the same name.
    expect(sources.filter((text) => text.startsWith('reviewed_memory'))).toEqual(['reviewed_memory · 12'])
    // The chips are a list, not a run of adjacent text: without the roles a screen reader reads
    // "workspace_instructions · 900session_history · 40" as one word, because the markup has no
    // whitespace between spans (the gap is CSS). Same convention as the composer's attachment strip.
    const list = row.get('[data-testid="context-pack-sources"]')
    expect(list.attributes('role')).toBe('list')
    expect(list.findAll('[role="listitem"]')).toHaveLength(3)
  })

  it('shows what the budget crowded out, because a missing name in the pack is not a missing fact', () => {
    const row = mountTab([pack({
      source_tokens: [{ source: 'workspace_instructions', tokens: 900 }],
      dropped_sources: [{ source: 'workspace_skills', tokens: 2100 }],
    })]).get('[data-testid="context-pack-row"]')

    const dropped = row.findAll('[data-testid="context-pack-dropped-source"]')
    expect(dropped).toHaveLength(1)
    expect(dropped[0].text()).toBe('workspace_skills (2100 tokens)')
    expect(row.text()).toContain('the model was not told them')
  })

  it('invents no price and no cut for an event written before either was recorded', () => {
    const row = mountTab([pack()]).get('[data-testid="context-pack-row"]')

    // A bare name is the honest rendering of "no price recorded"; "· 0" would claim
    // the source was measured free.
    expect(row.get('[data-testid="context-pack-source"]').text()).toBe('workspace_instructions')
    expect(row.find('[data-testid="context-pack-dropped"]').exists()).toBe(false)
    expect(row.text()).not.toContain('undefined')
    expect(row.text()).not.toContain('NaN')
  })

  it('labels a pack by the one field that separates them, because the wire carries no summary', () => {
    const laneRow = mountTab([pack({ lane_key: 'implementation' })]).get('[data-testid="context-pack-row"]')
    expect(laneRow.text()).toContain("Lane 'implementation' pack")

    const plannerRow = mountTab([pack()]).get('[data-testid="context-pack-row"]')
    expect(plannerRow.text()).toContain('Planner pack')
    expect(plannerRow.text()).not.toContain('Lane')
  })
})

describe('OrchestrationTab model usage', () => {
  async function mountLoaded() {
    const wrapper = mountTab([])
    await flushPromises()
    return wrapper
  }

  it('rolls the run audit up per model instead of listing every call', async () => {
    listModelInvocations.mockResolvedValue({
      items: [
        invocation({ id: 'a', input_tokens: 100, output_tokens: 40, total_tokens: 140 }),
        invocation({ id: 'b', input_tokens: 7, output_tokens: 3, total_tokens: 10 }),
      ],
    })

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    const groups = block.findAll('[data-testid="model-usage-group"]')
    expect(groups).toHaveLength(1)
    expect(groups[0]!.text()).toContain('gpt-test')
    expect(groups[0]!.text()).toContain('150 tokens')
    expect(groups[0]!.text()).toContain('2 calls')
    // Nothing in this run went unreported, so the panel must not claim that anything did.
    expect(block.find('[data-testid="model-usage-unpriced"]').exists()).toBe(false)
  })

  it('walks the cursor so a long run is not silently read as a short one', async () => {
    listModelInvocations
      .mockResolvedValueOnce({ items: [invocation({ id: 'a', total_tokens: 140, input_tokens: 100, output_tokens: 40 })], next_cursor: 'Y3Vyc29yLTI' })
      .mockResolvedValueOnce({ items: [invocation({ id: 'b', total_tokens: 60, input_tokens: 40, output_tokens: 20 })] })

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(listModelInvocations).toHaveBeenCalledTimes(2)
    expect(listModelInvocations.mock.calls[1]![0]).toMatchObject({ run_id: 'run-1', cursor: 'Y3Vyc29yLTI', limit: 200 })
    expect(block.get('[data-testid="model-usage-group"]').text()).toContain('200 tokens')
    expect(block.find('[data-testid="model-usage-truncated"]').exists()).toBe(false)
  })

  it('says a call reported nothing instead of pricing it at zero', async () => {
    listModelInvocations.mockResolvedValue({
      items: [
        invocation({ id: 'paid', total_tokens: 140, input_tokens: 100, output_tokens: 40 }),
        invocation({ id: 'silent', total_tokens: undefined, input_tokens: undefined, output_tokens: undefined }),
      ],
    })

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    const group = block.get('[data-testid="model-usage-group"]').text()
    expect(group).toContain('140 tokens')
    expect(group).toContain('(1 unreported)')
    expect(block.get('[data-testid="model-usage-unpriced"]').text()).toContain('1 of 2 calls')
    // The whole point of the wording: an unreported call is not a free one. Match a standalone
    // zero rather than a substring — "140 tokens" contains "0 tokens" and saying so would make
    // this case fire on the correct rendering.
    expect(group).toMatch(/^gpt-test · 140 tokens \(1 unreported\) · 2 calls$/)
    expect(group).not.toMatch(/(^|\D)0 tokens/)
  })

  it('keeps a model that never reported usage out of the totals without hiding it', async () => {
    listModelInvocations.mockResolvedValue({
      items: [invocation({ id: 'silent', model: 'no-usage', total_tokens: null, input_tokens: null, output_tokens: null })],
    })

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(block.get('[data-testid="model-usage-group"]').text()).toContain('no token usage reported')
    expect(block.get('[data-testid="model-usage-group"]').text()).toContain('no-usage')
    expect(block.text()).not.toContain('NaN')
    expect(block.text()).not.toMatch(/\b0 tokens\b/)
  })

  it('declares the figures a floor once the walk hits its page cap', async () => {
    const page = (cursor: string | null) => ({
      items: [invocation({ id: `p-${cursor ?? 'first'}`, total_tokens: 10, input_tokens: 6, output_tokens: 4 })],
      next_cursor: cursor,
    })
    listModelInvocations
      .mockResolvedValueOnce(page('c2'))
      .mockResolvedValueOnce(page('c3'))
      .mockResolvedValueOnce(page('c4'))
      .mockResolvedValueOnce(page('c5'))
      .mockResolvedValueOnce(page('c6'))

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(listModelInvocations).toHaveBeenCalledTimes(5)
    const truncated = block.get('[data-testid="model-usage-truncated"]')
    expect(truncated.text()).toContain('Partial')
    expect(truncated.text()).toContain('floor')
  })

  it('reports an unreadable audit as unreadable rather than as a run that cost nothing', async () => {
    listModelInvocations.mockRejectedValue(new Error('gateway down'))

    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(block.get('[data-testid="model-usage-unavailable"]').text()).toContain('An unread audit is not a zero-cost run')
    expect(block.findAll('[data-testid="model-usage-group"]')).toHaveLength(0)
    expect(block.text()).not.toMatch(/\b0 tokens\b/)
  })

  it('shows the empty state only when the audit really came back empty', async () => {
    const block = (await mountLoaded()).get('[data-testid="model-usage"]')
    expect(block.get('[data-testid="model-usage-empty"]').text()).toContain('No model calls')
  })
})

describe('OrchestrationTab runtime mini topology & i18n polish', () => {
  function buildTestMessages() {
    return { en, 'zh-CN': zhCN }
  }

  function toolExec(over: Partial<ToolExecutionTimelineItemDto> = {}): ToolExecutionTimelineItemDto {
    return {
      id: 'exec-1',
      run_id: 'run-1',
      session_id: 'session-1',
      tool_id: 'read_file',
      tool_display_name: 'Read File',
      source: 'core',
      provider_layer: 'execution',
      risk: 'low',
      requires_approval: false,
      status: 'completed',
      summary: 'Read file',
      evidence: [],
      requested_at: '2026-09-22T00:00:00Z',
      updated_at: '2026-09-22T00:00:01Z',
      duration_ms: 120,
      requested_seq: 1,
      updated_seq: 2,
      event_types: ['tool.execution.completed'],
      checkpoint_summary: '',
      ...over,
    }
  }

  function sampleTopologyRun(): TopologyRunDto {
    return {
      run_id: 'run-1',
      status: 'running',
      started_at: '2026-09-22T00:00:00Z',
      tier: 'self_dispatch',
      phase: 'executing',
      tasks_truncated: false,
      instances_truncated: false,
      instances: [
        { instance_id: 'inst-coord', agent_slug: 'solo_master', layer: 'operation', role: 'conversation', status: 'running', depth: 0 },
        { instance_id: 'inst-w1', agent_slug: 'coder', layer: 'execution', role: 'coder', status: 'running', parent_instance_id: 'inst-coord', task_id: 'task-w1', depth: 1 },
        { instance_id: 'inst-sub1', agent_slug: 'search', layer: 'execution', role: 'search', status: 'running', parent_instance_id: 'inst-w1', task_id: 'task-sub1', depth: 2 },
      ],
      tasks: [
        {
          task_id: 'task-w1',
          task_key: 'impl-api',
          title: 'Implement API slice',
          status: 'running',
          handle: 'coder#1',
          agent_slug: 'coder',
          worker_instance_id: 'inst-w1',
          dispatched_by_task_id: null,
          dependencies: [],
          write_scope: ['src/server.ts'],
          result_summary: null,
        },
        {
          task_id: 'task-w2',
          task_key: 'verify-docs',
          title: 'Verify docs',
          status: 'completed',
          handle: 'docs#1',
          agent_slug: 'docs',
          worker_instance_id: null,
          dispatched_by_task_id: null,
          dependencies: [],
          write_scope: [],
          result_summary: 'Docs verified cleanly.',
        },
        {
          task_id: 'task-sub1',
          task_key: 'grep-refs',
          title: 'Search call-sites',
          status: 'running',
          handle: 'search#1',
          agent_slug: 'search',
          worker_instance_id: 'inst-sub1',
          dispatched_by_task_id: 'task-w1',
          dependencies: [],
          write_scope: ['src/api.ts'],
          result_summary: null,
        },
        {
          task_id: 'task-sub2',
          task_key: 'lint-check',
          title: 'Run lint probe',
          status: 'failed',
          handle: 'lint#1',
          agent_slug: 'linter',
          worker_instance_id: null,
          dispatched_by_task_id: 'task-w1',
          dependencies: [],
          write_scope: [],
          result_summary: 'Lint failed on unused import.',
        },
      ],
    }
  }

  function nestedSnapshot(): OrchestrationSnapshotDto {
    return {
      run: {
        id: 'run-1',
        session_id: 'session-1',
        status: 'running',
        summary: 'Executing nested tasks.',
        created_at: '2026-09-22T00:00:00Z',
        updated_at: '2026-09-22T00:01:00Z',
      },
      nodes: [
        {
          id: 'task-w1',
          graph_id: 'g-1',
          run_id: 'run-1',
          session_id: 'session-1',
          title: 'Implement API slice',
          description: 'Wire endpoint handlers.',
          status: 'running',
          lane_key: 'coder',
          priority: 1,
          risk: 'medium',
          success_criteria: [],
          dependencies: [],
          required_capabilities: [],
          created_at: '2026-09-22T00:00:00Z',
          updated_at: '2026-09-22T00:01:00Z',
        },
        {
          id: 'task-sub1',
          graph_id: 'g-1',
          run_id: 'run-1',
          session_id: 'session-1',
          title: 'Search call-sites',
          description: 'Locate callers of listTools.',
          status: 'running',
          lane_key: 'search',
          priority: 2,
          risk: 'low',
          success_criteria: [],
          dependencies: [],
          required_capabilities: [],
          created_at: '2026-09-22T00:00:00Z',
          updated_at: '2026-09-22T00:01:00Z',
        },
      ],
      lanes: [],
      assignments: [],
      step_results: [
        { id: 'sr-w1', run_id: 'run-1', task_node_id: 'task-w1', agent_id: 'inst-w1', status: 'completed', summary: 'Worker draft ready.', evidence: [], created_at: '2026-09-22T00:00:30Z' },
        { id: 'sr-sub1', run_id: 'run-1', task_node_id: 'task-sub1', agent_id: 'inst-sub1', status: 'completed', summary: 'Found 4 call-sites.', evidence: [], created_at: '2026-09-22T00:00:40Z' },
      ],
      context_packs: [
        pack({ id: 'ctx-planner', lane_key: null }),
        pack({ id: 'ctx-search', lane_key: 'search', estimated_tokens: 1800 }),
      ],
      supervision_findings: [],
    }
  }

  it('lays out a 2-layer Coordinator -> Worker -> Subagent Dagre tree with live statuses and animated dispatch edges', () => {
    const executions = [
      toolExec({ id: 'exec-approval', task_id: 'task-sub1', agent_instance_id: 'inst-sub1', status: 'waiting_approval', requires_approval: true }),
    ]
    const topo = buildRuntimeTopology({
      snapshot: nestedSnapshot(),
      topologyRun: sampleTopologyRun(),
      toolExecutions: executions,
      coordinatorFallbackLabel: 'Coordinator',
    })
    const positions = layoutRuntimeTopology(topo.nodes, topo.edges)

    const coord = topo.nodes.find((n) => n.id === 'coordinator')!
    const worker1 = topo.nodes.find((n) => n.taskId === 'task-w1')!
    const worker2 = topo.nodes.find((n) => n.taskId === 'task-w2')!
    const sub1 = topo.nodes.find((n) => n.taskId === 'task-sub1')!
    const sub2 = topo.nodes.find((n) => n.taskId === 'task-sub2')!

    expect(coord.roleTier).toBe('coordinator')
    expect(worker1.roleTier).toBe('worker')
    expect(sub1.roleTier).toBe('subagent')
    expect(sub1.parentId).toBe(worker1.id)
    expect(sub2.parentId).toBe(worker1.id)

    // Dagre top-to-bottom rank ordering: Coordinator < Worker < Subagent
    expect(positions[coord.id]!.y).toBeLessThan(positions[worker1.id]!.y)
    expect(positions[worker1.id]!.y).toBeLessThan(positions[sub1.id]!.y)

    // Live status highlights including approval overlay from tool timeline
    expect(worker1.status).toBe('running')
    expect(worker2.status).toBe('completed')
    expect(sub1.status).toBe('awaiting_approval')
    expect(sub2.status).toBe('failed')

    // Animated directional dispatch edges only on active/awaiting targets
    const edgeToW1 = topo.edges.find((e) => e.target === worker1.id)!
    const edgeToW2 = topo.edges.find((e) => e.target === worker2.id)!
    const edgeToSub1 = topo.edges.find((e) => e.target === sub1.id)!
    expect(edgeToW1).toMatchObject({ source: 'coordinator', via: 'plan', animated: true })
    expect(edgeToW2).toMatchObject({ source: 'coordinator', via: 'plan', animated: false })
    expect(edgeToSub1).toMatchObject({ source: worker1.id, via: 'task_dispatch', animated: true })
  })

  it('filters logs, context packs, and task details when clicking a topology node and clears in 1 click', async () => {
    const executions = [
      toolExec({ id: 'exec-coord', task_id: null, agent_instance_id: 'inst-coord' }),
      toolExec({ id: 'exec-w1', task_id: 'task-w1', agent_instance_id: 'inst-w1' }),
      toolExec({ id: 'exec-sub1', task_id: 'task-sub1', agent_instance_id: 'inst-sub1', status: 'waiting_approval' }),
    ]
    const wrapper = mount(OrchestrationTab, {
      props: {
        snapshot: nestedSnapshot(),
        topologyRun: sampleTopologyRun(),
        toolExecutions: executions,
        tools: [],
      },
      global: {
        stubs,
        plugins: [createI18n({ legacy: false, locale: 'en', messages: buildTestMessages() })],
      },
    })
    await flushPromises()

    // Unfiltered baseline
    expect(wrapper.find('[data-testid="topology-clear-filter"]').exists()).toBe(false)
    expect(wrapper.getComponent(ToolExecutionTimeline).props('toolExecutions')).toHaveLength(3)
    expect(wrapper.findAll('[data-testid="context-pack-row"]')).toHaveLength(2)
    expect(wrapper.findAll('[data-testid="step-result-row"]')).toHaveLength(2)

    // Click Depth-2 Subagent node via VueFlow / custom node button
    const subNodeBtn = wrapper.find('[data-node-id="task:task-sub1"]')
    if (subNodeBtn.exists()) {
      await subNodeBtn.trigger('click')
    } else {
      wrapper.getComponent(VueFlow).vm.$emit('node-click', { node: { id: 'task:task-sub1' } })
      await flushPromises()
    }

    const details = wrapper.get('[data-testid="focused-node-details"]')
    expect(details.text()).toContain('Focused: search#1')
    expect(details.text()).toContain('Dispatched by coder#1')
    expect(details.text()).toContain('Locate callers of listTools.')
    expect(details.text()).toContain('src/api.ts')

    // Logs, context packs, and step results are all narrowed to search#1
    expect(wrapper.getComponent(ToolExecutionTimeline).props('toolExecutions').map((e) => e.id)).toEqual(['exec-sub1'])
    expect(wrapper.findAll('[data-testid="context-pack-row"]')).toHaveLength(1)
    expect(wrapper.get('[data-testid="context-pack-row"]').text()).toContain("Lane 'search' pack")
    expect(wrapper.findAll('[data-testid="step-result-row"]')).toHaveLength(1)
    expect(wrapper.get('[data-testid="step-result-row"]').text()).toContain('Found 4 call-sites.')

    // 1-click "All nodes" restores everything
    await wrapper.get('[data-testid="topology-clear-filter"]').trigger('click')
    expect(wrapper.find('[data-testid="focused-node-details"]').exists()).toBe(false)
    expect(wrapper.getComponent(ToolExecutionTimeline).props('toolExecutions')).toHaveLength(3)
    expect(wrapper.findAll('[data-testid="context-pack-row"]')).toHaveLength(2)
    expect(wrapper.findAll('[data-testid="step-result-row"]')).toHaveLength(2)
  })

  it('projects nested task_dispatch flows before topologyRun loads and syncs focusedTarget workerHandle', async () => {
    const snap = nestedSnapshot()
    snap.flows = [
      { from: 'solo_master', to: 'coder', handle: 'coder#1', task_key: 'impl-api', kind: 'dispatch', via: 'plan', status: 'running' },
      { from: 'coder#1', to: 'search', handle: 'search#1', task_key: 'grep-refs', kind: 'dispatch', via: 'task_dispatch', status: 'queued' },
    ]
    const wrapper = mount(OrchestrationTab, {
      props: {
        snapshot: snap,
        toolExecutions: [],
        tools: [],
        focusedTarget: { workerHandle: 'search#1', nonce: 1 },
      },
      global: {
        stubs,
        plugins: [createI18n({ legacy: false, locale: 'en', messages: buildTestMessages() })],
      },
    })
    await flushPromises()

    const details = wrapper.get('[data-testid="focused-node-details"]')
    expect(details.text()).toContain('Focused: search#1')
    expect(details.text()).toContain('Subagent')
    expect(details.text()).toContain('Dispatched by coder#1')
  })

  it('routes every UI label through i18n under zh-CN with zero hardcoded English headers', async () => {
    listModelInvocations.mockResolvedValue({
      items: [invocation({ id: 'inv-zh', model: 'deepseek-chat', total_tokens: 320, input_tokens: 200, output_tokens: 120 })],
    })
    const snap = nestedSnapshot()
    snap.context_packs = [
      pack({
        id: 'ctx-zh',
        lane_key: 'search',
        source_tokens: [{ source: 'workspace_instructions', tokens: 900 }],
        dropped_sources: [{ source: 'workspace_skills', tokens: 2100 }],
      }),
    ]
    snap.supervision_findings = [
      { id: 'sup-1', run_id: 'run-1', session_id: 'session-1', severity: '', category: '', summary: '', recommendation: '', status: 'assigned', created_at: '2026-09-22T00:00:00Z', ...({ decision: 'assigned', revision_round: 2, reasons: ['补齐契约校验'] } as Record<string, unknown>) },
    ]

    const wrapper = mount(OrchestrationTab, {
      props: {
        snapshot: snap,
        topologyRun: sampleTopologyRun(),
        toolExecutions: [],
        tools: [],
        focusedTarget: 'task-sub1',
      },
      global: {
        stubs,
        plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: buildTestMessages() })],
      },
    })
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('实时运行拓扑')
    expect(text).toContain('全部节点')
    expect(text).toContain('已聚焦：search#1')
    expect(text).toContain('子代理')
    expect(text).toContain('预算裁剪')
    expect(text).toContain('已分派 · 第 2 轮')

    for (const forbidden of ['Runtime Topology', 'All nodes', 'Focused:', 'Coordinator', 'Subagent', 'Truncated', 'Step Results', 'Context Packs', 'Model Usage']) {
      expect(text).not.toContain(forbidden)
    }
  })

  it('switches cleanly between [ 拓扑图 | 编排时间轴 ] views while preserving node selection filtering', async () => {
    const executions = [
      toolExec({ id: 'exec-coord', task_id: null, agent_instance_id: 'inst-coord' }),
      toolExec({ id: 'exec-w1', task_id: 'task-w1', agent_instance_id: 'inst-w1' }),
      toolExec({ id: 'exec-sub1', task_id: 'task-sub1', agent_instance_id: 'inst-sub1', status: 'waiting_approval' }),
    ]
    const wrapper = mount(OrchestrationTab, {
      props: {
        snapshot: nestedSnapshot(),
        topologyRun: sampleTopologyRun(),
        toolExecutions: executions,
        tools: [],
      },
      global: {
        stubs,
        plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: buildTestMessages() })],
      },
    })
    await flushPromises()

    const topoBtn = wrapper.get('[data-testid="topology-view-btn-topology"]')
    const timelineBtn = wrapper.get('[data-testid="topology-view-btn-timeline"]')
    expect(topoBtn.text()).toBe('拓扑图')
    expect(timelineBtn.text()).toBe('编排时间轴')
    expect(topoBtn.attributes('aria-selected')).toBe('true')
    expect(wrapper.find('[data-testid="orchestration-mini-graph"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="agent-timeline"]').exists()).toBe(false)

    // Switch to Agent Timeline view
    await timelineBtn.trigger('click')
    expect(timelineBtn.attributes('aria-selected')).toBe('true')
    expect(wrapper.find('[data-testid="orchestration-mini-graph"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="agent-timeline"]').exists()).toBe(true)

    // Click `search#1` row label in AgentTimeline -> filters logs, context packs, and step results
    const subRowLabel = wrapper.get('[data-testid="timeline-agent-row-label"][data-row-id="task:task-sub1"]')
    await subRowLabel.trigger('click')
    expect(wrapper.get('[data-testid="focused-node-details"]').text()).toContain('已聚焦：search#1')
    expect(wrapper.getComponent(ToolExecutionTimeline).props('toolExecutions').map((e) => e.id)).toEqual(['exec-sub1'])
    expect(wrapper.findAll('[data-testid="context-pack-row"]')).toHaveLength(1)
    expect(wrapper.findAll('[data-testid="step-result-row"]')).toHaveLength(1)

    // Switch back to Topology view -> preserves `search#1` selection cleanly
    await topoBtn.trigger('click')
    expect(wrapper.find('[data-testid="orchestration-mini-graph"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="focused-node-details"]').text()).toContain('已聚焦：search#1')
  })

  it('hosts Trajectory & Diagnostics (Tree, 5% Buckets, Zoomable Treemap, Overlap Audit, Click-to-Diagnose & Offline HTML Export)', async () => {
    const snap = nestedSnapshot()
    snap.context_packs = [
      pack({
        id: 'ctx-eval',
        lane_key: null,
        estimated_tokens: 7400,
        token_budget: 10000,
        sources: ['system_prompt', 'workspace_instructions', 'reviewed_memory', 'reviewed_memory', 'session_history'],
        source_tokens: [
          { source: 'system_prompt', tokens: 1200 },
          { source: 'workspace_instructions', tokens: 1800 },
          { source: 'reviewed_memory', tokens: 600 },
          { source: 'reviewed_memory', tokens: 400 },
          { source: 'session_history', tokens: 3400 },
        ],
        dropped_sources: [{ source: 'session_history', tokens: 900 }],
      }),
    ]
    const failedExec = toolExec({
      id: 'exec-stale',
      task_id: 'task-w1',
      agent_instance_id: 'inst-w1',
      status: 'failed',
      tool_success: false,
      error_category: 'fs_stale_version',
      summary: 'src/app.ts:18:4 line_hash mismatch (+6/-2)',
      evidence: ['> 18 | return oldHash'],
    })

    const wrapper = mount(OrchestrationTab, {
      props: {
        snapshot: snap,
        topologyRun: sampleTopologyRun(),
        toolExecutions: [failedExec],
        tools: [],
      },
      global: {
        stubs,
        plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: buildTestMessages() })],
      },
    })
    await flushPromises()

    // Preserves [ 拓扑图 | 编排时间轴 ] switcher alongside Trajectory & Diagnostics block
    expect(wrapper.find('[data-testid="topology-view-switcher"]').exists()).toBe(true)
    const diagBlock = wrapper.get('[data-testid="trajectory-diagnostics-block"]')
    expect(diagBlock.find('[data-testid="trajectory-tree-panel"]').exists()).toBe(true)
    expect(diagBlock.find('[data-testid="context-window-bucket-chart"]').exists()).toBe(true)
    expect(diagBlock.find('[data-testid="traj-badge-FS_STALE_VERSION"]').exists()).toBe(true)

    // Switch diagnostics tab -> Zoomable Context Treemap
    await diagBlock.get('[data-testid="diagnostics-tab-treemap"]').trigger('click')
    expect(diagBlock.find('[data-testid="context-treemap"]').exists()).toBe(true)
    await diagBlock.get('[data-testid="treemap-tile-cat:memory"]').trigger('click')
    expect(diagBlock.find('[data-testid="treemap-crumb-cat:memory"]').exists()).toBe(true)
    expect(diagBlock.find('[data-testid="treemap-detail-drawer"]').exists()).toBe(true)

    // Switch diagnostics tab -> 64-char Winnowing Context Overlap Audit
    await diagBlock.get('[data-testid="diagnostics-tab-overlap"]').trigger('click')
    expect(diagBlock.find('[data-testid="context-overlap-panel"]').exists()).toBe(true)

    // Export Offline HTML Report
    await diagBlock.get('[data-testid="diagnostics-export-html-btn"]').trigger('click')
    const exported = wrapper.emitted('export-report')
    expect(exported).toHaveLength(1)
    const report = exported![0]![0] as { filename: string; html: string }
    expect(report.filename).toMatch(/^tinadec-diagnostic-run-1-\d{4}-\d{2}-\d{2}\.html$/)
    expect(report.html).toContain('Executive Conclusion Summary')
    expect(report.html).toContain('FS_STALE_VERSION')

    // Click-to-Diagnose -> Composer draft with tinadec_inspect instruction
    await diagBlock.get('[data-testid="diagnostics-diagnose-btn"]').trigger('click')
    await flushPromises()
    const diagnosed = wrapper.emitted('diagnose-draft')
    expect(diagnosed).toHaveLength(1)
    expect(String(diagnosed![0]![0])).toContain('tinadec_inspect')
  })
})
