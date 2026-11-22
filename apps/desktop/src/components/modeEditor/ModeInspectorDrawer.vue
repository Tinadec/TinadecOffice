<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { History, Plus, RotateCcw, Settings2, Sliders, Terminal, Trash2, X } from '@lucide/vue'
import { UiBadge, UiButton, UiCheckbox, UiInput, UiLabel, UiSelectField, UiTextarea } from '@/components/ui'
import {
  api as baseApi,
  type AgentDefinitionDto,
  type AgentModeBindingDto,
  type AgentModeEdgeDto,
  type AgentModeNodeDto,
  type AgentModeRuntimePolicyDefinition,
  type AgentModeTopologyDto,
  type AgentOrchestrationTier,
  type ModeVersionDto,
  type ModelProviderInstanceDto,
  type ModelRouteDto,
} from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'
import { pickLocalized } from '@/lib/packI18n'
import type { ModeDraftState } from '@/composables/useAgentModesStore'
import type { ContextPolicyParams, ContextStrategyTier } from '@/lib/contextMemoryApi'
import {
  isExternalHarnessChannel,
  providerRuntimeChannel,
  runtimeChannelBadgeLabel,
  type RuntimeChannelKind,
} from '@/settings/agentCenter/directory/agentDirectoryHelpers'
import ModeContextStrategySection from './ModeContextStrategySection.vue'
import type { ModeLane } from './modeDagreLayout'

export type InspectorPanelMode = 'overview' | 'node' | 'edge' | 'add-node' | 'versions'

const props = defineProps<{
  panel: InspectorPanelMode
  readonly?: boolean
  selectedModeId: string
  selectedMode: AgentModeTopologyDto | null
  draft: ModeDraftState
  selectedNode: AgentModeNodeDto | null
  selectedEdge: AgentModeEdgeDto | null
  addNodeLane: ModeLane
  agents: AgentDefinitionDto[]
  providers: ModelProviderInstanceDto[]
  routes: ModelRouteDto[]
  runtimePolicyDefinitions: AgentModeRuntimePolicyDefinition[]
  policySettingsError: string
  bindingsError: string | null
  modeVersions: ModeVersionDto[]
  modeVersionsLoading: boolean
  modeBusy: boolean
}>()

const emit = defineEmits<{
  'close-panel': []
  'update-overview': [patch: Partial<ModeDraftState>, coalesceKey?: string]
  'set-policy-kind': [kind: string]
  'toggle-policy-feature': [feature: string, checked: boolean]
  'update-node': [payload: {
    id: string
    patch: Partial<AgentModeNodeDto>
    exclusiveConversation?: boolean
    coalesceKey?: string
  }]
  'delete-node': [id: string]
  'update-edge': [payload: { id: string; label?: string; condition?: Record<string, unknown> | null; coalesceKey?: string }]
  'delete-edge': [id: string]
  'confirm-add-node': [payload: { agentId: string; lane: ModeLane; label?: string }]
  'refresh-versions': []
  'restore-version': [version: ModeVersionDto]
}>()

const { t, locale } = useI18n()
const { notify } = useNotifications()
const api = scopedApi(baseApi, () => 'user')

function normalizeLane(value: unknown): ModeLane {
  return String(value ?? '').trim().toLowerCase() === 'execution' ? 'execution' : 'operation'
}

function agentDisplayName(agent: AgentDefinitionDto): string {
  return pickLocalized(agent.i18n, locale.value, 'display_name', agent.display_name) ?? agent.display_name ?? agent.name ?? agent.id
}

function layerLabel(layer?: string): string {
  return normalizeLane(layer) === 'execution' ? t('settings.agentLayerExecution') : t('settings.agentLayerPlanning')
}

// ── Mode Overview helpers ────────────────────────────────────────────────────

const tierOptions = computed(() => [
  { value: '__auto__', label: t('settings.modeTierAuto') },
  { value: 'solo_dispatch', label: t('settings.modeTierSolo') },
  { value: 'free_form', label: t('settings.modeTierFreeForm') },
  { value: 'self_dispatch', label: t('settings.modeTierSelfDispatch') },
  { value: 'deterministic', label: t('settings.modeTierDeterministic') },
])

const selectedTierValue = computed({
  get: () => props.draft.orchestrationTier ?? '__auto__',
  set: (value: string) => {
    const next: AgentOrchestrationTier | null = value === '__auto__'
      ? null
      : (value as AgentOrchestrationTier)
    emit('update-overview', { orchestrationTier: next })
  },
})

const selectedPolicyDefinition = computed(() =>
  props.runtimePolicyDefinitions.find((def) => def.kind === props.draft.runtimePolicyKind) ?? null,
)

type DefaultOptionKey = 'plan_first' | 'spec_enabled' | 'multi_agent' | 'bulletin_board' | 'worktree'

const DEFAULT_OPTION_ITEMS: ReadonlyArray<{ key: DefaultOptionKey; labelKey: string }> = [
  { key: 'plan_first', labelKey: 'commandPanel.plan' },
  { key: 'spec_enabled', labelKey: 'commandPanel.spec' },
  { key: 'multi_agent', labelKey: 'commandPanel.agents' },
  { key: 'bulletin_board', labelKey: 'commandPanel.bulletin' },
  { key: 'worktree', labelKey: 'commandPanel.worktree' },
]

function isDefaultOptionEnabled(key: DefaultOptionKey): boolean {
  return Boolean(props.draft.defaultOptions?.[key])
}

function toggleDefaultOption(key: DefaultOptionKey, checked: boolean): void {
  if (props.readonly) return
  const base = props.draft.defaultOptions ?? {
    plan_first: false,
    spec_enabled: false,
    multi_agent: false,
    bulletin_board: false,
    worktree: false,
    workflow_mode_version_id: null,
  }
  emit(
    'update-overview',
    { defaultOptions: { ...base, [key]: Boolean(checked) } },
    `mode-default-opt:${key}`,
  )
}

const modeContextStrategy = computed<ContextStrategyTier | null>(() => {
  const raw = props.draft.canvasLayout?.context_strategy
  return raw === 'direct' || raw === 'managed' || raw === 'deep_curator' ? raw : null
})

const modeContextAdvanced = computed<Partial<ContextPolicyParams> | null>(() => {
  const raw = props.draft.canvasLayout?.context_advanced
  return raw && typeof raw === 'object' && !Array.isArray(raw) ? (raw as Partial<ContextPolicyParams>) : null
})

function updateModeContextStrategy(next: ContextStrategyTier | null): void {
  if (props.readonly) return
  emit(
    'update-overview',
    { canvasLayout: { ...(props.draft.canvasLayout ?? {}), context_strategy: next } },
    'mode-ctx-strategy',
  )
}

function updateModeContextAdvanced(next: Partial<ContextPolicyParams>): void {
  if (props.readonly) return
  emit(
    'update-overview',
    { canvasLayout: { ...(props.draft.canvasLayout ?? {}), context_advanced: next } },
    'mode-ctx-adv',
  )
}

function resetModeContextStrategy(): void {
  if (props.readonly) return
  emit(
    'update-overview',
    { canvasLayout: { ...(props.draft.canvasLayout ?? {}), context_strategy: null, context_advanced: {} } },
    'mode-ctx-reset',
  )
}

