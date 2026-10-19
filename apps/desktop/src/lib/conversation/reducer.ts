import type { EventEnvelope } from '@/api'
import { readCorrectionTrace, type CorrectionTrace } from './correction'
import { readSupervisionReviewBrief, type SupervisionReviewBrief } from './supervision'

/**
 * Event-sourced model of what each run in a session did, built only from the session
 * SSE stream (live frames and the durable replay that precedes them). Nothing here
 * reads REST: the old activity composable re-fetched the whole tool timeline on every
 * tool event, which is what made a long session slow to open.
 *
 * Records are mutable and versioned. The caller applies a batch, then publishes the
 * touched runs (`shallowRef` + version bump) once per frame. Output is structured
 * data and status codes; every user-visible word is chosen by a component through i18n.
 *
 * Payload keys are Core's own (see `TinadecCore/DmaEA/FullDuplexRunEngine.cs` and
 * `Tools/ToolDispatcher.cs`); `normalizeEventEnvelope` already flattens the durable
 * `{ sequence, summary, payload }` wrapper, so live and replayed frames read the same.
 */

export type ToolStatus =
  | 'requested'
  | 'awaiting_approval'
  | 'running'
  | 'completed'
  | 'failed'
  | 'outcome_unknown'
  | 'cancelled'

export interface ToolEntry {
  kind: 'tool'
  id: string
  seq: number
  taskId: string | null
  agentInstanceId: string | null
  toolId: string
  status: ToolStatus
  risk: string | null
  requiresApproval: boolean
  mutatesWorkspace: boolean
  approvalId: string | null
  argumentsSummary: string | null
  resourcePath: string | null
  command: string | null
  cwd: string | null
  query: string | null
  /** The tool's own success flag (a shell exit status, a write that refused). */
  toolSuccess: boolean | null
  errorCategory: string | null
  exitCode: number | null
  timedOut: boolean
  /** Shell output tail, at most {@link OUTPUT_LINE_LIMIT} complete lines plus the open one. */
  output: string[]
  outputPartial: string
  outputDropped: number
  /** The agent terminal session a shell call streams into, once its first frame names it. */
  terminalSessionId: string | null
  changedPaths: string[]
  startedAt: string
  endedAt: string | null
}

export interface ReasoningEntry {
  kind: 'reasoning'
  id: string
  seq: number
  taskId: string | null
  handle: string | null
  agentName: string | null
  text: string
  /**
   * The call's public text while it works — a task worker's draft or narration (Solo's master
   * answering its task). Core journals it for task-bound calls only; the conversation's own
   * answer streams on the run stream instead.
   */
  output: string
  status: 'running' | 'completed' | 'failed' | 'interrupted'
  startedAt: string
  endedAt: string | null
}

export interface DispatchEntry {
  kind: 'dispatch'
  id: string
  seq: number
  /** The dispatched task, i.e. the sub-agent this entry introduces. */
  taskId: string
  /** The task that issued the dispatch; null for the coordinator or a planner-authored task. */
  parentTaskId: string | null
  /** True when no `task.dispatched` was seen and the entry stands in for a planner-authored task. */
  synthetic: boolean
  at: string
}

export interface WaitEntry {
  kind: 'wait'
  id: string
  seq: number
  taskId: string | null
  handle: string | null
  awaited: { taskKey: string | null; handle: string | null; title: string | null }[]
  status: 'waiting' | 'resolved'
  startedAt: string
  endedAt: string | null
}

export interface PlanStep {
  step: string
  status: 'pending' | 'in_progress' | 'completed'
}

export interface PlanEntry {
  kind: 'plan'
  id: string
  seq: number
  taskId: string | null
  handle: string | null
  steps: PlanStep[]
  /** The plan as it stood before the latest update, for the "rewritten" highlight. */
  previous: PlanStep[] | null
  explanation: string | null
  at: string
}

export interface CorrectionEntry {
  kind: 'correction'
  id: string
  seq: number
  taskId: null
  trace: CorrectionTrace
  at: string
}

