<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import {
  CheckCircle2,
  XCircle,
  Loader2,
  Clock,
  ShieldAlert,
  ChevronDown,
  ChevronRight,
} from '@lucide/vue'
import type { ToolCall } from '@/composables/useAgentActivity'
import TerminalCallBlock from './TerminalCallBlock.vue'

const props = defineProps<{
  toolCall: ToolCall
  /** Run that owns the tool call; enables pause/resume on terminal blocks. */
  runId?: string | null
}>()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
}>()

/* Auto-disclosure model (Codex fold bias + OpenCodeUI): open while the tool is
   active or awaiting approval, fold again shortly after it settles. Once the
   user toggles manually we stop touching their choice. */
const isActiveStatus = (status: ToolCall['status']) =>
  status === 'running' || status === 'waiting_approval'

const expanded = ref(isActiveStatus(props.toolCall.status))
const touched = ref(false)
let collapseTimer: ReturnType<typeof setTimeout> | null = null

function toggle(): void {
  touched.value = true
  if (collapseTimer !== null) {
    clearTimeout(collapseTimer)
    collapseTimer = null
  }
  expanded.value = !expanded.value
}

watch(
  () => props.toolCall.status,
  (next) => {
    if (isActiveStatus(next)) {
      if (collapseTimer !== null) {
        clearTimeout(collapseTimer)
        collapseTimer = null
      }
      if (!touched.value) expanded.value = true
      return
    }
    if (!touched.value && expanded.value) {
      collapseTimer = setTimeout(() => {
        expanded.value = false
        collapseTimer = null
      }, 800)
    }
  },
)

onBeforeUnmount(() => {
  if (collapseTimer !== null) clearTimeout(collapseTimer)
})

const statusConfig = computed(() => {
  switch (props.toolCall.status) {
    case 'running':
      return { icon: Loader2, key: 'running', spin: true, glow: true }
    case 'completed':
      return { icon: CheckCircle2, key: 'completed', spin: false, glow: false }
    case 'failed':
      return { icon: XCircle, key: 'failed', spin: false, glow: false }
    case 'waiting_approval':
      return { icon: ShieldAlert, key: 'waiting', spin: false, glow: true }
    default:
      return { icon: Clock, key: 'pending', spin: false, glow: false }
  }
})

const durationLabel = computed(() => {
  if (props.toolCall.durationMs == null) return null
  if (props.toolCall.durationMs < 1000) return `${props.toolCall.durationMs}ms`
  return `${(props.toolCall.durationMs / 1000).toFixed(2)}s`
})

const hasDetails = computed(
  () =>
    props.toolCall.evidence.length > 0 ||
    (props.toolCall.resultSummary && props.toolCall.resultSummary !== props.toolCall.argsSummary),
)

const isRisky = computed(
  () => props.toolCall.risk === 'high' || props.toolCall.risk === 'critical',
)

/**
 * Shell calls render as a live terminal block: the user can watch output, pause
 * the run, and jump into the function panel to help the agent finish.
 */
const isShellCall = computed(
  () => props.toolCall.toolId === 'shell' || props.toolCall.toolName.includes('Shell'),
)
</script>

<template>
  <article class="tool-call-card" :class="[`tool-${statusConfig.key}`]">
    <div class="tool-call-head" @click="hasDetails ? toggle() : undefined">
      <component
        :is="statusConfig.icon"
        :size="13"
        class="tool-status-glyph"
        :class="{ 'activity-glow-icon': statusConfig.glow, 'tool-icon-spin': statusConfig.spin }"
      />

      <div class="tool-call-main">
        <div class="tool-call-title-row">
          <strong>{{ toolCall.toolName }}</strong>
          <span v-if="isRisky" class="tool-call-risk-tag">高风险</span>
        </div>
        <p
          v-if="toolCall.argsSummary"
          class="tool-call-args"
          :class="{ 'chat-shimmer': toolCall.status === 'running' }"
        >
          {{ toolCall.argsSummary }}
        </p>
      </div>

      <div :key="toolCall.status" class="tool-call-meta chat-status-rise">
        <span v-if="durationLabel" class="tool-call-duration">{{ durationLabel }}</span>
        <button
          v-if="hasDetails"
          class="tool-call-toggle"
          type="button"
          :aria-expanded="expanded"
          @click.stop="toggle"
        >
          <ChevronDown v-if="expanded" :size="12" />
          <ChevronRight v-else :size="12" />
        </button>
      </div>
    </div>

    <div v-if="toolCall.status === 'waiting_approval' && toolCall.approvalId" class="tool-call-approval">
      <span class="tool-call-approval-text">此操作需要你的审批</span>
      <div class="tool-call-approval-actions">
        <button class="tool-call-approve-btn" type="button" @click="emit('approve', toolCall.approvalId!)">
          <CheckCircle2 :size="12" />
          批准
        </button>
        <button class="tool-call-reject-btn" type="button" @click="emit('reject', toolCall.approvalId!)">
          <XCircle :size="12" />
          拒绝
        </button>
      </div>
    </div>

    <TerminalCallBlock
      v-if="isShellCall && expanded"
      :execution-id="toolCall.id"
      :command="toolCall.argsSummary"
      :run-id="runId"
      :status="toolCall.status"
    />

    <!-- Keep details mounted so the grid-rows collapse transition can play. -->
    <div v-if="hasDetails" class="tool-details-collapse chat-collapse" :class="{ open: expanded }">
      <div>
        <div class="tool-call-details">
          <div v-if="toolCall.resultSummary && toolCall.resultSummary !== toolCall.argsSummary" class="tool-call-section">
            <span class="tool-call-section-title">结果摘要</span>
            <p class="tool-call-section-text">{{ toolCall.resultSummary }}</p>
          </div>
          <div v-if="toolCall.evidence.length > 0" class="tool-call-section">
            <span class="tool-call-section-title">证据 ({{ toolCall.evidence.length }})</span>
            <ul class="tool-call-evidence-list">
              <li v-for="(item, idx) in toolCall.evidence" :key="idx">{{ item }}</li>
            </ul>
          </div>
        </div>
      </div>
    </div>
  </article>
