<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Database,
  Download,
  Loader2,
  Sparkles,
  Terminal,
} from '@lucide/vue'
import {
  api,
  type AgentAssignmentDto,
  type ContextPackDto,
  type ModelInvocationDto,
  type OrchestrationSnapshotDto,
  type RunContextDiagnosticsDto,
  type TaskNodeDto,
  type ToolExecutionTimelineItemDto,
  type TopologyRunDto,
} from '@/api'
import ObservabilityTurnBar from '@/components/observability/ObservabilityTurnBar.vue'
import {
  useObservabilityRunSelection,
  type ObservabilityTurnEntry,
} from '@/composables/useObservabilityRunSelection'
import {
  buildOverlapCacheScopeKey,
  fetchRunDiagnosticsWithCache,
  getCachedOverlapAudit,
  getCachedRunDiagnostics,
} from '@/lib/contextDiagnosticsCache'
import {
  fromCoreOverlapAuditDto,
  type ContextOverlapAuditResult,
  type ContextOverlapPairSummary,
} from '@/lib/contextOverlapWinnowing'
import {
  resolveContextTreemapModel,
  type ContextTreemapCategory,
  type ContextTreemapModel,
  type ContextTreemapNode,
} from '@/lib/contextTreemapLayout'
import { activeDockFocus, type DockFocusTarget } from '@/lib/homeDockSync'
import { useHostAccess } from '@/lib/hostAccess'
import {
  exportOfflineDiagnosticHtml,
  triggerDiagnoseInComposer,
  type ExportOfflineHtmlResult,
} from '@/lib/offlineDiagnosticReport'
import {
  buildTrajectoryTree,
  flattenTrajectoryNodes,
  resolveContextWindowBuckets,
  resolveTrajectoryCopy,
  type TrajectoryCodeFrame,
  type TrajectoryNode,
} from '@/lib/trajectoryBuilder'
import ContextCompositionBar, { type ContextCompositionCategoryKey } from './ContextCompositionBar.vue'
import ContextOverlapPanel from './ContextOverlapPanel.vue'
import ContextTreemap from './ContextTreemap.vue'
import ContextWindowBucketChart from './ContextWindowBucketChart.vue'
import TrajectoryDiagnosisPopover from './TrajectoryDiagnosisPopover.vue'
import TrajectoryPathCanvas from './TrajectoryPathCanvas.vue'

export type TrajectorySubTab = 'path' | 'context' | 'eval' | 'code'

interface TrajectoryDockFocusShape extends Partial<DockFocusTarget> {
  trajectoryTab?: TrajectorySubTab | null
  trajectoryTurnNo?: number | null
  trajectoryNodeId?: string | null
  trajectoryCategory?: string | null
}

const props = withDefaults(defineProps<{
  sessionId?: string | null
  runs?: readonly unknown[] | null
  activeRunId?: string | null
  orchestration?: OrchestrationSnapshotDto | null
  toolExecutions?: ToolExecutionTimelineItemDto[] | null
  modelInvocations?: ModelInvocationDto[] | null
  storageId?: string
}>(), {
  sessionId: null,
  runs: () => [],
  activeRunId: null,
  orchestration: null,
  toolExecutions: () => [],
  modelInvocations: null,
  storageId: undefined,
})

const emit = defineEmits<{
  'diagnose-draft': [draft: string]
  'export-report': [result: ExportOfflineHtmlResult]
}>()

const { t, te } = useI18n()
const { canAccessBackend } = useHostAccess()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  return resolveTrajectoryCopy(t, te, key, fallback, params)
}

const effectiveSessionId = computed<string | null>(
  () => props.sessionId ?? props.orchestration?.run?.session_id ?? null,
)

const selection = useObservabilityRunSelection({
  sessionId: () => effectiveSessionId.value,
  runs: () => props.runs,
  activeRunId: () => props.activeRunId ?? props.orchestration?.run?.id ?? null,
})

const effectiveRunId = computed<string | null>(
  () => selection.effectiveRunId.value ?? props.activeRunId ?? props.orchestration?.run?.id ?? null,
)

// Navigation & filter state
const activeSubTab = ref<TrajectorySubTab>('path')
const activeNodeFilterId = ref<string | null>(null)
const focusedTurnNo = ref<number | null>(null)
const focusedContextCategory = ref<string | null>(null)
const focusSignalNonce = ref<number>(0)
const selectedTrajectoryNode = ref<TrajectoryNode | null>(null)
const selectedTreemapSegment = ref<ContextTreemapNode | null>(null)
const selectedTreemapPackId = ref<string | null>(null)
const showOverlapSection = ref<boolean>(false)
const headerDiagnosisOpen = ref<boolean>(false)
const codeDiagnosisNode = ref<TrajectoryNode | null>(null)

// Historical / remote run data loader state
const historicalSnapshot = ref<OrchestrationSnapshotDto | null>(null)
const historicalToolExecutions = ref<ToolExecutionTimelineItemDto[] | null>(null)
const fetchedModelInvocations = ref<ModelInvocationDto[] | null>(null)
const runDiagnostics = ref<RunContextDiagnosticsDto | null>(null)
const diagnosticsStale = ref<boolean>(false)
const diagnosticsCachedAt = ref<string | null>(null)
const latestOverlapAudit = ref<ContextOverlapAuditResult | null>(null)
const loadingHistorical = ref<boolean>(false)

let activeRunAbort: AbortController | null = null
let runLoadEpoch = 0

