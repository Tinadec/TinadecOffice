<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  MarkerType,
  Panel,
  VueFlow,
  useVueFlow,
  type Connection,
  type Edge,
  type EdgeMouseEvent,
  type EdgeUpdateEvent,
  type GraphNode,
  type Node,
  type NodeChange,
  type NodeDragEvent,
  type ValidConnectionFunc,
} from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import { Controls } from '@vue-flow/controls'
import { MiniMap } from '@vue-flow/minimap'
import { AlertTriangle, CheckCircle2, Cpu, LayoutGrid, Maximize2, Plus, Workflow, ZoomIn, ZoomOut } from '@lucide/vue'
import type { AgentDefinitionDto, AgentModeEdgeDto, AgentModeNodeDto } from '@/api'
import { validateMode, type ModeIssue, type ModeIssueCode } from '@/components/canvas/modeValidation'
import { pickLocalized, pickNodeLabel } from '@/lib/packI18n'
import ModeAgentNode, { type ModeAgentNodeData } from './ModeAgentNode.vue'
import {
  clampToLane,
  laneForY,
  MODE_GRID_SIZE,
  MODE_LANE_SPLIT_Y,
  MODE_NODE_WIDTH,
  snapPosition,
  type ModeLane,
} from './modeDagreLayout'

const FLOW_ID = 'agent-mode-editor'

const { t, te, locale } = useI18n()

const props = defineProps<{
  nodes: AgentModeNodeDto[]
  edges: AgentModeEdgeDto[]
  agents: AgentDefinitionDto[]
  /** Pack-managed/archived modes render inspect-only: no drag, no connect, no delete. */
  readonly?: boolean
  selectedNodeId?: string | null
  selectedEdgeId?: string | null
  /** Changes when the selected mode changes; triggers a re-fit once nodes initialize. */
  graphKey?: string | null
}>()

const emit = defineEmits<{
  'select-node': [id: string | null]
  'select-edge': [id: string | null]
  'node-moved': [payload: { id: string; position: { x: number; y: number }; lane?: ModeLane }]
  connect: [payload: { source: string; target: string }]
  'update-edge': [payload: { id: string; source: string; target: string }]
  'delete-node': [id: string]
  'delete-edge': [id: string]
  'clone-node': [id: string]
  'set-conversation': [id: string]
  'add-node': [payload: { lane: ModeLane }]
  'auto-layout': []
  undo: []
  redo: []
}>()

const wrapperRef = ref<HTMLElement | null>(null)
const canvasWrapRef = ref<HTMLElement | null>(null)
const wrapHeight = ref(560)
const { viewport, findNode, zoomIn, zoomOut, fitView } = useVueFlow(FLOW_ID)

const rejection = ref<string | null>(null)
const laneNotice = ref<string | null>(null)
const updatingEdgeId = ref<string | null>(null)
const highlightedNodeIds = ref<Set<string>>(new Set())
const highlightedEdgeIds = ref<Set<string>>(new Set())
let pendingFit = true
let rejectionTimer: ReturnType<typeof setTimeout> | null = null
let laneNoticeTimer: ReturnType<typeof setTimeout> | null = null
let highlightTimer: ReturnType<typeof setTimeout> | null = null
let wrapResizeObserver: ResizeObserver | null = null

function normalizeLane(value: unknown): ModeLane {
  return String(value ?? '').trim().toLowerCase() === 'execution' ? 'execution' : 'operation'
}

function agentFor(id: string): AgentDefinitionDto | undefined {
  return props.agents.find((agent) => agent.id === id)
}

function localizedRoleLabel(agent: AgentDefinitionDto | undefined): string {
  if (!agent) return '—'
  const raw = agent.role ?? agent.agent_type ?? ''
  if (!raw) return '—'
  const keyMap: Record<string, string> = {
    session_coordinator: 'settings.agentTypeSessionCoordinator',
    task_executor: 'settings.agentTypeTaskExecutor',
    governance_reviewer: 'settings.agentTypeGovernanceReviewer',
    context_maintenance: 'settings.agentTypeContextCompressor',
    exploration_planner: 'settings.agentTypeExplorationPlanner',
    solo_master: 'settings.agentTypeSoloMaster',
    search: 'settings.agentTypeSearchSpecialist',
    global_engineering: 'settings.agentTypeCodeWriter',
    meeting: 'settings.agentTypeMeeting',
    reviewer: 'settings.agentTypeReviewExecutor',
  }
  const key = keyMap[raw]
  if (key && te(key)) return t(key)
  return raw
}

