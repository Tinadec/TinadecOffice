<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import UieStack from './UieStack.vue'
import { useUie } from './useUie'
import { useDockDrag } from '@/composables/useDockDrag'
import type {
  ColumnGeometry,
  PersistedCardInstance,
  UieColumn as ColumnModel,
  UieDockGeometry,
  UieDockNode,
  UieDockPane,
} from '../engine/types'
import type { DockDropTarget } from '@/composables/useDockDrag'

const props = defineProps<{
  column: ColumnModel
  geometry: ColumnGeometry
  dock: UieDockGeometry
  resizing?: boolean
}>()

const wb = useUie()
const dockDrag = useDockDrag()
const containerRef = ref<HTMLElement | null>(null)

// ---- Pane lookup helpers (live tree) ----
function findPane(node: UieDockNode, paneId: string): UieDockPane | null {
  if (node.kind === 'pane') return node.paneId === paneId ? node : null
  return findPane(node.a, paneId) ?? findPane(node.b, paneId)
}
function findSplit(node: UieDockNode, splitId: string): Extract<UieDockNode, { kind: 'split' }> | null {
  if (node.kind === 'split') {
    if (node.splitId === splitId) return node
    return findSplit(node.a, splitId) ?? findSplit(node.b, splitId)
  }
  return null
}

/** Visible panes, pairing flattened geometry with live pane + card instances. */
const paneViews = computed(() => {
  const cards = wb.snapshot.value.cards
  return props.dock.panes
    .filter((g) => !g.degraded)
    .map((g) => {
      const pane = findPane(props.column.dock!, g.paneId)
      const instances: PersistedCardInstance[] = (pane?.tabIds ?? [])
        .map((id) => cards[id])
        .filter((c): c is PersistedCardInstance => !!c)
      return { geometry: g, pane, instances }
    })
})

// ---- Drop overlay (from the shared drag state) ----
type OverlayRect = DockDropTarget & { x: number; y: number; width: number; height: number }
const dropOverlay = computed<OverlayRect | null>(() => {
  const t = dockDrag.dropTarget.value
  if (!t) return null
  const pane = props.dock.panes.find((p) => p.paneId === t.paneId)
  if (!pane || pane.degraded) return null
  return { ...t, x: pane.x, y: pane.y, width: pane.width, height: pane.height }
})

// ---- Divider resize (resizeDockSplit gesture) ----
const isResizing = ref(false)
let divStartX = 0
let divStartY = 0
let divInitialRatio = 0.5
let divSplitId: string | null = null
let divDir: 'row' | 'column' = 'row'

function initialSplitRatio(splitId: string): number {
  if (!props.column.dock) return 0.5
  return findSplit(props.column.dock, splitId)?.ratio ?? 0.5
}

function onDividerDown(event: PointerEvent, divider: { splitId: string; dir: 'row' | 'column' }) {
  event.preventDefault()
  isResizing.value = true
  divStartX = event.clientX
  divStartY = event.clientY
  divSplitId = divider.splitId
  divDir = divider.dir
  divInitialRatio = initialSplitRatio(divider.splitId)
  window.addEventListener('pointermove', onDividerMove)
  window.addEventListener('pointerup', onDividerUp)
}
function onDividerMove(event: PointerEvent) {
  if (!divSplitId) return
  const el = containerRef.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  const axis = divDir === 'row' ? rect.width : rect.height
  if (axis <= 0) return
  const delta = divDir === 'row' ? event.clientX - divStartX : event.clientY - divStartY
  const ratio = Math.max(0.1, Math.min(0.9, divInitialRatio + delta / axis))
  wb.bus.dispatch(
    {
      command: {
        type: 'resizeDockSplit',
        scope: wb.scope.value,
        slotId: props.column.slotId,
        splitId: divSplitId,
        ratio,
      },
      source: 'user',
      expectedRevision: wb.snapshot.value.revision,
    },
    { gestureId: `dockresize:${divSplitId}` },
  )
}
function onDividerUp() {
  isResizing.value = false
  divSplitId = null
  window.removeEventListener('pointermove', onDividerMove)
  window.removeEventListener('pointerup', onDividerUp)
}

