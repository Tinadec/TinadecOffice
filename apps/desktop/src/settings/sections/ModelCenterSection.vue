<script setup lang="ts">
import {
  Activity,
  Check,
  ChevronLeft,
  Circle,
  Cpu,
  Database,
  Edit3,
  Info,
  Plus,
  Radio,
  RefreshCw,
  Route,
  Search,
  Terminal,
  Trash2,
  X,
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiBadge, UiButton, UiInput, UiSelectField, UiSkeleton } from '@/components/ui'
import ModelCenterModelRows from '@/settings/sections/ModelCenterModelRows.vue'
import ModelScopeNotice from '@/settings/sections/ModelScopeNotice.vue'
import ModelCenterLocalRuntimes, { type LocalRuntimeChannelKey } from '@/settings/modelCenter/ModelCenterLocalRuntimes.vue'
import ModelCenterCapabilityLibrary from '@/settings/modelCenter/ModelCenterCapabilityLibrary.vue'
import ModelCenterRoutingTab from '@/settings/modelCenter/ModelCenterRoutingTab.vue'
import ModelCenterProviderForm, { type ProviderFormState } from '@/settings/modelCenter/ModelCenterProviderForm.vue'
import ModelCenterDialogs from '@/settings/modelCenter/ModelCenterDialogs.vue'
import { centerDiagnosticLabel as centerDiagnosticText, providerStatusLabel } from '@/settings/modelCenterLabels'
import { useNotifications } from '@/composables/useNotifications'
import { useLiveDigestSettings, type LiveDigestMode } from '@/composables/useLiveDigest'
import { suggestModelProfile, type ModelCenterContext } from '@/composables/useModelCenter'
import type {
  CenterDiagnosticDto,
  CliDiscoveryCandidateDto,
  HarnessChannelDto,
  ModelCenterOverviewDto,
  ModelProfileDto,
  ModelProviderInstanceDto,
  ModelReferenceDto,
  SaveModelProviderInstanceInput,
} from '@/api'
import {
  PROVIDER_TEMPLATES,
  findTemplate,
  templateProtocol,
  type ProviderTemplate,
} from '@/providerTemplates'
import { buildModelCenterRows, filterModelCenterRows, type ModelCenterFilter } from '@/modelCenterView'
import { providersFromOverview } from '@/runtimeCenterView'

type CenterTabKey = 'connections' | 'models' | 'routes'

const props = defineProps<{
  center: ModelCenterContext
  runtimeModels: ModelCenterOverviewDto['models']
  runtimeCliOptions: ModelCenterOverviewDto['cli_runtimes']
  runtimeAcpOptions: ModelCenterOverviewDto['acp_runtimes']
  canAccessBackend: boolean
}>()

const {
  modelCenterOverview,
  providers,
  modelReadiness,
  modelCatalogReadiness,
  readinessReceipt,
  routes,
  selectedProviderId,
  selectedProvider,
  modelCenterBusy,
  modelCenterLoading,
  modelCenterFailure,
  modelCenterPartialFailureSources,
  loadModelCenter,
  modelApiProvider,
  modelsForProvider,
  setDefaultChatModel,
  toggleProviderEnabled,
  removeModel,
  saveProviderModels,
  fetchModelReferences,
  allModelReferencesLoaded,
  loadAllModelReferences,
  referencesForModel,
  agentReferenceCountForModel,
  runtimePolicy,
  runtimePolicyLoaded,
  loadRuntimePolicy,
  cliDiscoveryCandidates,
  cliDiscoveryLoading,
  cliDiscoveryLoaded,
  discoverHarnesses,
  connectDiscoveredCli,
  discoverProviderModels,
  saveProviderInstance,
  saveModelProfile,
} = props.center

const { t, te } = useI18n()
const { notify, confirm } = useNotifications()

const statusLabel = (status: string) => providerStatusLabel(t, status)
const centerDiagnosticLabel = (diagnostic: CenterDiagnosticDto) => centerDiagnosticText(t, diagnostic)

// ── 3-Tab Model Center State ────────────────────────────────────────
const centerTab = ref<CenterTabKey>('connections')
const localRuntimeChannel = ref<LocalRuntimeChannelKey>('cli')

const modelCenterTabs = computed(() => [
  {
    key: 'connections' as const,
    label: t('settings.centerConnections'),
    count: (modelCenterOverview.value?.api_connections.length ?? 0)
      + (modelCenterOverview.value?.cli_runtimes.length ?? 0)
      + (modelCenterOverview.value?.tui_runtimes.length ?? 0)
      + (modelCenterOverview.value?.acp_runtimes.length ?? 0),
  },
  {
    key: 'models' as const,
    label: t('settings.centerModelLibrary'),
    count: modelCenterOverview.value?.models.length ?? 0,
  },
  {
    key: 'routes' as const,
    label: t('settings.centerRoutingPolicy'),
    count: routes.value.length,
  },
])

const centerTabKeys = computed<CenterTabKey[]>(() => modelCenterTabs.value.map((tab) => tab.key))

watch(centerTab, (tab) => {
  if (!props.canAccessBackend) return
  if (tab === 'models' && !allModelReferencesLoaded.value) {
    void loadAllModelReferences()
  }
  if (tab === 'routes' && !runtimePolicyLoaded.value) {
    void loadRuntimePolicy()
  }
})

// ── API Connections Rail & Detail State ─────────────────────────────
const selectedProviderDetailId = ref('')
const providerDetailOpen = ref(false)
const providerWorkspaceEl = ref<HTMLElement | null>(null)
const providerRailEl = ref<HTMLElement | null>(null)
const providerRailScroll = ref(0)
const modelProviderFilter = ref<ModelCenterFilter>('all')
const modelProviderQuery = ref('')
const modelProviderListRef = ref<HTMLElement | null>(null)
const showDiagnostics = ref(false)
const showModal = ref(false)
const showTemplatePicker = ref(false)
const forceProviderWrite = ref(false)

function providerWorkspaceNarrow(): boolean {
  const width = providerWorkspaceEl.value?.clientWidth
  return width !== undefined && width > 0 && width <= 700
}

const providerForm = reactive<ProviderFormState>({
  id: '',
  driver: 'openai-compatible',
  display_name: t('settings.providerDefaultName'),
  connection_kind: 'api-key',
  protocol: 'openai-chat',
  base_url: 'https://api.openai.com/v1',
  model: 'gpt-5.4-mini',
  models: [],
  api_key: '',
  clear_api_key: false,
  binary_path: '',
  home_path: '',
  server_url: '',
  launch_args: '',
  enabled: true,
})

const currentTemplate = computed(() => findTemplate(providerForm.driver))
const formFields = computed(() => {
  const fields = currentTemplate.value?.fields ?? {
    base_url: true, model: true, api_key: true,
    binary_path: false, home_path: false, server_url: false, launch_args: false,
  }
  return { ...fields, model: false }
})

const providerFormSubmitted = ref(false)
const providerFormErrors = computed<Record<string, string>>(() => {
  const errors: Record<string, string> = {}
  if (!providerForm.display_name.trim()) errors.display_name = t('settings.fieldRequired')
  for (const key of ['base_url', 'server_url'] as const) {
    if (!formFields.value[key]) continue
    const value = providerForm[key].trim()
    if (value && !/^https?:\/\/\S+$/i.test(value)) errors[key] = t('settings.fieldUrlInvalid')
  }
  if (formFields.value.binary_path && providerForm.connection_kind === 'cli' && !providerForm.binary_path.trim()) {
    errors.binary_path = t('settings.fieldRequired')
  }
  return errors
})

const modelCenterRows = computed(() => buildModelCenterRows(
  providersFromOverview(modelCenterOverview.value).filter((provider) => provider.connection_kind !== 'cli'),
  PROVIDER_TEMPLATES,
  modelReadiness.value,
  (key) => t(key),
).filter((row) => row.kind === 'instance'))

const filteredModelCenterRows = computed(() => filterModelCenterRows(
  modelCenterRows.value,
  modelProviderFilter.value,
  modelProviderQuery.value,
))

const modelCenterIssueCount = computed(() => filterModelCenterRows(
  modelCenterRows.value,
  'issues',
  '',
).length)

