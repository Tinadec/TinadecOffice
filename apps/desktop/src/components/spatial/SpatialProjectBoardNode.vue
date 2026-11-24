<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Handle, Position } from '@vue-flow/core'
import { NodeResizer } from '@vue-flow/node-resizer'
import { Columns3, List, Plus, RefreshCw, Rows3 } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type {
  ProjectBoardFiltersDto,
  ProjectBoardGroupBy,
  ProjectBoardLayoutMode,
  ProjectBoardSortBy,
  ProjectBoardSwimlaneBy,
  ProjectBoardTaskDto,
  ProjectBoardTaskStatus,
  ProjectBoardVisibleField,
  UpdateProjectBoardTaskInput,
} from '@/api'
import {
  BoardKanbanView,
  BoardListView,
  BoardSwimlaneView,
  createDefaultBoardFilters,
  useProjectBoardView,
  type BoardTaskGroup,
} from '@/components/board'
import {
  sanitizeSpatialBoardWidgetProps,
  type SpatialBoardWidgetProps,
} from './spatialWhiteboardTypes'

const props = withDefaults(defineProps<{
  id: string
  sessionProjectId?: string | null
  projectName?: string | null
  projects?: Array<{ id: string; name: string }>
  project_id?: string | null
  title?: string
  layout?: ProjectBoardLayoutMode
  group_by?: ProjectBoardGroupBy
  swimlane_by?: ProjectBoardSwimlaneBy
  sort_by?: ProjectBoardSortBy
  filters?: ProjectBoardFiltersDto
  visible_fields?: ProjectBoardVisibleField[]
  compact?: boolean
  saved_view_id?: string | null
  selectedTaskId?: string | null
  selected?: boolean
  readonly?: boolean
  data?: Partial<SpatialBoardWidgetProps> & Record<string, unknown>
}>(), {
  selected: false,
  readonly: false,
  sessionProjectId: null,
  projectName: null,
  selectedTaskId: null,
})

const emit = defineEmits<{
  'update:widget-props': [patch: Partial<SpatialBoardWidgetProps>]
  'select-task': [task: ProjectBoardTaskDto]
  'new-task': [payload: { projectId: string; status: ProjectBoardTaskStatus }]
  'jump-spatial': [task: ProjectBoardTaskDto]
  'drop-task-to-canvas': [payload: { task: ProjectBoardTaskDto; clientX: number; clientY: number }]
  'resize-start': []
  'resize-end': [params: { x: number; y: number; width: number; height: number }]
}>()

const { t } = useI18n()
const rootEl = ref<HTMLElement | null>(null)
const sides = [Position.Top, Position.Bottom, Position.Left, Position.Right] as const

const effectiveProjectId = computed<string | null>(() => {
  const explicit = props.project_id ?? props.data?.project_id
  if (typeof explicit === 'string' && explicit.trim()) return explicit.trim()
  if (typeof props.sessionProjectId === 'string' && props.sessionProjectId.trim()) {
    return props.sessionProjectId.trim()
  }
  return props.projects?.[0]?.id ?? null
})

const resolvedLayoutProp = computed<ProjectBoardLayoutMode | undefined>(() => {
  const raw = props.layout ?? props.data?.layout
  return raw === 'kanban' || raw === 'list' || raw === 'swimlane' ? raw : undefined
})

const resolvedGroupByProp = computed<ProjectBoardGroupBy | undefined>(() => {
  const raw = props.group_by ?? props.data?.group_by
  return raw === 'status' || raw === 'project' || raw === 'assignee' || raw === 'priority' || raw === 'none'
    ? raw
    : undefined
})

const resolvedSwimlaneByProp = computed<ProjectBoardSwimlaneBy | undefined>(() => {
  const raw = props.swimlane_by ?? props.data?.swimlane_by
  return raw === 'project' || raw === 'assignee' ? raw : undefined
})

const resolvedSortByProp = computed<ProjectBoardSortBy | undefined>(() => {
  const raw = props.sort_by ?? props.data?.sort_by
  return raw === 'manual' || raw === 'priority' || raw === 'updated_at' || raw === 'dependency'
    ? raw
    : undefined
})

const resolvedFiltersProp = computed<ProjectBoardFiltersDto | undefined>(() => {
  const raw = props.filters ?? props.data?.filters
  return raw && typeof raw === 'object' ? raw : undefined
})

const resolvedVisibleFieldsProp = computed<ProjectBoardVisibleField[] | undefined>(() => {
  const raw = props.visible_fields ?? props.data?.visible_fields
  return Array.isArray(raw) ? raw : undefined
})

