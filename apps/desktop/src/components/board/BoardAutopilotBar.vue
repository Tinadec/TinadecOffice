<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  Bot,
  ExternalLink,
  Minus,
  Pause,
  Play,
  Plus,
  Square,
} from '@lucide/vue'
import { UiButton, UiSwitch } from '@/components/ui'
import {
  api,
  type ProjectAutopilotStateDto,
  type ProjectBoardSettingsDto,
  type ProjectBoardTaskDto,
} from '@/api'
import { useHostAccess } from '@/lib/hostAccess'

const props = defineProps<{
  projectId: string
  autopilot?: ProjectAutopilotStateDto | null
  settings?: ProjectBoardSettingsDto | null
  tasks?: readonly ProjectBoardTaskDto[]
}>()

const emit = defineEmits<{
  updated: [state: ProjectAutopilotStateDto]
  'open-task': [task: ProjectBoardTaskDto]
  'unblocked-task': [task: ProjectBoardTaskDto]
}>()

const { t } = useI18n()
const { canAccessBackend } = useHostAccess()

const localAutopilot = ref<ProjectAutopilotStateDto | null>(props.autopilot ?? null)
const updating = ref(false)
const unblockingTaskId = ref<string | null>(null)
const actionError = ref<string | null>(null)

watch(
  () => props.autopilot,
  (incoming) => {
    if (incoming !== undefined) {
      localAutopilot.value = incoming
    }
  },
)

async function fetchAutopilotState() {
  if (!props.projectId || !canAccessBackend.value) return
  try {
    const loaded = await api.getProjectAutopilot(props.projectId)
    localAutopilot.value = loaded
  } catch {
    // Graceful fallback to settings/tasks projections if endpoint not yet warm.
  }
}

onMounted(() => {
  if (props.autopilot === undefined && props.projectId) {
    void fetchAutopilotState()
  }
})

watch(
  () => props.projectId,
  (nextId, prevId) => {
    if (nextId && nextId !== prevId && props.autopilot === undefined) {
      void fetchAutopilotState()
    }
  },
)

const isEnabled = computed<boolean>(
  () => localAutopilot.value?.autopilot_enabled ?? props.settings?.autopilot_enabled ?? false,
)

const maxConcurrency = computed<number>(() => {
  const raw = localAutopilot.value?.max_concurrency ?? props.settings?.max_concurrency ?? 2
  return Math.max(1, Math.min(8, raw))
})

const activeTaskCount = computed<number>(() => {
  if (localAutopilot.value) return localAutopilot.value.active_task_count
  return (props.tasks ?? []).filter((item) => item.status === 'in_progress' && Boolean(item.active_run_id)).length
})

const queuedReadyCount = computed<number>(() => {
  if (localAutopilot.value) return localAutopilot.value.queued_ready_count
  return (props.tasks ?? []).filter((item) => item.status === 'ready' && !item.blocked).length
})

const blockedTasksList = computed<ProjectBoardTaskDto[]>(() => {
  const byId = new Map<string, ProjectBoardTaskDto>()
  for (const item of localAutopilot.value?.blocked_tasks ?? []) {
    if (item.blocked) {
      byId.set(item.task_id, item)
    }
  }
  for (const item of props.tasks ?? []) {
    if (item.blocked) {
      byId.set(item.task_id, item)
    } else if (props.tasks && byId.has(item.task_id) && !item.blocked) {
      // If board tasks array already reflects unblocked state, prefer fresher unblocked status
      const existing = byId.get(item.task_id)!
      if (item.revision >= existing.revision) {
        byId.delete(item.task_id)
      }
    }
  }
  return [...byId.values()]
})

const blockedCount = computed<number>(() =>
  Math.max(localAutopilot.value?.blocked_task_count ?? 0, blockedTasksList.value.length),
)

const lastError = computed<string | null>(() => localAutopilot.value?.last_error ?? null)

async function applyAutopilotPatch(patch: {
  autopilot_enabled?: boolean
  max_concurrency?: number
  cancel_active?: boolean
}) {
  if (!props.projectId || updating.value) return
  updating.value = true
  actionError.value = null
  try {
    const nextState = await api.updateProjectAutopilot(props.projectId, patch)
    localAutopilot.value = nextState
    emit('updated', nextState)
  } catch (err) {
    actionError.value = err instanceof Error ? err.message : String(err)
  } finally {
    updating.value = false
  }
}

function handleToggleEnabled(nextValue: boolean) {
  void applyAutopilotPatch({ autopilot_enabled: nextValue })
}

function handleStepConcurrency(delta: number) {
  const nextVal = Math.max(1, Math.min(8, maxConcurrency.value + delta))
  if (nextVal === maxConcurrency.value) return
  void applyAutopilotPatch({ max_concurrency: nextVal })
}

function handlePauseAndCancelActive() {
  void applyAutopilotPatch({ autopilot_enabled: false, cancel_active: true })
}

