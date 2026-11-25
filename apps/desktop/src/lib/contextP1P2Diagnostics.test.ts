// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { createI18n } from 'vue-i18n'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/components/ui', () => ({
  UiBadge: defineComponent({
    name: 'UiBadge',
    setup(_, { slots, attrs }) {
      return () => h('span', attrs, slots.default?.())
    },
  }),
  UiButton: defineComponent({
    name: 'UiButton',
    props: { disabled: Boolean },
    emits: ['click'],
    setup(props, { slots, attrs, emit }) {
      return () =>
        h(
          'button',
          { ...attrs, type: 'button', disabled: props.disabled, onClick: (e: MouseEvent) => emit('click', e) },
          slots.default?.(),
        )
    },
  }),
  UiInput: defineComponent({
    name: 'UiInput',
    props: { modelValue: String, disabled: Boolean, placeholder: String },
    emits: ['update:modelValue'],
    setup(props, { attrs, emit }) {
      return () =>
        h('input', {
          ...attrs,
          value: props.modelValue ?? '',
          disabled: props.disabled,
          placeholder: props.placeholder,
          onInput: (e: Event) => emit('update:modelValue', (e.target as HTMLInputElement).value),
        })
    },
  }),
  UiLabel: defineComponent({
    name: 'UiLabel',
    setup(_, { slots, attrs }) {
      return () => h('label', attrs, slots.default?.())
    },
  }),
  UiSelectField: defineComponent({
    name: 'UiSelectField',
    setup(_, { slots, attrs }) {
      return () => h('select', attrs, slots.default?.())
    },
  }),
  UiSwitch: defineComponent({
    name: 'UiSwitch',
    setup(_, { attrs }) {
      return () => h('input', { ...attrs, type: 'checkbox' })
    },
  }),
  UiTextarea: defineComponent({
    name: 'UiTextarea',
    setup(_, { attrs }) {
      return () => h('textarea', attrs)
    },
  }),
}))
import en from '@/locales/en'
import {
  api,
  type ContextEvolutionProposalDto,
  type ContextOverlapAuditDto,
  type OrchestrationSnapshotDto,
  type RunContextDiagnosticsDto,
} from '@/api'
import {
  buildOverlapCacheScopeKey,
  clearContextDiagnosticsCache,
  fetchOverlapAuditWithCache,
  fetchRunDiagnosticsWithCache,
  getCachedRunDiagnostics,
  saveCachedRunDiagnostics,
} from '@/lib/contextDiagnosticsCache'
import { setHostAccessStatus } from '@/lib/hostAccess'
import { setSelectedStorage } from '@/lib/storageScope'
import { fromCoreOverlapAuditDto } from '@/lib/contextOverlapWinnowing'
import { computeSquarifiedTreemap, resolveContextTreemapModel } from '@/lib/contextTreemapLayout'
import { buildTrajectoryTree, resolveContextWindowBuckets } from '@/lib/trajectoryBuilder'
import { buildDiagnosticDraft, buildOfflineDiagnosticHtml } from '@/lib/offlineDiagnosticReport'
import ContextOverlapPanel from '@/components/trajectory/ContextOverlapPanel.vue'
import TrajectoryWorkbenchPanel from '@/components/trajectory/TrajectoryWorkbenchPanel.vue'
import { __resetObservabilityRunSelectionForTests } from '@/composables/useObservabilityRunSelection'
import MemoryContextSection from '@/settings/sections/MemoryContextSection.vue'
import { contextMemoryApi } from '@/lib/contextMemoryApi'
import { companionApi } from '@/lib/companionApi'

function makeI18n() {
  return createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false,
  })
}

