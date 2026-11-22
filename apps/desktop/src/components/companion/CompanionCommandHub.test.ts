// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import zh from '@/locales/zh-CN'
import { COMPANION_TOOL_CALL_EVENT, COMPANION_WORK_SESSION_EVENT } from '@/events/companionEventTypes'
import { pendingConversationId } from '@/lib/pageRequests'
import type { CompanionConnectedWorkSession, CompanionConnection, CompanionConversation, CompanionSummary } from '@/lib/companionApi'
import { applyLiveStreamNotice, resetCompanionStore } from '@/lib/companionStore'
import CompanionThreadWorkspace from './CompanionThreadWorkspace.vue'
import CompanionRightDock from './CompanionRightDock.vue'

const routerPush = vi.hoisted(() => vi.fn())
const apiMocks = vi.hoisted(() => ({
  getConversation: vi.fn(),
  ensurePrivateConversation: vi.fn(),
  listMessages: vi.fn(),
  listMandates: vi.fn(),
  listRecruitments: vi.fn(),
  listConnections: vi.fn(),
  addConnection: vi.fn(),
  patchConnection: vi.fn(),
  listWorkSessions: vi.fn(),
  listBrowserProfiles: vi.fn(),
  listProjects: vi.fn(),
  decideApproval: vi.fn(),
  cancelRun: vi.fn(),
  sendMessage: vi.fn(),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: routerPush }),
}))

vi.mock('@/lib/hostAccess', () => ({
  useHostAccess: () => ({ canAccessBackend: ref(true), isElectron: ref(true) }),
}))

vi.mock('@/lib/companionApi', () => ({
  companionApi: apiMocks,
  companionStreamSupported: () => false,
  subscribeCompanionFeed: () => () => {},
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: { error: vi.fn(), success: vi.fn(), info: vi.fn(), warning: vi.fn() },
    confirm: vi.fn(async () => true),
  }),
}))

enableAutoUnmount(afterEach)

const BOT: CompanionSummary = {
  id: 'c-1',
  name: 'Alpha',
  avatar: 'fox',
  tagline: 'Commander',
  model_purpose: 'chat',
  model: null,
  model_provider_id: null,
  current_activity: 'idle',
  autonomy_enabled: true,
  autonomy_paused: false,
  memory_count: 1,
  skill_count: 0,
  connection_count: 1,
  revision: 1,
  status: 'active',
  role: 'autonomous',
  created_at: '2026-11-22T00:00:00Z',
  updated_at: '2026-11-22T00:00:00Z',
  last_activity_at: null,
}

const CONV: CompanionConversation = {
  id: 'conv-1',
  kind: 'private',
  title: null,
  origin: 'user',
  can_open: true,
  autonomy_paused: false,
  message_count: 1,
  turn_budget: 6,
  last_message_at: '2026-11-22T00:00:00Z',
  last_message_preview: 'hi',
  members: [],
}

const WORK_SESSION: CompanionConnectedWorkSession = {
  session_id: 'sess-100',
  title: 'Ship Task 37 Hub',
  project_id: 'proj-1',
  project_name: 'TinadecOffice',
  mode_name: 'Team',
  view_mode: 'flat',
  latest_run_id: 'run-200',
  latest_run_status: 'awaiting_approval',
  latest_run_summary: 'Needs approval for shell',
  plan_summary: '2/4 · Build frontend hub',
  plan_progress: { done: 2, total: 4, current_step: 'Build frontend hub', summary: '2/4 · Build frontend hub' },
  pending_approvals: [
    {
      approval_id: 'appr-9',
      tool_id: 'shell',
      summary: 'Run vitest',
      risk_level: 'medium',
      created_at: '2026-11-22T04:00:00Z',
    },
  ],
}

beforeEach(() => {
  resetCompanionStore()
  pendingConversationId.value = null
  routerPush.mockReset()
  apiMocks.getConversation.mockResolvedValue(CONV)
  apiMocks.ensurePrivateConversation.mockResolvedValue(CONV)
  apiMocks.listMessages.mockResolvedValue([])
  apiMocks.listMandates.mockResolvedValue([])
  apiMocks.listRecruitments.mockResolvedValue([])
  apiMocks.listConnections.mockResolvedValue([])
  apiMocks.addConnection.mockResolvedValue({})
  apiMocks.patchConnection.mockImplementation(async (_cid: string, id: string, rev: number, patch: Record<string, unknown>) => ({
    id,
    companion_id: 'c-1',
    kind: 'work_session',
    name: 'All Workspaces',
    endpoint: '*',
    authorization: String(patch.authorization ?? 'authorized'),
    runtime_status: patch.authorization === 'revoked' ? 'disconnected' : 'connected',
    enabled: typeof patch.enabled === 'boolean' ? patch.enabled : true,
    revision: rev + 1,
  }))
  apiMocks.listWorkSessions.mockResolvedValue([WORK_SESSION])
  apiMocks.listBrowserProfiles.mockResolvedValue([
    { id: 'work', name: 'Work Profile', driver: 'ego_lite' },
  ])
  apiMocks.decideApproval.mockResolvedValue({ id: 'appr-9', decision: 'approved' })
  apiMocks.cancelRun.mockResolvedValue({ run_id: 'run-200', status: 'cancelled', action: 'cancel', accepted: true })
  apiMocks.listProjects.mockResolvedValue([
    { id: 'proj-1', name: 'TinadecOffice', path: 'C:/git/TinadecOffice' },
  ])
})

afterEach(() => {
  resetCompanionStore()
})

