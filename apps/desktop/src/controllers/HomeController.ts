import i18n from '@/i18n'
import { ApiError, errorCategoryMessage } from '@/lib/apiError'
import { useHostAccess } from '@/lib/hostAccess'
import { readSessionRoster } from '@/lib/sessionRoster'
import { toErrorState } from '@/composables/useErrorState'
import { computed, effectScope, ref, watch, type Ref } from 'vue'
import { revealWorkspace } from '@/composables/useWorkspaceList'
import {
  api,
  createUserToolActionForPath,
  type ApprovalDto,
  type ApprovalRuleDto,
  type CreateApprovalRuleInput,
  type EventEnvelope,
  type MessageDto,
  type ModelSettingsDto,
  type OrchestrationSnapshotDto,
  type ProjectDto,
  type RuntimeReadinessReceiptDto,
  type SessionDto,
  type ToolExecutionTimelineItemDto,
  type ToolDescriptorDto,
} from '@/api'
import { basenameFromPath } from '@/format'
import { getDispatchPref } from '@/lib/dispatchPref'
import { attachmentsForSend, pendingAttachments, readyAttachmentCount, settleSentAttachments } from '@/lib/pendingAttachments'
import { followSession, subscribeToSessionEvents, suspendFollowingSession } from '@/lib/sessionEventBus'
import { isAbortError } from '@/lib/isAbortError'
import { useAgentActivity } from '@/composables/useAgentActivity'
import { useConversationFeed } from '@/composables/useConversationFeed'
import { projectRunReply } from '@/lib/runReply'
import { setErrorRecoveryHandlers, useNotifications } from '@/composables/useNotifications'
import type { PermissionLevel } from '@/types/mode'
// generated client is canonical; api.ts stays as compat alias (see bottom of api.ts)
import type { ComposerSubmitOptions, DispatchMode, MeetingModelOverrideDto, SessionSettingsUpdate, SpaceOptionsDto } from '@/api'
import { applyComposerSettings, copyComposerSettings, defaultSpaceOptions, newComposerSettings, type ComposerSettings } from '@/lib/composerSettings'
import { userToolActionIdempotencyKey, userToolActionToApproval } from '@/userToolAction'
import { createRunStream, type RunStreamHandle } from '@/lib/runStream'
import { generatedApi } from '@/generated/client'
import { useRunStore } from '@/stores/run'
import { registerProjectStorage, projectStorageId, setSelectedStorage, selectedStorage, selectedStorageId, selectionIdentity, selectionKey, scopedApi } from '@/lib/storageScope'
import type { SessionTransferDto } from '@/settings/storage'
import type { TaskHandle } from '@/composables/useNotifications'
import type { WorkspaceInput, WorkspaceDefinition, WorkspaceLoadState } from '@/lib/workspaces'

// ---------------------------------------------------------------------------
// HomeController — the single domain controller for the Home page.
//
// Module-level singleton that owns ALL Home data/state, so every Home card
// (nav, chat, git, approval, ...) reads the same sources and shares one SSE
// connection. This is the direct migration of HomePage.vue's script logic.
// ---------------------------------------------------------------------------

const t = i18n.global.t
const projects = ref<ProjectDto[]>([])
const workspaceEditor = ref<{ open: boolean; projectKey: string | null }>({ open: false, projectKey: null })
const workspaceLoadStates = ref<Record<string, WorkspaceLoadState>>({})
const sessions = ref<SessionDto[]>([])
const sessionTransfers = ref<Record<string, SessionTransferDto>>({})
type SessionView = 'flat' | 'space'
const viewMode = ref<SessionView>('flat')
const visibleSessions = computed(() => sessions.value.filter(s => (s.view_mode ?? 'flat') === viewMode.value))
const lastSessionByView: Record<SessionView, string | null> = { flat: null, space: null }
const messages = ref<MessageDto[]>([])
const approvals = ref<ApprovalDto[]>([])
const approvalRules = ref<ApprovalRuleDto[]>([])
const events = ref<EventEnvelope[]>([])
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
// loadInitial owns the first roster read. The selectedProjectId watcher must not
// start a second read and abort that first one before the initial load can finish.
let suppressProjectSessionsReload = false
// 模式身份只剩「已发布的 ModeVersion」：六值 agent_mode 词表已从契约删除，
// 因此不再有本地存储的"当前模式"——选择跟着会话走（session.mode_version_id）。
const newSessionSettings = ref<Record<SessionView, ComposerSettings>>({ flat: newComposerSettings('flat'), space: newComposerSettings('space') })
const settingsWrites = new Map<string, Promise<boolean>>()
const settingsPending = ref<Record<string, number>>({})
const settingsErrors = ref<Record<string, string | null>>({})
/**
 * The session's runs. `trigger_message_id` and the timestamps are what pair a run with the
 * user message that started it in the flat feed; rows added on admission know only id and
 * status until the next run-list read fills the rest.
 */
type SessionRun = { id: string; status: string; trigger_message_id?: string | null; created_at?: string | null; updated_at?: string | null }
const runs = ref<SessionRun[]>([])
/**
 * Messages waiting in the session's queue. `interactionId` is set when Core holds the message
 * (queued delivery never runs beside an unfinished run); the card then leaves when Core admits,
 * rejects or dequeues it, and acting on it takes it out of Core's queue first.
 */
const queuedMessages = ref<Array<{ id: string; content: string; interactionId?: string; permission_mode?: PermissionLevel; mode_version_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null; clear_meeting_model_override?: boolean; space_options?: SpaceOptionsDto | null; attachment_ids?: string[] }>>([])
const runStreams = new Map<string, RunStreamHandle>()
let runStreamsSuspended = false
const runText = new Map<string, string>()
const provisionalReplies = new Set<string>()
// 运行指示（问题 3 修复）：是否有活跃的 run 流。runStreams 是非响应式 Map，computed
// 无法追踪，故用显式 ref 并在每次 set/delete/clear 后 syncWorking()。用流数量而非
// activeRuns.length：activeRuns 含 lane_waiting/gate_review 等长驻态，会让指示永久
// 亮起；流随 done/error 的 disconnect+delete 天然归零。
const working = ref(false)
// 最近一次 run stream 活动时间（ack/delta/heartbeat 等任意 chunk）：供 UI 区分
// 「链路活着但暂无输出」与「链路已断」。
const lastStreamActivityAt = ref<number | null>(null)
function syncWorking() { working.value = runStreams.size > 0 }

const currentProject = computed(() => projects.value.find((p) => p.id === selectedProjectId.value && (!p.storage_id || p.storage_id === selectedStorage.value)) ?? null)
// 活动运行 = 非终态且不驻留人工决策（对齐 Core CountActiveRunsAsync 的口径，
// 词表以共享 12 态为准，不再使用自造的 running/ready/pending/queued）。
const activeRuns = computed(() => runs.value.filter((r) => !['completed', 'failed', 'cancelled', 'awaiting_user'].includes(r.status)))
const currentSession = computed(() => sessions.value.find((s) => s.id === selectedSessionId.value && (!s.storage_id || s.storage_id === selectedStorage.value)) ?? null)
function settingsFor(session: SessionDto | null, view: SessionView): ComposerSettings {
  return session ? {
    mode_version_id: session.mode_version_id ?? null,
    permission_mode: (session.permission_mode ?? 'default') as PermissionLevel,
    meeting_model_override: session.meeting_model_override ?? null,
    space_options: view === 'space' ? session.space_options ?? defaultSpaceOptions() : null,
  } : newSessionSettings.value[view]
}
const composerSettings = computed(() => settingsFor(currentSession.value, viewMode.value))
const currentPermission = computed(() => composerSettings.value.permission_mode)
const settingsKey = computed(() => `${selectedStorage.value}::${selectedSessionId.value ?? `draft:${viewMode.value}`}`)
const settingsSaving = computed(() => (settingsPending.value[settingsKey.value] ?? 0) > 0)
const settingsError = computed(() => settingsErrors.value[settingsKey.value] ?? null)

function sessionWriteKey(sessionId: string, storageId = selectedStorageId()) { return `${storageId}::${selectionIdentity(sessionId).id}` }
function sessionInScope(session: SessionDto, sessionId: string, storageId: string) { return session.id === sessionId && (!session.storage_id || session.storage_id === storageId) }
function acceptSessionReceipt(updated: SessionDto, storageId: string) {
  sessions.value = sessions.value.map(session => !sessionInScope(session, updated.id, storageId) || updated.settings_revision < session.settings_revision
    ? session : { ...session, ...updated })
}