const boardView = useProjectBoardView({
  scope: 'project',
  projectId: () => effectiveProjectId.value,
  projects: () => props.projects ?? [],
  initialConfig: {
    layout: resolvedLayoutProp.value ?? 'kanban',
    group_by: resolvedGroupByProp.value ?? 'status',
    swimlane_by: resolvedSwimlaneByProp.value ?? 'assignee',
    sort_by: resolvedSortByProp.value ?? 'manual',
    filters: resolvedFiltersProp.value ?? createDefaultBoardFilters(),
    ...(resolvedVisibleFieldsProp.value ? { visible_fields: resolvedVisibleFieldsProp.value } : {}),
  },
})

// Keep view projection synchronized when remote LWW props change on the spatial document.
watch(
  () => [
    resolvedLayoutProp.value,
    resolvedGroupByProp.value,
    resolvedSwimlaneByProp.value,
    resolvedSortByProp.value,
    JSON.stringify(resolvedFiltersProp.value ?? null),
    JSON.stringify(resolvedVisibleFieldsProp.value ?? null),
  ] as const,
  ([nextLayout, nextGroupBy, nextSwimlaneBy, nextSortBy]) => {
    if (nextLayout && boardView.layout.value !== nextLayout) {
      boardView.layout.value = nextLayout
    }
    if (nextGroupBy && boardView.groupBy.value !== nextGroupBy) {
      boardView.groupBy.value = nextGroupBy
    }
    if (nextSwimlaneBy && boardView.swimlaneBy.value !== nextSwimlaneBy) {
      boardView.swimlaneBy.value = nextSwimlaneBy
    }
    if (nextSortBy && boardView.sortBy.value !== nextSortBy) {
      boardView.sortBy.value = nextSortBy
    }
    if (resolvedFiltersProp.value) {
      const merged = { ...createDefaultBoardFilters(), ...resolvedFiltersProp.value }
      if (JSON.stringify(boardView.filters.value) !== JSON.stringify(merged)) {
        boardView.filters.value = merged
      }
    }
    if (resolvedVisibleFieldsProp.value) {
      if (JSON.stringify(boardView.visibleFields.value) !== JSON.stringify(resolvedVisibleFieldsProp.value)) {
        boardView.visibleFields.value = [...resolvedVisibleFieldsProp.value]
      }
    }
  },
)

const headerTitle = computed(() => {
  const custom = (props.title ?? props.data?.title ?? '').trim()
  if (custom) return custom
  if (effectiveProjectId.value && props.projects?.length) {
    const matched = props.projects.find((p) => p.id === effectiveProjectId.value)
    if (matched?.name) return matched.name
  }
  if (props.projectName?.trim()) return props.projectName.trim()
  return t('space.boardBridge.widgetDefaultTitle')
})

const isAllFilterActive = computed(() => boardView.activeFilterCount.value === 0)
const isNeedsActionActive = computed(() => Boolean(boardView.filters.value.needs_my_action))

function hasStatusFilter(status: ProjectBoardTaskStatus): boolean {
  return Boolean(boardView.filters.value.statuses?.includes(status))
}

function hasLabelFilter(label: string): boolean {
  return Boolean(boardView.filters.value.labels?.includes(label))
}

function emitProjectionPatch(patch: Partial<SpatialBoardWidgetProps>) {
  if (props.readonly) return
  const clean = sanitizeSpatialBoardWidgetProps(patch) as Partial<SpatialBoardWidgetProps>
  emit('update:widget-props', clean)
}

function setLayout(mode: ProjectBoardLayoutMode) {
  boardView.layout.value = mode
  emitProjectionPatch({ layout: mode })
}

function clearQuickFilters() {
  const clean = createDefaultBoardFilters()
  boardView.filters.value = clean
  emitProjectionPatch({ filters: clean })
}

function toggleNeedsAction() {
  const next: ProjectBoardFiltersDto = {
    ...boardView.filters.value,
    needs_my_action: !boardView.filters.value.needs_my_action,
  }
  boardView.filters.value = next
  emitProjectionPatch({ filters: next })
}

function toggleStatusFilter(status: ProjectBoardTaskStatus) {
  const current = boardView.filters.value.statuses ?? []
  const nextStatuses = current.includes(status)
    ? current.filter((item) => item !== status)
    : [...current, status]
  const next: ProjectBoardFiltersDto = {
    ...boardView.filters.value,
    statuses: nextStatuses,
  }
  boardView.filters.value = next
  emitProjectionPatch({ filters: next })
}

