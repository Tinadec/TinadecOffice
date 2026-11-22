// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '../../locales/en'
import zhCN from '../../locales/zh-CN'
import AgentTimeline from './AgentTimeline.vue'
import {
  AGENT_TIMELINE_PALETTE,
  buildAgentTimeline,
  computeCausalChain,
} from './agentTimelineModel'
import type {
  ModelInvocationDto,
  OrchestrationSnapshotDto,
  ToolExecutionTimelineItemDto,
  TopologyRunDto,
} from '../../api'

vi.mock('../../api', () => ({
  api: {},
}))

function buildI18n(locale: 'en' | 'zh-CN' = 'en') {
  return createI18n({
    legacy: false,
    locale,
    messages: { en, 'zh-CN': zhCN },
  })
}

function makeInvocation(over: Partial<ModelInvocationDto> = {}): ModelInvocationDto {
  return {
    id: 'inv-1',
    call_id: 'call-1',
    attempt: 1,
    session_id: 'sess-1',
    run_id: 'run-1',
    agent_instance_id: 'inst-coord',
    agent_definition_id: 'solo_master',
    agent_version_id: 'v1',
    mode_version_id: 'm1',
    strategy_source: 'route',
    provider_instance_id: 'prov-1',
    provider_version_id: 'pv-1',
    model: 'claude-sonnet-4-5',
    protocol: 'anthropic-messages',
    fallback_position: 0,
    status: 'succeeded',
    input_tokens: 4000,
    output_tokens: 1200,
    total_tokens: 5200,
    started_at: '2026-11-22T00:00:00.000Z',
    completed_at: '2026-11-22T00:00:01.000Z',
    ...over,
  }
}

function makeToolExec(over: Partial<ToolExecutionTimelineItemDto> = {}): ToolExecutionTimelineItemDto {
  return {
    id: 'exec-1',
    run_id: 'run-1',
    session_id: 'sess-1',
    tool_id: 'read_file',
    tool_display_name: 'Read File',
    source: 'core',
    provider_layer: 'execution',
    risk: 'low',
    requires_approval: false,
    status: 'completed',
    summary: 'Read src/api.ts',
    evidence: [],
    requested_at: '2026-11-22T00:00:02.000Z',
    updated_at: '2026-11-22T00:00:03.200Z',
    duration_ms: 1200,
    requested_seq: 1,
    updated_seq: 2,
    event_types: ['tool.execution.completed'],
    checkpoint_summary: '',
    ...over,
  }
}

