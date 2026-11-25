<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlignHorizontalJustifyCenter,
  AlignHorizontalJustifyEnd,
  AlignHorizontalJustifyStart,
  Columns3,
  MessageCircle,
  Minus,
} from '@lucide/vue'
import { useFeaturePanelMaximize } from '@/composables/useFeaturePanelMaximize'
import { homeController } from '@/controllers/HomeController'
import ChatCard from '../../../../TinadecUI/src/components/cards/home/ChatCard.vue'

type DockSide = 'left' | 'center' | 'right'
type ResizeEdge = 'top' | 'left' | 'right'

const ORB_SIZE = 48
const SAFE_MARGIN_X = 18
const SAFE_MARGIN_BOTTOM = 18
const SAFE_MARGIN_TOP = 18
const MIN_PIP_WIDTH = 340
const MAX_PIP_WIDTH = 580
const MIN_PIP_HEIGHT = 340
const MAX_PIP_HEIGHT_PCT = 78
const MIN_PIP_HEIGHT_PCT = 25
const ORB_DRAG_THRESHOLD_PX = 5
const HEADER_DRAG_THRESHOLD_PX = 4
const DOCK_SIDES: readonly DockSide[] = ['left', 'center', 'right']

const props = defineProps<{
  bounds: { x: number; y: number; width: number; height: number }
}>()

const { t, te } = useI18n()

const {
  maximized,
  pipOpen,
  dockSide,
  pipSize,
  exitMaximize,
  openPip,
  minimizePip,
  setDockSide,
  setPipSize,
} = useFeaturePanelMaximize()

function label(key: string, fallback: string, params?: Record<string, string | number>): string {
  if (te(key)) return params ? String(t(key, params)) : String(t(key))
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, name: string) => String(params[name] ?? ''))
}

function clamp(value: number, min: number, max: number): number {
  if (max < min) return min
  return Math.min(max, Math.max(min, value))
}

const shellRef = ref<HTMLElement | null>(null)
const orbHovered = ref(false)
const orbFocused = ref(false)

// Live drag & resize transient state (transitions disabled while active)
const isDragging = ref(false)
const dragMode = ref<'orb' | 'window' | null>(null)
const dragLeft = ref<number | null>(null)
const dragPreviewSide = ref<DockSide | null>(null)

const isResizing = ref(false)
const resizeWidth = ref<number | null>(null)
const resizeHeight = ref<number | null>(null)

// Pointer tracking state
let activePointerId: number | null = null
let dragStartX = 0
let dragStartY = 0
let dragStartLeft = 0
let dragMoved = false
let suppressNextOrbClick = false

let resizeEdge: ResizeEdge | null = null
let resizeStartX = 0
let resizeStartY = 0
let resizeStartW = 0
let resizeStartH = 0

// ---- Live controller status & unread badge ----
const isWorking = computed(() => {
  const working = Boolean(homeController.working?.value)
  const busy = Boolean(homeController.busy?.value)
  const hasActiveRun = (homeController.activeRuns?.value?.length ?? 0) > 0
  return working || busy || hasActiveRun
})

const pendingApprovalCount = computed(() =>
  (homeController.approvals?.value ?? []).filter((item) => item.status === 'pending').length,
)

const unreadAssistantCount = ref(0)
const seenAssistantIds = new Set<string>()
let lastTrackedSessionId: string | null = homeController.selectedSessionId?.value ?? null

function seedSeenAssistantMessages() {
  seenAssistantIds.clear()
  for (const msg of homeController.messages?.value ?? []) {
    if (msg.role === 'assistant' && msg.id) seenAssistantIds.add(msg.id)
  }
}

seedSeenAssistantMessages()

watch(
  () => homeController.selectedSessionId?.value ?? null,
  (nextSessionId) => {
    if (nextSessionId !== lastTrackedSessionId) {
      lastTrackedSessionId = nextSessionId
      unreadAssistantCount.value = 0
      seedSeenAssistantMessages()
    }
  },
)

watch(
  () => homeController.messages?.value ?? [],
  (list) => {
    let newlyArrived = 0
    for (const msg of list) {
      if (msg.role !== 'assistant' || !msg.id) continue
      if (!seenAssistantIds.has(msg.id)) {
        seenAssistantIds.add(msg.id)
        if (!pipOpen.value) newlyArrived += 1
      }
    }
    if (newlyArrived > 0 && !pipOpen.value) {
      unreadAssistantCount.value += newlyArrived
    }
  },
  { deep: false },
)

