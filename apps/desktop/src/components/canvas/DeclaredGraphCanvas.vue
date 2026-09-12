<script setup lang="ts">
import { computed } from 'vue'
import { VueFlow, Panel, type Node, type Edge } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import type { DeclaredModeGraphDto, OrchestrationFlowDto } from '@/api'

/**
 * Read-only declared-mode-graph canvas (DmaEA graph orchestration): renders the
 * published mode-version snapshot (nodes/edges/data contracts) as the base
 * topology, with observed dispatch flows from the durable task graph shown in the
 * panel. Purely additive: mounted only when the orchestration snapshot carries a
 * declared graph. Frozen projection — never draggable or connectable.
 */
const props = defineProps<{
  graph: DeclaredModeGraphDto
  flows?: OrchestrationFlowDto[]
}>()

const positions = computed(() => {
  const map = new Map<string, { x: number; y: number }>()
  const byLayer = new Map<string, string[]>()
  for (const node of props.graph.nodes) {
    const layer = node.layer ?? 'execution'
    if (!byLayer.has(layer)) byLayer.set(layer, [])
    byLayer.get(layer)!.push(node.node_key)
  }
  const layerIndex = new Map([...byLayer.keys()].map((layer, index) => [layer, index]))
  for (const [layer, keys] of byLayer) {
    keys.forEach((key, order) => {
      map.set(key, { x: order * 220, y: (layerIndex.get(layer) ?? 0) * 130 })
    })
  }
  return map
})

const nodes = computed<Node[]>(() =>
  props.graph.nodes.map((node) => ({
    id: node.node_key,
    position: positions.value.get(node.node_key) ?? { x: 0, y: 0 },
    data: { node },
  })),
)

const edges = computed<Edge[]>(() =>
  props.graph.edges.map((edge) => ({
    id: edge.edge_key,
    source: edge.source_node_key ?? '',
    target: edge.target_node_key ?? '',
    label: edge.data_contract ? 'contract' : '',
    style: { stroke: 'var(--accent-info, #4a9eff)' },
    labelStyle: { fontSize: '10px' },
  })),
)

const flowSummary = computed(() => (props.flows ?? []).slice(0, 6))
</script>

<template>
  <div class="declared-graph-canvas" data-testid="declared-graph-canvas">
    <VueFlow
      :nodes="nodes"
      :edges="edges"
      :nodes-draggable="false"
      :nodes-connectable="false"
      :edges-updatable="false"
      fit-view-on-init
      :default-viewport="{ zoom: 0.85 }"
    >
      <Background />
      <Panel position="top-left" class="declared-graph-legend">
        <span class="legend-dot legend-dot--conversation"></span>
        {{ $t('workbench.conversationNode', 'conversation identity') }}
        <span class="legend-count">{{ flows?.length ?? 0 }} flows</span>
      </Panel>
      <template #node-default="nodeProps">
        <div
          class="graph-node"
          :class="nodeProps.data.node.is_conversation ? 'graph-node--conversation' : ''"
        >
          <div class="graph-node__label">{{ nodeProps.data.node.label ?? nodeProps.data.node.node_key }}</div>
          <div class="graph-node__meta">
            <span>{{ nodeProps.data.node.layer }}</span>
            <span v-if="nodeProps.data.node.is_conversation">· identity</span>
          </div>
        </div>
      </template>
    </VueFlow>
    <ul v-if="flowSummary.length" class="declared-graph-flows">
      <li v-for="flow in flowSummary" :key="flow.task_key">
        {{ flow.from }} → {{ flow.to }} · {{ flow.task_key }} · {{ flow.status }}
      </li>
    </ul>
  </div>
</template>

<style scoped>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';

.declared-graph-canvas {
  position: relative;
  height: 300px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-section);
  overflow: hidden;
}

.declared-graph-legend {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--text-secondary);
  background: var(--surface-raised);
  border-radius: 6px;
  padding: 4px 8px;
}

.legend-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--accent-recovery, #b18aff);
}

.legend-count {
  color: var(--text-muted);
  margin-left: 6px;
}

.graph-node {
  min-width: 160px;
  padding: 9px 12px;
  border-radius: 8px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  font-size: 12px;
}

.graph-node--conversation {
  border-left: 3px solid var(--accent-recovery, #b18aff);
}

.graph-node__label {
  font-weight: 600;
  color: var(--text-primary);
  margin-bottom: 3px;
}

.graph-node__meta {
  color: var(--text-secondary);
  font-size: 11px;
}

.declared-graph-flows {
  margin: 0;
  max-height: 84px;
  overflow: auto;
  padding: 6px 10px;
  border-top: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
  list-style: none;
}
</style>
