<script setup lang="ts">
import { computed, ref, nextTick, watch } from 'vue'
import ChatHeader from './ChatHeader.vue'
import MessageList from './MessageList.vue'
import ComposerBar from './ComposerBar.vue'
import WelcomeScreen from './WelcomeScreen.vue'
import { useChatResponsiveMode } from '@/composables/useElementSize'
import type { ComposerSubmitOptions, SessionSettingsUpdate, MessageDto, SessionDto, ProjectDto, OrchestrationSnapshotDto } from '../api'
import type { ComposerSettings } from '@/lib/composerSettings'
import type { PermissionLevel } from '@/types/mode'
import type { ThinkingStep, ToolCall, TurnActivity } from '@/composables/useAgentActivity'

const props = defineProps<{
  messages: MessageDto[]
  sessions: SessionDto[]
  projects: ProjectDto[]
  currentSession: SessionDto | null
  currentProject: ProjectDto | null
  selectedProjectId: string | null
  modelName: string
  orchestration: OrchestrationSnapshotDto | null
  busy: boolean
  draft: string
  permission: PermissionLevel
  composerSettings?: ComposerSettings
  settingsSaving?: boolean
  settingsError?: string | null
  /** Activity of the most recent run, owned by HomePage. ChatPanel decides
      whether it belongs to the live turn or to the message that answered. */
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
  turnActivities?: Record<string, TurnActivity>
  panelStyle?: Record<string, string>
  panelDataAttrs?: Record<string, string>
  // new: pass runs for insert picker
  runsForComposer?: Array<{ id: string; status: string }>
  /**
   * Live text of the run currently in flight. The controller accumulated it per
   * delta but nothing rendered it, so the reply appeared only once it was persisted.
   */
  streamingReply?: string
  /** True while a run can still be cancelled — drives the composer's stop button. */
  canStop?: boolean
  /** False under the "concise" template: tool rows keep plain text instead of rich views. */
  richToolViews?: boolean
  /** True only under the "information" template: tool-authored HTML may be rendered. */
  allowToolWidget?: boolean
}>()

const emit = defineEmits<{
  'update:draft': [value: string]
  'update:permission': [value: PermissionLevel]
  'update:settings': [value: SessionSettingsUpdate]
  'send': [payload?: ComposerSubmitOptions]
  'welcome-send': [payload: ComposerSubmitOptions & { content: string }]
  'create-project': []
  'select-project': [id: string | null]
  'approve': [approvalId: string]
  'reject': [approvalId: string]
  'edit-message': [payload: { id: string; content: string }]
  'stop': []
}>()

// meeting_model_override 原样透传。此前这里把它读成 `payload.meeting_model`（字符串）
// 再以 `meeting_model` 键 emit，`as never` 压掉了类型错误，HomeController 读
// `meeting_model_override` 于是永远拿到 undefined —— 会话级会议模型覆写一路被丢。
function onComposerSubmit(payload: ComposerSubmitOptions) {
  emit('send', payload)
}

function onWelcomeSubmit(payload: ComposerSubmitOptions & { content: string }) {
  emit('welcome-send', payload)
}

// ---- Responsive mode detection for chat area ----
const conversationRef = ref<HTMLElement | null>(null)
const { mode: chatMode } = useChatResponsiveMode(conversationRef)

const hero = computed(() => props.messages.length === 0)

const lastAssistantId = computed(() => {
  for (let i = props.messages.length - 1; i >= 0; i--) {
    if (props.messages[i].role === 'assistant') return props.messages[i].id
  }
  return null
})

const hasActivity = computed(
  () => (props.thinkingSteps?.length ?? 0) > 0 || (props.toolCalls?.length ?? 0) > 0,
)

/**
 * Production supplies run-keyed activity; only standalone gallery callers use the
 * single-turn props. Busy is a transport state, never an ownership key: sending a
 * second message must not move the previous run's activity to the new live turn.
 */
const liveTurn = computed(() =>
  !props.turnActivities && hasActivity.value && (props.busy || !lastAssistantId.value)
    ? { thinkingSteps: props.thinkingSteps, toolCalls: props.toolCalls }
    : undefined,
)

const activityByMessage = computed(() => props.turnActivities
  ? Object.fromEntries(props.messages.filter((m) => m.role === 'assistant' && m.run_id && props.turnActivities?.[m.run_id])
    .map((m) => [m.id, props.turnActivities![m.run_id!]]))
  : hasActivity.value && !props.busy && lastAssistantId.value
    ? { [lastAssistantId.value]: { thinkingSteps: props.thinkingSteps, toolCalls: props.toolCalls } }
    : undefined,
)

const liveTurns = computed(() => props.turnActivities
  ? Object.values(props.turnActivities).filter((turn) => !props.messages.some((m) => m.role === 'assistant' && m.run_id === turn.runId))
  : undefined)

