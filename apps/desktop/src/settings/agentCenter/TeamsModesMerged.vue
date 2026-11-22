<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowDown,
  ArrowUp,
  Copy,
  Crown,
  GitBranch,
  History,
  LayoutGrid,
  Plus,
  Redo2,
  RefreshCw,
  Search,
  Trash2,
  Undo2,
  Workflow,
  X,
} from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'
import ModeGraphCanvas from '@/components/modeEditor/ModeGraphCanvas.vue'
import ModeInspectorDrawer, { type InspectorPanelMode } from '@/components/modeEditor/ModeInspectorDrawer.vue'
import { dagreLayoutMode, findFreeDagreSlot, type ModeLane } from '@/components/modeEditor/modeDagreLayout'
import { hasBlockingModeIssue, validateMode } from '@/components/canvas/modeValidation'
import type {
  AgentModeBindingDto,
  AgentModeDto,
  AgentModeEdgeDto,
  AgentModeNodeDto,
  AgentModeTopologyDto,
  AgentViewDto,
  ModeVersionDto,
} from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import {
  agentUsableForTeam,
  normalizeLayer,
  useAgentModesStore,
  type ModeWorkspaceView,
} from '@/composables/useAgentModesStore'
import { useHostAccess } from '@/lib/hostAccess'
import { modeIcon } from '@/lib/modePresentation'
import { consumeRequest, pendingModeId } from '@/lib/pageRequests'
import {
  agentSlugOf,
  buildTeamTopology,
  defaultMemberScope,
  MAX_TEAM_MEMBERS,
  type MemberScope,
  type TeamStyle,
} from './teamShape'
import { TEAM_TEMPLATES, pickLeader, pickMembers, type TeamTemplateDefinition } from './teamTemplates'
import {
  providerRuntimeChannel,
  runtimeChannelBadgeLabel,
  type RuntimeChannelKind,
} from './directory/agentDirectoryHelpers'

const props = withDefaults(defineProps<{
  modes?: Array<AgentModeDto | AgentModeTopologyDto>
  agents?: AgentViewDto[]
  canAccessBackend?: boolean
  loading?: boolean
  preferredView?: ModeWorkspaceView
}>(), {
  modes: undefined,
  agents: undefined,
  canAccessBackend: undefined,
  loading: false,
  preferredView: undefined,
})

const emit = defineEmits<{
  changed: []
  'open-graph': [id: string]
}>()

const { t, te, locale } = useI18n()
const { notify, confirm } = useNotifications()
const { canAccessBackend: hostCanAccessBackend } = useHostAccess()
const store = useAgentModesStore()

const canvasRef = ref<InstanceType<typeof ModeGraphCanvas> | null>(null)
const query = ref('')
const creatingNew = ref(false)
const selectedNodeId = ref<string | null>(null)
const selectedEdgeId = ref<string | null>(null)
const panelMode = ref<InspectorPanelMode>('overview')
const addNodeLane = ref<ModeLane>('operation')
const spawnMemberToAdd = ref('')
let modeNavigationGeneration = 0

const templates = TEAM_TEMPLATES

const effectiveCanAccessBackend = computed(() =>
  props.canAccessBackend !== undefined ? props.canAccessBackend : hostCanAccessBackend.value,
)

const userSwitchedView = ref(false)
const activeView = computed<ModeWorkspaceView>(() =>
  props.preferredView && !userSwitchedView.value
    ? props.preferredView
    : store.activeWorkspaceView.value,
)
const selectedModeId = computed(() => store.selectedModeId.value)
const selectedMode = computed(() => store.selectedMode.value)
const detail = computed(() => store.selectedModeDetail.value)
const detailLoading = computed(() => store.detailLoading.value)
const detailFailure = computed(() => store.detailFailure.value)
const modeBusy = computed(() => store.modeBusy.value)
const settingDefault = computed(() => store.settingDefault.value)
const defaultModeId = computed(() => store.defaultModeId.value)
const selectedModeReadOnly = computed(() => store.selectedModeReadOnly.value)
const isPackManaged = computed(() => store.isModePackManaged(selectedMode.value))

const draft = computed(() => store.draft.value)
const modeNodes = computed(() => store.draft.value.nodes)
const modeEdges = computed(() => store.draft.value.edges)
const agents = computed(() => store.agents.value)
const allAgentViews = computed(() => store.agentViews.value)
const operationAgents = computed(() => allAgentViews.value.filter((a) => normalizeLayer(a.layer) !== 'execution'))
const executionAgents = computed(() => allAgentViews.value.filter((a) => normalizeLayer(a.layer) === 'execution'))

const liveShape = computed(() => store.teamShape.value)
const canEditShape = computed(() => liveShape.value?.editable ?? false)

const filteredModes = computed(() => {
  const q = query.value.trim().toLowerCase()
  const sorted = store.sortedModes.value
  if (!q) return sorted
  return sorted.filter((m) =>
    `${store.modeLocalizedName(m, locale.value)} ${store.modeLocalizedDescription(m, locale.value)} ${m.slug ?? ''}`
      .toLowerCase()
      .includes(q),
  )
})

const isDefaultMode = computed(() => Boolean(selectedModeId.value && defaultModeId.value === selectedModeId.value))
const canSetDefault = computed(() => {
  const status = detail.value?.status ?? selectedMode.value?.status
  const versionId = detail.value?.latest_published_mode_version_id ?? selectedMode.value?.latest_published_mode_version_id
  return status === 'published' && Boolean(versionId)
})

const definitionStatusLabel = (status: string) =>
  (te(`agentCenter.filter.${status}`) ? t(`agentCenter.filter.${status}`) : status)

function readinessVariant(status?: string): 'default' | 'secondary' | 'outline' {
  if (status === 'published') return 'default'
  if (status === 'archived') return 'secondary'
  return 'outline'
}

function modeStatusLabel(mode: Pick<AgentModeTopologyDto, 'managed' | 'pack_managed' | 'status'>): string {
  if (store.isModePackManaged(mode)) return t('settings.teamBuiltIn')
  if (mode.status === 'published') return t('settings.teamStatusPublished')
  if (mode.status === 'archived') return t('settings.teamArchived')
  return t('settings.teamDraft')
}

function layerDisplay(layer?: string): string {
  return normalizeLayer(layer) === 'execution'
    ? t('agentCenter.filter.executionLayer')
    : t('agentCenter.filter.operationLayer')
}

// ── Shared Default Run Options ───────────────────────────────────────────────

type DefaultOptionKey = 'plan_first' | 'spec_enabled' | 'multi_agent' | 'bulletin_board' | 'worktree'

const DEFAULT_OPTION_ITEMS: ReadonlyArray<{ key: DefaultOptionKey; labelKey: string }> = [
  { key: 'plan_first', labelKey: 'commandPanel.plan' },
  { key: 'spec_enabled', labelKey: 'commandPanel.spec' },
  { key: 'multi_agent', labelKey: 'commandPanel.agents' },
  { key: 'bulletin_board', labelKey: 'commandPanel.bulletin' },
  { key: 'worktree', labelKey: 'commandPanel.worktree' },
]

function isTeamOptionEnabled(key: DefaultOptionKey): boolean {
  if (!canEditShape.value) {
    return Boolean(store.draft.value.defaultOptions?.[key])
  }
  return Boolean(store.teamDraft.value.defaultOptions?.[key])
}

function toggleTeamOption(key: DefaultOptionKey): void {
  if (selectedModeReadOnly.value) return
  if (!canEditShape.value) {
    const base = store.draft.value.defaultOptions ?? store.emptySpaceOptions()
    const next = { ...base, [key]: !base[key] }
    store.commitDraft((cur) => ({ ...cur, defaultOptions: next }), { coalesceKey: `team-opt:${key}` })
    store.teamDraft.value = { ...store.teamDraft.value, defaultOptions: { ...next } }
    return
  }
  const base = store.teamDraft.value.defaultOptions ?? store.emptySpaceOptions()
  store.teamDraft.value = {
    ...store.teamDraft.value,
    defaultOptions: { ...base, [key]: !base[key] },
  }
}

// ── View Switching & Rail Selection ──────────────────────────────────────────

function setView(view: ModeWorkspaceView) {
  creatingNew.value = false
  userSwitchedView.value = true
  store.switchWorkspaceView(view, { userInitiated: true })
  if (view === 'topology' && selectedModeId.value) {
    emit('open-graph', selectedModeId.value)
  }
}

async function selectModeRow(id: string) {
  if (creatingNew.value && !(await store.discardOrConfirm())) return
  creatingNew.value = false
  const switched = await store.selectMode(id, {
    preserveWorkspaceView: Boolean(props.preferredView),
  })
  if (switched) {
    selectedNodeId.value = null
    selectedEdgeId.value = null
    panelMode.value = 'overview'
  }
}

async function openTemplatePicker() {
  if (!effectiveCanAccessBackend.value) return
  if (!(await store.discardOrConfirm())) return
  creatingNew.value = true
}

// ── Squad View (Simple Team / Pipeline / Solo) ───────────────────────────────

const editingName = computed({
  get: () => store.teamDraft.value.displayName,
  set: (value: string | number) => { store.teamDraft.value.displayName = String(value) },
})

const editingDescription = computed({
  get: () => store.teamDraft.value.description,
  set: (value: string) => { store.teamDraft.value.description = value },
})

const editingInstructions = computed({
  get: () => store.teamDraft.value.instructions,
  set: (value: string) => { store.teamDraft.value.instructions = value },
})

const selection = computed(() => store.teamDraft.value.selection)
const memberScopes = computed(() => store.teamDraft.value.memberScopes)

const leaderOptions = computed(() => operationAgents.value
  .filter(agentUsableForTeam)
  .map((agent) => ({
    value: agent.id,
    label: store.agentLocalizedName(agent, locale.value),
  })))

