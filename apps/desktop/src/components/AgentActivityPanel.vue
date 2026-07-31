<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  Loader2,
  Brain,
  Activity,
  CheckCircle2,
  AlertCircle,
  Clock,
  ChevronDown,
  ChevronRight,
  ShieldAlert,
  Circle,
  CircleCheck,
  CircleDot,
  CircleX,
  GitBranch,
  ListTodo,
  Play,
  Network,
  UserCheck,
  ShieldCheck,
  Package,
  MessageSquare,
  Terminal,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { AgentActivity, AgentState, ThinkingStep, ProgressEvent } from '@/composables/useAgentActivity'
import type { AgentAssignmentDto, OrchestrationSnapshotDto, TaskNodeDto } from '../api'
import AgentStatusIndicator from './chat/AgentStatusIndicator.vue'

const { t } = useI18n()

const props = defineProps<{
  activity: AgentActivity
  agentStates: Record<string, AgentState>
  thinkingSteps: ThinkingStep[]
  progressEvents: ProgressEvent[]
  orchestration: OrchestrationSnapshotDto | null
}>()

// ---- Section collapse state ----
const statusExpanded = ref(true)
const taskGraphCollapsed = ref(false)
const thinkingExpanded = ref(false)

// ---- Status config ----
const statusConfig = computed(() => {
  switch (props.activity.status) {
    case 'thinking':
      return { icon: Brain, label: t('agent.thinking'), color: 'status-thinking', spin: true, pulse: false }
    case 'working':
      return { icon: Activity, label: t('agent.working'), color: 'status-working', spin: false, pulse: true }
    case 'waiting_approval':
      return { icon: ShieldAlert, label: t('agent.waitingApproval'), color: 'status-waiting', spin: false, pulse: false }
    case 'completed':
      return { icon: CheckCircle2, label: t('agent.completed'), color: 'status-completed', spin: false, pulse: false }
    case 'error':
      return { icon: AlertCircle, label: t('agent.error'), color: 'status-error', spin: false, pulse: false }
    default:
      return { icon: Activity, label: t('agent.idle'), color: 'status-idle', spin: false, pulse: false }
  }
})

const elapsedMs = computed(() => {
  if (!props.activity.runStartedAt) return null
  const start = new Date(props.activity.runStartedAt).getTime()
  const end = props.activity.lastUpdated ? new Date(props.activity.lastUpdated).getTime() : Date.now()
  return Math.max(0, end - start)
})

const elapsedLabel = computed(() => {
  if (elapsedMs.value == null) return null
  const seconds = Math.floor(elapsedMs.value / 1000)
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  const remainingSeconds = seconds % 60
  return `${minutes}m ${remainingSeconds}s`
})

const progressPercent = computed(() => {
  if (props.activity.totalNodes === 0) return 0
  return Math.min(100, (props.activity.completedNodes / props.activity.totalNodes) * 100)
})

// ---- Agent list ----
const agentList = computed(() => Object.values(props.agentStates))
const planningAgents = computed(() => agentList.value.filter((a) => a.agentLayer === 'planning'))
const executionAgents = computed(() => agentList.value.filter((a) => a.agentLayer === 'execution'))

const hasActivity = computed(
  () =>
    props.activity.status !== 'idle' ||
    props.activity.runId !== null ||
    props.activity.activeAgentName !== null ||
    agentList.value.length > 0,
)

// ---- Task graph ----
const assignmentsByNode = computed(() => {
  const map = new Map<string, AgentAssignmentDto[]>()
  for (const assignment of props.orchestration?.assignments ?? []) {
    const list = map.get(assignment.task_node_id) ?? []
    list.push(assignment)
    map.set(assignment.task_node_id, list)
  }
  return map
})

const sortedNodes = computed(() =>
  [...(props.orchestration?.nodes ?? [])].sort((a, b) => a.priority - b.priority),
)

