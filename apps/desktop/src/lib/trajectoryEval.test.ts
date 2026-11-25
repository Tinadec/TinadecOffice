// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { describe, expect, it, vi } from 'vitest'
import en from '@/locales/en'
import type {
  ContextPackDto,
  ModelInvocationDto,
  OrchestrationSnapshotDto,
  ToolExecutionTimelineItemDto,
} from '@/api'
import {
  auditContextOverlap,
  formatLineRangePair,
  winnowFingerprints,
  type ContextAuditSource,
} from './contextOverlapWinnowing'
import {
  buildContextTreemapModel,
  computeSquarifiedTreemap,
  findTreemapPath,
} from './contextTreemapLayout'
import {
  buildContextWindowBuckets,
  buildTrajectoryTree,
  detectRepeatedArgsStreaks,
  extractErrorCodeFrames,
  extractFileEditStats,
} from './trajectoryBuilder'
import * as offlineDiagnosticReport from './offlineDiagnosticReport'
import {
  buildDiagnosticDraft,
  buildOfflineDiagnosticHtml,
  escapeHtml,
  exportOfflineDiagnosticHtml,
  triggerDiagnoseInComposer,
} from './offlineDiagnosticReport'
import {
  aggregateTrajectoryEval,
  buildEvalCompareDraft,
  buildEvalLogScale,
  computeEvalCompareDelta,
  EVAL_DURATION_TICK_CANDIDATES,
  EVAL_TOKEN_TICK_CANDIDATES,
  formatEvalTokensWithHonesty,
  pickDefaultEvalComparePair,
} from './trajectoryEval'
import TrajectoryTreePanel from '@/components/trajectory/TrajectoryTreePanel.vue'
import TrajectoryEvalPanel from '@/components/trajectory/TrajectoryEvalPanel.vue'
import ContextWindowBucketChart from '@/components/trajectory/ContextWindowBucketChart.vue'
import ContextTreemap from '@/components/trajectory/ContextTreemap.vue'
import ContextOverlapPanel from '@/components/trajectory/ContextOverlapPanel.vue'

function makeI18n() {
  return createI18n({
    legacy: false,
    locale: 'en',
    messages: { en },
    missingWarn: false,
    fallbackWarn: false,
  })
}

function makeToolExec(overrides: Partial<ToolExecutionTimelineItemDto> = {}): ToolExecutionTimelineItemDto {
  return {
    id: overrides.id ?? 'exec-1',
    run_id: overrides.run_id ?? 'run-1',
    session_id: overrides.session_id ?? 'sess-1',
    tool_id: overrides.tool_id ?? 'edit_file',
    tool_display_name: overrides.tool_display_name ?? 'Edit File',
    source: overrides.source ?? 'builtin',
    provider_layer: overrides.provider_layer ?? 'core',
    risk: overrides.risk ?? 'medium',
    requires_approval: overrides.requires_approval ?? false,
    status: overrides.status ?? 'completed',
    approval_id: overrides.approval_id ?? null,
    step_result_id: overrides.step_result_id ?? null,
    summary: overrides.summary ?? 'Edited src/app.ts (+14/-3)',
    evidence: overrides.evidence ?? [],
    requested_at: overrides.requested_at ?? '2026-11-22T01:00:00.000Z',
    updated_at: overrides.updated_at ?? '2026-11-22T01:00:01.200Z',
    duration_ms: overrides.duration_ms ?? 1200,
    requested_seq: overrides.requested_seq ?? 1,
    updated_seq: overrides.updated_seq ?? 2,
    event_types: overrides.event_types ?? ['tool.requested', 'tool.completed'],
    checkpoint_summary: overrides.checkpoint_summary ?? '',
    tool_success: overrides.tool_success ?? true,
    task_id: overrides.task_id ?? 'task-1',
    agent_instance_id: overrides.agent_instance_id ?? 'agent-1',
    error_category: overrides.error_category ?? null,
    arguments_summary: overrides.arguments_summary ?? null,
    resource_path: overrides.resource_path ?? 'src/app.ts',
    command: overrides.command ?? null,
    cwd: overrides.cwd ?? null,
    query: overrides.query ?? null,
  }
}

function makeInvocation(overrides: Partial<ModelInvocationDto> = {}): ModelInvocationDto {
  return {
    id: overrides.id ?? 'inv-1',
    call_id: overrides.call_id ?? 'call-1',
    attempt: overrides.attempt ?? 1,
    session_id: overrides.session_id ?? 'sess-1',
    run_id: overrides.run_id ?? 'run-1',
    turn_id: overrides.turn_id ?? 'turn-1',
    agent_instance_id: overrides.agent_instance_id ?? 'agent-1',
    agent_definition_id: overrides.agent_definition_id ?? 'def-1',
    agent_version_id: overrides.agent_version_id ?? 'ver-1',
    mode_version_id: overrides.mode_version_id ?? 'mode-1',
    strategy_source: overrides.strategy_source ?? 'route',
    route_id: overrides.route_id ?? 'route-1',
    route_version_id: overrides.route_version_id ?? 'rv-1',
    provider_instance_id: overrides.provider_instance_id ?? 'prov-1',
    provider_version_id: overrides.provider_version_id ?? 'pv-1',
    model: overrides.model ?? 'gpt-5',
    protocol: overrides.protocol ?? 'openai',
    fallback_position: overrides.fallback_position ?? 0,
    status: overrides.status ?? 'completed',
    error_category: overrides.error_category ?? null,
    safe_error_message: overrides.safe_error_message ?? null,
    input_tokens: overrides.input_tokens ?? 10000,
    output_tokens: overrides.output_tokens ?? 1200,
    total_tokens: overrides.total_tokens ?? 11200,
    started_at: overrides.started_at ?? '2026-11-22T01:00:00.000Z',
    completed_at: overrides.completed_at ?? '2026-11-22T01:00:01.000Z',
  }
}

