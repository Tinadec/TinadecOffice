import { ref, watch, onUnmounted, type Ref } from 'vue'
import {
  api,
  type EventEnvelope,
  type OrchestrationSnapshotDto,
  type ToolExecutionTimelineItemDto,
} from '@/api'

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
  id: string
  type:
    | 'run_started'
    | 'task_graph'
    | 'agent_assignment'
    | 'supervision'
    | 'context_pack'
    | 'step_result'
  title: string
  description: string
  timestamp: string
  durationMs: number | null
  severity?: string
  category?: string
  details?: Record<string, unknown>
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
  const toolCalls = ref<ToolCall[]>([])
  const thinkingSteps = ref<ThinkingStep[]>([])
  const agentStates = ref<Record<string, AgentState>>({})
  const progressEvents = ref<ProgressEvent[]>([])

  let eventSource: EventSource | null = null
  let cleanupTimer: ReturnType<typeof setTimeout> | null = null
  let lastRunStartedAt: string | null = null

  function reset() {
    activity.value = { ...DEFAULT_ACTIVITY }
    toolCalls.value = []
    thinkingSteps.value = []
    agentStates.value = {}
    progressEvents.value = []
    lastRunStartedAt = null
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
    const runId = extractString(event.payload, 'id')
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
    const nodeCount = nodes.length
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
    const severity = extractString(event.payload, 'severity') ?? 'info'
    const category = extractString(event.payload, 'category') ?? ''
    const summary = extractString(event.payload, 'summary') ?? ''
    const recommendation = extractString(event.payload, 'recommendation') ?? ''

    thinkingSteps.value = [
      ...thinkingSteps.value,
      {
        id: `${event.seq}-supervision`,
        type: 'supervision',
        title: `监督发现 · ${severity}`,
        description: summary || recommendation || category,
        timestamp: event.ts,
        durationMs: null,
        severity,
        category,
        details: { recommendation },
      },
    ]
    addProgressEvent(event.seq, event.type, 'shield', `监督检查：${category || severity}`)
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
    const approvalId = extractString(event.payload, 'id')
    const summary = extractString(event.payload, 'summary') ?? '需要审批'

    activity.value = {
      ...activity.value,
      status: 'waiting_approval',
      lastUpdated: event.ts,
    }

    if (approvalId) {
      toolCalls.value = toolCalls.value.map((call) =>
        call.approvalId === approvalId
          ? { ...call, status: 'waiting_approval' as ToolCallStatus }
          : call,
      )
    }

    addProgressEvent(event.seq, event.type, 'alert-circle', `等待审批：${summary}`)
  }

  function processApprovalDecided(event: EventEnvelope, decision: 'approved' | 'rejected') {
    const approvalId = extractString(event.payload, 'id')

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
      case 'run.started':
        processRunStarted(event)
        break
      case 'task_graph.created':
        processTaskGraphCreated(event)
        break
      case 'task.assigned':
        processTaskAssigned(event)
        break
      case 'step.result.created':
        processStepResult(event)
        break
      case 'supervision.checked':
        processSupervision(event)
        break
      case 'context.pack.created':
        processContextPack(event)
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
      activity.value = { ...DEFAULT_ACTIVITY }
      cleanupTimer = null
    }, 30000)
  }

  async function refreshToolExecutions() {
    if (!sessionId.value) return
    try {
      const items = await api.listToolExecutions(sessionId.value, { limit: 20 })
      toolCalls.value = items
        .map((item: ToolExecutionTimelineItemDto): ToolCall => ({
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
  }

  function connect() {
    disconnect()
    if (!sessionId.value) return
    eventSource = api.connectEvents(sessionId.value, (event) => {
      processEvent(event)
      if (
        event.type.startsWith('tool.') ||
        event.type.startsWith('approval.') ||
        event.type.startsWith('step.') ||
        event.type.startsWith('task')
      ) {
        void refreshToolExecutions()
      }
    })
  }

  function disconnect() {
    if (eventSource) {
      eventSource.close()
      eventSource = null
    }
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

  onUnmounted(() => {
    disconnect()
    if (cleanupTimer) clearTimeout(cleanupTimer)
  })

  return {
    activity,
    toolCalls,
    thinkingSteps,
    agentStates,
    progressEvents,
    reset,
    refreshToolExecutions,
  }
}