async function saveSessionTitle(sessionId: string, title: string): Promise<void> {
  const identity = selectionIdentity(sessionId)
  const storageId = identity.storageId ?? selectedStorageId()
  const key = sessionWriteKey(identity.id, storageId)
  const boundApi = scopedApi(api, () => storageId)
  const previous = settingsWrites.get(key)
  const request = (async () => {
    if (previous) await previous
    acceptSessionReceipt(await boundApi.updateSessionTitle(identity.id, title), storageId)
  })()
  // A naming failure does not invalidate the task's chosen runtime settings.
  const settled = request.then(() => true, () => true)
  settingsWrites.set(key, settled)
  try { await request } finally { if (settingsWrites.get(key) === settled) settingsWrites.delete(key) }
}

/** Serial writes keep rapid selections ordered; revisions protect against other windows. */
async function updateComposerSettings(patch: SessionSettingsUpdate): Promise<boolean> {
  const sessionId = selectedSessionId.value
  const storageId = selectedStorageId()
  const boundApi = scopedApi(api, () => storageId)
  const view = viewMode.value
  const key = `${storageId}::${sessionId ?? `draft:${view}`}`
  const frozenPatch: SessionSettingsUpdate = {
    ...patch,
    ...(patch.space_options ? { space_options: { ...patch.space_options } } : {}),
    ...(patch.meeting_model_override ? { meeting_model_override: { ...patch.meeting_model_override } } : {}),
  }
  settingsErrors.value = { ...settingsErrors.value, [key]: null }
  if (!sessionId) {
    newSessionSettings.value = { ...newSessionSettings.value, [view]: applyComposerSettings(newSessionSettings.value[view], frozenPatch) }
    return true
  }
  settingsPending.value = { ...settingsPending.value, [key]: (settingsPending.value[key] ?? 0) + 1 }
  const previous = settingsWrites.get(key)
  const writing = (async () => {
    if (previous) await previous
    try {
      const session = sessions.value.find(s => sessionInScope(s, sessionId, storageId))
      sessionListRead++
      sessionListAbort?.abort()
      const updated = await boundApi.updateSessionSettings(sessionId, { ...frozenPatch, expected_settings_revision: session?.settings_revision ?? 0 })
      acceptSessionReceipt(updated, storageId)
      settingsErrors.value = { ...settingsErrors.value, [key]: null }
      return true
    } catch (error) {
      settingsErrors.value = { ...settingsErrors.value, [key]: error instanceof Error ? error.message : String(error) }
      if ((error as { code?: string }).code === 'session_settings_conflict') {
        try {
          const latest = (await boundApi.listSessions()).find(s => sessionInScope(s, sessionId, storageId))
          if (latest) sessions.value = sessions.value.map(s => sessionInScope(s, sessionId, storageId) ? latest : s)
        } catch { /* Preserve the original actionable save error. */ }
      }
      return false
    } finally {
      settingsPending.value = { ...settingsPending.value, [key]: Math.max(0, (settingsPending.value[key] ?? 1) - 1) }
    }
  })()
  settingsWrites.set(key, writing)
  const result = await writing
  if (settingsWrites.get(key) === writing) settingsWrites.delete(key)
  return result
}

let composerSessionCreation: { view: SessionView; projectId: string | null; storageId: string; promise: Promise<string> } | null = null
/** Attachments can create a draft session before the first message without losing its settings. */
async function ensureComposerSession(): Promise<string> {
  if (selectedSessionId.value) return selectedSessionId.value
  const view = viewMode.value
  const projectId = selectedProjectId.value
  const storageId = selectedStorageId()
  const boundApi = scopedApi(api, () => storageId)
  if (composerSessionCreation?.view === view && composerSessionCreation.projectId === projectId && composerSessionCreation.storageId === storageId) return composerSessionCreation.promise
  const settings = copyComposerSettings(composerSettings.value)
  const key = `${storageId}::draft:${view}`
  settingsPending.value = { ...settingsPending.value, [key]: (settingsPending.value[key] ?? 0) + 1 }
  const promise = (async () => {
    sessionListRead++
    sessionListAbort?.abort()
    const session = await boundApi.createSession(projectId, t('chat.title'), settings.mode_version_id, view, settings)
    sessions.value = [session, ...sessions.value.filter(s => !sessionInScope(s, session.id, storageId))]
    if (viewMode.value === view && selectedProjectId.value === projectId && selectedStorageId() === storageId && !selectedSessionId.value) {
      selectedSessionId.value = session.id
      pendingSessionId.value = session.id
    }
    return session.id
  })()
  composerSessionCreation = { view, projectId, storageId, promise }
  try { return await promise } finally {
    settingsPending.value = { ...settingsPending.value, [key]: Math.max(0, (settingsPending.value[key] ?? 1) - 1) }
    if (composerSessionCreation?.promise === promise) composerSessionCreation = null
  }
}
const recentEvents = computed(() => events.value.slice(-8).reverse())

const sessionIdRef = computed(() => currentSession.value?.id ?? null)
const {
  activity: agentActivity,
  toolCalls: agentToolCalls,
  thinkingSteps: agentThinkingSteps,
  turnActivities: agentTurnActivities,
  agentStates: agentStatesMap,
  progressEvents: agentProgressEvents,
} = useAgentActivity(sessionIdRef, orchestration)

// The flat conversation feed's event-sourced model, on the window's one session stream.
// Window lifetime, like the controller itself; a detached scope keeps its watchers owned.
const conversationFeed = effectScope(true).run(() => useConversationFeed(sessionIdRef))!

const { notify, banner, dismissByKey } = useNotifications()

function generateTitle(content: string, attachmentNames: readonly string[] = []): string {
  const trimmed = content.trim()
  if (!trimmed) return attachmentNames[0] ?? t('home.newChat')
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
    notify.error(err, { title: t('home.actionFailed', { action: label }) })
  } finally {
    busy.value = false
  }
}

let initialRead = 0
let contextRestored = false
async function loadInitial() {
  const read = ++initialRead
  busy.value = true
  const failure = (reason: unknown, key: string, title: string) => {
    if (read !== initialRead || isAbortError(reason)) return
    if (reason instanceof ApiError && (reason.code === 'desktop_restart_required' || reason.code === 'host_bridge_unavailable')) {
      dismissByKey(key)
      return // The shared host banner owns recovery; these reads never reached the backend.
    }
    const error = toErrorState(reason, t('home.loadFailed'))
    banner.error({ key, title, message: error.message, details: [error.details, error.traceId ? 'trace_id: ' + error.traceId : ''].filter(Boolean).join('\n'), action: { label: t('common.retry'), run: () => loadInitial() } })
  }
  try {
    await Promise.allSettled([
      api.listProjects().then(async projectList => {
        if (read !== initialRead) return
        projects.value = projectList
        const restoring = !contextRestored
        if (restoring) suppressProjectSessionsReload = true
        try {
          if (restoring) {
            const saved = localStorage.getItem('tinadec.workspace.context.v1')
            const restored = projectList.find(project => selectionKey(project) === saved)
            setSelectedStorage(restored?.storage_id ?? 'user')
            selectedProjectId.value = restored?.id ?? null
            contextRestored = true
          }
          await loadSessions()
        } finally { if (restoring) suppressProjectSessionsReload = false }
        if (read === initialRead) dismissByKey('home-load')
      }).catch(reason => failure(reason, 'home-load', t('home.workspacesLoadFailed'))),
      api.readiness().then(receipt => {
        if (read !== initialRead) return
        readiness.value = receipt
        const items = (receipt as { items?: Array<{ id: string; data?: { model?: string; base_url?: string } }> }).items ?? []
        const route = items.find(item => item.id === 'model_route')?.data
        if (route?.model) modelName.value = route.model
        if (route?.base_url) modelBaseUrl.value = route.base_url
        dismissByKey('home-readiness')
      }).catch(reason => failure(reason, 'home-readiness', t('home.readinessLoadFailed'))),
    ])
  } finally { if (read === initialRead) busy.value = false }
}

let sessionListRead = 0
let sessionListAbort: AbortController | null = null
const workspaceRetryAborts = new Map<string, AbortController>()