describe('CompanionThreadWorkspace & CompanionRightDock Command Hub integration', () => {
  it('mounts inline CompanionWorkSessionCard in thread on SSE backflow and live command tool calls without duplicates', async () => {
    const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
    const wrapper = mount(CompanionThreadWorkspace, {
      props: { companion: BOT, conversationId: 'conv-1' },
      global: {
        plugins: [i18n],
        stubs: { AgentPetAvatar: true },
      },
    })
    await flushPromises()

    // Live SSE work_session backflow renders inline card immediately with approval actions
    applyLiveStreamNotice({
      type: COMPANION_WORK_SESSION_EVENT,
      seq: 0,
      kind: 'awaiting_approval',
      companionId: 'c-1',
      conversationId: 'conv-1',
      runId: 'run-200',
      workSession: {
        sessionId: 'sess-100',
        sessionTitle: 'Ship Task 37 Hub',
        workspaceId: null,
        projectId: 'proj-1',
        projectName: 'TinadecOffice',
        runId: 'run-200',
        status: 'awaiting_approval',
        modeName: 'Team',
        viewMode: 'flat',
        summary: 'Needs approval for shell',
        planSummary: '2/4 · Build frontend hub',
        pendingApprovals: WORK_SESSION.pending_approvals,
        browserSpaceId: null,
        browserUrl: null,
        browserDriver: null,
        browserHandoffReason: null,
        updatedAt: '2026-11-22T04:01:00Z',
      },
    })
    await flushPromises()

    expect(wrapper.findAll('[data-testid="companion-work-session-card-sess-100"]')).toHaveLength(1)
    await wrapper.get('[data-testid="work-session-approve-btn"]').trigger('click')
    await flushPromises()
    expect(apiMocks.decideApproval).toHaveBeenCalledWith('appr-9', true)

    // Streaming companion_dispatch tool call referencing a second session mounts its card inline in streaming bubble
    applyLiveStreamNotice({
      type: COMPANION_TOOL_CALL_EVENT,
      seq: 0,
      kind: null,
      companionId: 'c-1',
      conversationId: 'conv-1',
      runId: 'run-live-1',
      callId: 'call-1',
      toolName: 'companion_dispatch',
      toolStatus: 'completed',
      resultPreview: '{"session_id":"sess-202","run_id":"run-202","session_title":"Fix CI"}',
    })
    await flushPromises()

    expect(wrapper.find('[data-testid="companion-work-session-card-sess-202"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="companion-work-session-card-sess-202"]').text()).toContain('Fix CI')
  })

  it('supports quick work_session & browser grants, revoke, and live work-session controls in CompanionRightDock', async () => {
    const existingConn: CompanionConnection = {
      id: 'conn-ws-1',
      companion_id: 'c-1',
      kind: 'work_session',
      name: '工作会话 · 全部工作区 (*)',
      endpoint: '*',
      authorization: 'authorized',
      runtime_status: 'connected',
      enabled: true,
      revision: 2,
    }
    apiMocks.listConnections.mockResolvedValue([existingConn])

    const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
    const wrapper = mount(CompanionRightDock, {
      props: { companion: BOT, conversationId: 'conv-1' },
      global: {
        plugins: [i18n],
        stubs: {
          CompanionSchedulePlanTodoPanel: true,
          CompanionStage: true,
          PreviewBrowserPanel: true,
        },
      },
    })
    await flushPromises()

    await wrapper.get('[data-testid="dock-tab-devices"]').trigger('click')
    await flushPromises()

    // Quick grant work_session scoped to project
    await wrapper.get('[data-testid="dock-work-session-scope"]').setValue('proj-1')
    await wrapper.get('[data-testid="dock-grant-work-session-btn"]').trigger('click')
    await flushPromises()
    expect(apiMocks.addConnection).toHaveBeenCalledWith('c-1', expect.objectContaining({
      kind: 'work_session',
      endpoint: 'proj-1',
      authorization: 'authorized',
    }))

    // Quick grant browser with ego_lite + profile
    await wrapper.get('[data-testid="dock-browser-driver"]').setValue('ego_lite')
    await flushPromises()
    await wrapper.get('[data-testid="dock-browser-profile"]').setValue('work')
    await wrapper.get('[data-testid="dock-grant-browser-btn"]').trigger('click')
    await flushPromises()
    expect(apiMocks.addConnection).toHaveBeenCalledWith('c-1', expect.objectContaining({
      kind: 'browser',
      endpoint: 'ego_lite:work',
      driver: 'ego_lite',
      profile_id: 'work',
      authorization: 'authorized',
    }))

    // Quick revoke existing work_session binding
    await wrapper.get('[data-testid="dock-conn-auth-btn-conn-ws-1"]').trigger('click')
    await flushPromises()
    expect(apiMocks.patchConnection).toHaveBeenCalledWith('c-1', 'conn-ws-1', 2, { authorization: 'revoked' })

    // Live connected Work Session card renders with Stop & Open Workspace actions
    const wsCard = wrapper.get('[data-testid="companion-work-session-card-sess-100"]')
    expect(wsCard.text()).toContain('Ship Task 37 Hub')
    await wsCard.get('[data-testid="work-session-cancel-btn"]').trigger('click')
    await flushPromises()
    expect(apiMocks.cancelRun).toHaveBeenCalledWith('run-200')

    await wsCard.get('[data-testid="work-session-open-btn"]').trigger('click')
    expect(pendingConversationId.value).toBe('sess-100')
    expect(routerPush).toHaveBeenCalledWith('/')
  })
})
