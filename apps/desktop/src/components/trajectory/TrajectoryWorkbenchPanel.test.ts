// @vitest-environment happy-dom
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import zhCN from '@/locales/zh-CN'

vi.mock('@tinadec/ui', () => ({
  useUie: () => null,
}))
import {
  api,
  type ContextPackDto,
  type ModelInvocationDto,
  type OrchestrationSnapshotDto,
  type ToolExecutionTimelineItemDto,
} from '@/api'
import { __resetObservabilityRunSelectionForTests } from '@/composables/useObservabilityRunSelection'
import { clearContextDiagnosticsCache } from '@/lib/contextDiagnosticsCache'
import { buildContextTreemapModel } from '@/lib/contextTreemapLayout'
import { focusDockCard, resetDockSyncForTests } from '@/lib/homeDockSync'
import { setHostAccessStatus } from '@/lib/hostAccess'
import * as offlineReport from '@/lib/offlineDiagnosticReport'
import ContextCompositionBar from './ContextCompositionBar.vue'
import ContextTreemap from './ContextTreemap.vue'
import TrajectoryWorkbenchPanel from './TrajectoryWorkbenchPanel.vue'

function makeI18n() {
  return createI18n({
    legacy: false,
    locale: 'zh-CN',
    messages: { 'zh-CN': zhCN },
    missingWarn: false,
    fallbackWarn: false,
  })
}

function makeToolExec(overrides: Partial<ToolExecutionTimelineItemDto> = {}): ToolExecutionTimelineItemDto {
  return {
    id: overrides.id ?? 'exec-1',
    run_id: overrides.run_id ?? 'run-2',
    session_id: overrides.session_id ?? 'sess-1',
    tool_id: overrides.tool_id ?? 'read_file',
    tool_display_name: overrides.tool_display_name ?? 'Read File',
    source: overrides.source ?? 'builtin',
    provider_layer: overrides.provider_layer ?? 'core',
    risk: overrides.risk ?? 'low',
    requires_approval: overrides.requires_approval ?? false,
    status: overrides.status ?? 'completed',
    approval_id: overrides.approval_id ?? null,
    step_result_id: overrides.step_result_id ?? null,
    summary: overrides.summary ?? 'Read src/invoice.ts',
    evidence: overrides.evidence ?? ['line 1: export interface Invoice {}'],
    requested_at: overrides.requested_at ?? '2026-11-25T07:00:01.000Z',
    updated_at: overrides.updated_at ?? '2026-11-25T07:00:01.400Z',
    duration_ms: overrides.duration_ms ?? 400,
    requested_seq: overrides.requested_seq ?? 1,
    updated_seq: overrides.updated_seq ?? 2,
    event_types: overrides.event_types ?? ['tool.completed'],
    checkpoint_summary: overrides.checkpoint_summary ?? '',
    tool_success: overrides.tool_success ?? true,
    task_id: overrides.task_id ?? 'task-1',
    agent_instance_id: overrides.agent_instance_id ?? 'agent-1',
    error_category: overrides.error_category ?? null,
    arguments_summary: overrides.arguments_summary ?? '{"path":"src/invoice.ts"}',
    resource_path: overrides.resource_path ?? 'src/invoice.ts',
    command: overrides.command ?? null,
    cwd: overrides.cwd ?? null,
    query: overrides.query ?? null,
  }
}

