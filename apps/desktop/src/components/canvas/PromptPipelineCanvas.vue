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
import { Graph, layout } from '@dagrejs/dagre'
import { LayoutGrid, Maximize2, Plus, Rocket, Save, Trash2 } from '@lucide/vue'
import type { PromptPipelineDto, PromptPipelineEdgeDto, PromptPipelineNodeDto } from '@/api'

const FLOW_ID = 'prompt-pipeline-editor'
const NODE_WIDTH = 208
const NODE_HEIGHT = 76

const { t } = useI18n()
const props = withDefaults(
  defineProps<{
    pipeline: PromptPipelineDto | null
    selectedNodeId?: string | null
    selectedEdgeId?: string | null
    readonly?: boolean
    dirty?: boolean
    saving?: boolean
    publishing?: boolean
  }>(),
  {
    selectedNodeId: null,
    selectedEdgeId: null,
    readonly: false,
    dirty: false,
    saving: false,
    publishing: false,
  },
)

const emit = defineEmits<{
  'update:nodes': [nodes: PromptPipelineNodeDto[]]
  'update:edges': [edges: PromptPipelineEdgeDto[]]
  'select-node': [node: PromptPipelineNodeDto | null]
  'select-edge': [edge: PromptPipelineEdgeDto | null]
  'save-draft': []
  publish: []
}>()

const wrapperRef = ref<HTMLElement | null>(null)
const { fitView } = useVueFlow(FLOW_ID)

const localSelectedNodeId = ref<string | null>(props.selectedNodeId ?? null)
const localSelectedEdgeId = ref<string | null>(props.selectedEdgeId ?? null)
const warn = ref('')

watch(
  () => props.selectedNodeId,
  (val) => {
    localSelectedNodeId.value = val ?? null
  },
)
watch(
  () => props.selectedEdgeId,
  (val) => {
    localSelectedEdgeId.value = val ?? null
  },
)

function edgeIdOf(edge: PromptPipelineEdgeDto): string {
  return edge.id || `${edge.source}->${edge.target}`
}

function nodeTypeOf(node: PromptPipelineNodeDto): string {
  return String(node.type ?? node.kind ?? 'template').toLowerCase()
}

function nodeLabelOf(node: PromptPipelineNodeDto): string {
  return String(node.label ?? node.title ?? node.key ?? node.id)
}

function nodeSnippetOf(node: PromptPipelineNodeDto): string {
  const cfg = node.config ?? node.data ?? {}
  const type = nodeTypeOf(node)
  if (type === 'fragment_ref') {
    return String(cfg.fragment_key ?? cfg.fragment_id ?? '')
  }
  if (type === 'condition') {
    const field = String(cfg.field ?? cfg.condition ?? '')
    const value = cfg.value !== undefined && cfg.value !== '' ? ` = ${String(cfg.value)}` : ''
    return `${field}${value}`.trim()
  }
  const content = String(cfg.content ?? cfg.template ?? cfg.text ?? '')
  return content ? content.replace(/\s+/g, ' ').slice(0, 48) : ''
}

const rawNodes = computed<PromptPipelineNodeDto[]>(() => {
  const g = props.pipeline?.graph
  if (g && Array.isArray(g.nodes)) return g.nodes
  if (Array.isArray(props.pipeline?.nodes)) return props.pipeline.nodes
  return []
})

const rawEdges = computed<PromptPipelineEdgeDto[]>(() => {
  const g = props.pipeline?.graph
  if (g && Array.isArray(g.edges)) return g.edges
  if (Array.isArray(props.pipeline?.edges)) return props.pipeline.edges
  return []
})

function computeDagrePositions(
  nodes: readonly PromptPipelineNodeDto[],
  edges: readonly PromptPipelineEdgeDto[],
): Map<string, { x: number; y: number }> {
  const result = new Map<string, { x: number; y: number }>()
  if (nodes.length === 0) return result

  const g = new Graph()
  g.setGraph({ rankdir: 'LR', nodesep: 42, ranksep: 72, marginx: 24, marginy: 24 })
  g.setDefaultEdgeLabel(() => ({}))

  const ids = new Set<string>()
  for (const n of nodes) {
    ids.add(n.id)
    g.setNode(n.id, { width: NODE_WIDTH, height: NODE_HEIGHT })
  }
  for (const e of edges) {
    if (e.source !== e.target && ids.has(e.source) && ids.has(e.target)) {
      g.setEdge(e.source, e.target)
    }
  }
  layout(g)

  for (const n of nodes) {
    const placed = g.node(n.id)
    if (placed && Number.isFinite(placed.x) && Number.isFinite(placed.y)) {
      result.set(n.id, {
        x: Math.round((placed.x - NODE_WIDTH / 2) / 10) * 10,
        y: Math.round((placed.y - NODE_HEIGHT / 2) / 10) * 10,
      })
    }
  }
  return result
}