const memberPool = computed(() => {
  const style = selection.value.style
  if (style === 'solo') return []
  const base = allAgentViews.value.filter((agent) => agent.id !== selection.value.leaderId && agentUsableForTeam(agent))
  return style === 'pipeline' ? base.filter((agent) => normalizeLayer(agent.layer) === 'execution') : base
})

const memberRows = computed(() => selection.value.memberIds
  .map((id) => allAgentViews.value.find((agent) => agent.id === id))
  .filter((agent): agent is AgentViewDto => Boolean(agent)))

const availableMembers = computed(() => memberPool.value
  .filter((agent) => !selection.value.memberIds.includes(agent.id)))

const memberLimitReached = computed(() => selection.value.memberIds.length >= MAX_TEAM_MEMBERS)
const unavailableAgents = computed(() => allAgentViews.value.filter((agent) =>
  agent.id !== selection.value.leaderId && !agentUsableForTeam(agent) && !selection.value.memberIds.includes(agent.id)))
const selectedLeader = computed(() => allAgentViews.value.find((agent) => agent.id === selection.value.leaderId) ?? null)

const teamErrors = computed<string[]>(() => {
  const errors: string[] = []
  const name = editingName.value.trim()
  if (!name) errors.push(t('settings.teamNameRequired'))
  else if (store.modes.value.some((mode) => mode.id !== selectedModeId.value && mode.display_name.trim().toLowerCase() === name.toLowerCase())) {
    errors.push(t('settings.teamNameTaken'))
  }
  if (!selection.value.leaderId) errors.push(t('settings.teamLeaderRequired'))
  else if (selectedLeader.value && (!operationAgents.value.some((agent) => agent.id === selectedLeader.value?.id) || !agentUsableForTeam(selectedLeader.value))) {
    errors.push(t('settings.teamLeaderUnavailable'))
  }
  if (selection.value.style !== 'solo' && selection.value.memberIds.length === 0) {
    errors.push(t('settings.teamMembersRequired'))
  }
  if (selection.value.memberIds.length > MAX_TEAM_MEMBERS) {
    errors.push(t('settings.teamMembersLimit', { max: MAX_TEAM_MEMBERS }))
  }
  const unavailable = memberRows.value.filter((agent) => !agentUsableForTeam(agent) || !memberPool.value.some((candidate) => candidate.id === agent.id))
  if (unavailable.length > 0) {
    errors.push(t('settings.teamMemberUnavailable', { names: unavailable.map((agent) => store.agentLocalizedName(agent, locale.value)).join(', ') }))
  }
  return errors
})

function agentOf(id: string): AgentViewDto | undefined {
  return allAgentViews.value.find((agent) => agent.id === id)
}

function scopeOf(id: string): MemberScope {
  const agent = agentOf(id)
  return memberScopes.value[id] ?? (agent ? defaultMemberScope(agent) : { read: true, write: false })
}

function toggleScope(id: string, key: 'read' | 'write') {
  if (selectedModeReadOnly.value) return
  const current = scopeOf(id)
  store.teamDraft.value = {
    ...store.teamDraft.value,
    memberScopes: {
      ...store.teamDraft.value.memberScopes,
      [id]: { ...current, [key]: !current[key] },
    },
  }
}

function eligibleForStyle(style: TeamStyle, leaderId: string): AgentViewDto[] {
  if (style === 'solo') return []
  const base = allAgentViews.value.filter((agent) => agent.id !== leaderId && agentUsableForTeam(agent))
  return style === 'pipeline' ? base.filter((agent) => normalizeLayer(agent.layer) === 'execution') : base
}

function setLeaderId(leaderId: string) {
  if (selectedModeReadOnly.value) return
  const allowed = eligibleForStyle(selection.value.style, leaderId)
  const kept = selection.value.memberIds.filter((id) => id !== leaderId && allowed.some((agent) => agent.id === id))
  store.teamDraft.value = {
    ...store.teamDraft.value,
    selection: { ...selection.value, leaderId, memberIds: kept },
  }
}

function setStyle(style: TeamStyle) {
  if (selectedModeReadOnly.value) return
  const allowed = eligibleForStyle(style, selection.value.leaderId)
  const kept = selection.value.memberIds.filter((id) => allowed.some((agent) => agent.id === id))
  const nextScopes = { ...store.teamDraft.value.memberScopes }
  let memberIds: string[] = []
  if (style !== 'solo') {
    memberIds = kept.length > 0 ? kept : allowed.slice(0, 2).map((agent) => agent.id)
    for (const id of memberIds) {
      const agent = agentOf(id)
      if (agent && !nextScopes[id]) nextScopes[id] = defaultMemberScope(agent)
    }
  }
  store.teamDraft.value = {
    ...store.teamDraft.value,
    orchestrationTier: null,
    selection: { ...selection.value, style, memberIds },
    memberScopes: nextScopes,
  }
}

async function toggleMember(id: string) {
  if (selectedModeReadOnly.value) return
  const members = [...selection.value.memberIds]
  const nextScopes = { ...store.teamDraft.value.memberScopes }
  const index = members.indexOf(id)
  if (index >= 0) {
    members.splice(index, 1)
  } else {
    if (members.length >= MAX_TEAM_MEMBERS) return
    if (!memberPool.value.some((agent) => agent.id === id)) return
    await store.ensureAgentDetails([id])
    members.push(id)
    const agent = agentOf(id)
    if (agent) nextScopes[id] = defaultMemberScope(agent)
  }
  store.teamDraft.value = {
    ...store.teamDraft.value,
    selection: { ...selection.value, memberIds: members },
    memberScopes: nextScopes,
  }
}

function moveMember(index: number, delta: -1 | 1) {
  if (selectedModeReadOnly.value) return
  const nextIndex = index + delta
  const members = [...selection.value.memberIds]
  if (nextIndex < 0 || nextIndex >= members.length) return
  const [moved] = members.splice(index, 1)
  members.splice(nextIndex, 0, moved)
  store.teamDraft.value = {
    ...store.teamDraft.value,
    selection: { ...selection.value, memberIds: members },
  }
}

async function createFromTemplate(template: TeamTemplateDefinition) {
  if (!effectiveCanAccessBackend.value) return
  const leaderId = pickLeader(operationAgents.value.filter(agentUsableForTeam))
  if (!leaderId) {
    notify.error(new Error(t('settings.teamNoLeader')))
    return
  }
  const pool = template.key === 'pipeline'
    ? executionAgents.value.filter(agentUsableForTeam)
    : allAgentViews.value.filter((agent) => agentUsableForTeam(agent) && agent.id !== leaderId)
  const memberIds = template.key === 'solo' ? [] : pickMembers(template, pool).slice(0, MAX_TEAM_MEMBERS)
  if (template.key !== 'solo' && memberIds.length === 0) {
    notify.error(new Error(t('settings.teamTemplateNoMembers')))
    return
  }
  if (memberIds.length > 0) {
    await store.ensureAgentDetails(memberIds)
  }
  const name = t(template.labelKey)
  const topology = buildTeamTopology({
    style: template.key,
    leaderId,
    memberIds,
    instructions: template.instructionsKey ? t(template.instructionsKey) : '',
    displayName: name,
    description: t(template.hintKey),
  }, allAgentViews.value)
  const created = await store.createMode({
    displayName: name,
    topology,
  })
  if (created) {
    creatingNew.value = false
    store.switchWorkspaceView('team')
    emit('changed')
  }
}

// ── Complex Mode Role Summary (for Squad View on 2-layer / branching graphs) ─

interface ComplexRoleSummaryRow {
  key: string
  name: string
  layer: 'operation' | 'execution'
  isConversation: boolean
  isSpawnOnly: boolean
  duty: string
  strategyBadge: string
  runtimeChannel: RuntimeChannelKind | null
}

function runtimeChannelForProviderId(providerId?: string | null): RuntimeChannelKind | null {
  if (!providerId) return null
  const provider = store.providers.value.find((p) => p.id === providerId)
  return provider ? providerRuntimeChannel(provider) : null
}

function runtimeChannelForAgent(
  agent?: Partial<Pick<AgentViewDto, 'model_binding' | 'model_strategy'>> | null,
): RuntimeChannelKind | null {
  if (!agent) return null
  const bindingProvider = agent.model_binding?.mode === 'fixed' ? agent.model_binding.provider_instance_id : null
  const strat = (agent.model_strategy && typeof agent.model_strategy === 'object')
    ? (agent.model_strategy as Record<string, unknown>)
    : null
  const stratProvider = strat?.kind === 'fixed' && typeof strat.provider_instance_id === 'string'
    ? strat.provider_instance_id
    : null
  return runtimeChannelForProviderId(bindingProvider || stratProvider)
}

const complexRoleRows = computed<ComplexRoleSummaryRow[]>(() => {
  const rows: ComplexRoleSummaryRow[] = []
  const seenAgentSlugs = new Set<string>()

  for (const node of modeNodes.value) {
    const agent = agents.value.find((a) => a.id === (node.agent_id || node.agent_definition_id))
    const slug = agent ? agentSlugOf(agent) : ''
    if (slug) seenAgentSlugs.add(slug)
    const lane = normalizeLayer(node.lane)
    const name = (node.label ?? '').trim()
      || (agent ? store.agentLocalizedName(agent, locale.value) : (node.agent_id || node.id))
    const relDuty = typeof node.relationship?.duty === 'string' ? node.relationship.duty.trim() : ''
    const duty = relDuty || (agent ? store.agentLocalizedDescription(agent, locale.value) : '')
    const overrideKind = node.model_strategy_override?.kind
    const strategyBadge = overrideKind === 'fixed'
      ? t('settings.modeStrategyFixedBadge')
      : overrideKind === 'route'
        ? t('settings.modeStrategyRouteBadge')
        : t('settings.modeStrategyInheritBadge')
    const runtimeChannel = overrideKind === 'fixed'
      ? runtimeChannelForProviderId(node.model_strategy_override?.provider_instance_id)
      : runtimeChannelForAgent(agent)
    rows.push({
      key: `node:${node.id}`,
      name,
      layer: lane,
      isConversation: node.config?.conversation === true,
      isSpawnOnly: false,
      duty,
      strategyBadge,
      runtimeChannel,
    })
  }

  for (const binding of store.parsedBindings.value.value ?? []) {
    if (binding.node_key || !binding.agent_ref) continue
    const cleanSlug = binding.agent_ref.replace(/^agent:/, '')
    if (seenAgentSlugs.has(cleanSlug)) continue
    seenAgentSlugs.add(cleanSlug)
    const agent = agents.value.find((a) => agentSlugOf(a) === cleanSlug)
    rows.push({
      key: `spawn:${cleanSlug}`,
      name: agent ? store.agentLocalizedName(agent, locale.value) : cleanSlug,
      layer: normalizeLayer(agent?.layer ?? 'execution'),
      isConversation: false,
      isSpawnOnly: true,
      duty: agent ? store.agentLocalizedDescription(agent, locale.value) : '',
      strategyBadge: t('settings.modeStrategyInheritBadge'),
      runtimeChannel: runtimeChannelForAgent(agent),
    })
  }

  return rows
})

