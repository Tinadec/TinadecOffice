<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  ArrowRight,
  Check,
  CheckCircle2,
  Eye,
  EyeOff,
  KeyRound,
  Loader2,
  RefreshCw,
  Terminal,
} from '@lucide/vue'
import {
  api,
  type CliDiscoveryCandidateDto,
  type HarnessChannelDto,
  type ModelProviderInstanceDto,
} from '@/api'
import { UiButton, UiInput, UiSelectField } from '@/components/ui'
import { findTemplate, PROVIDER_TEMPLATES, type ProviderTemplate } from '@/providerTemplates'
import { suggestModelProfile } from '@/composables/useModelCenter'
import {
  classifyProbeError,
  isProviderEffectivelyConfigured,
  useOobe,
  type ClassifiedProbeError,
} from '@/composables/useOobe'

type ConnectTrack = 'cli' | 'api'

const FEATURED_API_DRIVERS = [
  'openai-compatible',
  'anthropic',
  'google',
  'deepseek',
  'openrouter',
  'ollama',
  'custom',
] as const

const { t, te } = useI18n()
const { providerVerified } = useOobe()

function label(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

const activeTrack = ref<ConnectTrack>('api')
const trackManuallyChosen = ref(false)

// ---- Local CLI / ACP discovery state ----
const cliScanning = ref(false)
const cliConnectingKey = ref<string | null>(null)
const cliCandidates = ref<CliDiscoveryCandidateDto[]>([])
const cliError = ref('')

// ---- API Provider state ----
const existingProviders = ref<ModelProviderInstanceDto[]>([])
const selectedDriver = ref<string>('openai-compatible')
const baseUrl = ref('https://api.openai.com/v1')
const modelId = ref('gpt-5.4-mini')
const apiKey = ref('')
const showApiKey = ref(false)
const saving = ref(false)
const probeStatus = ref<'idle' | 'probing' | 'verified' | 'not_run' | 'failed'>('idle')
const probeError = ref<ClassifiedProbeError | null>(null)
const savedProviderId = ref<string | null>(null)

const featuredPresets = computed(() =>
  FEATURED_API_DRIVERS.map((driver) => {
    if (driver === 'custom') {
      return {
        driver: 'custom',
        shortLabel: label('oobe.provider.customShort', '自定义端点'),
        iconSvg: findTemplate('custom')?.icon ?? '',
      }
    }
    const tpl = findTemplate(driver)
    const shortMap: Record<string, string> = {
      'openai-compatible': 'OpenAI',
      anthropic: 'Anthropic',
      google: 'Gemini',
      deepseek: 'DeepSeek',
      openrouter: 'OpenRouter',
      ollama: 'Ollama',
    }
    return {
      driver,
      shortLabel: shortMap[driver] ?? label(tpl?.display_name_key ?? '', driver),
      iconSvg: tpl?.icon ?? '',
    }
  }),
)

const extraTemplates = computed(() =>
  PROVIDER_TEMPLATES.filter(
    (tpl) =>
      (tpl.category === 'cloud-api' || tpl.category === 'local-server') &&
      !(FEATURED_API_DRIVERS as readonly string[]).includes(tpl.driver),
  ).map((tpl) => ({
    value: tpl.driver,
    label: label(tpl.display_name_key, tpl.driver),
  })),
)

const activeTemplate = computed<ProviderTemplate | undefined>(() =>
  findTemplate(selectedDriver.value),
)

const isLocalEndpoint = computed(
  () => activeTemplate.value?.category === 'local-server' || selectedDriver.value === 'ollama',
)

function preferredChannel(candidate: CliDiscoveryCandidateDto): HarnessChannelDto | undefined {
  const drivable = (candidate.channels ?? []).filter((ch) => ch.drivable)
  if (!drivable.length) return undefined
  return (
    drivable.find((ch) => ch.channel === 'acp' || ch.protocol === 'acp') ??
    drivable.find((ch) => ch.channel === 'cli') ??
    drivable[0]
  )
}

function isHarnessConnected(candidate: CliDiscoveryCandidateDto): boolean {
  return existingProviders.value.some(
    (p) =>
      p.enabled &&
      p.driver.toLowerCase() === candidate.driver.toLowerCase() &&
      ['cli', 'acp', 'tui'].includes(String(p.connection_kind || '').toLowerCase()),
  )
}

const actionableCliCandidates = computed(() => {
  const list = cliCandidates.value.filter(
    (item) => Boolean(item.binary_path) && (Boolean(preferredChannel(item)) || isHarnessConnected(item)),
  )
  return [...list].sort((a, b) => {
    const aConn = isHarnessConnected(a) ? 1 : 0
    const bConn = isHarnessConnected(b) ? 1 : 0
    return bConn - aConn
  })
})

function harnessIconSvg(driver: string): string {
  return findTemplate(driver)?.icon ?? ''
}

function channelBadgeText(candidate: CliDiscoveryCandidateDto): string {
  const ch = preferredChannel(candidate)
  const raw = ch?.channel || ch?.protocol || 'cli'
  return raw.toUpperCase()
}

function selectTrack(track: ConnectTrack): void {
  activeTrack.value = track
  trackManuallyChosen.value = true
}

function applyDriverPreset(driver: string): void {
  selectedDriver.value = driver
  probeError.value = null
  if (driver === 'custom') {
    baseUrl.value = ''
    modelId.value = ''
    return
  }
  const tpl = findTemplate(driver)
  if (!tpl) return
  baseUrl.value = tpl.default_base_url ?? ''
  modelId.value = tpl.default_model ?? ''
  if (tpl.category === 'local-server') {
    apiKey.value = ''
  }
}

function probeErrorMessage(err: ClassifiedProbeError): string {
  switch (err.kind) {
    case 'auth':
      return label('oobe.provider.errAuth', '密钥无效或无权限（401/403），请检查 API Key。')
    case 'model_not_found':
      return label('oobe.provider.errModelNotFound', '模型名称不存在（404），请核对模型 ID。')
    case 'network':
      return label('oobe.provider.errNetwork', '无法连接目标地址，请检查接口地址或本地服务是否已启动。')
    case 'rate_limit':
      return label('oobe.provider.errRateLimit', '触发限流或额度不足（429），可稍后重试或换一个模型。')
    case 'not_run':
      return label('oobe.provider.probeNotRun', '尚未运行验证')
    default:
      return label('oobe.provider.errUnknown', `验证失败：${err.detail}`, { detail: err.detail || 'unknown error' })
  }
}

async function ensureDefaultChatRoute(providerId: string, model: string | null): Promise<void> {
  if (typeof api.saveModelRoute !== 'function') return
  let expectedRevision: number | undefined
  let existingCandidates: Array<{ provider_instance_id: string; model: string | null }> = []
  try {
    const routes = await api.listModelRoutes?.()
    const chatRoute = routes?.find((r) => r.purpose === 'chat')
    expectedRevision = chatRoute?.revision
    existingCandidates = (chatRoute?.candidates ?? [])
      .filter((c) => !(c.provider_instance_id === providerId && (c.model ?? null) === (model || null)))
      .map((c) => ({ provider_instance_id: c.provider_instance_id, model: c.model ?? null }))
  } catch {
    // Route does not exist yet; create fresh.
  }
  await api.saveModelRoute(
    'chat',
    { candidates: [{ provider_instance_id: providerId, model: model || null }, ...existingCandidates] },
    undefined,
    expectedRevision !== undefined ? { expected_revision: expectedRevision } : undefined,
  )
}

async function refreshProvidersAndHarnesses(): Promise<void> {
  cliScanning.value = true
  cliError.value = ''
  try {
    const [providerList, discoveryRes] = await Promise.all([
      typeof api.listModelProviders === 'function'
        ? api.listModelProviders().catch(() => [] as ModelProviderInstanceDto[])
        : Promise.resolve([] as ModelProviderInstanceDto[]),
      typeof api.discoverHarnesses === 'function'
        ? api.discoverHarnesses().catch(() => ({ cli_runtimes: [] as CliDiscoveryCandidateDto[] }))
        : Promise.resolve({ cli_runtimes: [] as CliDiscoveryCandidateDto[] }),
    ])

    existingProviders.value = providerList
    cliCandidates.value = discoveryRes?.cli_runtimes ?? []

    const configured = providerList.filter(isProviderEffectivelyConfigured)
    const configuredHttp = configured.find(
      (p) => !['cli', 'acp', 'tui'].includes(String(p.connection_kind || '').toLowerCase()),
    )
    if (configuredHttp) {
      savedProviderId.value = configuredHttp.id
      selectedDriver.value = configuredHttp.driver
      baseUrl.value = configuredHttp.base_url ?? ''
      modelId.value = configuredHttp.model ?? configuredHttp.models?.[0] ?? ''
    }
    if (configured.length > 0) {
      providerVerified.value = true
    }

    if (!trackManuallyChosen.value) {
      if (actionableCliCandidates.value.length > 0 && !configuredHttp) {
        activeTrack.value = 'cli'
      } else {
        activeTrack.value = 'api'
      }
    }
  } catch (err) {
    cliError.value = err instanceof Error ? err.message : String(err)
  } finally {
    cliScanning.value = false
  }
}

async function connectCliHarness(candidate: CliDiscoveryCandidateDto): Promise<void> {
  const channel = preferredChannel(candidate)
  if (!candidate.binary_path || !channel || typeof api.connectHarness !== 'function') return
  const key = `${candidate.driver}:${channel.channel ?? channel.protocol}`
  cliConnectingKey.value = key
  cliError.value = ''
  try {
    const ext = candidate as CliDiscoveryCandidateDto & {
      home_path?: string | null
      config_home?: string | null
      launch_args?: string | null
      default_launch_args?: string | null
      detected_models?: string[] | null
    }
    const homePath = ext.home_path || ext.config_home || null
    const launchArgs = ext.launch_args || ext.default_launch_args || null
    const detectedModels = Array.isArray(ext.detected_models) ? ext.detected_models.filter(Boolean) : []

    const created = await api.connectHarness({
      driver: candidate.driver,
      binary_path: candidate.binary_path,
      channel: channel.channel ?? null,
      protocol: channel.protocol,
      display_name: candidate.display_name || undefined,
      ...(homePath ? { home_path: homePath } : {}),
      ...(launchArgs ? { launch_args: launchArgs } : {}),
      ...(detectedModels.length > 0 ? { model: detectedModels[0], models: detectedModels } : {}),
    } as Parameters<typeof api.connectHarness>[0])

    if (created?.id) {
      await ensureDefaultChatRoute(created.id, detectedModels[0] ?? null).catch(() => undefined)
    }
    providerVerified.value = true
    await refreshProvidersAndHarnesses()
  } catch (err) {
    cliError.value = err instanceof Error ? err.message : String(err)
  } finally {
    cliConnectingKey.value = null
  }
}

async function runLiveProbe(providerId: string, model: string): Promise<boolean> {
  probeStatus.value = 'probing'
  probeError.value = null
  try {
    const probeResult = await api.probeModel(true)
    if (probeResult.status === 'ready') {
      await ensureDefaultChatRoute(providerId, model || null)
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
  if (!trimmedModel || (!isLocalEndpoint.value && !trimmedKey && !savedProviderId.value)) {
    return
  }

  saving.value = true
  probeError.value = null
  try {
    const tpl = activeTemplate.value
    const effectiveDriver = selectedDriver.value === 'custom' ? 'openai-compatible' : selectedDriver.value
    const profile = suggestModelProfile(trimmedModel)
    const displayName =
      selectedDriver.value === 'custom'
        ? label('oobe.provider.customCard', '自建兼容端点')
        : label(tpl?.display_name_key ?? '', effectiveDriver)

    const matchingExisting = existingProviders.value.find(
      (p) =>
        (savedProviderId.value && p.id === savedProviderId.value) ||
        (p.driver === effectiveDriver && !isProviderEffectivelyConfigured(p)),
    )

    const payload = {
      driver: effectiveDriver,
      protocol: tpl?.protocol ?? 'openai-chat',
      display_name: matchingExisting?.display_name || displayName,
      connection_kind: isLocalEndpoint.value ? 'local-server' : 'api-key',
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
  void refreshProvidersAndHarnesses()
})
</script>

<template>
  <section class="oobe-stage-panel" data-testid="oobe-step-provider">
    <header class="oobe-stage-head">
      <span class="oobe-eyebrow">02 / 03</span>
      <h1>{{ label('oobe.provider.title', '连接模型或本机引擎') }}</h1>
      <p>{{ label('oobe.provider.subtitle', '可直接启用本机已安装的 CLI / ACP 编程引擎，或填写 API 供应商密钥完成连通验证。') }}</p>
    </header>

    <div class="oobe-track-tabs" role="tablist" :aria-label="label('oobe.provider.title', '连接方式')">
      <button
        type="button"
        role="tab"
        class="oobe-track-tab"
        :class="{ 'is-active': activeTrack === 'cli' }"
        :aria-selected="activeTrack === 'cli'"
        data-testid="oobe-track-cli"
        @click="selectTrack('cli')"
      >
        <Terminal :size="13" aria-hidden="true" />
        <span>{{ label('oobe.provider.trackCli', '本机 CLI / ACP 引擎') }}</span>
        <span v-if="actionableCliCandidates.length > 0" class="oobe-track-count">
          {{ actionableCliCandidates.length }}
        </span>
      </button>
      <button
        type="button"
        role="tab"
        class="oobe-track-tab"
        :class="{ 'is-active': activeTrack === 'api' }"
        :aria-selected="activeTrack === 'api'"
        data-testid="oobe-track-api"
        @click="selectTrack('api')"
      >
        <KeyRound :size="13" aria-hidden="true" />
        <span>{{ label('oobe.provider.trackApi', 'API 供应商 / 本地模型') }}</span>
      </button>
    </div>

    <!-- Track 1: Auto-detected Local CLI / ACP Harnesses -->
    <div v-if="activeTrack === 'cli'" class="oobe-cli-pane" data-testid="oobe-cli-pane">
      <div class="oobe-cli-toolbar">
        <span class="oobe-cli-summary">
          <template v-if="cliScanning">
            {{ label('oobe.provider.cliScanning', '正在检测本机已安装的智能体工具…') }}
          </template>
          <template v-else-if="actionableCliCandidates.length > 0">
            {{ label('oobe.provider.cliDetectedCount', `已检测到 ${actionableCliCandidates.length} 个可用本机引擎`, { count: actionableCliCandidates.length }) }}
          </template>
          <template v-else>
            {{ label('oobe.provider.cliNoneShort', '暂未检测到可直接驱动的本机 CLI / ACP 工具') }}
          </template>
        </span>
        <UiButton
          variant="ghost"
          size="xs"
          :disabled="cliScanning"
          data-testid="oobe-rescan-cli"
          @click="refreshProvidersAndHarnesses"
        >
          <RefreshCw :size="12" :class="{ spinning: cliScanning }" />
          <span>{{ label('oobe.provider.cliRescan', '重新检测') }}</span>
        </UiButton>
      </div>

      <p v-if="cliError" class="oobe-inline-error" role="alert">{{ cliError }}</p>

      <ul v-if="actionableCliCandidates.length > 0" class="oobe-cli-list">
        <li
          v-for="candidate in actionableCliCandidates"
          :key="candidate.driver"
          class="oobe-cli-row"
          :data-testid="`oobe-cli-row-${candidate.driver}`"
        >
          <span class="oobe-cli-logo" aria-hidden="true">
            <span
              v-if="harnessIconSvg(candidate.driver)"
              class="oobe-svg-mark"
              v-html="harnessIconSvg(candidate.driver)"
            />
            <Terminal v-else :size="14" />
          </span>

          <div class="oobe-cli-info">
            <div class="oobe-cli-name-row">
              <strong>{{ candidate.display_name || candidate.driver }}</strong>
              <span class="oobe-protocol-pill">{{ channelBadgeText(candidate) }}</span>
            </div>
            <span v-if="candidate.binary_path" class="oobe-cli-path" :title="candidate.binary_path">
              {{ candidate.binary_path }}
            </span>
          </div>

          <div class="oobe-cli-action">
            <span
              v-if="isHarnessConnected(candidate)"
              class="oobe-connected-pill"
              :data-testid="`oobe-cli-connected-${candidate.driver}`"
            >
              <Check :size="12" aria-hidden="true" />
              <span>{{ label('oobe.provider.cliConnected', '已启用') }}</span>
            </span>
            <UiButton
              v-else
              variant="outline"
              size="xs"
              :disabled="cliConnectingKey !== null"
              :data-testid="`oobe-connect-cli-${candidate.driver}`"
              @click="connectCliHarness(candidate)"
            >
              <Loader2
                v-if="cliConnectingKey === `${candidate.driver}:${preferredChannel(candidate)?.channel ?? preferredChannel(candidate)?.protocol}`"
                :size="12"
                class="spinning"
              />
              <span>{{ label('oobe.provider.cliEnable', '一键启用') }}</span>
            </UiButton>
          </div>
        </li>
      </ul>

      <div v-else-if="!cliScanning" class="oobe-cli-empty" data-testid="oobe-cli-empty">
        <p>{{ label('oobe.provider.cliEmptyDesc', '若本机安装了 Claude Code、Codex、Gemini CLI 或 OpenCode，完成登录后点击「重新检测」即可启用；也可以直接通过 API 密钥接入。') }}</p>
        <UiButton variant="outline" size="sm" @click="selectTrack('api')">
          <span>{{ label('oobe.provider.switchToApi', '改用 API 供应商密钥') }}</span>
          <ArrowRight :size="13" />
        </UiButton>
      </div>
    </div>

    <!-- Track 2: Cloud API Providers & Local Model Endpoints -->
    <div v-else class="oobe-api-pane" data-testid="oobe-api-pane">
      <div class="oobe-preset-bar">
        <button
          v-for="preset in featuredPresets"
          :key="preset.driver"
          type="button"
          class="oobe-preset-chip"
          :class="{ 'is-active': selectedDriver === preset.driver }"
          :data-testid="`oobe-preset-${preset.driver}`"
          @click="applyDriverPreset(preset.driver)"
        >
          <span
            v-if="preset.iconSvg"
            class="oobe-chip-svg"
            aria-hidden="true"
            v-html="preset.iconSvg"
          />
          <span>{{ preset.shortLabel }}</span>
        </button>

        <div v-if="extraTemplates.length > 0" class="oobe-more-select">
          <UiSelectField
            :model-value="(FEATURED_API_DRIVERS as readonly string[]).includes(selectedDriver) ? '' : selectedDriver"
            :options="extraTemplates"
            :placeholder="label('oobe.provider.moreProviders', '更多供应商…')"
            :aria-label="label('oobe.provider.moreProviders', '更多供应商')"
            @update:model-value="applyDriverPreset"
          />
        </div>
      </div>

      <div class="oobe-fields-grid">
        <div class="oobe-field">
          <label for="oobe-baseurl-input">{{ label('oobe.provider.baseUrl', '接口地址') }}</label>
          <UiInput
            id="oobe-baseurl-input"
            v-model="baseUrl"
            class="font-mono text-[12.5px]"
            :placeholder="activeTemplate?.placeholders.base_url || 'https://api.openai.com/v1'"
            data-testid="oobe-provider-baseurl"
          />
        </div>

        <div class="oobe-field">
          <label for="oobe-model-input">{{ label('oobe.provider.modelName', '模型 ID') }}</label>
          <UiInput
            id="oobe-model-input"
            v-model="modelId"
            class="font-mono text-[12.5px]"
            :placeholder="activeTemplate?.placeholders.model || 'gpt-5.4-mini'"
            data-testid="oobe-provider-model"
          />
        </div>
      </div>

      <div class="oobe-field">
        <label for="oobe-apikey-input">
          <span>{{ label('oobe.provider.apiKey', 'API 密钥') }}</span>
          <small v-if="isLocalEndpoint">{{ label('oobe.provider.apiKeyOptionalLocal', '本地服务可留空') }}</small>
        </label>
        <div class="oobe-secret-wrap">
          <UiInput
            id="oobe-apikey-input"
            v-model="apiKey"
            :type="showApiKey ? 'text' : 'password'"
            class="font-mono text-[12.5px] pr-9"
            :placeholder="isLocalEndpoint ? label('oobe.provider.apiKeyOptionalLocal', '本地服务可留空') : (activeTemplate?.placeholders.api_key || 'sk-...')"
            data-testid="oobe-provider-apikey"
            @keydown.enter.prevent="saveAndVerify"
          />
          <button
            type="button"
            class="oobe-secret-eye"
            :aria-label="showApiKey ? label('common.hide', '隐藏') : label('common.show', '显示')"
            @click="showApiKey = !showApiKey"
          >
            <EyeOff v-if="showApiKey" :size="14" aria-hidden="true" />
            <Eye v-else :size="14" aria-hidden="true" />
          </button>
        </div>
      </div>

      <div class="oobe-verify-bar">
        <UiButton
          size="sm"
          :disabled="saving || !modelId.trim() || (!isLocalEndpoint && !apiKey.trim() && !savedProviderId)"
          data-testid="oobe-save-verify-btn"
          @click="saveAndVerify"
        >
          <Loader2 v-if="saving || probeStatus === 'probing'" :size="14" class="spinning" />
          <span>{{ saving || probeStatus === 'probing' ? label('oobe.provider.verifying', '正在验证连通…') : label('oobe.provider.saveAndVerify', '保存并验证连通') }}</span>
        </UiButton>

        <span
          v-if="probeStatus === 'verified'"
          class="oobe-verified-status"
          data-testid="oobe-probe-verified"
        >
          <CheckCircle2 :size="14" aria-hidden="true" />
          <span>{{ label('oobe.provider.verified', '验证通过，已设为默认对话模型') }}</span>
        </span>
      </div>

      <div
        v-if="probeError && probeStatus === 'failed'"
        class="oobe-probe-error"
        role="alert"
        data-testid="oobe-probe-error"
      >
        <AlertTriangle :size="14" class="oobe-probe-error__icon" aria-hidden="true" />
        <p>{{ probeErrorMessage(probeError) }}</p>
        <UiButton variant="outline" size="xs" :disabled="saving" @click="retryProbeOnly">
          <RefreshCw :size="12" />
          <span>{{ label('oobe.provider.retryProbe', '重试') }}</span>
        </UiButton>
      </div>
    </div>
  </section>
</template>

<style scoped>
.oobe-stage-panel {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.oobe-stage-head {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.oobe-eyebrow {
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11px;
  font-weight: 500;
  letter-spacing: 0.08em;
  color: var(--text-muted);
}

.oobe-stage-head h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
  letter-spacing: -0.025em;
  line-height: 1.25;
  color: var(--text-primary);
}

.oobe-stage-head p {
  margin: 2px 0 0;
  font-size: 13.5px;
  line-height: 1.6;
  color: var(--text-secondary);
}

.oobe-track-tabs {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 4px;
  padding: 3px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.oobe-track-tab {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  height: 32px;
  padding: 0 12px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease;
}

.oobe-track-tab:hover {
  color: var(--text-primary);
}

.oobe-track-tab.is-active {
  background: var(--surface-raised);
  color: var(--text-primary);
  font-weight: 600;
  box-shadow: var(--shadow-subtle, 0 1px 2px rgba(0, 0, 0, 0.08));
}

.oobe-track-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 16px;
  padding: 0 5px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  font-size: 10.5px;
  font-variant-numeric: tabular-nums;
  color: var(--text-secondary);
}

.oobe-cli-pane,
.oobe-api-pane {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.oobe-cli-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.oobe-cli-summary {
  font-size: 12px;
  color: var(--text-secondary);
}

.oobe-cli-list {
  list-style: none;
  margin: 0;
  padding: 0;
  border-top: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
  max-height: 248px;
  overflow-y: auto;
}

.oobe-cli-row {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 11px 2px;
}

.oobe-cli-row + .oobe-cli-row {
  border-top: 1px solid var(--border-muted);
}

.oobe-cli-logo {
  width: 30px;
  height: 30px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  color: var(--text-primary);
}

.oobe-svg-mark {
  width: 15px;
  height: 15px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.oobe-svg-mark :deep(svg) {
  width: 15px;
  height: 15px;
}

.oobe-cli-info {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.oobe-cli-name-row {
  display: flex;
  align-items: center;
  gap: 6px;
}

.oobe-cli-name-row strong {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.oobe-protocol-pill {
  padding: 1px 5px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 10px;
  font-weight: 500;
  color: var(--text-secondary);
}

.oobe-cli-path {
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.oobe-cli-action {
  flex-shrink: 0;
}

.oobe-connected-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 26px;
  padding: 0 9px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-success, #22c55e) 36%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-success, #22c55e) 10%, transparent);
  color: var(--accent-success, #22c55e);
  font-size: 11.5px;
  font-weight: 500;
}

.oobe-cli-empty {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 12px;
  padding: 16px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.oobe-cli-empty p {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.6;
  color: var(--text-secondary);
}

.oobe-preset-bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}

.oobe-preset-chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 10px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease, color 120ms ease;
}

.oobe-preset-chip:hover {
  border-color: var(--border-default, var(--border-muted));
  color: var(--text-primary);
}

.oobe-preset-chip.is-active {
  border-color: var(--text-primary);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-weight: 600;
}

.oobe-chip-svg {
  width: 13px;
  height: 13px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

.oobe-chip-svg :deep(svg) {
  width: 13px;
  height: 13px;
}

.oobe-more-select {
  min-width: 148px;
}

.oobe-fields-grid {
  display: grid;
  grid-template-columns: 1.25fr 1fr;
  gap: 12px;
}

@media (max-width: 540px) {
  .oobe-fields-grid {
    grid-template-columns: 1fr;
  }
}

.oobe-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.oobe-field label {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
}

.oobe-field label small {
  font-size: 11px;
  font-weight: 400;
  color: var(--text-muted);
}

.oobe-secret-wrap {
  position: relative;
  display: flex;
  align-items: center;
}

.oobe-secret-eye {
  position: absolute;
  right: 6px;
  width: 26px;
  height: 26px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text-muted);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
}

.oobe-secret-eye:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.oobe-verify-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  padding-top: 2px;
}

.oobe-verified-status {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
  font-weight: 500;
  color: var(--accent-success, #22c55e);
}

.oobe-probe-error {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid color-mix(in srgb, var(--accent-danger) 38%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-danger) 8%, transparent);
  color: var(--accent-danger);
  font-size: 12px;
}

.oobe-probe-error__icon {
  flex-shrink: 0;
}

.oobe-probe-error p {
  margin: 0;
  flex: 1;
  line-height: 1.45;
}

.oobe-inline-error {
  margin: 0;
  font-size: 12px;
  color: var(--accent-danger);
}

.spinning {
  animation: oobe-spin 1s linear infinite;
}

@keyframes oobe-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
