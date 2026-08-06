<script setup lang="ts">
import WorkbenchCanvas from './WorkbenchCanvas.vue'
import AppHeader from '@/components/AppHeader.vue'
import { useWorkbench } from '../useWorkbench'

// The Workbench store is a module singleton; this component reads it.
const wb = useWorkbench()
</script>

<template vapor>
  <main class="shell">
    <div class="top-drag-bar" />
    <AppHeader />
    <WorkbenchCanvas />
  </main>
</template>

<style scoped>
.shell {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  background: transparent;
}
</style>
