// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { computed, defineComponent, h, nextTick, ref } from 'vue'
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createMemoryHistory, createRouter } from 'vue-router'
import i18n from '@/i18n'
import { setHostAccessStatus } from '@/lib/hostAccess'
import {
  api,
  type BoardEventStreamHandle,
  type ProjectBoardSseEventDto,
  type ProjectBoardTaskDto,
} from '@/api'
import {
  BoardTaskCard,
  BoardTaskDrawer,
  PENDING_SPATIAL_FOCUS_STORAGE_KEY,
  SPATIAL_PROMOTION_PREF_STORAGE_KEY,
  buildSpatialFocusLocation,
  consumePendingSpatialFocus,
  readSpatialPromotionPreference,
  stashPendingSpatialFocus,
} from '@/components/board'
import GlobalBoardPage from '@/pages/GlobalBoardPage.vue'
import SpatialProjectBoardNode from '@/components/spatial/SpatialProjectBoardNode.vue'
import SpatialSelectionBar from '@/components/spatial/SpatialSelectionBar.vue'
import SpatialStickyNode from '@/components/spatial/SpatialStickyNode.vue'
import SpatialSectionNode from '@/components/spatial/SpatialSectionNode.vue'
import SpatialTextNode from '@/components/spatial/SpatialTextNode.vue'
import {
  computeSpatialFocusViewport,
  sanitizeSpatialBoardWidgetProps,
} from '@/components/spatial/spatialWhiteboardTypes'
import { useSpatialBoardSync } from '@/composables/useSpatialBoardSync'

const HandleStub = defineComponent({
  name: 'Handle',
  props: ['id', 'type', 'position'],
  render() {
    return h('span', { class: 'vue-flow-handle-stub' })
  },
})

const NodeResizerStub = defineComponent({
  name: 'NodeResizer',
  props: ['isVisible', 'minWidth', 'minHeight', 'maxWidth', 'maxHeight'],
  render() {
    return h('span', { class: 'vue-flow-resizer-stub' })
  },
})

const flowStubs = {
  Handle: HandleStub,
  NodeResizer: NodeResizerStub,
}

function makeTask(overrides: Partial<ProjectBoardTaskDto> = {}): ProjectBoardTaskDto {
  return {
    task_id: overrides.task_id ?? 'task-14',
    project_id: overrides.project_id ?? 'proj-alpha',
    project_name: overrides.project_name ?? 'Tinadec Desktop',
    task_key: overrides.task_key ?? 'TIN-14',
    title: overrides.title ?? 'Bridge spatial sticky notes to project board',
    description_md: overrides.description_md ?? 'Zero task duplication in spatial props.',
    checklist: overrides.checklist ?? [],
    status: overrides.status ?? 'in_progress',
    priority: overrides.priority ?? 'high',
    blocked: overrides.blocked ?? false,
    blocked_reason: overrides.blocked_reason ?? null,
    assignee: overrides.assignee ?? {
      kind: 'cli',
      harness_id: 'claude-code',
      display_name: 'Claude Code',
    },
    parent_task_id: overrides.parent_task_id ?? null,
    depends_on: overrides.depends_on ?? [],
    labels: overrides.labels ?? ['spatial', 'bridge'],
    spatial_ref: overrides.spatial_ref ?? null,
    sort_key: overrides.sort_key ?? 'V',
    active_session_id: overrides.active_session_id ?? 'sess-exec-1',
    active_run_id: overrides.active_run_id ?? 'run-exec-1',
    run_status: overrides.run_status ?? 'running',
    worktree_path: overrides.worktree_path ?? null,
    lease_id: overrides.lease_id ?? null,
    live_digest_summary: overrides.live_digest_summary ?? 'Wiring SSE task hydration into canvas pills',
    diff_stats: overrides.diff_stats ?? null,
    has_sync_conflict: overrides.has_sync_conflict ?? false,
    sync_conflict: overrides.sync_conflict ?? null,
    revision: overrides.revision ?? 2,
    created_at_unix_ms: overrides.created_at_unix_ms ?? 1_700_000_000_000,
    updated_at_unix_ms: overrides.updated_at_unix_ms ?? 1_700_000_010_000,
  }
}

