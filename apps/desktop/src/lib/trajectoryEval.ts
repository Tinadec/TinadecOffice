/**
 * Pure computation helpers for Trajectory Run Evaluation (`TrajectoryEvalPanel.vue`):
 * - Per-turn / per-run scatter points (`EvalTurnScatterPoint[]`) with strict Data Honesty
 *   (`estimatedTokens: boolean` whenever output tokens are estimated or only partially recorded)
 * - Global summary KPIs (`EvalSummaryMetrics`)
 * - Log-compressed X/Y axis tick & coordinate scales (`buildEvalLogScale`)
 * - Default A/B comparison pair selection, delta calculation, and Markdown diagnostic draft builder
 */

import type {
  ModelInvocationDto,
  RunContextDiagnosticsDto,
  ToolExecutionTimelineItemDto,
} from '@/api'
import type { ObservabilityTurnEntry } from '@/composables/useObservabilityRunSelection'
import type {
  ContextWindowBucketReport,
  TrajectoryNode,
  TrajectorySummary,
} from '@/lib/trajectoryBuilder'

export interface EvalSlowestTool {
  toolId: string
  durationMs: number
  seq: number
  resultSeq?: number | null
}

export interface EvalTurnScatterPoint {
  id: string
  turnNo: number
  label: string
  durationMs: number
  outputTokens: number
  estimatedTokens: boolean
  callCount: number
  modelCallCount: number
  toolCallCount: number
  errorCount: number
  hasToolError: boolean
  slowestTool: EvalSlowestTool | null
  runId?: string | null
  recordedOutputCalls?: number
}

export interface EvalSummaryMetrics {
  totalDurationMs: number
  totalCalls: number
  okCalls: number
  errorCalls: number
  unrecordedCalls: number
  avgLatencyMs: number
  peakOccupancyPct: number
  errorTurnCount: number
  comparableTurnCount: number
  errorOutputSharePct: number
  medianOutputTokens: number
  totalTurns: number
  timedTurnCount: number
  outcomeCoveragePct: number
}

export interface TrajectoryEvalAggregation {
  points: EvalTurnScatterPoint[]
  summary: EvalSummaryMetrics
  hasAnyEstimatedTokens: boolean
}

export interface TrajectoryEvalInput {
  trajectory?: TrajectorySummary | null
  turns?: readonly ObservabilityTurnEntry[] | null
  modelInvocations?: readonly ModelInvocationDto[] | null
  toolExecutions?: readonly ToolExecutionTimelineItemDto[] | null
  buckets?: ContextWindowBucketReport | null
  diagnostics?: RunContextDiagnosticsDto | null
  runId?: string | null
  sessionId?: string | null
}

export interface EvalLogAxisScale {
  limit: number
  unit: number
  ticks: number[]
  place: (value: number) => number
}

export interface EvalCompareDelta {
  tokenDiff: number
  durationDiffMs: number
  durationDiffSec: number
  durationDiffLabel: string
  anyEstimatedTokens: boolean
}

export interface BuildEvalCompareDraftInput {
  turnA: EvalTurnScatterPoint
  turnB?: EvalTurnScatterPoint | null
  runId?: string | null
  sessionId?: string | null
}

export const EVAL_TOKEN_TICK_CANDIDATES: readonly number[] = [
  1,
  3,
  10,
  30,
  100,
  300,
  1_000,
  3_000,
  10_000,
  30_000,
  100_000,
  300_000,
  1_000_000,
  3_000_000,
]

export const EVAL_DURATION_TICK_CANDIDATES: readonly number[] = [
  1,
  3,
  10,
  30,
  100,
  300,
  1_000,
  3_000,
  10_000,
  30_000,
  60_000,
  120_000,
  300_000,
  600_000,
  1_800_000,
  3_600_000,
]

const ERROR_STATUS_SET = new Set([
  'failed',
  'error',
  'rejected',
  'denied',
  'timeout',
  'timed_out',
  'cancelled',
  'canceled',
  'aborted',
])

const OK_STATUS_SET = new Set([
  'completed',
  'succeeded',
  'success',
  'done',
  'ok',
  'returned',
])

function parseTs(value?: string | null): number {
  if (!value) return 0
  const ms = Date.parse(value)
  return Number.isFinite(ms) ? ms : 0
}

function durationBetween(start?: string | null, end?: string | null): number | null {
  const s = parseTs(start)
  const e = parseTs(end)
  if (s <= 0 || e <= 0 || e < s) return null
  return Math.round(e - s)
}

function isErrorStatus(status?: string | null): boolean {
  if (!status) return false
  return ERROR_STATUS_SET.has(status.trim().toLowerCase())
}

function isOkStatus(status?: string | null): boolean {
  if (!status) return false
  return OK_STATUS_SET.has(status.trim().toLowerCase())
}

function isToolExecError(exec: ToolExecutionTimelineItemDto): boolean {
  return (
    isErrorStatus(exec.status)
    || exec.tool_success === false
    || Boolean(exec.error_category)
  )
}

function isToolExecOk(exec: ToolExecutionTimelineItemDto): boolean {
  if (isToolExecError(exec)) return false
  if (exec.tool_success === true) return true
  return isOkStatus(exec.status)
}

/**
 * Conservative character-based token heuristic used ONLY when authoritative
 * `output_tokens` are absent, always paired with `estimatedTokens: true`.
 */
