<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useStatusLabels } from '../lib/statusLabels'
import { useAgentDirectory } from '../composables/useAgentDirectory'
import {
  Activity,
  AlertTriangle,
  Archive,
  BarChart3,
  CheckCircle2,
  Database,
  Download,
  GitBranch,
  Layers3,
  ListTree,
  MessageSquare,
  Network,
  Package,
  RefreshCw,
  Sparkles,
  Wrench,
  X,
} from '@lucide/vue'
import {
  api,
  type ContextBudgetShareDto,
  type ContextPackDto,
  type ModelInvocationDto,
  type OrchestrationSnapshotDto,
  type RunContextDiagnosticsDto,
  type StepResultDto,
  type SupervisionFindingDto,
  type ToolDescriptorDto,
  type ToolExecutionTimelineItemDto,
  type TopologyRunDto,
} from '../api'
import {
  buildOverlapCacheScopeKey,
  fetchRunDiagnosticsWithCache,
  getCachedOverlapAudit,
  getCachedRunDiagnostics,
} from '../lib/contextDiagnosticsCache'
import { useHostAccess } from '../lib/hostAccess'
import { summarizeModelInvocations, type ModelUsageGroup, type ModelUsageSummary } from '../lib/modelUsage'
import DeclaredGraphCanvas from './canvas/DeclaredGraphCanvas.vue'
import OrchestrationMiniGraph, {
  buildRuntimeTopology,
  type RuntimeTopologyNode,
  type RuntimeTopologyRoleTier,
} from './OrchestrationMiniGraph.vue'
import AgentTimeline from './timeline/AgentTimeline.vue'
import TrajectoryTreePanel from './trajectory/TrajectoryTreePanel.vue'
import ContextWindowBucketChart from './trajectory/ContextWindowBucketChart.vue'
import ContextTreemap from './trajectory/ContextTreemap.vue'
import ContextOverlapPanel from './trajectory/ContextOverlapPanel.vue'
import ToolExecutionTimeline from './tools/ToolExecutionTimeline.vue'
import ToolCatalogBrowser from './tools/ToolCatalogBrowser.vue'
import ToolStatsDashboard from './tools/ToolStatsDashboard.vue'
import SessionPlanPanel from './plan/SessionPlanPanel.vue'
import TeammateSteerPopover, { type TeammateSteerTarget } from './teammate/TeammateSteerPopover.vue'
import {
  buildTrajectoryTree,
  resolveContextWindowBuckets,
  type TrajectoryNode,
} from '../lib/trajectoryBuilder'
import {
  resolveContextTreemapModel,
  type ContextTreemapModel,
  type ContextTreemapNode,
} from '../lib/contextTreemapLayout'
import {
  fromCoreOverlapAuditDto,
  type ContextOverlapAuditResult,
  type ContextOverlapPairSummary,
} from '../lib/contextOverlapWinnowing'
import {
  buildDiagnosticDraft,
  exportOfflineDiagnosticHtml,
  triggerDiagnoseInComposer,
  type ExportOfflineHtmlResult,
} from '../lib/offlineDiagnosticReport'

export interface OrchestrationFocusTarget {
  target?: string | null
  taskId?: string | null
  taskKey?: string | null
  handle?: string | null
  workerHandle?: string | null
  instanceId?: string | null
  agentSlug?: string | null
  nonce?: number
}

const props = defineProps<{
  snapshot: OrchestrationSnapshotDto | null
  toolExecutions: ToolExecutionTimelineItemDto[]
  tools?: ToolDescriptorDto[]
  sessionId?: string | null
  runId?: string | null
  topologyRun?: TopologyRunDto | null
  modelInvocations?: ModelInvocationDto[]
  focusedTarget?: string | OrchestrationFocusTarget | null
}>()

const { t, te, locale } = useI18n()
const { canAccessBackend } = useHostAccess()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? t(key, params) : t(key)
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const focusedSteerTarget = ref<TeammateSteerTarget | null>(null)
const focusedSteerOpen = ref(false)
const { runStatusLabel: statusWords, severityLabel } = useStatusLabels()
const { nameFor } = typeof api.listAgents === 'function'
  ? useAgentDirectory()
  : { nameFor: (slug: string | null | undefined, fallback?: string | null) => fallback ?? slug ?? '' }

const emit = defineEmits<{
  'rerun-tool': [toolExecution: ToolExecutionTimelineItemDto]
  'view-tool-details': [toolExecution: ToolExecutionTimelineItemDto]
  'execute-tool': [tool: ToolDescriptorDto]
  'select-node': [node: RuntimeTopologyNode | null]
  'diagnose-draft': [draft: string]
  'export-report': [result: ExportOfflineHtmlResult]
}>()

type TabKey = 'timeline' | 'catalog' | 'stats'
type TopologyViewMode = 'topology' | 'timeline'
type DiagnosticsTabKey = 'tree' | 'treemap' | 'overlap'

const activeTab = ref<TabKey>('timeline')
const topologyViewMode = ref<TopologyViewMode>('topology')
const diagnosticsTab = ref<DiagnosticsTabKey>('tree')
const selectedTrajectoryNode = ref<TrajectoryNode | null>(null)
const selectedTreemapSegment = ref<ContextTreemapNode | null>(null)

const toolCatalog = ref<ToolDescriptorDto[]>([])
const toolCatalogLoading = ref(false)
const selectedNodeId = ref<string | null>(null)
const detailsBlockRef = ref<HTMLElement | null>(null)

const hasSnapshot = computed(() => Boolean(props.snapshot?.run))
const lastObservedSnapshot = ref<OrchestrationSnapshotDto | null>(null)
const effectiveRunId = computed<string | null>(
  () => props.snapshot?.run?.id ?? props.runId ?? lastObservedSnapshot.value?.run?.id ?? null,
)
const effectiveSessionId = computed(
  () => props.sessionId || props.snapshot?.run?.session_id || lastObservedSnapshot.value?.run?.session_id || null,
)

watch(
  () => props.snapshot,
  (next) => {
    if (next?.run?.id) {
      lastObservedSnapshot.value = next
    }
  },
  { immediate: true },
)

const runDiagnostics = ref<RunContextDiagnosticsDto | null>(null)
const diagnosticsStale = ref(false)
const diagnosticsCachedAt = ref<string | null>(null)
const selectedTreemapPackId = ref<string | null>(null)
const latestOverlapAudit = ref<ContextOverlapAuditResult | null>(null)
let diagnosticsAbort: AbortController | null = null
let diagnosticsEpoch = 0

async function loadRunDiagnostics(): Promise<void> {
  const targetRunId = effectiveRunId.value
  diagnosticsAbort?.abort()
  diagnosticsAbort = null

  if (!targetRunId) {
    runDiagnostics.value = null
    diagnosticsStale.value = false
    diagnosticsCachedAt.value = null
    return
  }

  // Seed immediately from scoped local cache so reconnect/tab switch never flashes empty.
  if (!runDiagnostics.value || runDiagnostics.value.run_id !== targetRunId) {
    const cached = getCachedRunDiagnostics(targetRunId)
    if (cached) {
      runDiagnostics.value = cached.data
      diagnosticsCachedAt.value = cached.cachedAt
      diagnosticsStale.value = !canAccessBackend.value
    }
  }

  const overlapScopeKey = buildOverlapCacheScopeKey({ runId: targetRunId, autoLoadMode: 'all' })
  if (!latestOverlapAudit.value) {
    const cachedOverlap = getCachedOverlapAudit(overlapScopeKey, { runId: targetRunId })
    if (cachedOverlap) {
      latestOverlapAudit.value = fromCoreOverlapAuditDto(cachedOverlap.data)
    }
  }

  if (typeof api.getRunContextDiagnostics !== 'function') return

  const epoch = ++diagnosticsEpoch
  const controller = new AbortController()
  diagnosticsAbort = controller

  try {
    const outcome = await fetchRunDiagnosticsWithCache(
      targetRunId,
      () =>
        api.getRunContextDiagnostics(
          targetRunId,
          { selected_pack_id: selectedTreemapPackId.value },
          controller.signal,
        ),
      { canAccessBackend: canAccessBackend.value },
    )
    if (epoch !== diagnosticsEpoch || controller.signal.aborted) return
    if (outcome.data) {
      runDiagnostics.value = outcome.data
      diagnosticsStale.value = outcome.stale
      diagnosticsCachedAt.value = outcome.cachedAt
    } else if (outcome.error && runDiagnostics.value?.run_id === targetRunId) {
      diagnosticsStale.value = true
    }
  } catch {
    if (epoch === diagnosticsEpoch && !controller.signal.aborted && runDiagnostics.value?.run_id === targetRunId) {
      diagnosticsStale.value = true
    }
  } finally {
    if (diagnosticsAbort === controller) diagnosticsAbort = null
  }
}

