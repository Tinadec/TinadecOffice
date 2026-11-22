import { computed, onBeforeUnmount, onMounted, ref, watch, type ComputedRef, type Ref } from 'vue'
import {
  COMPANION_DELTA_EVENT,
  COMPANION_PROPOSAL_EVENT,
  COMPANION_RUN_EVENT,
  COMPANION_SETTINGS_EVENT,
  COMPANION_STAGE_CUE_EVENT,
  COMPANION_TOOL_CALL_EVENT,
  COMPANION_WORK_SESSION_EVENT,
  type CompanionEventNotice,
  type CompanionWorkSessionNotice,
} from '@/events/companionEventTypes'
import {
  companionApi,
  companionStreamSupported,
  subscribeCompanionFeed,
  type CompanionActivity,
  type CompanionConnectedWorkSession,
  type CompanionConnectionOverview,
  type CompanionConversation,
  type CompanionSettings,
  type CompanionSummary,
} from './companionApi'
import { parseWorkSessionPlanProgress } from './companionWorkSession'
import { useHostAccess } from './hostAccess'
import type { CompanionFeedFilter } from './companionView'

const { canAccessBackend } = useHostAccess()

/**
 * Low-frequency stream safety net (60s): real updates arrive over `GET /api/v1/companion-events`.
 */
export const COMPANION_CENTER_FALLBACK_MS = 60_000
const NOTICE_DEBOUNCE_MS = 250

export type CompanionSource = 'companions' | 'activity' | 'conversations' | 'connections' | 'settings'

export interface CompanionLiveToolCall {
  callId: string
  toolName: string
  status: 'started' | 'completed' | 'failed'
  argumentsPreview: string | null
  resultPreview: string | null
}

export interface CompanionLiveTurn {
  runId: string
  companionId: string | null
  conversationId: string | null
  body: string
  toolCalls: CompanionLiveToolCall[]
}

export interface CompanionStageCue {
  emotion: string | null
  speaking: boolean
  intensity: number
  gesture: string | null
  updatedAt: number
}

export interface CompanionProposalNotice {
  proposalId: string
  companionId: string | null
  conversationId: string | null
  kind: 'mandate' | 'recruitment'
  action: string | null
  status: string | null
  summary: string | null
  payload: Record<string, unknown> | null
}

export const companions: Ref<CompanionSummary[]> = ref([])
export const activity: Ref<CompanionActivity[]> = ref([])
export const conversations: Ref<CompanionConversation[]> = ref([])
export const connections: Ref<CompanionConnectionOverview[]> = ref([])
/** User-wide companion settings (main companion, recruit limit, max_bot_hop_depth, global pause). */
export const settings: Ref<CompanionSettings | null> = ref(null)
/** Selected companion id across Contacts Rail, Thread Workspace, and Right Bot Dock. */
export const selectedCompanionId: Ref<string | null> = ref(null)
/** Selected active conversation id (private bot chat or group chat). */
export const selectedConversationId: Ref<string | null> = ref(null)
export const loading = ref(true)
export const failures = ref<Partial<Record<CompanionSource, unknown>>>({})
export const loadedAt = ref<Date | null>(null)
export const feedFilter: Ref<CompanionFeedFilter> = ref('all')

/** Live SSE streaming buffers keyed by runId / companionId. */
export const streamingTurns: Ref<Record<string, CompanionLiveTurn>> = ref({})
export const stageCues: Ref<Record<string, CompanionStageCue>> = ref({})
export const liveProposals: Ref<CompanionProposalNotice[]> = ref([])

export interface CompanionWorkSessionBackflowRef {
  sessionId: string
  runId: string | null
  companionId: string | null
  conversationId: string | null
  status: string
  updatedAt: string
}

/** Live work-session snapshots keyed by companionId (from listWorkSessions + SSE updates). */
export const workSessionsByCompanion: Ref<Record<string, CompanionConnectedWorkSession[]>> = ref({})
/** Live work-session cards keyed by sessionId, updated immediately by `companion.work_session` SSE frames. */
export const liveWorkSessions: Ref<Record<string, CompanionConnectedWorkSession>> = ref({})
export const workSessionsLoading: Ref<Record<string, boolean>> = ref({})
/** Recent SSE work-session backflow references for inline chat cards. */
export const workSessionBackflows: Ref<CompanionWorkSessionBackflowRef[]> = ref([])

