// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { computed, ref, type Ref } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { ApprovalDto } from '@/api'
import type { ToolCall, TurnActivity } from '@/composables/useAgentActivity'
import type { SpatialObject } from '@/lib/spatialObjects'

const h = vi.hoisted(() => ({
  editQueued: vi.fn(), dismissQueued: vi.fn(), decideApprovalById: vi.fn(), decideApproval: vi.fn(),
  readFile: vi.fn(), controlRun: vi.fn(), createInteraction: vi.fn(),
  observe: vi.fn(), disconnect: vi.fn(), resize: [] as ResizeObserverCallback[],
}))
vi.mock('@/controllers/HomeController', async () => {
  const { ref } = await import('vue')
  return { homeController: {
    draft: ref(''), selectedSessionId: ref('session-1'), currentSession: ref({ id: 'session-1' }),
    approvals: ref([]), approvalRules: ref([]), shellCommand: ref(''), busy: ref(false), agentTurnActivities: ref({}),
    editQueued: h.editQueued, dismissQueued: h.dismissQueued, decideApprovalById: h.decideApprovalById, decideApproval: h.decideApproval,
    requestShellApproval: vi.fn(), revokeApprovalRule: vi.fn(), createApprovalRule: vi.fn(),
  } }
})
vi.mock('@/api', () => ({ api: { readFile: h.readFile, controlRun: h.controlRun, createInteraction: h.createInteraction } }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: { error: vi.fn() } }) }))
vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()), useI18n: () => ({
  t: (key: string, params?: Record<string, unknown>) => [key, ...Object.values(params ?? {})].join(' '), te: () => true,
}) }))
vi.mock('@/components/chat/ToolCallCard.vue', () => ({ default: {
  props: ['toolCall', 'runId'], emits: ['approve', 'reject'],
  template: '<div data-testid="heavy-tool">{{ toolCall.toolName }}<button class="tool-approve" @click="$emit(\'approve\', toolCall.approvalId)">approve</button><button class="tool-reject" @click="$emit(\'reject\', toolCall.approvalId)">reject</button></div>',
} }))
vi.mock('@/components/chat/ThinkingProcess.vue', () => ({ default: {
  props: ['steps'], template: '<div data-testid="thinking-steps">{{ steps }}</div>',
} }))
vi.mock('@/components/ApprovalTab.vue', () => ({ default: {
  props: ['approvals', 'approvalRules', 'selectedSessionId', 'compact', 'shellCommand', 'busy'], emits: ['decide-approval'],
  template: '<div data-testid="heavy-approvals"><span v-for="a in approvals" :key="a.id">{{ a.summary }}</span><button v-if="approvals.length" @click="$emit(\'decide-approval\', approvals[0], \'approved\', \'run\')">approve</button></div>',
} }))

import SpatialWorkCard from './SpatialWorkCard.vue'
import TurnTimeline from '@/components/chat/TurnTimeline.vue'
import ApprovalTab from '@/components/ApprovalTab.vue'
import { homeController as c } from '@/controllers/HomeController'

