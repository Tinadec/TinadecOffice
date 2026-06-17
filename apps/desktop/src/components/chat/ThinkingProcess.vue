<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  Brain,
  Network,
  UserCheck,
  ShieldCheck,
  Package,
  CheckCircle2,
  ChevronRight,
  ChevronDown,
  Clock,
} from '@lucide/vue'
import type { ThinkingStep } from '@/composables/useAgentActivity'

const props = defineProps<{
  steps: ThinkingStep[]
}>()

const expanded = ref(false)

const stepConfig = computed(() => {
  return (type: ThinkingStep['type']) => {
    switch (type) {
      case 'run_started':
        return { icon: Brain, color: 'step-run', label: '运行启动' }
      case 'task_graph':
        return { icon: Network, color: 'step-graph', label: '任务图' }
      case 'agent_assignment':
        return { icon: UserCheck, color: 'step-assign', label: '任务分配' }
      case 'supervision':
        return { icon: ShieldCheck, color: 'step-supervision', label: '监督' }
      case 'context_pack':
        return { icon: Package, color: 'step-context', label: '上下文' }
      case 'step_result':
        return { icon: CheckCircle2, color: 'step-result', label: '步骤结果' }
      default:
        return { icon: Brain, color: 'step-default', label: '思考' }
    }
  }
})

function formatTime(ts: string): string {
  try {
    return new Date(ts).toLocaleTimeString('zh-CN', {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    })
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

const hasSteps = computed(() => props.steps.length > 0)

const stepCount = computed(() => props.steps.length)
</script>

<template>
  <section v-if="hasSteps" class="thinking-process">
    <button class="thinking-header" type="button" @click="expanded = !expanded">
      <div class="thinking-header-left">
        <Brain :size="14" class="thinking-icon" />
        <span class="thinking-title">思考过程</span>
        <span class="thinking-count">{{ stepCount }} 步</span>
      </div>
      <component
        :is="expanded ? ChevronDown : ChevronRight"
        :size="14"
        class="thinking-chevron"
      />
    </button>

    <Transition name="thinking-expand">
      <div v-if="expanded" class="thinking-steps">
        <div
          v-for="(step, idx) in steps"
          :key="step.id"
          class="thinking-step"
          :class="stepConfig(step.type).color"
        >
          <div class="thinking-step-line" v-if="idx < steps.length - 1" />
          <div class="thinking-step-icon-wrap">
            <component :is="stepConfig(step.type).icon" :size="12" />
          </div>
          <div class="thinking-step-body">
            <div class="thinking-step-head">
              <strong>{{ step.title }}</strong>
              <span class="thinking-step-type-tag">{{ stepConfig(step.type).label }}</span>
            </div>
            <p v-if="step.description" class="thinking-step-desc">{{ step.description }}</p>
            <div class="thinking-step-meta">
              <span class="thinking-step-time">
                <Clock :size="10" />
                {{ formatTime(step.timestamp) }}
              </span>
              <span v-if="formatDuration(step.durationMs)" class="thinking-step-duration">
                {{ formatDuration(step.durationMs) }}
              </span>
              <span
                v-if="step.severity"
                class="thinking-step-severity"
                :class="`severity-${step.severity}`"
              >
                {{ step.severity }}
              </span>
            </div>
          </div>
        </div>
      </div>
    </Transition>
  </section>
</template>

<style scoped>
.thinking-process {
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
  overflow: hidden;
  margin-bottom: 8px;
}

.thinking-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding: 8px 10px;
  background: transparent;
  border: none;
  cursor: pointer;
  text-align: left;
  transition: background 0.15s;
}

.thinking-header:hover {
  background: var(--bg-hover);
}

.thinking-header-left {
  display: flex;
  align-items: center;
  gap: 6px;
}

.thinking-icon {
  color: #bc8cff;
}

.thinking-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
}

.thinking-count {
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  background: var(--bg-tertiary);
  color: var(--text-muted);
}

.thinking-chevron {
  color: var(--text-muted);
}

.thinking-steps {
  display: flex;
  flex-direction: column;
  gap: 0;
  padding: 4px 10px 10px;
}

.thinking-step {
  display: grid;
  grid-template-columns: 20px 1fr;
  gap: 8px;
  padding: 6px 0;
  position: relative;
}

.thinking-step-line {
  position: absolute;
  left: 9px;
  top: 24px;
  bottom: -6px;
  width: 1px;
  background: var(--border-muted);
}

.thinking-step:last-child .thinking-step-line {
  display: none;
}

.thinking-step-icon-wrap {
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  border-radius: 6px;
  background: var(--bg-tertiary);
  color: var(--text-secondary);
  flex-shrink: 0;
  z-index: 1;
}

.step-run .thinking-step-icon-wrap {
  background: rgba(188, 140, 255, 0.12);
  color: #bc8cff;
}

.step-graph .thinking-step-icon-wrap {
  background: rgba(88, 166, 255, 0.12);
  color: var(--accent-primary);
}

.step-assign .thinking-step-icon-wrap {
  background: rgba(63, 185, 80, 0.12);
  color: var(--accent-success);
}

.step-supervision .thinking-step-icon-wrap {
  background: rgba(210, 153, 34, 0.12);
  color: var(--accent-warning);
}

.step-context .thinking-step-icon-wrap {
  background: rgba(86, 212, 221, 0.12);
  color: #56d4dd;
}

.step-result .thinking-step-icon-wrap {
  background: rgba(63, 185, 80, 0.12);
  color: var(--accent-success);
}

.thinking-step-body {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.thinking-step-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.thinking-step-head strong {
  font-size: 12px;
  color: var(--text-primary);
}

.thinking-step-type-tag {
  padding: 1px 5px;
  border-radius: 4px;
  font-size: 10px;
  font-weight: 600;
  background: var(--bg-tertiary);
  color: var(--text-muted);
}

.thinking-step-desc {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-secondary);
  word-break: break-word;
}

.thinking-step-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.thinking-step-time {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 10px;
  color: var(--text-muted);
}

.thinking-step-duration {
  font-size: 10px;
  color: var(--text-muted);
}

.thinking-step-severity {
  padding: 1px 5px;
  border-radius: 4px;
  font-size: 10px;
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

.thinking-expand-enter-active,
.thinking-expand-leave-active {
  transition: opacity 0.2s ease, max-height 0.25s ease;
  overflow: hidden;
  max-height: 600px;
}

.thinking-expand-enter-from,
.thinking-expand-leave-to {
  opacity: 0;
  max-height: 0;
}
</style>
