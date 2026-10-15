// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import AppHeader from './AppHeader.vue'
import CommandPaletteButton from './CommandPaletteButton.vue'
import { UiButton } from './ui'

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key }),
}))

describe('AppHeader command palette entry', () => {
  // What the entry says and does is the shared button's own contract
  // (`CommandPaletteButton.test.ts`); this file only proves the live window chrome
  // actually renders it — the defect being guarded against is a header that quietly
  // drops the button, or re-inlines a second copy of its markup.
  it('renders the shared palette entry, not a private copy of it', () => {
    const wrapper = mount(AppHeader)

    expect(wrapper.findComponent(CommandPaletteButton).exists()).toBe(true)
    expect(wrapper.findAllComponents(CommandPaletteButton)).toHaveLength(1)
    wrapper.unmount()
  })
})

describe('AppHeader window controls', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('keeps the controls accessible and sends each click to its window IPC action', async () => {
    const controls = {
      minimizeWindow: vi.fn(),
      maximizeWindow: vi.fn(),
      closeWindow: vi.fn(),
    }
    vi.stubGlobal('tinadec', controls)
    const wrapper = mount(AppHeader)

    for (const [selector, action] of [
      ['.minimize', 'minimizeWindow'],
      ['.maximize', 'maximizeWindow'],
      ['.close', 'closeWindow'],
    ] as const) {
      const button = wrapper.get(selector)
      expect(button.attributes('aria-label')).toBe(button.attributes('title'))
      expect(button.attributes('aria-label')).toBeTruthy()
      expect(button.get('svg').attributes('aria-hidden')).toBe('true')
      expect(wrapper.findComponent<typeof UiButton>(selector).props('variant')).toBe('ghost')
      await button.trigger('click')
      expect(controls[action]).toHaveBeenCalledOnce()
    }
    wrapper.unmount()
  })
})
