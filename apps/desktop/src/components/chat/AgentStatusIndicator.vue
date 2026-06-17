<script setup lang="ts">
import { computed } from 'vue'
import type { AgentState } from '@/composables/useAgentActivity'

const props = defineProps<{
  agent: AgentState
}>()

const isPlanning = computed(() => props.agent.agentLayer === 'planning')
const isExecution = computed(() => props.agent.agentLayer === 'execution')

const statusClass = computed(() => {
  switch (props.agent.status) {
    case 'active':
      return 'agent-status-active'
    case 'waiting':
      return 'agent-status-waiting'
    case 'completed':
      return 'agent-status-completed'
    case 'error':
      return 'agent-status-error'
    default:
      return 'agent-status-idle'
  }
})

const statusLabel = computed(() => {
  switch (props.agent.status) {
    case 'active':
      return '执行中'
    case 'waiting':
      return '等待中'
    case 'completed':
      return '已完成'
    case 'error':
      return '出错'
    default:
      return '空闲'
  }
})

const layerClass = computed(() => {
  if (isPlanning.value) return 'agent-layer-planning'
  if (isExecution.value) return 'agent-layer-execution'
  return 'agent-layer-other'
})

const layerLabel = computed(() => {
  if (isPlanning.value) return '规划层'
  if (isExecution.value) return '执行层'
  return props.agent.agentLayer
})
</script>

<template>
  <div class="agent-status-indicator" :class="[statusClass, layerClass]">
    <div class="agent-status-dot" :class="`dot-${agent.status}`" />
    <div class="agent-status-main">
      <div class="agent-status-name-row">
        <strong>{{ agent.agentName }}</strong>
        <span class="agent-status-layer-tag">{{ layerLabel }}</span>
      </div>
      <div class="agent-status-meta">
        <span class="agent-status-text">{{ statusLabel }}</span>
        <span v-if="agent.currentTask" class="agent-status-task">{{ agent.currentTask }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.agent-status-indicator {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
  transition: border-color 0.15s, background 0.15s;
}

.agent-status-indicator.agent-status-active {
  border-color: rgba(88, 166, 255, 0.4);
  background: rgba(88, 166, 255, 0.06);
}

.agent-status-indicator.agent-status-completed {
  border-color: rgba(63, 185, 80, 0.35);
}

.agent-status-indicator.agent-status-error {
  border-color: rgba(248, 81, 73, 0.4);
  background: rgba(248, 81, 73, 0.05);
}

.agent-status-indicator.agent-status-waiting {
  border-color: rgba(210, 153, 34, 0.35);
}

.agent-status-dot {
  flex-shrink: 0;
  width: 8px;
  height: 8px;
  margin-top: 5px;
  border-radius: 50%;
  background: var(--text-muted);
}

.agent-status-dot.dot-active {
  background: var(--accent-primary);
  animation: agent-pulse 1.4s ease-in-out infinite;
}

.agent-status-dot.dot-completed {
  background: var(--accent-success);
}

.agent-status-dot.dot-error {
  background: var(--accent-danger);
}

.agent-status-dot.dot-waiting {
  background: var(--accent-warning);
  animation: agent-pulse 1.8s ease-in-out infinite;
}

.agent-status-main {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.agent-status-name-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.agent-status-name-row strong {
  font-size: 12px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-status-layer-tag {
  flex-shrink: 0;
  padding: 1px 6px;
  border-radius: 4px;
  font-size: 10px;
  font-weight: 600;
  background: var(--bg-tertiary);
  color: var(--text-secondary);
}

.agent-layer-planning .agent-status-layer-tag {
  background: rgba(188, 140, 255, 0.12);
  color: #bc8cff;
}

.agent-layer-execution .agent-status-layer-tag {
  background: rgba(63, 185, 80, 0.12);
  color: var(--accent-success);
}

.agent-status-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.agent-status-text {
  flex-shrink: 0;
  font-size: 11px;
  color: var(--text-secondary);
}

.agent-status-task {
  overflow: hidden;
  font-size: 11px;
  color: var(--text-muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

@keyframes agent-pulse {
  0%,
  100% {
    opacity: 1;
    transform: scale(1);
  }
  50% {
    opacity: 0.5;
    transform: scale(1.3);
  }
}
</style>
