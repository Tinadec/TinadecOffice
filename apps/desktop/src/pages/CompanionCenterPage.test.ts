// @vitest-environment happy-dom
import { defineComponent, h, ref } from 'vue'
import { createI18n } from 'vue-i18n'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import zh from '@/locales/zh-CN'
import { resetCompanionStore } from '@/lib/companionStore'
import CompanionCenterPage from './CompanionCenterPage.vue'
import type {
  CompanionActivity,
  CompanionConnection,
  CompanionConnectionOverview,
  CompanionConversation,
  CompanionMandate,
  CompanionMessage,
  CompanionPlan,
  CompanionSchedule,
  CompanionSummary,
  CompanionTodo,
} from '@/lib/companionApi'

const routerPush = vi.hoisted(() => vi.fn())
const feed = vi.hoisted(() => ({
  available: false,
  handlers: new Set<(notice: unknown) => void>(),
}))

const api = vi.hoisted(() => ({
  listCompanions: vi.fn(async (): Promise<unknown[]> => []),
  listActivity: vi.fn(async (): Promise<unknown[]> => []),
  listAllConversations: vi.fn(async (): Promise<unknown[]> => []),
  listConnectionsOverview: vi.fn(async (): Promise<unknown[]> => []),
  getSettings: vi.fn(async () => ({
    main_companion_id: 'c-a',
    max_bot_hop_depth: 3,
    recruit_limit: 3,
    autonomy_paused: false,
    revision: 1,
  })),
  listCompanionAvatars: vi.fn(async (): Promise<unknown[]> => []),
  listConversations: vi.fn(async (): Promise<unknown[]> => []),
  ensurePrivateConversation: vi.fn(async (): Promise<unknown> => PRIVATE_CONV),
  getConversation: vi.fn(async (): Promise<unknown> => GROUP),
  listMessages: vi.fn(async (): Promise<unknown[]> => []),
  listMandates: vi.fn(async (): Promise<unknown[]> => []),
  listRecruitments: vi.fn(async (): Promise<unknown[]> => []),
  sendMessage: vi.fn(async (): Promise<unknown> => ({ duplicate: false, message: MESSAGES[0], runs: [], skipped_runs: [] })),
  pauseConversation: vi.fn(async () => ({ ...PRIVATE_CONV, autonomy_paused: true })),
  recordEmote: vi.fn(async () => ({ ok: true })),
  inviteMember: vi.fn(async () => GROUP),
  createGroupConversation: vi.fn(async () => GROUP),
  decideMandate: vi.fn(async () => ({})),
  decideRecruitment: vi.fn(async () => ({})),
  listSchedules: vi.fn(async (): Promise<unknown[]> => []),
  createSchedule: vi.fn(async (_id: string, input: { title: string; cron_expression?: string; prompt: string }) => ({
    id: 'sched-new',
    companion_id: 'c-a',
    title: input.title,
    cron_expression: input.cron_expression ?? '0 9 * * *',
    prompt: input.prompt,
    enabled: true,
    revision: 1,
    created_at: '2026-11-22T00:00:00Z',
    updated_at: '2026-11-22T00:00:00Z',
  })),
  patchSchedule: vi.fn(async () => ({})),
  triggerSchedule: vi.fn(async () => ({ schedule: SCHEDULES[0], runs: [] })),
  deleteSchedule: vi.fn(async () => ({})),
  listPlans: vi.fn(async (): Promise<unknown[]> => []),
  createPlan: vi.fn(async (_id: string, input: { title: string; summary?: string | null }) => ({
    id: 'plan-new',
    companion_id: 'c-a',
    title: input.title,
    summary: input.summary ?? null,
    status: 'active',
    steps: [],
    revision: 1,
    created_at: '2026-11-22T00:00:00Z',
    updated_at: '2026-11-22T00:00:00Z',
  })),
  patchPlan: vi.fn(async () => ({})),
  deletePlan: vi.fn(async () => ({})),
  listTodos: vi.fn(async (): Promise<unknown[]> => []),
  createTodo: vi.fn(async (_id: string, input: { title: string; priority?: string }) => ({
    id: 'todo-new',
    companion_id: 'c-a',
    title: input.title,
    priority: input.priority ?? 'normal',
    status: 'pending',
    revision: 1,
    created_at: '2026-11-22T00:00:00Z',
    updated_at: '2026-11-22T00:00:00Z',
  })),
  patchTodo: vi.fn(async (_cid: string, tid: string) => ({
    id: tid,
    companion_id: 'c-a',
    title: 'Check PR',
    priority: 'normal',
    status: 'done',
    revision: 2,
    created_at: '2026-11-22T00:00:00Z',
    updated_at: '2026-11-22T00:01:00Z',
  })),
  deleteTodo: vi.fn(async () => ({})),
  listConnections: vi.fn(async (): Promise<unknown[]> => []),
  patchConnection: vi.fn(async () => ({})),
  tickAutonomy: vi.fn(async () => ({})),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: routerPush }),
}))

