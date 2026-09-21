// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import MessageList from './MessageList.vue'
import MessageItem from './MessageItem.vue'
import ToolCallCard from './chat/ToolCallCard.vue'
import { api, type MessageDto } from '../api'
import type { MessageAttachmentSummaryDto } from '@/generated/client'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

vi.mock('vue-i18n', async (importOriginal) => {
  const actual = await importOriginal<typeof import('vue-i18n')>()
  return {
    ...actual,
    useI18n: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
  }
})

function message(id: string, role: MessageDto['role']): MessageDto {
  return { id, session_id: 's1', role, content: `${role} ${id}`, created_at: '' } as MessageDto
}

function approvalCall(over: Partial<ToolCall> = {}): ToolCall {
  return {
    id: 'call-1',
    toolId: 'write_file',
    toolName: 'write_file',
    status: 'waiting_approval',
    startedAt: null,
    completedAt: null,
    durationMs: null,
    argsSummary: 'notes.txt',
    resultSummary: null,
    requiresApproval: true,
    approvalId: 'approval-1',
    evidence: [],
    seq: 1,
    risk: 'elevated',
    ...over,
  }
}

function step(over: Partial<ThinkingStep> = {}): ThinkingStep {
  return {
    id: 'step-1',
    type: 'run_started',
    title: 'Run started',
    description: '',
    timestamp: '',
    durationMs: null,
    ...over,
  } as ThinkingStep
}

const scrollStub = { template: '<div><slot /></div>' }

function mountList(props: Record<string, unknown>) {
  return mount(MessageList, {
    props: props as never,
    // MessageItem reaches for the plugin-installed global $t, which the
    // useI18n mock above does not provide.
    global: {
      stubs: { UiScrollArea: scrollStub },
      mocks: { $t: (key: string) => key },
    },
  })
}

