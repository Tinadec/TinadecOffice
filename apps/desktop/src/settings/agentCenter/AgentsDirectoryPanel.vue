<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import {
  Archive,
  Bot,
  Info,
  LayoutGrid,
  List,
  PanelRight,
  Plus,
  RefreshCw,
  Save,
  Search,
  Send,
  Sparkles,
  X,
} from '@lucide/vue'
import {
  api as baseApi,
  type AgentDefinitionDto,
  type AgentDirectoryItemDto,
  type AgentRuntimeSelectionKind,
  type AgentViewDto,
  type AgentVersionDto,
  type CenterDiagnosticDto,
  type HarnessManifestDto,
  type ModelCenterOverviewDto,
  type ModelProviderInstanceDto,
  type ModelRouteDto,
  type PermissionRequestDto,
  type PromptPipelineDto,
  type ToolDescriptorDto,
} from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { pickLocalized } from '@/lib/packI18n'
import { useNotifications } from '@/composables/useNotifications'
import { useAgentAvatars } from '@/composables/useAgentAvatars'
import { useAgentModesStore } from '@/composables/useAgentModesStore'
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { centerDiagnosticLabel as centerDiagnosticText } from '@/settings/modelCenterLabels'
import { bindingFromModelStrategy, legacyRouteWarning, modelOptionKey, runtimeSourceSummary } from '@/runtimeCenterView'
import { manifestTools } from '@/toolCatalog'
import { clearDraft, markDraftStale, readDraft, writeDraft, type StoredDraft } from '@/settings/drafts/draftStore'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { UiBadge, UiButton, UiInput, UiSelectField, UiSkeleton } from '@/components/ui'
import AgentDirectoryList from './directory/AgentDirectoryList.vue'
import AgentIdentitySection, { type UsedByModeChip } from './directory/AgentIdentitySection.vue'
import AgentRuntimeSection from './directory/AgentRuntimeSection.vue'
import AgentToolsSection from './directory/AgentToolsSection.vue'
import AgentPromptPipelineSection from './directory/AgentPromptPipelineSection.vue'
import AgentCreateSheet from './directory/AgentCreateSheet.vue'
import {
  agentDisplayDescription,
  agentDisplayName,
  agentLayerLabelKey,
  agentSourceKindFor as resolveSourceKind,
  agentSourceKindLabelKey,
  agentTypeLabelKey,
  isAgentReadOnly as checkAgentReadOnly,
  isExternalHarnessChannel,
  normalizeAgentLayer,
  providerRuntimeChannel,
  sourceBadgeVariant,
  type AgentDirectoryViewMode,
  type RuntimeChannelKind,
} from './directory/agentDirectoryHelpers'

const api = scopedApi(baseApi, () => 'user')

const props = defineProps<{
  agents: AgentViewDto[]
  agentDirectory: AgentDirectoryItemDto[]
  providers: ModelProviderInstanceDto[]
  routes: ModelRouteDto[]
  runtimeModels: ModelCenterOverviewDto['models']
  runtimeCliOptions: ModelCenterOverviewDto['cli_runtimes']
  runtimeAcpOptions: ModelCenterOverviewDto['acp_runtimes']
  availableTools: ToolDescriptorDto[]
  harnessManifest: HarnessManifestDto | null
  promptPipelines: PromptPipelineDto[]
  permissionRequests: PermissionRequestDto[]
  loading: boolean
  loaded: boolean
  failure: ErrorState | null
  readDiagnostics: Array<{ source: string; error: ErrorState }>
  centerDiagnostics: CenterDiagnosticDto[]
  definitionFailures: Record<string, ErrorState>
  definitionLoadedIds: Set<string>
  canAccessBackend: boolean
  selectedAgentId: string
  configuringAgentId: string
  viewMode: AgentDirectoryViewMode
  ensureAgentDefinition: (agentId: string) => Promise<AgentViewDto | null>
}>()

const emit = defineEmits<{
  'update:viewMode': [mode: 'topology' | 'list']
  'update:selectedAgentId': [id: string]
  'update:configuringAgentId': [id: string]
  'patch-agent': [agent: AgentViewDto]
  'open-mode': [modeId: string]
  reload: []
}>()

const { t, locale } = useI18n()
const router = useRouter()
const { notify, confirm, status } = useNotifications()
const agentModesStore = useAgentModesStore()

const centerDiagnosticLabel = (diagnostic: CenterDiagnosticDto) => centerDiagnosticText(t, diagnostic)
const directoryById = computed(() => new Map(props.agentDirectory.map((item) => [item.id, item])))

function displayName(agent: AgentViewDto): string {
  return agentDisplayName(agent, directoryById.value.get(agent.id), locale.value)
}

function displayDescription(agent: AgentViewDto): string {
  return agentDisplayDescription(agent, directoryById.value.get(agent.id), locale.value)
}

function agentSourceKindFor(agentId: string): string {
  return resolveSourceKind(agentId, directoryById.value, props.agents)
}

function isAgentReadOnly(agent: AgentViewDto | null): boolean {
  return checkAgentReadOnly(agent, directoryById.value, props.agents)
}

function agentSourceKindLabel(kind: string): string {
  const key = agentSourceKindLabelKey(kind)
  return key ? t(key) : kind
}

function agentTypeLabel(type: string): string {
  const key = agentTypeLabelKey(type)
  return key ? t(key) : type
}

function agentLayerLabel(layer: string): string {
  const key = agentLayerLabelKey(layer)
  return key ? t(key) : layer
}

// ── Filters ──
const agentQuery = ref('')
const agentLayerFilter = ref<'all' | 'operation' | 'execution'>('all')
const agentSourceFilter = ref('all')
const agentStatusFilter = ref('all')

const operationAgents = computed(() => props.agents.filter((agent) => normalizeAgentLayer(agent.layer) === 'operation'))
const executionAgents = computed(() => props.agents.filter((agent) => normalizeAgentLayer(agent.layer) === 'execution'))

const agentSourceOptions = computed(() => {
  const base = ['pack', 'custom', 'agent_generated']
  const extra = Array.from(new Set(props.agents.map((agent) => agentSourceKindFor(agent.id))))
    .filter((kind) => !base.includes(kind))
    .sort()
  return [...base, ...extra]
})

function agentMatchesFilters(agent: AgentViewDto): boolean {
  if (agentLayerFilter.value !== 'all' && normalizeAgentLayer(agent.layer) !== agentLayerFilter.value) return false
  if (agentSourceFilter.value !== 'all' && agentSourceKindFor(agent.id) !== agentSourceFilter.value) return false
  if (agentStatusFilter.value !== 'all' && (agent.status ?? '') !== agentStatusFilter.value) return false
  const q = agentQuery.value.trim().toLowerCase()
  if (!q) return true
  const item = directoryById.value.get(agent.id)
  const provenance = agent.provenance ?? item?.provenance
  return [
    displayName(agent),
    agent.name,
    agent.slug,
    displayDescription(agent),
    agent.role,
    agent.agent_type,
    provenance?.created_by_agent_id,
    provenance?.reason,
  ].some((value) => String(value ?? '').toLowerCase().includes(q))
}