let subscribers = 0
let timer: ReturnType<typeof setTimeout> | null = null
let noticeTimer: ReturnType<typeof setTimeout> | null = null
let pendingSources: Set<CompanionSource> | null = null
const queuedSources = new Set<CompanionSource>()
let unsubscribeFeed: (() => void) | null = null
let inflight: Promise<void> | null = null
let generation = 0
let stopped = false

function visible(): boolean {
  return typeof document === 'undefined' || document.visibilityState === 'visible'
}

function apply<T>(source: CompanionSource, value: T[] | null, target: Ref<T[]>): void {
  if (value === null) return
  target.value = value
  const next = { ...failures.value }
  delete next[source]
  failures.value = next
}

function fail(source: CompanionSource, reason: unknown): void {
  failures.value = { ...failures.value, [source]: reason }
}

/** Single-flight snapshot refresh. */
export async function refreshCompanionCenter(): Promise<void> {
  return refreshCompanionSources(null)
}

async function refreshCompanionSources(only: CompanionSource[] | null): Promise<void> {
  if (inflight) {
    if (only) for (const source of only) queuedSources.add(source)
    return inflight
  }
  const wants = (source: CompanionSource) => !only || only.includes(source)
  const token = ++generation
  inflight = (async () => {
    const [companionPage, activityPage, conversationPage, connectionPage, settingsResult] = await Promise.allSettled([
      wants('companions') ? companionApi.listCompanions() : Promise.resolve(null),
      wants('activity') ? companionApi.listActivity(100) : Promise.resolve(null),
      wants('conversations') ? companionApi.listAllConversations({ limit: 100 }) : Promise.resolve(null),
      wants('connections') ? companionApi.listConnectionsOverview() : Promise.resolve(null),
      wants('settings') ? companionApi.getSettings() : Promise.resolve(null),
    ])
    if (token !== generation) return
    if (companionPage.status === 'fulfilled') apply('companions', companionPage.value, companions)
    else fail('companions', companionPage.reason)
    if (activityPage.status === 'fulfilled') apply('activity', activityPage.value, activity)
    else fail('activity', activityPage.reason)
    if (conversationPage.status === 'fulfilled') apply('conversations', conversationPage.value, conversations)
    else fail('conversations', conversationPage.reason)
    if (connectionPage.status === 'fulfilled') apply('connections', connectionPage.value, connections)
    else fail('connections', connectionPage.reason)
    if (wants('settings')) {
      if (settingsResult.status === 'fulfilled') {
        if (settingsResult.value) settings.value = settingsResult.value
        const next = { ...failures.value }
        delete next.settings
        failures.value = next
      } else fail('settings', settingsResult.reason)
    }
    if (token !== generation) return
    if (companionPage.status === 'fulfilled' && companionPage.value && selectedCompanionId.value
      && !companionPage.value.some((item) => item.id === selectedCompanionId.value)) {
      selectedCompanionId.value = null
    }
    const applied = [companionPage, activityPage, conversationPage, connectionPage, settingsResult]
      .some((result) => result.status === 'fulfilled' && result.value !== null)
    if (applied) loadedAt.value = new Date()
    loading.value = false
  })().finally(() => {
    inflight = null
    if (queuedSources.size === 0) return
    const queued = [...queuedSources]
    queuedSources.clear()
    void refreshCompanionSources(queued)
  })
  return inflight
}

const TERMINAL_RUN_STATUSES = new Set(['completed', 'failed', 'cancelled', 'canceled', 'idle', 'stopped'])

