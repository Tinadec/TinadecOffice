import { computed, ref, watch, type Ref } from 'vue'
import {
  api,
  createUserToolActionForPath,
  type ApprovalDto,
  type DoctorReportDto,
  type EventEnvelope,
  type MessageDto,
  type ModelSettingsDto,
  type OrchestrationSnapshotDto,
  type ProjectDto,
  type RuntimeReadinessReceiptDto,
  type SessionDto,
  type ToolExecutionTimelineItemDto,
} from '@/api'
import { basenameFromPath } from '@/format'
import { getDispatchPref } from '@/lib/dispatchPref'
import { useAgentActivity } from '@/composables/useAgentActivity'
import { useNotifications } from '@/composables/useNotifications'
import type { AgentMode, PermissionLevel } from '@/types/mode'
// generated client is canonical; api.ts stays as compat alias (see bottom of api.ts)
import type { DispatchMode, MeetingModelOverrideDto } from '@/api'
import { userToolActionIdempotencyKey, userToolActionToApproval } from '@/userToolAction'
import { createRunStream, type RunStreamHandle } from '@/composables/useRunStream'
import { generatedApi } from '@/generated/client'
import { useRunStore } from '@/stores/run'

// ---------------------------------------------------------------------------
// HomeController — the single domain controller for the Home page.
//
// Module-level singleton that owns ALL Home data/state, so every Home card
// (nav, chat, git, approval, ...) reads the same sources and shares one SSE
// connection. This is the direct migration of HomePage.vue's script logic.
// ---------------------------------------------------------------------------

const projects = ref<ProjectDto[]>([])
const sessions = ref<SessionDto[]>([])
const messages = ref<MessageDto[]>([])
const approvals = ref<ApprovalDto[]>([])
const events = ref<EventEnvelope[]>([])
const doctor = ref<DoctorReportDto | null>(null)
const readiness = ref<RuntimeReadinessReceiptDto | null>(null)
const modelSettings = ref<ModelSettingsDto | null>(null)
const orchestration = ref<OrchestrationSnapshotDto | null>(null)
const toolExecutions = ref<ToolExecutionTimelineItemDto[]>([])

const selectedProjectId = ref<string | null>(null)
const selectedSessionId = ref<string | null>(null)
const pendingSessionId = ref<string | null>(null)
const draft = ref('')
const modelBaseUrl = ref('https://api.openai.com/v1')
// 空串 = 未解析。硬编码兜底值会在 readiness 回执缺 model_route 时冒充真实模型名，
// 让「没配好模型」看起来像「配好了」。UI 在空值时显示「未配置」。
const modelName = ref('')
const modelApiKey = ref('')
const shellCommand = ref('npm test')
const busy = ref(false)
const eventSource = ref<EventSource | null>(null)
const rightRailCollapsed = ref(false)
const rightRailWidth = ref(420)
const AGENT_MODE_KEY = 'tinadec.agent_mode'
const AGENT_MODES: AgentMode[] = ['plan', 'spec', 'ask', 'vibe', 'auto', 'agent']
function readStoredMode(): AgentMode {
  if (typeof localStorage === 'undefined') return 'auto'
  try {
    const value = localStorage.getItem(AGENT_MODE_KEY)
    return value && (AGENT_MODES as string[]).includes(value) ? (value as AgentMode) : 'auto'
  } catch {
    return 'auto'
  }
}
const currentMode = ref<AgentMode>(readStoredMode())
watch(currentMode, (mode) => {
  try { localStorage.setItem(AGENT_MODE_KEY, mode) } catch { /* storage unavailable */ }
})
const currentPermission = ref<PermissionLevel>('default')
const runs = ref<Array<{ id: string; status: string }>>([])
const queuedMessages = ref<Array<{ id: string; content: string }>>([])
const runStreams = new Map<string, RunStreamHandle>()
const runText = new Map<string, string>()
// 运行指示（问题 3 修复）：是否有活跃的 run 流。runStreams 是非响应式 Map，computed
// 无法追踪，故用显式 ref 并在每次 set/delete/clear 后 syncWorking()。用流数量而非
// activeRuns.length：activeRuns 含 lane_waiting/gate_review 等长驻态，会让指示永久
// 亮起；流随 done/error 的 disconnect+delete 天然归零。
const working = ref(false)
// 最近一次 run stream 活动时间（ack/delta/heartbeat 等任意 chunk）：供 UI 区分
// 「链路活着但暂无输出」与「链路已断」。
const lastStreamActivityAt = ref<number | null>(null)
function syncWorking() { working.value = runStreams.size > 0 }

