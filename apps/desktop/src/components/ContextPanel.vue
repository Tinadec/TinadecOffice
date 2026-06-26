<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ExternalLink, Home as HomeIcon, PanelRightClose, PanelRightOpen, Plus, X, type LucideIcon } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import ApprovalTab from './ApprovalTab.vue'
import GitPanel from './GitPanel.vue'
import EventsTab from './EventsTab.vue'
import DoctorTab from './DoctorTab.vue'
import OrchestrationTab from './OrchestrationTab.vue'
import PanelHome from './PanelHome.vue'
import PreviewBrowserPanel from './PreviewBrowserPanel.vue'
import AgentActivityPanel from './AgentActivityPanel.vue'
import { usePanelTabs, panelIcons, type PanelType, type PanelTab } from '../composables/usePanelTabs'
import { useResponsiveMode, useTabLabelMode } from '../composables/useElementSize'
import type { ApprovalDto, EventEnvelope, DoctorReportDto, OrchestrationSnapshotDto, RuntimeReadinessReceiptDto, ToolExecutionTimelineItemDto } from '../api'
import type { AgentActivity, AgentState, ThinkingStep, ProgressEvent } from '@/composables/useAgentActivity'

const { t } = useI18n()

const collapsed = defineModel<boolean>('collapsed', { default: false })
const panelWidth = defineModel<number>('width', { default: 420 })

const isResizing = ref(false)

const props = defineProps<{
  approvals: ApprovalDto[]
  events: EventEnvelope[]
  doctor: DoctorReportDto | null
  readiness: RuntimeReadinessReceiptDto | null
  orchestration: OrchestrationSnapshotDto | null
  toolExecutions: ToolExecutionTimelineItemDto[]
  shellCommand: string
  busy: boolean
  selectedSessionId: string | null
  currentProjectPath: string | undefined
  /** Agent activity data for the agent panel */
  agentActivity: AgentActivity
  agentStates: Record<string, AgentState>
  thinkingSteps: ThinkingStep[]
  progressEvents: ProgressEvent[]
}>()

const emit = defineEmits<{
  'request-approval': []
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected']
  'approval-created': [approval: ApprovalDto]
  'update:shellCommand': [value: string]
}>()

const {
  tabs,
  activeTabId,
  openPanel,
  closeTab,
  selectTab,
  goHome,
  detachTab,
  reattachTab,
  removeDetachedTab,
  detachedTabs,
} = usePanelTabs()

// ---- Responsive mode detection ----
const panelRef = ref<HTMLElement | null>(null)
const { mode: responsiveMode, isCompact } = useResponsiveMode(panelRef)

// ---- Smart tab label visibility ----
// Instead of hiding ALL labels at a fixed breakpoint, dynamically calculate
// whether there is enough space for labels based on actual tab count.
const openTabCount = computed(() => tabs.value.filter((t) => t.type !== 'home').length)
const detachedCount = computed(() => detachedTabs.value.length)
const tabLabelMode = useTabLabelMode(panelWidth, openTabCount, detachedCount)

const panelClass = computed(() => ({
  collapsed,
  resizing: isResizing.value,
  // mode-compact controls padding/grid/toolbar adjustments, NOT tab labels
  'mode-compact': isCompact.value,
  'mode-ultra': responsiveMode.value === 'ultra',
  // Tab label visibility is independently controlled by tabLabelMode
  'tab-labels-hidden': tabLabelMode.value === 'hidden',
  'tab-labels-active-only': tabLabelMode.value === 'active-only',
}))

const pendingApprovalCount = computed(() =>
  props.approvals?.filter((a) => a.status === 'pending').length ?? 0,
)

function handleOpenPanel(type: PanelType, title: string, icon: LucideIcon) {
  openPanel(type, title, icon)
}

function handleCloseTab(id: string, event: MouseEvent) {
  event.stopPropagation()
  closeTab(id)
}

// ---- Tab detach functionality ----

/**
 * Detach a tab into a floating window via Electron IPC.
 * Falls back gracefully if the Electron API is not available (e.g. in browser/preview).
 */
async function handleDetachTab(id: string, event?: MouseEvent) {
  event?.stopPropagation()
  event?.preventDefault()

  // If the Electron detach API is not available, do nothing
  if (!(window as unknown as { tinadec?: { detachPanel?: unknown } }).tinadec?.detachPanel) return

  await detachTab(id, {
    sessionId: props.selectedSessionId,
    projectPath: props.currentProjectPath,
  })
}