async function handleUnblockAndResume(task: ProjectBoardTaskDto) {
  if (unblockingTaskId.value) return
  unblockingTaskId.value = task.task_id
  actionError.value = null
  try {
    const targetPid = task.project_id || props.projectId
    const updatedTask = await api.updateProjectBoardTask(targetPid, task.task_id, {
      blocked: false,
      blocked_reason: null,
      status: task.status === 'done' ? 'done' : 'ready',
      expected_revision: task.revision,
    })
    if (localAutopilot.value) {
      const remaining = localAutopilot.value.blocked_tasks.filter((tItem) => tItem.task_id !== task.task_id)
      localAutopilot.value = {
        ...localAutopilot.value,
        blocked_tasks: remaining,
        blocked_task_count: remaining.length,
      }
    }
    emit('unblocked-task', updatedTask)
    void fetchAutopilotState()
  } catch (err) {
    actionError.value = err instanceof Error ? err.message : String(err)
  } finally {
    unblockingTaskId.value = null
  }
}
</script>

<template>
  <div class="autopilot-container" data-testid="board-autopilot-bar">
    <!-- Compact Monochrome Autopilot Control Strip -->
    <div class="autopilot-strip">
      <div class="autopilot-left">
        <div class="autopilot-switch-group">
          <UiSwitch
            :model-value="isEnabled"
            :disabled="updating"
            :aria-label="t('board.autopilot.toggleAria')"
            data-testid="autopilot-toggle-switch"
            @update:model-value="handleToggleEnabled"
          />
          <Bot :size="14" class="autopilot-icon" />
          <span class="autopilot-title">{{ t('board.autopilot.title') }}</span>
          <span
            class="autopilot-state-pill"
            :class="{ active: isEnabled }"
            data-testid="autopilot-status-badge"
          >
            {{ isEnabled ? t('board.autopilot.statusRunning') : t('board.autopilot.statusPaused') }}
          </span>
        </div>

        <!-- Max Concurrency Stepper (1..8) -->
        <div class="autopilot-stepper-wrap" :title="t('board.autopilot.concurrencyHint')">
          <span class="autopilot-stepper-label">{{ t('board.autopilot.concurrencyLabel') }}</span>
          <div class="autopilot-stepper">
            <button
              type="button"
              class="autopilot-step-btn ui-pressable"
              :disabled="updating || maxConcurrency <= 1"
              :aria-label="t('board.autopilot.decreaseConcurrency')"
              data-testid="autopilot-concurrency-dec"
              @click="handleStepConcurrency(-1)"
            >
              <Minus :size="11" />
            </button>
            <span class="autopilot-step-val" data-testid="autopilot-concurrency-value">
              {{ maxConcurrency }}
            </span>
            <button
              type="button"
              class="autopilot-step-btn ui-pressable"
              :disabled="updating || maxConcurrency >= 8"
              :aria-label="t('board.autopilot.increaseConcurrency')"
              data-testid="autopilot-concurrency-inc"
              @click="handleStepConcurrency(1)"
            >
              <Plus :size="11" />
            </button>
          </div>
        </div>

        <!-- Live Counters: Running X / Max Y · Ready Queue N -->
        <div class="autopilot-counters" data-testid="autopilot-live-counters">
          <span>
            {{
              t('board.autopilot.runningCounter', {
                active: activeTaskCount,
                max: maxConcurrency,
              })
            }}
          </span>
          <span class="autopilot-dot">·</span>
          <span>
            {{ t('board.autopilot.readyQueueCounter', { count: queuedReadyCount }) }}
          </span>
        </div>
      </div>

      <div class="autopilot-right">
        <span v-if="lastError" class="autopilot-error-pill" :title="lastError" data-testid="autopilot-last-error">
          <AlertTriangle :size="11" />
          <span>{{ lastError }}</span>
        </span>

        <button
          type="button"
          class="autopilot-quick-btn ui-pressable"
          :disabled="updating"
          data-testid="autopilot-instant-toggle-btn"
          @click="handleToggleEnabled(!isEnabled)"
        >
          <Pause v-if="isEnabled" :size="12" />
          <Play v-else :size="12" />
          <span>
            {{ isEnabled ? t('board.autopilot.instantPause') : t('board.autopilot.startAutopilot') }}
          </span>
        </button>

        <UiButton
          v-if="activeTaskCount > 0"
          type="button"
          variant="outline"
          size="sm"
          :disabled="updating"
          data-testid="autopilot-pause-cancel-btn"
          @click="handlePauseAndCancelActive"
        >
          <Square :size="11" />
          <span>{{ t('board.autopilot.pauseAndCancelActive') }}</span>
        </UiButton>
      </div>
    </div>

    <div v-if="actionError" class="autopilot-inline-error" role="alert">
      {{ actionError }}
    </div>

    <!-- Prominent Monochrome Blocked Human Intervention Banner -->
    <div
      v-if="blockedCount > 0"
      class="autopilot-blocked-banner"
      role="region"
      :aria-label="t('board.autopilot.blockedBannerAria')"
      data-testid="autopilot-blocked-banner"
    >
      <div class="autopilot-blocked-header">
        <div class="autopilot-blocked-heading">
          <span class="autopilot-blocked-badge">
            <AlertTriangle :size="12" />
            <span>{{ t('board.blocked') }}</span>
          </span>
          <strong>{{ t('board.autopilot.blockedBannerTitle', { count: blockedCount }) }}</strong>
          <span class="autopilot-blocked-sub">{{ t('board.autopilot.blockedBannerHint') }}</span>
        </div>
      </div>

      <div v-if="blockedTasksList.length > 0" class="autopilot-blocked-list">
        <div
          v-for="bTask in blockedTasksList"
          :key="bTask.task_id"
          class="autopilot-blocked-row"
          :data-testid="`autopilot-blocked-item-${bTask.task_id}`"
        >
          <div class="autopilot-blocked-info">
            <span class="autopilot-task-key">{{ bTask.task_key }}</span>
            <span class="autopilot-task-title">{{ bTask.title }}</span>
            <span class="autopilot-task-reason">
              — {{ bTask.blocked_reason || t('board.autopilot.blockedDefaultReason') }}
            </span>
          </div>

          <div class="autopilot-blocked-actions">
            <button
              type="button"
              class="autopilot-row-btn ui-pressable"
              :data-testid="`autopilot-blocked-open-${bTask.task_id}`"
              @click="emit('open-task', bTask)"
            >
              <ExternalLink :size="11" />
              <span>{{ t('board.autopilot.openInDrawer') }}</span>
            </button>
            <button
              type="button"
              class="autopilot-row-btn autopilot-row-btn--primary ui-pressable"
              :disabled="unblockingTaskId === bTask.task_id"
              :data-testid="`autopilot-blocked-unblock-${bTask.task_id}`"
              @click="handleUnblockAndResume(bTask)"
            >
              <Play :size="11" />
              <span>{{ t('board.autopilot.unblockAndResume') }}</span>
            </button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.autopilot-container {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 0 16px 6px;
}

