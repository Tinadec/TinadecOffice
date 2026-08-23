<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { History, Plus, RefreshCw, Search, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSheet } from '@/components/ui'
import AgentModeCanvas from '@/components/canvas/AgentModeCanvas.vue'
import GovernanceRolesPanel from '@/components/agentCenter/GovernanceRolesPanel.vue'
import { api, type AgentDefinitionDto, type AgentModeEdgeDto, type AgentModeNodeDto, type AgentModeTopologyDto, type ModeVersionDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'

const { t } = useI18n()
const { notify } = useNotifications()

const modes = ref<AgentModeTopologyDto[]>([])
const selectedModeId = ref('')
const modeQuery = ref('')
const modeNodes = ref<AgentModeNodeDto[]>([])
const modeEdges = ref<AgentModeEdgeDto[]>([])
const modeEtag = ref<string | null>(null)
const modeVersions = ref<ModeVersionDto[]>([])
const modeVersionsLoading = ref(false)
const showVersionDrawer = ref(false)
const modeBusy = ref(false)

const selectedModeNode = ref<AgentModeNodeDto | null>(null)
const selectedEdge = ref<AgentModeEdgeDto | null>(null)
const edgeLabelDraft = ref('')
const nodeOverride = ref({ agent_id: '', label: '' })

const agents = ref<AgentDefinitionDto[]>([])

const filteredModes = computed(() => {
  const q = modeQuery.value.trim().toLowerCase()
  if (!q) return modes.value
  return modes.value.filter((m) => `${m.display_name} ${m.summary ?? ''}`.toLowerCase().includes(q))
})
const selectedMode = computed(() => modes.value.find((m) => m.id === selectedModeId.value) ?? null)

function normalizeLayer(layer?: string): 'operation' | 'execution' {
  return layer === 'execution' ? 'execution' : 'operation'
}
function readinessVariant(status?: string): 'default' | 'secondary' | 'outline' {
  if (status === 'published') return 'default'
  if (status === 'archived') return 'secondary'
  return 'outline'
}

async function loadModes() {
  try {
    const list = await api.listAgentModeTopologies()
    modes.value = Array.isArray(list) ? list : []
    if (!selectedModeId.value && modes.value[0]) void selectMode(modes.value[0].id)
  } catch (e) {
    notify.error(e, { title: t('agentCenter.loadModesFailed') })
  }
}

async function loadAgents() {
  try {
    const definitions = await api.listAgentDefinitions()
    agents.value = Array.isArray(definitions) ? definitions : []
  } catch {
    agents.value = []
  }
}

async function selectMode(id: string) {
  selectedModeId.value = id
  try {
    const m = await api.getAgentModeTopology(id)
    modeNodes.value = (m.nodes ?? []) as AgentModeNodeDto[]
    modeEdges.value = (m.edges ?? []) as AgentModeEdgeDto[]
    modeEtag.value = m.revision != null ? String(m.revision) : null
  } catch {
    modeNodes.value = []
    modeEdges.value = []
    modeEtag.value = null
  }
}

async function createMode() {
  modeBusy.value = true
  try {
    const m = await api.createAgentModeDraft({
      display_name: `mode-${Date.now().toString(36).slice(0, 6)}`,
      summary: 'draft',
      nodes: [],
      edges: [],
      canvas_layout: {}
    })
    await loadModes()
    selectedModeId.value = m.id
    notify.success({ message: t('agentCenter.modeCreated', { name: m.display_name }) })
  } catch (e) {
    notify.error(e)
  } finally {
    modeBusy.value = false
  }
}

async function saveModeDraft() {
  if (!selectedModeId.value) return
  modeBusy.value = true
  try {
    await api.updateAgentModeDraft(selectedModeId.value, { nodes: modeNodes.value, edges: modeEdges.value, canvas_layout: {} }, modeEtag.value)
    notify.success({ message: t('agentCenter.modeDraftSaved') })
    await loadModes()
  } catch (e) {
    notify.error(e, { title: t('agentCenter.modeSaveFailed') })
  } finally {
    modeBusy.value = false
  }
}

async function publishMode() {
  if (!selectedModeId.value) return
  modeBusy.value = true
  try {
    await api.publishAgentMode(selectedModeId.value)
    notify.success({ message: t('agentCenter.modePublished') })
    await loadModes()
  } catch (e) {
    notify.error(e)
  } finally {
    modeBusy.value = false
  }
}

function addModeNode() {
  const id = `n-${Date.now().toString(36)}`
  const firstAgent = agents.value[0]?.id ?? 'meeting'
  modeNodes.value = [
    ...modeNodes.value,
    { id, agent_id: firstAgent, lane: 'operation', position: { x: 80 + modeNodes.value.length * 40, y: 80 }, label: `node ${modeNodes.value.length + 1}` }
  ]
}

function handleSelectNode(n: AgentModeNodeDto | null) {
  selectedModeNode.value = n
  selectedEdge.value = null
  if (n) nodeOverride.value = { agent_id: n.agent_id, label: n.label ?? '' }
}

function handleSelectEdge(e: AgentModeEdgeDto | null) {
  selectedEdge.value = e
  selectedModeNode.value = null
  edgeLabelDraft.value = e?.label ?? ''
}

function applyNodeOverride() {
  if (!selectedModeNode.value) return
  modeNodes.value = modeNodes.value.map((n) =>
    n.id === selectedModeNode.value!.id
      ? { ...n, agent_id: nodeOverride.value.agent_id || n.agent_id, label: nodeOverride.value.label || n.label }
      : n
  )
  selectedModeNode.value = null
}

function saveEdgeLabel() {
  if (!selectedEdge.value) return
  modeEdges.value = modeEdges.value.map((e) =>
    e.id === selectedEdge.value!.id ? { ...e, label: edgeLabelDraft.value || undefined } : e
  )
  selectedEdge.value = { ...selectedEdge.value, label: edgeLabelDraft.value || undefined }
}

function deleteEdge() {
  if (!selectedEdge.value) return
  modeEdges.value = modeEdges.value.filter((e) => e.id !== selectedEdge.value!.id)
  selectedEdge.value = null
}

async function openVersionDrawer() {
  showVersionDrawer.value = true
  await loadModeVersions()
}

async function loadModeVersions() {
  if (!selectedModeId.value) return
  modeVersionsLoading.value = true
  try {
    modeVersions.value = await api.listAgentModeVersions(selectedModeId.value)
  } catch {
    modeVersions.value = []
  } finally {
    modeVersionsLoading.value = false
  }
}

onMounted(() => {
  void loadAgents()
  void loadModes()
})

onBeforeUnmount(() => { /* no timers held */ })

defineExpose({ loadModes })
</script>

<template>
  <section class="center-resource-section agent-modes-panel">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('agentCenter.tabs.topology') }}</h3>
        <p>{{ t('settings.agentModesHint') }}</p>
      </div>
      <UiBadge variant="outline">{{ modeNodes.length }} · {{ modeEdges.length }}</UiBadge>
    </div>

    <div class="ac-mode-actions">
      <UiButton size="sm" :disabled="modeBusy" @click="createMode"><Plus :size="14" /><span>{{ t('agentCenter.createMode') }}</span></UiButton>
      <UiButton size="sm" variant="outline" :disabled="!selectedModeId || modeBusy" @click="saveModeDraft">{{ t('settings.saveDraftMode') }}</UiButton>
      <UiButton size="sm" variant="outline" :disabled="!selectedModeId || modeBusy" @click="publishMode">{{ t('settings.publishModeAction') }}</UiButton>
      <UiButton size="sm" variant="ghost" :disabled="!selectedModeId" @click="addModeNode"><Plus :size="14" />{{ t('settings.addNodeAction') }}</UiButton>
      <UiButton size="sm" variant="outline" :disabled="!selectedModeId" @click="openVersionDrawer"><History :size="14" />{{ t('agentCenter.versionHistory') }}</UiButton>
    </div>

    <div class="center-workbench workbench-duo">
      <aside class="center-resource-rail ac-mode-rail">
        <div class="model-provider-search">
          <Search :size="14" />
          <UiInput v-model="modeQuery" :placeholder="t('agentCenter.filter.searchModes')" />
        </div>
        <div class="ac-mode-tabs" role="tablist" :aria-label="t('settings.agentModes')">
          <button
            v-for="m in filteredModes"
            :key="m.id"
            role="tab"
            :aria-selected="selectedModeId === m.id"
            :class="{ active: selectedModeId === m.id }"
            class="ac-mode-row"
            @click="selectMode(m.id)"
          >
            <span class="ac-mode-meta">
              <strong>{{ m.display_name }}</strong>
              <small>rev {{ m.revision ?? '—' }}</small>
            </span>
            <UiBadge :variant="readinessVariant(m.status)">{{ m.status ?? 'draft' }}</UiBadge>
          </button>
          <div v-if="filteredModes.length === 0" class="quiet ac-mode-empty">{{ t('agentCenter.empty.noModes') }}</div>
        </div>

        <!-- Workspace defaults / governance roles -->
        <GovernanceRolesPanel :manifest-tool-ids="[]" />
      </aside>

      <main class="center-resource-stage ac-mode-canvas-col">
        <AgentModeCanvas
          :nodes="modeNodes"
          :edges="modeEdges"
          :agents="agents"
          @update:nodes="modeNodes = $event"
          @update:edges="modeEdges = $event"
          @select-node="handleSelectNode"
          @select-edge="handleSelectEdge"
        />
        <div v-if="selectedEdge" class="ac-edge-bar">
          <span>{{ selectedEdge.source }} → {{ selectedEdge.target }}</span>
          <UiInput v-model="edgeLabelDraft" :placeholder="t('settings.edgeLabelPlaceholder')" class="ac-edge-label-input" />
          <UiButton size="sm" variant="outline" @click="saveEdgeLabel">{{ t('settings.saveLabelAction') }}</UiButton>
          <UiButton size="sm" variant="ghost" class="provider-delete-btn" @click="deleteEdge"><Trash2 :size="14" />{{ t('settings.deleteAction') }}</UiButton>
        </div>
      </main>
    </div>

    <UiSheet :open="Boolean(selectedModeNode)" side="right" @update:open="!$event && (selectedModeNode = null)">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.nodeConfigTitle', { id: selectedModeNode?.id ?? '—' }) }}</h3>
        <p class="quiet">{{ t('settings.nodeConfigHint') }}</p>
        <div>
          <UiLabel>{{ t('settings.boundAgent') }}</UiLabel>
          <select v-model="nodeOverride.agent_id" class="settings-select">
            <option v-for="a in agents" :key="a.id" :value="a.id">{{ a.display_name ?? a.name }} ({{ normalizeLayer(a.layer) }})</option>
          </select>
        </div>
        <div>
          <UiLabel>{{ t('settings.nodeLabelField') }}</UiLabel>
          <UiInput v-model="nodeOverride.label" :placeholder="t('settings.nodeLabelPlaceholder')" />
        </div>
        <div class="ac-sheet-actions">
          <UiButton size="sm" variant="outline" @click="selectedModeNode = null">{{ t('settings.cancel') }}</UiButton>
          <UiButton size="sm" @click="applyNodeOverride">{{ t('settings.applyAction') }}</UiButton>
        </div>
      </div>
    </UiSheet>

    <UiSheet :open="showVersionDrawer" side="right" @update:open="showVersionDrawer = $event">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.modeVersionHistory', { name: selectedMode?.display_name ?? '—' }) }}</h3>
        <UiButton size="sm" variant="outline" :disabled="modeVersionsLoading" @click="loadModeVersions">{{ t('settings.refreshHistory') }}</UiButton>
        <div v-if="modeVersionsLoading" class="quiet">{{ t('common.loading') }}</div>
        <div v-else class="ac-version-list">
          <div v-for="v in modeVersions" :key="v.id" class="ac-version-row">
            <span class="ac-version-main">
              <strong>v{{ v.version }}</strong>
              <small>{{ v.created_at ? new Date(v.created_at).toLocaleString() : '—' }}</small>
            </span>
            <UiBadge :variant="v.is_active ? 'default' : 'secondary'">{{ v.is_active ? 'active' : 'archived' }}</UiBadge>
          </div>
          <div v-if="modeVersions.length === 0" class="quiet">{{ t('settings.noModeVersions') }}</div>
        </div>
      </div>
    </UiSheet>
  </section>