const policyKindOptions = computed(() => [
  { value: 'none', label: t('settings.runtimePolicyNone') },
  ...props.runtimePolicyDefinitions.map((def) => ({
    value: def.kind,
    label: def.kind === 'structured_investigation'
      ? t('settings.runtimePolicyStructuredInvestigation')
      : def.kind === 'autonomous_exploration'
        ? t('settings.runtimePolicyAutonomousExploration')
        : def.kind,
  })),
])

// ── Node Inspector helpers ───────────────────────────────────────────────────

const nodeRelationshipJson = ref('{}')
const nodeRelationshipError = ref<string | null>(null)
const nodePreviewSummary = ref('')
const nodePreviewBusy = ref(false)
const nodeChannelFilter = ref<'all' | RuntimeChannelKind>('all')

watch(
  () => props.selectedNode?.id,
  () => {
    nodeRelationshipJson.value = JSON.stringify(props.selectedNode?.relationship ?? {}, null, 2)
    nodeRelationshipError.value = null
    nodePreviewSummary.value = ''
    nodeChannelFilter.value = 'all'
  },
  { immediate: true },
)

watch(
  () => props.selectedNode?.relationship,
  (rel) => {
    if (!nodeRelationshipError.value) {
      nodeRelationshipJson.value = JSON.stringify(rel ?? {}, null, 2)
    }
  },
)

const nodeAgentOptions = computed(() =>
  props.agents.map((a) => ({
    value: a.id,
    label: `${agentDisplayName(a)} (${layerLabel(a.layer)})`,
  })),
)

const nodeStrategyKind = computed<'inherit' | 'route' | 'fixed'>(() =>
  props.selectedNode?.model_strategy_override?.kind ?? 'inherit',
)

const nodeRoutePurpose = computed(() =>
  props.selectedNode?.model_strategy_override?.route_purpose ?? props.routes[0]?.purpose ?? '',
)

const nodeFixedProviderId = computed(() =>
  props.selectedNode?.model_strategy_override?.provider_instance_id ?? '',
)

const nodeFixedModel = computed(() =>
  props.selectedNode?.model_strategy_override?.model ?? '',
)

const selectedFixedProvider = computed<ModelProviderInstanceDto | null>(() =>
  props.providers.find((provider) => provider.id === nodeFixedProviderId.value) ?? null,
)

const selectedFixedChannel = computed<RuntimeChannelKind | null>(() =>
  selectedFixedProvider.value ? providerRuntimeChannel(selectedFixedProvider.value) : null,
)

const selectedFixedIsHarness = computed(() =>
  Boolean(selectedFixedChannel.value && isExternalHarnessChannel(selectedFixedChannel.value)),
)

const nodeProviderChannelTabs = computed(() => {
  const counts: Record<RuntimeChannelKind, number> = { api: 0, http: 0, acp: 0, cli: 0, tui: 0 }
  for (const provider of props.providers) {
    counts[providerRuntimeChannel(provider)] += 1
  }
  const tabs: Array<{ key: 'all' | RuntimeChannelKind; label: string; count: number }> = [
    { key: 'all', label: t('settings.runtimeChannelAll'), count: props.providers.length },
    { key: 'api', label: t('settings.runtimeChannelApi'), count: counts.api },
  ]
  if (counts.http > 0) {
    tabs.push({ key: 'http', label: t('settings.runtimeChannelHttp'), count: counts.http })
  }
  tabs.push(
    { key: 'acp', label: t('settings.runtimeChannelAcp'), count: counts.acp },
    { key: 'cli', label: t('settings.runtimeChannelCli'), count: counts.cli },
    { key: 'tui', label: t('settings.runtimeChannelTui'), count: counts.tui },
  )
  return tabs
})

const filteredNodeProviderOptions = computed(() => {
  const list = props.providers.filter((provider) => {
    if (provider.id === nodeFixedProviderId.value) return true
    if (nodeChannelFilter.value === 'all') return true
    return providerRuntimeChannel(provider) === nodeChannelFilter.value
  })
  return [
    { value: '', label: t('settings.selectProvider'), disabled: true },
    ...list.map((provider) => {
      const channel = providerRuntimeChannel(provider)
      return {
        value: provider.id,
        label: `[${runtimeChannelBadgeLabel(channel)}] ${provider.display_name}`,
      }
    }),
  ]
})

const nodeDuty = computed(() => {
  const rel = props.selectedNode?.relationship
  return rel && typeof rel.duty === 'string' ? rel.duty : ''
})

const nodeDispatchTargetsText = computed(() => {
  const rel = props.selectedNode?.relationship
  const arr = rel?.allowed_dispatch_targets
  return Array.isArray(arr) ? arr.filter((v): v is string => typeof v === 'string').join(', ') : ''
})

const nodeAgentTypesText = computed(() => {
  const rel = props.selectedNode?.relationship
  const arr = rel?.agent_types
  return Array.isArray(arr) ? arr.filter((v): v is string => typeof v === 'string').join(', ') : ''
})

// ── Inheritance vs Override Layering ─────────────────────────────────────────

const boundNodeAgent = computed<AgentDefinitionDto | null>(() => {
  const agentId = props.selectedNode?.agent_id || props.selectedNode?.agent_definition_id
  if (!agentId) return null
  return props.agents.find((a) => a.id === agentId || a.slug === agentId) ?? null
})

const agentDefaultLabel = computed(() =>
  boundNodeAgent.value ? agentDisplayName(boundNodeAgent.value) : '',
)

const isLabelOverridden = computed(() => {
  const raw = (props.selectedNode?.label ?? '').trim()
  return Boolean(raw) && raw !== agentDefaultLabel.value.trim()
})

function restoreInheritedLabel(): void {
  if (!props.selectedNode || props.readonly) return
  emit('update-node', { id: props.selectedNode.id, patch: { label: null } })
}

const isStrategyOverridden = computed(() =>
  Boolean(props.selectedNode?.model_strategy_override?.kind && props.selectedNode.model_strategy_override.kind !== 'inherit'),
)

const agentInheritedStrategySummary = computed(() => {
  const agent = boundNodeAgent.value
  if (!agent) return t('settings.runtimeInherit')
  const binding = agent.model_binding
  if (binding && binding.mode && binding.mode !== 'inherit') {
    if (binding.mode === 'fixed') {
      const providerName = binding.provider_instance_id
        ? props.providers.find((p) => p.id === binding.provider_instance_id)?.display_name
        : null
      return binding.model || providerName || binding.provider_instance_id || t('settings.runtimeFixedModel')
    }
    if (binding.mode === 'route') return binding.route_purpose || t('settings.runtimeRoute')
  }
  const strat = (agent.model_strategy && typeof agent.model_strategy === 'object')
    ? (agent.model_strategy as Record<string, unknown>)
    : null
  const kind = typeof strat?.kind === 'string' ? strat.kind : 'inherit'
  if (kind === 'fixed') {
    const providerId = typeof strat?.provider_instance_id === 'string' ? strat.provider_instance_id : null
    const providerName = providerId ? props.providers.find((p) => p.id === providerId)?.display_name : null
    return String(strat?.model || providerName || providerId || t('settings.runtimeFixedModel'))
  }
  if (kind === 'route') return String(strat?.route_purpose || agent.model_route_purpose || t('settings.runtimeRoute'))
  return t('settings.runtimeInherit')
})

