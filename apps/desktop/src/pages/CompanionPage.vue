<script setup lang="ts">
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, Ellipsis, Maximize2, Minimize2, Pause, PawPrint, Send, UserPlus } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/lib/apiError'
import { formatClock } from '@/lib/conversation/format'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import CompanionStage from '@/components/pets/CompanionStage.vue'
import { PET_STATES, type PetState } from '@/pets/petRuntime'
import {
  companionBridge,
  type CompanionConversation,
  type CompanionMessage,
  type CompanionRun,
  type CompanionSettings,
  type CompanionSummary,
} from '@/lib/companionApi'

// The main process owns the window's shape; this page only renders what it reports and sends
// gestures. Never flip `expanded` locally — a stale local copy would disagree with the real
// bounds after a blur collapse or a display change.
const { t, locale } = useI18n()
const state = ref<CompanionState | null>(null)
const dragging = ref(false)
/**
 * The shape is the main process's answer. `mode` is the source of truth; `expanded` is derived so
 * the two-state language in the rest of this file (and in older snapshots) still reads correctly.
 */
const mode = computed<'capsule' | 'panel' | 'immersive'>(() => {
  if (state.value?.mode) return state.value.mode
  return state.value?.expanded === true ? 'panel' : 'capsule'
})
const expanded = computed(() => mode.value !== 'capsule')
const side = computed<'left' | 'right'>(() => state.value?.side ?? 'right')
/** Masked the same way `mode` is: the main process decides, the page draws. */
const capsuleStyle = computed<'peek' | 'grip'>(() => state.value?.capsuleStyle ?? 'peek')

const SWIPE_EXPAND_PX = 26
const AXIS_LOCK_PX = 4
const CLICK_SUPPRESS_MS = 350
const POLL_INTERVAL_MS = 800
const POLL_TIMEOUT_MS = 90_000
const SELECTED_COMPANION_KEY = 'tinadec.companion.selected'

interface DragStart {
  pointerId: number
  pointerX: number
  pointerY: number
  windowTop: number
  axis: 'x' | 'y' | null
}

let capsuleDrag: DragStart | null = null
let panelDrag: DragStart | null = null
let suppressClickUntil = 0
let disposeState: (() => void) | undefined

function applyState(next: CompanionState | null | undefined): void {
  if (next) state.value = next
}

function command(action: () => Promise<CompanionState>, label: string): void {
  action()
    .then(applyState)
    .catch((error) => console.warn(`[companion] ${label} failed:`, error))
}

function expand(): void {
  command(() => window.tinadec.companion.expand(), 'expand')
}

function collapse(): void {
  command(() => window.tinadec.companion.collapse(), 'collapse')
}

function dock(next: 'left' | 'right'): void {
  if (side.value === next) return
  command(() => window.tinadec.companion.setSide(next), 'setSide')
}

function trackPointer(event: PointerEvent, windowTop: number, axis: DragStart['axis']): DragStart {
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  dragging.value = true
  return {
    pointerId: event.pointerId,
    pointerX: event.screenX,
    pointerY: event.screenY,
    windowTop,
    axis,
  }
}

// The capsule is the whole 24px-wide window, so a vertical drag has to survive the window moving
// under the pointer (pointer capture) and a horizontal drag must not nudge the dock height (axis
// lock). Inward travel is measured from the capture point, not from the window, because by then
// the window may have moved.
function beginCapsuleDrag(event: PointerEvent): void {
  if (event.button !== 0) return
  capsuleDrag = trackPointer(event, window.screenY, null)
}

function moveCapsuleDrag(event: PointerEvent): void {
  const drag = capsuleDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  const dx = event.screenX - drag.pointerX
  const dy = event.screenY - drag.pointerY
  if (!drag.axis) {
    if (Math.abs(dx) < AXIS_LOCK_PX && Math.abs(dy) < AXIS_LOCK_PX) return
    drag.axis = Math.abs(dx) > Math.abs(dy) ? 'x' : 'y'
  }
  if (drag.axis === 'y') {
    window.tinadec.companion.moveTo(drag.windowTop + dy).catch((error) => {
      console.warn('[companion] moveTo failed:', error)
    })
  }
}

function endCapsuleDrag(event: PointerEvent): void {
  const drag = capsuleDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  capsuleDrag = null
  dragging.value = false
  if (!drag.axis) return
  // A drag that ends on the button still produces a click; that click is not a request to open.
  suppressClickUntil = performance.now() + CLICK_SUPPRESS_MS
  if (drag.axis !== 'x') return
  const inward = side.value === 'left' ? event.screenX - drag.pointerX : drag.pointerX - event.screenX
  if (inward >= SWIPE_EXPAND_PX) expand()
}

function onCapsuleClick(event: MouseEvent): void {
  // Keyboard activation reports detail 0 and must always open.
  if (event.detail !== 0 && performance.now() < suppressClickUntil) return
  expand()
}

function beginPanelDrag(event: PointerEvent): void {
  if (event.button !== 0) return
  panelDrag = trackPointer(event, window.screenY, 'y')
}

function movePanelDrag(event: PointerEvent): void {
  const drag = panelDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  window.tinadec.companion.moveTo(drag.windowTop + event.screenY - drag.pointerY).catch((error) => {
    console.warn('[companion] moveTo failed:', error)
  })
}

function endPanelDrag(event: PointerEvent): void {
  if (!panelDrag || panelDrag.pointerId !== event.pointerId) return
  panelDrag = null
  dragging.value = false
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || mode.value === 'capsule') return
  event.preventDefault()
  // Immersive is one step further in than the panel, so Escape walks back out the way the user
  // came in instead of closing everything at once.
  if (mode.value === 'immersive') {
    setMode('panel')
    return
  }
  collapse()
}

/** Grow into the reading shape; the panel keeps its scroll position because it is the same DOM. */
function enterImmersive(): void {
  if (mode.value === 'immersive') return
  setMode('immersive')
}

function exitImmersive(): void {
  if (mode.value !== 'immersive') return
  setMode('panel')
}

function setMode(next: 'capsule' | 'panel' | 'immersive'): void {
  command(() => window.tinadec.companion.setMode(next), 'setMode')
}

// Coming back from the call shape remounts the scrollback's visibility; a hidden log has no
// scrollTop to keep, so pin it to the newest line the way the user left it.
watch(mode, (next, previous) => {
  if (next === 'panel' && previous === 'immersive') scrollToBottom()
})

// ── Chat state ────────────────────────────────────────────────────────
// The companion window is not a trusted host: every read/write goes through the main-process
// whitelist proxy (`companionBridge`), never through `fetch` or `companionApi`.

const companions = ref<CompanionSummary[]>([])
const selectedCompanionId = ref<string | null>(null)
const conversations = ref<CompanionConversation[]>([])
const activeConversation = ref<CompanionConversation | null>(null)
const messages = ref<CompanionMessage[]>([])
const runs = ref<Record<string, CompanionRun>>({})
/** Run ids this page is waiting on; reactive so the thinking/failure rows recompute on send. */
const trackedRunIds = ref<string[]>([])
/** Drafts survive collapse/expand and companion switches; keyed by companion Id, memory only. */
const drafts = reactive<Record<string, string>>({})

const booting = ref(true)
const loadError = ref('')
const chatLoading = ref(false)
const chatError = ref('')
const sending = ref(false)
const sendError = ref('')
const pauseBusy = ref(false)
const menuOpen = ref(false)
const inviteOpen = ref(false)
const inviteTargetId = ref('')
const inviteShareHistory = ref(false)
const inviteBusy = ref(false)
const inviteError = ref('')
const logRef = ref<HTMLElement | null>(null)

let lastSeq = 0
let loadToken = 0
let pollTimer: ReturnType<typeof setTimeout> | undefined
let pollStartedAt = 0
let polling = false

