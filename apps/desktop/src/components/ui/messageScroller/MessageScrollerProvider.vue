<script setup lang="ts">
/**
 * Provides the transcript scroll engine to everything below it.
 *
 * Adapted from the shadcn-vue `message-scroller` registry item (MIT, unovue). Lives under
 * `components/ui/` next to the other hand-written primitives; exports the composables too
 * so a caller outside the transcript (a search result, a jump menu) can drive it.
 */
import type { MessageScrollerProviderProps } from './useMessageScroller'
import { provideMessageScroller } from './useMessageScroller'

const props = withDefaults(defineProps<MessageScrollerProviderProps>(), {
  autoScroll: false,
  defaultScrollPosition: 'end',
})

provideMessageScroller(props)
</script>

<template>
  <slot />
</template>
