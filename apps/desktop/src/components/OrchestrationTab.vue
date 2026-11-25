<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  ArrowUpRight,
  ChevronDown,
  ChevronRight,
  GitBranch,
  ListChecks,
  MessageSquare,
  Network,
  X,
} from '@lucide/vue'
import {
  api,
  type ModelInvocationDto,
  type OrchestrationSnapshotDto,
  type SupervisionFindingDto,
  type ToolDescriptorDto,
  type ToolExecutionTimelineItemDto,
  type TopologyRunDto,
} from '../api'
import { useStatusLabels } from '../lib/statusLabels'
import { useAgentDirectory } from '../composables/useAgentDirectory'
import { useObservabilityRunSelection } from '@/composables/useObservabilityRunSelection'
import ObservabilityTurnBar from '@/components/observability/ObservabilityTurnBar.vue'
import { focusDockCard } from '@/lib/homeDockSync'
import type { ExportOfflineHtmlResult } from '../lib/offlineDiagnosticReport'
import DeclaredGraphCanvas from './canvas/DeclaredGraphCanvas.vue'
import OrchestrationMiniGraph, {
  buildRuntimeTopology,
  type RuntimeTopologyNode,
  type RuntimeTopologyRoleTier,
} from './OrchestrationMiniGraph.vue'
import AgentTimeline from './timeline/AgentTimeline.vue'
import SessionPlanPanel from './plan/SessionPlanPanel.vue'
import TeammateSteerPopover, { type TeammateSteerTarget } from './teammate/TeammateSteerPopover.vue'

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

const props = withDefaults(defineProps<{
  snapshot: OrchestrationSnapshotDto | null
  toolExecutions: ToolExecutionTimelineItemDto[]
  tools?: ToolDescriptorDto[]
  sessionId?: string | null
  runId?: string | null
  runs?: readonly unknown[] | null
  activeRunId?: string | null
  topologyRun?: TopologyRunDto | null
  modelInvocations?: ModelInvocationDto[]
  focusedTarget?: string | OrchestrationFocusTarget | null
}>(), {
  tools: undefined,
  sessionId: null,
  runId: null,
  runs: null,
  activeRunId: null,
  topologyRun: undefined,
  modelInvocations: undefined,
  focusedTarget: null,
})

const emit = defineEmits<{
  'rerun-tool': [toolExecution: ToolExecutionTimelineItemDto]
  'view-tool-details': [toolExecution: ToolExecutionTimelineItemDto]
  'execute-tool': [tool: ToolDescriptorDto]
  'select-node': [node: RuntimeTopologyNode | null]
  'diagnose-draft': [draft: string]
  'export-report': [result: ExportOfflineHtmlResult]
}>()

const { t, te, locale } = useI18n()
const isZh = computed(() => String(locale.value || '').toLowerCase().startsWith('zh'))

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? String(t(key, params)) : String(t(key))
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

type CanvasMode = 'topology' | 'timeline' | 'blueprint'

const topologyViewMode = ref<CanvasMode>('topology')
const planExpanded = ref(false)
const supervisionExpanded = ref(false)
const selectedNodeId = ref<string | null>(null)
const detailsBlockRef = ref<HTMLElement | null>(null)
const focusedSteerTarget = ref<TeammateSteerTarget | null>(null)
const focusedSteerOpen = ref(false)

const { runStatusLabel: statusWords, severityLabel } = useStatusLabels()
const { nameFor } = typeof api.listAgents === 'function'
  ? useAgentDirectory()
  : { nameFor: (slug: string | null | undefined, fallback?: string | null) => fallback ?? slug ?? '' }

const lastObservedSnapshot = ref<OrchestrationSnapshotDto | null>(null)

watch(
  () => props.snapshot,
  (next) => {
    if (next?.run?.id) {
      lastObservedSnapshot.value = next
    }
  },
  { immediate: true },
)

const effectiveSessionId = computed(
  () => props.sessionId || props.snapshot?.run?.session_id || lastObservedSnapshot.value?.run?.session_id || null,
)

const {
  turns: observabilityTurns,
  isFollowingLatest,
  effectiveRunId: selectionEffectiveRunId,
  selectedTurn,
  selectRun,
  followLatest,
} = useObservabilityRunSelection({
  sessionId: () => effectiveSessionId.value,
  runs: () => props.runs,
  activeRunId: () => props.activeRunId ?? props.snapshot?.run?.id ?? props.runId ?? null,
})