// Live topology lineage enrichment (`dispatched_by_task_id` & `instances` depth/parent) when available.
const fetchedTopologyRun = ref<TopologyRunDto | null>(null)
let topologyAbort: AbortController | null = null
let topologyEpoch = 0

async function loadRunTopology(): Promise<void> {
  if (props.topologyRun !== undefined) return
  const runId = props.snapshot?.run?.id
  const sessId = effectiveSessionId.value
  topologyAbort?.abort()
  topologyAbort = null
  if (!runId || !sessId || typeof api.getSessionTopology !== 'function') {
    fetchedTopologyRun.value = null
    return
  }
  const epoch = ++topologyEpoch
  const controller = new AbortController()
  topologyAbort = controller
  try {
    const view = await api.getSessionTopology(
      sessId,
      { run_id: runId, include_finished: true, max_runs: 1, max_tasks: 200 },
      controller.signal,
    )
    if (epoch !== topologyEpoch || controller.signal.aborted) return
    fetchedTopologyRun.value = view.runs.find((r) => r.run_id === runId) ?? view.runs[0] ?? null
  } catch {
    if (epoch === topologyEpoch && !controller.signal.aborted) {
      // Keep snapshot-only fallback when topology endpoint is unavailable.
      if (fetchedTopologyRun.value?.run_id !== runId) {
        fetchedTopologyRun.value = null
      }
    }
  } finally {
    if (topologyAbort === controller) topologyAbort = null
  }
}

onBeforeUnmount(() => {
  topologyAbort?.abort()
  diagnosticsAbort?.abort()
})

watch(
  () => [
    effectiveRunId.value,
    props.snapshot?.run?.status,
    props.snapshot?.context_packs.length,
    selectedTreemapPackId.value,
    canAccessBackend.value,
  ],
  (next, prev) => {
    if (next[0] !== prev?.[0]) {
      selectedTreemapPackId.value = null
      latestOverlapAudit.value = null
    }
    void loadRunDiagnostics()
  },
  { immediate: true },
)

watch(
  () => [
    props.snapshot?.run?.id,
    effectiveSessionId.value,
    props.snapshot?.run?.status,
    props.snapshot?.run?.updated_at,
    props.snapshot?.nodes.length,
    props.snapshot?.flows?.length,
  ],
  (next, prev) => {
    const nextRunId = next[0]
    const prevRunId = prev?.[0]
    if (nextRunId !== prevRunId) {
      selectedNodeId.value = null
      fetchedTopologyRun.value = null
    }
    void loadRunTopology()
  },
  { immediate: true },
)

const activeTopologyRun = computed(() => props.topologyRun ?? fetchedTopologyRun.value)

const runtimeTopology = computed(() =>
  buildRuntimeTopology({
    snapshot: props.snapshot,
    topologyRun: activeTopologyRun.value,
    toolExecutions: props.toolExecutions,
    coordinatorFallbackLabel: t('orchestrationPanel.roleCoordinator'),
    locale: locale.value,
    resolveAgentName: nameFor,
  }),
)

const selectedNode = computed<RuntimeTopologyNode | null>(
  () => runtimeTopology.value.nodes.find((n) => n.id === selectedNodeId.value) ?? null,
)

const selectedParentNode = computed<RuntimeTopologyNode | null>(() => {
  const parentId = selectedNode.value?.parentId
  if (!parentId) return null
  return runtimeTopology.value.nodes.find((n) => n.id === parentId) ?? null
})

const canSteerSelectedNode = computed(() => {
  const node = selectedNode.value
  const runId = props.snapshot?.run?.id ?? activeTopologyRun.value?.run_id
  if (!node || !runId || node.roleTier === 'coordinator') return false
  return Boolean((node.handle || node.agentSlug || '').trim())
})

function openSelectedNodeSteer(event?: Event): void {
  const node = selectedNode.value
  const runId = props.snapshot?.run?.id ?? activeTopologyRun.value?.run_id
  if (!node || !runId || node.roleTier === 'coordinator') return
  const handle = (node.handle || node.agentSlug || '').trim()
  if (!handle) return
  const el = (event?.currentTarget as HTMLElement | null) ?? null
  const rect = el?.getBoundingClientRect?.()
  focusedSteerTarget.value = {
    runId,
    handle,
    displayName: node.label,
    status: node.rawStatus || node.status,
    paused: String(node.rawStatus ?? '').toLowerCase() === 'paused',
    taskSummary: node.title && node.title !== node.label ? node.title : (node.subtitle ?? node.description ?? null),
    anchorRect: rect && (rect.width > 0 || rect.height > 0)
      ? { top: rect.top, left: rect.left, width: rect.width, height: rect.height, right: rect.right, bottom: rect.bottom }
      : null,
  }
  focusedSteerOpen.value = true
}

function matchTopologyNode(target: string | OrchestrationFocusTarget | null | undefined): RuntimeTopologyNode | null {
  if (!target) return null
  const nodes = runtimeTopology.value.nodes
  if (!nodes.length) return null
  if (typeof target === 'string') {
    const q = target.trim()
    if (!q) return null
    return (
      nodes.find((n) => n.id === q)
      ?? nodes.find((n) => n.taskId === q)
      ?? nodes.find((n) => n.instanceIds.includes(q))
      ?? nodes.find((n) => n.taskKey === q)
      ?? nodes.find((n) => n.handle === q)
      ?? nodes.find((n) => n.agentSlug === q)
      ?? nodes.find((n) => n.label === q)
      ?? null
    )
  }
  if (target.taskId) {
    const hit = nodes.find((n) => n.taskId === target.taskId || n.id === `task:${target.taskId}`)
    if (hit) return hit
  }
  if (target.instanceId) {
    const hit = nodes.find((n) => n.instanceIds.includes(target.instanceId!))
    if (hit) return hit
  }
  if (target.taskKey) {
    const hit = nodes.find((n) => n.taskKey === target.taskKey)
    if (hit) return hit
  }
  const effectiveHandle = target.handle ?? target.workerHandle
  if (effectiveHandle) {
    const hit = nodes.find((n) => n.handle === effectiveHandle)
    if (hit) return hit
  }
  if (target.agentSlug) {
    const hit = nodes.find((n) => n.agentSlug === target.agentSlug)
    if (hit) return hit
  }
  if (target.target) {
    return matchTopologyNode(target.target)
  }
  return null
}

watch(
  [() => props.focusedTarget, () => runtimeTopology.value.nodes],
  ([target]) => {
    if (!target) return
    const matched = matchTopologyNode(target)
    if (!matched) return
    selectedNodeId.value = matched.id
    activeTab.value = 'timeline'
    emit('select-node', matched)
    void nextTick(() => {
      detailsBlockRef.value?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' })
    })
  },
  { immediate: true },
)

function onSelectTopologyNode(node: RuntimeTopologyNode | null): void {
  selectedNodeId.value = node ? node.id : null
  if (node && activeTab.value === 'catalog') {
    activeTab.value = 'timeline'
  }
  emit('select-node', node)
}

function onSelectTimelineNodeId(nodeId: string | null): void {
  if (!nodeId) {
    onSelectTopologyNode(null)
    return
  }
  const matched = runtimeTopology.value.nodes.find((n) => n.id === nodeId) ?? null
  onSelectTopologyNode(matched)
}

function onFocusPlanNode(target: { taskKey?: string | null; agentSlug?: string | null }): void {
  const matched = matchTopologyNode({ taskKey: target.taskKey, agentSlug: target.agentSlug })
  if (!matched) return
  selectedNodeId.value = matched.id
  activeTab.value = 'timeline'
  emit('select-node', matched)
  void nextTick(() => {
    detailsBlockRef.value?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' })
  })
}

