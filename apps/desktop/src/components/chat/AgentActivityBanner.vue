<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
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
  GitBranch,
  Send,
  Bot,
} from '@lucide/vue'
import type { AgentActivity, AgentState } from '@/composables/useAgentActivity'
import { useSessionWorktrees } from '@/composables/useSessionWorktrees'
import { homeController } from '@/controllers/HomeController'
import { focusDockCard, type InlineDockCardItem } from '@/lib/homeDockSync'
import AgentStatusIndicator from './AgentStatusIndicator.vue'
import { formatDuration as formatElapsed } from '@/lib/conversation/format'

const props = defineProps<{
  activity: AgentActivity
  agentStates: Record<string, AgentState>
}>()

const { t, locale } = useI18n()
const expanded = ref(false)
const c = homeController

const { isolatedGroups } = useSessionWorktrees({
  projectPath: () => c?.currentProject?.value?.path ?? null,
  sessionId: () => c?.selectedSessionId?.value ?? null,
  runs: () => c?.runs?.value,
  feed: () => c?.conversationFeed,
  orchestration: () => c?.orchestration?.value,
  toolExecutions: () => c?.toolExecutions?.value,
})

const statusConfig = computed(() => {
  switch (props.activity.status) {
    case 'thinking':
      return {
        icon: Brain,
        label: t('activity.banner.thinking'),
        color: 'banner-thinking',
        spin: true,
        pulse: false,
      }
    case 'working':
      return {
        icon: Activity,
        label: t('activity.banner.working'),
        color: 'banner-working',
        spin: false,
        pulse: true,
      }
    case 'waiting_approval':
      return {
        icon: ShieldAlert,
        label: t('activity.banner.waitingApproval'),
        color: 'banner-waiting',
        spin: false,
        pulse: false,
      }
    case 'completed':
      return {
        icon: CheckCircle2,
        label: t('activity.banner.completed'),
        color: 'banner-completed',
        spin: false,
        pulse: false,
      }
    case 'error':
      return {
        icon: AlertCircle,
        label: t('activity.banner.error'),
        color: 'banner-error',
        spin: false,
        pulse: false,
      }
    default:
      return {
        icon: Activity,
        label: t('activity.banner.idle'),
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

const elapsedLabel = computed(() => (elapsedMs.value == null ? null : formatElapsed(elapsedMs.value, t)))

const startedTimeLabel = computed(() => {
  if (!props.activity.runStartedAt) return null
  try {
    return new Date(props.activity.runStartedAt).toLocaleTimeString(locale.value, {
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

const inlineStatusCards = computed<InlineDockCardItem[]>(() => {
  const cards: InlineDockCardItem[] = []
  const pendingApprovalList = (c?.approvals?.value ?? []).filter((a) => a.status === 'pending')
  if (props.activity.status === 'waiting_approval' || pendingApprovalList.length > 0) {
    const first = pendingApprovalList[0]
    cards.push({
      id: `approval:${first?.id ?? props.activity.runId ?? 'wait'}`,
      kind: 'approval',
      card: 'approval',
      title: t('worktreeDock.pendingApproval'),
      subtitle: first?.summary ?? null,
      runId: props.activity.runId,
      approvalId: first?.id ?? null,
      urgent: true,
    })
  }
  for (const wt of isolatedGroups.value.slice(0, 2)) {
    cards.push({
      id: `wt:${wt.id}`,
      kind: 'worktree',
      card: 'git',
      title: t('worktreeDock.worktreeSwitched'),
      badge: wt.branch || null,
      subtitle: t('worktreeDock.diffSummary', {
        added: wt.counts.added,
        modified: wt.counts.modified,
        deleted: wt.counts.deleted,
      }),
      runId: wt.runId,
      taskId: wt.taskId,
      workerHandle: wt.workerHandle,
      workerLabel: wt.workerLabel,
      worktreePath: wt.worktreePath,
      branch: wt.branch,
    })
  }
  for (const agent of executionAgents.value.slice(0, 3)) {
    cards.push({
      id: `exec:${agent.agentId}`,
      kind: 'dispatch',
      card: 'orchestration',
      title: t('worktreeDock.subtaskDispatched'),
      subtitle: agent.currentTask,
      badge: agent.agentName,
      runId: props.activity.runId,
      workerHandle: agent.agentName,
      workerLabel: agent.agentName,
      agentSlug: agent.agentType,
    })
  }
  return cards
})

function openDockFromBanner(item: InlineDockCardItem): void {
  focusDockCard({
    card: item.card,
    reason: item.kind,
    runId: item.runId,
    taskId: item.taskId,
    workerHandle: item.workerHandle,
    workerLabel: item.workerLabel,
    agentSlug: item.agentSlug,
    worktreePath: item.worktreePath,
    branch: item.branch,
    approvalId: item.approvalId,
  }, { title: t(`uie.cardTitle.${item.card}`) })
}

function focusAgentWorker(agent: Pick<AgentState, 'agentId' | 'agentName' | 'agentType'>): void {
  focusDockCard({
    card: 'agent',
    reason: 'worker',
    runId: props.activity.runId,
    workerHandle: agent.agentName,
    workerLabel: agent.agentName,
    agentSlug: agent.agentType,
  }, { title: t('uie.cardTitle.agent') })
}
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
            <strong>{{ activity.activeAgentName ?? t('activity.agentFallback') }}</strong>
            <span v-if="activity.activeAgentRole" class="agent-banner-role">{{ activity.activeAgentRole }}</span>
            <!-- Core's run summary is its English narration of the last transition: diagnostic, so a tooltip. -->
            <span class="agent-banner-status-tag" :title="activity.runSummary ?? undefined">{{ statusConfig.label }}</span>
          </div>
          <div class="agent-banner-meta">
            <span v-if="startedTimeLabel" class="agent-banner-time">
              <Clock :size="11" />
              {{ startedTimeLabel }}
            </span>
            <span v-if="elapsedLabel" class="agent-banner-elapsed">{{ t('activity.banner.elapsed', { time: elapsedLabel }) }}</span>
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

    <div v-if="inlineStatusCards.length > 0" class="agent-banner-dock-sync" data-testid="agent-banner-dock-cards">
      <div
        v-for="item in inlineStatusCards"
        :key="item.id"
        class="agent-banner-dock-card"
        :class="`is-${item.kind}`"
        :data-dock-kind="item.kind"
        data-testid="agent-banner-dock-card"
      >
        <button type="button" class="agent-banner-dock-main" @click="openDockFromBanner(item)">
          <Send v-if="item.kind === 'dispatch'" :size="11" aria-hidden="true" />
          <GitBranch v-else-if="item.kind === 'worktree'" :size="11" aria-hidden="true" />
          <ShieldAlert v-else :size="11" aria-hidden="true" />
          <span class="agent-banner-dock-title">{{ item.title }}</span>
          <span v-if="item.badge" class="agent-banner-dock-badge">{{ item.badge }}</span>
          <span v-else-if="item.subtitle" class="agent-banner-dock-sub">{{ item.subtitle }}</span>
        </button>
        <button
          v-if="item.workerHandle"
          type="button"
          class="agent-banner-worker-badge"
          data-testid="agent-banner-worker-badge"
          @click.stop="focusAgentWorker({ agentId: item.workerHandle, agentName: item.workerHandle, agentType: item.agentSlug ?? '' })"
        >
          <Bot :size="10" aria-hidden="true" />
          <span>{{ item.workerLabel ?? item.workerHandle }}</span>
        </button>
      </div>
    </div>

    <Transition name="banner-expand">
      <div v-if="expanded" class="agent-banner-details">
        <div v-if="planningAgents.length > 0" class="agent-banner-section">
          <span class="agent-banner-section-title">{{ t('activity.banner.planningAgents') }}</span>
          <div class="agent-banner-agent-grid">
            <button
              v-for="agent in planningAgents"
              :key="agent.agentId"
              type="button"
              class="agent-banner-agent-btn"
              @click="focusAgentWorker(agent)"
            >
              <AgentStatusIndicator :agent="agent" />
            </button>
          </div>
        </div>
        <div v-if="executionAgents.length > 0" class="agent-banner-section">
          <span class="agent-banner-section-title">{{ t('activity.banner.executionAgents') }}</span>
          <div class="agent-banner-agent-grid">
            <button
              v-for="agent in executionAgents"
              :key="agent.agentId"
              type="button"
              class="agent-banner-agent-btn"
              @click="focusAgentWorker(agent)"
            >
              <AgentStatusIndicator :agent="agent" />
            </button>
          </div>
        </div>
        <div v-if="planningAgents.length === 0 && executionAgents.length === 0" class="agent-banner-empty">
          <Loader2 :size="14" class="agent-icon-spin" />
          <span>{{ t('activity.banner.waitingAssignment') }}</span>
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

.agent-banner-dock-sync {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
  padding: 0 12px 9px;
}

.agent-banner-dock-card {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: 100%;
  padding: 3px 7px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
  font-size: 11px;
}

.agent-banner-dock-card.is-dispatch {
  border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 6%, var(--bg-primary));
}

.agent-banner-dock-card.is-worktree {
  border-color: color-mix(in srgb, var(--accent-success) 28%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-success) 6%, var(--bg-primary));
}

.agent-banner-dock-card.is-approval {
  border-color: color-mix(in srgb, var(--accent-warning) 34%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning) 9%, var(--bg-primary));
}

.agent-banner-dock-card.is-dispatch .agent-banner-dock-main > svg {
  color: var(--accent-primary);
}

.agent-banner-dock-card.is-worktree .agent-banner-dock-main > svg {
  color: var(--accent-success);
}

.agent-banner-dock-card.is-approval .agent-banner-dock-main > svg {
  color: var(--accent-warning);
}

.agent-banner-dock-main {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font: inherit;
  cursor: pointer;
}

.agent-banner-dock-main:hover {
  color: var(--accent-primary);
}

.agent-banner-dock-title {
  font-weight: 600;
  white-space: nowrap;
}

.agent-banner-dock-badge {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--bg-tertiary);
  color: var(--text-secondary);
  font-size: 10px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  max-width: 130px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-banner-dock-sub {
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 10px;
  color: var(--text-muted);
}

.agent-banner-worker-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 6px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 35%, transparent);
  border-radius: 999px;
  background: color-mix(in srgb, var(--accent-primary) 10%, transparent);
  color: var(--accent-primary);
  font-size: 10px;
  font-weight: 600;
  cursor: pointer;
}

.agent-banner-agent-btn {
  display: block;
  width: 100%;
  padding: 0;
  border: 0;
  background: transparent;
  text-align: left;
  cursor: pointer;
}
</style>