function synthesizeSnapshotFromTopology(
  topo: TopologyRunDto,
  sessionId: string,
  turnEntry: ObservabilityTurnEntry | null,
): OrchestrationSnapshotDto {
  const startedAt = topo.started_at ?? turnEntry?.startedAt ?? new Date().toISOString()
  const completedAt = topo.completed_at ?? turnEntry?.completedAt ?? startedAt
  const nodes: TaskNodeDto[] = (topo.tasks ?? []).map((task, idx) => ({
    id: task.task_id,
    graph_id: `graph:${topo.run_id}`,
    run_id: topo.run_id,
    session_id: sessionId,
    title: task.title || task.task_key,
    description: task.result_summary ?? '',
    status: task.status,
    lane_key: task.handle || task.task_key || `lane-${idx + 1}`,
    priority: idx,
    risk: 'low',
    success_criteria: [],
    dependencies: task.dependencies ?? [],
    required_capabilities: [],
    created_at: startedAt,
    updated_at: completedAt,
  }))

  const assignments: AgentAssignmentDto[] = (topo.tasks ?? [])
    .filter((task) => task.worker_instance_id || task.agent_slug || task.handle)
    .map((task) => ({
      id: `assign:${task.task_id}`,
      run_id: topo.run_id,
      task_node_id: task.task_id,
      agent_id: task.worker_instance_id ?? task.agent_slug ?? task.task_id,
      agent_name: task.handle ?? task.agent_slug ?? task.title,
      agent_layer: 'execution',
      agent_type: 'worker',
      model_route_purpose: 'default',
      permission_mode: 'governed',
      allowed_tools: [],
      status: task.status,
      created_at: startedAt,
    }))

  return {
    run: {
      id: topo.run_id,
      session_id: sessionId,
      status: topo.status,
      summary: turnEntry?.headline ?? '',
      created_at: startedAt,
      updated_at: completedAt,
    },
    nodes,
    lanes: [],
    assignments,
    step_results: [],
    context_packs: [],
    supervision_findings: [],
  }
}

async function loadRunData(): Promise<void> {
  const targetRunId = effectiveRunId.value
  const sessId = effectiveSessionId.value

  activeRunAbort?.abort()
  activeRunAbort = null

  if (!targetRunId) {
    historicalSnapshot.value = null
    historicalToolExecutions.value = null
    fetchedModelInvocations.value = null
    runDiagnostics.value = null
    diagnosticsStale.value = false
    diagnosticsCachedAt.value = null
    loadingHistorical.value = false
    return
  }

  const snapshotMatchesRun = Boolean(props.orchestration?.run?.id && props.orchestration.run.id === targetRunId)
  if (snapshotMatchesRun) {
    historicalSnapshot.value = null
    historicalToolExecutions.value = null
  }

  // Immediate cache seed to avoid blank screen on offline/tab switch
  if (!runDiagnostics.value || runDiagnostics.value.run_id !== targetRunId) {
    const cached = getCachedRunDiagnostics(targetRunId, { storageId: props.storageId })
    if (cached) {
      runDiagnostics.value = cached.data
      diagnosticsCachedAt.value = cached.cachedAt
      diagnosticsStale.value = !canAccessBackend.value
    } else {
      runDiagnostics.value = null
      diagnosticsCachedAt.value = null
      diagnosticsStale.value = false
    }
  }

  const overlapScope = buildOverlapCacheScopeKey({ runId: targetRunId, autoLoadMode: 'all' })
  const cachedOverlap = getCachedOverlapAudit(overlapScope, {
    runId: targetRunId,
    storageId: props.storageId,
  })
  if (cachedOverlap) {
    latestOverlapAudit.value = fromCoreOverlapAuditDto(cachedOverlap.data)
  }

  const epoch = ++runLoadEpoch
  const controller = new AbortController()
  activeRunAbort = controller
  const needHistoricalFetch = !snapshotMatchesRun
  loadingHistorical.value = needHistoricalFetch

  try {
    const diagPromise =
      typeof api.getRunContextDiagnostics === 'function'
        ? fetchRunDiagnosticsWithCache(
            targetRunId,
            () =>
              api.getRunContextDiagnostics(
                targetRunId,
                { selected_pack_id: selectedTreemapPackId.value },
                controller.signal,
              ),
            { storageId: props.storageId, canAccessBackend: canAccessBackend.value },
          )
        : Promise.resolve(null)

    const topoPromise =
      needHistoricalFetch && sessId && canAccessBackend.value && typeof api.getSessionTopology === 'function'
        ? api.getSessionTopology(
            sessId,
            { run_id: targetRunId, include_finished: true, max_runs: 1, max_tasks: 200 },
            controller.signal,
          )
        : Promise.resolve(null)

    const toolsPromise =
      needHistoricalFetch && sessId && canAccessBackend.value && typeof api.listToolExecutions === 'function'
        ? api.listToolExecutions(sessId, { run_id: targetRunId, limit: 500 }, controller.signal)
        : Promise.resolve(null)

    const needInvocationsFetch = needHistoricalFetch || !props.modelInvocations
    const invocationsPromise =
      needInvocationsFetch && canAccessBackend.value && typeof api.listModelInvocations === 'function'
        ? api.listModelInvocations({ run_id: targetRunId, limit: 200 })
        : Promise.resolve(null)

    const [diagRes, topoRes, toolsRes, invRes] = await Promise.allSettled([
      diagPromise,
      topoPromise,
      toolsPromise,
      invocationsPromise,
    ])

    if (epoch !== runLoadEpoch || controller.signal.aborted) return

    if (diagRes.status === 'fulfilled' && diagRes.value) {
      if (diagRes.value.data) {
        runDiagnostics.value = diagRes.value.data
        diagnosticsStale.value = diagRes.value.stale
        diagnosticsCachedAt.value = diagRes.value.cachedAt
      } else if (diagRes.value.error && runDiagnostics.value?.run_id === targetRunId) {
        diagnosticsStale.value = true
      }
    }

    if (needHistoricalFetch) {
      if (topoRes.status === 'fulfilled' && topoRes.value && sessId) {
        const matchedRun =
          topoRes.value.runs.find((r) => r.run_id === targetRunId) ?? topoRes.value.runs[0] ?? null
        historicalSnapshot.value = matchedRun
          ? synthesizeSnapshotFromTopology(matchedRun, sessId, selection.selectedTurn.value)
          : null
      } else if (needHistoricalFetch && sessId) {
        const turnEntry = selection.selectedTurn.value
        historicalSnapshot.value = turnEntry
          ? {
              run: {
                id: targetRunId,
                session_id: sessId,
                status: turnEntry.status,
                summary: turnEntry.headline ?? '',
                created_at: turnEntry.startedAt ?? new Date().toISOString(),
                updated_at: turnEntry.completedAt ?? turnEntry.startedAt ?? new Date().toISOString(),
              },
              nodes: [],
              lanes: [],
              assignments: [],
              step_results: [],
              context_packs: [],
              supervision_findings: [],
            }
          : null
      }

      if (toolsRes.status === 'fulfilled' && Array.isArray(toolsRes.value)) {
        historicalToolExecutions.value = toolsRes.value
      } else {
        historicalToolExecutions.value = []
      }
    }

    if (invRes.status === 'fulfilled' && invRes.value && Array.isArray(invRes.value.items)) {
      fetchedModelInvocations.value = invRes.value.items
    } else if (needInvocationsFetch) {
      fetchedModelInvocations.value = []
    }
  } finally {
    if (epoch === runLoadEpoch) {
      loadingHistorical.value = false
    }
    if (activeRunAbort === controller) {
      activeRunAbort = null
    }
  }
}

