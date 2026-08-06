import { describe, it, expect } from 'vitest'
import { buildPreset, HOME_GEOMETRY, PRESET_BUILDERS } from './presets'

function nextId() {
  let i = 0
  return () => `preset-${++i}`
}

describe('built-in presets', () => {
  it('builds a home preset matching the pre-refactor geometry', () => {
    const preset = buildPreset('home', { nextInstanceId: nextId() })
    expect(preset.pageId).toBe('home')
    expect(preset.gap).toBe(HOME_GEOMETRY.gap)
    expect(HOME_GEOMETRY.gap).toBe(8)
    expect(preset.columns.left.width).toBe(HOME_GEOMETRY.leftWidth)
    expect(HOME_GEOMETRY.leftWidth).toBe(260)
    expect(preset.columns.right.width).toBe(HOME_GEOMETRY.rightWidth)
    expect(HOME_GEOMETRY.rightWidth).toBe(420)
    expect(preset.columns.left.topInset).toBe(HOME_GEOMETRY.leftTopInset)
    expect(preset.columns.right.topInset).toBe(HOME_GEOMETRY.rightTopInset)
    expect(HOME_GEOMETRY.leftTopInset).toBe(8)
    expect(HOME_GEOMETRY.rightTopInset).toBe(48)
    expect(preset.columns.left.surfaceMode).toBe('float')
    expect(preset.columns.right.surfaceMode).toBe('float')
  })

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

  it('market preset has gap 1 and three app columns', () => {
    const preset = buildPreset('market', { nextInstanceId: nextId() })
    expect(preset.gap).toBe(1)
    expect(preset.columns.left.width).toBe(292)
    expect(preset.columns.right.width).toBe(392)
    expect(preset.columns.left.surfaceMode).toBe('app')
  })

  it('every page has a non-empty preset', () => {
    for (const pageId of ['home', 'settings', 'market', 'code', 'debug'] as const) {
      const preset = buildPreset(pageId, { nextInstanceId: nextId() })
      expect(preset.pageId).toBe(pageId)
      expect(Object.keys(preset.cards).length).toBeGreaterThan(0)
      expect(preset.columnOrder.length).toBe(3)
    }
  })

  it('PRESET_BUILDERS covers every WorkbenchPageId', () => {
    expect(Object.keys(PRESET_BUILDERS).sort()).toEqual(['code', 'debug', 'home', 'market', 'settings'])
  })
})