const firstNeedsKeyProvider = computed(() =>
  providers.value.find((provider) => provider.status === 'needs_key') ?? null,
)
const modelCenterDiagnostics = computed(() => modelCenterOverview.value?.diagnostics ?? [])

const selectedProviderDetail = computed(() =>
  modelApiProvider(selectedProviderDetailId.value)
  ?? modelApiProvider(modelCenterOverview.value?.api_connections[0]?.id ?? '')
  ?? null,
)
const selectedProviderInstance = computed(() =>
  providers.value.find((provider) => provider.id === selectedProviderDetail.value?.id) ?? null,
)

// ── Default Chat Model Helpers ──────────────────────────────────────
const defaultChatRoute = computed(() => routes.value.find((route) => route.purpose === 'chat') ?? null)
const defaultChatCandidate = computed(() => defaultChatRoute.value?.candidates?.[0] ?? null)
const defaultChatValue = computed(() => defaultChatCandidate.value
  ? `${defaultChatCandidate.value.provider_instance_id}\u0000${defaultChatCandidate.value.model ?? ''}`
  : '')

function routeCandidateProviderLabel(providerInstanceId: string): string {
  const provider = providers.value.find((item) => item.id === providerInstanceId)
  if (provider) return provider.display_name
  const cli = props.runtimeCliOptions.find((item) => item.provider_instance_id === providerInstanceId)
  if (cli) return `${cli.display_name} (CLI)`
  const acp = props.runtimeAcpOptions.find((item) => item.provider_instance_id === providerInstanceId)
  if (acp) return `${acp.display_name} (ACP)`
  return providerInstanceId
}

const defaultChatLabel = computed(() => {
  const candidate = defaultChatCandidate.value
  return candidate ? [routeCandidateProviderLabel(candidate.provider_instance_id), candidate.model].filter(Boolean).join(' · ') : ''
})

const defaultChatOptions = computed(() => {
  const options = (modelCenterOverview.value?.api_connections ?? []).flatMap((provider) =>
    modelsForProvider(provider.id).map((model) => ({
      value: `${provider.id}\u0000${model.model_id}`,
      label: `${provider.display_name} · ${model.model_id}`,
    })))
  for (const runtime of props.runtimeCliOptions) {
    if (!runtime.provider_instance_id) continue
    const value = `${runtime.provider_instance_id}\u0000${runtime.model ?? ''}`
    if (!options.some((option) => option.value === value)) {
      options.push({
        value,
        label: runtime.model ? `${runtime.display_name} (CLI) · ${runtime.model}` : `${runtime.display_name} (CLI)`,
      })
    }
  }
  for (const runtime of props.runtimeAcpOptions) {
    if (!runtime.provider_instance_id) continue
    const value = `${runtime.provider_instance_id}\u0000`
    if (!options.some((option) => option.value === value)) {
      options.push({ value, label: `${runtime.display_name} (ACP)` })
    }
  }
  if (defaultChatValue.value && !options.some((option) => option.value === defaultChatValue.value)) {
    options.unshift({ value: defaultChatValue.value, label: defaultChatLabel.value })
  }
  return options
})

async function changeDefaultChat(value: string) {
  if (!value || value === defaultChatValue.value) return
  const [providerId, model] = value.split('\u0000')
  if (!providerId) return
  await setDefaultChatModel(providerId, model || null, routeCandidateProviderLabel(providerId))
}

// ── Live Digest Explainer Model Slot & 3-Mode Switch ────────────────
const LIVE_DIGEST_AUTO_VALUE = '__auto__'
const {
  settings: liveDigestSettings,
  loading: liveDigestSettingsLoading,
  saving: liveDigestSettingsSaving,
  loadSettings: loadLiveDigestSettings,
  setMode: saveLiveDigestMode,
  setExplainerModel: saveLiveDigestExplainerModel,
} = useLiveDigestSettings()

watch(
  () => props.canAccessBackend,
  (ready) => {
    if (ready) void loadLiveDigestSettings()
  },
  { immediate: true },
)

const liveDigestModeOptions = computed<Array<{ value: LiveDigestMode; label: string }>>(() => [
  { value: 'off', label: t('liveDigest.settings.modeOff') },
  { value: 'auto', label: t('liveDigest.settings.modeAuto') },
  { value: 'always', label: t('liveDigest.settings.modeAlways') },
])

const liveDigestMode = computed<LiveDigestMode>(() => liveDigestSettings.value?.mode ?? 'auto')

const liveDigestModelValue = computed<string>(() => {
  const s = liveDigestSettings.value
  if (s?.provider_instance_id) {
    return `${s.provider_instance_id}\u0000${s.model ?? ''}`
  }
  const route = routes.value.find((item) => item.purpose === 'live_explainer')
  const candidate = route?.candidates?.[0]
  if (candidate?.provider_instance_id) {
    return `${candidate.provider_instance_id}\u0000${candidate.model ?? ''}`
  }
  return LIVE_DIGEST_AUTO_VALUE
})

const liveDigestModelOptions = computed(() => {
  const options: Array<{ value: string; label: string }> = [
    { value: LIVE_DIGEST_AUTO_VALUE, label: t('liveDigest.settings.autoFastModelOption') },
    ...defaultChatOptions.value,
  ]
  const current = liveDigestModelValue.value
  if (current !== LIVE_DIGEST_AUTO_VALUE && !options.some((opt) => opt.value === current)) {
    const [providerId, model] = current.split('\u0000')
    const label = [routeCandidateProviderLabel(providerId), model].filter(Boolean).join(' · ')
    options.splice(1, 0, { value: current, label })
  }
  return options
})

const liveDigestResolvedHint = computed<string>(() => {
  const s = liveDigestSettings.value
  if (!s) return t('liveDigest.settings.subtitle')
  if (s.mode === 'off') return t('liveDigest.settings.modeOffHint')
  const modelText = s.resolved_model
    ? (s.resolved_provider_instance_id
        ? `${routeCandidateProviderLabel(s.resolved_provider_instance_id)} · ${s.resolved_model}`
        : s.resolved_model)
    : null
  if (s.resolved_source === 'explicit_route' && modelText) {
    return t('liveDigest.settings.resolvedExplicit', { model: modelText })
  }
  if (s.resolved_source === 'auto_fast_model' && modelText) {
    return t('liveDigest.settings.resolvedAutoFast', { model: modelText })
  }
  if (s.resolved_source === 'chat_fallback' && modelText) {
    return t('liveDigest.settings.resolvedChatFallback', { model: modelText })
  }
  if (s.resolved_source === 'unconfigured') {
    return t('liveDigest.settings.resolvedUnconfigured')
  }
  return t('liveDigest.settings.subtitle')
})

async function handleLiveDigestModeChange(nextMode: LiveDigestMode): Promise<void> {
  if (!props.canAccessBackend || liveDigestSettingsSaving.value || nextMode === liveDigestMode.value) return
  try {
    await saveLiveDigestMode(nextMode)
    notify.success(t('liveDigest.settings.saved'))
  } catch (error) {
    notify.error(error, { title: t('liveDigest.settings.title') })
  }
}

async function handleLiveDigestModelChange(value: string): Promise<void> {
  if (!props.canAccessBackend || liveDigestSettingsSaving.value || value === liveDigestModelValue.value) return
  try {
    if (!value || value === LIVE_DIGEST_AUTO_VALUE) {
      await saveLiveDigestExplainerModel(null, null)
    } else {
      const [providerId, model] = value.split('\u0000')
      if (!providerId) return
      await saveLiveDigestExplainerModel(providerId, model || null)
    }
    notify.success(t('liveDigest.settings.saved'))
    void loadModelCenter()
  } catch (error) {
    notify.error(error, { title: t('liveDigest.settings.title') })
  }
}

// ── Provider Form Lifecycle & Dirty Guard ───────────────────────────
const providerFormBaseline = ref('')
function providerFormSnapshot(): string {
  return JSON.stringify(providerForm)
}
function captureProviderFormBaseline(): void {
  providerFormBaseline.value = providerFormSnapshot()
}
function providerFormDirty(): boolean {
  return showModal.value && providerFormSnapshot() !== providerFormBaseline.value
}

