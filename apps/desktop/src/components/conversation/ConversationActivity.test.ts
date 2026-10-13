// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { nextTick, ref, shallowRef } from 'vue'
import en from '@/locales/en'
import { applyEvents, createFeedState, type PlanEntry, type ReasoningEntry, type ToolEntry } from '@/lib/conversation/reducer'
import { EventScript } from '@/lib/conversation/fixtures/builder'
import { exploreEditShell, parallelSubAgents, pendingApproval, type FeedFixture } from '@/lib/conversation/fixtures'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import { clearToolPreviewCache } from '@/composables/useToolPreview'

/**
 * APP-HOME-108 P4 exit criteria: folds are open while work runs, collapse when it settles
 * and stop moving once the reader touches them; approvals stay outside every fold; quiet
 * reads and searches fold while edits stand alone; previews load only when opened and only
 * for finished calls. Plus the approval card's actions and the plan list.
 */
const controller = vi.hoisted(() => ({
  currentSession: { value: { id: 'session-1' } },
  decideApprovalById: vi.fn(async () => {}),
  createApprovalRule: vi.fn(async () => {}),
  onEvent: vi.fn(() => () => {}),
}))
vi.mock('@/controllers/HomeController', () => ({ homeController: controller }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: { error: vi.fn(), info: vi.fn() } }) }))
const apiMock = vi.hoisted(() => ({
  attachmentContentUrl: (id: string) => `/a/${id}`,
  listAgents: vi.fn(async () => []),
  getToolExecutionPreview: vi.fn(),
  createInteraction: vi.fn(async () => ({})),
  controlRun: vi.fn(),
  getApprovalGates: vi.fn(async () => []),
  sendTerminalStdin: vi.fn(),
  killTerminalSession: vi.fn(),
}))
vi.mock('@/api', () => ({ api: apiMock }))

const { default: ConversationView } = await import('./ConversationView.vue')
const { default: ReasoningBlock } = await import('./ReasoningBlock.vue')
const { default: ToolRow } = await import('./ToolRow.vue')
const { default: ApprovalCard } = await import('./ApprovalCard.vue')
const { default: AgentTodos } = await import('./AgentTodos.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
const global = { plugins: [i18n] }

const flush = async () => {
  for (let i = 0; i < 4; i += 1) { await nextTick(); await Promise.resolve() }
}

function feedOf(events: FeedFixture['events']): ConversationFeed {
  const state = createFeedState()
  applyEvents(state, events)
  return { state: shallowRef(state), revision: ref(1), hydrated: ref(true) }
}

let mounted: VueWrapper[] = []
function track<T extends VueWrapper>(wrapper: T): T { mounted.push(wrapper); return wrapper }

beforeEach(() => {
  clearToolPreviewCache()
  vi.clearAllMocks()
})
afterEach(() => {
  mounted.forEach((wrapper) => wrapper.unmount())
  mounted = []
})

describe('turn work fold', () => {
  it('is open with a live status while running, folds to a summary when settled, and keeps a reader\'s choice', async () => {
    const fixture = exploreEditShell()
    const runs = ref([{ ...fixture.runs[0]!, status: 'executing' }])
    const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages.slice(0, 1), runs: runs.value, feed: feedOf(fixture.events), workspaceRoot: '/w' }, global, attachTo: document.body }))
    expect(wrapper.get('[data-testid="turn-status"]').attributes('role')).toBe('status')
    expect(wrapper.findAll('[data-testid="tool-sentence"]').length).toBeGreaterThan(0)

    await wrapper.setProps({ runs: [{ ...fixture.runs[0]!, status: 'completed' }], messages: fixture.messages })
    await flush()
    expect(wrapper.find('[data-testid="turn-status"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="turn-summary"]').text()).toContain('Worked for')
    // Finished in view: the summary sweeps once.
    expect(wrapper.get('[data-testid="turn-summary"]').classes()).toContain('shimmer-once')
    expect(wrapper.find('[data-testid="tool-sentence"]').exists()).toBe(false)

    // The reader opens it; a later change of the run no longer folds it.
    await wrapper.get('[data-testid="turn-work"] > [data-slot="collapsible-trigger"]').trigger('click')
    await flush()
    await wrapper.setProps({ runs: [{ ...fixture.runs[0]!, status: 'completed', updated_at: '2026-10-13T11:00:00Z' }] })
    await flush()
    expect(wrapper.findAll('[data-testid="tool-sentence"]').length).toBeGreaterThan(0)
  })

  it('reports the same "worked for" in the answer footer as in the work summary', () => {
    const fixture = exploreEditShell()
    const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture.events) }, global, attachTo: document.body }))
    const summary = wrapper.get('[data-testid="turn-summary"]').text().split(' · ')[0]!
    // History is already still: no sweep.
    expect(wrapper.get('[data-testid="turn-summary"]').classes()).not.toContain('shimmer-once')
    expect(summary).toMatch(/^Worked for /)
    expect(wrapper.get('[data-testid="assistant-answer"]').text()).toContain(summary)
  })

  it('folds reads and searches together and gives edits and commands their own lines', () => {
    const fixture = exploreEditShell()
    const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages.slice(0, 1), runs: [{ ...fixture.runs[0]!, status: 'executing' }], feed: feedOf(fixture.events), workspaceRoot: '/w' }, global, attachTo: document.body }))
    const groups = wrapper.findAll('[data-testid="activity-group"]')
    expect(groups).toHaveLength(1)
    expect(groups[0]!.get('[data-testid="group-label"]').text()).toBe('Explored 2 files · 1 search · 1 folder')
    expect(wrapper.findAll('[data-testid="tool-sentence"]').map((row) => row.text())).toEqual(['Edited src/a.ts', 'Ran npm test'])
  })

  it('keeps approvals outside the folded work, the sub-agent\'s included', async () => {
    for (const fixture of [pendingApproval(), parallelSubAgents()]) {
      const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture.events) }, global, attachTo: document.body }))
      const work = wrapper.find('[data-testid="turn-work"]')
      if (work.exists()) {
        await work.get('[data-slot="collapsible-trigger"]').trigger('click')
        await flush()
        await new Promise((resolve) => setTimeout(resolve, 0))
        // reka keeps the content wrapper (hidden) and unmounts what is inside it.
        const content = work.get('[data-slot="collapsible-content"]')
        expect(content.attributes('hidden')).toBeDefined()
        expect(content.element.children).toHaveLength(0)
      }
      const card = wrapper.get('[data-testid="approval-card"]')
      expect(card.element.closest('[data-testid="turn-work"]')).toBeNull()
    }
  })
})

