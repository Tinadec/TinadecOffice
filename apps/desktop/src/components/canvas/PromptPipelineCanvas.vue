<script setup lang="ts">
import { computed, ref } from 'vue'
import { VueFlow, Panel, useVueFlow, type Node, type Edge, type Connection } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import type { PromptPipelineDto } from '@/api'

const props = defineProps<{ pipeline: PromptPipelineDto | null }>()
const emit = defineEmits<{
  'update:nodes': [nodes: unknown[]]
  'update:edges': [edges: unknown[]]
  'select-node': [node: unknown | null]
  'select-edge': [edge: unknown | null]
}>()

const { onConnect } = useVueFlow()
const selectedId = ref<string | null>(null)
const selectedEdgeId = ref<string | null>(null)
const warn = ref('')

type RawNode = { id: string; position?: { x: number; y: number }; x?: number; y?: number; label?: string; title?: string; key?: string; type?: string; data?: Record<string, unknown> }
type RawEdge = { id: string; source: string; target: string; label?: string | null }

const rawNodes = computed<RawNode[]>(() => (props.pipeline?.nodes as RawNode[] | null) ?? [])
const rawEdges = computed<RawEdge[]>(() => (props.pipeline?.edges as RawEdge[] | null) ?? [])

const vfNodes = computed<Node[]>(() =>
  rawNodes.value.map((n) => {
    const pos = n.position ?? (n.x != null && n.y != null ? { x: n.x as number, y: n.y as number } : { x: 80, y: 80 })
    const isSel = selectedId.value === n.id
    return {
      id: n.id,
      type: 'custom',
      position: pos as { x: number; y: number },
      data: { label: String(n.label ?? n.title ?? n.key ?? n.id), nodeType: String(n.type ?? 'template'), selected: isSel, raw: n },
      style: { background: 'transparent', border: 'none', padding: '0', width: '200px' },
    }
  })
)
const vfEdges = computed<Edge[]>(() =>
  rawEdges.value.map((e) => ({
    id: e.id, source: e.source, target: e.target, label: e.label ?? '',
    style: { stroke: selectedEdgeId.value === e.id ? 'var(--accent-primary)' : 'var(--border-default)', strokeWidth: selectedEdgeId.value === e.id ? '2.2' : '1.4' },
    labelStyle: { fontSize: '10px', fill: 'var(--text-secondary)' },
    labelBgStyle: { fill: 'var(--surface-raised)', fillOpacity: 0.9 },
  }))
)

function hasCycle(next: RawEdge[]): boolean {
  const adj = new Map<string, string[]>()
  for (const e of next) { if (!adj.has(e.source)) adj.set(e.source, []); adj.get(e.source)!.push(e.target) }
  const vis = new Set<string>(), stk = new Set<string>()
  const dfs = (u: string): boolean => { vis.add(u); stk.add(u); for (const v of adj.get(u) ?? []) { if (!vis.has(v) && dfs(v)) return true; if (stk.has(v)) return true } stk.delete(u); return false }
  for (const n of rawNodes.value) if (!vis.has(n.id) && dfs(n.id)) return true
  return false
}

onConnect((c: Connection) => {
  if (!c.source || !c.target) return
  if (c.source === c.target) { warn.value = '不允许自连'; setTimeout(() => warn.value = '', 1800); return }
  if (rawEdges.value.some((e) => e.source === c.source && e.target === c.target)) return
  const next: RawEdge = { id: `e-${c.source}-${c.target}`, source: c.source, target: c.target }
  if (hasCycle([...rawEdges.value, next])) { warn.value = '连线将形成环，已拦截'; setTimeout(() => warn.value = '', 2200); return }
  emit('update:edges', [...rawEdges.value, next])
})
function onNodeClick(e: { node: Node }) { selectedId.value = e.node.id; selectedEdgeId.value = null; const n = rawNodes.value.find((x) => x.id === e.node.id) ?? null; emit('select-node', n); emit('select-edge', null) }
function onEdgeClick(e: { edge: Edge }) { selectedEdgeId.value = e.edge.id; selectedId.value = null; const ed = rawEdges.value.find((x) => x.id === e.edge.id) ?? null; emit('select-edge', ed); emit('select-node', null) }
function onPaneClick() { selectedId.value = null; selectedEdgeId.value = null; emit('select-node', null); emit('select-edge', null) }
function handleNodesChange(changes: unknown) {
  const list = changes as Array<{ id: string; position?: { x: number; y: number } }>
  let dirty = false
  const next = rawNodes.value.map((n) => { const c = list.find((x) => x.id === n.id && x.position); if (c?.position) { dirty = true; return { ...n, position: c.position } } return n })
  if (dirty) emit('update:nodes', next)
}
</script>

<template>
  <div class="prompt-pipeline-canvas">
    <div v-if="warn" class="pp-warn">{{ warn }}</div>
    <div v-if="!pipeline" class="center-empty-state"><span>请选择一条管线以预览画布</span></div>
    <div v-else class="pp-wrap">
      <VueFlow :nodes="vfNodes" :edges="vfEdges" fit-view-on-init class="vf" @node-click="onNodeClick" @edge-click="onEdgeClick" @pane-click="onPaneClick" @nodes-change="handleNodesChange">
        <Background />
        <Panel position="top-right" class="vf-panel">拖拽节点 · 连线 · 选中查看详情</Panel>
        <template #node-custom="{ data }">
          <div class="pp-node" :class="{ sel: data.selected }">
            <div class="pp-node-head"><span class="pp-node-type">{{ data.nodeType }}</span><strong :title="data.label">{{ data.label }}</strong></div>
            <div class="pp-node-id">{{ (data.raw as RawNode)?.id }}</div>
          </div>
        </template>
      </VueFlow>
    </div>
  </div>
</template>

<style scoped>
.prompt-pipeline-canvas { display: grid; gap: 8px; }
.pp-warn { font-size: 11px; color: var(--accent-danger); background: color-mix(in srgb, var(--accent-danger) 8%, var(--surface-raised)); border: 1px solid color-mix(in srgb, var(--accent-danger) 22%, var(--border-muted)); padding: 6px 10px; border-radius: 8px; }
.pp-wrap { height: 360px; border: 1px solid var(--border-muted); border-radius: 12px; overflow: hidden; background: var(--surface-section); position: relative; }
.vf { width: 100%; height: 100%; }
.vf-panel { font-size: 11px; color: var(--text-muted); background: var(--surface-raised); padding: 4px 8px; border-radius: 6px; }
.pp-node { width: 200px; padding: 8px 10px; border-radius: 10px; background: var(--surface-raised); border: 1px solid var(--border-muted); display: grid; gap: 4px; box-shadow: 0 1px 4px rgba(0,0,0,0.06); }
.pp-node.sel { border-color: var(--accent-primary); box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 22%, transparent); }
.pp-node-head { display: flex; align-items: center; gap: 6px; min-width: 0; }
.pp-node-type { font-size: 9px; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); background: var(--surface-section); padding: 2px 6px; border-radius: 999px; }
.pp-node-head strong { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; color: var(--text-primary); }
.pp-node-id { font-size: 10px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>

<style>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';
</style>
