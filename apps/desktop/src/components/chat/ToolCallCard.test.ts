// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import type { ToolCall } from '@/composables/useAgentActivity'
import ToolCallCard from './ToolCallCard.vue'
import zh from '@/locales/zh-CN'

const { getResult } = vi.hoisted(() => ({ getResult: vi.fn() }))
vi.mock('@/api', () => ({ api: { getToolExecutionResult: getResult } }))
vi.mock('./TerminalCallBlock.vue', () => ({ default: {
  props: ['executionId', 'command', 'runId', 'status'], template: '<div data-testid="live-terminal" />',
} }))
const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
const wrappers: ReturnType<typeof mount>[] = []
const shell = {
  id: 'historical-shell', runId: 'historical-run', toolId: 'shell', toolName: 'shell', status: 'completed',
  startedAt: null, completedAt: null, durationMs: 12, argsSummary: 'echo first-output',
  resultSummary: 'Completed', requiresApproval: false, approvalId: null, evidence: [], seq: 1, risk: 'low',
} as ToolCall
beforeEach(() => {
  getResult.mockReset().mockResolvedValue({ body: 'first-output preserved after hundreds of later events', tool_id: 'shell' })
})
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()) })
function render(toolCall = shell) {
  const wrapper = mount(ToolCallCard, { props: { toolCall }, global: { plugins: [i18n] } })
  wrappers.push(wrapper)
  return wrapper
}

describe('ToolCallCard shell results', () => {
  it.each(['completed', 'failed'] as const)('reads a %s shell result only after expansion instead of relying on the recent terminal buffer', async status => {
    const wrapper = render({ ...shell, status })
    await flushPromises()
    expect(getResult).not.toHaveBeenCalled()
    await wrapper.get('.tool-call-head').trigger('click')
    await flushPromises()
    expect(getResult).toHaveBeenCalledOnce()
    expect(getResult).toHaveBeenCalledWith(shell.id, expect.any(AbortSignal))
    expect(wrapper.get('.tool-result-raw').text()).toBe('first-output preserved after hundreds of later events')
    expect(wrapper.find('[data-testid="live-terminal"]').exists()).toBe(false)
  })

  it('keeps a running shell interactive and switches to its stored result once it finishes', async () => {
    const wrapper = render({ ...shell, status: 'running' })
    await wrapper.get('.tool-call-head').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="live-terminal"]').exists()).toBe(true)
    expect(getResult).not.toHaveBeenCalled()
    await wrapper.setProps({ toolCall: { ...shell, status: 'completed' } })
    await flushPromises()
    expect(wrapper.find('[data-testid="live-terminal"]').exists()).toBe(false)
    expect(wrapper.get('.tool-result-raw').text()).toBe('first-output preserved after hundreds of later events')
    expect(getResult).toHaveBeenCalledOnce()
  })
})
