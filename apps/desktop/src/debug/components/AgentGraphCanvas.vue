<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { Loader2 } from '@lucide/vue'

const { t } = useI18n()
const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'

interface AgentNode {
  id: string
  label: string
  status: 'completed' | 'running' | 'pending' | 'failed' | 'blocked'
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

const statusColors: Record<string, string> = {
  completed: '#238636',
  running: '#58a6ff',
  pending: '#6e7681',
  failed: '#da3633',
  blocked: '#d29922',
}

/* SVG path data for status icons (16×16 viewbox) */
const statusPaths: Record<string, string> = {
  completed: 'M3 8.5L6.5 12L13 5',    // checkmark
  running: 'M8 2L14 8L8 14L2 8Z',      // diamond / rotating
  pending: 'M8 4A4 4 0 1 1 8 12A4 4 0 0 1 8 4', // circle outline
  failed: 'M5 5L11 11M11 5L5 11',      // X
  blocked: 'M5 3L5 13M3 5L11 5L11 11L3 11Z', // pause-like block
}

async function fetchGraph() {
  loading.value = true
  try {
    const res = await fetch(`${gatewayUrl}/api/v1/agents`)
    const agents = await res.json()
    nodes.value = agents.map((a: any, i: number) => ({
      id: a.id,
      label: a.layer || a.id,
      status: 'pending' as const,
      x: 80 + (i % 3) * 240,
      y: 60 + Math.floor(i / 3) * 140,
    }))
    edges.value = []
    for (let i = 1; i < nodes.value.length; i++) {
      edges.value.push({ from: nodes.value[i - 1].id, to: nodes.value[i].id })
    }
  } catch {
    nodes.value = [
      { id: 'planner', label: 'planner', status: 'completed', x: 200, y: 60 },
      { id: 'code-explorer', label: 'code-explorer', status: 'running', x: 80, y: 200 },
      { id: 'code-writer', label: 'code-writer', status: 'pending', x: 320, y: 200 },
      { id: 'review-executor', label: 'review-executor', status: 'blocked', x: 200, y: 340 },
    ]
    edges.value = [
      { from: 'planner', to: 'code-explorer' },
      { from: 'planner', to: 'code-writer' },
      { from: 'code-explorer', to: 'review-executor' },
    ]
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
          :stroke="statusColors[node.status]"
          stroke-width="1.5"
          style="filter: drop-shadow(0 1px 3px rgba(0,0,0,.08)) drop-shadow(0 4px 12px rgba(0,0,0,.05));"
        />
        <!-- Status icon -->
        <svg
          :x="node.x - 60" :y="node.y - 7"
          width="14" height="14"
          viewBox="0 0 16 16"
          fill="none"
          :stroke="statusColors[node.status]"
          stroke-width="2"
          stroke-linecap="round"
          stroke-linejoin="round"
        >
          <path :d="statusPaths[node.status]" />
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
