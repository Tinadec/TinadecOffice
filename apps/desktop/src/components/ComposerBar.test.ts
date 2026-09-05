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
  getMeetingModelPref: vi.fn(() => ''),
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
  getMeetingModelPref: dispatchMock.getMeetingModelPref,
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

describe('ComposerBar merged mode selector (one dropdown: follow-default + published versions)', () => {
  it('lists follow-default plus workspace versions instead of the bare enum', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([
      { id: 'mv-1', display_name: '自动 (Auto)', status: 'published', nodes: [], edges: [] },
    ])
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')].map(b => b.textContent!.trim())
    // i18n is mocked to echo the key; only the published suffix is literal.
    expect(items[0]).toBe('chat.followDefault')
    expect(items.some(t => t.includes('自动 (Auto) · 默认'))).toBe(true)
    expect(items).toHaveLength(2)
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('falls back to the bare agent-mode enum when no versions are published', async () => {
    const wrapper = mountComposer({ mode: 'plan' })
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')].map(b => b.textContent!.trim())
    expect(items).toHaveLength(6)
    expect(items.every(t => t.startsWith('mode.'))).toBe(true)
    expect(wrapper.emitted('update:modeVersionId')).toBeUndefined()
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  it('selecting a version emits update:modeVersionId; follow-default emits null', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([
      { id: 'mv-1', display_name: '自动 (Auto)', status: 'published', nodes: [], edges: [] },
    ])
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')]
    ;(items.find(b => b.textContent!.includes('自动 (Auto)')) as HTMLElement).click()
    await flushPromises()
    expect(wrapper.emitted('update:modeVersionId')![0]![0]).toBe('mv-1')
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    ;(document.querySelector('.mode-selector-item') as HTMLElement).click()
    await flushPromises()
    expect(wrapper.emitted('update:modeVersionId')![1]![0]).toBeNull()
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })
})