</template>

<style scoped>
.agent-modes-panel {
  display: grid;
  gap: 12px;
}
.ac-mode-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.ac-mode-tabs {
  display: grid;
  gap: 6px;
}
.ac-mode-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  cursor: pointer;
}
.ac-mode-row.active {
  border-color: var(--accent-brand);
}
.ac-mode-meta {
  display: grid;
  gap: 2px;
  text-align: left;
  min-width: 0;
}
.ac-mode-meta strong {
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ac-mode-meta small {
  color: var(--text-muted);
  font-size: 10px;
}
.ac-mode-empty {
  font-size: 11px;
  padding: 8px;
}
.ac-mode-canvas-col {
  display: grid;
  gap: 10px;
  min-width: 0;
}
.ac-edge-bar {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-raised);
  font-size: 12px;
}
.ac-edge-label-input {
  flex: 1;
  min-width: 160px;
}
.ac-version-list {
  display: grid;
  gap: 8px;
  max-height: 60vh;
  overflow: auto;
}
.ac-version-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-section);
}
.ac-version-main {
  display: grid;
  gap: 2px;
}
.ac-version-main strong {
  font-size: 12px;
}
.ac-version-main small {
  color: var(--text-muted);
  font-size: 10px;
}
.quiet {
  color: var(--text-muted);
}
</style>
