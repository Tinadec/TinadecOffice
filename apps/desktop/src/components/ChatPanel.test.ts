// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import ChatPanel from './ChatPanel.vue'
import type { MessageDto } from '../api'
import type { AgentMode, PermissionLevel } from '@/types/mode'

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
  mode: 'auto' as AgentMode,
  permission: 'default' as PermissionLevel,
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
        MessageList: true,
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

  it('FLIP setup tolerates zero-size rects (no measurement available)', async () => {
    const wrapper = mountPanel([])
    await nextTick()
    // happy-dom rects are all-zero; the watcher must bail out silently.
    await wrapper.setProps({ messages: [msg('m1'), msg('m2')] })
    await nextTick()
    expect(wrapper.find('.composer-box').exists()).toBe(true)
    wrapper.unmount()
    document.body.innerHTML = ''
  })
})
