<script setup lang="ts">
import {
  Check,
  ChevronDown,
  ExternalLink,
  Globe,
  Loader2,
  MessageSquare,
  Minimize2,
  Pause,
  PawPrint,
  Play,
  Send,
  ShieldAlert,
  Sparkles,
  Square,
  UserPlus,
  Users,
  X,
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  COMPANION_ACTIVITY_EVENT,
  COMPANION_DELTA_EVENT,
  COMPANION_RUN_EVENT,
  COMPANION_SETTINGS_EVENT,
  COMPANION_STAGE_CUE_EVENT,
  COMPANION_WORK_SESSION_EVENT,
  type CompanionEventNotice,
} from '@/events/companionEventTypes'
import { ApiError, apiErrorMessage } from '@/lib/apiError'
import { formatClock } from '@/lib/conversation/format'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import CompanionStage from '@/components/pets/CompanionStage.vue'
import { PET_STATES, type PetState } from '@/pets/petRuntime'
import {
  companionBridge,
  subscribeCompanionEventRelay,
  type CompanionConversation,
  type CompanionMessage,
  type CompanionRun,
  type CompanionSettings,
  type CompanionSummary,
} from '@/lib/companionApi'
import {
  normalizeConnectedWorkSession,
  parseWorkSessionPlanProgress,
  type CompanionConnectedWorkSession,
  type CompanionWorkSessionPendingApproval,
} from '@/lib/companionWorkSession'
import { useNotifications } from '@/composables/useNotifications'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useHostAccess } from '@/lib/hostAccess'

/**
 * Floating Companion Capsule & Island Panel:
 * Pure 2-state widget (`capsule` <-> `panel`) with free 2D multi-display drag + magnetic edge snap,
 * unified top-bar identity popover, live `#37` work-session approval/cancel/jump strip, and
 * BroadcastChannel-relayed SSE updates (zero polling timers).
 */

interface CompanionMainSurfacePayload {
  target: 'companion-center' | 'session'
  companionId?: string
  sessionId?: string
}

type CompanionHostBridge = Partial<CompanionApi> & {
  dragMove?: (screenX: number, screenY: number) => Promise<unknown>
  dragEnd?: (screenX: number, screenY: number) => Promise<unknown>
  openMainSurface?: (payload: CompanionMainSurfacePayload) => Promise<unknown>
}

function companionHost(): CompanionHostBridge | undefined {
  if (typeof window === 'undefined') return undefined
  return window.tinadec?.companion as CompanionHostBridge | undefined
}

const { t, locale } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify, dismissByKey } = useNotifications()
const { getPanelDataAttributes } = usePanelStyles()
const materialAttributes = computed(() => getPanelDataAttributes())

const COMPANION_LOAD_KEY = 'companion-load'
const COMPANION_CHAT_LOAD_KEY = 'companion-chat-load'
const COMPANION_SEND_KEY = 'companion-send'
const COMPANION_INVITE_KEY = 'companion-invite'
const COMPANION_WORK_SESSION_KEY = 'companion-work-session'
const state = ref<CompanionState | null>(null)
const dragging = ref(false)
const panelRef = ref<HTMLElement | null>(null)
const popoverWrapRef = ref<HTMLElement | null>(null)

// Pure 2-state footprint: coerce any legacy `'immersive'` snapshot cleanly into `'panel'`.
const mode = computed<'capsule' | 'panel'>(() => {
  const raw = state.value?.mode
  if (raw === 'panel' || raw === 'immersive') return 'panel'
  if (raw === 'capsule') return 'capsule'
  return state.value?.expanded === true ? 'panel' : 'capsule'
})
const expanded = computed(() => mode.value === 'panel')
const side = computed<'left' | 'right'>(() => state.value?.side ?? 'right')

const DRAG_THRESHOLD_PX = 5
const CLICK_SUPPRESS_MS = 350
const NOTICE_DEBOUNCE_MS = 120
const SELECTED_COMPANION_KEY = 'tinadec.companion.selected'

interface PointerDragSession {
  pointerId: number
  startX: number
  startY: number
  offsetX: number
  offsetY: number
  windowTop: number
  moved: boolean
}

let capsuleDrag: PointerDragSession | null = null
let panelDrag: PointerDragSession | null = null
let suppressClickUntil = 0
let disposeState: (() => void) | undefined

function isCompanionState(value: unknown): value is CompanionState {
  return Boolean(value && typeof value === 'object' && 'open' in value)
}

function applyState(next: unknown): void {
  if (isCompanionState(next)) state.value = next
}

function command(action: () => Promise<unknown> | undefined, label: string): void {
  const promise = action()
  if (!promise) return
  promise
    .then(applyState)
    .catch((error) => console.warn(`[companion] ${label} failed:`, error))
}

const expand = () => command(() => companionHost()?.expand?.(), 'expand')
const collapse = () => {
  menuOpen.value = false
  inviteOpen.value = false
  command(() => companionHost()?.collapse?.(), 'collapse')
}

function sendDragMove(screenX: number, screenY: number, fallbackTop: number): void {
  const host = companionHost()
  if (typeof host?.dragMove === 'function') {
    host.dragMove(screenX, screenY).catch((error) => console.warn('[companion] dragMove failed:', error))
    return
  }
  host?.moveTo?.(fallbackTop).catch((error) => console.warn('[companion] moveTo failed:', error))
}

function sendDragEnd(screenX: number, screenY: number): void {
  const host = companionHost()
  if (typeof host?.dragEnd === 'function') {
    host.dragEnd(screenX, screenY)
      .then(applyState)
      .catch((error) => console.warn('[companion] dragEnd failed:', error))
  }
}

function centerOffsetFor(element: HTMLElement | null, event: PointerEvent): { offsetX: number; offsetY: number } {
  const rect = element?.getBoundingClientRect?.()
  if (!rect || rect.width <= 0 || rect.height <= 0) {
    return { offsetX: 0, offsetY: 0 }
  }
  return {
    offsetX: (rect.left + rect.width / 2) - event.clientX,
    offsetY: (rect.top + rect.height / 2) - event.clientY,
  }
}

function capturePointerSafe(target: EventTarget | null, pointerId: number): void {
  try {
    ;(target as HTMLElement | null)?.setPointerCapture?.(pointerId)
  } catch { /* synthetic events may not register an active pointer id */ }
}

function releasePointerSafe(target: EventTarget | null, pointerId: number): void {
  try {
    ;(target as HTMLElement | null)?.releasePointerCapture?.(pointerId)
  } catch { /* ignore */ }
}

function beginCapsuleDrag(event: PointerEvent): void {
  if (event.button !== 0) return
  const target = event.currentTarget as HTMLElement | null
  capturePointerSafe(target, event.pointerId)
  const { offsetX, offsetY } = centerOffsetFor(target, event)
  capsuleDrag = {
    pointerId: event.pointerId,
    startX: event.screenX,
    startY: event.screenY,
    offsetX,
    offsetY,
    windowTop: window.screenY,
    moved: false,
  }
}

function moveCapsuleDrag(event: PointerEvent): void {
  const drag = capsuleDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  const dx = event.screenX - drag.startX
  const dy = event.screenY - drag.startY
  if (!drag.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD_PX) return
  drag.moved = true
  dragging.value = true
  sendDragMove(
    Math.round(event.screenX + drag.offsetX),
    Math.round(event.screenY + drag.offsetY),
    drag.windowTop + dy,
  )
}

function endCapsuleDrag(event: PointerEvent): void {
  const drag = capsuleDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  capsuleDrag = null
  dragging.value = false
  releasePointerSafe(event.currentTarget, event.pointerId)

  const dx = event.screenX - drag.startX
  const dy = event.screenY - drag.startY
  const wasDrag = drag.moved || Math.hypot(dx, dy) >= DRAG_THRESHOLD_PX

  if (wasDrag) {
    suppressClickUntil = performance.now() + CLICK_SUPPRESS_MS
    sendDragEnd(
      Math.round(event.screenX + drag.offsetX),
      Math.round(event.screenY + drag.offsetY),
    )
    return
  }

  if (event.type === 'pointerup') {
    suppressClickUntil = performance.now() + CLICK_SUPPRESS_MS
    expand()
  }
}

function onCapsuleClick(event: MouseEvent): void {
  // Keyboard activation always reports detail === 0 and must expand regardless of drag suppression.
  if (event.detail !== 0 && performance.now() < suppressClickUntil) return
  expand()
}

function beginPanelDrag(event: PointerEvent): void {
  if (event.button !== 0) return
  const target = event.currentTarget as HTMLElement | null
  capturePointerSafe(target, event.pointerId)
  const { offsetX, offsetY } = centerOffsetFor(panelRef.value, event)
  panelDrag = {
    pointerId: event.pointerId,
    startX: event.screenX,
    startY: event.screenY,
    offsetX,
    offsetY,
    windowTop: window.screenY,
    moved: false,
  }
}

function movePanelDrag(event: PointerEvent): void {
  const drag = panelDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  const dx = event.screenX - drag.startX
  const dy = event.screenY - drag.startY
  if (!drag.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD_PX) return
  if (!drag.moved) menuOpen.value = false
  drag.moved = true
  dragging.value = true
  sendDragMove(
    Math.round(event.screenX + drag.offsetX),
    Math.round(event.screenY + drag.offsetY),
    drag.windowTop + dy,
  )
}

