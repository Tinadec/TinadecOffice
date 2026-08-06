<script setup lang="ts">
import { onMounted } from 'vue'
import WorkbenchShell from '@/workbench/components/WorkbenchShell.vue'
import { initWorkbench } from '@/workbench/useWorkbench'
import { buildWorkbenchRegistry, createComponentLookup } from '@/workbench/cards'
import { createElectronLayoutAdapter } from '@/workbench/persistence/types'
import { createLayerStore } from '@/workbench/persistence/layerStore'
import { homeController } from '@/controllers/HomeController'

// Initialize the Workbench store once (module singleton). Subsequent mounts
// reuse the existing store so layout state survives route changes.
if (typeof window !== 'undefined') {
  const registry = buildWorkbenchRegistry()
  const layerStore = createLayerStore(createElectronLayoutAdapter())
  initWorkbench({
    registry,
    componentFor: createComponentLookup(registry),
    persistence: { store: layerStore },
  })
}

onMounted(() => {
  homeController.start()
})
</script>

<template>
  <WorkbenchShell />
</template>
