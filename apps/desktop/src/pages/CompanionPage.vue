<script setup lang="ts">
import {
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  ChevronsRight,
  Ellipsis,
  Maximize2,
  Minimize2,
  Pause,
  PawPrint,
  Send,
  UserPlus,
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  COMPANION_DELTA_EVENT,
  COMPANION_RUN_EVENT,
  COMPANION_SETTINGS_EVENT,
  COMPANION_STAGE_CUE_EVENT,
  type CompanionEventNotice,
} from '@/events/companionEventTypes'
import { ApiError } from '@/lib/apiError'
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

/**
 * Satellite Companion Capsule & Drawer Shell:
 * Renders the Electron main-process window shape (`capsule` | `panel` | `immersive`) and
 * consumes live `companion.*` SSE frames relayed over BroadcastChannel (zero polling timers).
 */
const { t, locale } = useI18n()
const state = ref<CompanionState | null>(null)
const dragging = ref(false)
const mode = computed<'capsule' | 'panel' | 'immersive'>(() => {
  if (state.value?.mode) return state.value.mode
  return state.value?.expanded === true ? 'panel' : 'capsule'
})
const expanded = computed(() => mode.value !== 'capsule')
const side = computed<'left' | 'right'>(() => state.value?.side ?? 'right')
const capsuleStyle = computed<'peek' | 'grip'>(() => state.value?.capsuleStyle ?? 'peek')

const SWIPE_EXPAND_PX = 26
const AXIS_LOCK_PX = 4
const CLICK_SUPPRESS_MS = 350
const NOTICE_DEBOUNCE_MS = 120
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

const expand = () => command(() => window.tinadec.companion.expand(), 'expand')
const collapse = () => command(() => window.tinadec.companion.collapse(), 'collapse')
const setMode = (next: 'capsule' | 'panel' | 'immersive') => command(() => window.tinadec.companion.setMode(next), 'setMode')
const dock = (next: 'left' | 'right') => {
  if (side.value !== next) command(() => window.tinadec.companion.setSide(next), 'setSide')
}

function trackPointer(event: PointerEvent, windowTop: number, axis: DragStart['axis']): DragStart {
  ;(event.currentTarget as HTMLElement).setPointerCapture(event.pointerId)
  dragging.value = true
  return { pointerId: event.pointerId, pointerX: event.screenX, pointerY: event.screenY, windowTop, axis }
}

function beginCapsuleDrag(event: PointerEvent): void {
  if (event.button === 0) capsuleDrag = trackPointer(event, window.screenY, null)
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
    window.tinadec.companion.moveTo(drag.windowTop + dy).catch((error) => console.warn('[companion] moveTo failed:', error))
  }
}

function endCapsuleDrag(event: PointerEvent): void {
  const drag = capsuleDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  capsuleDrag = null
  dragging.value = false
  if (!drag.axis) return
  suppressClickUntil = performance.now() + CLICK_SUPPRESS_MS
  if (drag.axis !== 'x') return
  const inward = side.value === 'left' ? event.screenX - drag.pointerX : drag.pointerX - event.screenX
  if (inward >= SWIPE_EXPAND_PX) expand()
}

function onCapsuleClick(event: MouseEvent): void {
  if (event.detail !== 0 && performance.now() < suppressClickUntil) return
  expand()
}

function beginPanelDrag(event: PointerEvent): void {
  if (event.button === 0) panelDrag = trackPointer(event, window.screenY, 'y')
}

function movePanelDrag(event: PointerEvent): void {
  const drag = panelDrag
  if (!drag || drag.pointerId !== event.pointerId) return
  window.tinadec.companion.moveTo(drag.windowTop + event.screenY - drag.pointerY).catch((error) => console.warn('[companion] moveTo failed:', error))
}

function endPanelDrag(event: PointerEvent): void {
  if (!panelDrag || panelDrag.pointerId !== event.pointerId) return
  panelDrag = null
  dragging.value = false
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || mode.value === 'capsule') return
  event.preventDefault()
  if (mode.value === 'immersive') setMode('panel')
  else collapse()
}

