// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import TurnTimeline from './TurnTimeline.vue'
import ToolResultView from './ToolResultView.vue'
import ThinkingProcess from './ThinkingProcess.vue'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'
import zh from '@/locales/zh-CN'

const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })

vi.mock('@/controllers/HomeController', () => ({
  homeController: { currentSession: { value: { id: 'session-1' } } },
}))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: { error: vi.fn() } }),
}))
vi.mock('@/api', () => ({
  api: { getApprovalGates: vi.fn().mockResolvedValue(null), getToolExecutionResult: vi.fn() },
}))

// A write, not a read: quiet reads fold into a summary row by design, so the rich-result
// view only ever hangs off a call that keeps its own line.
const finished: ToolCall = {
  id: 'call-1', toolId: 'write_file', toolName: 'write_file', status: 'completed',
  startedAt: null, completedAt: null, durationMs: 12, argsSummary: 'src/graph.ts',
  resultSummary: 'written', requiresApproval: false, approvalId: null, evidence: [], seq: 1, risk: 'low',
}

function step(over: Partial<ThinkingStep> & { id: string }): ThinkingStep {
  return { type: 'reasoning', title: '', description: '', timestamp: '', durationMs: null, ...over } as ThinkingStep
}

function mountWith(props: Record<string, unknown>) {
  return mount(TurnTimeline, { props: props as never, global: { plugins: [i18n] } })
}

/** The trail, then the row itself — the result view is fetched per row, on open. */
async function openBoth(wrapper: ReturnType<typeof mountWith>) {
  await wrapper.get('[data-testid="activity-toggle"]').trigger('click')
  await wrapper.get('.tool-call-head').trigger('click')
}

describe('TurnTimeline conversation template', () => {
  it('does not fetch a result for a row the reader has not opened', async () => {
    const wrapper = mountWith({ toolCalls: [finished], settled: true, richToolViews: true })
    await wrapper.get('[data-testid="activity-toggle"]').trigger('click')
    // Open trail, closed row: nothing should have been requested for this call yet.
    expect(wrapper.findComponent(ToolResultView).exists()).toBe(false)
  })

  it('opens a finished tool row onto the stored result normally', async () => {
    const wrapper = mountWith({ toolCalls: [finished], settled: true, richToolViews: true })
    await openBoth(wrapper)
    expect(wrapper.findComponent(ToolResultView).exists()).toBe(true)
  })

  it('withholds the rich view under the concise template, keeping the summary', async () => {
    const wrapper = mountWith({ toolCalls: [finished], settled: true, richToolViews: false })
    await openBoth(wrapper)
    expect(wrapper.findComponent(ToolResultView).exists()).toBe(false)
    // Concise withholds richness, not information: the tool's own summary is still there.
    expect(wrapper.text()).toContain('written')
  })
})

/**
 * Attributed steps are drawn inside their subagent's card. Feeding them to the turn's own rows as
 * well put the same plan or thought on screen twice, and a turn whose only activity was a
 * subagent then had no details of its own — so it rendered nothing at all.
 */
describe('TurnTimeline subagent ownership', () => {
  const attributed = [
    step({ id: 'p1', seq: 1, type: 'plan', title: 'search#1 的计划', description: '✓ 搜索', handle: 'search#1', taskId: 't1', details: { slug: 'search' } }),
    step({ id: 'r1', seq: 2, type: 'reasoning', title: 'search#1', description: '想一下', handle: 'search#1', taskId: 't1' }),
  ]

  it('renders an attributed step only inside its subagent card', async () => {
    const wrapper = mountWith({ thinkingSteps: attributed, settled: false })
    await wrapper.get('[data-testid="activity-toggle"]').trigger('click')
    const cards = wrapper.findAll('[data-testid="subagent-card"]')
    expect(cards).toHaveLength(1)
    // Two folds: the turn's activity, then the card's own body.
    await cards[0]!.get('.subagent-head').trigger('click')
    // The reasoning belongs to the card, not to the turn's own spine.
    expect(cards[0]!.findComponent(ThinkingProcess).exists()).toBe(true)
    expect(wrapper.findAll('.activity-details > .thinking-process')).toHaveLength(0)
  })

  it('still renders the fold when a subagent is the only activity', async () => {
    const wrapper = mountWith({ thinkingSteps: attributed, settled: false })
    expect(wrapper.find('[data-testid="activity-summary"]').exists()).toBe(true)
    await wrapper.get('[data-testid="activity-toggle"]').trigger('click')
    expect(wrapper.findAll('[data-testid="subagent-card"]').length).toBe(1)
  })
})