const effectiveRunId = computed<string | null>(
  () =>
    selectionEffectiveRunId.value
    ?? props.snapshot?.run?.id
    ?? props.runId
    ?? lastObservedSnapshot.value?.run?.id
    ?? null,
)

const isHistoricalRunSelected = computed<boolean>(() => {
  const target = effectiveRunId.value
  const activeSnapRunId = props.snapshot?.run?.id ?? null
  return Boolean(target && activeSnapRunId && target !== activeSnapRunId)
})

// Live & historical topology + tool executions + model invocations hydration
const fetchedTopologyRun = ref<TopologyRunDto | null>(null)
const historicalToolExecutions = ref<ToolExecutionTimelineItemDto[]>([])
const loadedModelInvocations = ref<ModelInvocationDto[]>([])
let topologyAbort: AbortController | null = null
let topologyEpoch = 0

async function loadRunProjection(): Promise<void> {
  const runId = effectiveRunId.value
  const sessId = effectiveSessionId.value
  topologyAbort?.abort()
  topologyAbort = null

  if (!runId) {
    fetchedTopologyRun.value = null
    historicalToolExecutions.value = []
    loadedModelInvocations.value = []
    return
  }

  const epoch = ++topologyEpoch
  const controller = new AbortController()
  topologyAbort = controller

  try {
    const tasks: Promise<void>[] = []

    if (props.topologyRun === undefined && sessId && typeof api.getSessionTopology === 'function') {
      tasks.push(
        api
          .getSessionTopology(
            sessId,
            { run_id: runId, include_finished: true, max_runs: 1, max_tasks: 200 },
            controller.signal,
          )
          .then((view) => {
            if (epoch !== topologyEpoch || controller.signal.aborted) return
            fetchedTopologyRun.value = view.runs.find((r) => r.run_id === runId) ?? view.runs[0] ?? null
          })
          .catch(() => {
            if (epoch === topologyEpoch && !controller.signal.aborted && fetchedTopologyRun.value?.run_id !== runId) {
              fetchedTopologyRun.value = null
            }
          }),
      )
    }

    if (isHistoricalRunSelected.value && sessId && typeof api.listToolExecutions === 'function') {
      tasks.push(
        api
          .listToolExecutions(sessId, { run_id: runId, limit: 200 }, controller.signal)
          .then((items) => {
            if (epoch !== topologyEpoch || controller.signal.aborted) return
            historicalToolExecutions.value = Array.isArray(items) ? items : []
          })
          .catch(() => {
            if (epoch === topologyEpoch && !controller.signal.aborted) {
              historicalToolExecutions.value = []
            }
          }),
      )
    } else {
      historicalToolExecutions.value = []
    }

    if (!props.modelInvocations && typeof api.listModelInvocations === 'function') {
      tasks.push(
        api
          .listModelInvocations({ run_id: runId, limit: 200 })
          .then((res) => {
            if (epoch !== topologyEpoch || controller.signal.aborted) return
            loadedModelInvocations.value = Array.isArray(res?.items) ? res.items : []
          })
          .catch(() => {
            if (epoch === topologyEpoch && !controller.signal.aborted) {
              loadedModelInvocations.value = []
            }
          }),
      )
    }

    await Promise.all(tasks)
  } finally {
    if (topologyAbort === controller) topologyAbort = null
  }
}

onBeforeUnmount(() => {
  topologyAbort?.abort()
})

watch(
  () => [
    effectiveRunId.value,
    effectiveSessionId.value,
    props.snapshot?.run?.status,
    props.snapshot?.run?.updated_at,
    props.snapshot?.nodes.length,
    props.snapshot?.flows?.length,
  ],
  (next, prev) => {
    if (next[0] !== prev?.[0]) {
      selectedNodeId.value = null
      fetchedTopologyRun.value = null
      historicalToolExecutions.value = []
      loadedModelInvocations.value = []
    }
    void loadRunProjection()
  },
  { immediate: true },
)

