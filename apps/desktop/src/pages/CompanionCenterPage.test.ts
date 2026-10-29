// @vitest-environment happy-dom
import { defineComponent, h } from 'vue'
import { createI18n } from 'vue-i18n'
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import zh from '@/locales/zh-CN'
import { ApiError } from '@/lib/apiError'
import { resetCompanionStore, setFeedFilter } from '@/lib/companionStore'
import CompanionCenterPage from './CompanionCenterPage.vue'
import type {
  CompanionActivity,
  CompanionConnectionOverview,
  CompanionConversation,
  CompanionMessage,
  CompanionSummary,
} from '@/lib/companionApi'

const lifecycle = vi.hoisted(() => ({
  ready: Promise.resolve() as Promise<void>,
  showPage: vi.fn(),
}))

const api = vi.hoisted(() => ({
  listCompanions: vi.fn(async (): Promise<unknown[]> => []),
  listActivity: vi.fn(async (): Promise<unknown[]> => []),
  listAllConversations: vi.fn(async (): Promise<unknown[]> => []),
  listConnectionsOverview: vi.fn(async (): Promise<unknown[]> => []),
  listMessages: vi.fn(async (): Promise<unknown[]> => []),
  pauseConversation: vi.fn(async () => ({})),
  getCompanion: vi.fn(async () => ({})),
  patchCompanion: vi.fn(async () => ({})),
}))

vi.mock('@/lib/uiEngine', () => ({ useUiePage: () => ({ ready: lifecycle.ready, showPage: lifecycle.showPage }) }))
vi.mock('@/components/AppHeader.vue', () => ({ default: defineComponent({ render: () => h('header') }) }))
// The sidebar is the shared shell component; this suite is about the centre's four cards.
vi.mock('@/components/AppSidebar.vue', () => ({
  default: defineComponent({
    props: { companionDomain: { type: Boolean, default: false } },
    emits: ['companion-nav'],
    render() {
      return h('nav', { class: 'sidebar-probe', 'data-companion-domain': String(this.companionDomain) }, [
        h('button', { class: 'nav-attention', onClick: () => this.$emit('companion-nav', 'attention') }),
      ])
    },
  }),
}))
vi.mock('@/lib/companionApi', () => ({ companionApi: api }))

// The canvas is replaced by the four center cards directly: this suite is about what
// the cards read and say, not about UIE geometry (covered by the UIE engine tests).
vi.mock('@tinadec/ui', async () => {
  const { defineComponent: define, h: hyperscript } = await import('vue')
  const List = (await import('../../../TinadecUI/src/components/cards/companion/CompanionListCard.vue')).default
  const Feed = (await import('../../../TinadecUI/src/components/cards/companion/CompanionFeedCard.vue')).default
  const Groups = (await import('../../../TinadecUI/src/components/cards/companion/CompanionGroupsCard.vue')).default
  const Connections = (await import('../../../TinadecUI/src/components/cards/companion/CompanionConnectionsCard.vue')).default
  return {
    UieCanvas: define({
      render: () => hyperscript('div', { class: 'canvas-probe' }, [
        hyperscript(List), hyperscript(Feed), hyperscript(Groups), hyperscript(Connections),
      ]),
    }),
  }
})

enableAutoUnmount(afterEach)

const COMPANION_A: CompanionSummary = {
  id: 'c-a', name: 'Alpha', avatar: null, tagline: 'Review partner', model_purpose: 'chat',
  model: null, model_provider_id: null,
  current_activity: 'thinking', autonomy_enabled: true, autonomy_paused: false,
  memory_count: 3, skill_count: 2, connection_count: 1, revision: 1,
  status: 'active', created_at: '2026-10-01T00:00:00Z', updated_at: '2026-10-01T00:00:00Z',
  last_activity_at: '2026-10-27T10:00:00Z',
}
const COMPANION_B: CompanionSummary = {
  ...COMPANION_A, id: 'c-b', name: 'Beta', current_activity: 'paused',
  autonomy_enabled: false, autonomy_paused: false, last_activity_at: null,
}

const ACTIVITY: CompanionActivity = {
  id: 'a-1', kind: 'run_completed', companion_id: 'c-a', companion_name: 'Alpha',
  summary: 'Answered a question', created_at: '2026-10-27T10:05:00Z',
  conversation_id: 'conv-group', conversation_title: 'Alpha and Beta',
}

/** A user message: Core records it with no companion id, which used to render a blank author. */
const USER_MESSAGE: CompanionActivity = {
  id: 'a-2', kind: 'message_sent', companion_id: null, companion_name: null,
  summary: '用户发言：我先把范围缩小', payload: { preview: '我先把范围缩小', addressed_to: ['c-a'] },
  conversation_id: 'conv-group', conversation_title: 'Alpha and Beta', created_at: '2026-10-27T10:04:00Z',
}

/** A failure is the one kind the centre treats as "needs you". */
const FAILED: CompanionActivity = {
  id: 'a-3', kind: 'run_failed', companion_id: 'c-a', companion_name: 'Alpha',
  summary: '伙伴 Alpha 运行失败：core_unreachable',
  payload: { error_code: 'core_unreachable' }, created_at: '2026-10-27T10:03:00Z',
}

