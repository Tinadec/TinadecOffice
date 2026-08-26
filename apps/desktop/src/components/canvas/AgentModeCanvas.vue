<script setup lang="ts">
import { computed, ref } from 'vue'
import { VueFlow, Panel, useVueFlow, type Node, type Edge, type Connection } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import { Workflow, Cpu } from '@lucide/vue'
import type { AgentModeNodeDto, AgentModeEdgeDto, AgentDefinitionDto } from '@/api'

const props = defineProps<{
  nodes: AgentModeNodeDto[]
  edges: AgentModeEdgeDto[]
  agents: AgentDefinitionDto[]
  /** Pack-managed/published modes render inspect-only: no drag, no connect, no delete. */
  readonly?: boolean
}>()
const emit = defineEmits<{
  'update:nodes': [nodes: AgentModeNodeDto[]]
  'update:edges': [edges: AgentModeEdgeDto[]]
  'select-node': [node: AgentModeNodeDto | null]
  'select-edge': [edge: AgentModeEdgeDto | null]
}>()

function normalizeLane(v: unknown): 'operation' | 'execution' {
  const s = String(v ?? '').trim().toLowerCase()
  if (s === 'planning') return 'operation'
  return s === 'execution' ? 'execution' : 'operation'
}

const { onConnect } = useVueFlow()

const selectedId = ref<string | null>(null)
const selectedEdgeId = ref<string | null>(null)
const laneWarning = ref('')

const opCount = computed(() => props.nodes.filter((n) => normalizeLane(n.lane) === 'operation').length)
const exCount = computed(() => props.nodes.filter((n) => normalizeLane(n.lane) === 'execution').length)

function agentFor(id: string) { return props.agents.find((a) => a.id === id) }

const vfNodes = computed<Node[]>(() =>
  props.nodes.map((n) => {
    const ag = agentFor(n.agent_id)
    const cap = (ag?.capabilities ?? []).slice(0, 2).join(' · ')
    const tools = (ag?.allowed_tools ?? []).length
    const isSel = selectedId.value === n.id
    return {
      id: n.id,
      type: 'custom',
      position: n.position,
      data: {
        label: n.label ?? ag?.name ?? n.agent_id,
        agent_id: n.agent_id,
        lane: normalizeLane(n.lane),
        agentName: ag?.name ?? n.agent_id.slice(0, 8),
        role: ag?.agent_type ?? '—',
        cap: cap || '—',
        toolCount: tools,
        selected: isSel,
      },
      style: {
        background: 'transparent',
        border: 'none',
        padding: '0',
        width: '184px',
      },
    }
  })
)
const vfEdges = computed<Edge[]>(() =>
  props.edges.map((e) => ({
    id: e.id,
    source: e.source,
    target: e.target,
    label: e.label ?? '',
    animated: false,
    style: { stroke: selectedEdgeId.value === e.id ? 'var(--accent-primary)' : 'var(--border-default)', strokeWidth: selectedEdgeId.value === e.id ? '2.2' : '1.6' },
    labelStyle: { fontSize: '10px', fill: 'var(--text-secondary)' },
    labelBgStyle: { fill: 'var(--surface-raised)', fillOpacity: 0.9 },
  }))
)

function onNodeClick(e: { node: Node }) {
  selectedId.value = e.node.id
  selectedEdgeId.value = null
  const n = props.nodes.find((x) => x.id === e.node.id) ?? null
  emit('select-node', n)
  emit('select-edge', null)
}
function onPaneClick() {
  selectedId.value = null
  selectedEdgeId.value = null
  emit('select-node', null)
  emit('select-edge', null)
}
function onEdgeClick(e: { edge: Edge }) {
  selectedEdgeId.value = e.edge.id
  selectedId.value = null
  const ed = props.edges.find((x) => x.id === e.edge.id) ?? null
  emit('select-edge', ed)
  emit('select-node', null)
}