function makeContextPacks(): ContextPackDto[] {
  return [
    {
      id: 'pack-1',
      run_id: 'run-1',
      lane_key: null,
      evidence_count: 6,
      estimated_tokens: 7200,
      token_budget: 10000,
      sources: [
        'system_prompt',
        'workspace_instructions',
        'workspace_skills',
        'reviewed_memory',
        'reviewed_memory',
        'session_history',
      ],
      source_tokens: [
        { source: 'system_prompt', tokens: 1200 },
        { source: 'workspace_instructions', tokens: 1600 },
        { source: 'workspace_skills', tokens: 900 },
        { source: 'reviewed_memory', tokens: 700 },
        { source: 'reviewed_memory', tokens: 500 },
        { source: 'session_history', tokens: 2300 },
      ],
      dropped_sources: [
        { source: 'session_history', tokens: 1400 },
        { source: 'reviewed_memory', tokens: 300 },
      ],
      created_at: '2026-11-22T01:00:00.000Z',
    },
    {
      id: 'pack-2',
      run_id: 'run-1',
      lane_key: 'engineer',
      evidence_count: 4,
      estimated_tokens: 5400,
      token_budget: 10000,
      sources: ['system_prompt', 'session_compaction', 'workspace_instructions', 'tool'],
      source_tokens: [
        { source: 'system_prompt', tokens: 1200 },
        { source: 'session_compaction', tokens: 1100 },
        { source: 'workspace_instructions', tokens: 1600 },
        { source: 'tool', tokens: 1500 },
      ],
      dropped_sources: [],
      created_at: '2026-11-22T01:02:00.000Z',
    },
  ]
}

function makeSnapshot(): OrchestrationSnapshotDto {
  return {
    run: {
      id: 'run-1',
      session_id: 'sess-1',
      status: 'failed',
      summary: 'Refactor auth middleware and verify tests',
      created_at: '2026-11-22T01:00:00.000Z',
      updated_at: '2026-11-22T01:03:00.000Z',
    },
    nodes: [
      {
        id: 'task-1',
        graph_id: 'g-1',
        run_id: 'run-1',
        session_id: 'sess-1',
        title: 'Patch auth guard',
        description: 'Update token validation',
        status: 'failed',
        lane_key: 'engineer',
        priority: 1,
        risk: 'medium',
        success_criteria: ['Tests pass'],
        dependencies: [],
        required_capabilities: ['code'],
        created_at: '2026-11-22T01:00:01.000Z',
        updated_at: '2026-11-22T01:02:30.000Z',
      },
    ],
    lanes: [],
    assignments: [
      {
        id: 'assign-1',
        run_id: 'run-1',
        task_node_id: 'task-1',
        agent_id: 'agent-1',
        agent_name: 'Engineer',
        agent_layer: 'execution',
        agent_type: 'worker',
        model_route_purpose: 'coding',
        permission_mode: 'default',
        allowed_tools: ['read_file', 'edit_file', 'shell'],
        status: 'failed',
        created_at: '2026-11-22T01:00:01.000Z',
      },
    ],
    step_results: [
      {
        id: 'step-1',
        run_id: 'run-1',
        task_node_id: 'task-1',
        agent_id: 'agent-1',
        status: 'completed',
        summary: 'Located auth middleware in src/auth.ts',
        evidence: ['src/auth.ts:12'],
        created_at: '2026-11-22T01:00:05.000Z',
      },
    ],
    context_packs: makeContextPacks(),
    supervision_findings: [],
  }
}

