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
  topology: vi.fn(),
  readFile: vi.fn(),
  subscribe: vi.fn(),
  unsubscribe: vi.fn(),
  setViewport: vi.fn(),
  applyNodeChanges: vi.fn(),
  updateNodeInternals: vi.fn(),
  listener: undefined as ((event: EventEnvelope) => unknown) | undefined,
  terminalCreated: vi.fn(),
  pickHostFiles: vi.fn(),
  sendMessage: vi.fn(),
  handleWelcomeSend: vi.fn(),
  updateDraft: vi.fn(),
  openDocument: vi.fn(),
  openInEditor: vi.fn(),
  liveDigestRefresh: vi.fn(),
  liveDigestSnapshots: null as Ref<Record<string, import('@/composables/useLiveDigest').LiveDigestSnapshotDto | null>> | null,
  listRunTeammates: vi.fn(),
  steerRunTeammate: vi.fn(),
  pauseRunTeammate: vi.fn(),
  resumeRunTeammate: vi.fn(),
}))

vi.mock('@/controllers/HomeController', async () => {
  const { ref } = await import('vue')
  const draft = ref('')
  mocks.updateDraft.mockImplementation((v: string) => { draft.value = v })
  return {
    homeController: {
      viewMode: ref('space'),
      selectedSessionId: ref<string | null>('s'),
      selectedProjectId: ref(null),
      currentSession: ref(null),
      currentProject: ref(null),
      projects: ref([]),
      runs: ref([]),
      messages: ref<MessageDto[]>([]),
      queuedMessages: ref([]),
      approvals: ref<ApprovalDto[]>([]),
      approvalRules: ref([]),
      orchestration: ref<OrchestrationSnapshotDto | null>(null),
      agentTurnActivities: ref<Record<string, TurnActivity>>({}),
      streamingReply: ref(''),
      streamingReplies: ref<Record<string, string>>({}),
      stoppableRunId: ref<string | null>('r1'),
      draft,
      shellCommand: ref(''),
      currentPermission: ref('ask'),
      busy: ref(false),
      working: ref(false),
      composerSettings: ref({ mode_version_id: null, meeting_model_override: null, permission_mode: 'default', space_options: null }),
      settingsSaving: ref(false),
      settingsError: ref(null),
      updateComposerSettings: vi.fn(),
      updateDraft: mocks.updateDraft,
      updatePermission: vi.fn(),
      sendMessage: mocks.sendMessage,
      handleWelcomeSend: mocks.handleWelcomeSend,
      stopRun: vi.fn(),
      openProject: vi.fn(),
      setSelectedProject: vi.fn(),
      setViewMode: vi.fn(),
      start: vi.fn(),
    },
  }
})
vi.mock('@/api', () => ({ api: { getSessionTopology: mocks.topology, readFile: mocks.readFile } }))
vi.mock('@/lib/sessionEventBus', () => ({ subscribeToSessionEvents: mocks.subscribe }))
vi.mock('@/lib/hostConnection', () => ({
  pickHostFiles: mocks.pickHostFiles,
  resolveWebGatewayBase: () => 'http://127.0.0.1:48730',
  toWebSocketUrl: (base: string, p: string) => base.replace(/^http/, 'ws') + p,
}))
vi.mock('@/lib/documentWorkspace', () => ({
  openDocument: mocks.openDocument,
  openInEditor: mocks.openInEditor,
}))
vi.mock('@/lib/uiEngine', async () => {
  const { useUie } = await import('../../../TinadecUI/src/components/useUie')
  return { ensureProductionUie: useUie }
})
vi.mock('@/composables/useSpatialGit', async () => {
  const { ref } = await import('vue')
  return {
    useSpatialGit: () => ({
      state: ref({
        cwd: '/workspace',
        loaded: true,
        loading: false,
        error: '',
        commits: [],
        preview: { branch: 'feature', files: [{ path: 'src/task.ts' }] },
      }),
      refresh: vi.fn(),
    }),
  }
})
vi.mock('@/composables/usePanelStyles', () => ({
  usePanelStyles: () => ({
    getPanelStyle: () => ({}),
    getPanelDataAttributes: () => ({}),
  }),
}))
vi.mock('@/composables/useLiveDigest', async () => {
  const { computed, ref, toValue } = await import('vue')
  const snapshots = ref<Record<string, import('@/composables/useLiveDigest').LiveDigestSnapshotDto | null>>({})
  mocks.liveDigestSnapshots = snapshots
  return {
    selectPrimaryRunId: (runs: ReadonlyArray<{ id: string }> | null | undefined) => runs?.[0]?.id ?? null,
    useLiveDigest: (runIdSource: import('vue').MaybeRefOrGetter<string | null | undefined>) => {
      const runId = computed(() => {
        const raw = toValue(runIdSource)
        return typeof raw === 'string' && raw.trim() ? raw.trim() : null
      })
      const snapshot = computed(() => (runId.value ? snapshots.value[runId.value] ?? null : null))
      const isTerminal = computed(() => ['completed', 'failed', 'cancelled'].includes(snapshot.value?.status ?? ''))
      const isRunning = computed(() => Boolean(snapshot.value && !isTerminal.value))
      const teammateThoughts = computed(() => snapshot.value?.teammate_thoughts ?? [])
      const teammateThoughtByHandle = computed(() => {
        const map: Record<string, import('@/composables/useLiveDigest').LiveDigestTeammateThoughtDto> = {}
        for (const item of teammateThoughts.value) {
          if (item.handle) map[item.handle] = item
        }
        return map
      })
      return {
        runId,
        snapshot,
        loading: computed(() => false),
        connected: computed(() => isRunning.value),
        streaming: computed(() => isRunning.value),
        connectionState: computed(() => (isRunning.value ? 'live' : 'idle')),
        error: computed(() => null),
        isRunning,
        isTerminal,
        headline: computed(() => snapshot.value?.headline ?? ''),
        currentPhase: computed(() => snapshot.value?.current_phase ?? null),
        mode: computed(() => snapshot.value?.mode ?? 'auto'),
        explainerActive: computed(() => Boolean(snapshot.value?.explainer_active)),
        explainerModel: computed(() => snapshot.value?.explainer_model ?? null),
        contextWaterline: computed(() => snapshot.value?.context_waterline ?? null),
        governanceSignals: computed(() => snapshot.value?.governance_signals ?? []),
        teammateThoughts,
        teammateThoughtByHandle,
        chapters: computed(() => snapshot.value?.chapters ?? []),
        touchedFiles: computed(() => snapshot.value?.touched_files ?? []),
        sentinelAlerts: computed(() => snapshot.value?.sentinel_alerts ?? []),
        lastEventType: computed(() => null),
        refresh: mocks.liveDigestRefresh,
        reconnect: vi.fn(),
        disconnect: vi.fn(),
      }
    },
  }
})
vi.mock('@/lib/teammateControlApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/teammateControlApi')>()
  return {
    ...actual,
    listRunTeammates: mocks.listRunTeammates,
    steerRunTeammate: mocks.steerRunTeammate,
    pauseRunTeammate: mocks.pauseRunTeammate,
    resumeRunTeammate: mocks.resumeRunTeammate,
  }
})
vi.mock('vue-i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({
    t: (key: string, params?: Record<string, unknown>) => [key, ...Object.values(params ?? {})].join(' '),
    te: () => true,
    locale: ref('en'),
  }),
}))
vi.mock('@/components/ui', async () => ({
  UiButton: { template: '<button><slot /></button>' },
  UiCheckbox: {
    props: ['modelValue'],
    emits: ['update:modelValue'],
    template: '<button role="checkbox" :aria-checked="modelValue" @click="$emit(\'update:modelValue\', !modelValue)" />',
  },
  UiSelect: (await import('../components/ui/select.vue')).default,
}))
vi.mock('@/components/ComposerBar.vue', () => ({
  default: { template: '<div class="composer-box"><textarea data-testid="composer-textarea" /></div>' },
}))
vi.mock('@/components/plan/SessionPlanPanel.vue', () => ({
  default: { props: ['sessionId'], template: '<div data-testid="session-plan-probe">Plan {{ sessionId }}</div>' },
}))
vi.mock('@/components/ApprovalTab.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/components/chat/TurnTimeline.vue', () => ({ default: { template: '<div />' } }))
vi.mock('@/components/chat/ToolCallCard.vue', () => ({
  default: defineComponent({
    setup() {
      const open = inject<() => void>('space:open-terminal')
      return () => h('button', { class: 'open-terminal', onClick: open }, 'Open terminal')
    },
  }),
}))
vi.mock('@tinadec/ui', async () => {
  const spatial = await import('../../../TinadecUI/src/engine/spatial')
  const routing = await import('../../../TinadecUI/src/engine/spatialRouting')
  const smartGuides = await import('../../../TinadecUI/src/engine/spatialSmartGuides')
  const doc = await import('../../../TinadecUI/src/engine/spatialDocument')
  const frac = await import('../../../TinadecUI/src/engine/spatialFractionalIndex')
  return {
    UieShell: { template: '<main><slot /></main>' },
    UieCanvas: (await import('../../../TinadecUI/src/components/UieCanvas.vue')).default,
    UieCardHost: (await import('../../../TinadecUI/src/components/UieCardHost.vue')).default,
    arrangeSpace: spatial.arrangeSpace,
    routeSpatialEdges: routing.routeSpatialEdges,
    ...smartGuides,
    ...doc,
    ...frac,
  }
})
vi.mock('../../../TinadecUI/src/components/UieColumn.vue', async () => {
  const { useUie } = await import('../../../TinadecUI/src/components/useUie')
  const Host = (await import('../../../TinadecUI/src/components/UieCardHost.vue')).default
  return {
    default: defineComponent({
      props: { column: { type: Object as PropType<UieColumn>, required: true } },
      setup(props) {
        const uie = useUie()
        return () =>
          h(
            'section',
            { 'data-column': props.column.slotId },
            props.column.primary.tabIds.map((id) =>
              h(Host, { key: id, instance: uie.snapshot.value.cards[id], active: props.column.primary.activeTabId === id }),
            ),
          )
      },
    }),
  }
})
vi.mock('@vue-flow/core', () => ({
  MarkerType: { ArrowClosed: 'arrowclosed' },
  Position: { Top: 'top', Bottom: 'bottom', Left: 'left', Right: 'right' },
  SelectionMode: { Partial: 'partial', Full: 'full' },
  Handle: { template: '<span />' },
  BaseEdge: { template: '<path />' },
  EdgeLabelRenderer: { template: '<div><slot /></div>' },
  getBezierPath: () => ['M 0 0 L 10 10', 5, 5],
  getSmoothStepPath: () => ['M 0 0 L 10 10', 5, 5],
  useVueFlow: () => ({
    setViewport: mocks.setViewport,
    applyNodeChanges: mocks.applyNodeChanges,
    updateNodeInternals: mocks.updateNodeInternals,
    zoomIn: vi.fn(),
    zoomOut: vi.fn(),
  }),
  VueFlow: {
    name: 'VueFlow',
    props: ['nodes', 'edges', 'onlyRenderVisibleElements', 'panOnDrag', 'selectionKeyCode'],
    emits: ['viewport-change', 'viewport-change-end', 'edge-click', 'node-drag-start', 'node-drag', 'node-drag-stop', 'node-click', 'pane-click', 'nodes-change', 'connect'],
    template: `<div class="flow-probe" :data-only-visible="String(onlyRenderVisibleElements)" :data-pan-on-drag="String(panOnDrag)">
      <div v-for="node in nodes" :key="node.id" :data-node="node.id" :data-node-type="node.type" :style="node.style">
        <slot v-if="node.type === 'work'" name="node-work" :id="node.id" :selected="Boolean(node.selected)" />
        <slot v-else-if="node.type === 'section'" name="node-section" :id="node.id" :data="node.data" :selected="Boolean(node.selected)" />
        <slot v-else-if="node.type === 'sticky'" name="node-sticky" :id="node.id" :data="node.data" :selected="Boolean(node.selected)" />
        <slot v-else-if="node.type === 'text'" name="node-text" :id="node.id" :data="node.data" :selected="Boolean(node.selected)" />
        <slot v-else-if="node.type === 'media'" name="node-media" :id="node.id" :data="node.data" :selected="Boolean(node.selected)" />
      </div>
      <span v-for="edge in edges" :key="edge.id" class="edge-probe" :data-edge-type="edge.type" :data-source="edge.source" :data-target="edge.target">{{ edge.label }}</span>
      <slot />
    </div>`,
  },
}))
vi.mock('@vue-flow/background', () => ({ Background: { template: '<span />' } }))
vi.mock('@vue-flow/minimap', () => ({ MiniMap: { template: '<div class="minimap-probe" />' } }))
vi.mock('@vue-flow/node-resizer', () => ({ NodeResizer: { template: '<span />' } }))