function makeCoreRunDiagnostics(runId = 'run-p1-1'): RunContextDiagnosticsDto {
  return {
    run_id: runId,
    session_id: 'sess-p1-1',
    status: 'completed',
    mode_version_id: 'mode-v1',
    generated_at: '2026-11-24T00:30:00.000Z',
    window_buckets: {
      buckets: Array.from({ length: 20 }, (_, idx) => ({
        index: idx,
        range_start: idx * 5,
        range_end: (idx + 1) * 5,
        label: `${idx * 5}-${(idx + 1) * 5}%`,
        sample_count: idx === 15 ? 3 : idx === 4 ? 2 : 0,
        error_count: idx === 15 ? 2 : 0,
        error_rate: idx === 15 ? 0.667 : 0,
        avg_latency_ms: idx === 15 ? 4200 : idx === 4 ? 900 : 0,
        max_latency_ms: idx === 15 ? 5100 : idx === 4 ? 1100 : 0,
        avg_occupancy_pct: idx === 15 ? 77.4 : idx === 4 ? 22.1 : 0,
      })),
      samples: [],
      total_samples: 5,
      total_errors: 2,
      overall_error_rate: 0.4,
      avg_latency_ms: 2880,
      peak_occupancy_pct: 78.5,
      low_occupancy_error_rate: 0,
      high_occupancy_error_rate: 0.667,
      low_occupancy_avg_latency_ms: 900,
      high_occupancy_avg_latency_ms: 4200,
      degradation_detected: true,
      degradation_reason_codes: ['HIGH_OCCUPANCY_ERROR_SPIKE', 'HIGH_OCCUPANCY_LATENCY_SPIKE'],
    },
    compaction: {
      selected_pack_id: 'pack-core-1',
      pack_count: 1,
      total_dropped_tokens_across_packs: 1850,
      critical_dropped_sources: ['workspace_instructions'],
      root: {
        id: 'root',
        label: 'Context (Planner)',
        category: 'other',
        source_key: null,
        depth: 0,
        retained_tokens: 7600,
        dropped_tokens: 1850,
        retained_bytes_estimate: 30400,
        dropped_bytes_estimate: 7400,
        share_percent: 100,
        budget_share_percent: 76,
        item_count: 3,
        dropped_item_count: 1,
        items: [
          { id: 'i1', source: 'system_prompt', tokens: 2600, bytes_estimate: 10400, dropped: false },
          { id: 'i2', source: 'reviewed_memory', tokens: 5000, bytes_estimate: 20000, dropped: false },
          { id: 'i3', source: 'workspace_instructions', tokens: 1850, bytes_estimate: 7400, dropped: true },
        ],
        children: [
          {
            id: 'cat:memory',
            label: 'Memory (L2 / L3)',
            category: 'memory',
            source_key: 'reviewed_memory',
            depth: 1,
            retained_tokens: 5000,
            dropped_tokens: 0,
            retained_bytes_estimate: 20000,
            dropped_bytes_estimate: 0,
            share_percent: 65.8,
            budget_share_percent: 50,
            item_count: 1,
            dropped_item_count: 0,
            items: [{ id: 'i2', source: 'reviewed_memory', tokens: 5000, bytes_estimate: 20000, dropped: false }],
            children: [],
          },
          {
            id: 'cat:system_prompt',
            label: 'System Prompt',
            category: 'system_prompt',
            source_key: 'system_prompt',
            depth: 1,
            retained_tokens: 2600,
            dropped_tokens: 0,
            retained_bytes_estimate: 10400,
            dropped_bytes_estimate: 0,
            share_percent: 34.2,
            budget_share_percent: 26,
            item_count: 1,
            dropped_item_count: 0,
            items: [{ id: 'i1', source: 'system_prompt', tokens: 2600, bytes_estimate: 10400, dropped: false }],
            children: [],
          },
        ],
      },
      reconciliation: {
        pack_id: 'pack-core-1',
        lane_key: null,
        created_at: '2026-11-24T00:29:00.000Z',
        token_budget: 10000,
        retained_tokens: 7600,
        dropped_tokens: 1850,
        pre_compaction_tokens: 9450,
        retained_bytes_estimate: 30400,
        dropped_bytes_estimate: 7400,
        utilization_percent: 76,
        retained_ratio_percent: 80.4,
        dropped_ratio_percent: 19.6,
        has_pricing_data: true,
        has_compaction_digest: false,
        previous_pack_retained_delta: null,
        previous_pack_dropped_delta: null,
      },
      packs: [
        {
          pack_id: 'pack-core-1',
          lane_key: null,
          created_at: '2026-11-24T00:29:00.000Z',
          token_budget: 10000,
          retained_tokens: 7600,
          dropped_tokens: 1850,
          pre_compaction_tokens: 9450,
          retained_bytes_estimate: 30400,
          dropped_bytes_estimate: 7400,
          utilization_percent: 76,
          retained_ratio_percent: 80.4,
          dropped_ratio_percent: 19.6,
          has_pricing_data: true,
          has_compaction_digest: false,
          previous_pack_retained_delta: null,
          previous_pack_dropped_delta: null,
        },
      ],
    },
    governance: {
      signal_counts: {
        SAME_ARGS_RETRY: 3,
        CONTEXT_PRESSURE: 1,
        LOOP_DETECTED: 1,
      },
      active_signals: ['SAME_ARGS_RETRY', 'LOOP_DETECTED', 'CONTEXT_PRESSURE'],
      signals: [
        { code: 'CONTEXT_PRESSURE', severity: 'warn', count: 1, detail: '78.5% occupancy with dropped instructions', subject_ids: ['exec-1'] },
        { code: 'LOOP_DETECTED', severity: 'danger', count: 1, detail: 'Repeated stale edit cycle', subject_ids: ['exec-1'] },
      ],
      max_same_args_retry_streak: 3,
      retry_streaks: [
        { execution_id: 'exec-1', tool_id: 'edit_file', scope_key: 'agent-1', streak_count: 3, arguments_summary: 'src/index.ts' },
      ],
      total_tool_calls: 4,
      total_model_calls: 5,
      error_count: 2,
      total_added_lines: 22,
      total_removed_lines: 7,
      file_edits: [{ execution_id: 'exec-1', tool_id: 'edit_file', file_path: 'src/index.ts', added: 22, removed: 7 }],
      error_frames: [],
    },
    executive_findings: [
      {
        code: 'CONTEXT_ROT_HIGH_WATERMARK',
        severity: 'warn',
        summary: 'Error rate jumped from 0% (<70%) to 66.7% (>=70% occupancy) while workspace_instructions was dropped.',
        evidence_refs: ['pack-core-1', 'exec-1'],
        suggested_action: 'Lower compaction_trigger_ratio from 0.75 to 0.65.',
      },
    ],
  }
}

