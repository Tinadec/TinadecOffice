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
const showJumpToLatest = ref(false)
let followOutput = true
function onScroll(event: Event) {
  const viewport = inner.value?.parentElement
  if (event.target !== viewport || !viewport) return
  const distance = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight
  followOutput = distance < 40
  showJumpToLatest.value = !followOutput
}
async function jumpToLatest() {
  followOutput = true
  showJumpToLatest.value = false
  await nextTick()
  const viewport = inner.value?.parentElement
  viewport?.scrollTo({ top: viewport.scrollHeight, behavior: 'smooth' })
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
    <button v-if="showJumpToLatest" class="jump-to-latest" type="button" data-testid="jump-to-latest"
      :aria-label="t('chat.jumpToLatest')" @click="jumpToLatest">
      <span aria-hidden="true">↓</span>{{ t('chat.jumpToLatest') }}
    </button>
  </div>
</template>

<style scoped>
.message-stream-container {
  position: relative;
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
}

.jump-to-latest {
  position: absolute; right: 18px; bottom: 14px; z-index: 2;
  display: inline-flex; align-items: center; gap: 5px;
  border: 1px solid var(--border-muted); border-radius: 999px; padding: 5px 10px;
  color: var(--text-primary); background: var(--surface-section);
  box-shadow: var(--shadow-panel); font-size: 11px; cursor: pointer;
  animation: jump-to-latest-in 160ms ease-out;
}
.jump-to-latest:hover, .jump-to-latest:focus-visible { background: var(--surface-hover); }
@keyframes jump-to-latest-in { from { opacity: 0; transform: translateY(4px); } }
@media (prefers-reduced-motion: reduce) { .jump-to-latest { animation: none; } }

.message-stream-container :deep(.message-stream) {
  flex: 1;
  min-height: 0;
}
</style>
