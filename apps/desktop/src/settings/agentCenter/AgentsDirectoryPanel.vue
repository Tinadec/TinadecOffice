<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import {
  Archive,
  Bot,
  CopyPlus,
  Cpu,
  Info,
  LayoutGrid,
  List,
  PanelRight,
  Plus,
  RefreshCw,
  Route,
  Save,
  Search,
  Send,
  ShieldCheck,
  Shuffle,
  Sparkles,
  Workflow,
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
import { toErrorState, type ErrorState } from '@/composables/useErrorState'
import { centerDiagnosticLabel as centerDiagnosticText, providerStatusLabel } from '@/settings/modelCenterLabels'
import { useStatusLabels } from '@/lib/statusLabels'
import { bindingFromModelStrategy, legacyRouteWarning, modelOptionKey, runtimeSourceSummary } from '@/runtimeCenterView'
import { manifestTools } from '@/toolCatalog'
import { clearDraft, markDraftStale, readDraft, writeDraft, type StoredDraft } from '@/settings/drafts/draftStore'
import { requestToolAgent } from '@/lib/pageRequests'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSelectField, UiSheet, UiSkeleton, UiSwitch } from '@/components/ui'

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
  viewMode: 'topology' | 'list'
  ensureAgentDefinition: (agentId: string) => Promise<AgentViewDto | null>
}>()

const emit = defineEmits<{
  'update:viewMode': [mode: 'topology' | 'list']
  'update:selectedAgentId': [id: string]
  'update:configuringAgentId': [id: string]
  'patch-agent': [agent: AgentViewDto]
  reload: []
}>()

const { t, te, locale } = useI18n()
const router = useRouter()
const { riskLabel } = useStatusLabels()
const { notify, confirm, status } = useNotifications()

const definitionStatusLabel = (value: string) => (te(`agentCenter.filter.${value}`) ? t(`agentCenter.filter.${value}`) : value)
const statusLabel = (value: string) => providerStatusLabel(t, value)
const centerDiagnosticLabel = (diagnostic: CenterDiagnosticDto) => centerDiagnosticText(t, diagnostic)

const directoryById = computed(() => new Map(props.agentDirectory.map((item) => [item.id, item])))

function normalizeAgentLayer(layer: unknown): 'operation' | 'execution' {
  const v = String(layer ?? '').trim().toLowerCase()
  if (v === 'planning' || v === 'operation') return 'operation'
  return v === 'execution' ? 'execution' : 'operation'
}

function displayName(agent: AgentViewDto): string {
  const item = directoryById.value.get(agent.id)
  const baseline = agent.display_name || agent.name || agent.slug || agent.id
  return pickLocalized(agent.i18n ?? item?.i18n, locale.value, 'display_name', baseline) ?? baseline
}

function displayDescription(agent: AgentViewDto): string {
  const item = directoryById.value.get(agent.id)
  const baseline = agent.description ?? ''
  return pickLocalized(agent.i18n ?? item?.i18n, locale.value, 'description', baseline) ?? baseline
}

function agentSourceKindFor(agentId: string): string {
  const item = directoryById.value.get(agentId)
  if (item?.source_kind) return item.source_kind
  return props.agents.find((a) => a.id === agentId)?.source_kind ?? 'custom'
}

function isAgentReadOnly(agent: AgentViewDto | null): boolean {
  if (!agent) return true
  if (agent.is_built_in) return true
  const sourceKind = agentSourceKindFor(agent.id)
  return sourceKind === 'pack' || sourceKind === 'built_in'
}

function agentSourceKindLabel(kind: string): string {
  if (kind === 'custom') return t('settings.agentSourceCustom')
  if (kind === 'pack') return t('settings.agentSourcePack')
  if (kind === 'agent_generated') return t('settings.agentSourceAgentGenerated')
  if (kind === 'bootstrap') return t('settings.agentSourceBootstrap')
  if (kind === 'missing_reference') return t('settings.agentSourceMissing')
  return kind
}

function sourceBadgeVariant(kind: string): 'default' | 'secondary' | 'outline' {
  if (kind === 'agent_generated') return 'default'
  if (kind === 'custom') return 'secondary'
  return 'outline'
}