/**
 * Right-click context menu on tabs for detach option.
 */
function handleTabContextMenu(id: string, event: MouseEvent) {
  event.preventDefault()
  event.stopPropagation()
  const tab = tabs.value.find((t) => t.id === id)
  if (tab && tab.closable && (window as unknown as { tinadec?: { detachPanel?: unknown } }).tinadec?.detachPanel) {
    void handleDetachTab(id)
  }
}

// ---- Drag-to-detach functionality (Chrome-style tab tearing) ----
//
// The core challenge: HTML5 mouseup does not fire when the cursor leaves
// the browser window. We solve this by:
// 1. Tracking mousedown on the tab to record the starting position.
// 2. On mousemove, checking if the cursor has moved beyond a threshold.
// 3. If the cursor exits the main window bounds (queried via Electron IPC),
//    we immediately trigger the detach.
// 4. We poll cursor position via IPC during drag because renderer mousemove
//    events stop when the cursor leaves the window.

const dragState = ref<{
  tabId: string
  startClientX: number
  startClientY: number
  startScreenX: number
  startScreenY: number
  dragging: boolean
  detachTriggered: boolean
  polling: boolean
} | null>(null)

const DRAG_THRESHOLD = 30 // pixels of movement before considering it a drag
const DETACH_EDGE_MARGIN = 8 // pixels from window edge to trigger detach
let pollTimer: ReturnType<typeof setInterval> | null = null

function onTabMouseDown(event: MouseEvent, tab: PanelTab) {
  if (!tab.closable) return
  if (event.button !== 0) return
  // Don't start drag tracking if clicking on the close/detach buttons
  const target = event.target as HTMLElement
  if (target.closest('.browser-tab-close') || target.closest('.browser-tab-detach')) return

  dragState.value = {
    tabId: tab.id,
    startClientX: event.clientX,
    startClientY: event.clientY,
    startScreenX: event.screenX,
    startScreenY: event.screenY,
    dragging: false,
    detachTriggered: false,
    polling: false,
  }

  document.addEventListener('mousemove', onDragMouseMove)
  document.addEventListener('mouseup', onDragMouseUp)
}

function onDragMouseMove(event: MouseEvent) {
  if (!dragState.value || dragState.value.detachTriggered) return

  const dx = Math.abs(event.clientX - dragState.value.startClientX)
  const dy = Math.abs(event.clientY - dragState.value.startClientY)

  if (!dragState.value.dragging && (dx > DRAG_THRESHOLD || dy > DRAG_THRESHOLD)) {
    dragState.value.dragging = true
    // Start polling cursor position via IPC to detect when cursor leaves window
    startCursorPolling()
  }
}

/**
 * Poll the cursor screen position via Electron IPC. When the cursor is
 * detected outside the main window bounds, trigger the detach.
 */
function startCursorPolling() {
  if (!dragState.value || dragState.value.polling) return
  dragState.value.polling = true

  if (pollTimer) clearInterval(pollTimer)
  pollTimer = setInterval(async () => {
    if (!dragState.value || dragState.value.detachTriggered) {
      stopCursorPolling()
      return
    }

    try {
      const [cursor, mainBounds] = await Promise.all([
        window.tinadec?.getCursorScreen?.(),
        window.tinadec?.getMainBounds?.(),
      ])

      if (!cursor || !mainBounds) {
        stopCursorPolling()
        return
      }

      // Check if cursor is outside the main window bounds (with margin)
      const outsideX = cursor.x < mainBounds.x + DETACH_EDGE_MARGIN ||
                        cursor.x > mainBounds.x + mainBounds.width - DETACH_EDGE_MARGIN
      const outsideY = cursor.y < mainBounds.y + DETACH_EDGE_MARGIN ||
                        cursor.y > mainBounds.y + mainBounds.height - DETACH_EDGE_MARGIN

      if (outsideX || outsideY) {
        // Cursor left the main window — trigger detach
        dragState.value.detachTriggered = true
        stopCursorPolling()
        const tabId = dragState.value.tabId
        dragState.value = null
        void handleDetachTab(tabId)
      }
    } catch {
      stopCursorPolling()
    }
  }, 50) // Poll every 50ms for responsive detection
}

function stopCursorPolling() {
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = null
  }
  if (dragState.value) {
    dragState.value.polling = false
  }
}

