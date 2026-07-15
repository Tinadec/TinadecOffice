<script setup lang="ts">
import {
  ArrowLeft,
  Bot,
  Check,
  ChevronRight,
  Circle,
  Cpu,
  Dna,
  Edit3,
  FileText,
  GitBranch,
  Globe,
  Info,
  KeyRound,
  LayoutGrid,
  List,
  Minus,
  Monitor,
  Moon,
  MoreHorizontal,
  Palette,
  Plus,
  Save,
  Search,
  Server,
  Settings2,
  ShieldCheck,
  Square,
  Sun,
  Terminal,
  Trash2,
  Workflow,
  X
} from '@lucide/vue'
import { computed, nextTick, reactive, ref } from 'vue' 
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useTheme } from '../composables/useTheme'
import {
  api,
  type AgentCandidateDto,
  type AgentCenterOverviewDto,
  type AgentModeDto,
  type AgentProfileDto,
  type AgentRuntimeBindingInput,
  type AgentRuntimeSelectionKind,
  type CenterDiagnosticDto,
  type ModelCatalogReadinessReceiptDto,
  type ModelCatalogTemplateReadinessDto,
  type ModelCenterAcpRuntimeDto,
  type ModelCenterOverviewDto,
  type ModelProviderReadinessDto,
  type ModelProviderInstanceDto,
  type ModelReadinessReceiptDto,
  type ModelRouteDto,
  type PromptContextPreviewDto,
  type PromptFragmentDto,
  type SavePromptFragmentInput,
  type SaveModelProviderInstanceInput,
  type HarnessManifestDto,
  type ToolLayerReadinessReceiptDto,
  type ToolDescriptorDto,
  type ToolSearchResultDto
} from '../api'
import {
  PROVIDER_TEMPLATES,
  findTemplate,
  type ProviderTemplate
} from '../providerTemplates'
import {
  buildModelCenterRows,
  filterModelCenterRows,
  type ModelCenterFilter
} from '../modelCenterView'
import {
  bindingForAgent,
  legacyRouteWarning,
  modelOptionKey,
  providerTemplateFromSupplier,
  providersFromOverview,
  runtimeSourceSummary,
  type ModelCenterSection
} from '../runtimeCenterView'
import {
  codeSuiteTools,
  languageSupportFromTools,
  manifestTools,
  projectTemplatesFromResult,
  sortedAgentLayers,
  sortedRiskPolicies,
  sortedToolSearchResults,
  sortedToolProviders,
  type ProjectTemplateSummary
} from '../toolCatalog'
import BrandLogo from '@/components/BrandLogo.vue'
import { UiButton, UiInput, UiCard, UiBadge, UiLabel, UiSwitch } from '@/components/ui'
import AgentTopologyCanvas from '@/components/AgentTopologyCanvas.vue'
import AgentEvolutionPanel from '@/components/AgentEvolutionPanel.vue'
import PromptEngineeringPanel from '@/components/PromptEngineeringPanel.vue'
import BackgroundPreview from '@/components/ui/background-preview.vue'
import PanelStyleControl from '@/components/ui/panel-style-control.vue'
import { useBackground } from '@/composables/useBackground'
import { usePanelStyles } from '@/composables/usePanelStyles'

type SettingsSection = 'model' | 'agents' | 'agentEvolution' | 'promptContext' | 'promptEngineering' | 'tools' | 'appearance' | 'language' | 'apiDocs' | 'about'

interface ProviderForm {
  id: string
  driver: string
  display_name: string
  connection_kind: string
  base_url: string
  model: string
  api_key: string
  clear_api_key: boolean
  binary_path: string
  home_path: string
  server_url: string
  launch_args: string
  enabled: boolean
}

const { t, locale } = useI18n()
const router = useRouter()
const { theme, setTheme, accentColor, setAccentColor, accentColors } = useTheme()

// Background management — backgroundSettings is a singleton shared with
// App.vue (which renders the background layer globally).  The setters below
// are used by the Settings → Appearance section.
const {
settings: backgroundSettings,
setBackgroundType,
setBackgroundSource,
setBackgroundOpacity,
setBackgroundBlur,
setBackgroundSize,
setBackgroundPosition,
setBackgroundRepeat,
selectFile: selectBackgroundFile,
resetBackground,
} = useBackground()

// Computed source with getter/setter to ensure path normalization on manual input
const backgroundSource = computed({
  get: () => backgroundSettings.value.source,
  set: (val: string) => setBackgroundSource(val),
})

// Panel styles management (global material effect)
const {
panelStyle,
updatePanelStyle,
resetPanelStyle,
getPanelStyle,
getPanelDataAttributes,
} = usePanelStyles()

// Apply global material to settings nav
const settingsNavStyle = computed(() => getPanelStyle())
const settingsNavDataAttrs = computed(() => getPanelDataAttributes())

// Apply global material to settings content panel
const settingsContentStyle = computed(() => getPanelStyle())
const settingsContentDataAttrs = computed(() => getPanelDataAttributes())

/** Wrapper that also broadcasts theme changes to detached panel windows */
function changeTheme(newTheme: 'dark' | 'light' | 'system') {
  setTheme(newTheme)
  window.tinadec?.broadcastTheme?.(newTheme, accentColor.value)
}

/** Wrapper that also broadcasts accent color changes to detached panel windows */
function changeAccentColor(key: string) {
  setAccentColor(key)
  window.tinadec?.broadcastTheme?.(theme.value, key)
}

function minimizeWindow() {
  window.tinadec?.minimizeWindow?.()
}

function maximizeWindow() {
  window.tinadec?.maximizeWindow?.()
}

function closeWindow() {
  window.tinadec?.closeWindow?.()
}

function openExternal(url: string) {
  window.open(url, '_blank')
}

const activeSection = ref<SettingsSection>('model')

// ---- About page runtime health check ----
const aboutCoreStatus = ref<string>('')
const aboutCoreVersion = ref<string>('')
const aboutGatewayStatus = ref<string>('')

async function checkAboutHealth() {
  try {
    const res = await fetch('http://127.0.0.1:48731/api/v1/health')
    const data = await res.json()
    aboutCoreStatus.value = data.status === 'ok' ? 'ok' : ''
    aboutCoreVersion.value = data.version || ''
  } catch { aboutCoreStatus.value = '' }
  try {
    const res = await fetch('http://127.0.0.1:48730/api/v1/health')
    const data = await res.json()
    aboutGatewayStatus.value = data.status === 'ok' ? 'ok' : ''
  } catch { aboutGatewayStatus.value = '' }
}
checkAboutHealth()
const modelCenterOverview = ref<ModelCenterOverviewDto | null>(null)
const agentCenterOverview = ref<AgentCenterOverviewDto | null>(null)
const providers = ref<ModelProviderInstanceDto[]>([])
const modelReadiness = ref<ModelReadinessReceiptDto | null>(null)
const modelCatalogReadiness = ref<ModelCatalogReadinessReceiptDto | null>(null)
const routes = ref<ModelRouteDto[]>([])
const agentModes = ref<AgentModeDto[]>([])
const agents = ref<AgentProfileDto[]>([])
const agentCandidates = ref<AgentCandidateDto[]>([])
const availableTools = ref<ToolDescriptorDto[]>([])
const harnessManifest = ref<HarnessManifestDto | null>(null)
const toolLayerReadiness = ref<ToolLayerReadinessReceiptDto | null>(null)
const toolSearchResults = ref<ToolSearchResultDto[]>([])
const promptFragments = ref<PromptFragmentDto[]>([])
const promptPreview = ref<PromptContextPreviewDto | null>(null)
const projectTemplates = ref<ProjectTemplateSummary[]>([])
const selectedProviderId = ref('')
const selectedAgentId = ref('')
const configuringAgentId = ref('')
const modelCenterSection = ref<ModelCenterSection>('suppliers')
const agentRuntimeSelection = ref<AgentRuntimeSelectionKind>('inherit')
const agentRuntimeProviderId = ref('')
const agentRuntimeModelKey = ref('')
const agentRuntimeCliId = ref('')
const agentRuntimeAcpId = ref('')
const agentRuntimeModelQuery = ref('')
const agentRuntimeProviderQuery = ref('')
const agentRuntimeCliQuery = ref('')
const agentRuntimeAcpQuery = ref('')
const agentRuntimeNotice = ref('')
const agentEditTools = ref<string[]>([])
const agentEditCapabilities = ref<string[]>([])
const agentEditSystemPrompt = ref('')
const agentEditDescription = ref('')
const agentNewCapability = ref('')
const selectedProviderDetailId = ref('')
const modelProviderFilter = ref<ModelCenterFilter>('all')
const modelProviderQuery = ref('')
const modelProviderListRef = ref<HTMLElement | null>(null)
const modelDiagnosticsRef = ref<HTMLDetailsElement | null>(null)
const confirmDeleteId = ref('')
const busy = ref(false)
const loading = ref(false)
const modelCenterLoading = ref(false)
const agentCenterLoading = ref(false)
const modelCenterBusy = ref(false)
const agentRuntimeBusy = ref(false)
const modelCenterError = ref('')
const agentCenterError = ref('')
const modelCenterNotice = ref('')
const showModal = ref(false)
const agentViewMode = ref<'topology' | 'list'>('topology')
const promptSelectedFragmentId = ref('')
const promptFilterScope = ref('all')
const promptFilterCategory = ref('all')
const promptFilterAgentId = ref('all')
const promptFilterEnabled = ref('all')
const promptPreviewAgentId = ref('agent_meeting')
const promptPreviewMode = ref('')
const promptPreviewSessionId = ref('')
const promptPreviewRunId = ref('')
const promptPreviewUserContent = ref('')
const toolDiscoveryQuery = ref('')
const toolDiscoverySource = ref('all')
const toolDiscoveryRisk = ref('all')
const toolDiscoveryLoading = ref(false)
const promptForm = reactive({
  id: '',
  key: '',
  title: '',
  scope: 'agent',
  target_agent_id: 'agent_meeting',
  category: 'custom',
  content: '',
  priority: '500',
  enabled: true,
  is_builtin: false
})

const providerForm = reactive<ProviderForm>({
  id: '',
  driver: 'openai-compatible',
  display_name: 'OpenAI Compatible',
  connection_kind: 'api-key',
  base_url: 'https://api.openai.com/v1',
  model: 'gpt-5.4-mini',
  api_key: '',
  clear_api_key: false,
  binary_path: '',
  home_path: '',
  server_url: '',
  launch_args: '',
  enabled: true
})

const navItems = computed(() => [
  { key: 'model' as const, icon: KeyRound, label: t('settings.model') },
  { key: 'agents' as const, icon: Workflow, label: t('settings.agents') },
  { key: 'agentEvolution' as const, icon: Dna, label: 'Agent Evolution' },
  { key: 'promptContext' as const, icon: Bot, label: 'Prompt Context' },
  { key: 'promptEngineering' as const, icon: GitBranch, label: 'Prompt Engineering' },
  { key: 'tools' as const, icon: Terminal, label: t('settings.toolLayer') },
  { key: 'appearance' as const, icon: Palette, label: t('settings.appearance') },
  { key: 'language' as const, icon: Globe, label: t('settings.language') },
  { key: 'apiDocs' as const, icon: FileText, label: t('settings.apiDocs') },
  { key: 'about' as const, icon: Info, label: t('settings.about') },
])

const modelCenterSections = computed(() => [
  { key: 'suppliers' as const, label: t('settings.centerSuppliers'), count: modelCenterOverview.value?.suppliers.length ?? 0 },
  { key: 'api' as const, label: t('settings.centerApiConnections'), count: modelCenterOverview.value?.api_connections.length ?? 0 },
  { key: 'models' as const, label: t('settings.centerModels'), count: modelCenterOverview.value?.models.length ?? 0 },
  { key: 'cli' as const, label: 'CLI', count: modelCenterOverview.value?.cli_runtimes.length ?? 0 },
  { key: 'acp' as const, label: 'ACP', count: modelCenterOverview.value?.acp_runtimes.length ?? 0 }
])
const supplierTemplates = computed(() => new Map(
  (modelCenterOverview.value?.suppliers ?? []).map((supplier) => [supplier.driver, providerTemplateFromSupplier(supplier)])
))
const currentTemplate = computed(() => supplierTemplates.value.get(providerForm.driver) ?? findTemplate(providerForm.driver))

const chatRoute = computed(() =>
  routes.value.find((route) => route.purpose === 'planner') ?? routes.value.find((route) => route.purpose === 'chat') ?? null
)
const chatProvider = computed(() =>
  providers.value.find((provider) => provider.id === chatRoute.value?.provider_instance_id) ?? null
)
const providerReadinessById = computed(() => {
  const map = new Map<string, ModelProviderReadinessDto>()
  for (const provider of modelReadiness.value?.providers ?? []) {
    map.set(provider.provider_instance_id, provider)
  }
  return map
})
const blockedModelRoutes = computed(() =>
  (modelReadiness.value?.routes ?? []).filter((route) => route.status === 'blocked')
)
const warningCatalogTemplates = computed(() =>
  (modelCatalogReadiness.value?.templates ?? []).filter((template) => template.status !== 'ready')
)
const catalogReadinessByDriver = computed(() => {
  const map = new Map<string, ModelCatalogTemplateReadinessDto>()
  for (const template of modelCatalogReadiness.value?.templates ?? []) {
    map.set(template.driver, template)
  }
  return map
})

const formFields = computed(() => currentTemplate.value?.fields ?? {
  base_url: true, model: true, api_key: true,
  binary_path: false, home_path: false, server_url: false, launch_args: false
})
const formPlaceholders = computed(() => currentTemplate.value?.placeholders ?? {})

const modelCenterRows = computed(() => buildModelCenterRows(
  providersFromOverview(modelCenterOverview.value).filter((provider) => provider.connection_kind !== 'cli'),
  [...supplierTemplates.value.values()],
  modelReadiness.value,
  (key) => t(key)
).filter((row) => row.kind === 'instance'))
const filteredModelCenterRows = computed(() => filterModelCenterRows(
  modelCenterRows.value,
  modelProviderFilter.value,
  modelProviderQuery.value
))
const modelCenterIssueCount = computed(() => filterModelCenterRows(
  modelCenterRows.value,
  'issues',
  ''
).length)
const firstNeedsKeyProvider = computed(() =>
  providers.value.find((provider) => provider.status === 'needs_key') ?? null
)

const agentRuntimeBindings = computed(() =>
  Object.fromEntries((agentCenterOverview.value?.agents ?? []).map((agent) => [agent.id, agent.runtime_binding]))
)
const configuringRuntimeBinding = computed(() =>
  bindingForAgent(agentCenterOverview.value, configuringAgentId.value)
)
const configuringLegacyWarning = computed(() => legacyRouteWarning(configuringRuntimeBinding.value))
const runtimeBindingWritable = computed(() =>
  Boolean(agentCenterOverview.value?.capabilities.agent_runtime_binding_write && configuringRuntimeBinding.value?.writable)
)
const runtimeModels = computed(() => agentCenterOverview.value?.runtime_sources.models ?? modelCenterOverview.value?.models ?? [])
const runtimeProviders = computed(() => agentCenterOverview.value?.runtime_sources.providers ?? modelCenterOverview.value?.api_connections ?? [])
const runtimeCliOptions = computed(() => agentCenterOverview.value?.runtime_sources.cli_runtimes ?? modelCenterOverview.value?.cli_runtimes ?? [])
const runtimeAcpOptions = computed(() => agentCenterOverview.value?.runtime_sources.acp_runtimes ?? modelCenterOverview.value?.acp_runtimes ?? [])
const modelCenterDiagnostics = computed(() => modelCenterOverview.value?.diagnostics ?? [])
const agentCenterDiagnostics = computed(() => agentCenterOverview.value?.diagnostics ?? [])
const filteredRuntimeModels = computed(() => runtimeModels.value.filter((model) => runtimeQueryMatches(
  agentRuntimeModelQuery.value,
  model.model_id,
  model.provider_display_name,
  model.provider_instance_id,
  model.status,
  ...model.configuration_sources,
  ...model.route_purposes
)))
const filteredRuntimeProviders = computed(() => runtimeProviders.value.filter((provider) => runtimeQueryMatches(
  agentRuntimeProviderQuery.value,
  provider.display_name,
  provider.provider_instance_id,
  provider.driver,
  provider.status,
  provider.model
)))
const filteredRuntimeCliOptions = computed(() => runtimeCliOptions.value.filter((runtime) => runtimeQueryMatches(
  agentRuntimeCliQuery.value,
  runtime.display_name,
  runtime.runtime_id,
  runtime.driver,
  runtime.status,
  runtime.binary_path,
  runtime.home_path
)))
const filteredRuntimeAcpOptions = computed(() => runtimeAcpOptions.value.filter((runtime) => runtimeQueryMatches(
  agentRuntimeAcpQuery.value,
  runtime.display_name,
  runtime.runtime_id,
  runtime.source,
  runtime.driver,
  runtime.status,
  runtime.command
)))