export function estimateTokensFromText(text: string | null | undefined): number {
  if (!text) return 0
  const trimmed = text.trim()
  if (!trimmed) return 0
  let cjkCount = 0
  let otherCount = 0
  for (let i = 0; i < trimmed.length; i += 1) {
    const code = trimmed.charCodeAt(i)
    if (code >= 0x2e80 && code <= 0x9fff) cjkCount += 1
    else otherCount += 1
  }
  return Math.max(1, Math.round(cjkCount / 1.6 + otherCount / 4))
}

function deriveExactInvocationOutputTokens(inv: ModelInvocationDto): number | null {
  if (typeof inv.output_tokens === 'number' && Number.isFinite(inv.output_tokens) && inv.output_tokens >= 0) {
    return Math.round(inv.output_tokens)
  }
  if (
    typeof inv.total_tokens === 'number'
    && Number.isFinite(inv.total_tokens)
    && typeof inv.input_tokens === 'number'
    && Number.isFinite(inv.input_tokens)
    && inv.total_tokens >= inv.input_tokens
  ) {
    return Math.round(inv.total_tokens - inv.input_tokens)
  }
  return null
}

function deriveExactNodeOutputTokens(node: TrajectoryNode): number | null {
  if (typeof node.outputTokens === 'number' && Number.isFinite(node.outputTokens) && node.outputTokens >= 0) {
    return Math.round(node.outputTokens)
  }
  if (
    typeof node.tokensUsed === 'number'
    && Number.isFinite(node.tokensUsed)
    && typeof node.inputTokens === 'number'
    && Number.isFinite(node.inputTokens)
    && node.tokensUsed >= node.inputTokens
  ) {
    return Math.round(node.tokensUsed - node.inputTokens)
  }
  return null
}

function computeMedian(values: readonly number[]): number {
  const valid = values.filter((v) => Number.isFinite(v) && v >= 0).sort((a, b) => a - b)
  if (valid.length === 0) return 0
  const mid = (valid.length - 1) / 2
  const lo = valid[Math.floor(mid)] ?? 0
  const hi = valid[Math.ceil(mid)] ?? lo
  return Math.round((lo + hi) / 2)
}

function extractExplicitTurnNumber(rawId: string, fallback: number): number {
  const match = rawId.match(/^(?:turn:)?(?:turn[-_:]?)?(\d+)$/i)
  if (match) {
    const parsed = Number.parseInt(match[1]!, 10)
    if (Number.isFinite(parsed) && parsed > 0) return parsed
  }
  return fallback
}

function pickSlowestFromToolExecs(
  execs: readonly ToolExecutionTimelineItemDto[],
): EvalSlowestTool | null {
  let best: EvalSlowestTool | null = null
  for (let i = 0; i < execs.length; i += 1) {
    const ex = execs[i]!
    const dur =
      typeof ex.duration_ms === 'number' && Number.isFinite(ex.duration_ms) && ex.duration_ms >= 0
        ? Math.round(ex.duration_ms)
        : durationBetween(ex.requested_at, ex.updated_at)
    if (dur === null || dur < 0) continue
    const seq = typeof ex.requested_seq === 'number' && ex.requested_seq > 0 ? ex.requested_seq : i + 1
    const resultSeq = typeof ex.updated_seq === 'number' && ex.updated_seq > 0 ? ex.updated_seq : null
    if (!best || dur > best.durationMs || (dur === best.durationMs && seq < best.seq)) {
      best = {
        toolId: ex.tool_id || ex.tool_display_name || 'tool',
        durationMs: dur,
        seq,
        resultSeq,
      }
    }
  }
  return best
}

function pickSlowestFromToolNodes(
  nodes: readonly TrajectoryNode[],
  seqByToolNodeId: ReadonlyMap<string, { seq: number; resultSeq: number | null }>,
): EvalSlowestTool | null {
  let best: EvalSlowestTool | null = null
  for (let i = 0; i < nodes.length; i += 1) {
    const n = nodes[i]!
    const dur =
      typeof n.durationMs === 'number' && Number.isFinite(n.durationMs) && n.durationMs >= 0
        ? Math.round(n.durationMs)
        : durationBetween(n.startedAt, n.completedAt)
    if (dur === null || dur < 0) continue
    const seqInfo = seqByToolNodeId.get(n.id)
    const seq = seqInfo?.seq ?? i + 1
    const resultSeq = seqInfo?.resultSeq ?? null
    if (!best || dur > best.durationMs || (dur === best.durationMs && seq < best.seq)) {
      best = {
        toolId: n.toolId || n.label || 'tool',
        durationMs: dur,
        seq,
        resultSeq,
      }
    }
  }
  return best
}

