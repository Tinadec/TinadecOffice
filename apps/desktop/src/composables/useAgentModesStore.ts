import { computed, ref } from 'vue'
import {
  api as baseApi,
  type AgentDefinitionDto,
  type AgentModeBindingDto,
  type AgentModeDraftUpdate,
  type AgentModeEdgeDto,
  type AgentModeNodeDto,
  type AgentModeRuntimePolicy,
  type AgentModeRuntimePolicyDefinition,
  type AgentModeTopologyDto,
  type AgentOrchestrationTier,
  type AgentViewDto,
  type ModeVersionDto,
  type ModelProviderInstanceDto,
  type ModelRouteDto,
  type PackI18n,
} from '@/api'
import i18n from '@/i18n'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'
import { sortModes } from '@/lib/modePresentation'
import { pickLocalized } from '@/lib/packI18n'
import { withoutRetiredModes } from '@/lib/retiredModes'
import { scopedApi } from '@/lib/storageScope'
import { dagreLayoutMode, snapPosition } from '@/components/modeEditor/modeDagreLayout'
import {
  buildTeamTopology,
  classifyTeamShape,
  cloneModeTopology,
  defaultMemberScope,
  MAX_TEAM_MEMBERS,
  naturalTierForStyle,
  type MemberScope,
  type TeamSelection,
  type TeamShape,
} from '@/settings/agentCenter/teamShape'

export interface ModeDraftState {
  displayName: string
  description: string
  instructions: string
  orchestrationTier: AgentOrchestrationTier | null
  runtimePolicyKind: 'none' | AgentModeRuntimePolicy['kind']
  runtimePolicyFeatures: string[]
  runtimePolicySettingsJson: string
  nodes: AgentModeNodeDto[]
  edges: AgentModeEdgeDto[]
  bindingsDraft: string
  canvasLayout: Record<string, unknown> | null
}

export interface TeamDraftState {
  displayName: string
  description: string
  instructions: string
  orchestrationTier: AgentOrchestrationTier | null
  selection: TeamSelection
  memberScopes: Record<string, MemberScope>
}

export interface CommitDraftOptions {
  record?: boolean
  coalesceKey?: string
}

const MAX_HISTORY = 50
const COALESCE_WINDOW_MS = 650

function createEmptyModeDraft(): ModeDraftState {
  return {
    displayName: '',
    description: '',
    instructions: '',
    orchestrationTier: null,
    runtimePolicyKind: 'none',
    runtimePolicyFeatures: [],
    runtimePolicySettingsJson: '',
    nodes: [],
    edges: [],
    bindingsDraft: '[]',
    canvasLayout: null,
  }
}

function createEmptyTeamDraft(): TeamDraftState {
  return {
    displayName: '',
    description: '',
    instructions: '',
    orchestrationTier: null,
    selection: { style: 'team', leaderId: '', memberIds: [] },
    memberScopes: {},
  }
}

function cloneModeDraft(state: ModeDraftState): ModeDraftState {
  return {
    displayName: state.displayName,
    description: state.description,
    instructions: state.instructions,
    orchestrationTier: state.orchestrationTier,
    runtimePolicyKind: state.runtimePolicyKind,
    runtimePolicyFeatures: [...state.runtimePolicyFeatures],
    runtimePolicySettingsJson: state.runtimePolicySettingsJson,
    nodes: state.nodes.map((node) => ({
      ...node,
      position: { ...node.position },
      config: node.config ? { ...node.config } : null,
      relationship: node.relationship ? JSON.parse(JSON.stringify(node.relationship)) as Record<string, unknown> : null,
      model_strategy_override: node.model_strategy_override ? { ...node.model_strategy_override } : null,
    })),
    edges: state.edges.map((edge) => ({
      ...edge,
      condition: edge.condition ? JSON.parse(JSON.stringify(edge.condition)) as Record<string, unknown> : null,
    })),
    bindingsDraft: state.bindingsDraft,
    canvasLayout: state.canvasLayout ? JSON.parse(JSON.stringify(state.canvasLayout)) as Record<string, unknown> : null,
  }
}

function serializeModeDraft(state: ModeDraftState): string {
  return JSON.stringify(state)
}

function serializeTeamDraft(state: TeamDraftState): string {
  return JSON.stringify(state)
}

function tr(key: string, params?: Record<string, unknown>, fallback?: string): string {
  const globalI18n = i18n.global as unknown as {
    t: (k: string, p?: Record<string, unknown>) => string
    te?: (k: string) => boolean
  }
  if (typeof globalI18n.te === 'function' && !globalI18n.te(key) && fallback) return fallback
  const translated = params ? globalI18n.t(key, params) : globalI18n.t(key)
  return translated === key && fallback ? fallback : translated
}

function activeLocale(): string {
  return i18n.global.locale.value
}

export function normalizeLayer(layer?: unknown): 'operation' | 'execution' {
  return String(layer ?? '').trim().toLowerCase() === 'execution' ? 'execution' : 'operation'
}

export function normalizeOrchestrationTier(value: unknown): AgentOrchestrationTier | null {
  const normalized = String(value ?? '').trim().toLowerCase()
  if (normalized === 'solo' || normalized === 'solo_dispatch') return 'solo_dispatch'
  if (normalized === 'free_form') return 'free_form'
  if (normalized === 'self_dispatch') return 'self_dispatch'
  if (normalized === 'deterministic') return 'deterministic'
  return null
}

export function normalizeAgentDefinition(agent: AgentDefinitionDto): AgentDefinitionDto {
  const scopeTools = agent.tool_scope === '*'
    ? ['*']
    : Array.isArray(agent.tool_scope)
      ? agent.tool_scope
      : []
  const allowedTools = Array.isArray(agent.allowed_tools) && agent.allowed_tools.length > 0
    ? agent.allowed_tools
    : scopeTools
  return {
    ...agent,
    name: agent.name ?? agent.display_name ?? agent.slug ?? agent.id,
    display_name: agent.display_name ?? agent.name ?? agent.slug ?? agent.id,
    description: agent.description ?? '',
    allowed_tools: allowedTools,
    capabilities: Array.isArray(agent.capabilities) ? agent.capabilities : [],
  }
}

export function modeLocalizedName(mode: Pick<AgentModeTopologyDto, 'display_name' | 'i18n'>, locale = activeLocale()): string {
  return pickLocalized(mode.i18n, locale, 'display_name', mode.display_name) ?? mode.display_name
}

