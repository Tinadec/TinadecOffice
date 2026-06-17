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
} from '@lucide/vue'
import type { AgentActivity, AgentState } from '@/composables/useAgentActivity'
import AgentStatusIndicator from './AgentStatusIndicator.vue'

const props = defineProps<{
  activity: AgentActivity
  agentStates: Record<string, AgentState>
}>()

const expanded = ref(false)

const statusConfig = computed(() => {
  switch (props.activity.status) {
    case 'thinking':
      return {
        icon: Brain,
        label: '思考中',
        color: 'banner-thinking',
        spin: true,
        pulse: false,
      }
    case 'working':
      return {
        icon: Activity,
        label: '工作中',
        color: 'banner-working',
        spin: false,
        pulse: true,
      }
    case 'waiting_approval':
      return {
        icon: ShieldAlert,
        label: '等待审批',
        color: 'banner-waiting',
        spin: false,
        pulse: false,
      }
    case 'completed':
      return {
        icon: CheckCircle2,
        label: '已完成',
        color: 'banner-completed',
        spin: false,
        pulse: false,
      }
    case 'error':
      return {
        icon: AlertCircle,
        label: '出错',
        color: 'banner-error',
        spin: false,
        pulse: false,
      }
    default:
      return {
        icon: Activity,
        label: '空闲',
        color: 'banner-idle',
        spin: false,
        pulse: false,
      }
  }
})

const elapsedMs = computed(() => {
  if (!props.activity.runStartedAt) return null
  const start = new Date(props.activity.runStartedAt).getTime()
  const end = props.activity.lastUpdated
    ? new Date(props.activity.lastUpdated).getTime()
    : Date.now()
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

const startedTimeLabel = computed(() => {
  if (!props.activity.runStartedAt) return null
  try {
    return new Date(props.activity.runStartedAt).toLocaleTimeString('zh-CN', {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    })
  } catch {
    return null
  }
})

const progressPercent = computed(() => {
  if (props.activity.totalNodes === 0) return 0
  return Math.min(100, (props.activity.completedNodes / props.activity.totalNodes) * 100)
})

const agentList = computed(() => Object.values(props.agentStates))

const planningAgents = computed(() =>
  agentList.value.filter((a) => a.agentLayer === 'planning'),
)
const executionAgents = computed(() =>
  agentList.value.filter((a) => a.agentLayer === 'execution'),
)

const hasActivity = computed(
  () =>
    props.activity.status !== 'idle' &&
    (props.activity.runId !== null ||
      props.activity.activeAgentName !== null ||
      agentList.value.length > 0),
)
</script>

<template>
  <section v-if="hasActivity" class="agent-banner" :class="statusConfig.color">
    <div class="agent-banner-header" @click="expanded = !expanded">
      <div class="agent-banner-left">
        <div class="agent-banner-icon-wrap">
          <component
            :is="statusConfig.icon"
            :size="16"
            :class="{
              'agent-icon-spin': statusConfig.spin,
              'agent-icon-pulse': statusConfig.pulse,
            }"
          />
        </div>
        <div class="agent-banner-info">
          <div class="agent-banner-title-row">
            <strong>{{ activity.activeAgentName ?? '智能体' }}</strong>
            <span v-if="activity.activeAgentRole" class="agent-banner-role">{{ activity.activeAgentRole }}</span>
            <span class="agent-banner-status-tag">{{ statusConfig.label }}</span>
          </div>
          <div class="agent-banner-meta">
            <span v-if="activity.runSummary" class="agent-banner-summary">{{ activity.runSummary }}</span>
            <span v-if="startedTimeLabel" class="agent-banner-time">
              <Clock :size="11" />
              {{ startedTimeLabel }}
            </span>
            <span v-if="elapsedLabel" class="agent-banner-elapsed">已耗时 {{ elapsedLabel }}</span>
          </div>
        </div>
      </div>

      <div class="agent-banner-right">
        <div v-if="activity.totalNodes > 0" class="agent-banner-progress">
          <div class="agent-banner-progress-bar">
            <div class="agent-banner-progress-fill" :style="{ width: `${progressPercent}%` }" />
          </div>
          <span class="agent-banner-progress-text">{{ activity.completedNodes }}/{{ activity.totalNodes }}</span>
        </div>
        <button class="agent-banner-toggle" type="button">
          <ChevronDown v-if="expanded" :size="14" />
          <ChevronRight v-else :size="14" />
        </button>
      </div>
    </div>

    <Transition name="banner-expand">
      <div v-if="expanded" class="agent-banner-details">
        <div v-if="planningAgents.length > 0" class="agent-banner-section">
          <span class="agent-banner-section-title">规划层智能体</span>
          <div class="agent-banner-agent-grid">
            <AgentStatusIndicator
              v-for="agent in planningAgents"
              :key="agent.agentId"
              :agent="agent"
            />
          </div>
        </div>
        <div v-if="executionAgents.length > 0" class="agent-banner-section">
          <span class="agent-banner-section-title">执行层智能体</span>
          <div class="agent-banner-agent-grid">
            <AgentStatusIndicator
              v-for="agent in executionAgents"
              :key="agent.agentId"
              :agent="agent"
            />
          </div>
        </div>
        <div v-if="planningAgents.length === 0 && executionAgents.length === 0" class="agent-banner-empty">
          <Loader2 :size="14" class="agent-icon-spin" />
          <span>正在等待智能体分配...</span>
        </div>
      </div>
    </Transition>
  </section>
</template>

<style scoped>
.agent-banner {
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--bg-secondary);
  overflow: hidden;
  transition: border-color 0.2s;
}