function buildPointFromTrajectoryTurnNode(
  turnNode: TrajectoryNode,
  turnNo: number,
  runId: string | null,
  invocationsById: ReadonlyMap<string, ModelInvocationDto>,
  toolExecsById: ReadonlyMap<string, ToolExecutionTimelineItemDto>,
  seqByToolNodeId: ReadonlyMap<string, { seq: number; resultSeq: number | null }>,
): EvalTurnScatterPoint {
  const modelNodes = turnNode.children.filter((c) => c.kind === 'model_call')
  const toolNodes = turnNode.children.filter((c) => c.kind === 'tool_call')
  const stepNodes = turnNode.children.filter((c) => c.kind === 'step')

  let exactOutputSum = 0
  let recordedOutputCalls = 0
  let estimatedExtraTokens = 0

  for (const mNode of modelNodes) {
    const rawId = mNode.id.startsWith('model:') ? mNode.id.slice(6) : mNode.id
    const dto = invocationsById.get(rawId)
    const exact = dto ? deriveExactInvocationOutputTokens(dto) : deriveExactNodeOutputTokens(mNode)
    if (exact !== null) {
      exactOutputSum += exact
      recordedOutputCalls += 1
    } else {
      const totalFallback = dto?.total_tokens ?? mNode.tokensUsed ?? null
      if (typeof totalFallback === 'number' && totalFallback > 0) {
        estimatedExtraTokens += Math.max(1, Math.round(totalFallback * 0.2))
      } else {
        estimatedExtraTokens += estimateTokensFromText(mNode.summary)
      }
    }
  }

  let outputTokens = 0
  let estimatedTokens = false

  if (modelNodes.length > 0 && recordedOutputCalls === modelNodes.length) {
    outputTokens = exactOutputSum
    estimatedTokens = false
  } else if (recordedOutputCalls > 0) {
    outputTokens = exactOutputSum + estimatedExtraTokens
    estimatedTokens = true
  } else if (typeof turnNode.outputTokens === 'number' && Number.isFinite(turnNode.outputTokens) && turnNode.outputTokens >= 0) {
    outputTokens = Math.round(turnNode.outputTokens)
    estimatedTokens = false
  } else if (typeof turnNode.tokensUsed === 'number' && Number.isFinite(turnNode.tokensUsed) && turnNode.tokensUsed > 0) {
    outputTokens = Math.max(1, Math.round(turnNode.tokensUsed * 0.2))
    estimatedTokens = true
  } else {
    const textCorpus = [
      turnNode.summary ?? '',
      turnNode.subtitle ?? '',
      ...stepNodes.map((s) => s.summary ?? s.label ?? ''),
      ...toolNodes.map((t) => t.summary ?? ''),
    ]
      .filter(Boolean)
      .join('\n')
    outputTokens = estimateTokensFromText(textCorpus)
    estimatedTokens = true
  }

  const matchedToolDtos: ToolExecutionTimelineItemDto[] = []
  for (const tNode of toolNodes) {
    const rawId = tNode.id.startsWith('tool:') ? tNode.id.slice(5) : tNode.id
    const dto = toolExecsById.get(rawId)
    if (dto) matchedToolDtos.push(dto)
  }

  const toolErrorCount = toolNodes.filter((t) => t.isError).length
  const slowestTool =
    matchedToolDtos.length === toolNodes.length && matchedToolDtos.length > 0
      ? pickSlowestFromToolExecs(matchedToolDtos)
      : pickSlowestFromToolNodes(toolNodes, seqByToolNodeId)

  const childDurSum = turnNode.children.reduce((acc, c) => acc + (c.durationMs ?? 0), 0)
  const rawTurnDur =
    typeof turnNode.durationMs === 'number' && Number.isFinite(turnNode.durationMs) && turnNode.durationMs >= 0
      ? Math.round(turnNode.durationMs)
      : durationBetween(turnNode.startedAt, turnNode.completedAt) ?? childDurSum
  const durationMs = Math.max(0, rawTurnDur)

  const modelCallCount = modelNodes.length
  const toolCallCount = toolNodes.length
  const callCount = toolCallCount > 0 ? toolCallCount : modelCallCount
  const hasToolError = toolErrorCount > 0 || (toolCallCount === 0 && turnNode.isError)
  const errorCount = toolErrorCount > 0 ? toolErrorCount : turnNode.isError ? 1 : 0

  return {
    id: turnNode.id,
    turnNo,
    label: turnNode.label || `Turn #${turnNo}`,
    durationMs,
    outputTokens,
    estimatedTokens,
    callCount,
    modelCallCount,
    toolCallCount,
    errorCount,
    hasToolError,
    slowestTool,
    runId,
    recordedOutputCalls,
  }
}