export function modeLocalizedDescription(mode: Pick<AgentModeTopologyDto, 'description' | 'i18n'>, locale = activeLocale()): string {
  return pickLocalized(mode.i18n, locale, 'description', mode.description) ?? ''
}

export function agentLocalizedName(
  agent: Pick<AgentDefinitionDto, 'id' | 'name' | 'display_name' | 'i18n'>,
  locale = activeLocale(),
): string {
  return pickLocalized(agent.i18n, locale, 'display_name', agent.display_name) ?? agent.display_name ?? agent.name ?? agent.id
}

export function agentLocalizedDescription(
  agent: Pick<AgentDefinitionDto, 'description' | 'i18n'>,
  locale = activeLocale(),
): string {
  return pickLocalized(agent.i18n, locale, 'description', agent.description) ?? agent.description ?? ''
}

export function isModePackManaged(mode: Pick<AgentModeTopologyDto, 'managed' | 'pack_managed'> | null | undefined): boolean {
  return Boolean(mode?.managed || mode?.pack_managed)
}

export function isModeReadOnly(mode: Pick<AgentModeTopologyDto, 'managed' | 'pack_managed' | 'status'> | null | undefined): boolean {
  if (!mode) return false
  return isModePackManaged(mode) || mode.status === 'archived'
}

export function agentUsableForTeam(agent: Pick<AgentDefinitionDto, 'enabled' | 'status'>): boolean {
  return agent.enabled !== false && (!agent.status || agent.status === 'published')
}

// ── Shared module-level reactive state ──────────────────────────────────────

const api = scopedApi(baseApi, () => 'user')
const { notify, confirm } = useNotifications()
const { canAccessBackend } = useHostAccess()

const modes = ref<AgentModeTopologyDto[]>([])
const agents = ref<AgentDefinitionDto[]>([])
const providers = ref<ModelProviderInstanceDto[]>([])
const routes = ref<ModelRouteDto[]>([])
const runtimePolicyDefinitions = ref<AgentModeRuntimePolicyDefinition[]>([])
const manifestToolIds = ref<string[]>([])
const defaultModeId = ref<string | null>(null)

const selectedModeId = ref('')
const selectedModeDetail = ref<AgentModeTopologyDto | null>(null)
const modeEtag = ref<string | null>(null)

const modesLoading = ref(false)
const agentsLoading = ref(false)
const detailLoading = ref(false)
const detailFailure = ref<string | null>(null)
const modeBusy = ref(false)
const settingDefault = ref(false)

const draft = ref<ModeDraftState>(createEmptyModeDraft())
const modeBaselineSignal = ref('')
const historyPast = ref<ModeDraftState[]>([])
const historyFuture = ref<ModeDraftState[]>([])
let lastCoalesceKey: string | null = null
let lastCoalesceAt = 0

const teamDraft = ref<TeamDraftState>(createEmptyTeamDraft())
const teamBaselineSignal = ref('')

const modeVersions = ref<ModeVersionDto[]>([])
const modeVersionsLoading = ref(false)

let modesReadPromise: Promise<void> | null = null
let agentsReadPromise: Promise<void> | null = null
let detailGeneration = 0

const sortedModes = computed(() => sortModes(modes.value))

const selectedMode = computed<AgentModeTopologyDto | null>(() => {
  const row = modes.value.find((m) => m.id === selectedModeId.value) ?? null
  if (!row && selectedModeDetail.value && selectedModeDetail.value.id === selectedModeId.value) {
    return selectedModeDetail.value
  }
  if (row && selectedModeDetail.value && selectedModeDetail.value.id === row.id) {
    return {
      ...row,
      ...selectedModeDetail.value,
      latest_published_mode_version_id: selectedModeDetail.value.latest_published_mode_version_id ?? row.latest_published_mode_version_id,
      managed: row.managed ?? selectedModeDetail.value.managed,
      pack_managed: row.pack_managed ?? selectedModeDetail.value.pack_managed,
    }
  }
  return row
})

const selectedModeReadOnly = computed(() => {
  if (!selectedModeId.value) return false
  return isModeReadOnly(selectedMode.value)
})

const parsedPolicySettings = computed<{ value: Record<string, unknown> | null; error: string }>(() => {
  if (draft.value.runtimePolicyKind !== 'autonomous_exploration') return { value: null, error: '' }
  const raw = draft.value.runtimePolicySettingsJson.trim()
  if (!raw) return { value: {}, error: '' }
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return { value: null, error: tr('settings.runtimePolicySettingsInvalid', undefined, 'Invalid settings JSON') }
    }
    return { value: parsed as Record<string, unknown>, error: '' }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { value: null, error: `${tr('settings.runtimePolicySettingsInvalid', undefined, 'Invalid settings JSON')} ${message}` }
  }
})

const runtimePolicy = computed<AgentModeRuntimePolicy | null>(() => {
  if (draft.value.runtimePolicyKind === 'none') return null
  if (draft.value.runtimePolicyKind === 'structured_investigation') {
    return { kind: 'structured_investigation', version: 1, features: [...draft.value.runtimePolicyFeatures] }
  }
  const settings = parsedPolicySettings.value.value
  if (!settings) return null
  return { kind: 'autonomous_exploration', version: 1, features: [...draft.value.runtimePolicyFeatures], settings }
})

const parsedBindings = computed<{ value: AgentModeBindingDto[] | null; error: string | null }>(() => {
  const raw = draft.value.bindingsDraft.trim()
  if (!raw) return { value: [], error: null }
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return { value: null, error: tr('settings.bindingsMustBeArray', undefined, 'Bindings must be an array') }
    for (const binding of parsed) {
      if (!binding || typeof binding !== 'object' || Array.isArray(binding)) {
        return { value: null, error: tr('settings.bindingsMustBeObjects', undefined, 'Each binding must be an object') }
      }
      const record = binding as Record<string, unknown>
      if (!record.node_key && !record.agent_ref) {
        return { value: null, error: tr('settings.bindingNeedsKey', undefined, 'Each binding needs node_key or agent_ref') }
      }
    }
    return { value: parsed as AgentModeBindingDto[], error: null }
  } catch (error) {
    return { value: null, error: error instanceof Error ? error.message : String(error) }
  }
})

