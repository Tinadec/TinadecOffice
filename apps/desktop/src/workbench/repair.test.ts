import type { Component } from 'vue'
import { describe, expect, it } from 'vitest'
import { createWorkbenchPreset } from './presets'
import { repairWorkbenchLayout } from './repair'
import type { WorkbenchCardDescriptor } from './types'

const component = {} as Component

function descriptor(type: string, singleton = true): WorkbenchCardDescriptor {
  return {
    type,
    title: type,
    icon: component,
    component,
    minWidth: 100,
    minHeight: 100,
    singleton,
    movable: true,
    closable: true,
    detachable: false,
  }
}

describe('repairWorkbenchLayout', () => {
  it('falls back when the schema or page does not match', () => {
    expect(repairWorkbenchLayout({ version: 9 }, 'home')).toEqual(createWorkbenchPreset('home'))
    expect(repairWorkbenchLayout({ ...createWorkbenchPreset('code'), pageId: 'code' }, 'home'))
      .toEqual(createWorkbenchPreset('home'))
  })

  it('removes unknown cards, duplicate singleton instances, and invalid geometry', () => {
    const source = createWorkbenchPreset('home')
    source.cards.duplicate = { id: 'duplicate', type: 'home.chat', state: {} }
    source.cards.unknown = { id: 'unknown', type: 'unknown.card', state: {} }
    source.columns.left.primary.cardIds.push('duplicate', 'unknown')
    source.columns.left.width = Number.NaN
    source.columns.center.splitRatio = 9
    source.columnOrder = ['left', 'left', 'center'] as typeof source.columnOrder

    const descriptors = new Map([
      ['home.navigation', descriptor('home.navigation')],
      ['home.chat', descriptor('home.chat')],
      ['home.tools', descriptor('home.tools')],
    ])
    const repaired = repairWorkbenchLayout(source, 'home', descriptors)

    expect(repaired.cards.unknown).toBeUndefined()
    expect(Object.values(repaired.cards).filter((card) => card.type === 'home.chat')).toHaveLength(1)
    expect(repaired.columnOrder).toEqual(['left', 'center', 'right'])
    expect(repaired.columns.left.width).toBe(260)
    expect(repaired.columns.center.splitRatio).toBe(0.8)
  })

  it('always restores the fixed settings preset', () => {
    const settings = createWorkbenchPreset('settings')
    settings.columns.left.primary.cardIds = []
    settings.cards = {}
    expect(repairWorkbenchLayout(settings, 'settings')).toEqual(createWorkbenchPreset('settings'))
  })

  it('preserves only a valid Settings navigation width', () => {
    const settings = createWorkbenchPreset('settings')
    settings.columns.left.width = 336
    settings.columns.center.width = 999
    settings.columns.right.collapsed = false
    const repaired = repairWorkbenchLayout(settings, 'settings')
    expect(repaired.columns.left.width).toBe(336)
    expect(repaired.columns.center.width).toBe(createWorkbenchPreset('settings').columns.center.width)
    expect(repaired.columns.right.collapsed).toBe(true)

    settings.columns.left.width = 900
    expect(repairWorkbenchLayout(settings, 'settings').columns.left.width).toBe(480)
  })
})
