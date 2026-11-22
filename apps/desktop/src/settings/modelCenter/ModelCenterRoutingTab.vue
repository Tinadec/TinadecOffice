<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, ChevronRight, Coins, Plus, Route, Save, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiSelectField } from '@/components/ui'
import type {
  AgentRuntimePolicyDto,
  ModelCenterOverviewDto,
  ModelCostTier,
  ModelProviderInstanceDto,
  ModelRouteDto,
} from '@/api'
import {
  providerRuntimeChannel,
  runtimeChannelBadgeLabel,
  type RuntimeChannelKind,
} from '@/settings/agentCenter/directory/agentDirectoryHelpers'

const props = defineProps<{
  routes: ModelRouteDto[]
  providers: ModelProviderInstanceDto[]
  overview: ModelCenterOverviewDto | null
  runtimeCliOptions: ModelCenterOverviewDto['cli_runtimes']
  runtimeAcpOptions: ModelCenterOverviewDto['acp_runtimes']
  runtimePolicy: AgentRuntimePolicyDto | null
  defaultChatValue: string
  defaultChatLabel: string
  defaultChatHasCandidate: boolean
  defaultChatOptions: Array<{ value: string; label: string }>
  busy: boolean
  modelsForProvider: (providerId: string) => ModelCenterOverviewDto['models']
  saveRouteCandidates: (
    purpose: string,
    candidates: Array<{ provider_instance_id: string; model: string | null }>,
  ) => Promise<boolean>
}>()

const emit = defineEmits<{
  'change-default-chat': [value: string]
}>()

const { t } = useI18n()

const sortedRoutes = computed(() => [
  ...props.routes.filter((route) => route.purpose === 'chat'),
  ...props.routes.filter((route) => route.purpose !== 'chat'),
])

const routeEditorPurpose = ref('')
const routeEditorCandidates = ref<Array<{ provider_instance_id: string; model: string | null }>>([])
const routeEditorBusy = ref(false)
const routeEditorAdding = ref(false)
const routeEditorNewProviderId = ref('')
const routeEditorNewModelId = ref('')

const routeEditorRoute = computed(() =>
  props.routes.find((route) => route.purpose === routeEditorPurpose.value) ?? null,
)
const routeEditorDirty = computed(() => {
  const route = routeEditorRoute.value
  if (!route) return false
  const current = route.candidates?.map((candidate) => ({
    provider_instance_id: candidate.provider_instance_id,
    model: candidate.model ?? null,
  })) ?? []
  const editing = routeEditorCandidates.value
  return current.length !== editing.length
    || current.some((candidate, index) =>
      candidate.provider_instance_id !== editing[index]?.provider_instance_id
      || (candidate.model ?? null) !== (editing[index]?.model ?? null))
})

function openRouteEditor(purpose: string) {
  const route = props.routes.find((item) => item.purpose === purpose)
  routeEditorPurpose.value = purpose
  routeEditorCandidates.value = (route?.candidates ?? []).map((candidate) => ({
    provider_instance_id: candidate.provider_instance_id,
    model: candidate.model ?? null,
  }))
  routeEditorAdding.value = false
  routeEditorNewProviderId.value = ''
  routeEditorNewModelId.value = ''
}

function routeCandidateProviderLabel(providerInstanceId: string): string {
  const provider = props.providers.find((item) => item.id === providerInstanceId)
  if (provider) return provider.display_name
  const cli = props.runtimeCliOptions.find((item) => item.provider_instance_id === providerInstanceId)
  if (cli) return `${cli.display_name} (CLI)`
  const acp = props.runtimeAcpOptions.find((item) => item.provider_instance_id === providerInstanceId)
  if (acp) return `${acp.display_name} (ACP)`
  return providerInstanceId
}

function routeCandidateChannel(providerInstanceId: string): RuntimeChannelKind {
  if (props.runtimeAcpOptions.some((item) => item.provider_instance_id === providerInstanceId || item.runtime_id === providerInstanceId)) {
    return 'acp'
  }
  if (props.runtimeCliOptions.some((item) => item.provider_instance_id === providerInstanceId)) {
    return 'cli'
  }
  const provider = props.providers.find((item) => item.id === providerInstanceId)
  return provider ? providerRuntimeChannel(provider) : 'api'
}

