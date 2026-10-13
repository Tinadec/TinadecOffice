// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import {
  closePalette,
  installPaletteKeybinding,
  openPalette,
  paletteIsOpen,
  togglePalette,
  useCommandPalette,
} from './useCommandPalette'
import { PALETTE_COMBO, __resetKeybindingsForTests, registeredCombos } from '@/lib/keybindings'

function pressCombo() {
  const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, cancelable: true })
  document.dispatchEvent(event)
  return event
}

beforeEach(() => {
  __resetKeybindingsForTests()
  closePalette()
})

afterEach(() => {
  __resetKeybindingsForTests()
  closePalette()
})

describe('useCommandPalette', () => {
  it('opens, closes and toggles the one window-wide flag', () => {
    openPalette()
    expect(paletteIsOpen()).toBe(true)
    togglePalette()
    expect(paletteIsOpen()).toBe(false)
    togglePalette()
    expect(paletteIsOpen()).toBe(true)
  })

  it('binds the gesture so a real keydown drives the state', () => {
    const uninstall = installPaletteKeybinding()
    openPalette()
    const event = pressCombo()
    expect(paletteIsOpen()).toBe(false)
    expect(event.defaultPrevented).toBe(true)
    pressCombo()
    expect(paletteIsOpen()).toBe(true)
    uninstall()
  })

  it('frees the gesture on uninstall, so a remount can claim it again', () => {
    const uninstall = installPaletteKeybinding()
    expect(registeredCombos()).toEqual([PALETTE_COMBO])
    uninstall()
    expect(registeredCombos()).toEqual([])
    expect(() => installPaletteKeybinding()).not.toThrow()
    __resetKeybindingsForTests()
  })

  it('exposes the label built from the same constant the binding used', () => {
    // The footer text and the live gesture must come from one source; a hand-written
    // "Ctrl+K" in the template would keep passing after the binding moved.
    const { comboLabel, open } = useCommandPalette()
    expect(open.value).toBe(false)
    expect(comboLabel.value.length).toBeGreaterThan(0)
    expect(comboLabel.value).toContain(PALETTE_COMBO.split('+').pop()!.toUpperCase())
  })
})