describe('ReasoningBlock', () => {
  const entry = (over: Partial<ReasoningEntry> = {}): ReasoningEntry => ({
    kind: 'reasoning', id: 'r', seq: 1, taskId: null, handle: null, agentName: 'meeting',
    text: '**Checking callers**\nLook in graph.ts', status: 'running', startedAt: '2026-10-13T09:00:00Z', endedAt: null, ...over,
  })

  it('is open while thinking with the newest title, and folds to "Thought for" when done', async () => {
    const wrapper = track(mount(ReasoningBlock, { props: { entry: entry() }, global }))
    expect(wrapper.get('[data-slot="collapsible-trigger"]').text()).toContain('Checking callers')
    expect(wrapper.find('.conversation-reasoning-text').text()).toBe('Look in graph.ts')
    await wrapper.setProps({ entry: entry({ status: 'completed', endedAt: '2026-10-13T09:00:12Z' }) })
    await flush()
    expect(wrapper.get('[data-slot="collapsible-trigger"]').text()).toContain('Thought for 12s')
    expect(wrapper.get('[data-testid="reasoning-label"]').classes()).toEqual(['shimmer-once'])
    expect(wrapper.find('.conversation-reasoning-text').exists()).toBe(false)
  })

  it('stays open after the reader opens it, even when the thought ends', async () => {
    const wrapper = track(mount(ReasoningBlock, { props: { entry: entry({ status: 'completed', endedAt: '2026-10-13T09:00:03Z' }) }, global }))
    expect(wrapper.find('.conversation-reasoning-text').exists()).toBe(false)
    expect(wrapper.get('[data-testid="reasoning-label"]').classes()).not.toContain('shimmer-once')
    await wrapper.get('[data-slot="collapsible-trigger"]').trigger('click')
    await flush()
    await wrapper.setProps({ entry: entry({ status: 'completed', endedAt: '2026-10-13T09:00:04Z', text: '**Checking callers**\nLook in graph.ts\nand b.ts' }) })
    await flush()
    expect(wrapper.find('.conversation-reasoning-text').text()).toContain('and b.ts')
  })
})

describe('ToolRow preview', () => {
  const tool = (over: Partial<ToolEntry> = {}): ToolEntry => {
    const state = createFeedState()
    applyEvents(state, new EventScript().toolCall('run-1', 'e-1', 'read_file', { resource_path: '/w/a.ts' }).events)
    return { ...(state.runs.get('run-1')!.byId.get('e-1') as ToolEntry), ...over }
  }

  it('fetches nothing until opened, nothing for a running call, then once when finished', async () => {
    apiMock.getToolExecutionPreview.mockResolvedValue({ execution_id: 'e-1', status: 'completed', preview: { kind: 'file_lines', path: '/w/a.ts', lines: [{ line_number: 1, text: 'export {}' }] }, truncated: false })
    const wrapper = track(mount(ToolRow, { props: { entry: tool({ status: 'running' }), runId: 'run-1', workspaceRoot: '/w' }, global }))
    expect(apiMock.getToolExecutionPreview).not.toHaveBeenCalled()
    await wrapper.get('[data-slot="collapsible-trigger"]').trigger('click')
    await flush()
    expect(apiMock.getToolExecutionPreview).not.toHaveBeenCalled()
    await wrapper.setProps({ entry: tool({ status: 'completed', endedAt: '2026-10-13T09:00:05Z' }) })
    await flush()
    expect(apiMock.getToolExecutionPreview).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid="tool-preview"]').text()).toContain('export {}')
    await wrapper.get('[data-slot="collapsible-trigger"]').trigger('click')
    await wrapper.get('[data-slot="collapsible-trigger"]').trigger('click')
    await flush()
    expect(apiMock.getToolExecutionPreview).toHaveBeenCalledTimes(1)
  })

  it('says why a call failed', () => {
    const wrapper = track(mount(ToolRow, { props: { entry: tool({ toolId: 'shell', command: 'npm test', resourcePath: null, status: 'completed', toolSuccess: false, exitCode: 1, endedAt: '2026-10-13T09:00:05Z' }), runId: 'run-1' }, global }))
    expect(wrapper.get('[data-testid="tool-row"]').attributes('data-phase')).toBe('failed')
    expect(wrapper.text()).toContain('Failed: npm test')
    expect(wrapper.text()).toContain('exit 1')
  })
})