async function loadSessions() {
  // Unfiltered listing covers both project-bound sessions and free conversations
  // (sessions without a project), so the sidebar stays correct with zero projects.
  const read = ++sessionListRead
  for (const retry of workspaceRetryAborts.values()) retry.abort()
  workspaceRetryAborts.clear()
  sessionListAbort?.abort()
  const loadAbort = new AbortController()
  sessionListAbort = loadAbort
  try {
    const scopes = [{ storageId: 'user', projectId: undefined as string | undefined, key: 'user::free' },
      ...projects.value.map(project => ({ storageId: project.storage_id ?? projectStorageId(project.id), projectId: project.id, key: selectionKey(project) }))]
    workspaceLoadStates.value = Object.fromEntries(scopes.map(scope => [scope.key, { status: 'loading' as const }]))
    const result = await readSessionRoster({
      sources: scopes, previous: sessions.value, signal: loadAbort.signal,
      read: (scope, signal) => api.listSessions(scope.projectId, signal, scope.storageId),
    })
    if (read !== sessionListRead || loadAbort.signal.aborted) return
    const states: Record<string, WorkspaceLoadState> = Object.fromEntries(scopes.map(scope => [scope.key, { status: 'ready' as const }]))
    for (const failure of result.failures) states[failure.source.key!] = { status: 'error', message: failure.error.message, error: failure.error }
    sessions.value = result.rows
    workspaceLoadStates.value = states
    if (!selectedProjectId.value) {
      if (selectedSessionId.value && !visibleSessions.value.find((s) => s.id === selectedSessionId.value)) {
        selectedSessionId.value = null
      }
      return
    }
    const projectSessions = visibleSessions.value.filter((s) => (s.project_id ?? null) === selectedProjectId.value && (!s.storage_id || s.storage_id === selectedStorageId()))
    if (!projectSessions.find((s) => s.id === selectedSessionId.value)) {
      selectedSessionId.value = null
    }
  } catch (error) {
    if (!isAbortError(error)) throw error
  } finally {
    if (sessionListAbort === loadAbort) sessionListAbort = null
  }
}

/**
 * Removes a registered workspace from the host registry. This is the only action that
 * works when the recorded source folder no longer exists: archive/trash need the scope's
 * database, while unregister is a host-level operation. Stored data and sources are kept.
 */
async function unregisterWorkspace(projectKey: string) {
  const project = projects.value.find(row => selectionKey(row) === projectKey)
  if (!project) return
  const storageId = project.storage_id ?? projectStorageId(project.id)
  if (!storageId || storageId === 'user') return
  await run(t('home.action.unregisterWorkspace'), async () => {
    if (!window.tinadec?.storageAction) throw new Error(t('home.unregisterNeedsHost'))
    await window.tinadec.storageAction(storageId, 'unregister')
    if (selectedStorageId() === storageId) setSelectedStorage('user')
    revealWorkspace('user::free')
    await refreshProjectsAndSessions()
  })
}

/**
 * One place where a server-classified error becomes something a person can click. Registered
 * once at controller setup; the notification layer looks these up by the action kind the server
 * named, so no call site has to re-implement recovery.
 */
function installErrorRecovery(): void {
  setErrorRecoveryHandlers({
    // Retry refreshes the roster the failing request belonged to; reload is the same
    // read-only refresh, so both land in one implementation.
    retry: async error => {
      if (error?.code?.startsWith('host_') || error?.code === 'desktop_host_required') {
        const status = await window.tinadec.retryHostConnection()
        if (status.state === 'ready') await loadInitial()
      } else await refreshProjectsAndSessions()
    },
    reload: () => refreshProjectsAndSessions(),
    unregister_workspace: async (storageId?: string) => {
      const project = projects.value.find(row => (row.storage_id ?? projectStorageId(row.id)) === storageId)
      if (!project) return
      const approved = await useNotifications().confirm({ title: t('home.unregisterTitle'), message: t('home.unregisterMessage'), confirmLabel: t('home.unregisterConfirm'), destructive: true })
      if (approved) await unregisterWorkspace(selectionKey(project))
    },
  })
}

async function retryWorkspace(key: string) {
  const project = projects.value.find(row => selectionKey(row) === key)
  if (!project && key !== 'user::free') return
  const storage = project?.storage_id ?? (project ? projectStorageId(project.id) : 'user')
  const read = sessionListRead
  workspaceRetryAborts.get(key)?.abort()
  const retry = new AbortController()
  workspaceRetryAborts.set(key, retry)
  workspaceLoadStates.value = { ...workspaceLoadStates.value, [key]: { status: 'loading' } }
  try {
    const rows = await api.listSessions(project?.id, retry.signal, storage)
    let recovered: ProjectDto | null = null
    if (project?.availability === 'error') {
      const lists = await Promise.all(['active', 'archived', 'trashed'].map(lifecycleStatus => api.listProjects({ storageId: storage, signal: retry.signal, lifecycleStatus: lifecycleStatus as 'active' | 'archived' | 'trashed' })))
      recovered = lists.flat().find(row => selectionKey(row) === key && row.availability !== 'error' && row.lifecycle_status != null) ?? null
      if (!recovered) {
        const unavailable = lists.flat().find(row => selectionKey(row) === key && row.availability === 'error') ?? project
        throw new ApiError(unavailable.availability_error ?? t('home.workspaceStillUnreadable'), 409, { ...unavailable, code: unavailable.availability_code ?? 'storage_scope_unavailable' }, { storageId: storage })
      }
    }
    if (read !== sessionListRead || retry.signal.aborted) return
    if (recovered && recovered.lifecycle_status !== 'active') {
      projects.value = projects.value.filter(row => selectionKey(row) !== key)
      sessions.value = sessions.value.filter(row => (row.storage_id ?? 'user') !== storage || row.project_id !== project!.id)
      if (selectedStorageId() === storage && selectedProjectId.value === project!.id) startNewConversation(null)
      const states = { ...workspaceLoadStates.value }; delete states[key]; workspaceLoadStates.value = states
      return
    }
    if (recovered) projects.value = projects.value.map(row => selectionKey(row) === key ? recovered! : row)
    sessions.value = [...sessions.value.filter(row => (row.storage_id ?? 'user') !== storage || (row.project_id ?? null) !== (project?.id ?? null)), ...rows]
    workspaceLoadStates.value = { ...workspaceLoadStates.value, [key]: { status: 'ready' } }
  } catch (reason) {
    if (!retry.signal.aborted && read === sessionListRead && !isAbortError(reason)) {
      const error = toErrorState(reason, t('home.workspaceReadFailed'))
      workspaceLoadStates.value = { ...workspaceLoadStates.value, [key]: { status: 'error', message: error.message, error } }
    }
  } finally { if (workspaceRetryAborts.get(key) === retry) workspaceRetryAborts.delete(key) }
}

let sessionRead = 0
let sessionLoadAbort: AbortController | null = null
/**
 * True from a session switch until that session's transcript has loaded, when the switch
 * left nothing to show. The flat feed shows a skeleton instead of flashing the welcome
 * screen (and bouncing the composer to the centre and back) for a session that has history.
 */
const transcriptLoading = ref(false)

async function loadMessagesAndApprovals() {
  const read = sessionRead + 1
  try {
    await readMessagesAndApprovals()
  } finally {
    // Only the newest read may end the loading state; an older one finishing late must not.
    if (sessionRead === read) transcriptLoading.value = false
  }
}