const currentProject = computed(() => projects.value.find((p) => p.id === selectedProjectId.value) ?? null)
// 活动运行 = 非终态且不驻留人工决策（对齐 Core CountActiveRunsAsync 的口径，
// 词表以共享 12 态为准，不再使用自造的 running/ready/pending/queued）。
const activeRuns = computed(() => runs.value.filter((r) => !['completed', 'failed', 'cancelled', 'awaiting_user'].includes(r.status)))
const currentSession = computed(() => sessions.value.find((s) => s.id === selectedSessionId.value) ?? null)
const recentEvents = computed(() => events.value.slice(-8).reverse())

const sessionIdRef = computed(() => currentSession.value?.id ?? null)
const {
  activity: agentActivity,
  toolCalls: agentToolCalls,
  thinkingSteps: agentThinkingSteps,
  agentStates: agentStatesMap,
  progressEvents: agentProgressEvents,
} = useAgentActivity(sessionIdRef, orchestration)

const { notify, banner, dismissByKey } = useNotifications()

function generateTitle(content: string): string {
  const trimmed = content.trim()
  if (!trimmed) return 'New chat'
  const firstLine = trimmed.split('\n')[0]
  if (firstLine.length <= 50) return firstLine
  return firstLine.substring(0, 47) + '...'
}

