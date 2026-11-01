<script setup lang="ts">
import {
  Archive,
  ArrowLeft,
  Bot,
  Check,
  Cpu,
  Database,
  FileText,
  Info,
  KeyRound,
  LayoutGrid,
  List,
  Minus,
  MessagesSquare,
  MoreHorizontal,
  Palette,
  PanelRight,
  PawPrint,
  Plus,
  RefreshCw,
  Route,
  Save,
  Search,
  Settings2,
  ShieldCheck,
  Shuffle,
  Square,
  Terminal,
  UserRound,
  Workflow,
  X
} from '@lucide/vue'
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { useStatusLabels } from '@/lib/statusLabels'
import { useRouter } from 'vue-router'
import AboutSection from '@/settings/sections/AboutSection.vue'
import ArchiveTrashSection from '@/settings/sections/ArchiveTrashSection.vue'
import GeneralSection from '@/settings/sections/GeneralSection.vue'
import StorageSection from '@/settings/sections/StorageSection.vue'
import ModelCenterSection from '@/settings/sections/ModelCenterSection.vue'
import ApiDocsSection from '@/settings/sections/ApiDocsSection.vue'
import AppearanceSection from '@/settings/sections/AppearanceSection.vue'
import PersonalSection from '@/settings/sections/PersonalSection.vue'
import PetsSection from '@/settings/sections/PetsSection.vue'
import ToolCenterSection from '@/settings/sections/ToolCenterSection.vue'
import TinaChatSection from '@/settings/sections/TinaChatSection.vue'
import CompanionSection from '@/settings/sections/CompanionSection.vue'
import {
  api as baseApi,
  type AgentCandidateDto,
  type AgentCenterOverviewDto,
  type AgentDefinitionDto,
  type AgentDirectoryItemDto,
  type AgentModeDto,
  type AgentViewDto,
  type AgentRuntimeSelectionKind,
  type AgentVersionDto,
  type PermissionRequestDto,
  type CenterDiagnosticDto,
  type HarnessManifestDto,
  type ToolLayerReadinessReceiptDto,
  type ToolDescriptorDto,
  type ToolSearchResultDto,
  type AgentModeTopologyDto
} from '../api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import {
  bindingFromModelStrategy,
  legacyRouteWarning,
  modelOptionKey,
  runtimeSourceSummary
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
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import CommandPaletteButton from '@/components/CommandPaletteButton.vue'
import { UiButton, UiInput, UiBadge, UiLabel, UiSkeleton, UiSwitch, UiSelectField } from '@/components/ui'
import AgentTopologyCanvas from '@/components/AgentTopologyCanvas.vue'
import AgentEvolutionPanel from '@/components/AgentEvolutionPanel.vue'
import AgentModesPanel from '@/settings/sections/AgentModesPanel.vue'
import PromptEngineeringMerged from '@/settings/sections/PromptEngineeringMerged.vue'
import RuntimeInstancesPanel from '@/settings/sections/RuntimeInstancesPanel.vue'
import AgentPacksPanel from '@/settings/sections/AgentPacksPanel.vue'
import { consumeRequest, pendingAgentId, pendingModeId, pendingPromptId, pendingToolId, pendingToolAgentId, requestToolAgent, pendingModelProviderId, pendingSettingsSection, pendingCompanionId, type CompanionSettingsTab } from '@/lib/pageRequests'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { createSettingsLeaveGuard } from './settingsNavigation'
import { useNotifications } from '@/composables/useNotifications'
import { useAgentAvatars } from '@/composables/useAgentAvatars'
import { useHostAccess } from '@/lib/hostAccess'
import { useRecoverableRead, type ReadContext } from '@/composables/useRecoverableRead'
import { useModelCenter } from '@/composables/useModelCenter'
import { centerDiagnosticLabel as centerDiagnosticText, providerStatusLabel } from '@/settings/modelCenterLabels'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { isAbortError } from '@/lib/isAbortError'
import { graphSeedPackManifest } from '@/agentPacks/GraphSeedPack'

type SettingsSection = 'personal' | 'general' | 'storage' | 'model' | 'agentCenter' | 'companions' | 'tools' | 'tinachat' | 'archive' | 'appearance' | 'companionAvatar' | 'apiDocs' | 'about'

type AgentCenterTab = 'agents' | 'modes' | 'prompts' | 'evolution' | 'runtime'

const modePanelRef = ref<InstanceType<typeof AgentModesPanel> | null>(null)
const promptsPanelRef = ref<InstanceType<typeof PromptEngineeringMerged> | null>(null)
const evolutionPanelRef = ref<InstanceType<typeof AgentEvolutionPanel> | null>(null)
const agentPacksPanelRef = ref<InstanceType<typeof AgentPacksPanel> | null>(null)

/** Lazily refresh per-tab data when a tab becomes active. */
function switchAgentCenterTab(tab: AgentCenterTab) {
  agentCenterTab.value = tab
  // RuntimeInstancesPanel does not self-load on mount; wait one tick for its ref.
  if (tab === 'runtime') void nextTick(() => runtimePanelRef.value?.loadRuntimeInstances())
}

/** Single refresh entry for the whole Agent Center: reloads the active sub-tab. */
function refreshAgentCenterTab(tab: AgentCenterTab = agentCenterTab.value) {
  if (tab === 'modes') void modePanelRef.value?.loadModes()
  else if (tab === 'prompts') void promptsPanelRef.value?.refreshAll()
  else if (tab === 'evolution') void evolutionPanelRef.value?.loadProposals()
  else if (tab === 'runtime') void runtimePanelRef.value?.loadRuntimeInstances()
  else void loadAgentCenter()
}

const { t, te, locale } = useI18n()
const { riskLabel } = useStatusLabels()
const definitionStatusLabel = (status: string) => (te(`agentCenter.filter.${status}`) ? t(`agentCenter.filter.${status}`) : status)
// Shared with the Model Center section so the Agent Center words the same catalog rows identically.
const statusLabel = (status: string) => providerStatusLabel(t, status)
const centerDiagnosticLabel = (diagnostic: CenterDiagnosticDto) => centerDiagnosticText(t, diagnostic)
const { canAccessBackend, reason: hostReason } = useHostAccess()
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

async function closeWindow() {
  if (activeSection.value === 'storage' && storageRef.value && !await storageRef.value.canLeave()) return
  if (activeSection.value === 'tools' && toolCenterRef.value && !await toolCenterRef.value.canLeave()) return
  if (activeSection.value === 'model' && modelCenterRef.value && !await modelCenterRef.value.canLeave()) return
  window.tinadec?.closeWindow?.()
}

function openExternal(url: string) {
  window.open(url, '_blank')
}

const activeSection = ref<SettingsSection>('personal')
const toolCenterRef = ref<{ canLeave(): Promise<boolean> } | null>(null)
const storageRef = ref<{ canLeave(): Promise<boolean> } | null>(null)
const agentCenterTab = ref<AgentCenterTab>('agents')
/**
 * The companion section is mounted on first entry and then kept alive: its reads belong to the
 * section, so unmounting it on every switch would re-read characters the user already saw. A deep
 * link carries `{ id, seq }` — the seq makes the same companion id a fresh request.
 */
const companionsMounted = ref(false)
const companionRequest = ref<{ id: string; seq: number; tab?: CompanionSettingsTab } | null>(null)
let companionRequestSeq = 0
// Pets section moved to settings/sections/PetsSection.vue (D7.2)

/**
 * Sections whose content is a fixed-width column, horizontally centered in the
 * content panel. The remaining sections (model / agentCenter / pets / apiDocs)
 * stay fluid: they are workspaces (tables, canvases, an embedded docs frame)
 * that should use the full available width.
 */
const CENTERED_SECTIONS: ReadonlySet<SettingsSection> = new Set([
  'personal', 'general', 'archive', 'appearance', 'about',
])
const isCenteredSection = computed(() => CENTERED_SECTIONS.has(activeSection.value))

async function selectSettingsSection(section: SettingsSection) {
  if (section !== 'storage' && activeSection.value === 'storage' && storageRef.value && !await storageRef.value.canLeave()) return false
  if (section !== 'tools' && activeSection.value === 'tools' && toolCenterRef.value && !await toolCenterRef.value.canLeave()) return false
  if (section !== 'model' && activeSection.value === 'model' && modelCenterRef.value && !await modelCenterRef.value.canLeave()) return false
  // Mounting is the first read; `v-show` afterwards keeps the already-read section alive.
  if (section === 'companions') companionsMounted.value = true
  activeSection.value = section
  return true
}

// Spatial exit animation — declarative, class-driven.
const settingsExiting = ref(false)
const SETTINGS_EXIT_DURATION_MS = 530

onBeforeRouteLeave(createSettingsLeaveGuard({
  isTools: () => activeSection.value === 'tools' || activeSection.value === 'storage' || activeSection.value === 'model',
  canLeave: () => (activeSection.value === 'storage'
    ? storageRef.value
    : activeSection.value === 'model' ? modelCenterRef.value : toolCenterRef.value)?.canLeave() ?? Promise.resolve(true),
  isExiting: () => settingsExiting.value,
  markExiting: () => { settingsExiting.value = true },
  durationMs: SETTINGS_EXIT_DURATION_MS,
}))