function mergeWorkSessionNotice(
  prev: CompanionConnectedWorkSession | undefined,
  ws: CompanionWorkSessionNotice,
): CompanionConnectedWorkSession {
  const status = ws.status || prev?.latest_run_status || 'idle'
  const runId = ws.runId ?? prev?.latest_run_id ?? null
  const summary = ws.summary ?? (ws.runId && ws.runId !== prev?.latest_run_id ? null : prev?.latest_run_summary ?? null)
  const planSummary = ws.planSummary ?? prev?.plan_summary ?? null
  const planProgress = ws.planSummary !== null
    ? parseWorkSessionPlanProgress(ws.planSummary)
    : (prev?.plan_progress ?? parseWorkSessionPlanProgress(planSummary))
  const pendingApprovals = ws.pendingApprovals.length > 0
    ? ws.pendingApprovals
    : TERMINAL_RUN_STATUSES.has(status.toLowerCase()) || status.toLowerCase() === 'running'
      ? []
      : (prev?.pending_approvals ?? [])
  const isBrowserHandoff = status.toLowerCase() === 'browser_handoff'
  return {
    session_id: ws.sessionId,
    title: ws.sessionTitle || prev?.title || ws.sessionId.slice(0, 8),
    project_id: ws.projectId ?? prev?.project_id ?? null,
    project_name: ws.projectName ?? prev?.project_name ?? null,
    mode_name: ws.modeName ?? prev?.mode_name ?? null,
    view_mode: ws.viewMode ?? prev?.view_mode ?? null,
    latest_run_id: runId,
    latest_run_status: status,
    latest_run_summary: summary,
    plan_summary: planSummary,
    plan_progress: planProgress,
    pending_approvals: pendingApprovals,
    browser_space_id: ws.browserSpaceId ?? (isBrowserHandoff ? prev?.browser_space_id ?? null : null),
    browser_url: ws.browserUrl ?? (isBrowserHandoff ? prev?.browser_url ?? null : null),
    browser_driver: ws.browserDriver ?? (isBrowserHandoff ? prev?.browser_driver ?? null : null),
    browser_handoff_reason: ws.browserHandoffReason ?? (isBrowserHandoff ? prev?.browser_handoff_reason ?? null : null),
    updated_at: ws.updatedAt ?? new Date().toISOString(),
    latest_run: runId ? { id: runId, status, summary } : (prev?.latest_run ?? null),
  }
}

function updateCachedWorkSession(sessionId: string, updated: CompanionConnectedWorkSession, companionId?: string | null): void {
  liveWorkSessions.value = {
    ...liveWorkSessions.value,
    [sessionId]: updated,
  }
  const nextMap: Record<string, CompanionConnectedWorkSession[]> = { ...workSessionsByCompanion.value }
  let touched = false
  for (const [cid, rows] of Object.entries(nextMap)) {
    const idx = rows.findIndex((item) => item.session_id === sessionId)
    if (idx >= 0) {
      nextMap[cid] = rows.map((item, at) => (at === idx ? updated : item))
      touched = true
    } else if (companionId && cid === companionId) {
      nextMap[cid] = [updated, ...rows]
      touched = true
    }
  }
  if (companionId && !nextMap[companionId]) {
    nextMap[companionId] = [updated]
    touched = true
  }
  if (touched) {
    workSessionsByCompanion.value = nextMap
  }
}

export async function refreshWorkSessions(companionId: string): Promise<CompanionConnectedWorkSession[]> {
  if (!companionId || typeof companionApi.listWorkSessions !== 'function') return []
  workSessionsLoading.value = { ...workSessionsLoading.value, [companionId]: true }
  try {
    const rows = await companionApi.listWorkSessions(companionId)
    workSessionsByCompanion.value = {
      ...workSessionsByCompanion.value,
      [companionId]: rows,
    }
    if (rows.length > 0) {
      const nextLive = { ...liveWorkSessions.value }
      for (const row of rows) {
        nextLive[row.session_id] = row
      }
      liveWorkSessions.value = nextLive
    }
    return rows
  } catch {
    return workSessionsByCompanion.value[companionId] ?? []
  } finally {
    workSessionsLoading.value = { ...workSessionsLoading.value, [companionId]: false }
  }
}

export function getWorkSessionById(sessionId: string): CompanionConnectedWorkSession | null {
  if (!sessionId) return null
  if (liveWorkSessions.value[sessionId]) return liveWorkSessions.value[sessionId]!
  for (const rows of Object.values(workSessionsByCompanion.value)) {
    const match = rows.find((item) => item.session_id === sessionId)
    if (match) return match
  }
  return null
}

export function applyApprovalDecided(sessionId: string, approvalId: string, approved: boolean): void {
  const existing = getWorkSessionById(sessionId)
  if (!existing) return
  const remaining = existing.pending_approvals.filter((item) => item.approval_id !== approvalId)
  const nextStatus = remaining.length > 0
    ? existing.latest_run_status
    : (existing.latest_run_status === 'awaiting_approval' ? (approved ? 'running' : 'cancelled') : existing.latest_run_status)
  const updated: CompanionConnectedWorkSession = {
    ...existing,
    latest_run_status: nextStatus,
    pending_approvals: remaining,
    latest_run: existing.latest_run
      ? { ...existing.latest_run, status: nextStatus ?? existing.latest_run.status }
      : existing.latest_run,
    updated_at: new Date().toISOString(),
  }
  updateCachedWorkSession(sessionId, updated)
}

