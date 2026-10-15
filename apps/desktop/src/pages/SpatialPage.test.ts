// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest'
import { defineComponent, h, inject, ref, type Ref, type PropType } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { ApprovalDto, EventEnvelope, MessageDto, OrchestrationSnapshotDto, SessionTopologyDto, TopologyRunDto, TopologyTaskDto } from '@/api'
import type { ToolCall, TurnActivity } from '@/composables/useAgentActivity'
import { __resetUieForTests, initUie, type UieStore } from '../../../TinadecUI/src/components/useUie'
import { createCardRegistry } from '../../../TinadecUI/src/engine/registry'
import type { UieColumn } from '../../../TinadecUI/src/engine/types'
import SpatialPage from './SpatialPage.vue'
import SpatialWorkCard from '@/components/spatial/SpatialWorkCard.vue'
import { homeController as c } from '@/controllers/HomeController'

const mocks = vi.hoisted(() => ({
  topology: vi.fn(), readFile: vi.fn(), subscribe: vi.fn(), unsubscribe: vi.fn(),
  setViewport: vi.fn(), applyNodeChanges: vi.fn(), updateNodeInternals: vi.fn(),
  listener: undefined as ((event: EventEnvelope) => unknown) | undefined,
  terminalCreated: vi.fn(),
}))
vi.mock('@/controllers/HomeController', async () => {
  const { ref } = await import('vue')
  return { homeController: {
    viewMode: ref('space'), selectedSessionId: ref<string | null>('s'), selectedProjectId: ref(null),
    currentSession: ref(null), currentProject: ref(null), projects: ref([]), runs: ref([]),
    messages: ref<MessageDto[]>([]), queuedMessages: ref([]), approvals: ref<ApprovalDto[]>([]), approvalRules: ref([]),
    orchestration: ref<OrchestrationSnapshotDto | null>(null), agentTurnActivities: ref<Record<string, TurnActivity>>({}),
    streamingReply: ref(''), streamingReplies: ref<Record<string, string>>({}), stoppableRunId: ref<string | null>('r1'),
    draft: ref(''), shellCommand: ref(''), currentPermission: ref('ask'), busy: ref(false), working: ref(false),
    composerSettings: ref({ mode_version_id: null, meeting_model_override: null, permission_mode: 'default', space_options: null }),
    settingsSaving: ref(false), settingsError: ref(null), updateComposerSettings: vi.fn(),
    setViewMode: vi.fn(), start: vi.fn(),
  } }
})
vi.mock('@/api', () => ({ api: { getSessionTopology: mocks.topology, readFile: mocks.readFile } }))
vi.mock('@/lib/sessionEventBus', () => ({ subscribeToSessionEvents: mocks.subscribe }))
vi.mock('@/lib/uiEngine', async () => {
  const { useUie } = await import('../../../TinadecUI/src/components/useUie')
  return { ensureProductionUie: useUie }
})
vi.mock('@/composables/useSpatialGit', async () => {
  const { ref } = await import('vue')
  return { useSpatialGit: () => ({ state: ref({
    cwd: '/workspace', loaded: true, loading: false, error: '', commits: [],
    preview: { branch: 'feature', files: [{ path: 'src/task.ts' }] },
  }), refresh: vi.fn() }) }
})
vi.mock('@/composables/usePanelStyles', () => ({ usePanelStyles: () => ({
  getPanelStyle: () => ({}), getPanelDataAttributes: () => ({}),
}) }))
vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()), useI18n: () => ({
  t: (key: string, params?: Record<string, unknown>) => [key, ...Object.values(params ?? {})].join(' '), te: () => true,
}) }))
vi.mock('@/components/ui', async () => ({
  UiButton: { template: '<button><slot /></button>' },
  UiCheckbox: { props: ['modelValue'], emits: ['update:modelValue'], template: '<button role="checkbox" :aria-checked="modelValue" @click="$emit(\'update:modelValue\', !modelValue)" />' },
  UiSelect: (await import('../components/ui/select.vue')).default,
}))
vi.mock('@/components/ComposerBar.vue', () => ({ default: { template: '<div class="composer-box" />' } }))
vi.mock('@/components/ApprovalTab.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/components/chat/TurnTimeline.vue', () => ({ default: { template: '<div />' } }))
// xterm/transport is outside this regression. The leaf uses the same injected
// action as TerminalCallBlock; the actual UIE command, canvas and hosts stay real.
vi.mock('@/components/chat/ToolCallCard.vue', () => ({ default: defineComponent({
  setup() {
    const open = inject<() => void>('space:open-terminal')
    return () => h('button', { class: 'open-terminal', onClick: open }, 'Open terminal')
  },
}) }))
vi.mock('@tinadec/ui', async () => ({
  UieShell: { template: '<main><slot /></main>' },
  UieCanvas: (await import('../../../TinadecUI/src/components/UieCanvas.vue')).default,
  UieCardHost: (await import('../../../TinadecUI/src/components/UieCardHost.vue')).default,
  arrangeSpace: (await import('../../../TinadecUI/src/engine/spatial')).arrangeSpace,
  routeSpatialEdges: (await import('../../../TinadecUI/src/engine/spatialRouting')).routeSpatialEdges,
}))
// Preserve actual UieCardHost activation/reuse; skip tab chrome and drag sensors.
vi.mock('../../../TinadecUI/src/components/UieColumn.vue', async () => {
  const { useUie } = await import('../../../TinadecUI/src/components/useUie')
  const Host = (await import('../../../TinadecUI/src/components/UieCardHost.vue')).default
  return { default: defineComponent({
    props: { column: { type: Object as PropType<UieColumn>, required: true } },
    setup(props) {
      const uie = useUie()
      return () => h('section', { 'data-column': props.column.slotId }, props.column.primary.tabIds.map(id =>
        h(Host, { key: id, instance: uie.snapshot.value.cards[id], active: props.column.primary.activeTabId === id })))
    },
  }) }
})
vi.mock('@vue-flow/core', () => ({
  MarkerType: { ArrowClosed: 'arrowclosed' }, Position: { Top: 'top', Bottom: 'bottom', Left: 'left', Right: 'right' },
  Handle: { template: '<span />' }, BaseEdge: { template: '<path />' },
  useVueFlow: () => ({ setViewport: mocks.setViewport, applyNodeChanges: mocks.applyNodeChanges,
    updateNodeInternals: mocks.updateNodeInternals, zoomIn: vi.fn(), zoomOut: vi.fn() }),
  VueFlow: {
    name: 'VueFlow', props: ['nodes', 'edges'], emits: ['viewport-change-end', 'edge-click', 'node-drag-start'],
    template: `<div class="flow-probe">
      <div v-for="node in nodes" :key="node.id" :data-node="node.id" :style="node.style">
        <slot v-if="node.type === 'work'" name="node-work" :id="node.id" :selected="false" />
        <slot v-else name="node-cluster" :data="node.data" />
      </div>
      <span v-for="edge in edges" :key="edge.id" class="edge-probe" :data-source="edge.source" :data-target="edge.target">{{ edge.label }}</span>
      <slot />
    </div>`,
  },
}))
vi.mock('@vue-flow/background', () => ({ Background: { template: '<span />' } }))
vi.mock('@vue-flow/node-resizer', () => ({ NodeResizer: { template: '<span />' } }))