const modeDirty = computed(() =>
  Boolean(selectedModeId.value)
  && !selectedModeReadOnly.value
  && modeBaselineSignal.value !== ''
  && serializeModeDraft(draft.value) !== modeBaselineSignal.value,
)

const teamDirty = computed(() =>
  Boolean(selectedModeId.value)
  && !selectedModeReadOnly.value
  && teamBaselineSignal.value !== ''
  && serializeTeamDraft(teamDraft.value) !== teamBaselineSignal.value,
)

const isDirty = computed(() => modeDirty.value || teamDirty.value)
const canUndo = computed(() => !selectedModeReadOnly.value && historyPast.value.length > 0)
const canRedo = computed(() => !selectedModeReadOnly.value && historyFuture.value.length > 0)

function toAgentViews(list: AgentDefinitionDto[]): AgentViewDto[] {
  return list.map((a) => ({
    id: a.id,
    slug: a.slug,
    name: a.name ?? a.display_name ?? a.slug ?? a.id,
    display_name: a.display_name ?? a.name ?? a.slug ?? a.id,
    i18n: a.i18n ?? null,
    layer: a.layer,
    agent_type: a.agent_type ?? a.role ?? '',
    role: a.role ?? a.agent_type ?? '',
    mode: '',
    description: a.description ?? '',
    model_route_purpose: a.model_route_purpose ?? '',
    allowed_tools: a.allowed_tools ?? [],
    tool_scope: a.tool_scope ?? null,
    capabilities: a.capabilities ?? [],
    system_prompt: a.system_prompt ?? null,
    enabled: a.enabled ?? true,
    is_built_in: Boolean(a.is_built_in || a.managed || a.pack_managed),
    status: a.status,
    revision: a.revision ?? null,
    version: a.version ?? null,
    updated_at: a.updated_at ?? null,
  }))
}

function mapTopologyToModeDraft(m: AgentModeTopologyDto): ModeDraftState {
  const rawNodes = (m.nodes ?? []) as unknown as Array<Record<string, unknown>>
  const missingPosition = new Set<string>()
  const mappedNodes = rawNodes.map((n) => {
    const nodeId = String(n.node_key ?? n.id)
    const lane = normalizeLayer(n.layer ?? n.lane)
    const position = n.position as { x: number; y: number } | null
    if (!position || typeof position.x !== 'number' || typeof position.y !== 'number') {
      missingPosition.add(nodeId)
    }
    return {
      id: nodeId,
      node_key: n.node_key != null ? String(n.node_key) : undefined,
      agent_id: n.agent_id != null ? String(n.agent_id) : n.agent_definition_id != null ? String(n.agent_definition_id) : '',
      agent_definition_id: n.agent_definition_id != null ? String(n.agent_definition_id) : n.agent_id != null ? String(n.agent_id) : undefined,
      lane,
      layer: lane,
      position: position && typeof position.x === 'number' && typeof position.y === 'number'
        ? snapPosition(position)
        : { x: 60, y: 60 },
      label: (n.label as string) ?? '',
      i18n: (n.i18n as PackI18n | null | undefined) ?? null,
      config: (n.config as Record<string, unknown> | null | undefined) ?? null,
      relationship: (n.relationship as Record<string, unknown> | null | undefined) ?? null,
      model_strategy_override: (n.model_strategy_override as AgentModeNodeDto['model_strategy_override']) ?? null,
    } as AgentModeNodeDto
  })

  const mappedEdges = ((m.edges ?? []) as unknown as Array<Record<string, unknown>>).map((e) => {
    const condition = (e.condition as Record<string, unknown> | null | undefined) ?? null
    const conditionLabel = condition?.label
    return {
      id: String(e.edge_key ?? e.id),
      edge_key: e.edge_key != null ? String(e.edge_key) : undefined,
      source: e.source != null ? String(e.source) : String(e.source_node_key ?? ''),
      target: e.target != null ? String(e.target) : String(e.target_node_key ?? ''),
      source_node_key: e.source_node_key != null ? String(e.source_node_key) : undefined,
      target_node_key: e.target_node_key != null ? String(e.target_node_key) : undefined,
      label: (e.label as string | undefined) ?? (typeof conditionLabel === 'string' ? conditionLabel : undefined),
      condition,
    } as AgentModeEdgeDto
  })

  const suggested = missingPosition.size > 0
    ? dagreLayoutMode(
        mappedNodes.map((node) => ({ id: node.id, lane: normalizeLayer(node.lane) })),
        mappedEdges.map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
      ).positions
    : {}

  const nodesWithPositions = mappedNodes.map((node) =>
    missingPosition.has(node.id)
      ? { ...node, position: suggested[node.id] ?? { x: 60, y: 60 } }
      : node,
  )

  const bindings = ((m.bindings ?? []) as AgentModeBindingDto[]).map((b) => ({ ...b }))
  let policyKind: ModeDraftState['runtimePolicyKind'] = 'none'
  let policyFeatures: string[] = []
  let policySettingsJson = ''
  if (m.runtime_policy?.kind === 'structured_investigation') {
    policyKind = 'structured_investigation'
    policyFeatures = [...m.runtime_policy.features]
  } else if (m.runtime_policy?.kind === 'autonomous_exploration') {
    policyKind = 'autonomous_exploration'
    policyFeatures = [...m.runtime_policy.features]
    policySettingsJson = m.runtime_policy.settings ? JSON.stringify(m.runtime_policy.settings, null, 2) : ''
  }

  return {
    displayName: m.display_name ?? '',
    description: m.description ?? '',
    instructions: m.instructions ?? '',
    orchestrationTier: normalizeOrchestrationTier(m.orchestration_tier),
    runtimePolicyKind: policyKind,
    runtimePolicyFeatures: policyFeatures,
    runtimePolicySettingsJson: policySettingsJson,
    nodes: nodesWithPositions,
    edges: mappedEdges,
    bindingsDraft: JSON.stringify(bindings, null, 2),
    canvasLayout: (m.canvas_layout as Record<string, unknown> | null | undefined) ?? null,
  }
}

