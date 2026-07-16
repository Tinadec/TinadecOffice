<script setup lang="ts">
import { cn } from '@/lib/utils'

interface Props {
  class?: string
}

const props = defineProps<Props>()
</script>

<template>
  <div :class="cn('rounded-lg border bg-card text-card-foreground shadow', props.class)">
    <div v-if="$slots.header" class="card-header">
      <slot name="header" />
    </div>
    <div v-if="$slots.content || $slots.default" class="card-content">
      <slot name="content">
        <slot />
      </slot>
    </div>
    <div v-if="$slots.footer" class="card-footer">
      <slot name="footer" />
    </div>
  </div>
</template>

<style scoped>
.card-header {
  padding: 16px 20px 12px;
  border-bottom: 1px solid var(--border-default);
}
.card-content {
  padding: 16px 20px;
}
.card-footer {
  padding: 12px 20px 16px;
  border-top: 1px solid var(--border-default);
}
</style>
