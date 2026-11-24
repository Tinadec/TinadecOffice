<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import {
  Activity,
  AlertTriangle,
  Database,
  FileText,
  FolderOpen,
  HardDrive,
  Lock,
  MessageSquare,
  Plus,
  RefreshCw,
  Save,
  SlidersHorizontal,
  Sparkles,
} from '@lucide/vue'
import {
  UiButton,
  UiInput,
  UiSelectField,
  UiTextarea,
} from '@/components/ui'
import WorkspaceRootsPanel from '@/components/WorkspaceRootsPanel.vue'
import {
  BoardAutopilotBar,
  BoardKanbanView,
  BoardListView,
  BoardSwimlaneView,
  BoardTaskDrawer,
  BoardViewToolbar,
  CoordinatorPlanModal,
  ProjectNotesPanel,
  buildSpatialFocusLocation,
  stashPendingSpatialFocus,
  useProjectBoardView,
  type BoardTaskGroup,
} from '@/components/board'
import {
  api as baseApi,
  type AgentDirectoryItemDto,
  type AgentModeDto,
  type ApproveProjectBoardTaskInput,
  type CreateProjectBoardTaskInput,
  type DispatchProjectBoardTaskInput,
  type ProjectAutopilotStateDto,
  type ProjectBoardConflictResolution,
  type ProjectBoardStorageMode,
  type ProjectBoardTaskDto,
  type ProjectBoardTaskStatus,
  type ProjectNotesDto,
  type ProjectOverviewDto,
  type UpdateProjectBoardTaskInput,
  type WorkspaceDefaultsDto,
} from '@/api'
import { homeController as c } from '@/controllers/HomeController'
import { useHostAccess } from '@/lib/hostAccess'
import { canPickHostDirectories, pickProjectDirectory } from '@/lib/hostConnection'
import { pickLocalized } from '@/lib/packI18n'
import { projectStorageId, scopedApi, selectionIdentity, selectionKey } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'

const emit = defineEmits<{ ready: [] }>()
const route = useRoute()
const router = useRouter()
const { t, locale } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify } = useNotifications()

type HubTab = 'board' | 'context' | 'notes'
const activeTab = ref<HubTab>('board')

const routeProjectId = computed(() => {
  const param = route.params.projectId
  return typeof param === 'string' ? selectionIdentity(param).id : ''
})

const currentProject = computed(() =>
  c.projects.value.find((p) => p.id === routeProjectId.value) ?? null,
)

watch(
  [routeProjectId, () => c.projects.value],
  ([pid, list]) => {
    if (!pid) return
    const match = list.find((p) => p.id === pid)
    if (match && c.selectedProjectId.value !== match.id) {
      c.setSelectedProject(selectionKey(match))
    }
  },
  { immediate: true },
)

const board = useProjectBoardView({
  scope: 'project',
  projectId: () => routeProjectId.value || null,
})

// ---- Commander Strip: Project Overview ----
const overview = ref<ProjectOverviewDto | null>(null)
const overviewLoading = ref(false)
let overviewGeneration = 0

async function loadOverview() {
  const pid = routeProjectId.value
  if (!pid || !canAccessBackend.value) return
  const gen = ++overviewGeneration
  overviewLoading.value = true
  try {
    const res = await baseApi.getProjectOverview(pid)
    if (gen === overviewGeneration && routeProjectId.value === pid) {
      overview.value = res
    }
  } catch {
    if (gen === overviewGeneration && routeProjectId.value === pid) {
      overview.value = null
    }
  } finally {
    if (gen === overviewGeneration) {
      overviewLoading.value = false
    }
  }
}

// ---- Context & Settings State ----
const keyPrefix = ref('TIN')
const projectInstructions = ref('')
const storageMode = ref<ProjectBoardStorageMode>('repo_tinadec')
const customSyncRoot = ref('')
const savingBoardSettings = ref(false)

const workspaceDefaults = ref<WorkspaceDefaultsDto | null>(null)
const modesCatalog = ref<AgentModeDto[]>([])
const agentsCatalog = ref<AgentDirectoryItemDto[]>([])
const defaultModeId = ref('')
const defaultAgentId = ref('')
const savingDefaults = ref(false)
let defaultsGeneration = 0