// ---- About section moved to settings/sections/AboutSection.vue (D7.2) ----
// One read/write seam for the model catalog. The agent center consumes the same refs, and the
// composer keeps its own scope-aware default-model reader — this center must not read it again.
const modelCenter = useModelCenter({
  // The agent directory and previews are derived from this catalog, but its own reads are lazy:
  // a model write only wakes it when the user already entered that center.
  onModelsChanged: async () => {
    if (agentCenterRead.loaded.value) await loadAgentCenter()
  },
})
const {
  modelCenterOverview,
  providers,
  routes,
  modelCenterLoaded,
  loadModelCenter,
} = modelCenter
const modelCenterRef = ref<{ canLeave(): Promise<boolean>; focusProvider(providerId: string): Promise<void> } | null>(null)
/** A deep link that arrived before the lazily mounted section existed. */
const pendingProviderFocus = ref<string | null>(null)
const agentCenterOverview = ref<AgentCenterOverviewDto | null>(null)
const agentModes = ref<AgentModeDto[]>([])
const agentDirectory = ref<AgentDirectoryItemDto[]>([])
const agents = ref<AgentViewDto[]>([])
const agentCandidates = ref<AgentCandidateDto[]>([])
const availableTools = ref<ToolDescriptorDto[]>([])
const harnessManifest = ref<HarnessManifestDto | null>(null)
const toolLayerReadiness = ref<ToolLayerReadinessReceiptDto | null>(null)
const toolSearchResults = ref<ToolSearchResultDto[]>([])
const projectTemplates = ref<ProjectTemplateSummary[]>([])
const selectedAgentId = ref('')
const configuringAgentId = ref('')
const requestedAgentId = ref<string | null>(null)
let agentNavigationGeneration = 0
let providerNavigationGeneration = 0
onBeforeUnmount(() => { agentNavigationGeneration++; providerNavigationGeneration++; pendingProviderFocus.value = null })
const agentRuntimeSelection = ref<AgentRuntimeSelectionKind>('inherit')
const agentRuntimeModelKey = ref('')
const agentRuntimeRoutePurpose = ref('')
const agentRuntimeModelQuery = ref('')
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
// Versioned profile surface: published version timeline + pending permission requests.
const agentVersionHistory = ref<AgentVersionDto[]>([])
const agentVersionsLoading = ref(false)
const agentPermissionRequests = ref<PermissionRequestDto[]>([])
const runtimePanelRef = ref<InstanceType<typeof RuntimeInstancesPanel> | null>(null)
const pendingGovernanceCount = computed(() =>
  agentPermissionRequests.value.filter((request) => ['pending', 'awaiting_delegate', 'awaiting_user', 'awaiting_approval'].includes(request.status)).length
)
const busy = ref(false)
const agentDefinitionFailures = ref<Record<string, ErrorState>>({})
/** Definitions already read for this render; anything else is fetched when its agent is opened. */
const agentDefinitionLoadedIds = ref<Set<string>>(new Set())
const agentDefinitionLoadingId = ref('')
const agentReadDiagnostics = ref<Array<{ source: string; error: ErrorState }>>([])
const agentRuntimeBusy = ref(false)

/**
 * Agent avatars: the pet an agent wears. This is local cosmetic state, not part of the agent's
 * Core definition, so it stays editable even for built-in agents.
 */
const {
  pool: agentAvatarPool,
  revision: agentAvatarRevision,
  preferenceFor: agentAvatarPreferenceFor,
  agentPreviewSlug,
  setAgentPreference: setAgentAvatarPreference,
} = useAgentAvatars()
const agentAvatarBusy = ref(false)

/**
 * Preferences are keyed by slug, never by id: a sub-agent in the feed only carries `agentSlug`,
 * and settings and the conversation must agree on which agent owns a face.
 */
function agentAvatarKey(agent: AgentViewDto | null | undefined): string {
  if (!agent) return ''
  return agent.slug || agentDirectory.value.find((item) => item.id === agent.id)?.slug || agent.id
}

function agentAvatarSlug(agent: AgentViewDto | null | undefined): string {
  // Touch the revision so a pool refresh (download/delete) re-renders every face.
  void agentAvatarRevision.value
  return agentPreviewSlug(agentAvatarKey(agent))
}

const configuringAvatarPreference = computed(() => (
  configuringAgent.value ? agentAvatarPreferenceFor(agentAvatarKey(configuringAgent.value)) : { mode: 'random' as const }
))

async function chooseAgentAvatar(mode: 'random' | 'fixed', petSlug?: string) {
  const agent = configuringAgent.value
  if (!agent) return
  agentAvatarBusy.value = true
  try {
    await setAgentAvatarPreference(agentAvatarKey(agent), mode === 'fixed' && petSlug
      ? { mode: 'fixed', petSlug }
      : { mode: 'random' })
  } catch (error) {
    notify.error(error, { title: t('settings.agentAvatarSaveFailed') })
  } finally {
    agentAvatarBusy.value = false
  }
}

const agentViewMode = ref<'topology' | 'list'>('list')
const toolDiscoveryQuery = ref('')
const toolDiscoverySource = ref('all')
const toolDiscoveryRisk = ref('all')
const toolDiscoveryLoading = ref(false)

const navItems = computed(() => [
  { key: 'personal' as const, icon: UserRound, label: t('settings.personal') },
  { key: 'general' as const, icon: Settings2, label: t('settings.general') },
  { key: 'storage' as const, icon: Database, label: t('settings.storage') },
  { key: 'model' as const, icon: KeyRound, label: t('settings.model') },
  { key: 'agentCenter' as const, icon: Workflow, label: t('settings.agentCenter') },
  { key: 'companions' as const, icon: Bot, label: t('settings.companions.title') },
  { key: 'tools' as const, icon: Terminal, label: t('settings.toolLayer') },
  { key: 'tinachat' as const, icon: MessagesSquare, label: t('tinaChat.manage') },
  { key: 'archive' as const, icon: Archive, label: t('settings.archiveTrash') },
  { key: 'appearance' as const, icon: Palette, label: t('settings.appearance') },
  { key: 'companionAvatar' as const, icon: PawPrint, label: t('settings.pets') },
  { key: 'apiDocs' as const, icon: FileText, label: t('settings.apiDocs') },
  { key: 'about' as const, icon: Info, label: t('settings.about') },
])

// Gateway/dispatch config moved to settings/sections/GeneralSection.vue (D7.2)
const agentRuntimeBindings = computed(() =>
  Object.fromEntries(agents.value.map((agent) => [agent.id, bindingFromModelStrategy(agent, agent.model_binding)]))
)
const topologyAgentLabels = computed(() => Object.fromEntries(
  agents.value.map((agent) => [agent.id, agentTypeLabel(agent.agent_type)])
))
const topologyCandidateLabels = computed(() => Object.fromEntries(
  agentCandidates.value.map((candidate) => [candidate.id, agentTypeLabel(candidate.agent_type)])
))
const configuringRuntimeBinding = computed(() => {
  const agent = agents.value.find((item) => item.id === configuringAgentId.value)
  return agent ? bindingFromModelStrategy(agent, agent.model_binding) : null
})
const configuringLegacyWarning = computed(() => legacyRouteWarning(configuringRuntimeBinding.value))
const configuringDirectoryItem = computed(() =>
  agentDirectory.value.find((item) => item.id === configuringAgentId.value) ?? null
)

function agentSourceKindLabel(kind: string): string {
  if (kind === 'custom') return t('settings.agentSourceCustom')
  if (kind === 'pack') return t('settings.agentSourcePack')
  if (kind === 'bootstrap') return t('settings.agentSourceBootstrap')
  if (kind === 'missing_reference') return t('settings.agentSourceMissing')
  return kind
}

function agentSourceKindFor(agentId: string): string {
  return agentDirectory.value.find((item) => item.id === agentId)?.source_kind ?? 'custom'
}

/** Summarize one effective preview (per mode:node) into a compact evidence line. */
function agentPreviewLine(modeSlug: string, nodeKey: string, preview: { expected_selection?: { provider_instance_id?: string; model?: string | null } | null }): string {
  const selection = preview.expected_selection
  const target = selection
    ? `${selection.provider_instance_id ?? '—'}${selection.model ? ` · ${selection.model}` : ''}`
    : '—'
  return `${modeSlug}:${nodeKey} → ${target}`
}

/**
 * 「模型来源」摘要。inherit 态也要有证据：Core 的 `effective_previews`
 * 已经解析出实际 provider+model，这里把它喂给 runtimeSourceSummary，
 * 免得「跟随默认」永远显示成「尚未解析」。
 */
function agentRuntimeSummary(agentId: string | null | undefined): string {
  if (!agentId) return ''
  const item = agentDirectory.value.find((entry) => entry.id === agentId)
  const binding = agentRuntimeBindings.value[agentId]
  const resolveName = (providerInstanceId: string) => providers.value.find((provider) => provider.id === providerInstanceId)?.display_name ?? null
  // fixed/route 绑定的摘要此前退化成裸 provider GUID：bindingFromRuntimeOverride 不填
  // provider_display_name，而 runtimeSourceSummary 只在 inherit 分支用 providerName 回调。
  // 在调用侧 hydrate display_name（不改带钉住测试的 runtimeCenterView.ts），让「指定模型」
  // 保存后摘要立即显示人类可读的 provider 名，而不是看起来「没变化」的 GUID。
  const hydrated = binding && !binding.provider_display_name && binding.provider_instance_id
    ? { ...binding, provider_display_name: resolveName(binding.provider_instance_id) }
    : binding
  return runtimeSourceSummary(hydrated, item?.effective_previews ?? null, resolveName)
}

