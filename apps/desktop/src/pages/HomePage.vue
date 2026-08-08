<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
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

// Spatial transition state — declarative, class-driven.
// Same mechanism as the Settings page: toggling container classes
// (.home-entering / .home-exiting) drives CSS @keyframes in
// page-transitions.css. No <Transition>, no v-show, no document.querySelector.
//
// NOTE: The transition container is a CLASSIC plain div — never the Vapor
// WorkbenchShell component itself. Classic-around-Vapor Transition leave
// paths crash the interop unmount (see VaporExemptions.ts, AppSplash entry,
// commit 46a5988). We keep the classic wrapper and toggle classes on it.
//
// Entry animation: the container is always visible (never display:none), so
// WorkbenchCanvas.measure() reads the real .wb-canvas size on the first frame
// and the columns get correct geometry. The .home-entering class then plays
// the rise-from-below keyframes on the freshly mounted stacks — on both
// initial load and when returning from settings (fresh stacks replay the
// keyframes, exactly like the Settings nav/content do on every mount).
const homeEntering = ref(false)
const homeExiting = ref(false)

// Enter duration must cover the 0.35s rise + 0.1s max stagger + margin, so the
// class is removed only after every column has settled (avoids replaying the
// rise on later layout-driven stack remounts).
const ENTER_DURATION_MS = 500

// Exit duration must match page-transitions.css (@keyframes home-up-exit:
// 0.2s per column + 0.1s max stagger delay).
const EXIT_DURATION_MS = 300

onMounted(() => {
  homeController.start()
  homeEntering.value = true
  window.setTimeout(() => {
    homeEntering.value = false
  }, ENTER_DURATION_MS)
})

// Spatial exit: toggle .home-exiting so the exit keyframes drive the staggered
// upward flight declaratively, then complete the navigation once the CSS
// animation settles. Mirrors SettingsPage.vue's onBeforeRouteLeave guard.
onBeforeRouteLeave((_to, _from, next) => {
  if (homeExiting.value) {
    next()
    return
  }
  homeExiting.value = true
  setTimeout(() => next(), EXIT_DURATION_MS)
})
</script>

<template>
  <div
    class="home-page-container"
    :class="{ 'home-entering': homeEntering, 'home-exiting': homeExiting }"
  >
    <WorkbenchShell />
  </div>
</template>

<style scoped>
.home-page-container {
  position: relative;
  width: 100%;
  height: 100%;
  overflow: hidden;
}
</style>