function toggleLabelFilter(label: string) {
  const current = boardView.filters.value.labels ?? []
  const nextLabels = current.includes(label)
    ? current.filter((item) => item !== label)
    : [...current, label]
  const next: ProjectBoardFiltersDto = {
    ...boardView.filters.value,
    labels: nextLabels,
  }
  boardView.filters.value = next
  emitProjectionPatch({ filters: next })
}

function onProjectSelectChange(event: Event) {
  const nextId = (event.target as HTMLSelectElement).value || null
  emitProjectionPatch({ project_id: nextId })
}

function onNewTask(status: ProjectBoardTaskStatus = 'ready') {
  if (!effectiveProjectId.value) return
  emit('new-task', { projectId: effectiveProjectId.value, status })
}

function onNewTaskInGroup(group: BoardTaskGroup) {
  const status: ProjectBoardTaskStatus =
    group.kind === 'status' ? (group.key as ProjectBoardTaskStatus) : 'ready'
  onNewTask(status)
}

async function onMoveTask(payload: {
  task: ProjectBoardTaskDto
  peers: ProjectBoardTaskDto[]
  targetIndex: number
  patch: UpdateProjectBoardTaskInput
}) {
  try {
    await boardView.moveTask(payload.task, payload.peers, payload.targetIndex, payload.patch)
  } catch {
    // Move conflict already refreshes authoritative state inside useProjectBoardView.
  }
}

async function onQuickUpdate(task: ProjectBoardTaskDto, patch: UpdateProjectBoardTaskInput) {
  try {
    await boardView.updateTask(task.project_id, task.task_id, patch)
  } catch {
    // Handled by boardView error state.
  }
}

// Track dragged task inside the widget so dragging a card outside the widget drops a linked sticky on the canvas.
const activeDraggedTask = ref<ProjectBoardTaskDto | null>(null)

function onBodyDragStart(event: DragEvent) {
  const rawJson = event.dataTransfer?.getData('application/x-tinadec-board-task-json')
  if (rawJson) {
    try {
      const parsed = JSON.parse(rawJson) as { task_id?: string }
      if (parsed.task_id) {
        activeDraggedTask.value =
          boardView.tasks.value.find((item) => item.task_id === parsed.task_id) ?? null
        return
      }
    } catch {
      // Fallback to task_id lookup below.
    }
  }
  const taskId = event.dataTransfer?.getData('text/x-tinadec-board-task')
  if (taskId) {
    activeDraggedTask.value = boardView.tasks.value.find((item) => item.task_id === taskId) ?? null
  }
}

function onBodyDragEnd(event: DragEvent) {
  const dragged = activeDraggedTask.value
  activeDraggedTask.value = null
  if (!dragged || !rootEl.value) return
  if (event.clientX === 0 && event.clientY === 0) return
  const rect = rootEl.value.getBoundingClientRect()
  if (rect.width <= 0 || rect.height <= 0) return
  const inside =
    event.clientX >= rect.left &&
    event.clientX <= rect.right &&
    event.clientY >= rect.top &&
    event.clientY <= rect.bottom
  if (!inside) {
    emit('drop-task-to-canvas', {
      task: dragged,
      clientX: event.clientX,
      clientY: event.clientY,
    })
  }
}
</script>