function strategyDisplay(node: AgentModeNodeDto, agent: AgentDefinitionDto | undefined): {
  kind: 'inherit' | 'route' | 'fixed'
  badge: string
  overridden: boolean
} {
  const override = node.model_strategy_override
  if (override && override.kind && override.kind !== 'inherit') {
    if (override.kind === 'route') {
      const purpose = override.route_purpose ?? ''
      return {
        kind: 'route',
        badge: purpose ? `${t('settings.modeStrategyRouteBadge')}: ${purpose}` : t('settings.modeStrategyRouteBadge'),
        overridden: true,
      }
    }
    if (override.kind === 'fixed') {
      return {
        kind: 'fixed',
        badge: override.model ? `${t('settings.modeStrategyFixedBadge')}: ${override.model}` : t('settings.modeStrategyFixedBadge'),
        overridden: true,
      }
    }
  }
  const base = agent?.model_strategy
  if (base && typeof base === 'object') {
    const record = base as Record<string, unknown>
    if (record.kind === 'route' && typeof record.route_purpose === 'string') {
      return { kind: 'route', badge: `${t('settings.modeStrategyRouteBadge')}: ${record.route_purpose}`, overridden: false }
    }
    if (record.kind === 'fixed') {
      const model = typeof record.model === 'string' && record.model ? record.model : ''
      return {
        kind: 'fixed',
        badge: model ? `${t('settings.modeStrategyFixedBadge')}: ${model}` : t('settings.modeStrategyFixedBadge'),
        overridden: false,
      }
    }
  }
  return { kind: 'inherit', badge: t('settings.modeStrategyInheritBadge'), overridden: false }
}

const issues = computed<ModeIssue[]>(() => validateMode(
  props.nodes.map((node) => ({
    id: node.id,
    lane: normalizeLane(node.lane),
    agent_id: node.agent_id,
    config: node.config ?? null,
  })),
  props.edges.map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
))

const nodeIssueSeverity = computed<Map<string, 'error' | 'warning'>>(() => {
  const map = new Map<string, 'error' | 'warning'>()
  for (const issue of issues.value) {
    for (const nodeId of issue.nodes ?? []) {
      const prev = map.get(nodeId)
      if (!prev || issue.severity === 'error') map.set(nodeId, issue.severity)
    }
  }
  return map
})

const edgeIssueSet = computed<Set<string>>(() => {
  const set = new Set<string>()
  for (const issue of issues.value) {
    if (issue.severity === 'error') {
      for (const edgeId of issue.edges ?? []) set.add(edgeId)
    }
  }
  return set
})

const issueEntries = computed(() => {
  const entries = issues.value.map((issue) => ({
    key: `${issue.code}:${(issue.nodes ?? issue.edges ?? []).join(',')}`,
    code: issue.code,
    severity: issue.severity,
    text: t(`canvas.issue.${issue.code}`),
    nodes: issue.nodes ?? [],
    edges: issue.edges ?? [],
  }))
  if (rejection.value) {
    entries.unshift({
      key: `rejected:${rejection.value}`,
      code: rejection.value as ModeIssue['code'],
      severity: 'error' as const,
      text: t(`canvas.issue.${rejection.value}`),
      nodes: [],
      edges: [],
    })
  }
  return entries
})

function edgeLabel(edge: AgentModeEdgeDto): string {
  if (edge.label) return edge.label
  const conditionLabel = edge.condition?.label
  return typeof conditionLabel === 'string' ? conditionLabel : ''
}

const vfNodes = computed<Array<Node<ModeAgentNodeData>>>(() =>
  props.nodes.map((node) => {
    const agent = agentFor(node.agent_id)
    const agentName = agent
      ? pickLocalized(agent.i18n, locale.value, 'display_name', agent.display_name ?? agent.name) ?? agent.id
      : node.agent_id?.slice(0, 8) || t('canvas.unbound')
    const customLabel = pickNodeLabel(node, locale.value)
    const tools = agent?.allowed_tools ?? []
    const hasWildcardTools = agent?.tool_scope === '*' || tools.includes('*')
    const toolCount = tools.length > 0
      ? tools.length
      : (Array.isArray(agent?.tool_scope) ? agent.tool_scope.length : 0)
    const strategy = strategyDisplay(node, agent)
    const lane = normalizeLane(node.lane)

    return {
      id: node.id,
      type: 'agentNode',
      position: node.position,
      draggable: !props.readonly,
      connectable: !props.readonly,
      data: {
        agentName,
        customLabel,
        lane,
        roleLabel: localizedRoleLabel(agent),
        strategyBadge: strategy.badge,
        strategyKind: strategy.kind,
        strategyOverridden: strategy.overridden,
        toolCount,
        hasWildcardTools,
        isConversation: node.config?.conversation === true,
        unbound: !node.agent_id,
        selected: props.selectedNodeId === node.id,
        highlighted: highlightedNodeIds.value.has(node.id),
        issueSeverity: nodeIssueSeverity.value.get(node.id) ?? null,
        readonly: props.readonly,
      },
      style: {
        background: 'transparent',
        border: 'none',
        padding: '0',
        width: `${MODE_NODE_WIDTH}px`,
      },
    }
  }),
)