watch(
  () => [effectiveSessionId.value, effectiveRunId.value, selectedTreemapPackId.value, canAccessBackend.value] as const,
  () => {
    void loadRunData()
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  activeRunAbort?.abort()
})

// Deep-link sync from activeDockFocus
watch(
  activeDockFocus,
  (rawFocus) => {
    const focus = rawFocus as TrajectoryDockFocusShape | null
    if (!focus || (focus.card as string) !== 'trajectory') return

    if (focus.runId) {
      selection.selectRun(focus.runId)
    } else if (focus.trajectoryTurnNo && selection.turns.value.some((tItem) => tItem.turnNo === focus.trajectoryTurnNo)) {
      selection.selectTurnByNo(focus.trajectoryTurnNo)
    }

    if (focus.trajectoryTab) {
      activeSubTab.value = focus.trajectoryTab
    } else if (focus.trajectoryCategory) {
      activeSubTab.value = 'context'
    }

    if (focus.trajectoryNodeId !== undefined) {
      activeNodeFilterId.value = focus.trajectoryNodeId || null
    }
    if (focus.trajectoryTurnNo !== undefined) {
      focusedTurnNo.value = focus.trajectoryTurnNo ?? null
    }
    if (focus.trajectoryCategory !== undefined) {
      focusedContextCategory.value = focus.trajectoryCategory ?? null
    }
    focusSignalNonce.value += 1
  },
  { immediate: true },
)

// Resolved active vs historical data
const activeSnapshot = computed<OrchestrationSnapshotDto | null>(() => {
  const targetRunId = effectiveRunId.value
  if (props.orchestration?.run?.id && (!targetRunId || props.orchestration.run.id === targetRunId)) {
    return props.orchestration
  }
  return historicalSnapshot.value
})

const rawToolExecutions = computed<ToolExecutionTimelineItemDto[]>(() => {
  const targetRunId = effectiveRunId.value
  if (props.orchestration?.run?.id && (!targetRunId || props.orchestration.run.id === targetRunId)) {
    const passed = props.toolExecutions ?? []
    return targetRunId ? passed.filter((e) => !e.run_id || e.run_id === targetRunId) : [...passed]
  }
  if (historicalToolExecutions.value !== null) {
    return historicalToolExecutions.value
  }
  const fallback = props.toolExecutions ?? []
  return targetRunId ? fallback.filter((e) => e.run_id === targetRunId) : []
})

const rawModelInvocations = computed<ModelInvocationDto[]>(() => {
  const targetRunId = effectiveRunId.value
  if (props.modelInvocations && (!targetRunId || props.modelInvocations.some((m) => m.run_id === targetRunId))) {
    return targetRunId
      ? props.modelInvocations.filter((m) => !m.run_id || m.run_id === targetRunId)
      : [...props.modelInvocations]
  }
  return fetchedModelInvocations.value ?? []
})

function matchesNodeFilter(
  filterId: string,
  candidate: { taskId?: string | null; agentId?: string | null; id?: string | null; laneKey?: string | null },
): boolean {
  const f = filterId.toLowerCase()
  return Boolean(
    (candidate.id && candidate.id.toLowerCase() === f)
    || (candidate.taskId && candidate.taskId.toLowerCase() === f)
    || (candidate.agentId && candidate.agentId.toLowerCase() === f)
    || (candidate.laneKey && candidate.laneKey.toLowerCase() === f),
  )
}

