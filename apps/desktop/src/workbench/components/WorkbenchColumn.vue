<script setup lang="ts">
import { computed } from 'vue'
import WorkbenchStack from './WorkbenchStack.vue'
import { useWorkbench } from '../useWorkbench'
import type { ColumnGeometry, SplitGeometry, WorkbenchColumn as ColumnModel } from '../types'

const props = defineProps<{
  column: ColumnModel
  geometry: ColumnGeometry
  split?: SplitGeometry
}>()

const wb = useWorkbench()

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

// Pointer resize for the column edge (right edge).
let resizeStart = 0
let resizeWidth = 0
function onResizeDown(event: PointerEvent) {
  event.preventDefault()
  resizeStart = event.clientX
  resizeWidth = props.geometry.width
  window.addEventListener('pointermove', onResizeMove)
  window.addEventListener('pointerup', onResizeUp)
}
function onResizeMove(event: PointerEvent) {
  const delta = event.clientX - resizeStart
  resizeColumn(Math.max(160, resizeWidth + delta))
}
function onResizeUp() {
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

<template>
  <div
    class="wb-column"
    :style="{
      left: `${geometry.x}px`,
      top: `${geometry.y}px`,
      width: `${geometry.width}px`,
      height: `${geometry.height}px`,
    }"
  >
    <!-- Column resize handle on the right edge -->
    <div class="wb-column-resizer" @pointerdown="onResizeDown" />

    <!-- Primary stack -->
    <WorkbenchStack
      :stack="column.primary"
      :geometry="primaryGeometry"
      :instances="primaryInstances"
      :degraded="primaryDegraded"
    />

    <!-- Secondary stack + divider -->
    <template v-if="column.secondary && split">
      <div
        class="wb-split-divider"
        :style="{ top: `${split.dividerY - geometry.y - 2}px` }"
        @pointerdown="onDividerDown"
      />
      <WorkbenchStack
        :stack="column.secondary"
        :geometry="{ x: 0, y: split.lower.y - geometry.y, width: geometry.width, height: split.lower.height, degraded: !!split.lower.degraded }"
        :instances="secondaryInstances"
        :degraded="!!split.lower.degraded"
      />
    </template>
  </div>
</template>

<style scoped>
.wb-column {
  position: absolute;
  min-height: 0;
  overflow: hidden;
}

.wb-column-resizer {
  position: absolute;
  top: 0;
  right: -4px;
  bottom: 0;
  width: 8px;
  cursor: col-resize;
  z-index: 30;
  background: transparent;
}

.wb-column-resizer:hover,
.wb-column-resizer:active {
  background: var(--accent-primary);
  opacity: 0.3;
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

.wb-split-divider:hover,
.wb-split-divider:active {
  background: var(--accent-primary);
  opacity: 0.3;
}
</style>
