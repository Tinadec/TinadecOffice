import {
  computed,
  getCurrentScope,
  onScopeDispose,
  ref,
  shallowRef,
  toValue,
  watch,
  type ComputedRef,
  type MaybeRefOrGetter,
  type Ref,
} from 'vue'
import { generateKeyBetween, isValidFractionalKey } from '@tinadec/ui'
import {
  api,
  type ApproveProjectBoardTaskInput,
  type BoardEventStreamHandle,
  type CoordinatorApplyPlanInput,
  type CoordinatorApplyPlanResultDto,
  type CoordinatorPlanInput,
  type CoordinatorPlanResultDto,
  type CreateProjectBoardTaskInput,
  type DispatchProjectBoardTaskInput,
  type ProjectAutopilotStateDto,
  type ProjectBoardAssigneeKind,
  type ProjectBoardConflictResolution,
  type ProjectBoardFiltersDto,
  type ProjectBoardGroupBy,
  type ProjectBoardLayoutMode,
  type ProjectBoardSettingsDto,
  type ProjectBoardSortBy,
  type ProjectBoardSpatialRefDto,
  type ProjectBoardSseEventDto,
  type ProjectBoardSummaryDto,
  type ProjectBoardSwimlaneBy,
  type ProjectBoardSyncResultDto,
  type ProjectBoardTaskDto,
  type ProjectBoardTaskPriority,
  type ProjectBoardTaskStatus,
  type ProjectBoardViewConfigDto,
  type ProjectBoardViewDto,
  type ProjectBoardViewScope,
  type ProjectBoardVisibleField,
  type ProjectNotesDto,
  type PromoteFromSpatialInput,
  type PromoteFromSpatialResultDto,
  type UpdateProjectAutopilotInput,
  type UpdateProjectBoardSettingsInput,
  type UpdateProjectBoardTaskInput,
  type UpdateProjectNotesInput,
} from '@/api'
import { useHostAccess } from '@/lib/hostAccess'
import { selectionIdentity, selectedStorageId } from '@/lib/storageScope'

// ---------------------------------------------------------------------------
// Constants & Phase 2 Spatial Promotion Preference Contract
// ---------------------------------------------------------------------------

export const BOARD_TASK_STATUSES: readonly ProjectBoardTaskStatus[] = [
  'backlog',
  'ready',
  'in_progress',
  'in_review',
  'done',
] as const

export const BOARD_TASK_PRIORITIES: readonly ProjectBoardTaskPriority[] = [
  'urgent',
  'high',
  'medium',
  'low',
  'none',
] as const

export const BOARD_ASSIGNEE_KINDS: readonly ProjectBoardAssigneeKind[] = [
  'human',
  'native',
  'cli',
  'coordinator',
] as const

export const ALL_BOARD_VISIBLE_FIELDS: readonly ProjectBoardVisibleField[] = [
  'project_badge',
  'live_digest',
  'diff_stats',
  'dependencies',
  'conflict_badge',
  'spatial_ref',
  'labels',
  'checklist',
] as const

export const SPATIAL_PROMOTION_PREF_STORAGE_KEY = 'tinadec.projectBoard.spatialPromotePref.v1'
export const PENDING_SPATIAL_FOCUS_STORAGE_KEY = 'tinadec.pendingSpatialFocus'
const BOARD_SNAPSHOT_CACHE_PREFIX = 'tinadec.projectBoard.snapshot.v1:'

export interface PendingSpatialFocusPayload {
  session_id: string
  object_id: string
  viewport_hint?: { x: number; y: number; zoom: number } | Record<string, unknown> | null
}

export function buildSpatialFocusLocation(spatialRef: ProjectBoardSpatialRefDto): {
  path: '/space'
  query: { sessionId: string; focusObjectId: string }
} {
  return {
    path: '/space',
    query: {
      sessionId: spatialRef.session_id,
      focusObjectId: spatialRef.object_id,
    },
  }
}

export function stashPendingSpatialFocus(spatialRef: ProjectBoardSpatialRefDto): void {
  try {
    if (typeof window !== 'undefined' && window.sessionStorage) {
      const payload: PendingSpatialFocusPayload = {
        session_id: spatialRef.session_id,
        object_id: spatialRef.object_id,
        viewport_hint: spatialRef.viewport_hint ?? null,
      }
      window.sessionStorage.setItem(PENDING_SPATIAL_FOCUS_STORAGE_KEY, JSON.stringify(payload))
    }
  } catch {
    // Ignore restricted sessionStorage errors.
  }
}

export function consumePendingSpatialFocus(expectedSessionId?: string | null): PendingSpatialFocusPayload | null {
  try {
    if (typeof window === 'undefined' || !window.sessionStorage) return null
    const raw = window.sessionStorage.getItem(PENDING_SPATIAL_FOCUS_STORAGE_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<PendingSpatialFocusPayload>
    if (typeof parsed.session_id !== 'string' || typeof parsed.object_id !== 'string') {
      window.sessionStorage.removeItem(PENDING_SPATIAL_FOCUS_STORAGE_KEY)
      return null
    }
    if (expectedSessionId && parsed.session_id !== expectedSessionId) {
      return null
    }
    window.sessionStorage.removeItem(PENDING_SPATIAL_FOCUS_STORAGE_KEY)
    return {
      session_id: parsed.session_id,
      object_id: parsed.object_id,
      viewport_hint: parsed.viewport_hint ?? null,
    }
  } catch {
    return null
  }
}

export type SpatialPromotionMode = 'merge_single' | 'split_many' | 'parent_with_children'

export interface SpatialPromotionPreference {
  mode: SpatialPromotionMode
  target_status: ProjectBoardTaskStatus
}

const DEFAULT_SPATIAL_PROMOTION_PREF: SpatialPromotionPreference = {
  mode: 'split_many',
  target_status: 'ready',
}

export function readSpatialPromotionPreference(): SpatialPromotionPreference {
  try {
    if (typeof window === 'undefined' || !window.localStorage) {
      return { ...DEFAULT_SPATIAL_PROMOTION_PREF }
    }
    const raw = window.localStorage.getItem(SPATIAL_PROMOTION_PREF_STORAGE_KEY)
    if (!raw) return { ...DEFAULT_SPATIAL_PROMOTION_PREF }
    const parsed = JSON.parse(raw) as Partial<SpatialPromotionPreference>
    const mode: SpatialPromotionMode =
      parsed.mode === 'merge_single' || parsed.mode === 'split_many' || parsed.mode === 'parent_with_children'
        ? parsed.mode
        : DEFAULT_SPATIAL_PROMOTION_PREF.mode
    const targetStatus: ProjectBoardTaskStatus =
      parsed.target_status && (BOARD_TASK_STATUSES as readonly string[]).includes(parsed.target_status)
        ? parsed.target_status
        : DEFAULT_SPATIAL_PROMOTION_PREF.target_status
    return { mode, target_status: targetStatus }
  } catch {
    return { ...DEFAULT_SPATIAL_PROMOTION_PREF }
  }
}

export function saveSpatialPromotionPreference(pref: SpatialPromotionPreference): SpatialPromotionPreference {
  const normalized: SpatialPromotionPreference = {
    mode:
      pref.mode === 'merge_single' || pref.mode === 'split_many' || pref.mode === 'parent_with_children'
        ? pref.mode
        : DEFAULT_SPATIAL_PROMOTION_PREF.mode,
    target_status: (BOARD_TASK_STATUSES as readonly string[]).includes(pref.target_status)
      ? pref.target_status
      : DEFAULT_SPATIAL_PROMOTION_PREF.target_status,
  }
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      window.localStorage.setItem(SPATIAL_PROMOTION_PREF_STORAGE_KEY, JSON.stringify(normalized))
    }
  } catch {
    // Ignore storage quota errors in restricted webviews.
  }
  return normalized
}