const badgeCount = computed(() => pendingApprovalCount.value + unreadAssistantCount.value)
const badgeText = computed(() => (badgeCount.value > 99 ? '99+' : String(badgeCount.value)))
const hasUrgentApproval = computed(() => pendingApprovalCount.value > 0)

const sessionTitle = computed(() => {
  const raw = homeController.currentSession?.value?.title?.trim()
  return raw || ''
})

const headerTitle = computed(() => sessionTitle.value || label('chat.title', '对话'))

const hoverSummary = computed(() => {
  if (pendingApprovalCount.value > 0) {
    return label('floatingChat.pendingApprovals', '{count} 项待审批', { count: pendingApprovalCount.value })
  }
  if (isWorking.value) {
    const workingText = label('floatingChat.working', '运行中…')
    return sessionTitle.value ? `${sessionTitle.value} · ${workingText}` : workingText
  }
  if (unreadAssistantCount.value > 0) {
    return label('floatingChat.unreadReplies', '{count} 条新回复', { count: unreadAssistantCount.value })
  }
  if (sessionTitle.value) return sessionTitle.value
  return label('floatingChat.expandPip', '展开对话小窗')
})

// ---- Localized labels (kept in script so template stays free of raw CJK) ----
const orbAriaLabel = computed(() => label('floatingChat.orbAriaLabel', '对话悬浮球'))
const windowAriaLabel = computed(() => label('floatingChat.windowAriaLabel', '对话画中画小窗'))
const dockLeftLabel = computed(() => label('floatingChat.dockLeft', '靠左停靠'))
const dockCenterLabel = computed(() => label('floatingChat.dockCenter', '居中停靠'))
const dockRightLabel = computed(() => label('floatingChat.dockRight', '靠右停靠'))
const restoreThreeColLabel = computed(() => label('floatingChat.restoreThreeCol', '还原三栏'))
const minimizeToOrbLabel = computed(() => label('floatingChat.minimizeToOrb', '最小化为悬浮球'))
const dragToDockHint = computed(() => label('floatingChat.dragToDock', '拖拽停靠到左 / 中 / 右'))
const resizeTopLabel = computed(() => label('floatingChat.resizeTop', '拖拽调整小窗高度'))
const resizeLeftLabel = computed(() => label('floatingChat.resizeLeft', '拖拽调整小窗宽度'))
const resizeRightLabel = computed(() => label('floatingChat.resizeRight', '拖拽调整小窗宽度'))

// ---- Geometry & Bounds Clamping (strictly zero transform!) ----
const panelW = computed(() => Math.max(0, Math.round(props.bounds.width)))
const panelH = computed(() => Math.max(0, Math.round(props.bounds.height)))

const layerStyle = computed(() => ({
  left: `${Math.round(props.bounds.x)}px`,
  top: `${Math.round(props.bounds.y)}px`,
  width: `${panelW.value}px`,
  height: `${panelH.value}px`,
}))

const maxPipWidth = computed(() => {
  const available = Math.max(0, panelW.value - SAFE_MARGIN_X * 2)
  if (panelW.value > 0 && panelW.value < MIN_PIP_WIDTH) {
    return Math.max(0, panelW.value - 16)
  }
  return Math.min(MAX_PIP_WIDTH, Math.max(MIN_PIP_WIDTH, available))
})

const minPipWidth = computed(() => Math.min(MIN_PIP_WIDTH, maxPipWidth.value))

const clampedPipWidth = computed(() =>
  clamp(Math.round(pipSize.value.width || 420), minPipWidth.value, maxPipWidth.value),
)

const maxPipHeight = computed(() => {
  const pctCap = Math.round(panelH.value * (MAX_PIP_HEIGHT_PCT / 100))
  const safeAvailable = Math.max(0, panelH.value - SAFE_MARGIN_BOTTOM - SAFE_MARGIN_TOP)
  if (panelH.value > 0 && safeAvailable < MIN_PIP_HEIGHT) {
    return safeAvailable
  }
  return Math.min(safeAvailable, Math.max(MIN_PIP_HEIGHT, pctCap))
})

const minPipHeight = computed(() => Math.min(MIN_PIP_HEIGHT, maxPipHeight.value))

const clampedPipHeight = computed(() => {
  const raw = Math.round((panelH.value * (pipSize.value.heightPct || 62)) / 100)
  return clamp(raw, minPipHeight.value, maxPipHeight.value)
})

function horizontalBounds(itemWidth: number): { min: number; max: number } {
  const w = panelW.value
  const minLeft = Math.min(SAFE_MARGIN_X, Math.max(0, Math.floor((w - itemWidth) / 2)))
  const maxLeft = Math.max(minLeft, w - itemWidth - minLeft)
  return { min: minLeft, max: maxLeft }
}

