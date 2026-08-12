<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Home as HomeIcon, PanelRightOpen, type LucideIcon } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import UieStack from './UieStack.vue'
import UieDock from './UieDock.vue'
import { useUie } from './useUie'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useDockDrag } from '@/composables/useDockDrag'
import { FEATURE_CATALOG } from './cards/home/featureCatalog'
import { collectDockPanes, collectDockTabIds } from '../engine/reducer'
import { maxOverlayColumnWidth } from '../engine/constraints'
import type {
  ColumnGeometry,
  PersistedCardInstance,
  SplitGeometry,
  UieColumn as ColumnModel,
  UieDockGeometry,
} from '../engine/types'
import { COLLAPSED_COLUMN_WIDTH } from '../engine/types'

const props = defineProps<{
  column: ColumnModel
  geometry: ColumnGeometry
  split?: SplitGeometry
  dock?: UieDockGeometry
}>()

const { t } = useI18n()
const wb = useUie()

/** All tab instanceIds in this column — dock panes first, else the primary stack. */
const columnTabIds = computed<string[]>(() => {
  if (props.column.dock) return collectDockTabIds(props.column.dock)
  return props.column.primary.tabIds
})

// The feature panel is the column that hosts the pinned homePicker card.
// It gets the old ContextPanel treatment: browser tab chrome (in UieStack) and
// a 44px collapsed rail with expand + Home buttons + the open feature-tab icons.
const isFeatureColumn = computed(() =>
  columnTabIds.value.some((id) => wb.snapshot.value.cards[id]?.descriptorId === 'homePicker'),
)
const featureHomeId = computed(
  () =>
    columnTabIds.value.find((id) => wb.snapshot.value.cards[id]?.descriptorId === 'homePicker') ??
    null,
)
const isHomeActive = computed(() => {
  const home = featureHomeId.value
  if (home === null) return false
  if (props.column.dock) {
    // Home lives in the main pane; active when the main pane shows it.
    const panes = collectDockPanes(props.column.dock)
    const main = panes.find((p) => p.main)
    return main?.activeTabId === home
  }
  return props.column.primary.activeTabId === home
})

// True when the constraint solver visually collapsed this column (the window is
// too narrow to fit it at its persisted width) but the user did NOT collapse it
// in the layout. The feature panel then renders the 44px icon rail instead of a
// blank strip — same as a real collapse, so the panel never "disappears".
const visualCollapsed = computed(
  () => !props.column.collapsed && props.geometry.width <= COLLAPSED_COLUMN_WIDTH,
)

// Open feature tabs (everything in the column except the pinned homePicker),
// used to render the vertical icon rail while the panel is collapsed.
const openFeatureInstances = computed<PersistedCardInstance[]>(() =>
  columnTabIds.value
    .map((id) => wb.snapshot.value.cards[id])
    .filter((c): c is PersistedCardInstance => !!c && c.descriptorId !== 'homePicker'),
)
function isFeatureActive(instanceId: string): boolean {
  if (props.column.dock) {
    const panes = collectDockPanes(props.column.dock)
    return panes.some((p) => p.activeTabId === instanceId)
  }
  return props.column.primary.activeTabId === instanceId
}

// Icon lookup reuses the shared feature catalog so the collapsed rail matches
// the tab bar and the "+" menu (same single-color icons).
const FEATURE_ICON_BY_DESCRIPTOR = new Map(FEATURE_CATALOG.map((f) => [f.descriptorId, f.icon]))
function featureIconFor(descriptorId: string): LucideIcon {
  return FEATURE_ICON_BY_DESCRIPTOR.get(descriptorId) ?? HomeIcon
}

// Collapsed rail shares the float-panel material (translucent/blur follow the
// global panel-style setting just like the stack they replace).
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const railStyle = computed(() => getPanelStyle())
const railAttrs = computed(() => getPanelDataAttributes())

/**
 * Expand the collapsed panel. When `instanceId` is given, also activate that
 * card (used by the rail's feature icons and the Home button).
 */
