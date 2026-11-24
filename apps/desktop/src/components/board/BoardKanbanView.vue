<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Plus } from '@lucide/vue'
import type {
  ProjectBoardAssigneeKind,
  ProjectBoardTaskDto,
  ProjectBoardTaskPriority,
  ProjectBoardTaskStatus,
  ProjectBoardVisibleField,
  UpdateProjectBoardTaskInput,
} from '@/api'
import BoardTaskCard from './BoardTaskCard.vue'
import type { BoardTaskGroup } from './useProjectBoardView'

const props = defineProps<{
  groups: readonly BoardTaskGroup[]
  visibleFields: readonly ProjectBoardVisibleField[]
  selectedTaskId?: string | null
}>()

const emit = defineEmits<{
  'select-task': [task: ProjectBoardTaskDto]
  'jump-spatial': [task: ProjectBoardTaskDto]
  'new-task-in-group': [group: BoardTaskGroup]
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

const draggingTask = ref<ProjectBoardTaskDto | null>(null)
const dragOverGroupId = ref<string | null>(null)
const dragOverIndex = ref<number>(0)

function groupTitle(group: BoardTaskGroup): string {
  if (group.labelKey && te(group.labelKey)) return t(group.labelKey)
  return group.labelFallback
}

function buildGroupPatch(group: BoardTaskGroup, task: ProjectBoardTaskDto): UpdateProjectBoardTaskInput {
  if (group.kind === 'status' && group.key !== task.status) {
    return { status: group.key as ProjectBoardTaskStatus }
  }
  if (group.kind === 'priority' && group.key !== task.priority) {
    return { priority: group.key as ProjectBoardTaskPriority }
  }
  if (group.kind === 'assignee' && group.key !== (task.assignee?.kind ?? 'human')) {
    return {
      assignee: {
        kind: group.key as ProjectBoardAssigneeKind,
      },
    }
  }
  return {}
}

function findTaskById(taskId: string): ProjectBoardTaskDto | null {
  for (const group of props.groups) {
    const found = group.tasks.find((item) => item.task_id === taskId)
    if (found) return found
  }
  return null
}

function onCardDragStart(_event: DragEvent, task: ProjectBoardTaskDto) {
  draggingTask.value = task
}

function onCardDragEnd() {
  draggingTask.value = null
  dragOverGroupId.value = null
}

function onColumnDragOver(event: DragEvent, group: BoardTaskGroup) {
  event.preventDefault()
  dragOverGroupId.value = group.id
  dragOverIndex.value = group.tasks.length
}

function onCardSlotDragOver(event: DragEvent, group: BoardTaskGroup, index: number) {
  event.preventDefault()
  event.stopPropagation()
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  const after = event.clientY > rect.top + rect.height / 2
  dragOverGroupId.value = group.id
  dragOverIndex.value = after ? index + 1 : index
}

function onColumnDrop(event: DragEvent, group: BoardTaskGroup) {
  event.preventDefault()
  const transferredId = event.dataTransfer?.getData('text/x-tinadec-board-task')
  const task = draggingTask.value ?? (transferredId ? findTaskById(transferredId) : null)
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
  <div class="board-kanban" data-testid="board-kanban-view">
    <section
      v-for="group in groups"
      :key="group.id"
      class="board-kanban-col"
      :class="{ 'board-kanban-col--dragover': dragOverGroupId === group.id }"
      :data-testid="`board-kanban-col-${group.key}`"
      @dragover="onColumnDragOver($event, group)"
      @drop="onColumnDrop($event, group)"
    >
      <header class="board-kanban-col-header">
        <div class="board-kanban-col-title">
          <span>{{ groupTitle(group) }}</span>
          <span class="board-kanban-count">{{ group.tasks.length }}</span>
        </div>
        <button
          type="button"
          class="board-col-add-btn ui-pressable"
          :title="t('board.newTask')"
          :aria-label="t('board.newTask')"
          :data-testid="`board-col-add-${group.key}`"
          @click="emit('new-task-in-group', group)"
        >
          <Plus :size="13" />
        </button>
      </header>

      <div class="board-kanban-cards">
        <div
          v-for="(task, index) in group.tasks"
          :key="task.task_id"
          class="board-kanban-slot"
          :class="{
            'board-kanban-slot--drop-before': dragOverGroupId === group.id && dragOverIndex === index,
            'board-kanban-slot--drop-after':
              dragOverGroupId === group.id &&
              index === group.tasks.length - 1 &&
              dragOverIndex >= group.tasks.length,
          }"
          @dragover="onCardSlotDragOver($event, group, index)"
        >
          <BoardTaskCard
            :task="task"
            :visible-fields="visibleFields"
            :selected="selectedTaskId === task.task_id"
            @select="emit('select-task', $event)"
            @jump-spatial="emit('jump-spatial', $event)"
            @dragstart="onCardDragStart"
            @dragend="onCardDragEnd"
          />
        </div>

        <div v-if="group.tasks.length === 0" class="board-kanban-empty">
          <span>{{ t('board.emptyColumn') }}</span>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.board-kanban {
  flex: 1;
  min-height: 0;
  display: flex;
  align-items: stretch;
  gap: 10px;
  padding: 6px 16px 16px;
  overflow-x: auto;
  overflow-y: hidden;
}

.board-kanban-col {
  flex: 0 0 284px;
  min-width: 284px;
  max-height: 100%;
  display: flex;
  flex-direction: column;
  border-radius: var(--radius-island, 12px);
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 72%, transparent);
  border: 1px solid var(--border-card, var(--border-muted));
  padding: 8px;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.board-kanban-col--dragover {
  border-color: var(--border-card-active, var(--accent-primary));
  background: color-mix(in srgb, var(--surface-hover, var(--bg-tertiary)) 60%, transparent);
}

.board-kanban-col-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 4px 6px 8px;
}

.board-kanban-col-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.board-kanban-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 500;
  color: var(--text-muted);
  background: var(--surface-raised, var(--bg-tertiary));
}

.board-col-add-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.board-col-add-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.board-kanban-cards {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 7px;
  padding: 2px;
}

.board-kanban-slot {
  position: relative;
}

.board-kanban-slot--drop-before::before,
.board-kanban-slot--drop-after::after {
  content: '';
  position: absolute;
  left: 4px;
  right: 4px;
  height: 2px;
  border-radius: 1px;
  background: var(--accent-primary);
  pointer-events: none;
}

.board-kanban-slot--drop-before::before {
  top: -4px;
}

.board-kanban-slot--drop-after::after {
  bottom: -4px;
}

.board-kanban-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 76px;
  border-radius: 8px;
  border: 1px dashed var(--border-muted);
  font-size: 11.5px;
  color: var(--text-muted);
}
</style>