describe('64-Char Winnowing Context Overlap Audit', () => {
  const sharedRule =
    'Always read the exact target file with read_file before editing, verify line hashes against stale versions, and run scoped unit tests prior to completion.'

  it('produces deterministic rolling-hash fingerprints and detects >=64-char overlaps with exact L1 <-> L2 line ranges', () => {
    const fps = winnowFingerprints(sharedRule.toLowerCase(), 64, 16)
    expect(fps.length).toBeGreaterThan(0)

    const sources: ContextAuditSource[] = [
      {
        id: 'sys-1',
        label: 'Coordinator System Prompt',
        kind: 'system_prompt',
        content: [
          'You are the primary coordinator.',
          '',
          'Core execution rule:',
          sharedRule,
          'Keep replies concise.',
        ].join('\n'),
      },
      {
        id: 'skill-1',
        label: 'safe-code-edit',
        kind: 'skill',
        content: [
          '# Safe Code Edit Skill',
          `  ${sharedRule.toUpperCase()}  `,
          'Extra checklist items unique to this skill.',
        ].join('\n'),
      },
      {
        id: 'agents-md',
        label: 'AGENTS.md',
        kind: 'workspace_instructions',
        content: [
          'Repository layout overview.',
          'Build with bun run build.',
          'No overlapping boilerplate here at all.',
        ].join('\n'),
      },
    ]

    const result = auditContextOverlap(sources, { kGramSize: 64, windowSize: 16 })
    expect(result.kGramSize).toBe(64)
    expect(result.sourceCount).toBe(3)
    expect(result.pairs).toHaveLength(1)

    const pair = result.pairs[0]!
    expect(pair.sourceAId).toBe('sys-1')
    expect(pair.sourceBId).toBe('skill-1')
    expect(pair.sharedChars).toBeGreaterThanOrEqual(sharedRule.length - 2)
    expect(pair.overlapPercent).toBeGreaterThan(40)
    expect(pair.remediationCode).toBe('move_to_skill_references')
    expect(pair.remediationSummary).toContain('SKILL.md')

    const match = pair.matches[0]!
    expect(match.sourceALines).toEqual({ start: 4, end: 4 })
    expect(match.sourceBLines).toEqual({ start: 2, end: 2 })
    expect(formatLineRangePair(match.sourceALines, match.sourceBLines)).toBe('L4 <-> L2')
  })

  it('ignores passages shorter than k=64 and recommends AGENTS.md deduplication when workspace instructions overlap', () => {
    const shortShared = 'Always read files before editing them carefully.'
    expect(shortShared.length).toBeLessThan(64)

    const clean = auditContextOverlap([
      { id: 'a', label: 'A', kind: 'system_prompt', content: `Header line.\n${shortShared}\nUnique A tail.` },
      { id: 'b', label: 'B', kind: 'skill', content: `Skill title.\n${shortShared}\nUnique B tail.` },
    ])
    expect(clean.pairs).toHaveLength(0)
    expect(clean.duplicateEstimatedTokens).toBe(0)

    const longAgentsRule =
      'Never modify files outside your assigned write_scope without coordinator approval and always run vitest with pool=threads on Windows.'
    const wsOverlap = auditContextOverlap([
      { id: 'ws', label: 'AGENTS.md', kind: 'workspace_instructions', content: `Line 1\nLine 2\n${longAgentsRule}` },
      { id: 'frag', label: 'global-rules', kind: 'prompt_fragment', content: `${longAgentsRule}\nExtra line.` },
    ])
    expect(wsOverlap.pairs).toHaveLength(1)
    expect(wsOverlap.pairs[0]!.remediationCode).toBe('dedupe_workspace_instructions')
    expect(wsOverlap.pairs[0]!.matches[0]!.sourceALines).toEqual({ start: 3, end: 3 })
    expect(wsOverlap.pairs[0]!.matches[0]!.sourceBLines).toEqual({ start: 1, end: 1 })
  })
})

describe('Squarified Context Treemap & Compaction Reconciliation', () => {
  it('computes non-overlapping area-proportional rectangles filling bounds exactly', () => {
    const items = [
      { id: 'a', value: 60 },
      { id: 'b', value: 30 },
      { id: 'c', value: 20 },
      { id: 'd', value: 10 },
      { id: 'e', value: 5 },
      { id: 'zero', value: 0 },
    ]
    const bounds = { x: 0, y: 0, width: 100, height: 100 }
    const placed = computeSquarifiedTreemap(items, bounds)

    expect(placed).toHaveLength(5)
    const totalArea = placed.reduce((acc, r) => acc + r.width * r.height, 0)
    expect(totalArea).toBeCloseTo(10000, 3)

    // Verify bounds containment and pairwise non-overlap
    for (let i = 0; i < placed.length; i++) {
      const a = placed[i]!
      expect(a.x).toBeGreaterThanOrEqual(-1e-6)
      expect(a.y).toBeGreaterThanOrEqual(-1e-6)
      expect(a.x + a.width).toBeLessThanOrEqual(100 + 1e-6)
      expect(a.y + a.height).toBeLessThanOrEqual(100 + 1e-6)
      for (let j = i + 1; j < placed.length; j++) {
        const b = placed[j]!
        const overlapX = Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x)
        const overlapY = Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y)
        expect(overlapX <= 1e-6 || overlapY <= 1e-6).toBe(true)
      }
    }

    // Largest item gets proportional area (60 / 125 = 48%)
    const tileA = placed.find((p) => p.item.id === 'a')!
    expect((tileA.width * tileA.height) / 10000).toBeCloseTo(60 / 125, 4)
  })

  it('builds zoomable hierarchy + compaction reconciliation from Phase 11 ContextPackDto', () => {
    const packs = makeContextPacks()
    const model = buildContextTreemapModel(packs, 'pack-1')

    expect(model.packCount).toBe(2)
    expect(model.reconciliation.retainedTokens).toBe(7200)
    expect(model.reconciliation.droppedTokens).toBe(1700)
    expect(model.reconciliation.preCompactionTokens).toBe(8900)
    expect(model.reconciliation.utilizationPercent).toBe(72)
    expect(model.reconciliation.hasPricingData).toBe(true)

    const memoryCat = model.root.children.find((c) => c.category === 'memory')
    expect(memoryCat).toBeDefined()
    expect(memoryCat!.retainedTokens).toBe(1200)
    expect(memoryCat!.droppedTokens).toBe(300)
    // Two reviewed_memory rows become zoomable children
    expect(memoryCat!.children).toHaveLength(2)

    const path = findTreemapPath(model.root, memoryCat!.children[0]!.id)
    expect(path.map((n) => n.id)).toEqual(['root', memoryCat!.id, memoryCat!.children[0]!.id])

    // Pack 2 delta reconciliation vs Pack 1
    const model2 = buildContextTreemapModel(packs, 'pack-2')
    expect(model2.reconciliation.hasCompactionDigest).toBe(true)
    expect(model2.reconciliation.previousPackRetainedDelta).toBe(5400 - 7200)
    expect(model2.reconciliation.previousPackDroppedDelta).toBe(0 - 1700)
  })
})