function mapTopologyToTeamDraft(m: AgentModeTopologyDto, agentList: AgentDefinitionDto[]): TeamDraftState {
  const views = toAgentViews(agentList)
  const shape: TeamShape = classifyTeamShape(m, views)
  const usableOp = views.filter((a) => normalizeLayer(a.layer) === 'operation' && agentUsableForTeam(a))
  const fallbackLeader = usableOp.find((a) => a.slug === 'meeting')?.id ?? usableOp[0]?.id ?? ''
  const leaderId = shape.leaderId || fallbackLeader

  const eligible = shape.style === 'solo'
    ? []
    : views.filter((a) => a.id !== leaderId && agentUsableForTeam(a) && (shape.style !== 'pipeline' || normalizeLayer(a.layer) === 'execution'))

  const memberIds = shape.memberIds
    .filter((id) => eligible.some((a) => a.id === id))
    .slice(0, MAX_TEAM_MEMBERS)

  const scopes: Record<string, MemberScope> = {}
  for (const id of memberIds) {
    const ag = views.find((a) => a.id === id)
    scopes[id] = shape.memberScopes?.[id] ?? (ag ? defaultMemberScope(ag) : { read: true, write: false })
  }

  return {
    displayName: m.display_name ?? '',
    description: m.description ?? '',
    instructions: m.instructions ?? '',
    orchestrationTier: normalizeOrchestrationTier(m.orchestration_tier),
    selection: {
      style: shape.style,
      leaderId,
      memberIds,
    },
    memberScopes: scopes,
  }
}

function applyDetailToDrafts(detail: AgentModeTopologyDto) {
  selectedModeDetail.value = detail
  modeEtag.value = detail.etag ?? (detail.revision != null ? String(detail.revision) : null)

  const nextModeDraft = mapTopologyToModeDraft(detail)
  draft.value = nextModeDraft
  modeBaselineSignal.value = serializeModeDraft(nextModeDraft)
  historyPast.value = []
  historyFuture.value = []
  lastCoalesceKey = null
  lastCoalesceAt = 0

  const nextTeamDraft = mapTopologyToTeamDraft(detail, agents.value)
  teamDraft.value = nextTeamDraft
  teamBaselineSignal.value = serializeTeamDraft(nextTeamDraft)
}

function reseedTeamBaselineKeepingChangesIfClean() {
  if (!selectedModeDetail.value) return
  if (!teamDirty.value) {
    const next = mapTopologyToTeamDraft(selectedModeDetail.value, agents.value)
    teamDraft.value = next
    teamBaselineSignal.value = serializeTeamDraft(next)
  }
}

function resetDraftsToBaseline() {
  if (selectedModeDetail.value) {
    applyDetailToDrafts(selectedModeDetail.value)
  } else {
    draft.value = createEmptyModeDraft()
    modeBaselineSignal.value = ''
    historyPast.value = []
    historyFuture.value = []
    teamDraft.value = createEmptyTeamDraft()
    teamBaselineSignal.value = ''
  }
}

function commitDraft(updater: (current: ModeDraftState) => ModeDraftState, options: CommitDraftOptions = {}): void {
  if (selectedModeReadOnly.value) return
  const record = options.record !== false
  const now = Date.now()
  const key = options.coalesceKey ?? null
  const shouldSkipHistory = Boolean(
    key
    && key === lastCoalesceKey
    && (now - lastCoalesceAt) < COALESCE_WINDOW_MS
    && historyPast.value.length > 0,
  )

  if (record && !shouldSkipHistory) {
    const snap = cloneModeDraft(draft.value)
    const last = historyPast.value[historyPast.value.length - 1]
    if (!last || serializeModeDraft(last) !== serializeModeDraft(snap)) {
      historyPast.value = [...historyPast.value.slice(-(MAX_HISTORY - 1)), snap]
    }
    historyFuture.value = []
  } else if (record) {
    historyFuture.value = []
  }

  lastCoalesceKey = key
  lastCoalesceAt = now
  draft.value = updater(draft.value)
}

function undo(): void {
  if (!canUndo.value) return
  const previous = historyPast.value[historyPast.value.length - 1]!
  historyFuture.value = [cloneModeDraft(draft.value), ...historyFuture.value.slice(0, MAX_HISTORY - 1)]
  historyPast.value = historyPast.value.slice(0, -1)
  lastCoalesceKey = null
  draft.value = cloneModeDraft(previous)
}

function redo(): void {
  if (!canRedo.value) return
  const next = historyFuture.value[0]!
  historyPast.value = [...historyPast.value.slice(-(MAX_HISTORY - 1)), cloneModeDraft(draft.value)]
  historyFuture.value = historyFuture.value.slice(1)
  lastCoalesceKey = null
  draft.value = cloneModeDraft(next)
}

function setRuntimePolicyKind(value: string): void {
  if (selectedModeReadOnly.value) return
  const kind: ModeDraftState['runtimePolicyKind'] =
    value === 'structured_investigation' || value === 'autonomous_exploration' ? value : 'none'
  if (kind === draft.value.runtimePolicyKind) return
  const definition = runtimePolicyDefinitions.value.find((item) => item.kind === kind) ?? null
  commitDraft((current) => ({
    ...current,
    runtimePolicyKind: kind,
    runtimePolicyFeatures: definition ? [...definition.features] : [],
    runtimePolicySettingsJson: kind === 'autonomous_exploration' && definition?.default_settings
      ? JSON.stringify(definition.default_settings, null, 2)
      : '',
  }))
}

function togglePolicyFeature(feature: string, checked: boolean): void {
  if (selectedModeReadOnly.value) return
  const definition = runtimePolicyDefinitions.value.find((item) => item.kind === draft.value.runtimePolicyKind) ?? null
  if (!checked && definition?.required_features.includes(feature)) return
  commitDraft((current) => {
    const nextFeatures = checked
      ? (current.runtimePolicyFeatures.includes(feature) ? current.runtimePolicyFeatures : [...current.runtimePolicyFeatures, feature])
      : current.runtimePolicyFeatures.filter((item) => item !== feature)
    return { ...current, runtimePolicyFeatures: nextFeatures }
  })
}

async function discardOrConfirm(): Promise<boolean> {
  if (!isDirty.value) return true
  const confirmed = await confirm({
    title: tr('settings.modeDiscardChanges', undefined, 'Discard unsaved changes?'),
    message: tr('settings.modeDiscardNavMessage', undefined, 'You have unsaved mode or team edits. Discard them and leave?'),
    confirmLabel: tr('settings.discardChanges', undefined, 'Discard changes'),
    cancelLabel: tr('settings.keepEditing', undefined, 'Keep editing'),
    destructive: true,
  })
  if (confirmed) resetDraftsToBaseline()
  return confirmed
}

