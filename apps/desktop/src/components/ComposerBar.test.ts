// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ComposerBar from './ComposerBar.vue'
import type { AgentMode } from '@/types/mode'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn() }),
}))

const homeMock = vi.hoisted(() => ({
  queuedMessages: [] as Array<{ id: string; content: string }>,
  activeRuns: [] as Array<{ id: string; status: string }>,
  steerQueued: vi.fn(),
  promoteQueued: vi.fn(),
  editQueued: vi.fn(),
  dismissQueued: vi.fn(),
}))

const dispatchMock = vi.hoisted(() => ({
  getDispatchPref: vi.fn(() => 'queued' as 'queued' | 'parallel' | 'ask'),
}))

const apiMock = vi.hoisted(() => ({
  api: {
    listAgentModeTopologies: vi.fn(async () => [] as import('@/api').AgentModeTopologyDto[]),
  },
}))

vi.mock('@/controllers/HomeController', () => ({
  homeController: homeMock,
}))

vi.mock('@/lib/dispatchPref', () => ({
  getDispatchPref: dispatchMock.getDispatchPref,
}))

vi.mock('@/api', () => apiMock)

function mountComposer(props: Partial<{ busy: boolean; modelValue: string; modeVersionId: string | null; mode: AgentMode }> = {}) {
  return mount(ComposerBar, {
    props: {
      busy: false,
      modelValue: '',
      permission: 'default',
      ...props,
    },
  })
}

describe('ComposerBar Codex shell contract', () => {
  it('idle composer reports data-composer-active=false', async () => {
    const wrapper = mountComposer()
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('false')
    wrapper.unmount()
  })

  it('a non-empty draft flips the shell to active', async () => {
    const wrapper = mountComposer({ modelValue: 'hello' })
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('true')
    wrapper.unmount()
  })

  it('busy state shows the spinner and keeps the shell active', async () => {
    const wrapper = mountComposer({ busy: true, modelValue: 'working' })
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('true')
    expect(wrapper.find('.composer-send-spinner').exists()).toBe(true)
    wrapper.unmount()
    document.body.innerHTML = ''
  })
})