const activeTopologyRun = computed<TopologyRunDto | null>(() => {
  if (props.topologyRun !== undefined) return props.topologyRun
  const runId = effectiveRunId.value
  if (fetchedTopologyRun.value && (!runId || fetchedTopologyRun.value.run_id === runId)) {
    return fetchedTopologyRun.value
  }
  return null
})

const effectiveSnapshot = computed<OrchestrationSnapshotDto | null>(() => {
  const targetId = effectiveRunId.value
  if (props.snapshot?.run && (!targetId || props.snapshot.run.id === targetId)) {
    return props.snapshot
  }
  if (!targetId) return props.snapshot
  const turn = selectedTurn.value
  const topo = activeTopologyRun.value
  if (!turn && !topo) return props.snapshot
  const createdAt = topo?.started_at ?? turn?.startedAt ?? new Date(0).toISOString()
  const updatedAt = topo?.completed_at ?? turn?.completedAt ?? createdAt
  return {
    run: {
      id: targetId,
      session_id: effectiveSessionId.value ?? '',
      status: topo?.status ?? turn?.status ?? 'completed',
      summary: turn?.headline ?? '',
      created_at: createdAt,
      updated_at: updatedAt,
    },
    graph: props.snapshot?.graph ?? null,
    flows: [],
    nodes: [],
    lanes: [],
    assignments: [],
    step_results: [],
    context_packs: [],
    supervision_findings: [],
  }
})

const effectiveToolExecutions = computed<ToolExecutionTimelineItemDto[]>(() => {
  if (isHistoricalRunSelected.value) {
    const target = effectiveRunId.value
    const fromProps = props.toolExecutions.filter((item) => item.run_id === target)
    return historicalToolExecutions.value.length > 0 ? historicalToolExecutions.value : fromProps
  }
  return props.toolExecutions
})

const effectiveModelInvocations = computed<ModelInvocationDto[]>(
  () => props.modelInvocations ?? loadedModelInvocations.value,
)

const hasSnapshot = computed(() => Boolean(effectiveSnapshot.value?.run))