describe('Spatial <-> Flat Project Board Bridge (Phase 2 #55)', () => {
  beforeEach(() => {
    window.localStorage.clear()
    window.sessionStorage.clear()
    i18n.global.locale.value = 'zh-CN'
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

  it('executes 1-click default promotion and persists dropdown mode & target column in SPATIAL_PROMOTION_PREF_STORAGE_KEY', async () => {
    const bar = mount(SpatialSelectionBar, {
      global: { plugins: [i18n] },
      props: {
        selectedIds: ['sticky-1', 'sticky-2'],
        promotableIds: ['sticky-1', 'sticky-2'],
        projectId: 'proj-alpha',
        bounds: { x: 200, y: 180, width: 460, height: 220 },
        viewport: { x: 0, y: 0, zoom: 1 },
        surfaceWidth: 1280,
        surfaceHeight: 800,
      },
    })

    // 1. Default 1-click on main split button executes immediately without opening dropdown
    expect(bar.find('[data-testid="selection-promote-board-menu"]').exists()).toBe(false)
    await bar.get('[data-testid="selection-promote-board-main"]').trigger('click')

    expect(bar.emitted('promote-to-board')).toEqual([
      [
        {
          objectIds: ['sticky-1', 'sticky-2'],
          mode: 'split_many',
          targetStatus: 'ready',
        },
      ],
    ])

    // 2. Open right `▾` trigger, toggle target column to `backlog`, and pick `parent_with_children`
    await bar.get('[data-testid="selection-promote-board-trigger"]').trigger('click')
    expect(bar.find('[data-testid="selection-promote-board-menu"]').exists()).toBe(true)

    await bar.get('[data-testid="promote-target-backlog"]').trigger('click')
    expect(readSpatialPromotionPreference()).toEqual({
      mode: 'split_many',
      target_status: 'backlog',
    })

    await bar.get('[data-testid="promote-mode-parent_with_children"]').trigger('click')
    expect(bar.find('[data-testid="selection-promote-board-menu"]').exists()).toBe(false)

    // Persisted in localStorage under SPATIAL_PROMOTION_PREF_STORAGE_KEY
    const rawPref = window.localStorage.getItem(SPATIAL_PROMOTION_PREF_STORAGE_KEY)
    expect(rawPref).toBeTruthy()
    expect(JSON.parse(rawPref!)).toEqual({
      mode: 'parent_with_children',
      target_status: 'backlog',
    })

    expect(bar.emitted('promote-to-board')?.[1]).toEqual([
      {
        objectIds: ['sticky-1', 'sticky-2'],
        mode: 'parent_with_children',
        targetStatus: 'backlog',
      },
    ])

    // 3. Subsequent 1-click on main button uses the newly remembered mode + target column
    await bar.get('[data-testid="selection-promote-board-main"]').trigger('click')
    expect(bar.emitted('promote-to-board')?.[2]).toEqual([
      {
        objectIds: ['sticky-1', 'sticky-2'],
        mode: 'parent_with_children',
        targetStatus: 'backlog',
      },
    ])
  })

  it('renders live project tasks in SpatialProjectBoardNode with zero task duplication in spatial LWW props', async () => {
    // Verify spatial document & sanitizer strip any task/card/item copies
    const sync = useSpatialBoardSync({ sessionKey: 'draft:bridge-test' })
    const boardObj = sync.createBoardWidget({
      x: 100,
      y: 120,
      props: {
        project_id: 'proj-alpha',
        layout: 'kanban',
        tasks: [{ task_id: 'forbidden' }],
        cards: [{ id: 'forbidden' }],
        items: [{ id: 'forbidden' }],
      },
    })

    expect(boardObj.kind).toBe('board')
    expect(boardObj.width).toBe(780)
    expect(boardObj.height).toBe(480)
    expect(boardObj.props).toEqual({
      project_id: 'proj-alpha',
      layout: 'kanban',
    })
    expect('tasks' in boardObj.props).toBe(false)
    expect('cards' in boardObj.props).toBe(false)
    expect('items' in boardObj.props).toBe(false)

    sync.updateObjectProps(boardObj.id, {
      layout: 'list',
      tasks: [{ task_id: 'still-forbidden' }],
    })
    expect(sync.objectsById.value[boardObj.id]?.props).toEqual({
      project_id: 'proj-alpha',
      layout: 'list',
    })
    expect(
      sanitizeSpatialBoardWidgetProps({
        project_id: 'proj-alpha',
        compact: true,
        tasks: ['leak'],
        cards: ['leak'],
        items: ['leak'],
      }),
    ).toEqual({
      project_id: 'proj-alpha',
      compact: true,
    })

    // Mount SpatialProjectBoardNode and verify live tasks + SSE + projection-only emits
    let sseCallback: ((ev: ProjectBoardSseEventDto) => void) | undefined
    vi.spyOn(api, 'listProjectBoardTasks').mockResolvedValueOnce([
      makeTask({
        task_id: 't-live-1',
        task_key: 'TIN-21',
        title: 'Spatial Kanban Card One',
        status: 'in_progress',
        labels: ['frontend'],
      }),
      makeTask({
        task_id: 't-live-2',
        task_key: 'TIN-22',
        title: 'Backend Sync Review',
        status: 'in_review',
        labels: ['backend'],
      }),
    ])
    vi.spyOn(api, 'getProjectBoardSettings').mockResolvedValueOnce({
      project_id: 'proj-alpha',
      key_prefix: 'TIN',
      next_sequence: 23,
      storage_mode: 'repo_tinadec',
      default_assignee_kind: 'native',
      max_concurrency: 2,
      autopilot_enabled: false,
      updated_at_unix_ms: Date.now(),
    })
    vi.spyOn(api, 'listBoardViews').mockResolvedValueOnce([])
    vi.spyOn(api, 'subscribeProjectBoardEvents').mockImplementation((_pid, onEvent): BoardEventStreamHandle => {
      sseCallback = onEvent
      return { close: vi.fn(), closed: false, lastSeq: 0 }
    })

    const widget = mount(SpatialProjectBoardNode, {
      global: {
        plugins: [i18n],
        stubs: flowStubs,
      },
      props: {
        id: boardObj.id,
        sessionProjectId: 'proj-alpha',
        projectName: 'Tinadec Desktop',
        data: {
          project_id: 'proj-alpha',
          layout: 'kanban',
        },
      },
    })
    await flushPromises()

    expect(widget.text()).toContain('Spatial Kanban Card One')
    expect(widget.text()).toContain('Backend Sync Review')
    expect(widget.get('[data-testid="spatial-board-count"]').text()).toBe('2')

    // Switch layout to List -> emits ONLY `{ layout: 'list' }`, zero task data
    await widget.get('[data-testid="spatial-board-layout-list"]').trigger('click')
    const emittedPatches = widget.emitted('update:widget-props') ?? []
    expect(emittedPatches[0]).toEqual([{ layout: 'list' }])
    const firstPatchObj = (emittedPatches[0]?.[0] ?? {}) as Record<string, unknown>
    expect('tasks' in firstPatchObj).toBe(false)
    expect(widget.find('[data-testid="board-list-view"]').exists()).toBe(true)

    // Quick filter by `Needs My Action` -> filters down to `in_review` task & emits projection `{ filters }`
    await widget.get('[data-testid="spatial-board-filter-needs-action"]').trigger('click')
    expect(widget.get('[data-testid="spatial-board-count"]').text()).toBe('1')
    expect(widget.text()).toContain('Backend Sync Review')
    expect(widget.text()).not.toContain('Spatial Kanban Card One')

    // Reset via `All` chip -> shows both tasks again, plus SSE live upsert streams 3rd task
    await widget.get('[data-testid="spatial-board-filter-all"]').trigger('click')
    if (!sseCallback) throw new Error('Expected SSE listener to be registered')
    sseCallback({
      seq: 99,
      event_type: 'task_upserted',
      project_id: 'proj-alpha',
      task_id: 't-live-3',
      task: makeTask({
        task_id: 't-live-3',
        task_key: 'TIN-23',
        title: 'Live Streamed Task Three',
        status: 'ready',
      }),
      occurred_at_unix_ms: Date.now(),
    })
    await nextTick()
    expect(widget.get('[data-testid="spatial-board-count"]').text()).toBe('3')
    expect(widget.text()).toContain('Live Streamed Task Three')

    // Clicking a task emits `select-task` so SpatialPage opens BoardTaskDrawer
    await widget.get('[data-testid="board-list-row-t-live-1"]').trigger('click')
    expect(widget.emitted('select-task')?.[0]?.[0]).toMatchObject({
      task_id: 't-live-1',
      task_key: 'TIN-21',
    })

    widget.unmount()
    sync.dispose()
  })

  it('renders live status capsules on SpatialStickyNode, SpatialSectionNode, and SpatialTextNode and emits open-board-task on click', async () => {
    const liveTask = makeTask({
      task_id: 'task-14',
      task_key: 'TIN-14',
      title: 'Implement spatial bridge',
      status: 'in_progress',
      assignee: { kind: 'cli', harness_id: 'claude-code', display_name: 'Claude Code' },
      live_digest_summary: 'Running targeted Vitest suite',
    })
    const liveMap = ref(new Map<string, ProjectBoardTaskDto>([[liveTask.task_id, liveTask]]))
    const providedById = computed(() => liveMap.value)

    // 1. SpatialStickyNode hydrated from live project board map
    const sticky = mount(SpatialStickyNode, {
      global: {
        plugins: [i18n],
        stubs: flowStubs,
        provide: {
          'space:board-tasks-by-id': providedById,
        },
      },
      props: {
        id: 'sticky-14',
        data: {
          text: 'Draft sticky idea',
          tone: 'butter',
          board_task_id: 'task-14',
          board_task_key: 'TIN-14',
        },
      },
    })

    const stickyCapsule = sticky.get('[data-testid="spatial-task-status-capsule"]')
    expect(stickyCapsule.text()).toBe('TIN-14 · 进行中 · Claude Code')

    // Hovering previews title + live digest
    await sticky.get('.spatial-task-capsule-wrap').trigger('mouseenter')
    const tooltip = sticky.get('[data-testid="spatial-task-capsule-tooltip"]')
    expect(tooltip.text()).toContain('Implement spatial bridge')
    expect(tooltip.text()).toContain('Running targeted Vitest suite')

    // Clicking capsule emits `open-board-task`
    await stickyCapsule.trigger('click')
    expect(sticky.emitted('open-board-task')?.[0]?.[0]).toMatchObject({
      taskId: 'task-14',
      taskKey: 'TIN-14',
      task: expect.objectContaining({ task_id: 'task-14' }),
    })

    // 2. SpatialSectionNode hydrated via linkedTask prop
    const section = mount(SpatialSectionNode, {
      global: {
        plugins: [i18n],
        stubs: flowStubs,
      },
      props: {
        id: 'sec-14',
        data: {
          title: 'Phase 2 Architecture',
          board_task_id: 'task-14',
          board_task_key: 'TIN-14',
          linkedTask: liveTask,
        },
      },
    })
    const sectionCapsule = section.get('[data-testid="spatial-task-status-capsule"]')
    expect(sectionCapsule.text()).toBe('TIN-14 · 进行中 · Claude Code')
    await sectionCapsule.trigger('click')
    expect(section.emitted('open-board-task')?.[0]?.[0]).toMatchObject({
      taskId: 'task-14',
      taskKey: 'TIN-14',
    })

    // 3. SpatialTextNode clean fallback when task DTO is not yet in map (`board_task_key` only)
    const textNode = mount(SpatialTextNode, {
      global: {
        plugins: [i18n],
        stubs: flowStubs,
      },
      props: {
        id: 'txt-99',
        data: {
          text: 'Unloaded task note',
          scale: 'md',
          board_task_id: 'task-99',
          board_task_key: 'TIN-99',
        },
      },
    })
    const textCapsule = textNode.get('[data-testid="spatial-task-status-capsule"]')
    expect(textCapsule.text()).toBe('TIN-99')
    await textCapsule.trigger('click')
    expect(textNode.emitted('open-board-task')?.[0]?.[0]).toEqual({
      taskId: 'task-99',
      taskKey: 'TIN-99',
      task: null,
    })
  })

  it('navigates spatial_ref clicks to /space?sessionId=...&focusObjectId=... and centers viewport onto focused bounds', async () => {
    const anchoredTask = makeTask({
      task_id: 'task-anchor-1',
      task_key: 'TIN-30',
      title: 'Anchored Spatial Task',
      status: 'ready',
      spatial_ref: {
        session_id: 'sess-space-42',
        object_id: 'sticky-anchor-7',
        viewport_hint: { x: -120, y: -80, zoom: 0.9 },
      },
    })

    // Verify BoardTaskCard & BoardTaskDrawer emit `jump-spatial`
    const card = mount(BoardTaskCard, {
      global: { plugins: [i18n] },
      props: {
        task: anchoredTask,
        visibleFields: ['spatial_ref', 'live_digest'],
      },
    })
    await card.get('[data-testid="board-card-spatial-chip"]').trigger('click')
    expect(card.emitted('jump-spatial')?.[0]?.[0]).toEqual(anchoredTask)

    const drawer = mount(BoardTaskDrawer, {
      attachTo: document.body,
      global: { plugins: [i18n] },
      props: {
        open: true,
        mode: 'edit',
        task: anchoredTask,
        projectTasks: [anchoredTask],
      },
    })
    await flushPromises()
    await nextTick()
    const drawerJumpBtn = document.body.querySelector<HTMLButtonElement>(
      '[data-testid="drawer-jump-spatial-btn"]',
    )
    expect(drawerJumpBtn).not.toBeNull()
    drawerJumpBtn!.click()
    expect(drawer.emitted('jump-spatial')?.[0]?.[0]).toEqual(anchoredTask)
    drawer.unmount()

    // Verify GlobalBoardPage navigates to `/space?sessionId=sess-space-42&focusObjectId=sticky-anchor-7`
    vi.spyOn(api, 'listGlobalBoardTasks').mockResolvedValueOnce([anchoredTask])
    vi.spyOn(api, 'getGlobalBoardSummary').mockResolvedValueOnce({
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
      projects: [],
      updated_at_unix_ms: Date.now(),
    })
    vi.spyOn(api, 'listBoardViews').mockResolvedValueOnce([])
    vi.spyOn(api, 'subscribeGlobalBoardEvents').mockReturnValue({
      close: vi.fn(),
      closed: false,
      lastSeq: 0,
    })

    const SpaceRouteStub = defineComponent({
      name: 'SpaceRouteStub',
      render: () => h('div', { 'data-testid': 'space-route-stub' }),
    })

    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', component: SpaceRouteStub },
        { path: '/board', component: GlobalBoardPage },
        { path: '/space', component: SpaceRouteStub },
      ],
    })
    await router.push('/board')
    await router.isReady()

    const page = mount(GlobalBoardPage, {
      global: {
        plugins: [i18n, router],
        stubs: {
          BoardShellLayout: { template: '<div><slot /></div>' },
        },
      },
    })
    await flushPromises()

    await page.get('[data-testid="board-card-spatial-chip"]').trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.path).toBe('/space')
    expect(router.currentRoute.value.query).toEqual({
      sessionId: 'sess-space-42',
      focusObjectId: 'sticky-anchor-7',
    })
    expect(buildSpatialFocusLocation(anchoredTask.spatial_ref!)).toEqual({
      path: '/space',
      query: {
        sessionId: 'sess-space-42',
        focusObjectId: 'sticky-anchor-7',
      },
    })

    // Verify stashed focus payload & viewport centering calculation
    const consumed = consumePendingSpatialFocus('sess-space-42')
    expect(consumed).toEqual({
      session_id: 'sess-space-42',
      object_id: 'sticky-anchor-7',
      viewport_hint: { x: -120, y: -80, zoom: 0.9 },
    })

    // Centering onto node bounds `{ x: 400, y: 300, width: 200, height: 100 }` (center = 500, 350)
    // on a `1280 x 800` surface at zoom `0.9` -> x = 640 - 450 = 190, y = 400 - 315 = 85
    const centeredVp = computeSpatialFocusViewport({
      bounds: { x: 400, y: 300, width: 200, height: 100 },
      viewportHint: consumed?.viewport_hint,
      surfaceWidth: 1280,
      surfaceHeight: 800,
    })
    expect(centeredVp).toEqual({
      x: 190,
      y: 85,
      zoom: 0.9,
    })

    // Fallback to viewport_hint when node bounds are not yet materialized
    stashPendingSpatialFocus(anchoredTask.spatial_ref!)
    const fallbackVp = computeSpatialFocusViewport({
      bounds: null,
      viewportHint: consumePendingSpatialFocus('sess-space-42')?.viewport_hint,
      surfaceWidth: 1280,
      surfaceHeight: 800,
    })
    expect(fallbackVp).toEqual({
      x: -120,
      y: -80,
      zoom: 0.9,
    })

    page.unmount()
  })

  it('enforces monochrome minimal guardrails with zero border-left colored stripes in Phase 2 components', () => {
    const dir = path.resolve(fileURLToPath(import.meta.url), '..')
    for (const file of [
      'SpatialProjectBoardNode.vue',
      'SpatialTaskStatusCapsule.vue',
      'SpatialSelectionBar.vue',
    ]) {
      const css = readFileSync(path.join(dir, '..', file), 'utf8')
      expect(css).not.toMatch(/border-left\s*:\s*[23]px\s+solid/i)
    }
  })
})
