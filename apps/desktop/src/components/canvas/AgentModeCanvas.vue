<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Handle,
  MarkerType,
  Panel,
  Position,
  VueFlow,
  useVueFlow,
  type Connection,
  type Edge,
  type Node,
  type NodeDragEvent,
} from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import { AlertTriangle, Cpu, LayoutGrid, Maximize2, Plus, Workflow, ZoomIn, ZoomOut } from '@lucide/vue'
import type { AgentDefinitionDto, AgentModeEdgeDto, AgentModeNodeDto } from '@/api'
import { pickLocalized, pickNodeLabel } from '@/lib/packI18n'
import { validateMode, type ModeIssue } from './modeValidation'

const FLOW_ID = 'agent-mode-editor'

const { t, locale } = useI18n()
const props = defineProps<{
  nodes: AgentModeNodeDto[]
  edges: AgentModeEdgeDto[]
  agents: AgentDefinitionDto[]
  /** Pack-managed/published modes render inspect-only: no drag, no connect, no delete. */
  readonly?: boolean
  selectedNodeId?: string | null
  selectedEdgeId?: string | null
  /** Changes when the selected mode changes; triggers a re-fit. */
  graphKey?: string | null
}>()
const emit = defineEmits<{
  'select-node': [id: string | null]
  'select-edge': [id: string | null]
  'node-moved': [payload: { id: string; position: { x: number; y: number } }]
  connect: [payload: { source: string; target: string }]
  'delete-node': [id: string]
  'delete-edge': [id: string]
  'add-node': [payload: { lane: 'operation' | 'execution' }]
  'auto-layout': []
  undo: []
  redo: []
}>()

const wrapperRef = ref<HTMLElement | null>(null)
const { viewport, zoomIn, zoomOut, fitView } = useVueFlow(FLOW_ID)
const rejection = ref<string | null>(null)

function normalizeLane(value: unknown): 'operation' | 'execution' {
  return String(value ?? '').trim().toLowerCase() === 'execution' ? 'execution' : 'operation'
}

