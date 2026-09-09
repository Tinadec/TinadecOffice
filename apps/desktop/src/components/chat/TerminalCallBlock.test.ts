// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const h = vi.hoisted(() => {
  const handlers = new Set<(event: Record<string, unknown>) => void>()
  return {
    handlers,
    events: { value: [] as Array<Record<string, unknown>> },
    openAgentTerminal: vi.fn(),
    notifyInfo: vi.fn(),
    notifyError: vi.fn(),
    listTerminalSessions: vi.fn(),
    sendStdin: vi.fn(),
    killSession: vi.fn(),
    controlRun: vi.fn(),
    dispatch: vi.fn(),
  }
})

vi.mock('@xterm/xterm', () => {
  class FakeTerminal {
    static instances: FakeTerminal[] = []
    writes: string[] = []
    inputHandlers: Array<(data: string) => void> = []
    constructor(public options: Record<string, unknown>) {
      FakeTerminal.instances.push(this)
    }
    loadAddon(): void {}
    open(): void {}
    write(data: string): void { this.writes.push(data) }
    onData(cb: (data: string) => void): void { this.inputHandlers.push(cb) }
    dispose(): void {}
  }
  return { Terminal: FakeTerminal }
})

vi.mock('@xterm/addon-fit', () => ({
  FitAddon: class { fit(): void {} },
}))

vi.mock('@/api', () => ({
  api: {
    listTerminalSessions: h.listTerminalSessions,
    sendTerminalStdin: h.sendStdin,
    killTerminalSession: h.killSession,
    controlRun: h.controlRun,
  },
}))

vi.mock('@/controllers/HomeController', () => ({
  homeController: {
    onEvent: (handler: (event: Record<string, unknown>) => void) => {
      h.handlers.add(handler)
      return () => h.handlers.delete(handler)
    },
    events: h.events,
  },
}))

vi.mock('@/composables/useTerminal', () => ({
  useTerminal: () => ({ openAgentTerminal: h.openAgentTerminal }),
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: { info: h.notifyInfo, error: h.notifyError } }),
}))

vi.mock('@tinadec/ui', () => ({
  useUie: () => ({
    bus: { dispatch: h.dispatch },
    scope: { value: 'scope' },
    snapshot: { value: { revision: 1 } },
  }),
}))

import TerminalCallBlock from './TerminalCallBlock.vue'
import { Terminal } from '@xterm/xterm'

function terminalEvent(seq: number, type: string, payload: Record<string, unknown>) {
  return { v: '1', type, request_id: '', trace_id: '', seq, ts: '2026-09-09T00:00:00Z', capabilities: [], payload }
}

function emit(event: Record<string, unknown>): void {
  for (const handler of [...h.handlers]) handler(event)
}

function lastTermWrites(): string {
  const instances = (Terminal as unknown as { instances: Array<{ writes: string[] }> }).instances
  return instances[instances.length - 1]?.writes.join('') ?? ''
}

afterEach(() => {
  h.handlers.clear()
  h.events.value = []
  vi.clearAllMocks()
  ;(Terminal as unknown as { instances: unknown[] }).instances.length = 0
})

describe('TerminalCallBlock', () => {
  it('backfills the terminal session from the Core registry when the command predates mount', async () => {
    h.listTerminalSessions.mockResolvedValue([
      {
        terminal_session_id: 'ts-1',
        run_id: 'run-1',
        task_id: 'task-1',
        agent_instance_id: 'agent-1',
        execution_id: 'exec-1',
        command: 'npm test',
        status: 'running',
        live: true,
        started_at: '2026-09-09T00:00:00Z',
      },
    ])

    const wrapper = mount(TerminalCallBlock, {
      props: { executionId: 'exec-1', command: 'npm test', runId: 'run-1', status: 'running' },
    })
    await flushPromises()

    expect(h.listTerminalSessions).toHaveBeenCalledWith('run-1')
    const openButton = wrapper.findAll('button').find((b) => b.text().includes('在功能面板打开'))!
    await openButton.trigger('click')
    // Without the backfill this falls back to the "session not established" info notice.
    expect(h.notifyInfo).not.toHaveBeenCalled()
    expect(h.openAgentTerminal).toHaveBeenCalledWith(expect.objectContaining({ terminalSessionId: 'ts-1', runId: 'run-1' }))
    wrapper.unmount()
  })

  it('replays pre-mount events from the controller buffer instead of querying the registry', async () => {
    h.events.value = [
      terminalEvent(5, 'terminal.command', { execution_id: 'exec-1', terminal_session_id: 'ts-9', run_id: 'run-1', command: 'npm test', cwd: '/w' }),
      terminalEvent(6, 'terminal.stdout', { execution_id: 'exec-1', terminal_session_id: 'ts-9', run_id: 'run-1', data: 'hello-out' }),
    ]

    const wrapper = mount(TerminalCallBlock, {
      props: { executionId: 'exec-1', command: 'npm test', status: 'running' },
    })
    await flushPromises()

    expect(h.listTerminalSessions).not.toHaveBeenCalled()
    expect(lastTermWrites()).toContain('hello-out')

    const openButton = wrapper.findAll('button').find((b) => b.text().includes('在功能面板打开'))!
    await openButton.trigger('click')
    expect(h.openAgentTerminal).toHaveBeenCalledWith(expect.objectContaining({ terminalSessionId: 'ts-9', runId: 'run-1' }))
    wrapper.unmount()
  })

  it('still associates live events emitted after mount', async () => {
    h.listTerminalSessions.mockResolvedValue([])
    const wrapper = mount(TerminalCallBlock, {
      props: { executionId: 'exec-1', command: 'npm test', status: 'running' },
    })
    await flushPromises()

    emit(terminalEvent(1, 'terminal.command', { execution_id: 'exec-1', terminal_session_id: 'ts-live', run_id: 'run-1', command: 'npm test' }))
    emit(terminalEvent(2, 'terminal.stdout', { execution_id: 'exec-1', terminal_session_id: 'ts-live', run_id: 'run-1', data: 'live-out' }))
    await flushPromises()

    expect(lastTermWrites()).toContain('live-out')
    const openButton = wrapper.findAll('button').find((b) => b.text().includes('在功能面板打开'))!
    await openButton.trigger('click')
    expect(h.openAgentTerminal).toHaveBeenCalledWith(expect.objectContaining({ terminalSessionId: 'ts-live' }))
    wrapper.unmount()
  })
})
