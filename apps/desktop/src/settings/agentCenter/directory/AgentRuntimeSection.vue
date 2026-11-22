<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Cpu, Info, Route, Save, Search, ShieldCheck, Terminal, Workflow } from '@lucide/vue'
import type {
  AgentDirectoryItemDto,
  AgentRuntimeBindingDto,
  AgentRuntimeSelectionKind,
  AgentViewDto,
  ModelCenterOverviewDto,
  ModelProviderInstanceDto,
  ModelRouteDto,
} from '@/api'
import { providerStatusLabel } from '@/settings/modelCenterLabels'
import { modelOptionKey } from '@/runtimeCenterView'
import { UiButton, UiInput, UiLabel, UiSelectField } from '@/components/ui'
import {
  providerRuntimeChannel,
  runtimeChannelBadgeLabel,
  type RuntimeChannelKind,
} from './agentDirectoryHelpers'

const props = withDefaults(defineProps<{
  configuringAgent: AgentViewDto
  configuringAgentId: string
  configuringDirectoryItem: AgentDirectoryItemDto | null
  configuringRuntimeBinding: AgentRuntimeBindingDto | null
  configuringLegacyWarning: { purpose: string; agent_ids: string[] } | null
  runtimeSelection: AgentRuntimeSelectionKind
  runtimeModelKey: string
  runtimeRoutePurpose: string
  runtimeModelQuery: string
  routes: ModelRouteDto[]
  providers?: ModelProviderInstanceDto[]
  runtimeModels: ModelCenterOverviewDto['models']
  runtimeCliOptions: ModelCenterOverviewDto['cli_runtimes']
  runtimeTuiOptions?: ModelCenterOverviewDto['tui_runtimes']
  runtimeAcpOptions: ModelCenterOverviewDto['acp_runtimes']
  runtimeBusy: boolean
  strategySaveable: boolean
  runtimeSummary: string
}>(), {
  providers: () => [],
  runtimeTuiOptions: () => [],
})

const emit = defineEmits<{
  'update:runtimeSelection': [value: AgentRuntimeSelectionKind]
  'update:runtimeModelKey': [value: string]
  'update:runtimeRoutePurpose': [value: string]
  'update:runtimeModelQuery': [value: string]
  save: []
}>()

const { t } = useI18n()

type ChannelFilterKey = 'all' | RuntimeChannelKind
const channelFilter = ref<ChannelFilterKey>('all')

const statusLabel = (value: string) => providerStatusLabel(t, value)

const providerById = computed(() => new Map(props.providers.map((item) => [item.id, item])))

const mergedTuiOptions = computed(() => {
  const seen = new Set<string>()
  const list: Array<{
    provider_instance_id: string
    runtime_id: string
    display_name: string
    driver: string
    status: string
    binary_path?: string | null
  }> = []
  for (const item of props.runtimeTuiOptions) {
    if (!item.provider_instance_id || seen.has(item.provider_instance_id)) continue
    seen.add(item.provider_instance_id)
    list.push({
      provider_instance_id: item.provider_instance_id,
      runtime_id: item.runtime_id,
      display_name: item.display_name,
      driver: item.driver,
      status: item.status,
      binary_path: item.binary_path,
    })
  }
  for (const provider of props.providers) {
    if (seen.has(provider.id)) continue
    if (providerRuntimeChannel(provider) !== 'tui') continue
    seen.add(provider.id)
    list.push({
      provider_instance_id: provider.id,
      runtime_id: provider.id,
      display_name: provider.display_name,
      driver: provider.driver,
      status: provider.status,
      binary_path: provider.binary_path,
    })
  }
  return list
})

function modelChannel(providerInstanceId: string): RuntimeChannelKind {
  const provider = providerById.value.get(providerInstanceId)
  return provider ? providerRuntimeChannel(provider) : 'api'
}

const effectiveChannel = computed<RuntimeChannelKind | null>(() => {
  const providerId = props.configuringRuntimeBinding?.provider_instance_id
    ?? Object.values(props.configuringDirectoryItem?.effective_previews ?? {})
      .map((preview) => preview?.expected_selection?.provider_instance_id)
      .find((id): id is string => Boolean(id))
  if (!providerId) return null
  if (props.runtimeAcpOptions.some((item) => item.provider_instance_id === providerId || item.runtime_id === providerId)) return 'acp'
  if (mergedTuiOptions.value.some((item) => item.provider_instance_id === providerId)) return 'tui'
  if (props.runtimeCliOptions.some((item) => item.provider_instance_id === providerId)) return 'cli'
  const provider = providerById.value.get(providerId)
  return provider ? providerRuntimeChannel(provider) : 'api'
})

