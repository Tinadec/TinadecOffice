// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import CommandPaletteButton from './CommandPaletteButton.vue'
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

describe('CommandPaletteButton', () => {
  afterEach(() => closePalette())

  it('names itself after the palette and announces the real binding', () => {
    const button = mount(CommandPaletteButton).get('[data-testid="command-palette-button"]')

    expect(button.attributes('aria-label')).toBe('palette.title')
    expect(button.attributes('aria-haspopup')).toBe('dialog')
    expect(button.attributes('aria-expanded')).toBe('false')
    // The shortcut comes from the binding owner, so a tooltip can never advertise a combo
    // this platform does not use, and the palette cannot be renamed away from its button.
    expect(button.attributes('aria-keyshortcuts')).toBe(formatCombo(PALETTE_COMBO))
    expect(button.attributes('title')).toBe(`palette.openWithShortcut:${formatCombo(PALETTE_COMBO)}`)
  })

  it('opens the window palette without owning a second one', async () => {
    const wrapper = mount(CommandPaletteButton)
    expect(paletteIsOpen()).toBe(false)

    await wrapper.get('[data-testid="command-palette-button"]').trigger('click')

    expect(paletteIsOpen()).toBe(true)
    expect(wrapper.get('[data-testid="command-palette-button"]').attributes('aria-expanded')).toBe('true')
    // The palette element and its keybinding installation stay single-owners of their
    // surface (a structure test across src/** enforces it); the button must only ask.
    expect(wrapper.findAll('dialog')).toHaveLength(0)
  })
})