// ── Unified Save / Publish / Clone / Delete / Default Actions ────────────────

const publishIssues = computed(() => validateMode(
  modeNodes.value.map((node) => ({
    id: node.id,
    lane: normalizeLayer(node.lane),
    agent_id: node.agent_id,
    config: node.config ?? null,
  })),
  modeEdges.value.map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
  { requireConversation: true },
))
const publishBlocked = computed(() => hasBlockingModeIssue(publishIssues.value))

const saveDraftDisabled = computed(() => {
  if (!selectedModeId.value || !effectiveCanAccessBackend.value || modeBusy.value || selectedModeReadOnly.value) {
    return true
  }
  if (activeView.value === 'team' && !canEditShape.value) {
    // Allow saving default_options edits on complex modes via saveModeDraft, while blocking empty overwrites
    return !store.modeDirty.value
  }
  return false
})

const publishActionDisabled = computed(() => {
  if (!selectedModeId.value || !effectiveCanAccessBackend.value || modeBusy.value || selectedModeReadOnly.value) {
    return true
  }
  if (activeView.value === 'team') {
    if (!canEditShape.value) return publishBlocked.value
    return teamErrors.value.length > 0
  }
  return publishBlocked.value
})

async function handleSaveDraft() {
  if (!effectiveCanAccessBackend.value || selectedModeReadOnly.value) return
  if (activeView.value === 'team' && canEditShape.value) {
    const savedId = await store.saveTeamDraft()
    if (savedId) {
      creatingNew.value = false
      emit('changed')
    }
    return
  }
  const ok = await store.saveModeDraft()
  if (ok) emit('changed')
}

async function handlePublish() {
  if (!effectiveCanAccessBackend.value || selectedModeReadOnly.value) return
  if (activeView.value === 'team' && canEditShape.value) {
    if (teamErrors.value.length > 0) {
      notify.error(new Error(teamErrors.value[0]))
      return
    }
    const ok = await store.publishTeam()
    if (ok) emit('changed')
    return
  }
  if (publishBlocked.value) {
    notify.error(new Error(t('settings.modePublishBlocked')))
    return
  }
  const ok = await store.publishMode()
  if (ok) emit('changed')
}

async function handleCreateBlankMode() {
  if (!effectiveCanAccessBackend.value) return
  if (!(await store.discardOrConfirm())) return
  creatingNew.value = false
  const created = await store.createMode()
  if (created) {
    selectedNodeId.value = null
    selectedEdgeId.value = null
    panelMode.value = 'overview'
    store.switchWorkspaceView('topology', { userInitiated: true })
    emit('changed')
  }
}

async function handleCloneMode() {
  if (!effectiveCanAccessBackend.value || !selectedModeId.value) return
  const cloned = await store.cloneMode(selectedModeId.value)
  if (cloned) {
    creatingNew.value = false
    selectedNodeId.value = null
    selectedEdgeId.value = null
    panelMode.value = 'overview'
    emit('changed')
  }
}

async function handleDeleteMode() {
  if (!effectiveCanAccessBackend.value || !selectedModeId.value || isPackManaged.value) return
  const ok = await store.deleteOrArchiveMode(selectedModeId.value)
  if (ok) {
    creatingNew.value = false
    selectedNodeId.value = null
    selectedEdgeId.value = null
    panelMode.value = 'overview'
    emit('changed')
  }
}

async function setAsDefault() {
  if (!effectiveCanAccessBackend.value || !selectedModeId.value) return
  const ok = await store.setAsDefaultMode(selectedModeId.value)
  if (ok) emit('changed')
}

// ── Topology Canvas & Docked Inspector Handlers ──────────────────────────────

const selectedNode = computed<AgentModeNodeDto | null>(() =>
  selectedNodeId.value ? modeNodes.value.find((n) => n.id === selectedNodeId.value) ?? null : null,
)

const selectedEdge = computed<AgentModeEdgeDto | null>(() =>
  selectedEdgeId.value ? modeEdges.value.find((e) => e.id === selectedEdgeId.value) ?? null : null,
)

function nodeKeyOf(node: AgentModeNodeDto): string {
  return node.node_key ?? node.id
}

function newGraphId(prefix: string): string {
  const random = globalThis.crypto?.randomUUID?.().slice(0, 8) ?? Math.random().toString(36).slice(2, 10)
  return `${prefix}-${random}`
}

watch(selectedModeId, () => {
  if (selectedNodeId.value && !modeNodes.value.some((n) => n.id === selectedNodeId.value)) {
    selectedNodeId.value = null
  }
  if (selectedEdgeId.value && !modeEdges.value.some((e) => e.id === selectedEdgeId.value)) {
    selectedEdgeId.value = null
  }
})

consumeRequest(pendingModeId, async (modeId) => {
  const generation = ++modeNavigationGeneration
  if (!(await store.discardOrConfirm())) return
  query.value = ''
  creatingNew.value = false
  if (!store.modes.value.some((mode) => mode.id === modeId)) {
    await store.loadModes({ force: true })
  }
  if (generation !== modeNavigationGeneration) return
  if (!store.modes.value.some((mode) => mode.id === modeId)) {
    notify.error(t('app.loadFailedMessage'), { title: t('agentCenter.loadModesFailed'), key: 'search-navigation' })
    return
  }
  await store.selectMode(modeId, { force: true, preserveWorkspaceView: Boolean(props.preferredView) })
  selectedNodeId.value = null
  selectedEdgeId.value = null
  panelMode.value = 'overview'
})

function handleSelectNode(id: string | null) {
  selectedNodeId.value = id
  if (id) {
    selectedEdgeId.value = null
    panelMode.value = 'node'
    const node = modeNodes.value.find((n) => n.id === id)
    if (node?.agent_id) {
      void store.ensureAgentDetails([node.agent_id])
    }
  } else if (!selectedEdgeId.value && panelMode.value === 'node') {
    panelMode.value = 'overview'
  }
}

function handleSelectEdge(id: string | null) {
  selectedEdgeId.value = id
  if (id) {
    selectedNodeId.value = null
    panelMode.value = 'edge'
  } else if (!selectedNodeId.value && panelMode.value === 'edge') {
    panelMode.value = 'overview'
  }
}

function handleCloseInspectorPanel() {
  selectedNodeId.value = null
  selectedEdgeId.value = null
  panelMode.value = 'overview'
}

function handleNodeMoved(payload: { id: string; position: { x: number; y: number }; lane?: ModeLane }) {
  store.commitDraft((current) => ({
    ...current,
    nodes: current.nodes.map((node) =>
      node.id === payload.id
        ? {
            ...node,
            position: payload.position,
            ...(payload.lane ? { lane: payload.lane, layer: payload.lane } : {}),
          }
        : node,
    ),
  }))
}

function uniqueEdgeId(source: string, target: string): string {
  const base = `e-${source}-${target}`
  if (!modeEdges.value.some((edge) => edge.id === base)) return base
  let suffix = 2
  while (modeEdges.value.some((edge) => edge.id === `${base}-${suffix}`)) suffix += 1
  return `${base}-${suffix}`
}

function handleConnect(payload: { source: string; target: string }) {
  if (selectedModeReadOnly.value) return
  if (modeEdges.value.some((edge) => edge.source === payload.source && edge.target === payload.target)) return
  store.commitDraft((current) => ({
    ...current,
    edges: [
      ...current.edges,
      { id: uniqueEdgeId(payload.source, payload.target), source: payload.source, target: payload.target },
    ],
  }))
}

function handleUpdateEdgeEndpoint(payload: { id: string; source: string; target: string }) {
  if (selectedModeReadOnly.value) return
  store.commitDraft((current) => ({
    ...current,
    edges: current.edges.map((edge) =>
      edge.id === payload.id
        ? {
            ...edge,
            source: payload.source,
            target: payload.target,
            source_node_key: payload.source,
            target_node_key: payload.target,
          }
        : edge,
    ),
  }))
}

function handleDeleteEdge(id: string) {
  if (selectedModeReadOnly.value) return
  store.commitDraft((current) => ({
    ...current,
    edges: current.edges.filter((edge) => edge.id !== id),
  }))
  if (selectedEdgeId.value === id) {
    selectedEdgeId.value = null
    if (panelMode.value === 'edge') panelMode.value = 'overview'
  }
}