const agentInheritedChannel = computed<RuntimeChannelKind | null>(() => {
  const agent = boundNodeAgent.value
  if (!agent) return null
  const bindingProviderId = agent.model_binding?.mode === 'fixed' ? agent.model_binding.provider_instance_id : null
  const strat = (agent.model_strategy && typeof agent.model_strategy === 'object')
    ? (agent.model_strategy as Record<string, unknown>)
    : null
  const stratProviderId = strat?.kind === 'fixed' && typeof strat.provider_instance_id === 'string'
    ? strat.provider_instance_id
    : null
  const providerId = bindingProviderId || stratProviderId
  if (!providerId) return null
  const provider = props.providers.find((p) => p.id === providerId)
  return provider ? providerRuntimeChannel(provider) : null
})

function restoreInheritedStrategy(): void {
  if (!props.selectedNode || props.readonly) return
  emit('update-node', { id: props.selectedNode.id, patch: { model_strategy_override: null } })
}

const parsedDraftBindings = computed<AgentModeBindingDto[] | null>(() => {
  try {
    const parsed = JSON.parse(props.draft.bindingsDraft.trim() || '[]')
    return Array.isArray(parsed) ? (parsed as AgentModeBindingDto[]) : null
  } catch {
    return null
  }
})

const selectedNodeKey = computed(() => props.selectedNode?.node_key ?? props.selectedNode?.id ?? '')

const selectedNodeBinding = computed<AgentModeBindingDto | null>(() => {
  if (!selectedNodeKey.value || !parsedDraftBindings.value) return null
  return parsedDraftBindings.value.find((b) => b.node_key === selectedNodeKey.value) ?? null
})

const isToolsOverridden = computed(() => {
  const b = selectedNodeBinding.value
  if (!b) return false
  const hasSwitches = Boolean(b.tool_switches && Object.keys(b.tool_switches).length > 0)
  const hasEnvelope = Boolean(b.envelope && Object.keys(b.envelope).length > 0)
  return hasSwitches || hasEnvelope
})

const isWildcardToolsDisabled = computed(() =>
  selectedNodeBinding.value?.tool_switches?.['*'] === false,
)

const agentInheritedToolsSummary = computed(() => {
  const agent = boundNodeAgent.value
  if (!agent) return '—'
  if (agent.tool_scope === '*' || agent.allowed_tools?.includes('*')) {
    return t('settings.modeAllTools')
  }
  const list = agent.allowed_tools ?? []
  if (list.length === 0) return '0'
  return list.length <= 3 ? list.join(', ') : `${list.slice(0, 3).join(', ')} (+${list.length - 3})`
})

function toggleWildcardToolsDisabled(disabled: boolean): void {
  if (!props.selectedNode || props.readonly || !parsedDraftBindings.value) return
  const key = selectedNodeKey.value
  const current = [...parsedDraftBindings.value]
  const idx = current.findIndex((b) => b.node_key === key)
  if (disabled) {
    if (idx >= 0) {
      const existing = current[idx]!
      current[idx] = {
        ...existing,
        tool_switches: { ...(existing.tool_switches ?? {}), '*': false },
      }
    } else {
      current.push({
        node_key: key,
        tool_switches: { '*': false },
        includes_core_reserved: false,
      })
    }
  } else if (idx >= 0) {
    const existing = current[idx]!
    const nextSwitches = { ...(existing.tool_switches ?? {}) }
    delete nextSwitches['*']
    const cleaned: AgentModeBindingDto = {
      ...existing,
      tool_switches: Object.keys(nextSwitches).length > 0 ? nextSwitches : undefined,
    }
    const hasOtherFields = Boolean(
      (cleaned.tool_switches && Object.keys(cleaned.tool_switches).length > 0)
      || (cleaned.envelope && Object.keys(cleaned.envelope).length > 0)
      || cleaned.duty_description_ref,
    )
    if (hasOtherFields) current[idx] = cleaned
    else current.splice(idx, 1)
  }
  emit('update-overview', { bindingsDraft: JSON.stringify(current, null, 2) }, `node-tools:${key}`)
}

function restoreInheritedTools(): void {
  if (!props.selectedNode || props.readonly || !parsedDraftBindings.value) return
  const key = selectedNodeKey.value
  const nextBindings = parsedDraftBindings.value.flatMap((b) => {
    if (b.node_key !== key) return [b]
    const { tool_switches: _ts, envelope: _env, ...rest } = b
    return rest.duty_description_ref ? [rest] : []
  })
  emit('update-overview', { bindingsDraft: JSON.stringify(nextBindings, null, 2) }, `node-tools-reset:${key}`)
}

const agentDefaultDuty = computed(() => {
  const agent = boundNodeAgent.value
  if (!agent) return ''
  return pickLocalized(agent.i18n, locale.value, 'description', agent.description) ?? agent.description ?? ''
})

const isDutyOverridden = computed(() => {
  const raw = nodeDuty.value.trim()
  return Boolean(raw) && raw !== agentDefaultDuty.value.trim()
})

function restoreInheritedDuty(): void {
  if (!props.selectedNode || props.readonly) return
  const nextRel: Record<string, unknown> = { ...(props.selectedNode.relationship ?? {}) }
  delete nextRel.duty
  emit('update-node', {
    id: props.selectedNode.id,
    patch: { relationship: Object.keys(nextRel).length > 0 ? nextRel : null },
  })
}

function handleNodeAgentChange(nextAgentId: string): void {
  if (!props.selectedNode || props.readonly) return
  const targetAgent = props.agents.find((a) => a.id === nextAgentId)
  if (!targetAgent) {
    emit('update-node', { id: props.selectedNode.id, patch: { agent_id: nextAgentId } })
    return
  }
  const targetLane = normalizeLane(targetAgent.layer)
  if (props.selectedNode.config?.conversation === true && targetLane !== 'operation') {
    notify.error(new Error(t('settings.conversationLayerRequired')))
    return
  }
  emit('update-node', {
    id: props.selectedNode.id,
    patch: {
      agent_id: nextAgentId,
      agent_definition_id: nextAgentId,
      lane: targetLane,
      layer: targetLane,
    },
  })
}

function handleNodeLaneChange(value: string): void {
  if (!props.selectedNode || props.readonly) return
  const lane = normalizeLane(value)
  const bound = props.agents.find((a) => a.id === props.selectedNode!.agent_id)
  if (bound && normalizeLane(bound.layer) !== lane) {
    notify.error(new Error(t('settings.laneAgentMismatch')))
    return
  }
  if (props.selectedNode.config?.conversation === true && lane !== 'operation') {
    notify.error(new Error(t('settings.conversationLayerRequired')))
    return
  }
  emit('update-node', { id: props.selectedNode.id, patch: { lane, layer: lane } })
}

function handleConversationToggle(checked: boolean): void {
  if (!props.selectedNode || props.readonly) return
  if (checked && normalizeLane(props.selectedNode.lane) !== 'operation') {
    notify.error(new Error(t('settings.conversationLayerRequired')))
    return
  }
  const nextConfig: Record<string, unknown> = { ...(props.selectedNode.config ?? {}) }
  if (checked) nextConfig.conversation = true
  else delete nextConfig.conversation
  emit('update-node', {
    id: props.selectedNode.id,
    patch: { config: Object.keys(nextConfig).length > 0 ? nextConfig : null },
    exclusiveConversation: checked,
  })
}

