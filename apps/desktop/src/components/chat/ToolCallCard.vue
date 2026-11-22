<script setup lang="ts">
import { Transition, computed, ref, useId, type FunctionalComponent, type TransitionProps } from 'vue'
import { useI18n } from 'vue-i18n'

const MotionTransition: FunctionalComponent<TransitionProps> = (props, ctx) => Transition(props, ctx)
import { CheckCircle2, XCircle, Loader2, Clock, ShieldAlert, ChevronRight, GitBranch, Send, Bot } from '@lucide/vue'
import { toolIconOf } from '@/lib/toolPresentation'
import type { ToolCall } from '@/composables/useAgentActivity'
import { focusDockCard, parseToolDockInfo } from '@/lib/homeDockSync'
import TerminalCallBlock from './TerminalCallBlock.vue'
import ApprovalGateStatus from '@/components/approval/ApprovalGateStatus.vue'

const props = defineProps<{
  toolCall: ToolCall
  runId?: string | null
  workerHandle?: string | null
  workerTaskId?: string | null
  branch?: string | null
  worktreePath?: string | null
}>()
const emit = defineEmits<{ approve: [approvalId: string]; reject: [approvalId: string] }>()
const { t } = useI18n()
const expanded = ref(false)
const detailsId = useId()
const statusConfig = computed(() => {
  switch (props.toolCall.status) {
    case 'running': return { icon: Loader2, key: 'running' }
    case 'completed': return { icon: CheckCircle2, key: 'completed' }
    case 'failed': return { icon: XCircle, key: 'failed' }
    case 'waiting_approval': return { icon: ShieldAlert, key: 'waiting' }
    default: return { icon: Clock, key: 'pending' }
  }
})
const durationLabel = computed(() => props.toolCall.durationMs == null ? null
  : props.toolCall.durationMs < 1000 ? `${props.toolCall.durationMs}ms` : `${(props.toolCall.durationMs / 1000).toFixed(1)}s`)
const isRisky = computed(() => ['high', 'critical'].includes(props.toolCall.risk))
const isShellCall = computed(() => props.toolCall.toolId === 'shell')
const toolIcon = computed(() => toolIconOf(props.toolCall.toolId))

const dockInfo = computed(() => parseToolDockInfo(props.toolCall))
const resolvedWorkerHandle = computed(() => props.workerHandle ?? dockInfo.value?.workerHandle ?? null)
const resolvedBranch = computed(() => props.branch ?? dockInfo.value?.branch ?? null)
const resolvedWorktreePath = computed(() => props.worktreePath ?? dockInfo.value?.worktreePath ?? null)
const resolvedRunId = computed(() => props.runId ?? props.toolCall.runId ?? null)

function openDockFromCard(): void {
  if (!dockInfo.value) return
  if (dockInfo.value.kind === 'approval') {
    focusDockCard({
      card: 'approval',
      reason: 'approval',
      runId: resolvedRunId.value,
      taskId: props.workerTaskId ?? dockInfo.value.taskKey,
      workerHandle: resolvedWorkerHandle.value,
      approvalId: props.toolCall.approvalId,
      worktreePath: resolvedWorktreePath.value,
      branch: resolvedBranch.value,
    }, { title: t('uie.cardTitle.approval') })
    return
  }
  if (dockInfo.value.kind === 'worktree') {
    focusDockCard({
      card: 'git',
      reason: 'worktree',
      runId: resolvedRunId.value,
      taskId: props.workerTaskId ?? dockInfo.value.taskKey,
      workerHandle: resolvedWorkerHandle.value,
      worktreePath: resolvedWorktreePath.value,
      branch: resolvedBranch.value,
    }, { title: t('uie.cardTitle.git') })
    return
  }
  focusDockCard({
    card: 'orchestration',
    reason: 'dispatch',
    runId: resolvedRunId.value,
    taskId: props.workerTaskId ?? null,
    taskKey: dockInfo.value.taskKey,
    workerHandle: resolvedWorkerHandle.value,
  }, { title: t('uie.cardTitle.orchestration') })
}

