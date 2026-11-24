<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  Bot,
  ChevronRight,
  GripVertical,
  Link2,
  Lock,
  Sparkles,
  Terminal,
  User,
  Waypoints,
} from '@lucide/vue'
import type {
  ProjectBoardAssigneeKind,
  ProjectBoardTaskDto,
  ProjectBoardTaskPriority,
  ProjectBoardTaskStatus,
  ProjectBoardVisibleField,
  UpdateProjectBoardTaskInput,
} from '@/api'
import { BOARD_TASK_STATUSES, type BoardTaskGroup } from './useProjectBoardView'

const props = defineProps<{
  groups: readonly BoardTaskGroup[]
  visibleFields: readonly ProjectBoardVisibleField[]
  selectedTaskId?: string | null
}>()

const emit = defineEmits<{
  'select-task': [task: ProjectBoardTaskDto]
  'jump-spatial': [task: ProjectBoardTaskDto]
  'quick-update': [task: ProjectBoardTaskDto, patch: UpdateProjectBoardTaskInput]
  'move-task': [
    payload: {
      task: ProjectBoardTaskDto
      peers: ProjectBoardTaskDto[]
      targetIndex: number
      patch: UpdateProjectBoardTaskInput
    },
  ]
}>()

const { t, te } = useI18n()

const collapsedGroupIds = ref<Set<string>>(new Set())
const draggingTask = ref<ProjectBoardTaskDto | null>(null)
const dragOverGroupId = ref<string | null>(null)
const dragOverIndex = ref<number>(0)

function hasField(field: ProjectBoardVisibleField): boolean {
  return props.visibleFields.includes(field)
}

function groupTitle(group: BoardTaskGroup): string {
  if (group.labelKey && te(group.labelKey)) return t(group.labelKey)
  return group.labelFallback
}

function toggleGroup(id: string) {
  const next = new Set(collapsedGroupIds.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  collapsedGroupIds.value = next
}

function cycleStatus(task: ProjectBoardTaskDto) {
  const idx = BOARD_TASK_STATUSES.indexOf(task.status)
  const nextStatus = BOARD_TASK_STATUSES[(idx + 1) % BOARD_TASK_STATUSES.length]!
  emit('quick-update', task, { status: nextStatus, expected_revision: task.revision })
}

function buildGroupPatch(group: BoardTaskGroup, task: ProjectBoardTaskDto): UpdateProjectBoardTaskInput {
  if (group.kind === 'status' && group.key !== task.status) {
    return { status: group.key as ProjectBoardTaskStatus }
  }
  if (group.kind === 'priority' && group.key !== task.priority) {
    return { priority: group.key as ProjectBoardTaskPriority }
  }
  if (group.kind === 'assignee' && group.key !== (task.assignee?.kind ?? 'human')) {
    return { assignee: { kind: group.key as ProjectBoardAssigneeKind } }
  }
  return {}
}

function onRowDragStart(event: DragEvent, task: ProjectBoardTaskDto) {
  draggingTask.value = task
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'copyMove'
    event.dataTransfer.setData('text/x-tinadec-board-task', task.task_id)
    event.dataTransfer.setData(
      'application/x-tinadec-board-task-json',
      JSON.stringify({
        task_id: task.task_id,
        task_key: task.task_key,
        project_id: task.project_id,
        title: task.title,
      }),
    )
  }
}

function onRowDragOver(event: DragEvent, group: BoardTaskGroup, index: number) {
  event.preventDefault()
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  dragOverGroupId.value = group.id
  dragOverIndex.value = event.clientY > rect.top + rect.height / 2 ? index + 1 : index
}

function onGroupDrop(event: DragEvent, group: BoardTaskGroup) {
  event.preventDefault()
  const task = draggingTask.value
  const targetIndex = dragOverGroupId.value === group.id ? dragOverIndex.value : group.tasks.length
  draggingTask.value = null
  dragOverGroupId.value = null
  if (!task) return
  if (group.kind === 'project' && group.key !== task.project_id) return
  emit('move-task', {
    task,
    peers: group.tasks,
    targetIndex,
    patch: buildGroupPatch(group, task),
  })
}
</script>