function newId(): string {
  return (globalThis.crypto as Crypto | undefined)?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

async function run(label: string, action: () => Promise<void>) {
  busy.value = true
  try {
    await action()
  } catch (err) {
    notify.error(err, { title: `${label} failed` })
  } finally {
    busy.value = false
  }
}

async function loadInitial() {
  busy.value = true
  try {
    // GET /model-settings 是恒空的旧 stub（ControlPlaneEndpoints 501 家族），
    // 模型事实一律来自 readiness/model-readiness/model-providers。
    const [projectList, report, readinessReceipt] = await Promise.all([
      api.listProjects(),
      api.doctor(),
      api.readiness(),
    ])
    projects.value = projectList
    doctor.value = report
    readiness.value = readinessReceipt
    // 头部的模型名/地址改由统一 readiness receipt 供给（model_route/model_provider 项）。
    const items = (readinessReceipt as { items?: Array<{ id: string; data?: { model?: string; base_url?: string } }> }).items ?? []
    const routeData = items.find((item) => item.id === 'model_route')?.data
    if (routeData?.model) modelName.value = routeData.model
    if (routeData?.base_url) modelBaseUrl.value = routeData.base_url
    selectedProjectId.value = projectList[0]?.id ?? null
    await loadSessions()
    dismissByKey('home-load')
  } catch (err) {
    banner.error({
      key: 'home-load',
      title: '加载失败',
      message: '加载数据失败',
      details: err instanceof Error ? err.message : '加载失败',
      action: { label: '重试', run: () => loadInitial() },
    })
  } finally {
    busy.value = false
  }
}

async function loadSessions() {
  if (projects.value.length === 0) {
    sessions.value = []
    selectedSessionId.value = null
    return
  }
  const allSessions = await Promise.all(
    projects.value.map((p) => api.listSessions(p.id)),
  )
  sessions.value = allSessions.flat()
  if (!selectedProjectId.value) {
    selectedSessionId.value = null
    return
  }
  const projectSessions = sessions.value.filter((s) => s.project_id === selectedProjectId.value)
  if (!projectSessions.find((s) => s.id === selectedSessionId.value)) {
    selectedSessionId.value = projectSessions[0]?.id ?? null
  }
}

async function loadMessagesAndApprovals() {
  if (!selectedSessionId.value) {
    messages.value = []
    approvals.value = []
    orchestration.value = null
    toolExecutions.value = []
    runs.value = []
    return
  }
  const [messageList, approvalList, orchestrationSnapshot, toolTimeline, runList] = await Promise.all([
    api.listMessages(selectedSessionId.value),
    api.listApprovals(selectedSessionId.value),
    api.getOrchestrationSnapshot(selectedSessionId.value),
    api.listToolExecutions(selectedSessionId.value, { limit: 12 }),
    api.listRuns(selectedSessionId.value).catch(() => [] as unknown[]),
  ])
  // Keep optimistic pending sends until the backend echoes them: the
  // session-select reload races the first POST (new session has no messages
  // yet), and wiping the optimistic append bounces the composer back to the
  // hero position mid-dock instead of one immersive sink.
  const pendingEcho = messages.value.filter(
    (m) => m.id.startsWith('pending-') && !messageList.some((b) => b.role === 'user' && b.content === m.content),
  )
  messages.value = pendingEcho.length ? [...messageList, ...pendingEcho] : messageList
  approvals.value = approvalList
  orchestration.value = orchestrationSnapshot
  toolExecutions.value = toolTimeline
  runs.value = (Array.isArray(runList) ? runList : []).map((r) => ({ id: String((r as Record<string, unknown>).id), status: String((r as Record<string, unknown>).status ?? '') }))
  // 状态源统一（问题 3 修复）：ChatHeader 的 run-pills 读 Pinia runStore.runs，而
  // runStore.fetchRuns 此前只在无入口的 WorkbenchPage 调用 → Home 页 pills 恒空。
  // 把 Home 已拉取的 run 列表（含 session_id，WorkbenchPage.control 依赖）同步进 store。
  // HomeController 是模块级单例，可能早于 Pinia 安装被求值，故惰性获取 + 兜底。
  try {
    useRunStore().runs = (Array.isArray(runList) ? runList : []) as never
  } catch { /* Pinia 尚未安装：pills 退回空态，不阻断聊天 */ }
  attachActiveRuns()
}

function streamDelta(chunk: import('@/generated/client').SseChunk): string {
  const payload = chunk.payload as Record<string, unknown>
  return typeof payload.delta === 'string' ? payload.delta : ''
}

function attachRun(runId: string) {
  if (runStreams.has(runId)) return
  const handle = createRunStream({
    runId,
    onActivity: (chunk) => {
      // 活性信号：任意去重后的 chunk（含 ack/heartbeat）都刷新活动时间，供 UI 区分
      // 「链路活着但暂无输出」与「链路已断」。
      lastStreamActivityAt.value = Date.now()
      // ack 是「智能体已接收」的最早信号：乐观把该 run 置为 planning 并同步进 store，
      // 让 ChatHeader 的 pill 立即出现，而不必等首个 delta 或 done。
      if (chunk.kind === 'ack') {
        runs.value = runs.value.some((r) => r.id === runId)
          ? runs.value.map((r) => (r.id === runId ? { ...r, status: 'planning' } : r))
          : [...runs.value, { id: runId, status: 'planning' }]
        try { useRunStore().runs = runs.value as never } catch { /* Pinia 未就绪 */ }
      }
    },
    onChunk: (chunk) => {
      if (chunk.kind === 'delta') {
        const delta = streamDelta(chunk)
        if (delta) {
          const next = `${runText.get(runId) ?? ''}${delta}`
          runText.set(runId, next)
          streamingText.value = new Map(streamingText.value).set(runId, next)
        }
        return
      }
      if (chunk.kind === 'done' || chunk.kind === 'error') {
        if (chunk.kind === 'error') {
          const payload = chunk.payload as Record<string, unknown>
          const message = payload.safe_error_message ?? payload.message ?? payload.error_category
          invokeError.value = typeof message === 'string' ? message : '运行失败'
        }
        void loadMessagesAndApprovals()
        runStreams.get(runId)?.disconnect()
        runStreams.delete(runId)
        syncWorking()
      }
    },
    onError: (error) => {
      if (!navigator.onLine) invokeError.value = '网络已断开'
      else if (error.message) invokeError.value = error.message
    },
  })
  runStreams.set(runId, handle)
  syncWorking()
  handle.connect()
}

function attachActiveRuns() {
  if (!selectedSessionId.value) return
  for (const run of activeRuns.value) attachRun(run.id)
}

async function openProject() {
  await run('open project', async () => {
    const path = await window.tinadec.openProjectDialog()
    if (!path) return
    const project = await api.createProject(basenameFromPath(path), path)
    projects.value = [project, ...projects.value.filter((item) => item.id !== project.id)]
    selectedProjectId.value = project.id
  })
}

async function createSession(projectId: string) {
  if (pendingSessionId.value) {
    const existing = sessions.value.find((s) => s.id === pendingSessionId.value)
    if (existing && existing.project_id === projectId) {
      selectedSessionId.value = pendingSessionId.value
      selectedProjectId.value = projectId
      return
    }
  }
  await run('create session', async () => {
    const session = await api.createSession(projectId, 'Tinadec session')
    sessions.value = [session, ...sessions.value]
    selectedSessionId.value = session.id
    selectedProjectId.value = projectId
    pendingSessionId.value = session.id
  })
}

// ---------------------------------------------------------------------------
// Project/session lifecycle management (rename / archive / trash)
// ---------------------------------------------------------------------------

async function refreshProjectsAndSessions() {
  const projectList = await api.listProjects()
  projects.value = projectList
  const allSessions = await Promise.all(projectList.map((p) => api.listSessions(p.id)))
  sessions.value = allSessions.flat()
  if (selectedProjectId.value && !projectList.some((p) => p.id === selectedProjectId.value)) {
    selectedProjectId.value = projectList[0]?.id ?? null
  }
  const projectSessions = sessions.value.filter((s) => s.project_id === selectedProjectId.value)
  if (selectedSessionId.value && !projectSessions.some((s) => s.id === selectedSessionId.value)) {
    selectedSessionId.value = projectSessions[0]?.id ?? null
  }
}

async function renameProject(projectId: string, name: string) {
  const trimmed = name.trim()
  if (!trimmed) return
  await run('rename project', async () => {
    const updated = await generatedApi.renameProject(projectId, trimmed)
    projects.value = projects.value.map((p) => (p.id === projectId ? { ...p, name: updated.name } : p))
  })
}

async function renameSession(sessionId: string, title: string) {
  const trimmed = title.trim()
  if (!trimmed) return
  await run('rename session', async () => {
    await api.updateSessionTitle(sessionId, trimmed)
    sessions.value = sessions.value.map((s) => (s.id === sessionId ? { ...s, title: trimmed } : s))
  })
}

async function archiveProject(projectId: string) {
  await run('archive project', async () => {
    await generatedApi.archiveProject(projectId)
    await refreshProjectsAndSessions()
  })
}

async function trashProject(projectId: string) {
  await run('move project to trash', async () => {
    await generatedApi.trashProject(projectId)
    await refreshProjectsAndSessions()
  })
}

async function archiveSession(sessionId: string) {
  await run('archive session', async () => {
    await generatedApi.archiveSession(sessionId)
    await refreshProjectsAndSessions()
  })
}

async function trashSession(sessionId: string) {
  await run('move session to trash', async () => {
    await generatedApi.trashSession(sessionId)
    await refreshProjectsAndSessions()
  })
}

// invoke-stream: 5 required + 2 optional, ack optimistic → delta incremental → done persisted
// explicit states: model_not_configured / disconnected / permission_denied / recovering
const streamingText = ref<Map<string, string>>(new Map())
const invokeError = ref<string | null>(null)
const lastCursor = ref<number | null>(null)


async function handleSend(content: string, opts?: { dispatch_mode?: DispatchMode; target_run_id?: string | null; mode_version_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null; agent_mode?: AgentMode; permission_mode?: PermissionLevel }) {
  await run('send message', async () => {
    let sessionId = selectedSessionId.value
    if (!sessionId && selectedProjectId.value) {
      const session = await api.createSession(selectedProjectId.value, 'Tinadec session')
      sessions.value = [session, ...sessions.value]
      selectedSessionId.value = session.id
      sessionId = session.id
      pendingSessionId.value = session.id
    }
    if (!sessionId) {
      throw new Error('Open a project before sending a message.')
    }
    const snapshotContent = content
    draft.value = ''
    invokeError.value = null
    const clientMessageId = newId()
    const dispatchMode: DispatchMode = (opts?.dispatch_mode as DispatchMode) ?? getDispatchPref()
    const modeVersionId = opts?.mode_version_id ?? null
    const targetRunId = opts?.target_run_id ?? null
    const meetingModelOverride = opts?.meeting_model_override ?? null
    const requestedMode = opts?.agent_mode ?? currentMode.value
    const requestedPermission = opts?.permission_mode ?? currentPermission.value
    if (dispatchMode === 'insert' && !targetRunId) throw new Error('插入模式需选择目标 run')
    try {
      messages.value = [...messages.value, { id: `pending-${clientMessageId}`, session_id: sessionId, role: 'user', content: snapshotContent, created_at: new Date().toISOString() } as MessageDto]
      // new interaction path (snake_case)
      const resp = await api.createInteraction(sessionId, {
        content: snapshotContent,
        client_message_id: clientMessageId,
        mode_version_id: modeVersionId,
        // agent_mode 随消息发送以选定 TOML profile；mode_version_id 存在时 Core 优先用它冻结 roster。
        agent_mode: requestedMode,
        permission_mode: requestedPermission,
        dispatch_mode: dispatchMode,
        target_run_id: targetRunId,
        meeting_model_override: meetingModelOverride,
      })
      if (resp.run_id) {
        attachRun(resp.run_id)
        runs.value = [{ id: resp.run_id, status: resp.status || 'planning' }, ...runs.value.filter((run) => run.id !== resp.run_id)]
      }
      if (!resp.run_id && resp.status === 'queued') queuedMessages.value = [...queuedMessages.value, { id: clientMessageId, content: snapshotContent }]
      // optionally still stream via invoke for backwards compat if needed; interaction SSE will arrive via events
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err)
      const code = (err as { code?: unknown }).code
      // Main path only: POST /interactions is the single admission contract
      // (docs/app-core-ui.md §4.1). The legacy invoke-stream / POST messages
      // fallbacks were removed so failures surface visibly instead of
      // silently degrading to a non-durable path.
      if (msg.includes('mode_unavailable') || msg.includes('模式不可用')) invokeError.value = '当前对话模式不可用，请在输入框左下角重新选择模式'
      else if (code === 'context_conflict') {
        // §4.1-4: show revision conflict guidance; user must re-read before resending.
        invokeError.value = '上下文已更新（检测到新的目标修订）。请重新读取当前状态后再发送。'
      } else if (msg.includes('model_not_configured') || msg.includes('No model')) invokeError.value = '模型未配置，请在设置中选择模型后重试'
      else if (msg.includes('permission') || msg.includes('forbidden') || msg.includes('401') || msg.includes('403')) invokeError.value = '权限不足'
      else if (msg.includes('recovering')) invokeError.value = '恢复中，请稍候再试'
      else if (!navigator.onLine || msg.includes('Cannot connect') || msg.includes('Failed to fetch')) invokeError.value = '网络已断开'
      else invokeError.value = msg
      throw err
    }
    if (pendingSessionId.value === sessionId) {
      const title = generateTitle(snapshotContent)
      try {
        await api.updateSessionTitle(sessionId, title)
        const idx = sessions.value.findIndex((s) => s.id === sessionId)
        if (idx !== -1) sessions.value[idx] = { ...sessions.value[idx], title }
      } catch {
        const idx = sessions.value.findIndex((s) => s.id === sessionId)
        if (idx !== -1) sessions.value[idx] = { ...sessions.value[idx], title }
      }
      pendingSessionId.value = null
    }
    await loadMessagesAndApprovals()
  })
}