function endPanelDrag(event: PointerEvent): void {
  const drag = panelDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  panelDrag = null
  dragging.value = false
  releasePointerSafe(event.currentTarget, event.pointerId)
  if (!drag.moved) return
  sendDragEnd(
    Math.round(event.screenX + drag.offsetX),
    Math.round(event.screenY + drag.offsetY),
  )
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || mode.value === 'capsule') return
  event.preventDefault()
  if (inviteOpen.value) {
    inviteOpen.value = false
    return
  }
  if (menuOpen.value) {
    menuOpen.value = false
    return
  }
  collapse()
}

function onDocumentPointerDown(event: PointerEvent): void {
  if (!menuOpen.value) return
  const target = event.target as Node | null
  if (popoverWrapRef.value && target && popoverWrapRef.value.contains(target)) return
  menuOpen.value = false
}

// ── Satellite chat, work-session supervision & SSE relay state ────────

const companions = ref<CompanionSummary[]>([])
const selectedCompanionId = ref<string | null>(null)
const conversations = ref<CompanionConversation[]>([])
const activeConversation = ref<CompanionConversation | null>(null)
const messages = ref<CompanionMessage[]>([])
const runs = ref<Record<string, CompanionRun>>({})
const trackedRunIds = ref<string[]>([])
const liveDeltaBodies = ref<Record<string, string>>({})
const liveStageSpeaking = ref(false)
const liveEmotion = ref<string | null>(null)
const drafts = reactive<Record<string, string>>({})
const unreadCount = ref(0)

// #37 Connected work sessions hydrated via companion proxy + live BroadcastChannel notices.
const workSessions = ref<CompanionConnectedWorkSession[]>([])
const decidingApprovalIds = ref<Record<string, boolean>>({})
const cancellingRunIds = ref<Record<string, boolean>>({})

const booting = ref(true)
const bootFailed = ref(false)
const chatLoading = ref(false)
const chatLoadFailed = ref(false)
const sending = ref(false)
const pauseBusy = ref(false)
const menuOpen = ref(false)
const inviteOpen = ref(false)
const inviteTargetId = ref('')
const inviteShareHistory = ref(false)
const inviteBusy = ref(false)
const logRef = ref<HTMLElement | null>(null)
const composerRef = ref<HTMLTextAreaElement | null>(null)

let lastSeq = 0
let loadToken = 0
let workSessionToken = 0
const liveWorkSessionIds = new Set<string>()
let noticeTimer: ReturnType<typeof setTimeout> | undefined
let unsubscribeRelay: (() => void) | null = null
const speakReplies = ref(false)
let disposeSpeakSetting: (() => void) | undefined

const activeCompanion = computed(() => companions.value.find((item) => item.id === selectedCompanionId.value) ?? null)
const draft = computed({
  get: () => (selectedCompanionId.value ? drafts[selectedCompanionId.value] ?? '' : ''),
  set: (value: string) => { if (selectedCompanionId.value) drafts[selectedCompanionId.value] = value },
})
const canSend = computed(() => !sending.value && !!activeConversation.value && draft.value.trim().length > 0)
const headerTitle = computed(() => activeCompanion.value?.name ?? t('companion.title'))
const paused = computed(() => activeConversation.value?.autonomy_paused === true)
const activeStreamingText = computed(() => Object.values(liveDeltaBodies.value).join(''))
const thinking = computed(() =>
  trackedRunIds.value.some((id) => {
    const run = runs.value[id]
    return run != null && (run.status === 'queued' || run.status === 'running')
  }) || Boolean(activeStreamingText.value),
)
const failures = computed(() => trackedRunIds.value
  .map((id) => runs.value[id])
  .filter((run): run is CompanionRun => run != null && run.status === 'failed'))
const companionPetSlug = computed(() => activeCompanion.value?.avatar ?? '')

// ── #37 Work-session classification ───────────────────────────────────

const ACTIVE_WORK_STATUSES = new Set([
  'running',
  'queued',
  'pending',
  'starting',
  'in_progress',
  'thinking',
  'planning',
  'executing',
])

function workSessionStatus(session: CompanionConnectedWorkSession): string {
  return (session.latest_run_status ?? 'idle').toLowerCase()
}

function workSessionNeedsApproval(session: CompanionConnectedWorkSession): boolean {
  return session.pending_approvals.length > 0 || workSessionStatus(session) === 'awaiting_approval'
}

function workSessionIsHandoff(session: CompanionConnectedWorkSession): boolean {
  return workSessionStatus(session) === 'browser_handoff'
}

function workSessionIsActive(session: CompanionConnectedWorkSession): boolean {
  return ACTIVE_WORK_STATUSES.has(workSessionStatus(session))
}

const featuredWorkSession = computed<CompanionConnectedWorkSession | null>(() => {
  const list = workSessions.value
  if (!list.length) return null
  return list.find(workSessionNeedsApproval)
    ?? list.find(workSessionIsHandoff)
    ?? list.find(workSessionIsActive)
    ?? null
})

const featuredApproval = computed<CompanionWorkSessionPendingApproval | null>(() =>
  featuredWorkSession.value?.pending_approvals[0] ?? null)

const featuredExtraApprovals = computed(() =>
  Math.max(0, (featuredWorkSession.value?.pending_approvals.length ?? 0) - 1))

const featuredWorkTone = computed<'warning' | 'running'>(() => {
  const ws = featuredWorkSession.value
  if (!ws) return 'running'
  return workSessionNeedsApproval(ws) || workSessionIsHandoff(ws) ? 'warning' : 'running'
})

const featuredWorkStatusLabel = computed(() => {
  const ws = featuredWorkSession.value
  if (!ws) return ''
  if (workSessionNeedsApproval(ws)) return t('companionCenter.workSession.statusAwaitingApproval')
  if (workSessionIsHandoff(ws)) return t('companionCenter.workSession.statusBrowserHandoff')
  const status = workSessionStatus(ws)
  if (status === 'queued' || status === 'pending') return t('companionCenter.workSession.statusQueued')
  return t('companionCenter.workSession.statusRunning')
})

const featuredWorkDetail = computed(() => {
  const ws = featuredWorkSession.value
  if (!ws) return ''
  if (workSessionIsHandoff(ws)) {
    return ws.browser_handoff_reason || ws.browser_url || t('companionCenter.workSession.browserHandoffTitle')
  }
  const progress = ws.plan_progress ?? parseWorkSessionPlanProgress(ws.plan_summary)
  if (progress?.current_step) {
    return progress.total > 0 ? `${progress.done}/${progress.total} · ${progress.current_step}` : progress.current_step
  }
  return ws.latest_run_summary || ''
})

const canCancelFeaturedWorkRun = computed(() => {
  const ws = featuredWorkSession.value
  if (!ws || !ws.latest_run_id) return false
  return workSessionNeedsApproval(ws) || workSessionIsHandoff(ws) || workSessionIsActive(ws)
})

// Status dot on capsule appears ONLY for active / attention / error states (never when idle).
type CapsuleAlertState = 'awaiting_approval' | 'browser_handoff' | 'failed' | 'thinking' | null

const capsuleAlertState = computed<CapsuleAlertState>(() => {
  const ws = featuredWorkSession.value
  if (ws && workSessionNeedsApproval(ws)) return 'awaiting_approval'
  if (ws && workSessionIsHandoff(ws)) return 'browser_handoff'
  if (failures.value.length > 0) return 'failed'
  if (thinking.value || (ws && workSessionIsActive(ws))) return 'thinking'
  return null
})

const unreadDisplay = computed(() => (unreadCount.value > 9 ? '9+' : String(unreadCount.value)))

const observedStageState = computed<PetState>(() => {
  if (liveStageSpeaking.value) return 'running'
  if (capsuleAlertState.value === 'thinking') return 'waiting'
  if (capsuleAlertState.value === 'failed') return 'failed'
  return 'idle'
})

const impulse = ref<PetState | null>(null)
const patNote = ref('')
let impulseTimer: ReturnType<typeof setTimeout> | undefined
const stageState = computed<PetState>(() => impulse.value ?? observedStageState.value)

function playImpulse(next: PetState, note = ''): void {
  impulse.value = next
  patNote.value = note
  clearTimeout(impulseTimer)
  impulseTimer = setTimeout(() => {
    impulse.value = null
    patNote.value = ''
  }, PET_STATES[next].durationMs)
}

function pat(event: MouseEvent): void {
  const target = event.target as HTMLElement | null
  if (target?.closest('.companion-msg, button, a, input, textarea, .companion-invite, .companion-popover, .companion-ws-strip')) return
  playImpulse('jumping', t('companion.pat'))
  if (selectedCompanionId.value) void companionBridge.recordEmote(selectedCompanionId.value, 'pat').catch(() => {})
}

const inviteCandidates = computed(() => {
  const memberIds = new Set(
    (activeConversation.value?.members ?? [])
      .map((member) => member.companion_id)
      .filter((id): id is string => typeof id === 'string'),
  )
  return companions.value.filter((item) => item.id !== selectedCompanionId.value && !memberIds.has(item.id))
})

function isHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden'
}

