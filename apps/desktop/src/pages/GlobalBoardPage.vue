<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Activity, AlertTriangle, CheckCircle2, ExternalLink, Layers, Play, RefreshCw, Rows3 } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import {
  BoardKanbanView,
  BoardListView,
  BoardSwimlaneView,
  BoardViewToolbar,
  boardInspectorStore,
  buildBoardSwimlanes,
  buildSpatialFocusLocation,
  closeBoardTaskInspector,
  groupBoardTasks,
  openBoardTaskInspector,
  stashPendingSpatialFocus,
  syncBoardTaskInspectorData,
  useProjectBoardView,
  type BoardInspectorCallbacks,
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

const blockedOnlyFilter = ref(false)

const selectedTaskId = computed<string | null>(() =>
  boardInspectorStore.open ? boardInspectorStore.taskId : null,
)

watch(
  [() => board.tasks.value, activeProjects],
  ([tasks, projects]) => {
    syncBoardTaskInspectorData({
      tasks,
      projects,
      ownerScope: 'global',
      loading: board.loading.value,
    })
  },
  { immediate: true },
)

const blockedTasks = computed<ProjectBoardTaskDto[]>(() =>
  board.tasks.value.filter((item) => item.blocked),
)

const projectNamesMap = computed(() => {
  const map = new Map<string, string>()
  for (const p of activeProjects.value) {
    map.set(p.id, p.name)
  }
  return map
})

const visibleGroups = computed(() => {
  if (!blockedOnlyFilter.value) return board.groups.value
  const subset = board.filteredTasks.value.filter((item) => item.blocked)
  return groupBoardTasks(subset, board.groupBy.value, board.sortBy.value, projectNamesMap.value)
})

const visibleSwimlanes = computed(() => {
  if (!blockedOnlyFilter.value) return board.swimlanes.value
  const subset = board.filteredTasks.value.filter((item) => item.blocked)
  return buildBoardSwimlanes(subset, board.swimlaneBy.value, board.sortBy.value, projectNamesMap.value)
})

const headerStats = computed(() => {
  const s = board.summary.value
  const local = board.boardStats.value
  return {
    totalTasks: s?.total_tasks ?? local.total,
    activeRuns: s?.active_run_count ?? local.inProgress,
    inReview: s?.in_review_count ?? local.inReview,
    blocked: Math.max(s?.blocked_count ?? 0, local.blocked),
    needsAction: s?.needs_action_count ?? local.needsAction,
    conflicts: s?.conflict_count ?? local.conflicts,
  }
})

async function handleQuickUnblock(task: ProjectBoardTaskDto) {
  try {
    await board.updateTask(task.project_id, task.task_id, {
      blocked: false,
      blocked_reason: null,
      status: task.status === 'done' ? 'done' : 'ready',
      expected_revision: task.revision,
    })
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  }
}

onMounted(async () => {
  await nextTick()
  emit('ready')
})

const inspectorCallbacks: BoardInspectorCallbacks = {
  onCreateTask: handleCreateTask,
  onSaveTask: handleSaveTask,
  onDeleteTask: handleDeleteTask,
  onResolveConflict: handleResolveConflict,
  onDispatchTask: handleDispatchTask,
  onCancelTask: handleCancelTask,
  onApproveTask: handleApproveTask,
  onRequestChanges: handleRequestChanges,
  onOpenSession: handleOpenSession,
  onJumpSpatial: handleJumpSpatial,
}

function openCreateDrawer(defaultStatus: ProjectBoardTaskStatus = 'ready') {
  if (!activeProjects.value.length) {
    c.openProject()
    return
  }
  openBoardTaskInspector({
    mode: 'create',
    ownerScope: 'global',
    projectTasks: board.tasks.value,
    projects: activeProjects.value,
    defaultProjectId: activeProjects.value[0]?.id ?? null,
    defaultStatus,
    ...inspectorCallbacks,
  })
}

function openCreateInGroup(group: BoardTaskGroup) {
  const status: ProjectBoardTaskStatus =
    group.kind === 'status' ? (group.key as ProjectBoardTaskStatus) : 'ready'
  openCreateDrawer(status)
}

function openTaskDrawer(task: ProjectBoardTaskDto) {
  openBoardTaskInspector({
    mode: 'edit',
    ownerScope: 'global',
    projectId: task.project_id,
    taskId: task.task_id,
    task,
    projectTasks: board.tasks.value,
    projects: activeProjects.value,
    defaultProjectId: task.project_id || (activeProjects.value[0]?.id ?? null),
    ...inspectorCallbacks,
  })
}

async function handleCreateTask(projectId: string, input: CreateProjectBoardTaskInput) {
  try {
    const created = await board.createTask(projectId, input)
    openTaskDrawer(created)
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
    closeBoardTaskInspector()
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
  closeBoardTaskInspector()
  await c.setSelectedSession(sessionId)
  await router.push('/')
}

async function handleJumpSpatial(task: ProjectBoardTaskDto) {
  if (!task.spatial_ref) return
  closeBoardTaskInspector()
  stashPendingSpatialFocus(task.spatial_ref)
  await c.setSelectedSession(task.spatial_ref.session_id)
  await router.push(buildSpatialFocusLocation(task.spatial_ref))
}
</script>

<template>
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
          <button
            v-if="headerStats.blocked > 0 || blockedOnlyFilter"
            type="button"
            class="global-preset-btn ui-pressable"
            :class="{ active: blockedOnlyFilter }"
            data-testid="global-board-blocked-filter"
            @click="blockedOnlyFilter = !blockedOnlyFilter"
          >
            <AlertTriangle :size="13" />
            <span>{{ t('board.global.metricBlocked', { count: headerStats.blocked }) }}</span>
          </button>

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

      <!-- Cross-Project Blocked Human Intervention Alert -->
      <div
        v-if="blockedTasks.length > 0"
        class="global-blocked-banner"
        role="region"
        :aria-label="t('board.autopilot.blockedBannerAria')"
        data-testid="global-board-blocked-banner"
      >
        <div class="global-blocked-summary">
          <AlertTriangle :size="13" />
          <strong>{{ t('board.autopilot.blockedBannerTitle', { count: blockedTasks.length }) }}</strong>
        </div>
        <div class="global-blocked-items">
          <div
            v-for="bTask in blockedTasks.slice(0, 5)"
            :key="bTask.task_id"
            class="global-blocked-chip"
            :data-testid="`global-blocked-task-${bTask.task_id}`"
          >
            <span class="global-blocked-key">{{ bTask.task_key }}</span>
            <span class="global-blocked-reason">
              {{ bTask.blocked_reason || bTask.title }}
            </span>
            <button
              type="button"
              class="global-blocked-action ui-pressable"
              :title="t('board.autopilot.openInDrawer')"
              :data-testid="`global-blocked-open-${bTask.task_id}`"
              @click="openTaskDrawer(bTask)"
            >
              <ExternalLink :size="11" />
            </button>
            <button
              type="button"
              class="global-blocked-action ui-pressable"
              :title="t('board.autopilot.unblockAndResume')"
              :data-testid="`global-blocked-unblock-${bTask.task_id}`"
              @click="handleQuickUnblock(bTask)"
            >
              <Play :size="11" />
            </button>
          </div>
        </div>
      </div>

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
        @clear-filters="board.clearFilters(); blockedOnlyFilter = false"
        @new-task="openCreateDrawer()"
      />

      <!-- Layout Surface -->
      <BoardKanbanView
        v-if="board.layout.value === 'kanban'"
        :groups="visibleGroups"
        :visible-fields="board.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="openTaskDrawer"
        @jump-spatial="handleJumpSpatial"
        @new-task-in-group="openCreateInGroup"
        @move-task="handleMoveTask"
      />
      <BoardListView
        v-else-if="board.layout.value === 'list'"
        :groups="visibleGroups"
        :visible-fields="board.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="openTaskDrawer"
        @jump-spatial="handleJumpSpatial"
        @quick-update="(task, patch) => handleSaveTask(task.project_id, task.task_id, patch)"
        @move-task="handleMoveTask"
      />
      <BoardSwimlaneView
        v-else
        :swimlanes="visibleSwimlanes"
        :visible-fields="board.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="openTaskDrawer"
        @jump-spatial="handleJumpSpatial"
        @move-task="handleMoveTask"
      />
  </div>
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
  padding: 14px 16px 6px;
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

.global-blocked-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  margin: 4px 16px 2px;
  padding: 8px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-default, var(--border-muted));
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-primary);
}

.global-blocked-summary {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  font-size: 12px;
}

.global-blocked-items {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.global-blocked-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  font-size: 11.5px;
}

.global-blocked-key {
  font-family: var(--font-mono, monospace);
  font-weight: 600;
  color: var(--text-primary);
}

.global-blocked-reason {
  max-width: 220px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-secondary);
}

.global-blocked-action {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border-radius: 5px;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.global-blocked-action:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}
</style>
