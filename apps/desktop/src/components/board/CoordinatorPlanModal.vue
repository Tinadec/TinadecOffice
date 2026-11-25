<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowLeft,
  Check,
  GitBranch,
  LoaderCircle,
  Plus,
  Sparkles,
  Trash2,
  X,
} from '@lucide/vue'
import { UiButton, UiCheckbox, UiSwitch } from '@/components/ui'
import {
  api,
  type CoordinatorPlannerSource,
  type CoordinatorTaskDraftDto,
  type ProjectBoardAssigneeKind,
  type ProjectBoardTaskDto,
  type ProjectBoardTaskStatus,
} from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { ApiError } from '@/lib/apiError'
import { BOARD_TASK_PRIORITIES } from './useProjectBoardView'

const props = defineProps<{
  open: boolean
  projectId: string
  existingTasks?: readonly ProjectBoardTaskDto[]
  defaultAssigneeKind?: ProjectBoardAssigneeKind
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
  applied: [tasks: ProjectBoardTaskDto[]]
}>()

const { t } = useI18n()
const { notify, dismissByKey } = useNotifications()

const COORDINATOR_ERROR_KEY = 'board-coordinator-plan'

type PlannerStep = 'goal' | 'review'
const step = ref<PlannerStep>('goal')

// Step 1 form state
const goalText = ref('')
const defaultStatus = ref<'ready' | 'backlog'>('ready')
const defaultAssignee = ref<'native' | 'cli' | 'coordinator'>('native')
const maxTasks = ref<number>(8)
const autoApply = ref(false)

// Step 2 draft state
const drafts = ref<CoordinatorTaskDraftDto[]>([])
const planSummary = ref('')
const plannerSource = ref<CoordinatorPlannerSource>('heuristic')
const updateNotes = ref(true)
let customDraftCounter = 1

const planning = ref(false)
const applying = ref(false)

const statusChoices: readonly ('ready' | 'backlog')[] = ['ready', 'backlog']
const assigneeChoices: readonly ('native' | 'cli' | 'coordinator')[] = ['native', 'cli', 'coordinator']

const validDraftsCount = computed(
  () => drafts.value.filter((item) => item.title.trim().length > 0).length,
)

function normalizeAssigneeChoice(kind?: ProjectBoardAssigneeKind): 'native' | 'cli' | 'coordinator' {
  if (kind === 'cli' || kind === 'coordinator' || kind === 'native') return kind
  return 'native'
}

function resetModalState() {
  step.value = 'goal'
  goalText.value = ''
  defaultStatus.value = 'ready'
  defaultAssignee.value = normalizeAssigneeChoice(props.defaultAssigneeKind)
  maxTasks.value = 8
  autoApply.value = false
  drafts.value = []
  planSummary.value = ''
  plannerSource.value = 'heuristic'
  updateNotes.value = true
  dismissByKey(COORDINATOR_ERROR_KEY)
  planning.value = false
  applying.value = false
}

function close() {
  if (planning.value || applying.value) return
  dismissByKey(COORDINATOR_ERROR_KEY)
  emit('update:open', false)
}

function onWindowKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && !event.defaultPrevented) {
    event.preventDefault()
    close()
  }
}

watch(
  () => props.open,
  (isOpen) => {
    if (isOpen) {
      resetModalState()
      window.addEventListener('keydown', onWindowKeydown)
    } else {
      dismissByKey(COORDINATOR_ERROR_KEY)
      window.removeEventListener('keydown', onWindowKeydown)
    }
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  dismissByKey(COORDINATOR_ERROR_KEY)
  window.removeEventListener('keydown', onWindowKeydown)
})