function onDragMouseUp(_event: MouseEvent) {
  document.removeEventListener('mousemove', onDragMouseMove)
  document.removeEventListener('mouseup', onDragMouseUp)
  stopCursorPolling()
  dragState.value = null
}

// ---- Listen for reattach and closed events from floating windows ----

let removeReattachListener: (() => void) | null = null
let removeClosedListener: (() => void) | null = null
let removeDetachedListener: (() => void) | null = null

onMounted(() => {
  removeReattachListener = window.tinadec?.onPanelReattach?.((data) => {
    const icon = getIconForType(data.type as PanelType)
    reattachTab(data.type as PanelType, data.title, icon, data.state)
  }) ?? null

  removeClosedListener = window.tinadec?.onPanelClosed?.((data) => {
    removeDetachedTab(data.tabId)
  }) ?? null

  removeDetachedListener = window.tinadec?.onPanelDetached?.(() => {
    // Panel was detached; the usePanelTabs.detachTab already handles tab removal
  }) ?? null
})

onUnmounted(() => {
  removeReattachListener?.()
  removeClosedListener?.()
  removeDetachedListener?.()
  stopCursorPolling()
  document.removeEventListener('mousemove', onDragMouseMove)
  document.removeEventListener('mouseup', onDragMouseUp)
})

function getIconForType(type: PanelType): LucideIcon {
  switch (type) {
    case 'git': return panelIcons.GitBranch
    case 'approval': return panelIcons.ShieldCheck
    case 'orchestration': return panelIcons.Layers3
    case 'events': return panelIcons.Activity
    case 'doctor': return panelIcons.Stethoscope
    case 'preview': return panelIcons.Globe
    case 'agent': return panelIcons.Bot
    default: return panelIcons.Globe
  }
}

/**
 * Focus a detached panel window by clicking its indicator in the tab bar.
 */
function focusDetachedWindow(windowId: number) {
  window.tinadec?.focusPanelWindow?.(windowId)
}

function startResize(event: MouseEvent) {
  isResizing.value = true
  const startX = event.clientX
  const startWidth = panelWidth.value

  function onMouseMove(e: MouseEvent) {
    const delta = startX - e.clientX
    const newWidth = Math.max(280, Math.min(760, startWidth + delta))
    panelWidth.value = newWidth
  }

  function onMouseUp() {
    isResizing.value = false
    document.removeEventListener('mousemove', onMouseMove)
    document.removeEventListener('mouseup', onMouseUp)
  }

  document.addEventListener('mousemove', onMouseMove)
  document.addEventListener('mouseup', onMouseUp)
}
</script>

