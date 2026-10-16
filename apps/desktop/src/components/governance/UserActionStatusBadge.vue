<script setup lang="ts">
import { computed } from 'vue'
import { userActionStatusTone, statusLabel } from '@/lib/statusPresentation'
import { useStatusLabels } from '@/lib/statusLabels'

const props = defineProps<{
  status: string
  /** Localized override; otherwise the status's translated label, or the humanized raw value. */
  label?: string
}>()

const { userActionStatusLabel } = useStatusLabels()
const tone = computed(() => userActionStatusTone(props.status))
const text = computed(() => {
  if (props.label) return props.label
  const translated = userActionStatusLabel(props.status)
  return translated === props.status ? statusLabel(props.status) : translated
})
</script>

<template>
  <span class="status-pill" :class="`status-pill--${tone}`" :data-action-status="status">{{ text }}</span>
</template>