async function readMessagesAndApprovals() {
  const read = ++sessionRead
  sessionLoadAbort?.abort()
  const loadAbort = new AbortController()
  sessionLoadAbort = loadAbort
  const { signal } = loadAbort
  const session = selectedSessionId.value
  const storage = selectedStorageId()
  if (!canAccessBackend.value) { sessionLoadAbort = null; return }
  if (!selectedSessionId.value) {
    sessionLoadAbort = null
    messages.value = []
    approvals.value = []
    approvalRules.value = []
    orchestration.value = null
    toolExecutions.value = []
    runs.value = []
    return
  }
  const loaded = await Promise.all([
    api.listMessages(session!, signal),
    api.listApprovals(session!, undefined, signal),
    api.listApprovalRules(session!, signal).catch(() => [] as ApprovalRuleDto[]),
    api.getOrchestrationSnapshot(session!, signal),
    // No limit: the tool stats and timeline panels count the whole session. Core ignored
    // `limit` until it gained run/limit filtering; passing 12 now would cap their totals.
    api.listToolExecutions(session!, {}, signal),
    // An unavailable roster is not an empty roster: keep suspended stream cursors.
    api.listRuns(session!, signal),
  ]).catch((error) => {
    if (isAbortError(error) || session !== selectedSessionId.value || storage !== selectedStorageId() || read !== sessionRead) return null
    throw error
  }).finally(() => { if (sessionLoadAbort === loadAbort) sessionLoadAbort = null })
  if (!loaded) return
  const [messageList, approvalList, ruleList, orchestrationSnapshot, toolTimeline, runList] = loaded
  if (session !== selectedSessionId.value || storage !== selectedStorageId() || read !== sessionRead) return
  // Keep optimistic pending sends until the backend echoes them: the
  // session-select reload races the first POST (new session has no messages
  // yet), and wiping the optimistic append bounces the composer back to the
  // hero position mid-dock instead of one immersive sink.
  const pendingEcho = messages.value.filter(
    (m) => m.id.startsWith('pending-') && !messageList.some((b) => b.role === 'user' && b.content === m.content),
  )
  messages.value = pendingEcho.length ? [...messageList, ...pendingEcho] : messageList
  approvals.value = approvalList
  approvalRules.value = ruleList
  orchestration.value = orchestrationSnapshot
  toolExecutions.value = toolTimeline
  runs.value = (Array.isArray(runList) ? runList : []).map((r) => ({
    id: String(r.id),
    status: String(r.status ?? ''),
    trigger_message_id: r.trigger_message_id ?? null,
    created_at: r.created_at ?? null,
    updated_at: r.updated_at ?? null,
  }))
  // 状态源统一（问题 3 修复）：ChatHeader 的 run-pills 读 Pinia runStore.runs，而
  // runStore.fetchRuns 此前只在无入口的 WorkbenchPage 调用 → Home 页 pills 恒空。
  // 把 Home 已拉取的 run 列表（含 session_id，WorkbenchPage.control 依赖）同步进 store。
  // HomeController 是模块级单例，可能早于 Pinia 安装被求值，故惰性获取 + 兜底。
  try {
    useRunStore().runs = (Array.isArray(runList) ? runList : []) as never
  } catch { /* Pinia 尚未安装：pills 退回空态，不阻断聊天 */ }
  attachActiveRuns()
  if (sessionLoadAbort === loadAbort) sessionLoadAbort = null
}


function attachRun(runId: string) {
  if (!canAccessBackend.value) return
  const existing = runStreams.get(runId)
  if (existing) {
    if (existing.status.value === 'closed' || existing.status.value === 'error') existing.connect(existing.lastSeq.value)
    return
  }
  const session = selectedSessionId.value
  const storage = selectedStorageId()
  const handle = createRunStream({
    storageId: storage,
    runId,
    onActivity: (chunk) => {
      if (session !== selectedSessionId.value || storage !== selectedStorageId()) return
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
      if (session !== selectedSessionId.value || storage !== selectedStorageId()) return
      const reply = projectRunReply({ text: runText.get(runId) ?? '', provisional: provisionalReplies.has(runId) }, chunk)
      if (reply) {
        if (reply.provisional) provisionalReplies.add(runId)
        else provisionalReplies.delete(runId)
        runText.set(runId, reply.text)
        streamingText.value = new Map(streamingText.value).set(runId, reply.text)
        return
      }
      if (chunk.kind === 'done' || chunk.kind === 'error') {
        provisionalReplies.delete(runId)
        if (chunk.kind === 'error') {
          const payload = chunk.payload as Record<string, unknown>
          const message = errorCategoryMessage(payload.error_category) ?? payload.safe_error_message ?? payload.message ?? payload.error_category
          invokeError.value = typeof message === 'string' ? message : t('activity.runFailed')
        }
        void loadMessagesAndApprovals().catch(error => { if (!isAbortError(error)) notify.error(error, { title: t('home.sessionLoadFailed') }) })
        runStreams.get(runId)?.disconnect()
        runStreams.delete(runId)
        syncWorking()
      }
    },
    onError: (error) => {
      if (!navigator.onLine) invokeError.value = t('home.sendError.offline')
      else if (error.message) invokeError.value = error.message
    },
  })
  runStreams.set(runId, handle)
  syncWorking()
  handle.connect()
}

function attachActiveRuns() {
  if (!selectedSessionId.value || !canAccessBackend.value) return
  if (runStreamsSuspended) {
    const active = new Set(activeRuns.value.map(run => run.id))
    for (const [id, stream] of runStreams) if (!active.has(id)) {
      stream.disconnect(); runStreams.delete(id); runText.delete(id); provisionalReplies.delete(id)
      const text = new Map(streamingText.value); text.delete(id); streamingText.value = text
    }
  }
  for (const run of activeRuns.value) attachRun(run.id)
  runStreamsSuspended = false; syncWorking()
}

function openProject() { workspaceEditor.value = { open: true, projectKey: null } }
function editWorkspace(projectKey: string) { workspaceEditor.value = { open: true, projectKey } }
let explicitSelectionVersion = 0
function startNewConversation(projectKey: string | null) {
  explicitSelectionVersion++
  const identity = projectKey ? selectionIdentity(projectKey) : null
  sessionRead++; sessionLoadAbort?.abort()
  setSelectedStorage(identity?.storageId ?? projectStorageId(identity?.id))
  selectedProjectId.value = identity?.id ?? null
  selectedSessionId.value = null; pendingSessionId.value = null
  draft.value = ''; messages.value = []; invokeError.value = null
  const project = currentProject.value
  try { localStorage.setItem('tinadec.workspace.context.v1', project ? selectionKey(project) : 'user::free') } catch { /* State is optional. */ }
}
async function completeWorkspace(input: WorkspaceInput, projectKey: string | null, hash?: string, anchor?: string) {
  let definition: WorkspaceDefinition
  if (projectKey) {
    const identity = selectionIdentity(projectKey)
    definition = await api.saveWorkspace(identity.storageId ?? projectStorageId(identity.id), input, hash!)
    projects.value = projects.value.map(project => selectionKey(project) === projectKey ? { ...project, ...definition, path: definition.roots.find(root => root.id === definition.primary_root_id)!.path, configuration_hash: definition.content_hash } : project)
  } else {
    const path = anchor ?? input.roots.find(root => root.id === input.primary_root_id)!.path
    const scope = await api.openStorageScope({ project_path: path, ...input })
    if (!scope.project_id || !scope.workspace) throw new Error(t('home.workspaceIdentityMissing'))
    definition = scope.workspace
    registerProjectStorage(scope.project_id, scope.storage_id)
    const project: ProjectDto = { id: scope.project_id, storage_id: scope.storage_id, ...definition, path: definition.roots.find(root => root.id === definition.primary_root_id)!.path,
      configuration_hash: definition.content_hash, storage_root: scope.storage_root, external: scope.external, created_at: new Date().toISOString() }
    projects.value = [project, ...projects.value.filter(item => selectionKey(item) !== selectionKey(project))]
    startNewConversation(selectionKey(project))
    revealWorkspace(selectionKey(project))
    void loadSessions()
  }
  workspaceEditor.value = { open: false, projectKey: null }
}

async function createSession(projectId: string | null) {
  if (projectId) {
    const identity = selectionIdentity(projectId)
    if (identity.storageId) { setSelectedStorage(identity.storageId); selectedProjectId.value = identity.id }
    projectId = identity.id
  }
  const targetProjectId = projectId ?? null
  const requestedView = viewMode.value
  const requestedStorage = projectId ? projectStorageId(projectId) : 'user'
  setSelectedStorage(requestedStorage)
  const boundApi = scopedApi(api, () => requestedStorage)
  if (pendingSessionId.value) {
    const existing = sessions.value.find((s) => sessionInScope(s, pendingSessionId.value!, requestedStorage))
    // Compare normalized project identity: Core omits project_id for a free
    // conversation, so the value can arrive as null or undefined while the
    // argument is null — a raw === check would miss and create a duplicate.
    if (existing && (existing.view_mode ?? 'flat') === requestedView && (existing.project_id ?? null) === targetProjectId) {
      selectedSessionId.value = pendingSessionId.value
      selectedProjectId.value = targetProjectId
      return
    }
  }
  await run(t('home.action.createSession'), async () => {
    // A session roster read that started before this explicit create must not be
    // allowed to arrive after the create and restore the old selected session.
    sessionListRead++
    sessionListAbort?.abort()
    const session = await boundApi.createSession(projectId, t('chat.title'), null, requestedView)
    sessions.value = [session, ...sessions.value]
    if (viewMode.value !== requestedView || selectedStorageId() !== requestedStorage) return
    selectedSessionId.value = session.id
    selectedProjectId.value = projectId ?? null
    pendingSessionId.value = session.id
  })
}

