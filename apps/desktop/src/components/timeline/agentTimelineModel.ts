import type {
  ModelInvocationDto,
  OrchestrationSnapshotDto,
  StepResultDto,
  TaskNodeDto,
  ToolExecutionTimelineItemDto,
  TopologyRunDto,
} from '../../api'
import {
  buildRuntimeTopology,
  formatRuntimeEngineBadge,
  normalizeTopologyStatus,
  type RuntimeEngineBadge,
  type RuntimeEngineKind,
  type RuntimeEngineMetaInput,
  type RuntimeTopologyNode,
  type RuntimeTopologyStatus,
} from '../OrchestrationMiniGraph.vue'

export type AgentTimelineSpanKind = 'thinking' | 'tool' | 'waiting' | 'failed' | 'retry'
export type AgentTimelineRoleKind = 'coordinator' | 'teammate' | 'subagent' | 'worker'
export type AgentTimelineEdgeKind = 'dispatch' | 'completion' | 'handoff'
export type AgentTimelineEdgeDirection = 'down' | 'up'

export const AGENT_TIMELINE_PALETTE = [
  'var(--accent-recovery, #9b7bf7)',
  'var(--accent-info, #4a9eff)',
  '#2bb6a3',
  '#e08b3e',
  '#5c7cfa',
  '#d96b98',
  '#3fb950',
  '#8c7ae6',
] as const

export interface AgentTimelineSpan {
  id: string
  rowId: string
  kind: AgentTimelineSpanKind
  label: string
  defaultLabelKey?:
    | 'planning'
    | 'synthesis'
    | 'executing'
    | 'waitingChildren'
    | 'waitingApproval'
    | 'retryAttempt'
    | 'fallbackAttempt'
    | null
  toolId?: string | null
  toolName?: string | null
  model?: string | null
  startMs: number
  endMs: number
  durationMs: number
  startPct: number
  endPct: number
  widthPct: number
  isLiveActive: boolean
  tokens: number | null
  inputTokens?: number | null
  outputTokens?: number | null
  attempt?: number | null
  fallbackPosition?: number | null
  handoffNote?: string | null
  errorMessage?: string | null
  rawToolExecutionId?: string | null
  rawInvocationId?: string | null
}

export interface AgentTimelineRow {
  id: string
  topologyNodeId: string | null
  roleKind: AgentTimelineRoleKind
  tier: 'meeting' | 'operation' | 'execution'
  label: string
  shortLabel: string
  handle: string | null
  agentSlug: string | null
  taskId: string | null
  taskKey: string | null
  laneKey: string | null
  instanceIds: string[]
  dependencies: string[]
  parentRowId: string | null
  color: string
  paletteIndex: number
  modelLabel: string | null
  runtimeBadge: string | null
  runtimeKind: RuntimeEngineKind | null
  status: RuntimeTopologyStatus
  isPersistentTeammate: boolean
  firstStartMs: number
  lastEndMs: number
  activeDurationMs: number
  totalTokens: number | null
  spans: AgentTimelineSpan[]
}

export interface AgentTimelineEdge {
  id: string
  kind: AgentTimelineEdgeKind
  direction: AgentTimelineEdgeDirection
  fromRowId: string
  toRowId: string
  fromSpanId: string
  toSpanId: string
  atMs: number
  fromPct: number
  toPct: number
  via?: string | null
  note?: string | null
  targetLabel?: string | null
}

export interface AgentTimelineModel {
  runId: string | null
  live: boolean
  startMs: number
  endMs: number
  wallDurationMs: number
  totalAgentActiveMs: number
  speedupFactor: number
  agentCount: number
  totalTokens: number | null
  estimatedCostUsd: number | null
  rows: AgentTimelineRow[]
  spans: AgentTimelineSpan[]
  edges: AgentTimelineEdge[]
}

export interface CausalChainResult {
  rootSpanId: string
  spanIds: Set<string>
  upstreamSpanIds: Set<string>
  downstreamSpanIds: Set<string>
  rowIds: Set<string>
  edgeIds: Set<string>
}

export interface BuildAgentTimelineInput {
  snapshot: OrchestrationSnapshotDto | null
  topologyRun?: TopologyRunDto | null
  toolExecutions?: ToolExecutionTimelineItemDto[]
  modelInvocations?: ModelInvocationDto[]
  nowMs?: number
  coordinatorFallbackLabel?: string
  locale?: string
  resolveAgentName?: (slugOrHandle: string | null | undefined, fallback?: string | null) => string
}

interface RawTeammateCandidate extends RuntimeEngineMetaInput {
  participant_id?: string | null
  id?: string | null
  handle?: string | null
  display_name?: string | null
  displayName?: string | null
  agent_slug?: string | null
  agentSlug?: string | null
  role?: string | null
  presence?: string | null
  status?: string | null
  mode?: string | null
  task_id?: string | null
  taskId?: string | null
  agent_instance_id?: string | null
  instance_id?: string | null
  created_at?: string | null
  spawned_at?: string | null
  updated_at?: string | null
}

interface RawTeamMessageCandidate {
  id?: string | null
  from?: string | null
  sender?: string | null
  to?: string | null
  target?: string | null
  recipient?: string | null
  text?: string | null
  summary?: string | null
  created_at?: string | null
  timestamp?: string | null
}

function parseIsoMs(value?: string | null): number | null {
  if (!value) return null
  const ms = Date.parse(value)
  return Number.isFinite(ms) ? ms : null
}

function hashPaletteSlot(key: string, usedNonZero: Set<number>): number {
  let hash = 2166136261
  for (let i = 0; i < key.length; i++) {
    hash ^= key.charCodeAt(i)
    hash = Math.imul(hash, 16777619)
  }
  const slotCount = AGENT_TIMELINE_PALETTE.length - 1
  const preferred = 1 + ((hash >>> 0) % slotCount)
  if (usedNonZero.size >= slotCount || !usedNonZero.has(preferred)) {
    usedNonZero.add(preferred)
    return preferred
  }
  for (let offset = 1; offset < slotCount; offset++) {
    const candidate = 1 + (((preferred - 1 + offset) >>> 0) % slotCount)
    if (!usedNonZero.has(candidate)) {
      usedNonZero.add(candidate)
      return candidate
    }
  }
  return preferred
}

function shortenAgentLabel(label: string, handle?: string | null): string {
  const source = (handle || label || '').trim()
  if (!source) return ''
  if (source.length <= 12) return source
  const hashIdx = source.indexOf('#')
  if (hashIdx > 0) {
    const prefix = source.slice(0, Math.min(8, hashIdx))
    const suffix = source.slice(hashIdx)
    return `${prefix}${suffix}`
  }
  return `${source.slice(0, 11)}…`
}

function isFailedStatus(raw?: string | null): boolean {
  const s = String(raw ?? '').trim().toLowerCase()
  return (
    s === 'failed'
    || s === 'error'
    || s === 'rejected'
    || s === 'timed_out'
    || s === 'timeout'
    || s === 'aborted'
  )
}

function isLiveRunningStatus(raw?: string | null): boolean {
  const s = String(raw ?? '').trim().toLowerCase()
  return (
    s === 'running'
    || s === 'executing'
    || s === 'in_progress'
    || s === 'in-progress'
    || s === 'started'
    || s === 'pending'
    || s === 'planning'
    || s === 'understanding'
    || s === 'replanning'
    || s === 'reviewing'
    || s === 'finalizing'
    || s === 'assigned'
    || s === 'waiting_approval'
    || s === 'awaiting_approval'
    || s === 'awaiting_user'
    || s === 'awaiting_delegate'
    || s === 'gate_review'
  )
}

export function estimateInvocationCostUsd(inv: ModelInvocationDto): number | null {
  const rawAny = inv as ModelInvocationDto & {
    cost_usd?: number | null
    estimated_cost_usd?: number | null
    costUsd?: number | null
  }
  const explicit = rawAny.cost_usd ?? rawAny.estimated_cost_usd ?? rawAny.costUsd
  if (typeof explicit === 'number' && Number.isFinite(explicit) && explicit >= 0) {
    return explicit
  }

  const hasIn = typeof inv.input_tokens === 'number' && Number.isFinite(inv.input_tokens)
  const hasOut = typeof inv.output_tokens === 'number' && Number.isFinite(inv.output_tokens)
  const hasTotal = typeof inv.total_tokens === 'number' && Number.isFinite(inv.total_tokens)
  if (!hasIn && !hasOut && !hasTotal) return null

  const model = String(inv.model ?? '').toLowerCase()
  let inPerMillion = 1.5
  let outPerMillion = 6.0

  if (model.includes('opus')) {
    inPerMillion = 15.0
    outPerMillion = 75.0
  } else if (model.includes('sonnet')) {
    inPerMillion = 3.0
    outPerMillion = 15.0
  } else if (model.includes('haiku')) {
    inPerMillion = 0.8
    outPerMillion = 4.0
  } else if (model.includes('mini') || model.includes('flash') || model.includes('nano')) {
    inPerMillion = 0.15
    outPerMillion = 0.6
  } else if (model.includes('deepseek')) {
    inPerMillion = 0.27
    outPerMillion = 1.1
  } else if (model.includes('gpt-5') || model.includes('gpt-4') || model.includes('o3') || model.includes('pro')) {
    inPerMillion = 2.5
    outPerMillion = 10.0
  }

  if (hasIn || hasOut) {
    const input = hasIn ? Math.max(0, inv.input_tokens!) : 0
    const output = hasOut ? Math.max(0, inv.output_tokens!) : Math.max(0, (inv.total_tokens ?? input) - input)
    return (input * inPerMillion + output * outPerMillion) / 1_000_000
  }

  const total = Math.max(0, inv.total_tokens!)
  const blendedPerMillion = inPerMillion * 0.75 + outPerMillion * 0.25
  return (total * blendedPerMillion) / 1_000_000
}