const activeCompanion = computed(() => companions.value.find((item) => item.id === selectedCompanionId.value) ?? null)
const draft = computed({
  get: () => (selectedCompanionId.value ? drafts[selectedCompanionId.value] ?? '' : ''),
  set: (value: string) => {
    if (selectedCompanionId.value) drafts[selectedCompanionId.value] = value
  },
})
const canSend = computed(() => !sending.value && !!activeConversation.value && draft.value.trim().length > 0)
const headerTitle = computed(() => activeCompanion.value?.name ?? t('companion.title'))
const paused = computed(() => activeConversation.value?.autonomy_paused === true)
const thinking = computed(() => trackedRunIds.value.some((id) => isPending(runs.value[id])))
const failures = computed(() => trackedRunIds.value
  .map((id) => runs.value[id])
  .filter((run): run is CompanionRun => run != null && run.status === 'failed'))
/**
 * The selected companion's pet. The capsule crops it to the head (a 240px figure inside a 120px
 * window, leaned out of the edge), and the panel header shows the same pet as a small face, so
 * both shapes agree about who is talking.
 */
const companionPetSlug = computed(() => activeCompanion.value?.avatar ?? '')
/**
 * Only states the companion window can actually observe: a pending run, a failed run, and the
 * conversation's own pause flag. There is no approval surface here, so the capsule never claims
 * to be waiting for a decision it cannot show.
 */
const capsuleMood = computed<'idle' | 'thinking' | 'failed' | 'paused'>(() => {
  if (failures.value.length > 0) return 'failed'
  if (thinking.value) return 'thinking'
  if (paused.value) return 'paused'
  return 'idle'
})
/**
 * The atlas row for the same fact. `paused` deliberately maps to `idle`: the atlas has no sleeping
 * row, and pretending a paused companion is "waiting" would read as work in progress.
 */
const observedStageState = computed<PetState>(() => {
  if (capsuleMood.value === 'thinking') return 'waiting'
  if (capsuleMood.value === 'failed') return 'failed'
  return 'idle'
})

/**
 * A local interaction, not a message: clicking the companion's face makes it wave for one cycle.
 * Nothing is sent anywhere — the companion domain has no emote endpoint, and inventing a fake one
 * would be worse than a visual acknowledgement that claims nothing.
 */
const impulse = ref<PetState | null>(null)
const greeting = ref(false)
/** Caption for the head pat. It floats over the conversation and is never a message. */
const patNote = ref('')
let impulseTimer: ReturnType<typeof setTimeout> | undefined

const stageState = computed<PetState>(() => impulse.value ?? observedStageState.value)

/**
 * Play one atlas row for exactly as long as that row lasts, then hand the stage back to whatever
 * the run is actually doing. One shared timer: two gestures in a row must not fight over the stage.
 */
function playImpulse(next: PetState, note = ''): void {
  impulse.value = next
  patNote.value = note
  greeting.value = next === 'waving'
  clearTimeout(impulseTimer)
  impulseTimer = setTimeout(() => {
    impulse.value = null
    greeting.value = false
    patNote.value = ''
  }, PET_STATES[next].durationMs)
}

function greet(): void {
  playImpulse('waving')
}

/**
 * Double-clicking the panel's blank space pats the companion.
 *
 * Local for the same reason greeting is: the companion domain has no emote endpoint, and a
 * fabricated line in the transcript would be a lie about who said it. A double-click that lands on
 * text, a control or the invite sheet is selection, not affection, so it is ignored.
 */
function pat(event: MouseEvent): void {
  const target = event.target as HTMLElement | null
  if (target?.closest('.companion-msg, button, a, input, textarea, select, .companion-invite')) return
  playImpulse('jumping', t('companion.pat'))
}

/**
 * How tall the standing figure is in the call shape.
 *
 * The window does not grow for the call, so this is a share of the panel: 60% leaves room for the
 * subtitles and the composer underneath without crowding them, and the 180/500 rails keep the head
 * in frame on a short display and the sprite narrower than the column (width = height * 192/208).
 */
const viewportHeight = ref(typeof window === 'undefined' ? 0 : window.innerHeight)
const callStageHeight = computed(() => {
  const target = Math.round(viewportHeight.value * 0.6)
  return Math.min(500, Math.max(180, target))
})

function onViewportResize(): void {
  viewportHeight.value = window.innerHeight
}

/** The last exchange, oldest first: a call shows what was just said, not the backlog. */
const callLines = computed(() => messages.value
  .filter((message) => message.author_kind !== 'system')
  .slice(-2))

const headerSubtitle = computed(() => {
  // What the companion is doing outranks what the conversation is called: a stall or a running
  // turn is the thing worth a subtitle, and the picker row below already names the conversation.
  if (failures.value.length > 0) return t('companion.stateFailed')
  if (thinking.value) return t('companion.stateThinking')
  if (paused.value) return t('companion.statePaused')
  const conversation = activeConversation.value
  if (!conversation) return activeCompanion.value?.tagline ?? t('companion.subtitle')
  if (conversation.kind === 'group') {
    const members = conversation.members
      .filter((member) => member.status === 'active')
      .map((member) => member.display_name)
      .join(' · ')
    return t('companion.chat.groupSubtitle', {
      title: conversation.title || t('companion.chat.groupLabel'),
      members,
    })
  }
  return t('companion.chat.privateSubtitle', { name: activeCompanion.value?.name ?? '' })
})
const inviteCandidates = computed(() => {
  const memberIds = new Set(
    (activeConversation.value?.members ?? [])
      .map((member) => member.companion_id)
      .filter((id): id is string => typeof id === 'string'),
  )
  return companions.value.filter((item) => item.id !== selectedCompanionId.value && !memberIds.has(item.id))
})

function isPending(run: CompanionRun | undefined): boolean {
  return run != null && (run.status === 'queued' || run.status === 'running')
}

function isTerminal(run: CompanionRun | undefined): boolean {
  return run != null && (run.status === 'completed' || run.status === 'failed')
}

function isHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden'
}

function storedSelection(): string | null {
  try {
    return localStorage.getItem(SELECTED_COMPANION_KEY)
  } catch {
    return null
  }
}

function rememberSelection(id: string): void {
  try {
    localStorage.setItem(SELECTED_COMPANION_KEY, id)
  } catch {
    // A locked-down storage backend must not stop a chat from loading.
  }
}

function errorText(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'desktop_host_required') return t('companion.chat.desktopHostRequired')
    if (error.code) return t('companion.chat.apiError', { code: error.code, message: error.message })
    return error.message
  }
  return error instanceof Error ? error.message : String(error)
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
  const companionId = message.author_companion_id
  const member = companionId
    ? activeConversation.value?.members.find(
      (candidate) => candidate.member_kind === 'companion' && candidate.companion_id === companionId,
    )
    : undefined
  return member?.display_name || message.author_name
}

function authorInitial(message: CompanionMessage): string {
  return Array.from(authorLabel(message))[0] ?? ''
}

/** The face a companion message wears: the companion's pinned Petdex pet, else the bundled one. */
function authorPetSlug(message: CompanionMessage): string {
  if (message.author_kind !== 'companion' || !message.author_companion_id) return ''
  return companions.value.find((item) => item.id === message.author_companion_id)?.avatar ?? ''
}

/** A stable hue per companion so group messages from different members read as different people. */
function authorTone(message: CompanionMessage): string {
  if (message.author_kind === 'user') return 'var(--accent-primary)'
  if (message.author_kind === 'system') return 'var(--text-muted)'
  const seed = message.author_companion_id ?? message.author_name
  let hash = 0
  for (const character of seed) hash = (hash * 31 + character.charCodeAt(0)) % 360
  return `hsl(${hash} 58% 55%)`
}

function clock(iso: string | undefined): string {
  return iso ? formatClock(iso, locale.value) : ''
}

function conversationLabel(conversation: CompanionConversation): string {
  if (conversation.kind === 'group') return conversation.title || t('companion.chat.groupLabel')
  return t('companion.chat.privateLabel')
}

