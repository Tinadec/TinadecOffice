<script setup lang="ts">
import { computed, ref } from 'vue'
import { Bot, GitBranch, ListChecks, Send, ShieldAlert } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import ThinkingProcess from './ThinkingProcess.vue'
import ToolCallCard from './ToolCallCard.vue'
import SupervisionGate from '../conversation/SupervisionGate.vue'
import type { TurnActivity } from '@/composables/useAgentActivity'
import {
  focusDockCard,
  parseToolDockInfo,
  type InlineDockCardItem,
} from '@/lib/homeDockSync'
import { turnTimeline } from '@/lib/turnTimeline'

const props = defineProps<TurnActivity>()
const emit = defineEmits<{ approve: [id: string]; reject: [id: string] }>()
const { t } = useI18n()
const items = computed(() => turnTimeline(props.thinkingSteps ?? [], props.toolCalls ?? []))
const expanded = ref(false)
const pendingCalls = computed(() => (props.toolCalls ?? []).filter((call) => call.status === 'waiting_approval'))
const detailItems = computed(() => items.value.filter((item) => item.kind !== 'tool' || item.call.status !== 'waiting_approval'))
const isWorking = computed(() => (props.thinkingSteps ?? []).some((step) => step.status === 'running')
  || (props.toolCalls ?? []).some((call) => call.status === 'running' || call.status === 'pending'))
const activityState = computed(() => props.supervisionReview?.options.length
  ? t('agent.activityWaitingApproval')
  : pendingCalls.value.length
    ? t('agent.activityWaitingApproval')
    : (props.toolCalls ?? []).some((call) => call.status === 'running')
      ? t('agent.activityUsingTools')
      : isWorking.value ? t('agent.activityThinking') : '')

const dockCards = computed<InlineDockCardItem[]>(() => {
  const out: InlineDockCardItem[] = []
  const seen = new Set<string>()
  const pushUnique = (card: InlineDockCardItem) => {
    if (seen.has(card.id)) return
    seen.add(card.id)
    out.push(card)
  }

  for (const step of props.thinkingSteps ?? []) {
    if (step.type !== 'dispatch' && step.type !== 'agent_assignment') continue
    const details = step.details ?? {}
    const taskId = typeof details.task_id === 'string' ? details.task_id
      : typeof details.dispatch_id === 'string' ? details.dispatch_id : null
    const taskKey = typeof details.task_key === 'string' ? details.task_key : null
    const workerHandle = typeof details.handle === 'string' ? details.handle
      : typeof details.agent === 'string' ? details.agent
        : typeof details.agent_slug === 'string' ? details.agent_slug : null
    pushUnique({
      id: `step:${step.id}`,
      kind: 'dispatch',
      card: 'orchestration',
      title: step.title,
      subtitle: step.description || null,
      runId: props.runId ?? null,
      taskId,
      taskKey,
      workerHandle,
      workerLabel: workerHandle ?? step.title,
    })
  }

  for (const call of props.toolCalls ?? []) {
    const info = parseToolDockInfo(call)
    if (!info) continue
    const card = info.kind === 'approval' ? 'approval' : info.kind === 'worktree' ? 'git' : 'orchestration'
    pushUnique({
      id: `tool:${call.id}`,
      kind: info.kind,
      card,
      title: info.kind === 'dispatch'
        ? t('worktreeDock.subtaskDispatched')
        : info.kind === 'worktree'
          ? t('worktreeDock.worktreeSwitched')
          : t('worktreeDock.pendingApproval'),
      subtitle: call.argsSummary || call.toolName,
      badge: info.branch ?? info.taskKey ?? null,
      runId: props.runId ?? call.runId ?? null,
      taskId: null,
      taskKey: info.taskKey,
      workerHandle: info.workerHandle,
      worktreePath: info.worktreePath,
      branch: info.branch,
      approvalId: call.approvalId,
      urgent: info.kind === 'approval',
    })
  }

  if (props.supervisionReview?.options.length) {
    pushUnique({
      id: `supervision:${props.supervisionReview.runId}`,
      kind: 'approval',
      card: 'approval',
      title: t('worktreeDock.pendingApproval'),
      subtitle: props.supervisionReview.reasons[0] ?? null,
      runId: props.supervisionReview.runId,
      urgent: true,
    })
  }

  return out
})

