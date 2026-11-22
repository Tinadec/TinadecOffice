// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { defineComponent, h, nextTick, ref, shallowRef } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import en from '@/locales/en'
import type { EventEnvelope, SessionTopologyDto } from '@/api'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import type { ToolCall } from '@/composables/useAgentActivity'
import { applyEvents, createFeedState } from '@/lib/conversation/reducer'
import {
  buildWorkerWorktreeGroups,
  isPathWithinRoot,
  splitUnifiedDiffByFile,
  summarizeWorktreeDiff,
  useSessionWorktrees,
  type WorktreeDiffSnapshot,
} from '@/composables/useSessionWorktrees'
import {
  activeDockFocus,
  clearDockWorkerFilter,
  focusDockCard,
  highlightedDockCard,
  parseToolDockInfo,
  resetDockSyncForTests,
} from '@/lib/homeDockSync'

const hoisted = vi.hoisted(() => ({
  executeCodeTool: vi.fn(),
  getSessionTopology: vi.fn(),
  listAgents: vi.fn(async () => [
    { id: 'eng', slug: 'global_engineering', display_name: 'Engineer' },
    { id: 'lead', slug: 'coordinator', display_name: 'Lead Coordinator' },
  ]),
  gitLog: vi.fn(),
  createUserToolAction: vi.fn(),
  uieDispatch: vi.fn(() => true),
  uieSnapshot: {
    pageId: 'home',
    revision: 3,
    focusedCardId: null as string | null,
    columns: {
      right: {
        slotId: 'right',
        collapsed: true,
      },
    },
    cards: {},
  },
  homeState: {
    currentProject: null as { value: { id: string; path: string; name: string } | null } | null,
    selectedSessionId: null as { value: string | null } | null,
    currentSession: null as { value: { id: string } | null } | null,
    runs: null as { value: Array<{ id: string; status: string }> } | null,
    approvals: null as { value: Array<Record<string, unknown>> } | null,
    orchestration: null as { value: Record<string, unknown> | null } | null,
    toolExecutions: null as { value: Array<Record<string, unknown>> } | null,
    messages: null as { value: Array<Record<string, unknown>> } | null,
    sessions: null as { value: Array<Record<string, unknown>> } | null,
    projects: null as { value: Array<Record<string, unknown>> } | null,
    conversationFeed: null as ConversationFeed | null,
    streamingReplies: null as { value: Record<string, string> } | null,
    agentActivity: null as { value: Record<string, unknown> } | null,
    agentStatesMap: null as { value: Record<string, unknown> } | null,
    agentThinkingSteps: null as { value: Array<Record<string, unknown>> } | null,
    agentProgressEvents: null as { value: Array<Record<string, unknown>> } | null,
    draft: null as { value: string } | null,
    busy: null as { value: boolean } | null,
    currentPermission: null as { value: string } | null,
    composerSettings: null as { value: Record<string, unknown> } | null,
    settingsSaving: null as { value: boolean } | null,
    settingsError: null as { value: string | null } | null,
    modelName: null as { value: string } | null,
    stoppableRunId: null as { value: string | null } | null,
    transcriptLoading: null as { value: boolean } | null,
    selectedProjectId: null as { value: string | null } | null,
  },
}))

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    createUserToolActionForPath: hoisted.createUserToolAction,
    api: {
      ...actual.api,
      executeCodeTool: hoisted.executeCodeTool,
      getSessionTopology: hoisted.getSessionTopology,
      listAgents: hoisted.listAgents,
      gitLog: hoisted.gitLog,
      invokeStream: vi.fn(() => new AbortController()),
      controlRun: vi.fn(),
      createInteraction: vi.fn(),
      getApprovalGates: vi.fn(async () => []),
      attachmentContentUrl: (id: string) => `/a/${id}`,
    },
  }
})

vi.mock('@/lib/uiEngine', async () => {
  const { ref: vueRef } = await import('vue')
  return {
    useUiePage: () => ({
      ready: Promise.resolve(),
      bus: { dispatch: hoisted.uieDispatch },
      scope: vueRef({ kind: 'page', pageId: 'home' }),
      snapshot: vueRef(hoisted.uieSnapshot),
      focusedCardId: vueRef(null),
    }),
  }
})