function resolveHandlesForEdge(edge: AgentModeEdgeDto): { sourceHandle: string; targetHandle: string } {
  const sourceNode = props.nodes.find((n) => n.id === edge.source)
  const targetNode = props.nodes.find((n) => n.id === edge.target)
  const sourceLane = sourceNode ? normalizeLane(sourceNode.lane) : 'operation'
  const targetLane = targetNode ? normalizeLane(targetNode.lane) : 'execution'

  if (sourceLane === 'operation' && targetLane === 'execution') {
    return { sourceHandle: 'bottom-source', targetHandle: 'top-target' }
  }
  if (sourceLane === 'operation' && targetLane === 'operation') {
    return { sourceHandle: 'right-source', targetHandle: 'left-target' }
  }
  if (sourceNode && targetNode && Math.abs(targetNode.position.y - sourceNode.position.y) >= 48) {
    return { sourceHandle: 'bottom-source', targetHandle: 'top-target' }
  }
  return { sourceHandle: 'right-source', targetHandle: 'left-target' }
}

const vfEdges = computed<Edge[]>(() =>
  props.edges.map((edge) => {
    const selected = props.selectedEdgeId === edge.id
    const highlighted = highlightedEdgeIds.value.has(edge.id)
    const hasError = edgeIssueSet.value.has(edge.id)
    const handles = resolveHandlesForEdge(edge)
    const stroke = hasError
      ? 'var(--accent-danger, #ef4444)'
      : (selected || highlighted)
        ? 'var(--accent-primary)'
        : 'var(--border-default)'
    return {
      id: edge.id,
      source: edge.source,
      target: edge.target,
      sourceHandle: handles.sourceHandle,
      targetHandle: handles.targetHandle,
      type: 'smoothstep',
      label: edgeLabel(edge),
      animated: highlighted,
      updatable: !props.readonly,
      interactionWidth: 32,
      markerEnd: {
        type: MarkerType.ArrowClosed,
        width: 15,
        height: 15,
        color: stroke,
      },
      style: {
        stroke,
        strokeWidth: selected || highlighted ? 2.4 : 1.75,
      },
      labelStyle: { fontSize: '10px', fill: 'var(--text-secondary)', fontWeight: 500 },
      labelBgStyle: { fill: 'var(--surface-raised)', fillOpacity: 0.94 },
      labelBgPadding: [5, 2] as [number, number],
      labelBgBorderRadius: 4,
    }
  }),
)

const splitScreenY = computed(() => viewport.value.y + MODE_LANE_SPLIT_Y * viewport.value.zoom)

const operationBandStyle = computed(() => ({
  height: `${Math.max(0, splitScreenY.value)}px`,
}))

const executionBandStyle = computed(() => ({
  top: `${Math.max(0, splitScreenY.value)}px`,
}))

const dividerStyle = computed(() => ({
  top: `${splitScreenY.value}px`,
}))

const operationHeaderStyle = computed(() => ({
  top: `${Math.max(-44, Math.min(12, splitScreenY.value - 38))}px`,
}))

const executionHeaderStyle = computed(() => {
  const maxTop = Math.max(12, wrapHeight.value - 38)
  return {
    top: `${Math.max(12, Math.min(maxTop, splitScreenY.value + 12))}px`,
  }
})

const operationCount = computed(() => props.nodes.filter((node) => normalizeLane(node.lane) === 'operation').length)
const executionCount = computed(() => props.nodes.filter((node) => normalizeLane(node.lane) === 'execution').length)

function runFitView(options?: { nodes?: string[]; padding?: number; maxZoom?: number; duration?: number }): void {
  void nextTick(() => {
    const exec = () => {
      void fitView({
        padding: options?.padding ?? 0.22,
        maxZoom: options?.maxZoom ?? 1,
        ...(options?.nodes ? { nodes: options.nodes } : {}),
        ...(options?.duration ? { duration: options.duration } : {}),
      })
    }
    if (typeof requestAnimationFrame === 'function') requestAnimationFrame(exec)
    else exec()
  })
}