function moveRouteCandidate(index: number, delta: -1 | 1) {
  const target = index + delta
  if (target < 0 || target >= routeEditorCandidates.value.length) return
  const candidates = [...routeEditorCandidates.value]
  const [moved] = candidates.splice(index, 1)
  candidates.splice(target, 0, moved)
  routeEditorCandidates.value = candidates
}

function removeRouteCandidate(index: number) {
  routeEditorCandidates.value = routeEditorCandidates.value.filter((_, i) => i !== index)
}

function addRouteCandidate() {
  const providerInstanceId = routeEditorNewProviderId.value
  if (!providerInstanceId) return
  routeEditorCandidates.value = [
    ...routeEditorCandidates.value,
    { provider_instance_id: providerInstanceId, model: routeEditorNewModelId.value || null },
  ]
  routeEditorAdding.value = false
  routeEditorNewProviderId.value = ''
  routeEditorNewModelId.value = ''
}

async function handleSaveRoute() {
  if (!routeEditorPurpose.value) return
  routeEditorBusy.value = true
  try {
    if (await props.saveRouteCandidates(routeEditorPurpose.value, routeEditorCandidates.value)) {
      openRouteEditor(routeEditorPurpose.value)
    }
  } finally {
    routeEditorBusy.value = false
  }
}

const costDistribution = computed(() => {
  const counts: Record<ModelCostTier | 'unset', number> = { low: 0, medium: 0, high: 0, unset: 0 }
  const providerById = new Map(props.providers.map((p) => [p.id, p]))
  for (const model of props.overview?.models ?? []) {
    const profile = providerById.get(model.provider_instance_id)?.model_profiles?.[model.model_id]
    const cost = profile?.cost_tier
    if (cost === 'low' || cost === 'medium' || cost === 'high') counts[cost] += 1
    else counts.unset += 1
  }
  return counts
})
</script>