const runtimeTopology = computed(() =>
  buildRuntimeTopology({
    snapshot: effectiveSnapshot.value,
    topologyRun: activeTopologyRun.value,
    toolExecutions: effectiveToolExecutions.value,
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
  const runId = effectiveRunId.value
  if (!node || !runId || node.roleTier === 'coordinator') return false
  return Boolean((node.handle || node.agentSlug || '').trim())
})

function openSelectedNodeSteer(event?: Event): void {
  const node = selectedNode.value
  const runId = effectiveRunId.value
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

function jumpSelectedNodeTrajectory(): void {
  const node = selectedNode.value
  if (!node) return
  focusDockCard(
    {
      card: 'trajectory',
      reason: 'dispatch',
      trajectoryTab: 'path',
      trajectoryNodeId: node.id,
      trajectoryTurnNo: selectedTurn.value?.turnNo ?? undefined,
      runId: effectiveRunId.value,
    },
    { title: tr('uie.cardTitle.trajectory', isZh.value ? '轨迹' : 'Trajectory') },
  )
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
    emit('select-node', matched)
    void nextTick(() => {
      detailsBlockRef.value?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' })
    })
  },
  { immediate: true },
)

function onSelectTopologyNode(node: RuntimeTopologyNode | null): void {
  selectedNodeId.value = node ? node.id : null
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
  if (topologyViewMode.value === 'blueprint') {
    topologyViewMode.value = 'topology'
  }
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

const supervisionFindings = computed<SupervisionFindingDto[]>(
  () => effectiveSnapshot.value?.supervision_findings ?? [],
)

// Localized labels with safe bilingual fallbacks (zero raw CJK in <template>)
const tabTopologyLabel = computed(() =>
  tr('orchestrationPanel.viewLiveTopology', isZh.value ? '实时协作图' : 'Live graph'),
)
const tabTimelineLabel = computed(() =>
  tr('orchestrationPanel.viewParallelLanes', isZh.value ? '并行泳道' : 'Parallel lanes'),
)
const tabBlueprintLabel = computed(() =>
  tr('orchestrationPanel.viewModeBlueprint', isZh.value ? '模式蓝图' : 'Mode blueprint'),
)
const planToggleLabel = computed(() =>
  tr('sessionPlan.title', isZh.value ? '会话计划' : 'Session plan'),
)
const jumpNodeTrajectoryLabel = computed(() =>
  tr('orchestrationPanel.viewNodeTrajectory', isZh.value ? '查看该节点轨迹 ↗' : 'View node trajectory ↗'),
)
const noBlueprintLabel = computed(() =>
  tr(
    'orchestrationPanel.noModeBlueprint',
    isZh.value ? '当前运行采用动态自治编排，无静态模式蓝图。' : 'Dynamic runtime mode has no static blueprint graph.',
  ),
)
</script>

<template>
  <section class="orchestration-tab obs-geist-surface" data-testid="orchestration-tab">
    <!-- Shared Turn Bar -->
    <ObservabilityTurnBar
      :turns="observabilityTurns"
      :effective-run-id="effectiveRunId"
      :is-following-latest="isFollowingLatest"
      :filter-label="selectedNode ? selectedNode.label : null"
      @select-run="selectRun"
      @follow-latest="followLatest"
      @clear-filter="clearNodeFilter"
    />

    <!-- Flat Geist Segmented Switcher + Compact Collapsible Plan Trigger -->
    <div class="orchestration-toolbar">
      <div
        class="orchestration-segmented"
        role="tablist"
        :aria-label="t('orchestrationPanel.viewSwitcherLabel')"
        data-testid="orchestration-topology-toggle"
      >
        <button
          type="button"
          role="tab"
          class="orchestration-seg-btn"
          :class="{ 'is-active': topologyViewMode === 'topology' }"
          :aria-selected="topologyViewMode === 'topology'"
          data-testid="topology-view-btn-topology"
          @click="topologyViewMode = 'topology'"
        >
          <Network :size="12" aria-hidden="true" />
          <span>{{ tabTopologyLabel }}</span>
        </button>
        <button
          type="button"
          role="tab"
          class="orchestration-seg-btn"
          :class="{ 'is-active': topologyViewMode === 'timeline' }"
          :aria-selected="topologyViewMode === 'timeline'"
          data-testid="topology-view-btn-timeline"
          @click="topologyViewMode = 'timeline'"
        >
          <span>{{ tabTimelineLabel }}</span>
        </button>
        <button
          type="button"
          role="tab"
          class="orchestration-seg-btn"
          :class="{ 'is-active': topologyViewMode === 'blueprint' }"
          :aria-selected="topologyViewMode === 'blueprint'"
          data-testid="topology-view-btn-blueprint"
          @click="topologyViewMode = 'blueprint'"
        >
          <GitBranch :size="12" aria-hidden="true" />
          <span>{{ tabBlueprintLabel }}</span>
        </button>
      </div>

      <div class="orchestration-toolbar-right">
        <button
          v-if="selectedNode"
          type="button"
          class="topology-clear-btn ui-pressable"
          data-testid="topology-clear-filter"
          @click="clearNodeFilter"
        >
          <X :size="11" aria-hidden="true" />
          <span>{{ t('orchestrationPanel.allNodes') }}</span>
        </button>

        <button
          v-if="effectiveSessionId"
          type="button"
          class="orchestration-plan-toggle ui-pressable"
          :class="{ 'is-open': planExpanded }"
          :aria-expanded="planExpanded"
          data-testid="orchestration-plan-toggle"
          @click="planExpanded = !planExpanded"
        >
          <ListChecks :size="12" aria-hidden="true" />
          <span>{{ planToggleLabel }}</span>
          <ChevronDown v-if="planExpanded" :size="12" aria-hidden="true" />
          <ChevronRight v-else :size="12" aria-hidden="true" />
        </button>
      </div>
    </div>

    <!-- Compact Collapsible Session Plan Drawer -->
    <div
      v-if="effectiveSessionId"
      v-show="planExpanded"
      class="orchestration-plan-drawer"
      data-testid="orchestration-plan-drawer"
    >
      <SessionPlanPanel
        :session-id="effectiveSessionId"
        compact
        @focus-node="onFocusPlanNode"
      />
    </div>

    <!-- Empty State -->
    <div v-if="!hasSnapshot" class="orchestration-empty" data-testid="orchestration-empty">
      {{ t('orchestrationPanel.empty') }}
    </div>

    <!-- Main 75%+ Viewport Canvas Stage -->
    <template v-else-if="effectiveSnapshot">
      <div class="orchestration-canvas-stage" data-testid="runtime-topology-block">
        <OrchestrationMiniGraph
          v-if="topologyViewMode === 'topology'"
          fill-height
          :nodes="runtimeTopology.nodes"
          :edges="runtimeTopology.edges"
          :run-id="effectiveRunId"
          :selected-node-id="selectedNodeId"
          @select-node="onSelectTopologyNode"
        />

        <div v-else-if="topologyViewMode === 'timeline'" class="orchestration-timeline-wrap">
          <AgentTimeline
            :snapshot="effectiveSnapshot"
            :topology-run="activeTopologyRun"
            :tool-executions="effectiveToolExecutions"
            :model-invocations="effectiveModelInvocations"
            :selected-node-id="selectedNodeId"
            @select-node="onSelectTimelineNodeId"
          />
        </div>

        <div v-else class="orchestration-blueprint-wrap">
          <DeclaredGraphCanvas
            v-if="effectiveSnapshot.graph"
            :graph="effectiveSnapshot.graph"
            :flows="effectiveSnapshot.flows"
          />
          <div v-else class="orchestration-empty">
            {{ noBlueprintLabel }}
          </div>
        </div>
      </div>

      <!-- Selected Node Bottom Light Card (Direct Steer + Jump to Node Trajectory) -->
      <div
        v-if="selectedNode"
        ref="detailsBlockRef"
        class="focused-node-card"
        data-testid="focused-node-details"
      >
        <div class="focused-node-card__head">
          <span
            class="focused-node-card__dot"
            :class="`is-${selectedNode.status}`"
            aria-hidden="true"
          />
          <strong class="focused-node-card__lead" :title="selectedNode.label">
            {{ selectedNode.label }}
          </strong>
          <span class="focused-node-card__tier">{{ roleTierLabel(selectedNode.roleTier) }}</span>
          <span class="focused-node-card__status" :class="`is-${selectedNode.status}`">
            {{
              runStatusLabel(
                selectedNode.status === 'awaiting_approval'
                  ? 'awaiting_approval'
                  : (selectedNode.rawStatus || selectedNode.status),
              )
            }}
          </span>
        </div>

        <div
          v-if="selectedParentNode || selectedNode.handle || selectedNode.laneKey"
          class="focused-node-card__meta"
        >
          <span v-if="selectedParentNode">
            {{ t('orchestrationPanel.dispatchedBy', { parent: selectedParentNode.label }) }}
          </span>
          <code v-if="selectedNode.handle" class="focused-node-card__mono">@{{ selectedNode.handle }}</code>
          <span v-if="selectedNode.laneKey">
            {{ t('orchestrationPanel.laneBadge', { lane: selectedNode.laneKey }) }}
          </span>
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
          <div class="focused-node-card__scope-list">
            <code
              v-for="scopePath in selectedNode.writeScope"
              :key="scopePath"
              class="focused-node-card__scope-item"
              :title="scopePath"
            >{{ scopePath }}</code>
          </div>
        </div>

        <div class="focused-node-card__actions">
          <button
            v-if="canSteerSelectedNode"
            type="button"
            class="focused-node-card__steer-btn ui-pressable"
            data-testid="focused-node-steer-btn"
            :title="tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: selectedNode.label })"
            @click.stop="openSelectedNodeSteer($event)"
          >
            <MessageSquare :size="12" aria-hidden="true" />
            <span>{{ tr('teammateControl.steerAction', 'Direct control') }}</span>
          </button>

          <button
            type="button"
            class="focused-node-card__jump-btn ui-pressable"
            data-testid="orchestration-jump-trajectory"
            @click="jumpSelectedNodeTrajectory"
          >
            <span>{{ jumpNodeTrajectoryLabel }}</span>
            <ArrowUpRight :size="12" aria-hidden="true" />
          </button>
        </div>
      </div>

      <!-- Compact Collapsible Supervision Findings Bar -->
      <div
        v-if="supervisionFindings.length > 0"
        class="orchestration-supervision-bar"
        data-testid="orchestration-supervision-bar"
      >
        <button
          type="button"
          class="supervision-bar__toggle ui-pressable"
          :aria-expanded="supervisionExpanded"
          @click="supervisionExpanded = !supervisionExpanded"
        >
          <AlertTriangle :size="12" class="supervision-bar__icon" aria-hidden="true" />
          <strong>{{ t('orchestrationPanel.supervision') }}</strong>
          <span class="supervision-bar__count">{{ supervisionFindings.length }}</span>
          <span class="supervision-bar__preview">
            {{ supervisionBody(supervisionFindings[0]!) || supervisionHeader(supervisionFindings[0]!) }}
          </span>
          <ChevronDown v-if="supervisionExpanded" :size="12" aria-hidden="true" />
          <ChevronRight v-else :size="12" aria-hidden="true" />
        </button>

        <div v-if="supervisionExpanded" class="supervision-bar__list">
          <div
            v-for="finding in supervisionFindings"
            :key="finding.id"
            class="supervision-bar__item"
          >
            <strong>{{ supervisionHeader(finding) }}</strong>
            <p v-if="supervisionBody(finding)">{{ supervisionBody(finding) }}</p>
            <small v-if="finding.recommendation">{{ finding.recommendation }}</small>
          </div>
        </div>
      </div>
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
  display: flex;
  flex-direction: column;
  min-height: 100%;
  min-width: 0;
  background: var(--surface-canvas, var(--bg-primary));
  color: var(--text-primary);
  font-variant-numeric: tabular-nums;
}

/* ── Flat Geist Sub-Navigation & Plan Toggle ───────────────────── */
.orchestration-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 6px 10px;
  padding: 0 12px;
  min-height: 36px;
  border-bottom: 1px solid var(--obs-hairline, var(--border-muted));
  background: var(--surface-canvas, var(--bg-primary));
  flex-shrink: 0;
}