function clearNodeFilter(): void {
  selectedNodeId.value = null
  emit('select-node', null)
}

function roleTierLabel(tier: RuntimeTopologyRoleTier): string {
  if (tier === 'coordinator') return t('orchestrationPanel.roleCoordinator')
  if (tier === 'subagent') return t('orchestrationPanel.roleSubagent')
  return t('orchestrationPanel.roleWorker')
}

const filteredToolExecutions = computed<ToolExecutionTimelineItemDto[]>(() => {
  const node = selectedNode.value
  if (!node) return props.toolExecutions
  const runId = props.snapshot?.run?.id
  return props.toolExecutions.filter((exec) => {
    if (runId && exec.run_id && exec.run_id !== runId) return false
    if (node.roleTier === 'coordinator') {
      if (exec.agent_instance_id && node.instanceIds.includes(exec.agent_instance_id)) return true
      return !exec.task_id
    }
    if (node.taskId && exec.task_id === node.taskId) return true
    if (node.taskKey && exec.task_id === node.taskKey) return true
    if (exec.agent_instance_id && node.instanceIds.includes(exec.agent_instance_id)) return true
    return false
  })
})

const filteredContextPacks = computed<ContextPackDto[]>(() => {
  const packs = props.snapshot?.context_packs ?? []
  const node = selectedNode.value
  if (!node) return packs
  if (node.roleTier === 'coordinator') {
    return packs.filter(
      (pack) =>
        !pack.lane_key
        || pack.lane_key === 'planner'
        || Boolean(node.agentSlug && pack.lane_key === node.agentSlug),
    )
  }
  const keys = new Set(
    [node.laneKey, node.taskKey, node.taskId, node.handle, node.agentSlug].filter(
      (v): v is string => Boolean(v && v !== 'main'),
    ),
  )
  return packs.filter((pack) => {
    if (!pack.lane_key) return false
    if (keys.has(pack.lane_key)) return true
    return node.laneKey === 'main' && pack.lane_key === 'main'
  })
})

const filteredStepResults = computed<StepResultDto[]>(() => {
  const results = props.snapshot?.step_results ?? []
  const node = selectedNode.value
  if (!node) return results
  if (node.roleTier === 'coordinator') {
    return results.filter(
      (res) =>
        !res.task_node_id
        || node.instanceIds.includes(res.agent_id)
        || Boolean(node.agentSlug && res.agent_id === node.agentSlug),
    )
  }
  return results.filter((res) => {
    if (node.taskId && (res.task_node_id === node.taskId || res.id === node.taskId)) return true
    if (node.taskKey && (res.task_node_id === node.taskKey || res.id === node.taskKey)) return true
    if (res.agent_id && node.instanceIds.includes(res.agent_id)) return true
    if (node.agentSlug && res.agent_id === node.agentSlug) return true
    return false
  })
})

/**
 * Core caps a page of the invocation audit at 200 and pages by cursor only, so a run total is a
 * client-side walk. Five pages is already 1000 calls: enough for any run that a human reads, and
 * small enough that opening this panel cannot fan out into dozens of requests. Past the cap the
 * panel says it stopped — a walk cut short printed as a total would be a smaller lie than a wrong
 * one only to someone who cannot tell the two apart.
 */
const USAGE_PAGE_SIZE = 200
const USAGE_PAGE_LIMIT = 5

const usage = ref<ModelUsageSummary | null>(null)
const usageState = ref<'idle' | 'loading' | 'ready' | 'unavailable'>('idle')
const loadedModelInvocations = ref<ModelInvocationDto[]>([])
const effectiveModelInvocations = computed<ModelInvocationDto[]>(
  () => props.modelInvocations ?? loadedModelInvocations.value,
)

async function loadModelUsage(runId?: string | null): Promise<void> {
  usage.value = null
  loadedModelInvocations.value = []
  if (!runId) {
    usageState.value = 'idle'
    return
  }
  usageState.value = 'loading'
  try {
    const rows: ModelInvocationDto[] = []
    let cursor: string | undefined
    for (let page = 0; page < USAGE_PAGE_LIMIT; page++) {
      const result = await api.listModelInvocations({ run_id: runId, limit: USAGE_PAGE_SIZE, cursor })
      rows.push(...result.items)
      cursor = result.next_cursor ?? undefined
      if (!cursor) break
    }
    loadedModelInvocations.value = rows
    usage.value = summarizeModelInvocations(rows, { truncated: Boolean(cursor) })
    usageState.value = 'ready'
  } catch {
    loadedModelInvocations.value = []
    usageState.value = 'unavailable'
  }
}

watch(() => props.snapshot?.run?.id, (runId) => { void loadModelUsage(runId) }, { immediate: true })
watch(activeTab, (tab) => {
  if (tab === 'catalog') void loadToolCatalog()
})

async function loadToolCatalog(): Promise<void> {
  if (props.tools?.length || toolCatalogLoading.value) return
  toolCatalogLoading.value = true
  try {
    toolCatalog.value = await api.listTools()
  } catch {
    toolCatalog.value = []
  } finally {
    toolCatalogLoading.value = false
  }
}

function refreshToolCatalog(): void {
  toolCatalog.value = []
  void loadToolCatalog()
}

/** The provider is named only when two rows would otherwise read as one model. */
const ambiguousModels = computed(() => {
  const seen = new Set<string>()
  const shared = new Set<string>()
  for (const group of usage.value?.groups ?? []) {
    const key = group.model ?? ''
    if (seen.has(key)) shared.add(key)
    else seen.add(key)
  }
  return shared
})

function usageGroupLabel(group: ModelUsageGroup): string {
  const name = group.model ?? t('orchestrationPanel.unnamedModel')
  const via = ambiguousModels.value.has(group.model ?? '') ? t('orchestrationPanel.via', { provider: group.providerId.slice(0, 8) }) : ''
  const calls = t('orchestrationPanel.calls', group.calls)
  if (group.totalTokens === null) return t('orchestrationPanel.groupNoUsage', { name, via, calls })
  const partial = group.unpricedCalls > 0 ? t('orchestrationPanel.unreported', { count: group.unpricedCalls }) : ''
  return t('orchestrationPanel.groupTokens', { name, via, tokens: group.totalTokens.toLocaleString(locale.value), partial, calls })
}

function runStatusLabel(status?: string | null): string {
  return status ? statusWords(status) : ''
}

interface RawSupervisionEventFinding {
  decision?: string | null
  revision_round?: number | null
  reasons?: string[] | null
}

function supervisionHeader(finding: SupervisionFindingDto): string {
  const raw = finding as SupervisionFindingDto & RawSupervisionEventFinding
  if (finding.severity || finding.category) {
    return [finding.severity ? severityLabel(finding.severity) : '', finding.category ?? '']
      .filter(Boolean)
      .join(' · ')
  }
  if (raw.decision) {
    return t('orchestrationPanel.supervisionDecisionRound', {
      decision: runStatusLabel(raw.decision),
      round: raw.revision_round ?? 0,
    })
  }
  return runStatusLabel(finding.status)
}

function supervisionBody(finding: SupervisionFindingDto): string {
  if (finding.summary) return finding.summary
  const raw = finding as SupervisionFindingDto & RawSupervisionEventFinding
  return Array.isArray(raw.reasons) ? raw.reasons.join(' · ') : ''
}

/**
 * A context pack has no summary on the wire — the sentence the engine hands `AppendEventAsync` never
 * reaches the event envelope — so the row is labelled from the one field that distinguishes packs:
 * which lane it was assembled for, or none for the main planner.
 */
function packLabel(pack: ContextPackDto): string {
  return pack.lane_key ? t('orchestrationPanel.packLane', { lane: pack.lane_key }) : t('orchestrationPanel.packPlanner')
}

/**
 * One source can contribute several items (`reviewed_memory` adds one per promoted
 * entry), so the cost is summed per name. Listing every item separately would show
 * the same source at several prices and hide the number that actually matters —
 * what that source cost the pack in total.
 */
function sumBySource(shares: ContextBudgetShareDto[] | undefined): Map<string, number> {
  const totals = new Map<string, number>()
  for (const share of shares ?? []) {
    totals.set(share.source, (totals.get(share.source) ?? 0) + share.tokens)
  }
  return totals
}