<template>
  <section class="center-resource-section routing-policy-tab" data-testid="model-center-routing-tab">
    <!-- 1. Default Chat Model Policy -->
    <div class="routing-default-card">
      <div class="routing-default-info">
        <div class="routing-default-title">
          <Route :size="15" />
          <strong>{{ t('settings.defaultChatModel') }}</strong>
        </div>
        <p class="quiet">{{ t('settings.centerRoutesHint') }}</p>
      </div>
      <div class="routing-default-control">
        <UiSelectField
          v-if="defaultChatOptions.length > 0"
          class="model-center-default-select"
          :model-value="defaultChatValue"
          :options="defaultChatOptions"
          :aria-label="t('settings.defaultChatModel')"
          :placeholder="t('settings.defaultChatNone')"
          :disabled="busy"
          @update:model-value="emit('change-default-chat', $event)"
        />
        <strong v-else-if="defaultChatHasCandidate" class="model-center-default-value">{{ defaultChatLabel }}</strong>
        <span v-else class="quiet">{{ t('settings.defaultChatNone') }}</span>
      </div>
    </div>

    <!-- 2. Capability Fallback Chains -->
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.centerRoutes') }}</h3>
        <p>{{ t('settings.centerRoutesHint') }}</p>
      </div>
      <UiBadge variant="outline">{{ routes.length }}</UiBadge>
    </div>

    <div
      v-for="route in sortedRoutes"
      :key="route.id ?? route.purpose"
      class="center-resource-list-row model-center-route-row"
      :class="{ active: routeEditorPurpose === route.purpose, default: route.purpose === 'chat' }"
    >
      <button type="button" class="agent-card-select" @click="openRouteEditor(route.purpose)">
        <Route :size="15" />
        <div class="center-resource-primary">
          <strong>
            {{ route.purpose }}
            <span v-if="route.purpose === 'chat'" class="provider-cap-tag">{{ t('settings.defaultChatModel') }}</span>
          </strong>
          <span>
            {{ (route.candidates ?? []).map((candidate) => `${routeCandidateProviderLabel(candidate.provider_instance_id)}${candidate.model ? ` · ${candidate.model}` : ''}`).join(' → ') || t('settings.routeNoCandidates') }}
          </span>
        </div>
      </button>
    </div>

    <div v-if="routes.length === 0" class="center-empty-state">
      <Route :size="20" />
      <span>{{ t('settings.routeNoCandidates') }}</span>
    </div>

    <div v-if="routeEditorPurpose" class="route-candidate-editor">
      <div class="route-candidate-head">
        <strong>{{ routeEditorPurpose }}</strong>
        <span>{{ t('settings.routeEditorHint') }}</span>
      </div>
      <div
        v-for="(candidate, index) in routeEditorCandidates"
        :key="`${candidate.provider_instance_id}:${index}`"
        class="route-candidate-row"
      >
        <UiBadge variant="secondary">{{ index + 1 }}</UiBadge>
        <div class="route-candidate-main">
          <strong class="route-candidate-title">
            <span :class="['runtime-channel-badge', `channel-${routeCandidateChannel(candidate.provider_instance_id)}`]">
              {{ runtimeChannelBadgeLabel(routeCandidateChannel(candidate.provider_instance_id)) }}
            </span>
            <span>{{ routeCandidateProviderLabel(candidate.provider_instance_id) }}</span>
          </strong>
          <span :title="candidate.model ?? undefined">{{ candidate.model ?? t('settings.fixedModelNoModel') }}</span>
        </div>
        <UiButton
          variant="ghost"
          size="icon"
          :disabled="routeEditorBusy || index === 0"
          :title="t('settings.routeCandidateUp')"
          @click="moveRouteCandidate(index, -1)"
        >
          <ChevronRight :size="14" class="rotate-[-90deg]" />
        </UiButton>
        <UiButton
          variant="ghost"
          size="icon"
          :disabled="routeEditorBusy || index === routeEditorCandidates.length - 1"
          :title="t('settings.routeCandidateDown')"
          @click="moveRouteCandidate(index, 1)"
        >
          <ChevronRight :size="14" class="rotate-90" />
        </UiButton>
        <UiButton
          variant="ghost"
          size="icon"
          :disabled="routeEditorBusy"
          :title="t('settings.delete')"
          @click="removeRouteCandidate(index)"
        >
          <Trash2 :size="14" />
        </UiButton>
      </div>
      <div v-if="routeEditorCandidates.length === 0" class="center-empty-state">
        <Route :size="20" />
        <span>{{ t('settings.routeNoCandidates') }}</span>
      </div>

      <div v-if="routeEditorAdding" class="route-candidate-add">
        <UiSelectField
          v-model="routeEditorNewProviderId"
          :aria-label="t('settings.selectProvider')"
          :options="[
            { value: '', label: t('settings.selectProvider'), disabled: true },
            ...providers.map((provider) => ({ value: provider.id, label: provider.display_name })),
            ...runtimeCliOptions.map((runtime) => ({ value: runtime.provider_instance_id, label: `${runtime.display_name} (CLI)` })),
            ...runtimeAcpOptions.map((runtime) => ({ value: runtime.provider_instance_id ?? runtime.runtime_id, label: `${runtime.display_name} (ACP)` })),
          ]"
        />
        <UiSelectField
          v-if="modelsForProvider(routeEditorNewProviderId).length > 0"
          v-model="routeEditorNewModelId"
          :aria-label="t('settings.model')"
          :options="[
            { value: '', label: t('settings.fixedModelNoModel') },
            ...modelsForProvider(routeEditorNewProviderId).map((model) => ({ value: model.model_id, label: model.model_id })),
          ]"
        />
        <UiButton variant="outline" size="sm" :disabled="!routeEditorNewProviderId" @click="addRouteCandidate">
          <Check :size="14" />
        </UiButton>
        <UiButton variant="ghost" size="sm" @click="routeEditorAdding = false">{{ t('settings.cancel') }}</UiButton>
      </div>
      <div v-else class="route-candidate-actions">
        <UiButton variant="outline" size="sm" :disabled="routeEditorBusy" @click="routeEditorAdding = true">
          <Plus :size="14" />
          <span>{{ t('settings.routeAddCandidate') }}</span>
        </UiButton>
        <UiButton
          variant="outline"
          size="sm"
          :disabled="routeEditorBusy || !routeEditorDirty"
          @click="handleSaveRoute"
        >
          <Save :size="14" />
          <span>{{ t('settings.saveRoute') }}</span>
        </UiButton>
      </div>
    </div>

    <!-- 3. Budget & Billing Policy Slot -->
    <section class="budget-policy-slot" data-testid="model-budget-policy-slot">
      <div class="budget-policy-head">
        <div class="budget-policy-title">
          <Coins :size="15" />
          <div>
            <h4>{{ t('settings.modelBudgetPolicyTitle') }}</h4>
            <p class="quiet">{{ t('settings.modelBudgetPolicyHint') }}</p>
          </div>
        </div>
      </div>

      <div class="budget-policy-grid">
        <div class="budget-metric-card">
          <span class="budget-metric-label">{{ t('settings.modelBudgetCostDistribution') }}</span>
          <div class="budget-cost-chips">
            <UiBadge variant="outline">{{ t('settings.modelCostTier.low') }}: {{ costDistribution.low }}</UiBadge>
            <UiBadge variant="outline">{{ t('settings.modelCostTier.medium') }}: {{ costDistribution.medium }}</UiBadge>
            <UiBadge variant="outline">{{ t('settings.modelCostTier.high') }}: {{ costDistribution.high }}</UiBadge>
            <UiBadge variant="secondary">{{ t('settings.modelBudgetUnsetCost') }}: {{ costDistribution.unset }}</UiBadge>
          </div>
        </div>
        <div v-if="runtimePolicy?.context" class="budget-metric-card">
          <span class="budget-metric-label">{{ t('settings.runtimeCeilings.contextWindow') }}</span>
          <strong class="budget-metric-value">{{ runtimePolicy.context.default_token_budget }}</strong>
        </div>
        <div v-if="runtimePolicy?.context" class="budget-metric-card">
          <span class="budget-metric-label">{{ t('settings.runtimeCeilings.runBudget') }}</span>
          <strong class="budget-metric-value">
            {{ runtimePolicy.context.run_token_budget > 0 ? runtimePolicy.context.run_token_budget : t('settings.runtimeCeilings.disabled') }}
          </strong>
        </div>
      </div>
    </section>
  </section>
