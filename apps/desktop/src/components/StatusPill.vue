<script setup lang="ts">
import { toneForStatus } from '../format'

defineProps<{
  status: string
  /** The status in words; the status itself when absent. */
  label?: string
}>()
</script>

<template vapor>
  <span class="status-pill" :class="toneForStatus(status)">{{ label ?? status }}</span>
</template>
