<script setup lang="ts">
import { ArrowDown } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ConversationTurn } from '@/lib/conversation/turns'
import { MessageScrollerButton, useMessageScrollerScrollable } from '@/components/ui/message-scroller'

/**
 * The jump-to-latest button, with how many turns arrived while the reader was scrolled up.
 * Only turns that land below the reader count: not the history a session opens with, not a
 * different session's transcript, not the reader's own message (sending scrolls to it).
 * Back at the bottom the count clears.
 */
const props = defineProps<{ turns: readonly ConversationTurn[]; behavior: ScrollBehavior }>()
const { t } = useI18n()
const scrollable = useMessageScrollerScrollable()
const unread = ref(0)

watch(() => props.turns, (next, previous) => {
  // A first load or another session's history replaces the list: nothing in it is "new".
  if (!previous?.length || !next.length || next[0]!.id !== previous[0]!.id) {
    unread.value = 0
    return
  }
  if (!scrollable.value.end) return
  // Counted by length: an optimistic row that gets its Core id keeps the count.
  const added = next.slice(previous.length).filter((turn) => !turn.user?.id.startsWith('pending-'))
  unread.value += added.length
})
watch(() => scrollable.value.end, (below) => { if (!below) unread.value = 0 })

const label = computed(() => (unread.value
  ? `${t('chat.jumpToLatest')} · ${t('conversation.turn.newTurns', { count: unread.value })}`
  : t('chat.jumpToLatest')))
</script>

<template>
  <MessageScrollerButton
    :behavior="behavior"
    :class="unread ? 'conversation-jump has-unread' : 'conversation-jump'"
    :aria-label="label"
    :title="label"
    data-testid="jump-to-latest"
  >
    <ArrowDown aria-hidden="true" />
    <span v-if="unread" class="conversation-jump-count" data-testid="jump-unread">{{ t('conversation.turn.newTurns', { count: unread }) }}</span>
  </MessageScrollerButton>
</template>

<style scoped>
/* With a count the round button becomes a pill; the arrow keeps its place on the left. */
.conversation-jump.has-unread { width: auto; padding: 0 10px; }
.conversation-jump-count { font-size: 11px; font-variant-numeric: tabular-nums; white-space: nowrap; }
</style>
