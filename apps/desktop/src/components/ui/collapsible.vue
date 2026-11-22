<script setup lang="ts">
import { cn } from '@/lib/utils'
import { ref, watch } from 'vue'

interface Props {
  open?: boolean
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:open': [value: boolean]
}>()

const isOpen = ref(Boolean(props.open))

watch(() => props.open, (val) => {
  isOpen.value = Boolean(val)
})

function toggle() {
  isOpen.value = !isOpen.value
  emit('update:open', isOpen.value)
}
</script>

<template>
  <div :class="cn('ui-collapsible', props.class)" :data-state="isOpen ? 'open' : 'closed'">
    <div class="ui-collapsible-trigger" @click="toggle">
      <slot name="trigger" :open="isOpen" :toggle="toggle" />
    </div>
    <div
      data-slot="collapsible-content"
      class="ui-collapsible-panel"
      :data-state="isOpen ? 'open' : 'closed'"
      :aria-hidden="!isOpen"
    >
      <div class="ui-collapsible-inner">
        <slot />
      </div>
    </div>
  </div>
</template>

<style scoped>
.ui-collapsible-panel {
  display: grid;
  grid-template-rows: 0fr;
  opacity: 0;
  visibility: hidden;
  pointer-events: none;
  transition: grid-template-rows 200ms var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
              opacity 160ms var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
              visibility 0s linear 200ms;
}

.ui-collapsible-panel[data-state="open"] {
  grid-template-rows: 1fr;
  opacity: 1;
  visibility: visible;
  pointer-events: auto;
  transition: grid-template-rows 200ms var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
              opacity 160ms var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
              visibility 0s linear 0s;
}

.ui-collapsible-inner {
  overflow: hidden;
  min-height: 0;
}

@media (prefers-reduced-motion: reduce) {
  .ui-collapsible-panel,
  .ui-collapsible-panel[data-state="open"] {
    transition: none;
  }
}
</style>
