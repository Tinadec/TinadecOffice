// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import AppHeader from './AppHeader.vue'
import CommandPaletteButton from './CommandPaletteButton.vue'

vi.mock('vue-i18n', () => ({
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
  })
})
