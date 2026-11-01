<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiButton, UiInput } from '@/components/ui'
import { api as baseApi, type ModelParametersDto, type ModelProviderInstanceDto } from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { supportsReasoningEffort, type ReasoningEffortChoice } from '@/lib/reasoningEffort'
import ReasoningEffortSlider from '@/components/ReasoningEffortSlider.vue'
const api = scopedApi(baseApi, () => 'user')

const props = defineProps<{ provider: ModelProviderInstanceDto; model: string }>()
const emit = defineEmits<{ saved: []; cancel: []; reload: [] }>()
const { t } = useI18n()
const busy = ref(false)
const error = ref('')
const stale = ref(false)
const form = reactive({
  effort: '' as ReasoningEffortChoice,
  temperature: '',
  topP: '',
  maxTokens: '',
  imageEnabled: false,
  imageMaxBytes: '',
  imageMaxImages: '',
})
const supportsReasoning = computed(() => supportsReasoningEffort(props.provider.protocol))
const temperatureMax = computed(() => props.provider.protocol === 'anthropic-messages' ? 1 : 2)
const omitsSampling = computed(() => Boolean(form.effort && form.effort !== 'none' && /^(gpt-[56]|o[134])/i.test(props.model)))
const numberFields = computed(() => [
  { key: 'temperature' as const, label: 'settings.modelTemperature', min: 0, max: temperatureMax.value, step: '0.1', disabled: omitsSampling.value },
  { key: 'topP' as const, label: 'settings.modelTopP', min: 0, max: 1, step: '0.01', disabled: omitsSampling.value },
  { key: 'maxTokens' as const, label: 'settings.modelMaxOutput', min: 1, max: undefined, step: '1', disabled: false },
])
const invalidFields = computed(() => new Set(numberFields.value.filter(field => {
  if (field.disabled || !form[field.key].trim()) return false
  const value = Number(form[field.key])
  return !Number.isFinite(value) || value < field.min || (field.max !== undefined && value > field.max)
    || (field.key === 'maxTokens' && !Number.isSafeInteger(value))
}).map(field => field.key)))
const imageEditable = computed(() => props.provider.image_input?.editable === true)
const imageInvalid = computed(() => {
  if (!imageEditable.value) return false
  const bytes = form.imageMaxBytes.trim() ? Number(form.imageMaxBytes) : props.provider.image_input?.base_max_image_bytes ?? 20 * 1024 * 1024
  const images = form.imageMaxImages.trim() ? Number(form.imageMaxImages) : props.provider.image_input?.base_max_images_per_turn ?? 8
  return !Number.isSafeInteger(bytes) || bytes <= 0 || bytes > 32 * 1024 * 1024
    || !Number.isSafeInteger(images) || images <= 0 || images > 8
})

/**
 * The revision the open draft was built on. A refresh may replace the saved projection while this
 * editor is open; sending the new revision with the old draft would silently overwrite a change the
 * user never saw, so the draft keeps its own revision and a concurrent write surfaces as 412.
 */
const openedRevision = ref<number | null>(props.provider.revision ?? null)
const openedProtocol = ref(props.provider.protocol ?? '')
const baseline = ref('')
const draftKept = ref(false)
const snapshot = () => JSON.stringify(form)
const dirty = computed(() => snapshot() !== baseline.value)

function applyFromProps() {
  const settings = props.provider.model_parameters?.[props.model] ?? {}
  Object.assign(form, { effort: settings.reasoning_effort ?? '', temperature: String(settings.temperature ?? ''), topP: String(settings.top_p ?? ''), maxTokens: String(settings.max_output_tokens ?? '') })
  const image = props.provider.image_input
  const override = image?.model_overrides?.[props.model]
  Object.assign(form, {
    imageEnabled: override?.enabled ?? image?.base_enabled ?? image?.enabled ?? false,
    imageMaxBytes: String(override?.max_image_bytes ?? image?.base_max_image_bytes ?? image?.max_image_bytes ?? ''),
    imageMaxImages: String(override?.max_images_per_turn ?? image?.base_max_images_per_turn ?? image?.max_images_per_turn ?? ''),
  })
  baseline.value = snapshot()
  openedRevision.value = props.provider.revision ?? null
  openedProtocol.value = props.provider.protocol ?? ''
  error.value = ''
  stale.value = false
  draftKept.value = false
}

