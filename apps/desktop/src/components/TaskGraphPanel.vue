<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  ChevronDown,
  ChevronRight,
  Circle,
  CircleCheck,
  CircleDot,
  CircleX,
  GitBranch,
  ListTodo
} from '@lucide/vue'
import type { AgentAssignmentDto, OrchestrationLaneDto, OrchestrationSnapshotDto, TaskNodeDto } from '../api'

const props = defineProps<{
  snapshot: OrchestrationSnapshotDto | null
}>()

const collapsed = ref(false)

const assignmentsByNode = computed(() => {
  const map = new Map<string, AgentAssignmentDto[]>()
  for (const assignment of props.snapshot?.assignments ?? []) {
    const list = map.get(assignment.task_node_id) ?? []
    list.push(assignment)
    map.set(assignment.task_node_id, list)
  }
  return map
})

const sortedNodes = computed(() =>
  [...(props.snapshot?.nodes ?? [])].sort((a, b) => a.priority - b.priority)
)

const laneGroups = computed(() => {
  const groups: Array<{ lane_key: string; nodes: TaskNodeDto[] }> = []
  const index = new Map<string, number>()
  for (const node of sortedNodes.value) {
    const key = node.lane_key || 'main'
    let at = index.get(key)
    if (at === undefined) {
      at = groups.length
      index.set(key, at)
      groups.push({ lane_key: key, nodes: [] })
    }
    groups[at].nodes.push(node)
  }
  return groups
})

const singleLane = computed(() => laneGroups.value.length <= 1)

const lanesByLaneKey = computed(() => {
  const map = new Map<string, OrchestrationLaneDto>()
  for (const lane of props.snapshot?.lanes ?? []) map.set(lane.lane_key, lane)
  return map
})

function laneOf(laneKey: string) {
  return lanesByLaneKey.value.get(laneKey) ?? null
}

function isLaneGateState(status: string): boolean {
  const s = status.toLowerCase()
  return s === 'waiting' || s === 'gate_review'
}

const progress = computed(() => {
  const nodes = props.snapshot?.nodes ?? []
  if (nodes.length === 0) return { done: 0, total: 0, percent: 0 }
  const done = nodes.filter((n) => n.status === 'done' || n.status === 'completed').length
  return { done, total: nodes.length, percent: Math.round((done / nodes.length) * 100) }
})

function nodeAssignments(node: TaskNodeDto) {
  return assignmentsByNode.value.get(node.id) ?? []
}

function statusIcon(status: string) {
  const s = status.toLowerCase()
  if (s === 'done' || s === 'completed') return CircleCheck
  if (s === 'running' || s === 'in_progress' || s === 'in-progress' || s === 'executing') return CircleDot
  if (s === 'failed' || s === 'error' || s === 'cancelled') return CircleX
  if (s === 'blocked') return CircleX
  if (s === 'ready') return CircleDot
  return Circle
}

function statusClass(status: string): string {
  const s = status.toLowerCase()
  if (s === 'done' || s === 'completed') return 'done'
  if (s === 'running' || s === 'in_progress' || s === 'in-progress' || s === 'executing') return 'running'
  if (s === 'ready') return 'ready'
  if (s === 'blocked') return 'blocked'
  if (s === 'pending') return 'pending'
  if (s === 'failed' || s === 'error' || s === 'cancelled') return 'failed'
  return 'pending'
}

function toggleCollapse() {
  collapsed.value = !collapsed.value
}
</script>

<template>
  <section class="task-graph-panel" :class="{ collapsed }">
    <button class="task-graph-head" @click="toggleCollapse">
      <component
        :is="collapsed ? ChevronRight : ChevronDown"
        :size="12"
        class="task-graph-chevron"
      />
      <ListTodo :size="12" class="task-graph-icon" />
      <span class="task-graph-title">
        {{ snapshot?.graph?.title ?? 'Plan' }}
      </span>
      <span v-if="progress.total > 0" class="task-graph-progress">
        {{ progress.done }}/{{ progress.total }}
      </span>
      <div v-if="progress.total > 0" class="task-graph-bar">
        <div class="task-graph-bar-fill" :style="{ width: `${progress.percent}%` }"></div>
      </div>
      <span v-if="snapshot?.run" class="task-graph-run">
        <GitBranch :size="10" />
        {{ snapshot.run.status }}
      </span>
    </button>

    <div v-if="!collapsed" class="task-graph-body">
      <p v-if="!snapshot?.graph" class="task-graph-empty">
        Send a task to create the orchestration plan.
      </p>
      <div v-else class="task-lanes">
        <div v-for="group in laneGroups" :key="group.lane_key" class="task-lane">
          <div v-if="!singleLane" class="task-lane-head">
            <span class="task-lane-name">{{ group.lane_key }}</span>
            <span v-if="laneOf(group.lane_key)" class="task-lane-state" :class="laneOf(group.lane_key)!.status">
              {{ laneOf(group.lane_key)!.status }}
            </span>
            <span v-if="laneOf(group.lane_key)?.escalated" class="task-lane-escalated">escalated</span>
          </div>
          <ol class="task-step-list">
            <li
              v-for="node in group.nodes"
              :key="node.id"
              class="task-step"
              :class="statusClass(node.status)"
            >
              <span class="task-step-index">{{ node.priority }}</span>
              <component
                :is="statusIcon(node.status)"
                :size="11"
                class="task-step-status"
              />
              <div class="task-step-main">
                <span class="task-step-title">{{ node.title }}</span>
                <span v-if="nodeAssignments(node).length > 0" class="task-step-agent">
                  {{ nodeAssignments(node)[0].agent_name }}
                </span>
              </div>
              <span
                v-if="isLaneGateState(node.status)"
                class="task-step-gate"
                :class="node.status.toLowerCase()"
              >{{ node.status }}</span>
            </li>
          </ol>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.task-lanes {
  display: grid;
  gap: 8px;
}

.task-lane-head {
  align-items: center;
  display: flex;
  gap: 6px;
  margin-bottom: 4px;
}

.task-lane-name {
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.task-lane-state,
.task-lane-escalated,
.task-step-gate {
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  color: var(--text-muted);
  font-size: 10px;
  line-height: 1.4;
  padding: 1px 6px;
}

.task-lane-state.waiting,
.task-lane-state.gate_review,
.task-step-gate.waiting {
  border-color: var(--accent-warning);
  color: var(--accent-warning);
}

.task-lane-escalated,
.task-step-gate.gate_review {
  border-color: var(--accent-danger);
  color: var(--accent-danger);
}

.task-step-gate.gate_review {
  background: color-mix(in srgb, var(--accent-danger) 12%, transparent);
}
</style>
