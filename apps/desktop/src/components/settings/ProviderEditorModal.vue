<script setup lang="ts">
import { Database, Save, Terminal, X } from '@lucide/vue'
import { UiButton, UiInput, UiCard, UiLabel, UiSwitch } from '@/components/ui'
import { useModelSettings } from '@/pages/settings/modelSettings'

const {
  currentTemplate,
  formFields,
  formPlaceholders,
  providerForm,
  selectedProvider,
  showModal,
  modelCenterBusy,
  t,
  closeModal,
  saveProvider,
} = useModelSettings()
</script>

<template>
  <Transition name="modal-fade">
      <div v-if="showModal" class="model-provider-modal" @click.self="closeModal">
        <UiCard class="model-provider-modal-content">
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
                  <h3>{{ providerForm.id ? t('settings.editProviderTitle') : t('settings.newProvider') }}</h3>
                  <span class="modal-header-sub">{{ currentTemplate ? t(currentTemplate.display_name_key) : providerForm.driver }}</span>
                </div>
              </div>
              <UiButton variant="ghost" size="icon" @click="closeModal">
                <X :size="16" />
              </UiButton>
            </div>
          </template>

          <template #content>
            <p v-if="currentTemplate" class="template-summary">{{ t(currentTemplate.summary_key) }}</p>

            <div class="modal-form-section">
              <div class="modal-form-section-title">{{ t('settings.basicInfo') }}</div>
              <div class="settings-field">
                <UiLabel>{{ t('settings.displayName') }}</UiLabel>
                <UiInput v-model="providerForm.display_name" />
              </div>
            </div>

            <div v-if="formFields.base_url || formFields.model" class="modal-form-section">
              <div class="modal-form-section-title">{{ t('settings.connectionParams') }}</div>
              <div class="model-form-grid">
                <div v-if="formFields.base_url" class="settings-field">
                  <UiLabel>{{ t('settings.baseUrl') }}</UiLabel>
                  <UiInput v-model="providerForm.base_url" :placeholder="formPlaceholders.base_url" />
                </div>
                <div v-if="formFields.model" class="settings-field">
                  <UiLabel>{{ t('settings.modelLabel') }}</UiLabel>
                  <UiInput v-model="providerForm.model" :placeholder="formPlaceholders.model" />
                </div>
              </div>
            </div>

            <div v-if="formFields.api_key" class="modal-form-section">
              <div class="modal-form-section-title">{{ t('settings.authentication') }}</div>
              <div class="settings-field">
                <UiLabel>{{ t('settings.apiKey') }}</UiLabel>
                <UiInput
                  v-model="providerForm.api_key"
                  type="password"
                  :placeholder="selectedProvider?.has_api_key ? t('settings.apiKeyStored') : formPlaceholders.api_key ?? t('settings.apiKeyNotSet')"
                />
              </div>
            </div>

            <div v-if="formFields.binary_path || formFields.home_path" class="modal-form-section">
              <div class="modal-form-section-title">{{ t('settings.localPaths') }}</div>
              <div class="model-form-grid">
                <div v-if="formFields.binary_path" class="settings-field">
                  <UiLabel>{{ t('settings.binaryPath') }}</UiLabel>
                  <UiInput v-model="providerForm.binary_path" :placeholder="formPlaceholders.binary_path" />
                </div>
                <div v-if="formFields.home_path" class="settings-field">
                  <UiLabel>{{ t('settings.homePath') }}</UiLabel>
                  <UiInput v-model="providerForm.home_path" :placeholder="formPlaceholders.home_path" />
                </div>
              </div>
            </div>

            <div v-if="formFields.server_url || formFields.launch_args" class="modal-form-section">
              <div class="modal-form-section-title">{{ t('settings.serviceConfig') }}</div>
              <div class="model-form-grid">
                <div v-if="formFields.server_url" class="settings-field">
                  <UiLabel>{{ t('settings.serverUrl') }}</UiLabel>
                  <UiInput v-model="providerForm.server_url" :placeholder="formPlaceholders.server_url" />
                </div>
                <div v-if="formFields.launch_args" class="settings-field">
                  <UiLabel>{{ t('settings.launchArgs') }}</UiLabel>
                  <UiInput v-model="providerForm.launch_args" :placeholder="formPlaceholders.launch_args" />
                </div>
              </div>
            </div>

            <div class="modal-form-section">
              <div class="modal-form-section-title">{{ t('settings.status') }}</div>
              <div class="modal-enabled-row">
                <div>
                  <strong>{{ t('settings.enabled') }}</strong>
                  <span class="modal-enabled-hint">{{ providerForm.enabled ? t('settings.enabledHint') : t('settings.disabledHint') }}</span>
                </div>
                <UiSwitch v-model="providerForm.enabled" />
              </div>
            </div>

            <div v-if="currentTemplate" class="modal-capability-section">
              <div class="modal-form-section-title">{{ t('settings.supportedCapabilities') }}</div>
              <div class="model-capability-row">
                <span v-for="capability in currentTemplate.capabilities" :key="capability" class="provider-cap-tag">{{ capability }}</span>
              </div>
            </div>

            <div v-if="selectedProvider?.status_message" class="model-provider-note">
              <Terminal :size="14" />
              <span>{{ selectedProvider.status_message }}</span>
            </div>
          </template>

          <template #footer>
            <div class="modal-actions">
              <UiButton variant="outline" @click="closeModal">
                {{ t('settings.cancel') }}
              </UiButton>
              <UiButton :disabled="modelCenterBusy" @click="saveProvider()">
                <Save :size="14" />
                <span>{{ t('settings.save') }}</span>
              </UiButton>
            </div>
          </template>
        </UiCard>
      </div>
      </Transition>
</template>