// ---------------------------------------------------------------------------
// Project/session lifecycle management (rename / archive / trash)
// ---------------------------------------------------------------------------

async function refreshProjectsAndSessions() {
  const projectList = await api.listProjects()
  projects.value = projectList
  if (selectedProjectId.value && !projectList.some((p) => p.id === selectedProjectId.value && (!p.storage_id || p.storage_id === selectedStorageId()))) {
    startNewConversation(null)
  }
  // Unfiltered listing, same as loadSessions: per-project queries never return
  // free conversations, so archiving/trashing anything would drop them from the
  // sidebar until a full reload.
  await loadSessions()
  const projectSessions = visibleSessions.value.filter((s) => (s.project_id ?? null) === selectedProjectId.value && (!s.storage_id || s.storage_id === selectedStorageId()))
  if (selectedSessionId.value && !projectSessions.some((s) => s.id === selectedSessionId.value)) {
    selectedSessionId.value = null
  }
}

async function renameProject(projectId: string, name: string) {
  const identity = selectionIdentity(projectId)
  const trimmed = name.trim()
  if (!trimmed) return
  await run(t('home.action.renameProject'), async () => {
    const updated = await generatedApi.renameProject(projectId, trimmed)
    projects.value = projects.value.map((p) => (p.id === identity.id && (!identity.storageId || p.storage_id === identity.storageId) ? { ...p, name: updated.name } : p))
  })
}

async function renameSession(sessionId: string, title: string) {
  const trimmed = title.trim()
  if (!trimmed) return
  await run(t('home.action.renameSession'), async () => {
    await saveSessionTitle(sessionId, trimmed)
  })
}

async function archiveProject(projectId: string) {
  await run(t('home.action.archiveProject'), async () => {
    await generatedApi.archiveProject(projectId)
    await refreshProjectsAndSessions()
  })
}

async function trashProject(projectId: string) {
  await run(t('home.action.trashProject'), async () => {
    await generatedApi.trashProject(projectId)
    await refreshProjectsAndSessions()
  })
}

async function archiveSession(sessionId: string) {
  await run(t('home.action.archiveSession'), async () => {
    await generatedApi.archiveSession(sessionId)
    await refreshProjectsAndSessions()
  })
}

async function trashSession(sessionId: string) {
  await run(t('home.action.trashSession'), async () => {
    await generatedApi.trashSession(sessionId)
    await refreshProjectsAndSessions()
  })
}

async function monitorSessionTransfer(receipt: SessionTransferDto, task: TaskHandle) {
  const key = `${receipt.source_storage_id}::${receipt.session_id}`
  try {
    sessionTransfers.value = { ...sessionTransfers.value, [key]: receipt }
    if (receipt.status === 'failed' || receipt.status === 'cancelled') throw new Error(receipt.error ?? receipt.error_code ?? t('home.transfer.unfinished'))
    if (receipt.status === 'completed') {
      const followTarget = selectedStorageId() === receipt.source_storage_id && selectedSessionId.value === receipt.session_id
      const selectionVersion = explicitSelectionVersion
      await loadSessions()
      delete sessionTransfers.value[key]
      if (followTarget && selectionVersion === explicitSelectionVersion) {
        setSelectedStorage(receipt.storage_id)
        suppressProjectSessionsReload = true
        try { selectedProjectId.value = receipt.project_id } finally { suppressProjectSessionsReload = false }
        selectedSessionId.value = receipt.session_id
        followSession(`${receipt.storage_id}::${receipt.session_id}`)
        await loadMessagesAndApprovals()
      }
      task.succeed(t('home.transfer.done'))
      return
    }
    task.update({ message: receipt.status === 'pending' ? t('home.transfer.waiting') : t('home.transfer.moving') })
    setTimeout(() => { void api.getSessionTransfer(receipt.transfer_id).then(next => monitorSessionTransfer(next, task)).catch(error => {
      task.fail(error, { action: { label: t('home.transfer.recheck'), run: () => api.getSessionTransfer(receipt.transfer_id).then(next => monitorSessionTransfer(next, task)) } })
    }) }, 1000)
  } catch (error) {
    delete sessionTransfers.value[key]
    if (selectedStorageId() === receipt.source_storage_id && selectedSessionId.value === receipt.session_id) followSession(key)
    task.fail(error)
  }
}

async function migrateSession(sessionKey: string, targetProjectKey: string) {
  const source = selectionIdentity(sessionKey)
  const target = selectionIdentity(targetProjectKey)
  const sourceRow = sessions.value.find(row => selectionKey(row) === sessionKey)
  const targetRow = projects.value.find(row => selectionKey(row) === targetProjectKey)
  if (!sourceRow || sourceRow.project_id || !targetRow?.storage_id || !target.storageId) return
  const storageId = source.storageId ?? sourceRow.storage_id ?? 'user'
  busy.value = true
  try {
    const receipt = await scopedApi(api, () => storageId).migrateSession(source.id, { target_storage_id: target.storageId, target_project_id: target.id })
    if (selectedStorageId() === storageId && selectedSessionId.value === source.id) {
      suspendFollowingSession(sessionKey)
      for (const stream of runStreams.values()) stream.disconnect()
      runStreams.clear(); syncWorking()
    }
    const task = notify.task({ key: `session-transfer:${receipt.transfer_id}`, message: t('home.transfer.accepted'), source: 'storage' })
    void monitorSessionTransfer(receipt, task)
  } catch (error) {
    const queued = (error as { code?: string }).code === 'queued_interactions_pending'
    notify.error(queued ? t('home.transfer.queued') : error, { title: t('home.transfer.failed') })
  } finally { busy.value = false }
}

// invoke-stream: 5 required + 2 optional, ack optimistic → delta incremental → done persisted
// explicit states: model_not_configured / disconnected / permission_denied / recovering
const streamingText = ref<Map<string, string>>(new Map())
const invokeError = ref<string | null>(null)
const lastCursor = ref<number | null>(null)

/**
 * The run a "stop" would cancel: the newest run that has not reached a terminal
 * state. Parked runs (awaiting_user) are included on purpose — a run waiting on an
 * approval the user no longer wants is exactly the one they most need to stop, and
 * the composer offered no way to do it.
 */
const stoppableRunId = computed(
  () => runs.value.find((r) => !['completed', 'failed', 'cancelled'].includes(r.status))?.id ?? null,
)

/**
 * The live text of that run. The controller has accumulated this per delta all along
 * but nothing ever rendered it, so a user saw nothing at all until the entire reply
 * was persisted — which reads as a hung agent.
 */
const streamingReplies = computed(() => Object.fromEntries(
  runs.value.filter(run => !['completed', 'failed', 'cancelled'].includes(run.status)
    && !messages.value.some(message => message.role === 'assistant' && message.run_id === run.id))
    .map(run => [run.id, streamingText.value.get(run.id) ?? '']),
))
const streamingReply = computed(() =>
  stoppableRunId.value ? streamingReplies.value[stoppableRunId.value] ?? '' : '',
)


