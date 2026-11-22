<script setup lang="ts">
import { computed, ref, toRef } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertCircle,
  Bot,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Circle,
  Clock,
  FileText,
  GitBranch,
  Link2,
  ListChecks,
  Loader2,
  Paperclip,
  Plus,
  RefreshCw,
  ShieldCheck,
  SkipForward,
  Sparkles,
  XCircle,
} from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'
import {
  useSessionPlan,
  type SessionPlanPhaseDto,
  type SessionPlanStepDto,
  type SessionPlanStepStatus,
} from './useSessionPlan'

const props = withDefaults(
  defineProps<{
    sessionId: string | null | undefined
    compact?: boolean
  }>(),
  {
    compact: false,
  },
)

const emit = defineEmits<{
  'focus-node': [target: { taskKey?: string | null; agentSlug?: string | null }]
}>()

const { t, te } = useI18n()

function label(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

const sessionIdRef = toRef(props, 'sessionId')
const {
  plan,
  progress,
  progressPercent,
  hasPlan,
  canApprove,
  loading,
  mutating,
  error,
  conflictNotice,
  refresh,
  approvePlan,
  updateStepStatus,
  addStep,
} = useSessionPlan(sessionIdRef)

const collapsedPhases = ref<Record<string, boolean>>({})
const addingStep = ref(false)
const newStepTitle = ref('')
const newStepPhaseId = ref('')
const newStepAgentSlug = ref('')

const stepStatusOptions = computed<Array<{ value: SessionPlanStepStatus; label: string }>>(() => [
  { value: 'pending', label: label('sessionPlan.pending', 'Pending') },
  { value: 'in_progress', label: label('sessionPlan.inProgress', 'In progress') },
  { value: 'completed', label: label('sessionPlan.completed', 'Completed') },
  { value: 'blocked', label: label('sessionPlan.blocked', 'Blocked') },
  { value: 'failed', label: label('sessionPlan.failed', 'Failed') },
  { value: 'skipped', label: label('sessionPlan.skipped', 'Skipped') },
])

const phaseOptions = computed(() =>
  (plan.value?.phases ?? []).map((phase) => ({
    value: phase.id,
    label: phase.title || phase.id,
  })),
)

function togglePhase(phaseId: string): void {
  collapsedPhases.value = {
    ...collapsedPhases.value,
    [phaseId]: !collapsedPhases.value[phaseId],
  }
}

function phaseDoneCount(phase: SessionPlanPhaseDto): number {
  return phase.steps.filter((step) => step.status === 'completed').length
}

function planStatusText(status: string): string {
  switch (status) {
    case 'draft': return label('sessionPlan.draft', 'Draft')
    case 'approved': return label('sessionPlan.approved', 'Approved')
    case 'active': return label('sessionPlan.active', 'Active')
    case 'completed': return label('sessionPlan.completed', 'Completed')
    case 'blocked': return label('sessionPlan.blocked', 'Blocked')
    default: return status
  }
}

function stepStatusText(status: SessionPlanStepStatus): string {
  switch (status) {
    case 'in_progress': return label('sessionPlan.inProgress', 'In progress')
    case 'completed': return label('sessionPlan.completed', 'Completed')
    case 'blocked': return label('sessionPlan.blocked', 'Blocked')
    case 'failed': return label('sessionPlan.failed', 'Failed')
    case 'skipped': return label('sessionPlan.skipped', 'Skipped')
    default: return label('sessionPlan.pending', 'Pending')
  }
}

function stepStatusIcon(status: SessionPlanStepStatus) {
  switch (status) {
    case 'completed': return CheckCircle2
    case 'in_progress': return Clock
    case 'blocked': return AlertCircle
    case 'failed': return XCircle
    case 'skipped': return SkipForward
    default: return Circle
  }
}

function artifactIcon(kind: string) {
  switch (kind) {
    case 'worktree': return GitBranch
    case 'attachment': return Paperclip
    default: return FileText
  }
}

function focusStepTarget(step: SessionPlanStepDto): void {
  const detail = {
    taskKey: step.bound_task_key ?? null,
    agentSlug: step.assigned_agent_slug ?? null,
  }
  emit('focus-node', detail)
  if (typeof window !== 'undefined' && (detail.taskKey || detail.agentSlug)) {
    window.dispatchEvent(new CustomEvent('tinadec:focus-orchestration-node', { detail }))
  }
}

function openAddStepForm(): void {
  addingStep.value = true
  newStepTitle.value = ''
  newStepAgentSlug.value = ''
  newStepPhaseId.value = plan.value?.phases[0]?.id ?? ''
}

async function submitNewStep(): Promise<void> {
  const title = newStepTitle.value.trim()
  if (!title) return
  const ok = await addStep({
    title,
    phase_id: newStepPhaseId.value || undefined,
    assigned_agent_slug: newStepAgentSlug.value.trim() || undefined,
  })
  if (ok) {
    addingStep.value = false
    newStepTitle.value = ''
    newStepAgentSlug.value = ''
  }
}

async function onChangeStepStatus(step: SessionPlanStepDto, nextStatus: string): Promise<void> {
  if (!nextStatus || nextStatus === step.status) return
  await updateStepStatus(step.id, nextStatus as SessionPlanStepStatus)
}
</script>

<template>
  <section
    class="session-plan-panel"
    :class="{ 'session-plan-panel--compact': compact }"
    data-testid="session-plan-panel"
  >
    <header class="session-plan-head">
      <div class="session-plan-head__title">
        <ListChecks :size="15" aria-hidden="true" />
        <div class="session-plan-head__meta">
          <strong>{{ plan?.title || label('sessionPlan.title', 'Session Plan') }}</strong>
          <p v-if="!compact && (plan?.goal || !hasPlan)" class="session-plan-subtitle">
            {{ plan?.goal || label('sessionPlan.subtitle', 'Multi-phase structured plan synced with live tasks and artifacts') }}
          </p>
        </div>
      </div>

      <div class="session-plan-head__actions">
        <UiBadge
          v-if="plan && hasPlan"
          :variant="plan.status === 'completed' || plan.status === 'approved' ? 'default' : plan.status === 'blocked' ? 'destructive' : 'secondary'"
          data-testid="session-plan-status"
        >
          {{ planStatusText(plan.status) }}
        </UiBadge>
        <UiButton
          v-if="canApprove"
          size="sm"
          :disabled="mutating"
          data-testid="session-plan-approve"
          @click="approvePlan"
        >
          <ShieldCheck :size="13" />
          <span>{{ label('sessionPlan.approve', 'Approve plan') }}</span>
        </UiButton>
        <UiButton
          v-if="sessionId"
          variant="ghost"
          size="sm"
          :disabled="mutating"
          data-testid="session-plan-add-toggle"
          @click="addingStep ? (addingStep = false) : openAddStepForm()"
        >
          <Plus :size="13" />
          <span>{{ label('sessionPlan.addStep', 'Add step') }}</span>
        </UiButton>
        <UiButton
          variant="ghost"
          size="icon"
          :disabled="loading || !sessionId"
          :aria-label="label('settings.refresh', 'Refresh')"
          @click="refresh()"
        >
          <RefreshCw :size="13" :class="{ spinning: loading }" />
        </UiButton>
      </div>
    </header>

    <div v-if="hasPlan && progress.total_steps > 0" class="session-plan-progress" data-testid="session-plan-progress">
      <div class="session-plan-progress__bar" role="progressbar" :aria-valuenow="progressPercent" aria-valuemin="0" aria-valuemax="100">
        <div class="session-plan-progress__fill" :style="{ width: `${progressPercent}%` }" />
      </div>
      <div class="session-plan-progress__counts">
        <span>{{ progress.completed_steps }} / {{ progress.total_steps }}</span>
        <span v-if="progress.in_progress_steps > 0" class="is-active">
          {{ progress.in_progress_steps }} {{ label('sessionPlan.inProgress', 'In progress') }}
        </span>
        <span v-if="progress.blocked_steps > 0" class="is-blocked">
          {{ progress.blocked_steps }} {{ label('sessionPlan.blocked', 'Blocked') }}
        </span>
        <span v-if="progress.failed_steps > 0" class="is-failed">
          {{ progress.failed_steps }} {{ label('sessionPlan.failed', 'Failed') }}
        </span>
      </div>
    </div>

    <p v-if="conflictNotice" class="session-plan-notice" role="status">
      {{ label('sessionPlan.revisionReloaded', 'Plan was updated remotely; showing latest revision.') }}
    </p>
    <p v-if="error" class="session-plan-error" role="alert">
      {{ error }}
    </p>

    <form v-if="addingStep" class="session-plan-add-form" data-testid="session-plan-add-form" @submit.prevent="submitNewStep">
      <UiInput
        v-model="newStepTitle"
        :placeholder="label('sessionPlan.stepTitlePlaceholder', 'Step title...')"
        :aria-label="label('sessionPlan.stepTitlePlaceholder', 'Step title...')"
        data-testid="session-plan-new-step-title"
      />
      <div class="session-plan-add-form__row">
        <UiSelectField
          v-if="phaseOptions.length > 0"
          :model-value="newStepPhaseId"
          :options="phaseOptions"
          :aria-label="label('sessionPlan.phase', 'Phase')"
          @update:model-value="newStepPhaseId = $event"
        />
        <UiInput
          v-model="newStepAgentSlug"
          :placeholder="label('sessionPlan.agentPlaceholder', 'Agent slug (optional)')"
          :aria-label="label('sessionPlan.agentPlaceholder', 'Agent slug (optional)')"
        />
        <UiButton size="sm" type="submit" :disabled="mutating || !newStepTitle.trim()" data-testid="session-plan-new-step-submit">
          {{ label('common.add', 'Add') }}
        </UiButton>
      </div>
    </form>

    <div v-if="loading && !plan" class="session-plan-loading" role="status">
      <Loader2 :size="14" class="spinning" aria-hidden="true" />
      <span>{{ label('common.loading', 'Loading...') }}</span>
    </div>

    <div v-else-if="!hasPlan" class="session-plan-empty" data-testid="session-plan-empty">
      <Sparkles :size="18" aria-hidden="true" />
      <strong>{{ label('sessionPlan.empty', 'No session plan yet') }}</strong>
      <p>{{ label('sessionPlan.emptyHint', 'Enable Plan First or ask the coordinator to create a structured plan.') }}</p>
    </div>

    <div v-else-if="plan" class="session-plan-phases">
      <article
        v-for="phase in plan.phases"
        :key="phase.id"
        class="session-plan-phase"
        :data-testid="`session-plan-phase-${phase.id}`"
      >
        <button
          type="button"
          class="session-plan-phase__head"
          :aria-expanded="!collapsedPhases[phase.id]"
          @click="togglePhase(phase.id)"
        >
          <component :is="collapsedPhases[phase.id] ? ChevronRight : ChevronDown" :size="14" aria-hidden="true" />
          <span class="session-plan-phase__title">{{ phase.title }}</span>
          <span class="session-plan-phase__count">
            {{ phaseDoneCount(phase) }}/{{ phase.steps.length }}
          </span>
        </button>

        <p v-if="!collapsedPhases[phase.id] && phase.description" class="session-plan-phase__desc">
          {{ phase.description }}
        </p>

        <ul v-if="!collapsedPhases[phase.id]" class="session-plan-steps">
          <li
            v-for="step in phase.steps"
            :key="step.id"
            class="session-plan-step"
            :class="`is-${step.status}`"
            :data-testid="`session-plan-step-${step.id}`"
          >
            <div class="session-plan-step__main">
              <component :is="stepStatusIcon(step.status)" :size="14" class="session-plan-step__icon" aria-hidden="true" />
              <div class="session-plan-step__body">
                <div class="session-plan-step__top">
                  <span class="session-plan-step__title">{{ step.title }}</span>
                  <UiSelectField
                    class="session-plan-step__status-select"
                    :model-value="step.status"
                    :options="stepStatusOptions"
                    :disabled="mutating"
                    :aria-label="`${step.title}: ${stepStatusText(step.status)}`"
                    @update:model-value="onChangeStepStatus(step, $event)"
                  />
                </div>

                <p v-if="step.description" class="session-plan-step__desc">{{ step.description }}</p>
                <p v-if="step.summary" class="session-plan-step__summary">{{ step.summary }}</p>

                <div v-if="step.depends_on.length || step.assigned_agent_slug || step.bound_task_key" class="session-plan-step__chips">
                  <button
                    v-if="step.assigned_agent_slug"
                    type="button"
                    class="session-plan-chip session-plan-chip--action"
                    :title="label('sessionPlan.focusAgent', 'Focus in orchestration')"
                    @click="focusStepTarget(step)"
                  >
                    <Bot :size="11" aria-hidden="true" />
                    <span>{{ step.assigned_agent_slug }}</span>
                  </button>
                  <button
                    v-if="step.bound_task_key"
                    type="button"
                    class="session-plan-chip session-plan-chip--action"
                    :title="label('sessionPlan.boundTask', 'Bound task')"
                    @click="focusStepTarget(step)"
                  >
                    <Link2 :size="11" aria-hidden="true" />
                    <span>{{ step.bound_task_key }}</span>
                  </button>
                  <span
                    v-for="dep in step.depends_on"
                    :key="dep"
                    class="session-plan-chip session-plan-chip--dep"
                  >
                    {{ label('sessionPlan.dependsOn', 'Depends on') }}: {{ dep }}
                  </span>
                </div>

                <ul v-if="step.verification_criteria.length" class="session-plan-criteria">
                  <li v-for="(criterion, idx) in step.verification_criteria" :key="idx">
                    {{ criterion }}
                  </li>
                </ul>

                <div v-if="step.artifacts.length" class="session-plan-artifacts">
                  <span
                    v-for="(artifact, idx) in step.artifacts"
                    :key="`${artifact.label}-${idx}`"
                    class="session-plan-artifact"
                    :title="artifact.path || artifact.ref_id || artifact.label"
                  >
                    <component :is="artifactIcon(artifact.kind)" :size="11" aria-hidden="true" />
                    <span>{{ artifact.label }}</span>
                  </span>
                </div>
              </div>
            </div>
          </li>
        </ul>
      </article>
    </div>
  </section>
</template>

<style scoped>
.session-plan-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  min-width: 0;
}

