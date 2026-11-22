<script setup lang="ts">
import { cn } from '@/lib/utils'
import { computed } from 'vue'

interface Props {
  modelValue?: number
  max?: number
  class?: string
}

const props = withDefaults(defineProps<Props>(), {
  max: 100,
})

const percentage = computed(() => {
  const safeMax = props.max > 0 ? props.max : 100
  return Math.min(100, Math.max(0, ((props.modelValue || 0) / safeMax) * 100))
})
</script>

<template vapor>
  <div
    role="progressbar"
    data-slot="progress"
    :aria-valuenow="Math.round(percentage)"
    aria-valuemin="0"
    aria-valuemax="100"
    :class="cn(
      'relative h-1.5 w-full overflow-hidden rounded-full bg-[color-mix(in_srgb,var(--text-primary)_10%,transparent)]',
      props.class,
    )"
  >
    <div
      data-slot="progress-indicator"
      class="h-full w-full flex-1 rounded-full bg-[var(--bg-primary-button,hsl(var(--primary)))] transition-transform duration-300 ease-[cubic-bezier(0.2,0,0,1)] motion-reduce:transition-none"
      :style="{ transform: `translateX(-${100 - percentage}%)` }"
    />
  </div>
</template>
