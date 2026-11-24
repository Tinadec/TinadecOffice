// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createMemoryHistory, createRouter } from 'vue-router'
import i18n from '@/i18n'
import { ApiError } from '@/lib/apiError'
import { setHostAccessStatus } from '@/lib/hostAccess'
import {
  api,
  type CoordinatorPlanResultDto,
  type ProjectAutopilotStateDto,
  type ProjectBoardTaskDto,
  type ProjectNotesDto,
} from '@/api'
import {
  BoardAutopilotBar,
  CoordinatorPlanModal,
  ProjectNotesPanel,
  useProjectBoardView,
} from '@/components/board'
import GlobalBoardPage from '@/pages/GlobalBoardPage.vue'
import { homeController } from '@/controllers/HomeController'

function makeTask(overrides: Partial<ProjectBoardTaskDto> = {}): ProjectBoardTaskDto {
  return {
    task_id: overrides.task_id ?? 'task-1',
    project_id: overrides.project_id ?? 'proj-alpha',
    project_name: overrides.project_name ?? 'Alpha Repo',
    task_key: overrides.task_key ?? 'TIN-1',
    title: overrides.title ?? 'Implement Autopilot scheduler',
    description_md: overrides.description_md ?? 'Respect max_concurrency and DAG dependencies.',
    checklist: overrides.checklist ?? [],
    status: overrides.status ?? 'ready',
    priority: overrides.priority ?? 'high',
    blocked: overrides.blocked ?? false,
    blocked_reason: overrides.blocked_reason ?? null,
    assignee: overrides.assignee ?? { kind: 'native' },
    parent_task_id: overrides.parent_task_id ?? null,
    depends_on: overrides.depends_on ?? [],
    labels: overrides.labels ?? [],
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

function makeNotes(overrides: Partial<ProjectNotesDto> = {}): ProjectNotesDto {
  return {
    project_id: overrides.project_id ?? 'proj-alpha',
    content_md: overrides.content_md ?? '# Alpha Project\n\n## 项目目标\n- Ship Phase 3 cleanly',
    resolved_file_path: overrides.resolved_file_path ?? '/workspace/alpha/.tinadec/project/notes.md',
    storage_mode: overrides.storage_mode ?? 'repo_tinadec',
    revision: overrides.revision ?? 2,
    last_synced_hash: overrides.last_synced_hash ?? 'sha256-initial',
    updated_at_unix_ms: overrides.updated_at_unix_ms ?? 1_700_000_010_000,
  }
}

function makeAutopilot(overrides: Partial<ProjectAutopilotStateDto> = {}): ProjectAutopilotStateDto {
  return {
    project_id: overrides.project_id ?? 'proj-alpha',
    autopilot_enabled: overrides.autopilot_enabled ?? false,
    max_concurrency: overrides.max_concurrency ?? 2,
    active_task_count: overrides.active_task_count ?? 0,
    active_task_ids: overrides.active_task_ids ?? [],
    queued_ready_count: overrides.queued_ready_count ?? 3,
    blocked_task_count: overrides.blocked_task_count ?? 0,
    blocked_tasks: overrides.blocked_tasks ?? [],
    last_tick_at_unix_ms: overrides.last_tick_at_unix_ms ?? null,
    last_error: overrides.last_error ?? null,
    updated_at_unix_ms: overrides.updated_at_unix_ms ?? 1_700_000_020_000,
  }
}

describe('Phase 3 (#56): Project Notes Studio, Coordinator Planner & Autopilot UI', () => {
  beforeEach(() => {
    window.localStorage.clear()
    setHostAccessStatus({ state: 'ready', managed: true })
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ code: 'NOT_FOUND' }), { status: 404 })),
    )
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('loads, edits, inserts templates, saves with expected_revision (Ctrl+S), handles 409 revision_conflict, and refreshes live on SSE notes_updated', async () => {
    const initialNotes = makeNotes({
      revision: 2,
      content_md: '## 项目目标\n- Keep monochrome minimal',
    })
    const savedNotes = makeNotes({
      revision: 3,
      content_md: '## 项目目标\n- Keep monochrome minimal\n\n## 决策与进展记录\n- \n',
    })
    const diskUpdatedNotes = makeNotes({
      revision: 4,
      content_md: '## 项目目标\n- Keep monochrome minimal\n\n## 决策与进展记录\n- [TIN-1] Completed sync',
    })

    vi.spyOn(api, 'listProjectBoardTasks').mockResolvedValue([])
    vi.spyOn(api, 'getProjectBoardSettings').mockResolvedValue(null as never)
    vi.spyOn(api, 'listBoardViews').mockResolvedValue([])
    vi.spyOn(api, 'getProjectNotes').mockResolvedValue(initialNotes)
    vi.spyOn(api, 'getProjectAutopilot').mockResolvedValue(makeAutopilot())
    vi.spyOn(api, 'subscribeProjectBoardEvents').mockReturnValue({
      close: vi.fn(),
      closed: false,
      lastSeq: 0,
    })

    const updateNotesSpy = vi
      .spyOn(api, 'updateProjectNotes')
      .mockResolvedValueOnce(savedNotes)
      .mockRejectedValueOnce(new ApiError('Revision mismatch', 409, 'revision_conflict'))
      .mockResolvedValueOnce(makeNotes({ revision: 5, content_md: 'Force saved content' }))

    const board = useProjectBoardView({
      scope: 'project',
      projectId: 'proj-alpha',
    })
    await flushPromises()

    expect(board.notes.value?.revision).toBe(2)

    const wrapper = mount(ProjectNotesPanel, {
      global: { plugins: [i18n] },
      props: {
        projectId: 'proj-alpha',
        notes: board.notes.value,
      },
    })
    await flushPromises()

    expect(wrapper.get('[data-testid="notes-resolved-path"]').text()).toBe(
      '/workspace/alpha/.tinadec/project/notes.md',
    )
    expect(wrapper.get('[data-testid="notes-revision-badge"]').text()).toBe('r2')

    // Insert structured template section (`## 决策与进展记录` in default zh-CN locale)
    await wrapper.get('[data-testid="notes-template-decisions"]').trigger('click')
    const textarea = wrapper.get('[data-testid="notes-editor-textarea"]')
    expect((textarea.element as HTMLTextAreaElement).value).toContain('## 决策与进展记录')
    expect(wrapper.find('[data-testid="notes-dirty-badge"]').exists()).toBe(true)

    // Save via Ctrl+S keyboard shortcut -> sends optimistic `expected_revision: 2`
    await wrapper.trigger('keydown', { key: 's', ctrlKey: true })
    await flushPromises()

    expect(updateNotesSpy).toHaveBeenNthCalledWith(1, 'proj-alpha', {
      content_md: expect.stringContaining('## 决策与进展记录'),
      expected_revision: 2,
    })
    expect(wrapper.get('[data-testid="notes-revision-badge"]').text()).toBe('r3')
    expect(wrapper.find('[data-testid="notes-dirty-badge"]').exists()).toBe(false)

    // Live SSE `notes_updated` folds into `board.notes` and updates clean studio automatically
    board.applySseEvent({
      seq: 30,
      event_type: 'notes_updated',
      project_id: 'proj-alpha',
      notes: diskUpdatedNotes,
      occurred_at_unix_ms: Date.now(),
    })
    await wrapper.setProps({ notes: board.notes.value })
    await nextTick()

    expect((textarea.element as HTMLTextAreaElement).value).toContain('[TIN-1] Completed sync')
    expect(wrapper.get('[data-testid="notes-revision-badge"]').text()).toBe('r4')
    expect(wrapper.get('[data-testid="notes-external-sync-indicator"]').classes()).toContain('synced')

    // Switch Edit | Preview | Split modes cleanly
    await wrapper.get('[data-testid="notes-mode-preview"]').trigger('click')
    expect(wrapper.find('[data-testid="notes-editor-textarea"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="notes-preview-pane"]').text()).toContain('[TIN-1] Completed sync')
    await wrapper.get('[data-testid="notes-mode-split"]').trigger('click')

    // Trigger 409 `revision_conflict` and resolve via Keep Mine & Save (`expected_revision: null`)
    await wrapper.get('[data-testid="notes-editor-textarea"]').setValue('Force saved content')
    await wrapper.get('[data-testid="notes-save-btn"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="notes-conflict-banner"]').exists()).toBe(true)
    await wrapper.get('[data-testid="notes-conflict-overwrite-btn"]').trigger('click')
    await flushPromises()

    expect(updateNotesSpy).toHaveBeenNthCalledWith(3, 'proj-alpha', {
      content_md: 'Force saved content',
      expected_revision: null,
    })
    expect(wrapper.find('[data-testid="notes-conflict-banner"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="notes-revision-badge"]').text()).toBe('r5')

    wrapper.unmount()
    board.disconnectStream()
  })

  it('runs CoordinatorPlanModal from goal input -> draft generation -> draft editing/deletion/dependency tweak -> applyCoordinatorPlan', async () => {
    const planResultMock: CoordinatorPlanResultDto = {
      project_id: 'proj-alpha',
      goal: 'Deliver living notes and concurrent autopilot',
      summary: 'Decomposed into 3 sequential & parallel cards',
      planner_source: 'heuristic',
      auto_applied: false,
      drafts: [
        {
          draft_id: 'draft-1',
          title: 'Add notes.md bidirectional sync',
          description_md: 'Watch .tinadec/project/notes.md',
          checklist: [{ id: 'c1', text: 'SHA-256 anti-echo', done: false }],
          priority: 'high',
          status: 'ready',
          assignee: { kind: 'native' },
          labels: ['notes'],
          depends_on_draft_ids: [],
          depends_on_task_ids: [],
        },
        {
          draft_id: 'draft-2',
          title: 'Obsolete spike draft',
          description_md: 'Will be deleted by user',
          checklist: [],
          priority: 'low',
          status: 'backlog',
          assignee: { kind: 'native' },
          labels: [],
          depends_on_draft_ids: ['draft-1'],
          depends_on_task_ids: [],
        },
        {
          draft_id: 'draft-3',
          title: 'Wire Autopilot concurrent scheduler',
          description_md: 'Enforce max_concurrency',
          checklist: [],
          priority: 'medium',
          status: 'ready',
          assignee: { kind: 'native' },
          labels: ['autopilot'],
          depends_on_draft_ids: ['draft-2'],
          depends_on_task_ids: [],
        },
      ],
      created_tasks: [],
    }

    const createdTasksMock = [
      makeTask({ task_id: 'task-101', task_key: 'TIN-101', title: 'Add notes.md sync & context injection' }),
      makeTask({
        task_id: 'task-102',
        task_key: 'TIN-102',
        title: 'Wire Autopilot concurrent scheduler',
        depends_on: ['task-101', 'existing-1'],
      }),
    ]

    const planSpy = vi.spyOn(api, 'planCoordinatorTasks').mockResolvedValueOnce(planResultMock)
    const applySpy = vi.spyOn(api, 'applyCoordinatorPlan').mockResolvedValueOnce({
      project_id: 'proj-alpha',
      created_tasks: createdTasksMock,
    })

    const existingTask = makeTask({ task_id: 'existing-1', task_key: 'TIN-1', title: 'Phase 1 foundation' })

    const modal = mount(CoordinatorPlanModal, {
      attachTo: document.body,
      global: { plugins: [i18n] },
      props: {
        open: true,
        projectId: 'proj-alpha',
        existingTasks: [existingTask],
        defaultAssigneeKind: 'native',
      },
    })
    await nextTick()

    // Step 1: Enter goal, adjust max_tasks, and generate drafts
    const goalArea = document.body.querySelector<HTMLTextAreaElement>('[data-testid="coordinator-goal-input"]')!
    goalArea.value = 'Deliver living notes and concurrent autopilot'
    goalArea.dispatchEvent(new Event('input', { bubbles: true }))

    const maxInput = document.body.querySelector<HTMLInputElement>('[data-testid="coordinator-max-tasks"]')!
    maxInput.value = '6'
    maxInput.dispatchEvent(new Event('input', { bubbles: true }))
    await nextTick()

    document.body.querySelector<HTMLButtonElement>('[data-testid="coordinator-generate-btn"]')!.click()
    await flushPromises()

    expect(planSpy).toHaveBeenCalledWith('proj-alpha', {
      goal: 'Deliver living notes and concurrent autopilot',
      default_status: 'ready',
      default_assignee: { kind: 'native' },
      auto_apply: false,
      max_tasks: 6,
    })

    // Step 2: Draft review & tweak
    expect(document.body.querySelector('[data-testid="coordinator-step-review"]')).not.toBeNull()
    expect(document.body.querySelectorAll('[data-testid^="coordinator-draft-card-"]')).toHaveLength(3)

    // Delete `draft-2` -> also strips `draft-2` from `draft-3.depends_on_draft_ids`
    document.body.querySelector<HTMLButtonElement>('[data-testid="coordinator-draft-delete-draft-2"]')!.click()
    await nextTick()
    expect(document.body.querySelectorAll('[data-testid^="coordinator-draft-card-"]')).toHaveLength(2)

    // Edit `draft-1` title
    const draft1Title = document.body.querySelector<HTMLInputElement>(
      '[data-testid="coordinator-draft-title-draft-1"]',
    )!
    draft1Title.value = 'Add notes.md sync & context injection'
    draft1Title.dispatchEvent(new Event('input', { bubbles: true }))

    // Add `draft-1` and `existing-1` as prerequisites on `draft-3`
    document.body
      .querySelector<HTMLButtonElement>('[data-testid="coordinator-dep-draft-draft-3-draft-1"]')!
      .click()
    document.body
      .querySelector<HTMLButtonElement>('[data-testid="coordinator-dep-task-draft-3-existing-1"]')!
      .click()
    await nextTick()

    // Click Apply to Board
    document.body.querySelector<HTMLButtonElement>('[data-testid="coordinator-apply-btn"]')!.click()
    await flushPromises()

    expect(applySpy).toHaveBeenCalledTimes(1)
    const sentPayload = applySpy.mock.calls[0]?.[1]
    expect(sentPayload?.update_notes).toBe(true)
    expect(sentPayload?.drafts).toHaveLength(2)
    expect(sentPayload?.drafts[0]?.title).toBe('Add notes.md sync & context injection')
    expect(sentPayload?.drafts[1]?.depends_on_draft_ids).toEqual(['draft-1'])
    expect(sentPayload?.drafts[1]?.depends_on_task_ids).toEqual(['existing-1'])

    expect(modal.emitted('applied')?.[0]?.[0]).toEqual(createdTasksMock)
    expect(modal.emitted('update:open')?.at(-1)).toEqual([false])

    modal.unmount()
  })

  it('controls BoardAutopilotBar start, instant pause, max_concurrency stepper, Blocked human intervention banner, drawer open, and unblock', async () => {
    const blockedTask = makeTask({
      task_id: 't-blocked-9',
      task_key: 'TIN-9',
      title: 'Migrate auth token schema',
      status: 'ready',
      blocked: true,
      blocked_reason: 'Merge conflict in auth.ts; needs human confirmation',
      revision: 4,
    })

    const updateAutopilotSpy = vi
      .spyOn(api, 'updateProjectAutopilot')
      .mockResolvedValueOnce(
        makeAutopilot({
          autopilot_enabled: true,
          max_concurrency: 2,
          active_task_count: 2,
          active_task_ids: ['t-1', 't-2'],
          queued_ready_count: 1,
        }),
      )
      .mockResolvedValueOnce(
        makeAutopilot({
          autopilot_enabled: true,
          max_concurrency: 3,
          active_task_count: 2,
          active_task_ids: ['t-1', 't-2'],
          queued_ready_count: 1,
        }),
      )
      .mockResolvedValueOnce(
        makeAutopilot({
          autopilot_enabled: false,
          max_concurrency: 3,
          active_task_count: 0,
          active_task_ids: [],
          queued_ready_count: 3,
          blocked_task_count: 1,
          blocked_tasks: [blockedTask],
        }),
      )

    const updateTaskSpy = vi.spyOn(api, 'updateProjectBoardTask').mockResolvedValueOnce(
      makeTask({
        ...blockedTask,
        blocked: false,
        blocked_reason: null,
        status: 'ready',
        revision: 5,
      }),
    )
    vi.spyOn(api, 'getProjectAutopilot').mockResolvedValue(makeAutopilot())

    const bar = mount(BoardAutopilotBar, {
      global: { plugins: [i18n] },
      props: {
        projectId: 'proj-alpha',
        autopilot: makeAutopilot({
          autopilot_enabled: false,
          max_concurrency: 2,
          active_task_count: 0,
          queued_ready_count: 3,
        }),
        tasks: [],
      },
    })

    // 1. Toggle switch ON -> starts Autopilot
    await bar.get('[data-testid="autopilot-toggle-switch"]').trigger('click')
    await flushPromises()
    expect(updateAutopilotSpy).toHaveBeenNthCalledWith(1, 'proj-alpha', { autopilot_enabled: true })
    expect(bar.get('[data-testid="autopilot-live-counters"]').text()).toContain('2')

    // 2. Increment max_concurrency stepper (2 -> 3)
    await bar.get('[data-testid="autopilot-concurrency-inc"]').trigger('click')
    await flushPromises()
    expect(updateAutopilotSpy).toHaveBeenNthCalledWith(2, 'proj-alpha', { max_concurrency: 3 })
    expect(bar.get('[data-testid="autopilot-concurrency-value"]').text()).toBe('3')

    // 3. Instant Pause & Cancel Active when active runs exist
    expect(bar.find('[data-testid="autopilot-pause-cancel-btn"]').exists()).toBe(true)
    await bar.get('[data-testid="autopilot-pause-cancel-btn"]').trigger('click')
    await flushPromises()
    expect(updateAutopilotSpy).toHaveBeenNthCalledWith(3, 'proj-alpha', {
      autopilot_enabled: false,
      cancel_active: true,
    })

    // 4. Blocked task banner renders key + reason, emits `open-task` for BoardTaskDrawer, and supports 1-click Unblock & Resume
    expect(bar.find('[data-testid="autopilot-blocked-banner"]').exists()).toBe(true)
    expect(bar.get('[data-testid="autopilot-blocked-item-t-blocked-9"]').text()).toContain('TIN-9')
    expect(bar.get('[data-testid="autopilot-blocked-item-t-blocked-9"]').text()).toContain(
      'Merge conflict in auth.ts',
    )

    await bar.get('[data-testid="autopilot-blocked-open-t-blocked-9"]').trigger('click')
    expect(bar.emitted('open-task')?.[0]?.[0]).toMatchObject({ task_id: 't-blocked-9', task_key: 'TIN-9' })

    await bar.get('[data-testid="autopilot-blocked-unblock-t-blocked-9"]').trigger('click')
    await flushPromises()
    expect(updateTaskSpy).toHaveBeenCalledWith('proj-alpha', 't-blocked-9', {
      blocked: false,
      blocked_reason: null,
      status: 'ready',
      expected_revision: 4,
    })
    expect(bar.emitted('unblocked-task')?.[0]?.[0]).toMatchObject({
      task_id: 't-blocked-9',
      blocked: false,
    })

    bar.unmount()

    // 5. Verify GlobalBoardPage surfaces blocked alert & quick filter pill
    homeController.projects.value = [
      {
        id: 'proj-alpha',
        name: 'Alpha Repo',
        path: '/workspace/alpha',
        created_at: '2026-11-24T00:00:00Z',
        lifecycle_status: 'active',
      },
    ]
    vi.spyOn(api, 'listGlobalBoardTasks').mockResolvedValueOnce([
      blockedTask,
      makeTask({ task_id: 't-normal', task_key: 'TIN-10', blocked: false, status: 'ready' }),
    ])
    vi.spyOn(api, 'getGlobalBoardSummary').mockResolvedValueOnce({
      total_tasks: 2,
      backlog_count: 0,
      ready_count: 2,
      in_progress_count: 0,
      in_review_count: 0,
      done_count: 0,
      blocked_count: 1,
      conflict_count: 0,
      active_run_count: 0,
      needs_action_count: 1,
      projects: [],
      updated_at_unix_ms: Date.now(),
    })
    vi.spyOn(api, 'listBoardViews').mockResolvedValueOnce([])
    vi.spyOn(api, 'subscribeGlobalBoardEvents').mockReturnValue({
      close: vi.fn(),
      closed: false,
      lastSeq: 0,
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/board', component: GlobalBoardPage }],
    })
    await router.push('/board')
    await router.isReady()

    const globalPage = mount(GlobalBoardPage, {
      global: {
        plugins: [i18n, router],
        stubs: {
          BoardShellLayout: { template: '<div><slot /></div>' },
          BoardTaskDrawer: true,
        },
      },
    })
    await flushPromises()

    expect(globalPage.find('[data-testid="global-board-blocked-banner"]').exists()).toBe(true)
    expect(globalPage.get('[data-testid="global-board-blocked-banner"]').text()).toContain('TIN-9')
    await globalPage.get('[data-testid="global-board-blocked-filter"]').trigger('click')
    await nextTick()
    expect(globalPage.find('[data-testid="board-task-card-t-blocked-9"]').exists()).toBe(true)
    expect(globalPage.find('[data-testid="board-task-card-t-normal"]').exists()).toBe(false)

    globalPage.unmount()
  })

  it('enforces monochrome CSS & .tinadec/ guardrails across all Phase 3 Vue components', () => {
    const dir = path.dirname(fileURLToPath(import.meta.url))
    const filesToCheck = [
      path.resolve(dir, '../ProjectNotesPanel.vue'),
      path.resolve(dir, '../CoordinatorPlanModal.vue'),
      path.resolve(dir, '../BoardAutopilotBar.vue'),
      path.resolve(dir, '../BoardViewToolbar.vue'),
      path.resolve(dir, '../../../pages/ProjectHubPage.vue'),
      path.resolve(dir, '../../../pages/GlobalBoardPage.vue'),
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
