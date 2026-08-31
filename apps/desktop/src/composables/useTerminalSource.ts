/**
 * useTerminalSource — the data-plug abstraction behind every terminal view.
 *
 * The function panel renders one kind of widget (xterm.js) fed by one of two
 * transports:
 *
 * - `local`: the user's own shell, hosted by the Electron main process (node-pty)
 *   and reached over the `window.tinadec.terminal.*` IPC bridge. Direct, fast,
 *   and it is the only path that owns a real PTY on this machine.
 * - `agent`: a terminal session an agent started through the tool-core-gateway
 *   path (Core tool dispatch → TinadecTools). Output arrives as `terminal.*`
 *   events on the session event stream; input travels back as an audited HTTP
 *   call. There is no local PTY, so replay-then-follow over the event journal —
 *   not a socket — is what makes the panel able to show history the user missed
 *   before clicking "open in panel".
 */

import type { EventEnvelope } from '@/api'

export type TerminalSourceKind = 'local' | 'agent'

export interface TerminalSourceHandlers {
  /** Raw stream payload ready to hand to xterm.write(). */
  onData: (data: string) => void
  /** Session lifecycle transitions (running → exited / killed). */
  onStatus?: (status: string, detail?: string) => void
}

export interface TerminalSource {
  readonly kind: TerminalSourceKind
  /** True when the widget should accept keystrokes. */
  readonly interactive: boolean
  /** Attaches handlers and returns a detach function. */
  attach: (handlers: TerminalSourceHandlers) => void
  /** User keystrokes. */
  write: (data: string) => void
  /** Terminal size change; advisory for the agent path (no real PTY). */
  resize: (cols: number, rows: number) => void
  /** Detach only; the session itself keeps running. */
  detach: () => void
  /**
   * Stops the transport. For the local path this destroys the PTY; for the agent
   * path it only unsubscribes — a session an agent owns is theirs to keep until
   * it exits or the user explicitly kills it.
   */
  dispose: () => void
  /** Explicitly terminates the session (no-op for the local path, which closes instead). */
  kill: () => void
}

// ── Local: Electron main-process PTY ─────────────────────────────────────────

interface TinadecTerminalBridge {
  create(options: Record<string, unknown>): Promise<{ id: string; shell: string; title: string } | null>
  write(id: string, data: string): void
  resize(id: string, cols: number, rows: number): void
  onData(id: string, handler: (data: string) => void): () => void
  onExit(id: string, handler: (code: number) => void): () => void
  destroy(id: string): void
  getShells(): Promise<Array<{ id: string; label: string; shell: string; args: string[] }>>
}

function bridge(): TinadecTerminalBridge | null {
  const scope = globalThis as unknown as { window?: { tinadec?: { terminal?: TinadecTerminalBridge } } }
  return scope.window?.tinadec?.terminal ?? null
}

export interface LocalTerminalOptions {
  shell?: string
  shellId?: string
  args?: string[]
  cwd?: string
  title?: string
  cols?: number
  rows?: number
}

/**
 * Wraps the existing IPC PTY bridge. Kept intentionally thin: everything that
 * already works for the user's shell keeps working unchanged.
 */
export function createLocalTerminalSource(instanceId: string, options: LocalTerminalOptions = {}): TerminalSource {
  let detachData: (() => void) | null = null
  let detachExit: (() => void) | null = null
  let handlers: TerminalSourceHandlers | null = null
  let disposed = false

  const api = { bridge: bridge(), id: instanceId }

  function ensureAttached() {
    if (!api.bridge || detachData || disposed) return
    detachData = api.bridge.onData(instanceId, (data) => handlers?.onData(data))
    detachExit = api.bridge.onExit(instanceId, (code) => handlers?.onStatus?.('exited', String(code)))
  }

  // The PTY is created by useTerminal (which owns the instance id); if a fresh
  // one is needed, callers create it there first.
  void options

  return {
    kind: 'local',
    interactive: true,
    attach(next) {
      handlers = next
      ensureAttached()
    },
    write(data) {
      api.bridge?.write(instanceId, data)
    },
    resize(cols, rows) {
      api.bridge?.resize(instanceId, cols, rows)
    },
    detach() {
      detachData?.()
      detachExit?.()
      detachData = detachExit = null
      handlers = null
    },
    dispose() {
      disposed = true
      this.detach()
      api.bridge?.destroy(instanceId)
    },
    kill() {
      this.dispose()
    },
  }
}

