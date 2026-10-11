<script setup lang="ts">
/**
 * Transcript scroll root. Owns the positioning context for `MessageScrollerButton`
 * (which is absolutely placed) and the `data-scrollable` state both the viewport and the
 * button read.
 */
import { useMessageScrollerContext } from './useMessageScroller'

const { autoscrolling, scrollableAttr } = useMessageScrollerContext()
</script>

<template>
  <div
    class="message-scroller"
    :data-scrollable="scrollableAttr"
    :data-active="autoscrolling ? '' : undefined"
  >
    <slot />
  </div>
</template>

<style scoped>
.message-scroller {
  position: relative;
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  min-height: 0;
  overflow: hidden;
}
</style>