function reportCompanionError(
  error: unknown,
  key: string,
  action?: { label: string; run: () => void | Promise<void> },
): void {
  if (error instanceof ApiError && error.code === 'desktop_host_required') {
    notify.error(t('companion.chat.desktopHostRequired'), {
      title: t('companion.title'),
      key,
      ...(action ? { action } : {}),
    })
    return
  }
  notify.error(error, {
    title: t('companion.title'),
    key,
    ...(action ? { action } : {}),
  })
}

function runFailureText(run: CompanionRun): string {
  const code = run.error_code ?? ''
  const message = run.error_message ?? ''
  if (code === 'companion_model_unavailable') return t('companion.chat.modelUnavailable')
  if (code && message) return t('companion.chat.runFailed', { code, message })
  if (code) return t('companion.chat.runFailedCode', { code })
  return message || t('companion.chat.unknownFailure')
}

function authorLabel(message: CompanionMessage): string {
  if (message.author_kind === 'user') return t('companion.chat.you')
  if (message.author_kind === 'system') return t('companion.chat.system')
  const member = message.author_companion_id
    ? activeConversation.value?.members.find((c) => c.member_kind === 'companion' && c.companion_id === message.author_companion_id)
    : undefined
  return member?.display_name || message.author_name
}

const authorInitial = (message: CompanionMessage) => Array.from(authorLabel(message))[0] ?? ''
const authorPetSlug = (message: CompanionMessage) =>
  (message.author_kind === 'companion' && message.author_companion_id
    ? companions.value.find((item) => item.id === message.author_companion_id)?.avatar ?? ''
    : '')

function authorTone(message: CompanionMessage): string {
  if (message.author_kind === 'user') return 'var(--accent-primary)'
  if (message.author_kind === 'system') return 'var(--text-muted)'
  const seed = message.author_companion_id ?? message.author_name
  let hash = 0
  for (const ch of seed) hash = (hash * 31 + ch.charCodeAt(0)) % 360
  return `hsl(${hash} 58% 55%)`
}

const clock = (iso: string | undefined) => (iso ? formatClock(iso, locale.value) : '')
const conversationLabel = (c: CompanionConversation) =>
  (c.kind === 'group' ? c.title || t('companion.chat.groupLabel') : t('companion.chat.privateLabel'))

// ── Companion proxy helpers for #37 work sessions ─────────────────────

async function proxyCall<T>(method: 'GET' | 'POST' | 'PATCH', path: string, body?: unknown): Promise<T> {
  const invoke = companionHost()?.api
  if (typeof invoke !== 'function') {
    throw new ApiError(t('companion.chat.desktopHostRequired'), 403, { code: 'desktop_host_required' })
  }
  const result = await invoke(method, path, body)
  if (result.status < 200 || result.status >= 300) {
    throw new ApiError(apiErrorMessage(result.body, `Companion request failed (${result.status})`), result.status, result.body)
  }
  return result.body as T
}

async function hydrateWorkSessions(companionId: string | null): Promise<void> {
  if (!companionId) {
    workSessions.value = []
    return
  }
  const invoke = companionHost()?.api
  if (typeof invoke !== 'function') return
  const token = ++workSessionToken
  try {
    const raw = await proxyCall<unknown>('GET', `/api/v1/companions/${encodeURIComponent(companionId)}/work-sessions`)
    if (token !== workSessionToken || selectedCompanionId.value !== companionId) return
    const list = Array.isArray(raw)
      ? raw
      : Array.isArray((raw as { sessions?: unknown } | null)?.sessions)
        ? (raw as { sessions: unknown[] }).sessions
        : Array.isArray((raw as { items?: unknown } | null)?.items)
          ? (raw as { items: unknown[] }).items
          : []
    const fetched = list
      .map(normalizeConnectedWorkSession)
      .filter((item): item is CompanionConnectedWorkSession => item !== null)
    const merged = new Map(fetched.map((item) => [item.session_id, item]))
    for (const existing of workSessions.value) {
      if (liveWorkSessionIds.has(existing.session_id) && !merged.has(existing.session_id)) {
        merged.set(existing.session_id, existing)
      }
    }
    workSessions.value = [...merged.values()]
    dismissByKey(COMPANION_WORK_SESSION_KEY)
  } catch {
    // Work-session supervision is progressive enhancement; never block chat on 404/unreachable.
  }
}

function mergeWorkSessionNotice(notice: CompanionEventNotice): void {
  const ws = notice.workSession
  if (!ws) {
    if (selectedCompanionId.value) void hydrateWorkSessions(selectedCompanionId.value)
    return
  }
  const existing = workSessions.value.find((item) => item.session_id === ws.sessionId)
  const normalized: CompanionConnectedWorkSession = {
    session_id: ws.sessionId,
    title: ws.sessionTitle || existing?.title || ws.sessionId.slice(0, 8),
    project_id: ws.projectId ?? existing?.project_id ?? null,
    project_name: ws.projectName ?? existing?.project_name ?? null,
    mode_name: ws.modeName ?? existing?.mode_name ?? null,
    view_mode: ws.viewMode ?? existing?.view_mode ?? null,
    latest_run_id: ws.runId ?? existing?.latest_run_id ?? null,
    latest_run_status: ws.status ?? existing?.latest_run_status ?? null,
    latest_run_summary: ws.summary ?? existing?.latest_run_summary ?? null,
    plan_summary: ws.planSummary ?? existing?.plan_summary ?? null,
    plan_progress: parseWorkSessionPlanProgress(ws.planSummary ?? existing?.plan_summary) ?? existing?.plan_progress ?? null,
    pending_approvals: ws.pendingApprovals,
    browser_space_id: ws.browserSpaceId ?? existing?.browser_space_id ?? null,
    browser_url: ws.browserUrl ?? existing?.browser_url ?? null,
    browser_driver: ws.browserDriver ?? existing?.browser_driver ?? null,
    browser_handoff_reason: ws.browserHandoffReason ?? existing?.browser_handoff_reason ?? null,
    updated_at: ws.updatedAt ?? existing?.updated_at ?? null,
    latest_run: (ws.runId ?? existing?.latest_run_id)
      ? {
          id: (ws.runId ?? existing?.latest_run_id)!,
          status: ws.status ?? existing?.latest_run_status ?? 'running',
          summary: ws.summary ?? existing?.latest_run_summary ?? null,
        }
      : null,
  }
  const index = workSessions.value.findIndex((item) => item.session_id === ws.sessionId)
  liveWorkSessionIds.add(ws.sessionId)
  if (index >= 0) {
    const next = [...workSessions.value]
    next[index] = normalized
    workSessions.value = next
  } else {
    workSessions.value = [normalized, ...workSessions.value]
  }
}

async function decideWorkApproval(item: CompanionWorkSessionPendingApproval, approved: boolean): Promise<void> {
  const ws = featuredWorkSession.value
  if (!ws || decidingApprovalIds.value[item.approval_id]) return
  decidingApprovalIds.value = { ...decidingApprovalIds.value, [item.approval_id]: true }
  dismissByKey(COMPANION_WORK_SESSION_KEY)
  try {
    await proxyCall<unknown>('POST', `/api/v1/approvals/${encodeURIComponent(item.approval_id)}/decision`, {
      decision: approved ? 'approved' : 'rejected',
    })
    workSessions.value = workSessions.value.map((entry) => {
      if (entry.session_id !== ws.session_id) return entry
      const remaining = entry.pending_approvals.filter((a) => a.approval_id !== item.approval_id)
      const nextStatus = remaining.length > 0
        ? 'awaiting_approval'
        : (approved ? 'running' : 'cancelled')
      return {
        ...entry,
        pending_approvals: remaining,
        latest_run_status: nextStatus,
        latest_run: entry.latest_run ? { ...entry.latest_run, status: nextStatus } : null,
      }
    })
  } catch (error) {
    reportCompanionError(error, COMPANION_WORK_SESSION_KEY)
  } finally {
    const next = { ...decidingApprovalIds.value }
    delete next[item.approval_id]
    decidingApprovalIds.value = next
  }
}

async function cancelWorkRun(): Promise<void> {
  const ws = featuredWorkSession.value
  const runId = ws?.latest_run_id
  if (!ws || !runId || cancellingRunIds.value[runId]) return
  cancellingRunIds.value = { ...cancellingRunIds.value, [runId]: true }
  dismissByKey(COMPANION_WORK_SESSION_KEY)
  const clientControlId = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? `companion-cancel-${crypto.randomUUID()}`
    : `companion-cancel-${Date.now()}`
  try {
    await proxyCall<unknown>('POST', `/api/v1/runs/${encodeURIComponent(runId)}/control`, {
      action: 'cancel',
      client_control_id: clientControlId,
    })
    workSessions.value = workSessions.value.map((entry) => {
      if (entry.session_id !== ws.session_id) return entry
      return {
        ...entry,
        pending_approvals: [],
        latest_run_status: 'cancelled',
        latest_run: entry.latest_run ? { ...entry.latest_run, status: 'cancelled' } : null,
      }
    })
  } catch (error) {
    reportCompanionError(error, COMPANION_WORK_SESSION_KEY)
  } finally {
    const next = { ...cancellingRunIds.value }
    delete next[runId]
    cancellingRunIds.value = next
  }
}