.orchestration-segmented {
  display: inline-flex;
  align-items: stretch;
  gap: 12px;
  min-width: 0;
}

.orchestration-seg-btn {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 35px;
  padding: 0 2px;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  color: var(--text-muted);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: color 0.12s ease, border-color 0.12s ease;
}

.orchestration-seg-btn:hover {
  color: var(--text-primary);
}

.orchestration-seg-btn.is-active {
  color: var(--text-primary);
  font-weight: 600;
  border-bottom-color: var(--text-primary);
}

.orchestration-toolbar-right {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
}

.topology-clear-btn,
.orchestration-plan-toggle {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 7px;
  border: 0;
  border-radius: 5px;
  background: var(--surface-button, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color 0.12s ease, color 0.12s ease;
}

.topology-clear-btn:hover,
.orchestration-plan-toggle:hover,
.orchestration-plan-toggle.is-open {
  background: var(--surface-button-hover, var(--bg-hover));
  color: var(--text-primary);
}

.orchestration-plan-drawer {
  padding: 8px 12px;
  border-bottom: 1px solid var(--obs-hairline, var(--border-muted));
  background: var(--surface-canvas, var(--bg-primary));
  flex-shrink: 0;
}

/* ── 75%+ Viewport Height Canvas Stage ─────────────────────────── */
.orchestration-canvas-stage {
  flex: 1 1 auto;
  display: flex;
  flex-direction: column;
  min-height: clamp(360px, 75dvh, 720px);
  min-width: 0;
  background: var(--surface-canvas, var(--bg-primary));
}

.orchestration-timeline-wrap,
.orchestration-blueprint-wrap {
  flex: 1 1 auto;
  display: flex;
  flex-direction: column;
  padding: 10px 12px;
  min-width: 0;
}

.orchestration-blueprint-wrap :deep(.declared-graph-canvas) {
  flex: 1 1 auto;
  min-height: 340px;
  height: 100%;
  border: 0;
  border-radius: 0;
  background: var(--surface-canvas, var(--bg-primary));
}

.orchestration-empty {
  margin: auto 0;
  padding: 32px 16px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
  line-height: 1.5;
}

/* ── Selected Node Bottom Light Card ───────────────────────────── */
.focused-node-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  border-top: 1px solid var(--obs-hairline, var(--border-muted));
  background: var(--surface-section, var(--bg-secondary));
  flex-shrink: 0;
  min-width: 0;
}

