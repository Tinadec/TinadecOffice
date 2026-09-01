import { Window } from 'happy-dom'
import { isProxy, reactive } from 'vue'
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import type { FitAddon } from '@xterm/addon-fit'
import type { Terminal } from '@xterm/xterm'

let testWindow: Window
let module: typeof import('./useTerminal')

/** Main-process reply shape; `create` rejects anything Electron's serializer would refuse. */
interface CreateCall {
  options: Record<string, unknown>
}

interface FakeTerminal {
  options: { theme?: unknown }
  element?: HTMLElement
  cols: number
  rows: number
  opened: HTMLElement[]
  writes: string[]
  inputHandlers: Array<(data: string) => void>
  open(host: HTMLElement): void
  write(data: string): void
  focus(): void
  dispose(): void
  onData(cb: (data: string) => void): { dispose: () => void }
  onResize(cb: (size: { cols: number; rows: number }) => void): { dispose: () => void }
}

function createFakeTerminal(): FakeTerminal {
  const term: FakeTerminal = {
    options: {},
    cols: 80,
    rows: 24,
    opened: [],
    writes: [],
    inputHandlers: [],
    open(host) {
      term.opened.push(host)
      // Mirror xterm: `element` is the node the widget renders into, so the fit
      // guard measures the container through its parent.
      const element = document.createElement('div')
      host.appendChild(element)
      term.element = element
    },
    write(data) { term.writes.push(data) },
    focus() {},
    dispose() {},
    onData(cb) {
      term.inputHandlers.push(cb)
      return { dispose: () => { term.inputHandlers = term.inputHandlers.filter((h) => h !== cb) } }
    },
    onResize() { return { dispose: () => {} } },
  }
  return term
}

interface TerminalStub {
  create: ReturnType<typeof vi.fn>
  write: ReturnType<typeof vi.fn>
  resize: ReturnType<typeof vi.fn>
  destroy: ReturnType<typeof vi.fn>
  snapshot: ReturnType<typeof vi.fn>
  getShells: ReturnType<typeof vi.fn>
  list: ReturnType<typeof vi.fn>
  onData: ReturnType<typeof vi.fn>
  onExit: ReturnType<typeof vi.fn>
  dataHandlers: Array<{ id: string; cb: (data: string) => void; remover: ReturnType<typeof vi.fn> }>
}

function installTerminalStub(): TerminalStub {
  const dataHandlers: TerminalStub['dataHandlers'] = []
  const stub: TerminalStub = {
    create: vi.fn(async (options: Record<string, unknown>) => {
      // The real bridge hands `options` to Electron's V8 serializer, which refuses
      // Proxy objects. structuredClone enforces the same contract here, so a payload
      // that reacquires reactivity fails this test instead of deadening the button.
      const call: CreateCall = { options }
      structuredClone(call)
      return { id: 'term-1', shell: String(options?.shell ?? 'pwsh'), title: 'T', backend: 'pty' }
    }),
    write: vi.fn(),
    resize: vi.fn(),
    destroy: vi.fn(),
    snapshot: vi.fn(async () => ({ replay: '', exited: false, exitCode: null })),
    getShells: vi.fn(async () => [
      { id: 'pwsh', label: 'PowerShell 7', shell: 'C:\\pwsh.exe', args: ['-NoLogo'] },
      { id: 'cmd', label: 'Command Prompt', shell: 'C:\\cmd.exe', args: [] },
    ]),
    list: vi.fn(async () => []),
    onData: vi.fn((id: string, cb: (data: string) => void) => {
      const remover = vi.fn()
      dataHandlers.push({ id, cb, remover })
      return remover
    }),
    onExit: vi.fn(() => vi.fn()),
    dataHandlers,
  }
  Object.defineProperty(testWindow, 'tinadec', {
    value: { terminal: stub },
    configurable: true,
    writable: true,
  })
  return stub
}

