<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Cpu, PanelRight, Plus, Search, Server, Settings2, Trash2, Users, X } from '@lucide/vue'
import { UiBadge, UiButton, UiInput } from '@/components/ui'
import ModelCapabilityInspector from '@/settings/modelCenter/ModelCapabilityInspector.vue'
import {
  MODEL_PROFILE_COST_TIERS,
  MODEL_PROFILE_SPECIALTIES,
  MODEL_PROFILE_TIERS,
  suggestModelProfile,
} from '@/composables/useModelCenter'
import { findTemplate } from '@/providerTemplates'
import type {
  ModelCenterOverviewDto,
  ModelCostTier,
  ModelProfileDto,
  ModelProfileTier,
  ModelProviderInstanceDto,
  ModelReferenceDto,
  ModelSpecialty,
} from '@/api'

type ModelRow = ModelCenterOverviewDto['models'][number]

const props = defineProps<{
  overview: ModelCenterOverviewDto | null
  providers: ModelProviderInstanceDto[]
  busy: boolean
  defaultChatCandidate: { provider_instance_id: string; model?: string | null } | null
  modelsForProvider: (providerId: string) => ModelRow[]
  referencesForModel: (providerId: string, modelId: string) => ModelReferenceDto[]
  agentReferenceCountForModel: (providerId: string, modelId: string) => number
  statusLabel: (status: string) => string
  statusVariant: (status: string) => 'default' | 'secondary' | 'destructive' | 'outline'
  configuredModelSourceLabel: (source: string) => string
  referenceKindLabel: (kind: string) => string
  referenceDetailLabel: (reference: ModelReferenceDto) => string | null
}>()

const emit = defineEmits<{
  'locate-provider': [providerId: string]
  'add-model': [providerId: string]
  'set-default': [providerId: string, modelId: string]
  remove: [providerId: string, modelId: string]
  'save-profile': [provider: ModelProviderInstanceDto, modelId: string, profile: ModelProfileDto | null]
  'parameters-saved': []
  reload: []
}>()

const { t, te } = useI18n()

const catalogQuery = ref('')
const selectedSpecialties = ref<ModelSpecialty[]>([])
const selectedTier = ref<'' | ModelProfileTier>('')
const selectedCostTier = ref<'' | ModelCostTier>('')
const inspectedTarget = ref<{ providerId: string; modelId: string } | null>(null)

const hasActiveFilters = computed(
  () => Boolean(catalogQuery.value.trim())
    || selectedSpecialties.value.length > 0
    || Boolean(selectedTier.value)
    || Boolean(selectedCostTier.value),
)

function clearFilters() {
  catalogQuery.value = ''
  selectedSpecialties.value = []
  selectedTier.value = ''
  selectedCostTier.value = ''
}

function toggleSpecialtyFilter(spec: ModelSpecialty) {
  selectedSpecialties.value = selectedSpecialties.value.includes(spec)
    ? selectedSpecialties.value.filter((item) => item !== spec)
    : [...selectedSpecialties.value, spec]
}

function specialtyLabel(spec: string): string {
  const key = `settings.modelSpecialty.${spec}`
  return te(key) ? t(key) : spec
}

function tierLabel(tier: ModelProfileTier): string {
  return t(`settings.modelProfileTier.${tier}`)
}

function costTierLabel(tier: ModelCostTier): string {
  return t(`settings.modelCostTier.${tier}`)
}

function modelCatalogModeLabel(mode?: string) {
  return mode === 'configured_only' ? t('settings.configuredOnly') : mode ?? t('settings.configuredOnly')
}

function providerPresentation(driver: string) {
  return findTemplate(driver)
}

const providerById = computed(() => new Map(props.providers.map((item) => [item.id, item])))

function savedProfileFor(providerId: string, modelId: string): ModelProfileDto | null {
  return providerById.value.get(providerId)?.model_profiles?.[modelId] ?? null
}

function effectiveProfileFor(providerId: string, modelId: string): ModelProfileDto {
  return savedProfileFor(providerId, modelId) ?? suggestModelProfile(modelId)
}

