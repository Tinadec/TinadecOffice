<script setup lang="ts">
import {
  ArrowLeft,
  ExternalLink,
  Bot,
  Check,
  ChevronRight,
  Circle,
  Cpu,
  Database,
  Download,
  Dna,
  Edit3,
  FileText,
  FolderOpen,
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
  PanelRight,
  PawPrint,
  Plus,
  RefreshCw,
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
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import AboutSection from '@/settings/sections/AboutSection.vue'
import GeneralSection from '@/settings/sections/GeneralSection.vue'
import LanguageSection from '@/settings/sections/LanguageSection.vue'
import ApiDocsSection from '@/settings/sections/ApiDocsSection.vue'
import AppearanceSection from '@/settings/sections/AppearanceSection.vue'
import PetsSection from '@/settings/sections/PetsSection.vue'
import ToolCenterSection from '@/settings/sections/ToolCenterSection.vue'
import {
  api,
  type AgentCandidateDto,
  type AgentCenterOverviewDto,
  type AgentDefinitionDto,
  type AgentModeDto,
  type AgentProfileDto,
  type AgentRuntimeSelectionKind,
  type CenterDiagnosticDto,
  type CliDiscoveryCandidateDto,
  type AcpAdapterDto,
  type ModelCatalogReadinessReceiptDto,
  type ModelCenterAcpRuntimeDto,
  type ModelCenterOverviewDto,
  type ModelProviderReadinessDto,
  type ModelProviderInstanceDto,
  type ModelProviderTemplateDto,
  type ModelReadinessReceiptDto,
  type ModelRouteDto,
  type ModelCenterApiConnectionDto,
  type SaveModelProviderInstanceInput,
  type HarnessManifestDto,
  type ToolLayerReadinessReceiptDto,
  type ToolDescriptorDto,
  type ToolSearchResultDto,
  type AgentModeTopologyDto
} from '../api'
import {
  PROVIDER_CATEGORIES,
  PROVIDER_TEMPLATES,
  findTemplate,
  templateProtocol,
  templateProtocols,
  type ChatProtocol,
  type ProviderCategory,
  type ProviderTemplate
} from '../providerTemplates'
import {
  buildModelCenterRows,
  filterModelCenterRows,
  type ModelCenterFilter
} from '../modelCenterView'
import {
  aggregateModelCenterOverview,
  bindingFromModelStrategy,
  legacyRouteWarning,
  modelOptionKey,
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
import PetPreview from '@/components/PetPreview.vue'
import { UiButton, UiInput, UiCard, UiBadge, UiLabel, UiSkeleton, UiSwitch, UiDropdownMenu } from '@/components/ui'
import AgentTopologyCanvas from '@/components/AgentTopologyCanvas.vue'
import AgentEvolutionPanel from '@/components/AgentEvolutionPanel.vue'
import PromptContextPanel from '@/settings/sections/PromptContextPanel.vue'
import PromptEngineeringPanel from '@/components/PromptEngineeringPanel.vue'
import PanelStyleControl from '@/components/ui/panel-style-control.vue'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useNotifications } from '@/composables/useNotifications'

type SettingsSection = 'general' | 'model' | 'agentCenter' | 'tools' | 'appearance' | 'pets' | 'language' | 'apiDocs' | 'about'

type AgentCenterTab = 'config' | 'promptContext' | 'promptEngineering' | 'evolution'

interface ProviderForm {
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

const { t } = useI18n()
const router = useRouter()
const { items: notificationItems, notify, banner, confirm, dismiss: dismissNotification, status, dismissByKey } = useNotifications()

// Background management moved to settings/sections/AppearanceSection.vue (D7.2)

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

// Extend the material token scope to sibling UI such as window controls
// and provider dialogs. Root blur/background styles stay on the two panels.
const settingsPageDataAttrs = computed(() => getPanelDataAttributes())
const settingsPageMaterialStyle = computed(() => {
  const materialStyle = getPanelStyle()
  return {
    '--material-filter-section': materialStyle['--material-filter-section'] ?? 'none',
    '--material-filter-raised': materialStyle['--material-filter-raised'] ?? 'none',
  }
})

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

const activeSection = ref<SettingsSection>('general')
const agentCenterTab = ref<AgentCenterTab>('config')
function openFullWorkbench() {
  router.push('/agent-center')
}
// Pets section moved to settings/sections/PetsSection.vue (D7.2)

function selectSettingsSection(section: SettingsSection) {
  activeSection.value = section
}

// Spatial exit animation — declarative, class-driven.
const settingsExiting = ref(false)
const SETTINGS_EXIT_DURATION_MS = 530

onBeforeRouteLeave((_to, _from, next) => {
  if (settingsExiting.value) {
    next()
    return
  }
  settingsExiting.value = true
  setTimeout(() => next(), SETTINGS_EXIT_DURATION_MS)
})

// ---- About section moved to settings/sections/AboutSection.vue (D7.2) ----
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
const projectTemplates = ref<ProjectTemplateSummary[]>([])
const selectedProviderId = ref('')
const selectedAgentId = ref('')
const configuringAgentId = ref('')
const modelCenterSection = ref<ModelCenterSection>('api')
const agentRuntimeSelection = ref<AgentRuntimeSelectionKind>('inherit')
const agentRuntimeModelKey = ref('')
const agentRuntimeCliId = ref('')
const agentRuntimeAcpId = ref('')
const agentRuntimeModelQuery = ref('')
const agentRuntimeCliQuery = ref('')
const agentRuntimeAcpQuery = ref('')
const agentEditTools = ref<string[]>([])
const agentEditCapabilities = ref<string[]>([])
const agentEditSystemPrompt = ref('')
const agentEditDescription = ref('')
const agentEditRevision = ref<number | null>(null)
const agentNewCapability = ref('')
const agentToolQuery = ref('')
const agentToolSourceFilter = ref('all')
const agentToolRiskFilter = ref('all')
const agentCloneBusy = ref(false)
const selectedProviderDetailId = ref('')
const modelProviderFilter = ref<ModelCenterFilter>('all')
const modelProviderQuery = ref('')
const modelProviderListRef = ref<HTMLElement | null>(null)
const modelDiagnosticsRef = ref<HTMLDetailsElement | null>(null)
const busy = ref(false)
const loading = ref(false)
const modelCenterLoading = ref(false)
const agentCenterLoading = ref(false)
const modelCenterBusy = ref(false)
const agentRuntimeBusy = ref(false)

const showModal = ref(false)
const showTemplatePicker = ref(false)
const templatePickerQuery = ref('')
const agentViewMode = ref<'topology' | 'list'>('list')
const toolDiscoveryQuery = ref('')
const toolDiscoverySource = ref('all')
const toolDiscoveryRisk = ref('all')
const toolDiscoveryLoading = ref(false)
// promptForm/promptFragments state moved to settings/sections/PromptContextPanel.vue (D7.3)

const providerForm = reactive<ProviderForm>({
  id: '',
  driver: 'openai-compatible',
  display_name: 'OpenAI Compatible',
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
  enabled: true
})

const navItems = computed(() => [
  { key: 'general' as const, icon: Settings2, label: t('settings.general') },
  { key: 'model' as const, icon: KeyRound, label: t('settings.model') },
  { key: 'agentCenter' as const, icon: Workflow, label: t('settings.agentCenter') },
  { key: 'tools' as const, icon: Terminal, label: t('settings.toolLayer') },
  { key: 'appearance' as const, icon: Palette, label: t('settings.appearance') },
  { key: 'pets' as const, icon: PawPrint, label: t('settings.pets') },
  { key: 'language' as const, icon: Globe, label: t('settings.language') },
  { key: 'apiDocs' as const, icon: FileText, label: t('settings.apiDocs') },
  { key: 'about' as const, icon: Info, label: t('settings.about') },
])

// Gateway/dispatch config moved to settings/sections/GeneralSection.vue (D7.2)
const modelCenterSections = computed(() => [
  { key: 'api' as const, label: t('settings.centerSuppliers'), count: modelCenterOverview.value?.api_connections.length ?? 0 },
  { key: 'models' as const, label: t('settings.centerModels'), count: modelCenterOverview.value?.models.length ?? 0 },
  { key: 'cli' as const, label: 'CLI', count: modelCenterOverview.value?.cli_runtimes.length ?? 0 },
  { key: 'acp' as const, label: 'ACP', count: modelCenterOverview.value?.acp_runtimes.length ?? 0 }
])
const currentTemplate = computed(() => findTemplate(providerForm.driver))

const pickerTemplates = computed(() => PROVIDER_TEMPLATES)
const filteredPickerTemplates = computed(() => {
  const query = templatePickerQuery.value.trim().toLocaleLowerCase()
  return pickerTemplates.value.filter((template) => !query || [
    t(template.display_name_key),
    template.driver,
    template.connection_kind,
    template.default_model ?? ''
  ].some((value) => value.toLocaleLowerCase().includes(query)))
})
const pickerTemplateGroups = computed(() => {
  const groups: { category: ProviderCategory; labelKey: string; templates: ProviderTemplate[] }[] = []
  for (const category of PROVIDER_CATEGORIES) {
    const templates = filteredPickerTemplates.value.filter((template) => template.category === category.key)
    if (templates.length > 0) groups.push({ category: category.key, labelKey: category.labelKey, templates })
  }
  return groups
})

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

const formFields = computed(() => {
  const fields = currentTemplate.value?.fields ?? {
    base_url: true, model: true, api_key: true,
    binary_path: false, home_path: false, server_url: false, launch_args: false
  }
  return { ...fields, model: false }
})
const formPlaceholders = computed(() => currentTemplate.value?.placeholders ?? {})
const formProtocolOptions = computed<ChatProtocol[]>(() =>
  currentTemplate.value ? templateProtocols(currentTemplate.value) : []
)
const protocolLabelKeys: Record<ChatProtocol, string> = {
  'openai-chat': 'settings.protocolOpenaiChat',
  'openai-responses': 'settings.protocolOpenaiResponses',
  'anthropic-messages': 'settings.protocolAnthropicMessages'
}

const modelCenterRows = computed(() => buildModelCenterRows(
  providersFromOverview(modelCenterOverview.value).filter((provider) => provider.connection_kind !== 'cli'),
  PROVIDER_TEMPLATES,
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
  Object.fromEntries(agents.value.map((agent) => [agent.id, bindingFromModelStrategy(agent)]))
)
const topologyAgentLabels = computed(() => Object.fromEntries(
  agents.value.map((agent) => [agent.id, agentTypeLabel(agent.agent_type)])
))
const topologyCandidateLabels = computed(() => Object.fromEntries(
  agentCandidates.value.map((candidate) => [candidate.id, agentTypeLabel(candidate.agent_type)])
))
const configuringRuntimeBinding = computed(() =>
  agents.value.find((agent) => agent.id === configuringAgentId.value)
    ? bindingFromModelStrategy(agents.value.find((agent) => agent.id === configuringAgentId.value)!)
    : null
)
const configuringLegacyWarning = computed(() => legacyRouteWarning(configuringRuntimeBinding.value))
const runtimeModels = computed(() => agentCenterOverview.value?.runtime_sources.models ?? modelCenterOverview.value?.models ?? [])
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
const selectedProviderDetail = computed(() =>
  providers.value.find((provider) => provider.id === selectedProviderDetailId.value) ?? providers.value[0] ?? null
)
const selectedAgent = computed(() =>
  agents.value.find((agent) => agent.id === selectedAgentId.value) ?? null
)
const configuringAgent = computed(() =>
  agents.value.find((agent) => agent.id === configuringAgentId.value) ?? null
)
function normalizeAgentLayer(layer: unknown): 'operation' | 'execution' {
  const v = String(layer ?? '').trim().toLowerCase();
  if (v === 'planning') return 'operation';
  return v === 'execution' ? 'execution' : (v as 'operation' | 'execution');
}
const planningAgents = computed(() => agents.value.filter((agent) => normalizeAgentLayer(agent.layer) === 'operation'))
const executionAgents = computed(() => agents.value.filter((agent) => normalizeAgentLayer(agent.layer) === 'execution'))
const configuredAgentMode = computed(() => agentModes.value.find((mode) => mode.id === configuringAgent.value?.mode) ?? null)
const manifestToolList = computed(() => manifestTools(harnessManifest.value, availableTools.value))
// ponytail: agent tool panel filters — reuse manifestTools, no new deps
const filteredAgentTools = computed(() => {
  const q = agentToolQuery.value.trim().toLowerCase()
  return manifestToolList.value.filter((tool) => {
    if (agentToolSourceFilter.value !== 'all' && tool.source !== agentToolSourceFilter.value) return false
    if (agentToolRiskFilter.value !== 'all' && tool.risk !== agentToolRiskFilter.value) return false
    if (!q) return true
    return [tool.id, tool.display_name, tool.domain, tool.source, tool.risk].some((v) => v?.toLowerCase().includes(q))
  })
})
const groupedAgentTools = computed(() => {
  const groups = new Map<string, typeof manifestToolList.value>()
  for (const tool of filteredAgentTools.value) {
    const key = tool.source || 'unknown'
    const list = groups.get(key) ?? []
    list.push(tool)
    groups.set(key, list)
  }
  return [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0]))
})
const agentToolSelectionSummary = computed(() => {
  const total = manifestToolList.value.length
  const selected = agentEditTools.value.length
  const approval = manifestToolList.value.filter((t) => t.requires_approval && agentEditTools.value.includes(t.id)).length
  return { total, selected, approval }
})
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
  if (source === 'provider_models') return t('settings.modelSourceProviderModels')
  if (source === 'route_override') return t('settings.modelSourceRouteOverride')
  return source
}