function cloneDraft(raw: CoordinatorTaskDraftDto): CoordinatorTaskDraftDto {
  return {
    draft_id: raw.draft_id,
    title: raw.title,
    description_md: raw.description_md,
    checklist: raw.checklist.map((item) => ({ ...item })),
    priority: raw.priority,
    status: raw.status,
    assignee: raw.assignee ? { ...raw.assignee } : { kind: defaultAssignee.value },
    labels: [...raw.labels],
    depends_on_draft_ids: [...raw.depends_on_draft_ids],
    depends_on_task_ids: [...raw.depends_on_task_ids],
  }
}

function clampMaxTasks(value: number): number {
  if (!Number.isFinite(value)) return 8
  return Math.max(1, Math.min(20, Math.round(value)))
}

function reportCoordinatorError(err: unknown) {
  const isCycle =
    (err instanceof ApiError && err.code === 'cycle_detected') ||
    Boolean(err && typeof err === 'object' && (err as { code?: unknown }).code === 'cycle_detected')
  if (isCycle) {
    notify.error(t('board.coordinator.cycleDetectedError'), {
      title: t('board.coordinator.modalTitle'),
      key: COORDINATOR_ERROR_KEY,
    })
    return
  }
  notify.error(err, {
    title: t('board.coordinator.modalTitle'),
    key: COORDINATOR_ERROR_KEY,
  })
}

async function handleGeneratePlan() {
  const trimmedGoal = goalText.value.trim()
  if (!trimmedGoal || !props.projectId || planning.value) return

  planning.value = true
  dismissByKey(COORDINATOR_ERROR_KEY)
  const boundedMax = clampMaxTasks(maxTasks.value)
  maxTasks.value = boundedMax

  try {
    const result = await api.planCoordinatorTasks(props.projectId, {
      goal: trimmedGoal,
      default_status: defaultStatus.value,
      default_assignee: { kind: defaultAssignee.value },
      auto_apply: autoApply.value,
      max_tasks: boundedMax,
    })

    planSummary.value = result.summary
    plannerSource.value = result.planner_source

    if (result.auto_applied) {
      emit('applied', result.created_tasks)
      emit('update:open', false)
      return
    }

    drafts.value = result.drafts.map(cloneDraft)
    step.value = 'review'
  } catch (err) {
    reportCoordinatorError(err)
  } finally {
    planning.value = false
  }
}

function addCustomDraft() {
  let candidateId = `draft-custom-${customDraftCounter++}`
  while (drafts.value.some((d) => d.draft_id === candidateId)) {
    candidateId = `draft-custom-${customDraftCounter++}`
  }
  drafts.value = [
    ...drafts.value,
    {
      draft_id: candidateId,
      title: '',
      description_md: '',
      checklist: [],
      priority: 'medium',
      status: defaultStatus.value,
      assignee: { kind: defaultAssignee.value },
      labels: [],
      depends_on_draft_ids: [],
      depends_on_task_ids: [],
    },
  ]
}

function removeDraft(draftId: string) {
  drafts.value = drafts.value
    .filter((item) => item.draft_id !== draftId)
    .map((item) => ({
      ...item,
      depends_on_draft_ids: item.depends_on_draft_ids.filter((dep) => dep !== draftId),
    }))
}

function toggleDraftDependency(targetDraft: CoordinatorTaskDraftDto, depDraftId: string) {
  if (targetDraft.draft_id === depDraftId) return
  const idx = targetDraft.depends_on_draft_ids.indexOf(depDraftId)
  if (idx >= 0) {
    targetDraft.depends_on_draft_ids = targetDraft.depends_on_draft_ids.filter((id) => id !== depDraftId)
  } else {
    targetDraft.depends_on_draft_ids = [...targetDraft.depends_on_draft_ids, depDraftId]
  }
}

function toggleTaskDependency(targetDraft: CoordinatorTaskDraftDto, depTaskId: string) {
  const idx = targetDraft.depends_on_task_ids.indexOf(depTaskId)
  if (idx >= 0) {
    targetDraft.depends_on_task_ids = targetDraft.depends_on_task_ids.filter((id) => id !== depTaskId)
  } else {
    targetDraft.depends_on_task_ids = [...targetDraft.depends_on_task_ids, depTaskId]
  }
}