const taskProgress = computed(() => {
  const nodes = props.orchestration?.nodes ?? []
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
  if (s === 'running' || s === 'in_progress' || s === 'in-progress') return CircleDot
  if (s === 'failed' || s === 'error' || s === 'cancelled') return CircleX
  return Circle
}

function statusClass(status: string): string {
  const s = status.toLowerCase()
  if (s === 'done' || s === 'completed') return 'done'
  if (s === 'running' || s === 'in_progress' || s === 'in-progress') return 'running'
  if (s === 'failed' || s === 'error' || s === 'cancelled') return 'failed'
  return 'pending'
}

// ---- Thinking steps ----
const stepConfig = (type: ThinkingStep['type']) => {
  switch (type) {
    case 'run_started':
      return { icon: Brain, color: 'step-run', label: t('agent.stepRunStarted') }
    case 'task_graph':
      return { icon: Network, color: 'step-graph', label: t('agent.stepTaskGraph') }
    case 'agent_assignment':
      return { icon: UserCheck, color: 'step-assign', label: t('agent.stepAssignment') }
    case 'supervision':
      return { icon: ShieldCheck, color: 'step-supervision', label: t('agent.stepSupervision') }
    case 'context_pack':
      return { icon: Package, color: 'step-context', label: t('agent.stepContext') }
    case 'step_result':
      return { icon: CheckCircle2, color: 'step-result', label: t('agent.stepResult') }
    default:
      return { icon: Brain, color: 'step-default', label: t('agent.stepThinking') }
  }
}

function formatTime(ts: string): string {
  try {
    return new Date(ts).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit' })
  } catch {
    return ''
  }
}

function formatDuration(ms: number | null): string | null {
  if (ms == null) return null
  if (ms < 1000) return `${ms}ms`
  const seconds = ms / 1000
  if (seconds < 60) return `${seconds.toFixed(1)}s`
  const minutes = Math.floor(seconds / 60)
  const remaining = Math.floor(seconds % 60)
  return `${minutes}m${remaining}s`
}

// ---- Progress events ----
const progressIconMap: Record<string, unknown> = {
  play: Play,
  network: Network,
  'user-check': UserCheck,
  shield: ShieldCheck,
  package: Package,
  'check-circle': CheckCircle2,
  'alert-circle': AlertCircle,
  'message-square': MessageSquare,
  terminal: Terminal,
  check: CheckCircle2,
  x: AlertCircle,
}

function getProgressIcon(iconKey: string) {
  return progressIconMap[iconKey] ?? Activity
}

const visibleProgressEvents = computed(() => [...props.progressEvents].slice(-15).reverse())
</script>

<template>
  <section class="agent-activity-panel">
    <!-- Empty state -->
    <div v-if="!hasActivity && !orchestration" class="agent-panel-empty">
      <Activity :size="28" />
      <span>{{ t('agent.noActivity') }}</span>
    </div>

    <template v-else>
      <!-- ===== Status section ===== -->
      <article class="agent-panel-section" :class="statusConfig.color">
        <button class="agent-panel-section-head" @click="statusExpanded = !statusExpanded">
          <div class="agent-panel-status-left">
            <div class="agent-panel-status-icon">
              <component
                :is="statusConfig.icon"
                :size="16"
                :class="{ 'agent-icon-spin': statusConfig.spin, 'agent-icon-pulse': statusConfig.pulse }"
              />
            </div>
            <div class="agent-panel-status-info">
              <div class="agent-panel-status-title-row">
                <strong>{{ activity.activeAgentName ?? t('agent.agent') }}</strong>
                <span class="agent-panel-status-tag">{{ statusConfig.label }}</span>
              </div>
              <div class="agent-panel-status-meta">
                <span v-if="activity.runSummary" class="agent-panel-summary">{{ activity.runSummary }}</span>
                <span v-if="elapsedLabel" class="agent-panel-elapsed">
                  <Clock :size="10" />
                  {{ elapsedLabel }}
                </span>
              </div>
            </div>
          </div>
          <div class="agent-panel-status-right">
            <div v-if="activity.totalNodes > 0" class="agent-panel-progress">
              <div class="agent-panel-progress-bar">
                <div class="agent-panel-progress-fill" :style="{ width: `${progressPercent}%` }" />
              </div>
              <span class="agent-panel-progress-text">{{ activity.completedNodes }}/{{ activity.totalNodes }}</span>
            </div>
            <component :is="statusExpanded ? ChevronDown : ChevronRight" :size="14" class="agent-panel-chevron" />
          </div>
        </button>

        <Transition name="agent-section-expand">
          <div v-if="statusExpanded" class="agent-panel-section-body">
            <!-- Planning agents -->
            <div v-if="planningAgents.length > 0" class="agent-panel-agent-group">
              <span class="agent-panel-group-title">{{ t('agent.planningLayer') }}</span>
              <div class="agent-panel-agent-grid">
                <AgentStatusIndicator
                  v-for="agent in planningAgents"
                  :key="agent.agentId"
                  :agent="agent"
                />
              </div>
            </div>

            <!-- Execution agents -->
            <div v-if="executionAgents.length > 0" class="agent-panel-agent-group">
              <span class="agent-panel-group-title">{{ t('agent.executionLayer') }}</span>
              <div class="agent-panel-agent-grid">
                <AgentStatusIndicator
                  v-for="agent in executionAgents"
                  :key="agent.agentId"
                  :agent="agent"
                />
              </div>
            </div>

            <!-- Waiting for assignment -->
            <div v-if="planningAgents.length === 0 && executionAgents.length === 0" class="agent-panel-waiting">
              <Loader2 :size="12" class="agent-icon-spin" />
              <span>{{ t('agent.waitingAssignment') }}</span>
            </div>
          </div>
        </Transition>
      </article>

      <!-- ===== Task graph section ===== -->
      <article v-if="orchestration?.graph" class="agent-panel-section">
        <button class="agent-panel-section-head" @click="taskGraphCollapsed = !taskGraphCollapsed">
          <div class="agent-panel-section-title">
            <component :is="taskGraphCollapsed ? ChevronRight : ChevronDown" :size="12" />
            <ListTodo :size="12" />
            <span>{{ orchestration.graph.title ?? t('agent.taskPlan') }}</span>
            <span v-if="taskProgress.total > 0" class="agent-panel-count-badge">
              {{ taskProgress.done }}/{{ taskProgress.total }}
            </span>
          </div>
          <div v-if="taskProgress.total > 0" class="agent-panel-mini-bar">
            <div class="agent-panel-mini-bar-fill" :style="{ width: `${taskProgress.percent}%` }" />
          </div>
        </button>

        <Transition name="agent-section-expand">
          <div v-if="!taskGraphCollapsed" class="agent-panel-section-body">
            <ol class="agent-panel-task-list">
              <li
                v-for="node in sortedNodes"
                :key="node.id"
                class="agent-panel-task-step"
                :class="statusClass(node.status)"
              >
                <span class="agent-panel-task-index">{{ node.priority }}</span>
                <component :is="statusIcon(node.status)" :size="11" class="agent-panel-task-status-icon" />
                <div class="agent-panel-task-main">
                  <span class="agent-panel-task-title">{{ node.title }}</span>
                  <span v-if="nodeAssignments(node).length > 0" class="agent-panel-task-agent">
                    {{ nodeAssignments(node)[0].agent_name }}
                  </span>
                </div>
              </li>
            </ol>
          </div>
        </Transition>
      </article>

      <!-- ===== Thinking steps section ===== -->
      <article v-if="thinkingSteps.length > 0" class="agent-panel-section">
        <button class="agent-panel-section-head" @click="thinkingExpanded = !thinkingExpanded">
          <div class="agent-panel-section-title">
            <component :is="thinkingExpanded ? ChevronDown : ChevronRight" :size="12" />
            <Brain :size="12" />
            <span>{{ t('agent.thinkingProcess') }}</span>
            <span class="agent-panel-count-badge">{{ thinkingSteps.length }}</span>
          </div>
        </button>

        <Transition name="agent-section-expand">
          <div v-if="thinkingExpanded" class="agent-panel-section-body">
            <div class="agent-panel-thinking-list">
              <div
                v-for="(step, idx) in thinkingSteps"
                :key="step.id"
                class="agent-panel-thinking-step"
                :class="stepConfig(step.type).color"
              >
                <div v-if="idx < thinkingSteps.length - 1" class="agent-panel-thinking-line" />
                <div class="agent-panel-thinking-icon-wrap">
                  <component :is="stepConfig(step.type).icon" :size="11" />
                </div>
                <div class="agent-panel-thinking-body">
                  <div class="agent-panel-thinking-head">
                    <strong>{{ step.title }}</strong>
                    <span class="agent-panel-thinking-tag">{{ stepConfig(step.type).label }}</span>
                  </div>
                  <p v-if="step.description" class="agent-panel-thinking-desc">{{ step.description }}</p>
                  <div class="agent-panel-thinking-meta">
                    <span class="agent-panel-thinking-time">
                      <Clock :size="9" />
                      {{ formatTime(step.timestamp) }}
                    </span>
                    <span v-if="formatDuration(step.durationMs)" class="agent-panel-thinking-duration">
                      {{ formatDuration(step.durationMs) }}
                    </span>
                    <span
                      v-if="step.severity"
                      class="agent-panel-thinking-severity"
                      :class="`severity-${step.severity}`"
                    >
                      {{ step.severity }}
                    </span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </Transition>
      </article>

      <!-- ===== Progress stream section ===== -->
      <article v-if="visibleProgressEvents.length > 0" class="agent-panel-section">
        <div class="agent-panel-section-head agent-panel-progress-head">
          <div class="agent-panel-section-title">
            <Activity :size="12" />
            <span>{{ t('agent.liveActivity') }}</span>
          </div>
        </div>
        <div class="agent-panel-progress-list">
          <TransitionGroup name="agent-progress-slide">
            <div
              v-for="event in visibleProgressEvents"
              :key="event.id"
              class="agent-panel-progress-item"
            >
              <div class="agent-panel-progress-item-icon">
                <component :is="getProgressIcon(event.icon)" :size="10" />
              </div>
              <div class="agent-panel-progress-item-body">
                <span class="agent-panel-progress-item-text">{{ event.message }}</span>
                <span class="agent-panel-progress-item-time">{{ formatTime(event.timestamp) }}</span>
              </div>
            </div>
          </TransitionGroup>
        </div>
      </article>
    </template>
  </section>
</template>

<style scoped>
.agent-activity-panel {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  height: 100%;
  overflow-y: auto;
}

.agent-panel-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  height: 100%;
  color: var(--text-muted);
}