function makeSettledFixture() {
  const snapshot = {
    run: {
      id: 'run-1',
      session_id: 'sess-1',
      status: 'completed',
      summary: 'Integrated parallel changes cleanly.',
      created_at: '2026-11-22T00:00:00.000Z',
      updated_at: '2026-11-22T00:00:10.000Z',
    },
    graph: {
      tier: 'self_dispatch',
      nodes: [
        {
          node_key: 'solo_master',
          label: 'Lead Coordinator',
          layer: 'meeting',
          is_conversation: true,
        },
      ],
      edges: [],
    },
    nodes: [],
    lanes: [],
    assignments: [],
    step_results: [
      {
        id: 'sr-impl',
        run_id: 'run-1',
        task_node_id: 'task-impl',
        agent_id: 'inst-impl',
        status: 'completed',
        summary: 'Implemented timeline model.',
        evidence: [],
        created_at: '2026-11-22T00:00:07.500Z',
      },
      {
        id: 'sr-audit',
        run_id: 'run-1',
        task_node_id: 'task-audit',
        agent_id: 'inst-audit',
        status: 'completed',
        summary: 'Verified zero regressions.',
        evidence: [],
        created_at: '2026-11-22T00:00:08.500Z',
      },
    ],
    context_packs: [],
    supervision_findings: [],
    teammates: [
      {
        handle: 'impl#1',
        agent_slug: 'global_engineering',
        task_id: 'task-impl',
        instance_id: 'inst-impl',
        runtime_badge: 'ACP · Antigravity',
        model: 'gemini-3-pro',
        created_at: '2026-11-22T00:00:01.000Z',
      },
      {
        handle: 'audit#2',
        agent_slug: 'reviewer',
        task_id: 'task-audit',
        instance_id: 'inst-audit',
        channel: 'cli',
        driver: 'claude-code',
        model: 'claude-sonnet-4-5',
        created_at: '2026-11-22T00:00:02.000Z',
      },
      {
        handle: 'codex#3',
        display_name: 'codex#3',
        agent_slug: 'verifier',
        instance_id: 'inst-codex',
        channel: 'tui',
        driver: 'codex',
        model: 'gpt-5-codex',
        status: 'completed',
        created_at: '2026-11-22T00:00:03.000Z',
        updated_at: '2026-11-22T00:00:08.000Z',
      },
    ],
  } as unknown as OrchestrationSnapshotDto

  const topologyRun: TopologyRunDto = {
    run_id: 'run-1',
    status: 'completed',
    started_at: '2026-11-22T00:00:00.000Z',
    completed_at: '2026-11-22T00:00:10.000Z',
    tasks_truncated: false,
    instances_truncated: false,
    instances: [
      {
        instance_id: 'inst-coord',
        agent_slug: 'solo_master',
        layer: 'meeting',
        role: 'conversation',
        status: 'completed',
        depth: 0,
      },
      {
        instance_id: 'inst-impl',
        agent_slug: 'global_engineering',
        layer: 'execution',
        role: 'coder',
        status: 'completed',
        parent_instance_id: 'inst-coord',
        task_id: 'task-impl',
        depth: 1,
      },
      {
        instance_id: 'inst-audit',
        agent_slug: 'reviewer',
        layer: 'execution',
        role: 'reviewer',
        status: 'completed',
        parent_instance_id: 'inst-coord',
        task_id: 'task-audit',
        depth: 1,
      },
    ],
    tasks: [
      {
        task_id: 'task-audit',
        task_key: 'audit',
        title: 'Audit contracts',
        status: 'completed',
        handle: 'audit#2',
        agent_slug: 'reviewer',
        worker_instance_id: 'inst-audit',
        dependencies: [],
        write_scope: [],
        result_summary: 'Verified zero regressions.',
      },
      {
        task_id: 'task-impl',
        task_key: 'impl',
        title: 'Implement timeline',
        status: 'completed',
        handle: 'impl#1',
        agent_slug: 'global_engineering',
        worker_instance_id: 'inst-impl',
        dependencies: [],
        write_scope: ['src/components/timeline'],
        result_summary: 'Implemented timeline model.',
      },
    ],
  }

  const modelInvocations: ModelInvocationDto[] = [
    makeInvocation({
      id: 'inv-coord-plan',
      agent_instance_id: 'inst-coord',
      started_at: '2026-11-22T00:00:00.000Z',
      completed_at: '2026-11-22T00:00:01.000Z',
      total_tokens: 3000,
      input_tokens: 2400,
      output_tokens: 600,
    }),
    makeInvocation({
      id: 'inv-impl-1',
      agent_instance_id: 'inst-impl',
      agent_definition_id: 'global_engineering',
      model: 'gemini-3-pro',
      started_at: '2026-11-22T00:00:01.000Z',
      completed_at: '2026-11-22T00:00:04.500Z',
      total_tokens: 6400,
      input_tokens: 5000,
      output_tokens: 1400,
    }),
    makeInvocation({
      id: 'inv-audit-fail',
      agent_instance_id: 'inst-audit',
      agent_definition_id: 'reviewer',
      status: 'failed',
      safe_error_message: 'Rate limit exceeded',
      started_at: '2026-11-22T00:00:02.000Z',
      completed_at: '2026-11-22T00:00:03.000Z',
      total_tokens: null,
      input_tokens: null,
      output_tokens: null,
    }),
    makeInvocation({
      id: 'inv-audit-retry',
      agent_instance_id: 'inst-audit',
      agent_definition_id: 'reviewer',
      attempt: 2,
      fallback_position: 1,
      status: 'succeeded',
      started_at: '2026-11-22T00:00:03.200Z',
      completed_at: '2026-11-22T00:00:08.500Z',
      total_tokens: 4800,
      input_tokens: 3800,
      output_tokens: 1000,
    }),
    makeInvocation({
      id: 'inv-coord-synth',
      agent_instance_id: 'inst-coord',
      started_at: '2026-11-22T00:00:08.600Z',
      completed_at: '2026-11-22T00:00:10.000Z',
      total_tokens: 2200,
      input_tokens: 1700,
      output_tokens: 500,
    }),
  ]

  const toolExecutions: ToolExecutionTimelineItemDto[] = [
    makeToolExec({
      id: 'exec-impl-write',
      task_id: 'task-impl',
      agent_instance_id: 'inst-impl',
      tool_id: 'write_file',
      tool_display_name: 'Write File',
      summary: 'Write AgentTimeline.vue',
      requested_at: '2026-11-22T00:00:04.600Z',
      updated_at: '2026-11-22T00:00:06.800Z',
      duration_ms: 2200,
    }),
    makeToolExec({
      id: 'exec-impl-handoff',
      task_id: 'task-impl',
      agent_instance_id: 'inst-impl',
      tool_id: 'team_message',
      tool_display_name: 'Team Message',
      summary: 'Notify @audit#2 ready for contract review',
      arguments_summary: '@audit#2 timeline ready',
      requested_at: '2026-11-22T00:00:06.900Z',
      updated_at: '2026-11-22T00:00:07.500Z',
      duration_ms: 600,
    }),
  ]

  return { snapshot, topologyRun, modelInvocations, toolExecutions }
}