function openCompanionCenter(): void {
  menuOpen.value = false
  const payload: CompanionMainSurfacePayload = selectedCompanionId.value
    ? { target: 'companion-center', companionId: selectedCompanionId.value }
    : { target: 'companion-center' }
  void companionHost()?.openMainSurface?.(payload)?.catch((error) => {
    console.warn('[companion] openMainSurface(companion-center) failed:', error)
  })
}

function openWorkSessionInWorkbench(sessionId: string): void {
  void companionHost()?.openMainSurface?.({ target: 'session', sessionId })?.catch((error) => {
    console.warn('[companion] openMainSurface(session) failed:', error)
  })
}

// ── Chat messaging & SSE relay ────────────────────────────────────────

function speakFresh(fresh: CompanionMessage[]): void {
  if (!speakReplies.value || typeof window === 'undefined' || typeof window.speechSynthesis === 'undefined') return
  const body = fresh.filter((m) => m.author_kind === 'companion').at(-1)?.body?.trim()
  if (!body) return
  try {
    window.speechSynthesis.cancel()
    window.speechSynthesis.speak(new SpeechSynthesisUtterance(body))
  } catch { /* speech synthesis optional */ }
}

function mergeMessages(incoming: CompanionMessage[], speak = true, countUnread = false): void {
  if (!incoming.length) return
  const ordered = new Map(messages.value.map((m) => [m.id, m]))
  const fresh: CompanionMessage[] = []
  for (const m of incoming) {
    if (!ordered.has(m.id)) fresh.push(m)
    ordered.set(m.id, m)
    lastSeq = Math.max(lastSeq, m.seq)
  }
  messages.value = [...ordered.values()].sort((a, b) => a.seq - b.seq)
  if (countUnread && mode.value === 'capsule') {
    const companionFresh = fresh.filter((m) => m.author_kind === 'companion').length
    if (companionFresh > 0) unreadCount.value += companionFresh
  }
  if (speak) speakFresh(fresh)
}

function scrollToBottom(): void {
  void nextTick(() => {
    const element = logRef.value
    if (element) element.scrollTop = element.scrollHeight
  })
}

watch(mode, (next) => {
  if (next === 'panel') {
    unreadCount.value = 0
    if (selectedCompanionId.value) void hydrateWorkSessions(selectedCompanionId.value)
    scrollToBottom()
  } else {
    menuOpen.value = false
    inviteOpen.value = false
  }
})

async function loadMessagesFor(conversationId: string, token: number): Promise<void> {
  chatLoading.value = true
  chatLoadFailed.value = false
  dismissByKey(COMPANION_CHAT_LOAD_KEY)
  try {
    const list = await companionBridge.listMessages(conversationId)
    if (token !== loadToken) return
    messages.value = []
    lastSeq = 0
    mergeMessages(list ?? [], false)
    scrollToBottom()
  } catch (error) {
    if (token === loadToken) {
      chatLoadFailed.value = true
      reportCompanionError(error, COMPANION_CHAT_LOAD_KEY, {
        label: t('companion.chat.retry'),
        run: retryChat,
      })
    }
  } finally {
    if (token === loadToken) chatLoading.value = false
  }
}

async function selectCompanion(id: string): Promise<void> {
  const token = ++loadToken
  selectedCompanionId.value = id
  menuOpen.value = false
  try { localStorage.setItem(SELECTED_COMPANION_KEY, id) } catch { /* ignore */ }
  activeConversation.value = null
  conversations.value = []
  messages.value = []
  runs.value = {}
  trackedRunIds.value = []
  liveDeltaBodies.value = {}
  workSessions.value = []
  liveWorkSessionIds.clear()
  lastSeq = 0
  chatLoadFailed.value = false
  dismissByKey(COMPANION_WORK_SESSION_KEY)
  dismissByKey(COMPANION_CHAT_LOAD_KEY)
  dismissByKey(COMPANION_SEND_KEY)
  void hydrateWorkSessions(id)
  try {
    const conversation = await companionBridge.ensurePrivateConversation(id)
    if (token !== loadToken) return
    activeConversation.value = conversation
    const list = await companionBridge.listConversations(id)
    if (token !== loadToken) return
    conversations.value = list.some((item) => item.id === conversation.id) ? list : [conversation, ...list]
    await loadMessagesFor(conversation.id, token)
  } catch (error) {
    if (token === loadToken) {
      chatLoadFailed.value = true
      reportCompanionError(error, COMPANION_CHAT_LOAD_KEY, {
        label: t('companion.chat.retry'),
        run: retryChat,
      })
    }
  }
}

async function adoptConversation(conversation: CompanionConversation): Promise<void> {
  const token = ++loadToken
  menuOpen.value = false
  activeConversation.value = conversation
  messages.value = []
  runs.value = {}
  trackedRunIds.value = []
  liveDeltaBodies.value = {}
  lastSeq = 0
  dismissByKey(COMPANION_SEND_KEY)
  await loadMessagesFor(conversation.id, token)
}

function pickCompanion(settings: CompanionSettings, active: CompanionSummary[]): CompanionSummary | undefined {
  const byId = (id: string | null | undefined) => active.find((item) => item.id === id)
  let savedId: string | null = null
  try { savedId = localStorage.getItem(SELECTED_COMPANION_KEY) } catch { /* ignore */ }
  return active.find((item) => item.role === 'session_partner')
    ?? byId(settings.main_companion_id)
    ?? byId(savedId)
    ?? active[0]
}

async function boot(): Promise<void> {
  booting.value = true
  bootFailed.value = false
  dismissByKey(COMPANION_LOAD_KEY)
  try {
    const [settings, list] = await Promise.all([companionBridge.getSettings(), companionBridge.listCompanions()])
    const active = (list ?? []).filter((item) => item.status === 'active')
    companions.value = active
    const preferred = pickCompanion(settings, active)
    if (preferred) await selectCompanion(preferred.id)
  } catch (error) {
    bootFailed.value = true
    reportCompanionError(error, COMPANION_LOAD_KEY, {
      label: t('companion.chat.retry'),
      run: () => boot(),
    })
  } finally {
    booting.value = false
  }
}

const retryChat = () => {
  if (selectedCompanionId.value) void selectCompanion(selectedCompanionId.value)
  else void boot()
}

function pickCompanionFromMenu(id: string): void {
  menuOpen.value = false
  if (id && id !== selectedCompanionId.value) void selectCompanion(id)
}

function pickConversationFromMenu(conversation: CompanionConversation): void {
  menuOpen.value = false
  if (conversation.id !== activeConversation.value?.id) void adoptConversation(conversation)
}

watch(draft, () => {
  void nextTick(() => {
    const el = composerRef.value
    if (!el) return
    el.style.height = 'auto'
    el.style.height = `${Math.min(el.scrollHeight, 96)}px`
  })
})

function onComposerKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  void send()
}

async function send(): Promise<void> {
  const body = draft.value.trim()
  const conversation = activeConversation.value
  if (!body || !conversation || sending.value) return
  const companionId = selectedCompanionId.value
  sending.value = true
  dismissByKey(COMPANION_SEND_KEY)
  let key: string | undefined
  try { key = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function' ? crypto.randomUUID() : undefined } catch { /* ignore */ }
  try {
    const result = await companionBridge.sendMessage(conversation.id, { body, channel_kind: 'session_partner' }, key)
    if (selectedCompanionId.value === companionId && companionId) drafts[companionId] = ''
    if (activeConversation.value?.id !== conversation.id) return
    mergeMessages([result.message])
    for (const run of result.runs ?? []) runs.value = { ...runs.value, [run.id]: run }
    trackedRunIds.value = [...new Set([...trackedRunIds.value, ...(result.runs ?? []).map((run) => run.id)])]
    scrollToBottom()
  } catch (error) {
    reportCompanionError(error, COMPANION_SEND_KEY)
  } finally {
    sending.value = false
  }
}

function onComposerAction(): void {
  if (thinking.value) void togglePause()
  else void send()
}

async function catchUp(conversationId: string): Promise<void> {
  if (activeConversation.value?.id !== conversationId) return
  try {
    const incoming = await companionBridge.listMessages(conversationId, lastSeq)
    if (activeConversation.value?.id !== conversationId) return
    mergeMessages(incoming ?? [], true, true)
    await Promise.all(trackedRunIds.value.map(async (id) => {
      try { runs.value = { ...runs.value, [id]: await companionBridge.getRun(id) } } catch { /* ignore */ }
    }))
    scrollToBottom()
  } catch (error) {
    console.warn('[companion] catchUp failed:', error)
  }
}

function onCompanionNotice(notice: CompanionEventNotice): void {
  if (notice.type === COMPANION_WORK_SESSION_EVENT) {
    if (!notice.companionId || notice.companionId === selectedCompanionId.value) {
      mergeWorkSessionNotice(notice)
    }
    return
  }
  if (notice.type === COMPANION_STAGE_CUE_EVENT && notice.companionId === selectedCompanionId.value) {
    if (typeof notice.speaking === 'boolean') liveStageSpeaking.value = notice.speaking
    if (notice.emotion !== undefined) liveEmotion.value = notice.emotion ?? null
    return
  }
  if (notice.type === COMPANION_DELTA_EVENT) {
    if (notice.conversationId === activeConversation.value?.id && notice.runId) {
      liveDeltaBodies.value = {
        ...liveDeltaBodies.value,
        [notice.runId]: (liveDeltaBodies.value[notice.runId] ?? '') + (notice.delta ?? ''),
      }
      scrollToBottom()
    }
    return
  }
  if (notice.type === COMPANION_SETTINGS_EVENT) {
    void boot()
    return
  }
  if (notice.type === COMPANION_RUN_EVENT && notice.runId && (notice.kind === 'run_completed' || notice.kind === 'run_failed')) {
    if (liveDeltaBodies.value[notice.runId]) {
      const next = { ...liveDeltaBodies.value }
      delete next[notice.runId]
      liveDeltaBodies.value = next
    }
    liveStageSpeaking.value = false
  }
  if (
    notice.type === COMPANION_ACTIVITY_EVENT
    && notice.kind === 'message_sent'
    && mode.value === 'capsule'
    && (!notice.companionId || notice.companionId === selectedCompanionId.value)
    && (!notice.conversationId || notice.conversationId !== activeConversation.value?.id)
  ) {
    unreadCount.value += 1
  }
  if (notice.conversationId && notice.conversationId !== activeConversation.value?.id) return
  if (isHidden()) return
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => {
    noticeTimer = undefined
    if (activeConversation.value) void catchUp(activeConversation.value.id)
  }, NOTICE_DEBOUNCE_MS)
}

