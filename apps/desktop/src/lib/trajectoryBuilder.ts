/**
 * Hierarchical Trajectory Tree builder, Tinadec governance/isolation control
 * signal detector, error code-frame & edit-count extractor, and 5% context
 * window occupancy bucket analyzer.
 */

import type {
  AgentAssignmentDto,
  ContextPackDto,
  ModelInvocationDto,
  OrchestrationSnapshotDto,
  StepResultDto,
  SupervisionFindingDto,
  TaskNodeDto,
  ToolExecutionTimelineItemDto,
} from '@/api'

export type GovernanceSignalCode =
  | 'APPROVAL_REJECTED'
  | 'FS_NOT_OBSERVED'
  | 'FS_STALE_VERSION'
  | 'WORKTREE_GUARD'
  | 'WRITE_SCOPE_LOCKED'
  | 'TIMEOUT'
  | 'SAME_ARGS_RETRY'

export type GovernanceSignalSeverity = 'danger' | 'warn' | 'neutral'

export interface GovernanceSignal {
  code: GovernanceSignalCode
  severity: GovernanceSignalSeverity
  detail?: string | null
  count?: number
}

export interface FileEditStats {
  added: number
  removed: number
  filePath?: string | null
}

export interface CodeFrameLine {
  lineNumber?: number | null
  text: string
  highlight?: boolean
}

export interface TrajectoryCodeFrame {
  id: string
  title: string
  filePath?: string | null
  line?: number | null
  column?: number | null
  command?: string | null
  cwd?: string | null
  errorCategory?: string | null
  lines: CodeFrameLine[]
}

export type TrajectoryNodeKind = 'run' | 'turn' | 'step' | 'model_call' | 'tool_call'

export interface TrajectoryNode {
  id: string
  parentId: string | null
  kind: TrajectoryNodeKind
  label: string
  subtitle?: string | null
  status: string
  isError: boolean
  startedAt?: string | null
  completedAt?: string | null
  durationMs: number | null
  tokensUsed?: number | null
  inputTokens?: number | null
  outputTokens?: number | null
  toolId?: string | null
  taskId?: string | null
  agentId?: string | null
  laneKey?: string | null
  resourcePath?: string | null
  command?: string | null
  argumentsSummary?: string | null
  summary?: string | null
  evidence: string[]
  editStats: FileEditStats | null
  signals: GovernanceSignal[]
  retryStreakCount?: number
  codeFrames: TrajectoryCodeFrame[]
  children: TrajectoryNode[]
}

export interface TrajectorySummary {
  runId: string | null
  sessionId: string | null
  status: string
  root: TrajectoryNode
  totalTurns: number
  totalSteps: number
  totalToolCalls: number
  totalModelCalls: number
  errorCount: number
  totalDurationMs: number
  totalTokens: number
  totalAddedLines: number
  totalRemovedLines: number
  signalCounts: Record<GovernanceSignalCode, number>
  activeSignals: GovernanceSignalCode[]
}

export interface ContextWindowBucket {
  /** 0..19 */
  index: number
  /** Inclusive lower bound in percent (0, 5, ..., 95). */
  rangeStart: number
  /** Upper bound in percent (5, 10, ..., 100). */
  rangeEnd: number
  /** Canonical label e.g. `0-5%`, `70-75%`, `95-100%`. */
  label: string
  sampleCount: number
  errorCount: number
  /** Error rate in `[0, 1]`. */
  errorRate: number
  avgLatencyMs: number
  maxLatencyMs: number
  avgOccupancyPct: number
}

export interface ContextWindowSamplePoint {
  id: string
  sourceKind: 'invocation' | 'tool' | 'context_pack'
  occupancyPct: number
  bucketIndex: number
  isError: boolean
  latencyMs: number
  tokensUsed: number
  tokenBudget: number
  timestamp: string
}

export interface ContextWindowBucketReport {
  buckets: ContextWindowBucket[]
  samples: ContextWindowSamplePoint[]
  totalSamples: number
  totalErrors: number
  overallErrorRate: number
  avgLatencyMs: number
  peakOccupancyPct: number
  lowOccupancyErrorRate: number
  highOccupancyErrorRate: number
  lowOccupancyAvgLatencyMs: number
  highOccupancyAvgLatencyMs: number
  degradationDetected: boolean
}

export interface TrajectoryBuildInput {
  snapshot: OrchestrationSnapshotDto | null
  toolExecutions: ToolExecutionTimelineItemDto[]
  modelInvocations?: ModelInvocationDto[]
  contextPacks?: ContextPackDto[]
  contextWindowTokens?: number | null
  coordinatorLabel?: string | null
}

export const ALL_GOVERNANCE_SIGNALS: GovernanceSignalCode[] = [
  'APPROVAL_REJECTED',
  'FS_NOT_OBSERVED',
  'FS_STALE_VERSION',
  'WORKTREE_GUARD',
  'WRITE_SCOPE_LOCKED',
  'TIMEOUT',
  'SAME_ARGS_RETRY',
]

const SIGNAL_SEVERITY: Record<GovernanceSignalCode, GovernanceSignalSeverity> = {
  APPROVAL_REJECTED: 'warn',
  FS_NOT_OBSERVED: 'warn',
  FS_STALE_VERSION: 'warn',
  WORKTREE_GUARD: 'danger',
  WRITE_SCOPE_LOCKED: 'danger',
  TIMEOUT: 'danger',
  SAME_ARGS_RETRY: 'warn',
}

