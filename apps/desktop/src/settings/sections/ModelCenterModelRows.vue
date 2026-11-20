<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { Cpu, Info, Save, Settings2, Sparkles, Trash2 } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiBadge, UiButton, UiInput, UiLabel, UiSelectField } from '@/components/ui'
import ModelParametersEditor from '@/settings/sections/ModelParametersEditor.vue'
import {
  MODEL_PROFILE_COST_TIERS,
  MODEL_PROFILE_SPECIALTIES,
  MODEL_PROFILE_TIERS,
  suggestModelProfile,
} from '@/composables/useModelCenter'
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
  providerId: string
  provider: ModelProviderInstanceDto | null
  models: ModelRow[]
  busy: boolean
  defaultChatCandidate?: { provider_instance_id: string; model?: string | null } | null
  modelReferenceRows: Record<string, ModelReferenceDto[]>
  modelReferenceBusy: Record<string, boolean>
  editingModelParameters: { providerId: string; model: string } | null
  statusLabel: (status: string) => string
  statusVariant: (status: string) => 'default' | 'secondary' | 'destructive' | 'outline'
  configuredModelSourceLabel: (source: string) => string
  referenceKey: (providerInstanceId: string, modelId: string) => string
  referenceKindLabel: (kind: string) => string
  referenceDetailLabel: (reference: ModelReferenceDto) => string | null
}>()

function isDefaultChatModel(modelId: string): boolean {
  return props.defaultChatCandidate?.provider_instance_id === props.providerId
    && props.defaultChatCandidate?.model === modelId
}

const emit = defineEmits<{
  'set-default': [providerId: string, modelId: string]
  'toggle-parameters': [providerId: string, modelId: string]
  'toggle-references': [providerId: string, modelId: string]
  remove: [providerId: string, modelId: string]
  'parameters-saved': []
  'parameters-cancel': []
  'save-profile': [provider: ModelProviderInstanceDto, modelId: string, profile: ModelProfileDto | null]
  reload: []
}>()

const { t, te } = useI18n()

const profileDraft = reactive<{
  specialties: ModelSpecialty[]
  tier: ModelProfileTier
  notes: string
  contextWindow: string
  costTier: '' | ModelCostTier
}>({
  specialties: [],
  tier: 'balanced',
  notes: '',
  contextWindow: '',
  costTier: '',
})
const profileBaseline = ref('')
const profileDirty = computed(() => JSON.stringify(profileDraft) !== profileBaseline.value)