function hasCycle(nextEdges: AgentModeEdgeDto[]): boolean {
  const adj = new Map<string, string[]>()
  for (const ed of nextEdges) {
    if (!adj.has(ed.source)) adj.set(ed.source, [])
    adj.get(ed.source)!.push(ed.target)
  }
  const visited = new Set<string>()
  const stack = new Set<string>()
  function dfs(u: string): boolean {
    visited.add(u); stack.add(u)
    for (const v of adj.get(u) ?? []) {
      if (!visited.has(v) && dfs(v)) return true
      if (stack.has(v)) return true
    }
    stack.delete(u); return false
  }
  for (const n of props.nodes) if (!visited.has(n.id) && dfs(n.id)) return true
  return false
}

onConnect((conn: Connection) => {
  if (props.readonly) return
  if (!conn.source || !conn.target) return
  if (conn.source === conn.target) { laneWarning.value = '不允许自连'; setTimeout(() => laneWarning.value = '', 1800); return }
  if (props.edges.some((e) => e.source === conn.source && e.target === conn.target)) return
  const src = props.nodes.find((n) => n.id === conn.source)
  const tgt = props.nodes.find((n) => n.id === conn.target)
  if (src && tgt) {
    const sl = normalizeLane(src.lane); const tl = normalizeLane(tgt.lane)
    // lane 约束：禁止 execution → operation 回流，同层连线仅警告
    if (sl === 'execution' && tl === 'operation') {
      laneWarning.value = 'lane 约束：execution 不能连回 operation'
      setTimeout(() => laneWarning.value = '', 2200)
      return
    }
  }
  const next: AgentModeEdgeDto = { id: `e-${conn.source}-${conn.target}`, source: conn.source, target: conn.target }
  const check = [...props.edges, next]
  if (hasCycle(check)) { laneWarning.value = '连线将形成环，已拦截'; setTimeout(() => laneWarning.value = '', 2200); return }
  emit('update:edges', check)
})

function handleNodesChange(changes: unknown) {
  if (props.readonly) return
  const list = changes as Array<{ id: string; position?: { x:number;y:number } }>
  let dirty = false
  const next = props.nodes.map((n) => {
    const c = list.find((x) => x.id === n.id && x.position)
    if (c?.position) { dirty = true; return { ...n, position: c.position } }
    return n
  })
  if (dirty) emit('update:nodes', next)
}

function deleteSelectedEdge() {
  if (!selectedEdgeId.value) return
  emit('update:edges', props.edges.filter((e) => e.id !== selectedEdgeId.value))
  selectedEdgeId.value = null
  emit('select-edge', null)
}
function updateEdgeLabel(val: string) {
  if (!selectedEdgeId.value) return
  emit('update:edges', props.edges.map((e) => e.id === selectedEdgeId.value ? { ...e, label: val || null } : e))
}

defineExpose({ deleteSelectedEdge, updateEdgeLabel, selectedEdgeId, laneWarning })
</script>

<template>
  <div class="agent-mode-canvas">
    <div class="lane-headers">
      <div class="lane-head op"><Workflow :size="13" />operation<em>{{ opCount }}</em></div>
      <div class="lane-head ex"><Cpu :size="13" />execution<em>{{ exCount }}</em></div>
    </div>
    <div v-if="laneWarning" class="lane-warning">{{ laneWarning }}</div>
    <div class="canvas-wrap">
      <div class="swimlanes"><div class="swimlane op" /><div class="swimlane ex" /></div>
      <VueFlow
        :nodes="vfNodes"
        :edges="vfEdges"
        fit-view-on-init
        class="vf"
        :nodes-draggable="!readonly"
        :nodes-connectable="!readonly"
        :edges-focusable="false"
        :delete-key-code="null"
        @node-click="onNodeClick"
        @pane-click="onPaneClick"
        @edge-click="onEdgeClick"
        @nodes-change="handleNodesChange"
      >
        <Background />
        <Panel position="top-right" class="vf-panel">{{ readonly ? '只读 · 克隆后可编辑' : '双泳道 · 拖拽节点 / 连线 · 选中边可删/改标签' }}</Panel>
        <template #node-custom="{ data }">
          <div class="am-node" :class="{ sel: data.selected, op: data.lane === 'operation', ex: data.lane === 'execution' }">
            <div class="am-node-head">
              <span class="am-node-icon"><Workflow v-if="data.lane === 'operation'" :size="12" /><Cpu v-else :size="12" /></span>
              <strong :title="data.agentName">{{ data.agentName }}</strong>
              <span class="am-node-tool">{{ data.toolCount }} tools</span>
            </div>
            <div class="am-node-label" :title="data.label">{{ data.label }}</div>
            <div class="am-node-meta"><span>{{ data.role }}</span><span class="dot">·</span><span :title="data.cap">{{ data.cap }}</span></div>
          </div>
        </template>
      </VueFlow>
    </div>
  </div>
