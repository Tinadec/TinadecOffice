<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Activity, AlertTriangle, CheckCircle2, Layers, RefreshCw, Rows3 } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import {
  BoardKanbanView,
  BoardListView,
  BoardShellLayout,
  BoardSwimlaneView,
  BoardTaskDrawer,
  BoardViewToolbar,
  buildSpatialFocusLocation,
  stashPendingSpatialFocus,
  useProjectBoardView,
  type BoardTaskGroup,
} from '@/components/board'
import type {
  ApproveProjectBoardTaskInput,
  CreateProjectBoardTaskInput,
  DispatchProjectBoardTaskInput,
  ProjectBoardConflictResolution,
  ProjectBoardTaskDto,
  ProjectBoardTaskStatus,
  UpdateProjectBoardTaskInput,
} from '@/api'
import { homeController as c } from '@/controllers/HomeController'
import { useNotifications } from '@/composables/useNotifications'

const emit = defineEmits<{ ready: [] }>()
const router = useRouter()
const { t } = useI18n()
const { notify } = useNotifications()

const activeProjects = computed(() =>
  c.projects.value
    .filter((p) => p.lifecycle_status !== 'archived' && p.lifecycle_status !== 'trashed')
    .map((p) => ({ id: p.id, name: p.name })),
)

const board = useProjectBoardView({
  scope: 'global',
  projects: activeProjects,
})

const drawerOpen = ref(false)
const drawerMode = ref<'create' | 'edit'>('edit')
const selectedTaskId = ref<string | null>(null)
const createDefaultStatus = ref<ProjectBoardTaskStatus>('ready')

const selectedTask = computed<ProjectBoardTaskDto | null>(() => {
  if (!selectedTaskId.value) return null
  return board.tasks.value.find((item) => item.task_id === selectedTaskId.value) ?? null
})

const headerStats = computed(() => {
  const s = board.summary.value
  const local = board.boardStats.value
  return {
    totalTasks: s?.total_tasks ?? local.total,
    activeRuns: s?.active_run_count ?? local.inProgress,
    inReview: s?.in_review_count ?? local.inReview,
    needsAction: s?.needs_action_count ?? local.needsAction,
    conflicts: s?.conflict_count ?? local.conflicts,
  }
})

onMounted(async () => {
  await nextTick()
  emit('ready')
})

function openCreateDrawer(defaultStatus: ProjectBoardTaskStatus = 'ready') {
  if (!activeProjects.value.length) {
    c.openProject()
    return
  }
  drawerMode.value = 'create'
  selectedTaskId.value = null
  createDefaultStatus.value = defaultStatus
  drawerOpen.value = true
}

function openCreateInGroup(group: BoardTaskGroup) {
  const status: ProjectBoardTaskStatus =
    group.kind === 'status' ? (group.key as ProjectBoardTaskStatus) : 'ready'
  openCreateDrawer(status)
}

function openTaskDrawer(task: ProjectBoardTaskDto) {
  drawerMode.value = 'edit'
  selectedTaskId.value = task.task_id
  drawerOpen.value = true
}

async function handleCreateTask(projectId: string, input: CreateProjectBoardTaskInput) {
  try {
    const created = await board.createTask(projectId, input)
    selectedTaskId.value = created.task_id
    drawerMode.value = 'edit'
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  }
}

async function handleSaveTask(projectId: string, taskId: string, patch: UpdateProjectBoardTaskInput) {
  try {
    await board.updateTask(projectId, taskId, patch)
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  }
}

async function handleDeleteTask(projectId: string, taskId: string) {
  try {
    await board.deleteTask(projectId, taskId)
    drawerOpen.value = false
    selectedTaskId.value = null
  } catch (err) {
    notify.error(err, { title: t('board.errors.deleteFailed') })
  }
}

async function handleMoveTask(payload: {
  task: ProjectBoardTaskDto
  peers: ProjectBoardTaskDto[]
  targetIndex: number
  patch: UpdateProjectBoardTaskInput
}) {
  try {
    await board.moveTask(payload.task, payload.peers, payload.targetIndex, payload.patch)
  } catch (err) {
    notify.error(err, { title: t('board.errors.moveFailed') })
  }
}

async function handleResolveConflict(
  projectId: string,
  taskId: string,
  resolution: ProjectBoardConflictResolution,
) {
  try {
    await board.resolveConflict(projectId, taskId, resolution)
  } catch (err) {
    notify.error(err, { title: t('board.errors.conflictResolveFailed') })
  }
}

async function handleDispatchTask(
  projectId: string,
  taskId: string,
  input: DispatchProjectBoardTaskInput,
) {
  try {
    await board.dispatchTask(projectId, taskId, input)
  } catch (err) {
    notify.error(err, { title: t('board.errors.dispatchFailed') })
  }
}

async function handleCancelTask(projectId: string, taskId: string) {
  try {
    await board.cancelTask(projectId, taskId)
  } catch (err) {
    notify.error(err, { title: t('board.errors.cancelFailed') })
  }
}

async function handleApproveTask(
  projectId: string,
  taskId: string,
  input: ApproveProjectBoardTaskInput,
) {
  try {
    await board.approveTask(projectId, taskId, input)
  } catch (err) {
    notify.error(err, { title: t('board.errors.approveFailed') })
  }
}

async function handleRequestChanges(projectId: string, taskId: string, feedback: string) {
  try {
    await board.requestChanges(projectId, taskId, feedback)
  } catch (err) {
    notify.error(err, { title: t('board.errors.requestChangesFailed') })
  }
}

