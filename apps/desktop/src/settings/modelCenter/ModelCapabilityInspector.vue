<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Cpu, Link2, PanelRight, Save, Sparkles, Star, Trash2, X } from '@lucide/vue'
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
  model: ModelRow | null
  provider: ModelProviderInstanceDto | null
  providerDisplayName: string
  busy: boolean
  isDefaultChat: boolean
  references: ModelReferenceDto[]
  statusLabel: (status: string) => string
  statusVariant: (status: string) => 'default' | 'secondary' | 'destructive' | 'outline'
  referenceKindLabel: (kind: string) => string
  referenceDetailLabel: (reference: ModelReferenceDto) => string | null
}>()

const emit = defineEmits<{
  close: []
  'set-default': [providerId: string, modelId: string]
  'locate-provider': [providerId: string]
  remove: [providerId: string, modelId: string]
  'save-profile': [provider: ModelProviderInstanceDto, modelId: string, profile: ModelProfileDto | null]
  'parameters-saved': []
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

const savedProfile = computed<ModelProfileDto | null>(() =>
  props.model && props.provider ? props.provider.model_profiles?.[props.model.model_id] ?? null : null,
)

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
  const source = savedProfile.value ?? suggestModelProfile(modelId)
  profileDraft.specialties = [...(source.specialties ?? [])]
  profileDraft.tier = source.tier ?? 'balanced'
  profileDraft.notes = source.notes ?? ''
  profileDraft.contextWindow = source.context_window != null ? String(source.context_window) : ''
  profileDraft.costTier = (source.cost_tier as ModelCostTier | null) ?? ''
  profileBaseline.value = JSON.stringify(profileDraft)
}

watch(
  [() => props.model?.id, () => props.provider?.revision],
  ([nextId], [prevId]) => {
    if (!props.model) return
    if (nextId !== prevId || !profileDirty.value) {
      loadProfileDraft(props.model.model_id)
    }
  },
  { immediate: true },
)

function applySuggestedProfile() {
  if (!props.model) return
  const suggested = suggestModelProfile(props.model.model_id)
  profileDraft.specialties = [...suggested.specialties]
  profileDraft.tier = suggested.tier
  profileDraft.notes = suggested.notes ?? ''
}

function toggleSpecialty(spec: ModelSpecialty) {
  const idx = profileDraft.specialties.indexOf(spec)
  if (idx >= 0) profileDraft.specialties.splice(idx, 1)
  else profileDraft.specialties.push(spec)
}

function saveProfile() {
  if (!props.provider || !props.model) return
  const parsedCtx = profileDraft.contextWindow.trim() ? Number(profileDraft.contextWindow) : null
  const profile: ModelProfileDto = {
    specialties: [...profileDraft.specialties],
    tier: profileDraft.tier,
    notes: profileDraft.notes.trim() || null,
    ...(parsedCtx && Number.isFinite(parsedCtx) && parsedCtx > 0 ? { context_window: Math.round(parsedCtx) } : {}),
    ...(profileDraft.costTier ? { cost_tier: profileDraft.costTier } : {}),
  }
  emit('save-profile', props.provider, props.model.model_id, profile)
}

function clearProfile() {
  if (!props.provider || !props.model) return
  emit('save-profile', props.provider, props.model.model_id, null)
}
</script>

