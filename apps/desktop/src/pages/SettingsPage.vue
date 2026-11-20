<script setup lang="ts">
import {
  Archive,
  ArrowLeft,
  Bot,
  Database,
  FileText,
  Info,
  KeyRound,
  MessagesSquare,
  Minus,
  Palette,
  PawPrint,
  Settings2,
  Square,
  Terminal,
  UserRound,
  Workflow,
  X,
} from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
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
  type AgentCenterOverviewDto,
  type AgentDefinitionDto,
  type AgentDirectoryItemDto,
  type AgentModeDto,
  type AgentViewDto,
  type CenterDiagnosticDto,
  type HarnessManifestDto,
  type PermissionRequestDto,
  type PromptPipelineDto,
  type ToolDescriptorDto,
} from '../api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import { manifestTools } from '../toolCatalog'
import CommandPaletteButton from '@/components/CommandPaletteButton.vue'
import { UiButton } from '@/components/ui'
import AgentModesPanel from '@/settings/sections/AgentModesPanel.vue'
import AgentCenterSection, { type AgentCenterTabKey } from '@/settings/agentCenter/AgentCenterSection.vue'
import AgentsDirectoryPanel from '@/settings/agentCenter/AgentsDirectoryPanel.vue'
import PromptEngineeringMerged from '@/settings/sections/PromptEngineeringMerged.vue'
import {
  consumeRequest,
  pendingAgentId,
  pendingCompanionId,
  pendingModeId,
  pendingModelProviderId,
  pendingPromptId,
  pendingSettingsSection,
  pendingToolAgentId,
  pendingToolId,
  type CompanionSettingsTab,
} from '@/lib/pageRequests'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { createSettingsLeaveGuard } from './settingsNavigation'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'
import { useRecoverableRead, type ReadContext } from '@/composables/useRecoverableRead'
import { useModelCenter } from '@/composables/useModelCenter'
import { useAgentModesStore } from '@/composables/useAgentModesStore'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { isAbortError } from '@/lib/isAbortError'
import { patchAgentCenterUi, readCenterUiState } from '@/settings/drafts/uiState'
import { withoutRetiredModes } from '@/lib/retiredModes'
import { graphSeedPackManifest } from '@/agentPacks/GraphSeedPack'

type SettingsSection = 'personal' | 'general' | 'storage' | 'model' | 'agentCenter' | 'companions' | 'tools' | 'tinachat' | 'archive' | 'appearance' | 'companionAvatar' | 'apiDocs' | 'about'

type AgentCenterTab = AgentCenterTabKey

const agentCenterSectionRef = ref<InstanceType<typeof AgentCenterSection> | null>(null)
const agentsPanelRef = ref<InstanceType<typeof AgentsDirectoryPanel> | null>(null)
const modePanelRef = ref<InstanceType<typeof AgentModesPanel> | null>(null)
const promptsPanelRef = ref<InstanceType<typeof PromptEngineeringMerged> | null>(null)
const agentModesStore = useAgentModesStore()

/** Switch Agent Center sub-tab while guarding unsaved mode/team edits. */
async function switchAgentCenterTab(tab: AgentCenterTab) {
  if (tab === agentCenterTab.value) return
  if ((agentCenterTab.value === 'teams' || agentCenterTab.value === 'modes') && !(await agentModesStore.discardOrConfirm())) {
    return
  }
  agentCenterTab.value = tab
}

/** A team is opened in the deep graph editor by reusing the existing mode deep-link. */
async function openModeInGraph(id: string) {
  if (agentCenterTab.value !== 'modes' && !(await agentModesStore.discardOrConfirm())) return
  agentCenterTab.value = 'modes'
  pendingModeId.value = id
}

/** Single refresh entry for the whole Agent Center: reloads the active sub-tab. */
function refreshAgentCenterTab(tab: AgentCenterTab = agentCenterTab.value) {
  if (tab === 'modes') void modePanelRef.value?.loadModes()
  else if (tab === 'prompts') void promptsPanelRef.value?.refreshAll()
  else void loadAgentCenter()
}