describe('useTerminal', () => {
  beforeAll(async () => {
    testWindow = new Window({ url: 'http://127.0.0.1:5173' })
    vi.stubGlobal('window', testWindow)
    vi.stubGlobal('document', testWindow.document)
    // buildXtermTheme reads CSS variables through the global helper.
    vi.stubGlobal('getComputedStyle', testWindow.getComputedStyle.bind(testWindow))
    module = await import('./useTerminal')
  })

  beforeEach(() => {
    module.__resetTerminalStateForTests()
  })

  afterAll(() => {
    testWindow.close()
    vi.unstubAllGlobals()
  })

  describe('shell catalog', () => {
    it('keeps the catalog free of reactive wrappers', async () => {
      installTerminalStub()
      const { loadShells, availableShells, shellsLoaded } = module.useTerminal()
      await loadShells()

      expect(shellsLoaded.value).toBe(true)
      expect(availableShells.value).toHaveLength(2)
      expect(isProxy(availableShells.value[0].args)).toBe(false)
    })
  })

  describe('create', () => {
    it('sends a serializable payload built from the resolved profile', async () => {
      const terminal = installTerminalStub()
      const { createTerminal, loadShells, terminals } = module.useTerminal()
      await loadShells()

      const instance = await createTerminal({ shellId: 'cmd', cwd: 'C:\\work', cols: 90, rows: 30 })

      expect(instance).not.toBeNull()
      expect(terminals.value.map((entry) => entry.id)).toEqual(['term-1'])
      expect(terminal.create).toHaveBeenCalledTimes(1)
      const payload = terminal.create.mock.calls[0][0] as Record<string, unknown>
      expect(payload).toMatchObject({ shell: 'C:\\cmd.exe', args: [], cwd: 'C:\\work', cols: 90, rows: 30 })
    })

    it('survives a reactive profile reintroduced into the store', async () => {
      const terminal = installTerminalStub()
      const { createTerminal, availableShells, shellsLoaded } = module.useTerminal()

      // Hostile store write: a deep-reactive array is what actually killed terminal
      // creation, because it still satisfies the `string[]` type at every call site.
      availableShells.value = reactive([
        { id: 'pwsh', label: 'PowerShell 7', shell: 'C:\\pwsh.exe', args: ['-NoLogo'] },
      ]) as typeof availableShells.value
      shellsLoaded.value = true

      const instance = await createTerminal({ shellId: 'pwsh' })

      expect(instance).not.toBeNull()
      expect(terminal.create).toHaveBeenCalledTimes(1)
      expect(terminal.create.mock.calls[0][0]).toMatchObject({ args: ['-NoLogo'] })
    })

    it('lets the main process resolve the default shell on a cold catalog', async () => {
      const terminal = installTerminalStub()
      const { createTerminal } = module.useTerminal()

      const instance = await createTerminal({})

      expect(instance).not.toBeNull()
      const payload = terminal.create.mock.calls[0][0] as Record<string, unknown>
      expect(payload.shell).toBeUndefined()
      expect(payload.args).toBeUndefined()
    })

    it('keeps the failure visible after the component that hit it is gone', async () => {
      const terminal = installTerminalStub()
      terminal.create.mockRejectedValueOnce(new Error('posix_spawnp failed'))
      const { createTerminal, creationError } = module.useTerminal()

      const instance = await createTerminal({ shellId: 'cmd' })

      expect(instance).toBeNull()
      expect(creationError.value?.message).toBe('posix_spawnp failed')
      expect(creationError.value?.retryable).toBe(true)

      // A later success clears it, so the strip cannot outlive the problem.
      await expect(createTerminal({ shellId: 'cmd' })).resolves.not.toBeNull()
      expect(creationError.value).toBeNull()
    })

    it('surfaces the reason the main process gave instead of inventing one', async () => {
      const terminal = installTerminalStub()
      terminal.create.mockResolvedValueOnce({
        id: null, backend: null, error: 'Working directory is not available: C:\\gone',
      })
      const { createTerminal, creationError } = module.useTerminal()

      const instance = await createTerminal({ cwd: 'C:\\gone' })

      expect(instance).toBeNull()
      expect(creationError.value?.message).toBe('Working directory is not available: C:\\gone')
    })
  })

  describe('attach', () => {
    /** Let the snapshot's then/catch chain finish. */
    async function flush(): Promise<void> {
      await new Promise((resolve) => { setTimeout(resolve, 0) })
    }

    /** A host with real dimensions so the fit guard lets the attach proceed. */
    function laidOutHost(): HTMLElement {
      const host = document.createElement('div')
      document.body.appendChild(host)
      Object.defineProperty(host, 'clientWidth', { value: 600, configurable: true })
      Object.defineProperty(host, 'clientHeight', { value: 300, configurable: true })
      return host
    }

    async function attachOne(terminal: TerminalStub) {
      const { createTerminal, attachTerminal } = module.useTerminal()
      const instance = await createTerminal({ shellId: 'cmd' })
      expect(instance).not.toBeNull()
      const term = createFakeTerminal()
      attachTerminal('term-1', laidOutHost(), term as unknown as Terminal, { fit: vi.fn() } as unknown as FitAddon)
      await flush()
      return { term, terminal }
    }

    it('replaces the previous listeners instead of leaking them on re-attach', async () => {
      const terminal = installTerminalStub()
      const first = await attachOne(terminal)

      expect(first.terminal.onData).toHaveBeenCalledTimes(1)
      const firstRemover = first.terminal.dataHandlers[0].remover
      expect(firstRemover).not.toHaveBeenCalled()

      const secondTerm = createFakeTerminal()
      module.useTerminal().attachTerminal(
        'term-1', laidOutHost(), secondTerm as unknown as Terminal, { fit: vi.fn() } as unknown as FitAddon,
      )

      // The earlier attachment is torn down rather than silently overwritten.
      expect(firstRemover).toHaveBeenCalledTimes(1)
      expect(first.terminal.onData).toHaveBeenCalledTimes(2)
    })

    it('holds live output until the replay snapshot has been written', async () => {
      const terminal = installTerminalStub()
      let resolveSnapshot: (value: { replay: string; exited: boolean; exitCode: number | null }) => void = () => {}
      terminal.snapshot.mockImplementation(
        () => new Promise((resolve) => { resolveSnapshot = resolve }),
      )

      const { createTerminal, attachTerminal } = module.useTerminal()
      await createTerminal({ shellId: 'cmd' })
      const term = createFakeTerminal()
      attachTerminal('term-1', laidOutHost(), term as unknown as Terminal, { fit: vi.fn() } as unknown as FitAddon)

      // The shell prints before the snapshot round-trip completes.
      terminal.dataHandlers[0].cb('live-tail')
      expect(term.writes).toEqual([])

      resolveSnapshot({ replay: 'C:\\work> ', exited: false, exitCode: null })
      await flush()

      expect(term.writes).toEqual(['C:\\work> ', 'live-tail'])
    })

    it('still delivers live output when no snapshot is reachable', async () => {
      const terminal = installTerminalStub()
      terminal.snapshot.mockRejectedValue(new Error('no such terminal'))

      const { createTerminal, attachTerminal } = module.useTerminal()
      await createTerminal({ shellId: 'cmd' })
      const term = createFakeTerminal()
      attachTerminal('term-1', laidOutHost(), term as unknown as Terminal, { fit: vi.fn() } as unknown as FitAddon)

      terminal.dataHandlers[0].cb('live-only')
      await flush()

      expect(term.writes).toEqual(['live-only'])
    })
  })
})
