<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, provide, reactive, ref, shallowRef, watch } from 'vue'
import { Background } from '@vue-flow/background'
import { BaseEdge, Handle, MarkerType, Position, VueFlow, useVueFlow, type Node as FlowNode, type NodeChange } from '@vue-flow/core'
import { NodeResizer } from '@vue-flow/node-resizer'
import { Bot, ChevronLeft, ChevronRight, GitBranch, ListTodo, Maximize, Minus, Plus, Redo2, ShieldCheck, Undo2, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UieShell, UieCanvas, UieCardHost, arrangeSpace, routeSpatialEdges, type SpatialChange, type SpatialLayout, type SpatialSeed } from '@tinadec/ui'
import ComposerBar from '@/components/ComposerBar.vue'
import SpatialWorkCard from '@/components/spatial/SpatialWorkCard.vue'
import { UiButton, UiCheckbox, UiSelect } from '@/components/ui'
import { homeController as c } from '@/controllers/HomeController'
import { ensureProductionUie } from '@/lib/uiEngine'
import { api, type SessionTopologyDto } from '@/api'
import { projectSpatialObjects, projectSpatialRelations } from '@/lib/spatialObjects'
import { subscribeToSessionEvents } from '@/lib/sessionEventBus'
import { useSpatialGit } from '@/composables/useSpatialGit'
import { usePanelStyles } from '@/composables/usePanelStyles'