const message = (run_id: string, content: string, session_id = 's'): MessageDto => ({
  id: `message-${run_id}`,
  session_id,
  role: 'user',
  run_id,
  content,
  created_at: '',
  attachments: [],
})
const task = (task_id: string, extra: Partial<TopologyTaskDto> = {}): TopologyTaskDto => ({
  task_id,
  task_key: task_id,
  title: `Task ${task_id}`,
  status: 'running',
  dependencies: [],
  write_scope: [],
  worker_instance_id: `instance-${task_id}`,
  handle: `worker#${task_id}`,
  result_summary: `Result ${task_id}`,
  ...extra,
})
const run = (run_id: string, tasks: TopologyTaskDto[]): TopologyRunDto => ({
  run_id,
  status: 'running',
  tasks,
  instances: [],
  tasks_truncated: false,
  instances_truncated: false,
})
function topology(session_id = 's'): SessionTopologyDto {
  return {
    session_id,
    runs: [
      run('r1', [task('a'), task('b'), task('c', { dependencies: ['a', 'b'] })]),
      run('r2', [task('other')]),
    ],
    leases: [],
    members: [],
    runs_truncated: false,
    leases_truncated: false,
    members_truncated: false,
    generated_at: '2026-10-06T12:00:00Z',
  }
}
const tool: ToolCall = {
  id: 'shell-1',
  toolId: 'shell',
  toolName: 'Run tests',
  runId: 'r1',
  status: 'completed',
  startedAt: null,
  completedAt: null,
  durationMs: null,
  argsSummary: 'npm test',
  resultSummary: null,
  requiresApproval: false,
  approvalId: null,
  evidence: [],
  seq: 1,
  risk: 'medium',
}
const turns = c.agentTurnActivities as Ref<Record<string, TurnActivity>>
const stoppableRunId = c.stoppableRunId as Ref<string | null>
const streamingReply = c.streamingReply as Ref<string>
const streamingReplies = c.streamingReplies as Ref<Record<string, string>>
const TerminalProbe = defineComponent({
  setup() {
    mocks.terminalCreated()
    const count = ref(0)
    return () => h('button', { 'data-terminal-host': '', onClick: () => count.value++ }, String(count.value))
  },
})