function scheduleFit(): void {
  pendingFit = true
  runFitView()
}

function onNodesInitialized(): void {
  if (!pendingFit || props.nodes.length === 0) return
  pendingFit = false
  runFitView()
}

watch(
  () => [props.graphKey, props.nodes.length] as const,
  ([nextKey, nextLen], [prevKey, prevLen]) => {
    if (nextKey !== prevKey) {
      rejection.value = null
      laneNotice.value = null
      highlightedNodeIds.value = new Set()
      highlightedEdgeIds.value = new Set()
      pendingFit = true
      scheduleFit()
    } else if (prevLen === 0 && nextLen > 0) {
      pendingFit = true
      scheduleFit()
    }
  },
)

onMounted(() => {
  scheduleFit()
  if (canvasWrapRef.value && typeof ResizeObserver === 'function') {
    wrapResizeObserver = new ResizeObserver((entries) => {
      const entry = entries[0]
      if (entry && entry.contentRect.height > 0) {
        wrapHeight.value = entry.contentRect.height
      }
    })
    wrapResizeObserver.observe(canvasWrapRef.value)
  }
})

onBeforeUnmount(() => {
  if (rejectionTimer) clearTimeout(rejectionTimer)
  if (laneNoticeTimer) clearTimeout(laneNoticeTimer)
  if (highlightTimer) clearTimeout(highlightTimer)
  wrapResizeObserver?.disconnect()
})

function focusCanvas(): void {
  wrapperRef.value?.focus()
}

function onNodeClick(event: { node: Node }): void {
  focusCanvas()
  emit('select-node', event.node.id)
  emit('select-edge', null)
}

function onEdgeClick(event: { edge: Edge }): void {
  focusCanvas()
  emit('select-edge', event.edge.id)
  emit('select-node', null)
}

function onPaneClick(): void {
  focusCanvas()
  emit('select-node', null)
  emit('select-edge', null)
}

function showLaneNotice(message: string): void {
  laneNotice.value = message
  if (laneNoticeTimer) clearTimeout(laneNoticeTimer)
  laneNoticeTimer = setTimeout(() => {
    laneNotice.value = null
  }, 3200)
}

function commitNodePosition(nodeId: string, rawPos: { x: number; y: number }): void {
  if (props.readonly) return
  const existing = props.nodes.find((n) => n.id === nodeId)
  if (!existing) return

  const snapped = snapPosition(rawPos)
  const boundAgent = existing.agent_id ? agentFor(existing.agent_id) : undefined
  const currentLane = normalizeLane(existing.lane)

  let nextPos = snapped
  let nextLane: ModeLane = currentLane

  if (boundAgent) {
    // Bound nodes have an immutable layer dictated by their agent definition.
    const lockedLane = normalizeLane(boundAgent.layer)
    nextLane = lockedLane
    nextPos = clampToLane(snapped, lockedLane)
    if (laneForY(snapped.y) !== lockedLane) {
      showLaneNotice(t('canvas.laneClamped'))
    }
  } else {
    // An unbound draft node still belongs to the lane it was created in. Letting a
    // drag move it across 治理层/执行层 silently would change what the mode means
    // with no trace, so the crossing is clamped and announced; the lane stays
    // changeable on purpose through the inspector's lane select.
    nextLane = currentLane
    nextPos = clampToLane(snapped, currentLane)
    if (laneForY(snapped.y) !== currentLane || nextPos.x !== snapped.x || nextPos.y !== snapped.y) {
      showLaneNotice(t('canvas.laneClamped'))
    }
  }

  // Keep Vue Flow's internal node position aligned when clamping returns the
  // node to a coordinate that matches its previous prop value.
  const vfNode = findNode(nodeId)
  if (vfNode) {
    vfNode.position = { ...nextPos }
  }

  if (
    existing.position.x === nextPos.x
    && existing.position.y === nextPos.y
    && currentLane === nextLane
  ) {
    return
  }

  emit('node-moved', {
    id: nodeId,
    position: nextPos,
    ...(nextLane !== currentLane ? { lane: nextLane } : {}),
  })
}

function onNodeDragStop(event: NodeDragEvent): void {
  commitNodePosition(event.node.id, event.node.position)
}

function onNodesChange(changes: NodeChange[]): void {
  if (props.readonly) return
  for (const change of changes) {
    // Only commit completed non-drag position updates (arrow-key movement via
    // Vue Flow's useUpdateNodePositions emits dragging: false with position).
    // Drag ticks have dragging: true and are intentionally ignored to avoid jitter.
    if (change.type === 'position' && change.dragging === false && change.position) {
      commitNodePosition(change.id, change.position)
    }
  }
}

