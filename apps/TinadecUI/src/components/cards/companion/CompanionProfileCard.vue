<script setup lang="ts">
/**
 * The profile editor for the selected companion: who it is and which model answers as it.
 *
 * Editing lives here rather than only in Settings because the roster and the profile are read
 * together — picking a partner and then being sent to another page to change it is a detour the
 * centre can afford to skip. Memory and skills stay in Settings: those are lists with their own
 * flows, not two fields.
 */
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Save } from '@lucide/vue'
import { api, type ModelProviderInstanceDto } from '@/api'
import {
  companionApi,
  type CompanionDetail,
  type CompanionSummary,
} from '@/lib/companionApi'
import { isCompanionRevisionConflict } from '@/lib/companionConflict'
import { refreshCompanionCenter, selectedCompanion, selectedCompanionId, useCompanionCenter } from '@/lib/companionStore'
import { companionErrorText } from './companionText'

const { t } = useI18n()
useCompanionCenter()

const detail = ref<CompanionDetail | null>(null)
const providers = ref<ModelProviderInstanceDto[]>([])
const personaDraft = ref('')
const modelDraft = ref('')
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const conflict = ref(false)
let generation = 0

const selected = computed<CompanionSummary | null>(() => selectedCompanion.value)

/** `<providerInstanceId>|<model>` is the wire form; an empty string clears the pin. */
function splitModel(value: string): { providerId: string; model: string } {
  const separator = value.indexOf('|')
  if (separator <= 0) return { providerId: '', model: '' }
  return { providerId: value.slice(0, separator), model: value.slice(separator + 1) }
}

const modelOptions = computed(() => {
  const options: Array<{ value: string; label: string }> = []
  for (const provider of providers.value) {
    for (const model of provider.models ?? (provider.model ? [provider.model] : [])) {
      options.push({ value: `${provider.id}|${model}`, label: `${provider.display_name} · ${model}` })
    }
  }
  return options
})

async function load(): Promise<void> {
  const id = selectedCompanionId.value
  if (!id) {
    detail.value = null
    return
  }
  const token = ++generation
  loading.value = true
  error.value = ''
  try {
    const [loaded, catalog] = await Promise.all([
      companionApi.getCompanion(id),
      providers.value.length ? Promise.resolve(providers.value) : api.listModelProviders().catch(() => []),
    ])
    if (token !== generation) return
    detail.value = loaded
    providers.value = catalog
    personaDraft.value = loaded.persona_document ?? ''
    modelDraft.value = loaded.model_provider_id ? `${loaded.model_provider_id}|${loaded.model ?? ''}` : ''
  } catch (caught) {
    if (token !== generation) return
    error.value = companionErrorText(caught, t)
  } finally {
    if (token === generation) loading.value = false
  }
}

watch(selectedCompanionId, () => { void load() }, { immediate: true })

async function save(): Promise<void> {
  const current = detail.value
  if (!current || saving.value) return
  saving.value = true
  error.value = ''
  conflict.value = false
  try {
    const { providerId, model } = splitModel(modelDraft.value)
    const updated = await companionApi.patchCompanion(current.id, current.revision, {
      persona_document: personaDraft.value,
      // An empty value clears the pin so the tenant's route purpose decides again; a pinned
      // provider travels as "<providerInstanceId>|<model>".
      model: providerId ? `${providerId}|${model}` : '',
    })
    detail.value = updated
    personaDraft.value = updated.persona_document ?? ''
    modelDraft.value = updated.model_provider_id ? `${updated.model_provider_id}|${updated.model ?? ''}` : ''
    await refreshCompanionCenter()
  } catch (caught) {
    if (isCompanionRevisionConflict(caught)) {
      conflict.value = true
      return
    }
    error.value = companionErrorText(caught, t)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="companion-card">
    <header class="companion-card-head">
      <h2>{{ t('companion.center.profileTitle') }}</h2>
    </header>

    <p v-if="!selected" class="companion-card-note">{{ t('companion.center.profilePickCompanion') }}</p>
    <p v-else-if="loading" class="companion-card-note">{{ t('companion.center.loading') }}</p>

    <div v-else-if="detail" class="companion-profile">
      <label class="companion-profile-field">
        <span>{{ t('companion.center.profilePersona') }}</span>
        <textarea
          v-model="personaDraft"
          rows="6"
          data-testid="profile-persona"
          :placeholder="t('companion.center.profilePersonaPlaceholder')"
        />
      </label>

      <label class="companion-profile-field">
        <span>{{ t('companion.center.profileModel') }}</span>
        <select v-model="modelDraft" data-testid="profile-model">
          <option value="">{{ t('companion.center.profileModelRoute') }}</option>
          <option v-for="option in modelOptions" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
      </label>
      <p class="companion-hint">{{ t('companion.center.profileModelHint') }}</p>

      <p v-if="conflict" class="companion-card-error" role="alert">{{ t('companion.center.profileConflict') }}</p>
      <p v-else-if="error" class="companion-card-error" role="alert">{{ error }}</p>

      <button
        type="button"
        class="companion-profile-save"
        :disabled="saving"
        data-testid="profile-save"
        @click="save"
      >
        <Save :size="13" />
        <span>{{ saving ? t('companion.center.profileSaving') : t('companion.center.profileSave') }}</span>
      </button>
    </div>
  </section>
</template>

<style scoped>
.companion-profile {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 10px 12px 12px;
}

.companion-profile-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 12px;
  color: var(--text-secondary);
}

.companion-profile-field textarea,
.companion-profile-field select {
  width: 100%;
  padding: 6px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  font: inherit;
  font-size: 12px;
  color: var(--text-primary);
  background: var(--surface-raised);
  resize: vertical;
}

.companion-hint {
  margin: 0;
  font-size: 11px;
  color: var(--text-muted);
}

.companion-profile-save {
  display: inline-flex;
  align-self: flex-start;
  align-items: center;
  gap: 6px;
  padding: 5px 12px;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  font-size: 12px;
  color: var(--text-primary);
  background: var(--surface-raised);
  cursor: pointer;
}

.companion-profile-save:disabled {
  opacity: 0.55;
  cursor: default;
}
</style>
