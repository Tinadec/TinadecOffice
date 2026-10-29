<script setup lang="ts">
import { ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight, Ellipsis, Send, UserPlus } from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '@/lib/apiError'
import { formatClock } from '@/lib/conversation/format'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
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
const expanded = computed(() => state.value?.expanded === true)
const side = computed<'left' | 'right'>(() => state.value?.side ?? 'right')

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
  if (event.key !== 'Escape' || !expanded.value) return
  event.preventDefault()
  collapse()
}

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
const headerSubtitle = computed(() => {
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
  document.removeEventListener('visibilitychange', onVisibilityChange)
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
      v-if="!expanded"
      class="companion-capsule"
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
      <span class="companion-grip" aria-hidden="true">
        <span class="companion-grip-dot" />
        <span class="companion-grip-dot" />
        <span class="companion-grip-dot" />
      </span>
      <span class="companion-capsule-chevron" aria-hidden="true">
        <ChevronLeft v-if="side === 'right'" :size="15" />
        <ChevronRight v-else :size="15" />
      </span>
    </button>

    <section
      v-else
      class="companion-panel"
      role="region"
      :aria-label="t('companion.title')"
      :title="t('companion.dragHint')"
      @pointerdown="beginPanelDrag"
      @pointermove="movePanelDrag"
      @pointerup="endPanelDrag"
      @pointercancel="endPanelDrag"
    >
      <header class="companion-header">
        <div class="companion-heading">
          <p class="companion-title">{{ headerTitle }}</p>
          <p class="companion-subtitle">{{ headerSubtitle }}</p>
        </div>
        <div class="companion-actions">
          <button
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
            v-if="side === 'right'"
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
            v-else
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

      <div v-if="companions.length" class="companion-pickers" @pointerdown.stop>
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

      <div class="companion-chat" @pointerdown.stop>
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
            <span class="companion-run-dot" aria-hidden="true" />{{ t('companion.chat.thinking') }}
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
        <input
          v-model="draft"
          class="companion-input"
          type="text"
          :placeholder="t('companion.chat.placeholder')"
          :aria-label="t('companion.chat.placeholder')"
          :disabled="!activeConversation || sending || chatLoading"
        />
        <button
          class="companion-send"
          type="submit"
          :title="t('companion.chat.send')"
          :aria-label="t('companion.chat.send')"
          :disabled="!canSend"
        >
          <Send :size="15" />
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
  display: flex;
  flex-direction: column;
  gap: 3px;
  align-items: center;
  justify-content: center;
  width: 100%;
  height: 100%;
  padding: 0;
  border: 1px solid var(--border-default);
  border-radius: 999px;
  color: var(--text-muted);
  background: var(--bg-secondary);
  cursor: grab;
  touch-action: none;
  transition: background-color 120ms ease, border-color 120ms ease, color 120ms ease;
}

.companion-capsule:hover {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: var(--bg-tertiary);
}

.companion-capsule:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: -3px;
}

.companion--dragging .companion-capsule {
  cursor: grabbing;
}

.companion-grip {
  display: flex;
  flex-direction: column;
  gap: 3px;
  transition: opacity 120ms ease;
}

.companion-grip-dot {
  width: 3px;
  height: 3px;
  border-radius: 50%;
  background: currentColor;
}

.companion-capsule-chevron {
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
  opacity: 0;
  color: var(--accent-primary);
  transition: opacity 120ms ease;
}

.companion-capsule:hover .companion-grip,
.companion-capsule:focus-visible .companion-grip {
  opacity: 0;
}

.companion-capsule:hover .companion-capsule-chevron,
.companion-capsule:focus-visible .companion-capsule-chevron {
  opacity: 1;
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

.companion-select {
  flex: 1;
  min-width: 0;
  padding: 4px 6px;
  border: 1px solid var(--border-default);
  border-radius: 6px;
  color: var(--text-primary);
  background: var(--bg-tertiary);
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

.companion-run-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--accent-primary);
  animation: companion-pulse 1.1s ease-in-out infinite;
}

@keyframes companion-pulse {
  0%,
  100% {
    opacity: 0.35;
  }

  50% {
    opacity: 1;
  }
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
  height: 28px;
  padding: 0 9px;
  border: 1px solid var(--border-default);
  border-radius: 7px;
  color: var(--text-primary);
  background: var(--bg-tertiary);
  font-size: 12px;
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
