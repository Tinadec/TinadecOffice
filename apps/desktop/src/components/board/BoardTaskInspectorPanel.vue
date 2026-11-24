<script setup lang="ts">
import { computed, getCurrentInstance, inject, onBeforeUnmount, ref, watch } from 'vue'
import { type Router } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { LoaderCircle } from '@lucide/vue'
import {
  api as baseApi,
  type ApproveProjectBoardTaskInput,
  type BoardEventStreamHandle,
  type CreateProjectBoardTaskInput,
  type DispatchProjectBoardTaskInput,
  type ProjectBoardConflictResolution,
  type ProjectBoardTaskDto,
  type ProjectBoardTaskStatus,
  type UpdateProjectBoardTaskInput,
} from '@/api'
import { useHostAccess } from '@/lib/hostAccess'
import { projectStorageId, scopedApi, setSelectedStorage } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'
import { homeController } from '@/controllers/HomeController'
import BoardTaskDrawer from './BoardTaskDrawer.vue'
import {
  BOARD_TASK_INSPECTOR_DESCRIPTOR_ID,
  bindBoardInspectorUieWatcher,
  boardInspectorStore,
  closeBoardTaskInspector,
  findBoardInspectorCardInstance,
  syncBoardInspectorClosedFromUie,
  tryGetBoardInspectorUie,
  type BoardInspectorProjectOption,
} from './boardInspectorStore'
import {
  BOARD_TASK_STATUSES,
  buildSpatialFocusLocation,
  stashPendingSpatialFocus,
} from './useProjectBoardView'

const props = defineProps<{
  detachedState?: Record<string, unknown>
}>()

const { t } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify } = useNotifications()

const instance = getCurrentInstance()
const router = (instance?.appContext.config.globalProperties.$router as Router | undefined) ?? null

const injectedCardState = inject<Record<string, unknown> | undefined>('uie:cardState', undefined)
const uie = props.detachedState ? null : tryGetBoardInspectorUie()
if (uie) {
  bindBoardInspectorUieWatcher(uie)
}

const isDetachedWindow = computed(() => props.detachedState !== undefined)

// Use standalone hydration either in a detached child window (`detachedState` prop)
// or when UIE restored/reattached the card before `openBoardTaskInspector` populated the store.
const useStandaloneState = computed(() => {
  if (isDetachedWindow.value) return true
  if (boardInspectorStore.open) return false
  return Boolean(injectedCardState && Object.keys(injectedCardState).length > 0)
})

const standaloneSeed = computed<Record<string, unknown>>(() => {
  if (isDetachedWindow.value) return props.detachedState ?? {}
  return injectedCardState ?? {}
})

const standaloneStorageId = computed(() => {
  const raw = standaloneSeed.value.storageId
  if (typeof raw === 'string' && raw.trim()) return raw
  const pid = typeof standaloneSeed.value.projectId === 'string' ? standaloneSeed.value.projectId : null
  return pid ? projectStorageId(pid) : 'user'
})

const standaloneApi = scopedApi(baseApi, () => standaloneStorageId.value)

const standaloneLoading = ref(false)
const standaloneMode = ref<'create' | 'edit'>('edit')
const standaloneProjectId = ref<string | null>(null)
const standaloneTaskId = ref<string | null>(null)
const standaloneDefaultProjectId = ref<string | null>(null)
const standaloneDefaultStatus = ref<ProjectBoardTaskStatus>('ready')
const standaloneTasks = ref<ProjectBoardTaskDto[]>([])
const standaloneProjects = ref<BoardInspectorProjectOption[]>([])

let standaloneStream: BoardEventStreamHandle | null = null
let loadGeneration = 0

function disconnectStandaloneStream() {
  standaloneStream?.close()
  standaloneStream = null
}

function parseSeedStatus(raw: unknown): ProjectBoardTaskStatus {
  if (typeof raw === 'string' && (BOARD_TASK_STATUSES as readonly string[]).includes(raw)) {
    return raw as ProjectBoardTaskStatus
  }
  return 'ready'
}

