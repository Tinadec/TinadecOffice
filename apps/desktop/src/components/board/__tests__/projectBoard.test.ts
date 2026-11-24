// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent, h, nextTick } from 'vue'
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { RouterView, createMemoryHistory, createRouter } from 'vue-router'
import i18n from '@/i18n'
import { resetHostAccessForTests, setHostAccessStatus } from '@/lib/hostAccess'
import { useNotifications } from '@/composables/useNotifications'
import { homeController } from '@/controllers/HomeController'
import { appRoutes } from '@/router'
import HomePage from '@/pages/HomePage.vue'
import {
  api,
  type ProjectBoardTaskDto,
  type ProjectBoardViewDto,
  type ProjectDto,
  type SessionDto,
} from '@/api'
import AppSidebar from '@/components/AppSidebar.vue'
import {
  BoardKanbanView,
  BoardListView,
  BoardSwimlaneView,
  BoardTaskCard,
  BoardTaskDrawer,
  BoardViewToolbar,
  SPATIAL_PROMOTION_PREF_STORAGE_KEY,
  buildBoardSwimlanes,
  computeReorderSortKey,
  filterBoardTasks,
  filterOrdinarySidebarSessions,
  groupBoardTasks,
  isBoardTaskSession,
  readSpatialPromotionPreference,
  saveSpatialPromotionPreference,
  sortBoardTasks,
  useProjectBoardView,
} from '@/components/board'

function makeTask(overrides: Partial<ProjectBoardTaskDto> = {}): ProjectBoardTaskDto {
  return {
    task_id: overrides.task_id ?? 'task-1',
    project_id: overrides.project_id ?? 'proj-alpha',
    project_name: overrides.project_name ?? 'Alpha Repo',
    task_key: overrides.task_key ?? 'TIN-1',
    title: overrides.title ?? 'Implement .tinadec/ sync service',
    description_md: overrides.description_md ?? 'Sync declarative YAML frontmatter safely.',
    checklist: overrides.checklist ?? [
      { id: 'c1', text: 'SHA-256 anti-echo', done: true },
      { id: 'c2', text: 'Conflict preservation', done: false },
    ],
    status: overrides.status ?? 'ready',
    priority: overrides.priority ?? 'high',
    blocked: overrides.blocked ?? false,
    blocked_reason: overrides.blocked_reason ?? null,
    assignee: overrides.assignee ?? { kind: 'native', display_name: 'Carbon Worker' },
    parent_task_id: overrides.parent_task_id ?? null,
    depends_on: overrides.depends_on ?? [],
    labels: overrides.labels ?? ['sync', 'core'],
    spatial_ref: overrides.spatial_ref ?? null,
    sort_key: overrides.sort_key ?? 'V',
    active_session_id: overrides.active_session_id ?? null,
    active_run_id: overrides.active_run_id ?? null,
    run_status: overrides.run_status ?? null,
    worktree_path: overrides.worktree_path ?? null,
    lease_id: overrides.lease_id ?? null,
    live_digest_summary: overrides.live_digest_summary ?? null,
    diff_stats: overrides.diff_stats ?? null,
    has_sync_conflict: overrides.has_sync_conflict ?? false,
    sync_conflict: overrides.sync_conflict ?? null,
    revision: overrides.revision ?? 1,
    created_at_unix_ms: overrides.created_at_unix_ms ?? 1_700_000_000_000,
    updated_at_unix_ms: overrides.updated_at_unix_ms ?? 1_700_000_005_000,
  }
}