function dismissQueued(id: string) {
  queuedMessages.value = queuedMessages.value.filter((item) => item.id !== id)
}

function editQueued(id: string) {
  const item = queuedMessages.value.find((q) => q.id === id)
  if (!item) return
  draft.value = item.content
  dismissQueued(id)
}

async function steerQueued(id: string, targetRunId: string) {
  if (!selectedSessionId.value) return
  const item = queuedMessages.value.find((q) => q.id === id)
  if (!item) return
  let sent = false
  await run('steer message', async () => {
    await api.createInteraction(selectedSessionId.value!, {
      content: item.content,
      client_message_id: newId(),
      mode_version_id: null,
      dispatch_mode: 'insert',
      target_run_id: targetRunId,
    })
    sent = true
  })
  if (sent) dismissQueued(id)
}

async function promoteQueued(id: string) {
  if (!selectedSessionId.value) return
  const item = queuedMessages.value.find((q) => q.id === id)
  if (!item) return
  let sent = false
  await run('promote message', async () => {
    await api.createInteraction(selectedSessionId.value!, {
      content: item.content,
      client_message_id: newId(),
      mode_version_id: null,
      agent_mode: currentMode.value,
      dispatch_mode: 'parallel',
      target_run_id: null,
    })
    sent = true
  })
  if (sent) dismissQueued(id)
}