watch(
  () => board.settings.value,
  (s) => {
    if (!s) {
      keyPrefix.value = 'TIN'
      projectInstructions.value = ''
      storageMode.value = 'repo_tinadec'
      customSyncRoot.value = ''
      return
    }
    keyPrefix.value = s.key_prefix || 'TIN'
    projectInstructions.value = s.project_instructions ?? ''
    storageMode.value = s.storage_mode || 'repo_tinadec'
    customSyncRoot.value = s.custom_sync_root ?? ''
  },
  { immediate: true },
)

async function loadProjectWorkspaceDefaults() {
  const pid = routeProjectId.value
  if (!pid || !canAccessBackend.value) return
  const gen = ++defaultsGeneration
  const pStorage = currentProject.value?.storage_id ?? projectStorageId(pid)
  const pApi = scopedApi(baseApi, () => pStorage)
  try {
    const [defs, modes, agents] = await Promise.all([
      pApi.getWorkspaceDefaults().catch(() => null),
      pApi.listAgentModes().catch(() => [] as AgentModeDto[]),
      pApi.listAgents().catch(() => [] as AgentDirectoryItemDto[]),
    ])
    if (gen !== defaultsGeneration || routeProjectId.value !== pid) return
    workspaceDefaults.value = defs
    modesCatalog.value = modes.filter(
      (m) => m.status === 'published' && Boolean(m.latest_published_mode_version_id),
    )
    agentsCatalog.value = agents.filter((a) => a.enabled !== false && a.source_kind !== 'missing_reference')
    defaultModeId.value = defs?.default_agent_mode_id ?? ''
    defaultAgentId.value = defs?.default_agent_definition_id ?? ''
  } catch {
    // Graceful fallback if defaults endpoint unavailable.
  }
}

watch(
  routeProjectId,
  (pid, prevPid) => {
    if (prevPid !== undefined && pid !== prevPid) {
      overview.value = null
      workspaceDefaults.value = null
      defaultModeId.value = ''
      defaultAgentId.value = ''
      drawerOpen.value = false
      selectedTaskId.value = null
      planModalOpen.value = false
    }
  },
)

watch(
  [routeProjectId, canAccessBackend],
  ([pid, ready]) => {
    if (pid && ready) {
      void loadOverview()
      void loadProjectWorkspaceDefaults()
    }
  },
  { immediate: true },
)

onMounted(async () => {
  await nextTick()
  emit('ready')
})

const modeSelectOptions = computed(() => [
  { value: '', label: t('settings.workspaceDefaultNone') },
  ...modesCatalog.value.map((m) => ({
    value: m.id,
    label: pickLocalized(m.i18n, locale.value, 'display_name', m.display_name) ?? m.display_name,
  })),
])

const agentSelectOptions = computed(() => [
  { value: '', label: t('settings.workspaceDefaultNone') },
  ...agentsCatalog.value.map((a) => ({
    value: a.id,
    label: pickLocalized(a.i18n, locale.value, 'display_name', a.display_name) ?? a.display_name,
  })),
])

const resolvedBoardTasksPath = computed(() => {
  if (board.settings.value?.resolved_board_dir) {
    return `${board.settings.value.resolved_board_dir.replace(/[\\/]+$/, '')}/tasks/*.md`
  }
  const repoRoot = currentProject.value?.path ?? '<repo>'
  if (storageMode.value === 'repo_tinadec') {
    return `${repoRoot.replace(/[\\/]+$/, '')}/.tinadec/board/tasks/*.md`
  }
  if (storageMode.value === 'custom_dir') {
    const base = customSyncRoot.value.trim() || '<custom_dir>'
    return `${base.replace(/[\\/]+$/, '')}/board/tasks/*.md`
  }
  const appRoot = currentProject.value?.storage_root ?? '<app_data>'
  return `${appRoot.replace(/[\\/]+$/, '')}/board/tasks/*.md`
})

const resolvedNotesPath = computed(() => {
  const repoRoot = currentProject.value?.path ?? '<repo>'
  if (storageMode.value === 'repo_tinadec') {
    return `${repoRoot.replace(/[\\/]+$/, '')}/.tinadec/project/notes.md`
  }
  if (storageMode.value === 'custom_dir') {
    const base = customSyncRoot.value.trim() || '<custom_dir>'
    return `${base.replace(/[\\/]+$/, '')}/project/notes.md`
  }
  const appRoot = currentProject.value?.storage_root ?? '<app_data>'
  return `${appRoot.replace(/[\\/]+$/, '')}/project/notes.md`
})