function runtimeQueryMatches(query: string, ...values: Array<string | null | undefined>) {
  const normalized = query.trim().toLocaleLowerCase()
  if (!normalized) return true
  return values.some((value) => value?.toLocaleLowerCase().includes(normalized))
}

const filteredRuntimeModels = computed(() =>
  props.runtimeModels.filter((model) => {
    const channel = modelChannel(model.provider_instance_id)
    if (channelFilter.value !== 'all' && channelFilter.value !== channel) return false
    return runtimeQueryMatches(
      props.runtimeModelQuery,
      model.model_id,
      model.provider_display_name,
      model.provider_instance_id,
      model.status,
      channel,
      ...model.configuration_sources,
      ...model.route_purposes,
    )
  }),
)

const filteredRuntimeCliOptions = computed(() => {
  if (channelFilter.value !== 'all' && channelFilter.value !== 'cli') return []
  return props.runtimeCliOptions.filter((runtime) =>
    runtimeQueryMatches(
      props.runtimeModelQuery,
      runtime.display_name,
      runtime.runtime_id,
      runtime.driver,
      runtime.status,
      runtime.binary_path,
      runtime.home_path,
      'cli',
    ),
  )
})

const filteredRuntimeTuiOptions = computed(() => {
  if (channelFilter.value !== 'all' && channelFilter.value !== 'tui') return []
  return mergedTuiOptions.value.filter((runtime) =>
    runtimeQueryMatches(
      props.runtimeModelQuery,
      runtime.display_name,
      runtime.runtime_id,
      runtime.driver,
      runtime.status,
      runtime.binary_path,
      'tui',
    ),
  )
})

const filteredRuntimeAcpOptions = computed(() => {
  if (channelFilter.value !== 'all' && channelFilter.value !== 'acp') return []
  return props.runtimeAcpOptions.filter((runtime) =>
    runtimeQueryMatches(
      props.runtimeModelQuery,
      runtime.display_name,
      runtime.runtime_id,
      runtime.source,
      runtime.driver,
      runtime.status,
      runtime.command,
      'acp',
    ),
  )
})

const harnessQuickOptions = computed(() => [
  ...filteredRuntimeAcpOptions.value.map((runtime) => ({
    key: runtime.provider_instance_id ?? runtime.runtime_id,
    channel: 'acp' as const,
    displayName: runtime.display_name,
    status: runtime.status,
    detail: runtime.binary_path || runtime.command || runtime.driver || runtime.runtime_id,
    bindable: Boolean(runtime.provider_instance_id),
  })),
  ...filteredRuntimeCliOptions.value.map((runtime) => ({
    key: runtime.provider_instance_id,
    channel: 'cli' as const,
    displayName: runtime.display_name,
    status: runtime.status,
    detail: runtime.binary_path || runtime.driver || runtime.runtime_id,
    bindable: Boolean(runtime.provider_instance_id),
  })),
  ...filteredRuntimeTuiOptions.value.map((runtime) => ({
    key: runtime.provider_instance_id,
    channel: 'tui' as const,
    displayName: runtime.display_name,
    status: runtime.status,
    detail: runtime.binary_path || runtime.driver || runtime.runtime_id,
    bindable: Boolean(runtime.provider_instance_id),
  })),
])

const channelTabs = computed<Array<{ key: ChannelFilterKey; label: string; count: number; channel?: RuntimeChannelKind }>>(() => {
  const apiCount = props.runtimeModels.filter((m) => modelChannel(m.provider_instance_id) === 'api').length
  const httpCount = props.runtimeModels.filter((m) => modelChannel(m.provider_instance_id) === 'http').length
  const acpCount = props.runtimeAcpOptions.length
  const cliCount = props.runtimeCliOptions.length
  const tuiCount = mergedTuiOptions.value.length
  const total = apiCount + httpCount + acpCount + cliCount + tuiCount
  const list: Array<{ key: ChannelFilterKey; label: string; count: number; channel?: RuntimeChannelKind }> = [
    { key: 'all', label: t('settings.runtimeChannelAll'), count: total },
    { key: 'api', label: t('settings.runtimeChannelApi'), count: apiCount, channel: 'api' },
  ]
  if (httpCount > 0) {
    list.push({ key: 'http', label: t('settings.runtimeChannelHttp'), count: httpCount, channel: 'http' })
  }
  list.push(
    { key: 'acp', label: t('settings.runtimeChannelAcp'), count: acpCount, channel: 'acp' },
    { key: 'cli', label: t('settings.runtimeChannelCli'), count: cliCount, channel: 'cli' },
    { key: 'tui', label: t('settings.runtimeChannelTui'), count: tuiCount, channel: 'tui' },
  )
  return list
})