const message = (run_id: string, content: string, session_id = 's'): MessageDto => ({
  id: `message-${run_id}`, session_id, role: 'user', run_id, content, created_at: '', attachments: [],
})
const task = (task_id: string, extra: Partial<TopologyTaskDto> = {}): TopologyTaskDto => ({
  task_id, task_key: task_id, title: `Task ${task_id}`, status: 'running', dependencies: [], write_scope: [],
  worker_instance_id: `instance-${task_id}`, handle: `worker#${task_id}`, result_summary: `Result ${task_id}`, ...extra,
})
const run = (run_id: string, tasks: TopologyTaskDto[]): TopologyRunDto => ({
  run_id, status: 'running', tasks, instances: [], tasks_truncated: false, instances_truncated: false,
})
function topology(session_id = 's'): SessionTopologyDto {
  return { session_id, runs: [
    run('r1', [task('a'), task('b'), task('c', { dependencies: ['a', 'b'] })]),
    run('r2', [task('other')]),
  ], leases: [], members: [], runs_truncated: false, leases_truncated: false, members_truncated: false, generated_at: '2026-10-06T12:00:00Z' }
}
const tool: ToolCall = {
  id: 'shell-1', toolId: 'shell', toolName: 'Run tests', runId: 'r1', status: 'completed',
  startedAt: null, completedAt: null, durationMs: null, argsSummary: 'npm test', resultSummary: null,
  requiresApproval: false, approvalId: null, evidence: [], seq: 1, risk: 'medium',
}
const turns = c.agentTurnActivities as Ref<Record<string, TurnActivity>>
const stoppableRunId = c.stoppableRunId as Ref<string | null>
const streamingReply = c.streamingReply as Ref<string>
const streamingReplies = c.streamingReplies as Ref<Record<string, string>>
const TerminalProbe = defineComponent({ setup() {
  mocks.terminalCreated()
  const count = ref(0)
  return () => h('button', { 'data-terminal-host': '', onClick: () => count.value++ }, String(count.value))
} })
let uie: UieStore
let wrapper: VueWrapper
let dispatch: MockInstance<UieStore['dispatch']>
async function render() {
  wrapper = mount(SpatialPage, { attachTo: document.body })
  await flushPromises()
  return wrapper
}
function canvasCard(id: string) { return wrapper.get(`.flow-probe [data-object-id="${id}"]`) }
async function refreshTopology() {
  mocks.listener!({ session_id: c.selectedSessionId.value, type: 'run.completed' } as EventEnvelope)
  await vi.advanceTimersByTimeAsync(800)
  await flushPromises()
}
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(() =>
    ({ x: 0, y: 0, left: 0, top: 0, right: 1280, bottom: 800, width: 1280, height: 800, toJSON: () => ({}) }))
  mocks.subscribe.mockImplementation((listener: typeof mocks.listener) => { mocks.listener = listener; return mocks.unsubscribe })
  mocks.topology.mockResolvedValue(topology())
  mocks.setViewport.mockResolvedValue(undefined)
  c.selectedSessionId.value = 's'
  c.messages.value = [message('r1', 'Build the feature'), message('r2', 'Review the feature')]
  c.queuedMessages.value = []
  c.approvals.value = []
  c.orchestration.value = null
  streamingReply.value = ''
  streamingReplies.value = {}
  stoppableRunId.value = 'r1'
  turns.value = { r1: { runId: 'r1', toolCalls: [tool] } }
  __resetUieForTests()
  const registry = createCardRegistry()
  const empty = defineComponent({ render: () => h('div') })
  for (const type of ['nav', 'chat', 'homePicker', 'git', 'approval', 'orchestration', 'events', 'doctor', 'browser', 'agent', 'terminal', 'spatialWork']) {
    registry.register({ type, component: type === 'spatialWork' ? SpatialWorkCard : type === 'terminal' ? TerminalProbe : empty,
      minWidth: 100, minHeight: 100, singleton: type !== 'spatialWork', movable: true, closable: true, detachable: false, defaultTitle: type })
  }
  uie = initUie({ registry, componentFor: id => registry.get(id)?.component })
  dispatch = vi.spyOn(uie, 'dispatch')
})
afterEach(() => {
  wrapper?.unmount()
  __resetUieForTests()
  vi.restoreAllMocks()
  vi.useRealTimers()
})

