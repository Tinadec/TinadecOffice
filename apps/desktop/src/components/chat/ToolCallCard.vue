<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { CheckCircle2, XCircle, Loader2, Clock, ShieldAlert, ChevronRight, GitBranch, Send, Bot } from '@lucide/vue'
import { toolIconOf } from '@/lib/toolPresentation'
import { UiCollapsible } from '@/components/ui'
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
const toolIcon = computed(() => {
  return toolIconOf(props.toolCall.toolId)
})

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
    <UiCollapsible v-model:open="expanded">
      <template #trigger>
        <button class="tool-call-head" type="button" :aria-expanded="expanded" :aria-controls="detailsId"
          :aria-label="t(expanded ? 'agent.collapseTool' : 'agent.expandTool', { tool: toolCall.toolName })">
          <ChevronRight :size="13" class="tool-call-toggle" :class="{ expanded }" />
          <component :is="toolIcon" :size="13" class="tool-status-glyph"
            :class="{ 'tool-icon-spin': toolCall.status === 'running' }" />
          <strong>{{ toolCall.toolName }}</strong>
          <span v-if="isRisky" class="tool-call-risk-tag">{{ t('agent.highRisk') }}</span>
          <span class="tool-call-args">{{ toolCall.argsSummary }}</span>
          <span v-if="durationLabel" class="tool-call-duration">{{ durationLabel }}</span>
        </button>
      </template>
      <div :id="detailsId" class="tool-call-details">
        <pre v-if="toolCall.argsSummary">{{ toolCall.argsSummary }}</pre>
        <TerminalCallBlock v-if="isShellCall" :execution-id="toolCall.id" :command="toolCall.argsSummary"
          :run-id="runId ?? toolCall.runId" :status="toolCall.status" />
        <p v-if="toolCall.resultSummary && toolCall.resultSummary !== toolCall.argsSummary">{{ toolCall.resultSummary }}</p>
        <ul v-if="toolCall.evidence.length">
          <li v-for="(item, idx) in toolCall.evidence" :key="idx">{{ item }}</li>
        </ul>
      </div>
    </UiCollapsible>
    <!-- Compact high-visibility inline Dock sync card for dispatch / worktree / approval. -->
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
        <button class="tool-call-approve-btn" type="button" @click="emit('approve', toolCall.approvalId!)">
          <CheckCircle2 :size="12" />{{ t('agent.approve') }}
        </button>
        <button class="tool-call-reject-btn" type="button" @click="emit('reject', toolCall.approvalId!)">
          <XCircle :size="12" />{{ t('agent.reject') }}
        </button>
      </div>
    </div>
  </article>
</template>

<style scoped>
.tool-call-card { min-width: 0; }
.tool-waiting { margin: 3px 0; padding: 6px 8px; border: 1px solid color-mix(in srgb, var(--accent-warning) 40%, var(--border-muted)); border-radius: 8px; background: color-mix(in srgb, var(--accent-warning) 7%, var(--surface-raised)); }
.tool-call-head { display: flex; align-items: center; gap: 6px; width: 100%; min-width: 0; padding: 4px; background: transparent; border: none; border-radius: 4px; color: var(--text-secondary); text-align: left; cursor: pointer; }
.tool-call-head:hover { background: var(--bg-hover); }
.tool-call-head:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 1px; }
.tool-call-head strong { font-size: 12px; font-weight: 500; flex-shrink: 0; }
.tool-call-toggle, .tool-status-glyph { flex-shrink: 0; color: var(--text-muted); }
.tool-call-toggle.expanded { transform: rotate(90deg); }
.tool-running .tool-status-glyph { color: var(--accent-primary); }
.tool-completed .tool-status-glyph { color: var(--accent-success); }
.tool-failed .tool-status-glyph { color: var(--accent-danger); }
.tool-waiting .tool-status-glyph, .tool-call-approval { color: var(--accent-warning); }
.tool-call-args { min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11px; color: var(--text-chat-muted); }
.tool-call-duration { flex-shrink: 0; font-size: 10px; color: var(--text-muted); }
.tool-call-risk-tag { flex-shrink: 0; font-size: 10px; color: var(--accent-danger); font-weight: 600; }
.tool-call-details { margin: 4px 0 6px 25px; max-height: 18rem; overflow: auto; font-size: 12px; color: var(--text-secondary); }
.tool-call-details p, pre { margin: 4px 0; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12px; }
.tool-call-dock-card { display: flex; align-items: center; gap: 6px; margin: 3px 0 3px 25px; padding: 4px 8px; border-radius: 7px; border: 1px solid var(--border-card); background: var(--surface-section); font-size: 11px; flex-wrap: wrap; }
.tool-call-dock-card.is-dispatch { border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-card)); background: color-mix(in srgb, var(--accent-primary) 6%, var(--surface-section)); }
.tool-call-dock-card.is-worktree { border-color: color-mix(in srgb, var(--accent-success) 28%, var(--border-card)); background: color-mix(in srgb, var(--accent-success) 6%, var(--surface-section)); }
.tool-call-dock-card.is-approval { border-color: color-mix(in srgb, var(--accent-warning) 34%, var(--border-card)); background: color-mix(in srgb, var(--accent-warning) 8%, var(--surface-section)); }
.tool-call-dock-card.is-dispatch .tool-call-dock-main > svg { color: var(--accent-primary); }
.tool-call-dock-card.is-worktree .tool-call-dock-main > svg { color: var(--accent-success); }
.tool-call-dock-card.is-approval .tool-call-dock-main > svg { color: var(--accent-warning); }
.tool-call-dock-main { display: inline-flex; align-items: center; gap: 5px; flex: 1; min-width: 0; padding: 0; border: 0; background: transparent; color: var(--text-primary); font: inherit; text-align: left; cursor: pointer; }
.tool-call-dock-main:hover .tool-call-dock-cta { text-decoration: underline; }
.tool-call-dock-kind { font-weight: 600; flex-shrink: 0; }
.tool-call-dock-meta { padding: 1px 5px; border-radius: 4px; background: var(--surface-raised); color: var(--text-secondary); font-size: 10px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; max-width: 150px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tool-call-dock-cta { margin-left: auto; font-size: 10px; font-weight: 600; color: var(--accent-primary); white-space: nowrap; }
.tool-call-worker-badge { display: inline-flex; align-items: center; gap: 3px; padding: 2px 6px; border: 1px solid color-mix(in srgb, var(--accent-primary) 35%, transparent); border-radius: 999px; background: color-mix(in srgb, var(--accent-primary) 10%, transparent); color: var(--accent-primary); font-size: 10px; font-weight: 600; cursor: pointer; }
.tool-call-worker-badge:hover { background: color-mix(in srgb, var(--accent-primary) 18%, transparent); }
.tool-call-approval { margin: 5px 0 2px 25px; padding-top: 6px; border-top: 1px solid color-mix(in srgb, var(--accent-warning) 28%, var(--border-muted)); font-size: 12px; }
.tool-call-approval-actions { display: flex; gap: 8px; margin-top: 4px; }
.tool-call-approve-btn, .tool-call-reject-btn { display: inline-flex; align-items: center; gap: 4px; padding: 4px 8px; background: transparent; border: 1px solid var(--border-muted); border-radius: 4px; cursor: pointer; }
.tool-call-approve-btn { color: var(--accent-success); }
.tool-call-reject-btn { color: var(--accent-danger); }
.tool-icon-spin { animation: tool-spin 1s linear infinite; }
@keyframes tool-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .tool-icon-spin { animation: none; } }
</style>
