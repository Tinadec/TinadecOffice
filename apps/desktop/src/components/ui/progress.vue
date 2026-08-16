<script setup lang="ts">
import { cn } from '@/lib/utils'

interface Props {
  modelValue?: number
  max?: number
  class?: string
}

const props = withDefaults(defineProps<Props>(), {
  max: 100,
})

const percentage = Math.min(100, Math.max(0, ((props.modelValue || 0) / props.max) * 100))
</script>

<template vapor>
  <div
    :class="cn(
      'relative h-2 w-full overflow-hidden rounded-full bg-primary/20',
      props.class,
    )"
  >
    <div
      class="h-full w-full flex-1 bg-primary transition-all"
      :style="{ transform: `translateX(-${100 - percentage}%)` }"
    />
  </div>
</template>
