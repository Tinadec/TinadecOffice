// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import AppHeader from './AppHeader.vue'
import { closePalette, paletteIsOpen } from '@/composables/useCommandPalette'
import { PALETTE_COMBO, formatCombo } from '@/lib/keybindings'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({
    t: (key: string, named?: unknown) =>
      named && typeof named === 'object' && 'combo' in named
        ? `${key}:${(named as { combo: string }).combo}`
        : key,
  }),
}))

function commandsButton() {
  const wrapper = mount(AppHeader)
  return { wrapper, button: wrapper.get('[data-testid="app-header-commands"]') }
}

describe('AppHeader command palette entry', () => {
  afterEach(() => closePalette())

  it('names itself after the palette and announces the real binding', () => {
    const { button } = commandsButton()

    expect(button.attributes('aria-label')).toBe('palette.title')
    expect(button.attributes('aria-haspopup')).toBe('dialog')
    expect(button.attributes('aria-expanded')).toBe('false')
    // The shortcut comes from the binding owner, so a tooltip can never advertise a combo
    // this platform does not use, and the palette cannot be renamed away from its button.
    expect(button.attributes('aria-keyshortcuts')).toBe(formatCombo(PALETTE_COMBO))
    expect(button.attributes('title')).toBe(`palette.openWithShortcut:${formatCombo(PALETTE_COMBO)}`)
  })

  it('opens the window palette without owning a second one', async () => {
    const { wrapper, button } = commandsButton()
    expect(paletteIsOpen()).toBe(false)

    await button.trigger('click')

    expect(paletteIsOpen()).toBe(true)
    expect(wrapper.get('[data-testid="app-header-commands"]').attributes('aria-expanded')).toBe('true')
    // The palette element and its keybinding installation stay single-owners of their
    // surface (a structure test across src/** enforces it); the header must only ask.
    expect(wrapper.findAll('dialog')).toHaveLength(0)
  })
})