export function applyRunCancelled(sessionId: string, runId?: string | null): void {
  const existing = getWorkSessionById(sessionId)
  if (!existing) return
  if (runId && existing.latest_run_id && existing.latest_run_id !== runId) return
  const updated: CompanionConnectedWorkSession = {
    ...existing,
    latest_run_status: 'cancelled',
    pending_approvals: [],
    browser_handoff_reason: null,
    latest_run: existing.latest_run
      ? { ...existing.latest_run, status: 'cancelled' }
      : existing.latest_run,
    updated_at: new Date().toISOString(),
  }
  updateCachedWorkSession(sessionId, updated)
}

export function applyBrowserTakenOver(sessionId: string): void {
  const existing = getWorkSessionById(sessionId)
  if (!existing) return
  const nextStatus = existing.latest_run_status === 'browser_handoff' ? 'running' : existing.latest_run_status
  const updated: CompanionConnectedWorkSession = {
    ...existing,
    latest_run_status: nextStatus,
    browser_handoff_reason: null,
    latest_run: existing.latest_run
      ? { ...existing.latest_run, status: nextStatus ?? existing.latest_run.status }
      : existing.latest_run,
    updated_at: new Date().toISOString(),
  }
  updateCachedWorkSession(sessionId, updated)
}

export function applyLiveStreamNotice(notice: CompanionEventNotice): boolean {
  if (notice.type === COMPANION_WORK_SESSION_EVENT) {
    const ws = notice.workSession
    if (!ws) return true
    const merged = mergeWorkSessionNotice(liveWorkSessions.value[ws.sessionId], ws)
    updateCachedWorkSession(ws.sessionId, merged, notice.companionId)
    const entry: CompanionWorkSessionBackflowRef = {
      sessionId: ws.sessionId,
      runId: merged.latest_run_id,
      companionId: notice.companionId,
      conversationId: notice.conversationId,
      status: merged.latest_run_status ?? 'running',
      updatedAt: merged.updated_at ?? new Date().toISOString(),
    }
    const filtered = workSessionBackflows.value.filter(
      (item) => !(item.sessionId === entry.sessionId && item.conversationId === entry.conversationId),
    )
    workSessionBackflows.value = [entry, ...filtered].slice(0, 40)
    return true
  }
  if (notice.type === COMPANION_DELTA_EVENT) {
    const runId = notice.runId
    if (!runId) return true
    const prev = streamingTurns.value[runId] ?? {
      runId,
      companionId: notice.companionId,
      conversationId: notice.conversationId,
      body: '',
      toolCalls: [],
    }
    streamingTurns.value = {
      ...streamingTurns.value,
      [runId]: {
        ...prev,
        companionId: notice.companionId ?? prev.companionId,
        conversationId: notice.conversationId ?? prev.conversationId,
        body: prev.body + (notice.delta ?? ''),
      },
    }
    return true
  }

  if (notice.type === COMPANION_TOOL_CALL_EVENT) {
    const runId = notice.runId
    const callId = notice.callId || `${runId ?? 'call'}:${notice.toolName ?? 'tool'}`
    if (!runId) return true
    const prev = streamingTurns.value[runId] ?? {
      runId,
      companionId: notice.companionId,
      conversationId: notice.conversationId,
      body: '',
      toolCalls: [],
    }
    const entry: CompanionLiveToolCall = {
      callId,
      toolName: notice.toolName ?? 'tool',
      status: notice.toolStatus ?? 'started',
      argumentsPreview: notice.argumentsPreview ?? null,
      resultPreview: notice.resultPreview ?? null,
    }
    const idx = prev.toolCalls.findIndex((item) => item.callId === callId)
    const nextCalls = idx >= 0
      ? prev.toolCalls.map((item, at) => (at === idx ? entry : item))
      : [...prev.toolCalls, entry]
    streamingTurns.value = {
      ...streamingTurns.value,
      [runId]: { ...prev, toolCalls: nextCalls },
    }
    return true
  }

  if (notice.type === COMPANION_STAGE_CUE_EVENT) {
    if (!notice.companionId) return true
    const prev = stageCues.value[notice.companionId]
    stageCues.value = {
      ...stageCues.value,
      [notice.companionId]: {
        emotion: notice.emotion !== undefined ? notice.emotion : (prev?.emotion ?? null),
        speaking: typeof notice.speaking === 'boolean' ? notice.speaking : (prev?.speaking ?? false),
        intensity: typeof notice.intensity === 'number' ? notice.intensity : (prev?.intensity ?? 0),
        gesture: notice.gesture !== undefined ? notice.gesture : (prev?.gesture ?? null),
        updatedAt: Date.now(),
      },
    }
    return true
  }

  if (notice.type === COMPANION_PROPOSAL_EVENT) {
    const proposalId = notice.proposalId ?? `prop-${notice.seq}`
    const row: CompanionProposalNotice = {
      proposalId,
      companionId: notice.companionId,
      conversationId: notice.conversationId,
      kind: notice.proposalKind ?? 'mandate',
      action: notice.proposalAction ?? null,
      status: notice.proposalStatus ?? 'proposed',
      summary: notice.proposalSummary ?? null,
      payload: notice.proposalPayload ?? null,
    }
    const filtered = liveProposals.value.filter((item) => item.proposalId !== proposalId)
    liveProposals.value = row.status === 'proposed' ? [row, ...filtered] : filtered
    return false
  }

  if (notice.type === COMPANION_RUN_EVENT && notice.runId
    && (notice.kind === 'run_completed' || notice.kind === 'run_failed')) {
    if (streamingTurns.value[notice.runId]) {
      const next = { ...streamingTurns.value }
      delete next[notice.runId]
      streamingTurns.value = next
    }
    if (notice.companionId && stageCues.value[notice.companionId]?.speaking) {
      stageCues.value = {
        ...stageCues.value,
        [notice.companionId]: { ...stageCues.value[notice.companionId]!, speaking: false, updatedAt: Date.now() },
      }
    }
  }
  return false
}