<template>
  <aside
    ref="panelRef"
    class="float-panel browser-tabs-panel"
    :class="panelClass"
    :style="{ width: collapsed ? undefined : `${panelWidth}px` }"
  >
    <!-- Resize handle -->
    <div
      v-if="!collapsed"
      class="float-panel-resizer"
      @mousedown="startResize"
    />

    <template v-if="!collapsed">
      <!-- Browser-style tab bar -->
      <div class="browser-tab-bar">
        <button
          class="browser-tab browser-tab-home"
          :class="{ active: activeTabId === 'home' }"
          :title="t('context.homeTitle')"
          @click="goHome"
        >
          <HomeIcon :size="14" />
          <span class="browser-tab-label">{{ t('context.homeTabLabel') }}</span>
        </button>

        <button
          v-for="tab in tabs.filter((t) => t.type !== 'home')"
          :key="tab.id"
          class="browser-tab"
          :class="{
            active: activeTabId === tab.id,
            'tab-dragging': dragState?.tabId === tab.id && dragState?.dragging,
          }"
          :title="tab.title"
          @click="selectTab(tab.id)"
          @mousedown="onTabMouseDown($event, tab)"
          @contextmenu="handleTabContextMenu(tab.id, $event)"
        >
          <component :is="tab.icon" :size="14" class="browser-tab-icon" />
          <span class="browser-tab-label">{{ tab.title }}</span>
          <span
            class="browser-tab-detach"
            :title="t('context.detachTab')"
            @click="handleDetachTab(tab.id, $event)"
          >
            <ExternalLink :size="11" />
          </span>
          <span
            class="browser-tab-close"
            @click="handleCloseTab(tab.id, $event)"
          >
            <X :size="11" />
          </span>
        </button>

        <!-- Detached tab indicators (clickable to focus the floating window) -->
        <button
          v-for="detached in detachedTabs"
          :key="detached.tabId"
          class="browser-tab browser-tab-detached"
          :title="t('context.detachedTabHint', { title: detached.title })"
          @click="focusDetachedWindow(detached.windowId)"
        >
          <component :is="getIconForType(detached.type)" :size="14" class="browser-tab-icon" />
          <span class="browser-tab-label">{{ detached.title }}</span>
          <ExternalLink :size="10" class="browser-tab-detached-icon" />
        </button>

        <button
          class="browser-tab-add"
          :title="t('context.homeNewTab')"
          @click="goHome"
        >
          <Plus :size="14" />
        </button>

        <button
          class="browser-tab-collapse"
          :title="t('app.collapse')"
          @click="collapsed = true"
        >
          <PanelRightClose :size="14" />
        </button>
      </div>

      <!-- Tab content area -->
      <div class="browser-tab-content">
        <!-- Home panel -->
        <div v-show="activeTabId === 'home'" class="browser-tab-pane">
          <PanelHome
            :pending-approval-count="pendingApprovalCount"
            :compact="isCompact"
            @open-panel="handleOpenPanel"
          />
        </div>

        <!-- Git panel -->
        <div
          v-for="tab in tabs.filter((t) => t.type === 'git')"
          :key="tab.id"
          v-show="activeTabId === tab.id"
          class="browser-tab-pane"
        >
          <GitPanel
            :approvals="approvals"
            :current-project-path="currentProjectPath"
            :selected-session-id="selectedSessionId"
            @decide-approval="(a, d) => emit('decide-approval', a, d)"
            @approval-created="emit('approval-created', $event)"
          />
        </div>

        <!-- Approval panel -->
        <div
          v-for="tab in tabs.filter((t) => t.type === 'approval')"
          :key="tab.id"
          v-show="activeTabId === tab.id"
          class="browser-tab-pane"
        >
          <ApprovalTab
            :approvals="approvals"
            :shell-command="shellCommand"
            :busy="busy"
            :selected-session-id="selectedSessionId"
            @request-approval="emit('request-approval')"
            @decide-approval="(a, d) => emit('decide-approval', a, d)"
            @update:shell-command="emit('update:shellCommand', $event)"
          />
        </div>

        <!-- Orchestration panel -->
        <div
          v-for="tab in tabs.filter((t) => t.type === 'orchestration')"
          :key="tab.id"
          v-show="activeTabId === tab.id"
          class="browser-tab-pane"
        >
          <OrchestrationTab :snapshot="orchestration" :tool-executions="toolExecutions" />
        </div>

        <!-- Events panel -->
        <div
          v-for="tab in tabs.filter((t) => t.type === 'events')"
          :key="tab.id"
          v-show="activeTabId === tab.id"
          class="browser-tab-pane"
        >
          <EventsTab :events="events" />
        </div>

        <!-- Doctor panel -->
        <div
          v-for="tab in tabs.filter((t) => t.type === 'doctor')"
          :key="tab.id"
          v-show="activeTabId === tab.id"
          class="browser-tab-pane"
        >
          <DoctorTab :doctor="doctor" :readiness="readiness" />
        </div>

        <!-- Preview browser panels -->
        <div
          v-for="tab in tabs.filter((t) => t.type === 'preview')"
          :key="tab.id"
          v-show="activeTabId === tab.id"
          class="browser-tab-pane"
        >
          <PreviewBrowserPanel :initial-url="(tab.state?.url as string) ?? ''" />
        </div>

        <!-- Agent activity panel -->
        <div
          v-for="tab in tabs.filter((t) => t.type === 'agent')"
          :key="tab.id"
          v-show="activeTabId === tab.id"
          class="browser-tab-pane"
        >
          <AgentActivityPanel
            :activity="agentActivity"
            :agent-states="agentStates"
            :thinking-steps="thinkingSteps"
            :progress-events="progressEvents"
            :orchestration="orchestration"
          />
        </div>
      </div>
    </template>

    <!-- Collapsed state -->
    <div v-else class="float-panel-collapsed-bar">
      <button
        class="float-panel-toggle-btn"
        :title="t('app.expand')"
        @click="collapsed = false"
      >
        <PanelRightOpen :size="16" />
      </button>
      <button
        class="float-panel-collapsed-icon"
        :class="{ active: activeTabId === 'home' }"
        :title="t('context.homeTitle')"
        @click="collapsed = false; goHome()"
      >
        <HomeIcon :size="18" />
      </button>
    </div>
  </aside>
</template>