function openInlineCard(item: InlineDockCardItem): void {
  focusDockCard({
    card: item.card,
    reason: item.kind,
    runId: item.runId,
    taskId: item.taskId,
    taskKey: item.taskKey,
    workerHandle: item.workerHandle,
    workerLabel: item.workerLabel,
    worktreePath: item.worktreePath,
    branch: item.branch,
    approvalId: item.approvalId,
  }, { title: t(`uie.cardTitle.${item.card}`) })
}

function openWorkerFromCard(item: InlineDockCardItem): void {
  focusDockCard({
    card: 'agent',
    reason: 'worker',
    runId: item.runId,
    taskId: item.taskId,
    taskKey: item.taskKey,
    workerHandle: item.workerHandle,
    workerLabel: item.workerLabel,
    worktreePath: item.worktreePath,
    branch: item.branch,
  }, { title: t('uie.cardTitle.agent') })
}
</script>

<template>
  <div class="turn-timeline">
    <div v-if="dockCards.length > 0" class="turn-dock-cards" data-testid="turn-timeline-dock-cards">
      <div
        v-for="item in dockCards"
        :key="item.id"
        class="turn-dock-card"
        :class="[`is-${item.kind}`, { 'is-urgent': item.urgent }]"
        :data-dock-kind="item.kind"
        data-testid="turn-dock-card"
      >
        <button type="button" class="turn-dock-card-main" @click="openInlineCard(item)">
          <Send v-if="item.kind === 'dispatch'" :size="12" aria-hidden="true" />
          <GitBranch v-else-if="item.kind === 'worktree'" :size="12" aria-hidden="true" />
          <ShieldAlert v-else :size="12" aria-hidden="true" />
          <span class="turn-dock-card-title">{{ item.title }}</span>
          <span v-if="item.badge" class="turn-dock-card-badge">{{ item.badge }}</span>
          <span v-else-if="item.subtitle" class="turn-dock-card-sub">{{ item.subtitle }}</span>
        </button>
        <button
          v-if="item.workerHandle || item.taskId"
          type="button"
          class="turn-dock-worker-badge"
          data-testid="turn-dock-worker-badge"
          @click.stop="openWorkerFromCard(item)"
        >
          <Bot :size="10" aria-hidden="true" />
          <span>{{ item.workerHandle ?? item.workerLabel ?? item.taskId }}</span>
        </button>
      </div>
    </div>
    <div v-if="items.length" class="activity-summary" data-testid="activity-summary">
      <button class="activity-summary-trigger" type="button" :aria-expanded="expanded" data-testid="activity-toggle" @click="expanded = !expanded">
        <ListChecks :size="13" class="activity-summary-icon" aria-hidden="true" />
        <span class="activity-summary-chevron" :class="{ expanded }" aria-hidden="true">›</span>
        <span class="activity-summary-label">{{ t('agent.activity') }}</span>
        <span class="activity-summary-count">{{ t('agent.activityCount', { count: items.length }) }}</span>
        <span v-if="activityState" class="activity-summary-state">{{ activityState }}</span>
      </button>
      <div v-if="expanded" class="activity-details" data-testid="activity-details">
        <template v-for="item in detailItems" :key="item.id">
          <ThinkingProcess v-if="item.kind === 'thinking'" :steps="item.steps" />
          <ToolCallCard v-else :tool-call="item.call" :run-id="runId"
            @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
        </template>
      </div>
    </div>
    <template v-for="call in pendingCalls" :key="`attention-${call.id}`">
      <ToolCallCard :tool-call="call" :run-id="runId"
        @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
    </template>
    <SupervisionGate :review="supervisionReview" />
  </div>