function buildPointsFromInvocationTurns(
  invocations: readonly ModelInvocationDto[],
  toolExecs: readonly ToolExecutionTimelineItemDto[],
  defaultRunId: string | null,
): EvalTurnScatterPoint[] {
  const groups = new Map<string, ModelInvocationDto[]>()
  const sortedInv = [...invocations].sort(
    (a, b) => parseTs(a.started_at) - parseTs(b.started_at) || a.id.localeCompare(b.id),
  )

  for (const inv of sortedInv) {
    const key = (inv.turn_id ?? '').trim() || 'turn-default'
    let list = groups.get(key)
    if (!list) {
      list = []
      groups.set(key, list)
    }
    list.push(inv)
  }

  const entries = [...groups.entries()].map(([turnKey, invs]) => {
    const startMs = Math.min(...invs.map((i) => parseTs(i.started_at)).filter((t) => t > 0))
    const endMs = Math.max(
      ...invs.map((i) => parseTs(i.completed_at ?? i.started_at)).filter((t) => t > 0),
    )
    return {
      turnKey,
      invs,
      startMs: Number.isFinite(startMs) ? startMs : 0,
      endMs: Number.isFinite(endMs) ? endMs : 0,
    }
  })

  entries.sort((a, b) => a.startMs - b.startMs)

  // Assign each tool execution to the latest turn window starting at or before `requested_at`
  const toolsByTurnIdx = new Map<number, ToolExecutionTimelineItemDto[]>()
  for (let i = 0; i < entries.length; i += 1) toolsByTurnIdx.set(i, [])

  const sortedTools = [...toolExecs].sort(
    (a, b) =>
      (a.requested_seq || 0) - (b.requested_seq || 0)
      || parseTs(a.requested_at) - parseTs(b.requested_at),
  )

  for (const ex of sortedTools) {
    const tMs = parseTs(ex.requested_at)
    let targetIdx = 0
    for (let i = 0; i < entries.length; i += 1) {
      if (entries[i]!.startMs <= tMs || i === 0) {
        targetIdx = i
      } else {
        break
      }
    }
    toolsByTurnIdx.get(targetIdx)!.push(ex)
  }

  const usedTurnNos = new Set<number>()
  return entries.map((entry, idx) => {
    let turnNo = extractExplicitTurnNumber(entry.turnKey, idx + 1)
    if (usedTurnNos.has(turnNo)) turnNo = idx + 1
    usedTurnNos.add(turnNo)

    const turnTools = toolsByTurnIdx.get(idx) ?? []
    let exactSum = 0
    let recordedCount = 0
    let estExtra = 0
    let invDurSum = 0

    for (const inv of entry.invs) {
      const exact = deriveExactInvocationOutputTokens(inv)
      if (exact !== null) {
        exactSum += exact
        recordedCount += 1
      } else if (typeof inv.total_tokens === 'number' && inv.total_tokens > 0) {
        estExtra += Math.max(1, Math.round(inv.total_tokens * 0.2))
      }
      invDurSum += durationBetween(inv.started_at, inv.completed_at) ?? 0
    }

    const toolDurSum = turnTools.reduce(
      (acc, t) => acc + (typeof t.duration_ms === 'number' && t.duration_ms > 0 ? t.duration_ms : 0),
      0,
    )
    const latestToolEnd = Math.max(
      0,
      ...turnTools.map((t) => parseTs(t.updated_at || t.requested_at)),
    )
    const maxSingleToolDur = turnTools.reduce(
      (m, t) => Math.max(m, typeof t.duration_ms === 'number' && t.duration_ms > 0 ? t.duration_ms : 0),
      0,
    )
    const spanEnd = Math.max(entry.endMs, latestToolEnd)
    const wallMs = entry.startMs > 0 && spanEnd > entry.startMs ? spanEnd - entry.startMs : 0
    const durationMs = wallMs > 0 ? Math.max(wallMs, maxSingleToolDur) : invDurSum + toolDurSum

    const estimatedTokens = recordedCount < entry.invs.length
    const outputTokens = exactSum + estExtra
    const toolErrors = turnTools.filter(isToolExecError).length
    const invErrors = entry.invs.filter(
      (i) => isErrorStatus(i.status) || Boolean(i.error_category) || Boolean(i.safe_error_message),
    ).length
    const errorCount = toolErrors + (turnTools.length === 0 ? invErrors : 0)

    return {
      id: `turn:${entry.turnKey}`,
      turnNo,
      label: `Turn #${turnNo}`,
      durationMs: Math.round(durationMs),
      outputTokens,
      estimatedTokens,
      callCount: turnTools.length > 0 ? turnTools.length : entry.invs.length,
      modelCallCount: entry.invs.length,
      toolCallCount: turnTools.length,
      errorCount,
      hasToolError: toolErrors > 0 || (turnTools.length === 0 && invErrors > 0),
      slowestTool: pickSlowestFromToolExecs(turnTools),
      runId: entry.invs[0]?.run_id ?? defaultRunId,
      recordedOutputCalls: recordedCount,
    }
  })
}