async function hydrateStandaloneInspector() {
  if (!useStandaloneState.value) {
    disconnectStandaloneStream()
    return
  }

  const seed = standaloneSeed.value
  standaloneMode.value = seed.mode === 'create' ? 'create' : 'edit'
  standaloneProjectId.value = typeof seed.projectId === 'string' && seed.projectId ? seed.projectId : null
  standaloneTaskId.value = typeof seed.taskId === 'string' && seed.taskId ? seed.taskId : null
  standaloneDefaultProjectId.value =
    typeof seed.defaultProjectId === 'string' && seed.defaultProjectId
      ? seed.defaultProjectId
      : standaloneProjectId.value
  standaloneDefaultStatus.value = parseSeedStatus(seed.defaultStatus)

  if (isDetachedWindow.value) {
    setSelectedStorage(standaloneStorageId.value)
  }

  if (!canAccessBackend.value) return

  const gen = ++loadGeneration
  standaloneLoading.value = true
  try {
    const pid = standaloneProjectId.value ?? standaloneDefaultProjectId.value
    const [projectList, taskList] = await Promise.all([
      standaloneApi.listProjects().catch(() => []),
      pid ? standaloneApi.listProjectBoardTasks(pid).catch(() => [] as ProjectBoardTaskDto[]) : Promise.resolve([]),
    ])
    if (gen !== loadGeneration) return

    standaloneProjects.value = projectList
      .filter((p) => p.lifecycle_status !== 'archived' && p.lifecycle_status !== 'trashed')
      .map((p) => ({ id: p.id, name: p.name }))
    standaloneTasks.value = taskList

    disconnectStandaloneStream()
    if (pid && typeof standaloneApi.subscribeProjectBoardEvents === 'function') {
      standaloneStream = standaloneApi.subscribeProjectBoardEvents(pid, (ev) => {
        if (ev.event_type === 'task_upserted' && ev.task) {
          const idx = standaloneTasks.value.findIndex((item) => item.task_id === ev.task!.task_id)
          if (idx >= 0) {
            const copy = [...standaloneTasks.value]
            copy[idx] = ev.task
            standaloneTasks.value = copy
          } else {
            standaloneTasks.value = [...standaloneTasks.value, ev.task]
          }
        } else if (ev.event_type === 'task_deleted' && ev.task_id) {
          standaloneTasks.value = standaloneTasks.value.filter((item) => item.task_id !== ev.task_id)
        }
      })
    }
  } finally {
    if (gen === loadGeneration) {
      standaloneLoading.value = false
    }
  }
}

watch(
  [useStandaloneState, standaloneSeed, canAccessBackend],
  ([active]) => {
    if (active) {
      void hydrateStandaloneInspector()
    } else {
      disconnectStandaloneStream()
    }
  },
  { immediate: true },
)

if (uie) {
  watch(
    () => uie.snapshot.value.cards,
    (cards) => {
      if (isDetachedWindow.value || !boardInspectorStore.open) return
      const exists = Object.values(cards ?? {}).some(
        (card) => card.descriptorId === BOARD_TASK_INSPECTOR_DESCRIPTOR_ID,
      )
      if (!exists) {
        syncBoardInspectorClosedFromUie()
      }
    },
    { flush: 'sync' },
  )
}

onBeforeUnmount(() => {
  disconnectStandaloneStream()
  if (!isDetachedWindow.value && uie && !findBoardInspectorCardInstance(uie)) {
    syncBoardInspectorClosedFromUie()
  }
})

const standaloneTask = computed<ProjectBoardTaskDto | null>(() => {
  if (!standaloneTaskId.value) return null
  return standaloneTasks.value.find((item) => item.task_id === standaloneTaskId.value) ?? null
})

const effectiveMode = computed<'create' | 'edit'>(() =>
  useStandaloneState.value ? standaloneMode.value : boardInspectorStore.mode,
)

const effectiveTask = computed<ProjectBoardTaskDto | null>(() => {
  if (useStandaloneState.value) return standaloneTask.value
  if (boardInspectorStore.mode !== 'edit') return null
  if (boardInspectorStore.taskId) {
    const fromList = boardInspectorStore.projectTasks.find(
      (item) => item.task_id === boardInspectorStore.taskId,
    )
    if (fromList) return fromList
  }
  return boardInspectorStore.task
})