const filteredOperationAgents = computed(() => operationAgents.value.filter(agentMatchesFilters))
const filteredExecutionAgents = computed(() => executionAgents.value.filter(agentMatchesFilters))
const filteredAgentCount = computed(() => filteredOperationAgents.value.length + filteredExecutionAgents.value.length)
const agentGeneratedCount = computed(() => props.agents.filter((agent) => agentSourceKindFor(agent.id) === 'agent_generated').length)

// ── Inspector & Editor State ──
const configuringAgent = computed(() =>
  props.definitionFailures[props.configuringAgentId]
    ? null
    : props.agents.find((agent) => agent.id === props.configuringAgentId) ?? null,
)
const configuringDirectoryItem = computed(() => directoryById.value.get(props.configuringAgentId) ?? null)
const configuringIsReadOnly = computed(() => isAgentReadOnly(configuringAgent.value))
const configuringProvenance = computed(() =>
  configuringAgent.value?.provenance ?? configuringDirectoryItem.value?.provenance ?? null,
)
const showProvenanceCard = computed(() =>
  Boolean(configuringAgent.value && (agentSourceKindFor(configuringAgent.value.id) === 'agent_generated' || configuringProvenance.value)),
)

const agentEditName = ref('')
const agentEditLayer = ref<'operation' | 'execution'>('operation')
const agentEditRole = ref('')
const agentEditBasePromptPipelineId = ref('')
const agentEditTools = ref<string[]>([])
const agentEditCapabilities = ref<string[]>([])
const agentEditSystemPrompt = ref('')
const agentEditDescription = ref('')
const agentEditRevision = ref<number | null>(null)

const agentRuntimeSelection = ref<AgentRuntimeSelectionKind>('inherit')
const agentRuntimeModelKey = ref('')
const agentRuntimeRoutePurpose = ref('')
const agentRuntimeModelQuery = ref('')

const busy = ref(false)
const agentCloneBusy = ref(false)
const agentRuntimeBusy = ref(false)
const agentVersionHistory = ref<AgentVersionDto[]>([])
const agentVersionHistoryError = ref<ErrorState | null>(null)
const agentVersionsLoading = ref(false)
const lastSeededAgentId = ref('')

const pendingGovernanceCount = computed(() =>
  props.permissionRequests.filter((request) =>
    ['pending', 'awaiting_delegate', 'awaiting_user', 'awaiting_approval'].includes(request.status),
  ).length,
)

const pipelineOptions = computed(() => [
  { value: '', label: t('settings.agentBasePipelineNone') },
  ...props.promptPipelines
    .filter((pipeline) => pipeline.status !== 'archived')
    .map((pipeline) => {
      const title = pickLocalized(
        pipeline.i18n,
        locale.value,
        'display_name',
        pipeline.display_name ?? pipeline.name ?? pipeline.title ?? pipeline.slug ?? pipeline.id,
      ) ?? pipeline.id
      return { value: pipeline.id, label: title }
    }),
])

const manifestToolList = computed(() => manifestTools(props.harnessManifest, props.availableTools))

const usedByModes = computed<UsedByModeChip[]>(() => {
  const agent = configuringAgent.value
  if (!agent) return []
  const dirUsages = configuringDirectoryItem.value?.mode_usages ?? []
  const storeWithUsages = agentModesStore as typeof agentModesStore & {
    modesUsingAgent?: (
      target: string | { id?: string; slug?: string | null; mode_usages?: Array<{ mode_id: string; mode_slug: string; node_key?: string }> },
      loc?: string,
    ) => Array<{ id: string; slug: string; displayName: string; status?: string }>
  }
  if (typeof storeWithUsages.modesUsingAgent === 'function') {
    const items = storeWithUsages.modesUsingAgent(
      { id: agent.id, slug: agent.slug ?? configuringDirectoryItem.value?.slug, mode_usages: dirUsages },
      locale.value,
    )
    return items.map((item) => {
      const matchedUsage = dirUsages.find((u) => u.mode_id === item.id || u.mode_slug === item.slug)
      return {
        modeId: item.id,
        label: item.displayName,
        nodeKey: matchedUsage?.node_key ?? null,
        status: item.status ?? null,
      }
    })
  }
  const modesById = new Map(agentModesStore.modes.value.map((m) => [m.id, m]))
  const seen = new Set<string>()
  const chips: UsedByModeChip[] = []
  for (const usage of dirUsages) {
    if (seen.has(usage.mode_id)) continue
    seen.add(usage.mode_id)
    const mode = modesById.get(usage.mode_id)
    const label = mode ? agentModesStore.modeLocalizedName(mode, locale.value) : usage.mode_slug
    chips.push({
      modeId: usage.mode_id,
      label,
      nodeKey: usage.node_key ?? null,
      status: mode?.status ?? null,
    })
  }
  return chips
})

// ── Runtime Model Binding Helpers ──
const agentRuntimeBindings = computed(() =>
  Object.fromEntries(props.agents.map((agent) => [agent.id, bindingFromModelStrategy(agent, agent.model_binding)])),
)
const configuringRuntimeBinding = computed(() => {
  const agent = props.agents.find((item) => item.id === props.configuringAgentId)
  return agent ? bindingFromModelStrategy(agent, agent.model_binding) : null
})
const configuringLegacyWarning = computed(() => legacyRouteWarning(configuringRuntimeBinding.value))

function agentRuntimeSummary(agentId: string | null | undefined): string {
  if (!agentId) return ''
  const item = directoryById.value.get(agentId)
  const binding = agentRuntimeBindings.value[agentId]
  const resolveName = (providerInstanceId: string) =>
    props.providers.find((provider) => provider.id === providerInstanceId)?.display_name ?? null
  const hydrated =
    binding && !binding.provider_display_name && binding.provider_instance_id
      ? { ...binding, provider_display_name: resolveName(binding.provider_instance_id) }
      : binding
  return runtimeSourceSummary(hydrated, item?.effective_previews ?? null, resolveName)
}

function agentRuntimeChannel(agentId: string | null | undefined): RuntimeChannelKind | null {
  if (!agentId) return null
  const item = directoryById.value.get(agentId)
  const binding = agentRuntimeBindings.value[agentId]
  const providerId = binding?.provider_instance_id
    ?? Object.values(item?.effective_previews ?? {})
      .map((preview) => preview?.expected_selection?.provider_instance_id)
      .find((id): id is string => Boolean(id))
  if (!providerId) return null
  if (props.runtimeAcpOptions.some((r) => r.provider_instance_id === providerId || r.runtime_id === providerId)) return 'acp'
  if (props.runtimeCliOptions.some((r) => r.provider_instance_id === providerId)) return 'cli'
  const provider = props.providers.find((p) => p.id === providerId)
  return provider ? providerRuntimeChannel(provider) : null
}