function agentTypeLabel(type: string): string {
  const map: Record<string, string> = {
    session_coordinator: t('settings.agentTypeSessionCoordinator'),
    task_executor: t('settings.agentTypeTaskExecutor'),
    governance_reviewer: t('settings.agentTypeGovernanceReviewer'),
    context_maintenance: t('settings.agentTypeContextCompressor'),
    exploration_planner: t('settings.agentTypeExplorationPlanner'),
    solo_master: t('settings.agentTypeSoloMaster'),
    search: t('settings.agentTypeSearchSpecialist'),
    global_engineering: t('settings.agentTypeCodeWriter'),
    artex_director: t('settings.agentTypeArtexDirector'),
    artex_planner: t('settings.agentTypeExplorationPlanner'),
    artex_worker: t('settings.agentTypeTaskExecutor'),
    artex_verifier: t('settings.agentTypeReviewExecutor'),
    meeting: t('settings.agentTypeMeeting'),
    'context-compressor': t('settings.agentTypeContextCompressor'),
    'prompt-context-engineer': t('settings.agentTypePromptContextEngineer'),
    evolver: t('settings.agentTypeEvolver'),
    'tool-assistant': t('settings.agentTypeToolAssistant'),
    supervisor: t('settings.agentTypeSupervisor'),
    'skill-learner': t('settings.agentTypeSkillLearner'),
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
    chair: t('settings.agentTypeMeeting'),
    planner: t('settings.agentTypeTaskPlanner'),
    'tool-manager': t('settings.agentTypeToolAssistant'),
    'evolution-algorithm': t('settings.agentTypeEvolver'),
    executor: t('settings.agentTypeCodeWriter'),
    reviewer: t('settings.agentTypeSupervisor'),
  }
  return map[type] ?? type
}

function agentLayerLabel(layer: string): string {
  const map: Record<string, string> = {
    operation: t('settings.agentLayerPlanning'),
    planning: t('settings.agentLayerPlanning'),
    execution: t('settings.agentLayerExecution'),
    evolution: t('settings.agentLayerEvolution'),
  }
  return map[layer] ?? layer
}

// ── Filters & Role Roster ──
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

function creatorAgentLabel(creatorId: string | null | undefined): string {
  if (!creatorId) return '—'
  const found = props.agents.find((a) => a.id === creatorId || a.slug === creatorId)
  return found ? `${displayName(found)} (${found.slug ?? creatorId})` : creatorId
}

const agentEditName = ref('')
const agentEditLayer = ref<'operation' | 'execution'>('operation')
const agentEditRole = ref('')
const agentEditBasePromptPipelineId = ref('')
const agentEditTools = ref<string[]>([])
const agentEditCapabilities = ref<string[]>([])
const agentEditSystemPrompt = ref('')
const agentEditDescription = ref('')
const agentEditRevision = ref<number | null>(null)
const agentNewCapability = ref('')
const agentToolQuery = ref('')
const agentToolSourceFilter = ref('all')
const agentToolRiskFilter = ref('all')

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

// ── Pipeline Options ──
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

// ── Tools & Wildcard Handling ──
const manifestToolList = computed(() => manifestTools(props.harnessManifest, props.availableTools))
const hasWildcardTools = computed(() => agentEditTools.value.includes('*'))

function isToolSelected(toolId: string): boolean {
  return hasWildcardTools.value || agentEditTools.value.includes(toolId)
}

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

const toolSourceOptions = computed(() => Array.from(new Set(manifestToolList.value.map((tool) => tool.source))).sort())
const toolRiskOptions = computed(() => Array.from(new Set(manifestToolList.value.map((tool) => tool.risk))).sort())

const agentToolSelectionSummary = computed(() => {
  const total = manifestToolList.value.length
  if (hasWildcardTools.value) {
    const approval = manifestToolList.value.filter((t) => t.requires_approval).length
    return { total, selected: total, approval, wildcard: true }
  }
  const selected = agentEditTools.value.length
  const approval = manifestToolList.value.filter((t) => t.requires_approval && agentEditTools.value.includes(t.id)).length
  return { total, selected, approval, wildcard: false }
})

function toggleAgentTool(toolId: string) {
  if (hasWildcardTools.value) {
    agentEditTools.value = manifestToolList.value.map((item) => item.id).filter((id) => id !== toolId)
    return
  }
  const idx = agentEditTools.value.indexOf(toolId)
  if (idx >= 0) agentEditTools.value.splice(idx, 1)
  else agentEditTools.value.push(toolId)
}

function removeAgentCapability(cap: string) {
  const idx = agentEditCapabilities.value.indexOf(cap)
  if (idx >= 0) agentEditCapabilities.value.splice(idx, 1)
}

