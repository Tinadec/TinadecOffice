import { describe, expect, it } from 'vitest'
import { classifyScroll } from './scrollIntent'

/**
 * The distinction this file exists for: a native scrollbar drag and the engine's own animation
 * both arrive as `scroll`, so "is the engine busy" cannot tell them apart. The target can.
 */
describe('classifyScroll', () => {
  it('calls it the reader when the engine is not scrolling', () => {
    expect(classifyScroll({ scrollTop: 120, previousScrollTop: 100, engineTarget: null })).toBe('reader')
  })

  it('calls movement toward an outstanding target the engine animating', () => {
    expect(classifyScroll({ scrollTop: 160, previousScrollTop: 100, engineTarget: 200 })).toBe('engine')
  })

  /** The case the flag could not see: a hand on the scrollbar mid-animation. */
  it('calls movement away from the target a reader dragging during an animation', () => {
    expect(classifyScroll({ scrollTop: 80, previousScrollTop: 100, engineTarget: 200 })).toBe('reader')
  })

  it('calls arriving at the target the engine finishing', () => {
    expect(classifyScroll({ scrollTop: 200, previousScrollTop: 190, engineTarget: 200 })).toBe('engine')
  })

  it('treats a sub-pixel difference as arrival', () => {
    expect(classifyScroll({ scrollTop: 199.8, previousScrollTop: 190, engineTarget: 200 })).toBe('engine')
  })

  /** A measurement with no movement is not evidence of intent in either direction. */
  it('leaves a stationary measurement with the engine', () => {
    expect(classifyScroll({ scrollTop: 100, previousScrollTop: 100, engineTarget: 200 })).toBe('engine')
  })

  it('detects a drag upward while the engine is animating downward', () => {
    expect(classifyScroll({ scrollTop: 40, previousScrollTop: 60, engineTarget: 300 })).toBe('reader')
  })

  it('detects a drag downward while the engine is animating upward', () => {
    expect(classifyScroll({ scrollTop: 240, previousScrollTop: 220, engineTarget: 0 })).toBe('reader')
  })
})