const selectedKeyChannel = computed<RuntimeChannelKind | null>(() => {
  const key = props.runtimeModelKey
  if (!key) return null
  if (key.includes('\u0000')) {
    const providerId = key.split('\u0000')[0] ?? ''
    return modelChannel(providerId)
  }
  if (props.runtimeAcpOptions.some((r) => (r.provider_instance_id ?? r.runtime_id) === key)) return 'acp'
  if (mergedTuiOptions.value.some((r) => r.provider_instance_id === key)) return 'tui'
  if (props.runtimeCliOptions.some((r) => r.provider_instance_id === key)) return 'cli'
  const provider = providerById.value.get(key)
  return provider ? providerRuntimeChannel(provider) : null
})

function selectHarnessQuick(key: string) {
  emit('update:runtimeModelKey', key)
}

function agentPreviewLine(
  modeSlug: string,
  nodeKey: string,
  preview: { expected_selection?: { provider_instance_id?: string; model?: string | null } | null },
): string {
  const selection = preview.expected_selection
  const target = selection ? `${selection.provider_instance_id ?? '—'}${selection.model ? ` · ${selection.model}` : ''}` : '—'
  return `${modeSlug}:${nodeKey} → ${target}`
}

function agentInvocationLine(invocation: {
  provider_instance_id: string
  model?: string | null
  status: string
  completed_at?: string | null
}): string {
  const model = invocation.model ? ` · ${invocation.model}` : ''
  const time = invocation.completed_at ? ` · ${invocation.completed_at}` : ''
  return `${invocation.provider_instance_id}${model} · ${invocation.status}${time}`
}
</script>