describe('agentTimelineModel real data adapter & causal closure', () => {
  it('orders Coordinator on top and parallel subagents/teammates chronologically with #36 runtime badges, speedup, tokens, and all 5 span states', () => {
    const { snapshot, topologyRun, modelInvocations, toolExecutions } = makeSettledFixture()
    const model = buildAgentTimeline({
      snapshot,
      topologyRun,
      modelInvocations,
      toolExecutions,
    })

    expect(model.live).toBe(false)
    expect(model.wallDurationMs).toBe(10_000)
    expect(model.agentCount).toBe(4)

    // Coordinator strictly on top (meeting tier), teammates chronologically ordered below: impl#1 (1s) -> audit#2 (2s) -> codex#3 (3s)
    expect(model.rows.map((r) => r.label)).toEqual([
      'Lead Coordinator',
      'impl#1',
      'audit#2',
      'codex#3',
    ])
    expect(model.rows[0]!.roleKind).toBe('coordinator')
    expect(model.rows[0]!.tier).toBe('meeting')
    expect(model.rows[0]!.color).toBe(AGENT_TIMELINE_PALETTE[0])

    // #33 persistent teammate flags + #36 heterogeneous runtime badges
    expect(model.rows[1]).toMatchObject({
      roleKind: 'teammate',
      isPersistentTeammate: true,
      runtimeBadge: 'ACP · Antigravity',
      runtimeKind: 'acp',
      modelLabel: 'gemini-3-pro',
    })
    expect(model.rows[2]).toMatchObject({
      roleKind: 'teammate',
      isPersistentTeammate: true,
      runtimeBadge: 'CLI · Claude Code',
      runtimeKind: 'cli',
      modelLabel: 'claude-sonnet-4-5',
    })
    expect(model.rows[3]).toMatchObject({
      roleKind: 'teammate',
      isPersistentTeammate: true,
      runtimeBadge: 'TUI · Codex',
      runtimeKind: 'tui',
      modelLabel: 'gpt-5-codex',
    })

    // Parallel active sum > 10s wall clock => speedup > 1.5x
    expect(model.speedupFactor).toBeGreaterThan(1.5)
    expect(model.totalTokens).toBe(3000 + 6400 + 4800 + 2200)
    expect(model.estimatedCostUsd).not.toBeNull()
    expect(model.estimatedCostUsd!).toBeGreaterThan(0)

    // Verify all 5 span kinds exist across the projected run
    const kinds = new Set(model.spans.map((s) => s.kind))
    expect(kinds.has('thinking')).toBe(true)
    expect(kinds.has('tool')).toBe(true)
    expect(kinds.has('waiting')).toBe(true)
    expect(kinds.has('failed')).toBe(true)
    expect(kinds.has('retry')).toBe(true)

    // Verify downward dispatch, upward completion, and @mention team_message handoff edges
    expect(model.edges.some((e) => e.kind === 'dispatch' && e.direction === 'down')).toBe(true)
    expect(model.edges.some((e) => e.kind === 'completion' && e.direction === 'up')).toBe(true)
    expect(
      model.edges.some(
        (e) => e.kind === 'handoff' && e.via === 'team_message' && e.targetLabel === 'audit#2',
      ),
    ).toBe(true)
  })

  it('computes directed upstream + downstream causal closure while isolating unrelated parallel rows', () => {
    const { snapshot, topologyRun, modelInvocations, toolExecutions } = makeSettledFixture()
    const model = buildAgentTimeline({
      snapshot,
      topologyRun,
      modelInvocations,
      toolExecutions,
    })

    const codexRow = model.rows.find((r) => r.handle === 'codex#3')!
    const implRow = model.rows.find((r) => r.handle === 'impl#1')!
    const codexSpan = codexRow.spans[0]!

    const chain = computeCausalChain(codexSpan.id, model.spans, model.edges)
    expect(chain.spanIds.has(codexSpan.id)).toBe(true)
    expect(chain.rowIds.has('coordinator')).toBe(true)
    expect(chain.rowIds.has(codexRow.id)).toBe(true)
    // Unrelated parallel branch `impl#1` must NOT be pulled into `codex#3`'s causal chain
    expect(chain.rowIds.has(implRow.id)).toBe(false)
  })
})

