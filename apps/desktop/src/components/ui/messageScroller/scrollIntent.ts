/**
 * Who moved the viewport: the engine, or the reader.
 *
 * The engine already hears the reader through wheel, touch and key listeners, but a **native
 * scrollbar drag** produces only `scroll` events — the same event the engine's own scrolling
 * produces. Treating every `scroll` as the reader's stops the transcript from following a
 * streaming reply; treating every `scroll` as the engine's means a drag that ends a few pixels
 * above the bottom is snapped back down, because nothing recorded that a person put it there.
 *
 * The distinguishing fact is the target the engine asked for. While the viewport is closing in on
 * it, that movement is the engine's own animation — including the several events a smooth jump
 * emits. The moment the position moves *away* from it, the reader has taken the viewport back.
 * That is what makes a drag during the engine's own scroll detectable, which a plain "is the
 * engine busy" flag cannot do.
 */

/** Sub-pixel scroll positions are noise; everything below this is "the same place". */
export const SCROLL_EPSILON = 0.5

export type ScrollOrigin = 'engine' | 'reader'

export interface ScrollOriginInput {
  scrollTop: number
  previousScrollTop: number
  /** Where the engine asked the viewport to go, or null when it is not scrolling. */
  engineTarget: number | null
}

export function classifyScroll({ scrollTop, previousScrollTop, engineTarget }: ScrollOriginInput): ScrollOrigin {
  // No outstanding request: nobody but the reader could have moved it.
  if (engineTarget === null) return 'reader'
  // Arrived. The animation is over and the next movement is open to interpretation again.
  if (Math.abs(engineTarget - scrollTop) <= SCROLL_EPSILON) return 'engine'
  const delta = scrollTop - previousScrollTop
  // A measurement with no movement says nothing about intent; leave the request outstanding so a
  // later event can still resolve it.
  if (delta === 0) return 'engine'
  // Closing in is the engine animating; pulling away is a hand on the scrollbar.
  return Math.sign(delta) === Math.sign(engineTarget - scrollTop) ? 'engine' : 'reader'
}