describe('Project Board View Engine & Components (Phase 1 #54)', () => {
  beforeEach(() => {
    window.localStorage.clear()
    resetHostAccessForTests()
    setHostAccessStatus({ state: 'ready', managed: true })
    const notifications = useNotifications()
    notifications.setGlobalConnectionBlocked(false)
    notifications.dismissAll()
    notifications.clearHistory()
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ code: 'NOT_FOUND' }), { status: 404 })),
    )
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('computes strictly ordered Base-62 Fractional Indexing sort_key values across head, middle, and tail moves', () => {
    const k1 = computeReorderSortKey([], 't-1', 0)
    const k2 = computeReorderSortKey([{ task_id: 't-1', sort_key: k1 }], 't-2', 1)
    const k0 = computeReorderSortKey(
      [
        { task_id: 't-1', sort_key: k1 },
        { task_id: 't-2', sort_key: k2 },
      ],
      't-0',
      0,
    )
    const kMid = computeReorderSortKey(
      [
        { task_id: 't-0', sort_key: k0 },
        { task_id: 't-1', sort_key: k1 },
        { task_id: 't-2', sort_key: k2 },
      ],
      't-mid',
      2,
    )

    expect(k0 < k1).toBe(true)
    expect(k1 < kMid).toBe(true)
    expect(kMid < k2).toBe(true)

    // Defensive fallback when external edits produce duplicate sort_keys: never throws RangeError.
    const fallback = computeReorderSortKey(
      [
        { task_id: 'dup-1', sort_key: 'V' },
        { task_id: 'dup-2', sort_key: 'V' },
      ],
      'moved',
      1,
    )
    expect(typeof fallback).toBe('string')
    expect(fallback.length).toBeGreaterThan(0)
  })

  it('filters, groups, builds swimlanes, and sorts tasks by DAG dependency depth & priority', () => {
    const tRoot = makeTask({
      task_id: 't-root',
      task_key: 'TIN-1',
      project_id: 'proj-a',
      project_name: 'Alpha',
      status: 'done',
      priority: 'medium',
      assignee: { kind: 'human' },
      labels: ['schema'],
      sort_key: 'Z',
    })
    const tMid = makeTask({
      task_id: 't-mid',
      task_key: 'TIN-2',
      project_id: 'proj-a',
      project_name: 'Alpha',
      status: 'in_review',
      priority: 'urgent',
      assignee: { kind: 'cli', harness_id: 'claude-code' },
      depends_on: ['t-root'],
      has_sync_conflict: true,
      labels: ['gateway'],
      sort_key: 'M',
    })
    const tLeaf = makeTask({
      task_id: 't-leaf',
      task_key: 'TIN-3',
      project_id: 'proj-b',
      project_name: 'Beta',
      status: 'ready',
      priority: 'high',
      blocked: true,
      assignee: { kind: 'native' },
      depends_on: ['t-mid'],
      labels: ['ui'],
      sort_key: 'A',
    })

    const all = [tLeaf, tMid, tRoot]

    // DAG dependency sort puts root (depth 0) -> mid (depth 1) -> leaf (depth 2) regardless of sort_key
    const depSorted = sortBoardTasks(all, 'dependency')
    expect(depSorted.map((t) => t.task_id)).toEqual(['t-root', 't-mid', 't-leaf'])

    // Multi-criteria filtering
    expect(filterBoardTasks(all, { has_conflict: true }).map((t) => t.task_id)).toEqual(['t-mid'])
    expect(filterBoardTasks(all, { needs_my_action: true }).map((t) => t.task_id)).toEqual([
      't-leaf',
      't-mid',
    ])
    expect(filterBoardTasks(all, { assignee_kinds: ['cli'] }).map((t) => t.task_id)).toEqual(['t-mid'])
    expect(filterBoardTasks(all, { query: 'tin-3' }).map((t) => t.task_id)).toEqual(['t-leaf'])

    // Grouping by assignee & Swimlanes by project
    const byAssignee = groupBoardTasks(all, 'assignee', 'manual')
    expect(byAssignee.find((g) => g.key === 'cli')?.tasks.map((t) => t.task_id)).toEqual(['t-mid'])

    const lanes = buildBoardSwimlanes(
      all,
      'project',
      'manual',
      new Map([
        ['proj-a', 'Alpha'],
        ['proj-b', 'Beta'],
      ]),
    )
    expect(lanes).toHaveLength(2)
    expect(lanes[0]?.cells.in_review.map((t) => t.task_id)).toEqual(['t-mid'])
    expect(lanes[1]?.cells.ready.map((t) => t.task_id)).toEqual(['t-leaf'])
  })

  it('manages Saved Views CRUD, SSE incremental updates, offline cache resilience, and Phase 2 spatial promotion preference', async () => {
    const initialTasks = [
      makeTask({ task_id: 't-1', task_key: 'TIN-1', status: 'ready', sort_key: 'G' }),
      makeTask({ task_id: 't-2', task_key: 'TIN-2', status: 'in_review', sort_key: 'T' }),
    ]
    const savedViewMock: ProjectBoardViewDto = {
      view_id: 'view-review',
      scope: 'project',
      project_id: 'proj-alpha',
      name: 'Review Queue',
      config: {
        layout: 'list',
        group_by: 'priority',
        swimlane_by: 'assignee',
        sort_by: 'priority',
        filters: { statuses: ['in_review'], needs_my_action: true },
        visible_fields: ['live_digest', 'diff_stats'],
      },
      sort_order: 0,
      created_at_unix_ms: 1_700_000_000_000,
      updated_at_unix_ms: 1_700_000_000_000,
    }

    vi.spyOn(api, 'listProjectBoardTasks').mockResolvedValueOnce(initialTasks)
    vi.spyOn(api, 'getProjectBoardSettings').mockResolvedValueOnce({
      project_id: 'proj-alpha',
      key_prefix: 'TIN',
      next_sequence: 3,
      storage_mode: 'repo_tinadec',
      resolved_board_dir: '/repo/.tinadec/board',
      project_instructions: 'Keep monochrome minimal.',
      default_assignee_kind: 'native',
      max_concurrency: 2,
      autopilot_enabled: false,
      updated_at_unix_ms: 1_700_000_000_000,
    })
    vi.spyOn(api, 'listBoardViews').mockResolvedValueOnce([])
    vi.spyOn(api, 'createBoardView').mockResolvedValueOnce(savedViewMock)
    vi.spyOn(api, 'deleteBoardView').mockResolvedValueOnce(undefined)
    vi.spyOn(api, 'subscribeProjectBoardEvents').mockReturnValue({
      close: vi.fn(),
      closed: false,
      lastSeq: 0,
    })

    const board = useProjectBoardView({
      scope: 'project',
      projectId: 'proj-alpha',
    })
    await flushPromises()

    expect(board.tasks.value).toHaveLength(2)
    expect(board.stale.value).toBe(false)

    // Save custom view & switch between default and saved view
    board.layout.value = 'list'
    board.groupBy.value = 'priority'
    board.filters.value = { ...board.filters.value, statuses: ['in_review'], needs_my_action: true }
    const createdView = await board.saveCurrentAsView('Review Queue')
    expect(createdView.view_id).toBe('view-review')
    expect(board.activeViewId.value).toBe('view-review')
    expect(board.filteredTasks.value.map((t) => t.task_id)).toEqual(['t-2'])

    board.selectSavedView(null)
    expect(board.layout.value).toBe('kanban')
    expect(board.filteredTasks.value).toHaveLength(2)

    board.selectSavedView('view-review')
    expect(board.layout.value).toBe('list')
    expect(board.groupBy.value).toBe('priority')

    await board.deleteSavedView('view-review')
    expect(board.savedViews.value).toHaveLength(0)
    expect(board.activeViewId.value).toBeNull()

    // Live SSE incremental upsert & delete
    board.applySseEvent({
      seq: 11,
      event_type: 'task_upserted',
      project_id: 'proj-alpha',
      task_id: 't-3',
      task: makeTask({ task_id: 't-3', task_key: 'TIN-3', title: 'Streamed via SSE', revision: 1 }),
      occurred_at_unix_ms: Date.now(),
    })
    expect(board.tasks.value.some((t) => t.task_id === 't-3')).toBe(true)

    board.applySseEvent({
      seq: 12,
      event_type: 'task_deleted',
      project_id: 'proj-alpha',
      task_id: 't-1',
      occurred_at_unix_ms: Date.now(),
    })
    expect(board.tasks.value.some((t) => t.task_id === 't-1')).toBe(false)

    // Offline cache fallback: transient network failure keeps cached tasks & marks stale=true
    vi.spyOn(api, 'listProjectBoardTasks').mockRejectedValueOnce(new Error('Network offline'))
    await board.refresh()
    expect(board.stale.value).toBe(true)
    expect(board.tasks.value.map((t) => t.task_id)).toEqual(['t-2', 't-3'])

    // Phase 2 Spatial promotion preference persistence
    saveSpatialPromotionPreference({ mode: 'parent_with_children', target_status: 'backlog' })
    expect(window.localStorage.getItem(SPATIAL_PROMOTION_PREF_STORAGE_KEY)).toContain('parent_with_children')
    expect(readSpatialPromotionPreference()).toEqual({
      mode: 'parent_with_children',
      target_status: 'backlog',
    })

    board.disconnectStream()
  })

  it('switches cleanly across Kanban, List, and Swimlane view components and emits toolbar layout changes', async () => {
    const tasks = [
      makeTask({
        task_id: 't-k1',
        task_key: 'TIN-10',
        status: 'in_progress',
        live_digest_summary: 'Editing router & sidebar',
        diff_stats: { added: 48, removed: 6, files: 2 },
        spatial_ref: { session_id: 'sess-space-1', object_id: 'sticky-9' },
      }),
    ]
    const groups = groupBoardTasks(tasks, 'status', 'manual')
    const lanes = buildBoardSwimlanes(tasks, 'project', 'manual', new Map([['proj-alpha', 'Alpha Repo']]))

    const toolbar = mount(BoardViewToolbar, {
      global: { plugins: [i18n] },
      props: {
        scope: 'global',
        layout: 'kanban',
        groupBy: 'status',
        swimlaneBy: 'project',
        sortBy: 'manual',
        filters: {},
        visibleFields: ['project_badge', 'live_digest', 'diff_stats', 'spatial_ref'],
        savedViews: [],
        activeViewId: null,
        activeFilterCount: 0,
        availableLabels: ['sync'],
      },
    })

    await toolbar.get('[data-testid="board-layout-list"]').trigger('click')
    await toolbar.get('[data-testid="board-layout-swimlane"]').trigger('click')
    expect(toolbar.emitted('update:layout')).toEqual([['list'], ['swimlane']])

    const kanban = mount(BoardKanbanView, {
      global: { plugins: [i18n] },
      props: {
        groups,
        visibleFields: ['project_badge', 'live_digest', 'diff_stats', 'spatial_ref'],
      },
    })
    expect(kanban.find('[data-testid="board-task-card-t-k1"]').exists()).toBe(true)
    expect(kanban.find('[data-testid="board-card-diff-pill"]').text()).toContain('+48')
    await kanban.get('[data-testid="board-card-spatial-chip"]').trigger('click')
    expect(kanban.emitted('jump-spatial')?.[0]?.[0]).toMatchObject({ task_id: 't-k1' })

    const listView = mount(BoardListView, {
      global: { plugins: [i18n] },
      props: {
        groups,
        visibleFields: ['project_badge', 'live_digest', 'diff_stats'],
      },
    })
    expect(listView.find('[data-testid="board-list-row-t-k1"]').exists()).toBe(true)
    await listView.get('[data-testid="board-list-status-t-k1"]').trigger('click')
    expect(listView.emitted('quick-update')?.[0]?.[1]).toMatchObject({ status: 'in_review' })

    const swimlaneView = mount(BoardSwimlaneView, {
      global: { plugins: [i18n] },
      props: {
        swimlanes: lanes,
        visibleFields: ['live_digest'],
      },
    })
    expect(swimlaneView.find('[data-testid="board-swimlane-cell-proj-alpha-in_progress"]').exists()).toBe(
      true,
    )
    expect(swimlaneView.findComponent(BoardTaskCard).exists()).toBe(true)
  })

  it('enforces AppSidebar hygiene: hides origin==="board_task" sessions, navigates to /projects/:projectId on project click, and toggles sessions via chevron', async () => {
    vi.spyOn(api, 'getGlobalBoardSummary').mockResolvedValue({
      total_tasks: 12,
      backlog_count: 2,
      ready_count: 3,
      in_progress_count: 4,
      in_review_count: 2,
      done_count: 1,
      blocked_count: 1,
      conflict_count: 1,
      active_run_count: 4,
      needs_action_count: 4,
      projects: [],
      updated_at_unix_ms: Date.now(),
    })
    vi.spyOn(api, 'subscribeGlobalBoardEvents').mockReturnValue({
      close: vi.fn(),
      closed: false,
      lastSeq: 0,
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', component: { template: '<div />' } },
        { path: '/board', component: { template: '<div />' } },
        { path: '/projects/:projectId', component: { template: '<div />' } },
      ],
    })
    await router.push('/')
    await router.isReady()

    const projects: ProjectDto[] = [
      {
        id: 'proj-clean',
        name: 'Clean Workspace',
        path: '/workspace/clean',
        created_at: '2026-11-24T00:00:00Z',
        lifecycle_status: 'active',
      },
    ]

    const ordinarySession: SessionDto = {
      id: 'sess-chat-1',
      project_id: 'proj-clean',
      title: 'User Architecture Discussion',
      status: 'active',
      permission_mode: 'default',
      space_options: null,
      settings_revision: 1,
      created_at: '2026-11-24T01:00:00Z',
      updated_at: '2026-11-24T01:05:00Z',
      origin: null,
    }

    const backgroundBoardSessions: SessionDto[] = Array.from({ length: 10 }, (_, idx) => ({
      id: `sess-board-${idx + 1}`,
      project_id: 'proj-clean',
      title: `Background Board Task TIN-${idx + 1}`,
      status: 'active',
      permission_mode: 'default',
      space_options: null,
      settings_revision: 1,
      created_at: '2026-11-24T02:00:00Z',
      updated_at: '2026-11-24T02:05:00Z',
      origin: 'board_task',
      board_task_id: `task-${idx + 1}`,
    }))

    const allSessions = [ordinarySession, ...backgroundBoardSessions]
    expect(isBoardTaskSession(backgroundBoardSessions[0])).toBe(true)
    expect(filterOrdinarySidebarSessions(allSessions)).toEqual([ordinarySession])

    const sidebar = mount(AppSidebar, {
      global: {
        plugins: [i18n, router],
        stubs: {
          BrandLogo: true,
          TinadecCalligraphy: true,
          PersonalAvatar: true,
          SidebarActivityCard: true,
        },
      },
      props: {
        projects,
        sessions: allSessions,
        selectedProjectId: 'proj-clean',
        selectedSessionId: 'sess-chat-1',
        busy: false,
      },
    })
    await flushPromises()

    // Ordinary session is shown; all 10 background `board_task` sessions are hidden!
    expect(sidebar.text()).toContain('User Architecture Discussion')
    expect(sidebar.text()).not.toContain('Background Board Task TIN-1')

    // Pending action count pill is displayed on the top Global Board entry
    expect(sidebar.get('[data-testid="sidebar-board-needs-action"]').text()).toBe('4')

    // Clicking chevron collapses/expands ordinary session list WITHOUT navigating away from '/'
    const chevron = sidebar.get('[data-testid="project-expand-chevron"]')
    await chevron.trigger('click')
    expect(router.currentRoute.value.fullPath).toBe('/')
    expect(sidebar.text()).not.toContain('User Architecture Discussion')

    await chevron.trigger('click')
    expect(sidebar.text()).toContain('User Architecture Discussion')

    // Clicking project row main navigates to `/projects/proj-clean`
    await sidebar.get('[data-testid="project-row-main"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.fullPath).toBe('/projects/proj-clean')

    // Clicking Global Board nav item navigates to `/board`
    await sidebar.get('[data-testid="sidebar-global-board"]').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.fullPath).toBe('/board')

    sidebar.unmount()
  })

  it('resolves .tinadec/ sync conflicts, dispatches runs, sends live steer, and executes diff review acceptance/changes in BoardTaskDrawer', async () => {
    vi.spyOn(api, 'listAgentModes').mockResolvedValue([])
    vi.spyOn(api, 'listModelProviders').mockResolvedValue([])
    vi.spyOn(api, 'discoverHarnesses').mockResolvedValue({ cli_runtimes: [] })
    vi.spyOn(api, 'listMessages').mockResolvedValue([
      {
        id: 'm-1',
        session_id: 'sess-live-9',
        role: 'assistant',
        content: 'Updated sync service tests.',
        created_at: '2026-11-24T03:00:00Z',
      } as never,
    ])
    vi.spyOn(api, 'getProjectBoardTaskDiff').mockResolvedValue({
      worktree_path: '/repo/.tinadec/worktrees/TIN-7',
      diff_text: '--- a/src/sync.ts\n+++ b/src/sync.ts\n@@ -1,2 +1,3 @@\n+export const sha256 = true;',
      files: [{ path: 'src/sync.ts', status: 'modified', added: 1, removed: 0 }],
    })
    const createInteractionSpy = vi.spyOn(api, 'createInteraction').mockResolvedValue({
      interaction_id: 'int-1',
      status: 'admitted',
    } as never)

    const conflictedReviewTask = makeTask({
      task_id: 'task-7',
      task_key: 'TIN-7',
      status: 'in_review',
      active_session_id: 'sess-live-9',
      active_run_id: 'run-live-9',
      run_status: 'reviewing',
      worktree_path: '/repo/.tinadec/worktrees/TIN-7',
      has_sync_conflict: true,
      sync_conflict: {
        file_rel_path: '.tinadec/board/tasks/TIN-7.md',
        detected_at_unix_ms: Date.now(),
        conflicting_fields: ['priority'],
        file_snapshot: { priority: 'urgent' },
        board_snapshot: { priority: 'high' },
      },
    })

    const drawer = mount(BoardTaskDrawer, {
      attachTo: document.body,
      global: { plugins: [i18n] },
      props: {
        open: true,
        mode: 'edit',
        task: conflictedReviewTask,
        projectTasks: [conflictedReviewTask],
      },
    })
    await flushPromises()
    await nextTick()

    const bodyText = document.body.textContent ?? ''
    expect(bodyText).toContain('.tinadec/board/tasks/TIN-7.md')
    expect(bodyText).toContain('+export const sha256 = true;')

    // 1-click Keep File & Keep Board conflict resolution
    const keepFileBtn = document.body.querySelector<HTMLButtonElement>(
      '[data-testid="drawer-conflict-keep-file"]',
    )!
    keepFileBtn.click()
    const keepBoardBtn = document.body.querySelector<HTMLButtonElement>(
      '[data-testid="drawer-conflict-keep-board"]',
    )!
    keepBoardBtn.click()
    expect(drawer.emitted('resolve-conflict')).toEqual([
      ['proj-alpha', 'task-7', 'keep_file'],
      ['proj-alpha', 'task-7', 'keep_board'],
    ])

    // Live execution Steer input
    const steerInput = document.body.querySelector<HTMLInputElement>('[data-testid="drawer-steer-input"]')!
    steerInput.value = 'Focus only on deterministic YAML keys'
    steerInput.dispatchEvent(new Event('input', { bubbles: true }))
    await nextTick()
    const steerBtn = document.body.querySelector<HTMLButtonElement>('[data-testid="drawer-steer-send"]')!
    steerBtn.click()
    await flushPromises()
    expect(createInteractionSpy).toHaveBeenCalledWith(
      'sess-live-9',
      expect.objectContaining({
        content: 'Focus only on deterministic YAML keys',
        dispatch_mode: 'insert',
        target_run_id: 'run-live-9',
      }),
    )

    // In-Review Approve & Request Changes
    const feedbackArea = document.body.querySelector<HTMLTextAreaElement>(
      '[data-testid="drawer-review-feedback-input"]',
    )!
    feedbackArea.value = 'Add one more cycle detection assertion'
    feedbackArea.dispatchEvent(new Event('input', { bubbles: true }))
    await nextTick()

    const requestChangesBtn = document.body.querySelector<HTMLButtonElement>(
      '[data-testid="drawer-request-changes-btn"]',
    )!
    requestChangesBtn.click()
    expect(drawer.emitted('request-changes')).toEqual([
      ['proj-alpha', 'task-7', 'Add one more cycle detection assertion'],
    ])

    const approveBtn = document.body.querySelector<HTMLButtonElement>('[data-testid="drawer-approve-btn"]')!
    approveBtn.click()
    expect(drawer.emitted('approve-task')).toEqual([
      ['proj-alpha', 'task-7', { merge_or_clean_worktree: true, note: null }],
    ])

    // Steer error routes to unified notification center without rendering inline .board-drawer-error
    createInteractionSpy.mockRejectedValueOnce(new Error('Steer channel busy'))
    steerInput.value = 'Retry steer when ready'
    steerInput.dispatchEvent(new Event('input', { bubbles: true }))
    await nextTick()
    steerBtn.click()
    await flushPromises()
    expect(document.body.querySelector('.board-drawer-error')).toBeNull()
    expect(useNotifications().items.value.some((item) => item.key === 'board-task-drawer')).toBe(true)

    drawer.unmount()
  })

  it('keeps HomePage & AppSidebar mounted with stable scrollTop while switching ChatCard in-place across /, /board, and /projects/:projectId', async () => {
    // Provide non-zero `.uie-canvas` bounds & Web Animations stub for happy-dom layout measurement
    const origGetAnimations = HTMLElement.prototype.getAnimations
    HTMLElement.prototype.getAnimations = () => []
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      if (this.classList?.contains('uie-canvas')) {
        return new DOMRect(0, 0, 1360, 860)
      }
      return new DOMRect(0, 0, 0, 0)
    })

    vi.spyOn(homeController, 'start').mockImplementation(() => undefined)
    homeController.projects.value = [
      {
        id: 'proj-alpha',
        name: 'Alpha Repo',
        path: '/workspace/alpha',
        created_at: '2026-11-24T00:00:00Z',
        lifecycle_status: 'active',
      },
      {
        id: 'proj-beta',
        name: 'Beta Repo',
        path: '/workspace/beta',
        created_at: '2026-11-24T00:05:00Z',
        lifecycle_status: 'active',
      },
    ]
    homeController.sessions.value = [
      {
        id: 'sess-alpha-1',
        project_id: 'proj-alpha',
        title: 'Alpha Main Thread',
        status: 'active',
        permission_mode: 'default',
        space_options: null,
        settings_revision: 1,
        created_at: '2026-11-24T01:00:00Z',
        updated_at: '2026-11-24T01:05:00Z',
        origin: null,
      },
    ]
    homeController.selectedProjectId.value = 'proj-alpha'
    homeController.selectedSessionId.value = 'sess-alpha-1'

    vi.spyOn(api, 'listGlobalBoardTasks').mockResolvedValue([
      makeTask({ task_id: 't-global-1', task_key: 'TIN-1', title: 'Global Board Card' }),
    ])
    vi.spyOn(api, 'getGlobalBoardSummary').mockResolvedValue({
      total_tasks: 1,
      backlog_count: 0,
      ready_count: 1,
      in_progress_count: 0,
      in_review_count: 0,
      done_count: 0,
      blocked_count: 0,
      conflict_count: 0,
      active_run_count: 0,
      needs_action_count: 1,
      projects: [],
      updated_at_unix_ms: Date.now(),
    })
    vi.spyOn(api, 'listBoardViews').mockResolvedValue([])
    vi.spyOn(api, 'subscribeGlobalBoardEvents').mockReturnValue({
      close: vi.fn(),
      closed: false,
      lastSeq: 0,
    })
    vi.spyOn(api, 'subscribeProjectBoardEvents').mockReturnValue({
      close: vi.fn(),
      closed: false,
      lastSeq: 0,
    })
    const listProjectTasksSpy = vi.spyOn(api, 'listProjectBoardTasks').mockImplementation(async (pid) => [
      makeTask({
        task_id: `task-${pid}`,
        project_id: pid,
        project_name: pid === 'proj-beta' ? 'Beta Repo' : 'Alpha Repo',
        task_key: pid === 'proj-beta' ? 'BET-1' : 'ALP-1',
        title: `Task for ${pid}`,
      }),
    ])
    vi.spyOn(api, 'getProjectBoardSettings').mockImplementation(async (pid) => ({
      project_id: pid,
      key_prefix: pid === 'proj-beta' ? 'BET' : 'ALP',
      next_sequence: 2,
      storage_mode: 'repo_tinadec',
      resolved_board_dir: `/workspace/${pid}/.tinadec/board`,
      project_instructions: '',
      default_assignee_kind: 'native',
      max_concurrency: 2,
      autopilot_enabled: false,
      updated_at_unix_ms: Date.now(),
    }))
    vi.spyOn(api, 'getProjectNotes').mockResolvedValue(null as never)
    vi.spyOn(api, 'getProjectAutopilot').mockResolvedValue(null as never)
    const overviewSpy = vi.spyOn(api, 'getProjectOverview').mockImplementation(async (pid) => ({
      project_id: pid,
      name: pid === 'proj-beta' ? 'Beta Repo' : 'Alpha Repo',
      path: `/workspace/${pid}`,
      board_summary: {
        total_tasks: 1,
        backlog_count: 0,
        ready_count: 1,
        in_progress_count: 0,
        in_review_count: 0,
        done_count: 0,
        blocked_count: 0,
        conflict_count: 0,
        active_run_count: 0,
        needs_action_count: 0,
      },
      resolved_board_dir: `/workspace/${pid}/.tinadec/board`,
      resolved_notes_path: `/workspace/${pid}/.tinadec/project/notes.md`,
      storage_mode: 'repo_tinadec',
      ordinary_session_count: 1,
    } as never))
    vi.spyOn(api, 'syncProjectBoard').mockResolvedValue({
      imported_count: 2,
      exported_count: 1,
      conflict_count: 0,
    } as never)

    const router = createRouter({
      history: createMemoryHistory(),
      routes: appRoutes,
    })
    // Cold-boot directly on `/board` first to verify HomePage entrance `ready` event works on child board routes
    await router.push('/board')
    await router.isReady()

    let homeReadyCount = 0
    const ShellHost = defineComponent({
      name: 'PersistentShellTestHost',
      setup() {
        return () =>
          h(RouterView, null, {
            default: ({ Component }: { Component?: unknown }) =>
              Component ? h(Component as never, { onReady: () => { homeReadyCount += 1 } }) : null,
          })
      },
    })

    const shell = mount(ShellHost, {
      attachTo: document.body,
      global: {
        plugins: [i18n, router],
        stubs: {
          ChatPanel: { template: '<div data-testid="chat-panel-stub">Chat Stream</div>' },
          LiveDigestHeartbeatStrip: { template: '<div data-testid="live-digest-strip-stub" />' },
          BrandLogo: true,
          TinadecCalligraphy: true,
          PersonalAvatar: true,
          SidebarActivityCard: true,
          BoardTaskDrawer: true,
          CoordinatorPlanModal: true,
        },
      },
    })

    await flushPromises()
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)))
    await flushPromises()

    expect(homeReadyCount).toBe(1)
    const homeEl = shell.findComponent(HomePage).element
    const homeUid = (shell.findComponent(HomePage).vm as unknown as { $: { uid: number } }).$.uid
    const sidebarEl = shell.get('.sidebar').element
    const workspacesEl = shell.get('#sidebar-workspaces').element as HTMLElement
    workspacesEl.scrollTop = 148
    await shell.get('#sidebar-workspaces').trigger('scroll')

    // On `/board`: GlobalBoardPage renders inside ChatCard workspace surface, chat-only strip is hidden
    expect(shell.get('[data-testid="chat-card-root"]').attributes('data-home-surface')).toBe('board')
    expect(shell.find('[data-testid="global-board-page"]').exists()).toBe(true)
    expect(shell.find('[data-testid="chat-panel-stub"]').exists()).toBe(false)
    expect(shell.find('[data-testid="live-digest-strip-stub"]').exists()).toBe(false)

    // Navigate `/board` -> `/projects/proj-alpha`: zero HomePage/AppSidebar remount, scrollTop stays 148
    await router.push('/projects/proj-alpha')
    await flushPromises()
    expect(shell.findComponent(HomePage).element).toBe(homeEl)
    expect((shell.findComponent(HomePage).vm as unknown as { $: { uid: number } }).$.uid).toBe(homeUid)
    expect(shell.get('.sidebar').element).toBe(sidebarEl)
    expect(workspacesEl.scrollTop).toBe(148)
    expect(shell.get('[data-testid="chat-card-root"]').attributes('data-home-surface')).toBe('project-hub')
    expect(shell.find('[data-testid="project-hub-page"]').exists()).toBe(true)
    expect(overviewSpy).toHaveBeenCalledWith('proj-alpha')
    expect(listProjectTasksSpy).toHaveBeenCalledWith('proj-alpha')

    // Trigger `.tinadec/` sync -> transient notification via useNotifications, zero inline `.hub-sync-banner`
    await shell.get('[data-testid="hub-sync-tinadec-btn"]').trigger('click')
    await flushPromises()
    expect(shell.find('.hub-sync-banner').exists()).toBe(false)
    expect(
      useNotifications().items.value.some(
        (item) => item.key === 'board-tinadec-sync' && item.level === 'success',
      ),
    ).toBe(true)

    // Navigate `/projects/proj-alpha` -> `/projects/proj-beta`: HomePage stays mounted, scrollTop preserved across project scope, overview & tasks reload in-place
    await router.push('/projects/proj-beta')
    await flushPromises()
    expect(shell.findComponent(HomePage).element).toBe(homeEl)
    expect((shell.findComponent(HomePage).vm as unknown as { $: { uid: number } }).$.uid).toBe(homeUid)
    const betaSidebarEl = shell.get('.sidebar').element
    expect((shell.get('#sidebar-workspaces').element as HTMLElement).scrollTop).toBe(148)
    expect(overviewSpy).toHaveBeenCalledWith('proj-beta')
    expect(listProjectTasksSpy).toHaveBeenCalledWith('proj-beta')

    // Navigate `/projects/proj-beta` -> `/` -> `/board`: zero HomePage/AppSidebar remount, scrollTop stays 148
    await router.push('/')
    await flushPromises()
    expect(shell.findComponent(HomePage).element).toBe(homeEl)
    expect((shell.findComponent(HomePage).vm as unknown as { $: { uid: number } }).$.uid).toBe(homeUid)
    expect(shell.get('.sidebar').element).toBe(betaSidebarEl)
    expect((shell.get('#sidebar-workspaces').element as HTMLElement).scrollTop).toBe(148)
    expect(shell.get('[data-testid="chat-card-root"]').attributes('data-home-surface')).toBe('chat')
    expect(shell.find('[data-testid="chat-panel-stub"]').exists()).toBe(true)
    expect(shell.find('[data-testid="project-hub-page"]').exists()).toBe(false)

    await router.push('/board')
    await flushPromises()
    expect(shell.findComponent(HomePage).element).toBe(homeEl)
    expect(shell.get('.sidebar').element).toBe(betaSidebarEl)
    expect((shell.get('#sidebar-workspaces').element as HTMLElement).scrollTop).toBe(148)
    expect(shell.get('[data-testid="chat-card-root"]').attributes('data-home-surface')).toBe('board')

    shell.unmount()
    HTMLElement.prototype.getAnimations = origGetAnimations
  })

  it('respects strict monochrome & .tinadec/ guardrails across all board UI files (BoardShellLayout deleted, zero border-left stripes, zero .agent/ paths)', () => {
    const dir = path.dirname(fileURLToPath(import.meta.url))
    expect(existsSync(path.resolve(dir, '../BoardShellLayout.vue'))).toBe(false)

    const filesToCheck = [
      path.resolve(dir, '../BoardViewToolbar.vue'),
      path.resolve(dir, '../BoardTaskCard.vue'),
      path.resolve(dir, '../BoardKanbanView.vue'),
      path.resolve(dir, '../BoardListView.vue'),
      path.resolve(dir, '../BoardSwimlaneView.vue'),
      path.resolve(dir, '../BoardTaskDrawer.vue'),
      path.resolve(dir, '../../../pages/HomePage.vue'),
      path.resolve(dir, '../../../pages/GlobalBoardPage.vue'),
      path.resolve(dir, '../../../pages/ProjectHubPage.vue'),
      path.resolve(dir, '../../../../../TinadecUI/src/components/cards/home/ChatCard.vue'),
    ]

    for (const filePath of filesToCheck) {
      const content = readFileSync(filePath, 'utf8')
      expect(content, `${path.basename(filePath)} must not use border-left colored stripes`).not.toMatch(
        /border-left\s*:\s*[23]px\s+solid/i,
      )
      expect(content, `${path.basename(filePath)} must never reference .agent/`).not.toContain('.agent/')
    }
  })
})