function mergeMessages(incoming: CompanionMessage[]): void {
  if (incoming.length === 0) return
  const ordered = new Map(messages.value.map((message) => [message.id, message]))
  for (const message of incoming) {
    ordered.set(message.id, message)
    lastSeq = Math.max(lastSeq, message.seq)
  }
  messages.value = [...ordered.values()].sort((left, right) => left.seq - right.seq)
}

function scrollToBottom(): void {
  void nextTick(() => {
    const element = logRef.value
    if (element) element.scrollTop = element.scrollHeight
  })
}

async function loadMessagesFor(conversationId: string, token: number): Promise<void> {
  chatLoading.value = true
  chatError.value = ''
  try {
    const list = await companionBridge.listMessages(conversationId)
    if (token !== loadToken) return
    messages.value = []
    lastSeq = 0
    mergeMessages(list ?? [])
    scrollToBottom()
  } catch (error) {
    if (token !== loadToken) return
    chatError.value = errorText(error)
  } finally {
    if (token === loadToken) chatLoading.value = false
  }
}

async function selectCompanion(id: string): Promise<void> {
  const token = ++loadToken
  stopPolling()
  selectedCompanionId.value = id
  rememberSelection(id)
  activeConversation.value = null
  conversations.value = []
  messages.value = []
  runs.value = {}
  trackedRunIds.value = []
  lastSeq = 0
  chatError.value = ''
  sendError.value = ''
  try {
    const conversation = await companionBridge.ensurePrivateConversation(id)
    if (token !== loadToken) return
    activeConversation.value = conversation
    const list = await companionBridge.listConversations(id)
    if (token !== loadToken) return
    conversations.value = list.length > 0 && list.some((item) => item.id === conversation.id)
      ? list
      : [conversation, ...list]
    await loadMessagesFor(conversation.id, token)
  } catch (error) {
    if (token !== loadToken) return
    chatError.value = errorText(error)
  }
}

async function adoptConversation(conversation: CompanionConversation): Promise<void> {
  const token = ++loadToken
  stopPolling()
  activeConversation.value = conversation
  messages.value = []
  runs.value = {}
  trackedRunIds.value = []
  lastSeq = 0
  sendError.value = ''
  await loadMessagesFor(conversation.id, token)
}

async function refreshConversations(): Promise<void> {
  if (!selectedCompanionId.value) return
  try {
    conversations.value = await companionBridge.listConversations(selectedCompanionId.value)
  } catch (error) {
    console.warn('[companion] conversation list read failed:', error)
  }
}

function pickCompanion(settings: CompanionSettings, active: CompanionSummary[]): CompanionSummary | undefined {
  const byId = (id: string | null | undefined) => active.find((item) => item.id === id)
  return byId(settings.main_companion_id) ?? byId(storedSelection()) ?? active[0]
}

async function boot(): Promise<void> {
  booting.value = true
  loadError.value = ''
  try {
    const [settings, list] = await Promise.all([
      companionBridge.getSettings(),
      companionBridge.listCompanions(),
    ])
    const active = (list ?? []).filter((item) => item.status === 'active')
    companions.value = active
    const preferred = pickCompanion(settings, active)
    if (preferred) await selectCompanion(preferred.id)
  } catch (error) {
    loadError.value = errorText(error)
  } finally {
    booting.value = false
  }
}

function retryChat(): void {
  if (selectedCompanionId.value) void selectCompanion(selectedCompanionId.value)
  else void boot()
}

function onCompanionSelect(event: Event): void {
  const id = (event.target as HTMLSelectElement).value
  if (id && id !== selectedCompanionId.value) void selectCompanion(id)
}

function onConversationSelect(event: Event): void {
  const id = (event.target as HTMLSelectElement).value
  const conversation = conversations.value.find((item) => item.id === id)
  if (conversation && conversation.id !== activeConversation.value?.id) void adoptConversation(conversation)
}

function idempotencyKey(): string | undefined {
  try {
    return typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID()
      : undefined
  } catch {
    return undefined
  }
}

const composerRef = ref<HTMLTextAreaElement | null>(null)
/** Four lines, then the box scrolls: a long instruction should be readable without eating the chat. */
const COMPOSER_MAX_HEIGHT = 96

function resizeComposer(): void {
  const element = composerRef.value
  if (!element) return
  element.style.height = 'auto'
  element.style.height = `${Math.min(element.scrollHeight, COMPOSER_MAX_HEIGHT)}px`
}

watch(draft, () => { void nextTick(resizeComposer) })

/** Enter sends, Shift+Enter is a newline, and an IME composition never submits by accident. */
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
  sendError.value = ''
  try {
    const result = await companionBridge.sendMessage(conversation.id, { body }, idempotencyKey())
    if (selectedCompanionId.value === companionId && companionId) drafts[companionId] = ''
    if (activeConversation.value?.id !== conversation.id) return
    mergeMessages([result.message])
    for (const run of result.runs ?? []) runs.value = { ...runs.value, [run.id]: run }
    trackedRunIds.value = [...new Set([...trackedRunIds.value, ...(result.runs ?? []).map((run) => run.id)])]
    scrollToBottom()
    startPolling()
  } catch (error) {
    sendError.value = errorText(error)
  } finally {
    sending.value = false
  }
}

/**
 * One key, two meanings: while a turn is in flight it pauses the conversation, otherwise it sends.
 *
 * It says "pause" and not "stop" on purpose — Core has no cancel endpoint for a companion run, so
 * a stop button would promise an interruption this window cannot deliver. Pausing stops the
 * companion from speaking up on its own; the reply already being written still lands.
 */
function onComposerAction(): void {
  if (thinking.value) {
    void togglePause()
    return
  }
  void send()
}

async function refreshRuns(): Promise<void> {
  await Promise.all(trackedRunIds.value.map(async (id) => {
    try {
      const run = await companionBridge.getRun(id)
      runs.value = { ...runs.value, [id]: run }
    } catch (error) {
      console.warn('[companion] run read failed:', error)
    }
  }))
}

async function catchUp(conversationId: string): Promise<void> {
  if (activeConversation.value?.id !== conversationId) return
  try {
    const incoming = await companionBridge.listMessages(conversationId, lastSeq)
    if (activeConversation.value?.id !== conversationId) return
    mergeMessages(incoming ?? [])
    await refreshRuns()
  } catch (error) {
    console.warn('[companion] message poll failed:', error)
  }
}

function schedulePoll(): void {
  if (pollTimer) clearTimeout(pollTimer)
  pollTimer = setTimeout(() => {
    pollTimer = undefined
    void pollTick()
  }, POLL_INTERVAL_MS)
}

async function pollTick(): Promise<void> {
  if (!polling) return
  if (pollTimer) {
    clearTimeout(pollTimer)
    pollTimer = undefined
  }
  if (Date.now() - pollStartedAt > POLL_TIMEOUT_MS) {
    stopPolling()
    return
  }
  const conversation = activeConversation.value
  if (!conversation) {
    stopPolling()
    return
  }
  // A hidden page must not spend requests; the next visible tick refetches before anything else.
  if (isHidden()) return
  await catchUp(conversation.id)
  if (!polling) return
  if (trackedRunIds.value.length === 0 || trackedRunIds.value.every((id) => isTerminal(runs.value[id]))) {
    stopPolling()
    return
  }
  schedulePoll()
}

function startPolling(): void {
  stopPolling()
  polling = true
  pollStartedAt = Date.now()
  void pollTick()
}

function stopPolling(): void {
  polling = false
  if (pollTimer) {
    clearTimeout(pollTimer)
    pollTimer = undefined
  }
}

function onVisibilityChange(): void {
  if (!polling) return
  if (isHidden()) {
    if (pollTimer) {
      clearTimeout(pollTimer)
      pollTimer = undefined
    }
    return
  }
  void pollTick()
}

