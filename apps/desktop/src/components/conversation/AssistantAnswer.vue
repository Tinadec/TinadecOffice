<script setup lang="ts">
import { Check, Copy } from '@lucide/vue'
import { computed, ref, toRef } from 'vue'
import { useI18n } from 'vue-i18n'
import MarkdownRender from '@/components/MarkdownRender.vue'
import { useSmoothText } from '@/composables/useSmoothText'
import { feedLocale, formatClock, formatDuration } from '@/lib/conversation/format'

/**
 * The answer body. It is never folded: activity collapses around it, the answer stays.
 * While streaming, the text is paced by useSmoothText (pace only; MarkdownRender still
 * parses and sanitises) and ends in a caret. The footer — copy, time, how long the turn
 * worked and how many tools it ran — stays visible on the last answer, on hover elsewhere.
 */
const props = defineProps<{
  content: string
  streaming?: boolean
  createdAt?: string | null
  /** Elapsed work of the turn this answer closes; omitted while it is still running. */
  workedMs?: number | null
  toolCount?: number
  last?: boolean
  /** A short interim answer between steps: clamped to two quiet lines, the full text on hover. */
  compact?: boolean
}>()
const { t, locale } = useI18n()

const shown = useSmoothText(toRef(props, 'content'), computed(() => Boolean(props.streaming)))
const copied = ref(false)
const lang = computed(() => feedLocale(locale.value))
const clock = computed(() => (props.createdAt ? formatClock(props.createdAt, lang.value) : ''))

function copy() {
  void navigator.clipboard?.writeText(props.content)
  copied.value = true
  setTimeout(() => { copied.value = false }, 2000)
}
</script>

<template>
  <div class="conversation-answer" :class="{ 'is-last': last, 'is-compact': compact }" :title="compact ? content : undefined" :aria-busy="streaming ? 'true' : undefined" :data-testid="streaming ? 'chat-streaming-reply' : 'assistant-answer'">
    <div class="message-content assistant">
      <MarkdownRender :content="shown" />
      <span v-if="streaming" class="conversation-caret" aria-hidden="true" />
    </div>
    <div v-if="!streaming && !compact" class="conversation-answer-footer">
      <button type="button" class="conversation-icon-button" :aria-label="t(copied ? 'chat.copied' : 'chat.copy')" :title="t(copied ? 'chat.copied' : 'chat.copy')" @click="copy">
        <Check v-if="copied" :size="12" aria-hidden="true" />
        <Copy v-else :size="12" aria-hidden="true" />
      </button>
      <time v-if="clock" :datetime="createdAt ?? undefined">{{ clock }}</time>
      <span v-if="workedMs">{{ t('conversation.turn.workedFor', { duration: formatDuration(workedMs, lang) }) }}</span>
      <span v-if="toolCount">{{ t('conversation.turn.toolCount', { count: toolCount }, toolCount) }}</span>
    </div>
  </div>
</template>

<style scoped>
.conversation-answer { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.conversation-answer-footer { display: flex; align-items: center; gap: 8px; min-height: 22px; color: var(--text-muted); font-size: 11px; font-variant-numeric: tabular-nums; opacity: 0; transition: opacity 150ms cubic-bezier(0.23, 1, 0.32, 1); }
.conversation-answer.is-last .conversation-answer-footer,
.conversation-answer:hover .conversation-answer-footer,
.conversation-answer:focus-within .conversation-answer-footer { opacity: 1; }
/* The markdown body sets its own size and colour, so the compact note restyles it directly.
   No footer: an interim note is not where the turn's time and tool count belong. */
.conversation-answer.is-compact :deep(.markdown-body) { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden; color: var(--text-secondary); font-size: 12px; line-height: 1.5; }
.conversation-answer.is-compact :deep(.markdown-body p) { margin: 0; }
.conversation-caret { display: inline-block; width: 2px; height: 1em; margin-left: 1px; vertical-align: text-bottom; background: currentColor; animation: conversation-caret 1s steps(1) infinite; }
@keyframes conversation-caret { 50% { opacity: 0; } }
@media (prefers-reduced-motion: reduce) {
  .conversation-answer-footer { transition: none; }
  .conversation-caret { animation: none; }
}
</style>
