<script setup lang="ts">
import { Check, ChevronRight, Cpu, Dna, Sparkles, ThumbsDown, Workflow, X } from '@lucide/vue'
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

const { t } = useI18n()

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
    proposed: 'Proposed',
    promoted: 'Promoted',
    rejected: 'Rejected',
    evaluating: 'Evaluating'
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
}

function closePromotePanel() {
  showPromotePanel.value = ''
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
    showPromotePanel.value = ''
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
        <h2><Dna :size="18" /> Agent Evolution</h2>
        <p>Heuristically generate agent candidates from observed workflow patterns.</p>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="loadProposals">
        <Sparkles :size="14" />
        <span>Refresh</span>
      </UiButton>
    </div>

    <UiCard class="evolution-generate-card">
      <template #content>
        <div class="evolution-generate-row">
          <div class="evolution-generate-field">
            <UiLabel>Session ID (optional)</UiLabel>
            <UiInput v-model="generateSessionId" placeholder="session id for pattern mining" />
          </div>
          <div class="evolution-generate-field">
            <UiLabel>Lookback Events</UiLabel>
            <UiInput v-model.number="generateLookback" type="number" placeholder="200" />
          </div>
          <UiButton :disabled="busy" @click="generateProposals">
            <Dna :size="14" />
            <span>Generate Proposals</span>
          </UiButton>
        </div>
      </template>
    </UiCard>

    <div v-if="error" class="evolution-error">
      <X :size="14" />
      <span>{{ error }}</span>
    </div>

    <div class="evolution-list-header">
      <h3>Proposals</h3>
      <UiBadge variant="outline">{{ proposals.length }}</UiBadge>
    </div>

    <p v-if="loading" class="quiet">Loading proposals…</p>
    <p v-else-if="proposals.length === 0" class="quiet">
      No evolution proposals yet. Click "Generate Proposals" to mine workflow patterns.
    </p>

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
          <span class="evolution-proposal-by">by {{ proposal.generated_by_agent_id }}</span>
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
            <h3>{{ selectedProposal.name }}</h3>
            <p>{{ agentLayerLabel(selectedProposal.layer) }} · {{ selectedProposal.agent_type }} · {{ statusLabel(selectedProposal.status) }}</p>
          </div>
          <UiBadge :variant="confidenceVariant(selectedProposal.confidence_score)">
            Confidence {{ (selectedProposal.confidence_score * 100).toFixed(0) }}%
          </UiBadge>
        </div>

        <div class="evolution-detail-section">
          <div class="evolution-detail-section-title">Description</div>
          <p>{{ selectedProposal.description }}</p>
        </div>

        <div v-if="selectedProposal.observed_patterns.length > 0" class="evolution-detail-section">
          <div class="evolution-detail-section-title">Observed Patterns</div>
          <ul class="evolution-pattern-list">
            <li v-for="pattern in selectedProposal.observed_patterns" :key="pattern">{{ pattern }}</li>
          </ul>
        </div>

        <div v-if="selectedProposal.suggested_tools.length > 0" class="evolution-detail-section">
          <div class="evolution-detail-section-title">Suggested Tools</div>
          <div class="evolution-tag-row">
            <span v-for="tool in selectedProposal.suggested_tools" :key="tool" class="evolution-tag">{{ tool }}</span>
          </div>
        </div>

        <div v-if="selectedProposal.evaluation_notes.length > 0" class="evolution-detail-section">
          <div class="evolution-detail-section-title">Evaluation Notes</div>
          <ul class="evolution-pattern-list">
            <li v-for="note in selectedProposal.evaluation_notes" :key="note">{{ note }}</li>
          </ul>
        </div>

        <div v-if="selectedProposal.status === 'proposed' || selectedProposal.status === 'evaluating'" class="evolution-detail-actions">
          <UiButton :disabled="busy" @click="openPromotePanel(selectedProposal)">
            <Check :size="14" />
            <span>Promote to Agent</span>
          </UiButton>
          <template v-if="confirmRejectId !== selectedProposal.id">
            <UiButton variant="ghost" :disabled="busy" @click="confirmRejectId = selectedProposal.id">
              <ThumbsDown :size="14" />
              <span>Reject</span>
            </UiButton>
          </template>
          <template v-else>
            <UiInput v-model="rejectReason" placeholder="rejection reason (optional)" size="sm" />
            <UiButton variant="destructive" size="sm" :disabled="busy" @click="rejectCandidate(selectedProposal)">
              Confirm Reject
            </UiButton>
            <UiButton variant="ghost" size="sm" @click="confirmRejectId = ''">Cancel</UiButton>
          </template>
        </div>
      </template>
    </UiCard>

    <Transition name="modal-fade">
      <div v-if="showPromotePanel" class="evolution-promote-modal" @click.self="closePromotePanel">
        <UiCard class="evolution-promote-modal-content">
          <template #header>
            <div class="evolution-modal-header">
              <h3>Promote Candidate</h3>
              <UiButton variant="ghost" size="icon" @click="closePromotePanel">
                <X :size="16" />
              </UiButton>
            </div>
          </template>

          <template #content>
            <p class="evolution-modal-subtitle">
              Promote <strong>{{ selectedProposal?.name }}</strong> to a full agent profile.
            </p>

            <div class="evolution-form-grid">
              <div class="settings-field">
                <UiLabel>Agent ID</UiLabel>
                <UiInput v-model="promoteForm.agent_id" placeholder="agent_xxx" />
              </div>
              <div class="settings-field">
                <UiLabel>Mode</UiLabel>
                <select v-model="promoteForm.mode" class="settings-select">
                  <option v-for="mode in agentModes" :key="mode.id" :value="mode.id">
                    {{ mode.display_name }} · {{ mode.summary }}
                  </option>
                </select>
              </div>
              <div class="settings-field">
                <UiLabel>Model Route Purpose</UiLabel>
                <UiInput v-model="promoteForm.model_route_purpose" placeholder="chat / planner / executor / reviewer" />
              </div>
            </div>

            <div class="evolution-modal-section">
              <UiLabel>Allowed Tools</UiLabel>
              <div class="evolution-tag-list">
                <span v-for="tool in promoteForm.allowed_tools" :key="tool" class="evolution-tag removable">
                  {{ tool }}
                  <button class="evolution-tag-remove" @click="removePromoteTool(tool)">×</button>
                </span>
              </div>
              <div class="evolution-add-row">
                <UiInput v-model="promoteToolInput" placeholder="tool id" size="sm" @keydown.enter="addPromoteTool" />
                <UiButton variant="outline" size="sm" :disabled="!promoteToolInput.trim()" @click="addPromoteTool">Add</UiButton>
              </div>
            </div>

            <div class="evolution-modal-section">
              <UiLabel>Capabilities</UiLabel>
              <div class="evolution-tag-list">
                <span v-for="cap in promoteForm.capabilities" :key="cap" class="evolution-tag removable">
                  {{ cap }}
                  <button class="evolution-tag-remove" @click="removePromoteCapability(cap)">×</button>
                </span>
              </div>
              <div class="evolution-add-row">
                <UiInput v-model="promoteCapabilityInput" placeholder="capability" size="sm" @keydown.enter="addPromoteCapability" />
                <UiButton variant="outline" size="sm" :disabled="!promoteCapabilityInput.trim()" @click="addPromoteCapability">Add</UiButton>
              </div>
            </div>

            <div class="evolution-modal-section">
              <UiLabel>System Prompt (optional)</UiLabel>
              <textarea
                v-model="promoteForm.system_prompt"
                class="settings-textarea"
                rows="4"
                placeholder="Custom system prompt override"
              ></textarea>
            </div>
          </template>

          <template #footer>
            <div class="modal-actions">
              <UiButton variant="outline" @click="closePromotePanel">Cancel</UiButton>
              <UiButton :disabled="busy || !promoteForm.agent_id.trim()" @click="promoteCandidate(selectedProposal!)">
                <Check :size="14" />
                <span>Promote Agent</span>
              </UiButton>
            </div>
          </template>
        </UiCard>
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
.evolution-header h2 {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 0 0 4px;
  font-size: 18px;
}
.evolution-header p {
  margin: 0;
  color: var(--text-muted, #888);
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
.evolution-error {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: rgba(255, 99, 99, 0.1);
  border: 1px solid rgba(255, 99, 99, 0.3);
  border-radius: 6px;
  color: var(--accent-danger, #ff6363);
  font-size: 13px;
}
.evolution-list-header {
  display: flex;
  align-items: center;
  gap: 8px;
}
.evolution-list-header h3 {
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
  background: var(--bg-elevated, rgba(255, 255, 255, 0.04));
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.08));
  border-radius: 8px;
  cursor: pointer;
  text-align: left;
  color: inherit;
  transition: border-color 0.15s, background 0.15s;
}
.evolution-proposal-card:hover {
  border-color: var(--accent-primary, #58a6ff);
}
.evolution-proposal-card.active {
  border-color: var(--accent-primary, #58a6ff);
  background: rgba(88, 166, 255, 0.08);
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
  background: rgba(88, 166, 255, 0.12);
  color: var(--accent-primary, #58a6ff);
}
.evolution-proposal-icon.execution {
  background: rgba(46, 196, 182, 0.12);
  color: var(--accent-success, #2ec4b6);
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
  color: var(--text-muted, #888);
}
.evolution-proposal-desc {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted, #aaa);
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
  color: var(--text-muted, #888);
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
  color: var(--text-muted, #888);
}
.evolution-detail-section {
  margin-top: 12px;
}
.evolution-detail-section-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-muted, #aaa);
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
  background: rgba(88, 166, 255, 0.1);
  border: 1px solid rgba(88, 166, 255, 0.2);
  border-radius: 4px;
  font-size: 11px;
  font-family: var(--font-mono, monospace);
}
.evolution-tag.removable {
  background: rgba(255, 255, 255, 0.06);
  border-color: rgba(255, 255, 255, 0.12);
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
  border-top: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.08));
  flex-wrap: wrap;
}
.evolution-promote-modal {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
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
.evolution-modal-header h3 {
  margin: 0;
  font-size: 16px;
}
.evolution-modal-subtitle {
  margin: 0 0 14px;
  font-size: 13px;
  color: var(--text-muted, #aaa);
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
  color: var(--text-muted, #888);
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
  background: var(--bg-input, rgba(255, 255, 255, 0.04));
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.12));
  border-radius: 6px;
  color: inherit;
  font-size: 13px;
}
.settings-textarea {
  width: 100%;
  padding: 8px 10px;
  background: var(--bg-input, rgba(255, 255, 255, 0.04));
  border: 1px solid var(--border-subtle, rgba(255, 255, 255, 0.12));
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
</style>
