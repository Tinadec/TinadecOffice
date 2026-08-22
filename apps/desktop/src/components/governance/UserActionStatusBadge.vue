<script setup lang="ts">
import { computed } from 'vue'
import { userActionStatusTone, statusLabel } from '@/lib/statusPresentation'

const props = defineProps<{
  status: string
  /** Localized override; falls back to the humanized raw value. */
  label?: string
}>()

const tone = computed(() => userActionStatusTone(props.status))
const text = computed(() => props.label ?? statusLabel(props.status))
</script>

<template vapor>
  <span class="status-pill" :class="`status-pill--${tone}`" :data-action-status="status">{{ text }}</span>
</template>
