<script setup lang="ts">
import { computed, ref, nextTick, watch } from 'vue'
import ChatHeader from './ChatHeader.vue'
import MessageList from './MessageList.vue'
import ComposerBar from './ComposerBar.vue'
import WelcomeScreen from './WelcomeScreen.vue'
import { useChatResponsiveMode } from '@/composables/useElementSize'
import type { MessageDto, SessionDto, ProjectDto, OrchestrationSnapshotDto, MeetingModelOverrideDto } from '../api'
import type { AgentMode, PermissionLevel } from '@/types/mode'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

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
  mode: AgentMode
  permission: PermissionLevel
  /** Agent activity data — now owned by HomePage, passed down for per-message rendering */
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
  panelStyle?: Record<string, string>
  panelDataAttrs?: Record<string, string>
  // new: pass runs for insert picker
  runsForComposer?: Array<{ id: string; status: string }>
}>()

const emit = defineEmits<{
  'update:draft': [value: string]
  'update:mode': [value: AgentMode]
  'update:permission': [value: PermissionLevel]
  'send': [payload?: { dispatch_mode: 'parallel'|'queued'|'insert'; target_run_id?: string | null; mode_version_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null }]
  'welcome-send': [payload: { content: string; agent_mode: AgentMode; permission_mode: PermissionLevel; mode_version_id: string | null }]
  'create-project': []
  'select-project': [id: string]
  'approve': [approvalId: string]
  'reject': [approvalId: string]
}>()

// meeting_model_override 原样透传。此前这里把它读成 `payload.meeting_model`（字符串）
// 再以 `meeting_model` 键 emit，`as never` 压掉了类型错误，HomeController 读
// `meeting_model_override` 于是永远拿到 undefined —— 会话级会议模型覆写一路被丢。
function onComposerSubmit(payload: { dispatch_mode: 'parallel'|'queued'|'insert'; target_run_id?: string | null; mode_version_id?: string | null; meeting_model_override?: MeetingModelOverrideDto | null }) {
  emit('send', {
    dispatch_mode: payload.dispatch_mode,
    target_run_id: payload.target_run_id ?? null,
    mode_version_id: payload.mode_version_id ?? null,
    meeting_model_override: payload.meeting_model_override ?? null,
  })
}

function onWelcomeSubmit(payload: { content: string; agent_mode: AgentMode; permission_mode: PermissionLevel; mode_version_id: string | null }) {
  emit('welcome-send', payload)
}

// ---- Responsive mode detection for chat area ----
const conversationRef = ref<HTMLElement | null>(null)
const { mode: chatMode } = useChatResponsiveMode(conversationRef)

const hero = computed(() => props.messages.length === 0)

const modeVersionId = ref<string | null>(null)
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
          :thinking-steps="thinkingSteps"
          :tool-calls="toolCalls"
          @approve="handleApprove"
          @reject="handleReject"
        />
      </div>
    </Transition>

    <!-- Persistent composer: mounted once for both hero and docked states so
         sending the first message sinks the SAME box instead of swapping it. -->
    <ComposerBar
      :hero="hero"
      :busy="busy"
      :model-value="draft"
      :mode="mode"
      :permission="permission"
      :projects="projects"
      :selected-project-id="selectedProjectId"
      :session-id="currentSession?.id ?? null"
      :mode-version-id="modeVersionId"
      :meeting-model-override="currentSession?.meeting_model_override ?? null"
      :runs="runsForComposer"
      :panel-style="panelStyle"
      :panel-data-attrs="panelDataAttrs"
      @update:model-value="emit('update:draft', $event)"
      @update:mode="emit('update:mode', $event)"
      @update:permission="emit('update:permission', $event)"
      @update:mode-version-id="modeVersionId = $event"
      @welcome-submit="onWelcomeSubmit"
      @submit="onComposerSubmit"
      @create-project="emit('create-project')"
      @select-project="emit('select-project', $event)"
    />
  </section>
</template>
