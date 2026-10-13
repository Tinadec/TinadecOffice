<script setup lang="ts">
import { computed, ref, nextTick, watch } from 'vue'
import ChatHeader from './ChatHeader.vue'
import ConversationView from './conversation/ConversationView.vue'
import AgentTodosStatus from './conversation/AgentTodosStatus.vue'
import ComposerBar from './ComposerBar.vue'
import WelcomeScreen from './WelcomeScreen.vue'
import { useChatResponsiveMode } from '@/composables/useElementSize'
import type { ComposerSubmitOptions, SessionSettingsUpdate, MessageDto, SessionDto, ProjectDto, OrchestrationSnapshotDto } from '../api'
import type { ComposerSettings } from '@/lib/composerSettings'
import type { PermissionLevel } from '@/types/mode'
import type { SupervisionReview } from '@/composables/useAgentActivity'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import type { ApprovalDto } from '../api'
import type { TurnRunInput } from '@/lib/conversation/turns'

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
  panelStyle?: Record<string, string>
  panelDataAttrs?: Record<string, string>
  // new: pass runs for insert picker
  runsForComposer?: Array<{ id: string; status: string }>
  /** True while a run can still be cancelled — drives the composer's stop button. */
  canStop?: boolean
  /**
   * The selected session's transcript is still loading. The panel stays docked (with a
   * skeleton) instead of flashing the welcome screen for a session that has history.
   */
  loading?: boolean
  /**
   * The flat feed's inputs (APP-HOME-108): session messages are paired with these runs and
   * the event-sourced feed into turns by ConversationView. Activity, approvals and streaming
   * answers all come from here; ChatPanel derives none of them itself.
   */
  conversation: {
    feed: ConversationFeed
    runs: readonly TurnRunInput[]
    streamingReplies?: Readonly<Record<string, string>>
    approvals?: readonly ApprovalDto[]
    supervisionReviews?: Readonly<Record<string, SupervisionReview | null | undefined>>
    workspaceRoot?: string | null
  }
}>()

const emit = defineEmits<{
  'update:draft': [value: string]
  'update:permission': [value: PermissionLevel]
  'update:settings': [value: SessionSettingsUpdate]
  'send': [payload?: ComposerSubmitOptions]
  'welcome-send': [payload: ComposerSubmitOptions & { content: string }]
  'create-project': []
  'select-project': [id: string | null]
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

const hero = computed(() => props.messages.length === 0 && !props.loading)

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
        <ConversationView
          :messages="messages"
          :runs="conversation.runs"
          :feed="conversation.feed"
          :streaming-replies="conversation.streamingReplies"
          :approvals="conversation.approvals"
          :supervision-reviews="conversation.supervisionReviews"
          :workspace-root="conversation.workspaceRoot"
          :loading="loading"
          @edit="emit('edit-message', $event)"
        />
        <AgentTodosStatus :feed="conversation.feed" :runs="conversation.runs" />
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