function formatContextWindow(tokens?: number | null): string | null {
  if (!tokens || tokens <= 0) return null
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(tokens % 1_000_000 === 0 ? 0 : 1)}M`
  if (tokens >= 1000) return `${Math.round(tokens / 1000)}k`
  return String(tokens)
}

function isDefaultChatModel(providerId: string, modelId: string): boolean {
  return props.defaultChatCandidate?.provider_instance_id === providerId
    && props.defaultChatCandidate?.model === modelId
}

function isModelSelected(providerId: string, modelId: string): boolean {
  return inspectedTarget.value?.providerId === providerId && inspectedTarget.value?.modelId === modelId
}

function selectModelForInspector(providerId: string, modelId: string) {
  if (isModelSelected(providerId, modelId)) return
  inspectedTarget.value = { providerId, modelId }
}

function toggleModelInspector(providerId: string, modelId: string) {
  inspectedTarget.value = isModelSelected(providerId, modelId)
    ? null
    : { providerId, modelId }
}

const modelCatalogGroups = computed(() => {
  const query = catalogQuery.value.trim().toLocaleLowerCase()
  const specs = selectedSpecialties.value
  const tier = selectedTier.value
  const cost = selectedCostTier.value
  const structuredFiltering = specs.length > 0 || Boolean(tier) || Boolean(cost)

  return (props.overview?.api_connections ?? []).flatMap((provider) => {
    const providerHit = !query || [provider.display_name, provider.driver]
      .some((value) => value.toLocaleLowerCase().includes(query))

    const models = props.modelsForProvider(provider.id).filter((model) => {
      const saved = savedProfileFor(provider.id, model.model_id)
      const eff = saved ?? suggestModelProfile(model.model_id)

      if (query && !providerHit) {
        const textHit = [
          model.model_id,
          ...(model.configuration_sources ?? []),
          ...(model.route_purposes ?? []),
          saved?.notes ?? '',
        ].some((value) => value.toLocaleLowerCase().includes(query))
        if (!textHit) return false
      }
      if (specs.length > 0 && !specs.every((s) => (eff.specialties ?? []).includes(s))) {
        return false
      }
      if (tier && eff.tier !== tier) {
        return false
      }
      if (cost && saved?.cost_tier !== cost) {
        return false
      }
      return true
    })

    const keepEmptyGroup = !structuredFiltering && !query && providerHit
    return models.length > 0 || keepEmptyGroup ? [{ provider, models }] : []
  })
})

const modelCatalogCount = computed(() =>
  modelCatalogGroups.value.reduce((total, group) => total + group.models.length, 0),
)

const inspectedModel = computed<ModelRow | null>(() => {
  if (!inspectedTarget.value) return null
  const { providerId, modelId } = inspectedTarget.value
  return props.modelsForProvider(providerId).find((item) => item.model_id === modelId) ?? null
})

const inspectedProvider = computed<ModelProviderInstanceDto | null>(() => {
  if (!inspectedTarget.value) return null
  return providerById.value.get(inspectedTarget.value.providerId) ?? null
})

const inspectedProviderDisplayName = computed(() => {
  if (!inspectedTarget.value) return ''
  const conn = props.overview?.api_connections.find((c) => c.id === inspectedTarget.value?.providerId)
  return conn?.display_name ?? inspectedProvider.value?.display_name ?? inspectedTarget.value.providerId
})

// Clear inspector selection if the inspected model was removed from the catalog.
watch(
  () => props.overview?.models,
  () => {
    if (inspectedTarget.value && !inspectedModel.value) {
      inspectedTarget.value = null
    }
  },
)

function handleParametersSaved() {
  inspectedTarget.value = null
  emit('parameters-saved')
}
</script>

<template>
  <section class="center-resource-section model-capability-library" data-testid="model-capability-library">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.centerModelLibrary') }}</h3>
        <p>{{ t('settings.modelLibraryHint') }}</p>
      </div>
      <UiBadge variant="outline" :title="t('settings.configuredModelsHint')">
        {{ modelCatalogModeLabel(overview?.capabilities.model_catalog_mode) }}
      </UiBadge>
    </div>

    <!-- Search + Capability Filter Chips -->
    <div class="library-filter-panel">
      <div class="model-center-catalog-toolbar">
        <div class="model-provider-search">
          <Search :size="15" />
          <UiInput
            v-model="catalogQuery"
            :placeholder="t('settings.modelCatalogSearch')"
            :aria-label="t('settings.modelCatalogSearch')"
          />
        </div>
        <span class="model-provider-count">
          {{ t('settings.providerResultCount', { visible: modelCatalogCount, total: overview?.models.length ?? 0 }) }}
        </span>
        <UiButton v-if="hasActiveFilters" variant="ghost" size="sm" @click="clearFilters">
          <X :size="13" />
          <span>{{ t('settings.modelLibraryClearFilters') }}</span>
        </UiButton>
      </div>

      <div class="library-filter-rows">
        <div class="library-filter-group" role="group" :aria-label="t('settings.modelProfileSpecialties')">
          <span class="library-filter-label">{{ t('settings.modelProfileSpecialties') }}</span>
          <div class="library-chip-list">
            <button
              v-for="spec in MODEL_PROFILE_SPECIALTIES"
              :key="spec"
              type="button"
              class="library-chip"
              :class="{ active: selectedSpecialties.includes(spec) }"
              :aria-pressed="selectedSpecialties.includes(spec)"
              :data-filter-specialty="spec"
              @click="toggleSpecialtyFilter(spec)"
            >
              {{ specialtyLabel(spec) }}
            </button>
          </div>
        </div>

        <div class="library-filter-split">
          <div class="library-filter-group" role="group" :aria-label="t('settings.modelProfileTierLabel')">
            <span class="library-filter-label">{{ t('settings.modelProfileTierLabel') }}</span>
            <div class="library-chip-list">
              <button
                type="button"
                class="library-chip"
                :class="{ active: selectedTier === '' }"
                :aria-pressed="selectedTier === ''"
                data-filter-tier="all"
                @click="selectedTier = ''"
              >
                {{ t('settings.filterAll') }}
              </button>
              <button
                v-for="tier in MODEL_PROFILE_TIERS"
                :key="tier"
                type="button"
                class="library-chip"
                :class="{ active: selectedTier === tier }"
                :aria-pressed="selectedTier === tier"
                :data-filter-tier="tier"
                @click="selectedTier = selectedTier === tier ? '' : tier"
              >
                {{ tierLabel(tier) }}
              </button>
            </div>
          </div>

          <div class="library-filter-group" role="group" :aria-label="t('settings.modelProfileCostTierLabel')">
            <span class="library-filter-label">{{ t('settings.modelProfileCostTierLabel') }}</span>
            <div class="library-chip-list">
              <button
                type="button"
                class="library-chip"
                :class="{ active: selectedCostTier === '' }"
                :aria-pressed="selectedCostTier === ''"
                data-filter-cost="all"
                @click="selectedCostTier = ''"
              >
                {{ t('settings.filterAll') }}
              </button>
              <button
                v-for="cost in MODEL_PROFILE_COST_TIERS"
                :key="cost"
                type="button"
                class="library-chip"
                :class="{ active: selectedCostTier === cost }"
                :aria-pressed="selectedCostTier === cost"
                :data-filter-cost="cost"
                @click="selectedCostTier = selectedCostTier === cost ? '' : cost"
              >
                {{ costTierLabel(cost) }}
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Main 2-column Hero Workspace: Capability Cards + Docked Right Inspector -->
    <div class="model-library-workspace" :class="{ 'has-inspector': Boolean(inspectedModel) }">
      <div class="model-library-catalog">
        <div
          v-for="{ provider, models } in modelCatalogGroups"
          :key="provider.id"
          class="model-provider-group"
        >
          <div class="center-resource-list-row model-provider-group-head">
            <div class="center-resource-primary">
              <span
                class="provider-brand-icon"
                :style="{ color: providerPresentation(provider.driver)?.brand_color, backgroundColor: providerPresentation(provider.driver)?.brand_bg }"
              >
                <span v-if="providerPresentation(provider.driver)?.icon" class="provider-brand-mark" v-html="providerPresentation(provider.driver)?.icon"></span>
                <Server v-else :size="16" />
              </span>
              <div>
                <strong>{{ provider.display_name }}</strong>
                <span>{{ provider.driver }}</span>
              </div>
            </div>
            <UiBadge :variant="statusVariant(provider.status)">{{ statusLabel(provider.status) }}</UiBadge>
            <UiButton
              variant="ghost"
              size="sm"
              :title="t('settings.locateProvider')"
              @click="emit('locate-provider', provider.id)"
            >
              <PanelRight :size="14" />
              {{ t('settings.locateProvider') }}
            </UiButton>
            <UiButton
              variant="outline"
              size="sm"
              :disabled="busy"
              :title="t('settings.addModel')"
              @click="emit('add-model', provider.id)"
            >
              <Plus :size="14" />
              {{ t('settings.addModel') }}
            </UiButton>
          </div>

          <div class="model-capability-grid">
            <article
              v-for="model in models"
              :key="model.id"
              class="model-group-model model-capability-card"
              :class="{ selected: isModelSelected(provider.id, model.model_id) }"
              :data-model-card="model.model_id"
              @click="selectModelForInspector(provider.id, model.model_id)"
            >
              <div class="model-card-top">
                <div class="model-card-identity">
                  <Cpu :size="15" />
                  <strong :title="model.model_id">{{ model.model_id }}</strong>
                </div>
                <div class="model-card-badges">
                  <UiBadge v-if="isDefaultChatModel(provider.id, model.model_id)" variant="default">
                    {{ t('settings.defaultChatModel') }}
                  </UiBadge>
                  <UiBadge :variant="statusVariant(model.status)">
                    {{ statusLabel(model.status) }}
                  </UiBadge>
                </div>
              </div>

              <div class="model-card-capabilities">
                <span class="cap-pill tier-pill">{{ tierLabel(effectiveProfileFor(provider.id, model.model_id).tier) }}</span>
                <span v-if="savedProfileFor(provider.id, model.model_id)?.cost_tier" class="cap-pill cost-pill">
                  {{ costTierLabel(savedProfileFor(provider.id, model.model_id)!.cost_tier!) }}
                </span>
                <span v-if="formatContextWindow(savedProfileFor(provider.id, model.model_id)?.context_window)" class="cap-pill">
                  {{ formatContextWindow(savedProfileFor(provider.id, model.model_id)?.context_window) }}
                </span>
                <span
                  v-for="spec in effectiveProfileFor(provider.id, model.model_id).specialties.slice(0, 4)"
                  :key="spec"
                  class="cap-pill"
                  :class="{ suggested: !savedProfileFor(provider.id, model.model_id) }"
                >
                  {{ specialtyLabel(spec) }}
                </span>
                <span v-if="effectiveProfileFor(provider.id, model.model_id).specialties.length > 4" class="cap-pill">
                  +{{ effectiveProfileFor(provider.id, model.model_id).specialties.length - 4 }}
                </span>
              </div>

              <div class="model-card-footer">
                <div class="model-card-meta-left">
                  <span class="model-agent-ref-chip" data-testid="model-agent-ref-count">
                    <Users :size="12" />
                    <span>{{ t('settings.modelLibraryAgentRefs', { count: agentReferenceCountForModel(provider.id, model.model_id) }) }}</span>
                  </span>
                  <span v-for="source in model.configuration_sources" :key="source" class="quiet model-source-tag">
                    {{ configuredModelSourceLabel(source) }}
                  </span>
                </div>

                <div class="model-card-actions" @click.stop>
                  <UiButton
                    v-if="!isDefaultChatModel(provider.id, model.model_id)"
                    variant="ghost"
                    size="sm"
                    :disabled="busy"
                    :title="t('settings.setDefaultModel')"
                    @click="emit('set-default', provider.id, model.model_id)"
                  >
                    {{ t('settings.setDefaultModel') }}
                  </UiButton>
                  <UiButton
                    variant="outline"
                    size="sm"
                    :disabled="busy"
                    :aria-expanded="isModelSelected(provider.id, model.model_id)"
                    @click="toggleModelInspector(provider.id, model.model_id)"
                  >
                    <Settings2 data-icon="inline-start" />
                    {{ t('settings.modelParameters') }}
                  </UiButton>
                  <UiButton
                    variant="ghost"
                    size="sm"
                    :disabled="busy"
                    :title="t('settings.removeModel')"
                    @click="emit('remove', provider.id, model.model_id)"
                  >
                    <Trash2 :size="14" />
                  </UiButton>
                </div>
              </div>
            </article>
          </div>

          <div v-if="models.length === 0" class="center-empty-state">
            <Cpu :size="20" />
            <span>{{ t('settings.noProviderModels') }}</span>
          </div>
        </div>

        <div v-if="modelCatalogGroups.length === 0" class="center-empty-state">
          <Cpu :size="20" />
          <span>{{ hasActiveFilters ? t('settings.modelFilterEmpty') : t('settings.noConfiguredModels') }}</span>
        </div>
      </div>

      <ModelCapabilityInspector
        :model="inspectedModel"
        :provider="inspectedProvider"
        :provider-display-name="inspectedProviderDisplayName"
        :busy="busy"
        :is-default-chat="Boolean(inspectedModel && inspectedProvider && isDefaultChatModel(inspectedProvider.id, inspectedModel.model_id))"
        :references="inspectedModel && inspectedProvider ? referencesForModel(inspectedProvider.id, inspectedModel.model_id) : []"
        :status-label="statusLabel"
        :status-variant="statusVariant"
        :reference-kind-label="referenceKindLabel"
        :reference-detail-label="referenceDetailLabel"
        @close="inspectedTarget = null"
        @set-default="(pid, mid) => emit('set-default', pid, mid)"
        @locate-provider="(pid) => emit('locate-provider', pid)"
        @remove="(pid, mid) => emit('remove', pid, mid)"
        @save-profile="(prov, mid, prof) => emit('save-profile', prov, mid, prof)"
        @parameters-saved="handleParametersSaved"
        @reload="emit('reload')"
      />
    </div>
  </section>
</template>

<style scoped>
.model-capability-library {
  display: grid;
  gap: 14px;
}
.library-filter-panel {
  border: 1px solid var(--border-muted);
  border-radius: 12px;
  background: var(--surface-raised);
  padding: 12px;
  display: grid;
  gap: 10px;
}
.library-filter-rows {
  display: grid;
  gap: 8px;
}
.library-filter-split {
  display: flex;
  flex-wrap: wrap;
  gap: 12px 20px;
}
.library-filter-group {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.library-filter-label {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted);
  white-space: nowrap;
}
.library-chip-list {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}
.library-chip {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  min-height: var(--control-height-xs, 26px);
  padding: 2px 9px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease;
}
.library-chip:hover {
  border-color: var(--accent-primary);
}
.library-chip:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}
.library-chip.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 15%, var(--surface-raised));
  color: var(--text-primary);
  font-weight: 600;
}
.model-library-workspace {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 340px;
  gap: 14px;
  align-items: start;
}
@media (max-width: 1020px) {
  .model-library-workspace {
    grid-template-columns: minmax(0, 1fr);
  }
}
.model-library-catalog {
  display: grid;
  gap: 12px;
  min-width: 0;
}
.model-capability-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 10px;
  padding: 10px;
}
.model-capability-card {
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-raised);
  padding: 11px 12px;
  display: grid;
  gap: 9px;
  cursor: pointer;
  transition: border-color 120ms ease, box-shadow 120ms ease;
}
.model-capability-card:hover {
  border-color: color-mix(in srgb, var(--accent-primary) 55%, var(--border-muted));
}
.model-capability-card.selected {
  border-color: var(--accent-primary);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 18%, transparent);
}
.model-card-top {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
}
.model-card-identity {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.model-card-identity strong {
  font-size: 13px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.model-card-badges {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
  flex-wrap: wrap;
}
.model-card-capabilities {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.cap-pill {
  font-size: 10px;
  padding: 2px 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  white-space: nowrap;
  flex-shrink: 0;
  font-variant-numeric: tabular-nums;
}
.cap-pill.tier-pill {
  border-color: color-mix(in srgb, var(--accent-primary) 35%, var(--border-muted));
  color: var(--text-primary);
  font-weight: 500;
}
.cap-pill.cost-pill {
  border-color: color-mix(in srgb, var(--accent-warning) 40%, var(--border-muted));
}
.cap-pill.suggested {
  border-style: dashed;
  opacity: 0.85;
}
.model-card-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}
.model-card-meta-left {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}
.model-agent-ref-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  color: var(--text-secondary);
}
.model-source-tag {
  font-size: 10px;
}
.model-card-actions {
  display: flex;
  align-items: center;
  gap: 4px;
}
</style>