const { t } = useI18n()
const { canAccessBackend } = useHostAccess()
const router = useRouter()
const { notify, status, dismissByKey } = useNotifications()

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
  if (activeSection.value === 'agentCenter' && !await agentModesStore.discardOrConfirm()) return
  window.tinadec?.closeWindow?.()
}

const activeSection = ref<SettingsSection>('personal')
const toolCenterRef = ref<{ canLeave(): Promise<boolean> } | null>(null)
const storageRef = ref<{ canLeave(): Promise<boolean> } | null>(null)
const storedCenterUi = readCenterUiState()

function initialAgentCenterTab(): AgentCenterTab {
  const tab = storedCenterUi.agentCenter.tab
  if (tab === 'teams') return 'teams'
  if (tab === 'modes' || tab === 'graph') return 'modes'
  if (tab === 'prompts') return 'prompts'
  if (tab === 'advanced' || tab === 'evolution' || tab === 'runtime') return 'advanced'
  return 'agents'
}
const agentCenterTab = ref<AgentCenterTab>(initialAgentCenterTab())
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
  if (section !== 'agentCenter' && activeSection.value === 'agentCenter' && !await agentModesStore.discardOrConfirm()) return false
  // Mounting is the first read; `v-show` afterwards keeps the already-read section alive.
  if (section === 'companions') companionsMounted.value = true
  activeSection.value = section
  return true
}

// Spatial exit animation — declarative, class-driven.
const settingsExiting = ref(false)
const SETTINGS_EXIT_DURATION_MS = 530