function openWorkerDock(): void {
  if (!resolvedWorkerHandle.value && !props.workerTaskId) return
  focusDockCard({
    card: 'agent',
    reason: 'worker',
    runId: resolvedRunId.value,
    taskId: props.workerTaskId ?? null,
    taskKey: dockInfo.value?.taskKey ?? null,
    workerHandle: resolvedWorkerHandle.value,
    worktreePath: resolvedWorktreePath.value,
    branch: resolvedBranch.value,
  }, { title: t('uie.cardTitle.agent') })
}
</script>

<template>
  <article class="tool-call-card" :class="`tool-${statusConfig.key}`">
    <button
      class="tool-call-head"
      type="button"
      :aria-expanded="expanded"
      :aria-controls="detailsId"
      :aria-label="t(expanded ? 'agent.collapseTool' : 'agent.expandTool', { tool: toolCall.toolName })"
      @click="expanded = !expanded"
    >
      <ChevronRight :size="13" class="tool-call-toggle" :class="{ expanded }" />
      <component :is="toolIcon" :size="13" class="tool-status-glyph" />
      <strong :class="{ 'ui-shimmer-text': toolCall.status === 'running' }">{{ toolCall.toolName }}</strong>
      <span v-if="isRisky" class="tool-call-risk-tag">{{ t('agent.highRisk') }}</span>
      <span class="tool-call-args" :class="{ 'ui-shimmer-text': toolCall.status === 'running' && Boolean(toolCall.argsSummary) }">{{ toolCall.argsSummary }}</span>
      <span v-if="durationLabel" class="tool-call-duration">{{ durationLabel }}</span>
    </button>

    <MotionTransition name="ui-collapse">
      <div v-if="expanded" class="tool-call-collapse">
        <div class="tool-call-collapse-inner">
          <div :id="detailsId" class="tool-call-details">
            <pre v-if="toolCall.argsSummary">{{ toolCall.argsSummary }}</pre>
            <TerminalCallBlock
              v-if="isShellCall"
              :execution-id="toolCall.id"
              :command="toolCall.argsSummary"
              :run-id="runId ?? toolCall.runId"
              :status="toolCall.status"
            />
            <p v-if="toolCall.resultSummary && toolCall.resultSummary !== toolCall.argsSummary">{{ toolCall.resultSummary }}</p>
            <ul v-if="toolCall.evidence.length">
              <li v-for="(item, idx) in toolCall.evidence" :key="idx">{{ item }}</li>
            </ul>
          </div>
        </div>
      </div>
    </MotionTransition>

    <!-- Compact inline Dock sync card for dispatch / worktree / approval. -->
    <div
      v-if="dockInfo"
      class="tool-call-dock-card"
      :class="`is-${dockInfo.kind}`"
      data-testid="tool-call-dock-card"
    >
      <button
        type="button"
        class="tool-call-dock-main"
        data-testid="tool-call-dock-jump"
        @click="openDockFromCard"
      >
        <Send v-if="dockInfo.kind === 'dispatch'" :size="12" aria-hidden="true" />
        <GitBranch v-else-if="dockInfo.kind === 'worktree'" :size="12" aria-hidden="true" />
        <ShieldAlert v-else :size="12" aria-hidden="true" />
        <span class="tool-call-dock-kind">
          {{ dockInfo.kind === 'dispatch'
            ? t('worktreeDock.subtaskDispatched')
            : dockInfo.kind === 'worktree'
              ? t('worktreeDock.worktreeSwitched')
              : t('worktreeDock.pendingApproval') }}
        </span>
        <span v-if="resolvedBranch" class="tool-call-dock-meta">{{ resolvedBranch }}</span>
        <span v-else-if="dockInfo.taskKey" class="tool-call-dock-meta">{{ dockInfo.taskKey }}</span>
        <span class="tool-call-dock-cta">
          {{ dockInfo.kind === 'dispatch'
            ? t('worktreeDock.focusOrchestrationDock')
            : dockInfo.kind === 'worktree'
              ? t('worktreeDock.focusGitDock')
              : t('worktreeDock.focusApprovalDock') }}
        </span>
      </button>
      <button
        v-if="resolvedWorkerHandle || workerTaskId"
        type="button"
        class="tool-call-worker-badge"
        data-testid="tool-call-worker-badge"
        @click.stop="openWorkerDock"
      >
        <Bot :size="11" aria-hidden="true" />
        <span>{{ resolvedWorkerHandle ?? workerTaskId }}</span>
      </button>
    </div>

    <!-- Folding output must never hide a pending decision. -->
    <div v-if="toolCall.status === 'waiting_approval' && toolCall.approvalId" class="tool-call-approval">
      <pre v-if="toolCall.argsSummary">{{ toolCall.argsSummary }}</pre>
      <span>{{ t('agent.toolApprovalRequired') }}</span>
      <!-- A delegated approval shows its gates; the person's buttons stay and always win. -->
      <ApprovalGateStatus :approval-id="toolCall.approvalId" />
      <div class="tool-call-approval-actions">
        <button class="tool-call-approve-btn ui-pressable" type="button" @click="emit('approve', toolCall.approvalId!)">
          <CheckCircle2 :size="12" />{{ t('agent.approve') }}
        </button>
        <button class="tool-call-reject-btn ui-pressable" type="button" @click="emit('reject', toolCall.approvalId!)">
          <XCircle :size="12" />{{ t('agent.reject') }}
        </button>
      </div>
    </div>
  </article>