const filteredSnapshot = computed<OrchestrationSnapshotDto | null>(() => {
  const snap = activeSnapshot.value
  const nf = activeNodeFilterId.value
  if (!snap || !nf) return snap
  const matchingTasks = snap.nodes.filter((tNode) =>
    matchesNodeFilter(nf, { id: tNode.id, taskId: tNode.id, laneKey: tNode.lane_key }),
  )
  const taskIds = new Set(matchingTasks.map((tNode) => tNode.id))
  const matchingAssigns = snap.assignments.filter(
    (a) => taskIds.has(a.task_node_id) || matchesNodeFilter(nf, { agentId: a.agent_id, id: a.id }),
  )
  if (matchingTasks.length === 0 && matchingAssigns.length === 0) return snap
  return {
    ...snap,
    nodes: matchingTasks,
    assignments: matchingAssigns,
    step_results: snap.step_results.filter(
      (sr) => taskIds.has(sr.task_node_id) || matchesNodeFilter(nf, { agentId: sr.agent_id }),
    ),
  }
})

const filteredToolExecutions = computed<ToolExecutionTimelineItemDto[]>(() => {
  const list = rawToolExecutions.value
  const nf = activeNodeFilterId.value
  if (!nf) return list
  const taskIds = new Set((filteredSnapshot.value?.nodes ?? []).map((n) => n.id))
  return list.filter(
    (ex) =>
      (ex.task_id && taskIds.has(ex.task_id))
      || matchesNodeFilter(nf, { id: ex.id, taskId: ex.task_id, agentId: ex.agent_instance_id }),
  )
})

const filteredModelInvocations = computed<ModelInvocationDto[]>(() => {
  const list = rawModelInvocations.value
  const nf = activeNodeFilterId.value
  if (!nf) return list
  return list.filter((inv) => matchesNodeFilter(nf, { id: inv.id, agentId: inv.agent_instance_id }))
})

const filteredContextPacks = computed<ContextPackDto[]>(() => {
  const packs = activeSnapshot.value?.context_packs ?? []
  const nf = activeNodeFilterId.value
  if (!nf) return packs
  const lanes = new Set((filteredSnapshot.value?.nodes ?? []).map((n) => n.lane_key).filter(Boolean))
  if (lanes.size === 0) return packs
  return packs.filter((p) => p.lane_key && lanes.has(p.lane_key))
})

const effectiveCoreDiagnostics = computed<RunContextDiagnosticsDto | null>(() => {
  if (activeNodeFilterId.value) return null
  if (runDiagnostics.value && (!effectiveRunId.value || runDiagnostics.value.run_id === effectiveRunId.value)) {
    return runDiagnostics.value
  }
  return null
})

const trajectorySummary = computed(() =>
  buildTrajectoryTree({
    snapshot: filteredSnapshot.value,
    toolExecutions: filteredToolExecutions.value,
    modelInvocations: filteredModelInvocations.value,
    contextPacks: filteredContextPacks.value,
    coordinatorLabel: tr('orchestrationPanel.roleCoordinator', '主协调器'),
    diagnostics: effectiveCoreDiagnostics.value,
  }),
)

const contextTreemapModel = computed<ContextTreemapModel>(() =>
  resolveContextTreemapModel({
    diagnostics: effectiveCoreDiagnostics.value,
    contextPacks: filteredContextPacks.value,
    selectedPackId: selectedTreemapPackId.value,
  }),
)

const contextWindowBuckets = computed(() =>
  resolveContextWindowBuckets({
    diagnostics: effectiveCoreDiagnostics.value,
    isNodeFiltered: Boolean(activeNodeFilterId.value),
    contextPacks: filteredContextPacks.value,
    modelInvocations: filteredModelInvocations.value,
    toolExecutions: filteredToolExecutions.value,
  }),
)

const activeNodeFilterLabel = computed<string | null>(() => {
  if (!activeNodeFilterId.value) return null
  const matchedTask = activeSnapshot.value?.nodes.find((n) => n.id === activeNodeFilterId.value)
  return matchedTask ? matchedTask.title : activeNodeFilterId.value
})

const compositionTurnLabel = computed<string | null>(() => {
  const turn = selection.selectedTurn.value
  if (!turn) return null
  return tr('trajectoryWorkbench.turnBadge', '回合 #{n}', { n: turn.turnNo })
})

// Wave 1 Code Execution Frame List Fallback
interface CodeExecutionEntry {
  node: TrajectoryNode
  frames: TrajectoryCodeFrame[]
  command: string | null
  isError: boolean
}

const codeExecutionEntries = computed<CodeExecutionEntry[]>(() => {
  const all = flattenTrajectoryNodes(trajectorySummary.value.root)
  const out: CodeExecutionEntry[] = []
  for (const n of all) {
    if (n.kind !== 'tool_call' && n.codeFrames.length === 0) continue
    const idLower = (n.toolId || n.label || '').toLowerCase()
    const isCmd =
      Boolean(n.command)
      || n.codeFrames.length > 0
      || /(bash|pwsh|powershell|cmd|sh|exec|terminal|run_command|python|node|test)/.test(idLower)
    if (isCmd) {
      out.push({
        node: n,
        frames: n.codeFrames,
        command: n.command ?? n.argumentsSummary ?? null,
        isError: n.isError,
      })
    }
  }
  return out
})