async function requestCloseProviderModal(): Promise<void> {
  if (!providerFormDirty()) {
    showModal.value = false
    return
  }
  const discard = await confirm({
    title: t('settings.discardProviderChanges'),
    message: t('settings.discardProviderChangesMessage'),
    confirmLabel: t('settings.discardChanges'),
    cancelLabel: t('settings.keepEditing'),
    destructive: true,
  })
  if (discard) showModal.value = false
}

async function confirmDiscardProviderDraft(): Promise<boolean> {
  if (!showModal.value) return true
  if (!providerFormDirty()) {
    showModal.value = false
    return true
  }
  await requestCloseProviderModal()
  return !showModal.value
}

function onProviderModalKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || !showModal.value || event.defaultPrevented) return
  event.preventDefault()
  void requestCloseProviderModal()
}
onMounted(() => window.addEventListener('keydown', onProviderModalKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onProviderModalKeydown))

function fillForm(provider: ModelProviderInstanceDto) {
  providerForm.id = provider.id
  providerForm.driver = provider.driver
  providerForm.display_name = provider.display_name
  providerForm.connection_kind = provider.connection_kind
  providerForm.protocol = provider.protocol ?? templateProtocol(findTemplate(provider.driver) ?? PROVIDER_TEMPLATES[0]) ?? 'openai-chat'
  providerForm.base_url = provider.base_url ?? ''
  providerForm.model = provider.model ?? ''
  providerForm.models = provider.models ?? []
  providerForm.api_key = ''
  providerForm.clear_api_key = false
  providerForm.binary_path = provider.binary_path ?? ''
  providerForm.home_path = provider.home_path ?? ''
  providerForm.server_url = provider.server_url ?? ''
  providerForm.launch_args = provider.launch_args ?? ''
  providerForm.enabled = provider.enabled
}

function applyTemplateDefaults(template: ProviderTemplate) {
  providerForm.driver = template.driver
  providerForm.display_name = te(template.display_name_key) ? t(template.display_name_key) : template.driver
  providerForm.connection_kind = template.connection_kind
  providerForm.protocol = templateProtocol(template) ?? template.protocol ?? ''
  providerForm.base_url = template.default_base_url ?? ''
  providerForm.model = ''
  providerForm.models = []
  providerForm.binary_path = ''
  providerForm.home_path = ''
  providerForm.server_url = template.fields.server_url ? template.default_base_url ?? '' : ''
  providerForm.launch_args = ''
}

async function configureDiscoveredCli(candidate: CliDiscoveryCandidateDto, channel?: HarnessChannelDto) {
  if (!await confirmDiscardProviderDraft()) return
  const ext = candidate as CliDiscoveryCandidateDto & {
    home_path?: string | null
    config_home?: string | null
    launch_args?: string | null
    default_launch_args?: string | null
    default_protocol?: string | null
    detected_models?: string[] | null
  }
  const tpl = findTemplate(candidate.driver)
  centerTab.value = 'connections'
  selectedProviderId.value = ''
  providerForm.id = ''
  if (tpl) applyTemplateDefaults(tpl)
  providerForm.driver = candidate.driver
  providerForm.display_name = candidate.display_name || tpl?.driver || candidate.driver
  providerForm.connection_kind = 'cli'
  providerForm.protocol = channel?.protocol ?? ext.default_protocol ?? tpl?.protocol ?? 'acp'
  providerForm.binary_path = candidate.binary_path ?? ''
  providerForm.home_path = ext.home_path ?? ext.config_home ?? ''
  providerForm.launch_args = ext.launch_args ?? ext.default_launch_args ?? ''
  providerForm.models = Array.isArray(ext.detected_models) ? [...ext.detected_models] : []
  providerForm.model = providerForm.models[0] ?? ''
  providerForm.api_key = ''
  providerForm.clear_api_key = false
  providerForm.enabled = true
  forceProviderWrite.value = false
  providerFormSubmitted.value = false
  showModal.value = true
  captureProviderFormBaseline()
}

async function openAddCustomAcp() {
  const customAcpTemplate = findTemplate('custom-acp')
  if (customAcpTemplate) {
    await openAddModal(customAcpTemplate)
    return
  }
  if (!await confirmDiscardProviderDraft()) return
  centerTab.value = 'connections'
  selectedProviderId.value = ''
  providerForm.id = ''
  providerForm.driver = 'custom-acp'
  providerForm.display_name = te('providers.customAcp') ? t('providers.customAcp') : 'Custom ACP Harness'
  providerForm.connection_kind = 'cli'
  providerForm.protocol = 'acp'
  providerForm.base_url = ''
  providerForm.model = ''
  providerForm.models = []
  providerForm.api_key = ''
  providerForm.clear_api_key = false
  providerForm.binary_path = ''
  providerForm.home_path = ''
  providerForm.server_url = ''
  providerForm.launch_args = ''
  providerForm.enabled = true
  forceProviderWrite.value = false
  providerFormSubmitted.value = false
  showModal.value = true
  captureProviderFormBaseline()
}

async function selectCenterTab(key: CenterTabKey): Promise<void> {
  if (!await confirmDiscardProviderDraft()) return
  centerTab.value = key
}

async function onCenterTabKeydown(event: KeyboardEvent): Promise<void> {
  const keys = centerTabKeys.value
  const index = keys.indexOf(centerTab.value)
  if (index < 0) return
  let target = -1
  if (event.key === 'ArrowRight') target = (index + 1) % keys.length
  else if (event.key === 'ArrowLeft') target = (index - 1 + keys.length) % keys.length
  else if (event.key === 'Home') target = 0
  else if (event.key === 'End') target = keys.length - 1
  if (target < 0) return
  event.preventDefault()
  const list = (event.currentTarget as HTMLElement | null)?.closest<HTMLElement>('[role="tablist"]')
  await selectCenterTab(keys[target])
  await nextTick()
  list?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]')?.focus()
}

async function selectProviderDetail(providerId: string): Promise<void> {
  if (showModal.value && providerForm.id === providerId) return
  if (!await confirmDiscardProviderDraft()) return
  if (!modelApiProvider(providerId)) {
    const instance = providers.value.find((provider) => provider.id === providerId)
    if (instance) { await openEditModal(instance); return }
  }
  providerRailScroll.value = providerRailEl.value?.scrollTop ?? providerRailScroll.value
  providerDetailOpen.value = true
  selectedProviderDetailId.value = providerId
  selectedProviderId.value = providerId
  await nextTick()
  if (providerWorkspaceNarrow()) {
    providerWorkspaceEl.value?.querySelector<HTMLElement>('.model-center-provider-back')?.focus()
  }
}

async function closeProviderDetail(): Promise<void> {
  if (showModal.value) {
    await requestCloseProviderModal()
    if (showModal.value) return
  }
  providerDetailOpen.value = false
  await nextTick()
  if (providerRailEl.value) providerRailEl.value.scrollTop = providerRailScroll.value
  const providerId = selectedProviderDetailId.value
  Array.from(providerRailEl.value?.querySelectorAll<HTMLElement>('[data-provider-id]') ?? [])
    .find((element) => element.dataset.providerId === providerId)
    ?.focus()
}

async function openAddModal(template?: ProviderTemplate) {
  if (!await confirmDiscardProviderDraft()) return
  centerTab.value = 'connections'
  selectedProviderId.value = ''
  providerForm.id = ''
  applyTemplateDefaults(template ?? PROVIDER_TEMPLATES[0])
  providerForm.api_key = ''
  providerForm.clear_api_key = false
  providerForm.enabled = true
  forceProviderWrite.value = false
  providerFormSubmitted.value = false
  showModal.value = true
  captureProviderFormBaseline()
}

async function openEditModal(provider: ModelProviderInstanceDto) {
  if (showModal.value && providerForm.id === provider.id) return
  if (!await confirmDiscardProviderDraft()) return
  centerTab.value = 'connections'
  selectedProviderId.value = provider.id
  selectedProviderDetailId.value = provider.id
  providerDetailOpen.value = true
  fillForm(provider)
  forceProviderWrite.value = false
  providerFormSubmitted.value = false
  showModal.value = true
  captureProviderFormBaseline()
}

