<script setup lang="ts">
import { Check, Cpu, Dna, ThumbsDown, Workflow } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  api as baseApi,
  type AgentEvolutionProposalDto,
} from '../api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import { UiBadge, UiButton, UiCard, UiInput, UiLabel, UiSheet, UiSkeleton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'

const emit = defineEmits<{ changed: [] }>()

const { t } = useI18n()
const { notify, status, confirm, dismissByKey } = useNotifications()
const { canAccessBackend } = useHostAccess()

const proposals = ref<AgentEvolutionProposalDto[]>([])
const loading = ref(false)
const busy = ref(false)
const selectedProposalId = ref('')
const showPromotePanel = ref('')
const rejectReason = ref('')
const generateSessionId = ref('')
const generateLookback = ref('200')

/**
 * The promotion body is Core's `PromoteAgentCandidateRequest`
 * (TinadecCore/Contracts/Dtos/AgentEvolutionDtos.cs:17-21): `reason` + `eval_run_id`, nothing
 * else. The client type is `PromoteAgentDecisionInput` at `src/api.ts:1951` and matches it
 * exactly, so the call below is type-checked against the real wire contract.
 *
 * It re-reads the evaluation evidence and refuses with 422 `EVAL_REQUIRED` when
 * `eval_run_id` is absent, so the field is required in the UI. The agent identity is derived
 * from the proposal by Core — the old form's agent_id/mode/tools fields were ignored and
 * made promotion impossible.
 */
const promoteForm = ref({
  eval_run_id: '',
  reason: '',
})


const selectedProposal = computed(() =>
  proposals.value.find((p) => p.id === selectedProposalId.value) ?? null
)

watch(selectedProposalId, () => {
  rejectReason.value = ''
})

const sortedProposals = computed(() =>
  [...proposals.value].sort((a, b) => b.confidence - a.confidence)
)

/**
 * Layer spelling is not stable across data: Core writes `operation`, the pack manifests and
 * older proposals say `planning`, and both mean the governance seat. Trailing/leading case
 * and whitespace drift too, so the comparison is normalised once here rather than at each
 * call site — the icon, the label and the default route purpose must agree.
 */
function normalizeLayer(layer?: string | null): string {
  // `planner` is a third spelling seen in older proposal rows.
  const raw = String(layer ?? '').trim().toLowerCase()
  if (raw === 'planning' || raw === 'planner') return 'operation'
  return raw
}

function isOperationLayer(layer?: string | null): boolean {
  return normalizeLayer(layer) === 'operation'
}

function agentLayerLabel(layer?: string | null): string {
  const normalized = normalizeLayer(layer)
  if (normalized === 'operation') return t('settings.agentLayerPlanning')
  if (normalized === 'execution') return t('settings.agentLayerExecution')
  if (normalized === 'evolution') return t('settings.agentLayerEvolution')
  return String(layer ?? '')
}

function confidenceVariant(score: number): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (score >= 0.7) return 'default'
  if (score >= 0.4) return 'outline'
  return 'secondary'
}

function statusLabel(status: string): string {
  const map: Record<string, string> = {
    proposed: t('agentCenter.evolution.statusProposed'),
    promoted: t('agentCenter.evolution.statusPromoted'),
    rejected: t('agentCenter.evolution.statusRejected'),
    evaluating: t('agentCenter.evolution.statusEvaluating')
  }
  return map[status] ?? status
}

function statusVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'promoted') return 'default'
  if (status === 'rejected') return 'destructive'
  if (status === 'evaluating') return 'outline'
  return 'secondary'
}

async function loadProposals() {
  if (!canAccessBackend.value) return
  loading.value = true
  try {
    const proposalList = await api.listEvolutionProposals()
    proposals.value = Array.isArray(proposalList) ? proposalList : []
    if (!selectedProposalId.value && proposals.value.length > 0) {
      selectedProposalId.value = proposals.value[0].id
    }
    dismissByKey('evolution')
  } catch (err) {
    status.error({
      key: 'evolution',
      title: t('app.loadFailed'),
      message: err instanceof Error ? err.message : String(err),
      source: 'evolution',
      action: { label: t('app.retry'), run: loadProposals }
    })
  } finally {
    loading.value = false
  }
}

