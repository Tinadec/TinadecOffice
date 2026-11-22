<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Check,
  Hand,
  Pause,
  Play,
  Send,
  Settings,
  ShieldAlert,
  Sparkles,
  UserPlus,
  Volume2,
  VolumeX,
  Wrench,
  X,
} from '@lucide/vue'
import { UiButton } from '@/components/ui'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import CompanionWorkSessionCard from './CompanionWorkSessionCard.vue'
import { useNotifications } from '@/composables/useNotifications'
import { formatClock } from '@/lib/conversation/format'
import {
  companionApi,
  type CompanionConnectedWorkSession,
  type CompanionConversation,
  type CompanionMandate,
  type CompanionMessage,
  type CompanionRecruitment,
  type CompanionRun,
  type CompanionSummary,
} from '@/lib/companionApi'
import {
  activeCompanions,
  activity,
  autonomousCompanions,
  dismissProposalNotice,
  getWorkSessionById,
  liveProposals,
  liveWorkSessions,
  maxBotHopDepth,
  refreshCompanionCenter,
  refreshWorkSessions,
  selectConversation,
  stageCues,
  streamingTurns,
  workSessionBackflows,
  workSessionsByCompanion,
  type CompanionLiveToolCall,
} from '@/lib/companionStore'
import { avatarKindOf, type AvatarKind } from '@/pets/avatarKind'

const props = defineProps<{
  companion: CompanionSummary | null
  conversationId: string | null
  live2dSlugs?: readonly string[]
}>()

const emit = defineEmits<{
  'edit-companion': [companionId: string]
}>()

const { t, locale } = useI18n()
const { notify } = useNotifications()

const activeConversation = ref<CompanionConversation | null>(null)
const messages = ref<CompanionMessage[]>([])
const trackedRuns = ref<Record<string, CompanionRun>>({})
const mandates = ref<CompanionMandate[]>([])
const recruitments = ref<CompanionRecruitment[]>([])
const draft = ref('')
const loading = ref(false)
const sending = ref(false)
const inviteOpen = ref(false)
const inviteTargetId = ref('')
const inviteShareHistory = ref(false)
const hopBlockedNotice = ref<{ depth: number; limit: number } | null>(null)
const voiceEnabled = ref(false)
const logRef = ref<HTMLElement | null>(null)

let loadSeq = 0
let lastSeq = 0
let lastSpokenId: string | null = null

const live2dSlugsList = computed(() => [...(props.live2dSlugs ?? [])])
const speechAvailable = computed(
  () => typeof window !== 'undefined' && typeof window.speechSynthesis !== 'undefined' && typeof SpeechSynthesisUtterance !== 'undefined',
)

function kindOf(slug: string | null | undefined): AvatarKind {
  return avatarKindOf(slug, live2dSlugsList.value) ?? 'sprite'
}

function speakText(text: string) {
  if (!voiceEnabled.value || !speechAvailable.value || !text.trim()) return
  try {
    window.speechSynthesis.cancel()
    window.speechSynthesis.speak(new SpeechSynthesisUtterance(text))
  } catch { /* speech synthesis optional */ }
}

function toggleVoice() {
  if (!speechAvailable.value) return
  voiceEnabled.value = !voiceEnabled.value
  if (!voiceEnabled.value) {
    try { window.speechSynthesis.cancel() } catch { /* ignore */ }
  }
}

async function scrollToBottom() {
  await nextTick()
  const el = logRef.value
  if (el) el.scrollTop = el.scrollHeight
}