function mergeExternalModes(incoming: AgentModeTopologyDto[] | Array<{ id: string; display_name: string } & Record<string, unknown>>): void {
  const existingById = new Map(modes.value.map((m) => [m.id, m]))
  const list = withoutRetiredModes(incoming as AgentModeTopologyDto[]).map((m) => {
    const prev = existingById.get(m.id)
    return {
      ...prev,
      ...m,
      nodes: m.nodes ?? prev?.nodes ?? [],
      edges: m.edges ?? prev?.edges ?? [],
    } as AgentModeTopologyDto
  })
  modes.value = list
}

function mergeExternalAgents(incoming: AgentViewDto[]): void {
  const existingById = new Map(agents.value.map((a) => [a.id, a]))
  agents.value = incoming.map((item) => {
    const prev = existingById.get(item.id)
    const normalized = normalizeAgentDefinition({
      id: item.id,
      slug: item.slug,
      name: item.name,
      display_name: item.display_name ?? item.name,
      i18n: item.i18n ?? prev?.i18n ?? null,
      layer: item.layer,
      agent_type: item.agent_type,
      role: item.role ?? item.agent_type,
      description: item.description || prev?.description || '',
      model_route_purpose: item.model_route_purpose || prev?.model_route_purpose || '',
      model_strategy: (item.model_strategy as AgentDefinitionDto['model_strategy']) ?? prev?.model_strategy ?? null,
      allowed_tools: (item.allowed_tools && item.allowed_tools.length > 0) ? item.allowed_tools : (prev?.allowed_tools ?? []),
      tool_scope: item.tool_scope ?? prev?.tool_scope ?? null,
      capabilities: (item.capabilities && item.capabilities.length > 0) ? item.capabilities : (prev?.capabilities ?? []),
      system_prompt: item.system_prompt ?? prev?.system_prompt ?? null,
      enabled: item.enabled,
      is_built_in: item.is_built_in,
      status: item.status,
      revision: item.revision ?? prev?.revision ?? null,
      version: item.version ?? prev?.version ?? null,
      updated_at: item.updated_at ?? prev?.updated_at ?? null,
    })
    return normalized
  })
  reseedTeamBaselineKeepingChangesIfClean()
}

async function loadModes(options: { force?: boolean } = {}): Promise<void> {
  if (!canAccessBackend.value) return
  if (modesReadPromise && !options.force) return modesReadPromise
  modesLoading.value = true
  const req = api.listAgentModeTopologies()
    .then((list) => {
      const cleaned = withoutRetiredModes(Array.isArray(list) ? list : [])
      mergeExternalModes(cleaned)
      if (!selectedModeId.value && modes.value.length > 0) {
        const first = sortModes(modes.value)[0]
        if (first) void selectMode(first.id, { force: true })
      }
    })
    .catch((e) => {
      notify.error(e, { title: tr('agentCenter.loadModesFailed', undefined, 'Failed to load modes') })
    })
    .finally(() => {
      modesLoading.value = false
      modesReadPromise = null
    })
  modesReadPromise = req
  return req
}

async function loadAgents(options: { force?: boolean } = {}): Promise<void> {
  if (!canAccessBackend.value) return
  if (agentsReadPromise && !options.force) return agentsReadPromise
  agentsLoading.value = true
  const req = api.listAgentDefinitions()
    .then((definitions) => {
      const list = Array.isArray(definitions) ? definitions.map(normalizeAgentDefinition) : []
      agents.value = list
      reseedTeamBaselineKeepingChangesIfClean()
    })
    .catch(() => {
      // Keep any existing prop-seeded agents if definition fetch fails
    })
    .finally(() => {
      agentsLoading.value = false
      agentsReadPromise = null
    })
  agentsReadPromise = req
  return req
}

function loadPoliciesAndRoutes(): void {
  if (!canAccessBackend.value) return
  void api.listAgentModeRuntimePolicies()
    .then((rows) => { runtimePolicyDefinitions.value = Array.isArray(rows) ? rows : [] })
    .catch(() => { runtimePolicyDefinitions.value = [] })
  void api.listModelProviders()
    .then((rows) => { providers.value = Array.isArray(rows) ? rows : [] })
    .catch(() => { providers.value = [] })
  void api.listModelRoutes()
    .then((rows) => { routes.value = Array.isArray(rows) ? rows : [] })
    .catch(() => { routes.value = [] })
  void api.getHarnessManifest()
    .then((m) => { manifestToolIds.value = (m?.tools ?? []).map((item: { id: string }) => item.id) })
    .catch(() => { manifestToolIds.value = [] })
}

async function loadWorkspaceDefaults(): Promise<void> {
  if (!canAccessBackend.value) return
  try {
    const defaults = await api.getWorkspaceDefaults()
    defaultModeId.value = defaults.default_agent_mode_id ?? null
  } catch {
    defaultModeId.value = null
  }
}

async function loadAll(): Promise<void> {
  if (!canAccessBackend.value) return
  loadPoliciesAndRoutes()
  await Promise.all([loadAgents(), loadModes(), loadWorkspaceDefaults()])
}

async function selectMode(id: string, options: { force?: boolean } = {}): Promise<boolean> {
  if (!id) return false
  if (!options.force && id !== selectedModeId.value && !(await discardOrConfirm())) {
    return false
  }
  const generation = ++detailGeneration
  selectedModeId.value = id
  detailLoading.value = true
  detailFailure.value = null
  try {
    const loaded = await api.getAgentModeTopology(id)
    if (generation !== detailGeneration || selectedModeId.value !== id) return false
    applyDetailToDrafts(loaded)
    modes.value = modes.value.map((row) =>
      row.id === id
        ? {
            ...row,
            display_name: loaded.display_name ?? row.display_name,
            description: loaded.description ?? row.description,
            orchestration_tier: loaded.orchestration_tier ?? row.orchestration_tier,
            managed: loaded.managed ?? row.managed,
            pack_managed: loaded.pack_managed ?? row.pack_managed,
            status: loaded.status ?? row.status,
            revision: loaded.revision ?? row.revision,
            latest_published_mode_version_id: loaded.latest_published_mode_version_id ?? row.latest_published_mode_version_id,
          }
        : row,
    )
    return true
  } catch (error) {
    if (generation !== detailGeneration || selectedModeId.value !== id) return false
    const msg = error instanceof Error ? error.message : String(error)
    detailFailure.value = msg
    selectedModeDetail.value = null
    modeEtag.value = null
    resetDraftsToBaseline()
    notify.error(error, { title: tr('agentCenter.loadModesFailed', undefined, 'Failed to load mode') })
    return false
  } finally {
    if (generation === detailGeneration) detailLoading.value = false
  }
}

