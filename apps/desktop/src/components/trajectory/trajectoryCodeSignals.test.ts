import { describe, expect, it } from 'vitest'
import type { OrchestrationSnapshotDto, ToolExecutionTimelineItemDto } from '@/api'
import { buildTrajectoryTree } from '@/lib/trajectoryBuilder'
import {
  aggregateCodeExecutionSignals,
  classifyCodeFailureSignal,
  isCodeExecutionCandidate,
  normalizeExecutorKind,
} from './trajectoryCodeSignals'

function makeExec(overrides: Partial<ToolExecutionTimelineItemDto> = {}): ToolExecutionTimelineItemDto {
  return {
    id: overrides.id ?? 'exec-1',
    run_id: overrides.run_id ?? 'run-code-42',
    session_id: overrides.session_id ?? 'sess-code-1',
    tool_id: overrides.tool_id ?? 'pwsh',
    tool_display_name: overrides.tool_display_name ?? 'PowerShell',
    source: overrides.source ?? 'builtin',
    provider_layer: overrides.provider_layer ?? 'core',
    risk: overrides.risk ?? 'medium',
    requires_approval: overrides.requires_approval ?? false,
    status: overrides.status ?? 'completed',
    approval_id: overrides.approval_id ?? null,
    step_result_id: overrides.step_result_id ?? null,
    summary: overrides.summary ?? 'Command completed',
    evidence: overrides.evidence ?? [],
    requested_at: overrides.requested_at ?? '2026-11-25T08:00:01.000Z',
    updated_at: overrides.updated_at ?? '2026-11-25T08:00:01.500Z',
    duration_ms: overrides.duration_ms ?? 500,
    requested_seq: overrides.requested_seq ?? 10,
    updated_seq: overrides.updated_seq ?? 11,
    event_types: overrides.event_types ?? ['tool.completed'],
    checkpoint_summary: overrides.checkpoint_summary ?? '',
    tool_success: overrides.tool_success ?? true,
    task_id: overrides.task_id ?? 'task-1',
    agent_instance_id: overrides.agent_instance_id ?? 'agent-1',
    error_category: overrides.error_category ?? null,
    arguments_summary: overrides.arguments_summary ?? null,
    resource_path: overrides.resource_path ?? null,
    command: overrides.command ?? null,
    cwd: overrides.cwd ?? 'C:/git/agent/TinadecOffice',
    query: overrides.query ?? null,
  }
}

function makeSnapshot(runId = 'run-code-42'): OrchestrationSnapshotDto {
  return {
    run: {
      id: runId,
      session_id: 'sess-code-1',
      status: 'failed',
      summary: 'Execute test suite & lint checks',
      created_at: '2026-11-25T08:00:00.000Z',
      updated_at: '2026-11-25T08:00:20.000Z',
    },
    nodes: [
      {
        id: 'task-1',
        graph_id: 'g-1',
        run_id: runId,
        session_id: 'sess-code-1',
        title: 'Turn 1 Search',
        description: '',
        status: 'failed',
        lane_key: 'worker-a',
        priority: 1,
        risk: 'low',
        success_criteria: [],
        dependencies: [],
        required_capabilities: [],
        created_at: '2026-11-25T08:00:00.000Z',
        updated_at: '2026-11-25T08:00:08.000Z',
      },
      {
        id: 'task-2',
        graph_id: 'g-1',
        run_id: runId,
        session_id: 'sess-code-1',
        title: 'Turn 2 Verification',
        description: '',
        status: 'failed',
        lane_key: 'worker-b',
        priority: 2,
        risk: 'low',
        success_criteria: [],
        dependencies: ['task-1'],
        required_capabilities: [],
        created_at: '2026-11-25T08:00:09.000Z',
        updated_at: '2026-11-25T08:00:20.000Z',
      },
    ],
    lanes: [],
    assignments: [],
    step_results: [],
    context_packs: [],
    supervision_findings: [],
  }
}

