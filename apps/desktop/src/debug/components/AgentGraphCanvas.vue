<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { Loader2 } from '@lucide/vue'
import { api } from '@/api'

const { t } = useI18n()

/*
 * The agent DIRECTORY, laid out by layer — nothing more. It used to invent a chain of edges
 * between consecutive agents, mark every agent "pending", and draw a hardcoded fake graph when
 * the request failed; none of that was Core state. Run topology (who dispatched whom, live
 * status) lives in a run's Orchestration tab, which reads Core's projection.
 */
interface AgentNode {
  id: string
  label: string
  layer: string
  enabled: boolean
  x: number
  y: number
}

interface AgentEdge {
  from: string
  to: string
}

const nodes = ref<AgentNode[]>([])
const edges = ref<AgentEdge[]>([])
const loading = ref(false)
const failed = ref(false)

const statusColors: Record<string, string> = {
  enabled: '#58a6ff',
  disabled: '#6e7681',
}

const statusPaths: Record<string, string> = {
  enabled: 'M8 4A4 4 0 1 1 8 12A4 4 0 0 1 8 4',
  disabled: 'M5 5L11 11M11 5L5 11',
}

async function fetchGraph() {
  loading.value = true
  failed.value = false
  try {
    const agents = await api.listAgents()
    const rows = new Map<string, number>()
    const layerOrder = ['operation', 'execution']
    nodes.value = [...agents]
      .sort((a, b) => layerOrder.indexOf(a.layer) - layerOrder.indexOf(b.layer))
      .map((agent) => {
        const row = layerOrder.includes(agent.layer) ? layerOrder.indexOf(agent.layer) : layerOrder.length
        const column = rows.get(agent.layer) ?? 0
        rows.set(agent.layer, column + 1)
        return {
          id: agent.id,
          label: agent.display_name || agent.slug || agent.id,
          layer: agent.layer,
          enabled: agent.enabled,
          x: 80 + column * 170,
          y: 60 + row * 140,
        }
      })
    edges.value = []
  } catch {
    nodes.value = []
    edges.value = []
    failed.value = true
  } finally {
    loading.value = false
  }
}

onMounted(fetchGraph)
</script>

<template>
  <div class="agent-graph">
    <div v-if="loading" class="graph-empty">
      <Loader2 :size="24" class="empty-icon spinning" />
      <span>{{ t('debugStudio.loadingGraph') }}</span>
    </div>
    <div v-else-if="failed" class="graph-empty">
      <span>{{ t('debugStudio.graphLoadFailed') }}</span>
    </div>
    <svg v-else class="graph-canvas" width="100%" height="100%">
      <!-- Arrow marker definition -->
      <defs>
        <marker id="arrowhead" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
          <polygon points="0 0, 8 3, 0 6" fill="#484f58" />
        </marker>
      </defs>

      <!-- Edges -->
      <line
        v-for="(edge, i) in edges"
        :key="'e' + i"
        :x1="nodes.find(n => n.id === edge.from)?.x"
        :y1="nodes.find(n => n.id === edge.from)?.y"
        :x2="nodes.find(n => n.id === edge.to)?.x"
        :y2="nodes.find(n => n.id === edge.to)?.y"
        stroke="#484f58"
        stroke-width="1.5"
        stroke-dasharray="6 3"
        marker-end="url(#arrowhead)"
      />

      <!-- Nodes -->
      <g v-for="node in nodes" :key="node.id">
        <!-- Shadow -->
        <rect
          :x="node.x - 70 + 2" :y="node.y - 24 + 2"
          width="140" height="48" rx="10"
          fill="#000" fill-opacity="0.3"
        />
        <!-- Card（岛屿卡片：surface-raised + 状态描边） -->
        <rect
          :x="node.x - 70" :y="node.y - 24"
          width="140" height="48" rx="10"
          fill="var(--surface-raised, #1a1f29)"
          :stroke="statusColors[node.enabled ? 'enabled' : 'disabled']"
          stroke-width="1.5"
          style="filter: drop-shadow(0 1px 3px rgba(0,0,0,.08)) drop-shadow(0 4px 12px rgba(0,0,0,.05));"
        />
        <!-- Status icon -->
        <svg
          :x="node.x - 60" :y="node.y - 7"
          width="14" height="14"
          viewBox="0 0 16 16"
          fill="none"
          :stroke="statusColors[node.enabled ? 'enabled' : 'disabled']"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
        >
          <path :d="statusPaths[node.enabled ? 'enabled' : 'disabled']" />
        </svg>
        <!-- Label -->
        <text
          :x="node.x - 40" :y="node.y + 1"
          text-anchor="start" dominant-baseline="middle"
          fill="#e6edf3" font-size="12" font-weight="500"
        >
          {{ node.label }}
        </text>
      </g>
    </svg>
  </div>
</template>

<style scoped>
.agent-graph {
  width: 100%;
  height: 100%;
  min-height: 400px;
  background: transparent;
  flex: 1;
  display: flex;
  flex-direction: column;
}

.graph-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: 100%;
  gap: 8px;
  color: var(--text-muted, #6e7681);
  background: var(--surface-raised, #1a1f29);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 12px;
  margin: 12px;
  box-shadow: var(--shadow-card-subtle);
}
.empty-icon { color: var(--text-muted, #6e7681); }
.empty-icon.spinning {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

.graph-canvas {
  min-height: 400px;
  flex: 1;
  background: transparent;
}
</style>
