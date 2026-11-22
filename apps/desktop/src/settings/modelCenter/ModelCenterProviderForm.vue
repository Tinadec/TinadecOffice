<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Database, Eye, EyeOff, Save, Terminal, X } from '@lucide/vue'
import { UiButton, UiCard, UiCheckbox, UiInput, UiLabel, UiSelectField, UiSwitch } from '@/components/ui'
import {
  findTemplate,
  templateProtocols,
  type ChatProtocol,
} from '@/providerTemplates'
import { protocolLabelKey } from '@/runtimeCenterView'
import type { ModelProviderInstanceDto } from '@/api'

export interface ProviderFormState {
  id: string
  driver: string
  display_name: string
  connection_kind: string
  protocol: string
  base_url: string
  model: string
  models: string[]
  api_key: string
  clear_api_key: boolean
  binary_path: string
  home_path: string
  server_url: string
  launch_args: string
  enabled: boolean
}

const props = defineProps<{
  form: ProviderFormState
  selectedProvider: ModelProviderInstanceDto | null
  submitted: boolean
  errors: Record<string, string>
  dirty: boolean
  busy: boolean
}>()

const emit = defineEmits<{
  save: []
  cancel: []
}>()

const { t, te } = useI18n()
const showProviderKey = ref(false)

const currentTemplate = computed(() => findTemplate(props.form.driver))
const formFields = computed(() => {
  const fields = currentTemplate.value?.fields ?? {
    base_url: true, model: true, api_key: true,
    binary_path: false, home_path: false, server_url: false, launch_args: false,
  }
  return { ...fields }
})
const formPlaceholders = computed(() => currentTemplate.value?.placeholders ?? {})
const formProtocolOptions = computed<ChatProtocol[]>(() =>
  currentTemplate.value ? templateProtocols(currentTemplate.value) : [],
)
const protocolOptionLabel = (protocol: ChatProtocol) => t(protocolLabelKey(protocol))

function providerNote(provider: { status?: string | null; status_message?: string | null }): string | null {
  return provider.status_message && !['configured', 'disabled'].includes(provider.status ?? '')
    ? provider.status_message
    : null
}
</script>

