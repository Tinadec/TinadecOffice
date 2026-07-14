/**
 * useTerminal — Terminal state management composable.
 *
 * Manages multiple terminal instances, xterm.js integration, theme adaptation,
 * and the data flow between xterm.js (renderer) and node-pty (main process).
 *
 * Architecture:
 * - Each terminal has a unique ID from the main process.
 * - xterm.js Terminal instances are created lazily and attached to DOM elements.
 * - Data flow: xterm input → IPC write → pty stdin → pty stdout → IPC data → xterm write.
 * - Theme is adapted from CSS variables to xterm ITheme on changes.
 */

import { ref, computed, onUnmounted } from 'vue'
import type { Terminal } from '@xterm/xterm'
import type { ITheme as XtermTheme } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'

// ---- Types ----

export interface TerminalInstance {
  /** Unique terminal ID from main process */
  id: string
  /** Shell executable path */
  shell: string
  /** Display title */
  title: string
  /** xterm.js Terminal instance (lazy-initialized) */
  term: Terminal | null
  /** Fit addon for auto-sizing */
  fitAddon: FitAddon | null
  /** Whether the terminal process has exited */
  exited: boolean
  /** Whether the terminal is ready for interaction */
  ready: boolean
  /** Working directory */
  cwd: string
  /** Shell profile ID (e.g. 'pwsh', 'cmd', 'bash') */
  shellId: string
}

export interface ShellProfile {
  id: string
  label: string
  shell: string
  args: string[]
}

// ---- Theme mapping ----

/**
 * Read CSS custom properties from the document root and build an xterm ITheme.
 * This adapts the terminal colors to match the current app theme.
 */
function buildXtermTheme(): XtermTheme {
  const root = document.documentElement
  const getVar = (name: string, fallback: string): string => {
    const val = getComputedStyle(root).getPropertyValue(name).trim()
    return val || fallback
  }

  const isDark = root.getAttribute('data-theme') === 'dark'

  if (isDark) {
    return {
      background: getVar('--bg-primary', '#0a0e14'),
      foreground: getVar('--text-primary', '#c9d1d9'),
      cursor: getVar('--accent-primary', '#2ec4b6'),
      cursorAccent: getVar('--bg-primary', '#0a0e14'),
      selectionBackground: 'rgba(46, 196, 182, 0.25)',
      black: '#484f58',
      red: getVar('--text-error', '#f85149'),
      green: '#3fb950',
      yellow: '#d29922',
      blue: '#58a6ff',
      magenta: '#bc8cff',
      cyan: '#39c5cf',
      white: '#b1bac4',
      brightBlack: '#6e7681',
      brightRed: '#ff7b72',
      brightGreen: '#56d364',
      brightYellow: '#e3b341',
      brightBlue: '#79c0ff',
      brightMagenta: '#d2a8ff',
      brightCyan: '#56d4dd',
      brightWhite: '#f0f6fc',
    }
  } else {
    return {
      background: getVar('--bg-primary', '#ffffff'),
      foreground: getVar('--text-primary', '#1f2328'),
      cursor: getVar('--accent-primary', '#1f8f80'),
      cursorAccent: getVar('--bg-primary', '#ffffff'),
      selectionBackground: 'rgba(31, 143, 128, 0.2)',
      black: '#1f2328',
      red: '#cf222e',
      green: '#1a7f37',
      yellow: '#9a6700',
      blue: '#0969da',
      magenta: '#8250df',
      cyan: '#1b7c83',
      white: '#6e7781',
      brightBlack: '#656d76',
      brightRed: '#a40e26',
      brightGreen: '#2da44e',
      brightYellow: '#bf8700',
      brightBlue: '#218bff',
      brightMagenta: '#a475f9',
      brightCyan: '#3192aa',
      brightWhite: '#24292f',
    }
  }
}

// ---- Singleton terminal manager ----

const terminalInstances = ref<TerminalInstance[]>([])
const activeTerminalId = ref<string | null>(null)
const availableShells = ref<ShellProfile[]>([])
const shellsLoaded = ref(false)

/** Cleanup functions for IPC listeners, keyed by terminal ID */
const ipcCleanup = new Map<string, Array<() => void>>()

/**
 * Check if the Electron terminal API is available.
 */
function isTerminalAvailable(): boolean {
  return typeof window !== 'undefined'
    && !!window.tinadec
    && !!window.tinadec.terminal
}

/**
 * Load available shell profiles from Electron IPC.
 */
