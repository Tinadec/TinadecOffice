<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { History, Plus, RefreshCw, Search, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSheet } from '@/components/ui'
import AgentModeCanvas from '@/components/canvas/AgentModeCanvas.vue'
import GovernanceRolesPanel from '@/components/agentCenter/GovernanceRolesPanel.vue'
import { api, type AgentDefinitionDto, type AgentModeEdgeDto, type AgentModeNodeDto, type AgentModeTopologyDto, type ModeVersionDto, type ModelProviderInstanceDto, type ModelRouteDto } from '@/api'
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
const nodeOverride = ref({
  agent_id: '',
  label: '',
  strategy_kind: 'inherit' as 'inherit' | 'route' | 'fixed',
  strategy_route_purpose: '',
  strategy_provider_instance_id: '',
  strategy_model: '',
})
const nodePreviewSummary = ref('')
const nodePreviewBusy = ref(false)

const agents = ref<AgentDefinitionDto[]>([])
const providers = ref<ModelProviderInstanceDto[]>([])
const routes = ref<ModelRouteDto[]>([])

const filteredModes = computed(() => {
  const q = modeQuery.value.trim().toLowerCase()
  if (!q) return modes.value
  return modes.value.filter((m) => `${m.display_name} ${m.summary ?? ''}`.toLowerCase().includes(q))
})
const selectedMode = computed(() => modes.value.find((m) => m.id === selectedModeId.value) ?? null)
const selectedModeReadOnly = computed(() => {
  const m = selectedMode.value
  return m !== null && Boolean(m.managed || m.status === 'published' || m.status === 'archived')
})

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
    let operationIndex = 0
    let executionIndex = 0
    modeNodes.value = ((m.nodes ?? []) as unknown as Array<Record<string, unknown>>).map((n) => {
      // Canvas contract (agent_id/lane); Core projections speak agent_definition_id/layer.
      const lane = normalizeLayer((n.layer ?? n.lane) != null ? String(n.layer ?? n.lane) : undefined)
      const slot = lane === 'execution' ? executionIndex++ : operationIndex++
      return {
        id: String(n.node_key ?? n.id),
        node_key: n.node_key != null ? String(n.node_key) : undefined,
        agent_id: n.agent_id != null ? String(n.agent_id) : n.agent_definition_id != null ? String(n.agent_definition_id) : '',
        lane,
        // Published projections carry null positions; give VueFlow a stable two-lane layout.
        position: (n.position as { x: number; y: number } | null) ?? { x: 60 + (slot % 4) * 230, y: 30 },
        label: (n.label as string) ?? '',
        model_strategy_override: (n.model_strategy_override as AgentModeNodeDto['model_strategy_override']) ?? null,
      }
    }) as AgentModeNodeDto[]
    modeEdges.value = ((m.edges ?? []) as unknown as Array<Record<string, unknown>>).map((e) => ({
      id: String(e.edge_key ?? e.id),
      source: e.source != null ? String(e.source) : String(e.source_node_key ?? ''),
      target: e.target != null ? String(e.target) : String(e.target_node_key ?? ''),
      label: (e.label as string | undefined) ?? undefined,
    })) as AgentModeEdgeDto[]
    modeEtag.value = m.revision != null ? String(m.revision) : null
    // Fold the managed/status facts back onto the list row so the read-only notice
    // reflects the live resource state for pack-installed published modes too.
    if (m.managed !== undefined || m.status) {
      modes.value = modes.value.map((row) =>
        row.id === id
          ? {
              ...row,
              managed: m.managed ?? row.managed,
              status: m.status ?? row.status,
            }
          : row,
      )
    }
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

function toCoreTopology(nodes: AgentModeNodeDto[], edges: AgentModeEdgeDto[]) {
  // Core UpsertModeTopology speaks node_key/agent_definition_id/layer and
  // source_node_key/target_node_key — map the canvas shape back before saving.
  return {
    nodes: nodes.map((n) => ({
      node_key: n.node_key ?? n.id,
      agent_definition_id: n.agent_id,
      layer: normalizeLayer(n.lane),
      label: n.label ?? null,
      position: n.position ?? null,
      model_strategy_override: n.model_strategy_override ?? null,
    })),
    edges: edges.map((e) => ({
      source_node_key: e.source,
      target_node_key: e.target,
      ...(e.label ? { condition: { label: e.label } } : {}),
    })),
  }
}

async function saveModeDraft() {
  if (!selectedModeId.value || selectedModeReadOnly.value) return
  modeBusy.value = true
  try {
    await api.updateAgentModeDraft(selectedModeId.value, { ...toCoreTopology(modeNodes.value, modeEdges.value), canvas_layout: {} }, modeEtag.value)
    notify.success({ message: t('agentCenter.modeDraftSaved') })
    await loadModes()
  } catch (e) {
    notify.error(e, { title: t('agentCenter.modeSaveFailed') })
  } finally {
    modeBusy.value = false
  }
}

