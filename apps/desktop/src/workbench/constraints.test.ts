import { describe, expect, it } from 'vitest'
import { resolveWorkbenchLayout } from './constraints'
import { createWorkbenchPreset } from './presets'

describe('resolveWorkbenchLayout', () => {
  it('preserves the default three-column geometry at the desktop minimum size', () => {
    const resolved = resolveWorkbenchLayout(createWorkbenchPreset('home'), { width: 1120, height: 720 })
    expect(resolved.columns).toHaveLength(3)
    expect(resolved.columns.every((column) => !column.autoCollapsed)).toBe(true)
    expect(resolved.columns.reduce((sum, column) => sum + column.rect.width, 0)).toBeLessThanOrEqual(1088)
  })

  it('lets Settings content span the remaining width without a gap for its zero-width right slot', () => {
    const resolved = resolveWorkbenchLayout(createWorkbenchPreset('settings'), { width: 1120, height: 720 })
    const left = resolved.columns.find((column) => column.slotId === 'left')!
    const center = resolved.columns.find((column) => column.slotId === 'center')!
    const right = resolved.columns.find((column) => column.slotId === 'right')!
    expect(right.rect.width).toBe(0)
    expect(left.rect.x + left.rect.width + 8).toBe(center.rect.x)
    expect(center.rect.x + center.rect.width).toBe(1120 - 8)
  })

  it('temporarily collapses the right column before the left column', () => {
    const rightOnly = resolveWorkbenchLayout(createWorkbenchPreset('home'), { width: 760, height: 720 })
    expect(rightOnly.columns.find((column) => column.slotId === 'right')?.autoCollapsed).toBe(true)
    expect(rightOnly.columns.find((column) => column.slotId === 'left')?.autoCollapsed).toBe(false)

    const both = resolveWorkbenchLayout(createWorkbenchPreset('home'), { width: 440, height: 720 })
    expect(both.columns.find((column) => column.slotId === 'right')?.autoCollapsed).toBe(true)
    expect(both.columns.find((column) => column.slotId === 'left')?.autoCollapsed).toBe(true)
  })

  it('merges a secondary stack only in the resolved layout when height is insufficient', () => {
    const preset = createWorkbenchPreset('debug')
    const short = resolveWorkbenchLayout(preset, { width: 1440, height: 300 })
    const center = short.columns.find((column) => column.slotId === 'center')
    expect(center?.mergedSecondary).toBe(true)
    expect(center?.stacks).toHaveLength(1)
    expect(preset.columns.center.secondary).not.toBeNull()
  })

  it('resolves the default browser-below-chat split as 65/35 and restores it after a temporary merge', () => {
    const preset = createWorkbenchPreset('home')
    preset.cards.browser = { id: 'browser', type: 'tool.browser', state: { url: 'https://example.com' } }
    preset.columns.center.secondary = { cardIds: ['browser'], activeCardId: 'browser' }

    const large = resolveWorkbenchLayout(preset, { width: 1440, height: 920 })
    const largeCenter = large.columns.find((column) => column.slotId === 'center')
    expect(largeCenter?.stacks).toHaveLength(2)
    expect((largeCenter?.stacks[0].rect.height ?? 0) / (largeCenter?.rect.height ?? 1)).toBeCloseTo(0.65, 2)

    const short = resolveWorkbenchLayout(preset, { width: 1440, height: 300 })
    expect(short.columns.find((column) => column.slotId === 'center')?.mergedSecondary).toBe(true)
    const restored = resolveWorkbenchLayout(preset, { width: 1440, height: 920 })
    expect(restored.columns.find((column) => column.slotId === 'center')?.stacks).toHaveLength(2)
    expect(preset.columns.center.secondary?.cardIds).toEqual(['browser'])
  })
})