// ── Avatars ──
const {
  pool: agentAvatarPool,
  revision: agentAvatarRevision,
  preferenceFor: agentAvatarPreferenceFor,
  agentPreviewSlug,
  setAgentPreference: setAgentAvatarPreference,
} = useAgentAvatars()
const agentAvatarBusy = ref(false)

function agentAvatarKey(agent: AgentViewDto | null | undefined): string {
  if (!agent) return ''
  return agent.slug || directoryById.value.get(agent.id)?.slug || agent.id
}

function agentAvatarSlug(agent: AgentViewDto | null | undefined): string {
  void agentAvatarRevision.value
  return agentPreviewSlug(agentAvatarKey(agent))
}

const configuringAvatarPreference = computed(() =>
  configuringAgent.value ? agentAvatarPreferenceFor(agentAvatarKey(configuringAgent.value)) : { mode: 'random' as const },
)

async function chooseAgentAvatar(mode: 'random' | 'fixed', petSlug?: string) {
  const agent = configuringAgent.value
  if (!agent) return
  agentAvatarBusy.value = true
  try {
    await setAgentAvatarPreference(
      agentAvatarKey(agent),
      mode === 'fixed' && petSlug ? { mode: 'fixed', petSlug } : { mode: 'random' },
    )
  } catch (error) {
    notify.error(error, { title: t('settings.agentAvatarSaveFailed') })
  } finally {
    agentAvatarBusy.value = false
  }
}

// ── Local Unsaved Draft Persistence ──
let agentEditorBaseline = ''
function agentEditorSnapshot(): string {
  return JSON.stringify([
    props.configuringAgentId,
    agentEditName.value,
    agentEditLayer.value,
    agentEditRole.value,
    agentEditBasePromptPipelineId.value,
    agentEditTools.value,
    agentEditCapabilities.value,
    agentEditSystemPrompt.value,
    agentEditDescription.value,
    agentRuntimeSelection.value,
    agentRuntimeModelKey.value,
    agentRuntimeRoutePurpose.value,
  ])
}

function isEditorDirty(): boolean {
  return agentEditorBaseline !== '' && agentEditorSnapshot() !== agentEditorBaseline
}

const agentDraftNotice = ref<{ status: 'dirty' | 'stale'; savedAt: number } | null>(null)
let agentDraftTimer: ReturnType<typeof setTimeout> | null = null
let applyingAgentDraft = false

function agentDraftPayload(agent: AgentViewDto): Record<string, unknown> {
  return {
    display_name: agentEditName.value.trim() || agent.name,
    layer: agentEditLayer.value,
    role: agentEditRole.value.trim() || agent.role || agent.agent_type,
    base_prompt_pipeline_id: agentEditBasePromptPipelineId.value || null,
    tool_scope: [...agentEditTools.value],
    capabilities: [...agentEditCapabilities.value],
    system_prompt: agentEditSystemPrompt.value || null,
    description: agentEditDescription.value || null,
    enabled: agent.enabled,
    model_selection: agentRuntimeSelection.value,
    model_key: agentRuntimeModelKey.value,
    route_purpose: agentRuntimeRoutePurpose.value,
  }
}

function applyAgentDraft(draft: StoredDraft) {
  const payload = draft.payload
  applyingAgentDraft = true
  try {
    if (typeof payload.display_name === 'string') agentEditName.value = payload.display_name
    if (payload.layer === 'operation' || payload.layer === 'execution') agentEditLayer.value = payload.layer
    if (typeof payload.role === 'string') agentEditRole.value = payload.role
    if (typeof payload.base_prompt_pipeline_id === 'string' || payload.base_prompt_pipeline_id === null) {
      agentEditBasePromptPipelineId.value = payload.base_prompt_pipeline_id ?? ''
    }
    if (Array.isArray(payload.tool_scope)) {
      agentEditTools.value = payload.tool_scope.filter((item): item is string => typeof item === 'string')
    }
    if (Array.isArray(payload.capabilities)) {
      agentEditCapabilities.value = payload.capabilities.filter((item): item is string => typeof item === 'string')
    }
    if (typeof payload.system_prompt === 'string' || payload.system_prompt === null) {
      agentEditSystemPrompt.value = payload.system_prompt ?? ''
    }
    if (typeof payload.description === 'string' || payload.description === null) {
      agentEditDescription.value = payload.description ?? ''
    }
    if (payload.model_selection === 'inherit' || payload.model_selection === 'route' || payload.model_selection === 'fixed_model') {
      agentRuntimeSelection.value = payload.model_selection
    }
    if (typeof payload.model_key === 'string') agentRuntimeModelKey.value = payload.model_key
    if (typeof payload.route_purpose === 'string') agentRuntimeRoutePurpose.value = payload.route_purpose
  } finally {
    applyingAgentDraft = false
  }
  agentDraftNotice.value = { status: draft.status, savedAt: draft.savedAt }
}

function restoreAgentDraft(agent: AgentViewDto) {
  agentDraftNotice.value = null
  const draft = readDraft('user', 'agent', agent.id)
  if (!draft) return
  const currentRevision = agent.revision ?? agentEditRevision.value
  const baseRevision = draft.base.revision
  if (baseRevision != null && currentRevision != null && baseRevision !== currentRevision) {
    markDraftStale('user', 'agent', agent.id)
    agentDraftNotice.value = { status: 'stale', savedAt: draft.savedAt }
    return
  }
  if (draft.status === 'stale') {
    agentDraftNotice.value = { status: 'stale', savedAt: draft.savedAt }
    return
  }
  applyAgentDraft(draft)
}

function persistAgentDraft() {
  const agent = configuringAgent.value
  if (!agent || isAgentReadOnly(agent) || applyingAgentDraft) return
  if (agentEditorSnapshot() === agentEditorBaseline) return
  const baseRevision = agentEditRevision.value ?? agent.revision ?? null
  const now = Date.now()
  const result = writeDraft('user', 'agent', agent.id, agentDraftPayload(agent), { revision: baseRevision, etag: null }, now)
  if (result.ok) {
    agentDraftNotice.value = { status: 'dirty', savedAt: now }
  } else if (result.reason === 'too_large') {
    status.warning({ key: 'agent-draft', source: 'agents', message: t('settings.agentDraftTooLarge') })
  }
}

function scheduleAgentDraftSave() {
  if (applyingAgentDraft) return
  if (agentDraftTimer) clearTimeout(agentDraftTimer)
  agentDraftTimer = setTimeout(() => {
    agentDraftTimer = null
    persistAgentDraft()
  }, 400)
}

function flushAgentDraft() {
  if (!agentDraftTimer) return
  clearTimeout(agentDraftTimer)
  agentDraftTimer = null
  persistAgentDraft()
}

function dropAgentDraft() {
  const agent = configuringAgent.value
  if (agent) clearDraft('user', 'agent', agent.id)
  agentDraftNotice.value = null
}