function buildPointsFromObservabilityTurns(
  turns: readonly ObservabilityTurnEntry[],
  trajectory: TrajectorySummary | null,
  invocations: readonly ModelInvocationDto[],
  toolExecs: readonly ToolExecutionTimelineItemDto[],
): EvalTurnScatterPoint[] {
  return turns.map((entry) => {
    const runInvocations = invocations.filter((i) => i.run_id === entry.runId)
    const runTools = toolExecs.filter((t) => t.run_id === entry.runId)
    const matchesActiveTrajectory = Boolean(trajectory?.runId && trajectory.runId === entry.runId)

    let exactSum = 0
    let recordedCount = 0
    let estExtra = 0
    for (const inv of runInvocations) {
      const exact = deriveExactInvocationOutputTokens(inv)
      if (exact !== null) {
        exactSum += exact
        recordedCount += 1
      } else if (typeof inv.total_tokens === 'number' && inv.total_tokens > 0) {
        estExtra += Math.max(1, Math.round(inv.total_tokens * 0.2))
      }
    }

    let outputTokens = 0
    let estimatedTokens = false

    if (runInvocations.length > 0 && recordedCount === runInvocations.length) {
      outputTokens = exactSum
      estimatedTokens = false
    } else if (recordedCount > 0) {
      outputTokens = exactSum + estExtra
      estimatedTokens = true
    } else if (matchesActiveTrajectory && trajectory && trajectory.totalTokens > 0) {
      const modelChildren = trajectory.root.children
        .flatMap((t) => t.children)
        .filter((c) => c.kind === 'model_call')
      const exactNodes = modelChildren
        .map(deriveExactNodeOutputTokens)
        .filter((v): v is number => v !== null)
      if (modelChildren.length > 0 && exactNodes.length === modelChildren.length) {
        outputTokens = exactNodes.reduce((a, b) => a + b, 0)
        estimatedTokens = false
      } else {
        outputTokens = Math.max(1, Math.round(trajectory.totalTokens * 0.2))
        estimatedTokens = true
      }
    } else {
      outputTokens = estimateTokensFromText(entry.headline)
      estimatedTokens = true
    }

    const toolErrorCount = runTools.filter(isToolExecError).length
    const fallbackTrajErrors = matchesActiveTrajectory && trajectory ? trajectory.errorCount : 0
    const errorCount =
      runTools.length > 0
        ? toolErrorCount
        : fallbackTrajErrors > 0
          ? fallbackTrajErrors
          : entry.isFailed
            ? 1
            : 0

    const modelCallCount =
      runInvocations.length > 0
        ? runInvocations.length
        : matchesActiveTrajectory && trajectory
          ? trajectory.totalModelCalls
          : 0
    const toolCallCount =
      runTools.length > 0
        ? runTools.length
        : matchesActiveTrajectory && trajectory
          ? trajectory.totalToolCalls
          : 0

    const durationMs = Math.max(
      0,
      entry.durationMs
        ?? durationBetween(entry.startedAt, entry.completedAt)
        ?? (matchesActiveTrajectory && trajectory ? trajectory.totalDurationMs : 0),
    )

    let slowestTool = pickSlowestFromToolExecs(runTools)
    if (!slowestTool && matchesActiveTrajectory && trajectory) {
      const allToolNodes = trajectory.root.children
        .flatMap((t) => t.children)
        .filter((c) => c.kind === 'tool_call')
      slowestTool = pickSlowestFromToolNodes(allToolNodes, new Map())
    }

    return {
      id: `run:${entry.runId}`,
      turnNo: entry.turnNo,
      label: entry.headline?.trim() || `Turn #${entry.turnNo}`,
      durationMs: Math.round(durationMs),
      outputTokens,
      estimatedTokens,
      callCount: toolCallCount > 0 ? toolCallCount : modelCallCount,
      modelCallCount,
      toolCallCount,
      errorCount,
      hasToolError: errorCount > 0 || entry.isFailed,
      slowestTool,
      runId: entry.runId,
      recordedOutputCalls: recordedCount,
    }
  })
}

/**
 * Aggregate per-turn scatter points and global summary KPIs from `trajectory`,
 * `turns`, `modelInvocations`, `toolExecutions`, `buckets`, and `diagnostics`.
 */