function agentInvocationLine(invocation: { provider_instance_id: string; model?: string | null; status: string; completed_at?: string | null }): string {
  const model = invocation.model ? ` · ${invocation.model}` : ''
  const time = invocation.completed_at ? ` · ${invocation.completed_at}` : ''
  return `${invocation.provider_instance_id}${model} · ${invocation.status}${time}`
}
const runtimeModels = computed(() => agentCenterOverview.value?.runtime_sources.models ?? modelCenterOverview.value?.models ?? [])
const runtimeCliOptions = computed(() => agentCenterOverview.value?.runtime_sources.cli_runtimes ?? modelCenterOverview.value?.cli_runtimes ?? [])
const runtimeAcpOptions = computed(() => agentCenterOverview.value?.runtime_sources.acp_runtimes ?? modelCenterOverview.value?.acp_runtimes ?? [])
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
  agentRuntimeModelQuery.value,
  runtime.display_name,
  runtime.runtime_id,
  runtime.driver,
  runtime.status,
  runtime.binary_path,
  runtime.home_path
)))
const filteredRuntimeAcpOptions = computed(() => runtimeAcpOptions.value.filter((runtime) => runtimeQueryMatches(
  agentRuntimeModelQuery.value,
  runtime.display_name,
  runtime.runtime_id,
  runtime.source,
  runtime.driver,
  runtime.status,
  runtime.command
)))

const selectedAgent = computed(() =>
  agents.value.find((agent) => agent.id === selectedAgentId.value) ?? null
)
const configuringAgent = computed(() =>
  agentDefinitionFailures.value[configuringAgentId.value] ? null
    : agents.value.find((agent) => agent.id === configuringAgentId.value) ?? null
)
function normalizeAgentLayer(layer: unknown): 'operation' | 'execution' {
  const v = String(layer ?? '').trim().toLowerCase();
  if (v === 'planning') return 'operation';
  return v === 'execution' ? 'execution' : (v as 'operation' | 'execution');
}
const planningAgents = computed(() => agents.value.filter((agent) => normalizeAgentLayer(agent.layer) === 'operation'))
const executionAgents = computed(() => agents.value.filter((agent) => normalizeAgentLayer(agent.layer) === 'execution'))
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

const agentCenterRead = useRecoverableRead(loadAgentCenterData, {
  lazy: true,
  onSuccess: () => dismissByKey('agent-center'),
  onFailure: error => status.error(error, { key: 'agent-center', source: 'agents', action: { label: t('settings.retry'), run: loadAgentCenter } }),
})
const agentCenterLoading = agentCenterRead.loading
const agentCenterLoaded = agentCenterRead.loaded
const agentCenterFailure = agentCenterRead.failure.error
function loadAgentCenter(): Promise<void> {
  return agentCenterRead.refresh()
}
// The centers read lazily (see their `useRecoverableRead` options): entering a section is what
// asks for its data the first time. `loaded` keeps this to once per mount — later section
// switches must not re-fetch, and explicit refresh paths still go through load*/refresh.
watch(activeSection, section => {
  if (section === 'model' && !modelCenterLoaded.value) void loadModelCenter()
  else if (section === 'agentCenter' && !agentCenterRead.loaded.value) void loadAgentCenter()
}, { flush: 'post' })
async function loadAgentCenterData(context: ReadContext) {
  const { signal, isCurrent } = context
  // getAgentCenterOverview is a deleted 404 route (docs/app-core-ui.md §4.8).
  // Load the versioned directory + full definitions directly; overview-only projections degrade.
      const partial: Array<{ source: string; error: ErrorState }> = []
      function preserve<T>(source: string, fallback: T): (error: unknown) => T {
        return error => { if (isCurrent()) partial.push({ source, error: toErrorState(error) }); return fallback }
      }
      const [directory, modes, candidates, toolReadiness, packDetail] = await Promise.all([
        api.listAgents({ signal }),
        api.listAgentModes({ signal }).catch(preserve('agent-modes', agentModes.value)),
        api.listAgentCandidates({ signal }).catch(preserve('agent-candidates', agentCandidates.value)),
        api.getToolLayerReadiness({ signal }).catch(preserve('tool-layer-readiness', toolLayerReadiness.value)),
        api.getAgentPack(graphSeedPackManifest.metadata.pack_id, { signal }).catch(() => null),
      ])
      if (!isCurrent()) return
      const managedAgentIds = new Set(
        (packDetail?.resources ?? [])
          .filter((resource) => resource.kind === 'agent' && resource.logical_entity_id)
          .map((resource) => resource.logical_entity_id!),
      )
      // The directory *is* the list: source, usage, preview and invocation evidence all ride
      // on it, so the rail is painted from this read alone. A full definition (prompt, tools,
      // capabilities) is the inspector's business and is read on demand — pulling every
      // definition in waves of four before the first row could render was the whole cost.
      const failures: Record<string, ErrorState> = {}
      const preserveDraft = agentEditorBaseline !== '' && agentEditorSnapshot() !== agentEditorBaseline
      // A dirty editor keeps the definition it was seeded from: dropping its provenance would
      // unmount the very editor the user is typing in.
      if (!preserveDraft) agentDefinitionLoadedIds.value = new Set()
      agentDefinitionLoadingId.value = ''
      const previousAgents = new Map(agents.value.map(agent => [agent.id, agent]))
      if (preserveDraft && !directory.some(item => item.id === configuringAgentId.value)) {
        failures[configuringAgentId.value] = toErrorState(new Error(t('settings.agentDraftSourceMissing')))
        partial.push({ source: previousAgents.get(configuringAgentId.value)?.name ?? configuringAgentId.value, error: failures[configuringAgentId.value]! })
      }
      agentDefinitionFailures.value = failures
      agentCenterOverview.value = null
      agentModes.value = modes
      agentDirectory.value = directory
      // Rows carry the directory projection plus whatever definition was already read for that
      // agent; the inspector fills the rest in when it opens. Until then the card shows its
      // source kind as the secondary line instead of an invented empty description.
      agents.value = directory.map((item) => {
        const previous = previousAgents.get(item.id)
        return {
          id: item.id,
          slug: item.slug,
          display_name: item.display_name,
          name: item.display_name ?? item.slug,
          layer: item.layer,
          role: item.role,
          agent_type: item.role,
          mode: '',
          description: previous?.description ?? '',
          model_route_purpose: item.configured_strategy?.route_purpose ?? '',
          allowed_tools: previous?.allowed_tools ?? [],
          capabilities: previous?.capabilities ?? [],
          system_prompt: previous?.system_prompt ?? null,
          enabled: item.enabled,
          is_built_in: item.managed || item.source_kind !== 'custom' || managedAgentIds.has(item.id),
          model_strategy: item.configured_strategy as unknown as Record<string, unknown>,
          // 用户级运行时绑定：这才是「在智能体中心设过的模型」。缺了它，保存成功后
          // 重新 seed 会拿未变的定义策略把选择打回原样（症状：设置成功但立刻回弹）。
          model_binding: item.model_binding ?? null,
          status: item.status,
          revision: item.revision,
          version: item.version,
          updated_at: item.updated_at ?? null,
        }
      })
      agentCandidates.value = candidates
      toolLayerReadiness.value = toolReadiness
      const activeAgent = requestedAgentId.value
        ? agents.value.find((agent) => agent.id === requestedAgentId.value)
        : agents.value.find((agent) => agent.id === configuringAgentId.value)
          ?? agents.value.find((agent) => agent.id === selectedAgentId.value)
          ?? agents.value[0]
      if (activeAgent && !preserveDraft) {
        await openAgentConfigFromDirectory(activeAgent.id)
      }
      // Keep the definition failure this read just produced (if any) instead of replacing it.
      const readSources = new Set(partial.map(item => item.source))
      agentReadDiagnostics.value = [...partial, ...agentReadDiagnostics.value.filter(item => !readSources.has(item.source))]
      // The directory is already on screen; auxiliary catalog reads only feed the inspector's
      // tool picker, so they are settled in the background instead of holding up this read.
      const auxiliary = Promise.all([
        (async () => {
          try {
            const manifest = await api.getHarnessManifest({ signal })
            if (!isCurrent()) return
            harnessManifest.value = manifest
            availableTools.value = manifest.tools
          } catch (error) {
            if (!isCurrent()) return
            try {
              const tools = await api.listTools({ signal })
              if (!isCurrent()) return
              harnessManifest.value = null
              availableTools.value = tools
            } catch (error) { preserve('tools', availableTools.value)(error) }
          }
        })(),
        api.executeCodeTool('project_templates', {}, { signal }).then(result => {
          if (isCurrent()) projectTemplates.value = projectTemplatesFromResult(result)
        }).catch(preserve('project-templates', null)),
      ])
      // The rail is already painted and the inspector resolves on its own; these only feed
      // secondary catalogs, so the read finishes here instead of waiting on them.
      void auxiliary.then(() => {
        if (!isCurrent()) return
        const known = new Set(partial.map(item => item.source))
        agentReadDiagnostics.value = [...partial, ...agentReadDiagnostics.value.filter(item => !known.has(item.source))]
      })
      if (isCurrent()) void loadToolDiscovery(context)
}