async function togglePause(): Promise<void> {
  const conversation = activeConversation.value
  if (!conversation || pauseBusy.value) return
  pauseBusy.value = true
  try {
    const updated = await companionBridge.pauseConversation(conversation.id, !conversation.autonomy_paused)
    activeConversation.value = updated
    const index = conversations.value.findIndex((item) => item.id === updated.id)
    if (index >= 0) conversations.value = conversations.value.map((item, at) => (at === index ? updated : item))
  } catch (error) {
    sendError.value = errorText(error)
  } finally {
    pauseBusy.value = false
  }
}

function openInvite(): void {
  inviteOpen.value = true
  inviteError.value = ''
  inviteShareHistory.value = false
  inviteTargetId.value = inviteCandidates.value[0]?.id ?? ''
}

async function confirmInvite(): Promise<void> {
  const conversation = activeConversation.value
  const target = inviteTargetId.value
  if (!conversation || !target || inviteBusy.value) return
  inviteBusy.value = true
  inviteError.value = ''
  try {
    const result = await companionBridge.inviteMember(conversation.id, {
      companion_id: target,
      share_history: inviteShareHistory.value,
    })
    inviteOpen.value = false
    await refreshConversations()
    await adoptConversation(result)
    if (!conversations.value.some((item) => item.id === result.id)) {
      conversations.value = [result, ...conversations.value]
    }
  } catch (error) {
    inviteError.value = errorText(error)
  } finally {
    inviteBusy.value = false
  }
}