.autopilot-strip {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  padding: 7px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 68%, transparent);
}

.autopilot-left {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.autopilot-switch-group {
  display: inline-flex;
  align-items: center;
  gap: 7px;
}

.autopilot-icon {
  color: var(--text-secondary);
}

.autopilot-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.autopilot-state-pill {
  display: inline-flex;
  align-items: center;
  height: 20px;
  padding: 0 7px;
  border-radius: 999px;
  font-size: 10.5px;
  font-weight: 500;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-muted);
  transition: background-color 150ms ease, color 150ms ease, border-color 150ms ease;
}

.autopilot-state-pill.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.autopilot-stepper-wrap {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.autopilot-stepper-label {
  font-size: 11.5px;
  color: var(--text-muted);
}

.autopilot-stepper {
  display: inline-flex;
  align-items: center;
  height: 24px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-secondary));
  overflow: hidden;
}

.autopilot-step-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.autopilot-step-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.autopilot-step-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.autopilot-step-val {
  min-width: 22px;
  text-align: center;
  font-family: var(--font-mono, monospace);
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.autopilot-counters {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 11.5px;
  color: var(--text-secondary);
}

.autopilot-dot {
  color: var(--text-muted);
}

.autopilot-right {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.autopilot-error-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 240px;
  height: 24px;
  padding: 0 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.autopilot-quick-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 10px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 150ms ease, color 150ms ease;
}

.autopilot-quick-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.autopilot-inline-error {
  padding: 5px 10px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-error, var(--text-primary));
  font-size: 11.5px;
}

.autopilot-blocked-banner {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-default, var(--border-muted));
  background: var(--surface-raised, var(--bg-secondary));
}

.autopilot-blocked-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.autopilot-blocked-heading {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 12px;
  color: var(--text-primary);
}

.autopilot-blocked-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 21px;
  padding: 0 8px;
  border-radius: 999px;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 11px;
  font-weight: 600;
}

.autopilot-blocked-sub {
  font-size: 11.5px;
  color: var(--text-muted);
}

.autopilot-blocked-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.autopilot-blocked-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
  padding: 6px 9px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 65%, transparent);
}

.autopilot-blocked-info {
  display: flex;
  align-items: center;
  gap: 7px;
  flex-wrap: wrap;
  min-width: 0;
  font-size: 12px;
}

.autopilot-task-key {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  font-weight: 600;
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-primary);
}

.autopilot-task-title {
  font-weight: 500;
  color: var(--text-primary);
}

.autopilot-task-reason {
  color: var(--text-secondary);
  font-size: 11.5px;
}

.autopilot-blocked-actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.autopilot-row-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 25px;
  padding: 0 9px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}

.autopilot-row-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.autopilot-row-btn--primary {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.autopilot-row-btn--primary:hover:not(:disabled) {
  opacity: 0.9;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
}
</style>