/**
 * The wire lists one entry per evidence item, so a source that contributed several
 * entries appears several times. It is rendered once, with its summed price: the
 * question this row answers is which sources the model was told, not how many items
 * each one happened to add — the "N evidence" chip already says that.
 */
function sourceNames(pack: ContextPackDto): string[] {
  return [...new Set(pack.sources)]
}

/**
 * Absent on events written before the pack reported costs, so a missing price
 * renders as a bare name rather than as "0 tokens" — the latter would read as a
 * measured free item.
 */
function sourceLabel(pack: ContextPackDto, source: string): string {
  const tokens = sumBySource(pack.source_tokens).get(source)
  return tokens === undefined ? source : `${source} · ${tokens}`
}

function droppedShares(pack: ContextPackDto): Array<{ source: string; tokens: number }> {
  return [...sumBySource(pack.dropped_sources)].map(([source, tokens]) => ({ source, tokens }))
}

function droppedTooltip(pack: ContextPackDto): string {
  const items = droppedShares(pack)
    .map((share) => t('orchestrationPanel.droppedSource', { source: share.source, tokens: share.tokens }))
    .join(', ')
  return `${t('orchestrationPanel.droppedLead')} ${items} ${t('orchestrationPanel.droppedTail')}`
}

function packBudgetPercent(pack: ContextPackDto): number {
  if (!pack.token_budget || pack.token_budget <= 0) return 0
  return Math.min(100, Math.max(0, Math.round((pack.estimated_tokens / pack.token_budget) * 100)))
}

const tabs: Array<{ key: TabKey; icon: typeof ListTree }> = [
  { key: 'timeline', icon: ListTree },
  { key: 'catalog', icon: Package },
  { key: 'stats', icon: BarChart3 },
]

function onRerun(exec: ToolExecutionTimelineItemDto) {
  emit('rerun-tool', exec)
}

function onViewDetails(exec: ToolExecutionTimelineItemDto) {
  emit('view-tool-details', exec)
}

function onExecuteTool(tool: ToolDescriptorDto) {
  emit('execute-tool', tool)
}

const effectiveSnapshotForDiagnostics = computed<OrchestrationSnapshotDto | null>(
  () => props.snapshot ?? lastObservedSnapshot.value,
)

const hasCachedDiagnosticsFallback = computed(
  () => !hasSnapshot.value && Boolean(runDiagnostics.value),
)

const activeCoreDiagnostics = computed<RunContextDiagnosticsDto | null>(
  () => (selectedNode.value ? null : runDiagnostics.value),
)

const trajectorySummary = computed(() =>
  buildTrajectoryTree({
    snapshot: effectiveSnapshotForDiagnostics.value,
    toolExecutions: filteredToolExecutions.value,
    modelInvocations: effectiveModelInvocations.value,
    contextPacks: filteredContextPacks.value,
    coordinatorLabel: t('orchestrationPanel.roleCoordinator'),
    diagnostics: activeCoreDiagnostics.value,
  }),
)

const contextWindowBuckets = computed(() =>
  resolveContextWindowBuckets({
    diagnostics: activeCoreDiagnostics.value,
    isNodeFiltered: Boolean(selectedNode.value),
    contextPacks: filteredContextPacks.value,
    modelInvocations: effectiveModelInvocations.value,
    toolExecutions: filteredToolExecutions.value,
  }),
)

const contextTreemapModel = computed(() =>
  resolveContextTreemapModel({
    diagnostics: activeCoreDiagnostics.value,
    contextPacks: filteredContextPacks.value,
    selectedPackId: selectedTreemapPackId.value,
  }),
)

function onOverlapAuditUpdated(
  audit: ContextOverlapAuditResult,
  meta: { stale: boolean; cachedAt: string | null },
) {
  latestOverlapAudit.value = audit
  if (meta.stale && !diagnosticsCachedAt.value) {
    diagnosticsCachedAt.value = meta.cachedAt
  }
}

function handleDiagnoseRun(overrides?: {
  node?: TrajectoryNode | null
  segment?: ContextTreemapNode | null
  treemap?: ContextTreemapModel | null
  overlap?: ContextOverlapAuditResult | null
  pair?: ContextOverlapPairSummary | null
}) {
  const input = {
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
    trajectory: trajectorySummary.value,
    selectedNode: overrides?.node !== undefined ? overrides.node : selectedTrajectoryNode.value,
    treemap: overrides?.treemap ?? contextTreemapModel.value,
    selectedTreemapSegment: overrides?.segment !== undefined ? overrides.segment : selectedTreemapSegment.value,
    buckets: contextWindowBuckets.value,
    overlap: overrides?.overlap ?? latestOverlapAudit.value,
    selectedOverlapPair: overrides?.pair ?? null,
    diagnostics: activeCoreDiagnostics.value,
    staleSnapshot:
      diagnosticsStale.value && diagnosticsCachedAt.value
        ? { cachedAt: diagnosticsCachedAt.value }
        : null,
  }
  const draft = buildDiagnosticDraft(input)
  emit('diagnose-draft', draft)
  void triggerDiagnoseInComposer(input)
}

function handleExportDiagnosticHtml() {
  const result = exportOfflineDiagnosticHtml({
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
    trajectory: trajectorySummary.value,
    buckets: contextWindowBuckets.value,
    treemap: contextTreemapModel.value,
    overlap: latestOverlapAudit.value,
    diagnostics: activeCoreDiagnostics.value,
    staleSnapshot:
      diagnosticsStale.value && diagnosticsCachedAt.value
        ? { cachedAt: diagnosticsCachedAt.value }
        : null,
  })
  emit('export-report', result)
}
</script>