// ── Agent: tool-core-gateway session stream ─────────────────────────────────

export interface AgentTerminalFeed {
  /** Subscribes to the session event stream. Returns an unsubscribe function. */
  subscribe: (handler: (event: EventEnvelope) => void) => () => void
  /** Replays already-persisted events, oldest first, before the live feed starts. */
  replay?: () => EventEnvelope[]
}

export interface AgentTerminalOptions {
  terminalSessionId: string
  runId?: string
  feed: AgentTerminalFeed
  /** Sends user keystrokes to Core; must be audited server-side. */
  sendStdin: (terminalSessionId: string, data: string) => Promise<unknown>
  /** Terminates the session. */
  kill?: (terminalSessionId: string) => Promise<unknown>
}

const TERMINAL_TYPES = new Set([
  'terminal.command',
  'terminal.stdout',
  'terminal.exit',
  'terminal.stdin',
  'terminal.session.killed',
])

function payloadOf(event: EventEnvelope): Record<string, unknown> {
  return event.payload && typeof event.payload === 'object' ? (event.payload as Record<string, unknown>) : {}
}

function dim(text: string): string {
  return `\x1b[90m${text}\x1b[0m`
}

/**
 * Feed for a session an agent owns. Output is replayed from the event journal and
 * then followed live, so opening the panel mid-command shows the full history.
 */
export function createAgentTerminalSource(options: AgentTerminalOptions): TerminalSource {
  const { terminalSessionId, runId, feed, sendStdin, kill } = options
  let unsubscribe: (() => void) | null = null
  let handlers: TerminalSourceHandlers | null = null
  let status = 'running'
  let disposed = false

  function accepts(payload: Record<string, unknown>): boolean {
    if (payload.terminal_session_id !== terminalSessionId) return false
    if (runId && payload.run_id && payload.run_id !== runId) return false
    return true
  }

  function render(event: EventEnvelope) {
    if (!handlers) return
    const payload = payloadOf(event)
    if (!accepts(payload)) return

    switch (event.type) {
      case 'terminal.command': {
        const command = String(payload.command ?? '')
        const cwd = payload.cwd ? ` ${dim(`(cwd: ${payload.cwd})`)}` : ''
        handlers.onData(`${dim('$ ')}${command}${cwd}\r\n`)
        handlers.onStatus?.('running')
        break
      }
      case 'terminal.stdout': {
        const data = String(payload.data ?? '')
        if (data.length > 0) handlers.onData(data)
        break
      }
      case 'terminal.stdin': {
        // Locally echo what the user typed; the remote process rarely echoes it back.
        break
      }
      case 'terminal.exit': {
        const code = payload.exit_code
        status = 'exited'
        handlers.onData(`\r\n${dim(`[Process exited with code ${code ?? 'unknown'}]`)}\r\n`)
        handlers.onStatus?.('exited', code == null ? undefined : String(code))
        break
      }
      case 'terminal.session.killed': {
        status = 'killed'
        handlers.onData(`\r\n${dim('[Session terminated]')}\r\n`)
        handlers.onStatus?.('killed')
        break
      }
    }
  }

  function attach(next: TerminalSourceHandlers) {
    handlers = next
    for (const event of feed.replay?.() ?? []) {
      if (TERMINAL_TYPES.has(event.type)) render(event)
    }
    unsubscribe = feed.subscribe((event) => {
      if (!TERMINAL_TYPES.has(event.type)) return
      render(event)
    })
    if (status === 'exited' || status === 'killed') handlers.onStatus?.(status)
  }

  return {
    kind: 'agent',
    interactive: true,
    attach,
    write(data) {
      void sendStdin(terminalSessionId, data).catch(() => {
        handlers?.onData(`\r\n${dim('[Input was not delivered]')}\r\n`)
      })
    },
    resize() {
      // No PTY on this path; size is advisory and intentionally ignored.
    },
    detach() {
      unsubscribe?.()
      unsubscribe = null
      handlers = null
    },
    dispose() {
      disposed = true
      this.detach()
    },
    kill() {
      void kill?.(terminalSessionId).catch(() => undefined)
      status = 'killed'
      handlers?.onData(`\r\n${dim('[Session terminated]')}\r\n`)
      handlers?.onStatus?.('killed')
      this.detach()
    },
  }
}
