// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { nextTick, ref, shallowRef } from 'vue'
import en from '@/locales/en'
import { applyEvents, createFeedState, type PlanEntry, type ReasoningEntry, type ToolEntry } from '@/lib/conversation/reducer'
import type { EventEnvelope } from '@/api'
import { EventScript } from '@/lib/conversation/fixtures/builder'
import { exploreEditShell, finishedTeamRun, parallelSubAgents, pendingApproval, type FeedFixture } from '@/lib/conversation/fixtures'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import { clearToolPreviewCache } from '@/composables/useToolPreview'
import { resetConversationFlowStyleForTests, setConversationFlowPreset } from '@/lib/conversationFlowStyle'

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
  resetConversationFlowStyleForTests()
})
afterEach(() => {
  mounted.forEach((wrapper) => wrapper.unmount())
  mounted = []
  resetConversationFlowStyleForTests()
})

describe('turn work fold', () => {
  it('is open with a live status while running, folds to a summary when settled, and keeps a reader\'s choice', async () => {
    // The minimal step is the one that folds a finished turn away; the fuller steps keep it
    // open and are covered by the next case. Behaviour is chosen in Settings → General.
    setConversationFlowPreset('minimal')
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

  it('keeps a settled turn open in the shapes that are meant to show the work', async () => {
    setConversationFlowPreset('rich')
    const fixture = exploreEditShell()
    const runs = ref([{ ...fixture.runs[0]!, status: 'executing' }])
    const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages.slice(0, 1), runs: runs.value, feed: feedOf(fixture.events), workspaceRoot: '/w' }, global, attachTo: document.body }))

    await wrapper.setProps({ runs: [{ ...fixture.runs[0]!, status: 'completed' }], messages: fixture.messages })
    await flush()
    // The rows are still there: the summary line is the fold's heading, not a replacement for it.
    expect(wrapper.findAll('[data-testid="tool-sentence"]').map((row) => row.text())).toEqual(['Edited src/a.ts', 'Ran npm test'])
    expect(wrapper.get('[data-testid="turn-summary"]').text()).toContain('Worked for')
    expect(wrapper.get('[data-testid="turn-work"] > [data-slot="collapsible-content"]').attributes('hidden')).toBeUndefined()
  })

  it('folds all but the last rows of a settled turn when the shape sets a budget', async () => {
    setConversationFlowPreset('concise')
    const s = new EventScript()
    for (let i = 1; i <= 9; i += 1) {
      s.toolCall('run-1', `e-${i}`, 'replace_lines', { resource_path: `/w/f${i}.ts` }).toolDone('run-1', `e-${i}`, 'replace_lines')
    }
    const messages = [
      { id: 'm-1', session_id: 's', role: 'user', content: 'Touch nine files', created_at: '2026-10-13T09:00:00Z', run_id: null, attachments: [] },
      { id: 'm-2', session_id: 's', role: 'assistant', content: 'Done.', created_at: '2026-10-13T09:00:09Z', run_id: 'run-1', attachments: [] },
    ] as never
    const runs = [{ id: 'run-1', status: 'completed', trigger_message_id: 'm-1', created_at: '2026-10-13T09:00:01Z', updated_at: '2026-10-13T09:00:09Z' }]
    const wrapper = track(mount(ConversationView, { props: { messages, runs, feed: feedOf(s.events), workspaceRoot: '/w' }, global, attachTo: document.body }))

    expect(wrapper.findAll('[data-testid="tool-sentence"]')).toHaveLength(7)
    const earlier = wrapper.get('[data-testid="turn-earlier"]')
    expect(earlier.text()).toBe('2 earlier steps')
    await earlier.trigger('click')
    await flush()
    expect(wrapper.findAll('[data-testid="tool-sentence"]')).toHaveLength(9)
    expect(wrapper.find('[data-testid="turn-earlier"]').exists()).toBe(false)
  })

  it('never spends the budget on a sub-agent row, a plan or a failure', async () => {
    setConversationFlowPreset('concise')
    const fixture = finishedTeamRun()
    const wrapper = track(mount(ConversationView, { props: { messages: fixture.messages, runs: fixture.runs, feed: feedOf(fixture.events), workspaceRoot: '/w' }, global, attachTo: document.body }))
    await flush()
    // The thought and the wait are the two rows this budget folds; everything a settled team
    // run is about stays in view without a click.
    expect(wrapper.get('[data-testid="turn-earlier"]').text()).toBe('2 earlier steps')
    expect(wrapper.findAll('[data-testid="sub-agent-card"]')).toHaveLength(2)
    expect(wrapper.find('[data-testid="agent-todos"]').exists()).toBe(true)
    expect(wrapper.findAll('[data-testid="tool-sentence"]').map((row) => row.text())).toContain('Failed: npm run build')
    // Opening the fold adds only the rows it was hiding, in their original place.
    await wrapper.get('[data-testid="turn-earlier"]').trigger('click')
    await flush()
    expect(wrapper.find('[data-testid="turn-earlier"]').exists()).toBe(false)
    expect(wrapper.findAll('[data-testid="sub-agent-card"]')).toHaveLength(2)
  })

  it('never hides rows of a run that is still going', async () => {
    setConversationFlowPreset('concise')
    const s = new EventScript()
    for (let i = 1; i <= 9; i += 1) {
      s.toolCall('run-1', `e-${i}`, 'replace_lines', { resource_path: `/w/f${i}.ts` }).toolDone('run-1', `e-${i}`, 'replace_lines')
    }
    const messages = [{ id: 'm-1', session_id: 's', role: 'user', content: 'Touch nine files', created_at: '2026-10-13T09:00:00Z', run_id: null, attachments: [] }] as never
    const runs = [{ id: 'run-1', status: 'executing', trigger_message_id: 'm-1', created_at: '2026-10-13T09:00:01Z', updated_at: '2026-10-13T09:00:09Z' }]
    const wrapper = track(mount(ConversationView, { props: { messages, runs, feed: feedOf(s.events), workspaceRoot: '/w' }, global, attachTo: document.body }))
    expect(wrapper.find('[data-testid="turn-earlier"]').exists()).toBe(false)
    expect(wrapper.findAll('[data-testid="tool-sentence"]')).toHaveLength(9)
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
    text: '**Checking callers**\nLook in graph.ts', output: '', status: 'running', startedAt: '2026-10-13T09:00:00Z', endedAt: null, ...over,
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

  it('carries a plan far longer than twenty steps, and folds it back', async () => {
    // plan_update accepts up to 64 steps: a migration or spec build is structurally long, so the
    // plan area has to render all of them and give the turn back when the list is closed again.
    const wrapper = track(mount(AgentTodos, { props: { entry: plan(64) }, global }))
    expect(wrapper.findAll('.conversation-todo')).toHaveLength(6)
    expect(wrapper.text()).toContain('64')
    await wrapper.get('.conversation-link-button').trigger('click')
    expect(wrapper.findAll('.conversation-todo')).toHaveLength(64)
    await wrapper.get('.conversation-link-button').trigger('click')
    expect(wrapper.findAll('.conversation-todo')).toHaveLength(6)
  })

  it('renders every row when two steps share the same wording', () => {
    const repeated = Array.from({ length: 8 }, () => ({ step: 'Update the call sites', status: 'pending' as const }))
    const wrapper = track(mount(AgentTodos, { props: { entry: { ...plan(0), steps: repeated } }, global }))
    expect(wrapper.findAll('.conversation-todo')).toHaveLength(6)
    expect(wrapper.findAll('.conversation-todo').every((row) => row.text().includes('Update the call sites'))).toBe(true)
  })
})

