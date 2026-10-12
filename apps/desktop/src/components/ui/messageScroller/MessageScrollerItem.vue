<script setup lang="ts">
import { onBeforeUnmount, onMounted, useTemplateRef, watch } from 'vue'
import { useMessageScrollerRegister } from './useMessageScroller'

const props = withDefaults(defineProps<{
  /** Stable id used for anchoring, visibility and `scrollToMessage`. */
  messageId?: string
  /** Marks this row as the start of a turn. */
  scrollAnchor?: boolean
}>(), {
  scrollAnchor: false,
})

const register = useMessageScrollerRegister()
const itemRef = useTemplateRef<HTMLElement>('item')

onMounted(() => {
  if (props.messageId && itemRef.value) register(props.messageId, itemRef.value, null)
})

watch(() => props.messageId, (messageId, previousMessageId) => {
  const element = itemRef.value
  if (!element) return
  if (previousMessageId) register(previousMessageId, null, element)
  if (messageId) register(messageId, element, null)
})

onBeforeUnmount(() => {
  if (props.messageId && itemRef.value) register(props.messageId, null, itemRef.value)
})
</script>

<template>
  <div
    ref="item"
    class="message-scroller-item"
    :data-message-id="messageId"
    :data-scroll-anchor="scrollAnchor ? 'true' : 'false'"
  >
    <slot />
  </div>
</template>

<style scoped>
.message-scroller-item {
  min-width: 0;
  flex-shrink: 0;
}
/* Keep real row heights for precise history prepend/jump anchoring. */
</style>