.focused-node-card__head {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.focused-node-card__dot {
  width: 7px;
  height: 7px;
  border-radius: 999px;
  background: var(--text-muted);
  flex-shrink: 0;
}

.focused-node-card__dot.is-running,
.focused-node-card__dot.is-completed {
  background: var(--obs-status-ok, var(--accent-success));
}

.focused-node-card__dot.is-awaiting_approval {
  background: var(--obs-status-warn, var(--accent-warning));
}

.focused-node-card__dot.is-failed {
  background: var(--obs-status-danger, var(--accent-danger));
}

.focused-node-card__lead {
  flex: 1;
  min-width: 0;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.focused-node-card__tier {
  flex-shrink: 0;
  font-size: 10.5px;
  font-weight: 500;
  padding: 1px 6px;
  border-radius: 4px;
  color: var(--text-secondary);
  background: var(--surface-raised, var(--bg-primary));
}

.focused-node-card__status {
  flex-shrink: 0;
  font-size: 10.5px;
  font-weight: 600;
  padding: 1px 6px;
  border-radius: 999px;
  color: var(--text-muted);
  background: var(--surface-raised, var(--bg-primary));
}

.focused-node-card__status.is-running {
  color: var(--obs-status-ok, var(--accent-success));
}

.focused-node-card__status.is-awaiting_approval {
  color: var(--obs-status-warn, var(--accent-warning));
}

.focused-node-card__status.is-failed {
  color: var(--obs-status-danger, var(--accent-danger));
}

.focused-node-card__meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  font-size: 11px;
  color: var(--text-muted);
}

.focused-node-card__mono {
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--text-secondary);
}

