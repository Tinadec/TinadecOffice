// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  PALETTE_COMBO,
  formatCombo,
  installGlobalKeybindings,
  isBareCombo,
  isEditableTarget,
  matchesCombo,
  parseCombo,
  registerKeybinding,
  registeredCombos,
  __resetKeybindingsForTests,
} from './keybindings'

function press(options: KeyboardEventInit): KeyboardEvent {
  const event = new KeyboardEvent('keydown', { cancelable: true, ...options })
  document.dispatchEvent(event)
  return event
}

beforeEach(() => {
  __resetKeybindingsForTests()
})

afterEach(() => {
  __resetKeybindingsForTests()
})

describe('parseCombo', () => {
  it('reads Mod, Shift and a plain key', () => {
    expect(parseCombo('Mod+Shift+P')).toEqual({
      key: 'p',
      mod: true,
      ctrl: false,
      meta: false,
      shift: true,
      alt: false,
    })
  })

  it('maps a named key onto its canonical form', () => {
    expect(parseCombo('Escape').key).toBe('escape')
    expect(parseCombo('Esc').key).toBe('escape')
    expect(parseCombo('Up').key).toBe('arrowup')
    expect(parseCombo('Space').key).toBe(' ')
  })

  it('refuses a combo with no key or an unknown modifier', () => {
    expect(() => parseCombo('Mod+')).toThrow('has no key')
    expect(() => parseCombo('Hyper+K')).toThrow('unknown modifier')
  })
})

describe('matchesCombo', () => {
  const modK = parseCombo(PALETTE_COMBO)

  it('accepts either primary modifier for Mod', () => {
    expect(matchesCombo(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }), modK)).toBe(true)
    expect(matchesCombo(new KeyboardEvent('keydown', { key: 'k', metaKey: true }), modK)).toBe(true)
  })

  it('rejects the bare key and a shifted variant it never claimed', () => {
    expect(matchesCombo(new KeyboardEvent('keydown', { key: 'k' }), modK)).toBe(false)
    expect(matchesCombo(new KeyboardEvent('keydown', { key: 'K', ctrlKey: true, shiftKey: true }), modK)).toBe(false)
  })

  it('keeps Ctrl+Tab and Mod+Tab distinct on a Mac-style press', () => {
    const ctrlTab = parseCombo('Ctrl+Tab')
    const metaPress = new KeyboardEvent('keydown', { key: 'Tab', metaKey: true })
    expect(matchesCombo(metaPress, ctrlTab)).toBe(false)
    expect(matchesCombo(new KeyboardEvent('keydown', { key: 'Tab', ctrlKey: true }), ctrlTab)).toBe(true)
  })

  it('does not answer to a modifier it does not name', () => {
    const plain = parseCombo('F9')
    expect(matchesCombo(new KeyboardEvent('keydown', { key: 'F9', altKey: true }), plain)).toBe(false)
    expect(matchesCombo(new KeyboardEvent('keydown', { key: 'F9' }), plain)).toBe(true)
  })
})

describe('formatCombo', () => {
  it('spells Mod for the platform it is shown on', () => {
    expect(formatCombo('Mod+K', true)).toBe('⌘K')
    expect(formatCombo('Mod+K', false)).toBe('Ctrl+K')
    expect(formatCombo('Mod+Shift+P', true)).toBe('⌘⇧P')
    expect(formatCombo('Mod+Shift+P', false)).toBe('Ctrl+Shift+P')
  })

  it('renders the palette gesture from the same constant the binding uses', () => {
    // The footer shows this string and the layer listens for PALETTE_COMBO; if the two
    // ever disagreed the hint would teach a gesture that does nothing.
    expect(formatCombo(PALETTE_COMBO, false)).toBe('Ctrl+K')
  })
})

