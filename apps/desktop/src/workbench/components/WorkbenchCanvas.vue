<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref } from 'vue'
import WorkbenchColumn from './WorkbenchColumn.vue'
import { useWorkbench } from '../useWorkbench'

const wb = useWorkbench()

const canvasRef = ref<HTMLElement | null>(null)
let observer: ResizeObserver | null = null

function measure() {
  const el = canvasRef.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  // Round to integer pixels so ResizeObserver can't feed back sub-pixel drift.
  wb.setContainerSize({ width: Math.round(rect.width), height: Math.round(rect.height) })
}

onMounted(() => {
  measure()
  if (typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(() => measure())
    if (canvasRef.value) observer.observe(canvasRef.value)
  }
  window.addEventListener('resize', measure)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  window.removeEventListener('resize', measure)
})
</script>

<template vapor>
  <div ref="canvasRef" class="wb-canvas">
    <!-- Columns are absolutely positioned by the constraint solver. -->
    <WorkbenchColumn
      v-for="slotId in wb.snapshot.value.columnOrder"
      :key="slotId"
      :column="wb.snapshot.value.columns[slotId]"
      :geometry="wb.geometry.value.columns[slotId]"
      :split="wb.geometry.value.splits[slotId]"
    />
  </div>
</template>

<style scoped>
.wb-canvas {
  position: absolute;
  inset: 0;
  overflow: hidden;
  /* Transparent — the background-layer in App.vue shows through. */
  background: transparent;
}
</style>