async function handleOpenSession(sessionId: string) {
  drawerOpen.value = false
  await c.setSelectedSession(sessionId)
  await router.push('/')
}

async function handleJumpSpatial(task: ProjectBoardTaskDto) {
  if (!task.spatial_ref) return
  drawerOpen.value = false
  stashPendingSpatialFocus(task.spatial_ref)
  await c.setSelectedSession(task.spatial_ref.session_id)
  await router.push(buildSpatialFocusLocation(task.spatial_ref))
}
</script>

<template>
  <BoardShellLayout>
    <div class="global-board-page" data-testid="global-board-page">
      <!-- Cross-Project Command Header -->
      <header class="global-board-header">
        <div class="global-board-title-block">
          <h1 class="global-board-title">{{ t('board.global.title') }}</h1>
          <p class="global-board-subtitle">{{ t('board.global.subtitle') }}</p>
        </div>

        <div class="global-board-metrics">
          <div class="global-metric-pill">
            <Layers :size="13" />
            <span>{{ t('board.global.metricTotal', { count: headerStats.totalTasks }) }}</span>
          </div>
          <div class="global-metric-pill">
            <Activity :size="13" />
            <span>{{ t('board.global.metricActiveRuns', { count: headerStats.activeRuns }) }}</span>
          </div>
          <div class="global-metric-pill">
            <CheckCircle2 :size="13" />
            <span>{{ t('board.global.metricInReview', { count: headerStats.inReview }) }}</span>
          </div>
          <div
            v-if="headerStats.needsAction > 0"
            class="global-metric-pill global-metric-pill--emphasis"
            data-testid="global-board-needs-action-pill"
          >
            <AlertTriangle :size="13" />
            <span>{{ t('board.global.metricNeedsAction', { count: headerStats.needsAction }) }}</span>
          </div>

          <button
            type="button"
            class="global-preset-btn ui-pressable"
            :class="{
              active: board.layout.value === 'swimlane' && board.swimlaneBy.value === 'project',
            }"
            data-testid="global-board-swimlane-preset"
            @click="board.applySwimlaneByProjectPreset()"
          >
            <Rows3 :size="13" />
            <span>{{ t('board.global.swimlaneByProjectPreset') }}</span>
          </button>

          <UiButton
            type="button"
            variant="ghost"
            size="icon"
            :title="t('common.refresh')"
            :aria-label="t('common.refresh')"
            @click="board.refresh()"
          >
            <RefreshCw :size="14" :class="{ 'animate-spin': board.loading.value }" />
          </UiButton>
        </div>
      </header>

      <!-- Composable View Toolbar -->
      <BoardViewToolbar
        scope="global"
        :layout="board.layout.value"
        :group-by="board.groupBy.value"
        :swimlane-by="board.swimlaneBy.value"
        :sort-by="board.sortBy.value"
        :filters="board.filters.value"
        :visible-fields="board.visibleFields.value"
        :saved-views="board.savedViews.value"
        :active-view-id="board.activeViewId.value"
        :active-filter-count="board.activeFilterCount.value"
        :available-labels="board.availableLabels.value"
        :projects="activeProjects"
        :stale="board.stale.value"
        @update:layout="board.layout.value = $event"
        @update:group-by="board.groupBy.value = $event"
        @update:swimlane-by="board.swimlaneBy.value = $event"
        @update:sort-by="board.sortBy.value = $event"
        @update:filters="board.filters.value = $event"
        @toggle-field="board.toggleVisibleField($event)"
        @select-view="board.selectSavedView($event)"
        @save-view="board.saveCurrentAsView($event)"
        @update-active-view="board.updateActiveSavedView()"
        @delete-view="board.deleteSavedView($event)"
        @clear-filters="board.clearFilters()"
        @new-task="openCreateDrawer()"
      />

      <!-- Layout Surface -->
      <BoardKanbanView
        v-if="board.layout.value === 'kanban'"
        :groups="board.groups.value"
        :visible-fields="board.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="openTaskDrawer"
        @jump-spatial="handleJumpSpatial"
        @new-task-in-group="openCreateInGroup"
        @move-task="handleMoveTask"
      />
      <BoardListView
        v-else-if="board.layout.value === 'list'"
        :groups="board.groups.value"
        :visible-fields="board.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="openTaskDrawer"
        @jump-spatial="handleJumpSpatial"
        @quick-update="(task, patch) => handleSaveTask(task.project_id, task.task_id, patch)"
        @move-task="handleMoveTask"
      />
      <BoardSwimlaneView
        v-else
        :swimlanes="board.swimlanes.value"
        :visible-fields="board.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="openTaskDrawer"
        @jump-spatial="handleJumpSpatial"
        @move-task="handleMoveTask"
      />

      <!-- Right Slide-over Task Drawer -->
      <BoardTaskDrawer
        v-model:open="drawerOpen"
        :mode="drawerMode"
        :task="selectedTask"
        :project-tasks="board.tasks.value"
        :default-project-id="activeProjects[0]?.id ?? null"
        :default-status="createDefaultStatus"
        :projects="activeProjects"
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
    </div>
  </BoardShellLayout>
</template>

<style scoped>
.global-board-page {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.global-board-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 16px 16px 6px;
  flex-wrap: wrap;
}

.global-board-title-block {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.global-board-title {
  margin: 0;
  font-size: 17px;
  font-weight: 600;
  color: var(--text-primary);
}

.global-board-subtitle {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
}

.global-board-metrics {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.global-metric-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 10px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 11.5px;
}

.global-metric-pill--emphasis {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
  font-weight: 500;
}

.global-preset-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.global-preset-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.global-preset-btn.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}
</style>
