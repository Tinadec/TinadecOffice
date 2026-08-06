<script setup lang="ts">
import {
  ArrowLeft,
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
import { computed, nextTick, onBeforeUnmount, proxyRefs, reactive, ref, watch } from 'vue'
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
  type ModelCenterSupplierDto,
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
import PetPreview from '@/components/PetPreview.vue'
import { UiButton, UiInput, UiCard, UiBadge, UiLabel, UiSkeleton, UiSwitch, UiDropdownMenu } from '@/components/ui'
import AgentTopologyCanvas from '@/components/AgentTopologyCanvas.vue'
import AgentEvolutionPanel from '@/components/AgentEvolutionPanel.vue'
import PromptEngineeringPanel from '@/components/PromptEngineeringPanel.vue'
import BackgroundPreview from '@/components/ui/background-preview.vue'
import PanelStyleControl from '@/components/ui/panel-style-control.vue'
import { useBackground } from '@/composables/useBackground'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useNotifications } from '@/composables/useNotifications'
import { useOptionalSettingsWorkbench, type SettingsSection as WorkbenchSettingsSection } from '@/workbench/settingsController'
import SettingsModuleBoundary from '@/settings/SettingsModuleBoundary.vue'
import { settingsModuleDescriptors } from '@/settings/settingsModuleRegistry'
import { provideSettingsModuleContext } from '@/settings/settingsModuleContext'

type SettingsSection = WorkbenchSettingsSection

interface DesktopAppConfig {
  gateway_url: string
  source: 'default' | 'user' | 'environment'
  managed: boolean
}

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
const { items: notificationItems, notify, banner, confirm, dismiss: dismissNotification, status, dismissByKey } = useNotifications()

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

const settingsWorkbench = useOptionalSettingsWorkbench()
const localActiveSection = ref<SettingsSection>('general')
const activeSection = settingsWorkbench?.activeSection ?? localActiveSection
const localVisitedSections = ref<Set<SettingsSection>>(new Set(['general']))
const visitedSections = settingsWorkbench?.visitedSections ?? localVisitedSections

function hasVisitedSection(section: SettingsSection): boolean {
  return visitedSections.value.has(section)
}
const appConfig = ref<DesktopAppConfig>({ gateway_url: api.gatewayUrl, source: 'default', managed: false })
const appConfigLoaded = ref(false)
const gatewayUrlDraft = ref(api.gatewayUrl)
const gatewayConfigBusy = ref(false)
const gatewayConnectionState = ref<'idle' | 'testing' | 'ready' | 'failed'>('idle')
const PET_CATALOG_PAGE_SIZE = 48
const petCatalog = ref<PetdexCatalogPet[]>([])
const downloadedPets = ref<DownloadedPet[]>([])
const petCatalogQuery = ref('')
const petCatalogKind = ref('all')
const petCatalogLimit = ref(PET_CATALOG_PAGE_SIZE)
const petLoadMoreRef = ref<HTMLElement | null>(null)
const petCatalogLoading = ref(false)
const petCatalogLoaded = ref(false)
const petActionSlug = ref('')


const downloadedPetBySlug = computed(() => new Map(downloadedPets.value.map((pet) => [pet.slug, pet])))
const petCatalogKinds = computed(() => Array.from(new Set(petCatalog.value.map((pet) => pet.kind))).sort())
const matchingPetCatalog = computed(() => {
  const query = petCatalogQuery.value.trim().toLowerCase()
  return petCatalog.value.filter((pet) => {
    if (petCatalogKind.value !== 'all' && pet.kind !== petCatalogKind.value) return false
    return !query || [pet.displayName, pet.slug, pet.kind, pet.submittedBy]
      .some((value) => value.toLowerCase().includes(query))
  })
})
const visiblePetCatalog = computed(() => matchingPetCatalog.value.slice(0, petCatalogLimit.value))
const canLoadMorePets = computed(() => visiblePetCatalog.value.length < matchingPetCatalog.value.length)