describe('Trajectory Tree, Governance Signals, Code Frames & 5% Window Buckets', () => {
  it('detects all 7 governance signals, repeated-args streaks, +M/-N edit stats, and error code frames', () => {
    const snapshot = makeSnapshot()
    const execs: ToolExecutionTimelineItemDto[] = [
      makeToolExec({
        id: 'e-edit',
        requested_seq: 1,
        tool_id: 'edit_file',
        summary: 'Patched src/auth.ts (+14/-3)',
      }),
      makeToolExec({
        id: 'e-stale',
        requested_seq: 2,
        tool_id: 'edit_file',
        status: 'failed',
        tool_success: false,
        error_category: 'fs_stale_version',
        arguments_summary: '{"path":"src/auth.ts","line":42}',
        summary: 'src/auth.ts:42:7 line_hash mismatch (modified since last read)',
        evidence: ['41 | export function verify(token: string) {', '> 42 |   return jwt.decode(token)', '43 | }'],
      }),
      makeToolExec({
        id: 'e-stale-retry',
        requested_seq: 3,
        tool_id: 'edit_file',
        status: 'failed',
        tool_success: false,
        error_category: 'fs_stale_version',
        arguments_summary: '{"path":"src/auth.ts","line":42}',
        summary: 'Repeated edit_file call failed with stale version',
      }),
      makeToolExec({
        id: 'e-not-obs',
        requested_seq: 4,
        tool_id: 'write_file',
        status: 'failed',
        tool_success: false,
        error_category: 'fs_not_observed',
        summary: 'File not observed before write',
      }),
      makeToolExec({
        id: 'e-rejected',
        requested_seq: 5,
        tool_id: 'shell',
        status: 'rejected',
        tool_success: false,
        command: 'rm -rf dist',
        summary: 'User rejected approval request',
      }),
      makeToolExec({
        id: 'e-worktree',
        requested_seq: 6,
        tool_id: 'write_file',
        status: 'failed',
        tool_success: false,
        error_category: 'worktree_guard',
        summary: 'Path escapes active worktree isolation boundary',
      }),
      makeToolExec({
        id: 'e-scope',
        requested_seq: 7,
        tool_id: 'edit_file',
        status: 'failed',
        tool_success: false,
        error_category: 'write_scope_locked',
        summary: 'outside write_scope: lease conflict with reviewer',
      }),
      makeToolExec({
        id: 'e-timeout',
        requested_seq: 8,
        tool_id: 'web_fetch',
        status: 'timeout',
        tool_success: false,
        error_category: 'timeout',
        summary: 'web_fetch gave up on https://example.com after 15000 ms',
        duration_ms: 15000,
      }),
    ]

    const streaks = detectRepeatedArgsStreaks(execs)
    expect(streaks.get('e-stale-retry')).toBe(2)

    expect(extractFileEditStats(execs[0]!)).toEqual({ added: 14, removed: 3, filePath: 'src/app.ts' })

    const frames = extractErrorCodeFrames(execs[1]!)
    expect(frames).toHaveLength(1)
    expect(frames[0]!.filePath).toBe('src/auth.ts')
    expect(frames[0]!.line).toBe(42)
    expect(frames[0]!.lines.some((l) => l.highlight && l.lineNumber === 42)).toBe(true)

    const tree = buildTrajectoryTree({
      snapshot,
      toolExecutions: execs,
      modelInvocations: [makeInvocation()],
    })

    expect(tree.totalToolCalls).toBe(8)
    expect(tree.totalModelCalls).toBe(1)
    expect(tree.totalAddedLines).toBe(14)
    expect(tree.totalRemovedLines).toBe(3)
    expect(tree.activeSignals).toEqual(
      expect.arrayContaining([
        'APPROVAL_REJECTED',
        'FS_NOT_OBSERVED',
        'FS_STALE_VERSION',
        'WORKTREE_GUARD',
        'WRITE_SCOPE_LOCKED',
        'TIMEOUT',
        'SAME_ARGS_RETRY',
      ]),
    )
  })

  it('buckets context occupancy into 20 deterministic 5% buckets (0-5% .. 95-100%) and flags high-occupancy degradation', () => {
    const invocations: ModelInvocationDto[] = [
      makeInvocation({
        id: 'i1',
        input_tokens: 12000, // 12% -> bucket 2 (10-15%)
        status: 'completed',
        started_at: '2026-11-22T01:00:00.000Z',
        completed_at: '2026-11-22T01:00:00.800Z',
      }),
      makeInvocation({
        id: 'i2',
        input_tokens: 72000, // 72% -> bucket 14 (70-75%)
        status: 'failed',
        error_category: 'timeout',
        started_at: '2026-11-22T01:01:00.000Z',
        completed_at: '2026-11-22T01:01:04.000Z',
      }),
      makeInvocation({
        id: 'i3',
        input_tokens: 100000, // 100% -> bucket 19 (95-100%)
        status: 'failed',
        error_category: 'context_overflow',
        started_at: '2026-11-22T01:02:00.000Z',
        completed_at: '2026-11-22T01:02:05.000Z',
      }),
    ]

    const report = buildContextWindowBuckets({
      modelInvocations: invocations,
      contextWindowTokens: 100000,
    })

    expect(report.buckets).toHaveLength(20)
    expect(report.buckets[0]!.label).toBe('0-5%')
    expect(report.buckets[19]!.label).toBe('95-100%')
    expect(report.buckets[2]!.sampleCount).toBe(1)
    expect(report.buckets[2]!.errorRate).toBe(0)
    expect(report.buckets[14]!.sampleCount).toBe(1)
    expect(report.buckets[14]!.errorRate).toBe(1)
    expect(report.buckets[19]!.sampleCount).toBe(1)
    expect(report.buckets[19]!.avgLatencyMs).toBe(5000)
    expect(report.peakOccupancyPct).toBe(100)
    expect(report.degradationDetected).toBe(true)
  })
})