const fallbackPositions = computed(() => {
  const needsFallback = rawNodes.value.some((n) => !n.position && (n.x == null || n.y == null))
  if (!needsFallback) return new Map<string, { x: number; y: number }>()
  return computeDagrePositions(rawNodes.value, rawEdges.value)
})

const vfNodes = computed<Node[]>(() =>
  rawNodes.value.map((n, index) => {
    const pos =
      n.position ??
      (n.x != null && n.y != null ? { x: Number(n.x), y: Number(n.y) } : undefined) ??
      fallbackPositions.value.get(n.id) ?? { x: 40 + index * 240, y: 80 }
    const isSel = localSelectedNodeId.value === n.id
    return {
      id: n.id,
      type: 'custom',
      position: pos,
      draggable: !props.readonly,
      connectable: !props.readonly,
      data: {
        label: nodeLabelOf(n),
        nodeType: nodeTypeOf(n),
        snippet: nodeSnippetOf(n),
        selected: isSel,
        raw: n,
      },
      style: { background: 'transparent', border: 'none', padding: '0', width: `${NODE_WIDTH}px` },
    }
  }),
)

const vfEdges = computed<Edge[]>(() =>
  rawEdges.value.map((e) => {
    const id = edgeIdOf(e)
    const isSel = localSelectedEdgeId.value === id
    return {
      id,
      source: e.source,
      target: e.target,
      type: 'smoothstep',
      label: e.label ?? '',
      interactionWidth: 32,
      markerEnd: {
        type: MarkerType.ArrowClosed,
        width: 16,
        height: 16,
        color: isSel ? 'var(--accent-primary)' : 'var(--border-default)',
      },
      style: {
        stroke: isSel ? 'var(--accent-primary)' : 'var(--border-default)',
        strokeWidth: isSel ? '2.2' : '1.4',
      },
      labelStyle: { fontSize: '10px', fill: 'var(--text-secondary)' },
      labelBgStyle: { fill: 'var(--surface-raised)', fillOpacity: 0.9 },
    }
  }),
)

function hasCycle(next: readonly PromptPipelineEdgeDto[]): boolean {
  const adj = new Map<string, string[]>()
  for (const e of next) {
    if (!adj.has(e.source)) adj.set(e.source, [])
    adj.get(e.source)!.push(e.target)
  }
  const vis = new Set<string>()
  const stk = new Set<string>()
  const dfs = (u: string): boolean => {
    vis.add(u)
    stk.add(u)
    for (const v of adj.get(u) ?? []) {
      if (!vis.has(v) && dfs(v)) return true
      if (stk.has(v)) return true
    }
    stk.delete(u)
    return false
  }
  for (const n of rawNodes.value) {
    if (!vis.has(n.id) && dfs(n.id)) return true
  }
  return false
}

function isValidConnection(connection: Connection): boolean {
  if (props.readonly || !connection.source || !connection.target) return false
  if (connection.source === connection.target) return false
  if (rawEdges.value.some((e) => e.source === connection.source && e.target === connection.target)) return false
  const candidate: PromptPipelineEdgeDto = {
    id: `e-${connection.source}-${connection.target}`,
    source: connection.source,
    target: connection.target,
  }
  return !hasCycle([...rawEdges.value, candidate])
}

function showWarn(message: string) {
  warn.value = message
  window.setTimeout(() => {
    if (warn.value === message) warn.value = ''
  }, 2200)
}

