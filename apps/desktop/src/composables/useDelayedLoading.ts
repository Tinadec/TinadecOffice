import { onScopeDispose, ref, watch } from 'vue'

/**
 * A loading flag that does not flicker.
 *
 * Two failure modes come from showing a spinner the moment work starts. A fast operation
 * paints a spinner for a frame or two, which reads as a glitch rather than as progress; and
 * an operation that finishes just after the delay keeps the spinner up for a beat after the
 * work is done, which reads as a hang.
 *
 * So: wait `delay` before showing anything, and once shown keep it for `minDuration`. Both
 * are cancellable, and the scope owns the timers.
 */
export interface DelayedLoadingOptions {
  /** Milliseconds of work before the spinner appears. Default 120. */
  delay?: number
  /** Milliseconds the spinner stays once shown. Default 400. */
  minDuration?: number
}

export function useDelayedLoading(
  loading: () => boolean,
  options: DelayedLoadingOptions = {},
) {
  const { delay = 120, minDuration = 400 } = options

  const visible = ref(false)
  let showTimer: ReturnType<typeof setTimeout> | null = null
  let hideTimer: ReturnType<typeof setTimeout> | null = null
  let shownAt = 0

  function clear() {
    if (showTimer !== null) { clearTimeout(showTimer); showTimer = null }
    if (hideTimer !== null) { clearTimeout(hideTimer); hideTimer = null }
  }

  function scheduleHide() {
    const elapsed = Date.now() - shownAt
    const remaining = minDuration - elapsed
    if (remaining <= 0) {
      visible.value = false
      return
    }
    hideTimer = setTimeout(() => { hideTimer = null; visible.value = false }, remaining)
  }

  watch(loading, (active) => {
    if (active) {
      if (hideTimer !== null) { clearTimeout(hideTimer); hideTimer = null }
      if (visible.value) return
      showTimer = setTimeout(() => { showTimer = null; shownAt = Date.now(); visible.value = true }, delay)
      return
    }
    if (showTimer !== null) { clearTimeout(showTimer); showTimer = null }
    if (visible.value) scheduleHide()
  }, { immediate: true })

  onScopeDispose(clear)

  return visible
}