describe('trajectoryCodeSignals', () => {
  it('normalizes executor kinds accurately across PowerShell, Bash, Terminal, and Other', () => {
    expect(normalizeExecutorKind({ toolId: 'pwsh', command: 'rg foo' })).toBe('PowerShell')
    expect(normalizeExecutorKind({ toolId: 'powershell', command: 'Get-ChildItem' })).toBe('PowerShell')
    expect(normalizeExecutorKind({ toolId: 'run_command', command: 'Set-Location C:\\repo; bun test' })).toBe('PowerShell')
    expect(normalizeExecutorKind({ toolId: 'bash', command: 'ls -la' })).toBe('Bash')
    expect(normalizeExecutorKind({ toolId: 'zsh', command: 'echo ok' })).toBe('Bash')
    expect(normalizeExecutorKind({ toolId: 'terminal_exec', command: 'git status' })).toBe('Terminal')
    expect(normalizeExecutorKind({ toolId: 'cmd', command: 'dir' })).toBe('Terminal')
    expect(normalizeExecutorKind({ toolId: 'edit_file', command: '' })).toBe('Other')

    // Non-command read_file without command/codeFrames is excluded
    expect(isCodeExecutionCandidate({ kind: 'tool_call', toolId: 'read_file', command: null, codeFrames: [] })).toBe(false)
    // Word-boundary check: "refresh" does NOT match "sh"
    expect(isCodeExecutionCandidate({ kind: 'tool_call', toolId: 'refresh_cache', command: null, codeFrames: [] })).toBe(false)
    expect(isCodeExecutionCandidate({ kind: 'tool_call', toolId: 'sh', command: null, codeFrames: [] })).toBe(true)
  })

  it('classifies all 6 specific governance/runtime signals plus generic fallback', () => {
    expect(
      classifyCodeFailureSignal({
        status: 'failed',
        scriptSource: 'rg -n total src/{invoice.ts,totals.ts}',
        feedbackText: 'ParserError:\nLine |\n  1 | rg -n total src/{invoice.ts,totals.ts}\n    | Missing argument in parameter list.',
        signals: [],
      }),
    ).toMatchObject({
      slug: 'parser_error',
      code: 'ParserError',
      label: '脚本语法错误',
      isSpecificSignal: true,
    })

    expect(
      classifyCodeFailureSignal({
        status: 'failed',
        scriptSource: 'npx vitest run src/invoice.test.ts',
        feedbackText: 'FAIL src/invoice.test.ts > AssertionError: expected 10 to be 12',
        signals: [],
      }),
    ).toMatchObject({
      slug: 'verification_failed',
      code: 'VerificationFailed',
      label: '验证未通过',
      isSpecificSignal: true,
    })

    expect(
      classifyCodeFailureSignal({
        status: 'failed',
        errorCategory: 'worktree_guard',
        scriptSource: 'rm -rf ../outside',
        feedbackText: 'Blocked by worktree isolation',
        signals: [{ code: 'WORKTREE_GUARD', severity: 'danger' }],
      }),
    ).toMatchObject({
      slug: 'worktree_guard',
      code: 'WorktreeGuard',
      label: '工作树拦截',
      isSpecificSignal: true,
    })

    expect(
      classifyCodeFailureSignal({
        status: 'timed_out',
        scriptSource: 'sleep 999',
        feedbackText: 'Command timed out after 30000ms',
        signals: [{ code: 'TIMEOUT', severity: 'danger' }],
      }),
    ).toMatchObject({
      slug: 'timeout',
      code: 'Timeout',
      label: '超时',
      isSpecificSignal: true,
    })

    expect(
      classifyCodeFailureSignal({
        status: 'rejected',
        scriptSource: 'curl https://example.com',
        feedbackText: 'User rejected approval',
        signals: [{ code: 'APPROVAL_REJECTED', severity: 'warn' }],
      }),
    ).toMatchObject({
      slug: 'permission_denied',
      code: 'PermissionDenied',
      label: '权限拒绝',
      isSpecificSignal: true,
    })

    expect(
      classifyCodeFailureSignal({
        status: 'failed',
        errorCategory: 'fs_stale_version',
        scriptSource: '',
        feedbackText: 'src/invoice.ts:18:2 line_hash mismatch',
        signals: [{ code: 'FS_STALE_VERSION', severity: 'warn' }],
      }),
    ).toMatchObject({
      slug: 'stale_version',
      code: 'StaleVersion',
      label: '文件冲突',
      isSpecificSignal: true,
    })

    expect(
      classifyCodeFailureSignal({
        status: 'failed',
        scriptSource: './custom-bin',
        feedbackText: 'exited with code 2',
        signals: [],
      }),
    ).toMatchObject({
      slug: 'execution_failed',
      code: 'ExecutionFailed',
      label: '执行失败',
      isSpecificSignal: false,
      errorSignalCode: 'EXIT_2',
    })
  })

  it('aggregates invocations, extracts code frames & seqLabel, filters by executor, and builds 4 remediation drafts with run_id', () => {
    const snapshot = makeSnapshot('run-code-99')
    const toolExecutions: ToolExecutionTimelineItemDto[] = [
      // Non-command read_file -> ignored
      makeExec({
        id: 'ex-read',
        run_id: 'run-code-99',
        task_id: 'task-1',
        tool_id: 'read_file',
        command: null,
        summary: 'Read src/invoice.ts',
        evidence: [],
      }),
      // PowerShell syntax error #1
      makeExec({
        id: 'ex-pwsh-1',
        run_id: 'run-code-99',
        task_id: 'task-1',
        tool_id: 'pwsh',
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
      // PowerShell syntax error #2 in Turn 2
      makeExec({
        id: 'ex-pwsh-2',
        run_id: 'run-code-99',
        task_id: 'task-2',
        tool_id: 'pwsh',
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
      // Bash verification failed
      makeExec({
        id: 'ex-bash-fail',
        run_id: 'run-code-99',
        task_id: 'task-2',
        tool_id: 'bash',
        status: 'failed',
        tool_success: false,
        requested_seq: 350,
        updated_seq: 351,
        command: 'npx vitest run src/invoice.test.ts',
        summary: 'FAIL src/invoice.test.ts:24:7',
        evidence: ['> 24 | expect(computeTotal()).toBe(100)'],
      }),
      // Succeeded bash call
      makeExec({
        id: 'ex-bash-ok',
        run_id: 'run-code-99',
        task_id: 'task-2',
        tool_id: 'bash',
        status: 'completed',
        tool_success: true,
        requested_seq: 360,
        updated_seq: 361,
        command: 'npx vitest run src/invoice.test.ts',
        summary: '1 passed',
        evidence: ['Test Files 1 passed (1)'],
      }),
    ]

    const trajectory = buildTrajectoryTree({ snapshot, toolExecutions })
    const report = aggregateCodeExecutionSignals({
      trajectory,
      toolExecutions,
      runId: 'run-code-99',
      sessionId: 'sess-code-1',
    })

    expect(report.totalCalls).toBe(4)
    expect(report.errorCount).toBe(3)
    expect(report.detectedSignalCount).toBe(3)
    expect(report.signalGroups.map((g) => `${g.slug}:${g.count}`)).toEqual([
      'parser_error:2',
      'verification_failed:1',
    ])

    const parserGroup = report.signalGroups[0]!
    expect(parserGroup.turnCount).toBe(2)
    const latestPwsh = parserGroup.items[0]!
    expect(latestPwsh.turnNo).toBe(2)
    expect(latestPwsh.seqLabel).toBe('#340 → #341')
    expect(latestPwsh.executor).toBe('PowerShell')
    expect(latestPwsh.lineLocation).toBe('L1')
    expect(latestPwsh.primaryErrorMessage).toBe('Missing argument in parameter list.')

    // Highlighted code line inside frame
    const hiLine = latestPwsh.frames[0]!.lines.find((l) => l.highlight)
    expect(hiLine).toBeTruthy()
    expect(hiLine?.lineNumber).toBe(1)
    expect(hiLine?.text).toContain('rg -n total src/{invoice.ts,totals.ts}')

    // Verify all 4 remediation prompts carry exact code-frame lines, executor, location, and run_id
    expect(latestPwsh.remediations.map((r) => r.key)).toEqual([
      'fix_impl',
      'recover_observe',
      'add_capability',
      'keep_reusable',
    ])
    for (const rem of latestPwsh.remediations) {
      expect(rem.draft).toContain('run-code-99')
      expect(rem.draft).toContain('PowerShell')
      expect(rem.draft).toContain('L1')
      expect(rem.draft).toContain('#340 → #341')
      expect(rem.draft).toContain('rg -n total src/{invoice.ts,totals.ts}')
      expect(rem.draft).toContain('tinadec_inspect')
    }

    // Executor filter narrows KPIs and groups cleanly
    const bashOnly = aggregateCodeExecutionSignals({
      trajectory,
      toolExecutions,
      runId: 'run-code-99',
      executorFilter: 'Bash',
    })
    expect(bashOnly.totalCalls).toBe(2)
    expect(bashOnly.errorCount).toBe(1)
    expect(bashOnly.signalGroups.map((g) => g.slug)).toEqual(['verification_failed'])
  })

  it('handles succeeded-only command runs cleanly without creating false failure signal groups', () => {
    const snapshot = makeSnapshot('run-clean-1')
    snapshot.run!.status = 'completed'
    snapshot.nodes[0]!.status = 'completed'
    snapshot.nodes[1]!.status = 'completed'

    const toolExecutions: ToolExecutionTimelineItemDto[] = [
      makeExec({
        id: 'ex-ok-1',
        run_id: 'run-clean-1',
        task_id: 'task-1',
        tool_id: 'pwsh',
        status: 'completed',
        tool_success: true,
        command: 'pnpm typecheck',
        summary: '0 errors',
      }),
      makeExec({
        id: 'ex-ok-2',
        run_id: 'run-clean-1',
        task_id: 'task-2',
        tool_id: 'run_command',
        status: 'completed',
        tool_success: true,
        command: 'git status --short',
        summary: 'Clean working tree',
      }),
    ]

    const trajectory = buildTrajectoryTree({ snapshot, toolExecutions })
    const report = aggregateCodeExecutionSignals({
      trajectory,
      toolExecutions,
      runId: 'run-clean-1',
    })

    expect(report.totalCalls).toBe(2)
    expect(report.errorCount).toBe(0)
    expect(report.detectedSignalCount).toBe(0)
    expect(report.signalGroups).toHaveLength(0)
    expect(report.succeededInvocations).toHaveLength(2)
  })
})