async function publishMode() {
  if (!selectedModeId.value || selectedModeReadOnly.value) return
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

async function cloneSelectedMode() {
  const source = selectedMode.value
  if (!source || !selectedModeReadOnly.value) return
  modeBusy.value = true
  try {
    const m = await api.createAgentModeDraft({
      display_name: `${source.display_name} (copy)`,
      summary: 'draft',
      ...toCoreTopology(modeNodes.value, modeEdges.value),
      canvas_layout: {}
    })
    await loadModes()
    await selectMode(m.id)
    notify.success({ message: t('agentCenter.modeCreated', { name: m.display_name }) })
  } catch (e) {
    notify.error(e)
  } finally {
    modeBusy.value = false
  }
}

function addModeNode() {
  if (selectedModeReadOnly.value) return
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
  nodePreviewSummary.value = ''
  if (n) {
    const strategy = n.model_strategy_override
    nodeOverride.value = {
      agent_id: n.agent_id,
      label: n.label ?? '',
      strategy_kind: strategy?.kind ?? 'inherit',
      strategy_route_purpose: strategy?.route_purpose ?? routes.value[0]?.purpose ?? '',
      strategy_provider_instance_id: strategy?.provider_instance_id ?? '',
      strategy_model: strategy?.model ?? '',
    }
  }
}

function handleSelectEdge(e: AgentModeEdgeDto | null) {
  selectedEdge.value = e
  selectedModeNode.value = null
  edgeLabelDraft.value = e?.label ?? ''
}

/** Build the node's model_strategy_override from the editor state. */
function nodeStrategyOverride(): Record<string, unknown> | null {
  if (nodeOverride.value.strategy_kind === 'inherit') return null
  if (nodeOverride.value.strategy_kind === 'route') {
    return nodeOverride.value.strategy_route_purpose
      ? { kind: 'route', route_purpose: nodeOverride.value.strategy_route_purpose }
      : null
  }
  return nodeOverride.value.strategy_provider_instance_id
    ? { kind: 'fixed', provider_instance_id: nodeOverride.value.strategy_provider_instance_id, model: nodeOverride.value.strategy_model || null }
    : null
}

/** Resolve what the node's strategy would select before applying it. */
async function previewNodeStrategy() {
  if (!selectedModeNode.value) return
  nodePreviewBusy.value = true
  nodePreviewSummary.value = ''
  try {
    const preview = await api.previewModelResolution({
      strategy: nodeStrategyOverride() as never,
      agent_definition_id: selectedModeNode.value.agent_id,
      mode_version_id: selectedModeId.value || null,
      node_key: selectedModeNode.value.node_key ?? selectedModeNode.value.id,
    })
    const selection = preview.expected_selection
    nodePreviewSummary.value = selection
      ? `${selection.provider_instance_id ?? '—'}${selection.model ? ` · ${selection.model}` : ''}`
      : t('settings.runtimeUnresolved')
  } catch (e) {
    nodePreviewSummary.value = e instanceof Error ? e.message : String(e)
  } finally {
    nodePreviewBusy.value = false
  }
}

async function applyNodeOverride() {
  if (!selectedModeNode.value || selectedModeReadOnly.value) return
  modeNodes.value = modeNodes.value.map((n) =>
    n.id === selectedModeNode.value!.id
      ? {
          ...n,
          agent_id: nodeOverride.value.agent_id || n.agent_id,
          label: nodeOverride.value.label || n.label,
          model_strategy_override: nodeStrategyOverride() as AgentModeNodeDto['model_strategy_override'],
        }
      : n
  )
  selectedModeNode.value = null
}

function saveEdgeLabel() {
  if (!selectedEdge.value || selectedModeReadOnly.value) return
  modeEdges.value = modeEdges.value.map((e) =>
    e.id === selectedEdge.value!.id ? { ...e, label: edgeLabelDraft.value || undefined } : e
  )
  selectedEdge.value = { ...selectedEdge.value, label: edgeLabelDraft.value || undefined }
}

function deleteEdge() {
  if (!selectedEdge.value || selectedModeReadOnly.value) return
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
  void api.listModelProviders().then((rows) => { providers.value = rows }).catch(() => { providers.value = [] })
  void api.listModelRoutes().then((rows) => { routes.value = rows }).catch(() => { routes.value = [] })
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
      <UiButton size="sm" variant="outline" :disabled="!selectedModeId || modeBusy || selectedModeReadOnly" @click="saveModeDraft">{{ t('settings.saveDraftMode') }}</UiButton>
      <UiButton size="sm" variant="outline" :disabled="!selectedModeId || modeBusy || selectedModeReadOnly" @click="publishMode">{{ t('settings.publishModeAction') }}</UiButton>
      <UiButton size="sm" variant="ghost" :disabled="!selectedModeId || selectedModeReadOnly" @click="addModeNode"><Plus :size="14" />{{ t('settings.addNodeAction') }}</UiButton>
      <UiButton size="sm" variant="outline" :disabled="!selectedModeId" @click="openVersionDrawer"><History :size="14" />{{ t('agentCenter.versionHistory') }}</UiButton>
      <UiButton v-if="selectedModeReadOnly" size="sm" :disabled="modeBusy" @click="cloneSelectedMode">{{ t('agentPack.cloneAction') }}</UiButton>
    </div>

    <div v-if="selectedModeReadOnly && selectedMode" class="ac-mode-readonly-notice">
      <UiBadge variant="outline">{{ t('agentPack.managed') }}</UiBadge>
      <span>{{ selectedMode.managed ? t('agentPack.managedReadOnly') : t('agentCenter.publishedReadOnlyHint') }}</span>
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
          :readonly="selectedModeReadOnly"
          @update:nodes="modeNodes = $event"
          @update:edges="modeEdges = $event"
          @select-node="handleSelectNode"
          @select-edge="handleSelectEdge"
        />
        <div v-if="selectedEdge && !selectedModeReadOnly" class="ac-edge-bar">
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
        <div class="ac-node-strategy">
          <UiLabel>{{ t('settings.nodeStrategyOverride') }}</UiLabel>
          <select v-model="nodeOverride.strategy_kind" class="settings-select">
            <option value="inherit">{{ t('settings.runtimeInherit') }}</option>
            <option value="route">{{ t('settings.runtimeRoute') }}</option>
            <option value="fixed">{{ t('settings.runtimeFixedModel') }}</option>
          </select>
          <div v-if="nodeOverride.strategy_kind === 'route'" class="ac-node-strategy-field">
            <UiLabel>{{ t('settings.routePurpose') }}</UiLabel>
            <select v-model="nodeOverride.strategy_route_purpose" class="settings-select">
              <option value="" disabled>{{ t('settings.selectRoutePurpose') }}</option>
              <option v-for="route in routes" :key="route.id ?? route.purpose" :value="route.purpose">{{ route.purpose }}</option>
            </select>
          </div>
          <div v-else-if="nodeOverride.strategy_kind === 'fixed'" class="ac-node-strategy-field">
            <UiLabel>{{ t('settings.selectProvider') }}</UiLabel>
            <select v-model="nodeOverride.strategy_provider_instance_id" class="settings-select">
              <option value="" disabled>{{ t('settings.selectProvider') }}</option>
              <option v-for="provider in providers" :key="provider.id" :value="provider.id">{{ provider.display_name }}</option>
            </select>
            <UiInput
              v-model="nodeOverride.strategy_model"
              :placeholder="t('settings.routeModel')"
              class="ac-node-strategy-model"
            />
          </div>
          <div v-if="nodePreviewSummary" class="ac-node-preview quiet">{{ nodePreviewSummary }}</div>
          <UiButton size="sm" variant="outline" :disabled="nodePreviewBusy || nodeOverride.strategy_kind === 'inherit'" @click="previewNodeStrategy">
            {{ t('settings.previewNodeStrategy') }}
          </UiButton>
        </div>
        <div class="ac-sheet-actions">
          <UiButton size="sm" variant="outline" @click="selectedModeNode = null">{{ t('settings.cancel') }}</UiButton>
          <UiButton size="sm" :disabled="selectedModeReadOnly" @click="applyNodeOverride">{{ t('settings.applyAction') }}</UiButton>
        </div>
      </div>
    </UiSheet>

    <UiSheet :open="showVersionDrawer" side="right" @update:open="showVersionDrawer = $event">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.modeVersionHistory', { name: selectedMode?.display_name ?? '—' }) }}</h3>
        <UiButton size="sm" variant="outline" :disabled="modeVersionsLoading" @click="loadModeVersions">{{ t('settings.refreshHistory') }}</UiButton>
        <div v-if="modeVersionsLoading" class="quiet">{{ t('common.loading') }}</div>
        <div v-else class="ac-version-list">
          <div v-for="v in modeVersions" :key="v.id" class="center-list-row ac-version-row">
            <strong>v{{ v.version }}</strong>
            <small>{{ v.created_at ? new Date(v.created_at).toLocaleString() : '—' }}</small>
            <UiBadge :variant="v.is_active ? 'default' : 'secondary'">{{ v.is_active ? t('settings.agentVersionCurrent') : t('settings.agentVersionArchived') }}</UiBadge>
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
.ac-node-strategy {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}
.ac-node-strategy-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.ac-node-preview {
  font-size: 12px;
  word-break: break-all;
}
.ac-mode-readonly-notice {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px dashed var(--border-muted);
  background: var(--surface-section);
  color: var(--text-muted);
  font-size: 12px;
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
  background: var(--surface-raised);
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
  font-size: 11px;
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
.ac-version-row strong {
  font-size: 12px;
}
.ac-version-row small {
  color: var(--text-muted);
  font-size: 11px;
}
.quiet {
  color: var(--text-muted);
}
</style>