function toCoreTopologyWrite(state: ModeDraftState, bindings: AgentModeBindingDto[]): AgentModeDraftUpdate {
  return {
    display_name: state.displayName.trim(),
    description: state.description.trim() || null,
    instructions: state.instructions.trim() || null,
    orchestration_tier: state.orchestrationTier ?? null,
    nodes: state.nodes.map((n) => ({
      node_key: n.node_key ?? n.id,
      agent_definition_id: n.agent_id || n.agent_definition_id || '',
      layer: normalizeLayer(n.lane ?? n.layer),
      label: n.label ?? null,
      position: n.position ?? null,
      config: n.config ?? null,
      relationship: n.relationship ?? null,
      model_strategy_override: n.model_strategy_override ?? null,
    })),
    edges: state.edges.map((e) => ({
      edge_key: e.edge_key ?? e.id,
      source_node_key: e.source,
      target_node_key: e.target,
      ...(e.condition || e.label ? { condition: e.condition ?? { label: e.label } } : {}),
    })),
    bindings,
    runtime_policy: runtimePolicy.value,
    canvas_layout: state.canvasLayout,
  }
}

function syncSavedTopologyResponse(saved: AgentModeTopologyDto): void {
  const merged: AgentModeTopologyDto = {
    ...(selectedModeDetail.value ?? {}),
    ...saved,
    latest_published_mode_version_id: saved.latest_published_mode_version_id ?? selectedModeDetail.value?.latest_published_mode_version_id,
  }
  applyDetailToDrafts(merged)
  const exists = modes.value.some((row) => row.id === saved.id)
  if (exists) {
    modes.value = modes.value.map((row) =>
      row.id === saved.id
        ? {
            ...row,
            display_name: saved.display_name ?? row.display_name,
            description: saved.description ?? row.description,
            orchestration_tier: saved.orchestration_tier ?? row.orchestration_tier,
            revision: saved.revision ?? row.revision,
            status: saved.status ?? row.status,
            latest_published_mode_version_id: saved.latest_published_mode_version_id ?? row.latest_published_mode_version_id,
          }
        : row,
    )
  } else {
    modes.value = [merged, ...modes.value]
  }
}

async function saveModeDraft(): Promise<boolean> {
  if (!selectedModeId.value || selectedModeReadOnly.value) return false
  if (!draft.value.displayName.trim()) {
    notify.error(new Error(tr('settings.modeNameRequired', undefined, 'Mode name is required')))
    return false
  }
  if (parsedPolicySettings.value.error) {
    notify.error(parsedPolicySettings.value.error, {
      title: tr('settings.runtimePolicySettingsInvalid', undefined, 'Invalid runtime policy settings'),
    })
    return false
  }
  if (!parsedBindings.value.value) {
    notify.error(new Error(tr('settings.bindingsInvalid', undefined, 'Invalid bindings JSON')))
    return false
  }
  modeBusy.value = true
  try {
    const saved = await api.updateAgentModeDraft(
      selectedModeId.value,
      toCoreTopologyWrite(draft.value, parsedBindings.value.value),
      modeEtag.value,
    )
    if (saved) syncSavedTopologyResponse(saved)
    else modeBaselineSignal.value = serializeModeDraft(draft.value)
    notify.success({ message: tr('agentCenter.modeDraftSaved', undefined, 'Mode draft saved') })
    return true
  } catch (e) {
    notify.error(e, { title: tr('agentCenter.modeSaveFailed', undefined, 'Failed to save mode') })
    return false
  } finally {
    modeBusy.value = false
  }
}

async function publishMode(): Promise<boolean> {
  if (!selectedModeId.value || selectedModeReadOnly.value) return false
  if (modeDirty.value && !(await saveModeDraft())) return false
  modeBusy.value = true
  try {
    const published = await api.publishAgentMode(selectedModeId.value, modeEtag.value)
    if (published?.revision != null) modeEtag.value = String(published.revision)
    notify.success({ message: tr('agentCenter.modePublished', undefined, 'Mode published') })
    await loadModes({ force: true })
    await selectMode(selectedModeId.value, { force: true })
    return true
  } catch (e) {
    notify.error(e)
    return false
  } finally {
    modeBusy.value = false
  }
}

async function createMode(input?: { displayName?: string; slug?: string; topology?: AgentModeDraftUpdate }): Promise<AgentModeTopologyDto | null> {
  if (!canAccessBackend.value) return null
  if (!(await discardOrConfirm())) return null
  modeBusy.value = true
  try {
    const topo = input?.topology
    const displayName = (topo?.display_name ?? input?.displayName ?? `mode-${Date.now().toString(36).slice(0, 6)}`).trim()
    const payload = topo
      ? {
          ...topo,
          display_name: displayName,
          ...(input?.slug ? { slug: input.slug } : {}),
        }
      : {
          display_name: displayName,
          ...(input?.slug ? { slug: input.slug } : {}),
          nodes: [],
          edges: [],
          runtime_policy: null,
          canvas_layout: null,
        }
    const created = await api.createAgentModeDraft(payload)
    await loadModes({ force: true })
    await selectMode(created.id, { force: true })
    notify.success({ message: tr('agentCenter.modeCreated', { name: created.display_name }, `Created ${created.display_name}`) })
    return created
  } catch (e) {
    notify.error(e)
    return null
  } finally {
    modeBusy.value = false
  }
}

async function cloneMode(id = selectedModeId.value, customName?: string): Promise<AgentModeTopologyDto | null> {
  if (!canAccessBackend.value || !id) return null
  const source = modes.value.find((m) => m.id === id) ?? selectedModeDetail.value
  const fallbackName = source ? modeLocalizedName(source) : 'Mode'
  const displayName = (customName ?? tr('common.copyName', { name: fallbackName }, `${fallbackName} copy`)).trim()
  modeBusy.value = true
  try {
    let created: AgentModeTopologyDto
    if (typeof api.cloneAgentMode === 'function') {
      created = await api.cloneAgentMode(id, { display_name: displayName })
    } else {
      const detail = selectedModeDetail.value?.id === id ? selectedModeDetail.value : await api.getAgentModeTopology(id)
      const cloned = cloneModeTopology(detail)
      created = await api.createAgentModeDraft({
        ...cloned,
        display_name: displayName,
        slug: `mode-${Date.now().toString(36)}`,
      })
    }
    await loadModes({ force: true })
    await selectMode(created.id, { force: true })
    notify.success({ message: tr('agentCenter.modeCreated', { name: created.display_name }, `Created ${created.display_name}`) })
    return created
  } catch (e) {
    notify.error(e)
    return null
  } finally {
    modeBusy.value = false
  }
}

