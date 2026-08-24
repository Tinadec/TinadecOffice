<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
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
        return { icon: Brain, color: 'step-run' }
      case 'task_graph':
        return { icon: Network, color: 'step-graph' }
      case 'agent_assignment':
        return { icon: UserCheck, color: 'step-assign' }
      case 'supervision':
        return { icon: ShieldCheck, color: 'step-supervision' }
      case 'context_pack':
        return { icon: Package, color: 'step-context' }
      case 'step_result':
        return { icon: CheckCircle2, color: 'step-result' }
      default:
        return { icon: Brain, color: 'step-default' }
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

/* Latest-step preview drives the collapsed row; re-keying it replays the
   rise animation each time the agent advances. */
const lastStep = computed(() => props.steps[props.steps.length - 1])
const lastStepKey = computed(() => lastStep.value?.id ?? 'none')
const lastPreview = computed(() => {
  const step = lastStep.value
  if (!step) return ''
  return step.description || step.title || ''
})

/* Shimmer only while steps keep advancing; settles back to static muted. */
const advancing = ref(false)
let advanceTimer: ReturnType<typeof setTimeout> | null = null
watch(
  () => props.steps.length,
  () => {
    advancing.value = true
    if (advanceTimer !== null) clearTimeout(advanceTimer)
    advanceTimer = setTimeout(() => {
      advancing.value = false
    }, 2500)
  },
)
onBeforeUnmount(() => {
  if (advanceTimer !== null) clearTimeout(advanceTimer)
})

function stepMetaSuffix(step: ThinkingStep): string {
  const parts = [formatTime(step.timestamp), formatDuration(step.durationMs)].filter(Boolean)
  return parts.join(' · ')
}
</script>

<template>
  <section v-if="hasSteps" class="thinking-process">
    <button class="thinking-row" type="button" @click="expanded = !expanded">
      <Brain :size="14" class="thinking-icon" />
      <span class="thinking-title">已思考 · {{ stepCount }} 步</span>
      <span v-if="lastPreview" class="thinking-sep" aria-hidden="true" />
      <!-- Rise plays on the keyed outer span; shimmer lives on an inner span so
           the two `animation` declarations never fight for the property. -->
      <span :key="lastStepKey" class="thinking-preview chat-status-rise">
        <span :class="{ 'chat-shimmer': advancing }">{{ lastPreview }}</span>
      </span>
      <component :is="expanded ? ChevronDown : ChevronRight" :size="13" class="thinking-chevron" />
    </button>

    <div class="thinking-collapse chat-collapse" :class="{ open: expanded }">
      <div>
        <div class="thinking-steps">
          <div
            v-for="(step, idx) in steps"
            :key="step.id"
            class="thinking-step"
          >
            <div class="thinking-step-line" v-if="idx < steps.length - 1" />
            <div
              class="thinking-step-icon-wrap"
              :class="[stepConfig(step.type).color, step.severity ? `severity-${step.severity}` : null]"
            >
              <component :is="stepConfig(step.type).icon" :size="12" />
            </div>
            <div class="thinking-step-body">
              <div class="thinking-step-head">
                <strong>{{ step.title }}</strong>
                <span class="thinking-step-suffix">{{ stepMetaSuffix(step) }}</span>
              </div>
              <p v-if="step.description" class="thinking-step-desc">{{ step.description }}</p>
            </div>
          </div>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.thinking-process {
  margin-bottom: 8px;
}

.thinking-row {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-width: 0;
  padding: 4px 8px;
  border: none;
  border-radius: 6px;
  background: transparent;
  cursor: pointer;
  text-align: left;
  transition: background 0.15s;
}

.thinking-row:hover {
  background: var(--bg-hover);
}

.thinking-icon {
  flex-shrink: 0;
  color: #bc8cff;
}

.thinking-title {
  flex-shrink: 0;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
}

.thinking-sep {
  flex-shrink: 0;
  width: 2px;
  height: 2px;
  border-radius: 1px;
  background: var(--text-muted);
  opacity: 0.7;
}

.thinking-preview {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-chat-muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.thinking-chevron {
  flex-shrink: 0;
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
  background: color-mix(in srgb, var(--border-muted) 60%, transparent);
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

.step-run .thinking-step-icon-wrap,
.thinking-step-icon-wrap.step-run {
  background: rgba(188, 140, 255, 0.12);
  color: #bc8cff;
}

.thinking-step-icon-wrap.step-graph {
  background: rgba(88, 166, 255, 0.12);
  color: var(--accent-primary);
}

.thinking-step-icon-wrap.step-assign {
  background: rgba(63, 185, 80, 0.12);
  color: var(--accent-success);
}

.thinking-step-icon-wrap.step-supervision {
  background: rgba(210, 153, 34, 0.12);
  color: var(--accent-warning);
}

.thinking-step-icon-wrap.step-context {
  background: rgba(86, 212, 221, 0.12);
  color: #56d4dd;
}

.thinking-step-icon-wrap.step-result {
  background: rgba(63, 185, 80, 0.12);
  color: var(--accent-success);
}

/* Severity tints the glyph itself — no badge pill anymore. */
.thinking-step-icon-wrap.severity-warning {
  background: transparent;
  color: var(--accent-warning);
}

.thinking-step-icon-wrap.severity-critical,
.thinking-step-icon-wrap.severity-error {
  background: transparent;
  color: var(--accent-danger);
}

.thinking-step-body {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.thinking-step-head {
  display: flex;
  align-items: baseline;
  gap: 6px;
  flex-wrap: wrap;
}

.thinking-step-head strong {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.thinking-step-suffix {
  font-size: 10px;
  color: var(--text-muted);
}

.thinking-step-desc {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-chat-muted);
  word-break: break-word;
}
</style>
