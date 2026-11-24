import { reactive, watch } from 'vue'
import { useUie, type PersistedCardInstance, type UieStore } from '@tinadec/ui'
import type {
  ApproveProjectBoardTaskInput,
  CreateProjectBoardTaskInput,
  DispatchProjectBoardTaskInput,
  ProjectBoardConflictResolution,
  ProjectBoardTaskDto,
  ProjectBoardTaskStatus,
  UpdateProjectBoardTaskInput,
} from '@/api'
import { ensureProductionUie } from '@/lib/uiEngine'
import { projectStorageId } from '@/lib/storageScope'

export const BOARD_TASK_INSPECTOR_DESCRIPTOR_ID = 'boardTaskInspector'

export interface BoardInspectorProjectOption {
  id: string
  name: string
}

export interface BoardInspectorCallbacks {
  onCreateTask?: ((projectId: string, input: CreateProjectBoardTaskInput) => void | Promise<void>) | null
  onSaveTask?: ((projectId: string, taskId: string, patch: UpdateProjectBoardTaskInput) => void | Promise<void>) | null
  onDeleteTask?: ((projectId: string, taskId: string) => void | Promise<void>) | null
  onResolveConflict?: ((
    projectId: string,
    taskId: string,
    resolution: ProjectBoardConflictResolution,
  ) => void | Promise<void>) | null
  onDispatchTask?: ((
    projectId: string,
    taskId: string,
    input: DispatchProjectBoardTaskInput,
  ) => void | Promise<void>) | null
  onCancelTask?: ((projectId: string, taskId: string) => void | Promise<void>) | null
  onApproveTask?: ((
    projectId: string,
    taskId: string,
    input: ApproveProjectBoardTaskInput,
  ) => void | Promise<void>) | null
  onRequestChanges?: ((projectId: string, taskId: string, feedback: string) => void | Promise<void>) | null
  onOpenSession?: ((sessionId: string) => void | Promise<void>) | null
  onJumpSpatial?: ((task: ProjectBoardTaskDto) => void | Promise<void>) | null
}

export interface BoardInspectorState extends Required<BoardInspectorCallbacks> {
  open: boolean
  mode: 'create' | 'edit'
  ownerScope: 'global' | 'project' | null
  projectId: string | null
  taskId: string | null
  task: ProjectBoardTaskDto | null
  projectTasks: ProjectBoardTaskDto[]
  projects: BoardInspectorProjectOption[]
  defaultProjectId: string | null
  defaultStatus: ProjectBoardTaskStatus
  storageId: string | null
}

export interface OpenBoardTaskInspectorOptions extends BoardInspectorCallbacks {
  mode: 'create' | 'edit'
  ownerScope?: 'global' | 'project' | null
  projectId?: string | null
  taskId?: string | null
  task?: ProjectBoardTaskDto | null
  projectTasks?: readonly ProjectBoardTaskDto[]
  projects?: readonly BoardInspectorProjectOption[]
  defaultProjectId?: string | null
  defaultStatus?: ProjectBoardTaskStatus
  storageId?: string | null
}

export interface SyncBoardTaskInspectorDataOptions {
  tasks: readonly ProjectBoardTaskDto[]
  projects?: readonly BoardInspectorProjectOption[]
  ownerScope?: 'global' | 'project'
  projectId?: string | null
  loading?: boolean
}

export const boardInspectorStore: BoardInspectorState = reactive<BoardInspectorState>({
  open: false,
  mode: 'edit',
  ownerScope: null,
  projectId: null,
  taskId: null,
  task: null,
  projectTasks: [],
  projects: [],
  defaultProjectId: null,
  defaultStatus: 'ready',
  storageId: null,
  onCreateTask: null,
  onSaveTask: null,
  onDeleteTask: null,
  onResolveConflict: null,
  onDispatchTask: null,
  onCancelTask: null,
  onApproveTask: null,
  onRequestChanges: null,
  onOpenSession: null,
  onJumpSpatial: null,
})

let managedByUie = false
const boundUieStores = new WeakSet<UieStore>()

export function tryGetBoardInspectorUie(): UieStore | null {
  try {
    return useUie()
  } catch {
    return null
  }
}

function resolveUieForOpen(explicitUie?: UieStore | null): UieStore | null {
  if (explicitUie !== undefined) return explicitUie
  const existing = tryGetBoardInspectorUie()
  if (existing) return existing
  try {
    return ensureProductionUie()
  } catch {
    return null
  }
}