function onVisibilityChange(): void {
  if (!isHidden() && activeConversation.value) void catchUp(activeConversation.value.id)
}

async function togglePause(): Promise<void> {
  const conversation = activeConversation.value
  if (!conversation || pauseBusy.value) return
  pauseBusy.value = true
  dismissByKey(COMPANION_SEND_KEY)
  try {
    const updated = await companionBridge.pauseConversation(conversation.id, !conversation.autonomy_paused)
    activeConversation.value = updated
    conversations.value = conversations.value.map((item) => (item.id === updated.id ? updated : item))
  } catch (error) {
    reportCompanionError(error, COMPANION_SEND_KEY)
  } finally {
    pauseBusy.value = false
  }
}

function openInvite(): void {
  menuOpen.value = false
  inviteOpen.value = true
  dismissByKey(COMPANION_INVITE_KEY)
  inviteShareHistory.value = false
  inviteTargetId.value = inviteCandidates.value[0]?.id ?? ''
}

async function confirmInvite(): Promise<void> {
  const conversation = activeConversation.value
  const target = inviteTargetId.value
  if (!conversation || !target || inviteBusy.value) return
  inviteBusy.value = true
  dismissByKey(COMPANION_INVITE_KEY)
  try {
    const result = await companionBridge.inviteMember(conversation.id, {
      companion_id: target,
      share_history: inviteShareHistory.value,
    })
    inviteOpen.value = false
    if (selectedCompanionId.value) conversations.value = await companionBridge.listConversations(selectedCompanionId.value)
    await adoptConversation(result)
    if (!conversations.value.some((item) => item.id === result.id)) conversations.value = [result, ...conversations.value]
  } catch (error) {
    reportCompanionError(error, COMPANION_INVITE_KEY)
  } finally {
    inviteBusy.value = false
  }
}

watch(canAccessBackend, (ready, wasReady) => {
  if (ready && wasReady === false) {
    dismissByKey(COMPANION_LOAD_KEY)
    dismissByKey(COMPANION_CHAT_LOAD_KEY)
    dismissByKey(COMPANION_WORK_SESSION_KEY)
    if (bootFailed.value || companions.value.length === 0) {
      void boot()
    } else if (chatLoadFailed.value) {
      retryChat()
    } else if (selectedCompanionId.value) {
      void hydrateWorkSessions(selectedCompanionId.value)
    }
  }
})

onMounted(async () => {
  window.addEventListener('keydown', onKeydown)
  document.addEventListener('pointerdown', onDocumentPointerDown)
  document.addEventListener('visibilitychange', onVisibilityChange)
  disposeState = companionHost()?.onStateChanged?.(applyState)
  unsubscribeRelay = subscribeCompanionEventRelay(onCompanionNotice)
  void window.tinadec?.getAppConfig?.().then((config) => { speakReplies.value = config.companion_speak_replies === true }).catch(() => {})
  disposeSpeakSetting = window.tinadec?.onCompanionSpeakRepliesChanged?.(({ speak_replies }) => {
    speakReplies.value = speak_replies === true
  })
  try {
    const initial = await companionHost()?.getState?.()
    if (!state.value) applyState(initial)
  } catch (error) {
    console.warn('[companion] state read failed:', error)
  }
  void boot()
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  document.removeEventListener('pointerdown', onDocumentPointerDown)
  document.removeEventListener('visibilitychange', onVisibilityChange)
  dismissByKey(COMPANION_LOAD_KEY)
  dismissByKey(COMPANION_CHAT_LOAD_KEY)
  dismissByKey(COMPANION_SEND_KEY)
  dismissByKey(COMPANION_INVITE_KEY)
  dismissByKey(COMPANION_WORK_SESSION_KEY)
  clearTimeout(impulseTimer)
  if (noticeTimer) clearTimeout(noticeTimer)
  unsubscribeRelay?.()
  disposeSpeakSetting?.()
  if (typeof window !== 'undefined' && typeof window.speechSynthesis !== 'undefined') {
    try { window.speechSynthesis.cancel() } catch { /* ignore */ }
  }
  disposeState?.()
})
</script>

