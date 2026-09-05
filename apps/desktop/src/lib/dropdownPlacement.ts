export interface DropdownPlacement {
  /** Always 'fixed' — the dropdown is teleported to <body>. */
  position: 'fixed'
  top?: string
  bottom?: string
  left: string
  minWidth?: string
  maxHeight?: string
  overflowY?: 'auto'
}

export interface DropdownTriggerRect {
  top: number
  bottom: number
  left: number
  width: number
}

export interface DropdownPlacementOptions {
  /** Estimated rendered dropdown height — drives the flip decision. */
  estimatedHeight?: number
  minWidth?: number
  /** Gap between the trigger edge and the dropdown. */
  gap?: number
  /** Minimum viewport side margin. */
  margin?: number
  maxHeightCap?: number
}

const DEFAULTS = {
  estimatedHeight: 220,
  minWidth: 0,
  gap: 6,
  margin: 8,
  maxHeightCap: 280,
} as const

/**
 * Fixed-position placement for a dropdown teleported to <body>, anchored to a
 * trigger rect. Opens downward when there is room below; flips above the
 * trigger when the space below is too small and above is larger — e.g. the
 * composer docked at the window bottom must open its menus upward.
 */
export function computeDropdownPlacement(
  rect: DropdownTriggerRect,
  viewportWidth: number,
  viewportHeight: number,
  options: DropdownPlacementOptions = {},
): DropdownPlacement {
  const { estimatedHeight, minWidth, gap, margin, maxHeightCap } = { ...DEFAULTS, ...options }
  const spaceBelow = viewportHeight - rect.bottom
  const spaceAbove = rect.top
  const flip = spaceBelow < estimatedHeight && spaceAbove > spaceBelow
  const left = Math.max(margin, Math.min(rect.left, viewportWidth - minWidth - margin))

  if (flip) {
    const available = Math.min(maxHeightCap, spaceAbove - gap - margin)
    return {
      position: 'fixed',
      bottom: `${viewportHeight - rect.top + gap}px`,
      left: `${left}px`,
      ...(minWidth ? { minWidth: `${minWidth}px` } : {}),
      ...(available > 24 ? { maxHeight: `${available}px`, overflowY: 'auto' } : {}),
    }
  }
  const available = Math.min(maxHeightCap, spaceBelow - gap - margin)
  return {
    position: 'fixed',
    top: `${rect.bottom + gap}px`,
    left: `${left}px`,
    ...(minWidth ? { minWidth: `${minWidth}px` } : {}),
    ...(available > 24 ? { maxHeight: `${available}px`, overflowY: 'auto' } : {}),
  }
}
