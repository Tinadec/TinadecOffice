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
import type { AgentAssignmentDto, OrchestrationSnapshotDto, TaskNodeDto } from '../api'

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
      <ol v-else class="task-step-list">
        <li
          v-for="node in sortedNodes"
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
        </li>
      </ol>
    </div>
  </section>
</template>