async function handleSend(content: string, opts?: ComposerSubmitOptions) {
  // Freeze the selected policy before session creation yields to UI changes.
  const requestedView = viewMode.value
  const requestedSession = selectedSessionId.value
  const requestedProject = selectedProjectId.value
  const requestedStorage = selectedStorageId()
  const boundApi = scopedApi(api, () => requestedStorage)
  const preparingSession = !requestedSession && composerSessionCreation?.view === requestedView && composerSessionCreation.projectId === requestedProject && composerSessionCreation.storageId === requestedStorage
    ? composerSessionCreation.promise : null
  if (preparingSession) {
    invokeError.value = t('home.attachmentSessionPreparing')
    return
  }
  if (pendingAttachments.value.some(attachment => attachment.status === 'uploading')) {
    invokeError.value = t('home.attachmentUploading')
    return
  }
  const pendingSettings = requestedSession ? settingsWrites.get(sessionWriteKey(requestedSession, requestedStorage)) : null
  const outgoing = attachmentsForSend()
  if (pendingSettings && !await pendingSettings) return
  if (pendingSettings && (selectedStorageId() !== requestedStorage || selectedSessionId.value !== requestedSession || viewMode.value !== requestedView || selectedProjectId.value !== requestedProject)) return
  const requestedRecord = sessions.value.find(s => sessionInScope(s, requestedSession ?? '', requestedStorage))
  const settings = copyComposerSettings(settingsFor(requestedRecord ?? null, requestedView))
  const selected = applyComposerSettings(settings, opts ?? {})
  const requestedPermission = selected.permission_mode
  const requestedMode = selected.mode_version_id
  const requestedModel = selected.meeting_model_override
  const requestedSpace = requestedView === 'space' ? selected.space_options : null
  let requestedSettingsRevision = requestedRecord?.settings_revision
  await run(t('home.action.sendMessage'), async () => {
    let sessionId = requestedSession
    if (!sessionId) {
      // Created in the mode being sent: ChatPanel resets the picker to the new session's own
      // mode, so a session created on the default would flip the picker back after this send.
      sessionListRead++
      sessionListAbort?.abort()
      const session = await boundApi.createSession(requestedProject ?? null, t('chat.title'), requestedMode, requestedView, {
        permission_mode: requestedPermission, meeting_model_override: requestedModel,
        ...(requestedSpace ? { space_options: requestedSpace } : {}),
      })
      sessions.value = [session, ...sessions.value]
      sessionId = session.id
      requestedSettingsRevision = session.settings_revision
      if (selectedStorageId() === requestedStorage && viewMode.value === requestedView && selectedSessionId.value === requestedSession) {
        selectedSessionId.value = session.id
        pendingSessionId.value = session.id
      }
    }
    const isCurrent = () => selectedStorageId() === requestedStorage && selectedSessionId.value === sessionId && viewMode.value === requestedView
    if (isCurrent() && draft.value.trim() === content.trim()) draft.value = ''
    const snapshotContent = content
    if (isCurrent()) invokeError.value = null
    const clientMessageId = newId()
    const dispatchMode: DispatchMode = (opts?.dispatch_mode as DispatchMode) ?? getDispatchPref()
    const modeVersionId = requestedMode
    const targetRunId = opts?.target_run_id ?? null
    const meetingModelOverride = requestedModel
    if (dispatchMode === 'insert' && !targetRunId) throw new Error(t('home.insertNeedsRun'))
    // Taken before the request, not after it: a send that fails must leave the chips
    // alone so the same selection can be retried. Core binds these rows to the message
    // it appends, so the optimistic bubble carries the same projection a reload shows.
    if (dispatchMode === 'insert' && outgoing.attachmentIds.length > 0) {
      throw new Error(t('home.steerNoAttachments'))
    }
    try {
      if (isCurrent()) messages.value = [...messages.value, { id: `pending-${clientMessageId}`, session_id: sessionId, role: 'user', content: snapshotContent, created_at: new Date().toISOString(), attachments: outgoing.summaries } as MessageDto]
      // new interaction path (snake_case)
      const resp = await boundApi.createInteraction(sessionId, {
        content: snapshotContent,
        client_message_id: clientMessageId,
        mode_version_id: modeVersionId,
        permission_mode: requestedPermission,
        dispatch_mode: dispatchMode,
        target_run_id: targetRunId,
        ...(dispatchMode !== 'insert' ? {
          ...(requestedSettingsRevision !== undefined ? { expected_settings_revision: requestedSettingsRevision } : {}),
          meeting_model_override: meetingModelOverride,
          clear_meeting_model_override: meetingModelOverride === null,
          ...(requestedSpace ? { space_options: requestedSpace } : {}),
        } : {}),
        ...(outgoing.attachmentIds.length > 0 ? { attachment_ids: outgoing.attachmentIds } : {}),
      })
      settleSentAttachments(outgoing)
      // An admitted interaction names its own turn; a queued one names the run it waits behind
      // and no turn — that run's status is not "queued", so it is left alone.
      const waitingBehind = resp.status === 'queued' && !resp.turn_id && Boolean(resp.interaction_id)
      if (isCurrent() && resp.run_id && !waitingBehind) {
        attachRun(resp.run_id)
        runs.value = [{ id: resp.run_id, status: resp.status || 'planning' }, ...runs.value.filter((run) => run.id !== resp.run_id)]
      }
      if (isCurrent() && (waitingBehind || (!resp.run_id && resp.status === 'queued'))) {
        queuedMessages.value = [...queuedMessages.value, { id: clientMessageId, content: snapshotContent, interactionId: waitingBehind ? resp.interaction_id : undefined,
          permission_mode: requestedPermission, mode_version_id: modeVersionId, meeting_model_override: meetingModelOverride,
          clear_meeting_model_override: meetingModelOverride === null, space_options: requestedSpace,
          ...(outgoing.attachmentIds.length > 0 ? { attachment_ids: outgoing.attachmentIds } : {}) }]
      }
    } catch (err) {
      if (!isCurrent()) throw err
      const msg = err instanceof Error ? err.message : String(err)
      const code = (err as { code?: unknown }).code
      if (code === 'session_settings_conflict') {
        // Admission rejected this snapshot; keep the user's request for an explicit retry.
        if (!draft.value.trim()) draft.value = snapshotContent
        messages.value = messages.value.filter(message => message.id !== `pending-${clientMessageId}`)
        try {
          const latest = (await boundApi.listSessions()).find(s => sessionInScope(s, sessionId!, requestedStorage))
          if (latest) sessions.value = sessions.value.map(s => sessionInScope(s, sessionId!, requestedStorage) ? latest : s)
        } catch { /* The conflict remains actionable even if the refresh fails. */ }
      }
      // Main path only: POST /interactions is the single admission contract
      // (docs/app-core-ui.md §4.1). The legacy invoke-stream / POST messages
      // fallbacks were removed so failures surface visibly instead of
      // silently degrading to a non-durable path.
      if (code === 'mode_unavailable' || msg.includes('mode_unavailable')) invokeError.value = t('home.sendError.modeUnavailable')
      else if (code === 'context_conflict') {
        // §4.1-4: show revision conflict guidance; user must re-read before resending.
        invokeError.value = t('home.sendError.contextChanged')
      } else if (code === 'model_not_configured' || msg.includes('model_not_configured') || msg.includes('No model')) invokeError.value = t('home.sendError.modelNotConfigured')
      else if (code === 'forbidden' || code === 'unauthorized' || msg.includes('permission') || msg.includes('forbidden') || msg.includes('401') || msg.includes('403')) invokeError.value = t('home.sendError.forbidden')
      else if (msg.includes('recovering')) invokeError.value = t('home.sendError.recovering')
      else if (!navigator.onLine || code === 'backend_network_unavailable' || code === 'backend_write_outcome_unknown' || msg.includes('Failed to fetch')) invokeError.value = t('home.sendError.offline')
      else invokeError.value = msg
      throw err
    }
    if (isCurrent() && pendingSessionId.value === sessionId) {
      const title = generateTitle(snapshotContent, outgoing.summaries.map((row) => row.file_name))
      try {
        await saveSessionTitle(sessionId, title)
      } catch {
        const idx = sessions.value.findIndex((s) => s.id === sessionId)
        if (idx !== -1) sessions.value[idx] = { ...sessions.value[idx], title }
      }
      pendingSessionId.value = null
    }
    await loadMessagesAndApprovals()
  })
}

/**
 * Edit-and-resend. The cut is the irreversible half, so it happens first and alone:
 * if Core refuses it (an active run is still reading that history) nothing is lost.
 * Both exits hand the corrected text to the composer instead of sending it blind —
 * `handleSend` reports failures through `run()` rather than throwing, so an auto-send
 * could drop the user's words into a history that no longer has the original. A
 * non-empty composer aborts the whole operation: two unsent texts must never collide.
 */
async function editAndResend(payload: { id: string; content: string }) {
  const sessionId = selectedSessionId.value
  if (!sessionId) return
  if (draft.value.trim()) {
    invokeError.value = t('home.editBlockedByDraft')
    return
  }
  invokeError.value = null
  try {
    await api.revertSessionMessage(sessionId, payload.id)
  } catch (err) {
    const code = (err as { code?: unknown }).code
    const msg = err instanceof Error ? err.message : String(err)
    invokeError.value = code === 'active_run_conflict' || msg.includes('active_run_conflict')
      ? t('home.editBlockedByRun')
      : msg
    draft.value = payload.content
    return
  }
  draft.value = payload.content
  await loadMessagesAndApprovals()
}

function forgetQueued(id: string) {
  queuedMessages.value = queuedMessages.value.filter((item) => item.id !== id)
}

