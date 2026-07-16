<script setup lang="ts">
import { Check, ChevronRight, Cpu, Dna, Info, Sparkles, ThumbsDown, Workflow, X } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  api,
  type AgentEvolutionProposalDto,
  type AgentModeDto,
  type AgentProfileDto,
  type PromoteAgentCandidateInput
} from '../api'
import { UiBadge, UiButton, UiCard, UiInput, UiLabel } from '@/components/ui'
import { useDialogFocus } from '@/composables/useDialogFocus'
import { useSettingsLabels } from '@/pages/settings/settingsLabels'

const { t } = useI18n()
const { settingsErrorLabel } = useSettingsLabels()

const proposals = ref<AgentEvolutionProposalDto[]>([])
const agents = ref<AgentProfileDto[]>([])
const agentModes = ref<AgentModeDto[]>([])
const loading = ref(false)
const busy = ref(false)
const error = ref<string | null>(null)
const selectedProposalId = ref('')
const showPromotePanel = ref('')
const rejectReason = ref('')
const confirmRejectId = ref('')
const generateSessionId = ref('')
const generateLookback = ref('200')

// Promote form state
const promoteForm = ref<PromoteAgentCandidateInput>({
  agent_id: '',
  mode: 'plan',
  model_route_purpose: 'chat',
  allowed_tools: [],
  capabilities: [],
  system_prompt: null
})
const promoteToolInput = ref('')
const promoteCapabilityInput = ref('')
const {
  closeDialog: closePromoteDialog,
  dialogRef: promoteDialogRef,
  onDialogKeydown: onPromoteDialogKeydown,
  openDialog: focusPromoteDialog
} = useDialogFocus(() => { showPromotePanel.value = '' })

const selectedProposal = computed(() =>
  proposals.value.find((p) => p.id === selectedProposalId.value) ?? null
)

const sortedProposals = computed(() =>
  [...proposals.value].sort((a, b) => b.confidence_score - a.confidence_score)
)

function agentLayerLabel(layer: string): string {
  if (layer === 'planning') return t('settings.agentLayerPlanning')
  if (layer === 'execution') return t('settings.agentLayerExecution')
  if (layer === 'evolution') return t('settings.agentLayerEvolution')
  return layer
}

function confidenceVariant(score: number): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (score >= 0.7) return 'default'
  if (score >= 0.4) return 'outline'
  return 'secondary'
}

