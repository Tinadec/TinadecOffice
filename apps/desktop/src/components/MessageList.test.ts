// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import MessageList from './MessageList.vue'
import MessageItem from './MessageItem.vue'
import ToolCallCard from './chat/ToolCallCard.vue'
import type { MessageDto } from '../api'
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
