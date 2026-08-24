<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiScrollArea } from '@/components/ui'
import type { MessageDto } from '../api'
import MessageItem from './MessageItem.vue'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

const { t } = useI18n()

defineProps<{
  messages: MessageDto[]
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
  /** Per-message activity override; falls back to the shared arrays. */
  activityByMessage?: Record<string, { thinkingSteps?: ThinkingStep[]; toolCalls?: ToolCall[] }>
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
          :thinking-steps="activityByMessage?.[message.id]?.thinkingSteps ?? (message.role === 'assistant' ? thinkingSteps : undefined)"
          :tool-calls="activityByMessage?.[message.id]?.toolCalls ?? (message.role === 'assistant' ? toolCalls : undefined)"
          @approve="emit('approve', $event)"
          @reject="emit('reject', $event)"
        />
        <div v-if="messages.length === 0" class="empty-state">
          <Bot :size="20" />
          <span>{{ t('chat.ready') }}</span>
        </div>
      </div>
    </UiScrollArea>
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
