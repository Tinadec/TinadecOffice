/**
 * useTerminal — Terminal state management composable.
 *
 * Manages multiple terminal instances, xterm.js integration, theme adaptation,
 * and the data flow between xterm.js (renderer) and node-pty (main process).
 *
 * Architecture:
 * - Each terminal has a unique ID from the main process.
 * - xterm.js Terminal instances are created lazily and attached to DOM elements.
 * - Local terminals (the user's own shell) run over node-pty in the main process:
 *   xterm input → IPC write → pty stdin → pty stdout → IPC data → xterm write.
 * - Agent terminals belong to a Core-owned session created by the `shell` tool and
 *   reached through the tool-core-gateway path: output replays from the run event
 *   journal and then follows the session event stream; input is an audited HTTP
 *   call. Both are rendered by the same widget (see useTerminalSource).
 * - Theme is adapted from CSS variables to xterm ITheme on changes.
 *
 * Every value handed to `window.tinadec.terminal.*` must be plain data: Electron
 * serializes IPC with the V8 algorithm, which refuses Proxy objects — and a deep
 * `ref`/`reactive` store hands out Proxies that still pass `Array.isArray`.
 */

import { computed, getCurrentInstance, markRaw, onUnmounted, ref, shallowRef } from 'vue'
import i18n from '@/i18n'
import type { Terminal } from '@xterm/xterm'
import type { ITheme as XtermTheme } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import {
  createLocalTerminalSource,
  createWebTerminalSource,
  type TerminalSource,
  type TerminalSourceKind,
} from '@/composables/useTerminalSource'
import { useHostAccess } from '@/lib/hostAccess'
import { resolveWebGatewayBase } from '@/lib/hostConnection'

// ---- Types ----