const effectiveProjectTasks = computed<readonly ProjectBoardTaskDto[]>(() =>
  useStandaloneState.value ? standaloneTasks.value : boardInspectorStore.projectTasks,
)

const effectiveProjects = computed<BoardInspectorProjectOption[]>(() =>
  useStandaloneState.value ? standaloneProjects.value : boardInspectorStore.projects,
)

const effectiveDefaultProjectId = computed<string | null>(() =>
  useStandaloneState.value
    ? (standaloneDefaultProjectId.value ?? standaloneProjects.value[0]?.id ?? null)
    : (boardInspectorStore.defaultProjectId ?? boardInspectorStore.projectId),
)

const effectiveDefaultStatus = computed<ProjectBoardTaskStatus>(() =>
  useStandaloneState.value ? standaloneDefaultStatus.value : boardInspectorStore.defaultStatus,
)

const hasInspectableTarget = computed(() => {
  if (effectiveMode.value === 'create') return true
  return Boolean(effectiveTask.value)
})

function upsertStandaloneTask(updated: ProjectBoardTaskDto) {
  const idx = standaloneTasks.value.findIndex((item) => item.task_id === updated.task_id)
  if (idx >= 0) {
    const copy = [...standaloneTasks.value]
    copy[idx] = updated
    standaloneTasks.value = copy
  } else {
    standaloneTasks.value = [...standaloneTasks.value, updated]
  }
}

function handleOpenChange(nextOpen: boolean) {
  if (nextOpen) return
  if (isDetachedWindow.value) {
    window.tinadec?.closeWindow?.()
    return
  }
  closeBoardTaskInspector(uie)
}

async function handleCreateTask(projectId: string, input: CreateProjectBoardTaskInput) {
  if (!useStandaloneState.value && boardInspectorStore.onCreateTask) {
    await boardInspectorStore.onCreateTask(projectId, input)
    return
  }
  try {
    const created = await standaloneApi.createProjectBoardTask(projectId, input)
    upsertStandaloneTask(created)
    standaloneProjectId.value = created.project_id
    standaloneTaskId.value = created.task_id
    standaloneMode.value = 'edit'
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  }
}

async function handleSaveTask(projectId: string, taskId: string, patch: UpdateProjectBoardTaskInput) {
  if (!useStandaloneState.value && boardInspectorStore.onSaveTask) {
    await boardInspectorStore.onSaveTask(projectId, taskId, patch)
    return
  }
  try {
    const updated = await standaloneApi.updateProjectBoardTask(projectId, taskId, patch)
    upsertStandaloneTask(updated)
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  }
}

async function handleDeleteTask(projectId: string, taskId: string) {
  if (!useStandaloneState.value && boardInspectorStore.onDeleteTask) {
    await boardInspectorStore.onDeleteTask(projectId, taskId)
    return
  }
  try {
    await standaloneApi.deleteProjectBoardTask(projectId, taskId)
    standaloneTasks.value = standaloneTasks.value.filter((item) => item.task_id !== taskId)
    handleOpenChange(false)
  } catch (err) {
    notify.error(err, { title: t('board.errors.deleteFailed') })
  }
}

async function handleResolveConflict(
  projectId: string,
  taskId: string,
  resolution: ProjectBoardConflictResolution,
) {
  if (!useStandaloneState.value && boardInspectorStore.onResolveConflict) {
    await boardInspectorStore.onResolveConflict(projectId, taskId, resolution)
    return
  }
  try {
    const resolved = await standaloneApi.resolveProjectBoardConflict(projectId, taskId, { resolution })
    upsertStandaloneTask(resolved)
  } catch (err) {
    notify.error(err, { title: t('board.errors.conflictResolveFailed') })
  }
}

async function handleDispatchTask(
  projectId: string,
  taskId: string,
  input: DispatchProjectBoardTaskInput,
) {
  if (!useStandaloneState.value && boardInspectorStore.onDispatchTask) {
    await boardInspectorStore.onDispatchTask(projectId, taskId, input)
    return
  }
  try {
    const dispatched = await standaloneApi.dispatchProjectBoardTask(projectId, taskId, input)
    upsertStandaloneTask(dispatched)
  } catch (err) {
    notify.error(err, { title: t('board.errors.dispatchFailed') })
  }
}