/**
 * Runs that have not reached a terminal state. The activity trail opens while a turn is
 * working and folds to a summary once it answers, so the transcript has to know which of its
 * turns are still in flight — a persisted assistant message alone does not say that, because
 * a run can persist an answer and keep working.
 */
const TERMINAL_RUN_STATUS = ['completed', 'failed', 'cancelled']
const runningRunIds = computed<Record<string, true>>(() => Object.fromEntries(
  (props.runsForComposer ?? [])
    .filter((run) => !TERMINAL_RUN_STATUS.includes(run.status))
    .map((run) => [run.id, true as const]),
))

const modeVersionId = ref<string | null>(null)
function selectMode(value: string | null) {
  modeVersionId.value = value
  emit('update:settings', value ? { mode_version_id: value } : { clear_mode_version: true })
}
watch(
  () => props.currentSession?.id,
  () => {
    modeVersionId.value = props.currentSession?.mode_version_id ?? null
  },
  { immediate: true },
)

const conversationClass = computed(() => ({
  'chat-narrow': chatMode.value === 'narrow' || chatMode.value === 'ultra',
  'chat-ultra': chatMode.value === 'ultra',
  'composer-hero': hero.value,
}))

// Immersive dock: the composer box is one persistent element. When the first
// message flips the panel between hero (centered) and docked (bottom), FLIP it
// from its previous position so it visibly sinks/rises instead of being
// swapped for a differently-styled box. Transform goes on the box itself —
// never on an ancestor of the backdrop-filtered surface.
watch(hero, async () => {
  const container = conversationRef.value
  const before = container?.querySelector<HTMLElement>('.composer-box')
  const from = before?.getBoundingClientRect()
  await nextTick()
  if (!from || !from.width || !from.height) return
  if (typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return
  const after = container?.querySelector<HTMLElement>('.composer-box')
  if (!after || typeof after.animate !== 'function') return
  const to = after.getBoundingClientRect()
  if (!to.width || !to.height) return
  const dy = from.top - to.top
  if (Math.abs(dy) < 1) return
  after.animate(
    [{ transform: `translateY(${dy}px)` }, { transform: 'translateY(0px)' }],
    { duration: 350, easing: 'cubic-bezier(0.4, 0, 0.2, 1)' },
  )
})

function handleApprove(approvalId: string) {
  emit('approve', approvalId)
}

function handleReject(approvalId: string) {
  emit('reject', approvalId)
}
</script>

<template>
  <section ref="conversationRef" class="conversation" :class="conversationClass">
    <!-- Immersive chat zone: transparent background so page background shows through.
         User's global material setting controls backdrop-filter on inner objects (composer, welcome dialog, bubbles).
         NO border or shadow here — the card frame and parent stack own the visual boundary. -->
    <Transition name="chat-panel">
      <WelcomeScreen
        v-if="hero"
        key="welcome"
      />
      <!-- Active chat panel: transparent so background layer shows through -->
      <div v-else key="chat-active" class="chat-active-panel" style="background: transparent !important; border: none !important; box-shadow: none !important;">
        <ChatHeader :current-session="currentSession" />
        <MessageList
          :messages="messages"
          :activity-by-message="activityByMessage"
          :live-turn="liveTurn"
          :live-turns="liveTurns"
          :running-run-ids="runningRunIds"
          :rich-tool-views="richToolViews"
          :allow-tool-widget="allowToolWidget"
          :streaming-reply="streamingReply"
          @approve="handleApprove"
          @reject="handleReject"
          @edit="emit('edit-message', $event)"
        />
      </div>
    </Transition>

    <!-- Persistent composer: mounted once for both hero and docked states so
         sending the first message sinks the SAME box instead of swapping it. -->
    <ComposerBar
      :hero="hero"
      :busy="busy"
      :can-stop="canStop"
      :model-value="draft"
      :permission="permission"
      :projects="projects"
      :selected-project-id="selectedProjectId"
      :session-id="currentSession?.id ?? null"
      :mode-version-id="composerSettings ? composerSettings.mode_version_id : modeVersionId"
      :meeting-model-override="composerSettings ? composerSettings.meeting_model_override : currentSession?.meeting_model_override ?? null"
      :settings-saving="settingsSaving"
      :settings-error="settingsError"
      :runs="runsForComposer"
      :panel-style="panelStyle"
      :panel-data-attrs="panelDataAttrs"
      @update:model-value="emit('update:draft', $event)"
      @update:permission="emit('update:permission', $event)"
      @update:mode-version-id="selectMode"
      @update:meeting-model-override="emit('update:settings', $event ? { meeting_model_override: $event } : { clear_meeting_model_override: true })"
      @welcome-submit="onWelcomeSubmit"
      @submit="onComposerSubmit"
      @stop="emit('stop')"
      @create-project="emit('create-project')"
      @select-project="emit('select-project', $event)"
    />
  </section>
</template>