onBeforeRouteLeave(createSettingsLeaveGuard({
  isTools: () =>
    activeSection.value === 'tools'
    || activeSection.value === 'storage'
    || activeSection.value === 'model'
    || activeSection.value === 'agentCenter',
  canLeave: () => {
    if (activeSection.value === 'agentCenter') return agentModesStore.discardOrConfirm()
    return (activeSection.value === 'storage'
      ? storageRef.value
      : activeSection.value === 'model' ? modelCenterRef.value : toolCenterRef.value)?.canLeave() ?? Promise.resolve(true)
  },
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
const availableTools = ref<ToolDescriptorDto[]>([])
const harnessManifest = ref<HarnessManifestDto | null>(null)
const promptPipelines = ref<PromptPipelineDto[]>([])
const selectedAgentId = ref('')
const configuringAgentId = ref('')
const requestedAgentId = ref<string | null>(null)
let agentNavigationGeneration = 0
let providerNavigationGeneration = 0
onBeforeUnmount(() => {
  agentNavigationGeneration++
  providerNavigationGeneration++
  pendingProviderFocus.value = null
  agentsPanelRef.value?.flushAgentDraft()
})
const agentPermissionRequests = ref<PermissionRequestDto[]>([])
const agentDefinitionFailures = ref<Record<string, ErrorState>>({})
/** Definitions already read for this render; anything else is fetched when its agent is opened. */
const agentDefinitionLoadedIds = ref<Set<string>>(new Set())
const agentDefinitionLoadingId = ref('')
const agentReadDiagnostics = ref<Array<{ source: string; error: ErrorState }>>([])

const agentViewMode = ref<'topology' | 'list'>(storedCenterUi.agentCenter.viewMode)
watch([agentCenterTab, agentViewMode], () => {
  patchAgentCenterUi({ tab: agentCenterTab.value, viewMode: agentViewMode.value })
}, { flush: 'post' })

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

const runtimeModels = computed(() => agentCenterOverview.value?.runtime_sources.models ?? modelCenterOverview.value?.models ?? [])
const runtimeCliOptions = computed(() => agentCenterOverview.value?.runtime_sources.cli_runtimes ?? modelCenterOverview.value?.cli_runtimes ?? [])
const runtimeAcpOptions = computed(() => agentCenterOverview.value?.runtime_sources.acp_runtimes ?? modelCenterOverview.value?.acp_runtimes ?? [])
const agentCenterDiagnostics = computed(() => agentCenterOverview.value?.diagnostics ?? [])
const manifestToolList = computed(() => manifestTools(harnessManifest.value, availableTools.value))

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

// Preload useModelCenter on agentCenter entry so model/route dropdowns are populated right away.
watch(activeSection, section => {
  if (section === 'model' && !modelCenterLoaded.value) void loadModelCenter()
  else if (section === 'agentCenter') {
    if (!modelCenterLoaded.value) void loadModelCenter()
    if (!agentCenterRead.loaded.value) void loadAgentCenter()
  }
}, { flush: 'post' })

async function loadAgentPermissionRequests() {
  try {
    agentPermissionRequests.value = await api.listPermissionRequests()
  } catch {
    agentPermissionRequests.value = []
  }
}

async function loadAgentCenterData(context: ReadContext) {
  const { signal, isCurrent } = context
  const partial: Array<{ source: string; error: ErrorState }> = []
  function preserve<T>(source: string, fallback: T): (error: unknown) => T {
    return error => { if (isCurrent()) partial.push({ source, error: toErrorState(error) }); return fallback }
  }
  const [directory, modes, packDetail, pipelineList] = await Promise.all([
    api.listAgents({ signal }),
    api.listAgentModes({ signal }).catch(preserve('agent-modes', agentModes.value)),
    api.getAgentPack(graphSeedPackManifest.metadata.pack_id, { signal }).catch(() => null),
    api.listPromptPipelines({ signal }).catch(() => promptPipelines.value),
  ])
  if (!isCurrent()) return
  const managedAgentIds = new Set(
    (packDetail?.resources ?? [])
      .filter((resource) => resource.kind === 'agent' && resource.logical_entity_id)
      .map((resource) => resource.logical_entity_id!),
  )
  const failures: Record<string, ErrorState> = {}
  const preserveDraft = Boolean(agentsPanelRef.value?.isEditorDirty())
  if (!preserveDraft) agentDefinitionLoadedIds.value = new Set()
  agentDefinitionLoadingId.value = ''
  const previousAgents = new Map(agents.value.map(agent => [agent.id, agent]))
  if (preserveDraft && !directory.some(item => item.id === configuringAgentId.value)) {
    failures[configuringAgentId.value] = toErrorState(new Error(t('settings.agentDraftSourceMissing')))
    partial.push({ source: previousAgents.get(configuringAgentId.value)?.name ?? configuringAgentId.value, error: failures[configuringAgentId.value]! })
  }
  agentDefinitionFailures.value = failures
  agentCenterOverview.value = null
  agentModes.value = withoutRetiredModes(modes)
  agentDirectory.value = directory
  promptPipelines.value = Array.isArray(pipelineList) ? pipelineList : []
  agents.value = directory.map((item) => {
    const previous = previousAgents.get(item.id)
    const isManaged = item.managed || item.source_kind === 'pack' || item.source_kind === 'built_in' || managedAgentIds.has(item.id)
    return {
      id: item.id,
      slug: item.slug,
      display_name: item.display_name,
      i18n: item.i18n ?? previous?.i18n ?? null,
      name: item.display_name ?? item.slug,
      layer: item.layer,
      role: item.role,
      source_kind: item.source_kind,
      provenance: item.provenance ?? previous?.provenance ?? null,
      base_prompt_pipeline_id: item.base_prompt_pipeline_id ?? previous?.base_prompt_pipeline_id ?? null,
      agent_type: item.role,
      mode: '',
      description: previous?.description ?? '',
      model_route_purpose: item.configured_strategy?.route_purpose ?? '',
      allowed_tools: previous?.allowed_tools ?? [],
      capabilities: previous?.capabilities ?? [],
      system_prompt: previous?.system_prompt ?? null,
      enabled: item.enabled,
      is_built_in: isManaged,
      model_strategy: item.configured_strategy as unknown as Record<string, unknown>,
      model_binding: item.model_binding ?? null,
      status: item.status,
      revision: item.revision,
      version: item.version,
      updated_at: item.updated_at ?? null,
    }
  })
  const activeAgent = requestedAgentId.value
    ? agents.value.find((agent) => agent.id === requestedAgentId.value)
    : agents.value.find((agent) => agent.id === configuringAgentId.value)
      ?? agents.value.find((agent) => agent.id === selectedAgentId.value)
      ?? agents.value[0]
  if (activeAgent && !preserveDraft) {
    await openAgentConfigFromDirectory(activeAgent.id)
  }
  const readSources = new Set(partial.map(item => item.source))
  agentReadDiagnostics.value = [...partial, ...agentReadDiagnostics.value.filter(item => !readSources.has(item.source))]
  void loadAgentPermissionRequests()
  const auxiliary = Promise.all([
    (async () => {
      try {
        const manifest = await api.getHarnessManifest({ signal })
        if (!isCurrent()) return
        harnessManifest.value = manifest
        availableTools.value = manifest.tools
      } catch {
        if (!isCurrent()) return
        try {
          const tools = await api.listTools({ signal })
          if (!isCurrent()) return
          harnessManifest.value = null
          availableTools.value = tools
        } catch (error) { preserve('tools', availableTools.value)(error) }
      }
    })(),
  ])
  void auxiliary.then(() => {
    if (!isCurrent()) return
    const known = new Set(partial.map(item => item.source))
    agentReadDiagnostics.value = [...partial, ...agentReadDiagnostics.value.filter(item => !known.has(item.source))]
  })
}

async function openGraphSeedPackCloneFlow(requestedSlug?: string) {
  await switchAgentCenterTab('agents')
  if (!agents.value.some((agent) => agent.is_built_in)) await loadAgentCenter()
  const packSlugs = new Set(graphSeedPackManifest.resources.agents.map((agent) => agent.slug))
  const managedAgent = (requestedSlug
    ? agents.value.find((agent) => agent.is_built_in && (agent.slug === requestedSlug || agent.name === requestedSlug))
    : undefined)
    ?? agents.value.find((agent) => agent.is_built_in && packSlugs.has(agent.slug ?? agent.name))
  if (!managedAgent) {
    status.warning({
      key: 'graph-seed-pack-clone',
      source: 'GraphSeedPack',
      message: t('agentPack.managedReadOnly'),
    })
    return
  }
  await openAgentConfigFromDirectory(managedAgent.id)
}

async function ensureAgentDefinition(agentId: string): Promise<AgentViewDto | null> {
  if (agentDefinitionLoadedIds.value.has(agentId)) return agents.value.find((item) => item.id === agentId) ?? null
  if (agentDefinitionLoadingId.value === agentId) return null
  agentDefinitionLoadingId.value = agentId
  try {
    const definition = await api.getAgent(agentId)
    const index = agents.value.findIndex((item) => item.id === agentId)
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

function mergeAgentDefinition(row: AgentViewDto, definition: AgentDefinitionDto): AgentViewDto {
  const tools = definition.tool_scope === '*'
    ? ['*']
    : Array.isArray(definition.tool_scope)
      ? definition.tool_scope
      : Array.isArray(definition.allowed_tools)
        ? definition.allowed_tools
        : row.allowed_tools ?? []
  return {
    ...row,
    display_name: definition.display_name ?? row.display_name,
    name: definition.display_name ?? definition.name ?? row.name,
    layer: definition.layer ?? row.layer,
    role: definition.role ?? definition.agent_type ?? row.role,
    agent_type: definition.role ?? definition.agent_type ?? row.agent_type,
    source_kind: definition.source_kind ?? row.source_kind,
    provenance: definition.provenance ?? row.provenance ?? null,
    base_prompt_pipeline_id: definition.base_prompt_pipeline_id ?? row.base_prompt_pipeline_id ?? null,
    description: definition.description ?? row.description ?? '',
    allowed_tools: tools,
    capabilities: definition.capabilities ?? row.capabilities ?? [],
    system_prompt: definition.system_prompt ?? null,
    revision: definition.revision ?? row.revision ?? null,
    version: definition.version ?? row.version ?? null,
    status: definition.status ?? row.status,
  }
}

function handlePatchAgent(patched: AgentViewDto) {
  const index = agents.value.findIndex((item) => item.id === patched.id)
  if (index >= 0) {
    agents.value = agents.value.map((item, idx) => (idx === index ? patched : item))
  }
}

async function openAgentConfigFromDirectory(agentId: string): Promise<void> {
  selectedAgentId.value = agentId
  const loaded = await ensureAgentDefinition(agentId)
  if (loaded) {
    configuringAgentId.value = loaded.id
    agentsPanelRef.value?.openAgentConfig(loaded)
  } else if (agentDefinitionFailures.value[agentId]) {
    configuringAgentId.value = agentId
  }
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
  await switchAgentCenterTab('agents')
  agentViewMode.value = 'list'
  selectedAgentId.value = agentId
  configuringAgentId.value = agentId
  requestedAgentId.value = agentId
  if (!agents.value.some((item) => item.id === agentId)) await loadAgentCenter()
  if (generation !== agentNavigationGeneration) return
  const agent = agents.value.find((item) => item.id === agentId)
  requestedAgentId.value = null
  if (agent) {
    await openAgentConfigFromDirectory(agent.id)
  } else {
    notify.error(t('app.loadFailedMessage'), { title: t('app.loadFailed'), key: 'search-navigation' })
  }
})
// Child panels own these request IDs; activate their host before they consume.
watch(pendingModeId, async (id) => {
  if (!id) return
  if (!await selectSettingsSection('agentCenter')) return
  await switchAgentCenterTab('modes')
}, { immediate: true })
watch(pendingPromptId, async (id) => {
  if (!id) return
  if (!await selectSettingsSection('agentCenter')) return
  await switchAgentCenterTab('prompts')
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
          <AgentCenterSection
            ref="agentCenterSectionRef"
            :tab="agentCenterTab"
            :agents="agents"
            :modes="agentModes"
            :prompt-pipelines="promptPipelines"
            :manifest-tool-ids="manifestToolList.map(tool => tool.id)"
            :can-access-backend="canAccessBackend"
            :loading="agentCenterLoading"
            @update:tab="switchAgentCenterTab"
            @changed="loadAgentCenter"
            @clone-pack="openGraphSeedPackCloneFlow"
            @refresh-legacy="refreshAgentCenterTab"
            @open-graph="openModeInGraph"
          >
            <template #agents>
              <AgentsDirectoryPanel
                ref="agentsPanelRef"
                :agents="agents"
                :agent-directory="agentDirectory"
                :providers="providers"
                :routes="routes"
                :runtime-models="runtimeModels"
                :runtime-cli-options="runtimeCliOptions"
                :runtime-acp-options="runtimeAcpOptions"
                :available-tools="availableTools"
                :harness-manifest="harnessManifest"
                :prompt-pipelines="promptPipelines"
                :permission-requests="agentPermissionRequests"
                :loading="agentCenterLoading"
                :loaded="agentCenterLoaded"
                :failure="agentCenterFailure"
                :read-diagnostics="agentReadDiagnostics"
                :center-diagnostics="agentCenterDiagnostics"
                :definition-failures="agentDefinitionFailures"
                :definition-loaded-ids="agentDefinitionLoadedIds"
                :can-access-backend="canAccessBackend"
                :selected-agent-id="selectedAgentId"
                :configuring-agent-id="configuringAgentId"
                :view-mode="agentViewMode"
                :ensure-agent-definition="ensureAgentDefinition"
                @update:view-mode="agentViewMode = $event"
                @update:selected-agent-id="selectedAgentId = $event"
                @update:configuring-agent-id="configuringAgentId = $event"
                @patch-agent="handlePatchAgent"
                @reload="loadAgentCenter"
              />
            </template>
            <template #prompts>
              <PromptEngineeringMerged ref="promptsPanelRef" />
            </template>
            <template #modes>
              <AgentModesPanel ref="modePanelRef" />
            </template>
          </AgentCenterSection>
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
