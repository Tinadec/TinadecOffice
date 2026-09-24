import { computed, ref, watch, onScopeDispose, type Ref } from 'vue'
import {
  api,
  type EventEnvelope,
  type OrchestrationSnapshotDto,
  type ToolExecutionTimelineItemDto,
} from '@/api'
import { subscribeToSessionEvents } from '@/lib/sessionEventBus'

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

function agentRoleLabel(agentType: string): string {
  const labels: Record<string, string> = {
    meeting: '会议智能体',
    context_compressor: '上下文压缩',
    prompt_context_engineer: '提示词工程师',
    evolver: '进化智能体',
    tool_assistant: '工具助理',
    supervisor: '监督智能体',
    skill_learner: '技能学习',
    task_planner: '任务规划',
    test_multimodal: '测试多模态',
    code_explorer: '代码探查',
    search_specialist: '搜索专家',
    file_finder: '文件查找',
    git_manager: 'Git 管理',
    code_writer: '代码编写',
    designer: '设计智能体',
  }
  return labels[agentType] ?? agentType
}

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
      activeAgentName: 'Meeting Agent',
      activeAgentRole: '会议智能体',
      lastUpdated: event.ts,
    }
    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-run`,
        type: 'run_started',
        title: '编排运行已启动',
        description: summary ?? '智能体开始分析用户意图',
        timestamp: event.ts,
        durationMs: null,
      },
    ]
    addProgressEvent(event.seq, event.type, 'play', '编排运行已启动')
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
        title: '任务图已创建',
        description: title
          ? `${title}（${nodeCount} 个任务节点）`
          : `创建了 ${nodeCount} 个任务节点`,
        timestamp: event.ts,
        durationMs: lastRunStartedAt
          ? new Date(event.ts).getTime() - new Date(lastRunStartedAt).getTime()
          : null,
        details: { nodeCount, title },
      },
    ]
    addProgressEvent(event.seq, event.type, 'network', `创建了 ${nodeCount} 个任务节点`)
  }

  function processTaskAssigned(event: EventEnvelope) {
    const agentId = extractString(event.payload, 'agent_id') ?? 'unknown'
    const agentName = extractString(event.payload, 'agent_name') ?? 'Agent'
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
        title: `${agentName} 已分配任务`,
        description: taskTitle ?? `${role} 开始执行任务`,
        timestamp: event.ts,
        durationMs: null,
        details: { agentId, agentLayer, agentType },
      },
    ]
    addProgressEvent(event.seq, event.type, 'user-check', `${agentName} 接受了任务分配`)
  }

  function processStepResult(event: EventEnvelope) {
    const agentId = extractString(event.payload, 'agent_id') ?? 'unknown'
    const agentName = extractString(event.payload, 'agent_name') ?? 'Agent'
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
        title: `${agentName} 完成步骤`,
        description: summary,
        timestamp: event.ts,
        durationMs: null,
        details: { status },
      },
    ]
    addProgressEvent(event.seq, event.type, 'check-circle', `${agentName} 完成了一个步骤`)
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
    const summary = extractString(event.payload, 'summary') ?? reasons.join('；')
    const recommendation = extractString(event.payload, 'recommendation') ?? ''

    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-supervision`,
        type: 'supervision',
        title: decision ? `监督发现 · ${decision}` : `监督发现 · ${severity}`,
        description: summary || recommendation || category,
        timestamp: event.ts,
        durationMs: null,
        severity,
        category,
        details: { recommendation, decision, reasons },
      },
    ]
    addProgressEvent(event.seq, event.type, 'shield', `监督检查：${category || severity}`)
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
        title: '上下文包已创建',
        description:
          compressionRatio != null
            ? `${summary}（压缩比 ${compressionRatio.toFixed(2)}x）`
            : summary,
        timestamp: event.ts,
        durationMs: null,
        details: { tokenBudget, compressionRatio },
      },
    ]
    addProgressEvent(event.seq, event.type, 'package', '上下文包已压缩')
  }

  function processApprovalRequested(event: EventEnvelope) {
    // Core publishes the id under approval_id; older shapes used id. Reading only
    // 'id' meant the pending call was never marked, so the chat could not offer the
    // approve/reject buttons it already had markup for.
    const approvalId = extractString(event.payload, 'approval_id') ?? extractString(event.payload, 'id')
    const toolId = extractString(event.payload, 'tool_id')
    const summary = extractString(event.payload, 'summary') ?? (toolId ? `工具 ${toolId} 需要审批` : '需要审批')

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

    addProgressEvent(event.seq, event.type, 'alert-circle', `等待审批：${summary}`)
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
      decision === 'approved' ? '审批已通过' : '审批已拒绝',
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
      addProgressEvent(event.seq, event.type, 'wrench', `调用工具 ${toolId}`)
    } else if (event.type === 'tool.execution.completed') {
      addProgressEvent(
        event.seq,
        event.type,
        embeddedFailure ? 'alert-triangle' : 'check',
        embeddedFailure ? `工具 ${toolId} 返回失败结果` : `工具 ${toolId} 完成`,
      )
    } else {
      // failed / outcome_unknown: name the category, because "the tool failed" gives
      // a reader nothing to act on and the category is what the model acted on.
      const label = category ? `${toolId} 失败（${category}）` : `${toolId} 失败`
      thinkingSteps.value = [
        ...thinkingSteps.value,
        {
          id: `${event.seq}-tool-${event.type}`,
          type: 'tool',
          title: event.type === 'tool.execution.outcome_unknown' ? '工具结果未知' : '工具执行失败',
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
        title: '运行失败',
        description: [category, message].filter(Boolean).join(' · ') || '运行失败',
        timestamp: event.ts,
        durationMs: null,
        severity: 'error',
        details: { category },
      },
    ]
    addProgressEvent(event.seq, event.type, 'x', `运行失败${category ? `：${category}` : ''}`)
  }

  function processMeetingFallback(event: EventEnvelope) {
    const category = extractString(event.payload, 'error_category') ?? ''
    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-meeting-fallback`,
        type: 'run',
        title: '最终汇总已降级',
        description: category
          ? `汇总模型不可用（${category}），已直接返回持久化的执行证据。`
          : '汇总模型不可用，已直接返回持久化的执行证据。',
        timestamp: event.ts,
        durationMs: null,
        severity: 'warning',
        category,
      },
    ]
    addProgressEvent(event.seq, event.type, 'alert-triangle', '最终汇总模型不可用，已返回执行证据')
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
      addProgressEvent(event.seq, event.type, 'user-check', `${slug} 接单${reason ? `（${reason}）` : ''}`)
      return
    }
    if (event.type === 'worker.tool_failed') {
      // A fed-back tool failure is not a dead end any more: it is an iteration.
      const label = category ? `${slug} 的调用失败（${category}），已交回模型` : `${slug} 的调用失败，已交回模型`
      addProgressEvent(event.seq, event.type, 'alert-triangle', label)
      return
    }
    if (event.type === 'worker.budget_exhausted') {
      addProgressEvent(event.seq, event.type, 'hourglass', `${slug} 收敛收尾：${message || category || '预算耗尽'}`)
      return
    }
    if (event.type === 'worker.blocked') {
      updateAgentState(slug, slug, 'execution', slug, 'waiting')
      thinkingSteps.value = [
        ...thinkingSteps.value,
        {
          id: `${event.seq}-worker-blocked`,
          type: 'step_result',
          title: `${slug} 未完成任务`,
          description: message || '执行者报告任务受阻，系统将重新规划或请求用户处理。',
          timestamp: event.ts,
          durationMs: null,
          severity: 'warning',
          category: 'blocked',
          details: { slug },
        },
      ]
      addProgressEvent(event.seq, event.type, 'alert-triangle', message || `${slug} 的任务受阻`)
      return
    }
    addProgressEvent(
      event.seq,
      event.type,
      event.type === 'worker.failed' ? 'x' : 'check-circle',
      message || (event.type === 'worker.failed' ? `${slug} 失败` : `${slug} 完成`),
    )
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
            toolName: 'Shell 执行',
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

    addProgressEvent(event.seq, event.type, 'terminal', `Shell 命令等待审批：${command}`)
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
    addProgressEvent(event.seq, event.type, 'message-square', '新消息已生成')
  }

  function processEvent(event: EventEnvelope) {
    switch (event.type) {
      case 'model.output.started':
      case 'model.output.delta':
      case 'model.output.completed':
      case 'model.output.failed': {
        const id = extractString(event.payload, 'response_id')
        if (!id) break
        const existing = thinkingSteps.value.find((step) => step.id === id)
        const delta = extractString(event.payload, 'delta') ?? ''
        const status = event.type === 'model.output.completed' ? 'completed'
          : event.type === 'model.output.failed' ? 'failed' : 'running'
        const step: ThinkingStep = existing ? { ...existing, status } : {
          id, type: 'reasoning', title: extractString(event.payload, 'agent_name') ?? 'Model',
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

  async function refreshToolExecutions() {
    const session = sessionId.value
    const runId = activity.value.runId
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
      void refreshToolExecutions()
    }
    if ((known || historical) && previous && previous !== runId) selectTurn(previous)
    else latestSelectedSequence = Math.max(latestSelectedSequence, event.seq)
  }

  function connect() {
    disconnect()
    if (!sessionId.value) return
    // The window's one session stream is owned by sessionEventBus; scoping the
    // subscription is what keeps this view on the session it was built for.
    unsubscribe = subscribeToSessionEvents(handleSessionEvent, { scope: sessionId })
  }

  function disconnect() {
    unsubscribe?.()
    unsubscribe = null
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