<template>
  <section
    ref="rootEl"
    class="spatial-board-node"
    :class="{ 'is-selected': selected }"
    :data-board-widget-id="id"
    data-testid="spatial-project-board-node"
  >
    <template v-for="side in sides" :key="side">
      <Handle :id="`${side}-target`" type="target" :position="side" class="spatial-board-handle" />
      <Handle :id="`${side}-source`" type="source" :position="side" class="spatial-board-handle" />
    </template>

    <NodeResizer
      :is-visible="selected && !readonly"
      :min-width="480"
      :min-height="300"
      :max-width="2200"
      :max-height="1600"
      @resize-start="emit('resize-start')"
      @resize-end="({ params }) => emit('resize-end', params)"
    />

    <!-- Draggable Top Chrome Header -->
    <header class="spatial-board-header space-drag-handle">
      <div class="spatial-board-title-group">
        <Columns3 :size="14" class="spatial-board-icon" aria-hidden="true" />
        <select
          v-if="projects && projects.length > 1 && !readonly"
          class="spatial-board-project-select nodrag nopan"
          :value="effectiveProjectId || ''"
          :aria-label="t('board.drawer.projectLabel')"
          data-testid="spatial-board-project-select"
          @pointerdown.stop
          @click.stop
          @change="onProjectSelectChange"
        >
          <option v-for="proj in projects" :key="proj.id" :value="proj.id">
            {{ proj.name }}
          </option>
        </select>
        <strong v-else class="spatial-board-title" data-testid="spatial-board-title">
          {{ headerTitle }}
        </strong>
        <span class="spatial-board-count" data-testid="spatial-board-count">
          {{ boardView.filteredTasks.value.length }}
        </span>
      </div>

      <div
        class="spatial-board-controls nodrag nopan"
        @pointerdown.stop
        @click.stop
      >
        <!-- Layout Switcher: Kanban / List / Swimlane -->
        <div class="spatial-board-layout-group" role="group" :aria-label="t('board.layoutLabel')">
          <button
            type="button"
            class="spatial-board-layout-btn ui-pressable"
            :class="{ 'is-active': boardView.layout.value === 'kanban' }"
            :title="t('board.layout.kanban')"
            :aria-label="t('board.layout.kanban')"
            data-testid="spatial-board-layout-kanban"
            @click="setLayout('kanban')"
          >
            <Columns3 :size="12" aria-hidden="true" />
            <span>{{ t('board.layout.kanban') }}</span>
          </button>
          <button
            type="button"
            class="spatial-board-layout-btn ui-pressable"
            :class="{ 'is-active': boardView.layout.value === 'list' }"
            :title="t('board.layout.list')"
            :aria-label="t('board.layout.list')"
            data-testid="spatial-board-layout-list"
            @click="setLayout('list')"
          >
            <List :size="12" aria-hidden="true" />
            <span>{{ t('board.layout.list') }}</span>
          </button>
          <button
            type="button"
            class="spatial-board-layout-btn ui-pressable"
            :class="{ 'is-active': boardView.layout.value === 'swimlane' }"
            :title="t('board.layout.swimlane')"
            :aria-label="t('board.layout.swimlane')"
            data-testid="spatial-board-layout-swimlane"
            @click="setLayout('swimlane')"
          >
            <Rows3 :size="12" aria-hidden="true" />
            <span>{{ t('board.layout.swimlane') }}</span>
          </button>
        </div>

        <button
          type="button"
          class="spatial-board-icon-btn ui-pressable"
          :title="t('common.refresh')"
          :aria-label="t('common.refresh')"
          data-testid="spatial-board-refresh"
          @click="boardView.refresh()"
        >
          <RefreshCw :size="12" :class="{ 'animate-spin': boardView.loading.value }" />
        </button>

        <button
          v-if="!readonly"
          type="button"
          class="spatial-board-new-btn ui-pressable"
          :disabled="!effectiveProjectId"
          data-testid="spatial-board-new-task"
          @click="onNewTask('ready')"
        >
          <Plus :size="12" aria-hidden="true" />
          <span>{{ t('board.newTask') }}</span>
        </button>
      </div>
    </header>

    <!-- Quick Filter Chips Strip -->
    <div
      v-if="effectiveProjectId"
      class="spatial-board-filters nodrag nopan"
      @pointerdown.stop
      @click.stop
    >
      <button
        type="button"
        class="spatial-board-chip ui-pressable"
        :class="{ 'is-active': isAllFilterActive }"
        data-testid="spatial-board-filter-all"
        @click="clearQuickFilters"
      >
        {{ t('space.boardBridge.filterAll') }}
      </button>
      <button
        type="button"
        class="spatial-board-chip ui-pressable"
        :class="{ 'is-active': isNeedsActionActive }"
        data-testid="spatial-board-filter-needs-action"
        @click="toggleNeedsAction"
      >
        {{ t('board.needsMyAction') }}
      </button>
      <button
        type="button"
        class="spatial-board-chip ui-pressable"
        :class="{ 'is-active': hasStatusFilter('in_progress') }"
        data-testid="spatial-board-filter-status-in_progress"
        @click="toggleStatusFilter('in_progress')"
      >
        {{ t('board.status.in_progress') }}
      </button>
      <button
        type="button"
        class="spatial-board-chip ui-pressable"
        :class="{ 'is-active': hasStatusFilter('in_review') }"
        data-testid="spatial-board-filter-status-in_review"
        @click="toggleStatusFilter('in_review')"
      >
        {{ t('board.status.in_review') }}
      </button>
      <button
        v-for="lbl in boardView.availableLabels.value.slice(0, 4)"
        :key="lbl"
        type="button"
        class="spatial-board-chip ui-pressable"
        :class="{ 'is-active': hasLabelFilter(lbl) }"
        :data-testid="`spatial-board-filter-label-${lbl}`"
        @click="toggleLabelFilter(lbl)"
      >
        #{{ lbl }}
      </button>
    </div>

    <!-- Interactive Scrollable Board Body -->
    <div
      class="spatial-board-body nodrag nopan nowheel"
      data-testid="spatial-board-body"
      @wheel.stop
      @pointerdown.stop
      @dragstart="onBodyDragStart"
      @dragend="onBodyDragEnd"
    >
      <div v-if="!effectiveProjectId" class="spatial-board-empty" data-testid="spatial-board-no-project">
        <p>{{ t('space.boardBridge.noProjectBound') }}</p>
      </div>
      <BoardKanbanView
        v-else-if="boardView.layout.value === 'kanban'"
        :groups="boardView.groups.value"
        :visible-fields="boardView.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="emit('select-task', $event)"
        @jump-spatial="emit('jump-spatial', $event)"
        @new-task-in-group="onNewTaskInGroup"
        @move-task="onMoveTask"
      />
      <BoardListView
        v-else-if="boardView.layout.value === 'list'"
        :groups="boardView.groups.value"
        :visible-fields="boardView.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="emit('select-task', $event)"
        @jump-spatial="emit('jump-spatial', $event)"
        @quick-update="onQuickUpdate"
        @move-task="onMoveTask"
      />
      <BoardSwimlaneView
        v-else
        :swimlanes="boardView.swimlanes.value"
        :visible-fields="boardView.visibleFields.value"
        :selected-task-id="selectedTaskId"
        @select-task="emit('select-task', $event)"
        @jump-spatial="emit('jump-spatial', $event)"
        @move-task="onMoveTask"
      />
    </div>
  </section>