function savedProfileFor(modelId: string): ModelProfileDto | null {
  return props.provider?.model_profiles?.[modelId] ?? null
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

function loadProfileDraft(modelId: string) {
  const source = savedProfileFor(modelId) ?? suggestModelProfile(modelId)
  profileDraft.specialties = [...(source.specialties ?? [])]
  profileDraft.tier = source.tier ?? 'balanced'
  profileDraft.notes = source.notes ?? ''
  profileDraft.contextWindow = source.context_window != null ? String(source.context_window) : ''
  profileDraft.costTier = (source.cost_tier as ModelCostTier | null) ?? ''
  profileBaseline.value = JSON.stringify(profileDraft)
}

const activeEditingModel = computed(() =>
  props.editingModelParameters?.providerId === props.providerId
    ? props.editingModelParameters.model
    : null,
)

watch(
  [activeEditingModel, () => props.provider?.revision],
  ([modelId], [prevModel]) => {
    if (!modelId) return
    if (modelId !== prevModel || !profileDirty.value) {
      loadProfileDraft(modelId)
    }
  },
  { immediate: true },
)

function applySuggestedProfile(modelId: string) {
  const suggested = suggestModelProfile(modelId)
  profileDraft.specialties = [...suggested.specialties]
  profileDraft.tier = suggested.tier
  profileDraft.notes = suggested.notes ?? ''
}

function toggleSpecialty(spec: ModelSpecialty) {
  const idx = profileDraft.specialties.indexOf(spec)
  if (idx >= 0) {
    profileDraft.specialties.splice(idx, 1)
  } else {
    profileDraft.specialties.push(spec)
  }
}

function saveProfile(modelId: string) {
  if (!props.provider) return
  const parsedCtx = profileDraft.contextWindow.trim() ? Number(profileDraft.contextWindow) : null
  const profile: ModelProfileDto = {
    specialties: [...profileDraft.specialties],
    tier: profileDraft.tier,
    notes: profileDraft.notes.trim() || null,
    ...(parsedCtx && Number.isFinite(parsedCtx) && parsedCtx > 0 ? { context_window: Math.round(parsedCtx) } : {}),
    ...(profileDraft.costTier ? { cost_tier: profileDraft.costTier } : {}),
  }
  emit('save-profile', props.provider, modelId, profile)
}

function clearProfile(modelId: string) {
  if (!props.provider) return
  emit('save-profile', props.provider, modelId, null)
}
</script>

<template>
  <template v-for="model in models" :key="model.id">
    <div class="center-resource-list-row model-group-model">
      <div class="center-resource-primary">
        <Cpu :size="15" />
        <div>
          <strong :title="model.model_id">{{ model.model_id }}</strong>
          <div class="center-resource-meta">
            <span v-for="source in model.configuration_sources" :key="source">{{ configuredModelSourceLabel(source) }}</span>
            <span v-for="purpose in model.route_purposes" :key="purpose">{{ purpose }}</span>
            <template v-if="savedProfileFor(model.model_id)">
              <span class="model-profile-summary-pill">{{ tierLabel(savedProfileFor(model.model_id)!.tier) }}</span>
              <span
                v-for="spec in savedProfileFor(model.model_id)!.specialties.slice(0, 3)"
                :key="spec"
                class="model-profile-summary-pill"
              >
                {{ specialtyLabel(spec) }}
              </span>
              <span v-if="savedProfileFor(model.model_id)!.specialties.length > 3" class="model-profile-summary-pill">
                +{{ savedProfileFor(model.model_id)!.specialties.length - 3 }}
              </span>
            </template>
          </div>
          <div v-if="modelReferenceRows[referenceKey(providerId, model.model_id)]" class="model-reference-rows">
            <span v-for="(reference, index) in modelReferenceRows[referenceKey(providerId, model.model_id)]" :key="index">
              {{ referenceKindLabel(reference.reference_kind) }}<template v-if="reference.detail"> · {{ referenceDetailLabel(reference) }}</template>
            </span>
            <span v-if="modelReferenceRows[referenceKey(providerId, model.model_id)].length === 0" class="quiet">{{ t('settings.noModelReferences') }}</span>
          </div>
        </div>
      </div>
      <UiBadge v-if="isDefaultChatModel(model.model_id)" variant="default">{{ t('settings.defaultChatModel') }}</UiBadge>
      <UiBadge :variant="statusVariant(model.status)">{{ statusLabel(model.status) }}</UiBadge>
      <div class="center-resource-actions">
        <UiButton
          v-if="!isDefaultChatModel(model.model_id)"
          variant="outline"
          size="sm"
          :disabled="busy"
          :title="t('settings.setDefaultModel')"
          @click="emit('set-default', providerId, model.model_id)"
        >
          {{ t('settings.setDefaultModel') }}
        </UiButton>
        <UiButton
          variant="outline"
          size="sm"
          :disabled="busy"
          :aria-expanded="editingModelParameters?.providerId === providerId && editingModelParameters?.model === model.model_id"
          @click="emit('toggle-parameters', providerId, model.model_id)"
        >
          <Settings2 data-icon="inline-start" />
          {{ t('settings.modelParameters') }}
        </UiButton>
        <UiButton
          variant="ghost"
          size="sm"
          :disabled="modelReferenceBusy[referenceKey(providerId, model.model_id)]"
          :title="t('settings.toggleModelReferences')"
          @click="emit('toggle-references', providerId, model.model_id)"
        >
          <Info :size="14" />
        </UiButton>
        <UiButton
          variant="ghost"
          size="sm"
          :disabled="busy"
          :title="t('settings.removeModel')"
          @click="emit('remove', providerId, model.model_id)"
        >
          <Trash2 :size="14" />
        </UiButton>
      </div>
      <div
        v-if="provider && editingModelParameters?.providerId === providerId && editingModelParameters?.model === model.model_id"
        class="model-row-drawer"
      >
        <ModelParametersEditor
          :provider="provider"
          :model="model.model_id"
          @saved="emit('parameters-saved')"
          @cancel="emit('parameters-cancel')"
          @reload="emit('reload')"
        />
        <section class="model-profile-editor" data-testid="model-profile-editor">
          <div class="model-profile-head">
            <div>
              <strong>{{ t('settings.modelProfileTitle') }}</strong>
              <p class="quiet">{{ t('settings.modelProfileHint') }}</p>
            </div>
            <div class="model-profile-head-actions">
              <UiBadge :variant="savedProfileFor(model.model_id) ? 'default' : 'outline'">
                {{ savedProfileFor(model.model_id) ? t('settings.modelProfileSavedBadge') : t('settings.modelProfileSuggestedBadge') }}
              </UiBadge>
              <UiButton
                type="button"
                variant="ghost"
                size="sm"
                :disabled="busy"
                data-testid="model-profile-suggest-btn"
                @click="applySuggestedProfile(model.model_id)"
              >
                <Sparkles :size="14" />
                <span>{{ t('settings.modelProfileApplySuggested') }}</span>
              </UiButton>
            </div>
          </div>

          <div class="model-profile-block">
            <UiLabel>{{ t('settings.modelProfileSpecialties') }}</UiLabel>
            <div class="model-profile-chips" role="group" :aria-label="t('settings.modelProfileSpecialties')">
              <button
                v-for="spec in MODEL_PROFILE_SPECIALTIES"
                :key="spec"
                type="button"
                class="model-profile-chip"
                :class="{ active: profileDraft.specialties.includes(spec) }"
                :aria-pressed="profileDraft.specialties.includes(spec)"
                :disabled="busy"
                :data-specialty="spec"
                @click="toggleSpecialty(spec)"
              >
                {{ specialtyLabel(spec) }}
              </button>
            </div>
          </div>

          <div class="model-profile-grid">
            <div class="settings-field">
              <UiLabel>{{ t('settings.modelProfileTierLabel') }}</UiLabel>
              <div class="model-profile-tier-group" role="group" :aria-label="t('settings.modelProfileTierLabel')">
                <button
                  v-for="tier in MODEL_PROFILE_TIERS"
                  :key="tier"
                  type="button"
                  class="model-profile-tier-btn"
                  :class="{ active: profileDraft.tier === tier }"
                  :aria-pressed="profileDraft.tier === tier"
                  :disabled="busy"
                  :data-tier="tier"
                  @click="profileDraft.tier = tier"
                >
                  {{ tierLabel(tier) }}
                </button>
              </div>
            </div>
            <div class="settings-field">
              <UiLabel>{{ t('settings.modelProfileCostTierLabel') }}</UiLabel>
              <UiSelectField
                v-model="profileDraft.costTier"
                :aria-label="t('settings.modelProfileCostTierLabel')"
                :options="[
                  { value: '', label: t('settings.modelDefaultParameter') },
                  ...MODEL_PROFILE_COST_TIERS.map((cost) => ({ value: cost, label: costTierLabel(cost) })),
                ]"
              />
            </div>
            <div class="settings-field">
              <UiLabel>{{ t('settings.modelProfileContextWindowLabel') }}</UiLabel>
              <UiInput
                v-model="profileDraft.contextWindow"
                type="number"
                min="1024"
                step="1024"
                :disabled="busy"
                placeholder="128000"
              />
            </div>
          </div>

          <div class="settings-field">
            <UiLabel>{{ t('settings.modelProfileNotesLabel') }}</UiLabel>
            <textarea
              v-model="profileDraft.notes"
              class="settings-textarea"
              rows="2"
              maxlength="500"
              :disabled="busy"
              :placeholder="t('settings.modelProfileNotesPlaceholder')"
            ></textarea>
          </div>

          <div class="model-profile-actions">
            <UiButton
              v-if="savedProfileFor(model.model_id)"
              type="button"
              variant="ghost"
              size="sm"
              :disabled="busy || provider.revision == null"
              @click="clearProfile(model.model_id)"
            >
              {{ t('settings.modelProfileClear') }}
            </UiButton>
            <UiButton
              type="button"
              size="sm"
              :disabled="busy || provider.revision == null"
              data-testid="save-model-profile-btn"
              @click="saveProfile(model.model_id)"
            >
              <Save :size="14" />
              <span>{{ t('settings.modelProfileSave') }}</span>
            </UiButton>
          </div>
        </section>
      </div>
    </div>
  </template>
