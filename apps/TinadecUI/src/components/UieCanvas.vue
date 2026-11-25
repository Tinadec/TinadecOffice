<script setup lang="ts">
import { computed, onMounted, onBeforeUnmount, ref, watch } from 'vue'
import UieColumn from './UieColumn.vue'
import { useUie } from './useUie'
import FloatingChatPip from '@/components/chat/FloatingChatPip.vue'
import { useFeaturePanelMaximize } from '@/composables/useFeaturePanelMaximize'
import { flattenDock } from '../engine/constraints'
import type {
  ColumnGeometry,
  SplitGeometry,
  UieDockGeometry,
  UieSlotId,
} from '../engine/types'

const uie = useUie()
const { maximized } = useFeaturePanelMaximize()
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

const isHomeMaximized = computed(() => {
  if (props.spatial) return false
  if (uie.pageId.value !== 'home') return false
  if (!maximized.value) return false
  const rightCol = uie.snapshot.value.columns.right
  const centerGeom = uie.geometry.value.columns.center
  const rightGeom = uie.geometry.value.columns.right
  if (!rightCol || rightCol.collapsed || !centerGeom || !rightGeom) return false
  return true
})

const maximizedRect = computed(() => {
  const centerGeom = uie.geometry.value.columns.center
  const rightGeom = uie.geometry.value.columns.right
  if (!centerGeom || !rightGeom) {
    return rightGeom
      ? { x: rightGeom.x, y: rightGeom.y, width: rightGeom.width, height: rightGeom.height }
      : { x: 0, y: 0, width: 0, height: 0 }
  }
  const width = rightGeom.overlay
    ? Math.max(rightGeom.width, rightGeom.x + rightGeom.width - centerGeom.x)
    : centerGeom.width + uie.snapshot.value.gap + rightGeom.width
  return {
    x: centerGeom.x,
    y: rightGeom.y,
    width,
    height: rightGeom.height,
  }
})

function columnGeometryFor(slotId: UieSlotId): ColumnGeometry {
  const base = uie.geometry.value.columns[slotId]
  if (slotId === 'right' && isHomeMaximized.value) {
    const rect = maximizedRect.value
    return {
      ...base,
      x: rect.x,
      y: rect.y,
      width: rect.width,
      height: rect.height,
      effectiveWidth: rect.width,
      overlay: false,
    }
  }
  return base
}

function splitGeometryFor(slotId: UieSlotId): SplitGeometry | undefined {
  const base = uie.geometry.value.splits[slotId]
  if (!base || slotId !== 'right' || !isHomeMaximized.value) return base
  const rect = maximizedRect.value
  return {
    ...base,
    upper: { ...base.upper, x: rect.x, width: rect.width },
    lower: { ...base.lower, x: rect.x, width: rect.width },
  }
}

function dockGeometryFor(slotId: UieSlotId): UieDockGeometry | undefined {
  const base = uie.geometry.value.docks[slotId]
  if (slotId !== 'right' || !isHomeMaximized.value) return base
  const rightCol = uie.snapshot.value.columns.right
  if (!rightCol?.dock) return base
  const rect = maximizedRect.value
  const flat = flattenDock({ width: rect.width, height: rect.height }, rightCol.dock)
  return {
    slotId: 'right',
    panes: flat.panes,
    dividers: flat.dividers,
    degradedPanes: flat.degradedPanes,
    collapsible: flat.panes.filter((p) => !p.degraded).length > 1,
  }
}

const resizingColumns = ref<Record<string, boolean>>({})
const anyColumnResizing = computed(() => Object.values(resizingColumns.value).some(Boolean))
function onColumnResizingChange(slotId: UieSlotId, active: boolean) {
  if (active) {
    resizingColumns.value = { ...resizingColumns.value, [slotId]: true }
  } else if (resizingColumns.value[slotId]) {
    const copy = { ...resizingColumns.value }
    delete copy[slotId]
    resizingColumns.value = copy
  }
}

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
      :geometry="columnGeometryFor(slotId)"
      :split="splitGeometryFor(slotId)"
      :dock="dockGeometryFor(slotId)"
      :hide-resizer="isHomeMaximized && slotId === 'right'"
      :maximized="isHomeMaximized && slotId === 'right'"
      :maximized-hidden="isHomeMaximized && slotId === 'center'"
      :peer-resizing="anyColumnResizing"
      @resizing-change="onColumnResizingChange(slotId, $event)"
    />
    <FloatingChatPip v-if="isHomeMaximized" :bounds="maximizedRect" />
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