function makeCoreOverlapAudit(): ContextOverlapAuditDto {
  return {
    scope: 'static_config',
    project_id: 'proj-1',
    k_gram_size: 64,
    window_size: 16,
    source_count: 4,
    total_estimated_tokens: 1800,
    duplicate_estimated_tokens: 310,
    overall_overlap_percent: 17.2,
    generated_at: '2026-11-24T00:31:00.000Z',
    pairs: [
      {
        id: 'agent:coord__skill:git-guard',
        source_a_id: 'agent:coord',
        source_a_label: 'Coordinator System Prompt',
        source_a_kind: 'system_prompt',
        source_b_id: 'skill:git-guard',
        source_b_label: 'git-guard',
        source_b_kind: 'skill',
        shared_chars: 420,
        overlap_percent: 54.5,
        overlap_percent_a: 32.0,
        overlap_percent_b: 54.5,
        estimated_duplicate_tokens: 310,
        match_count: 1,
        remediation_code: 'move_to_skill_references',
        remediation_summary: 'Move repeated procedural rules into SKILL.md references/.',
        matches: [
          {
            id: 'm1',
            source_a_id: 'agent:coord',
            source_a_label: 'Coordinator System Prompt',
            source_a_kind: 'system_prompt',
            source_b_id: 'skill:git-guard',
            source_b_label: 'git-guard',
            source_b_kind: 'skill',
            source_a_lines: { start: 12, end: 24 },
            source_b_lines: { start: 4, end: 16 },
            line_range_pair: 'L12-L24 <-> L4-L16',
            shared_chars: 420,
            estimated_duplicate_tokens: 310,
            excerpt: 'Never run git add -A or git push without explicit user instruction...',
            remediation_code: 'move_to_skill_references',
            remediation_summary: 'Move repeated procedural rules into SKILL.md references/.',
          },
        ],
      },
    ],
    matches: [],
    sources: [
      {
        id: 'agent:coord',
        label: 'Coordinator System Prompt',
        kind: 'system_prompt',
        path: null,
        char_count: 1300,
        line_count: 35,
        estimated_tokens: 340,
        duplicated_chars: 420,
        overlap_percent: 32.0,
      },
      {
        id: 'skill:git-guard',
        label: 'git-guard',
        kind: 'skill',
        path: '.agents/skills/git-guard/SKILL.md',
        char_count: 770,
        line_count: 22,
        estimated_tokens: 210,
        duplicated_chars: 420,
        overlap_percent: 54.5,
      },
    ],
  }
}