async function deleteOrArchiveMode(id = selectedModeId.value): Promise<boolean> {
  if (!canAccessBackend.value || !id) return false
  const target = modes.value.find((m) => m.id === id) ?? selectedModeDetail.value
  if (!target || isModePackManaged(target)) return false
  const targetName = modeLocalizedName(target)
  const confirmed = await confirm({
    title: tr('settings.modeDelete', undefined, 'Delete mode'),
    message: tr('settings.modeDeleteConfirm', { name: targetName }, `Delete "${targetName}"?`),
    confirmLabel: tr('settings.deleteAction', undefined, 'Delete'),
    cancelLabel: tr('common.cancel', undefined, 'Cancel'),
    destructive: true,
  })
  if (!confirmed) return false
  modeBusy.value = true
  try {
    if (typeof api.deleteAgentMode === 'function') {
      try {
        await api.deleteAgentMode(id, modeEtag.value)
      } catch (err) {
        // Fallback to archiving if the backend preserves versioned history via 409
        const msg = err instanceof Error ? err.message : String(err)
        if (/version|published|archive|409/i.test(msg)) {
          await api.archiveAgentMode(id, modeEtag.value)
        } else {
          throw err
        }
      }
    } else {
      await api.archiveAgentMode(id, modeEtag.value)
    }

    modes.value = modes.value.filter((m) => m.id !== id)
    if (selectedModeId.value === id) {
      selectedModeId.value = ''
      selectedModeDetail.value = null
      resetDraftsToBaseline()
      const next = sortModes(modes.value)[0]
      if (next) await selectMode(next.id, { force: true })
    }
    notify.success({ message: tr('settings.modeDeleted', { name: targetName }, `Deleted ${targetName}`) })
    return true
  } catch (e) {
    notify.error(e)
    return false
  } finally {
    modeBusy.value = false
  }
}

async function saveTeamDraft(): Promise<string | null> {
  if (!canAccessBackend.value) return null
  const name = teamDraft.value.displayName.trim()
  if (!name) {
    notify.error(new Error(tr('settings.teamNameRequired', undefined, 'Team name required')))
    return null
  }
  if (modes.value.some((m) => m.id !== selectedModeId.value && m.display_name.trim().toLowerCase() === name.toLowerCase())) {
    notify.error(new Error(tr('settings.teamNameTaken', undefined, 'Team name already exists')))
    return null
  }
  if (!teamDraft.value.selection.leaderId) {
    notify.error(new Error(tr('settings.teamLeaderRequired', undefined, 'Leader required')))
    return null
  }

  modeBusy.value = true
  try {
    const views = toAgentViews(agents.value)
    const topology = buildTeamTopology({
      style: teamDraft.value.selection.style,
      leaderId: teamDraft.value.selection.leaderId,
      memberIds: teamDraft.value.selection.memberIds,
      memberScopes: teamDraft.value.memberScopes,
      instructions: teamDraft.value.instructions,
      orchestrationTier: teamDraft.value.orchestrationTier ?? naturalTierForStyle(teamDraft.value.selection.style),
      displayName: name,
      description: teamDraft.value.description,
      existingDetail: selectedModeDetail.value,
    }, views)

    if (!selectedModeId.value || selectedModeReadOnly.value) {
      const created = await api.createAgentModeDraft({
        ...topology,
        slug: `team-${Date.now().toString(36)}`,
        display_name: topology.display_name ?? name,
      })
      await loadModes({ force: true })
      await selectMode(created.id, { force: true })
      notify.success(tr('settings.teamCreated', { name: created.display_name }, `Created team ${created.display_name}`))
      return created.id
    }

    const saved = await api.updateAgentModeDraft(selectedModeId.value, topology, modeEtag.value)
    if (saved) syncSavedTopologyResponse(saved)
    await loadModes({ force: true })
    notify.success(tr('settings.teamDraftSaved', undefined, 'Team draft saved'))
    return selectedModeId.value
  } catch (error) {
    notify.error(error)
    return null
  } finally {
    modeBusy.value = false
  }
}

async function publishTeam(): Promise<boolean> {
  const id = await saveTeamDraft()
  if (!id) return false
  modeBusy.value = true
  try {
    await api.publishAgentMode(id, modeEtag.value)
    await loadModes({ force: true })
    await selectMode(id, { force: true })
    notify.success(tr('settings.teamPublished', undefined, 'Team published'))
    return true
  } catch (error) {
    notify.error(error)
    return false
  } finally {
    modeBusy.value = false
  }
}

/**
 * Sets a published mode as workspace default while preserving ALL other
 * WorkspaceDefaultsDto fields (default_agent_definition_id, default_agent_version_id,
 * default_prompt_pipeline_id, default_prompt_version_id, etc.).
 */
async function setAsDefaultMode(id = selectedModeId.value): Promise<boolean> {
  if (!canAccessBackend.value || !id) return false
  const modeRow = modes.value.find((m) => m.id === id)
  const versionId = selectedModeDetail.value?.id === id
    ? (selectedModeDetail.value.latest_published_mode_version_id ?? modeRow?.latest_published_mode_version_id ?? null)
    : (modeRow?.latest_published_mode_version_id ?? null)

  settingDefault.value = true
  try {
    const current = await api.getWorkspaceDefaults()
    const draftDefaults = await api.updateWorkspaceDefaultsDraft({
      default_agent_id: current.default_agent_id ?? null,
      default_agent_definition_id: current.default_agent_definition_id ?? null,
      default_agent_version_id: current.default_agent_version_id ?? null,
      default_agent_mode_id: id,
      default_mode_version_id: versionId,
      default_prompt_pipeline_id: current.default_prompt_pipeline_id ?? null,
      default_prompt_version_id: current.default_prompt_version_id ?? null,
    }, current.revision ?? null)
    await api.publishWorkspaceDefaults(draftDefaults.revision ?? null)
    defaultModeId.value = id
    notify.success(tr('settings.teamDefaultSet', undefined, 'Default mode updated'))
    return true
  } catch (error) {
    notify.error(error)
    return false
  } finally {
    settingDefault.value = false
  }
}

