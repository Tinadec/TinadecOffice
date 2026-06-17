<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiScrollArea } from '@/components/ui'
import type { MessageDto } from '../api'
import MessageItem from './MessageItem.vue'
import ProgressStream from './chat/ProgressStream.vue'
import type { ThinkingStep, ToolCall, ProgressEvent } from '@/composables/useAgentActivity'

const { t } = useI18n()

defineProps<{
  messages: MessageDto[]
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
  progressEvents?: ProgressEvent[]
  agentLabel?: string | null
}>()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
}>()
</script>

<template>
  <div class="message-stream-container">
    <UiScrollArea class="message-stream">
      <div class="message-stream-inner">
        <MessageItem
          v-for="(message, index) in messages"
          :key="message.id"
          :message="message"
          :index="index"
          :thinking-steps="message.role === 'assistant' ? thinkingSteps : undefined"
          :tool-calls="message.role === 'assistant' ? toolCalls : undefined"
          :agent-label="message.role === 'assistant' ? agentLabel : null"
          @approve="emit('approve', $event)"
          @reject="emit('reject', $event)"
        />
        <div v-if="messages.length === 0" class="empty-state">
          <Bot :size="24" />
          <span>{{ t('chat.ready') }}</span>
        </div>
      </div>
    </UiScrollArea>
    <ProgressStream v-if="progressEvents && progressEvents.length > 0" :events="progressEvents" />
  </div>
</template>

<style scoped>
.message-stream-container {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
}

.message-stream-container :deep(.message-stream) {
  flex: 1;
  min-height: 0;
}
</style>