function addAgentCapability() {
  const cap = agentNewCapability.value.trim()
  if (cap && !agentEditCapabilities.value.includes(cap)) {
    agentEditCapabilities.value.push(cap)
    agentNewCapability.value = ''
  }
}

function agentToolSummaryText(agent: AgentViewDto): string {
  const tools = agent.allowed_tools ?? []
  if (tools.includes('*')) return `* (${manifestToolList.value.length || '*'})`
  return String(tools.length)
}

// ── Runtime Model Binding Helpers ──
const agentRuntimeBindings = computed(() =>
  Object.fromEntries(props.agents.map((agent) => [agent.id, bindingFromModelStrategy(agent, agent.model_binding)])),
)
const configuringRuntimeBinding = computed(() => {
  const agent = props.agents.find((item) => item.id === props.configuringAgentId)
  return agent ? bindingFromModelStrategy(agent, agent.model_binding) : null
})
const configuringLegacyWarning = computed(() => legacyRouteWarning(configuringRuntimeBinding.value))

function runtimeQueryMatches(query: string, ...values: Array<string | null | undefined>) {
  const normalized = query.trim().toLocaleLowerCase()
  if (!normalized) return true
  return values.some((value) => value?.toLocaleLowerCase().includes(normalized))
}

const filteredRuntimeModels = computed(() =>
  props.runtimeModels.filter((model) =>
    runtimeQueryMatches(
      agentRuntimeModelQuery.value,
      model.model_id,
      model.provider_display_name,
      model.provider_instance_id,
      model.status,
      ...model.configuration_sources,
      ...model.route_purposes,
    ),
  ),
)
const filteredRuntimeCliOptions = computed(() =>
  props.runtimeCliOptions.filter((runtime) =>
    runtimeQueryMatches(
      agentRuntimeModelQuery.value,
      runtime.display_name,
      runtime.runtime_id,
      runtime.driver,
      runtime.status,
      runtime.binary_path,
      runtime.home_path,
    ),
  ),
)
const filteredRuntimeAcpOptions = computed(() =>
  props.runtimeAcpOptions.filter((runtime) =>
    runtimeQueryMatches(
      agentRuntimeModelQuery.value,
      runtime.display_name,
      runtime.runtime_id,
      runtime.source,
      runtime.driver,
      runtime.status,
      runtime.command,
    ),
  ),
)

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

function agentPreviewLine(
  modeSlug: string,
  nodeKey: string,
  preview: { expected_selection?: { provider_instance_id?: string; model?: string | null } | null },
): string {
  const selection = preview.expected_selection
  const target = selection ? `${selection.provider_instance_id ?? '—'}${selection.model ? ` · ${selection.model}` : ''}` : '—'
  return `${modeSlug}:${nodeKey} → ${target}`
}

