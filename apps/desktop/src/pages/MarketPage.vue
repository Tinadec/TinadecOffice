<script setup lang="ts">
import { onMounted } from 'vue'
import WorkbenchCanvas from '@/workbench/components/WorkbenchCanvas.vue'
import AppHeader from '@/components/AppHeader.vue'
import { useWorkbench } from '@/workbench/useWorkbench'
import { marketController } from '@/controllers/MarketController'

const wb = useWorkbench()
const { busy, loading } = marketController

onMounted(() => {
  if (wb.pageId.value !== 'market') {
    wb.applyPreset('market')
  }
})
</script>

<template vapor>
  <main class="shell">
    <div class="top-drag-bar" />
    <AppHeader :busy="busy || loading" />
    <WorkbenchCanvas />
  </main>
</template>

<style scoped>
.shell {
  position: relative;
  height: 100vh;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  background: transparent;
}
</style>