function computeDockLeft(side: DockSide, itemWidth: number): number {
  const { min, max } = horizontalBounds(itemWidth)
  if (side === 'left') return min
  if (side === 'right') return max
  return Math.round(clamp((panelW.value - itemWidth) / 2, min, max))
}

function resolveNearestDockSide(currentLeft: number, itemWidth: number): DockSide {
  const leftPos = computeDockLeft('left', itemWidth)
  const rightPos = computeDockLeft('right', itemWidth)
  if (Math.abs(rightPos - leftPos) < 2) return dockSide.value
  let best: DockSide = dockSide.value
  let bestDist = Number.POSITIVE_INFINITY
  for (const side of DOCK_SIDES) {
    const dist = Math.abs(currentLeft - computeDockLeft(side, itemWidth))
    if (dist < bestDist) {
      bestDist = dist
      best = side
    }
  }
  return best
}

const currentWidth = computed(() => {
  if (!pipOpen.value) return ORB_SIZE
  return resizeWidth.value ?? clampedPipWidth.value
})

const currentHeight = computed(() => {
  if (!pipOpen.value) return ORB_SIZE
  return resizeHeight.value ?? clampedPipHeight.value
})

const currentLeft = computed(() => {
  if (dragLeft.value !== null) {
    const { min, max } = horizontalBounds(currentWidth.value)
    return Math.round(clamp(dragLeft.value, min, max))
  }
  return computeDockLeft(dockSide.value, currentWidth.value)
})

const shellStyle = computed(() => ({
  left: `${currentLeft.value}px`,
  bottom: `${SAFE_MARGIN_BOTTOM}px`,
  width: `${currentWidth.value}px`,
  height: `${currentHeight.value}px`,
  borderRadius: pipOpen.value ? '14px' : '999px',
}))

// Lock expanded body geometry to the target pixel size (shell minus 2px border) so opening/closing
// shell width/height transitions clip smoothly without reflowing the inner chat/scroller/composer tree
// 18+ times across breakpoints during the 300ms morph. Strictly zero transform.
const windowContentStyle = computed(() => {
  const targetWidth = Math.max(0, (resizeWidth.value ?? clampedPipWidth.value) - 2)
  const targetHeight = Math.max(0, (resizeHeight.value ?? clampedPipHeight.value) - 2)
  return {
    width: `${targetWidth}px`,
    height: `${targetHeight}px`,
  }
})

// Hover chip positioned strictly inside bounds next to the orb (zero transform)
const showHoverChip = computed(() => !pipOpen.value && !isDragging.value && (orbHovered.value || orbFocused.value))

const hoverChipStyle = computed(() => {
  const orbLeft = currentLeft.value
  const chipBottom = SAFE_MARGIN_BOTTOM + 8
  if (dockSide.value === 'right') {
    const rightOffset = Math.max(SAFE_MARGIN_X, panelW.value - orbLeft + 10)
    const maxWidth = Math.max(120, Math.min(240, orbLeft - SAFE_MARGIN_X - 10))
    return {
      right: `${rightOffset}px`,
      bottom: `${chipBottom}px`,
      maxWidth: `${maxWidth}px`,
    }
  }
  const leftOffset = orbLeft + ORB_SIZE + 10
  const maxWidth = Math.max(120, Math.min(240, panelW.value - leftOffset - SAFE_MARGIN_X))
  return {
    left: `${leftOffset}px`,
    bottom: `${chipBottom}px`,
    maxWidth: `${maxWidth}px`,
  }
})

// 3 dashed target landing ghosts while dragging orb or window
const dropGhosts = computed(() => {
  if (!isDragging.value || !dragMode.value) return []
  const isOrbDrag = dragMode.value === 'orb'
  const w = isOrbDrag ? ORB_SIZE : clampedPipWidth.value
  const h = isOrbDrag ? ORB_SIZE : clampedPipHeight.value
  const radius = isOrbDrag ? '999px' : '14px'
  return DOCK_SIDES.map((side) => ({
    side,
    active: dragPreviewSide.value === side,
    style: {
      left: `${computeDockLeft(side, w)}px`,
      bottom: `${SAFE_MARGIN_BOTTOM}px`,
      width: `${w}px`,
      height: `${h}px`,
      borderRadius: radius,
    },
  }))
})

