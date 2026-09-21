/**
 * The command palette's open state and the one gesture that toggles it.
 *
 * Module-level because the surface is per-window, not per-view: the shortcut has to
 * answer from Settings, from the market, from a detached panel, and each of those is a
 * different component tree. `App.vue` is the single place that installs the binding, so
 * a keystroke has one owner; this module holds the state it drives, which is the same
 * shape `useNotifications` and `sessionEventBus` already use.
 */
import { computed, ref } from 'vue'
import { PALETTE_COMBO, formatCombo, installGlobalKeybindings, registerKeybinding } from '@/lib/keybindings'

const open = ref(false)

export function paletteIsOpen(): boolean {
  return open.value
}

export function openPalette(): void {
  open.value = true
}

export function closePalette(): void {
  open.value = false
}

export function togglePalette(): void {
  open.value = !open.value
}

/**
 * Binds the palette gesture and attaches the document listener, returning one off
 * switch for both. Call it from a component scope, never at module load: a hot reload
 * that re-ran the registration without the cleanup would throw on the duplicate, and
 * that throw is the point - two owners of one keystroke is the bug this layer exists
 * to prevent.
 */
export function installPaletteKeybinding(): () => void {
  const unbind = registerKeybinding(PALETTE_COMBO, togglePalette)
  const detach = installGlobalKeybindings()
  return () => {
    unbind()
    detach()
  }
}

export function useCommandPalette() {
  return {
    open,
    /** The same string the binding was registered with, spelled for this platform. */
    comboLabel: computed(() => formatCombo(PALETTE_COMBO)),
    toggle: togglePalette,
    openPalette,
    closePalette,
  }
}