function makeMultiTurnSnapshot(runId = 'run-2'): OrchestrationSnapshotDto {
  return {
    run: {
      id: runId,
      session_id: 'sess-1',
      status: 'running',
      summary: 'Refactor billing & invoice pipeline',
      created_at: '2026-11-25T07:00:00.000Z',
      updated_at: '2026-11-25T07:00:12.000Z',
    },
    nodes: [
      {
        id: 'task-1',
        graph_id: 'g-1',
        run_id: runId,
        session_id: 'sess-1',
        title: 'Inspect & patch invoice',
        description: 'Update invoice.ts',
        status: 'completed',
        lane_key: 'invoice-worker',
        priority: 1,
        risk: 'low',
        success_criteria: [],
        dependencies: [],
        required_capabilities: [],
        created_at: '2026-11-25T07:00:00.000Z',
        updated_at: '2026-11-25T07:00:05.000Z',
      },
      {
        id: 'task-2',
        graph_id: 'g-1',
        run_id: runId,
        session_id: 'sess-1',
        title: 'Verify export types',
        description: 'Validate export.ts',
        status: 'failed',
        lane_key: 'verify-worker',
        priority: 2,
        risk: 'medium',
        success_criteria: [],
        dependencies: ['task-1'],
        required_capabilities: [],
        created_at: '2026-11-25T07:00:06.000Z',
        updated_at: '2026-11-25T07:00:12.000Z',
      },
    ],
    lanes: [],
    assignments: [
      {
        id: 'a-1',
        run_id: runId,
        task_node_id: 'task-1',
        agent_id: 'agent-1',
        agent_name: 'author-1',
        agent_layer: 'execution',
        agent_type: 'worker',
        model_route_purpose: 'default',
        permission_mode: 'governed',
        allowed_tools: [],
        status: 'completed',
        created_at: '2026-11-25T07:00:00.000Z',
      },
      {
        id: 'a-2',
        run_id: runId,
        task_node_id: 'task-2',
        agent_id: 'agent-2',
        agent_name: 'verifier-1',
        agent_layer: 'execution',
        agent_type: 'worker',
        model_route_purpose: 'default',
        permission_mode: 'governed',
        allowed_tools: [],
        status: 'failed',
        created_at: '2026-11-25T07:00:06.000Z',
      },
    ],
    step_results: [],
    context_packs: [
      {
        id: 'pack-exact',
        run_id: runId,
        lane_key: 'invoice-worker',
        evidence_count: 5,
        estimated_tokens: 6400,
        token_budget: 10000,
        sources: ['system_prompt', 'tool_schemas', 'workspace_skills', 'session_history', 'tool_outputs'],
        source_tokens: [
          { source: 'system_prompt', tokens: 1000 },
          { source: 'tool_schemas', tokens: 1400 },
          { source: 'workspace_skills', tokens: 800 },
          { source: 'session_history', tokens: 1200 },
          { source: 'tool_outputs', tokens: 2000 },
        ],
        dropped_sources: [{ source: 'reviewed_memory', tokens: 450 }],
        created_at: '2026-11-25T07:00:02.000Z',
      },
    ],
    supervision_findings: [],
  }
}