function reject(code: string): void {
  rejection.value = code
  if (rejectionTimer) clearTimeout(rejectionTimer)
  rejectionTimer = setTimeout(() => {
    if (rejection.value === code) rejection.value = null
  }, 4000)
}

function checkConnection(
  source: string | null | undefined,
  target: string | null | undefined,
  ignoreEdgeId?: string | null,
): { valid: true } | { valid: false; code: ModeIssueCode } {
  if (props.readonly) return { valid: false, code: 'dangling-endpoint' }
  if (!source || !target) return { valid: false, code: 'dangling-endpoint' }
  if (source === target) return { valid: false, code: 'self-loop' }

  const sourceNode = props.nodes.find((node) => node.id === source)
  const targetNode = props.nodes.find((node) => node.id === target)
  if (!sourceNode || !targetNode) return { valid: false, code: 'dangling-endpoint' }

  const remainingEdges = ignoreEdgeId
    ? props.edges.filter((edge) => edge.id !== ignoreEdgeId)
    : props.edges

  if (remainingEdges.some((edge) => edge.source === source && edge.target === target)) {
    return { valid: false, code: 'duplicate-edge' }
  }

  if (normalizeLane(sourceNode.lane) === 'execution' && normalizeLane(targetNode.lane) === 'operation') {
    return { valid: false, code: 'backflow' }
  }

  const candidate = validateMode(
    props.nodes.map((node) => ({
      id: node.id,
      lane: normalizeLane(node.lane),
      agent_id: node.agent_id,
      config: node.config ?? null,
    })),
    [...remainingEdges, { id: `candidate:${source}->${target}`, source, target }]
      .map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
  )
  if (candidate.some((issue) => issue.code === 'cycle')) {
    return { valid: false, code: 'cycle' }
  }
  return { valid: true }
}

const isValidConnection: ValidConnectionFunc = (connection) => {
  return checkConnection(connection.source, connection.target, updatingEdgeId.value).valid
}

function onConnect(connection: Connection): void {
  if (props.readonly) return
  const result = checkConnection(connection.source, connection.target)
  if (!result.valid) {
    reject(result.code)
    return
  }
  rejection.value = null
  emit('connect', { source: connection.source, target: connection.target })
}

function onEdgeUpdateStart(event: EdgeMouseEvent): void {
  updatingEdgeId.value = event.edge.id
}

function onEdgeUpdateEnd(): void {
  updatingEdgeId.value = null
}

function onEdgeUpdate(event: EdgeUpdateEvent): void {
  updatingEdgeId.value = null
  if (props.readonly) return
  const { edge, connection } = event
  const result = checkConnection(connection.source, connection.target, edge.id)
  if (!result.valid) {
    reject(result.code)
    return
  }
  rejection.value = null
  emit('update-edge', { id: edge.id, source: connection.source, target: connection.target })
}

function onKeydown(event: KeyboardEvent): void {
  if (event.defaultPrevented || props.readonly) return
  const target = event.target as HTMLElement | null
  if (target && (/^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable)) return

  if (event.key === 'Delete' || event.key === 'Backspace') {
    if (props.selectedNodeId) {
      event.preventDefault()
      emit('delete-node', props.selectedNodeId)
      return
    }
    if (props.selectedEdgeId) {
      event.preventDefault()
      emit('delete-edge', props.selectedEdgeId)
      return
    }
  }
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
    event.preventDefault()
    if (event.shiftKey) emit('redo')
    else emit('undo')
    return
  }
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y') {
    event.preventDefault()
    emit('redo')
    return
  }
  if (event.key === 'Escape') {
    emit('select-node', null)
    emit('select-edge', null)
  }
}

function selectIssue(entry: { nodes: string[]; edges: string[] }): void {
  const focusNodes = new Set<string>(entry.nodes)
  const focusEdges = new Set<string>(entry.edges)

  for (const edgeId of entry.edges) {
    const edge = props.edges.find((e) => e.id === edgeId)
    if (edge) {
      focusNodes.add(edge.source)
      focusNodes.add(edge.target)
    }
  }

  highlightedNodeIds.value = focusNodes
  highlightedEdgeIds.value = focusEdges
  if (highlightTimer) clearTimeout(highlightTimer)
  highlightTimer = setTimeout(() => {
    highlightedNodeIds.value = new Set()
    highlightedEdgeIds.value = new Set()
  }, 2800)

  if (entry.nodes[0]) {
    emit('select-node', entry.nodes[0])
    emit('select-edge', null)
  } else if (entry.edges[0]) {
    emit('select-edge', entry.edges[0])
    emit('select-node', null)
  }

  const nodeList = [...focusNodes].filter((id) => props.nodes.some((n) => n.id === id))
  if (nodeList.length > 0) {
    runFitView({ nodes: nodeList, padding: 0.45, maxZoom: 1.1, duration: 260 })
  }
}