// A string key, not an array: an array getter returns a fresh reference on every provider replace,
// which would re-apply the saved projection (and wipe the draft) when only the revision changed.
const draftKey = computed(() => `${props.provider.id}\u0000${props.model}`)
watch(draftKey, () => {
  applyFromProps()
}, { immediate: true })

// A refresh only updates the saved projection. Unsaved choices stay as typed — unless the protocol
// changed underneath, because then the draft describes a different provider and cannot be applied.
watch(() => props.provider.revision, () => {
  if (dirty.value && (props.provider.protocol ?? '') === openedProtocol.value) {
    draftKept.value = true
    return
  }
  applyFromProps()
})

/** Explicit "load latest": discarding the draft is the point of this action, not a side effect. */
function loadLatest() {
  applyFromProps()
  emit('reload')
}

async function save() {
  const expectedRevision = openedRevision.value ?? props.provider.revision
  if (busy.value || invalidFields.value.size > 0 || imageInvalid.value || expectedRevision == null) return
  busy.value = true
  error.value = ''
  const settings: ModelParametersDto = {}
  if (supportsReasoning.value && form.effort) settings.reasoning_effort = form.effort
  if (!omitsSampling.value && form.temperature.trim()) settings.temperature = Number(form.temperature)
  if (!omitsSampling.value && form.topP.trim()) settings.top_p = Number(form.topP)
  if (form.maxTokens.trim()) settings.max_output_tokens = Number(form.maxTokens)
  const parameters = { ...props.provider.model_parameters }
  if (Object.keys(settings).length > 0) parameters[props.model] = settings
  else delete parameters[props.model]
  let imageInput = undefined
  if (imageEditable.value && props.provider.image_input) {
    const base = props.provider.image_input
    const overrides = { ...(base.model_overrides ?? {}) }
    const maxBytes = form.imageMaxBytes.trim() ? Number(form.imageMaxBytes) : base.base_max_image_bytes
    const maxImages = form.imageMaxImages.trim() ? Number(form.imageMaxImages) : base.base_max_images_per_turn
    if (form.imageEnabled === base.base_enabled
      && maxBytes === base.base_max_image_bytes
      && maxImages === base.base_max_images_per_turn) delete overrides[props.model]
    else overrides[props.model] = { enabled: form.imageEnabled, max_image_bytes: maxBytes, max_images_per_turn: maxImages }
    imageInput = {
      enabled: base.base_enabled,
      max_image_bytes: base.base_max_image_bytes,
      max_images_per_turn: base.base_max_images_per_turn,
      model_overrides: overrides,
    }
  }
  try {
    await api.saveModelProvider(props.provider.id, {
      driver: props.provider.driver, display_name: props.provider.display_name, connection_kind: props.provider.connection_kind,
      enabled: props.provider.enabled, model_parameters: parameters, ...(imageInput ? { image_input: imageInput } : {}),
    }, { expected_revision: expectedRevision })
    emit('saved')
  } catch (err) {
    stale.value = [412, 428].includes((err as { status?: number }).status ?? 0)
    error.value = stale.value ? t('settings.modelParametersStale') : err instanceof Error ? err.message : String(err)
  } finally { busy.value = false }
}
</script>

