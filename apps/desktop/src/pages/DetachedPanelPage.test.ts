// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { shallowMount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { reactive, ref, nextTick } from 'vue'
import type { EventEnvelope } from '@/api'

const h = vi.hoisted(() => ({
  route: null as unknown as { query: Record<string, string> },
  handlers: new Set<(event: EventEnvelope) => unknown>(),
  listMessagePage: vi.fn(), getSessionActivity: vi.fn(),
  listApprovals: vi.fn(), listApprovalRules: vi.fn(), getOrchestrationSnapshot: vi.fn(),
  hydrate: vi.fn(), reset: vi.fn(), notify: vi.fn(), error: vi.fn(), dismiss: vi.fn(),
}))
vi.mock('vue-router', () => ({ useRoute: () => h.route }))
vi.mock('vue-i18n', () => ({ useI18n: () => ({ t: (key: string) => key }) }))
vi.mock('@/api', () => ({ api: {
  listMessagePage: (...args: unknown[]) => h.listMessagePage(...args),
  getSessionActivity: (...args: unknown[]) => h.getSessionActivity(...args),
  listApprovals: (...args: unknown[]) => h.listApprovals(...args),
  listApprovalRules: (...args: unknown[]) => h.listApprovalRules(...args),
  getOrchestrationSnapshot: (...args: unknown[]) => h.getOrchestrationSnapshot(...args),
}, createUserToolActionForPath: vi.fn() }))
vi.mock('@/lib/storageScope', () => ({ setSelectedStorage: vi.fn(), registerSessionStorage: vi.fn(), scopedApi: (api: unknown) => api }))
vi.mock('@/composables/useTheme', () => ({ useTheme: () => ({ theme: ref('dark'), accentColor: ref('blue'), applyInitialTheme: () => {} }) }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: { error: h.notify }, status: { error: h.error }, dismissByKey: h.dismiss }) }))
vi.mock('@/composables/useAgentActivity', () => ({ useAgentActivity: () => ({
  activity: ref({}), thinkingSteps: ref([]), agentStates: ref({}), progressEvents: ref([]), hydrate: h.hydrate, reset: h.reset,
}) }))
vi.mock('@/lib/sessionEventBus', () => ({ followSession: vi.fn(), subscribeToSessionEvents: (handler: (event: EventEnvelope) => unknown) => {
  h.handlers.add(handler); return () => h.handlers.delete(handler)
} }))
vi.mock('@/components/GitPanel.vue', () => ({ default: { name: 'GitPanel', template: '<div />' } }))
vi.mock('@/components/ApprovalTab.vue', () => ({ default: { name: 'ApprovalTab', template: '<div />' } }))
vi.mock('@/components/EventsTab.vue', () => ({ default: { name: 'EventsTab', props: ['events'], template: '<div />' } }))
vi.mock('@/components/DoctorTab.vue', () => ({ default: { name: 'DoctorTab', template: '<div />' } }))
vi.mock('@/components/OrchestrationTab.vue', () => ({ default: { name: 'OrchestrationTab', template: '<div />' } }))
vi.mock('@/components/organization/OrganizationPanel.vue', () => ({ default: { name: 'OrganizationPanel', template: '<div />' } }))
vi.mock('@/components/PreviewBrowserPanel.vue', () => ({ default: { name: 'PreviewBrowserPanel', template: '<div />' } }))
vi.mock('@/components/AgentActivityPanel.vue', () => ({ default: { name: 'AgentActivityPanel', template: '<div />' } }))
vi.mock('@/components/TerminalPanel.vue', () => ({ default: { name: 'TerminalPanel', template: '<div />' } }))
import DetachedPanelPage from './DetachedPanelPage.vue'
import EventsTab from '@/components/EventsTab.vue'