// ---------------------------------------------------------------------------
// Pure Helpers: Filtering, Sorting, Grouping, Swimlanes & Fractional Indexing
// ---------------------------------------------------------------------------

const PRIORITY_RANK: Record<ProjectBoardTaskPriority, number> = {
  urgent: 0,
  high: 1,
  medium: 2,
  low: 3,
  none: 4,
}

export function isTaskNeedsAction(task: ProjectBoardTaskDto): boolean {
  return (
    task.status === 'in_review' ||
    task.blocked ||
    task.has_sync_conflict ||
    task.run_status === 'awaiting_approval' ||
    task.run_status === 'awaiting_user'
  )
}

export function defaultVisibleFieldsForScope(scope: ProjectBoardViewScope): ProjectBoardVisibleField[] {
  return scope === 'global'
    ? [...ALL_BOARD_VISIBLE_FIELDS]
    : ALL_BOARD_VISIBLE_FIELDS.filter((field) => field !== 'project_badge')
}

export function createDefaultBoardFilters(): ProjectBoardFiltersDto {
  return {
    project_ids: [],
    assignee_kinds: [],
    statuses: [],
    priorities: [],
    labels: [],
    needs_my_action: false,
    has_conflict: false,
    query: '',
  }
}

export function createDefaultViewConfig(scope: ProjectBoardViewScope): ProjectBoardViewConfigDto {
  return {
    layout: 'kanban',
    group_by: 'status',
    swimlane_by: scope === 'global' ? 'project' : 'assignee',
    sort_by: 'manual',
    filters: createDefaultBoardFilters(),
    visible_fields: defaultVisibleFieldsForScope(scope),
  }
}

export function cloneViewConfig(
  config: Partial<ProjectBoardViewConfigDto> | null | undefined,
  scope: ProjectBoardViewScope,
): ProjectBoardViewConfigDto {
  const defaults = createDefaultViewConfig(scope)
  const rawFilters = config?.filters ?? {}
  return {
    layout:
      config?.layout === 'kanban' || config?.layout === 'list' || config?.layout === 'swimlane'
        ? config.layout
        : defaults.layout,
    group_by:
      config?.group_by === 'status' ||
      config?.group_by === 'project' ||
      config?.group_by === 'assignee' ||
      config?.group_by === 'priority' ||
      config?.group_by === 'none'
        ? config.group_by
        : defaults.group_by,
    swimlane_by:
      config?.swimlane_by === 'project' || config?.swimlane_by === 'assignee'
        ? config.swimlane_by
        : defaults.swimlane_by,
    sort_by:
      config?.sort_by === 'manual' ||
      config?.sort_by === 'priority' ||
      config?.sort_by === 'updated_at' ||
      config?.sort_by === 'dependency'
        ? config.sort_by
        : defaults.sort_by,
    filters: {
      project_ids: Array.isArray(rawFilters.project_ids) ? [...rawFilters.project_ids] : [],
      assignee_kinds: Array.isArray(rawFilters.assignee_kinds) ? [...rawFilters.assignee_kinds] : [],
      statuses: Array.isArray(rawFilters.statuses) ? [...rawFilters.statuses] : [],
      priorities: Array.isArray(rawFilters.priorities) ? [...rawFilters.priorities] : [],
      labels: Array.isArray(rawFilters.labels) ? [...rawFilters.labels] : [],
      needs_my_action: Boolean(rawFilters.needs_my_action),
      has_conflict: Boolean(rawFilters.has_conflict),
      query: typeof rawFilters.query === 'string' ? rawFilters.query : '',
    },
    visible_fields: Array.isArray(config?.visible_fields)
      ? [...config.visible_fields]
      : defaults.visible_fields,
  }
}

export function filterBoardTasks(
  tasks: readonly ProjectBoardTaskDto[],
  filters: ProjectBoardFiltersDto,
): ProjectBoardTaskDto[] {
  const projectSet = filters.project_ids?.length ? new Set(filters.project_ids) : null
  const assigneeSet = filters.assignee_kinds?.length ? new Set(filters.assignee_kinds) : null
  const statusSet = filters.statuses?.length ? new Set(filters.statuses) : null
  const prioritySet = filters.priorities?.length ? new Set(filters.priorities) : null
  const labelSet = filters.labels?.length ? new Set(filters.labels.map((l) => l.toLowerCase())) : null
  const needle = (filters.query ?? '').trim().toLowerCase()

  return tasks.filter((task) => {
    if (projectSet && !projectSet.has(task.project_id)) return false
    if (assigneeSet) {
      const kind = task.assignee?.kind ?? 'human'
      if (!assigneeSet.has(kind)) return false
    }
    if (statusSet && !statusSet.has(task.status)) return false
    if (prioritySet && !prioritySet.has(task.priority)) return false
    if (labelSet) {
      const matched = task.labels.some((label) => labelSet.has(label.toLowerCase()))
      if (!matched) return false
    }
    if (filters.needs_my_action && !isTaskNeedsAction(task)) return false
    if (filters.has_conflict && !task.has_sync_conflict) return false
    if (needle) {
      const haystack = [
        task.task_key,
        task.title,
        task.description_md,
        task.project_name ?? '',
        task.assignee?.display_name ?? '',
        task.assignee?.harness_id ?? '',
        ...task.labels,
      ]
        .join(' ')
        .toLowerCase()
      if (!haystack.includes(needle)) return false
    }
    return true
  })
}

