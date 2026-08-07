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
  /* relative + 100vh so the shell fills (and therefore props open) the
     .main-content wrapper — an absolute/inset:0 root would collapse the
     wrapper's height to 0 and its overflow:clip would blank the window. */
  position: relative;
  height: 100vh;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  background: transparent;
}
</style>