function expand(instanceId?: string | null) {
  // Visually collapsed (window too narrow, or the persisted width overflows the
  // window) but NOT user-collapsed: if the persisted width is too wide for the
  // window, shrink it to the largest fitting width first so the panel can
  // actually reopen. When the window itself is the constraint the fit clamps to
  // 280 and the solver keeps the rail — correct, the panel simply doesn't fit.
  if (!props.column.collapsed && visualCollapsed.value && props.column.width > COLLAPSED_COLUMN_WIDTH) {
    const fit = dockFitWidth.value
    if (fit !== null && fit < props.column.width) resizeColumn(fit)
  }
  wb.bus.dispatch({
    command: { type: 'collapseColumn', scope: wb.scope.value, slotId: props.column.slotId, collapsed: false },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
  if (instanceId) {
    wb.bus.dispatch({
      command: { type: 'activateCard', scope: wb.scope.value, instanceId },
      source: 'user',
      expectedRevision: wb.snapshot.value.revision,
    })
  }
}

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

// Feature-panel width limits match the legacy ContextPanel (280–760px); other
// columns keep the reducer's global clamp (160–1200px).
// A float feature column's drag ceiling is the widest it can be while leaving
// at least MIN_OVERLAY_STRIP of the chat visible when it overlays (the solver
// floats the panel over the chat instead of squeezing the composer).
const dockFitWidth = computed<number | null>(() =>
  isFeatureColumn.value && props.column.surfaceMode === 'float'
    ? Math.max(280, maxOverlayColumnWidth(wb.containerSize.value, wb.snapshot.value))
    : null,
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
  const maxW = isFeatureColumn.value ? (dockFitWidth.value ?? 760) : 1200
  const minW = isFeatureColumn.value ? 280 : 160
  resizeColumn(Math.min(maxW, Math.max(minW, newWidth)))
}
function onResizeUp() {
  isResizing.value = false
  window.removeEventListener('pointermove', onResizeMove)
  window.removeEventListener('pointerup', onResizeUp)
}

// ---- Virtual main-pane drop rect (pre-dock) ----
// Before the first split there is no UieDock to register pane rects, so the
// drag target resolver has nothing to hit. Register the whole feature column
// as a single virtual main pane; the first edge drop then splits it.
const dockDrag = useDockDrag()
const colRef = ref<HTMLElement | null>(null)
let virtualUnsub: (() => void) | null = null

function syncVirtualPane() {
  virtualUnsub?.()
  virtualUnsub = null
  if (props.column.dock || !isFeatureColumn.value) return
  const el = colRef.value
  if (!el) return
  const rect = el.getBoundingClientRect()
  virtualUnsub = dockDrag.registerDock(props.column.slotId, { x: rect.x, y: rect.y }, [
    {
      paneId: 'virtual-main',
      x: 0,
      y: 0,
      width: props.geometry.width,
      height: props.geometry.height,
      degraded: false,
    },
  ])
}

onMounted(() => syncVirtualPane())
watch(
  () => [props.column.dock, props.geometry.width, props.geometry.height, isFeatureColumn.value],
  syncVirtualPane,
)
onBeforeUnmount(() => {
  virtualUnsub?.()
  virtualUnsub = null
})

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
    ref="colRef"
    class="wb-column"
    :class="{ 'is-resizing': isResizing, 'wb-column--overlay': geometry.overlay }"
    :style="{
      left: `${geometry.x}px`,
      top: `${geometry.y}px`,
      width: `${geometry.width}px`,
      height: `${geometry.height}px`,
    }"
  >
    <!-- Collapsed feature panel: 44px rail (expand + Home + open feature-tab
         icons), same material as the stack. Shows both for a real user
         collapse and for a visual (solver-forced) collapse — without this the
         dock column would render an empty pane list and blank out. -->
    <div
      v-if="isFeatureColumn && (column.collapsed || visualCollapsed)"
      class="float-panel-collapsed-bar wb-column-rail"
      :style="railStyle"
      v-bind="railAttrs"
    >
      <button
        class="float-panel-toggle-btn"
        :title="t('app.expand')"
        @click="expand()"
      >
        <PanelRightOpen :size="16" />
      </button>
      <button
        class="float-panel-collapsed-icon"
        :class="{ active: isHomeActive }"
        :title="t('context.homeTitle')"
        @click="expand(featureHomeId)"
      >
        <HomeIcon :size="18" />
      </button>
      <!-- Open feature-tab icons: icon-only, vertical; clicking activates the
           tab and expands the panel. -->
      <button
        v-for="inst in openFeatureInstances"
        :key="inst.id"
        class="float-panel-collapsed-icon"
        :class="{ active: isFeatureActive(inst.id) }"
        :title="inst.title"
        @click="expand(inst.id)"
      >
        <component :is="featureIconFor(inst.descriptorId)" :size="16" />
      </button>
    </div>

    <template v-else>
      <!-- Resizer handle: Left column resizes via right edge, Right column resizes via left edge -->
      <div
        v-if="column.slotId === 'left' || column.slotId === 'right'"
        class="wb-column-resizer"
        :class="column.slotId === 'right' ? 'wb-column-resizer-left' : 'wb-column-resizer-right'"
        @pointerdown="onResizeDown"
      />

      <!-- Dock (multi-pane split) column: panes + dividers + drop overlay -->
      <UieDock
        v-if="column.dock && dock"
        :column="column"
        :geometry="geometry"
        :dock="dock"
        :resizing="isResizing"
      />

      <!-- Plain stacks: primary + optional secondary -->
      <template v-else>
        <!-- Primary stack -->
        <UieStack
          :stack="column.primary"
          :geometry="primaryGeometry"
          :instances="primaryInstances"
          :degraded="primaryDegraded"
          :surface-mode="column.surfaceMode"
          :slot-id="column.slotId"
          :resizing="isResizing"
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
            :slot-id="column.slotId"
            :resizing="isResizing"
          />
        </template>
      </template>
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

/* Window-stacking overlay: a very wide right feature panel floats over the chat
   (raised above the other columns) so the composer keeps its comfortable width.
   The float stack inside already carries the panel border/shadow; a leftward
   drop shadow makes the overlap read as stacked windows. */
.wb-column--overlay {
  z-index: 20;
}

.wb-column--overlay .wb-stack {
  box-shadow: -8px 0 16px -12px rgba(0, 0, 0, 0.4), var(--shadow-card-subtle);
}

/* Collapsed feature panel rail: carries the same island material as the stack
   it replaces (rounded, bordered, shadowed). Buttons use the shared
   .float-panel-* rules from styles.css. Vertical scrolling lets the icon rail
   grow past the rail height when many tabs are open. */
.wb-column-rail {
  width: 100%;
  height: 100%;
  background: var(--surface-section);
  border: 1px solid var(--border-card);
  border-radius: 12px;
  box-shadow: var(--shadow-card-subtle);
  overflow-y: auto;
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
