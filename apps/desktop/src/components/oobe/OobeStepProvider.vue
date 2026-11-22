<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  CheckCircle2,
  Cloud,
  Cpu,
  Loader2,
  RefreshCw,
  Server,
  ShieldAlert,
} from '@lucide/vue'
import { api, type ModelProviderInstanceDto } from '@/api'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'
import { PROVIDER_TEMPLATES, type ProviderTemplate } from '@/providerTemplates'
import { suggestModelProfile } from '@/composables/useModelCenter'
import {
  classifyProbeError,
  isProviderEffectivelyConfigured,
  useOobe,
  type ClassifiedProbeError,
} from '@/composables/useOobe'

type ProviderPath = 'cloud' | 'local' | 'custom'

const { t, te } = useI18n()
const { providerVerified } = useOobe()

function label(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

const selectedPath = ref<ProviderPath>('cloud')
const existingProviders = ref<ModelProviderInstanceDto[]>([])
const hasOnlyKeylessSeed = ref(false)

const cloudTemplates = computed(() =>
  PROVIDER_TEMPLATES.filter((tpl) => tpl.category === 'cloud-api' && tpl.connection_kind === 'api-key'),
)
const localTemplates = computed(() =>
  PROVIDER_TEMPLATES.filter((tpl) => tpl.category === 'local-server'),
)

const selectedDriver = ref('openai-compatible')
const baseUrl = ref('https://api.openai.com/v1')
const modelId = ref('gpt-5.4-mini')
const apiKey = ref('')
const saving = ref(false)
const probeStatus = ref<'idle' | 'probing' | 'verified' | 'not_run' | 'failed'>('idle')
const probeError = ref<ClassifiedProbeError | null>(null)
const savedProviderId = ref<string | null>(null)

const driverOptions = computed(() => {
  const list = selectedPath.value === 'local' ? localTemplates.value : cloudTemplates.value
  return list.map((tpl) => ({
    value: tpl.driver,
    label: label(tpl.display_name_key, tpl.driver),
  }))
})

const activeTemplate = computed<ProviderTemplate | undefined>(() =>
  PROVIDER_TEMPLATES.find((tpl) => tpl.driver === selectedDriver.value),
)

const isLocalPath = computed(() => selectedPath.value === 'local')

function applyTemplate(driver: string): void {
  selectedDriver.value = driver
  const tpl = PROVIDER_TEMPLATES.find((item) => item.driver === driver)
  if (!tpl) return
  baseUrl.value = tpl.default_base_url ?? ''
  modelId.value = tpl.default_model ?? ''
  probeError.value = null
}

function choosePath(path: ProviderPath): void {
  selectedPath.value = path
  probeError.value = null
  if (path === 'cloud') {
    applyTemplate(cloudTemplates.value[0]?.driver ?? 'openai-compatible')
  } else if (path === 'local') {
    applyTemplate(localTemplates.value[0]?.driver ?? 'ollama')
    apiKey.value = ''
  } else {
    selectedDriver.value = 'openai-compatible'
    baseUrl.value = ''
    modelId.value = ''
  }
}

function probeErrorMessage(err: ClassifiedProbeError): string {
  switch (err.kind) {
    case 'auth':
      return label('oobe.provider.errAuth', 'API key was rejected (401/403). Check your key and retry.')
    case 'model_not_found':
      return label('oobe.provider.errModelNotFound', 'Model not found (404). Check the model ID.')
    case 'network':
      return label('oobe.provider.errNetwork', 'Could not reach endpoint. Check Base URL or ensure local service is running.')
    case 'rate_limit':
      return label('oobe.provider.errRateLimit', 'Rate limit or quota reached (429). Retry later or switch model.')
    case 'not_run':
      return label('oobe.provider.probeNotRun', 'Not verified yet')
    default:
      return label('oobe.provider.errUnknown', `Probe failed: ${err.detail}`, { detail: err.detail || 'unknown error' })
  }
}

async function loadExisting(): Promise<void> {
  if (typeof api.listModelProviders !== 'function') return
  try {
    const list = await api.listModelProviders()
    existingProviders.value = list
    const configured = list.filter(isProviderEffectivelyConfigured)
    // Explicitly ignore keyless DevSeed placeholder rows so user sees clean empty state.
    hasOnlyKeylessSeed.value = list.length > 0 && configured.length === 0
    if (configured.length > 0) {
      const first = configured[0]
      savedProviderId.value = first.id
      selectedDriver.value = first.driver
      baseUrl.value = first.base_url ?? ''
      modelId.value = first.model ?? first.models?.[0] ?? ''
      selectedPath.value = first.connection_kind === 'local-server' ? 'local' : 'cloud'
    }
  } catch {
    // Keep clean defaults.
  }
}

async function runLiveProbe(providerId: string, model: string): Promise<boolean> {
  probeStatus.value = 'probing'
  probeError.value = null
  try {
    const probeResult = await api.probeModel(true)
    if (probeResult.status === 'ready') {
      // Only write default chat route AFTER live probe passes!
      if (typeof api.saveModelRoute === 'function') {
        let expectedRevision: number | undefined
        try {
          const routes = await api.listModelRoutes?.()
          const chatRoute = routes?.find((r) => r.purpose === 'chat')
          expectedRevision = chatRoute?.revision
        } catch {
          // Proceed without revision if route does not exist yet.
        }
        await api.saveModelRoute(
          'chat',
          { candidates: [{ provider_instance_id: providerId, model: model || null }] },
          undefined,
          expectedRevision !== undefined ? { expected_revision: expectedRevision } : undefined,
        )
      }
      probeStatus.value = 'verified'
      providerVerified.value = true
      return true
    }

    const classified = classifyProbeError(probeResult)
    probeError.value = classified
    probeStatus.value = classified.kind === 'not_run' ? 'not_run' : 'failed'
    return false
  } catch (err) {
    const classified = classifyProbeError(err)
    probeError.value = classified
    probeStatus.value = classified.kind === 'not_run' ? 'not_run' : 'failed'
    return false
  }
}

async function saveAndVerify(): Promise<void> {
  const trimmedModel = modelId.value.trim()
  const trimmedBaseUrl = baseUrl.value.trim()
  const trimmedKey = apiKey.value.trim()
  if (!trimmedModel || (!isLocalPath.value && !trimmedKey && !savedProviderId.value)) {
    return
  }

  saving.value = true
  probeError.value = null
  try {
    const tpl = activeTemplate.value
    const profile = suggestModelProfile(trimmedModel)
    const displayName =
      selectedPath.value === 'custom'
        ? 'Custom OpenAI-Compatible'
        : label(tpl?.display_name_key ?? '', selectedDriver.value)

    const matchingExisting = existingProviders.value.find(
      (p) =>
        (savedProviderId.value && p.id === savedProviderId.value) ||
        (p.driver === selectedDriver.value && !isProviderEffectivelyConfigured(p)),
    )

    const payload = {
      driver: selectedDriver.value,
      protocol: tpl?.protocol ?? 'openai-chat',
      display_name: matchingExisting?.display_name || displayName,
      connection_kind: isLocalPath.value ? 'local-server' : 'api-key',
      base_url: trimmedBaseUrl || null,
      model: trimmedModel,
      models: [trimmedModel],
      model_profiles: { [trimmedModel]: profile },
      ...(trimmedKey ? { api_key: trimmedKey } : {}),
      enabled: true,
    }

    let saved: ModelProviderInstanceDto
    if (matchingExisting && typeof api.saveModelProvider === 'function') {
      saved = await api.saveModelProvider(matchingExisting.id, payload, {
        expected_revision: matchingExisting.revision,
      })
    } else {
      saved = await api.createModelProvider(payload)
    }

    savedProviderId.value = saved.id
    apiKey.value = ''
    await runLiveProbe(saved.id, trimmedModel)
  } catch (err) {
    probeError.value = classifyProbeError(err)
    probeStatus.value = 'failed'
  } finally {
    saving.value = false
  }
}

async function retryProbeOnly(): Promise<void> {
  if (!savedProviderId.value) {
    await saveAndVerify()
    return
  }
  saving.value = true
  try {
    await runLiveProbe(savedProviderId.value, modelId.value.trim())
  } finally {
    saving.value = false
  }
}

onMounted(() => {
  void loadExisting()
})
</script>

<template>
  <div class="oobe-step" data-testid="oobe-step-provider">
    <div class="oobe-step-hero">
      <h3>{{ label('oobe.provider.title', 'Connect your first model') }}</h3>
      <p>{{ label('oobe.provider.subtitle', 'Choose a provider path, enter credentials, and verify with a live 1-token probe.') }}</p>
    </div>

    <p v-if="hasOnlyKeylessSeed && probeStatus !== 'verified'" class="oobe-seed-hint" data-testid="oobe-devseed-hint">
      <ShieldAlert :size="14" aria-hidden="true" />
      <span>{{ label('oobe.provider.devSeedEmptyHint', 'No configured API key detected yet.') }}</span>
    </p>

    <div class="oobe-path-cards">
      <button
        type="button"
        class="oobe-path-card"
        :class="{ active: selectedPath === 'cloud' }"
        data-testid="oobe-path-cloud"
        @click="choosePath('cloud')"
      >
        <Cloud :size="18" aria-hidden="true" />
        <strong>{{ label('oobe.provider.cloudCard', 'Cloud Provider') }}</strong>
      </button>
      <button
        type="button"
        class="oobe-path-card"
        :class="{ active: selectedPath === 'local' }"
        data-testid="oobe-path-local"
        @click="choosePath('local')"
      >
        <Cpu :size="18" aria-hidden="true" />
        <strong>{{ label('oobe.provider.localCard', 'Local Runtime (Ollama / LM Studio / vLLM)') }}</strong>
      </button>
      <button
        type="button"
        class="oobe-path-card"
        :class="{ active: selectedPath === 'custom' }"
        data-testid="oobe-path-custom"
        @click="choosePath('custom')"
      >
        <Server :size="18" aria-hidden="true" />
        <strong>{{ label('oobe.provider.customCard', 'Custom OpenAI-Compatible Endpoint') }}</strong>
      </button>
    </div>

    <div class="oobe-form-card">
      <div v-if="selectedPath !== 'custom'" class="oobe-form-field">
        <label>{{ label('oobe.provider.cloudCard', 'Provider') }}</label>
        <UiSelectField
          :model-value="selectedDriver"
          :options="driverOptions"
          :aria-label="label('oobe.provider.cloudCard', 'Provider')"
          @update:model-value="applyTemplate"
        />
      </div>

      <div class="oobe-form-row">
        <div class="oobe-form-field">
          <label>{{ label('oobe.provider.baseUrl', 'Base URL') }}</label>
          <UiInput
            v-model="baseUrl"
            :placeholder="activeTemplate?.placeholders.base_url || 'https://api.openai.com/v1'"
            data-testid="oobe-provider-baseurl"
          />
        </div>
        <div class="oobe-form-field">
          <label>{{ label('oobe.provider.modelName', 'Model ID') }}</label>
          <UiInput
            v-model="modelId"
            :placeholder="activeTemplate?.placeholders.model || 'gpt-5.4-mini'"
            data-testid="oobe-provider-model"
          />
        </div>
      </div>

      <div class="oobe-form-field">
        <label>
          {{ label('oobe.provider.apiKey', 'API Key') }}
          <small v-if="isLocalPath">({{ label('oobe.provider.apiKeyOptionalLocal', 'Optional for local endpoints') }})</small>
        </label>
        <UiInput
          v-model="apiKey"
          type="password"
          :placeholder="isLocalPath ? label('oobe.provider.apiKeyOptionalLocal', 'Optional for local endpoints') : (activeTemplate?.placeholders.api_key || 'sk-...')"
          data-testid="oobe-provider-apikey"
        />
      </div>

      <p class="oobe-auto-profile-hint">
        {{ label('oobe.provider.autoProfileHint', 'Capability profile tags will be auto-suggested from the model family.') }}
      </p>

      <div class="oobe-verify-row">
        <UiButton
          size="sm"
          :disabled="saving || !modelId.trim() || (!isLocalPath && !apiKey.trim() && !savedProviderId)"
          data-testid="oobe-save-verify-btn"
          @click="saveAndVerify"
        >
          <Loader2 v-if="saving || probeStatus === 'probing'" :size="14" class="spinning" />
          <span>{{ saving || probeStatus === 'probing' ? label('oobe.provider.verifying', 'Probing model...') : label('oobe.provider.saveAndVerify', 'Save & Verify') }}</span>
        </UiButton>

        <UiBadge v-if="probeStatus === 'verified'" variant="default" data-testid="oobe-probe-verified">
          <CheckCircle2 :size="12" />
          <span>{{ label('oobe.provider.verified', 'Verified & set as default chat model') }}</span>
        </UiBadge>
        <UiBadge v-else-if="probeStatus === 'not_run'" variant="secondary" data-testid="oobe-probe-not-run">
          <span>{{ label('oobe.provider.probeNotRun', 'Not verified yet') }}</span>
        </UiBadge>
      </div>

      <div v-if="probeError && probeStatus === 'failed'" class="oobe-probe-error" role="alert" data-testid="oobe-probe-error">
        <AlertTriangle :size="15" aria-hidden="true" />
        <div class="oobe-probe-error__body">
          <p>{{ probeErrorMessage(probeError) }}</p>
          <div class="oobe-probe-error__actions">
            <UiButton variant="outline" size="sm" :disabled="saving" @click="retryProbeOnly">
              <RefreshCw :size="12" />
              <span>{{ label('oobe.provider.retryProbe', 'Retry probe') }}</span>
            </UiButton>
            <UiButton
              v-if="probeError.kind === 'rate_limit' || probeError.kind === 'model_not_found'"
              variant="ghost"
              size="sm"
              @click="modelId = ''"
            >
              <span>{{ label('oobe.provider.switchModel', 'Try another model') }}</span>
            </UiButton>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.oobe-step { display: flex; flex-direction: column; gap: 12px; }
.oobe-step-hero h3 { margin: 0 0 4px; font-size: 17px; color: var(--text-primary); }
.oobe-step-hero p { margin: 0; font-size: 13px; color: var(--text-secondary); line-height: 1.5; }
.oobe-seed-hint {
  margin: 0;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 9px;
  font-size: 12px;
  color: var(--accent-warning);
  background: color-mix(in srgb, var(--accent-warning) 12%, transparent);
}
.oobe-path-cards {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}
.oobe-path-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  padding: 10px 12px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  cursor: pointer;
  text-align: left;
  font-size: 12px;
}
.oobe-path-card strong { color: var(--text-primary); font-size: 12.5px; line-height: 1.35; }
.oobe-path-card.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 10%, var(--surface-raised));
}
.oobe-form-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}
.oobe-form-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 10px;
}
.oobe-form-field { display: flex; flex-direction: column; gap: 5px; }
.oobe-form-field label { font-size: 12px; font-weight: 600; color: var(--text-primary); }
.oobe-form-field label small { font-weight: 400; color: var(--text-muted); margin-left: 4px; }
.oobe-auto-profile-hint { margin: 0; font-size: 11.5px; color: var(--text-muted); }
.oobe-verify-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.oobe-probe-error {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 10px;
  border-radius: 9px;
  border: 1px solid color-mix(in srgb, var(--accent-danger) 40%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-danger) 10%, transparent);
  color: var(--accent-danger);
  font-size: 12px;
}
.oobe-probe-error__body { display: flex; flex-direction: column; gap: 8px; flex: 1; }
.oobe-probe-error__body p { margin: 0; line-height: 1.45; }
.oobe-probe-error__actions { display: flex; gap: 6px; flex-wrap: wrap; }
.spinning { animation: oobe-spin 1s linear infinite; }
@keyframes oobe-spin { to { transform: rotate(360deg); } }
</style>
