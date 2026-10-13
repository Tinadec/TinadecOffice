import { onScopeDispose, ref, watch, type Ref } from 'vue'

/**
 * A loading indicator that does not flicker: it appears only when the work is still
 * running after `delayMs`, and once shown it stays for at least `minimumMs` so a fast
 * finish does not flash it. Placeholders keep their space either way; this only decides
 * whether the spinner glyph is drawn.
 */
export function useDelayedLoading(
  loading: Ref<boolean>,
  { delayMs = 150, minimumMs = 400 }: { delayMs?: number; minimumMs?: number } = {},
): Readonly<Ref<boolean>> {
  const visible = ref(false)
  let showTimer: ReturnType<typeof setTimeout> | null = null
  let hideTimer: ReturnType<typeof setTimeout> | null = null
  let shownAt = 0

  const clear = () => {
    if (showTimer) clearTimeout(showTimer)
    if (hideTimer) clearTimeout(hideTimer)
    showTimer = hideTimer = null
  }

  watch(loading, (active) => {
    if (active) {
      if (hideTimer) {
        // Work resumed before the indicator left: keep it, cancel the exit.
        clearTimeout(hideTimer)
        hideTimer = null
        return
      }
      if (visible.value || showTimer) return
      showTimer = setTimeout(() => {
        showTimer = null
        visible.value = true
        shownAt = Date.now()
      }, delayMs)
      return
    }
    if (showTimer) {
      clearTimeout(showTimer)
      showTimer = null
    }
    if (!visible.value || hideTimer) return
    hideTimer = setTimeout(() => {
      hideTimer = null
      visible.value = false
    }, Math.max(0, minimumMs - (Date.now() - shownAt)))
  }, { immediate: true })

  onScopeDispose(clear)
  return visible
}
