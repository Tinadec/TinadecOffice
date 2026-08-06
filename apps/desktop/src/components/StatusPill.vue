<script setup lang="ts">
import { toneForStatus } from '../format'

defineProps<{
  status: string
}>()
</script>

<template vapor>
  <span class="status-pill" :class="toneForStatus(status)">{{ status }}</span>
</template>
