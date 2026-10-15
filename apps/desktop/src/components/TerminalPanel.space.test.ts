// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { computed, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()), useI18n: () => ({ t: (key: string) => key }) }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: { error: vi.fn() } }) }))
vi.mock('./TerminalView.vue', () => ({ default: { props: ['terminalId'], template: '<div class="terminal-probe">{{ terminalId }}</div>' } }))
vi.mock('@/composables/useTerminal', async () => {
  const { ref } = await import('vue')
  const state = {
    terminals: ref([
      { id: 'a', runId: 'run-a', sourceKind: 'agent', title: 'Session A', exited: false },
      { id: 'b', runId: 'run-b', sourceKind: 'agent', title: 'Session B', exited: false },
      { id: 'local', runId: null, sourceKind: 'local', title: 'Local shell', exited: false },
    ]), activeTerminalId: ref('a'), availableShells: ref([]), shellsLoaded: ref(true), creationError: ref(null),
    loadShells: vi.fn(), createTerminal: vi.fn(), closeTerminal: vi.fn(), killTerminal: vi.fn(), setActiveTerminal: vi.fn(),
    fitTerminal: vi.fn(), focusTerminal: vi.fn(), fitAllTerminals: vi.fn(), clearCreationError: vi.fn(), isTerminalAvailable: () => true,
  }
  return { useTerminal: () => state }
})
import TerminalPanel from './TerminalPanel.vue'
import { useTerminal } from '@/composables/useTerminal'

afterEach(() => vi.clearAllMocks())
describe('space terminal scope', () => {
  it('retains a hidden session view, filters foreign/local terminals and does not create or close a process on session change', async () => {
    const allowed = ref(new Set(['run-a']))
    const state = useTerminal()
    const wrapper = mount(TerminalPanel, { attachTo: document.body, props: { visible: true }, global: { provide: {
      'space:terminal-runs': computed(() => allowed.value),
    } } })
    await flushPromises()
    expect(wrapper.findAll('.terminal-probe').map(w => w.text())).toEqual(['a'])
    const element = wrapper.get('.terminal-probe').element
    expect(wrapper.find('.terminal-new-wrapper').exists()).toBe(false)
    await wrapper.setProps({ visible: false })
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'w', ctrlKey: true }))
    expect(state.closeTerminal).not.toHaveBeenCalled()
    await wrapper.setProps({ visible: true })
    expect(wrapper.get('.terminal-probe').element).toBe(element)
    allowed.value = new Set(['run-b'])
    await flushPromises()
    expect(wrapper.findAll('.terminal-probe').map(w => w.text())).toEqual(['b'])
    expect(wrapper.text()).not.toContain('Session A')
    allowed.value = new Set()
    await flushPromises()
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'T', ctrlKey: true, shiftKey: true }))
    expect(wrapper.findAll('.terminal-probe')).toHaveLength(0)
    expect(state.createTerminal).not.toHaveBeenCalled()
    expect(state.closeTerminal).not.toHaveBeenCalled()
    expect(state.killTerminal).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