function requestAdd(lane: ModeLane): void {
  if (props.readonly) return
  focusCanvas()
  emit('add-node', { lane })
}

function handleAutoLayout(): void {
  if (props.readonly) return
  pendingFit = true
  emit('auto-layout')
  runFitView({ duration: 220 })
}

function miniMapNodeColor(node: GraphNode<ModeAgentNodeData>): string {
  return node.data?.lane === 'execution'
    ? 'color-mix(in srgb, var(--accent-warning, #d97706) 55%, var(--surface-raised))'
    : 'color-mix(in srgb, var(--accent-primary) 55%, var(--surface-raised))'
}

function miniMapStrokeColor(node: GraphNode<ModeAgentNodeData>): string {
  if (node.data?.selected) return 'var(--accent-primary)'
  return 'var(--border-muted)'
}

defineExpose({
  fitView: scheduleFit,
  zoomIn,
  zoomOut,
  /** Test seam for the same handler Vue Flow's `connect` event calls. */
  connect: onConnect,
})
</script>

<template>
  <div
    ref="wrapperRef"
    class="mode-graph-canvas"
    tabindex="0"
    :data-flow-id="FLOW_ID"
    @keydown="onKeydown"
  >
    <div class="canvas-toolbar">
      <div class="canvas-toolbar-group">
        <button type="button" class="canvas-tool" :aria-label="t('canvas.zoomOut')" :title="t('canvas.zoomOut')" @click="zoomOut()">
          <ZoomOut :size="14" />
        </button>
        <button type="button" class="canvas-tool" :aria-label="t('canvas.zoomIn')" :title="t('canvas.zoomIn')" @click="zoomIn()">
          <ZoomIn :size="14" />
        </button>
        <button type="button" class="canvas-tool" :aria-label="t('canvas.fitView')" :title="t('canvas.fitView')" @click="scheduleFit">
          <Maximize2 :size="14" />
        </button>
        <button
          type="button"
          class="canvas-tool"
          :aria-label="t('canvas.autoLayout')"
          :title="t('canvas.autoLayout')"
          :disabled="readonly || nodes.length < 2"
          @click="handleAutoLayout"
        >
          <LayoutGrid :size="14" />
        </button>
        <span class="toolbar-sep" aria-hidden="true" />
        <button
          type="button"
          class="canvas-tool-pill op"
          :disabled="readonly || agents.length === 0"
          @click="requestAdd('operation')"
        >
          <Plus :size="13" />
          <span>{{ t('canvas.addGovernanceNode') }}</span>
        </button>
        <button
          type="button"
          class="canvas-tool-pill ex"
          :disabled="readonly || agents.length === 0"
          @click="requestAdd('execution')"
        >
          <Plus :size="13" />
          <span>{{ t('canvas.addExecutionNode') }}</span>
        </button>
      </div>

      <div class="canvas-toolbar-group canvas-status">
        <span v-if="laneNotice" class="lane-notice" role="status">{{ laneNotice }}</span>
        <span v-if="issueEntries.length === 0" class="canvas-valid-pill">
          <CheckCircle2 :size="12" />
          <span>{{ t('canvas.noIssues') }}</span>
        </span>
        <span v-else class="canvas-issues-pill">
          <AlertTriangle :size="12" />
          <span>{{ t('canvas.issueCount', { count: issueEntries.length }) }}</span>
        </span>
      </div>
    </div>

    <div v-if="issueEntries.length" class="canvas-issues" role="status" aria-live="polite">
      <AlertTriangle :size="13" aria-hidden="true" />
      <button
        v-for="entry in issueEntries"
        :key="entry.key"
        type="button"
        class="canvas-issue"
        :class="entry.severity"
        :title="entry.text"
        @click="selectIssue(entry)"
      >
        {{ entry.text }}
      </button>
    </div>

    <div ref="canvasWrapRef" class="canvas-wrap">
      <!-- Infinite-width swimlane background bands mapped to world Y = MODE_LANE_SPLIT_Y -->
      <div class="lane-backdrop" aria-hidden="true">
        <div class="lane-band op" :style="operationBandStyle" />
        <div class="lane-band ex" :style="executionBandStyle" />
        <div class="lane-divider" :style="dividerStyle" />
      </div>

      <!-- Viewport-fixed left swimlane headers ("治理层" / "执行层") -->
      <div class="lane-chrome" aria-hidden="true">
        <div class="lane-header op" :style="operationHeaderStyle">
          <Workflow :size="12" />
          <span>{{ t('agentCenter.filter.operationLayer') }}</span>
          <em>{{ operationCount }}</em>
        </div>
        <div class="lane-header ex" :style="executionHeaderStyle">
          <Cpu :size="12" />
          <span>{{ t('agentCenter.filter.executionLayer') }}</span>
          <em>{{ executionCount }}</em>
        </div>
      </div>

      <VueFlow
        :id="FLOW_ID"
        :nodes="vfNodes"
        :edges="vfEdges"
        :min-zoom="0.25"
        :max-zoom="1.8"
        :nodes-draggable="!readonly"
        :nodes-connectable="!readonly"
        :edges-updatable="!readonly"
        :is-valid-connection="isValidConnection"
        :delete-key-code="null"
        :snap-to-grid="true"
        :snap-grid="[MODE_GRID_SIZE, MODE_GRID_SIZE]"
        class="vf"
        @nodes-initialized="onNodesInitialized"
        @nodes-change="onNodesChange"
        @node-click="onNodeClick"
        @node-drag-stop="onNodeDragStop"
        @edge-click="onEdgeClick"
        @edge-update-start="onEdgeUpdateStart"
        @edge-update="onEdgeUpdate"
        @edge-update-end="onEdgeUpdateEnd"
        @pane-click="onPaneClick"
        @connect="onConnect"
      >
        <Background :gap="20" pattern-color="var(--border-muted)" />
        <Controls :show-interactive="false" position="bottom-left" class="vf-controls" />
        <MiniMap
          pannable
          zoomable
          position="bottom-right"
          :width="148"
          :height="92"
          :node-color="miniMapNodeColor"
          :node-stroke-color="miniMapStrokeColor"
          mask-color="color-mix(in srgb, var(--surface-canvas, #0f1116) 55%, transparent)"
          class="vf-minimap"
        />
        <Panel position="top-right" class="vf-panel">
          {{ readonly ? t('canvas.readonlyHint') : t('canvas.modeHint') }}
        </Panel>
        <template #node-agentNode="{ id, data }">
          <ModeAgentNode
            :id="id"
            :data="data"
            @set-conversation="emit('set-conversation', $event)"
            @clone-node="emit('clone-node', $event)"
            @delete-node="emit('delete-node', $event)"
          />
        </template>
      </VueFlow>

      <div v-if="nodes.length === 0" class="canvas-empty">
        <strong>{{ t('canvas.emptyTitle') }}</strong>
        <span>{{ readonly ? t('canvas.emptyReadonly') : t('canvas.emptyHint') }}</span>
        <div v-if="!readonly" class="canvas-empty-actions">
          <button
            type="button"
            class="canvas-empty-action"
            :disabled="agents.length === 0"
            @click="requestAdd('operation')"
          >
            <Plus :size="13" />{{ t('canvas.addGovernanceNode') }}
          </button>
          <button
            type="button"
            class="canvas-empty-action"
            :disabled="agents.length === 0"
            @click="requestAdd('execution')"
          >
            <Plus :size="13" />{{ t('canvas.addExecutionNode') }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.mode-graph-canvas {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
  min-height: 0;
  outline: none;
}

.mode-graph-canvas:focus-visible .canvas-wrap {
  border-color: var(--accent-primary);
}

.canvas-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.canvas-toolbar-group {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.toolbar-sep {
  width: 1px;
  height: 18px;
  margin: 0 4px;
  background: var(--border-muted);
}

.canvas-tool {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  cursor: pointer;
}

.canvas-tool:hover:not(:disabled) {
  border-color: var(--accent-primary);
  color: var(--text-primary);
}

.canvas-tool:disabled {
  opacity: 0.4;
  cursor: default;
}

.canvas-tool-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 10px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}

.canvas-tool-pill.op:hover:not(:disabled) {
  border-color: var(--accent-primary);
}

.canvas-tool-pill.ex:hover:not(:disabled) {
  border-color: var(--accent-warning, #d97706);
}

.canvas-tool-pill:disabled {
  opacity: 0.42;
  cursor: default;
}

.canvas-status {
  gap: 6px;
}

.lane-notice {
  padding: 3px 8px;
  border-radius: 999px;
  font-size: 11px;
  color: var(--accent-warning, #d97706);
  background: color-mix(in srgb, var(--accent-warning, #d97706) 10%, var(--surface-section));
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 35%, var(--border-muted));
}

.canvas-valid-pill,
.canvas-issues-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 9px;
  border-radius: 999px;
  font-size: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.canvas-valid-pill {
  color: var(--accent-success, #16a34a);
}

.canvas-issues-pill {
  color: var(--accent-warning, #d97706);
}

.canvas-issues {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  padding: 6px 10px;
  border-radius: 8px;
  font-size: 11px;
  color: var(--text-secondary);
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
}

.canvas-issue {
  padding: 2px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.canvas-issue:hover {
  background: var(--surface-hover);
}

.canvas-issue.error {
  border-color: color-mix(in srgb, var(--accent-danger, #ef4444) 40%, var(--border-muted));
  color: var(--accent-danger, #ef4444);
}

.canvas-issue.warning {
  border-color: color-mix(in srgb, var(--accent-warning, #d97706) 40%, var(--border-muted));
  color: var(--accent-warning, #d97706);
}

.canvas-wrap {
  position: relative;
  flex: 1 1 auto;
  min-height: 460px;
  border: 1px solid var(--border-muted);
  border-radius: 12px;
  overflow: hidden;
  background: var(--surface-section);
}

.lane-backdrop,
.lane-chrome {
  position: absolute;
  inset: 0;
  pointer-events: none;
  overflow: hidden;
}

.lane-backdrop {
  z-index: 0;
}

.lane-chrome {
  z-index: 3;
}

.lane-band {
  position: absolute;
  left: 0;
  right: 0;
}

.lane-band.op {
  top: 0;
  background: color-mix(in srgb, var(--accent-primary) 4%, transparent);
}

.lane-band.ex {
  bottom: 0;
  background: color-mix(in srgb, var(--accent-warning, #d97706) 3%, transparent);
}

.lane-divider {
  position: absolute;
  left: 0;
  right: 0;
  height: 0;
  border-top: 1px dashed color-mix(in srgb, var(--border-default) 70%, transparent);
}

.lane-header {
  position: absolute;
  left: 12px;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
  background: color-mix(in srgb, var(--surface-raised) 88%, transparent);
  backdrop-filter: blur(6px);
  border: 1px solid var(--border-muted);
}

.lane-header.op {
  border-left: 3px solid var(--accent-primary);
}

.lane-header.ex {
  border-left: 3px solid var(--accent-warning, #d97706);
}

.lane-header em {
  font-style: normal;
  color: var(--text-primary);
  font-weight: 600;
}

.vf {
  position: relative;
  z-index: 1;
  width: 100%;
  height: 100%;
  background: transparent;
}

.vf :deep(.vue-flow__pane) {
  cursor: grab;
}

.vf :deep(.vue-flow__edge.selected .vue-flow__edge-path) {
  stroke: var(--accent-primary);
}

.vf :deep(.vue-flow__connection-path) {
  stroke: var(--accent-primary);
  stroke-width: 2;
}

.vf-panel {
  font-size: 11px;
  color: var(--text-muted);
  background: color-mix(in srgb, var(--surface-raised) 90%, transparent);
  border: 1px solid var(--border-muted);
  padding: 4px 8px;
  border-radius: 6px;
}

.vf :deep(.vue-flow__controls) {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 3px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  box-shadow: 0 4px 12px color-mix(in srgb, #000 12%, transparent);
}

.vf :deep(.vue-flow__controls-button) {
  width: 24px;
  height: 24px;
  padding: 4px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  fill: currentColor;
}

.vf :deep(.vue-flow__controls-button:hover) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.vf :deep(.vue-flow__minimap) {
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  overflow: hidden;
  box-shadow: 0 4px 12px color-mix(in srgb, #000 12%, transparent);
}

.canvas-empty {
  position: absolute;
  inset: 0;
  z-index: 4;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  text-align: center;
  padding: 24px;
  pointer-events: none;
}

.canvas-empty strong {
  font-size: 14px;
  color: var(--text-primary);
}

.canvas-empty span {
  font-size: 12px;
  color: var(--text-secondary);
  max-width: 360px;
}

.canvas-empty-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  justify-content: center;
}

.canvas-empty-action {
  pointer-events: auto;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
  padding: 6px 12px;
  border-radius: 8px;
  cursor: pointer;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 12px;
}

.canvas-empty-action:hover:not(:disabled) {
  border-color: var(--accent-primary);
}

.canvas-empty-action:disabled {
  opacity: 0.5;
  cursor: default;
}
</style>

<style>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';
@import '@vue-flow/controls/dist/style.css';
@import '@vue-flow/minimap/dist/style.css';
</style>
