<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import {
  Activity,
  Bot,
  ChevronsLeft,
  GitBranch,
  Globe,
  Layers3,
  ShieldCheck,
  Stethoscope,
  TerminalSquare,
  type LucideIcon,
} from '@lucide/vue'
import UieCardHost from './UieCardHost.vue'
import BrowserTabBar from './BrowserTabBar.vue'
import { useI18n } from 'vue-i18n'
import { useUie } from './useUie'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useResponsiveMode, useTabLabelMode } from '@/composables/useElementSize'
import { useDockDrag, type DockDropTarget } from '@/composables/useDockDrag'
import { dropZoneToSplit } from '../engine/dockDrop'
import {
  descriptorForDetachedType,
  useDetachedTabs,
} from '@/composables/useDetachedTabs'
import type {
  PersistedCardInstance,
  StackGeometry,
  SurfaceMode,
  UieSlotId,
  UieStack as StackModel,
} from '../engine/types'

const props = defineProps<{
  stack: StackModel
  geometry: StackGeometry
  /** Card instances in this stack, ordered by tabIds. */
  instances: PersistedCardInstance[]
  /** Whether this stack is a degraded split (visual single stack). */
  degraded?: boolean
  /** Column surface mode — float panels vs connected app layout. */
  surfaceMode?: SurfaceMode
  /** Owning column slot (used for collapse/resize of the feature panel). */
  slotId?: UieSlotId
  /** True while the owning column is being dragged (disables width transition). */
  resizing?: boolean
  /** Dock pane id — set when this stack renders a dock pane (split panel). */
  paneId?: string
  /** True when this pane is the dock's main pane (hosts the collapse button). */
  paneMain?: boolean
  /** Show the "restore single panel" action (dock has >1 panes). */
  canRestore?: boolean
}>()

const wb = useUie()
const { t } = useI18n()

// The stack is a single material root (like the old ContextPanel / .sidebar /
// .conversation). Tab bar and content share one continuous surface.
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()

// Immersive stacks (Home chat column) are transparent: the root carries no
// background/backdrop so the page background shows through, but it still keeps
// the data-panel-effect attribute so inner objects (composer, welcome dialog,
// bubbles) inherit the remapped --surface-* tokens and follow the global
// material. Keep the --material-filter-* vars for blur-glass inner surfaces.
const materialStyle = computed(() => {
  const style = getPanelStyle()
  if (props.surfaceMode === 'immersive') {
    const { backgroundColor, backdropFilter, WebkitBackdropFilter, ...rest } = style
    return rest
  }
  return style
})
const materialAttrs = computed(() => getPanelDataAttributes())

// The feature panel is the stack that hosts the pinned homePicker card (the
// Home grid). It gets the full browser-style tab chrome from 2b4377c7's
// ContextPanel; all other stacks keep the minimal tab bar.
const isFeaturePanel = computed(() =>
  props.instances.some((i) => i.descriptorId === 'homePicker'),
)
const homeInstance = computed(
  () => props.instances.find((i) => i.descriptorId === 'homePicker') ?? null,
)

// Main dock pane (or a plain feature stack) gets the full browser tab chrome;
// split panes get the minimal bar + a merge button, shown even for a single
// card so the pane stays mergeable and drag-splittable.
const isMainPane = computed(() => props.paneMain === true || isFeaturePanel.value)
const isSplitPane = computed(() => !!props.paneId && !isMainPane.value)
const showTabBar = computed(() => isMainPane.value || isSplitPane.value || props.instances.length > 1)

// ---- Feature-panel responsive modes (ported from ContextPanel) ----
const panelRef = ref<HTMLElement | null>(null)
const { mode: responsiveMode, isCompact } = useResponsiveMode(panelRef)

const detached = useDetachedTabs()
const openTabCount = computed(
  () => props.instances.filter((i) => i.descriptorId !== 'homePicker').length,
)
const detachedCount = computed(() => detached.detachedTabs.value.length)
const tabLabelMode = useTabLabelMode(
  computed(() => props.geometry.width),
  openTabCount,
  detachedCount,
)

const stackClass = computed(() => ({
  'wb-stack--app': props.surfaceMode === 'app',
  'wb-stack--immersive': props.surfaceMode === 'immersive',
  'wb-stack--resizing': !!props.resizing,
  // Responsive classes mirror the legacy .float-panel.mode-* / .tab-labels-*
  // hooks; styles.css has matching .wb-stack selectors.
  'mode-compact': isFeaturePanel.value && isCompact.value,
  'mode-ultra': isFeaturePanel.value && responsiveMode.value === 'ultra',
  'tab-labels-hidden': isFeaturePanel.value && tabLabelMode.value === 'hidden',
  'tab-labels-active-only': isFeaturePanel.value && tabLabelMode.value === 'active-only',
}))