let wrapper: VueWrapper | null = null
const state = (session: string, storage = 'user') => JSON.stringify({ sessionId: session, storageId: storage })
const snapshot = (id: string) => ({ events: [{ run_id: id }], tool_executions: [], runs: [] })
const page = (run = 'run-1') => ({ items: [], runs: [{ id: run }], has_more: false, next_before_sequence: null, history_revision: 1, revert_epoch: 0 })
async function settle() { await vi.advanceTimersByTimeAsync(60); await flushPromises(); await nextTick() }
function emit(type: string, run_id = 'run-1', payload: Record<string, unknown> = {}) {
  const event = { type, run_id, session_id: 'session-1', seq: 1, payload } as EventEnvelope
  return Promise.all([...h.handlers].map(handler => handler(event)))
}
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  vi.clearAllMocks(); h.handlers.clear()
  h.route = reactive({ query: { type: 'agent', state: state('session-1') } })
  h.listMessagePage.mockResolvedValue(page())
  h.getSessionActivity.mockResolvedValue(snapshot('run-1'))
  h.listApprovals.mockResolvedValue([]); h.listApprovalRules.mockResolvedValue([]); h.getOrchestrationSnapshot.mockResolvedValue(null)
})
afterEach(() => { wrapper?.unmount(); wrapper = null; vi.useRealTimers() })