describe('MessageList turn anchoring', () => {
  /**
   * Regression guard for the 真机走查 finding: a first-turn run has no assistant
   * message yet, so activity that only ever hangs off a message id renders
   * nowhere — the conversation shows a spinner and "awaiting user" with no tool
   * card and no in-chat approval affordance.
   */
  it('renders tool activity and its approval affordance while no assistant message exists', () => {
    const wrapper = mountList({
      messages: [message('u1', 'user')],
      liveTurn: { toolCalls: [approvalCall()] },
    })

    const live = wrapper.find('[data-testid="live-turn"]')
    expect(live.exists()).toBe(true)
    expect(live.text()).toContain('write_file')

    const items = wrapper.findAllComponents(MessageItem)
    expect(items).toHaveLength(1)
    expect(items[0].props('toolCalls')).toBeUndefined()
    wrapper.unmount()
  })

  it('forwards an approval decision raised from the live turn', async () => {
    const wrapper = mountList({
      messages: [],
      liveTurn: { toolCalls: [approvalCall()], thinkingSteps: [step()] },
    })

    wrapper.findComponent(ToolCallCard).vm.$emit('approve', 'approval-1')
    expect(wrapper.emitted('approve')?.[0]).toEqual(['approval-1'])
    wrapper.unmount()
  })

  it('anchors a finished turn onto the answering message only, not every assistant message', () => {
    const toolCalls = [approvalCall({ status: 'completed', approvalId: null })]
    const wrapper = mountList({
      messages: [message('u1', 'user'), message('a1', 'assistant'), message('a2', 'assistant')],
      activityByMessage: { a2: { toolCalls, thinkingSteps: [step()] } },
    })

    const items = wrapper.findAllComponents(MessageItem)
    expect(items).toHaveLength(3)
    expect(items[1].props('toolCalls')).toBeUndefined()
    expect(items[2].props('toolCalls')).toEqual(toolCalls)
    expect(items[2].props('thinkingSteps')).toHaveLength(1)
    // The turn is over and its activity is on the message: no trailing live row.
    expect(wrapper.find('[data-testid="live-turn"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('renders nothing extra when a run has produced no activity', () => {
    const wrapper = mountList({ messages: [message('u1', 'user')] })
    expect(wrapper.find('[data-testid="live-turn"]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('MessageList edit-and-resend', () => {
  /**
   * Regression guard for the second half of the 真机走查 list: the pencil button was
   * wired to `saveEdit()` — a stub that closed the textarea and emitted nothing, so an
   * edit looked applied while the next run kept reasoning over the old sentence.
   */
  it('re-emits an edited user turn so the caller can cut history and resend it', async () => {
    const wrapper = mountList({ messages: [message('u1', 'user')] })
    const item = wrapper.findAllComponents(MessageItem)[0]!
    await item.find('[aria-label="chat.edit"]').trigger('click')

    const input = item.find('[data-testid="message-edit-input"]')
    expect(input.exists()).toBe(true)
    await input.setValue('改过的话')
    // The confirm button names itself: "保存" would promise persistence the cut alone
    // does not deliver, and a raw key here would mean the label is missing in both bundles.
    expect(item.find('[data-testid="message-edit-save"]').text()).toBe('chat.applyEdit')
    await item.find('[data-testid="message-edit-save"]').trigger('click')

    expect(wrapper.emitted('edit')?.[0]).toEqual([{ id: 'u1', content: '改过的话' }])
    wrapper.unmount()
  })

  it('offers no edit affordance for an optimistic row Core has never seen', () => {
    const wrapper = mountList({ messages: [message('pending-1', 'user')] })
    const item = wrapper.findAllComponents(MessageItem)[0]!
    // Reverting by an id the server never issued would target nothing, while copy
    // still makes sense for text the user just typed.
    expect(item.find('[aria-label="chat.edit"]').exists()).toBe(false)
    expect(item.find('[aria-label="chat.copy"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('cuts nothing when the correction is cleared', async () => {
    const wrapper = mountList({ messages: [message('u1', 'user')] })
    const item = wrapper.findAllComponents(MessageItem)[0]!
    await item.find('[aria-label="chat.edit"]').trigger('click')
    await item.find('[data-testid="message-edit-input"]').setValue('   ')
    await item.find('[data-testid="message-edit-save"]').trigger('click')

    expect(wrapper.emitted('edit')).toBeUndefined()
    wrapper.unmount()
  })
})

describe('MessageItem attachment strip', () => {
  function attachment(over: Partial<MessageAttachmentSummaryDto> = {}): MessageAttachmentSummaryDto {
    return {
      id: 'att-1',
      file_name: 'notes.txt',
      media_type: 'text/plain',
      content_hash: 'a'.repeat(64),
      content_length: 2048,
      created_at: '2026-09-21T00:00:00Z',
      bound_at: '2026-09-21T00:00:01Z',
      ...over,
    }
  }

  function carrying(rows: MessageAttachmentSummaryDto[]): MessageDto {
    return { ...message('u1', 'user'), attachments: rows } as MessageDto
  }

  /**
   * The href is compared against api.attachmentContentUrl itself, not a hand-written
   * string: a renderer that guessed the route shape would keep passing a literal
   * assertion after the Gateway moved the path.
   */
  it('addresses every row by its id through the gateway content route', () => {
    const wrapper = mountList({
      messages: [carrying([attachment(), attachment({ id: 'att-2', file_name: 'a.bin', media_type: 'application/octet-stream' })])],
    })
    const links = wrapper.findAll('[data-testid="message-attachments"] a')
    expect(links).toHaveLength(2)
    expect(links[0]!.attributes('href')).toBe(api.attachmentContentUrl('att-1'))
    expect(links[0]!.attributes('download')).toBe('notes.txt')
    expect(links[1]!.attributes('href')).toBe(api.attachmentContentUrl('att-2'))
    expect(links[1]!.text()).toContain('2 KB')
    wrapper.unmount()
  })

  it('shows an image as a thumbnail whose link still carries the original', () => {
    const wrapper = mountList({ messages: [carrying([attachment({ file_name: 'shot.png', media_type: 'image/png' })])] })
    const link = wrapper.find('[data-testid="message-attachments"] a')
    const img = link.find('img')
    expect(img.attributes('src')).toBe(api.attachmentContentUrl('att-1'))
    expect(img.attributes('alt')).toBe('shot.png')
    expect(link.attributes('href')).toBe(api.attachmentContentUrl('att-1'))
    wrapper.unmount()
  })

  it('will not render svg as an image, because svg can carry script', () => {
    const wrapper = mountList({ messages: [carrying([attachment({ file_name: 'diagram.svg', media_type: 'image/svg+xml' })])] })
    const strip = wrapper.find('[data-testid="message-attachments"]')
    expect(strip.find('img').exists()).toBe(false)
    expect(strip.text()).toContain('diagram.svg')
    wrapper.unmount()
  })

  it('names the strip and shows no storage path for any row', () => {
    const wrapper = mountList({ messages: [carrying([attachment()])] })
    const strip = wrapper.find('[data-testid="message-attachments"]')
    expect(strip.attributes('aria-label')).toBe('chat.attachments')
    expect(strip.attributes('role')).toBe('list')
    expect(strip.html()).not.toContain('content_reference')
    expect(strip.html()).not.toContain('DataRoot')
    wrapper.unmount()
  })

  it('renders no strip at all when the message carries nothing', () => {
    const wrapper = mountList({ messages: [message('u1', 'user')] })
    expect(wrapper.find('[data-testid="message-attachments"]').exists()).toBe(false)
    wrapper.unmount()
  })
})