function computeDependencyDepthMap(tasks: readonly ProjectBoardTaskDto[]): Map<string, number> {
  const byId = new Map<string, ProjectBoardTaskDto>()
  for (const task of tasks) byId.set(task.task_id, task)
  const memo = new Map<string, number>()
  const visiting = new Set<string>()

  function depthOf(taskId: string): number {
    if (memo.has(taskId)) return memo.get(taskId)!
    if (visiting.has(taskId)) return 0
    const task = byId.get(taskId)
    if (!task) return 0
    visiting.add(taskId)
    let maxDep = -1
    for (const depId of task.depends_on) {
      if (byId.has(depId)) {
        maxDep = Math.max(maxDep, depthOf(depId))
      }
    }
    if (task.parent_task_id && byId.has(task.parent_task_id)) {
      maxDep = Math.max(maxDep, depthOf(task.parent_task_id))
    }
    visiting.delete(taskId)
    const resolved = maxDep + 1
    memo.set(taskId, resolved)
    return resolved
  }

  for (const task of tasks) depthOf(task.task_id)
  return memo
}

function compareSortKeys(a: ProjectBoardTaskDto, b: ProjectBoardTaskDto): number {
  if (a.sort_key !== b.sort_key) {
    return a.sort_key < b.sort_key ? -1 : 1
  }
  if (a.created_at_unix_ms !== b.created_at_unix_ms) {
    return a.created_at_unix_ms - b.created_at_unix_ms
  }
  return a.task_id.localeCompare(b.task_id)
}

export function sortBoardTasks(
  tasks: readonly ProjectBoardTaskDto[],
  sortBy: ProjectBoardSortBy,
): ProjectBoardTaskDto[] {
  const copy = [...tasks]
  if (sortBy === 'manual') {
    return copy.sort(compareSortKeys)
  }
  if (sortBy === 'priority') {
    return copy.sort((a, b) => {
      const diff = (PRIORITY_RANK[a.priority] ?? 4) - (PRIORITY_RANK[b.priority] ?? 4)
      return diff !== 0 ? diff : compareSortKeys(a, b)
    })
  }
  if (sortBy === 'updated_at') {
    return copy.sort((a, b) => {
      const diff = b.updated_at_unix_ms - a.updated_at_unix_ms
      return diff !== 0 ? diff : compareSortKeys(a, b)
    })
  }
  const depths = computeDependencyDepthMap(copy)
  return copy.sort((a, b) => {
    const depthDiff = (depths.get(a.task_id) ?? 0) - (depths.get(b.task_id) ?? 0)
    if (depthDiff !== 0) return depthDiff
    const prioDiff = (PRIORITY_RANK[a.priority] ?? 4) - (PRIORITY_RANK[b.priority] ?? 4)
    return prioDiff !== 0 ? prioDiff : compareSortKeys(a, b)
  })
}

function sanitizeFractionalBound(key: string | null | undefined): string | null {
  if (!key || !isValidFractionalKey(key)) return null
  // A bound consisting purely of '0's cannot have a key generated strictly below it.
  if (/^0+$/.test(key)) return null
  return key
}

/**
 * Computes a Base-62 Fractional Indexing `sort_key` placing `movedTaskId` at `targetIndex`
 * among `orderedTasks` (excluding `movedTaskId` first so within-column moves calculate exact bounds).
 */
export function computeReorderSortKey(
  orderedTasks: readonly Pick<ProjectBoardTaskDto, 'task_id' | 'sort_key'>[],
  movedTaskId: string,
  targetIndex: number,
): string {
  const peers = orderedTasks.filter((t) => t.task_id !== movedTaskId)
  if (peers.length === 0) {
    return generateKeyBetween(null, null)
  }
  const clampedIndex = Math.max(0, Math.min(targetIndex, peers.length))
  const prevRaw = clampedIndex > 0 ? peers[clampedIndex - 1]?.sort_key : null
  const nextRaw = clampedIndex < peers.length ? peers[clampedIndex]?.sort_key : null

  const lo = sanitizeFractionalBound(prevRaw)
  const hi = sanitizeFractionalBound(nextRaw)

  if (lo !== null && hi !== null) {
    if (lo < hi) {
      try {
        return generateKeyBetween(lo, hi)
      } catch {
        return generateKeyBetween(lo, null)
      }
    }
    // Defensive fallback if peers arrived with equal/inverted sort_key from external edit.
    return generateKeyBetween(lo, null)
  }
  try {
    return generateKeyBetween(lo, hi)
  } catch {
    return generateKeyBetween(null, null)
  }
}

export interface BoardTaskGroup {
  id: string
  key: string
  kind: ProjectBoardGroupBy
  labelKey?: string
  labelFallback: string
  tasks: ProjectBoardTaskDto[]
}

export interface BoardSwimlaneRow {
  id: string
  key: string
  kind: ProjectBoardSwimlaneBy
  title: string
  projectId?: string
  assigneeKind?: ProjectBoardAssigneeKind
  totalCount: number
  cells: Record<ProjectBoardTaskStatus, ProjectBoardTaskDto[]>
}

export function groupBoardTasks(
  tasks: readonly ProjectBoardTaskDto[],
  groupBy: ProjectBoardGroupBy,
  sortBy: ProjectBoardSortBy,
  projectNames: ReadonlyMap<string, string> = new Map(),
): BoardTaskGroup[] {
  if (groupBy === 'none') {
    return [
      {
        id: 'none:all',
        key: 'all',
        kind: 'none',
        labelKey: 'board.group.allTasks',
        labelFallback: 'All Tasks',
        tasks: sortBoardTasks(tasks, sortBy),
      },
    ]
  }

  if (groupBy === 'status') {
    return BOARD_TASK_STATUSES.map((status) => ({
      id: `status:${status}`,
      key: status,
      kind: 'status',
      labelKey: `board.status.${status}`,
      labelFallback: status,
      tasks: sortBoardTasks(
        tasks.filter((t) => t.status === status),
        sortBy,
      ),
    }))
  }

  if (groupBy === 'priority') {
    return BOARD_TASK_PRIORITIES.map((priority) => ({
      id: `priority:${priority}`,
      key: priority,
      kind: 'priority',
      labelKey: `board.priority.${priority}`,
      labelFallback: priority,
      tasks: sortBoardTasks(
        tasks.filter((t) => t.priority === priority),
        sortBy,
      ),
    }))
  }

  if (groupBy === 'assignee') {
    return BOARD_ASSIGNEE_KINDS.map((kind) => ({
      id: `assignee:${kind}`,
      key: kind,
      kind: 'assignee',
      labelKey: `board.assignee.${kind}`,
      labelFallback: kind,
      tasks: sortBoardTasks(
        tasks.filter((t) => (t.assignee?.kind ?? 'human') === kind),
        sortBy,
      ),
    }))
  }

  // groupBy === 'project'
  const byProject = new Map<string, ProjectBoardTaskDto[]>()
  for (const [projectId] of projectNames) {
    byProject.set(projectId, [])
  }
  for (const task of tasks) {
    const list = byProject.get(task.project_id) ?? []
    list.push(task)
    byProject.set(task.project_id, list)
  }

  return [...byProject.entries()].map(([projectId, items]) => ({
    id: `project:${projectId}`,
    key: projectId,
    kind: 'project',
    labelFallback: items[0]?.project_name || projectNames.get(projectId) || projectId.slice(0, 8),
    tasks: sortBoardTasks(items, sortBy),
  }))
}

