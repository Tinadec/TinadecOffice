<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { History, Plus, RefreshCw, Redo2, Search, Trash2, Undo2 } from '@lucide/vue'
import { UiBadge, UiButton, UiCheckbox, UiInput, UiLabel, UiSelectField, UiSheet, UiTextarea } from '@/components/ui'
import AgentModeCanvas from '@/components/canvas/AgentModeCanvas.vue'
import { findFreeSlot, layoutMode } from '@/components/canvas/modeLayout'
import { hasBlockingModeIssue, validateMode } from '@/components/canvas/modeValidation'
import { agentSlugOf, defaultMemberScope, type MemberScope } from '@/settings/agentCenter/teamShape'
import GovernanceRolesPanel from '@/components/agentCenter/GovernanceRolesPanel.vue'
import { api as baseApi, type AgentDefinitionDto, type AgentModeBindingDto, type AgentModeEdgeDto, type AgentModeNodeDto, type AgentModeRuntimePolicy, type AgentModeRuntimePolicyDefinition, type AgentModeTopologyDto, type ModeVersionDto, type ModelProviderInstanceDto, type ModelRouteDto, type PackI18n } from '@/api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import { useNotifications } from '@/composables/useNotifications'
import { modeIcon, sortModes } from '@/lib/modePresentation'
import { withoutRetiredModes } from '@/lib/retiredModes'
import { pickLocalized } from '@/lib/packI18n'
import { consumeRequest, pendingModeId } from '@/lib/pageRequests'

const { t, te, locale } = useI18n()
const definitionStatusLabel = (status: string) => (te(`agentCenter.filter.${status}`) ? t(`agentCenter.filter.${status}`) : status)
const { notify, confirm } = useNotifications()

interface GraphSnapshot {
  nodes: AgentModeNodeDto[]
  edges: AgentModeEdgeDto[]
  bindingsDraft: string
}

const canvasRef = ref<InstanceType<typeof AgentModeCanvas> | null>(null)
const canvasLayout = ref<Record<string, unknown> | null>(null)
const modeNameDraft = ref('')
const modeDescriptionDraft = ref('')
const modeInstructionsDraft = ref('')
const graphBaseline = ref('')
const historyPast = ref<GraphSnapshot[]>([])
const historyFuture = ref<GraphSnapshot[]>([])
const showAddNode = ref(false)
const addNodeDraft = ref<{ agentId: string; lane: 'operation' | 'execution' }>({ agentId: '', lane: 'operation' })
const addNodeAgentOptions = computed(() => {
  const lane = addNodeDraft.value.lane
  const pool = lane === 'execution'
    ? agents.value.filter((agent) => normalizeLayer(agent.layer) === 'execution')
    : agents.value.filter((agent) => normalizeLayer(agent.layer) !== 'execution')
  return pool.map((agent) => ({ value: agent.id, label: `${agentName(agent)} (${normalizeLayer(agent.layer)})` }))
})
watch(() => addNodeDraft.value.lane, () => {
  if (!addNodeAgentOptions.value.some((option) => option.value === addNodeDraft.value.agentId)) {
    addNodeDraft.value.agentId = addNodeAgentOptions.value[0]?.value ?? ''
  }
})

const selectedNodeId = computed(() => selectedModeNode.value?.id ?? null)
const selectedEdgeId = computed(() => selectedEdge.value?.id ?? null)

function normalizeLane(value: unknown): 'operation' | 'execution' {
  return String(value ?? '').trim().toLowerCase() === 'execution' ? 'execution' : 'operation'
}

function nodeKeyOf(node: AgentModeNodeDto): string {
  return node.node_key ?? node.id
}

function cloneSnapshot(): GraphSnapshot {
  return {
    nodes: modeNodes.value.map((node) => ({ ...node, position: { ...node.position } })),
    edges: modeEdges.value.map((edge) => ({ ...edge })),
    bindingsDraft: bindingsDraft.value,
  }
}

function graphSignal(): string {
  return JSON.stringify({
    nodes: modeNodes.value,
    edges: modeEdges.value,
    bindings: bindingsDraft.value,
    policy: {
      kind: runtimePolicyKind.value,
      features: runtimePolicyFeatures.value,
      settings: runtimePolicySettingsJson.value,
    },
    name: modeNameDraft.value,
    description: modeDescriptionDraft.value,
    instructions: modeInstructionsDraft.value,
  })
}

const graphDirty = computed(() => Boolean(selectedModeId.value) && graphSignal() !== graphBaseline.value)

const publishIssues = computed(() => validateMode(
  modeNodes.value.map((node) => ({
    id: node.id,
    lane: normalizeLane(node.lane),
    agent_id: node.agent_id,
    config: node.config ?? null,
  })),
  modeEdges.value.map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
  { requireConversation: true },
))
const publishBlocked = computed(() => hasBlockingModeIssue(publishIssues.value))

function resetGraphHistory() {
  historyPast.value = []
  historyFuture.value = []
}

function recordGraphHistory() {
  const snapshot = cloneSnapshot()
  const last = historyPast.value[historyPast.value.length - 1]
  if (last && JSON.stringify(last) === JSON.stringify(snapshot)) return
  historyPast.value = [...historyPast.value.slice(-49), snapshot]
  historyFuture.value = []
}

function applyGraph(
  nodes: AgentModeNodeDto[],
  edges: AgentModeEdgeDto[],
  record = true,
  nextBindings?: string,
) {
  if (selectedModeReadOnly.value) return
  if (record) recordGraphHistory()
  modeNodes.value = nodes
  modeEdges.value = edges
  if (nextBindings !== undefined) bindingsDraft.value = nextBindings
}

function restoreSnapshot(snapshot: GraphSnapshot) {
  modeNodes.value = snapshot.nodes
  modeEdges.value = snapshot.edges
  bindingsDraft.value = snapshot.bindingsDraft
  if (selectedModeNode.value && !snapshot.nodes.some((node) => node.id === selectedModeNode.value?.id)) {
    selectedModeNode.value = null
  }
  if (selectedEdge.value && !snapshot.edges.some((edge) => edge.id === selectedEdge.value?.id)) {
    selectedEdge.value = null
  }
}

function undoGraph() {
  const previous = historyPast.value[historyPast.value.length - 1]
  if (!previous || selectedModeReadOnly.value) return
  historyFuture.value = [cloneSnapshot(), ...historyFuture.value]
  historyPast.value = historyPast.value.slice(0, -1)
  restoreSnapshot(previous)
}

function redoGraph() {
  const next = historyFuture.value[0]
  if (!next || selectedModeReadOnly.value) return
  historyPast.value = [...historyPast.value, cloneSnapshot()]
  historyFuture.value = historyFuture.value.slice(1)
  restoreSnapshot(next)
}

async function confirmDiscardGraph(): Promise<boolean> {
  if (!graphBaseline.value || !graphDirty.value) return true
  return confirm({
    title: t('settings.modeDiscardChanges'),
    message: t('settings.modeDiscardChangesMessage'),
    confirmLabel: t('settings.discardChanges'),
    cancelLabel: t('settings.keepEditing'),
    destructive: true,
  })
}

const modes = ref<AgentModeTopologyDto[]>([])
const selectedModeId = ref('')
const modeQuery = ref('')
const modeNodes = ref<AgentModeNodeDto[]>([])
const modeEdges = ref<AgentModeEdgeDto[]>([])
const bindingsDraft = ref('[]')
const bindingsError = ref<string | null>(null)
const relationshipDraft = ref('{}')
const relationshipError = ref<string | null>(null)
const edgeConditionDraft = ref('{}')
const edgeConditionError = ref<string | null>(null)
const modeEtag = ref<string | null>(null)
const runtimePolicyKind = ref<'none' | AgentModeRuntimePolicy['kind']>('none')
const runtimePolicyFeatures = ref<string[]>([])
const runtimePolicySettingsJson = ref('')
const runtimePolicyDefinitions = ref<AgentModeRuntimePolicyDefinition[]>([])
const modeVersions = ref<ModeVersionDto[]>([])
const modeVersionsLoading = ref(false)
const showVersionDrawer = ref(false)
const modeBusy = ref(false)
let modeNavigationGeneration = 0

