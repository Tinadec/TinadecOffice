<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { computed, toRef } from 'vue'
import { usePreferredReducedMotion } from '@vueuse/core'
import { useDelayedLoading } from '@/composables/useDelayedLoading'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto, MessageDto } from '@/api'
import type { SupervisionReview } from '@/composables/useAgentActivity'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import { buildTurns, type TurnRunInput } from '@/lib/conversation/turns'
import { crossesDay, dayLabel } from '@/lib/conversation/format'
import { Marker, MarkerContent } from '@/components/ui/marker'
import { MessageScroller, MessageScrollerContent, MessageScrollerItem, MessageScrollerProvider, MessageScrollerViewport } from '@/components/ui/message-scroller'
import ConversationTurn from './ConversationTurn.vue'
import JumpToLatest from './JumpToLatest.vue'

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
  /** The transcript is still loading: after 200ms a skeleton stands in, for at least 500ms. */
  loading?: boolean
}>()
const emit = defineEmits<{ edit: [payload: { id: string; content: string }] }>()
const { t, locale } = useI18n()
// Reduced motion is a second design, not a slower one: jumps are instant.
const motion = usePreferredReducedMotion()
const showSkeleton = useDelayedLoading(toRef(() => Boolean(props.loading)), { delayMs: 200, minimumMs: 500 })
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

/** A date line above each turn that starts on a later day than the one before it. */
const dayBreaks = computed(() => {
  const now = new Date()
  const breaks = new Map<string, string>()
  turns.value.forEach((turn, index) => {
    const previous = turns.value[index - 1]?.startedAt
    if (turn.startedAt && crossesDay(previous, turn.startedAt)) breaks.set(turn.id, dayLabel(turn.startedAt, now, locale.value, t))
  })
  return breaks
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
            <Marker v-if="dayBreaks.has(turn.id)" variant="separator" class="conversation-day" role="separator" data-testid="day-separator">
              <MarkerContent><time :datetime="turn.startedAt ?? undefined">{{ dayBreaks.get(turn.id) }}</time></MarkerContent>
            </Marker>
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
      </MessageScrollerViewport>
      <!-- Over the viewport, not inside it: the scroller content fills the viewport's height.
           The skeleton has a turn's shape (a right-aligned bubble, then answer lines) so
           nothing jumps when the real transcript replaces it. -->
      <div v-if="showSkeleton && turns.length === 0" class="conversation-skeleton" role="status" :aria-label="t('conversation.turn.loading')" data-testid="conversation-skeleton">
        <div v-for="row in 2" :key="row" class="conversation-skeleton-turn">
          <span class="conversation-skeleton-bar is-bubble" />
          <span class="conversation-skeleton-bar is-line" />
          <span class="conversation-skeleton-bar is-line is-short" />
        </div>
      </div>
      <div v-else-if="turns.length === 0 && !loading" class="conversation-empty">
        <Bot :size="20" aria-hidden="true" />
        <span>{{ t('chat.ready') }}</span>
      </div>
      <JumpToLatest :turns="turns" :behavior="jumpBehavior" />
    </MessageScroller>
  </MessageScrollerProvider>
</template>

<style scoped>
.conversation-view { flex: 1; min-height: 0; }
.conversation-content { gap: 24px; width: 100%; max-width: 852px; margin: 0 auto; padding: 16px 16px 24px; }
:global(.chat-narrow) .conversation-content { gap: 18px; padding: 12px 10px 18px; }
:global(.chat-ultra) .conversation-content { gap: 14px; padding: 8px 6px 14px; }
.conversation-skeleton { position: absolute; inset: 0 0 auto; display: flex; flex-direction: column; gap: 24px; width: 100%; max-width: 852px; margin: 0 auto; padding: 16px; }
.conversation-skeleton-turn { display: flex; flex-direction: column; gap: 8px; }
.conversation-skeleton-bar { display: block; height: 12px; border-radius: 6px; background: color-mix(in srgb, var(--text-primary) 9%, transparent); animation: conversation-skeleton-pulse 1.4s ease-in-out infinite; }
.conversation-skeleton-bar.is-bubble { align-self: flex-end; width: 40%; height: 32px; border-radius: 10px; }
.conversation-skeleton-bar.is-line { width: 80%; }
.conversation-skeleton-bar.is-short { width: 55%; }
@keyframes conversation-skeleton-pulse { 50% { opacity: 0.55; } }
@media (prefers-reduced-motion: reduce) { .conversation-skeleton-bar { animation: none; } }
.conversation-day { margin-bottom: 16px; font-size: 11px; color: var(--text-muted); }
.conversation-empty { position: absolute; inset: 0; pointer-events: none; display: flex; align-items: center; justify-content: center; gap: 8px; height: 100%; color: var(--text-muted); font-size: 13px; }
</style>