const selectedProvider = computed(() =>
  providers.value.find((provider) => provider.id === selectedProviderId.value) ?? null
)
const selectedAgent = computed(() =>
  agents.value.find((agent) => agent.id === selectedAgentId.value) ?? agents.value[0] ?? null
)
const configuringAgent = computed(() =>
  agents.value.find((agent) => agent.id === configuringAgentId.value) ?? null
)
const planningAgents = computed(() => agents.value.filter((agent) => agent.layer === 'planning'))
const executionAgents = computed(() => agents.value.filter((agent) => agent.layer === 'execution'))
const configuredAgentMode = computed(() => agentModes.value.find((mode) => mode.id === configuringAgent.value?.mode) ?? null)
const gitManagerAgent = computed(() =>
  agents.value.find((agent) => agent.id === 'executor_git_manager') ??
  agents.value.find((agent) => agent.agent_type === 'git-manager') ??
  null
)
const gitManagerMode = computed(() => agentModes.value.find((mode) => mode.id === gitManagerAgent.value?.mode) ?? null)
const gitManagerTools = computed(() => {
  const toolIds = gitManagerAgent.value?.allowed_tools ?? []
  return toolIds
    .map((toolId) => availableTools.value.find((tool) => tool.id === toolId))
    .filter((tool): tool is ToolDescriptorDto => Boolean(tool))
})
const gitManagerCapabilities = computed(() =>
  (gitManagerAgent.value?.capabilities ?? []).filter((capability) =>
    capability.startsWith('git.') ||
    capability === 'handoff.explain' ||
    capability === 'conflict.resolve'
  )
)
const manifestToolList = computed(() => manifestTools(harnessManifest.value, availableTools.value))
const manifestProviders = computed(() => sortedToolProviders(harnessManifest.value))
const manifestAgentLayers = computed(() => sortedAgentLayers(harnessManifest.value))
const manifestRiskPolicies = computed(() => sortedRiskPolicies(harnessManifest.value))
const codeSuiteToolList = computed(() => codeSuiteTools(manifestToolList.value))
const codexPrimitiveTools = computed(() => manifestToolList.value.filter((tool) => tool.source === 'codex-rust'))
const supportedLanguages = computed(() => languageSupportFromTools(manifestToolList.value))
const warningToolLayerTools = computed(() =>
  (toolLayerReadiness.value?.tools ?? []).filter((tool) => tool.status !== 'ready')
)
const warningToolLayerAgents = computed(() =>
  (toolLayerReadiness.value?.agent_scopes ?? []).filter((agent) => agent.status !== 'ready')
)
const toolSourceOptions = computed(() =>
  Array.from(new Set(manifestToolList.value.map((tool) => tool.source))).sort()
)
const toolRiskOptions = computed(() =>
  Array.from(new Set(manifestToolList.value.map((tool) => tool.risk))).sort()
)
const sortedToolDiscoveryResults = computed(() => sortedToolSearchResults(toolSearchResults.value))
const promptCategories = computed(() =>
  Array.from(new Set(promptFragments.value.map((fragment) => fragment.category))).sort()
)
const promptFilteredFragments = computed(() => promptFragments.value.filter((fragment) => {
  if (promptFilterScope.value !== 'all' && fragment.scope !== promptFilterScope.value) return false
  if (promptFilterCategory.value !== 'all' && fragment.category !== promptFilterCategory.value) return false
  if (promptFilterAgentId.value !== 'all' && (fragment.target_agent_id ?? '') !== promptFilterAgentId.value) return false
  if (promptFilterEnabled.value === 'enabled' && !fragment.enabled) return false
  if (promptFilterEnabled.value === 'disabled' && fragment.enabled) return false
  return true
}))

function brandColor(driver: string) {
  return findTemplate(driver)?.brand_color ?? supplierTemplates.value.get(driver)?.brand_color ?? '#58a6ff'
}

function brandBg(driver: string) {
  return findTemplate(driver)?.brand_bg ?? supplierTemplates.value.get(driver)?.brand_bg ?? 'rgba(88,166,255,0.12)'
}

function runtimeQueryMatches(query: string, ...values: Array<string | null | undefined>) {
  const normalized = query.trim().toLocaleLowerCase()
  if (!normalized) return true
  return values.some((value) => value?.toLocaleLowerCase().includes(normalized))
}

function centerDiagnosticLabel(diagnostic: CenterDiagnosticDto) {
  if (diagnostic.code === 'CORE_CAPABILITY_UNAVAILABLE') {
    return t('settings.optionalCapabilityUnavailable', {
      source: diagnostic.source ?? 'Core',
      status: diagnostic.status ?? '—'
    })
  }
  if (diagnostic.code === 'LEGACY_SHARED_ROUTE') {
    return t('settings.sharedRouteDiagnostic', {
      purpose: diagnostic.route_purpose ?? '—',
      count: diagnostic.agent_ids?.length ?? 0
    })
  }
  return diagnostic.message
}

function configuredModelSourceLabel(source: string) {
  if (source === 'provider_default') return t('settings.modelSourceProviderDefault')
  if (source === 'route_override') return t('settings.modelSourceRouteOverride')
  return source
}

function acpRuntimeSourceLabel(source: string) {
  return source === 'legacy_provider' ? t('settings.legacyProvider') : t('settings.acpAdapter')
}

function modelCatalogModeLabel(mode?: string) {
  return mode === 'configured_only' ? t('settings.configuredOnly') : mode ?? t('settings.configuredOnly')
}

function setLocale(lang: string) {
  locale.value = lang
  localStorage.setItem('tinadec-locale', lang)
}

function fillForm(provider: ModelProviderInstanceDto) {
  providerForm.id = provider.id
  providerForm.driver = provider.driver
  providerForm.display_name = provider.display_name
  providerForm.connection_kind = provider.connection_kind
  providerForm.base_url = provider.base_url ?? ''
  providerForm.model = provider.model ?? ''
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
  providerForm.display_name = t(template.display_name_key)
  providerForm.connection_kind = template.connection_kind
  providerForm.base_url = template.default_base_url ?? ''
  providerForm.model = template.default_model ?? ''
  providerForm.binary_path = ''
  providerForm.home_path = ''
  providerForm.server_url = template.fields.server_url ? template.default_base_url ?? '' : ''
  providerForm.launch_args = ''
}

function openAddModal(template?: ProviderTemplate) {
  selectedProviderId.value = ''
  providerForm.id = ''
  if (template) {
    applyTemplateDefaults(template)
  } else {
    applyTemplateDefaults(
      modelCenterOverview.value?.suppliers[0]
        ? providerTemplateFromSupplier(modelCenterOverview.value.suppliers[0])
        : PROVIDER_TEMPLATES[0]
    )
  }
  providerForm.api_key = ''
  providerForm.clear_api_key = false
  providerForm.enabled = true
  showModal.value = true
}

function openEditModal(provider: ModelProviderInstanceDto) {
  selectedProviderId.value = provider.id
  fillForm(provider)
  showModal.value = true
}

function toggleProviderDetail(providerId: string) {
  selectedProviderDetailId.value = selectedProviderDetailId.value === providerId ? '' : providerId
}

function focusModelProviderList(filter: ModelCenterFilter) {
  modelCenterSection.value = filter === 'available' ? 'suppliers' : 'api'
  modelProviderFilter.value = filter
  nextTick(() => {
    modelProviderListRef.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
    modelProviderListRef.value?.querySelector<HTMLInputElement>('input')?.focus()
  })
}

