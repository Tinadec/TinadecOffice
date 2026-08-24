<script setup lang="ts">
import { computed } from 'vue'
import { VueFlow, Panel, type Node, type Edge } from '@vue-flow/core'
import { Background } from '@vue-flow/background'
import { layoutLanes, toFlowNodes, makeEdge } from './flowShared'
import type { AgentLineageEntryDto } from '@/api'

/**
 * Read-only two-lane visualization of one run (docs/app-core-ui.md §4.2).
 *
 * - operation lane: governance instances (meeting/supervisor/git_steward/…)
 * - execution lane: task planner + workers with spawn lineage edges
 *
 * Data comes exclusively from Core projections (orchestration + agent-lineage);
 * when both are empty the canvas degrades to a labeled empty state rather
 * than inventing nodes. Frozen runs are never draggable or connectable.
 */
const props = defineProps<{
  lineage: Array<AgentLineageEntryDto & { display_name?: string }>
  hasOrchestrationData: boolean
}>()

const emit = defineEmits<{
  select: [instanceId: string]
}>()

const operationEntries = computed(() => props.lineage.filter((x) => x.layer === 'operation'))
const executionEntries = computed(() => props.lineage.filter((x) => x.layer === 'execution'))
const isEmpty = computed(() => props.lineage.length === 0)

const nodes = computed<Node[]>(() => {
  if (isEmpty.value) return []
  const opInputs = operationEntries.value.map((entry, index) => ({
    id: entry.id,
    lane: 'operation' as const,
    depth: index,
    order: index,
  }))
  const exInputs = executionEntries.value.map((entry) => ({
    id: entry.id,
    lane: 'execution' as const,
    depth: entry.generation_depth ?? 0,
    order: executionEntries.value.indexOf(entry),
  }))
  const positions = layoutLanes([...opInputs, ...exInputs])
  return toFlowNodes(
    props.lineage.map((entry) => ({
      id: entry.id,
      position: positions[entry.id] ?? { x: 0, y: 0 },
      data: {
        label: '',
        entry,
      },
    })),
  )
})

const edges = computed<Edge[]>(() => {
  if (isEmpty.value) return []
  const result: Edge[] = []
  for (const entry of props.lineage) {
    if (!entry.parent_instance_id) continue
    const parent = props.lineage.find((x) => x.id === entry.parent_instance_id)
    if (!parent) continue
    result.push(
      makeEdge(
        `lineage-${entry.id}`,
        parent.id,
        entry.id,
        'lineage',
        `depth ${entry.generation_depth}`,
      ),
    )
  }
  return result
})

function nodeStyleClass(layer: string, status: string): string {
  const statusPart =
    status === 'completed' || status === 'succeeded'
      ? 'is-done'
      : status === 'failed'
        ? 'is-failed'
        : ''
  return `${layer === 'operation' ? 'lane-node--operation' : 'lane-node--execution'} ${statusPart}`
}
</script>

<template>
  <div class="run-lane-canvas" data-testid="run-lane-canvas">
    <div v-if="isEmpty && !hasOrchestrationData" class="run-lane-canvas__empty" data-testid="lane-empty">
      {{ $t('workbench.laneEmpty', 'No agent instances projected for this run yet.') }}
    </div>
    <VueFlow
      v-else
      :nodes="nodes"
      :edges="edges"
      :nodes-draggable="false"
      :nodes-connectable="false"
      :edges-updatable="false"
      fit-view-on-init
      :default-viewport="{ zoom: 0.9 }"
      @node-click="(e: { node: { id: string } }) => emit('select', e.node.id)"
    >
      <Background />
      <Panel position="top-left" class="run-lane-legend">
        <span class="legend-dot legend-dot--operation"></span> {{ $t('workbench.operationLane', 'operation') }}
        <span class="legend-dot legend-dot--execution"></span> {{ $t('workbench.executionLane', 'execution') }}
        <span class="legend-line"></span> {{ $t('workbench.lineageEdge', 'spawn lineage') }}
      </Panel>
      <template #node-default="nodeProps">
        <div
          class="lane-node"
          :class="nodeStyleClass(nodeProps.data.entry.layer, nodeProps.data.entry.status)"
          :data-instance-id="nodeProps.id"
        >
          <div class="lane-node__role">{{ nodeProps.data.entry.display_name ?? nodeProps.data.entry.role }}</div>
          <div class="lane-node__meta">
            <span>{{ nodeProps.data.entry.status }}</span>
            <span v-if="nodeProps.data.entry.generated">· G{{ nodeProps.data.entry.generation_depth }}</span>
          </div>
        </div>
      </template>
    </VueFlow>
  </div>
</template>

<style scoped>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';

.run-lane-canvas {
  height: 380px;
  border-radius: 10px;
  background: var(--surface-section);
  overflow: hidden;
}

.run-lane-canvas__empty {
  display: flex;
  height: 100%;
  align-items: center;
  justify-content: center;
  color: var(--text-secondary);
  font-size: 13px;
}

.run-lane-legend {
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
}

.legend-dot--operation {
  background: var(--accent-recovery, #b18aff);
  margin-left: 8px;
}

.legend-dot--execution {
  background: var(--accent-info, #4a9eff);
  margin-left: 8px;
}

.legend-line {
  display: inline-block;
  width: 18px;
  height: 2px;
  background: var(--accent-info, #4a9eff);
  margin-left: 8px;
}

.lane-node {
  min-width: 170px;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  font-size: 12px;
  cursor: pointer;
}

.lane-node--operation {
  border-left: 3px solid var(--accent-recovery, #b18aff);
}

.lane-node--execution {
  border-left: 3px solid var(--accent-info, #4a9eff);
}

.lane-node.is-done {
  opacity: 0.75;
}

.lane-node.is-failed {
  border-color: var(--accent-danger);
}

.lane-node__role {
  font-weight: 600;
  color: var(--text-primary);
  margin-bottom: 4px;
}

.lane-node__meta {
  display: flex;
  gap: 6px;
  color: var(--text-secondary);
  font-size: 11px;
}
</style>