function sourcesFor(notice: CompanionEventNotice): CompanionSource[] {
  if (notice.type === COMPANION_SETTINGS_EVENT) return ['settings', 'companions']
  const sources: CompanionSource[] = ['activity']
  if (notice.conversationId) sources.push('conversations')
  if (notice.type === COMPANION_RUN_EVENT || notice.kind === 'message_sent' || notice.kind?.startsWith('autonomy_')) {
    sources.push('companions')
  }
  return sources
}

function onNotice(notice: CompanionEventNotice): void {
  const inMemoryOnly = applyLiveStreamNotice(notice)
  if (stopped || subscribers === 0 || inMemoryOnly) return
  pendingSources = new Set([...(pendingSources ?? []), ...sourcesFor(notice)])
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => {
    noticeTimer = null
    const only = pendingSources ? [...pendingSources] : []
    pendingSources = null
    if (stopped || !visible()) return
    void refreshCompanionSources(only)
  }, NOTICE_DEBOUNCE_MS)
}

function schedule(): void {
  if (stopped || subscribers === 0) return
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => {
    timer = null
    if (!stopped && visible()) void refreshCompanionCenter()
    schedule()
  }, COMPANION_CENTER_FALLBACK_MS)
}

function onVisibilityChange(): void {
  if (!visible() || subscribers === 0) return
  void refreshCompanionCenter()
  schedule()
}

function startFeed(): void {
  if (unsubscribeFeed || !companionStreamSupported() || !canAccessBackend.value) return
  unsubscribeFeed = subscribeCompanionFeed(onNotice)
}

function stopFeed(): void {
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = null
  pendingSources = null
}

function disposeFeed(): void {
  stopFeed()
  unsubscribeFeed?.()
  unsubscribeFeed = null
}

function isMainWindow(): boolean {
  if (typeof window === 'undefined') return false
  const path = window.location.hash.split('?')[0]
  return path !== '#/companion' && path !== '#/pet' && path !== '#/panel'
}

if (isMainWindow()) {
  watch(canAccessBackend, (ready) => {
    if (ready) { startFeed(); return }
    unsubscribeFeed?.()
    unsubscribeFeed = null
  }, { immediate: true })
}

export function selectCompanion(id: string | null): void {
  selectedCompanionId.value = id
}

export function selectConversation(id: string | null): void {
  selectedConversationId.value = id
}

export function setFeedFilter(next: CompanionFeedFilter): void {
  feedFilter.value = next
}

export function dismissProposalNotice(proposalId: string): void {
  liveProposals.value = liveProposals.value.filter((item) => item.proposalId !== proposalId)
}