/**
 * Takes a message Core holds out of its queue. False when it already left (admitted or decided):
 * then acting on it again would send the same words twice.
 */
async function dequeue(item: { interactionId?: string }, sessionId: string, capturedApi = api): Promise<boolean> {
  if (!item.interactionId) return true
  try {
    await capturedApi.cancelInteraction(sessionId, item.interactionId)
    return true
  } catch (err) {
    const status = (err as { status?: number }).status
    // 404: Core no longer knows it as queued; nothing is waiting to be taken out.
    return status === 404
  }
}

async function dismissQueued(id: string) {
  const sessionId = selectedSessionId.value
  const storageId = selectedStorageId(); const capturedApi = scopedApi(api, () => storageId)
  const item = queuedMessages.value.find((q) => q.id === id)
  if (!item || !sessionId) return
  if (await dequeue(item, sessionId, capturedApi) && selectedSessionId.value === sessionId && selectedStorageId() === storageId) forgetQueued(id)
}

async function editQueued(id: string) {
  const sessionId = selectedSessionId.value
  const storageId = selectedStorageId(); const capturedApi = scopedApi(api, () => storageId)
  const item = queuedMessages.value.find((q) => q.id === id)
  if (!item || !sessionId) return
  if (!(await dequeue(item, sessionId, capturedApi)) || selectedSessionId.value !== sessionId || selectedStorageId() !== storageId) return
  draft.value = item.content
  forgetQueued(id)
}

/**
 * Steers a run with a waiting message. `interrupt` is the hard insert: Core cuts off what the run is
 * doing (a model call is redone, tool calls not yet started are skipped) instead of letting the
 * steering apply at its next step.
 */
async function steerQueued(id: string, targetRunId: string, interrupt = false) {
  const sessionId = selectedSessionId.value
  const storageId = selectedStorageId(); const capturedApi = scopedApi(api, () => storageId)
  if (!sessionId) return
  const item = queuedMessages.value.find((q) => q.id === id)
  if (!item) return
  if (!(await dequeue(item, sessionId, capturedApi))) return
  let sent = false
  await run(t('home.action.steerMessage'), async () => {
    await capturedApi.createInteraction(sessionId, {
      content: item.content,
      client_message_id: newId(),
      mode_version_id: null,
      dispatch_mode: 'insert',
      target_run_id: targetRunId,
      ...(interrupt ? { interrupt: true } : {}),
    })
    sent = true
  })
  if (sent && selectedSessionId.value === sessionId && selectedStorageId() === storageId) forgetQueued(id)
}

async function promoteQueued(id: string) {
  const sessionId = selectedSessionId.value
  const storageId = selectedStorageId(); const capturedApi = scopedApi(api, () => storageId)
  const permission = currentPermission.value
  if (!sessionId) return
  const item = queuedMessages.value.find((q) => q.id === id)
  if (!item) return
  if (!(await dequeue(item, sessionId, capturedApi))) return
  let sent = false
  await run(t('home.action.promoteMessage'), async () => {
    await capturedApi.createInteraction(sessionId, {
      content: item.content,
      // A message Core already holds keeps its id, so running it now reuses the words the user
      // already sent instead of posting them a second time.
      client_message_id: item.interactionId ? item.id : newId(),
      mode_version_id: item.mode_version_id ?? null,
      dispatch_mode: 'parallel',
      target_run_id: null,
      permission_mode: item.permission_mode ?? permission,
      meeting_model_override: item.meeting_model_override ?? null,
      ...(item.clear_meeting_model_override ? { clear_meeting_model_override: true } : {}),
      ...(item.space_options ? { space_options: { ...item.space_options } } : {}),
      ...(item.attachment_ids?.length ? { attachment_ids: item.attachment_ids } : {}),
    })
    sent = true
  })
  if (sent && selectedSessionId.value === sessionId && selectedStorageId() === storageId) forgetQueued(id)
}

async function requestShellApproval() {
  await run(t('home.action.requestApproval'), async () => {
    const projectPath = currentProject.value?.path
    if (!projectPath) throw new Error(t('home.shellNeedsProject'))
    const command = shellCommand.value.trim()
    if (!command) throw new Error(t('home.shellNeedsCommand'))
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
    const approval = userToolActionToApproval(action, i18n.global.t('approval.runCommandSummary', { command }), {
      sessionId: selectedSessionId.value,
      cwd: projectPath,
    })
    approvals.value = [approval, ...approvals.value]
  })
}

async function decideApproval(
  approval: ApprovalDto,
  decision: 'approved' | 'rejected',
  scope?: 'once' | 'run',
) {
  await run(t('home.action.decideApproval'), async () => {
    await api.decideApproval(approval.id, decision, null, scope)
    await loadMessagesAndApprovals()
  })
}

/**
 * Decide by id, for the chat's inline approve/reject buttons. Those buttons sit on a
 * tool card, which knows the approval id but not the whole approval record, and the
 * chat had no handler at all before — the button existed and clicked into nothing.
 */
async function decideApprovalById(approvalId: string, decision: 'approved' | 'rejected', scope?: 'once' | 'run') {
  await run(t('home.action.decideApproval'), async () => {
    await api.decideApproval(approvalId, decision, null, scope)
    await loadMessagesAndApprovals()
  })
}

async function revokeApprovalRule(rule: ApprovalRuleDto) {
  await run(t('home.action.revokeApprovalRule'), async () => {
    await api.revokeApprovalRule(rule.id)
    approvalRules.value = approvalRules.value.filter((item) => item.id !== rule.id)
  })
}

async function createApprovalRule(input: CreateApprovalRuleInput) {
  await run(t('home.action.createApprovalRule'), async () => {
    const created = await api.createApprovalRule(input)
    approvalRules.value = [created, ...approvalRules.value.filter((item) => item.id !== created.id)]
  })
}

/** Cancel the run the composer is currently bound to. */
async function stopRun() {
  const runId = stoppableRunId.value
  if (!runId) return
  await run(t('home.action.stopRun'), async () => {
    await api.controlRun(runId, 'cancel')
    await loadMessagesAndApprovals()
  })
}

/** Run a catalogued tool with the active workspace/session context. The
 * provider remains the authority for approval and execution policy. */
async function executeCatalogTool(tool: ToolDescriptorDto) {
  const sessionId = selectedSessionId.value
  const cwd = currentProject.value?.path
  if (!sessionId || !cwd) {
    notify.warning({
      key: 'tool-catalog-context',
      title: t('home.toolNeedsWorkspace'),
      message: t('home.toolNeedsWorkspaceHint'),
      source: 'tools',
    })
    return
  }
  await run(t('home.action.runTool', { name: tool.display_name }), async () => {
    const response = tool.execute_endpoint.includes('/tool-runtime/')
      ? await api.executeToolRuntime(tool.id, { session_id: sessionId, cwd, arguments: {} })
      : await api.executeCodeTool(tool.id, { session_id: sessionId, cwd, arguments: {} })
    if (response.approval_summary) {
      // Core's approval_summary and summary are diagnostic English; the notification says it in words.
      notify.info({ key: `tool-approval-${tool.id}`, title: t('home.toolNeedsApproval'), message: t('activity.toolNeedsApproval', { tool: tool.display_name }), details: response.approval_summary, source: 'tools' })
    } else {
      notify.success({ key: `tool-complete-${tool.id}`, title: t('home.toolCompleted'), message: t('activity.toolCompleted', { tool: tool.display_name }), source: 'tools' })
    }
    await loadMessagesAndApprovals()
  })
}

function recordApproval(approval: ApprovalDto) {
  approvals.value = [approval, ...approvals.value.filter((item) => item.id !== approval.id)]
}

/**
 * Fan-out seam for terminal widgets. It stays on the controller because that is where
 * widgets already reach, but the connection itself is no longer the controller's to
 * own: one session stream per window lives in `sessionEventBus`, which
 * `useAgentActivity` also subscribes to instead of opening a second EventSource.
 */
function onEvent(handler: (event: EventEnvelope) => void): () => void {
  return subscribeToSessionEvents(handler)
}