<template>
  <form class="model-provider-form" @submit.prevent="emit('save')">
    <UiCard>
      <template #header>
        <div class="modal-header-row">
          <div class="modal-header-left">
            <span
              class="modal-provider-logo"
              :style="{ color: currentTemplate?.brand_color, backgroundColor: currentTemplate?.brand_bg }"
            >
              <span v-if="currentTemplate?.icon" class="provider-brand-mark" v-html="currentTemplate?.icon"></span>
              <Database v-else :size="18" />
            </span>
            <div class="modal-header-info">
              <h3>{{ form.id ? t('settings.editProviderTitle') : t('settings.newProvider') }}</h3>
              <span class="modal-header-sub">{{ currentTemplate ? t(currentTemplate.display_name_key) : form.driver }}</span>
            </div>
          </div>
          <UiButton type="button" variant="ghost" size="icon" :aria-label="t('common.close')" @click="emit('cancel')">
            <X :size="16" />
          </UiButton>
        </div>
      </template>

      <template #content>
        <p v-if="currentTemplate" class="template-summary">{{ t(currentTemplate.summary_key) }}</p>

        <div class="modal-form-section">
          <div class="modal-form-section-title">{{ t('settings.basicInfo') }}</div>
          <div class="settings-field">
            <UiLabel for="provider-display-name">{{ t('settings.displayName') }}</UiLabel>
            <UiInput
              id="provider-display-name"
              v-model="form.display_name"
              :aria-invalid="submitted && !!errors.display_name"
              :aria-describedby="submitted && errors.display_name ? 'provider-display-name-error' : undefined"
            />
            <span v-if="submitted && errors.display_name" id="provider-display-name-error" class="model-field-error" role="alert">
              {{ errors.display_name }}
            </span>
          </div>
        </div>

        <div v-if="formFields.base_url || formFields.model || formProtocolOptions.length > 0" class="modal-form-section">
          <div class="modal-form-section-title">{{ t('settings.connectionParams') }}</div>
          <div class="model-form-grid">
            <div v-if="formFields.base_url" class="settings-field">
              <UiLabel for="provider-base-url">{{ t('settings.baseUrl') }}</UiLabel>
              <UiInput
                id="provider-base-url"
                v-model="form.base_url"
                :placeholder="formPlaceholders.base_url"
                :aria-invalid="submitted && !!errors.base_url"
                :aria-describedby="submitted && errors.base_url ? 'provider-base-url-error' : undefined"
              />
              <span v-if="submitted && errors.base_url" id="provider-base-url-error" class="model-field-error" role="alert">
                {{ errors.base_url }}
              </span>
            </div>
            <div v-if="formFields.model" class="settings-field">
              <UiLabel for="provider-default-model">{{ t('settings.modelLabel') }}</UiLabel>
              <UiInput id="provider-default-model" v-model="form.model" :placeholder="formPlaceholders.model" />
              <span v-if="!form.id" class="quiet">
                {{ te('settings.initialModelHint') ? t('settings.initialModelHint') : 'Optional initial model ID — you can fetch or add more after saving.' }}
              </span>
            </div>
            <div v-if="formProtocolOptions.length > 0" class="settings-field">
              <UiLabel>{{ t('settings.protocol') }}</UiLabel>
              <UiSelectField
                v-model="form.protocol"
                :aria-label="t('settings.protocol')"
                :options="formProtocolOptions.map((option) => ({ value: option, label: protocolOptionLabel(option) }))"
              />
            </div>
          </div>
        </div>

        <div v-if="formFields.api_key" class="modal-form-section">
          <div class="modal-form-section-title">{{ t('settings.authentication') }}</div>
          <div class="settings-field">
            <UiLabel for="provider-api-key">{{ t('settings.apiKey') }}</UiLabel>
            <div class="model-key-row">
              <UiInput
                id="provider-api-key"
                v-model="form.api_key"
                :type="showProviderKey ? 'text' : 'password'"
                autocomplete="off"
                :disabled="form.clear_api_key"
                :placeholder="selectedProvider?.has_api_key ? t('settings.apiKeyStored') : formPlaceholders.api_key ?? t('settings.apiKeyNotSet')"
              />
              <UiButton
                type="button"
                variant="ghost"
                size="icon"
                :disabled="form.clear_api_key"
                :aria-label="showProviderKey ? t('settings.hideApiKey') : t('settings.showApiKey')"
                :aria-pressed="showProviderKey"
                @click="showProviderKey = !showProviderKey"
              >
                <EyeOff v-if="showProviderKey" :size="15" />
                <Eye v-else :size="15" />
              </UiButton>
            </div>
            <span v-if="selectedProvider?.has_api_key" class="quiet">{{ t('settings.apiKeyKeepHint') }}</span>
          </div>
          <label v-if="selectedProvider?.has_api_key" class="modal-clear-key">
            <UiCheckbox v-model="form.clear_api_key" :aria-label="t('settings.clearApiKey')" />
            <span>
              <strong>{{ t('settings.clearApiKey') }}</strong>
              <em>{{ t('settings.clearApiKeyHint') }}</em>
            </span>
          </label>
        </div>

        <div v-if="formFields.binary_path || formFields.home_path" class="modal-form-section">
          <div class="modal-form-section-title">{{ t('settings.localPaths') }}</div>
          <div class="model-form-grid">
            <div v-if="formFields.binary_path" class="settings-field">
              <UiLabel for="provider-binary-path">{{ t('settings.binaryPath') }}</UiLabel>
              <UiInput
                id="provider-binary-path"
                v-model="form.binary_path"
                :placeholder="formPlaceholders.binary_path"
                :aria-invalid="submitted && !!errors.binary_path"
                :aria-describedby="submitted && errors.binary_path ? 'provider-binary-path-error' : undefined"
              />
              <span v-if="submitted && errors.binary_path" id="provider-binary-path-error" class="model-field-error" role="alert">
                {{ errors.binary_path }}
              </span>
            </div>
            <div v-if="formFields.home_path" class="settings-field">
              <UiLabel>{{ t('settings.homePath') }}</UiLabel>
              <UiInput v-model="form.home_path" :placeholder="formPlaceholders.home_path" />
            </div>
          </div>
        </div>

        <div v-if="formFields.server_url || formFields.launch_args" class="modal-form-section">
          <div class="modal-form-section-title">{{ t('settings.serviceConfig') }}</div>
          <div class="model-form-grid">
            <div v-if="formFields.server_url" class="settings-field">
              <UiLabel for="provider-server-url">{{ t('settings.serverUrl') }}</UiLabel>
              <UiInput
                id="provider-server-url"
                v-model="form.server_url"
                :placeholder="formPlaceholders.server_url"
                :aria-invalid="submitted && !!errors.server_url"
                :aria-describedby="submitted && errors.server_url ? 'provider-server-url-error' : undefined"
              />
              <span v-if="submitted && errors.server_url" id="provider-server-url-error" class="model-field-error" role="alert">
                {{ errors.server_url }}
              </span>
            </div>
            <div v-if="formFields.launch_args" class="settings-field">
              <UiLabel>{{ t('settings.launchArgs') }}</UiLabel>
              <UiInput v-model="form.launch_args" :placeholder="formPlaceholders.launch_args" />
            </div>
          </div>
        </div>

        <div class="modal-form-section">
          <div class="modal-form-section-title">{{ t('settings.status') }}</div>
          <div class="modal-enabled-row">
            <div>
              <strong>{{ t('settings.enabled') }}</strong>
              <span class="modal-enabled-hint">{{ form.enabled ? t('settings.enabledHint') : t('settings.disabledHint') }}</span>
            </div>
            <UiSwitch v-model="form.enabled" />
          </div>
        </div>

        <div v-if="currentTemplate" class="modal-capability-section">
          <div class="modal-form-section-title">{{ t('settings.supportedCapabilities') }}</div>
          <div class="model-capability-row">
            <span v-for="capability in currentTemplate.capabilities" :key="capability" class="provider-cap-tag">{{ capability }}</span>
          </div>
        </div>

        <div v-if="selectedProvider && providerNote(selectedProvider)" class="model-provider-note">
          <Terminal :size="14" />
          <span>{{ selectedProvider.status_message }}</span>
        </div>
      </template>

      <template #footer>
        <div class="modal-actions model-provider-form-actions">
          <span v-if="dirty" class="modal-unsaved" role="status">{{ t('settings.unsavedChanges') }}</span>
          <UiButton type="button" variant="outline" @click="emit('cancel')">
            {{ t('settings.cancel') }}
          </UiButton>
          <UiButton type="submit" :disabled="busy">
            <Save :size="14" />
            <span>{{ t('settings.save') }}</span>
          </UiButton>
        </div>
      </template>
    </UiCard>
  </form>
</template>