watch(mode, (next, previous) => {
  if (next === 'panel' && previous === 'immersive') scrollToBottom()
})

// ── Satellite chat & SSE relay state (zero polling timers) ────────────

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
const composerRef = ref<HTMLTextAreaElement | null>(null)

let lastSeq = 0
let loadToken = 0
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

const capsuleMood = computed<'idle' | 'thinking' | 'failed' | 'paused'>(() => {
  if (failures.value.length > 0) return 'failed'
  if (thinking.value) return 'thinking'
  if (paused.value) return 'paused'
  return 'idle'
})

const observedStageState = computed<PetState>(() => {
  if (liveStageSpeaking.value) return 'running'
  if (capsuleMood.value === 'thinking') return 'waiting'
  if (capsuleMood.value === 'failed') return 'failed'
  return 'idle'
})

const impulse = ref<PetState | null>(null)
const greeting = ref(false)
const patNote = ref('')
let impulseTimer: ReturnType<typeof setTimeout> | undefined
const stageState = computed<PetState>(() => impulse.value ?? observedStageState.value)

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
  if (selectedCompanionId.value) void companionBridge.recordEmote(selectedCompanionId.value, 'wave').catch(() => {})
}

function pat(event: MouseEvent): void {
  const target = event.target as HTMLElement | null
  if (target?.closest('.companion-msg, button, a, input, textarea, select, .companion-invite')) return
  playImpulse('jumping', t('companion.pat'))
  if (selectedCompanionId.value) void companionBridge.recordEmote(selectedCompanionId.value, 'pat').catch(() => {})
}

const viewportHeight = ref(typeof window === 'undefined' ? 0 : window.innerHeight)
const callStageHeight = computed(() => Math.min(500, Math.max(180, Math.round(viewportHeight.value * 0.6))))
const onViewportResize = () => { viewportHeight.value = window.innerHeight }

const callLines = computed(() => messages.value.filter((message) => message.author_kind !== 'system').slice(-2))