function openModelDiagnostics() {
  if (!modelDiagnosticsRef.value) return
  modelDiagnosticsRef.value.open = true
  modelDiagnosticsRef.value.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

async function toggleProviderEnabled(provider: ModelProviderInstanceDto) {
  modelCenterBusy.value = true
  try {
    const payload: SaveModelProviderInstanceInput = {
      id: provider.id,
      driver: provider.driver,
      display_name: provider.display_name,
      connection_kind: provider.connection_kind,
      base_url: provider.base_url,
      model: provider.model,
      clear_api_key: false,
      binary_path: provider.binary_path,
      home_path: provider.home_path,
      server_url: provider.server_url,
      launch_args: provider.launch_args,
      capabilities: provider.capabilities,
      enabled: !provider.enabled
    }
    await api.saveModelProvider(provider.id, payload)
    await Promise.all([loadModelCenter(), loadAgentCenter()])
  } finally {
    modelCenterBusy.value = false
  }
}

async function deleteProvider(providerId: string) {
  modelCenterBusy.value = true
  try {
    await api.deleteModelProvider(providerId)
    if (selectedProviderDetailId.value === providerId) {
      selectedProviderDetailId.value = ''
    }
    await Promise.all([loadModelCenter(), loadAgentCenter()])
  } finally {
    modelCenterBusy.value = false
    confirmDeleteId.value = ''
  }
}

function closeModal() {
  showModal.value = false
}

async function loadModelCenter() {
  modelCenterLoading.value = true
  modelCenterError.value = ''
  try {
    const overview = await api.getModelCenterOverview()
    modelCenterOverview.value = overview
    modelCenterNotice.value = ''
    const instances = providersFromOverview(overview)
    providers.value = instances
    modelReadiness.value = overview.readiness.model ?? null
    modelCatalogReadiness.value = overview.readiness.catalog ?? null

    const selected = instances.find((provider) => provider.id === selectedProviderId.value) ?? instances[0]
    if (selected) {
      selectedProviderId.value = selected.id
    }
  } catch (error) {
    modelCenterError.value = error instanceof Error ? error.message : t('settings.centerLoadFailed')
  } finally {
    modelCenterLoading.value = false
  }
}

async function refreshProviderModels(providerInstanceId: string) {
  modelCenterBusy.value = true
  modelCenterNotice.value = ''
  try {
    await api.refreshProviderModels(providerInstanceId)
    await loadModelCenter()
  } catch (error) {
    modelCenterNotice.value = error instanceof Error ? error.message : t('settings.modelDiscoveryUnsupported')
  } finally {
    modelCenterBusy.value = false
  }
}

async function probeAcpRuntime(runtime: ModelCenterAcpRuntimeDto) {
  if (!runtime.adapter_id) return
  modelCenterBusy.value = true
  modelCenterNotice.value = ''
  try {
    await api.probeAcpAdapter(runtime.adapter_id)
    await Promise.all([loadModelCenter(), loadAgentCenter()])
  } catch (error) {
    modelCenterNotice.value = error instanceof Error ? error.message : t('settings.acpProbeFailed')
  } finally {
    modelCenterBusy.value = false
  }
}

async function loadAgentCenter() {
  agentCenterLoading.value = true
  agentCenterError.value = ''
  try {
    const [overview, toolReadiness] = await Promise.all([
      api.getAgentCenterOverview(),
      api.getToolLayerReadiness().catch(() => null)
    ])
    agentCenterOverview.value = overview
    agentModes.value = overview.modes
    agents.value = overview.agents
    agentCandidates.value = overview.candidates
    const routeMap = new Map<string, ModelRouteDto>()
    for (const agent of overview.agents) {
      const binding = agent.runtime_binding
      if (!binding.provider_instance_id) continue
      routeMap.set(binding.route_purpose, {
        purpose: binding.route_purpose,
        provider_instance_id: binding.provider_instance_id,
        model: binding.model_id ?? null,
        updated_at: agent.updated_at ?? ''
      })
    }
    routes.value = [...routeMap.values()]
    toolLayerReadiness.value = toolReadiness
    // Harness manifest is non-critical: fall back to the legacy tool list for older Core builds.
    api.getHarnessManifest()
      .then((manifest) => {
        harnessManifest.value = manifest
        availableTools.value = manifest.tools
        void loadToolDiscovery()
      })
      .catch(() => {
        harnessManifest.value = null
        api.listTools()
          .then((tools) => {
            availableTools.value = tools
            void loadToolDiscovery()
          })
          .catch(() => {
            availableTools.value = []
            toolSearchResults.value = []
          })
      })
    api.executeCodeTool('project_templates')
      .then((result) => { projectTemplates.value = projectTemplatesFromResult(result) })
      .catch(() => { projectTemplates.value = [] })
    if (!overview.agents.some((agent) => agent.id === selectedAgentId.value)) {
      selectedAgentId.value = overview.agents[0]?.id ?? ''
    }
  } catch (error) {
    agentCenterError.value = error instanceof Error ? error.message : t('settings.centerLoadFailed')
  } finally {
    agentCenterLoading.value = false
  }
}

async function loadToolDiscovery() {
  toolDiscoveryLoading.value = true
  try {
    toolSearchResults.value = await api.searchTools({
      query: toolDiscoveryQuery.value.trim() || undefined,
      source: toolDiscoverySource.value === 'all' ? undefined : toolDiscoverySource.value,
      risk: toolDiscoveryRisk.value === 'all' ? undefined : toolDiscoveryRisk.value,
      limit: 10
    })
  } catch {
    toolSearchResults.value = []
  } finally {
    toolDiscoveryLoading.value = false
  }
}

async function loadPromptContextCenter() {
  loading.value = true
  try {
    const fragments = await api.listPromptFragments()
    promptFragments.value = fragments
    if (!promptSelectedFragmentId.value && fragments.length > 0) {
      selectPromptFragment(fragments[0])
    } else if (promptSelectedFragmentId.value) {
      const selected = fragments.find((fragment) => fragment.id === promptSelectedFragmentId.value)
      if (selected) {
        selectPromptFragment(selected)
      }
    }

    if (!promptPreviewMode.value) {
      promptPreviewMode.value = agentModes.value.find((mode) => mode.id === 'plan-first')?.id ?? agentModes.value[0]?.id ?? 'plan-first'
    }
  } finally {
    loading.value = false
  }
}

function selectPromptFragment(fragment: PromptFragmentDto) {
  promptSelectedFragmentId.value = fragment.id
  promptForm.id = fragment.id
  promptForm.key = fragment.key
  promptForm.title = fragment.title
  promptForm.scope = fragment.scope
  promptForm.target_agent_id = fragment.target_agent_id ?? ''
  promptForm.category = fragment.category
  promptForm.content = fragment.content
  promptForm.priority = String(fragment.priority)
  promptForm.enabled = fragment.enabled
  promptForm.is_builtin = fragment.is_builtin
}

function newPromptFragment() {
  promptSelectedFragmentId.value = ''
  promptForm.id = ''
  promptForm.key = `custom.meeting.${Date.now()}`
  promptForm.title = 'Custom Meeting Context'
  promptForm.scope = 'agent'
  promptForm.target_agent_id = 'agent_meeting'
  promptForm.category = 'custom'
  promptForm.content = ''
  promptForm.priority = '500'
  promptForm.enabled = true
  promptForm.is_builtin = false
}

function promptPayload(): SavePromptFragmentInput {
  return {
    key: promptForm.key,
    title: promptForm.title,
    scope: promptForm.scope,
    target_agent_id: promptForm.target_agent_id || null,
    category: promptForm.category,
    content: promptForm.content,
    priority: Number(promptForm.priority) || 0,
    enabled: promptForm.enabled
  }
}

async function savePromptFragment() {
  busy.value = true
  try {
    const saved = promptForm.id
      ? await api.savePromptFragment(promptForm.id, promptPayload())
      : await api.createPromptFragment(promptPayload())
    promptSelectedFragmentId.value = saved.id
    await loadPromptContextCenter()
  } finally {
    busy.value = false
  }
}

async function deletePromptFragment() {
  if (!promptForm.id || promptForm.is_builtin) return
  busy.value = true
  try {
    await api.deletePromptFragment(promptForm.id)
    promptSelectedFragmentId.value = ''
    await loadPromptContextCenter()
  } finally {
    busy.value = false
  }
}

async function clonePromptFragment(fragmentId = promptForm.id) {
  if (!fragmentId) return
  busy.value = true
  try {
    const cloned = await api.clonePromptFragment(fragmentId)
    promptSelectedFragmentId.value = cloned.id
    await loadPromptContextCenter()
  } finally {
    busy.value = false
  }
}

async function generatePromptPreview() {
  busy.value = true
  try {
    promptPreview.value = await api.previewPromptContext({
      agent_id: promptPreviewAgentId.value || 'agent_meeting',
      mode: promptPreviewMode.value || null,
      session_id: promptPreviewSessionId.value || null,
      run_id: promptPreviewRunId.value || null,
      user_content: promptPreviewUserContent.value || null
    })
  } finally {
    busy.value = false
  }
}

async function updateAgentMode(agent: AgentProfileDto, mode: string) {
  busy.value = true
  try {
    await api.updateAgentMode(agent.id, mode)
    await loadAgentCenter()
  } finally {
    busy.value = false
  }
}

async function setAgentEnabled(agent: AgentProfileDto, enabled: boolean) {
  busy.value = true
  try {
    await api.saveAgent(agent.id, {
      name: agent.name,
      layer: agent.layer,
      agent_type: agent.agent_type,
      mode: agent.mode,
      description: agent.description,
      model_route_purpose: agent.model_route_purpose,
      allowed_tools: agent.allowed_tools,
      capabilities: agent.capabilities,
      system_prompt: agent.system_prompt,
      enabled
    })
    await loadAgentCenter()
  } finally {
    busy.value = false
  }
}

async function saveAgentProfile() {
  if (!configuringAgent.value) return
  busy.value = true
  try {
    await api.saveAgent(configuringAgent.value.id, {
      name: configuringAgent.value.name,
      layer: configuringAgent.value.layer,
      agent_type: configuringAgent.value.agent_type,
      mode: configuringAgent.value.mode,
      description: agentEditDescription.value,
      model_route_purpose: configuringAgent.value.model_route_purpose,
      allowed_tools: agentEditTools.value,
      capabilities: agentEditCapabilities.value,
      system_prompt: agentEditSystemPrompt.value || null,
      enabled: configuringAgent.value.enabled
    })
    await loadAgentCenter()
    // Re-sync edit state from the saved agent
    const updated = agents.value.find((a) => a.id === configuringAgentId.value)
    if (updated) {
      agentEditTools.value = [...updated.allowed_tools]
      agentEditCapabilities.value = [...updated.capabilities]
      agentEditSystemPrompt.value = updated.system_prompt ?? ''
      agentEditDescription.value = updated.description
    }
  } finally {
    busy.value = false
  }
}

function toggleAgentTool(toolId: string) {
  const idx = agentEditTools.value.indexOf(toolId)
  if (idx >= 0) {
    agentEditTools.value.splice(idx, 1)
  } else {
    agentEditTools.value.push(toolId)
  }
}

function removeAgentCapability(cap: string) {
  const idx = agentEditCapabilities.value.indexOf(cap)
  if (idx >= 0) {
    agentEditCapabilities.value.splice(idx, 1)
  }
}

function addAgentCapability() {
  const cap = agentNewCapability.value.trim()
  if (cap && !agentEditCapabilities.value.includes(cap)) {
    agentEditCapabilities.value.push(cap)
    agentNewCapability.value = ''
  }
}

function openAgentConfig(agent: AgentProfileDto) {
  selectedAgentId.value = agent.id
  configuringAgentId.value = agent.id
  agentEditTools.value = [...(agent.allowed_tools ?? [])]
  agentEditCapabilities.value = [...(agent.capabilities ?? [])]
  agentEditSystemPrompt.value = agent.system_prompt ?? ''
  agentEditDescription.value = agent.description ?? ''
  agentNewCapability.value = ''
  const binding = bindingForAgent(agentCenterOverview.value, agent.id)
  agentRuntimeSelection.value = binding?.selection_kind ?? 'inherit'
  agentRuntimeProviderId.value = binding?.provider_instance_id ?? runtimeProviders.value[0]?.provider_instance_id ?? ''
  agentRuntimeModelKey.value = binding?.provider_instance_id && binding.model_id
    ? modelOptionKey(binding.provider_instance_id, binding.model_id)
    : runtimeModels.value[0]
      ? modelOptionKey(runtimeModels.value[0].provider_instance_id, runtimeModels.value[0].model_id)
      : ''
  agentRuntimeCliId.value = binding?.runtime_kind === 'cli' ? binding.runtime_id ?? '' : runtimeCliOptions.value[0]?.runtime_id ?? ''
  agentRuntimeAcpId.value = binding?.runtime_kind === 'acp' ? binding.runtime_id ?? '' : runtimeAcpOptions.value[0]?.runtime_id ?? ''
  agentRuntimeModelQuery.value = ''
  agentRuntimeProviderQuery.value = ''
  agentRuntimeCliQuery.value = ''
  agentRuntimeAcpQuery.value = ''
  agentRuntimeNotice.value = ''
  // Scroll to config panel after next tick
  nextTick(() => {
    const panel = document.querySelector('.agent-detail-panel')
    if (panel) {
      panel.scrollIntoView({ behavior: 'smooth', block: 'start' })
    }
  })
}

function runtimeBindingInput(): AgentRuntimeBindingInput | null {
  if (agentRuntimeSelection.value === 'inherit') return { selection_kind: 'inherit' }
  if (agentRuntimeSelection.value === 'provider_auto') {
    return agentRuntimeProviderId.value
      ? { selection_kind: 'provider_auto', provider_instance_id: agentRuntimeProviderId.value }
      : null
  }
  if (agentRuntimeSelection.value === 'cli') {
    return agentRuntimeCliId.value ? { selection_kind: 'cli', runtime_id: agentRuntimeCliId.value } : null
  }
  if (agentRuntimeSelection.value === 'acp') {
    return agentRuntimeAcpId.value ? { selection_kind: 'acp', runtime_id: agentRuntimeAcpId.value } : null
  }

  const selected = runtimeModels.value.find((model) =>
    modelOptionKey(model.provider_instance_id, model.model_id) === agentRuntimeModelKey.value
  )
  return selected
    ? { selection_kind: 'fixed_model', provider_instance_id: selected.provider_instance_id, model_id: selected.model_id }
    : null
}

async function saveAgentRuntimeBinding(agent: AgentProfileDto) {
  const binding = runtimeBindingInput()
  if (!binding) return
  agentRuntimeBusy.value = true
  agentRuntimeNotice.value = ''
  try {
    await api.saveAgentRuntimeBinding(agent.id, binding)
    await loadAgentCenter()
  } catch (error) {
    agentRuntimeNotice.value = error instanceof Error ? error.message : t('settings.runtimeBindingUnsupported')
  } finally {
    agentRuntimeBusy.value = false
  }
}

async function saveProvider() {
  modelCenterBusy.value = true
  try {
    const isNewProvider = !providerForm.id
    const tmpl = currentTemplate.value
    const payload: SaveModelProviderInstanceInput = {
      id: providerForm.id || undefined,
      driver: providerForm.driver,
      display_name: providerForm.display_name,
      connection_kind: providerForm.connection_kind,
      base_url: formFields.value.base_url ? (providerForm.base_url || null) : null,
      model: formFields.value.model ? (providerForm.model || null) : null,
      api_key: formFields.value.api_key ? (providerForm.api_key || null) : null,
      clear_api_key: providerForm.clear_api_key,
      binary_path: formFields.value.binary_path ? (providerForm.binary_path || null) : null,
      home_path: formFields.value.home_path ? (providerForm.home_path || null) : null,
      server_url: formFields.value.server_url ? (providerForm.server_url || null) : null,
      launch_args: formFields.value.launch_args ? (providerForm.launch_args || null) : null,
      capabilities: providerForm.id
        ? selectedProvider.value?.capabilities ?? tmpl?.capabilities ?? []
        : tmpl?.capabilities ?? [],
      enabled: providerForm.enabled
    }

    const saved = providerForm.id
      ? await api.saveModelProvider(providerForm.id, payload)
      : await api.createModelProvider(payload)

    selectedProviderId.value = saved.id
    showModal.value = false
    await Promise.all([loadModelCenter(), loadAgentCenter()])
    if (isNewProvider) {
      modelProviderFilter.value = 'configured'
    }
  } finally {
    modelCenterBusy.value = false
  }
}

function connectionKindLabel(kind: string) {
  if (kind === 'cli') return t('settings.connectionKindCli')
  if (kind === 'local-server') return t('settings.connectionKindLocal')
  if (kind === 'public-api') return t('settings.connectionKindPublicApi')
  return t('settings.connectionKindApiKey')
}

function agentTypeLabel(type: string) {
  const map: Record<string, string> = {
    // Layer 1 · Planning 主动智能体
    meeting: t('settings.agentTypeMeeting'),
    'context-compressor': t('settings.agentTypeContextCompressor'),
    evolver: t('settings.agentTypeEvolver'),
    'tool-assistant': t('settings.agentTypeToolAssistant'),
    supervisor: t('settings.agentTypeSupervisor'),
    'skill-learner': t('settings.agentTypeSkillLearner'),
    // Layer 2 · Execution 被动执行类智能体
    'task-planner': t('settings.agentTypeTaskPlanner'),
    'test-multimodal': t('settings.agentTypeTestMultimodal'),
    'code-explorer': t('settings.agentTypeCodeExplorer'),
    'search-specialist': t('settings.agentTypeSearchSpecialist'),
    'file-finder': t('settings.agentTypeFileFinder'),
    'git-manager': t('settings.agentTypeGitManager'),
    'code-writer': t('settings.agentTypeCodeWriter'),
    designer: t('settings.agentTypeDesigner'),
    // Legacy types (kept for backward compatibility)
    chair: t('settings.agentTypeMeeting'),
    planner: t('settings.agentTypeTaskPlanner'),
    'tool-manager': t('settings.agentTypeToolAssistant'),
    'evolution-algorithm': t('settings.agentTypeEvolver'),
    executor: t('settings.agentTypeCodeWriter'),
    reviewer: t('settings.agentTypeSupervisor'),
  }
  return map[type] ?? type
}

function agentLayerLabel(layer: string) {
  const map: Record<string, string> = {
    planning: t('settings.agentLayerPlanning'),
    execution: t('settings.agentLayerExecution'),
    evolution: t('settings.agentLayerEvolution'),
  }
  return map[layer] ?? layer
}

function agentModeLabel(mode: string) {
  const map: Record<string, string> = {
    chat: t('settings.agentModeChat'),
    plan: t('settings.agentModePlan'),
    execute: t('settings.agentModeExecute'),
    review: t('settings.agentModeReview'),
  }
  return map[mode] ?? mode
}

function statusLabel(status: string) {
  if (status === 'ready') return t('settings.statusReady')
  if (status === 'needs_key') return t('settings.statusNeedsKey')
  if (status === 'disabled') return t('settings.statusDisabled')
  if (status === 'cooldown') return t('settings.statusCooldown')
  if (status === 'not_configured' || !status) return t('settings.statusNotConfigured')
  return status
}

function statusVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'ready') return 'default'
  if (status === 'needs_key' || status === 'not_configured') return 'destructive'
  if (status === 'disabled') return 'secondary'
  if (status === 'cooldown') return 'outline'
  return 'outline'
}

function readinessVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'ready') return 'default'
  if (status === 'blocked') return 'destructive'
  if (status === 'warning') return 'outline'
  return 'secondary'
}

function readinessStatusLabel(status: string) {
  if (status === 'ready') return t('settings.readinessReady')
  if (status === 'blocked') return t('settings.readinessBlocked')
  if (status === 'warning') return t('settings.readinessWarning')
  return status
}

loadModelCenter()
loadAgentCenter()
loadPromptContextCenter()
</script>

<template>
<div class="settings-page">
<!-- Background Layer is now rendered globally in App.vue, outside the page transition -->