// ---- Tab icons (descriptorId -> icon; 'preview' matches the detached type) ----
const FEATURE_ICONS: Record<string, LucideIcon> = {
  agent: Bot,
  terminal: TerminalSquare,
  git: GitBranch,
  approval: ShieldCheck,
  orchestration: Layers3,
  browser: Globe,
  preview: Globe,
  events: Activity,
  doctor: Stethoscope,
}

function iconFor(descriptorId: string): LucideIcon {
  return FEATURE_ICONS[descriptorId] ?? Globe
}

// ---- Dispatch helpers ----
function activate(instanceId: string) {
  wb.bus.dispatch({
    command: { type: 'activateCard', scope: wb.scope.value, instanceId },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}

function close(instanceId: string) {
  wb.bus.dispatch({
    command: { type: 'closeCard', scope: wb.scope.value, instanceId },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}

function goHome() {
  const home = homeInstance.value
  if (home) activate(home.id)
}

function collapse() {
  if (!props.slotId) return
  wb.bus.dispatch({
    command: {
      type: 'collapseColumn',
      scope: wb.scope.value,
      slotId: props.slotId,
      collapsed: true,
    },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}

function focusDetached(windowId: number) {
  detached.focus(windowId)
}

/**
 * Open a feature page from the "+" new-tab menu. The reducer handles
 * singleton dedup (git/approval/orchestration/events/doctor/agent reuse the
 * open tab and just activate it); browser/terminal allow multiple instances.
 */
function openFeature(descriptorId: string) {
  wb.bus.dispatch({
    command: {
      type: 'openCard',
      scope: wb.scope.value,
      descriptorId,
      slotId: props.slotId,
    },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}

// ---- Dock drag source (shared with BrowserTabBar + split panes) ----
const dockDrag = useDockDrag()

/**
 * Start an in-window dock drag from a tab. Only the main pane supports
 * floating-window detach; split panes stay in-window (split/merge only).
 */
function startDockDrag(event: MouseEvent, instance: PersistedCardInstance) {
  if (event.button !== 0) return
  if (instance.id === homeInstance.value?.id) return
  const target = event.target as HTMLElement
  if (target.closest('.browser-tab-close') || target.closest('.browser-tab-detach')) return
  dockDrag.startDrag(
    instance,
    {
      slotId: props.slotId ?? 'right',
      sourcePaneId: props.paneId ?? null,
      onDetach: isMainPane.value ? (tabId) => detachTab(tabId) : () => {},
      onEnd: (target_, tabId) => {
        if (target_) handleDockDrop(target_, tabId)
      },
    },
    { x: event.clientX, y: event.clientY },
  )
}

/** Commit a dock drop: center → merge tab into pane, edge → split the pane. */
function handleDockDrop(target: DockDropTarget, tabId: string) {
  if (target.zone === 'center') {
    wb.bus.dispatch({
      command: {
        type: 'moveCardToDockPane',
        scope: wb.scope.value,
        instanceId: tabId,
        toPaneId: target.paneId,
      },
      source: 'user',
      expectedRevision: wb.snapshot.value.revision,
    })
    return
  }
  const { dir, place } = dropZoneToSplit(target.zone)
  wb.bus.dispatch({
    command: {
      type: 'splitDockPane',
      scope: wb.scope.value,
      slotId: props.slotId ?? 'right',
      paneId: target.paneId,
      dir,
      place,
      instanceId: tabId,
    },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}

/** Merge this split pane back into the main pane. */
function mergeIntoMain() {
  if (!props.slotId || !props.paneId) return
  wb.bus.dispatch({
    command: {
      type: 'mergeDockPane',
      scope: wb.scope.value,
      slotId: props.slotId,
      paneId: props.paneId,
    },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}

/** Restore the whole dock back to a single stack (from the main pane). */
function restoreDock() {
  if (!props.slotId) return
  wb.bus.dispatch({
    command: {
      type: 'mergeDockColumn',
      scope: wb.scope.value,
      slotId: props.slotId,
    },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}

/** Detach a card into a floating window, then remove its tab from the stack. */
async function detachTab(instanceId: string) {
  const inst = props.instances.find((i) => i.id === instanceId)
  if (!inst) return
  const ok = await detached.detach(inst, inst.state)
  if (ok) close(instanceId)
}

// ---- Reattach from a floating window back into the stack ----
let unsubscribeDetached: (() => void) | null = null

onMounted(() => {
  if (!isFeaturePanel.value) return
  unsubscribeDetached = detached.bind((data) => {
    wb.bus.dispatch({
      command: {
        type: 'openCard',
        scope: wb.scope.value,
        descriptorId: descriptorForDetachedType(data.type),
        slotId: props.slotId,
        title: data.title,
        state: data.state,
        instanceId: data.tabId,
      },
      source: 'user',
      expectedRevision: wb.snapshot.value.revision,
    })
  })
})

onUnmounted(() => {
  unsubscribeDetached?.()
  unsubscribeDetached = null
})
</script>

<template vapor>
  <div
    ref="panelRef"
    class="wb-stack"
    :class="stackClass"
    :style="{
      left: `${geometry.x}px`,
      top: `${geometry.y}px`,
      width: `${geometry.width}px`,
      height: `${geometry.height}px`,
      ...materialStyle,
    }"
    v-bind="materialAttrs"
  >
    <!-- Feature panel / main dock pane: full browser-style tab chrome. -->
    <BrowserTabBar
      v-if="isMainPane && showTabBar"
      :instances="instances"
      :active-tab-id="stack.activeTabId"
      :icon-for="iconFor"
      :home-instance="homeInstance"
      :slot-id="slotId"
      :pane-id="paneId"
      :can-restore="canRestore"
      @home="goHome"
      @activate="activate"
      @close="close"
      @detach="detachTab"
      @collapse="collapse"
      @focus-detached="focusDetached"
      @open-panel="openFeature"
      @dock-drop="handleDockDrop"
      @restore-dock="restoreDock"
    />

    <!-- Split dock pane: minimal browser-style tab bar + merge-into-main -->
    <div v-else-if="isSplitPane && showTabBar" class="browser-tab-bar wb-stack-tabbar">
      <button
        v-for="inst in instances"
        :key="inst.id"
        class="browser-tab"
        :class="{ active: stack.activeTabId === inst.id, 'tab-dragging': dockDrag.isDraggingTab(inst.id) }"
        :title="inst.title"
        @click="activate(inst.id)"
        @mousedown="startDockDrag($event, inst)"
      >
        <span class="browser-tab-label">{{ inst.title }}</span>
        <span
          v-if="inst.id !== stack.activeTabId"
          class="browser-tab-close"
          @click.stop="close(inst.id)"
        >
          ×
        </span>
      </button>
      <button
        class="wb-dock-merge-btn"
        :title="t('context.mergePanel')"
        @click="mergeIntoMain"
      >
        <ChevronsLeft :size="12" />
      </button>
    </div>

    <!-- Other stacks: minimal browser-style tab bar for multi-card stacks -->
    <div v-else-if="showTabBar" class="browser-tab-bar wb-stack-tabbar">
      <button
        v-for="inst in instances"
        :key="inst.id"
        class="browser-tab"
        :class="{ active: stack.activeTabId === inst.id }"
        :title="inst.title"
        @click="activate(inst.id)"
      >
        <span class="browser-tab-label">{{ inst.title }}</span>
        <span
          v-if="inst.id !== stack.activeTabId"
          class="browser-tab-close"
          @click.stop="close(inst.id)"
        >
          ×
        </span>
      </button>
    </div>

    <!-- Card hosts — all mounted, visibility toggles only -->
    <div class="wb-stack-body">
      <UieCardHost
        v-for="inst in instances"
        :key="inst.id"
        :instance="inst"
        :active="stack.activeTabId === inst.id"
        :surface-mode="surfaceMode"
      />
    </div>
  </div>
</template>

<style scoped>
.wb-stack {
  position: absolute;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  transition: left 0.25s cubic-bezier(0.2, 0, 0, 1), width 0.25s cubic-bezier(0.2, 0, 0, 1);
}

/* While the column is being dragged, snap geometry (matches .float-panel.resizing). */
.wb-stack--resizing {
  transition: none !important;
}

/* Float-panel look (Left/Right): rounded, background + subtle border for island-style elevation */
.wb-stack:not(.wb-stack--app):not(.wb-stack--immersive) {
  background: var(--surface-section);
  border: 1px solid var(--border-card);
  border-radius: 12px;
  /* Conditional shadow: only when surface mode is not immersive */
  box-shadow: var(--shadow-card-subtle);
  transition: box-shadow 0.2s ease;
}

/* Hover elevation for floating panels */
.wb-stack:not(.wb-stack--app):not(.wb-stack--immersive):hover {
  box-shadow: var(--shadow-card-hover);
}

/* Connected app look (Market/Code): no rounding, no border, continuous surface. */
.wb-stack--app {
  background: var(--bg-primary);
  border: none;
  border-radius: 0;
  box-shadow: none;
}

/* Immersive zone (Home chat center column): transparent so page background shows through.
   User's global material setting controls backdrop-filter on inner objects.
   NO background color here — the conversation area is truly invisible. */
.wb-stack--immersive {
  background: transparent !important;
  border: none !important;
  border-radius: 0 !important;
  box-shadow: none !important;
}

.wb-stack-tabbar {
  flex-shrink: 0;
}

/* Merge-into-main button on split panes — compact icon matching the tab bar. */
.wb-dock-merge-btn {
  display: grid;
  place-items: center;
  align-self: center;
  width: 22px;
  height: 22px;
  margin-left: 2px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.wb-dock-merge-btn:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.wb-stack-body {
  flex: 1;
  min-height: 0;
  position: relative;
  overflow: hidden;
}
</style>