export function buildBoardSwimlanes(
  tasks: readonly ProjectBoardTaskDto[],
  swimlaneBy: ProjectBoardSwimlaneBy,
  sortBy: ProjectBoardSortBy,
  projectNames: ReadonlyMap<string, string> = new Map(),
): BoardSwimlaneRow[] {
  const emptyCells = (): Record<ProjectBoardTaskStatus, ProjectBoardTaskDto[]> => ({
    backlog: [],
    ready: [],
    in_progress: [],
    in_review: [],
    done: [],
  })

  if (swimlaneBy === 'assignee') {
    return BOARD_ASSIGNEE_KINDS.map((kind) => {
      const subset = tasks.filter((t) => (t.assignee?.kind ?? 'human') === kind)
      const cells = emptyCells()
      for (const status of BOARD_TASK_STATUSES) {
        cells[status] = sortBoardTasks(
          subset.filter((t) => t.status === status),
          sortBy,
        )
      }
      return {
        id: `lane:assignee:${kind}`,
        key: kind,
        kind: 'assignee',
        title: kind,
        assigneeKind: kind,
        totalCount: subset.length,
        cells,
      }
    })
  }

  // swimlaneBy === 'project'
  const projectOrder = new Map<string, string>()
  for (const [id, name] of projectNames) {
    projectOrder.set(id, name)
  }
  for (const task of tasks) {
    if (!projectOrder.has(task.project_id)) {
      projectOrder.set(task.project_id, task.project_name || task.project_id.slice(0, 8))
    }
  }

  return [...projectOrder.entries()].map(([projectId, name]) => {
    const subset = tasks.filter((t) => t.project_id === projectId)
    const cells = emptyCells()
    for (const status of BOARD_TASK_STATUSES) {
      cells[status] = sortBoardTasks(
        subset.filter((t) => t.status === status),
        sortBy,
      )
    }
    return {
      id: `lane:project:${projectId}`,
      key: projectId,
      kind: 'project',
      title: subset[0]?.project_name || name,
      projectId,
      totalCount: subset.length,
      cells,
    }
  })
}

// ---------------------------------------------------------------------------
// Scoped Local Snapshot Cache (Memory + localStorage fallback)
// ---------------------------------------------------------------------------

interface CachedBoardSnapshot {
  tasks: ProjectBoardTaskDto[]
  views: ProjectBoardViewDto[]
  settings: ProjectBoardSettingsDto | null
  summary: ProjectBoardSummaryDto | null
  notes?: ProjectNotesDto | null
  autopilot?: ProjectAutopilotStateDto | null
  savedAtUnixMs: number
}

const memorySnapshots = new Map<string, CachedBoardSnapshot>()

function snapshotCacheKey(scope: ProjectBoardViewScope, projectId: string | null): string {
  const storage = selectedStorageId()
  return `${BOARD_SNAPSHOT_CACHE_PREFIX}${storage}:${scope}:${projectId ?? 'global'}`
}

function readCachedBoardSnapshot(scope: ProjectBoardViewScope, projectId: string | null): CachedBoardSnapshot | null {
  const key = snapshotCacheKey(scope, projectId)
  const mem = memorySnapshots.get(key)
  if (mem) return mem
  try {
    if (typeof window === 'undefined' || !window.localStorage) return null
    const raw = window.localStorage.getItem(key)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<CachedBoardSnapshot>
    if (!parsed || !Array.isArray(parsed.tasks)) return null
    const hydrated: CachedBoardSnapshot = {
      tasks: parsed.tasks,
      views: Array.isArray(parsed.views) ? parsed.views : [],
      settings: parsed.settings ?? null,
      summary: parsed.summary ?? null,
      notes: parsed.notes ?? null,
      autopilot: parsed.autopilot ?? null,
      savedAtUnixMs: typeof parsed.savedAtUnixMs === 'number' ? parsed.savedAtUnixMs : Date.now(),
    }
    memorySnapshots.set(key, hydrated)
    return hydrated
  } catch {
    return null
  }
}

function writeCachedBoardSnapshot(
  scope: ProjectBoardViewScope,
  projectId: string | null,
  snapshot: CachedBoardSnapshot,
): void {
  const key = snapshotCacheKey(scope, projectId)
  memorySnapshots.set(key, snapshot)
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      window.localStorage.setItem(key, JSON.stringify(snapshot))
    }
  } catch {
    // Ignore storage quota errors.
  }
}

function emitBoardSummaryBroadcast(needsActionCount?: number): void {
  if (typeof window === 'undefined') return
  window.dispatchEvent(
    new CustomEvent('tinadec:board-summary-changed', {
      detail: needsActionCount !== undefined ? { needs_action_count: needsActionCount } : {},
    }),
  )
}

// ---------------------------------------------------------------------------
// Composable: `useProjectBoardView`
// ---------------------------------------------------------------------------

export interface UseProjectBoardViewOptions {
  scope: ProjectBoardViewScope
  projectId?: MaybeRefOrGetter<string | null>
  projects?: MaybeRefOrGetter<Array<{ id: string; name: string }>>
  autoConnect?: boolean
  initialConfig?: Partial<ProjectBoardViewConfigDto>
}