<template>
  <section class="orchestration-tab">
    <SessionPlanPanel
      v-if="effectiveSessionId"
      :session-id="effectiveSessionId"
      compact
      @focus-node="onFocusPlanNode"
    />

    <div v-if="!hasSnapshot && !hasCachedDiagnosticsFallback" class="orchestration-empty">
      {{ t('orchestrationPanel.empty') }}
    </div>

    <article
      v-else-if="hasCachedDiagnosticsFallback"
      class="orchestration-block"
      data-testid="trajectory-diagnostics-block"
    >
      <div class="orchestration-block-head orchestration-block-head--between orchestration-block-head--wrap">
        <div class="orchestration-block-title">
          <Activity :size="15" />
          <strong>{{ t('trajectoryEval.panel.title') }}</strong>
        </div>
        <div class="diagnostics-head-actions">
          <button
            type="button"
            class="diagnostics-action-btn ui-pressable"
            data-testid="diagnostics-diagnose-btn"
            @click="handleDiagnoseRun()"
          >
            <Sparkles :size="12" aria-hidden="true" />
            <span>{{ t('trajectoryEval.actions.diagnoseInComposer') }}</span>
          </button>
          <button
            type="button"
            class="diagnostics-action-btn ui-pressable"
            data-testid="diagnostics-export-html-btn"
            @click="handleExportDiagnosticHtml"
          >
            <Download :size="12" aria-hidden="true" />
            <span>{{ t('trajectoryEval.panel.exportHtml') }}</span>
          </button>
        </div>
      </div>

      <div class="diagnostics-stale-banner" role="status" data-testid="diagnostics-offline-banner">
        <Database :size="13" aria-hidden="true" />
        <span>{{ t('trajectoryEval.offline.staleBanner') }}</span>
      </div>

      <div
        v-if="runDiagnostics?.executive_findings?.length"
        class="diagnostics-findings-strip"
        data-testid="diagnostics-executive-findings"
      >
        <div
          v-for="finding in runDiagnostics.executive_findings.slice(0, 3)"
          :key="finding.code"
          class="diagnostics-finding-row"
        >
          <span class="diagnostics-finding-code mono">{{ finding.code }}</span>
          <span>{{ finding.summary }}</span>
        </div>
      </div>

      <div
        class="orchestration-tabs"
        role="tablist"
        :aria-label="t('trajectoryEval.panel.tabsLabel')"
        data-testid="diagnostics-subtabs"
      >
        <button
          type="button"
          role="tab"
          class="orchestration-tab-btn"
          :class="{ active: diagnosticsTab === 'tree' }"
          :aria-selected="diagnosticsTab === 'tree'"
          data-testid="diagnostics-tab-tree"
          @click="diagnosticsTab = 'tree'"
        >
          <span>{{ t('trajectoryEval.panel.tabTree') }}</span>
        </button>
        <button
          type="button"
          role="tab"
          class="orchestration-tab-btn"
          :class="{ active: diagnosticsTab === 'treemap' }"
          :aria-selected="diagnosticsTab === 'treemap'"
          data-testid="diagnostics-tab-treemap"
          @click="diagnosticsTab = 'treemap'"
        >
          <span>{{ t('trajectoryEval.panel.tabTreemap') }}</span>
        </button>
        <button
          type="button"
          role="tab"
          class="orchestration-tab-btn"
          :class="{ active: diagnosticsTab === 'overlap' }"
          :aria-selected="diagnosticsTab === 'overlap'"
          data-testid="diagnostics-tab-overlap"
          @click="diagnosticsTab = 'overlap'"
        >
          <span>{{ t('trajectoryEval.panel.tabOverlap') }}</span>
        </button>
      </div>

      <div v-if="diagnosticsTab === 'tree'" class="diagnostics-pane">
        <TrajectoryTreePanel
          :trajectory="trajectorySummary"
          :selected-node-id="selectedTrajectoryNode?.id ?? null"
          compact
          @select-node="selectedTrajectoryNode = $event"
          @diagnose-node="handleDiagnoseRun({ node: $event })"
        />
        <ContextWindowBucketChart
          :report="contextWindowBuckets"
          :diagnostics="activeCoreDiagnostics"
          stale
          compact
        />
      </div>

      <div v-else-if="diagnosticsTab === 'treemap'" class="diagnostics-pane">
        <ContextTreemap
          :context-packs="filteredContextPacks"
          :diagnostics="activeCoreDiagnostics"
          stale
          compact
          @select-pack="selectedTreemapPackId = $event"
          @select-segment="selectedTreemapSegment = $event"
          @diagnose-segment="(segment, model) => handleDiagnoseRun({ segment, treemap: model })"
        />
      </div>

      <div v-else class="diagnostics-pane">
        <ContextOverlapPanel
          :run-id="effectiveRunId"
          auto-load-mode="all"
          :can-access-backend="canAccessBackend"
          :tools="tools ?? toolCatalog"
          compact
          @audit-updated="onOverlapAuditUpdated"
          @diagnose-overlap="(audit, pair) => handleDiagnoseRun({ overlap: audit, pair })"
        />
      </div>
    </article>

    <template v-else-if="snapshot">
      <article class="orchestration-block" data-testid="runtime-topology-block">
        <div class="orchestration-block-head orchestration-block-head--between orchestration-block-head--wrap">
          <div class="orchestration-block-title">
            <Network :size="15" />
            <strong>{{ t('orchestrationPanel.runtimeTopology') }}</strong>
          </div>
          <div class="topology-head-actions">
            <div
              class="topology-view-switcher"
              role="tablist"
              :aria-label="t('orchestrationPanel.viewSwitcherLabel')"
              data-testid="topology-view-switcher"
            >
              <button
                type="button"
                role="tab"
                class="topology-view-btn"
                :class="{ active: topologyViewMode === 'topology' }"
                :aria-selected="topologyViewMode === 'topology'"
                data-testid="topology-view-btn-topology"
                @click="topologyViewMode = 'topology'"
              >
                {{ t('orchestrationPanel.viewTopology') }}
              </button>
              <button
                type="button"
                role="tab"
                class="topology-view-btn"
                :class="{ active: topologyViewMode === 'timeline' }"
                :aria-selected="topologyViewMode === 'timeline'"
                data-testid="topology-view-btn-timeline"
                @click="topologyViewMode = 'timeline'"
              >
                {{ t('orchestrationPanel.viewTimeline') }}
              </button>
            </div>

            <button
              v-if="selectedNode"
              type="button"
              class="topology-clear-btn"
              data-testid="topology-clear-filter"
              @click="clearNodeFilter"
            >
              <X :size="11" />
              <span>{{ t('orchestrationPanel.allNodes') }}</span>
            </button>
          </div>
        </div>

        <OrchestrationMiniGraph
          v-if="topologyViewMode === 'topology'"
          :nodes="runtimeTopology.nodes"
          :edges="runtimeTopology.edges"
          :run-id="snapshot.run?.id ?? activeTopologyRun?.run_id ?? null"
          :selected-node-id="selectedNodeId"
          @select-node="onSelectTopologyNode"
        />
        <AgentTimeline
          v-else
          :snapshot="snapshot"
          :topology-run="activeTopologyRun"
          :tool-executions="toolExecutions"
          :model-invocations="effectiveModelInvocations"
          :selected-node-id="selectedNodeId"
          @select-node="onSelectTimelineNodeId"
        />

        <div v-if="selectedNode" ref="detailsBlockRef" class="focused-node-card" data-testid="focused-node-details">
          <div class="focused-node-card__head">
            <span class="focused-node-card__lead" :title="t('orchestrationPanel.filteringNode', { name: selectedNode.label })">
              {{ t('orchestrationPanel.filteringNode', { name: selectedNode.label }) }}
            </span>
            <span class="focused-node-card__tier">{{ roleTierLabel(selectedNode.roleTier) }}</span>
            <span class="focused-node-card__status" :class="`is-${selectedNode.status}`">
              {{ runStatusLabel(selectedNode.status === 'awaiting_approval' ? 'awaiting_approval' : (selectedNode.rawStatus || selectedNode.status)) }}
            </span>
            <button
              v-if="canSteerSelectedNode"
              type="button"
              class="focused-node-card__steer-btn ui-pressable"
              data-testid="focused-node-steer-btn"
              :title="tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: selectedNode.label })"
              @click.stop="openSelectedNodeSteer($event)"
            >
              <MessageSquare :size="13" aria-hidden="true" />
              <span>{{ tr('teammateControl.steerAction', 'Direct control') }}</span>
            </button>
          </div>
          <div class="orchestration-tags">
            <span
              v-if="selectedParentNode"
              :title="t('orchestrationPanel.dispatchedBy', { parent: selectedParentNode.label })"
            >{{ t('orchestrationPanel.dispatchedBy', { parent: selectedParentNode.label }) }}</span>
            <span v-if="selectedNode.handle" :title="selectedNode.handle">{{ selectedNode.handle }}</span>
            <span
              v-if="selectedNode.laneKey"
              :title="t('orchestrationPanel.laneBadge', { lane: selectedNode.laneKey })"
            >{{ t('orchestrationPanel.laneBadge', { lane: selectedNode.laneKey }) }}</span>
          </div>
          <p v-if="selectedNode.title && selectedNode.title !== selectedNode.label" class="focused-node-card__title">
            {{ selectedNode.title }}
          </p>
          <p v-if="selectedNode.description" class="focused-node-card__desc">
            {{ selectedNode.description }}
          </p>
          <p v-if="selectedNode.resultSummary" class="focused-node-card__result">
            {{ selectedNode.resultSummary }}
          </p>
          <div v-if="selectedNode.writeScope.length > 0" class="focused-node-card__scope">
            <small>{{ t('orchestrationPanel.writeScopeLabel') }}</small>
            <div class="orchestration-tags">
              <span v-for="scopePath in selectedNode.writeScope" :key="scopePath" :title="scopePath">{{ scopePath }}</span>
            </div>
          </div>
        </div>
        <p v-else class="topology-hint">
          {{ t('orchestrationPanel.clickNodeHint') }}
        </p>
      </article>

      <article v-if="snapshot.graph" class="orchestration-block">
        <div class="orchestration-block-head">
          <GitBranch :size="15" />
          <strong>{{ t('orchestrationPanel.modeGraph') }}</strong>
        </div>
        <DeclaredGraphCanvas :graph="snapshot.graph" :flows="snapshot.flows" />
      </article>

      <article class="orchestration-block">
        <div class="orchestration-block-head">
          <Layers3 :size="15" />
          <strong>{{ t('orchestrationPanel.run') }}</strong>
        </div>
        <div class="orchestration-tags">
          <!-- Core's run summary is its English narration of the last transition: diagnostic, so a tooltip. -->
          <span :title="snapshot.run?.summary || undefined">{{ runStatusLabel(snapshot.run?.status) }}</span>
          <span>{{ t('orchestrationPanel.nodes', snapshot.nodes.length) }}</span>
          <span>{{ t('orchestrationPanel.assignments', snapshot.assignments.length) }}</span>
        </div>
      </article>

      <article class="orchestration-block">
        <div class="orchestration-block-head">
          <AlertTriangle :size="15" />
          <strong>{{ t('orchestrationPanel.supervision') }}</strong>
        </div>
        <div v-if="snapshot.supervision_findings.length === 0" class="quiet">
          {{ t('orchestrationPanel.noFindings') }}
        </div>
        <div v-for="finding in snapshot.supervision_findings" :key="finding.id" class="orchestration-finding">
          <span>{{ supervisionHeader(finding) }}</span>
          <p v-if="supervisionBody(finding)">{{ supervisionBody(finding) }}</p>
          <small v-if="finding.recommendation">{{ finding.recommendation }}</small>
        </div>
      </article>

      <article class="orchestration-block" data-testid="tool-executions-block">
        <div class="orchestration-block-head orchestration-block-head--between">
          <div class="orchestration-block-title">
            <Wrench :size="15" />
            <strong>{{ t('orchestrationPanel.toolExecutions') }}</strong>
          </div>
          <span v-if="selectedNode" class="filter-scope-pill" :title="selectedNode.label">
            {{ selectedNode.label }}
          </span>
        </div>

        <div class="orchestration-tabs">
          <button
            v-for="tab in tabs"
            :key="tab.key"
            type="button"
            class="orchestration-tab-btn"
            :class="{ active: activeTab === tab.key }"
            @click="activeTab = tab.key"
          >
            <component :is="tab.icon" :size="13" />
            <span>{{ t(`orchestrationPanel.tabs.${tab.key}`) }}</span>
          </button>
        </div>

        <div class="orchestration-tab-content">
          <ToolExecutionTimeline
            v-if="activeTab === 'timeline'"
            :tool-executions="filteredToolExecutions"
            @rerun="onRerun"
            @view-details="onViewDetails"
          />
          <ToolCatalogBrowser
            v-else-if="activeTab === 'catalog'"
            :tools="tools ?? toolCatalog"
            @execute="onExecuteTool"
          />
          <ToolStatsDashboard
            v-else-if="activeTab === 'stats'"
            :tool-executions="filteredToolExecutions"
          />
        </div>
        <button
          v-if="activeTab === 'catalog'"
          type="button"
          class="orchestration-refresh"
          :disabled="toolCatalogLoading"
          :title="t('orchestrationPanel.refreshToolsHint')"
          @click="refreshToolCatalog"
        >
          <RefreshCw :size="12" :class="{ spinning: toolCatalogLoading }" />
          <span>{{ t('orchestrationPanel.refreshTools') }}</span>
        </button>
      </article>

      <article class="orchestration-block" data-testid="context-packs-block">
        <div class="orchestration-block-head orchestration-block-head--between">
          <div class="orchestration-block-title">
            <Archive :size="15" />
            <strong>{{ t('orchestrationPanel.contextPacks') }}</strong>
          </div>
          <span v-if="selectedNode" class="filter-scope-pill" :title="selectedNode.label">
            {{ selectedNode.label }}
          </span>
        </div>
        <div v-if="snapshot.context_packs.length === 0" class="quiet">
          {{ t('orchestrationPanel.noContextPacks') }}
        </div>
        <div v-else-if="filteredContextPacks.length === 0 && selectedNode" class="quiet" data-testid="context-packs-filtered-empty">
          {{ t('orchestrationPanel.noNodeContextPacks', { name: selectedNode.label }) }}
        </div>
        <div v-for="pack in filteredContextPacks" :key="pack.id" class="context-pack-row" data-testid="context-pack-row">
          <div class="context-pack-row__head">
            <p class="context-pack-row__title" :title="packLabel(pack)">{{ packLabel(pack) }}</p>
            <span
              v-if="droppedShares(pack).length"
              class="context-pack-truncated-badge"
            >{{ t('orchestrationPanel.budgetTruncatedBadge') }}</span>
          </div>
          <div class="orchestration-tags">
            <span :title="t('orchestrationPanel.evidence', { count: pack.evidence_count })">
              {{ t('orchestrationPanel.evidence', { count: pack.evidence_count }) }}
            </span>
            <span :title="t('orchestrationPanel.tokens', { used: pack.estimated_tokens, budget: pack.token_budget })">
              {{ t('orchestrationPanel.tokens', { used: pack.estimated_tokens, budget: pack.token_budget }) }}
            </span>
          </div>
          <div v-if="pack.token_budget > 0" class="context-pack-meter" aria-hidden="true">
            <div
              class="context-pack-meter__fill"
              :class="{ 'is-truncated': droppedShares(pack).length > 0 }"
              :style="{ width: `${packBudgetPercent(pack)}%` }"
            />
          </div>
          <div
            v-if="pack.sources.length"
            class="orchestration-tags"
            role="list"
            :aria-label="t('orchestrationPanel.sourcesLabel')"
            data-testid="context-pack-sources"
          >
            <span
              v-for="source in sourceNames(pack)"
              :key="source"
              role="listitem"
              data-testid="context-pack-source"
              :title="sourceLabel(pack, source)"
            >{{ sourceLabel(pack, source) }}</span>
          </div>
          <p v-else class="quiet context-pack-note" :title="t('orchestrationPanel.noSourceList')">
            {{ t('orchestrationPanel.noSourceList') }}
          </p>
          <!-- Available but cut: the pack answered "which sources" without saying which
               ones the budget removed, and those are two different answers to "why did the
               agent ignore the project rules". -->
          <div
            v-if="droppedShares(pack).length"
            class="context-pack-dropped"
            data-testid="context-pack-dropped"
            :title="droppedTooltip(pack)"
          >
            <span class="context-pack-dropped__lead">{{ t('orchestrationPanel.droppedLead') }}</span>
            <div class="context-pack-dropped__chips">
              <span
                v-for="share in droppedShares(pack)"
                :key="share.source"
                class="context-pack-dropped__chip"
                data-testid="context-pack-dropped-source"
                :title="t('orchestrationPanel.droppedSource', { source: share.source, tokens: share.tokens })"
              >{{ t('orchestrationPanel.droppedSource', { source: share.source, tokens: share.tokens }) }}</span>
            </div>
            <span class="context-pack-dropped__tail">{{ t('orchestrationPanel.droppedTail') }}</span>
          </div>
        </div>
      </article>

      <article class="orchestration-block" data-testid="trajectory-diagnostics-block">
        <div class="orchestration-block-head orchestration-block-head--between orchestration-block-head--wrap">
          <div class="orchestration-block-title">
            <Activity :size="15" />
            <strong>{{ t('trajectoryEval.panel.title') }}</strong>
          </div>
          <div class="diagnostics-head-actions">
            <button
              type="button"
              class="diagnostics-action-btn ui-pressable"
              data-testid="diagnostics-diagnose-btn"
              @click="handleDiagnoseRun()"
            >
              <Sparkles :size="12" aria-hidden="true" />
              <span>{{ t('trajectoryEval.actions.diagnoseInComposer') }}</span>
            </button>
            <button
              type="button"
              class="diagnostics-action-btn ui-pressable"
              data-testid="diagnostics-export-html-btn"
              @click="handleExportDiagnosticHtml"
            >
              <Download :size="12" aria-hidden="true" />
              <span>{{ t('trajectoryEval.panel.exportHtml') }}</span>
            </button>
          </div>
        </div>

        <div
          v-if="diagnosticsStale"
          class="diagnostics-stale-banner"
          role="status"
          data-testid="diagnostics-offline-banner"
        >
          <Database :size="13" aria-hidden="true" />
          <span>{{ t('trajectoryEval.offline.staleBanner') }}</span>
        </div>

        <div
          v-if="activeCoreDiagnostics?.executive_findings?.length"
          class="diagnostics-findings-strip"
          data-testid="diagnostics-executive-findings"
        >
          <div
            v-for="finding in activeCoreDiagnostics.executive_findings.slice(0, 3)"
            :key="finding.code"
            class="diagnostics-finding-row"
          >
            <span class="diagnostics-finding-code mono">{{ finding.code }}</span>
            <span>{{ finding.summary }}</span>
          </div>
        </div>

        <div
          class="orchestration-tabs"
          role="tablist"
          :aria-label="t('trajectoryEval.panel.tabsLabel')"
          data-testid="diagnostics-subtabs"
        >
          <button
            type="button"
            role="tab"
            class="orchestration-tab-btn"
            :class="{ active: diagnosticsTab === 'tree' }"
            :aria-selected="diagnosticsTab === 'tree'"
            data-testid="diagnostics-tab-tree"
            @click="diagnosticsTab = 'tree'"
          >
            <span>{{ t('trajectoryEval.panel.tabTree') }}</span>
          </button>
          <button
            type="button"
            role="tab"
            class="orchestration-tab-btn"
            :class="{ active: diagnosticsTab === 'treemap' }"
            :aria-selected="diagnosticsTab === 'treemap'"
            data-testid="diagnostics-tab-treemap"
            @click="diagnosticsTab = 'treemap'"
          >
            <span>{{ t('trajectoryEval.panel.tabTreemap') }}</span>
          </button>
          <button
            type="button"
            role="tab"
            class="orchestration-tab-btn"
            :class="{ active: diagnosticsTab === 'overlap' }"
            :aria-selected="diagnosticsTab === 'overlap'"
            data-testid="diagnostics-tab-overlap"
            @click="diagnosticsTab = 'overlap'"
          >
            <span>{{ t('trajectoryEval.panel.tabOverlap') }}</span>
          </button>
        </div>

        <div v-if="diagnosticsTab === 'tree'" class="diagnostics-pane">
          <TrajectoryTreePanel
            :trajectory="trajectorySummary"
            :selected-node-id="selectedTrajectoryNode?.id ?? null"
            compact
            @select-node="selectedTrajectoryNode = $event"
            @diagnose-node="handleDiagnoseRun({ node: $event })"
          />
          <ContextWindowBucketChart
            :report="contextWindowBuckets"
            :diagnostics="activeCoreDiagnostics"
            :stale="diagnosticsStale"
            compact
          />
        </div>

        <div v-else-if="diagnosticsTab === 'treemap'" class="diagnostics-pane">
          <ContextTreemap
            :context-packs="filteredContextPacks"
            :diagnostics="activeCoreDiagnostics"
            :stale="diagnosticsStale"
            compact
            @select-pack="selectedTreemapPackId = $event"
            @select-segment="selectedTreemapSegment = $event"
            @diagnose-segment="(segment, model) => handleDiagnoseRun({ segment, treemap: model })"
          />
        </div>

        <div v-else class="diagnostics-pane">
          <ContextOverlapPanel
            :run-id="effectiveRunId"
            auto-load-mode="all"
            :can-access-backend="canAccessBackend"
            :tools="tools ?? toolCatalog"
            compact
            @audit-updated="onOverlapAuditUpdated"
            @diagnose-overlap="(audit, pair) => handleDiagnoseRun({ overlap: audit, pair })"
          />
        </div>
      </article>

      <article class="orchestration-block" data-testid="model-usage">
        <div class="orchestration-block-head">
          <BarChart3 :size="15" />
          <strong>{{ t('orchestrationPanel.modelUsage') }}</strong>
        </div>
        <div v-if="usageState === 'loading'" class="quiet">
          {{ t('orchestrationPanel.usageLoading') }}
        </div>
        <div v-else-if="usageState === 'unavailable'" class="quiet" data-testid="model-usage-unavailable">
          {{ t('orchestrationPanel.usageUnavailable') }}
        </div>
        <div v-else-if="!usage || usage.calls === 0" class="quiet" data-testid="model-usage-empty">
          {{ t('orchestrationPanel.usageEmpty') }}
        </div>
        <template v-else>
          <p
            v-if="usage.truncated"
            class="quiet usage-note"
            data-testid="model-usage-truncated"
            :title="t('orchestrationPanel.usageTruncated', { limit: USAGE_PAGE_LIMIT * USAGE_PAGE_SIZE })"
          >
            {{ t('orchestrationPanel.usageTruncated', { limit: USAGE_PAGE_LIMIT * USAGE_PAGE_SIZE }) }}
          </p>
          <div
            class="orchestration-tags model-usage-chips"
            role="list"
            :aria-label="t('orchestrationPanel.usageGroupsLabel')"
          >
            <span
              v-for="group in usage.groups"
              :key="group.key"
              role="listitem"
              class="model-usage-chip"
              data-testid="model-usage-group"
              :title="usageGroupLabel(group)"
            >{{ usageGroupLabel(group) }}</span>
          </div>
          <p
            v-if="usage.unpricedCalls"
            class="quiet usage-note"
            data-testid="model-usage-unpriced"
            :title="t('orchestrationPanel.usageUnpriced', { unpriced: usage.unpricedCalls, calls: usage.calls })"
          >
            {{ t('orchestrationPanel.usageUnpriced', { unpriced: usage.unpricedCalls, calls: usage.calls }) }}
          </p>
          <p class="quiet usage-note">
            {{ t('orchestrationPanel.tokensOnly') }}
          </p>
        </template>
      </article>

      <article class="orchestration-block" data-testid="step-results-block">
        <div class="orchestration-block-head orchestration-block-head--between">
          <div class="orchestration-block-title">
            <CheckCircle2 :size="15" />
            <strong>{{ t('orchestrationPanel.stepResults') }}</strong>
          </div>
          <span v-if="selectedNode" class="filter-scope-pill" :title="selectedNode.label">
            {{ selectedNode.label }}
          </span>
        </div>
        <div v-if="snapshot.step_results.length === 0" class="quiet">
          {{ t('orchestrationPanel.noStepResults') }}
        </div>
        <div v-else-if="filteredStepResults.length === 0 && selectedNode" class="quiet" data-testid="step-results-filtered-empty">
          {{ t('orchestrationPanel.noNodeStepResults', { name: selectedNode.label }) }}
        </div>
        <div v-for="result in filteredStepResults" :key="result.id" class="step-result-row" data-testid="step-result-row">
          <span>{{ runStatusLabel(result.status) }}</span>
          <p>{{ result.summary }}</p>
        </div>
      </article>
    </template>

    <TeammateSteerPopover
      v-if="focusedSteerTarget"
      v-model:open="focusedSteerOpen"
      :run-id="focusedSteerTarget.runId"
      :handle="focusedSteerTarget.handle"
      :display-name="focusedSteerTarget.displayName"
      :status="focusedSteerTarget.status"
      :paused="focusedSteerTarget.paused"
      :task-summary="focusedSteerTarget.taskSummary"
      :anchor-rect="focusedSteerTarget.anchorRect"
    />
  </section>