let petLoadMoreObserver: IntersectionObserver | null = null
const stopPetChanged = window.tinadec.pets.onChanged((pet) => {
  downloadedPets.value = downloadedPets.value.map((item) => item.slug === pet.slug ? { ...item, enabled: pet.enabled } : item)
})

function loadMorePets() {
  petCatalogLimit.value = Math.min(matchingPetCatalog.value.length, petCatalogLimit.value + PET_CATALOG_PAGE_SIZE)
}

async function observePetLoadMore() {
  petLoadMoreObserver?.disconnect()
  if (activeSection.value !== 'pets' || !canLoadMorePets.value) return
  await nextTick()
  if (!petLoadMoreRef.value) return
  petLoadMoreObserver = new IntersectionObserver((entries) => {
    if (entries.some((entry) => entry.isIntersecting)) loadMorePets()
  }, { rootMargin: '320px 0px' })
  petLoadMoreObserver.observe(petLoadMoreRef.value)
}

watch([petCatalogQuery, petCatalogKind], () => {
  petCatalogLimit.value = PET_CATALOG_PAGE_SIZE
})
watch([activeSection, () => visiblePetCatalog.value.length, canLoadMorePets], () => {
  void observePetLoadMore()
})
onBeforeUnmount(() => {
  petLoadMoreObserver?.disconnect()
  stopPetChanged()
})

async function loadPets(force = false) {
  petCatalogLoading.value = true
  dismissByKey('pets')
  try {
    const [catalog, downloaded] = await Promise.all([
      window.tinadec.pets.fetchCatalog(force),
      window.tinadec.pets.listDownloaded(),
    ])
    petCatalog.value = catalog
    downloadedPets.value = downloaded
    petCatalogLimit.value = PET_CATALOG_PAGE_SIZE
    petCatalogLoaded.value = true
  } catch (error) {
    petCatalogLoaded.value = false
    status.error({ key: 'pets', source: 'pets', message: error instanceof Error ? error.message : t('settings.petsLoadFailed') })
  } finally {
    petCatalogLoading.value = false
  }
}

function selectSettingsSection(section: SettingsSection) {
  if (settingsWorkbench) settingsWorkbench.selectSection(section)
  else {
    activeSection.value = section
    localVisitedSections.value = new Set([...localVisitedSections.value, section])
  }
}