let uie: UieStore
let wrapper: VueWrapper
let dispatch: MockInstance<UieStore['dispatch']>

async function render() {
  wrapper = mount(SpatialPage, { attachTo: document.body })
  await flushPromises()
  return wrapper
}
function canvasCard(id: string) {
  return wrapper.get(`.flow-probe [data-object-id="${id}"]`)
}
async function openZoomMenu() {
  if (!wrapper.find('[data-testid="space-zoom-menu"]').exists()) {
    await wrapper.get('[data-testid="space-zoom-trigger"]').trigger('click')
  }
}
async function refreshTopology() {
  mocks.listener!({ session_id: c.selectedSessionId.value, type: 'run.completed' } as EventEnvelope)
  await vi.advanceTimersByTimeAsync(800)
  await flushPromises()
}
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => { resolve = done })
  return { promise, resolve }
}

beforeEach(() => {
  vi.clearAllMocks()
  window.localStorage?.clear?.()
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(() => ({
    x: 0,
    y: 0,
    left: 0,
    top: 0,
    right: 1280,
    bottom: 800,
    width: 1280,
    height: 800,
    toJSON: () => ({}),
  }))
  mocks.subscribe.mockImplementation((listener: typeof mocks.listener) => {
    mocks.listener = listener
    return mocks.unsubscribe
  })
  mocks.topology.mockResolvedValue(topology())
  mocks.setViewport.mockResolvedValue(undefined)
  mocks.pickHostFiles.mockResolvedValue([])
  mocks.listRunTeammates.mockResolvedValue([])
  mocks.steerRunTeammate.mockResolvedValue({
    accepted: true,
    run_id: 'r1',
    handle: 'worker#a',
    status: 'working',
    paused: false,
    urgent: true,
    woken: false,
    delivery_mode: 'injected_next_turn',
    pending_steer_count: 1,
  })
  mocks.pauseRunTeammate.mockResolvedValue({
    accepted: true,
    run_id: 'r1',
    handle: 'worker#a',
    action: 'pause',
    status: 'paused',
    paused: true,
  })
  mocks.resumeRunTeammate.mockResolvedValue({
    accepted: true,
    run_id: 'r1',
    handle: 'worker#a',
    action: 'resume',
    status: 'working',
    paused: false,
  })
  if (mocks.liveDigestSnapshots) mocks.liveDigestSnapshots.value = {}
  c.draft.value = ''
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
    registry.register({
      type,
      component: type === 'spatialWork' ? SpatialWorkCard : type === 'terminal' ? TerminalProbe : empty,
      minWidth: 100,
      minHeight: 100,
      singleton: type !== 'spatialWork',
      movable: true,
      closable: true,
      detachable: false,
      defaultTitle: type,
    })
  }
  uie = initUie({ registry, componentFor: (id) => registry.get(id)?.component })
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
    expect(wrapper.findAll('.edge-probe').map((edge) => [edge.attributes('data-source'), edge.attributes('data-target'), edge.text()])).toEqual([
      ['task:r1:a', 'task:r1:c', 'space.cluster.requires'],
      ['task:r1:b', 'task:r1:c', 'space.cluster.requires'],
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
    await openZoomMenu()
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
    const inspector = wrapper.get('[data-testid="space-inspector"]')
    const detail = wrapper.get('.space-detail')
    const card = detail.get('[data-object-id="task:r1:c"]')
    const element = card.element
    expect(inspector.isVisible()).toBe(true)
    expect(detail.findAll('.space-relation-record')).toHaveLength(2)
    await inspector.get('[aria-label="space.cluster.closeDetails"]').trigger('click')
    expect(inspector.isVisible()).toBe(false)
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
    await inspector.get('[aria-label="space.cluster.closeDetails"]').trigger('click')
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
    const outside = Object.values(uie.snapshot.value.space!.items).filter((item) => item.groupId !== 'r2')
    dispatch.mockClear()
    await openZoomMenu()
    await wrapper.get('[title="space.cluster.arrangeHint"]').trigger('click')
    await flushPromises()
    const moves = dispatch.mock.calls.map(([envelope]) => envelope.command).filter((command) => command.type === 'spaceMove')
    expect(moves).toHaveLength(1)
    expect(moves[0]).toMatchObject({ changes: expect.arrayContaining([expect.objectContaining({ id: 'tasks:r2' }), expect.objectContaining({ id: 'task:r2:other' })]) })
    if (moves[0].type !== 'spaceMove') throw new Error('Expected a spaceMove command')
    expect(moves[0].changes).toHaveLength(2)
    for (const item of outside) expect(uie.snapshot.value.space!.items[item.id]).toEqual(item)
    await goalSelect.trigger('click')
    await wrapper.find('.space-select-option').trigger('click')
    await openZoomMenu()
    expect(wrapper.get('[title="space.cluster.arrangeHint"]').attributes('disabled')).toBeDefined()
  })

  it('opens the terminal through the sole UIE right-column host and reuses it after hiding', async () => {
    await render()
    expect(wrapper.find('[data-column="right"]').exists()).toBe(false)
    expect(mocks.terminalCreated).not.toHaveBeenCalled()
    await canvasCard('tool:r1:shell-1').get('.space-open-detail').trigger('click')
    await wrapper.get('.space-detail .open-terminal').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="space-inspector"]').isVisible()).toBe(false)
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
    expect(Object.values(uie.snapshot.value.cards).filter((card) => card.descriptorId === 'terminal')).toHaveLength(1)
    expect(dispatch.mock.calls.filter(([envelope]) => envelope.command.type === 'openCard')).toHaveLength(2)
  })
})