export function aggregateTrajectoryEval(input: TrajectoryEvalInput): TrajectoryEvalAggregation {
  const trajectory = input.trajectory ?? null
  const turns = input.turns ?? []
  const invocations = input.modelInvocations ?? []
  const toolExecs = input.toolExecutions ?? []
  const buckets = input.buckets ?? null
  const diagnostics = input.diagnostics ?? null
  const effectiveRunId = input.runId ?? trajectory?.runId ?? diagnostics?.run_id ?? null

  const invocationsById = new Map<string, ModelInvocationDto>()
  for (const inv of invocations) invocationsById.set(inv.id, inv)

  const toolExecsById = new Map<string, ToolExecutionTimelineItemDto>()
  const seqByToolNodeId = new Map<string, { seq: number; resultSeq: number | null }>()
  const sortedTools = [...toolExecs].sort(
    (a, b) =>
      (a.requested_seq || 0) - (b.requested_seq || 0)
      || parseTs(a.requested_at) - parseTs(b.requested_at),
  )
  sortedTools.forEach((ex, idx) => {
    toolExecsById.set(ex.id, ex)
    seqByToolNodeId.set(`tool:${ex.id}`, {
      seq: ex.requested_seq > 0 ? ex.requested_seq : idx + 1,
      resultSeq: ex.updated_seq > 0 ? ex.updated_seq : null,
    })
  })

  const trajectoryTurnNodes = (trajectory?.root.children ?? []).filter((c) => c.kind === 'turn')
  const distinctInvocationTurnIds = new Set(
    invocations.map((i) => (i.turn_id ?? '').trim()).filter(Boolean),
  )

  let points: EvalTurnScatterPoint[] = []

  if (distinctInvocationTurnIds.size >= 2) {
    points = buildPointsFromInvocationTurns(invocations, toolExecs, effectiveRunId)
  } else if (trajectoryTurnNodes.length >= 2) {
    const usedNos = new Set<number>()
    points = trajectoryTurnNodes.map((tNode, idx) => {
      let turnNo = extractExplicitTurnNumber(tNode.id, idx + 1)
      if (usedNos.has(turnNo)) turnNo = idx + 1
      usedNos.add(turnNo)
      return buildPointFromTrajectoryTurnNode(
        tNode,
        turnNo,
        effectiveRunId,
        invocationsById,
        toolExecsById,
        seqByToolNodeId,
      )
    })
  } else if (turns.length >= 2) {
    points = buildPointsFromObservabilityTurns(turns, trajectory, invocations, toolExecs)
  } else if (trajectoryTurnNodes.length === 1) {
    const singleNode = trajectoryTurnNodes[0]!
    const hasActivity =
      singleNode.children.length > 0
      || (singleNode.durationMs ?? 0) > 0
      || (singleNode.tokensUsed ?? 0) > 0
      || invocations.length > 0
      || toolExecs.length > 0
    if (hasActivity) {
      const matchedTurnEntry =
        (effectiveRunId ? turns.find((t) => t.runId === effectiveRunId) : null) ?? turns[0] ?? null
      const turnNo = matchedTurnEntry?.turnNo ?? extractExplicitTurnNumber(singleNode.id, 1)
      points = [
        buildPointFromTrajectoryTurnNode(
          singleNode,
          turnNo,
          effectiveRunId,
          invocationsById,
          toolExecsById,
          seqByToolNodeId,
        ),
      ]
    }
  } else if (invocations.length > 0 || toolExecs.length > 0) {
    points = buildPointsFromInvocationTurns(invocations, toolExecs, effectiveRunId)
  } else if (turns.length === 1) {
    const only = turns[0]!
    if ((only.durationMs ?? 0) > 0 || only.headline) {
      points = buildPointsFromObservabilityTurns(turns, trajectory, invocations, toolExecs)
    }
  }

  // Global call breakdown (Success · Error · Unrecorded)
  const allTrajToolNodes = trajectoryTurnNodes.flatMap((t) =>
    t.children.filter((c) => c.kind === 'tool_call'),
  )

  let okCalls = 0
  let errorCalls = 0
  let unrecordedCalls = 0
  const measuredCallLatencies: number[] = []

  if (toolExecs.length > 0) {
    for (const ex of toolExecs) {
      if (isToolExecError(ex)) errorCalls += 1
      else if (isToolExecOk(ex)) okCalls += 1
      else unrecordedCalls += 1

      const dur =
        typeof ex.duration_ms === 'number' && Number.isFinite(ex.duration_ms) && ex.duration_ms > 0
          ? ex.duration_ms
          : durationBetween(ex.requested_at, ex.updated_at)
      if (dur !== null && dur > 0) measuredCallLatencies.push(dur)
    }
  } else if (allTrajToolNodes.length > 0) {
    for (const n of allTrajToolNodes) {
      if (n.isError || isErrorStatus(n.status)) errorCalls += 1
      else if (isOkStatus(n.status)) okCalls += 1
      else unrecordedCalls += 1

      if (typeof n.durationMs === 'number' && n.durationMs > 0) {
        measuredCallLatencies.push(n.durationMs)
      }
    }
  } else if (diagnostics?.governance && (diagnostics.governance.total_tool_calls ?? 0) > 0) {
    const govTotal = Math.max(0, diagnostics.governance.total_tool_calls)
    errorCalls = Math.min(govTotal, Math.max(0, diagnostics.governance.error_count ?? 0))
    okCalls = Math.max(0, govTotal - errorCalls)
    unrecordedCalls = 0
  } else if (trajectory && trajectory.totalToolCalls > 0) {
    const tot = trajectory.totalToolCalls
    errorCalls = Math.min(tot, Math.max(0, trajectory.errorCount))
    okCalls = Math.max(0, tot - errorCalls)
    unrecordedCalls = 0
  } else if (invocations.length > 0) {
    for (const inv of invocations) {
      const invErr =
        isErrorStatus(inv.status) || Boolean(inv.error_category) || Boolean(inv.safe_error_message)
      if (invErr) errorCalls += 1
      else if (isOkStatus(inv.status)) okCalls += 1
      else unrecordedCalls += 1

      const dur = durationBetween(inv.started_at, inv.completed_at)
      if (dur !== null && dur > 0) measuredCallLatencies.push(dur)
    }
  } else if (points.length > 0) {
    for (const p of points) {
      errorCalls += p.errorCount
      okCalls += Math.max(0, p.callCount - p.errorCount)
    }
  }

  const totalCalls = okCalls + errorCalls + unrecordedCalls
  const recordedCalls = okCalls + errorCalls
  const outcomeCoveragePct =
    totalCalls > 0 ? Math.round((recordedCalls / totalCalls) * 1000) / 10 : 0

  const avgLatencyMs =
    measuredCallLatencies.length > 0
      ? Math.round(measuredCallLatencies.reduce((a, b) => a + b, 0) / measuredCallLatencies.length)
      : Math.round(
          diagnostics?.window_buckets?.avg_latency_ms
            ?? buckets?.avgLatencyMs
            ?? 0,
        )

  const peakRaw =
    diagnostics?.window_buckets?.peak_occupancy_pct
    ?? buckets?.peakOccupancyPct
    ?? 0
  const peakOccupancyPct = Math.round(Math.max(0, peakRaw) * 10) / 10

  const pointsDurationSum = points.reduce((acc, p) => acc + Math.max(0, p.durationMs), 0)
  const multiRunPoints = new Set(points.map((p) => p.runId).filter(Boolean)).size >= 2
  const totalDurationMs = multiRunPoints
    ? pointsDurationSum
    : trajectory && trajectory.totalDurationMs > 0
      ? Math.max(trajectory.totalDurationMs, pointsDurationSum)
      : pointsDurationSum

  const comparablePoints = points.filter(
    (p) => Number.isFinite(p.durationMs) && p.durationMs >= 0 && Number.isFinite(p.outputTokens) && p.outputTokens >= 0,
  )
  const comparableTurnCount = comparablePoints.length
  const errorPoints = comparablePoints.filter((p) => p.hasToolError)
  const errorTurnCount = errorPoints.length
  const totalComparableOutput = comparablePoints.reduce((acc, p) => acc + p.outputTokens, 0)
  const errorComparableOutput = errorPoints.reduce((acc, p) => acc + p.outputTokens, 0)
  const errorOutputSharePct =
    totalComparableOutput > 0
      ? Math.round((errorComparableOutput / totalComparableOutput) * 10000) / 100
      : 0

  const medianOutputTokens = computeMedian(comparablePoints.map((p) => p.outputTokens))
  const timedTurnCount = points.filter((p) => p.durationMs > 0).length
  const hasAnyEstimatedTokens = points.some((p) => p.estimatedTokens)

  return {
    points,
    hasAnyEstimatedTokens,
    summary: {
      totalDurationMs,
      totalCalls,
      okCalls,
      errorCalls,
      unrecordedCalls,
      avgLatencyMs,
      peakOccupancyPct,
      errorTurnCount,
      comparableTurnCount,
      errorOutputSharePct,
      medianOutputTokens,
      totalTurns: points.length,
      timedTurnCount,
      outcomeCoveragePct,
    },
  }
}