</template>

<style scoped>
.agent-mode-canvas { display: grid; gap: 8px; }
.lane-headers { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; position: sticky; top: 0; z-index: 2; }
.lane-head { display: flex; align-items: center; gap: 6px; padding: 6px 10px; border-radius: 8px; font-size: 11px; font-weight: 700; letter-spacing: 0.04em; text-transform: uppercase; color: var(--text-secondary); background: var(--surface-section); border: 1px solid var(--border-muted); }
.lane-head.op { background: var(--surface-section); }
.lane-head.ex { background: var(--surface-raised); }
.lane-head em { margin-left: auto; font-style: normal; background: var(--surface-raised); color: var(--text-primary); padding: 1px 7px; border-radius: 999px; font-size: 11px; }
.lane-head.ex em { background: var(--surface-section); }
.lane-warning { font-size: 11px; color: var(--accent-danger); background: color-mix(in srgb, var(--accent-danger) 8%, var(--surface-raised)); border: 1px solid color-mix(in srgb, var(--accent-danger) 22%, var(--border-muted)); padding: 6px 10px; border-radius: 8px; }
.canvas-wrap { position: relative; height: 460px; border: 1px solid var(--border-muted); border-radius: 12px; overflow: hidden; background: var(--surface-section); }
.swimlanes { position: absolute; inset: 0; display: grid; grid-template-rows: 1fr 1fr; pointer-events: none; }
.swimlane.op { border-bottom: 1px dashed var(--border-muted); background: linear-gradient(180deg, transparent, rgba(0,0,0,0.02)); }
.swimlane.ex { background: transparent; }
.vf { width: 100%; height: 100%; }
.vf-panel { font-size: 11px; color: var(--text-muted); background: var(--surface-raised); padding: 4px 8px; border-radius: 6px; }
.am-node { width: 184px; padding: 8px 10px; border-radius: 10px; background: var(--surface-raised); border: 1px solid var(--border-muted); display: grid; gap: 4px; box-shadow: 0 1px 4px rgba(0,0,0,0.06); }
.am-node.sel { border-color: var(--accent-primary); box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 22%, transparent); }
.am-node.op { border-left: 3px solid color-mix(in srgb, var(--accent-primary) 70%, var(--border-muted)); }
.am-node.ex { border-left: 3px solid color-mix(in srgb, var(--accent-warning, #d97706) 65%, var(--border-muted)); }
.am-node-head { display: flex; align-items: center; gap: 6px; min-width: 0; }
.am-node-icon { width: 18px; height: 18px; border-radius: 6px; display: grid; place-items: center; background: var(--surface-section); color: var(--text-secondary); flex-shrink: 0; }
.am-node-head strong { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; color: var(--text-primary); }
.am-node-tool { font-size: 10px; color: var(--text-muted); background: var(--surface-section); padding: 1px 5px; border-radius: 999px; }
.am-node-label { font-size: 11px; color: var(--text-secondary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.am-node-meta { display: flex; align-items: center; gap: 4px; font-size: 10px; color: var(--text-muted); overflow: hidden; }
.am-node-meta span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.dot { flex-shrink: 0; }
</style>

<style>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';
</style>