async function generateProposals() {
  if (!canAccessBackend.value) return
  busy.value = true
  try {
    const params: { session_id?: string; lookback_event_count?: number } = {}
    if (generateSessionId.value.trim()) params.session_id = generateSessionId.value.trim()
    const lookback = Number(generateLookback.value)
    if (lookback > 0) params.lookback_event_count = lookback
    const generated = await api.generateEvolutionProposals(params)
    proposals.value = Array.isArray(generated) ? generated : []
    if (proposals.value.length > 0) {
      selectedProposalId.value = proposals.value[0].id
    }
    notify.success({ message: t('agentCenter.evolution.generateSuccess', { count: proposals.value.length }), source: 'evolution' })
  } catch (err) {
    notify.error(err, { title: t('agentCenter.evolution.generateFailed'), source: 'evolution' })
  } finally {
    busy.value = false
  }
}

function openPromotePanel(proposal: AgentEvolutionProposalDto) {
  showPromotePanel.value = proposal.id
  // The evaluation run id is the only decision input Core accepts; it is never derivable
  // from the proposal row, so the operator must supply it and the field stays empty.
  promoteForm.value = { eval_run_id: '', reason: '' }
}

function closePromotePanel() {
  showPromotePanel.value = ''
}

async function promoteCandidate(proposal: AgentEvolutionProposalDto) {
  if (!canAccessBackend.value || !promoteForm.value.eval_run_id.trim()) return
  busy.value = true
  try {
    await api.promoteAgentCandidate(proposal.id, {
      eval_run_id: promoteForm.value.eval_run_id.trim(),
      reason: promoteForm.value.reason.trim() || null,
    })
    await loadProposals()
    showPromotePanel.value = ''
    emit('changed')
    notify.success({ title: t('agentCenter.evolution.promoteTitle'), message: t('agentCenter.evolution.promoteSuccess'), source: 'evolution' })
  } catch (err) {
    notify.error(err, { title: t('agentCenter.evolution.promoteFailed', { name: proposal.name }), source: 'evolution' })
  } finally {
    busy.value = false
  }
}

async function rejectCandidate(proposal: AgentEvolutionProposalDto) {
  if (!canAccessBackend.value) return
  const reason = rejectReason.value.trim() || undefined
  if (!await confirm({
    title: t('agentCenter.evolution.rejectTitle'),
    message: reason ? t('agentCenter.evolution.rejectConfirmReason', { name: proposal.name, reason }) : t('agentCenter.evolution.rejectConfirm', { name: proposal.name }),
    confirmLabel: t('agentCenter.evolution.reject'),
    cancelLabel: t('settings.cancel'),
    destructive: true
  })) return
  busy.value = true
  try {
    await api.rejectAgentCandidate(proposal.id, reason)
    rejectReason.value = ''
    await loadProposals()
    notify.success({ message: t('agentCenter.evolution.rejectDone', { name: proposal.name }), source: 'evolution' })
  } catch (err) {
    notify.error(err, { title: t('agentCenter.evolution.rejectFailed', { name: proposal.name }), source: 'evolution' })
  } finally {
    busy.value = false
  }
}

onMounted(() => {
  if (canAccessBackend.value) void loadProposals()
})

watch(canAccessBackend, (ready) => {
  if (ready) void loadProposals()
})

defineExpose({ loadProposals })
</script>