function handleConnect(c: Connection) {
  if (props.readonly || !c.source || !c.target) return
  if (c.source === c.target) {
    showWarn(t('canvas.noSelfLink'))
    return
  }
  if (rawEdges.value.some((e) => e.source === c.source && e.target === c.target)) return
  const next: PromptPipelineEdgeDto = { id: `e-${c.source}-${c.target}`, source: c.source, target: c.target }
  if (hasCycle([...rawEdges.value, next])) {
    showWarn(t('canvas.cycleBlocked'))
    return
  }
  warn.value = ''
  emit('update:edges', [...rawEdges.value, next])
}

function onNodeClick(e: { node: Node }) {
  localSelectedNodeId.value = e.node.id
  localSelectedEdgeId.value = null
  const n = rawNodes.value.find((x) => x.id === e.node.id) ?? null
  emit('select-node', n)
  emit('select-edge', null)
}

function onEdgeClick(e: { edge: Edge }) {
  localSelectedEdgeId.value = e.edge.id
  localSelectedNodeId.value = null
  const ed = rawEdges.value.find((x) => edgeIdOf(x) === e.edge.id) ?? null
  emit('select-edge', ed)
  emit('select-node', null)
}

function onPaneClick() {
  localSelectedNodeId.value = null
  localSelectedEdgeId.value = null
  emit('select-node', null)
  emit('select-edge', null)
}

function onNodeDragStop(event: NodeDragEvent) {
  if (props.readonly) return
  const pos = {
    x: Math.round(event.node.position.x / 10) * 10,
    y: Math.round(event.node.position.y / 10) * 10,
  }
  const next = rawNodes.value.map((n) => (n.id === event.node.id ? { ...n, position: pos } : n))
  emit('update:nodes', next)
}

function addNode(type: 'template' | 'fragment_ref' | 'condition' | 'assemble') {
  if (props.readonly || !props.pipeline) return
  let idx = rawNodes.value.length + 1
  let id = `${type}_${idx}`
  const existingIds = new Set(rawNodes.value.map((n) => n.id))
  while (existingIds.has(id)) {
    idx++
    id = `${type}_${idx}`
  }

  const maxX = rawNodes.value.reduce((acc, n) => {
    const pos = n.position ?? fallbackPositions.value.get(n.id)
    return Math.max(acc, pos?.x ?? 0)
  }, -200)
  const position = { x: Math.max(40, Math.round((maxX + 260) / 10) * 10), y: 80 + (rawNodes.value.length % 3) * 90 }

  const configByType: Record<typeof type, Record<string, unknown>> = {
    template: { content: '' },
    fragment_ref: { fragment_id: '', fragment_key: '' },
    condition: { field: 'layer', value: 'execution' },
    assemble: { separator: '\n\n' },
  }
  const newNode: PromptPipelineNodeDto = {
    id,
    type,
    label: id,
    position,
    config: configByType[type],
  }
  const nextNodes = [...rawNodes.value, newNode]
  emit('update:nodes', nextNodes)
  localSelectedNodeId.value = newNode.id
  localSelectedEdgeId.value = null
  emit('select-node', newNode)
  emit('select-edge', null)
}

function autoLayout() {
  if (props.readonly || rawNodes.value.length === 0) return
  const positions = computeDagrePositions(rawNodes.value, rawEdges.value)
  const next = rawNodes.value.map((n) => ({
    ...n,
    position: positions.get(n.id) ?? n.position ?? { x: 40, y: 80 },
  }))
  emit('update:nodes', next)
  scheduleFit()
}

function deleteSelected() {
  if (props.readonly || !props.pipeline) return
  if (localSelectedNodeId.value) {
    const targetId = localSelectedNodeId.value
    const nextNodes = rawNodes.value.filter((n) => n.id !== targetId)
    const nextEdges = rawEdges.value.filter((e) => e.source !== targetId && e.target !== targetId)
    localSelectedNodeId.value = null
    emit('update:nodes', nextNodes)
    emit('update:edges', nextEdges)
    emit('select-node', null)
    return
  }
  if (localSelectedEdgeId.value) {
    const targetEdgeId = localSelectedEdgeId.value
    const nextEdges = rawEdges.value.filter((e) => edgeIdOf(e) !== targetEdgeId)
    localSelectedEdgeId.value = null
    emit('update:edges', nextEdges)
    emit('select-edge', null)
  }
}