.agent-banner.banner-thinking {
  border-color: rgba(188, 140, 255, 0.35);
  background: linear-gradient(135deg, rgba(188, 140, 255, 0.06), var(--bg-secondary));
}

.agent-banner.banner-working {
  border-color: rgba(88, 166, 255, 0.35);
  background: linear-gradient(135deg, rgba(88, 166, 255, 0.06), var(--bg-secondary));
}

.agent-banner.banner-waiting {
  border-color: rgba(210, 153, 34, 0.4);
  background: linear-gradient(135deg, rgba(210, 153, 34, 0.08), var(--bg-secondary));
}

.agent-banner.banner-completed {
  border-color: rgba(63, 185, 80, 0.35);
}

.agent-banner.banner-error {
  border-color: rgba(248, 81, 73, 0.4);
  background: linear-gradient(135deg, rgba(248, 81, 73, 0.05), var(--bg-secondary));
}

.agent-banner-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 12px;
  cursor: pointer;
  user-select: none;
}

.agent-banner-left {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  min-width: 0;
  flex: 1;
}

.agent-banner-icon-wrap {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  flex-shrink: 0;
  border-radius: 8px;
  background: var(--bg-tertiary);
  color: var(--accent-primary);
}

.banner-thinking .agent-banner-icon-wrap {
  color: #bc8cff;
}

.banner-working .agent-banner-icon-wrap {
  color: var(--accent-primary);
}

.banner-waiting .agent-banner-icon-wrap {
  color: var(--accent-warning);
}

.banner-completed .agent-banner-icon-wrap {
  color: var(--accent-success);
}

.banner-error .agent-banner-icon-wrap {
  color: var(--accent-danger);
}

.agent-icon-spin {
  animation: agent-spin 1.2s linear infinite;
}

.agent-icon-pulse {
  animation: agent-pulse-icon 1.6s ease-in-out infinite;
}

@keyframes agent-spin {
  to {
    transform: rotate(360deg);
  }
}

@keyframes agent-pulse-icon {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.5;
  }
}

.agent-banner-info {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  flex: 1;
}

.agent-banner-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.agent-banner-title-row strong {
  font-size: 13px;
  color: var(--text-primary);
}

.agent-banner-role {
  font-size: 11px;
  color: var(--text-secondary);
}

.agent-banner-status-tag {
  display: inline-flex;
  align-items: center;
  padding: 1px 7px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  background: var(--bg-tertiary);
  color: var(--text-secondary);
}

.banner-thinking .agent-banner-status-tag {
  background: rgba(188, 140, 255, 0.14);
  color: #bc8cff;
}

.banner-working .agent-banner-status-tag {
  background: rgba(88, 166, 255, 0.14);
  color: var(--accent-primary);
}

.banner-waiting .agent-banner-status-tag {
  background: rgba(210, 153, 34, 0.14);
  color: var(--accent-warning);
}

.banner-completed .agent-banner-status-tag {
  background: rgba(63, 185, 80, 0.14);
  color: var(--accent-success);
}

.banner-error .agent-banner-status-tag {
  background: rgba(248, 81, 73, 0.14);
  color: var(--accent-danger);
}

.agent-banner-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.agent-banner-summary {
  overflow: hidden;
  font-size: 11px;
  color: var(--text-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 320px;
}

.agent-banner-time {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 11px;
  color: var(--text-muted);
}

.agent-banner-elapsed {
  font-size: 11px;
  color: var(--text-muted);
}

.agent-banner-right {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-shrink: 0;
}

.agent-banner-progress {
  display: flex;
  align-items: center;
  gap: 6px;
}

.agent-banner-progress-bar {
  width: 60px;
  height: 4px;
  border-radius: 2px;
  background: var(--bg-tertiary);
  overflow: hidden;
}

.agent-banner-progress-fill {
  height: 100%;
  border-radius: 2px;
  background: var(--accent-primary);
  transition: width 0.3s ease;
}

.banner-completed .agent-banner-progress-fill {
  background: var(--accent-success);
}

.banner-error .agent-banner-progress-fill {
  background: var(--accent-danger);
}

.agent-banner-progress-text {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
  white-space: nowrap;
}

.agent-banner-toggle {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  color: var(--text-muted);
  background: transparent;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  transition: color 0.15s, background 0.15s;
}

.agent-banner-toggle:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.agent-banner-details {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 0 12px 12px;
  border-top: 1px solid var(--border-muted);
  padding-top: 10px;
}

.agent-banner-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.agent-banner-section-title {
  font-size: 11px;
  font-weight: 700;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.agent-banner-agent-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 6px;
}

.agent-banner-empty {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px;
  color: var(--text-muted);
  font-size: 12px;
}

.banner-expand-enter-active,
.banner-expand-leave-active {
  transition: opacity 0.2s ease, max-height 0.25s ease;
  overflow: hidden;
  max-height: 400px;
}

.banner-expand-enter-from,
.banner-expand-leave-to {
  opacity: 0;
  max-height: 0;
}
</style>