<template>
  <div
    class="companion"
    :class="{
      'companion--expanded': expanded,
      'companion--dragging': dragging,
      'companion--side-left': side === 'left',
      'companion--side-right': side === 'right',
    }"
    v-bind="materialAttributes"
  >
    <button
      v-if="mode === 'capsule'"
      class="companion-capsule"
      :class="`companion-capsule--${side}`"
      type="button"
      :title="t('companion.expand')"
      :aria-label="t('companion.expand')"
      :aria-expanded="false"
      data-testid="companion-capsule"
      @pointerdown="beginCapsuleDrag"
      @pointermove="moveCapsuleDrag"
      @pointerup="endCapsuleDrag"
      @pointercancel="endCapsuleDrag"
      @click="onCapsuleClick"
    >
      <span class="companion-capsule-pill" aria-hidden="true">
        <CompanionStage
          v-if="companionPetSlug"
          class="companion-capsule-avatar"
          :pet-slug="companionPetSlug"
          :state="stageState"
          :speaking="liveStageSpeaking"
          :emotion="liveEmotion"
          :height="42"
          :alt="activeCompanion?.name ?? ''"
        />
        <span v-else class="companion-capsule-glyph"><PawPrint :size="22" /></span>
        <span
          v-if="capsuleAlertState"
          class="companion-capsule-status"
          :class="`is-${capsuleAlertState}`"
          data-testid="companion-capsule-status"
        />
        <span
          v-if="unreadCount > 0"
          class="companion-capsule-unread"
          :aria-label="t('companion.unreadBadge', { count: unreadCount })"
          data-testid="companion-capsule-unread"
        >{{ unreadDisplay }}</span>
      </span>
    </button>

    <section
      v-else
      ref="panelRef"
      class="companion-panel"
      role="region"
      :aria-label="t('companion.title')"
      data-testid="companion-panel"
    >
      <header
        class="companion-topbar"
        :title="t('companion.dragHint')"
        data-testid="companion-topbar"
        @pointerdown="beginPanelDrag"
        @pointermove="movePanelDrag"
        @pointerup="endPanelDrag"
        @pointercancel="endPanelDrag"
      >
        <div ref="popoverWrapRef" class="companion-identity-wrap" @pointerdown.stop>
          <button
            class="companion-identity-pill"
            type="button"
            :title="t('companion.switchMenu')"
            :aria-label="t('companion.switchMenu')"
            :aria-expanded="menuOpen"
            aria-haspopup="menu"
            data-testid="companion-identity-trigger"
            @click="menuOpen = !menuOpen"
          >
            <AgentPetAvatar
              v-if="companionPetSlug"
              class="companion-identity-avatar"
              :pet-slug="companionPetSlug"
              :size="24"
              :alt="headerTitle"
            />
            <span v-else class="companion-header-glyph" aria-hidden="true"><PawPrint :size="14" /></span>
            <span class="companion-title">{{ headerTitle }}</span>
            <span
              v-if="capsuleAlertState"
              class="companion-identity-dot"
              :class="`is-${capsuleAlertState}`"
              aria-hidden="true"
            />
            <ChevronDown class="companion-identity-caret" :class="{ 'is-open': menuOpen }" :size="13" aria-hidden="true" />
          </button>

          <div
            v-if="menuOpen"
            class="companion-popover"
            role="menu"
            data-testid="companion-popover"
          >
            <div v-if="companions.length > 0" class="companion-popover-section">
              <p class="companion-popover-heading">{{ t('companion.chat.companionPicker') }}</p>
              <button
                v-for="item in companions"
                :key="item.id"
                class="companion-menu-item"
                :class="{ 'is-active': item.id === selectedCompanionId }"
                type="button"
                role="menuitem"
                :data-testid="`companion-menu-pick-${item.id}`"
                @click="pickCompanionFromMenu(item.id)"
              >
                <AgentPetAvatar
                  v-if="item.avatar"
                  :pet-slug="item.avatar"
                  :size="18"
                  :alt="item.name"
                />
                <span v-else class="companion-menu-glyph" aria-hidden="true"><PawPrint :size="12" /></span>
                <span class="companion-menu-label">{{ item.name }}</span>
                <Check v-if="item.id === selectedCompanionId" :size="13" class="companion-menu-check" aria-hidden="true" />
              </button>
            </div>

            <div v-if="conversations.length > 0" class="companion-popover-section">
              <p class="companion-popover-heading">{{ t('companion.chat.conversationPicker') }}</p>
              <button
                v-for="conv in conversations"
                :key="conv.id"
                class="companion-menu-item"
                :class="{ 'is-active': conv.id === activeConversation?.id }"
                type="button"
                role="menuitem"
                :data-testid="`companion-menu-thread-${conv.id}`"
                @click="pickConversationFromMenu(conv)"
              >
                <Users v-if="conv.kind === 'group'" :size="13" class="companion-menu-icon" aria-hidden="true" />
                <MessageSquare v-else :size="13" class="companion-menu-icon" aria-hidden="true" />
                <span class="companion-menu-label">{{ conversationLabel(conv) }}</span>
                <Check v-if="conv.id === activeConversation?.id" :size="13" class="companion-menu-check" aria-hidden="true" />
              </button>
            </div>

            <div class="companion-popover-section">
              <p class="companion-popover-heading">{{ t('companion.actionsSection') }}</p>
              <button
                class="companion-menu-item"
                type="button"
                role="menuitem"
                :disabled="!activeConversation || pauseBusy"
                data-testid="companion-menu-pause"
                @click="menuOpen = false; togglePause()"
              >
                <Play v-if="paused" :size="13" class="companion-menu-icon" aria-hidden="true" />
                <Pause v-else :size="13" class="companion-menu-icon" aria-hidden="true" />
                <span class="companion-menu-label">{{ paused ? t('companion.chat.resume') : t('companion.chat.pause') }}</span>
              </button>

              <button
                class="companion-menu-item"
                type="button"
                role="menuitem"
                :disabled="!activeConversation || inviteCandidates.length === 0"
                data-testid="companion-menu-invite"
                @click="openInvite"
              >
                <UserPlus :size="13" class="companion-menu-icon" aria-hidden="true" />
                <span class="companion-menu-label">{{ t('companion.chat.invite') }}</span>
              </button>

              <button
                class="companion-menu-item companion-menu-item--accent"
                type="button"
                role="menuitem"
                data-testid="companion-menu-open-center"
                @click="openCompanionCenter"
              >
                <Sparkles :size="13" class="companion-menu-icon" aria-hidden="true" />
                <span class="companion-menu-label">{{ t('companion.openCenter') }}</span>
                <ExternalLink :size="12" class="companion-menu-ext" aria-hidden="true" />
              </button>
            </div>
          </div>
        </div>

        <button
          class="companion-collapse-btn"
          type="button"
          :title="t('companion.collapse')"
          :aria-label="t('companion.collapse')"
          data-testid="companion-collapse-btn"
          @pointerdown.stop
          @click="collapse"
        >
          <Minimize2 :size="14" aria-hidden="true" />
        </button>
      </header>

      <section
        v-if="featuredWorkSession"
        class="companion-ws-strip"
        :class="`is-${featuredWorkTone}`"
        :aria-label="t('companion.workSessionStrip')"
        data-testid="companion-work-session-strip"
        @pointerdown.stop
      >
        <div class="companion-ws-head">
          <span class="companion-ws-status" :class="`is-${featuredWorkTone}`">
            <ShieldAlert v-if="workSessionNeedsApproval(featuredWorkSession)" :size="12" aria-hidden="true" />
            <Globe v-else-if="workSessionIsHandoff(featuredWorkSession)" :size="12" aria-hidden="true" />
            <Loader2 v-else :size="12" class="companion-ws-spin" aria-hidden="true" />
            <span>{{ featuredWorkStatusLabel }}</span>
          </span>
          <span class="companion-ws-title" :title="featuredWorkSession.title">{{ featuredWorkSession.title }}</span>
          <button
            class="companion-ws-open"
            type="button"
            :title="t('companion.openInWorkbench')"
            :aria-label="t('companion.openInWorkbench')"
            data-testid="companion-ws-open"
            @click="openWorkSessionInWorkbench(featuredWorkSession.session_id)"
          >
            <ExternalLink :size="12" aria-hidden="true" />
          </button>
        </div>

        <div v-if="featuredApproval" class="companion-ws-approval">
          <div class="companion-ws-approval-info">
            <code class="companion-ws-tool">{{ featuredApproval.tool_id }}</code>
            <span v-if="featuredExtraApprovals > 0" class="companion-ws-more">
              {{ t('companion.moreApprovals', { count: featuredExtraApprovals }) }}
            </span>
            <p v-if="featuredApproval.summary" class="companion-ws-summary">{{ featuredApproval.summary }}</p>
          </div>
          <div class="companion-ws-actions">
            <button
              class="companion-ws-btn companion-ws-btn--approve"
              type="button"
              :disabled="Boolean(decidingApprovalIds[featuredApproval.approval_id])"
              data-testid="companion-ws-approve"
              @click="decideWorkApproval(featuredApproval, true)"
            >
              <Check :size="12" aria-hidden="true" />
              <span>{{ t('companionCenter.chat.approve') }}</span>
            </button>
            <button
              class="companion-ws-btn companion-ws-btn--reject"
              type="button"
              :disabled="Boolean(decidingApprovalIds[featuredApproval.approval_id])"
              data-testid="companion-ws-reject"
              @click="decideWorkApproval(featuredApproval, false)"
            >
              <X :size="12" aria-hidden="true" />
              <span>{{ t('companionCenter.chat.deny') }}</span>
            </button>
            <button
              v-if="canCancelFeaturedWorkRun"
              class="companion-ws-btn companion-ws-btn--cancel"
              type="button"
              :disabled="Boolean(featuredWorkSession.latest_run_id && cancellingRunIds[featuredWorkSession.latest_run_id])"
              data-testid="companion-ws-cancel"
              @click="cancelWorkRun"
            >
              <Square :size="10" aria-hidden="true" />
              <span>{{ t('companionCenter.workSession.cancelRun') }}</span>
            </button>
          </div>
        </div>

        <div v-else class="companion-ws-footer">
          <p v-if="featuredWorkDetail" class="companion-ws-detail">{{ featuredWorkDetail }}</p>
          <button
            v-if="canCancelFeaturedWorkRun"
            class="companion-ws-btn companion-ws-btn--cancel"
            type="button"
            :disabled="Boolean(featuredWorkSession.latest_run_id && cancellingRunIds[featuredWorkSession.latest_run_id])"
            data-testid="companion-ws-cancel"
            @click="cancelWorkRun"
          >
            <Square :size="10" aria-hidden="true" />
            <span>{{ t('companionCenter.workSession.cancelRun') }}</span>
          </button>
        </div>
      </section>

      <p v-if="patNote" class="companion-pat-note" aria-live="polite">{{ patNote }}</p>

      <div class="companion-chat" @pointerdown.stop @dblclick="pat">
        <p v-if="booting" class="companion-chat-state">{{ t('companion.chat.loading') }}</p>
        <div v-else-if="bootFailed" class="companion-chat-state" data-testid="companion-empty-retry">
          <p class="companion-chat-state-body">{{ t('companion.chat.noMessages') }}</p>
          <button class="companion-retry" type="button" @click="boot">{{ t('companion.chat.retry') }}</button>
        </div>
        <div v-else-if="companions.length === 0" class="companion-chat-state">
          <p class="companion-chat-state-title">{{ t('companion.chat.noCompanionsTitle') }}</p>
          <p class="companion-chat-state-body">{{ t('companion.chat.noCompanionsBody') }}</p>
        </div>

        <template v-else>
          <p v-if="chatLoading" class="companion-chat-state">{{ t('companion.chat.loading') }}</p>
          <div v-else ref="logRef" class="companion-log">
            <div v-if="messages.length === 0 && !activeStreamingText" class="companion-chat-state">
              <p class="companion-chat-state-body">{{ t('companion.chat.noMessages') }}</p>
              <button v-if="chatLoadFailed" class="companion-retry" type="button" @click="retryChat">
                {{ t('companion.chat.retry') }}
              </button>
            </div>
            <article v-for="message in messages" :key="message.id" class="companion-msg" :class="`companion-msg--${message.author_kind}`">
              <AgentPetAvatar
                v-if="message.author_kind === 'companion'"
                class="companion-msg-avatar companion-msg-avatar--pet"
                :pet-slug="authorPetSlug(message)"
                :size="24"
                :alt="authorLabel(message)"
              />
              <span
                v-else-if="message.author_kind === 'user'"
                class="companion-msg-avatar companion-msg-avatar--user"
                :style="{ backgroundColor: authorTone(message) }"
                aria-hidden="true"
              >{{ authorInitial(message) }}</span>
              <div class="companion-msg-main">
                <div class="companion-msg-meta">
                  <span class="companion-msg-author">{{ authorLabel(message) }}</span>
                  <span class="companion-msg-time">{{ clock(message.created_at) }}</span>
                </div>
                <p class="companion-msg-body">{{ message.body }}</p>
              </div>
            </article>
            <article v-if="activeStreamingText" class="companion-msg companion-msg--companion companion-streaming-bubble">
              <AgentPetAvatar
                class="companion-msg-avatar companion-msg-avatar--pet"
                :pet-slug="companionPetSlug"
                :size="24"
                :alt="headerTitle"
              />
              <div class="companion-msg-main">
                <div class="companion-msg-meta"><span class="companion-msg-author">{{ headerTitle }}</span></div>
                <p class="companion-msg-body">{{ activeStreamingText }}</p>
              </div>
            </article>
          </div>

          <p v-if="paused" class="companion-run companion-run--paused">{{ t('companion.chat.pausedBadge') }}</p>
          <p v-if="thinking && !activeStreamingText" class="companion-run companion-run--thinking">
            {{ t('companion.chat.thinking') }}<span class="companion-run-dots" aria-hidden="true"><span /><span /><span /></span>
          </p>
          <p v-for="failure in failures" :key="failure.id" class="companion-run companion-run--failed">{{ runFailureText(failure) }}</p>
        </template>

        <div v-if="inviteOpen" class="companion-invite" role="dialog" :aria-label="t('companion.chat.inviteTitle')" @pointerdown.stop>
          <p class="companion-invite-title">{{ t('companion.chat.inviteTitle') }}</p>
          <p v-if="inviteCandidates.length === 0" class="companion-invite-empty">{{ t('companion.chat.inviteEmpty') }}</p>
          <label v-for="candidate in inviteCandidates" :key="candidate.id" class="companion-invite-option">
            <input v-model="inviteTargetId" type="radio" name="companion-invite-target" :value="candidate.id" />
            <span>{{ candidate.name }}</span>
          </label>
          <label class="companion-invite-share">
            <input v-model="inviteShareHistory" type="checkbox" />
            <span>{{ t('companion.chat.shareHistory') }}</span>
          </label>
          <div class="companion-invite-actions">
            <button class="companion-secondary" type="button" @click="inviteOpen = false">{{ t('companion.chat.inviteCancel') }}</button>
            <button class="companion-primary" type="button" :disabled="!inviteTargetId || inviteBusy" @click="confirmInvite">{{ t('companion.chat.inviteConfirm') }}</button>
          </div>
        </div>
      </div>

      <form v-if="companions.length > 0 && !bootFailed" class="companion-composer" @submit.prevent="send" @pointerdown.stop>
        <textarea
          ref="composerRef"
          v-model="draft"
          class="companion-input"
          rows="1"
          :placeholder="t('companion.chat.placeholder')"
          :aria-label="t('companion.chat.placeholder')"
          :disabled="!activeConversation || sending || chatLoading"
          @keydown="onComposerKeydown"
        />
        <button
          class="companion-send"
          :class="{ 'is-pause': thinking }"
          type="button"
          :title="thinking ? t('companion.chat.pauseHint') : t('companion.chat.send')"
          :aria-label="thinking ? t('companion.chat.pauseAction') : t('companion.chat.send')"
          :disabled="thinking ? pauseBusy || !activeConversation : !canSend"
          @click="onComposerAction"
        >
          <Pause v-if="thinking" :size="14" aria-hidden="true" />
          <Send v-else :size="14" aria-hidden="true" />
        </button>
      </form>
    </section>
  </div>