.focused-node-card__title {
  margin: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.focused-node-card__desc,
.focused-node-card__result {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}

.focused-node-card__scope {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.focused-node-card__scope small {
  font-size: 10.5px;
  font-weight: 600;
  color: var(--text-muted);
}

.focused-node-card__scope-list {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.focused-node-card__scope-item {
  font-family: var(--font-mono);
  font-size: 10.5px;
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--surface-raised, var(--bg-primary));
  color: var(--text-secondary);
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.focused-node-card__actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  padding-top: 2px;
}

.focused-node-card__steer-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 10px;
  border: 0;
  border-radius: 5px;
  background: var(--bg-primary-button);
  color: var(--text-on-primary);
  font-size: 11.5px;
  font-weight: 600;
  cursor: pointer;
}

.focused-node-card__steer-btn:hover {
  background: var(--bg-primary-button-hover);
}

.focused-node-card__jump-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 26px;
  padding: 0 9px;
  border: 1px solid var(--obs-hairline, var(--border-muted));
  border-radius: 5px;
  background: var(--surface-button, var(--bg-primary));
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 0.12s ease, border-color 0.12s ease;
}

.focused-node-card__jump-btn:hover {
  background: var(--surface-button-hover, var(--bg-hover));
  border-color: var(--border-default);
}

/* ── Compact Supervision Findings Bar ──────────────────────────── */
.orchestration-supervision-bar {
  border-top: 1px solid var(--obs-hairline, var(--border-muted));
  background: var(--surface-canvas, var(--bg-primary));
  flex-shrink: 0;
}

.supervision-bar__toggle {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-height: 34px;
  padding: 6px 12px;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font-size: 11.5px;
  text-align: left;
  cursor: pointer;
}

.supervision-bar__toggle:hover {
  background: var(--surface-hover, var(--bg-secondary));
}

.supervision-bar__icon {
  color: var(--obs-status-warn, var(--accent-warning));
  flex-shrink: 0;
}

.supervision-bar__count {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 10.5px;
  font-weight: 600;
  padding: 0 5px;
  border-radius: 999px;
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-secondary);
  flex-shrink: 0;
}

.supervision-bar__preview {
  flex: 1;
  min-width: 0;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.supervision-bar__list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 0 12px 10px;
}

.supervision-bar__item {
  display: flex;
  flex-direction: column;
  gap: 3px;
  padding: 6px 8px;
  border-radius: 5px;
  background: var(--surface-section, var(--bg-secondary));
  font-size: 11.5px;
}

.supervision-bar__item strong {
  color: var(--text-primary);
  font-weight: 600;
}

.supervision-bar__item p {
  margin: 0;
  color: var(--text-secondary);
  line-height: 1.4;
}

.supervision-bar__item small {
  color: var(--text-muted);
  font-size: 11px;
}
</style>