function loadAgentDraft() {
  const agent = configuringAgent.value
  if (!agent) return
  const draft = readDraft('user', 'agent', agent.id)
  if (!draft) {
    agentDraftNotice.value = null
    return
  }
  applyAgentDraft(draft)
}

function settleAgentDraft(agentId: string) {
  clearDraft('user', 'agent', agentId)
  if (props.configuringAgentId === agentId) agentDraftNotice.value = null
}

watch(
  [
    agentEditName,
    agentEditLayer,
    agentEditRole,
    agentEditBasePromptPipelineId,
    agentEditTools,
    agentEditCapabilities,
    agentEditSystemPrompt,
    agentEditDescription,
    agentRuntimeSelection,
    agentRuntimeModelKey,
    agentRuntimeRoutePurpose,
  ],
  scheduleAgentDraftSave,
  { deep: true, flush: 'post' },
)

onBeforeUnmount(() => {
  flushAgentDraft()
})

// ── Open & Load Definition ──
async function loadAgentVersionHistory(agentId: string) {
  agentVersionsLoading.value = true
  agentVersionHistoryError.value = null
  try {
    const versions = await api.listAgentVersions(agentId)
    if (props.configuringAgentId === agentId) agentVersionHistory.value = versions
  } catch (error) {
    if (props.configuringAgentId === agentId) agentVersionHistoryError.value = toErrorState(error)
  } finally {
    if (props.configuringAgentId === agentId) agentVersionsLoading.value = false
  }
}

async function openAgentConfigFromDirectory(agentId: string): Promise<void> {
  emit('update:selectedAgentId', agentId)
  const loaded = await props.ensureAgentDefinition(agentId)
  if (loaded) openAgentConfig(loaded, true)
  else if (props.definitionFailures[agentId]) emit('update:configuringAgentId', agentId)
}

function openAgentConfig(agent: AgentViewDto, forceSeed = false) {
  if (props.configuringAgentId && props.configuringAgentId !== agent.id) flushAgentDraft()
  emit('update:selectedAgentId', agent.id)
  if (props.definitionFailures[agent.id]) {
    emit('update:configuringAgentId', agent.id)
    return
  }
  if (!props.definitionLoadedIds.has(agent.id)) {
    void openAgentConfigFromDirectory(agent.id)
    return
  }
  emit('update:configuringAgentId', agent.id)
  if (!forceSeed && lastSeededAgentId.value === agent.id && isEditorDirty()) {
    return
  }
  lastSeededAgentId.value = agent.id
  agentEditName.value = agent.display_name ?? agent.name ?? ''
  agentEditLayer.value = normalizeAgentLayer(agent.layer)
  agentEditRole.value = agent.role ?? agent.agent_type ?? ''
  agentEditBasePromptPipelineId.value = agent.base_prompt_pipeline_id ?? directoryById.value.get(agent.id)?.base_prompt_pipeline_id ?? ''
  agentEditTools.value = [...(agent.allowed_tools ?? [])]
  agentEditCapabilities.value = [...(agent.capabilities ?? [])]
  agentEditSystemPrompt.value = agent.system_prompt ?? ''
  agentEditDescription.value = agent.description ?? ''
  agentEditRevision.value = agent.revision ?? null
  agentRuntimeModelQuery.value = ''

  const binding = bindingFromModelStrategy(agent, agent.model_binding)
  agentRuntimeSelection.value = binding?.selection_kind ?? 'inherit'
  agentRuntimeModelKey.value = binding?.provider_instance_id
    ? binding.model_id
      ? modelOptionKey(binding.provider_instance_id, binding.model_id)
      : binding.provider_instance_id
    : ''
  agentRuntimeRoutePurpose.value =
    binding?.route_purpose || (agent as AgentViewDto & { model_route_purpose?: string }).model_route_purpose || props.routes[0]?.purpose || ''
  restoreAgentDraft(agent)
  agentEditorBaseline = agentEditorSnapshot()
  void loadAgentVersionHistory(agent.id)
  void nextTick(() => {
    if (!window.matchMedia('(max-width: 760px)').matches) return
    const panel = document.querySelector('.agent-detail-panel')
    panel?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  })
}

function closeAgentConfig() {
  flushAgentDraft()
  lastSeededAgentId.value = ''
  agentEditorBaseline = ''
  emit('update:selectedAgentId', '')
  emit('update:configuringAgentId', '')
}

watch(
  () => [props.configuringAgentId, props.definitionLoadedIds.has(props.configuringAgentId)] as const,
  ([id, isLoaded]) => {
    if (!id || !isLoaded) return
    const agent = props.agents.find((item) => item.id === id)
    if (agent && (lastSeededAgentId.value !== id || !isEditorDirty())) {
      openAgentConfig(agent)
    }
  },
  { immediate: true },
)

// ── Errors & Saves (Draft vs Publish) ──
function errorStatus(error: unknown): unknown {
  return error && typeof error === 'object' ? (error as { status?: unknown }).status : undefined
}

function isRevisionConflict(error: unknown): boolean {
  const msg = error instanceof Error ? error.message : String(error)
  return (
    errorStatus(error) === 412 ||
    errorStatus(error) === 428 ||
    msg.includes('412') ||
    msg.toLowerCase().includes('revision') ||
    msg.includes('Precondition')
  )
}

function agentSaveErrorMessage(error: unknown): string {
  const msg = error instanceof Error ? error.message : String(error)
  if (isRevisionConflict(error)) return t('settings.agentConflict')
  if (errorStatus(error) === 409 || msg.includes('409') || msg.toLowerCase().includes('built-in')) {
    return t('settings.agentBuiltInConflict')
  }
  return msg
}

function buildEditPayload(agent: AgentViewDto): Partial<AgentDefinitionDto> {
  return {
    display_name: agentEditName.value.trim() || agent.name,
    layer: agentEditLayer.value,
    role: agentEditRole.value.trim() || agent.role || agent.agent_type,
    base_prompt_pipeline_id: agentEditBasePromptPipelineId.value || null,
    tool_scope: agentEditTools.value.includes('*') ? ['*'] : [...agentEditTools.value],
    capabilities: [...agentEditCapabilities.value],
    system_prompt: agentEditSystemPrompt.value || null,
    description: agentEditDescription.value || null,
    enabled: agent.enabled,
  }
}

async function resolveAgentConflict(agentId: string, body: Partial<AgentDefinitionDto>, publishAfterDraft: boolean) {
  const overwrite = await confirm({
    title: t('settings.agentConflictTitle'),
    message: t('settings.agentConflictMessage'),
    confirmLabel: t('settings.agentConflictOverwrite'),
    cancelLabel: t('settings.agentConflictRefresh'),
  })
  emit('reload')
  if (!overwrite) return
  const latest = props.agents.find((item) => item.id === agentId)
  if (!latest) return
  try {
    const draft = await api.updateAgentDraft(latest.id, body, latest.revision != null ? String(latest.revision) : null)
    if (publishAfterDraft) {
      await api.publishAgent(latest.id, draft.revision != null ? String(draft.revision) : null)
    }
    settleAgentDraft(latest.id)
    agentEditorBaseline = agentEditorSnapshot()
    emit('reload')
    notify.success(displayName(latest))
  } catch (retryError) {
    notify.error(new Error(isRevisionConflict(retryError) ? t('settings.agentConflict') : agentSaveErrorMessage(retryError)), {
      title: displayName(latest),
    })
  }
}