describe('DetachedPanelPage read ownership', () => {
  it('coalesces a tool burst into one activity read without unrelated panel reads', async () => {
    wrapper = shallowMount(DetachedPanelPage); await settle()
    h.getSessionActivity.mockClear(); h.listMessagePage.mockClear()
    const updates = Array.from({ length: 100 }, () => emit('tool.execution.completed'))
    await settle(); await Promise.all(updates)
    expect(h.getSessionActivity).toHaveBeenCalledTimes(1)
    expect(h.listMessagePage).not.toHaveBeenCalled()
    expect(h.listApprovals).not.toHaveBeenCalled()
    expect(h.getOrchestrationSnapshot).not.toHaveBeenCalled()
  })
  it('does not hydrate a late read from the previous session or storage', async () => {
    let finish!: (result: ReturnType<typeof snapshot>) => void
    h.getSessionActivity.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    wrapper = shallowMount(DetachedPanelPage); await settle()
    const oldSignal = h.getSessionActivity.mock.calls[0][2] as AbortSignal
    h.listMessagePage.mockResolvedValue(page('new-run')); h.getSessionActivity.mockResolvedValue(snapshot('new-run'))
    h.route.query.state = state('session-2', 'other-scope'); await nextTick(); await settle()
    expect(oldSignal.aborted).toBe(true)
    finish(snapshot('old-run')); await flushPromises()
    expect(h.hydrate).toHaveBeenCalledTimes(1)
    expect(h.hydrate).toHaveBeenCalledWith(snapshot('new-run').events, [], ['new-run'])
    expect(wrapper.find('.detached-panel-loading').exists()).toBe(false)
  })
  it('cancels the old snapshot when history changes before it completes', async () => {
    let finish!: (result: ReturnType<typeof snapshot>) => void
    h.getSessionActivity.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    wrapper = shallowMount(DetachedPanelPage); await settle()
    const oldSignal = h.getSessionActivity.mock.calls[0][2] as AbortSignal
    h.listMessagePage.mockResolvedValue(page('retained-run')); h.getSessionActivity.mockResolvedValue(snapshot('retained-run'))
    const changing = emit('history.changed'); await settle(); await changing
    expect(oldSignal.aborted).toBe(true)
    finish(snapshot('reverted-run')); await flushPromises()
    expect(h.hydrate).toHaveBeenCalledTimes(1)
    expect(h.hydrate).toHaveBeenCalledWith(snapshot('retained-run').events, [], ['retained-run'])
  })
  it('completes the activity snapshot while orchestration remains pending', async () => {
    let finish!: (result: null) => void
    h.route.query.type = 'orchestration'
    h.getOrchestrationSnapshot.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    wrapper = shallowMount(DetachedPanelPage); await settle()
    expect(h.hydrate).toHaveBeenCalledTimes(1)
    const ready = emit('stream.ready'); await settle(); await ready
    const before = h.getSessionActivity.mock.calls.length
    const live = emit('tool.execution.completed'); await settle(); await live
    expect(h.getSessionActivity.mock.calls.length - before).toBe(1)
    finish(null); await settle()
  })
  it('clears diagnostic frames on history changes and session or storage switches', async () => {
    h.route.query.type = 'events'
    wrapper = shallowMount(DetachedPanelPage); await settle()
    await emit('diagnostic.before-revert'); await nextTick()
    expect(wrapper.findComponent(EventsTab).props('events')).toHaveLength(1)
    await emit('history.changed'); await nextTick()
    expect(wrapper.findComponent(EventsTab).props('events')).toEqual([])
    await emit('diagnostic.before-scope-switch'); await nextTick()
    expect(wrapper.findComponent(EventsTab).props('events')).toHaveLength(1)
    h.route.query.state = state('session-2', 'other-scope'); await nextTick(); await settle()
    expect(wrapper.findComponent(EventsTab).props('events')).toEqual([])
  })
  it('drops activity revoked in another window while disconnected before hydrating the reconnect snapshot', async () => {
    wrapper = shallowMount(DetachedPanelPage); await settle()
    expect(h.hydrate).toHaveBeenCalledWith(snapshot('run-1').events, [], ['run-1'])
    h.reset.mockClear(); h.hydrate.mockClear(); h.getSessionActivity.mockClear()
    h.listMessagePage.mockResolvedValue({ ...page('retained-run'), revert_epoch: 2 })
    h.getSessionActivity.mockResolvedValue(snapshot('retained-run'))
    const ready = emit('stream.ready', undefined, { revert_epoch: 2 }); await settle(); await ready
    expect(h.reset).toHaveBeenCalledTimes(1)
    expect(h.getSessionActivity).toHaveBeenCalledWith('session-1', ['retained-run'], expect.any(AbortSignal))
    expect(h.hydrate).toHaveBeenCalledWith(snapshot('retained-run').events, [], ['retained-run'])
  })
  it('stops after one recovery when history changes twice and retries independently on a later ready frame', async () => {
    wrapper = shallowMount(DetachedPanelPage); await settle()
    h.hydrate.mockClear(); h.reset.mockClear(); h.listMessagePage.mockClear(); h.getSessionActivity.mockClear()
    h.listMessagePage.mockResolvedValueOnce({ ...page('unstable-one'), revert_epoch: 1 })
      .mockResolvedValueOnce({ ...page('unstable-two'), revert_epoch: 2 })
    const unstable = emit('stream.ready', undefined, { revert_epoch: 0 })
    await settle(); await unstable
    expect(h.listMessagePage).toHaveBeenCalledTimes(2)
    expect(h.getSessionActivity).not.toHaveBeenCalled()
    expect(h.hydrate).not.toHaveBeenCalled()
    expect(h.reset).not.toHaveBeenCalled()
    expect(h.error).toHaveBeenCalledWith(expect.objectContaining({ key: 'detached-panel', message: '会话历史再次更新，请重试读取。' }))
    expect(wrapper.find('.detached-panel-loading').exists()).toBe(false)
    await settle()
    expect(h.listMessagePage).toHaveBeenCalledTimes(2)
    h.listMessagePage.mockResolvedValue({ ...page('stable-two'), revert_epoch: 2 })
    h.getSessionActivity.mockResolvedValue(snapshot('stable-two'))
    const retry = emit('stream.ready', undefined, { revert_epoch: 2 })
    await settle(); await retry
    expect(h.listMessagePage).toHaveBeenCalledTimes(3)
    expect(h.hydrate).toHaveBeenCalledWith(snapshot('stable-two').events, [], ['stable-two'])
    expect(h.dismiss).toHaveBeenCalledWith('detached-panel')
  })
  it('refreshes the latest page on reconnect to include a run completed while disconnected', async () => {
    wrapper = shallowMount(DetachedPanelPage); await settle()
    h.reset.mockClear(); h.getSessionActivity.mockClear(); h.listMessagePage.mockClear()
    h.listMessagePage.mockResolvedValue({ ...page('completed-offline'), runs: [{ id: 'completed-offline', status: 'completed' }] })
    h.getSessionActivity.mockResolvedValue(snapshot('completed-offline'))
    const ready = emit('stream.ready', undefined, { revert_epoch: 0 }); await settle(); await ready
    expect(h.listMessagePage).toHaveBeenCalledTimes(1)
    expect(h.getSessionActivity).toHaveBeenCalledWith('session-1', ['run-1', 'completed-offline'], expect.any(AbortSignal))
    expect(h.reset).not.toHaveBeenCalled()
  })
})