<template>
  <aside
    class="model-capability-inspector"
    data-testid="model-capability-inspector"
    :aria-label="t('settings.modelLibraryInspectorTitle')"
  >
    <div v-if="!model || !provider" class="inspector-empty-state">
      <Cpu :size="20" />
      <strong>{{ t('settings.modelLibraryInspectorTitle') }}</strong>
      <p>{{ t('settings.modelLibraryInspectorEmpty') }}</p>
    </div>

    <div v-else class="inspector-body">
      <header class="inspector-header">
        <div class="inspector-title">
          <strong :title="model.model_id">{{ model.model_id }}</strong>
          <span>{{ providerDisplayName }} · {{ provider.driver }}</span>
        </div>
        <UiButton
          type="button"
          variant="ghost"
          size="icon"
          :aria-label="t('common.close')"
          @click="emit('close')"
        >
          <X :size="15" />
        </UiButton>
      </header>

      <div class="inspector-status-strip">
        <UiBadge :variant="statusVariant(model.status)">{{ statusLabel(model.status) }}</UiBadge>
        <UiBadge v-if="isDefaultChat" variant="default">{{ t('settings.defaultChatModel') }}</UiBadge>
      </div>

      <div class="inspector-quick-actions">
        <UiButton
          v-if="!isDefaultChat"
          type="button"
          variant="outline"
          size="sm"
          :disabled="busy"
          @click="emit('set-default', provider.id, model.model_id)"
        >
          <Star :size="13" />
          <span>{{ t('settings.setDefaultModel') }}</span>
        </UiButton>
        <UiButton
          type="button"
          variant="ghost"
          size="sm"
          @click="emit('locate-provider', provider.id)"
        >
          <PanelRight :size="13" />
          <span>{{ t('settings.locateProvider') }}</span>
        </UiButton>
        <UiButton
          type="button"
          variant="ghost"
          size="sm"
          class="inspector-remove-btn"
          :disabled="busy"
          @click="emit('remove', provider.id, model.model_id)"
        >
          <Trash2 :size="13" />
          <span>{{ t('settings.removeModel') }}</span>
        </UiButton>
      </div>

      <!-- Active References -->
      <section class="inspector-card">
        <div class="inspector-card-head">
          <div class="inspector-card-title">
            <Link2 :size="13" />
            <strong>{{ t('settings.modelLibraryReferencesTitle') }}</strong>
          </div>
          <UiBadge variant="secondary">{{ references.length }}</UiBadge>
        </div>
        <div v-if="references.length > 0" class="inspector-ref-list">
          <span v-for="(reference, idx) in references" :key="idx" class="inspector-ref-pill">
            {{ referenceKindLabel(reference.reference_kind) }}<template v-if="reference.detail"> · {{ referenceDetailLabel(reference) }}</template>
          </span>
        </div>
        <p v-else class="quiet inspector-empty-line">{{ t('settings.noModelReferences') }}</p>
      </section>

      <!-- Capability Profile Editor -->
      <section class="model-profile-editor inspector-card" data-testid="model-profile-editor">
        <div class="model-profile-head">
          <div>
            <strong>{{ t('settings.modelProfileTitle') }}</strong>
            <p class="quiet">{{ t('settings.modelProfileHint') }}</p>
          </div>
          <div class="model-profile-head-actions">
            <UiBadge :variant="savedProfile ? 'default' : 'outline'">
              {{ savedProfile ? t('settings.modelProfileSavedBadge') : t('settings.modelProfileSuggestedBadge') }}
            </UiBadge>
            <UiButton
              type="button"
              variant="ghost"
              size="sm"
              :disabled="busy"
              data-testid="model-profile-suggest-btn"
              @click="applySuggestedProfile"
            >
              <Sparkles :size="13" />
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
            v-if="savedProfile"
            type="button"
            variant="ghost"
            size="sm"
            :disabled="busy || provider.revision == null"
            @click="clearProfile"
          >
            {{ t('settings.modelProfileClear') }}
          </UiButton>
          <UiButton
            type="button"
            size="sm"
            :disabled="busy || provider.revision == null"
            data-testid="save-model-profile-btn"
            @click="saveProfile"
          >
            <Save :size="14" />
            <span>{{ t('settings.modelProfileSave') }}</span>
          </UiButton>
        </div>
      </section>

      <!-- Inference Parameters Editor -->
      <section class="inspector-card">
        <ModelParametersEditor
          :provider="provider"
          :model="model.model_id"
          @saved="emit('parameters-saved')"
          @cancel="emit('close')"
          @reload="emit('reload')"
        />
      </section>
    </div>
  </aside>
</template>

<style scoped>
.model-capability-inspector {
  border: 1px solid var(--border-muted);
  border-radius: 12px;
  background: var(--surface-raised);
  padding: 14px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  align-self: start;
  position: sticky;
  top: 8px;
}
.inspector-empty-state {
  display: grid;
  justify-items: center;
  text-align: center;
  gap: 6px;
  padding: 28px 14px;
  color: var(--text-muted);
}
.inspector-empty-state strong {
  color: var(--text-primary);
  font-size: 13px;
}
.inspector-empty-state p {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
}
.inspector-body {
  display: grid;
  gap: 12px;
}
.inspector-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
}
.inspector-title {
  display: grid;
  gap: 2px;
  min-width: 0;
}
.inspector-title strong {
  font-size: 14px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.inspector-title span {
  font-size: 11px;
  color: var(--text-muted);
}
.inspector-status-strip {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}
.inspector-quick-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}
.inspector-remove-btn {
  color: var(--accent-danger);
}
.inspector-card {
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-section);
  padding: 11px 12px;
  display: grid;
  gap: 8px;
}
.inspector-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.inspector-card-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
}
.inspector-ref-list {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}
.inspector-ref-pill {
  font-size: 11px;
  padding: 2px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
}
.inspector-empty-line {
  margin: 0;
  font-size: 12px;
}
.model-profile-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}
.model-profile-head strong {
  font-size: 13px;
}
.model-profile-head p {
  margin: 2px 0 0;
  font-size: 11px;
  line-height: 1.45;
}
.model-profile-head-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}
.model-profile-block {
  display: grid;
  gap: 6px;
}
.model-profile-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}
.model-profile-chip {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  min-height: var(--control-height-xs, 26px);
  padding: 2px 9px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease, color 120ms ease;
}
.model-profile-chip.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 14%, var(--surface-raised));
  color: var(--text-primary);
  font-weight: 600;
}
.model-profile-chip:focus-visible,
.model-profile-tier-btn:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}
.model-profile-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 8px;
}
.model-profile-tier-group {
  display: inline-flex;
  border: 1px solid var(--border-muted);
  border-radius: var(--radius-control-md, 8px);
  overflow: hidden;
}
.model-profile-tier-btn {
  min-height: var(--control-height-xs, 26px);
  padding: 4px 10px;
  font-size: 11px;
  border: none;
  background: var(--surface-raised);
  color: var(--text-secondary);
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
}
.model-profile-tier-btn.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-weight: 600;
}
.model-profile-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
}
</style>