async function loadThread() {
  const convId = props.conversationId
  const botId = props.companion?.id ?? null
  if (!convId && !botId) {
    activeConversation.value = null
    messages.value = []
    mandates.value = []
    return
  }
  const seq = ++loadSeq
  loading.value = true
  hopBlockedNotice.value = null
  try {
    const conv = convId
      ? await companionApi.getConversation(convId)
      : await companionApi.ensurePrivateConversation(botId!)
    if (seq !== loadSeq) return
    activeConversation.value = conv
    const [msgList, botMandates, allRecruits] = await Promise.all([
      companionApi.listMessages(conv.id, undefined, 200),
      botId ? companionApi.listMandates(botId).catch(() => [] as CompanionMandate[]) : Promise.resolve([] as CompanionMandate[]),
      companionApi.listRecruitments().catch(() => [] as CompanionRecruitment[]),
    ])
    if (seq !== loadSeq) return
    messages.value = msgList ?? []
    lastSeq = messages.value.reduce((max, row) => Math.max(max, row.seq), 0)
    mandates.value = botMandates ?? []
    recruitments.value = allRecruits ?? []
    if (botId) void refreshWorkSessions(botId)
    await scrollToBottom()
  } catch (error) {
    if (seq === loadSeq) notify.error(error)
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}

watch(() => [props.companion?.id, props.conversationId] as const, () => {
  void loadThread()
}, { immediate: true })

async function catchUpMessages() {
  const conv = activeConversation.value
  if (!conv) return
  try {
    const fresh = await companionApi.listMessages(conv.id, lastSeq || undefined, 200)
    if (!fresh.length || activeConversation.value?.id !== conv.id) return
    const known = new Set(messages.value.map((m) => m.id))
    const appended: CompanionMessage[] = []
    for (const row of fresh) {
      if (!known.has(row.id)) {
        appended.push(row)
        messages.value.push(row)
      }
      if (row.seq > lastSeq) lastSeq = row.seq
    }
    if (appended.length > 0) {
      const latestBot = [...appended].reverse().find((m) => m.author_kind === 'companion' && m.id !== lastSpokenId)
      if (latestBot) {
        lastSpokenId = latestBot.id
        speakText(latestBot.body)
      }
      await scrollToBottom()
    }
  } catch { /* silent background sync */ }
}

const activeLiveTurns = computed(() => {
  const convId = activeConversation.value?.id
  if (!convId) return []
  return Object.values(streamingTurns.value).filter((turn) => turn.conversationId === convId)
})

watch(() => Object.keys(streamingTurns.value).length, (next, prev) => {
  if (next < prev) void catchUpMessages()
})

watch(() => activeLiveTurns.value.map((turn) => turn.body).join('\u0000'), () => {
  void scrollToBottom()
})

const orderedMessages = computed(() => [...messages.value].sort((a, b) => a.seq - b.seq))

const pendingProposals = computed(() => {
  const botId = props.companion?.id
  const items: Array<{
    key: string
    kind: 'mandate' | 'recruitment'
    id: string
    companionId: string
    revision: number
    title: string
    summary: string
    dispatchedSessionId?: string | null
  }> = []
  const seen = new Set<string>()
  for (const m of mandates.value) {
    if (m.status !== 'proposed') continue
    seen.add(`mandate:${m.id}`)
    const sessionId = typeof m.proposal?.session_id === 'string' ? m.proposal.session_id : null
    items.push({
      key: `mandate:${m.id}`,
      kind: 'mandate',
      id: m.id,
      companionId: m.companion_id,
      revision: m.revision,
      title: m.action,
      summary: m.summary ?? m.action,
      dispatchedSessionId: sessionId,
    })
  }
  for (const r of recruitments.value) {
    if (r.status !== 'proposed' || (botId && r.proposer_id !== botId)) continue
    seen.add(`recruitment:${r.id}`)
    items.push({
      key: `recruitment:${r.id}`,
      kind: 'recruitment',
      id: r.id,
      companionId: r.proposer_id,
      revision: r.revision,
      title: r.name,
      summary: r.rationale ?? r.persona_document ?? r.name,
    })
  }
  for (const live of liveProposals.value) {
    if (live.status !== 'proposed') continue
    if (botId && live.companionId && live.companionId !== botId) continue
    const key = `${live.kind}:${live.proposalId}`
    if (seen.has(key)) continue
    items.push({
      key,
      kind: live.kind,
      id: live.proposalId,
      companionId: live.companionId ?? botId ?? '',
      revision: 0,
      title: live.action ?? t('companionCenter.chat.proposalTitle'),
      summary: live.summary ?? live.action ?? '',
    })
  }
  return items
})

const WORK_SESSION_TOOL_NAMES = new Set([
  'companion_create_session',
  'companion_dispatch',
  'companion_steer_run',
  'companion_cancel_run',
  'companion_read_run',
  'companion_session_detail',
  'companion_side_question',
  'companion_list_approvals',
])

const WORK_SESSION_ACTIVITY_KINDS = new Set([
  'work_session_backflow',
  'companion_dispatched',
  'companion_steered',
  'companion_session_created',
])

interface ParsedSessionHint {
  sessionId: string
  runId: string | null
  title: string | null
  status: string | null
}

function extractWorkSessionHints(text: string | null | undefined): ParsedSessionHint[] {
  if (!text) return []
  const hints: ParsedSessionHint[] = []
  const seen = new Set<string>()
  const idPatterns = [
    /["']?\bsession_id["']?\s*[=:]\s*["']?([0-9a-fA-F-]{8,64}|[A-Za-z0-9_-]{6,64})["']?/g,
    /["']?\bsessionId["']?\s*[=:]\s*["']?([0-9a-fA-F-]{8,64}|[A-Za-z0-9_-]{6,64})["']?/g,
  ]
  const runMatch = text.match(/["']?\brun_id["']?\s*[=:]\s*["']?([0-9a-fA-F-]{8,64}|[A-Za-z0-9_-]{6,64})["']?/)
    ?? text.match(/["']?\brunId["']?\s*[=:]\s*["']?([0-9a-fA-F-]{8,64}|[A-Za-z0-9_-]{6,64})["']?/)
  const titleMatch = text.match(/会话「([^」]+)」/)
    ?? text.match(/"(?:session_title|title)"\s*:\s*"([^"]+)"/)
  let inferredStatus: string | null = null
  if (text.includes('浏览器人工接管') || text.includes('browser_handoff')) inferredStatus = 'browser_handoff'
  else if (text.includes('待审批') || text.includes('awaiting_approval')) inferredStatus = 'awaiting_approval'
  else if (text.includes('执行失败') || text.includes('回流 · 失败')) inferredStatus = 'failed'
  else if (text.includes('已完成')) inferredStatus = 'completed'

  for (const pattern of idPatterns) {
    for (const match of text.matchAll(pattern)) {
      const sessionId = match[1]?.trim()
      if (!sessionId || seen.has(sessionId)) continue
      seen.add(sessionId)
      hints.push({
        sessionId,
        runId: runMatch?.[1]?.trim() ?? null,
        title: titleMatch?.[1]?.trim() ?? null,
        status: inferredStatus,
      })
    }
  }
  return hints
}

function resolveWorkSessionCard(
  sessionId: string,
  fallback?: { runId?: string | null; title?: string | null; status?: string | null; projectName?: string | null; modeName?: string | null; summary?: string | null },
): CompanionConnectedWorkSession {
  const cached = liveWorkSessions.value[sessionId] ?? getWorkSessionById(sessionId)
  if (cached) return cached
  return {
    session_id: sessionId,
    title: fallback?.title || t('companionCenter.chat.dispatchCardTitle'),
    project_id: null,
    project_name: fallback?.projectName ?? null,
    mode_name: fallback?.modeName ?? null,
    latest_run_id: fallback?.runId ?? null,
    latest_run_status: fallback?.status ?? 'running',
    latest_run_summary: fallback?.summary ?? null,
    plan_summary: null,
    plan_progress: null,
    pending_approvals: [],
  }
}

function toolCallWorkSessions(toolCalls: CompanionLiveToolCall[]): CompanionConnectedWorkSession[] {
  const seen = new Set<string>()
  const cards: CompanionConnectedWorkSession[] = []
  for (const call of toolCalls) {
    if (!WORK_SESSION_TOOL_NAMES.has(call.toolName)) continue
    const hints = [
      ...extractWorkSessionHints(call.resultPreview),
      ...extractWorkSessionHints(call.argumentsPreview),
    ]
    for (const hint of hints) {
      if (seen.has(hint.sessionId)) continue
      seen.add(hint.sessionId)
      cards.push(resolveWorkSessionCard(hint.sessionId, {
        runId: hint.runId,
        title: hint.title,
        status: hint.status ?? (call.status === 'failed' ? 'failed' : 'running'),
      }))
    }
  }
  return cards
}

/** Map each message id -> inline work-session cards (keeping each session_id on the newest message that mentions it). */
const messageWorkSessionMap = computed<Record<string, CompanionConnectedWorkSession[]>>(() => {
  // Touch reactive maps so cards refresh immediately when SSE or listWorkSessions updates them.
  void liveWorkSessions.value
  void workSessionsByCompanion.value
  const latestMsgBySession = new Map<string, { msgId: string; hint: ParsedSessionHint }>()
  for (const msg of orderedMessages.value) {
    if (msg.author_kind !== 'system' && msg.author_kind !== 'companion') continue
    for (const hint of extractWorkSessionHints(msg.body)) {
      latestMsgBySession.set(hint.sessionId, { msgId: msg.id, hint })
    }
  }
  const byMsg: Record<string, CompanionConnectedWorkSession[]> = {}
  for (const { msgId, hint } of latestMsgBySession.values()) {
    const card = resolveWorkSessionCard(hint.sessionId, {
      runId: hint.runId,
      title: hint.title,
      status: hint.status,
    })
    ;(byMsg[msgId] ??= []).push(card)
  }
  return byMsg
})

const inlineRenderedSessionIds = computed(() => {
  const ids = new Set<string>()
  for (const cards of Object.values(messageWorkSessionMap.value)) {
    for (const card of cards) ids.add(card.session_id)
  }
  for (const turn of activeLiveTurns.value) {
    for (const card of toolCallWorkSessions(turn.toolCalls)) ids.add(card.session_id)
  }
  return ids
})

const topWorkSessionCards = computed<CompanionConnectedWorkSession[]>(() => {
  void liveWorkSessions.value
  void workSessionsByCompanion.value
  const convId = activeConversation.value?.id ?? null
  const botId = props.companion?.id ?? null
  const cards: CompanionConnectedWorkSession[] = []
  const seen = new Set<string>(inlineRenderedSessionIds.value)

  const pushUnique = (
    sessionId: string,
    fallback?: { runId?: string | null; title?: string | null; status?: string | null; summary?: string | null },
  ) => {
    if (!sessionId || seen.has(sessionId)) return
    seen.add(sessionId)
    cards.push(resolveWorkSessionCard(sessionId, fallback))
  }

  // 1. Live SSE backflow events for this conversation (or urgent awaiting_approval / browser_handoff for this companion).
  for (const backflow of workSessionBackflows.value) {
    const matchesConv = Boolean(convId && backflow.conversationId === convId)
    const matchesBot = Boolean(botId && backflow.companionId === botId)
    const isUrgent = backflow.status === 'awaiting_approval' || backflow.status === 'browser_handoff'
    if (matchesConv || (matchesBot && (isUrgent || !backflow.conversationId))) {
      pushUnique(backflow.sessionId, { runId: backflow.runId, status: backflow.status })
    }
  }

  // 2. Sessions dispatched directly from runs triggered in this thread.
  for (const run of Object.values(trackedRuns.value)) {
    if (!run.dispatched_session_id) continue
    pushUnique(run.dispatched_session_id, { runId: run.id, status: run.status })
  }

  // 3. Durable work-session activities for this thread/companion with active or urgent state.
  for (const row of activity.value) {
    if (!WORK_SESSION_ACTIVITY_KINDS.has(row.kind)) continue
    const matchesConv = Boolean(convId && row.conversation_id === convId)
    const matchesBot = Boolean(botId && row.companion_id === botId)
    if (!matchesConv && !matchesBot) continue
    const payload = row.payload && typeof row.payload === 'object' ? row.payload as Record<string, unknown> : null
    const sessionId = typeof payload?.session_id === 'string' ? payload.session_id : null
    if (!sessionId) continue
    const status = typeof payload?.status === 'string' ? payload.status : null
    if (!matchesConv && status !== 'awaiting_approval' && status !== 'browser_handoff') continue
    pushUnique(sessionId, {
      runId: typeof payload?.run_id === 'string' ? payload.run_id : null,
      title: typeof payload?.session_title === 'string' ? payload.session_title : typeof payload?.title === 'string' ? payload.title : null,
      status,
      summary: row.summary ?? null,
    })
  }

  return cards.slice(0, 6)
})

function onWorkSessionMutated() {
  if (props.companion?.id) void refreshWorkSessions(props.companion.id)
}

const inviteCandidates = computed(() => {
  const memberIds = new Set(
    (activeConversation.value?.members ?? [])
      .filter((m) => m.member_kind === 'companion' && m.companion_id && m.status === 'active')
      .map((m) => m.companion_id!),
  )
  return autonomousCompanions.value.filter((bot) => !memberIds.has(bot.id))
})

const threadTitle = computed(() => {
  const conv = activeConversation.value
  if (conv?.kind === 'group') return conv.title || t('companion.chat.groupLabel')
  return props.companion?.name ?? t('companion.title')
})

const threadPaused = computed(() => activeConversation.value?.autonomy_paused === true)
const activeSpeaking = computed(() => Boolean(props.companion && stageCues.value[props.companion.id]?.speaking))

function authorName(msg: CompanionMessage): string {
  if (msg.author_kind === 'user') return t('companion.chat.you')
  if (msg.author_kind === 'system') return t('companion.chat.system')
  return msg.author_name ?? activeCompanions.value.find((c) => c.id === msg.author_companion_id)?.name ?? props.companion?.name ?? t('companion.title')
}

async function send() {
  const body = draft.value.trim()
  const conv = activeConversation.value
  if (!body || !conv || sending.value) return
  sending.value = true
  hopBlockedNotice.value = null
  try {
    const idempotencyKey = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID()
      : undefined
    const res = await companionApi.sendMessage(conv.id, {
      body,
      channel_kind: 'desktop_chat',
    }, idempotencyKey)
    draft.value = ''
    if (res.message && !messages.value.some((m) => m.id === res.message.id)) {
      messages.value.push(res.message)
      lastSeq = Math.max(lastSeq, res.message.seq)
    }
    for (const run of res.runs ?? []) {
      trackedRuns.value = { ...trackedRuns.value, [run.id]: run }
    }
    const capped = (res.skipped_runs ?? []).find((item) => item.reason === 'hop_depth_limit')
    if (capped) {
      hopBlockedNotice.value = { depth: maxBotHopDepth.value, limit: maxBotHopDepth.value }
    }
    await scrollToBottom()
  } catch (error) {
    notify.error(error)
  } finally {
    sending.value = false
  }
}

function onComposerKeydown(event: KeyboardEvent) {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  void send()
}

async function decideProposal(item: (typeof pendingProposals.value)[number], decision: 'approved' | 'denied') {
  try {
    if (item.kind === 'mandate') {
      await companionApi.decideMandate(item.companionId, item.id, item.revision, decision)
      mandates.value = mandates.value.filter((m) => m.id !== item.id)
    } else {
      await companionApi.decideRecruitment(item.id, item.revision, decision)
      recruitments.value = recruitments.value.filter((r) => r.id !== item.id)
    }
    dismissProposalNotice(item.id)
    void refreshCompanionCenter()
  } catch (error) {
    notify.error(error)
  }
}

async function togglePause() {
  const conv = activeConversation.value
  if (!conv) return
  try {
    activeConversation.value = await companionApi.pauseConversation(conv.id, !conv.autonomy_paused)
  } catch (error) {
    notify.error(error)
  }
}

async function sendEmote(emote: 'wave' | 'pat') {
  if (!props.companion) return
  try {
    await companionApi.recordEmote(props.companion.id, emote)
  } catch (error) {
    notify.error(error)
  }
}

async function confirmInvite() {
  const conv = activeConversation.value
  const target = inviteTargetId.value
  if (!conv || !target) return
  try {
    const updated = await companionApi.inviteMember(conv.id, {
      companion_id: target,
      share_history: inviteShareHistory.value,
    })
    inviteOpen.value = false
    selectConversation(updated.id)
    activeConversation.value = updated
  } catch (error) {
    notify.error(error)
  }
}

onBeforeUnmount(() => {
  if (speechAvailable.value) {
    try { window.speechSynthesis.cancel() } catch { /* ignore */ }
  }
})
</script>

<template>
  <main class="companion-thread" data-testid="companion-thread-workspace">
    <div v-if="!companion && !conversationId" class="thread-empty-state">
      <Sparkles :size="28" aria-hidden="true" />
      <p>{{ t('companionCenter.noSelection') }}</p>
    </div>

    <template v-else>
      <header class="thread-header">
        <div class="thread-header-left">
          <AgentPetAvatar
            v-if="companion"
            :pet-slug="companion.avatar ?? ''"
            :avatar-kind="kindOf(companion.avatar)"
            :size="32"
            :alt="threadTitle"
          />
          <div class="thread-title-group">
            <div class="thread-title-row">
              <h2>{{ threadTitle }}</h2>
              <span v-if="activeSpeaking" class="thread-live-pill">{{ t('companionCenter.chat.streaming') }}</span>
              <span v-if="threadPaused" class="thread-paused-pill">{{ t('companionCenter.dutyPaused') }}</span>
            </div>
            <p class="thread-subtitle">
              {{ companion?.tagline || t('companionCenter.hopLimitNote', { limit: maxBotHopDepth }) }}
            </p>
          </div>
        </div>

        <div class="thread-header-actions">
          <button
            type="button"
            class="thread-icon-btn"
            :title="speechAvailable ? t(voiceEnabled ? 'companionCenter.chat.voiceOn' : 'companionCenter.chat.voiceOff') : t('companionCenter.chat.voiceUnsupported')"
            :disabled="!speechAvailable"
            data-testid="companion-voice-toggle"
            @click="toggleVoice"
          >
            <Volume2 v-if="voiceEnabled" :size="15" aria-hidden="true" />
            <VolumeX v-else :size="15" aria-hidden="true" />
          </button>
          <button
            v-if="companion"
            type="button"
            class="thread-icon-btn"
            :title="t('companion.pat')"
            data-testid="companion-pat-btn"
            @click="sendEmote('pat')"
          >
            <Hand :size="15" aria-hidden="true" />
          </button>
          <button
            type="button"
            class="thread-icon-btn"
            :title="t(threadPaused ? 'companion.chat.resume' : 'companion.chat.pause')"
            @click="togglePause"
          >
            <Play v-if="threadPaused" :size="15" aria-hidden="true" />
            <Pause v-else :size="15" aria-hidden="true" />
          </button>
          <button
            type="button"
            class="thread-icon-btn"
            :title="t('companionCenter.chat.inviteBot')"
            :disabled="!inviteCandidates.length"
            data-testid="companion-invite-btn"
            @click="inviteOpen = !inviteOpen; inviteTargetId = inviteCandidates[0]?.id ?? ''"
          >
            <UserPlus :size="15" aria-hidden="true" />
          </button>
          <button
            v-if="companion"
            type="button"
            class="thread-icon-btn"
            :title="t('companionCenter.editBot')"
            data-testid="companion-thread-edit-btn"
            @click="emit('edit-companion', companion.id)"
          >
            <Settings :size="15" aria-hidden="true" />
          </button>
        </div>
      </header>

      <form v-if="inviteOpen && inviteCandidates.length" class="thread-invite-bar" @submit.prevent="confirmInvite">
        <select v-model="inviteTargetId" class="thread-select">
          <option v-for="bot in inviteCandidates" :key="bot.id" :value="bot.id">{{ bot.name }}</option>
        </select>
        <label class="thread-check">
          <input v-model="inviteShareHistory" type="checkbox" />
          <span>{{ t('companion.chat.shareHistory') }}</span>
        </label>
        <UiButton type="submit" size="sm">{{ t('companion.chat.inviteConfirm') }}</UiButton>
        <button type="button" class="thread-icon-btn" :aria-label="t('common.close')" @click="inviteOpen = false">
          <X :size="14" />
        </button>
      </form>

      <!-- Pending inline autonomy proposals & live engineering work-session cards -->
      <div v-if="pendingProposals.length || topWorkSessionCards.length" class="thread-inline-cards" data-testid="companion-thread-work-sessions">
        <div v-for="proposal in pendingProposals" :key="proposal.key" class="inline-proposal-card" data-testid="companion-proposal-card">
          <ShieldAlert :size="15" class="proposal-icon" aria-hidden="true" />
          <div class="inline-card-copy">
            <strong>{{ t('companionCenter.chat.proposalTitle') }} · {{ proposal.title }}</strong>
            <p>{{ proposal.summary }}</p>
          </div>
          <div class="inline-card-actions">
            <UiButton size="sm" variant="default" data-testid="proposal-approve-btn" @click="decideProposal(proposal, 'approved')">
              <Check :size="13" aria-hidden="true" />
              <span>{{ t('companionCenter.chat.approve') }}</span>
            </UiButton>
            <UiButton size="sm" variant="outline" data-testid="proposal-deny-btn" @click="decideProposal(proposal, 'denied')">
              <X :size="13" aria-hidden="true" />
              <span>{{ t('companionCenter.chat.deny') }}</span>
            </UiButton>
          </div>
        </div>

        <CompanionWorkSessionCard
          v-for="ws in topWorkSessionCards"
          :key="ws.session_id"
          :session="ws"
          compact
          @approval-decided="onWorkSessionMutated"
          @run-cancelled="onWorkSessionMutated"
          @browser-takeover="onWorkSessionMutated"
        />
      </div>

      <div ref="logRef" class="thread-messages" role="log" aria-live="polite" data-testid="companion-message-log">
        <p v-if="loading" class="thread-status">{{ t('companion.chat.loading') }}</p>
        <p v-else-if="!orderedMessages.length && !activeLiveTurns.length" class="thread-status">{{ t('companion.chat.noMessages') }}</p>

        <article
          v-for="msg in orderedMessages"
          :key="msg.id"
          class="thread-bubble"
          :class="`is-${msg.author_kind}`"
          data-testid="companion-message-row"
        >
          <header class="bubble-meta">
            <strong>{{ authorName(msg) }}</strong>
            <time v-if="msg.created_at">{{ formatClock(msg.created_at, locale) }}</time>
          </header>
          <p class="bubble-body">{{ msg.body }}</p>
          <div v-if="messageWorkSessionMap[msg.id]?.length" class="bubble-work-sessions">
            <CompanionWorkSessionCard
              v-for="ws in messageWorkSessionMap[msg.id]"
              :key="ws.session_id"
              :session="ws"
              compact
              @approval-decided="onWorkSessionMutated"
              @run-cancelled="onWorkSessionMutated"
              @browser-takeover="onWorkSessionMutated"
            />
          </div>
        </article>

        <!-- Real-time SSE streamed assistant replies + tool call badges -->
        <article
          v-for="turn in activeLiveTurns"
          :key="turn.runId"
          class="thread-bubble is-companion is-streaming"
          data-testid="companion-streaming-bubble"
        >
          <header class="bubble-meta">
            <strong>{{ companion?.name ?? t('companion.title') }}</strong>
            <span class="streaming-badge">{{ t('companionCenter.chat.streaming') }}</span>
          </header>
          <div v-if="turn.toolCalls.length" class="streaming-tools">
            <span v-for="call in turn.toolCalls" :key="call.callId" class="streaming-tool-pill" :class="`is-${call.status}`">
              <Wrench :size="11" aria-hidden="true" />
              <span>{{ t('companionCenter.chat.toolCalling', { tool: call.toolName }) }} ({{ call.status }})</span>
            </span>
          </div>
          <div v-if="toolCallWorkSessions(turn.toolCalls).length" class="bubble-work-sessions">
            <CompanionWorkSessionCard
              v-for="ws in toolCallWorkSessions(turn.toolCalls)"
              :key="ws.session_id"
              :session="ws"
              compact
              @approval-decided="onWorkSessionMutated"
              @run-cancelled="onWorkSessionMutated"
              @browser-takeover="onWorkSessionMutated"
            />
          </div>
          <p v-if="turn.body" class="bubble-body">{{ turn.body }}</p>
        </article>

        <p v-if="hopBlockedNotice" class="thread-hop-notice" role="status" data-testid="companion-hop-notice">
          {{ t('companionCenter.chat.hopBlocked', hopBlockedNotice) }}
        </p>
      </div>

      <form class="thread-composer" data-testid="companion-thread-form" @submit.prevent="send">
        <textarea
          v-model="draft"
          class="thread-input"
          rows="2"
          :placeholder="t('companionCenter.chat.placeholder', { name: threadTitle })"
          @keydown="onComposerKeydown"
        />
        <UiButton type="submit" size="sm" :disabled="!draft.trim() || sending || !activeConversation" data-testid="companion-send-btn">
          <Send :size="14" aria-hidden="true" />
          <span>{{ t('companion.chat.send') }}</span>
        </UiButton>
      </form>
    </template>
  </main>
</template>

<style scoped>
.companion-thread {
  display: flex; flex-direction: column; min-height: 0; min-width: 0;
  border-radius: var(--radius-island, 12px); border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-section); backdrop-filter: var(--material-filter-raised, none);
  box-shadow: var(--shadow-card-subtle, var(--shadow-panel)); overflow: hidden;
}
.thread-empty-state {
  flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px;
  padding: 24px; text-align: center; color: var(--text-muted); font-size: 13px;
}
.thread-header {
  display: flex; align-items: center; justify-content: space-between; gap: 12px;
  padding: 12px 16px; border-bottom: 1px solid var(--border-muted);
}
.thread-header-left { display: flex; align-items: center; gap: 10px; min-width: 0; flex: 1; }
.thread-title-group { min-width: 0; flex: 1; }
.thread-title-row { display: flex; align-items: center; gap: 8px; min-width: 0; }
.thread-title-row h2 {
  margin: 0; min-width: 0; font-size: 14px; font-weight: 600;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.thread-live-pill, .thread-paused-pill {
  display: inline-flex; align-items: center; flex-shrink: 0; white-space: nowrap;
  padding: 1px 7px; border-radius: 999px; font-size: 11px; font-weight: 600;
  border: 1px solid transparent;
}
.thread-live-pill {
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
  border-color: color-mix(in srgb, var(--accent-primary) 28%, transparent);
  color: var(--accent-primary);
}
.thread-paused-pill {
  background: color-mix(in srgb, var(--accent-warning, #eab308) 16%, transparent);
  border-color: color-mix(in srgb, var(--accent-warning, #eab308) 30%, transparent);
  color: var(--accent-warning, #eab308);
}
.thread-subtitle {
  margin: 2px 0 0; font-size: 11px; color: var(--text-muted);
  font-variant-numeric: tabular-nums;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.thread-header-actions { display: flex; align-items: center; gap: 4px; flex-shrink: 0; }
.thread-icon-btn {
  display: inline-flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; flex-shrink: 0; white-space: nowrap;
  border: 1px solid transparent; border-radius: var(--radius-control, 6px);
  background: transparent; color: var(--text-muted); cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease, border-color 120ms ease;
}
.thread-icon-btn:hover:not(:disabled) { background: var(--surface-raised); color: var(--text-primary); }
.thread-icon-btn:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  color: var(--text-primary);
}
.thread-icon-btn:disabled { opacity: 0.45; cursor: default; }
.thread-invite-bar {
  display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
  padding: 8px 16px; background: var(--surface-raised); border-bottom: 1px solid var(--border-muted); font-size: 12px;
}
.thread-select {
  height: 28px; padding: 0 8px; border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted); background: var(--surface-section);
  color: var(--text-primary); font-size: 12px;
}
.thread-select:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  border-color: var(--accent-primary);
}
.thread-check {
  display: inline-flex; align-items: center; gap: 5px;
  white-space: nowrap; flex-shrink: 0; font-size: 12px; cursor: pointer;
}
.thread-check input:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
}
.thread-invite-bar :deep(button[type="submit"]),
.inline-card-actions :deep(button),
.inline-dispatch-card :deep(button) {
  display: inline-flex; align-items: center; justify-content: center; gap: 6px;
  height: 28px; padding: 0 10px; border-radius: var(--radius-control, 6px);
  font-size: 12px; white-space: nowrap; flex-shrink: 0;
}
.thread-inline-cards { display: flex; flex-direction: column; gap: 6px; padding: 10px 16px 0; }
.inline-proposal-card, .inline-dispatch-card {
  display: flex; align-items: center; gap: 10px; padding: 8px 12px;
  border-radius: var(--radius-card, 10px); border: 1px solid var(--border-card);
  background: var(--surface-raised); font-size: 12px; min-width: 0;
}
.proposal-icon { color: var(--accent-warning, #eab308); flex-shrink: 0; }
.inline-card-copy { flex: 1; min-width: 0; }
.inline-card-copy strong {
  display: block; min-width: 0;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.inline-card-copy p { margin: 2px 0 0; font-size: 11px; color: var(--text-muted); overflow-wrap: anywhere; }
.inline-card-actions { display: flex; align-items: center; gap: 6px; flex-shrink: 0; }
.thread-messages {
  flex: 1; min-height: 0; overflow-y: auto; padding: 14px 16px;
  display: flex; flex-direction: column; gap: 10px;
}
.thread-status, .thread-hop-notice {
  margin: 0; font-size: 12px; color: var(--text-muted); text-align: center; padding: 6px 10px;
  font-variant-numeric: tabular-nums;
}
.thread-hop-notice {
  border-radius: var(--radius-control, 8px);
  border: 1px solid color-mix(in srgb, var(--accent-warning, #eab308) 28%, transparent);
  background: color-mix(in srgb, var(--accent-warning, #eab308) 10%, transparent);
}
.thread-bubble {
  max-width: 82%; padding: 9px 12px; border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-muted); background: var(--surface-raised);
}
.thread-bubble.is-user {
  align-self: flex-end;
  background: color-mix(in srgb, var(--accent-primary) 13%, var(--surface-raised));
  border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-muted));
}
.thread-bubble.is-companion { align-self: flex-start; }
.thread-bubble.is-system { align-self: center; font-size: 12px; opacity: 0.85; }
.bubble-meta {
  display: flex; align-items: center; gap: 8px; min-width: 0;
  font-size: 11px; color: var(--text-muted); margin-bottom: 4px;
}
.bubble-meta strong { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bubble-meta time { flex-shrink: 0; white-space: nowrap; font-variant-numeric: tabular-nums; }
.streaming-badge { color: var(--accent-primary); font-weight: 600; white-space: nowrap; flex-shrink: 0; }
.streaming-tools { display: flex; flex-wrap: wrap; gap: 5px; margin-bottom: 6px; }
.streaming-tool-pill {
  display: inline-flex; align-items: center; gap: 4px; padding: 2px 7px; border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 24%, transparent);
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
  color: var(--accent-primary); font-size: 11px; white-space: nowrap; flex-shrink: 0;
}
.bubble-body { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 13px; line-height: 1.5; }
.bubble-work-sessions {
  display: flex; flex-direction: column; gap: 6px; margin-top: 8px; min-width: 0;
}
.thread-composer {
  display: flex; align-items: flex-end; gap: 8px; padding: 10px 14px;
  border-top: 1px solid var(--border-muted); background: color-mix(in srgb, var(--surface-raised) 50%, transparent);
}
.thread-composer :deep(button[type="submit"]) {
  display: inline-flex; align-items: center; justify-content: center; gap: 6px;
  height: 32px; padding: 0 12px; border-radius: var(--radius-control, 8px);
  font-size: 12px; font-weight: 600; white-space: nowrap; flex-shrink: 0;
}
.thread-input {
  flex: 1; min-width: 0; resize: none; padding: 8px 10px;
  border: 1px solid var(--border-card); border-radius: var(--radius-control, 8px);
  background: var(--surface-input, var(--surface-raised)); color: var(--text-primary); font: inherit; font-size: 13px;
}
.thread-input:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  border-color: var(--accent-primary);
}
</style>