vi.mock('@/lib/hostAccess', () => ({
  useHostAccess: () => ({ canAccessBackend: ref(true), isElectron: ref(true) }),
}))

vi.mock('@/composables/usePanelStyles', () => ({
  usePanelStyles: () => ({
    getPanelStyle: () => ({}),
    getPanelDataAttributes: () => ({}),
  }),
}))

vi.mock('@/lib/companionApi', () => ({
  companionApi: api,
  companionStreamSupported: () => feed.available,
  subscribeCompanionFeed: (handler: (notice: unknown) => void) => {
    feed.handlers.add(handler)
    return () => { feed.handlers.delete(handler) }
  },
}))

vi.mock('@/settings/sections/CompanionEditorDrawer.vue', () => ({
  default: defineComponent({
    name: 'CompanionEditorDrawer',
    props: ['open', 'companionId', 'initialTab', 'live2dAvatars'],
    emits: ['update:open', 'close', 'saved', 'created'],
    setup(props) {
      return () => props.open
        ? h('div', {
            'data-testid': 'editor-drawer-probe',
            'data-companion-id': props.companionId ?? '',
            'data-tab': props.initialTab ?? 'identity',
          })
        : null
    },
  }),
}))

function emitNotice(notice: Record<string, unknown>): void {
  for (const handler of [...feed.handlers]) handler(notice)
}

enableAutoUnmount(afterEach)

const COMPANION_A: CompanionSummary = {
  id: 'c-a', name: 'Alpha', avatar: 'fox', tagline: 'Review partner', model_purpose: 'chat',
  model: null, model_provider_id: null,
  current_activity: 'thinking', autonomy_enabled: true, autonomy_paused: false,
  memory_count: 3, skill_count: 2, connection_count: 1, revision: 1,
  status: 'active', role: 'autonomous', created_at: '2026-10-01T00:00:00Z', updated_at: '2026-10-01T00:00:00Z',
  last_activity_at: '2026-10-27T10:00:00Z',
}
const COMPANION_B: CompanionSummary = {
  ...COMPANION_A, id: 'c-b', name: 'Beta', avatar: 'cat', current_activity: 'idle',
  autonomy_enabled: false, autonomy_paused: false, last_activity_at: null,
}
const PARTNER_LEGACY: CompanionSummary = {
  ...COMPANION_A, id: 'c-partner', name: 'Partner', role: 'session_partner',
}

const PRIVATE_CONV: CompanionConversation = {
  id: 'conv-private', kind: 'private', title: null, origin: 'user', can_open: true,
  autonomy_paused: false, message_count: 2, turn_budget: 6, last_message_at: '2026-10-27T10:06:00Z',
  last_message_preview: 'Latest private line',
  members: [
    { display_name: 'You', member_kind: 'user', status: 'active', joined_at: '2026-10-01T00:00:00Z' },
    { display_name: 'Alpha', member_kind: 'companion', companion_id: 'c-a', status: 'active', joined_at: '2026-10-01T00:00:00Z' },
  ],
}

const GROUP: CompanionConversation = {
  id: 'conv-group', kind: 'group', title: 'Alpha and Beta', origin: 'user', can_open: true,
  autonomy_paused: false, message_count: 2, turn_budget: 6, last_message_at: '2026-10-27T10:06:00Z',
  last_message_preview: 'Latest group line',
  members: [
    { display_name: 'You', member_kind: 'user', status: 'active', joined_at: '2026-10-01T00:00:00Z' },
    { display_name: 'Alpha', member_kind: 'companion', companion_id: 'c-a', status: 'active', joined_at: '2026-10-01T00:00:00Z' },
    { display_name: 'Beta', member_kind: 'companion', companion_id: 'c-b', status: 'active', joined_at: '2026-10-01T00:00:00Z' },
  ],
}

