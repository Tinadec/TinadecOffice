/**
 * The window's global keyboard layer.
 *
 * Keystrokes in this app were handled in four unrelated `document` listeners, each
 * guarded by "is my own surface open". That worked, but it meant every new global
 * shortcut had to hope the others stepped aside. This module owns the gestures that
 * are global by intent - chiefly Mod+K - and it is the only place that decides which
 * binding wins when two claim the same combo.
 *
 * Deliberately small: no chord sequences, no per-contextual-scope stacks. A surface
 * that needs to intercept keys while it is open (the palette's own Escape) keeps
 * listening on its own element, because that is an element-local concern and faking
 * a focus stack here would be the larger lie.
 */

/**
 * The gesture that opens the command palette. It lives next to the parser rather than
 * in the component so the footer label and the live binding cannot disagree: the label
 * is this string run through `formatCombo`, and a test asserts exactly that.
 */
export const PALETTE_COMBO = 'Mod+K'

/** The whole modifier vocabulary. Everything below reads this list, so a combo cannot
 * be accepted by the parser and then fail to render in the footer. */
export type Modifier = 'mod' | 'ctrl' | 'meta' | 'shift' | 'alt'

const MODIFIERS: readonly Modifier[] = ['mod', 'ctrl', 'meta', 'shift', 'alt']

const DISPLAY_MODIFIER: Record<Modifier, { mac: string; other: string }> = {
  mod: { mac: '⌘', other: 'Ctrl+' },
  ctrl: { mac: 'Ctrl+', other: 'Ctrl+' },
  meta: { mac: '⌘', other: 'Win+' },
  shift: { mac: '⇧', other: 'Shift+' },
  alt: { mac: '⌥', other: 'Alt+' },
}

export interface ParsedCombo {
  key: string
  mod: boolean
  ctrl: boolean
  meta: boolean
  shift: boolean
  alt: boolean
}

export interface Keybinding {
  combo: ParsedCombo
  raw: string
  run(): void
  /** A binding may bow out at press time without unregistering. */
  when?(): boolean
}

const SPECIAL_KEYS: Record<string, string> = {
  escape: 'escape',
  esc: 'escape',
  enter: 'enter',
  space: ' ',
  tab: 'tab',
  backspace: 'backspace',
  delete: 'delete',
  up: 'arrowup',
  down: 'arrowdown',
  left: 'arrowleft',
  right: 'arrowright',
}

/**
 * `Mod` is the platform's primary modifier (Cmd on macOS, Ctrl elsewhere), so a
 * binding written once works on both. `Ctrl`/`Meta` stay literal for the gestures
 * that are spelled out per key on purpose.
 */
export function parseCombo(combo: string): ParsedCombo {
  const parts = combo.split('+')
  const key = parts.pop()
  if (!key) throw new Error(`keybinding "${combo}" has no key`)
  const mods = parts.map((part) => part.toLowerCase())
  const unknown = mods.filter((part) => !MODIFIERS.includes(part as Modifier))
  if (unknown.length) throw new Error(`keybinding "${combo}" has unknown modifier(s): ${unknown.join(', ')}`)
  const normalized = key.toLowerCase()
  return {
    key: SPECIAL_KEYS[normalized] ?? normalized,
    mod: mods.includes('mod'),
    ctrl: mods.includes('ctrl'),
    meta: mods.includes('meta'),
    shift: mods.includes('shift'),
    alt: mods.includes('alt'),
  }
}

function keyOf(event: KeyboardEvent): string {
  const raw = event.key.toLowerCase()
  return SPECIAL_KEYS[raw] ?? raw
}

/**
 * Every modifier is compared, not just the ones the combo names: a binding for
 * Mod+K must not answer to Mod+Shift+K, or the first registered shortcut silently
 * swallows the second.
 */
export function matchesCombo(event: KeyboardEvent, combo: ParsedCombo): boolean {
  if (combo.mod && !(event.metaKey || event.ctrlKey)) return false
  if (combo.ctrl && !event.ctrlKey) return false
  if (combo.meta && !event.metaKey) return false
  if (event.shiftKey !== combo.shift) return false
  if (event.altKey !== combo.alt) return false
  // A modifier the combo does not name may not be held, unless `Mod` already covers
  // it: Ctrl+Tab and Mod+Tab are different gestures on macOS.
  if (!combo.mod && !combo.ctrl && event.ctrlKey) return false
  if (!combo.mod && !combo.meta && event.metaKey) return false
  return keyOf(event) === combo.key
}

