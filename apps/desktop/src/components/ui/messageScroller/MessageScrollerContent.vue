<script setup lang="ts">
/**
 * Transcript scroll scaffolding. Three elements, no visual opinion of their own — the
 * material, padding and typography come from `.message-scroller-*` in `styles.css`.
 *
 * Provider must wrap a height-bounded parent so the viewport can actually scroll.
 */
import { onBeforeUnmount, onMounted, useTemplateRef } from 'vue'
import { useMessageScrollerContext } from './useMessageScroller'

const { setContentElement, setSpacerElement, handleContentChange, handleResize } = useMessageScrollerContext()

const contentRef = useTemplateRef<HTMLElement>('content')
const spacerRef = useTemplateRef<HTMLElement>('spacer')

let mutationObserver: MutationObserver | null = null
let resizeObserver: ResizeObserver | null = null
let resizeFrame = 0

onMounted(() => {
  const content = contentRef.value
  if (!content) return

  setContentElement(content)
  setSpacerElement(spacerRef.value ?? null)
  handleContentChange()

  // Rows are appended by Vue and grown by streaming Markdown, so both a childList
  // mutation and a size change have to feed the scroll engine.
  if (typeof MutationObserver !== 'undefined') {
    mutationObserver = new MutationObserver(() => handleContentChange())
    mutationObserver.observe(content, { childList: true })
  }

  if (typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(() => {
      window.cancelAnimationFrame(resizeFrame)
      resizeFrame = window.requestAnimationFrame(handleResize)
    })
    resizeObserver.observe(content)
  }
})

onBeforeUnmount(() => {
  window.cancelAnimationFrame(resizeFrame)
  mutationObserver?.disconnect()
  resizeObserver?.disconnect()
  mutationObserver = null
  resizeObserver = null
  setContentElement(null)
  setSpacerElement(null)
})
</script>

<template>
  <div ref="content" class="message-scroller-content" role="log" aria-live="polite" aria-relevant="additions">
    <slot />
    <!-- Tail spacer: holds the transcript's height while a turn is anchored near the top,
         so a streaming reply grows into empty room instead of pushing the anchor away. -->
    <div ref="spacer" aria-hidden="true" data-message-scroller-spacer hidden />
  </div>
</template>
