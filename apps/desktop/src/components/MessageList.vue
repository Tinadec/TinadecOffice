<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { ref, watch, nextTick } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiScrollArea } from '@/components/ui'
import type { MessageDto } from '../api'
import MessageItem from './MessageItem.vue'
import LiveTurnBlock from './chat/LiveTurnBlock.vue'
import MarkdownRender from './MarkdownRender.vue'
import type { TurnActivity } from '@/composables/useAgentActivity'

const { t } = useI18n()

const props = defineProps<{
  messages: MessageDto[]
  /** Completed turns, keyed by the message that ended them. */
  activityByMessage?: Record<string, TurnActivity>
  /**
   * The turn still in flight. It has no message id yet — a run emits thinking
   * and tool calls long before its assistant message is persisted — so without
   * a dedicated anchor the whole turn, approvals included, renders nowhere.
   */
  liveTurn?: TurnActivity
  liveTurns?: TurnActivity[]
  streamingReply?: string
}>()

const inner = ref<HTMLElement | null>(null)
let followOutput = true
function onScroll(event: Event) {
  const viewport = inner.value?.parentElement
  if (event.target !== viewport || !viewport) return
  followOutput = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight < 40
}
watch(() => [props.streamingReply, props.messages.length, props.liveTurn, props.liveTurns], async () => {
  if (!followOutput) return
  await nextTick()
  const viewport = inner.value?.parentElement
  if (viewport) viewport.scrollTop = viewport.scrollHeight
}, { flush: 'post' })

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
  edit: [payload: { id: string; content: string }]
}>()
</script>

<template>
  <div class="message-stream-container" @scroll.capture="onScroll">
    <UiScrollArea class="message-stream">
      <div
        ref="inner"
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
          :supervision-review="activityByMessage?.[message.id]?.supervisionReview"
          @approve="emit('approve', $event)"
          @reject="emit('reject', $event)"
          @edit="emit('edit', $event)"
        />
        <LiveTurnBlock
          v-if="liveTurn"
          :thinking-steps="liveTurn?.thinkingSteps"
          :tool-calls="liveTurn?.toolCalls"
          :supervision-review="liveTurn?.supervisionReview"
          @approve="emit('approve', $event)"
          @reject="emit('reject', $event)"
        />
        <LiveTurnBlock v-for="turn in liveTurns" :key="turn.runId" v-bind="turn"
          @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
        <div v-if="streamingReply" class="message-content assistant" data-testid="chat-streaming-reply">
          <MarkdownRender :content="streamingReply" />
        </div>
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
