<script setup lang="ts">
import { onMounted } from 'vue'
import { UieCanvas, useUie } from '@tinadec/ui'
import AppHeader from '@/components/AppHeader.vue'
import { marketController } from '@/controllers/MarketController'

const wb = useUie()
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
    <UieCanvas />
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