function invocationTokens(inv: ModelInvocationDto): number | null {
  if (typeof inv.total_tokens === 'number' && Number.isFinite(inv.total_tokens)) {
    return inv.total_tokens
  }
  const hasIn = typeof inv.input_tokens === 'number' && Number.isFinite(inv.input_tokens)
  const hasOut = typeof inv.output_tokens === 'number' && Number.isFinite(inv.output_tokens)
  if (hasIn || hasOut) {
    return (hasIn ? inv.input_tokens! : 0) + (hasOut ? inv.output_tokens! : 0)
  }
  return null
}

function sumMergedIntervals(intervals: Array<[number, number]>): number {
  if (intervals.length === 0) return 0
  const sorted = [...intervals]
    .filter(([s, e]) => Number.isFinite(s) && Number.isFinite(e) && e > s)
    .sort((a, b) => a[0] - b[0] || a[1] - b[1])
  if (sorted.length === 0) return 0
  let total = 0
  let [curStart, curEnd] = sorted[0]!
  for (let i = 1; i < sorted.length; i++) {
    const [nextStart, nextEnd] = sorted[i]!
    if (nextStart <= curEnd) {
      curEnd = Math.max(curEnd, nextEnd)
    } else {
      total += curEnd - curStart
      curStart = nextStart
      curEnd = nextEnd
    }
  }
  total += curEnd - curStart
  return total
}