// The production read model is computed; this test owns its mocked source.
const turnActivities = c.agentTurnActivities as Ref<Record<string, TurnActivity>>
const task: SpatialObject = {
  id: 'task:run-123456789:task-1', groupId: 'run-123456789', kind: 'task', runId: 'run-123456789',
  taskId: 'task-1', title: 'Repair the parser and verify invalid inputs', owner: 'engineering#2', instanceId: 'instance-2',
  status: 'blocked', body: '**Parser repaired**\n\nVerification is still pending.',
  waitingFor: ['Install compiler'], unresolvedDependencies: ['missing-task'], truncated: true,
}
const tool: ToolCall = {
  id: 'tool-1', toolId: 'shell', toolName: 'Run compiler', runId: 'run-123456789', status: 'waiting_approval',
  startedAt: null, completedAt: null, durationMs: null, argsSummary: 'npm test', resultSummary: null,
  requiresApproval: true, approvalId: 'approval-1', evidence: [], seq: 2, risk: 'medium',
}
const toolObject: SpatialObject = { id: 'tool:run-123456789:tool-1', groupId: 'run-123456789', kind: 'tool', title: 'Run compiler', tool }
const meeting: SpatialObject = {
  id: 'meeting:session-1', groupId: 'session-1', kind: 'meeting', body: 'Unlinked historical answer',
  queue: [
    { id: 'history', content: 'Historical request', status: 'queued' },
    { id: 'active', content: 'Active request', status: 'running', runId: 'run-123456789' },
    { id: 'queued', content: 'Real queued request', status: 'queued', editable: true },
  ],
}
const approvalObject: SpatialObject = { id: 'approval:session-1', groupId: 'overview', kind: 'approval' }
const approval = (id: string, sessionId: string | null): ApprovalDto => ({ id, session_id: sessionId, kind: 'permission', summary: `Approval ${id}`, status: 'pending', created_at: '' })
const wrappers: VueWrapper[] = []
function render(props: { object?: SpatialObject; compact?: boolean; preview?: boolean; detail?: boolean }, provide: Record<string, unknown> = {}) {
  const wrapper = mount(SpatialWorkCard, { props, global: { provide } })
  wrappers.push(wrapper)
  return wrapper
}
beforeEach(() => {
  vi.clearAllMocks()
  h.resize.length = 0
  vi.stubGlobal('ResizeObserver', class {
    constructor(callback: ResizeObserverCallback) { h.resize.push(callback) }
    observe = h.observe
    disconnect = h.disconnect
  })
  c.draft.value = ''
  c.selectedSessionId.value = 'session-1'
  c.approvals.value = [approval('local', 'session-1'), approval('foreign', 'session-2'), approval('unlinked', null)]
  c.approvalRules.value = []
  turnActivities.value = {}
})
afterEach(() => {
  wrappers.splice(0).forEach(wrapper => wrapper.unmount())
  vi.unstubAllGlobals()
})

