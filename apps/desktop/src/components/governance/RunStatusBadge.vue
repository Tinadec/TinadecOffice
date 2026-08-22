<script setup lang="ts">
import { computed } from 'vue'
import { runStatusTone, statusLabel } from '@/lib/statusPresentation'

const props = defineProps<{
  status: string
  label?: string
}>()

const tone = computed(() => runStatusTone(props.status))
const text = computed(() => props.label ?? statusLabel(props.status))
</script>

<template vapor>
  <span class="status-pill" :class="`status-pill--${tone}`" :data-run-status="status">{{ text }}</span>
</template>