/**
 * Build a `log1p`-compressed axis scale matching `trajectory-tree` (`eval-overview.jpg`),
 * spacing ticks cleanly (`>= 13%` apart) across `0, 300, 1000, 3000` or `0, 3s, 10s, 30s`.
 */
export function buildEvalLogScale(
  maxValue: number,
  knee: number,
  candidates: readonly number[] = EVAL_TOKEN_TICK_CANDIDATES,
): EvalLogAxisScale {
  const safeMax = Number.isFinite(maxValue) && maxValue > 0 ? maxValue : 0
  const limit = Math.max(1, safeMax) * 1.12
  const unit = Math.max(1e-6, Math.min(Math.max(1, knee), limit / 8))
  const span = Math.max(1e-9, Math.log1p(limit / unit))

  const place = (value: number): number => {
    if (!Number.isFinite(value) || value <= 0) return 0
    const pct = (Math.log1p(value / unit) / span) * 100
    return Math.min(100, Math.max(0, pct))
  }

  const ticks: number[] = [0]
  for (const tick of candidates) {
    if (tick > limit) break
    const prev = ticks[ticks.length - 1]!
    if (place(tick) - place(prev) >= 13) {
      ticks.push(tick)
    }
  }

  return { limit, unit, ticks, place }
}

/**
 * Select a meaningful default Side A / Side B comparison pair when `>= 2` points exist:
 * prefers contrasting slowest vs fastest (or error vs clean turn with distinct `turnNo`).
 */
export function pickDefaultEvalComparePair(
  points: readonly EvalTurnScatterPoint[],
): [EvalTurnScatterPoint, EvalTurnScatterPoint] | null {
  if (points.length < 2) return null

  const sortedBySlowest = [...points].sort(
    (a, b) =>
      b.durationMs - a.durationMs
      || b.outputTokens - a.outputTokens
      || a.turnNo - b.turnNo,
  )
  const turnA = sortedBySlowest[0]!
  const rest = points.filter((p) => p.turnNo !== turnA.turnNo)
  if (rest.length === 0) return null

  const oppositeErrorPool = rest.filter((p) => p.hasToolError !== turnA.hasToolError)
  if (oppositeErrorPool.length > 0) {
    // Pair with the opposite-error turn that has the closest output tokens (like eval-overview.jpg) or fastest duration
    const byClosestOutput = [...oppositeErrorPool].sort(
      (a, b) =>
        Math.abs(a.outputTokens - turnA.outputTokens) - Math.abs(b.outputTokens - turnA.outputTokens)
        || a.durationMs - b.durationMs,
    )
    return [turnA, byClosestOutput[0]!]
  }

  const fastest = [...rest].sort(
    (a, b) => a.durationMs - b.durationMs || a.outputTokens - b.outputTokens || a.turnNo - b.turnNo,
  )[0]!
  return [turnA, fastest]
}

export function formatEvalSeconds(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '0s'
  if (ms < 1000) return `${Math.round(ms)}ms`
  const sec = ms / 1000
  if (sec < 60) {
    const rounded = Math.round(sec * 100) / 100
    return `${rounded}s`
  }
  const totalSec = Math.round(sec)
  const minutes = Math.floor(totalSec / 60)
  const remSec = totalSec % 60
  if (minutes < 60) {
    return `${minutes}m ${String(remSec).padStart(2, '0')}s`
  }
  const hours = Math.floor(minutes / 60)
  const remMin = minutes % 60
  return `${hours}h ${String(remMin).padStart(2, '0')}m`
}

export function formatEvalDuration(ms: number): string {
  return formatEvalSeconds(ms)
}

export function formatEvalTickDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '0'
  if (ms >= 3_600_000) return `${ms / 3_600_000}h`
  if (ms >= 60_000) return `${ms / 60_000}m`
  if (ms >= 1_000) return `${Math.round((ms / 1_000) * 10) / 10}s`
  return `${Math.round(ms)}ms`
}

