// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { nextTick, ref, shallowRef } from 'vue'
import en from '@/locales/en'
import { applyEvents, createFeedState } from '@/lib/conversation/reducer'
import { parallelSubAgents, type FeedFixture } from '@/lib/conversation/fixtures'
import { conversationSelection, selectSubAgent } from '@/lib/conversation/selection'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import { resetAgentDirectory } from '@/composables/useAgentDirectory'

/**
 * APP-HOME-108 P5: parallel sub-agents each get a compact card under one "N agents
 * working…" group, a card opens in place to its own activity and summary, and "open in
 * panel" selects the same agent the right-column card then shows.
 */
const controller = vi.hoisted(() => ({
  currentSession: { value: { id: 'session-1' } },
  decideApprovalById: vi.fn(async () => {}),
  createApprovalRule: vi.fn(async () => {}),
  onEvent: vi.fn(() => () => {}),
}))
vi.mock('@/controllers/HomeController', () => ({ homeController: controller }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: { error: vi.fn(), info: vi.fn() } }) }))
const uie = vi.hoisted(() => ({ bus: { dispatch: vi.fn() }, scope: { value: 'home' }, snapshot: { value: { revision: 7 } } }))
vi.mock('@tinadec/ui', () => ({ useUie: () => uie }))
const apiMock = vi.hoisted(() => ({
  attachmentContentUrl: (id: string) => `/a/${id}`,
  listAgents: vi.fn(async () => [{ slug: 'search', display_name: 'Search' }, { slug: 'global_engineering', display_name: 'Engineer' }]),
  getToolExecutionPreview: vi.fn(),
  createInteraction: vi.fn(),
  controlRun: vi.fn(),
  getApprovalGates: vi.fn(async () => []),
}))
vi.mock('@/api', () => ({ api: apiMock }))

const { default: ConversationView } = await import('./ConversationView.vue')
const { default: SubAgentsPanel } = await import('./SubAgentsPanel.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
const global = { plugins: [i18n] }
const flush = async () => { for (let i = 0; i < 4; i += 1) { await nextTick(); await Promise.resolve() } }

function feedOf(events: FeedFixture['events']): ConversationFeed {
  const state = createFeedState()
  applyEvents(state, events)
  return { state: shallowRef(state), revision: ref(1), hydrated: ref(true) }
}

let mounted: VueWrapper[] = []
const track = <T extends VueWrapper>(wrapper: T): T => { mounted.push(wrapper); return wrapper }

beforeEach(() => {
  vi.clearAllMocks()
  resetAgentDirectory()
  selectSubAgent(null)
})
afterEach(() => { mounted.forEach((wrapper) => wrapper.unmount()); mounted = [] })

/** The parallel fixture, with search#1 finished and reporting back. */
function withSummary(): FeedFixture {
  const fixture = parallelSubAgents()
  const last = fixture.events.at(-1)!
  fixture.events.push({ ...last, seq: last.seq + 1, ts: '2026-10-13T09:10:00Z', type: 'worker.completed', payload: { task_id: 'task-a', task_key: 'research', status: 'completed', summary: 'Found **3** callers' } })
  return fixture
}

describe('sub-agent cards in the feed', () => {
  it('gives two parallel sub-agents one card each under one working group', async () => {
    const fixture = parallelSubAgents()
    const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture.events) }, global, attachTo: document.body }))
    await flush()
    const groups = wrapper.findAll('[data-testid="agent-group"]')
    expect(groups).toHaveLength(1)
    expect(groups[0]!.get('[data-testid="agent-group-head"]').text()).toBe('2 agents working…')
    const cards = groups[0]!.findAll('[data-testid="sub-agent-card"]')
    expect(cards).toHaveLength(2)
    expect(cards[0]!.text()).toContain('search#1 · Search')
    expect(cards[0]!.text()).toContain('Research the callers of useResults')
    expect(cards[0]!.get('[data-testid="sub-agent-latest"]').text()).toBe('Reading /w/src/graph.ts')
    expect(cards[1]!.text()).toContain('Needs approval')
    // The approval the second agent asked for is on the turn, not in its card.
    expect(cards[1]!.find('[data-testid="approval-card"]').exists()).toBe(false)
    expect(wrapper.findAll('[data-testid="approval-card"]')).toHaveLength(1)
  })

  it('opens a card in place to its own activity and its summary', async () => {
    const fixture = withSummary()
    const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture.events), workspaceRoot: '/w' }, global, attachTo: document.body }))
    const card = wrapper.findAll('[data-testid="sub-agent-card"]')[0]!
    expect(card.attributes('data-status')).toBe('completed')
    expect(card.find('[data-testid="sub-agent-summary"]').exists()).toBe(false)
    const toggle = card.get('[data-testid="sub-agent-toggle"]')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    await toggle.trigger('click')
    await flush()
    expect(toggle.attributes('aria-expanded')).toBe('true')
    expect(card.get('[data-testid="sub-agent-summary"]').html()).toContain('<strong>3</strong>')
    expect(card.find('[data-testid="activity-group"]').exists()).toBe(true)
  })

  it('"open in panel" selects that agent and opens the Sub-agents card', async () => {
    const fixture = parallelSubAgents()
    const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture.events) }, global, attachTo: document.body }))
    await wrapper.findAll('[data-testid="sub-agent-open-panel"]')[1]!.trigger('click')
    expect(conversationSelection.value).toEqual({ runId: 'run-1', taskId: 'task-b' })
    expect(uie.bus.dispatch).toHaveBeenCalledWith(expect.objectContaining({
      command: expect.objectContaining({ type: 'openCard', descriptorId: 'subAgents', slotId: 'right' }),
      expectedRevision: 7,
    }))
  })
})

