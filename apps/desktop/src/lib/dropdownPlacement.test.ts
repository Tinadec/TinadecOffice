import { describe, expect, it } from 'vitest'
import { computeDropdownPlacement } from './dropdownPlacement'

const VW = 1000
const VH = 800

describe('computeDropdownPlacement', () => {
  it('opens downward when there is room below', () => {
    const placement = computeDropdownPlacement(
      { top: 100, bottom: 130, left: 40, width: 60 },
      VW,
      VH,
    )
    expect(placement.top).toBe('136px')
    expect(placement.bottom).toBeUndefined()
  })

  it('flips above the trigger when the space below is too small', () => {
    // Composer docked at the window bottom: ~60px below, plenty above.
    const placement = computeDropdownPlacement(
      { top: 700, bottom: 730, left: 40, width: 60 },
      VW,
      VH,
    )
    expect(placement.bottom).toBe(`${VH - 700 + 6}px`)
    expect(placement.top).toBeUndefined()
  })

  it('keeps downward opening when below is small but above is smaller', () => {
    // Window bottom edge: almost no room on either side, but above is smaller.
    const placement = computeDropdownPlacement(
      { top: 2, bottom: 795, left: 40, width: 60 },
      VW,
      800,
      { estimatedHeight: 90 },
    )
    expect(placement.top).toBeDefined()
    expect(placement.bottom).toBeUndefined()
  })

  it('clamps the left edge into the viewport', () => {
    const placement = computeDropdownPlacement(
      { top: 100, bottom: 130, left: 990, width: 60 },
      VW,
      VH,
      { minWidth: 220 },
    )
    expect(placement.left).toBe(`${VW - 220 - 8}px`)
    expect(placement.minWidth).toBe('220px')
  })

  it('omits maxHeight when the available space is tiny', () => {
    // Both sides cramped (100px viewport, trigger at the bottom): flipping up
    // leaves only ~14px above, so no maxHeight/scroll hint is emitted.
    const placement = computeDropdownPlacement(
      { top: 28, bottom: 90, left: 40, width: 60 },
      VW,
      100,
    )
    expect(placement.bottom).toBeDefined()
    expect(placement.maxHeight).toBeUndefined()
    expect(placement.overflowY).toBeUndefined()
  })
})