// ---- Focus composer textarea on open & clear unread ----
async function focusComposerTextarea() {
  await nextTick()
  if (!pipOpen.value) return
  const textarea = shellRef.value?.querySelector<HTMLTextAreaElement>('textarea.welcome-dialog-input, textarea')
  textarea?.focus({ preventScroll: true })
}

function handleOpenPip() {
  unreadAssistantCount.value = 0
  orbHovered.value = false
  orbFocused.value = false
  openPip()
}

watch(pipOpen, (open) => {
  if (open) {
    unreadAssistantCount.value = 0
    orbHovered.value = false
    orbFocused.value = false
    void focusComposerTextarea()
  }
})

// ---- Orb pointer click + horizontal drag-to-snap ----
function onOrbPointerDown(event: PointerEvent) {
  if (event.button !== 0 || pipOpen.value) return
  activePointerId = event.pointerId
  dragStartX = event.clientX
  dragStartY = event.clientY
  dragStartLeft = currentLeft.value
  dragMoved = false
  suppressNextOrbClick = false
  ;(event.currentTarget as HTMLElement | null)?.setPointerCapture?.(event.pointerId)
}

function onOrbPointerMove(event: PointerEvent) {
  if (activePointerId === null || event.pointerId !== activePointerId || pipOpen.value) return
  const dx = event.clientX - dragStartX
  const dy = event.clientY - dragStartY
  if (!dragMoved && Math.hypot(dx, dy) >= ORB_DRAG_THRESHOLD_PX) {
    dragMoved = true
    isDragging.value = true
    dragMode.value = 'orb'
  }
  if (!dragMoved) return
  const { min, max } = horizontalBounds(ORB_SIZE)
  const nextLeft = Math.round(clamp(dragStartLeft + dx, min, max))
  dragLeft.value = nextLeft
  dragPreviewSide.value = resolveNearestDockSide(nextLeft, ORB_SIZE)
}

function finishOrbPointer(event: PointerEvent, cancelled: boolean) {
  if (activePointerId === null || event.pointerId !== activePointerId) return
  ;(event.currentTarget as HTMLElement | null)?.releasePointerCapture?.(event.pointerId)
  const wasMoved = dragMoved
  const finalLeft = dragLeft.value
  activePointerId = null
  dragMoved = false
  isDragging.value = false
  dragMode.value = null
  dragLeft.value = null
  dragPreviewSide.value = null

  if (wasMoved) {
    suppressNextOrbClick = true
    if (!cancelled && finalLeft !== null) {
      setDockSide(resolveNearestDockSide(finalLeft, ORB_SIZE))
    }
    return
  }

  if (!cancelled && !pipOpen.value) {
    suppressNextOrbClick = true
    handleOpenPip()
  }
}

function onOrbClick() {
  if (suppressNextOrbClick) {
    suppressNextOrbClick = false
    return
  }
  if (!pipOpen.value) {
    handleOpenPip()
  }
}

// ---- Expanded PiP Header pointer-captured horizontal drag ----
function onHeaderPointerDown(event: PointerEvent) {
  if (event.button !== 0 || !pipOpen.value) return
  const target = event.target as HTMLElement | null
  if (target?.closest('button, [data-no-drag="true"]')) return
  activePointerId = event.pointerId
  dragStartX = event.clientX
  dragStartY = event.clientY
  dragStartLeft = currentLeft.value
  dragMoved = false
  ;(event.currentTarget as HTMLElement | null)?.setPointerCapture?.(event.pointerId)
}

function onHeaderPointerMove(event: PointerEvent) {
  if (activePointerId === null || event.pointerId !== activePointerId || !pipOpen.value) return
  const dx = event.clientX - dragStartX
  if (!dragMoved && Math.abs(dx) >= HEADER_DRAG_THRESHOLD_PX) {
    dragMoved = true
    isDragging.value = true
    dragMode.value = 'window'
  }
  if (!dragMoved) return
  const w = clampedPipWidth.value
  const { min, max } = horizontalBounds(w)
  const nextLeft = Math.round(clamp(dragStartLeft + dx, min, max))
  dragLeft.value = nextLeft
  dragPreviewSide.value = resolveNearestDockSide(nextLeft, w)
}

function finishHeaderPointer(event: PointerEvent, cancelled: boolean) {
  if (activePointerId === null || event.pointerId !== activePointerId) return
  ;(event.currentTarget as HTMLElement | null)?.releasePointerCapture?.(event.pointerId)
  const wasMoved = dragMoved
  const finalLeft = dragLeft.value
  activePointerId = null
  dragMoved = false
  isDragging.value = false
  dragMode.value = null
  dragLeft.value = null
  dragPreviewSide.value = null

  if (wasMoved && !cancelled && finalLeft !== null) {
    setDockSide(resolveNearestDockSide(finalLeft, clampedPipWidth.value))
  }
}