export interface UseProjectBoardViewReturn {
  scope: ProjectBoardViewScope
  resolvedProjectId: ComputedRef<string | null>
  tasks: Ref<ProjectBoardTaskDto[]>
  filteredTasks: ComputedRef<ProjectBoardTaskDto[]>
  groups: ComputedRef<BoardTaskGroup[]>
  swimlanes: ComputedRef<BoardSwimlaneRow[]>
  availableLabels: ComputedRef<string[]>
  savedViews: Ref<ProjectBoardViewDto[]>
  activeViewId: Ref<string | null>
  settings: Ref<ProjectBoardSettingsDto | null>
  summary: Ref<ProjectBoardSummaryDto | null>
  notes: Ref<ProjectNotesDto | null>
  autopilot: Ref<ProjectAutopilotStateDto | null>
  layout: Ref<ProjectBoardLayoutMode>
  groupBy: Ref<ProjectBoardGroupBy>
  swimlaneBy: Ref<ProjectBoardSwimlaneBy>
  sortBy: Ref<ProjectBoardSortBy>
  filters: Ref<ProjectBoardFiltersDto>
  visibleFields: Ref<ProjectBoardVisibleField[]>
  spatialPromotionPref: Ref<SpatialPromotionPreference>
  loading: Ref<boolean>
  syncing: Ref<boolean>
  stale: Ref<boolean>
  connected: Ref<boolean>
  error: Ref<string | null>
  activeFilterCount: ComputedRef<number>
  boardStats: ComputedRef<{
    total: number
    backlog: number
    ready: number
    inProgress: number
    inReview: number
    done: number
    blocked: number
    conflicts: number
    needsAction: number
  }>
  isFieldVisible: (field: ProjectBoardVisibleField) => boolean
  toggleVisibleField: (field: ProjectBoardVisibleField) => void
  applyViewConfig: (config: Partial<ProjectBoardViewConfigDto>) => void
  selectSavedView: (viewId: string | null) => void
  applySwimlaneByProjectPreset: () => void
  clearFilters: () => void
  setSpatialPromotionPref: (pref: SpatialPromotionPreference) => void
  refresh: () => Promise<void>
  syncBoard: () => Promise<ProjectBoardSyncResultDto | null>
  saveCurrentAsView: (name: string) => Promise<ProjectBoardViewDto>
  updateActiveSavedView: () => Promise<ProjectBoardViewDto | null>
  deleteSavedView: (viewId: string) => Promise<void>
  createTask: (projectId: string, input: CreateProjectBoardTaskInput) => Promise<ProjectBoardTaskDto>
  promoteFromSpatial: (
    projectId: string,
    input: PromoteFromSpatialInput,
  ) => Promise<PromoteFromSpatialResultDto>
  upsertTaskLocal: (incoming: ProjectBoardTaskDto) => void
  updateTask: (projectId: string, taskId: string, patch: UpdateProjectBoardTaskInput) => Promise<ProjectBoardTaskDto>
  deleteTask: (projectId: string, taskId: string) => Promise<void>
  moveTask: (
    task: ProjectBoardTaskDto,
    groupPeers: readonly ProjectBoardTaskDto[],
    targetIndex: number,
    patch?: UpdateProjectBoardTaskInput,
  ) => Promise<ProjectBoardTaskDto>
  resolveConflict: (
    projectId: string,
    taskId: string,
    resolution: ProjectBoardConflictResolution,
  ) => Promise<ProjectBoardTaskDto>
  dispatchTask: (
    projectId: string,
    taskId: string,
    input?: DispatchProjectBoardTaskInput,
  ) => Promise<ProjectBoardTaskDto>
  cancelTask: (projectId: string, taskId: string) => Promise<ProjectBoardTaskDto>
  approveTask: (
    projectId: string,
    taskId: string,
    input?: ApproveProjectBoardTaskInput,
  ) => Promise<ProjectBoardTaskDto>
  requestChanges: (projectId: string, taskId: string, feedback: string) => Promise<ProjectBoardTaskDto>
  saveSettings: (input: UpdateProjectBoardSettingsInput) => Promise<ProjectBoardSettingsDto | null>
  reloadNotes: () => Promise<ProjectNotesDto | null>
  saveNotes: (input: UpdateProjectNotesInput) => Promise<ProjectNotesDto | null>
  planCoordinator: (input: CoordinatorPlanInput) => Promise<CoordinatorPlanResultDto | null>
  applyCoordinatorPlan: (input: CoordinatorApplyPlanInput) => Promise<CoordinatorApplyPlanResultDto | null>
  reloadAutopilot: () => Promise<ProjectAutopilotStateDto | null>
  updateAutopilot: (input: UpdateProjectAutopilotInput) => Promise<ProjectAutopilotStateDto | null>
  applySseEvent: (event: ProjectBoardSseEventDto) => void
  disconnectStream: () => void
}

