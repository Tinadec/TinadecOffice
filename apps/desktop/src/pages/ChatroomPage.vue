<script setup lang="ts">
import { onMounted, watch } from 'vue'
import { UieShell, initUie, useUie, buildUieRegistry, createComponentLookup, createElectronLayoutAdapter, createLayerStore } from '@tinadec/ui'
import { homeController } from '@/controllers/HomeController'

const registry = buildUieRegistry()
initUie({ registry, componentFor: createComponentLookup(registry), persistence: { store: createLayerStore(createElectronLayoutAdapter()) } })
const wb = useUie()
onMounted(() => {
  homeController.start()
  if (wb.pageId.value !== 'chatroom') wb.applyPreset('chatroom')
})
// A cold deep link may finish layout hydration after mount. Keep this route's
// content selected, including that delayed restore from a previous home layout.
watch(wb.pageId, page => { if (page !== 'chatroom') wb.applyPreset('chatroom') }, { flush: 'post' })
</script>

<template>
  <UieShell />
</template>
