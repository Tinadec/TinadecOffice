// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import TinaChatSection from './TinaChatSection.vue'
import zh from '@/locales/zh-CN'
import type { TinaChatParticipant, TinaChatWorkspacePolicy } from '@/api'

const mocks = vi.hoisted(() => ({
  participants: vi.fn(), policy: vi.fn(), conversations: vi.fn(), members: vi.fn(),
  register: vi.fn(), update: vi.fn(), createRoom: vi.fn(), changeMember: vi.fn(), setPolicy: vi.fn(),
  success: vi.fn(), error: vi.fn(),
}))
vi.mock('@/api', () => ({ api: {
  tinaChatParticipants: mocks.participants, tinaChatPolicy: mocks.policy, tinaChatConversations: mocks.conversations,
  tinaChatMembers: mocks.members, tinaChatRegisterParticipant: mocks.register, tinaChatUpdateParticipant: mocks.update,
  tinaChatCreateConversation: mocks.createRoom, tinaChatChangeMember: mocks.changeMember, tinaChatSetPolicy: mocks.setPolicy,
} }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ status: { success: mocks.success, error: mocks.error } }) }))

const person = (over: Partial<TinaChatParticipant> = {}): TinaChatParticipant => ({
  id: 'p1', workspace_id: 'w1', handle: 'me', display_name: '我', kind: 'human', job_title: null, description: null,
  agent_definition_id: null, receive_human_messages: true, can_interpret_intent: false, discoverable: true, status: 'active', revision: 1, ...over,
})
const policy = (over: Partial<TinaChatWorkspacePolicy> = {}): TinaChatWorkspacePolicy => ({
  workspace_id: 'w1', revision: 4, allow_cross_workspace_discovery: false, allow_cross_workspace_messaging: false, ...over,
})
const mounted: Array<{ unmount(): void }> = []

beforeEach(() => {
  vi.clearAllMocks()
  localStorage.clear()
  mocks.participants.mockResolvedValue([person()])
  mocks.policy.mockResolvedValue(policy())
  mocks.conversations.mockResolvedValue([])
  mocks.members.mockResolvedValue([])
  mocks.register.mockResolvedValue(person({ id: 'p2', handle: 'runner', display_name: '执行体', kind: 'agent' }))
  mocks.update.mockResolvedValue(person())
  mocks.createRoom.mockResolvedValue({ id: 'room1', workspace_id: 'w1', title: '交付群', kind: 'group', allow_cross_workspace: false, revision: 1, last_sequence: 0, accepted_intent_id: null, membership_status: 'active' })
  mocks.changeMember.mockResolvedValue({})
  mocks.setPolicy.mockResolvedValue(policy({ allow_cross_workspace_messaging: true }))
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()) })

function section() {
  const wrapper = mount(TinaChatSection, { global: { plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })] } })
  mounted.push(wrapper)
  return wrapper
}

describe('TinaChat management section', () => {
  it('registers an agent identity with the capability flags the server expects', async () => {
    const wrapper = section()
    await flushPromises()
    await wrapper.get('[data-testid="tinachat-handle"]').setValue('runner')
    await wrapper.get('[data-testid="tinachat-display"]').setValue('执行体')
    await wrapper.get('[data-testid="tinachat-register"]').trigger('click')
    await flushPromises()
    expect(mocks.register).toHaveBeenCalledWith(expect.objectContaining({
      handle: 'runner', display_name: '执行体', kind: 'agent', receive_human_messages: false, can_interpret_intent: true,
    }))
    // Registering claims the identity locally so the same device can act as it later.
    expect(JSON.parse(localStorage.getItem('tinadec.tinachat.identities') ?? '[]')).toContain('p2')
  })

  it('keeps a human identity able to read originals regardless of the checkbox', async () => {
    const wrapper = section()
    await flushPromises()
    await wrapper.get('[data-testid="tinachat-kind"]').setValue('human')
    await wrapper.get('[data-testid="tinachat-handle"]').setValue('me')
    await wrapper.get('[data-testid="tinachat-display"]').setValue('我')
    await wrapper.get('[data-testid="tinachat-register"]').trigger('click')
    await flushPromises()
    expect(mocks.register).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'human', receive_human_messages: true, can_interpret_intent: false }))
  })

  it('creates a room as the acting identity without inviting itself', async () => {
    localStorage.setItem('tinadec.tinachat.identities', JSON.stringify(['p1']))
    const wrapper = section()
    await flushPromises()
    await wrapper.get('[data-testid="tinachat-room-title"]').setValue('交付群')
    await wrapper.get('[data-testid="tinachat-invitee"]').setValue('p2')
    await wrapper.get('[data-testid="tinachat-create-room"]').trigger('click')
    await flushPromises()
    expect(mocks.createRoom).toHaveBeenCalledWith(expect.objectContaining({ actor_id: 'p1', title: '交付群', participant_ids: [] }))
  })

  it('sends the policy revision it read and preserves the untouched flag', async () => {
    const wrapper = section()
    await flushPromises()
    await wrapper.get('[data-testid="tinachat-policy"] [role="switch"]').trigger('click')
    await flushPromises()
    expect(mocks.setPolicy).toHaveBeenCalledWith({
      expected_revision: 4, allow_cross_workspace_discovery: true, allow_cross_workspace_messaging: false,
    })
  })

  it('names a stale revision instead of failing quietly', async () => {
    localStorage.setItem('tinadec.tinachat.identities', JSON.stringify(['p1']))
    const wrapper = section()
    await flushPromises()
    mocks.setPolicy.mockRejectedValueOnce(Object.assign(new Error('moved'), { status: 412 }))
    await wrapper.get('[data-testid="tinachat-policy"] [role="switch"]').trigger('click')
    await flushPromises()
    expect(mocks.error).toHaveBeenCalledWith(expect.objectContaining({ title: '配置已被其他改动更新，请刷新后重试' }))
  })

  it('shows nothing actionable until an identity is registered', async () => {
    const wrapper = section()
    await flushPromises()
    expect(wrapper.get('[data-testid="tinachat-actor"]').text()).toContain('先在下方注册一个身份')
    expect(wrapper.get('[data-testid="tinachat-create-room"]').attributes('disabled')).toBeDefined()
  })
})
