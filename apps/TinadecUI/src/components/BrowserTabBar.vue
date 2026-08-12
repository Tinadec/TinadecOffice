<script setup lang="ts">
// BrowserTabBar — the browser-style tab chrome of the feature panel (the UIE
// right-column stack). This is the faithful port of the old ContextPanel tab
// bar UI/UX:
//   - a pinned Home tab (homePicker card),
//   - one tab per open feature card, with hover-revealed detach/close icons,
//   - dashed detached-window indicator tabs that focus the floating window,
//   - a `+` add tab that opens a dropdown menu listing every openable page
//     (like a browser's new-tab menu) instead of jumping straight Home,
//   - a collapse button,
//   - Chrome-style drag-to-detach (tab tearing) with Electron cursor polling.
//
// Shared styles come from apps/desktop/src/styles.css (.browser-tab-*).
import { computed, nextTick, onMounted, onUnmounted, ref } from 'vue'
import {
  ChevronsLeft,
  ExternalLink,
  Home as HomeIcon,
  PanelRightClose,
  Plus,
  X,
  type LucideIcon,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { useDetachedTabs } from '@/composables/useDetachedTabs'
import { useDockDrag, type DockDropTarget } from '@/composables/useDockDrag'
import { FEATURE_CATALOG } from './cards/home/featureCatalog'
import type { PersistedCardInstance, UieSlotId } from '../engine/types'

const { t } = useI18n()

const props = defineProps<{
  /** Card instances in the stack, including the pinned homePicker. */
  instances: PersistedCardInstance[]
  activeTabId: string | null
  /** DescriptorId (or detached-window type) -> icon for per-tab icons. */
  iconFor: (descriptorId: string) => LucideIcon
  /** The pinned home card instance (tab 0). */
  homeInstance: PersistedCardInstance | null
  /** Owning column slot (drag source for dock splits). */
  slotId?: UieSlotId
  /** Dock pane id (drag source origin; main pane id on the feature panel). */
  paneId?: string
  /** Show the "restore single panel" button (dock has >1 panes). */
  canRestore?: boolean
}>()

const emit = defineEmits<{
  home: []
  activate: [instanceId: string]
  close: [instanceId: string]
  detach: [instanceId: string]
  collapse: []
  'focus-detached': [windowId: number]
  /** Open a feature page from the "+" new-tab menu. */
  'open-panel': [descriptorId: string]
  /** A dock drop was committed by the shared drag state. */
  'dock-drop': [target: DockDropTarget, tabId: string]
  /** Restore the whole dock back to a single panel. */
  'restore-dock': []
}>()

const detached = useDetachedTabs()
const dockDrag = useDockDrag()

const featureInstances = computed(() =>
  props.instances.filter((i) => i.id !== props.homeInstance?.id),
)
const detachedTabs = computed(() => detached.detachedTabs.value)

// ---- "+" new-tab dropdown menu ----
const menuOpen = ref(false)
const addButtonRef = ref<HTMLElement | null>(null)
const menuStyle = ref<Record<string, string>>({})

function toggleMenu() {
  if (menuOpen.value) {
    closeMenu()
  } else {
    openMenu()
  }
}

function openMenu() {
  menuOpen.value = true
  // Measure after the DOM slot for the menu has flushed so getBoundingClientRect
  // is not recomputed against a stale layout; the fixed position is computed
  // from the trigger button's viewport rect and never needs to be static.
  void nextTick(() => {
    const rect = addButtonRef.value?.getBoundingClientRect()
    if (!rect) return
    menuStyle.value = {
      top: `${rect.bottom + 6}px`,
      right: `${Math.max(4, window.innerWidth - rect.right)}px`,
    }
  })
}

function closeMenu() {
  menuOpen.value = false
}

function onSelect(descriptorId: string) {
  closeMenu()
  emit('open-panel', descriptorId)
}

function onDocPointerDown(event: MouseEvent) {
  if (!menuOpen.value) return
  const target = event.target as HTMLElement
  if (target.closest('.browser-tab-add') || target.closest('.feature-panel-menu')) return
  closeMenu()
}

function onDocKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') closeMenu()
}

function onDocResize() {
  closeMenu()
}

onMounted(() => {
  document.addEventListener('pointerdown', onDocPointerDown)
  document.addEventListener('keydown', onDocKeydown)
  window.addEventListener('resize', onDocResize)
})

// ---- Drag-to-split / drag-to-detach (shared dock drag state) ----
// The shared useDockDrag owns the 30px threshold, in-window drop target
// computation, and the Electron cursor polling that detaches a floating window
// when the cursor leaves the main window. The tab bar only seeds the drag and
// forwards the outcome (detach → floating window; dock drop → split/merge).
function onTabMouseDown(event: MouseEvent, instance: PersistedCardInstance) {
  // Only closable (non-pinned) cards can be torn out.
  if (instance.id === props.homeInstance?.id) return
  if (event.button !== 0) return
  // Don't start drag tracking when clicking the close/detach buttons.
  const target = event.target as HTMLElement
  if (target.closest('.browser-tab-close') || target.closest('.browser-tab-detach')) return

  dockDrag.startDrag(
    instance,
    {
      slotId: props.slotId ?? 'right',
      sourcePaneId: props.paneId ?? null,
      onDetach: (tabId) => emit('detach', tabId),
      onEnd: (target_, tabId) => {
        if (target_) emit('dock-drop', target_, tabId)
      },
    },
    { x: event.clientX, y: event.clientY },
  )
}