async function handleSessionEvent(event: EventEnvelope) {
  const bySeq = new Map(events.value.map((item) => [item.seq, item]))
  bySeq.set(event.seq, event)
  events.value = [...bySeq.values()].sort((left, right) => left.seq - right.seq).slice(-80)
  // Core took a waiting message out of the queue: it runs now, was rejected, or was dequeued.
  if (event.type === 'interaction.queued_executed' || event.type === 'interaction.queue_cancelled' || event.type === 'interaction.queued_unreadable') {
    const directive = event.payload?.['directive_id']
    if (typeof directive === 'string') queuedMessages.value = queuedMessages.value.filter((item) => item.interactionId !== directive)
    const released = event.payload?.['released_run_id']
    if (event.type === 'interaction.queued_executed' && typeof released === 'string') attachRun(released)
  }
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
    scheduleSessionRefresh()
  }
}

/*
 * Session events refresh the transcript, approvals and runs — six REST reads, two of them the
 * whole session (messages and the tool timeline). They used to run once per event, each one
 * aborting the read before it: opening a session replays its whole history, so a long one sent
 * hundreds of full reloads that Core still executed after the client gave up on them, the
 * transcript loaded last of all, and the stream itself stalled behind the load. Now the replay
 * is skipped (the session switch already reads the state it describes) with one read after it
 * if anything arrived; live events coalesce into one trailing read, one in flight at a time,
 * and a burst during a read earns exactly one more.
 */
const SESSION_REFRESH_DELAY_MS = 250
let sessionReplaying = false
let eventsDuringReplay = false
let refreshTimer: ReturnType<typeof setTimeout> | null = null
let refreshing = false
let refreshAgain = false

function scheduleSessionRefresh() {
  if (sessionReplaying) { eventsDuringReplay = true; return }
  if (refreshing) { refreshAgain = true; return }
  if (refreshTimer) return
  refreshTimer = setTimeout(() => { refreshTimer = null; void runSessionRefresh() }, SESSION_REFRESH_DELAY_MS)
}

async function runSessionRefresh() {
  refreshing = true
  try { await loadMessagesAndApprovals() } catch { /* the next event or session switch reads again */ }
  finally {
    refreshing = false
    if (refreshAgain) { refreshAgain = false; scheduleSessionRefresh() }
  }
}

function sessionStreamOpened() {
  sessionReplaying = true
  eventsDuringReplay = false
}

function sessionReplayFinished() {
  if (!sessionReplaying) return
  sessionReplaying = false
  if (eventsDuringReplay) { eventsDuringReplay = false; scheduleSessionRefresh() }
}

watch(selectedProjectId, id => {
  setSelectedStorage(projectStorageId(id))
  if (suppressProjectSessionsReload || !canAccessBackend.value) return
  void loadSessions()
}, { flush: 'sync' })

watch([selectedSessionId, selectedStorage], ([, storageId], [, previousStorage]) => {
  sessionLoadAbort?.abort()
  messages.value = storageId !== previousStorage ? [] : messages.value.filter((message) => message.session_id === selectedSessionId.value)
  transcriptLoading.value = Boolean(selectedSessionId.value) && messages.value.length === 0
  orchestration.value = null
  approvals.value = []
  toolExecutions.value = []
  runs.value = []
  for (const stream of runStreams.values()) stream.disconnect()
  runStreams.clear()
  syncWorking()
  runText.clear()
  provisionalReplies.clear()
  streamingText.value = new Map()
  void loadMessagesAndApprovals().catch(error => { if (!isAbortError(error)) notify.error(error, { title: t('home.sessionLoadFailed') }) })
  followSession(currentSession.value ? selectionKey(currentSession.value) : selectedSessionId.value)
  queuedMessages.value = []
})

/** Start the controller's data pipeline (idempotent). */
let started = false
const { canAccessBackend, status: hostAccessStatus } = useHostAccess()
let recoveryRead = 0
watch(canAccessBackend, ready => {
  if (!started) return
  const recovery = ++recoveryRead
  if (ready) {
    void loadInitial().then(async () => {
      if (recovery !== recoveryRead || !canAccessBackend.value || !currentSession.value) return
      const key = selectionKey(currentSession.value)
      await loadMessagesAndApprovals()
      if (recovery === recoveryRead && canAccessBackend.value && currentSession.value && selectionKey(currentSession.value) === key) followSession(key)
    }).catch(error => { if (recovery === recoveryRead && !isAbortError(error)) notify.error(error, { title: t('home.sessionRecoverFailed') }) })
    return
  }
  if (hostAccessStatus.value?.error?.code === 'desktop_restart_required' || hostAccessStatus.value?.error?.code === 'host_bridge_unavailable') {
    for (const key of ['home-load', 'home-readiness']) dismissByKey(key)
  }
  initialRead++; sessionListRead++; sessionListAbort?.abort(); sessionLoadAbort?.abort()
  for (const stream of runStreams.values()) stream.disconnect()
  suspendFollowingSession(currentSession.value ? selectionKey(currentSession.value) : selectedSessionId.value ?? '')
  runStreamsSuspended = true; working.value = false; busy.value = false
})
function setViewMode(next: SessionView) {
  if (viewMode.value === next) return
  lastSessionByView[viewMode.value] = selectedSessionId.value
  sessionListRead++
  sessionListAbort?.abort()
  viewMode.value = next
  draft.value = ''
  invokeError.value = null
  const candidates = visibleSessions.value.filter(s => (s.project_id ?? null) === selectedProjectId.value && (!s.storage_id || s.storage_id === selectedStorageId()))
  selectedSessionId.value = candidates.find(s => s.id === lastSessionByView[next])?.id ?? candidates[0]?.id ?? null
  if (started) void loadSessions()
}
function start() {
  if (started) return
  started = true
  if (canAccessBackend.value) void loadInitial()
  subscribeToSessionEvents(handleSessionEvent, { onOpen: sessionStreamOpened, onHeartbeat: sessionReplayFinished })
  followSession(currentSession.value ? selectionKey(currentSession.value) : selectedSessionId.value)
}

export const homeController = {
  viewMode,
  visibleSessions,
  setViewMode,
  // Refs (reactive state)
  projects,
  sessions,
  messages,
  approvals,
  approvalRules,
  events,
  recentEvents,
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
  currentPermission,
  composerSettings,
  settingsSaving,
  settingsError,
  updateComposerSettings,
  ensureComposerSession,
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
  editWorkspace,
  workspaceEditor,
  workspaceLoadStates,
  completeWorkspace,
  startNewConversation,
  retryWorkspaces: retryWorkspace,
  unregisterWorkspace,
  installErrorRecovery,
  createSession,
  renameProject,
  renameSession,
  archiveProject,
  trashProject,
  archiveSession,
  trashSession,
  migrateSession,
  sessionTransfers,
  runs,
  queuedMessages,
  activeRuns,
  dismissQueued,
  editQueued,
  steerQueued,
  promoteQueued,
  sendMessage: async (opts?: ComposerSubmitOptions) => {
    const content = draft.value.trim()
    // Empty is sendable when a finished upload is there to speak for the turn; Core appends
    // it as a message and starts no run. Same rule the Send button reads, from the same owner.
    if (!content && readyAttachmentCount() === 0) return
    await handleSend(content, opts)
  },
  handleWelcomeSend: (payload: ComposerSubmitOptions & { content: string }) => handleSend(payload.content, payload),
  editAndResend,
  requestShellApproval,
  decideApproval,
    decideApprovalById,
    revokeApprovalRule,
    createApprovalRule,
  stopRun,
  executeCatalogTool,
  stoppableRunId,
  streamingReply,
  streamingReplies,
  conversationFeed,
  transcriptLoading,
  agentTurnActivities,
  recordApproval,
  loadMessagesAndApprovals,
  refreshProjectsAndSessions,
  updateDraft: (value: string) => { draft.value = value },
  updatePermission: (value: PermissionLevel) => updateComposerSettings({ permission_mode: value }),
  setSelectedProject: (id: string | null) => {
    startNewConversation(id)
  },
  setSelectedSession: (id: string) => {
    explicitSelectionVersion++
    const identity = selectionIdentity(id)
    if (identity.storageId) setSelectedStorage(identity.storageId)
    const session = sessions.value.find(s => s.id === identity.id && (!s.storage_id || s.storage_id === selectedStorageId()))
    if (session && (session.view_mode ?? 'flat') !== viewMode.value) return
    if (session) { suppressProjectSessionsReload = true; selectedProjectId.value = session.project_id ?? null; suppressProjectSessionsReload = false }
    selectedSessionId.value = identity.id
    try { localStorage.setItem('tinadec.workspace.context.v1', currentProject.value ? selectionKey(currentProject.value) : 'user::free') } catch { /* Optional local state. */ }
  },
}