const MESSAGES: CompanionMessage[] = [
  {
    id: 'm-1', conversation_id: 'conv-private', seq: 1, author_kind: 'user', author_name: 'You',
    body: 'Hello Alpha', addressed_to: [], created_at: '2026-10-27T10:05:30Z',
  },
  {
    id: 'm-2', conversation_id: 'conv-private', seq: 2, author_kind: 'companion', author_name: 'Alpha',
    author_companion_id: 'c-a', body: 'Ready to help', addressed_to: [], created_at: '2026-10-27T10:06:00Z',
  },
]

const ACTIVITY: CompanionActivity[] = [
  {
    id: 'a-1', kind: 'run_completed', companion_id: 'c-a', companion_name: 'Alpha',
    summary: 'Answered a question', created_at: '2026-10-27T10:05:00Z',
    conversation_id: 'conv-private', conversation_title: 'Alpha',
  },
  {
    id: 'a-2', kind: 'run_failed', companion_id: 'c-a', companion_name: 'Alpha',
    summary: 'Failed once', payload: { error_code: 'core_unreachable' },
    created_at: '2026-10-27T10:03:00Z',
  },
]

const CONNECTIONS: CompanionConnectionOverview[] = [
  {
    id: 'conn-1', companion_id: 'c-a', companion_name: 'Alpha', kind: 'mcp', name: 'Local MCP',
    authorization: 'authorized', runtime_status: 'unknown', enabled: true,
    last_checked_at: '2026-10-27T09:00:00Z', revision: 1,
  },
]

const BOT_CONNECTIONS: CompanionConnection[] = [
  {
    id: 'conn-1', companion_id: 'c-a', kind: 'mcp', name: 'Local MCP',
    enabled: true, authorization: 'authorized', runtime_status: 'unknown',
    revision: 1,
  },
]

const SCHEDULES: CompanionSchedule[] = [
  {
    id: 'sched-1', companion_id: 'c-a', title: 'Morning Standup',
    cron_expression: '0 9 * * 1-5', prompt: 'Summarize open PRs', enabled: true,
    run_at: null, next_run_at: null, last_run_at: null, target_conversation_id: null,
    one_shot: false, status: 'active',
    revision: 1, created_at: '2026-11-22T00:00:00Z', updated_at: '2026-11-22T00:00:00Z',
  },
]

const PLANS: CompanionPlan[] = [
  {
    id: 'plan-1', companion_id: 'c-a', title: 'Q4 Polish', summary: 'Ship workspace',
    status: 'active', steps: [], revision: 1,
    created_at: '2026-11-22T00:00:00Z', updated_at: '2026-11-22T00:00:00Z',
  },
]

const TODOS: CompanionTodo[] = [
  {
    id: 'todo-1', companion_id: 'c-a', title: 'Check PR', priority: 'medium',
    status: 'pending', note: null, due_at: null, plan_id: null, revision: 1,
    created_at: '2026-11-22T00:00:00Z', updated_at: '2026-11-22T00:00:00Z',
  },
]

const MANDATES: CompanionMandate[] = [
  {
    id: 'mandate-1', companion_id: 'c-a', action: 'run_cleanup',
    summary: 'Archive old drafts', status: 'proposed', revision: 1,
    created_at: '2026-11-22T00:00:00Z', updated_at: '2026-11-22T00:00:00Z',
  } as CompanionMandate,
]

function mountWorkspace() {
  const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
  return mount(CompanionCenterPage, {
    global: {
      plugins: [i18n],
      stubs: {
        BrandLogo: true,
        TinadecCalligraphy: true,
        AgentPetAvatar: true,
        CompanionStage: true,
        PreviewBrowserPanel: true,
      },
    },
  })
}