vi.mock('@tinadec/ui', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>()
  const { defineComponent: dc, h: vh, ref: vueRef } = await import('vue')
  return {
    ...actual,
    UieShell: dc({
      setup: () => () =>
        vh('div', { class: 'uie-slot-right' }, [
          vh('div', { class: 'uie-card-host' }, [
            vh('div', { 'data-dock-card': 'git' }, [
              vh('div', { 'data-worker-task-id': 'task-2' }),
            ]),
          ]),
        ]),
    }),
    useUie: () => ({
      bus: { dispatch: hoisted.uieDispatch },
      scope: vueRef({ kind: 'page', pageId: 'home' }),
      snapshot: vueRef(hoisted.uieSnapshot),
      focusedCardId: vueRef(null),
    }),
  }
})

vi.mock('@/controllers/HomeController', async () => {
  const { ref: vueRef, shallowRef: vueShallowRef } = await import('vue')
  const state = createFeedState()
  hoisted.homeState.currentProject = vueRef({ id: 'p-1', name: 'Repo', path: 'D:/repo' })
  hoisted.homeState.selectedSessionId = vueRef('session-1')
  hoisted.homeState.currentSession = vueRef({ id: 'session-1' })
  hoisted.homeState.runs = vueRef([{ id: 'run-1', status: 'executing' }])
  hoisted.homeState.approvals = vueRef([
    {
      id: 'app-1',
      session_id: 'session-1',
      run_id: 'run-1',
      kind: 'tool',
      summary: 'Approve write to src/worker.ts',
      command: null,
      cwd: 'D:/repo/.tinadec/worktrees/task-2',
      status: 'pending',
      created_at: '2026-11-22T04:00:00Z',
    },
  ])
  hoisted.homeState.orchestration = vueRef(null)
  hoisted.homeState.toolExecutions = vueRef([])
  hoisted.homeState.messages = vueRef([])
  hoisted.homeState.sessions = vueRef([])
  hoisted.homeState.projects = vueRef([hoisted.homeState.currentProject.value!])
  hoisted.homeState.conversationFeed = {
    state: vueShallowRef(state),
    revision: vueRef(1),
    hydrated: vueRef(true),
  }
  hoisted.homeState.streamingReplies = vueRef({})
  hoisted.homeState.agentActivity = vueRef({
    status: 'working',
    runId: 'run-1',
    runStartedAt: null,
    runSummary: null,
    activeAgentName: 'Lead',
    activeAgentRole: 'coordinator',
    completedNodes: 0,
    totalNodes: 1,
    lastUpdated: null,
  })
  hoisted.homeState.agentStatesMap = vueRef({
    lead: {
      agentId: 'lead',
      agentName: 'engineer#1',
      agentLayer: 'execution',
      agentType: 'global_engineering',
      status: 'active',
      lastActiveAt: null,
      currentTask: 'Implement API fix',
    },
  })
  hoisted.homeState.agentThinkingSteps = vueRef([])
  hoisted.homeState.agentProgressEvents = vueRef([])
  hoisted.homeState.draft = vueRef('')
  hoisted.homeState.busy = vueRef(false)
  hoisted.homeState.currentPermission = vueRef('default')
  hoisted.homeState.composerSettings = vueRef({
    mode_version_id: null,
    permission_mode: 'default',
    meeting_model_override: null,
    space_options: null,
  })
  hoisted.homeState.settingsSaving = vueRef(false)
  hoisted.homeState.settingsError = vueRef(null)
  hoisted.homeState.modelName = vueRef('gpt-5')
  hoisted.homeState.stoppableRunId = vueRef('run-1')
  hoisted.homeState.transcriptLoading = vueRef(false)
  hoisted.homeState.selectedProjectId = vueRef('p-1')

  return {
    homeController: {
      ...hoisted.homeState,
      viewMode: vueRef('flat'),
      start: vi.fn(),
      setViewMode: vi.fn(),
      refreshProjectsAndSessions: vi.fn(),
      decideApproval: vi.fn(),
      decideApprovalById: vi.fn(),
      recordApproval: vi.fn(),
      updateDraft: vi.fn(),
      updatePermission: vi.fn(),
      updateComposerSettings: vi.fn(),
      sendMessage: vi.fn(),
      handleWelcomeSend: vi.fn(),
      openProject: vi.fn(),
      setSelectedProject: vi.fn(),
      editAndResend: vi.fn(),
      stopRun: vi.fn(),
    },
  }
})