export function findBoardInspectorCardInstance(uie: UieStore | null | undefined): PersistedCardInstance | null {
  const cards = uie?.snapshot?.value?.cards
  if (!cards) return null
  return (
    Object.values(cards).find((card) => card.descriptorId === BOARD_TASK_INSPECTOR_DESCRIPTOR_ID) ?? null
  )
}

export function bindBoardInspectorUieWatcher(uie: UieStore | null | undefined): void {
  if (!uie?.snapshot || boundUieStores.has(uie)) return
  boundUieStores.add(uie)
  watch(
    () => uie.snapshot.value,
    (snap) => {
      if (!managedByUie || !boardInspectorStore.open) return
      const stillMounted = Object.values(snap?.cards ?? {}).some(
        (card) => card.descriptorId === BOARD_TASK_INSPECTOR_DESCRIPTOR_ID,
      )
      if (!stillMounted) {
        syncBoardInspectorClosedFromUie()
      }
    },
    { flush: 'sync' },
  )
}

export function syncBoardInspectorClosedFromUie(): void {
  managedByUie = false
  boardInspectorStore.open = false
  boardInspectorStore.taskId = null
  boardInspectorStore.task = null
}

function buildSerializedCardState(): Record<string, unknown> {
  return {
    mode: boardInspectorStore.mode,
    projectId: boardInspectorStore.projectId,
    taskId: boardInspectorStore.taskId,
    defaultProjectId: boardInspectorStore.defaultProjectId,
    defaultStatus: boardInspectorStore.defaultStatus,
    storageId: boardInspectorStore.storageId ?? 'user',
  }
}