function handleNodeLabelInput(value: string | number): void {
  if (!props.selectedNode || props.readonly) return
  const str = String(value)
  emit('update-node', {
    id: props.selectedNode.id,
    patch: { label: str.trim() ? str : null },
    coalesceKey: `node-label:${props.selectedNode.id}`,
  })
}

function handleStrategyKindChange(kind: string): void {
  if (!props.selectedNode || props.readonly) return
  if (kind === 'route') {
    emit('update-node', {
      id: props.selectedNode.id,
      patch: {
        model_strategy_override: {
          kind: 'route',
          route_purpose: nodeRoutePurpose.value || props.routes[0]?.purpose || 'default',
        },
      },
    })
  } else if (kind === 'fixed') {
    emit('update-node', {
      id: props.selectedNode.id,
      patch: {
        model_strategy_override: {
          kind: 'fixed',
          provider_instance_id: nodeFixedProviderId.value || props.providers[0]?.id || '',
          model: nodeFixedModel.value || null,
        },
      },
    })
  } else {
    emit('update-node', {
      id: props.selectedNode.id,
      patch: { model_strategy_override: null },
    })
  }
}

function handleRoutePurposeChange(purpose: string): void {
  if (!props.selectedNode || props.readonly) return
  emit('update-node', {
    id: props.selectedNode.id,
    patch: { model_strategy_override: { kind: 'route', route_purpose: purpose } },
  })
}

function handleFixedProviderChange(providerId: string): void {
  if (!props.selectedNode || props.readonly) return
  const nextProvider = props.providers.find((provider) => provider.id === providerId)
  const nextIsHarness = Boolean(nextProvider && isExternalHarnessChannel(providerRuntimeChannel(nextProvider)))
  emit('update-node', {
    id: props.selectedNode.id,
    patch: {
      model_strategy_override: {
        kind: 'fixed',
        provider_instance_id: providerId,
        model: nextIsHarness ? null : (nodeFixedModel.value || null),
      },
    },
  })
}

function handleFixedModelInput(value: string | number): void {
  if (!props.selectedNode || props.readonly) return
  const model = String(value)
  emit('update-node', {
    id: props.selectedNode.id,
    patch: {
      model_strategy_override: {
        kind: 'fixed',
        provider_instance_id: nodeFixedProviderId.value,
        model: model || null,
      },
    },
    coalesceKey: `node-model:${props.selectedNode.id}`,
  })
}

function splitCommaList(raw: string): string[] {
  return raw
    .split(',')
    .map((s) => s.trim())
    .filter(Boolean)
}

function updateStructuredRelationship(patch: Record<string, unknown>, coalesceKey?: string): void {
  if (!props.selectedNode || props.readonly) return
  const base: Record<string, unknown> = { ...(props.selectedNode.relationship ?? {}), ...patch }
  emit('update-node', {
    id: props.selectedNode.id,
    patch: { relationship: base },
    coalesceKey,
  })
}

function handleRelationshipJsonInput(value: string): void {
  nodeRelationshipJson.value = value
  if (!props.selectedNode || props.readonly) return
  if (!value.trim()) {
    nodeRelationshipError.value = null
    emit('update-node', {
      id: props.selectedNode.id,
      patch: { relationship: null },
      coalesceKey: `node-rel:${props.selectedNode.id}`,
    })
    return
  }
  try {
    const parsed: unknown = JSON.parse(value)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      nodeRelationshipError.value = t('settings.jsonObjectRequired', { field: t('settings.nodeRelationshipField') })
      return
    }
    nodeRelationshipError.value = null
    const keys = Object.keys(parsed as Record<string, unknown>)
    emit('update-node', {
      id: props.selectedNode.id,
      patch: { relationship: keys.length > 0 ? (parsed as Record<string, unknown>) : null },
      coalesceKey: `node-rel:${props.selectedNode.id}`,
    })
  } catch (error) {
    nodeRelationshipError.value = error instanceof Error ? error.message : String(error)
  }
}

async function previewNodeStrategy(): Promise<void> {
  if (!props.selectedNode) return
  nodePreviewBusy.value = true
  nodePreviewSummary.value = ''
  try {
    const preview = await api.previewModelResolution({
      strategy: (props.selectedNode.model_strategy_override ?? null) as never,
      agent_definition_id: props.selectedNode.agent_id,
      mode_version_id: props.selectedModeId || null,
      node_key: props.selectedNode.node_key ?? props.selectedNode.id,
    })
    const sel = preview.expected_selection
    nodePreviewSummary.value = sel
      ? `${sel.provider_instance_id ?? '—'}${sel.model ? ` · ${sel.model}` : ''}`
      : t('settings.runtimeUnresolved')
  } catch (e) {
    nodePreviewSummary.value = e instanceof Error ? e.message : String(e)
  } finally {
    nodePreviewBusy.value = false
  }
}

// ── Edge Inspector helpers ───────────────────────────────────────────────────

const edgeConditionJson = ref('{}')
const edgeConditionError = ref<string | null>(null)
const edgeLabelLocal = computed(() => {
  const edge = props.selectedEdge
  if (!edge) return ''
  const condLabel = edge.condition?.label
  return edge.label ?? (typeof condLabel === 'string' ? condLabel : '')
})

watch(
  () => props.selectedEdge?.id,
  () => {
    const edge = props.selectedEdge
    edgeConditionJson.value = JSON.stringify(edge?.condition ?? (edge?.label ? { label: edge.label } : {}), null, 2)
    edgeConditionError.value = null
  },
  { immediate: true },
)

function handleEdgeLabelInput(value: string | number): void {
  if (!props.selectedEdge || props.readonly) return
  const label = String(value).trim()
  const baseCond: Record<string, unknown> = { ...(props.selectedEdge.condition ?? {}) }
  if (label) baseCond.label = label
  else delete baseCond.label
  const nextCond = Object.keys(baseCond).length > 0 ? baseCond : null
  edgeConditionJson.value = JSON.stringify(nextCond ?? {}, null, 2)
  edgeConditionError.value = null
  emit('update-edge', {
    id: props.selectedEdge.id,
    label: label || undefined,
    condition: nextCond,
    coalesceKey: `edge-label:${props.selectedEdge.id}`,
  })
}

function handleEdgeConditionInput(value: string): void {
  edgeConditionJson.value = value
  if (!props.selectedEdge || props.readonly) return
  if (!value.trim()) {
    edgeConditionError.value = null
    emit('update-edge', {
      id: props.selectedEdge.id,
      label: undefined,
      condition: null,
      coalesceKey: `edge-cond:${props.selectedEdge.id}`,
    })
    return
  }
  try {
    const parsed: unknown = JSON.parse(value)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      edgeConditionError.value = t('settings.jsonObjectRequired', { field: t('settings.edgeConditionField') })
      return
    }
    edgeConditionError.value = null
    const record = parsed as Record<string, unknown>
    const keys = Object.keys(record)
    const condLabel = typeof record.label === 'string' ? record.label : undefined
    emit('update-edge', {
      id: props.selectedEdge.id,
      label: condLabel,
      condition: keys.length > 0 ? record : null,
      coalesceKey: `edge-cond:${props.selectedEdge.id}`,
    })
  } catch (error) {
    edgeConditionError.value = error instanceof Error ? error.message : String(error)
  }
}

// ── Add Node panel helpers ───────────────────────────────────────────────────

const draftAddLane = ref<ModeLane>('operation')
const draftAddAgentId = ref('')
const draftAddLabel = ref('')

