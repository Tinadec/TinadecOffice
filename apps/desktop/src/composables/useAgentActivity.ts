import { computed, ref, watch, onScopeDispose, type Ref } from 'vue'
import {
  api,
  type EventEnvelope,
  type OrchestrationSnapshotDto,
  type ToolExecutionTimelineItemDto,
} from '@/api'
import { subscribeToSessionEvents } from '@/lib/sessionEventBus'
import i18n from '@/i18n'
import { errorCategoryMessage } from '@/lib/apiError'

export type AgentRunStatus =
  | 'idle'
  | 'thinking'
  | 'working'
  | 'waiting_approval'
  | 'completed'
  | 'error'

export type ToolCallStatus =
  | 'pending'
  | 'running'
  | 'completed'
  | 'failed'
  | 'waiting_approval'

export type AgentStateStatus =
  | 'idle'
  | 'active'
  | 'waiting'
  | 'completed'
  | 'error'

export interface AgentActivity {
  status: AgentRunStatus
  runId: string | null
  runStartedAt: string | null
  runSummary: string | null
  activeAgentName: string | null
  activeAgentRole: string | null
  completedNodes: number
  totalNodes: number
  lastUpdated: string | null
}

export interface ToolCall {
  runId?: string
  id: string
  toolId: string
  toolName: string
  status: ToolCallStatus
  startedAt: string | null
  completedAt: string | null
  durationMs: number | null
  argsSummary: string
  resultSummary: string | null
  requiresApproval: boolean
  approvalId: string | null
  evidence: string[]
  seq: number
  risk: string
}

export interface ThinkingStep {
  seq?: number
  status?: 'running' | 'completed' | 'failed'
  id: string
  type:
    | 'run_started'
    | 'task_graph'
    | 'agent_assignment'
    | 'supervision'
    | 'context_pack'
    | 'step_result'
    // A tool dispatch that failed or came back with an unknown outcome. It is a
    // step of its own because it is fed back to the worker as a result — the model
    // reads it and decides — so it belongs in the visible reasoning trail.
    | 'tool'
    // A terminal run failure.
    | 'run'
    | 'reasoning'
    // The coordinator handing a sub-task off (task_dispatch) and pausing for results (task_wait).
    | 'dispatch'
    | 'wait'
    // An agent's plan_update plan. One step per agent instance, replaced in place on each update.
    | 'plan'
  title: string
  description: string
  timestamp: string
  durationMs: number | null
  severity?: string
  category?: string
  details?: Record<string, unknown>
}

export interface TurnActivity {
  runId?: string
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
  supervisionReview?: SupervisionReview | null
}

export type SupervisionDecisionOption = 'continue' | 'correct' | 'cancel'

export interface SupervisionReview {
  runId: string
  reasons: string[]
  options: SupervisionDecisionOption[]
}

export interface AgentState {
  agentId: string
  agentName: string
  agentLayer: string
  agentType: string
  status: AgentStateStatus
  lastActiveAt: string | null
  currentTask: string | null
}

export interface ProgressEvent {
  id: string
  seq: number
  type: string
  icon: string
  message: string
  timestamp: string
}

const DEFAULT_ACTIVITY: AgentActivity = {
  status: 'idle',
  runId: null,
  runStartedAt: null,
  runSummary: null,
  activeAgentName: null,
  activeAgentRole: null,
  completedNodes: 0,
  totalNodes: 0,
  lastUpdated: null,
}

function extractString(value: unknown, key: string): string | null {
  if (!value || typeof value !== 'object') return null
  const record = value as Record<string, unknown>
  const v = record[key]
  return typeof v === 'string' ? v : null
}

function extractNumber(value: unknown, key: string): number | null {
  if (!value || typeof value !== 'object') return null
  const record = value as Record<string, unknown>
  const v = record[key]
  return typeof v === 'number' ? v : null
}

function extractArray(value: unknown, key: string): unknown[] {
  if (!value || typeof value !== 'object') return []
  const record = value as Record<string, unknown>
  const v = record[key]
  return Array.isArray(v) ? v : []
}

function extractStringArray(value: unknown, key: string): string[] {
  return extractArray(value, key).filter((item): item is string => typeof item === 'string')
}

// Activity lines are written once per event, in the language the app has at that moment.
const { t, te } = i18n.global

function agentRoleLabel(agentType: string): string {
  return agentType && te(`activity.roles.${agentType}`) ? t(`activity.roles.${agentType}`) : agentType
}

/** How long tool events of one run are gathered into a single timeline read. */
export const TOOL_REFRESH_DELAY_MS = 300
/** A stream that never sends the post-replay heartbeat is treated as replayed after this. */
export const REPLAY_FALLBACK_MS = 2000