</template>

<style scoped>
.turn-timeline { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
.turn-dock-cards { display: flex; flex-wrap: wrap; gap: 5px; }
.turn-dock-card { display: inline-flex; align-items: center; gap: 5px; max-width: 100%; padding: 3px 7px; border-radius: 7px; border: 1px solid var(--border-card); background: var(--surface-section); font-size: 11px; }
.turn-dock-card.is-dispatch { border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-card)); background: color-mix(in srgb, var(--accent-primary) 6%, var(--surface-section)); }
.turn-dock-card.is-worktree { border-color: color-mix(in srgb, var(--accent-success) 28%, var(--border-card)); background: color-mix(in srgb, var(--accent-success) 6%, var(--surface-section)); }
.turn-dock-card.is-approval { border-color: color-mix(in srgb, var(--accent-warning) 34%, var(--border-card)); background: color-mix(in srgb, var(--accent-warning) 9%, var(--surface-section)); }
.turn-dock-card.is-dispatch .turn-dock-card-main > svg { color: var(--accent-primary); }
.turn-dock-card.is-worktree .turn-dock-card-main > svg { color: var(--accent-success); }
.turn-dock-card.is-approval .turn-dock-card-main > svg { color: var(--accent-warning); }
.turn-dock-card-main { display: inline-flex; align-items: center; gap: 5px; min-width: 0; padding: 0; border: 0; background: transparent; color: var(--text-primary); font: inherit; cursor: pointer; }
.turn-dock-card-main:hover { color: var(--accent-primary); }
.turn-dock-card-title { font-weight: 600; white-space: nowrap; }
.turn-dock-card-badge { padding: 1px 5px; border-radius: 4px; background: var(--surface-raised); font-size: 10px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; color: var(--text-secondary); max-width: 130px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.turn-dock-card-sub { max-width: 160px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 10.5px; color: var(--text-muted); }
.turn-dock-worker-badge { display: inline-flex; align-items: center; gap: 3px; padding: 1px 6px; border: 1px solid color-mix(in srgb, var(--accent-primary) 35%, transparent); border-radius: 999px; background: color-mix(in srgb, var(--accent-primary) 10%, transparent); color: var(--accent-primary); font-size: 10px; font-weight: 600; cursor: pointer; }
.turn-dock-worker-badge:hover { background: color-mix(in srgb, var(--accent-primary) 18%, transparent); }
.activity-summary { min-width: 0; overflow: hidden; }
.activity-summary-trigger { display: flex; align-items: center; gap: 6px; width: 100%; min-height: 28px; padding: 4px 2px; border: 0; border-radius: 6px; color: var(--text-secondary); background: transparent; font-size: 11px; text-align: left; cursor: pointer; }
.activity-summary-trigger:hover, .activity-summary-trigger:focus-visible { color: var(--text-primary); background: var(--surface-hover); }
.activity-summary-trigger:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 1px; }
.activity-summary-chevron { display: inline-block; width: 12px; color: var(--text-muted); font-size: 18px; line-height: 12px; transition: transform .15s ease; }
.activity-summary-chevron.expanded { transform: rotate(90deg); }
.activity-summary-icon { flex: 0 0 auto; color: var(--accent-primary); }
.activity-summary-label { font-weight: 500; }
.activity-summary-count { color: var(--text-muted); font-variant-numeric: tabular-nums; }
.activity-summary-state { min-width: 0; margin-left: auto; overflow: hidden; color: var(--text-muted); font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }
.activity-details { position: relative; display: flex; flex-direction: column; gap: 1px; margin-left: 8px; padding: 3px 0 5px 14px; }
.activity-details::before { position: absolute; top: 0; bottom: 5px; left: 3px; width: 1px; background: color-mix(in srgb, var(--border-muted) 72%, transparent); content: ''; }
@media (prefers-reduced-motion: reduce) { .activity-summary-chevron { transition: none; } }
</style>