async function downloadPet(slug: string) {
  petActionSlug.value = slug
  dismissByKey('pets')
  try {
    await window.tinadec.pets.download(slug)
    downloadedPets.value = await window.tinadec.pets.listDownloaded()
    notify.success(t('settings.petDownloaded'))
  } catch (error) {
    notify.error(error, { title: t('settings.petDownloadFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function setPetEnabled(pet: DownloadedPet, enabled: boolean) {
  petActionSlug.value = pet.slug
  dismissByKey('pets')
  try {
    const updated = await window.tinadec.pets.setEnabled(pet.slug, enabled)
    downloadedPets.value = downloadedPets.value.map((item) => item.slug === updated.slug ? updated : item)
    notify.success(`${pet.displayName}: ${enabled ? t('settings.enablePet') : t('settings.disablePet')}`)
  } catch (error) {
    notify.error(error, { title: t('settings.petUpdateFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function openPetFolder(pet: DownloadedPet) {
  petActionSlug.value = pet.slug
  dismissByKey('pets')
  try {
    await window.tinadec.pets.openFolder(pet.slug)
  } catch (error) {
    notify.error(error, { title: t('settings.petUpdateFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

async function removePet(pet: DownloadedPet) {
  if (!await confirm({
    title: t('settings.deletePet'),
    message: t('settings.deletePetConfirmation', { name: pet.displayName }),
    confirmLabel: t('settings.deletePet'),
    cancelLabel: t('settings.cancel'),
    destructive: true
  })) return
  petActionSlug.value = pet.slug
  dismissByKey('pets')
  try {
    await window.tinadec.pets.remove(pet.slug)
    downloadedPets.value = downloadedPets.value.filter((item) => item.slug !== pet.slug)
    notify.success(`${pet.displayName}: ${t('settings.deletePet')}`)
  } catch (error) {
    notify.error(error, { title: t('settings.petUpdateFailed') })
  } finally {
    petActionSlug.value = ''
  }
}

// ---- About page runtime health check ----
const aboutCoreStatus = ref<string>('')
const aboutCoreVersion = ref<string>('')
const aboutGatewayStatus = ref<string>('')
const aboutHealthLoaded = ref(false)

async function checkAboutHealth() {
  dismissByKey('settings-about')
  try {
    const data = await api.health()
    aboutCoreStatus.value = data.status === 'ok' ? 'ok' : ''
    aboutCoreVersion.value = typeof data.version === 'string' ? data.version : ''
    aboutGatewayStatus.value = data.gateway === 'ok' ? 'ok' : ''
    aboutHealthLoaded.value = true
  } catch (error) {
    aboutHealthLoaded.value = false
    aboutCoreStatus.value = ''
    aboutGatewayStatus.value = ''
    status.error({
      key: 'settings-about',
      source: 'settings',
      message: error instanceof Error ? error.message : t('settings.centerLoadFailed'),
      action: { label: t('settings.retry'), run: checkAboutHealth },
    })
  }
}
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
const busy = ref(false)
const loading = ref(false)
const modelCenterLoading = ref(false)
const agentCenterLoading = ref(false)
const modelCenterLoaded = ref(false)
const agentCenterLoaded = ref(false)
const promptContextLoaded = ref(false)
const modelCenterBusy = ref(false)
const agentRuntimeBusy = ref(false)

const showModal = ref(false)
const agentViewMode = ref<'topology' | 'list'>('list')
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
  { key: 'general' as const, icon: Settings2, label: t('settings.general') },
  { key: 'model' as const, icon: KeyRound, label: t('settings.model') },
  { key: 'agents' as const, icon: Workflow, label: t('settings.agents') },
  { key: 'agentEvolution' as const, icon: Dna, label: t('settings.agentEvolution') },
  { key: 'promptContext' as const, icon: Bot, label: t('settings.promptContext') },
  { key: 'promptEngineering' as const, icon: GitBranch, label: t('settings.promptEngineering') },
  { key: 'tools' as const, icon: Terminal, label: t('settings.toolLayer') },
  { key: 'appearance' as const, icon: Palette, label: t('settings.appearance') },
  { key: 'pets' as const, icon: PawPrint, label: t('settings.pets') },
  { key: 'language' as const, icon: Globe, label: t('settings.language') },
  { key: 'apiDocs' as const, icon: FileText, label: t('settings.apiDocs') },
  { key: 'about' as const, icon: Info, label: t('settings.about') },
])

async function loadAppConfig() {
  dismissByKey('gateway-config-load')
  try {
    appConfig.value = await window.tinadec.getAppConfig()
    gatewayUrlDraft.value = appConfig.value.gateway_url
    appConfigLoaded.value = true
  } catch (error) {
    appConfigLoaded.value = false
    status.error({
      key: 'gateway-config-load',
      source: 'gateway',
      message: error instanceof Error ? error.message : t('settings.centerLoadFailed'),
      action: { label: t('settings.retry'), run: loadAppConfig },
    })
  }
}

function normalizedGatewayDraft() {
  const url = new URL(gatewayUrlDraft.value.trim())
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error(t('settings.gatewayUrlInvalid'))
  return url.toString().replace(/\/$/, '')
}

async function testGatewayConnection() {
  dismissByKey('gateway-config')
  gatewayConnectionState.value = 'testing'
  try {
    const gatewayUrl = normalizedGatewayDraft()
    const response = await fetch(`${gatewayUrl}/api/v1/health`, {
      headers: { accept: 'application/json' },
      signal: AbortSignal.timeout(5000)
    })
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`)
    gatewayConnectionState.value = 'ready'
    notify.success({ message: t('settings.gatewayConnectionReady'), source: 'gateway' })
  } catch (error) {
    gatewayConnectionState.value = 'failed'
    status.error({ key: 'gateway-config', source: 'gateway', message: error instanceof Error ? error.message : t('settings.gatewayConnectionFailed') })
  }
}

async function saveGatewayConfiguration() {
  dismissByKey('gateway-config')
  try {
    normalizedGatewayDraft()
  } catch (error) {
    status.error({ key: 'gateway-config', source: 'gateway', message: error instanceof Error ? error.message : t('settings.gatewayUrlInvalid') })
    return
  }
  gatewayConfigBusy.value = true
  dismissByKey('gateway-config')
  try {
    appConfig.value = await window.tinadec.saveGatewayUrl(gatewayUrlDraft.value)
    gatewayUrlDraft.value = appConfig.value.gateway_url
    if (appConfig.value.gateway_url !== api.gatewayUrl) {
      banner.warning({
        key: 'gateway-restart',
        message: t('settings.gatewaySavedRestart'),
        action: { label: t('settings.restartNow'), run: restartDesktop }
      })
    } else {
      clearGatewayRestartBanner()
      notify.success(t('settings.gatewaySaved'))
    }
  } catch (error) {
    notify.error(error, { title: t('settings.gatewaySaveFailed') })
  } finally {
    gatewayConfigBusy.value = false
  }
}

async function resetGatewayConfiguration() {
  gatewayConfigBusy.value = true
  dismissByKey('gateway-config')
  try {
    appConfig.value = await window.tinadec.resetGatewayUrl()
    gatewayUrlDraft.value = appConfig.value.gateway_url
    gatewayConnectionState.value = 'idle'
    if (appConfig.value.gateway_url !== api.gatewayUrl) {
      banner.warning({
        key: 'gateway-restart',
        message: t('settings.gatewayResetRestart'),
        action: { label: t('settings.restartNow'), run: restartDesktop }
      })
    } else {
      clearGatewayRestartBanner()
      notify.success(t('settings.gatewayReset'))
    }
  } catch (error) {
    notify.error(error, { title: t('settings.gatewaySaveFailed') })
  } finally {
    gatewayConfigBusy.value = false
  }
}

function restartDesktop() {
  void window.tinadec.restartApp()
}

function clearGatewayRestartBanner() {
  const existing = notificationItems.value.find((item) => item.key === 'gateway-restart')
  if (existing) dismissNotification(existing.id)
}

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
const topologyAgentLabels = computed(() => Object.fromEntries(
  agents.value.map((agent) => [agent.id, agentTypeLabel(agent.agent_type)])
))
const topologyCandidateLabels = computed(() => Object.fromEntries(
  agentCandidates.value.map((candidate) => [candidate.id, agentTypeLabel(candidate.agent_type)])
))
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
const selectedProviderDetail = computed(() =>
  providers.value.find((provider) => provider.id === selectedProviderDetailId.value) ?? providers.value[0] ?? null
)
const selectedAgent = computed(() =>
  agents.value.find((agent) => agent.id === selectedAgentId.value) ?? null
)
const configuringAgent = computed(() =>
  agents.value.find((agent) => agent.id === configuringAgentId.value) ?? null
)
const planningAgents = computed(() => agents.value.filter((agent) => agent.layer === 'planning'))
const executionAgents = computed(() => agents.value.filter((agent) => agent.layer === 'execution'))
const configuredAgentMode = computed(() => agentModes.value.find((mode) => mode.id === configuringAgent.value?.mode) ?? null)
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
  if (selectedProviderDetailId.value) {
    selectedProviderId.value = providerId
  }
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
    const overview = await api.getModelCenterOverview()
    modelCenterOverview.value = overview
    const instances = providersFromOverview(overview)
    providers.value = instances
    modelReadiness.value = overview.readiness.model ?? null
    modelCatalogReadiness.value = overview.readiness.catalog ?? null

    const selected = instances.find((provider) => provider.id === selectedProviderId.value) ?? instances[0]
    if (selected) {
      selectedProviderId.value = selected.id
    }
    modelCenterLoaded.value = true
  } catch (error) {
    modelCenterLoaded.value = false
    status.error({ key: 'model-center', source: 'models', message: error instanceof Error ? error.message : t('settings.centerLoadFailed'), action: { label: t('settings.retry'), run: loadModelCenter } })
  } finally {
    modelCenterLoading.value = false
  }
}

async function refreshProviderModels(providerInstanceId: string) {
  modelCenterBusy.value = true
  try {
    await api.refreshProviderModels(providerInstanceId)
    await loadModelCenter()
    notify.success(t('settings.refreshModels'))
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

async function loadAgentCenter() {
  agentCenterLoading.value = true
  dismissByKey('agent-center')
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
    const activeAgent = overview.agents.find((agent) => agent.id === configuringAgentId.value)
      ?? overview.agents.find((agent) => agent.id === selectedAgentId.value)
      ?? overview.agents[0]
    if (activeAgent) openAgentConfig(activeAgent)
    agentCenterLoaded.value = true
  } catch (error) {
    agentCenterLoaded.value = false
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

async function loadPromptContextCenter() {
  loading.value = true
  dismissByKey('prompt-context-center')
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
    promptContextLoaded.value = true
  } catch (error) {
    promptContextLoaded.value = false
    status.error({
      key: 'prompt-context-center',
      source: 'prompts',
      message: error instanceof Error ? error.message : t('settings.centerLoadFailed'),
      action: { label: t('settings.retry'), run: loadPromptContextCenter },
    })
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
    notify.success(saved.title)
  } catch (error) {
    notify.error(error, { title: promptForm.title })
  } finally {
    busy.value = false
  }
}

async function deletePromptFragment() {
  if (!promptForm.id || promptForm.is_builtin) return
  const fragmentId = promptForm.id
  const fragmentTitle = promptForm.title
  if (!await confirm({
    title: t('settings.delete'),
    message: `${t('settings.confirmDelete')} ${promptForm.title}?`,
    confirmLabel: t('settings.confirmDelete'),
    cancelLabel: t('settings.cancel'),
    destructive: true
  })) return
  busy.value = true
  try {
    await api.deletePromptFragment(fragmentId)
    promptSelectedFragmentId.value = ''
    await loadPromptContextCenter()
    notify.success(`${fragmentTitle}: ${t('settings.delete')}`)
  } catch (error) {
    notify.error(error, { title: fragmentTitle })
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
    notify.success(cloned.title)
  } catch (error) {
    notify.error(error)
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
  } catch (error) {
    notify.error(error, { title: t('settings.preview') })
  } finally {
    busy.value = false
  }
}

async function updateAgentMode(agent: AgentProfileDto, mode: string) {
  busy.value = true
  try {
    await api.updateAgentMode(agent.id, mode)
    await loadAgentCenter()
    notify.success(agent.name)
  } catch (error) {
    notify.error(error, { title: agent.name })
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
    notify.success(agent.name)
  } catch (error) {
    notify.error(error, { title: agent.name })
  } finally {
    busy.value = false
  }
}

async function saveAgentProfile() {
  const agent = configuringAgent.value
  if (!agent) return
  busy.value = true
  try {
    await api.saveAgent(agent.id, {
      name: agent.name,
      layer: agent.layer,
      agent_type: agent.agent_type,
      mode: agent.mode,
      description: agentEditDescription.value,
      model_route_purpose: agent.model_route_purpose,
      allowed_tools: agentEditTools.value,
      capabilities: agentEditCapabilities.value,
      system_prompt: agentEditSystemPrompt.value || null,
      enabled: agent.enabled
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
    notify.success(agent.name)
  } catch (error) {
    notify.error(error, { title: agent.name })
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
  try {
    await api.saveAgentRuntimeBinding(agent.id, binding)
    await loadAgentCenter()
    notify.success(agent.name)
  } catch (error) {
    notify.error(error, { title: agent.name })
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

function supplierTransportLabel(kind: string) {
  const map: Record<string, string> = {
    http_json: t('settings.transportCloudApi'),
    local_http: t('settings.transportLocalService'),
    cli: 'CLI',
    acp: 'ACP'
  }
  return map[kind] ?? kind
}

function supplierCredentialLabel(kind: string) {
  const map: Record<string, string> = {
    api_key: t('settings.apiKey'),
    'api-key': t('settings.apiKey'),
    cli: t('settings.localCredential'),
    none: t('settings.noCredential')
  }
  return map[kind] ?? kind
}

function supplierSummary(supplier: ModelCenterSupplierDto) {
  const template = findTemplate(supplier.driver)
  if (template) return t(template.summary_key)
  if (supplier.transport_kind === 'local_http') return t('settings.supplierLocalSummary')
  if (supplier.transport_kind === 'cli') return t('settings.supplierCliSummary')
  if (supplier.transport_kind === 'acp') return t('settings.supplierAcpSummary')
  return t('settings.supplierCloudSummary')
}

function providerPresentation(driver: string) {
  return supplierTemplates.value.get(driver) ?? findTemplate(driver)
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

watch(activeSection, (section) => {
  if (section === 'general' && !appConfigLoaded.value) void loadAppConfig()
  else if (section === 'model' && !modelCenterLoaded.value) void loadModelCenter()
  else if ((section === 'agents' || section === 'tools') && !agentCenterLoaded.value) void loadAgentCenter()
  else if (section === 'promptContext' && !promptContextLoaded.value) {
    void Promise.all([
      loadPromptContextCenter(),
      agentCenterLoaded.value ? Promise.resolve() : loadAgentCenter(),
    ])
  } else if (section === 'pets' && !petCatalogLoaded.value) void loadPets()
  else if (section === 'about' && !aboutHealthLoaded.value) void checkAboutHealth()
}, { immediate: true })

const settingsModuleContext = proxyRefs({
  ArrowLeft,
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
  X,
  computed,
  nextTick,
  onBeforeUnmount,
  reactive,
  ref,
  watch,
  useI18n,
  useRouter,
  useTheme,
  api,
  PROVIDER_TEMPLATES,
  findTemplate,
  buildModelCenterRows,
  filterModelCenterRows,
  bindingForAgent,
  legacyRouteWarning,
  modelOptionKey,
  providerTemplateFromSupplier,
  providersFromOverview,
  runtimeSourceSummary,
  codeSuiteTools,
  languageSupportFromTools,
  manifestTools,
  projectTemplatesFromResult,
  sortedAgentLayers,
  sortedRiskPolicies,
  sortedToolSearchResults,
  sortedToolProviders,
  BrandLogo,
  PetPreview,
  UiButton,
  UiInput,
  UiCard,
  UiBadge,
  UiLabel,
  UiSkeleton,
  UiSwitch,
  UiDropdownMenu,
  AgentTopologyCanvas,
  AgentEvolutionPanel,
  PromptEngineeringPanel,
  BackgroundPreview,
  PanelStyleControl,
  useBackground,
  usePanelStyles,
  useNotifications,
  useOptionalSettingsWorkbench,
  t,
  locale,
  router,
  theme,
  setTheme,
  accentColor,
  setAccentColor,
  accentColors,
  notificationItems,
  notify,
  banner,
  confirm,
  dismissNotification,
  status,
  dismissByKey,
  backgroundSettings,
  setBackgroundType,
  setBackgroundSource,
  setBackgroundOpacity,
  setBackgroundBlur,
  setBackgroundSize,
  setBackgroundPosition,
  setBackgroundRepeat,
  selectBackgroundFile,
  resetBackground,
  backgroundSource,
  panelStyle,
  updatePanelStyle,
  resetPanelStyle,
  getPanelStyle,
  getPanelDataAttributes,
  settingsNavStyle,
  settingsNavDataAttrs,
  settingsContentStyle,
  settingsContentDataAttrs,
  settingsPageDataAttrs,
  settingsPageMaterialStyle,
  changeTheme,
  changeAccentColor,
  minimizeWindow,
  maximizeWindow,
  closeWindow,
  openExternal,
  settingsWorkbench,
  localActiveSection,
  activeSection,
  localVisitedSections,
  visitedSections,
  hasVisitedSection,
  appConfig,
  appConfigLoaded,
  gatewayUrlDraft,
  gatewayConfigBusy,
  gatewayConnectionState,
  PET_CATALOG_PAGE_SIZE,
  petCatalog,
  downloadedPets,
  petCatalogQuery,
  petCatalogKind,
  petCatalogLimit,
  petLoadMoreRef,
  petCatalogLoading,
  petCatalogLoaded,
  petActionSlug,
  downloadedPetBySlug,
  petCatalogKinds,
  matchingPetCatalog,
  visiblePetCatalog,
  canLoadMorePets,
  petLoadMoreObserver,
  stopPetChanged,
  loadMorePets,
  observePetLoadMore,
  loadPets,
  selectSettingsSection,
  downloadPet,
  setPetEnabled,
  openPetFolder,
  removePet,
  aboutCoreStatus,
  aboutCoreVersion,
  aboutGatewayStatus,
  aboutHealthLoaded,
  checkAboutHealth,
  modelCenterOverview,
  agentCenterOverview,
  providers,
  modelReadiness,
  modelCatalogReadiness,
  routes,
  agentModes,
  agents,
  agentCandidates,
  availableTools,
  harnessManifest,
  toolLayerReadiness,
  toolSearchResults,
  promptFragments,
  promptPreview,
  projectTemplates,
  selectedProviderId,
  selectedAgentId,
  configuringAgentId,
  modelCenterSection,
  agentRuntimeSelection,
  agentRuntimeProviderId,
  agentRuntimeModelKey,
  agentRuntimeCliId,
  agentRuntimeAcpId,
  agentRuntimeModelQuery,
  agentRuntimeProviderQuery,
  agentRuntimeCliQuery,
  agentRuntimeAcpQuery,
  agentEditTools,
  agentEditCapabilities,
  agentEditSystemPrompt,
  agentEditDescription,
  agentNewCapability,
  selectedProviderDetailId,
  modelProviderFilter,
  modelProviderQuery,
  modelProviderListRef,
  modelDiagnosticsRef,
  busy,
  loading,
  modelCenterLoading,
  agentCenterLoading,
  modelCenterLoaded,
  agentCenterLoaded,
  promptContextLoaded,
  modelCenterBusy,
  agentRuntimeBusy,
  showModal,
  agentViewMode,
  promptSelectedFragmentId,
  promptFilterScope,
  promptFilterCategory,
  promptFilterAgentId,
  promptFilterEnabled,
  promptPreviewAgentId,
  promptPreviewMode,
  promptPreviewSessionId,
  promptPreviewRunId,
  promptPreviewUserContent,
  toolDiscoveryQuery,
  toolDiscoverySource,
  toolDiscoveryRisk,
  toolDiscoveryLoading,
  promptForm,
  providerForm,
  navItems,
  loadAppConfig,
  normalizedGatewayDraft,
  testGatewayConnection,
  saveGatewayConfiguration,
  resetGatewayConfiguration,
  restartDesktop,
  clearGatewayRestartBanner,
  modelCenterSections,
  supplierTemplates,
  currentTemplate,
  chatRoute,
  chatProvider,
  providerReadinessById,
  blockedModelRoutes,
  warningCatalogTemplates,
  catalogReadinessByDriver,
  formFields,
  formPlaceholders,
  modelCenterRows,
  filteredModelCenterRows,
  modelCenterIssueCount,
  firstNeedsKeyProvider,
  agentRuntimeBindings,
  topologyAgentLabels,
  topologyCandidateLabels,
  configuringRuntimeBinding,
  configuringLegacyWarning,
  runtimeBindingWritable,
  runtimeModels,
  runtimeProviders,
  runtimeCliOptions,
  runtimeAcpOptions,
  modelCenterDiagnostics,
  agentCenterDiagnostics,
  filteredRuntimeModels,
  filteredRuntimeProviders,
  filteredRuntimeCliOptions,
  filteredRuntimeAcpOptions,
  selectedProvider,
  selectedProviderDetail,
  selectedAgent,
  configuringAgent,
  planningAgents,
  executionAgents,
  configuredAgentMode,
  manifestToolList,
  manifestProviders,
  manifestAgentLayers,
  manifestRiskPolicies,
  codeSuiteToolList,
  codexPrimitiveTools,
  supportedLanguages,
  warningToolLayerTools,
  warningToolLayerAgents,
  toolSourceOptions,
  toolRiskOptions,
  sortedToolDiscoveryResults,
  promptCategories,
  promptFilteredFragments,
  runtimeQueryMatches,
  centerDiagnosticLabel,
  configuredModelSourceLabel,
  acpRuntimeSourceLabel,
  modelCatalogModeLabel,
  setLocale,
  fillForm,
  applyTemplateDefaults,
  openAddModal,
  openEditModal,
  toggleProviderDetail,
  focusModelProviderList,
  openModelDiagnostics,
  toggleProviderEnabled,
  deleteProvider,
  closeModal,
  loadModelCenter,
  refreshProviderModels,
  probeAcpRuntime,
  loadAgentCenter,
  loadToolDiscovery,
  loadPromptContextCenter,
  selectPromptFragment,
  newPromptFragment,
  promptPayload,
  savePromptFragment,
  deletePromptFragment,
  clonePromptFragment,
  generatePromptPreview,
  updateAgentMode,
  setAgentEnabled,
  saveAgentProfile,
  toggleAgentTool,
  removeAgentCapability,
  addAgentCapability,
  openAgentConfig,
  closeAgentConfig,
  openAgentConfigById,
  runtimeBindingInput,
  saveAgentRuntimeBinding,
  saveProvider,
  connectionKindLabel,
  agentTypeLabel,
  agentLayerLabel,
  agentModeLabel,
  agentModeSummary,
  agentPolicyLabel,
  supplierTransportLabel,
  supplierCredentialLabel,
  supplierSummary,
  providerPresentation,
  candidateStatusLabel,
  statusLabel,
  statusVariant,
  readinessVariant,
  readinessStatusLabel,
})
provideSettingsModuleContext(settingsModuleContext)

import '../settings/settings.css'

withDefaults(defineProps<{ embedded?: boolean; contentOnly?: boolean }>(), {
  embedded: false,
  contentOnly: false,
})
</script>

<template>
<div class="settings-page" :class="{ 'workbench-embedded': embedded, 'workbench-content-only': contentOnly }" :style="settingsPageMaterialStyle" v-bind="settingsPageDataAttrs">
<!-- Background Layer is now rendered globally in App.vue, outside the page transition -->

<!-- Full-width draggable bar for window dragging -->
<div v-if="!embedded" class="top-drag-bar" />
<div v-if="!embedded" class="settings-window-controls">
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
      <nav v-if="!contentOnly" class="settings-nav" :style="settingsNavStyle" v-bind="settingsNavDataAttrs">
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
        <div class="settings-section-wrapper">
        <template v-for="module in settingsModuleDescriptors" :key="module.id">
          <SettingsModuleBoundary
            v-if="hasVisitedSection(module.id)"
            v-show="activeSection === module.id"
            :module-id="module.id"
          >
            <template #default="{ retryKey }">
              <component :is="module.component" :key="retryKey" />
            </template>
          </SettingsModuleBoundary>
        </template>
        </div>
      </div>
    </div>

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
  </div>
</template>
