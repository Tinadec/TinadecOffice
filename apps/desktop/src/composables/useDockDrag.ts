import { ref, type Ref } from 'vue'
import { resolveDockDrop, type DockDropZone, type DockPaneHitRect } from '@tinadec/ui'
import type { UieDockPaneGeometry, UieSlotId } from '@tinadec/ui'

// ---------------------------------------------------------------------------
// useDockDrag — in-window tab drag state for the feature-panel dock.
//
// When a tab is dragged inside the main window it computes a live drop target
// (dockview-style: pane edges -> split, pane center -> merge) so UieDock can
// render a highlight overlay. If the cursor leaves the window, the drag falls
// back to the legacy detach-to-floating-window behavior (the caller's onDetach).
//
// Module singleton so the dragging tab, the overlay, and every UieDock pane
// rect share one source of truth (same pattern as useDetachedTabs).
// ---------------------------------------------------------------------------

export interface DockDragOptions {
  slotId: UieSlotId
  /** The pane the tab came from (null when dragged from a plain stack). */
  sourcePaneId: string | null
  /** Called when the cursor leaves the window (floating-window detach). */
  onDetach: (tabId: string) => void
  /** Called on mouseup with the final drop target (or null for a plain click). */
  onEnd?: (target: DockDropTarget | null, tabId: string) => void
}

interface DockDragState {
  tabId: string
  slotId: UieSlotId
  sourcePaneId: string | null
  startX: number
  startY: number
  lastX: number
  lastY: number
  dragging: boolean
  detachTriggered: boolean
  polling: boolean
}

export interface DockDropTarget {
  paneId: string
  zone: DockDropZone
}

const DRAG_THRESHOLD = 30
const DETACH_EDGE_MARGIN = 8

const dragState = ref<DockDragState | null>(null)
const dropTarget = ref<DockDropTarget | null>(null)

/** slotId -> current pane viewport rects (registered by UieDock / UieColumn). */
const dockRegistrations = new Map<UieSlotId, { token: number; rects: DockPaneHitRect[] }>()
let regCounter = 0

/** Latest pane viewport rects for a slot (fallback: empty). */
function currentRects(slotId: UieSlotId): DockPaneHitRect[] {
  return dockRegistrations.get(slotId)?.rects ?? []
}

/** The latest startDrag options (for the detach callback fired from polling). */
let pendingOpts: DockDragOptions | null = null

let pollTimer: ReturnType<typeof setInterval> | null = null

export function useDockDrag() {
  function isDraggingTab(tabId: string): boolean {
    return dragState.value?.tabId === tabId && dragState.value.dragging
  }

  /**
   * Register (or refresh) a dock's pane rects in viewport coordinates.
   * UieDock calls this from a watchEffect over its geometry; the returned
   * unsubscribe removes the entry when the dock unmounts.
   */
  function registerDock(
    slotId: UieSlotId,
    containerRect: { x: number; y: number },
    panes: UieDockPaneGeometry[],
  ): () => void {
    const token = ++regCounter
    dockRegistrations.set(slotId, {
      token,
      rects: panes.map((p) => ({
        paneId: p.paneId,
        x: containerRect.x + p.x,
        y: containerRect.y + p.y,
        width: p.width,
        height: p.height,
      })),
    })
    // Latest registration wins; stale unsubscribes never remove a newer one.
    return () => {
      const cur = dockRegistrations.get(slotId)
      if (cur?.token === token) dockRegistrations.delete(slotId)
    }
  }

  function startDrag(
    instance: { id: string },
    opts: DockDragOptions,
    startPoint: { x: number; y: number },
  ): void {
    if (dragState.value) return
    pendingOpts = opts
    dragState.value = {
      tabId: instance.id,
      slotId: opts.slotId,
      sourcePaneId: opts.sourcePaneId,
      startX: startPoint.x,
      startY: startPoint.y,
      lastX: startPoint.x,
      lastY: startPoint.y,
      dragging: false,
      detachTriggered: false,
      polling: false,
    }
    document.addEventListener('mousemove', onDragMouseMove)
    document.addEventListener('mouseup', onDragMouseUp)
  }

  function onDragMouseMove(event: MouseEvent): void {
    const state = dragState.value
    if (!state || state.detachTriggered) return

    if (!state.dragging) {
      const dx = Math.abs(event.clientX - state.startX)
      const dy = Math.abs(event.clientY - state.startY)
      if (dx > DRAG_THRESHOLD || dy > DRAG_THRESHOLD) {
        state.dragging = true
        startCursorPolling()
      }
    }

    state.lastX = event.clientX
    state.lastY = event.clientY
    if (state.dragging) {
      dropTarget.value = resolveDockDrop(
        { x: state.lastX, y: state.lastY },
        currentRects(state.slotId),
      )
    }
  }

  function startCursorPolling(): void {
    const state = dragState.value
    if (!state || state.polling) return
    state.polling = true

    if (pollTimer) clearInterval(pollTimer)
    pollTimer = setInterval(async () => {
      const s = dragState.value
      if (!s || s.detachTriggered) {
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
        const outsideX =
          cursor.x < mainBounds.x + DETACH_EDGE_MARGIN ||
          cursor.x > mainBounds.x + mainBounds.width - DETACH_EDGE_MARGIN
        const outsideY =
          cursor.y < mainBounds.y + DETACH_EDGE_MARGIN ||
          cursor.y > mainBounds.y + mainBounds.height - DETACH_EDGE_MARGIN
        if (outsideX || outsideY) {
          s.detachTriggered = true
          stopCursorPolling()
          dropTarget.value = null
          const tabId = s.tabId
          dragState.value = null
          document.removeEventListener('mousemove', onDragMouseMove)
          document.removeEventListener('mouseup', onDragMouseUp)
          const opts = pendingOpts
          pendingOpts = null
          opts?.onDetach(tabId)
        }
      } catch {
        stopCursorPolling()
      }
    }, 50)
  }

  function stopCursorPolling(): void {
    if (pollTimer) {
      clearInterval(pollTimer)
      pollTimer = null
    }
    if (dragState.value) dragState.value.polling = false
  }

  function onDragMouseUp(): void {
    const state = dragState.value
    const target = state?.dragging ? dropTarget.value : null
    document.removeEventListener('mousemove', onDragMouseMove)
    document.removeEventListener('mouseup', onDragMouseUp)
    stopCursorPolling()
    const opts = pendingOpts
    const tabId = state?.tabId ?? ''
    pendingOpts = null
    dragState.value = null
    dropTarget.value = null
    opts?.onEnd?.(target, tabId)
  }

  function cancel(): void {
    document.removeEventListener('mousemove', onDragMouseMove)
    document.removeEventListener('mouseup', onDragMouseUp)
    stopCursorPolling()
    pendingOpts = null
    dragState.value = null
    dropTarget.value = null
  }

  return {
    dragState,
    dropTarget,
    isDraggingTab,
    startDrag,
    cancel,
    registerDock,
  }
}

/** Test-only: clear the module-singleton drag state. */
export function __resetDockDragForTests(): void {
  dragState.value = null
  dropTarget.value = null
  dockRegistrations.clear()
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}
