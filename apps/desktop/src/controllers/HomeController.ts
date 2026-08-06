import { computed, ref, watch, type Ref } from 'vue'
import {
  api,
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
import { useAgentActivity } from '@/composables/useAgentActivity'
import { useNotifications } from '@/composables/useNotifications'
import type { AgentMode, PermissionLevel } from '@/types/mode'

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

const currentProject = computed(() => projects.value.find((p) => p.id === selectedProjectId.value) ?? null)
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

const agentLabel = computed(() => agentActivity.value.activeAgentName ?? null)

const { notify, banner, dismissByKey } = useNotifications()

function generateTitle(content: string): string {
  const trimmed = content.trim()
  if (!trimmed) return 'New chat'
  const firstLine = trimmed.split('\n')[0]
  if (firstLine.length <= 50) return firstLine
  return firstLine.substring(0, 47) + '...'
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
    return
  }
  const [messageList, approvalList, orchestrationSnapshot, toolTimeline] = await Promise.all([
    api.listMessages(selectedSessionId.value),
    api.listApprovals(selectedSessionId.value),
    api.getOrchestrationSnapshot(selectedSessionId.value),
    api.listToolExecutions(selectedSessionId.value, { limit: 12 }),
  ])
  messages.value = messageList
  approvals.value = approvalList
  orchestration.value = orchestrationSnapshot
  toolExecutions.value = toolTimeline
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

async function handleSend(content: string) {
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
    draft.value = ''
    await api.postMessage(sessionId, content)
    if (pendingSessionId.value === sessionId) {
      const title = generateTitle(content)
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

async function requestShellApproval() {
  await run('request approval', async () => {
    const approval = await api.createShellApproval(selectedSessionId.value, shellCommand.value, currentProject.value?.path)
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
  void loadMessagesAndApprovals()
  reconnectEvents()
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
  agentLabel,
  // Methods
  start,
  openProject,
  createSession,
  sendMessage: async () => {
    const content = draft.value.trim()
    if (!content) return
    await handleSend(content)
  },
  handleWelcomeSend: (content: string) => handleSend(content),
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
