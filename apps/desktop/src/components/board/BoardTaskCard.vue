<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  Bot,
  CheckSquare,
  Folder,
  GitBranch,
  Link2,
  Lock,
  Sparkles,
  Terminal,
  User,
  Waypoints,
} from '@lucide/vue'
import type { ProjectBoardTaskDto, ProjectBoardVisibleField } from '@/api'

const props = defineProps<{
  task: ProjectBoardTaskDto
  visibleFields: readonly ProjectBoardVisibleField[]
  selected?: boolean
  compact?: boolean
}>()

const emit = defineEmits<{
  select: [task: ProjectBoardTaskDto]
  'jump-spatial': [task: ProjectBoardTaskDto]
  dragstart: [event: DragEvent, task: ProjectBoardTaskDto]
  dragend: [event: DragEvent, task: ProjectBoardTaskDto]
}>()

const { t } = useI18n()

function hasField(field: ProjectBoardVisibleField): boolean {
  return props.visibleFields.includes(field)
}

const assigneeKind = computed(() => props.task.assignee?.kind ?? 'human')

const assigneeLabel = computed(() => {
  const custom = props.task.assignee?.display_name?.trim()
  if (custom) return custom
  if (props.task.assignee?.harness_id) return props.task.assignee.harness_id
  return t(`board.assignee.${assigneeKind.value}`)
})

const checklistDoneCount = computed(() => props.task.checklist.filter((item) => item.done).length)

const isRunning = computed(() => {
  if (!props.task.active_run_id) return false
  const rs = (props.task.run_status ?? '').toLowerCase()
  return rs !== 'completed' && rs !== 'failed' && rs !== 'cancelled'
})

function onDragStart(event: DragEvent) {
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'move'
    event.dataTransfer.setData('text/x-tinadec-board-task', props.task.task_id)
  }
  emit('dragstart', event, props.task)
}
</script>

<template>
  <article
    class="board-task-card ui-pressable"
    :class="{
      'board-task-card--selected': selected,
      'board-task-card--compact': compact,
      'board-task-card--blocked': task.blocked,
    }"
    draggable="true"
    role="button"
    tabindex="0"
    :data-testid="`board-task-card-${task.task_id}`"
    :data-task-key="task.task_key"
    @click="emit('select', task)"
    @keydown.enter.prevent="emit('select', task)"
    @keydown.space.prevent="emit('select', task)"
    @dragstart="onDragStart"
    @dragend="emit('dragend', $event, task)"
  >
    <!-- Header row: priority dot + task_key + optional project badge + conflict/blocked pills -->
    <div class="board-card-top">
      <div class="board-card-id-group">
        <span
          class="board-priority-dot"
          :class="`board-priority-dot--${task.priority}`"
          :title="`${t('board.priorityLabel')}: ${t(`board.priority.${task.priority}`)}`"
          :data-testid="`board-priority-dot-${task.priority}`"
        />
        <span class="board-task-key">{{ task.task_key }}</span>
        <span
          v-if="hasField('project_badge') && task.project_name"
          class="board-meta-chip"
          :title="task.project_name"
          data-testid="board-card-project-badge"
        >
          <Folder :size="10" />
          <span>{{ task.project_name }}</span>
        </span>
      </div>

      <div class="board-card-flags">
        <span
          v-if="hasField('conflict_badge') && task.has_sync_conflict"
          class="board-flag-pill board-flag-pill--conflict"
          :title="t('board.conflictDetected')"
          data-testid="board-card-conflict-badge"
        >
          <AlertTriangle :size="11" />
          <span>{{ t('board.conflictShort') }}</span>
        </span>
        <span
          v-if="task.blocked"
          class="board-flag-pill"
          :title="task.blocked_reason || t('board.blocked')"
          data-testid="board-card-blocked-badge"
        >
          <Lock :size="10" />
          <span>{{ t('board.blocked') }}</span>
        </span>
      </div>
    </div>

    <!-- Title -->
    <h4 class="board-card-title">{{ task.title }}</h4>

    <!-- 1-line Live Digest progress -->
    <div
      v-if="hasField('live_digest') && (task.live_digest_summary || isRunning)"
      class="board-card-digest"
      data-testid="board-card-live-digest"
    >
      <span class="board-digest-dot" :class="{ 'board-digest-dot--live': isRunning }" />
      <span class="board-digest-text" :class="{ 'ui-shimmer-text': isRunning }">
        {{ task.live_digest_summary || t('board.runningLive') }}
      </span>
    </div>

    <!-- Labels -->
    <div v-if="hasField('labels') && task.labels.length && !compact" class="board-card-labels">
      <span v-for="lbl in task.labels.slice(0, 4)" :key="lbl" class="board-label-tag">
        #{{ lbl }}
      </span>
    </div>

    <!-- Footer row: Assignee + Checklist + Dependencies + Diff Stats + Spatial Anchor -->
    <div class="board-card-footer">
      <span
        class="board-assignee-badge"
        :data-assignee-kind="assigneeKind"
        data-testid="board-card-assignee"
      >
        <User v-if="assigneeKind === 'human'" :size="11" />
        <Terminal v-else-if="assigneeKind === 'cli'" :size="11" />
        <Sparkles v-else-if="assigneeKind === 'coordinator'" :size="11" />
        <Bot v-else :size="11" />
        <span>{{ assigneeLabel }}</span>
      </span>

      <div class="board-card-metrics">
        <span
          v-if="hasField('checklist') && task.checklist.length > 0"
          class="board-metric-chip"
          data-testid="board-card-checklist"
        >
          <CheckSquare :size="10" />
          <span>{{ checklistDoneCount }}/{{ task.checklist.length }}</span>
        </span>

        <span
          v-if="hasField('dependencies') && task.depends_on.length > 0"
          class="board-metric-chip"
          :title="t('board.dependsOnCount', { count: task.depends_on.length })"
          data-testid="board-card-dependencies"
        >
          <Link2 :size="10" />
          <span>{{ task.depends_on.length }}</span>
        </span>

        <span
          v-if="task.worktree_path && !compact"
          class="board-metric-chip"
          :title="task.worktree_path"
        >
          <GitBranch :size="10" />
        </span>

        <span
          v-if="hasField('diff_stats') && task.diff_stats && (task.diff_stats.added > 0 || task.diff_stats.removed > 0)"
          class="board-diff-pill"
          data-testid="board-card-diff-pill"
        >
          <span class="board-diff-add">+{{ task.diff_stats.added }}</span>
          <span class="board-diff-del">-{{ task.diff_stats.removed }}</span>
        </span>

        <button
          v-if="hasField('spatial_ref') && task.spatial_ref"
          type="button"
          class="board-spatial-chip ui-pressable"
          :title="t('board.jumpToSpatial')"
          data-testid="board-card-spatial-chip"
          @click.stop="emit('jump-spatial', task)"
        >
          <Waypoints :size="10" />
          <span>{{ t('board.spatialAnchorShort') }}</span>
        </button>
      </div>
    </div>
  </article>