vi.mock('@/composables/useHomeEntrance', async () => {
  const { ref: vueRef } = await import('vue')
  return { useHomeEntrance: () => ({ contentReady: vueRef(true), phase: vueRef('ready') }) }
})

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn() }),
  useRoute: () => ({ path: '/', query: {}, params: {} }),
  onBeforeRouteLeave: vi.fn(),
}))

const { default: GitPanel } = await import('./GitPanel.vue')
const { default: SubAgentsPanel } = await import('./conversation/SubAgentsPanel.vue')
const { default: ToolCallCard } = await import('./chat/ToolCallCard.vue')
const { default: TurnTimeline } = await import('./chat/TurnTimeline.vue')
const { default: AgentActivityBanner } = await import('./chat/AgentActivityBanner.vue')
const { default: ChatCard } = await import('../../../TinadecUI/src/components/cards/home/ChatCard.vue')
const { default: WorkspaceCard } = await import('../../../TinadecUI/src/components/cards/home/WorkspaceCard.vue')
const { default: HomePage } = await import('../pages/HomePage.vue')

const i18n = createI18n({
  legacy: false,
  locale: 'en',
  missingWarn: false,
  fallbackWarn: false,
  messages: { en },
})

let wrappers: VueWrapper[] = []
const track = <T extends VueWrapper>(w: T): T => {
  wrappers.push(w)
  return w
}

function env(
  seq: number,
  ts: string,
  type: string,
  payload: Record<string, unknown>,
): EventEnvelope {
  return {
    v: '1',
    seq,
    ts,
    session_id: 'session-1',
    run_id: 'run-1',
    type,
    request_id: `req-${seq}`,
    trace_id: `trace-${seq}`,
    capabilities: [],
    payload,
  }
}

function makeFeed(): ConversationFeed {
  const state = createFeedState()
  applyEvents(state, [
    env(1, '2026-11-22T04:00:00Z', 'task.dispatched', {
      dispatch_id: 'task-1',
      task_key: 'plan',
      title: 'Plan module layout',
      agent: 'coordinator',
    }),
    env(2, '2026-11-22T04:00:01Z', 'worker.assigned', {
      task_id: 'task-1',
      task_key: 'plan',
      handle: 'coordinator#1',
      agent_slug: 'coordinator',
      reason: 'delegated',
    }),
    env(3, '2026-11-22T04:00:02Z', 'task.dispatched', {
      dispatch_id: 'task-2',
      dispatched_by: 'coordinator#1',
      task_key: 'implement',
      title: 'Implement worktree patch',
      agent: 'global_engineering',
    }),
    env(4, '2026-11-22T04:00:03Z', 'worker.assigned', {
      task_id: 'task-2',
      task_key: 'implement',
      handle: 'global_engineering#1',
      agent_slug: 'global_engineering',
      reason: 'delegated',
    }),
  ])
  return { state: shallowRef(state), revision: ref(1), hydrated: ref(true) }
}

const sampleDiff = [
  'diff --git a/src/new.ts b/src/new.ts',
  '--- /dev/null',
  '+++ b/src/new.ts',
  '@@ -0,0 +1,2 @@',
  '+export const created = true',
  'diff --git a/src/edit.ts b/src/edit.ts',
  '--- a/src/edit.ts',
  '+++ b/src/edit.ts',
  '@@ -1 +1 @@',
  '-old()',
  '+next()',
].join('\n')

const isolatedPreviewSnapshot: WorktreeDiffSnapshot = {
  branch: 'tinadec/task-run1-task2',
  files: [
    { path: 'src/new.ts', status: 'A', is_untracked: true },
    { path: 'src/edit.ts', status: 'M' },
    { path: 'src/old.ts', status: 'D' },
  ],
  sections: [
    {
      id: 'working_tree',
      kind: 'working_tree',
      title: 'Working Tree',
      diff: sampleDiff,
      file_count: 3,
      additions: 2,
      deletions: 1,
      notices: [],
      files: [
        { path: 'src/new.ts', change_type: 'added', additions: 1, deletions: 0, binary: false, truncated: false },
        { path: 'src/edit.ts', change_type: 'modified', additions: 1, deletions: 1, binary: false, truncated: false },
        { path: 'src/old.ts', change_type: 'deleted', additions: 0, deletions: 0, binary: false, truncated: false },
      ],
    },
  ],
}