describe('Sub-agents panel', () => {
  it('shows the agent selected from the feed, from the same feed', async () => {
    const fixture = parallelSubAgents()
    selectSubAgent({ runId: 'run-1', taskId: 'task-b' })
    const panel = track(mount(SubAgentsPanel, { props: { feed: feedOf(fixture.events), runs: fixture.runs }, global }))
    await flush()
    const detail = panel.get('[data-testid="sub-agents-detail"]')
    expect(detail.text()).toContain('global_engineering#1 · Engineer')
    expect(detail.text()).toContain('Implement the fix')
    expect(detail.find('[data-testid="sub-agent-open-panel"]').exists()).toBe(false)
    expect(detail.get('[data-testid="sub-agent-toggle"]').attributes('aria-expanded')).toBe('true')
    expect(panel.findAll('[data-testid="sub-agents-roster-item"]').map((item) => item.attributes('aria-selected'))).toEqual(['false', 'true'])
  })

  it('lists working agents first and selects the first when nothing is chosen', async () => {
    const fixture = withSummary()
    const panel = track(mount(SubAgentsPanel, { props: { feed: feedOf(fixture.events), runs: fixture.runs }, global }))
    await flush()
    const items = panel.findAll('[data-testid="sub-agents-roster-item"]')
    expect(items.map((item) => item.text())).toEqual([expect.stringContaining('Engineer'), expect.stringContaining('Search')])
    expect(panel.get('[data-testid="sub-agents-detail"]').text()).toContain('Engineer')
    await items[1]!.trigger('click')
    await flush()
    expect(conversationSelection.value).toEqual({ runId: 'run-1', taskId: 'task-a' })
    expect(panel.get('[data-testid="sub-agents-detail"]').text()).toContain('Found')
  })

  it('says so when the session has no sub-agents', () => {
    const panel = track(mount(SubAgentsPanel, { props: { feed: feedOf([]), runs: [] }, global }))
    expect(panel.text()).toContain('No sub-agents in this conversation yet')
  })
})