const codeErrorCount = computed(() => codeExecutionEntries.value.filter((e) => e.isError).length)
const codeFrameCount = computed(() =>
  codeExecutionEntries.value.reduce((acc, e) => acc + e.frames.length, 0),
)

function clearNodeFilter(): void {
  activeNodeFilterId.value = null
}

function handleCategoryClick(
  compKey: ContextCompositionCategoryKey,
  treemapCat: ContextTreemapCategory,
): void {
  focusedContextCategory.value = compKey === 'inject' ? 'inject' : treemapCat
  activeSubTab.value = 'context'
}

function onOverlapAuditUpdated(
  audit: ContextOverlapAuditResult,
  meta: { stale: boolean; cachedAt: string | null },
): void {
  latestOverlapAudit.value = audit
  if (meta.stale && !diagnosticsCachedAt.value) {
    diagnosticsCachedAt.value = meta.cachedAt
  }
}

function handleQuickDiagnose(overrides?: {
  node?: TrajectoryNode | null
  segment?: ContextTreemapNode | null
  treemap?: ContextTreemapModel | null
  overlap?: ContextOverlapAuditResult | null
  pair?: ContextOverlapPairSummary | null
}): void {
  void triggerDiagnoseInComposer({
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
    trajectory: trajectorySummary.value,
    selectedNode: overrides?.node !== undefined ? overrides.node : selectedTrajectoryNode.value,
    treemap: overrides?.treemap ?? contextTreemapModel.value,
    selectedTreemapSegment: overrides?.segment !== undefined ? overrides.segment : selectedTreemapSegment.value,
    buckets: contextWindowBuckets.value,
    overlap: overrides?.overlap ?? latestOverlapAudit.value,
    selectedOverlapPair: overrides?.pair ?? null,
    diagnostics: effectiveCoreDiagnostics.value,
    staleSnapshot:
      diagnosticsStale.value && diagnosticsCachedAt.value
        ? { cachedAt: diagnosticsCachedAt.value }
        : null,
  }).then((res) => {
    emit('diagnose-draft', res.draft)
  })
}

function handleExportHtml(): void {
  const result = exportOfflineDiagnosticHtml({
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
    trajectory: trajectorySummary.value,
    buckets: contextWindowBuckets.value,
    treemap: contextTreemapModel.value,
    overlap: latestOverlapAudit.value,
    diagnostics: effectiveCoreDiagnostics.value,
    staleSnapshot:
      diagnosticsStale.value && diagnosticsCachedAt.value
        ? { cachedAt: diagnosticsCachedAt.value }
        : null,
  })
  emit('export-report', result)
}

function formatDurationMs(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '0ms'
  if (ms < 1000) return `${Math.round(ms)}ms`
  return `${(ms / 1000).toFixed(1)}s`
}
</script>

