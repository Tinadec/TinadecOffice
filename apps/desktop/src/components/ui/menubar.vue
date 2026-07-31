<script setup lang="ts">
import { cn } from '@/lib/utils'

interface Props {
  class?: string
}

const props = defineProps<Props>()
</script>

<template>
  <div :class="cn('flex h-9 items-center space-x-1 rounded-md border bg-[var(--surface-section)] p-1 shadow-sm', props.class)">
    <slot />
  </div>
</template>