describe('P1 Scoped Local Snapshot Cache & Offline Resilience', () => {
  beforeEach(() => {
    clearContextDiagnosticsCache()
    setSelectedStorage('user')
    setHostAccessStatus({ state: 'ready', webAuth: false } as never)
  })

  afterEach(() => {
    vi.restoreAllMocks()
    clearContextDiagnosticsCache()
    setHostAccessStatus({ state: 'ready', webAuth: false } as never)
  })

  it('persists RunContextDiagnosticsDto & ContextOverlapAuditDto per storage scope and degrades gracefully when Core disconnects', async () => {
    const dto = makeCoreRunDiagnostics('run-offline-1')

    const liveRes = await fetchRunDiagnosticsWithCache(
      'run-offline-1',
      async () => dto,
      { storageId: 'scope-alpha' },
    )
    expect(liveRes.stale).toBe(false)
    expect(liveRes.fromCache).toBe(false)
    expect(liveRes.data?.run_id).toBe('run-offline-1')

    // Scope isolation: scope-beta cannot see scope-alpha's cached run
    expect(getCachedRunDiagnostics('run-offline-1', { storageId: 'scope-beta' })).toBeNull()
    expect(getCachedRunDiagnostics('run-offline-1', { storageId: 'scope-alpha' })?.data.run_id).toBe('run-offline-1')

    // Simulate transient Core/Gateway disconnect (502 / network failure + host error state)
    setHostAccessStatus({
      state: 'error',
      webAuth: false,
      error: { code: 'CORE_UNREACHABLE', message: 'connect ECONNREFUSED' },
    } as never)

    const fallbackRes = await fetchRunDiagnosticsWithCache(
      'run-offline-1',
      async () => {
        throw new Error('502 Bad Gateway: CORE_UNREACHABLE')
      },
      { storageId: 'scope-alpha' },
    )
    expect(fallbackRes.data).not.toBeNull()
    expect(fallbackRes.fromCache).toBe(true)
    expect(fallbackRes.stale).toBe(true)
    expect(fallbackRes.data?.window_buckets.peak_occupancy_pct).toBe(78.5)

    // Overlap cache resilience
    const scopeKey = buildOverlapCacheScopeKey({ projectId: 'proj-1', autoLoadMode: 'all' })
    await fetchOverlapAuditWithCache(scopeKey, async () => makeCoreOverlapAudit(), { storageId: 'scope-alpha' })
    const offlineOverlap = await fetchOverlapAuditWithCache(
      scopeKey,
      async () => {
        throw new TypeError('Failed to fetch')
      },
      { storageId: 'scope-alpha' },
    )
    expect(offlineOverlap.stale).toBe(true)
    expect(offlineOverlap.fromCache).toBe(true)
    expect(offlineOverlap.data?.duplicate_estimated_tokens).toBe(310)

    // Explicit web-auth revocation purges cached snapshots immediately
    setHostAccessStatus({ state: 'auth_required', webAuth: true } as never)
    expect(getCachedRunDiagnostics('run-offline-1', { storageId: 'scope-alpha' })).toBeNull()
  })

  it('wires Core authoritative compaction, 5% buckets, and 9 governance signals into Treemap, Trajectory, and Offline HTML', () => {
    const diag = makeCoreRunDiagnostics('run-core-truth')
    const treemapModel = resolveContextTreemapModel({ diagnostics: diag, contextPacks: [] })

    expect(treemapModel.reconciliation.retainedTokens).toBe(7600)
    expect(treemapModel.reconciliation.droppedTokens).toBe(1850)
    expect(treemapModel.root.children).toHaveLength(2)

    // Pure 2D Squarified Treemap layout still fills bounds without overlap
    const tiles = computeSquarifiedTreemap(
      treemapModel.root.children.map((c) => ({ id: c.id, value: c.retainedTokens })),
      { x: 0, y: 0, width: 240, height: 120 },
    )
    expect(tiles).toHaveLength(2)
    const areaSum = tiles.reduce((s, r) => s + r.width * r.height, 0)
    expect(areaSum).toBeCloseTo(240 * 120, 3)

    const buckets = resolveContextWindowBuckets({ diagnostics: diag })
    expect(buckets.peakOccupancyPct).toBe(78.5)
    expect(buckets.degradationDetected).toBe(true)
    expect(buckets.buckets[15]!.sampleCount).toBe(3)

    const snapshot: OrchestrationSnapshotDto = {
      run: {
        id: 'run-core-truth',
        session_id: 'sess-p1-1',
        status: 'completed',
        summary: 'Diagnose context pressure',
        created_at: '2026-11-24T00:28:00.000Z',
        updated_at: '2026-11-24T00:30:00.000Z',
      },
      nodes: [],
      lanes: [],
      assignments: [],
      step_results: [],
      context_packs: [],
      supervision_findings: [],
    }
    const traj = buildTrajectoryTree({ snapshot, toolExecutions: [], diagnostics: diag })
    expect(traj.activeSignals).toEqual(
      expect.arrayContaining(['SAME_ARGS_RETRY', 'LOOP_DETECTED', 'CONTEXT_PRESSURE']),
    )
    expect(traj.totalAddedLines).toBe(22)
    expect(traj.totalRemovedLines).toBe(7)

    const overlap = fromCoreOverlapAuditDto(makeCoreOverlapAudit())
    const draft = buildDiagnosticDraft({
      diagnostics: diag,
      trajectory: traj,
      treemap: treemapModel,
      buckets,
      overlap,
      staleSnapshot: { cachedAt: '2026-11-24T00:30:00.000Z' },
    })
    expect(draft).toContain('run_diagnostics')
    expect(draft).toContain('context_audit')
    expect(draft).toContain('CONTEXT_ROT_HIGH_WATERMARK')
    expect(draft).toContain('cached local snapshot')

    const html = buildOfflineDiagnosticHtml({
      diagnostics: diag,
      trajectory: traj,
      treemap: treemapModel,
      buckets,
      overlap,
      staleSnapshot: { cachedAt: '2026-11-24T00:30:00.000Z' },
    })
    expect(html).toContain('CONTEXT_ROT_HIGH_WATERMARK')
    expect(html).toContain('cached (2026-11-24T00:30:00.000Z)')
    expect(html).not.toContain('<script>')
  })
})