const addLaneAgentOptions = computed(() => {
  const pool = draftAddLane.value === 'execution'
    ? props.agents.filter((a) => normalizeLane(a.layer) === 'execution')
    : props.agents.filter((a) => normalizeLane(a.layer) !== 'execution')
  return pool.map((a) => ({ value: a.id, label: `${agentDisplayName(a)} (${layerLabel(a.layer)})` }))
})

watch(
  () => [props.panel, props.addNodeLane] as const,
  ([panel, lane]) => {
    if (panel === 'add-node') {
      draftAddLane.value = lane
      draftAddLabel.value = ''
      const pool = lane === 'execution'
        ? props.agents.filter((a) => normalizeLane(a.layer) === 'execution')
        : props.agents.filter((a) => normalizeLane(a.layer) !== 'execution')
      draftAddAgentId.value = pool[0]?.id ?? ''
    }
  },
  { immediate: true },
)

watch(draftAddLane, () => {
  if (!addLaneAgentOptions.value.some((opt) => opt.value === draftAddAgentId.value)) {
    draftAddAgentId.value = addLaneAgentOptions.value[0]?.value ?? ''
  }
})

function submitAddNode(): void {
  if (!draftAddAgentId.value || props.readonly) return
  emit('confirm-add-node', {
    agentId: draftAddAgentId.value,
    lane: draftAddLane.value,
    label: draftAddLabel.value.trim() || undefined,
  })
}
</script>