beforeEach(() => {
  setActivePinia(createPinia())
  resetDockSyncForTests()
  vi.clearAllMocks()
  hoisted.uieSnapshot.columns.right.collapsed = true
  hoisted.executeCodeTool.mockImplementation(async (_toolId: string, req: { cwd: string; arguments: { action: string; repository_path?: string } }) => {
    if (req.arguments.action === 'worktrees') {
      return {
        tool_id: 'git_worktree_manager',
        status: 'completed',
        data: {
          worktrees: [
            { path: 'D:/repo', branch: 'main', is_main: true, is_current: true },
            { path: 'D:/repo/.tinadec/worktrees/task-2', branch: 'tinadec/task-run1-task2', is_main: false, is_current: false },
          ],
        },
      }
    }
    if (req.arguments.action === 'push_plan') {
      return {
        tool_id: 'git_worktree_manager',
        status: 'completed',
        data: {
          branch: 'main',
          worktrees: [
            { path: 'D:/repo', branch: 'main', is_main: true, is_current: true },
            { path: 'D:/repo/.tinadec/worktrees/task-2', branch: 'tinadec/task-run1-task2', is_main: false, is_current: false },
          ],
        },
      }
    }
    const targetPath = req.arguments.repository_path ?? req.cwd
    if (targetPath.includes('task-2')) {
      return { tool_id: 'git_worktree_manager', status: 'completed', data: isolatedPreviewSnapshot }
    }
    return { tool_id: 'git_worktree_manager', status: 'completed', data: { branch: 'main', files: [], sections: [] } }
  })

  const topology: SessionTopologyDto = {
    session_id: 'session-1',
    runs_truncated: false,
    leases_truncated: false,
    members_truncated: false,
    generated_at: '2026-11-22T04:00:00Z',
    members: [],
    runs: [
      {
        run_id: 'run-1',
        status: 'executing',
        tasks_truncated: false,
        instances_truncated: false,
        instances: [],
        tasks: [
          {
            task_id: 'task-1',
            task_key: 'plan',
            title: 'Plan module layout',
            status: 'completed',
            handle: 'coordinator#1',
            agent_slug: 'coordinator',
            dependencies: [],
            write_scope: [],
          },
          {
            task_id: 'task-2',
            task_key: 'implement',
            title: 'Implement worktree patch',
            status: 'completed',
            handle: 'global_engineering#1',
            agent_slug: 'global_engineering',
            dispatched_by_task_id: 'task-1',
            dependencies: [],
            write_scope: ['src/new.ts', 'src/edit.ts'],
          },
        ],
      },
    ],
    leases: [
      {
        lease_id: 'lease-1',
        kind: 'worktree',
        resource_key: 'D:/repo/.tinadec/worktrees/task-2',
        exclusive: true,
        run_id: 'run-1',
        task_id: 'task-2',
      },
    ],
  }
  hoisted.getSessionTopology.mockResolvedValue(topology)
})

afterEach(() => {
  for (const w of wrappers) w.unmount()
  wrappers = []
  resetDockSyncForTests()
})