async function loadToolDiscovery(context?: ReadContext) {
  if (!canAccessBackend.value) return
  toolDiscoveryLoading.value = true
  try {
    const results = await api.searchTools({
      query: toolDiscoveryQuery.value.trim() || undefined,
      source: toolDiscoverySource.value === 'all' ? undefined : toolDiscoverySource.value,
      risk: toolDiscoveryRisk.value === 'all' ? undefined : toolDiscoveryRisk.value,
      limit: 10
    }, context ? { signal: context.signal } : undefined)
    if (!context || context.isCurrent()) toolSearchResults.value = results
  } catch (error) {
    if (!context || context.isCurrent()) agentReadDiagnostics.value = [...agentReadDiagnostics.value,
      { source: 'tool-search', error: toErrorState(error) }]
  } finally {
    if (!context || context.isCurrent()) toolDiscoveryLoading.value = false
  }
}

function errorStatus(error: unknown): unknown {
  return error && typeof error === 'object' ? (error as { status?: unknown }).status : undefined
}

function agentSaveErrorMessage(error: unknown): string {
  const msg = error instanceof Error ? error.message : String(error)
  if (isRevisionConflict(error)) return t('settings.agentConflict')
  if (errorStatus(error) === 409 || msg.includes('409') || msg.toLowerCase().includes('built-in')) return t('settings.agentBuiltInConflict')
  return msg
}

function isRevisionConflict(error: unknown): boolean {
  const msg = error instanceof Error ? error.message : String(error)
  return errorStatus(error) === 412 || errorStatus(error) === 428
    || msg.includes('412') || msg.toLowerCase().includes('revision') || msg.includes('Precondition')
}

/** One versioned write attempt: update draft (If-Match revision) then publish a new immutable version. */
async function writeAgentDraft(body: Partial<AgentDefinitionDto>, agentId: string, revision: number | null | undefined) {
  const draft = await api.updateAgentDraft(agentId, body, revision != null ? String(revision) : null)
  await api.publishAgent(agentId, draft.revision != null ? String(draft.revision) : null)
}

/** 412 resolution dialog: reload the latest state, or force-overwrite on a fresh revision. */
async function resolveAgentConflict(agentId: string, body: Partial<AgentDefinitionDto>) {
  const overwrite = await confirm({
    title: t('settings.agentConflictTitle'),
    message: t('settings.agentConflictMessage'),
    confirmLabel: t('settings.agentConflictOverwrite'),
    cancelLabel: t('settings.agentConflictRefresh')
  })
  await loadAgentCenter()
  if (!overwrite) return
  const latest = agents.value.find((item) => item.id === agentId)
  if (!latest) return
  try {
    await writeAgentDraft(body, latest.id, latest.revision)
    await loadAgentCenter()
    notify.success(latest.name)
  } catch (retryError) {
    notify.error(new Error(isRevisionConflict(retryError) ? t('settings.agentConflict') : agentSaveErrorMessage(retryError)), { title: latest.name })
  }
}

/** Shared versioned write path; 412 revision conflicts open the conflict dialog instead of a generic toast. */
async function publishAgentDraft(
  agent: AgentViewDto,
  body: Partial<AgentDefinitionDto>,
  revision: number | null | undefined,
  successTitle: string
) {
  try {
    await writeAgentDraft(body, agent.id, revision)
  } catch (error) {
    if (!isRevisionConflict(error)) throw error
    await resolveAgentConflict(agent.id, body)
    return
  }
  await loadAgentCenter()
  notify.success(successTitle)
}