export function openBoardTaskInspector(
  options: OpenBoardTaskInspectorOptions,
  uieStore?: UieStore | null,
): void {
  const resolvedTask = options.mode === 'edit' ? (options.task ?? null) : null
  const resolvedTaskId =
    options.mode === 'edit' ? (resolvedTask?.task_id ?? options.taskId ?? null) : null
  const resolvedProjectId =
    options.mode === 'edit'
      ? (resolvedTask?.project_id ?? options.projectId ?? options.defaultProjectId ?? null)
      : (options.projectId ?? options.defaultProjectId ?? options.projects?.[0]?.id ?? null)
  const resolvedStorageId =
    options.storageId ?? (resolvedProjectId ? projectStorageId(resolvedProjectId) : 'user')

  boardInspectorStore.open = true
  boardInspectorStore.mode = options.mode
  boardInspectorStore.ownerScope = options.ownerScope ?? boardInspectorStore.ownerScope ?? null
  boardInspectorStore.projectId = resolvedProjectId
  boardInspectorStore.taskId = resolvedTaskId
  boardInspectorStore.task = resolvedTask
  if (options.projectTasks !== undefined) {
    boardInspectorStore.projectTasks = [...options.projectTasks]
  }
  if (options.projects !== undefined) {
    boardInspectorStore.projects = [...options.projects]
  }
  boardInspectorStore.defaultProjectId =
    options.defaultProjectId ?? resolvedProjectId ?? boardInspectorStore.defaultProjectId
  boardInspectorStore.defaultStatus = options.defaultStatus ?? 'ready'
  boardInspectorStore.storageId = resolvedStorageId

  if (options.onCreateTask !== undefined) boardInspectorStore.onCreateTask = options.onCreateTask
  if (options.onSaveTask !== undefined) boardInspectorStore.onSaveTask = options.onSaveTask
  if (options.onDeleteTask !== undefined) boardInspectorStore.onDeleteTask = options.onDeleteTask
  if (options.onResolveConflict !== undefined) {
    boardInspectorStore.onResolveConflict = options.onResolveConflict
  }
  if (options.onDispatchTask !== undefined) boardInspectorStore.onDispatchTask = options.onDispatchTask
  if (options.onCancelTask !== undefined) boardInspectorStore.onCancelTask = options.onCancelTask
  if (options.onApproveTask !== undefined) boardInspectorStore.onApproveTask = options.onApproveTask
  if (options.onRequestChanges !== undefined) {
    boardInspectorStore.onRequestChanges = options.onRequestChanges
  }
  if (options.onOpenSession !== undefined) boardInspectorStore.onOpenSession = options.onOpenSession
  if (options.onJumpSpatial !== undefined) boardInspectorStore.onJumpSpatial = options.onJumpSpatial

  const uie = resolveUieForOpen(uieStore)
  if (!uie?.snapshot?.value || !uie?.scope?.value || typeof uie.dispatch !== 'function') {
    managedByUie = false
    return
  }

  bindBoardInspectorUieWatcher(uie)
  const cardState = buildSerializedCardState()

  if (uie.snapshot.value.columns?.right?.collapsed) {
    uie.dispatch({
      command: {
        type: 'collapseColumn',
        scope: uie.scope.value,
        slotId: 'right',
        collapsed: false,
      },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
  }

  const existing = findBoardInspectorCardInstance(uie)
  if (!existing) {
    const opened = uie.dispatch({
      command: {
        type: 'openCard',
        scope: uie.scope.value,
        descriptorId: BOARD_TASK_INSPECTOR_DESCRIPTOR_ID,
        slotId: 'right',
        stackId: 'primary',
        state: cardState,
      },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
    managedByUie = opened || Boolean(findBoardInspectorCardInstance(uie))
  } else {
    try {
      existing.state = cardState
    } catch {
      // Ignore if snapshot instance was frozen in an isolated test.
    }
    uie.dispatch({
      command: {
        type: 'activateCard',
        scope: uie.scope.value,
        instanceId: existing.id,
      },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
    managedByUie = true
  }
}

export function syncBoardTaskInspectorData(options: SyncBoardTaskInspectorDataOptions): void {
  if (
    options.ownerScope &&
    boardInspectorStore.ownerScope &&
    options.ownerScope !== boardInspectorStore.ownerScope
  ) {
    return
  }
  if (
    options.ownerScope === 'project' &&
    options.projectId &&
    boardInspectorStore.projectId &&
    options.projectId !== boardInspectorStore.projectId
  ) {
    return
  }

  boardInspectorStore.projectTasks = [...options.tasks]
  if (options.projects !== undefined) {
    boardInspectorStore.projects = [...options.projects]
  }

  if (!boardInspectorStore.open || boardInspectorStore.mode !== 'edit' || !boardInspectorStore.taskId) {
    return
  }

  const liveMatch = options.tasks.find((item) => item.task_id === boardInspectorStore.taskId)
  if (liveMatch) {
    boardInspectorStore.task = liveMatch
    boardInspectorStore.projectId = liveMatch.project_id
    return
  }

  // If the inspected task was deleted remotely or locally (not merely awaiting initial fetch), close cleanly.
  if (!options.loading) {
    closeBoardTaskInspector()
  }
}

export function closeBoardTaskInspector(uieStore?: UieStore | null): void {
  managedByUie = false
  boardInspectorStore.open = false
  boardInspectorStore.taskId = null
  boardInspectorStore.task = null

  const uie = uieStore !== undefined ? uieStore : tryGetBoardInspectorUie()
  if (!uie?.snapshot?.value || !uie?.scope?.value || typeof uie.dispatch !== 'function') {
    return
  }

  const existing = findBoardInspectorCardInstance(uie)
  if (existing) {
    uie.dispatch({
      command: {
        type: 'closeCard',
        scope: uie.scope.value,
        instanceId: existing.id,
      },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
  }
}

export function useBoardInspectorStore(): BoardInspectorState {
  return boardInspectorStore
}

export function resetBoardInspectorForTests(): void {
  managedByUie = false
  boardInspectorStore.open = false
  boardInspectorStore.mode = 'edit'
  boardInspectorStore.ownerScope = null
  boardInspectorStore.projectId = null
  boardInspectorStore.taskId = null
  boardInspectorStore.task = null
  boardInspectorStore.projectTasks = []
  boardInspectorStore.projects = []
  boardInspectorStore.defaultProjectId = null
  boardInspectorStore.defaultStatus = 'ready'
  boardInspectorStore.storageId = null
  boardInspectorStore.onCreateTask = null
  boardInspectorStore.onSaveTask = null
  boardInspectorStore.onDeleteTask = null
  boardInspectorStore.onResolveConflict = null
  boardInspectorStore.onDispatchTask = null
  boardInspectorStore.onCancelTask = null
  boardInspectorStore.onApproveTask = null
  boardInspectorStore.onRequestChanges = null
  boardInspectorStore.onOpenSession = null
  boardInspectorStore.onJumpSpatial = null
}