onMounted(async () => {
  window.addEventListener('keydown', onKeydown)
  window.addEventListener('resize', onViewportResize)
  document.addEventListener('visibilitychange', onVisibilityChange)
  // Subscribe before reading: a state change that lands during the read must win over the
  // snapshot, so the initial value is only applied while nothing has arrived yet.
  disposeState = window.tinadec.companion.onStateChanged(applyState)
  try {
    const initial = await window.tinadec.companion.getState()
    if (!state.value) applyState(initial)
  } catch (error) {
    console.warn('[companion] state read failed:', error)
  }
  void boot()
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  window.removeEventListener('resize', onViewportResize)
  document.removeEventListener('visibilitychange', onVisibilityChange)
  clearTimeout(impulseTimer)
  stopPolling()
  disposeState?.()
  disposeState = undefined
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
  >
    <button
      v-if="mode === 'capsule'"
      class="companion-capsule"
      :class="[`companion-capsule--${capsuleStyle}`, `companion-capsule--${side}`]"
      type="button"
      :title="t('companion.expand')"
      :aria-label="t('companion.expand')"
      :aria-expanded="false"
      @pointerdown="beginCapsuleDrag"
      @pointermove="moveCapsuleDrag"
      @pointerup="endCapsuleDrag"
      @pointercancel="endCapsuleDrag"
      @click="onCapsuleClick"
    >
      <span v-if="capsuleStyle === 'peek'" class="companion-capsule-stage" aria-hidden="true">
        <span class="companion-capsule-tilt">
          <!-- 92px and 22° are PEEK_HEIGHT / PEEK_TILT_DEGREES in companionGeometry.cjs: the window
               is sized so this frame fits while leaning, so the two numbers move together. -->
          <CompanionStage
            v-if="companionPetSlug"
            class="companion-peek-pet"
            :pet-slug="companionPetSlug"
            :state="stageState"
            :height="92"
            :alt="activeCompanion?.name ?? ''"
          />
          <span v-else class="companion-capsule-glyph"><PawPrint :size="22" /></span>
        </span>
      </span>
      <!-- The grip is only a handle: no pet, nothing to read, just something to pull. -->
      <span v-else class="companion-capsule-grip" aria-hidden="true"><span /></span>
      <span class="companion-capsule-status" :class="`is-${capsuleMood}`" />
      <span class="companion-capsule-chevron" aria-hidden="true">
        <ChevronLeft v-if="side === 'right'" :size="15" />
        <ChevronRight v-else :size="15" />
      </span>
    </button>

    <section
      v-else
      class="companion-panel"
      :class="`companion-panel--${mode}`"
      role="region"
      :aria-label="t('companion.title')"
      :title="t('companion.dragHint')"
      @pointerdown="beginPanelDrag"
      @pointermove="movePanelDrag"
      @pointerup="endPanelDrag"
      @pointercancel="endPanelDrag"
    >
      <header class="companion-header">
        <button
          v-if="companionPetSlug"
          class="companion-header-face"
          :class="{ 'is-greeting': greeting }"
          type="button"
          :title="t('companion.greet')"
          :aria-label="t('companion.greet')"
          data-testid="companion-greet"
          @pointerdown.stop
          @click="greet"
        >
          <AgentPetAvatar :pet-slug="companionPetSlug" :alt="headerTitle" :size="30" />
        </button>
        <span v-else class="companion-header-glyph" aria-hidden="true"><PawPrint :size="16" /></span>
        <div class="companion-heading">
          <p class="companion-title">{{ headerTitle }}</p>
          <p class="companion-subtitle">{{ headerSubtitle }}</p>
        </div>
        <div class="companion-actions">
          <button
            v-if="mode !== 'immersive'"
            class="companion-action"
            type="button"
            :title="t('companion.chat.invite')"
            :aria-label="t('companion.chat.invite')"
            :disabled="!activeConversation || inviteCandidates.length === 0"
            @pointerdown.stop
            @click="openInvite"
          >
            <UserPlus :size="15" />
          </button>
          <div class="companion-menu-wrap" @pointerdown.stop>
            <button
              class="companion-action"
              type="button"
              :title="t('companion.chat.options')"
              :aria-label="t('companion.chat.options')"
              :aria-expanded="menuOpen"
              aria-haspopup="menu"
              @click="menuOpen = !menuOpen"
            >
              <Ellipsis :size="15" />
            </button>
            <div v-if="menuOpen" class="companion-menu" role="menu">
              <button
                class="companion-menu-item"
                type="button"
                role="menuitem"
                :disabled="!activeConversation || pauseBusy"
                @click="togglePause"
              >
                {{ paused ? t('companion.chat.resume') : t('companion.chat.pause') }}
              </button>
            </div>
          </div>
          <button
            v-if="mode !== 'immersive' && side === 'right'"
            class="companion-action"
            type="button"
            :title="t('companion.dockLeft')"
            :aria-label="t('companion.dockLeft')"
            @pointerdown.stop
            @click="dock('left')"
          >
            <ChevronsLeft :size="15" />
          </button>
          <button
            v-else-if="mode !== 'immersive'"
            class="companion-action"
            type="button"
            :title="t('companion.dockRight')"
            :aria-label="t('companion.dockRight')"
            @pointerdown.stop
            @click="dock('right')"
          >
            <ChevronsRight :size="15" />
          </button>
          <button
            class="companion-action"
            type="button"
            :title="mode === 'immersive' ? t('companion.exitImmersive') : t('companion.immersive')"
            :aria-label="mode === 'immersive' ? t('companion.exitImmersive') : t('companion.immersive')"
            :aria-pressed="mode === 'immersive'"
            data-testid="companion-immersive-toggle"
            @pointerdown.stop
            @click="mode === 'immersive' ? exitImmersive() : enterImmersive()"
          >
            <Minimize2 v-if="mode === 'immersive'" :size="15" />
            <Maximize2 v-else :size="15" />
          </button>
          <button
            class="companion-action"
            type="button"
            :title="t('companion.collapse')"
            :aria-label="t('companion.collapse')"
            @pointerdown.stop
            @click="collapse"
          >
            <ChevronsRight v-if="side === 'right'" :size="15" />
            <ChevronsLeft v-else :size="15" />
          </button>
        </div>
      </header>

      <!-- The panel keeps a strip above the conversation, and the strip is itself the door:
           clicking the companion is what "放大" should mean, not hunting for a toolbar button. -->
      <button
        v-if="mode === 'panel' && companionPetSlug"
        class="companion-stage-button"
        type="button"
        :title="t('companion.immersive')"
        :aria-label="t('companion.immersive')"
        data-testid="companion-stage-expand"
        @pointerdown.stop
        @click="enterImmersive"
      >
        <CompanionStage
          class="companion-panel-stage"
          :pet-slug="companionPetSlug"
          :state="stageState"
          :height="88"
          :alt="headerTitle"
        />
      </button>

      <!-- Immersive is a call, not a scrollback: the standing figure, the last exchange as
           subtitles, one input. The log below stays mounted but hidden, so stepping back into the
           panel is instant and keeps the conversation it already had. -->
      <div v-if="mode === 'immersive'" class="companion-call" @dblclick="pat">
        <CompanionStage
          v-if="companionPetSlug"
          class="companion-call-stage"
          :pet-slug="companionPetSlug"
          :state="stageState"
          :height="callStageHeight"
          :alt="headerTitle"
        />
        <span v-else class="companion-call-glyph" aria-hidden="true"><PawPrint :size="30" /></span>

        <div class="companion-call-subtitles" aria-live="polite">
          <p v-if="booting || chatLoading" class="companion-call-note">{{ t('companion.chat.loading') }}</p>
          <p v-else-if="loadError" class="companion-call-note companion-call-note--error">
            {{ loadError }}
            <button class="companion-retry" type="button" @click="boot">{{ t('companion.chat.retry') }}</button>
          </p>
          <p v-else-if="companions.length === 0" class="companion-call-note">
            {{ t('companion.chat.noCompanionsBody') }}
          </p>
          <template v-else>
            <p v-if="callLines.length === 0" class="companion-call-note">{{ t('companion.chat.noMessages') }}</p>
            <p
              v-for="(line, index) in callLines"
              :key="line.id"
              class="companion-call-line"
              :class="{ 'is-past': index < callLines.length - 1 }"
            >
              <span class="companion-call-author">{{ authorLabel(line) }}</span>
              <span class="companion-call-body">{{ line.body }}</span>
            </p>
            <p v-if="paused" class="companion-call-status">{{ t('companion.chat.pausedBadge') }}</p>
            <p v-if="thinking" class="companion-call-status">
              <span class="companion-run-dots" aria-hidden="true"><span /><span /><span /></span>
              {{ t('companion.chat.thinking') }}
            </p>
            <p
              v-for="failure in failures"
              :key="failure.id"
              class="companion-call-status companion-call-status--failed"
            >
              {{ runFailureText(failure) }}
            </p>
            <p v-if="chatError" class="companion-call-status companion-call-status--failed">
              {{ chatError }}
              <button class="companion-retry" type="button" @click="retryChat">{{ t('companion.chat.retry') }}</button>
            </p>
            <p v-if="sendError" class="companion-call-status companion-call-status--failed">{{ sendError }}</p>
          </template>
        </div>
      </div>

      <!-- The pat caption belongs to whatever shape is open, so it lives on the panel, not the log. -->
      <p v-if="patNote" class="companion-pat-note" aria-live="polite">{{ patNote }}</p>

      <div v-if="companions.length && mode !== 'immersive'" class="companion-pickers" @pointerdown.stop>
        <span v-if="companionPetSlug" class="companion-picker-face" aria-hidden="true">
          <AgentPetAvatar :pet-slug="companionPetSlug" :size="22" :alt="headerTitle" />
        </span>
        <select
          class="companion-select companion-companion-select"
          :value="selectedCompanionId ?? ''"
          :title="t('companion.chat.companionPicker')"
          :aria-label="t('companion.chat.companionPicker')"
          @change="onCompanionSelect"
        >
          <option v-for="item in companions" :key="item.id" :value="item.id">{{ item.name }}</option>
        </select>
        <select
          class="companion-select companion-conversation-select"
          :value="activeConversation?.id ?? ''"
          :title="t('companion.chat.conversationPicker')"
          :aria-label="t('companion.chat.conversationPicker')"
          :disabled="conversations.length === 0"
          @change="onConversationSelect"
        >
          <option v-for="item in conversations" :key="item.id" :value="item.id">{{ conversationLabel(item) }}</option>
        </select>
      </div>

      <div v-show="mode !== 'immersive'" class="companion-chat" @pointerdown.stop @dblclick="pat">
        <p v-if="booting" class="companion-chat-state">{{ t('companion.chat.loading') }}</p>
        <div v-else-if="loadError" class="companion-chat-state companion-chat-state--error">
          <p class="companion-chat-state-body">{{ loadError }}</p>
          <button class="companion-retry" type="button" @click="boot">{{ t('companion.chat.retry') }}</button>
        </div>
        <div v-else-if="companions.length === 0" class="companion-chat-state">
          <p class="companion-chat-state-title">{{ t('companion.chat.noCompanionsTitle') }}</p>
          <p class="companion-chat-state-body">{{ t('companion.chat.noCompanionsBody') }}</p>
        </div>

        <template v-else>
          <p v-if="chatLoading" class="companion-chat-state">{{ t('companion.chat.loading') }}</p>
          <div v-else ref="logRef" class="companion-log">
            <p v-if="messages.length === 0" class="companion-log-empty">{{ t('companion.chat.noMessages') }}</p>
            <article
              v-for="message in messages"
              :key="message.id"
              class="companion-msg"
              :class="`companion-msg--${message.author_kind}`"
            >
              <AgentPetAvatar
                v-if="message.author_kind === 'companion'"
                class="companion-msg-avatar companion-msg-avatar--pet"
                :pet-slug="authorPetSlug(message)"
                :size="24"
                :alt="authorLabel(message)"
              />
              <span v-else class="companion-msg-avatar" :style="{ backgroundColor: authorTone(message) }" aria-hidden="true">
                {{ authorInitial(message) }}
              </span>
              <div class="companion-msg-main">
                <div class="companion-msg-meta">
                  <span class="companion-msg-author">{{ authorLabel(message) }}</span>
                  <span class="companion-msg-time">{{ clock(message.created_at) }}</span>
                </div>
                <p class="companion-msg-body">{{ message.body }}</p>
              </div>
            </article>
          </div>

          <p v-if="paused" class="companion-run companion-run--paused">{{ t('companion.chat.pausedBadge') }}</p>
          <p v-if="thinking" class="companion-run companion-run--thinking">
            <span class="companion-run-dots" aria-hidden="true"><span /><span /><span /></span>
            {{ t('companion.chat.thinking') }}
          </p>
          <p v-for="failure in failures" :key="failure.id" class="companion-run companion-run--failed">
            {{ runFailureText(failure) }}
          </p>
          <p v-if="chatError" class="companion-chat-error">
            {{ chatError }}
            <button class="companion-retry" type="button" @click="retryChat">{{ t('companion.chat.retry') }}</button>
          </p>
          <p v-if="sendError" class="companion-chat-error">{{ sendError }}</p>
        </template>

        <div
          v-if="inviteOpen"
          class="companion-invite"
          role="dialog"
          :aria-label="t('companion.chat.inviteTitle')"
          @pointerdown.stop
        >
          <p class="companion-invite-title">{{ t('companion.chat.inviteTitle') }}</p>
          <p v-if="inviteCandidates.length === 0" class="companion-invite-empty">{{ t('companion.chat.inviteEmpty') }}</p>
          <label v-for="candidate in inviteCandidates" :key="candidate.id" class="companion-invite-option">
            <input
              v-model="inviteTargetId"
              type="radio"
              name="companion-invite-target"
              :value="candidate.id"
            />
            <span>{{ candidate.name }}</span>
          </label>
          <label class="companion-invite-share">
            <input v-model="inviteShareHistory" type="checkbox" />
            <span>{{ t('companion.chat.shareHistory') }}</span>
          </label>
          <p v-if="inviteError" class="companion-chat-error">{{ inviteError }}</p>
          <div class="companion-invite-actions">
            <button class="companion-secondary" type="button" @click="inviteOpen = false">
              {{ t('companion.chat.inviteCancel') }}
            </button>
            <button
              class="companion-primary"
              type="button"
              :disabled="!inviteTargetId || inviteBusy"
              @click="confirmInvite"
            >
              {{ t('companion.chat.inviteConfirm') }}
            </button>
          </div>
        </div>
      </div>

      <form
        v-if="companions.length > 0 && !loadError"
        class="companion-composer"
        @submit.prevent="send"
        @pointerdown.stop
      >
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
          <Pause v-if="thinking" :size="14" />
          <Send v-else :size="15" />
        </button>
      </form>
    </section>
  </div>
</template>

<style scoped>
/* The window is transparent and only as large as the current shape; the OS window has no
   shadow, so every edge this page draws must be its own. */
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
  width: 100%;
  height: 100%;
  overflow: hidden;
  color: var(--text-primary);
  background: transparent;
  user-select: none;
  -webkit-user-select: none;
}

/* ---- Collapsed capsule ---- */

.companion-capsule {
  position: relative;
  display: block;
  width: 100%;
  height: 100%;
  padding: 0;
  overflow: hidden;
  border: 0;
  color: var(--text-muted);
  background: transparent;
  cursor: grab;
  touch-action: none;
  /* The capsule slides in from behind the screen edge, so collapsing reads as the companion
     ducking back rather than the button simply reappearing. */
  animation: companion-capsule-enter 260ms cubic-bezier(0.2, 0, 0, 1) both;
  transition: transform 160ms cubic-bezier(0.2, 0, 0, 1);
}

/* No shell: a pill behind the pet is what made the old capsule read as a picture in a frame. The
   window itself is transparent, so both styles stand on the desktop on their own. */

/* Hovering leans the capsule into the screen by 4px: the character notices you before you click.
   This has to be `transform` while the entrance animation owns `translate` — a `both`-filled
   animation beats any normal declaration on the property it animates, forever. */
.companion-capsule:hover,
.companion-capsule:focus-visible {
  transform: translateX(var(--capsule-nudge, 0));
}

.companion-capsule:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: -2px;
}