describe('SpatialPage runtime workspace', () => {
  it('selects a run, shows only its task summaries, and labels real dependency edges', async () => {
    await render()
    const select = wrapper.get('.space-goal-select')
    expect(select.text()).toContain('Build the feature')
    await select.trigger('click')
    expect(wrapper.text()).toContain('Build the feature')
    expect(wrapper.text()).toContain('Review the feature')
    await wrapper.findAll('.space-select-option')[1].trigger('click')
    expect(wrapper.findAll('.edge-probe').map(edge => [edge.attributes('data-source'), edge.attributes('data-target'), edge.text()])).toEqual([
      ['task:r1:a', 'task:r1:c', 'space.cluster.requires'], ['task:r1:b', 'task:r1:c', 'space.cluster.requires'],
    ])
    expect(canvasCard('task:r1:a').text()).toContain('worker#a')
    expect(canvasCard('task:r1:a').isVisible()).toBe(true)
    await select.trigger('click')
    await wrapper.findAll('.space-select-option')[2].trigger('click')
    await flushPromises()
    expect(canvasCard('task:r1:a').isVisible()).toBe(false)
    expect(canvasCard('task:r2:other').isVisible()).toBe(true)
    expect(wrapper.findAll('.edge-probe')).toHaveLength(0)
    await select.trigger('click')
    await wrapper.find('.space-select-option').trigger('click')
    expect(wrapper.findAll('.edge-probe')).toHaveLength(2)
    await wrapper.get('.space-relation-toggle [role="checkbox"]').trigger('click')
    expect(wrapper.findAll('.edge-probe')).toHaveLength(0)
  })

  it('does not move the camera or existing cards on array replacement, new objects or status refresh', async () => {
    await render()
    const viewport = { x: -210, y: 315, zoom: 0.64 }
    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('viewport-change-end', viewport)
    await flushPromises()
    const original = { ...uie.snapshot.value.space!.items['task:r1:a'] }
    mocks.setViewport.mockClear()
    dispatch.mockClear()
    c.messages.value = [...c.messages.value]
    c.queuedMessages.value = [{ id: 'queued', content: 'Another request' }]
    const updated = topology()
    updated.runs[0].tasks[0].status = 'completed'
    updated.runs[0].tasks.push(task('new'))
    mocks.topology.mockResolvedValueOnce(updated)
    await refreshTopology()
    expect(canvasCard('task:r1:a').text()).toContain('space.status.completed')
    expect(canvasCard('task:r1:new').isVisible()).toBe(true)
    expect(uie.snapshot.value.space!.items['task:r1:a']).toEqual(original)
    expect(uie.snapshot.value.space!.viewport).toEqual(viewport)
    expect(mocks.setViewport).not.toHaveBeenCalled()
    expect(dispatch.mock.calls.some(([envelope]) => envelope.command.type === 'spaceViewport')).toBe(false)
  })

  it('retains old projection on a failed refresh, exposes stale state and recovers on retry', async () => {
    await render()
    const taskElement = canvasCard('task:r1:a').element
    mocks.topology.mockRejectedValueOnce(new Error('network offline'))
    await refreshTopology()
    expect(wrapper.get('.space-data-status').text()).toContain('space.cluster.stale: network offline')
    expect(canvasCard('task:r1:a').element).toBe(taskElement)
    expect(wrapper.findAll('.edge-probe')).toHaveLength(2)
    await wrapper.get('.space-data-status button').trigger('click')
    await flushPromises()
    expect(wrapper.get('.space-data-status').text()).not.toContain('space.cluster.stale')
    expect(canvasCard('task:r1:a').element).toBe(taskElement)
  })

  it('discards a late previous-session response even if its transport ignores cancellation', async () => {
    const old = deferred<SessionTopologyDto>()
    mocks.topology.mockReturnValueOnce(old.promise)
    await render()
    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('node-drag-start')
    const signal = mocks.topology.mock.calls[0][2] as AbortSignal
    const next = { ...topology('next'), runs: [run('new-run', [task('new-session')])] }
    mocks.topology.mockResolvedValueOnce(next)
    c.messages.value = [message('new-run', 'New session request', 'next')]
    c.selectedSessionId.value = 'next'
    await flushPromises()
    expect(signal.aborted).toBe(true)
    expect(canvasCard('task:new-run:new-session').isVisible()).toBe(true)
    old.resolve(topology())
    await flushPromises()
    expect(wrapper.find('[data-object-id="task:r1:a"]').exists()).toBe(false)
    expect(canvasCard('task:new-run:new-session').isVisible()).toBe(true)
    expect(wrapper.get('.space-goal-select').text()).not.toContain('Build the feature')
    expect(uie.snapshot.value.space!.sessionId).toBe('next')
    expect(wrapper.get('.space-data-status').text()).not.toContain('space.cluster.stale')
  })

  it('reuses visited task detail instances and retained Git reads across close and reopen', async () => {
    await render()
    expect(wrapper.findAll('.space-detail .is-detail')).toHaveLength(0)
    await canvasCard('task:r1:c').get('.space-open-detail').trigger('click')
    const detail = wrapper.get('.space-detail')
    const card = detail.get('[data-object-id="task:r1:c"]')
    const element = card.element
    expect(detail.isVisible()).toBe(true)
    expect(detail.findAll('.space-relation-record')).toHaveLength(2)
    await detail.get('[aria-label="space.cluster.closeDetails"]').trigger('click')
    expect(detail.isVisible()).toBe(false)
    expect(card.element).toBe(element)
    await canvasCard('task:r1:a').get('.space-open-detail').trigger('click')
    expect(card.isVisible()).toBe(false)
    await canvasCard('task:r1:c').get('.space-open-detail').trigger('click')
    expect(card.isVisible()).toBe(true)
    expect(detail.get('[data-object-id="task:r1:c"]').element).toBe(element)

    mocks.readFile.mockResolvedValueOnce({ status: 'completed', summary: '', data: { all_contents: [{ content: { Content: 'retained file contents' } }] } })
    await canvasCard('git:s').get('.space-open-detail').trigger('click')
    await detail.get('.space-git-file button').trigger('click')
    await flushPromises()
    const file = detail.get('.space-file-view')
    expect(file.text()).toContain('retained file contents')
    await detail.get('[aria-label="space.cluster.closeDetails"]').trigger('click')
    await canvasCard('git:s').get('.space-open-detail').trigger('click')
    expect(detail.get('.space-file-view').element).toBe(file.element)
    expect(detail.get('.space-file-view').text()).toContain('retained file contents')
    expect(mocks.readFile).toHaveBeenCalledOnce()
  })

  it('arranges only the selected run using one spaceMove and preserves other groups', async () => {
    await render()
    const goalSelect = wrapper.get('.space-goal-select')
    await goalSelect.trigger('click')
    await wrapper.findAll('.space-select-option')[2].trigger('click')
    await flushPromises()
    const outside = Object.values(uie.snapshot.value.space!.items).filter(item => item.groupId !== 'r2')
    dispatch.mockClear()
    await wrapper.get('[title="space.cluster.arrangeHint"]').trigger('click')
    await flushPromises()
    const moves = dispatch.mock.calls.map(([envelope]) => envelope.command).filter(command => command.type === 'spaceMove')
    expect(moves).toHaveLength(1)
    expect(moves[0]).toMatchObject({ changes: expect.arrayContaining([expect.objectContaining({ id: 'tasks:r2' }), expect.objectContaining({ id: 'task:r2:other' })]) })
    if (moves[0].type !== 'spaceMove') throw new Error('Expected a spaceMove command')
    expect(moves[0].changes).toHaveLength(2)
    for (const item of outside) expect(uie.snapshot.value.space!.items[item.id]).toEqual(item)
    await goalSelect.trigger('click')
    await wrapper.find('.space-select-option').trigger('click')
    expect(wrapper.get('[title="space.cluster.arrangeHint"]').attributes('disabled')).toBeDefined()
  })

  it('opens the terminal through the sole UIE right-column host and reuses it after hiding', async () => {
    await render()
    expect(wrapper.find('[data-column="right"]').exists()).toBe(false)
    expect(mocks.terminalCreated).not.toHaveBeenCalled()
    await canvasCard('tool:r1:shell-1').get('.space-open-detail').trigger('click')
    await wrapper.get('.space-detail .open-terminal').trigger('click')
    await flushPromises()
    expect(wrapper.get('.space-detail').isVisible()).toBe(false)
    expect(wrapper.findAll('[data-terminal-host]')).toHaveLength(1)
    const terminal = wrapper.get('[data-column="right"] [data-terminal-host]')
    const element = terminal.element
    await terminal.trigger('click')
    expect(terminal.text()).toBe('1')
    await wrapper.get('.space-hide-terminal').trigger('click')
    expect(terminal.isVisible()).toBe(false)
    await canvasCard('tool:r1:shell-1').get('.space-open-detail').trigger('click')
    await wrapper.get('.space-detail .open-terminal').trigger('click')
    expect(wrapper.findAll('[data-terminal-host]')).toHaveLength(1)
    expect(wrapper.get('[data-terminal-host]').element).toBe(element)
    expect(wrapper.get('[data-terminal-host]').text()).toBe('1')
    expect(wrapper.get('[data-terminal-host]').isVisible()).toBe(true)
    expect(mocks.terminalCreated).toHaveBeenCalledOnce()
    expect(Object.values(uie.snapshot.value.cards).filter(card => card.descriptorId === 'terminal')).toHaveLength(1)
    expect(dispatch.mock.calls.filter(([envelope]) => envelope.command.type === 'openCard')).toHaveLength(2)
  })
})