describe('Click-to-Diagnose -> Composer & Offline HTML Export', () => {
  it('builds a tinadec_inspect diagnostic draft and injects into composer without creating debug routes', async () => {
    const exec = makeToolExec({
      id: 'e-fail',
      status: 'failed',
      tool_success: false,
      error_category: 'fs_stale_version',
      summary: 'src/auth.ts:42:3 stale hash mismatch (+5/-2)',
      evidence: ['> 42 | verify(token)'],
    })
    const tree = buildTrajectoryTree({ snapshot: makeSnapshot(), toolExecutions: [exec] })
    const failedNode = tree.root.children[0]!.children.find((n) => n.kind === 'tool_call')!
    const treemap = buildContextTreemapModel(makeContextPacks(), 'pack-1')

    const updateDraft = vi.fn()
    const navigateToHome = vi.fn()

    const draft = buildDiagnosticDraft({
      trajectory: tree,
      selectedNode: failedNode,
      treemap,
    })
    expect(draft).toContain('tinadec_inspect')
    expect(draft).toContain('run-1')
    expect(draft).toContain('FS_STALE_VERSION')
    expect(draft).toContain('src/auth.ts:42:3')

    const res = await triggerDiagnoseInComposer(
      { trajectory: tree, selectedNode: failedNode, treemap },
      { updateDraft, navigateToHome, skipFocus: true },
    )
    expect(updateDraft).toHaveBeenCalledWith(res.draft)
    expect(navigateToHome).toHaveBeenCalledTimes(1)
    expect(res.navigated).toBe(true)
  })

  it('exports self-contained XSS-safe monochrome HTML report with zero left border stripes', () => {
    const xssPayload = '<script>alert("xss")</script><img src=x onerror=alert(1)>'
    expect(escapeHtml(xssPayload)).not.toContain('<script>')

    const snapshot = makeSnapshot()
    snapshot.run!.summary = `Investigate ${xssPayload}`
    const exec = makeToolExec({
      id: 'e-xss',
      status: 'failed',
      tool_success: false,
      summary: `Crash on ${xssPayload}`,
      evidence: [`> 10 | const evil = "${xssPayload}"`],
    })
    const trajectory = buildTrajectoryTree({ snapshot, toolExecutions: [exec] })
    const buckets = buildContextWindowBuckets({ contextPacks: makeContextPacks(), toolExecutions: [exec] })
    const treemap = buildContextTreemapModel(makeContextPacks(), 'pack-1')
    const shared =
      'Repeat this exact sixty-four plus character diagnostic rule verbatim across both sources to trigger winnowing overlap detection.'
    const overlap = auditContextOverlap([
      { id: 's1', label: `Prompt ${xssPayload}`, kind: 'system_prompt', content: shared },
      { id: 's2', label: 'Skill B', kind: 'skill', content: shared },
    ])

    const downloaded: Array<{ filename: string; html: string }> = []
    const exported = exportOfflineDiagnosticHtml(
      { trajectory, buckets, treemap, overlap, generatedAt: '2026-11-22T05:00:00.000Z' },
      { download: (filename, html) => downloaded.push({ filename, html }) },
    )

    expect(downloaded).toHaveLength(1)
    expect(exported.filename).toBe('tinadec-diagnostic-run-1-2026-11-22.html')
    expect(exported.bytes).toBeGreaterThan(1000)

    const html = buildOfflineDiagnosticHtml({ trajectory, buckets, treemap, overlap })
    expect(html).toContain('Executive Conclusion Summary')
    expect(html).toContain('5% Context Window Occupancy')
    expect(html).toContain('Context Composition Treemap Breakdown')
    expect(html).toContain('64-Char Winnowing Context Overlap Audit')
    expect(html).toContain('Trajectory Evidence Trace')
    // Strict XSS check: zero raw <script> or unescaped onerror attribute
    expect(html).not.toContain('<script>')
    expect(html).not.toContain('<img src=x')
    expect(html).toContain('&lt;script&gt;alert(&quot;xss&quot;)&lt;/script&gt;')
    // Zero left border stripes
    expect(html).not.toMatch(/border-left\s*:\s*[23]px/i)
    expect(html).not.toMatch(/inset\s+[23]px\s+0/i)
  })
})