<template>
  <aside class="mode-inspector-drawer" data-testid="mode-inspector-drawer">
    <!-- 1. Add Node docked panel -->
    <template v-if="panel === 'add-node'">
      <header class="drawer-head">
        <div class="drawer-title">
          <Plus :size="15" />
          <div>
            <strong>{{ t('settings.addNodeAction') }}</strong>
            <small>{{ t('settings.nodeLane') }}</small>
          </div>
        </div>
        <button type="button" class="drawer-close" :aria-label="t('common.close')" @click="emit('close-panel')">
          <X :size="14" />
        </button>
      </header>
      <div class="drawer-body">
        <div class="drawer-field">
          <UiLabel>{{ t('settings.nodeLane') }}</UiLabel>
          <UiSelectField
            v-model="draftAddLane"
            :options="[
              { value: 'operation', label: t('agentCenter.filter.operationLayer') },
              { value: 'execution', label: t('agentCenter.filter.executionLayer') },
            ]"
          />
        </div>
        <div class="drawer-field">
          <UiLabel>{{ t('settings.boundAgent') }}</UiLabel>
          <UiSelectField v-model="draftAddAgentId" :options="addLaneAgentOptions" />
          <p v-if="addLaneAgentOptions.length === 0" class="quiet">{{ t('settings.modeNoAgentsInLane') }}</p>
        </div>
        <div class="drawer-field">
          <UiLabel>{{ t('settings.nodeLabelField') }}</UiLabel>
          <UiInput v-model="draftAddLabel" :placeholder="t('settings.nodeLabelPlaceholder')" />
        </div>
        <div class="drawer-actions">
          <UiButton size="sm" variant="outline" @click="emit('close-panel')">{{ t('settings.cancel') }}</UiButton>
          <UiButton size="sm" :disabled="!draftAddAgentId || readonly" @click="submitAddNode">
            <Plus :size="14" />{{ t('settings.addNodeAction') }}
          </UiButton>
        </div>
      </div>
    </template>

    <!-- 2. Version History docked panel -->
    <template v-else-if="panel === 'versions'">
      <header class="drawer-head">
        <div class="drawer-title">
          <History :size="15" />
          <div>
            <strong>{{ t('agentCenter.versionHistory') }}</strong>
            <small>{{ selectedMode?.display_name ?? '—' }}</small>
          </div>
        </div>
        <button type="button" class="drawer-close" :aria-label="t('common.close')" @click="emit('close-panel')">
          <X :size="14" />
        </button>
      </header>
      <div class="drawer-body">
        <UiButton size="sm" variant="outline" :disabled="modeVersionsLoading" @click="emit('refresh-versions')">
          {{ t('settings.refreshHistory') }}
        </UiButton>
        <div v-if="modeVersionsLoading" class="quiet">{{ t('common.loading') }}</div>
        <div v-else class="drawer-version-list">
          <div v-for="v in modeVersions" :key="v.id" class="drawer-version-row">
            <div>
              <strong>v{{ v.version }}</strong>
              <small>{{ v.created_at ? new Date(v.created_at).toLocaleString(locale) : '—' }}</small>
            </div>
            <UiBadge :variant="v.is_active ? 'default' : 'secondary'">
              {{ v.is_active ? t('settings.agentVersionCurrent') : t('settings.agentVersionArchived') }}
            </UiBadge>
            <UiButton
              v-if="v.snapshot"
              size="sm"
              variant="outline"
              :disabled="modeBusy"
              @click="emit('restore-version', v)"
            >
              {{ t('settings.restoreVersion') }}
            </UiButton>
          </div>
          <div v-if="modeVersions.length === 0" class="quiet">{{ t('settings.noModeVersions') }}</div>
        </div>
      </div>
    </template>

    <!-- 3. Node Inspector docked panel -->
    <template v-else-if="panel === 'node' && selectedNode">
      <header class="drawer-head">
        <div class="drawer-title">
          <Sliders :size="15" />
          <div>
            <strong>{{ t('settings.nodeConfigTitle', { id: selectedNode.node_key ?? selectedNode.id }) }}</strong>
            <small>{{ t('settings.nodeConfigHint') }}</small>
          </div>
        </div>
        <button type="button" class="drawer-close" :aria-label="t('common.close')" @click="emit('close-panel')">
          <X :size="14" />
        </button>
      </header>

      <div class="drawer-body">
        <div class="drawer-field">
          <UiLabel>{{ t('settings.boundAgent') }}</UiLabel>
          <UiSelectField
            :model-value="selectedNode.agent_id"
            :disabled="readonly"
            :options="nodeAgentOptions"
            @update:model-value="handleNodeAgentChange"
          />
        </div>

        <div class="drawer-field">
          <UiLabel>{{ t('settings.nodeLane') }}</UiLabel>
          <UiSelectField
            :model-value="normalizeLane(selectedNode.lane)"
            :disabled="readonly"
            :options="[
              { value: 'operation', label: t('agentCenter.filter.operationLayer') },
              { value: 'execution', label: t('agentCenter.filter.executionLayer') },
            ]"
            @update:model-value="handleNodeLaneChange"
          />
        </div>

        <label class="drawer-checkbox-row">
          <UiCheckbox
            :model-value="selectedNode.config?.conversation === true"
            :disabled="readonly"
            @update:model-value="handleConversationToggle"
          />
          <span>{{ t('settings.conversationNode') }}</span>
        </label>

        <div class="drawer-field">
          <div class="drawer-inherit-row">
            <UiLabel>{{ t('settings.nodeLabelField') }}</UiLabel>
            <span
              :class="['drawer-state-pill', isLabelOverridden ? 'overridden' : 'inherited']"
              data-testid="node-label-state"
            >
              {{ isLabelOverridden ? t('settings.nodeOverriddenBadge') : t('settings.nodeInheritedBadge') }}
            </span>
            <button
              v-if="isLabelOverridden && !readonly"
              type="button"
              class="drawer-restore-btn"
              data-testid="restore-inherit-label"
              @click="restoreInheritedLabel"
            >
              <RotateCcw :size="11" />{{ t('settings.nodeRestoreInherit') }}
            </button>
          </div>
          <UiInput
            :model-value="selectedNode.label ?? ''"
            :disabled="readonly"
            :placeholder="agentDefaultLabel || t('settings.nodeLabelPlaceholder')"
            @update:model-value="handleNodeLabelInput"
          />
        </div>

        <div :class="['drawer-section', { 'is-overridden': isStrategyOverridden }]" data-testid="node-strategy-section">
          <div class="drawer-inherit-row">
            <UiLabel>{{ t('settings.nodeStrategyOverride') }}</UiLabel>
            <span
              :class="['drawer-state-pill', isStrategyOverridden ? 'overridden' : 'inherited']"
              data-testid="node-strategy-state"
            >
              {{ isStrategyOverridden ? t('settings.nodeOverriddenBadge') : t('settings.nodeInheritedBadge') }}
            </span>
            <button
              v-if="isStrategyOverridden && !readonly"
              type="button"
              class="drawer-restore-btn"
              data-testid="restore-inherit-strategy"
              @click="restoreInheritedStrategy"
            >
              <RotateCcw :size="11" />{{ t('settings.nodeRestoreInherit') }}
            </button>
          </div>
          <div class="drawer-inherited-line">
            <span
              v-if="agentInheritedChannel"
              :class="['runtime-channel-badge', `channel-${agentInheritedChannel}`]"
              data-testid="node-inherited-channel-badge"
            >
              {{ runtimeChannelBadgeLabel(agentInheritedChannel) }}
            </span>
            <small class="quiet">{{ t('settings.nodeInheritedStrategySummary', { summary: agentInheritedStrategySummary }) }}</small>
          </div>
          <UiSelectField
            :model-value="nodeStrategyKind"
            :disabled="readonly"
            :options="[
              { value: 'inherit', label: t('settings.runtimeInherit') },
              { value: 'route', label: t('settings.runtimeRoute') },
              { value: 'fixed', label: t('settings.runtimeFixedModel') },
            ]"
            @update:model-value="handleStrategyKindChange"
          />
          <div v-if="nodeStrategyKind === 'route'" class="drawer-field">
            <UiLabel>{{ t('settings.routePurpose') }}</UiLabel>
            <UiSelectField
              :model-value="nodeRoutePurpose"
              :disabled="readonly"
              :options="[
                { value: '', label: t('settings.selectRoutePurpose'), disabled: true },
                ...routes.map((route) => ({ value: route.purpose, label: route.purpose })),
              ]"
              @update:model-value="handleRoutePurposeChange"
            />
          </div>
          <div v-else-if="nodeStrategyKind === 'fixed'" class="drawer-field">
            <div class="drawer-provider-head">
              <UiLabel>{{ t('settings.selectProvider') }}</UiLabel>
              <span
                v-if="selectedFixedChannel"
                :class="['runtime-channel-badge', `channel-${selectedFixedChannel}`]"
                data-testid="node-selected-channel-badge"
              >
                {{ runtimeChannelBadgeLabel(selectedFixedChannel) }}
              </span>
            </div>
            <div
              v-if="providers.length > 0"
              class="drawer-channel-filter"
              role="group"
              :aria-label="t('settings.runtimeChannelFilter')"
              data-testid="node-runtime-channel-filter"
            >
              <button
                v-for="tab in nodeProviderChannelTabs"
                :key="tab.key"
                type="button"
                class="drawer-channel-btn"
                :class="{ active: nodeChannelFilter === tab.key }"
                :aria-pressed="nodeChannelFilter === tab.key"
                :data-channel-filter="tab.key"
                @click="nodeChannelFilter = tab.key"
              >
                <span>{{ tab.label }}</span>
                <em>{{ tab.count }}</em>
              </button>
            </div>
            <UiSelectField
              :model-value="nodeFixedProviderId"
              :disabled="readonly"
              :options="filteredNodeProviderOptions"
              data-testid="node-fixed-provider-select"
              @update:model-value="handleFixedProviderChange"
            />
            <div v-if="selectedFixedIsHarness && selectedFixedProvider" class="drawer-harness-meta" data-testid="node-harness-meta">
              <Terminal :size="12" aria-hidden="true" />
              <span :class="['runtime-channel-badge', `channel-${selectedFixedChannel}`]">
                {{ runtimeChannelBadgeLabel(selectedFixedChannel!) }}
              </span>
              <code :title="selectedFixedProvider.binary_path || selectedFixedProvider.driver">
                {{ selectedFixedProvider.binary_path || selectedFixedProvider.driver }}
              </code>
            </div>
            <UiInput
              :model-value="nodeFixedModel"
              :disabled="readonly"
              :placeholder="selectedFixedIsHarness ? t('settings.runtimeHarnessModelOptional') : t('settings.routeModel')"
              @update:model-value="handleFixedModelInput"
            />
          </div>
          <div v-if="nodePreviewSummary" class="drawer-preview quiet">{{ nodePreviewSummary }}</div>
          <UiButton
            size="sm"
            variant="outline"
            :disabled="nodePreviewBusy || nodeStrategyKind === 'inherit'"
            @click="previewNodeStrategy"
          >
            {{ t('settings.previewNodeStrategy') }}
          </UiButton>
        </div>

        <div :class="['drawer-section', { 'is-overridden': isToolsOverridden }]" data-testid="node-tools-section">
          <div class="drawer-inherit-row">
            <UiLabel>{{ t('settings.nodeToolSwitchesTitle') }}</UiLabel>
            <span
              :class="['drawer-state-pill', isToolsOverridden ? 'overridden' : 'inherited']"
              data-testid="node-tools-state"
            >
              {{ isToolsOverridden ? t('settings.nodeOverriddenBadge') : t('settings.nodeInheritedBadge') }}
            </span>
            <button
              v-if="isToolsOverridden && !readonly"
              type="button"
              class="drawer-restore-btn"
              data-testid="restore-inherit-tools"
              @click="restoreInheritedTools"
            >
              <RotateCcw :size="11" />{{ t('settings.nodeRestoreInherit') }}
            </button>
          </div>
          <small class="quiet">{{ t('settings.nodeInheritedToolsSummary', { summary: agentInheritedToolsSummary }) }}</small>
          <label class="drawer-checkbox-row" data-testid="node-wildcard-tools-switch">
            <UiCheckbox
              :model-value="isWildcardToolsDisabled"
              :disabled="readonly || !parsedDraftBindings"
              @update:model-value="toggleWildcardToolsDisabled"
            />
            <span>{{ t('settings.nodeDisableWildcardTools') }}</span>
          </label>
        </div>

        <div :class="['drawer-section', { 'is-overridden': isDutyOverridden }]" data-testid="node-duty-section">
          <div class="drawer-field">
            <div class="drawer-inherit-row">
              <UiLabel>{{ t('settings.nodeDutyField') }}</UiLabel>
              <span
                :class="['drawer-state-pill', isDutyOverridden ? 'overridden' : 'inherited']"
                data-testid="node-duty-state"
              >
                {{ isDutyOverridden ? t('settings.nodeOverriddenBadge') : t('settings.nodeInheritedBadge') }}
              </span>
              <button
                v-if="isDutyOverridden && !readonly"
                type="button"
                class="drawer-restore-btn"
                data-testid="restore-inherit-duty"
                @click="restoreInheritedDuty"
              >
                <RotateCcw :size="11" />{{ t('settings.nodeRestoreInherit') }}
              </button>
            </div>
            <small v-if="agentDefaultDuty" class="quiet">{{ t('settings.nodeInheritedDutySummary', { summary: agentDefaultDuty }) }}</small>
            <UiTextarea
              :model-value="nodeDuty"
              :rows="2"
              :disabled="readonly"
              :placeholder="agentDefaultDuty || undefined"
              @update:model-value="(val: string) => updateStructuredRelationship({ duty: val }, `node-duty:${selectedNode!.id}`)"
            />
          </div>
          <div class="drawer-field">
            <UiLabel>{{ t('settings.nodeDispatchTargetsField') }}</UiLabel>
            <UiInput
              :model-value="nodeDispatchTargetsText"
              :disabled="readonly"
              placeholder="search, global_engineering"
              @update:model-value="(val: string | number) => updateStructuredRelationship({ allowed_dispatch_targets: splitCommaList(String(val)) }, `node-targets:${selectedNode!.id}`)"
            />
          </div>
          <div class="drawer-field">
            <UiLabel>{{ t('settings.nodeAgentTypesField') }}</UiLabel>
            <UiInput
              :model-value="nodeAgentTypesText"
              :disabled="readonly"
              placeholder="search, global_engineering"
              @update:model-value="(val: string | number) => updateStructuredRelationship({ agent_types: splitCommaList(String(val)) }, `node-types:${selectedNode!.id}`)"
            />
          </div>
          <details class="drawer-advanced">
            <summary>{{ t('settings.nodeAdvancedRelationshipJson') }}</summary>
            <p class="quiet">{{ t('settings.nodeRelationshipHint') }}</p>
            <textarea
              :value="nodeRelationshipJson"
              class="drawer-json"
              rows="5"
              spellcheck="false"
              :disabled="readonly"
              @input="handleRelationshipJsonInput(($event.target as HTMLTextAreaElement).value)"
            />
            <p v-if="nodeRelationshipError" class="drawer-error">{{ nodeRelationshipError }}</p>
          </details>
        </div>

        <div class="drawer-actions">
          <UiButton
            size="sm"
            variant="ghost"
            class="drawer-danger-btn"
            :disabled="readonly"
            @click="emit('delete-node', selectedNode.id)"
          >
            <Trash2 :size="14" />{{ t('settings.deleteAction') }}
          </UiButton>
        </div>
      </div>
    </template>

    <!-- 4. Edge Inspector docked panel -->
    <template v-else-if="panel === 'edge' && selectedEdge">
      <header class="drawer-head">
        <div class="drawer-title">
          <Sliders :size="15" />
          <div>
            <strong>{{ t('settings.edgeConfigTitle') }}</strong>
            <small>{{ selectedEdge.source }} → {{ selectedEdge.target }}</small>
          </div>
        </div>
        <button type="button" class="drawer-close" :aria-label="t('common.close')" @click="emit('close-panel')">
          <X :size="14" />
        </button>
      </header>

      <div class="drawer-body">
        <div class="drawer-field">
          <UiLabel>{{ t('settings.edgeLabelField') }}</UiLabel>
          <UiInput
            :model-value="edgeLabelLocal"
            :disabled="readonly"
            :placeholder="t('settings.edgeLabelPlaceholder')"
            @update:model-value="handleEdgeLabelInput"
          />
        </div>
        <div class="drawer-field">
          <UiLabel>{{ t('settings.edgeConditionField') }}</UiLabel>
          <textarea
            :value="edgeConditionJson"
            class="drawer-json"
            rows="6"
            spellcheck="false"
            :disabled="readonly"
            @input="handleEdgeConditionInput(($event.target as HTMLTextAreaElement).value)"
          />
          <p v-if="edgeConditionError" class="drawer-error">{{ edgeConditionError }}</p>
        </div>
        <div class="drawer-actions">
          <UiButton
            size="sm"
            variant="ghost"
            class="drawer-danger-btn"
            :disabled="readonly"
            @click="emit('delete-edge', selectedEdge.id)"
          >
            <Trash2 :size="14" />{{ t('settings.deleteAction') }}
          </UiButton>
        </div>
      </div>
    </template>

    <!-- 5. Mode Overview docked panel (default when canvas background is active) -->
    <template v-else>
      <header class="drawer-head">
        <div class="drawer-title">
          <Settings2 :size="15" />
          <div>
            <strong>{{ t('settings.modeOverview') }}</strong>
            <small>{{ t('settings.modeOverviewHint') }}</small>
          </div>
        </div>
      </header>

      <div v-if="!selectedModeId" class="drawer-empty quiet">
        {{ t('settings.modeInspectorEmpty') }}
      </div>
      <div v-else class="drawer-body">
        <div class="drawer-field">
          <UiLabel>{{ t('settings.modeNameField') }}</UiLabel>
          <UiInput
            :model-value="draft.displayName"
            :disabled="readonly"
            :placeholder="t('settings.modeNameField')"
            @update:model-value="(val: string | number) => emit('update-overview', { displayName: String(val) }, 'mode-name')"
          />
        </div>

        <div class="drawer-field">
          <UiLabel>{{ t('settings.modeDescriptionField') }}</UiLabel>
          <UiInput
            :model-value="draft.description"
            :disabled="readonly"
            :placeholder="t('settings.modeDescriptionField')"
            @update:model-value="(val: string | number) => emit('update-overview', { description: String(val) }, 'mode-desc')"
          />
        </div>

        <div class="drawer-field">
          <UiLabel>{{ t('settings.modeOrchestrationTier') }}</UiLabel>
          <UiSelectField
            v-model="selectedTierValue"
            :disabled="readonly"
            :options="tierOptions"
          />
        </div>

        <div class="drawer-field">
          <UiLabel>{{ t('settings.modeInstructionsField') }}</UiLabel>
          <UiTextarea
            :model-value="draft.instructions"
            :rows="4"
            maxlength="4000"
            :disabled="readonly"
            :placeholder="t('settings.modeInstructionsPlaceholder')"
            @update:model-value="(val: string) => emit('update-overview', { instructions: val }, 'mode-instructions')"
          />
          <small class="quiet">{{ draft.instructions.length }} / 4000</small>
        </div>

        <div class="drawer-section" data-testid="mode-default-options">
          <UiLabel>{{ t('settings.modeDefaultOptions') }}</UiLabel>
          <p class="quiet">{{ t('settings.modeDefaultOptionsHint') }}</p>
          <div class="drawer-features">
            <label
              v-for="opt in DEFAULT_OPTION_ITEMS"
              :key="opt.key"
              class="drawer-checkbox-row"
              :data-testid="`mode-default-opt-${opt.key}`"
            >
              <UiCheckbox
                :model-value="isDefaultOptionEnabled(opt.key)"
                :disabled="readonly"
                :aria-label="t(opt.labelKey)"
                @update:model-value="(checked: boolean) => toggleDefaultOption(opt.key, checked)"
              />
              <span>{{ t(opt.labelKey) }}</span>
            </label>
          </div>
        </div>

        <div class="drawer-section" data-testid="mode-context-strategy-host">
          <ModeContextStrategySection
            compact
            allow-inherit
            :readonly="readonly"
            :strategy="modeContextStrategy"
            :advanced="modeContextAdvanced"
            @update:strategy="updateModeContextStrategy"
            @update:advanced="updateModeContextAdvanced"
            @reset="resetModeContextStrategy"
          />
        </div>

        <div class="drawer-section">
          <UiLabel>{{ t('settings.runtimePolicy') }}</UiLabel>
          <UiSelectField
            :model-value="draft.runtimePolicyKind"
            :disabled="readonly"
            :options="policyKindOptions"
            @update:model-value="emit('set-policy-kind', $event)"
          />
          <template v-if="selectedPolicyDefinition">
            <div class="drawer-features">
              <UiLabel>{{ t('settings.runtimePolicyFeatures') }}</UiLabel>
              <label
                v-for="feature in selectedPolicyDefinition.features"
                :key="feature"
                class="drawer-checkbox-row"
              >
                <UiCheckbox
                  :model-value="draft.runtimePolicyFeatures.includes(feature)"
                  :disabled="readonly || selectedPolicyDefinition.required_features.includes(feature)"
                  :aria-label="feature"
                  @update:model-value="(checked: boolean) => emit('toggle-policy-feature', feature, checked)"
                />
                <span>{{ feature }}</span>
                <UiBadge v-if="selectedPolicyDefinition.required_features.includes(feature)" variant="outline">
                  {{ t('settings.runtimePolicyRequiredFeature') }}
                </UiBadge>
              </label>
              <p v-if="selectedPolicyDefinition.dependencies.length" class="quiet">
                {{ t('settings.runtimePolicyDependenciesHint', {
                  pairs: selectedPolicyDefinition.dependencies.map((d) => `${d.feature} → ${d.requires}`).join(', '),
                }) }}
              </p>
            </div>
            <div v-if="draft.runtimePolicyKind === 'autonomous_exploration'" class="drawer-field">
              <UiLabel>{{ t('settings.runtimePolicySettings') }}</UiLabel>
              <UiTextarea
                :model-value="draft.runtimePolicySettingsJson"
                :rows="6"
                :disabled="readonly"
                placeholder='{ "max_parallel_workers": 4 }'
                @update:model-value="(val: string) => emit('update-overview', { runtimePolicySettingsJson: val }, 'mode-policy-json')"
              />
              <p v-if="policySettingsError" class="drawer-error">{{ policySettingsError }}</p>
            </div>
          </template>
        </div>

        <details class="drawer-advanced">
          <summary>{{ t('settings.bindingsAdvanced') }}</summary>
          <p class="quiet">{{ t('settings.bindingsAdvancedHint') }}</p>
          <textarea
            :value="draft.bindingsDraft"
            class="drawer-json"
            rows="6"
            spellcheck="false"
            :disabled="readonly"
            @input="emit('update-overview', { bindingsDraft: ($event.target as HTMLTextAreaElement).value }, 'mode-bindings')"
          />
          <p v-if="bindingsError" class="drawer-error">{{ bindingsError }}</p>
        </details>
      </div>
    </template>
  </aside>