describe('#7 ui-worktree-git-highlight', () => {
  it('splits diffs by file, summarizes +A/~M/-D counts, and builds worker lineage groups', () => {
    const byFile = splitUnifiedDiffByFile(sampleDiff)
    expect(byFile.get('src/new.ts')).toContain('export const created = true')
    expect(byFile.get('src/edit.ts')).toContain('+next()')

    const summary = summarizeWorktreeDiff(isolatedPreviewSnapshot)
    expect(summary.counts).toMatchObject({ added: 1, modified: 1, deleted: 1, totalFiles: 3 })

    const feed = makeFeed()
    const diffMap = new Map<string, WorktreeDiffSnapshot>([
      ['d:/repo/.tinadec/worktrees/task-2', isolatedPreviewSnapshot],
    ])
    const groups = buildWorkerWorktreeGroups({
      mainRootPath: 'D:/repo',
      worktrees: [
        { path: 'D:/repo', branch: 'main', is_main: true, is_current: true },
        { path: 'D:/repo/.tinadec/worktrees/task-2', branch: 'tinadec/task-run1-task2', is_main: false, is_current: false },
      ],
      diffByWorktreePath: diffMap,
      feed,
      topology: {
        session_id: 'session-1',
        runs_truncated: false,
        leases_truncated: false,
        members_truncated: false,
        generated_at: '',
        members: [],
        runs: [
          {
            run_id: 'run-1',
            status: 'executing',
            tasks_truncated: false,
            instances_truncated: false,
            instances: [],
            tasks: [
              {
                task_id: 'task-1',
                task_key: 'plan',
                title: 'Plan module layout',
                status: 'completed',
                handle: 'coordinator#1',
                agent_slug: 'coordinator',
                dependencies: [],
                write_scope: [],
              },
              {
                task_id: 'task-2',
                task_key: 'implement',
                title: 'Implement worktree patch',
                status: 'running',
                handle: 'global_engineering#1',
                agent_slug: 'global_engineering',
                dispatched_by_task_id: 'task-1',
                dependencies: [],
                write_scope: [],
              },
            ],
          },
        ],
        leases: [
          {
            lease_id: 'lease-1',
            kind: 'worktree',
            resource_key: 'D:/repo/.tinadec/worktrees/task-2',
            exclusive: true,
            run_id: 'run-1',
            task_id: 'task-2',
          },
        ],
      },
    })

    expect(groups).toHaveLength(2)
    expect(groups[0]).toMatchObject({
      isMain: false,
      scope: 'isolated',
      branch: 'tinadec/task-run1-task2',
      taskId: 'task-2',
      workerHandle: 'global_engineering#1',
      canCleanup: true,
      counts: { added: 1, modified: 1, deleted: 1 },
    })
    expect(groups[0]!.lineage.map((s) => s.taskId)).toEqual(['task-1', 'task-2'])
    expect(groups[1]).toMatchObject({ isMain: true, scope: 'main', branch: 'main', canCleanup: false })
  })

  it('groups GitPanel by worker with branch, +A/~M/-D, lineage, 1-click diff drawer, and cleanup action', async () => {
    const DiffStub = defineComponent({
      name: 'DiffViewer',
      props: ['filePath', 'diffText'],
      setup: (p) => () => h('div', { 'data-testid': 'diff-viewer-stub' }, `${p.filePath}:${p.diffText}`),
    })

    const wrapper = track(mount(GitPanel, {
      props: {
        approvals: [],
        currentProjectPath: 'D:/repo',
        selectedSessionId: 'session-1',
        feed: makeFeed(),
        runs: [{ id: 'run-1', status: 'executing' }],
      },
      global: {
        plugins: [i18n],
        stubs: {
          GitChangesView: true,
          GitHistoryView: true,
          GitBranchView: true,
          SnapshotOverrideDialog: true,
          DiffViewer: DiffStub,
        },
      },
    }))

    await nextTick()
    await new Promise((r) => setTimeout(r, 10))
    await nextTick()

    const groups = wrapper.findAll('[data-testid="git-worker-worktree-group"]')
    expect(groups).toHaveLength(2)
    expect(groups[0]!.get('[data-testid="git-worker-branch-name"]').text()).toContain('tinadec/task-run1-task2')
    expect(groups[0]!.get('[data-testid="git-worker-diff-counts"]').text()).toMatch(/\+1.*~1.*-1/)
    expect(groups[0]!.get('[data-testid="git-worker-lineage"]').text()).toContain('Engineer')

    // 1-click file diff inspection opens the diff drawer for the clicked file
    await groups[0]!.findAll('[data-testid="git-worker-file-diff-btn"]')[0]!.trigger('click')
    await nextTick()
    expect(wrapper.find('[data-testid="git-worktree-diff-drawer"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="diff-viewer-stub"]').text()).toContain('src/new.ts')
    expect(wrapper.get('[data-testid="git-active-scope-badge"]').classes()).toContain('is-isolated')
  })

  it('shows bound worktree, lineage, and +A/~M/-D in SubAgentsPanel and WorkspaceCard', async () => {
    const subPanel = track(mount(SubAgentsPanel, {
      props: {
        feed: makeFeed(),
        runs: [{ id: 'run-1', status: 'executing' }],
        workspaceRoot: 'D:/repo',
      },
      global: { plugins: [i18n], stubs: { DiffViewer: true } },
    }))
    await new Promise((r) => setTimeout(r, 10))
    await nextTick()

    const items = subPanel.findAll('[data-testid="sub-agents-roster-item"]')
    await items[1]!.trigger('click')
    await nextTick()
    const summary = subPanel.get('[data-testid="sub-agent-worktree-summary"]')
    expect(summary.get('[data-testid="sub-agent-worktree-branch"]').text()).toContain('tinadec/task-run1-task2')
    expect(summary.get('[data-testid="sub-agent-lineage"]').text()).toContain('Lead Coordinator')

    const wsCard = track(mount(WorkspaceCard, {
      global: { plugins: [i18n], stubs: { WorkspaceRootsPanel: { template: '<div class="roots-stub" />' } } },
    }))
    await new Promise((r) => setTimeout(r, 10))
    await nextTick()
    expect(wsCard.find('[data-testid="workspace-worktrees-section"]').exists()).toBe(true)
    expect(wsCard.findAll('[data-testid="workspace-worktree-item"]')).toHaveLength(2)
  })
})