beforeEach(() => {
  resetCompanionStore()
  feed.available = false
  feed.handlers.clear()
  routerPush.mockReset()
  api.listCompanions.mockResolvedValue([COMPANION_A, COMPANION_B, PARTNER_LEGACY])
  api.listActivity.mockResolvedValue(ACTIVITY)
  api.listAllConversations.mockResolvedValue([GROUP])
  api.listConnectionsOverview.mockResolvedValue(CONNECTIONS)
  api.listConversations.mockResolvedValue([PRIVATE_CONV])
  api.ensurePrivateConversation.mockResolvedValue(PRIVATE_CONV)
  api.getConversation.mockResolvedValue(GROUP)
  api.listMessages.mockResolvedValue(MESSAGES)
  api.listMandates.mockResolvedValue([])
  api.listRecruitments.mockResolvedValue([])
  api.listSchedules.mockResolvedValue(SCHEDULES)
  api.listPlans.mockResolvedValue(PLANS)
  api.listTodos.mockResolvedValue(TODOS)
  api.listConnections.mockResolvedValue(BOT_CONNECTIONS)
})

afterEach(() => {
  vi.useRealTimers()
  vi.clearAllMocks()
  resetCompanionStore()
  feed.handlers.clear()
})

describe('CompanionCenterPage 3-column Bot Workspace', () => {
  it('mounts contacts rail, thread workspace, and right dock while filtering autonomous bots', async () => {
    const wrapper = mountWorkspace()
    await flushPromises()

    expect(wrapper.emitted('ready')).toHaveLength(1)
    expect(wrapper.find('[data-testid="companion-contacts-rail"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="companion-thread-workspace"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="companion-right-dock"]').exists()).toBe(true)

    const bots = wrapper.findAll('[data-testid="companion-contact-bot"]')
    // Only autonomous bots (Alpha, Beta) render in Bot contacts; session_partner is excluded.
    expect(bots).toHaveLength(2)
    expect(bots[0]!.text()).toContain('Alpha')
    expect(bots[1]!.text()).toContain('Beta')

    // Selected main bot auto-loads private conversation & messages cleanly.
    expect(wrapper.get('[data-testid="companion-message-log"]').text()).toContain('Hello Alpha')
    expect(wrapper.get('[data-testid="companion-message-log"]').text()).toContain('Ready to help')
  })

  it('opens CompanionEditorDrawer for create (+) and edit without leaving workspace', async () => {
    const wrapper = mountWorkspace()
    await flushPromises()

    await wrapper.get('[data-testid="companion-create-btn"]').trigger('click')
    await flushPromises()
    let drawer = wrapper.get('[data-testid="editor-drawer-probe"]')
    expect(drawer.attributes('data-companion-id')).toBe('')

    await wrapper.findAll('[data-testid="companion-contact-edit"]')[0]!.trigger('click')
    await flushPromises()
    drawer = wrapper.get('[data-testid="editor-drawer-probe"]')
    expect(drawer.attributes('data-companion-id')).toBe('c-a')
  })

  it('creates a new multi-bot group conversation from the contacts rail', async () => {
    const wrapper = mountWorkspace()
    await flushPromises()

    await wrapper.get('[data-testid="companion-new-group-btn"]').trigger('click')
    const form = wrapper.get('[data-testid="companion-group-form"]')
    await form.get('input[type="text"], input:not([type])').setValue('Release Crew')
    await form.get('input[type="checkbox"]').setValue(true)
    await form.trigger('submit')
    await flushPromises()

    expect(api.createGroupConversation).toHaveBeenCalledWith('c-a', expect.objectContaining({
      title: 'Release Crew',
    }))
  })

  it('renders live SSE delta and tool_call chips then settles without spurious message reloads', async () => {
    feed.available = true
    const wrapper = mountWorkspace()
    await flushPromises()
    api.listMessages.mockClear()

    emitNotice({
      type: 'companion.delta',
      seq: 0,
      kind: null,
      companionId: 'c-a',
      conversationId: 'conv-private',
      runId: 'run-live',
      delta: 'Checking codebase',
    })
    emitNotice({
      type: 'companion.tool_call',
      seq: 0,
      kind: null,
      companionId: 'c-a',
      conversationId: 'conv-private',
      runId: 'run-live',
      toolName: 'search_repo',
      toolStatus: 'running',
    })
    await flushPromises()

    const bubble = wrapper.get('[data-testid="companion-streaming-bubble"]')
    expect(bubble.text()).toContain('Checking codebase')
    expect(bubble.text()).toContain('search_repo')
    expect(api.listMessages).not.toHaveBeenCalled()
  })

  it('approves inline proposals and displays bot hop-depth cap notices', async () => {
    api.listMandates.mockResolvedValue(MANDATES)
    api.sendMessage.mockResolvedValue({
      duplicate: false,
      message: {
        id: 'm-new', conversation_id: 'conv-private', seq: 3, author_kind: 'user',
        author_name: 'You', body: 'Ping all', addressed_to: [], created_at: '2026-11-22T00:05:00Z',
      },
      runs: [],
      skipped_runs: [{ companion_id: 'c-b', reason: 'hop_depth_limit' }],
    })

    const wrapper = mountWorkspace()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="companion-proposal-card"]')).toHaveLength(1)
    await wrapper.get('[data-testid="proposal-approve-btn"]').trigger('click')
    await flushPromises()
    expect(api.decideMandate).toHaveBeenCalledWith('c-a', 'mandate-1', 1, 'approved')

    await wrapper.get('[data-testid="companion-thread-form"] textarea').setValue('Ping all')
    await wrapper.get('[data-testid="companion-thread-form"]').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[data-testid="companion-hop-notice"]').exists()).toBe(true)
  })

  it('supports Schedule, Plan, and Todo CRUD + manual trigger inside the Right Dock', async () => {
    const wrapper = mountWorkspace()
    await flushPromises()

    // Trigger schedule immediately & add a new schedule
    await wrapper.get('[data-testid="trigger-schedule-btn"]').trigger('click')
    await flushPromises()
    expect(api.triggerSchedule).toHaveBeenCalledWith('c-a', 'sched-1')

    await wrapper.get('[data-testid="add-schedule-toggle"]').trigger('click')
    const schedForm = wrapper.get('[data-testid="schedule-form"]')
    const schedInputs = schedForm.findAll('input')
    await schedInputs[0]!.setValue('Nightly Audit')
    await schedForm.get('textarea').setValue('Run audit')
    await schedForm.trigger('submit')
    await flushPromises()
    expect(api.createSchedule).toHaveBeenCalledWith('c-a', expect.objectContaining({ title: 'Nightly Audit' }))

    // Plans subtab
    await wrapper.get('[data-testid="spt-tab-plans"]').trigger('click')
    expect(wrapper.get('[data-testid="plan-row"]').text()).toContain('Q4 Polish')
    await wrapper.get('[data-testid="add-plan-toggle"]').trigger('click')
    const planForm = wrapper.get('[data-testid="plan-form"]')
    await planForm.get('input').setValue('Phase 9')
    await planForm.trigger('submit')
    await flushPromises()
    expect(api.createPlan).toHaveBeenCalledWith('c-a', expect.objectContaining({ title: 'Phase 9' }))

    // Todos subtab
    await wrapper.get('[data-testid="spt-tab-todos"]').trigger('click')
    expect(wrapper.get('[data-testid="todo-row"]').text()).toContain('Check PR')
    await wrapper.get('[data-testid="todo-row"] input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(api.patchTodo).toHaveBeenCalledWith('c-a', 'todo-1', 1, { status: 'done' })
  })

  it('rereads only the sources named by durable companion journal notices', async () => {
    vi.useFakeTimers()
    feed.available = true
    mountWorkspace()
    await vi.runOnlyPendingTimersAsync()
    await flushPromises()

    api.getSettings.mockClear()
    api.listCompanions.mockClear()
    api.listActivity.mockClear()
    api.listAllConversations.mockClear()
    api.listConnectionsOverview.mockClear()

    emitNotice({
      type: 'companion.settings', seq: 41, kind: 'settings_changed',
      companionId: null, conversationId: null, runId: null,
    })
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    expect(api.getSettings).toHaveBeenCalledTimes(1)
    expect(api.listCompanions).toHaveBeenCalledTimes(1)
    expect(api.listActivity).not.toHaveBeenCalled()
    expect(api.listAllConversations).not.toHaveBeenCalled()
    expect(api.listConnectionsOverview).not.toHaveBeenCalled()
  })
})
