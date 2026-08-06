<script setup lang="ts">
import PreviewBrowserPanel from '@/components/PreviewBrowserPanel.vue'
import { useOptionalWorkbenchCard } from '../cardContext'

const card = useOptionalWorkbenchCard()

function updateUrl(url: string): void {
  card?.updateState({ ...card.card.value.state, url })
}
</script>

<template>
  <PreviewBrowserPanel
    :initial-url="(card?.card.value.state.url as string | undefined) ?? ''"
    @update:url="updateUrl"
  />
</template>

<style scoped>
:deep(.preview-browser-panel) {
  width: 100%;
  height: 100%;
  min-height: 0;
}
</style>
