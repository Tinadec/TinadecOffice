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
const modelName = ref('gpt-5.4-mini')
const modelApiKey = ref('')
const shellCommand = ref('npm test')
const busy = ref(false)
const eventSource = ref<EventSource | null>(null)
const rightRailCollapsed = ref(false)
const rightRailWidth = ref(420)
const currentMode = ref<AgentMode>('auto')
const currentPermission = ref<PermissionLevel>('default')
const runs = ref<Array<{ id: string; status: string }>>([])
const queuedMessages = ref<Array<{ id: string; content: string }>>([])
const runStreams = new Map<string, RunStreamHandle>()
const runText = new Map<string, string>()

const currentProject = computed(() => projects.value.find((p) => p.id === selectedProjectId.value) ?? null)
const activeRuns = computed(() => runs.value.filter((r) => ['running', 'ready', 'pending', 'queued'].includes(r.status)))
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
    const [projectList, settings, report, readinessReceipt] = await Promise.all([
      api.listProjects(),
      api.getModelSettings(),
      api.doctor(),
      api.readiness(),
    ])
    projects.value = projectList
    modelSettings.value = settings
    doctor.value = report
    readiness.value = readinessReceipt
    modelBaseUrl.value = settings.base_url
    modelName.value = settings.model
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
  messages.value = messageList
  approvals.value = approvalList
  orchestration.value = orchestrationSnapshot
  toolExecutions.value = toolTimeline
  runs.value = (Array.isArray(runList) ? runList : []).map((r) => ({ id: String((r as Record<string, unknown>).id), status: String((r as Record<string, unknown>).status ?? '') }))
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
      }
    },
    onError: (error) => {
      if (!navigator.onLine) invokeError.value = '网络已断开'
      else if (error.message) invokeError.value = error.message
    },
  })
  runStreams.set(runId, handle)
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
        agent_mode: modeVersionId ? null : requestedMode,
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
      if (code === 'context_conflict') {
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
    const windows = typeof navigator !== 'undefined' && /windows/i.test(navigator.userAgent)
    const params = {
      executable: windows ? 'cmd.exe' : '/bin/sh',
      arguments: windows ? ['/d', '/c', command] : ['-lc', command],
      working_directory: projectPath,
    }
    const idempotencyKey = await userToolActionIdempotencyKey('desktop:home:shell', {
      project_path: projectPath,
      command,
    })
    const action = await createUserToolActionForPath(projectPath, 'command_run', params, idempotencyKey)
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

function reconnectEvents() {
  eventSource.value?.close()
  eventSource.value = api.connectEvents(selectedSessionId.value, async (event) => {
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
  invokeError,
  lastCursor,
  // Methods
  start,
  openProject,
  createSession,
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
  handleWelcomeSend: (payload: { content: string; agent_mode: AgentMode; permission_mode: PermissionLevel }) => handleSend(payload.content, payload),
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