.companion--dragging .companion-capsule {
  cursor: grabbing;
}

/* `--capsule-enter-x` slides in from behind the screen edge; `--capsule-nudge` is the hover lean,
   inward on whichever side the companion is docked. */
.companion--side-right .companion-capsule { --capsule-enter-x: 16px; --capsule-nudge: -4px; }
.companion--side-left .companion-capsule { --capsule-enter-x: -16px; --capsule-nudge: 4px; }

@keyframes companion-capsule-enter {
  from { opacity: 0; translate: var(--capsule-enter-x, 0) 0; }
  to { opacity: 1; translate: 0 0; }
}

/* The peek: the whole pet, leaned out of the screen edge. The window is sized from this frame
   (companionGeometry.cjs's PEEK_HEIGHT / PEEK_WIDTH), so the tilt is only a rotation — nothing is
   cropped by it. Clip and tilt are two elements because the breathing animation owns `transform`:
   one element cannot rotate and scale from the same property. */
.companion-capsule-stage {
  position: absolute;
  inset: 0;
  overflow: hidden;
}

.companion-capsule-tilt {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  transform-origin: 50% 50%;
}

/* Leaning inward, over the screen: a right-docked pet tilts left, a left-docked one tilts right. */
.companion-capsule--right .companion-capsule-tilt { transform: rotate(-22deg); }
.companion-capsule--left .companion-capsule-tilt { transform: rotate(22deg); }

.companion-peek-pet {
  flex: none;
  animation: companion-breathe 4.2s ease-in-out infinite;
  transform-origin: 50% 50%;
}

/* A grip has no character to show: the window is transparent apart from one narrow handle. */
.companion-capsule-grip {
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
}

.companion-capsule-grip > span {
  width: 10px;
  height: 84px;
  border-radius: 999px;
  background: var(--bg-secondary);
  box-shadow: 0 0 0 1px var(--border-default), 0 2px 8px rgb(0 0 0 / 18%);
  transition: box-shadow 140ms ease, background-color 140ms ease;
}

.companion-capsule--grip:hover .companion-capsule-grip > span {
  background: var(--bg-tertiary);
  box-shadow: 0 0 0 1px var(--accent-primary), 0 2px 10px rgb(0 0 0 / 24%);
}

/* The handle is the entire affordance; a scrim and an arrow over it would only add noise. */
.companion-capsule--grip .companion-capsule-chevron {
  display: none;
}

/* Focus has to land on the handle, not on the transparent window around it. */
.companion-capsule--grip:focus-visible {
  outline: none;
}

.companion-capsule--grip:focus-visible .companion-capsule-grip > span {
  box-shadow: 0 0 0 1px var(--accent-primary), 0 0 0 3px var(--accent-soft);
}

/* On a grip the pet's mood has nowhere else to show, so the dot rides on top of the handle. */
.companion-capsule--grip .companion-capsule-status {
  top: 20px;
  left: 50%;
  margin-top: 0;
  transform: translateX(-50%);
}

.companion-capsule-glyph {
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
  color: var(--text-muted);
}

/* The status dot sits on the inward edge — the screen border hides the outward one. */
.companion-capsule-status {
  position: absolute;
  top: 50%;
  width: 7px;
  height: 7px;
  margin-top: -3.5px;
  border-radius: 50%;
  background: var(--text-muted);
  box-shadow: 0 0 0 2px var(--bg-secondary);
}

.companion--side-right .companion-capsule-status { left: 7px; }
.companion--side-left .companion-capsule-status { right: 7px; }

.companion-capsule-status.is-thinking {
  background: var(--accent-primary);
  animation: companion-status-pulse 1.4s ease-in-out infinite;
}

.companion-capsule-status.is-failed { background: var(--accent-danger); }
.companion-capsule-status.is-paused { background: var(--accent-warning); opacity: 0.75; }

/* At rest the capsule shows the companion, not a control. On hover a small badge appears on the
   side that is actually on screen — the outer half of a peeking window hangs off the display. */
.companion-capsule-chevron {
  position: absolute;
  bottom: 10px;
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  opacity: 0;
  color: var(--accent-primary);
  background: var(--bg-secondary);
  box-shadow: 0 0 0 1px var(--border-default), 0 2px 6px rgb(0 0 0 / 26%);
  transition: opacity 140ms ease;
}

.companion-capsule--right .companion-capsule-chevron { left: 10px; }
.companion-capsule--left .companion-capsule-chevron { right: 10px; }

.companion-capsule:hover .companion-capsule-chevron,
.companion-capsule:focus-visible .companion-capsule-chevron {
  opacity: 1;
}

@keyframes companion-breathe {
  0%, 100% { transform: scale(1) translateY(0); }
  50% { transform: scale(1.028) translateY(-1px); }
}

@keyframes companion-status-pulse {
  0%, 100% { opacity: 1; transform: scale(1); }
  50% { opacity: 0.4; transform: scale(0.78); }
}

/* Breathing, sliding in and a pulsing dot are decoration, not feedback: with reduced motion the
   capsule is simply there, and a thinking run still reads from the dot's colour. */
@media (prefers-reduced-motion: reduce) {
  .companion-capsule,
  .companion-peek-pet,
  .companion-capsule-status.is-thinking {
    animation: none;
  }

  /* The lean, the typing wave and the pat caption are all decoration; the state itself still reads
     from the dot's colour, the row's text and the bubble. */
  .companion-capsule {
    transition: none;
  }

  .companion-capsule:hover,
  .companion-capsule:focus-visible {
    transform: none;
  }

  .companion-run-dots > span,
  .companion-pat-note {
    animation: none;
  }

  .companion-run-dots > span {
    opacity: 0.7;
  }
}

