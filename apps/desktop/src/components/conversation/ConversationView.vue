<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { computed } from 'vue'
import { usePreferredReducedMotion } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto, MessageDto } from '@/api'
import type { SupervisionReview } from '@/composables/useAgentActivity'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import { buildTurns, type TurnRunInput } from '@/lib/conversation/turns'
import { MessageScroller, MessageScrollerButton, MessageScrollerContent, MessageScrollerItem, MessageScrollerProvider, MessageScrollerViewport } from '@/components/ui/message-scroller'
import ConversationTurn from './ConversationTurn.vue'

/**
 * The flat conversation feed: one scroller row per turn, the user's message anchoring
 * each new turn near the top while the reader stays where they are when they scroll up.
 * Everything it shows comes from the session messages, the run list and the
 * event-sourced feed; it fetches nothing itself.
 */
const props = defineProps<{
  messages: readonly MessageDto[]
  runs: readonly TurnRunInput[]
  feed: ConversationFeed
  streamingReplies?: Readonly<Record<string, string>>
  approvals?: readonly ApprovalDto[]
  supervisionReviews?: Readonly<Record<string, SupervisionReview | null | undefined>>
  workspaceRoot?: string | null
}>()
const emit = defineEmits<{ edit: [payload: { id: string; content: string }] }>()
const { t } = useI18n()
// Reduced motion is a second design, not a slower one: jumps are instant.
const motion = usePreferredReducedMotion()
const jumpBehavior = computed<ScrollBehavior>(() => (motion.value === 'reduce' ? 'auto' : 'smooth'))

// tool_evidence is the model's own checkpoint of tool results; the activity area is the
// one user-facing view of tool work, so it never reaches the transcript.
const visibleMessages = computed(() => props.messages.filter((message) => message.role !== 'tool_evidence'))

const turns = computed(() => {
  void props.feed.revision.value
  return buildTurns({
    messages: visibleMessages.value,
    runs: props.runs,
    feed: props.feed.state.value,
    streaming: new Map(Object.entries(props.streamingReplies ?? {}).filter(([, text]) => text)),
  })
})
</script>

<template>
  <MessageScrollerProvider auto-scroll default-scroll-position="last-anchor">
    <MessageScroller class="conversation-view" data-testid="conversation-view">
      <MessageScrollerViewport :label="t('chat.ready')">
        <MessageScrollerContent class="conversation-content">
          <MessageScrollerItem
            v-for="(turn, index) in turns"
            :key="turn.id"
            :message-id="turn.id"
            :scroll-anchor="Boolean(turn.user)"
          >
            <ConversationTurn
              :turn="turn"
              :revision="feed.revision.value"
              :approvals="approvals ?? []"
              :supervision-reviews="supervisionReviews"
              :workspace-root="workspaceRoot"
              :last="index === turns.length - 1"
              @edit="emit('edit', $event)"
            />
          </MessageScrollerItem>
        </MessageScrollerContent>
        <div v-if="turns.length === 0" class="conversation-empty">
          <Bot :size="20" aria-hidden="true" />
          <span>{{ t('chat.ready') }}</span>
        </div>
      </MessageScrollerViewport>
      <MessageScrollerButton :label="t('chat.jumpToLatest')" :behavior="jumpBehavior" data-testid="jump-to-latest" />
    </MessageScroller>
  </MessageScrollerProvider>
</template>

<style scoped>
.conversation-view { flex: 1; min-height: 0; }
.conversation-content { gap: 24px; width: 100%; max-width: 852px; margin: 0 auto; padding: 16px 16px 24px; }
:global(.chat-narrow) .conversation-content { gap: 18px; padding: 12px 10px 18px; }
:global(.chat-ultra) .conversation-content { gap: 14px; padding: 8px 6px 14px; }
.conversation-empty { display: flex; align-items: center; justify-content: center; gap: 8px; height: 100%; color: var(--text-muted); font-size: 13px; }
</style>