// ---- Register viewport rects for drop resolution ----
let unsubscribe: (() => void) | null = null
function syncDockRects() {
  const el = containerRef.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  unsubscribe?.()
  unsubscribe = dockDrag.registerDock(props.column.slotId, { x: rect.x, y: rect.y }, props.dock.panes)
}

onMounted(() => {
  syncDockRects()
})
watch(() => props.dock.panes, syncDockRects)
watch(
  () => props.column.dock,
  () => syncDockRects(),
)
onBeforeUnmount(() => {
  unsubscribe?.()
  unsubscribe = null
})
</script>

<template vapor>
  <div
    ref="containerRef"
    class="wb-dock"
    :class="{ 'is-resizing': isResizing }"
  >
    <UieStack
      v-for="view in paneViews"
      :key="view.geometry.paneId"
      :stack="{ stackId: 'primary', tabIds: view.pane?.tabIds ?? [], activeTabId: view.pane?.activeTabId ?? null }"
      :geometry="view.geometry"
      :instances="view.instances"
      :degraded="false"
      :surface-mode="column.surfaceMode"
      :slot-id="column.slotId"
      :pane-id="view.geometry.paneId"
      :pane-main="view.pane?.main === true"
      :can-restore="dock.panes.length > 1"
      :resizing="resizing || isResizing"
    />

    <div
      v-for="divider in dock.dividers"
      :key="divider.splitId"
      class="wb-dock-divider"
      :class="divider.dir === 'row' ? 'wb-dock-divider--row' : 'wb-dock-divider--column'"
      :style="{
        left: `${divider.x}px`,
        top: `${divider.y}px`,
        width: `${divider.width}px`,
        height: `${divider.height}px`,
      }"
      @pointerdown="onDividerDown($event, divider)"
    />

    <!-- Drop highlight: whole pane + zone emphasis via data-zone -->
    <div
      v-if="dropOverlay"
      class="wb-dock-drop-overlay"
      :data-zone="dropOverlay.zone"
      :style="{
        left: `${dropOverlay.x}px`,
        top: `${dropOverlay.y}px`,
        width: `${dropOverlay.width}px`,
        height: `${dropOverlay.height}px`,
      }"
    />
  </div>
</template>

<style scoped>
.wb-dock {
  position: absolute;
  inset: 0;
  overflow: hidden;
}

.wb-dock-divider {
  position: absolute;
  z-index: 25;
  cursor: col-resize;
  background: transparent;
}

.wb-dock-divider--column {
  cursor: row-resize;
}

/* Pill handle, centered — mirrors the column/split pill style. */
.wb-dock-divider::after {
  content: '';
  position: absolute;
  border-radius: 999px;
  background: var(--accent-primary);
  opacity: 0;
  transition: opacity 0.15s ease;
}

.wb-dock-divider--row::after {
  left: 50%;
  top: 50%;
  width: 3px;
  height: 28px;
  transform: translate(-50%, -50%);
}

.wb-dock-divider--column::after {
  left: 50%;
  top: 50%;
  width: 28px;
  height: 3px;
  transform: translate(-50%, -50%);
}

.wb-dock-divider:hover::after,
.wb-dock-divider:active::after {
  opacity: 0.45;
}

/* Drop highlight — translucent fill + zone-emphasized border. */
.wb-dock-drop-overlay {
  position: absolute;
  z-index: 40;
  border-radius: 8px;
  pointer-events: none;
  background: color-mix(in srgb, var(--accent-primary) 18%, transparent);
  box-shadow: inset 0 0 0 1.5px color-mix(in srgb, var(--accent-primary) 65%, transparent);
  transition: left 0.05s, top 0.05s, width 0.05s, height 0.05s;
}

.wb-dock-drop-overlay[data-zone='left'] {
  box-shadow: inset 3px 0 0 0 var(--accent-primary);
}
.wb-dock-drop-overlay[data-zone='right'] {
  box-shadow: inset -3px 0 0 0 var(--accent-primary);
}
.wb-dock-drop-overlay[data-zone='top'] {
  box-shadow: inset 0 3px 0 0 var(--accent-primary);
}
.wb-dock-drop-overlay[data-zone='bottom'] {
  box-shadow: inset 0 -3px 0 0 var(--accent-primary);
}
</style>