<!-- Full-width draggable bar for window dragging -->
<div class="top-drag-bar" />
<div class="settings-window-controls">
      <UiButton variant="ghost" size="icon" class="window-btn minimize" :title="t('app.minimize')" @click="minimizeWindow">
        <Minus :size="14" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn maximize" :title="t('app.maximize')" @click="maximizeWindow">
        <Square :size="12" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn close" :title="t('app.close')" @click="closeWindow">
        <X :size="14" />
      </UiButton>
    </div>
    <div class="settings-shell">
      <nav class="settings-nav" :style="settingsNavStyle" v-bind="settingsNavDataAttrs">
        <div class="settings-nav-header">
          <UiButton variant="ghost" size="icon" :title="t('settings.back')" @click="router.push('/')">
            <ArrowLeft :size="16" />
          </UiButton>
          <span>{{ t('settings.title') }}</span>
        </div>
        <UiButton
          v-for="item in navItems"
          :key="item.key"
          variant="ghost"
          size="sm"
          class="settings-nav-item w-full justify-start"
          :class="{ active: activeSection === item.key }"
          @click="activeSection = item.key"
        >
          <component :is="item.icon" :size="16" />
          {{ item.label }}
        </UiButton>
      </nav>

      <div class="settings-content" :style="settingsContentStyle" v-bind="settingsContentDataAttrs">
        <Transition name="section-fade" mode="out-in">
        <div :key="activeSection" class="settings-section-wrapper">
        <template v-if="activeSection === 'model'">
          <div class="model-center-heading">
            <div>
              <h2>{{ t('settings.modelCenter') }}</h2>
              <p>{{ t('settings.modelCenterSubtitle') }}</p>
            </div>
            <div class="model-heading-actions">
              <UiButton variant="outline" size="sm" @click="focusModelProviderList('available')">
                <Plus :size="14" />
                <span>{{ t('settings.addProvider') }}</span>
              </UiButton>
              <UiButton variant="outline" size="sm" :disabled="modelCenterLoading || modelCenterBusy" @click="loadModelCenter">
                <Server :size="14" />
                <span>{{ t('settings.refresh') }}</span>
              </UiButton>
            </div>
          </div>

          <div v-if="modelCenterError" class="center-message error">
            <Info :size="16" />
            <span>{{ modelCenterError }}</span>
            <UiButton variant="outline" size="sm" @click="loadModelCenter">{{ t('settings.retry') }}</UiButton>
          </div>
          <div v-if="modelCenterNotice" class="center-message warning">
            <Info :size="16" />
            <span>{{ modelCenterNotice }}</span>
          </div>
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

          <section v-if="modelReadiness || modelCatalogReadiness" class="model-health-overview">
            <div class="model-health-head">
              <div>
                <h3>{{ t('settings.modelHealth') }}</h3>
                <span>{{ t('settings.modelHealthHint') }}</span>
              </div>
              <UiBadge v-if="modelReadiness" :variant="readinessVariant(modelReadiness.status)">
                <Circle :size="8" />
                {{ readinessStatusLabel(modelReadiness.status) }}
              </UiBadge>
            </div>
            <div class="model-health-metrics">
              <div>
                <span>{{ t('settings.readyProvidersMetric') }}</span>
                <strong>{{ modelReadiness ? `${modelReadiness.ready_provider_count}/${modelReadiness.provider_count}` : '—' }}</strong>
              </div>
              <div :class="{ attention: (modelReadiness?.blocked_route_count ?? 0) > 0 }">
                <span>{{ t('settings.blockedRoutesMetric') }}</span>
                <strong>{{ modelReadiness?.blocked_route_count ?? '—' }}</strong>
              </div>
              <div>
                <span>{{ t('settings.readyTemplatesMetric') }}</span>
                <strong>{{ modelCatalogReadiness ? `${modelCatalogReadiness.ready_template_count}/${modelCatalogReadiness.template_count}` : '—' }}</strong>
              </div>
              <div>
                <span>{{ t('settings.runtimeModulesMetric') }}</span>
                <strong>{{ modelCatalogReadiness?.runtime_module_count ?? '—' }}</strong>
              </div>
            </div>
            <div v-if="modelReadiness && modelReadiness.status !== 'ready'" class="model-health-alert">
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
              <UiButton v-else variant="outline" size="sm" @click="openModelDiagnostics">
                {{ t('settings.advancedDiagnostics') }}
              </UiButton>
            </div>
          </section>

          <details v-if="modelReadiness || modelCatalogReadiness" ref="modelDiagnosticsRef" class="model-diagnostics">
            <summary>
              <span>{{ t('settings.advancedDiagnostics') }}</span>
              <ChevronRight :size="14" />
            </summary>
            <div class="model-diagnostics-grid">
              <section v-if="modelReadiness" class="model-diagnostic-section">
                <div class="model-diagnostic-head">
                  <div>
                    <strong>{{ t('settings.providerReceipt') }}</strong>
                    <span>{{ modelReadiness.receipt_id }}</span>
                  </div>
                  <UiBadge :variant="readinessVariant(modelReadiness.status)">{{ readinessStatusLabel(modelReadiness.status) }}</UiBadge>
                </div>
                <p class="model-diagnostic-meta">{{ t('settings.generatedAt') }} · {{ modelReadiness.generated_at }}</p>
                <div class="model-diagnostic-list">
                  <strong>{{ t('settings.blockedRoutes') }}</strong>
                  <div v-if="blockedModelRoutes.length > 0" class="model-readiness-routes">
                    <span v-for="route in blockedModelRoutes" :key="route.purpose">
                      {{ route.purpose }} · {{ route.provider_display_name ?? route.provider_instance_id }}
                    </span>
                  </div>
                  <span v-else class="quiet">{{ t('settings.noBlockedRoutes') }}</span>
                </div>
                <ul v-if="modelReadiness.design_notes.length > 0" class="model-diagnostic-notes">
                  <li v-for="note in modelReadiness.design_notes" :key="note">{{ note }}</li>
                </ul>
              </section>
              <section v-if="modelCatalogReadiness" class="model-diagnostic-section">
                <div class="model-diagnostic-head">
                  <div>
                    <strong>{{ t('settings.catalogReceipt') }}</strong>
                    <span>{{ modelCatalogReadiness.receipt_id }}</span>
                  </div>
                  <UiBadge :variant="readinessVariant(modelCatalogReadiness.status)">{{ readinessStatusLabel(modelCatalogReadiness.status) }}</UiBadge>
                </div>
                <p class="model-diagnostic-meta">{{ t('settings.generatedAt') }} · {{ modelCatalogReadiness.generated_at }}</p>
                <div class="model-diagnostic-list">
                  <strong>{{ t('settings.catalogWarnings') }}</strong>
                  <div v-if="warningCatalogTemplates.length > 0" class="catalog-readiness-rows">
                    <div v-for="template in warningCatalogTemplates" :key="template.driver" class="catalog-readiness-row">
                      <div>
                        <strong>{{ template.display_name }}</strong>
                        <span>{{ template.runtime_module_family }} · {{ template.live_discovery_policy }}</span>
                      </div>
                      <UiBadge :variant="readinessVariant(template.status)">{{ template.runtime_module_status }}</UiBadge>
                    </div>
                  </div>
                  <span v-else class="quiet">{{ t('settings.noCatalogWarnings') }}</span>
                </div>
                <ul v-if="modelCatalogReadiness.design_notes.length > 0" class="model-diagnostic-notes">
                  <li v-for="note in modelCatalogReadiness.design_notes" :key="note">{{ note }}</li>
                </ul>
              </section>
            </div>
          </details>

          <div class="model-center-tabs" role="tablist" :aria-label="t('settings.modelCenterResources')">
            <button
              v-for="section in modelCenterSections"
              :key="section.key"
              role="tab"
              :aria-selected="modelCenterSection === section.key"
              :class="{ active: modelCenterSection === section.key }"
              @click="modelCenterSection = section.key"
            >
              <span>{{ section.label }}</span>
              <UiBadge variant="secondary">{{ section.count }}</UiBadge>
            </button>
          </div>

          <section v-if="modelCenterSection === 'suppliers'" ref="modelProviderListRef" class="center-resource-section">
            <div class="center-resource-heading">
              <div>
                <h3>{{ t('settings.centerSuppliers') }}</h3>
                <p>{{ t('settings.suppliersHint') }}</p>
              </div>
              <UiBadge variant="outline">{{ t('settings.coreCatalog') }}</UiBadge>
            </div>
            <div class="center-resource-grid supplier-grid">
              <article v-for="supplier in modelCenterOverview?.suppliers ?? []" :key="supplier.supplier_id" class="center-resource-card">
                <div class="center-resource-card-head">
                  <span class="provider-brand-icon" :style="{ color: brandColor(supplier.driver), background: brandBg(supplier.driver) }" v-html="supplierTemplates.get(supplier.driver)?.icon ?? ''"></span>
                  <div>
                    <strong>{{ supplier.display_name }}</strong>
                    <span>{{ supplier.provider_family }} · {{ supplier.driver }}</span>
                  </div>
                  <UiBadge v-if="catalogReadinessByDriver.get(supplier.driver)" :variant="readinessVariant(catalogReadinessByDriver.get(supplier.driver)!.status)">
                    {{ readinessStatusLabel(catalogReadinessByDriver.get(supplier.driver)!.status) }}
                  </UiBadge>
                </div>
                <p>{{ supplier.summary }}</p>
                <div class="center-resource-meta">
                  <span>{{ supplier.transport_kind }}</span>
                  <span>{{ supplier.credential_kind }}</span>
                  <span v-if="supplier.default_model">{{ supplier.default_model }}</span>
                </div>
                <div class="center-resource-actions">
                  <UiButton variant="outline" size="sm" @click="openAddModal(providerTemplateFromSupplier(supplier))">
                    <Plus :size="14" />
                    {{ t('settings.addProvider') }}
                  </UiButton>
                </div>
              </article>
            </div>
            <div v-if="(modelCenterOverview?.suppliers.length ?? 0) === 0" class="center-empty-state">
              <Server :size="20" />
              <span>{{ t('settings.noSuppliers') }}</span>
            </div>
          </section>

          <section v-if="modelCenterSection === 'models'" class="center-resource-section">
            <div class="center-resource-heading">
              <div>
                <h3>{{ t('settings.centerModels') }}</h3>
                <p>{{ t('settings.configuredModelsHint') }}</p>
              </div>
              <UiBadge variant="outline">{{ modelCatalogModeLabel(modelCenterOverview?.capabilities.model_catalog_mode) }}</UiBadge>
            </div>
            <div class="center-resource-list">
              <article v-for="model in modelCenterOverview?.models ?? []" :key="model.id" class="center-resource-list-row">
                <div class="center-resource-primary">
                  <Cpu :size="17" />
                  <div>
                    <strong>{{ model.model_id }}</strong>
                    <span>{{ model.provider_display_name ?? model.provider_instance_id }}</span>
                  </div>
                </div>
                <div class="center-resource-meta">
                  <span v-for="source in model.configuration_sources" :key="source">{{ configuredModelSourceLabel(source) }}</span>
                  <span v-for="purpose in model.route_purposes" :key="purpose">{{ purpose }}</span>
                </div>
                <UiBadge :variant="statusVariant(model.status)">{{ statusLabel(model.status) }}</UiBadge>
                <UiButton
                  variant="outline"
                  size="sm"
                  :disabled="modelCenterBusy || !modelCenterOverview?.capabilities.model_discovery_refresh"
                  :title="modelCenterOverview?.capabilities.model_discovery_refresh ? t('settings.refreshModels') : t('settings.modelDiscoveryUnsupported')"
                  @click="refreshProviderModels(model.provider_instance_id)"
                >
                  <Server :size="14" />
                  {{ t('settings.refreshModels') }}
                </UiButton>
              </article>
            </div>
            <div v-if="(modelCenterOverview?.models.length ?? 0) === 0" class="center-empty-state">
              <Cpu :size="20" />
              <span>{{ t('settings.noConfiguredModels') }}</span>
            </div>
          </section>

          <section v-if="modelCenterSection === 'cli'" class="center-resource-section">
            <div class="center-resource-heading">
              <div>
                <h3>CLI</h3>
                <p>{{ t('settings.cliRuntimeHint') }}</p>
              </div>
            </div>
            <div class="center-resource-list">
              <article v-for="runtime in modelCenterOverview?.cli_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
                <div class="center-resource-primary">
                  <Terminal :size="17" />
                  <div>
                    <strong>{{ runtime.display_name }}</strong>
                    <span>{{ runtime.driver }} · {{ runtime.runtime_id }}</span>
                  </div>
                </div>
                <div class="center-resource-paths">
                  <code>{{ runtime.binary_path || t('settings.pathNotConfigured') }}</code>
                  <code>{{ runtime.home_path || t('settings.workspaceNotConfigured') }}</code>
                </div>
                <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
                <UiButton
                  v-if="providers.find(provider => provider.id === runtime.provider_instance_id)"
                  variant="outline"
                  size="sm"
                  @click="openEditModal(providers.find(provider => provider.id === runtime.provider_instance_id)!)"
                >
                  <Settings2 :size="14" />
                  {{ t('settings.editConfig') }}
                </UiButton>
              </article>
            </div>
            <div v-if="(modelCenterOverview?.cli_runtimes.length ?? 0) === 0" class="center-empty-state">
              <Terminal :size="20" />
              <span>{{ t('settings.noCliRuntimes') }}</span>
            </div>
          </section>

          <section v-if="modelCenterSection === 'acp'" class="center-resource-section">
            <div class="center-resource-heading">
              <div>
                <h3>ACP</h3>
                <p>{{ t('settings.acpRuntimeHint') }}</p>
              </div>
              <UiButton variant="outline" size="sm" @click="router.push('/market')">
                <Plus :size="14" />
                {{ t('settings.manageInMarketplace') }}
              </UiButton>
            </div>
            <div class="center-resource-list">
              <article v-for="runtime in modelCenterOverview?.acp_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
                <div class="center-resource-primary">
                  <Workflow :size="17" />
                  <div>
                    <strong>{{ runtime.display_name }}</strong>
                    <span>{{ acpRuntimeSourceLabel(runtime.source) }} · {{ runtime.runtime_id }}</span>
                  </div>
                </div>
                <div class="center-resource-meta">
                  <span v-for="capability in runtime.capabilities.slice(0, 4)" :key="capability">{{ capability }}</span>
                </div>
                <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
                <UiButton
                  v-if="runtime.adapter_id"
                  variant="outline"
                  size="sm"
                  :disabled="modelCenterBusy || !modelCenterOverview?.capabilities.acp_probe"
                  @click="probeAcpRuntime(runtime)"
                >
                  <Server :size="14" />
                  {{ t('settings.probe') }}
                </UiButton>
              </article>
            </div>
            <div v-if="(modelCenterOverview?.acp_runtimes.length ?? 0) === 0" class="center-empty-state">
              <Workflow :size="20" />
              <span>{{ t('settings.noAcpRuntimes') }}</span>
            </div>
          </section>

          <section v-if="modelCenterSection === 'api'" ref="modelProviderListRef" class="model-provider-section">
            <div class="model-provider-toolbar">
              <div class="model-provider-search">
                <Search :size="15" />
                <UiInput v-model="modelProviderQuery" :placeholder="t('settings.providerSearchPlaceholder')" />
              </div>
              <div class="model-provider-filters" role="group" :aria-label="t('settings.providerFilters')">
                <button :class="{ active: modelProviderFilter === 'all' }" :aria-pressed="modelProviderFilter === 'all'" @click="modelProviderFilter = 'all'">
                  {{ t('settings.filterAll') }}
                </button>
                <button :class="{ active: modelProviderFilter === 'issues' }" :aria-pressed="modelProviderFilter === 'issues'" @click="modelProviderFilter = 'issues'">
                  {{ t('settings.filterIssues') }}
                  <span v-if="modelCenterIssueCount > 0">{{ modelCenterIssueCount }}</span>
                </button>
                <button :class="{ active: modelProviderFilter === 'configured' }" :aria-pressed="modelProviderFilter === 'configured'" @click="modelProviderFilter = 'configured'">
                  {{ t('settings.filterConfigured') }}
                </button>
              </div>
              <span class="model-provider-count">{{ t('settings.providerResultCount', { visible: filteredModelCenterRows.length, total: modelCenterRows.length }) }}</span>
            </div>

            <div class="model-provider-table">
              <div class="model-provider-table-head" aria-hidden="true">
                <span>{{ t('settings.providerName') }}</span>
                <span>{{ t('settings.connectionKind') }}</span>
                <span>{{ t('settings.modelLabel') }}</span>
                <span>{{ t('settings.status') }}</span>
                <span>{{ t('settings.actions') }}</span>
              </div>
              <template v-for="row in filteredModelCenterRows" :key="row.key">
                <div class="model-provider-row" :class="{ issue: row.kind === 'instance' && ['blocked', 'warning'].includes(row.readiness?.status ?? '') }">
                  <button
                    class="model-provider-identity"
                    :aria-expanded="row.kind === 'instance' ? selectedProviderDetailId === row.provider.id : undefined"
                    @click="row.kind === 'instance' ? toggleProviderDetail(row.provider.id) : openAddModal(row.template)"
                  >
                    <span class="provider-brand-icon" :style="{ color: brandColor(row.driver), background: brandBg(row.driver) }" v-html="row.template?.icon ?? ''"></span>
                    <span>
                      <strong :title="row.display_name">{{ row.display_name }}</strong>
                      <small :title="row.driver">{{ row.driver }}</small>
                    </span>
                  </button>
                  <span class="model-provider-cell model-provider-connection">{{ connectionKindLabel(row.connection_kind) }}</span>
                  <span class="model-provider-cell model-provider-model" :title="row.model || t('settings.noModel')">{{ row.model || t('settings.noModel') }}</span>
                  <div class="model-provider-status">
                    <UiBadge v-if="row.kind === 'instance'" :variant="statusVariant(row.provider.status)">
                      <Circle :size="8" />
                      {{ statusLabel(row.provider.status) }}
                    </UiBadge>
                    <UiBadge v-else variant="outline">{{ t('settings.notAdded') }}</UiBadge>
                  </div>
                  <div class="model-provider-actions">
                    <template v-if="row.kind === 'instance'">
                      <UiButton
                        v-if="row.template"
                        variant="ghost"
                        size="icon"
                        :title="t('settings.addSameProvider')"
                        @click="openAddModal(row.template)"
                      >
                        <Plus :size="14" />
                      </UiButton>
                      <UiButton variant="ghost" size="icon" :title="t('settings.editConfig')" @click="openEditModal(row.provider)">
                        <Settings2 :size="14" />
                      </UiButton>
                      <UiButton
                        variant="ghost"
                        size="icon"
                        :title="selectedProviderDetailId === row.provider.id ? t('settings.collapseDetails') : t('settings.expandDetails')"
                        :aria-expanded="selectedProviderDetailId === row.provider.id"
                        @click="toggleProviderDetail(row.provider.id)"
                      >
                        <ChevronRight :size="14" class="provider-chevron" :class="{ open: selectedProviderDetailId === row.provider.id }" />
                      </UiButton>
                    </template>
                    <UiButton v-else variant="outline" size="sm" @click="openAddModal(row.template)">
                      <Plus :size="14" />
                      {{ t('settings.addProvider') }}
                    </UiButton>
                  </div>
                  <span class="model-provider-mobile-meta">
                    {{ connectionKindLabel(row.connection_kind) }} · {{ row.model || row.driver }}
                  </span>
                </div>

                <Transition name="detail-slide">
                <div v-if="row.kind === 'instance' && selectedProviderDetailId === row.provider.id" class="provider-detail-panel">
                  <div class="provider-detail-head">
                    <span class="provider-detail-logo" :style="{ background: brandBg(row.provider.driver), borderColor: brandColor(row.provider.driver) + '30' }" v-html="row.template?.icon ?? ''"></span>
                    <div class="provider-detail-info">
                      <strong>{{ row.provider.display_name }}</strong>
                      <span class="provider-detail-driver">{{ row.provider.driver }} · {{ connectionKindLabel(row.provider.connection_kind) }}</span>
                    </div>
                    <UiBadge :variant="statusVariant(row.provider.status)">
                      <Circle :size="8" />
                      {{ statusLabel(row.provider.status) }}
                    </UiBadge>
                  </div>

                  <div class="provider-detail-section">
                    <div class="provider-detail-section-title">{{ t('settings.connectionConfig') }}</div>
                    <div class="provider-detail-grid">
                      <div v-if="row.provider.base_url" class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.baseUrl') }}</span>
                        <span class="provider-detail-value provider-detail-mono">{{ row.provider.base_url }}</span>
                      </div>
                      <div v-if="row.provider.model" class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.modelLabel') }}</span>
                        <span class="provider-detail-value provider-detail-mono">{{ row.provider.model }}</span>
                      </div>
                      <div class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.apiKey') }}</span>
                        <span class="provider-detail-value">
                          <span :class="['provider-key-indicator', row.provider.has_api_key ? 'has-key' : 'no-key']"></span>
                          {{ row.provider.has_api_key ? t('settings.apiKeyStored') : t('settings.apiKeyNotSet') }}
                        </span>
                      </div>
                      <div class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.connectionKind') }}</span>
                        <span class="provider-detail-value">{{ connectionKindLabel(row.provider.connection_kind) }}</span>
                      </div>
                      <div v-if="row.provider.binary_path" class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.binaryPath') }}</span>
                        <span class="provider-detail-value provider-detail-mono">{{ row.provider.binary_path }}</span>
                      </div>
                      <div v-if="row.provider.server_url" class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.serverUrl') }}</span>
                        <span class="provider-detail-value provider-detail-mono">{{ row.provider.server_url }}</span>
                      </div>
                      <div v-if="row.provider.home_path" class="provider-detail-cell">
                        <span class="provider-detail-label">{{ t('settings.homePath') }}</span>
                        <span class="provider-detail-value provider-detail-mono">{{ row.provider.home_path }}</span>
                      </div>
                    </div>
                  </div>

                  <div v-if="row.provider.capabilities.length > 0" class="provider-detail-section">
                    <div class="provider-detail-section-title">{{ t('settings.capabilities') }}</div>
                    <div class="model-capability-row">
                      <span v-for="cap in row.provider.capabilities" :key="cap" class="provider-cap-tag">{{ cap }}</span>
                    </div>
                  </div>

                  <div v-if="row.provider.status_message" class="provider-status-note">
                    <Terminal :size="14" />
                    <span>{{ row.provider.status_message }}</span>
                  </div>

                  <details
                    v-if="catalogReadinessByDriver.get(row.provider.driver) || providerReadinessById.get(row.provider.id)"
                    class="provider-diagnostics"
                  >
                    <summary>
                      <span>{{ t('settings.providerDiagnostics') }}</span>
                      <ChevronRight :size="13" />
                    </summary>
                    <div class="provider-diagnostics-content">
                      <div v-if="catalogReadinessByDriver.get(row.provider.driver)" class="provider-readiness-detail">
                        <div class="provider-readiness-detail-head">
                          <span>{{ t('settings.catalogReceipt') }}</span>
                          <UiBadge :variant="readinessVariant(catalogReadinessByDriver.get(row.provider.driver)!.status)">
                            {{ readinessStatusLabel(catalogReadinessByDriver.get(row.provider.driver)!.status) }}
                          </UiBadge>
                        </div>
                        <p>{{ catalogReadinessByDriver.get(row.provider.driver)!.summary }}</p>
                        <div class="provider-readiness-evidence">
                          <span>{{ catalogReadinessByDriver.get(row.provider.driver)!.runtime_module_family }}</span>
                          <span>{{ catalogReadinessByDriver.get(row.provider.driver)!.runtime_module_status }}</span>
                          <span>{{ catalogReadinessByDriver.get(row.provider.driver)!.live_discovery_policy }}</span>
                        </div>
                      </div>
                      <div v-if="providerReadinessById.get(row.provider.id)" class="provider-readiness-detail">
                        <div class="provider-readiness-detail-head">
                          <span>{{ t('settings.providerReceipt') }}</span>
                          <UiBadge :variant="readinessVariant(providerReadinessById.get(row.provider.id)!.status)">
                            {{ readinessStatusLabel(providerReadinessById.get(row.provider.id)!.status) }}
                          </UiBadge>
                        </div>
                        <p>{{ providerReadinessById.get(row.provider.id)!.summary }}</p>
                        <div class="provider-readiness-evidence">
                          <span v-for="item in providerReadinessById.get(row.provider.id)!.evidence.slice(0, 5)" :key="item">{{ item }}</span>
                        </div>
                        <div v-if="providerReadinessById.get(row.provider.id)!.route_purposes.length > 0" class="provider-readiness-evidence route-list">
                          <span v-for="purpose in providerReadinessById.get(row.provider.id)!.route_purposes" :key="purpose">{{ purpose }}</span>
                        </div>
                      </div>
                    </div>
                  </details>

                  <div class="provider-detail-actions">
                    <UiButton variant="outline" size="sm" @click="openEditModal(row.provider)">
                      <Edit3 :size="14" />
                      <span>{{ t('settings.editConfig') }}</span>
                    </UiButton>
                    <UiButton variant="outline" size="sm" :disabled="modelCenterBusy" @click="toggleProviderEnabled(row.provider)">
                      <component :is="row.provider.enabled ? X : Check" :size="14" />
                      <span>{{ row.provider.enabled ? t('settings.disable') : t('settings.enable') }}</span>
                    </UiButton>
                    <span class="provider-action-spacer"></span>
                    <UiButton v-if="confirmDeleteId !== row.provider.id" variant="ghost" size="sm" class="provider-delete-btn" @click="confirmDeleteId = row.provider.id">
                      <Trash2 :size="14" />
                      <span>{{ t('settings.delete') }}</span>
                    </UiButton>
                    <template v-else>
                      <span class="delete-confirm-text">{{ t('settings.confirmDeleteProvider') }}</span>
                      <UiButton variant="destructive" size="sm" :disabled="modelCenterBusy" @click="deleteProvider(row.provider.id)">{{ t('settings.confirmDelete') }}</UiButton>
                      <UiButton variant="ghost" size="sm" @click="confirmDeleteId = ''">{{ t('settings.cancel') }}</UiButton>
                    </template>
                  </div>
                </div>
                </Transition>
              </template>
              <div v-if="filteredModelCenterRows.length === 0" class="model-provider-empty">
                <Search :size="18" />
                <span>{{ t('settings.noProviderResults') }}</span>
              </div>
            </div>
          </section>
        </template>

        <template v-if="activeSection === 'agents'">
          <div class="model-center-heading">
            <div>
              <h2>{{ t('settings.agentCenter') }}</h2>
              <p>{{ t('settings.agentCenterSubtitle') }}</p>
            </div>
            <div class="agent-heading-actions">
              <div class="agent-view-toggle">
                <button
                  :class="['agent-view-btn', { active: agentViewMode === 'topology' }]"
                  :title="t('settings.topologyView')"
                  @click="agentViewMode = 'topology'"
                >
                  <LayoutGrid :size="15" />
                </button>
                <button
                  :class="['agent-view-btn', { active: agentViewMode === 'list' }]"
                  :title="t('settings.listView')"
                  @click="agentViewMode = 'list'"
                >
                  <List :size="15" />
                </button>
              </div>
              <UiButton variant="outline" size="sm" :disabled="agentCenterLoading || agentRuntimeBusy" @click="loadAgentCenter">
                <Server :size="14" />
                <span>{{ t('settings.refresh') }}</span>
              </UiButton>
            </div>
          </div>

          <div v-if="agentCenterError" class="center-message error">
            <Info :size="16" />
            <span>{{ agentCenterError }}</span>
            <UiButton variant="outline" size="sm" @click="loadAgentCenter">{{ t('settings.retry') }}</UiButton>
          </div>
          <div v-if="agentCenterDiagnostics.length > 0" class="center-message warning center-diagnostics-message">
            <Info :size="16" />
            <div class="center-message-content">
              <strong>{{ t('settings.centerDiagnostics') }}</strong>
              <ul>
                <li v-for="diagnostic in agentCenterDiagnostics" :key="`${diagnostic.code}:${diagnostic.source ?? ''}:${diagnostic.route_purpose ?? ''}:${diagnostic.agent_ids?.join(',') ?? ''}:${diagnostic.status ?? ''}`">
                  {{ centerDiagnosticLabel(diagnostic) }}
                </li>
              </ul>
            </div>
            <UiButton variant="outline" size="sm" :disabled="agentCenterLoading" @click="loadAgentCenter">{{ t('settings.retry') }}</UiButton>
          </div>

          <section v-if="gitManagerAgent" class="git-manager-panel">
            <div class="git-manager-summary">
              <div class="agent-card-icon execution git-manager-icon">
                <Terminal :size="18" />
              </div>
              <div class="git-manager-copy">
                <div class="git-manager-title-row">
                  <h3>{{ gitManagerAgent.name }}</h3>
                  <UiBadge :variant="gitManagerAgent.enabled ? 'default' : 'secondary'">
                    {{ gitManagerAgent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
                  </UiBadge>
                </div>
                <p>{{ t('settings.gitManagerSubtitle') }}</p>
              </div>
              <UiButton variant="outline" size="sm" @click="openAgentConfig(gitManagerAgent)">
                <Settings2 :size="14" />
                <span>{{ t('settings.gitManagerConfigure') }}</span>
              </UiButton>
            </div>

            <div class="git-manager-grid">
              <div class="git-manager-metric">
                <ShieldCheck :size="16" />
                <div>
                  <span>{{ t('settings.gitManagerApprovalTitle') }}</span>
                  <strong>{{ t('settings.approvalGateOn') }}</strong>
                  <p>{{ t('settings.gitManagerApprovalBody') }}</p>
                </div>
              </div>
              <div class="git-manager-metric">
                <Workflow :size="16" />
                <div>
                  <span>{{ t('settings.gitManagerRouteTitle') }}</span>
                  <strong>{{ runtimeSourceSummary(agentRuntimeBindings[gitManagerAgent.id]) || t('settings.runtimeUnresolved') }}</strong>
                  <p>{{ gitManagerMode?.display_name ?? agentModeLabel(gitManagerAgent.mode) }} · {{ gitManagerAgent.model_route_purpose }}</p>
                </div>
              </div>
              <div class="git-manager-metric">
                <FileText :size="16" />
                <div>
                  <span>{{ t('settings.gitManagerHandoffTitle') }}</span>
                  <strong>{{ t('settings.gitManagerHandoffName') }}</strong>
                  <p>{{ t('settings.gitManagerHandoffBody') }}</p>
                </div>
              </div>
            </div>

            <div class="git-manager-tool-row">
              <div>
                <span>{{ t('settings.agentTools') }}</span>
                <div class="model-capability-row compact">
                  <span v-for="tool in gitManagerTools" :key="tool.id">{{ tool.display_name }}</span>
                </div>
              </div>
              <div>
                <span>{{ t('settings.gitManagerCapabilitiesTitle') }}</span>
                <div class="model-capability-row compact">
                  <span v-for="capability in gitManagerCapabilities" :key="capability">{{ capability }}</span>
                </div>
              </div>
            </div>
          </section>

          <div v-if="agentViewMode === 'topology'" class="agent-topology-section">
            <AgentTopologyCanvas
              :agents="agents"
              :candidates="agentCandidates"
              :providers="providers"
              :routes="routes"
              :runtime-bindings="agentRuntimeBindings"
              :selected-agent-id="selectedAgentId"
              @select-agent="selectedAgentId = $event"
              @configure-agent="openAgentConfig(agents.find(a => a.id === $event)!)"
            />
          </div>

          <template v-if="agentViewMode === 'list'">
            <section class="agent-column">
              <div class="model-section-header">
                <h3>{{ t('settings.planningLayer') }}</h3>
                <UiBadge variant="secondary">{{ planningAgents.length }}</UiBadge>
              </div>
              <article
                v-for="agent in planningAgents"
                :key="agent.id"
                class="agent-card"
                :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
              >
                <button class="agent-card-select" @click="selectedAgentId = agent.id">
                  <div class="agent-card-icon">
                    <Workflow :size="17" />
                  </div>
                  <div class="agent-card-main">
                    <strong>{{ agent.name }}</strong>
                    <span>{{ agentTypeLabel(agent.agent_type) }} · {{ agentModeLabel(agent.mode) }}</span>
                    <small>{{ runtimeSourceSummary(agentRuntimeBindings[agent.id]) || t('settings.runtimeUnresolved') }}</small>
                  </div>
                  <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
                    {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
                  </UiBadge>
                </button>
                <button class="agent-card-more" :title="t('settings.openAgentConfig')" @click.stop="openAgentConfig(agent)">
                  <MoreHorizontal :size="16" />
                </button>
              </article>
            </section>

            <section class="agent-column">
              <div class="model-section-header">
                <h3>{{ t('settings.executionLayer') }}</h3>
                <UiBadge variant="secondary">{{ executionAgents.length }}</UiBadge>
              </div>
              <article
                v-for="agent in executionAgents"
                :key="agent.id"
                class="agent-card"
                :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
              >
                <button class="agent-card-select" @click="selectedAgentId = agent.id">
                  <div class="agent-card-icon execution">
                    <Cpu :size="17" />
                  </div>
                  <div class="agent-card-main">
                    <strong>{{ agent.name }}</strong>
                    <span>{{ agentTypeLabel(agent.agent_type) }} · {{ agentModeLabel(agent.mode) }}</span>
                    <small>{{ runtimeSourceSummary(agentRuntimeBindings[agent.id]) || t('settings.runtimeUnresolved') }}</small>
                  </div>
                  <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
                    {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
                  </UiBadge>
                </button>
                <button class="agent-card-more" :title="t('settings.openAgentConfig')" @click.stop="openAgentConfig(agent)">
                  <MoreHorizontal :size="16" />
                </button>
              </article>
            </section>
          </template>

          <UiCard v-if="configuringAgent" class="agent-detail-panel">
            <template #content>
              <div class="agent-detail-head">
                <div class="agent-card-icon" :class="{ execution: configuringAgent.layer === 'execution' }">
                  <component :is="configuringAgent.layer === 'planning' ? Workflow : Cpu" :size="20" />
                </div>
                <div>
                  <h3>{{ t('settings.agentConfiguration') }} · {{ configuringAgent.name }}</h3>
                  <p>{{ agentTypeLabel(configuringAgent.agent_type) }} · {{ agentLayerLabel(configuringAgent.layer) }}</p>
                </div>
                <UiButton variant="ghost" size="icon" :title="t('settings.closeConfig')" @click="configuringAgentId = ''">
                  <X :size="16" />
                </UiButton>
              </div>

              <!-- 启用开关 -->
              <div class="agent-config-switch">
                <div>
                  <strong>{{ t('settings.agentEnabled') }}</strong>
                  <span>{{ configuringAgent.is_built_in ? t('settings.builtInAgent') : configuringAgent.id }}</span>
                </div>
                <UiSwitch
                  :model-value="configuringAgent.enabled"
                  :disabled="busy"
                  @update:model-value="setAgentEnabled(configuringAgent, $event)"
                />
              </div>

              <!-- 运行模式 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentModeTitle') }}</div>
                <div class="agent-mode-grid">
                  <button
                    v-for="mode in agentModes"
                    :key="mode.id"
                    class="agent-mode-card"
                    :class="{ active: configuringAgent.mode === mode.id }"
                    @click="updateAgentMode(configuringAgent, mode.id)"
                  >
                    <strong>{{ mode.display_name }}</strong>
                    <span>{{ mode.summary }}</span>
                    <small>
                      {{ t('settings.parallelExecutors') }} {{ mode.max_parallel_executors }}
                      · {{ mode.worktree_isolation ? t('settings.worktreeOn') : t('settings.worktreeOff') }}
                    </small>
                  </button>
                </div>
                <div v-if="configuredAgentMode" class="agent-policy-strip">
                  <ShieldCheck :size="16" />
                  <span>
                    {{ configuredAgentMode.approval_required ? t('settings.approvalGateOn') : t('settings.approvalGateOff') }}
                    · {{ configuredAgentMode.budget_policy }}
                  </span>
                </div>
              </div>

              <!-- 运行来源 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentRuntimeSource') }}</div>
                <div class="agent-detail-grid">
                  <div>
                    <span>{{ t('settings.routePurpose') }}</span>
                    <strong>{{ configuringRuntimeBinding?.route_purpose ?? configuringAgent.model_route_purpose }}</strong>
                  </div>
                  <div>
                    <span>{{ t('settings.effectiveRuntime') }}</span>
                    <strong>{{ runtimeSourceSummary(configuringRuntimeBinding) || t('settings.runtimeUnresolved') }}</strong>
                  </div>
                </div>

                <div v-if="configuringLegacyWarning" class="runtime-binding-warning">
                  <Info :size="16" />
                  <div>
                    <strong>{{ t('settings.sharedLegacyRoute', { purpose: configuringLegacyWarning.purpose }) }}</strong>
                    <span>{{ t('settings.sharedLegacyRouteHint', { count: configuringLegacyWarning.agent_ids.length }) }}</span>
                  </div>
                </div>

                <div class="runtime-source-grid">
                  <button :class="{ active: agentRuntimeSelection === 'inherit' }" @click="agentRuntimeSelection = 'inherit'">
                    <Workflow :size="16" />
                    <strong>{{ t('settings.runtimeInherit') }}</strong>
                    <span>{{ t('settings.runtimeInheritHint') }}</span>
                  </button>
                  <button :class="{ active: agentRuntimeSelection === 'fixed_model' }" @click="agentRuntimeSelection = 'fixed_model'">
                    <Cpu :size="16" />
                    <strong>{{ t('settings.runtimeFixedModel') }}</strong>
                    <span>{{ t('settings.runtimeFixedModelHint') }}</span>
                  </button>
                  <button :class="{ active: agentRuntimeSelection === 'provider_auto' }" @click="agentRuntimeSelection = 'provider_auto'">
                    <Server :size="16" />
                    <strong>{{ t('settings.runtimeProviderAuto') }}</strong>
                    <span>{{ t('settings.runtimeProviderAutoHint') }}</span>
                  </button>
                  <button :class="{ active: agentRuntimeSelection === 'cli' }" @click="agentRuntimeSelection = 'cli'">
                    <Terminal :size="16" />
                    <strong>CLI</strong>
                    <span>{{ t('settings.runtimeCliHint') }}</span>
                  </button>
                  <button :class="{ active: agentRuntimeSelection === 'acp' }" @click="agentRuntimeSelection = 'acp'">
                    <Bot :size="16" />
                    <strong>ACP</strong>
                    <span>{{ t('settings.runtimeAcpHint') }}</span>
                  </button>
                </div>

                <div v-if="agentRuntimeSelection === 'inherit'" class="runtime-source-current">
                  <ShieldCheck :size="16" />
                  <span>{{ t('settings.runtimeInheritedCurrent', { source: runtimeSourceSummary(configuringRuntimeBinding) || t('settings.runtimeUnresolved') }) }}</span>
                </div>
                <div v-else-if="agentRuntimeSelection === 'fixed_model'" class="settings-field runtime-source-picker">
                  <UiLabel>{{ t('settings.runtimeFixedModel') }}</UiLabel>
                  <div class="runtime-source-search">
                    <Search :size="14" />
                    <UiInput v-model="agentRuntimeModelQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: t('settings.centerModels') })" />
                  </div>
                  <select v-model="agentRuntimeModelKey" class="settings-select">
                    <option value="" disabled>{{ t('settings.selectModel') }}</option>
                    <option v-for="model in filteredRuntimeModels" :key="model.id" :value="modelOptionKey(model.provider_instance_id, model.model_id)">
                      {{ model.model_id }} · {{ model.provider_display_name ?? model.provider_instance_id }} · {{ statusLabel(model.status) }}
                    </option>
                  </select>
                  <p v-if="filteredRuntimeModels.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                </div>
                <div v-else-if="agentRuntimeSelection === 'provider_auto'" class="settings-field runtime-source-picker">
                  <UiLabel>{{ t('settings.routeProvider') }}</UiLabel>
                  <div class="runtime-source-search">
                    <Search :size="14" />
                    <UiInput v-model="agentRuntimeProviderQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: t('settings.centerApiConnections') })" />
                  </div>
                  <select v-model="agentRuntimeProviderId" class="settings-select">
                    <option value="" disabled>{{ t('settings.selectProvider') }}</option>
                    <option v-for="provider in filteredRuntimeProviders" :key="provider.provider_instance_id" :value="provider.provider_instance_id">
                      {{ provider.display_name }} · {{ statusLabel(provider.status) }}
                    </option>
                  </select>
                  <p v-if="filteredRuntimeProviders.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                  <p class="agent-config-hint">{{ t('settings.providerAutoOwnedByCore') }}</p>
                </div>
                <div v-else-if="agentRuntimeSelection === 'cli'" class="settings-field runtime-source-picker">
                  <UiLabel>CLI</UiLabel>
                  <div class="runtime-source-search">
                    <Search :size="14" />
                    <UiInput v-model="agentRuntimeCliQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: 'CLI' })" />
                  </div>
                  <select v-model="agentRuntimeCliId" class="settings-select">
                    <option value="" disabled>{{ t('settings.selectCliRuntime') }}</option>
                    <option v-for="runtime in filteredRuntimeCliOptions" :key="runtime.runtime_id" :value="runtime.runtime_id">
                      {{ runtime.display_name }} · {{ statusLabel(runtime.status) }}
                    </option>
                  </select>
                  <p v-if="filteredRuntimeCliOptions.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                </div>
                <div v-else class="settings-field runtime-source-picker">
                  <UiLabel>ACP</UiLabel>
                  <div class="runtime-source-search">
                    <Search :size="14" />
                    <UiInput v-model="agentRuntimeAcpQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: 'ACP' })" />
                  </div>
                  <select v-model="agentRuntimeAcpId" class="settings-select">
                    <option value="" disabled>{{ t('settings.selectAcpRuntime') }}</option>
                    <option v-for="runtime in filteredRuntimeAcpOptions" :key="runtime.runtime_id" :value="runtime.runtime_id">
                      {{ runtime.display_name }} · {{ acpRuntimeSourceLabel(runtime.source) }} · {{ statusLabel(runtime.status) }}
                    </option>
                  </select>
                  <p v-if="filteredRuntimeAcpOptions.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                </div>

                <div v-if="!runtimeBindingWritable" class="runtime-binding-readonly">
                  <Info :size="16" />
                  <div>
                    <strong>{{ t('settings.runtimeBindingPendingCore') }}</strong>
                    <span>{{ t('settings.runtimeBindingPendingCoreHint') }}</span>
                  </div>
                </div>
                <div v-if="agentRuntimeNotice" class="runtime-binding-warning">
                  <Info :size="16" />
                  <span>{{ agentRuntimeNotice }}</span>
                </div>
                <div class="modal-actions compact">
                  <UiButton :disabled="agentRuntimeBusy || !runtimeBindingWritable || !runtimeBindingInput()" size="sm" @click="saveAgentRuntimeBinding(configuringAgent)">
                    <Save :size="14" />
                    <span>{{ t('settings.saveRuntimeBinding') }}</span>
                  </UiButton>
                </div>
              </div>

              <!-- 描述 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentDescription') }}</div>
                <div class="settings-field">
                  <textarea
                    v-model="agentEditDescription"
                    class="settings-textarea"
                    rows="2"
                    :placeholder="t('settings.agentDescriptionPlaceholder')"
                  ></textarea>
                </div>
              </div>

              <!-- 工具绑定 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentTools') }}</div>
                <p class="agent-config-hint">{{ t('settings.agentToolsHint') }}</p>
                <div class="agent-tool-grid">
                  <button
                    v-for="tool in availableTools"
                    :key="tool.id"
                    class="agent-tool-chip"
                    :class="{
                      active: agentEditTools.includes(tool.id),
                      risky: tool.requires_approval
                    }"
                    @click="toggleAgentTool(tool.id)"
                  >
                    <span class="agent-tool-name">{{ tool.display_name }}</span>
                    <span class="agent-tool-risk">{{ tool.risk }}</span>
                  </button>
                </div>
              </div>

              <!-- 能力标签 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentCapabilities') }}</div>
                <p class="agent-config-hint">{{ t('settings.agentCapabilitiesHint') }}</p>
                <div class="agent-capability-list">
                  <span v-for="cap in agentEditCapabilities" :key="cap" class="agent-cap-tag">
                    {{ cap }}
                    <button class="agent-cap-remove" @click="removeAgentCapability(cap)">×</button>
                  </span>
                </div>
                <div class="agent-cap-add-row">
                  <UiInput v-model="agentNewCapability" :placeholder="t('settings.newCapabilityPlaceholder')" size="sm" @keydown.enter="addAgentCapability" />
                  <UiButton variant="outline" size="sm" :disabled="!agentNewCapability.trim()" @click="addAgentCapability">
                    <Plus :size="14" />
                    {{ t('settings.addCapability') }}
                  </UiButton>
                </div>
              </div>

              <!-- System Prompt -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentSystemPrompt') }}</div>
                <p class="agent-config-hint">{{ t('settings.agentSystemPromptHint') }} Saved as an agent-specific override fragment.</p>
                <div class="settings-field">
                  <textarea
                    v-model="agentEditSystemPrompt"
                    class="settings-textarea prompt-editor"
                    rows="6"
                    :placeholder="t('settings.agentSystemPromptPlaceholder')"
                  ></textarea>
                </div>
              </div>

              <!-- 保存按钮 -->
              <div class="agent-save-bar">
                <UiButton :disabled="busy" @click="saveAgentProfile">
                  <Save :size="14" />
                  <span>{{ t('settings.saveAgent') }}</span>
                </UiButton>
              </div>
            </template>
          </UiCard>

          <div class="model-section-header">
            <h3>{{ t('settings.evolutionCandidates') }}</h3>
            <UiBadge variant="outline">{{ agentCandidates.length }}</UiBadge>
          </div>

          <div class="agent-candidate-grid">
            <UiCard v-for="candidate in agentCandidates" :key="candidate.id" class="agent-candidate-card">
              <template #content>
                <div class="agent-candidate-head">
                  <div>
                    <strong>{{ candidate.name }}</strong>
                    <span>{{ agentLayerLabel(candidate.layer) }} · {{ agentTypeLabel(candidate.agent_type) }} · {{ candidate.status }}</span>
                  </div>
                  <UiBadge variant="secondary">{{ t('settings.generatedByEvolution') }}</UiBadge>
                </div>
                <p>{{ candidate.description }}</p>
                <div class="model-capability-row">
                  <span v-for="tool in candidate.suggested_tools" :key="tool">{{ tool }}</span>
                </div>
                <ul class="agent-eval-list">
                  <li v-for="note in candidate.evaluation_notes" :key="note">{{ note }}</li>
                </ul>
              </template>
            </UiCard>
          </div>
        </template>

        <template v-if="activeSection === 'agentEvolution'">
          <AgentEvolutionPanel />
        </template>

        <template v-if="activeSection === 'promptContext'">
          <div class="model-center-heading">
            <div>
              <h2>Prompt Context</h2>
              <p>Meeting Agent prompt fragments and preview</p>
            </div>
            <div class="agent-heading-actions">
              <UiButton variant="outline" size="sm" :disabled="loading" @click="loadPromptContextCenter">
                <Server :size="14" />
                <span>{{ t('settings.refresh') }}</span>
              </UiButton>
              <UiButton size="sm" @click="newPromptFragment">
                <Plus :size="14" />
                <span>New Fragment</span>
              </UiButton>
            </div>
          </div>

          <div class="model-form-grid">
            <div class="settings-field">
              <UiLabel>Scope</UiLabel>
              <select v-model="promptFilterScope" class="settings-select">
                <option value="all">All</option>
                <option value="global">Global</option>
                <option value="agent">Agent</option>
                <option value="mode">Mode</option>
                <option value="session">Session</option>
                <option value="project">Project</option>
              </select>
            </div>
            <div class="settings-field">
              <UiLabel>Category</UiLabel>
              <select v-model="promptFilterCategory" class="settings-select">
                <option value="all">All</option>
                <option v-for="category in promptCategories" :key="category" :value="category">{{ category }}</option>
              </select>
            </div>
            <div class="settings-field">
              <UiLabel>Target Agent</UiLabel>
              <select v-model="promptFilterAgentId" class="settings-select">
                <option value="all">All</option>
                <option value="">Global target</option>
                <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
              </select>
            </div>
            <div class="settings-field">
              <UiLabel>Status</UiLabel>
              <select v-model="promptFilterEnabled" class="settings-select">
                <option value="all">All</option>
                <option value="enabled">Enabled</option>
                <option value="disabled">Disabled</option>
              </select>
            </div>
          </div>

          <div class="model-section-header">
            <h3>Fragments</h3>
            <UiBadge variant="outline">{{ promptFilteredFragments.length }}</UiBadge>
          </div>

          <div class="agent-tool-grid">
            <button
              v-for="fragment in promptFilteredFragments"
              :key="fragment.id"
              class="agent-tool-chip"
              :class="{ active: promptSelectedFragmentId === fragment.id, risky: !fragment.enabled }"
              @click="selectPromptFragment(fragment)"
            >
              <span class="agent-tool-name">{{ fragment.title }}</span>
              <span class="agent-tool-risk">
                {{ fragment.scope }} / {{ fragment.category }} / {{ fragment.priority }}
                <template v-if="fragment.is_builtin"> / built-in</template>
              </span>
            </button>
          </div>

          <UiCard class="agent-detail-panel">
            <template #content>
              <div class="agent-detail-head">
                <div class="agent-card-icon">
                  <Bot :size="20" />
                </div>
                <div>
                  <h3>{{ promptForm.id ? promptForm.title : 'New Prompt Fragment' }}</h3>
                  <p>{{ promptForm.is_builtin ? 'Built-in read-only fragment' : 'Custom editable fragment' }}</p>
                </div>
                <UiBadge :variant="promptForm.enabled ? 'default' : 'secondary'">
                  {{ promptForm.enabled ? 'enabled' : 'disabled' }}
                </UiBadge>
              </div>

              <div class="agent-config-switch">
                <div>
                  <strong>Enabled</strong>
                  <span>{{ promptForm.is_builtin ? 'Clone to customize built-in content' : promptForm.id || 'custom fragment' }}</span>
                </div>
                <UiSwitch v-model="promptForm.enabled" :disabled="promptForm.is_builtin" />
              </div>

              <div class="model-form-grid">
                <div class="settings-field">
                  <UiLabel>Key</UiLabel>
                  <UiInput v-model="promptForm.key" :disabled="promptForm.is_builtin" />
                </div>
                <div class="settings-field">
                  <UiLabel>Title</UiLabel>
                  <UiInput v-model="promptForm.title" :disabled="promptForm.is_builtin" />
                </div>
                <div class="settings-field">
                  <UiLabel>Scope</UiLabel>
                  <select v-model="promptForm.scope" class="settings-select" :disabled="promptForm.is_builtin">
                    <option value="global">Global</option>
                    <option value="agent">Agent</option>
                    <option value="mode">Mode</option>
                    <option value="session">Session</option>
                    <option value="project">Project</option>
                  </select>
                </div>
                <div class="settings-field">
                  <UiLabel>Target</UiLabel>
                  <select v-if="promptForm.scope === 'agent'" v-model="promptForm.target_agent_id" class="settings-select" :disabled="promptForm.is_builtin">
                    <option value="">Any agent</option>
                    <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
                  </select>
                  <UiInput v-else v-model="promptForm.target_agent_id" :disabled="promptForm.is_builtin" placeholder="optional target id" />
                </div>
                <div class="settings-field">
                  <UiLabel>Category</UiLabel>
                  <UiInput v-model="promptForm.category" :disabled="promptForm.is_builtin" />
                </div>
                <div class="settings-field">
                  <UiLabel>Priority</UiLabel>
                  <UiInput v-model="promptForm.priority" type="number" :disabled="promptForm.is_builtin" />
                </div>
              </div>

              <div class="agent-config-section">
                <div class="agent-config-section-title">Content</div>
                <div class="settings-field">
                  <textarea
                    v-model="promptForm.content"
                    class="settings-textarea prompt-editor"
                    rows="7"
                    :disabled="promptForm.is_builtin"
                  ></textarea>
                </div>
              </div>

              <div class="agent-save-bar">
                <UiButton v-if="promptForm.is_builtin" :disabled="busy || !promptForm.id" @click="clonePromptFragment()">
                  <Plus :size="14" />
                  <span>Clone Custom</span>
                </UiButton>
                <UiButton v-else :disabled="busy || !promptForm.content.trim()" @click="savePromptFragment">
                  <Save :size="14" />
                  <span>{{ t('settings.save') }}</span>
                </UiButton>
                <UiButton v-if="!promptForm.is_builtin && promptForm.id" variant="ghost" :disabled="busy" @click="deletePromptFragment">
                  <Trash2 :size="14" />
                  <span>{{ t('settings.delete') }}</span>
                </UiButton>
              </div>
            </template>
          </UiCard>

          <UiCard class="agent-detail-panel">
            <template #content>
              <div class="agent-detail-head">
                <div class="agent-card-icon">
                  <FileText :size="20" />
                </div>
                <div>
                  <h3>Preview</h3>
                  <p>Final local system prompt</p>
                </div>
                <UiBadge v-if="promptPreview" variant="outline">{{ promptPreview.estimated_tokens }} tokens</UiBadge>
              </div>

              <div class="model-form-grid">
                <div class="settings-field">
                  <UiLabel>Agent</UiLabel>
                  <select v-model="promptPreviewAgentId" class="settings-select">
                    <option v-for="agent in agents" :key="agent.id" :value="agent.id">{{ agent.name }}</option>
                  </select>
                </div>
                <div class="settings-field">
                  <UiLabel>Mode</UiLabel>
                  <select v-model="promptPreviewMode" class="settings-select">
                    <option value="">Agent default</option>
                    <option v-for="mode in agentModes" :key="mode.id" :value="mode.id">{{ mode.display_name }}</option>
                  </select>
                </div>
                <div class="settings-field">
                  <UiLabel>Session ID</UiLabel>
                  <UiInput v-model="promptPreviewSessionId" placeholder="optional" />
                </div>
                <div class="settings-field">
                  <UiLabel>Run ID</UiLabel>
                  <UiInput v-model="promptPreviewRunId" placeholder="optional" />
                </div>
              </div>

              <div class="agent-config-section">
                <div class="agent-config-section-title">User content</div>
                <textarea
                  v-model="promptPreviewUserContent"
                  class="settings-textarea"
                  rows="3"
                  placeholder="optional preview text"
                ></textarea>
              </div>

              <div class="agent-save-bar">
                <UiButton :disabled="busy" @click="generatePromptPreview">
                  <FileText :size="14" />
                  <span>Generate Preview</span>
                </UiButton>
              </div>

              <template v-if="promptPreview">
                <div class="model-capability-row">
                  <span v-for="fragment in promptPreview.fragments" :key="fragment.id">{{ fragment.key }}</span>
                </div>
                <div v-if="promptPreview.context_pack_ids.length > 0" class="model-capability-row">
                  <span v-for="contextPackId in promptPreview.context_pack_ids" :key="contextPackId">{{ contextPackId }}</span>
                </div>
                <div v-if="promptPreview.warnings.length > 0" class="provider-status-note">
                  <ShieldCheck :size="14" />
                  <span>{{ promptPreview.warnings.join(' ') }}</span>
                </div>
                <div class="settings-field">
                  <textarea
                    :value="promptPreview.system_prompt"
                    class="settings-textarea prompt-editor"
                    rows="14"
                    readonly
                  ></textarea>
                </div>
              </template>
            </template>
          </UiCard>
        </template>

        <template v-if="activeSection === 'promptEngineering'">
          <PromptEngineeringPanel />
        </template>

        <template v-if="activeSection === 'tools'">
          <div class="model-center-heading">
            <div>
              <h2>{{ t('settings.toolLayerTitle') }}</h2>
              <p>{{ t('settings.toolLayerSubtitle') }}</p>
            </div>
            <UiButton variant="outline" size="sm" :disabled="loading" @click="loadAgentCenter">
              <Server :size="14" />
              <span>{{ t('settings.refresh') }}</span>
            </UiButton>
          </div>

          <div v-if="harnessManifest" class="provider-status-note harness-manifest-note">
            <ShieldCheck :size="14" />
            <span>{{ harnessManifest.runtime }} · {{ harnessManifest.ownership_model }}</span>
          </div>

          <section v-if="toolLayerReadiness" class="tool-layer-readiness-panel">
            <div class="model-readiness-head">
              <div>
                <h3>{{ t('settings.toolLayerReadiness') }}</h3>
                <span>{{ toolLayerReadiness.receipt_id }}</span>
              </div>
              <UiBadge :variant="readinessVariant(toolLayerReadiness.status)">
                <Circle :size="8" />
                {{ toolLayerReadiness.status }}
              </UiBadge>
            </div>
            <div class="model-readiness-metrics">
              <div>
                <strong>{{ toolLayerReadiness.tool_count }}</strong>
                <span>{{ t('settings.toolsCount') }}</span>
              </div>
              <div>
                <strong>{{ toolLayerReadiness.execution_agent_count }}</strong>
                <span>{{ t('settings.executionAgents') }}</span>
              </div>
              <div>
                <strong>{{ toolLayerReadiness.human_checkpoint_tool_count }}</strong>
                <span>{{ t('settings.humanCheckpoints') }}</span>
              </div>
              <div>
                <strong>{{ toolLayerReadiness.unresolved_scope_count }}</strong>
                <span>{{ t('settings.unresolvedScopes') }}</span>
              </div>
            </div>
            <div v-if="warningToolLayerTools.length > 0 || warningToolLayerAgents.length > 0" class="tool-layer-readiness-grid">
              <div
                v-for="tool in warningToolLayerTools.slice(0, 4)"
                :key="tool.tool_id"
                class="tool-layer-readiness-row"
              >
                <div>
                  <strong>{{ tool.display_name }}</strong>
                  <span>{{ tool.source }} · {{ tool.provider_layer }} · {{ tool.risk }}</span>
                </div>
                <UiBadge :variant="readinessVariant(tool.status)">{{ tool.status }}</UiBadge>
              </div>
              <div
                v-for="agent in warningToolLayerAgents.slice(0, 4)"
                :key="agent.agent_id"
                class="tool-layer-readiness-row"
              >
                <div>
                  <strong>{{ agent.agent_name }}</strong>
                  <span>{{ agent.dispatchable_tool_count }} tools · {{ agent.unresolved_scope_count }} unresolved</span>
                </div>
                <UiBadge :variant="readinessVariant(agent.status)">{{ agent.status }}</UiBadge>
              </div>
            </div>
          </section>

          <div v-if="harnessManifest?.tool_registry" class="model-section-header">
            <h3>{{ t('settings.toolRegistryGovernance') }}</h3>
            <UiBadge variant="outline">{{ harnessManifest.tool_registry.canonical_tool_count }}</UiBadge>
          </div>

          <div v-if="harnessManifest?.tool_registry" class="harness-registry-summary">
            <div class="harness-registry-metrics">
              <div>
                <span>{{ t('settings.declaredTools') }}</span>
                <strong>{{ harnessManifest.tool_registry.declared_tool_count }}</strong>
              </div>
              <div>
                <span>{{ t('settings.canonicalTools') }}</span>
                <strong>{{ harnessManifest.tool_registry.canonical_tool_count }}</strong>
              </div>
              <div>
                <span>{{ t('settings.duplicateToolIds') }}</span>
                <strong>{{ harnessManifest.tool_registry.duplicate_tool_id_count }}</strong>
              </div>
            </div>
            <p>{{ harnessManifest.tool_registry.selection_policy }}</p>
            <div class="model-capability-row compact">
              <span v-for="source in harnessManifest.tool_registry.source_precedence" :key="source">{{ source }}</span>
            </div>
            <div v-if="harnessManifest.tool_registry.duplicate_tool_ids.length > 0" class="model-capability-row compact">
              <span v-for="toolId in harnessManifest.tool_registry.duplicate_tool_ids" :key="toolId">{{ toolId }}</span>
            </div>
          </div>

          <div v-if="harnessManifest?.design_notes.length" class="model-section-header">
            <h3>{{ t('settings.harnessDesignNotes') }}</h3>
            <UiBadge variant="outline">{{ harnessManifest.design_notes.length }}</UiBadge>
          </div>

          <div v-if="harnessManifest?.design_notes.length" class="harness-design-notes">
            <span v-for="note in harnessManifest.design_notes" :key="note">{{ note }}</span>
          </div>

          <div v-if="manifestAgentLayers.length > 0" class="model-section-header">
            <h3>{{ t('settings.harnessAgentLayers') }}</h3>
            <UiBadge variant="outline">{{ manifestAgentLayers.length }}</UiBadge>
          </div>

          <div v-if="manifestAgentLayers.length > 0" class="harness-manifest-grid">
            <div v-for="layer in manifestAgentLayers" :key="layer.layer" class="harness-manifest-panel">
              <div class="harness-panel-head">
                <span class="harness-panel-title">{{ layer.layer }}</span>
                <UiBadge :variant="layer.approval_required ? 'secondary' : 'outline'">
                  {{ layer.enabled_agent_count }}/{{ layer.agent_count }}
                </UiBadge>
              </div>
              <p class="harness-panel-meta">{{ layer.role }}</p>
              <div class="harness-panel-stats">
                <span>{{ t('settings.maxParallel') }} {{ layer.max_parallel_executors }}</span>
                <span>{{ layer.worktree_isolation ? t('settings.worktreeIsolated') : t('settings.sharedWorkspace') }}</span>
              </div>
              <div class="model-capability-row compact">
                <span v-for="agentType in layer.agent_types" :key="agentType">{{ agentType }}</span>
              </div>
            </div>
          </div>

          <div v-if="manifestProviders.length > 0" class="model-section-header">
            <h3>{{ t('settings.toolProviders') }}</h3>
            <UiBadge variant="outline">{{ manifestProviders.length }}</UiBadge>
          </div>

          <div v-if="manifestProviders.length > 0" class="harness-manifest-grid">
            <div v-for="provider in manifestProviders" :key="provider.source" class="harness-manifest-panel">
              <div class="harness-panel-head">
                <span class="harness-panel-title">{{ provider.display_name }}</span>
                <UiBadge :variant="provider.status === 'active' ? 'secondary' : 'outline'">{{ provider.status }}</UiBadge>
              </div>
              <p class="harness-panel-meta">{{ provider.layer }} · {{ provider.source }}</p>
              <div class="harness-panel-stats">
                <span>{{ t('settings.toolsCount') }} {{ provider.tool_count }}</span>
                <span>{{ t('settings.approvalCount') }} {{ provider.approval_required_count }}</span>
                <span>{{ t('settings.futureCount') }} {{ provider.future_tool_count }}</span>
              </div>
              <div class="model-capability-row compact">
                <span v-for="prefix in provider.capability_prefixes" :key="prefix">{{ prefix }}</span>
              </div>
            </div>
          </div>

          <div v-if="manifestRiskPolicies.length > 0" class="model-section-header">
            <h3>{{ t('settings.riskPolicies') }}</h3>
            <UiBadge variant="outline">{{ manifestRiskPolicies.length }}</UiBadge>
          </div>

          <div v-if="manifestRiskPolicies.length > 0" class="agent-tool-grid manifest-risk-row">
            <button
              v-for="risk in manifestRiskPolicies"
              :key="risk.risk"
              class="agent-tool-chip"
              :class="{ risky: risk.requires_human_checkpoint }"
            >
              <span class="agent-tool-name">{{ risk.risk }}</span>
              <span class="agent-tool-risk">{{ risk.tool_count }} · {{ risk.policy_summary }}</span>
            </button>
          </div>

          <div class="model-section-header">
            <h3>{{ t('settings.toolDiscovery') }}</h3>
            <UiBadge variant="outline">{{ sortedToolDiscoveryResults.length }}</UiBadge>
          </div>

          <div class="tool-discovery-controls">
            <UiInput
              v-model="toolDiscoveryQuery"
              :placeholder="t('settings.toolDiscoveryPlaceholder')"
              @keyup.enter="loadToolDiscovery"
            />
            <select v-model="toolDiscoverySource" class="settings-select" @change="loadToolDiscovery">
              <option value="all">{{ t('settings.allSources') }}</option>
              <option v-for="source in toolSourceOptions" :key="source" :value="source">{{ source }}</option>
            </select>
            <select v-model="toolDiscoveryRisk" class="settings-select" @change="loadToolDiscovery">
              <option value="all">{{ t('settings.allRisks') }}</option>
              <option v-for="risk in toolRiskOptions" :key="risk" :value="risk">{{ risk }}</option>
            </select>
            <UiButton size="sm" :disabled="toolDiscoveryLoading" @click="loadToolDiscovery">
              <Search :size="14" />
              <span>{{ t('settings.search') }}</span>
            </UiButton>
          </div>

          <div class="tool-discovery-grid">
            <button
              v-for="result in sortedToolDiscoveryResults"
              :key="result.tool.id"
              class="tool-discovery-card"
              :class="{ risky: result.requires_human_checkpoint }"
            >
              <span class="tool-discovery-title">{{ result.tool.display_name }}</span>
              <span class="tool-discovery-meta">{{ result.tool.source }} · {{ result.provider_layer }} · {{ result.tool.risk }}</span>
              <span class="tool-discovery-meta">{{ result.approval_summary }}</span>
              <span class="tool-discovery-fields">
                {{ t('settings.matchedFields') }} {{ result.matched_fields.join(', ') }}
              </span>
            </button>
          </div>
          <p v-if="!toolDiscoveryLoading && sortedToolDiscoveryResults.length === 0" class="quiet">
            {{ t('settings.noToolSearchResults') }}
          </p>

          <div class="model-section-header">
            <h3>{{ t('settings.codeToolSuite') }}</h3>
            <UiBadge variant="secondary">{{ codeSuiteToolList.length }}</UiBadge>
          </div>

          <div v-if="supportedLanguages.length > 0" class="model-capability-row">
            <span v-for="language in supportedLanguages" :key="language">{{ language }}</span>
          </div>

          <div class="model-section-header">
            <h3>{{ t('settings.projectTemplates') }}</h3>
            <UiBadge variant="outline">{{ projectTemplates.length }}</UiBadge>
          </div>

          <div class="agent-tool-grid">
            <button
              v-for="template in projectTemplates"
              :key="template.id"
              class="agent-tool-chip"
            >
              <span class="agent-tool-name">{{ template.name }}</span>
              <span class="agent-tool-risk">{{ template.language }} · {{ template.package_manager }}</span>
            </button>
          </div>

          <div class="agent-tool-grid">
            <button
              v-for="tool in codeSuiteToolList"
              :key="tool.id"
              class="agent-tool-chip active"
              :class="{ risky: tool.requires_approval }"
            >
              <span class="agent-tool-name">{{ tool.display_name }}</span>
              <span class="agent-tool-risk">
                {{ tool.requires_approval ? t('settings.approvalRequired') : t('settings.readOnlyTool') }} · {{ tool.risk }}
              </span>
            </button>
          </div>
          <p v-if="codeSuiteToolList.length === 0" class="quiet">{{ t('settings.noTools') }}</p>

          <div class="model-section-header">
            <h3>{{ t('settings.codexPrimitiveTools') }}</h3>
            <UiBadge variant="outline">{{ codexPrimitiveTools.length }}</UiBadge>
          </div>

          <div class="agent-tool-grid">
            <button
              v-for="tool in codexPrimitiveTools"
              :key="tool.id"
              class="agent-tool-chip"
              :class="{ risky: tool.requires_approval }"
            >
              <span class="agent-tool-name">{{ tool.display_name }}</span>
              <span class="agent-tool-risk">{{ tool.source }} · {{ tool.risk }}</span>
            </button>
          </div>
        </template>

        <template v-if="activeSection === 'appearance'">
          <h2>{{ t('settings.appearance') }}</h2>

          <h3>{{ t('settings.theme') }}</h3>
          <div class="theme-options">
            <button
              :class="['theme-option', { active: theme === 'dark' }]"
              @click="changeTheme('dark')"
            >
              <Moon :size="18" />
              {{ t('settings.dark') }}
            </button>
            <button
              :class="['theme-option', { active: theme === 'light' }]"
              @click="changeTheme('light')"
            >
              <Sun :size="18" />
              {{ t('settings.light') }}
            </button>
            <button
              :class="['theme-option', { active: theme === 'system' }]"
              @click="changeTheme('system')"
            >
              <Monitor :size="18" />
              {{ t('settings.system') }}
            </button>
          </div>

          <h3>{{ t('settings.accentColor') }}</h3>
          <p class="accent-color-hint">{{ t('settings.accentColorHint') }}</p>
          <div class="accent-color-grid">
            <button
              v-for="color in accentColors"
              :key="color.key"
              :class="['accent-color-swatch', { active: accentColor === color.key }]"
              :style="{ '--swatch-color': color.dark.accentPrimary }"
              :title="t(color.labelKey)"
              @click="changeAccentColor(color.key)"
            >
              <span class="accent-color-dot"></span>
              <span class="accent-color-label">{{ t(color.labelKey) }}</span>
              <Check v-if="accentColor === color.key" :size="14" class="accent-color-check" />
            </button>
          </div>
          
          <!-- Global Material Effect Section -->
          <h3>{{ t('settings.globalMaterial') }}</h3>
          <p class="accent-color-hint">{{ t('settings.globalMaterialHint') }}</p>
          <div class="panel-styles-grid">
            <PanelStyleControl
              :label="t('settings.globalMaterial')"
              :settings="panelStyle"
              @update="updatePanelStyle($event)"
            />
          </div>
          <div class="panel-styles-actions">
            <UiButton variant="outline" size="sm" @click="resetPanelStyle">
              {{ t('settings.resetPanelStyles') }}
            </UiButton>
          </div>
          
          <!-- Background Settings Section -->
          <h2>{{ t('settings.background') }}</h2>
          
          <!-- Background Type Selection -->
          <h3>{{ t('settings.backgroundType') }}</h3>
          <div class="background-type-options">
            <button
              :class="['bg-type-option', { active: backgroundSettings.type === 'none' }]"
              @click="setBackgroundType('none')"
            >
              {{ t('settings.bgNone') }}
            </button>
            <button
              :class="['bg-type-option', { active: backgroundSettings.type === 'image' }]"
              @click="setBackgroundType('image')"
            >
              {{ t('settings.bgImage') }}
            </button>
            <button
              :class="['bg-type-option', { active: backgroundSettings.type === 'video' }]"
              @click="setBackgroundType('video')"
            >
              {{ t('settings.bgVideo') }}
            </button>
            <button
              :class="['bg-type-option', { active: backgroundSettings.type === 'html' }]"
              @click="setBackgroundType('html')"
            >
              {{ t('settings.bgHtml') }}
            </button>
          </div>
          
          <!-- File/URL Input (for image and video) -->
          <div v-if="backgroundSettings.type !== 'none'" class="background-source-section">
            <h3>{{ t('settings.backgroundSource') }}</h3>
            <div class="source-input-row">
              <UiInput
                v-model="backgroundSource"
                :placeholder="t('settings.bgSourcePlaceholder')"
                class="source-input"
              />
              <UiButton
                v-if="backgroundSettings.type === 'image' || backgroundSettings.type === 'video'"
                variant="outline"
                @click="selectBackgroundFile"
              >
                {{ t('settings.browse') }}
              </UiButton>
            </div>
            <p v-if="backgroundSettings.type === 'image'" class="source-hint">
              {{ t('settings.bgImageFormats') }}
            </p>
            <p v-else-if="backgroundSettings.type === 'video'" class="source-hint">
              {{ t('settings.bgVideoFormats') }}
            </p>
            <p v-else-if="backgroundSettings.type === 'html'" class="source-hint">
              {{ t('settings.bgHtmlHint') }}
            </p>
          </div>
          
          <!-- Background Parameters -->
          <div v-if="backgroundSettings.type !== 'none'" class="background-params-section">
            <h3>{{ t('settings.backgroundParams') }}</h3>
            
            <!-- Opacity -->
            <div class="param-row">
              <label class="param-label">{{ t('settings.opacity') }}</label>
              <input
                type="range"
                min="0"
                max="100"
                :value="backgroundSettings.opacity"
                class="param-slider"
                @input="setBackgroundOpacity(parseInt(($event.target as HTMLInputElement).value))"
              />
              <span class="param-value">{{ backgroundSettings.opacity }}%</span>
            </div>
            
            <!-- Blur -->
            <div class="param-row">
              <label class="param-label">{{ t('settings.blur') }}</label>
              <input
                type="range"
                min="0"
                max="20"
                :value="backgroundSettings.blur"
                class="param-slider"
                @input="setBackgroundBlur(parseInt(($event.target as HTMLInputElement).value))"
              />
              <span class="param-value">{{ backgroundSettings.blur }}px</span>
            </div>
            
            <!-- Size -->
            <div v-if="backgroundSettings.type === 'image'" class="param-row">
              <label class="param-label">{{ t('settings.bgSize') }}</label>
              <select
                :value="backgroundSettings.size"
                class="param-select"
                @change="setBackgroundSize(($event.target as HTMLSelectElement).value as any)"
              >
                <option value="cover">{{ t('settings.bgSizeCover') }}</option>
                <option value="contain">{{ t('settings.bgSizeContain') }}</option>
                <option value="auto">{{ t('settings.bgSizeAuto') }}</option>
              </select>
            </div>
            
            <!-- Position (for image) -->
            <div v-if="backgroundSettings.type === 'image'" class="param-row">
              <label class="param-label">{{ t('settings.bgPosition') }}</label>
              <select
                :value="backgroundSettings.position"
                class="param-select"
                @change="setBackgroundPosition(($event.target as HTMLSelectElement).value as any)"
              >
                <option value="center">{{ t('settings.bgPositionCenter') }}</option>
                <option value="top">{{ t('settings.bgPositionTop') }}</option>
                <option value="bottom">{{ t('settings.bgPositionBottom') }}</option>
                <option value="left">{{ t('settings.bgPositionLeft') }}</option>
                <option value="right">{{ t('settings.bgPositionRight') }}</option>
              </select>
            </div>
            
            <!-- Repeat (for image) -->
            <div v-if="backgroundSettings.type === 'image'" class="param-row">
              <label class="param-label">{{ t('settings.bgRepeat') }}</label>
              <select
                :value="backgroundSettings.repeat"
                class="param-select"
                @change="setBackgroundRepeat(($event.target as HTMLSelectElement).value as any)"
              >
                <option value="no-repeat">{{ t('settings.bgRepeatNoRepeat') }}</option>
                <option value="repeat">{{ t('settings.bgRepeatRepeat') }}</option>
                <option value="repeat-x">{{ t('settings.bgRepeatRepeatX') }}</option>
                <option value="repeat-y">{{ t('settings.bgRepeatRepeatY') }}</option>
              </select>
            </div>
          </div>
          
          <!-- Background Preview -->
          <div v-if="backgroundSettings.type !== 'none'" class="background-preview-section">
            <h3>{{ t('settings.preview') }}</h3>
            <BackgroundPreview :settings="backgroundSettings" :height="150" />
          </div>
          
          <!-- Reset Button -->
          <div class="background-actions">
            <UiButton variant="outline" size="sm" @click="resetBackground">
              {{ t('settings.resetBackground') }}
            </UiButton>
          </div>
          
          <!-- Performance Warning -->
          <div v-if="backgroundSettings.type !== 'none'" class="performance-warning">
            <Info :size="14" />
            <span>{{ t('settings.bgPerformanceWarning') }}</span>
          </div>
        </template>

        <template v-if="activeSection === 'language'">
          <h2>{{ t('settings.language') }}</h2>
          <div class="lang-options">
            <UiButton
              variant="outline"
              :class="['lang-option', { active: locale === 'zh-CN' }]"
              @click="setLocale('zh-CN')"
            >
              中文
            </UiButton>
            <UiButton
              variant="outline"
              :class="['lang-option', { active: locale === 'en' }]"
              @click="setLocale('en')"
            >
              English
            </UiButton>
          </div>
        </template>

        <template v-if="activeSection === 'apiDocs'">
          <h2>{{ t('settings.apiDocs') }}</h2>
          <iframe class="api-docs-frame" :src="api.gatewayUrl + '/docs'" />
        </template>

        <template v-if="activeSection === 'about'">
          <h2>{{ t('settings.about') }}</h2>

          <!-- App identity -->
          <div class="about-brand">
            <div class="about-brand-icon">
              <BrandLogo :size="28" />
            </div>
            <div class="about-brand-text">
              <span class="about-brand-name">TinadecOffice</span>
              <span class="about-brand-ver">v0.1.0</span>
            </div>
          </div>

          <!-- Runtime status -->
          <div class="about-status-grid">
            <div class="about-status-card">
              <div class="about-status-row">
                <span class="about-status-label">Core (.NET)</span>
                <span class="about-status-dot" :class="aboutCoreStatus === 'ok' ? 'ok' : 'off'" />
                <span class="about-status-text" :class="aboutCoreStatus === 'ok' ? 'ok' : 'off'">
                  {{ aboutCoreStatus === 'ok' ? t('aboutPage.running') : t('aboutPage.unreachable') }}
                </span>
              </div>
              <div v-if="aboutCoreVersion" class="about-status-detail">{{ aboutCoreVersion }}</div>
            </div>
            <div class="about-status-card">
              <div class="about-status-row">
                <span class="about-status-label">Gateway</span>
                <span class="about-status-dot" :class="aboutGatewayStatus === 'ok' ? 'ok' : 'off'" />
                <span class="about-status-text" :class="aboutGatewayStatus === 'ok' ? 'ok' : 'off'">
                  {{ aboutGatewayStatus === 'ok' ? t('aboutPage.running') : t('aboutPage.unreachable') }}
                </span>
              </div>
            </div>
          </div>

          <!-- Component versions -->
          <UiCard class="about-section">
            <div class="about-row">
              <span>{{ t('settings.versionDesktop') }}</span>
              <span>0.1.0</span>
            </div>
            <div class="about-row">
              <span>{{ t('settings.versionCode') }}</span>
              <span>0.1.0</span>
            </div>
            <div class="about-row">
              <span>{{ t('settings.versionCore') }}</span>
              <span>0.1.0</span>
            </div>
          </UiCard>

          <!-- Architecture -->
          <div class="about-arch">
            <h3>{{ t('aboutPage.architecture') }}</h3>
            <p class="about-decouple-hint">{{ t('settings.decoupleHint') }}</p>
            <div class="about-layers">
              <div class="about-layer">
                <div class="about-layer-header">
                  <Monitor :size="14" />
                  <span>Desktop</span>
                </div>
                <div class="about-layer-tech">Electron + Vue 3 + Tailwind</div>
                <div class="about-layer-port">:5173</div>
              </div>
              <div class="about-layer-arrow">
                <ChevronRight :size="14" />
              </div>
              <div class="about-layer">
                <div class="about-layer-header">
                  <Globe :size="14" />
                  <span>Gateway</span>
                </div>
                <div class="about-layer-tech">Elysia + Node.js</div>
                <div class="about-layer-port">:48730</div>
              </div>
              <div class="about-layer-arrow">
                <ChevronRight :size="14" />
              </div>
              <div class="about-layer about-layer--core">
                <div class="about-layer-header">
                  <Cpu :size="14" />
                  <span>Core</span>
                </div>
                <div class="about-layer-tech">.NET 10 + SQLite</div>
                <div class="about-layer-port">:48731</div>
              </div>
            </div>
          </div>

          <!-- Links -->
          <div class="about-links">
            <UiButton variant="outline" size="sm" class="about-link-btn" @click="openExternal('https://github.com/apanzinc/TinadecCode')">
              <Globe :size="14" />
              <span>GitHub</span>
            </UiButton>
            <UiButton variant="outline" size="sm" class="about-link-btn" @click="openExternal('http://127.0.0.1:48730/docs')">
              <FileText :size="14" />
              <span>{{ t('settings.apiDocs') }}</span>
            </UiButton>
          </div>

          <p class="about-license">&copy; {{ new Date().getFullYear() }} TinadecOffice &middot; GPL-3.0-or-later</p>
        </template>
        </div>
        </Transition>
      </div>
    </div>

    <Transition name="modal-fade">
    <div v-if="showModal" class="model-provider-modal" @click.self="closeModal">
      <UiCard class="model-provider-modal-content">
        <template #header>
          <div class="modal-header-row">
            <div class="modal-header-left">
              <span class="modal-provider-logo" :style="{ background: brandBg(providerForm.driver), borderColor: brandColor(providerForm.driver) + '40' }" v-html="currentTemplate?.icon ?? ''"></span>
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
  </div>
</template>
