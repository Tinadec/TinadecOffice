<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { VueFlow, Panel, useVueFlow, type Node, type Edge, type Connection } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import type { AgentModeNodeDto, AgentModeEdgeDto, AgentDefinitionDto } from '@/api'

const props = defineProps<{
  nodes: AgentModeNodeDto[]
  edges: AgentModeEdgeDto[]
  agents: AgentDefinitionDto[]
}>()
const emit = defineEmits<{
  'update:nodes': [nodes: AgentModeNodeDto[]]
  'update:edges': [edges: AgentModeEdgeDto[]]
  'select-node': [node: AgentModeNodeDto | null]
}>()

const { onConnect, onNodesChange, onEdgesChange } = useVueFlow()

const vfNodes = computed<Node[]>(() =>
  props.nodes.map((n) => ({
    id: n.id,
    type: 'default',
    position: n.position,
    data: { label: n.label ?? props.agents.find((a) => a.id === n.agent_id)?.name ?? n.agent_id, agent_id: n.agent_id, lane: n.lane },
    style: {
      background: n.lane === 'operation' ? 'var(--surface-section)' : 'var(--surface-raised)',
      border: '1px solid var(--border-muted)',
      borderRadius: '8px',
      padding: '8px',
      fontSize: '12px',
    },
  }))
)
const vfEdges = computed<Edge[]>(() => props.edges.map((e) => ({ id: e.id, source: e.source, target: e.target, label: e.label ?? '', animated: false })))

const selectedId = ref<string | null>(null)

function onNodeClick(e: { node: Node }) {
  selectedId.value = e.node.id
  const n = props.nodes.find((x) => x.id === e.node.id) ?? null
  emit('select-node', n)
}
function onPaneClick() {
  selectedId.value = null
  emit('select-node', null)
}

onConnect((conn: Connection) => {
  if (!conn.source || !conn.target) return
  const next: AgentModeEdgeDto = { id: `e-${conn.source}-${conn.target}`, source: conn.source, target: conn.target }
  emit('update:edges', [...props.edges, next])
})

// vue-flow emits changes via watch; we sync position updates
watch(
  () => props.nodes,
  () => {},
)

function handleNodesChange(changes: unknown) {
  // propagate position updates back to parent DTOs
  const list = changes as Array<{ id: string; position?: { x:number;y:number } }>
  let dirty = false
  const next = props.nodes.map((n) => {
    const c = list.find((x) => x.id === n.id && x.position)
    if (c?.position) { dirty = true; return { ...n, position: c.position } }
    return n
  })
  if (dirty) emit('update:nodes', next)
}
</script>

<template>
  <div class="agent-mode-canvas">
    <div class="lane-labels">
      <span class="lane lane-op">operation</span>
      <span class="lane lane-ex">execution</span>
    </div>
    <div class="canvas-wrap">
      <!-- dual swimlanes via background stripes -->
      <div class="swimlanes">
        <div class="swimlane op" />
        <div class="swimlane ex" />
      </div>
      <VueFlow
        :nodes="vfNodes"
        :edges="vfEdges"
        fit-view-on-init
        class="vf"
        @node-click="onNodeClick"
        @pane-click="onPaneClick"
        @nodes-change="handleNodesChange"
      >
        <Background />
        <Panel position="top-right" class="vf-panel">双泳道 · 拖拽节点 / 连线 · 保存草稿需重发布</Panel>
      </VueFlow>
    </div>
  </div>
</template>

<style scoped>
.agent-mode-canvas { display: grid; gap: 8px; }
.lane-labels { display: flex; gap: 12px; font-size: 11px; color: var(--text-muted); }
.lane { padding: 2px 8px; border-radius: 999px; border: 1px solid var(--border-muted); }
.lane-op { background: var(--surface-section); }
.lane-ex { background: var(--surface-raised); }
.canvas-wrap { position: relative; height: 420px; border: 1px solid var(--border-muted); border-radius: 12px; overflow: hidden; background: var(--surface-section); }
.swimlanes { position: absolute; inset: 0; display: grid; grid-template-rows: 1fr 1fr; pointer-events: none; }
.swimlane.op { border-bottom: 1px dashed var(--border-muted); background: linear-gradient(180deg, transparent, rgba(0,0,0,0.02)); }
.swimlane.ex { background: transparent; }
.vf { width: 100%; height: 100%; }
.vf-panel { font-size: 11px; color: var(--text-muted); background: var(--surface-raised); padding: 4px 8px; border-radius: 6px; }
</style>

<style>
/* vue-flow core styles minimal */
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';
</style>