<template>
  <div class="agent-config-section" data-testid="agent-section-runtime">
    <div class="agent-config-section-title">{{ t('settings.agentSectionRuntime') }}</div>
    <div class="agent-detail-grid">
      <div>
        <span>{{ t('settings.routePurpose') }}</span>
        <strong>{{ configuringRuntimeBinding?.route_purpose ?? configuringAgent.model_route_purpose ?? '—' }}</strong>
      </div>
      <div>
        <span>{{ t('settings.effectiveRuntime') }}</span>
        <strong class="runtime-effective-value">
          <span
            v-if="effectiveChannel"
            :class="['runtime-channel-badge', `channel-${effectiveChannel}`]"
            data-testid="agent-effective-channel-badge"
          >
            {{ runtimeChannelBadgeLabel(effectiveChannel) }}
          </span>
          <span class="runtime-effective-text">{{ runtimeSummary || t('settings.runtimeUnresolved') }}</span>
        </strong>
      </div>
    </div>

    <div v-if="configuringLegacyWarning" class="runtime-binding-warning">
      <Info :size="16" />
      <div>
        <strong>{{ t('settings.sharedLegacyRoute', { purpose: configuringLegacyWarning.purpose }) }}</strong>
        <span>{{ t('settings.sharedLegacyRouteHint', { count: configuringLegacyWarning.agent_ids.length }) }}</span>
      </div>
    </div>

    <div class="runtime-source-grid">
      <button
        type="button"
        :class="{ active: runtimeSelection === 'inherit' }"
        :aria-pressed="runtimeSelection === 'inherit'"
        @click="emit('update:runtimeSelection', 'inherit')"
      >
        <Workflow :size="16" />
        <strong>{{ t('settings.runtimeInherit') }}</strong>
        <span>{{ t('settings.runtimeInheritHint') }}</span>
      </button>
      <button
        type="button"
        :class="{ active: runtimeSelection === 'route' }"
        :aria-pressed="runtimeSelection === 'route'"
        @click="emit('update:runtimeSelection', 'route')"
      >
        <Route :size="16" />
        <strong>{{ t('settings.runtimeRoute') }}</strong>
        <span>{{ t('settings.runtimeRouteHint') }}</span>
      </button>
      <button
        type="button"
        :class="{ active: runtimeSelection === 'fixed_model' }"
        :aria-pressed="runtimeSelection === 'fixed_model'"
        @click="emit('update:runtimeSelection', 'fixed_model')"
      >
        <Cpu :size="16" />
        <strong>{{ t('settings.runtimeFixedModel') }}</strong>
        <span>{{ t('settings.runtimeFixedModelHint') }}</span>
      </button>
    </div>

    <div v-if="runtimeSelection === 'inherit'" class="runtime-source-current">
      <ShieldCheck :size="16" />
      <span>{{ t('settings.runtimeInheritedCurrent', { source: runtimeSummary || t('settings.runtimeUnresolved') }) }}</span>
    </div>
    <div v-else-if="runtimeSelection === 'route'" class="settings-field runtime-source-picker">
      <UiLabel>{{ t('settings.routePurpose') }}</UiLabel>
      <UiSelectField
        :model-value="runtimeRoutePurpose"
        :aria-label="t('settings.routePurpose')"
        :options="[
          { value: '', label: t('settings.routePurpose'), disabled: true },
          ...routes.map((route) => ({ value: route.purpose, label: `${route.purpose} · ${route.candidates.length}` })),
        ]"
        @update:model-value="emit('update:runtimeRoutePurpose', String($event ?? ''))"
      />
      <p v-if="routes.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
    </div>
    <div v-else class="settings-field runtime-source-picker">
      <div class="runtime-picker-label-row">
        <UiLabel>{{ t('settings.runtimeFixedModel') }}</UiLabel>
        <span
          v-if="selectedKeyChannel"
          :class="['runtime-channel-badge', `channel-${selectedKeyChannel}`]"
          data-testid="agent-selected-channel-badge"
        >
          {{ runtimeChannelBadgeLabel(selectedKeyChannel) }}
        </span>
      </div>

      <div
        class="runtime-channel-filter-bar"
        role="group"
        :aria-label="t('settings.runtimeChannelFilter')"
        data-testid="agent-runtime-channel-filter"
      >
        <button
          v-for="tab in channelTabs"
          :key="tab.key"
          type="button"
          class="runtime-channel-filter-btn"
          :class="{ active: channelFilter === tab.key }"
          :aria-pressed="channelFilter === tab.key"
          :data-channel-filter="tab.key"
          @click="channelFilter = tab.key"
        >
          <span>{{ tab.label }}</span>
          <em>{{ tab.count }}</em>
        </button>
      </div>

      <div class="runtime-source-search">
        <Search :size="14" />
        <UiInput
          :model-value="runtimeModelQuery"
          :placeholder="t('settings.runtimeSearchPlaceholder', { kind: t('settings.centerModels') })"
          @update:model-value="emit('update:runtimeModelQuery', String($event ?? ''))"
        />
      </div>
      <UiSelectField
        :model-value="runtimeModelKey"
        :aria-label="t('settings.selectModel')"
        :options="[
          { value: '', label: t('settings.selectModel'), disabled: true },
          ...filteredRuntimeModels.map((model) => {
            const channel = modelChannel(model.provider_instance_id)
            return {
              value: modelOptionKey(model.provider_instance_id, model.model_id),
              label: `[${runtimeChannelBadgeLabel(channel)}] ${model.model_id} · ${model.provider_display_name ?? model.provider_instance_id} · ${statusLabel(model.status)}`,
            }
          }),
          ...filteredRuntimeCliOptions.map((runtime) => ({
            value: runtime.provider_instance_id,
            label: `[CLI] ${runtime.display_name} · ${statusLabel(runtime.status)}`,
          })),
          ...filteredRuntimeTuiOptions.map((runtime) => ({
            value: runtime.provider_instance_id,
            label: `[TUI] ${runtime.display_name} · ${statusLabel(runtime.status)}`,
          })),
          ...filteredRuntimeAcpOptions.map((runtime) => ({
            value: runtime.provider_instance_id ?? runtime.runtime_id,
            label: `[ACP] ${runtime.display_name} · ${statusLabel(runtime.status)}`,
          })),
        ]"
        @update:model-value="emit('update:runtimeModelKey', String($event ?? ''))"
      />

      <div v-if="harnessQuickOptions.length > 0" class="runtime-harness-quick" data-testid="agent-runtime-harness-list">
        <div class="runtime-harness-quick-title">
          <Terminal :size="12" aria-hidden="true" />
          <span>{{ t('settings.runtimeHarnessQuickPick') }}</span>
        </div>
        <div class="runtime-harness-grid">
          <button
            v-for="item in harnessQuickOptions"
            :key="`${item.channel}:${item.key}`"
            type="button"
            class="runtime-harness-chip"
            :class="{ active: runtimeModelKey === item.key }"
            :aria-pressed="runtimeModelKey === item.key"
            :disabled="!item.bindable"
            :title="item.detail || item.displayName"
            @click="selectHarnessQuick(item.key)"
          >
            <span :class="['runtime-channel-badge', `channel-${item.channel}`]">
              {{ runtimeChannelBadgeLabel(item.channel) }}
            </span>
            <strong class="runtime-harness-name">{{ item.displayName }}</strong>
            <small class="runtime-harness-status">{{ statusLabel(item.status) }}</small>
          </button>
        </div>
      </div>

      <p
        v-if="filteredRuntimeModels.length === 0 && filteredRuntimeCliOptions.length === 0 && filteredRuntimeTuiOptions.length === 0 && filteredRuntimeAcpOptions.length === 0"
        class="agent-config-hint"
      >
        {{ t('settings.noRuntimeMatches') }}
      </p>
    </div>

    <div class="runtime-binding-readonly">
      <Info :size="16" />
      <div>
        <strong>{{ t('settings.agentStrategyVersioned') }}</strong>
        <span>{{ t('settings.agentStrategyVersionedHint') }}</span>
      </div>
    </div>
    <div class="modal-actions compact">
      <UiButton :disabled="runtimeBusy || !strategySaveable" size="sm" @click="emit('save')">
        <Save :size="14" />
        <span>{{ t('settings.saveRuntimeBinding') }}</span>
      </UiButton>
    </div>

    <template v-if="configuringDirectoryItem">
      <div class="agent-config-section-title secondary">{{ t('settings.agentEffectivePreviewSection') }}</div>
      <div v-if="Object.keys(configuringDirectoryItem.effective_previews ?? {}).length > 0" class="agent-mode-usage-list">
        <span v-for="(preview, key) in configuringDirectoryItem.effective_previews" :key="key" :title="key">
          {{ agentPreviewLine(String(key).split(':')[0], String(key).split(':')[1] ?? '', preview) }}
        </span>
      </div>
      <p v-else class="agent-config-hint">{{ t('settings.agentNoEffectivePreview') }}</p>

      <div class="agent-config-section-title secondary">{{ t('settings.agentRecentInvocationSection') }}</div>
      <p v-if="configuringDirectoryItem.recent_invocation" class="agent-config-hint">
        {{ agentInvocationLine(configuringDirectoryItem.recent_invocation) }}
      </p>
      <p v-else class="agent-config-hint">{{ t('settings.agentNoRecentInvocation') }}</p>
    </template>
  </div>
