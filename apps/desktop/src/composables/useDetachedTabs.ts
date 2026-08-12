import { ref } from 'vue'

// ---------------------------------------------------------------------------
// useDetachedTabs — detached feature-panel window tracking for the UIE.
//
// The feature panel (right column stack) supports browser-style tab tearing:
// a tab can be dragged out of the main window into a floating BrowserWindow.
// The floating window is created by Electron (panelWindow.cjs) and renders via
// DetachedPanelPage.vue by `?type=`. This composable is the renderer-side
// module singleton that:
//   - maps UIE descriptor ids <-> the detached-window PanelType strings
//     (`browser` -> `preview`, the rest are identical),
//   - calls the `tinadec.detachPanel` IPC to spawn the window and tracks the
//     resulting `{ tabId, windowId }` as a dashed "detached indicator" tab,
//   - subscribes to reattach/closed IPC events so the indicator list stays in
//     sync with the real floating windows (bind() -> unsubscribe()).
//
// Like the legacy usePanelTabs.detachedTabs, this state is intentionally NOT
// part of the persisted layout snapshot — it is volatile window bookkeeping.
// The state is a module singleton so every consumer (the feature stack and its
// tab bar) observes the same indicator list.
// ---------------------------------------------------------------------------

/** A card instance that has been detached into a floating window. */
export interface DetachedTabInfo {
  /** The UIE card instance id (used to key the indicator tab). */
  tabId: string
  /** Detached-window PanelType string (e.g. 'preview'). */
  type: string
  title: string
  windowId: number
}

/** Reverse lookup: the PanelType string used by DetachedPanelPage for a UIE descriptor. */
const DETACHED_TYPE_BY_DESCRIPTOR: Record<string, string> = {
  browser: 'preview',
}

/** The PanelType string a UIE card descriptor detaches as (e.g. 'browser' -> 'preview'). */
export function detachedTypeForDescriptor(descriptorId: string): string {
  return DETACHED_TYPE_BY_DESCRIPTOR[descriptorId] ?? descriptorId
}

/** The UIE card descriptor a detached window's type reattaches to ('preview' -> 'browser'). */
export function descriptorForDetachedType(type: string): string {
  const entry = Object.entries(DETACHED_TYPE_BY_DESCRIPTOR).find(([, v]) => v === type)
  return entry ? entry[0] : type
}

/** Shape of the IPC reattach payload (matches preload `ReattachData`). */
export interface ReattachEvent {
  tabId: string
  type: string
  title: string
  state: Record<string, unknown>
}

const detachedTabs = ref<DetachedTabInfo[]>([])

export function useDetachedTabs() {
  /**
   * Detach a card into a floating BrowserWindow via Electron IPC.
   * The caller is responsible for removing the tab from its stack (closeCard);
   * this only spawns the window and records the indicator. Returns true when
   * the window was created.
   */
  async function detach(
    instance: { id: string; descriptorId: string; title: string },
    state?: Record<string, unknown>,
  ): Promise<boolean> {
    if (!window.tinadec?.detachPanel) return false
    const type = detachedTypeForDescriptor(instance.descriptorId)
    try {
      const result = await window.tinadec.detachPanel(instance.id, type, instance.title, state ?? {})
      if (!result) return false
      detachedTabs.value = [
        ...detachedTabs.value,
        { tabId: result.tabId, type, title: instance.title, windowId: result.windowId },
      ]
      return true
    } catch {
      return false
    }
  }

  function focus(windowId: number): void {
    window.tinadec?.focusPanelWindow?.(windowId)
  }

  function removeDetachedTab(tabId: string): void {
    detachedTabs.value = detachedTabs.value.filter((t) => t.tabId !== tabId)
  }

  /**
   * Install the IPC listeners that keep the indicator list in sync with the
   * real floating windows. Returns an unsubscribe function. Call from the
   * feature stack's onMounted and unsubscribe on onUnmounted.
   *
   * @param onReattach Optional callback fired when a floating window is
   *   closing to reattach, so the layout owner can open the card back up
   *   (the indicator is dropped here regardless).
   */
  function bind(onReattach?: (data: ReattachEvent) => void): () => void {
    if (typeof window === 'undefined') return () => {}
    const unsubs: Array<() => void> = []

    // A detached window was closed without reattach — drop its indicator.
    const offClosed = window.tinadec?.onPanelClosed?.((data) => {
      removeDetachedTab(data.tabId)
    })
    if (offClosed) unsubs.push(offClosed)

    // The floating window is closing to reattach — drop the indicator and let
    // the layout owner re-open the card.
    const offReattach = window.tinadec?.onPanelReattach?.((data) => {
      removeDetachedTab(data.tabId)
      onReattach?.(data)
    })
    if (offReattach) unsubs.push(offReattach)

    return () => unsubs.forEach((off) => off())
  }

  return {
    detachedTabs,
    detach,
    focus,
    removeDetachedTab,
    bind,
  }
}

/** Test-only: clear the module-singleton detached state. */
export function __resetDetachedTabsForTests(): void {
  detachedTabs.value = []
}