async function pickCustomSyncDirectory() {
  if (!canPickHostDirectories()) return
  const picked = await pickProjectDirectory(t('board.hub.pickCustomDirTitle'), {
    initialPath: customSyncRoot.value || currentProject.value?.path || undefined,
  })
  if (picked) {
    customSyncRoot.value = picked
  }
}

async function handleSaveBoardSettings() {
  if (!routeProjectId.value || savingBoardSettings.value) return
  savingBoardSettings.value = true
  try {
    const normalizedPrefix =
      keyPrefix.value
        .trim()
        .toUpperCase()
        .replace(/[^A-Z0-9]/g, '')
        .slice(0, 10) || 'TIN'
    keyPrefix.value = normalizedPrefix
    await board.saveSettings({
      key_prefix: normalizedPrefix,
      storage_mode: storageMode.value,
      custom_sync_root: storageMode.value === 'custom_dir' ? customSyncRoot.value.trim() || null : null,
      project_instructions: projectInstructions.value,
    })
    notify.success(t('board.hub.settingsSaved'))
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  } finally {
    savingBoardSettings.value = false
  }
}

async function handleSaveWorkspaceDefaults() {
  const pid = routeProjectId.value
  if (!pid || savingDefaults.value) return
  savingDefaults.value = true
  const pStorage = currentProject.value?.storage_id ?? projectStorageId(pid)
  const pApi = scopedApi(baseApi, () => pStorage)
  try {
    const mode = modesCatalog.value.find((m) => m.id === defaultModeId.value) ?? null
    const nextAgentId = defaultAgentId.value || null
    const agentChanged = nextAgentId !== (workspaceDefaults.value?.default_agent_definition_id ?? null)
    const draft = await pApi.updateWorkspaceDefaultsDraft(
      {
        default_agent_definition_id: nextAgentId,
        default_agent_version_id: nextAgentId
          ? agentChanged
            ? null
            : (workspaceDefaults.value?.default_agent_version_id ?? null)
          : null,
        default_agent_mode_id: mode?.id ?? null,
        default_mode_version_id: mode?.latest_published_mode_version_id ?? null,
      },
      workspaceDefaults.value?.revision ?? null,
    )
    const published = await pApi.publishWorkspaceDefaults(draft.revision ?? null)
    workspaceDefaults.value = published
    notify.success(t('settings.workspaceDefaultsSaved'))
  } catch (err) {
    notify.error(err, { title: t('settings.workspaceDefaultsSaveFailed') })
  } finally {
    savingDefaults.value = false
  }
}

async function handleSyncTinadec() {
  try {
    const res = await board.syncBoard()
    if (res) {
      notify.success(
        t('board.hub.syncCompletedSummary', {
          imported: res.imported_count,
          exported: res.exported_count,
          conflicts: res.conflict_count,
        }),
        { key: 'board-tinadec-sync' },
      )
    }
  } catch (err) {
    notify.error(err, { title: t('board.errors.syncFailed'), key: 'board-tinadec-sync' })
  }
}

function handleStartNewChat() {
  const proj = currentProject.value
  c.startNewConversation(proj ? selectionKey(proj) : routeProjectId.value)
  void router.push('/')
}

// ---- Task Drawer, Coordinator Planner & Autopilot Actions ----
const drawerOpen = ref(false)
const drawerMode = ref<'create' | 'edit'>('edit')
const selectedTaskId = ref<string | null>(null)
const createDefaultStatus = ref<ProjectBoardTaskStatus>('ready')
const planModalOpen = ref(false)

function handleNotesSaved(savedNotes: ProjectNotesDto) {
  board.notes.value = savedNotes
}

function handleAutopilotUpdated(nextState: ProjectAutopilotStateDto) {
  board.autopilot.value = nextState
  if (board.settings.value) {
    board.settings.value = {
      ...board.settings.value,
      autopilot_enabled: nextState.autopilot_enabled,
      max_concurrency: nextState.max_concurrency,
    }
  }
  void loadOverview()
}

function handleUnblockedTask(updatedTask: ProjectBoardTaskDto) {
  board.upsertTaskLocal(updatedTask)
  void board.reloadAutopilot().catch(() => undefined)
}