</template>

<style scoped>
:global(html),
:global(body),
:global(#app) {
  width: 100%;
  height: 100%;
  margin: 0;
  overflow: hidden;
  background: transparent !important;
}

.companion {
  box-sizing: border-box;
  width: 100%;
  height: 100%;
  overflow: hidden;
  color: var(--text-primary);
  background: transparent;
  user-select: none;
  -webkit-user-select: none;
}

.companion--expanded {
  padding: 6px;
}

.companion--dragging {
  cursor: grabbing;
}

/* ── Collapsed Floating Capsule ─────────────────────────────────────── */

.companion-capsule {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  height: 100%;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  cursor: grab;
  touch-action: none;
}

.companion--dragging .companion-capsule {
  cursor: grabbing;
}

.companion-capsule-pill {
  position: relative;
  left: 0;
  display: grid;
  place-items: center;
  width: 56px;
  height: 56px;
  border-radius: 20px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 8px 22px rgb(0 0 0 / 22%), 0 2px 6px rgb(0 0 0 / 10%);
  transition: left 160ms cubic-bezier(0.2, 0, 0, 1), transform 160ms cubic-bezier(0.2, 0, 0, 1), box-shadow 160ms ease, border-color 160ms ease;
}

.companion-capsule--right .companion-capsule-pill {
  left: 3px;
}

.companion-capsule--left .companion-capsule-pill {
  left: -3px;
}

.companion-capsule:hover .companion-capsule-pill {
  left: 0;
  transform: scale(1.04);
  border-color: color-mix(in srgb, var(--accent-primary) 42%, var(--material-floating-border, var(--border-muted)));
  box-shadow: 0 10px 26px rgb(0 0 0 / 26%), 0 2px 8px rgb(0 0 0 / 12%);
}

.companion-capsule:focus-visible {
  outline: none;
}

.companion-capsule:focus-visible .companion-capsule-pill {
  outline: 2px solid var(--accent-primary);
  outline-offset: 2px;
}

.companion-capsule-avatar {
  pointer-events: none;
}

.companion-capsule-glyph {
  display: grid;
  place-items: center;
  color: var(--text-secondary);
}

.companion-capsule-status {
  position: absolute;
  right: 4px;
  bottom: 4px;
  width: 9px;
  height: 9px;
  border-radius: 999px;
  border: 1.5px solid var(--surface-section, var(--bg-secondary));
  background: var(--accent-primary, #58a6ff);
  animation: companion-breathe 1.8s ease-in-out infinite;
}

.companion-capsule-status.is-awaiting_approval,
.companion-capsule-status.is-browser_handoff {
  background: var(--accent-warning, #f59e0b);
}

.companion-capsule-status.is-failed {
  background: var(--accent-danger, #ef4444);
  animation: none;
}

.companion-capsule-unread {
  position: absolute;
  top: -4px;
  right: -4px;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: 999px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 10px;
  font-weight: 700;
  line-height: 1;
  font-variant-numeric: tabular-nums;
  box-shadow: 0 2px 6px rgb(0 0 0 / 24%);
}

@keyframes companion-breathe {
  0%, 100% { opacity: 1; transform: scale(1); }
  50% { opacity: 0.52; transform: scale(0.86); }
}

/* ── Expanded Frosted Island Panel ──────────────────────────────────── */

.companion-panel {
  position: relative;
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  overflow: hidden;
  border-radius: 16px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 14px 36px rgb(0 0 0 / 24%), 0 2px 8px rgb(0 0 0 / 12%);
  user-select: text;
  -webkit-user-select: text;
}

/* ── 40px Draggable Top Bar ─────────────────────────────────────────── */

.companion-topbar {
  height: 40px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 0 8px 0 8px;
  cursor: grab;
  user-select: none;
  -webkit-user-select: none;
}

.companion--dragging .companion-topbar {
  cursor: grabbing;
}

.companion-identity-wrap {
  position: relative;
  min-width: 0;
  max-width: calc(100% - 38px);
}

.companion-identity-pill {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  max-width: 100%;
  height: 30px;
  padding: 0 10px 0 5px;
  border: 1px solid transparent;
  border-radius: 999px;
  background: transparent;
  color: var(--text-primary);
  cursor: pointer;
  transition: background-color 140ms ease, border-color 140ms ease;
}

.companion-identity-pill:hover,
.companion-identity-pill[aria-expanded='true'] {
  background: color-mix(in srgb, var(--surface-raised, var(--bg-tertiary)) 76%, transparent);
  border-color: var(--border-subtle, var(--border-muted));
}

.companion-identity-pill:focus-visible,
.companion-collapse-btn:focus-visible,
.companion-ws-open:focus-visible,
.companion-ws-btn:focus-visible,
.companion-send:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 2px;
}

.companion-identity-avatar {
  flex-shrink: 0;
  border-radius: 999px;
}

.companion-header-glyph {
  width: 24px;
  height: 24px;
  display: grid;
  place-items: center;
  border-radius: 999px;
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-secondary);
  flex-shrink: 0;
}

.companion-title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  line-height: 1.2;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-identity-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  flex-shrink: 0;
  background: var(--accent-primary);
}