export function signalSeverity(code: GovernanceSignalCode): GovernanceSignalSeverity {
  return SIGNAL_SEVERITY[code] ?? 'neutral'
}

function parseTs(value?: string | null): number {
  if (!value) return 0
  const ms = Date.parse(value)
  return Number.isFinite(ms) ? ms : 0
}

function durationBetween(start?: string | null, end?: string | null): number | null {
  const s = parseTs(start)
  const e = parseTs(end)
  if (s <= 0 || e <= 0 || e < s) return null
  return e - s
}

function isErrorStatus(status?: string | null): boolean {
  const s = (status ?? '').trim().toLowerCase()
  return (
    s === 'failed'
    || s === 'error'
    || s === 'rejected'
    || s === 'denied'
    || s === 'timeout'
    || s === 'timed_out'
    || s === 'cancelled'
    || s === 'aborted'
  )
}

/** Extract `+M/-N` file edit line counts from summary / arguments / evidence text. */
export function extractFileEditStats(exec: ToolExecutionTimelineItemDto): FileEditStats | null {
  const haystack = [
    exec.summary ?? '',
    exec.checkpoint_summary ?? '',
    exec.arguments_summary ?? '',
    ...(exec.evidence ?? []),
  ].join('\n')

  if (!haystack.trim()) return null

  // Pattern 1: explicit "+12/-4", "+12 -4", "(+12, -4)"
  const compact = haystack.match(/\+(\d+)\s*[/, ]\s*-(\d+)/)
  if (compact) {
    return {
      added: Number.parseInt(compact[1]!, 10),
      removed: Number.parseInt(compact[2]!, 10),
      filePath: exec.resource_path ?? null,
    }
  }

  // Pattern 2: git shortstat "12 insertions(+), 4 deletions(-)" or one-sided
  const ins = haystack.match(/(\d+)\s+insertions?\(\+\)/i)
  const del = haystack.match(/(\d+)\s+deletions?\(-\)/i)
  if (ins || del) {
    return {
      added: ins ? Number.parseInt(ins[1]!, 10) : 0,
      removed: del ? Number.parseInt(del[1]!, 10) : 0,
      filePath: exec.resource_path ?? null,
    }
  }

  // Pattern 3: explicit JSON/log fields "lines_added": 12 / "added 12 lines"
  const addedField = haystack.match(/(?:lines_added|added_lines|added)\s*[:=]?\s*(\d+)/i)
  const removedField = haystack.match(/(?:lines_removed|removed_lines|deleted_lines|removed|deleted)\s*[:=]?\s*(\d+)/i)
  if (addedField || removedField) {
    return {
      added: addedField ? Number.parseInt(addedField[1]!, 10) : 0,
      removed: removedField ? Number.parseInt(removedField[1]!, 10) : 0,
      filePath: exec.resource_path ?? null,
    }
  }

  // Pattern 4: unified diff hunk lines inside evidence
  let diffAdded = 0
  let diffRemoved = 0
  let sawHunk = false
  for (const line of haystack.split(/\r?\n/)) {
    if (line.startsWith('@@ ')) {
      sawHunk = true
      continue
    }
    if (!sawHunk) continue
    if (line.startsWith('+') && !line.startsWith('+++')) diffAdded++
    else if (line.startsWith('-') && !line.startsWith('---')) diffRemoved++
  }
  if (sawHunk && (diffAdded > 0 || diffRemoved > 0)) {
    return {
      added: diffAdded,
      removed: diffRemoved,
      filePath: exec.resource_path ?? null,
    }
  }

  return null
}

/** Detect Tinadec governance & isolation signals on a single tool execution. */
export function detectToolGovernanceSignals(exec: ToolExecutionTimelineItemDto): GovernanceSignal[] {
  const status = (exec.status ?? '').toLowerCase()
  const errCat = (exec.error_category ?? '').toLowerCase()
  const blob = [
    status,
    errCat,
    exec.summary ?? '',
    exec.checkpoint_summary ?? '',
    ...(exec.event_types ?? []),
    ...(exec.evidence ?? []),
  ]
    .join(' ')
    .toLowerCase()

  const found = new Map<GovernanceSignalCode, GovernanceSignal>()
  const add = (code: GovernanceSignalCode, detail?: string) => {
    if (!found.has(code)) {
      found.set(code, { code, severity: signalSeverity(code), detail: detail ?? null })
    }
  }

  if (
    status === 'rejected'
    || status === 'denied'
    || status === 'approval_rejected'
    || errCat.includes('approval_rejected')
    || errCat.includes('approval_denied')
    || /\b(approval[_\s-]*(rejected|denied)|user rejected|denied approval)\b/i.test(blob)
  ) {
    add('APPROVAL_REJECTED', exec.summary || exec.error_category || undefined)
  }

  if (
    errCat.includes('fs_not_observed')
    || errCat.includes('not_observed')
    || /\b(fs_not_observed|file[_\s-]*not[_\s-]*observed|must read before edit|unobserved file|read_file before)\b/i.test(blob)
  ) {
    add('FS_NOT_OBSERVED', exec.resource_path || exec.summary || undefined)
  }

  if (
    errCat.includes('fs_stale_version')
    || errCat.includes('stale_version')
    || errCat.includes('version_conflict')
    || /\b(fs_stale_version|stale[_\s-]*version|line_hash mismatch|hash mismatch|etag mismatch|modified since last read|precondition failed)\b/i.test(blob)
  ) {
    add('FS_STALE_VERSION', exec.resource_path || exec.summary || undefined)
  }

  if (
    errCat.includes('worktree_guard')
    || errCat.includes('worktree')
    || /\b(worktree[_\s-]*guard|outside worktree|worktree isolation|dirty worktree|branch guard)\b/i.test(blob)
  ) {
    add('WORKTREE_GUARD', exec.resource_path || exec.cwd || exec.summary || undefined)
  }

  if (
    errCat.includes('write_scope_locked')
    || errCat.includes('write_scope')
    || errCat.includes('scope_violation')
    || errCat.includes('lease_conflict')
    || /\b(write_scope_locked|outside write_scope|write_scope violation|scope\.violation|lease\.conflict|lease[_\s-]*conflict|resource[_\s-]*locked)\b/i.test(blob)
  ) {
    add('WRITE_SCOPE_LOCKED', exec.resource_path || exec.summary || undefined)
  }

  if (
    status === 'timeout'
    || status === 'timed_out'
    || errCat.includes('timeout')
    || /\b(timed?\s*out|deadline exceeded|did not answer within|gave up on .+ after \d+)\b/i.test(blob)
  ) {
    add('TIMEOUT', exec.summary || `${exec.duration_ms}ms`)
  }

  if (
    errCat.includes('same_args_retry')
    || errCat.includes('duplicate_call')
    || /\b(same_args_retry|repeated identical arguments)\b/i.test(blob)
  ) {
    add('SAME_ARGS_RETRY', exec.arguments_summary || exec.command || undefined)
  }

  return [...found.values()]
}

