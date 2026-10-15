// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { ref } from 'vue'
import { Globe } from '@lucide/vue'
import BrowserTabBar from '../../../TinadecUI/src/components/BrowserTabBar.vue'
import type { PersistedCardInstance } from '../../../TinadecUI/src/engine/types'

const drag = {
  isDraggingTab: vi.fn(() => false),
  startDrag: vi.fn(),
  cancel: vi.fn(),
}

vi.mock('@/composables/useDetachedTabs', () => ({
  useDetachedTabs: () => ({
    detachedTabs: ref([]),
    focus: vi.fn(),
  }),
}))

vi.mock('@/composables/useDockDrag', () => ({
  useDockDrag: () => drag,
}))

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  // No message table: tab titles fall back to the stored ones these cases look for.
  useI18n: () => ({ t: (key: string) => key, te: () => false, availableLocales: [] }),
}))

const home: PersistedCardInstance = {
  id: 'home',
  descriptorId: 'homePicker',
  title: 'Home',
}

const feature: PersistedCardInstance = {
  id: 'terminal-1',
  descriptorId: 'terminal',
  title: 'Terminal',
}

function mountTabs() {
  return mount(BrowserTabBar, {
    props: {
      instances: [home, feature],
      activeTabId: feature.id,
      iconFor: () => Globe,
      homeInstance: home,
    },
  })
}

describe('BrowserTabBar middle-click close', () => {
  afterEach(() => vi.clearAllMocks())

  it('closes a feature tab on middle click without activating it', async () => {
    const wrapper = mountTabs()
    const tab = wrapper.findAll('.browser-tab').find((candidate) => candidate.text().includes(feature.title))!
    const event = new MouseEvent('auxclick', { button: 1, bubbles: true, cancelable: true })
    tab.element.dispatchEvent(event)
    await wrapper.vm.$nextTick()

    expect(event.defaultPrevented).toBe(true)
    expect(wrapper.emitted('close')).toEqual([[feature.id]])
    expect(wrapper.emitted('activate')).toBeUndefined()
    wrapper.unmount()
  })

  it('keeps left-click selection and right-click detach behavior unchanged', async () => {
    const wrapper = mountTabs()
    const tab = wrapper.findAll('.browser-tab').find((candidate) => candidate.text().includes(feature.title))!

    await tab.trigger('click')
    expect(wrapper.emitted('activate')).toEqual([[feature.id]])

    await tab.trigger('contextmenu')
    expect(wrapper.emitted('detach')).toEqual([[feature.id]])
    wrapper.unmount()
  })

  it('never closes the pinned Home tab on a middle click', async () => {
    const wrapper = mountTabs()
    const tab = wrapper.find('.browser-tab-home')
    const event = new MouseEvent('auxclick', { button: 1, bubbles: true, cancelable: true })
    tab.element.dispatchEvent(event)
    await wrapper.vm.$nextTick()

    expect(event.defaultPrevented).toBe(true)
    expect(wrapper.emitted('close')).toBeUndefined()
    wrapper.unmount()
  })
})
