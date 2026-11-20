<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Copy, History, Plus, Redo2, Search, Trash2, Undo2 } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSelectField } from '@/components/ui'
import ModeGraphCanvas from '@/components/modeEditor/ModeGraphCanvas.vue'
import ModeInspectorDrawer, { type InspectorPanelMode } from '@/components/modeEditor/ModeInspectorDrawer.vue'
import { dagreLayoutMode, findFreeDagreSlot, type ModeLane } from '@/components/modeEditor/modeDagreLayout'
import { hasBlockingModeIssue, validateMode } from '@/components/canvas/modeValidation'
import { agentSlugOf, defaultMemberScope, type MemberScope } from '@/settings/agentCenter/teamShape'
import {
  normalizeLayer,
  useAgentModesStore,
} from '@/composables/useAgentModesStore'
import type { AgentModeBindingDto, AgentModeEdgeDto, AgentModeNodeDto, ModeVersionDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { modeIcon } from '@/lib/modePresentation'
import { consumeRequest, pendingModeId } from '@/lib/pageRequests'
import { useHostAccess } from '@/lib/hostAccess'

const { t, te, locale } = useI18n()
const { notify, confirm } = useNotifications()
const { canAccessBackend } = useHostAccess()
const store = useAgentModesStore()

const canvasRef = ref<InstanceType<typeof ModeGraphCanvas> | null>(null)
const modeQuery = ref('')
const selectedNodeId = ref<string | null>(null)
const selectedEdgeId = ref<string | null>(null)
const panelMode = ref<InspectorPanelMode>('overview')
const addNodeLane = ref<ModeLane>('operation')
const spawnMemberToAdd = ref('')
let modeNavigationGeneration = 0

const definitionStatusLabel = (status: string) =>
  (te(`agentCenter.filter.${status}`) ? t(`agentCenter.filter.${status}`) : status)

const selectedModeId = computed(() => store.selectedModeId.value)
const selectedMode = computed(() => store.selectedMode.value)
const selectedModeReadOnly = computed(() => store.selectedModeReadOnly.value)
const isPackManaged = computed(() => store.isModePackManaged(selectedMode.value))
const modeBusy = computed(() => store.modeBusy.value)
const draft = computed(() => store.draft.value)
const modeNodes = computed(() => store.draft.value.nodes)
const modeEdges = computed(() => store.draft.value.edges)
const agents = computed(() => store.agents.value)
const graphDirty = computed(() => store.modeDirty.value)

const filteredModes = computed(() => {
  const q = modeQuery.value.trim().toLowerCase()
  const sorted = store.sortedModes.value
  if (!q) return sorted
  return sorted.filter((m) =>
    `${store.modeLocalizedName(m, locale.value)} ${store.modeLocalizedDescription(m, locale.value)} ${m.slug ?? ''}`
      .toLowerCase()
      .includes(q),
  )
})

const selectedNode = computed<AgentModeNodeDto | null>(() =>
  selectedNodeId.value ? modeNodes.value.find((n) => n.id === selectedNodeId.value) ?? null : null,
)

const selectedEdge = computed<AgentModeEdgeDto | null>(() =>
  selectedEdgeId.value ? modeEdges.value.find((e) => e.id === selectedEdgeId.value) ?? null : null,
)

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

function readinessVariant(status?: string): 'default' | 'secondary' | 'outline' {
  if (status === 'published') return 'default'
  if (status === 'archived') return 'secondary'
  return 'outline'
}

function layerDisplayLabel(layer?: string): string {
  return normalizeLayer(layer) === 'execution'
    ? t('settings.agentLayerExecution')
    : t('settings.agentLayerPlanning')
}

function nodeKeyOf(node: AgentModeNodeDto): string {
  return node.node_key ?? node.id
}

function newGraphId(prefix: string): string {
  const random = globalThis.crypto?.randomUUID?.().slice(0, 8) ?? Math.random().toString(36).slice(2, 10)
  return `${prefix}-${random}`
}

async function selectModeRow(id: string) {
  const switched = await store.selectMode(id)
  if (switched) {
    selectedNodeId.value = null
    selectedEdgeId.value = null
    panelMode.value = 'overview'
  }
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
  modeQuery.value = ''
  if (!store.modes.value.some((mode) => mode.id === modeId)) {
    await store.loadModes({ force: true })
  }
  if (generation !== modeNavigationGeneration) return
  if (!store.modes.value.some((mode) => mode.id === modeId)) {
    notify.error(t('app.loadFailedMessage'), { title: t('agentCenter.loadModesFailed'), key: 'search-navigation' })
    return
  }
  await store.selectMode(modeId, { force: true })
  selectedNodeId.value = null
  selectedEdgeId.value = null
  panelMode.value = 'overview'
})