// ---- Edge resize handles (top -> heightPct, left/right -> width) ----
function onResizePointerDown(edge: ResizeEdge, event: PointerEvent) {
  if (event.button !== 0 || !pipOpen.value) return
  event.stopPropagation()
  resizeEdge = edge
  activePointerId = event.pointerId
  resizeStartX = event.clientX
  resizeStartY = event.clientY
  resizeStartW = clampedPipWidth.value
  resizeStartH = clampedPipHeight.value
  isResizing.value = true
  ;(event.currentTarget as HTMLElement | null)?.setPointerCapture?.(event.pointerId)
}

function onResizePointerMove(event: PointerEvent) {
  if (!isResizing.value || !resizeEdge || activePointerId === null || event.pointerId !== activePointerId) return
  if (resizeEdge === 'top') {
    const dy = resizeStartY - event.clientY
    const nextH = Math.round(clamp(resizeStartH + dy, minPipHeight.value, maxPipHeight.value))
    resizeHeight.value = nextH
    const pct = panelH.value > 0
      ? clamp(Math.round((nextH / panelH.value) * 100), MIN_PIP_HEIGHT_PCT, MAX_PIP_HEIGHT_PCT)
      : pipSize.value.heightPct
    setPipSize({ width: clampedPipWidth.value, heightPct: pct })
    return
  }

  const rawDelta = resizeEdge === 'right'
    ? event.clientX - resizeStartX
    : resizeStartX - event.clientX
  const multiplier = dockSide.value === 'center' ? 2 : 1
  const nextW = Math.round(clamp(resizeStartW + rawDelta * multiplier, minPipWidth.value, maxPipWidth.value))
  resizeWidth.value = nextW
  setPipSize({ width: nextW, heightPct: pipSize.value.heightPct })
}

function finishResizePointer(event: PointerEvent) {
  if (!isResizing.value || activePointerId === null || event.pointerId !== activePointerId) return
  ;(event.currentTarget as HTMLElement | null)?.releasePointerCapture?.(event.pointerId)
  activePointerId = null
  resizeEdge = null
  isResizing.value = false
  resizeWidth.value = null
  resizeHeight.value = null
}

onBeforeUnmount(() => {
  activePointerId = null
  resizeEdge = null
  isDragging.value = false
  isResizing.value = false
})
</script>