const selectedModeNode = ref<AgentModeNodeDto | null>(null)
const selectedEdge = ref<AgentModeEdgeDto | null>(null)
const edgeLabelDraft = ref('')
const nodeOverride = ref({
  agent_id: '',
  label: '',
  lane: 'operation' as 'operation' | 'execution',
  conversation: false,
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
  const sorted = sortModes(modes.value)
  if (!q) return sorted
  return sorted.filter((m) => `${modeName(m)} ${modeDescription(m)}`.toLowerCase().includes(q))
})
const selectedMode = computed(() => modes.value.find((m) => m.id === selectedModeId.value) ?? null)

/** Displayed names follow the reader's language; edits still write the baseline fields. */
function modeName(mode: AgentModeTopologyDto): string {
  return pickLocalized(mode.i18n, locale.value, 'display_name', mode.display_name) ?? mode.display_name
}
function modeDescription(mode: AgentModeTopologyDto): string {
  return pickLocalized(mode.i18n, locale.value, 'description', mode.description) ?? ''
}
function agentName(agent: AgentDefinitionDto): string {
  return pickLocalized(agent.i18n, locale.value, 'display_name', agent.display_name) ?? agent.display_name ?? agent.name ?? agent.id
}
const selectedModeReadOnly = computed(() => {
  const m = selectedMode.value
  return Boolean(selectedModeId.value && (!m || m.managed || m.status === 'published' || m.status === 'archived'))
})
const selectedPolicyDefinition = computed(() =>
  runtimePolicyDefinitions.value.find((definition) => definition.kind === runtimePolicyKind.value) ?? null
)
const policyKindOptions = computed(() => [
  { value: 'none', label: t('settings.runtimePolicyNone') },
  ...runtimePolicyDefinitions.value.map((definition) => ({
    value: definition.kind,
    label: definition.kind === 'structured_investigation'
      ? t('settings.runtimePolicyStructuredInvestigation')
      : definition.kind === 'autonomous_exploration'
        ? t('settings.runtimePolicyAutonomousExploration')
        : definition.kind,
  })),
])
/** Parse the settings draft without mutating it: invalid JSON must block saving, not silently save null. */
const parsedPolicySettings = computed<{ value: Record<string, unknown> | null; error: string }>(() => {
  if (runtimePolicyKind.value !== 'autonomous_exploration') return { value: null, error: '' }
  const raw = runtimePolicySettingsJson.value.trim()
  if (!raw) return { value: {}, error: '' }
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return { value: null, error: t('settings.runtimePolicySettingsInvalid') }
    }
    return { value: parsed as Record<string, unknown>, error: '' }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { value: null, error: `${t('settings.runtimePolicySettingsInvalid')} ${message}` }
  }
})
const runtimePolicy = computed<AgentModeRuntimePolicy | null>(() => {
  if (runtimePolicyKind.value === 'none') return null
  if (runtimePolicyKind.value === 'structured_investigation') {
    return { kind: 'structured_investigation', version: 1, features: [...runtimePolicyFeatures.value] }
  }
  const settings = parsedPolicySettings.value.value
  if (!settings) return null
  return { kind: 'autonomous_exploration', version: 1, features: [...runtimePolicyFeatures.value], settings }
})

function setRuntimePolicyKind(value: string) {
  const kind: 'none' | AgentModeRuntimePolicy['kind'] =
    value === 'structured_investigation' || value === 'autonomous_exploration' ? value : 'none'
  if (kind === runtimePolicyKind.value) return
  runtimePolicyKind.value = kind
  const definition = runtimePolicyDefinitions.value.find((item) => item.kind === kind) ?? null
  // Selecting a policy starts from its full allow-list (the old correction
  // editor did the same); required features cannot be unchecked below.
  runtimePolicyFeatures.value = definition ? [...definition.features] : []
  runtimePolicySettingsJson.value = kind === 'autonomous_exploration' && definition?.default_settings
    ? JSON.stringify(definition.default_settings, null, 2)
    : ''
}

function togglePolicyFeature(feature: string, checked: boolean) {
  if (selectedModeReadOnly.value) return
  if (!checked) {
    if (selectedPolicyDefinition.value?.required_features.includes(feature)) return
    runtimePolicyFeatures.value = runtimePolicyFeatures.value.filter((item) => item !== feature)
    return
  }
  if (!runtimePolicyFeatures.value.includes(feature)) runtimePolicyFeatures.value = [...runtimePolicyFeatures.value, feature]
}

function normalizeLayer(layer?: string): 'operation' | 'execution' {
  return layer === 'execution' ? 'execution' : 'operation'
}
function readinessVariant(status?: string): 'default' | 'secondary' | 'outline' {
  if (status === 'published') return 'default'
  if (status === 'archived') return 'secondary'
  return 'outline'
}

