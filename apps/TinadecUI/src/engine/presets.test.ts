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

  it('companion center preset keeps the shared left nav card and clears legacy companion cards', () => {
    const preset = buildPreset('companionCenter', { nextInstanceId: nextId() })
    expect(preset.pageId).toBe('companionCenter')
    expect(preset.gap).toBe(8)
    expect(preset.edgeInset).toBe(8)
    expect(preset.columns.left.width).toBe(260)
    expect(preset.columns.center.surfaceMode).toBe('immersive')
    expect(preset.columns.right.width).toBe(380)

    const descriptors = (slot: 'left' | 'center' | 'right') =>
      preset.columns[slot].primary.tabIds.map((id) => preset.cards[id].descriptorId)
    expect(descriptors('left')).toEqual(['nav'])
    expect(descriptors('center')).toEqual([])
    expect(descriptors('right')).toEqual([])
    expect(Object.keys(preset.cards)).toHaveLength(1)
  })

  it('board and projectHub presets host dedicated center cards, empty 460px right slot, and zero chat tools', () => {
    for (const [pageId, centerDescriptor] of [
      ['board', 'globalBoard'],
      ['projectHub', 'projectHub'],
    ] as const) {
      const preset = buildPreset(pageId, { nextInstanceId: nextId() })
      expect(preset.pageId).toBe(pageId)
      expect(preset.presetRevision).toBe(1)
      expect(preset.gap).toBe(8)
      expect(preset.edgeInset).toBe(8)

      expect(preset.columns.left.width).toBe(260)
      expect(preset.columns.left.topInset).toBe(8)
      expect(preset.columns.left.surfaceMode).toBe('float')

      expect(preset.columns.center.topInset).toBe(48)
      expect(preset.columns.center.surfaceMode).toBe('float')

      expect(preset.columns.right.width).toBe(460)
      expect(preset.columns.right.topInset).toBe(48)
      expect(preset.columns.right.surfaceMode).toBe('float')
      expect(preset.columns.right.collapsed).toBe(false)
      expect(preset.columns.right.dock).toBeNull()
      expect(preset.columns.right.primary.tabIds).toEqual([])
      expect(preset.columns.right.primary.activeTabId).toBeNull()

      const descriptors = (slot: 'left' | 'center' | 'right') =>
        preset.columns[slot].primary.tabIds.map((id) => preset.cards[id].descriptorId)
      expect(descriptors('left')).toEqual(['nav'])
      expect(descriptors('center')).toEqual([centerDescriptor])
      expect(descriptors('right')).toEqual([])

      const allDescriptors = Object.values(preset.cards).map((c) => c.descriptorId)
      expect(allDescriptors.sort()).toEqual([centerDescriptor, 'nav'].sort())
      expect(allDescriptors).not.toContain('homePicker')
      expect(allDescriptors).not.toContain('chat')
      expect(allDescriptors).not.toContain('git')
      expect(allDescriptors).not.toContain('terminal')
      expect(preset.focusedCardId).toBe(preset.columns.center.primary.activeTabId)
    }
  })

  it('every page has a non-empty preset', () => {
    for (const pageId of ['home', 'board', 'projectHub', 'settings', 'code', 'debug', 'companionCenter'] as const) {
      const preset = buildPreset(pageId, { nextInstanceId: nextId() })
      expect(preset.pageId).toBe(pageId)
      expect(Object.keys(preset.cards).length).toBeGreaterThan(0)
      expect(preset.columnOrder.length).toBe(3)
      expect(typeof preset.edgeInset).toBe('number')
    }
  })

})