<template>
  <section class="center-resource-section agent-evolution-panel">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.agentEvolution') }}</h3>
        <p>{{ t('agentCenter.evolution.panelHint') }}</p>
      </div>
      <UiBadge variant="outline">{{ proposals.length }}</UiBadge>
    </div>

    <div v-if="loading && proposals.length === 0" class="center-loading-state" aria-live="polite">
      <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
    </div>

    <template v-else>
      <UiCard class="evolution-generate-card">
        <template #content>
          <div class="evolution-generate-row">
            <div class="evolution-form-field">
              <UiLabel>{{ t('agentCenter.evolution.targetSession') }}</UiLabel>
              <UiInput v-model="generateSessionId" :placeholder="t('agentCenter.evolution.sessionPlaceholder')" />
            </div>
            <div class="evolution-form-field">
              <UiLabel>{{ t('agentCenter.evolution.lookbackEvents') }}</UiLabel>
              <UiInput v-model.number="generateLookback" type="number" placeholder="200" />
            </div>
            <UiButton :disabled="!canAccessBackend || busy" @click="generateProposals">
              <Dna :size="14" />
              <span>{{ t('agentCenter.evolution.generateAction') }}</span>
            </UiButton>
          </div>
        </template>
      </UiCard>

      <div v-if="!loading && proposals.length === 0" class="model-provider-empty">
        <Dna :size="24" />
        <span>{{ t('agentCenter.evolution.noProposals') }}</span>
      </div>

      <div class="evolution-proposal-grid">
        <button
          v-for="proposal in sortedProposals"
          :key="proposal.id"
          class="evolution-proposal-card"
          :class="{ active: selectedProposalId === proposal.id }"
          @click="selectedProposalId = proposal.id"
        >
          <div class="evolution-proposal-head">
            <div class="evolution-proposal-icon" :class="proposal.layer">
              <component :is="isOperationLayer(proposal.layer) ? Workflow : Cpu" :size="16" />
            </div>
            <div class="evolution-proposal-main">
              <strong>{{ proposal.name }}</strong>
              <span>{{ agentLayerLabel(proposal.layer) }} · {{ proposal.agent_type }}</span>
            </div>
            <UiBadge :variant="confidenceVariant(proposal.confidence)">
              {{ (proposal.confidence * 100).toFixed(0) }}%
            </UiBadge>
          </div>
          <p class="evolution-proposal-desc">{{ proposal.decision_reason ?? '' }}</p>
          <div class="evolution-proposal-meta">
            <UiBadge :variant="statusVariant(proposal.status)">{{ statusLabel(proposal.status) }}</UiBadge>
            <span class="evolution-proposal-by">{{ t('agentCenter.evolution.generatedBy', { id: proposal.generated_by_instance_id }) }}</span>
          </div>
        </button>
      </div>

      <UiCard v-if="selectedProposal" class="evolution-detail-panel">
        <template #content>
          <div class="evolution-detail-head">
            <div class="evolution-proposal-icon" :class="selectedProposal.layer">
              <component :is="isOperationLayer(selectedProposal.layer) ? Workflow : Cpu" :size="20" />
            </div>
            <div>
              <h3>{{ selectedProposal.name }}</h3>
              <p>{{ agentLayerLabel(selectedProposal.layer) }} · {{ selectedProposal.agent_type }} · {{ statusLabel(selectedProposal.status) }}</p>
            </div>
            <UiBadge :variant="confidenceVariant(selectedProposal.confidence)">
              {{ t('agentCenter.evolution.confidence') }} {{ (selectedProposal.confidence * 100).toFixed(0) }}%
            </UiBadge>
          </div>

          <div class="evolution-detail-section">
            <div class="evolution-detail-section-title">{{ t('agentCenter.evolution.description') }}</div>
            <p>{{ selectedProposal.decision_reason ?? '' }}</p>
          </div>

          <div v-if="selectedProposal.status === 'proposed' || selectedProposal.status === 'evaluating'" class="evolution-detail-actions">
            <UiButton :disabled="!canAccessBackend || busy" @click="openPromotePanel(selectedProposal)">
              <Check :size="14" />
              <span>{{ t('agentCenter.evolution.promote') }}</span>
            </UiButton>
            <UiInput v-model="rejectReason" :placeholder="t('agentCenter.evolution.rejectPlaceholder')" size="sm" />
            <UiButton variant="ghost" :disabled="!canAccessBackend || busy" @click="rejectCandidate(selectedProposal)">
              <ThumbsDown :size="14" />
              <span>{{ t('agentCenter.evolution.reject') }}</span>
            </UiButton>
          </div>
        </template>
      </UiCard>
    </template>

    <UiSheet :open="Boolean(showPromotePanel)" side="right" @update:open="!$event && closePromotePanel()">
      <div class="ac-sheet-body">
        <h3>{{ t('agentCenter.evolution.promoteTitle') }}</h3>
        <p class="quiet">{{ t('agentCenter.evolution.promoteSubtitle', { name: selectedProposal?.name ?? '' }) }}</p>

        <div class="evolution-form-grid">
          <div class="evolution-form-field">
            <UiLabel>{{ t('agentCenter.evolution.fieldEvalRun') }}</UiLabel>
            <UiInput v-model="promoteForm.eval_run_id" placeholder="eval_run_id" data-testid="promote-eval-run-id" />
          </div>
          <div class="evolution-form-field">
            <UiLabel>{{ t('agentCenter.evolution.fieldReason') }}</UiLabel>
            <UiInput v-model="promoteForm.reason" :placeholder="t('agentCenter.evolution.rejectPlaceholder')" />
          </div>
        </div>

        <p class="quiet evolution-eval-hint" data-testid="promote-eval-hint">
          {{ t('agentCenter.evolution.evalRequiredHint') }}
        </p>

        <div class="ac-sheet-actions">
          <UiButton variant="outline" @click="closePromotePanel">{{ t('settings.cancel') }}</UiButton>
          <UiButton :disabled="!canAccessBackend || busy || !promoteForm.eval_run_id.trim()" @click="promoteCandidate(selectedProposal!)">
            <Check :size="14" />
            <span>{{ t('agentCenter.evolution.promoteConfirm') }}</span>
          </UiButton>
        </div>
      </div>
    </UiSheet>
  </section>