export function formatEvalTokensWithHonesty(tokens: number, estimated: boolean): string {
  const safe = Number.isFinite(tokens) && tokens >= 0 ? Math.round(tokens) : 0
  return `${estimated ? '≈' : ''}${safe.toLocaleString('en-US')}`
}

export function computeEvalCompareDelta(
  turnA: EvalTurnScatterPoint,
  turnB: EvalTurnScatterPoint,
): EvalCompareDelta {
  const tokenDiff = Math.abs(Math.round(turnA.outputTokens) - Math.round(turnB.outputTokens))
  const durationDiffMs = Math.abs(Math.round(turnA.durationMs) - Math.round(turnB.durationMs))
  const durationDiffSec = Math.round((durationDiffMs / 1000) * 100) / 100
  const durationDiffLabel = formatEvalSeconds(durationDiffMs)
  const anyEstimatedTokens = Boolean(turnA.estimatedTokens || turnB.estimatedTokens)

  return {
    tokenDiff,
    durationDiffMs,
    durationDiffSec,
    durationDiffLabel,
    anyEstimatedTokens,
  }
}

function formatTurnBulletBlock(heading: string, pt: EvalTurnScatterPoint): string[] {
  const tokText = `${formatEvalTokensWithHonesty(pt.outputTokens, pt.estimatedTokens)} tokens${pt.estimatedTokens ? ' (estimated)' : ''}`
  const lines: string[] = [
    `#### ${heading}: Turn #${pt.turnNo} (${pt.label})`,
    `- **duration**: ${pt.durationMs} ms (${formatEvalSeconds(pt.durationMs)})`,
    `- **output_tokens**: ${tokText}`,
    `- **calls**: ${pt.modelCallCount} model call(s), ${pt.toolCallCount} tool call(s), ${pt.errorCount} error(s) (${pt.hasToolError ? 'tool_error_recorded' : 'clean'})`,
  ]
  if (pt.runId) {
    lines.push(`- **turn_run_id**: \`${pt.runId}\``)
  }
  if (pt.slowestTool) {
    lines.push(
      `- **slowest_tool**: \`${pt.slowestTool.toolId}\` — ${pt.slowestTool.durationMs} ms (seq #${pt.slowestTool.seq}${pt.slowestTool.resultSeq ? ` -> #${pt.slowestTool.resultSeq}` : ''})`,
    )
  }
  return lines
}

/**
 * Build a structured Markdown diagnostic draft for `问这轮` (single turn) or
 * `对照这两轮` (Side A vs Side B delta) targeting `tinadec_inspect`.
 */
export function buildEvalCompareDraft(input: BuildEvalCompareDraftInput): string {
  const { turnA, turnB = null } = input
  const effectiveRunId = input.runId ?? turnA.runId ?? turnB?.runId ?? null
  const sessionId = input.sessionId ?? null

  const lines: string[] = [
    turnB
      ? `Compare Turn #${turnA.turnNo} and Turn #${turnB.turnNo} execution metrics using read-only \`tinadec_inspect\` before proposing optimizations:`
      : `Diagnose Turn #${turnA.turnNo} latency, token output, and tool execution behavior using read-only \`tinadec_inspect\`:`,
    '',
    '### Context Reference',
  ]

  if (effectiveRunId) lines.push(`- **run_id**: \`${effectiveRunId}\``)
  if (sessionId) lines.push(`- **session_id**: \`${sessionId}\``)

  if (turnB) {
    const delta = computeEvalCompareDelta(turnA, turnB)
    const diffTokPrefix = delta.anyEstimatedTokens ? '≈' : ''
    lines.push(
      `- **comparison_pair**: Turn #${turnA.turnNo} vs Turn #${turnB.turnNo}`,
      `- **output_delta**: ${diffTokPrefix}${delta.tokenDiff.toLocaleString('en-US')} tokens`,
      `- **duration_delta**: ${delta.durationDiffMs} ms (${delta.durationDiffLabel})`,
      '',
      ...formatTurnBulletBlock('Side A', turnA),
      '',
      ...formatTurnBulletBlock('Side B', turnB),
      '',
      '### Requested Analysis',
      '1. Call `tinadec_inspect` (`run_diagnostics`, `context_audit`, and relevant tool/agent state) to inspect both turns.',
      `2. Explain what drove the ${delta.durationDiffLabel} duration gap and ${diffTokPrefix}${delta.tokenDiff.toLocaleString('en-US')} token output gap between Turn #${turnA.turnNo} and Turn #${turnB.turnNo}.`,
      '3. Recommend concrete, minimal adjustments to eliminate avoidable retries, slow tool bottlenecks, or context bloat.',
    )
  } else {
    lines.push(
      `- **target_turn**: Turn #${turnA.turnNo}`,
      '',
      ...formatTurnBulletBlock('Target Turn', turnA),
      '',
      '### Requested Analysis',
      '1. Call `tinadec_inspect` (`run_diagnostics`, `context_audit`, and relevant tool executions) to inspect Turn #' + turnA.turnNo + '.',
      '2. Identify the main driver of latency, output token volume, or tool errors in this turn.',
      '3. Propose minimal, evidence-backed next steps without bypassing governance checks.',
    )
  }

  return lines.join('\n')
}