function detectFindingSignals(findings: SupervisionFindingDto[]): GovernanceSignal[] {
  const map = new Map<GovernanceSignalCode, GovernanceSignal>()
  for (const finding of findings) {
    const text = `${finding.category ?? ''} ${finding.summary ?? ''} ${finding.recommendation ?? ''}`.toLowerCase()
    const push = (code: GovernanceSignalCode) => {
      if (!map.has(code)) {
        map.set(code, { code, severity: signalSeverity(code), detail: finding.summary || finding.category })
      }
    }
    if (text.includes('write_scope') || text.includes('scope violation') || text.includes('lease')) push('WRITE_SCOPE_LOCKED')
    if (text.includes('worktree')) push('WORKTREE_GUARD')
    if (text.includes('approval') && (text.includes('reject') || text.includes('denied'))) push('APPROVAL_REJECTED')
    if (text.includes('timeout') || text.includes('timed out')) push('TIMEOUT')
    if (text.includes('stale') && text.includes('version')) push('FS_STALE_VERSION')
    if (text.includes('not_observed') || text.includes('not observed')) push('FS_NOT_OBSERVED')
    if (text.includes('same_args') || text.includes('retry loop')) push('SAME_ARGS_RETRY')
  }
  return [...map.values()]
}

function canonicalToolArgSignature(exec: ToolExecutionTimelineItemDto): string | null {
  const payload = [
    exec.arguments_summary ?? '',
    exec.command ?? '',
    exec.resource_path ?? '',
    exec.query ?? '',
  ]
    .map((s) => s.trim().replace(/\s+/g, ' ').toLowerCase())
    .filter(Boolean)
    .join('|')

  if (!payload) return null
  return `${exec.tool_id.toLowerCase()}::${payload}`
}

/**
 * Repeated-args streak detector across chronologically ordered tool executions.
 * Returns a Map from `toolExecution.id` -> streak count (`>= 2`) for repeated
 * consecutive calls with identical `(tool_id, normalized arguments)` within the
 * same agent/task scope.
 */
export function detectRepeatedArgsStreaks(
  toolExecutions: ToolExecutionTimelineItemDto[],
): Map<string, number> {
  const streaks = new Map<string, number>()
  const sorted = [...toolExecutions].sort(
    (a, b) =>
      (a.requested_seq || 0) - (b.requested_seq || 0)
      || parseTs(a.requested_at) - parseTs(b.requested_at)
      || a.id.localeCompare(b.id),
  )

  const stateByScope = new Map<string, { sig: string; count: number }>()

  for (const exec of sorted) {
    const scope = exec.agent_instance_id || exec.task_id || exec.run_id || 'default'
    const sig = canonicalToolArgSignature(exec)
    if (!sig) {
      stateByScope.delete(scope)
      continue
    }
    const prev = stateByScope.get(scope)
    if (prev && prev.sig === sig) {
      prev.count += 1
      streaks.set(exec.id, prev.count)
    } else {
      stateByScope.set(scope, { sig, count: 1 })
    }
  }

  return streaks
}

