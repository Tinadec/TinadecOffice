// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick, ref, shallowRef } from 'vue'
import { createFeedState } from '@/lib/conversation/reducer'
import ChatPanel from './ChatPanel.vue'
import type { MessageDto } from '../api'
import type { PermissionLevel } from '@/types/mode'

vi.mock('vue-i18n', async (importOriginal) => {
  const actual = await importOriginal<typeof import('vue-i18n')>()
  return {
    ...actual,
    useI18n: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
  }
})

const homeMock = vi.hoisted(() => ({
  queuedMessages: [] as Array<{ id: string; content: string }>,
  activeRuns: [] as Array<{ id: string; status: string }>,
  steerQueued: vi.fn(),
  promoteQueued: vi.fn(),
  editQueued: vi.fn(),
  dismissQueued: vi.fn(),
}))

vi.mock('@/controllers/HomeController', () => ({
  homeController: homeMock,
}))

// ResizeObserver is not implemented in every happy-dom build; the chat
// responsive-mode composable must not explode without it.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
;(globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver ??= ResizeObserverStub

const baseProps = {
  sessions: [],
  projects: [],
  currentSession: null,
  currentProject: null,
  selectedProjectId: null,
  modelName: 'test-model',
  orchestration: null,
  busy: false,
  draft: '',
  permission: 'default' as PermissionLevel,
  conversation: { feed: { state: shallowRef(createFeedState()), revision: ref(0), hydrated: ref(true) }, runs: [] },
}

function msg(id: string): MessageDto {
  return { id, session_id: 's1', role: 'user', content: `msg ${id}`, created_at: '' } as MessageDto
}

function probe(box: { element: Element }): Record<string, string> {
  return (box.element as HTMLElement).dataset as Record<string, string>
}

function mountPanel(messages: MessageDto[] = []) {
  return mount(ChatPanel, {
    props: { ...baseProps, messages },
    global: {
      stubs: {
        ChatHeader: true,
        ConversationView: true,
      },
    },
  })
}

describe('ChatPanel persistent composer', () => {
  it('keeps the same composer element when the first message docks the panel', async () => {
    const wrapper = mountPanel([])
    await nextTick()
    expect(wrapper.find('.composer-box').exists()).toBe(true)
    expect(wrapper.find('.conversation').classes()).toContain('composer-hero')

    await wrapper.setProps({ currentSession: { id: 'existing-empty' } as never })
    expect(wrapper.find('.conversation').classes()).toContain('composer-hero')

    // Mark the live element, then send the first message.
    probe(wrapper.find('.composer-box')).probe = 'keep-me'
    await wrapper.setProps({ messages: [msg('m1')] })
    await nextTick()

    const box = wrapper.find('.composer-box')
    expect(box.exists()).toBe(true)
    expect(probe(box).probe).toBe('keep-me')
    expect(wrapper.find('.conversation').classes()).not.toContain('composer-hero')
    wrapper.unmount()
  })

  it('keeps the same composer element when returning to the start page', async () => {
    const wrapper = mountPanel([msg('m1')])
    await nextTick()
    expect(wrapper.find('.conversation').classes()).not.toContain('composer-hero')
    probe(wrapper.find('.composer-box')).probe = 'still-me'

    await wrapper.setProps({ messages: [] })
    await nextTick()

    expect(probe(wrapper.find('.composer-box')).probe).toBe('still-me')
    expect(wrapper.find('.conversation').classes()).toContain('composer-hero')
    wrapper.unmount()
  })

  it('stays docked while a switched-to session is still loading its transcript', async () => {
    const wrapper = mountPanel([msg('m1')])
    await nextTick()
    // Switching sessions empties the list before the new transcript arrives.
    await wrapper.setProps({ messages: [], loading: true })
    expect(wrapper.find('.conversation').classes()).not.toContain('composer-hero')
    // An existing session that turns out to be empty rises to the start page once loaded.
    await wrapper.setProps({ loading: false })
    expect(wrapper.find('.conversation').classes()).toContain('composer-hero')
    wrapper.unmount()
  })
})

describe('ChatPanel send payload', () => {
  /**
   * 回归护栏：此前这里把 composer 的 `meeting_model_override`（对象）读成
   * `payload.meeting_model`（字符串）再以 `meeting_model` 键 emit，`as never`
   * 压掉了类型错误，HomeController 于是永远拿到 undefined。
   */
  it('passes the composer meeting_model_override object through verbatim', async () => {
    const override = { provider_instance_id: 'prov-1', model: 'gpt-x' }
    const wrapper = mount(ChatPanel, {
      props: {
        ...baseProps,
        messages: [msg('m1')],
        currentSession: { id: 's1', meeting_model_override: override } as never,
      },
      global: { stubs: { ChatHeader: true, ConversationView: true } },
    })
    await nextTick()

    wrapper.findComponent({ name: 'ComposerBar' }).vm.$emit('submit', {
      dispatch_mode: 'queued',
      target_run_id: null,
      mode_version_id: null,
      meeting_model_override: override,
    })
    await nextTick()

    expect(wrapper.emitted('send')![0]![0]).toEqual({
      dispatch_mode: 'queued',
      target_run_id: null,
      mode_version_id: null,
      meeting_model_override: override,
    })
    wrapper.unmount()
    document.body.innerHTML = ''
  })
})