describe('AgentTimeline.vue clean-room component UX', () => {
  let resizeCallbacks: ResizeObserverCallback[] = []
  const OriginalResizeObserver = globalThis.ResizeObserver

  beforeEach(() => {
    resizeCallbacks = []
    globalThis.ResizeObserver = class MockResizeObserver {
      private cb: ResizeObserverCallback
      constructor(cb: ResizeObserverCallback) {
        this.cb = cb
        resizeCallbacks.push(cb)
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver
  })

  afterEach(() => {
    globalThis.ResizeObserver = OriginalResizeObserver
    vi.restoreAllMocks()
  })

  it('renders settled summary, explicit × Failed & Retry states, SVG elbow connectors, hover card, and Enter/Escape causal pinning', async () => {
    const { snapshot, topologyRun, modelInvocations, toolExecutions } = makeSettledFixture()
    const wrapper = mount(AgentTimeline, {
      props: {
        snapshot,
        topologyRun,
        modelInvocations,
        toolExecutions,
      },
      global: { plugins: [buildI18n('en')] },
    })

    // Settled header summary: N agents · Xs · Yx faster than sequential · tokens · cost
    const summaryText = wrapper.get('[data-testid="timeline-replay-summary"]').text()
    expect(summaryText).toContain('4 agents')
    expect(summaryText).toContain('10s')
    expect(summaryText).toContain('faster than sequential')
    expect(summaryText).toContain('16.4k tok')
    expect(summaryText).toContain('$')

    // Explicit `× Failed` label (never color alone) + Retry badge
    const failedLabel = wrapper.get('[data-testid="timeline-failed-label"]')
    expect(failedLabel.text()).toContain('×')
    expect(failedLabel.text()).toContain('Failed')
    expect(wrapper.get('[data-testid="timeline-retry-label"]').text()).toContain('Fallback #1')

    // SVG orthogonal elbow connectors + endpoint circles
    const svg = wrapper.get('[data-testid="timeline-connectors-svg"]')
    const edges = svg.findAll('[data-testid="timeline-connector-edge"]')
    expect(edges.length).toBeGreaterThanOrEqual(4)
    expect(svg.findAll('path').length).toBe(edges.length)
    expect(svg.findAll('circle').length).toBe(edges.length * 2)

    // Hovering a tool span opens the detail card with tool name, duration, model, and runtime badge
    const writeSpan = wrapper.get('[data-span-id="span:tool:exec-impl-write"]')
    await writeSpan.trigger('mouseenter')
    const card = wrapper.get('[data-testid="timeline-detail-card"]')
    expect(card.text()).toContain('impl#1')
    expect(card.text()).toContain('ACP · Antigravity')
    expect(card.text()).toContain('gemini-3-pro')
    expect(card.text()).toContain('Write File')
    expect(card.text()).toContain('2.2s')

    // Pinning `codex#3` span via Enter key dims unrelated `impl#1` row and clears cleanly on Escape
    const codexRow = wrapper.findAll('[data-testid="timeline-track-row"]').find((el) =>
      el.attributes('data-row-id')?.includes('codex#3'),
    )!
    const implRow = wrapper.findAll('[data-testid="timeline-track-row"]').find((el) =>
      el.attributes('data-row-id')?.includes('task-impl'),
    )!
    const codexSpan = codexRow.get('[data-testid="timeline-span"]')

    await codexSpan.trigger('keydown.enter')
    expect(codexSpan.attributes('aria-pressed')).toBe('true')
    expect(implRow.classes()).toContain('is-causal-dimmed')
    expect(codexRow.classes()).not.toContain('is-causal-dimmed')

    // Escape unpins and restores all rows
    await wrapper.get('[data-testid="agent-timeline"]').trigger('keydown', { key: 'Escape' })
    expect(codexSpan.attributes('aria-pressed')).toBe('false')
    expect(implRow.classes()).not.toContain('is-causal-dimmed')
  })

  it('drives live active span via single --timeline-now-pct CSS custom property in rAF with ~20Hz clock throttling', async () => {
    const rafCallbacks: FrameRequestCallback[] = []
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb: FrameRequestCallback) => {
      rafCallbacks.push(cb)
      return rafCallbacks.length
    })
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {})
    let fakePerfNow = 1000
    vi.spyOn(performance, 'now').mockImplementation(() => fakePerfNow)

    const liveSnapshot: OrchestrationSnapshotDto = {
      run: {
        id: 'run-live',
        session_id: 'sess-live',
        status: 'running',
        summary: 'Streaming tool work',
        created_at: '2026-11-22T00:00:00.000Z',
        updated_at: '2026-11-22T00:00:02.000Z',
      },
      nodes: [
        {
          id: 'task-live-1',
          graph_id: 'g-1',
          run_id: 'run-live',
          session_id: 'sess-live',
          title: 'Run test suite',
          description: '',
          status: 'running',
          lane_key: 'qa',
          priority: 1,
          risk: 'low',
          success_criteria: [],
          dependencies: [],
          required_capabilities: [],
          created_at: '2026-11-22T00:00:00.500Z',
          updated_at: '2026-11-22T00:00:02.000Z',
        },
      ],
      lanes: [],
      assignments: [],
      step_results: [],
      context_packs: [],
      supervision_findings: [],
    }

    const wrapper = mount(AgentTimeline, {
      props: {
        snapshot: liveSnapshot,
        nowMs: Date.parse('2026-11-22T00:00:02.500Z'),
      },
      global: { plugins: [buildI18n('en')] },
    })

    const rootEl = wrapper.get('[data-testid="agent-timeline"]').element as HTMLElement
    expect(wrapper.find('[data-testid="timeline-live-header"]').exists()).toBe(true)
    expect(wrapper.findAll('.agent-timeline__span.is-live-active').length).toBeGreaterThan(0)

    const initialClock = wrapper.get('[data-testid="timeline-live-clock"]').text()
    expect(rafCallbacks.length).toBeGreaterThan(0)

    // Sub-50ms rAF frame (16ms): updates `--timeline-now-pct` DOM style directly WITHOUT updating reactive clock text
    const firstPct = rootEl.style.getPropertyValue('--timeline-now-pct')
    const frame1 = rafCallbacks.pop()!
    fakePerfNow = 1016
    frame1(1016)
    await wrapper.vm.$nextTick()
    const secondPct = rootEl.style.getPropertyValue('--timeline-now-pct')
    expect(Number(secondPct)).toBeGreaterThan(Number(firstPct))
    expect(wrapper.get('[data-testid="timeline-live-clock"]').text()).toBe(initialClock)

    // >=50ms frame (1600ms => +600ms): updates both CSS variable and throttled numeric clock text
    const frame2 = rafCallbacks.pop()!
    fakePerfNow = 1600
    frame2(1600)
    await wrapper.vm.$nextTick()
    expect(wrapper.get('[data-testid="timeline-live-clock"]').text()).not.toBe(initialClock)

    wrapper.unmount()
  })

  it('supports keyboard replay scrubbing, 1x/2x toggle, and prefers-reduced-motion instant bypass', async () => {
    const { snapshot, topologyRun, modelInvocations, toolExecutions } = makeSettledFixture()

    // Mock prefers-reduced-motion: reduce
    vi.spyOn(window, 'matchMedia').mockImplementation((query: string) => ({
      matches: query.includes('prefers-reduced-motion'),
      media: query,
      onchange: null,
      addListener: () => {},
      removeListener: () => {},
      addEventListener: () => {},
      removeEventListener: () => {},
      dispatchEvent: () => false,
    }))

    const wrapper = mount(AgentTimeline, {
      props: { snapshot, topologyRun, modelInvocations, toolExecutions },
      global: { plugins: [buildI18n('en')] },
    })

    // Speed toggle switches cleanly between 1x and 2x
    const speedBtn = wrapper.get('[data-testid="timeline-replay-speed"]')
    expect(speedBtn.text()).toBe('1x')
    await speedBtn.trigger('click')
    expect(speedBtn.text()).toBe('2x')

    // Keyboard Home / ArrowRight / End on range scrubber
    const scrubber = wrapper.get('[data-testid="timeline-scrubber"]')
    await scrubber.trigger('keydown', { key: 'Home' })
    expect(scrubber.attributes('aria-valuenow')).toBe('0')
    expect(wrapper.find('[data-testid="timeline-playhead"]').exists()).toBe(true)

    await scrubber.trigger('keydown', { key: 'ArrowRight' })
    expect(scrubber.attributes('aria-valuenow')).toBe('2')

    // Under prefers-reduced-motion: reduce, Play bypasses animation loop and jumps straight to 100%
    await wrapper.get('[data-testid="timeline-replay-play"]').trigger('click')
    expect(scrubber.attributes('aria-valuenow')).toBe('100')
  })

  it('collapses left label column at 420px dock width via ResizeObserver and moves model/runtime details to hover card', async () => {
    const { snapshot, topologyRun, modelInvocations, toolExecutions } = makeSettledFixture()
    const wrapper = mount(AgentTimeline, {
      props: { snapshot, topologyRun, modelInvocations, toolExecutions },
      global: { plugins: [buildI18n('zh-CN')] },
    })

    // At default 640px width, inline engine badges are visible in the left column
    expect(wrapper.get('[data-testid="agent-timeline"]').attributes('data-compact')).toBe('false')
    expect(wrapper.findAll('[data-testid="timeline-row-meta"]').length).toBeGreaterThan(0)

    // Simulate ResizeObserver reporting 420px narrow right-dock width
    const rootEl = wrapper.get('[data-testid="agent-timeline"]').element
    for (const cb of resizeCallbacks) {
      cb(
        [
          {
            target: rootEl,
            contentRect: { width: 420, height: 240 } as DOMRectReadOnly,
          } as ResizeObserverEntry,
        ],
        {} as ResizeObserver,
      )
    }
    await wrapper.vm.$nextTick()

    expect(wrapper.get('[data-testid="agent-timeline"]').attributes('data-compact')).toBe('true')
    // Inline model/runtime metadata collapses out of the narrow left column
    expect(wrapper.findAll('[data-testid="timeline-row-meta"]')).toHaveLength(0)

    // Hovering row label surfaces model & #36 runtime badge inside the detail card, localized in zh-CN
    const implRowHeader = wrapper.findAll('[data-testid="timeline-agent-row-label"]')[1]!
    await implRowHeader.trigger('mouseenter')
    const detail = wrapper.get('[data-testid="timeline-detail-card"]')
    expect(detail.get('[data-testid="timeline-detail-runtime"]').text()).toBe('ACP · Antigravity')
    expect(detail.get('[data-testid="timeline-detail-model"]').text()).toBe('gemini-3-pro')
    expect(detail.text()).toContain('常驻队友')
  })
})