function openEditProviderById(providerId: string) {
  const provider = providers.value.find((item) => item.id === providerId)
  if (provider) void openEditModal(provider)
}

function disableProviderById(providerId: string) {
  const provider = providers.value.find((item) => item.id === providerId)
  if (provider) void disableProviderWithReferences(provider)
}

async function locateProvider(providerId: string) {
  if (!await confirmDiscardProviderDraft()) return
  centerTab.value = 'connections'
  modelProviderFilter.value = 'all'
  modelProviderQuery.value = ''
  providerDetailOpen.value = true
  selectedProviderDetailId.value = providerId
  await nextTick()
  if (providerWorkspaceNarrow()) {
    providerWorkspaceEl.value?.querySelector<HTMLElement>('.model-center-provider-back')?.focus()
    return
  }
  Array.from(modelProviderListRef.value?.querySelectorAll<HTMLButtonElement>('.model-provider-identity') ?? [])
    .find((button) => button.dataset.providerId === providerId)?.focus()
}

function focusModelProviderList(filter: ModelCenterFilter) {
  centerTab.value = 'connections'
  modelProviderFilter.value = filter
  nextTick(() => {
    modelProviderListRef.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
    modelProviderListRef.value?.querySelector<HTMLInputElement>('input')?.focus()
  })
}

function handleAddProviderClick() {
  if (showModal.value) {
    void confirmDiscardProviderDraft().then((proceed) => { if (proceed) showTemplatePicker.value = true })
    return
  }
  showTemplatePicker.value = true
}

async function pickTemplate(template: ProviderTemplate) {
  showTemplatePicker.value = false
  await openAddModal(template)
}

async function onProviderRailKeydown(event: KeyboardEvent): Promise<void> {
  if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return
  const rail = event.currentTarget as HTMLElement | null
  const buttons = Array.from(rail?.querySelectorAll<HTMLButtonElement>('.model-provider-identity') ?? [])
  const index = buttons.indexOf(document.activeElement as HTMLButtonElement)
  if (index < 0) return
  event.preventDefault()
  const target = event.key === 'Home' ? 0
    : event.key === 'End' ? buttons.length - 1
      : event.key === 'ArrowDown' ? (index + 1) % buttons.length
        : (index - 1 + buttons.length) % buttons.length
  buttons[target]?.focus()
  const row = filteredModelCenterRows.value[target]
  if (row?.kind === 'instance') await selectProviderDetail(row.provider.id)
}

async function saveProvider() {
  providerFormSubmitted.value = true
  if (Object.keys(providerFormErrors.value).length > 0) {
    void nextTick(() => document.querySelector<HTMLElement>('.model-provider-form [aria-invalid="true"]')?.focus())
    return
  }
  modelCenterBusy.value = true
  try {
    const isNewProvider = !providerForm.id
    const tmpl = currentTemplate.value
    const trimmedModel = providerForm.model.trim()
    const mergedModels = trimmedModel
      ? Array.from(new Set([...providerForm.models, trimmedModel]))
      : [...providerForm.models]
    const seededProfiles: Record<string, ModelProfileDto> = { ...(selectedProvider.value?.model_profiles ?? {}) }
    for (const modelId of mergedModels) {
      if (modelId && !seededProfiles[modelId]) {
        seededProfiles[modelId] = suggestModelProfile(modelId)
      }
    }
    const payload: SaveModelProviderInstanceInput = {
      id: providerForm.id || undefined,
      driver: providerForm.driver,
      display_name: providerForm.display_name,
      connection_kind: providerForm.connection_kind,
      protocol: providerForm.protocol || null,
      base_url: formFields.value.base_url ? (providerForm.base_url || null) : null,
      model: trimmedModel || null,
      models: mergedModels,
      ...(Object.keys(seededProfiles).length > 0 ? { model_profiles: seededProfiles } : {}),
      api_key: formFields.value.api_key ? (providerForm.api_key || null) : null,
      clear_api_key: providerForm.clear_api_key,
      binary_path: formFields.value.binary_path ? (providerForm.binary_path || null) : null,
      home_path: formFields.value.home_path ? (providerForm.home_path || null) : null,
      server_url: formFields.value.server_url ? (providerForm.server_url || null) : null,
      launch_args: formFields.value.launch_args ? (providerForm.launch_args || null) : null,
      capabilities: providerForm.id
        ? selectedProvider.value?.capabilities ?? tmpl?.capabilities ?? []
        : tmpl?.capabilities ?? [],
      enabled: providerForm.enabled,
    }

    const saved = await saveProviderInstance(payload, {
      id: providerForm.id,
      expectedRevision: selectedProvider.value?.revision,
      force: forceProviderWrite.value,
    })

    showModal.value = false
    if (selectedProviderId.value) selectedProviderDetailId.value = selectedProviderId.value
    providerDetailOpen.value = true
    if (isNewProvider) {
      modelProviderFilter.value = 'configured'
      // When a new HTTP/local provider was created without an initial model, smoothly open the Add/Fetch Models picker.
      if (saved.connection_kind !== 'cli' && mergedModels.length === 0 && modelApiProvider(saved.id)) {
        openAddModelModal(saved.id)
      }
    }
  } catch (error) {
    const err = error as { code?: string | null; message?: string }
    if (err.code === 'provider_in_use') {
      const message = err.message ?? t('settings.providerInUse')
      if (!await confirm({
        title: providerForm.display_name,
        message,
        confirmLabel: t('settings.forceAnyway'),
        cancelLabel: t('settings.cancel'),
        destructive: true,
      })) return
      forceProviderWrite.value = true
      await saveProvider()
      return
    }
    if (props.center.isModelWriteConflict(error)) {
      notify.warning({ message: t('settings.configStaleReloading') })
      await loadModelCenter()
    } else {
      notify.error(error, { title: providerForm.display_name })
    }
  }
}

// ── References & Destructive Guards ─────────────────────────────────
const modelReferenceRows = ref<Record<string, ModelReferenceDto[]>>({})
const modelReferenceBusy = ref<Record<string, boolean>>({})
const editingModelParameters = ref<{ providerId: string; model: string } | null>(null)

function referenceKey(providerInstanceId: string, modelId: string): string {
  return `${providerInstanceId}:${modelId}`
}

function referenceKindLabel(kind: string): string {
  const map: Record<string, string> = {
    route: t('settings.centerRoutes'),
    agent_default: t('settings.referenceAgentDefault'),
    mode_override: t('settings.referenceModeOverride'),
    session_override: t('settings.referenceMeetingOverride'),
    recent_invocation: t('settings.referenceRecentInvocation'),
  }
  return map[kind] ?? kind
}

function referenceDetailLabel(reference: ModelReferenceDto): string | null {
  const detail = reference.detail
  if (!detail || reference.reference_kind === 'recent_invocation') return detail ?? null
  const candidate = /^candidate:(\d+)$/.exec(detail)
  if (candidate) return t('settings.referenceCandidate', { position: candidate[1] })
  return te(`agentCenter.filter.${detail}`) ? t(`agentCenter.filter.${detail}`) : detail
}

async function providerReferenceNote(providerId: string): Promise<{ count: number; text: string; unreadable: boolean }> {
  const models = modelsForProvider(providerId)
  if (models.length === 0) return { count: 0, text: t('settings.providerNoReferences'), unreadable: false }
  try {
    const rows = (await Promise.all(models.map((model) => fetchModelReferences(providerId, model.model_id)))).flat()
    if (rows.length === 0) return { count: 0, text: t('settings.providerNoReferences'), unreadable: false }
    const counts = new Map<string, number>()
    for (const row of rows) counts.set(row.reference_kind, (counts.get(row.reference_kind) ?? 0) + 1)
    const summary = [...counts].map(([kind, count]) => `${referenceKindLabel(kind)} ×${count}`).join(' · ')
    return { count: rows.length, text: t('settings.providerReferencesAffected', { count: rows.length, summary }), unreadable: false }
  } catch {
    return { count: 0, text: t('settings.providerReferencesUnreadable'), unreadable: true }
  }
}

