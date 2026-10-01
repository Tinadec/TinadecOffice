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

  it('has exactly one owner of the install call and one place that renders the surface', async () => {
    const sources = import.meta.glob(['../**/*.vue', '../**/*.ts'], {
      query: '?raw',
      import: 'default',
      eager: true,
    }) as Record<string, string>
    const installers: string[] = []
    const renderers: string[] = []
    for (const [path, source] of Object.entries(sources)) {
      if (path.endsWith('.test.ts')) continue
      if (path.includes('useCommandPalette')) continue
      if (source.includes('installPaletteKeybinding(')) installers.push(path)
      // The tag must end at the component name: `<CommandPaletteButton />` is the entry
      // that opens the surface, not a second rendering of it, and a plain substring
      // search would report every page that mounts the entry as a new dialog owner.
      if (/<CommandPalette(?![A-Za-z])/.test(source)) renderers.push(path)
    }
    // Two installs would throw at runtime on the duplicate combo; two renders would show
    // two dialogs. Both are worth a red rather than a comment.
    expect(installers).toEqual(['../App.vue'])
    expect(renderers).toEqual(['../App.vue'])
  })

  it('leaves no working surface keyboard-only', async () => {
    const pages = import.meta.glob(['../pages/*.vue'], {
      query: '?raw',
      import: 'default',
      eager: true,
    }) as Record<string, string>
    // An entry reaches the palette three ways, and all three are legitimate: the shared
    // button itself, `AppHeader` (which renders it), or `UieShell` (which renders
    // AppHeader) — Home and Chatroom, the two surfaces people actually live on, come
    // through the shell, so a scan for the button alone would call them bare.
    const missing = Object.entries(pages)
      .filter(([, source]) =>
        !source.includes('CommandPaletteButton')
        && !source.includes('AppHeader')
        && !source.includes('UieShell'))
      .map(([path]) => path.replace('../pages/', ''))
      .sort()
    // Exact match, so both directions are caught: a new page that ships without an
    // entry, and one of these four gaining an entry while still being listed here.
    // Pet: a desktop pet has no commands to run. Panel: a detached right-rail window
    // whose whole chrome is one reattach button. Debug Studio: a developer window.
    // Recovery check: a decision page with no action row, so an icon would be the
    // first button on a page that is asking a yes/no question — mouse entry pending
    // that layout, keyboard works there like everywhere else.
    expect(missing).toEqual([
      'DebugStudioPage.vue',
      'DesktopPetPage.vue',
      'DetachedPanelPage.vue',
      'RecoveryCheckPage.vue',
    ])
  })
})