function agentFor(id: string) {
  return props.agents.find((agent) => agent.id === id)
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

const vfNodes = computed<Node[]>(() =>
  props.nodes.map((node) => {
    const agent = agentFor(node.agent_id)
    const capabilities = (agent?.capabilities ?? []).slice(0, 2).join(' · ')
    const agentName = agent
      ? pickLocalized(agent.i18n, locale.value, 'display_name', agent.display_name ?? agent.name) ?? agent.id
      : node.agent_id?.slice(0, 8) ?? t('canvas.unbound')
    return {
      id: node.id,
      type: 'custom',
      position: node.position,
      draggable: !props.readonly,
      connectable: !props.readonly,
      data: {
        label: pickNodeLabel(node, locale.value) || agent?.name || node.agent_id || '',
        agentName,
        lane: normalizeLane(node.lane),
        role: agent?.agent_type ?? '—',
        capability: capabilities || '—',
        toolCount: (agent?.allowed_tools ?? []).length,
        selected: props.selectedNodeId === node.id,
        isConversation: node.config?.conversation === true,
        unbound: !node.agent_id,
      },
      style: {
        background: 'transparent',
        border: 'none',
        padding: '0',
        width: '184px',
      },
    }
  }),
)

const vfEdges = computed<Edge[]>(() =>
  props.edges.map((edge) => {
    const selected = props.selectedEdgeId === edge.id
    return {
      id: edge.id,
      source: edge.source,
      target: edge.target,
      type: 'smoothstep',
      label: edgeLabel(edge),
      animated: false,
      interactionWidth: 32,
      markerEnd: {
        type: MarkerType.ArrowClosed,
        width: 16,
        height: 16,
        color: selected ? 'var(--accent-primary)' : 'var(--border-default)',
      },
      style: {
        stroke: selected ? 'var(--accent-primary)' : 'var(--border-default)',
        strokeWidth: selected ? 2.2 : 1.6,
      },
      labelStyle: { fontSize: '10px', fill: 'var(--text-secondary)' },
      labelBgStyle: { fill: 'var(--surface-raised)', fillOpacity: 0.9 },
      labelBgPadding: [4, 2] as [number, number],
      labelBgBorderRadius: 4,
    }
  }),
)

const laneLayerStyle = computed(() => ({
  transform: `translate(${viewport.value.x}px, ${viewport.value.y}px) scale(${viewport.value.zoom})`,
}))

const operationCount = computed(() => props.nodes.filter((node) => normalizeLane(node.lane) === 'operation').length)
const executionCount = computed(() => props.nodes.filter((node) => normalizeLane(node.lane) === 'execution').length)

function scheduleFit(): void {
  void nextTick(() => {
    const run = () => { void fitView({ padding: 0.2, maxZoom: 1 }) }
    if (typeof requestAnimationFrame === 'function') requestAnimationFrame(run)
    else run()
  })
}

watch(() => props.graphKey, () => { rejection.value = null; scheduleFit() })
watch(() => props.nodes.length, (next, previous) => {
  if (previous === 0 && next > 0) scheduleFit()
})
onMounted(scheduleFit)

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

function onNodeDragStop(event: NodeDragEvent): void {
  if (props.readonly) return
  const { x, y } = event.node.position
  emit('node-moved', { id: event.node.id, position: { x, y } })
}

function reject(code: string): void {
  rejection.value = code
  window.setTimeout(() => {
    if (rejection.value === code) rejection.value = null
  }, 4000)
}

function onConnect(connection: Connection): void {
  if (props.readonly) return
  const { source, target } = connection
  if (!source || !target) return
  if (source === target) { reject('self-loop'); return }
  if (props.edges.some((edge) => edge.source === source && edge.target === target)) {
    reject('duplicate-edge')
    return
  }
  const sourceNode = props.nodes.find((node) => node.id === source)
  const targetNode = props.nodes.find((node) => node.id === target)
  if (!sourceNode || !targetNode) { reject('dangling-endpoint'); return }
  if (normalizeLane(sourceNode.lane) === 'execution' && normalizeLane(targetNode.lane) === 'operation') {
    reject('backflow')
    return
  }
  const candidate = validateMode(
    props.nodes.map((node) => ({
      id: node.id,
      lane: normalizeLane(node.lane),
      agent_id: node.agent_id,
      config: node.config ?? null,
    })),
    [...props.edges, { id: `candidate:${source}->${target}`, source, target }]
      .map((edge) => ({ id: edge.id, source: edge.source, target: edge.target })),
  )
  const cycle = candidate.find((issue) => issue.code === 'cycle')
  if (cycle) { reject('cycle'); return }
  rejection.value = null
  emit('connect', { source, target })
}

function onKeydown(event: KeyboardEvent): void {
  if (event.defaultPrevented || props.readonly) return
  const target = event.target as HTMLElement | null
  if (target && /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return
  if (event.key === 'Delete' || event.key === 'Backspace') {
    if (props.selectedNodeId) { event.preventDefault(); emit('delete-node', props.selectedNodeId); return }
    if (props.selectedEdgeId) { event.preventDefault(); emit('delete-edge', props.selectedEdgeId); return }
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
  if (entry.nodes[0]) emit('select-node', entry.nodes[0])
  else if (entry.edges[0]) emit('select-edge', entry.edges[0])
}

function requestAdd(lane: 'operation' | 'execution'): void {
  if (props.readonly) return
  focusCanvas()
  emit('add-node', { lane })
}

defineExpose({
  fitView: scheduleFit,
  zoomIn,
  zoomOut,
  /** Test seam for the same handler the Vue Flow `connect` event calls. */
  connect: onConnect,
})
</script>

<template>
  <div
    ref="wrapperRef"
    class="agent-mode-canvas"
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
          @click="emit('auto-layout')"
        >
          <LayoutGrid :size="14" />
        </button>
        <button
          type="button"
          class="canvas-tool"
          :aria-label="t('canvas.addNode')"
          :title="t('canvas.addNode')"
          :disabled="readonly || agents.length === 0"
          @click="requestAdd('operation')"
        >
          <Plus :size="14" />
        </button>
      </div>
      <div class="canvas-toolbar-group canvas-legend">
        <span class="legend-lane op"><Workflow :size="12" />{{ t('workbench.operationLane') }}<em>{{ operationCount }}</em></span>
        <span class="legend-lane ex"><Cpu :size="12" />{{ t('workbench.executionLane') }}<em>{{ executionCount }}</em></span>
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
      >{{ entry.text }}</button>
    </div>

    <div class="canvas-wrap">
      <div class="lane-layer" aria-hidden="true" :style="laneLayerStyle">
        <div class="lane-band op" />
        <div class="lane-band ex" />
        <span class="lane-band-label op">{{ t('workbench.operationLane') }}</span>
        <span class="lane-band-label ex">{{ t('workbench.executionLane') }}</span>
      </div>
      <VueFlow
        :id="FLOW_ID"
        :nodes="vfNodes"
        :edges="vfEdges"
        :min-zoom="0.25"
        :max-zoom="1.8"
        :nodes-draggable="!readonly"
        :nodes-connectable="!readonly"
        :edges-updatable="false"
        :delete-key-code="null"
        :snap-to-grid="true"
        :snap-grid="[20, 20]"
        class="vf"
        @node-click="onNodeClick"
        @node-drag-stop="onNodeDragStop"
        @edge-click="onEdgeClick"
        @pane-click="onPaneClick"
        @connect="onConnect"
      >
        <Background :gap="24" />
        <Panel position="top-right" class="vf-panel">
          {{ readonly ? t('canvas.readonlyHint') : t('canvas.modeHint') }}
        </Panel>
        <template #node-custom="{ data }">
          <div
            class="am-node"
            :class="{ sel: data.selected, op: data.lane === 'operation', ex: data.lane === 'execution', unbound: data.unbound }"
          >
            <Handle type="target" :position="Position.Left" :connectable="!readonly" />
            <div class="am-node-head">
              <span class="am-node-icon"><Workflow v-if="data.lane === 'operation'" :size="12" /><Cpu v-else :size="12" /></span>
              <strong :title="data.agentName">{{ data.agentName }}</strong>
              <span class="am-node-tool">{{ t('canvas.toolCount', { n: data.toolCount }) }}</span>
            </div>
            <div class="am-node-label" :title="data.label">{{ data.label }}</div>
            <div class="am-node-meta">
              <span>{{ data.role }}</span>
              <span class="dot">·</span>
              <span :title="data.capability">{{ data.capability }}</span>
              <em v-if="data.isConversation" class="am-node-conversation">{{ t('canvas.conversationBadge') }}</em>
            </div>
            <Handle type="source" :position="Position.Right" :connectable="!readonly" />
          </div>
        </template>
      </VueFlow>
      <div v-if="nodes.length === 0" class="canvas-empty">
        <strong>{{ t('canvas.emptyTitle') }}</strong>
        <span>{{ readonly ? t('canvas.emptyReadonly') : t('canvas.emptyHint') }}</span>
        <button
          v-if="!readonly"
          type="button"
          class="canvas-empty-action"
          :disabled="agents.length === 0"
          @click="requestAdd('operation')"
        >
          <Plus :size="13" />{{ t('canvas.addNode') }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.agent-mode-canvas { display: grid; gap: 8px; outline: none; }
.agent-mode-canvas:focus-visible .canvas-wrap { border-color: var(--accent-primary); }

.canvas-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 8px; flex-wrap: wrap; }
.canvas-toolbar-group { display: flex; align-items: center; gap: 4px; }
.canvas-tool {
  display: inline-flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; border-radius: 7px; border: 1px solid var(--border-muted);
  background: var(--surface-raised); color: var(--text-secondary); cursor: pointer;
}
.canvas-tool:hover:not(:disabled) { border-color: var(--accent-primary); color: var(--text-primary); }
.canvas-tool:disabled { opacity: .4; cursor: default; }

.canvas-legend { gap: 6px; }
.legend-lane {
  display: inline-flex; align-items: center; gap: 5px; padding: 3px 8px;
  border-radius: 999px; font-size: 11px; color: var(--text-secondary);
  background: var(--surface-section); border: 1px solid var(--border-muted);
}
.legend-lane em { font-style: normal; color: var(--text-primary); }
.legend-lane.op { border-left: 3px solid color-mix(in srgb, var(--accent-primary) 70%, var(--border-muted)); }
.legend-lane.ex { border-left: 3px solid color-mix(in srgb, var(--accent-warning, #d97706) 65%, var(--border-muted)); }

.canvas-issues {
  display: flex; align-items: center; gap: 6px; flex-wrap: wrap;
  padding: 6px 10px; border-radius: 8px; font-size: 11px;
  color: var(--text-secondary); background: var(--surface-section); border: 1px solid var(--border-muted);
}
.canvas-issue {
  padding: 2px 8px; border-radius: 999px; border: 1px solid var(--border-muted);
  background: transparent; color: var(--text-secondary); font-size: 11px; cursor: pointer;
}
.canvas-issue.error { border-color: color-mix(in srgb, var(--accent-danger) 35%, var(--border-muted)); color: var(--accent-danger); }
.canvas-issue.warning { border-color: color-mix(in srgb, var(--accent-warning, #d97706) 35%, var(--border-muted)); color: var(--accent-warning, #d97706); }

.canvas-wrap {
  position: relative; height: clamp(520px, 68vh, 720px);
  border: 1px solid var(--border-muted); border-radius: 12px;
  overflow: hidden; background: var(--surface-section);
}

.lane-layer {
  position: absolute; inset: 0; pointer-events: none; z-index: 0;
  transform-origin: 0 0; will-change: transform;
}
.lane-band { position: absolute; left: -2000px; width: 6000px; }
.lane-band.op { top: 0; height: 200px; background: color-mix(in srgb, var(--accent-primary) 4%, transparent); border-bottom: 1px dashed var(--border-muted); }
.lane-band.ex { top: 200px; height: 2000px; background: color-mix(in srgb, var(--accent-warning, #d97706) 3%, transparent); }
.lane-band-label { position: absolute; left: 12px; font-size: 10px; letter-spacing: .06em; text-transform: uppercase; color: var(--text-muted); }
.lane-band-label.op { top: 8px; }
.lane-band-label.ex { top: 208px; }

.vf { width: 100%; height: 100%; background: transparent; }
.vf :deep(.vue-flow__pane) { cursor: grab; }
.vf :deep(.vue-flow__edge.selected .vue-flow__edge-path) { stroke: var(--accent-primary); }
.vf-panel { font-size: 11px; color: var(--text-muted); background: var(--surface-raised); padding: 4px 8px; border-radius: 6px; }

.am-node {
  position: relative; width: 184px; padding: 8px 10px; border-radius: 10px;
  background: var(--surface-raised); border: 1px solid var(--border-muted);
  display: grid; gap: 4px; box-shadow: 0 1px 4px rgba(0,0,0,0.06);
}
.am-node.sel { border-color: var(--accent-primary); box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 22%, transparent); }
.am-node.op { border-left: 3px solid color-mix(in srgb, var(--accent-primary) 70%, var(--border-muted)); }
.am-node.ex { border-left: 3px solid color-mix(in srgb, var(--accent-warning, #d97706) 65%, var(--border-muted)); }
.am-node.unbound { border-style: dashed; }
.am-node-head { display: flex; align-items: center; gap: 6px; min-width: 0; }
.am-node-icon { width: 18px; height: 18px; border-radius: 6px; display: grid; place-items: center; background: var(--surface-section); color: var(--text-secondary); flex-shrink: 0; }
.am-node-head strong { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; color: var(--text-primary); }
.am-node-tool { font-size: 10px; color: var(--text-muted); background: var(--surface-section); padding: 1px 5px; border-radius: 999px; }
.am-node-label { font-size: 11px; color: var(--text-secondary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.am-node-meta { display: flex; align-items: center; gap: 4px; font-size: 10px; color: var(--text-muted); overflow: hidden; }
.am-node-meta span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.am-node-conversation { margin-left: auto; font-style: normal; padding: 1px 5px; border-radius: 999px; font-size: 9px; color: var(--accent-primary); border: 1px solid color-mix(in srgb, var(--accent-primary) 40%, transparent); }
.dot { flex-shrink: 0; }

.canvas-empty {
  position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center;
  gap: 8px; text-align: center; padding: 24px; pointer-events: none;
}
.canvas-empty strong { font-size: 14px; color: var(--text-primary); }
.canvas-empty span { font-size: 12px; color: var(--text-secondary); max-width: 360px; }
.canvas-empty-action {
  pointer-events: auto; display: inline-flex; align-items: center; gap: 6px;
  margin-top: 4px; padding: 6px 12px; border-radius: 8px; cursor: pointer;
  border: 1px solid var(--border-muted); background: var(--surface-raised); color: var(--text-primary); font-size: 12px;
}
.canvas-empty-action:disabled { opacity: .5; cursor: default; }
</style>

<style>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';
</style>