async function handleDeleteNode(id: string) {
  const node = modeNodes.value.find((item) => item.id === id)
  if (!node || selectedModeReadOnly.value) return
  if (node.config?.conversation === true) {
    notify.error(new Error(t('settings.conversationNodeRequired')))
    return
  }
  const confirmed = await confirm({
    title: t('settings.deleteNodeTitle'),
    message: t('settings.deleteNodeMessage', { name: node.label || node.agent_id }),
    confirmLabel: t('settings.deleteAction'),
    cancelLabel: t('common.cancel'),
    destructive: true,
  })
  if (!confirmed) return

  const key = nodeKeyOf(node)
  store.commitDraft((current) => {
    let nextBindingsDraft = current.bindingsDraft
    if (store.parsedBindings.value.value) {
      nextBindingsDraft = JSON.stringify(
        store.parsedBindings.value.value.filter((binding) => binding.node_key !== key),
        null,
        2,
      )
    }
    return {
      ...current,
      nodes: current.nodes.filter((item) => item.id !== id),
      edges: current.edges.filter((edge) => edge.source !== id && edge.target !== id),
      bindingsDraft: nextBindingsDraft,
    }
  })

  if (selectedNodeId.value === id) {
    selectedNodeId.value = null
    if (panelMode.value === 'node') panelMode.value = 'overview'
  }
}

function handleCloneNode(id: string) {
  if (selectedModeReadOnly.value) return
  const source = modeNodes.value.find((n) => n.id === id)
  if (!source) return
  const lane = normalizeLayer(source.lane)
  const newId = newGraphId('n')
  const pos = findFreeDagreSlot(
    modeNodes.value.map((n) => ({ id: n.id, lane: normalizeLayer(n.lane), position: n.position })),
    lane,
  )
  const nextConfig = source.config ? { ...source.config } : null
  if (nextConfig) delete nextConfig.conversation

  const clonedNode: AgentModeNodeDto = {
    ...source,
    id: newId,
    node_key: newId,
    position: pos,
    label: source.label ? t('common.copyName', { name: source.label }) : '',
    config: nextConfig && Object.keys(nextConfig).length > 0 ? nextConfig : null,
    relationship: source.relationship ? JSON.parse(JSON.stringify(source.relationship)) as Record<string, unknown> : null,
    model_strategy_override: source.model_strategy_override ? { ...source.model_strategy_override } : null,
  }

  store.commitDraft((current) => ({
    ...current,
    nodes: [...current.nodes, clonedNode],
  }))
  handleSelectNode(newId)
}

function handleSetConversation(id: string) {
  if (selectedModeReadOnly.value) return
  const target = modeNodes.value.find((n) => n.id === id)
  if (!target) return
  if (normalizeLayer(target.lane) !== 'operation') {
    notify.error(new Error(t('settings.conversationLayerRequired')))
    return
  }
  store.commitDraft((current) => ({
    ...current,
    nodes: current.nodes.map((node) => {
      if (node.id === id) {
        return { ...node, config: { ...(node.config ?? {}), conversation: true } }
      }
      if (node.config?.conversation === true) {
        const rest = { ...node.config }
        delete rest.conversation
        return { ...node, config: Object.keys(rest).length > 0 ? rest : null }
      }
      return node
    }),
  }))
}

function openAddNode(lane: ModeLane) {
  if (selectedModeReadOnly.value) return
  const pool = lane === 'execution'
    ? agents.value.filter((agent) => normalizeLayer(agent.layer) === 'execution')
    : agents.value.filter((agent) => normalizeLayer(agent.layer) !== 'execution')
  if (pool.length === 0) {
    notify.error(new Error(t(lane === 'execution' ? 'settings.modeNoAgentsInLane' : 'settings.modeNoAgents')))
    return
  }
  addNodeLane.value = lane
  selectedNodeId.value = null
  selectedEdgeId.value = null
  panelMode.value = 'add-node'
}

function confirmAddNode(payload: { agentId: string; lane: ModeLane; label?: string }) {
  if (selectedModeReadOnly.value) return
  const agent = agents.value.find((item) => item.id === payload.agentId)
  if (!agent) {
    notify.error(new Error(t('settings.modeNoAgents')))
    return
  }
  const lane = normalizeLayer(payload.lane)
  const id = newGraphId('n')
  const hasConversation = modeNodes.value.some((node) => node.config?.conversation === true)
  const config = lane === 'operation' && !hasConversation ? { conversation: true } : null
  const position = findFreeDagreSlot(
    modeNodes.value.map((node) => ({ id: node.id, lane: normalizeLayer(node.lane), position: node.position })),
    lane,
  )
  const node: AgentModeNodeDto = {
    id,
    node_key: id,
    agent_id: agent.id,
    agent_definition_id: agent.id,
    lane,
    layer: lane,
    position,
    label: payload.label ?? '',
    config,
    relationship: null,
    model_strategy_override: null,
  }
  store.commitDraft((current) => ({
    ...current,
    nodes: [...current.nodes, node],
  }))
  handleSelectNode(id)
}

function autoLayoutGraph() {
  if (selectedModeReadOnly.value) return
  const result = dagreLayoutMode(
    modeNodes.value.map((node) => ({ id: node.id, lane: normalizeLayer(node.lane) })),
    modeEdges.value.map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
  )
  store.commitDraft((current) => ({
    ...current,
    nodes: current.nodes.map((node) => {
      const position = result.positions[node.id]
      return position ? { ...node, position } : node
    }),
  }))
  void nextTick(() => canvasRef.value?.fitView())
}

function handleUpdateOverview(patch: Partial<typeof draft.value>, coalesceKey?: string) {
  store.commitDraft((current) => ({ ...current, ...patch }), { coalesceKey })
}

function handleUpdateNode(payload: {
  id: string
  patch: Partial<AgentModeNodeDto>
  exclusiveConversation?: boolean
  coalesceKey?: string
}) {
  store.commitDraft((current) => ({
    ...current,
    nodes: current.nodes.map((node) => {
      if (node.id === payload.id) {
        const nextLane = payload.patch.lane ? normalizeLayer(payload.patch.lane) : normalizeLayer(node.lane)
        let nextPos = node.position
        if (nextLane !== normalizeLayer(node.lane)) {
          nextPos = findFreeDagreSlot(
            current.nodes.filter((n) => n.id !== node.id).map((n) => ({ id: n.id, lane: normalizeLayer(n.lane), position: n.position })),
            nextLane,
          )
        }
        return { ...node, ...payload.patch, position: nextPos }
      }
      if (payload.exclusiveConversation && node.config?.conversation === true) {
        const rest = { ...node.config }
        delete rest.conversation
        return { ...node, config: Object.keys(rest).length > 0 ? rest : null }
      }
      return node
    }),
  }), { coalesceKey: payload.coalesceKey })
}

function handleUpdateEdge(payload: {
  id: string
  label?: string
  condition?: Record<string, unknown> | null
  coalesceKey?: string
}) {
  store.commitDraft((current) => ({
    ...current,
    edges: current.edges.map((edge) =>
      edge.id === payload.id
        ? { ...edge, label: payload.label, condition: payload.condition }
        : edge,
    ),
  }), { coalesceKey: payload.coalesceKey })
}

function toggleVersionsPanel() {
  if (!selectedModeId.value) return
  if (panelMode.value === 'versions') {
    panelMode.value = 'overview'
    return
  }
  selectedNodeId.value = null
  selectedEdgeId.value = null
  panelMode.value = 'versions'
  void store.loadModeVersions(selectedModeId.value)
}

async function handleRestoreVersion(version: ModeVersionDto) {
  const ok = await store.restoreModeVersion(version)
  if (ok) panelMode.value = 'overview'
}

// ── Spawnable Members Belt (Topology view on Team-shaped modes) ──────────────

const MAX_SPAWN_MEMBERS = 4
const parsedBindingsList = computed(() => store.parsedBindings.value.value)
const leaderGraphNode = computed(() =>
  modeNodes.value.find((node) => normalizeLayer(node.lane) === 'operation' && node.config?.conversation === true)
  ?? modeNodes.value.find((node) => normalizeLayer(node.lane) === 'operation')
  ?? null,
)
const spawnMemberSlugs = computed<string[]>(() => {
  const types = leaderGraphNode.value?.relationship?.agent_types
  return Array.isArray(types) ? types.filter((v): v is string => typeof v === 'string' && v.length > 0) : []
})
const showSpawnBelt = computed(() =>
  Boolean(leaderGraphNode.value) && !modeNodes.value.some((node) => normalizeLayer(node.lane) === 'execution'),
)
const spawnMembers = computed(() => {
  if (!showSpawnBelt.value || !parsedBindingsList.value) return []
  const rows: Array<{ agentRef: string; name: string; layer: string; scope: MemberScope }> = []
  const seen = new Set<string>()
  for (const binding of parsedBindingsList.value) {
    if (binding.node_key || !binding.agent_ref || seen.has(binding.agent_ref)) continue
    seen.add(binding.agent_ref)
    const agent = agents.value.find((item) => agentSlugOf(item) === binding.agent_ref)
    const resources = binding.envelope?.resources as { read?: unknown; write?: unknown } | undefined
    const has = (value: unknown) => Array.isArray(value) && value.length > 0
    rows.push({
      agentRef: binding.agent_ref,
      name: agent ? store.agentLocalizedName(agent, locale.value) : binding.agent_ref,
      layer: agent ? layerDisplay(agent.layer) : '—',
      scope: {
        read: resources ? has(resources.read) : true,
        write: resources ? has(resources.write) : false,
      },
    })
  }
  for (const slug of spawnMemberSlugs.value) {
    if (seen.has(slug)) continue
    const agent = agents.value.find((item) => agentSlugOf(item) === slug)
    rows.push({
      agentRef: slug,
      name: agent ? store.agentLocalizedName(agent, locale.value) : slug,
      layer: agent ? layerDisplay(agent.layer) : '—',
      scope: agent ? defaultMemberScope(agent) : { read: true, write: false },
    })
  }
  return rows
})
const spawnMemberOptions = computed(() => agents.value
  .filter((agent) => agent.enabled !== false && (!agent.status || agent.status === 'published'))
  .filter((agent) => agent.id !== leaderGraphNode.value?.agent_id)
  .filter((agent) => !spawnMembers.value.some((member) => member.agentRef === agentSlugOf(agent)))
  .map((agent) => ({
    value: agent.id,
    label: `${store.agentLocalizedName(agent, locale.value)} (${layerDisplay(agent.layer)})`,
  })))