</template>

<style scoped>
.mode-inspector-drawer {
  display: flex;
  flex-direction: column;
  min-width: 0;
  height: 100%;
  min-height: 0;
  border-radius: 10px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  overflow: hidden;
}

.drawer-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
  padding: 12px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--surface-section);
  flex-shrink: 0;
}

.drawer-title {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  min-width: 0;
}

.drawer-title > svg {
  margin-top: 2px;
  color: var(--text-secondary);
  flex-shrink: 0;
}

.drawer-title > div {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.drawer-title strong {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.drawer-title small {
  font-size: 11px;
  color: var(--text-muted);
  line-height: 1.35;
}

.drawer-close {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  flex-shrink: 0;
}

.drawer-close:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.drawer-close:focus-visible,
.drawer-restore-btn:focus-visible,
.drawer-channel-btn:focus-visible,
.drawer-advanced summary:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.drawer-inherited-line {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.drawer-provider-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.drawer-channel-filter {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
}

.drawer-channel-btn {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 4px;
  height: 24px;
  padding: 0 7px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease, color 120ms ease;
}

.drawer-channel-btn:hover {
  border-color: var(--border-input-focus);
  color: var(--text-primary);
}

.drawer-channel-btn.active {
  border-color: color-mix(in srgb, var(--accent-primary) 50%, var(--border-muted));
  background: var(--surface-selected);
  color: var(--text-primary);
  font-weight: 600;
}

.drawer-channel-btn em {
  font-style: normal;
  font-size: 10px;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.drawer-harness-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  padding: 5px 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
}

.drawer-harness-meta > svg {
  flex-shrink: 0;
  color: var(--text-muted);
}

.drawer-harness-meta code {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono);
  font-size: 11px;
}

.drawer-body {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.drawer-empty {
  padding: 20px 14px;
  font-size: 12px;
  line-height: 1.5;
}

.drawer-field {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.drawer-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  transition: border-color 140ms ease, background-color 140ms ease;
}

.drawer-section.is-overridden {
  border-color: color-mix(in srgb, var(--accent-primary) 45%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 4%, var(--surface-section));
}

.drawer-inherit-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}

.drawer-state-pill {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  padding: 1px 7px;
  border-radius: 999px;
  font-size: 10px;
  line-height: 1.45;
  white-space: nowrap;
  border: 1px solid var(--border-muted);
}

.drawer-state-pill.inherited {
  background: var(--surface-raised);
  color: var(--text-muted);
}

.drawer-state-pill.overridden {
  border-color: color-mix(in srgb, var(--accent-primary) 50%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised));
  color: var(--accent-primary);
  font-weight: 500;
}

.drawer-restore-btn {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 4px;
  padding: 2px 7px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 10px;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color 120ms ease, color 120ms ease, background-color 120ms ease;
}

.drawer-restore-btn:hover {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: var(--surface-hover);
}

.drawer-features {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.drawer-checkbox-row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  color: var(--text-secondary);
  cursor: pointer;
}

.drawer-advanced {
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.drawer-advanced summary {
  cursor: pointer;
  font-size: 12px;
  font-weight: 500;
  color: var(--text-secondary);
}

.drawer-advanced p {
  margin: 6px 0 4px;
  font-size: 11px;
}

.drawer-json {
  width: 100%;
  margin-top: 6px;
  padding: 8px;
  border: 1px solid var(--border-input);
  border-radius: var(--radius-control, 6px);
  background: var(--surface-input);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 11px;
  line-height: 1.45;
  resize: vertical;
}

.drawer-json:focus-visible {
  outline: none;
  border-color: var(--border-input-focus);
  box-shadow: var(--focus-ring, 0 0 0 2px color-mix(in srgb, var(--accent-primary) 28%, transparent));
}

.drawer-error {
  margin: 4px 0 0;
  color: var(--accent-danger, #ef4444);
  font-size: 11px;
}

.drawer-preview {
  font-size: 11px;
  word-break: break-word;
}

.drawer-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding-top: 4px;
}

.drawer-danger-btn {
  color: var(--accent-danger, #ef4444);
}

.drawer-version-list {
  display: grid;
  gap: 8px;
}

.drawer-version-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.drawer-version-row > div {
  display: grid;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.drawer-version-row strong {
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.drawer-version-row small {
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  color: var(--text-muted);
}

.quiet {
  color: var(--text-muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}
</style>