describe('SpatialPage Figma UI3 shell, whiteboard integration & resident composer', () => {
  it('keeps ComposerBar resident and expanded at bottom-center by default even on empty sessions, and collapses/re-expands cleanly via pill or / hotkey', async () => {
    c.messages.value = []
    await render()

    const dock = wrapper.get('[data-testid="space-composer-dock"]')
    expect(dock.classes()).not.toContain('is-collapsed')
    expect(dock.classes()).not.toContain('space-composer-hero')
    expect(wrapper.find('.composer-box').exists()).toBe(true)
    expect(wrapper.find('[data-testid="space-composer-pill"]').exists()).toBe(false)

    // Collapse to sleek floating pill
    await wrapper.get('[data-testid="space-composer-collapse"]').trigger('click')
    expect(dock.classes()).toContain('is-collapsed')
    const pill = wrapper.get('[data-testid="space-composer-pill"]')
    expect(pill.text()).toContain('space.figma.startChatPill')
    expect(wrapper.find('.composer-box').exists()).toBe(false)

    // Re-expand via `/` hotkey
    document.dispatchEvent(new KeyboardEvent('keydown', { key: '/' }))
    await flushPromises()
    expect(wrapper.find('.composer-box').exists()).toBe(true)
    expect(wrapper.find('[data-testid="space-composer-pill"]').exists()).toBe(false)
  })

  it('enables onlyRenderVisibleElements, switches semantic macro LOD below 0.55 zoom, and toggles minimap with M', async () => {
    await render()
    const flowProbe = wrapper.get('.flow-probe')
    expect(flowProbe.attributes('data-only-visible')).toBe('true')

    // Initially normal LOD
    expect(canvasCard('task:r1:a').attributes('data-lod')).toBe('normal')

    // Zoom out below 0.55 -> semantic macro LOD
    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('viewport-change', { x: 0, y: 0, zoom: 0.42 })
    await flushPromises()
    expect(wrapper.get('.space-surface').classes()).toContain('is-macro-lod')
    expect(canvasCard('task:r1:a').attributes('data-lod')).toBe('macro')
    expect(wrapper.get('[data-testid="space-zoom-trigger"]').text()).toContain('42%')

    // Zoom back in -> normal LOD
    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('viewport-change', { x: 0, y: 0, zoom: 1 })
    await flushPromises()
    expect(canvasCard('task:r1:a').attributes('data-lod')).toBe('normal')

    // Minimap toggles with M
    expect(wrapper.find('.minimap-probe').exists()).toBe(false)
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'm' }))
    await flushPromises()
    expect(wrapper.find('.minimap-probe').exists()).toBe(true)
  })

  it('supports Left Tool Dock creation (Sticky, Section, Media via pickHostFiles), Layers outline, and Smart Guides during drag', async () => {
    mocks.pickHostFiles.mockResolvedValueOnce(['/workspace/diagram.png'])
    await render()

    // Select Sticky tool (N) -> choose mint color -> click pane to create
    await wrapper.get('[data-tool="sticky"]').trigger('click')
    expect(wrapper.find('[data-testid="space-sticky-popover"]').exists()).toBe(true)
    await wrapper.get('[data-sticky-preset="mint"]').trigger('click')
    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('pane-click', new MouseEvent('click', { clientX: 360, clientY: 280 }))
    await flushPromises()
    expect(wrapper.find('[data-node-type="sticky"]').exists()).toBe(true)

    // Select Section tool (S) -> click pane to create
    await wrapper.get('[data-tool="section"]').trigger('click')
    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('pane-click', new MouseEvent('click', { clientX: 500, clientY: 320 }))
    await flushPromises()
    expect(wrapper.find('[data-node-type="section"]').exists()).toBe(true)

    // Insert Image/File via unified #43 pickHostFiles (tool I)
    await wrapper.get('[data-tool="media"]').trigger('click')
    await flushPromises()
    expect(mocks.pickHostFiles).toHaveBeenCalledOnce()
    expect(wrapper.find('[data-node-type="media"]').exists()).toBe(true)

    // Layers drawer lists work cards & whiteboard nodes
    await wrapper.get('[data-testid="space-layers-toggle"]').trigger('click')
    const layers = wrapper.get('[data-testid="space-layers-drawer"]')
    expect(layers.find('[data-layer-id="task:r1:a"]').exists()).toBe(true)
    expect(layers.findAll('.space-layer-row').length).toBeGreaterThanOrEqual(4)

    // Real-time Smart Guides overlay renders when dragged node aligns with another node
    const targetItem = uie.snapshot.value.space!.items['task:r1:b']!
    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('node-drag', {
      node: {
        id: 'task:r1:a',
        position: { x: targetItem.x + 2, y: targetItem.y + 300 },
        dimensions: { width: targetItem.width, height: targetItem.height },
      },
      nodes: [{ id: 'task:r1:a' }],
    })
    await flushPromises()
    expect(wrapper.find('[data-testid="space-smart-guides"]').exists()).toBe(true)

    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('node-drag-stop', { nodes: [] })
    await flushPromises()
    expect(wrapper.find('[data-testid="space-smart-guides"]').exists()).toBe(false)
  })

  it('shows selection context strip above ComposerBar, dispatches to team, switches unified inspector tabs cleanly, and renders presence chips', async () => {
    await render()

    // Active AI teammate presence avatars appear in top-right island
    expect(wrapper.get('[data-testid="space-presence-avatars"]').findAll('.space-presence-chip').length).toBeGreaterThan(0)

    // Select a work card via VueFlow node-click
    wrapper.getComponent({ name: 'VueFlow' }).vm.$emit('node-click', {
      node: { id: 'task:r1:a' },
      event: new MouseEvent('click'),
    })
    await flushPromises()

    const strip = wrapper.get('[data-testid="space-selected-context-strip"]')
    expect(strip.text()).toContain('space.figma.selectedContextCount 1')

    // Click dispatch to team -> populates draft with selection context
    await wrapper.get('[data-testid="space-composer-dispatch"]').trigger('click')
    expect(mocks.updateDraft).toHaveBeenCalled()
    expect(c.draft.value).toContain('Task a')

    // Clear selection hides strip
    await wrapper.get('[data-testid="space-clear-selection"]').trigger('click')
    expect(wrapper.find('[data-testid="space-selected-context-strip"]').exists()).toBe(false)

    // Unified Smart Inspector switches cleanly between Details, Style, and Plan tabs in a single drawer
    await wrapper.get('[data-testid="space-plan-toggle"]').trigger('click')
    const inspector = wrapper.get('[data-testid="space-inspector"]')
    expect(inspector.isVisible()).toBe(true)
    expect(inspector.find('[data-testid="session-plan-probe"]').exists()).toBe(true)

    await inspector.get('[data-inspector-tab="style"]').trigger('click')
    expect(inspector.find('[data-testid="space-style-panel"]').exists()).toBe(true)
    expect(inspector.find('[data-testid="session-plan-probe"]').exists()).toBe(false)
  })

  it('places a Comment Pin via tool C and dispatches @AI review notes directly to the team', async () => {
    await render()

    await wrapper.get('[data-tool="comment"]').trigger('click')
    await wrapper.get('[data-testid="spatial-comments-layer"]').trigger('click', { clientX: 420, clientY: 260 })
    await flushPromises()

    await wrapper.get('[data-testid="comment-create-input"]').setValue('@AI please review the auth edge case')
    await wrapper.get('[data-testid="comment-create-popover"]').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[data-testid="comment-thread-popover"]').exists()).toBe(true)
    await wrapper.get('[data-testid="comment-dispatch-btn"]').trigger('click')
    await flushPromises()

    expect(mocks.sendMessage).toHaveBeenCalledOnce()
    expect(c.draft.value).toContain('@AI please review the auth edge case')
  })

  it('projects same-source Live Digest onto Run Section subtitle, AI cursor thought micro-bubble with direct steer/pause, and Inspector Live Digest tab', async () => {
    mocks.liveDigestSnapshots!.value = {
      r1: {
        run_id: 'r1',
        session_id: 's',
        status: 'running',
        mode: 'auto',
        explainer_active: true,
        explainer_model: 'gemini-2.5-flash',
        seq: 7,
        headline: 'Refactoring auth middleware & running checks',
        current_phase: 'verification',
        context_waterline: {
          used_tokens: 48000,
          max_tokens: 128000,
          usage_percent: 37.5,
          compaction_count: 1,
          compaction_points: [
            { seq: 3, before_tokens: 92000, after_tokens: 24000, occurred_at: '2026-11-23T02:20:00Z', summary: 'Compacted history' },
          ],
        },
        governance_signals: [
          {
            id: 'sig-1',
            code: 'SAME_ARGS_RETRY',
            severity: 'warning',
            summary: 'Repeated test invocation with identical args',
            occurred_at: '2026-11-23T02:22:00Z',
          },
        ],
        teammate_thoughts: [
          {
            handle: 'worker#a',
            display_name: 'worker#a',
            status: 'working',
            current_task: 'Task a',
            thought: 'Inspecting token refresh race condition',
            updated_at: '2026-11-23T02:23:00Z',
          },
        ],
        chapters: [
          {
            id: 'ch-1',
            index: 1,
            phase: 'verification',
            title: 'Verify token refresh lock',
            narrative: 'Checking concurrent refresh requests under load.',
            source: 'l1_narrator',
            raw_thought_excerpt: 'Lock contention appears in refreshToken()',
            scout_note: 'Two callers enter refresh concurrently.',
            tool_calls: [
              { tool_call_id: 'tc-1', tool_name: 'read_file', status: 'succeeded', compact_summary: 'Read src/auth.ts' },
            ],
            touched_files: [
              { path: 'src/auth.ts', added_lines: 18, removed_lines: 4 },
            ],
            started_at: '2026-11-23T02:21:00Z',
            updated_at: '2026-11-23T02:23:00Z',
          },
        ],
        touched_files: [{ path: 'src/auth.ts', added_lines: 18, removed_lines: 4 }],
        sentinel_alerts: [
          {
            id: 'alert-1',
            code: 'SAME_ARGS_RETRY',
            severity: 'warning',
            title: 'Repeated test retry',
            message: 'Same test command ran twice without edits.',
            suggested_steer_prompt: 'Inspect the failing assertion before rerunning tests.',
            teammate_handle: 'worker#a',
            created_at: '2026-11-23T02:23:30Z',
          },
        ],
        updated_at: '2026-11-23T02:23:30Z',
      },
    }

    await render()

    // 1. Run Section top live subtitle + context waterline micro-badge
    await openZoomMenu()
    await wrapper.get('[data-testid="space-menu-wrap-run"]').trigger('click')
    await flushPromises()

    const sectionDigest = wrapper.get('[data-testid="section-live-digest"]')
    expect(sectionDigest.get('[data-testid="section-live-headline"]').text()).toBe(
      'Refactoring auth middleware & running checks',
    )
    expect(sectionDigest.get('[data-testid="section-live-waterline"]').text()).toContain('38%')

    // Clicking the Run Section live digest strip opens the right Inspector directly on the Live Digest tab
    await sectionDigest.trigger('click')
    await flushPromises()
    const inspector = wrapper.get('[data-testid="space-inspector"]')
    expect(inspector.isVisible()).toBe(true)
    const digestPanel = inspector.get('[data-testid="space-live-digest-panel"]')
    expect(digestPanel.get('[data-testid="spatial-digest-headline"]').text()).toBe(
      'Refactoring auth middleware & running checks',
    )
    expect(digestPanel.get('[data-testid="spatial-digest-waterline"]').text()).toContain('38')
    expect(digestPanel.get('[data-testid="spatial-digest-waterline"]').text()).toContain('48.0k')
    expect(digestPanel.get('[data-testid="spatial-digest-waterline"]').text()).toContain('128.0k')
    expect(digestPanel.get('[data-testid="spatial-digest-signals"]').text()).toContain('SAME_ARGS_RETRY')
    expect(digestPanel.get('[data-testid="spatial-digest-chapters"]').text()).toContain('Verify token refresh lock')

    // 2. Sentinel alert [Fill steer prompt] fills the bottom ComposerBar draft
    await digestPanel.get('[data-testid="spatial-digest-fill-steer"]').trigger('click')
    await flushPromises()
    expect(c.draft.value).toBe('Inspect the failing assertion before rerunning tests.')

    // 3. AI Teammate cursor thought micro-bubble renders on SpatialPresenceLayer and opens TeammateSteerPopover on click
    const thoughtBubble = wrapper.get('[data-testid="spatial-thought-bubble"][data-thought-handle="worker#a"]')
    expect(thoughtBubble.text()).toContain('Inspecting token refresh race condition')

    await thoughtBubble.trigger('click')
    await flushPromises()

    const popover = document.body.querySelector('[data-testid="teammate-steer-popover"]') as HTMLElement | null
    expect(popover).not.toBeNull()
    expect(popover?.getAttribute('data-teammate-handle')).toBe('worker#a')

    // Send direct steer via Enter and independently pause teammate
    const input = popover!.querySelector('[data-testid="teammate-steer-input"]') as HTMLTextAreaElement
    input.value = 'Focus on refresh lock first'
    input.dispatchEvent(new Event('input'))
    await flushPromises()
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    await flushPromises()

    expect(mocks.steerRunTeammate).toHaveBeenCalledWith(
      'r1',
      'worker#a',
      { message: 'Focus on refresh lock first', urgent: true },
      { storageId: undefined },
    )

    const pauseBtn = popover!.querySelector('[data-testid="teammate-steer-pause-btn"]') as HTMLButtonElement
    pauseBtn.click()
    await flushPromises()
    expect(mocks.pauseRunTeammate).toHaveBeenCalledWith('r1', 'worker#a', { storageId: undefined })
  })
})