.agent-panel-empty span {
  font-size: 12px;
}

/* ===== Section card ===== */
.agent-panel-section {
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-section);
  overflow: hidden;
}

.agent-panel-section.status-thinking {
  border-color: rgba(188, 140, 255, 0.35);
  background: linear-gradient(135deg, rgba(188, 140, 255, 0.06), var(--bg-secondary));
}

.agent-panel-section.status-working {
  border-color: rgba(88, 166, 255, 0.35);
  background: linear-gradient(135deg, rgba(88, 166, 255, 0.06), var(--bg-secondary));
}

.agent-panel-section.status-waiting {
  border-color: rgba(210, 153, 34, 0.4);
  background: linear-gradient(135deg, rgba(210, 153, 34, 0.08), var(--bg-secondary));
}

.agent-panel-section.status-completed {
  border-color: rgba(63, 185, 80, 0.35);
}

.agent-panel-section.status-error {
  border-color: rgba(248, 81, 73, 0.4);
  background: linear-gradient(135deg, rgba(248, 81, 73, 0.05), var(--bg-secondary));
}

/* ===== Section header ===== */
.agent-panel-section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 8px 10px;
  background: transparent;
  border: none;
  cursor: pointer;
  text-align: left;
  transition: background 0.15s;
}

.agent-panel-section-head:hover {
  background: var(--surface-hover);
}