function acpRuntimeSourceLabel(source: string) {
  return source === 'legacy_provider' ? t('settings.legacyProvider') : t('settings.acpAdapter')
}

function modelCatalogModeLabel(mode?: string) {
  return mode === 'configured_only' ? t('settings.configuredOnly') : mode ?? t('settings.configuredOnly')
}


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
  providerForm.display_name = t(template.display_name_key)
  providerForm.connection_kind = template.connection_kind
  providerForm.protocol = templateProtocol(template) ?? ''
  providerForm.base_url = template.default_base_url ?? ''
  providerForm.model = ''
  providerForm.models = []
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
    applyTemplateDefaults(PROVIDER_TEMPLATES[0])
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
  if (selectedProviderDetailId.value) {
    selectedProviderId.value = providerId
  }
}

function focusModelProviderList(filter: ModelCenterFilter) {
  modelCenterSection.value = 'api'
  modelProviderFilter.value = filter
  nextTick(() => {
    modelProviderListRef.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
    modelProviderListRef.value?.querySelector<HTMLInputElement>('input')?.focus()
  })
}

function handleAddProviderClick() {
  showTemplatePicker.value = true
}

function pickTemplate(template: ProviderTemplate) {
  showTemplatePicker.value = false
  openAddModal(template)
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
    notify.success(`${provider.display_name}: ${provider.enabled ? t('settings.disable') : t('settings.enable')}`)
  } catch (error) {
    notify.error(error, { title: provider.display_name })
  } finally {
    modelCenterBusy.value = false
  }
}

async function deleteProvider(providerId: string) {
  const provider = providers.value.find((item) => item.id === providerId)
  if (!await confirm({
    title: t('settings.delete'),
    message: `${t('settings.confirmDeleteProvider')}\n${provider?.display_name ?? providerId} (${providerId})`,
    confirmLabel: t('settings.confirmDelete'),
    cancelLabel: t('settings.cancel'),
    destructive: true
  })) return
  modelCenterBusy.value = true
  try {
    await api.deleteModelProvider(providerId)
    if (selectedProviderDetailId.value === providerId) {
      selectedProviderDetailId.value = ''
    }
    await Promise.all([loadModelCenter(), loadAgentCenter()])
    notify.success(`${provider?.display_name ?? providerId}: ${t('settings.delete')}`)
  } catch (error) {
    notify.error(error, { title: provider?.display_name ?? providerId })
  } finally {
    modelCenterBusy.value = false
  }
}

function closeModal() {
  showModal.value = false
}

async function loadModelCenter() {
  modelCenterLoading.value = true
  dismissByKey('model-center')
  try {
    // model-center/overview BFF was deleted; derive the same projection from versioned APIs.
    const [providerRows, templates, routes, acpAdapters, modelReadinessReceipt, catalogReadinessReceipt] = await Promise.all([
      api.listModelProviders().catch(() => [] as ModelProviderInstanceDto[]),
      api.listModelProviderTemplates().catch(() => [] as ModelProviderTemplateDto[]),
      api.listModelRoutes().catch(() => [] as ModelRouteDto[]),
      api.listAcpAdapters().catch(() => [] as AcpAdapterDto[]),
      api.getModelReadiness().catch(() => null),
      api.getModelCatalogReadiness().catch(() => null)
    ])
    const overview = aggregateModelCenterOverview({
      providers: providerRows,
      templates,
      routes,
      acp_adapters: acpAdapters,
      model_readiness: modelReadinessReceipt,
      catalog_readiness: catalogReadinessReceipt
    })
    modelCenterOverview.value = overview
    const instances = providersFromOverview(overview)
    providers.value = instances
    modelReadiness.value = overview.readiness.model ?? null
    modelCatalogReadiness.value = overview.readiness.catalog ?? null

    const selected = instances.find((provider) => provider.id === selectedProviderId.value) ?? instances[0]
    if (selected) {
      selectedProviderId.value = selected.id
    }
  } catch (error) {
    status.error({ key: 'model-center', source: 'models', message: error instanceof Error ? error.message : t('settings.centerLoadFailed'), action: { label: t('settings.retry'), run: loadModelCenter } })
  } finally {
    modelCenterLoading.value = false
  }
}

const showModelModal = ref(false)
const modelModalProviderId = ref('')
const modelModalManual = ref('')
const modelModalPending = ref<string[]>([])
const modelModalDiscovered = ref<Array<{ id: string; display_name: string }>>([])
const modelModalBusy = ref(false)
const modelModalError = ref('')
const modelModalFetched = ref(false)

function modelApiProvider(providerId: string) {
  return modelCenterOverview.value?.api_connections.find((item) => item.id === providerId) ?? null
}

function modelsForProvider(providerId: string) {
  return (modelCenterOverview.value?.models ?? []).filter((model) => model.provider_instance_id === providerId)
}

function openAddModelModal(providerId: string) {
  modelModalProviderId.value = providerId
  modelModalManual.value = ''
  modelModalPending.value = []
  modelModalDiscovered.value = []
  modelModalError.value = ''
  modelModalFetched.value = false
  showModelModal.value = true
}