</template>

<style scoped>
.tool-call-card {
  border-radius: 6px;
}

.tool-call-card.tool-waiting {
  /* Quiet amber wash keeps the approval gate visible without a border. */
  background: rgba(210, 153, 34, 0.07);
}

.tool-call-head {
  display: flex;
  align-items: flex-start;
  gap: 7px;
  min-width: 0;
  padding: 3px 4px;
  border-radius: 5px;
  cursor: default;
  transition: background 0.15s;
}

.tool-call-card:has(.tool-call-toggle) .tool-call-head {
  cursor: pointer;
}

.tool-call-head:hover {
  background: var(--bg-hover);
}

.tool-status-glyph {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--text-muted);
}

.tool-running .tool-status-glyph {
  color: var(--accent-primary);
}

.tool-completed .tool-status-glyph {
  color: var(--accent-success);
}

.tool-failed .tool-status-glyph {
  color: var(--accent-danger);
}

.tool-waiting .tool-status-glyph {
  color: var(--accent-warning);
}

.tool-icon-spin {
  animation: tool-spin 1s linear infinite;
}

@keyframes tool-spin {
  to {
    transform: rotate(360deg);
  }
}

.tool-call-main {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  flex: 1;
}

.tool-call-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
}

.tool-call-title-row strong {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
}

.tool-failed .tool-call-title-row strong {
  color: var(--accent-danger);
}

.tool-call-risk-tag {
  padding: 1px 5px;
  border-radius: 4px;
  font-size: 9px;
  font-weight: 700;
  background: rgba(248, 81, 73, 0.14);
  color: var(--accent-danger);
}

.tool-call-args {
  margin: 0;
  overflow: hidden;
  max-width: 100%;
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-chat-muted);
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
}

.tool-call-meta {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
  align-self: flex-start;
}

.tool-call-duration {
  font-size: 10px;
  color: var(--text-muted);
}

.tool-call-toggle {
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  color: var(--text-muted);
  background: transparent;
  border: none;
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.15s, color 0.1s;
}

.tool-call-card:hover .tool-call-toggle,
.tool-call-toggle:focus-visible {
  opacity: 1;
}

.tool-call-toggle:hover {
  color: var(--text-primary);
}

.tool-call-approval {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 6px 10px;
  border-top: none;
  border-radius: 0 0 6px 6px;
}

.tool-call-approval-text {
  font-size: 11px;
  color: var(--accent-warning);
  font-weight: 600;
}

.tool-call-approval-actions {
  display: flex;
  gap: 4px;
}

.tool-call-approve-btn,
.tool-call-reject-btn {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 3px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}

.tool-call-approve-btn {
  color: var(--accent-success);
  background: transparent;
}

.tool-call-approve-btn:hover {
  background: rgba(63, 185, 80, 0.1);
  border-color: var(--accent-success);
}

.tool-call-reject-btn {
  color: var(--accent-danger);
  background: transparent;
}

.tool-call-reject-btn:hover {
  background: rgba(248, 81, 73, 0.1);
  border-color: var(--accent-danger);
}

.tool-details-collapse {
  will-change: grid-template-rows;
}

.tool-call-details {
  /* Proma-style left hairline indent instead of boxed separators. */
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 2px 8px 6px 20px;
  padding: 2px 0 2px 10px;
  border-left: 2px solid var(--border-muted);
}

.tool-call-section {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.tool-call-section-title {
  font-size: 10px;
  font-weight: 700;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.tool-call-section-text {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-secondary);
  word-break: break-word;
}

.tool-call-evidence-list {
  margin: 0;
  padding-left: 16px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.tool-call-evidence-list li {
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-secondary);
  word-break: break-word;
}
</style>
