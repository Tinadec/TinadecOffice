// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import zh from '@/locales/zh-CN'
import { COMPANION_WORK_SESSION_EVENT } from '@/events/companionEventTypes'
import { pendingConversationId } from '@/lib/pageRequests'
import type { CompanionConnectedWorkSession } from '@/lib/companionApi'
import { applyLiveStreamNotice, resetCompanionStore } from '@/lib/companionStore'
import CompanionWorkSessionCard from './CompanionWorkSessionCard.vue'

const routerPush = vi.hoisted(() => vi.fn())
const apiMocks = vi.hoisted(() => ({
  decideApproval: vi.fn(),
  cancelRun: vi.fn(),
}))
const notifyMocks = vi.hoisted(() => ({
  error: vi.fn(),
  success: vi.fn(),
  info: vi.fn(),
  warning: vi.fn(),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: routerPush }),
}))

vi.mock('@/lib/companionApi', () => ({
  companionApi: apiMocks,
  companionStreamSupported: () => false,
  subscribeCompanionFeed: () => () => {},
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: notifyMocks,
    confirm: vi.fn(async () => true),
  }),
}))

enableAutoUnmount(afterEach)

function sampleSession(over: Partial<CompanionConnectedWorkSession> = {}): CompanionConnectedWorkSession {
  return {
    session_id: 'sess-42',
    title: 'Implement Companion Hub',
    project_id: 'proj-1',
    project_name: 'TinadecOffice',
    mode_name: 'Team',
    view_mode: 'flat',
    latest_run_id: 'run-77',
    latest_run_status: 'awaiting_approval',
    latest_run_summary: 'Waiting for shell approval',
    plan_summary: '2/4 · Verify vitest suites',
    plan_progress: { done: 2, total: 4, current_step: 'Verify vitest suites', summary: '2/4 · Verify vitest suites' },
    pending_approvals: [
      {
        approval_id: 'appr-1',
        tool_id: 'shell',
        summary: 'Run bun test',
        risk_level: 'high',
        created_at: '2026-11-22T04:00:00Z',
      },
    ],
    ...over,
  }
}

function mountCard(session: CompanionConnectedWorkSession) {
  const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
  return mount(CompanionWorkSessionCard, {
    props: { session },
    global: { plugins: [i18n] },
  })
}

beforeEach(() => {
  resetCompanionStore()
  pendingConversationId.value = null
  routerPush.mockReset()
  apiMocks.decideApproval.mockReset().mockResolvedValue({ id: 'appr-1', decision: 'approved' })
  apiMocks.cancelRun.mockReset().mockResolvedValue({ run_id: 'run-77', status: 'cancelled', action: 'cancel', accepted: true })
  notifyMocks.error.mockReset()
})

afterEach(() => {
  resetCompanionStore()
  delete (window as unknown as { tinadec?: unknown }).tinadec
})

describe('CompanionWorkSessionCard', () => {
  it('renders workspace, mode, status, plan progress, and lets user approve/reject, stop, and jump to workspace', async () => {
    const wrapper = mountCard(sampleSession())
    expect(wrapper.text()).toContain('TinadecOffice')
    expect(wrapper.text()).toContain('Team')
    expect(wrapper.text()).toContain('Implement Companion Hub')
    expect(wrapper.get('[data-testid="work-session-plan-progress"]').text()).toContain('2 / 4')
    expect(wrapper.get('[data-testid="work-session-plan-progress"]').text()).toContain('Verify vitest suites')

    // Approve pending tool call directly
    await wrapper.get('[data-testid="work-session-approve-btn"]').trigger('click')
    await flushPromises()
    expect(apiMocks.decideApproval).toHaveBeenCalledWith('appr-1', true)

    // Cancel active run directly
    await wrapper.get('[data-testid="work-session-cancel-btn"]').trigger('click')
    await flushPromises()
    expect(apiMocks.cancelRun).toHaveBeenCalledWith('run-77')

    // Jump into workspace
    await wrapper.get('[data-testid="work-session-open-btn"]').trigger('click')
    expect(pendingConversationId.value).toBe('sess-42')
    expect(routerPush).toHaveBeenCalledWith('/')
  })

  it('reacts immediately to companion.work_session SSE updates and renders browser handoff takeover controls', async () => {
    const takeOver = vi.fn(async () => ({ ok: true }))
    const openWindow = vi.fn(async () => ({ ok: true }))
    Object.defineProperty(window, 'tinadec', {
      configurable: true,
      value: { agentBrowser: { takeOver, openWindow } },
    })

    const wrapper = mountCard(sampleSession({
      latest_run_status: 'running',
      pending_approvals: [],
    }))
    expect(wrapper.find('[data-testid="work-session-browser-handoff"]').exists()).toBe(false)

    // Push live SSE browser_handoff transition into companionStore
    applyLiveStreamNotice({
      type: COMPANION_WORK_SESSION_EVENT,
      seq: 0,
      kind: 'browser_handoff',
      companionId: 'c-1',
      conversationId: 'conv-1',
      runId: 'run-77',
      workSession: {
        sessionId: 'sess-42',
        sessionTitle: 'Implement Companion Hub',
        workspaceId: null,
        projectId: 'proj-1',
        projectName: 'TinadecOffice',
        runId: 'run-77',
        status: 'browser_handoff',
        modeName: 'Team',
        viewMode: 'flat',
        summary: 'Complete 2FA login in browser',
        planSummary: '3/4 · Authenticate OAuth portal',
        pendingApprovals: [],
        browserSpaceId: 'space-9',
        browserUrl: 'https://github.com/login',
        browserDriver: 'ego_lite',
        browserHandoffReason: '2FA verification required',
        updatedAt: '2026-11-22T04:10:00Z',
      },
    })
    await flushPromises()

    expect(wrapper.find('[data-testid="work-session-browser-handoff"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('需要你接管浏览器')
    expect(wrapper.text()).toContain('3 / 4')

    await wrapper.get('[data-testid="work-session-browser-open-btn"]').trigger('click')
    await flushPromises()
    expect(openWindow).toHaveBeenCalledWith('space-9')

    await wrapper.get('[data-testid="work-session-browser-takeover-btn"]').trigger('click')
    await flushPromises()
    expect(takeOver).toHaveBeenCalledWith('space-9')
    expect(wrapper.emitted('browser-takeover')).toHaveLength(1)
    expect(wrapper.find('[data-testid="work-session-browser-handoff"]').exists()).toBe(false)
  })
})