export const selectedCompanion: ComputedRef<CompanionSummary | null> = computed(() =>
  companions.value.find((item) => item.id === selectedCompanionId.value) ?? null)

export const activeCompanions: ComputedRef<CompanionSummary[]> = computed(() =>
  companions.value.filter((item) => item.status !== 'archived'))

export const autonomousCompanions: ComputedRef<CompanionSummary[]> = computed(() =>
  activeCompanions.value.filter((item) => item.role !== 'session_partner'))

export const mainCompanion: ComputedRef<CompanionSummary | null> = computed(() => {
  const id = settings.value?.main_companion_id
  if (!id) return null
  return companions.value.find((item) => item.id === id && item.status !== 'archived') ?? null
})

export const maxBotHopDepth: ComputedRef<number> = computed(() =>
  typeof settings.value?.max_bot_hop_depth === 'number' ? settings.value.max_bot_hop_depth : 3)

export const groupConversations: ComputedRef<CompanionConversation[]> = computed(() =>
  conversations.value.filter((item) => item.kind === 'group'))

function workSessionPriority(status: string | null | undefined): number {
  const normalized = (status ?? '').toLowerCase()
  if (normalized === 'awaiting_approval' || normalized === 'browser_handoff') return 0
  if (normalized === 'running' || normalized === 'in_progress' || normalized === 'queued' || normalized === 'pending' || normalized === 'starting' || normalized === 'thinking' || normalized === 'planning' || normalized === 'executing') return 1
  if (normalized === 'failed') return 2
  if (normalized === 'completed') return 3
  return 4
}

export const selectedCompanionWorkSessions: ComputedRef<CompanionConnectedWorkSession[]> = computed(() => {
  const id = selectedCompanionId.value
  if (!id) return []
  const rows = workSessionsByCompanion.value[id] ?? []
  return rows
    .map((row) => liveWorkSessions.value[row.session_id] ?? row)
    .sort((left, right) => {
      const pDiff = workSessionPriority(left.latest_run_status) - workSessionPriority(right.latest_run_status)
      if (pDiff !== 0) return pDiff
      const a = left.updated_at ? Date.parse(left.updated_at) : 0
      const b = right.updated_at ? Date.parse(right.updated_at) : 0
      return (Number.isFinite(b) ? b : 0) - (Number.isFinite(a) ? a : 0)
    })
})

export function useCompanionCenter() {
  onMounted(() => {
    subscribers++
    if (subscribers === 1) {
      loading.value = loading.value && loadedAt.value === null
      void refreshCompanionCenter()
      startFeed()
      schedule()
      if (typeof document !== 'undefined') document.addEventListener('visibilitychange', onVisibilityChange)
    }
  })
  onBeforeUnmount(() => {
    subscribers = Math.max(0, subscribers - 1)
    if (subscribers > 0) return
    stopped = true
    generation++
    stopFeed()
    if (timer) clearTimeout(timer)
    timer = null
    if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', onVisibilityChange)
    stopped = false
  })
  return {
    companions,
    activeCompanions,
    autonomousCompanions,
    mainCompanion,
    activity,
    conversations,
    groupConversations,
    connections,
    settings,
    maxBotHopDepth,
    selectedCompanionId,
    selectedCompanion,
    selectedConversationId,
    streamingTurns,
    stageCues,
    liveProposals,
    workSessionsByCompanion,
    liveWorkSessions,
    workSessionsLoading,
    workSessionBackflows,
    selectedCompanionWorkSessions,
    refreshWorkSessions,
    feedFilter,
    loading,
    failures,
    loadedAt,
    refresh: refreshCompanionCenter,
  }
}

export function resetCompanionStore(): void {
  companions.value = []
  activity.value = []
  conversations.value = []
  connections.value = []
  settings.value = null
  selectedCompanionId.value = null
  selectedConversationId.value = null
  streamingTurns.value = {}
  stageCues.value = {}
  liveProposals.value = []
  workSessionsByCompanion.value = {}
  liveWorkSessions.value = {}
  workSessionsLoading.value = {}
  workSessionBackflows.value = []
  loading.value = true
  failures.value = {}
  loadedAt.value = null
  feedFilter.value = 'all'
  subscribers = 0
  generation++
  disposeFeed()
  if (timer) clearTimeout(timer)
  timer = null
  inflight = null
  queuedSources.clear()
}