async function handleCancelTask(projectId: string, taskId: string) {
  if (!useStandaloneState.value && boardInspectorStore.onCancelTask) {
    await boardInspectorStore.onCancelTask(projectId, taskId)
    return
  }
  try {
    const cancelled = await standaloneApi.cancelProjectBoardTask(projectId, taskId)
    upsertStandaloneTask(cancelled)
  } catch (err) {
    notify.error(err, { title: t('board.errors.cancelFailed') })
  }
}

async function handleApproveTask(
  projectId: string,
  taskId: string,
  input: ApproveProjectBoardTaskInput,
) {
  if (!useStandaloneState.value && boardInspectorStore.onApproveTask) {
    await boardInspectorStore.onApproveTask(projectId, taskId, input)
    return
  }
  try {
    const approved = await standaloneApi.approveProjectBoardTask(projectId, taskId, input)
    upsertStandaloneTask(approved)
  } catch (err) {
    notify.error(err, { title: t('board.errors.approveFailed') })
  }
}

async function handleRequestChanges(projectId: string, taskId: string, feedback: string) {
  if (!useStandaloneState.value && boardInspectorStore.onRequestChanges) {
    await boardInspectorStore.onRequestChanges(projectId, taskId, feedback)
    return
  }
  try {
    const updated = await standaloneApi.requestProjectBoardTaskChanges(projectId, taskId, { feedback })
    upsertStandaloneTask(updated)
  } catch (err) {
    notify.error(err, { title: t('board.errors.requestChangesFailed') })
  }
}

async function handleOpenSession(sessionId: string) {
  if (!useStandaloneState.value && boardInspectorStore.onOpenSession) {
    await boardInspectorStore.onOpenSession(sessionId)
    return
  }
  if (!isDetachedWindow.value) {
    closeBoardTaskInspector(uie)
  }
  await homeController.setSelectedSession(sessionId)
  await router?.push('/')
}

async function handleJumpSpatial(task: ProjectBoardTaskDto) {
  if (!useStandaloneState.value && boardInspectorStore.onJumpSpatial) {
    await boardInspectorStore.onJumpSpatial(task)
    return
  }
  if (!task.spatial_ref) return
  if (!isDetachedWindow.value) {
    closeBoardTaskInspector(uie)
  }
  stashPendingSpatialFocus(task.spatial_ref)
  await homeController.setSelectedSession(task.spatial_ref.session_id)
  await router?.push(buildSpatialFocusLocation(task.spatial_ref))
}
</script>

<template>
  <div class="board-task-inspector-panel" data-testid="board-task-inspector-panel">
    <div v-if="useStandaloneState && standaloneLoading" class="board-inspector-status">
      <LoaderCircle :size="18" class="animate-spin" />
      <span>{{ t('context.loadingPanel') }}</span>
    </div>

    <BoardTaskDrawer
      v-else-if="hasInspectableTarget"
      embedded
      :open="true"
      :mode="effectiveMode"
      :task="effectiveTask"
      :project-tasks="effectiveProjectTasks"
      :default-project-id="effectiveDefaultProjectId"
      :default-status="effectiveDefaultStatus"
      :projects="effectiveProjects"
      @update:open="handleOpenChange"
      @create-task="handleCreateTask"
      @save-task="handleSaveTask"
      @delete-task="handleDeleteTask"
      @resolve-conflict="handleResolveConflict"
      @dispatch-task="handleDispatchTask"
      @cancel-task="handleCancelTask"
      @approve-task="handleApproveTask"
      @request-changes="handleRequestChanges"
      @open-session="handleOpenSession"
      @jump-spatial="handleJumpSpatial"
    />

    <div v-else class="board-inspector-status">
      <span>{{ t('board.emptyColumn') }}</span>
    </div>
  </div>
</template>

<style scoped>
.board-task-inspector-panel {
  height: 100%;
  width: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.board-inspector-status {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 24px;
  font-size: 12.5px;
  color: var(--text-muted);
}
</style>
