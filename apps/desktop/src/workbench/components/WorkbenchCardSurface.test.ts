// @vitest-environment happy-dom

import { defineComponent, h } from 'vue'
import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import WorkbenchCardSurface from './WorkbenchCardSurface.vue'
import type { PersistedCardInstance, WorkbenchCardDescriptor } from '../types'

function descriptor(type: string): WorkbenchCardDescriptor {
  const component = defineComponent(() => () => h('div', type))
  return {
    type,
    title: type,
    icon: component,
    component,
    minWidth: 180,
    minHeight: 160,
    singleton: true,
    movable: true,
    closable: true,
    detachable: false,
  }
}

describe('WorkbenchCardSurface tabs', () => {
  it('reorders a tab with Pointer Events and supports keyboard reordering', async () => {
    const first: PersistedCardInstance = { id: 'first', type: 'first.card', state: {} }
    const second: PersistedCardInstance = { id: 'second', type: 'second.card', state: {} }
    const firstDescriptor = descriptor(first.type)
    const secondDescriptor = descriptor(second.type)
    const wrapper = mount(WorkbenchCardSurface, {
      props: {
        card: first,
        descriptor: firstDescriptor,
        rect: { x: 0, y: 0, width: 500, height: 400 },
        stackCards: [first, second],
        slotId: 'center',
        stackId: 'primary',
        hasSplit: false,
        descriptors: new Map([[first.type, firstDescriptor], [second.type, secondDescriptor]]),
        active: true,
        pageActive: true,
        locked: false,
        pageId: 'home',
        panelStyle: {},
        panelDataAttributes: {},
      },
    })
    const tabs = wrapper.findAll('[data-workbench-tab]')
    tabs.forEach((tab, index) => {
      Object.defineProperty(tab.element, 'setPointerCapture', { value: vi.fn() })
      vi.spyOn(tab.element, 'getBoundingClientRect').mockReturnValue({
        x: index * 100,
        y: 0,
        left: index * 100,
        top: 0,
        right: index * 100 + 100,
        bottom: 30,
        width: 100,
        height: 30,
        toJSON: () => ({}),
      })
    })

    await tabs[1].trigger('pointerdown', { button: 0, pointerId: 9, clientX: 150 })
    await tabs[1].trigger('pointermove', { pointerId: 9, clientX: 10 })
    await tabs[1].trigger('pointerup', { pointerId: 9, clientX: 10 })
    expect(wrapper.emitted('move')?.[0]).toEqual(['second', 'center', 'primary', 0])

    await tabs[0].trigger('keydown', { key: 'ArrowRight', ctrlKey: true, shiftKey: true })
    expect(wrapper.emitted('move')?.[1]).toEqual(['first', 'center', 'primary', 1])
    expect(tabs[0].attributes('tabindex')).toBe('0')
    expect(tabs[1].attributes('tabindex')).toBe('-1')
  })
})
