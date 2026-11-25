import { onMounted, onUnmounted, ref, computed, watch, type Ref } from 'vue'

/**
 * Tracks the width and height of a DOM element using ResizeObserver,
 * rounding to integer CSS pixels and coalescing bursts on rAF to avoid
 * ResizeObserver feedback loops across fractional DPR displays.
 *
 * Usage:
 * ```ts
 * const el = ref<HTMLElement | null>(null)
 * const { width, height } = useElementSize(el)
 * ```
 */
export function useElementSize(target: Ref<HTMLElement | null>) {
  const width = ref(0)
  const height = ref(0)
  let observer: ResizeObserver | null = null
  let rafId: number | null = null
  let pendingW = 0
  let pendingH = 0

  function applySize(rawW: number, rawH: number) {
    const nextW = Math.max(0, Math.round(rawW))
    const nextH = Math.max(0, Math.round(rawH))
    if (width.value !== nextW) width.value = nextW
    if (height.value !== nextH) height.value = nextH
  }

  function flushPending() {
    rafId = null
    applySize(pendingW, pendingH)
  }

  onMounted(() => {
    if (!target.value) return

    observer = new ResizeObserver((entries) => {
      const entry = entries[entries.length - 1]
      if (!entry) return
      pendingW = entry.contentRect.width
      pendingH = entry.contentRect.height
      if (typeof requestAnimationFrame === 'undefined') {
        flushPending()
        return
      }
      if (rafId !== null) cancelAnimationFrame(rafId)
      rafId = requestAnimationFrame(flushPending)
    })

    observer.observe(target.value)

    // Set initial size synchronously (rounded) so initial layout has integer measurements.
    const rect = target.value.getBoundingClientRect()
    applySize(rect.width, rect.height)
  })

  onUnmounted(() => {
    if (rafId !== null && typeof cancelAnimationFrame !== 'undefined') {
      cancelAnimationFrame(rafId)
      rafId = null
    }
    observer?.disconnect()
    observer = null
  })

  return { width, height }
}

/**
 * Responsive breakpoint helper for panel widths.
 *
 * Breakpoint strategy (inspired by Material Design and Ant Design):
 * - **normal** (≥ 420px): Full layout with labels, descriptions, multi-column grids.
 * - **compact** (340–419px): Icon-only tabs, single-column grids, reduced padding.
 * - **ultra** (< 340px): Extreme compact — hide secondary actions, minimal padding.
 *
 * Applies a 10px hysteresis deadband (±5px around 340 and 420) after the initial
 * non-zero measurement so subpixel or scrollbar width changes near a boundary
 * cannot oscillate between modes.
 */
export type ResponsiveMode = 'normal' | 'compact' | 'ultra'

export function useResponsiveMode(target: Ref<HTMLElement | null>) {
  const { width } = useElementSize(target)
  const mode = ref<ResponsiveMode>('normal')
  let measuredOnce = false

  watch(
    width,
    (w) => {
      if (w === 0) {
        mode.value = 'normal'
        return
      }
      if (!measuredOnce) {
        measuredOnce = true
        mode.value = w < 340 ? 'ultra' : w < 420 ? 'compact' : 'normal'
        return
      }
      const cur = mode.value
      if (cur === 'ultra') {
        if (w >= 345) mode.value = w >= 425 ? 'normal' : 'compact'
      } else if (cur === 'compact') {
        if (w <= 335) mode.value = 'ultra'
        else if (w >= 425) mode.value = 'normal'
      } else {
        if (w <= 335) mode.value = 'ultra'
        else if (w <= 415) mode.value = 'compact'
      }
    },
    { immediate: true },
  )

  const isCompact = computed(() => mode.value !== 'normal')

  return { width, mode, isCompact }
}

