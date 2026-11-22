<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref, watch } from 'vue'
import UieColumn from './UieColumn.vue'
import { useUie } from './useUie'

const uie = useUie()
const props = defineProps<{ spatial?: boolean; spatialPanel?: boolean; fullBleed?: boolean }>()
const panelVisited = ref(false)
watch(() => props.spatialPanel, value => { if (value) panelVisited.value = true }, { immediate: true })
const slots = computed(() => props.spatial ? panelVisited.value ? ['left', 'right'] as const : ['left'] as const : uie.snapshot.value.columnOrder)
const spaceLeft = computed(() => {
  const leftEdge = uie.geometry.value.columns.left.x + uie.geometry.value.columns.left.width
  return props.fullBleed ? leftEdge : leftEdge + uie.snapshot.value.gap
})
const spaceRight = computed(() =>
  props.spatialPanel
    ? uie.geometry.value.columns.right.width + uie.snapshot.value.gap
    : props.fullBleed ? 0 : 8,
)

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

<template>
  <div ref="canvasRef" class="uie-canvas" :class="{ 'uie-canvas--spatial': spatial, 'uie-canvas--full-bleed': spatial && fullBleed }">
    <!-- Columns are absolutely positioned by the constraint solver. -->
    <UieColumn
      v-for="slotId in slots"
      :key="slotId"
      v-show="!spatial || slotId !== 'right' || spatialPanel"
      :column="uie.snapshot.value.columns[slotId]"
      :geometry="uie.geometry.value.columns[slotId]"
      :split="uie.geometry.value.splits[slotId]"
      :dock="uie.geometry.value.docks[slotId]"
    />
    <section
      v-if="spatial"
      class="uie-space-stage"
      :class="{ 'uie-space-stage--full-bleed': fullBleed }"
      :style="{ left: `${spaceLeft}px`, right: `${spaceRight}px` }"
    >
      <slot />
    </section>
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
.uie-canvas--spatial :deep(.uie-column) {
  z-index: 12;
}
.uie-canvas--spatial :deep(.uie-column--overlay) {
  z-index: 20;
}
.uie-space-stage {
  position: absolute;
  top: 0;
  right: 8px;
  bottom: 0;
  z-index: 1;
  min-width: 0;
  overflow: hidden;
}
.uie-space-stage--full-bleed {
  top: 0;
  right: 0;
  bottom: 0;
}
</style>