/* ---- Expanded panel ---- */

.companion-panel {
  position: relative;
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  overflow: hidden;
  border: 1px solid var(--border-default);
  border-radius: 14px;
  color: var(--text-primary);
  background: var(--bg-secondary);
  cursor: grab;
  touch-action: none;
}

/* The shape is flush with the screen edge on its docked side: square that side off so the
   rounded corners do not leave a see-through notch against the display border. */
.companion--side-right .companion-panel {
  border-right: 0;
  border-radius: 14px 0 0 14px;
}

.companion--side-left .companion-panel {
  border-left: 0;
  border-radius: 0 14px 14px 0;
}

.companion--dragging .companion-panel {
  cursor: grabbing;
}

.companion-header {
  display: flex;
  flex-shrink: 0;
  gap: 8px;
  align-items: center;
  padding: 10px 10px 9px 12px;
  border-bottom: 1px solid var(--border-muted);
}

/* The face doubles as the one interaction this window owns: a click makes it wave. */
.companion-header-face {
  display: grid;
  flex: none;
  place-items: center;
  width: 32px;
  height: 32px;
  padding: 0;
  overflow: hidden;
  border: 0;
  border-radius: 8px;
  background: var(--bg-tertiary);
  cursor: pointer;
}

.companion-header-face:hover {
  box-shadow: 0 0 0 1px var(--accent-primary);
}

.companion-header-face:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 1px;
}

.companion-header-face.is-greeting {
  animation: companion-greet 420ms cubic-bezier(0.3, 0.7, 0.4, 1);
}

@keyframes companion-greet {
  0%, 100% { transform: translateY(0) rotate(0); }
  35% { transform: translateY(-4px) rotate(-6deg); }
  70% { transform: translateY(-1px) rotate(4deg); }
}

.companion-header-glyph {
  display: grid;
  flex: none;
  place-items: center;
  width: 28px;
  height: 30px;
  border-radius: 8px;
  color: var(--text-muted);
  background: var(--bg-tertiary);
}

.companion-heading {
  flex: 1;
  min-width: 0;
}

.companion-title,
.companion-subtitle {
  margin: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-title {
  font-size: 13px;
  font-weight: 600;
  line-height: 1.3;
}

.companion-subtitle {
  margin-top: 2px;
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.3;
}

.companion-actions {
  display: flex;
  flex-shrink: 0;
  gap: 2px;
}

.companion-action {
  display: grid;
  width: 24px;
  height: 24px;
  padding: 0;
  place-items: center;
  border: 0;
  border-radius: 6px;
  color: var(--text-secondary);
  background: transparent;
  cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease;
}

.companion-action:hover:not(:disabled) {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.companion-action:disabled {
  opacity: 0.45;
  cursor: default;
}

.companion-action:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: -2px;
}

.companion-menu-wrap {
  position: relative;
}

.companion-menu {
  position: absolute;
  top: 26px;
  right: 0;
  z-index: 5;
  min-width: 150px;
  padding: 4px;
  border: 1px solid var(--border-default);
  border-radius: 8px;
  background: var(--bg-secondary);
  box-shadow: 0 8px 24px rgb(0 0 0 / 28%);
}

.companion-menu-item {
  display: block;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 6px;
  color: var(--text-primary);
  background: transparent;
  font-size: 11.5px;
  text-align: left;
  cursor: pointer;
}

.companion-menu-item:hover:not(:disabled) {
  background: var(--bg-hover);
}

.companion-menu-item:disabled {
  color: var(--text-muted);
  cursor: default;
}

/* ---- Chat ---- */

.companion-pickers {
  display: flex;
  flex-shrink: 0;
  gap: 6px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border-muted);
}

.companion-picker-face {
  display: grid;
  flex: none;
  place-items: center;
  width: 24px;
  height: 24px;
  overflow: hidden;
  border-radius: 6px;
  background: var(--bg-tertiary);
}

.companion-select {
  flex: 1;
  min-width: 0;
  padding: 4px 20px 4px 8px;
  border: 1px solid var(--border-default);
  border-radius: 6px;
  color: var(--text-primary);
  /* `background-color` + a separate `background-image`: the shorthand would drop the chevron. */
  background-color: var(--bg-tertiary);
  background-image: url("data:image/svg+xml;charset=utf-8,%3Csvg xmlns='http://www.w3.org/2000/svg' width='10' height='6' viewBox='0 0 10 6'%3E%3Cpath d='M1 1l4 4 4-4' fill='none' stroke='%23888888' stroke-width='1.4' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E");
  background-repeat: no-repeat;
  background-position: right 6px center;
  appearance: none;
  font-size: 11.5px;
}

.companion-select:disabled {
  opacity: 0.5;
}

.companion-chat {
  position: relative;
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 6px;
  min-height: 0;
  padding: 10px 12px;
  overflow: hidden;
  cursor: default;
}

.companion-chat-state {
  margin: 0;
  color: var(--text-secondary);
  font-size: 11.5px;
  line-height: 1.6;
  text-align: center;
}

.companion-chat-state--error {
  display: flex;
  flex-direction: column;
  gap: 8px;
  align-items: center;
  justify-content: center;
  height: 100%;
}

.companion-chat-state-title {
  margin: 0 0 4px;
  color: var(--text-primary);
  font-size: 12.5px;
  font-weight: 600;
}

.companion-chat-state-body {
  margin: 0;
  max-width: 250px;
  color: var(--text-secondary);
  font-size: 11.5px;
  line-height: 1.6;
}

.companion-chat > .companion-chat-state:first-child {
  margin: auto;
}

.companion-log {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 8px;
  min-height: 0;
  overflow-x: hidden;
  overflow-y: auto;
  padding-right: 2px;
}

.companion-log-empty {
  margin: auto;
  color: var(--text-muted);
  font-size: 11.5px;
}

/* The pat reaction floats over the conversation instead of entering it: it acknowledges a gesture,
   it is not something either side said, so it must never look like a message. It sits on the panel
   so it shows in both open shapes, just above the composer. */
.companion-pat-note {
  position: absolute;
  right: 14px;
  bottom: 58px;
  z-index: 2;
  margin: 0;
  padding: 4px 10px;
  border-radius: 999px;
  background: var(--accent-soft);
  color: var(--text-primary);
  font-size: 11px;
  pointer-events: none;
  animation: companion-pat-in 200ms ease-out both;
}

@keyframes companion-pat-in {
  from { opacity: 0; transform: translateY(4px); }
  to { opacity: 1; transform: translateY(0); }
}

.companion-msg {
  display: flex;
  gap: 8px;
  align-items: flex-start;
}

.companion-msg--user {
  flex-direction: row-reverse;
  text-align: right;
}

.companion-msg-avatar {
  display: grid;
  flex-shrink: 0;
  width: 22px;
  height: 22px;
  place-items: center;
  border-radius: 50%;
  color: #16131c;
  font-size: 10px;
  font-weight: 700;
  opacity: 0.9;
}

/* A pet face keeps its sprite aspect; the circle crop stays for the tone fallback. */
.companion-msg-avatar--pet {
  width: auto;
  height: 24px;
  background: transparent !important;
}

.companion-msg--system .companion-msg-avatar {
  color: var(--text-muted);
  background: transparent !important;
  border: 1px solid var(--border-muted);
}

.companion-msg-main {
  min-width: 0;
  max-width: calc(100% - 34px);
}

.companion-msg-meta {
  display: flex;
  gap: 6px;
  align-items: baseline;
  margin-bottom: 2px;
}

.companion-msg--user .companion-msg-meta {
  justify-content: flex-end;
}

.companion-msg-author {
  color: var(--text-secondary);
  font-size: 10.5px;
  font-weight: 600;
}

.companion-msg-time {
  color: var(--text-muted);
  font-size: 10px;
}