async function handleApplyPlan() {
  const cleanDrafts = drafts.value
    .map((item) => ({
      ...item,
      title: item.title.trim(),
    }))
    .filter((item) => item.title.length > 0)

  if (!cleanDrafts.length || !props.projectId || applying.value) return

  applying.value = true
  dismissByKey(COORDINATOR_ERROR_KEY)
  try {
    const res = await api.applyCoordinatorPlan(props.projectId, {
      goal: goalText.value.trim() || null,
      update_notes: updateNotes.value,
      drafts: cleanDrafts,
    })
    emit('applied', res.created_tasks)
    emit('update:open', false)
  } catch (err) {
    reportCoordinatorError(err)
  } finally {
    applying.value = false
  }
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open"
      class="coordinator-modal-backdrop"
      data-testid="coordinator-plan-backdrop"
      @click.self="close"
    >
      <div
        class="coordinator-modal-dialog"
        role="dialog"
        aria-modal="true"
        :aria-label="t('board.coordinator.modalTitle')"
        data-testid="coordinator-plan-modal"
      >
        <!-- Modal Header -->
        <header class="coordinator-modal-header">
          <div class="coordinator-header-title">
            <Sparkles :size="15" />
            <div>
              <h2>{{ t('board.coordinator.modalTitle') }}</h2>
              <p>{{ t('board.coordinator.modalSubtitle') }}</p>
            </div>
          </div>
          <button
            type="button"
            class="coordinator-close-btn ui-pressable"
            :aria-label="t('common.cancel')"
            data-testid="coordinator-close-btn"
            @click="close"
          >
            <X :size="15" />
          </button>
        </header>

        <!-- STEP 1: Goal / PRD Input & Planner Parameters -->
        <div v-if="step === 'goal'" class="coordinator-step-body" data-testid="coordinator-step-goal">
          <div class="coordinator-field">
            <label class="coordinator-label">{{ t('board.coordinator.goalLabel') }}</label>
            <textarea
              v-model="goalText"
              rows="7"
              class="coordinator-textarea"
              :placeholder="t('board.coordinator.goalPlaceholder')"
              data-testid="coordinator-goal-input"
            />
          </div>

          <div class="coordinator-options-grid">
            <div class="coordinator-field">
              <label class="coordinator-label">{{ t('board.coordinator.defaultStatusLabel') }}</label>
              <select
                v-model="defaultStatus"
                class="coordinator-select"
                data-testid="coordinator-default-status"
              >
                <option v-for="st in statusChoices" :key="st" :value="st">
                  {{ t(`board.status.${st}`) }}
                </option>
              </select>
            </div>

            <div class="coordinator-field">
              <label class="coordinator-label">{{ t('board.coordinator.defaultAssigneeLabel') }}</label>
              <select
                v-model="defaultAssignee"
                class="coordinator-select"
                data-testid="coordinator-default-assignee"
              >
                <option v-for="kind in assigneeChoices" :key="kind" :value="kind">
                  {{ t(`board.assignee.${kind}`) }}
                </option>
              </select>
            </div>

            <div class="coordinator-field">
              <label class="coordinator-label">{{ t('board.coordinator.maxTasksLabel') }}</label>
              <input
                v-model.number="maxTasks"
                type="number"
                min="1"
                max="20"
                class="coordinator-input"
                data-testid="coordinator-max-tasks"
              />
            </div>
          </div>

          <div class="coordinator-auto-apply-row">
            <div class="coordinator-auto-apply-copy">
              <span class="coordinator-label">{{ t('board.coordinator.autoApplyLabel') }}</span>
              <span class="coordinator-hint">{{ t('board.coordinator.autoApplyHint') }}</span>
            </div>
            <UiSwitch
              v-model="autoApply"
              :aria-label="t('board.coordinator.autoApplyLabel')"
              data-testid="coordinator-auto-apply-switch"
            />
          </div>

          <footer class="coordinator-modal-footer">
            <UiButton type="button" variant="ghost" size="sm" @click="close">
              {{ t('common.cancel') }}
            </UiButton>
            <button
              type="button"
              class="coordinator-primary-btn ui-pressable"
              :disabled="!goalText.trim() || planning"
              data-testid="coordinator-generate-btn"
              @click="handleGeneratePlan"
            >
              <LoaderCircle v-if="planning" :size="14" class="animate-spin" />
              <Sparkles v-else :size="14" />
              <span>
                {{ planning ? t('board.coordinator.generating') : t('board.coordinator.generateDrafts') }}
              </span>
            </button>
          </footer>
        </div>

        <!-- STEP 2: Draft Review, DAG Dependency Tweaking & 1-Click Apply -->
        <div v-else class="coordinator-step-body coordinator-review-body" data-testid="coordinator-step-review">
          <div class="coordinator-review-topbar">
            <div class="coordinator-review-meta">
              <button
                type="button"
                class="coordinator-ghost-chip ui-pressable"
                data-testid="coordinator-back-btn"
                @click="step = 'goal'"
              >
                <ArrowLeft :size="12" />
                <span>{{ t('board.coordinator.backToGoal') }}</span>
              </button>
              <span class="coordinator-source-pill" data-testid="coordinator-planner-source">
                {{ t(`board.coordinator.source.${plannerSource}`) }}
              </span>
              <span v-if="planSummary" class="coordinator-summary-text">
                {{ planSummary }}
              </span>
            </div>

            <button
              type="button"
              class="coordinator-ghost-chip ui-pressable"
              data-testid="coordinator-add-draft-btn"
              @click="addCustomDraft"
            >
              <Plus :size="12" />
              <span>{{ t('board.coordinator.addDraft') }}</span>
            </button>
          </div>

          <div class="coordinator-drafts-scroll" data-testid="coordinator-drafts-list">
            <article
              v-for="(draft, index) in drafts"
              :key="draft.draft_id"
              class="coordinator-draft-card"
              :data-testid="`coordinator-draft-card-${draft.draft_id}`"
            >
              <div class="coordinator-draft-top">
                <span class="coordinator-draft-index">#{{ index + 1 }}</span>
                <input
                  v-model="draft.title"
                  type="text"
                  class="coordinator-draft-title-input"
                  :placeholder="t('board.drawer.titlePlaceholder')"
                  :data-testid="`coordinator-draft-title-${draft.draft_id}`"
                />
                <select
                  v-model="draft.priority"
                  class="coordinator-mini-select"
                  :aria-label="t('board.priorityLabel')"
                  :data-testid="`coordinator-draft-priority-${draft.draft_id}`"
                >
                  <option
                    v-for="prio in BOARD_TASK_PRIORITIES"
                    :key="prio"
                    :value="prio"
                  >
                    {{ t(`board.priority.${prio}`) }}
                  </option>
                </select>
                <select
                  v-model="draft.status"
                  class="coordinator-mini-select"
                  :aria-label="t('board.statusLabel')"
                  :data-testid="`coordinator-draft-status-${draft.draft_id}`"
                >
                  <option
                    v-for="st in statusChoices"
                    :key="st"
                    :value="st"
                  >
                    {{ t(`board.status.${st}`) }}
                  </option>
                </select>
                <button
                  type="button"
                  class="coordinator-delete-btn ui-pressable"
                  :title="t('board.actions.delete')"
                  :aria-label="t('board.actions.delete')"
                  :data-testid="`coordinator-draft-delete-${draft.draft_id}`"
                  @click="removeDraft(draft.draft_id)"
                >
                  <Trash2 :size="13" />
                </button>
              </div>

              <textarea
                v-model="draft.description_md"
                rows="2"
                class="coordinator-draft-desc"
                :placeholder="t('board.drawer.descriptionPlaceholder')"
                :data-testid="`coordinator-draft-desc-${draft.draft_id}`"
              />

              <!-- Intra-plan & Existing Board DAG Dependencies -->
              <div class="coordinator-deps-row">
                <span class="coordinator-deps-label">
                  <GitBranch :size="11" />
                  <span>{{ t('board.coordinator.dependsOnLabel') }}:</span>
                </span>

                <button
                  v-for="(other, otherIdx) in drafts.filter((d) => d.draft_id !== draft.draft_id)"
                  :key="other.draft_id"
                  type="button"
                  class="coordinator-dep-chip ui-pressable"
                  :class="{ active: draft.depends_on_draft_ids.includes(other.draft_id) }"
                  :data-testid="`coordinator-dep-draft-${draft.draft_id}-${other.draft_id}`"
                  @click="toggleDraftDependency(draft, other.draft_id)"
                >
                  <span>#{{ drafts.findIndex((d) => d.draft_id === other.draft_id) + 1 || otherIdx + 1 }}</span>
                  <span class="coordinator-dep-chip-title">
                    {{ other.title || other.draft_id }}
                  </span>
                </button>

                <button
                  v-for="existing in (existingTasks ?? []).slice(0, 12)"
                  :key="existing.task_id"
                  type="button"
                  class="coordinator-dep-chip ui-pressable"
                  :class="{ active: draft.depends_on_task_ids.includes(existing.task_id) }"
                  :data-testid="`coordinator-dep-task-${draft.draft_id}-${existing.task_id}`"
                  @click="toggleTaskDependency(draft, existing.task_id)"
                >
                  <span>{{ existing.task_key }}</span>
                </button>
              </div>
            </article>

            <p v-if="!drafts.length" class="coordinator-empty-drafts">
              {{ t('board.coordinator.emptyDrafts') }}
            </p>
          </div>

          <footer class="coordinator-modal-footer coordinator-review-footer">
            <label
              class="coordinator-notes-check"
              data-testid="coordinator-update-notes-label"
            >
              <UiCheckbox
                :model-value="updateNotes"
                :aria-label="t('board.coordinator.updateNotesLabel')"
                data-testid="coordinator-update-notes-checkbox"
                @update:model-value="updateNotes = Boolean($event)"
              />
              <span>{{ t('board.coordinator.updateNotesLabel') }}</span>
            </label>

            <div class="coordinator-footer-actions">
              <UiButton type="button" variant="ghost" size="sm" @click="close">
                {{ t('common.cancel') }}
              </UiButton>
              <button
                type="button"
                class="coordinator-primary-btn ui-pressable"
                :disabled="validDraftsCount === 0 || applying"
                data-testid="coordinator-apply-btn"
                @click="handleApplyPlan"
              >
                <LoaderCircle v-if="applying" :size="14" class="animate-spin" />
                <Check v-else :size="14" />
                <span>
                  {{
                    applying
                      ? t('board.coordinator.applying')
                      : t('board.coordinator.applyToBoard', { count: validDraftsCount })
                  }}
                </span>
              </button>
            </div>
          </footer>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.coordinator-modal-backdrop {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 20px;
  background: rgba(0, 0, 0, 0.48);
}

.coordinator-modal-dialog {
  width: min(820px, 100%);
  max-height: min(86vh, 840px);
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 18px 20px;
  border-radius: var(--radius-island, 12px);
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  box-shadow: var(--shadow-elevated, 0 18px 48px rgba(0, 0, 0, 0.28));
  overflow: hidden;
}

.coordinator-modal-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.coordinator-header-title {
  display: flex;
  align-items: flex-start;
  gap: 9px;
}

.coordinator-header-title h2 {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
  color: var(--text-primary);
}

.coordinator-header-title p {
  margin: 2px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}

.coordinator-close-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  border: 1px solid transparent;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.coordinator-close-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.coordinator-step-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
}