export interface TerminalInstance {
  /** Unique terminal ID from the main process (or `agent:<sessionId>`) */
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
  /**
   * Transport behind the widget: `local` is the Electron PTY the user owns;
   * `agent` is a Core-owned session the agent started.
   */
  sourceKind: TerminalSourceKind
  /** Core terminal session id for agent terminals. */
  terminalSessionId: string | null
  /** Run that owns an agent session; drives pause/cancel controls. */
  runId: string | null
  /** Data plug used by TerminalView. */
  source: TerminalSource | null
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
/**
 * Expand `#rrggbb` into `rgba()`. xterm rejects the `color-mix()`/triplet
 * forms the rest of the app uses, so the selection tint is derived here from
 * the same `--accent-primary` the surfaces read — one accent, one source.
 */
function withAlpha(color: string, alpha: number): string {
  const hex = color.trim()
  if (!/^#[0-9a-fA-F]{6}$/.test(hex)) return hex
  const n = Number.parseInt(hex.slice(1), 16)
  return `rgba(${(n >> 16) & 0xff}, ${(n >> 8) & 0xff}, ${n & 0xff}, ${alpha})`
}

function buildXtermTheme(): XtermTheme {
  const root = document.documentElement
  const getVar = (name: string, fallback: string): string => {
    const val = getComputedStyle(root).getPropertyValue(name).trim()
    return val || fallback
  }

  const isDark = root.getAttribute('data-theme') === 'dark'

  if (isDark) {
    return {
      background: getVar('--bg-primary', '#09090b'),
      foreground: getVar('--text-primary', '#fafafa'),
      cursor: getVar('--accent-primary', '#fafafa'),
      cursorAccent: getVar('--bg-primary', '#09090b'),
      selectionBackground: withAlpha(getVar('--accent-primary', '#fafafa'), 0.25),
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
      cursor: getVar('--accent-primary', '#18181b'),
      cursorAccent: getVar('--bg-primary', '#ffffff'),
      selectionBackground: withAlpha(getVar('--accent-primary', '#18181b'), 0.2),
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
/**
 * `shallowRef` keeps the IPC reply from being re-wrapped in a deep reactive Proxy:
 * `profile.args` is read straight into a `terminal:create` payload, and Electron's
 * serializer refuses Proxy objects.
 */
const availableShells = shallowRef<ShellProfile[]>([])
const shellsLoaded = ref(false)

/** Last terminal creation failure; shown in-panel and cleared on the next success. */
const creationError = ref<{ message: string; retryable: boolean } | null>(null)

/**
 * Copy into a plain string array. A reactive Proxy reads exactly like an array but
 * Electron's IPC serializer refuses it, so never pass a store value through as-is.
 */
function plainStringArray(value: readonly string[] | undefined | null): string[] | undefined {
  if (value === undefined || value === null) return undefined
  return Array.from(value, (item) => String(item))
}

function plainText(value: string | undefined | null): string | undefined {
  if (value === undefined || value === null) return undefined
  return String(value)
}

/** Cleanup functions for IPC listeners, keyed by terminal ID */
const ipcCleanup = new Map<string, Array<() => void>>()

/**
 * Check if the Electron terminal IPC API is available.
 */
function isElectronTerminalAvailable(): boolean {
  return typeof window !== 'undefined'
    && !!window.tinadec
    && !!window.tinadec.terminal
}

/**
 * Check if WebSocket-backed Web Terminal (/ws/terminal) is available in Web ready mode.
 */
function isWebTerminalAvailable(): boolean {
  if (isElectronTerminalAvailable()) return false
  if (typeof window === 'undefined' || typeof WebSocket === 'undefined') return false
  const { status, canAccessBackend } = useHostAccess()
  const tinadec = (window as unknown as { tinadec?: { isWebShim?: boolean } }).tinadec
  if (!tinadec?.isWebShim && !status.value?.webAuth) return false
  return canAccessBackend.value
}

/**
 * Check if any interactive terminal backend (Electron PTY or WebSocket /ws/terminal) is available.
 */
function isTerminalAvailable(): boolean {
  return isElectronTerminalAvailable() || isWebTerminalAvailable()
}

/**
 * Load available shell profiles from Electron IPC or Gateway Web Terminal API.
 *
 * The reply is frozen on the way in: a deep-reactive copy would hand
 * `terminal:create` a Proxy for `args`, which Electron's serializer refuses.
 */
async function loadShells(): Promise<void> {
  if (!isTerminalAvailable()) {
    availableShells.value = []
    return
  }
  if (!isElectronTerminalAvailable() && isWebTerminalAvailable()) {
    try {
      const res = await fetch(`${resolveWebGatewayBase()}/api/v1/web-terminal/shells`, {
        credentials: 'include',
        headers: { accept: 'application/json' },
      })
      if (!res.ok) {
        availableShells.value = []
        return
      }
      const data = (await res.json()) as { shells?: ShellProfile[] }
      const shells = Array.isArray(data?.shells) ? data.shells : []
      availableShells.value = Object.freeze(
        shells.map((s) => Object.freeze({
          id: String(s.id),
          label: String(s.label),
          shell: String(s.shell),
          args: Object.freeze(plainStringArray(s.args) ?? []),
        })),
      ) as ShellProfile[]
      shellsLoaded.value = true
    } catch {
      availableShells.value = []
    }
    return
  }
  try {
    const shells = await window.tinadec.terminal.getShells()
    availableShells.value = Object.freeze(
      shells.map((s) => Object.freeze({
        id: String(s.id),
        label: String(s.label),
        shell: String(s.shell),
        args: Object.freeze(plainStringArray(s.args) ?? []),
      })),
    ) as ShellProfile[]
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
  creationError.value = null

  try {
    let shell = plainText(options.shell)
    let args = plainStringArray(options.args)
    let shellId = plainText(options.shellId) || 'default'

    // Only consult the catalog when it is already cached. A cold start lets the main
    // process resolve the default shell so a click never waits on an IPC round-trip.
    if (!shell && shellsLoaded.value && availableShells.value.length > 0) {
      const profile = availableShells.value.find((s) => s.id === shellId)
        ?? availableShells.value[0]
      shell = plainText(profile.shell)
      args = args ?? plainStringArray(profile.args)
      shellId = plainText(profile.id) ?? 'default'
    }

    if (!isElectronTerminalAvailable() && isWebTerminalAvailable()) {
      const id = `web-${globalThis.crypto?.randomUUID?.() ?? Date.now().toString(36)}`
      const webSource = createWebTerminalSource({
        shellId,
        cwd: plainText(options.cwd),
        onReady: (meta) => {
          const target = terminalInstances.value.find((entry) => entry.id === id)
          if (!target) return
          if (meta.shell) target.shell = meta.shell
          if (meta.title && !options.title) target.title = meta.title
        },
      })
      const instance: TerminalInstance = {
        id,
        shell: shell || shellId || 'shell',
        title: options.title || shell || shellId || i18n.global.t('terminal.defaultTitle'),
        term: null,
        fitAddon: null,
        exited: false,
        ready: false,
        cwd: options.cwd || '',
        shellId,
        sourceKind: 'local',
        terminalSessionId: null,
        runId: null,
        source: webSource,
      }
      terminalInstances.value = [...terminalInstances.value, instance]
      activeTerminalId.value = instance.id
      return instance
    }

    const result = isElectronTerminalAvailable()
      ? await window.tinadec.terminal.create({
        shell,
        args,
        cwd: plainText(options.cwd),
        cols: options.cols ?? 80,
        rows: options.rows ?? 24,
        title: plainText(options.title),
      })
      : null

    if (!result?.id) {
      throw new Error(result?.error || i18n.global.t('terminal.noProcess'))
    }

    // Callers name the tab in the user's language; without a name it shows the shell, like any terminal.
    const shellName = result.shell || shell || 'shell'
    const instance: TerminalInstance = {
      id: result.id,
      shell: shellName,
      title: result.title || options.title || shellName,
      term: null,
      fitAddon: null,
      exited: false,
      ready: false,
      cwd: options.cwd || '',
      shellId,
      sourceKind: 'local',
      terminalSessionId: null,
      runId: null,
      source: createLocalTerminalSource(result.id),
    }

    terminalInstances.value = [...terminalInstances.value, instance]
    activeTerminalId.value = instance.id

    return instance
  } catch (err) {
    console.error('[useTerminal] Failed to create terminal:', err)
    creationError.value = {
      message: err instanceof Error ? err.message : String(err),
      retryable: true,
    }
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
  const instance = terminalInstances.value.find((entry) => entry.id === id)
  if (!instance) return

  // Re-attaching overwrote the previous cleanup array and leaked an xterm plus its
  // IPC listeners, so tear the earlier attachment down first.
  if (ipcCleanup.has(id)) detachTerminal(id)

  // Store references
  // xterm and its addons own mutable private object graphs. Vue must never proxy
  // them: proxying breaks identity/ownership checks and can interfere with xterm's
  // renderer lifecycle.
  instance.term = markRaw(term)
  instance.fitAddon = markRaw(fitAddon)

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

  instance.ready = true

  // Agent sessions and WebSocket-backed Web terminals use the unified TerminalSource plug:
  // output arrives asynchronously and keystrokes/resizes travel through instance.source.
  if (instance.source && (instance.sourceKind === 'agent' || !isElectronTerminalAvailable())) {
    instance.source.attach({
      onData: (data) => {
        if (instance.term && !instance.exited) instance.term.write(data)
      },
      onStatus: (status) => {
        if (status === 'exited' || status === 'killed') instance.exited = true
      },
    })

    const sourceInputDisposable = term.onData((data) => {
      instance.source?.write(data)
    })
    const sourceResizeDisposable = term.onResize(({ cols, rows }) => {
      instance.source?.resize(cols, rows)
    })

    ipcCleanup.set(id, [
      instance.source.detach,
      () => sourceInputDisposable.dispose(),
      () => sourceResizeDisposable.dispose(),
    ])
    return
  }

  // Notify main process of the initial size
  const { cols, rows } = term
  window.tinadec.terminal.resize(id, cols, rows)

  // The shell starts printing as soon as it spawns, so listen first and hold the
  // bytes until the replay snapshot has been written; writing in that order leaves
  // neither a gap nor a duplicate.
  let replayed = false
  const held: string[] = []
  const writeOrHold = (data: string) => {
    if (!instance.term || instance.exited) return
    if (replayed) instance.term.write(data)
    else held.push(data)
  }

  // Set up IPC data listener → xterm write
  const removeDataListener = window.tinadec.terminal.onData(id, writeOrHold)

  // Set up IPC exit listener
  const removeExitListener = window.tinadec.terminal.onExit(id, (exitCode) => {
    instance.exited = true
    if (instance.term) {
      instance.term.write(`\r\n\x1b[90m${i18n.global.t('terminal.output.exited', { code: exitCode })}\x1b[0m\r\n`)
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

  void window.tinadec.terminal.snapshot(id).then((snapshot) => {
    replayed = true
    if (instance.term) {
      if (snapshot?.replay) instance.term.write(snapshot.replay)
      for (const chunk of held) instance.term.write(chunk)
      if (snapshot?.exited) instance.exited = true
    }
    held.length = 0
  }).catch(() => {
    // No snapshot available; deliver whatever was held and keep streaming.
    replayed = true
    if (instance.term) for (const chunk of held) instance.term.write(chunk)
    held.length = 0
  })
}

/**
 * Detach an xterm.js Terminal from its terminal ID.
 * Cleans up IPC listeners and xterm disposables.
 *
 * @param id - Terminal ID
 */
function detachTerminal(id: string, expectedTerm?: Terminal | null): void {
  const instance = terminalInstances.value.find((entry) => entry.id === id)
  if (!instance) return
  // A stale TerminalView can unmount after a newer view has already taken ownership
  // of this terminal ID. Never let that stale view dispose the newer xterm renderer
  // or tear down its IPC listeners.
  if (expectedTerm && instance.term !== expectedTerm) return

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
  const instance = terminalInstances.value.find((entry) => entry.id === id)
  detachTerminal(id)

  if (instance?.sourceKind === 'agent') {
    // Closing the tab detaches the view; the session Core owns keeps running
    // until it exits or the user kills it explicitly (killTerminal).
    instance.source?.dispose()
  } else if (isElectronTerminalAvailable()) {
    // Tell main process to destroy the PTY
    window.tinadec.terminal.destroy(id)
  } else {
    instance?.source?.dispose()
  }

  // Remove from instances list
  terminalInstances.value = terminalInstances.value.filter((entry) => entry.id !== id)

  // Update active terminal
  if (activeTerminalId.value === id) {
    activeTerminalId.value = terminalInstances.value[0]?.id ?? null
  }
}

/**
 * Open an agent-owned terminal session in the panel.
 *
 * The session already exists in Core (created by the `shell` tool dispatch); this
 * only attaches a view to it. Re-opening the same session reuses the widget state
 * so the jump from the conversation is idempotent.
 */
function openAgentTerminal(options: {
  terminalSessionId: string
  runId?: string | null
  title?: string
  command?: string
  source: TerminalSource
}): string {
  const id = `agent:${options.terminalSessionId}`
  const existing = terminalInstances.value.find((entry) => entry.id === id)
  if (existing) {
    activeTerminalId.value = existing.id
    return existing.id
  }

  const shell = 'agent'
  const instance: TerminalInstance = {
    id,
    shell,
    title: options.title || options.command?.slice(0, 40) || shell,
    term: null,
    fitAddon: null,
    exited: false,
    ready: false,
    cwd: '',
    shellId: 'agent',
    sourceKind: 'agent',
    terminalSessionId: options.terminalSessionId,
    runId: options.runId ?? null,
    source: options.source,
  }

  terminalInstances.value = [...terminalInstances.value, instance]
  activeTerminalId.value = instance.id
  return instance.id
}

/** Terminate an agent-owned terminal session (kill switch in the panel header). */
function killTerminal(id: string): void {
  const instance = terminalInstances.value.find((entry) => entry.id === id)
  if (!instance) return
  if (instance.sourceKind === 'agent') {
    instance.source?.kill()
    instance.exited = true
  } else {
    closeTerminal(id)
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
  return terminalInstances.value.find((entry) => entry.id === id)
}

/**
 * Set the active terminal.
 */
function setActiveTerminal(id: string): void {
  if (terminalInstances.value.some((entry) => entry.id === id)) {
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
    fitTerminal(instance.id)
  }
}

/**
 * Fit a specific terminal by ID.
 *
 * Never fit against an unlaid-out host: FitAddon measures the xterm element's
 * parent, so a `display:none` card proposes its 2×1 minimum and the shell then
 * repaints at that size.
 */
function fitTerminal(id: string): void {
  const instance = getTerminal(id)
  if (!instance?.fitAddon || !instance.term) return

  const host = instance.term.element?.parentElement
  if (!host || !host.isConnected || host.clientWidth <= 0 || host.clientHeight <= 0) return

  try {
    instance.fitAddon.fit()
  } catch {
    // Ignore fit errors during a rapid resize
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

  // This module is a singleton reached from non-component call sites too, so the
  // unmount hook is only registered when a setup instance actually owns it.
  if (getCurrentInstance()) {
    onUnmounted(() => {
      // Don't destroy terminals on unmount — they persist across panel switches.
      // Only uninstall the theme watcher if there are no more terminals.
      if (terminalInstances.value.length === 0) {
        uninstallThemeWatcher()
      }
    })
  }

  return {
    // State
    terminals: terminalInstances,
    activeTerminalId,
    availableShells,
    shellsLoaded,
    creationError,
    activeTerminal: computed(() =>
      terminalInstances.value.find((entry) => entry.id === activeTerminalId.value) ?? null,
    ),

    // Actions
    loadShells,
    createTerminal: createTerminalInstance,
    clearCreationError: () => { creationError.value = null },
    openAgentTerminal,
    killTerminal,
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

/** Test-only: reset the module singleton between cases. */
export function __resetTerminalStateForTests(): void {
  for (const id of [...ipcCleanup.keys()]) detachTerminal(id)
  terminalInstances.value = []
  activeTerminalId.value = null
  availableShells.value = []
  shellsLoaded.value = false
  creationError.value = null
}