.companion-msg-body {
  display: inline-block;
  margin: 0;
  padding: 6px 9px;
  border-radius: 9px;
  background: var(--bg-tertiary);
  font-size: 12px;
  line-height: 1.45;
  overflow-wrap: anywhere;
  text-align: left;
  user-select: text;
  -webkit-user-select: text;
}

.companion-msg--user .companion-msg-body {
  background: var(--bg-user-msg);
}

.companion-msg--system .companion-msg-body {
  color: var(--text-muted);
  background: transparent;
  font-size: 11px;
}

/* The panel's strip: 88px is the most the 340x520 panel can give before the conversation drops
   under ~280px, which is still four or five bubbles. */
.companion-panel-stage {
  margin: 2px 0 2px;
  background: radial-gradient(ellipse at 50% 100%, var(--accent-soft) 0%, transparent 72%);
}

/* ---- Immersive call ---- */

/* A call screen, not a chat window: the figure takes the height, the last exchange sits under it
   as subtitles, and the composer below is the only control. Nothing here scrolls. */
.companion-call {
  position: relative;
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 10px;
  align-items: center;
  justify-content: flex-end;
  min-height: 0;
  padding: 6px 18px 12px;
  overflow: hidden;
  cursor: default;
}

/* The figure is a control: one click on the pet opens the call. The toolbar button stays for the
   people who never think to click an illustration. */
.companion-stage-button {
  display: flex;
  flex: none;
  justify-content: center;
  width: 100%;
  padding: 0;
  border: 0;
  background: transparent;
  cursor: pointer;
}

.companion-stage-button:hover .companion-panel-stage {
  filter: brightness(1.06);
}

.companion-stage-button:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: -2px;
}

.companion-call-glyph {
  display: grid;
  flex: 1;
  place-items: center;
  color: var(--text-muted);
  opacity: 0.45;
}

.companion-call-subtitles {
  display: flex;
  flex: none;
  flex-direction: column;
  gap: 5px;
  align-items: center;
  width: 100%;
  /* Reserved so the composer does not jump when the first line lands. */
  min-height: 46px;
  text-align: center;
}

.companion-call-line {
  margin: 0;
  max-width: 100%;
}

.companion-call-author {
  display: block;
  margin-bottom: 1px;
  color: var(--text-muted);
  font-size: 10px;
  letter-spacing: 0.02em;
}

.companion-call-body {
  display: block;
  color: var(--text-primary);
  font-size: 14px;
  line-height: 1.55;
  overflow-wrap: anywhere;
  user-select: text;
  -webkit-user-select: text;
}

/* The line before it stays on screen, dimmed and smaller: enough context to follow the exchange
   without turning the call back into a scrollback. */
.companion-call-line.is-past {
  opacity: 0.42;
}

.companion-call-line.is-past .companion-call-body {
  font-size: 12.5px;
}

.companion-call-status {
  display: flex;
  gap: 6px;
  align-items: center;
  margin: 0;
  color: var(--text-secondary);
  font-size: 11.5px;
}

.companion-call-status--failed {
  color: #e29393;
}

.companion-call-note {
  margin: 0;
  max-width: 320px;
  color: var(--text-muted);
  font-size: 12px;
  line-height: 1.6;
}

.companion-call-note--error {
  color: #e29393;
}

.companion-run {
  display: flex;
  flex-shrink: 0;
  gap: 6px;
  align-items: center;
  margin: 0;
  padding: 5px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-tertiary);
  color: var(--text-secondary);
  font-size: 11px;
  line-height: 1.4;
}

.companion-run--failed {
  border-color: color-mix(in srgb, #e26d6d 40%, var(--border-muted));
  color: #e29393;
}

.companion-run--paused {
  color: var(--text-muted);
}

/* Three dots in a travelling wave: one pulsing dot reads as a status light, three read as someone
   typing. Same 1.1s cycle as before, so nothing else about the row changes. */
.companion-run-dots {
  display: inline-flex;
  flex: none;
  gap: 3px;
  align-items: center;
}

.companion-run-dots > span {
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: var(--accent-primary);
  animation: companion-typing 1.1s ease-in-out infinite;
}

.companion-run-dots > span:nth-child(2) { animation-delay: 0.16s; }
.companion-run-dots > span:nth-child(3) { animation-delay: 0.32s; }

@keyframes companion-typing {
  0%, 60%, 100% { opacity: 0.3; transform: translateY(0); }
  30% { opacity: 1; transform: translateY(-2px); }
}

.companion-chat-error {
  flex-shrink: 0;
  margin: 0;
  color: #e29393;
  font-size: 11px;
  line-height: 1.4;
  overflow-wrap: anywhere;
}

.companion-retry {
  padding: 2px 7px;
  border: 1px solid var(--border-default);
  border-radius: 6px;
  color: var(--text-primary);
  background: var(--bg-tertiary);
  font-size: 11px;
  cursor: pointer;
}

.companion-retry:hover {
  background: var(--bg-hover);
}

/* ---- Composer ---- */

.companion-composer {
  display: flex;
  flex-shrink: 0;
  gap: 6px;
  align-items: center;
  padding: 8px 12px 10px;
  border-top: 1px solid var(--border-muted);
}

.companion-input {
  flex: 1;
  min-width: 0;
  min-height: 28px;
  max-height: 96px;
  padding: 5px 9px;
  border: 1px solid var(--border-default);
  border-radius: 7px;
  color: var(--text-primary);
  background: var(--bg-tertiary);
  font-family: inherit;
  font-size: 12px;
  line-height: 1.45;
  /* The height is set from `scrollHeight`; letting the user drag it would fight that. */
  resize: none;
  overflow-y: auto;
  user-select: text;
  -webkit-user-select: text;
}

.companion-input:focus {
  border-color: var(--accent-primary);
  outline: none;
}

.companion-input::placeholder {
  color: var(--text-muted);
}

.companion-send {
  display: grid;
  flex-shrink: 0;
  width: 28px;
  height: 28px;
  padding: 0;
  place-items: center;
  border: 1px solid var(--border-default);
  border-radius: 7px;
  color: var(--text-primary);
  background: var(--accent-soft);
  cursor: pointer;
}

.companion-send:hover:not(:disabled) {
  border-color: var(--accent-primary);
}

.companion-send:disabled {
  opacity: 0.45;
  cursor: default;
}

/* The same key while a turn is running: a quieter fill, because pausing is not the primary act. */
.companion-send.is-pause {
  color: var(--text-secondary);
  background: transparent;
}

/* ---- Invite dialog (inline, never a second window) ---- */

.companion-invite {
  position: absolute;
  inset: 0;
  z-index: 6;
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 10px;
  padding: 14px;
  overflow-y: auto;
  border: 1px solid var(--border-default);
  border-radius: 10px;
  background: var(--bg-secondary);
  box-shadow: 0 12px 32px rgb(0 0 0 / 32%);
}

.companion-invite-title {
  margin: 0;
  font-size: 12.5px;
  font-weight: 600;
}

.companion-invite-empty {
  margin: 0;
  color: var(--text-muted);
  font-size: 11.5px;
}

.companion-invite-option,
.companion-invite-share {
  display: flex;
  gap: 7px;
  align-items: center;
  color: var(--text-primary);
  font-size: 11.5px;
  cursor: pointer;
}

.companion-invite-share {
  color: var(--text-secondary);
}

.companion-invite-actions {
  display: flex;
  gap: 8px;
  justify-content: flex-end;
  margin-top: auto;
  padding-top: 6px;
}

.companion-secondary,
.companion-primary {
  padding: 5px 12px;
  border: 1px solid var(--border-default);
  border-radius: 7px;
  font-size: 11.5px;
  cursor: pointer;
}

.companion-secondary {
  color: var(--text-primary);
  background: var(--bg-tertiary);
}

.companion-primary {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: var(--accent-soft);
}

.companion-primary:disabled {
  opacity: 0.45;
  cursor: default;
}
</style>