const headerSubtitle = computed(() => {
  if (failures.value.length > 0) return t('companion.stateFailed')
  if (thinking.value) return t('companion.stateThinking')
  if (paused.value) return t('companion.statePaused')
  const conversation = activeConversation.value
  if (!conversation) return activeCompanion.value?.tagline ?? t('companion.subtitle')
  if (conversation.kind === 'group') {
    const members = conversation.members.filter((m) => m.status === 'active').map((m) => m.display_name).join(' · ')
    return t('companion.chat.groupSubtitle', { title: conversation.title || t('companion.chat.groupLabel'), members })
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

function isHidden(): boolean {
  return typeof document !== 'undefined' && document.visibilityState === 'hidden'
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

function speakFresh(fresh: CompanionMessage[]): void {
  if (!speakReplies.value || typeof window === 'undefined' || typeof window.speechSynthesis === 'undefined') return
  const body = fresh.filter((m) => m.author_kind === 'companion').at(-1)?.body?.trim()
  if (!body) return
  try {
    window.speechSynthesis.cancel()
    window.speechSynthesis.speak(new SpeechSynthesisUtterance(body))
  } catch { /* speech synthesis optional */ }
}

function mergeMessages(incoming: CompanionMessage[], speak = true): void {
  if (!incoming.length) return
  const ordered = new Map(messages.value.map((m) => [m.id, m]))
  const fresh: CompanionMessage[] = []
  for (const m of incoming) {
    if (!ordered.has(m.id)) fresh.push(m)
    ordered.set(m.id, m)
    lastSeq = Math.max(lastSeq, m.seq)
  }
  messages.value = [...ordered.values()].sort((a, b) => a.seq - b.seq)
  if (speak) speakFresh(fresh)
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
    mergeMessages(list ?? [], false)
    scrollToBottom()
  } catch (error) {
    if (token === loadToken) chatError.value = errorText(error)
  } finally {
    if (token === loadToken) chatLoading.value = false
  }
}

async function selectCompanion(id: string): Promise<void> {
  const token = ++loadToken
  selectedCompanionId.value = id
  try { localStorage.setItem(SELECTED_COMPANION_KEY, id) } catch { /* ignore */ }
  activeConversation.value = null
  conversations.value = []
  messages.value = []
  runs.value = {}
  trackedRunIds.value = []
  liveDeltaBodies.value = {}
  lastSeq = 0
  chatError.value = ''
  sendError.value = ''
  try {
    const conversation = await companionBridge.ensurePrivateConversation(id)
    if (token !== loadToken) return
    activeConversation.value = conversation
    const list = await companionBridge.listConversations(id)
    if (token !== loadToken) return
    conversations.value = list.some((item) => item.id === conversation.id) ? list : [conversation, ...list]
    await loadMessagesFor(conversation.id, token)
  } catch (error) {
    if (token === loadToken) chatError.value = errorText(error)
  }
}

async function adoptConversation(conversation: CompanionConversation): Promise<void> {
  const token = ++loadToken
  activeConversation.value = conversation
  messages.value = []
  runs.value = {}
  trackedRunIds.value = []
  liveDeltaBodies.value = {}
  lastSeq = 0
  sendError.value = ''
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
  loadError.value = ''
  try {
    const [settings, list] = await Promise.all([companionBridge.getSettings(), companionBridge.listCompanions()])
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

const retryChat = () => {
  if (selectedCompanionId.value) void selectCompanion(selectedCompanionId.value)
  else void boot()
}

const onCompanionSelect = (event: Event) => {
  const id = (event.target as HTMLSelectElement).value
  if (id && id !== selectedCompanionId.value) void selectCompanion(id)
}

const onConversationSelect = (event: Event) => {
  const id = (event.target as HTMLSelectElement).value
  const conv = conversations.value.find((item) => item.id === id)
  if (conv && conv.id !== activeConversation.value?.id) void adoptConversation(conv)
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
  sendError.value = ''
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
    sendError.value = errorText(error)
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
    mergeMessages(incoming ?? [])
    await Promise.all(trackedRunIds.value.map(async (id) => {
      try { runs.value = { ...runs.value, [id]: await companionBridge.getRun(id) } } catch { /* ignore */ }
    }))
    scrollToBottom()
  } catch (error) {
    console.warn('[companion] catchUp failed:', error)
  }
}

function onCompanionNotice(notice: CompanionEventNotice): void {
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
  try {
    const updated = await companionBridge.pauseConversation(conversation.id, !conversation.autonomy_paused)
    activeConversation.value = updated
    conversations.value = conversations.value.map((item) => (item.id === updated.id ? updated : item))
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
    if (selectedCompanionId.value) conversations.value = await companionBridge.listConversations(selectedCompanionId.value)
    await adoptConversation(result)
    if (!conversations.value.some((item) => item.id === result.id)) conversations.value = [result, ...conversations.value]
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
  disposeState = window.tinadec.companion.onStateChanged(applyState)
  unsubscribeRelay = subscribeCompanionEventRelay(onCompanionNotice)
  void window.tinadec.getAppConfig?.().then((config) => { speakReplies.value = config.companion_speak_replies === true }).catch(() => {})
  disposeSpeakSetting = window.tinadec.onCompanionSpeakRepliesChanged?.(({ speak_replies }) => {
    speakReplies.value = speak_replies === true
  })
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
                @click="menuOpen = false; togglePause()"
              >
                {{ paused ? t('companion.chat.resume') : t('companion.chat.pause') }}
              </button>
            </div>
          </div>
          <button
            v-if="mode !== 'immersive'"
            class="companion-action"
            type="button"
            :title="t(side === 'right' ? 'companion.dockLeft' : 'companion.dockRight')"
            :aria-label="t(side === 'right' ? 'companion.dockLeft' : 'companion.dockRight')"
            @pointerdown.stop
            @click="dock(side === 'right' ? 'left' : 'right')"
          >
            <ChevronsLeft v-if="side === 'right'" :size="15" />
            <ChevronsRight v-else :size="15" />
          </button>
          <button
            class="companion-action"
            type="button"
            :title="mode === 'immersive' ? t('companion.exitImmersive') : t('companion.immersive')"
            :aria-label="mode === 'immersive' ? t('companion.exitImmersive') : t('companion.immersive')"
            :aria-pressed="mode === 'immersive'"
            data-testid="companion-immersive-toggle"
            @pointerdown.stop
            @click="setMode(mode === 'immersive' ? 'panel' : 'immersive')"
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

      <button
        v-if="mode === 'panel' && companionPetSlug"
        class="companion-stage-button"
        type="button"
        :title="t('companion.immersive')"
        :aria-label="t('companion.immersive')"
        data-testid="companion-stage-expand"
        @pointerdown.stop
        @click="setMode('immersive')"
      >
        <CompanionStage
          class="companion-panel-stage"
          :pet-slug="companionPetSlug"
          :state="stageState"
          :speaking="liveStageSpeaking"
          :emotion="liveEmotion"
          :height="88"
          :alt="headerTitle"
        />
      </button>

      <div v-if="mode === 'immersive'" class="companion-call" @dblclick="pat">
        <CompanionStage
          v-if="companionPetSlug"
          class="companion-call-stage"
          :pet-slug="companionPetSlug"
          :state="stageState"
          :speaking="liveStageSpeaking"
          :emotion="liveEmotion"
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
          <p v-else-if="companions.length === 0" class="companion-call-note">{{ t('companion.chat.noCompanionsBody') }}</p>
          <template v-else>
            <p v-if="callLines.length === 0 && !activeStreamingText" class="companion-call-note">{{ t('companion.chat.noMessages') }}</p>
            <p
              v-for="(line, index) in callLines"
              :key="line.id"
              class="companion-call-line"
              :class="{ 'is-past': index < callLines.length - 1 }"
            >
              <span class="companion-call-author">{{ authorLabel(line) }}</span>
              <span class="companion-call-body">{{ line.body }}</span>
            </p>
            <p v-if="activeStreamingText" class="companion-call-line">
              <span class="companion-call-author">{{ headerTitle }}</span>
              <span class="companion-call-body">{{ activeStreamingText }}</span>
            </p>
            <p v-if="paused" class="companion-call-status">{{ t('companion.chat.pausedBadge') }}</p>
            <p v-if="thinking && !activeStreamingText" class="companion-call-status">{{ t('companion.chat.thinking') }}</p>
            <p v-for="failure in failures" :key="failure.id" class="companion-call-status companion-call-status--failed">{{ runFailureText(failure) }}</p>
            <p v-if="chatError" class="companion-call-status companion-call-status--failed">
              {{ chatError }}
              <button class="companion-retry" type="button" @click="retryChat">{{ t('companion.chat.retry') }}</button>
            </p>
            <p v-if="sendError" class="companion-call-status companion-call-status--failed">{{ sendError }}</p>
          </template>
        </div>
      </div>

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
            <p v-if="messages.length === 0 && !activeStreamingText" class="companion-log-empty">{{ t('companion.chat.noMessages') }}</p>
            <article v-for="message in messages" :key="message.id" class="companion-msg" :class="`companion-msg--${message.author_kind}`">
              <AgentPetAvatar
                v-if="message.author_kind === 'companion'"
                class="companion-msg-avatar companion-msg-avatar--pet"
                :pet-slug="authorPetSlug(message)"
                :size="24"
                :alt="authorLabel(message)"
              />
              <span v-else class="companion-msg-avatar" :style="{ backgroundColor: authorTone(message) }" aria-hidden="true">{{ authorInitial(message) }}</span>
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
          <p v-if="chatError" class="companion-chat-error">
            {{ chatError }}
            <button class="companion-retry" type="button" @click="retryChat">{{ t('companion.chat.retry') }}</button>
          </p>
          <p v-if="sendError" class="companion-chat-error">{{ sendError }}</p>
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
          <p v-if="inviteError" class="companion-chat-error">{{ inviteError }}</p>
          <div class="companion-invite-actions">
            <button class="companion-secondary" type="button" @click="inviteOpen = false">{{ t('companion.chat.inviteCancel') }}</button>
            <button class="companion-primary" type="button" :disabled="!inviteTargetId || inviteBusy" @click="confirmInvite">{{ t('companion.chat.inviteConfirm') }}</button>
          </div>
        </div>
      </div>

      <form v-if="companions.length > 0 && !loadError" class="companion-composer" @submit.prevent="send" @pointerdown.stop>
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
:global(html), :global(body), :global(#app) { width: 100%; height: 100%; margin: 0; overflow: hidden; background: transparent !important; }
.companion { width: 100%; height: 100%; overflow: hidden; color: var(--text-primary); background: transparent; user-select: none; -webkit-user-select: none; }
.companion-capsule { position: relative; display: block; width: 100%; height: 100%; padding: 0; overflow: hidden; border: 0; color: var(--text-muted); background: transparent; cursor: grab; touch-action: none; }
.companion-capsule-stage { position: absolute; inset: 0; display: grid; place-items: center; overflow: hidden; pointer-events: none; }
.companion-capsule-tilt { display: block; transform-origin: center center; transition: transform 180ms ease; }
.companion-capsule--right .companion-capsule-tilt { transform: translateX(18px) rotate(-22deg); }
.companion-capsule--left .companion-capsule-tilt { transform: translateX(-18px) rotate(22deg) scaleX(-1); }
.companion-capsule-grip { display: flex; align-items: center; justify-content: center; width: 100%; height: 100%; }
.companion-capsule-grip span { width: 4px; height: 28px; border-radius: 999px; background: currentColor; opacity: 0.6; }
.companion-capsule-status { position: absolute; left: 50%; bottom: 8px; width: 8px; height: 8px; transform: translateX(-50%); border-radius: 999px; background: var(--accent-success, #22c55e); }
.companion-capsule-status.is-thinking { background: var(--accent-primary, #58a6ff); }
.companion-capsule-status.is-failed { background: var(--accent-danger, #ef4444); }
.companion-capsule-status.is-paused { background: var(--accent-warning, #eab308); }
.companion-capsule-chevron { position: absolute; inset-inline: 0; top: 8px; display: flex; justify-content: center; opacity: 0.7; }
.companion-panel {
  display: flex; flex-direction: column; width: 100%; height: 100%; overflow: hidden;
  border-radius: 16px; border: 1px solid var(--border-muted); background: var(--surface-section);
  box-shadow: var(--shadow-panel); user-select: text; -webkit-user-select: text;
}
.companion-header { display: flex; align-items: center; gap: 8px; padding: 10px 10px 8px; border-bottom: 1px solid var(--border-muted); cursor: grab; }
.companion-header-face { padding: 0; border: 0; background: transparent; border-radius: 999px; cursor: pointer; }
.companion-header-face.is-greeting { transform: scale(1.08); }
.companion-header-glyph { width: 30px; height: 30px; display: grid; place-items: center; border-radius: 999px; background: var(--surface-raised); }
.companion-heading { flex: 1; min-width: 0; }
.companion-title { margin: 0; font-size: 13px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.companion-subtitle { margin: 1px 0 0; font-size: 11px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.companion-actions { display: flex; align-items: center; gap: 2px; position: relative; }
.companion-action { width: 26px; height: 26px; display: grid; place-items: center; border: 0; border-radius: 6px; background: transparent; color: var(--text-muted); cursor: pointer; }
.companion-action:hover:not(:disabled) { background: var(--surface-raised); color: var(--text-primary); }
.companion-action:disabled { opacity: 0.4; cursor: default; }
.companion-menu-wrap { position: relative; }
.companion-menu { position: absolute; right: 0; top: 100%; z-index: 10; min-width: 150px; padding: 4px; border-radius: 8px; border: 1px solid var(--border-muted); background: var(--surface-section); box-shadow: var(--shadow-panel); }
.companion-menu-item { width: 100%; padding: 6px 8px; border: 0; border-radius: 6px; background: transparent; color: var(--text-primary); font-size: 12px; text-align: left; cursor: pointer; }
.companion-menu-item:hover:not(:disabled) { background: var(--surface-raised); }
.companion-stage-button { display: flex; justify-content: center; width: 100%; padding: 6px 10px 0; border: 0; background: transparent; cursor: pointer; }
.companion-call { flex: 1; min-height: 0; display: flex; flex-direction: column; align-items: center; justify-content: space-between; padding: 12px; gap: 10px; overflow-y: auto; }
.companion-call-subtitles { width: 100%; display: flex; flex-direction: column; gap: 6px; text-align: center; }
.companion-call-line, .companion-call-note, .companion-call-status { margin: 0; font-size: 12px; }
.companion-call-line.is-past { opacity: 0.65; }
.companion-call-author { font-weight: 600; margin-right: 6px; }
.companion-call-status--failed, .companion-call-note--error { color: var(--accent-danger, #ef4444); }
.companion-pat-note { margin: 4px 12px 0; font-size: 11px; text-align: center; color: var(--accent-primary); }
.companion-pickers { display: flex; align-items: center; gap: 6px; padding: 8px 10px; border-bottom: 1px solid var(--border-muted); }
.companion-select { flex: 1; min-width: 0; padding: 4px 6px; border-radius: 6px; border: 1px solid var(--border-muted); background: var(--surface-raised); color: var(--text-primary); font-size: 12px; }
.companion-chat { position: relative; flex: 1; min-height: 0; display: flex; flex-direction: column; overflow: hidden; }
.companion-log { flex: 1; min-height: 0; overflow-y: auto; padding: 10px; display: flex; flex-direction: column; gap: 8px; }
.companion-log-empty, .companion-chat-state, .companion-run, .companion-chat-error { margin: 6px 10px; font-size: 12px; color: var(--text-muted); }
.companion-chat-error, .companion-run--failed { color: var(--accent-danger, #ef4444); }
.companion-msg { display: flex; align-items: flex-start; gap: 8px; }
.companion-msg-avatar { width: 24px; height: 24px; border-radius: 999px; display: grid; place-items: center; color: #fff; font-size: 11px; flex-shrink: 0; }
.companion-msg-main { min-width: 0; flex: 1; }
.companion-msg-meta { display: flex; align-items: center; gap: 6px; font-size: 11px; color: var(--text-muted); }
.companion-msg-body { margin: 2px 0 0; font-size: 12.5px; white-space: pre-wrap; overflow-wrap: anywhere; }
.companion-invite { position: absolute; inset: 8px; z-index: 8; display: flex; flex-direction: column; gap: 8px; padding: 12px; border-radius: 12px; border: 1px solid var(--border-card); background: var(--surface-section); box-shadow: var(--shadow-panel); overflow-y: auto; }
.companion-invite-title { margin: 0; font-size: 13px; font-weight: 600; }
.companion-invite-option, .companion-invite-share { display: flex; align-items: center; gap: 6px; font-size: 12px; }
.companion-invite-actions { display: flex; justify-content: flex-end; gap: 6px; margin-top: auto; }
.companion-secondary, .companion-primary, .companion-retry { padding: 4px 10px; border-radius: 6px; border: 1px solid var(--border-muted); background: var(--surface-raised); color: var(--text-primary); font-size: 12px; cursor: pointer; }
.companion-primary { background: var(--accent-primary); border-color: transparent; color: #fff; }
.companion-composer { display: flex; align-items: flex-end; gap: 6px; padding: 8px 10px; border-top: 1px solid var(--border-muted); }
.companion-input { flex: 1; min-width: 0; resize: none; padding: 6px 8px; border-radius: 8px; border: 1px solid var(--border-muted); background: var(--surface-raised); color: var(--text-primary); font: inherit; font-size: 12px; }
.companion-send { width: 30px; height: 30px; display: grid; place-items: center; border: 0; border-radius: 8px; background: var(--accent-primary); color: #fff; cursor: pointer; }
.companion-send:disabled { opacity: 0.45; cursor: default; }
</style>