/** Extract code frames when tool/command errors occur. */
export function extractErrorCodeFrames(exec: ToolExecutionTimelineItemDto): TrajectoryCodeFrame[] {
  const failed =
    isErrorStatus(exec.status)
    || exec.tool_success === false
    || Boolean(exec.error_category)
  if (!failed) return []

  const rawLines = [
    ...(exec.evidence ?? []).flatMap((item) => item.split(/\r?\n/)),
    ...(exec.summary ? exec.summary.split(/\r?\n/) : []),
  ]
    .map((line) => line.replace(/\r$/, ''))
    .filter((line) => line.trim().length > 0)

  const frames: TrajectoryCodeFrame[] = []
  const fileLineRe = /((?:[A-Za-z]:[\\/]|[./\\]|\b[\w-]+[/\\])[\w./\\-]+\.[A-Za-z0-9]{1,8})(?::(\d+)(?::(\d+))?|\((\d+),(\d+)\))/
  const numberedLineRe = /^\s*(?:>|→)?\s*(\d+)\s*[|:]\s?(.*)$/

  let detectedPath: string | null = exec.resource_path ?? null
  let detectedLine: number | null = null
  let detectedCol: number | null = null
  const parsedLines: CodeFrameLine[] = []

  for (const line of rawLines) {
    const fileMatch = line.match(fileLineRe)
    if (fileMatch && detectedLine === null) {
      detectedPath = fileMatch[1] ?? detectedPath
      detectedLine = Number.parseInt(fileMatch[2] ?? fileMatch[4] ?? '', 10) || null
      detectedCol = Number.parseInt(fileMatch[3] ?? fileMatch[5] ?? '', 10) || null
    }

    const numMatch = line.match(numberedLineRe)
    if (numMatch) {
      const ln = Number.parseInt(numMatch[1]!, 10)
      parsedLines.push({
        lineNumber: ln,
        text: numMatch[2] ?? '',
        highlight: line.trimStart().startsWith('>') || line.trimStart().startsWith('→') || (detectedLine !== null && ln === detectedLine),
      })
    } else if (parsedLines.length < 10) {
      parsedLines.push({
        lineNumber: null,
        text: line,
        highlight: /(error|exception|failed|rejected|conflict|violation|timeout|stale|not_observed)/i.test(line),
      })
    }
  }

  if (parsedLines.length === 0) {
    const fallbackText = exec.summary || exec.error_category || `Tool ${exec.tool_id} failed (${exec.status})`
    parsedLines.push({ lineNumber: detectedLine, text: fallbackText, highlight: true })
  }

  const title = detectedPath
    ? detectedLine
      ? `${detectedPath}:${detectedLine}${detectedCol ? `:${detectedCol}` : ''}`
      : detectedPath
    : exec.command
      ? `$ ${exec.command}`
      : exec.tool_display_name || exec.tool_id

  frames.push({
    id: `frame:${exec.id}:0`,
    title,
    filePath: detectedPath,
    line: detectedLine,
    column: detectedCol,
    command: exec.command ?? null,
    cwd: exec.cwd ?? null,
    errorCategory: exec.error_category ?? null,
    lines: parsedLines.slice(0, 12),
  })

  return frames
}

function mergeSignals(lists: GovernanceSignal[][]): GovernanceSignal[] {
  const byCode = new Map<GovernanceSignalCode, GovernanceSignal>()
  for (const list of lists) {
    for (const sig of list) {
      const existing = byCode.get(sig.code)
      if (!existing) {
        byCode.set(sig.code, { ...sig, count: sig.count ?? 1 })
      } else {
        existing.count = (existing.count ?? 1) + (sig.count ?? 1)
        if (!existing.detail && sig.detail) existing.detail = sig.detail
      }
    }
  }
  return [...byCode.values()]
}

function sumEditStats(nodes: TrajectoryNode[]): FileEditStats | null {
  let added = 0
  let removed = 0
  let any = false
  for (const n of nodes) {
    if (n.editStats) {
      added += n.editStats.added
      removed += n.editStats.removed
      any = true
    }
  }
  return any ? { added, removed } : null
}

/**
 * Build a hierarchical trajectory tree (`run -> turn/teammate -> step/tool_call/model_call`).
 */