describe('P1 Component Upgrades & Zero-Blank-Screen Offline Fallback', () => {
  beforeEach(() => {
    clearContextDiagnosticsCache()
    __resetObservabilityRunSelectionForTests()
    setSelectedStorage('user')
    setHostAccessStatus({ state: 'ready', webAuth: false } as never)
  })

  afterEach(() => {
    vi.restoreAllMocks()
    clearContextDiagnosticsCache()
    __resetObservabilityRunSelectionForTests()
  })

  it('ContextOverlapPanel calls Core POST /api/v1/context/audit-overlap without 24x getToolSkill loop and falls back to cache offline', async () => {
    const auditSpy = vi.spyOn(api, 'auditContextOverlap').mockResolvedValueOnce(makeCoreOverlapAudit())
    const getSkillSpy = vi.spyOn(api, 'getToolSkill')

    const wrapper = mount(ContextOverlapPanel, {
      props: {
        autoLoadMode: 'all',
        projectId: 'proj-1',
        canAccessBackend: true,
      },
      global: { plugins: [makeI18n()] },
    })
    await flushPromises()

    expect(auditSpy).toHaveBeenCalledTimes(1)
    expect(getSkillSpy).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="overlap-line-range"]').text()).toBe('L12-L24 <-> L4-L16')
    expect(wrapper.find('[data-testid="overlap-cached-badge"]').exists()).toBe(false)

    // Now simulate backend failure on rescan -> falls back to cached snapshot without blank screen
    auditSpy.mockRejectedValueOnce(new Error('connect ECONNREFUSED'))
    await wrapper.get('[data-testid="overlap-rescan-btn"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="overlap-cached-badge"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="overlap-pair-list"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="overlap-line-range"]').text()).toBe('L12-L24 <-> L4-L16')
  })

  it('TrajectoryWorkbenchPanel renders cached RunContextDiagnosticsDto when backend disconnects (even with null orchestration snapshot) and exports offline HTML', async () => {
    saveCachedRunDiagnostics('run-cached-99', makeCoreRunDiagnostics('run-cached-99'))
    vi.spyOn(api, 'getRunContextDiagnostics').mockRejectedValue(new Error('502 CORE_UNREACHABLE'))
    vi.spyOn(api, 'auditContextOverlap').mockRejectedValue(new Error('502 CORE_UNREACHABLE'))
    vi.spyOn(api, 'listModelInvocations').mockRejectedValue(new Error('502 CORE_UNREACHABLE'))
    vi.spyOn(api, 'getSessionTopology').mockRejectedValue(new Error('502 CORE_UNREACHABLE'))
    vi.spyOn(api, 'listToolExecutions').mockRejectedValue(new Error('502 CORE_UNREACHABLE'))

    const wrapper = mount(TrajectoryWorkbenchPanel, {
      props: {
        orchestration: null,
        activeRunId: 'run-cached-99',
        toolExecutions: [],
      },
      global: {
        plugins: [makeI18n()],
      },
    })
    await flushPromises()

    // Zero blank screen: cached diagnostics workbench renders stale banner + executive findings + composition bar
    expect(wrapper.find('[data-testid="trajectory-workbench-panel"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="diagnostics-offline-banner"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="composition-cached-badge"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="diagnostics-executive-findings"]').text()).toContain('CONTEXT_ROT_HIGH_WATERMARK')

    // Switch to Eval tab while offline -> renders Core cached 5% occupancy buckets cleanly
    await wrapper.get('[data-testid="trajectory-tab-eval"]').trigger('click')
    expect(wrapper.find('[data-testid="bucket-cached-badge"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="bucket-peak"]').text()).toContain('78.5%')

    // Switch to Context Matrix (Treemap) tab while offline -> renders Core cached compaction categories & dropped badge cleanly
    await wrapper.get('[data-testid="trajectory-tab-context"]').trigger('click')
    expect(wrapper.find('[data-testid="treemap-cached-badge"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="treemap-dropped-badge"]').text()).toContain('1,850')

    // Offline HTML export works while disconnected
    const createUrlSpy = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:offline-report')
    const revokeUrlSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
    await wrapper.get('[data-testid="workbench-export-btn"]').trigger('click')

    const exportedEvents = wrapper.emitted('export-report')
    expect(exportedEvents).toBeTruthy()
    expect(exportedEvents![0]![0]).toMatchObject({
      filename: expect.stringContaining('run-cached-99'),
    })
    createUrlSpy.mockRestore()
    revokeUrlSpy.mockRestore()
    wrapper.unmount()
  })
})