<template>
  <div
    v-if="maximized"
    class="floating-chat-pip-layer"
    :style="layerStyle"
    data-testid="floating-chat-pip-layer"
  >
    <!-- 3 dashed landing ghosts shown behind the shell while dragging -->
    <div
      v-for="ghost in dropGhosts"
      :key="ghost.side"
      class="floating-chat-ghost"
      :class="{ 'is-active': ghost.active }"
      :style="ghost.style"
      :data-testid="`floating-chat-drop-ghost-${ghost.side}`"
      :data-active="ghost.active ? 'true' : 'false'"
      aria-hidden="true"
    />

    <!-- Single-line hover preview chip positioned cleanly next to the orb -->
    <div
      v-if="showHoverChip"
      class="floating-chat-hover-chip"
      :style="hoverChipStyle"
      role="tooltip"
      data-testid="floating-chat-hover-chip"
    >
      <span
        class="floating-chat-chip-dot"
        :class="{ 'is-working': isWorking, 'is-warning': hasUrgentApproval && !isWorking }"
        aria-hidden="true"
      />
      <span class="floating-chat-chip-text">{{ hoverSummary }}</span>
    </div>

    <!-- Single outer floating shell: continuous glass-safe morph between 48px orb & PiP window -->
    <section
      ref="shellRef"
      class="floating-chat-shell"
      :class="{
        'is-orb': !pipOpen,
        'is-window': pipOpen,
        'is-dragging': isDragging,
        'is-resizing': isResizing,
        'is-working': isWorking,
      }"
      :style="shellStyle"
      :data-testid="pipOpen ? 'floating-chat-pip-window' : 'floating-chat-orb'"
      :data-dock-side="dockSide"
      :data-pip-state="pipOpen ? 'window' : 'orb'"
      :aria-label="pipOpen ? windowAriaLabel : orbAriaLabel"
    >
      <!-- Collapsed Orb State -->
      <div v-if="!pipOpen" class="floating-chat-orb-wrap">
        <span
          v-if="isWorking"
          class="floating-chat-orb-ring"
          data-testid="floating-chat-orb-pulse"
          aria-hidden="true"
        />
        <button
          type="button"
          class="floating-chat-orb-btn"
          :aria-label="hoverSummary"
          :title="hoverSummary"
          @mouseenter="orbHovered = true"
          @mouseleave="orbHovered = false"
          @focus="orbFocused = true"
          @blur="orbFocused = false"
          @pointerdown="onOrbPointerDown"
          @pointermove="onOrbPointerMove"
          @pointerup="finishOrbPointer($event, false)"
          @pointercancel="finishOrbPointer($event, true)"
          @click="onOrbClick"
        >
          <MessageCircle :size="20" class="floating-chat-orb-icon" aria-hidden="true" />
        </button>
        <span
          v-if="badgeCount > 0"
          class="floating-chat-orb-badge"
          :class="{ 'is-urgent': hasUrgentApproval }"
          data-testid="floating-chat-orb-badge"
          aria-live="polite"
        >
          {{ badgeText }}
        </span>
      </div>

      <!-- Expanded PiP Window State -->
      <div v-else class="floating-chat-window-content" :style="windowContentStyle">
        <!-- Edge resize handles -->
        <div
          class="floating-chat-resize-handle is-top"
          data-testid="floating-chat-resize-top"
          role="separator"
          aria-orientation="horizontal"
          :aria-label="resizeTopLabel"
          @pointerdown="onResizePointerDown('top', $event)"
          @pointermove="onResizePointerMove"
          @pointerup="finishResizePointer"
          @pointercancel="finishResizePointer"
        />
        <div
          class="floating-chat-resize-handle is-left"
          data-testid="floating-chat-resize-left"
          role="separator"
          aria-orientation="vertical"
          :aria-label="resizeLeftLabel"
          @pointerdown="onResizePointerDown('left', $event)"
          @pointermove="onResizePointerMove"
          @pointerup="finishResizePointer"
          @pointercancel="finishResizePointer"
        />
        <div
          class="floating-chat-resize-handle is-right"
          data-testid="floating-chat-resize-right"
          role="separator"
          aria-orientation="vertical"
          :aria-label="resizeRightLabel"
          @pointerdown="onResizePointerDown('right', $event)"
          @pointermove="onResizePointerMove"
          @pointerup="finishResizePointer"
          @pointercancel="finishResizePointer"
        />

        <!-- Compact Header & Horizontal Drag Handle -->
        <header
          class="floating-chat-header"
          data-testid="floating-chat-pip-header"
          :title="dragToDockHint"
          @pointerdown="onHeaderPointerDown"
          @pointermove="onHeaderPointerMove"
          @pointerup="finishHeaderPointer($event, false)"
          @pointercancel="finishHeaderPointer($event, true)"
        >
          <div class="floating-chat-header-title-wrap">
            <span
              class="floating-chat-status-dot"
              :class="{
                'is-working': isWorking,
                'is-warning': hasUrgentApproval && !isWorking,
              }"
              data-testid="floating-chat-status-dot"
              aria-hidden="true"
            />
            <span
              class="floating-chat-session-title"
              data-testid="floating-chat-session-title"
              :title="headerTitle"
            >
              {{ headerTitle }}
            </span>
          </div>

          <div class="floating-chat-header-controls" data-no-drag="true">
            <div class="floating-chat-dock-group" role="group">
              <button
                type="button"
                class="floating-chat-icon-btn"
                :class="{ 'is-active': dockSide === 'left' }"
                :aria-pressed="dockSide === 'left'"
                :aria-label="dockLeftLabel"
                :title="dockLeftLabel"
                data-testid="floating-chat-dock-left"
                @click="setDockSide('left')"
              >
                <AlignHorizontalJustifyStart :size="13" aria-hidden="true" />
              </button>
              <button
                type="button"
                class="floating-chat-icon-btn"
                :class="{ 'is-active': dockSide === 'center' }"
                :aria-pressed="dockSide === 'center'"
                :aria-label="dockCenterLabel"
                :title="dockCenterLabel"
                data-testid="floating-chat-dock-center"
                @click="setDockSide('center')"
              >
                <AlignHorizontalJustifyCenter :size="13" aria-hidden="true" />
              </button>
              <button
                type="button"
                class="floating-chat-icon-btn"
                :class="{ 'is-active': dockSide === 'right' }"
                :aria-pressed="dockSide === 'right'"
                :aria-label="dockRightLabel"
                :title="dockRightLabel"
                data-testid="floating-chat-dock-right"
                @click="setDockSide('right')"
              >
                <AlignHorizontalJustifyEnd :size="13" aria-hidden="true" />
              </button>
            </div>

            <span class="floating-chat-header-divider" aria-hidden="true" />

            <button
              type="button"
              class="floating-chat-icon-btn"
              :aria-label="restoreThreeColLabel"
              :title="restoreThreeColLabel"
              data-testid="floating-chat-restore-3col"
              @click="exitMaximize()"
            >
              <Columns3 :size="13" aria-hidden="true" />
            </button>

            <button
              type="button"
              class="floating-chat-icon-btn"
              :aria-label="minimizeToOrbLabel"
              :title="minimizeToOrbLabel"
              data-testid="floating-chat-minimize-orb"
              @click="minimizePip()"
            >
              <Minus :size="14" aria-hidden="true" />
            </button>
          </div>
        </header>

        <!-- Live Chat Body -->
        <div class="floating-chat-body">
          <ChatCard compact-pip />
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
/* Overlay layer matches feature panel bounds strictly — ZERO transform on any ancestor
   wrapping the glass shell so backdrop-filter samples the live panel cleanly. */
