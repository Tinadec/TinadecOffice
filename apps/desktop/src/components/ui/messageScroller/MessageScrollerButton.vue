<script setup lang="ts">
/**
 * "Back to the bottom", with a count of what arrived while the reader was scrolled away.
 *
 * The unseen count exists because the transcript is no longer the only thing moving:
 * a run emits activity, approvals and a streamed reply regardless of where the reader is
 * looking. Without the count the only signal was an arrow that might mean nothing arrived.
 */
import { computed, ref, useTemplateRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ArrowDown } from '@lucide/vue'
import { useMessageScroller, useMessageScrollerScrollable } from './useMessageScroller'

const props = withDefaults(defineProps<{
  direction?: 'start' | 'end'
  behavior?: ScrollBehavior
  /** Rows carry `data-message-id`; the count is of ids this reader has not seen at the end. */
  showUnseen?: boolean
}>(), {
  direction: 'end',
  behavior: 'smooth',
  showUnseen: true,
})

const { t } = useI18n()
const { scrollToEnd, scrollToStart } = useMessageScroller()
const scrollable = useMessageScrollerScrollable()

const active = computed(() => (props.direction === 'start' ? scrollable.value.start : scrollable.value.end))

const anchorRef = useTemplateRef<HTMLButtonElement>('anchor')

/** Ids the reader has already been at the end for. Null until the first measurement. */
const seen = ref<Set<string> | null>(null)
const unseen = ref(0)
let observer: MutationObserver | null = null

function rowIds(root: HTMLElement): string[] {
  return Array.from(root.querySelectorAll<HTMLElement>('[data-message-id]'), (node) => node.dataset.messageId ?? '')
    .filter(Boolean)
}

function count() {
  const root = anchorRef.value?.closest('.message-scroller')
  if (!root) return
  const ids = rowIds(root as HTMLElement)
  // Always at the bottom: nothing is unseen by definition, and every row counts as read.
  if (!scrollable.value.end) {
    seen.value = new Set(ids)
    unseen.value = 0
    return
  }
  if (seen.value === null) {
    seen.value = new Set(ids)
    unseen.value = 0
    return
  }
  unseen.value = ids.filter((id) => !seen.value!.has(id)).length
}

watch([active, scrollable], count, { immediate: true, flush: 'post' })

watch(anchorRef, (element) => {
  observer?.disconnect()
  observer = null
  if (!element || !props.showUnseen || props.direction !== 'end') return
  const content = element.closest('.message-scroller')?.querySelector('.message-scroller-content')
  if (!content || typeof MutationObserver === 'undefined') return
  // Rows are appended, not replaced, so childList on the content is the whole signal.
  observer = new MutationObserver(count)
  observer.observe(content, { childList: true })
}, { immediate: true, flush: 'post' })

function onClick() {
  if (!active.value) return
  anchorRef.value?.blur()
  if (props.direction === 'start') scrollToStart({ behavior: props.behavior })
  else scrollToEnd({ behavior: props.behavior })
}
</script>

<template>
  <button
    ref="anchor"
    type="button"
    class="message-scroller-button"
    :data-direction="direction"
    :data-active="active ? 'true' : 'false'"
    :inert="!active"
    :tabindex="active ? undefined : -1"
    :aria-label="direction === 'end' ? t('chat.jumpToLatest') : t('chat.jumpToStart')"
    @click="onClick"
  >
    <ArrowDown :size="13" aria-hidden="true" />
    <span v-if="showUnseen && direction === 'end' && unseen > 0" class="message-scroller-button-count">
      {{ t('chat.unseenMessages', { count: unseen }, unseen) }}
    </span>
  </button>
</template>

<style scoped>
.message-scroller-button {
  position: absolute;
  left: 50%;
  z-index: 2;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 9px;
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  color: var(--text-primary);
  background: var(--surface-section);
  box-shadow: var(--shadow-panel);
  font-size: 11px;
  cursor: pointer;
  transform: translateX(-50%);
  transition: translate 0.2s cubic-bezier(0.23, 1, 0.32, 1), scale 0.2s cubic-bezier(0.23, 1, 0.32, 1), opacity 0.2s;
}

.message-scroller-button[data-direction='end'] { bottom: 14px; }
.message-scroller-button[data-direction='start'] { top: 14px; }
.message-scroller-button[data-direction='start'] :deep(svg) { transform: rotate(180deg); }

.message-scroller-button:hover { background: var(--surface-hover); }
.message-scroller-button:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 1px; }

/* Inactive is a state, not a removed element: keeping it mounted lets the count settle
   in place instead of the reader losing the affordance the moment they scroll up. */
.message-scroller-button[data-active='false'] {
  pointer-events: none;
  opacity: 0;
  scale: 0.95;
  transition-duration: 0.3s;
}
.message-scroller-button[data-active='true'] { opacity: 1; scale: 1; }

.message-scroller-button-count { font-variant-numeric: tabular-nums; white-space: nowrap; }

@media (prefers-reduced-motion: reduce) {
  .message-scroller-button { transition: opacity 0.01ms; }
}
</style>