/** Right-click context menu on tabs for detach option. */
function onTabContextMenu(event: MouseEvent, instance: PersistedCardInstance) {
  event.preventDefault()
  event.stopPropagation()
  if (instance.id === props.homeInstance?.id) return
  emit('detach', instance.id)
}

function onTabClick(event: MouseEvent, instanceId: string) {
  // Suppress activation when the drag threshold was crossed (click follows drag).
  if (dockDrag.isDraggingTab(instanceId)) return
  emit('activate', instanceId)
}

onUnmounted(() => {
  document.removeEventListener('pointerdown', onDocPointerDown)
  document.removeEventListener('keydown', onDocKeydown)
  window.removeEventListener('resize', onDocResize)
  dockDrag.cancel()
})
</script>

<template vapor>
  <div class="browser-tab-bar wb-stack-tabbar">
    <!-- Pinned Home tab -->
    <button
      class="browser-tab browser-tab-home"
      :class="{ active: activeTabId === homeInstance?.id }"
      :title="t('context.homeTitle')"
      @click="emit('home')"
    >
      <HomeIcon :size="14" />
      <span class="browser-tab-label">{{ t('context.homeTabLabel') }}</span>
    </button>

    <!-- Open feature tabs -->
    <button
      v-for="inst in featureInstances"
      :key="inst.id"
      class="browser-tab"
      :class="{ active: activeTabId === inst.id, 'tab-dragging': dockDrag.isDraggingTab(inst.id) }"
      :title="inst.title"
      @click="onTabClick($event, inst.id)"
      @mousedown="onTabMouseDown($event, inst)"
      @contextmenu="onTabContextMenu($event, inst)"
    >
      <component :is="iconFor(inst.descriptorId)" :size="14" class="browser-tab-icon" />
      <span class="browser-tab-label">{{ inst.title }}</span>
      <span
        class="browser-tab-detach"
        :title="t('context.detachTab')"
        @click.stop="emit('detach', inst.id)"
      >
        <ExternalLink :size="11" />
      </span>
      <span
        class="browser-tab-close"
        :title="t('app.close')"
        @click.stop="emit('close', inst.id)"
      >
        <X :size="11" />
      </span>
    </button>

    <!-- Detached window indicators (click to focus the floating window) -->
    <button
      v-for="dt in detachedTabs"
      :key="dt.tabId"
      class="browser-tab browser-tab-detached"
      :title="t('context.detachedTabHint', { title: dt.title })"
      @click="emit('focus-detached', dt.windowId)"
    >
      <component :is="iconFor(dt.type)" :size="14" class="browser-tab-icon" />
      <span class="browser-tab-label">{{ dt.title }}</span>
      <ExternalLink :size="10" class="browser-tab-detached-icon" />
    </button>

    <!-- Add tab: opens the new-tab dropdown (no longer goes straight Home) -->
    <button
      ref="addButtonRef"
      class="browser-tab-add"
      :class="{ active: menuOpen }"
      :title="t('context.homeNewTab')"
      :aria-haspopup="'menu'"
      :aria-expanded="menuOpen"
      @click="toggleMenu"
    >
      <Plus :size="14" />
    </button>

    <!-- Restore single panel (dock has more than one pane) -->
    <button
      v-if="canRestore"
      class="browser-tab-collapse"
      :title="t('context.restoreDock')"
      @click="emit('restore-dock')"
    >
      <ChevronsLeft :size="14" />
    </button>

    <!-- Collapse button -->
    <button
      class="browser-tab-collapse"
      :title="t('app.collapse')"
      @click="emit('collapse')"
    >
      <PanelRightClose :size="14" />
    </button>

    <!-- New-tab dropdown: teleported to body so the panel's overflow clipping
         never cuts it off; position:fixed right-aligned to the + button.
         Compact list — each item is [small monochrome icon] + [title] only. -->
    <Teleport to="body">
      <div
        v-if="menuOpen"
        class="feature-panel-menu"
        role="menu"
        :style="menuStyle"
      >
        <button
          v-for="feature in FEATURE_CATALOG"
          :key="feature.descriptorId"
          class="feature-panel-menu-item"
          role="menuitem"
          @click="onSelect(feature.descriptorId)"
        >
          <component :is="feature.icon" :size="14" class="feature-panel-menu-icon" />
          <span class="feature-panel-menu-title">{{ t(feature.titleKey) }}</span>
        </button>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
/* All visual styles come from the shared .browser-tab-* rules in styles.css.
   The component only needs to be a flex-shrink-safe strip. */
.wb-stack-tabbar {
  flex-shrink: 0;
}

/* Highlight the + button while its menu is open. */
.browser-tab-add.active {
  color: var(--text-primary);
  background: var(--surface-hover);
}

/* New-tab dropdown menu — compact, styled with the shared surface tokens so
   it matches the panel material (opaque/translucent/blur). Teleported to body,
   so these scoped rules still apply (the element keeps its data-v scoping
   attribute). */
.feature-panel-menu {
  position: fixed;
  z-index: 200;
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 160px;
  max-width: 220px;
  padding: 4px;
  background: var(--surface-raised);
  border: 1px solid var(--border-card);
  border-radius: 8px;
  box-shadow: var(--shadow-panel);
}

.feature-panel-menu-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 5px 8px;
  text-align: left;
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.feature-panel-menu-item:hover {
  background: var(--surface-hover);
}

/* Single-color small icon matching the tab bar: inherits the muted text color,
   no background tile. */
.feature-panel-menu-icon {
  flex-shrink: 0;
  color: var(--text-secondary);
}

.feature-panel-menu-item:hover .feature-panel-menu-icon {
  color: var(--text-primary);
}

.feature-panel-menu-title {
  min-width: 0;
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
</style>
