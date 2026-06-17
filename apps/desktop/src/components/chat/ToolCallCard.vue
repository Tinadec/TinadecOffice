<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  FileText,
  Search,
  FilePen,
  GitBranch,
  Terminal,
  FolderSearch,
  CheckCircle2,
  XCircle,
  Loader2,
  Clock,
  ShieldAlert,
  ChevronDown,
  ChevronRight,
  Wrench,
} from '@lucide/vue'
import type { ToolCall } from '@/composables/useAgentActivity'

const props = defineProps<{
  toolCall: ToolCall
}>()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
}>()

const expanded = ref(false)

const toolIcon = computed(() => {
  const id = props.toolCall.toolId.toLowerCase()
  if (id.includes('read') || id.includes('file')) return FileText
  if (id.includes('search') || id.includes('grep') || id.includes('glob')) return Search
  if (id.includes('patch') || id.includes('write') || id.includes('edit')) return FilePen
  if (id.includes('git')) return GitBranch
  if (id.includes('shell') || id.includes('exec') || id.includes('sandbox')) return Terminal
  if (id.includes('list') || id.includes('directory') || id.includes('find')) return FolderSearch
  return Wrench
})

const statusConfig = computed(() => {
  switch (props.toolCall.status) {
    case 'running':
      return { icon: Loader2, label: '执行中', color: 'tool-running', spin: true }
    case 'completed':
      return { icon: CheckCircle2, label: '已完成', color: 'tool-completed', spin: false }
    case 'failed':
      return { icon: XCircle, label: '失败', color: 'tool-failed', spin: false }
    case 'waiting_approval':
      return { icon: ShieldAlert, label: '等待审批', color: 'tool-waiting', spin: false }
    default:
      return { icon: Clock, label: '待执行', color: 'tool-pending', spin: false }
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
</script>

<template>
  <article class="tool-call-card" :class="statusConfig.color">
    <div class="tool-call-head" @click="expanded = !expanded">
      <div class="tool-call-icon-wrap" :class="{ risky: isRisky }">
        <component :is="toolIcon" :size="14" />
      </div>

      <div class="tool-call-main">
        <div class="tool-call-title-row">
          <strong>{{ toolCall.toolName }}</strong>
          <span v-if="isRisky" class="tool-call-risk-tag">高风险</span>
        </div>
        <p v-if="toolCall.argsSummary" class="tool-call-args">{{ toolCall.argsSummary }}</p>
      </div>

      <div class="tool-call-status-wrap">
        <div class="tool-call-status-badge">
          <component
            :is="statusConfig.icon"
            :size="11"
            :class="{ 'tool-icon-spin': statusConfig.spin }"
          />
          <span>{{ statusConfig.label }}</span>
        </div>
        <span v-if="durationLabel" class="tool-call-duration">{{ durationLabel }}</span>
      </div>

      <button v-if="hasDetails" class="tool-call-toggle" type="button" @click.stop="expanded = !expanded">
        <ChevronDown v-if="expanded" :size="12" />
        <ChevronRight v-else :size="12" />
      </button>
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

    <Transition name="tool-expand">
      <div v-if="expanded && hasDetails" class="tool-call-details">
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
    </Transition>
  </article>
</template>

<style scoped>
.tool-call-card {
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
  overflow: hidden;
  transition: border-color 0.15s;
}

.tool-call-card.tool-running {
  border-color: rgba(88, 166, 255, 0.3);
}

.tool-call-card.tool-completed {
  border-color: rgba(63, 185, 80, 0.25);
}

.tool-call-card.tool-failed {
  border-color: rgba(248, 81, 73, 0.3);
}

.tool-call-card.tool-waiting {
  border-color: rgba(210, 153, 34, 0.35);
  background: rgba(210, 153, 34, 0.04);
}

.tool-call-head {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px 10px;
  cursor: pointer;
}

.tool-call-icon-wrap {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  flex-shrink: 0;
  border-radius: 6px;
  background: var(--bg-tertiary);
  color: var(--accent-primary);
}

.tool-call-icon-wrap.risky {
  background: rgba(248, 81, 73, 0.12);
  color: var(--accent-danger);
}

.tool-call-main {
  display: flex;
  flex-direction: column;
  gap: 2px;
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
  color: var(--text-primary);
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
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-secondary);
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
}

.tool-call-status-wrap {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 3px;
  flex-shrink: 0;
}

.tool-call-status-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  background: var(--bg-tertiary);
  color: var(--text-secondary);
}

.tool-running .tool-call-status-badge {
  background: rgba(88, 166, 255, 0.12);
  color: var(--accent-primary);
}

.tool-completed .tool-call-status-badge {
  background: rgba(63, 185, 80, 0.12);
  color: var(--accent-success);
}

.tool-failed .tool-call-status-badge {
  background: rgba(248, 81, 73, 0.12);
  color: var(--accent-danger);
}

.tool-waiting .tool-call-status-badge {
  background: rgba(210, 153, 34, 0.14);
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
  flex-shrink: 0;
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
  border-top: 1px solid var(--border-muted);
  background: rgba(210, 153, 34, 0.06);
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

.tool-call-details {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 0 10px 10px;
  border-top: 1px solid var(--border-muted);
  padding-top: 8px;
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

.tool-expand-enter-active,
.tool-expand-leave-active {
  transition: opacity 0.2s ease, max-height 0.25s ease;
  overflow: hidden;
  max-height: 400px;
}

.tool-expand-enter-from,
.tool-expand-leave-to {
  opacity: 0;
  max-height: 0;
}
</style>
