<script setup lang="ts">
import { computed, ref } from 'vue'
import UieStack from './UieStack.vue'
import { useUie } from './useUie'
import type { ColumnGeometry, SplitGeometry, UieColumn as ColumnModel } from '../engine/types'

const props = defineProps<{
  column: ColumnModel
  geometry: ColumnGeometry
  split?: SplitGeometry
}>()

const wb = useUie()

const primaryGeometry = computed(() =>
  props.split
    ? props.split.upper
    : { x: 0, y: 0, width: props.geometry.width, height: props.geometry.height, degraded: false },
)
const primaryDegraded = computed(() => !!props.split?.upper.degraded)

// Instances in this column, mapped from the snapshot by tabIds (primary).
const primaryInstances = computed(() =>
  props.column.primary.tabIds
    .map((id) => wb.snapshot.value.cards[id])
    .filter((c) => !!c),
)
const secondaryInstances = computed(() =>
  props.column.secondary
    ? props.column.secondary.tabIds.map((id) => wb.snapshot.value.cards[id]).filter((c) => !!c)
    : [],
)

function resizeColumn(width: number) {
  wb.bus.dispatch(
    {
      command: { type: 'resizeColumn', scope: wb.scope.value, slotId: props.column.slotId, width },
      source: 'user',
      expectedRevision: wb.snapshot.value.revision,
    },
    { gestureId: `resize:${props.column.slotId}` },
  )
}

function resizeSplit(ratio: number) {
  wb.bus.dispatch(
    {
      command: { type: 'resizeSplit', scope: wb.scope.value, slotId: props.column.slotId, ratio },
      source: 'user',
      expectedRevision: wb.snapshot.value.revision,
    },
    { gestureId: `split:${props.column.slotId}` },
  )
}

// Pointer resize for column edge.
const isResizing = ref(false)
let resizeStart = 0
let resizeWidth = 0
function onResizeDown(event: PointerEvent) {
  event.preventDefault()
  isResizing.value = true
  resizeStart = event.clientX
  resizeWidth = props.geometry.width
  window.addEventListener('pointermove', onResizeMove)
  window.addEventListener('pointerup', onResizeUp)
}
function onResizeMove(event: PointerEvent) {
  const delta = event.clientX - resizeStart
  const newWidth = props.column.slotId === 'right' ? resizeWidth - delta : resizeWidth + delta
  resizeColumn(Math.max(160, newWidth))
}
function onResizeUp() {
  isResizing.value = false
  window.removeEventListener('pointermove', onResizeMove)
  window.removeEventListener('pointerup', onResizeUp)
}

// Split divider drag.
let splitStartY = 0
let splitRatioStart = 0
function onDividerDown(event: PointerEvent) {
  event.preventDefault()
  splitStartY = event.clientY
  splitRatioStart = props.column.splitRatio ?? 0.65
  window.addEventListener('pointermove', onDividerMove)
  window.addEventListener('pointerup', onDividerUp)
}
function onDividerMove(event: PointerEvent) {
  const delta = event.clientY - splitStartY
  const colHeight = props.geometry.height
  if (colHeight <= 0) return
  const ratio = Math.max(0.1, Math.min(0.9, splitRatioStart + delta / colHeight))
  resizeSplit(ratio)
}
function onDividerUp() {
  window.removeEventListener('pointermove', onDividerMove)
  window.removeEventListener('pointerup', onDividerUp)
}
</script>

<template vapor>
  <div
    class="wb-column"
    :class="{ 'is-resizing': isResizing }"
    :style="{
      left: `${geometry.x}px`,
      top: `${geometry.y}px`,
      width: `${geometry.width}px`,
      height: `${geometry.height}px`,
    }"
  >
    <!-- Resizer handle: Left column resizes via right edge, Right column resizes via left edge -->
    <div
      v-if="column.slotId === 'left' || column.slotId === 'right'"
      class="wb-column-resizer"
      :class="column.slotId === 'right' ? 'wb-column-resizer-left' : 'wb-column-resizer-right'"
      @pointerdown="onResizeDown"
    />

    <!-- Primary stack -->
    <UieStack
      :stack="column.primary"
      :geometry="primaryGeometry"
      :instances="primaryInstances"
      :degraded="primaryDegraded"
      :surface-mode="column.surfaceMode"
    />

    <!-- Secondary stack + divider -->
    <template v-if="column.secondary && split">
      <div
        class="wb-split-divider"
        :style="{ top: `${split.dividerY - geometry.y - 2}px` }"
        @pointerdown="onDividerDown"
      />
      <UieStack
        :stack="column.secondary"
        :geometry="{ x: 0, y: split.lower.y - geometry.y, width: geometry.width, height: split.lower.height, degraded: !!split.lower.degraded }"
        :instances="secondaryInstances"
        :degraded="!!split.lower.degraded"
        :surface-mode="column.surfaceMode"
      />
    </template>
  </div>
</template>

<style scoped>
.wb-column {
  position: absolute;
  min-height: 0;
  overflow: hidden;
  transition: left 0.25s cubic-bezier(0.2, 0, 0, 1), width 0.25s cubic-bezier(0.2, 0, 0, 1);
}

.wb-column.is-resizing {
  transition: none !important;
}

.wb-column-resizer-right {
  right: -4px;
}

.wb-column-resizer-left {
  left: -4px;
}

.wb-column-resizer {
  position: absolute;
  top: 0;
  bottom: 0;
  width: 8px;
  cursor: col-resize;
  z-index: 30;
  background: transparent;
}

/* Small translucent pill handle, vertically centered on the column edge.
   Hidden by default; fades in on hover/active. The 8px full-height hit
   area stays (pointerdown + cursor) — only the visual is a tiny pill. */
.wb-column-resizer::after {
  content: '';
  position: absolute;
  top: 50%;
  width: 3px;
  height: 28px;
  border-radius: 999px;
  background: var(--accent-primary);
  opacity: 0;
  transform: translateY(-50%);
  transition: opacity 0.15s ease;
}

.wb-column-resizer-right::after {
  /* Right edge extends 4px outside the column and is clipped by
     overflow:hidden, so the pill anchors fully inside the column. */
  left: 1px;
}

.wb-column-resizer-left::after {
  right: 1px;
}

.wb-column-resizer:hover::after,
.wb-column-resizer:active::after {
  opacity: 0.45;
}

.wb-split-divider {
  position: absolute;
  left: 0;
  right: 0;
  height: 4px;
  cursor: row-resize;
  z-index: 25;
  background: transparent;
  border-top: 1px solid var(--border-muted);
}

/* Horizontal pill handle, centered on the split line. Same style as the
   column-edge pill so both dividers read consistently. */
.wb-split-divider::after {
  content: '';
  position: absolute;
  left: 50%;
  top: 50%;
  width: 28px;
  height: 3px;
  border-radius: 999px;
  background: var(--accent-primary);
  opacity: 0;
  transform: translate(-50%, -50%);
  transition: opacity 0.15s ease;
}

.wb-split-divider:hover::after,
.wb-split-divider:active::after {
  opacity: 0.45;
}
</style>