async function loadShells(): Promise<void> {
  if (!isTerminalAvailable()) {
    availableShells.value = []
    return
  }
  try {
    const shells = await window.tinadec.terminal.getShells()
    availableShells.value = shells
    shellsLoaded.value = true
  } catch {
    availableShells.value = []
  }
}

/**
 * Create a new terminal instance.
 *
 * @param options - Terminal creation options
 * @returns The terminal instance or null on failure
 */
async function createTerminalInstance(
  options: {
    shell?: string
    shellId?: string
    args?: string[]
    cwd?: string
    title?: string
    cols?: number
    rows?: number
  } = {},
): Promise<TerminalInstance | null> {
  // Ensure shells are loaded for default selection
  if (!shellsLoaded.value) {
    await loadShells()
  }

  try {
    // Determine shell and args
    let shell = options.shell
    let args = options.args
    let shellId = options.shellId || 'default'

    if (!shell && availableShells.value.length > 0) {
      const profile = availableShells.value.find((s) => s.id === shellId)
        ?? availableShells.value[0]
      shell = profile.shell
      args = args ?? profile.args
      shellId = profile.id
    }

    let result: { id: string; shell: string; title: string } | null = null
    if (isTerminalAvailable()) {
      result = await window.tinadec.terminal.create({
        shell,
        args,
        cwd: options.cwd,
        cols: options.cols ?? 80,
        rows: options.rows ?? 24,
        title: options.title,
      })
    }
    if (!result) {
      throw new Error('Electron terminal IPC not available')
    }

    const instance: TerminalInstance = {
      id: result.id,
      shell: result.shell,
      title: result.title || options.title || 'Terminal',
      term: null,
      fitAddon: null,
      exited: false,
      ready: false,
      cwd: options.cwd || '',
      shellId,
    }

    terminalInstances.value = [...terminalInstances.value, instance]
    activeTerminalId.value = instance.id

    return instance
  } catch (err) {
    console.error('[useTerminal] Failed to create terminal:', err)
    return null
  }
}

/**
 * Attach an xterm.js Terminal to a DOM element for a given terminal ID.
 * This is called by the TerminalView component on mount.
 *
 * @param id - Terminal ID
 * @param container - DOM element to attach to
 * @param term - xterm.js Terminal instance (created by the component)
 * @param fitAddon - Fit addon instance
 */
function attachTerminal(
  id: string,
  container: HTMLElement,
  term: Terminal,
  fitAddon: FitAddon,
): void {
  const instance = terminalInstances.value.find((t) => t.id === id)
  if (!instance) return

  // Store references
  instance.term = term
  instance.fitAddon = fitAddon

  // Apply current theme
  term.options.theme = buildXtermTheme()

  // Open xterm in the container
  term.open(container)

  // Fit to container size
  try {
    fitAddon.fit()
  } catch {
    // Fit may fail if container has no dimensions yet
  }

  // Notify main process of the initial size
  const { cols, rows } = term
  window.tinadec.terminal.resize(id, cols, rows)

  instance.ready = true

  // Set up IPC data listener → xterm write
  const removeDataListener = window.tinadec.terminal.onData(id, (data) => {
    if (instance.term && !instance.exited) {
      instance.term.write(data)
    }
  })

  // Set up IPC exit listener
  const removeExitListener = window.tinadec.terminal.onExit(id, (exitCode) => {
    instance.exited = true
    if (instance.term) {
      instance.term.write(`\r\n\x1b[90m[Process exited with code ${exitCode}]\x1b[0m\r\n`)
    }
  })

  // Set up xterm input → IPC write
  const inputDisposable = term.onData((data) => {
    window.tinadec.terminal.write(id, data)
  })

  // Set up xterm resize → IPC resize
  const resizeDisposable = term.onResize(({ cols, rows }) => {
    window.tinadec.terminal.resize(id, cols, rows)
  })

  // Store cleanup functions
  ipcCleanup.set(id, [
    removeDataListener,
    removeExitListener,
    () => inputDisposable.dispose(),
    () => resizeDisposable.dispose(),
  ])
}

/**
 * Detach an xterm.js Terminal from its terminal ID.
 * Cleans up IPC listeners and xterm disposables.
 *
 * @param id - Terminal ID
 */
function detachTerminal(id: string): void {
  const instance = terminalInstances.value.find((t) => t.id === id)
  if (!instance) return

  // Run cleanup functions
  const cleanups = ipcCleanup.get(id)
  if (cleanups) {
    for (const cleanup of cleanups) {
      try { cleanup() } catch { /* ignore */ }
    }
    ipcCleanup.delete(id)
  }

  // Dispose xterm
  if (instance.term) {
    try { instance.term.dispose() } catch { /* ignore */ }
    instance.term = null
  }
  instance.fitAddon = null
  instance.ready = false
}