function handlePlanApplied(createdTasks: ProjectBoardTaskDto[]) {
  for (const task of createdTasks) {
    board.upsertTaskLocal(task)
  }
  activeTab.value = 'board'
  void board.reloadNotes().catch(() => undefined)
  void board.reloadAutopilot().catch(() => undefined)
  notify.success(t('board.coordinator.appliedSuccess', { count: createdTasks.length }))
}

const selectedTask = computed<ProjectBoardTaskDto | null>(() => {
  if (!selectedTaskId.value) return null
  return board.tasks.value.find((item) => item.task_id === selectedTaskId.value) ?? null
})

function openCreateDrawer(defaultStatus: ProjectBoardTaskStatus = 'ready') {
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
    const created = await board.createTask(projectId || routeProjectId.value, input)
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
    void loadOverview()
  } catch (err) {
    notify.error(err, { title: t('board.errors.dispatchFailed') })
  }
}

async function handleCancelTask(projectId: string, taskId: string) {
  try {
    await board.cancelTask(projectId, taskId)
    void loadOverview()
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
    void loadOverview()
  } catch (err) {
    notify.error(err, { title: t('board.errors.approveFailed') })
  }
}

async function handleRequestChanges(projectId: string, taskId: string, feedback: string) {
  try {
    await board.requestChanges(projectId, taskId, feedback)
    void loadOverview()
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
  <div class="project-hub-page" data-testid="project-hub-page">
      <!-- Top Commander Strip -->
      <header class="hub-commander-strip" data-testid="hub-commander-strip">
        <div class="hub-title-area">
          <div class="hub-title-row">
            <h1 class="hub-project-name">
              {{ currentProject?.name || overview?.name || t('board.hub.projectTitleFallback') }}
            </h1>
            <span v-if="board.settings.value?.key_prefix" class="hub-prefix-pill">
              {{ board.settings.value.key_prefix }}
            </span>
          </div>
          <p v-if="currentProject?.path" class="hub-project-path" :title="currentProject.path">
            {{ currentProject.path }}
          </p>
        </div>

        <!-- Live Overview Counters + Quick Actions -->
        <div class="hub-commander-right">
          <div class="hub-stat-pills">
            <span class="hub-stat-pill" data-testid="hub-stat-active-runs">
              <Activity :size="12" />
              <span>
                {{
                  t('board.hub.statActiveRuns', {
                    count: overview?.active_runs?.length ?? board.boardStats.value.inProgress,
                  })
                }}
              </span>
            </span>
            <span class="hub-stat-pill" data-testid="hub-stat-open-reports">
              <AlertTriangle :size="12" />
              <span>{{ t('board.hub.statOpenReports', { count: overview?.open_reports?.length ?? 0 }) }}</span>
            </span>
            <span class="hub-stat-pill" data-testid="hub-stat-leases">
              <Lock :size="12" />
              <span>{{ t('board.hub.statLeases', { count: overview?.leases?.length ?? 0 }) }}</span>
            </span>
            <span class="hub-stat-pill" data-testid="hub-stat-sessions">
              <MessageSquare :size="12" />
              <span>{{ t('board.hub.statSessions', { count: overview?.session_count ?? 0 }) }}</span>
            </span>
          </div>

          <div class="hub-quick-actions">
            <UiButton
              type="button"
              variant="outline"
              size="sm"
              :disabled="board.syncing.value"
              data-testid="hub-sync-tinadec-btn"
              @click="handleSyncTinadec"
            >
              <RefreshCw :size="13" :class="{ 'animate-spin': board.syncing.value }" />
              <span>{{ t('board.hub.syncTinadec') }}</span>
            </UiButton>

            <UiButton
              type="button"
              variant="outline"
              size="sm"
              data-testid="hub-plan-tasks-btn"
              @click="planModalOpen = true"
            >
              <Sparkles :size="13" />
              <span>{{ t('board.coordinator.planTasksBtn') }}</span>
            </UiButton>

            <UiButton
              type="button"
              variant="outline"
              size="sm"
              data-testid="hub-new-chat-btn"
              @click="handleStartNewChat"
            >
              <MessageSquare :size="13" />
              <span>{{ t('sidebar.newChat') }}</span>
            </UiButton>

            <button
              type="button"
              class="hub-primary-btn ui-pressable"
              data-testid="hub-new-task-btn"
              @click="openCreateDrawer()"
            >
              <Plus :size="14" />
              <span>{{ t('board.newTask') }}</span>
            </button>
          </div>
        </div>
      </header>

      <!-- Phase 3 (#56): Autopilot Control Strip & Blocked Human Intervention Banner -->
      <BoardAutopilotBar
        v-if="routeProjectId"
        :project-id="routeProjectId"
        :autopilot="board.autopilot.value"
        :settings="board.settings.value"
        :tasks="board.tasks.value"
        @updated="handleAutopilotUpdated"
        @open-task="openTaskDrawer"
        @unblocked-task="handleUnblockedTask"
      />

      <!-- 3 Hub Tabs: Board | Context & Settings | Notes -->
      <nav class="hub-tabs-bar" role="tablist" :aria-label="t('board.hub.tabsAria')">
        <button
          type="button"
          role="tab"
          class="hub-tab-btn ui-pressable"
          :class="{ active: activeTab === 'board' }"
          :aria-selected="activeTab === 'board'"
          data-testid="hub-tab-board"
          @click="activeTab = 'board'"
        >
          <span>{{ t('board.hub.tabBoard') }}</span>
          <span class="hub-tab-count">{{ board.boardStats.value.total }}</span>
        </button>

        <button
          type="button"
          role="tab"
          class="hub-tab-btn ui-pressable"
          :class="{ active: activeTab === 'context' }"
          :aria-selected="activeTab === 'context'"
          data-testid="hub-tab-context"
          @click="activeTab = 'context'"
        >
          <SlidersHorizontal :size="13" />
          <span>{{ t('board.hub.tabContext') }}</span>
        </button>

        <button
          type="button"
          role="tab"
          class="hub-tab-btn ui-pressable"
          :class="{ active: activeTab === 'notes' }"
          :aria-selected="activeTab === 'notes'"
          data-testid="hub-tab-notes"
          @click="activeTab = 'notes'"
        >
          <FileText :size="13" />
          <span>{{ t('board.hub.tabNotes') }}</span>
        </button>
      </nav>

      <!-- TAB 1: Composable Project Board -->
      <div v-if="activeTab === 'board'" class="hub-board-pane">
        <BoardViewToolbar
          scope="project"
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
          @plan-tasks="planModalOpen = true"
          @new-task="openCreateDrawer()"
        />

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
      </div>

      <!-- TAB 2: Context & Settings (Claude Projects style) -->
      <div v-else-if="activeTab === 'context'" class="hub-scroll-pane" data-testid="hub-context-panel">
        <div class="hub-context-grid">
          <!-- Left Column: Multi-folder Workspace Roots & File Grants -->
          <section class="hub-card">
            <WorkspaceRootsPanel />
          </section>

          <!-- Right Column: Project Instructions, Defaults & 3-Way Storage Selector -->
          <div class="hub-context-stack">
            <section class="hub-card">
              <div class="hub-card-header">
                <h2 class="hub-card-title">{{ t('board.hub.instructionsAndPrefixTitle') }}</h2>
              </div>

              <div class="hub-form-field">
                <label class="hub-field-label">{{ t('board.hub.keyPrefixLabel') }}</label>
                <div class="hub-prefix-input-row">
                  <UiInput
                    v-model="keyPrefix"
                    maxlength="10"
                    placeholder="TIN"
                    data-testid="hub-key-prefix-input"
                  />
                  <span class="hub-field-hint">
                    {{ t('board.hub.keyPrefixPreview', { key: `${keyPrefix || 'TIN'}-1` }) }}
                  </span>
                </div>
              </div>

              <div class="hub-form-field">
                <label class="hub-field-label">{{ t('board.hub.projectInstructionsLabel') }}</label>
                <p class="hub-field-hint">{{ t('board.hub.projectInstructionsHint') }}</p>
                <UiTextarea
                  v-model="projectInstructions"
                  :rows="5"
                  :placeholder="t('board.hub.projectInstructionsPlaceholder')"
                  data-testid="hub-project-instructions-input"
                />
              </div>

              <!-- 3-Way Storage Mode Selector -->
              <div class="hub-form-field">
                <label class="hub-field-label">{{ t('board.hub.storageModeTitle') }}</label>
                <div class="hub-storage-cards" role="radiogroup" :aria-label="t('board.hub.storageModeTitle')">
                  <button
                    type="button"
                    class="hub-storage-card ui-pressable"
                    :class="{ active: storageMode === 'repo_tinadec' }"
                    data-testid="hub-storage-mode-repo_tinadec"
                    @click="storageMode = 'repo_tinadec'"
                  >
                    <div class="hub-storage-card-top">
                      <FolderOpen :size="14" />
                      <span>{{ t('board.hub.storageRepoTinadec') }}</span>
                    </div>
                    <p>{{ t('board.hub.storageRepoTinadecDesc') }}</p>
                  </button>

                  <button
                    type="button"
                    class="hub-storage-card ui-pressable"
                    :class="{ active: storageMode === 'app_data' }"
                    data-testid="hub-storage-mode-app_data"
                    @click="storageMode = 'app_data'"
                  >
                    <div class="hub-storage-card-top">
                      <Database :size="14" />
                      <span>{{ t('board.hub.storageAppData') }}</span>
                    </div>
                    <p>{{ t('board.hub.storageAppDataDesc') }}</p>
                  </button>

                  <button
                    type="button"
                    class="hub-storage-card ui-pressable"
                    :class="{ active: storageMode === 'custom_dir' }"
                    data-testid="hub-storage-mode-custom_dir"
                    @click="storageMode = 'custom_dir'"
                  >
                    <div class="hub-storage-card-top">
                      <HardDrive :size="14" />
                      <span>{{ t('board.hub.storageCustomDir') }}</span>
                    </div>
                    <p>{{ t('board.hub.storageCustomDirDesc') }}</p>
                  </button>
                </div>

                <div v-if="storageMode === 'custom_dir'" class="hub-custom-dir-row">
                  <UiInput
                    v-model="customSyncRoot"
                    :placeholder="t('board.hub.customDirPlaceholder')"
                    data-testid="hub-custom-dir-input"
                  />
                  <UiButton
                    type="button"
                    variant="outline"
                    size="sm"
                    data-testid="hub-pick-custom-dir-btn"
                    @click="pickCustomSyncDirectory"
                  >
                    <FolderOpen :size="13" />
                    <span>{{ t('board.hub.browseDirectory') }}</span>
                  </UiButton>
                </div>

                <div class="hub-resolved-path">
                  <span>{{ t('board.hub.resolvedTasksPath') }}:</span>
                  <code>{{ resolvedBoardTasksPath }}</code>
                </div>
              </div>

              <div class="hub-card-actions">
                <button
                  type="button"
                  class="hub-primary-btn ui-pressable"
                  :disabled="savingBoardSettings"
                  data-testid="hub-save-board-settings-btn"
                  @click="handleSaveBoardSettings"
                >
                  <Save :size="13" />
                  <span>{{ t('board.hub.saveBoardSettings') }}</span>
                </button>
              </div>
            </section>

            <!-- Project Default Mode & Default Agent -->
            <section class="hub-card">
              <div class="hub-card-header">
                <h2 class="hub-card-title">{{ t('board.hub.workspaceDefaultsTitle') }}</h2>
              </div>
              <p class="hub-field-hint">{{ t('board.hub.workspaceDefaultsHint') }}</p>

              <div class="hub-defaults-grid">
                <div class="hub-form-field">
                  <label class="hub-field-label">{{ t('board.hub.defaultModeLabel') }}</label>
                  <UiSelectField v-model="defaultModeId" :options="modeSelectOptions" />
                </div>
                <div class="hub-form-field">
                  <label class="hub-field-label">{{ t('board.hub.defaultAgentLabel') }}</label>
                  <UiSelectField v-model="defaultAgentId" :options="agentSelectOptions" />
                </div>
              </div>

              <div class="hub-card-actions">
                <UiButton
                  type="button"
                  variant="outline"
                  size="sm"
                  :disabled="savingDefaults"
                  data-testid="hub-save-defaults-btn"
                  @click="handleSaveWorkspaceDefaults"
                >
                  <Save :size="13" />
                  <span>{{ t('board.hub.saveDefaults') }}</span>
                </UiButton>
              </div>
            </section>
          </div>
        </div>
      </div>

      <!-- TAB 3: Living Project Notes Studio (Phase 3 #56) -->
      <div v-else class="hub-notes-pane" data-testid="hub-notes-panel">
        <ProjectNotesPanel
          :project-id="routeProjectId"
          :notes="board.notes.value"
          :fallback-resolved-path="resolvedNotesPath"
          :fallback-storage-mode="storageMode"
          @saved="handleNotesSaved"
          @reloaded="handleNotesSaved"
        />
      </div>

      <!-- Coordinator Smart Task Planner Modal -->
      <CoordinatorPlanModal
        v-model:open="planModalOpen"
        :project-id="routeProjectId"
        :existing-tasks="board.tasks.value"
        :default-assignee-kind="board.settings.value?.default_assignee_kind"
        @applied="handlePlanApplied"
      />

      <!-- Right Slide-over Task Drawer -->
      <BoardTaskDrawer
        v-model:open="drawerOpen"
        :mode="drawerMode"
        :task="selectedTask"
        :project-tasks="board.tasks.value"
        :default-project-id="routeProjectId"
        :default-status="createDefaultStatus"
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
</template>

<style scoped>
.project-hub-page {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.hub-commander-strip {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 14px 16px 8px;
  flex-wrap: wrap;
}

.hub-title-area {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.hub-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.hub-project-name {
  margin: 0;
  font-size: 17px;
  font-weight: 600;
  color: var(--text-primary);
}

.hub-prefix-pill {
  padding: 1px 7px;
  border-radius: 999px;
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  font-weight: 600;
  background: var(--surface-section, var(--bg-tertiary));
  color: var(--text-secondary);
}

.hub-project-path {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-muted);
  max-width: 440px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.hub-commander-right {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.hub-stat-pills {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.hub-stat-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 9px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 11px;
}

.hub-quick-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.hub-primary-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 12px;
  border-radius: var(--radius-control, 6px);
  border: 0;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.hub-primary-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.hub-tabs-bar {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 0 16px 6px;
}

.hub-tab-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 12px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid transparent;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12.5px;
  font-weight: 500;
  cursor: pointer;
}

.hub-tab-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.hub-tab-btn.active {
  background: var(--surface-selected);
  border-color: var(--border-card-active, var(--border-muted));
  color: var(--text-primary);
}

.hub-tab-count {
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 11px;
  background: var(--surface-section, var(--bg-tertiary));
  color: var(--text-muted);
}

.hub-board-pane,
.hub-notes-pane {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.hub-scroll-pane {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 8px 16px 24px;
}

.hub-context-grid {
  display: grid;
  grid-template-columns: minmax(300px, 1fr) minmax(340px, 1.3fr);
  gap: 14px;
  align-items: start;
}

@media (max-width: 980px) {
  .hub-context-grid {
    grid-template-columns: 1fr;
  }
}

.hub-context-stack {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.hub-card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px;
  border-radius: var(--radius-island, 12px);
  border: 1px solid var(--border-card, var(--border-muted));
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 65%, transparent);
}

.hub-card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.hub-card-title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  color: var(--text-primary);
}

.hub-form-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.hub-field-label {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
}

.hub-field-hint {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-muted);
}

.hub-prefix-input-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.hub-prefix-input-row :deep(input) {
  max-width: 120px;
  font-family: var(--font-mono, monospace);
}

.hub-storage-cards {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

@media (max-width: 720px) {
  .hub-storage-cards {
    grid-template-columns: 1fr;
  }
}

.hub-storage-card {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 10px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
}

.hub-storage-card:hover {
  border-color: var(--border-default);
}

.hub-storage-card.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.hub-storage-card-top {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 600;
}

.hub-storage-card p {
  margin: 0;
  font-size: 11px;
  opacity: 0.82;
  line-height: 1.35;
}

.hub-custom-dir-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.hub-resolved-path {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  padding: 6px 8px;
  border-radius: 6px;
  background: var(--surface-raised, var(--bg-tertiary));
  font-size: 11px;
  color: var(--text-secondary);
}

.hub-resolved-path code {
  font-family: var(--font-mono, monospace);
  color: var(--text-primary);
}

.hub-defaults-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}

.hub-card-actions {
  display: flex;
  justify-content: flex-end;
}

.hub-notes-card {
  max-width: 720px;
}

.hub-notes-header {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  color: var(--text-primary);
}

.hub-notes-meta-grid {
  display: grid;
  gap: 10px;
  padding: 12px;
  border-radius: 8px;
  background: var(--surface-raised, var(--bg-secondary));
}

.hub-notes-meta-item {
  display: flex;
  flex-direction: column;
  gap: 3px;
  font-size: 12px;
}

.hub-notes-meta-item code {
  font-family: var(--font-mono, monospace);
  font-size: 11.5px;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.hub-notes-roadmap {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
  line-height: 1.5;
}
</style>