export function buildTrajectoryTree(input: TrajectoryBuildInput): TrajectorySummary {
  const snapshot = input.snapshot
  const run = snapshot?.run ?? null
  const runId = run?.id ?? input.toolExecutions[0]?.run_id ?? input.modelInvocations?.[0]?.run_id ?? null
  const sessionId = run?.session_id ?? input.toolExecutions[0]?.session_id ?? input.modelInvocations?.[0]?.session_id ?? null
  const tasks = snapshot?.nodes ?? []
  const assignments = snapshot?.assignments ?? []
  const stepResults = snapshot?.step_results ?? []
  const findings = snapshot?.supervision_findings ?? []
  const toolExecs = input.toolExecutions ?? []
  const invocations = input.modelInvocations ?? []

  const assignmentByTaskId = new Map<string, AgentAssignmentDto>()
  const assignmentByAgentId = new Map<string, AgentAssignmentDto>()
  for (const a of assignments) {
    if (a.task_node_id) assignmentByTaskId.set(a.task_node_id, a)
    if (a.agent_id) assignmentByAgentId.set(a.agent_id, a)
  }

  const taskById = new Map<string, TaskNodeDto>()
  for (const task of tasks) {
    taskById.set(task.id, task)
  }

  const retryStreaks = detectRepeatedArgsStreaks(toolExecs)

  // Map turn buckets (task/teammate/coordinator turns)
  const turnMap = new Map<string, TrajectoryNode>()
  const ensureTurn = (key: string, init: () => Omit<TrajectoryNode, 'children' | 'signals' | 'editStats' | 'codeFrames' | 'evidence'>): TrajectoryNode => {
    let existing = turnMap.get(key)
    if (!existing) {
      const base = init()
      existing = {
        ...base,
        evidence: [],
        editStats: null,
        signals: [],
        codeFrames: [],
        children: [],
      }
      turnMap.set(key, existing)
    }
    return existing
  }

  // Seed turns from task nodes so even pending/quiet teammates appear in hierarchy
  for (const task of tasks) {
    const assign = assignmentByTaskId.get(task.id)
    ensureTurn(`task:${task.id}`, () => ({
      id: `turn:task:${task.id}`,
      parentId: `run:${runId ?? 'active'}`,
      kind: 'turn',
      label: assign?.agent_name ? `${assign.agent_name} · ${task.title}` : task.title,
      subtitle: task.description || null,
      status: task.status,
      isError: isErrorStatus(task.status),
      startedAt: task.created_at,
      completedAt: task.updated_at,
      durationMs: durationBetween(task.created_at, task.updated_at),
      tokensUsed: null,
      taskId: task.id,
      agentId: assign?.agent_id ?? null,
      laneKey: task.lane_key || null,
    }))
  }

  const resolveTurnFor = (
    taskId?: string | null,
    agentId?: string | null,
    turnId?: string | null,
    startedAt?: string | null,
  ): TrajectoryNode => {
    if (taskId && taskById.has(taskId)) {
      return turnMap.get(`task:${taskId}`)!
    }
    if (taskId) {
      return ensureTurn(`task:${taskId}`, () => ({
        id: `turn:task:${taskId}`,
        parentId: `run:${runId ?? 'active'}`,
        kind: 'turn',
        label: `Task ${taskId.slice(0, 8)}`,
        subtitle: null,
        status: 'running',
        isError: false,
        startedAt: startedAt ?? null,
        completedAt: null,
        durationMs: null,
        taskId,
        agentId: agentId ?? null,
      }))
    }
    if (agentId && assignmentByAgentId.has(agentId)) {
      const assign = assignmentByAgentId.get(agentId)!
      if (assign.task_node_id && turnMap.has(`task:${assign.task_node_id}`)) {
        return turnMap.get(`task:${assign.task_node_id}`)!
      }
      return ensureTurn(`agent:${agentId}`, () => ({
        id: `turn:agent:${agentId}`,
        parentId: `run:${runId ?? 'active'}`,
        kind: 'turn',
        label: assign.agent_name || agentId,
        subtitle: assign.agent_type || null,
        status: assign.status,
        isError: isErrorStatus(assign.status),
        startedAt: assign.created_at,
        completedAt: null,
        durationMs: null,
        agentId,
      }))
    }
    if (turnId) {
      return ensureTurn(`turn:${turnId}`, () => ({
        id: `turn:${turnId}`,
        parentId: `run:${runId ?? 'active'}`,
        kind: 'turn',
        label: `Turn ${turnId.slice(0, 8)}`,
        subtitle: null,
        status: run?.status ?? 'running',
        isError: false,
        startedAt: startedAt ?? null,
        completedAt: null,
        durationMs: null,
        agentId: agentId ?? null,
      }))
    }
    return ensureTurn('coordinator', () => ({
      id: 'turn:coordinator',
      parentId: `run:${runId ?? 'active'}`,
      kind: 'turn',
      label: input.coordinatorLabel?.trim() || 'Coordinator',
      subtitle: run?.summary || null,
      status: run?.status ?? 'completed',
      isError: isErrorStatus(run?.status),
      startedAt: run?.created_at ?? startedAt ?? null,
      completedAt: run?.updated_at ?? null,
      durationMs: durationBetween(run?.created_at, run?.updated_at),
      laneKey: 'main',
    }))
  }

  // Attach StepResults
  for (const step of stepResults) {
    const turn = resolveTurnFor(step.task_node_id || null, step.agent_id || null, null, step.created_at)
    const stepIsError = isErrorStatus(step.status)
    const stepFrames: TrajectoryCodeFrame[] = stepIsError
      ? [
          {
            id: `frame:step:${step.id}`,
            title: step.summary || `Step ${step.id.slice(0, 8)}`,
            errorCategory: step.status,
            lines: (step.evidence.length > 0 ? step.evidence : [step.summary || step.status]).map((text) => ({
              text,
              highlight: true,
            })),
          },
        ]
      : []

    turn.children.push({
      id: `step:${step.id}`,
      parentId: turn.id,
      kind: 'step',
      label: step.summary || `Step ${step.id.slice(0, 8)}`,
      subtitle: step.agent_id || null,
      status: step.status,
      isError: stepIsError,
      startedAt: step.created_at,
      completedAt: step.created_at,
      durationMs: null,
      taskId: step.task_node_id || null,
      agentId: step.agent_id || null,
      summary: step.summary,
      evidence: step.evidence ?? [],
      editStats: null,
      signals: [],
      codeFrames: stepFrames,
      children: [],
    })
  }

  // Attach ModelInvocations
  for (const inv of invocations) {
    const turn = resolveTurnFor(null, inv.agent_instance_id ?? null, inv.turn_id ?? null, inv.started_at)
    const invDuration = durationBetween(inv.started_at, inv.completed_at)
    const invIsError = isErrorStatus(inv.status) || Boolean(inv.error_category) || Boolean(inv.safe_error_message)
    const invSignals: GovernanceSignal[] = []
    if ((inv.error_category ?? '').toLowerCase().includes('timeout') || (inv.safe_error_message ?? '').toLowerCase().includes('timeout')) {
      invSignals.push({ code: 'TIMEOUT', severity: 'danger', detail: inv.safe_error_message || inv.error_category })
    }
    const invFrames: TrajectoryCodeFrame[] = invIsError
      ? [
          {
            id: `frame:model:${inv.id}`,
            title: inv.model ? `${inv.model} (${inv.protocol})` : inv.call_id,
            errorCategory: inv.error_category ?? inv.status,
            lines: [
              {
                text: inv.safe_error_message || inv.error_category || `Model invocation failed (${inv.status})`,
                highlight: true,
              },
            ],
          },
        ]
      : []

    turn.children.push({
      id: `model:${inv.id}`,
      parentId: turn.id,
      kind: 'model_call',
      label: inv.model || inv.protocol || 'Model Call',
      subtitle: inv.attempt > 1 ? `attempt #${inv.attempt}` : inv.strategy_source || null,
      status: inv.status,
      isError: invIsError,
      startedAt: inv.started_at,
      completedAt: inv.completed_at ?? null,
      durationMs: invDuration,
      tokensUsed: inv.total_tokens ?? ((inv.input_tokens ?? 0) + (inv.output_tokens ?? 0) || null),
      inputTokens: inv.input_tokens ?? null,
      outputTokens: inv.output_tokens ?? null,
      agentId: inv.agent_instance_id ?? null,
      summary: inv.safe_error_message ?? null,
      evidence: inv.safe_error_message ? [inv.safe_error_message] : [],
      editStats: null,
      signals: invSignals,
      codeFrames: invFrames,
      children: [],
    })
  }

  // Attach ToolExecutions
  for (const exec of toolExecs) {
    const turn = resolveTurnFor(exec.task_id ?? null, exec.agent_instance_id ?? null, null, exec.requested_at)
    const signals = detectToolGovernanceSignals(exec)
    const streak = retryStreaks.get(exec.id)
    if (streak && streak >= 2 && !signals.some((s) => s.code === 'SAME_ARGS_RETRY')) {
      signals.push({
        code: 'SAME_ARGS_RETRY',
        severity: 'warn',
        detail: `${exec.tool_id} repeated ${streak}x with identical args`,
        count: streak,
      })
    }

    const editStats = extractFileEditStats(exec)
    const codeFrames = extractErrorCodeFrames(exec)
    const execIsError =
      isErrorStatus(exec.status)
      || exec.tool_success === false
      || Boolean(exec.error_category)

    turn.children.push({
      id: `tool:${exec.id}`,
      parentId: turn.id,
      kind: 'tool_call',
      label: exec.tool_display_name || exec.tool_id,
      subtitle: exec.resource_path || exec.command || exec.query || exec.arguments_summary || null,
      status: exec.status,
      isError: execIsError,
      startedAt: exec.requested_at,
      completedAt: exec.updated_at,
      durationMs: Number.isFinite(exec.duration_ms) ? exec.duration_ms : durationBetween(exec.requested_at, exec.updated_at),
      toolId: exec.tool_id,
      taskId: exec.task_id ?? null,
      agentId: exec.agent_instance_id ?? null,
      resourcePath: exec.resource_path ?? null,
      command: exec.command ?? null,
      argumentsSummary: exec.arguments_summary ?? null,
      summary: exec.summary || exec.checkpoint_summary || null,
      evidence: exec.evidence ?? [],
      editStats,
      signals,
      retryStreakCount: streak,
      codeFrames,
      children: [],
    })
  }

  // Sort children chronologically inside each turn and roll up metrics
  const turns = [...turnMap.values()].sort(
    (a, b) => parseTs(a.startedAt) - parseTs(b.startedAt) || a.id.localeCompare(b.id),
  )

  for (const turn of turns) {
    turn.children.sort(
      (a, b) => parseTs(a.startedAt) - parseTs(b.startedAt) || a.id.localeCompare(b.id),
    )
    const childTokens = turn.children.reduce((sum, c) => sum + (c.tokensUsed ?? 0), 0)
    const childDur = turn.children.reduce((sum, c) => sum + (c.durationMs ?? 0), 0)
    if (childTokens > 0) turn.tokensUsed = childTokens
    if (turn.durationMs === null && childDur > 0) turn.durationMs = childDur
    turn.editStats = sumEditStats(turn.children)
    turn.signals = mergeSignals(turn.children.map((c) => c.signals))
    turn.isError = turn.isError || turn.children.some((c) => c.isError)
  }

  const findingSignals = detectFindingSignals(findings)
  const rootSignals = mergeSignals([findingSignals, ...turns.map((t) => t.signals)])
  const rootEditStats = sumEditStats(turns)
  const totalTokens = turns.reduce((sum, t) => sum + (t.tokensUsed ?? 0), 0)
  const runDuration =
    durationBetween(run?.created_at, run?.updated_at)
    ?? turns.reduce((sum, t) => sum + (t.durationMs ?? 0), 0)

  const root: TrajectoryNode = {
    id: `run:${runId ?? 'active'}`,
    parentId: null,
    kind: 'run',
    label: run?.summary?.trim() ? run.summary.trim() : runId ? `Run ${runId.slice(0, 8)}` : 'Active Run',
    subtitle: runId ? `run_id: ${runId}` : null,
    status: run?.status ?? (turns.some((t) => t.isError) ? 'failed' : 'completed'),
    isError: isErrorStatus(run?.status) || turns.some((t) => t.isError),
    startedAt: run?.created_at ?? turns[0]?.startedAt ?? null,
    completedAt: run?.updated_at ?? turns[turns.length - 1]?.completedAt ?? null,
    durationMs: runDuration,
    tokensUsed: totalTokens > 0 ? totalTokens : null,
    summary: run?.summary ?? null,
    evidence: findings.map((f) => f.summary).filter(Boolean),
    editStats: rootEditStats,
    signals: rootSignals,
    codeFrames: [],
    children: turns,
  }

  const signalCounts: Record<GovernanceSignalCode, number> = {
    APPROVAL_REJECTED: 0,
    FS_NOT_OBSERVED: 0,
    FS_STALE_VERSION: 0,
    WORKTREE_GUARD: 0,
    WRITE_SCOPE_LOCKED: 0,
    TIMEOUT: 0,
    SAME_ARGS_RETRY: 0,
  }

  let totalSteps = 0
  let totalToolCalls = 0
  let totalModelCalls = 0
  let errorCount = 0

  for (const turn of turns) {
    for (const child of turn.children) {
      if (child.kind === 'step') totalSteps++
      else if (child.kind === 'tool_call') totalToolCalls++
      else if (child.kind === 'model_call') totalModelCalls++
      if (child.isError) errorCount++
      for (const sig of child.signals) {
        signalCounts[sig.code] = (signalCounts[sig.code] ?? 0) + 1
      }
    }
  }
  for (const sig of findingSignals) {
    signalCounts[sig.code] = (signalCounts[sig.code] ?? 0) + 1
  }

  const activeSignals = ALL_GOVERNANCE_SIGNALS.filter((code) => signalCounts[code] > 0)

  return {
    runId,
    sessionId,
    status: root.status,
    root,
    totalTurns: turns.length,
    totalSteps,
    totalToolCalls,
    totalModelCalls,
    errorCount,
    totalDurationMs: runDuration ?? 0,
    totalTokens,
    totalAddedLines: rootEditStats?.added ?? 0,
    totalRemovedLines: rootEditStats?.removed ?? 0,
    signalCounts,
    activeSignals,
  }
}