</template>

<style scoped>
.orchestration-tab {
  display: grid;
  gap: 10px;
  padding: 12px;
  min-width: 0;
}

.orchestration-block {
  border-bottom: 1px solid var(--border-muted);
  display: grid;
  gap: 8px;
  padding-bottom: 12px;
  min-width: 0;
}

.orchestration-block-head {
  align-items: center;
  color: var(--text-primary);
  display: flex;
  gap: 7px;
  min-width: 0;
}

.orchestration-block-head--between {
  justify-content: space-between;
}

.orchestration-block-head--wrap {
  flex-wrap: wrap;
}

.topology-head-actions {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  min-width: 0;
}

.topology-view-switcher {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.topology-view-btn {
  padding: 2px 8px;
  border-radius: 4px;
  border: 1px solid transparent;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  line-height: 1.3;
  cursor: pointer;
  white-space: nowrap;
  transition: background 0.14s ease, color 0.14s ease, border-color 0.14s ease;
}

.topology-view-btn:hover {
  color: var(--text-primary);
}

.topology-view-btn.active {
  color: var(--accent-primary);
  background: var(--surface-active, var(--bg-primary));
  border-color: var(--border-muted);
}

.orchestration-block-title {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
}

.orchestration-block-title strong,
.orchestration-block-head > strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.orchestration-block p {
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.4;
  margin: 3px 0 0;
  min-width: 0;
  overflow-wrap: anywhere;
}

.topology-clear-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
  padding: 2px 8px;
  font-size: 11px;
  font-weight: 600;
  line-height: 1.35;
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary, #58a6ff) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-primary, #58a6ff) 45%, var(--border-muted));
  border-radius: 999px;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}