<template>
  <form class="model-parameters-editor" :aria-busy="busy" @submit.prevent="save">
    <fieldset :disabled="busy">
      <legend>{{ t('settings.modelParametersFor', { model }) }}</legend>
      <p class="quiet" id="model-parameters-help">{{ t('settings.modelParametersHint') }}</p>
      <div class="model-parameters-fields">
        <div v-if="supportsReasoning" class="settings-field model-reasoning-field">
          <ReasoningEffortSlider id="model-reasoning" v-model="form.effort" :label="t('settings.modelReasoningEffort')" />
        </div>
        <div v-for="field in numberFields" :key="field.key" class="settings-field" :data-invalid="invalidFields.has(field.key) || undefined" :data-disabled="field.disabled || undefined">
          <label :for="`model-parameter-${field.key}`">{{ t(field.label) }}</label>
          <UiInput :id="`model-parameter-${field.key}`" v-model="form[field.key]" type="number" :min="field.min" :max="field.max" :step="field.step" :disabled="field.disabled"
            :aria-invalid="invalidFields.has(field.key)" :aria-describedby="invalidFields.has(field.key) ? `model-error-${field.key}` : 'model-parameters-help'" :placeholder="t('settings.modelDefaultParameter')" />
          <span v-if="invalidFields.has(field.key)" :id="`model-error-${field.key}`" class="model-parameter-error">{{ t(field.key === 'maxTokens' ? 'settings.modelIntegerRequired' : 'settings.modelRangeRequired', { max: field.max }) }}</span>
        </div>
      </div>
      <section class="model-image-input" :data-disabled="!imageEditable || undefined">
        <label class="model-image-toggle">
          <input v-model="form.imageEnabled" type="checkbox" :disabled="!imageEditable">
          <span>{{ t('settings.modelImageInput') }}</span>
        </label>
        <p class="quiet">
          {{ provider.image_input?.source === 'acp_snapshot'
            ? t('settings.modelImageAcpSnapshot')
            : provider.image_input?.source === 'protocol_unsupported'
              ? t('settings.modelImageUnsupported')
              : t('settings.modelImageConfigured') }}
        </p>
        <div class="model-parameters-fields">
          <div class="settings-field" :data-invalid="imageInvalid || undefined">
            <label for="model-image-max-bytes">{{ t('settings.modelImageMaxBytes') }}</label>
            <UiInput id="model-image-max-bytes" v-model="form.imageMaxBytes" type="number" min="1" :max="32 * 1024 * 1024" step="1" :disabled="!imageEditable" :aria-invalid="imageInvalid" />
          </div>
          <div class="settings-field" :data-invalid="imageInvalid || undefined">
            <label for="model-image-max-count">{{ t('settings.modelImageMaxCount') }}</label>
            <UiInput id="model-image-max-count" v-model="form.imageMaxImages" type="number" min="1" max="8" step="1" :disabled="!imageEditable" :aria-invalid="imageInvalid" />
          </div>
        </div>
        <p v-if="imageInvalid" class="model-parameter-error" role="alert">{{ t('settings.modelImageBudgetInvalid') }}</p>
      </section>
      <p v-if="omitsSampling" class="quiet" role="note">{{ t('settings.modelSamplingUnavailable') }}</p>
      <p v-if="draftKept" class="quiet" role="note">{{ t('settings.modelParametersDraftKept') }}</p>
      <p v-if="error" class="model-parameter-error" role="alert">{{ error }}</p>
      <div class="model-parameters-actions">
        <UiButton type="button" variant="ghost" size="sm" @click="Object.assign(form, { effort: '', temperature: '', topP: '', maxTokens: '' })">{{ t('settings.modelResetParameters') }}</UiButton>
        <UiButton v-if="stale" type="button" variant="outline" size="sm" @click="loadLatest">{{ t('settings.modelParametersLoadLatest') }}</UiButton>
        <UiButton type="button" variant="ghost" size="sm" @click="emit('cancel')">{{ t('settings.cancel') }}</UiButton>
        <UiButton type="submit" size="sm" :disabled="busy || stale || invalidFields.size > 0 || imageInvalid || provider.revision == null">{{ t(busy ? 'settings.modelParametersSaving' : 'settings.save') }}</UiButton>
      </div>
    </fieldset>
  </form>
</template>

<style scoped>
.model-parameters-editor { padding: var(--spacing-4, 16px); background: var(--surface-section); border-radius: var(--radius-md); }
fieldset { border: 0; padding: 0; margin: 0; min-width: 0; }
legend { font-weight: 600; margin-bottom: var(--spacing-2, 8px); overflow-wrap: anywhere; }
.model-parameters-fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr)); gap: var(--spacing-3, 12px); margin-top: var(--spacing-3, 12px); }
.model-parameters-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: var(--spacing-2, 8px); margin-top: var(--spacing-4, 16px); }
.model-reasoning-field { grid-column: 1 / -1; }
.model-image-input { margin-top: var(--spacing-4, 16px); padding-top: var(--spacing-3, 12px); border-top: 1px solid var(--border-default); }
.model-image-toggle { display: inline-flex; align-items: center; gap: var(--spacing-2, 8px); font-weight: 600; }
.model-image-toggle input { width: 16px; height: 16px; margin: 0; }
.model-image-input[data-disabled] .model-image-toggle { opacity: .7; }
.model-parameter-error { color: var(--accent-danger); font-size: var(--font-size-sm, 12px); }
</style>