describe('P2 Self-Evolution Proposal Review UI (MemoryContextSection.vue)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('displays pending policy & prompt-dedup proposals with run_id evidence, before/after metrics, Apply & Write, Reject, and Composer deep-dive', async () => {
    const policyProposal: ContextEvolutionProposalDto = {
      id: 'prop-policy-1',
      kind: 'context_policy_proposal',
      status: 'pending',
      target_scope: 'global',
      title: 'Lower compaction_trigger_ratio to prevent >=70% context rot',
      summary: '3 of last 5 runs exhibited high-occupancy error spikes.',
      rationale: 'Triggering async compaction earlier preserves workspace_instructions.',
      confidence: 0.88,
      risk_level: 'low',
      requires_human_approval: true,
      evidence_run_ids: ['11111111-aaaa-bbbb-cccc-000000000001', '22222222-aaaa-bbbb-cccc-000000000002'],
      policy_patch: { compaction_trigger_ratio: 0.65 },
      metrics_comparison: {
        window_run_count: 5,
        degraded_run_count: 3,
        degradation_rate: 0.6,
        dropped_critical_run_count: 2,
        avg_peak_occupancy_pct: 81.4,
        high_occupancy_error_rate: 0.55,
        low_occupancy_error_rate: 0.08,
        high_occupancy_avg_latency_ms: 4100,
        low_occupancy_avg_latency_ms: 1100,
        dropped_sources: ['workspace_instructions'],
        before_values: { compaction_trigger_ratio: '0.75' },
        after_values: { compaction_trigger_ratio: '0.65' },
        expected_benefit: 'Avoid >=70% context rot and keep AGENTS.md retained.',
      },
      composer_prompt: 'Inspect prop-policy-1 with tinadec_inspect (run_diagnostics, context_memory).',
      created_at: '2026-11-24T00:40:00.000Z',
      updated_at: '2026-11-24T00:40:00.000Z',
    }

    const dedupProposal: ContextEvolutionProposalDto = {
      id: 'prop-dedup-2',
      kind: 'prompt_dedup_proposal',
      status: 'pending',
      target_scope: 'workspace',
      title: 'Deduplicate git safety prose between Coordinator prompt and git-guard skill',
      summary: '290 wasted tokens per turn across L12-L24 <-> L4-L16.',
      rationale: 'Move checklist into on-demand SKILL.md reference.',
      confidence: 0.92,
      risk_level: 'low',
      requires_human_approval: true,
      evidence_run_ids: ['33333333-aaaa-bbbb-cccc-000000000003'],
      remediation_code: 'move_to_skill_references',
      dedup_evidence: {
        source_a_id: 'agent:coord',
        source_a_label: 'Coordinator Prompt',
        source_a_kind: 'system_prompt',
        source_a_start_line: 12,
        source_a_end_line: 24,
        source_b_id: 'skill:git-guard',
        source_b_label: 'git-guard',
        source_b_kind: 'skill',
        source_b_start_line: 4,
        source_b_end_line: 16,
        line_range_label: 'L12-L24 <-> L4-L16',
        overlap_chars: 410,
        estimated_wasted_tokens: 290,
        remediation_code: 'move_to_skill_references',
        excerpt: 'Never run git add -A or git push...',
        suggested_remediation: 'Keep a 1-line skill pointer in system prompt.',
      },
      created_at: '2026-11-24T00:41:00.000Z',
      updated_at: '2026-11-24T00:41:00.000Z',
    }

    vi.spyOn(api, 'listProjects').mockResolvedValue([])
    vi.spyOn(companionApi, 'listCompanions').mockResolvedValue([])
    vi.spyOn(contextMemoryApi, 'getPolicy').mockResolvedValue({
      strategy: 'managed',
      recall_fidelity: 'balanced',
      compaction_trigger_ratio: 0.75,
      recent_message_limit: 24,
      allow_cross_session_recall: false,
      micro_compact_stale_tool_outputs: true,
      offload_large_tool_payload_bytes: 16384,
      evolution_mode: 'auto_evolve',
      candidate_only: false,
      core_markdown_max_tokens: 1500,
      project_markdown_projection: true,
      project_memory_path: '.tinadec/memory/project-core.md',
      companion_can_write_project_memory: false,
      content_hash: 'hash-v1',
      context: {
        strategy: 'managed',
        recall_fidelity: 'balanced',
        compaction_trigger_ratio: 0.75,
        recent_message_limit: 24,
        allow_cross_session_recall: false,
        micro_compact_stale_tool_outputs: true,
        offload_large_tool_payload_bytes: 16384,
      },
      memory: {
        evolution_mode: 'auto_evolve',
        candidate_only: false,
        core_markdown_max_tokens: 1500,
        project_markdown_projection: true,
        project_memory_path: '.tinadec/memory/project-core.md',
        companion_can_write_project_memory: false,
      },
    })
    vi.spyOn(contextMemoryApi, 'getMemoryDocument').mockResolvedValue({
      scope: 'principal',
      path: 'memory/user-profile.md',
      file_path: 'memory/user-profile.md',
      markdown: '# Profile\n',
      estimated_tokens: 10,
      core_markdown_max_tokens: 1500,
      active_item_count: 0,
      active_hot_count: 0,
      cold_archive_count: 0,
    })
    vi.spyOn(contextMemoryApi, 'listMemoryItems').mockResolvedValue([])

    vi.spyOn(api, 'listContextEvolutionProposals').mockResolvedValue({
      items: [policyProposal, dedupProposal],
      total_count: 2,
      pending_count: 2,
      auto_tune_low_risk_context_policy: false,
    })

    const applySpy = vi.spyOn(api, 'applyContextEvolutionProposal').mockResolvedValue({
      proposal: {
        ...policyProposal,
        status: 'applied',
        applied_toml_path: 'runtime.toml',
      },
      applied: true,
      summary: 'Updated runtime.toml [context].compaction_trigger_ratio = 0.65 with run provenance comment.',
      updated_toml_preview:
        '# [context-evolution prop-policy-1] runs=11111111,22222222 high_err=55%->low_err=8%\ncompaction_trigger_ratio = 0.65',
    })

    const rejectSpy = vi.spyOn(api, 'rejectContextEvolutionProposal').mockResolvedValue({
      ...dedupProposal,
      status: 'rejected',
      decision_reason: 'Intentional safety reminder',
    })

    const focusEvents: string[] = []
    const onFocusComposer = (e: Event) => {
      focusEvents.push((e as CustomEvent<{ draft: string }>).detail.draft)
    }
    window.addEventListener('tinadec:focus-composer', onFocusComposer)

    const wrapper = mount(MemoryContextSection, {
      props: { initialTab: 'evolution' },
      global: { plugins: [makeI18n()] },
    })
    await flushPromises()

    expect(wrapper.find('[data-testid="evolution-pending-badge"]').text()).toBe('2')
    const policyCard = wrapper.get('[data-testid="evolution-proposal-prop-policy-1"]')
    expect(policyCard.get('[data-testid="proposal-before-after"]').text()).toContain('0.75')
    expect(policyCard.get('[data-testid="proposal-before-after"]').text()).toContain('0.65')
    expect(policyCard.findAll('[data-testid="proposal-evidence-run"]')).toHaveLength(2)

    const dedupCard = wrapper.get('[data-testid="evolution-proposal-prop-dedup-2"]')
    expect(dedupCard.get('[data-testid="proposal-dedup-evidence"]').text()).toContain('L12-L24 <-> L4-L16')
    expect(dedupCard.get('[data-testid="proposal-dedup-evidence"]').text()).toContain('290')

    // Composer deep-dive shortcut
    await policyCard.get('[data-testid="proposal-diagnose-btn"]').trigger('click')
    await vi.waitFor(() => {
      expect(focusEvents[0]).toContain('tinadec_inspect')
    })

    // Apply & Write policy proposal -> displays commented TOML receipt
    await policyCard.get('[data-testid="proposal-apply-btn"]').trigger('click')
    await flushPromises()
    expect(applySpy).toHaveBeenCalledWith('prop-policy-1', {
      reason: null,
      expected_content_hash: 'hash-v1',
    })
    expect(policyCard.get('[data-testid="proposal-applied-receipt"]').text()).toContain(
      'compaction_trigger_ratio = 0.65',
    )

    // Reject dedup proposal
    await dedupCard.get('[data-testid="proposal-reject-btn"]').trigger('click')
    await flushPromises()
    expect(rejectSpy).toHaveBeenCalledWith('prop-dedup-2', null)
    expect(dedupCard.text()).toContain('Rejected')

    window.removeEventListener('tinadec:focus-composer', onFocusComposer)
  })
})
