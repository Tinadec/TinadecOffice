<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiScrollArea } from '@/components/ui'
import type { MessageDto } from '../api'
import MessageItem from './MessageItem.vue'
import LiveTurnBlock from './chat/LiveTurnBlock.vue'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

const { t } = useI18n()

interface TurnActivity {
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
}

defineProps<{
  messages: MessageDto[]
  /** Completed turns, keyed by the message that ended them. */
  activityByMessage?: Record<string, TurnActivity>
  /**
   * The turn still in flight. It has no message id yet — a run emits thinking
   * and tool calls long before its assistant message is persisted — so without
   * a dedicated anchor the whole turn, approvals included, renders nowhere.
   */
  liveTurn?: TurnActivity
}>()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
  edit: [payload: { id: string; content: string }]
}>()
</script>

<template>
  <div class="message-stream-container">
    <UiScrollArea class="message-stream">
      <div
        class="message-stream-inner"
        role="log"
        aria-live="polite"
        aria-relevant="additions"
        :aria-label="t('chat.ready')"
      >
        <MessageItem
          v-for="(message, index) in messages"
          :key="message.id"
          :message="message"
          :index="index"
          :thinking-steps="activityByMessage?.[message.id]?.thinkingSteps"
          :tool-calls="activityByMessage?.[message.id]?.toolCalls"
          @approve="emit('approve', $event)"
          @reject="emit('reject', $event)"
          @edit="emit('edit', $event)"
        />
        <LiveTurnBlock
          :thinking-steps="liveTurn?.thinkingSteps"
          :tool-calls="liveTurn?.toolCalls"
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