function resourcesFor(scope: MemberScope): Record<string, string[]> {
  const resources: Record<string, string[]> = {}
  if (scope.read) resources.read = ['']
  if (scope.write) resources.write = ['']
  return resources
}

function withSpawnTarget(
  current: Record<string, unknown> | null | undefined,
  slug: string,
  add: boolean,
): Record<string, unknown> | null {
  const next: Record<string, unknown> = { ...(current ?? {}) }
  for (const key of ['agent_types', 'allowed_dispatch_targets']) {
    const list = Array.isArray(next[key])
      ? (next[key] as unknown[]).filter((v): v is string => typeof v === 'string')
      : []
    const updated = add
      ? (list.includes(slug) ? list : [...list, slug])
      : list.filter((v) => v !== slug)
    if (updated.length > 0) next[key] = updated
    else delete next[key]
  }
  return Object.keys(next).length > 0 ? next : null
}

function toggleSpawnScope(agentRef: string, key: 'read' | 'write') {
  if (selectedModeReadOnly.value || !parsedBindingsList.value) return
  const member = spawnMembers.value.find((row) => row.agentRef === agentRef)
  if (!member) return
  const scope = { ...member.scope, [key]: !member.scope[key] }
  const nextBindings = parsedBindingsList.value.map((binding) =>
    binding.node_key || binding.agent_ref !== agentRef
      ? binding
      : { ...binding, envelope: { ...(binding.envelope ?? {}), resources: resourcesFor(scope) } },
  )
  store.commitDraft((current) => ({
    ...current,
    bindingsDraft: JSON.stringify(nextBindings, null, 2),
  }))
}

async function addSpawnMember() {
  if (selectedModeReadOnly.value || !parsedBindingsList.value || !spawnMemberToAdd.value) return
  await store.ensureAgentDetails([spawnMemberToAdd.value])
  const agent = agents.value.find((item) => item.id === spawnMemberToAdd.value)
  if (!agent || spawnMembers.value.length >= MAX_SPAWN_MEMBERS) return
  const slug = agentSlugOf(agent)
  if (parsedBindingsList.value.some((binding) => !binding.node_key && binding.agent_ref === slug)) return
  const nextBindings: AgentModeBindingDto[] = [
    ...parsedBindingsList.value,
    {
      agent_ref: slug,
      envelope: { resources: resourcesFor(defaultMemberScope(agent)) },
      includes_core_reserved: false,
    },
  ]
  store.commitDraft((current) => ({
    ...current,
    bindingsDraft: JSON.stringify(nextBindings, null, 2),
    nodes: current.nodes.map((node) =>
      node.id === leaderGraphNode.value?.id
        ? { ...node, relationship: withSpawnTarget(node.relationship, slug, true) }
        : node,
    ),
  }))
  spawnMemberToAdd.value = ''
}

function removeSpawnMember(agentRef: string) {
  if (selectedModeReadOnly.value || !parsedBindingsList.value) return
  const nextBindings = parsedBindingsList.value.filter((binding) => binding.node_key || binding.agent_ref !== agentRef)
  store.commitDraft((current) => ({
    ...current,
    bindingsDraft: JSON.stringify(nextBindings, null, 2),
    nodes: current.nodes.map((node) =>
      node.id === leaderGraphNode.value?.id
        ? { ...node, relationship: withSpawnTarget(node.relationship, agentRef, false) }
        : node,
    ),
  }))
}

// ── Global Keyboard Shortcuts ────────────────────────────────────────────────

function onPanelKeydown(event: KeyboardEvent) {
  if (activeView.value !== 'topology') return
  if (event.defaultPrevented) return
  const target = event.target as HTMLElement | null
  const inTextField = Boolean(target && (/^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable))
  if (inTextField) {
    if (event.key === 'Escape') target?.blur()
    return
  }

  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
    event.preventDefault()
    if (event.shiftKey) store.redo()
    else store.undo()
    return
  }
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y') {
    event.preventDefault()
    store.redo()
    return
  }
  if (!selectedModeReadOnly.value && (event.key === 'Delete' || event.key === 'Backspace')) {
    if (selectedNodeId.value) {
      event.preventDefault()
      void handleDeleteNode(selectedNodeId.value)
      return
    }
    if (selectedEdgeId.value) {
      event.preventDefault()
      handleDeleteEdge(selectedEdgeId.value)
      return
    }
  }
  if (event.key === 'Escape') {
    handleCloseInspectorPanel()
  }
}

// ── Lifecycle & External Prop Sync ───────────────────────────────────────────

function reload() {
  if (effectiveCanAccessBackend.value) {
    store.loadPoliciesAndRoutes()
    if (props.agents === undefined) void store.loadAgents({ force: true })
    void store.loadWorkspaceDefaults()
    if (selectedModeId.value) void store.selectMode(selectedModeId.value, { force: true })
    else void store.loadModes({ force: true })
  }
}

watch(() => props.preferredView, (preferred) => {
  if (preferred) {
    store.switchWorkspaceView(preferred, { userInitiated: true })
  }
}, { immediate: true })

watch(() => props.modes, (incoming) => {
  if (incoming?.length) {
    store.mergeExternalModes(incoming as AgentModeTopologyDto[])
    if (!store.selectedModeId.value && !creatingNew.value) {
      const first = store.sortedModes.value[0]
      if (first) void store.selectMode(first.id, { force: true, preserveWorkspaceView: Boolean(props.preferredView) })
    }
  }
}, { immediate: true })

watch(() => props.agents, (incoming) => {
  if (incoming !== undefined) store.mergeExternalAgents(incoming)
}, { immediate: true })

watch(effectiveCanAccessBackend, (ready) => {
  if (ready) {
    store.loadPoliciesAndRoutes()
    if (props.agents === undefined) void store.loadAgents()
    void store.loadWorkspaceDefaults()
    if (store.modes.value.length === 0) void store.loadModes()
    else if (!store.selectedModeId.value && !creatingNew.value) {
      const first = store.sortedModes.value[0]
      if (first) void store.selectMode(first.id, { force: true, preserveWorkspaceView: Boolean(props.preferredView) })
    }
  }
})

onMounted(() => {
  if (effectiveCanAccessBackend.value) {
    store.loadPoliciesAndRoutes()
    if (props.agents === undefined) void store.loadAgents()
    void store.loadWorkspaceDefaults()
    if (store.modes.value.length === 0) {
      void store.loadModes()
    } else if (!store.selectedModeId.value && !creatingNew.value) {
      const first = store.sortedModes.value[0]
      if (first) void store.selectMode(first.id, { force: true, preserveWorkspaceView: Boolean(props.preferredView) })
    } else if (store.selectedModeId.value && !store.selectedModeDetail.value) {
      void store.selectMode(store.selectedModeId.value, { force: true, preserveWorkspaceView: Boolean(props.preferredView) })
    }
  }
  window.addEventListener('keydown', onPanelKeydown)
})

onBeforeUnmount(() => {
  modeNavigationGeneration++
  window.removeEventListener('keydown', onPanelKeydown)
})

defineExpose({
  reload,
  loadModes: store.loadModes,
})
</script>