<template>
  <section class="twb-root" data-testid="trajectory-workbench-panel">
    <!-- 1. Top Header Zone: Shared Turn Bar + Optional Node Filter Pill + Actions -->
    <header class="twb-topzone">
      <ObservabilityTurnBar
        :turns="selection.turns.value"
        :effective-run-id="effectiveRunId"
        :is-following-latest="selection.isFollowingLatest.value"
        :filter-label="activeNodeFilterLabel"
        @select-run="selection.selectRun"
        @follow-latest="selection.followLatest"
        @clear-filter="clearNodeFilter"
      >
        <template #actions>
          <Loader2
            v-if="loadingHistorical"
            :size="13"
            class="twb-spinner"
            aria-hidden="true"
            data-testid="trajectory-historical-loading"
          />

          <button
            type="button"
            class="twb-icon-btn"
            :title="tr('trajectoryEval.actions.diagnoseInComposer', '带入对话诊断')"
            :aria-label="tr('trajectoryEval.actions.diagnoseInComposer', '带入对话诊断')"
            data-testid="workbench-diagnose-btn"
            @click="headerDiagnosisOpen = !headerDiagnosisOpen"
          >
            <Sparkles :size="13" />
          </button>

          <button
            type="button"
            class="twb-icon-btn"
            :title="tr('trajectoryEval.panel.exportHtml', '导出离线诊断报告')"
            :aria-label="tr('trajectoryEval.panel.exportHtml', '导出离线诊断报告')"
            data-testid="workbench-export-btn"
            @click="handleExportHtml"
          >
            <Download :size="13" />
          </button>
        </template>
      </ObservabilityTurnBar>

      <div v-if="headerDiagnosisOpen" class="twb-header-popover">
        <TrajectoryDiagnosisPopover
          :open="headerDiagnosisOpen"
          :run-id="effectiveRunId"
          :session-id="effectiveSessionId"
          :turn-no="selection.selectedTurn.value?.turnNo ?? null"
          :turn-label="selection.selectedTurn.value?.headline ?? null"
          :node="selectedTrajectoryNode ?? trajectorySummary.root"
          :trajectory="trajectorySummary"
          :treemap="contextTreemapModel"
          :buckets="contextWindowBuckets"
          :diagnostics="effectiveCoreDiagnostics"
          :stale-cached-at="diagnosticsStale ? diagnosticsCachedAt : null"
          @close="headerDiagnosisOpen = false"
          @injected="emit('diagnose-draft', $event.draft)"
        />
      </div>

      <!-- 2. 7-Category Context Composition Bar directly underneath -->
      <ContextCompositionBar
        :treemap="contextTreemapModel"
        :diagnostics="effectiveCoreDiagnostics"
        :model-invocations="filteredModelInvocations"
        :tool-executions="filteredToolExecutions"
        :turn-label="compositionTurnLabel"
        :active-category="activeSubTab === 'context' ? focusedContextCategory : null"
        :stale="diagnosticsStale"
        @select-category="handleCategoryClick"
      />

      <div
        v-if="diagnosticsStale"
        class="twb-offline-banner"
        role="status"
        data-testid="diagnostics-offline-banner"
      >
        <Database :size="12" aria-hidden="true" />
        <span>{{ tr('trajectoryEval.offline.staleBanner', 'Backend connection unavailable — displaying latest cached diagnostic snapshot.') }}</span>
      </div>

      <div
        v-if="effectiveCoreDiagnostics?.executive_findings?.length"
        class="twb-findings-strip"
        data-testid="diagnostics-executive-findings"
      >
        <div
          v-for="finding in effectiveCoreDiagnostics.executive_findings.slice(0, 3)"
          :key="finding.code"
          class="twb-finding-row"
        >
          <span class="twb-finding-code mono">{{ finding.code }}</span>
          <span>{{ finding.summary }}</span>
        </div>
      </div>

      <!-- 3. Geist Underline Sub-Navigation -->
      <nav
        class="twb-subnav"
        role="tablist"
        :aria-label="tr('trajectoryWorkbench.subnavAria', '轨迹子视图')"
        data-testid="trajectory-subnav"
      >
        <button
          type="button"
          role="tab"
          class="twb-subnav__tab"
          :class="{ 'is-active': activeSubTab === 'path' }"
          :aria-selected="activeSubTab === 'path'"
          data-testid="trajectory-tab-path"
          @click="activeSubTab = 'path'"
        >
          {{ tr('trajectoryWorkbench.tabs.path', '执行路径') }}
        </button>
        <button
          type="button"
          role="tab"
          class="twb-subnav__tab"
          :class="{ 'is-active': activeSubTab === 'context' }"
          :aria-selected="activeSubTab === 'context'"
          data-testid="trajectory-tab-context"
          @click="activeSubTab = 'context'"
        >
          {{ tr('trajectoryWorkbench.tabs.context', '上下文矩阵') }}
        </button>
        <button
          type="button"
          role="tab"
          class="twb-subnav__tab"
          :class="{ 'is-active': activeSubTab === 'eval' }"
          :aria-selected="activeSubTab === 'eval'"
          data-testid="trajectory-tab-eval"
          @click="activeSubTab = 'eval'"
        >
          {{ tr('trajectoryWorkbench.tabs.eval', '运行评估') }}
        </button>
        <button
          type="button"
          role="tab"
          class="twb-subnav__tab"
          :class="{ 'is-active': activeSubTab === 'code' }"
          :aria-selected="activeSubTab === 'code'"
          data-testid="trajectory-tab-code"
          @click="activeSubTab = 'code'"
        >
          <span>{{ tr('trajectoryWorkbench.tabs.code', '代码执行') }}</span>
          <span v-if="codeErrorCount > 0" class="twb-subnav__err-dot" aria-hidden="true" />
        </button>
      </nav>
    </header>

    <!-- Main Body Canvas -->
    <div class="twb-body">
      <!-- View 1: Execution Path -->
      <TrajectoryPathCanvas
        v-if="activeSubTab === 'path'"
        :trajectory="trajectorySummary"
        :run-id="effectiveRunId"
        :session-id="effectiveSessionId"
        :treemap="contextTreemapModel"
        :buckets="contextWindowBuckets"
        :diagnostics="effectiveCoreDiagnostics"
        :stale-cached-at="diagnosticsStale ? diagnosticsCachedAt : null"
        :focus-turn-no="focusedTurnNo"
        :focus-node-id="activeNodeFilterId"
        :focus-nonce="focusSignalNonce"
        @select-node="selectedTrajectoryNode = $event"
        @diagnose-draft="emit('diagnose-draft', $event)"
      />

      <!-- View 2: Context Matrix (Treemap + Overlap Audit Subsection) -->
      <div v-else-if="activeSubTab === 'context'" class="twb-pane" data-testid="trajectory-pane-context">
        <ContextTreemap
          :model="contextTreemapModel"
          :context-packs="filteredContextPacks"
          :diagnostics="effectiveCoreDiagnostics"
          :focus-category="focusedContextCategory"
          :stale="diagnosticsStale"
          @select-pack="selectedTreemapPackId = $event"
          @select-segment="selectedTreemapSegment = $event"
          @diagnose-segment="(segment, model) => handleQuickDiagnose({ segment, treemap: model })"
        />

        <div class="twb-overlap-toggle-row">
          <button
            type="button"
            class="twb-overlap-switch"
            :aria-expanded="showOverlapSection"
            data-testid="context-overlap-subsection-toggle"
            @click="showOverlapSection = !showOverlapSection"
          >
            <span>{{ tr('trajectoryWorkbench.matrix.overlapSubsection', '上下文重合审计 (64-Char Winnowing)') }}</span>
            <span class="mono">{{ showOverlapSection ? '−' : '+' }}</span>
          </button>
        </div>

        <ContextOverlapPanel
          v-if="showOverlapSection"
          :run-id="effectiveRunId"
          auto-load-mode="all"
          :can-access-backend="canAccessBackend"
          compact
          @audit-updated="onOverlapAuditUpdated"
          @diagnose-overlap="(audit, pair) => handleQuickDiagnose({ overlap: audit, pair })"
        />
      </div>

      <!-- View 3: Run Eval (Wave 1 Geist Summary + 20-Bucket Occupancy Chart) -->
      <div v-else-if="activeSubTab === 'eval'" class="twb-pane" data-testid="trajectory-pane-eval">
        <div class="twb-kpi-strip mono">
          <div class="twb-kpi-cell">
            <span class="twb-kpi-cell__label">{{ tr('trajectoryEval.summary.duration', '总耗时') }}</span>
            <strong>{{ formatDurationMs(trajectorySummary.totalDurationMs) }}</strong>
          </div>
          <div class="twb-kpi-cell">
            <span class="twb-kpi-cell__label">{{ tr('trajectoryEval.summary.calls', '模型 / 工具') }}</span>
            <strong>{{ trajectorySummary.totalModelCalls }} / {{ trajectorySummary.totalToolCalls }}</strong>
          </div>
          <div class="twb-kpi-cell">
            <span class="twb-kpi-cell__label">{{ tr('trajectoryEval.summary.errors', '异常调用') }}</span>
            <strong :class="{ 'is-danger': trajectorySummary.errorCount > 0 }">
              {{ trajectorySummary.errorCount }}
            </strong>
          </div>
          <div class="twb-kpi-cell">
            <span class="twb-kpi-cell__label">{{ tr('trajectoryEval.summary.peakWindow', '峰值窗口') }}</span>
            <strong>{{ contextWindowBuckets.peakOccupancyPct }}%</strong>
          </div>
        </div>

        <div class="twb-eval-chart-wrap">
          <ContextWindowBucketChart
            :report="contextWindowBuckets"
            :diagnostics="effectiveCoreDiagnostics"
            :stale="diagnosticsStale"
          />
        </div>
      </div>

      <!-- View 4: Code Execution (Wave 1 Geist Frame List Fallback) -->
      <div v-else class="twb-pane" data-testid="trajectory-pane-code">
        <div class="twb-kpi-strip mono">
          <div class="twb-kpi-cell">
            <span class="twb-kpi-cell__label">{{ tr('trajectoryCode.kpi.commands', '命令与脚本调用') }}</span>
            <strong>{{ codeExecutionEntries.length }}</strong>
          </div>
          <div class="twb-kpi-cell">
            <span class="twb-kpi-cell__label">{{ tr('trajectoryCode.kpi.errors', '报错记录') }}</span>
            <strong :class="{ 'is-danger': codeErrorCount > 0 }">{{ codeErrorCount }}</strong>
          </div>
          <div class="twb-kpi-cell">
            <span class="twb-kpi-cell__label">{{ tr('trajectoryCode.kpi.frames', '定位代码帧') }}</span>
            <strong>{{ codeFrameCount }}</strong>
          </div>
        </div>

        <div v-if="codeDiagnosisNode" class="twb-header-popover">
          <TrajectoryDiagnosisPopover
            :open="Boolean(codeDiagnosisNode)"
            :run-id="effectiveRunId"
            :session-id="effectiveSessionId"
            :node="codeDiagnosisNode"
            :trajectory="trajectorySummary"
            :diagnostics="effectiveCoreDiagnostics"
            @close="codeDiagnosisNode = null"
            @injected="emit('diagnose-draft', $event.draft)"
          />
        </div>

        <div v-if="codeExecutionEntries.length === 0" class="twb-empty" data-testid="trajectory-code-empty">
          <Terminal :size="15" aria-hidden="true" />
          <span>{{ tr('trajectoryCode.empty', '当前回合未记录终端命令或报错代码帧') }}</span>
        </div>

        <div v-else class="twb-code-list" data-testid="trajectory-code-list">
          <article
            v-for="entry in codeExecutionEntries"
            :key="entry.node.id"
            class="twb-code-item"
            :class="{ 'is-error': entry.isError }"
            :data-testid="`code-exec-item-${entry.node.id}`"
          >
            <div class="twb-code-item__head">
              <div class="twb-code-item__title mono">
                <Terminal :size="12" aria-hidden="true" />
                <strong>{{ entry.node.toolId || entry.node.label }}</strong>
                <span class="twb-code-status" :class="{ 'is-error': entry.isError }">
                  {{ entry.node.status }}
                </span>
                <span v-if="entry.node.durationMs !== null" class="twb-muted">
                  {{ formatDurationMs(entry.node.durationMs) }}
                </span>
              </div>

              <button
                type="button"
                class="twb-code-diag-btn"
                :data-testid="`code-diagnose-${entry.node.id}`"
                @click="codeDiagnosisNode = entry.node"
              >
                <Sparkles :size="11" aria-hidden="true" />
                <span>{{ tr('trajectoryPath.diagnoseBtn', '诊断') }}</span>
              </button>
            </div>

            <p v-if="entry.command" class="twb-code-cmd mono">$ {{ entry.command }}</p>
            <p v-if="entry.node.summary" class="twb-code-summary">{{ entry.node.summary }}</p>

            <div
              v-for="frame in entry.frames"
              :key="frame.id"
              class="twb-code-frame mono"
              data-testid="code-exec-frame"
            >
              <div class="twb-code-frame__head">
                <span>{{ frame.title }}</span>
                <span v-if="frame.errorCategory" class="is-danger">{{ frame.errorCategory }}</span>
              </div>
              <pre class="twb-code-frame__pre"><div
                v-for="(fl, idx) in frame.lines"
                :key="idx"
                class="twb-code-frame__line"
                :class="{ 'is-hi': fl.highlight }"
              ><span class="twb-code-frame__ln">{{ fl.lineNumber ?? '·' }}</span><span>{{ fl.text }}</span></div></pre>
            </div>
          </article>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.twb-root {
  container-type: inline-size;
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  background: var(--bg-primary);
  color: var(--text-primary);
}