let modesRead: Promise<void> | null = null
function loadModes(): Promise<void> {
  return modesRead ??= loadModesData().finally(() => { modesRead = null })
}
async function loadModesData() {
  try {
    const list = await api.listAgentModeTopologies()
    modes.value = withoutRetiredModes(Array.isArray(list) ? list : [])
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

function applyRuntimePolicy(policy: AgentModeRuntimePolicy | null | undefined) {
  if (policy?.kind === 'structured_investigation') {
    runtimePolicyKind.value = policy.kind
    runtimePolicyFeatures.value = [...policy.features]
    runtimePolicySettingsJson.value = ''
    return
  }
  if (policy?.kind === 'autonomous_exploration') {
    runtimePolicyKind.value = policy.kind
    runtimePolicyFeatures.value = [...policy.features]
    runtimePolicySettingsJson.value = policy.settings ? JSON.stringify(policy.settings, null, 2) : ''
    return
  }
  runtimePolicyKind.value = 'none'
  runtimePolicyFeatures.value = []
  runtimePolicySettingsJson.value = ''
}

async function selectMode(id: string, options: { force?: boolean } = {}) {
  if (!options.force && id !== selectedModeId.value && !(await confirmDiscardGraph())) return
  selectedModeId.value = id
  selectedModeNode.value = null
  selectedEdge.value = null
  try {
    const m = await api.getAgentModeTopology(id)
    if (selectedModeId.value !== id) return
    const rawNodes = (m.nodes ?? []) as unknown as Array<Record<string, unknown>>
    const missingPosition = new Set<string>()
    const mappedNodes = rawNodes.map((n) => {
      // Canvas contract (agent_id/lane); Core projections speak agent_definition_id/layer.
      const nodeId = String(n.node_key ?? n.id)
      const lane = normalizeLayer((n.layer ?? n.lane) != null ? String(n.layer ?? n.lane) : undefined)
      const position = n.position as { x: number; y: number } | null
      if (!position || typeof position.x !== 'number' || typeof position.y !== 'number') missingPosition.add(nodeId)
      return {
        id: nodeId,
        node_key: n.node_key != null ? String(n.node_key) : undefined,
        agent_id: n.agent_id != null ? String(n.agent_id) : n.agent_definition_id != null ? String(n.agent_definition_id) : '',
        lane,
        position: position ?? { x: 60, y: 40 },
        label: (n.label as string) ?? '',
        i18n: (n.i18n as PackI18n | null | undefined) ?? null,
        config: (n.config as Record<string, unknown> | null | undefined) ?? null,
        relationship: (n.relationship as Record<string, unknown> | null | undefined) ?? null,
        model_strategy_override: (n.model_strategy_override as AgentModeNodeDto['model_strategy_override']) ?? null,
      } as AgentModeNodeDto
    })
    const mappedEdges = ((m.edges ?? []) as unknown as Array<Record<string, unknown>>).map((e) => {
      const condition = (e.condition as Record<string, unknown> | null | undefined) ?? null
      const conditionLabel = condition?.label
      return {
        id: String(e.edge_key ?? e.id),
        edge_key: e.edge_key != null ? String(e.edge_key) : undefined,
        source: e.source != null ? String(e.source) : String(e.source_node_key ?? ''),
        target: e.target != null ? String(e.target) : String(e.target_node_key ?? ''),
        // Core stores an edge's label inside `condition`; reading only `label`
        // dropped it on every reload.
        label: (e.label as string | undefined) ?? (typeof conditionLabel === 'string' ? conditionLabel : undefined),
        condition,
      } as AgentModeEdgeDto
    })
    // Published projections may carry null positions; suggest a two-lane layout
    // for exactly those nodes and keep every hand-placed position untouched.
    const suggested = missingPosition.size > 0
      ? layoutMode(
          mappedNodes.map((node) => ({ id: node.id, lane: normalizeLane(node.lane) })),
          mappedEdges.map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
        ).positions
      : {}
    modeNodes.value = mappedNodes.map((node) => missingPosition.has(node.id)
      ? { ...node, position: suggested[node.id] ?? { x: 60, y: 40 } }
      : node)
    modeEdges.value = mappedEdges
    const bindings = ((m.bindings ?? []) as AgentModeBindingDto[]).map((binding) => ({ ...binding }))
    bindingsDraft.value = JSON.stringify(bindings, null, 2)
    bindingsError.value = null
    modeEtag.value = m.revision != null ? String(m.revision) : null
    canvasLayout.value = (m.canvas_layout as Record<string, unknown> | null | undefined) ?? null
    modeNameDraft.value = m.display_name ?? ''
    modeDescriptionDraft.value = m.description ?? ''
    modeInstructionsDraft.value = m.instructions ?? ''
    applyRuntimePolicy(m.runtime_policy)
    graphBaseline.value = graphSignal()
    resetGraphHistory()
    // Fold the managed/status facts back onto the list row so the read-only notice
    // reflects the live resource state for pack-installed published modes too.
    if (m.managed !== undefined || m.status) {
      modes.value = modes.value.map((row) =>
        row.id === id
          ? {
              ...row,
              display_name: m.display_name ?? row.display_name,
              description: m.description ?? row.description,
              managed: m.managed ?? row.managed,
              status: m.status ?? row.status,
            }
          : row,
      )
    }
  } catch (error) {
    if (selectedModeId.value !== id) return
    modeNodes.value = []
    modeEdges.value = []
    modeEtag.value = null
    canvasLayout.value = null
    modeNameDraft.value = ''
    modeDescriptionDraft.value = ''
    modeInstructionsDraft.value = ''
    applyRuntimePolicy(null)
    graphBaseline.value = graphSignal()
    resetGraphHistory()
    notify.error(error, { title: t('agentCenter.loadModesFailed') })
  }
}

consumeRequest(pendingModeId, async (modeId) => {
  const generation = ++modeNavigationGeneration
  if (!(await confirmDiscardGraph())) return
  modeQuery.value = ''
  if (!modes.value.some((mode) => mode.id === modeId)) await loadModes()
  if (generation !== modeNavigationGeneration) return
  if (!modes.value.some((mode) => mode.id === modeId)) {
    notify.error(t('app.loadFailedMessage'), { title: t('agentCenter.loadModesFailed'), key: 'search-navigation' })
    return
  }
  await selectMode(modeId, { force: true })
})

async function createMode() {
  if (!(await confirmDiscardGraph())) return
  modeBusy.value = true
  try {
    const m = await api.createAgentModeDraft({
      display_name: `mode-${Date.now().toString(36).slice(0, 6)}`,
      nodes: [],
      edges: [],
      runtime_policy: null,
      canvas_layout: null,
    })
    await loadModes()
    await selectMode(m.id, { force: true })
    notify.success({ message: t('agentCenter.modeCreated', { name: m.display_name }) })
  } catch (e) {
    notify.error(e)
  } finally {
    modeBusy.value = false
  }
}

/** Parse the advanced bindings editor; invalid JSON aborts the save instead of dropping envelopes. */
function readBindings(): AgentModeBindingDto[] | null {
  if (!bindingsDraft.value.trim()) { bindingsError.value = null; return [] }
  try {
    const parsed = JSON.parse(bindingsDraft.value)
    if (!Array.isArray(parsed)) throw new Error(t('settings.bindingsMustBeArray'))
    for (const binding of parsed) {
      if (!binding || typeof binding !== 'object' || Array.isArray(binding)) throw new Error(t('settings.bindingsMustBeObjects'))
      if (!binding.node_key && !binding.agent_ref) throw new Error(t('settings.bindingNeedsKey'))
    }
    bindingsError.value = null
    return parsed as AgentModeBindingDto[]
  } catch (error) {
    bindingsError.value = error instanceof Error ? error.message : String(error)
    return null
  }
}

function toCoreTopology(nodes: AgentModeNodeDto[], edges: AgentModeEdgeDto[], bindings: AgentModeBindingDto[]) {
  // Core UpsertModeTopology speaks node_key/agent_definition_id/layer and
  // source_node_key/target_node_key — map the canvas shape back before saving.
  return {
    nodes: nodes.map((n) => ({
      node_key: n.node_key ?? n.id,
      agent_definition_id: n.agent_id,
      layer: normalizeLayer(n.lane),
      label: n.label ?? null,
      position: n.position ?? null,
      config: n.config ?? null,
      relationship: n.relationship ?? null,
      model_strategy_override: n.model_strategy_override ?? null,
    })),
    edges: edges.map((e) => ({
      edge_key: e.edge_key ?? e.id,
      source_node_key: e.source,
      target_node_key: e.target,
      ...(e.condition || e.label ? { condition: e.condition ?? { label: e.label } } : {}),
    })),
    bindings,
  }
}

async function saveModeDraft(): Promise<boolean> {
  if (!selectedModeId.value || selectedModeReadOnly.value) return false
  if (!modeNameDraft.value.trim()) {
    notify.error(new Error(t('settings.modeNameRequired')))
    return false
  }
  if (parsedPolicySettings.value.error) {
    notify.error(parsedPolicySettings.value.error, { title: t('settings.runtimePolicySettingsInvalid') })
    return false
  }
  const bindings = readBindings()
  if (!bindings) { notify.error(new Error(t('settings.bindingsInvalid'))); return false }
  modeBusy.value = true
  try {
    const saved = await api.updateAgentModeDraft(selectedModeId.value, {
      display_name: modeNameDraft.value.trim(),
      description: modeDescriptionDraft.value.trim() || null,
      instructions: modeInstructionsDraft.value.trim() || null,
      ...toCoreTopology(modeNodes.value, modeEdges.value, bindings),
      runtime_policy: runtimePolicy.value,
      canvas_layout: canvasLayout.value,
    }, modeEtag.value)
    // The response carries the new revision; keeping the old If-Match made the
    // second save of the same draft fail with 412.
    if (saved?.revision != null) modeEtag.value = String(saved.revision)
    modes.value = modes.value.map((row) => row.id === selectedModeId.value
      ? {
          ...row,
          display_name: saved?.display_name ?? modeNameDraft.value.trim(),
          description: saved?.description ?? modeDescriptionDraft.value.trim(),
          revision: saved?.revision ?? row.revision,
          status: saved?.status ?? row.status,
        }
      : row)
    graphBaseline.value = graphSignal()
    notify.success({ message: t('agentCenter.modeDraftSaved') })
    return true
  } catch (e) {
    notify.error(e, { title: t('agentCenter.modeSaveFailed') })
    return false
  } finally {
    modeBusy.value = false
  }
}

async function publishMode() {
  if (!selectedModeId.value || selectedModeReadOnly.value) return
  if (publishBlocked.value) {
    notify.error(new Error(t('settings.modePublishBlocked')))
    return
  }
  if (graphDirty.value && !(await saveModeDraft())) return
  modeBusy.value = true
  try {
    const published = await api.publishAgentMode(selectedModeId.value, modeEtag.value)
    if (published?.revision != null) modeEtag.value = String(published.revision)
    notify.success({ message: t('agentCenter.modePublished') })
    await loadModes()
    await selectMode(selectedModeId.value, { force: true })
  } catch (e) {
    notify.error(e)
  } finally {
    modeBusy.value = false
  }
}

async function cloneSelectedMode() {
  const source = selectedMode.value
  if (!source || !selectedModeReadOnly.value) return
  if (parsedPolicySettings.value.error) {
    notify.error(parsedPolicySettings.value.error, { title: t('settings.runtimePolicySettingsInvalid') })
    return
  }
  const bindings = readBindings()
  if (!bindings) { notify.error(new Error(t('settings.bindingsInvalid'))); return }
  modeBusy.value = true
  try {
    const m = await api.createAgentModeDraft({
      display_name: t('common.copyName', { name: source.display_name }),
      description: source.description ?? null,
      instructions: modeInstructionsDraft.value.trim() || null,
      ...toCoreTopology(modeNodes.value, modeEdges.value, bindings),
      runtime_policy: runtimePolicy.value,
      canvas_layout: canvasLayout.value,
    })
    await loadModes()
    await selectMode(m.id, { force: true })
    notify.success({ message: t('agentCenter.modeCreated', { name: m.display_name }) })
  } catch (e) {
    notify.error(e)
  } finally {
    modeBusy.value = false
  }
}

function newGraphId(prefix: string): string {
  const random = globalThis.crypto?.randomUUID?.().slice(0, 8) ?? Math.random().toString(36).slice(2, 10)
  return `${prefix}-${random}`
}

function openAddNode(lane: 'operation' | 'execution') {
  if (selectedModeReadOnly.value) return
  const pool = lane === 'execution'
    ? agents.value.filter((agent) => normalizeLayer(agent.layer) === 'execution')
    : agents.value.filter((agent) => normalizeLayer(agent.layer) !== 'execution')
  if (pool.length === 0) {
    notify.error(new Error(t(lane === 'execution' ? 'settings.modeNoAgentsInLane' : 'settings.modeNoAgents')))
    return
  }
  addNodeDraft.value = { agentId: pool[0].id, lane }
  showAddNode.value = true
}

function confirmAddNode() {
  if (selectedModeReadOnly.value) return
  const agent = agents.value.find((item) => item.id === addNodeDraft.value.agentId)
  if (!agent) {
    notify.error(new Error(t('settings.modeNoAgents')))
    return
  }
  const lane = normalizeLane(addNodeDraft.value.lane)
  const id = newGraphId('n')
  const hasConversation = modeNodes.value.some((node) => node.config?.conversation === true)
  // A brand-new mode has no conversation identity yet; PublishMode requires one,
  // so the first operation node becomes it unless the user moves the marker.
  const config = lane === 'operation' && !hasConversation ? { conversation: true } : null
  const position = findFreeSlot(
    modeNodes.value.map((node) => ({ id: node.id, lane: normalizeLane(node.lane), position: node.position })),
    lane,
  )
  const node: AgentModeNodeDto = {
    id,
    node_key: id,
    agent_id: agent.id,
    lane,
    position,
    label: '',
    config,
    relationship: null,
    model_strategy_override: null,
  }
  applyGraph([...modeNodes.value, node], modeEdges.value)
  showAddNode.value = false
  handleSelectNode(id)
}

function handleSelectNode(id: string | null) {
  const node = id ? modeNodes.value.find((item) => item.id === id) ?? null : null
  selectedModeNode.value = node
  selectedEdge.value = null
  nodePreviewSummary.value = ''
  if (node) {
    relationshipDraft.value = JSON.stringify(node.relationship ?? {}, null, 2)
    relationshipError.value = null
    const strategy = node.model_strategy_override
    nodeOverride.value = {
      agent_id: node.agent_id,
      label: node.label ?? '',
      lane: normalizeLane(node.lane),
      conversation: node.config?.conversation === true,
      strategy_kind: strategy?.kind ?? 'inherit',
      strategy_route_purpose: strategy?.route_purpose ?? routes.value[0]?.purpose ?? '',
      strategy_provider_instance_id: strategy?.provider_instance_id ?? '',
      strategy_model: strategy?.model ?? '',
    }
  }
}

function handleSelectEdge(id: string | null) {
  const edge = id ? modeEdges.value.find((item) => item.id === id) ?? null : null
  selectedEdge.value = edge
  selectedModeNode.value = null
  const conditionLabel = edge?.condition?.label
  edgeLabelDraft.value = edge?.label ?? (typeof conditionLabel === 'string' ? conditionLabel : '')
  edgeConditionDraft.value = JSON.stringify(edge?.condition ?? (edge?.label ? { label: edge.label } : {}), null, 2)
  edgeConditionError.value = null
}

function handleNodeMoved(payload: { id: string; position: { x: number; y: number } }) {
  applyGraph(
    modeNodes.value.map((node) => node.id === payload.id ? { ...node, position: payload.position } : node),
    modeEdges.value,
  )
}

function uniqueEdgeId(source: string, target: string): string {
  const base = `e-${source}-${target}`
  if (!modeEdges.value.some((edge) => edge.id === base)) return base
  let suffix = 2
  while (modeEdges.value.some((edge) => edge.id === `${base}-${suffix}`)) suffix += 1
  return `${base}-${suffix}`
}

function handleConnect(payload: { source: string; target: string }) {
  if (selectedModeReadOnly.value) return
  if (modeEdges.value.some((edge) => edge.source === payload.source && edge.target === payload.target)) return
  applyGraph(modeNodes.value, [
    ...modeEdges.value,
    { id: uniqueEdgeId(payload.source, payload.target), source: payload.source, target: payload.target },
  ])
}

function handleDeleteEdge(id: string) {
  if (selectedModeReadOnly.value) return
  applyGraph(modeNodes.value, modeEdges.value.filter((edge) => edge.id !== id))
  if (selectedEdge.value?.id === id) selectedEdge.value = null
}

async function handleDeleteNode(id: string) {
  const node = modeNodes.value.find((item) => item.id === id)
  if (!node || selectedModeReadOnly.value) return
  if (node.config?.conversation === true) {
    notify.error(new Error(t('settings.conversationNodeRequired')))
    return
  }
  const discard = await confirm({
    title: t('settings.deleteNodeTitle'),
    message: t('settings.deleteNodeMessage', { name: node.label || node.agent_id }),
    confirmLabel: t('settings.deleteAction'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!discard) return
  const bindings = readBindings()
  const nextBindings = bindings
    ? JSON.stringify(bindings.filter((binding) => binding.node_key !== nodeKeyOf(node)), null, 2)
    : undefined
  applyGraph(
    modeNodes.value.filter((item) => item.id !== id),
    modeEdges.value.filter((edge) => edge.source !== id && edge.target !== id),
    true,
    nextBindings,
  )
  if (selectedModeNode.value?.id === id) selectedModeNode.value = null
}

function autoLayoutGraph() {
  if (selectedModeReadOnly.value) return
  const result = layoutMode(
    modeNodes.value.map((node) => ({ id: node.id, lane: normalizeLane(node.lane) })),
    modeEdges.value.map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
  )
  applyGraph(
    modeNodes.value.map((node) => {
      const position = result.positions[node.id]
      return position ? { ...node, position } : node
    }),
    modeEdges.value,
  )
  void nextTick(() => canvasRef.value?.fitView())
}

/** Team-shaped modes keep their members as node-less spawn templates, so the
 * graph can only show the leader; this belt is the same data the team tab edits. */
const MAX_SPAWN_MEMBERS = 4
const spawnMemberToAdd = ref('')
const parsedBindings = computed<AgentModeBindingDto[] | null>(() => {
  if (!bindingsDraft.value.trim()) return []
  try {
    const parsed: unknown = JSON.parse(bindingsDraft.value)
    return Array.isArray(parsed) ? parsed as AgentModeBindingDto[] : null
  } catch {
    return null
  }
})
const leaderGraphNode = computed(() => modeNodes.value.find((node) => normalizeLane(node.lane) === 'operation' && node.config?.conversation === true)
  ?? modeNodes.value.find((node) => normalizeLane(node.lane) === 'operation')
  ?? null)
const spawnMemberSlugs = computed<string[]>(() => {
  const types = leaderGraphNode.value?.relationship?.agent_types
  return Array.isArray(types) ? types.filter((value): value is string => typeof value === 'string' && value.length > 0) : []
})
const showSpawnBelt = computed(() => Boolean(leaderGraphNode.value) && !modeNodes.value.some((node) => normalizeLane(node.lane) === 'execution'))
const spawnMembers = computed(() => {
  if (!showSpawnBelt.value || !parsedBindings.value) return []
  const rows: Array<{ agentRef: string; name: string; layer: string; scope: MemberScope }> = []
  const seen = new Set<string>()
  for (const binding of parsedBindings.value) {
    if (binding.node_key || !binding.agent_ref || seen.has(binding.agent_ref)) continue
    seen.add(binding.agent_ref)
    const agent = agents.value.find((item) => agentSlugOf(item) === binding.agent_ref)
    const resources = binding.envelope?.resources as { read?: unknown; write?: unknown } | undefined
    const has = (value: unknown) => Array.isArray(value) && value.length > 0
    rows.push({
      agentRef: binding.agent_ref,
      name: agent ? agentName(agent) : binding.agent_ref,
      layer: agent ? normalizeLayer(agent.layer) : '—',
      scope: {
        read: resources ? has(resources.read) : true,
        write: resources ? has(resources.write) : false,
      },
    })
  }
  for (const slug of spawnMemberSlugs.value) {
    if (seen.has(slug)) continue
    const agent = agents.value.find((item) => agentSlugOf(item) === slug)
    rows.push({
      agentRef: slug,
      name: agent ? agentName(agent) : slug,
      layer: agent ? normalizeLayer(agent.layer) : '—',
      scope: agent ? defaultMemberScope(agent) : { read: true, write: false },
    })
  }
  return rows
})
const spawnMemberOptions = computed(() => agents.value
  .filter((agent) => agent.enabled !== false && (!agent.status || agent.status === 'published'))
  .filter((agent) => agent.id !== leaderGraphNode.value?.agent_id)
  .filter((agent) => !spawnMembers.value.some((member) => member.agentRef === agentSlugOf(agent)))
  .map((agent) => ({ value: agent.id, label: `${agentName(agent)} (${normalizeLayer(agent.layer)})` })))

function resourcesFor(scope: MemberScope): Record<string, string[]> {
  const resources: Record<string, string[]> = {}
  if (scope.read) resources.read = ['']
  if (scope.write) resources.write = ['']
  return resources
}

function withSpawnTarget(
  current: Record<string, unknown> | null | undefined,
  slug: string,
  add: boolean,
): Record<string, unknown> | null {
  const next: Record<string, unknown> = { ...(current ?? {}) }
  for (const key of ['agent_types', 'allowed_dispatch_targets']) {
    const list = Array.isArray(next[key])
      ? (next[key] as unknown[]).filter((value): value is string => typeof value === 'string')
      : []
    const updated = add
      ? (list.includes(slug) ? list : [...list, slug])
      : list.filter((value) => value !== slug)
    if (updated.length > 0) next[key] = updated
    else delete next[key]
  }
  return Object.keys(next).length > 0 ? next : null
}

function writeBindings(next: AgentModeBindingDto[], nextNodes = modeNodes.value) {
  applyGraph(nextNodes, modeEdges.value, true, JSON.stringify(next, null, 2))
}

function toggleSpawnScope(agentRef: string, key: 'read' | 'write') {
  if (selectedModeReadOnly.value || !parsedBindings.value) return
  const member = spawnMembers.value.find((row) => row.agentRef === agentRef)
  if (!member) return
  const scope = { ...member.scope, [key]: !member.scope[key] }
  writeBindings(parsedBindings.value.map((binding) => binding.node_key || binding.agent_ref !== agentRef
    ? binding
    : {
        ...binding,
        envelope: { ...(binding.envelope ?? {}), resources: resourcesFor(scope) },
      }))
}

function addSpawnMember() {
  if (selectedModeReadOnly.value || !parsedBindings.value) return
  const agent = agents.value.find((item) => item.id === spawnMemberToAdd.value)
  if (!agent || spawnMembers.value.length >= MAX_SPAWN_MEMBERS) return
  const slug = agentSlugOf(agent)
  if (parsedBindings.value.some((binding) => !binding.node_key && binding.agent_ref === slug)) return
  const nextBindings: AgentModeBindingDto[] = [
    ...parsedBindings.value,
    {
      agent_ref: slug,
      envelope: { resources: resourcesFor(defaultMemberScope(agent)) },
      includes_core_reserved: false,
    },
  ]
  const nextNodes = modeNodes.value.map((node) => node.id === leaderGraphNode.value?.id
    ? { ...node, relationship: withSpawnTarget(node.relationship, slug, true) }
    : node)
  writeBindings(nextBindings, nextNodes)
  spawnMemberToAdd.value = ''
}

function removeSpawnMember(agentRef: string) {
  if (selectedModeReadOnly.value || !parsedBindings.value) return
  const nextBindings = parsedBindings.value.filter((binding) => binding.node_key || binding.agent_ref !== agentRef)
  const nextNodes = modeNodes.value.map((node) => node.id === leaderGraphNode.value?.id
    ? { ...node, relationship: withSpawnTarget(node.relationship, agentRef, false) }
    : node)
  writeBindings(nextBindings, nextNodes)
}

function parseJsonObject(text: string, label: string): Record<string, unknown> | null {
  if (!text.trim()) return null
  const parsed = JSON.parse(text)
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error(t('settings.jsonObjectRequired', { field: label }))
  }
  return Object.keys(parsed as Record<string, unknown>).length ? parsed as Record<string, unknown> : null
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
  const current = selectedModeNode.value
  if (!current || selectedModeReadOnly.value) return
  let relationship: Record<string, unknown> | null
  try {
    relationship = parseJsonObject(relationshipDraft.value, t('settings.nodeRelationshipField'))
    relationshipError.value = null
  } catch (error) {
    relationshipError.value = error instanceof Error ? error.message : String(error)
    return
  }
  const lane = normalizeLane(nodeOverride.value.lane)
  const nextAgent = agents.value.find((agent) => agent.id === (nodeOverride.value.agent_id || current.agent_id))
  if (nextAgent && normalizeLayer(nextAgent.layer) !== lane) {
    notify.error(new Error(t('settings.laneAgentMismatch')))
    return
  }
  if (nodeOverride.value.conversation && lane !== 'operation') {
    notify.error(new Error(t('settings.conversationLayerRequired')))
    return
  }
  const nextNodes = modeNodes.value.map((node) => {
    if (node.id === current.id) {
      const config: Record<string, unknown> = { ...(node.config ?? {}) }
      if (nodeOverride.value.conversation) config.conversation = true
      else delete config.conversation
      const label = nodeOverride.value.label.trim()
      return {
        ...node,
        agent_id: nodeOverride.value.agent_id || node.agent_id,
        // Empty means "clear"; the old `|| oldLabel` kept a deleted label alive.
        label: label || null,
        lane,
        config: Object.keys(config).length > 0 ? config : null,
        relationship,
        model_strategy_override: nodeStrategyOverride() as AgentModeNodeDto['model_strategy_override'],
      }
    }
    if (nodeOverride.value.conversation && node.config?.conversation === true) {
      const config = { ...node.config }
      delete config.conversation
      return { ...node, config: Object.keys(config).length > 0 ? config : null }
    }
    return node
  })
  if (lane !== normalizeLane(current.lane)) {
    const moved = nextNodes.find((node) => node.id === current.id)!
    moved.position = findFreeSlot(
      nextNodes.filter((node) => node.id !== current.id)
        .map((node) => ({ id: node.id, lane: normalizeLane(node.lane), position: node.position })),
      lane,
    )
  }
  applyGraph(nextNodes, modeEdges.value)
  selectedModeNode.value = null
}

function saveEdgeLabel() {
  if (!selectedEdge.value || selectedModeReadOnly.value) return
  let condition: Record<string, unknown>
  try {
    condition = parseJsonObject(edgeConditionDraft.value, t('settings.edgeConditionField')) ?? {}
    edgeConditionError.value = null
  } catch (error) {
    edgeConditionError.value = error instanceof Error ? error.message : String(error)
    return
  }
  const label = edgeLabelDraft.value.trim()
  // Core stores the edge label inside `condition`; mirroring it here is what
  // makes the label survive a reload.
  if (label) condition.label = label
  else delete condition.label
  const nextCondition = Object.keys(condition).length > 0 ? condition : null
  applyGraph(
    modeNodes.value,
    modeEdges.value.map((edge) => edge.id === selectedEdge.value!.id
      ? { ...edge, label: label || undefined, condition: nextCondition }
      : edge),
  )
  selectedEdge.value = { ...selectedEdge.value, label: label || undefined, condition: nextCondition }
}

function deleteEdge() {
  if (!selectedEdge.value) return
  handleDeleteEdge(selectedEdge.value.id)
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

async function restoreModeVersion(version: ModeVersionDto) {
  const snapshot = version.snapshot
  if (!snapshot) {
    notify.error(new Error(t('settings.modeVersionSnapshotMissing')))
    return
  }
  if (!(await confirmDiscardGraph())) return
  const nodes = (snapshot.nodes ?? []).map((node) => ({
    node_key: String(node.node_key ?? ''),
    agent_definition_id: String(node.agent_definition_id ?? ''),
    layer: (node.layer === 'execution' ? 'execution' : 'operation') as 'operation' | 'execution',
    label: (node.label as string | null | undefined) ?? null,
    position: (node.position as { x: number; y: number } | null | undefined) ?? null,
    config: (node.config as Record<string, unknown> | null | undefined) ?? null,
    relationship: (node.relationship as Record<string, unknown> | null | undefined) ?? null,
    model_strategy_override: (node.model_strategy_override as Record<string, unknown> | null | undefined) ?? null,
  })).filter(node => node.node_key && node.agent_definition_id)
  const edges = (snapshot.edges ?? []).map((edge) => ({
    edge_key: String(edge.edge_key ?? ''),
    source_node_key: String(edge.source_node_key ?? ''),
    target_node_key: String(edge.target_node_key ?? ''),
    ...(edge.condition ? { condition: edge.condition as Record<string, unknown> } : {}),
  })).filter(edge => edge.source_node_key && edge.target_node_key)
  const topology = {
    nodes,
    edges,
    bindings: (snapshot.bindings ?? []) as AgentModeBindingDto[],
    canvas_layout: snapshot.canvas_layout ?? null,
    instructions: snapshot.instructions ?? null,
    ...(snapshot.runtime_policy ? { runtime_policy: snapshot.runtime_policy } : {}),
  }
  modeBusy.value = true
  try {
    if (selectedModeReadOnly.value) {
      const name = selectedMode.value?.display_name ?? 'mode'
      const created = await api.createAgentModeDraft({
        display_name: t('settings.modeRestoreName', { name, version: version.version }),
        description: selectedMode.value?.description ?? null,
        ...topology,
      })
      await loadModes()
      await selectMode(created.id, { force: true })
      notify.success({ message: t('settings.modeRestored', { version: version.version }) })
    } else if (selectedModeId.value) {
      const saved = await api.updateAgentModeDraft(selectedModeId.value, topology, modeEtag.value)
      if (saved?.revision != null) modeEtag.value = String(saved.revision)
      await selectMode(selectedModeId.value, { force: true })
      notify.success({ message: t('settings.modeRestored', { version: version.version }) })
    }
  } catch (error) {
    notify.error(error)
  } finally {
    modeBusy.value = false
  }
}

onMounted(() => {
  void loadAgents()
  void loadModes()
  void api.listAgentModeRuntimePolicies().then((rows) => { runtimePolicyDefinitions.value = rows }).catch(() => { runtimePolicyDefinitions.value = [] })
  void api.listModelProviders().then((rows) => { providers.value = rows }).catch(() => { providers.value = [] })
  void api.listModelRoutes().then((rows) => { routes.value = rows }).catch(() => { routes.value = [] })
})

onBeforeUnmount(() => { modeNavigationGeneration++ })

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
      <UiButton size="sm" variant="outline" :disabled="!selectedModeId || modeBusy || selectedModeReadOnly || publishBlocked" :title="publishBlocked ? t('settings.modePublishBlocked') : undefined" @click="publishMode">{{ t('settings.publishModeAction') }}</UiButton>
      <UiButton size="sm" variant="ghost" :disabled="!selectedModeId || selectedModeReadOnly" @click="openAddNode('operation')"><Plus :size="14" />{{ t('settings.addNodeAction') }}</UiButton>
      <UiButton size="sm" variant="ghost" :disabled="!selectedModeId || selectedModeReadOnly || historyPast.length === 0" :title="t('settings.undoGraph')" @click="undoGraph"><Undo2 :size="14" /></UiButton>
      <UiButton size="sm" variant="ghost" :disabled="!selectedModeId || selectedModeReadOnly || historyFuture.length === 0" :title="t('settings.redoGraph')" @click="redoGraph"><Redo2 :size="14" /></UiButton>
      <UiButton size="sm" variant="outline" :disabled="!selectedModeId" @click="openVersionDrawer"><History :size="14" />{{ t('agentCenter.versionHistory') }}</UiButton>
      <UiButton v-if="selectedModeReadOnly" size="sm" :disabled="modeBusy" @click="cloneSelectedMode">{{ t('agentPack.cloneAction') }}</UiButton>
      <UiBadge v-if="graphDirty && !selectedModeReadOnly" variant="outline" class="ac-mode-dirty">{{ t('settings.modeUnsaved') }}</UiBadge>
    </div>

    <div v-if="selectedModeReadOnly && selectedMode" class="ac-mode-readonly-notice">
      <UiBadge variant="outline">{{ t('agentPack.managed') }}</UiBadge>
      <span>{{ selectedMode.managed ? t('agentPack.managedReadOnly') : t('agentCenter.publishedReadOnlyHint') }}</span>
    </div>

    <div v-if="selectedModeId" class="ac-mode-settings">
      <div class="ac-mode-settings-field">
        <UiLabel>{{ t('settings.modeNameField') }}</UiLabel>
        <UiInput v-model="modeNameDraft" :disabled="selectedModeReadOnly" :placeholder="t('settings.modeNameField')" />
      </div>
      <div class="ac-mode-settings-field">
        <UiLabel>{{ t('settings.modeDescriptionField') }}</UiLabel>
        <UiInput v-model="modeDescriptionDraft" :disabled="selectedModeReadOnly" :placeholder="t('settings.modeDescriptionField')" />
      </div>
      <div class="ac-mode-settings-field wide">
        <UiLabel>{{ t('settings.modeInstructionsField') }}</UiLabel>
        <UiTextarea
          v-model="modeInstructionsDraft"
          :rows="5"
          maxlength="4000"
          :disabled="selectedModeReadOnly"
          :placeholder="t('settings.modeInstructionsPlaceholder')"
        />
        <small class="quiet">{{ modeInstructionsDraft.length }} / 4000</small>
      </div>
    </div>

    <div v-if="selectedModeId" class="ac-mode-policy">
      <div class="ac-mode-policy-head">
        <UiLabel>{{ t('settings.runtimePolicy') }}</UiLabel>
        <UiSelectField
          :model-value="runtimePolicyKind"
          :disabled="selectedModeReadOnly"
          :options="policyKindOptions"
          @update:model-value="setRuntimePolicyKind"
        />
      </div>
      <template v-if="selectedPolicyDefinition">
        <div class="ac-mode-policy-features">
          <UiLabel>{{ t('settings.runtimePolicyFeatures') }}</UiLabel>
          <label
            v-for="feature in selectedPolicyDefinition.features"
            :key="feature"
            class="ac-mode-policy-feature"
          >
            <UiCheckbox
              :model-value="runtimePolicyFeatures.includes(feature)"
              :disabled="selectedModeReadOnly || selectedPolicyDefinition.required_features.includes(feature)"
              :aria-label="feature"
              @update:model-value="(checked: boolean) => togglePolicyFeature(feature, checked)"
            />
            <span>{{ feature }}</span>
            <UiBadge v-if="selectedPolicyDefinition.required_features.includes(feature)" variant="outline">
              {{ t('settings.runtimePolicyRequiredFeature') }}
            </UiBadge>
          </label>
          <p v-if="selectedPolicyDefinition.dependencies.length" class="quiet">
            {{ t('settings.runtimePolicyDependenciesHint', { pairs: selectedPolicyDefinition.dependencies.map((dependency) => `${dependency.feature} → ${dependency.requires}`).join(', ') }) }}
          </p>
        </div>
        <div v-if="runtimePolicyKind === 'autonomous_exploration'" class="ac-mode-policy-settings">
          <UiLabel>{{ t('settings.runtimePolicySettings') }}</UiLabel>
          <UiTextarea
            v-model="runtimePolicySettingsJson"
            :rows="10"
            :disabled="selectedModeReadOnly"
            placeholder='{ "max_parallel_workers": 4 }'
          />
          <p v-if="parsedPolicySettings.error" class="ac-mode-policy-error">{{ parsedPolicySettings.error }}</p>
        </div>
      </template>
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
            <component :is="modeIcon(m.slug)" :size="14" class="ac-mode-icon" />
            <span class="ac-mode-meta" :title="modeDescription(m) || undefined">
              <strong>{{ modeName(m) }}</strong>
              <small>{{ modeDescription(m) || t('agentCenter.revisionShort', { revision: m.revision ?? '—' }) }}</small>
            </span>
            <UiBadge :variant="readinessVariant(m.status)">{{ m.status ? definitionStatusLabel(m.status) : t('common.draft') }}</UiBadge>
          </button>
          <div v-if="filteredModes.length === 0" class="quiet ac-mode-empty">{{ t('agentCenter.empty.noModes') }}</div>
        </div>

        <!-- Workspace defaults / governance roles -->
        <GovernanceRolesPanel :manifest-tool-ids="[]" />
      </aside>

      <main class="center-resource-stage ac-mode-canvas-col">
        <AgentModeCanvas
          ref="canvasRef"
          :nodes="modeNodes"
          :edges="modeEdges"
          :agents="agents"
          :readonly="selectedModeReadOnly"
          :selected-node-id="selectedNodeId"
          :selected-edge-id="selectedEdgeId"
          :graph-key="selectedModeId"
          @select-node="handleSelectNode"
          @select-edge="handleSelectEdge"
          @node-moved="handleNodeMoved"
          @connect="handleConnect"
          @delete-node="handleDeleteNode"
          @delete-edge="handleDeleteEdge"
          @add-node="openAddNode($event.lane)"
          @auto-layout="autoLayoutGraph"
          @undo="undoGraph"
          @redo="redoGraph"
        />

        <div v-if="showSpawnBelt" class="ac-spawn-belt">
          <div class="ac-spawn-belt-head">
            <strong>{{ t('settings.spawnMembersTitle') }}</strong>
            <span>{{ spawnMembers.length }} / {{ MAX_SPAWN_MEMBERS }}</span>
            <small :title="t('settings.spawnMembersHint')">{{ t('settings.spawnMembersHint') }}</small>
          </div>
          <div v-if="spawnMembers.length > 0" class="ac-spawn-belt-list">
            <div v-for="member in spawnMembers" :key="member.agentRef" class="ac-spawn-member">
              <span class="ac-spawn-member-name" :title="member.agentRef">{{ member.name }}</span>
              <small>{{ member.layer }}</small>
              <div class="ac-spawn-scope">
                <button type="button" :class="{ active: member.scope.read }" :disabled="selectedModeReadOnly" @click="toggleSpawnScope(member.agentRef, 'read')">
                  {{ t('settings.teamMemberRead') }}
                </button>
                <button type="button" :class="{ active: member.scope.write }" :disabled="selectedModeReadOnly" @click="toggleSpawnScope(member.agentRef, 'write')">
                  {{ t('settings.teamMemberWrite') }}
                </button>
              </div>
              <button type="button" class="ac-spawn-remove" :disabled="selectedModeReadOnly" :aria-label="t('settings.teamRemoveMember')" @click="removeSpawnMember(member.agentRef)">
                <Trash2 :size="12" />
              </button>
            </div>
          </div>
          <p v-else class="quiet">{{ t('settings.spawnMembersEmpty') }}</p>
          <div v-if="!selectedModeReadOnly && spawnMembers.length < MAX_SPAWN_MEMBERS" class="ac-spawn-add">
            <UiSelectField v-model="spawnMemberToAdd" :options="spawnMemberOptions" />
            <UiButton size="sm" variant="outline" :disabled="!spawnMemberToAdd" @click="addSpawnMember">
              <Plus :size="13" />{{ t('settings.spawnMemberAdd') }}
            </UiButton>
          </div>
          <p v-else-if="spawnMembers.length >= MAX_SPAWN_MEMBERS" class="quiet">{{ t('settings.spawnMemberLimit') }}</p>
        </div>

        <details class="ac-advanced-bindings">
          <summary>{{ t('settings.bindingsAdvanced') }}</summary>
          <p class="quiet">{{ t('settings.bindingsAdvancedHint') }}</p>
          <textarea
            v-model="bindingsDraft"
            class="ac-json-editor"
            rows="8"
            spellcheck="false"
            :disabled="selectedModeReadOnly"
          ></textarea>
          <p v-if="bindingsError" class="ac-mode-policy-error">{{ bindingsError }}</p>
        </details>
      </main>
    </div>

    <UiSheet :open="Boolean(selectedModeNode)" side="right" @update:open="!$event && (selectedModeNode = null)">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.nodeConfigTitle', { id: selectedModeNode?.id ?? '—' }) }}</h3>
        <p class="quiet">{{ t('settings.nodeConfigHint') }}</p>
        <div>
          <UiLabel>{{ t('settings.boundAgent') }}</UiLabel>
          <UiSelectField
            v-model="nodeOverride.agent_id"
            :options="agents.map((a) => ({ value: a.id, label: `${agentName(a)} (${normalizeLayer(a.layer)})` }))"
          />
        </div>
        <div>
          <UiLabel>{{ t('settings.nodeLane') }}</UiLabel>
          <UiSelectField
            v-model="nodeOverride.lane"
            :disabled="selectedModeReadOnly"
            :options="[
              { value: 'operation', label: t('workbench.operationLane') },
              { value: 'execution', label: t('workbench.executionLane') },
            ]"
          />
        </div>
        <label class="ac-node-conversation">
          <UiCheckbox
            :model-value="nodeOverride.conversation"
            :disabled="selectedModeReadOnly"
            @update:model-value="(checked: boolean) => (nodeOverride.conversation = checked)"
          />
          <span>{{ t('settings.conversationNode') }}</span>
        </label>
        <div>
          <UiLabel>{{ t('settings.nodeLabelField') }}</UiLabel>
          <UiInput v-model="nodeOverride.label" :placeholder="t('settings.nodeLabelPlaceholder')" />
        </div>
        <div class="ac-node-strategy">
          <UiLabel>{{ t('settings.nodeStrategyOverride') }}</UiLabel>
          <UiSelectField
            v-model="nodeOverride.strategy_kind"
            :options="[
              { value: 'inherit', label: t('settings.runtimeInherit') },
              { value: 'route', label: t('settings.runtimeRoute') },
              { value: 'fixed', label: t('settings.runtimeFixedModel') },
            ]"
          />
          <div v-if="nodeOverride.strategy_kind === 'route'" class="ac-node-strategy-field">
            <UiLabel>{{ t('settings.routePurpose') }}</UiLabel>
            <UiSelectField
              v-model="nodeOverride.strategy_route_purpose"
              :options="[
                { value: '', label: t('settings.selectRoutePurpose'), disabled: true },
                ...routes.map((route) => ({ value: route.purpose, label: route.purpose })),
              ]"
            />
          </div>
          <div v-else-if="nodeOverride.strategy_kind === 'fixed'" class="ac-node-strategy-field">
            <UiLabel>{{ t('settings.selectProvider') }}</UiLabel>
            <UiSelectField
              v-model="nodeOverride.strategy_provider_instance_id"
              :options="[
                { value: '', label: t('settings.selectProvider'), disabled: true },
                ...providers.map((provider) => ({ value: provider.id, label: provider.display_name })),
              ]"
            />
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
        <div class="ac-node-strategy">
          <UiLabel>{{ t('settings.nodeRelationshipField') }}</UiLabel>
          <p class="quiet">{{ t('settings.nodeRelationshipHint') }}</p>
          <textarea
            v-model="relationshipDraft"
            class="ac-json-editor"
            rows="6"
            spellcheck="false"
            :disabled="selectedModeReadOnly"
          ></textarea>
          <p v-if="relationshipError" class="ac-mode-policy-error">{{ relationshipError }}</p>
        </div>
        <div class="ac-sheet-actions">
          <UiButton size="sm" variant="ghost" class="provider-delete-btn" :disabled="selectedModeReadOnly" @click="selectedModeNode && handleDeleteNode(selectedModeNode.id)">
            <Trash2 :size="14" />{{ t('settings.deleteAction') }}
          </UiButton>
          <UiButton size="sm" variant="outline" @click="selectedModeNode = null">{{ t('settings.cancel') }}</UiButton>
          <UiButton size="sm" :disabled="selectedModeReadOnly" @click="applyNodeOverride">{{ t('settings.applyAction') }}</UiButton>
        </div>
      </div>
    </UiSheet>

    <UiSheet :open="Boolean(selectedEdge)" side="right" @update:open="!$event && (selectedEdge = null)">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.edgeConfigTitle') }}</h3>
        <p class="quiet">{{ selectedEdge ? `${selectedEdge.source} → ${selectedEdge.target}` : '' }}</p>
        <div>
          <UiLabel>{{ t('settings.edgeLabelField') }}</UiLabel>
          <UiInput v-model="edgeLabelDraft" :placeholder="t('settings.edgeLabelPlaceholder')" />
        </div>
        <div class="ac-node-strategy">
          <UiLabel>{{ t('settings.edgeConditionField') }}</UiLabel>
          <textarea
            v-model="edgeConditionDraft"
            class="ac-json-editor"
            rows="6"
            spellcheck="false"
            :disabled="selectedModeReadOnly"
          ></textarea>
          <p v-if="edgeConditionError" class="ac-mode-policy-error">{{ edgeConditionError }}</p>
        </div>
        <div class="ac-sheet-actions">
          <UiButton size="sm" variant="ghost" class="provider-delete-btn" :disabled="selectedModeReadOnly" @click="deleteEdge">
            <Trash2 :size="14" />{{ t('settings.deleteAction') }}
          </UiButton>
          <UiButton size="sm" variant="outline" @click="selectedEdge = null">{{ t('settings.cancel') }}</UiButton>
          <UiButton size="sm" :disabled="selectedModeReadOnly" @click="saveEdgeLabel">{{ t('settings.applyAction') }}</UiButton>
        </div>
      </div>
    </UiSheet>

    <UiSheet :open="showAddNode" side="right" @update:open="showAddNode = $event">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.addNodeAction') }}</h3>
        <div>
          <UiLabel>{{ t('settings.boundAgent') }}</UiLabel>
          <UiSelectField v-model="addNodeDraft.agentId" :options="addNodeAgentOptions" />
          <p v-if="addNodeAgentOptions.length === 0" class="quiet">{{ t('settings.modeNoAgentsInLane') }}</p>
        </div>
        <div>
          <UiLabel>{{ t('settings.nodeLane') }}</UiLabel>
          <UiSelectField
            v-model="addNodeDraft.lane"
            :options="[
              { value: 'operation', label: t('workbench.operationLane') },
              { value: 'execution', label: t('workbench.executionLane') },
            ]"
          />
        </div>
        <div class="ac-sheet-actions">
          <UiButton size="sm" variant="outline" @click="showAddNode = false">{{ t('settings.cancel') }}</UiButton>
          <UiButton size="sm" :disabled="!addNodeDraft.agentId" @click="confirmAddNode">{{ t('settings.addNodeAction') }}</UiButton>
        </div>
      </div>
    </UiSheet>

    <UiSheet :open="showVersionDrawer" side="right" @update:open="showVersionDrawer = $event">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.modeVersionHistory', { name: selectedMode ? modeName(selectedMode) : '—' }) }}</h3>
        <UiButton size="sm" variant="outline" :disabled="modeVersionsLoading" @click="loadModeVersions">{{ t('settings.refreshHistory') }}</UiButton>
        <div v-if="modeVersionsLoading" class="quiet">{{ t('common.loading') }}</div>
        <div v-else class="ac-version-list">
          <div v-for="v in modeVersions" :key="v.id" class="center-list-row ac-version-row">
            <strong>v{{ v.version }}</strong>
            <small>{{ v.created_at ? new Date(v.created_at).toLocaleString(locale) : '—' }}</small>
            <UiBadge :variant="v.is_active ? 'default' : 'secondary'">{{ v.is_active ? t('settings.agentVersionCurrent') : t('settings.agentVersionArchived') }}</UiBadge>
            <UiButton v-if="v.snapshot" size="sm" variant="outline" :disabled="modeBusy" @click="restoreModeVersion(v)">
              {{ t('settings.restoreVersion') }}
            </UiButton>
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
  align-items: center;
}
.ac-mode-dirty { border-color: color-mix(in srgb, var(--accent-warning, #d97706) 45%, var(--border-muted)); color: var(--accent-warning, #d97706); }
.ac-mode-settings {
  display: grid;
  grid-template-columns: minmax(180px, 1fr) minmax(220px, 2fr);
  gap: 10px;
  max-width: 760px;
}
.ac-mode-settings-field { display: flex; flex-direction: column; gap: 4px; }
.ac-mode-settings-field.wide { grid-column: 1 / -1; }
.ac-node-conversation { display: flex; align-items: center; gap: 8px; font-size: 12px; color: var(--text-secondary); }
.ac-mode-policy {
  display: flex;
  flex-direction: column;
  gap: 10px;
  max-width: 640px;
}
.ac-mode-policy-head {
  display: flex;
  align-items: center;
  gap: 8px;
  max-width: 360px;
}
.ac-mode-policy-head > :first-child { flex: 0 0 auto; }
.ac-mode-policy-head > :last-child { flex: 1; min-width: 0; }
.ac-mode-policy-features {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.ac-mode-policy-feature {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
}
.ac-mode-policy-settings {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.ac-mode-policy-error {
  margin: 0;
  color: var(--destructive);
  font-size: 12px;
}
.ac-spawn-belt {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 8px;
  padding: 10px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-section);
}
.ac-spawn-belt-head { display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
.ac-spawn-belt-head strong { font-size: 12px; }
.ac-spawn-belt-head span { font-size: 11px; color: var(--text-secondary); }
.ac-spawn-belt-head small { font-size: 10px; color: var(--text-muted); }
.ac-spawn-belt-list { display: grid; gap: 6px; }
.ac-spawn-member { display: flex; align-items: center; gap: 8px; padding: 6px 8px; border-radius: 8px; background: var(--surface-raised); border: 1px solid var(--border-muted); }
.ac-spawn-member-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; }
.ac-spawn-member small { font-size: 10px; color: var(--text-muted); }
.ac-spawn-scope { display: flex; gap: 4px; }
.ac-spawn-scope button {
  padding: 1px 7px; border-radius: 999px; border: 1px solid var(--border-muted);
  background: transparent; color: var(--text-muted); font-size: 10px; cursor: pointer;
}
.ac-spawn-scope button.active { border-color: var(--accent-primary); background: var(--surface-selected); color: var(--text-primary); }
.ac-spawn-scope button:disabled { opacity: .5; cursor: default; }
.ac-spawn-remove { display: inline-flex; align-items: center; justify-content: center; width: 22px; height: 22px; border: 0; border-radius: 6px; background: transparent; color: var(--text-muted); cursor: pointer; }
.ac-spawn-remove:hover:not(:disabled) { background: var(--surface-hover); color: var(--accent-danger); }
.ac-spawn-add { display: flex; align-items: center; gap: 8px; }
.ac-spawn-add > :first-child { flex: 1; min-width: 0; }
.ac-advanced-bindings {
  margin-top: 8px;
  padding: 10px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-section);
}
.ac-advanced-bindings summary {
  cursor: pointer;
  font-size: 12px;
  color: var(--text-secondary);
}
.ac-json-editor {
  width: 100%;
  margin-top: 6px;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-input);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 12px;
  line-height: 1.5;
  resize: vertical;
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
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
/* The meta column takes the slack, so the name stays next to its icon instead of drifting
   to the middle under the row's space-between. */
.ac-mode-row > .ac-mode-meta {
  flex: 1;
}
.ac-mode-icon {
  flex-shrink: 0;
  color: var(--text-secondary);
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