<template>
  <section
    class="center-resource-section teams-modes-merged agent-modes-panel"
    data-testid="teams-modes-merged"
    @keydown="onPanelKeydown"
  >
    <div class="tm-workbench" data-testid="agent-teams">
      <!-- Shared Left Mode / Team Rail -->
      <aside class="tm-rail">
        <div class="tm-rail-head">
          <div class="tm-search-box">
            <Search :size="14" />
            <UiInput v-model="query" :placeholder="t('settings.teamSearch')" />
          </div>
          <div class="tm-rail-create-row">
            <UiButton
              size="sm"
              :variant="creatingNew ? 'default' : 'outline'"
              class="team-new-btn"
              data-testid="team-new-btn"
              :disabled="modeBusy || !effectiveCanAccessBackend"
              @click="creatingNew ? (creatingNew = false) : openTemplatePicker()"
            >
              <Plus v-if="!creatingNew" :size="14" />
              <span>{{ creatingNew ? t('settings.teamBackToEditor') : t('settings.teamNew') }}</span>
            </UiButton>
            <UiButton
              size="sm"
              variant="ghost"
              :title="t('agentCenter.createMode')"
              :aria-label="t('agentCenter.createMode')"
              data-testid="mode-create-blank-btn"
              :disabled="modeBusy || !effectiveCanAccessBackend"
              @click="handleCreateBlankMode"
            >
              <GitBranch :size="14" />
            </UiButton>
          </div>
        </div>

        <div class="ac-mode-tabs tm-rail-list" role="tablist" :aria-label="t('settings.agentTeamsAndModes')">
          <button
            v-for="mode in filteredModes"
            :key="mode.id"
            role="tab"
            type="button"
            :aria-selected="!creatingNew && selectedModeId === mode.id"
            :class="['tm-rail-row', 'ac-mode-row', { active: !creatingNew && selectedModeId === mode.id }]"
            @click="selectModeRow(mode.id)"
          >
            <component :is="modeIcon(mode.slug)" :size="14" class="tm-mode-icon" />
            <span class="tm-mode-meta" :title="store.modeLocalizedDescription(mode, locale) || undefined">
              <strong>{{ store.modeLocalizedName(mode, locale) }}</strong>
              <small>{{ store.modeLocalizedDescription(mode, locale) || modeStatusLabel(mode) }}</small>
            </span>
            <span class="tm-mode-badges">
              <UiBadge v-if="defaultModeId === mode.id" variant="secondary">{{ t('settings.teamDefaultBadge') }}</UiBadge>
              <UiBadge :variant="readinessVariant(mode.status)">
                {{ modeStatusLabel(mode) }}
              </UiBadge>
            </span>
          </button>
          <p v-if="!loading && !store.modesLoading.value && filteredModes.length === 0" class="quiet tm-rail-empty">
            {{ t('settings.teamEmptyList') }}
          </p>
        </div>
      </aside>

      <!-- Shared Right Stage -->
      <main class="tm-stage">
        <!-- Stage Header with Segmented Switch [ 编队视图 | 拓扑画布 ] -->
        <header v-if="!creatingNew && selectedModeId" class="tm-stage-head">
          <div class="tm-stage-title">
            <div class="tm-stage-title-row">
              <h3>{{ selectedMode ? store.modeLocalizedName(selectedMode, locale) : (detail?.display_name ?? '—') }}</h3>
              <UiBadge variant="outline">{{ modeNodes.length }} · {{ modeEdges.length }}</UiBadge>
              <UiBadge v-if="store.isDirty.value && !selectedModeReadOnly" variant="outline" class="tm-dirty-badge">
                {{ t('settings.modeUnsaved') }}
              </UiBadge>
              <UiBadge v-if="isDefaultMode" variant="secondary">{{ t('settings.teamDefaultCurrent') }}</UiBadge>
            </div>
            <p v-if="selectedModeReadOnly">
              {{ isPackManaged ? t('settings.teamReadOnlyHint') : t('agentCenter.publishedReadOnlyHint') }}
            </p>
          </div>

          <div
            class="tm-view-segmented"
            role="tablist"
            data-testid="mode-view-switch"
            :aria-label="t('settings.agentTeamsAndModes')"
          >
            <button
              type="button"
              role="tab"
              data-testid="mode-view-team"
              :aria-selected="activeView === 'team'"
              :class="{ active: activeView === 'team' }"
              @click="setView('team')"
            >
              <LayoutGrid :size="13" />
              <span>{{ t('settings.modeViewTeam') }}</span>
            </button>
            <button
              type="button"
              role="tab"
              data-testid="mode-view-topology"
              :aria-selected="activeView === 'topology'"
              :class="{ active: activeView === 'topology' }"
              @click="setView('topology')"
            >
              <Workflow :size="13" />
              <span>{{ t('settings.modeViewTopology') }}</span>
            </button>
          </div>

          <div class="tm-stage-actions">
            <UiButton
              v-if="canSetDefault"
              size="sm"
              variant="outline"
              :disabled="settingDefault || !effectiveCanAccessBackend || isDefaultMode"
              @click="setAsDefault"
            >
              {{ isDefaultMode ? t('settings.teamDefaultCurrent') : t('settings.teamSetDefault') }}
            </UiButton>
            <UiButton
              size="sm"
              :variant="selectedModeReadOnly ? 'default' : 'outline'"
              :disabled="modeBusy || !effectiveCanAccessBackend"
              @click="handleCloneMode"
            >
              <Copy :size="14" />
              <span>{{ t('settings.teamClone') }}</span>
            </UiButton>
            <template v-if="!selectedModeReadOnly">
              <UiButton
                size="sm"
                variant="outline"
                :disabled="saveDraftDisabled"
                @click="handleSaveDraft"
              >
                {{ t('settings.teamSaveDraft') }}
              </UiButton>
              <UiButton
                size="sm"
                :disabled="publishActionDisabled"
                :title="publishBlocked && activeView === 'topology' ? t('settings.modePublishBlocked') : undefined"
                @click="handlePublish"
              >
                {{ t('settings.teamPublish') }}
              </UiButton>
            </template>
            <template v-if="activeView === 'topology'">
              <UiButton
                size="sm"
                variant="ghost"
                :disabled="!store.canUndo.value"
                :title="t('settings.undoGraph')"
                @click="store.undo"
              >
                <Undo2 :size="14" />
              </UiButton>
              <UiButton
                size="sm"
                variant="ghost"
                :disabled="!store.canRedo.value"
                :title="t('settings.redoGraph')"
                @click="store.redo"
              >
                <Redo2 :size="14" />
              </UiButton>
              <UiButton
                size="sm"
                variant="outline"
                @click="toggleVersionsPanel"
              >
                <History :size="14" />
                <span>{{ t('agentCenter.versionHistory') }}</span>
              </UiButton>
            </template>
            <UiButton
              v-if="!isPackManaged"
              size="sm"
              variant="ghost"
              class="tm-delete-btn"
              :disabled="modeBusy || !effectiveCanAccessBackend"
              :title="t('settings.teamDelete')"
              @click="handleDeleteMode"
            >
              <Trash2 :size="14" />
            </UiButton>
          </div>
        </header>

        <!-- 1. Welcome / Template Picker -->
        <div v-if="creatingNew || (!selectedModeId && !detailLoading)" class="tm-welcome">
          <div class="tm-welcome-head">
            <div>
              <h3>{{ t('settings.teamWelcomeTitle') }}</h3>
              <p>{{ t('settings.teamWelcomeHint') }}</p>
            </div>
            <UiButton v-if="creatingNew && selectedModeId" variant="ghost" size="sm" @click="creatingNew = false">
              {{ t('common.cancel') }}
            </UiButton>
          </div>
          <div class="tm-template-grid">
            <button
              v-for="template in templates"
              :key="template.key"
              type="button"
              class="tm-template-card"
              :disabled="modeBusy || !effectiveCanAccessBackend"
              @click="createFromTemplate(template)"
            >
              <strong>{{ t(template.labelKey) }}</strong>
              <span>{{ t(template.hintKey) }}</span>
            </button>
          </div>
        </div>

        <!-- 2. Loading / Error -->
        <div v-else-if="detailLoading" class="tm-state">
          <RefreshCw :size="16" class="spin" />
          <span>{{ t('settings.refreshing') }}</span>
        </div>

        <div v-else-if="detailFailure" class="tm-state error" role="alert">
          <p>{{ detailFailure }}</p>
          <UiButton variant="outline" size="sm" @click="selectedModeId && store.selectMode(selectedModeId, { force: true })">
            {{ t('settings.retry') }}
          </UiButton>
        </div>

        <!-- 3. Squad View (编队视图) -->
        <div v-else-if="activeView === 'team'" class="tm-team-pane">
          <!-- 3A. Advanced 2-layer / branching mode: Read-only Mini Topology + Role Summary + Editable default_options + Refine CTA -->
          <div v-if="!canEditShape" class="tm-complex-card" data-testid="team-complex-overview">
            <div class="tm-complex-banner" role="status">
              <div class="tm-complex-banner-copy">
                <strong>{{ t('settings.teamComplexPreviewTitle') }}</strong>
                <p>{{ t('settings.teamComplexPreviewHint') }}</p>
                <small class="quiet">{{ t('settings.teamComplexShape') }}</small>
              </div>
              <UiButton
                size="sm"
                data-testid="refine-in-topology-btn"
                @click="setView('topology')"
              >
                <Workflow :size="14" />
                <span>{{ t('settings.teamRefineInTopology') }}</span>
              </UiButton>
            </div>

            <div class="tm-mini-preview" data-testid="team-mini-topology-preview">
              <ModeGraphCanvas
                :nodes="modeNodes"
                :edges="modeEdges"
                :agents="agents"
                :providers="store.providers.value"
                :readonly="true"
                :graph-key="`${selectedModeId}:mini`"
              />
            </div>

            <div class="tm-role-summary" data-testid="team-role-summary">
              <h4>{{ t('settings.teamRoleSummary') }}</h4>
              <div class="tm-role-grid">
                <div v-for="role in complexRoleRows" :key="role.key" class="tm-role-card">
                  <div class="tm-role-card-head">
                    <Crown v-if="role.isConversation" :size="13" class="tm-crown" />
                    <strong>{{ role.name }}</strong>
                    <UiBadge variant="outline">{{ layerDisplay(role.layer) }}</UiBadge>
                    <span
                      v-if="role.runtimeChannel"
                      :class="['runtime-channel-badge', `channel-${role.runtimeChannel}`]"
                    >
                      {{ runtimeChannelBadgeLabel(role.runtimeChannel) }}
                    </span>
                    <UiBadge variant="secondary">{{ role.strategyBadge }}</UiBadge>
                  </div>
                  <p class="quiet">{{ role.duty || '—' }}</p>
                </div>
              </div>
            </div>

            <div class="team-field" data-testid="team-default-options">
              <span>{{ t('settings.modeDefaultOptions') }}</span>
              <small class="team-hint">{{ t('settings.modeDefaultOptionsHint') }}</small>
              <div class="team-options-row" role="group" :aria-label="t('settings.modeDefaultOptions')">
                <button
                  v-for="opt in DEFAULT_OPTION_ITEMS"
                  :key="opt.key"
                  type="button"
                  role="switch"
                  :data-testid="`team-default-opt-${opt.key}`"
                  :aria-checked="isTeamOptionEnabled(opt.key)"
                  :class="{ active: isTeamOptionEnabled(opt.key) }"
                  :disabled="selectedModeReadOnly"
                  @click="toggleTeamOption(opt.key)"
                >
                  {{ t(opt.labelKey) }}
                </button>
              </div>
            </div>
          </div>

          <!-- 3B. Simple Editable Mode (Solo / Squad / Linear Pipeline) -->
          <div v-else class="team-form">
            <div class="team-summary">
              <UiBadge variant="outline">{{ selection.style }}</UiBadge>
              <span v-if="selectedLeader">{{ t('settings.teamLeader') }} · {{ store.agentLocalizedName(selectedLeader, locale) }}</span>
              <span>{{ t('settings.teamMemberCount', { count: memberRows.length, max: MAX_TEAM_MEMBERS }) }}</span>
            </div>

            <div v-if="teamErrors.length > 0 && !selectedModeReadOnly" class="team-errors" role="alert">
              <p v-for="error in teamErrors" :key="error">{{ error }}</p>
            </div>

            <label class="team-field">
              <span>{{ t('settings.teamName') }}</span>
              <UiInput v-model="editingName" :disabled="selectedModeReadOnly" />
            </label>
            <label class="team-field">
              <span>{{ t('settings.teamDescription') }}</span>
              <textarea v-model="editingDescription" class="team-textarea" rows="2" :disabled="selectedModeReadOnly" />
            </label>
            <label class="team-field">
              <span>{{ t('settings.teamInstructions') }}</span>
              <textarea
                v-model="editingInstructions"
                class="team-textarea"
                rows="4"
                maxlength="4000"
                :disabled="selectedModeReadOnly"
                :placeholder="t('settings.teamInstructionsPlaceholder')"
              />
              <small class="team-hint">{{ editingInstructions.length }} / 4000</small>
            </label>

            <div class="team-field" data-testid="team-default-options">
              <span>{{ t('settings.modeDefaultOptions') }}</span>
              <small class="team-hint">{{ t('settings.modeDefaultOptionsHint') }}</small>
              <div class="team-options-row" role="group" :aria-label="t('settings.modeDefaultOptions')">
                <button
                  v-for="opt in DEFAULT_OPTION_ITEMS"
                  :key="opt.key"
                  type="button"
                  role="switch"
                  :data-testid="`team-default-opt-${opt.key}`"
                  :aria-checked="isTeamOptionEnabled(opt.key)"
                  :class="{ active: isTeamOptionEnabled(opt.key) }"
                  :disabled="selectedModeReadOnly"
                  @click="toggleTeamOption(opt.key)"
                >
                  {{ t(opt.labelKey) }}
                </button>
              </div>
            </div>

            <div class="team-field">
              <span>{{ t('settings.teamStyle') }}</span>
              <div class="team-style-row" role="radiogroup" :aria-label="t('settings.teamStyle')">
                <button
                  v-for="template in templates"
                  :key="template.key"
                  type="button"
                  role="radio"
                  :aria-checked="selection.style === template.key"
                  :class="{ active: selection.style === template.key }"
                  :disabled="selectedModeReadOnly"
                  @click="setStyle(template.key)"
                >
                  <strong>{{ t(template.labelKey) }}</strong>
                  <span>{{ t(template.hintKey) }}</span>
                </button>
              </div>
            </div>

            <label class="team-field">
              <span>{{ t('settings.teamLeader') }}</span>
              <UiSelectField
                :model-value="selection.leaderId"
                :aria-label="t('settings.teamLeader')"
                :options="leaderOptions"
                :disabled="selectedModeReadOnly"
                @update:model-value="setLeaderId"
              />
            </label>

            <div v-if="selection.style !== 'solo'" class="team-field">
              <span>{{ t('settings.teamMembers') }} · {{ memberRows.length }} / {{ MAX_TEAM_MEMBERS }}</span>
              <div v-if="memberRows.length" class="team-member-list" role="list">
                <div v-for="(agent, index) in memberRows" :key="agent.id" class="team-member-row" role="listitem">
                  <div class="team-member-main">
                    <strong>{{ store.agentLocalizedName(agent, locale) }}</strong>
                    <span>{{ store.agentLocalizedDescription(agent, locale) || '—' }}</span>
                  </div>
                  <span
                    v-if="runtimeChannelForAgent(agent)"
                    :class="['runtime-channel-badge', `channel-${runtimeChannelForAgent(agent)}`]"
                  >
                    {{ runtimeChannelBadgeLabel(runtimeChannelForAgent(agent)!) }}
                  </span>
                  <em class="team-member-layer">{{ layerDisplay(agent.layer) }}</em>
                  <div class="team-member-scope" role="group" :aria-label="t('settings.teamMemberScope')">
                    <button
                      type="button"
                      :class="{ active: scopeOf(agent.id).read }"
                      :aria-pressed="scopeOf(agent.id).read"
                      :disabled="selectedModeReadOnly"
                      @click="toggleScope(agent.id, 'read')"
                    >{{ t('settings.teamMemberRead') }}</button>
                    <button
                      type="button"
                      :class="{ active: scopeOf(agent.id).write }"
                      :aria-pressed="scopeOf(agent.id).write"
                      :disabled="selectedModeReadOnly"
                      @click="toggleScope(agent.id, 'write')"
                    >{{ t('settings.teamMemberWrite') }}</button>
                  </div>
                  <div class="team-member-actions">
                    <button type="button" :disabled="selectedModeReadOnly || index === 0" :aria-label="t('settings.teamMoveUp')" @click="moveMember(index, -1)">
                      <ArrowUp :size="13" />
                    </button>
                    <button type="button" :disabled="selectedModeReadOnly || index === memberRows.length - 1" :aria-label="t('settings.teamMoveDown')" @click="moveMember(index, 1)">
                      <ArrowDown :size="13" />
                    </button>
                    <button type="button" :disabled="selectedModeReadOnly" :aria-label="t('settings.teamRemoveMember')" @click="toggleMember(agent.id)">
                      <X :size="13" />
                    </button>
                  </div>
                </div>
              </div>
              <p v-else class="team-hint">{{ t('settings.teamNoMembers') }}</p>
              <div v-if="availableMembers.length" class="team-member-pool">
                <button
                  v-for="agent in availableMembers"
                  :key="agent.id"
                  type="button"
                  :disabled="selectedModeReadOnly || modeBusy || memberLimitReached"
                  @click="toggleMember(agent.id)"
                >
                  <Plus :size="12" />
                  <span>{{ store.agentLocalizedName(agent, locale) }}</span>
                  <em>{{ layerDisplay(agent.layer) }}</em>
                </button>
              </div>
              <p v-if="memberLimitReached" class="team-hint">{{ t('settings.teamMembersLimit', { max: MAX_TEAM_MEMBERS }) }}</p>
              <p v-else-if="availableMembers.length === 0 && selection.style === 'pipeline'" class="team-hint">{{ t('settings.teamNoExecutionAgents') }}</p>
              <p v-if="unavailableAgents.length > 0" class="team-hint">
                {{ t('settings.teamUnavailableAgents', { names: unavailableAgents.map((agent) => store.agentLocalizedName(agent, locale)).join(', ') }) }}
              </p>
            </div>
          </div>
        </div>

        <!-- 4. Topology Canvas View (拓扑画布 + Right Docked Inspector) -->
        <div v-else class="tm-topology-pane">
          <div class="tm-canvas-stage">
            <div class="tm-canvas-host">
              <ModeGraphCanvas
                ref="canvasRef"
                :nodes="modeNodes"
                :edges="modeEdges"
                :agents="agents"
                :providers="store.providers.value"
                :readonly="selectedModeReadOnly"
                :selected-node-id="selectedNodeId"
                :selected-edge-id="selectedEdgeId"
                :graph-key="selectedModeId"
                @select-node="handleSelectNode"
                @select-edge="handleSelectEdge"
                @node-moved="handleNodeMoved"
                @connect="handleConnect"
                @update-edge="handleUpdateEdgeEndpoint"
                @delete-node="handleDeleteNode"
                @delete-edge="handleDeleteEdge"
                @clone-node="handleCloneNode"
                @set-conversation="handleSetConversation"
                @add-node="openAddNode($event.lane)"
                @auto-layout="autoLayoutGraph"
                @undo="store.undo"
                @redo="store.redo"
              />
            </div>

            <div v-if="showSpawnBelt" class="ac-spawn-belt">
              <div class="ac-spawn-belt-head">
                <strong>{{ t('settings.spawnMembersTitle') }}</strong>
                <span>{{ spawnMembers.length }} / {{ MAX_SPAWN_MEMBERS }}</span>
                <small :title="t('settings.spawnMembersHint')">{{ t('settings.spawnMembersHint') }}</small>
              </div>
              <div v-if="spawnMembers.length > 0" class="ac-spawn-belt-list">
                <div v-for="member in spawnMembers" :key="member.agentRef" class="ac-spawn-member">
                  <span class="ac-spawn-member-name" :title="member.agentRef">{{ member.name }}</span>
                  <small>{{ member.layer }}</small>
                  <div class="ac-spawn-scope">
                    <button type="button" :class="{ active: member.scope.read }" :disabled="selectedModeReadOnly" @click="toggleSpawnScope(member.agentRef, 'read')">
                      {{ t('settings.teamMemberRead') }}
                    </button>
                    <button type="button" :class="{ active: member.scope.write }" :disabled="selectedModeReadOnly" @click="toggleSpawnScope(member.agentRef, 'write')">
                      {{ t('settings.teamMemberWrite') }}
                    </button>
                  </div>
                  <button type="button" class="ac-spawn-remove" :disabled="selectedModeReadOnly" :aria-label="t('settings.teamRemoveMember')" @click="removeSpawnMember(member.agentRef)">
                    <Trash2 :size="12" />
                  </button>
                </div>
              </div>
              <p v-else class="quiet">{{ t('settings.spawnMembersEmpty') }}</p>
              <div v-if="!selectedModeReadOnly && spawnMembers.length < MAX_SPAWN_MEMBERS" class="ac-spawn-add">
                <UiSelectField v-model="spawnMemberToAdd" :options="spawnMemberOptions" />
                <UiButton size="sm" variant="outline" :disabled="!spawnMemberToAdd" @click="addSpawnMember">
                  <Plus :size="13" />{{ t('settings.spawnMemberAdd') }}
                </UiButton>
              </div>
              <p v-else-if="spawnMembers.length >= MAX_SPAWN_MEMBERS" class="quiet">{{ t('settings.spawnMemberLimit') }}</p>
            </div>
          </div>

          <ModeInspectorDrawer
            class="tm-inspector-col"
            :panel="panelMode"
            :readonly="selectedModeReadOnly"
            :selected-mode-id="selectedModeId"
            :selected-mode="selectedMode"
            :draft="draft"
            :selected-node="selectedNode"
            :selected-edge="selectedEdge"
            :add-node-lane="addNodeLane"
            :agents="agents"
            :providers="store.providers.value"
            :routes="store.routes.value"
            :runtime-policy-definitions="store.runtimePolicyDefinitions.value"
            :policy-settings-error="store.parsedPolicySettings.value.error"
            :bindings-error="store.parsedBindings.value.error"
            :mode-versions="store.modeVersions.value"
            :mode-versions-loading="store.modeVersionsLoading.value"
            :mode-busy="modeBusy"
            @close-panel="handleCloseInspectorPanel"
            @update-overview="handleUpdateOverview"
            @set-policy-kind="store.setRuntimePolicyKind"
            @toggle-policy-feature="store.togglePolicyFeature"
            @update-node="handleUpdateNode"
            @delete-node="handleDeleteNode"
            @update-edge="handleUpdateEdge"
            @delete-edge="handleDeleteEdge"
            @confirm-add-node="confirmAddNode"
            @refresh-versions="store.loadModeVersions(selectedModeId)"
            @restore-version="handleRestoreVersion"
          />
        </div>
      </main>
    </div>
  </section>