describe('SpatialWorkCard summaries and detail', () => {
  it('uses injected compact state with the real work title, owner and run focus actions', async () => {
    const open = vi.fn(), locate = vi.fn(), focus = vi.fn()
    const wrapper = render({}, {
      'uie:cardState': { objectId: task.id }, 'space:objects': computed(() => ({ [task.id]: task })),
      'space:compact': computed(() => ({ [task.id]: true })), 'space:open': open, 'space:locate': locate, 'space:focus-instance': focus,
    })
    expect(wrapper.classes()).toContain('is-compact')
    expect(wrapper.get('h2').text()).toBe(task.title)
    expect(wrapper.get('.space-owner').text()).toBe('engineering#2')
    expect(wrapper.get('.space-run').text()).toContain('run-1234')
    expect(wrapper.get('.space-card-status').text()).toContain('blocked')
    expect(wrapper.get('.space-summary').text()).toContain('Parser repaired')
    expect(wrapper.find('.markdown-body').exists()).toBe(false)
    expect(wrapper.text()).toContain('space.cluster.truncated')
    await wrapper.get('.space-open-detail').trigger('click')
    await wrapper.get('.space-owner').trigger('click')
    await wrapper.get('.space-run').trigger('click')
    expect(open).toHaveBeenCalledWith(task.id)
    expect(focus).toHaveBeenCalledWith(task.runId, task.instanceId)
    expect(locate).toHaveBeenCalledWith(`tasks:${task.runId}`)
  })

  it('never mounts heavy tools or approval controls in compact cards; legacy hosts stay full', () => {
    const compactTool = render({ object: toolObject, compact: true })
    const compactApproval = render({ object: approvalObject, compact: true })
    expect(compactTool.find('[data-testid="heavy-tool"]').exists()).toBe(false)
    expect(compactTool.text()).toContain('npm test')
    expect(compactApproval.find('[data-testid="heavy-approvals"]').exists()).toBe(false)
    expect(compactApproval.text()).toContain('Approval local')
    expect(compactApproval.text()).not.toContain('Approval foreign')
    expect(render({ object: toolObject }).find('[data-testid="heavy-tool"]').exists()).toBe(true)
  })

  it('explicit false overrides a compact host and detail always shows complete Markdown and dependency facts', () => {
    const provide = { 'space:compact': computed(() => ({ [task.id]: true })) }
    expect(render({ object: task, compact: false }, provide).classes()).not.toContain('is-compact')
    turnActivities.value = { [task.runId!]: { toolCalls: [tool] } }
    const wrapper = render({ object: task, compact: true, detail: true }, provide)
    expect(wrapper.classes()).not.toContain('is-compact')
    expect(wrapper.get('.markdown-body strong').text()).toBe('Parser repaired')
    expect(wrapper.get('.space-waiting').text()).toContain('Install compiler')
    expect(wrapper.get('.space-unresolved').text()).toContain('missing-task')
    expect(wrapper.findComponent(TurnTimeline).exists()).toBe(false)
    expect(wrapper.find('[data-testid="heavy-tool"]').exists()).toBe(false)
  })

  it('keeps every preview read-only even with detail requested and interactive providers available', () => {
    const run: SpatialObject = { id: `tasks:${task.runId}`, groupId: task.runId!, kind: 'run', runId: task.runId, rows: [{ id: task.id, title: task.title!, status: 'blocked' }] }
    turnActivities.value = { [task.runId!]: { toolCalls: [tool], supervisionReview: { runId: task.runId!, reasons: ['Review required'], options: ['continue', 'cancel'] } } }
    const measure = vi.fn(), open = vi.fn(), locate = vi.fn(), focus = vi.fn()
    for (const object of [task, toolObject, approvalObject, meeting, run]) {
      const wrapper = render({ object, preview: true, detail: true }, { 'space:measure': measure, 'space:open': open, 'space:locate': locate, 'space:focus-instance': focus })
      expect(wrapper.findAll('button')).toHaveLength(0)
      expect(wrapper.find('[data-testid="heavy-tool"]').exists()).toBe(false)
      expect(wrapper.find('[data-testid="heavy-approvals"]').exists()).toBe(false)
      expect(wrapper.findComponent(TurnTimeline).exists()).toBe(false)
      expect(wrapper.text()).not.toContain('Approval foreign')
      expect(wrapper.text()).not.toContain('Approval unlinked')
    }
    expect(h.observe).not.toHaveBeenCalled()
    expect(measure).not.toHaveBeenCalled()
    expect(h.editQueued).not.toHaveBeenCalled()
    expect(h.dismissQueued).not.toHaveBeenCalled()
    expect(h.controlRun).not.toHaveBeenCalled()
  })

  it('measures only canvas cards, not detail instances', () => {
    const measure = vi.fn()
    render({ object: task, detail: true }, { 'space:measure': measure })
    expect(h.observe).not.toHaveBeenCalled()
    render({ object: task, compact: true }, { 'space:measure': measure })
    expect(h.observe).toHaveBeenCalledOnce()
    h.resize[0]([{ borderBoxSize: [{ blockSize: 156 }] }] as unknown as ResizeObserverEntry[], {} as ResizeObserver)
    expect(measure).toHaveBeenCalledWith(task.id, 156)
  })

  it('offers queue edits only for real queued rows, disables edits for occupied drafts, and keeps removal available', async () => {
    const wrapper = render({ object: meeting, detail: true })
    expect(wrapper.findAll('.space-queue-actions')).toHaveLength(1)
    expect(wrapper.findAll('.space-queue-row')[0].text()).toContain('space.cluster.unlinkedMessage')
    const actions = wrapper.get('.space-queue-actions').findAll('button')
    await actions[0].trigger('click')
    expect(h.editQueued).toHaveBeenCalledWith('queued')
    h.editQueued.mockClear()
    c.draft.value = 'Keep my unsent draft'
    await flushPromises()
    expect(actions[0].attributes('disabled')).toBeDefined()
    expect(actions[0].attributes('title')).toBe('space.cluster.draftOccupied')
    await actions[0].trigger('click')
    expect(h.editQueued).not.toHaveBeenCalled()
    await actions[1].trigger('click')
    expect(h.dismissQueued).toHaveBeenCalledWith('queued')
    expect(c.draft.value).toBe('Keep my unsent draft')
  })

  it('filters approvals before the full view and preserves its decision callback', async () => {
    const wrapper = render({ object: approvalObject, detail: true })
    const panel = wrapper.getComponent(ApprovalTab)
    expect(panel.props('approvals').map((a: ApprovalDto) => a.id)).toEqual(['local'])
    await panel.get('button').trigger('click')
    expect(h.decideApproval).toHaveBeenCalledWith(c.approvals.value[0], 'approved', 'run')
    c.selectedSessionId.value = 'session-2'
    await flushPromises()
    expect(wrapper.findComponent(ApprovalTab).exists()).toBe(false)
    expect(wrapper.text()).not.toContain('Approval foreign')
  })

  it('shows only the selected run activity, reuses supervision decisions, and locates related objects without duplicating widgets', async () => {
    const run: SpatialObject = { id: `tasks:${task.runId}`, groupId: task.runId!, kind: 'run', runId: task.runId, body: 'Fix the parser', rows: [{ id: task.id, title: task.title!, status: 'blocked' }] }
    const plan: SpatialObject = { id: `plan:${task.runId}:plan-1`, groupId: task.runId!, kind: 'plan', title: 'Parser plan' }
    const foreignTool: SpatialObject = { ...toolObject, id: 'tool:other:call', groupId: 'other', title: 'Other run tool' }
    const locate = vi.fn()
    turnActivities.value = {
      [task.runId!]: { toolCalls: [tool], supervisionReview: { runId: task.runId!, reasons: ['Verify the parser'], options: ['continue', 'correct', 'cancel'] } },
      other: { toolCalls: [{ ...tool, toolName: 'Foreign call' }] },
    }
    const wrapper = render({ object: run, detail: true }, { 'space:locate': locate, 'space:objects': computed(() => ({ [plan.id]: plan, [toolObject.id]: toolObject, [foreignTool.id]: foreignTool })) })
    expect(wrapper.text()).toContain('space.cluster.loadedActivity')
    expect(wrapper.text()).not.toContain('Foreign call')
    expect(wrapper.text()).not.toContain('Other run tool')
    expect(wrapper.findAll('[data-testid="heavy-tool"]')).toHaveLength(1)
    expect(wrapper.getComponent(TurnTimeline).props('runId')).toBe(task.runId)
    await wrapper.get('[data-option="continue"]').trigger('click')
    await wrapper.get('[data-option="cancel"]').trigger('click')
    expect(h.controlRun).toHaveBeenNthCalledWith(1, task.runId, 'resume')
    expect(h.controlRun).toHaveBeenNthCalledWith(2, task.runId, 'cancel')
    vi.stubGlobal('prompt', vi.fn(() => 'Use the read-only check'))
    await wrapper.get('[data-option="correct"]').trigger('click')
    expect(h.createInteraction).toHaveBeenCalledWith('session-1', expect.objectContaining({ dispatch_mode: 'insert', target_run_id: task.runId, content: 'Use the read-only check' }))
    await wrapper.get('.tool-approve').trigger('click')
    await wrapper.get('.tool-reject').trigger('click')
    expect(h.decideApprovalById).toHaveBeenCalledWith('approval-1', 'approved')
    expect(h.decideApprovalById).toHaveBeenCalledWith('approval-1', 'rejected')
    const links = wrapper.findAll('.space-row-link')
    await links[0].trigger('click')
    expect(locate).toHaveBeenLastCalledWith(task.id)
    await wrapper.get('.space-related .space-row-link').trigger('click')
    expect(locate).toHaveBeenLastCalledWith(plan.id)
  })

  it('keeps Git shared and bounds current-file reads to the first 500 lines; preview never reads', async () => {
    const git = { state: ref({ cwd: '/workspace', loaded: true, loading: false, error: '', commits: [], preview: { branch: 'feature', ahead: 0, behind: 0, files: [{ path: 'src/a.ts' }] } }), refresh: vi.fn() }
    const object: SpatialObject = { id: 'git:session-1', groupId: 'overview', kind: 'git' }
    const preview = render({ object, preview: true }, { 'space:git': git })
    expect(preview.text()).toContain('space.cluster.sharedGit')
    expect(preview.findAll('button')).toHaveLength(0)
    expect(h.readFile).not.toHaveBeenCalled()
    h.readFile.mockResolvedValue({ status: 'failed', summary: 'File unavailable', data: { success: false, error: 'File unavailable' } })
    const wrapper = render({ object, detail: true }, { 'space:git': git })
    await wrapper.get('.space-git-file button').trigger('click')
    await flushPromises()
    expect(h.readFile).toHaveBeenCalledWith('/workspace', 'src/a.ts', { start_row: 1, end_row: 500 })
    expect(wrapper.get('.space-file-view summary').text()).toContain('space.currentFile')
    // The card words the failure; the read tool's own sentence stays as the tooltip.
    expect(wrapper.get('[role="status"]').text()).toBe('space.fileReadFailed')
    expect(wrapper.get('[role="status"]').attributes('title')).toBe('File unavailable')
  })
})