/**
 * Close and destroy a terminal instance.
 *
 * @param id - Terminal ID
 */
function closeTerminal(id: string): void {
  detachTerminal(id)

  // Tell main process to destroy the PTY
  if (isTerminalAvailable()) {
    window.tinadec.terminal.destroy(id)
  }

  // Remove from instances list
  terminalInstances.value = terminalInstances.value.filter((t) => t.id !== id)

  // Update active terminal
  if (activeTerminalId.value === id) {
    activeTerminalId.value = terminalInstances.value[0]?.id ?? null
  }
}

/**
 * Close all terminal instances.
 */
function closeAllTerminals(): void {
  for (const instance of [...terminalInstances.value]) {
    closeTerminal(instance.id)
  }
}

/**
 * Get a terminal instance by ID.
 */
function getTerminal(id: string): TerminalInstance | undefined {
  return terminalInstances.value.find((t) => t.id === id)
}

/**
 * Set the active terminal.
 */
function setActiveTerminal(id: string): void {
  if (terminalInstances.value.some((t) => t.id === id)) {
    activeTerminalId.value = id
  }
}

/**
 * Refresh the xterm theme for all terminals.
 * Called when the app theme changes.
 */
function refreshTerminalThemes(): void {
  const theme = buildXtermTheme()
  for (const instance of terminalInstances.value) {
    if (instance.term) {
      instance.term.options.theme = theme
    }
  }
}

/**
 * Fit all terminals to their containers.
 * Called when the panel is resized or shown.
 */
function fitAllTerminals(): void {
  for (const instance of terminalInstances.value) {
    if (instance.fitAddon && instance.term) {
      try {
        instance.fitAddon.fit()
      } catch {
        // Ignore fit errors
      }
    }
  }
}

/**
 * Fit a specific terminal by ID.
 */
function fitTerminal(id: string): void {
  const instance = getTerminal(id)
  if (instance?.fitAddon && instance.term) {
    try {
      instance.fitAddon.fit()
    } catch {
      // Ignore
    }
  }
}

/**
 * Focus a terminal by ID.
 */
function focusTerminal(id: string): void {
  const instance = getTerminal(id)
  if (instance?.term) {
    instance.term.focus()
  }
}

// ---- Theme watcher ----

let themeObserver: MutationObserver | null = null
let themeWatcherInstalled = false

/**
 * Install a MutationObserver to watch for data-theme attribute changes
 * on the document root, and refresh terminal themes accordingly.
 */
function installThemeWatcher(): void {
  if (themeWatcherInstalled) return
  themeWatcherInstalled = true

  if (typeof MutationObserver === 'undefined') return

  themeObserver = new MutationObserver((mutations) => {
    for (const mutation of mutations) {
      if (mutation.type === 'attributes' && mutation.attributeName === 'data-theme') {
        refreshTerminalThemes()
        return
      }
    }
  })

  themeObserver.observe(document.documentElement, {
    attributes: true,
    attributeFilter: ['data-theme', 'style'],
  })
}

/**
 * Uninstall the theme watcher.
 */
function uninstallThemeWatcher(): void {
  if (themeObserver) {
    themeObserver.disconnect()
    themeObserver = null
  }
  themeWatcherInstalled = false
}

// ---- Composable ----

export function useTerminal() {
  // Install theme watcher on first use
  installThemeWatcher()

  // Clean up all terminals when the last composable user unmounts
  // (We use a module-level singleton, so cleanup happens on page navigation)
  onUnmounted(() => {
    // Don't destroy terminals on unmount — they persist across panel switches.
    // Only uninstall the theme watcher if there are no more terminals.
    if (terminalInstances.value.length === 0) {
      uninstallThemeWatcher()
    }
  })

  return {
    // State
    terminals: terminalInstances,
    activeTerminalId,
    availableShells,
    shellsLoaded,
    activeTerminal: computed(() =>
      terminalInstances.value.find((t) => t.id === activeTerminalId.value) ?? null,
    ),

    // Actions
    loadShells,
    createTerminal: createTerminalInstance,
    closeTerminal,
    closeAllTerminals,
    getTerminal,
    setActiveTerminal,
    attachTerminal,
    detachTerminal,
    fitTerminal,
    fitAllTerminals,
    focusTerminal,
    refreshTerminalThemes,

    // Utilities
    isTerminalAvailable,
  }
}