</template>

<style scoped>
.teams-modes-merged {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.tm-workbench {
  display: grid;
  grid-template-columns: 248px minmax(0, 1fr);
  gap: 12px;
  min-height: 540px;
  align-items: start;
}

.tm-rail {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
  max-height: calc(100vh - 210px);
  min-height: 480px;
  padding: 10px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  overflow: hidden;
}

.tm-rail-head {
  display: grid;
  gap: 6px;
}

.tm-search-box {
  position: relative;
  display: flex;
  align-items: center;
}

.tm-search-box > svg {
  position: absolute;
  left: 10px;
  color: var(--text-muted);
  pointer-events: none;
}

.tm-search-box :deep(input) {
  padding-left: 30px;
}

.tm-rail-create-row {
  display: flex;
  gap: 6px;
}

.team-new-btn {
  flex: 1;
}

.tm-rail-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
  overflow-y: auto;
  min-height: 0;
  flex: 1;
}

.tm-rail-row {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px 10px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
  transition: background-color 120ms ease, border-color 120ms ease;
}

.tm-rail-row:hover {
  background: var(--surface-hover);
}

.tm-rail-row.active {
  background: var(--surface-selected);
  border-color: color-mix(in srgb, var(--accent-primary) 45%, var(--border-muted));
}

.tm-mode-icon {
  margin-top: 2px;
  flex-shrink: 0;
  color: var(--text-secondary);
}

