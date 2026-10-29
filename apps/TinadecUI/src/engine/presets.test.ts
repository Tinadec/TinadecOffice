import { describe, it, expect } from 'vitest'
import { buildPreset } from './presets'

function nextId() {
  let i = 0
  return () => `preset-${++i}`
}

describe('built-in presets', () => {

  it('home preset has left/center/right columns in order', () => {
    const preset = buildPreset('home', { nextInstanceId: nextId() })
    expect(preset.columnOrder).toEqual(['left', 'center', 'right'])
    expect(Object.keys(preset.cards).length).toBeGreaterThan(0)
    expect(preset.focusedCardId).toBeTruthy()
  })

  it('right column hosts the home picker first then feature cards', () => {
    const preset = buildPreset('home', { nextInstanceId: nextId() })
    const rightTabs = preset.columns.right.primary.tabIds
    const firstId = rightTabs[0]
    expect(preset.cards[firstId].descriptorId).toBe('homePicker')
    expect(rightTabs.length).toBeGreaterThan(1)
  })

  it('settings preset has a non-collapsible nav/content and locked left', () => {
    const preset = buildPreset('settings', { nextInstanceId: nextId() })
    const leftTabs = preset.columns.left.primary.tabIds
    expect(leftTabs.length).toBe(1)
    expect(preset.cards[leftTabs[0]].descriptorId).toBe('settingsNav')
    const centerTabs = preset.columns.center.primary.tabIds
    expect(preset.cards[centerTabs[0]].descriptorId).toBe('settingsContent')
    // right column collapsed (not used)
    expect(preset.columns.right.collapsed).toBe(true)
  })

  it('companion center preset lays out list, feed and the groups/connections stack', () => {
    const preset = buildPreset('companionCenter', { nextInstanceId: nextId() })
    expect(preset.pageId).toBe('companionCenter')
    expect(preset.gap).toBe(8)
    expect(preset.edgeInset).toBe(8)
    expect(preset.columns.left.width).toBe(280)
    expect(preset.columns.center.surfaceMode).toBe('immersive')
    expect(preset.columns.right.width).toBe(380)

    const descriptors = (slot: 'left' | 'center' | 'right') =>
      preset.columns[slot].primary.tabIds.map((id) => preset.cards[id].descriptorId)
    expect(descriptors('left')).toEqual(['companionList'])
    expect(descriptors('center')).toEqual(['companionFeed'])
    // Groups and connections share the right stack as two tabs.
    expect(descriptors('right')).toEqual(['companionGroups', 'companionConnections'])
    // Four cards, all singleton/movable/closable descriptors in the registry.
    expect(Object.keys(preset.cards)).toHaveLength(4)
  })

  it('every page has a non-empty preset', () => {
    for (const pageId of ['home', 'settings', 'market', 'code', 'debug', 'companionCenter'] as const) {
      const preset = buildPreset(pageId, { nextInstanceId: nextId() })
      expect(preset.pageId).toBe(pageId)
      expect(Object.keys(preset.cards).length).toBeGreaterThan(0)
      expect(preset.columnOrder.length).toBe(3)
      expect(typeof preset.edgeInset).toBe('number')
    }
  })

})
