<script setup lang="ts">
import { UiSkeleton } from '@/components/ui'
</script>

<template>
  <div class="settings-module-loading" aria-live="polite" aria-label="Loading settings module">
    <UiSkeleton class="h-7 w-48" />
    <UiSkeleton class="h-4 w-80 max-w-full" />
    <UiSkeleton class="h-28 w-full" />
    <UiSkeleton class="h-20 w-full" />
  </div>
</template>

<style scoped>
.settings-module-loading { display: flex; flex-direction: column; gap: 12px; padding: 24px; }
</style>