async function loadModeVersions(id = selectedModeId.value): Promise<void> {
  if (!canAccessBackend.value || !id) return
  modeVersionsLoading.value = true
  try {
    modeVersions.value = await api.listAgentModeVersions(id)
  } catch {
    modeVersions.value = []
  } finally {
    modeVersionsLoading.value = false
  }
}

async function restoreModeVersion(version: ModeVersionDto): Promise<boolean> {
  const snapshot = version.snapshot
  if (!snapshot) {
    notify.error(new Error(tr('settings.modeVersionSnapshotMissing', undefined, 'Version snapshot is missing')))
    return false
  }
  if (!(await discardOrConfirm())) return false
  const nodes = (snapshot.nodes ?? []).map((node) => ({
    node_key: String(node.node_key ?? ''),
    agent_definition_id: String(node.agent_definition_id ?? ''),
    layer: (node.layer === 'execution' ? 'execution' : 'operation') as 'operation' | 'execution',
    label: (node.label as string | null | undefined) ?? null,
    position: (node.position as { x: number; y: number } | null | undefined) ?? null,
    config: (node.config as Record<string, unknown> | null | undefined) ?? null,
    relationship: (node.relationship as Record<string, unknown> | null | undefined) ?? null,
    model_strategy_override: (node.model_strategy_override as Record<string, unknown> | null | undefined) ?? null,
  })).filter((node) => node.node_key && node.agent_definition_id)
  const edges = (snapshot.edges ?? []).map((edge) => ({
    edge_key: String(edge.edge_key ?? ''),
    source_node_key: String(edge.source_node_key ?? ''),
    target_node_key: String(edge.target_node_key ?? ''),
    ...(edge.condition ? { condition: edge.condition as Record<string, unknown> } : {}),
  })).filter((edge) => edge.source_node_key && edge.target_node_key)
  const snapshotMode = snapshot.mode as Record<string, unknown> | undefined
  const topology: AgentModeDraftUpdate = {
    nodes,
    edges,
    bindings: (snapshot.bindings ?? []) as AgentModeBindingDto[],
    canvas_layout: snapshot.canvas_layout ?? null,
    instructions: snapshot.instructions ?? null,
    orchestration_tier: normalizeOrchestrationTier(snapshotMode?.orchestration_tier),
    ...(snapshot.runtime_policy ? { runtime_policy: snapshot.runtime_policy } : {}),
  }

  modeBusy.value = true
  try {
    if (selectedModeReadOnly.value) {
      const name = selectedMode.value?.display_name ?? 'mode'
      const created = await api.createAgentModeDraft({
        ...topology,
        display_name: tr('settings.modeRestoreName', { name, version: version.version }, `${name} v${version.version}`),
        description: selectedMode.value?.description ?? null,
      })
      await loadModes({ force: true })
      await selectMode(created.id, { force: true })
      notify.success({ message: tr('settings.modeRestored', { version: version.version }, `Restored v${version.version}`) })
      return true
    }
    if (selectedModeId.value) {
      const saved = await api.updateAgentModeDraft(selectedModeId.value, topology, modeEtag.value)
      if (saved) syncSavedTopologyResponse(saved)
      notify.success({ message: tr('settings.modeRestored', { version: version.version }, `Restored v${version.version}`) })
      return true
    }
    return false
  } catch (error) {
    notify.error(error)
    return false
  } finally {
    modeBusy.value = false
  }
}

/** Test-only reset to keep Vitest suites isolated. */
export function __resetAgentModesStoreForTests(): void {
  modes.value = []
  agents.value = []
  providers.value = []
  routes.value = []
  runtimePolicyDefinitions.value = []
  manifestToolIds.value = []
  defaultModeId.value = null
  selectedModeId.value = ''
  selectedModeDetail.value = null
  modeEtag.value = null
  modesLoading.value = false
  agentsLoading.value = false
  detailLoading.value = false
  detailFailure.value = null
  modeBusy.value = false
  settingDefault.value = false
  draft.value = createEmptyModeDraft()
  modeBaselineSignal.value = ''
  historyPast.value = []
  historyFuture.value = []
  lastCoalesceKey = null
  lastCoalesceAt = 0
  teamDraft.value = createEmptyTeamDraft()
  teamBaselineSignal.value = ''
  modeVersions.value = []
  modeVersionsLoading.value = false
  modesReadPromise = null
  agentsReadPromise = null
  detailGeneration += 1
}

export function useAgentModesStore() {
  return {
    // Catalog & selection
    modes,
    sortedModes,
    agents,
    agentViews: computed(() => toAgentViews(agents.value)),
    providers,
    routes,
    runtimePolicyDefinitions,
    manifestToolIds,
    defaultModeId,
    selectedModeId,
    selectedMode,
    selectedModeDetail,
    selectedModeReadOnly,
    modeEtag,

    // Status flags
    modesLoading,
    agentsLoading,
    detailLoading,
    detailFailure,
    modeBusy,
    settingDefault,

    // Draft & dirty tracking
    draft,
    teamDraft,
    modeDirty,
    teamDirty,
    isDirty,
    canUndo,
    canRedo,
    parsedPolicySettings,
    runtimePolicy,
    parsedBindings,
    modeVersions,
    modeVersionsLoading,

    // Helpers
    sortModes,
    modeLocalizedName,
    modeLocalizedDescription,
    agentLocalizedName,
    agentLocalizedDescription,
    isModePackManaged,
    isModeReadOnly,

    // Actions
    mergeExternalModes,
    mergeExternalAgents,
    loadAll,
    loadModes,
    loadAgents,
    loadPoliciesAndRoutes,
    loadWorkspaceDefaults,
    selectMode,
    commitDraft,
    undo,
    redo,
    setRuntimePolicyKind,
    togglePolicyFeature,
    saveModeDraft,
    publishMode,
    createMode,
    cloneMode,
    deleteOrArchiveMode,
    saveTeamDraft,
    publishTeam,
    setAsDefaultMode,
    loadModeVersions,
    restoreModeVersion,
    discardOrConfirm,
    canLeave: discardOrConfirm,
    resetDraftsToBaseline,
  }
}