async function disableProviderWithReferences(provider: ModelProviderInstanceDto) {
  if (!provider.enabled) {
    await toggleProviderEnabled(provider)
    return
  }
  const note = await providerReferenceNote(provider.id)
  if (note.count > 0 || note.unreadable) {
    if (!await confirm({
      title: `${t('settings.disable')} · ${provider.display_name}`,
      message: note.text,
      confirmLabel: t('settings.disable'),
      cancelLabel: t('settings.cancel'),
      destructive: note.count > 0,
    })) return
  }
  await toggleProviderEnabled(provider, note.count > 0 ? { referencesNote: note.text } : undefined)
}

async function deleteProvider(providerId: string) {
  const note = await providerReferenceNote(providerId)
  if (!await props.center.deleteProvider(providerId, { referencesNote: note.text })) return
  if (selectedProviderDetailId.value === providerId) {
    selectedProviderDetailId.value = ''
    providerDetailOpen.value = false
  }
}

function toggleModelParameters(providerId: string, modelId: string) {
  editingModelParameters.value =
    editingModelParameters.value?.providerId === providerId && editingModelParameters.value?.model === modelId
      ? null
      : { providerId, model: modelId }
}

async function toggleModelReferences(providerInstanceId: string, modelId: string) {
  const key = referenceKey(providerInstanceId, modelId)
  if (modelReferenceRows.value[key]) {
    delete modelReferenceRows.value[key]
    modelReferenceRows.value = { ...modelReferenceRows.value }
    return
  }
  modelReferenceBusy.value = { ...modelReferenceBusy.value, [key]: true }
  try {
    const references = await fetchModelReferences(providerInstanceId, modelId)
    modelReferenceRows.value = { ...modelReferenceRows.value, [key]: references }
  } catch {
    modelReferenceRows.value = { ...modelReferenceRows.value, [key]: [] }
  } finally {
    modelReferenceBusy.value = { ...modelReferenceBusy.value, [key]: false }
  }
}

async function modelParametersSaved() {
  editingModelParameters.value = null
  await loadModelCenter()
  notify.success(t('settings.modelParametersSaved'))
}

async function handleSaveModelProfile(
  provider: ModelProviderInstanceDto,
  modelId: string,
  profile: ModelProfileDto | null,
) {
  await saveModelProfile(provider, modelId, profile)
}

// ── Add / Discover Model Modal State ────────────────────────────────
const showModelModal = ref(false)
const modelModalProviderId = ref('')
const modelModalManual = ref('')
const modelModalSelected = ref<string[]>([])
const modelModalDiscovered = ref<Array<{ id: string; display_name: string }>>([])
const modelModalQuery = ref('')
const modelModalBusy = ref(false)
const modelModalError = ref('')
const modelModalFetched = ref(false)
let modelModalFetchToken = 0

const modelModalExisting = computed(() => modelApiProvider(modelModalProviderId.value)?.models ?? [])
const modelModalRows = computed(() => {
  const existing = modelModalExisting.value
  const seen = new Set(existing)
  const rows = existing.map((id) => ({ id, label: id, saved: true }))
  for (const item of modelModalDiscovered.value) {
    if (!item.id || seen.has(item.id)) continue
    seen.add(item.id)
    rows.push({ id: item.id, label: item.display_name || item.id, saved: false })
  }
  for (const id of modelModalSelected.value) {
    if (seen.has(id)) continue
    seen.add(id)
    rows.push({ id, label: id, saved: false })
  }
  return rows
})
const modelModalVisibleRows = computed(() => {
  const query = modelModalQuery.value.trim().toLocaleLowerCase()
  return modelModalRows.value.filter((row) => !query
    || row.id.toLocaleLowerCase().includes(query)
    || row.label.toLocaleLowerCase().includes(query))
})

function openAddModelModal(providerId: string) {
  modelModalProviderId.value = providerId
  modelModalManual.value = ''
  modelModalSelected.value = []
  modelModalDiscovered.value = []
  modelModalQuery.value = ''
  modelModalError.value = ''
  modelModalFetched.value = false
  modelModalBusy.value = false
  modelModalFetchToken += 1
  showModelModal.value = true
}

function closeModelModal() {
  modelModalFetchToken += 1
  modelModalBusy.value = false
  showModelModal.value = false
}

function toggleModelSelection(id: string) {
  if (modelModalExisting.value.includes(id)) return
  modelModalSelected.value = modelModalSelected.value.includes(id)
    ? modelModalSelected.value.filter((item) => item !== id)
    : [...modelModalSelected.value, id]
}

function selectAllVisibleModels() {
  const next = new Set(modelModalSelected.value)
  for (const row of modelModalVisibleRows.value) {
    if (!row.saved) next.add(row.id)
  }
  modelModalSelected.value = Array.from(next)
}

function clearSelectedModels() {
  modelModalSelected.value = []
}

async function fetchDiscoveredModels() {
  const providerId = modelModalProviderId.value
  const token = ++modelModalFetchToken
  modelModalBusy.value = true
  modelModalError.value = ''
  try {
    const result = await discoverProviderModels(providerId)
    if (token !== modelModalFetchToken || providerId !== modelModalProviderId.value || !showModelModal.value) return
    modelModalDiscovered.value = result
    modelModalFetched.value = true
    const fresh = result
      .map((model) => model.id)
      .filter((id) => id && !modelModalExisting.value.includes(id) && !modelModalSelected.value.includes(id))
    modelModalSelected.value = [...modelModalSelected.value, ...fresh]
    notify.success(t(fresh.length > 0 ? 'settings.discoveredNewModels' : 'settings.noNewModels', { count: fresh.length }))
  } catch (error) {
    if (token !== modelModalFetchToken) return
    modelModalError.value = error instanceof Error ? error.message : String(error)
  } finally {
    if (token === modelModalFetchToken) modelModalBusy.value = false
  }
}

function addManualModel() {
  const id = modelModalManual.value.trim()
  if (!id) return
  if (!modelModalExisting.value.includes(id) && !modelModalSelected.value.includes(id)) {
    modelModalSelected.value = [...modelModalSelected.value, id]
  }
  modelModalManual.value = ''
}

async function saveModelModal() {
  const provider = modelApiProvider(modelModalProviderId.value)
  if (!provider) return
  const merged = [...new Set([...(provider.models ?? []), ...modelModalSelected.value])]
  modelModalBusy.value = true
  try {
    if (await saveProviderModels(provider, merged, provider.model ?? merged[0] ?? null)) closeModelModal()
  } finally {
    modelModalBusy.value = false
  }
}

// ── Display Helpers ─────────────────────────────────────────────────
function configuredModelSourceLabel(source: string) {
  if (source === 'provider_default') return t('settings.modelSourceProviderDefault')
  if (source === 'provider_models') return t('settings.modelSourceProviderModels')
  if (source === 'route_override') return t('settings.modelSourceRouteOverride')
  return source
}

function connectionKindLabel(kind: string) {
  if (kind === 'cli') return t('settings.connectionKindCli')
  if (kind === 'local-server') return t('settings.connectionKindLocal')
  if (kind === 'public-api') return t('settings.connectionKindPublicApi')
  return t('settings.connectionKindApiKey')
}

function providerNote(provider: { status?: string | null; status_message?: string | null }): string | null {
  return provider.status_message && !['configured', 'disabled'].includes(provider.status ?? '')
    ? provider.status_message
    : null
}

function providerPresentation(driver: string) {
  return findTemplate(driver)
}

function statusVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'ready') return 'default'
  if (status === 'needs_key' || status === 'not_configured') return 'destructive'
  if (status === 'disabled') return 'secondary'
  if (status === 'cooldown') return 'outline'
  return 'outline'
}

// ── Public Imperative API (SettingsPage.vue) ────────────────────────
async function focusProvider(providerId: string): Promise<void> {
  centerTab.value = 'connections'
  modelProviderQuery.value = ''
  modelProviderFilter.value = 'all'
  selectedProviderDetailId.value = providerId
  providerDetailOpen.value = true
  const overview = modelCenterOverview.value
  if (overview?.cli_runtimes.some((row) => row.provider_instance_id === providerId)) {
    localRuntimeChannel.value = 'cli'
  } else if (overview?.tui_runtimes.some((row) => row.provider_instance_id === providerId)) {
    localRuntimeChannel.value = 'tui'
  } else if (overview?.acp_runtimes.some((row) => row.provider_instance_id === providerId)) {
    localRuntimeChannel.value = 'acp'
  }
  await nextTick()
}