async function fetchDiscoveredModels() {
  modelModalBusy.value = true
  modelModalError.value = ''
  try {
    const result = await api.refreshProviderModels(modelModalProviderId.value)
    modelModalDiscovered.value = result.models
    modelModalFetched.value = true
    // Auto-pend models the provider does not persist yet; the user confirms via 保存.
    const existing = new Set(modelApiProvider(modelModalProviderId.value)?.models ?? [])
    const fresh = result.models.map((model) => model.id).filter((id) => id && !existing.has(id))
    for (const id of fresh) addPendingModel(id)
    if (fresh.length > 0) notify.success(t('settings.discoveredNewModels', { count: fresh.length }))
  } catch (error) {
    modelModalError.value = error instanceof Error ? error.message : String(error)
  } finally {
    modelModalBusy.value = false
  }
}

function addPendingModel(id: string) {
  if (!modelModalPending.value.includes(id)) modelModalPending.value.push(id)
}

function addManualModel() {
  const id = modelModalManual.value.trim()
  if (!id) return
  addPendingModel(id)
  modelModalManual.value = ''
}

async function saveModelModal() {
  const provider = modelApiProvider(modelModalProviderId.value)
  if (!provider) return
  const merged = [...new Set([...(provider.models ?? []), ...modelModalPending.value])]
  await putProviderModels(provider, merged, merged[0] ?? null)
  showModelModal.value = false
}

async function removeModel(providerId: string, modelId: string) {
  const provider = modelApiProvider(providerId)
  if (!provider) return
  if (!await confirm({
    title: t('settings.removeModel'),
    message: `${t('settings.confirmRemoveModel')}\n${modelId} (${provider.display_name})`,
    confirmLabel: t('settings.confirmDelete'),
    cancelLabel: t('settings.cancel'),
    destructive: true
  })) return
  modelCenterBusy.value = true
  try {
    // Clear every source that contributes the model so it stays gone after reload.
    const routes = await api.listModelRoutes()
    for (const route of routes) {
      if (route.model === modelId) {
        await api.saveModelRoute(route.purpose, route.provider_instance_id, null)
      }
    }
    const merged = (provider.models ?? []).filter((id) => id !== modelId)
    const nextDefault = provider.model === modelId ? merged[0] ?? null : (provider.model ?? null)
    await putProviderModels(provider, merged, nextDefault)
    await loadAgentCenter()
  } catch (error) {
    notify.error(error, { title: modelId })
  } finally {
    modelCenterBusy.value = false
  }
}

async function putProviderModels(
  provider: ModelCenterApiConnectionDto,
  models: string[],
  model: string | null
) {
  modelCenterBusy.value = true
  try {
    const payload: SaveModelProviderInstanceInput = {
      id: provider.id,
      driver: provider.driver,
      display_name: provider.display_name,
      connection_kind: provider.connection_kind,
      base_url: provider.base_url ?? null,
      model,
      models,
      server_url: provider.server_url ?? null,
      capabilities: provider.capabilities,
      enabled: provider.enabled
    }
    await api.saveModelProvider(provider.id, payload)
    await loadModelCenter()
    notify.success(t('settings.refreshModels'))
  } catch (error) {
    notify.error(error, { title: provider.display_name })
  } finally {
    modelCenterBusy.value = false
  }
}

async function refreshProviderModels(providerInstanceId: string) {
  modelCenterBusy.value = true
  try {
    const result = await api.refreshProviderModels(providerInstanceId)
    const provider = modelApiProvider(providerInstanceId)
    const existing = new Set(provider?.models ?? [])
    const fresh = result.models.map((model) => model.id).filter((id) => id && !existing.has(id))
    if (!provider || fresh.length === 0) {
      notify.info(t('settings.noNewModels'))
      return
    }
    const confirmed = await confirm({
      title: t('settings.mergeDiscoveredTitle'),
      message: `${t('settings.confirmMergeDiscovered', { count: fresh.length })}\n${fresh.join('\n')}`,
      confirmLabel: t('settings.confirmSave'),
      cancelLabel: t('settings.cancel')
    })
    if (!confirmed) return
    const merged = [...new Set([...(provider.models ?? []), ...fresh])]
    await putProviderModels(provider, merged, provider.model ?? merged[0] ?? null)
  } catch (error) {
    notify.error(error, { title: t('settings.modelDiscoveryUnsupported') })
  } finally {
    modelCenterBusy.value = false
  }
}

async function probeAcpRuntime(runtime: ModelCenterAcpRuntimeDto) {
  if (!runtime.adapter_id) return
  modelCenterBusy.value = true
  try {
    await api.probeAcpAdapter(runtime.adapter_id)
    await Promise.all([loadModelCenter(), loadAgentCenter()])
    notify.success(runtime.display_name)
  } catch (error) {
    notify.error(error, { title: t('settings.acpProbeFailed') })
  } finally {
    modelCenterBusy.value = false
  }
}

const cliDiscoveryCandidates = ref<CliDiscoveryCandidateDto[]>([])
const cliDiscoveryLoading = ref(false)
const cliDiscoveryLoaded = ref(false)

async function discoverCliRuntimes() {
  cliDiscoveryLoading.value = true
  try {
    const res = await api.discoverCliRuntimes()
    cliDiscoveryCandidates.value = res.cli_runtimes ?? []
    cliDiscoveryLoaded.value = true
  } catch (error) {
    notify.error(error, { title: t('settings.cliDiscoveryError') })
  } finally {
    cliDiscoveryLoading.value = false
  }
}

async function connectDiscoveredCli(candidate: CliDiscoveryCandidateDto) {
  if (!candidate.binary_path) return
  cliDiscoveryLoading.value = true
  try {
    const created = await api.connectCliRuntime({
      driver: candidate.driver,
      binary_path: candidate.binary_path,
      display_name: candidate.display_name || undefined,
      home_path: candidate.home_path ?? null,
      server_url: candidate.server_url ?? null,
      launch_args: candidate.launch_args ?? null,
    })
    await loadModelCenter()
    notify.success(created.display_name)
  } catch (error) {
    notify.error(error, { title: candidate.display_name })
  } finally {
    cliDiscoveryLoading.value = false
  }
}

