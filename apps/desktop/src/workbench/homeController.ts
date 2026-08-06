import { computed, inject, provide, ref, watch, type InjectionKey } from 'vue'
import { useI18n } from 'vue-i18n'
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

export function createHomeWorkbenchController() {
  const { t } = useI18n()
  const { notify, banner, dismissByKey } = useNotifications()

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
  const currentMode = ref<AgentMode>('auto')
  const currentPermission = ref<PermissionLevel>('default')

  const currentProject = computed(
    () => projects.value.find((project) => project.id === selectedProjectId.value) ?? null,
  )
  const currentSession = computed(
    () => sessions.value.find((session) => session.id === selectedSessionId.value) ?? null,
  )
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

  let eventSource: EventSource | null = null
  let started = false
  let preferredSelection: { projectId?: string; projectPath?: string; sessionId?: string } = {}

  function generateTitle(content: string): string {
    const trimmed = content.trim()
    if (!trimmed) return t('chat.newChat')
    const firstLine = trimmed.split('\n')[0]
    return firstLine.length <= 50 ? firstLine : `${firstLine.substring(0, 47)}...`
  }

  async function run(label: string, action: () => Promise<void>): Promise<void> {
    busy.value = true
    try {
      await action()
    } catch (error) {
      notify.error(error, { title: `${label} failed` })
    } finally {
      busy.value = false
    }
  }

  async function loadInitial(): Promise<void> {
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
      selectedProjectId.value = projectList.find((project) =>
        project.id === preferredSelection.projectId || project.path === preferredSelection.projectPath,
      )?.id ?? projectList[0]?.id ?? null
      await loadSessions()
      if (preferredSelection.sessionId && sessions.value.some((session) => session.id === preferredSelection.sessionId)) {
        selectedSessionId.value = preferredSelection.sessionId
      }
      dismissByKey('home-load')
    } catch (error) {
      banner.error({
        key: 'home-load',
        title: t('app.loadFailed'),
        message: t('app.loadFailedMessage'),
        details: error instanceof Error ? error.message : t('app.loadFailed'),
        action: { label: t('app.retry'), run: loadInitial },
      })
    } finally {
      busy.value = false
    }
  }

  async function loadSessions(): Promise<void> {
    if (projects.value.length === 0) {
      sessions.value = []
      selectedSessionId.value = null
      return
    }
    const allSessions = await Promise.all(projects.value.map((project) => api.listSessions(project.id)))
    sessions.value = allSessions.flat()
    if (!selectedProjectId.value) {
      selectedSessionId.value = null
      return
    }
    const projectSessions = sessions.value.filter(
      (session) => session.project_id === selectedProjectId.value,
    )
    if (!projectSessions.some((session) => session.id === selectedSessionId.value)) {
      selectedSessionId.value = projectSessions[0]?.id ?? null
    }
  }

  async function loadMessagesAndApprovals(): Promise<void> {
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

  async function openProject(): Promise<void> {
    await run('open project', async () => {
      const path = await window.tinadec.openProjectDialog()
      if (!path) return
      const project = await api.createProject(basenameFromPath(path), path)
      projects.value = [project, ...projects.value.filter((item) => item.id !== project.id)]
      selectedProjectId.value = project.id
    })
  }

  async function createSession(projectId: string): Promise<void> {
    if (pendingSessionId.value) {
      const existing = sessions.value.find((session) => session.id === pendingSessionId.value)
      if (existing?.project_id === projectId) {
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

  async function sendMessage(): Promise<void> {
    const content = draft.value.trim()
    if (content) await handleSend(content)
  }

  async function handleSend(content: string): Promise<void> {
    await run('send message', async () => {
      let sessionId = selectedSessionId.value
      if (!sessionId && selectedProjectId.value) {
        const session = await api.createSession(selectedProjectId.value, 'Tinadec session')
        sessions.value = [session, ...sessions.value]
        selectedSessionId.value = session.id
        sessionId = session.id
        pendingSessionId.value = session.id
      }
      if (!sessionId) throw new Error('Open a project before sending a message.')

      draft.value = ''
      await api.postMessage(sessionId, content)
      if (pendingSessionId.value === sessionId) {
        const title = generateTitle(content)
        try {
          await api.updateSessionTitle(sessionId, title)
        } catch {
          // The local title still reflects the user's first message when the optional update endpoint is unavailable.
        } finally {
          const index = sessions.value.findIndex((session) => session.id === sessionId)
          if (index >= 0) sessions.value[index] = { ...sessions.value[index], title }
        }
        pendingSessionId.value = null
      }
      await loadMessagesAndApprovals()
    })
  }

  async function requestShellApproval(): Promise<void> {
    await run('request approval', async () => {
      const approval = await api.createShellApproval(
        selectedSessionId.value,
        shellCommand.value,
        currentProject.value?.path,
      )
      approvals.value = [approval, ...approvals.value]
    })
  }

  async function decideApproval(
    approval: ApprovalDto,
    decision: 'approved' | 'rejected',
  ): Promise<void> {
    await run('decide approval', async () => {
      await api.decideApproval(approval.id, decision)
      await loadMessagesAndApprovals()
    })
  }

  function recordApproval(approval: ApprovalDto): void {
    approvals.value = [approval, ...approvals.value.filter((item) => item.id !== approval.id)]
  }

  function reconnectEvents(): void {
    eventSource?.close()
    if (!started) return
    eventSource = api.connectEvents(selectedSessionId.value, async (event) => {
      const bySequence = new Map(events.value.map((item) => [item.seq, item]))
      bySequence.set(event.seq, event)
      events.value = [...bySequence.values()]
        .sort((left, right) => left.seq - right.seq)
        .slice(-80)
      if (
        event.type.startsWith('message.')
        || event.type.startsWith('approval.')
        || event.type.startsWith('tool.')
        || event.type.startsWith('run.')
        || event.type.startsWith('task')
        || event.type.startsWith('supervision.')
        || event.type.startsWith('context.')
        || event.type.startsWith('step.')
      ) {
        await loadMessagesAndApprovals()
      }
    })
  }

  watch(selectedProjectId, () => {
    if (started) void loadSessions()
  })
  watch(selectedSessionId, () => {
    if (!started) return
    void loadMessagesAndApprovals()
    reconnectEvents()
  })

  function start(): void {
    if (started) return
    started = true
    void loadInitial()
    reconnectEvents()
  }

  function configureInitialSelection(state: Record<string, unknown>): void {
    if (started) return
    preferredSelection = {
      projectId: typeof state.projectId === 'string' ? state.projectId : undefined,
      projectPath: typeof state.projectPath === 'string' ? state.projectPath : undefined,
      sessionId: typeof state.sessionId === 'string' ? state.sessionId : undefined,
    }
  }

  function stop(): void {
    started = false
    eventSource?.close()
    eventSource = null
  }

  return {
    projects,
    sessions,
    messages,
    approvals,
    doctor,
    readiness,
    orchestration,
    toolExecutions,
    selectedProjectId,
    selectedSessionId,
    draft,
    modelName,
    shellCommand,
    busy,
    currentMode,
    currentPermission,
    currentProject,
    currentSession,
    recentEvents,
    agentActivity,
    agentToolCalls,
    agentThinkingSteps,
    agentStatesMap,
    agentProgressEvents,
    agentLabel,
    configureInitialSelection,
    start,
    stop,
    openProject,
    createSession,
    sendMessage,
    handleSend,
    requestShellApproval,
    decideApproval,
    recordApproval,
  }
}

export type HomeWorkbenchController = ReturnType<typeof createHomeWorkbenchController>

const HOME_WORKBENCH_KEY: InjectionKey<HomeWorkbenchController> = Symbol('home-workbench')

export function provideHomeWorkbench(controller: HomeWorkbenchController): void {
  provide(HOME_WORKBENCH_KEY, controller)
}

export function useHomeWorkbench(): HomeWorkbenchController {
  const controller = inject(HOME_WORKBENCH_KEY)
  if (!controller) throw new Error('Home workbench controller was not provided.')
  return controller
}