async function saveAgentDraftOnly() {
  const agent = configuringAgent.value
  if (!agent) return
  if (isAgentReadOnly(agent)) {
    status.warning({ key: 'agent-builtin', source: 'agents', message: t('settings.builtInCloneHint') })
    return
  }
  if (!agentEditName.value.trim()) {
    notify.error(new Error(t('settings.fieldRequired')), { title: t('settings.agentName') })
    return
  }
  const body = buildEditPayload(agent)
  busy.value = true
  try {
    const draft = await api.updateAgentDraft(
      agent.id,
      body,
      (agentEditRevision.value ?? agent.revision) != null ? String(agentEditRevision.value ?? agent.revision) : null,
    )
    settleAgentDraft(agent.id)
    agentEditRevision.value = draft.revision ?? agentEditRevision.value
    agentEditorBaseline = agentEditorSnapshot()
    emit('reload')
    notify.success(t('settings.agentDraftSaved', { name: body.display_name ?? displayName(agent) }))
  } catch (error) {
    if (isRevisionConflict(error)) {
      markDraftStale('user', 'agent', agent.id)
      await resolveAgentConflict(agent.id, body, false)
    } else {
      notify.error(new Error(agentSaveErrorMessage(error)), { title: displayName(agent) })
    }
  } finally {
    busy.value = false
  }
}

async function saveAndPublishAgent() {
  const agent = configuringAgent.value
  if (!agent) return
  if (isAgentReadOnly(agent)) {
    status.warning({ key: 'agent-builtin', source: 'agents', message: t('settings.builtInCloneHint') })
    return
  }
  if (!agentEditName.value.trim()) {
    notify.error(new Error(t('settings.fieldRequired')), { title: t('settings.agentName') })
    return
  }
  const body = buildEditPayload(agent)
  busy.value = true
  try {
    const rev = agentEditRevision.value ?? agent.revision
    const draft = await api.updateAgentDraft(agent.id, body, rev != null ? String(rev) : null)
    const published = await api.publishAgent(agent.id, draft.revision != null ? String(draft.revision) : null)
    settleAgentDraft(agent.id)
    agentEditRevision.value = published.revision ?? draft.revision ?? agentEditRevision.value
    agentEditorBaseline = agentEditorSnapshot()
    emit('reload')
    void loadAgentVersionHistory(agent.id)
    notify.success(t('settings.agentPublished', { name: body.display_name ?? displayName(agent) }))
  } catch (error) {
    if (isRevisionConflict(error)) {
      markDraftStale('user', 'agent', agent.id)
      await resolveAgentConflict(agent.id, body, true)
    } else {
      notify.error(new Error(agentSaveErrorMessage(error)), { title: displayName(agent) })
    }
  } finally {
    busy.value = false
  }
}

async function setAgentEnabled(agent: AgentViewDto, enabled: boolean) {
  if (isAgentReadOnly(agent)) {
    status.warning({ key: 'agent-builtin', source: 'agents', message: t('settings.builtInCloneHint') })
    return
  }
  busy.value = true
  try {
    const rev = agent.revision
    const draft = await api.updateAgentDraft(agent.id, { enabled }, rev != null ? String(rev) : null)
    await api.publishAgent(agent.id, draft.revision != null ? String(draft.revision) : null)
    emit('reload')
    notify.success(displayName(agent))
  } catch (error) {
    notify.error(new Error(agentSaveErrorMessage(error)), { title: displayName(agent) })
  } finally {
    busy.value = false
  }
}

function agentModelStrategy(): Record<string, unknown> | null {
  if (agentRuntimeSelection.value === 'route') {
    return agentRuntimeRoutePurpose.value ? { kind: 'route', route_purpose: agentRuntimeRoutePurpose.value } : null
  }
  if (agentRuntimeSelection.value === 'fixed_model') {
    const selected = props.runtimeModels.find(
      (model) => modelOptionKey(model.provider_instance_id, model.model_id) === agentRuntimeModelKey.value,
    )
    if (selected) {
      return { kind: 'fixed', provider_instance_id: selected.provider_instance_id, model: selected.model_id }
    }
    const runtime = [...props.runtimeCliOptions, ...props.runtimeAcpOptions].find(
      (item) => item.provider_instance_id === agentRuntimeModelKey.value,
    )
    if (runtime) {
      return { kind: 'fixed', provider_instance_id: runtime.provider_instance_id, model: null }
    }
    const provider = props.providers.find(
      (item) => item.id === agentRuntimeModelKey.value && isExternalHarnessChannel(providerRuntimeChannel(item)),
    )
    return provider ? { kind: 'fixed', provider_instance_id: provider.id, model: null } : null
  }
  return { kind: 'inherit' }
}

function agentStrategySaveable(): boolean {
  return agentModelStrategy() !== null
}

function agentDefinitionDirty(agent: AgentViewDto): boolean {
  const same = (a: string[] | null | undefined, b: string[] | null | undefined) =>
    [...(a ?? [])].sort().join('\u0000') === [...(b ?? [])].sort().join('\u0000')
  return (
    !same(agentEditTools.value, agent.allowed_tools) ||
    !same(agentEditCapabilities.value, agent.capabilities) ||
    (agentEditSystemPrompt.value || '') !== (agent.system_prompt ?? '') ||
    (agentEditDescription.value || '') !== (agent.description ?? '') ||
    (agentEditName.value.trim() || '') !== (agent.display_name ?? agent.name ?? '') ||
    agentEditLayer.value !== normalizeAgentLayer(agent.layer) ||
    (agentEditRole.value.trim() || '') !== (agent.role ?? agent.agent_type ?? '') ||
    (agentEditBasePromptPipelineId.value || null) !== (agent.base_prompt_pipeline_id ?? null)
  )
}

async function saveAgentModelStrategy(agent: AgentViewDto) {
  const strategy = agentModelStrategy()
  if (!strategy) return
  agentRuntimeBusy.value = true
  if (agentDefinitionDirty(agent)) persistAgentDraft()
  try {
    const toolScope = agent.model_binding?.tool_scope_override
    const saved = await api.putAgentRuntimeBinding(agent.id, {
      mode: strategy.kind as 'inherit' | 'route' | 'fixed',
      provider_instance_id: (strategy as { provider_instance_id?: string }).provider_instance_id ?? null,
      model: (strategy as { model?: string | null }).model ?? null,
      route_purpose: (strategy as { route_purpose?: string }).route_purpose ?? null,
      ...(Array.isArray(toolScope) ? { tool_scope: toolScope } : {}),
    })
    const patched: AgentViewDto = { ...agent, model_binding: saved as unknown as AgentViewDto['model_binding'] }
    emit('patch-agent', patched)
    openAgentConfig(patched, true)
    if (agentDefinitionDirty(patched)) persistAgentDraft()
    else settleAgentDraft(agent.id)
    notify.success(t('settings.runtimeBindingSaved', { name: displayName(agent) }))
  } catch (error) {
    notify.error(new Error(agentSaveErrorMessage(error)), { title: displayName(agent) })
  } finally {
    agentRuntimeBusy.value = false
  }
}