function statusLabel(status: string): string {
  const map: Record<string, string> = {
    proposed: t('settings.evolutionStatusProposed'),
    promoted: t('settings.evolutionStatusPromoted'),
    rejected: t('settings.evolutionStatusRejected'),
    evaluating: t('settings.evolutionStatusEvaluating')
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
  loading.value = true
  error.value = null
  try {
    const [proposalList, agentList, modes] = await Promise.all([
      api.listEvolutionProposals(),
      api.listAgents(),
      api.listAgentModes()
    ])
    proposals.value = proposalList
    agents.value = agentList
    agentModes.value = modes
    if (!selectedProposalId.value && proposalList.length > 0) {
      selectedProposalId.value = proposalList[0].id
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}

async function generateProposals() {
  busy.value = true
  error.value = null
  try {
    const params: { session_id?: string; lookback_event_count?: number } = {}
    if (generateSessionId.value.trim()) params.session_id = generateSessionId.value.trim()
    const lookback = Number(generateLookback.value)
    if (lookback > 0) params.lookback_event_count = lookback
    const generated = await api.generateEvolutionProposals(params)
    proposals.value = generated
    if (generated.length > 0) {
      selectedProposalId.value = generated[0].id
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

function openPromotePanel(proposal: AgentEvolutionProposalDto) {
  showPromotePanel.value = proposal.id
  // Pre-fill form with proposal suggestions
  const baseAgentId = `agent_${proposal.agent_type}_${Date.now().toString(36)}`
  promoteForm.value = {
    agent_id: baseAgentId,
    mode: proposal.layer === 'planning' ? 'plan' : 'execute',
    model_route_purpose: proposal.layer === 'planning' ? 'planner' : 'chat',
    allowed_tools: [...proposal.suggested_tools],
    capabilities: [],
    system_prompt: null
  }
  promoteToolInput.value = ''
  promoteCapabilityInput.value = ''
  focusPromoteDialog()
}

function closePromotePanel() {
  closePromoteDialog()
}

function addPromoteTool() {
  const tool = promoteToolInput.value.trim()
  if (tool && !promoteForm.value.allowed_tools.includes(tool)) {
    promoteForm.value.allowed_tools.push(tool)
    promoteToolInput.value = ''
  }
}

function removePromoteTool(tool: string) {
  const idx = promoteForm.value.allowed_tools.indexOf(tool)
  if (idx >= 0) promoteForm.value.allowed_tools.splice(idx, 1)
}

function addPromoteCapability() {
  const cap = promoteCapabilityInput.value.trim()
  if (cap && !promoteForm.value.capabilities.includes(cap)) {
    promoteForm.value.capabilities.push(cap)
    promoteCapabilityInput.value = ''
  }
}

function removePromoteCapability(cap: string) {
  const idx = promoteForm.value.capabilities.indexOf(cap)
  if (idx >= 0) promoteForm.value.capabilities.splice(idx, 1)
}

async function promoteCandidate(proposal: AgentEvolutionProposalDto) {
  if (!promoteForm.value.agent_id.trim()) return
  busy.value = true
  error.value = null
  try {
    await api.promoteAgentCandidate(proposal.id, promoteForm.value)
    await loadProposals()
    closePromotePanel()
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

async function rejectCandidate(proposal: AgentEvolutionProposalDto) {
  busy.value = true
  error.value = null
  try {
    await api.rejectAgentCandidate(proposal.id, rejectReason.value.trim() || undefined)
    rejectReason.value = ''
    confirmRejectId.value = ''
    await loadProposals()
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    busy.value = false
  }
}

onMounted(() => {
  void loadProposals()
})
</script>

<template>
  <section class="agent-evolution-panel">
    <div class="evolution-header">
      <div>
        <h1><Dna :size="18" /> {{ t('settings.evolutionTitle') }}</h1>
        <p>{{ t('settings.evolutionSubtitle') }}</p>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="loadProposals">
        <Sparkles :size="14" />
        <span>{{ t('settings.refresh') }}</span>
      </UiButton>
    </div>

    <UiCard v-if="!error || proposals.length > 0" class="evolution-generate-card">
      <template #content>
        <div class="evolution-generate-row">
          <div class="evolution-generate-field">
            <UiLabel>{{ t('settings.evolutionSessionId') }}</UiLabel>
            <UiInput v-model="generateSessionId" :placeholder="t('settings.evolutionSessionPlaceholder')" />
          </div>
          <div class="evolution-generate-field">
            <UiLabel>{{ t('settings.evolutionLookback') }}</UiLabel>
            <UiInput v-model.number="generateLookback" type="number" placeholder="200" />
          </div>
          <UiButton :disabled="busy" @click="generateProposals">
            <Dna :size="14" />
            <span>{{ t('settings.evolutionGenerate') }}</span>
          </UiButton>
        </div>
      </template>
    </UiCard>

    <div v-if="error" class="center-message error">
      <Info :size="16" />
      <span>{{ settingsErrorLabel(error) }}</span>
      <UiButton variant="outline" size="sm" @click="loadProposals">{{ t('settings.retry') }}</UiButton>
    </div>

    <div v-if="!error || proposals.length > 0" class="evolution-list-header">
      <h2>{{ t('settings.evolutionProposals') }}</h2>
      <UiBadge variant="outline">{{ proposals.length }}</UiBadge>
    </div>

    <p v-if="loading" class="quiet">{{ t('settings.evolutionLoading') }}</p>
    <p v-else-if="!error && proposals.length === 0" class="quiet">{{ t('settings.evolutionEmpty') }}</p>

    <div v-if="!error || proposals.length > 0" class="evolution-proposal-grid">
      <button
        v-for="proposal in sortedProposals"
        :key="proposal.id"
        class="evolution-proposal-card"
        :class="{ active: selectedProposalId === proposal.id }"
        @click="selectedProposalId = proposal.id"
      >
        <div class="evolution-proposal-head">
          <div class="evolution-proposal-icon" :class="proposal.layer">
            <component :is="proposal.layer === 'planning' ? Workflow : Cpu" :size="16" />
          </div>
          <div class="evolution-proposal-main">
            <strong>{{ proposal.name }}</strong>
            <span>{{ agentLayerLabel(proposal.layer) }} · {{ proposal.agent_type }}</span>
          </div>
          <UiBadge :variant="confidenceVariant(proposal.confidence_score)">
            {{ (proposal.confidence_score * 100).toFixed(0) }}%
          </UiBadge>
        </div>
        <p class="evolution-proposal-desc">{{ proposal.description }}</p>
        <div class="evolution-proposal-meta">
          <UiBadge :variant="statusVariant(proposal.status)">{{ statusLabel(proposal.status) }}</UiBadge>
          <span class="evolution-proposal-by">{{ t('settings.evolutionBy', { agent: proposal.generated_by_agent_id }) }}</span>
        </div>
      </button>
    </div>

    <UiCard v-if="selectedProposal" class="evolution-detail-panel">
      <template #content>
        <div class="evolution-detail-head">
          <div class="evolution-proposal-icon" :class="selectedProposal.layer">
            <component :is="selectedProposal.layer === 'planning' ? Workflow : Cpu" :size="20" />
          </div>
          <div>
            <h2>{{ selectedProposal.name }}</h2>
            <p>{{ agentLayerLabel(selectedProposal.layer) }} · {{ selectedProposal.agent_type }} · {{ statusLabel(selectedProposal.status) }}</p>
          </div>
          <UiBadge :variant="confidenceVariant(selectedProposal.confidence_score)">
            {{ t('settings.evolutionConfidence', { score: (selectedProposal.confidence_score * 100).toFixed(0) }) }}
          </UiBadge>
        </div>

        <div class="evolution-detail-section">
          <div class="evolution-detail-section-title">{{ t('settings.evolutionDescription') }}</div>
          <p>{{ selectedProposal.description }}</p>
        </div>

        <div v-if="selectedProposal.observed_patterns.length > 0" class="evolution-detail-section">
          <div class="evolution-detail-section-title">{{ t('settings.evolutionPatterns') }}</div>
          <ul class="evolution-pattern-list">
            <li v-for="pattern in selectedProposal.observed_patterns" :key="pattern">{{ pattern }}</li>
          </ul>
        </div>

        <div v-if="selectedProposal.suggested_tools.length > 0" class="evolution-detail-section">
          <div class="evolution-detail-section-title">{{ t('settings.evolutionSuggestedTools') }}</div>
          <div class="evolution-tag-row">
            <span v-for="tool in selectedProposal.suggested_tools" :key="tool" class="evolution-tag">{{ tool }}</span>
          </div>
        </div>

        <div v-if="selectedProposal.evaluation_notes.length > 0" class="evolution-detail-section">
          <div class="evolution-detail-section-title">{{ t('settings.evolutionEvaluationNotes') }}</div>
          <ul class="evolution-pattern-list">
            <li v-for="note in selectedProposal.evaluation_notes" :key="note">{{ note }}</li>
          </ul>
        </div>

        <div v-if="selectedProposal.status === 'proposed' || selectedProposal.status === 'evaluating'" class="evolution-detail-actions">
          <UiButton :disabled="busy" @click="openPromotePanel(selectedProposal)">
            <Check :size="14" />
            <span>{{ t('settings.evolutionPromote') }}</span>
          </UiButton>
          <template v-if="confirmRejectId !== selectedProposal.id">
            <UiButton variant="ghost" :disabled="busy" @click="confirmRejectId = selectedProposal.id">
              <ThumbsDown :size="14" />
              <span>{{ t('settings.evolutionReject') }}</span>
            </UiButton>
          </template>
          <template v-else>
            <UiInput v-model="rejectReason" :placeholder="t('settings.evolutionRejectReason')" size="sm" />
            <UiButton variant="destructive" size="sm" :disabled="busy" @click="rejectCandidate(selectedProposal)">
              {{ t('settings.evolutionConfirmReject') }}
            </UiButton>
            <UiButton variant="ghost" size="sm" @click="confirmRejectId = ''">{{ t('settings.cancel') }}</UiButton>
          </template>
        </div>
      </template>
    </UiCard>

    <Transition name="modal-fade">
      <div v-if="showPromotePanel" class="evolution-promote-modal" @click.self="closePromotePanel">
        <div ref="promoteDialogRef" role="dialog" aria-modal="true" aria-labelledby="evolution-promote-title" tabindex="-1" @keydown="onPromoteDialogKeydown">
        <UiCard class="evolution-promote-modal-content">
          <template #header>
            <div class="evolution-modal-header">
              <h2 id="evolution-promote-title">{{ t('settings.evolutionPromoteCandidate') }}</h2>
              <UiButton variant="ghost" size="icon" :aria-label="t('app.close')" @click="closePromotePanel">
                <X :size="16" />
              </UiButton>
            </div>
          </template>

          <template #content>
            <p class="evolution-modal-subtitle">
              {{ t('settings.evolutionPromoteDescription', { name: selectedProposal?.name }) }}
            </p>

            <div class="evolution-form-grid">
              <div class="settings-field">
                <UiLabel>{{ t('settings.evolutionAgentId') }}</UiLabel>
                <UiInput v-model="promoteForm.agent_id" placeholder="agent_xxx" />
              </div>
              <div class="settings-field">
                <UiLabel>{{ t('settings.evolutionMode') }}</UiLabel>
                <select v-model="promoteForm.mode" class="settings-select">
                  <option v-for="mode in agentModes" :key="mode.id" :value="mode.id">
                    {{ mode.display_name }} · {{ mode.summary }}
                  </option>
                </select>
              </div>
              <div class="settings-field">
                <UiLabel>{{ t('settings.evolutionRoutePurpose') }}</UiLabel>
                <UiInput v-model="promoteForm.model_route_purpose" placeholder="chat / planner / executor / reviewer" />
              </div>
            </div>

            <div class="evolution-modal-section">
              <UiLabel>{{ t('settings.evolutionAllowedTools') }}</UiLabel>
              <div class="evolution-tag-list">
                <span v-for="tool in promoteForm.allowed_tools" :key="tool" class="evolution-tag removable">
                  {{ tool }}
                  <button class="evolution-tag-remove" @click="removePromoteTool(tool)">×</button>
                </span>
              </div>
              <div class="evolution-add-row">
                <UiInput v-model="promoteToolInput" :placeholder="t('settings.evolutionToolId')" size="sm" @keydown.enter="addPromoteTool" />
                <UiButton variant="outline" size="sm" :disabled="!promoteToolInput.trim()" @click="addPromoteTool">{{ t('settings.evolutionAdd') }}</UiButton>
              </div>
            </div>

            <div class="evolution-modal-section">
              <UiLabel>{{ t('settings.capabilities') }}</UiLabel>
              <div class="evolution-tag-list">
                <span v-for="cap in promoteForm.capabilities" :key="cap" class="evolution-tag removable">
                  {{ cap }}
                  <button class="evolution-tag-remove" @click="removePromoteCapability(cap)">×</button>
                </span>
              </div>
              <div class="evolution-add-row">
                <UiInput v-model="promoteCapabilityInput" :placeholder="t('settings.capabilities')" size="sm" @keydown.enter="addPromoteCapability" />
                <UiButton variant="outline" size="sm" :disabled="!promoteCapabilityInput.trim()" @click="addPromoteCapability">{{ t('settings.evolutionAdd') }}</UiButton>
              </div>
            </div>

            <div class="evolution-modal-section">
              <UiLabel>{{ t('settings.evolutionSystemPrompt') }}</UiLabel>
              <textarea
                v-model="promoteForm.system_prompt"
                class="settings-textarea"
                rows="4"
                :placeholder="t('settings.evolutionSystemPromptPlaceholder')"
              ></textarea>
            </div>
          </template>

          <template #footer>
            <div class="modal-actions">
              <UiButton variant="outline" @click="closePromotePanel">{{ t('settings.cancel') }}</UiButton>
              <UiButton :disabled="busy || !promoteForm.agent_id.trim()" @click="promoteCandidate(selectedProposal!)">
                <Check :size="14" />
                <span>{{ t('settings.evolutionPromoteAgent') }}</span>
              </UiButton>
            </div>
          </template>
        </UiCard>
        </div>
      </div>
    </Transition>
  </section>
</template>

<style scoped>
.agent-evolution-panel {
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.evolution-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
}
.evolution-header h1 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 4px;
  font-size: 24px;
  font-weight: 600;
  line-height: 1.2;
  letter-spacing: 0;
}
.evolution-header p {
  margin: 0;
  color: var(--text-muted);
  font-size: 13px;
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
.evolution-generate-field {
  flex: 1 1 200px;
  min-width: 180px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.evolution-list-header {
  display: flex;
  align-items: center;
  gap: 8px;
}
.evolution-list-header h2 {
  margin: 0;
  font-size: 14px;
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
  background: var(--bg-tertiary);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  cursor: pointer;
  text-align: left;
  color: inherit;
  transition: border-color 0.15s, background 0.15s;
}
.evolution-proposal-card:hover {
  border-color: var(--accent-primary);
}
.evolution-proposal-card.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 8%, transparent);
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
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
  color: var(--accent-primary);
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
.evolution-detail-head h2 {
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
  background: color-mix(in srgb, var(--accent-primary) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-primary) 20%, transparent);
  border-radius: 4px;
  font-size: 11px;
  font-family: var(--font-mono, monospace);
}
.evolution-tag.removable {
  background: color-mix(in srgb, var(--text-primary) 6%, transparent);
  border-color: color-mix(in srgb, var(--text-primary) 12%, transparent);
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
.evolution-promote-modal {
  position: fixed;
  inset: 0;
  background: color-mix(in srgb, var(--bg-primary) 70%, transparent);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 100;
  padding: 20px;
}
.evolution-promote-modal-content {
  width: 100%;
  max-width: 560px;
  max-height: 90vh;
  overflow-y: auto;
}
.evolution-modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.evolution-modal-header h2 {
  margin: 0;
  font-size: 16px;
}
.evolution-modal-subtitle {
  margin: 0 0 14px;
  font-size: 13px;
  color: var(--text-muted);
}
.evolution-form-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
  margin-bottom: 14px;
}
.evolution-modal-section {
  margin-bottom: 14px;
}
.evolution-tag-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 8px;
  min-height: 24px;
}
.evolution-add-row {
  display: flex;
  gap: 6px;
}
.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
.quiet {
  color: var(--text-muted);
  font-size: 13px;
}
.settings-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.settings-select {
  height: 32px;
  padding: 0 8px;
  background: var(--bg-input);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  color: inherit;
  font-size: 13px;
}
.settings-textarea {
  width: 100%;
  padding: 8px 10px;
  background: var(--bg-input);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  color: inherit;
  font-size: 13px;
  font-family: var(--font-mono, monospace);
  resize: vertical;
}
.modal-fade-enter-active,
.modal-fade-leave-active {
  transition: opacity 0.18s ease;
}
.modal-fade-enter-from,
.modal-fade-leave-to {
  opacity: 0;
}
@media (prefers-reduced-motion: reduce) {
  .modal-fade-enter-active,
  .modal-fade-leave-active {
    transition: none;
  }
}
</style>