.companion-identity-dot.is-awaiting_approval,
.companion-identity-dot.is-browser_handoff {
  background: var(--accent-warning, #f59e0b);
}

.companion-identity-dot.is-failed {
  background: var(--accent-danger, #ef4444);
}

.companion-identity-caret {
  color: var(--text-muted);
  flex-shrink: 0;
  transition: transform 140ms ease;
}

.companion-identity-caret.is-open {
  transform: rotate(180deg);
}

.companion-collapse-btn {
  width: 28px;
  height: 28px;
  flex-shrink: 0;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.companion-collapse-btn:hover {
  background: color-mix(in srgb, var(--surface-raised, var(--bg-tertiary)) 78%, transparent);
  color: var(--text-primary);
}

/* ── Unified Floating Identity Popover ──────────────────────────────── */

.companion-popover {
  position: absolute;
  left: 0;
  top: calc(100% + 6px);
  z-index: 20;
  width: 236px;
  max-height: 360px;
  overflow-y: auto;
  padding: 6px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  border-radius: 12px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 12px 30px rgb(0 0 0 / 26%), 0 2px 8px rgb(0 0 0 / 12%);
}

.companion-popover-section {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.companion-popover-heading {
  margin: 0;
  padding: 2px 8px 3px;
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.03em;
  color: var(--text-muted);
}

.companion-menu-item {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
  transition: background-color 120ms ease;
}

.companion-menu-item:hover:not(:disabled) {
  background: color-mix(in srgb, var(--surface-raised, var(--bg-tertiary)) 82%, transparent);
}

.companion-menu-item.is-active {
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
}

.companion-menu-item--accent {
  color: var(--accent-primary);
  font-weight: 500;
}

.companion-menu-item:disabled {
  opacity: 0.42;
  cursor: default;
}

.companion-menu-glyph {
  width: 18px;
  height: 18px;
  border-radius: 999px;
  display: grid;
  place-items: center;
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-muted);
  flex-shrink: 0;
}

.companion-menu-icon {
  flex-shrink: 0;
  color: var(--text-muted);
}

.companion-menu-item--accent .companion-menu-icon,
.companion-menu-ext {
  color: var(--accent-primary);
  flex-shrink: 0;
}

.companion-menu-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-menu-check {
  flex-shrink: 0;
  color: var(--accent-primary);
}

/* ── #37 Compact Work-Session Approval / Cancel / Jump Strip ────────── */

.companion-ws-strip {
  flex-shrink: 0;
  margin: 2px 10px 6px;
  padding: 8px 10px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  border-radius: 12px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 24%, var(--border-subtle, var(--border-muted)));
  background: color-mix(in srgb, var(--accent-primary) 7%, var(--surface-raised, var(--bg-tertiary)));
}

.companion-ws-strip.is-warning {
  border-color: color-mix(in srgb, var(--accent-warning, #d97706) 34%, var(--border-subtle, var(--border-muted)));
  background: color-mix(in srgb, var(--accent-warning, #d97706) 8%, var(--surface-raised, var(--bg-tertiary)));
}

.companion-ws-head {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.companion-ws-status {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 10.5px;
  font-weight: 600;
  white-space: nowrap;
  flex-shrink: 0;
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
}

.companion-ws-status.is-warning {
  color: var(--accent-warning, #d97706);
  background: color-mix(in srgb, var(--accent-warning, #d97706) 16%, transparent);
}

.companion-ws-spin {
  animation: companion-ws-spin 1.1s linear infinite;
}

@keyframes companion-ws-spin {
  to { transform: rotate(360deg); }
}

.companion-ws-title {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-ws-open {
  width: 24px;
  height: 24px;
  flex-shrink: 0;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.companion-ws-open:hover {
  background: color-mix(in srgb, var(--text-primary) 10%, transparent);
  color: var(--text-primary);
}

.companion-ws-approval {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.companion-ws-approval-info {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 5px;
  min-width: 0;
}

.companion-ws-tool {
  font-family: var(--font-mono, monospace);
  font-size: 10.5px;
  font-weight: 600;
  padding: 1px 6px;
  border-radius: 5px;
  background: color-mix(in srgb, var(--text-primary) 8%, transparent);
  color: var(--text-primary);
}

.companion-ws-more {
  font-size: 10.5px;
  font-weight: 600;
  color: var(--accent-warning, #d97706);
}

.companion-ws-summary,
.companion-ws-detail {
  margin: 0;
  flex: 1 1 100%;
  font-size: 11.5px;
  line-height: 1.38;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-ws-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-width: 0;
}

.companion-ws-footer .companion-ws-detail {
  flex: 1 1 auto;
}

.companion-ws-actions {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.companion-ws-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 8px;
  border-radius: 7px;
  border: 1px solid var(--border-subtle, var(--border-muted));
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 85%, transparent);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
  white-space: nowrap;
}

.companion-ws-btn:disabled {
  opacity: 0.45;
  cursor: default;
}

.companion-ws-btn--approve {
  border-color: transparent;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
}

.companion-ws-btn--cancel {
  color: var(--accent-danger, #ef4444);
  border-color: color-mix(in srgb, var(--accent-danger, #ef4444) 30%, var(--border-subtle, var(--border-muted)));
}

/* ── Message Feed ───────────────────────────────────────────────────── */

.companion-pat-note {
  margin: 0 12px 4px;
  font-size: 11px;
  text-align: center;
  color: var(--accent-primary);
}

.companion-chat {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.companion-log {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 6px 12px 10px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.companion-log-empty,
.companion-chat-state,
.companion-run {
  margin: 6px 12px;
  font-size: 12px;
  color: var(--text-muted);
}

.companion-chat-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  text-align: center;
  gap: 6px;
}

.companion-chat-state-title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.companion-chat-state-body {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-muted);
}

.companion-run--failed {
  color: var(--text-secondary);
}

.companion-msg {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}

.companion-msg--user {
  flex-direction: row-reverse;
}

.companion-msg--system {
  justify-content: center;
}

.companion-msg--system .companion-msg-main {
  flex: 0 1 auto;
  text-align: center;
  padding: 4px 10px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--surface-raised, var(--bg-tertiary)) 64%, transparent);
}

.companion-msg--system .companion-msg-meta {
  justify-content: center;
}

.companion-msg-avatar {
  width: 24px;
  height: 24px;
  border-radius: 999px;
  display: grid;
  place-items: center;
  color: #fff;
  font-size: 11px;
  font-weight: 600;
  flex-shrink: 0;
}

.companion-msg-main {
  min-width: 0;
  flex: 1;
}

.companion-msg--user .companion-msg-main {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
}

.companion-msg-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 10.5px;
  color: var(--text-muted);
}

.companion-msg--user .companion-msg-meta {
  flex-direction: row-reverse;
}

.companion-msg-body {
  margin: 3px 0 0;
  padding: 7px 10px;
  border-radius: 12px;
  background: color-mix(in srgb, var(--surface-raised, var(--bg-tertiary)) 72%, transparent);
  font-size: 12.5px;
  line-height: 1.48;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.companion-msg--user .companion-msg-body {
  background: color-mix(in srgb, var(--accent-primary) 16%, var(--surface-raised, var(--bg-tertiary)));
}

.companion-msg--system .companion-msg-body {
  padding: 2px 0 0;
  background: transparent;
  font-size: 11.5px;
  color: var(--text-muted);
}

.companion-run-dots {
  display: inline-flex;
  gap: 3px;
  margin-left: 5px;
  vertical-align: middle;
}

.companion-run-dots > span {
  width: 4px;
  height: 4px;
  border-radius: 999px;
  background: currentColor;
  opacity: 0.45;
  animation: companion-dot 1.2s ease-in-out infinite;
}

.companion-run-dots > span:nth-child(2) { animation-delay: 0.16s; }
.companion-run-dots > span:nth-child(3) { animation-delay: 0.32s; }

@keyframes companion-dot {
  0%, 80%, 100% { opacity: 0.3; transform: translateY(0); }
  40% { opacity: 1; transform: translateY(-2px); }
}

/* ── Invite Sheet ───────────────────────────────────────────────────── */

.companion-invite {
  position: absolute;
  inset: 8px;
  z-index: 12;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border-radius: 14px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 12px 28px rgb(0 0 0 / 24%);
  overflow-y: auto;
}

.companion-invite-title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
}

.companion-invite-option,
.companion-invite-share {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}

.companion-invite-actions {
  display: flex;
  justify-content: flex-end;
  gap: 6px;
  margin-top: auto;
}

.companion-secondary,
.companion-primary,
.companion-retry {
  padding: 5px 11px;
  border-radius: 8px;
  border: 1px solid var(--border-subtle, var(--border-muted));
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-primary);
  font-size: 12px;
  cursor: pointer;
}

.companion-primary {
  background: var(--bg-primary-button, var(--accent-primary));
  border-color: transparent;
  color: var(--text-on-primary, #ffffff);
}

/* ── Bottom Floating Input Pill (Zero Hard Dividers) ────────────────── */

.companion-composer {
  flex-shrink: 0;
  display: flex;
  align-items: flex-end;
  gap: 6px;
  margin: 4px 10px 10px;
  padding: 6px 6px 6px 12px;
  border-radius: 14px;
  border: 1px solid var(--border-subtle, var(--border-muted));
  background: color-mix(in srgb, var(--surface-raised, var(--bg-tertiary)) 78%, transparent);
  transition: border-color 140ms ease, box-shadow 140ms ease;
}

.companion-composer:focus-within {
  border-color: color-mix(in srgb, var(--accent-primary) 52%, var(--border-subtle, var(--border-muted)));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 16%, transparent);
}

.companion-input {
  flex: 1;
  min-width: 0;
  resize: none;
  padding: 5px 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font: inherit;
  font-size: 12.5px;
  line-height: 1.4;
  outline: none;
}

.companion-input::placeholder {
  color: var(--text-muted);
}

.companion-send {
  width: 30px;
  height: 30px;
  flex-shrink: 0;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 999px;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  cursor: pointer;
  transition: transform 120ms ease, opacity 120ms ease;
}

.companion-send:hover:not(:disabled) {
  transform: scale(1.04);
}

.companion-send:disabled {
  opacity: 0.38;
  cursor: default;
}

@media (prefers-reduced-motion: reduce) {
  .companion-capsule-pill,
  .companion-capsule-status,
  .companion-ws-spin,
  .companion-run-dots > span {
    animation: none;
    transition: none;
  }
}
</style>