</template>

<style scoped>
.model-row-drawer {
  flex: 1 1 100%;
  display: grid;
  gap: 12px;
  margin-top: 8px;
}

.model-profile-summary-pill {
  padding: 1px 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised));
  color: var(--text-secondary);
  font-size: 11px;
}

.model-profile-editor {
  display: grid;
  gap: 12px;
  padding: 14px 16px;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: var(--radius-md);
}

.model-profile-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.model-profile-head strong {
  display: block;
  font-size: 13px;
}

.model-profile-head p {
  margin: 2px 0 0;
  font-size: 12px;
}

.model-profile-head-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.model-profile-block {
  display: grid;
  gap: 6px;
}

.model-profile-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.model-profile-chip {
  padding: 4px 10px;
  border-radius: 999px;
  border: 1px solid var(--border-default);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 12px;
  cursor: pointer;
  transition: background-color 140ms ease, border-color 140ms ease, color 140ms ease;
}

.model-profile-chip:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.model-profile-chip.active {
  border-color: color-mix(in srgb, var(--accent-primary) 55%, transparent);
  background: color-mix(in srgb, var(--accent-primary) 16%, var(--surface-raised));
  color: var(--text-primary);
  font-weight: 600;
}

.model-profile-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 170px), 1fr));
  gap: 12px;
  align-items: end;
}

.model-profile-tier-group {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 4px;
  padding: 3px;
  border-radius: 8px;
  background: var(--surface-chrome);
}

.model-profile-tier-btn {
  padding: 5px 8px;
  border: 1px solid transparent;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.model-profile-tier-btn.active {
  border-color: color-mix(in srgb, var(--accent-primary) 40%, transparent);
  background: var(--surface-selected);
  color: var(--text-primary);
  font-weight: 600;
}

.model-profile-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}
</style>
