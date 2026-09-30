<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref } from 'vue'
import UieColumn from './UieColumn.vue'
import { useUie } from './useUie'

const uie = useUie()

const canvasRef = ref<HTMLElement | null>(null)
let observer: ResizeObserver | null = null

function measure() {
  const el = canvasRef.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  // Round to integer pixels so ResizeObserver can't feed back sub-pixel drift.
  uie.setContainerSize({ width: Math.round(rect.width), height: Math.round(rect.height) })
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
  <div ref="canvasRef" class="uie-canvas">
    <!-- Columns are absolutely positioned by the constraint solver. -->
    <UieColumn
      v-for="slotId in uie.snapshot.value.columnOrder"
      :key="slotId"
      :column="uie.snapshot.value.columns[slotId]"
      :geometry="uie.geometry.value.columns[slotId]"
      :split="uie.geometry.value.splits[slotId]"
      :dock="uie.geometry.value.docks[slotId]"
    />
  </div>
</template>

<style scoped>
.uie-canvas {
  position: absolute;
  inset: 0;
  overflow: hidden;
  /* Transparent — the background-layer in App.vue shows through. */
  background: transparent;
}
</style>
