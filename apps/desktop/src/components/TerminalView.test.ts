// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
const state = vi.hoisted(() => ({ current: null as unknown, focus: vi.fn(), fit: vi.fn(), detach: vi.fn(), dispose: vi.fn() }))
vi.mock('@xterm/xterm', () => ({ Terminal: class { loadAddon() {} dispose = state.dispose } }))
vi.mock('@xterm/addon-fit', () => ({ FitAddon: class {} }))
vi.mock('@xterm/addon-web-links', () => ({ WebLinksAddon: class {} }))
vi.mock('@/composables/useTerminal', () => ({ useTerminal: () => ({
  attachTerminal: (_id: string, _host: unknown, term: unknown) => { state.current = term; return true },
  detachTerminal: state.detach, fitTerminal: state.fit, focusTerminal: state.focus,
  getTerminal: () => ({ term: state.current, exited: false }),
}) }))
import TerminalView from './TerminalView.vue'
afterEach(() => { vi.useRealTimers(); vi.clearAllMocks(); state.current = null })
describe('TerminalView attachment callbacks', () => {
  it('cancels delayed focus when its view unmounts', () => {
    vi.useFakeTimers()
    const wrapper = mount(TerminalView, { props: { terminalId: 'term-1' } })
    wrapper.unmount()
    vi.advanceTimersByTime(100)
    expect(state.focus).not.toHaveBeenCalled()
    expect(state.detach).toHaveBeenCalledTimes(1)
  })
  it('does not focus or fit a newer view sharing the terminal id', () => {
    vi.useFakeTimers()
    const wrapper = mount(TerminalView, { props: { terminalId: 'term-1' } })
    state.current = {}
    vi.advanceTimersByTime(100)
    wrapper.vm.fit()
    wrapper.vm.focus()
    expect(state.focus).not.toHaveBeenCalled()
    expect(state.fit).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