async function archiveCurrentAgent() {
  const agent = configuringAgent.value
  if (!agent || isAgentReadOnly(agent)) return
  const confirmed = await confirm({
    title: t('settings.archiveAgent'),
    message: t('settings.archiveAgentConfirm', { name: displayName(agent) }),
    confirmLabel: t('settings.archiveAgent'),
    cancelLabel: t('common.cancel'),
  })
  if (!confirmed) return
  busy.value = true
  try {
    await api.archiveAgent(agent.id, agent.revision)
    closeAgentConfig()
    emit('reload')
    notify.success(t('settings.agentArchived', { name: displayName(agent) }))
  } catch (error) {
    notify.error(error, { title: t('settings.archiveAgent') })
  } finally {
    busy.value = false
  }
}

async function cloneAgent(targetAgent: AgentViewDto | null = configuringAgent.value) {
  const agent = targetAgent
  if (!agent) return
  agentCloneBusy.value = true
  try {
    const isActiveConfig = configuringAgent.value?.id === agent.id
    const loaded = isActiveConfig ? agent : await props.ensureAgentDefinition(agent.id)
    const source = loaded ?? agent
    const newName = t('common.copyName', { name: displayName(source) })
    const rawTools = isActiveConfig ? agentEditTools.value : source.allowed_tools ?? []
    const created = await api.createAgentDraft({
      slug: `${source.slug ?? source.id}-copy-${Date.now()}`,
      display_name: newName,
      layer: normalizeAgentLayer(source.layer),
      role: source.role ?? source.agent_type,
      source_kind: 'custom',
      base_prompt_pipeline_id: (isActiveConfig ? agentEditBasePromptPipelineId.value : source.base_prompt_pipeline_id) || null,
      capabilities: isActiveConfig ? [...agentEditCapabilities.value] : [...(source.capabilities ?? [])],
      tool_scope: rawTools.includes('*') ? ['*'] : [...rawTools],
      system_prompt: (isActiveConfig ? agentEditSystemPrompt.value : source.system_prompt) || null,
      description: (isActiveConfig ? agentEditDescription.value : source.description) || null,
      model_strategy: bindingFromModelStrategy(source).selection_kind === 'inherit' ? { kind: 'inherit' } : undefined,
      enabled: true,
    })
    emit('reload')
    if (created?.id) emit('update:configuringAgentId', created.id)
    notify.success(newName)
  } catch (error) {
    notify.error(error, { title: t('settings.cloneAgent') })
  } finally {
    agentCloneBusy.value = false
  }
}

// ── Create New Agent Sheet ──
const showCreateAgent = ref(false)
const newAgentName = ref('')
const newAgentSlug = ref('')
const newAgentLayer = ref<'operation' | 'execution'>('operation')
const newAgentRole = ref('')
const newAgentPipelineId = ref('')
const newAgentDescription = ref('')
const newAgentBusy = ref(false)

function openCreateAgent() {
  newAgentName.value = ''
  newAgentSlug.value = ''
  newAgentLayer.value = 'operation'
  newAgentRole.value = ''
  newAgentPipelineId.value = ''
  newAgentDescription.value = ''
  showCreateAgent.value = true
}

async function createAgent(publishImmediately = false) {
  const name = newAgentName.value.trim()
  if (!name) return
  newAgentBusy.value = true
  try {
    const created = await api.createAgentDraft({
      slug: newAgentSlug.value.trim() || undefined,
      display_name: name,
      layer: newAgentLayer.value,
      role: newAgentRole.value.trim() || (newAgentLayer.value === 'operation' ? 'session_coordinator' : 'code_writer'),
      source_kind: 'custom',
      base_prompt_pipeline_id: newAgentPipelineId.value || null,
      description: newAgentDescription.value.trim() || null,
      capabilities: [],
      tool_scope: [],
      enabled: true,
    })
    if (publishImmediately && created?.id) {
      await api.publishAgent(created.id, created.revision != null ? String(created.revision) : null)
    }
    showCreateAgent.value = false
    if (created?.id) {
      emit('update:selectedAgentId', created.id)
      emit('update:configuringAgentId', created.id)
    }
    emit('reload')
    notify.success(name)
  } catch (error) {
    notify.error(error, { title: t('settings.newAgent') })
  } finally {
    newAgentBusy.value = false
  }
}

// ── AI Autonomous Creation Assistant (`tinadec-agent-architect`) ──
async function launchAgentArchitect() {
  const prefill = t('settings.agentArchitectPrefill')
  try {
    const mod = await import('@/controllers/HomeController')
    mod.homeController?.updateDraft?.(prefill)
  } catch {
    // HomeController is optional under isolated component tests.
  }
  notify.info(t('settings.agentArchitectLaunched'))
  await router.push('/')
}

defineExpose({
  openAgentConfig,
  openAgentConfigFromDirectory,
  isEditorDirty,
  flushAgentDraft,
})
</script>