describe('TrajectoryWorkbenchPanel & Wave 1 Trajectory Components', () => {
  enableAutoUnmount(afterEach)

  beforeEach(() => {
    resetDockSyncForTests()
    __resetObservabilityRunSelectionForTests()
    clearContextDiagnosticsCache()
    setHostAccessStatus({ state: 'ready', webAuth: false } as never)
    vi.spyOn(api, 'getRunContextDiagnostics').mockRejectedValue(new Error('offline in unit test'))
    vi.spyOn(api, 'listModelInvocations').mockResolvedValue({ items: [], next_cursor: null })
    vi.spyOn(api, 'auditContextOverlap').mockRejectedValue(new Error('offline in unit test'))
  })

  afterEach(() => {
    vi.restoreAllMocks()
    resetDockSyncForTests()
    __resetObservabilityRunSelectionForTests()
    clearContextDiagnosticsCache()
  })

  it('enforces ContextCompositionBar data honesty: exact source_tokens omit ≈ while estimated weights show ≈ + footnote', () => {
    const exactPack: ContextPackDto = {
      id: 'p-exact',
      run_id: 'run-1',
      lane_key: null,
      evidence_count: 2,
      estimated_tokens: 4000,
      token_budget: 10000,
      sources: ['system_prompt', 'tool_schemas'],
      source_tokens: [
        { source: 'system_prompt', tokens: 1500 },
        { source: 'tool_schemas', tokens: 2500 },
      ],
      created_at: '2026-11-25T07:00:00.000Z',
    }
    const exactWrapper = mount(ContextCompositionBar, {
      props: {
        treemap: buildContextTreemapModel([exactPack]),
      },
      global: { plugins: [makeI18n()] },
    })

    expect(exactWrapper.get('[data-testid="context-composition-used"]').text()).toBe('4.0k')
    expect(exactWrapper.get('[data-testid="context-composition-pct"]').text()).toContain('40.0%')
    expect(exactWrapper.find('[data-testid="context-composition-footnote"]').exists()).toBe(false)

    // Unpriced pack (only sources[] + estimated_tokens -> ratio-derived uniform weights) MUST prefix ≈ and show footnote
    const estimatedPack: ContextPackDto = {
      id: 'p-est',
      run_id: 'run-1',
      lane_key: null,
      evidence_count: 2,
      estimated_tokens: 4000,
      token_budget: 10000,
      sources: ['system_prompt', 'tool_schemas'],
      created_at: '2026-11-25T07:00:00.000Z',
    }
    const estWrapper = mount(ContextCompositionBar, {
      props: {
        treemap: buildContextTreemapModel([estimatedPack]),
      },
      global: { plugins: [makeI18n()] },
    })

    expect(estWrapper.get('[data-testid="context-composition-used"]').text()).toBe('≈4.0k')
    expect(estWrapper.get('[data-testid="context-category-system"]').text()).toContain('≈2.0k')
    expect(estWrapper.find('[data-testid="context-composition-footnote"]').exists()).toBe(true)
  })

  it('switches cleanly between Overview Mode and Single-Turn Action Mode and filters turns by modified file pill', async () => {
    const snapshot = makeMultiTurnSnapshot('run-2')
    const execs: ToolExecutionTimelineItemDto[] = [
      makeToolExec({
        id: 'ex-read-1',
        task_id: 'task-1',
        tool_id: 'read_file',
        resource_path: 'src/invoice.ts',
        summary: 'Read src/invoice.ts',
      }),
      makeToolExec({
        id: 'ex-edit-1',
        task_id: 'task-1',
        requested_seq: 2,
        requested_at: '2026-11-25T07:00:02.000Z',
        updated_at: '2026-11-25T07:00:02.800Z',
        tool_id: 'edit_file',
        resource_path: 'src/invoice.ts',
        summary: 'Patched src/invoice.ts (+12/-4)',
      }),
      makeToolExec({
        id: 'ex-fail-2',
        task_id: 'task-2',
        requested_seq: 3,
        tool_id: 'edit_file',
        status: 'failed',
        tool_success: false,
        error_category: 'fs_stale_version',
        resource_path: 'src/export.ts',
        summary: 'src/export.ts:18:2 stale hash mismatch',
        evidence: ['> 18 | export const total = 0'],
      }),
    ]

    const wrapper = mount(TrajectoryWorkbenchPanel, {
      props: {
        sessionId: 'sess-1',
        runs: [{ id: 'run-2', status: 'running', created_at: '2026-11-25T07:00:00.000Z' }],
        activeRunId: 'run-2',
        orchestration: snapshot,
        toolExecutions: execs,
      },
      global: { plugins: [makeI18n()] },
    })
    await flushPromises()

    // Multi-turn defaults to Overview Mode SVG
    expect(wrapper.find('[data-testid="trajectory-overview-svg"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="branch-blocked-marker"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="branch-gov-shield"]').exists()).toBe(true)

    // Clicking modified file pill highlights touching turn and dims unrelated turn
    const filePills = wrapper.findAll('[data-testid="overview-file-pill"]')
    expect(filePills.length).toBeGreaterThanOrEqual(1)
    await filePills[0]!.trigger('click')
    expect(wrapper.find('[data-testid="active-file-filter-bar"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="overview-turn-1"]').classes()).toContain('is-dimmed')

    await wrapper.get('[data-testid="clear-file-filter"]').trigger('click')
    expect(wrapper.find('[data-testid="active-file-filter-bar"]').exists()).toBe(false)

    // Click Turn 1 pill -> enters Single-Turn Action Mode
    await wrapper.get('[data-testid="overview-turn-pill-0"]').trigger('click')
    expect(wrapper.find('[data-testid="trajectory-action-svg"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="action-read-pill"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="action-write-pill"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="action-step-detail"]').exists()).toBe(true)

    // Navigate to step 2 -> shows +12 / -4 diff
    await wrapper.get('[data-testid="step-next-btn"]').trigger('click')
    expect(wrapper.get('[data-testid="action-step-detail"]').text()).toContain('+12')

    // Click back to Overview
    await wrapper.get('[data-testid="path-back-overview"]').trigger('click')
    expect(wrapper.find('[data-testid="trajectory-overview-svg"]').exists()).toBe(true)
  })

  it('opens TrajectoryDiagnosisPopover with 3 presets carrying run/turn/step/tool/governance evidence and injects into Composer', async () => {
    const diagnoseSpy = vi
      .spyOn(offlineReport, 'triggerDiagnoseInComposer')
      .mockResolvedValue({ draft: 'injected', navigated: false })

    const snapshot = makeMultiTurnSnapshot('run-diag-7')
    const execs: ToolExecutionTimelineItemDto[] = [
      makeToolExec({
        id: 'ex-stuck',
        run_id: 'run-diag-7',
        task_id: 'task-2',
        tool_id: 'edit_file',
        status: 'failed',
        tool_success: false,
        error_category: 'fs_stale_version',
        resource_path: 'src/export.ts',
        summary: 'src/export.ts:24:5 line_hash mismatch',
        evidence: ['> 24 | return computeTax()'],
      }),
    ]

    const wrapper = mount(TrajectoryWorkbenchPanel, {
      props: {
        sessionId: 'sess-9',
        runs: [{ id: 'run-diag-7', status: 'failed', created_at: '2026-11-25T07:00:00.000Z' }],
        activeRunId: 'run-diag-7',
        orchestration: snapshot,
        toolExecutions: execs,
      },
      global: { plugins: [makeI18n()] },
    })
    await flushPromises()

    // Enter Turn 2 (failed turn) and open step Diagnosis Popover
    await wrapper.get('[data-testid="overview-turn-pill-1"]').trigger('click')
    await wrapper.get('[data-testid="step-diagnose-btn"]').trigger('click')

    const popover = wrapper.get('[data-testid="trajectory-diagnosis-popover"]')
    const textarea = popover.get<HTMLTextAreaElement>('[data-testid="diagnosis-draft-textarea"]')

    // Failed step defaults to 'stuck' preset ("为什么卡在这一步") with full identifiers
    expect(textarea.element.value).toContain('为什么卡在这一步')
    expect(textarea.element.value).toContain('run-diag-7')
    expect(textarea.element.value).toContain('#2')
    expect(textarea.element.value).toContain('#1')
    expect(textarea.element.value).toContain('edit_file')
    expect(textarea.element.value).toContain('FS_STALE_VERSION')

    // Switch to 'progress' preset ("这一步推进了什么")
    await popover.get('[data-testid="diagnosis-preset-progress"]').trigger('click')
    expect(textarea.element.value).toContain('这一步推进了什么')
    expect(textarea.element.value).toContain('run-diag-7')

    // Switch to 'custom' preset ("自己问") and append custom question
    await popover.get('[data-testid="diagnosis-preset-custom"]').trigger('click')
    expect(textarea.element.value).toContain('回答我的问题')
    await textarea.setValue(`${textarea.element.value}\n请重点核查 export.ts 的行哈希。`)

    // Click "填入对话" -> triggers triggerDiagnoseInComposer
    await popover.get('[data-testid="diagnosis-inject-btn"]').trigger('click')
    await flushPromises()

    expect(diagnoseSpy).toHaveBeenCalledTimes(1)
    const callArg = diagnoseSpy.mock.calls[0]![0]
    expect(callArg.runId).toBe('run-diag-7')
    expect(callArg.proposal?.composer_prompt).toContain('请重点核查 export.ts 的行哈希。')
    expect(callArg.proposal?.composer_prompt).toContain('FS_STALE_VERSION')
  })

  it('supports Geist ContextTreemap drilldown, selection detail, dropped warning bar, and Escape reset', async () => {
    const pack: ContextPackDto = {
      id: 'pack-matrix',
      run_id: 'run-2',
      lane_key: null,
      evidence_count: 4,
      estimated_tokens: 3400,
      token_budget: 5000,
      sources: ['reviewed_memory', 'reviewed_memory', 'tool_schemas'],
      source_tokens: [
        { source: 'reviewed_memory', tokens: 900 },
        { source: 'reviewed_memory', tokens: 500 },
        { source: 'tool_schemas', tokens: 2000 },
      ],
      dropped_sources: [{ source: 'workspace_instructions', tokens: 600 }],
      created_at: '2026-11-25T07:00:00.000Z',
    }

    const wrapper = mount(ContextTreemap, {
      props: { contextPacks: [pack] },
      global: { plugins: [makeI18n()] },
    })

    expect(wrapper.get('[data-testid="treemap-dropped-warning"]').text()).toContain('600')
    expect(wrapper.get('[data-testid="treemap-dropped-warning"]').text()).toContain('workspace_instructions')

    // Drill down into composite memory category
    await wrapper.get('[data-testid="treemap-tile-cat:memory"]').trigger('click')
    expect(wrapper.find('[data-testid="treemap-crumb-cat:memory"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="treemap-detail-drawer"]').text()).toContain('1,400')

    // Press Escape -> resets to root full map
    await wrapper.get('[data-testid="context-treemap"]').trigger('keydown.esc')
    expect(wrapper.find('[data-testid="treemap-crumb-cat:memory"]').exists()).toBe(false)
  })

  it('honors activeDockFocus deep links, category click jump, and loads historical run data cleanly', async () => {
    const topoSpy = vi.spyOn(api, 'getSessionTopology').mockResolvedValue({
      session_id: 'sess-1',
      runs: [
        {
          run_id: 'run-hist-1',
          status: 'completed',
          started_at: '2026-11-25T06:50:00.000Z',
          completed_at: '2026-11-25T06:50:09.000Z',
          tasks: [
            {
              task_id: 'task-hist-a',
              task_key: 'audit',
              title: 'Historical Audit Node',
              status: 'completed',
              handle: 'auditor',
              dependencies: [],
              write_scope: [],
            },
          ],
          instances: [],
          tasks_truncated: false,
          instances_truncated: false,
        },
      ],
      leases: [],
      members: [],
      runs_truncated: false,
      leases_truncated: false,
      members_truncated: false,
      generated_at: '2026-11-25T06:50:10.000Z',
    })
    const listToolsSpy = vi.spyOn(api, 'listToolExecutions').mockResolvedValue([
      makeToolExec({
        id: 'ex-hist-1',
        run_id: 'run-hist-1',
        task_id: 'task-hist-a',
        tool_id: 'bash',
        command: 'pnpm test',
        summary: 'Tests passed',
      }),
    ])
    const listInvSpy = vi.spyOn(api, 'listModelInvocations').mockResolvedValue({
      items: [] as ModelInvocationDto[],
      next_cursor: null,
    })

    const wrapper = mount(TrajectoryWorkbenchPanel, {
      props: {
        sessionId: 'sess-1',
        runs: [
          { id: 'run-hist-1', status: 'completed', created_at: '2026-11-25T06:50:00.000Z' },
          { id: 'run-2', status: 'running', created_at: '2026-11-25T07:00:00.000Z' },
        ],
        activeRunId: 'run-2',
        orchestration: makeMultiTurnSnapshot('run-2'),
        toolExecutions: [],
      },
      global: { plugins: [makeI18n()] },
    })
    await flushPromises()

    // Clicking a category in ContextCompositionBar switches subnav to 'context'
    await wrapper.get('[data-testid="context-category-skill"]').trigger('click')
    expect(wrapper.find('[data-testid="trajectory-pane-context"]').exists()).toBe(true)

    // Deep-link via focusDockCard to historical run + 'code' tab + node filter
    focusDockCard({
      card: 'trajectory',
      runId: 'run-hist-1',
      trajectoryTab: 'code',
      trajectoryNodeId: 'task-hist-a',
    })
    await flushPromises()

    expect(topoSpy).toHaveBeenCalledWith(
      'sess-1',
      expect.objectContaining({ run_id: 'run-hist-1' }),
      expect.any(AbortSignal),
    )
    expect(listToolsSpy).toHaveBeenCalledWith(
      'sess-1',
      expect.objectContaining({ run_id: 'run-hist-1' }),
      expect.any(AbortSignal),
    )
    expect(listInvSpy).toHaveBeenCalledWith(expect.objectContaining({ run_id: 'run-hist-1' }))
    expect(wrapper.find('[data-testid="trajectory-pane-code"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="trajectory-code-list"]').text()).toContain('pnpm test')

    // Node filter chip renders in ObservabilityTurnBar and clears on click
    const filterChip = wrapper.get('[data-testid="observability-filter-chip"]')
    expect(filterChip.text()).toContain('Historical Audit Node')
    await filterChip.trigger('click')
    expect(wrapper.find('[data-testid="observability-filter-chip"]').exists()).toBe(false)
  })

  it('Wave 2 Code Execution: groups failure signals, highlights error code frame line, filters by executor, and triggers all 4 remediation drafts', async () => {
    const diagnoseSpy = vi
      .spyOn(offlineReport, 'triggerDiagnoseInComposer')
      .mockResolvedValue({ draft: 'code-remediation-draft', navigated: false })

    const snapshot = makeMultiTurnSnapshot('run-w2-code')
    const execs: ToolExecutionTimelineItemDto[] = [
      makeToolExec({
        id: 'ex-pwsh-err-1',
        run_id: 'run-w2-code',
        task_id: 'task-1',
        tool_id: 'pwsh',
        tool_display_name: 'PowerShell',
        status: 'failed',
        tool_success: false,
        requested_seq: 16,
        updated_seq: 17,
        command: 'rg -n total src/{invoice.ts,totals.ts}',
        summary: 'Missing argument in parameter list.',
        evidence: [
          'ParserError:\nLine |\n   1 | rg -n total src/{invoice.ts,totals.ts}\n     | Missing argument in parameter list.',
        ],
      }),
      makeToolExec({
        id: 'ex-pwsh-err-2',
        run_id: 'run-w2-code',
        task_id: 'task-2',
        tool_id: 'pwsh',
        tool_display_name: 'PowerShell',
        status: 'failed',
        tool_success: false,
        requested_seq: 340,
        updated_seq: 341,
        command: 'rg -n total src/{invoice.ts,totals.ts}',
        summary: 'Missing argument in parameter list.',
        evidence: [
          'ParserError:\nLine |\n   1 | rg -n total src/{invoice.ts,totals.ts}\n     | Missing argument in parameter list.',
        ],
      }),
      makeToolExec({
        id: 'ex-bash-verify-err',
        run_id: 'run-w2-code',
        task_id: 'task-2',
        tool_id: 'bash',
        tool_display_name: 'Bash',
        status: 'failed',
        tool_success: false,
        requested_seq: 350,
        updated_seq: 351,
        command: 'npx vitest run src/invoice.test.ts',
        summary: 'FAIL src/invoice.test.ts > AssertionError: expected 10 to equal 20',
        evidence: ['> 24 | expect(computeInvoiceTotal()).toBe(20)'],
      }),
      makeToolExec({
        id: 'ex-bash-ok',
        run_id: 'run-w2-code',
        task_id: 'task-2',
        tool_id: 'bash',
        tool_display_name: 'Bash',
        status: 'completed',
        tool_success: true,
        requested_seq: 360,
        updated_seq: 361,
        command: 'npx vitest run src/invoice.test.ts',
        summary: '1 passed',
        evidence: ['Test Files 1 passed (1)'],
      }),
    ]

    const wrapper = mount(TrajectoryWorkbenchPanel, {
      props: {
        sessionId: 'sess-w2',
        runs: [{ id: 'run-w2-code', status: 'failed', created_at: '2026-11-25T07:00:00.000Z' }],
        activeRunId: 'run-w2-code',
        orchestration: snapshot,
        toolExecutions: execs,
      },
      global: { plugins: [makeI18n()] },
    })
    await flushPromises()

    // Code tab shows red error indicator dot from aggregateCodeExecutionSignals
    const codeTabBtn = wrapper.get('[data-testid="trajectory-tab-code"]')
    expect(codeTabBtn.find('.twb-subnav__err-dot').exists()).toBe(true)
    await codeTabBtn.trigger('click')

    const codePane = wrapper.get('[data-testid="trajectory-pane-code"]')
    expect(codePane.get('[data-testid="code-kpi-total"]').text()).toBe('4')
    expect(codePane.get('[data-testid="code-kpi-errors"]').text()).toBe('3')
    expect(codePane.get('[data-testid="code-kpi-signals"]').text()).toBe('3')

    // Signal groups render with accurate counts
    const parserGroupBtn = codePane.get('[data-testid="code-signal-group-parser_error"]')
    const verifyGroupBtn = codePane.get('[data-testid="code-signal-group-verification_failed"]')
    expect(parserGroupBtn.text()).toContain('脚本语法错误')
    expect(parserGroupBtn.text()).toContain('2')
    expect(verifyGroupBtn.text()).toContain('验证未通过')
    expect(verifyGroupBtn.text()).toContain('1')

    // Default active group is parser_error (count 2); verify highlighted code frame line + raw blocks
    const frameView = codePane.get('[data-testid="code-frame-view"]')
    const hiLine = frameView.get('[data-testid="code-frame-highlight-line"]')
    expect(hiLine.text()).toContain('rg -n total src/{invoice.ts,totals.ts}')
    expect(codePane.get('[data-testid="code-raw-script"]').text()).toContain('rg -n total src/{invoice.ts,totals.ts}')
    expect(codePane.get('[data-testid="code-raw-feedback"]').text()).toContain('ParserError:')

    // Click all 4 remediation direction cards -> each invokes triggerDiagnoseInComposer and emits diagnose-draft
    const remediationKeys = ['fix_impl', 'recover_observe', 'add_capability', 'keep_reusable'] as const
    for (const key of remediationKeys) {
      await codePane.get(`[data-testid="code-remediation-${key}"]`).trigger('click')
      await flushPromises()
    }

    expect(diagnoseSpy).toHaveBeenCalledTimes(4)
    for (let i = 0; i < remediationKeys.length; i++) {
      const prompt = diagnoseSpy.mock.calls[i]![0].proposal?.composer_prompt ?? ''
      expect(prompt).toContain('run-w2-code')
      expect(prompt).toContain('PowerShell')
      expect(prompt).toContain('L1')
      expect(prompt).toContain('rg -n total src/{invoice.ts,totals.ts}')
    }
    expect(wrapper.emitted('diagnose-draft')?.length).toBe(4)

    // Filter by executor = Bash -> narrows KPIs to 2 calls / 1 error and switches group to verification_failed
    await codePane.get('[data-testid="code-executor-filter"]').setValue('Bash')
    expect(codePane.get('[data-testid="code-kpi-total"]').text()).toBe('2')
    expect(codePane.get('[data-testid="code-kpi-errors"]').text()).toBe('1')
    expect(codePane.get('[data-testid="code-frame-highlight-line"]').text()).toContain('expect(computeInvoiceTotal()).toBe(20)')
  })

  it('Wave 2 Run Eval: renders scatter SVG, supports two-turn A/B comparison & Composer ask, keeps bucket chart, and exports offline HTML', async () => {
    const diagnoseSpy = vi
      .spyOn(offlineReport, 'triggerDiagnoseInComposer')
      .mockResolvedValue({ draft: 'eval-compare-draft', navigated: false })
    const createUrlSpy = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:eval-report')
    const revokeUrlSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})

    const snapshot = makeMultiTurnSnapshot('run-w2-eval')
    const modelInvocations: ModelInvocationDto[] = [
      {
        id: 'inv-1',
        run_id: 'run-w2-eval',
        session_id: 'sess-w2',
        call_id: 'call-1',
        agent_instance_id: 'agent-1',
        protocol: 'openai_responses',
        model: 'gpt-5',
        status: 'completed',
        attempt: 1,
        input_tokens: 4200,
        output_tokens: 380,
        total_tokens: 4580,
        started_at: '2026-11-25T07:00:01.000Z',
        completed_at: '2026-11-25T07:00:03.200Z',
      } as ModelInvocationDto,
      {
        id: 'inv-2',
        run_id: 'run-w2-eval',
        session_id: 'sess-w2',
        call_id: 'call-2',
        agent_instance_id: 'agent-2',
        protocol: 'openai_responses',
        model: 'gpt-5',
        status: 'completed',
        attempt: 1,
        input_tokens: 6800,
        output_tokens: 1450,
        total_tokens: 8250,
        started_at: '2026-11-25T07:00:06.500Z',
        completed_at: '2026-11-25T07:00:11.800Z',
      } as ModelInvocationDto,
    ]
    const execs: ToolExecutionTimelineItemDto[] = [
      makeToolExec({
        id: 'ex-eval-ok',
        run_id: 'run-w2-eval',
        task_id: 'task-1',
        agent_instance_id: 'agent-1',
        tool_id: 'read_file',
        duration_ms: 320,
      }),
      makeToolExec({
        id: 'ex-eval-err',
        run_id: 'run-w2-eval',
        task_id: 'task-2',
        agent_instance_id: 'agent-2',
        tool_id: 'pwsh',
        status: 'failed',
        tool_success: false,
        duration_ms: 2400,
        command: 'pnpm test',
        summary: '1 test failed',
      }),
    ]

    const wrapper = mount(TrajectoryWorkbenchPanel, {
      props: {
        sessionId: 'sess-w2',
        runs: [{ id: 'run-w2-eval', status: 'failed', created_at: '2026-11-25T07:00:00.000Z' }],
        activeRunId: 'run-w2-eval',
        orchestration: snapshot,
        toolExecutions: execs,
        modelInvocations,
      },
      global: { plugins: [makeI18n()] },
    })
    await flushPromises()

    await wrapper.get('[data-testid="trajectory-tab-eval"]').trigger('click')
    const evalPane = wrapper.get('[data-testid="trajectory-pane-eval"]')

    expect(evalPane.find('[data-testid="eval-summary-strip"]').exists()).toBe(true)
    expect(evalPane.find('[data-testid="eval-scatter-svg"]').exists()).toBe(true)
    expect(evalPane.find('[data-testid="eval-compare-card"]').exists()).toBe(true)

    // Scatter points exist and support click selection
    const pt1 = evalPane.find('[data-testid="eval-scatter-point-1"]')
    const pt2 = evalPane.find('[data-testid="eval-scatter-point-2"]')
    expect(pt1.exists()).toBe(true)
    expect(pt2.exists()).toBe(true)
    await pt1.trigger('click')

    // Compare diagnostic action injects into Composer & bubbles diagnose-draft
    await evalPane.get('[data-testid="eval-ask-compare"]').trigger('click')
    await flushPromises()
    expect(diagnoseSpy).toHaveBeenCalled()
    expect(wrapper.emitted('diagnose-draft')).toBeTruthy()

    // 5% context window bucket chart remains intact inside Eval pane
    expect(evalPane.find('[data-testid="bucket-peak"]').exists()).toBe(true)

    // Export offline HTML report from Eval pane
    await evalPane.get('[data-testid="eval-export-report-btn"]').trigger('click')
    const exportEvents = wrapper.emitted('export-report')
    expect(exportEvents).toBeTruthy()
    expect(exportEvents![0]![0]).toMatchObject({
      filename: expect.stringContaining('run-w2-eval'),
    })

    createUrlSpy.mockRestore()
    revokeUrlSpy.mockRestore()
  })
})