function extractMentionHandles(text?: string | null): string[] {
  if (!text) return []
  const found: string[] = []
  for (const match of text.matchAll(/@([a-zA-Z0-9_#-]+)/g)) {
    const handle = match[1]?.trim()
    if (handle && handle !== 'all') found.push(handle)
  }
  return [...new Set(found)]
}

function readTeammateCandidates(
  snapshot: OrchestrationSnapshotDto | null,
  topologyRun: TopologyRunDto | null,
): RawTeammateCandidate[] {
  const out: RawTeammateCandidate[] = []
  const snapAny = snapshot as unknown as {
    teammates?: RawTeammateCandidate[]
    members?: RawTeammateCandidate[]
  } | null
  const topoAny = topologyRun as unknown as {
    teammates?: RawTeammateCandidate[]
    members?: RawTeammateCandidate[]
  } | null

  for (const list of [snapAny?.teammates, snapAny?.members, topoAny?.teammates, topoAny?.members]) {
    if (!Array.isArray(list)) continue
    for (const item of list) {
      if (item && typeof item === 'object') out.push(item)
    }
  }
  return out
}

function readTeamMessageCandidates(
  snapshot: OrchestrationSnapshotDto | null,
  topologyRun: TopologyRunDto | null,
): RawTeamMessageCandidate[] {
  const out: RawTeamMessageCandidate[] = []
  const snapAny = snapshot as unknown as { team_messages?: RawTeamMessageCandidate[] } | null
  const topoAny = topologyRun as unknown as { team_messages?: RawTeamMessageCandidate[] } | null
  for (const list of [snapAny?.team_messages, topoAny?.team_messages]) {
    if (!Array.isArray(list)) continue
    for (const item of list) {
      if (item && typeof item === 'object') out.push(item)
    }
  }
  return out
}

function clampPct(pct: number): number {
  if (!Number.isFinite(pct)) return 0
  return Math.max(0, Math.min(100, pct))
}

/**
 * Computes the directed causal closure (upstream ancestors + pinned span + downstream descendants)
 * across inter-row dispatch/completion/handoff edges and intra-row chronological span flow.
 *
 * Walking strictly backwards for ancestors and strictly forwards for descendants avoids lighting up
 * unrelated sibling branches that share the same coordinator dispatch or synthesis span.
 */
export function computeCausalChain(
  spanId: string,
  spans: readonly AgentTimelineSpan[],
  edges: readonly AgentTimelineEdge[],
): CausalChainResult {
  const spanById = new Map<string, AgentTimelineSpan>()
  const spansByRow = new Map<string, AgentTimelineSpan[]>()

  for (const span of spans) {
    spanById.set(span.id, span)
    const rowList = spansByRow.get(span.rowId)
    if (rowList) rowList.push(span)
    else spansByRow.set(span.rowId, [span])
  }

  for (const rowList of spansByRow.values()) {
    rowList.sort((a, b) => a.startMs - b.startMs || a.endMs - b.endMs)
  }

  if (!spanById.has(spanId)) {
    return {
      rootSpanId: spanId,
      spanIds: new Set(),
      upstreamSpanIds: new Set(),
      downstreamSpanIds: new Set(),
      rowIds: new Set(),
      edgeIds: new Set(),
    }
  }

  interface DirectedCausalLink {
    fromSpanId: string
    toSpanId: string
    atMs: number
    edgeId: string | null
  }

  const forwardLinks = new Map<string, DirectedCausalLink[]>()
  const backwardLinks = new Map<string, DirectedCausalLink[]>()

  const addLink = (linkItem: DirectedCausalLink) => {
    if (!linkItem.fromSpanId || !linkItem.toSpanId || linkItem.fromSpanId === linkItem.toSpanId) return
    if (!spanById.has(linkItem.fromSpanId) || !spanById.has(linkItem.toSpanId)) return
    const fwd = forwardLinks.get(linkItem.fromSpanId)
    if (fwd) fwd.push(linkItem)
    else forwardLinks.set(linkItem.fromSpanId, [linkItem])

    const bwd = backwardLinks.get(linkItem.toSpanId)
    if (bwd) bwd.push(linkItem)
    else backwardLinks.set(linkItem.toSpanId, [linkItem])
  }

  // Intra-row sequential causal progression
  for (const rowList of spansByRow.values()) {
    for (let i = 0; i < rowList.length - 1; i++) {
      const cur = rowList[i]!
      const next = rowList[i + 1]!
      addLink({
        fromSpanId: cur.id,
        toSpanId: next.id,
        atMs: next.startMs,
        edgeId: null,
      })
    }
  }

  for (const edge of edges) {
    addLink({
      fromSpanId: edge.fromSpanId,
      toSpanId: edge.toSpanId,
      atMs: edge.atMs,
      edgeId: edge.id,
    })
  }

  const rootSpan = spanById.get(spanId)!
  const TIME_TOLERANCE_MS = 25

  // Time-monotone backward reachability: track latest departure timestamp per span
  const latestDeparture = new Map<string, number>([[spanId, rootSpan.endMs]])
  const upstreamSpanIds = new Set<string>()
  const upstreamEdgeIds = new Set<string>()
  const backQueue: string[] = [spanId]

  while (backQueue.length > 0) {
    const current = backQueue.shift()!
    const departLimit = latestDeparture.get(current)!
    for (const linkItem of backwardLinks.get(current) ?? []) {
      if (linkItem.atMs > departLimit + TIME_TOLERANCE_MS) continue
      if (linkItem.edgeId) upstreamEdgeIds.add(linkItem.edgeId)
      const prevSpan = spanById.get(linkItem.fromSpanId)!
      const nextLimit = Math.min(prevSpan.endMs, linkItem.atMs)
      if (linkItem.fromSpanId !== spanId) {
        upstreamSpanIds.add(linkItem.fromSpanId)
      }
      const prevRecorded = latestDeparture.get(linkItem.fromSpanId)
      if (prevRecorded === undefined || nextLimit > prevRecorded) {
        latestDeparture.set(linkItem.fromSpanId, nextLimit)
        backQueue.push(linkItem.fromSpanId)
      }
    }
  }

  // Time-monotone forward reachability: track earliest arrival timestamp per span
  const earliestArrival = new Map<string, number>([[spanId, rootSpan.startMs]])
  const downstreamSpanIds = new Set<string>()
  const downstreamEdgeIds = new Set<string>()
  const fwdQueue: string[] = [spanId]

  while (fwdQueue.length > 0) {
    const current = fwdQueue.shift()!
    const arriveTime = earliestArrival.get(current)!
    for (const linkItem of forwardLinks.get(current) ?? []) {
      if (linkItem.atMs < arriveTime - TIME_TOLERANCE_MS) continue
      if (linkItem.edgeId) downstreamEdgeIds.add(linkItem.edgeId)
      const nextSpan = spanById.get(linkItem.toSpanId)!
      const nextArrive = Math.max(nextSpan.startMs, linkItem.atMs)
      if (linkItem.toSpanId !== spanId) {
        downstreamSpanIds.add(linkItem.toSpanId)
      }
      const prevRecorded = earliestArrival.get(linkItem.toSpanId)
      if (prevRecorded === undefined || nextArrive < prevRecorded) {
        earliestArrival.set(linkItem.toSpanId, nextArrive)
        fwdQueue.push(linkItem.toSpanId)
      }
    }
  }

  const spanIds = new Set<string>([spanId, ...upstreamSpanIds, ...downstreamSpanIds])
  const rowIds = new Set<string>()
  for (const id of spanIds) {
    const s = spanById.get(id)
    if (s) rowIds.add(s.rowId)
  }

  const edgeIds = new Set<string>([...upstreamEdgeIds, ...downstreamEdgeIds])

  return {
    rootSpanId: spanId,
    spanIds,
    upstreamSpanIds,
    downstreamSpanIds,
    rowIds,
    edgeIds,
  }
}

export function buildAgentTimeline(input: BuildAgentTimelineInput): AgentTimelineModel {
  const {
    snapshot,
    topologyRun = null,
    toolExecutions = [],
    modelInvocations = [],
    nowMs,
    coordinatorFallbackLabel = 'Coordinator',
    locale = 'en',
    resolveAgentName,
  } = input

  const run = snapshot?.run ?? null
  if (!run) {
    return {
      runId: null,
      live: false,
      startMs: 0,
      endMs: 1000,
      wallDurationMs: 0,
      totalAgentActiveMs: 0,
      speedupFactor: 1,
      agentCount: 0,
      totalTokens: null,
      estimatedCostUsd: null,
      rows: [],
      spans: [],
      edges: [],
    }
  }

  const validTopoRun = topologyRun && (!topologyRun.run_id || topologyRun.run_id === run.id) ? topologyRun : null
  const runTools = toolExecutions.filter((exec) => !exec.run_id || exec.run_id === run.id)
  const runInvocations = modelInvocations.filter((inv) => !inv.run_id || inv.run_id === run.id)
  const snapNodes = (snapshot?.nodes ?? []).filter((n) => !n.run_id || n.run_id === run.id)
  const snapAssignments = (snapshot?.assignments ?? []).filter((a) => !a.run_id || a.run_id === run.id)
  const snapStepResults = (snapshot?.step_results ?? []).filter((s) => !s.run_id || s.run_id === run.id)

  const topo = buildRuntimeTopology({
    snapshot,
    topologyRun: validTopoRun,
    toolExecutions: runTools,
    coordinatorFallbackLabel,
    locale,
    resolveAgentName,
  })

  // Collect observed timestamps across all projection sources
  const observedTimestamps: number[] = []
  const pushTs = (iso?: string | null) => {
    const ms = parseIsoMs(iso)
    if (ms !== null) observedTimestamps.push(ms)
  }

  pushTs(run.created_at)
  pushTs(run.updated_at)
  pushTs(validTopoRun?.started_at)
  pushTs(validTopoRun?.completed_at)
  for (const n of snapNodes) {
    pushTs(n.created_at)
    pushTs(n.updated_at)
  }
  for (const a of snapAssignments) pushTs(a.created_at)
  for (const sr of snapStepResults) pushTs(sr.created_at)
  for (const t of runTools) {
    const req = parseIsoMs(t.requested_at)
    if (req !== null) {
      observedTimestamps.push(req)
      if (t.duration_ms > 0) observedTimestamps.push(req + t.duration_ms)
    }
    pushTs(t.updated_at)
  }
  for (const inv of runInvocations) {
    pushTs(inv.started_at)
    pushTs(inv.completed_at)
  }

  const teammates = readTeammateCandidates(snapshot, validTopoRun)
  for (const tm of teammates) {
    pushTs(tm.created_at ?? tm.spawned_at)
    pushTs(tm.updated_at)
  }

  const teamMessages = readTeamMessageCandidates(snapshot, validTopoRun)
  for (const msg of teamMessages) {
    pushTs(msg.created_at ?? msg.timestamp)
  }

  const rawRunStatus = validTopoRun?.status ?? run.status
  const hasActiveNode = topo.nodes.some((n) => n.status === 'running' || n.status === 'awaiting_approval')
  const hasActiveTool = runTools.some((t) => isLiveRunningStatus(t.status))
  const hasActiveInv = runInvocations.some((inv) => !inv.completed_at && isLiveRunningStatus(inv.status))
  const live = Boolean(
    !validTopoRun?.completed_at
      && (isLiveRunningStatus(rawRunStatus) || hasActiveNode || hasActiveTool || hasActiveInv),
  )

  const minObserved = observedTimestamps.length > 0 ? Math.min(...observedTimestamps) : 0
  const maxObserved = observedTimestamps.length > 0 ? Math.max(...observedTimestamps) : minObserved + 1000
  const startMs = parseIsoMs(validTopoRun?.started_at) ?? parseIsoMs(run.created_at) ?? minObserved
  const observedSpanMs = Math.max(1000, maxObserved - startMs)

  let endMs = Math.max(startMs + 500, maxObserved)
  if (live) {
    if (typeof nowMs === 'number' && Number.isFinite(nowMs) && nowMs > startMs) {
      endMs = Math.max(endMs, nowMs)
    } else {
      const wallNow = Date.now()
      // Only bind to live wall clock if timestamps are within 10 minutes of wallNow (avoids squashing historical test fixtures)
      if (wallNow >= maxObserved && wallNow - maxObserved <= 10 * 60 * 1000) {
        endMs = Math.max(endMs + 250, wallNow)
      } else {
        endMs = maxObserved + Math.max(400, Math.round(observedSpanMs * 0.12))
      }
    }
  }
  const wallDurationMs = Math.max(1, endMs - startMs)

  // Determine coordinator layer ('meeting' | 'operation')
  const declaredConv = snapshot?.graph?.nodes.find((n) => n.is_conversation)
  const coordinatorLayer: 'meeting' | 'operation' =
    declaredConv?.layer === 'meeting'
      || validTopoRun?.instances.some((i) => !i.task_id && i.layer === 'meeting')
      ? 'meeting'
      : 'operation'

  const teammateHandleSet = new Set<string>()
  const teammateSlugSet = new Set<string>()
  const teammateTaskSet = new Set<string>()
  const teammateInstSet = new Set<string>()
  const teammateMetaByKey = new Map<string, RawTeammateCandidate>()

  for (const tm of teammates) {
    const isTm = tm.mode !== 'ephemeral'
    if (tm.handle) {
      if (isTm) teammateHandleSet.add(tm.handle)
      teammateMetaByKey.set(`handle:${tm.handle}`, tm)
    }
    const slug = tm.agent_slug ?? tm.agentSlug ?? null
    if (slug) {
      if (isTm) teammateSlugSet.add(slug)
      if (!teammateMetaByKey.has(`slug:${slug}`)) teammateMetaByKey.set(`slug:${slug}`, tm)
    }
    const tId = tm.task_id ?? tm.taskId ?? null
    if (tId) {
      if (isTm) teammateTaskSet.add(tId)
      teammateMetaByKey.set(`task:${tId}`, tm)
    }
    const iId = tm.agent_instance_id ?? tm.instance_id ?? null
    if (iId) {
      if (isTm) teammateInstSet.add(iId)
      teammateMetaByKey.set(`inst:${iId}`, tm)
    }
  }

  // Also detect persistent teammates spawned via `team_spawn` or addressed by `team_message`
  for (const exec of runTools) {
    if (exec.tool_id === 'team_message' || exec.tool_id === 'team_spawn') {
      const text = `${exec.arguments_summary ?? ''} ${exec.summary ?? ''}`
      if (exec.tool_id === 'team_message' || text.includes('teammate')) {
        for (const h of extractMentionHandles(text)) teammateHandleSet.add(h)
      }
    }
  }

  interface MutableRowDraft {
    id: string
    topologyNodeId: string | null
    roleKind: AgentTimelineRoleKind
    tier: 'meeting' | 'operation' | 'execution'
    depth: number
    originalOrder: number
    label: string
    handle: string | null
    agentSlug: string | null
    taskId: string | null
    taskKey: string | null
    laneKey: string | null
    instanceIds: string[]
    dependencies: string[]
    parentRowId: string | null
    modelLabel: string | null
    runtimeBadge: string | null
    runtimeKind: RuntimeEngineKind | null
    status: RuntimeTopologyStatus
    isPersistentTeammate: boolean
    explicitCreatedMs: number | null
    explicitUpdatedMs: number | null
    resultSummary: string | null
    title: string | null
  }

  const snapNodeById = new Map<string, TaskNodeDto>()
  const snapNodeByKey = new Map<string, TaskNodeDto>()
  for (const n of snapNodes) {
    snapNodeById.set(n.id, n)
    const tk = (n as unknown as { task_key?: string | null }).task_key
    if (tk) snapNodeByKey.set(tk, n)
  }

  const drafts: MutableRowDraft[] = []
  const representedHandles = new Set<string>()
  const representedSlugs = new Set<string>()
  const representedTaskIds = new Set<string>()
  const representedInstanceIds = new Set<string>()

  topo.nodes.forEach((node: RuntimeTopologyNode, idx: number) => {
    const snapNode =
      (node.taskId ? snapNodeById.get(node.taskId) : undefined)
      ?? (node.taskKey ? snapNodeByKey.get(node.taskKey) : undefined)
    const assign = snapAssignments.find(
      (a) =>
        (node.taskId && String(a.task_node_id) === node.taskId)
        || (a.agent_id && node.instanceIds.includes(a.agent_id)),
    )
    const tmMeta =
      (node.taskId ? teammateMetaByKey.get(`task:${node.taskId}`) : undefined)
      ?? node.instanceIds.map((id) => teammateMetaByKey.get(`inst:${id}`)).find(Boolean)
      ?? (node.handle ? teammateMetaByKey.get(`handle:${node.handle}`) : undefined)
      ?? (node.agentSlug ? teammateMetaByKey.get(`slug:${node.agentSlug}`) : undefined)

    const isCoordinator = node.roleTier === 'coordinator'
    const isTeammate =
      !isCoordinator
      && Boolean(
        (node.handle && teammateHandleSet.has(node.handle))
          || (node.taskId && teammateTaskSet.has(node.taskId))
          || node.instanceIds.some((id) => teammateInstSet.has(id))
          || (node.agentSlug && teammateSlugSet.has(node.agentSlug) && teammates.length > 0),
      )

    const roleKind: AgentTimelineRoleKind = isCoordinator
      ? 'coordinator'
      : isTeammate
        ? 'teammate'
        : node.roleTier === 'subagent'
          ? 'subagent'
          : 'worker'

    const metaModel =
      tmMeta?.model
      ?? tmMeta?.model_id
      ?? tmMeta?.modelId
      ?? tmMeta?.recent_actual_model?.model
      ?? (snapNode as unknown as RuntimeEngineMetaInput | undefined)?.model
      ?? (assign?.model_route_purpose || null)

    let badge: RuntimeEngineBadge | null = node.runtimeBadge
      ? { label: node.runtimeBadge, kind: node.runtimeKind ?? 'api' }
      : null
    if (!badge && tmMeta) {
      badge = formatRuntimeEngineBadge(tmMeta)
    }

    if (node.handle) representedHandles.add(node.handle)
    if (node.agentSlug) representedSlugs.add(node.agentSlug)
    if (node.taskId) representedTaskIds.add(node.taskId)
    for (const id of node.instanceIds) representedInstanceIds.add(id)

    drafts.push({
      id: node.id,
      topologyNodeId: node.id,
      roleKind,
      tier: isCoordinator ? coordinatorLayer : 'execution',
      depth: node.depth,
      originalOrder: idx,
      label: node.label,
      handle: node.handle ?? null,
      agentSlug: node.agentSlug ?? null,
      taskId: node.taskId ?? null,
      taskKey: node.taskKey ?? null,
      laneKey: node.laneKey ?? null,
      instanceIds: [...node.instanceIds],
      dependencies: [...node.dependencies],
      parentRowId: isCoordinator ? null : (node.parentId ?? 'coordinator'),
      modelLabel: metaModel ?? null,
      runtimeBadge: badge?.label ?? null,
      runtimeKind: badge?.kind ?? null,
      status: node.status,
      isPersistentTeammate: isTeammate,
      explicitCreatedMs:
        parseIsoMs(assign?.created_at)
        ?? parseIsoMs(snapNode?.created_at)
        ?? parseIsoMs(tmMeta?.created_at ?? tmMeta?.spawned_at)
        ?? (isCoordinator ? startMs : null),
      explicitUpdatedMs:
        parseIsoMs(snapNode?.updated_at)
        ?? parseIsoMs(tmMeta?.updated_at)
        ?? null,
      resultSummary: node.resultSummary ?? null,
      title: node.title ?? null,
    })
  })

  // Append any #33 persistent teammates that have not yet materialized as a task node
  teammates.forEach((tm, idx) => {
    const handle = tm.handle?.trim() || null
    const slug = (tm.agent_slug ?? tm.agentSlug)?.trim() || null
    const tId = (tm.task_id ?? tm.taskId)?.trim() || null
    const iId = (tm.agent_instance_id ?? tm.instance_id)?.trim() || null

    if (tId && representedTaskIds.has(tId)) return
    if (iId && representedInstanceIds.has(iId)) return
    if (handle && representedHandles.has(handle)) return
    if (!handle && slug && representedSlugs.has(slug)) return
    // Skip coordinator self-member if present in session members
    if (tm.role === 'conversation' || tm.role === 'coordinator' || handle === drafts[0]?.handle) return

    if (handle) representedHandles.add(handle)
    if (slug) representedSlugs.add(slug)
    if (tId) representedTaskIds.add(tId)
    if (iId) representedInstanceIds.add(iId)

    const rawName =
      tm.display_name
      ?? tm.displayName
      ?? (handle ? (resolveAgentName ? resolveAgentName(handle, handle) : handle) : null)
      ?? (slug ? (resolveAgentName ? resolveAgentName(slug, slug) : slug) : null)
      ?? `teammate-${idx + 1}`
    const badge = formatRuntimeEngineBadge(tm)
    const rawPresence = tm.status ?? tm.presence ?? 'queued'

    drafts.push({
      id: `teammate:${handle ?? iId ?? slug ?? idx}`,
      topologyNodeId: null,
      roleKind: 'teammate',
      tier: 'execution',
      depth: 1,
      originalOrder: 1000 + idx,
      label: rawName,
      handle,
      agentSlug: slug,
      taskId: tId,
      taskKey: null,
      laneKey: slug,
      instanceIds: iId ? [iId] : [],
      dependencies: [],
      parentRowId: 'coordinator',
      modelLabel: tm.model ?? tm.model_id ?? tm.modelId ?? tm.recent_actual_model?.model ?? null,
      runtimeBadge: badge?.label ?? null,
      runtimeKind: badge?.kind ?? null,
      status: normalizeTopologyStatus(rawPresence === 'waiting' || rawPresence === 'idle' ? 'completed' : rawPresence),
      isPersistentTeammate: true,
      explicitCreatedMs: parseIsoMs(tm.created_at ?? tm.spawned_at),
      explicitUpdatedMs: parseIsoMs(tm.updated_at),
      resultSummary: null,
      title: rawName,
    })
  })

  const coordinatorDraft = drafts.find((d) => d.roleKind === 'coordinator') ?? drafts[0]!

  // Attribute toolExecutions, modelInvocations, and stepResults to row drafts
  const toolsByRow = new Map<string, ToolExecutionTimelineItemDto[]>()
  const invocationsByRow = new Map<string, ModelInvocationDto[]>()
  const stepsByRow = new Map<string, StepResultDto[]>()
  for (const d of drafts) {
    toolsByRow.set(d.id, [])
    invocationsByRow.set(d.id, [])
    stepsByRow.set(d.id, [])
  }

  for (const exec of runTools) {
    let target = drafts.find(
      (d) =>
        d.roleKind !== 'coordinator'
        && ((exec.task_id && (d.taskId === exec.task_id || d.taskKey === exec.task_id))
          || (exec.agent_instance_id && d.instanceIds.includes(exec.agent_instance_id))),
    )
    if (!target) {
      target =
        drafts.find((d) => exec.agent_instance_id && d.instanceIds.includes(exec.agent_instance_id))
        ?? coordinatorDraft
    }
    toolsByRow.get(target.id)!.push(exec)
  }

  for (const inv of runInvocations) {
    let target = drafts.find(
      (d) => inv.agent_instance_id && d.instanceIds.includes(inv.agent_instance_id),
    )
    if (!target && inv.agent_definition_id) {
      const slugMatches = drafts.filter(
        (d) => d.roleKind !== 'coordinator' && d.agentSlug === inv.agent_definition_id,
      )
      if (slugMatches.length === 1) target = slugMatches[0]
    }
    if (!target) target = coordinatorDraft
    invocationsByRow.get(target.id)!.push(inv)
  }

  for (const sr of snapStepResults) {
    let target = drafts.find(
      (d) =>
        d.roleKind !== 'coordinator'
        && ((sr.task_node_id && (d.taskId === sr.task_node_id || d.taskKey === sr.task_node_id))
          || (sr.agent_id && (d.instanceIds.includes(sr.agent_id) || d.agentSlug === sr.agent_id))),
    )
    if (!target) target = coordinatorDraft
    stepsByRow.get(target.id)!.push(sr)
  }

  // Enrich row modelLabel & #36 runtimeBadge from attributed ModelInvocationDto rows
  for (const d of drafts) {
    const rowInvs = invocationsByRow.get(d.id) ?? []
    if (rowInvs.length > 0) {
      const latestWithModel = [...rowInvs].reverse().find((inv) => inv.model)
      if (latestWithModel?.model) {
        d.modelLabel = latestWithModel.model
      }
      if (!d.runtimeBadge) {
        const latest = rowInvs[rowInvs.length - 1]!
        const invBadge = formatRuntimeEngineBadge({
          protocol: latest.protocol,
          provider_instance_id: latest.provider_instance_id,
          model: latest.model,
        })
        if (invBadge) {
          d.runtimeBadge = invBadge.label
          d.runtimeKind = invBadge.kind
        }
      }
    }
  }

  // Estimate initial start times per non-coordinator row for chronological sorting
  const preliminaryStart = new Map<string, number>()
  preliminaryStart.set(coordinatorDraft.id, startMs)

  const nonCoordCount = Math.max(1, drafts.length - 1)
  drafts.forEach((d, idx) => {
    if (d.roleKind === 'coordinator') return
    const candidates: number[] = []
    if (d.explicitCreatedMs !== null) candidates.push(d.explicitCreatedMs)
    for (const inv of invocationsByRow.get(d.id) ?? []) {
      const ms = parseIsoMs(inv.started_at)
      if (ms !== null) candidates.push(ms)
    }
    for (const exec of toolsByRow.get(d.id) ?? []) {
      const ms = parseIsoMs(exec.requested_at)
      if (ms !== null) candidates.push(ms)
    }
    if (candidates.length > 0) {
      preliminaryStart.set(d.id, Math.max(startMs, Math.min(...candidates)))
    } else {
      // Stagger synthetic fallback slightly after coordinator planning start
      const offsetRatio = Math.min(0.45, 0.08 + ((idx - 1) / nonCoordCount) * 0.18 + (d.depth > 1 ? 0.14 : 0))
      preliminaryStart.set(d.id, startMs + Math.round(wallDurationMs * offsetRatio))
    }
  })

  // Sort: Coordinator strictly on top; workers/subagents/teammates chronologically below
  const sortedDrafts = [
    coordinatorDraft,
    ...drafts
      .filter((d) => d.id !== coordinatorDraft.id)
      .sort(
        (a, b) =>
          (preliminaryStart.get(a.id) ?? startMs) - (preliminaryStart.get(b.id) ?? startMs)
          || a.depth - b.depth
          || a.originalOrder - b.originalOrder,
      ),
  ]

  // Assign deterministic palette colors
  const usedSlots = new Set<number>()
  const draftPalette = new Map<string, { index: number; color: string }>()
  for (const d of sortedDrafts) {
    if (d.roleKind === 'coordinator') {
      draftPalette.set(d.id, { index: 0, color: AGENT_TIMELINE_PALETTE[0] })
    } else {
      const key = d.handle || d.agentSlug || d.taskKey || d.id
      const slot = hashPaletteSlot(key, usedSlots)
      draftPalette.set(d.id, { index: slot, color: AGENT_TIMELINE_PALETTE[slot]! })
    }
  }

  // Wait-lane lookup from OrchestrationSnapshotDto.lanes
  const waitingTaskKeys = new Map<string, string>()
  for (const lane of snapshot?.lanes ?? []) {
    for (const w of lane.waits ?? []) {
      if (w.waiting_task) {
        waitingTaskKeys.set(w.waiting_task, w.predicate || w.required_criteria.join(', '))
      }
    }
  }

  // First pass: construct raw spans for non-coordinator rows so Coordinator can accurately reflect child wait windows
  const rawSpansByRow = new Map<string, AgentTimelineSpan[]>()

  function makeSpan(
    partial: Omit<AgentTimelineSpan, 'durationMs' | 'startPct' | 'endPct' | 'widthPct'> & {
      startMs: number
      endMs: number
    },
  ): AgentTimelineSpan {
    const clampedStart = Math.max(startMs, Math.min(endMs, partial.startMs))
    const minWidthMs = Math.max(1, Math.round(wallDurationMs * 0.015))
    const clampedEnd = Math.max(clampedStart + minWidthMs, Math.min(endMs, partial.endMs))
    const durationMs = Math.max(0, Math.round(partial.endMs - partial.startMs))
    const startPct = clampPct(((clampedStart - startMs) / wallDurationMs) * 100)
    const rawEndPct = clampPct(((clampedEnd - startMs) / wallDurationMs) * 100)
    const widthPct = Math.max(1.2, Math.min(100 - startPct, rawEndPct - startPct))
    const endPct = clampPct(startPct + widthPct)
    return {
      ...partial,
      startMs: clampedStart,
      endMs: clampedEnd,
      durationMs,
      startPct,
      endPct,
      widthPct,
    }
  }

  function buildGranularSpansForRow(d: MutableRowDraft): AgentTimelineSpan[] {
    const rowInvs = [...(invocationsByRow.get(d.id) ?? [])].sort(
      (a, b) => (parseIsoMs(a.started_at) ?? 0) - (parseIsoMs(b.started_at) ?? 0) || a.attempt - b.attempt,
    )
    const rowTools = [...(toolsByRow.get(d.id) ?? [])].sort(
      (a, b) =>
        (parseIsoMs(a.requested_at) ?? 0) - (parseIsoMs(b.requested_at) ?? 0)
        || a.requested_seq - b.requested_seq,
    )
    const spans: AgentTimelineSpan[] = []

    for (const inv of rowInvs) {
      const sMs = parseIsoMs(inv.started_at) ?? preliminaryStart.get(d.id) ?? startMs
      const invActive = live && !inv.completed_at && isLiveRunningStatus(inv.status)
      const defaultDur = Math.max(180, Math.round(wallDurationMs * 0.08))
      const eMs = invActive
        ? endMs
        : (parseIsoMs(inv.completed_at) ?? Math.min(endMs, sMs + defaultDur))

      let kind: AgentTimelineSpanKind = 'thinking'
      let defaultLabelKey: AgentTimelineSpan['defaultLabelKey'] = null
      let label = inv.model ? `${inv.model}` : d.title || d.label

      if (isFailedStatus(inv.status)) {
        kind = 'failed'
        label = inv.safe_error_message || inv.error_category || label
      } else if ((inv.fallback_position ?? 0) > 0 || (inv.attempt ?? 1) > 1 || inv.strategy_source === 'fallback') {
        kind = 'retry'
        if ((inv.fallback_position ?? 0) > 0) {
          defaultLabelKey = 'fallbackAttempt'
          label = inv.model ? `${inv.model} (#${inv.fallback_position + 1})` : `Fallback #${inv.fallback_position}`
        } else {
          defaultLabelKey = 'retryAttempt'
          label = `Retry #${inv.attempt}`
        }
      } else {
        defaultLabelKey = d.roleKind === 'coordinator' ? 'planning' : 'executing'
      }

      spans.push(
        makeSpan({
          id: `span:inv:${inv.id}`,
          rowId: d.id,
          kind,
          label,
          defaultLabelKey,
          model: inv.model ?? d.modelLabel,
          startMs: sMs,
          endMs: eMs,
          isLiveActive: invActive,
          tokens: invocationTokens(inv),
          inputTokens: inv.input_tokens ?? null,
          outputTokens: inv.output_tokens ?? null,
          attempt: inv.attempt,
          fallbackPosition: inv.fallback_position,
          errorMessage: inv.safe_error_message ?? null,
          rawInvocationId: inv.id,
        }),
      )
    }

    for (const exec of rowTools) {
      const sMs = parseIsoMs(exec.requested_at) ?? preliminaryStart.get(d.id) ?? startMs
      const statusLower = String(exec.status ?? '').toLowerCase()
      const isApprovalWait = statusLower === 'waiting_approval' || statusLower === 'awaiting_approval'
      const isTeamWait = exec.tool_id === 'team_wait' || statusLower === 'blocked'
      const execActive = live && isLiveRunningStatus(exec.status)
      const observedEnd =
        exec.duration_ms > 0
          ? sMs + exec.duration_ms
          : parseIsoMs(exec.updated_at)
      const fallbackDur = Math.max(120, Math.round(wallDurationMs * 0.06))
      const eMs = execActive
        ? endMs
        : Math.max(sMs + Math.max(exec.duration_ms, 60), observedEnd ?? Math.min(endMs, sMs + fallbackDur))

      let kind: AgentTimelineSpanKind = 'tool'
      let defaultLabelKey: AgentTimelineSpan['defaultLabelKey'] = null
      if (isApprovalWait || isTeamWait) {
        kind = 'waiting'
        defaultLabelKey = isApprovalWait ? 'waitingApproval' : 'waitingChildren'
      } else if (isFailedStatus(statusLower) || exec.tool_success === false) {
        kind = 'failed'
      } else if (statusLower === 'retry' || statusLower === 'retrying') {
        kind = 'retry'
        defaultLabelKey = 'retryAttempt'
      }

      const handoffNote =
        exec.tool_id === 'team_message' || exec.tool_id === 'team_spawn'
          ? (exec.summary || exec.arguments_summary || null)
          : null

      spans.push(
        makeSpan({
          id: `span:tool:${exec.id}`,
          rowId: d.id,
          kind,
          label: exec.summary || exec.tool_display_name || exec.tool_id,
          defaultLabelKey,
          toolId: exec.tool_id,
          toolName: exec.tool_display_name || exec.tool_id,
          model: d.modelLabel,
          startMs: sMs,
          endMs: eMs,
          isLiveActive: execActive,
          tokens: null,
          handoffNote,
          errorMessage: kind === 'failed' ? (exec.error_category || exec.summary || null) : null,
          rawToolExecutionId: exec.id,
        }),
      )
    }

    spans.sort((a, b) => a.startMs - b.startMs || a.endMs - b.endMs)
    return spans
  }

  // Build spans for non-coordinator rows first
  const draftByKeyOrId = new Map<string, MutableRowDraft>()
  for (const d of sortedDrafts) {
    draftByKeyOrId.set(d.id, d)
    if (d.taskId) draftByKeyOrId.set(d.taskId, d)
    if (d.taskKey) draftByKeyOrId.set(d.taskKey, d)
    if (d.handle) draftByKeyOrId.set(d.handle, d)
  }

  const rowSettledEnd = new Map<string, number>()

  for (const d of sortedDrafts) {
    if (d.roleKind === 'coordinator') continue
    const granular = buildGranularSpansForRow(d)
    const rowSteps = stepsByRow.get(d.id) ?? []
    const stepEndMs = rowSteps
      .map((s) => parseIsoMs(s.created_at))
      .filter((v): v is number => v !== null)

    if (granular.length > 0) {
      // Inject blocking/waiting intervals between turns when awaiting approval or lane wait
      const enriched: AgentTimelineSpan[] = []
      const waitReason =
        (d.taskKey ? waitingTaskKeys.get(d.taskKey) : undefined)
        ?? (d.taskId ? waitingTaskKeys.get(d.taskId) : undefined)

      for (let i = 0; i < granular.length; i++) {
        const cur = granular[i]!
        if (i > 0) {
          const prev = enriched[enriched.length - 1]!
          const gap = cur.startMs - prev.endMs
          if (gap >= Math.max(250, Math.round(wallDurationMs * 0.06)) && (d.isPersistentTeammate || waitReason)) {
            enriched.push(
              makeSpan({
                id: `span:wait:${d.id}:${i}`,
                rowId: d.id,
                kind: 'waiting',
                label: waitReason || d.label,
                defaultLabelKey: 'waitingChildren',
                startMs: prev.endMs,
                endMs: cur.startMs,
                isLiveActive: false,
                tokens: null,
                handoffNote: waitReason ?? null,
              }),
            )
          }
        }
        enriched.push(cur)
      }

      const last = enriched[enriched.length - 1]!
      if (d.status === 'awaiting_approval' && last.kind !== 'waiting') {
        enriched.push(
          makeSpan({
            id: `span:wait-approval:${d.id}`,
            rowId: d.id,
            kind: 'waiting',
            label: d.title || d.label,
            defaultLabelKey: 'waitingApproval',
            startMs: last.endMs,
            endMs: live ? endMs : Math.min(endMs, last.endMs + Math.max(200, Math.round(wallDurationMs * 0.1))),
            isLiveActive: live,
            tokens: null,
          }),
        )
      }

      rawSpansByRow.set(d.id, enriched)
      rowSettledEnd.set(d.id, enriched[enriched.length - 1]!.endMs)
      continue
    }

    // Lifecycle fallback when granular tool/invocation records are absent for this worker/teammate
    let rStart = preliminaryStart.get(d.id) ?? startMs
    const depEnds = d.dependencies
      .map((depKey) => draftByKeyOrId.get(depKey))
      .filter((dep): dep is MutableRowDraft => Boolean(dep))
      .map((dep) => rowSettledEnd.get(dep.id))
      .filter((v): v is number => typeof v === 'number')

    const maxDepEnd = depEnds.length > 0 ? Math.max(...depEnds) : null
    const synth: AgentTimelineSpan[] = []

    if (maxDepEnd !== null && maxDepEnd > rStart + 80) {
      synth.push(
        makeSpan({
          id: `span:dep-wait:${d.id}`,
          rowId: d.id,
          kind: 'waiting',
          label: d.title || d.label,
          defaultLabelKey: 'waitingChildren',
          startMs: rStart,
          endMs: maxDepEnd,
          isLiveActive: false,
          tokens: null,
        }),
      )
      rStart = maxDepEnd
    }

    const rowActive = live && (d.status === 'running' || d.status === 'awaiting_approval' || d.status === 'queued')
    const explicitEnd =
      stepEndMs.length > 0
        ? Math.max(...stepEndMs)
        : d.explicitUpdatedMs !== null && d.explicitUpdatedMs > rStart
          ? d.explicitUpdatedMs
          : null

    const rEnd = rowActive
      ? endMs
      : (explicitEnd ?? Math.min(endMs, rStart + Math.max(250, Math.round(wallDurationMs * 0.42))))

    const lastStep = rowSteps[rowSteps.length - 1]
    const note = d.resultSummary ?? lastStep?.summary ?? null

    if (d.status === 'awaiting_approval') {
      const splitMs = rStart + Math.round((rEnd - rStart) * 0.55)
      if (splitMs > rStart + 60) {
        synth.push(
          makeSpan({
            id: `span:exec:${d.id}`,
            rowId: d.id,
            kind: 'thinking',
            label: d.title || d.label,
            defaultLabelKey: 'executing',
            model: d.modelLabel,
            startMs: rStart,
            endMs: splitMs,
            isLiveActive: false,
            tokens: null,
          }),
        )
      }
      synth.push(
        makeSpan({
          id: `span:approval:${d.id}`,
          rowId: d.id,
          kind: 'waiting',
          label: d.title || d.label,
          defaultLabelKey: 'waitingApproval',
          model: d.modelLabel,
          startMs: splitMs > rStart + 60 ? splitMs : rStart,
          endMs: rEnd,
          isLiveActive: live,
          tokens: null,
          handoffNote: note,
        }),
      )
    } else if (d.status === 'queued') {
      synth.push(
        makeSpan({
          id: `span:queued:${d.id}`,
          rowId: d.id,
          kind: 'waiting',
          label: d.title || d.label,
          defaultLabelKey: 'waitingChildren',
          model: d.modelLabel,
          startMs: rStart,
          endMs: rEnd,
          isLiveActive: live,
          tokens: null,
        }),
      )
    } else if (d.status === 'failed') {
      synth.push(
        makeSpan({
          id: `span:failed:${d.id}`,
          rowId: d.id,
          kind: 'failed',
          label: note || d.title || d.label,
          model: d.modelLabel,
          startMs: rStart,
          endMs: rEnd,
          isLiveActive: false,
          tokens: null,
          errorMessage: note,
        }),
      )
    } else {
      synth.push(
        makeSpan({
          id: `span:exec:${d.id}`,
          rowId: d.id,
          kind: 'thinking',
          label: d.title || d.label,
          defaultLabelKey: 'executing',
          model: d.modelLabel,
          startMs: rStart,
          endMs: rEnd,
          isLiveActive: rowActive,
          tokens: null,
          handoffNote: note,
        }),
      )
    }

    rawSpansByRow.set(d.id, synth)
    rowSettledEnd.set(d.id, synth[synth.length - 1]!.endMs)
  }

  // Now build Coordinator spans (using granular records when present + waiting bridges over delegated children)
  const workerSpansFlat = sortedDrafts
    .filter((d) => d.id !== coordinatorDraft.id)
    .flatMap((d) => rawSpansByRow.get(d.id) ?? [])
  const minWorkerMs = workerSpansFlat.length > 0 ? Math.min(...workerSpansFlat.map((s) => s.startMs)) : null
  const maxWorkerMs = workerSpansFlat.length > 0 ? Math.max(...workerSpansFlat.map((s) => s.endMs)) : null
  const anyWorkerLive = workerSpansFlat.some((s) => s.isLiveActive)

  const coordGranular = buildGranularSpansForRow(coordinatorDraft)
  if (coordGranular.length > 0) {
    const enrichedCoord: AgentTimelineSpan[] = []
    for (let i = 0; i < coordGranular.length; i++) {
      const cur = coordGranular[i]!
      if (i > 0) {
        const prev = enrichedCoord[enrichedCoord.length - 1]!
        const gap = cur.startMs - prev.endMs
        const overlapsChild = workerSpansFlat.some(
          (ws) => ws.startMs < cur.startMs && ws.endMs > prev.endMs,
        )
        if (gap >= Math.max(200, Math.round(wallDurationMs * 0.05)) && overlapsChild) {
          enrichedCoord.push(
            makeSpan({
              id: `span:coord-wait:${i}`,
              rowId: coordinatorDraft.id,
              kind: 'waiting',
              label: coordinatorDraft.label,
              defaultLabelKey: 'waitingChildren',
              startMs: prev.endMs,
              endMs: cur.startMs,
              isLiveActive: false,
              tokens: null,
            }),
          )
        }
      }
      enrichedCoord.push(cur)
    }

    const lastCoord = enrichedCoord[enrichedCoord.length - 1]!
    if (maxWorkerMs !== null && maxWorkerMs > lastCoord.endMs + 60) {
      const synthDur = Math.max(60, Math.round(wallDurationMs * 0.06))
      const synthStart = !live ? Math.max(lastCoord.endMs + 20, Math.min(maxWorkerMs, endMs - synthDur)) : maxWorkerMs
      const waitEnd = anyWorkerLive ? endMs : synthStart
      if (waitEnd > lastCoord.endMs + 20) {
        enrichedCoord.push(
          makeSpan({
            id: 'span:coord-wait:tail',
            rowId: coordinatorDraft.id,
            kind: 'waiting',
            label: coordinatorDraft.label,
            defaultLabelKey: 'waitingChildren',
            startMs: lastCoord.endMs,
            endMs: waitEnd,
            isLiveActive: live && anyWorkerLive,
            tokens: null,
          }),
        )
      }
      if (!live) {
        enrichedCoord.push(
          makeSpan({
            id: 'span:coord-synthesis',
            rowId: coordinatorDraft.id,
            kind: isFailedStatus(rawRunStatus) ? 'failed' : 'thinking',
            label: run.summary || coordinatorDraft.label,
            defaultLabelKey: 'synthesis',
            model: coordinatorDraft.modelLabel,
            startMs: synthStart,
            endMs,
            isLiveActive: false,
            tokens: null,
          }),
        )
      }
    }
    rawSpansByRow.set(coordinatorDraft.id, enrichedCoord)
  } else if (minWorkerMs !== null && maxWorkerMs !== null) {
    const coordSynth: AgentTimelineSpan[] = []
    const planEnd = Math.max(startMs + Math.min(180, Math.round(wallDurationMs * 0.12)), minWorkerMs)
    coordSynth.push(
      makeSpan({
        id: 'span:coord-plan',
        rowId: coordinatorDraft.id,
        kind: 'thinking',
        label: coordinatorDraft.label,
        defaultLabelKey: 'planning',
        model: coordinatorDraft.modelLabel,
        startMs,
        endMs: planEnd,
        isLiveActive: false,
        tokens: null,
      }),
    )
    if (maxWorkerMs > planEnd + 60) {
      coordSynth.push(
        makeSpan({
          id: 'span:coord-wait',
          rowId: coordinatorDraft.id,
          kind: 'waiting',
          label: coordinatorDraft.label,
          defaultLabelKey: 'waitingChildren',
          startMs: planEnd,
          endMs: anyWorkerLive ? endMs : maxWorkerMs,
          isLiveActive: live && anyWorkerLive,
          tokens: null,
        }),
      )
    }
    if (!anyWorkerLive && endMs >= maxWorkerMs) {
      const synthStart = Math.min(maxWorkerMs, endMs - Math.max(80, Math.round(wallDurationMs * 0.08)))
      coordSynth.push(
        makeSpan({
          id: 'span:coord-synthesis',
          rowId: coordinatorDraft.id,
          kind: isFailedStatus(rawRunStatus) ? 'failed' : 'thinking',
          label: run.summary || coordinatorDraft.label,
          defaultLabelKey: 'synthesis',
          model: coordinatorDraft.modelLabel,
          startMs: Math.max(planEnd, synthStart),
          endMs,
          isLiveActive: live && coordinatorDraft.status === 'running',
          tokens: null,
        }),
      )
    }
    rawSpansByRow.set(coordinatorDraft.id, coordSynth)
  } else {
    rawSpansByRow.set(coordinatorDraft.id, [
      makeSpan({
        id: 'span:coord-solo',
        rowId: coordinatorDraft.id,
        kind: isFailedStatus(rawRunStatus) ? 'failed' : 'thinking',
        label: run.summary || coordinatorDraft.label,
        defaultLabelKey: 'executing',
        model: coordinatorDraft.modelLabel,
        startMs,
        endMs,
        isLiveActive: live,
        tokens: null,
      }),
    ])
  }

  // Finalize AgentTimelineRow objects
  const rows: AgentTimelineRow[] = []
  const allSpans: AgentTimelineSpan[] = []

  for (const d of sortedDrafts) {
    const rowSpans = rawSpansByRow.get(d.id) ?? []
    allSpans.push(...rowSpans)

    const activeIntervals: Array<[number, number]> = rowSpans
      .filter((s) => s.kind !== 'waiting')
      .map((s) => [s.startMs, s.endMs])
    const activeDurationMs = sumMergedIntervals(activeIntervals)

    const tokenValues = rowSpans
      .map((s) => s.tokens)
      .filter((v): v is number => typeof v === 'number' && Number.isFinite(v))
    const rowTokens = tokenValues.length > 0 ? tokenValues.reduce((a, b) => a + b, 0) : null

    const palette = draftPalette.get(d.id)!
    const firstStartMs = rowSpans.length > 0 ? rowSpans[0]!.startMs : startMs
    const lastEndMs = rowSpans.length > 0 ? rowSpans[rowSpans.length - 1]!.endMs : endMs

    rows.push({
      id: d.id,
      topologyNodeId: d.topologyNodeId,
      roleKind: d.roleKind,
      tier: d.tier,
      label: d.label,
      shortLabel: shortenAgentLabel(d.label, d.handle),
      handle: d.handle,
      agentSlug: d.agentSlug,
      taskId: d.taskId,
      taskKey: d.taskKey,
      laneKey: d.laneKey,
      instanceIds: d.instanceIds,
      dependencies: d.dependencies,
      parentRowId: d.parentRowId,
      color: palette.color,
      paletteIndex: palette.index,
      modelLabel: d.modelLabel,
      runtimeBadge: d.runtimeBadge,
      runtimeKind: d.runtimeKind,
      status: d.status,
      isPersistentTeammate: d.isPersistentTeammate,
      firstStartMs,
      lastEndMs,
      activeDurationMs,
      totalTokens: rowTokens,
      spans: rowSpans,
    })
  }

  const rowIndexById = new Map<string, number>()
  const rowById = new Map<string, AgentTimelineRow>()
  rows.forEach((r, idx) => {
    rowIndexById.set(r.id, idx)
    rowById.set(r.id, r)
  })

  function resolveRowByTarget(refText?: string | null): AgentTimelineRow | undefined {
    const key = refText?.trim()
    if (!key) return undefined
    const clean = key.startsWith('@') ? key.slice(1) : key
    return (
      rows.find((r) => r.id === clean)
      ?? rows.find((r) => r.handle === clean)
      ?? rows.find((r) => r.taskId === clean)
      ?? rows.find((r) => r.taskKey === clean)
      ?? rows.find((r) => r.agentSlug === clean)
      ?? rows.find((r) => r.label === clean)
    )
  }

  function findDispatchSpan(parentRow: AgentTimelineRow, targetStartMs: number): AgentTimelineSpan | undefined {
    if (parentRow.spans.length === 0) return undefined
    const spawnTool = parentRow.spans.find(
      (s) =>
        (s.toolId === 'team_spawn' || s.toolId === 'task_dispatch')
        && Math.abs(s.startMs - targetStartMs) <= Math.max(1500, wallDurationMs * 0.25),
    )
    if (spawnTool) return spawnTool

    const activeNonWait = parentRow.spans.filter(
      (s) => s.kind !== 'waiting' && s.startMs <= targetStartMs + 50,
    )
    if (activeNonWait.length > 0) return activeNonWait[activeNonWait.length - 1]
    return parentRow.spans[0]
  }

  function findReceivingSpan(targetRow: AgentTimelineRow, afterMs: number): AgentTimelineSpan | undefined {
    if (targetRow.spans.length === 0) return undefined
    const nonWaitingAfter = targetRow.spans.find(
      (s) => s.kind !== 'waiting' && s.endMs >= afterMs - 50 && s.startMs >= afterMs - Math.max(500, wallDurationMs * 0.15),
    )
    if (nonWaitingAfter) return nonWaitingAfter
    const anyAfter = targetRow.spans.find((s) => s.startMs >= afterMs - 50)
    if (anyAfter) return anyAfter
    return targetRow.spans[targetRow.spans.length - 1]
  }

  // Construct Handoff & Causal Edges
  const edges: AgentTimelineEdge[] = []
  const seenEdgeKeys = new Set<string>()

  const pushEdge = (edge: AgentTimelineEdge) => {
    const dedup = `${edge.kind}:${edge.fromSpanId}->${edge.toSpanId}`
    if (seenEdgeKeys.has(dedup)) return
    seenEdgeKeys.add(dedup)
    edges.push(edge)
  }

  // 1. Downward Dispatch Edges & Upward Settlement Completion Edges
  for (const row of rows) {
    if (row.roleKind === 'coordinator' || row.spans.length === 0) continue
    const parentRow = (row.parentRowId ? rowById.get(row.parentRowId) : undefined) ?? rows[0]
    if (!parentRow || parentRow.id === row.id || parentRow.spans.length === 0) continue

    const parentIdx = rowIndexById.get(parentRow.id) ?? 0
    const childIdx = rowIndexById.get(row.id) ?? 1

    const fromDispatchSpan = findDispatchSpan(parentRow, row.firstStartMs)
    const toChildFirstSpan = row.spans[0]!

    if (fromDispatchSpan) {
      const dispatchMs = toChildFirstSpan.startMs
      const fromPct = clampPct(
        Math.min(fromDispatchSpan.endPct, Math.max(fromDispatchSpan.startPct, toChildFirstSpan.startPct)),
      )
      pushEdge({
        id: `edge:dispatch:${parentRow.id}->${row.id}`,
        kind: 'dispatch',
        direction: childIdx >= parentIdx ? 'down' : 'up',
        fromRowId: parentRow.id,
        toRowId: row.id,
        fromSpanId: fromDispatchSpan.id,
        toSpanId: toChildFirstSpan.id,
        atMs: dispatchMs,
        fromPct,
        toPct: toChildFirstSpan.startPct,
        via: row.isPersistentTeammate ? 'team_spawn' : row.roleKind === 'subagent' ? 'task_dispatch' : 'plan',
        note: toChildFirstSpan.label,
        targetLabel: row.label,
      })
    }

    // Upward completion edge once child has settled (or recorded a step_result)
    const rowSteps = stepsByRow.get(row.id) ?? []
    const isSettled = row.status === 'completed' || row.status === 'failed' || rowSteps.length > 0
    if (isSettled) {
      const lastActiveSpan =
        [...row.spans].reverse().find((s) => s.kind !== 'waiting')
        ?? row.spans[row.spans.length - 1]!
      const targetReceiveSpan = findReceivingSpan(parentRow, lastActiveSpan.endMs)
      if (
        targetReceiveSpan
        && (targetReceiveSpan.id !== fromDispatchSpan?.id || lastActiveSpan.endPct > toChildFirstSpan.startPct + 0.5)
      ) {
        const settleNote =
          rowSteps[rowSteps.length - 1]?.summary
          ?? lastActiveSpan.handoffNote
          ?? lastActiveSpan.errorMessage
          ?? null
        const targetToPct =
          targetReceiveSpan.id === fromDispatchSpan?.id
            ? clampPct(Math.min(targetReceiveSpan.endPct, lastActiveSpan.endPct))
            : clampPct(Math.max(lastActiveSpan.endPct, targetReceiveSpan.startPct))
        pushEdge({
          id: `edge:complete:${row.id}->${parentRow.id}`,
          kind: 'completion',
          direction: parentIdx <= childIdx ? 'up' : 'down',
          fromRowId: row.id,
          toRowId: parentRow.id,
          fromSpanId: lastActiveSpan.id,
          toSpanId: targetReceiveSpan.id,
          atMs: lastActiveSpan.endMs,
          fromPct: lastActiveSpan.endPct,
          toPct: targetToPct,
          via: 'step_result',
          note: settleNote,
          targetLabel: parentRow.label,
        })
      }
    }
  }

  // 2. Teammate `@mention` & `team_message` Handoff Edges
  for (const row of rows) {
    for (const span of row.spans) {
      const isMsgTool = span.toolId === 'team_message'
      const textToScan = `${span.handoffNote ?? ''} ${isMsgTool ? span.label : ''}`
      const mentions = extractMentionHandles(textToScan)
      if (!isMsgTool && mentions.length === 0) continue

      for (const handle of mentions) {
        const targetRow = resolveRowByTarget(handle)
        if (!targetRow || targetRow.id === row.id || targetRow.spans.length === 0) continue
        const targetSpan = findReceivingSpan(targetRow, span.startMs)
        if (!targetSpan) continue
        const fromIdx = rowIndexById.get(row.id) ?? 0
        const toIdx = rowIndexById.get(targetRow.id) ?? 0

        pushEdge({
          id: `edge:handoff:${span.id}->${targetSpan.id}`,
          kind: 'handoff',
          direction: toIdx < fromIdx ? 'up' : 'down',
          fromRowId: row.id,
          toRowId: targetRow.id,
          fromSpanId: span.id,
          toSpanId: targetSpan.id,
          atMs: span.endMs,
          fromPct: span.endPct,
          toPct: targetSpan.startPct,
          via: isMsgTool ? 'team_message' : 'mention',
          note: span.handoffNote || span.label,
          targetLabel: targetRow.handle || targetRow.label,
        })
      }
    }
  }

  for (const msg of teamMessages) {
    const senderRow = resolveRowByTarget(msg.from ?? msg.sender) ?? rows[0]
    const explicitTarget = msg.to ?? msg.target ?? msg.recipient
    const targets = explicitTarget
      ? [explicitTarget]
      : extractMentionHandles(msg.text ?? msg.summary)
    const msgMs = parseIsoMs(msg.created_at ?? msg.timestamp) ?? startMs

    for (const tRef of targets) {
      const targetRow = resolveRowByTarget(tRef)
      if (!senderRow || !targetRow || senderRow.id === targetRow.id) continue
      const fromSpan = findDispatchSpan(senderRow, msgMs)
      const toSpan = findReceivingSpan(targetRow, msgMs)
      if (!fromSpan || !toSpan) continue
      const fromIdx = rowIndexById.get(senderRow.id) ?? 0
      const toIdx = rowIndexById.get(targetRow.id) ?? 0

      pushEdge({
        id: `edge:msg:${msg.id ?? `${senderRow.id}-${targetRow.id}-${msgMs}`}`,
        kind: 'handoff',
        direction: toIdx < fromIdx ? 'up' : 'down',
        fromRowId: senderRow.id,
        toRowId: targetRow.id,
        fromSpanId: fromSpan.id,
        toSpanId: toSpan.id,
        atMs: msgMs,
        fromPct: fromSpan.endPct,
        toPct: toSpan.startPct,
        via: 'team_message',
        note: msg.summary ?? msg.text ?? null,
        targetLabel: targetRow.handle || targetRow.label,
      })
    }
  }

  // Compute totals: tokens, cost, active duration, speedup
  const totalAgentActiveMs = rows.reduce((sum, r) => sum + r.activeDurationMs, 0)
  const rawSpeedup = totalAgentActiveMs > 0 ? totalAgentActiveMs / wallDurationMs : 1
  const speedupFactor = Math.max(0.1, Math.round(rawSpeedup * 10) / 10)

  const invTokenList = runInvocations
    .map((inv) => invocationTokens(inv))
    .filter((v): v is number => v !== null)

  let totalTokens: number | null = null
  if (invTokenList.length > 0) {
    totalTokens = invTokenList.reduce((a, b) => a + b, 0)
  } else {
    const packTokens = (snapshot?.context_packs ?? [])
      .filter((p) => !p.run_id || p.run_id === run.id)
      .map((p) => p.estimated_tokens)
      .filter((v): v is number => typeof v === 'number' && Number.isFinite(v) && v > 0)
    if (packTokens.length > 0) {
      totalTokens = packTokens.reduce((a, b) => a + b, 0)
    }
  }

  const invCosts = runInvocations
    .map((inv) => estimateInvocationCostUsd(inv))
    .filter((v): v is number => v !== null)
  const estimatedCostUsd = invCosts.length > 0 ? invCosts.reduce((a, b) => a + b, 0) : null

  return {
    runId: run.id,
    live,
    startMs,
    endMs,
    wallDurationMs,
    totalAgentActiveMs,
    speedupFactor,
    agentCount: rows.length,
    totalTokens,
    estimatedCostUsd,
    rows,
    spans: allSpans,
    edges,
  }
}
