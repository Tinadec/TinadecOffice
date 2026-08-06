<script setup lang="ts">
import { computed } from 'vue'
import PreviewBrowserPanel from '@/components/PreviewBrowserPanel.vue'

const props = defineProps<{
  /** Serialized card state (may carry url). */
  cardState?: Record<string, unknown>
}>()

const initialUrl = computed(() => (props.cardState?.url as string) ?? '')
</script>

<template>
  <PreviewBrowserPanel :initial-url="initialUrl" />
</template>
