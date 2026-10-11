<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { MessageDto } from '../api'
import MessageItem from './MessageItem.vue'
import LiveTurnBlock from './chat/LiveTurnBlock.vue'
import MarkdownRender from './MarkdownRender.vue'
import {
  MessageScroller,
  MessageScrollerButton,
  MessageScrollerContent,
  MessageScrollerItem,
  MessageScrollerProvider,
  MessageScrollerViewport,
} from './ui/messageScroller'
import type { TurnActivity } from '@/composables/useAgentActivity'

const { t } = useI18n()

const props = defineProps<{
  messages: MessageDto[]
  /** Completed turns, keyed by the message that ended them. */
  activityByMessage?: Record<string, TurnActivity>
  /** Runs still working, keyed by run id. A turn is "running" until its run ends. */
  runningRunIds?: Record<string, true>
  /**
   * The turn still in flight. It has no message id yet — a run emits thinking
   * and tool calls long before its assistant message is persisted — so without
   * a dedicated anchor the whole turn, approvals included, renders nowhere.
   */
  liveTurn?: TurnActivity
  liveTurns?: TurnActivity[]
  streamingReply?: string
  /** False under the "concise" template; threaded down to the tool rows it changes. */
  richToolViews?: boolean
  /** True only under the "information" template; reaches the one component that mounts a frame. */
  allowToolWidget?: boolean
}>()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
  edit: [payload: { id: string; content: string }]
}>()
</script>

<template>
  <!--
    Scrolling is owned by `messageScroller`, not by an `onScroll` handler here. The old
    version decided "am I at the bottom?" from one distance check and then wrote `scrollTop`
    on every streaming delta — correct for the easy case, wrong for anchoring a new turn,
    holding position while a reply streams below it, restoring the reader's place when older
    history is prepended, and jumping to a message.

    `defaultScrollPosition="last-anchor"` opens a saved session on its last turn rather than
    on the absolute end, so reopening a thread does not drop the reader in without context.
  -->
  <MessageScrollerProvider auto-scroll default-scroll-position="last-anchor">
    <MessageScroller>
      <MessageScrollerViewport>
        <MessageScrollerContent>
          <MessageScrollerItem
            v-for="(message, index) in messages"
            :key="message.id"
            :message-id="message.id"
            :scroll-anchor="message.role === 'user'"
          >
            <MessageItem
              :message="message"
              :index="index"
              :thinking-steps="activityByMessage?.[message.id]?.thinkingSteps"
              :tool-calls="activityByMessage?.[message.id]?.toolCalls"
              :supervision-review="activityByMessage?.[message.id]?.supervisionReview"
              :settled="!message.run_id || !runningRunIds?.[message.run_id]"
              :rich-tool-views="richToolViews"
              :allow-tool-widget="allowToolWidget"
              @approve="emit('approve', $event)"
              @reject="emit('reject', $event)"
              @edit="emit('edit', $event)"
            />
          </MessageScrollerItem>
          <LiveTurnBlock
            v-if="liveTurn"
            :rich-tool-views="richToolViews"
            :allow-tool-widget="allowToolWidget"
            :thinking-steps="liveTurn?.thinkingSteps"
            :tool-calls="liveTurn?.toolCalls"
            :supervision-review="liveTurn?.supervisionReview"
            @approve="emit('approve', $event)"
            @reject="emit('reject', $event)"
          />
          <LiveTurnBlock v-for="turn in liveTurns" :key="turn.runId" v-bind="turn"
            :rich-tool-views="richToolViews"
            :allow-tool-widget="allowToolWidget"
            @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
          <div v-if="streamingReply" class="message-content assistant" data-testid="chat-streaming-reply">
            <MarkdownRender :content="streamingReply" />
          </div>
          <div v-if="messages.length === 0" class="empty-state">
            <Bot :size="20" />
            <span>{{ t('chat.ready') }}</span>
          </div>
        </MessageScrollerContent>
      </MessageScrollerViewport>
      <MessageScrollerButton />
    </MessageScroller>
  </MessageScrollerProvider>
</template>

<style scoped>
/* The conversation column is flex so the scroller can claim the remaining height; the
   scroller itself must stay height-bounded or the viewport never scrolls. */
:deep(.message-scroller) {
  flex: 1;
  min-height: 0;
}
</style>