</template>

<style scoped>
.runtime-effective-value {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  max-width: 100%;
}

.runtime-effective-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.runtime-picker-label-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.runtime-channel-filter-bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
  margin-bottom: 6px;
}

.runtime-channel-filter-btn {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 5px;
  height: var(--control-height-xs, 26px);
  padding: 0 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease, color 120ms ease;
}

.runtime-channel-filter-btn:hover {
  border-color: var(--border-input-focus);
  color: var(--text-primary);
  background: var(--surface-hover);
}

.runtime-channel-filter-btn.active {
  border-color: color-mix(in srgb, var(--accent-primary) 50%, var(--border-muted));
  background: var(--surface-selected);
  color: var(--text-primary);
  font-weight: 600;
}

.runtime-channel-filter-btn:focus-visible,
.runtime-harness-chip:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.runtime-channel-filter-btn em {
  font-style: normal;
  font-size: 10px;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.runtime-harness-quick {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 8px;
}

.runtime-harness-quick-title {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted);
}

.runtime-harness-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.runtime-harness-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 100%;
  min-width: 0;
  min-height: var(--control-height-sm, 30px);
  padding: 4px 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 12px;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease;
}

.runtime-harness-chip:hover:not(:disabled) {
  border-color: var(--border-input-focus);
  background: var(--surface-hover);
}

.runtime-harness-chip.active {
  border-color: color-mix(in srgb, var(--accent-primary) 55%, var(--border-muted));
  background: var(--surface-selected);
}

.runtime-harness-chip:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.runtime-harness-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 500;
}

.runtime-harness-status {
  flex-shrink: 0;
  font-size: 10px;
  color: var(--text-muted);
  white-space: nowrap;
}
</style>