export function useAgentActivity(
  sessionId: Ref<string | null>,
  orchestration?: Ref<OrchestrationSnapshotDto | null>,
) {
  const activity = ref<AgentActivity>({ ...DEFAULT_ACTIVITY })
  const supervisionReview = ref<SupervisionReview | null>(null)
  const toolCalls = ref<ToolCall[]>([])
  const thinkingSteps = ref<ThinkingStep[]>([])
  const agentStates = ref<Record<string, AgentState>>({})
  const progressEvents = ref<ProgressEvent[]>([])

  let unsubscribe: (() => void) | null = null
  let cleanupTimer: ReturnType<typeof setTimeout> | null = null
  let lastRunStartedAt: string | null = null
  // Reactive on purpose: `turnActivities` is a computed over these two stores, and a
  // plain object/Map mutates without notifying anyone — the computed kept serving the
  // snapshot from its first evaluation, so a delayed event for an earlier run was
  // saved but never rendered. `ref` gives both the object and the Map deep reactivity.
  const turns = ref<Record<string, {
    activity: AgentActivity
    thinkingSteps: ThinkingStep[]
    toolCalls: ToolCall[]
    agentStates: Record<string, AgentState>
    progressEvents: ProgressEvent[]
  }>>({})
  let generation = 0
  const toolReads = new Map<string, number>()
  /**
   * Tool timeline reads are coalesced per run: the first tool/approval/task event schedules
   * one read TOOL_REFRESH_DELAY_MS later, and the events that follow in that window ride on
   * it. During the initial durable replay nothing is read at all - the runs that saw such
   * events are only marked, and each is read once when Core's post-replay heartbeat arrives
   * (or after REPLAY_FALLBACK_MS for a stream that never sends one). Opening a long session
   * used to issue one full-session read per historical event.
   */
  const toolRefreshTimers = new Map<string, ReturnType<typeof setTimeout>>()
  const replayDirtyRuns = new Set<string>()
  let replaying = false
  let replayFallback: ReturnType<typeof setTimeout> | null = null
  const supervisionReviews = ref(new Map<string, SupervisionReview>())
  const seenEvents = new Set<string>()
  let latestSelectedSequence = 0

  function saveTurn() {
    const id = activity.value.runId
    if (!id) return
    turns.value[id] = {
      activity: { ...activity.value }, thinkingSteps: thinkingSteps.value,
      toolCalls: toolCalls.value, agentStates: agentStates.value, progressEvents: progressEvents.value,
    }
  }

  function selectTurn(id: string) {
    if (cleanupTimer) clearTimeout(cleanupTimer)
    cleanupTimer = null
    const saved = turns.value[id]
    activity.value = saved ? { ...saved.activity } : { ...DEFAULT_ACTIVITY, runId: id }
    thinkingSteps.value = saved?.thinkingSteps ?? []
    toolCalls.value = saved?.toolCalls ?? []
    supervisionReview.value = supervisionReviews.value.get(id) ?? null
    agentStates.value = saved?.agentStates ?? {}
    progressEvents.value = saved?.progressEvents ?? []
    lastRunStartedAt = activity.value.runStartedAt
  }

  const turnActivities = computed<Record<string, TurnActivity>>(() => Object.fromEntries(
    Object.entries(turns.value).map(([runId, turn]) => [runId, {
      runId, thinkingSteps: turn.thinkingSteps, toolCalls: turn.toolCalls,
      supervisionReview: supervisionReviews.value.get(runId) ?? null,
    }]),
  ))

  function reset() {
    generation++
    turns.value = {}
    // A review belongs to the session that raised it: leaving the map behind would
    // resurrect a stale decision gate if the same run id ever reappeared here.
    supervisionReviews.value.clear()
    toolReads.clear()
    clearToolRefreshes()
    seenEvents.clear()
    latestSelectedSequence = 0
    activity.value = { ...DEFAULT_ACTIVITY }
    toolCalls.value = []
    thinkingSteps.value = []
    agentStates.value = {}
    progressEvents.value = []
    lastRunStartedAt = null
    supervisionReview.value = null
    if (cleanupTimer) {
      clearTimeout(cleanupTimer)
      cleanupTimer = null
    }
  }

  function addProgressEvent(seq: number, type: string, icon: string, message: string) {
    const event: ProgressEvent = {
      id: `${seq}-${type}`,
      seq,
      type,
      icon,
      message,
      timestamp: new Date().toISOString(),
    }
    const existing = progressEvents.value.find((e) => e.id === event.id)
    if (existing) return
    progressEvents.value = [...progressEvents.value, event].slice(-20)
  }

  function updateAgentState(
    agentId: string,
    agentName: string,
    agentLayer: string,
    agentType: string,
    status: AgentStateStatus,
    currentTask: string | null = null,
  ) {
    const existing = agentStates.value[agentId]
    agentStates.value = {
      ...agentStates.value,
      [agentId]: {
        agentId,
        agentName,
        agentLayer,
        agentType,
        status,
        lastActiveAt: new Date().toISOString(),
        currentTask: currentTask ?? existing?.currentTask ?? null,
      },
    }
  }

  function processRunStarted(event: EventEnvelope) {
    const runId = event.run_id ?? extractString(event.payload, 'run_id') ?? activity.value.runId
    // Admission and queue receipts describe the same run, not extra thinking steps.
    if (activity.value.runStartedAt) return
    const summary = extractString(event.payload, 'summary')
    lastRunStartedAt = event.ts
    activity.value = {
      ...activity.value,
      status: 'thinking',
      runId,
      runStartedAt: event.ts,
      runSummary: summary,
      activeAgentName: t('activity.meetingAgentName'),
      activeAgentRole: agentRoleLabel('meeting'),
      lastUpdated: event.ts,
    }
    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-run`,
        type: 'run_started',
        title: t('activity.runStarted'),
        description: summary ?? t('activity.runStartedHint'),
        timestamp: event.ts,
        durationMs: null,
      },
    ]
    addProgressEvent(event.seq, event.type, 'play', t('activity.runStarted'))
  }

  function processTaskGraphCreated(event: EventEnvelope) {
    const title = extractString(event.payload, 'title')
    const nodes = extractArray(event.payload, 'nodes')
    // Core's durable task_graph.created contract carries task_count/task_keys;
    // older preview fixtures carried an inline nodes array. Prefer the explicit
    // count so a real one-task run never renders as "0 个任务节点" merely because
    // the event intentionally avoids duplicating the whole graph.
    const nodeCount = extractNumber(event.payload, 'task_count') ?? nodes.length
    activity.value = {
      ...activity.value,
      status: 'thinking',
      totalNodes: nodeCount,
      lastUpdated: event.ts,
    }
    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-graph`,
        type: 'task_graph',
        title: t('activity.taskGraphCreated'),
        description: title
          ? t('activity.taskGraphTitled', { title, count: nodeCount }, nodeCount)
          : t('activity.taskGraphNodes', { count: nodeCount }, nodeCount),
        timestamp: event.ts,
        durationMs: lastRunStartedAt
          ? new Date(event.ts).getTime() - new Date(lastRunStartedAt).getTime()
          : null,
        details: { nodeCount, title },
      },
    ]
    addProgressEvent(event.seq, event.type, 'network', t('activity.taskGraphNodes', { count: nodeCount }, nodeCount))
  }

  function processTaskAssigned(event: EventEnvelope) {
    const agentId = extractString(event.payload, 'agent_id') ?? 'unknown'
    const agentName = extractString(event.payload, 'agent_name') ?? t('activity.agentFallback')
    const agentLayer = extractString(event.payload, 'agent_layer') ?? 'execution'
    const agentType = extractString(event.payload, 'agent_type') ?? ''
    const taskTitle = extractString(event.payload, 'task_title') ?? extractString(event.payload, 'title')
    const role = agentRoleLabel(agentType)

    activity.value = {
      ...activity.value,
      status: 'working',
      activeAgentName: agentName,
      activeAgentRole: role,
      lastUpdated: event.ts,
    }

    updateAgentState(agentId, agentName, agentLayer, agentType, 'active', taskTitle)

    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-assign`,
        type: 'agent_assignment',
        title: t('activity.assigned', { name: agentName }),
        description: taskTitle ?? t('activity.assignedHint', { role }),
        timestamp: event.ts,
        durationMs: null,
        details: { agentId, agentLayer, agentType },
      },
    ]
    addProgressEvent(event.seq, event.type, 'user-check', t('activity.accepted', { name: agentName }))
  }

  function processStepResult(event: EventEnvelope) {
    const agentId = extractString(event.payload, 'agent_id') ?? 'unknown'
    const agentName = extractString(event.payload, 'agent_name') ?? t('activity.agentFallback')
    const summary = extractString(event.payload, 'summary') ?? ''
    const status = extractString(event.payload, 'status') ?? 'completed'
    const agentLayer = extractString(event.payload, 'agent_layer') ?? 'execution'
    const agentType = extractString(event.payload, 'agent_type') ?? ''

    const completed = activity.value.completedNodes + 1
    activity.value = {
      ...activity.value,
      completedNodes: completed,
      lastUpdated: event.ts,
    }

    if (agentStates.value[agentId]) {
      updateAgentState(agentId, agentName, agentLayer, agentType, 'completed')
    }

    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-step`,
        type: 'step_result',
        title: t('activity.stepDone', { name: agentName }),
        description: summary,
        timestamp: event.ts,
        durationMs: null,
        details: { status },
      },
    ]
    addProgressEvent(event.seq, event.type, 'check-circle', t('activity.stepCompleted', { name: agentName }))
  }

  function processSupervision(event: EventEnvelope) {
    // Core's supervision payload carries the decision and its reasons; the
    // severity/category/summary shape only ever existed in preview fixtures. Reading
    // just the fixture fields rendered every real review as a blank
    // "监督发现 · info" row — the user could see a review happened, never what it
    // decided or why, and the escalation gate below is what needs the reasons.
    const decision = extractString(event.payload, 'decision') ?? ''
    const reasons = extractStringArray(event.payload, 'reasons')
    const severity = extractString(event.payload, 'severity')
      ?? (decision === 'escalate' || decision === 'revise' ? 'warning' : 'info')
    const category = extractString(event.payload, 'category') ?? decision
    const summary = extractString(event.payload, 'summary') ?? reasons.join(t('activity.reasonSeparator'))
    const recommendation = extractString(event.payload, 'recommendation') ?? ''

    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-supervision`,
        type: 'supervision',
        title: t('activity.supervisionFinding', { label: decision || severity }),
        description: summary || recommendation || category,
        timestamp: event.ts,
        durationMs: null,
        severity,
        category,
        details: { recommendation, decision, reasons },
      },
    ]
    addProgressEvent(event.seq, event.type, 'shield', t('activity.supervisionCheck', { label: category || severity }))
  }

  function processSupervisionUserReview(event: EventEnvelope) {
    const runId = event.run_id ?? extractString(event.payload, 'run_id') ?? activity.value.runId
    if (!runId) return
    const reasons = extractStringArray(event.payload, 'reasons')
    const options = extractStringArray(event.payload, 'options')
    // Keep only the options this UI can act on; an unrecognised vocabulary must not
    // produce a button whose click has no defined meaning. Nothing recognised means
    // the payload predates the vocabulary, so fall back to the documented trio.
    const recognised = options.filter((option): option is SupervisionDecisionOption =>
      option === 'continue' || option === 'correct' || option === 'cancel')
    supervisionReviews.value.set(runId, {
      runId,
      reasons,
      options: recognised.length > 0 ? recognised : ['continue', 'correct', 'cancel'],
    })
    if (activity.value.runId === runId) supervisionReview.value = supervisionReviews.value.get(runId) ?? null
  }

  function clearSupervisionReview(runId: string) {
    if (!runId) return
    // Overwrite instead of delete: the entry is the "this gate was answered" marker,
    // and a live binding to it must re-evaluate rather than vanish from the map.
    supervisionReviews.value.set(runId, { runId, reasons: [], options: [] })
    if (activity.value.runId === runId) supervisionReview.value = supervisionReviews.value.get(runId) ?? null
  }

  function processContextPack(event: EventEnvelope) {
    const summary = extractString(event.payload, 'summary') ?? ''
    const tokenBudget = extractNumber(event.payload, 'token_budget')
    const compressionRatio = extractNumber(event.payload, 'compression_ratio')

    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-context`,
        type: 'context_pack',
        title: t('activity.contextPackCreated'),
        description:
          compressionRatio != null
            ? t('activity.contextPackRatio', { summary, ratio: compressionRatio.toFixed(2) })
            : summary,
        timestamp: event.ts,
        durationMs: null,
        details: { tokenBudget, compressionRatio },
      },
    ]
    addProgressEvent(event.seq, event.type, 'package', t('activity.contextPackCompressed'))
  }

  function processApprovalRequested(event: EventEnvelope) {
    // Core publishes the id under approval_id; older shapes used id. Reading only
    // 'id' meant the pending call was never marked, so the chat could not offer the
    // approve/reject buttons it already had markup for.
    const approvalId = extractString(event.payload, 'approval_id') ?? extractString(event.payload, 'id')
    const toolId = extractString(event.payload, 'tool_id')
    const summary = extractString(event.payload, 'summary') ?? (toolId ? t('activity.toolNeedsApproval', { tool: toolId }) : t('activity.needsApproval'))

    activity.value = {
      ...activity.value,
      status: 'waiting_approval',
      lastUpdated: event.ts,
    }

    if (approvalId) {
      toolCalls.value = toolCalls.value.map((call) =>
        call.approvalId === approvalId || (toolId != null && call.toolId === toolId)
          ? { ...call, status: 'waiting_approval' as ToolCallStatus, approvalId }
          : call,
      )
    }

    // handleSessionEvent refreshes the authoritative timeline once after projection.

    addProgressEvent(event.seq, event.type, 'alert-circle', t('activity.waitingApproval', { summary }))
  }

  function processApprovalDecided(event: EventEnvelope, decision: 'approved' | 'rejected') {
    const approvalId = extractString(event.payload, 'approval_id') ?? extractString(event.payload, 'id')

    activity.value = {
      ...activity.value,
      status: 'working',
      lastUpdated: event.ts,
    }

    if (approvalId) {
      toolCalls.value = toolCalls.value.map((call) =>
        call.approvalId === approvalId
          ? {
              ...call,
              status: decision === 'approved' ? ('running' as ToolCallStatus) : ('failed' as ToolCallStatus),
            }
          : call,
      )
    }

    addProgressEvent(
      event.seq,
      event.type,
      decision === 'approved' ? 'check' : 'x',
      decision === 'approved' ? t('activity.approvalGranted') : t('activity.approvalRejected'),
    )
  }

  /**
   * One delegated gate decided (delegate-* permission modes). Only a progress note: the call
   * itself moves on approval.decided, which follows once every gate has approved (or one refused).
   * An escalation hands the approval back, so the pending card stays the user's to decide.
   */
  function processGateDecided(event: EventEnvelope) {
    const decision = extractString(event.payload, 'decision')
    const rationale = extractString(event.payload, 'rationale')
    const who = extractString(event.payload, 'gate_kind') === 'conversation_identity' ? t('activity.gateConversation') : t('activity.gateReviewer')
    const text = decision === 'approved' ? t('activity.gateApproved', { who }) : decision === 'rejected' ? t('activity.gateRejected', { who }) : t('activity.gateEscalated', { who })
    addProgressEvent(
      event.seq,
      event.type,
      decision === 'approved' ? 'check' : decision === 'rejected' ? 'x' : 'alert-circle',
      rationale ? t('activity.withDetail', { title: text, detail: rationale }) : text,
    )
  }

  /**
   * A tool dispatch outcome. Every one of these used to be unsubscribed, so the
   * timeline only ever moved when the whole run finished — a call could fail, be
   * fed back to the model, and be retried with nothing shown in between.
   */
  function processToolExecution(event: EventEnvelope) {
    const toolId = extractString(event.payload, 'tool_id') ?? ''
    const category = extractString(event.payload, 'error_category')
    const embeddedFailure = event.payload?.['tool_success'] === false

    if (event.type === 'tool.execution.requested') {
      addProgressEvent(event.seq, event.type, 'wrench', t('activity.toolCalling', { tool: toolId }))
    } else if (event.type === 'tool.execution.completed') {
      addProgressEvent(
        event.seq,
        event.type,
        embeddedFailure ? 'alert-triangle' : 'check',
        embeddedFailure ? t('activity.toolReturnedFailure', { tool: toolId }) : t('activity.toolCompleted', { tool: toolId }),
      )
    } else {
      // failed / outcome_unknown: name the category, because "the tool failed" gives
      // a reader nothing to act on and the category is what the model acted on.
      const label = category ? t('activity.toolFailedWith', { tool: toolId, category: errorCategoryMessage(category) ?? category }) : t('activity.toolFailed', { tool: toolId })
      thinkingSteps.value = [
        ...thinkingSteps.value,
        {
          id: `${event.seq}-tool-${event.type}`,
          type: 'tool',
          title: event.type === 'tool.execution.outcome_unknown' ? t('activity.toolOutcomeUnknown') : t('activity.toolExecutionFailed'),
          description: label,
          timestamp: event.ts,
          durationMs: null,
          severity: 'warning',
          details: { toolId, category },
        },
      ]
      addProgressEvent(event.seq, event.type, 'alert-triangle', label)
    }

  }

  /** A terminal run failure — previously invisible, so a failed run just stopped. */
  function processRunFailed(event: EventEnvelope) {
    const category = extractString(event.payload, 'error_category') ?? ''
    const message = extractString(event.payload, 'message') ?? extractString(event.payload, 'summary') ?? ''
    // The category in words when this build knows it; the server's own text then stays in details.
    const reason = errorCategoryMessage(category)

    activity.value = {
      ...activity.value,
      status: 'error',
      lastUpdated: event.ts,
    }
    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-run-failed`,
        type: 'run',
        title: t('activity.runFailed'),
        description: reason ?? ([category, message].filter(Boolean).join(' · ') || t('activity.runFailed')),
        timestamp: event.ts,
        durationMs: null,
        severity: 'error',
        details: reason && message ? { category, message } : { category },
      },
    ]
    addProgressEvent(event.seq, event.type, 'x', category ? t('activity.runFailedWith', { category: reason ?? category }) : t('activity.runFailed'))
  }

  function processMeetingFallback(event: EventEnvelope) {
    const category = extractString(event.payload, 'error_category') ?? ''
    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-meeting-fallback`,
        type: 'run',
        title: t('activity.summaryDegraded'),
        description: category
          ? t('activity.summaryDegradedWith', { category: errorCategoryMessage(category) ?? category })
          : t('activity.summaryDegradedHint'),
        timestamp: event.ts,
        durationMs: null,
        severity: 'warning',
        category,
      },
    ]
    addProgressEvent(event.seq, event.type, 'alert-triangle', t('activity.summaryModelUnavailable'))
  }

  /**
   * Execution-layer instance events: who got the task, who finished or failed, and
   * where the tool loop stands. Insufficient on its own to drive the icons, but the
   * activity line and the progress list are what tell a watching user that work is
   * still moving.
   */
  function processWorkerEvent(event: EventEnvelope) {
    const slug = extractString(event.payload, 'agent_slug') ?? extractString(event.payload, 'worker_agent_id') ?? 'worker'
    const reason = extractString(event.payload, 'reason')
    const category = extractString(event.payload, 'error_category')
    const message = extractString(event.payload, 'summary') ?? extractString(event.payload, 'message') ?? ''

    activity.value = {
      ...activity.value,
      status: event.type === 'worker.failed' ? 'error' : 'working',
      activeAgentName: slug,
      lastUpdated: event.ts,
    }

    if (event.type === 'worker.assigned') {
      // Core names each assignment with a run-scoped handle (search#2): the only way to tell
      // two instances of one role apart in the list.
      const who = extractString(event.payload, 'handle') ?? slug
      const why = reason === 'coordinator_assigned' ? t('activity.namedByCoordinator') : reason
      addProgressEvent(event.seq, event.type, 'user-check', why ? t('activity.workerTookWith', { who, why }) : t('activity.workerTook', { who }))
      return
    }
    if (event.type === 'worker.tool_failed') {
      // A fed-back tool failure is not a dead end any more: it is an iteration.
      const label = category ? t('activity.workerToolFailedWith', { slug, category: errorCategoryMessage(category) ?? category }) : t('activity.workerToolFailed', { slug })
      addProgressEvent(event.seq, event.type, 'alert-triangle', label)
      return
    }
    if (event.type === 'worker.budget_exhausted') {
      addProgressEvent(event.seq, event.type, 'hourglass', t('activity.workerWrappingUp', { slug, reason: errorCategoryMessage(category) ?? (message || category || t('activity.budgetExhausted')) }))
      return
    }
    if (event.type === 'worker.blocked') {
      updateAgentState(slug, slug, 'execution', slug, 'waiting')
      thinkingSteps.value = [
        ...thinkingSteps.value,
        {
          id: `${event.seq}-worker-blocked`,
          type: 'step_result',
          title: t('activity.workerUnfinished', { slug }),
          description: message || t('activity.workerBlockedHint'),
          timestamp: event.ts,
          durationMs: null,
          severity: 'warning',
          category: 'blocked',
          details: { slug },
        },
      ]
      addProgressEvent(event.seq, event.type, 'alert-triangle', message || t('activity.workerBlocked', { slug }))
      return
    }
    addProgressEvent(
      event.seq,
      event.type,
      event.type === 'worker.failed' ? 'x' : 'check-circle',
      message || (event.type === 'worker.failed' ? t('activity.workerFailed', { slug }) : t('activity.workerFinished', { slug })),
    )
  }

  /**
   * The coordinator's dispatch and wait moves. They are reasoning-trail steps, not just
   * progress lines: they are the only visible answer to "why did search#2 start" and
   * "why has the coordinator gone quiet".
   */
  function processCoordinatorEvent(event: EventEnvelope) {
    const payload = event.payload
    const awaited = extractArray(payload, 'awaited')
      .map((item) => extractString(item, 'handle') ?? extractString(item, 'title') ?? extractString(item, 'task_key'))
      .filter((item): item is string => Boolean(item))
    const waiter = extractString(payload, 'handle') ?? extractString(payload, 'task_key') ?? t('activity.coordinator')
    let step: Pick<ThinkingStep, 'type' | 'title' | 'description'> & { severity?: string }
    let icon: string
    switch (event.type) {
      case 'task.dispatched': {
        const agent = extractString(payload, 'agent') ?? t('activity.executor')
        const followUp = extractString(payload, 'follow_up_of')
        const by = extractString(payload, 'dispatched_by') ?? t('activity.coordinator')
        step = {
          type: 'dispatch',
          title: t('activity.dispatched', { by, agent }),
          description: (extractString(payload, 'title') ?? '') + (followUp ? t('activity.followUpOf', { task: followUp }) : ''),
        }
        icon = 'send'
        break
      }
      case 'task.dispatch_rejected':
        step = {
          type: 'dispatch',
          title: t('activity.dispatchRejected'),
          description: extractString(payload, 'code') ?? '',
          severity: 'warning',
        }
        icon = 'alert-triangle'
        break
      case 'task.wait_started':
        step = { type: 'wait', title: t('activity.waitStarted', { waiter }), description: awaited.join(t('activity.listSeparator')) }
        icon = 'hourglass'
        break
      default:
        step = { type: 'wait', title: t('activity.waitResolved', { waiter }), description: awaited.join(t('activity.listSeparator')) }
        icon = 'check-circle'
    }
    activity.value = { ...activity.value, lastUpdated: event.ts }
    thinkingSteps.value = [
      ...thinkingSteps.value,
      { id: `${event.seq}-${event.type}`, timestamp: event.ts, durationMs: null, ...step },
    ]
    addProgressEvent(event.seq, event.type, icon, step.description ? t('activity.withDetail', { title: step.title, detail: step.description }) : step.title)
  }

  /**
   * An agent's working plan. Every update carries the whole plan, so the step is keyed by
   * the task and replaced in place: the trail shows the plan's current state, not a pile
   * of stale copies.
   */
  function processPlanUpdated(event: EventEnvelope) {
    const payload = event.payload
    const taskId = extractString(payload, 'task_id') ?? extractString(payload, 'task_key') ?? String(event.seq)
    const who = extractString(payload, 'handle') ?? extractString(payload, 'task_key') ?? t('activity.agentFallback')
    const steps = extractArray(payload, 'steps')
      .map((item) => ({ step: extractString(item, 'step') ?? '', status: extractString(item, 'status') ?? 'pending' }))
      .filter((item) => item.step)
    const done = steps.filter((item) => item.status === 'completed').length
    const marker: Record<string, string> = { completed: '✓', in_progress: '→', pending: '○' }
    const id = `plan-${taskId}`
    const step: ThinkingStep = {
      id,
      type: 'plan',
      title: t('activity.planTitle', { who, done, total: steps.length }),
      description: steps.map((item) => `${marker[item.status] ?? '○'} ${item.step}`).join('\n'),
      timestamp: event.ts,
      durationMs: null,
      details: { steps, explanation: extractString(payload, 'explanation') },
    }
    thinkingSteps.value = thinkingSteps.value.some((item) => item.id === id)
      ? thinkingSteps.value.map((item) => item.id === id ? step : item)
      : [...thinkingSteps.value, step]
    activity.value = { ...activity.value, lastUpdated: event.ts }
    const current = steps.find((item) => item.status === 'in_progress')
    addProgressEvent(event.seq, event.type, 'list-checks', current ? t('activity.withDetail', { title: who, detail: current.step }) : t('activity.planUpdated', { who }))
  }

  function processShellApprovalRequired(event: EventEnvelope) {
    const command = extractString(event.payload, 'command') ?? ''
    const approvalId = extractString(event.payload, 'approval_id')

    activity.value = {
      ...activity.value,
      status: 'waiting_approval',
      lastUpdated: event.ts,
    }

    if (approvalId) {
      const existing = toolCalls.value.find((c) => c.approvalId === approvalId)
      if (!existing) {
        toolCalls.value = [
          ...toolCalls.value,
          {
            id: `shell-${event.seq}`,
            toolId: 'shell',
            toolName: t('activity.shellExecution'),
            status: 'waiting_approval',
            startedAt: event.ts,
            completedAt: null,
            durationMs: null,
            argsSummary: command,
            resultSummary: null,
            requiresApproval: true,
            approvalId,
            evidence: [],
            seq: event.seq,
            risk: 'high',
          },
        ]
      }
    }

    addProgressEvent(event.seq, event.type, 'terminal', t('activity.shellAwaitingApproval', { command }))
  }

  function processMessageCreated(event: EventEnvelope) {
    const role = extractString(event.payload, 'role')
    if (role === 'assistant') {
      const completed = activity.value.completedNodes
      const total = activity.value.totalNodes
      const isComplete = total > 0 && completed >= total
      activity.value = {
        ...activity.value,
        status: isComplete ? 'completed' : 'working',
        lastUpdated: event.ts,
      }
      if (isComplete) {
        scheduleCleanup()
      }
    }
    addProgressEvent(event.seq, event.type, 'message-square', t('activity.messageCreated'))
  }

  function processEvent(event: EventEnvelope) {
    switch (event.type) {
      case 'model.output.started':
      case 'model.output.delta':
      case 'model.output.completed':
      case 'model.output.failed':
      case 'model.output.interrupted': {
        const id = extractString(event.payload, 'response_id')
        if (!id) break
        const existing = thinkingSteps.value.find((step) => step.id === id)
        const delta = extractString(event.payload, 'delta') ?? ''
        // A thought the user cut off ended; it did not fail.
        const status = event.type === 'model.output.completed' || event.type === 'model.output.interrupted' ? 'completed'
          : event.type === 'model.output.failed' ? 'failed' : 'running'
        const step: ThinkingStep = existing ? { ...existing, status } : {
          // The run handle (search#2) names the instance; parallel workers of one role
          // share agent_name and would otherwise render as identical "search" thoughts.
          id, type: 'reasoning', title: extractString(event.payload, 'handle') ?? extractString(event.payload, 'agent_name') ?? t('activity.modelFallback'),
          description: '', timestamp: event.ts, seq: event.seq, durationMs: null, status,
        }
        // Only the provider's explicit reasoning channel is a reasoning text. Internal
        // planning JSON, prompts and tool arguments never become a "thought" here.
        if (event.payload?.channel === 'reasoning') step.description += delta
        thinkingSteps.value = existing
          ? thinkingSteps.value.map((item) => item.id === id ? step : item)
          : [...thinkingSteps.value, step]
        break
      }
      // Lifecycle. The real Core names come first; run.started is kept because the
      // envelope mapper still produces it for older frames.
      case 'run.started':
      case 'task.accepted':
      case 'interaction.created':
      case 'run.queued':
        processRunStarted(event)
        break
      case 'task_graph.created':
        processTaskGraphCreated(event)
        break
      case 'task.assigned':
        processTaskAssigned(event)
        break
      case 'worker.assigned':
      case 'worker.completed':
      case 'worker.blocked':
      case 'worker.failed':
      case 'worker.tool_failed':
      case 'worker.budget_exhausted':
        processWorkerEvent(event)
        break
      case 'step.result.created':
        processStepResult(event)
        break
      case 'worker.interrupted':
      case 'run.interrupted':
        addProgressEvent(event.seq, event.type, 'hand', event.type === 'worker.interrupted'
          ? t('activity.workerInterrupted', { who: extractString(event.payload, 'handle') ?? t('activity.executor') })
          : t('activity.runInterrupted'))
        break
      case 'task.dispatched':
      case 'task.dispatch_rejected':
      case 'task.wait_started':
      case 'task.wait_resolved':
        processCoordinatorEvent(event)
        break
      case 'plan.updated':
        processPlanUpdated(event)
        break
      case 'supervision.checked':
      case 'supervision.requested':
      case 'supervision.completed':
      case 'supervision.skipped':
        processSupervision(event)
        break
      case 'supervision.user_review.requested':
        processSupervisionUserReview(event)
        break
      case 'supervision.user_decision':
      case 'context.goal_adjusted':
        clearSupervisionReview(event.run_id ?? extractString(event.payload, 'run_id') ?? activity.value.runId ?? '')
        break
      case 'context.pack.created':
      case 'context.packed':
        processContextPack(event)
        break
      // Tool outcomes: subscribed to but never handled before, so the timeline only
      // moved at the end of the run.
      case 'tool.execution.requested':
      case 'tool.execution.completed':
      case 'tool.execution.failed':
      case 'tool.execution.outcome_unknown':
        processToolExecution(event)
        break
      case 'approval.requested':
      case 'tool.shell.approval_required':
        if (event.type === 'tool.shell.approval_required') {
          processShellApprovalRequired(event)
        } else {
          processApprovalRequested(event)
        }
        break
      case 'approval.approved':
        processApprovalDecided(event, 'approved')
        break
      case 'approval.rejected':
        processApprovalDecided(event, 'rejected')
        break
      case 'approval.gate_decided':
        processGateDecided(event)
        break
      case 'approval.decided':
      case 'governance.permission_decided': {
        // Core's real names for a decision; the outcome vocabulary differs between
        // the approval layer (approved/rejected) and the PDP (allowed/denied).
        const outcome = extractString(event.payload, 'outcome') ?? extractString(event.payload, 'decision') ?? ''
        processApprovalDecided(event, outcome === 'approved' || outcome === 'allowed' ? 'approved' : 'rejected')
        break
      }
      case 'run.failed':
        processRunFailed(event)
        break
      case 'meeting.response_fallback':
        processMeetingFallback(event)
        break
      case 'message.created':
        processMessageCreated(event)
        break
      default:
        break
    }
  }

  function scheduleCleanup() {
    if (cleanupTimer) clearTimeout(cleanupTimer)
    cleanupTimer = setTimeout(() => {
      activity.value = { ...activity.value, status: 'idle' }
      cleanupTimer = null
    }, 30000)
  }

  function clearToolRefreshes() {
    for (const timer of toolRefreshTimers.values()) clearTimeout(timer)
    toolRefreshTimers.clear()
    replayDirtyRuns.clear()
  }

  function requestToolRefresh(runId: string | null) {
    if (!runId) return
    if (replaying) {
      replayDirtyRuns.add(runId)
      return
    }
    if (toolRefreshTimers.has(runId)) return
    toolRefreshTimers.set(runId, setTimeout(() => {
      toolRefreshTimers.delete(runId)
      void fetchToolExecutions(runId)
    }, TOOL_REFRESH_DELAY_MS))
  }

  function endReplay() {
    if (replayFallback) clearTimeout(replayFallback)
    replayFallback = null
    if (!replaying) return
    replaying = false
    const dirty = [...replayDirtyRuns]
    replayDirtyRuns.clear()
    dirty.forEach(requestToolRefresh)
  }

  /** Read the current run's tool timeline now (an explicit refresh, not an event). */
  function refreshToolExecutions() {
    return fetchToolExecutions(activity.value.runId)
  }

  async function fetchToolExecutions(runId: string | null) {
    const session = sessionId.value
    if (!session || !runId) return
    const epoch = generation
    const read = (toolReads.get(runId) ?? 0) + 1
    toolReads.set(runId, read)
    try {
      const items = await api.listToolExecutions(session, { run_id: runId, limit: 200 })
      if (epoch !== generation || session !== sessionId.value || toolReads.get(runId) !== read) return
      const calls = items.filter((item) => item.run_id === runId)
        .map((item: ToolExecutionTimelineItemDto): ToolCall => ({
          runId,
          id: item.id,
          toolId: item.tool_id,
          toolName: item.tool_display_name || item.tool_id,
          status: mapToolStatus(item.status),
          startedAt: item.requested_at,
          completedAt: item.status === 'completed' || item.status === 'failed' ? item.updated_at : null,
          durationMs: item.duration_ms || null,
          argsSummary: item.checkpoint_summary || item.summary,
          resultSummary: item.summary,
          requiresApproval: item.requires_approval,
          approvalId: item.approval_id ?? null,
          evidence: item.evidence,
          seq: item.requested_seq,
          risk: item.risk,
        }))
        .sort((a, b) => a.seq - b.seq)
      if (activity.value.runId === runId) {
        toolCalls.value = calls
        saveTurn()
      } else if (turns.value[runId]) turns.value[runId].toolCalls = calls
    } catch {
      // ignore fetch errors
    }
  }

  function mapToolStatus(status: string): ToolCallStatus {
    switch (status) {
      case 'completed':
        return 'completed'
      case 'failed':
      case 'error':
        return 'failed'
      case 'running':
      case 'pending':
        return 'running'
      case 'waiting_approval':
      case 'approval_required':
        return 'waiting_approval'
      default:
        return 'pending'
    }
  }

  function enrichFromOrchestration(snapshot: OrchestrationSnapshotDto | null) {
    if (!snapshot) return
    if (snapshot.run?.session_id && snapshot.run.session_id !== sessionId.value) return

    if (snapshot.run && snapshot.run.id !== activity.value.runId) {
      // A late snapshot for an already seen run must not replace the current turn.
      if (turns.value[snapshot.run.id]) return
      saveTurn()
      selectTurn(snapshot.run.id)
    }

    if (snapshot.run) {
      const isComplete =
        snapshot.run.status === 'completed' || snapshot.run.status === 'failed'
      const isError = snapshot.run.status === 'failed'
      activity.value = {
        ...activity.value,
        runId: activity.value.runId ?? snapshot.run.id,
        runSummary: snapshot.run.summary || activity.value.runSummary,
        status: isError
          ? 'error'
          : isComplete
            ? 'completed'
            : activity.value.status === 'idle'
              ? 'working'
              : activity.value.status,
      }
    }

    if (snapshot.nodes.length > 0) {
      const completed = snapshot.nodes.filter(
        (n) => n.status === 'completed' || n.status === 'done',
      ).length
      activity.value = {
        ...activity.value,
        totalNodes: snapshot.nodes.length,
        completedNodes: completed || activity.value.completedNodes,
      }
    }

    for (const assignment of snapshot.assignments) {
      const existing = agentStates.value[assignment.agent_id]
      const status: AgentStateStatus =
        assignment.status === 'completed'
          ? 'completed'
          : assignment.status === 'active' || assignment.status === 'running'
            ? 'active'
            : assignment.status === 'waiting'
              ? 'waiting'
              : assignment.status === 'failed' || assignment.status === 'error'
                ? 'error'
                : 'idle'
      updateAgentState(
        assignment.agent_id,
        assignment.agent_name,
        assignment.agent_layer,
        assignment.agent_type,
        status,
        existing?.currentTask ?? null,
      )
    }
    saveTurn()
  }

  function handleSessionEvent(event: EventEnvelope) {
    const runId = event.run_id ?? extractString(event.payload, 'run_id')
    const key = `${runId}:${event.seq}:${event.type}`
    if (seenEvents.has(key)) return
    seenEvents.add(key)
    const previous = activity.value.runId
    const known = runId ? !!turns.value[runId] : false
    const historical = event.seq < latestSelectedSequence
    if (runId && previous !== runId) {
      saveTurn()
      selectTurn(runId)
    }
    processEvent(event)
    if (['task.cancelled', 'run.failed', 'user.response'].includes(event.type)) {
      thinkingSteps.value = thinkingSteps.value.map((step) => step.status === 'running'
        ? { ...step, status: event.type === 'user.response' ? 'completed' : 'failed' } : step)
    }
    thinkingSteps.value = thinkingSteps.value.map((step) => step.seq == null ? { ...step, seq: event.seq } : step)
    saveTurn()
    if (
      event.type.startsWith('tool.') ||
      event.type.startsWith('approval.') ||
      event.type === 'governance.permission_decided' ||
      event.type.startsWith('step.') ||
      event.type.startsWith('task')
    ) {
      requestToolRefresh(runId ?? activity.value.runId)
    }
    if ((known || historical) && previous && previous !== runId) selectTurn(previous)
    else latestSelectedSequence = Math.max(latestSelectedSequence, event.seq)
  }

  function connect() {
    disconnect()
    if (!sessionId.value) return
    // The window's one session stream is owned by sessionEventBus; scoping the
    // subscription is what keeps this view on the session it was built for.
    replaying = true
    if (replayFallback) clearTimeout(replayFallback)
    replayFallback = setTimeout(endReplay, REPLAY_FALLBACK_MS)
    unsubscribe = subscribeToSessionEvents(handleSessionEvent, { scope: sessionId, onHeartbeat: endReplay })
  }

  function disconnect() {
    unsubscribe?.()
    unsubscribe = null
    replaying = false
    if (replayFallback) clearTimeout(replayFallback)
    replayFallback = null
    clearToolRefreshes()
  }

  watch(
    sessionId,
    (newId, oldId) => {
      if (newId !== oldId) {
        reset()
        connect()
      }
    },
    { immediate: true },
  )

  watch(
    () => orchestration?.value ?? null,
    (snapshot) => {
      enrichFromOrchestration(snapshot)
    },
    { immediate: true, deep: true },
  )

  // onScopeDispose, not onUnmounted: the subscription is now into a window-wide bus, so
  // a view that is torn down outside a component (an effect scope, a detached panel that
  // is closed and recreated) would otherwise leave its handler registered and keep the
  // connection alive for a panel that no longer exists.
  onScopeDispose(() => {
    generation++
    disconnect()
    if (cleanupTimer) clearTimeout(cleanupTimer)
  })

  return {
    activity,
    toolCalls,
    thinkingSteps,
    supervisionReview,
    turnActivities,
    agentStates,
    progressEvents,
    reset,
    refreshToolExecutions,
  }
}