</template>

<style scoped>
.board-task-card {
  display: flex;
  flex-direction: column;
  gap: 7px;
  padding: 10px 11px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-primary);
  box-shadow: var(--shadow-card-subtle);
  cursor: pointer;
  user-select: none;
}

.board-task-card:hover {
  border-color: var(--border-card-active, var(--border-default));
  box-shadow: var(--shadow-card-hover, var(--shadow-card-subtle));
}

.board-task-card--selected {
  border-color: var(--accent-primary);
  background: var(--surface-selected, var(--bg-selected));
}

.board-task-card--compact {
  padding: 8px 9px;
  gap: 5px;
}

.board-card-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.board-card-id-group {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.board-priority-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--text-muted);
  opacity: 0.45;
}

.board-priority-dot--urgent {
  background: var(--text-primary);
  opacity: 1;
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--text-primary) 22%, transparent);
}

.board-priority-dot--high {
  background: var(--text-primary);
  opacity: 0.88;
}

.board-priority-dot--medium {
  background: var(--text-secondary);
  opacity: 0.75;
}

.board-priority-dot--low {
  background: var(--text-muted);
  opacity: 0.5;
}

.board-priority-dot--none {
  background: transparent;
  border: 1px solid var(--text-muted);
  opacity: 0.45;
}

.board-task-key {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.01em;
}

.board-meta-chip {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  max-width: 110px;
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--surface-section, var(--bg-tertiary));
  color: var(--text-secondary);
  font-size: 10.5px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.board-card-flags {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.board-flag-pill {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10.5px;
  font-weight: 500;
  background: var(--surface-section, var(--bg-tertiary));
  color: var(--text-primary);
  border: 1px solid var(--border-default, var(--border-muted));
}

.board-flag-pill--conflict {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.board-card-title {
  margin: 0;
  font-size: 12.5px;
  font-weight: 500;
  line-height: 1.4;
  color: var(--text-primary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.board-card-digest {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  padding: 3px 6px;
  border-radius: 6px;
  background: var(--surface-section, var(--bg-tertiary));
}

.board-digest-dot {
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: var(--text-muted);
  flex-shrink: 0;
}

.board-digest-dot--live {
  background: var(--text-primary);
}

.board-digest-text {
  font-size: 11px;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.board-card-labels {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.board-label-tag {
  padding: 1px 5px;
  border-radius: 4px;
  font-size: 10.5px;
  color: var(--text-muted);
  background: var(--surface-section, var(--bg-tertiary));
}

.board-card-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  margin-top: 2px;
}

.board-assignee-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 128px;
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 11px;
  color: var(--text-secondary);
  background: var(--surface-section, var(--bg-tertiary));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.board-card-metrics {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-shrink: 0;
}

.board-metric-chip {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 10.5px;
  color: var(--text-muted);
}

.board-diff-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 5px;
  border-radius: 4px;
  font-family: var(--font-mono, monospace);
  font-size: 10.5px;
  background: var(--surface-section, var(--bg-tertiary));
}

.board-diff-add {
  color: var(--text-approve, var(--accent-success));
}

.board-diff-del {
  color: var(--text-reject, var(--accent-danger));
}

.board-spatial-chip {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px 6px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 10.5px;
  cursor: pointer;
}

.board-spatial-chip:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}
</style>