.session-plan-panel--compact {
  padding: 10px;
  gap: 8px;
}

.session-plan-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.session-plan-head__title {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  min-width: 0;
  flex: 1;
}

.session-plan-head__meta {
  min-width: 0;
}

.session-plan-head__meta strong {
  display: block;
  font-size: 13px;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.session-plan-subtitle {
  margin: 2px 0 0;
  font-size: 12px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.session-plan-head__actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.session-plan-progress {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.session-plan-progress__bar {
  height: 5px;
  border-radius: 999px;
  background: var(--surface-hover);
  overflow: hidden;
}

.session-plan-progress__fill {
  height: 100%;
  border-radius: 999px;
  background: var(--accent-primary);
  transition: width 180ms ease;
}

.session-plan-progress__counts {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  font-size: 11px;
  color: var(--text-secondary);
}

.session-plan-progress__counts .is-active { color: var(--accent-primary); }
.session-plan-progress__counts .is-blocked { color: var(--accent-warning); }
.session-plan-progress__counts .is-failed { color: var(--accent-danger); }

.session-plan-notice {
  margin: 0;
  padding: 6px 8px;
  border-radius: 8px;
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 12px;
}

.session-plan-error {
  margin: 0;
  padding: 6px 8px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent-danger) 12%, transparent);
  color: var(--accent-danger);
  font-size: 12px;
}

.session-plan-add-form {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px;
  border-radius: 9px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
}

.session-plan-add-form__row {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.session-plan-add-form__row > * {
  flex: 1 1 120px;
}

.session-plan-add-form__row > button {
  flex: 0 0 auto;
}

.session-plan-loading,
.session-plan-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 18px 12px;
  text-align: center;
  color: var(--text-secondary);
  font-size: 12px;
}

.session-plan-empty strong {
  color: var(--text-primary);
  font-size: 13px;
}

.session-plan-empty p {
  margin: 0;
  max-width: 320px;
  line-height: 1.5;
}

.session-plan-phases {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.session-plan-phase {
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  overflow: hidden;
}

.session-plan-phase__head {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  cursor: pointer;
  font-size: 12px;
  font-weight: 600;
  text-align: left;
}

.session-plan-phase__head:hover {
  background: var(--surface-hover);
}

.session-plan-phase__title {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
}

.session-plan-phase__count {
  font-size: 11px;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.session-plan-phase__desc {
  margin: 0 10px 8px;
  font-size: 12px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.session-plan-steps {
  list-style: none;
  margin: 0;
  padding: 0 8px 8px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.session-plan-step {
  padding: 8px 9px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.session-plan-step.is-completed {
  border-color: color-mix(in srgb, var(--accent-success, #22c55e) 35%, var(--border-muted));
}

.session-plan-step.is-in_progress {
  border-color: color-mix(in srgb, var(--accent-primary) 45%, var(--border-muted));
}

.session-plan-step.is-blocked {
  border-color: color-mix(in srgb, var(--accent-warning) 45%, var(--border-muted));
}

.session-plan-step.is-failed {
  border-color: color-mix(in srgb, var(--accent-danger) 45%, var(--border-muted));
}

.session-plan-step__main {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}

.session-plan-step__icon {
  margin-top: 2px;
  flex-shrink: 0;
  color: var(--text-muted);
}

.session-plan-step.is-completed .session-plan-step__icon { color: var(--accent-success, #22c55e); }
.session-plan-step.is-in_progress .session-plan-step__icon { color: var(--accent-primary); }
.session-plan-step.is-blocked .session-plan-step__icon { color: var(--accent-warning); }
.session-plan-step.is-failed .session-plan-step__icon { color: var(--accent-danger); }

.session-plan-step__body {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.session-plan-step__top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.session-plan-step__title {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.session-plan-step__status-select {
  min-width: 108px;
  flex-shrink: 0;
}

.session-plan-step__desc,
.session-plan-step__summary {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.session-plan-step__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.session-plan-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
}

.session-plan-chip--action {
  cursor: pointer;
  color: var(--text-primary);
}

.session-plan-chip--action:hover {
  border-color: var(--accent-primary);
  background: var(--surface-hover);
}

.session-plan-chip--dep {
  color: var(--text-muted);
}

.session-plan-criteria {
  margin: 0;
  padding-left: 16px;
  font-size: 11px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.session-plan-artifacts {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.session-plan-artifact {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border-radius: 6px;
  font-size: 11px;
  background: var(--surface-hover);
  color: var(--text-primary);
}

.spinning {
  animation: session-plan-spin 1s linear infinite;
}

@keyframes session-plan-spin {
  to { transform: rotate(360deg); }
}
</style>