export function useProjectBoardView(options: UseProjectBoardViewOptions): UseProjectBoardViewReturn {
  const { scope } = options
  const { canAccessBackend } = useHostAccess()

  const resolvedProjectId = computed<string | null>(() => {
    if (scope === 'global') return null
    const raw = toValue(options.projectId)
    return raw ? selectionIdentity(raw).id : null
  })

  const projectNamesMap = computed<ReadonlyMap<string, string>>(() => {
    const map = new Map<string, string>()
    const list = toValue(options.projects) ?? []
    for (const item of list) {
      map.set(selectionIdentity(item.id).id, item.name)
    }
    return map
  })

  const initialCfg = cloneViewConfig(options.initialConfig, scope)
  const layout = ref<ProjectBoardLayoutMode>(initialCfg.layout)
  const groupBy = ref<ProjectBoardGroupBy>(initialCfg.group_by)
  const swimlaneBy = ref<ProjectBoardSwimlaneBy>(
    initialCfg.swimlane_by ?? (scope === 'global' ? 'project' : 'assignee'),
  )
  const sortBy = ref<ProjectBoardSortBy>(initialCfg.sort_by)
  const filters = ref<ProjectBoardFiltersDto>(initialCfg.filters)
  const visibleFields = ref<ProjectBoardVisibleField[]>(initialCfg.visible_fields)
  const spatialPromotionPref = ref<SpatialPromotionPreference>(readSpatialPromotionPreference())

  const tasks = ref<ProjectBoardTaskDto[]>([])
  const savedViews = ref<ProjectBoardViewDto[]>([])
  const activeViewId = ref<string | null>(null)
  const settings = shallowRef<ProjectBoardSettingsDto | null>(null)
  const summary = shallowRef<ProjectBoardSummaryDto | null>(null)
  const notes = shallowRef<ProjectNotesDto | null>(null)
  const autopilot = shallowRef<ProjectAutopilotStateDto | null>(null)

  const loading = ref(false)
  const syncing = ref(false)
  const stale = ref(false)
  const connected = ref(false)
  const error = ref<string | null>(null)

  let streamHandle: BoardEventStreamHandle | null = null
  let loadGeneration = 0

  function resetScopedState(): void {
    tasks.value = []
    savedViews.value = []
    activeViewId.value = null
    settings.value = null
    summary.value = null
    notes.value = null
    autopilot.value = null
    error.value = null
    stale.value = false
  }

  function hydrateFromCache(pid: string | null): void {
    const cached = readCachedBoardSnapshot(scope, pid)
    if (!cached) return
    tasks.value = cached.tasks
    savedViews.value = cached.views
    settings.value = cached.settings
    summary.value = cached.summary
    notes.value = cached.notes ?? null
    autopilot.value = cached.autopilot ?? null
  }

  function persistCache(): void {
    writeCachedBoardSnapshot(scope, resolvedProjectId.value, {
      tasks: tasks.value,
      views: savedViews.value,
      settings: settings.value,
      summary: summary.value,
      notes: notes.value,
      autopilot: autopilot.value,
      savedAtUnixMs: Date.now(),
    })
  }

  function upsertTaskLocal(incoming: ProjectBoardTaskDto): void {
    const idx = tasks.value.findIndex((t) => t.task_id === incoming.task_id)
    if (idx >= 0) {
      const existing = tasks.value[idx]!
      if (incoming.revision >= existing.revision || incoming.updated_at_unix_ms >= existing.updated_at_unix_ms) {
        const next = [...tasks.value]
        next[idx] = incoming
        tasks.value = next
      }
    } else {
      tasks.value = [...tasks.value, incoming]
    }
    persistCache()
  }

  function removeTaskLocal(taskId: string): void {
    tasks.value = tasks.value.filter((t) => t.task_id !== taskId)
    persistCache()
  }

  function applySseEvent(event: ProjectBoardSseEventDto): void {
    stale.value = false
    if (event.event_type === 'snapshot') {
      if (Array.isArray(event.tasks)) tasks.value = event.tasks
      if (Array.isArray(event.views)) savedViews.value = event.views
      if (event.settings) settings.value = event.settings
      if (event.summary) summary.value = event.summary
      if (event.notes) notes.value = event.notes
      if (event.autopilot) autopilot.value = event.autopilot
      persistCache()
    } else if (event.event_type === 'task_upserted' && event.task) {
      if (scope === 'global' || event.task.project_id === resolvedProjectId.value) {
        upsertTaskLocal(event.task)
      }
    } else if (event.event_type === 'task_deleted' && event.task_id) {
      removeTaskLocal(event.task_id)
    } else if (event.event_type === 'settings_updated' && event.settings) {
      if (scope === 'project' && event.settings.project_id === resolvedProjectId.value) {
        settings.value = event.settings
        persistCache()
      }
    } else if (event.event_type === 'notes_updated' && event.notes) {
      const targetPid = event.notes.project_id || event.project_id
      if (scope === 'project' && (!targetPid || targetPid === resolvedProjectId.value)) {
        notes.value = event.notes
        persistCache()
      }
    } else if (event.event_type === 'autopilot_updated' && event.autopilot) {
      const targetPid = event.autopilot.project_id || event.project_id
      if (scope === 'project' && (!targetPid || targetPid === resolvedProjectId.value)) {
        autopilot.value = event.autopilot
        if (settings.value) {
          settings.value = {
            ...settings.value,
            autopilot_enabled: event.autopilot.autopilot_enabled,
            max_concurrency: event.autopilot.max_concurrency,
          }
        }
        for (const blockedTask of event.autopilot.blocked_tasks) {
          if (!blockedTask.project_id || blockedTask.project_id === resolvedProjectId.value) {
            upsertTaskLocal(blockedTask)
          }
        }
        persistCache()
      }
    } else if (event.event_type === 'view_upserted' && event.view) {
      const idx = savedViews.value.findIndex((v) => v.view_id === event.view!.view_id)
      if (idx >= 0) {
        const next = [...savedViews.value]
        next[idx] = event.view
        savedViews.value = next
      } else {
        savedViews.value = [...savedViews.value, event.view]
      }
      persistCache()
    } else if (event.event_type === 'view_deleted' && event.view_id) {
      savedViews.value = savedViews.value.filter((v) => v.view_id !== event.view_id)
      if (activeViewId.value === event.view_id) activeViewId.value = null
      persistCache()
    } else if (event.event_type === 'sync_completed') {
      void refresh()
    }
    if (event.summary) {
      summary.value = event.summary
      emitBoardSummaryBroadcast(event.summary.needs_action_count)
    } else {
      emitBoardSummaryBroadcast()
    }
  }

  function disconnectStream(): void {
    streamHandle?.close()
    streamHandle = null
    connected.value = false
  }

  function connectStream(): void {
    disconnectStream()
    if (options.autoConnect === false || !canAccessBackend.value) return
    if (scope === 'project' && !resolvedProjectId.value) return

    const streamOpts = {
      onOpen: () => {
        connected.value = true
        stale.value = false
      },
      onError: () => {
        connected.value = false
        if (tasks.value.length > 0) stale.value = true
      },
    }

    streamHandle =
      scope === 'global'
        ? api.subscribeGlobalBoardEvents(applySseEvent, streamOpts)
        : api.subscribeProjectBoardEvents(resolvedProjectId.value!, applySseEvent, streamOpts)
  }

  async function refresh(): Promise<void> {
    const pid = resolvedProjectId.value
      if (scope === 'project' && !pid) {
        tasks.value = []
        settings.value = null
        notes.value = null
        autopilot.value = null
        return
      }
    if (!canAccessBackend.value) {
      hydrateFromCache(pid)
      if (tasks.value.length > 0) stale.value = true
      return
    }

    const gen = ++loadGeneration
    loading.value = true
    error.value = null

    try {
      if (scope === 'global') {
        const [loadedTasks, loadedSummary, loadedViews] = await Promise.all([
          api.listGlobalBoardTasks(),
          api.getGlobalBoardSummary().catch(() => null),
          api.listBoardViews({ scope: 'global' }).catch(() => [] as ProjectBoardViewDto[]),
        ])
        if (gen !== loadGeneration) return
        tasks.value = loadedTasks
        if (loadedSummary) {
          summary.value = loadedSummary
          emitBoardSummaryBroadcast(loadedSummary.needs_action_count)
        }
        savedViews.value = loadedViews
      } else {
        const [loadedTasks, loadedSettings, loadedViews, loadedNotes, loadedAutopilot, loadedSummary] = await Promise.all([
          api.listProjectBoardTasks(pid!),
          api.getProjectBoardSettings(pid!).catch(() => null),
          api.listBoardViews({ scope: 'project', project_id: pid! }).catch(() => [] as ProjectBoardViewDto[]),
          api.getProjectNotes(pid!).catch(() => null),
          api.getProjectAutopilot(pid!).catch(() => null),
          api.getGlobalBoardSummary().catch(() => null),
        ])
        if (gen !== loadGeneration) return
        tasks.value = loadedTasks
        if (loadedSettings) settings.value = loadedSettings
        savedViews.value = loadedViews
        if (loadedNotes) notes.value = loadedNotes
        if (loadedAutopilot) autopilot.value = loadedAutopilot
        if (loadedSummary) {
          summary.value = loadedSummary
          emitBoardSummaryBroadcast(loadedSummary.needs_action_count)
        }
      }
      stale.value = false
      persistCache()
    } catch (err) {
      if (gen !== loadGeneration) return
      error.value = err instanceof Error ? err.message : String(err)
      hydrateFromCache(pid)
      stale.value = true
    } finally {
      if (gen === loadGeneration) loading.value = false
    }
  }

  const filteredTasks = computed(() => filterBoardTasks(tasks.value, filters.value))

  const groups = computed(() =>
    groupBoardTasks(filteredTasks.value, groupBy.value, sortBy.value, projectNamesMap.value),
  )

  const swimlanes = computed(() =>
    buildBoardSwimlanes(filteredTasks.value, swimlaneBy.value, sortBy.value, projectNamesMap.value),
  )

  const availableLabels = computed(() => {
    const set = new Set<string>()
    for (const task of tasks.value) {
      for (const label of task.labels) {
        if (label.trim()) set.add(label.trim())
      }
    }
    return [...set].sort((a, b) => a.localeCompare(b))
  })

  const activeFilterCount = computed(() => {
    const f = filters.value
    let count = 0
    if (f.project_ids?.length) count += 1
    if (f.assignee_kinds?.length) count += 1
    if (f.statuses?.length) count += 1
    if (f.priorities?.length) count += 1
    if (f.labels?.length) count += 1
    if (f.needs_my_action) count += 1
    if (f.has_conflict) count += 1
    if (f.query?.trim()) count += 1
    return count
  })

  const boardStats = computed(() => {
    let backlog = 0
    let ready = 0
    let inProgress = 0
    let inReview = 0
    let done = 0
    let blocked = 0
    let conflicts = 0
    let needsAction = 0
    for (const task of tasks.value) {
      if (task.status === 'backlog') backlog += 1
      else if (task.status === 'ready') ready += 1
      else if (task.status === 'in_progress') inProgress += 1
      else if (task.status === 'in_review') inReview += 1
      else if (task.status === 'done') done += 1
      if (task.blocked) blocked += 1
      if (task.has_sync_conflict) conflicts += 1
      if (isTaskNeedsAction(task)) needsAction += 1
    }
    return {
      total: tasks.value.length,
      backlog,
      ready,
      inProgress,
      inReview,
      done,
      blocked,
      conflicts,
      needsAction,
    }
  })

  function isFieldVisible(field: ProjectBoardVisibleField): boolean {
    return visibleFields.value.includes(field)
  }

  function toggleVisibleField(field: ProjectBoardVisibleField): void {
    if (visibleFields.value.includes(field)) {
      visibleFields.value = visibleFields.value.filter((f) => f !== field)
    } else {
      visibleFields.value = [...visibleFields.value, field]
    }
  }

  function currentConfigSnapshot(): ProjectBoardViewConfigDto {
    return cloneViewConfig(
      {
        layout: layout.value,
        group_by: groupBy.value,
        swimlane_by: swimlaneBy.value,
        sort_by: sortBy.value,
        filters: filters.value,
        visible_fields: visibleFields.value,
      },
      scope,
    )
  }

  function applyViewConfig(config: Partial<ProjectBoardViewConfigDto>): void {
    const normalized = cloneViewConfig(config, scope)
    layout.value = normalized.layout
    groupBy.value = normalized.group_by
    swimlaneBy.value = normalized.swimlane_by ?? (scope === 'global' ? 'project' : 'assignee')
    sortBy.value = normalized.sort_by
    filters.value = normalized.filters
    visibleFields.value = normalized.visible_fields
  }

  function selectSavedView(viewId: string | null): void {
    activeViewId.value = viewId
    if (!viewId) {
      applyViewConfig(createDefaultViewConfig(scope))
      return
    }
    const target = savedViews.value.find((v) => v.view_id === viewId)
    if (target) {
      applyViewConfig(target.config)
    }
  }

  function applySwimlaneByProjectPreset(): void {
    activeViewId.value = null
    layout.value = 'swimlane'
    swimlaneBy.value = 'project'
    groupBy.value = 'status'
  }

  function clearFilters(): void {
    filters.value = createDefaultBoardFilters()
  }

  function setSpatialPromotionPref(pref: SpatialPromotionPreference): void {
    spatialPromotionPref.value = saveSpatialPromotionPreference(pref)
  }

  async function saveCurrentAsView(name: string): Promise<ProjectBoardViewDto> {
    const trimmed = name.trim()
    const created = await api.createBoardView({
      scope,
      project_id: scope === 'project' ? resolvedProjectId.value : null,
      name: trimmed,
      config: currentConfigSnapshot(),
      sort_order: savedViews.value.length,
    })
    savedViews.value = [...savedViews.value.filter((v) => v.view_id !== created.view_id), created]
    activeViewId.value = created.view_id
    persistCache()
    return created
  }

  async function updateActiveSavedView(): Promise<ProjectBoardViewDto | null> {
    if (!activeViewId.value) return null
    const updated = await api.updateBoardView(
      activeViewId.value,
      { config: currentConfigSnapshot() },
      { projectId: resolvedProjectId.value },
    )
    savedViews.value = savedViews.value.map((v) => (v.view_id === updated.view_id ? updated : v))
    persistCache()
    return updated
  }

  async function deleteSavedView(viewId: string): Promise<void> {
    await api.deleteBoardView(viewId, { projectId: resolvedProjectId.value })
    savedViews.value = savedViews.value.filter((v) => v.view_id !== viewId)
    if (activeViewId.value === viewId) {
      activeViewId.value = null
    }
    persistCache()
  }

  async function syncBoard(): Promise<ProjectBoardSyncResultDto | null> {
    const pid = resolvedProjectId.value
    if (!pid) return null
    syncing.value = true
    try {
      const res = await api.syncProjectBoard(pid)
      await refresh()
      emitBoardSummaryBroadcast()
      return res
    } finally {
      syncing.value = false
    }
  }

  async function createTask(
    projectId: string,
    input: CreateProjectBoardTaskInput,
  ): Promise<ProjectBoardTaskDto> {
    const created = await api.createProjectBoardTask(projectId, input)
    upsertTaskLocal(created)
    emitBoardSummaryBroadcast()
    return created
  }

  async function promoteFromSpatial(
    projectId: string,
    input: PromoteFromSpatialInput,
  ): Promise<PromoteFromSpatialResultDto> {
    const result = await api.promoteSpatialToBoard(projectId, input)
    for (const task of result.tasks) {
      upsertTaskLocal(task)
    }
    emitBoardSummaryBroadcast()
    return result
  }

  async function updateTask(
    projectId: string,
    taskId: string,
    patch: UpdateProjectBoardTaskInput,
  ): Promise<ProjectBoardTaskDto> {
    const updated = await api.updateProjectBoardTask(projectId, taskId, patch)
    upsertTaskLocal(updated)
    emitBoardSummaryBroadcast()
    return updated
  }

  async function deleteTask(projectId: string, taskId: string): Promise<void> {
    await api.deleteProjectBoardTask(projectId, taskId)
    removeTaskLocal(taskId)
    emitBoardSummaryBroadcast()
  }

  async function moveTask(
    task: ProjectBoardTaskDto,
    groupPeers: readonly ProjectBoardTaskDto[],
    targetIndex: number,
    patch: UpdateProjectBoardTaskInput = {},
  ): Promise<ProjectBoardTaskDto> {
    const sortKey = computeReorderSortKey(groupPeers, task.task_id, targetIndex)
    const optimistic: ProjectBoardTaskDto = {
      ...task,
      ...(patch.status ? { status: patch.status } : {}),
      ...(patch.priority ? { priority: patch.priority } : {}),
      ...(patch.assignee !== undefined ? { assignee: patch.assignee } : {}),
      sort_key: sortKey,
      updated_at_unix_ms: Date.now(),
    }
    upsertTaskLocal(optimistic)
    try {
      const saved = await api.updateProjectBoardTask(task.project_id, task.task_id, {
        ...patch,
        sort_key: sortKey,
        expected_revision: task.revision,
      })
      upsertTaskLocal(saved)
      emitBoardSummaryBroadcast()
      return saved
    } catch (err) {
      // Refresh authoritative state on revision conflict or validation error.
      void refresh()
      throw err
    }
  }

  async function resolveConflict(
    projectId: string,
    taskId: string,
    resolution: ProjectBoardConflictResolution,
  ): Promise<ProjectBoardTaskDto> {
    const resolved = await api.resolveProjectBoardConflict(projectId, taskId, { resolution })
    upsertTaskLocal(resolved)
    emitBoardSummaryBroadcast()
    return resolved
  }

  async function dispatchTask(
    projectId: string,
    taskId: string,
    input: DispatchProjectBoardTaskInput = {},
  ): Promise<ProjectBoardTaskDto> {
    const dispatched = await api.dispatchProjectBoardTask(projectId, taskId, input)
    upsertTaskLocal(dispatched)
    emitBoardSummaryBroadcast()
    return dispatched
  }

  async function cancelTask(projectId: string, taskId: string): Promise<ProjectBoardTaskDto> {
    const cancelled = await api.cancelProjectBoardTask(projectId, taskId)
    upsertTaskLocal(cancelled)
    emitBoardSummaryBroadcast()
    return cancelled
  }

  async function approveTask(
    projectId: string,
    taskId: string,
    input: ApproveProjectBoardTaskInput = {},
  ): Promise<ProjectBoardTaskDto> {
    const approved = await api.approveProjectBoardTask(projectId, taskId, input)
    upsertTaskLocal(approved)
    emitBoardSummaryBroadcast()
    return approved
  }

  async function requestChanges(
    projectId: string,
    taskId: string,
    feedback: string,
  ): Promise<ProjectBoardTaskDto> {
    const updated = await api.requestProjectBoardTaskChanges(projectId, taskId, { feedback })
    upsertTaskLocal(updated)
    emitBoardSummaryBroadcast()
    return updated
  }

  async function saveSettings(
    input: UpdateProjectBoardSettingsInput,
  ): Promise<ProjectBoardSettingsDto | null> {
    const pid = resolvedProjectId.value
    if (!pid) return null
    const updated = await api.updateProjectBoardSettings(pid, input)
    settings.value = updated
    persistCache()
    return updated
  }

  async function reloadNotes(): Promise<ProjectNotesDto | null> {
    const pid = resolvedProjectId.value
    if (!pid) return null
    const loaded = await api.getProjectNotes(pid)
    notes.value = loaded
    persistCache()
    return loaded
  }

  async function saveNotes(input: UpdateProjectNotesInput): Promise<ProjectNotesDto | null> {
    const pid = resolvedProjectId.value
    if (!pid) return null
    const saved = await api.updateProjectNotes(pid, input)
    notes.value = saved
    persistCache()
    return saved
  }

  async function planCoordinator(
    input: CoordinatorPlanInput,
  ): Promise<CoordinatorPlanResultDto | null> {
    const pid = resolvedProjectId.value
    if (!pid) return null
    const result = await api.planCoordinatorTasks(pid, input)
    if (result.auto_applied && result.created_tasks.length > 0) {
      for (const task of result.created_tasks) {
        upsertTaskLocal(task)
      }
      emitBoardSummaryBroadcast()
      void reloadNotes().catch(() => undefined)
      void reloadAutopilot().catch(() => undefined)
    }
    return result
  }

  async function applyCoordinatorPlan(
    input: CoordinatorApplyPlanInput,
  ): Promise<CoordinatorApplyPlanResultDto | null> {
    const pid = resolvedProjectId.value
    if (!pid) return null
    const result = await api.applyCoordinatorPlan(pid, input)
    for (const task of result.created_tasks) {
      upsertTaskLocal(task)
    }
    emitBoardSummaryBroadcast()
    if (input.update_notes !== false) {
      void reloadNotes().catch(() => undefined)
    }
    void reloadAutopilot().catch(() => undefined)
    return result
  }

  async function reloadAutopilot(): Promise<ProjectAutopilotStateDto | null> {
    const pid = resolvedProjectId.value
    if (!pid) return null
    const loaded = await api.getProjectAutopilot(pid)
    autopilot.value = loaded
    persistCache()
    return loaded
  }

  async function updateAutopilot(
    input: UpdateProjectAutopilotInput,
  ): Promise<ProjectAutopilotStateDto | null> {
    const pid = resolvedProjectId.value
    if (!pid) return null
    const updated = await api.updateProjectAutopilot(pid, input)
    autopilot.value = updated
    if (settings.value) {
      settings.value = {
        ...settings.value,
        autopilot_enabled: updated.autopilot_enabled,
        max_concurrency: updated.max_concurrency,
      }
    }
    if (input.cancel_active) {
      void refresh()
    } else {
      persistCache()
    }
    emitBoardSummaryBroadcast()
    return updated
  }

  if (options.autoConnect !== false) {
    watch(
      [resolvedProjectId, canAccessBackend],
      ([pid, backendReady], prev) => {
        const prevPid = prev?.[0]
        const projectChanged = prev === undefined || pid !== prevPid
        if (projectChanged) {
          if (prev !== undefined) {
            resetScopedState()
          }
          hydrateFromCache(pid)
        }
        if (backendReady && (scope === 'global' || Boolean(pid))) {
          error.value = null
          void refresh()
          connectStream()
        } else {
          disconnectStream()
          if (!backendReady && tasks.value.length > 0) {
            stale.value = true
          }
        }
      },
      { immediate: true },
    )
  } else {
    hydrateFromCache(resolvedProjectId.value)
  }

  if (getCurrentScope()) {
    onScopeDispose(() => {
      disconnectStream()
    })
  }

  return {
    scope,
    resolvedProjectId,
    tasks,
    filteredTasks,
    groups,
    swimlanes,
    availableLabels,
    savedViews,
    activeViewId,
    settings,
    summary,
    notes,
    autopilot,
    layout,
    groupBy,
    swimlaneBy,
    sortBy,
    filters,
    visibleFields,
    spatialPromotionPref,
    loading,
    syncing,
    stale,
    connected,
    error,
    activeFilterCount,
    boardStats,
    isFieldVisible,
    toggleVisibleField,
    applyViewConfig,
    selectSavedView,
    applySwimlaneByProjectPreset,
    clearFilters,
    setSpatialPromotionPref,
    refresh,
    syncBoard,
    saveCurrentAsView,
    updateActiveSavedView,
    deleteSavedView,
    createTask,
    promoteFromSpatial,
    upsertTaskLocal,
    updateTask,
    deleteTask,
    moveTask,
    resolveConflict,
    dispatchTask,
    cancelTask,
    approveTask,
    requestChanges,
    saveSettings,
    reloadNotes,
    saveNotes,
    planCoordinator,
    applyCoordinatorPlan,
    reloadAutopilot,
    updateAutopilot,
    applySseEvent,
    disconnectStream,
  }
}
