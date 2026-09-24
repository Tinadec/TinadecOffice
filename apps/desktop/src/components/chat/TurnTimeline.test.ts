// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import TurnTimeline from './TurnTimeline.vue'
import type { SupervisionReview } from '@/composables/useAgentActivity'
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