async function loadAgentCenter() {
  agentCenterLoading.value = true
  dismissByKey('agent-center')
  // getAgentCenterOverview is a deleted 404 route (docs/app-core-ui.md §4.8).
  // Load the versioned catalog directly; overview-only projections degrade.
  loading.value = true
  try {
      const [definitions, modes, candidates, toolReadiness] = await Promise.all([
        api.listAgents().catch(() => [] as AgentProfileDto[]),
        api.listAgentModes().catch(() => [] as AgentModeDto[]),
        api.listAgentCandidates().catch(() => [] as AgentCandidateDto[]),
        api.getToolLayerReadiness().catch(() => null),
      ])
      agentCenterOverview.value = null
      agentModes.value = modes
      agents.value = (definitions as Array<AgentProfileDto & Partial<AgentDefinitionDto>>).map((definition) => ({
        ...definition,
        name: definition.display_name ?? definition.slug ?? definition.name,
        agent_type: definition.role ?? definition.agent_type,
        allowed_tools: definition.tool_scope
          ? Array.isArray(definition.tool_scope)
            ? definition.tool_scope
            : []
          : (definition as unknown as AgentProfileDto).allowed_tools,
      }))
      agentCandidates.value = candidates as unknown as AgentCandidateDto[]
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
      const activeAgent = agents.value.find((agent) => agent.id === configuringAgentId.value)
        ?? agents.value.find((agent) => agent.id === selectedAgentId.value)
        ?? agents.value[0]
      if (activeAgent) openAgentConfig(activeAgent)
      api.executeCodeTool('project_templates')
        .then((result) => { projectTemplates.value = projectTemplatesFromResult(result) })
        .catch(() => { projectTemplates.value = [] })
    } catch (error) {
      status.error({ key: 'agent-center', source: 'agents', message: error instanceof Error ? error.message : t('settings.centerLoadFailed'), action: { label: t('settings.retry'), run: loadAgentCenter } })
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

// PromptContext CRUD/preview moved to settings/sections/PromptContextPanel.vue (D7.3)

function agentSaveErrorMessage(error: unknown): string {
  const msg = error instanceof Error ? error.message : String(error)
  if (msg.includes('412') || msg.toLowerCase().includes('revision') || msg.includes('Precondition')) return t('settings.agentConflict')
  if (msg.includes('409') || msg.toLowerCase().includes('built-in')) return t('settings.agentBuiltInConflict')
  return msg
}

/** Shared versioned write path: update draft (If-Match revision) then publish a new immutable version. */
async function publishAgentDraft(
  agent: AgentProfileDto,
  body: Partial<AgentDefinitionDto>,
  revision: number | null | undefined,
  successTitle: string
) {
  const draft = await api.updateAgentDraft(agent.id, body, revision != null ? String(revision) : null)
  await api.publishAgent(agent.id, draft.revision != null ? String(draft.revision) : null)
  await loadAgentCenter()
  notify.success(successTitle)
}

async function updateAgentMode(agent: AgentProfileDto, mode: string) {
  if (agent.is_built_in) {
    status.warning({ key: 'agent-builtin', source: 'agents', message: t('settings.builtInCloneHint') })
    return
  }
  busy.value = true
  try {
    // Legacy flat `mode` has no versioned column; the operation/execution layer is the
    // versioned identity. Keep the call surface but persist nothing beyond the current profile.
    await publishAgentDraft(agent, {
      display_name: agent.name,
      layer: agent.layer,
      role: agent.agent_type,
      tool_scope: agent.allowed_tools,
      capabilities: agent.capabilities,
      system_prompt: agent.system_prompt ?? null,
      description: agent.description || null,
      enabled: agent.enabled
    }, agent.revision, agent.name)
  } catch (error) {
    notify.error(new Error(agentSaveErrorMessage(error)), { title: agent.name })
  } finally {
    busy.value = false
  }
}

async function setAgentEnabled(agent: AgentProfileDto, enabled: boolean) {
  if (agent.is_built_in) {
    status.warning({ key: 'agent-builtin', source: 'agents', message: t('settings.builtInCloneHint') })
    return
  }
  busy.value = true
  try {
    await publishAgentDraft(agent, { enabled }, agent.revision, agent.name)
  } catch (error) {
    notify.error(new Error(agentSaveErrorMessage(error)), { title: agent.name })
  } finally {
    busy.value = false
  }
}

async function cloneAgentProfile() {
  const agent = configuringAgent.value
  if (!agent) return
  agentCloneBusy.value = true
  try {
    const newName = `${agent.name} (copy)`
    await api.createAgentDraft({
      slug: `${agent.id}-copy-${Date.now()}`,
      display_name: newName,
      layer: agent.layer as AgentDefinitionDto['layer'],
      role: agent.agent_type,
      capabilities: agentEditCapabilities.value,
      tool_scope: agentEditTools.value,
      system_prompt: agentEditSystemPrompt.value || agent.system_prompt || null,
      description: agentEditDescription.value || agent.description || null,
      model_strategy: bindingFromModelStrategy(agent).selection_kind === 'inherit' ? { kind: 'inherit' } : undefined,
      enabled: true
    })
    await loadAgentCenter()
    const cloned = agents.value.find((a) => (a as AgentProfileDto & { display_name?: string }).display_name === newName)
      ?? agents.value.find((a) => a.name === newName)
    if (cloned) openAgentConfig(cloned)
    notify.success(newName)
  } catch (error) {
    notify.error(error, { title: t('settings.cloneAgent') })
  } finally {
    agentCloneBusy.value = false
  }
}

async function saveAgentProfile() {
  const agent = configuringAgent.value
  if (!agent) return
  if (agent.is_built_in) {
    status.warning({ key: 'agent-builtin', source: 'agents', message: t('settings.builtInCloneHint') })
    return
  }
  busy.value = true
  try {
    await publishAgentDraft(agent, {
      display_name: agent.name,
      layer: agent.layer,
      role: agent.agent_type,
      tool_scope: agentEditTools.value,
      capabilities: agentEditCapabilities.value,
      system_prompt: agentEditSystemPrompt.value || null,
      description: agentEditDescription.value || null,
      enabled: agent.enabled
    }, agentEditRevision.value ?? agent.revision, agent.name)
    // Re-sync edit state from the published agent
    const updated = agents.value.find((a) => a.id === configuringAgentId.value)
    if (updated) {
      agentEditTools.value = [...(updated.allowed_tools ?? [])]
      agentEditCapabilities.value = [...(updated.capabilities ?? [])]
      agentEditSystemPrompt.value = updated.system_prompt ?? ''
      agentEditDescription.value = updated.description ?? ''
      agentEditRevision.value = updated.revision ?? null
    }
  } catch (error) {
    notify.error(new Error(agentSaveErrorMessage(error)), { title: agent.name })
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
  agentEditRevision.value = agent.revision ?? null
  agentNewCapability.value = ''
  agentToolQuery.value = ''
  agentToolSourceFilter.value = 'all'
  agentToolRiskFilter.value = 'all'
  const binding = bindingFromModelStrategy(agent)
  agentRuntimeSelection.value = binding?.selection_kind ?? 'inherit'
  agentRuntimeModelKey.value = binding?.provider_instance_id && binding.model_id
    ? modelOptionKey(binding.provider_instance_id, binding.model_id)
    : runtimeModels.value[0]
      ? modelOptionKey(runtimeModels.value[0].provider_instance_id, runtimeModels.value[0].model_id)
      : ''
  agentRuntimeCliId.value = binding?.runtime_kind === 'cli' ? binding.provider_instance_id ?? '' : runtimeCliOptions.value[0]?.runtime_id ?? ''
  agentRuntimeAcpId.value = binding?.runtime_kind === 'acp' ? binding.provider_instance_id ?? '' : runtimeAcpOptions.value[0]?.runtime_id ?? ''
  agentRuntimeModelQuery.value = ''
  agentRuntimeCliQuery.value = ''
  agentRuntimeAcpQuery.value = ''
  nextTick(() => {
    if (!window.matchMedia('(max-width: 760px)').matches) return
    const panel = document.querySelector('.agent-detail-panel')
    panel?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  })
}

function closeAgentConfig() {
  selectedAgentId.value = ''
  configuringAgentId.value = ''
}

function openAgentConfigById(agentId: string) {
  const agent = agents.value.find((item) => item.id === agentId)
  if (agent) openAgentConfig(agent)
}

/** Build the Core model_strategy JSON from the current runtime-source selection. */
function agentModelStrategy(): Record<string, unknown> | null {
  if (agentRuntimeSelection.value === 'cli') {
    return agentRuntimeCliId.value ? { kind: 'cli', runtime_id: agentRuntimeCliId.value } : null
  }
  if (agentRuntimeSelection.value === 'acp') {
    return agentRuntimeAcpId.value ? { kind: 'acp', runtime_id: agentRuntimeAcpId.value } : null
  }
  if (agentRuntimeSelection.value === 'fixed_model') {
    const selected = runtimeModels.value.find((model) =>
      modelOptionKey(model.provider_instance_id, model.model_id) === agentRuntimeModelKey.value
    )
    return selected
      ? { kind: 'fixed', provider_instance_id: selected.provider_instance_id, model: selected.model_id }
      : null
  }
  // provider_auto has no Core persistence; inherit covers it.
  return { kind: 'inherit' }
}

function agentStrategySaveable(): boolean {
  return agentModelStrategy() !== null
}

async function saveAgentModelStrategy(agent: AgentProfileDto) {
  const strategy = agentModelStrategy()
  if (!strategy) return
  agentRuntimeBusy.value = true
  try {
    const draft = await api.updateAgentDraft(agent.id, { model_strategy: strategy } as Partial<AgentDefinitionDto>, agent.revision != null ? String(agent.revision) : null)
    await api.publishAgent(agent.id, draft.revision != null ? String(draft.revision) : null)
    await loadAgentCenter()
    notify.success(t('settings.agentModelStrategyPublished', { name: agent.name }))
  } catch (error) {
    notify.error(new Error(agentSaveErrorMessage(error)), { title: agent.name })
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
      protocol: providerForm.protocol || null,
      base_url: formFields.value.base_url ? (providerForm.base_url || null) : null,
      model: providerForm.id ? (providerForm.model || null) : null,
      models: providerForm.models,
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
    notify.success(saved.display_name)
  } catch (error) {
    notify.error(error, { title: providerForm.display_name })
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
    'prompt-context-engineer': t('settings.agentTypePromptContextEngineer'),
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
    'review-executor': t('settings.agentTypeReviewExecutor'),
    'tool-packager': t('settings.agentTypeToolPackager'),
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
    balanced: t('settings.agentModeBalanced'),
    'plan-first': t('settings.agentModePlanFirst'),
    parallel: t('settings.agentModeParallel'),
    'safe-research': t('settings.agentModeSafeResearch'),
    chat: t('settings.agentModeChat'),
    plan: t('settings.agentModePlan'),
    execute: t('settings.agentModeExecute'),
    review: t('settings.agentModeReview'),
  }
  return map[mode] ?? mode
}

function agentModeSummary(mode: AgentModeDto) {
  const map: Record<string, string> = {
    balanced: t('settings.agentModeBalancedHint'),
    'plan-first': t('settings.agentModePlanFirstHint'),
    parallel: t('settings.agentModeParallelHint'),
    'safe-research': t('settings.agentModeSafeResearchHint')
  }
  return map[mode.id] ?? mode.summary
}

function agentPolicyLabel(policy: string) {
  const map: Record<string, string> = {
    balanced: t('settings.policyBalanced'),
    strict: t('settings.policyStrict'),
    performance: t('settings.policyPerformance')
  }
  return map[policy] ?? policy
}

function providerPresentation(driver: string) {
  return findTemplate(driver)
}

function candidateStatusLabel(status: string) {
  return status === 'proposed' ? t('settings.candidateProposed') : status
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

import '../settings/settings.css'
</script>

<template>
<div class="settings-page" :class="{ 'settings-exiting': settingsExiting }" :style="settingsPageMaterialStyle" v-bind="settingsPageDataAttrs">
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
          :title="item.label"
          :aria-label="item.label"
            @click="selectSettingsSection(item.key)"
        >
          <component :is="item.icon" :size="16" />
          {{ item.label }}
        </UiButton>
      </nav>

      <div class="settings-content" :style="settingsContentStyle" v-bind="settingsContentDataAttrs">
        <Transition name="section-fade" mode="out-in">
        <div :key="activeSection" class="settings-section-wrapper">
        <template v-if="activeSection === 'general'">
          <GeneralSection />
        </template>

        <template v-if="activeSection === 'model'">
          <div class="center-page model-center-page">
          <div class="center-command-bar">
            <div>
              <span class="center-kicker">{{ t('settings.model') }}</span>
              <h2>{{ t('settings.modelCenter') }}</h2>
              <p>{{ t('settings.modelCenterSubtitle') }}</p>
            </div>
            <div class="center-command-actions">
              <UiButton variant="outline" size="sm" @click="handleAddProviderClick">
                <Plus :size="14" />
                <span>{{ t('settings.addProvider') }}</span>
              </UiButton>
              <UiButton variant="outline" size="sm" :disabled="modelCenterLoading || modelCenterBusy" @click="loadModelCenter">
                <RefreshCw :size="14" />
                <span>{{ t('settings.refresh') }}</span>
              </UiButton>
            </div>
          </div>

          <section class="center-overview-receipt" :aria-label="t('settings.centerOverview')">
            <div class="center-receipt-item" :class="{ ready: modelCenterOverview?.capabilities.provider_crud }">
              <Database :size="17" />
              <div>
                <span>{{ t('settings.modelProviderManagement') }}</span>
                <strong>{{ t('settings.modelProviderManagementHint') }}</strong>
              </div>
              <UiBadge :variant="modelCenterOverview?.capabilities.provider_crud ? 'default' : 'secondary'">
                {{ modelCenterOverview?.capabilities.provider_crud ? t('settings.writable') : t('settings.readOnly') }}
              </UiBadge>
            </div>
            <div class="center-receipt-item configured">
              <Cpu :size="17" />
              <div>
                <span>{{ t('settings.modelCatalogScope') }}</span>
                <strong>{{ t('settings.configuredModelsOnly') }}</strong>
              </div>
              <UiBadge variant="outline">{{ modelCatalogModeLabel(modelCenterOverview?.capabilities.model_catalog_mode) }}</UiBadge>
            </div>
            <div class="center-receipt-item" :class="{ ready: modelCenterOverview?.capabilities.live_model_discovery, unavailable: !modelCenterOverview?.capabilities.live_model_discovery }">
              <Search :size="17" />
              <div>
                <span>{{ t('settings.liveDiscovery') }}</span>
                <strong>{{ modelCenterOverview?.capabilities.live_model_discovery ? t('settings.available') : t('settings.pendingCore') }}</strong>
              </div>
              <UiBadge :variant="modelCenterOverview?.capabilities.live_model_discovery ? 'default' : 'secondary'">
                {{ modelCenterOverview?.capabilities.live_model_discovery ? t('settings.available') : t('settings.unavailable') }}
              </UiBadge>
            </div>
          </section>

          <div v-if="modelCenterLoading && !modelCenterOverview" class="center-loading-state" aria-live="polite">
            <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
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

          <div class="center-workbench model-workbench">
          <aside class="center-inspector" :aria-label="t('settings.centerInspector')">
            <div class="center-pane-heading">
              <div>
                <span>{{ t('settings.centerInspector') }}</span>
                <strong>{{ t('settings.modelHealth') }}</strong>
              </div>
              <PanelRight :size="16" />
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

            <section v-if="selectedProviderDetail" class="inspector-provider-detail">
              <div class="provider-detail-head compact">
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
              <div v-if="selectedProviderDetail.status_message" class="provider-status-note compact">
                <Terminal :size="14" />
                <span>{{ selectedProviderDetail.status_message }}</span>
              </div>
              <div class="provider-detail-actions compact">
                <UiButton variant="outline" size="sm" @click="openEditModal(selectedProviderDetail)">
                  <Edit3 :size="14" />
                  <span>{{ t('settings.editConfig') }}</span>
                </UiButton>
                <UiButton variant="outline" size="sm" :disabled="modelCenterBusy" @click="toggleProviderEnabled(selectedProviderDetail)">
                  <component :is="selectedProviderDetail.enabled ? X : Check" :size="14" />
                  <span>{{ selectedProviderDetail.enabled ? t('settings.disable') : t('settings.enable') }}</span>
                </UiButton>
                <UiButton variant="ghost" size="sm" class="provider-delete-btn" :disabled="modelCenterBusy" @click="deleteProvider(selectedProviderDetail.id)">
                  <Trash2 :size="14" />
                  <span>{{ t('settings.delete') }}</span>
                </UiButton>
              </div>
            </section>
          </aside>

          <aside class="center-resource-rail model-resource-navigation" :aria-label="t('settings.centerResources')">
            <div class="center-pane-heading">
              <div>
                <span>{{ t('settings.centerResources') }}</span>
                <strong>{{ t('settings.modelCenterResources') }}</strong>
              </div>
            </div>
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
          </aside>

          <main class="center-resource-stage">
          <section v-if="modelCenterSection === 'models'" class="center-resource-section">
            <div class="center-resource-heading">
              <div>
                <h3>{{ t('settings.centerModels') }}</h3>
                <p>{{ t('settings.configuredModelsHint') }}</p>
              </div>
              <UiBadge variant="outline">{{ modelCatalogModeLabel(modelCenterOverview?.capabilities.model_catalog_mode) }}</UiBadge>
            </div>
            <div v-for="provider in modelCenterOverview?.api_connections ?? []" :key="provider.id" class="model-provider-group">
              <div class="center-resource-list-row model-provider-group-head">
                <div class="center-resource-primary">
                  <span
                    class="provider-brand-icon"
                    :style="{ color: providerPresentation(provider.driver)?.brand_color, backgroundColor: providerPresentation(provider.driver)?.brand_bg }"
                  >
                    <span v-if="providerPresentation(provider.driver)?.icon" class="provider-brand-mark" v-html="providerPresentation(provider.driver)?.icon"></span>
                    <Server v-else :size="16" />
                  </span>
                  <div>
                    <strong>{{ provider.display_name }}</strong>
                    <span>{{ provider.driver }}</span>
                  </div>
                </div>
                <UiBadge :variant="statusVariant(provider.status)">{{ statusLabel(provider.status) }}</UiBadge>
                <UiButton
                  variant="outline"
                  size="sm"
                  :disabled="modelCenterBusy || !modelCenterOverview?.capabilities.model_discovery_refresh"
                  :title="modelCenterOverview?.capabilities.model_discovery_refresh ? t('settings.addModel') : t('settings.modelDiscoveryUnsupported')"
                  @click="openAddModelModal(provider.id)"
                >
                  <Plus :size="14" />
                  {{ t('settings.addModel') }}
                </UiButton>
              </div>
              <div class="model-group-models">
                <div v-for="model in modelsForProvider(provider.id)" :key="model.id" class="center-resource-list-row model-group-model">
                  <div class="center-resource-primary">
                    <Cpu :size="15" />
                    <div>
                      <strong>{{ model.model_id }}</strong>
                      <div class="center-resource-meta">
                        <span v-for="source in model.configuration_sources" :key="source">{{ configuredModelSourceLabel(source) }}</span>
                        <span v-for="purpose in model.route_purposes" :key="purpose">{{ purpose }}</span>
                      </div>
                    </div>
                  </div>
                  <UiBadge :variant="statusVariant(model.status)">{{ statusLabel(model.status) }}</UiBadge>
                  <UiButton
                    variant="ghost"
                    size="sm"
                    :disabled="modelCenterBusy"
                    :title="t('settings.removeModel')"
                    @click="removeModel(provider.id, model.model_id)"
                  >
                    <Trash2 :size="14" />
                  </UiButton>
                </div>
                <div v-if="modelsForProvider(provider.id).length === 0" class="center-empty-state">
                  <Cpu :size="20" />
                  <span>{{ t('settings.noProviderModels') }}</span>
                </div>
              </div>
            </div>
            <div v-if="(modelCenterOverview?.api_connections.length ?? 0) === 0" class="center-empty-state">
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
              <UiButton
                variant="outline"
                size="sm"
                :disabled="cliDiscoveryLoading"
                @click="discoverCliRuntimes"
              >
                <RefreshCw :size="14" :class="{ 'animate-spin': cliDiscoveryLoading }" />
                {{ cliDiscoveryLoading ? t('settings.discoveringCli') : t('settings.discoverCli') }}
              </UiButton>
            </div>

            <!-- Discovery Candidates Panel -->
            <div v-if="cliDiscoveryLoaded" class="cli-discovery-panel">
              <div class="cli-discovery-header">
                <strong>{{ t('settings.cliDiscoveryTitle') }}</strong>
                <p>{{ t('settings.cliDiscoveryHint') }}</p>
              </div>
              <div v-if="cliDiscoveryCandidates.length === 0" class="cli-discovery-empty">
                {{ t('settings.cliDiscoveryEmpty') }}
              </div>
              <div v-else class="cli-discovery-grid">
                <div
                  v-for="candidate in cliDiscoveryCandidates"
                  :key="candidate.driver"
                  class="cli-discovery-card"
                  :class="{ 'is-found': candidate.status === 'found', 'is-configured': candidate.status === 'configured', 'is-missing': candidate.status === 'missing' }"
                >
                  <div class="cli-discovery-info">
                    <div class="cli-discovery-name-row">
                      <Terminal :size="15" />
                      <strong>{{ candidate.display_name }}</strong>
                      <span class="cli-discovery-driver">{{ candidate.driver }}</span>
                    </div>
                    <code v-if="candidate.binary_path" class="cli-discovery-path" :title="candidate.binary_path">
                      {{ candidate.binary_path }}
                    </code>
                    <span v-else-if="candidate.status === 'missing'" class="cli-discovery-status-text muted">
                      {{ t('settings.notDetected') }}
                    </span>
                  </div>
                  <div class="cli-discovery-action">
                    <UiBadge v-if="candidate.status === 'configured'" variant="secondary">
                      {{ t('settings.alreadyConnected') }}
                    </UiBadge>
                    <UiButton
                      v-else-if="candidate.status === 'found'"
                      variant="default"
                      size="sm"
                      @click="connectDiscoveredCli(candidate)"
                    >
                      <Plus :size="13" />
                      {{ t('settings.quickConnect') }}
                    </UiButton>
                    <UiBadge v-else variant="outline" class="muted-badge">
                      {{ t('settings.notDetected') }}
                    </UiBadge>
                  </div>
                </div>
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
                <div class="center-resource-actions">
                  <UiButton
                    v-if="providers.find(provider => provider.id === runtime.provider_instance_id)"
                    variant="outline"
                    size="sm"
                    @click="openEditModal(providers.find(provider => provider.id === runtime.provider_instance_id)!)"
                  >
                    <Settings2 :size="14" />
                    {{ t('settings.editConfig') }}
                  </UiButton>
                  <UiButton
                    v-if="runtime.provider_instance_id"
                    variant="ghost"
                    size="icon"
                    class="provider-delete-btn"
                    :disabled="modelCenterBusy"
                    :title="t('settings.delete')"
                    @click="deleteProvider(runtime.provider_instance_id)"
                  >
                    <Trash2 :size="14" />
                  </UiButton>
                </div>
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
                    <span
                      class="provider-brand-icon"
                      :style="{ color: row.template?.brand_color, backgroundColor: row.template?.brand_bg }"
                    >
                      <span v-if="row.template?.icon" class="provider-brand-mark" v-html="row.template?.icon"></span>
                      <Database v-else :size="16" />
                    </span>
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
                        class="provider-delete-btn"
                        :disabled="modelCenterBusy"
                        :title="t('settings.delete')"
                        @click="deleteProvider(row.provider.id)"
                      >
                        <Trash2 :size="14" />
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

              </template>
              <div v-if="filteredModelCenterRows.length === 0" class="model-provider-empty">
                <Search :size="18" />
                <span>{{ t('settings.noProviderResults') }}</span>
                <UiButton variant="outline" size="sm" @click="handleAddProviderClick">
                  <Plus :size="14" />
                  {{ t('settings.addProvider') }}
                </UiButton>
              </div>
            </div>
          </section>
          </main>
          </div>
          </div>
        </template>

        <template v-if="activeSection === 'agentCenter'">
          <div class="agent-center-merged">
            <div class="center-command-bar">
              <div>
                <h2>{{ t('settings.agentCenter') }}</h2>
                <p>{{ t('settings.agentCenterSubtitle') }}</p>
              </div>
              <div class="center-command-actions">
                <UiButton variant="outline" size="sm" data-testid="open-agent-workbench" @click="openFullWorkbench">
                  <ExternalLink :size="14" />
                  <span>{{ t('agentCenter.openWorkbench', '完整工作台') }}</span>
                </UiButton>
              </div>
            </div>
            <div class="ac-subtabs" role="tablist" data-testid="agent-center-subtabs">
              <button :class="['ac-subtab', { active: agentCenterTab === 'config' }]" role="tab" :aria-selected="agentCenterTab === 'config'" @click="agentCenterTab = 'config'">{{ t('settings.agents') }}</button>
              <button :class="['ac-subtab', { active: agentCenterTab === 'promptContext' }]" role="tab" :aria-selected="agentCenterTab === 'promptContext'" @click="agentCenterTab = 'promptContext'">{{ t('settings.promptContext') }}</button>
              <button :class="['ac-subtab', { active: agentCenterTab === 'promptEngineering' }]" role="tab" :aria-selected="agentCenterTab === 'promptEngineering'" @click="agentCenterTab = 'promptEngineering'">{{ t('settings.promptEngineering') }}</button>
              <button :class="['ac-subtab', { active: agentCenterTab === 'evolution' }]" role="tab" :aria-selected="agentCenterTab === 'evolution'" @click="agentCenterTab = 'evolution'">{{ t('settings.agentEvolution') }}</button>
            </div>
            <template v-if="agentCenterTab === 'config'">

          <div class="center-page agent-center-page">
          <div class="center-command-bar">
            <div>
              <span class="center-kicker">{{ t('settings.agents') }}</span>
              <h2>{{ t('settings.agentCenter') }}</h2>
              <p>{{ t('settings.agentCenterSubtitle') }}</p>
            </div>
            <div class="center-command-actions">
              <div class="agent-view-toggle">
                <button
                  :class="['agent-view-btn', { active: agentViewMode === 'topology' }]"
                  :title="t('settings.topologyView')"
                  :aria-label="t('settings.topologyView')"
                  :aria-pressed="agentViewMode === 'topology'"
                  @click="agentViewMode = 'topology'"
                >
                  <LayoutGrid :size="15" />
                </button>
                <button
                  :class="['agent-view-btn', { active: agentViewMode === 'list' }]"
                  :title="t('settings.listView')"
                  :aria-label="t('settings.listView')"
                  :aria-pressed="agentViewMode === 'list'"
                  @click="agentViewMode = 'list'"
                >
                  <List :size="15" />
                </button>
              </div>
              <UiButton variant="outline" size="sm" :disabled="agentCenterLoading || agentRuntimeBusy" @click="loadAgentCenter">
                <RefreshCw :size="14" />
                <span>{{ t('settings.refresh') }}</span>
              </UiButton>
            </div>
          </div>

          <section class="center-overview-receipt agent-overview-receipt" :aria-label="t('settings.centerOverview')">
            <div class="center-receipt-item ready">
              <Settings2 :size="17" />
              <div>
                <span>{{ t('settings.agentProfilesWritable') }}</span>
                <strong>{{ t('settings.agentProfilesWritableHint') }}</strong>
              </div>
              <UiBadge variant="default">{{ t('settings.writable') }}</UiBadge>
            </div>
            <div class="center-receipt-item preview" :class="{ ready: true }">
              <Workflow :size="17" />
              <div>
                <span>{{ t('settings.runtimePreviewOnly') }}</span>
                <strong>{{ t('settings.runtimePreviewOnlyHint') }}</strong>
              </div>
              <UiBadge variant="default">
                {{ t('settings.writable') }}
              </UiBadge>
            </div>
            <div class="center-receipt-item configured">
              <Bot :size="17" />
              <div>
                <span>{{ t('settings.activeAgents') }}</span>
                <strong>{{ agents.filter(agent => agent.enabled).length }} / {{ agents.length }}</strong>
              </div>
              <UiBadge variant="outline">{{ planningAgents.length }} + {{ executionAgents.length }}</UiBadge>
            </div>
          </section>

          <div v-if="agentCenterLoading && agents.length === 0" class="center-loading-state" aria-live="polite">
            <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
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

          <div v-if="!agentCenterLoading && agents.length === 0" class="center-empty-state center-empty-state-prominent">
            <Bot :size="22" />
            <div>
              <strong>{{ t('settings.noAgents') }}</strong>
              <span>{{ t('settings.noAgentsHint') }}</span>
            </div>
          </div>

          <div class="center-workbench agent-workbench" :class="`view-${agentViewMode}`">
          <aside class="center-resource-rail" :aria-label="t('settings.centerResources')">
            <div class="center-pane-heading">
              <div>
                <span>{{ t('settings.centerResources') }}</span>
                <strong>{{ t('settings.agentProfiles') }}</strong>
              </div>
            </div>

            <section class="agent-column compact">
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
                <button class="agent-card-select" @click="openAgentConfig(agent)">
                  <div class="agent-card-icon"><Workflow :size="17" /></div>
                  <div class="agent-card-main">
                    <strong>{{ agentTypeLabel(agent.agent_type) }}</strong>
                    <span>{{ agentModeLabel(agent.mode) }} · {{ agent.id }}</span>
                    <small :title="runtimeSourceSummary(agentRuntimeBindings[agent.id])">{{ runtimeSourceSummary(agentRuntimeBindings[agent.id]) || t('settings.runtimeUnresolved') }}</small>
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

            <section class="agent-column compact">
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
                <button class="agent-card-select" @click="openAgentConfig(agent)">
                  <div class="agent-card-icon execution"><Cpu :size="17" /></div>
                  <div class="agent-card-main">
                    <strong>{{ agentTypeLabel(agent.agent_type) }}</strong>
                    <span>{{ agentModeLabel(agent.mode) }} · {{ agent.id }}</span>
                    <small :title="runtimeSourceSummary(agentRuntimeBindings[agent.id])">{{ runtimeSourceSummary(agentRuntimeBindings[agent.id]) || t('settings.runtimeUnresolved') }}</small>
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
          </aside>

          <main class="center-resource-stage">

          <div v-if="agentViewMode === 'topology'" class="agent-topology-section">
            <AgentTopologyCanvas
              :agents="agents"
              :candidates="agentCandidates"
              :providers="providers"
              :routes="routes"
              :runtime-bindings="agentRuntimeBindings"
              :selected-agent-id="selectedAgentId"
              :agent-labels="topologyAgentLabels"
              :candidate-labels="topologyCandidateLabels"
              @select-agent="openAgentConfigById"
              @configure-agent="openAgentConfigById"
            />
          </div>

          <div v-if="agentViewMode === 'list'" class="agent-list-summary">
            <PanelRight :size="16" />
            <span>{{ selectedAgent ? agentTypeLabel(selectedAgent.agent_type) : t('settings.pleaseOpenAgentConfig') }}</span>
            <UiButton v-if="selectedAgent" variant="outline" size="sm" @click="openAgentConfig(selectedAgent)">
              <Settings2 :size="14" />
              {{ t('settings.openAgentConfig') }}
            </UiButton>
          </div>
          </main>

          <aside class="center-inspector agent-inspector" :aria-label="t('settings.centerInspector')">
            <div class="center-pane-heading">
              <div>
                <span>{{ t('settings.centerInspector') }}</span>
                <strong>{{ configuringAgent ? agentTypeLabel(configuringAgent.agent_type) : t('settings.agentConfiguration') }}</strong>
              </div>
              <PanelRight :size="16" />
            </div>
            <div v-if="configuringAgent" class="agent-detail-panel">
              <div class="agent-detail-head">
                <div class="agent-card-icon" :class="{ execution: configuringAgent.layer === 'execution' }">
                  <component :is="configuringAgent.layer === 'planning' ? Workflow : Cpu" :size="20" />
                </div>
                <div>
                  <h3>{{ agentTypeLabel(configuringAgent.agent_type) }}</h3>
                  <p>{{ agentTypeLabel(configuringAgent.agent_type) }} · {{ agentLayerLabel(configuringAgent.layer) }}</p>
                </div>
                <UiButton variant="ghost" size="icon" :title="t('settings.closeConfig')" @click="closeAgentConfig">
                  <X :size="16" />
                </UiButton>
              </div>

              <!-- 启用开关 — 内置只读 -->
              <div class="agent-config-switch">
                <div>
                  <strong>{{ t('settings.agentEnabled') }}</strong>
                  <span>{{ configuringAgent.is_built_in ? t('settings.builtInAgent') : configuringAgent.id }}</span>
                  <small v-if="configuringAgent.is_built_in" class="agent-builtin-label">{{ t('settings.builtInCloneHint') }}</small>
                </div>
                <UiSwitch
                  :model-value="configuringAgent.enabled"
                  :disabled="busy || configuringAgent.is_built_in"
                  @update:model-value="setAgentEnabled(configuringAgent, $event)"
                />
              </div>

              <!-- 运行模式 — 统一走 PUT /agents -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentModeTitle') }}</div>
                <p v-if="configuringAgent.is_built_in" class="agent-config-hint">{{ t('settings.builtInCloneHint') }}</p>
                <div class="agent-mode-grid">
                  <button
                    v-for="mode in agentModes"
                    :key="mode.id"
                    class="agent-mode-card"
                    :class="{ active: configuringAgent.mode === mode.id, disabled: configuringAgent.is_built_in }"
                    :disabled="configuringAgent.is_built_in"
                    @click="updateAgentMode(configuringAgent, mode.id)"
                  >
                    <strong>{{ agentModeLabel(mode.id) }}</strong>
                    <span>{{ agentModeSummary(mode) }}</span>
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
                    · {{ agentPolicyLabel(configuredAgentMode.budget_policy) }}
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
                  <button :class="{ active: agentRuntimeSelection === 'inherit' }" :aria-pressed="agentRuntimeSelection === 'inherit'" @click="agentRuntimeSelection = 'inherit'">
                    <Workflow :size="16" />
                    <strong>{{ t('settings.runtimeInherit') }}</strong>
                    <span>{{ t('settings.runtimeInheritHint') }}</span>
                  </button>
                  <button :class="{ active: agentRuntimeSelection === 'fixed_model' }" :aria-pressed="agentRuntimeSelection === 'fixed_model'" @click="agentRuntimeSelection = 'fixed_model'">
                    <Cpu :size="16" />
                    <strong>{{ t('settings.runtimeFixedModel') }}</strong>
                    <span>{{ t('settings.runtimeFixedModelHint') }}</span>
                  </button>
                  <button :class="{ active: agentRuntimeSelection === 'cli' }" :aria-pressed="agentRuntimeSelection === 'cli'" @click="agentRuntimeSelection = 'cli'">
                    <Terminal :size="16" />
                    <strong>CLI</strong>
                    <span>{{ t('settings.runtimeCliHint') }}</span>
                  </button>
                  <button :class="{ active: agentRuntimeSelection === 'acp' }" :aria-pressed="agentRuntimeSelection === 'acp'" @click="agentRuntimeSelection = 'acp'">
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

                <div class="runtime-binding-readonly">
                  <Info :size="16" />
                  <div>
                    <strong>{{ t('settings.agentStrategyVersioned') }}</strong>
                    <span>{{ t('settings.agentStrategyVersionedHint') }}</span>
                  </div>
                </div>
                <div class="modal-actions compact">
                  <UiButton :disabled="agentRuntimeBusy || !agentStrategySaveable()" size="sm" @click="saveAgentModelStrategy(configuringAgent)">
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

              <!-- 工具绑定 — 完整面板（分组/风险/审批） -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">
                  {{ t('settings.agentTools') }}
                  <UiBadge variant="outline">{{ agentToolSelectionSummary.selected }}/{{ agentToolSelectionSummary.total }}</UiBadge>
                  <UiBadge v-if="agentToolSelectionSummary.approval > 0" variant="secondary">{{ t('settings.approvalRequired') }} {{ agentToolSelectionSummary.approval }}</UiBadge>
                </div>
                <p class="agent-config-hint">{{ t('settings.agentToolsHint') }}</p>
                <div class="agent-tool-toolbar">
                  <div class="agent-tool-search">
                    <Search :size="14" />
                    <UiInput v-model="agentToolQuery" :placeholder="t('settings.toolSearchPlaceholder')" />
                  </div>
                  <select v-model="agentToolSourceFilter" class="settings-select compact">
                    <option value="all">{{ t('settings.allSources') }}</option>
                    <option v-for="src in toolSourceOptions" :key="src" :value="src">{{ src }}</option>
                  </select>
                  <select v-model="agentToolRiskFilter" class="settings-select compact">
                    <option value="all">{{ t('settings.allRisks') }}</option>
                    <option v-for="risk in toolRiskOptions" :key="risk" :value="risk">{{ risk }}</option>
                  </select>
                </div>
                <div v-if="configuringAgent.is_built_in" class="agent-builtin-hint">
                  <Info :size="14" />
                  <span>{{ t('settings.builtInCloneHint') }}</span>
                </div>
                <div v-for="[source, tools] in groupedAgentTools" :key="source" class="agent-tool-group">
                  <div class="agent-tool-group-head">
                    <strong>{{ source }}</strong>
                    <UiBadge variant="outline">{{ tools.length }}</UiBadge>
                  </div>
                  <div class="agent-tool-grid">
                    <button
                      v-for="tool in tools"
                      :key="tool.id"
                      class="agent-tool-chip"
                      :class="{
                        active: agentEditTools.includes(tool.id),
                        risky: tool.requires_approval
                      }"
                      :disabled="configuringAgent.is_built_in"
                      :title="tool.display_name + ' · ' + tool.risk + (tool.requires_approval ? ' · ' + t('settings.approvalRequired') : '')"
                      @click="toggleAgentTool(tool.id)"
                    >
                      <span class="agent-tool-name">{{ tool.display_name }}</span>
                      <span class="agent-tool-meta">
                        <span class="agent-tool-risk" :class="tool.risk">{{ tool.risk }}</span>
                        <span v-if="tool.requires_approval" class="agent-tool-approval">⚑ {{ t('settings.approvalRequired') }}</span>
                      </span>
                      <span class="agent-tool-id">{{ tool.id }}</span>
                    </button>
                  </div>
                </div>
                <p v-if="groupedAgentTools.length === 0" class="quiet">{{ t('settings.noTools') }}</p>
                <div class="agent-tool-bulk">
                  <UiButton variant="ghost" size="sm" :disabled="configuringAgent.is_built_in" @click="agentEditTools = manifestToolList.filter(t => !t.requires_approval).map(t => t.id)">{{ t('settings.selectReadOnlyTools') }}</UiButton>
                  <UiButton variant="ghost" size="sm" :disabled="configuringAgent.is_built_in" @click="agentEditTools = []">{{ t('settings.clearSelection') }}</UiButton>
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
                  <UiInput v-model="agentNewCapability" :placeholder="t('settings.newCapabilityPlaceholder')" @keydown.enter="addAgentCapability" />
                  <UiButton variant="outline" size="sm" :disabled="!agentNewCapability.trim()" @click="addAgentCapability">
                    <Plus :size="14" />
                    {{ t('settings.addCapability') }}
                  </UiButton>
                </div>
              </div>

              <!-- System Prompt -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentSystemPrompt') }}</div>
                <p class="agent-config-hint">{{ t('settings.agentSystemPromptOverrideHint') }}</p>
                <div class="settings-field">
                  <textarea
                    v-model="agentEditSystemPrompt"
                    class="settings-textarea prompt-editor"
                    rows="6"
                    :placeholder="t('settings.agentSystemPromptPlaceholder')"
                  ></textarea>
                </div>
              </div>

              <!-- 保存按钮 — 克隆后编辑 -->
              <div class="agent-save-bar">
                <UiButton v-if="configuringAgent.is_built_in" :disabled="agentCloneBusy" @click="cloneAgentProfile">
                  <Plus :size="14" />
                  <span>{{ t('settings.cloneAgent') }}</span>
                </UiButton>
                <UiButton v-else :disabled="busy" @click="saveAgentProfile">
                  <Save :size="14" />
                  <span>{{ t('settings.saveAgent') }}</span>
                </UiButton>
                <span v-if="!configuringAgent.is_built_in && agentEditRevision !== null" class="agent-revision-hint">rev {{ agentEditRevision }}</span>
                <span v-if="configuringAgent.is_built_in" class="agent-builtin-save-hint">{{ t('settings.builtInCloneHint') }}</span>
              </div>
            </div>
            <div v-else class="center-empty-state inspector-empty">
              <PanelRight :size="20" />
              <span>{{ agents.length > 0 ? t('settings.pleaseOpenAgentConfig') : t('settings.noAgents') }}</span>
            </div>

            <details class="center-diagnostics-panel agent-candidates-panel">
              <summary>
                <span>{{ t('settings.evolutionCandidates') }}</span>
                <UiBadge variant="outline">{{ agentCandidates.length }}</UiBadge>
              </summary>
              <div class="agent-candidate-list compact">
                <article v-for="candidate in agentCandidates" :key="candidate.id" class="agent-candidate-row">
                  <div>
                    <strong>{{ candidate.name }}</strong>
                    <span>{{ agentLayerLabel(candidate.layer) }} · {{ agentTypeLabel(candidate.agent_type) }} · {{ candidateStatusLabel(candidate.status) }}</span>
                  </div>
                  <UiBadge variant="secondary">{{ t('settings.generatedByEvolution') }}</UiBadge>
                </article>
              </div>
            </details>
          </aside>
          </div>
          </div>
        </template>
            <template v-else-if="agentCenterTab === 'promptContext'">
              <PromptContextPanel />
            </template>
            <template v-else-if="agentCenterTab === 'promptEngineering'">
              <PromptEngineeringPanel />
            </template>
            <template v-else-if="agentCenterTab === 'evolution'">
              <AgentEvolutionPanel />
            </template>
          </div>
        </template>

        <template v-if="activeSection === 'tools'">
          <ToolCenterSection />
        </template>

        <template v-if="activeSection === 'appearance'">
          <AppearanceSection />
        </template>

        <template v-if="activeSection === 'pets'">
          <PetsSection />
        </template>

        <template v-if="activeSection === 'language'">
          <LanguageSection />
        </template>

        <template v-if="activeSection === 'apiDocs'">
          <ApiDocsSection />
        </template>

        <template v-if="activeSection === 'about'">
          <AboutSection />
        </template>
    </div>
    </Transition>

    <div v-if="showModelModal" class="model-provider-modal" @click.self="showModelModal = false">
      <UiCard class="model-provider-modal-content">
        <template #header>
          <div class="modal-header-row">
            <div class="modal-header-left">
              <span
                class="modal-provider-logo"
                :style="{ color: providerPresentation(modelApiProvider(modelModalProviderId)?.driver ?? '')?.brand_color, backgroundColor: providerPresentation(modelApiProvider(modelModalProviderId)?.driver ?? '')?.brand_bg }"
              >
                <span v-if="providerPresentation(modelApiProvider(modelModalProviderId)?.driver ?? '')?.icon" class="provider-brand-mark" v-html="providerPresentation(modelApiProvider(modelModalProviderId)?.driver ?? '')?.icon"></span>
                <Cpu v-else :size="18" />
              </span>
              <div class="modal-header-info">
                <h3>{{ t('settings.addModelTitle') }}</h3>
                <span class="modal-header-sub">{{ modelApiProvider(modelModalProviderId)?.display_name ?? modelModalProviderId }}</span>
              </div>
            </div>
            <UiButton variant="ghost" size="icon" @click="showModelModal = false">
              <X :size="16" />
            </UiButton>
          </div>
        </template>

        <template #content>
          <div class="modal-form-section">
            <div class="modal-form-section-title">{{ t('settings.fetchModelsFromProvider') }}</div>
            <div class="model-modal-fetch-row">
              <UiButton
                variant="outline"
                size="sm"
                :disabled="modelModalBusy"
                @click="fetchDiscoveredModels"
              >
                <Server :size="14" />
                {{ modelModalBusy ? t('settings.fetchingModels') : (modelModalFetched ? t('settings.refreshModels') : t('settings.fetchModels')) }}
              </UiButton>
            </div>
            <div v-if="modelModalError" class="model-provider-note">
              <Terminal :size="14" />
              <span>{{ modelModalError }}</span>
            </div>
            <div v-if="modelModalFetched && modelModalDiscovered.length" class="model-discovered-list">
              <div v-for="item in modelModalDiscovered" :key="item.id" class="model-discovered-row">
                <strong>{{ item.id }}</strong>
                <UiButton
                  variant="outline"
                  size="sm"
                  :disabled="modelModalPending.includes(item.id)"
                  @click="addPendingModel(item.id)"
                >
                  {{ modelModalPending.includes(item.id) ? t('settings.modelAdded') : t('settings.addModel') }}
                </UiButton>
              </div>
            </div>
            <div v-else-if="modelModalFetched" class="center-empty-state">
              <Cpu :size="20" />
              <span>{{ t('settings.fetchModelsEmpty') }}</span>
            </div>
          </div>

          <div class="modal-form-section">
            <div class="modal-form-section-title">{{ t('settings.manualModel') }}</div>
            <div class="model-manual-row">
              <UiInput v-model="modelModalManual" :placeholder="t('settings.modelNamePlaceholder')" @keydown.enter="addManualModel" />
              <UiButton variant="outline" :disabled="!modelModalManual.trim()" @click="addManualModel">
                <Plus :size="14" />
                {{ t('settings.addModel') }}
              </UiButton>
            </div>
            <div v-if="modelModalPending.length" class="model-pending-list">
              <span v-for="id in modelModalPending" :key="id" class="provider-cap-tag">{{ id }}</span>
            </div>
          </div>
        </template>

        <template #footer>
          <div class="modal-actions">
            <UiButton variant="outline" @click="showModelModal = false">
              {{ t('settings.cancel') }}
            </UiButton>
            <UiButton :disabled="modelModalBusy || !modelModalPending.length" @click="saveModelModal()">
              <Save :size="14" />
              <span>{{ t('settings.save') }}</span>
            </UiButton>
          </div>
        </template>
      </UiCard>
    </div>
      </div>
    </div>

    <Transition name="modal-fade">
    <div v-if="showTemplatePicker" class="model-provider-modal" @click.self="showTemplatePicker = false">
      <UiCard class="model-provider-modal-content template-picker-content">
        <template #header>
          <div class="modal-header-row">
            <div class="modal-header-left">
              <span class="modal-provider-logo" :style="{ color: '#8b949e', backgroundColor: 'rgba(139,148,158,0.10)' }">
                <Plus :size="18" />
              </span>
              <div class="modal-header-info">
                <h3>{{ t('settings.addProviderTemplate') }}</h3>
                <span class="modal-header-sub">{{ t('settings.templatePickerHint') }}</span>
              </div>
            </div>
            <UiButton variant="ghost" size="icon" @click="showTemplatePicker = false">
              <X :size="16" />
            </UiButton>
          </div>
        </template>

        <template #content>
          <div class="template-picker-search">
            <Search :size="15" />
            <UiInput v-model="templatePickerQuery" :placeholder="t('settings.templateSearchPlaceholder')" />
          </div>

          <div v-if="pickerTemplateGroups.length === 0" class="template-picker-empty">
            <Server :size="20" />
            <span>{{ t('settings.templatePickerEmpty') }}</span>
          </div>

          <div v-for="group in pickerTemplateGroups" :key="group.category" class="template-picker-group">
            <div class="template-picker-group-title">{{ t(group.labelKey) }}</div>
            <div class="template-picker-grid">
              <button
                v-for="template in group.templates"
                :key="template.driver"
                class="template-picker-card"
                :style="{ '--template-accent': template.brand_color }"
                @click="pickTemplate(template)"
              >
                <span class="provider-brand-icon" :style="{ color: template.brand_color, backgroundColor: template.brand_bg }">
                  <span v-if="template.icon" class="provider-brand-mark" v-html="template.icon"></span>
                  <Database v-else :size="16" />
                </span>
                <span class="template-picker-card-body">
                  <strong :title="t(template.display_name_key)">{{ t(template.display_name_key) }}</strong>
                  <small :title="template.driver">{{ template.driver }}</small>
                </span>
                <span class="template-picker-card-meta">
                  <span>{{ connectionKindLabel(template.connection_kind) }}</span>
                  <span v-if="template.default_model" :title="template.default_model">{{ template.default_model }}</span>
                </span>
              </button>
            </div>
          </div>
        </template>
      </UiCard>
    </div>
    </Transition>

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

          <div v-if="formFields.base_url || formFields.model || formProtocolOptions.length > 0" class="modal-form-section">
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
              <div v-if="formProtocolOptions.length > 0" class="settings-field">
                <UiLabel>{{ t('settings.protocol') }}</UiLabel>
                <select v-model="providerForm.protocol" class="settings-select">
                  <option v-for="option in formProtocolOptions" :key="option" :value="option">
                    {{ t(protocolLabelKeys[option]) }}
                  </option>
                </select>
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