</template>

<style scoped>
.tool-call-card {
  min-width: 0;
}
.tool-waiting {
  margin: 4px 0;
  padding: 6px 8px;
  border: 1px solid color-mix(in srgb, var(--accent-warning) 34%, var(--border-muted));
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent-warning) 6%, var(--surface-section));
}
.tool-call-head {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-width: 0;
  padding: 4px 6px;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  color: var(--text-secondary);
  text-align: left;
  cursor: pointer;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.tool-call-head:hover {
  background: var(--surface-hover, var(--bg-hover));
  color: var(--text-primary);
}
.tool-call-head:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 1px;
}
.tool-call-head strong {
  font-size: 12px;
  font-weight: 500;
  flex-shrink: 0;
  color: var(--text-primary);
}
.tool-call-toggle {
  flex-shrink: 0;
  color: var(--text-muted);
  transition: transform var(--duration-pop, 180ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.tool-call-toggle.expanded {
  transform: rotate(90deg);
}
.tool-status-glyph {
  flex-shrink: 0;
  color: var(--text-muted);
}
.tool-running .tool-status-glyph,
.tool-completed .tool-status-glyph {
  color: var(--text-secondary);
}
.tool-failed .tool-status-glyph {
  color: var(--accent-danger);
}
.tool-waiting .tool-status-glyph,
.tool-call-approval {
  color: var(--accent-warning);
}
.tool-call-args {
  min-width: 0;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
  color: var(--text-chat-muted);
}
.tool-call-duration {
  flex-shrink: 0;
  padding: 1px 6px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  font-family: var(--font-mono, ui-monospace, SFMono-Regular, Menlo, Consolas, monospace);
  font-size: 10px;
  font-variant-numeric: tabular-nums;
  line-height: 1.35;
  color: var(--text-muted);
}
.tool-call-risk-tag {
  flex-shrink: 0;
  padding: 1px 5px;
  border-radius: 4px;
  border: 1px solid color-mix(in srgb, var(--accent-danger) 30%, transparent);
  background: color-mix(in srgb, var(--accent-danger) 8%, transparent);
  font-size: 10px;
  color: var(--accent-danger);
  font-weight: 600;
}
.tool-call-collapse {
  display: grid;
}
.tool-call-collapse-inner {
  min-height: 0;
  overflow: hidden;
}
.tool-call-details {
  margin: 4px 0 6px 24px;
  padding: 8px 10px;
  max-height: 18rem;
  overflow: auto;
  border: 1px solid var(--border-card, var(--border-muted));
  border-radius: 8px;
  background: var(--surface-section);
  font-size: 12px;
  color: var(--text-secondary);
}
.tool-call-details p,
.tool-call-details pre,
.tool-call-approval pre {
  margin: 4px 0;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-family: var(--font-mono, ui-monospace, SFMono-Regular, Menlo, Consolas, monospace);
  font-size: 11.5px;
}
.tool-call-dock-card {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 3px 0 3px 24px;
  padding: 4px 8px;
  border-radius: 7px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-section);
  font-size: 11px;
  flex-wrap: wrap;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    border-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.tool-call-dock-card:hover {
  background: var(--surface-hover);
  border-color: var(--border-default, var(--border-muted));
}
.tool-call-dock-card.is-approval {
  border-color: color-mix(in srgb, var(--accent-warning) 32%, var(--border-card, var(--border-muted)));
  background: color-mix(in srgb, var(--accent-warning) 6%, var(--surface-section));
}
.tool-call-dock-card .tool-call-dock-main > svg {
  color: var(--text-secondary);
  flex-shrink: 0;
}
.tool-call-dock-card.is-approval .tool-call-dock-main > svg {
  color: var(--accent-warning);
}
.tool-call-dock-main {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex: 1;
  min-width: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font: inherit;
  text-align: left;
  cursor: pointer;
}
.tool-call-dock-main:hover .tool-call-dock-cta {
  color: var(--text-primary);
  text-decoration: underline;
  text-underline-offset: 2px;
}
.tool-call-dock-kind {
  font-weight: 500;
  flex-shrink: 0;
}
.tool-call-dock-meta {
  padding: 1px 6px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 10px;
  font-family: var(--font-mono, ui-monospace, SFMono-Regular, Menlo, Consolas, monospace);
  font-variant-numeric: tabular-nums;
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.tool-call-dock-cta {
  margin-left: auto;
  font-size: 10.5px;
  font-weight: 500;
  color: var(--text-secondary);
  white-space: nowrap;
  transition: color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.tool-call-worker-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 7px;
  border: 1px solid var(--border-default, var(--border-muted));
  border-radius: 999px;
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1)),
    color var(--duration-tap, 120ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}
.tool-call-worker-badge:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}
.tool-call-approval {
  margin: 5px 0 2px 24px;
  padding-top: 6px;
  border-top: 1px solid color-mix(in srgb, var(--accent-warning) 28%, var(--border-muted));
  font-size: 12px;
}
.tool-call-approval-actions {
  display: flex;
  gap: 8px;
  margin-top: 6px;
}
.tool-call-approve-btn,
.tool-call-reject-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 9px;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
}
.tool-call-approve-btn {
  color: var(--accent-success);
}
.tool-call-approve-btn:hover {
  background: color-mix(in srgb, var(--accent-success) 10%, var(--surface-raised));
  border-color: color-mix(in srgb, var(--accent-success) 35%, var(--border-muted));
}
.tool-call-reject-btn {
  color: var(--accent-danger);
}
.tool-call-reject-btn:hover {
  background: color-mix(in srgb, var(--accent-danger) 10%, var(--surface-raised));
  border-color: color-mix(in srgb, var(--accent-danger) 35%, var(--border-muted));
}
@media (prefers-reduced-motion: reduce) {
  .tool-call-head,
  .tool-call-toggle,
  .tool-call-dock-card,
  .tool-call-dock-cta,
  .tool-call-worker-badge {
    transition: none;
  }
}
</style>
