<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight } from '@lucide/vue'
import type {
  ProjectBoardTaskDto,
  ProjectBoardTaskStatus,
  ProjectBoardVisibleField,
  UpdateProjectBoardTaskInput,
} from '@/api'
import BoardTaskCard from './BoardTaskCard.vue'
import { BOARD_TASK_STATUSES, type BoardSwimlaneRow } from './useProjectBoardView'

const props = defineProps<{
  swimlanes: readonly BoardSwimlaneRow[]
  visibleFields: readonly ProjectBoardVisibleField[]
  selectedTaskId?: string | null
}>()

const emit = defineEmits<{
  'select-task': [task: ProjectBoardTaskDto]
  'jump-spatial': [task: ProjectBoardTaskDto]
  'move-task': [
    payload: {
      task: ProjectBoardTaskDto
      peers: ProjectBoardTaskDto[]
      targetIndex: number
      patch: UpdateProjectBoardTaskInput
    },
  ]
}>()

const { t } = useI18n()

const collapsedLaneIds = ref<Set<string>>(new Set())
const draggingTask = ref<ProjectBoardTaskDto | null>(null)
const dragOverCellKey = ref<string | null>(null)

function laneTitle(lane: BoardSwimlaneRow): string {
  if (lane.kind === 'assignee' && lane.assigneeKind) {
    return t(`board.assignee.${lane.assigneeKind}`)
  }
  return lane.title
}

function toggleLane(id: string) {
  const next = new Set(collapsedLaneIds.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  collapsedLaneIds.value = next
}

function cellKey(laneId: string, status: ProjectBoardTaskStatus): string {
  return `${laneId}::${status}`
}

function onCardDragStart(_event: DragEvent, task: ProjectBoardTaskDto) {
  draggingTask.value = task
}

function onCellDrop(event: DragEvent, lane: BoardSwimlaneRow, status: ProjectBoardTaskStatus) {
  event.preventDefault()
  const task = draggingTask.value
  draggingTask.value = null
  dragOverCellKey.value = null
  if (!task) return
  if (lane.kind === 'project' && lane.projectId && lane.projectId !== task.project_id) {
    return
  }
  const patch: UpdateProjectBoardTaskInput = {}
  if (task.status !== status) patch.status = status
  if (lane.kind === 'assignee' && lane.assigneeKind && (task.assignee?.kind ?? 'human') !== lane.assigneeKind) {
    patch.assignee = { kind: lane.assigneeKind }
  }
  const peers = lane.cells[status]
  emit('move-task', {
    task,
    peers,
    targetIndex: peers.length,
    patch,
  })
}
</script>

<template>
  <div class="board-swimlane-view" :aria-label="t('board.layout.swimlane')" data-testid="board-swimlane-view">
    <!-- Sticky Status Axis Header -->
    <div class="board-swimlane-axis">
      <div
        v-for="status in BOARD_TASK_STATUSES"
        :key="status"
        class="board-swimlane-axis-col"
      >
        <span>{{ t(`board.status.${status}`) }}</span>
      </div>
    </div>

    <section
      v-for="lane in swimlanes"
      :key="lane.id"
      class="board-swimlane-section"
      :data-testid="`board-swimlane-row-${lane.key}`"
    >
      <button
        type="button"
        class="board-swimlane-header ui-pressable"
        :aria-expanded="!collapsedLaneIds.has(lane.id)"
        @click="toggleLane(lane.id)"
      >
        <ChevronRight
          :size="13"
          class="board-swimlane-chevron"
          :class="{ expanded: !collapsedLaneIds.has(lane.id) }"
        />
        <span class="board-swimlane-title">{{ laneTitle(lane) }}</span>
        <span class="board-swimlane-count">{{ lane.totalCount }}</span>
      </button>

      <div class="ui-collapse" :class="{ 'is-open': !collapsedLaneIds.has(lane.id) }">
        <div class="ui-collapse-inner">
          <div class="board-swimlane-grid">
            <div
              v-for="status in BOARD_TASK_STATUSES"
              :key="status"
              class="board-swimlane-cell"
              :class="{ 'board-swimlane-cell--dragover': dragOverCellKey === cellKey(lane.id, status) }"
              :data-testid="`board-swimlane-cell-${lane.key}-${status}`"
              @dragover.prevent="dragOverCellKey = cellKey(lane.id, status)"
              @dragleave="dragOverCellKey === cellKey(lane.id, status) && (dragOverCellKey = null)"
              @drop="onCellDrop($event, lane, status)"
            >
              <BoardTaskCard
                v-for="task in lane.cells[status]"
                :key="task.task_id"
                :task="task"
                :visible-fields="visibleFields"
                :selected="selectedTaskId === task.task_id"
                compact
                @select="emit('select-task', $event)"
                @jump-spatial="emit('jump-spatial', $event)"
                @dragstart="onCardDragStart"
                @dragend="draggingTask = null; dragOverCellKey = null"
              />
            </div>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.board-swimlane-view {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 4px 16px 20px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.board-swimlane-axis {
  position: sticky;
  top: 0;
  z-index: 5;
  display: grid;
  grid-template-columns: repeat(5, minmax(220px, 1fr));
  gap: 8px;
  padding: 6px 8px;
  border-radius: 8px;
  background: var(--surface-raised, var(--bg-secondary));
  border: 1px solid var(--border-card, var(--border-muted));
}

.board-swimlane-axis-col {
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-secondary);
  padding: 0 4px;
}

.board-swimlane-section {
  border-radius: var(--radius-island, 12px);
  border: 1px solid var(--border-card, var(--border-muted));
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 65%, transparent);
  padding: 6px;
}

.board-swimlane-header {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12.5px;
  font-weight: 600;
  cursor: pointer;
  text-align: left;
}

.board-swimlane-header:hover {
  background: var(--surface-hover);
}

.board-swimlane-chevron {
  color: var(--text-muted);
  transition: transform var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.board-swimlane-chevron.expanded {
  transform: rotate(90deg);
}

.board-swimlane-count {
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 500;
  color: var(--text-muted);
  background: var(--surface-raised, var(--bg-tertiary));
}

.board-swimlane-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(220px, 1fr));
  gap: 8px;
  padding: 6px 2px 2px;
}

.board-swimlane-cell {
  min-height: 84px;
  padding: 6px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--bg-primary) 42%, transparent);
  border: 1px solid var(--border-muted);
  display: flex;
  flex-direction: column;
  gap: 6px;
  transition: border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.board-swimlane-cell--dragover {
  border-color: var(--accent-primary);
  background: var(--surface-hover);
}
</style>