async function canLeave(): Promise<boolean> {
  if (!showModal.value) return true
  await requestCloseProviderModal()
  return !showModal.value
}

defineExpose({ canLeave, focusProvider })
</script>

<template>
  <ModelScopeNotice v-if="canAccessBackend" />
  <div class="center-page model-center-page">
    <div class="center-command-bar">
      <div>
        <h2>{{ t('settings.modelCenter') }}</h2>
        <p>{{ t('settings.modelCenterSubtitle') }}</p>
      </div>
      <div class="center-command-actions">
        <div class="model-center-default-chat">
          <Route :size="14" aria-hidden="true" />
          <span class="model-center-default-label">{{ t('settings.defaultChatModel') }}</span>
          <UiSelectField
            v-if="defaultChatOptions.length > 0"
            class="model-center-default-select"
            :model-value="defaultChatValue"
            :options="defaultChatOptions"
            :aria-label="t('settings.defaultChatModel')"
            :placeholder="t('settings.defaultChatNone')"
            @update:model-value="changeDefaultChat"
          />
          <strong v-else-if="defaultChatCandidate" class="model-center-default-value">{{ defaultChatLabel }}</strong>
          <span v-else class="quiet">{{ t('settings.defaultChatNone') }}</span>
        </div>
        <UiButton variant="outline" size="sm" @click="handleAddProviderClick">
          <Plus :size="14" />
          <span>{{ t('settings.addProvider') }}</span>
        </UiButton>
        <UiButton variant="outline" size="sm" :disabled="modelCenterLoading || modelCenterBusy || !canAccessBackend" @click="loadModelCenter">
          <RefreshCw :size="14" />
          <span>{{ t('settings.refresh') }}</span>
        </UiButton>
        <UiButton variant="ghost" size="sm" :disabled="!canAccessBackend" @click="showDiagnostics = true">
          <Activity :size="14" />
          <span>{{ t('settings.advancedDiagnostics') }}</span>
        </UiButton>
      </div>
    </div>

    <div v-if="modelCenterFailure && !modelCenterOverview" class="center-message" role="alert" data-testid="model-center-read-failure">
      <Info :size="16" aria-hidden="true" />
      <div class="center-message-content">
        <strong>{{ t('settings.centerLoadFailed') }}</strong>
        <span>{{ modelCenterFailure.message }}</span>
        <span v-if="modelCenterFailure.code">{{ modelCenterFailure.code }}</span>
        <pre v-if="modelCenterFailure.details">{{ modelCenterFailure.details }}</pre>
      </div>
      <UiButton variant="outline" size="sm" :disabled="modelCenterLoading || !canAccessBackend" @click="loadModelCenter">{{ t('settings.retry') }}</UiButton>
    </div>

    <div v-if="modelCenterLoading && !modelCenterOverview" class="center-loading-state" aria-live="polite">
      <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
    </div>

    <p v-if="modelCenterLoading && modelCenterOverview" class="center-stale-status" role="status">
      <RefreshCw :size="13" class="center-stale-spinner" aria-hidden="true" />
      {{ t('settings.refreshing') }}
    </p>

    <div v-if="modelCenterDiagnostics.length > 0" class="center-message warning center-diagnostics-message">
      <Info :size="16" />
      <div class="center-message-content">
        <strong>{{ t('settings.centerDiagnostics') }}</strong>
        <ul>
          <li v-for="diagnostic in modelCenterDiagnostics" :key="`${diagnostic.code}:${diagnostic.source ?? ''}:${diagnostic.status ?? ''}`">
            {{ centerDiagnosticLabel(diagnostic) }}
          </li>
        </ul>
      </div>
      <UiButton variant="outline" size="sm" :disabled="modelCenterLoading" @click="loadModelCenter">{{ t('settings.retry') }}</UiButton>
    </div>

    <div v-if="modelCenterPartialFailureSources.length > 0" class="center-message warning">
      <Info :size="16" />
      <div class="center-message-content">
        <strong>{{ t('settings.centerPartialLoadFailed') }}</strong>
        <ul>
          <li v-for="source in modelCenterPartialFailureSources" :key="source">{{ source }}</li>
        </ul>
      </div>
    </div>

    <div v-if="modelReadiness && modelReadiness.status !== 'ready'" class="model-health-alert model-center-health-alert" role="status">
      <Info :size="16" />
      <div>
        <strong>
          {{ firstNeedsKeyProvider
            ? t('settings.missingKeySummary', { name: firstNeedsKeyProvider.display_name })
            : t('settings.modelIssuesSummary') }}
        </strong>
        <span>{{ t('settings.modelIssueHint') }}</span>
      </div>
      <UiButton v-if="firstNeedsKeyProvider" variant="outline" size="sm" @click="openEditModal(firstNeedsKeyProvider)">
        {{ t('settings.configureNow') }}
      </UiButton>
      <UiButton v-else-if="modelCenterIssueCount > 0" variant="outline" size="sm" @click="focusModelProviderList('issues')">
        {{ t('settings.viewIssues') }}
      </UiButton>
      <UiButton v-else variant="outline" size="sm" @click="showDiagnostics = true">
        {{ t('settings.advancedDiagnostics') }}
      </UiButton>
    </div>

    <section
      v-if="canAccessBackend"
      class="live-digest-settings-bar"
      data-testid="live-digest-settings-section"
      :aria-label="t('liveDigest.settings.title')"
    >
      <div class="live-digest-settings-meta">
        <div class="live-digest-settings-head">
          <Radio :size="14" aria-hidden="true" />
          <strong>{{ t('liveDigest.settings.title') }}</strong>
          <UiBadge variant="outline">live_explainer</UiBadge>
        </div>
        <p class="live-digest-settings-hint">{{ liveDigestResolvedHint }}</p>
      </div>

      <div class="live-digest-settings-controls">
        <div
          class="live-digest-mode-segment"
          role="radiogroup"
          :aria-label="t('liveDigest.settings.modeLabel')"
          data-testid="live-digest-mode-switch"
        >
          <button
            v-for="option in liveDigestModeOptions"
            :key="option.value"
            type="button"
            role="radio"
            class="live-digest-mode-btn ui-pressable"
            :class="{ 'is-active': liveDigestMode === option.value }"
            :aria-checked="liveDigestMode === option.value"
            :disabled="liveDigestSettingsSaving || liveDigestSettingsLoading"
            :data-mode="option.value"
            :data-testid="`live-digest-mode-${option.value}`"
            @click="handleLiveDigestModeChange(option.value)"
          >
            {{ option.label }}
          </button>
        </div>

        <div class="live-digest-model-slot">
          <span class="live-digest-model-label">{{ t('liveDigest.settings.modelSlotLabel') }}</span>
          <UiSelectField
            class="live-digest-model-select"
            :model-value="liveDigestModelValue"
            :options="liveDigestModelOptions"
            :aria-label="t('liveDigest.settings.modelSlotLabel')"
            :placeholder="t('liveDigest.settings.autoFastModelOption')"
            :disabled="liveDigestSettingsSaving || liveDigestSettingsLoading || liveDigestMode === 'off'"
            data-testid="live-digest-model-select"
            @update:model-value="handleLiveDigestModelChange"
          />
        </div>
      </div>
    </section>

    <div class="model-center-shell">
      <div class="model-center-tabs" role="tablist" :aria-label="t('settings.modelCenterResources')">
        <button
          v-for="tab in modelCenterTabs"
          :key="tab.key"
          :id="`model-center-tab-${tab.key}`"
          type="button"
          role="tab"
          :aria-controls="`model-center-panel-${tab.key}`"
          :aria-selected="centerTab === tab.key"
          :tabindex="centerTab === tab.key ? 0 : -1"
          :class="{ active: centerTab === tab.key }"
          @click="selectCenterTab(tab.key)"
          @keydown="onCenterTabKeydown"
        >
          <span>{{ tab.label }}</span>
          <UiBadge variant="secondary">{{ tab.count }}</UiBadge>
        </button>
      </div>

      <main
        :id="`model-center-panel-${centerTab}`"
        class="center-resource-stage model-center-stage"
        role="tabpanel"
        :aria-labelledby="`model-center-tab-${centerTab}`"
      >
        <Transition name="center-stage-fade" mode="out-in">
          <div :key="centerTab" class="model-center-stage-pane">
            <!-- Tab 1: Connections & Providers (API Suppliers + Local Runtimes CLI/TUI/ACP) -->
            <section v-if="centerTab === 'connections'" ref="modelProviderListRef" class="model-provider-section model-center-providers">
              <div class="model-provider-toolbar">
                <div class="model-provider-search">
                  <Search :size="15" />
                  <UiInput v-model="modelProviderQuery" :placeholder="t('settings.providerSearchPlaceholder')" :aria-label="t('settings.providerSearchPlaceholder')" />
                </div>
                <div class="model-provider-filters" role="group" :aria-label="t('settings.providerFilters')">
                  <button type="button" :class="{ active: modelProviderFilter === 'all' }" :aria-pressed="modelProviderFilter === 'all'" @click="modelProviderFilter = 'all'">
                    {{ t('settings.filterAll') }}
                  </button>
                  <button type="button" :class="{ active: modelProviderFilter === 'issues' }" :aria-pressed="modelProviderFilter === 'issues'" @click="modelProviderFilter = 'issues'">
                    {{ t('settings.filterIssues') }}
                    <span v-if="modelCenterIssueCount > 0" class="filter-issue-count">{{ modelCenterIssueCount }}</span>
                  </button>
                  <button type="button" :class="{ active: modelProviderFilter === 'configured' }" :aria-pressed="modelProviderFilter === 'configured'" @click="modelProviderFilter = 'configured'">
                    {{ t('settings.filterConfigured') }}
                  </button>
                </div>
                <span class="model-provider-count">{{ t('settings.providerResultCount', { visible: filteredModelCenterRows.length, total: modelCenterRows.length }) }}</span>
              </div>

              <div ref="providerWorkspaceEl" class="model-center-provider-workspace" :class="{ 'show-detail': providerDetailOpen || showModal }">
                <div
                  ref="providerRailEl"
                  class="model-center-provider-rail"
                  role="list"
                  :aria-label="t('settings.providerListLabel')"
                  @keydown="onProviderRailKeydown"
                >
                  <div
                    v-for="row in filteredModelCenterRows"
                    :key="row.key"
                    role="listitem"
                    class="model-provider-row model-center-provider-row"
                    :class="{
                      active: row.kind === 'instance' && selectedProviderDetail?.id === row.provider.id,
                      issue: row.kind === 'instance' && ['blocked', 'warning'].includes(row.readiness?.status ?? ''),
                    }"
                  >
                    <button
                      type="button"
                      class="model-provider-identity"
                      :data-provider-id="row.kind === 'instance' ? row.provider.id : undefined"
                      :aria-current="row.kind === 'instance' && selectedProviderDetail?.id === row.provider.id ? 'true' : undefined"
                      @click="row.kind === 'instance' ? selectProviderDetail(row.provider.id) : openAddModal(row.template)"
                    >
                      <span
                        class="provider-brand-icon"
                        :style="{ color: row.template?.brand_color, backgroundColor: row.template?.brand_bg }"
                      >
                        <span v-if="row.template?.icon" class="provider-brand-mark" v-html="row.template?.icon"></span>
                        <Database v-else :size="16" />
                      </span>
                      <span>
                        <strong :title="row.display_name">{{ row.display_name }}</strong>
                        <small :title="row.driver">{{ row.kind === 'instance' ? row.driver : t('settings.notAdded') }}</small>
                      </span>
                    </button>
                    <UiBadge v-if="row.kind === 'instance'" :variant="statusVariant(row.provider.status)">
                      <Circle :size="8" />
                      {{ statusLabel(row.provider.status) }}
                    </UiBadge>
                    <UiButton v-else variant="outline" size="icon" :title="t('settings.addProvider')" :aria-label="t('settings.addProvider')" @click="openAddModal(row.template)">
                      <Plus :size="14" />
                    </UiButton>
                  </div>
                  <div v-if="filteredModelCenterRows.length === 0" class="model-provider-empty">
                    <Search :size="18" />
                    <span>{{ t('settings.noProviderResults') }}</span>
                    <UiButton variant="outline" size="sm" @click="handleAddProviderClick">
                      <Plus :size="14" />
                      {{ t('settings.addProvider') }}
                    </UiButton>
                  </div>
                </div>

                <div class="model-center-provider-detail">
                  <UiButton variant="ghost" size="sm" class="model-center-provider-back" @click="closeProviderDetail()">
                    <ChevronLeft :size="14" />
                    <span>{{ t('settings.backToProviderList') }}</span>
                  </UiButton>

                  <ModelCenterProviderForm
                    v-if="showModal"
                    :form="providerForm"
                    :selected-provider="selectedProvider"
                    :submitted="providerFormSubmitted"
                    :errors="providerFormErrors"
                    :dirty="providerFormDirty()"
                    :busy="modelCenterBusy"
                    @save="saveProvider"
                    @cancel="requestCloseProviderModal"
                  />

                  <div v-else-if="selectedProviderDetail" :key="selectedProviderDetail.id" class="model-center-provider-card">
                    <div class="provider-detail-head compact model-center-provider-head">
                      <span
                        class="provider-brand-icon"
                        :style="{ color: providerPresentation(selectedProviderDetail.driver)?.brand_color, backgroundColor: providerPresentation(selectedProviderDetail.driver)?.brand_bg }"
                      >
                        <span v-if="providerPresentation(selectedProviderDetail.driver)?.icon" class="provider-brand-mark" v-html="providerPresentation(selectedProviderDetail.driver)?.icon"></span>
                        <Database v-else :size="16" />
                      </span>
                      <div class="provider-detail-info">
                        <strong>{{ selectedProviderDetail.display_name }}</strong>
                        <span class="provider-detail-driver">{{ selectedProviderDetail.driver }} · {{ connectionKindLabel(selectedProviderDetail.connection_kind) }}</span>
                      </div>
                      <UiBadge :variant="statusVariant(selectedProviderDetail.status)">
                        <Circle :size="8" />
                        {{ statusLabel(selectedProviderDetail.status) }}
                      </UiBadge>
                    </div>
                    <div class="provider-detail-grid compact">
                      <div v-if="selectedProviderDetail.base_url" class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.baseUrl') }}</span>
                        <span class="provider-detail-value provider-detail-mono">{{ selectedProviderDetail.base_url }}</span>
                      </div>
                      <div v-if="selectedProviderDetail.model" class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.modelLabel') }}</span>
                        <span class="provider-detail-value provider-detail-mono">{{ selectedProviderDetail.model }}</span>
                      </div>
                      <div class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.apiKey') }}</span>
                        <span class="provider-detail-value">
                          <span :class="['provider-key-indicator', selectedProviderDetail.has_api_key ? 'has-key' : 'no-key']"></span>
                          {{ selectedProviderDetail.has_api_key ? t('settings.apiKeyStored') : t('settings.apiKeyNotSet') }}
                        </span>
                      </div>
                      <div class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.connectionKind') }}</span>
                        <span class="provider-detail-value">{{ connectionKindLabel(selectedProviderDetail.connection_kind) }}</span>
                      </div>
                    </div>
                    <div v-if="providerNote(selectedProviderDetail)" class="provider-status-note compact">
                      <Terminal :size="14" />
                      <span>{{ selectedProviderDetail.status_message }}</span>
                    </div>
                    <div class="provider-detail-actions compact">
                      <UiButton variant="outline" size="sm" @click="openEditProviderById(selectedProviderDetail.id)">
                        <Edit3 :size="14" />
                        <span>{{ t('settings.editConfig') }}</span>
                      </UiButton>
                      <UiButton variant="outline" size="sm" :disabled="modelCenterBusy" @click="disableProviderById(selectedProviderDetail.id)">
                        <component :is="selectedProviderDetail.enabled ? X : Check" :size="14" />
                        <span>{{ selectedProviderDetail.enabled ? t('settings.disable') : t('settings.enable') }}</span>
                      </UiButton>
                      <UiButton variant="ghost" size="sm" class="provider-delete-btn" :disabled="modelCenterBusy" @click="deleteProvider(selectedProviderDetail.id)">
                        <Trash2 :size="14" />
                        <span>{{ t('settings.delete') }}</span>
                      </UiButton>
                    </div>

                    <section class="model-center-provider-models" :aria-label="t('settings.centerModels')">
                      <div class="model-center-provider-models-head">
                        <div>
                          <strong>{{ t('settings.centerModels') }}</strong>
                          <span>{{ t('settings.providerModelsHint') }}</span>
                        </div>
                        <UiButton
                          variant="outline"
                          size="sm"
                          :disabled="modelCenterBusy"
                          :title="t('settings.addModel')"
                          @click="openAddModelModal(selectedProviderDetail.id)"
                        >
                          <Plus :size="14" />
                          {{ t('settings.addModel') }}
                        </UiButton>
                      </div>
                      <div class="model-center-provider-model-list">
                        <ModelCenterModelRows
                          :provider-id="selectedProviderDetail.id"
                          :provider="selectedProviderInstance"
                          :models="modelsForProvider(selectedProviderDetail.id)"
                          :busy="modelCenterBusy"
                          :default-chat-candidate="defaultChatCandidate"
                          :model-reference-rows="modelReferenceRows"
                          :model-reference-busy="modelReferenceBusy"
                          :editing-model-parameters="editingModelParameters"
                          :status-label="statusLabel"
                          :status-variant="statusVariant"
                          :configured-model-source-label="configuredModelSourceLabel"
                          :reference-key="referenceKey"
                          :reference-kind-label="referenceKindLabel"
                          :reference-detail-label="referenceDetailLabel"
                          @set-default="setDefaultChatModel"
                          @toggle-parameters="toggleModelParameters"
                          @toggle-references="toggleModelReferences"
                          @remove="removeModel"
                          @parameters-saved="modelParametersSaved"
                          @parameters-cancel="editingModelParameters = null"
                          @save-profile="handleSaveModelProfile"
                          @reload="loadModelCenter"
                        />
                      </div>
                      <div v-if="modelsForProvider(selectedProviderDetail.id).length === 0" class="center-empty-state">
                        <Cpu :size="20" />
                        <span>{{ t('settings.noProviderModels') }}</span>
                      </div>
                      <div v-if="modelsForProvider(selectedProviderDetail.id).length === 0" class="provider-next-step" role="note">
                        <Cpu :size="14" aria-hidden="true" />
                        <span>{{ t('settings.providerNextStep') }}</span>
                        <UiButton
                          variant="outline"
                          size="sm"
                          :disabled="modelCenterBusy"
                          @click="openAddModelModal(selectedProviderDetail.id)"
                        >
                          <Plus :size="14" />
                          {{ t('settings.addModel') }}
                        </UiButton>
                      </div>
                    </section>
                  </div>

                  <div v-else class="center-empty-state">
                    <Database :size="20" />
                    <span>{{ t('settings.noProviderSelected') }}</span>
                  </div>
                </div>
              </div>

              <!-- Local Runtimes (CLI / TUI / ACP) grouped cleanly on the same Connections page -->
              <ModelCenterLocalRuntimes
                v-model:active-channel="localRuntimeChannel"
                :overview="modelCenterOverview"
                :providers="providers"
                :busy="modelCenterBusy"
                :cli-discovery-candidates="cliDiscoveryCandidates"
                :cli-discovery-loading="cliDiscoveryLoading"
                :cli-discovery-loaded="cliDiscoveryLoaded"
                :status-label="statusLabel"
                :status-variant="statusVariant"
                @discover="discoverHarnesses"
                @connect-cli="connectDiscoveredCli"
                @configure-cli="configureDiscoveredCli"
                @add-custom-acp="openAddCustomAcp"
                @edit-provider="openEditModal"
                @delete-provider="deleteProvider"
              />
            </section>

            <!-- Tab 2: Model Capability Library (Hero Tab) -->
            <ModelCenterCapabilityLibrary
              v-if="centerTab === 'models'"
              :overview="modelCenterOverview"
              :providers="providers"
              :busy="modelCenterBusy"
              :default-chat-candidate="defaultChatCandidate"
              :models-for-provider="modelsForProvider"
              :references-for-model="referencesForModel"
              :agent-reference-count-for-model="agentReferenceCountForModel"
              :status-label="statusLabel"
              :status-variant="statusVariant"
              :configured-model-source-label="configuredModelSourceLabel"
              :reference-kind-label="referenceKindLabel"
              :reference-detail-label="referenceDetailLabel"
              @locate-provider="locateProvider"
              @add-model="openAddModelModal"
              @set-default="setDefaultChatModel"
              @remove="removeModel"
              @save-profile="handleSaveModelProfile"
              @parameters-saved="modelParametersSaved"
              @reload="loadModelCenter"
            />

            <!-- Tab 3: Routing & Default Policy -->
            <ModelCenterRoutingTab
              v-if="centerTab === 'routes'"
              :routes="routes"
              :providers="providers"
              :overview="modelCenterOverview"
              :runtime-cli-options="runtimeCliOptions"
              :runtime-acp-options="runtimeAcpOptions"
              :runtime-policy="runtimePolicy"
              :default-chat-value="defaultChatValue"
              :default-chat-label="defaultChatLabel"
              :default-chat-has-candidate="Boolean(defaultChatCandidate)"
              :default-chat-options="defaultChatOptions"
              :busy="modelCenterBusy"
              :models-for-provider="modelsForProvider"
              :save-route-candidates="center.saveRouteCandidates"
              @change-default-chat="changeDefaultChat"
            />
          </div>
        </Transition>
      </main>
    </div>
  </div>

  <ModelCenterDialogs
    v-model:show-diagnostics="showDiagnostics"
    v-model:show-template-picker="showTemplatePicker"
    v-model:model-modal-manual="modelModalManual"
    v-model:model-modal-query="modelModalQuery"
    :overview="modelCenterOverview"
    :model-readiness="modelReadiness"
    :model-catalog-readiness="modelCatalogReadiness"
    :readiness-receipt="readinessReceipt"
    :show-model-modal="showModelModal"
    :model-modal-provider="modelApiProvider(modelModalProviderId)"
    :model-modal-provider-id="modelModalProviderId"
    :model-modal-selected="modelModalSelected"
    :model-modal-visible-rows="modelModalVisibleRows"
    :model-modal-busy="modelModalBusy"
    :model-modal-error="modelModalError"
    :model-modal-fetched="modelModalFetched"
    :connection-kind-label="connectionKindLabel"
    @close-model-modal="closeModelModal"
    @fetch-discovered-models="fetchDiscoveredModels"
    @add-manual-model="addManualModel"
    @toggle-model-selection="toggleModelSelection"
    @select-all-visible-models="selectAllVisibleModels"
    @clear-selected-models="clearSelectedModels"
    @save-model-modal="saveModelModal"
    @pick-template="pickTemplate"
  />
</template>

<style scoped>
.live-digest-settings-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px 16px;
  padding: 12px 14px;
  margin-bottom: 12px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-muted);
  background: var(--bg-secondary);
}

.live-digest-settings-meta {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 220px;
  flex: 1 1 260px;
}

.live-digest-settings-head {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--text-primary);
  font-size: 13px;
}

.live-digest-settings-hint {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-muted);
}

.live-digest-settings-controls {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.live-digest-mode-segment {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 3px;
  border-radius: var(--radius-control-md, 8px);
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
}

.live-digest-mode-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 5px 10px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  line-height: 1.2;
  white-space: nowrap;
  cursor: pointer;
}

.live-digest-mode-btn:hover:not(:disabled):not(.is-active) {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.live-digest-mode-btn.is-active {
  background: var(--bg-primary-button);
  color: var(--text-on-primary);
  font-weight: 600;
}

.live-digest-mode-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.live-digest-model-slot {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.live-digest-model-label {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-secondary);
  white-space: nowrap;
}

.live-digest-model-select {
  min-width: 210px;
}
</style>