</template>

<style scoped>
.routing-policy-tab {
  display: grid;
  gap: 14px;
}
.routing-default-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 14px;
  flex-wrap: wrap;
  padding: 12px 14px;
  border: 1px solid var(--border-muted);
  border-radius: 12px;
  background: var(--surface-raised);
}
.routing-default-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
}
.routing-default-info {
  min-width: 0;
  flex: 1 1 200px;
}
.routing-default-info p {
  margin: 3px 0 0;
  font-size: 12px;
}
.routing-default-control {
  min-width: 220px;
}
.route-candidate-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.route-candidate-title > span:last-child {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.budget-policy-slot {
  margin-top: 6px;
  padding: 14px;
  border: 1px dashed var(--border-muted);
  border-radius: 12px;
  background: var(--surface-section);
  display: grid;
  gap: 12px;
}
.budget-policy-title {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}
.budget-policy-title h4 {
  margin: 0 0 2px;
  font-size: 13px;
  font-weight: 600;
}
.budget-policy-title p {
  margin: 0;
  font-size: 12px;
}
.budget-policy-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 10px;
}
.budget-metric-card {
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: 9px;
  background: var(--surface-raised);
  display: grid;
  gap: 6px;
}
.budget-metric-label {
  font-size: 11px;
  color: var(--text-muted);
}
.budget-metric-value {
  font-size: 15px;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  color: var(--text-primary);
}
.budget-cost-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}
</style>