.twb-topzone {
  flex: none;
  display: flex;
  flex-direction: column;
  background: var(--bg-primary);
}

.twb-turnbar-row {
  padding: 6px 14px;
  border-bottom: 1px solid var(--border-muted);
}

.twb-filter-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 7px;
  border: 1px solid var(--border-default);
  border-radius: 5px;
  background: var(--bg-secondary);
  color: var(--text-primary);
  font-size: 10.5px;
  cursor: pointer;
}

.twb-filter-pill:hover {
  border-color: var(--text-primary);
}

.twb-icon-btn {
  width: 26px;
  height: 26px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-primary);
  color: var(--text-secondary);
  cursor: pointer;
}

.twb-icon-btn:hover {
  border-color: var(--border-default);
  background: var(--bg-secondary);
  color: var(--text-primary);
}

.twb-spinner {
  color: var(--text-muted);
  animation: twb-spin 0.85s linear infinite;
}

@keyframes twb-spin {
  to {
    transform: rotate(360deg);
  }
}

.twb-header-popover {
  padding: 10px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  display: flex;
  justify-content: center;
}

.twb-offline-banner {
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 6px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  color: var(--text-secondary);
  font-size: 11.5px;
  line-height: 1.4;
}

.twb-findings-strip {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 7px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
}