export type MarkerType =
  | 'compacted'
  | 'interrupted'
  | 'model_retry'
  | 'model_recovered'
  | 'recovered'
  | 'workspace_bound'
  | 'response_fallback'
  | 'run_failed'
  | 'run_cancelled'
  | 'worker_interrupted'

export interface MarkerEntry {
  kind: 'marker'
  id: string
  seq: number
  taskId: string | null
  type: MarkerType
  at: string
  /** Structured facts for the component (error category, retry number, root path…). */
  data: Record<string, string | number | boolean | null>
}

export interface SupervisionEntry {
  kind: 'supervision'
  id: string
  seq: number
  taskId: null
  decision: string
  reasons: string[]
  at: string
}

export type FeedEntry = ToolEntry | ReasoningEntry | DispatchEntry | WaitEntry | PlanEntry | CorrectionEntry | MarkerEntry | SupervisionEntry

export type SubAgentStatus = 'waiting' | 'running' | 'completed' | 'failed' | 'blocked' | 'cancelled'

export interface SubAgent {
  taskId: string
  taskKey: string | null
  handle: string | null
  agentSlug: string | null
  reason: string | null
  title: string | null
  dispatchedBy: string | null
  followUpOf: string | null
  status: SubAgentStatus
  summary: string | null
  startedAt: string | null
  endedAt: string | null
  /** `solo_master`: the run's own agent, whose entries belong to the turn itself. */
  isMain: boolean
}

export interface RunActivity {
  runId: string
  /** Bumped on every applied event; a component re-derives only when it changes. */
  version: number
  entries: FeedEntry[]
  tasks: Map<string, SubAgent>
  firstAt: string | null
  lastAt: string | null
  failure: { category: string | null; message: string | null } | null
  /**
   * A supervision escalation Core is waiting on. This is run state, not a log line: it is
   * why the run stopped, and it stays true until the person decides (or a decision event
   * clears it), so it survives regrouping, folding and a reload — a reader who reopens the
   * session must still find the gate.
   */
  review: { reasons: string[]; options: string[]; brief: SupervisionReviewBrief | null; at: string } | null
  correction: CorrectionTrace | null
  /** Internal indexes. */
  byId: Map<string, FeedEntry>
  seen: Set<string>
  executionByApproval: Map<string, string>
}

export interface FeedState {
  runs: Map<string, RunActivity>
}

export const OUTPUT_LINE_LIMIT = 200

export function createFeedState(): FeedState {
  return { runs: new Map() }
}

function run(state: FeedState, runId: string): RunActivity {
  let record = state.runs.get(runId)
  if (!record) {
    record = {
      runId,
      version: 0,
      entries: [],
      tasks: new Map(),
      firstAt: null,
      lastAt: null,
      failure: null,
      review: null,
      correction: null,
      byId: new Map(),
      seen: new Set(),
      executionByApproval: new Map(),
    }
    state.runs.set(runId, record)
  }
  return record
}

type Payload = Record<string, unknown>

function str(payload: Payload, key: string): string | null {
  const value = payload[key]
  return typeof value === 'string' && value.length > 0 ? value : null
}