</template>

<style scoped>
.spatial-board-node {
  position: relative;
  width: 100%;
  height: 100%;
  min-width: 480px;
  min-height: 300px;
  display: flex;
  flex-direction: column;
  border-radius: 14px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: color-mix(in srgb, var(--surface-raised, var(--bg-secondary)) 94%, transparent);
  backdrop-filter: blur(16px);
  color: var(--text-primary);
  box-shadow:
    0 12px 32px rgba(15, 23, 42, 0.12),
    0 2px 6px rgba(15, 23, 42, 0.06);
  overflow: hidden;
  transition:
    border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    box-shadow var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.spatial-board-node.is-selected {
  border-color: var(--accent-primary);
  box-shadow:
    0 0 0 2px color-mix(in srgb, var(--accent-primary) 24%, transparent),
    0 14px 36px rgba(15, 23, 42, 0.16);
}

.spatial-board-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 12px;
  background: color-mix(in srgb, var(--surface-section, var(--bg-tertiary)) 72%, transparent);
  border-bottom: 1px solid color-mix(in srgb, var(--border-muted) 65%, transparent);
  cursor: grab;
  user-select: none;
  flex-shrink: 0;
}

.spatial-board-header:active {
  cursor: grabbing;
}

.spatial-board-title-group {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
}

.spatial-board-icon {
  color: var(--text-secondary);
  flex-shrink: 0;
}

.spatial-board-title {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.spatial-board-project-select {
  height: 24px;
  padding: 0 6px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
}

.spatial-board-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: 999px;
  background: var(--surface-raised, var(--bg-secondary));
  border: 1px solid color-mix(in srgb, var(--border-muted) 70%, transparent);
  color: var(--text-secondary);
  font-size: 10.5px;
  font-weight: 600;
}

.spatial-board-controls {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.spatial-board-layout-group {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border-radius: 8px;
  background: var(--surface-raised, var(--bg-secondary));
  border: 1px solid color-mix(in srgb, var(--border-muted) 70%, transparent);
}

.spatial-board-layout-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 7px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.spatial-board-layout-btn:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.spatial-board-layout-btn.is-active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
}

.spatial-board-icon-btn {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 6px;
  border: 1px solid color-mix(in srgb, var(--border-muted) 70%, transparent);
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-secondary);
  cursor: pointer;
}

.spatial-board-icon-btn:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.spatial-board-new-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 9px;
  border-radius: 7px;
  border: 0;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  transition: opacity var(--duration-tap, 120ms) ease;
}

.spatial-board-new-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.spatial-board-filters {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 6px 12px;
  overflow-x: auto;
  border-bottom: 1px solid color-mix(in srgb, var(--border-muted) 48%, transparent);
  flex-shrink: 0;
}

.spatial-board-chip {
  display: inline-flex;
  align-items: center;
  height: 21px;
  padding: 0 8px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--border-muted) 75%, transparent);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.spatial-board-chip:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.spatial-board-chip.is-active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.spatial-board-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: auto;
  cursor: default;
}

.spatial-board-empty {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  color: var(--text-muted);
  font-size: 12.5px;
  text-align: center;
}

.spatial-board-handle {
  opacity: 0;
  width: 8px;
  height: 8px;
}
</style>