describe('ApprovalCard actions', () => {
  const entry = (): ToolEntry => {
    const state = createFeedState()
    const s = new EventScript().toolCall('run-1', 'e-1', 'shell', { command: 'npm run build', requires_approval: true })
    s.emit('run-1', 'approval.requested', { approval_id: 'p-1', execution_id: 'e-1' })
    applyEvents(state, s.events)
    return state.runs.get('run-1')!.byId.get('e-1') as ToolEntry
  }
  const mountCard = () => track(mount(ApprovalCard, { props: { entry: entry(), runId: 'run-1' }, global, attachTo: document.body }))

  it('approves once or for the whole run, and rejects', async () => {
    const card = mountCard()
    await card.get('[data-testid="approval-approve"]').trigger('click'); await flush()
    await card.get('[data-testid="approval-approve-run"]').trigger('click'); await flush()
    await card.get('[data-testid="approval-reject"]').trigger('click'); await flush()
    expect(controller.decideApprovalById.mock.calls).toEqual([['p-1', 'approved'], ['p-1', 'approved', 'run'], ['p-1', 'rejected']])
  })

  it('"do this instead" rejects and inserts the correction into the run', async () => {
    const card = mountCard()
    await card.get('[data-testid="approval-instead"]').trigger('click'); await flush()
    await card.get('[data-testid="approval-correction"]').setValue('Use pnpm build')
    await card.get('[data-testid="approval-send-correction"]').trigger('click'); await flush()
    expect(controller.decideApprovalById).toHaveBeenCalledWith('p-1', 'rejected')
    expect(apiMock.createInteraction).toHaveBeenCalledWith('session-1', expect.objectContaining({ content: 'Use pnpm build', dispatch_mode: 'insert', target_run_id: 'run-1' }))
  })

  it('offers an "always allow" rule for the command\'s first word, then approves this call', async () => {
    const card = mountCard()
    expect(card.get('[data-testid="approval-allow-prefix"]').text()).toBe('Always allow commands starting with npm')
    await card.get('[data-testid="approval-allow-prefix"]').trigger('click'); await flush()
    expect(controller.createApprovalRule).toHaveBeenCalledWith({ kind: 'command_prefix', tool_id: 'shell', pattern: 'npm', session_id: 'session-1' })
    expect(controller.decideApprovalById).toHaveBeenCalledWith('p-1', 'approved')
  })

  it('answers ⌘/Ctrl+Enter and ⌘/Ctrl+Backspace', async () => {
    const card = mountCard()
    await card.get('[data-testid="approval-card"]').trigger('keydown', { key: 'Enter', ctrlKey: true }); await flush()
    await card.get('[data-testid="approval-card"]').trigger('keydown', { key: 'Backspace', metaKey: true }); await flush()
    expect(controller.decideApprovalById.mock.calls).toEqual([['p-1', 'approved'], ['p-1', 'rejected']])
  })
})

describe('AgentTodos', () => {
  const plan = (count: number, previous: PlanEntry['previous'] = null): PlanEntry => ({
    kind: 'plan', id: 'plan:t', seq: 1, taskId: 't', handle: null, explanation: 'Added a test step', at: '',
    steps: Array.from({ length: count }, (_, i) => ({ step: `Step ${i + 1}`, status: i === 0 ? 'completed' : i === 1 ? 'in_progress' : 'pending' })),
    previous,
  })

  it('shows six steps and folds the rest', async () => {
    const wrapper = track(mount(AgentTodos, { props: { entry: plan(9) }, global }))
    expect(wrapper.findAll('.conversation-todo')).toHaveLength(6)
    await wrapper.get('.conversation-link-button').trigger('click')
    expect(wrapper.findAll('.conversation-todo')).toHaveLength(9)
  })

  it('marks steps added by the latest rewrite and shows why', () => {
    const wrapper = track(mount(AgentTodos, { props: { entry: plan(3, [{ step: 'Step 1', status: 'pending' }, { step: 'Step 2', status: 'pending' }]) }, global }))
    expect(wrapper.findAll('.conversation-todo.is-new').map((row) => row.text())).toEqual([expect.stringContaining('Step 3')])
    expect(wrapper.text()).toContain('Added a test step')
  })
})