.tm-mode-meta {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.tm-mode-meta strong {
  font-size: 13px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tm-mode-meta small {
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tm-mode-badges {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 3px;
  flex-shrink: 0;
}

.tm-rail-empty {
  padding: 16px 8px;
  text-align: center;
}

.tm-stage {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
}

.tm-stage-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.tm-stage-title {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.tm-stage-title-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.tm-stage-title-row h3 {
  margin: 0;
  font-size: 15px;
}

.tm-stage-title p {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.tm-dirty-badge {
  border-color: color-mix(in srgb, var(--accent-warning, #d97706) 45%, var(--border-muted));
  color: var(--accent-warning, #d97706);
}

.tm-view-segmented {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 2px;
  padding: 3px;
  border-radius: var(--radius-control-md, 8px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.tm-view-segmented button {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 5px;
  padding: 5px 11px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease;
}

.tm-view-segmented button:focus-visible,
.tm-rail-row:focus-visible,
.tm-template-card:focus-visible,
.team-style-row button:focus-visible,
.team-options-row button:focus-visible,
.team-member-scope button:focus-visible,
.team-member-actions button:focus-visible,
.team-member-pool button:focus-visible,
.ac-spawn-scope button:focus-visible,
.ac-spawn-remove:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.tm-view-segmented button.active {
  background: var(--surface-raised);
  color: var(--text-primary);
  box-shadow: 0 1px 3px color-mix(in srgb, #000 12%, transparent);
}

.tm-stage-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}

.tm-delete-btn {
  color: var(--accent-danger, #ef4444);
}

.tm-welcome {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 16px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.tm-welcome-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.tm-welcome-head h3 { margin: 0 0 4px; }
.tm-welcome-head p { margin: 0; color: var(--text-secondary); font-size: 12px; }

.tm-template-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 10px;
}

.tm-template-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 14px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-section);
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
  transition: border-color 140ms ease, background-color 140ms ease;
}

.tm-template-card:hover:not(:disabled) {
  border-color: var(--accent-primary);
}

.tm-template-card span {
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.45;
}

.tm-state {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  min-height: 280px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
}

.tm-state.error {
  flex-direction: column;
  color: var(--accent-danger, #ef4444);
}

.tm-team-pane {
  padding: 14px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

/* Advanced 2-layer / branching overview inside Squad View */
.tm-complex-card {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.tm-complex-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 12px;
  padding: 12px 14px;
  border-radius: 9px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 35%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 6%, var(--surface-section));
}

.tm-complex-banner-copy {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-width: 680px;
}

.tm-complex-banner-copy strong {
  font-size: 13px;
  color: var(--text-primary);
}

.tm-complex-banner-copy p {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.tm-mini-preview {
  height: 270px;
  border-radius: 10px;
  overflow: hidden;
  border: 1px solid var(--border-muted);
}

.tm-role-summary {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.tm-role-summary h4 {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.tm-role-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 8px;
}

.tm-role-card {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 10px 12px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.tm-role-card-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}

.tm-role-card-head strong {
  font-size: 13px;
}

.tm-crown {
  color: var(--accent-warning, #d97706);
}

.tm-role-card p {
  margin: 0;
  line-height: 1.4;
}

/* Simple Team Form */
.team-summary {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-section);
  font-size: 12px;
  color: var(--text-secondary);
}

.team-errors {
  display: grid;
  gap: 4px;
  padding: 8px 10px;
  border: 1px solid color-mix(in srgb, var(--accent-danger) 30%, var(--border-muted));
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent-danger) 6%, var(--surface-section));
}

.team-errors p {
  margin: 0;
  font-size: 12px;
  color: var(--accent-danger);
}

.team-form {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.team-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 12px;
  color: var(--text-secondary);
}

.team-textarea {
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--border-input);
  border-radius: var(--radius-control, 6px);
  background: var(--surface-input);
  color: var(--text-primary);
  font: inherit;
  resize: vertical;
}

.team-textarea:focus-visible {
  outline: none;
  border-color: var(--border-input-focus);
  box-shadow: var(--focus-ring, 0 0 0 2px color-mix(in srgb, var(--accent-primary) 28%, transparent));
}

.team-hint {
  margin: 0;
  color: var(--text-muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.team-style-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 8px;
}

.team-style-row button {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: 9px;
  background: var(--surface-section);
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
  transition: border-color 140ms ease, background-color 140ms ease;
}

.team-style-row button.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
}

.team-style-row button span {
  color: var(--text-secondary);
  font-size: 11px;
  line-height: 1.4;
}

.team-options-row {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.team-options-row button {
  padding: 4px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
  transition: border-color 140ms ease, background-color 140ms ease, color 140ms ease;
}

.team-options-row button.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
  color: var(--text-primary);
  font-weight: 500;
}

.team-options-row button:disabled {
  opacity: 0.5;
  cursor: default;
}

.team-member-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.team-member-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-section);
}

.team-member-main {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.team-member-main strong,
.team-member-main span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.team-member-main strong { font-size: 13px; color: var(--text-primary); }
.team-member-main span { color: var(--text-secondary); font-size: 11px; }

.team-member-layer {
  font-style: normal;
  font-size: 10px;
  color: var(--text-muted);
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--surface-raised);
}

.team-member-scope {
  display: flex;
  gap: 4px;
}

.team-member-scope button {
  padding: 2px 7px;
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  background: transparent;
  color: var(--text-muted);
  font-size: 10px;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
}

.team-member-scope button.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
  color: var(--text-primary);
}

.team-member-scope button:disabled { opacity: 0.5; cursor: default; }

.team-member-actions {
  display: flex;
  gap: 2px;
}

.team-member-actions button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.team-member-actions button:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.team-member-actions button:disabled { opacity: 0.35; cursor: default; }

.team-member-pool {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 6px;
}

.team-member-pool button {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 4px;
  padding: 4px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  cursor: pointer;
}

.team-member-pool button em {
  font-style: normal;
  color: var(--text-muted);
  font-size: 9px;
}

.team-member-pool button:hover:not(:disabled) {
  border-color: var(--accent-primary);
  color: var(--text-primary);
}

.team-member-pool button:disabled { opacity: 0.4; cursor: default; }

/* Topology Canvas Workbench */
.tm-topology-pane {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 320px;
  gap: 12px;
  height: calc(100vh - 260px);
  min-height: 520px;
  max-height: 820px;
}

.tm-canvas-stage {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
  min-height: 0;
}

.tm-canvas-host {
  flex: 1;
  min-height: 0;
  display: flex;
}

.tm-canvas-host > :deep(*) {
  flex: 1;
  min-height: 0;
}

.tm-inspector-col {
  min-height: 0;
}

.ac-spawn-belt {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 12px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.ac-spawn-belt-head {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.ac-spawn-belt-head small {
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ac-spawn-belt-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.ac-spawn-member {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  border-radius: var(--radius-control-md, 8px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  font-size: 12px;
}

.ac-spawn-member small {
  color: var(--text-muted);
  font-size: 10px;
}

.ac-spawn-scope {
  display: inline-flex;
  gap: 3px;
}

.ac-spawn-scope button {
  padding: 1px 6px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-muted);
  font-size: 10px;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
}

.ac-spawn-scope button.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
  color: var(--text-primary);
}

.ac-spawn-remove {
  border: 0;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  padding: 2px;
}

.ac-spawn-add {
  display: flex;
  align-items: center;
  gap: 6px;
}

.quiet {
  color: var(--text-muted);
  font-size: 11px;
}

@media (max-width: 1080px) {
  .tm-topology-pane {
    grid-template-columns: 1fr;
    height: auto;
    max-height: none;
  }
  .tm-canvas-host {
    min-height: 440px;
  }
}

@media (max-width: 820px) {
  .tm-workbench {
    grid-template-columns: 1fr;
  }
  .tm-rail {
    max-height: 240px;
    min-height: 0;
  }
}
</style>