</template>

<style scoped>
.agent-evolution-panel {
  display: grid;
  gap: 12px;
}
.evolution-generate-card :deep(.ui-card-content) {
  padding: 14px 16px;
}
.evolution-generate-row {
  display: flex;
  gap: 12px;
  align-items: flex-end;
  flex-wrap: wrap;
}
.evolution-form-field {
  flex: 1 1 200px;
  min-width: 180px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.evolution-proposal-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 10px;
}
.evolution-proposal-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  cursor: pointer;
  text-align: left;
  color: inherit;
  transition: border-color 0.15s, background 0.15s;
}
.evolution-proposal-card:hover {
  border-color: var(--accent-brand);
}
.evolution-proposal-card.active {
  border-color: var(--accent-brand);
  background: var(--surface-selected);
}
.evolution-proposal-head {
  display: flex;
  align-items: center;
  gap: 10px;
}
.evolution-proposal-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--accent-brand) 12%, transparent);
  color: var(--accent-brand);
}
.evolution-proposal-icon.execution {
  background: color-mix(in srgb, var(--accent-success) 12%, transparent);
  color: var(--accent-success);
}
.evolution-proposal-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.evolution-proposal-main strong {
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.evolution-proposal-main span {
  font-size: 11px;
  color: var(--text-muted);
}
.evolution-proposal-desc {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
  line-height: 1.4;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.evolution-proposal-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 11px;
}
.evolution-proposal-by {
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.evolution-detail-panel :deep(.ui-card-content) {
  padding: 18px 20px;
}
.evolution-detail-head {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 14px;
}
.evolution-detail-head h3 {
  margin: 0;
  font-size: 16px;
}
.evolution-detail-head p {
  margin: 2px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}
.evolution-detail-section {
  margin-top: 12px;
}
.evolution-detail-section-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.5px;
  margin-bottom: 6px;
}
.evolution-pattern-list {
  margin: 0;
  padding-left: 18px;
  font-size: 13px;
  line-height: 1.6;
}
.evolution-tag-row {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.evolution-tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  background: color-mix(in srgb, var(--accent-brand) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-brand) 20%, transparent);
  border-radius: 4px;
  font-size: 11px;
  font-family: var(--font-mono);
}
.evolution-tag.removable {
  background: var(--surface-raised);
  border-color: var(--border-default);
}
.evolution-tag-remove {
  background: none;
  border: none;
  color: inherit;
  cursor: pointer;
  font-size: 14px;
  line-height: 1;
  padding: 0;
}
.evolution-detail-actions {
  display: flex;
  gap: 8px;
  align-items: center;
  margin-top: 18px;
  padding-top: 14px;
  border-top: 1px solid var(--border-muted);
  flex-wrap: wrap;
}
.evolution-form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}
.evolution-eval-hint {
  margin: 0;
  font-size: 11px;
  line-height: 1.45;
}
.evolution-form-block {
  display: grid;
  gap: 6px;
}
.evolution-add-row {
  display: flex;
  gap: 6px;
}
@media (max-width: 700px) {
  .evolution-form-grid {
    grid-template-columns: 1fr;
  }
}
</style>