async function requestShellApproval() {
  await run('request approval', async () => {
    const projectPath = currentProject.value?.path
    if (!projectPath) throw new Error('Select a registered project before requesting a shell action.')
    const command = shellCommand.value.trim()
    if (!command) throw new Error('Enter a command before requesting a shell action.')
    // 'shell' is the governed command tool id (same one agent workers use);
    // Core resolves it from the live provider manifest and its frozen schema
    // takes { command, cwd }, not the legacy command_run executable shape.
    const params = {
      command,
      cwd: projectPath,
    }
    const idempotencyKey = await userToolActionIdempotencyKey('desktop:home:shell', {
      project_path: projectPath,
      command,
    })
    const action = await createUserToolActionForPath(projectPath, 'shell', params, idempotencyKey)
    const approval = userToolActionToApproval(action, `Run command: ${command}`, {
      sessionId: selectedSessionId.value,
      cwd: projectPath,
    })
    approvals.value = [approval, ...approvals.value]
  })
}

async function decideApproval(approval: ApprovalDto, decision: 'approved' | 'rejected') {
  await run('decide approval', async () => {
    await api.decideApproval(approval.id, decision)
    await loadMessagesAndApprovals()
  })
}

function recordApproval(approval: ApprovalDto) {
  approvals.value = [approval, ...approvals.value.filter((item) => item.id !== approval.id)]
}