describe('bare combos', () => {
  it('knows a bare key from a modified one', () => {
    expect(isBareCombo(parseCombo('F9'))).toBe(true)
    expect(isBareCombo(parseCombo('Mod+K'))).toBe(false)
  })

  it('recognises the fields a bare key would land in', () => {
    const input = document.createElement('input')
    const textarea = document.createElement('textarea')
    const editable = document.createElement('div')
    editable.contentEditable = 'true'
    const button = document.createElement('button')
    expect(isEditableTarget(input)).toBe(true)
    expect(isEditableTarget(textarea)).toBe(true)
    expect(isEditableTarget(editable)).toBe(true)
    expect(isEditableTarget(button)).toBe(false)
    expect(isEditableTarget(null)).toBe(false)
  })
})

describe('the layer', () => {
  it('runs the binding for a matching press and consumes the event', () => {
    const run = vi.fn()
    registerKeybinding(PALETTE_COMBO, run)
    installGlobalKeybindings()
    const event = press({ key: 'k', ctrlKey: true })
    expect(run).toHaveBeenCalledTimes(1)
    expect(event.defaultPrevented).toBe(true)
  })

  it('does not run anything without a matching press', () => {
    const run = vi.fn()
    registerKeybinding(PALETTE_COMBO, run)
    installGlobalKeybindings()
    press({ key: 'k' })
    expect(run).not.toHaveBeenCalled()
  })

  it('leaves an event another handler already took alone', () => {
    const run = vi.fn()
    registerKeybinding(PALETTE_COMBO, run)
    installGlobalKeybindings()
    const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true, cancelable: true })
    event.preventDefault()
    document.dispatchEvent(event)
    expect(run).not.toHaveBeenCalled()
  })

  it('ignores a held key repeating into the binding', () => {
    const run = vi.fn()
    registerKeybinding(PALETTE_COMBO, run)
    installGlobalKeybindings()
    press({ key: 'k', ctrlKey: true, repeat: true })
    expect(run).not.toHaveBeenCalled()
  })

  it('hands a bare key to the field being typed in, unless the binding insists', () => {
    const steal = vi.fn()
    const insisted = vi.fn()
    registerKeybinding('F9', steal)
    installGlobalKeybindings()
    const input = document.createElement('input')
    document.body.append(input)
    input.focus()
    const event = new KeyboardEvent('keydown', { key: 'F9', cancelable: true, bubbles: true })
    input.dispatchEvent(event)
    expect(steal).not.toHaveBeenCalled()
    expect(event.defaultPrevented).toBe(false)

    __resetKeybindingsForTests()
    registerKeybinding('F9', insisted, () => true)
    installGlobalKeybindings()
    const forced = new KeyboardEvent('keydown', { key: 'F9', cancelable: true, bubbles: true })
    input.dispatchEvent(forced)
    expect(insisted).toHaveBeenCalledTimes(1)
    input.remove()
  })

  it('refuses a second claim of one combo instead of overriding silently', () => {
    registerKeybinding(PALETTE_COMBO, () => {})
    expect(() => registerKeybinding(PALETTE_COMBO, () => {})).toThrow('already registered')
    expect(registeredCombos()).toEqual([PALETTE_COMBO])
  })

  it('frees the combo again when the owner unsubscribes', () => {
    const unsubscribe = registerKeybinding(PALETTE_COMBO, () => {})
    unsubscribe()
    expect(() => registerKeybinding(PALETTE_COMBO, () => {})).not.toThrow()
  })

  it('stops listening only when the last owner has left', () => {
    const run = vi.fn()
    registerKeybinding(PALETTE_COMBO, run)
    const detachFirst = installGlobalKeybindings()
    const detachSecond = installGlobalKeybindings()
    detachFirst()
    press({ key: 'k', ctrlKey: true })
    expect(run).toHaveBeenCalledTimes(1)
    detachSecond()
    press({ key: 'k', ctrlKey: true })
    expect(run).toHaveBeenCalledTimes(1)
  })
})