describe('live activity reactivity', () => {
  // The reducer appends `entry.text` on the same object it handed the leaf as a prop. If the
  // record were a plain object, Vue would skip the child (stable prop identity) and cache its
  // computeds, freezing the reasoning text until a remount. This pins the reactive-record fix:
  // a delta plus a published revision must reach the DOM with no remount. Reverting the
  // `reactive(...)` wrap in reducer.ts turns this red (the text stops at the first frame).
  it('streams reasoning text into an open block as deltas arrive', async () => {
    const state = createFeedState()
    const envelope = (seq: number, type: string, payload: Record<string, unknown>): EventEnvelope => ({
      run_id: 'r1', v: '1.0', type, request_id: '', session_id: 's1', trace_id: '', seq,
      ts: '2026-11-07T09:00:00Z', capabilities: [], payload,
    })
    applyEvents(state, [
      envelope(1, 'model.output.started', { response_id: 'resp1', task_id: 't1', channel: 'reasoning' }),
      envelope(2, 'model.output.delta', { response_id: 'resp1', channel: 'reasoning', delta: 'first thought' }),
    ])
    const revision = ref(1)
    const feed: ConversationFeed = { state: shallowRef(state), revision, hydrated: ref(true) }
    const runs = [{ id: 'r1', status: 'executing', trigger_message_id: 'm1', created_at: '2026-11-07T09:00:00Z', updated_at: null }]
    const messages = [{ id: 'm1', role: 'user', content: 'hi', created_at: '2026-11-07T09:00:00Z', session_id: 's1' } as never]
    const wrapper = track(mount(ConversationView, { props: { messages, runs, feed, workspaceRoot: '/w' }, global, attachTo: document.body }))
    await flush()
    expect(wrapper.text()).toContain('first thought')

    applyEvents(state, [envelope(3, 'model.output.delta', { response_id: 'resp1', channel: 'reasoning', delta: ' and a second' })])
    revision.value += 1
    await flush()
    expect(wrapper.text()).toContain('and a second')
  })
})