/**
 * Fan-out for terminal widgets. The controller owns the single SSE connection, so
 * agent terminal panels subscribe here instead of opening their own EventSource.
 */
const eventListeners = new Set<(event: EventEnvelope) => void>()
function onEvent(handler: (event: EventEnvelope) => void): () => void {
  eventListeners.add(handler)
  return () => {
    eventListeners.delete(handler)
  }
}

function reconnectEvents() {
  eventSource.value?.close()
  eventSource.value = api.connectEvents(selectedSessionId.value, async (event) => {
    for (const listener of [...eventListeners]) {
      try {
        listener(event)
      } catch {
        // A failing terminal widget must not break the session event pipeline.
      }
    }
    const bySeq = new Map(events.value.map((item) => [item.seq, item]))
    bySeq.set(event.seq, event)
    events.value = [...bySeq.values()].sort((left, right) => left.seq - right.seq).slice(-80)
    if (
      event.type.startsWith('message.') ||
      event.type.startsWith('approval.') ||
      event.type.startsWith('tool.') ||
      event.type.startsWith('run.') ||
      event.type.startsWith('task') ||
      event.type.startsWith('supervision.') ||
      event.type.startsWith('context.') ||
      event.type.startsWith('step.')
    ) {
      await loadMessagesAndApprovals()
    }
  })
}

watch(selectedProjectId, () => {
  void loadSessions()
})

watch(selectedSessionId, () => {
  for (const stream of runStreams.values()) stream.disconnect()
  runStreams.clear()
  syncWorking()
  runText.clear()
  streamingText.value = new Map()
  void loadMessagesAndApprovals()
  reconnectEvents()
  queuedMessages.value = []
})

/** Start the controller's data pipeline (idempotent). */
let started = false
function start() {
  if (started) return
  started = true
  void loadInitial()
  reconnectEvents()
}

export const homeController = {
  // Refs (reactive state)
  projects,
  sessions,
  messages,
  approvals,
  events,
  recentEvents,
  doctor,
  readiness,
  modelSettings,
  orchestration,
  toolExecutions,
  onEvent,
  selectedProjectId,
  selectedSessionId,
  draft,
  modelBaseUrl,
  modelName,
  modelApiKey,
  shellCommand,
  busy,
  eventSource,
  rightRailCollapsed,
  rightRailWidth,
  currentMode,
  currentPermission,
  currentProject,
  currentSession,
  agentActivity,
  agentToolCalls,
  agentThinkingSteps,
  agentStatesMap,
  agentProgressEvents,
  streamingText,
  working,
  lastStreamActivityAt,
  invokeError,
  lastCursor,
  // Methods
  start,
  openProject,
  createSession,
  renameProject,
  renameSession,
  archiveProject,
  trashProject,
  archiveSession,
  trashSession,
  runs,
  queuedMessages,
  activeRuns,
  dismissQueued,
  editQueued,
  steerQueued,
  promoteQueued,
  sendMessage: async (opts?: { dispatch_mode?: DispatchMode; target_run_id?: string | null; mode_version_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null }) => {
    const content = draft.value.trim()
    if (!content) return
    await handleSend(content, opts)
  },
  handleWelcomeSend: (payload: { content: string; agent_mode: AgentMode; permission_mode: PermissionLevel; mode_version_id?: string | null }) => handleSend(payload.content, payload),
  requestShellApproval,
  decideApproval,
  recordApproval,
  loadMessagesAndApprovals,
  updateDraft: (value: string) => { draft.value = value },
  updateMode: (value: AgentMode) => { currentMode.value = value },
  updatePermission: (value: PermissionLevel) => { currentPermission.value = value },
  setSelectedProject: (id: string) => { selectedProjectId.value = id },
  setSelectedSession: (id: string) => { selectedSessionId.value = id },
}
