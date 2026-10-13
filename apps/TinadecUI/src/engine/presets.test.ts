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

  it('every page has a non-empty preset', () => {
    for (const pageId of ['home', 'settings', 'market', 'code', 'debug'] as const) {
      const preset = buildPreset(pageId, { nextInstanceId: nextId() })
      expect(preset.pageId).toBe(pageId)
      expect(Object.keys(preset.cards).length).toBeGreaterThan(0)
      expect(preset.columnOrder.length).toBe(3)
      expect(typeof preset.edgeInset).toBe('number')
    }
  })

})