const GROUP: CompanionConversation = {
  id: 'conv-group', kind: 'group', title: 'Alpha and Beta', origin: 'user', can_open: true,
  autonomy_paused: true, message_count: 2, turn_budget: 6, last_message_at: '2026-10-27T10:06:00Z',
  last_message_preview: 'Latest group line',
  members: [
    { display_name: 'You', member_kind: 'user', status: 'active', joined_at: '2026-10-01T00:00:00Z' },
    { display_name: 'Alpha', member_kind: 'companion', companion_id: 'c-a', status: 'active', joined_at: '2026-10-01T00:00:00Z' },
  ],
}

const AUTONOMOUS_GROUP: CompanionConversation = {
  ...GROUP, id: 'conv-auto', title: 'Aurora 讨论', origin: 'companion', autonomy_paused: false,
  last_message_at: '2026-10-27T09:30:00Z',
}

const MESSAGES: CompanionMessage[] = [
  {
    id: 'm-1', conversation_id: 'conv-group', seq: 1, author_kind: 'user', author_name: 'You',
    body: 'Hello group', addressed_to: [], created_at: '2026-10-27T10:05:30Z',
  },
  {
    id: 'm-2', conversation_id: 'conv-group', seq: 2, author_kind: 'companion', author_name: 'Alpha',
    author_companion_id: 'c-a', body: 'Hello you', addressed_to: [], created_at: '2026-10-27T10:06:00Z',
  },
]

const CONNECTIONS: CompanionConnectionOverview[] = [
  {
    id: 'conn-1', companion_id: 'c-a', companion_name: 'Alpha', kind: 'mcp', name: 'Local MCP',
    authorization: 'authorized', runtime_status: 'unsupported', enabled: true,
    last_checked_at: null, revision: 1,
  },
  {
    id: 'conn-2', companion_id: 'c-a', companion_name: 'Alpha', kind: 'server', name: 'Build server',
    authorization: 'configured', runtime_status: 'unknown', enabled: false,
    last_checked_at: '2026-10-27T09:00:00Z', revision: 1,
  },
]

function create() {
  const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
  return mount(CompanionCenterPage, { global: { plugins: [i18n] } })
}

function emptyAll(): void {
  api.listCompanions.mockResolvedValue([])
  api.listActivity.mockResolvedValue([])
  api.listAllConversations.mockResolvedValue([])
  api.listMessages.mockResolvedValue([])
  api.listConnectionsOverview.mockResolvedValue([])
}

beforeEach(() => {
  resetCompanionStore()
  lifecycle.showPage.mockClear()
  api.listCompanions.mockResolvedValue([COMPANION_A, COMPANION_B])
  api.listActivity.mockResolvedValue([ACTIVITY, USER_MESSAGE, FAILED])
  api.listAllConversations.mockResolvedValue([GROUP, AUTONOMOUS_GROUP])
  api.listMessages.mockResolvedValue(MESSAGES)
  api.listConnectionsOverview.mockResolvedValue(CONNECTIONS)
})

afterEach(() => {
  vi.clearAllMocks()
  resetCompanionStore()
})