const { t, te, locale } = useI18n()
const emit = defineEmits<{ ready: [] }>()
const uie = ensureProductionUie()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const materialStyle = computed(() => {
  const { backgroundColor, backdropFilter, WebkitBackdropFilter, ...tokens } = getPanelStyle()
  return tokens
})
const materialAttrs = computed(() => getPanelDataAttributes())
c.setViewMode('space')
const ready = ref(false)
const surface = ref<HTMLElement | null>(null)
const composerDock = ref<HTMLElement | null>(null)
const hero = computed(() => c.messages.value.length === 0)
watch(hero, async () => {
  const box = composerDock.value?.querySelector<HTMLElement>('.composer-box')
  const from = box?.getBoundingClientRect()
  await nextTick()
  if (!box || !from?.width || !from.height || window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return
  const to = box.getBoundingClientRect()
  if (!to.width || !to.height || typeof box.animate !== 'function') return
  const dy = from.top - to.top
  if (Math.abs(dy) < 1) return
  box.animate([{ transform: `translateY(${dy}px)` }, { transform: 'translateY(0px)' }],
    { duration: 350, easing: 'cubic-bezier(0.4, 0, 0.2, 1)' })
})
const gesturing = ref(false)
const topology = shallowRef<SessionTopologyDto | null>(null)
const loadError = ref('')
const loading = ref(false)
const sessionKey = computed(() => c.selectedSessionId.value ?? `draft:${c.selectedProjectId.value ?? 'free'}`)
const projectPath = computed(() => c.currentSession.value
  ? c.projects.value.find(p => p.id === c.currentSession.value?.project_id)?.path ?? '' : c.currentProject.value?.path ?? '')
const git = useSpatialGit(projectPath)
provide('space:git', git)
const { applyNodeChanges, updateNodeInternals, getNodes, setViewport, zoomIn, zoomOut } = useVueFlow({ id: 'session-space' })
let alive = true
let generation = 0
let topologyAbort: AbortController | undefined
let topologyFlight: string | null = null
let topologyDirty = false
let refreshTimer: ReturnType<typeof setTimeout> | undefined
let unsubscribe: (() => void) | undefined
const focusedRun = ref('')
const selectedId = ref<string | null>(null)
const detailOpen = ref(false)
const terminalVisible = ref(false)
const visitedDetails = ref<string[]>([])
const focusedInstance = ref<string | null>(null)
const showDependencies = ref(true)
const objects = computed(() => projectSpatialObjects({ sessionId: sessionKey.value, messages: c.messages.value,
  queuedMessages: c.queuedMessages.value, approvals: c.approvals.value, hasGitChanges: Boolean(git.state.value.preview.files?.length),
  topology: topology.value, orchestration: c.orchestration.value, turns: c.agentTurnActivities.value,
  streamingReply: c.streamingReply.value, streamingReplies: c.streamingReplies.value, streamingRunId: c.stoppableRunId.value }))
const objectMap = computed(() => Object.fromEntries(objects.value.map(o => [o.id, o])))
const runs = computed(() => objects.value.filter(o => o.kind === 'run'))
provide('space:terminal-runs', computed(() => new Set(runs.value.flatMap(o => o.runId ? [o.runId] : []))))
provide('space:terminal-visible', terminalVisible)
const selectedObject = computed(() => selectedId.value ? objectMap.value[selectedId.value] : undefined)
const compact = computed(() => Object.fromEntries(objects.value.map(o => [o.id, Boolean(uie.snapshot.value.space?.items[o.id]?.compact)])))
const relations = computed(() => projectSpatialRelations(objects.value))
const related = computed(() => relations.value.filter(r => r.source === selectedId.value || r.target === selectedId.value))
const instanceTasks = computed(() => objects.value.filter(o => o.kind === 'task' && o.runId === focusedRun.value && o.instanceId === focusedInstance.value))
const visibleObjects = computed(() => objects.value.filter(o => !compact.value[o.id] || !o.runId || o.kind === 'run'
  || focusedRun.value === '*' || o.runId === focusedRun.value))
const visibleIds = computed(() => new Set(visibleObjects.value.map(o => o.id)))
const pendingApprovals = computed(() => c.approvals.value.filter(a => a.session_id === c.selectedSessionId.value && a.status === 'pending'))
const incomplete = computed(() => Boolean(topology.value && (topology.value.runs_truncated || topology.value.members_truncated
  || topology.value.leases_truncated || topology.value.runs.some(r => r.tasks_truncated || r.instances_truncated))))
const statusText = (status?: string) => status && te(`space.status.${status}`) ? t(`space.status.${status}`) : status
const titleOf = (id: string) => objectMap.value[id]?.title || t(`space.kind.${objectMap.value[id]?.kind ?? 'task'}`)
provide('space:objects', objectMap)
provide('space:compact', compact)
provide('space:open', openDetails)
provide('space:locate', locate)
provide('space:focus-instance', (runId: string, instanceId: string) => {
  focusedRun.value = runId
  focusedInstance.value = instanceId
  const task = objects.value.find(o => o.runId === runId && o.instanceId === instanceId)
  if (task) openDetails(task.id)
})
provide('space:open-terminal', () => {
  uie.dispatch({ command: { type: 'openCard', scope: uie.scope.value, descriptorId: 'terminal', slotId: 'right', title: t('space.cluster.terminal') }, source: 'user', expectedRevision: uie.snapshot.value.revision })
  terminalVisible.value = true
  detailOpen.value = false
})
watch(runs, value => {
  if (!focusedRun.value && value.length) focusedRun.value = value.find(o => o.runId === c.stoppableRunId.value)?.runId ?? value[0].runId ?? '*'
})
const measuredHeights = reactive(new Map<string, number>())
const seeds = computed<SpatialSeed[]>(() => objects.value.map(o => ({
  id: o.id, groupId: o.groupId, groupOrder: o.kind === 'meeting' ? 0 : o.runId ? 1 : 2, measuredHeight: measuredHeights.get(o.id), dependencyIds: o.dependencyIds,
  dependencyUnverified: o.dependencyUnverified,
  role: o.kind === 'run' || o.kind === 'meeting' ? 'anchor' : o.kind === 'task' ? 'task' : o.kind === 'result' ? 'result' : o.runId ? 'activity' : 'shared',
})))
const nodes = computed<FlowNode[]>((previous) => {
  if (gesturing.value) return previous ?? []
  const space = uie.snapshot.value.space
  const work: FlowNode[] = objects.value.flatMap(object => {
    const item = space?.items[object.id]
    if (!item) return []
    const isCompact = compact.value[object.id]
    return [{ id: object.id, type: 'work', position: { x: item.x, y: item.y }, width: item.width,
      height: isCompact || !item.autoHeight ? item.height : undefined,
      class: !isCompact && item.autoHeight ? 'space-auto-height' : '',
      style: { width: `${item.width}px`, height: isCompact || !item.autoHeight ? `${item.height}px` : 'auto', display: visibleIds.value.has(object.id) ? undefined : 'none' },
      dragHandle: '.space-drag-handle', data: {}, connectable: false, deletable: false }]
  })
  // The run card is the visual goal anchor. Do not add a second enclosing
  // rectangle around its child cards: each work card already owns its material.
  return work
})
const routes = computed(() => {
  if (!showDependencies.value) return []
  const liveNodes = new Map((gesturing.value ? getNodes?.value ?? [] : []).map(node => [node.id, node]))
  const items = visibleObjects.value.flatMap(object => {
    const item = uie.snapshot.value.space?.items[object.id]
    if (!item) return []
    const live = liveNodes.get(object.id)
    return [{ ...item, ...(gesturing.value && live ? { ...live.position, width: live.dimensions.width || item.width, height: live.dimensions.height || item.height } : {}) }]
  })
  return routeSpatialEdges(items, relations.value.filter(r => visibleIds.value.has(r.source) && visibleIds.value.has(r.target)))
})
const routeMap = computed(() => Object.fromEntries(routes.value.map(route => [route.id, route])))
const edges = computed(() => routes.value.filter(route => route.kind !== 'blocked').map(route => ({
  id: route.id, source: route.source, target: route.target,
  type: 'spatial', sourceHandle: route.sourceSide, targetHandle: route.targetSide, selectable: true,
  data: { route }, label: t('space.cluster.requires'), markerEnd: MarkerType.ArrowClosed,
})))
provide('space:measure', (id: string, height: number) => {
  const rounded = Math.ceil(height)
  if (gesturing.value || compact.value[id] || measuredHeights.get(id) === rounded) return
  measuredHeights.set(id, rounded)
  syncObjects()
  void nextTick(() => updateNodeInternals([id]))
})
function syncObjects() {
  if (!ready.value || uie.snapshot.value.space?.sessionId !== sessionKey.value || !seeds.value.length) return
  uie.dispatch({ command: { type: 'spaceSync', scope: uie.scope.value, seeds: seeds.value }, source: 'route', expectedRevision: uie.snapshot.value.revision })
}
watch(objects, syncObjects)

async function loadTopology() {
  const id = c.selectedSessionId.value
  if (id && topologyFlight === id) { topologyDirty = true; return }
  const read = ++generation
  topologyFlight = id
  topologyAbort?.abort()
  topologyAbort = new AbortController()
  if (!id) { topology.value = null; loading.value = false; return }
  loading.value = true
  try {
    const data = await api.getSessionTopology(id, { include_finished: true, max_runs: 40, max_tasks: 200 }, AbortSignal.any([topologyAbort.signal, AbortSignal.timeout(10000)]))
    if (!alive || read !== generation || id !== c.selectedSessionId.value) return
    if (data.session_id !== id) throw new Error(t('space.cluster.scopeMismatch'))
    topology.value = data
    loadError.value = ''
  } catch (error) {
    if (alive && read === generation) loadError.value = error instanceof Error ? error.message : String(error)
  } finally {
    if (alive && read === generation) {
      loading.value = false
      topologyFlight = null
      if (topologyDirty) { topologyDirty = false; void loadTopology() }
    }
  }
}
function enterSession() {
  if (!ready.value) return
  generation++
  gesturing.value = false
  clearTimeout(refreshTimer); refreshTimer = undefined
  topologyFlight = null
  topologyDirty = false
  topology.value = null
  loadError.value = ''
  measuredHeights.clear()
  focusedRun.value = ''
  selectedId.value = null
  detailOpen.value = false
  terminalVisible.value = false
  focusedInstance.value = null
  visitedDetails.value = []
  uie.showSpace(sessionKey.value)
  syncObjects()
  peek.value = null
  void loadTopology()
  void nextTick(restoreViewport)
}
watch(sessionKey, enterSession)
onMounted(async () => {
  c.start()
  await uie.ready
  if (!alive) return
  ready.value = true
  enterSession()
  unsubscribe = subscribeToSessionEvents(event => {
    if (event.session_id !== c.selectedSessionId.value || refreshTimer) return
    refreshTimer = setTimeout(() => {
      refreshTimer = undefined
      void loadTopology()
      if (event.type.startsWith('tool.execution.') || event.type === 'run.completed') void git.refresh()
    }, 800)
  })
  await nextTick()
  emit('ready')
})
onBeforeUnmount(() => { alive = false; generation++; topologyAbort?.abort(); unsubscribe?.(); clearTimeout(refreshTimer) })

function commit(changes: SpatialChange[]) {
  if (changes.length) uie.dispatch({ command: { type: 'spaceMove', scope: uie.scope.value, changes }, source: 'user', expectedRevision: uie.snapshot.value.revision })
}
function dragStop(event: { nodes: FlowNode[] }) {
  commit(event.nodes.filter(n => objectMap.value[n.id]).map(n => ({ id: n.id, x: n.position.x, y: n.position.y })))
  gesturing.value = false
}
function resizeStop(id: string, params: { x: number; y: number; width: number; height: number }) { commit([{ id, ...params }]); gesturing.value = false }
function camera(viewport: SpatialLayout['viewport']) {
  if (ready.value) uie.dispatch({ command: { type: 'spaceViewport', scope: uie.scope.value, viewport }, source: 'user', expectedRevision: uie.snapshot.value.revision })
}
function restoreViewport() { const v = uie.snapshot.value.space?.viewport; if (v) void setViewport(v) }
async function fitItems(ids: string[]) {
  const session = sessionKey.value
  await nextTick()
  if (session !== sessionKey.value) return
  const items = ids.map(id => uie.snapshot.value.space?.items[id]).filter((i): i is NonNullable<typeof i> => !!i)
  const rect = surface.value?.getBoundingClientRect()
  if (!items.length || !rect?.width || !rect.height) return
  const side = detailOpen.value ? (surface.value?.querySelector('.space-detail')?.getBoundingClientRect().width ?? 0) + 24 : 0
  const bottom = hero.value ? 24 : (composerDock.value?.getBoundingClientRect().height ?? 100) + 24
  const toolbarBottom = surface.value?.querySelector('.space-toolbar')?.getBoundingClientRect().bottom ?? rect.top + 76
  const top = toolbarBottom - rect.top + 56
  const width = Math.max(100, rect.width - side - 48), height = Math.max(100, rect.height - bottom - top - 12)
  const x = Math.min(...items.map(i => i.x)), y = Math.min(...items.map(i => i.y))
  const w = Math.max(...items.map(i => i.x + i.width)) - x, h = Math.max(...items.map(i => i.y + i.height)) - y
  const zoom = Math.max(0.2, Math.min(1, width / Math.max(1, w), height / Math.max(1, h)))
  const viewport = { x: 24 + (width - w * zoom) / 2 - x * zoom, y: top + (height - h * zoom) / 2 - y * zoom, zoom }
  await setViewport(viewport)
  if (session === sessionKey.value) camera(viewport)
}
function locate(id: string) {
  const object = objectMap.value[id]
  if (!object) return
  if (object.runId) focusedRun.value = object.runId
  peek.value = null
  void fitItems([id])
}
function overview() { void fitItems(visibleObjects.value.map(o => o.id)) }
function selectRun() {
  focusedInstance.value = null
  const ids = focusedRun.value === '*' ? visibleObjects.value.map(o => o.id) : objects.value.filter(o => o.runId === focusedRun.value).map(o => o.id)
  void fitItems(ids)
}
function arrangeCurrent() {
  const space = uie.snapshot.value.space
  if (!space || !focusedRun.value || focusedRun.value === '*') return
  commit(arrangeSpace(space, seeds.value, focusedRun.value))
  selectRun()
}
function openDetails(id: string) {
  if (!objectMap.value[id]) return
  selectedId.value = id
  detailOpen.value = true
  terminalVisible.value = false
  if (!visitedDetails.value.includes(id)) visitedDetails.value.push(id)
}
function closeDetails() {
  detailOpen.value = false
  surface.value?.querySelector<HTMLButtonElement>(`[data-open-object="${CSS.escape(selectedId.value ?? '')}"]`)?.focus()
}
function locateBlocker() {
  const blocker = objects.value.find(o => (!o.runId || focusedRun.value === '*' || o.runId === focusedRun.value)
    && (o.waitingFor?.length || ['blocked', 'failed', 'waiting_approval', 'awaiting_user', 'awaiting_approval'].includes(o.status ?? '')))
  if (blocker) { openDetails(blocker.id); locate(blocker.id) }
  else if (pendingApprovals.value.length) { const card = objects.value.find(o => o.kind === 'approval'); if (card) { openDetails(card.id); locate(card.id) } }
}
const hasBlocker = computed(() => pendingApprovals.value.length > 0 || objects.value.some(o => (!o.runId || focusedRun.value === '*' || o.runId === focusedRun.value)
  && (o.waitingFor?.length || ['blocked', 'failed', 'waiting_approval', 'awaiting_user', 'awaiting_approval'].includes(o.status ?? ''))))
function keyboard(event: KeyboardEvent) {
  if (event.key === 'Escape' && detailOpen.value) { closeDetails(); return }
  const target = event.target as HTMLElement
  if (target.closest('input,textarea,select,[contenteditable=true]') || !(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'z') return
  event.preventDefault()
  if (event.shiftKey) uie.redo(); else uie.undo()
}
const entries = [{ kind: 'meeting', key: 'planning', icon: Bot }, { kind: 'run', key: 'cluster.goals', icon: ListTodo }, { kind: 'git', key: 'changes', icon: GitBranch }, { kind: 'approval', key: 'approvals', icon: ShieldCheck }] as const
const visibleEntries = computed(() => entries.filter(entry => objects.value.some(object => object.kind === entry.kind)))
const peek = ref<string | null>(null)
const peekIndex = ref(0)
const expanded = ref(false)
const previews = computed(() => objects.value.filter(o => o.kind === peek.value))
const preview = computed(() => previews.value[Math.min(peekIndex.value, previews.value.length - 1)])
function showPreview(kind: string) { if (peek.value !== kind) { peekIndex.value = 0; expanded.value = false }; peek.value = kind }
function leavePreview(event: FocusEvent) { if (!(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null)) peek.value = null }
</script>

<template>
  <UieShell v-if="ready && c.viewMode.value === 'space'">
    <UieCanvas spatial :spatial-panel="terminalVisible">
      <div ref="surface" class="space-surface" :style="materialStyle" v-bind="materialAttrs" tabindex="-1" @keydown="keyboard">
        <div class="space-flow-area">
          <VueFlow :key="sessionKey" id="session-space" :nodes="nodes" :edges="edges" :apply-default="false"
            :nodes-connectable="false" :delete-key-code="null" :pan-on-drag="[1]" :pan-on-scroll="true" :zoom-on-scroll="false"
            :zoom-on-pinch="true" :zoom-on-double-click="false" :min-zoom="0.2" :max-zoom="2" :only-render-visible-elements="false"
            @nodes-change="(changes: NodeChange[]) => applyNodeChanges(changes)" @node-drag-start="gesturing = true" @node-drag-stop="dragStop"
            @edge-click="({ edge }) => openDetails(edge.target)" @pane-ready="restoreViewport" @viewport-change-end="camera">
            <template #edge-spatial="edge">
              <BaseEdge :id="edge.id" :path="edge.data.route.path" :marker-end="edge.markerEnd"
                :label="edge.label" :label-x="edge.data.route.labelX || 0.001" :label-y="edge.data.route.labelY || 0.001"
                :label-style="{ fill: 'var(--text-secondary)', fontSize: 12 }"
                :label-bg-style="{ fill: 'var(--surface-raised)' }" />
            </template>
            <Background pattern-color="var(--border-muted)" :gap="24" :size="1" />
            <template #node-work="{ id, selected }">
              <template v-for="side in [Position.Top, Position.Bottom, Position.Left, Position.Right]" :key="side">
                <Handle :id="side" type="target" :position="side" />
                <Handle :id="side" type="source" :position="side" />
              </template>
              <NodeResizer :min-width="320" :min-height="160" :max-width="1400" :max-height="1200" :is-visible="selected"
                @resize-start="gesturing = true" @resize-end="({ params }) => resizeStop(id, params)" />
              <UieCardHost :instance="{ id, descriptorId: 'spatialWork', title: '', state: { objectId: id } }" :active="visibleIds.has(id)" />
            </template>
          </VueFlow>
          <div class="space-toolbar">
            <div v-if="runs.length" class="space-goal-controls">
              <UiSelect
                :model-value="focusedRun"
                :display-value="focusedRun === '*' ? t('space.cluster.allGoals') : (runs.find(run => run.runId === focusedRun)?.title || t('space.kind.run')) + ' · ' + statusText(runs.find(run => run.runId === focusedRun)?.status)"
                :aria-label="t('space.cluster.currentGoal')"
                class="space-goal-select"
                @update:model-value="(value) => { focusedRun = value; selectRun() }"
              >
                <template #default="{ select, selectedValue }">
                  <button type="button" class="space-select-option" :class="{ 'space-select-option-active': selectedValue === '*' }" @click="select('*')">{{ t('space.cluster.allGoals') }}</button>
                  <button v-for="run in runs" :key="run.id" type="button" class="space-select-option" :class="{ 'space-select-option-active': selectedValue === run.runId }" @click="run.runId && select(run.runId)">
                    <span>{{ run.title || t('space.kind.run') }}</span><small>{{ statusText(run.status) }}</small>
                  </button>
                </template>
              </UiSelect>
              <UiButton variant="ghost" size="sm" :disabled="!hasBlocker" @click="locateBlocker">{{ t('space.cluster.blockers') }}</UiButton>
              <UiButton variant="ghost" size="sm" :disabled="!focusedRun || focusedRun === '*'" :title="t('space.cluster.arrangeHint')" @click="arrangeCurrent">{{ t('space.cluster.arrange') }}</UiButton>
              <label class="space-relation-toggle" @click.stop>
                <UiCheckbox v-model="showDependencies" :aria-label="t('space.cluster.dependencies')" />
                <span>{{ t('space.cluster.dependencies') }}</span>
              </label>
            </div>
            <div class="space-controls">
              <UiButton variant="ghost" size="icon" :aria-label="t('space.undo')" :disabled="!uie.canUndo.value" @click="uie.undo()"><Undo2 /></UiButton>
              <UiButton variant="ghost" size="icon" :aria-label="t('space.redo')" :disabled="!uie.canRedo.value" @click="uie.redo()"><Redo2 /></UiButton>
              <UiButton variant="ghost" size="icon" :aria-label="t('space.zoomOut')" @click="zoomOut()"><Minus /></UiButton>
              <UiButton variant="ghost" size="icon" :aria-label="t('space.zoomIn')" @click="zoomIn()"><Plus /></UiButton>
              <UiButton variant="ghost" size="icon" :aria-label="t('space.overview')" @click="overview"><Maximize /></UiButton>
            </div>
          <div v-if="topology || loadError || loading" class="space-data-status" role="status">
            <span v-if="topology">{{ t('space.cluster.loaded', { runs: topology.runs.length, tasks: topology.runs.reduce((n, r) => n + r.tasks.length, 0) }) }} · {{ t('space.cluster.fetchedAt') }} {{ new Date(topology.generated_at).toLocaleTimeString(locale) }}</span>
            <span v-if="incomplete">{{ t('space.cluster.truncated') }}</span>
            <span v-if="loadError">{{ t(topology ? 'space.cluster.stale' : 'space.cluster.loadFailed') }}: {{ loadError }}</span>
            <button v-if="loadError" type="button" @click="loadTopology">{{ t('space.retry') }}</button>
            <span v-if="loading">{{ t('space.loading') }}</span>
          </div>
          </div>
          <button v-if="terminalVisible" type="button" class="space-hide-terminal" @click="terminalVisible = false">{{ t('space.cluster.hideTerminal') }}</button>
        </div>
        <aside v-show="detailOpen && selectedObject" class="space-detail" role="region" :aria-label="t('space.cluster.details')">
          <header class="space-detail-heading"><strong>{{ t('space.cluster.details') }}</strong><UiButton variant="ghost" size="icon" :aria-label="t('space.cluster.closeDetails')" @click="closeDetails"><X /></UiButton></header>
          <div class="space-detail-scroll">
            <div v-if="focusedInstance && instanceTasks.length" class="space-related-list">
              <strong>{{ t('space.cluster.instanceTasks') }}</strong>
              <button v-for="task in instanceTasks" :key="task.id" type="button" @click="openDetails(task.id); locate(task.id)">{{ task.title }}</button>
              <button type="button" @click="focusedInstance = null">{{ t('space.cluster.clearFocus') }}</button>
            </div>
            <template v-for="id in visitedDetails" :key="`${sessionKey}:${id}`">
              <SpatialWorkCard v-if="objectMap[id]" v-show="selectedId === id" :object="objectMap[id]" detail />
            </template>
            <section v-if="selectedObject" class="space-related-list" :aria-label="t('space.cluster.relations')">
              <strong>{{ t('space.cluster.relations') }}</strong>
              <p>{{ t('space.cluster.constraintOnly') }}</p>
              <div v-for="relation in related" :key="relation.id" class="space-relation-record">
                <button type="button" @click="locate(relation.source)">{{ titleOf(relation.source) }}</button>
                <span>{{ t('space.cluster.requires') }} →</span>
                <button type="button" @click="locate(relation.target)">{{ titleOf(relation.target) }}</button>
                <small>{{ t('space.cluster.source') }}: {{ relation.sourceField }}</small>
                <small v-if="routeMap[relation.id]?.kind === 'blocked'">{{ t('space.cluster.routeBlocked') }}</small>
              </div>
              <p v-if="!related.length">{{ t('space.cluster.noRelations') }}</p>
              <small>{{ selectedObject.runId || sessionKey }}<br />{{ selectedObject.taskId || selectedObject.id }}</small>
            </section>
          </div>
        </aside>
        <div ref="composerDock" class="space-composer-dock" :class="{ 'space-composer-hero': hero }">
          <nav v-if="visibleEntries.length" class="space-index" :aria-label="t('space.index')" @mouseleave="peek = null" @focusout="leavePreview" @keydown.esc.stop="peek = null">
            <div v-for="entry in visibleEntries" :key="entry.kind" class="space-index-entry">
              <button class="space-index-button" :aria-expanded="peek === entry.kind" @mouseenter="showPreview(entry.kind)" @focus="showPreview(entry.kind)" @click="showPreview(entry.kind); preview && locate(preview.id)">
                <component :is="entry.icon" :size="16" />{{ t('space.' + entry.key) }}
                <span v-if="entry.kind === 'approval' && pendingApprovals.length" class="space-approval-count">{{ pendingApprovals.length }}</span>
              </button>
              <Transition name="space-preview">
                <div v-if="peek === entry.kind && preview" class="space-peek" :class="{ 'space-peek-stacked': previews.length > 1 }">
                  <div v-if="previews.length > 1" class="space-peek-pagination">
                    <button :disabled="peekIndex <= 0" :aria-label="t('space.previous')" @click="peekIndex--"><ChevronLeft :size="16" /></button>
                    <span>{{ peekIndex + 1 }} / {{ previews.length }}</span>
                    <button :disabled="peekIndex >= previews.length - 1" :aria-label="t('space.next')" @click="peekIndex++"><ChevronRight :size="16" /></button>
                    <button @click="expanded = !expanded">{{ t(expanded ? 'space.collapse' : 'space.expand') }}</button>
                  </div>
                  <div v-if="expanded" class="space-plan-list"><button v-for="item in previews" :key="item.id" @click="locate(item.id)">{{ item.title || t('space.kind.' + item.kind) }} · {{ item.groupId.slice(0, 8) }}</button></div>
                  <div v-else class="space-peek-content" role="button" tabindex="0" :aria-label="preview.title || t('space.kind.' + preview.kind)" @keydown.enter.prevent="locate(preview.id)" @keydown.space.prevent="locate(preview.id)" @click="locate(preview.id)"><SpatialWorkCard :object="preview" preview /></div>
                </div>
              </Transition>
            </div>
          </nav>
          <ComposerBar spatial :hero="hero" :busy="c.busy.value || c.working.value" :model-value="c.draft.value" :permission="c.currentPermission.value"
            :projects="c.projects.value" :selected-project-id="c.selectedProjectId.value" :session-id="c.currentSession.value?.id ?? null"
            :mode-version-id="c.composerSettings.value.mode_version_id" :meeting-model-override="c.composerSettings.value.meeting_model_override"
            :space-options="c.composerSettings.value.space_options" :settings-saving="c.settingsSaving.value" :settings-error="c.settingsError.value"
            @update:space-options="c.updateComposerSettings({ space_options: $event })"
            @update:meeting-model-override="c.updateComposerSettings($event ? { meeting_model_override: $event } : { clear_meeting_model_override: true })"
            :runs="c.runs.value" :can-stop="Boolean(c.stoppableRunId.value)"
            @update:model-value="c.updateDraft($event)" @update:permission="c.updatePermission($event)" @submit="c.sendMessage($event)" @welcome-submit="c.handleWelcomeSend($event)"
            @stop="c.stopRun()" @create-project="c.openProject()" @select-project="c.setSelectedProject($event)">
            <template #capabilities><span class="space-meeting-label"><Bot :size="14" />{{ t('space.kind.meeting') }}</span></template>
          </ComposerBar>
        </div>
      </div>
    </UieCanvas>
  </UieShell>
</template>

<style>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';
@import '@vue-flow/node-resizer/dist/style.css';
.space-surface { position: relative; height: 100%; min-height: 0; }
.space-flow-area { position: absolute; inset: 0; }
.space-flow-area .vue-flow__node-work { border-radius: 16px; background: transparent; box-shadow: var(--shadow-card-subtle); }
.space-flow-area .vue-flow__node-work.selected { outline: 2px solid var(--accent-primary); outline-offset: 3px; }
.space-toolbar { position: absolute; top: 8px; left: 12px; right: 12px; display: flex; align-items: flex-start; justify-content: space-between; gap: 8px; pointer-events: none; flex-wrap: wrap; }
.space-goal-controls, .space-controls { display: flex; align-items: center; gap: 4px; border-radius: 12px; background: var(--surface-raised); pointer-events: auto; backdrop-filter: var(--material-filter-raised, none); }
.space-goal-controls { flex-wrap: wrap; padding: 4px; max-width: 100%; }
.space-goal-select { max-width: 270px; min-width: 190px; }
.space-select-option { display: flex; width: 100%; align-items: center; justify-content: space-between; gap: 12px; padding: 8px 12px; text-align: left; color: var(--text-primary); background: transparent; border: 0; cursor: pointer; font-size: 13px; }
.space-select-option:hover, .space-select-option-active { background: var(--surface-hover); }
.space-select-option small { color: var(--text-muted); white-space: nowrap; }
.space-controls { margin-left: auto; }
.space-relation-toggle { white-space: nowrap; display: inline-flex; align-items: center; gap: 6px; font-size: 12px; color: var(--text-secondary); padding: 6px; cursor: pointer; }
.space-data-status { flex-basis: 100%; display: flex; flex-wrap: wrap; align-items: center; gap: 6px; font-size: 11px; color: var(--text-secondary); pointer-events: none; }
.space-data-status span { background: var(--surface-raised); border-radius: 4px; padding: 2px 4px; }
.space-data-status button, .space-hide-terminal { pointer-events: auto; border: 0; border-radius: 6px; padding: 4px 8px; color: var(--text-primary); background: var(--surface-raised); cursor: pointer; }
.space-hide-terminal { position: absolute; top: 132px; right: 12px; }
.space-detail { position: absolute; top: 128px; right: 12px; bottom: 152px; width: min(430px, 48%); min-width: 260px; display: flex; flex-direction: column; border-radius: 16px; background: var(--surface-section); backdrop-filter: var(--material-filter-raised, none); border: 1px solid var(--border-muted); box-shadow: var(--shadow-card-subtle); z-index: 5; }
.space-detail-heading { display: flex; align-items: center; justify-content: space-between; padding: 4px 10px 4px 18px; font-size: 13px; color: var(--text-primary); }
.space-detail-scroll { overflow: auto; min-height: 0; flex: 1; }
.space-detail .spatial-work-card { height: auto; background: transparent; backdrop-filter: none; }
.space-related-list { display: flex; flex-direction: column; gap: 8px; padding: 16px 18px; border-top: 1px solid var(--border-muted); color: var(--text-secondary); font-size: 12px; }
.space-related-list p { margin: 0; line-height: 1.6; }
.space-related-list button { border: 0; border-radius: 4px; background: transparent; color: var(--text-primary); cursor: pointer; padding: 4px; text-align: left; overflow-wrap: anywhere; }
.space-related-list button:hover { background: var(--surface-hover); }
.space-related-list small { overflow-wrap: anywhere; }
.space-relation-record { display: flex; flex-direction: column; gap: 4px; }
.space-composer-dock { position: absolute; bottom: 12px; left: 0; right: 0; width: min(760px, calc(100% - 32px)); margin: auto; z-index: 10; pointer-events: none; }
.space-composer-dock .composer, .space-index-entry { pointer-events: auto; }
.space-composer-dock.space-composer-hero { bottom: 50%; }
.space-composer-dock .composer { width: 100%; margin: 0; padding: 0; }
.space-index { display: flex; justify-content: center; gap: 8px; margin-bottom: 12px; position: relative; }
.space-index-entry { position: relative; }
.space-index-button { display: flex; align-items: center; gap: 8px; padding: 9px 14px; border: 0; border-radius: 10px; color: var(--text-secondary); background: var(--surface-raised); cursor: pointer; font-size: 13px; }
.space-index-button:hover, .space-index-button:focus-visible { color: var(--text-primary); background: var(--surface-hover); }
.space-peek { position: absolute; width: 420px; max-height: min(520px, 65vh); bottom: 100%; left: 50%; transform: translateX(-50%); padding-bottom: 12px; display: flex; flex-direction: column; }
.space-peek-content { border-radius: 16px; box-shadow: var(--shadow-card-subtle); min-height: 0; flex: 1; cursor: pointer; overflow: hidden; }
.space-index-button, .space-peek-pagination, .space-plan-list { backdrop-filter: var(--material-filter-raised, none); }
.space-peek-stacked::before { content: ''; position: absolute; inset: -7px 7px 22px; border-radius: 16px; background: var(--surface-section); border: 1px solid var(--border-muted); z-index: -1; }
.space-peek-pagination { display: flex; justify-content: space-around; gap: 8px; padding: 8px; background: var(--surface-raised); border-radius: 12px 12px 0 0; font-size: 12px; }
.space-peek button { color: var(--text-primary); background: var(--surface-raised); border: 0; cursor: pointer; }
.space-plan-list { flex: 1; overflow: auto; background: var(--surface-raised); }
.space-plan-list button { display: block; width: 100%; padding: 12px; text-align: left; }
.space-approval-count { color: var(--accent-warning); font-size: 12px; }
.space-meeting-label { display: flex; align-items: center; gap: 6px; color: var(--text-secondary); font-size: 12px; }
@media (max-width: 800px) { .space-index-entry { position: static; } .space-index-button { padding: 8px; gap: 4px; } .space-peek { max-width: calc(100vw - 88px); } .space-goal-select { max-width: 220px; min-width: 160px; } }
.space-auto-height .uie-card-host { height: auto; }
.space-auto-height .spatial-work-card { height: auto; max-height: 560px; }
.space-flow-area .vue-flow__handle { opacity: 0; pointer-events: none; }
.space-flow-area .vue-flow__edge-path { stroke: var(--text-secondary); stroke-width: 1.5; }
.space-peek-content .spatial-work-card { height: auto; max-height: 440px; }
.space-peek { transform-origin: bottom center; }
.space-preview-enter-active, .space-preview-leave-active { transition: opacity 160ms ease, transform 160ms ease; }
.space-preview-enter-from, .space-preview-leave-to { opacity: 0; transform: translateX(-50%) translateY(6px) scale(.96); }
@media (prefers-reduced-motion: reduce) { .space-preview-enter-active, .space-preview-leave-active { transition: none; } }
</style>