function onKeydown(event: KeyboardEvent) {
  if (event.defaultPrevented || props.readonly) return
  const target = event.target as HTMLElement | null
  if (target && /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return
  if (event.key === 'Delete' || event.key === 'Backspace') {
    if (localSelectedNodeId.value || localSelectedEdgeId.value) {
      event.preventDefault()
      deleteSelected()
    }
  } else if (event.key === 'Escape') {
    onPaneClick()
  }
}

function scheduleFit() {
  void nextTick(() => {
    const run = () => { void fitView({ padding: 0.2, maxZoom: 1.15 }) }
    if (typeof requestAnimationFrame === 'function') requestAnimationFrame(run)
    else run()
  })
}

watch(() => props.pipeline?.id, () => {
  warn.value = ''
  localSelectedNodeId.value = null
  localSelectedEdgeId.value = null
  scheduleFit()
})

watch(() => rawNodes.value.length, (next, prev) => {
  if (prev === 0 && next > 0) scheduleFit()
})

onMounted(scheduleFit)

defineExpose({ fitView: scheduleFit, autoLayout, connect: handleConnect })
</script>

<template>
  <div ref="wrapperRef" class="prompt-pipeline-canvas" tabindex="0" @keydown="onKeydown">
    <div v-if="pipeline" class="pp-toolbar" data-testid="prompt-pipeline-toolbar">
      <div class="pp-toolbar-group">
        <button type="button" class="pp-tool-btn" :disabled="readonly" @click="addNode('template')">
          <Plus :size="12" /><span>{{ t('settings.promptStudio.addTemplate') }}</span>
        </button>
        <button type="button" class="pp-tool-btn" :disabled="readonly" @click="addNode('fragment_ref')">
          <Plus :size="12" /><span>{{ t('settings.promptStudio.addFragmentRef') }}</span>
        </button>
        <button type="button" class="pp-tool-btn" :disabled="readonly" @click="addNode('condition')">
          <Plus :size="12" /><span>{{ t('settings.promptStudio.addCondition') }}</span>
        </button>
        <button type="button" class="pp-tool-btn" :disabled="readonly" @click="addNode('assemble')">
          <Plus :size="12" /><span>{{ t('settings.promptStudio.addAssemble') }}</span>
        </button>
      </div>
      <div class="pp-toolbar-group">
        <button type="button" class="pp-tool-btn" :disabled="readonly || rawNodes.length < 2" :title="t('canvas.autoLayout')" @click="autoLayout">
          <LayoutGrid :size="12" /><span>{{ t('canvas.autoLayout') }}</span>
        </button>
        <button type="button" class="pp-tool-btn" :disabled="rawNodes.length === 0" :title="t('canvas.fitView')" @click="scheduleFit">
          <Maximize2 :size="12" /><span>{{ t('canvas.fitView') }}</span>
        </button>
        <button type="button" class="pp-tool-btn danger" :disabled="readonly || (!localSelectedNodeId && !localSelectedEdgeId)" @click="deleteSelected">
          <Trash2 :size="12" /><span>{{ t('settings.deleteAction') }}</span>
        </button>
      </div>
      <div class="pp-toolbar-group pp-toolbar-persist">
        <button type="button" class="pp-tool-btn" :disabled="readonly || saving || !dirty" @click="emit('save-draft')">
          <Save :size="12" /><span>{{ t('settings.promptStudio.saveDraftAction') }}</span>
        </button>
        <button type="button" class="pp-tool-btn primary" :disabled="readonly || publishing" @click="emit('publish')">
          <Rocket :size="12" /><span>{{ t('settings.publishAction') }}</span>
        </button>
      </div>
    </div>

    <div v-if="warn" class="pp-warn" role="status">{{ warn }}</div>
    <div v-if="!pipeline" class="center-empty-state"><span>{{ t('canvas.choosePipeline') }}</span></div>
    <div v-else class="pp-wrap">
      <VueFlow
        :id="FLOW_ID"
        :nodes="vfNodes"
        :edges="vfEdges"
        :snap-to-grid="true"
        :snap-grid="[20, 20]"
        :is-valid-connection="isValidConnection"
        fit-view-on-init
        class="vf"
        @nodes-initialized="scheduleFit"
        @connect="handleConnect"
        @node-click="onNodeClick"
        @edge-click="onEdgeClick"
        @pane-click="onPaneClick"
        @node-drag-stop="onNodeDragStop"
      >
        <Background :gap="20" />
        <Panel position="top-right" class="vf-panel">{{ readonly ? t('canvas.readonlyHint') : t('canvas.pipelineHint') }}</Panel>
        <template #node-custom="{ data }">
          <div class="pp-node" :class="[{ sel: data.selected }, `type-${data.nodeType}`]">
            <Handle type="target" :position="Position.Left" :connectable="!readonly" :class="{ 'pp-handle-readonly': readonly }" />
            <div class="pp-node-head">
              <span class="pp-node-type">{{ data.nodeType }}</span>
              <strong :title="data.label">{{ data.label }}</strong>
            </div>
            <div v-if="data.snippet" class="pp-node-snippet" :title="data.snippet">{{ data.snippet }}</div>
            <div v-else class="pp-node-id">{{ (data.raw as PromptPipelineNodeDto)?.id }}</div>
            <Handle type="source" :position="Position.Right" :connectable="!readonly" :class="{ 'pp-handle-readonly': readonly }" />
          </div>
        </template>
      </VueFlow>
    </div>
  </div>
</template>

<style scoped>
.prompt-pipeline-canvas { display: grid; gap: 8px; outline: none; }
.pp-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  padding: 6px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-chrome);
}
.pp-toolbar-group {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}
.pp-tool-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}
.pp-tool-btn:hover:not(:disabled) {
  border-color: var(--accent-primary);
}
.pp-tool-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}
.pp-tool-btn.primary {
  border-color: var(--bg-primary-button, var(--accent-primary));
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
}
.pp-tool-btn.danger:hover:not(:disabled) {
  border-color: var(--accent-danger);
  color: var(--accent-danger);
}
.pp-warn { font-size: 11px; color: var(--accent-danger); background: color-mix(in srgb, var(--accent-danger) 8%, var(--surface-raised)); border: 1px solid color-mix(in srgb, var(--accent-danger) 22%, var(--border-muted)); padding: 6px 10px; border-radius: 8px; }
.pp-wrap { height: 380px; border: 1px solid var(--border-muted); border-radius: 12px; overflow: hidden; background: var(--surface-section); position: relative; }
.vf { width: 100%; height: 100%; }
.vf-panel { font-size: 11px; color: var(--text-muted); background: var(--surface-raised); padding: 4px 8px; border-radius: 6px; }
.pp-node { position: relative; width: 208px; padding: 8px 10px; border-radius: 10px; background: var(--surface-raised); border: 1px solid var(--border-muted); display: grid; gap: 4px; box-shadow: 0 1px 4px rgba(0,0,0,0.06); }
.pp-node.type-assemble {
  border-color: color-mix(in srgb, var(--accent-success) 28%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-success) 4%, var(--surface-raised));
}
.pp-node.type-assemble .pp-node-type {
  color: var(--accent-success);
  background: color-mix(in srgb, var(--accent-success) 14%, var(--surface-section));
}
.pp-node.type-condition {
  border-color: color-mix(in srgb, var(--accent-warning) 28%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning) 4%, var(--surface-raised));
}
.pp-node.type-condition .pp-node-type {
  color: var(--accent-warning);
  background: color-mix(in srgb, var(--accent-warning) 14%, var(--surface-section));
}
.pp-node.type-fragment_ref {
  border-color: color-mix(in srgb, var(--accent-brand) 28%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-brand) 4%, var(--surface-raised));
}
.pp-node.type-fragment_ref .pp-node-type {
  color: var(--accent-brand);
  background: color-mix(in srgb, var(--accent-brand) 14%, var(--surface-section));
}
.pp-node.sel { border-color: var(--accent-primary); box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 22%, transparent); }
.pp-node-head { display: flex; align-items: center; gap: 6px; min-width: 0; }
.pp-node-type { font-size: 9px; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); background: var(--surface-section); padding: 2px 6px; border-radius: 999px; flex-shrink: 0; }
.pp-node-head strong { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; color: var(--text-primary); }
.pp-node-id,
.pp-node-snippet { font-size: 10px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.pp-handle-readonly { opacity: 0; pointer-events: none; }
</style>

<style>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';
</style>