describe('ComposerBar hero variant (start page)', () => {
  it('renders mode selector and project trigger, and no queued cards', async () => {
    const wrapper = mount(ComposerBar, {
      props: {
        hero: true,
        busy: false,
        modelValue: '',
        mode: 'plan',
        permission: 'default',
        projects: [{ id: 'p1', name: 'Proj One' } as never],
        selectedProjectId: 'p1',
      },
    })
    await flushPromises()
    expect(wrapper.find('.mode-selector-trigger').exists()).toBe(true)
    expect(wrapper.find('.project-dropdown-trigger').exists()).toBe(true)
    expect(wrapper.find('.project-dropdown-label').text()).toContain('Proj One')
    expect(wrapper.find('.composer-queued').exists()).toBe(false)
    expect(wrapper.find('.composer--hero').exists()).toBe(true)
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  it('enter key emits the welcome payload with content instead of dispatch submit', async () => {
    const wrapper = mount(ComposerBar, {
      props: {
        hero: true,
        busy: false,
        modelValue: 'hello world',
        mode: 'plan',
        permission: 'default',
      },
    })
    await flushPromises()
    await wrapper.find('.welcome-dialog-input').trigger('keydown', { key: 'Enter' })
    const emitted = wrapper.emitted('welcome-submit')
    expect(emitted).toHaveLength(1)
    expect(emitted![0]![0]).toEqual({ content: 'hello world', agent_mode: 'plan', permission_mode: 'default', mode_version_id: null })
    expect(wrapper.emitted('submit')).toBeUndefined()
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  it('docked ask dispatch teleports its menu to body (never clipped by the dialog)', async () => {
    dispatchMock.getDispatchPref.mockReturnValue('ask')
    const wrapper = mountComposer({ modelValue: 'hello' })
    await flushPromises()
    await wrapper.find('.welcome-dialog-send').trigger('click')
    await flushPromises()
    expect(document.querySelector('.ask-menu')).not.toBeNull()
    expect(wrapper.find('.ask-menu').exists()).toBe(false)
    wrapper.unmount()
    document.body.innerHTML = ''
    dispatchMock.getDispatchPref.mockReturnValue('queued')
  })
})

/**
 * pack 安装的 conversation.* 模式带 application_mode，工作区自建拓扑不带。
 * 前者由「对话模式」组承载（显示名取自这里），后者才进「工作区拓扑」组。
 */
const CONVERSATION_MODE = {
  id: 'am-conv-auto',
  display_name: '自动 (Auto)',
  status: 'published',
  application_mode: 'auto',
  latest_published_mode_version_id: 'mv-conv-auto',
  nodes: [],
  edges: [],
}
const WORKSPACE_TOPOLOGY = {
  id: 'am-custom',
  display_name: '自建评审流',
  status: 'published',
  application_mode: null,
  latest_published_mode_version_id: 'mv-custom-1',
  nodes: [],
  edges: [],
}

describe('ComposerBar merged mode selector (one dropdown: follow-default + published versions)', () => {
  it('shows a conversation mode exactly once and keeps only workspace topologies in the topology group', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([
      CONVERSATION_MODE,
      WORKSPACE_TOPOLOGY,
      { id: 'am-draft', display_name: '草稿模式', status: 'draft', application_mode: null, nodes: [], edges: [] },
    ] as never)
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')].map(b => b.textContent!.trim())
    // follow-default + 6 个对话模式 + 1 个自建拓扑（draft 与 conversation.* 都不进拓扑组）。
    expect(items[0]).toBe('chat.followDefault')
    // 去重判据：`自动 (Auto)` 只出现一次 —— 它是对话模式组里 auto 的显示名，不再另开一项。
    expect(items.filter(t => t.includes('自动 (Auto)'))).toHaveLength(1)
    expect(items.some(t => t.includes('自建评审流'))).toBe(true)
    expect(items.some(t => t.includes('草稿模式'))).toBe(false)
    expect(items).toHaveLength(8)
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('hides the topology group entirely when every published mode is a conversation mode', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([CONVERSATION_MODE] as never)
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const groups = [...document.querySelectorAll('.mode-selector-group')].map(b => b.textContent!.trim())
    expect(groups).toEqual(['chat.conversationModeGroup'])
    expect([...document.querySelectorAll('.mode-selector-item')]).toHaveLength(7)
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('falls back to the i18n enum labels when the gateway is offline', async () => {
    apiMock.api.listAgentModeTopologies.mockRejectedValue(new Error('offline'))
    const wrapper = mountComposer({ mode: 'plan' })
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')].map(b => b.textContent!.trim())
    expect(items[0]).toBe('chat.followDefault')
    expect(items.filter(t => t.startsWith('mode.'))).toHaveLength(6)
    expect(items).toHaveLength(7)
    expect(wrapper.emitted('update:modeVersionId')).toBeUndefined()
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('selecting a workspace topology emits its mode_version_id; picking a conversation mode clears it', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([WORKSPACE_TOPOLOGY] as never)
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')]
    ;(items.find(b => b.textContent!.includes('自建评审流')) as HTMLElement).click()
    await flushPromises()
    expect(wrapper.emitted('update:modeVersionId')![0]![0]).toBe('mv-custom-1')
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    ;(document.querySelector('.mode-selector-item') as HTMLElement).click()
    await flushPromises()
    expect(wrapper.emitted('update:modeVersionId')![1]![0]).toBeNull()
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('a stale modeVersionId activates nothing in the topology group (self-healing fallback)', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([WORKSPACE_TOPOLOGY] as never)
    const wrapper = mountComposer({ modeVersionId: 'mv-gone' })
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const active = [...document.querySelectorAll('.mode-selector-item.active')].map(b => b.textContent!.trim())
    // 自愈态：跟随默认 与 当前枚举（auto）同属一个有效状态，二者同时高亮；
    // 失效的 topology 项不得出现在激活集合里。
    expect(active).toContain('chat.followDefault')
    expect(active).toContain('mode.auto')
    expect(active.some(t => t.includes('自建评审流'))).toBe(false)
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })
})