function agentInvocationLine(invocation: {
  provider_instance_id: string
  model?: string | null
  status: string
  completed_at?: string | null
}): string {
  const model = invocation.model ? ` · ${invocation.model}` : ''
  const time = invocation.completed_at ? ` · ${invocation.completed_at}` : ''
  return `${invocation.provider_instance_id}${model} · ${invocation.status}${time}`
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
  agentNewCapability.value = ''
  agentToolQuery.value = ''
  agentToolSourceFilter.value = 'all'
  agentToolRiskFilter.value = 'all'

  const binding = bindingFromModelStrategy(agent, agent.model_binding)
  agentRuntimeSelection.value = binding?.selection_kind ?? 'inherit'
  agentRuntimeModelKey.value = binding?.provider_instance_id
    ? binding.model_id
      ? modelOptionKey(binding.provider_instance_id, binding.model_id)
      : binding.provider_instance_id
    : ''
  agentRuntimeRoutePurpose.value =
    binding?.route_purpose || (agent as AgentViewDto & { model_route_purpose?: string }).model_route_purpose || props.routes[0]?.purpose || ''
  agentRuntimeModelQuery.value = ''
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
    return runtime ? { kind: 'fixed', provider_instance_id: runtime.provider_instance_id, model: null } : null
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
          :class="['agent-view-btn', { active: viewMode === 'topology' }]"
          :title="t('settings.roleRosterView')"
          :aria-label="t('settings.roleRosterView')"
          :aria-pressed="viewMode === 'topology'"
          @click="emit('update:viewMode', 'topology')"
        >
          <LayoutGrid :size="15" />
        </button>
        <button
          :class="['agent-view-btn', { active: viewMode === 'list' }]"
          :title="t('settings.listView')"
          :aria-label="t('settings.listView')"
          :aria-pressed="viewMode === 'list'"
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
      <!-- Compact Rail (List Mode) -->
      <aside v-if="viewMode === 'list'" class="center-resource-rail" :aria-label="t('settings.centerResources')">
        <div class="center-pane-heading">
          <div>
            <span>{{ t('settings.centerResources') }}</span>
            <strong>{{ t('settings.agentProfiles') }}</strong>
          </div>
          <UiBadge v-if="agentGeneratedCount > 0" variant="default">{{ agentGeneratedCount }}</UiBadge>
        </div>

        <section v-if="agentLayerFilter !== 'execution'" class="agent-column compact">
          <div class="model-section-header">
            <h3>{{ t('settings.planningLayer') }}</h3>
            <UiBadge variant="secondary">{{ filteredOperationAgents.length }}</UiBadge>
          </div>
          <article
            v-for="agent in filteredOperationAgents"
            :key="agent.id"
            class="agent-card"
            :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
          >
            <button class="agent-card-select" @click="openAgentConfig(agent)">
              <div class="agent-card-icon pet">
                <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="30" :alt="displayName(agent)" />
              </div>
              <div class="agent-card-main">
                <div class="agent-card-title-row">
                  <strong>{{ displayName(agent) || agentTypeLabel(agent.agent_type) }}</strong>
                  <UiBadge :variant="sourceBadgeVariant(agentSourceKindFor(agent.id))" class="agent-source-pill">
                    {{ agentSourceKindLabel(agentSourceKindFor(agent.id)) }}
                  </UiBadge>
                </div>
                <span>{{ displayDescription(agent) || agentTypeLabel(agent.role ?? agent.agent_type) }}</span>
                <small :title="agentRuntimeSummary(agent.id)">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</small>
              </div>
              <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
                {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
              </UiBadge>
            </button>
          </article>
        </section>

        <section v-if="agentLayerFilter !== 'operation'" class="agent-column compact">
          <div class="model-section-header">
            <h3>{{ t('settings.executionLayer') }}</h3>
            <UiBadge variant="secondary">{{ filteredExecutionAgents.length }}</UiBadge>
          </div>
          <article
            v-for="agent in filteredExecutionAgents"
            :key="agent.id"
            class="agent-card"
            :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
          >
            <button class="agent-card-select" @click="openAgentConfig(agent)">
              <div class="agent-card-icon pet execution">
                <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="30" :alt="displayName(agent)" />
              </div>
              <div class="agent-card-main">
                <div class="agent-card-title-row">
                  <strong>{{ displayName(agent) || agentTypeLabel(agent.agent_type) }}</strong>
                  <UiBadge :variant="sourceBadgeVariant(agentSourceKindFor(agent.id))" class="agent-source-pill">
                    {{ agentSourceKindLabel(agentSourceKindFor(agent.id)) }}
                  </UiBadge>
                </div>
                <span>{{ displayDescription(agent) || agentTypeLabel(agent.role ?? agent.agent_type) }}</span>
                <small :title="agentRuntimeSummary(agent.id)">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</small>
              </div>
              <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
                {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
              </UiBadge>
            </button>
          </article>
        </section>

        <p v-if="!filteredOperationAgents.length && !filteredExecutionAgents.length" class="quiet">
          {{ t('settings.noFilteredAgents') }}
        </p>
      </aside>

      <!-- Role Roster Card View (Card Mode) -->
      <main v-else class="center-resource-stage" data-testid="agent-role-roster">
        <div class="roster-stage-head">
          <div>
            <h3>{{ t('settings.roleRosterTitle') }}</h3>
            <p class="quiet">{{ t('settings.roleRosterHint') }}</p>
          </div>
          <UiButton variant="ghost" size="sm" :disabled="!canAccessBackend" @click="launchAgentArchitect">
            <Sparkles :size="14" />
            <span>{{ t('settings.agentArchitectHintShort') }}</span>
          </UiButton>
        </div>

        <section v-if="agentLayerFilter !== 'execution'" class="roster-layer-section">
          <div class="model-section-header">
            <h3>{{ t('settings.planningLayer') }}</h3>
            <UiBadge variant="secondary">{{ filteredOperationAgents.length }}</UiBadge>
          </div>
          <div class="roster-card-grid">
            <article
              v-for="agent in filteredOperationAgents"
              :key="agent.id"
              class="agent-card roster-card"
              :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
            >
              <button class="agent-card-select roster-card-select" @click="openAgentConfig(agent)">
                <div class="roster-card-top">
                  <div class="agent-card-icon pet">
                    <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="34" :alt="displayName(agent)" />
                  </div>
                  <div class="roster-card-identity">
                    <strong>{{ displayName(agent) || agentTypeLabel(agent.agent_type) }}</strong>
                    <span>{{ agent.slug ?? agent.id.slice(0, 8) }} · {{ agentTypeLabel(agent.role ?? agent.agent_type) }}</span>
                  </div>
                  <UiBadge :variant="sourceBadgeVariant(agentSourceKindFor(agent.id))">
                    {{ agentSourceKindLabel(agentSourceKindFor(agent.id)) }}
                  </UiBadge>
                </div>
                <p class="roster-card-desc">{{ displayDescription(agent) || t('settings.agentNoDescription') }}</p>
                <div class="roster-card-foot">
                  <small :title="agentRuntimeSummary(agent.id)">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</small>
                  <div class="roster-card-badges">
                    <UiBadge variant="outline">{{ agentToolSummaryText(agent) }}</UiBadge>
                    <UiBadge :variant="agent.status === 'published' ? 'default' : 'secondary'">
                      {{ definitionStatusLabel(agent.status ?? 'draft') }}
                    </UiBadge>
                  </div>
                </div>
              </button>
              <button
                v-if="isAgentReadOnly(agent)"
                type="button"
                class="roster-quick-clone"
                :title="t('settings.cloneAgent')"
                :disabled="agentCloneBusy"
                @click.stop="cloneAgent(agent)"
              >
                <CopyPlus :size="13" />
                <span>{{ t('settings.cloneAgent') }}</span>
              </button>
            </article>
          </div>
        </section>

        <section v-if="agentLayerFilter !== 'operation'" class="roster-layer-section">
          <div class="model-section-header">
            <h3>{{ t('settings.executionLayer') }}</h3>
            <UiBadge variant="secondary">{{ filteredExecutionAgents.length }}</UiBadge>
          </div>
          <div class="roster-card-grid">
            <article
              v-for="agent in filteredExecutionAgents"
              :key="agent.id"
              class="agent-card roster-card"
              :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
            >
              <button class="agent-card-select roster-card-select" @click="openAgentConfig(agent)">
                <div class="roster-card-top">
                  <div class="agent-card-icon pet execution">
                    <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="34" :alt="displayName(agent)" />
                  </div>
                  <div class="roster-card-identity">
                    <strong>{{ displayName(agent) || agentTypeLabel(agent.agent_type) }}</strong>
                    <span>{{ agent.slug ?? agent.id.slice(0, 8) }} · {{ agentTypeLabel(agent.role ?? agent.agent_type) }}</span>
                  </div>
                  <UiBadge :variant="sourceBadgeVariant(agentSourceKindFor(agent.id))">
                    {{ agentSourceKindLabel(agentSourceKindFor(agent.id)) }}
                  </UiBadge>
                </div>
                <p class="roster-card-desc">{{ displayDescription(agent) || t('settings.agentNoDescription') }}</p>
                <div class="roster-card-foot">
                  <small :title="agentRuntimeSummary(agent.id)">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</small>
                  <div class="roster-card-badges">
                    <UiBadge variant="outline">{{ agentToolSummaryText(agent) }}</UiBadge>
                    <UiBadge :variant="agent.status === 'published' ? 'default' : 'secondary'">
                      {{ definitionStatusLabel(agent.status ?? 'draft') }}
                    </UiBadge>
                  </div>
                </div>
              </button>
              <button
                v-if="isAgentReadOnly(agent)"
                type="button"
                class="roster-quick-clone"
                :title="t('settings.cloneAgent')"
                :disabled="agentCloneBusy"
                @click.stop="cloneAgent(agent)"
              >
                <CopyPlus :size="13" />
                <span>{{ t('settings.cloneAgent') }}</span>
              </button>
            </article>
          </div>
        </section>

        <p v-if="!filteredOperationAgents.length && !filteredExecutionAgents.length" class="quiet">
          {{ t('settings.noFilteredAgents') }}
        </p>
      </main>

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

          <!-- Provenance (`agent_generated`) -->
          <div v-if="showProvenanceCard" class="agent-config-section agent-provenance-box" data-testid="agent-provenance-section">
            <div class="agent-config-section-title">
              <Sparkles :size="14" />
              <span>{{ t('settings.agentProvenanceTitle') }}</span>
            </div>
            <div class="agent-detail-grid">
              <div>
                <span>{{ t('settings.agentProvenanceCreator') }}</span>
                <strong>{{ creatorAgentLabel(configuringProvenance?.created_by_agent_id) }}</strong>
              </div>
              <div>
                <span>{{ t('settings.agentProvenanceRun') }}</span>
                <strong>{{ configuringProvenance?.created_by_run_id || '—' }}</strong>
              </div>
            </div>
            <p v-if="configuringProvenance?.reason" class="agent-config-hint">
              {{ t('settings.agentProvenanceReason') }}: {{ configuringProvenance.reason }}
            </p>
          </div>

          <!-- 身份与基础定义（名称 / 层级 / 角色 / 基础提示词管线） -->
          <div class="agent-config-section">
            <div class="agent-config-section-title">{{ t('settings.agentIdentitySection') }}</div>
            <div class="agent-identity-grid">
              <div>
                <span>{{ t('settings.agentSlugLabel') }}</span>
                <strong>{{ configuringAgent.slug ?? configuringAgent.id.slice(0, 8) }}</strong>
              </div>
              <div>
                <span>{{ t('settings.routePurpose') }}</span>
                <strong>{{ configuringRuntimeBinding?.route_purpose ?? configuringAgent.model_route_purpose ?? '—' }}</strong>
              </div>
            </div>

            <div class="agent-definition-fields">
              <div class="settings-field">
                <UiLabel>{{ t('settings.agentName') }}</UiLabel>
                <UiInput
                  v-model="agentEditName"
                  data-testid="agent-edit-name"
                  :disabled="configuringIsReadOnly"
                  :placeholder="t('settings.agentName')"
                />
              </div>
              <div class="agent-identity-grid">
                <div class="settings-field">
                  <UiLabel>{{ t('settings.agentLayer') }}</UiLabel>
                  <UiSelectField
                    v-model="agentEditLayer"
                    data-testid="agent-edit-layer"
                    :disabled="configuringIsReadOnly"
                    :aria-label="t('settings.agentLayer')"
                    :options="[
                      { value: 'operation', label: t('settings.agentLayerPlanning') },
                      { value: 'execution', label: t('settings.agentLayerExecution') },
                    ]"
                  />
                </div>
                <div class="settings-field">
                  <UiLabel>{{ t('settings.agentRole') }}</UiLabel>
                  <UiInput
                    v-model="agentEditRole"
                    data-testid="agent-edit-role"
                    :disabled="configuringIsReadOnly"
                    placeholder="session_coordinator / code_writer"
                  />
                </div>
              </div>
              <div class="settings-field">
                <UiLabel>{{ t('settings.agentBasePipeline') }}</UiLabel>
                <UiSelectField
                  v-model="agentEditBasePromptPipelineId"
                  data-testid="agent-edit-pipeline"
                  :disabled="configuringIsReadOnly"
                  :aria-label="t('settings.agentBasePipeline')"
                  :options="pipelineOptions"
                />
              </div>
            </div>

            <div class="agent-config-switch">
              <div>
                <strong>{{ t('settings.agentEnabled') }}</strong>
                <span>{{ configuringIsReadOnly ? t('settings.builtInAgent') : t('settings.agentEnabledHint') }}</span>
                <small v-if="configuringIsReadOnly" class="agent-builtin-label">{{ t('settings.builtInCloneHint') }}</small>
              </div>
              <UiSwitch
                :model-value="configuringAgent.enabled"
                :disabled="busy || configuringIsReadOnly"
                @update:model-value="setAgentEnabled(configuringAgent, $event)"
              />
            </div>
          </div>

          <!-- 形象 -->
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

          <!-- 目录证据 -->
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
                <strong>{{ configuringRuntimeBinding?.route_purpose ?? configuringAgent.model_route_purpose ?? '—' }}</strong>
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
                  ...filteredRuntimeModels.map((model) => ({
                    value: modelOptionKey(model.provider_instance_id, model.model_id),
                    label: `${model.model_id} · ${model.provider_display_name ?? model.provider_instance_id} · ${statusLabel(model.status)}`,
                  })),
                  ...filteredRuntimeCliOptions.map((runtime) => ({
                    value: runtime.provider_instance_id,
                    label: `${runtime.display_name} · CLI · ${statusLabel(runtime.status)}`,
                  })),
                  ...filteredRuntimeAcpOptions.map((runtime) => ({
                    value: runtime.provider_instance_id ?? runtime.runtime_id,
                    label: `${runtime.display_name} · ACP · ${statusLabel(runtime.status)}`,
                  })),
                ]"
              />
              <p v-if="filteredRuntimeModels.length === 0 && filteredRuntimeCliOptions.length === 0 && filteredRuntimeAcpOptions.length === 0" class="agent-config-hint">
                {{ t('settings.noRuntimeMatches') }}
              </p>
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
                :disabled="configuringIsReadOnly"
                :placeholder="t('settings.agentDescriptionPlaceholder')"
              ></textarea>
            </div>
          </div>

          <!-- 工具绑定（支持 '*' 通配高亮与计数） -->
          <div class="agent-config-section">
            <div class="agent-config-section-title">
              {{ t('settings.agentToolsPermissions') }}
              <UiBadge v-if="agentToolSelectionSummary.wildcard" variant="default" data-testid="agent-wildcard-tools-badge">
                {{ t('settings.allToolsWildcard', { total: agentToolSelectionSummary.total }) }}
              </UiBadge>
              <UiBadge v-else variant="outline">
                {{ agentToolSelectionSummary.selected }}/{{ agentToolSelectionSummary.total }}
              </UiBadge>
              <UiBadge v-if="agentToolSelectionSummary.approval > 0" variant="secondary">
                {{ t('settings.approvalRequired') }} {{ agentToolSelectionSummary.approval }}
              </UiBadge>
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
            <div v-if="configuringIsReadOnly" class="agent-builtin-hint">
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
                    active: isToolSelected(tool.id),
                    risky: tool.requires_approval,
                  }"
                  :disabled="configuringIsReadOnly"
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
              <UiButton variant="ghost" size="sm" :disabled="configuringIsReadOnly" @click="agentEditTools = manifestToolList.filter((item) => !item.requires_approval).map((item) => item.id)">
                {{ t('settings.selectReadOnlyTools') }}
              </UiButton>
              <UiButton variant="ghost" size="sm" :disabled="configuringIsReadOnly" @click="agentEditTools = manifestToolList.map((item) => item.id)">
                {{ t('settings.selectAllReadWrite') }}
              </UiButton>
              <UiButton variant="ghost" size="sm" :disabled="configuringIsReadOnly" @click="agentEditTools = ['*']">
                {{ t('settings.allowAllTools') }}
              </UiButton>
              <UiButton variant="ghost" size="sm" :disabled="configuringIsReadOnly" @click="agentEditTools = []">
                {{ t('settings.clearSelection') }}
              </UiButton>
            </div>

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
                <button class="agent-cap-remove" :disabled="configuringIsReadOnly" @click="removeAgentCapability(cap)">×</button>
              </span>
            </div>
            <div class="agent-cap-add-row">
              <UiInput
                v-model="agentNewCapability"
                :disabled="configuringIsReadOnly"
                :placeholder="t('settings.newCapabilityPlaceholder')"
                @keydown.enter="addAgentCapability"
              />
              <UiButton variant="outline" size="sm" :disabled="configuringIsReadOnly || !agentNewCapability.trim()" @click="addAgentCapability">
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
                :disabled="configuringIsReadOnly"
                :placeholder="t('settings.agentSystemPromptPlaceholder')"
              ></textarea>
            </div>
          </div>

          <!-- 版本历史 -->
          <div class="agent-config-section">
            <div class="agent-config-section-title">
              {{ t('settings.agentVersionHistory') }}
              <UiBadge variant="outline">{{ agentVersionHistory.length }}</UiBadge>
            </div>
            <p class="agent-config-hint">{{ t('settings.agentVersionHistoryHint') }}</p>
            <div v-if="agentVersionsLoading" class="center-loading-state" aria-live="polite">
              <UiSkeleton v-for="index in 2" :key="index" class="center-loading-line agent-version-loading-line" />
            </div>
            <div v-else-if="agentVersionHistoryError" class="center-message" role="alert" data-testid="agent-version-read-failure">
              <Info :size="16" aria-hidden="true" />
              <div class="center-message-content">
                <strong>{{ t('settings.centerLoadFailed') }}</strong>
                <span>{{ agentVersionHistoryError.message }}</span>
              </div>
              <UiButton variant="outline" size="sm" @click="loadAgentVersionHistory(configuringAgent.id)">{{ t('settings.retry') }}</UiButton>
            </div>
            <div v-else-if="agentVersionHistory.length > 0" class="agent-version-list">
              <div
                v-for="version in [...agentVersionHistory].reverse()"
                :key="version.id"
                class="center-list-row agent-version-row"
                :class="{ latest: version.version === configuringAgent.version }"
              >
                <strong>v{{ version.version }}</strong>
                <span class="agent-version-hash">{{ (version as AgentVersionDto & { content_hash?: string }).content_hash?.slice(0, 8) ?? '—' }}</span>
                <time>{{ new Date(version.created_at).toLocaleString(locale) }}</time>
                <UiBadge v-if="version.version === configuringAgent.version" variant="default">{{ t('settings.agentVersionCurrent') }}</UiBadge>
              </div>
            </div>
            <p v-else class="quiet">{{ t('settings.noVersionsPublished') }}</p>
          </div>

          <!-- 保存草稿 vs 保存并发布 / 克隆 -->
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

    <!-- 新建智能体 Sheet -->
    <UiSheet :open="showCreateAgent" side="right" @update:open="showCreateAgent = $event">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.newAgent') }}</h3>
        <div>
          <UiLabel>{{ t('settings.agentName') }}</UiLabel>
          <UiInput v-model="newAgentName" :placeholder="t('settings.agentName')" />
        </div>
        <div>
          <UiLabel>{{ t('settings.agentSlugLabel') }}</UiLabel>
          <UiInput v-model="newAgentSlug" :placeholder="t('settings.agentSlugOptionalPlaceholder')" />
        </div>
        <div>
          <UiLabel>{{ t('settings.agentLayer') }}</UiLabel>
          <UiSelectField
            v-model="newAgentLayer"
            :options="[
              { value: 'operation', label: t('settings.agentLayerPlanning') },
              { value: 'execution', label: t('settings.agentLayerExecution') },
            ]"
          />
        </div>
        <div>
          <UiLabel>{{ t('settings.agentRole') }}</UiLabel>
          <UiInput v-model="newAgentRole" :placeholder="newAgentLayer === 'operation' ? 'session_coordinator' : 'code_writer'" />
        </div>
        <div>
          <UiLabel>{{ t('settings.agentBasePipeline') }}</UiLabel>
          <UiSelectField v-model="newAgentPipelineId" :options="pipelineOptions" />
        </div>
        <div>
          <UiLabel>{{ t('settings.agentDescription') }}</UiLabel>
          <textarea
            v-model="newAgentDescription"
            class="settings-textarea"
            rows="2"
            :placeholder="t('settings.agentDescriptionPlaceholder')"
          ></textarea>
        </div>
        <div class="ac-sheet-actions">
          <UiButton variant="outline" @click="showCreateAgent = false">{{ t('common.cancel') }}</UiButton>
          <UiButton variant="outline" :disabled="newAgentBusy || !newAgentName.trim()" @click="createAgent(false)">
            {{ t('settings.saveAgentDraftOnly') }}
          </UiButton>
          <UiButton :disabled="newAgentBusy || !newAgentName.trim()" @click="createAgent(true)">
            {{ t('settings.createAndPublishAgent') }}
          </UiButton>
        </div>
      </div>
    </UiSheet>
  </div>