.floating-chat-pip-layer {
  position: absolute;
  pointer-events: none;
  z-index: 40;
  overflow: visible;
  box-sizing: border-box;
}

.floating-chat-ghost {
  position: absolute;
  z-index: 1;
  pointer-events: none;
  box-sizing: border-box;
  border: 1.5px dashed color-mix(in srgb, var(--text-muted) 42%, transparent);
  background: color-mix(in srgb, var(--surface-section) 24%, transparent);
  opacity: 0.72;
  transition:
    border-color 140ms ease,
    background-color 140ms ease,
    opacity 140ms ease;
}

.floating-chat-ghost.is-active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
  opacity: 1;
}

.floating-chat-hover-chip {
  position: absolute;
  z-index: 3;
  pointer-events: none;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 11px;
  border-radius: 999px;
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  border: 1px solid var(--material-floating-border, var(--border-muted));
  box-shadow: var(--shadow-md, 0 8px 20px rgba(0, 0, 0, 0.18));
  color: var(--text-primary);
  font-size: 12px;
  line-height: 1;
  white-space: nowrap;
  overflow: hidden;
  transition: opacity 160ms ease;
}

.floating-chat-chip-dot,
.floating-chat-status-dot {
  width: 7px;
  height: 7px;
  border-radius: 999px;
  flex-shrink: 0;
  background: var(--text-muted);
}

.floating-chat-chip-dot.is-working,
.floating-chat-status-dot.is-working {
  background: var(--accent-primary);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-primary) 24%, transparent);
}

.floating-chat-chip-dot.is-warning,
.floating-chat-status-dot.is-warning {
  background: var(--accent-warning);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-warning) 24%, transparent);
}

.floating-chat-chip-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Single outer floating shell: continuous glass-safe morph */
.floating-chat-shell {
  position: absolute;
  z-index: 2;
  pointer-events: auto;
  display: flex;
  flex-direction: column;
  box-sizing: border-box;
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  border: 1px solid var(--material-floating-border, var(--border-muted));
  box-shadow: var(--shadow-lg, 0 12px 32px rgba(0, 0, 0, 0.24));
  transition:
    left 300ms cubic-bezier(0.22, 1, 0.36, 1),
    bottom 300ms cubic-bezier(0.22, 1, 0.36, 1),
    width 300ms cubic-bezier(0.22, 1, 0.36, 1),
    height 300ms cubic-bezier(0.22, 1, 0.36, 1),
    border-radius 300ms cubic-bezier(0.22, 1, 0.36, 1),
    box-shadow 200ms ease;
}

.floating-chat-shell.is-orb {
  overflow: visible;
}

.floating-chat-shell.is-window {
  overflow: clip;
}

.floating-chat-shell.is-dragging,
.floating-chat-shell.is-resizing {
  transition: none;
}

.floating-chat-shell.is-orb:hover {
  box-shadow: var(--shadow-xl, 0 14px 36px rgba(0, 0, 0, 0.28));
}

/* Orb contents */
.floating-chat-orb-wrap {
  position: relative;
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
}

.floating-chat-orb-ring {
  position: absolute;
  inset: -4px;
  border-radius: 999px;
  pointer-events: none;
  border: 1.5px solid color-mix(in srgb, var(--accent-primary) 55%, transparent);
  box-shadow: 0 0 12px color-mix(in srgb, var(--accent-primary) 34%, transparent);
  animation: floating-chat-orb-breathe 2.2s ease-in-out infinite;
}