function num(payload: Payload, key: string): number | null {
  const value = payload[key]
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function bool(payload: Payload, key: string): boolean | null {
  const value = payload[key]
  return typeof value === 'boolean' ? value : null
}

function scalar(value: unknown): string | number | boolean | null {
  return typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean' ? value : null
}

function add<T extends FeedEntry>(record: RunActivity, entry: T): T {
  record.entries.push(entry)
  record.byId.set(entry.id, entry)
  return entry
}

function find<K extends FeedEntry['kind']>(record: RunActivity, id: string, kind: K): Extract<FeedEntry, { kind: K }> | null {
  const entry = record.byId.get(id)
  return entry && entry.kind === kind ? (entry as Extract<FeedEntry, { kind: K }>) : null
}

function tool(record: RunActivity, executionId: string, event: EventEnvelope, payload: Payload): ToolEntry {
  const existing = find(record, executionId, 'tool')
  if (existing) return existing
  // A frame can precede its request (a replay cut mid-run); create the row and let the
  // request fill the sentence fields when it arrives.
  return add(record, {
    kind: 'tool',
    id: executionId,
    seq: event.seq,
    taskId: str(payload, 'task_id'),
    agentInstanceId: null,
    toolId: str(payload, 'tool_id') ?? 'tool',
    status: 'requested',
    risk: null,
    requiresApproval: false,
    mutatesWorkspace: false,
    approvalId: null,
    argumentsSummary: null,
    resourcePath: null,
    command: null,
    cwd: null,
    query: null,
    toolSuccess: null,
    errorCategory: null,
    exitCode: null,
    timedOut: false,
    output: [],
    outputPartial: '',
    outputDropped: 0,
    terminalSessionId: null,
    changedPaths: [],
    startedAt: event.ts,
    endedAt: null,
  })
}

const TERMINAL_TOOL: ReadonlySet<ToolStatus> = new Set(['completed', 'failed', 'outcome_unknown', 'cancelled'])

function settle(entry: ToolEntry, status: ToolStatus, at: string) {
  entry.status = status
  if (TERMINAL_TOOL.has(status)) entry.endedAt = at
}

function appendOutput(entry: ToolEntry, data: string) {
  const parts = (entry.outputPartial + data).split('\n')
  entry.outputPartial = parts.pop() ?? ''
  for (const line of parts) entry.output.push(line.endsWith('\r') ? line.slice(0, -1) : line)
  const overflow = entry.output.length - OUTPUT_LINE_LIMIT
  if (overflow > 0) {
    entry.output.splice(0, overflow)
    entry.outputDropped += overflow
  }
}

function task(record: RunActivity, taskId: string): SubAgent {
  let agent = record.tasks.get(taskId)
  if (!agent) {
    agent = {
      taskId,
      taskKey: null,
      handle: null,
      agentSlug: null,
      reason: null,
      title: null,
      dispatchedBy: null,
      followUpOf: null,
      status: 'waiting',
      summary: null,
      startedAt: null,
      endedAt: null,
      isMain: false,
    }
    record.tasks.set(taskId, agent)
  }
  return agent
}

/**
 * `task.dispatched` names the new task by `dispatch_id`, which Core reuses as the task id
 * only when it is unique. When `worker.assigned` arrives for an id never dispatched, the
 * dispatch it belongs to is the pending one with the same task key.
 */
function adoptDispatch(record: RunActivity, taskId: string, taskKey: string | null): SubAgent | null {
  if (!taskKey) return null
  for (const [id, agent] of record.tasks) {
    if (id !== taskId && agent.taskKey === taskKey && !agent.handle && agent.status === 'waiting') {
      record.tasks.delete(id)
      agent.taskId = taskId
      record.tasks.set(taskId, agent)
      const dispatch = find(record, `dispatch:${id}`, 'dispatch')
      if (dispatch) {
        record.byId.delete(dispatch.id)
        dispatch.id = `dispatch:${taskId}`
        dispatch.taskId = taskId
        record.byId.set(dispatch.id, dispatch)
      }
      return agent
    }
  }
  return null
}

function handleOwner(record: RunActivity, handle: string | null): string | null {
  if (!handle) return null
  for (const agent of record.tasks.values()) if (agent.handle === handle) return agent.taskId
  return null
}

function marker(record: RunActivity, event: EventEnvelope, type: MarkerType, data: MarkerEntry['data'], taskId: string | null = null) {
  add(record, { kind: 'marker', id: `marker:${event.seq}:${event.type}`, seq: event.seq, taskId, type, at: event.ts, data })
}

function runIdOf(event: EventEnvelope, payload: Payload): string | null {
  // The interrupt is admitted by a new interaction; it is a fact about the run it cut off.
  if (event.type === 'interaction.interrupt_requested') return str(payload, 'target_run_id') ?? event.run_id ?? null
  return event.run_id ?? str(payload, 'run_id')
}

/**
 * Applies one event. Returns the run it changed, or null when the event was a duplicate,
 * carried no run, or is not part of the feed.
 */
export function applyEvent(state: FeedState, event: EventEnvelope): RunActivity | null {
  const payload: Payload = event.payload ?? {}
  const runId = runIdOf(event, payload)
  if (!runId) return null
  const record = run(state, runId)
  // Sequences are allocated per run; replay and live overlap is deduplicated here.
  const key = `${event.seq}:${event.type}`
  if (record.seen.has(key)) return null
  if (!reduce(record, event, payload)) return null
  record.seen.add(key)
  record.firstAt ??= event.ts
  if (!record.lastAt || event.ts > record.lastAt) record.lastAt = event.ts
  record.version += 1
  return record
}

/** Applies a batch and returns the runs it touched, each once. */
export function applyEvents(state: FeedState, events: Iterable<EventEnvelope>): Set<RunActivity> {
  const touched = new Set<RunActivity>()
  for (const event of events) {
    const record = applyEvent(state, event)
    if (record) touched.add(record)
  }
  return touched
}

function reduce(record: RunActivity, event: EventEnvelope, payload: Payload): boolean {
  switch (event.type) {
    case 'model.output.started':
    case 'model.output.delta':
    case 'model.output.completed':
    case 'model.output.failed':
    case 'model.output.interrupted': {
      const responseId = str(payload, 'response_id')
      if (!responseId) return false
      const id = `reasoning:${responseId}`
      let entry = find(record, id, 'reasoning')
      if (!entry) {
        // A task worker's call opens at once: it may think silently or only write, and a turn
        // with no entry shows no work at all while it waits. Other calls (the conversation's
        // answer, which streams elsewhere) open on their first thought.
        const taskBound = Boolean(str(payload, 'task_id'))
        const opens = event.type === 'model.output.started' ? taskBound
          : event.type === 'model.output.delta' && (payload.channel === 'reasoning' || taskBound)
        if (!opens) return false
        entry = add(record, {
          kind: 'reasoning',
          id,
          seq: event.seq,
          taskId: str(payload, 'task_id'),
          handle: str(payload, 'handle'),
          agentName: str(payload, 'agent_name'),
          text: '',
          output: '',
          status: 'running',
          startedAt: event.ts,
          endedAt: null,
        })
      }
      if (event.type === 'model.output.delta') {
        if (payload.channel === 'reasoning') entry.text += str(payload, 'delta') ?? ''
        else if (payload.channel === 'text') entry.output += str(payload, 'delta') ?? ''
      } else if (event.type !== 'model.output.started') {
        entry.status = event.type === 'model.output.completed' ? 'completed'
          : event.type === 'model.output.interrupted' ? 'interrupted' : 'failed'
        entry.endedAt = event.ts
      }
      return true
    }

    case 'tool.execution.requested': {
      const executionId = str(payload, 'execution_id')
      if (!executionId) return false
      const entry = tool(record, executionId, event, payload)
      entry.seq = Math.min(entry.seq, event.seq)
      entry.taskId = str(payload, 'task_id') ?? entry.taskId
      entry.agentInstanceId = str(payload, 'agent_instance_id')
      entry.toolId = str(payload, 'tool_id') ?? entry.toolId
      entry.risk = str(payload, 'risk')
      entry.requiresApproval = bool(payload, 'requires_approval') ?? false
      entry.mutatesWorkspace = bool(payload, 'mutates_workspace') ?? false
      entry.argumentsSummary = str(payload, 'arguments_summary')
      entry.resourcePath = str(payload, 'resource_path')
      entry.command = str(payload, 'command') ?? entry.command
      entry.cwd = str(payload, 'cwd') ?? entry.cwd
      entry.query = str(payload, 'query')
      if (event.ts < entry.startedAt) entry.startedAt = event.ts
      return true
    }
    case 'approval.requested': {
      const executionId = str(payload, 'execution_id')
      if (!executionId) return false
      const entry = tool(record, executionId, event, payload)
      entry.requiresApproval = true
      // The PDP park names its request; the decision endpoint accepts that id, so it is the approval id.
      entry.approvalId = str(payload, 'approval_id') ?? str(payload, 'permission_request_id') ?? entry.approvalId
      for (const id of [str(payload, 'approval_id'), str(payload, 'permission_request_id')]) {
        if (id) record.executionByApproval.set(id, executionId)
      }
      if (!TERMINAL_TOOL.has(entry.status)) entry.status = 'awaiting_approval'
      return true
    }
    case 'approval.pre_authorized_minted':
    case 'approval.auto_decided':
    case 'approval.decided':
    case 'governance.permission_decided': {
      const approvalKey = str(payload, 'approval_id') ?? str(payload, 'permission_request_id')
      const executionId = str(payload, 'execution_id') ?? (approvalKey ? record.executionByApproval.get(approvalKey) ?? null : null)
      if (!executionId) return false
      const entry = find(record, executionId, 'tool')
      if (!entry || TERMINAL_TOOL.has(entry.status)) return Boolean(entry)
      const verdict = event.type === 'governance.permission_decided' ? str(payload, 'outcome') : str(payload, 'decision')
      const approved = event.type === 'approval.pre_authorized_minted' || verdict === 'approved' || verdict === 'allowed'
      if (approved) entry.status = 'running'
      else {
        settle(entry, 'failed', event.ts)
        entry.errorCategory ??= 'approval_rejected'
      }
      return true
    }
    case 'terminal.command': {
      const executionId = str(payload, 'execution_id')
      if (!executionId) return false
      const entry = tool(record, executionId, event, payload)
      entry.command ??= str(payload, 'command')
      entry.cwd ??= str(payload, 'cwd')
      if (!TERMINAL_TOOL.has(entry.status)) entry.status = 'running'
      return true
    }
    case 'terminal.stdout': {
      const executionId = str(payload, 'execution_id')
      const data = str(payload, 'data')
      if (!executionId || data === null) return false
      const entry = tool(record, executionId, event, payload)
      entry.terminalSessionId ??= str(payload, 'terminal_session_id')
      appendOutput(entry, data)
      return true
    }
    case 'terminal.exit': {
      const executionId = str(payload, 'execution_id')
      if (!executionId) return false
      const entry = tool(record, executionId, event, payload)
      entry.terminalSessionId ??= str(payload, 'terminal_session_id')
      entry.exitCode = num(payload, 'exit_code')
      entry.timedOut = bool(payload, 'timed_out') ?? false
      return true
    }
    case 'workspace.changes_observed': {
      const executionId = str(payload, 'execution_id')
      if (!executionId) return false
      const entry = tool(record, executionId, event, payload)
      const paths = Array.isArray(payload.paths) ? payload.paths.filter((p): p is string => typeof p === 'string') : []
      entry.changedPaths = [...new Set([...entry.changedPaths, ...paths])]
      return true
    }
    case 'tool.execution.completed':
    case 'tool.execution.failed':
    case 'tool.execution.outcome_unknown': {
      const executionId = str(payload, 'execution_id')
      if (!executionId) return false
      const entry = tool(record, executionId, event, payload)
      if (event.type === 'tool.execution.completed') {
        entry.toolSuccess = bool(payload, 'tool_success')
        entry.errorCategory = null
        settle(entry, 'completed', event.ts)
      } else {
        entry.errorCategory = str(payload, 'error_category') ?? str(payload, 'category')
        // A retryable failure is followed by another attempt of the same execution.
        if (event.type === 'tool.execution.failed' && bool(payload, 'retryable') === true) entry.status = 'running'
        else settle(entry, event.type === 'tool.execution.failed' ? 'failed' : 'outcome_unknown', event.ts)
      }
      return true
    }

    case 'task.dispatched': {
      const taskId = str(payload, 'dispatch_id')
      if (!taskId) return false
      const agent = task(record, taskId)
      agent.taskKey = str(payload, 'task_key') ?? agent.taskKey
      agent.title = str(payload, 'title') ?? agent.title
      agent.agentSlug ??= str(payload, 'agent')
      agent.dispatchedBy = str(payload, 'dispatched_by')
      agent.followUpOf = str(payload, 'follow_up_of')
      if (!record.byId.has(`dispatch:${taskId}`)) {
        add(record, {
          kind: 'dispatch',
          id: `dispatch:${taskId}`,
          seq: event.seq,
          taskId,
          parentTaskId: handleOwner(record, agent.dispatchedBy),
          synthetic: false,
          at: event.ts,
        })
      }
      return true
    }
    case 'worker.assigned': {
      const taskId = str(payload, 'task_id')
      if (!taskId) return false
      const taskKey = str(payload, 'task_key')
      const agent = record.tasks.get(taskId) ?? adoptDispatch(record, taskId, taskKey) ?? task(record, taskId)
      agent.taskKey = taskKey ?? agent.taskKey
      agent.title = str(payload, 'title') ?? agent.title
      agent.handle = str(payload, 'handle') ?? agent.handle
      agent.agentSlug = str(payload, 'agent_slug') ?? agent.agentSlug
      agent.reason = str(payload, 'reason')
      agent.isMain = agent.reason === 'solo_master'
      agent.status = 'running'
      agent.startedAt ??= event.ts
      if (!agent.isMain && !record.byId.has(`dispatch:${taskId}`)) {
        add(record, { kind: 'dispatch', id: `dispatch:${taskId}`, seq: event.seq, taskId, parentTaskId: null, synthetic: true, at: event.ts })
      }
      return true
    }
    case 'worker.completed':
    case 'worker.blocked':
    case 'worker.failed': {
      const taskId = str(payload, 'task_id')
      if (!taskId) return false
      const agent = task(record, taskId)
      agent.status = event.type === 'worker.completed' ? 'completed' : event.type === 'worker.blocked' ? 'blocked' : 'failed'
      agent.summary = str(payload, 'summary') ?? agent.summary
      agent.endedAt = event.ts
      return true
    }
    case 'task.cancelled': {
      const taskId = str(payload, 'task_id')
      if (!taskId) return false
      const agent = task(record, taskId)
      agent.status = 'cancelled'
      agent.endedAt = event.ts
      return true
    }
    case 'worker.interrupted':
      marker(record, event, 'worker_interrupted', { during: scalar(payload.during) }, str(payload, 'task_id'))
      return true

    case 'task.wait_started':
    case 'task.wait_resolved': {
      const callId = str(payload, 'call_id') ?? String(event.seq)
      const id = `wait:${str(payload, 'task_id') ?? 'run'}:${callId}`
      const awaited = (Array.isArray(payload.awaited) ? payload.awaited : [])
        .filter((item): item is Payload => !!item && typeof item === 'object')
        .map((item) => ({ taskKey: str(item, 'task_key'), handle: str(item, 'handle'), title: str(item, 'title') }))
      let entry = find(record, id, 'wait')
      if (!entry) {
        entry = add(record, {
          kind: 'wait',
          id,
          seq: event.seq,
          taskId: str(payload, 'task_id'),
          handle: str(payload, 'handle'),
          awaited,
          status: 'waiting',
          startedAt: event.ts,
          endedAt: null,
        })
      }
      if (event.type === 'task.wait_resolved') {
        entry.status = 'resolved'
        entry.endedAt = event.ts
        // The resolution lists statuses only; keep the titles the start carried.
        if (!entry.awaited.length) entry.awaited = awaited
      }
      return true
    }
    case 'plan.updated': {
      const taskId = str(payload, 'task_id')
      const id = `plan:${taskId ?? str(payload, 'task_key') ?? 'run'}`
      const steps = (Array.isArray(payload.steps) ? payload.steps : [])
        .filter((item): item is Payload => !!item && typeof item === 'object')
        .map((item) => ({
          step: str(item, 'step') ?? '',
          status: (['pending', 'in_progress', 'completed'].includes(String(item.status)) ? item.status : 'pending') as PlanStep['status'],
        }))
        .filter((item) => item.step)
      const existing = find(record, id, 'plan')
      if (existing) {
        existing.previous = existing.steps
        existing.steps = steps
        existing.explanation = str(payload, 'explanation')
        existing.at = event.ts
      } else {
        add(record, { kind: 'plan', id, seq: event.seq, taskId, handle: str(payload, 'handle'), steps, previous: null, explanation: str(payload, 'explanation'), at: event.ts })
      }
      return true
    }

    case 'correction.trace.updated':
    case 'correction.feedback.received':
    case 'correction.question.answered': {
      const trace = readCorrectionTrace(payload)
      if (!trace) return false
      record.correction = trace
      const id = `correction:${trace.episodeId}`
      const existing = find(record, id, 'correction')
      if (existing) {
        existing.trace = trace
        existing.at = event.ts
      } else {
        add(record, {
          kind: 'correction',
          id,
          seq: event.seq,
          taskId: null,
          trace,
          at: event.ts,
        })
      }
      return true
    }
    case 'correction.question.requested': {
      const trace = readCorrectionTrace(payload)
      if (trace) {
        record.correction = trace
        return true
      }
      return false
    }
    case 'correction.episode.reopened':
      return Boolean(record.correction)

    case 'supervision.completed': {
      const decision = str(payload, 'decision')
      if (!decision) return false
      const reasons = Array.isArray(payload.reasons) ? payload.reasons.filter((r): r is string => typeof r === 'string') : []
      add(record, { kind: 'supervision', id: `supervision:${event.seq}`, seq: event.seq, taskId: null, decision, reasons, at: event.ts })
      return true
    }
    case 'supervision.user_review.requested': {
      // Core is parked on a person. Held as run state rather than a log node so the gate
      // survives refolding and reopening the session — an escalation the reader cannot find
      // again is an escalation they cannot answer.
      const reasons = Array.isArray(payload.reasons) ? payload.reasons.filter((r): r is string => typeof r === 'string') : []
      const options = Array.isArray(payload.options) ? payload.options.filter((o): o is string => typeof o === 'string') : []
      record.review = { reasons, options, brief: readSupervisionReviewBrief(payload.brief), at: event.ts }
      return true
    }
    case 'supervision.user_decision':
    case 'context.goal_adjusted':
      // Answered, or superseded by an accepted correction: the gate must stop asking.
      if (!record.review) return false
      record.review = null
      return true
    case 'context.compacted':
      marker(record, event, 'compacted', { agent: scalar(payload.agent_slug) })
      return true
    case 'run.interrupted':
    case 'interaction.interrupt_requested': {
      // The request and the cut share a patch id and are one fact for a reader.
      const id = `marker:interrupt:${str(payload, 'patch_id') ?? event.seq}`
      if (record.byId.has(id)) return true
      add(record, { kind: 'marker', id, seq: event.seq, taskId: null, type: 'interrupted', at: event.ts, data: {} })
      return true
    }
    case 'run.model_retry_scheduled':
      marker(record, event, 'model_retry', {
        retry: scalar(payload.retry_number),
        limit: scalar(payload.retry_limit),
        delayMs: scalar(payload.delay_ms),
        category: scalar(payload.error_category),
      })
      return true
    case 'run.model_retry_recovered':
      marker(record, event, 'model_recovered', { retries: scalar(payload.recovered_retry_count) })
      return true
    case 'run.recovered':
      marker(record, event, 'recovered', { reason: scalar(payload.reason) })
      return true
    case 'session.workspace_bound':
      marker(record, event, 'workspace_bound', { root: scalar(payload.root_path), created: scalar(payload.project_created) })
      return true
    case 'meeting.response_fallback':
      marker(record, event, 'response_fallback', { category: scalar(payload.error_category) })
      return true
    case 'run.failed': {
      const category = str(payload, 'error_category')
      const message = str(payload, 'summary') ?? str(payload, 'message')
      record.failure = { category, message }
      marker(record, event, 'run_failed', { category, message })
      return true
    }
    default:
      return false
  }
}
