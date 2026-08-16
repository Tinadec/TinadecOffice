<script setup lang="ts">
import { computed, ref, toRef } from 'vue'
import ChatHeader from './ChatHeader.vue'
import MessageList from './MessageList.vue'
import ComposerBar from './ComposerBar.vue'
import WelcomeScreen from './WelcomeScreen.vue'
import { useChatResponsiveMode } from '@/composables/useElementSize'
import type { MessageDto, SessionDto, ProjectDto, OrchestrationSnapshotDto } from '../api'
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
  agentLabel?: string | null
  panelStyle?: Record<string, string>
  panelDataAttrs?: Record<string, string>
}>()

const emit = defineEmits<{
  'update:draft': [value: string]
  'update:mode': [value: AgentMode]
  'update:permission': [value: PermissionLevel]
  'send': []
  'welcome-send': [content: string]
  'create-project': []
  'select-project': [id: string]
  'approve': [approvalId: string]
  'reject': [approvalId: string]
}>()

// ---- Responsive mode detection for chat area ----
const conversationRef = ref<HTMLElement | null>(null)
const { mode: chatMode } = useChatResponsiveMode(conversationRef)

const conversationClass = computed(() => ({
  'chat-narrow': chatMode.value === 'narrow' || chatMode.value === 'ultra',
  'chat-ultra': chatMode.value === 'ultra',
}))

function handleApprove(approvalId: string) {
  emit('approve', approvalId)
}

function handleReject(approvalId: string) {
  emit('reject', approvalId)
}
</script>

<template>
  <!-- Immersive chat zone: transparent background so page background shows through.
       User's global material setting controls backdrop-filter on inner objects (composer, welcome dialog, bubbles).
       NO border or shadow here — the card frame and parent stack own the visual boundary. -->
  <section ref="conversationRef" class="conversation" :class="conversationClass">
    <Transition name="chat-panel" mode="out-in">
      <template v-if="messages.length === 0">
        <WelcomeScreen
          :projects="props.projects"
          :selected-project-id="selectedProjectId"
          :model-name="modelName"
          :busy="busy"
          :panel-style="panelStyle"
          :panel-data-attrs="panelDataAttrs"
          @send="emit('welcome-send', $event)"
          @create-project="emit('create-project')"
          @select-project="emit('select-project', $event)"
          @update:mode="emit('update:mode', $event)"
          @update:permission="emit('update:permission', $event)"
        />
      </template>
      <template v-else>
        <!-- Active chat panel: transparent so background layer shows through -->
        <div class="chat-active-panel" key="chat-active" style="background: transparent !important; border: none !important; box-shadow: none !important;">
          <ChatHeader :current-session="currentSession" />
          <MessageList
            :messages="messages"
            :thinking-steps="thinkingSteps"
            :tool-calls="toolCalls"
            :agent-label="agentLabel"
            @approve="handleApprove"
            @reject="handleReject"
          />
          <ComposerBar
            :busy="busy"
            :model-value="draft"
            :mode="mode"
            :permission="permission"
            @update:model-value="emit('update:draft', $event)"
            @update:mode="emit('update:mode', $event)"
            @update:permission="emit('update:permission', $event)"
            @submit="emit('send')"
          />
        </div>
      </template>
    </Transition>
  </section>
</template>
