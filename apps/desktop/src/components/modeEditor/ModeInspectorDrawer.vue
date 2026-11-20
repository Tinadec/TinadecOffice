<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { History, Plus, Settings2, Sliders, Trash2, X } from '@lucide/vue'
import { UiBadge, UiButton, UiCheckbox, UiInput, UiLabel, UiSelectField, UiTextarea } from '@/components/ui'
import {
  api as baseApi,
  type AgentDefinitionDto,
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

watch(
  () => props.selectedNode?.id,
  () => {
    nodeRelationshipJson.value = JSON.stringify(props.selectedNode?.relationship ?? {}, null, 2)
    nodeRelationshipError.value = null
    nodePreviewSummary.value = ''
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
  emit('update-node', {
    id: props.selectedNode.id,
    patch: {
      model_strategy_override: {
        kind: 'fixed',
        provider_instance_id: providerId,
        model: nodeFixedModel.value || null,
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
          <UiLabel>{{ t('settings.nodeLabelField') }}</UiLabel>
          <UiInput
            :model-value="selectedNode.label ?? ''"
            :disabled="readonly"
            :placeholder="t('settings.nodeLabelPlaceholder')"
            @update:model-value="handleNodeLabelInput"
          />
        </div>

        <div class="drawer-section">
          <UiLabel>{{ t('settings.nodeStrategyOverride') }}</UiLabel>
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
            <UiLabel>{{ t('settings.selectProvider') }}</UiLabel>
            <UiSelectField
              :model-value="nodeFixedProviderId"
              :disabled="readonly"
              :options="[
                { value: '', label: t('settings.selectProvider'), disabled: true },
                ...providers.map((provider) => ({ value: provider.id, label: provider.display_name })),
              ]"
              @update:model-value="handleFixedProviderChange"
            />
            <UiInput
              :model-value="nodeFixedModel"
              :disabled="readonly"
              :placeholder="t('settings.routeModel')"
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

        <div class="drawer-section">
          <div class="drawer-field">
            <UiLabel>{{ t('settings.nodeDutyField') }}</UiLabel>
            <UiTextarea
              :model-value="nodeDuty"
              :rows="2"
              :disabled="readonly"
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
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  background: var(--surface-input);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 11px;
  line-height: 1.45;
  resize: vertical;
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
}

.drawer-version-row small {
  font-size: 11px;
  color: var(--text-muted);
}

.quiet {
  color: var(--text-muted);
  font-size: 11px;
}
</style>
