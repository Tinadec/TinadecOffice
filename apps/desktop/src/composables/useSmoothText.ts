import { onScopeDispose, ref, watch, type Ref } from 'vue'

/**
 * Paces a streaming answer so it reads as a steady reveal instead of bursts: about 90
 * characters a second, catching up within 0.3s when the stream gets ahead, and flushing
 * the rest within 0.12s once the stream ends. Text that is not streaming when first seen
 * (history) is shown whole on the first frame.
 *
 * Only the pace is decided here. The revealed prefix goes through `MarkdownRender`
 * unchanged, so parsing and sanitising stay where they are.
 */
export interface SmoothTextOptions {
  charsPerSecond?: number
  catchUpSeconds?: number
  flushSeconds?: number
  frame?: (callback: (time: number) => void) => unknown
  cancelFrame?: (handle: unknown) => void
}

export function useSmoothText(target: Ref<string>, streaming: Ref<boolean>, options: SmoothTextOptions = {}): Readonly<Ref<string>> {
  const rate = options.charsPerSecond ?? 90
  const catchUp = options.catchUpSeconds ?? 0.3
  const flush = options.flushSeconds ?? 0.12
  const frame = options.frame ?? ((callback) => requestAnimationFrame(callback))
  const cancelFrame = options.cancelFrame ?? ((handle) => cancelAnimationFrame(handle as number))

  const shown = ref(streaming.value ? '' : target.value)
  let revealed = shown.value.length
  let handle: unknown = null
  let last: number | null = null
  // The rate is fixed when the reveal falls behind (or the stream ends) and held until it
  // has caught up; recomputing it from the shrinking backlog every frame would decay
  // geometrically and never arrive on time.
  let pace = rate
  let flushing = false

  function tick(time: number) {
    handle = null
    const text = target.value
    // A rewritten stream (retry, replaced preview) restarts from what still matches.
    if (!text.startsWith(shown.value)) revealed = Math.min(revealed, commonPrefix(text, shown.value))
    const backlog = text.length - revealed
    if (backlog <= 0) {
      shown.value = text
      last = null
      pace = rate
      flushing = false
      return
    }
    const elapsed = last === null ? 1 / 60 : Math.max(0, (time - last) / 1000)
    last = time
    if (!streaming.value && !flushing) {
      flushing = true
      pace = Math.max(pace, backlog / flush)
    } else if (backlog / pace > catchUp) {
      pace = backlog / catchUp
    }
    revealed = Math.min(text.length, revealed + Math.max(1, Math.ceil(pace * elapsed)))
    shown.value = text.slice(0, revealed)
    handle = frame(tick)
  }

  function schedule() {
    if (handle === null) handle = frame(tick)
  }

  watch([target, streaming], schedule)
  onScopeDispose(() => {
    if (handle !== null) cancelFrame(handle)
  })
  return shown
}

function commonPrefix(a: string, b: string): number {
  let i = 0
  while (i < a.length && i < b.length && a[i] === b[i]) i += 1
  return i
}
