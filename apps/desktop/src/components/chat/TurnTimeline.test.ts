// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import TurnTimeline from './TurnTimeline.vue'
import type { SupervisionReview } from '@/composables/useAgentActivity'
import type { ToolCall } from '@/composables/useAgentActivity'
import zh from '@/locales/zh-CN'

const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })

vi.mock('@/controllers/HomeController', () => ({
  homeController: { currentSession: { value: { id: 'session-1' } } },
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: { error: vi.fn() } }),
}))

const apiMock = vi.hoisted(() => ({
  controlRun: vi.fn(),
  createInteraction: vi.fn(),
}))

vi.mock('@/api', () => ({
  api: apiMock,
}))

const review: SupervisionReview = {
  runId: 'run-1',
  reasons: ['Task budget exhausted after three rounds.'],
  options: ['continue', 'correct', 'cancel'],
}

describe('TurnTimeline supervision review', () => {
  beforeEach(() => {
    apiMock.controlRun.mockClear()
    apiMock.createInteraction.mockClear()
  })
  it('shows escalation reasons and decisions inside the expanded activity', async () => {
    const wrapper = mount(TurnTimeline, {
      props: { supervisionReview: review },
      global: { plugins: [i18n] },
    })
    expect(wrapper.get('[data-testid="supervision-reasons"]').text()).toContain('Task budget exhausted')
    expect(wrapper.findAll('[data-testid="supervision-decision"]').map((button) => button.text()))
      .toEqual(['继续', '纠正', '取消'])
  })

  it('continues or cancels through durable run control', async () => {
    apiMock.controlRun.mockResolvedValue({})
    const wrapper = mount(TurnTimeline, { props: { supervisionReview: review }, global: { plugins: [i18n] } })
    await wrapper.get('[data-option="continue"]').trigger('click')
    await wrapper.get('[data-option="cancel"]').trigger('click')
    // "continue" is the escalation option's name, not a run-control verb: the
    // endpoint only accepts pause|resume|cancel, so the button has to translate.
    // Sending it verbatim was rejected as INVALID_RUN_CONTROL and did nothing.
    expect(apiMock.controlRun).toHaveBeenNthCalledWith(1, 'run-1', 'resume')
    expect(apiMock.controlRun).toHaveBeenNthCalledWith(2, 'run-1', 'cancel')
  })

  it('asks for correction text and injects steering into the parked run', async () => {
    apiMock.createInteraction.mockResolvedValue({})
    vi.stubGlobal('prompt', vi.fn(() => '请改用只读方式'))
    const wrapper = mount(TurnTimeline, { props: { supervisionReview: review }, global: { plugins: [i18n] } })
    await wrapper.get('[data-option="correct"]').trigger('click')
    expect(apiMock.createInteraction).toHaveBeenCalledWith('session-1', expect.objectContaining({
      content: '请改用只读方式',
      dispatch_mode: 'insert',
      target_run_id: 'run-1',
    }))
    expect(apiMock.controlRun).not.toHaveBeenCalled()
    vi.unstubAllGlobals()
  })
})

describe('TurnTimeline activity summary', () => {
  const call = {
    id: 'call-1', toolId: 'read_file', toolName: 'read_file', status: 'completed',
    startedAt: null, completedAt: null, durationMs: 12, argsSummary: 'README.md',
    resultSummary: 'ok', requiresApproval: false, approvalId: null, evidence: [], seq: 1, risk: 'low',
  } as unknown as ToolCall

  /**
   * The turn's own state decides the default: open while the run works, so the reader can see
   * what it is doing, and folded to one summary once it answers.
   */
  it('opens the trail while the run is still working', () => {
    const wrapper = mount(TurnTimeline, { props: { toolCalls: [call], settled: false }, global: { plugins: [i18n] } })
    expect(wrapper.find('[data-testid="activity-details"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="activity-toggle"]').attributes('aria-expanded')).toBe('true')
  })

  it('folds to one compact row once the turn has settled', () => {
    const wrapper = mount(TurnTimeline, { props: { toolCalls: [call], settled: true }, global: { plugins: [i18n] } })
    expect(wrapper.find('[data-testid="activity-summary"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="activity-toggle"]').attributes('aria-expanded')).toBe('false')
  })

  it('lets the reader open it, and never re-folds under them afterwards', async () => {
    const wrapper = mount(TurnTimeline, { props: { toolCalls: [call], settled: true }, global: { plugins: [i18n] } })
    await wrapper.get('[data-testid="activity-toggle"]').trigger('click')
    expect(wrapper.get('[data-testid="activity-toggle"]').attributes('aria-expanded')).toBe('true')
    // A lone read is quiet work, so it reaches the screen as a folded group rather than
    // a raw tool row — that layering is the point, and the target is what names it.
    expect(wrapper.text()).toContain('README.md')
    // The run keeps settling; the reader's choice stands.
    await wrapper.setProps({ settled: false })
    await wrapper.setProps({ settled: true })
    expect(wrapper.get('[data-testid="activity-toggle"]').attributes('aria-expanded')).toBe('true')
  })

  it('keeps a waiting approval out of the fold', () => {
    const waiting = { ...call, id: 'call-2', status: 'waiting_approval', approvalId: 'a1', requiresApproval: true } as unknown as ToolCall
    const wrapper = mount(TurnTimeline, { props: { toolCalls: [waiting], settled: true }, global: { plugins: [i18n] } })
    // Nothing to fold, so no summary row is invented — and the decision is on screen.
    expect(wrapper.find('[data-testid="activity-summary"]').exists()).toBe(false)
    expect(wrapper.find('.tool-call-approve-btn').exists()).toBe(true)
    expect(wrapper.find('.tool-call-reject-btn').exists()).toBe(true)
  })

  it('lifts an approval out of a fold that has other work in it', () => {
    const quiet = { ...call, id: 'call-1', toolId: 'read_file' } as unknown as ToolCall
    const waiting = { ...call, id: 'call-2', status: 'waiting_approval', approvalId: 'a1', requiresApproval: true } as unknown as ToolCall
    const wrapper = mount(TurnTimeline, { props: { toolCalls: [quiet, waiting], settled: true }, global: { plugins: [i18n] } })
    expect(wrapper.get('[data-testid="activity-toggle"]').attributes('aria-expanded')).toBe('false')
    // The approval's buttons are outside the collapsed region, so they survive the fold.
    expect(wrapper.find('.tool-call-approve-btn').exists()).toBe(true)
    const collapsed = wrapper.get('[data-testid="activity-details"]')
    expect(collapsed.find('.tool-call-approve-btn').exists()).toBe(false)
  })
})
