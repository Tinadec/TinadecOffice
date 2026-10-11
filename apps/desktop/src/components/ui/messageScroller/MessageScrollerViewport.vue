<script setup lang="ts">
import { onBeforeUnmount, onMounted, useTemplateRef, watch } from 'vue'
import { SCROLL_KEYS, useMessageScrollerContext } from './useMessageScroller'

const props = withDefaults(defineProps<{
  preserveScrollOnPrepend?: boolean
}>(), {
  preserveScrollOnPrepend: true,
})

const {
  autoscrolling,
  scrollableAttr,
  handleResize,
  setPreserveScrollOnPrepend,
  setViewportElement,
  syncAfterScroll,
  userScrollIntent,
} = useMessageScrollerContext()

const viewportRef = useTemplateRef<HTMLElement>('viewport')

watch(() => props.preserveScrollOnPrepend, setPreserveScrollOnPrepend, { immediate: true })

function onKeyDown(event: KeyboardEvent) {
  if (SCROLL_KEYS.has(event.key)) userScrollIntent()
}

let resizeObserver: ResizeObserver | null = null
let resizeFrame = 0

onMounted(() => {
  const viewport = viewportRef.value
  setViewportElement(viewport)
  if (!viewport || typeof ResizeObserver === 'undefined') return
  resizeObserver = new ResizeObserver(() => {
    window.cancelAnimationFrame(resizeFrame)
    resizeFrame = window.requestAnimationFrame(handleResize)
  })
  resizeObserver.observe(viewport)
})

onBeforeUnmount(() => {
  window.cancelAnimationFrame(resizeFrame)
  resizeObserver?.disconnect()
  resizeObserver = null
  setViewportElement(null)
})
</script>

<template>
  <!--
    A native scroll container, not a `UiScrollArea` wrapper: the engine measures this
    element directly (`scrollTop`, `clientHeight`, spacer maths), and a second wrapper
    between it and the content would break that measurement.

    `data-active` drives the scrollbar and edge fade from `styles.css`.
  -->
  <div
    ref="viewport"
    class="message-scroller-viewport"
    role="region"
    :aria-label="$t('chat.transcript')"
    tabindex="0"
    :data-scrollable="scrollableAttr"
    :data-active="autoscrolling ? '' : undefined"
    @scroll="syncAfterScroll()"
    @wheel="userScrollIntent()"
    @touchmove="userScrollIntent()"
    @keydown="onKeyDown"
  >
    <slot />
  </div>
</template>