describe('companion center route', () => {
  it('switches the shared layout to companionCenter and mounts the four cards', async () => {
    const wrapper = create()
    await flushPromises()

    expect(lifecycle.showPage).toHaveBeenCalledWith('companionCenter')
    expect(wrapper.emitted('ready')).toHaveLength(1)
    expect(wrapper.get('.canvas-probe').findAll('section.companion-card')).toHaveLength(4)
  })

  it('mounts the shared sidebar in companion domain, without work projects or sessions', async () => {
    const wrapper = create()
    await flushPromises()

    const sidebar = wrapper.get('.sidebar-probe')
    expect(sidebar.attributes('data-companion-domain')).toBe('true')
  })

  it('reads the whole domain once for four cards instead of once per card', async () => {
    create()
    await flushPromises()

    // The regression this pins: the four panels used to poll for themselves, so one snapshot was
    // actually three `listCompanions` calls plus one per companion for its conversations.
    expect(api.listCompanions).toHaveBeenCalledTimes(1)
    expect(api.listAllConversations).toHaveBeenCalledTimes(1)
    expect(api.listActivity).toHaveBeenCalledTimes(1)
    expect(api.listConnectionsOverview).toHaveBeenCalledTimes(1)
  })

  it('shows the companion list with Core three-state status and a per-companion attention badge', async () => {
    const wrapper = create()
    await flushPromises()

    const rows = wrapper.findAll('.companion-row')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('Alpha')
    expect(rows[0]!.text()).toContain(zh.companion.center.statusThinking)
    expect(rows[1]!.text()).toContain(zh.companion.center.statusPaused)
    // Alpha has one failed run; Beta has none.
    expect(rows[0]!.find('.companion-row-attention').text()).toBe('1')
    expect(rows[1]!.find('.companion-row-attention').exists()).toBe(false)

    await rows[0]!.trigger('click')
    await flushPromises()
    const detail = wrapper.get('.companion-detail')
    expect(detail.text()).toContain(zh.companion.center.autonomyOn)
    expect(detail.text()).toContain('3 条记忆')
    expect(detail.text()).toContain('2 项技能')
    expect(detail.text()).toContain('1 个连接')
    expect(detail.text()).toContain(zh.companion.center.openWindowHint)
  })

  it('renders feed sentences from the event kind instead of Core Chinese summaries', async () => {
    const wrapper = create()
    await flushPromises()

    const rows = wrapper.findAll('.companion-feed-row')
    // Newest first: run_completed, then the user message, then the failure.
    expect(rows[0]!.text()).toContain('Alpha 完成了回复')
    // The user's own message used to render with an empty author; it is "我" now.
    expect(rows[1]!.text()).toContain('我：我先把范围缩小')
    expect(rows[2]!.text()).toContain('Alpha 的回复失败（core_unreachable）')
    // The server's Chinese summary must not leak through when the kind is known.
    expect(wrapper.text()).not.toContain('伙伴 Alpha 运行失败')
  })

  it('narrows the feed to what needs the user when the filter says so', async () => {
    const wrapper = create()
    await flushPromises()

    setFeedFilter('attention')
    await flushPromises()
    const rows = wrapper.findAll('.companion-feed-row')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.text()).toContain('core_unreachable')
  })

  it('splits groups by who opened them and reads recent messages without a composer', async () => {
    const wrapper = create()
    await flushPromises()

    const labels = wrapper.findAll('.companion-group-label').map((node) => node.text())
    expect(labels).toEqual([zh.companion.center.groupsMine, zh.companion.center.groupsByCompanions])

    const group = wrapper.findAll('.companion-group')[0]!
    expect(group.text()).toContain('Alpha and Beta')
    expect(group.text()).toContain('You · Alpha')
    expect(group.text()).toContain(zh.companion.center.groupPaused)
    expect(group.text()).toContain('Latest group line')

    await group.get('.companion-group-main').trigger('click')
    await flushPromises()
    expect(api.listMessages).toHaveBeenCalledWith('conv-group', undefined, 20)
    const panel = wrapper.get('.companion-messages')
    expect(panel.text()).toContain('Hello group')
    expect(panel.text()).toContain('Hello you')
    expect(panel.text()).toContain(zh.companion.center.groupReadOnly)
    expect(panel.find('textarea, input').exists()).toBe(false)
  })

  it('pauses a group through the conversation endpoint and re-reads the snapshot', async () => {
    const wrapper = create()
    await flushPromises()

    await wrapper.findAll('.companion-group')[0]!.get('.companion-group-pause').trigger('click')
    await flushPromises()

    // The open group was paused, so the button asks Core for the opposite state.
    expect(api.pauseConversation).toHaveBeenCalledWith('conv-group', false)
    expect(api.listAllConversations).toHaveBeenCalledTimes(2)
  })

  it('reports connection authorization and runtime honestly, never promoting configured to online', async () => {
    const wrapper = create()
    await flushPromises()

    const card = wrapper.findAll('section.companion-card')[3]!
    expect(card.text()).toContain('Local MCP')
    expect(card.text()).toContain(zh.companion.center.connectionKindMcp)
    expect(card.text()).toContain(zh.companion.center.connectionAuthAuthorized)
    expect(card.text()).toContain(zh.companion.center.connectionRuntimeUnsupported)
    expect(card.text()).toContain(zh.companion.center.connectionAuthConfigured)
    expect(card.text()).toContain(zh.companion.center.connectionRuntimeUnknown)
    expect(card.text()).toContain(zh.companion.center.connectionNeverChecked)
    // A disabled binding says so instead of looking identical to an active one.
    expect(card.text()).toContain(zh.companion.center.connectionDisabled)
    expect(card.text()).not.toContain(zh.companion.center.connectionRuntimeOnline)
  })

  it('scopes groups and connections to the selected companion', async () => {
    const wrapper = create()
    await flushPromises()

    await wrapper.findAll('.companion-row')[1]!.trigger('click')
    await flushPromises()

    // Beta is not a member of the group and owns no connections.
    const cards = wrapper.findAll('section.companion-card')
    expect(cards[2]!.text()).toContain(zh.companion.center.groupsEmpty)
    expect(cards[3]!.text()).toContain(zh.companion.center.connectionEmpty)
  })

  it('renders every card empty state instead of a blank or a fake row', async () => {
    emptyAll()
    const wrapper = create()
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain(zh.companion.center.listEmpty)
    expect(text).toContain(zh.companion.center.feedEmpty)
    expect(text).toContain(zh.companion.center.groupsEmpty)
    expect(text).toContain(zh.companion.center.connectionEmpty)
  })

  it('shows the desktop-host failure code instead of a friendlier claim', async () => {
    api.listCompanions.mockRejectedValue(
      new ApiError('preview', 403, { code: 'desktop_host_required' }),
    )
    const wrapper = create()
    await flushPromises()

    expect(wrapper.text()).toContain(zh.companion.center.errorHostRequired)
  })
})