describe('Stage-1 Vue Components', () => {
  it('mounts TrajectoryTreePanel with governance badges, code frames, and Diagnose in Composer emit', async () => {
    const exec = makeToolExec({
      id: 'e-ui',
      status: 'failed',
      tool_success: false,
      error_category: 'write_scope_locked',
      summary: 'src/app.ts:9:2 write_scope violation (+8/-1)',
      evidence: ['> 9 | fs.writeFileSync("outside.ts", data)'],
    })
    const trajectory = buildTrajectoryTree({ snapshot: makeSnapshot(), toolExecutions: [exec] })
    const wrapper = mount(TrajectoryTreePanel, {
      props: { trajectory },
      global: { plugins: [makeI18n()] },
    })

    expect(wrapper.find('[data-testid="traj-badge-WRITE_SCOPE_LOCKED"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="traj-kpi-edits"]').text()).toContain('+8')

    await wrapper.get('[data-testid="traj-row-tool:e-ui"]').trigger('click')
    expect(wrapper.find('[data-testid="traj-code-frame"]').exists()).toBe(true)

    await wrapper.get('[data-testid="traj-diagnose-btn"]').trigger('click')
    expect(wrapper.emitted('diagnose-node')).toHaveLength(1)
  })

  it('mounts ContextWindowBucketChart with 20 bucket slots and degradation indicator', () => {
    const report = buildContextWindowBuckets({
      modelInvocations: [
        makeInvocation({ id: 'a', input_tokens: 10000, status: 'completed' }),
        makeInvocation({ id: 'b', input_tokens: 85000, status: 'failed', error_category: 'timeout' }),
      ],
      contextWindowTokens: 100000,
    })

    const wrapper = mount(ContextWindowBucketChart, {
      props: { report },
      global: { plugins: [makeI18n()] },
    })

    expect(wrapper.findAll('[data-testid^="bucket-bar-"]')).toHaveLength(20)
    expect(wrapper.find('[data-testid="bucket-degradation-badge"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="bucket-readout"]').text()).toContain('85-90%')
  })

  it('mounts ContextTreemap with click-to-zoom breadcrumbs and retained/dropped detail drawer', async () => {
    const wrapper = mount(ContextTreemap, {
      props: { contextPacks: makeContextPacks() },
      global: { plugins: [makeI18n()] },
    })

    // Switch to pack-1 (which has multi-item memory and dropped tokens)
    await wrapper.get('[data-testid="treemap-pack-select"]').setValue('pack-1')
    expect(wrapper.find('[data-testid="treemap-dropped-badge"]').text()).toContain('1,700')

    // Click composite memory category tile -> zooms in + opens detail drawer
    await wrapper.get('[data-testid="treemap-tile-cat:memory"]').trigger('click')
    expect(wrapper.find('[data-testid="treemap-crumb-cat:memory"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="treemap-detail-drawer"]').text()).toContain('1,200 tok')
    expect(wrapper.find('[data-testid="treemap-detail-drawer"]').text()).toContain('300 tok')

    // Click root breadcrumb -> zooms back to root
    await wrapper.get('[data-testid="treemap-crumb-root"]').trigger('click')
    expect(wrapper.find('[data-testid="treemap-crumb-cat:memory"]').exists()).toBe(false)
  })

  it('mounts ContextOverlapPanel with 64-char Winnowing matches, line ranges, and collapsible toggle', async () => {
    const shared =
      'Always invoke tinadec_inspect before applying any agent or mode configuration change and verify revision conflicts explicitly.'
    const sources: ContextAuditSource[] = [
      { id: 'p1', label: 'System Prompt', kind: 'system_prompt', content: `Intro\n${shared}` },
      { id: 's1', label: 'config-skill', kind: 'skill', content: `# Skill\n\n${shared}` },
    ]

    const wrapper = mount(ContextOverlapPanel, {
      props: { sources, collapsible: true, defaultOpen: false },
      global: { plugins: [makeI18n()] },
    })

    expect(wrapper.find('[data-testid="context-overlap-body"]').exists()).toBe(false)
    await wrapper.get('[data-testid="context-overlap-toggle"]').trigger('click')
    expect(wrapper.find('[data-testid="context-overlap-body"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="overlap-line-range"]').text()).toContain('L2 <-> L3')
    expect(wrapper.find('[data-testid="overlap-remediation"]').text()).toContain('SKILL.md')
  })
})