.coordinator-review-body {
  flex: 1;
  overflow: hidden;
}

.coordinator-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.coordinator-label {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
}

.coordinator-hint {
  font-size: 11.5px;
  color: var(--text-muted);
}

.coordinator-textarea {
  width: 100%;
  resize: vertical;
  padding: 10px 12px;
  border-radius: 8px;
  border: 1px solid var(--border-input, var(--border-muted));
  background: var(--bg-input, var(--surface-section));
  color: var(--text-primary);
  font-size: 12.5px;
  line-height: 1.55;
  outline: none;
}

.coordinator-textarea:focus {
  border-color: var(--border-input-focus, var(--text-primary));
}

.coordinator-options-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 10px;
}

@media (max-width: 680px) {
  .coordinator-options-grid {
    grid-template-columns: 1fr;
  }
}

.coordinator-select,
.coordinator-input {
  height: 32px;
  padding: 0 10px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-input, var(--border-muted));
  background: var(--bg-input, var(--surface-section));
  color: var(--text-primary);
  font-size: 12px;
  outline: none;
}

.coordinator-auto-apply-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
}

.coordinator-auto-apply-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.coordinator-review-topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.coordinator-review-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  min-width: 0;
}

.coordinator-ghost-chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 9px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
}

.coordinator-ghost-chip:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.coordinator-source-pill {
  display: inline-flex;
  align-items: center;
  height: 22px;
  padding: 0 8px;
  border-radius: 999px;
  font-size: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
}

