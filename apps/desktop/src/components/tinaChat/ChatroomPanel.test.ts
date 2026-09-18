// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import { defineComponent } from 'vue'
import ChatroomPanel from './ChatroomPanel.vue'
import zh from '@/locales/zh-CN'
import { useChatroomObserver } from '@/tinaChat/useChatroomObserver'
import type { TinaChatObservedConversation, TinaChatObservedDetail, TinaChatObservedMessage, TinaChatObservedMessagePage } from '@/api'

const mocks = vi.hoisted(() => ({ access: vi.fn(), list: vi.fn(), detail: vi.fn(), messages: vi.fn(), notify: vi.fn(), dismiss: vi.fn() }))
vi.mock('@/api', () => ({ api: {
  tinaChatObserverAccess: mocks.access, tinaChatObserverConversations: mocks.list,
  tinaChatObserverConversation: mocks.detail, tinaChatObserverMessages: mocks.messages,
} }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ status: { error: mocks.notify }, dismissByKey: mocks.dismiss }) }))

const participant = (id: string, name: string) => ({ id, display_name: name, handle: id, kind: 'agent', workspace_id: 'workspace',
  job_title: '工程师', description: null, agent_definition_id: null, receive_human_messages: false, can_interpret_intent: false,
  discoverable: true, status: 'active', revision: 1 })
const room = (id: string): TinaChatObservedConversation => ({ id, workspace_id: 'workspace', workspace_name: '开发工作区',
  title: id === 'group' ? '工程协作群' : '青禾与见微', kind: id === 'group' ? 'group' : 'direct',
  last_sequence: 2, revision: 3, member_count: 2, participant_names: ['青禾', '见微'], last_message_preview: '完成核对', last_message_at: '2026-09-18T12:00:00Z' })
const detail = (id: string): TinaChatObservedDetail => ({ conversation: room(id), members: [
  { participant: participant('sender', '青禾'), role: 'owner', status: 'active', joined_after_sequence: 0 },
  { participant: participant('receiver', '见微'), role: 'member', status: 'active', joined_after_sequence: 0 },
] })
const message = (id: string, sequence: number, content = '仅向见微发送的保密资料'): TinaChatObservedMessage => ({
  message: { id, conversation_id: 'group', sender_id: 'sender', sender_kind: 'agent', sequence, kind: 'message', content,
    sensitivity: 'confidential', allow_derived_sharing: false, reply_to_message_id: null, source_message_ids: [], created_at: '2026-09-18T12:00:00Z' },
  sender: participant('sender', '青禾'), audience: [{ participant: participant('receiver', '见微'), can_read_original: true, can_receive_derived: false, acknowledged: false }],
  intent_id: null, intent_status: null,
})
const page = (items: TinaChatObservedMessage[], hasMore = false): TinaChatObservedMessagePage => ({ items, has_more: hasMore,
  oldest_sequence: items[0]?.message.sequence ?? 0, newest_sequence: items.at(-1)?.message.sequence ?? 0 })
const mounted: Array<{ unmount(): void }> = []

beforeEach(() => {
  vi.clearAllMocks()
  mocks.access.mockResolvedValue({ level: 'tenant', workspaces: [{ id: 'workspace', name: '开发工作区' }] })
  mocks.list.mockImplementation(async (params: { kind?: string }) => ({ items: params.kind ? [room(params.kind)] : [room('group'), room('direct')], total: params.kind ? 1 : 2, next_offset: 2, has_more: false }))
  mocks.detail.mockImplementation(async (id: string) => detail(id))
  mocks.messages.mockResolvedValue(page([message('one', 1)]))
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })

function panel() {
  const wrapper = mount(ChatroomPanel, { global: { plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })] } })
  mounted.push(wrapper)
  return wrapper
}
function model() {
  let observer!: ReturnType<typeof useChatroomObserver>
  mounted.push(mount(defineComponent({ setup() { observer = useChatroomObserver(); return {} }, template: '<div />' })))
  return observer
}

describe('Chatroom administrator observation', () => {
  it('shows private originals with named recipients and has no composer', async () => {
    const wrapper = panel()
    await flushPromises()
    expect(wrapper.text()).toContain('工程协作群')
    expect(wrapper.text()).toContain('仅向见微发送的保密资料')
    expect(wrapper.get('.message-direction').text()).toContain('见微')
    expect(wrapper.find('textarea').exists()).toBe(false)
    expect(wrapper.text()).toContain('尚未确认接收')
    await wrapper.get('[data-testid="chatroom-members"]').trigger('click')
    expect(wrapper.get('[data-testid="chatroom-member-list"]').text()).toContain('青禾')
  })

  it('filters direct conversations through the server and renders message text without HTML execution', async () => {
    vi.useFakeTimers()
    mocks.messages.mockResolvedValue(page([message('html', 1, '<img src=x onerror="alert(1)">')]))
    const wrapper = panel()
    await flushPromises()
    expect(wrapper.get('.observed-content').text()).toContain('<img')
    expect(wrapper.find('.observed-content img').exists()).toBe(false)
    await wrapper.get('[data-testid="chatroom-filter-direct"]').trigger('click')
    await vi.advanceTimersByTimeAsync(260)
    await flushPromises()
    expect(mocks.list).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'direct' }), expect.any(AbortSignal))
    expect(wrapper.findAll('.room-row')).toHaveLength(1)
    expect(wrapper.text()).toContain('青禾与见微')
  })

  it('clears already-rendered confidential data when administrator permission is revoked', async () => {
    const wrapper = panel()
    await flushPromises()
    mocks.access.mockRejectedValueOnce(Object.assign(new Error('Observer permission revoked'), { status: 403 }))
    await wrapper.get('[data-testid="chatroom-refresh"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="chatroom-forbidden"]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('仅向见微发送的保密资料')
    expect(wrapper.text()).not.toContain('工程协作群')
  })

  it('ignores a late response from the previously selected conversation', async () => {
    const observer = model()
    let resolveOld!: (value: TinaChatObservedDetail) => void
    mocks.detail.mockImplementationOnce(() => new Promise<TinaChatObservedDetail>(resolve => { resolveOld = resolve }))
    const old = observer.selectConversation('group')
    await observer.selectConversation('direct')
    resolveOld(detail('group'))
    await old
    expect(observer.selectedId.value).toBe('direct')
    expect(observer.detail.value?.conversation.id).toBe('direct')
  })

  it('loads older history without duplicates and follows a large burst without gaps', async () => {
    const observer = model()
    mocks.messages.mockResolvedValueOnce(page([message('two', 2), message('three', 3)], true))
    await observer.selectConversation('group')
    mocks.messages.mockResolvedValueOnce(page([message('one', 1)]))
    await observer.loadOlder()
    expect(observer.messages.value.map(x => x.message.sequence)).toEqual([1, 2, 3])
    mocks.messages.mockResolvedValueOnce(page([message('four', 4)], true))
    mocks.messages.mockResolvedValueOnce(page([message('four', 4), message('six', 6)]))
    await observer.refreshMessages()
    expect(observer.messages.value.map(x => x.message.sequence)).toEqual([1, 2, 3, 4])
    mocks.messages.mockResolvedValueOnce(page([message('five', 5), message('six', 6)]))
    mocks.messages.mockResolvedValueOnce(page([message('five', 5), message('six', 6)]))
    await observer.refreshMessages()
    expect(observer.messages.value.map(x => x.message.sequence)).toEqual([1, 2, 3, 4, 5, 6])
  })
})
