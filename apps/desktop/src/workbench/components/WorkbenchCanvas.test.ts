// @vitest-environment happy-dom

import { defineComponent, h, onMounted, onUnmounted, type Component } from 'vue'
import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createWorkbenchPreset } from '../presets'
import WorkbenchCanvas from './WorkbenchCanvas.vue'
import type { WorkbenchCardDescriptor, WorkbenchLayoutSnapshot } from '../types'

class ResizeObserverStub {
  observe() {}
  disconnect() {}
}

function singleCardSnapshot(): WorkbenchLayoutSnapshot {
  const snapshot = createWorkbenchPreset('home')
  snapshot.cards = { test: { id: 'test', type: 'test.card', state: { draft: 'kept' } } }
  for (const column of Object.values(snapshot.columns)) {
    column.primary = { cardIds: [], activeCardId: null }
    column.secondary = null
  }
  snapshot.columns.center.primary = { cardIds: ['test'], activeCardId: 'test' }
  snapshot.focusedCardId = 'test'
  return snapshot
}

describe('WorkbenchCanvas surface pool', () => {
  let mountedCount = 0
  let unmountedCount = 0

  beforeEach(() => {
    mountedCount = 0
    unmountedCount = 0
    vi.stubGlobal('ResizeObserver', ResizeObserverStub)
  })

  afterEach(() => vi.unstubAllGlobals())

  it('keeps a card instance mounted while it moves or its page is hidden, then destroys it on close', async () => {
    const TestCard = defineComponent({
      setup() {
        onMounted(() => { mountedCount += 1 })
        onUnmounted(() => { unmountedCount += 1 })
        return () => h('textarea', { value: 'kept' })
      },
    })
    const descriptor: WorkbenchCardDescriptor = {
      type: 'test.card',
      title: 'Test card',
      icon: defineComponent(() => () => h('span')) as Component,
      component: TestCard,
      minWidth: 200,
      minHeight: 160,
      singleton: true,
      movable: true,
      closable: true,
      detachable: false,
    }
    const descriptors = new Map([[descriptor.type, descriptor]])
    const initial = singleCardSnapshot()
    const wrapper = mount(WorkbenchCanvas, {
      props: { pageId: 'home', snapshot: initial, descriptors, active: true },
    })
    await Promise.resolve()
    expect(mountedCount).toBe(1)

    const moved = structuredClone(initial)
    moved.columns.center.primary = { cardIds: [], activeCardId: null }
    moved.columns.right.primary = { cardIds: ['test'], activeCardId: 'test' }
    await wrapper.setProps({ snapshot: moved })
    await wrapper.setProps({ active: false })
    await wrapper.setProps({ active: true })
    expect(mountedCount).toBe(1)
    expect(unmountedCount).toBe(0)
    expect(wrapper.find('textarea').attributes('value')).toBe('kept')

    const closed = structuredClone(moved)
    closed.columns.right.primary = { cardIds: [], activeCardId: null }
    closed.cards = {}
    await wrapper.setProps({ snapshot: closed })
    expect(unmountedCount).toBe(1)
    wrapper.unmount()
  })
})