.agent-panel-status-left {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  min-width: 0;
  flex: 1;
}

.agent-panel-status-icon {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  flex-shrink: 0;
  border-radius: 7px;
  background: var(--surface-raised);
  color: var(--accent-primary);
}

.status-thinking .agent-panel-status-icon {
  color: #bc8cff;
}

.status-working .agent-panel-status-icon {
  color: var(--accent-primary);
}

.status-waiting .agent-panel-status-icon {
  color: var(--accent-warning);
}

.status-completed .agent-panel-status-icon {
  color: var(--accent-success);
}

.status-error .agent-panel-status-icon {
  color: var(--accent-danger);
}

.agent-icon-spin {
  animation: agent-spin 1.2s linear infinite;
}

.agent-icon-pulse {
  animation: agent-pulse-icon 1.6s ease-in-out infinite;
}

@keyframes agent-spin {
  to { transform: rotate(360deg); }
}

@keyframes agent-pulse-icon {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.5; }
}

.agent-panel-status-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.agent-panel-status-title-row {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.agent-panel-status-title-row strong {
  font-size: 12.5px;
  color: var(--text-primary);
}

.agent-panel-status-tag {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  background: var(--surface-raised);
  color: var(--text-secondary);
}

.status-thinking .agent-panel-status-tag {
  background: rgba(188, 140, 255, 0.14);
  color: #bc8cff;
}

.status-working .agent-panel-status-tag {
  background: rgba(88, 166, 255, 0.14);
  color: var(--accent-primary);
}

.status-waiting .agent-panel-status-tag {
  background: rgba(210, 153, 34, 0.14);
  color: var(--accent-warning);
}

.status-completed .agent-panel-status-tag {
  background: rgba(63, 185, 80, 0.14);
  color: var(--accent-success);
}

.status-error .agent-panel-status-tag {
  background: rgba(248, 81, 73, 0.14);
  color: var(--accent-danger);
}

.agent-panel-status-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.agent-panel-summary {
  overflow: hidden;
  font-size: 11px;
  color: var(--text-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 200px;
}

.agent-panel-elapsed {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 11px;
  color: var(--text-muted);
}

.agent-panel-status-right {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.agent-panel-progress {
  display: flex;
  align-items: center;
  gap: 5px;
}

.agent-panel-progress-bar {
  width: 48px;
  height: 4px;
  border-radius: 2px;
  background: var(--surface-raised);
  overflow: hidden;
}

.agent-panel-progress-fill {
  height: 100%;
  border-radius: 2px;
  background: var(--accent-primary);
  transition: width 0.3s ease;
}

.status-completed .agent-panel-progress-fill {
  background: var(--accent-success);
}

.status-error .agent-panel-progress-fill {
  background: var(--accent-danger);
}

.agent-panel-progress-text {
  font-size: 10px;
  font-weight: 600;
  color: var(--text-secondary);
  white-space: nowrap;
}

.agent-panel-chevron {
  color: var(--text-muted);
  flex-shrink: 0;
}

/* ===== Section title (for task graph, thinking, etc.) ===== */
.agent-panel-section-title {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  flex: 1;
}

.agent-panel-section-title span {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-panel-count-badge {
  display: inline-flex;
  align-items: center;
  padding: 1px 5px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  background: var(--surface-raised);
  color: var(--text-muted);
  flex-shrink: 0;
}

.agent-panel-mini-bar {
  width: 40px;
  height: 3px;
  border-radius: 2px;
  background: var(--surface-raised);
  overflow: hidden;
  flex-shrink: 0;
}

.agent-panel-mini-bar-fill {
  height: 100%;
  background: var(--accent-primary);
  transition: width 0.3s ease;
}

/* ===== Section body ===== */
.agent-panel-section-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 0 10px 10px;
  border-top: 1px solid var(--border-muted);
  padding-top: 8px;
}

/* ===== Agent groups ===== */
.agent-panel-agent-group {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.agent-panel-group-title {
  font-size: 10px;
  font-weight: 700;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.agent-panel-agent-grid {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.agent-panel-waiting {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 6px;
  color: var(--text-muted);
  font-size: 11px;
}

/* ===== Task list ===== */
.agent-panel-task-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.agent-panel-task-step {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  padding: 4px 6px;
  border-radius: 5px;
  background: var(--surface-raised);
}

.agent-panel-task-index {
  flex-shrink: 0;
  width: 16px;
  height: 16px;
  display: grid;
  place-items: center;
  font-size: 10px;
  font-weight: 700;
  color: var(--text-muted);
  background: var(--surface-section);
  border-radius: 4px;
}

.agent-panel-task-status-icon {
  flex-shrink: 0;
  margin-top: 1px;
}

.agent-panel-task-step.pending .agent-panel-task-status-icon {
  color: var(--text-muted);
}

.agent-panel-task-step.running .agent-panel-task-status-icon {
  color: var(--accent-primary);
}

.agent-panel-task-step.done .agent-panel-task-status-icon {
  color: var(--accent-success);
}

.agent-panel-task-step.failed .agent-panel-task-status-icon {
  color: var(--accent-danger);
}

.agent-panel-task-main {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  flex: 1;
}

.agent-panel-task-title {
  font-size: 11.5px;
  color: var(--text-primary);
  line-height: 1.3;
}

.agent-panel-task-step.pending .agent-panel-task-title {
  color: var(--text-secondary);
}

.agent-panel-task-step.done .agent-panel-task-title {
  color: var(--text-secondary);
  text-decoration: line-through;
  text-decoration-color: var(--text-muted);
  text-decoration-thickness: 1px;
}

.agent-panel-task-agent {
  font-size: 10px;
  color: var(--text-muted);
}

/* ===== Thinking steps ===== */
.agent-panel-thinking-list {
  display: flex;
  flex-direction: column;
  gap: 0;
}

.agent-panel-thinking-step {
  display: grid;
  grid-template-columns: 18px 1fr;
  gap: 6px;
  padding: 5px 0;
  position: relative;
}

.agent-panel-thinking-line {
  position: absolute;
  left: 8px;
  top: 22px;
  bottom: -5px;
  width: 1px;
  background: var(--border-muted);
}

.agent-panel-thinking-step:last-child .agent-panel-thinking-line {
  display: none;
}

.agent-panel-thinking-icon-wrap {
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  border-radius: 5px;
  background: var(--surface-raised);
  color: var(--text-secondary);
  flex-shrink: 0;
  z-index: 1;
}

.step-run .agent-panel-thinking-icon-wrap {
  background: rgba(188, 140, 255, 0.12);
  color: #bc8cff;
}

.step-graph .agent-panel-thinking-icon-wrap {
  background: rgba(88, 166, 255, 0.12);
  color: var(--accent-primary);
}

.step-assign .agent-panel-thinking-icon-wrap {
  background: rgba(63, 185, 80, 0.12);
  color: var(--accent-success);
}

.step-supervision .agent-panel-thinking-icon-wrap {
  background: rgba(210, 153, 34, 0.12);
  color: var(--accent-warning);
}

.step-context .agent-panel-thinking-icon-wrap {
  background: rgba(86, 212, 221, 0.12);
  color: #56d4dd;
}

.step-result .agent-panel-thinking-icon-wrap {
  background: rgba(63, 185, 80, 0.12);
  color: var(--accent-success);
}

.agent-panel-thinking-body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.agent-panel-thinking-head {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.agent-panel-thinking-head strong {
  font-size: 11.5px;
  color: var(--text-primary);
}

.agent-panel-thinking-tag {
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 9px;
  font-weight: 600;
  background: var(--surface-raised);
  color: var(--text-muted);
}

.agent-panel-thinking-desc {
  margin: 0;
  font-size: 11px;
  line-height: 1.35;
  color: var(--text-secondary);
  word-break: break-word;
}

.agent-panel-thinking-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.agent-panel-thinking-time {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-size: 9px;
  color: var(--text-muted);
}

.agent-panel-thinking-duration {
  font-size: 9px;
  color: var(--text-muted);
}

.agent-panel-thinking-severity {
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 9px;
  font-weight: 600;
}

.severity-info {
  background: rgba(88, 166, 255, 0.12);
  color: var(--accent-primary);
}

.severity-warning {
  background: rgba(210, 153, 34, 0.14);
  color: var(--accent-warning);
}

.severity-critical,
.severity-error {
  background: rgba(248, 81, 73, 0.14);
  color: var(--accent-danger);
}

/* ===== Progress stream ===== */
.agent-panel-progress-head {
  cursor: default;
}

.agent-panel-progress-head:hover {
  background: transparent;
}

.agent-panel-progress-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 0 10px 8px;
  max-height: 200px;
  overflow-y: auto;
}

.agent-panel-progress-item {
  display: flex;
  align-items: flex-start;
  gap: 5px;
  padding: 2px 0;
}

.agent-panel-progress-item-icon {
  display: grid;
  place-items: center;
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  border-radius: 3px;
  background: var(--surface-raised);
  color: var(--text-secondary);
  margin-top: 1px;
}

.agent-panel-progress-item-body {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  flex: 1;
}

.agent-panel-progress-item-text {
  overflow: hidden;
  font-size: 10.5px;
  color: var(--text-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
  min-width: 0;
}

.agent-panel-progress-item-time {
  flex-shrink: 0;
  font-size: 9px;
  color: var(--text-muted);
}

/* ===== Transitions ===== */
.agent-section-expand-enter-active,
.agent-section-expand-leave-active {
  transition: opacity 0.2s ease, max-height 0.25s ease;
  overflow: hidden;
  max-height: 500px;
}

.agent-section-expand-enter-from,
.agent-section-expand-leave-to {
  opacity: 0;
  max-height: 0;
}

.agent-progress-slide-enter-active {
  transition: all 0.3s ease;
}

.agent-progress-slide-leave-active {
  transition: all 0.2s ease;
  position: absolute;
}

.agent-progress-slide-enter-from {
  opacity: 0;
  transform: translateX(-8px);
}

.agent-progress-slide-leave-to {
  opacity: 0;
  transform: translateX(8px);
}
</style>