<template>
  <div class="board-list-view" data-testid="board-list-view">
    <section
      v-for="group in groups"
      :key="group.id"
      class="board-list-section"
      :data-testid="`board-list-group-${group.key}`"
      @dragover.prevent
      @drop="onGroupDrop($event, group)"
    >
      <button
        type="button"
        class="board-list-group-header ui-pressable"
        :aria-expanded="!collapsedGroupIds.has(group.id)"
        @click="toggleGroup(group.id)"
      >
        <ChevronRight
          :size="13"
          class="board-list-chevron"
          :class="{ expanded: !collapsedGroupIds.has(group.id) }"
        />
        <span class="board-list-group-title">{{ groupTitle(group) }}</span>
        <span class="board-list-group-count">{{ group.tasks.length }}</span>
      </button>

      <div class="ui-collapse" :class="{ 'is-open': !collapsedGroupIds.has(group.id) }">
        <div class="ui-collapse-inner">
          <div v-if="group.tasks.length === 0" class="board-list-empty">
            {{ t('board.emptyColumn') }}
          </div>
          <div
            v-for="(task, index) in group.tasks"
            :key="task.task_id"
            class="board-list-row"
            :class="{
              active: selectedTaskId === task.task_id,
              'board-list-row--drop-before': dragOverGroupId === group.id && dragOverIndex === index,
            }"
            draggable="true"
            :data-testid="`board-list-row-${task.task_id}`"
            @dragstart="onRowDragStart($event, task)"
            @dragend="draggingTask = null; dragOverGroupId = null"
            @dragover="onRowDragOver($event, group, index)"
            @click="emit('select-task', task)"
          >
            <GripVertical :size="12" class="board-list-grip" />

            <button
              type="button"
              class="board-list-status-pill ui-pressable"
              :title="t('board.clickToCycleStatus')"
              :data-testid="`board-list-status-${task.task_id}`"
              @click.stop="cycleStatus(task)"
            >
              {{ t(`board.status.${task.status}`) }}
            </button>

            <span class="board-list-key">{{ task.task_key }}</span>

            <span
              v-if="hasField('project_badge') && task.project_name"
              class="board-list-project"
            >
              {{ task.project_name }}
            </span>

            <span class="board-list-title">{{ task.title }}</span>

            <span
              v-if="hasField('live_digest') && task.live_digest_summary"
              class="board-list-digest"
            >
              {{ task.live_digest_summary }}
            </span>

            <div class="board-list-meta">
              <span
                v-if="hasField('conflict_badge') && task.has_sync_conflict"
                class="board-list-conflict"
                data-testid="board-list-conflict-badge"
              >
                <AlertTriangle :size="11" />
                <span>{{ t('board.conflictShort') }}</span>
              </span>

              <span v-if="task.blocked" class="board-list-badge">
                <Lock :size="10" />
                <span>{{ t('board.blocked') }}</span>
              </span>

              <span
                v-if="hasField('dependencies') && task.depends_on.length > 0"
                class="board-list-badge"
              >
                <Link2 :size="10" />
                <span>{{ task.depends_on.length }}</span>
              </span>

              <span
                v-if="hasField('diff_stats') && task.diff_stats && (task.diff_stats.added > 0 || task.diff_stats.removed > 0)"
                class="board-list-diff"
              >
                <span class="board-diff-add">+{{ task.diff_stats.added }}</span>
                <span class="board-diff-del">-{{ task.diff_stats.removed }}</span>
              </span>

              <button
                v-if="hasField('spatial_ref') && task.spatial_ref"
                type="button"
                class="board-list-spatial ui-pressable"
                @click.stop="emit('jump-spatial', task)"
              >
                <Waypoints :size="11" />
              </button>

              <span class="board-list-priority">
                {{ t(`board.priority.${task.priority}`) }}
              </span>

              <span class="board-list-assignee">
                <User v-if="(task.assignee?.kind ?? 'human') === 'human'" :size="11" />
                <Terminal v-else-if="task.assignee?.kind === 'cli'" :size="11" />
                <Sparkles v-else-if="task.assignee?.kind === 'coordinator'" :size="11" />
                <Bot v-else :size="11" />
                <span>
                  {{
                    task.assignee?.display_name ||
                    task.assignee?.harness_id ||
                    t(`board.assignee.${task.assignee?.kind ?? 'human'}`)
                  }}
                </span>
              </span>
            </div>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.board-list-view {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 6px 16px 20px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.board-list-section {
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-card, var(--border-muted));
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 65%, transparent);
  padding: 4px;
}

.board-list-group-header {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  text-align: left;
}

.board-list-group-header:hover {
  background: var(--surface-hover);
}

.board-list-chevron {
  color: var(--text-muted);
  transition: transform var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.board-list-chevron.expanded {
  transform: rotate(90deg);
}

.board-list-group-count {
  font-size: 11px;
  font-weight: 500;
  color: var(--text-muted);
}

.board-list-empty {
  padding: 12px 14px;
  font-size: 11.5px;
  color: var(--text-muted);
}

.board-list-row {
  position: relative;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  border-radius: 7px;
  border: 1px solid transparent;
  cursor: pointer;
  transition: background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.board-list-row:hover {
  background: var(--surface-hover);
}

.board-list-row.active {
  background: var(--surface-selected);
  border-color: var(--border-card-active, var(--border-muted));
}

.board-list-row--drop-before::before {
  content: '';
  position: absolute;
  top: -1px;
  left: 8px;
  right: 8px;
  height: 2px;
  background: var(--accent-primary);
}

.board-list-grip {
  color: var(--text-muted);
  opacity: 0.45;
  flex-shrink: 0;
}

.board-list-status-pill {
  padding: 2px 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  cursor: pointer;
  flex-shrink: 0;
}

.board-list-status-pill:hover {
  color: var(--text-primary);
  border-color: var(--border-default);
}

.board-list-key {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
  flex-shrink: 0;
}

.board-list-project {
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-muted);
  font-size: 10.5px;
  flex-shrink: 0;
}

.board-list-title {
  flex: 1;
  min-width: 0;
  font-size: 12.5px;
  font-weight: 500;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.board-list-digest {
  max-width: 220px;
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.board-list-meta {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.board-list-conflict {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10.5px;
  font-weight: 600;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
}

.board-list-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 11px;
  color: var(--text-muted);
}

.board-list-diff {
  display: inline-flex;
  gap: 4px;
  font-family: var(--font-mono, monospace);
  font-size: 10.5px;
}

.board-diff-add {
  color: var(--text-approve, var(--accent-success));
}

.board-diff-del {
  color: var(--text-reject, var(--accent-danger));
}

.board-list-spatial {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.board-list-priority {
  font-size: 11px;
  color: var(--text-muted);
  min-width: 44px;
  text-align: right;
}

.board-list-assignee {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-width: 84px;
  font-size: 11px;
  color: var(--text-secondary);
}
</style>