describe('Wave-2 Run Eval Pure Helpers & TrajectoryEvalPanel', () => {
  it('computes log-compressed scatter ticks, honest ≈ token flags, summary KPIs, and A/B compare drafts', () => {
    const tokenScale = buildEvalLogScale(3500, 1000, EVAL_TOKEN_TICK_CANDIDATES)
    expect(tokenScale.ticks).toEqual([0, 300, 1000, 3000])
    expect(tokenScale.place(0)).toBe(0)
    expect(tokenScale.place(300)).toBeLessThan(tokenScale.place(1000))
    expect(tokenScale.place(1000)).toBeLessThan(tokenScale.place(3000))

    const durationScale = buildEvalLogScale(41_190, 10_000, EVAL_DURATION_TICK_CANDIDATES)
    expect(durationScale.ticks).toEqual([0, 3000, 10000, 30000])

    // Multi-turn aggregation with exact vs estimated output tokens
    const invocations: ModelInvocationDto[] = [
      makeInvocation({
        id: 'inv-t1',
        turn_id: 'turn-1',
        agent_instance_id: 'agent-t1',
        output_tokens: 1346,
        started_at: '2026-11-22T01:00:00.000Z',
        completed_at: '2026-11-22T01:00:04.000Z',
      }),
      makeInvocation({
        id: 'inv-t2',
        turn_id: 'turn-2',
        agent_instance_id: 'agent-t2',
        output_tokens: 1374,
        started_at: '2026-11-22T01:01:00.000Z',
        completed_at: '2026-11-22T01:01:06.190Z',
      }),
      {
        ...makeInvocation({
          id: 'inv-t3',
          turn_id: 'turn-3',
          agent_instance_id: 'agent-t3',
          total_tokens: 2000,
          started_at: '2026-11-22T01:02:00.000Z',
          completed_at: '2026-11-22T01:02:03.000Z',
        }),
        input_tokens: null,
        output_tokens: null,
      },
    ]

    const toolExecs: ToolExecutionTimelineItemDto[] = [
      makeToolExec({
        id: 'tx-1',
        requested_seq: 10,
        updated_seq: 11,
        tool_id: 'edit_file',
        status: 'failed',
        tool_success: false,
        error_category: 'fs_stale_version',
        duration_ms: 7180,
        requested_at: '2026-11-22T01:00:04.000Z',
        updated_at: '2026-11-22T01:00:11.180Z',
        task_id: undefined,
        agent_instance_id: 'agent-t1',
      }),
      makeToolExec({
        id: 'tx-2',
        requested_seq: 209,
        updated_seq: 210,
        tool_id: 'pwsh',
        status: 'completed',
        tool_success: true,
        duration_ms: 35000,
        requested_at: '2026-11-22T01:01:06.190Z',
        updated_at: '2026-11-22T01:01:41.190Z',
        task_id: undefined,
        agent_instance_id: 'agent-t2',
      }),
      {
        ...makeToolExec({
          id: 'tx-3',
          requested_seq: 220,
          updated_seq: 0,
          tool_id: 'read_file',
          status: 'running',
          duration_ms: 500,
          requested_at: '2026-11-22T01:02:01.000Z',
          updated_at: '2026-11-22T01:02:01.500Z',
          agent_instance_id: 'agent-t3',
        }),
        task_id: null,
        tool_success: undefined,
      },
    ]

    const trajectory = buildTrajectoryTree({
      snapshot: null,
      toolExecutions: toolExecs,
      modelInvocations: invocations,
    })
    const buckets = buildContextWindowBuckets({
      modelInvocations: invocations,
      contextWindowTokens: 100000,
    })

    const evalRes = aggregateTrajectoryEval({
      trajectory,
      modelInvocations: invocations,
      toolExecutions: toolExecs,
      buckets,
      runId: 'run-eval-1',
      sessionId: 'sess-eval-1',
    })

    expect(evalRes.points).toHaveLength(3)
    expect(evalRes.hasAnyEstimatedTokens).toBe(true)

    const p1 = evalRes.points.find((p) => p.turnNo === 1)!
    const p2 = evalRes.points.find((p) => p.turnNo === 2)!
    const p3 = evalRes.points.find((p) => p.turnNo === 3)!

    expect(p1.outputTokens).toBe(1346)
    expect(p1.estimatedTokens).toBe(false)
    expect(p1.hasToolError).toBe(true)

    expect(p2.outputTokens).toBe(1374)
    expect(p2.estimatedTokens).toBe(false)
    expect(p2.hasToolError).toBe(false)
    expect(p2.slowestTool).toMatchObject({ toolId: 'pwsh', durationMs: 35000, seq: 209 })

    // Turn 3 lacked explicit output_tokens -> must honestly flag estimatedTokens: true
    expect(p3.estimatedTokens).toBe(true)
    expect(formatEvalTokensWithHonesty(p3.outputTokens, p3.estimatedTokens)).toMatch(/^≈/)
    expect(formatEvalTokensWithHonesty(p2.outputTokens, p2.estimatedTokens)).toBe('1,374')

    expect(evalRes.summary.totalCalls).toBe(3)
    expect(evalRes.summary.okCalls).toBe(1)
    expect(evalRes.summary.errorCalls).toBe(1)
    expect(evalRes.summary.unrecordedCalls).toBe(1)
    expect(evalRes.summary.errorTurnCount).toBe(1)
    expect(evalRes.summary.comparableTurnCount).toBe(3)
    expect(evalRes.summary.errorOutputSharePct).toBeGreaterThan(0)

    const pair = pickDefaultEvalComparePair(evalRes.points)!
    expect(pair[0].turnNo).toBe(2) // slowest turn
    expect(pair[1].turnNo).toBe(1) // error turn with closest output

    const delta = computeEvalCompareDelta(p2, p1)
    expect(delta.tokenDiff).toBe(28)
    expect(delta.anyEstimatedTokens).toBe(false)

    const singleDraft = buildEvalCompareDraft({
      turnA: p3,
      runId: 'run-eval-1',
      sessionId: 'sess-eval-1',
    })
    expect(singleDraft).toContain('tinadec_inspect')
    expect(singleDraft).toContain('Turn #3')
    expect(singleDraft).toContain('≈')

    const compareDraft = buildEvalCompareDraft({
      turnA: p2,
      turnB: p1,
      runId: 'run-eval-1',
      sessionId: 'sess-eval-1',
    })
    expect(compareDraft).toContain('Turn #2 vs Turn #1')
    expect(compareDraft).toContain('28 tokens')
    expect(compareDraft).toContain('pwsh')
  })

  it('mounts TrajectoryEvalPanel, swaps Side A/B on scatter clicks, triggers Composer diagnostics for 问这轮 / 对照这两轮, and exports Geist HTML', async () => {
    const invocations: ModelInvocationDto[] = [
      makeInvocation({
        id: 'm1',
        run_id: 'run-panel-1',
        turn_id: 'turn-1',
        agent_instance_id: 'ag-1',
        output_tokens: 400,
        started_at: '2026-11-22T02:00:00.000Z',
        completed_at: '2026-11-22T02:00:02.000Z',
      }),
      makeInvocation({
        id: 'm2',
        run_id: 'run-panel-1',
        turn_id: 'turn-2',
        agent_instance_id: 'ag-2',
        output_tokens: 1100,
        started_at: '2026-11-22T02:01:00.000Z',
        completed_at: '2026-11-22T02:01:05.000Z',
      }),
      {
        ...makeInvocation({
          id: 'm3',
          run_id: 'run-panel-1',
          turn_id: 'turn-3',
          agent_instance_id: 'ag-3',
          total_tokens: 3000,
          started_at: '2026-11-22T02:02:00.000Z',
          completed_at: '2026-11-22T02:02:18.000Z',
        }),
        output_tokens: null,
        input_tokens: null,
      },
    ]
    const toolExecs: ToolExecutionTimelineItemDto[] = [
      makeToolExec({
        id: 'te-1',
        requested_seq: 1,
        updated_seq: 2,
        tool_id: 'read_file',
        status: 'completed',
        tool_success: true,
        duration_ms: 800,
        requested_at: '2026-11-22T02:00:02.000Z',
        updated_at: '2026-11-22T02:00:02.800Z',
        task_id: undefined,
        agent_instance_id: 'ag-1',
      }),
      makeToolExec({
        id: 'te-2',
        requested_seq: 5,
        updated_seq: 6,
        tool_id: 'pwsh',
        status: 'failed',
        tool_success: false,
        error_category: 'timeout',
        duration_ms: 9000,
        requested_at: '2026-11-22T02:01:05.000Z',
        updated_at: '2026-11-22T02:01:14.000Z',
        task_id: undefined,
        agent_instance_id: 'ag-2',
      }),
      makeToolExec({
        id: 'te-3',
        requested_seq: 9,
        updated_seq: 10,
        tool_id: 'bash',
        status: 'completed',
        tool_success: true,
        duration_ms: 22000,
        requested_at: '2026-11-22T02:02:18.000Z',
        updated_at: '2026-11-22T02:02:40.000Z',
        task_id: undefined,
        agent_instance_id: 'ag-3',
      }),
    ]

    const trajectory = buildTrajectoryTree({
      snapshot: null,
      toolExecutions: toolExecs,
      modelInvocations: invocations,
    })
    const buckets = buildContextWindowBuckets({
      modelInvocations: invocations,
      contextWindowTokens: 100000,
    })

    const diagnoseSpy = vi.spyOn(offlineDiagnosticReport, 'triggerDiagnoseInComposer')
    const composerDrafts: string[] = []
    const onComposerFocus = (e: Event) => {
      composerDrafts.push((e as CustomEvent<{ draft: string }>).detail.draft)
    }
    window.addEventListener('tinadec:focus-composer', onComposerFocus)

    const wrapper = mount(TrajectoryEvalPanel, {
      props: {
        trajectory,
        buckets,
        runId: 'run-panel-1',
        sessionId: 'sess-panel-1',
        modelInvocations: invocations,
        toolExecutions: toolExecs,
      },
      global: { plugins: [makeI18n()] },
    })

    expect(wrapper.find('[data-testid="eval-summary-strip"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="eval-scatter-svg"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="eval-pair-link"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="eval-compare-card"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="eval-estimated-note"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="bucket-peak"]').exists()).toBe(true)

    // Default pair starts with Turn #3 (slowest, estimated tokens prefixed with ≈) vs Turn #2 (error)
    expect(wrapper.get('[data-testid="eval-compare-slot-a"]').text()).toContain('≈')
    expect(wrapper.get('[data-testid="eval-compare-slot-b"]').text()).toContain('1,100')

    // Click scatter point 1 then scatter point 2 to swap A/B comparison pair to Turn #1 vs Turn #2
    await wrapper.get('[data-testid="eval-scatter-point-1"]').trigger('click')
    await wrapper.get('[data-testid="eval-scatter-point-2"]').trigger('click')

    expect(wrapper.get('[data-testid="eval-compare-slot-a"]').text()).toContain('400 token')
    expect(wrapper.get('[data-testid="eval-compare-slot-b"]').text()).toContain('1,100 token')
    expect(wrapper.get('[data-testid="eval-compare-delta"]').text()).toContain('700')

    // Click 问这轮 -> calls triggerDiagnoseInComposer & emits diagnose-draft
    await wrapper.get('[data-testid="eval-ask-turn"]').trigger('click')
    await vi.waitFor(
      () => {
        expect(diagnoseSpy).toHaveBeenCalledTimes(1)
        expect(composerDrafts[0]).toContain('Turn #1')
        expect(wrapper.emitted('diagnose-draft')?.[0]?.[0]).toContain('Turn #1')
      },
      { timeout: 5000 },
    )

    // Click 对照这两轮 -> calls triggerDiagnoseInComposer with A/B delta & emits diagnose-draft
    await wrapper.get('[data-testid="eval-ask-compare"]').trigger('click')
    await vi.waitFor(
      () => {
        expect(diagnoseSpy).toHaveBeenCalledTimes(2)
        expect(composerDrafts[1]).toContain('Turn #1 vs Turn #2')
        expect(composerDrafts[1]).toContain('700 tokens')
        expect(wrapper.emitted('diagnose-draft')?.[1]?.[0]).toContain('Turn #1 vs Turn #2')
      },
      { timeout: 5000 },
    )

    // Jump links & offline report export
    await wrapper.get('[data-testid="eval-jump-turn"]').trigger('click')
    expect(wrapper.emitted('jump-turn')?.[0]).toEqual([1])

    await wrapper.get('[data-testid="eval-jump-code"]').trigger('click')
    expect(wrapper.emitted('jump-code')?.[0]).toEqual([{ turnNo: 1, runId: 'run-panel-1' }])

    const createUrlSpy = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:eval-report')
    const revokeUrlSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {})
    await wrapper.get('[data-testid="eval-export-report-btn"]').trigger('click')
    const exported = wrapper.emitted('export-report')?.[0]?.[0] as offlineDiagnosticReport.ExportOfflineHtmlResult
    expect(exported.filename).toContain('run-panel-1')
    expect(exported.html).toContain('prefers-color-scheme: dark')

    window.removeEventListener('tinadec:focus-composer', onComposerFocus)
    createUrlSpy.mockRestore()
    revokeUrlSpy.mockRestore()
    diagnoseSpy.mockRestore()
  })
})
