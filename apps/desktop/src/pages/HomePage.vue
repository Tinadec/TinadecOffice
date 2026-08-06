<script setup lang="ts">
import { onMounted } from 'vue'
import WorkbenchShell from '@/workbench/components/WorkbenchShell.vue'
import { initWorkbench } from '@/workbench/useWorkbench'
import { buildWorkbenchRegistry, createComponentLookup } from '@/workbench/cards'
import { homeController } from '@/controllers/HomeController'

// Initialize the Workbench store once (module singleton). Subsequent mounts
// reuse the existing store so layout state survives route changes.
if (typeof window !== 'undefined') {
  const registry = buildWorkbenchRegistry()
  initWorkbench({
    registry,
    componentFor: createComponentLookup(registry),
  })
}

onMounted(() => {
  homeController.start()
})
</script>

<template>
  <WorkbenchShell />
</template>