.topology-clear-btn:hover {
  background: color-mix(in srgb, var(--accent-primary, #58a6ff) 20%, transparent);
}

.topology-hint {
  margin: 0 !important;
  font-size: 11px !important;
  line-height: 1.35 !important;
  color: var(--text-muted) !important;
}

.filter-scope-pill {
  max-width: 120px;
  flex-shrink: 1;
  padding: 1px 7px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  line-height: 1.35;
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary, #58a6ff) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-primary, #58a6ff) 35%, var(--border-muted));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.focused-node-card {
  background: var(--surface-section);
  border: 1px solid color-mix(in srgb, var(--accent-primary, #58a6ff) 40%, var(--border-muted));
  border-radius: 8px;
  display: grid;
  gap: 6px;
  padding: 8px 10px;
  min-width: 0;
}

.focused-node-card__head {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.focused-node-card__lead {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  font-weight: 700;
  line-height: 1.35;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.focused-node-card__tier {
  flex-shrink: 0;
  font-size: 10px;
  font-weight: 600;
  line-height: 1.3;
  padding: 1px 6px;
  border-radius: 4px;
  color: var(--text-secondary);
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
}

.focused-node-card__status {
  flex-shrink: 0;
  font-size: 10px;
  font-weight: 700;
  line-height: 1.3;
  padding: 1px 6px;
  border-radius: 999px;
  color: var(--text-muted);
  background: var(--surface-raised);
}

.focused-node-card__status.is-running {
  color: var(--accent-primary, #58a6ff);
  background: color-mix(in srgb, var(--accent-primary, #58a6ff) 15%, transparent);
}

.focused-node-card__status.is-awaiting_approval {
  color: var(--accent-warning, #d29922);
  background: color-mix(in srgb, var(--accent-warning, #d29922) 16%, transparent);
}

.focused-node-card__status.is-completed {
  color: var(--accent-success, #3fb950);
  background: color-mix(in srgb, var(--accent-success, #3fb950) 15%, transparent);
}

.focused-node-card__status.is-failed {
  color: var(--accent-danger, #f85149);
  background: color-mix(in srgb, var(--accent-danger, #f85149) 15%, transparent);
}

.focused-node-card__steer-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-shrink: 0;
  height: 24px;
  padding: 0 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-default);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  line-height: 1;
  cursor: pointer;
}

.focused-node-card__steer-btn:hover {
  background: var(--bg-hover);
}

.focused-node-card__title {
  margin: 0 !important;
  font-weight: 600;
  color: var(--text-primary) !important;
}

.focused-node-card__desc,
.focused-node-card__result {
  margin: 0 !important;
}

.focused-node-card__scope {
  display: grid;
  gap: 4px;
  min-width: 0;
}

.focused-node-card__scope small {
  font-size: 10px;
  font-weight: 600;
  color: var(--text-muted);
}

/* Narrow-dock safe chip layout: single-line pills with clean ellipsis and stable line-height */
.orchestration-tags {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 5px;
  color: var(--text-muted);
  font-size: 11px;
  min-width: 0;
  max-width: 100%;
}

.orchestration-tags span {
  display: inline-block;
  max-width: 100%;
  min-width: 0;
  box-sizing: border-box;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  padding: 2px 8px;
  line-height: 1.35;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.model-usage-chips {
  gap: 6px;
}

.model-usage-chip {
  color: var(--text-secondary);
}

.usage-note {
  font-size: 11px !important;
  line-height: 1.4 !important;
  margin: 2px 0 0 !important;
}

.orchestration-finding {
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  display: grid;
  gap: 5px;
  padding: 9px;
  min-width: 0;
}

.orchestration-finding span {
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
  line-height: 1.35;
}

.orchestration-finding small {
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.35;
  overflow-wrap: anywhere;
}

.quiet {
  color: var(--text-muted);
  font-size: 12px;
  line-height: 1.4;
}

.orchestration-tabs {
  display: flex;
  gap: 2px;
  padding: 2px;
  background: var(--surface-raised);
  border-radius: 6px;
  min-width: 0;
}

.orchestration-tab-btn {
  flex: 1 1 0;
  min-width: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  padding: 5px 8px;
  font-size: 11px;
  font-weight: 600;
  line-height: 1.3;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.orchestration-tab-btn span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.orchestration-tab-btn:hover {
  color: var(--text-primary);
}

.orchestration-tab-btn.active {
  color: var(--accent-primary);
  background: var(--bg-primary);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.1);
}

.orchestration-tab-content {
  min-height: 200px;
  min-width: 0;
}

.orchestration-refresh {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  align-self: start;
  padding: 4px 10px;
  font-size: 11px;
  font-weight: 600;
  line-height: 1.35;
  color: var(--text-secondary);
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  cursor: pointer;
}

.orchestration-refresh:hover:not(:disabled) {
  color: var(--text-primary);
}

.orchestration-refresh:disabled {
  opacity: 0.6;
  cursor: default;
}

.context-pack-row,
.step-result-row {
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  display: grid;
  gap: 6px;
  padding: 9px;
  min-width: 0;
}

.context-pack-row__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  min-width: 0;
}

.context-pack-row__title {
  margin: 0 !important;
  flex: 1;
  min-width: 0;
  font-weight: 600;
  color: var(--text-primary) !important;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.context-pack-truncated-badge {
  flex-shrink: 0;
  font-size: 10px;
  font-weight: 700;
  line-height: 1.3;
  padding: 1px 6px;
  border-radius: 999px;
  color: var(--accent-warning, #d29922);
  background: color-mix(in srgb, var(--accent-warning, #d29922) 15%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d29922) 40%, var(--border-muted));
}

.context-pack-meter {
  height: 3px;
  width: 100%;
  border-radius: 999px;
  background: var(--surface-raised);
  overflow: hidden;
}

.context-pack-meter__fill {
  height: 100%;
  border-radius: 999px;
  background: var(--accent-primary, #58a6ff);
  transition: width 0.2s ease;
}

.context-pack-meter__fill.is-truncated {
  background: var(--accent-warning, #d29922);
}

.context-pack-note {
  margin: 0 !important;
  font-size: 11px !important;
  line-height: 1.4 !important;
}

.context-pack-dropped {
  display: grid;
  gap: 4px;
  padding: 6px 8px;
  border-radius: 6px;
  background: color-mix(in srgb, var(--accent-warning, #d29922) 8%, var(--surface-raised));
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d29922) 32%, var(--border-muted));
  min-width: 0;
}

.context-pack-dropped__lead {
  font-size: 11px;
  font-weight: 600;
  line-height: 1.35;
  color: var(--text-primary);
}

.context-pack-dropped__chips {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  min-width: 0;
}

.context-pack-dropped__chip {
  display: inline-block;
  max-width: 100%;
  min-width: 0;
  box-sizing: border-box;
  padding: 1px 7px;
  font-size: 10px;
  font-weight: 600;
  line-height: 1.35;
  border-radius: 999px;
  color: var(--accent-warning, #d29922);
  background: var(--surface-section);
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d29922) 35%, var(--border-muted));
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.context-pack-dropped__tail {
  font-size: 11px;
  line-height: 1.35;
  color: var(--text-muted);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.step-result-row p {
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.4;
  margin: 0;
  overflow-wrap: anywhere;
}

.step-result-row span {
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
  line-height: 1.35;
}

.orchestration-empty {
  color: var(--text-muted);
  font-size: 12px;
  line-height: 1.4;
  padding: 16px;
}

.diagnostics-head-actions {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.diagnostics-action-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
  white-space: nowrap;
  transition: border-color 0.14s ease, color 0.14s ease, background-color 0.14s ease;
}

.diagnostics-action-btn:hover {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: var(--bg-primary);
}

.diagnostics-pane {
  display: grid;
  gap: 8px;
  min-width: 0;
}

.diagnostics-stale-banner {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 6px 9px;
  border-radius: 7px;
  border: 1px dashed var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
  line-height: 1.35;
}

.diagnostics-findings-strip {
  display: grid;
  gap: 5px;
  padding: 7px 9px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.diagnostics-finding-row {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-secondary);
  min-width: 0;
}

.diagnostics-finding-code {
  flex-shrink: 0;
  font-family: var(--font-mono);
  font-size: 10px;
  padding: 1px 5px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
}
</style>