async function setAgentEnabled(agent: AgentViewDto, enabled: boolean) {
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

async function cloneAgent() {
  const agent = configuringAgent.value
  if (!agent) return
  agentCloneBusy.value = true
  try {
    const newName = t('common.copyName', { name: agent.name })
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
    const cloned = agents.value.find((a) => (a as AgentViewDto & { display_name?: string }).display_name === newName)
      ?? agents.value.find((a) => a.name === newName)
    if (cloned) openAgentConfig(cloned)
    notify.success(newName)
  } catch (error) {
    notify.error(error, { title: t('settings.cloneAgent') })
  } finally {
    agentCloneBusy.value = false
  }
}

async function openGraphSeedPackCloneFlow() {
  agentCenterTab.value = 'agents'
  if (!agents.value.some((agent) => agent.is_built_in)) await loadAgentCenter()
  const packSlugs = new Set(graphSeedPackManifest.resources.agents.map((agent) => agent.slug))
  const managedAgent = agents.value.find((agent) =>
    agent.is_built_in && packSlugs.has(agent.slug ?? agent.name),
  )
  if (!managedAgent) {
    status.warning({
      key: 'graph-seed-pack-clone',
      source: 'GraphSeedPack',
      message: t('agentPack.managedReadOnly'),
    })
    return
  }
  openAgentConfig(managedAgent)
}

async function saveAgent() {
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

let agentEditorBaseline = ''
function agentEditorSnapshot(): string {
  return JSON.stringify([configuringAgentId.value, agentEditTools.value, agentEditCapabilities.value,
    agentEditSystemPrompt.value, agentEditDescription.value, agentRuntimeSelection.value,
    agentRuntimeModelKey.value, agentRuntimeRoutePurpose.value])
}
/**
 * The one place a definition is read. The editor is seeded from the merge in
 * `mergeAgentDefinition`, and only then does the panel open — a failed read leaves the panel
 * closed with a retry, never an invented empty prompt/tool list.
 */
async function ensureAgentDefinition(agentId: string): Promise<AgentViewDto | null> {
  if (agentDefinitionLoadedIds.value.has(agentId)) return agents.value.find((item) => item.id === agentId) ?? null
  if (agentDefinitionLoadingId.value === agentId) return null
  agentDefinitionLoadingId.value = agentId
  try {
    const definition = await api.getAgent(agentId)
    const index = agents.value.findIndex((item) => item.id === agentId)
    // The directory may have been replaced while this was out; a row that is gone stays gone.
    if (index < 0) return null
    agents.value = agents.value.map((item, position) => position === index ? mergeAgentDefinition(item, definition) : item)
    agentDefinitionLoadedIds.value = new Set([...agentDefinitionLoadedIds.value, agentId])
    const failures = { ...agentDefinitionFailures.value }
    if (failures[agentId]) { delete failures[agentId]; agentDefinitionFailures.value = failures }
    return agents.value[index] ?? null
  } catch (error) {
    if (isAbortError(error)) return null
    agentDefinitionFailures.value = { ...agentDefinitionFailures.value, [agentId]: toErrorState(error) }
    agentReadDiagnostics.value = [...agentReadDiagnostics.value.filter(item => item.source !== agentId),
      { source: agents.value.find(item => item.id === agentId)?.name ?? agentId, error: toErrorState(error) }]
    return null
  } finally {
    if (agentDefinitionLoadingId.value === agentId) agentDefinitionLoadingId.value = ''
  }
}

/** Application fields the directory cannot supply; the directory keeps ownership of the rest. */
function mergeAgentDefinition(row: AgentViewDto, definition: AgentDefinitionDto): AgentViewDto {
  return {
    ...row,
    description: definition.description ?? row.description ?? '',
    allowed_tools: Array.isArray(definition.tool_scope) ? definition.tool_scope as string[] : row.allowed_tools ?? [],
    capabilities: definition.capabilities ?? row.capabilities ?? [],
    system_prompt: definition.system_prompt ?? null,
    revision: definition.revision ?? row.revision ?? null,
    version: definition.version ?? row.version ?? null,
    status: definition.status ?? row.status,
  }
}

/** Open an agent by id from the directory, reading its definition first when needed. */
async function openAgentConfigFromDirectory(agentId: string): Promise<void> {
  selectedAgentId.value = agentId
  const loaded = await ensureAgentDefinition(agentId)
  // A failed read leaves the failure (and its retry) in the inspector; never open an empty editor.
  if (loaded) openAgentConfig(loaded)
  else if (agentDefinitionFailures.value[agentId]) configuringAgentId.value = agentId
}

function openAgentConfig(agent: AgentViewDto) {
  selectedAgentId.value = agent.id
  if (agentDefinitionFailures.value[agent.id]) { configuringAgentId.value = agent.id; return }
  // The rail is painted before any definition exists; opening one is what reads it.
  if (!agentDefinitionLoadedIds.value.has(agent.id)) { void openAgentConfigFromDirectory(agent.id); return }
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
  // 从运行时绑定 seed（而不是 agent 定义策略）：绑定写入不改定义，只读定义会回弹。
  const binding = bindingFromModelStrategy(agent, agent.model_binding)
  agentRuntimeSelection.value = binding?.selection_kind ?? 'inherit'
  agentRuntimeModelKey.value = binding?.provider_instance_id
    // CLI/ACP 运行时的 fixed 绑定没有 model：此时 key 就是裸 provider id
    // （与 agentModelStrategy() 的 CLI/ACP 分支同一把钥匙）。
    ? (binding.model_id ? modelOptionKey(binding.provider_instance_id, binding.model_id) : binding.provider_instance_id)
    // 无绑定（跟随默认）时保持空选择，不再猜 runtimeModels[0]：列表首项是任意模型，
    // 在 runtimeModels 尚未加载/为空时还会把刚保存的选择打回原样（问题 1 的回弹竞态）。
    : ''
  agentRuntimeRoutePurpose.value = binding?.route_purpose
    || (agent as AgentViewDto & { model_route_purpose?: string }).model_route_purpose
    || routes.value[0]?.purpose
    || ''
  agentRuntimeModelQuery.value = ''
  agentEditorBaseline = agentEditorSnapshot()
  void loadAgentVersionHistory(agent.id)
  nextTick(() => {
    if (!window.matchMedia('(max-width: 760px)').matches) return
    const panel = document.querySelector('.agent-detail-panel')
    panel?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  })
}

/** Published version timeline for the configured agent (immutable snapshots). */
async function loadAgentVersionHistory(agentId: string) {
  agentVersionsLoading.value = true
  try {
    const versions = await api.listAgentVersions(agentId)
    if (configuringAgentId.value === agentId) agentVersionHistory.value = versions
  } catch {
    if (configuringAgentId.value === agentId) agentVersionHistory.value = []
  } finally {
    if (configuringAgentId.value === agentId) agentVersionsLoading.value = false
  }
}

/** Pending governance requests surfaced in the inspector (approval visibility). */
async function loadAgentPermissionRequests() {
  try {
    agentPermissionRequests.value = await api.listPermissionRequests()
  } catch {
    agentPermissionRequests.value = []
  }
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
  if (agentRuntimeSelection.value === 'route') {
    return agentRuntimeRoutePurpose.value ? { kind: 'route', route_purpose: agentRuntimeRoutePurpose.value } : null
  }
  if (agentRuntimeSelection.value === 'fixed_model') {
    const selected = runtimeModels.value.find((model) =>
      modelOptionKey(model.provider_instance_id, model.model_id) === agentRuntimeModelKey.value
    )
    if (selected) {
      return { kind: 'fixed', provider_instance_id: selected.provider_instance_id, model: selected.model_id }
    }
    // CLI/ACP provider instances participate in `fixed` and may omit the model.
    const runtime = [...runtimeCliOptions.value, ...runtimeAcpOptions.value]
      .find((item) => item.provider_instance_id === agentRuntimeModelKey.value)
    return runtime ? { kind: 'fixed', provider_instance_id: runtime.provider_instance_id, model: null } : null
  }
  return { kind: 'inherit' }
}

function agentStrategySaveable(): boolean {
  return agentModelStrategy() !== null
}

async function saveAgentModelStrategy(agent: AgentViewDto) {
  const strategy = agentModelStrategy()
  if (!strategy) return
  agentRuntimeBusy.value = true
  try {
    // 运行时绑定（配置体验改造 A）：覆盖记录，pack 管理的智能体同样可写，
    // 不再走 draft/publish，也就没有 412/409 managed_resource_read_only。
    // 三档原样透传。此前 route 被静默降级成 inherit，用户选「按用途路由」保存后
    // 会变成「跟随默认」——Core 现已支持 mode='route' + route_purpose。
    // The endpoint replaces the whole binding: the tool-scope override goes back as it was, or
    // saving a model would clear it.
    const toolScope = agent.model_binding?.tool_scope_override
    const saved = await api.putAgentRuntimeBinding(agent.id, {
      mode: strategy.kind as 'inherit' | 'route' | 'fixed',
      provider_instance_id: (strategy as { provider_instance_id?: string }).provider_instance_id ?? null,
      model: (strategy as { model?: string | null }).model ?? null,
      route_purpose: (strategy as { route_purpose?: string }).route_purpose ?? null,
      ...(Array.isArray(toolScope) ? { tool_scope: toolScope } : {}),
    })
    // 定向 patch（问题 1 修复）：PUT 响应体已是权威的最新绑定（含 revision/updated_at），
    // 直接写回 agents 那一行并据其重新 seed 选择，不再 loadAgentCenter() 全量重载——
    // 后者会触发 ~30 个往返、无条件 openAgentConfig re-seed，并在 runtimeModels 尚未
    // 加载时把选择打回列表首项（症状：设置成功但立刻回弹）。
    const patched: AgentViewDto = { ...agent, model_binding: saved as unknown as AgentViewDto['model_binding'] }
    const index = agents.value.findIndex((item) => item.id === agent.id)
    if (index >= 0) agents.value[index] = patched
    openAgentConfig(patched)
    notify.success(t('settings.runtimeBindingSaved', { name: agent.name }))
  } catch (error) {
    notify.error(new Error(agentSaveErrorMessage(error)), { title: agent.name })
  } finally {
    agentRuntimeBusy.value = false
  }
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

function candidateStatusLabel(status: string) {
  return status === 'proposed' ? t('settings.candidateProposed') : status
}

// The spotlight hands deep-links over through the request channel: a section
// row lands straight, a provider row opens the model section and expands the
// provider once the catalog has actually loaded (consumeRequest fires before
// the first load finishes, so this one waits for the row to exist).
consumeRequest(pendingSettingsSection, (section) => {
  selectSettingsSection(section as SettingsSection)
})
// The companion centre / failure recovery opens one companion: switch the section and hand the
// id (and, when the caller meant a specific panel, the editor tab) to the still-mounted section.
consumeRequest(pendingCompanionId, async (request) => {
  companionRequest.value = {
    id: request.id,
    seq: ++companionRequestSeq,
    ...(request.tab ? { tab: request.tab } : {}),
  }
  await selectSettingsSection('companions')
})
consumeRequest(pendingModelProviderId, async (providerId) => {
  const generation = ++providerNavigationGeneration
  if (!await selectSettingsSection('model')) return
  if (!providers.value.some((provider) => provider.id === providerId)) await loadModelCenter()
  if (generation !== providerNavigationGeneration) return
  if (!providers.value.some((provider) => provider.id === providerId)) {
    notify.error(t('app.loadFailedMessage'), { title: t('app.loadFailed'), key: 'search-navigation' })
    return
  }
  // The section mounts behind a leaving-sibling transition, so the ref may not exist yet.
  if (modelCenterRef.value) void modelCenterRef.value.focusProvider(providerId)
  else pendingProviderFocus.value = providerId
})
watch(modelCenterRef, (section) => {
  if (!section || !pendingProviderFocus.value) return
  const providerId = pendingProviderFocus.value
  pendingProviderFocus.value = null
  void section.focusProvider(providerId)
})
consumeRequest(pendingAgentId, async (agentId) => {
  const generation = ++agentNavigationGeneration
  if (!await selectSettingsSection('agentCenter')) return
  switchAgentCenterTab('agents')
  agentViewMode.value = 'list'
  selectedAgentId.value = agentId
  configuringAgentId.value = agentId
  requestedAgentId.value = agentId
  if (!agents.value.some((item) => item.id === agentId)) await loadAgentCenter()
  if (generation !== agentNavigationGeneration) return
  const agent = agents.value.find((item) => item.id === agentId)
  requestedAgentId.value = null
  if (agent) {
    openAgentConfig(agent)
  } else {
    notify.error(t('app.loadFailedMessage'), { title: t('app.loadFailed'), key: 'search-navigation' })
  }
})
// Child panels own these request IDs; activate their host before they consume.
watch(pendingModeId, async (id) => {
  if (!id) return
  if (!await selectSettingsSection('agentCenter')) return
  switchAgentCenterTab('modes')
}, { immediate: true })
watch(pendingPromptId, async (id) => {
  if (!id) return
  if (!await selectSettingsSection('agentCenter')) return
  switchAgentCenterTab('prompts')
}, { immediate: true })
watch(pendingToolId, (id) => {
  if (id) selectSettingsSection('tools')
}, { immediate: true })
watch(pendingToolAgentId, (id) => {
  if (id) void selectSettingsSection('tools')
}, { immediate: true })

import '../settings/settings.css'
</script>

<template>
<div class="settings-page" :class="{ 'settings-exiting': settingsExiting }" :style="settingsPageMaterialStyle" v-bind="settingsPageDataAttrs">
<!-- Background Layer is now rendered globally in App.vue, outside the page transition -->

<!-- Full-width draggable bar for window dragging -->
<div class="top-drag-bar" />
<div class="settings-window-controls">
      <CommandPaletteButton />
      <UiButton variant="ghost" size="icon" class="window-btn minimize" :title="t('app.minimize')" :aria-label="t('app.minimize')" @click="minimizeWindow">
        <Minus :size="14" aria-hidden="true" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn maximize" :title="t('app.maximize')" :aria-label="t('app.maximize')" @click="maximizeWindow">
        <Square :size="12" aria-hidden="true" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn close" :title="t('app.close')" :aria-label="t('app.close')" @click="closeWindow">
        <X :size="14" aria-hidden="true" />
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
          :aria-current="activeSection === item.key ? 'page' : undefined"
            @click="selectSettingsSection(item.key)"
        >
          <component :is="item.icon" :size="16" />
          {{ item.label }}
        </UiButton>
      </nav>

      <div class="settings-content" :style="settingsContentStyle" v-bind="settingsContentDataAttrs">
        <p v-if="activeSection === 'model' || activeSection === 'agentCenter'" class="quiet" role="status">{{ t('settings.userConfigurationSource') }}</p>
        <p v-if="(activeSection === 'model' || activeSection === 'agentCenter') && !canAccessBackend" class="quiet" role="status">{{ hostReason }}</p>
        <Transition name="section-fade" mode="out-in">
        <div
          :key="activeSection"
          :class="['settings-section-wrapper', { 'settings-section-wrapper--centered': isCenteredSection }]"
        >
        <!-- Each center section owns its own first-load skeleton, so a page-level one here
             would stack a second set on top of it. -->
        <template v-if="activeSection === 'personal'">
          <PersonalSection />
        </template>

        <template v-if="activeSection === 'general'">
          <GeneralSection />
        </template>
        <template v-if="activeSection === 'storage'">
          <StorageSection ref="storageRef" />
        </template>

        <template v-if="activeSection === 'model'">
          <ModelCenterSection
            ref="modelCenterRef"
            :center="modelCenter"
            :runtime-models="runtimeModels"
            :runtime-cli-options="runtimeCliOptions"
            :runtime-acp-options="runtimeAcpOptions"
            :can-access-backend="canAccessBackend"
          />
        </template>

        <template v-if="activeSection === 'agentCenter'">
          <div class="agent-center-merged">
            <div class="center-command-bar">
              <div>
                <h2>{{ t('settings.agentCenter') }}</h2>
                <p>{{ t('settings.agentCenterSubtitle') }}</p>
              </div>
              <div class="center-command-actions">
                <UiButton variant="outline" size="sm" :disabled="agentCenterLoading || !canAccessBackend" @click="refreshAgentCenterTab()">
                  <RefreshCw :size="14" />
                  <span>{{ t('settings.refresh') }}</span>
                </UiButton>
              </div>
            </div>
            <AgentPacksPanel
              ref="agentPacksPanelRef"
              @clone="openGraphSeedPackCloneFlow"
              @uninstalled="loadAgentCenter()"
            />
            <div class="ac-subtabs" role="tablist" data-testid="agent-center-subtabs">
              <button :class="['ac-subtab', { active: agentCenterTab === 'agents' }]" role="tab" :aria-selected="agentCenterTab === 'agents'" @click="agentCenterTab = 'agents'">{{ t('settings.agents') }}</button>
              <button :class="['ac-subtab', { active: agentCenterTab === 'modes' }]" role="tab" :aria-selected="agentCenterTab === 'modes'" @click="agentCenterTab = 'modes'">{{ t('settings.agentModes') }}</button>
              <button :class="['ac-subtab', { active: agentCenterTab === 'prompts' }]" role="tab" :aria-selected="agentCenterTab === 'prompts'" @click="agentCenterTab = 'prompts'">{{ t('settings.promptEngineering') }}</button>
              <button :class="['ac-subtab', { active: agentCenterTab === 'evolution' }]" role="tab" :aria-selected="agentCenterTab === 'evolution'" @click="agentCenterTab = 'evolution'">{{ t('settings.agentEvolution') }}</button>
              <button :class="['ac-subtab', { active: agentCenterTab === 'runtime' }]" role="tab" :aria-selected="agentCenterTab === 'runtime'" @click="switchAgentCenterTab('runtime')">{{ t('settings.runtimeInstances') }}</button>
            </div>
            <template v-if="agentCenterTab === 'agents'">

          <div class="center-page agent-center-page">
          <div class="ac-toolbar-row">
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
            <span class="ac-toolbar-readout">{{ t('settings.activeAgents') }} · {{ agents.length }}</span>
          </div>

          <section class="center-overview-receipt agent-overview-receipt" :aria-label="t('settings.centerOverview')">
            <div class="center-receipt-item ready">
              <Settings2 :size="17" />
              <div>
                <strong :title="t('settings.agentProfilesWritable')">{{ t('settings.agentProfilesWritable') }}</strong>
                <span :title="t('settings.agentProfilesWritableHint')">{{ t('settings.agentProfilesWritableHint') }}</span>
              </div>
              <UiBadge variant="default">{{ t('settings.writable') }}</UiBadge>
            </div>
            <div class="center-receipt-item preview">
              <Workflow :size="17" />
              <div>
                <strong :title="t('settings.runtimePreviewOnly')">{{ t('settings.runtimePreviewOnly') }}</strong>
                <span :title="t('settings.runtimePreviewOnlyHint')">{{ t('settings.runtimePreviewOnlyHint') }}</span>
              </div>
              <UiBadge variant="outline">{{ t('settings.previewBadge') }}</UiBadge>
            </div>
            <div class="center-receipt-item configured">
              <Bot :size="17" />
              <div>
                <strong>{{ agents.filter(agent => agent.enabled).length }} / {{ agents.length }}</strong>
                <span :title="t('settings.activeAgents')">{{ t('settings.activeAgents') }}</span>
              </div>
              <UiBadge variant="outline" :title="t('settings.agentLayerCounts', { planning: planningAgents.length, execution: executionAgents.length })">
                {{ t('settings.agentLayerCounts', { planning: planningAgents.length, execution: executionAgents.length }) }}
              </UiBadge>
            </div>
          </section>

          <div v-if="agentCenterLoading && agents.length === 0" class="center-loading-state" aria-live="polite">
            <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
          </div>

          <p v-else-if="agentCenterLoading" class="center-stale-status" role="status">
            <RefreshCw :size="13" class="center-stale-spinner" aria-hidden="true" />
            {{ t('settings.refreshing') }}
          </p>

          <div v-if="agentCenterFailure" class="center-message" role="alert" data-testid="agent-center-read-failure">
            <Info :size="16" aria-hidden="true" />
            <div class="center-message-content">
              <strong>{{ t('settings.centerLoadFailed') }}</strong>
              <span>{{ agentCenterFailure.message }}</span>
              <span v-if="agentCenterFailure.code">{{ agentCenterFailure.code }}</span>
              <pre v-if="agentCenterFailure.details">{{ agentCenterFailure.details }}</pre>
            </div>
            <UiButton variant="outline" size="sm" :disabled="agentCenterLoading || !canAccessBackend" @click="loadAgentCenter">{{ t('settings.retry') }}</UiButton>
          </div>

          <details v-if="agentReadDiagnostics.length" class="center-message-content" data-testid="agent-center-partial-failure">
            <summary>{{ t('settings.centerPartialLoadFailed') }}</summary>
            <p v-for="diagnostic in agentReadDiagnostics" :key="diagnostic.source">
              {{ diagnostic.source }}: {{ diagnostic.error.message }}
              <span v-if="diagnostic.error.code"> · {{ diagnostic.error.code }}</span>
              <pre v-if="diagnostic.error.details">{{ diagnostic.error.details }}</pre>
            </p>
          </details>

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

          <div v-if="canAccessBackend && agentCenterLoaded && !agentCenterFailure && !agentCenterLoading && agents.length === 0" class="center-empty-state center-empty-state-prominent">
            <Bot :size="22" />
            <div>
              <strong>{{ t('settings.noAgents') }}</strong>
              <span>{{ t('settings.noAgentsHint') }}</span>
            </div>
          </div>

          <div v-if="agentViewMode === 'list'" class="agent-list-summary">
            <PanelRight :size="16" />
            <span>{{ selectedAgent ? agentTypeLabel(selectedAgent.agent_type) : t('settings.pleaseOpenAgentConfig') }}</span>
            <UiButton v-if="selectedAgent" variant="outline" size="sm" @click="openAgentConfig(selectedAgent)">
              <Settings2 :size="14" />
              {{ t('settings.openAgentConfig') }}
            </UiButton>
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
                  <div class="agent-card-icon pet">
                    <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="30" :alt="agent.name" />
                  </div>
                  <div class="agent-card-main">
                    <strong>{{ agent.name || agentTypeLabel(agent.agent_type) }}</strong>
                    <span>{{ agent.description || agentSourceKindLabel(agentSourceKindFor(agent.id)) }}</span>
                    <small :title="agentRuntimeSummary(agent.id)">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</small>
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
                  <div class="agent-card-icon pet execution">
                    <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="30" :alt="agent.name" />
                  </div>
                  <div class="agent-card-main">
                    <strong>{{ agent.name || agentTypeLabel(agent.agent_type) }}</strong>
                    <span>{{ agent.description || agentSourceKindLabel(agentSourceKindFor(agent.id)) }}</span>
                    <small :title="agentRuntimeSummary(agent.id)">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</small>
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
          </main>

          <aside class="center-inspector agent-inspector" :aria-label="t('settings.centerInspector')">
            <div class="center-pane-heading">
              <div>
                <span>{{ t('settings.centerInspector') }}</span>
                <strong>{{ configuringAgent ? agentTypeLabel(configuringAgent.agent_type) : t('settings.agentConfiguration') }}</strong>
              </div>
              <PanelRight :size="16" />
            </div>
            <div v-if="agentDefinitionFailures[configuringAgentId]" class="center-message-content" role="alert" data-testid="agent-definition-read-failure">
              <strong>{{ t('settings.agentDefinitionLoadFailed') }}</strong>
              <p>{{ agentDefinitionFailures[configuringAgentId]?.message }}</p>
              <UiButton variant="outline" size="sm" :disabled="agentCenterLoading || !canAccessBackend" @click="loadAgentCenter">{{ t('settings.retry') }}</UiButton>
            </div>
            <div v-else-if="configuringAgent" class="agent-detail-panel">
              <div class="agent-detail-head">
                <div class="agent-card-icon pet" :class="{ execution: configuringAgent.layer === 'execution' }">
                  <AgentPetAvatar :pet-slug="agentAvatarSlug(configuringAgent)" :size="44" :alt="configuringAgent.name" />
                </div>
                <div>
                  <h3>{{ configuringAgent.name }}</h3>
                  <p>{{ agentTypeLabel(configuringAgent.agent_type) }} · {{ agentLayerLabel(configuringAgent.layer) }}</p>
                </div>
                <UiBadge :variant="configuringAgent.status === 'published' ? 'default' : 'secondary'" class="agent-status-badge">
                  {{ configuringAgent.status === 'published' ? t('settings.agentStatusPublished', { version: configuringAgent.version ?? 1 }) : t('settings.agentStatusDraft') }}
                </UiBadge>
                <UiButton variant="ghost" size="icon" :title="t('settings.closeConfig')" @click="closeAgentConfig">
                  <X :size="16" />
                </UiButton>
              </div>

              <!-- 身份与状态 — 启用开关 + 标识 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentIdentitySection') }}</div>
                <div class="agent-identity-grid">
                  <div>
                    <span>{{ t('settings.agentSlugLabel') }}</span>
                    <strong>{{ (configuringAgent as AgentViewDto & { slug?: string }).slug ?? configuringAgent.id.slice(0, 8) }}</strong>
                  </div>
                  <div>
                    <span>{{ t('settings.routePurpose') }}</span>
                    <strong>{{ configuringRuntimeBinding?.route_purpose ?? configuringAgent.model_route_purpose ?? '—' }}</strong>
                  </div>
                </div>
                <div class="agent-config-switch">
                  <div>
                    <strong>{{ t('settings.agentEnabled') }}</strong>
                    <span>{{ configuringAgent.is_built_in ? t('settings.builtInAgent') : t('settings.agentEnabledHint') }}</span>
                    <small v-if="configuringAgent.is_built_in" class="agent-builtin-label">{{ t('settings.builtInCloneHint') }}</small>
                  </div>
                  <UiSwitch
                    :model-value="configuringAgent.enabled"
                    :disabled="busy || configuringAgent.is_built_in"
                    @update:model-value="setAgentEnabled(configuringAgent, $event)"
                  />
                </div>
              </div>

              <!-- 形象 — 这个智能体被派发时用的宠物；随机则每个实例各自一张脸 -->
              <div class="agent-config-section" data-testid="agent-avatar-section">
                <div class="agent-config-section-title">{{ t('settings.agentAvatarSection') }}</div>
                <p class="agent-config-hint">{{ t('settings.agentAvatarHint') }}</p>
                <div class="agent-avatar-choices" role="radiogroup" :aria-label="t('settings.agentAvatarSection')">
                  <button
                    type="button"
                    role="radio"
                    class="agent-avatar-choice"
                    :class="{ active: configuringAvatarPreference.mode !== 'fixed' }"
                    :aria-checked="configuringAvatarPreference.mode !== 'fixed'"
                    :disabled="agentAvatarBusy"
                    data-testid="agent-avatar-random"
                    @click="chooseAgentAvatar('random')"
                  >
                    <span class="agent-avatar-choice-glyph"><Shuffle :size="18" /></span>
                    <span class="agent-avatar-choice-name">{{ t('settings.agentAvatarRandom') }}</span>
                  </button>
                  <button
                    v-for="pet in agentAvatarPool"
                    :key="pet.slug"
                    type="button"
                    role="radio"
                    class="agent-avatar-choice"
                    :class="{ active: configuringAvatarPreference.mode === 'fixed' && configuringAvatarPreference.petSlug === pet.slug }"
                    :aria-checked="configuringAvatarPreference.mode === 'fixed' && configuringAvatarPreference.petSlug === pet.slug"
                    :disabled="agentAvatarBusy"
                    :data-testid="`agent-avatar-${pet.slug}`"
                    @click="chooseAgentAvatar('fixed', pet.slug)"
                  >
                    <AgentPetAvatar :pet-slug="pet.slug" :size="26" :alt="pet.displayName" />
                    <span class="agent-avatar-choice-name">{{ pet.displayName }}</span>
                  </button>
                </div>
                <p v-if="agentAvatarPool.length === 0" class="agent-config-hint">{{ t('settings.agentAvatarEmpty') }}</p>
              </div>

              <!-- 目录证据 — 来源 / Mode 使用位置 / 有效预览 / 最近调用 -->
              <div v-if="configuringDirectoryItem" class="agent-config-section">
                <div class="agent-config-section-title">{{ t('settings.agentModeUsageSection') }}</div>
                <div class="agent-detail-grid">
                  <div>
                    <span>{{ t('settings.agentSourceLabel') }}</span>
                    <strong>
                      {{ agentSourceKindLabel(configuringDirectoryItem.source_kind) }}
                      <template v-if="configuringDirectoryItem.managed"> · {{ t('settings.agentSourceManaged') }}</template>
                    </strong>
                  </div>
                  <div>
                    <span>{{ t('settings.agentStatus') }}</span>
                    <strong>{{ definitionStatusLabel(configuringDirectoryItem.status) }}</strong>
                  </div>
                </div>
                <div v-if="(configuringDirectoryItem.mode_usages ?? []).length > 0" class="agent-mode-usage-list">
                  <span v-for="usage in configuringDirectoryItem.mode_usages" :key="`${usage.mode_id}:${usage.node_key}`">
                    {{ usage.mode_slug }} · {{ usage.node_key }}
                  </span>
                </div>
                <p v-else class="agent-config-hint">{{ t('settings.agentNoModeUsage') }}</p>

                <div class="agent-config-section-title secondary">{{ t('settings.agentEffectivePreviewSection') }}</div>
                <div v-if="Object.keys(configuringDirectoryItem.effective_previews ?? {}).length > 0" class="agent-mode-usage-list">
                  <span v-for="(preview, key) in configuringDirectoryItem.effective_previews" :key="key" :title="key">
                    {{ agentPreviewLine(String(key).split(':')[0], String(key).split(':')[1] ?? '', preview) }}
                  </span>
                </div>
                <p v-else class="agent-config-hint">{{ t('settings.agentNoEffectivePreview') }}</p>

                <div class="agent-config-section-title secondary">{{ t('settings.agentRecentInvocationSection') }}</div>
                <p v-if="configuringDirectoryItem.recent_invocation" class="agent-config-hint">
                  {{ agentInvocationLine(configuringDirectoryItem.recent_invocation) }}
                </p>
                <p v-else class="agent-config-hint">{{ t('settings.agentNoRecentInvocation') }}</p>
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
                    <strong>{{ agentRuntimeSummary(configuringAgentId) || t('settings.runtimeUnresolved') }}</strong>
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
                  <button :class="{ active: agentRuntimeSelection === 'route' }" :aria-pressed="agentRuntimeSelection === 'route'" @click="agentRuntimeSelection = 'route'">
                    <Route :size="16" />
                    <strong>{{ t('settings.runtimeRoute') }}</strong>
                    <span>{{ t('settings.runtimeRouteHint') }}</span>
                  </button>
                  <button :class="{ active: agentRuntimeSelection === 'fixed_model' }" :aria-pressed="agentRuntimeSelection === 'fixed_model'" @click="agentRuntimeSelection = 'fixed_model'">
                    <Cpu :size="16" />
                    <strong>{{ t('settings.runtimeFixedModel') }}</strong>
                    <span>{{ t('settings.runtimeFixedModelHint') }}</span>
                  </button>
                </div>

                <div v-if="agentRuntimeSelection === 'inherit'" class="runtime-source-current">
                  <ShieldCheck :size="16" />
                  <span>{{ t('settings.runtimeInheritedCurrent', { source: agentRuntimeSummary(configuringAgentId) || t('settings.runtimeUnresolved') }) }}</span>
                </div>
                <div v-else-if="agentRuntimeSelection === 'route'" class="settings-field runtime-source-picker">
                  <UiLabel>{{ t('settings.routePurpose') }}</UiLabel>
                  <UiSelectField
                    v-model="agentRuntimeRoutePurpose"
                    :aria-label="t('settings.routePurpose')"
                    :options="[
                      { value: '', label: t('settings.routePurpose'), disabled: true },
                      ...routes.map((route) => ({ value: route.purpose, label: `${route.purpose} · ${route.candidates.length}` })),
                    ]"
                  />
                  <p v-if="routes.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                </div>
                <div v-else class="settings-field runtime-source-picker">
                  <UiLabel>{{ t('settings.runtimeFixedModel') }}</UiLabel>
                  <div class="runtime-source-search">
                    <Search :size="14" />
                    <UiInput v-model="agentRuntimeModelQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: t('settings.centerModels') })" />
                  </div>
                  <UiSelectField
                    v-model="agentRuntimeModelKey"
                    :aria-label="t('settings.selectModel')"
                    :options="[
                      { value: '', label: t('settings.selectModel'), disabled: true },
                      ...filteredRuntimeModels.map((model) => ({ value: modelOptionKey(model.provider_instance_id, model.model_id), label: `${model.model_id} · ${model.provider_display_name ?? model.provider_instance_id} · ${statusLabel(model.status)}` })),
                      ...filteredRuntimeCliOptions.map((runtime) => ({ value: runtime.provider_instance_id, label: `${runtime.display_name} · CLI · ${statusLabel(runtime.status)}` })),
                      ...filteredRuntimeAcpOptions.map((runtime) => ({ value: runtime.provider_instance_id ?? runtime.runtime_id, label: `${runtime.display_name} · ACP · ${statusLabel(runtime.status)}` })),
                    ]"
                  />
                  <p v-if="filteredRuntimeModels.length === 0 && filteredRuntimeCliOptions.length === 0 && filteredRuntimeAcpOptions.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
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
                  {{ t('settings.agentToolsPermissions') }}
                  <UiBadge variant="outline">{{ agentToolSelectionSummary.selected }}/{{ agentToolSelectionSummary.total }}</UiBadge>
                  <UiBadge v-if="agentToolSelectionSummary.approval > 0" variant="secondary">{{ t('settings.approvalRequired') }} {{ agentToolSelectionSummary.approval }}</UiBadge>
                </div>
                <p class="agent-config-hint">{{ t('settings.agentToolsHint') }}</p>
                <UiButton variant="outline" size="sm" @click="requestToolAgent(configuringAgent.id)">{{ t('toolsSettings.agentSettingsLink') }}</UiButton>
                <div class="agent-tool-toolbar">
                  <div class="agent-tool-search">
                    <Search :size="14" />
                    <UiInput v-model="agentToolQuery" :placeholder="t('settings.toolSearchPlaceholder')" />
                  </div>
                  <UiSelectField
                    v-model="agentToolSourceFilter"
                    class="compact"
                    :aria-label="t('settings.allSources')"
                    :options="[
                      { value: 'all', label: t('settings.allSources') },
                      ...toolSourceOptions.map((src) => ({ value: src, label: src })),
                    ]"
                  />
                  <UiSelectField
                    v-model="agentToolRiskFilter"
                    class="compact"
                    :aria-label="t('settings.allRisks')"
                    :options="[
                      { value: 'all', label: t('settings.allRisks') },
                      ...toolRiskOptions.map((risk) => ({ value: risk, label: risk })),
                    ]"
                  />
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
                      :title="tool.display_name + ' · ' + riskLabel(tool.risk) + (tool.requires_approval ? ' · ' + t('settings.approvalRequired') : '')"
                      @click="toggleAgentTool(tool.id)"
                    >
                      <span class="agent-tool-name">{{ tool.display_name }}</span>
                      <span class="agent-tool-meta">
                        <span class="agent-tool-risk" :class="tool.risk">{{ riskLabel(tool.risk) }}</span>
                        <span v-if="tool.requires_approval" class="agent-tool-approval">⚑ {{ t('settings.approvalRequired') }}</span>
                      </span>
                      <span class="agent-tool-id">{{ tool.id }}</span>
                    </button>
                  </div>
                </div>
                <p v-if="groupedAgentTools.length === 0" class="quiet">{{ t('settings.noTools') }}</p>
                <div class="agent-tool-bulk">
                  <UiButton variant="ghost" size="sm" :disabled="configuringAgent.is_built_in" @click="agentEditTools = manifestToolList.filter(t => !t.requires_approval).map(t => t.id)">{{ t('settings.selectReadOnlyTools') }}</UiButton>
                  <UiButton variant="ghost" size="sm" :disabled="configuringAgent.is_built_in" @click="agentEditTools = manifestToolList.map(t => t.id)">{{ t('settings.selectAllReadWrite') }}</UiButton>
                  <UiButton variant="ghost" size="sm" :disabled="configuringAgent.is_built_in" @click="agentEditTools = manifestToolList.filter(t => !t.requires_approval).map(t => t.id), agentEditTools.push('*')">{{ t('settings.allowAllTools') }}</UiButton>
                  <UiButton variant="ghost" size="sm" :disabled="configuringAgent.is_built_in" @click="agentEditTools = []">{{ t('settings.clearSelection') }}</UiButton>
                </div>

                <!-- 治理可见性：审批升级路径说明 + 待处理授权 -->
                <div class="runtime-binding-readonly agent-governance-card">
                  <ShieldCheck :size="16" />
                  <div>
                    <strong>{{ t('settings.agentGovernanceTitle') }}</strong>
                    <span>{{ t('settings.agentGovernanceHint') }}</span>
                  </div>
                  <UiBadge :variant="pendingGovernanceCount > 0 ? 'default' : 'outline'">
                    {{ t('settings.pendingGovernance', { count: pendingGovernanceCount }) }}
                  </UiBadge>
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

              <!-- 版本历史 — 不可变发布快照 -->
              <div class="agent-config-section">
                <div class="agent-config-section-title">
                  {{ t('settings.agentVersionHistory') }}
                  <UiBadge variant="outline">{{ agentVersionHistory.length }}</UiBadge>
                </div>
                <p class="agent-config-hint">{{ t('settings.agentVersionHistoryHint') }}</p>
                <div v-if="agentVersionsLoading" class="center-loading-state" aria-live="polite">
                  <UiSkeleton v-for="index in 2" :key="index" class="center-loading-line agent-version-loading-line" />
                </div>
                <div v-else-if="agentVersionHistory.length > 0" class="agent-version-list">
                  <div v-for="version in [...agentVersionHistory].reverse()" :key="version.id" class="center-list-row agent-version-row" :class="{ latest: version.version === configuringAgent.version }">
                    <strong>v{{ version.version }}</strong>
                    <span class="agent-version-hash">{{ (version as AgentVersionDto & { content_hash?: string }).content_hash?.slice(0, 8) ?? '—' }}</span>
                    <time>{{ new Date(version.created_at).toLocaleString(locale) }}</time>
                    <UiBadge v-if="version.version === configuringAgent.version" variant="default">{{ t('settings.agentVersionCurrent') }}</UiBadge>
                  </div>
                </div>
                <p v-else class="quiet">{{ t('settings.noVersionsPublished') }}</p>
              </div>

              <!-- 保存按钮 — 克隆后编辑 -->
              <div class="agent-save-bar">
                <UiButton v-if="configuringAgent.is_built_in" :disabled="agentCloneBusy" @click="cloneAgent">
                  <Plus :size="14" />
                  <span>{{ t('settings.cloneAgent') }}</span>
                </UiButton>
                <UiButton v-else :disabled="busy" @click="saveAgent">
                  <Save :size="14" />
                  <span>{{ t('settings.saveAgent') }}</span>
                </UiButton>
                <span v-if="!configuringAgent.is_built_in && agentEditRevision !== null" class="agent-revision-hint">{{ t('settings.agentRevision', { revision: agentEditRevision }) }}</span>
                <span v-if="configuringAgent.is_built_in" class="agent-builtin-save-hint">{{ t('settings.builtInCloneHint') }}</span>
              </div>
            </div>
            <div v-else-if="canAccessBackend && agentCenterLoaded && !agentCenterFailure" class="center-empty-state inspector-empty">
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
            <template v-else-if="agentCenterTab === 'prompts'">
              <PromptEngineeringMerged ref="promptsPanelRef" />
            </template>
            <template v-else-if="agentCenterTab === 'modes'">
              <AgentModesPanel ref="modePanelRef" />
            </template>
            <template v-else-if="agentCenterTab === 'runtime'">
              <RuntimeInstancesPanel ref="runtimePanelRef" />
            </template>
            <template v-else-if="agentCenterTab === 'evolution'">
              <AgentEvolutionPanel ref="evolutionPanelRef" />
            </template>
          </div>
        </template>

        <template v-if="activeSection === 'tools'">
          <ToolCenterSection ref="toolCenterRef" />
        </template>

        <template v-if="activeSection === 'tinachat'">
          <TinaChatSection />
        </template>

        <template v-if="activeSection === 'archive'">
          <ArchiveTrashSection />
        </template>

        <template v-if="activeSection === 'appearance'">
          <AppearanceSection />
        </template>

        <template v-if="activeSection === 'companionAvatar'">
          <PetsSection />
        </template>

        <template v-if="activeSection === 'apiDocs'">
          <ApiDocsSection />
        </template>

        <template v-if="activeSection === 'about'">
          <AboutSection />
        </template>
    </div>
    </Transition>

    <!-- Entered once, then kept mounted: the section's reads happen on mount, so a later visit
         must not spend them again (the section owns its own refresh button). -->
    <div v-if="companionsMounted" v-show="activeSection === 'companions'" class="settings-section-wrapper">
      <CompanionSection :companion-request="companionRequest" />
    </div>

    
      </div>
    </div>

    

    
  </div>
</template>