export function isMacPlatform(userAgent?: string): boolean {
  const ua = userAgent ?? (typeof navigator === 'undefined' ? '' : navigator.userAgent)
  return /Mac|iPhone|iPad/.test(ua)
}

const KEY_GLYPH: Record<string, string> = {
  ' ': 'Space',
  arrowup: '↑',
  arrowdown: '↓',
  arrowleft: '←',
  arrowright: '→',
  escape: 'Esc',
  enter: 'Enter',
  tab: 'Tab',
}

/** Renders a combo the way the footer should show it on this platform. */
export function formatCombo(combo: string, mac = isMacPlatform()): string {
  const parsed = parseCombo(combo)
  const tokens = combo.split('+').slice(0, -1)
  // parseCombo already rejected anything outside MODIFIERS, and the map is keyed
  // by that same union, so a new modifier cannot be added here without a glyph for it.
  const prefix = tokens
    .map((token) => DISPLAY_MODIFIER[token.toLowerCase() as Modifier])
    .map((entry) => (mac ? entry.mac : entry.other))
    .join('')
  const key = KEY_GLYPH[parsed.key] ?? parsed.key.toUpperCase()
  // Each non-mac token already carries its own "+", so joining is enough for both
  // spellings: "⌘⇧K" on macOS, "Ctrl+Shift+K" everywhere else.
  return `${prefix}${key}`
}

/**
 * A binding with no primary modifier is a bare key, and bare keys belong to whatever
 * has focus: the layer must not steal them from a field the user is typing into.
 */
export function isBareCombo(combo: ParsedCombo): boolean {
  return !combo.mod && !combo.ctrl && !combo.meta
}

export function isEditableTarget(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null
  if (!el || typeof el.tagName !== 'string') return false
  const tag = el.tagName.toLowerCase()
  if (tag === 'input' || tag === 'textarea' || tag === 'select') return true
  return el.isContentEditable === true
}

const bindings = new Map<string, Keybinding>()
let listeners = 0
let attached: (() => void) | null = null

function handleKeyDown(event: KeyboardEvent) {
  if (event.defaultPrevented || event.repeat) return
  for (const binding of bindings.values()) {
    if (!matchesCombo(event, binding.combo)) continue
    // A bare key while a field has focus is the field's, unless the binding says
    // otherwise through `when` - which is how a dialog keeps Escape.
    if (isBareCombo(binding.combo) && isEditableTarget(event.target) && binding.when?.() !== true) return
    if (binding.when && !binding.when()) continue
    event.preventDefault()
    binding.run()
    return
  }
}

/**
 * Registers `combo` and returns its off switch. A second claim of the same combo is
 * an error rather than a silent override: two surfaces answering one keystroke is the
 * bug this layer exists to prevent, and the registry is small enough to fix.
 */
export function registerKeybinding(combo: string, run: () => void, when?: () => boolean): () => void {
  const parsed = parseCombo(combo)
  const key = comboKey(combo)
  if (bindings.has(key)) {
    throw new Error(`keybinding "${combo}" is already registered; unregister it before binding it twice`)
  }
  bindings.set(key, { combo: parsed, raw: combo, run, when })
  return () => {
    if (bindings.get(key)?.run === run) bindings.delete(key)
  }
}

function comboKey(combo: string): string {
  const parsed = parseCombo(combo)
  return [
    parsed.mod ? 'mod' : '',
    parsed.ctrl ? 'ctrl' : '',
    parsed.meta ? 'meta' : '',
    parsed.shift ? 'shift' : '',
    parsed.alt ? 'alt' : '',
    parsed.key,
  ].join('+')
}

/** Attaches the document listener once per owner; the last owner detaches it. */
export function installGlobalKeybindings(): () => void {
  listeners++
  if (!attached) {
    const handler = (event: Event) => handleKeyDown(event as KeyboardEvent)
    document.addEventListener('keydown', handler)
    attached = () => document.removeEventListener('keydown', handler)
  }
  return () => {
    listeners = Math.max(0, listeners - 1)
    if (listeners === 0 && attached) {
      attached()
      attached = null
    }
  }
}

export function registeredCombos(): string[] {
  return [...bindings.values()].map((binding) => binding.raw)
}

/** Test-only: the registry is module state, and cases must not inherit it. */
export function __resetKeybindingsForTests(): void {
  bindings.clear()
  listeners = 0
  if (attached) {
    attached()
    attached = null
  }
}