/**
 * Smart tab-label visibility calculator.
 *
 * Instead of a fixed breakpoint (e.g. "panel < 420px → hide all labels"),
 * this dynamically computes whether there is enough horizontal space to
 * show text labels for all tabs, based on the actual number of tabs.
 *
 * Space model (all in px):
 * - Fixed elements in the tab bar: home button, add button, collapse button,
 *   bar padding/gaps ≈ 110px.
 * - Each tab WITH a visible label ≈ 120px (icon 14 + gap 5 + label ~50-70 +
 *   close btn 16 + padding 18 + margin).
 * - Each tab icon-only ≈ 38px (icon 14 + padding 18 + gap).
 *
 * Modes:
 * - **full**: All tabs show their labels.
 * - **active-only**: Only the active tab shows its label; others are icon-only.
 * - **hidden**: All tabs are icon-only (not enough room even for one label).
 */
export type TabLabelMode = 'full' | 'active-only' | 'hidden'

export function useTabLabelMode(
  panelWidth: Ref<number>,
  tabCount: Ref<number>,
  detachedCount: Ref<number> = ref(0),
) {
  // Fixed elements that always occupy space in the tab bar
  const FIXED_ELEMENTS_WIDTH = 110

  // Estimated width per tab
  const TAB_WITH_LABEL = 120
  const TAB_ICON_ONLY = 38

  return computed<TabLabelMode>(() => {
    const w = panelWidth.value
    if (w === 0) return 'full' // Before measurement, assume full

    const totalTabs = tabCount.value + detachedCount.value
    const available = w - FIXED_ELEMENTS_WIDTH

    // If no tabs, labels don't matter
    if (totalTabs === 0) return 'full'

    // Can all tabs fit with full labels?
    if (totalTabs * TAB_WITH_LABEL <= available) {
      return 'full'
    }

    // Can all tabs fit as icon-only, with room for one label?
    const iconOnlyWidth = (totalTabs - 1) * TAB_ICON_ONLY + TAB_WITH_LABEL
    if (iconOnlyWidth <= available) {
      return 'active-only'
    }

    // Can all tabs fit as icon-only at least?
    if (totalTabs * TAB_ICON_ONLY <= available) {
      return 'hidden'
    }

    // Even icon-only doesn't fit — still use hidden (tab bar will scroll)
    return 'hidden'
  })
}

/**
 * Responsive mode for the **conversation / chat** area (not the narrow sidebar).
 *
 * The chat area is the space between the left sidebar (260px) and the right
 * feature panel. Its effective width can vary dramatically depending on window
 * size and right-panel state, so we use different breakpoints than the panel.
 *
 * Breakpoint strategy:
 * - **normal** (≥ 560px): Full layout with labels, multi-element toolbars.
 * - **narrow** (400–559px): Hide selector labels and agent-config text,
 *   reduce paddings, keep toolbar on one row but icon-only.
 * - **ultra** (< 400px): Stack toolbar vertically, minimal padding,
 *   user messages take full width.
 *
 * Applies a 12px hysteresis deadband (±6px around 400 and 560) after the
 * first non-zero measurement so layout changes caused by switching modes
 * (e.g. scrollbar toggling) cannot oscillate back across the threshold.
 */
export type ChatResponsiveMode = 'normal' | 'narrow' | 'ultra'

export function useChatResponsiveMode(target: Ref<HTMLElement | null>) {
  const { width } = useElementSize(target)
  const mode = ref<ChatResponsiveMode>('normal')
  let measuredOnce = false

  watch(
    width,
    (w) => {
      if (w === 0) {
        mode.value = 'normal'
        return
      }
      if (!measuredOnce) {
        measuredOnce = true
        mode.value = w < 400 ? 'ultra' : w < 560 ? 'narrow' : 'normal'
        return
      }
      const cur = mode.value
      if (cur === 'ultra') {
        if (w >= 406) mode.value = w >= 566 ? 'normal' : 'narrow'
      } else if (cur === 'narrow') {
        if (w <= 394) mode.value = 'ultra'
        else if (w >= 566) mode.value = 'normal'
      } else {
        if (w <= 394) mode.value = 'ultra'
        else if (w <= 554) mode.value = 'narrow'
      }
    },
    { immediate: true },
  )

  const isNarrow = computed(() => mode.value !== 'normal')

  return { width, mode, isNarrow }
}