describe('#8 ui-chat-stream-dock-sync', () => {
  it('parses worktree/dispatch tools and dispatches UIE collapseColumn + openCard + orchestration event on click', async () => {
    const wtCall = {
      id: 'tc-wt',
      toolId: 'git_worktree_create',
      toolName: 'git_worktree_create',
      status: 'completed',
      startedAt: null,
      completedAt: null,
      durationMs: 25,
      argsSummary: 'branch=tinadec/task-run1-task2 path=D:/repo/.tinadec/worktrees/task-2 handle=engineer#1',
      resultSummary: 'created',
      requiresApproval: false,
      approvalId: null,
      evidence: [],
      seq: 1,
      risk: 'medium',
    } as ToolCall

    expect(parseToolDockInfo(wtCall)).toMatchObject({
      kind: 'worktree',
      branch: 'tinadec/task-run1-task2',
      workerHandle: 'engineer#1',
    })

    const toolWrapper = track(mount(ToolCallCard, {
      props: { toolCall: wtCall, runId: 'run-1', workerTaskId: 'task-2' },
      global: { plugins: [i18n] },
    }))
    await toolWrapper.get('[data-testid="tool-call-dock-jump"]').trigger('click')
    expect(activeDockFocus.value?.card).toBe('git')
    expect(highlightedDockCard.value).toBe('git')
    expect(hoisted.uieDispatch).toHaveBeenCalledWith(expect.objectContaining({
      command: expect.objectContaining({ type: 'collapseColumn', slotId: 'right', collapsed: false }),
    }))
    expect(hoisted.uieDispatch).toHaveBeenCalledWith(expect.objectContaining({
      command: expect.objectContaining({ type: 'openCard', descriptorId: 'git', slotId: 'right' }),
    }))

    await toolWrapper.get('[data-testid="tool-call-worker-badge"]').trigger('click')
    expect(activeDockFocus.value?.card).toBe('agent')
    expect(activeDockFocus.value?.workerHandle).toBe('engineer#1')

    clearDockWorkerFilter()
    expect(activeDockFocus.value?.workerHandle).toBeNull()
  })

  it('renders compact dock cards across TurnTimeline, AgentActivityBanner, and ChatCard and routes clicks cleanly', async () => {
    const dispatchSpy = vi.fn()
    window.addEventListener('tinadec:focus-orchestration-node', dispatchSpy)

    const dispatchCall = {
      id: 'tc-dispatch',
      toolId: 'task_dispatch',
      toolName: 'task_dispatch',
      status: 'running',
      startedAt: null,
      completedAt: null,
      durationMs: null,
      argsSummary: 'agent=engineer#1 task_key=implement',
      resultSummary: null,
      requiresApproval: false,
      approvalId: null,
      evidence: [],
      seq: 2,
      risk: 'low',
    } as ToolCall

    const timeline = track(mount(TurnTimeline, {
      props: { runId: 'run-1', toolCalls: [dispatchCall] },
      global: { plugins: [i18n] },
    }))
    const cards = timeline.findAll('[data-testid="turn-dock-card"]')
    expect(cards).toHaveLength(1)
    await cards[0]!.get('.turn-dock-card-main').trigger('click')
    expect(activeDockFocus.value?.card).toBe('orchestration')
    expect(dispatchSpy).toHaveBeenCalled()

    const banner = track(mount(AgentActivityBanner, {
      props: {
        activity: {
          status: 'waiting_approval',
          runId: 'run-1',
          runStartedAt: null,
          runSummary: null,
          activeAgentName: 'Lead',
          activeAgentRole: 'coordinator',
          completedNodes: 0,
          totalNodes: 1,
          lastUpdated: null,
        },
        agentStates: {
          e1: {
            agentId: 'e1',
            agentName: 'engineer#1',
            agentLayer: 'execution',
            agentType: 'global_engineering',
            status: 'waiting',
            lastActiveAt: null,
            currentTask: 'Wait approval',
          },
        },
      },
      global: { plugins: [i18n] },
    }))
    await new Promise((r) => setTimeout(r, 10))
    await nextTick()
    const approvalCardBtn = banner.get('[data-dock-kind="approval"] .agent-banner-dock-main')
    await approvalCardBtn.trigger('click')
    expect(activeDockFocus.value?.card).toBe('approval')

    hoisted.homeState.conversationFeed = makeFeed()
    const chatCard = track(mount(ChatCard, {
      global: {
        plugins: [i18n],
        stubs: { ChatPanel: true, UiButton: true },
      },
    }))
    await new Promise((r) => setTimeout(r, 10))
    await nextTick()
    expect(chatCard.find('[data-testid="chat-stream-dock-bar"]').exists()).toBe(true)
    const workerBtns = chatCard.findAll('[data-testid="chat-stream-worker-badge"]')
    expect(workerBtns.length).toBeGreaterThan(0)
    await workerBtns[0]!.trigger('click')
    expect(activeDockFocus.value?.card).toBe('agent')

    window.removeEventListener('tinadec:focus-orchestration-node', dispatchSpy)

    const homePage = track(mount(HomePage, {
      global: { plugins: [createPinia(), i18n] },
    }))
    await nextTick()
    const scrollIntoView = vi.fn()
    const workerEl = homePage.get('[data-worker-task-id="task-2"]').element as HTMLElement
    workerEl.scrollIntoView = scrollIntoView

    focusDockCard({
      card: 'git',
      reason: 'worktree',
      taskId: 'task-2',
      workerHandle: 'global_engineering#1',
      worktreePath: 'D:/repo/.tinadec/worktrees/task-2',
    })
    await nextTick()
    await nextTick()

    expect(homePage.get('.home-page-container').attributes('data-dock-focused-card')).toBe('git')
    expect(homePage.get('.home-page-container').attributes('data-dock-focused-worker')).toBe('global_engineering#1')
    expect(homePage.get('.uie-card-host').classes()).toContain('is-dock-focus-highlight')
    expect(scrollIntoView).toHaveBeenCalled()
  })

  it('skips out-of-root external temp worktrees and probes in-root worktrees via registered root cwd', async () => {
    expect(isPathWithinRoot('D:/repo/.tinadec/worktrees/wt-1', 'D:/repo')).toBe(true)
    expect(isPathWithinRoot('D:\\repo', 'd:/repo/')).toBe(true)
    expect(isPathWithinRoot('D:/repo2/other', 'D:/repo')).toBe(false)
    expect(isPathWithinRoot('C:/Users/lincu/AppData/Local/Temp/ext-wt', 'D:/repo')).toBe(false)

    hoisted.getSessionTopology.mockResolvedValueOnce(null)
    hoisted.executeCodeTool.mockClear()
    hoisted.executeCodeTool.mockImplementation(async (_tool: string, req: { arguments?: { action?: string } }) => {
      if (req.arguments?.action === 'worktrees') {
        return {
          status: 'completed',
          data: {
            worktrees: [
              { path: 'D:/repo', branch: 'main', is_main: true },
              { path: 'D:/repo/.tinadec/worktrees/wt-1', branch: 'feat/wt-1', is_main: false },
              { path: 'C:/Users/lincu/AppData/Local/Temp/ext-wt', branch: 'feat/ext', is_main: false },
            ],
          },
        }
      }
      return { status: 'completed', data: { branch: 'feat/wt-1', files: [], sections: [] } }
    })

    const { effectScope } = await import('vue')
    const scope = effectScope()
    scope.run(() => {
      useSessionWorktrees({
        projectPath: () => 'D:/repo',
        sessionId: () => 'session-1',
        mainPreviewData: () => ({ branch: 'main', files: [], sections: [] }),
      })
    })
    await new Promise((r) => setTimeout(r, 30))
    await nextTick()

    const probeCalls = hoisted.executeCodeTool.mock.calls
      .filter(([, req]) => (req as { arguments?: { action?: string } }).arguments?.action === 'diff_preview')
      .map(([, req]) => req as { cwd: string; arguments: { repository_path?: string } })

    expect(probeCalls.length).toBe(1)
    expect(probeCalls[0]).toEqual({
      cwd: 'D:/repo',
      arguments: expect.objectContaining({
        action: 'diff_preview',
        repository_path: 'D:/repo/.tinadec/worktrees/wt-1',
      }),
    })
    scope.stop()
  })
})
