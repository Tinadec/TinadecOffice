<script setup lang="ts">
// RealMarketPage.vue — mounts the REAL MarketPage inside the Debug Studio
// preview viewport. The UIE engine must be initialized (without persistence)
// before UieCanvas/useUie run; see uieBridge.ts for why persistence stays off.
import { defineAsyncComponent } from 'vue'
import { ensurePreviewUie } from './uieBridge'
import PreviewNavScope from './PreviewNavScope.vue'

ensurePreviewUie()
const MarketPage = defineAsyncComponent(() => import('@/pages/MarketPage.vue'))
</script>

<template>
  <PreviewNavScope>
    <div class="real-page-host real-market">
      <MarketPage />
    </div>
  </PreviewNavScope>
</template>

<style scoped>
.real-page-host {
  height: 100%;
  overflow: auto;
}
.real-page-host :deep(.shell) {
  height: 100%;
  min-height: 0;
}
/* 页面自带的窗口拖拽条与窗口控制按钮在预览中无意义，隐藏避免干扰 */
.real-page-host :deep(.top-drag-bar),
.real-page-host :deep(.window-controls) {
  display: none !important;
}
</style>