.coordinator-summary-text {
  font-size: 12px;
  color: var(--text-secondary);
}

.coordinator-drafts-scroll {
  flex: 1;
  min-height: 180px;
  max-height: 48vh;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 9px;
  padding-right: 2px;
}

.coordinator-draft-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 70%, transparent);
}

.coordinator-draft-top {
  display: flex;
  align-items: center;
  gap: 7px;
  flex-wrap: wrap;
}

.coordinator-draft-index {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  font-weight: 600;
  padding: 2px 6px;
  border-radius: 5px;
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-secondary);
}

.coordinator-draft-title-input {
  flex: 1;
  min-width: 180px;
  height: 28px;
  padding: 0 8px;
  border-radius: 6px;
  border: 1px solid var(--border-input, var(--border-muted));
  background: var(--bg-input, transparent);
  color: var(--text-primary);
  font-size: 12.5px;
  font-weight: 500;
  outline: none;
}

.coordinator-mini-select {
  height: 28px;
  padding: 0 7px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 11.5px;
}

.coordinator-delete-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  border-radius: 6px;
  border: 0;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.coordinator-delete-btn:hover {
  background: var(--surface-hover);
  color: var(--text-error, var(--text-primary));
}

.coordinator-draft-desc {
  width: 100%;
  resize: vertical;
  padding: 6px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.45;
  outline: none;
}

.coordinator-deps-row {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.coordinator-deps-label {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: var(--text-muted);
}

.coordinator-dep-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 210px;
  height: 22px;
  padding: 0 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
  transition: background-color 150ms ease, color 150ms ease, border-color 150ms ease;
}

.coordinator-dep-chip-title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.coordinator-dep-chip.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.coordinator-empty-drafts {
  margin: 12px 0;
  text-align: center;
  font-size: 12px;
  color: var(--text-muted);
}

.coordinator-modal-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding-top: 4px;
}

.coordinator-review-footer {
  justify-content: space-between;
  flex-wrap: wrap;
}

.coordinator-notes-check {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  font-size: 12px;
  color: var(--text-secondary);
  cursor: pointer;
}

.coordinator-footer-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.coordinator-primary-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 14px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid transparent;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: opacity 150ms ease, background-color 150ms ease;
}

.coordinator-primary-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