export function flattenTrajectoryNodes(root: TrajectoryNode): TrajectoryNode[] {
  const out: TrajectoryNode[] = []
  const walk = (node: TrajectoryNode) => {
    out.push(node)
    for (const child of node.children) walk(child)
  }
  walk(root)
  return out
}

export function findTrajectoryNode(root: TrajectoryNode, id: string | null | undefined): TrajectoryNode | null {
  if (!id) return null
  if (root.id === id) return root
  for (const child of root.children) {
    const hit = findTrajectoryNode(child, id)
    if (hit) return hit
  }
  return null
}

function findNearestPack(packs: ContextPackDto[], tsMs: number): ContextPackDto | null {
  if (packs.length === 0) return null
  let best: ContextPackDto | null = null
  for (const pack of packs) {
    const pTs = parseTs(pack.created_at)
    if (pTs <= tsMs || !best) {
      best = pack
    } else {
      break
    }
  }
  return best ?? packs[packs.length - 1] ?? null
}

/**
 * Compute 5% context-window occupancy buckets (`0-5%` .. `95-100%`)
 * correlating window fullness against error rate and average latency.
 */
export function buildContextWindowBuckets(input: {
  contextPacks?: ContextPackDto[]
  modelInvocations?: ModelInvocationDto[]
  toolExecutions?: ToolExecutionTimelineItemDto[]
  contextWindowTokens?: number | null
}): ContextWindowBucketReport {
  const sortedPacks = [...(input.contextPacks ?? [])].sort(
    (a, b) => parseTs(a.created_at) - parseTs(b.created_at) || a.id.localeCompare(b.id),
  )
  const invocations = input.modelInvocations ?? []
  const tools = input.toolExecutions ?? []
  const explicitWindow = input.contextWindowTokens && input.contextWindowTokens > 0 ? input.contextWindowTokens : null

  const samples: ContextWindowSamplePoint[] = []

  // Primary source: Model invocations paired with explicit window or nearest ContextPack budget.
  for (const inv of invocations) {
    const invTs = parseTs(inv.started_at)
    const pack = findNearestPack(sortedPacks, invTs)
    const budget = explicitWindow ?? (pack && pack.token_budget > 0 ? pack.token_budget : 0)
    const used = inv.input_tokens ?? pack?.estimated_tokens ?? null
    if (budget <= 0 || used === null || used < 0) continue

    const occupancyPct = Math.min(100, Math.max(0, (used / budget) * 100))
    const bucketIndex = Math.min(19, Math.max(0, Math.floor(occupancyPct / 5)))
    const latencyMs = durationBetween(inv.started_at, inv.completed_at) ?? 0
    const isError =
      isErrorStatus(inv.status)
      || Boolean(inv.error_category)
      || Boolean(inv.safe_error_message)

    samples.push({
      id: `inv:${inv.id}`,
      sourceKind: 'invocation',
      occupancyPct: Math.round(occupancyPct * 10) / 10,
      bucketIndex,
      isError,
      latencyMs,
      tokensUsed: used,
      tokenBudget: budget,
      timestamp: inv.started_at,
    })
  }

  // Secondary source when model invocations are absent or unpriced: pair tool executions with nearest ContextPack.
  if (samples.length === 0 && sortedPacks.length > 0 && tools.length > 0) {
    for (const exec of tools) {
      const pack = findNearestPack(sortedPacks, parseTs(exec.requested_at))
      if (!pack || pack.token_budget <= 0) continue
      const occupancyPct = Math.min(100, Math.max(0, (pack.estimated_tokens / pack.token_budget) * 100))
      const bucketIndex = Math.min(19, Math.max(0, Math.floor(occupancyPct / 5)))
      const isError =
        isErrorStatus(exec.status)
        || exec.tool_success === false
        || Boolean(exec.error_category)

      samples.push({
        id: `tool:${exec.id}`,
        sourceKind: 'tool',
        occupancyPct: Math.round(occupancyPct * 10) / 10,
        bucketIndex,
        isError,
        latencyMs: Math.max(0, exec.duration_ms || 0),
        tokensUsed: pack.estimated_tokens,
        tokenBudget: pack.token_budget,
        timestamp: exec.requested_at,
      })
    }
  }

  // Fallback when only context packs exist (snapshot-only view without tool calls or invocations)
  if (samples.length === 0 && sortedPacks.length > 0) {
    for (const pack of sortedPacks) {
      if (pack.token_budget <= 0) continue
      const occupancyPct = Math.min(100, Math.max(0, (pack.estimated_tokens / pack.token_budget) * 100))
      const bucketIndex = Math.min(19, Math.max(0, Math.floor(occupancyPct / 5)))
      const hasDrops = (pack.dropped_sources?.length ?? 0) > 0
      samples.push({
        id: `pack:${pack.id}`,
        sourceKind: 'context_pack',
        occupancyPct: Math.round(occupancyPct * 10) / 10,
        bucketIndex,
        isError: hasDrops,
        latencyMs: 0,
        tokensUsed: pack.estimated_tokens,
        tokenBudget: pack.token_budget,
        timestamp: pack.created_at,
      })
    }
  }

  const buckets: ContextWindowBucket[] = Array.from({ length: 20 }, (_, idx) => {
    const rangeStart = idx * 5
    const rangeEnd = (idx + 1) * 5
    return {
      index: idx,
      rangeStart,
      rangeEnd,
      label: `${rangeStart}-${rangeEnd}%`,
      sampleCount: 0,
      errorCount: 0,
      errorRate: 0,
      avgLatencyMs: 0,
      maxLatencyMs: 0,
      avgOccupancyPct: 0,
    }
  })

  const latencySums = new Float64Array(20)
  const occupancySums = new Float64Array(20)

  let totalErrors = 0
  let totalLatency = 0
  let peakOccupancyPct = 0

  let lowCount = 0
  let lowErrors = 0
  let lowLatency = 0
  let highCount = 0
  let highErrors = 0
  let highLatency = 0

  for (const s of samples) {
    const b = buckets[s.bucketIndex]!
    b.sampleCount += 1
    if (s.isError) {
      b.errorCount += 1
      totalErrors += 1
    }
    latencySums[s.bucketIndex] = (latencySums[s.bucketIndex] ?? 0) + s.latencyMs
    occupancySums[s.bucketIndex] = (occupancySums[s.bucketIndex] ?? 0) + s.occupancyPct
    if (s.latencyMs > b.maxLatencyMs) b.maxLatencyMs = s.latencyMs
    totalLatency += s.latencyMs
    if (s.occupancyPct > peakOccupancyPct) peakOccupancyPct = s.occupancyPct

    if (s.occupancyPct >= 70) {
      highCount++
      if (s.isError) highErrors++
      highLatency += s.latencyMs
    } else {
      lowCount++
      if (s.isError) lowErrors++
      lowLatency += s.latencyMs
    }
  }

  for (let i = 0; i < 20; i++) {
    const b = buckets[i]!
    if (b.sampleCount > 0) {
      b.errorRate = Math.round((b.errorCount / b.sampleCount) * 1000) / 1000
      b.avgLatencyMs = Math.round(latencySums[i]! / b.sampleCount)
      b.avgOccupancyPct = Math.round((occupancySums[i]! / b.sampleCount) * 10) / 10
    }
  }

  const totalSamples = samples.length
  const overallErrorRate = totalSamples > 0 ? Math.round((totalErrors / totalSamples) * 1000) / 1000 : 0
  const avgLatencyMs = totalSamples > 0 ? Math.round(totalLatency / totalSamples) : 0
  const lowOccupancyErrorRate = lowCount > 0 ? Math.round((lowErrors / lowCount) * 1000) / 1000 : 0
  const highOccupancyErrorRate = highCount > 0 ? Math.round((highErrors / highCount) * 1000) / 1000 : 0
  const lowOccupancyAvgLatencyMs = lowCount > 0 ? Math.round(lowLatency / lowCount) : 0
  const highOccupancyAvgLatencyMs = highCount > 0 ? Math.round(highLatency / highCount) : 0

  const degradationDetected =
    highCount > 0
    && (
      highOccupancyErrorRate > lowOccupancyErrorRate + 0.1
      || (lowOccupancyAvgLatencyMs > 0 && highOccupancyAvgLatencyMs >= lowOccupancyAvgLatencyMs * 1.5)
    )

  return {
    buckets,
    samples,
    totalSamples,
    totalErrors,
    overallErrorRate,
    avgLatencyMs,
    peakOccupancyPct: Math.round(peakOccupancyPct * 10) / 10,
    lowOccupancyErrorRate,
    highOccupancyErrorRate,
    lowOccupancyAvgLatencyMs,
    highOccupancyAvgLatencyMs,
    degradationDetected,
  }
}

/**
 * Safe translator helper for Stage-1 components before Stage-2 locale keys land:
 * uses `te(key) ? t(key, params) : fallback` with simple `{param}` interpolation.
 */
export function resolveTrajectoryCopy(
  t: (key: string, params?: Record<string, unknown>) => string,
  te: (key: string) => boolean,
  key: string,
  fallback: string,
  params?: Record<string, unknown>,
): string {
  if (te(key)) {
    return params ? t(key, params) : t(key)
  }
  if (!params) return fallback
  return fallback.replace(/\{([A-Za-z0-9_]+)\}/g, (_, name: string) =>
    params[name] !== undefined && params[name] !== null ? String(params[name]) : `{${name}}`,
  )
}
