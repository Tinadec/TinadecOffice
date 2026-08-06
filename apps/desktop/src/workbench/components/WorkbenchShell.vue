<script setup lang="ts">
import { onMounted, ref } from 'vue'
import WorkbenchCanvas from './WorkbenchCanvas.vue'
import AppHeader from '@/components/AppHeader.vue'
import { useWorkbench } from '../useWorkbench'

const wb = useWorkbench()

// main-rise entrance animation (matches the pre-refactor HomePage behavior).
const isChildWindow = new URLSearchParams(window.location.search).get('splash') === '0'
const riseTransitionName = isChildWindow ? 'no-transition' : 'main-rise'
const mounted = ref(false)

onMounted(() => {
  // Trigger the entrance animation once after mount.
  requestAnimationFrame(() => {
    mounted.value = true
  })
})
</script>

<template>
  <Transition :name="riseTransitionName" appear>
    <main class="shell" :class="{ 'shell-entered': mounted }">
      <div class="top-drag-bar" />
      <AppHeader />
      <WorkbenchCanvas />
    </main>
  </Transition>
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
