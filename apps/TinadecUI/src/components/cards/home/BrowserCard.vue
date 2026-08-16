<script setup lang="ts">
import { computed, inject } from 'vue'
import PreviewBrowserPanel from '@/components/PreviewBrowserPanel.vue'

// Card context is provided by UieCardHost (see UieCardHost.vue).
const cardState = inject<Record<string, unknown> | undefined>('wb:cardState')

const initialUrl = computed(() => (cardState?.url as string) ?? '')
</script>

<template vapor>
  <PreviewBrowserPanel :initial-url="initialUrl" />
</template>