.twb-finding-row {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.twb-finding-code {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--obs-status-warn-soft, color-mix(in srgb, var(--accent-warning) 13%, transparent));
  color: var(--obs-status-warn, var(--accent-warning));
  font-size: 10.5px;
  font-weight: 600;
  flex-shrink: 0;
}

/* Geist Underline Sub-Navigation */
.twb-subnav {
  display: flex;
  align-items: stretch;
  gap: 16px;
  padding: 0 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
  overflow-x: auto;
}

.twb-subnav__tab {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 34px;
  padding: 0 2px;
  border: 0;
  border-bottom: 2px solid transparent;
  margin-bottom: -1px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: color 110ms ease, border-color 110ms ease;
}

.twb-subnav__tab:hover {
  color: var(--text-primary);
}

.twb-subnav__tab.is-active {
  color: var(--text-primary);
  font-weight: 600;
  border-bottom: 2px solid var(--text-primary);
}

.twb-subnav__err-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--accent-danger, #c72b31);
}

.twb-body {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  overflow-x: hidden;
}

.twb-pane {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.twb-overlap-toggle-row {
  padding: 8px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
}

.twb-overlap-switch {
  width: 100%;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 2px 0;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.twb-overlap-switch:hover {
  color: var(--text-primary);
}

/* Wave 1 Geist KPI Strip */
.twb-kpi-strip {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
}

@container (min-width: 520px) {
  .twb-kpi-strip {
    grid-template-columns: repeat(4, minmax(0, 1fr));
  }
}

.twb-kpi-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 10px 14px;
  border-right: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
}

.twb-kpi-cell:last-child {
  border-right: 0;
}

.twb-kpi-cell__label {
  font-size: 10.5px;
  color: var(--text-muted);
}

.twb-kpi-cell strong {
  font-size: 16px;
  font-weight: 600;
  color: var(--text-primary);
}

.is-danger {
  color: var(--text-error, #c72b31) !important;
}

.twb-eval-chart-wrap {
  padding: 12px 14px;
}

/* Wave 1 Code Execution List */
.twb-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 32px 16px;
  color: var(--text-muted);
  font-size: 12px;
}

.twb-code-list {
  display: flex;
  flex-direction: column;
}

.twb-code-item {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 12px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
}

.twb-code-item__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.twb-code-item__title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.twb-code-status {
  padding: 1px 5px;
  border: 1px solid var(--border-muted);
  border-radius: 4px;
  font-size: 10px;
  color: var(--text-secondary);
}

.twb-code-status.is-error {
  border-color: color-mix(in srgb, var(--accent-danger, #c72b31) 35%, var(--border-muted));
  color: var(--text-error, #c72b31);
  background: var(--bg-status-danger);
}

.twb-muted {
  font-size: 10.5px;
  color: var(--text-muted);
}

.twb-code-diag-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 8px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 11px;
  cursor: pointer;
}

.twb-code-diag-btn:hover {
  border-color: var(--text-primary);
  background: var(--bg-secondary);
}

.twb-code-cmd {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}

.twb-code-summary {
  margin: 0;
  font-size: 12px;
  color: var(--text-primary);
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.twb-code-frame {
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  overflow: hidden;
  background: var(--bg-secondary);
}

.twb-code-frame__head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 8px;
  border-bottom: 1px solid var(--border-muted);
  font-size: 10.5px;
  color: var(--text-secondary);
}

.twb-code-frame__pre {
  margin: 0;
  padding: 5px 0;
  font-size: 11px;
  overflow-x: auto;
}

.twb-code-frame__line {
  display: grid;
  grid-template-columns: 36px 1fr;
  gap: 8px;
  padding: 1px 8px;
}

.twb-code-frame__line.is-hi {
  background: var(--bg-status-danger);
  color: var(--text-error, #c72b31);
}

.twb-code-frame__ln {
  text-align: right;
  color: var(--text-muted);
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
