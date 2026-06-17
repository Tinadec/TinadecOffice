<script setup lang="ts">
import { computed, toRef } from 'vue'
import ChatHeader from './ChatHeader.vue'
import MessageList from './MessageList.vue'
import ComposerBar from './ComposerBar.vue'
import WelcomeScreen from './WelcomeScreen.vue'
import TaskGraphPanel from './TaskGraphPanel.vue'
import AgentActivityBanner from './chat/AgentActivityBanner.vue'
import { useAgentActivity } from '@/composables/useAgentActivity'
import type { MessageDto, SessionDto, ProjectDto, OrchestrationSnapshotDto } from '../api'
import type { AgentMode, PermissionLevel } from '@/types/mode'

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

const sessionId = computed(() => props.currentSession?.id ?? null)
const orchestrationRef = toRef(props, 'orchestration')

const {
  activity,
  toolCalls,
  thinkingSteps,
  agentStates,
  progressEvents,
} = useAgentActivity(sessionId, orchestrationRef)

const agentLabel = computed(() => activity.value.activeAgentName ?? null)

function handleApprove(approvalId: string) {
  emit('approve', approvalId)
}

function handleReject(approvalId: string) {
  emit('reject', approvalId)
}
</script>

<template>
  <section class="conversation">
    <Transition name="chat-panel" mode="out-in">
      <template v-if="messages.length === 0">
        <WelcomeScreen
          :projects="props.projects"
          :selected-project-id="selectedProjectId"
          :model-name="modelName"
          :busy="busy"
          @send="emit('welcome-send', $event)"
          @create-project="emit('create-project')"
          @select-project="emit('select-project', $event)"
          @update:mode="emit('update:mode', $event)"
          @update:permission="emit('update:permission', $event)"
        />
      </template>
      <template v-else>
        <div class="chat-active-panel" key="chat-active">
          <ChatHeader :current-session="currentSession" />
          <AgentActivityBanner :activity="activity" :agent-states="agentStates" />
          <TaskGraphPanel :snapshot="orchestration" />
          <MessageList
            :messages="messages"
            :thinking-steps="thinkingSteps"
            :tool-calls="toolCalls"
            :progress-events="progressEvents"
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
