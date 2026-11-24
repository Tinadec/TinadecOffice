<script setup lang="ts">
import { computed, inject, ref, type ComputedRef } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ProjectBoardTaskDto } from '@/api'

const props = withDefaults(defineProps<{
  boardTaskId?: string | null
  boardTaskKey?: string | null
  linkedTask?: ProjectBoardTaskDto | null
}>(), {
  boardTaskId: null,
  boardTaskKey: null,
  linkedTask: null,
})

const emit = defineEmits<{
  'open-task': [
    payload: {
      taskId: string | null
      taskKey: string | null
      task: ProjectBoardTaskDto | null
    },
  ]
}>()

const { t } = useI18n()
const hovered = ref(false)

const injectedTasksById = inject<ComputedRef<ReadonlyMap<string, ProjectBoardTaskDto>> | null>(
  'space:board-tasks-by-id',
  null,
)
const injectedTasksByKey = inject<ComputedRef<ReadonlyMap<string, ProjectBoardTaskDto>> | null>(
  'space:board-tasks-by-key',
  null,
)

const cleanTaskId = computed<string | null>(() => {
  const raw = props.boardTaskId
  return typeof raw === 'string' && raw.trim().length > 0 ? raw.trim() : null
})

const cleanTaskKey = computed<string | null>(() => {
  const raw = props.boardTaskKey
  return typeof raw === 'string' && raw.trim().length > 0 ? raw.trim() : null
})

const resolvedTask = computed<ProjectBoardTaskDto | null>(() => {
  if (props.linkedTask) return props.linkedTask
  if (cleanTaskId.value && injectedTasksById?.value.has(cleanTaskId.value)) {
    return injectedTasksById.value.get(cleanTaskId.value) ?? null
  }
  if (cleanTaskKey.value && injectedTasksByKey?.value.has(cleanTaskKey.value)) {
    return injectedTasksByKey.value.get(cleanTaskKey.value) ?? null
  }
  return null
})

const visible = computed(() => Boolean(cleanTaskId.value || cleanTaskKey.value || resolvedTask.value))

const displayKey = computed(() => {
  if (resolvedTask.value?.task_key) return resolvedTask.value.task_key
  if (cleanTaskKey.value) return cleanTaskKey.value
  if (cleanTaskId.value) return cleanTaskId.value.slice(0, 8)
  return ''
})

const statusLabel = computed(() => {
  const task = resolvedTask.value
  if (!task) return ''
  return t(`board.status.${task.status}`)
})

const assigneeLabel = computed(() => {
  const assignee = resolvedTask.value?.assignee
  if (!assignee) return ''
  if (assignee.display_name?.trim()) return assignee.display_name.trim()
  if (assignee.harness_id?.trim()) return assignee.harness_id.trim()
  return t(`board.assignee.${assignee.kind}`)
})

const capsuleText = computed(() => {
  return [displayKey.value, statusLabel.value, assigneeLabel.value].filter(Boolean).join(' · ')
})

const isRunning = computed(() => {
  const task = resolvedTask.value
  if (!task) return false
  if (task.status === 'in_progress') return true
  if (!task.active_run_id) return false
  const rs = (task.run_status ?? '').toLowerCase()
  return rs !== 'completed' && rs !== 'failed' && rs !== 'cancelled'
})

const tooltipAttr = computed(() => {
  const task = resolvedTask.value
  if (!task) return t('space.boardBridge.clickToOpenTask')
  const parts = [task.title]
  if (task.live_digest_summary?.trim()) {
    parts.push(task.live_digest_summary.trim())
  }
  return parts.filter(Boolean).join(' — ')
})

function onOpenClick() {
  emit('open-task', {
    taskId: resolvedTask.value?.task_id ?? cleanTaskId.value,
    taskKey: resolvedTask.value?.task_key ?? cleanTaskKey.value,
    task: resolvedTask.value,
  })
}
</script>

<template>
  <div
    v-if="visible"
    class="spatial-task-capsule-wrap nodrag nopan"
    @mouseenter="hovered = true"
    @mouseleave="hovered = false"
    @pointerdown.stop
    @dblclick.stop
  >
    <button
      type="button"
      class="spatial-task-status-capsule ui-pressable"
      :class="{
        'is-running': isRunning,
        'is-done': resolvedTask?.status === 'done',
      }"
      :title="tooltipAttr"
      data-testid="spatial-task-status-capsule"
      @click.stop="onOpenClick"
    >
      <span class="spatial-task-capsule-dot" :class="{ 'is-live': isRunning }" aria-hidden="true" />
      <span class="spatial-task-capsule-text">{{ capsuleText }}</span>
    </button>

    <div
      v-if="hovered && resolvedTask"
      class="spatial-task-capsule-tooltip"
      role="tooltip"
      data-testid="spatial-task-capsule-tooltip"
    >
      <div class="spatial-task-tooltip-title">{{ resolvedTask.title }}</div>
      <div v-if="resolvedTask.live_digest_summary" class="spatial-task-tooltip-digest">
        {{ resolvedTask.live_digest_summary }}
      </div>
      <div v-else class="spatial-task-tooltip-hint">
        {{ t('space.boardBridge.clickToOpenTask') }}
      </div>
    </div>
  </div>
</template>

<style scoped>
.spatial-task-capsule-wrap {
  position: relative;
  display: inline-flex;
  align-items: center;
  max-width: 100%;
  z-index: 3;
}

.spatial-task-status-capsule {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: 240px;
  height: 20px;
  padding: 0 7px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--border-default, var(--border-muted)) 78%, transparent);
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 92%, transparent);
  backdrop-filter: blur(8px);
  color: var(--text-primary);
  font-size: 10.5px;
  font-weight: 600;
  line-height: 1;
  cursor: pointer;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.spatial-task-status-capsule:hover {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.spatial-task-status-capsule.is-done {
  opacity: 0.72;
}

.spatial-task-capsule-dot {
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: currentColor;
  opacity: 0.65;
  flex-shrink: 0;
}

.spatial-task-capsule-dot.is-live {
  opacity: 1;
  box-shadow: 0 0 0 2px color-mix(in srgb, currentColor 24%, transparent);
}

.spatial-task-capsule-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.spatial-task-capsule-tooltip {
  position: absolute;
  bottom: calc(100% + 6px);
  left: 0;
  min-width: 180px;
  max-width: 260px;
  padding: 7px 9px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-raised, #18181b) 96%, transparent);
  backdrop-filter: blur(12px);
  color: var(--text-primary);
  box-shadow: 0 8px 24px rgba(15, 23, 42, 0.18);
  pointer-events: none;
  z-index: 20;
  white-space: normal;
  text-align: left;
}

.spatial-task-tooltip-title {
  font-size: 11.5px;
  font-weight: 600;
  line-height: 1.35;
  color: var(--text-primary);
}

.spatial-task-tooltip-digest {
  margin-top: 3px;
  font-size: 10.5px;
  line-height: 1.35;
  color: var(--text-secondary);
}

.spatial-task-tooltip-hint {
  margin-top: 3px;
  font-size: 10px;
  color: var(--text-muted);
}
</style>