<template>
  <div class="center-page agent-center-page">
    <div class="ac-toolbar-row ac-agent-toolbar">
      <div class="agent-view-toggle">
        <button
          type="button"
          :class="['agent-view-btn', { active: viewMode !== 'list' }]"
          :title="t('settings.agentViewGrouped')"
          :aria-label="t('settings.agentViewGrouped')"
          :aria-pressed="viewMode !== 'list'"
          data-testid="agent-view-grouped-btn"
          @click="emit('update:viewMode', 'topology')"
        >
          <LayoutGrid :size="15" />
        </button>
        <button
          type="button"
          :class="['agent-view-btn', { active: viewMode === 'list' }]"
          :title="t('settings.agentViewCompact')"
          :aria-label="t('settings.agentViewCompact')"
          :aria-pressed="viewMode === 'list'"
          data-testid="agent-view-list-btn"
          @click="emit('update:viewMode', 'list')"
        >
          <List :size="15" />
        </button>
      </div>
      <div class="agent-tool-search ac-agent-search">
        <Search :size="14" />
        <UiInput v-model="agentQuery" :placeholder="t('settings.agentSearchPlaceholder')" />
      </div>
      <UiSelectField
        v-model="agentSourceFilter"
        class="compact"
        :aria-label="t('settings.agentFilterSource')"
        :options="[
          { value: 'all', label: t('settings.allSources') },
          ...agentSourceOptions.map((source) => ({ value: source, label: agentSourceKindLabel(source) })),
        ]"
      />
      <UiSelectField
        v-model="agentLayerFilter"
        class="compact"
        :aria-label="t('settings.agentFilterLayer')"
        :options="[
          { value: 'all', label: t('settings.agentFilterAllLayers') },
          { value: 'operation', label: t('settings.agentLayerPlanning') },
          { value: 'execution', label: t('settings.agentLayerExecution') },
        ]"
      />
      <UiSelectField
        v-model="agentStatusFilter"
        class="compact"
        :aria-label="t('settings.agentFilterStatus')"
        :options="[
          { value: 'all', label: t('settings.agentFilterAllStatuses') },
          { value: 'draft', label: t('settings.teamDraft') },
          { value: 'published', label: t('settings.teamStatusPublished') },
          { value: 'archived', label: t('settings.teamArchived') },
        ]"
      />
      <span class="ac-toolbar-readout">{{ filteredAgentCount }} / {{ agents.length }}</span>
      <UiBadge variant="outline" :title="t('settings.agentLayerCounts', { planning: operationAgents.length, execution: executionAgents.length })">
        {{ t('settings.agentLayerCounts', { planning: operationAgents.length, execution: executionAgents.length }) }}
      </UiBadge>
      <UiButton
        variant="outline"
        size="sm"
        data-testid="launch-agent-architect"
        :disabled="!canAccessBackend"
        :title="t('settings.agentArchitectHint')"
        @click="launchAgentArchitect"
      >
        <Sparkles :size="14" />
        <span>{{ t('settings.agentArchitectAction') }}</span>
      </UiButton>
      <UiButton size="sm" :disabled="!canAccessBackend" @click="openCreateAgent">
        <Plus :size="14" />
        <span>{{ t('settings.newAgent') }}</span>
      </UiButton>
    </div>

    <div v-if="loading && agents.length === 0" class="center-loading-state" aria-live="polite">
      <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
    </div>

    <p v-else-if="loading" class="center-stale-status" role="status">
      <RefreshCw :size="13" class="center-stale-spinner" aria-hidden="true" />
      {{ t('settings.refreshing') }}
    </p>

    <div v-if="failure" class="center-message" role="alert" data-testid="agent-center-read-failure">
      <Info :size="16" aria-hidden="true" />
      <div class="center-message-content">
        <strong>{{ t('settings.centerLoadFailed') }}</strong>
        <span>{{ failure.message }}</span>
        <span v-if="failure.code">{{ failure.code }}</span>
        <pre v-if="failure.details">{{ failure.details }}</pre>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading || !canAccessBackend" @click="emit('reload')">{{ t('settings.retry') }}</UiButton>
    </div>

    <details v-if="readDiagnostics.length" class="center-message-content" data-testid="agent-center-partial-failure">
      <summary>{{ t('settings.centerPartialLoadFailed') }}</summary>
      <p v-for="diagnostic in readDiagnostics" :key="diagnostic.source">
        {{ diagnostic.source }}: {{ diagnostic.error.message }}
        <span v-if="diagnostic.error.code"> · {{ diagnostic.error.code }}</span>
        <pre v-if="diagnostic.error.details">{{ diagnostic.error.details }}</pre>
      </p>
    </details>

    <div v-if="centerDiagnostics.length > 0" class="center-message warning center-diagnostics-message">
      <Info :size="16" />
      <div class="center-message-content">
        <strong>{{ t('settings.centerDiagnostics') }}</strong>
        <ul>
          <li
            v-for="diagnostic in centerDiagnostics"
            :key="`${diagnostic.code}:${diagnostic.source ?? ''}:${diagnostic.route_purpose ?? ''}:${diagnostic.agent_ids?.join(',') ?? ''}:${diagnostic.status ?? ''}`"
          >
            {{ centerDiagnosticLabel(diagnostic) }}
          </li>
        </ul>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="emit('reload')">{{ t('settings.retry') }}</UiButton>
    </div>

    <div
      v-if="canAccessBackend && loaded && !failure && !loading && agents.length === 0"
      class="center-empty-state center-empty-state-prominent"
    >
      <Bot :size="22" />
      <div>
        <strong>{{ t('settings.noAgents') }}</strong>
        <span>{{ t('settings.noAgentsHint') }}</span>
      </div>
    </div>

    <div class="center-workbench agent-workbench" :class="`view-${viewMode}`">
      <AgentDirectoryList
        :view-mode="viewMode"
        :agent-layer-filter="agentLayerFilter"
        :filtered-operation-agents="filteredOperationAgents"
        :filtered-execution-agents="filteredExecutionAgents"
        :directory-by-id="directoryById"
        :agents="agents"
        :selected-agent-id="selectedAgentId"
        :agent-generated-count="agentGeneratedCount"
        :can-access-backend="canAccessBackend"
        :agent-clone-busy="agentCloneBusy"
        :manifest-tool-count="manifestToolList.length"
        :agent-avatar-slug="agentAvatarSlug"
        :agent-runtime-summary="agentRuntimeSummary"
        :agent-runtime-channel="agentRuntimeChannel"
        @select="openAgentConfig($event)"
        @clone="cloneAgent($event)"
        @launch-architect="launchAgentArchitect"
      />

      <!-- Inspector -->
      <aside class="center-inspector agent-inspector" :aria-label="t('settings.centerInspector')">
        <div class="center-pane-heading">
          <div>
            <span>{{ t('settings.centerInspector') }}</span>
            <strong>{{ configuringAgent ? agentTypeLabel(configuringAgent.role ?? configuringAgent.agent_type) : t('settings.agentConfiguration') }}</strong>
          </div>
          <PanelRight :size="16" />
        </div>

        <div v-if="definitionFailures[configuringAgentId]" class="center-message-content" role="alert" data-testid="agent-definition-read-failure">
          <strong>{{ t('settings.agentDefinitionLoadFailed') }}</strong>
          <p>{{ definitionFailures[configuringAgentId]?.message }}</p>
          <UiButton variant="outline" size="sm" :disabled="loading || !canAccessBackend" @click="emit('reload')">{{ t('settings.retry') }}</UiButton>
        </div>

        <div v-else-if="configuringAgent" class="agent-detail-panel">
          <div v-if="agentDraftNotice" class="agent-draft-notice" :class="agentDraftNotice.status" role="status" data-testid="agent-draft-notice">
            <Info :size="14" aria-hidden="true" />
            <span>{{ agentDraftNotice.status === 'stale' ? t('settings.agentDraftStale') : t('settings.agentDraftRestored') }}</span>
            <UiButton v-if="agentDraftNotice.status === 'stale'" variant="ghost" size="sm" @click="loadAgentDraft">{{ t('settings.agentDraftApply') }}</UiButton>
            <UiButton variant="ghost" size="sm" @click="dropAgentDraft">{{ t('settings.agentDraftDiscard') }}</UiButton>
          </div>

          <div class="agent-detail-head">
            <div class="agent-card-icon pet" :class="{ execution: normalizeAgentLayer(configuringAgent.layer) === 'execution' }">
              <AgentPetAvatar :pet-slug="agentAvatarSlug(configuringAgent)" :size="44" :alt="displayName(configuringAgent)" />
            </div>
            <div>
              <h3>{{ displayName(configuringAgent) }}</h3>
              <p>{{ agentTypeLabel(configuringAgent.role ?? configuringAgent.agent_type) }} · {{ agentLayerLabel(configuringAgent.layer) }}</p>
            </div>
            <UiBadge :variant="sourceBadgeVariant(agentSourceKindFor(configuringAgent.id))">
              {{ agentSourceKindLabel(agentSourceKindFor(configuringAgent.id)) }}
            </UiBadge>
            <UiBadge :variant="configuringAgent.status === 'published' ? 'default' : 'secondary'" class="agent-status-badge">
              {{ configuringAgent.status === 'published' ? t('settings.agentStatusPublished', { version: configuringAgent.version ?? 1 }) : t('settings.agentStatusDraft') }}
            </UiBadge>
            <UiButton
              v-if="!configuringIsReadOnly"
              variant="ghost"
              size="icon"
              :title="t('settings.archiveAgent')"
              :aria-label="t('settings.archiveAgent')"
              :disabled="busy"
              @click="archiveCurrentAgent"
            >
              <Archive :size="16" />
            </UiButton>
            <UiButton variant="ghost" size="icon" :title="t('settings.closeConfig')" @click="closeAgentConfig">
              <X :size="16" />
            </UiButton>
          </div>

          <AgentIdentitySection
            :configuring-agent="configuringAgent"
            :configuring-directory-item="configuringDirectoryItem"
            :configuring-runtime-binding="configuringRuntimeBinding"
            :configuring-is-read-only="configuringIsReadOnly"
            :show-provenance-card="showProvenanceCard"
            :configuring-provenance="configuringProvenance"
            :directory-by-id="directoryById"
            :agents="agents"
            :used-by-modes="usedByModes"
            :edit-name="agentEditName"
            :edit-layer="agentEditLayer"
            :edit-role="agentEditRole"
            :edit-description="agentEditDescription"
            :busy="busy"
            :agent-avatar-busy="agentAvatarBusy"
            :configuring-avatar-preference="configuringAvatarPreference"
            :agent-avatar-pool="agentAvatarPool"
            @update:edit-name="agentEditName = $event"
            @update:edit-layer="agentEditLayer = $event"
            @update:edit-role="agentEditRole = $event"
            @update:edit-description="agentEditDescription = $event"
            @set-enabled="setAgentEnabled(configuringAgent, $event)"
            @choose-avatar="chooseAgentAvatar"
            @open-mode="emit('open-mode', $event)"
          />

          <AgentRuntimeSection
            :configuring-agent="configuringAgent"
            :configuring-agent-id="configuringAgentId"
            :configuring-directory-item="configuringDirectoryItem"
            :configuring-runtime-binding="configuringRuntimeBinding"
            :configuring-legacy-warning="configuringLegacyWarning"
            :runtime-selection="agentRuntimeSelection"
            :runtime-model-key="agentRuntimeModelKey"
            :runtime-route-purpose="agentRuntimeRoutePurpose"
            :runtime-model-query="agentRuntimeModelQuery"
            :routes="routes"
            :providers="providers"
            :runtime-models="runtimeModels"
            :runtime-cli-options="runtimeCliOptions"
            :runtime-acp-options="runtimeAcpOptions"
            :runtime-busy="agentRuntimeBusy"
            :strategy-saveable="agentStrategySaveable()"
            :runtime-summary="agentRuntimeSummary(configuringAgentId)"
            @update:runtime-selection="agentRuntimeSelection = $event"
            @update:runtime-model-key="agentRuntimeModelKey = $event"
            @update:runtime-route-purpose="agentRuntimeRoutePurpose = $event"
            @update:runtime-model-query="agentRuntimeModelQuery = $event"
            @save="saveAgentModelStrategy(configuringAgent)"
          />

          <AgentToolsSection
            :configuring-agent="configuringAgent"
            :configuring-is-read-only="configuringIsReadOnly"
            :available-tools="availableTools"
            :harness-manifest="harnessManifest"
            :edit-tools="agentEditTools"
            :edit-capabilities="agentEditCapabilities"
            :pending-governance-count="pendingGovernanceCount"
            @update:edit-tools="agentEditTools = $event"
            @update:edit-capabilities="agentEditCapabilities = $event"
          />

          <AgentPromptPipelineSection
            :configuring-agent="configuringAgent"
            :configuring-is-read-only="configuringIsReadOnly"
            :edit-base-prompt-pipeline-id="agentEditBasePromptPipelineId"
            :edit-system-prompt="agentEditSystemPrompt"
            :pipeline-options="pipelineOptions"
            :version-history="agentVersionHistory"
            :version-history-error="agentVersionHistoryError"
            :versions-loading="agentVersionsLoading"
            @update:edit-base-prompt-pipeline-id="agentEditBasePromptPipelineId = $event"
            @update:edit-system-prompt="agentEditSystemPrompt = $event"
            @reload-versions="loadAgentVersionHistory(configuringAgent.id)"
          />

          <div class="agent-save-bar">
            <template v-if="configuringIsReadOnly">
              <UiButton :disabled="agentCloneBusy" @click="cloneAgent(configuringAgent)">
                <Plus :size="14" />
                <span>{{ t('settings.cloneAgent') }}</span>
              </UiButton>
              <span class="agent-builtin-save-hint">{{ t('settings.builtInCloneHint') }}</span>
            </template>
            <template v-else>
              <span v-if="agentEditRevision !== null" class="agent-revision-hint">{{ t('settings.agentRevision', { revision: agentEditRevision }) }}</span>
              <UiButton variant="outline" :disabled="busy" data-testid="save-agent-draft-btn" @click="saveAgentDraftOnly">
                <Save :size="14" />
                <span>{{ t('settings.saveAgentDraftOnly') }}</span>
              </UiButton>
              <UiButton :disabled="busy" data-testid="publish-agent-btn" @click="saveAndPublishAgent">
                <Send :size="14" />
                <span>{{ t('settings.saveAndPublishAgent') }}</span>
              </UiButton>
            </template>
          </div>
        </div>
        <div v-else-if="canAccessBackend && loaded && !failure" class="center-empty-state inspector-empty">
          <PanelRight :size="20" />
          <span>{{ agents.length > 0 ? t('settings.pleaseOpenAgentConfig') : t('settings.noAgents') }}</span>
        </div>
      </aside>
    </div>

    <AgentCreateSheet
      v-model:open="showCreateAgent"
      v-model:name="newAgentName"
      v-model:slug="newAgentSlug"
      v-model:layer="newAgentLayer"
      v-model:role="newAgentRole"
      v-model:pipeline-id="newAgentPipelineId"
      v-model:description="newAgentDescription"
      :busy="newAgentBusy"
      :pipeline-options="pipelineOptions"
      @create="createAgent"
    />
  </div>
</template>