// ── Canvas & Inspector Actions ──────────────────────────────────────────────

function handleSelectNode(id: string | null) {
  selectedNodeId.value = id
  if (id) {
    selectedEdgeId.value = null
    panelMode.value = 'node'
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

async function handleCreateMode() {
  const created = await store.createMode()
  if (created) {
    selectedNodeId.value = null
    selectedEdgeId.value = null
    panelMode.value = 'overview'
  }
}

async function handleSaveDraft() {
  await store.saveModeDraft()
}

async function handlePublishMode() {
  if (publishBlocked.value) {
    notify.error(new Error(t('settings.modePublishBlocked')))
    return
  }
  await store.publishMode()
}

async function handleCloneMode() {
  if (!selectedModeId.value) return
  const created = await store.cloneMode(selectedModeId.value)
  if (created) {
    selectedNodeId.value = null
    selectedEdgeId.value = null
    panelMode.value = 'overview'
  }
}

async function handleDeleteMode() {
  if (!selectedModeId.value || isPackManaged.value) return
  const ok = await store.deleteOrArchiveMode(selectedModeId.value)
  if (ok) {
    selectedNodeId.value = null
    selectedEdgeId.value = null
    panelMode.value = 'overview'
  }
}

// ── Spawnable Members Belt (Team-shaped modes) ──────────────────────────────

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
      layer: agent ? layerDisplayLabel(agent.layer) : '—',
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
      layer: agent ? layerDisplayLabel(agent.layer) : '—',
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
    label: `${store.agentLocalizedName(agent, locale.value)} (${layerDisplayLabel(agent.layer)})`,
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

function addSpawnMember() {
  if (selectedModeReadOnly.value || !parsedBindingsList.value) return
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

// ── Global Keyboard Shortcuts (across graph & metadata/policy edits) ────────

function onPanelKeydown(event: KeyboardEvent) {
  if (event.defaultPrevented) return
  const target = event.target as HTMLElement | null
  const inTextField = Boolean(target && (/^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable))
  if (inTextField) {
    if (event.key === 'Escape') {
      target?.blur()
    }
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

onMounted(() => {
  if (canAccessBackend.value) {
    void store.loadAll()
  }
  window.addEventListener('keydown', onPanelKeydown)
})

watch(canAccessBackend, (ready) => {
  if (ready) {
    void store.loadAll()
  }
})

onBeforeUnmount(() => {
  modeNavigationGeneration++
  window.removeEventListener('keydown', onPanelKeydown)
})

defineExpose({ loadModes: store.loadModes })
</script>

<template>
  <section class="center-resource-section agent-modes-panel" @keydown="onPanelKeydown">
    <div class="modes-header-bar">
      <div class="modes-header-title">
        <h3>{{ t('agentCenter.tabs.topology') }}</h3>
        <p>{{ t('settings.agentModesHint') }}</p>
      </div>
      <div class="modes-header-actions">
        <UiBadge variant="outline">{{ modeNodes.length }} · {{ modeEdges.length }}</UiBadge>
        <UiBadge v-if="graphDirty && !selectedModeReadOnly" variant="outline" class="ac-mode-dirty">
          {{ t('settings.modeUnsaved') }}
        </UiBadge>
        <UiButton size="sm" :disabled="!canAccessBackend || modeBusy" @click="handleCreateMode">
          <Plus :size="14" /><span>{{ t('agentCenter.createMode') }}</span>
        </UiButton>
        <UiButton
          size="sm"
          variant="outline"
          :disabled="!selectedModeId || !canAccessBackend || modeBusy || selectedModeReadOnly"
          @click="handleSaveDraft"
        >
          {{ t('settings.saveDraftMode') }}
        </UiButton>
        <UiButton
          size="sm"
          :disabled="!selectedModeId || !canAccessBackend || modeBusy || selectedModeReadOnly || publishBlocked"
          :title="publishBlocked ? t('settings.modePublishBlocked') : undefined"
          @click="handlePublishMode"
        >
          {{ t('settings.publishModeAction') }}
        </UiButton>
        <UiButton
          size="sm"
          variant="ghost"
          :disabled="!selectedModeId || !store.canUndo.value"
          :title="t('settings.undoGraph')"
          @click="store.undo"
        >
          <Undo2 :size="14" />
        </UiButton>
        <UiButton
          size="sm"
          variant="ghost"
          :disabled="!selectedModeId || !store.canRedo.value"
          :title="t('settings.redoGraph')"
          @click="store.redo"
        >
          <Redo2 :size="14" />
        </UiButton>
        <UiButton
          size="sm"
          variant="outline"
          :disabled="!selectedModeId"
          @click="toggleVersionsPanel"
        >
          <History :size="14" /><span>{{ t('agentCenter.versionHistory') }}</span>
        </UiButton>
        <UiButton
          v-if="selectedModeId"
          size="sm"
          variant="outline"
          :disabled="!canAccessBackend || modeBusy"
          @click="handleCloneMode"
        >
          <Copy :size="14" /><span>{{ t('agentPack.cloneAction') }}</span>
        </UiButton>
        <UiButton
          v-if="selectedModeId && !isPackManaged"
          size="sm"
          variant="ghost"
          class="mode-delete-btn"
          :disabled="!canAccessBackend || modeBusy"
          :title="t('settings.modeDelete')"
          @click="handleDeleteMode"
        >
          <Trash2 :size="14" />
        </UiButton>
      </div>
    </div>

    <div v-if="selectedModeReadOnly && selectedMode" class="ac-mode-readonly-notice">
      <UiBadge variant="outline">{{ t('agentPack.managed') }}</UiBadge>
      <span>{{ isPackManaged ? t('agentPack.managedReadOnly') : t('agentCenter.publishedReadOnlyHint') }}</span>
    </div>

    <div class="modes-workbench">
      <!-- Left Mode Rail -->
      <aside class="modes-rail">
        <div class="model-provider-search">
          <Search :size="14" />
          <UiInput v-model="modeQuery" :placeholder="t('agentCenter.filter.searchModes')" />
        </div>
        <div class="ac-mode-tabs" role="tablist" :aria-label="t('settings.agentModes')">
          <button
            v-for="m in filteredModes"
            :key="m.id"
            role="tab"
            type="button"
            :aria-selected="selectedModeId === m.id"
            :class="{ active: selectedModeId === m.id }"
            class="ac-mode-row"
            @click="selectModeRow(m.id)"
          >
            <component :is="modeIcon(m.slug)" :size="14" class="ac-mode-icon" />
            <span class="ac-mode-meta" :title="store.modeLocalizedDescription(m, locale) || undefined">
              <strong>{{ store.modeLocalizedName(m, locale) }}</strong>
              <small>{{ store.modeLocalizedDescription(m, locale) || t('agentCenter.revisionShort', { revision: m.revision ?? '—' }) }}</small>
            </span>
            <UiBadge :variant="readinessVariant(m.status)">
              {{ m.status ? definitionStatusLabel(m.status) : t('common.draft') }}
            </UiBadge>
          </button>
          <div v-if="filteredModes.length === 0" class="quiet ac-mode-empty">{{ t('agentCenter.empty.noModes') }}</div>
        </div>
      </aside>

      <!-- Center Graph Canvas Stage -->
      <main class="modes-canvas-stage">
        <div class="modes-canvas-host">
          <ModeGraphCanvas
            ref="canvasRef"
            :nodes="modeNodes"
            :edges="modeEdges"
            :agents="agents"
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
      </main>

      <!-- Non-blocking Right Docked Inspector -->
      <ModeInspectorDrawer
        class="modes-inspector-col"
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
  </section>
</template>

<style scoped>
.agent-modes-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
}

.modes-header-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.modes-header-title h3 {
  margin: 0 0 2px;
}

.modes-header-title p {
  margin: 0;
  color: var(--text-secondary);
  font-size: 12px;
}

.modes-header-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.ac-mode-dirty {
  border-color: color-mix(in srgb, var(--accent-warning, #d97706) 45%, var(--border-muted));
  color: var(--accent-warning, #d97706);
}

.mode-delete-btn {
  color: var(--accent-danger, #ef4444);
}

.ac-mode-readonly-notice {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 7px 10px;
  border-radius: 8px;
  border: 1px dashed var(--border-muted);
  background: var(--surface-section);
  color: var(--text-muted);
  font-size: 12px;
}

.modes-workbench {
  display: grid;
  grid-template-columns: 224px minmax(0, 1fr) 320px;
  gap: 10px;
  height: clamp(540px, calc(100dvh - 230px), 780px);
  min-height: 520px;
  padding: 8px;
  border-radius: 12px;
  background: var(--surface-section);
}

.modes-rail {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
  min-height: 0;
  padding: 8px;
  border-radius: 8px;
  background: var(--surface-chrome);
}

.modes-rail .model-provider-search {
  width: 100%;
  flex-shrink: 0;
}

.ac-mode-tabs {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.ac-mode-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 8px 9px;
  border-radius: 8px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  cursor: pointer;
  transition: border-color 140ms ease, background-color 140ms ease;
}

.ac-mode-row:hover {
  background: var(--surface-hover);
}

.ac-mode-row.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
}

.ac-mode-meta {
  display: grid;
  gap: 2px;
  text-align: left;
  min-width: 0;
  flex: 1;
}

.ac-mode-meta strong {
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ac-mode-meta small {
  color: var(--text-muted);
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ac-mode-icon {
  flex-shrink: 0;
  color: var(--text-secondary);
}

.ac-mode-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 96px;
  padding: 16px 12px;
  border: 1px dashed var(--border-muted);
  border-radius: 8px;
  color: var(--text-muted);
  font-size: 12px;
  text-align: center;
}

.modes-canvas-stage {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
  min-height: 0;
}

.modes-canvas-host {
  flex: 1 1 auto;
  min-height: 0;
}

.modes-inspector-col {
  min-height: 0;
}

.ac-spawn-belt {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-raised);
  flex-shrink: 0;
}

.ac-spawn-belt-head {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
}

.ac-spawn-belt-head strong { font-size: 12px; }
.ac-spawn-belt-head span { font-size: 11px; color: var(--text-secondary); }
.ac-spawn-belt-head small { font-size: 10px; color: var(--text-muted); }
.ac-spawn-belt-list { display: flex; flex-wrap: wrap; gap: 6px; }
.ac-spawn-member {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 8px;
  border-radius: 8px;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
}
.ac-spawn-member-name { font-size: 12px; font-weight: 500; }
.ac-spawn-member small { font-size: 10px; color: var(--text-muted); }
.ac-spawn-scope { display: flex; gap: 4px; }
.ac-spawn-scope button {
  padding: 1px 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-muted);
  font-size: 10px;
  cursor: pointer;
}
.ac-spawn-scope button.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
  color: var(--text-primary);
}
.ac-spawn-scope button:disabled { opacity: 0.5; cursor: default; }
.ac-spawn-remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}
.ac-spawn-remove:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--accent-danger);
}
.ac-spawn-add { display: flex; align-items: center; gap: 8px; }
.ac-spawn-add > :first-child { flex: 1; min-width: 0; }
.quiet { color: var(--text-muted); font-size: 11px; }

@media (max-width: 1120px) {
  .modes-workbench {
    grid-template-columns: 210px minmax(0, 1fr);
    height: auto;
  }
  .modes-canvas-stage {
    min-height: 480px;
  }
  .modes-inspector-col {
    grid-column: 1 / -1;
    max-height: 440px;
  }
}

@media (max-width: 780px) {
  .modes-workbench {
    grid-template-columns: 1fr;
  }
  .modes-rail {
    max-height: 220px;
  }
}
</style>