@keyframes floating-chat-orb-breathe {
  0%, 100% {
    opacity: 0.45;
    box-shadow: 0 0 6px color-mix(in srgb, var(--accent-primary) 22%, transparent);
  }
  50% {
    opacity: 0.95;
    box-shadow: 0 0 16px color-mix(in srgb, var(--accent-primary) 46%, transparent);
  }
}

.floating-chat-orb-btn {
  width: 100%;
  height: 100%;
  padding: 0;
  border: 0;
  border-radius: 999px;
  background: transparent;
  color: var(--text-primary);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  touch-action: none;
  transition: color 160ms ease, background-color 160ms ease;
}

.floating-chat-orb-btn:hover {
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--surface-hover) 55%, transparent);
}

.floating-chat-orb-btn:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 2px;
}

.floating-chat-orb-icon {
  pointer-events: none;
}

.floating-chat-orb-badge {
  position: absolute;
  top: -3px;
  right: -3px;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: 999px;
  background: var(--accent-primary);
  color: var(--text-on-primary, #ffffff);
  font-size: 10px;
  font-weight: 700;
  line-height: 18px;
  text-align: center;
  font-variant-numeric: tabular-nums;
  box-shadow: 0 2px 6px rgba(0, 0, 0, 0.28);
  pointer-events: none;
}

.floating-chat-orb-badge.is-urgent {
  background: var(--accent-warning);
}

/* Expanded window contents */
.floating-chat-window-content {
  position: relative;
  display: flex;
  flex-direction: column;
  flex: 0 0 auto;
  box-sizing: border-box;
  min-width: 0;
  min-height: 0;
  animation: floating-chat-content-fade 180ms ease-out;
}

@keyframes floating-chat-content-fade {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}

.floating-chat-resize-handle {
  position: absolute;
  z-index: 6;
  touch-action: none;
}

.floating-chat-resize-handle.is-top {
  top: 0;
  left: 10px;
  right: 10px;
  height: 6px;
  cursor: ns-resize;
}

.floating-chat-resize-handle.is-left {
  left: 0;
  top: 10px;
  bottom: 10px;
  width: 6px;
  cursor: ew-resize;
}

.floating-chat-resize-handle.is-right {
  right: 0;
  top: 10px;
  bottom: 10px;
  width: 6px;
  cursor: ew-resize;
}

.floating-chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  height: 36px;
  padding: 0 8px 0 12px;
  flex-shrink: 0;
  border-bottom: 1px solid var(--material-floating-border, var(--border-muted));
  background: color-mix(in srgb, var(--surface-chrome, var(--surface-section)) 45%, transparent);
  cursor: grab;
  user-select: none;
  touch-action: none;
}

.floating-chat-shell.is-dragging .floating-chat-header {
  cursor: grabbing;
}

.floating-chat-header-title-wrap {
  display: flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
  flex: 1 1 auto;
}

.floating-chat-session-title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.floating-chat-header-controls {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
}

.floating-chat-dock-group {
  display: inline-flex;
  align-items: center;
  gap: 1px;
  padding: 2px;
  border-radius: 7px;
  background: color-mix(in srgb, var(--surface-section) 60%, transparent);
  border: 1px solid color-mix(in srgb, var(--border-muted) 75%, transparent);
}

.floating-chat-header-divider {
  width: 1px;
  height: 14px;
  margin: 0 3px;
  background: var(--border-muted);
}

.floating-chat-icon-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease;
}

.floating-chat-icon-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.floating-chat-icon-btn.is-active {
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
}

.floating-chat-icon-btn:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
}

.floating-chat-body {
  flex: 1 1 auto;
  min-width: 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: clip;
}

@media (prefers-reduced-motion: reduce) {
  .floating-chat-shell,
  .floating-chat-window-content,
  .floating-chat-orb-btn,
  .floating-chat-hover-chip,
  .floating-chat-ghost,
  .floating-chat-orb-ring,
  .floating-chat-icon-btn {
    transition: none !important;
    animation: none !important;
  }
}

:global([data-reduced-motion="true"]) .floating-chat-shell,
:global([data-reduced-motion="true"]) .floating-chat-window-content,
:global([data-reduced-motion="true"]) .floating-chat-orb-btn,
:global([data-reduced-motion="true"]) .floating-chat-hover-chip,
:global([data-reduced-motion="true"]) .floating-chat-ghost,
:global([data-reduced-motion="true"]) .floating-chat-orb-ring,
:global([data-reduced-motion="true"]) .floating-chat-icon-btn {
  transition: none !important;
  animation: none !important;
}
</style>