</template>

<style scoped>
.roster-stage-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 12px;
}

.roster-stage-head h3 {
  margin: 0;
  font-size: 14px;
}

.roster-stage-head p {
  margin: 2px 0 0;
  font-size: 12px;
}

.roster-layer-section {
  display: grid;
  gap: 8px;
  margin-bottom: 16px;
}

.roster-card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));
  gap: 10px;
}

.roster-card {
  position: relative;
  display: flex;
  flex-direction: column;
  padding: 0;
}

.roster-card-select {
  display: grid;
  gap: 8px;
  padding: 12px;
  text-align: left;
  width: 100%;
}

.roster-card-top {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}

.roster-card-identity {
  flex: 1;
  min-width: 0;
  display: grid;
  gap: 2px;
}

.roster-card-identity strong {
  font-size: 13px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.roster-card-identity span {
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.roster-card-desc {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  min-height: 32px;
}

.roster-card-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}

.roster-card-foot small {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-muted);
  font-size: 11px;
}

.roster-card-badges {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.roster-quick-clone {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 4px;
  margin: 0 12px 10px;
  padding: 4px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-default);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.roster-quick-clone:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.agent-card-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.agent-card-title-row strong {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-source-pill {
  flex-shrink: 0;
}

.agent-definition-fields {
  display: grid;
  gap: 10px;
  margin: 10px 0;
}

.agent-provenance-box {
  border-left: 3px solid var(--accent-primary);
  padding-left: 10px;
}
</style>
