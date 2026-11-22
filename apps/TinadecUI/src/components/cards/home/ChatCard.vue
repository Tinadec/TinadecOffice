<script setup lang="ts">
import ChatPanel from '@/components/ChatPanel.vue'
import LiveDigestHeartbeatStrip from '@/components/liveDigest/LiveDigestHeartbeatStrip.vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, GitBranch, Send, ShieldAlert } from '@lucide/vue'
import { api } from '@/api'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { selectPrimaryRunId } from '@/composables/useLiveDigest'
import { useSessionWorktrees } from '@/composables/useSessionWorktrees'
import { homeController } from '@/controllers/HomeController'
import { focusDockCard, type InlineDockCardItem } from '@/lib/homeDockSync'
import { selectionIdentity } from '@/lib/storageScope'

const c = homeController
const { t } = useI18n()
const resolveAgentName = typeof api?.listAgents === 'function'
  ? useAgentDirectory().nameFor
  : ((slugOrHandle: string | null | undefined) => slugOrHandle ?? '')

const { isolatedGroups } = useSessionWorktrees({
  projectPath: () => c.currentProject.value?.path ?? null,
  sessionId: () => c.selectedSessionId.value ?? null,
  runs: () => c.runs.value,
  feed: () => c.conversationFeed,
  orchestration: () => c.orchestration.value,
  toolExecutions: () => c.toolExecutions.value,
  resolveAgentName,
})

const streamDockCards = computed<InlineDockCardItem[]>(() => {
  if (!c.selectedSessionId.value) return []
  const cards: InlineDockCardItem[] = []
  for (const approval of c.approvals.value) {
    if (approval.status !== 'pending') continue
    cards.push({
      id: `approval:${approval.id}`,
      kind: 'approval',
      card: 'approval',
      title: t('worktreeDock.pendingApproval'),
      subtitle: approval.summary,
      approvalId: approval.id,
      runId: approval.run_id ?? null,
      worktreePath: approval.cwd ?? null,
      urgent: true,
    })
  }

  for (const wt of isolatedGroups.value) {
    if (!wt.taskId && !wt.workerHandle && !wt.runId) continue
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
      taskKey: wt.taskKey,
      workerHandle: wt.workerHandle,
      workerLabel: wt.workerLabel,
      agentSlug: wt.agentSlug,
      worktreePath: wt.worktreePath,
      branch: wt.branch,
    })
  }

  void c.conversationFeed.revision.value
  const recentRuns = [...c.runs.value].slice(-2).reverse()
  for (const run of recentRuns) {
    const runState = c.conversationFeed.state.value.runs.get(run.id)
    if (!runState) continue
    for (const sub of runState.tasks.values()) {
      if (sub.isMain) continue
      const label = resolveAgentName(sub.handle ?? sub.agentSlug) || sub.taskKey || sub.taskId
      cards.push({
        id: `dispatch:${run.id}:${sub.taskId}`,
        kind: 'dispatch',
        card: 'orchestration',
        title: t('worktreeDock.subtaskDispatched'),
        subtitle: sub.title,
        badge: sub.status,
        runId: run.id,
        taskId: sub.taskId,
        taskKey: sub.taskKey,
        workerHandle: sub.handle,
        workerLabel: label,
        agentSlug: sub.agentSlug,
      })
    }
  }

  return cards.slice(0, 6)
})

function openStreamDockCard(item: InlineDockCardItem): void {
  focusDockCard({
    card: item.card,
    reason: item.kind,
    runId: item.runId,
    taskId: item.taskId,
    taskKey: item.taskKey,
    workerHandle: item.workerHandle,
    workerLabel: item.workerLabel,
    agentSlug: item.agentSlug,
    worktreePath: item.worktreePath,
    branch: item.branch,
    approvalId: item.approvalId,
  }, { title: t(`uie.cardTitle.${item.card}`) })
}

function openStreamWorkerBadge(item: InlineDockCardItem): void {
  focusDockCard({
    card: 'agent',
    reason: 'worker',
    runId: item.runId,
    taskId: item.taskId,
    taskKey: item.taskKey,
    workerHandle: item.workerHandle,
    workerLabel: item.workerLabel,
    agentSlug: item.agentSlug,
    worktreePath: item.worktreePath,
    branch: item.branch,
  }, { title: t('uie.cardTitle.agent') })
}

// `busy` means "the send is still on its way", not "a run is working". A run's progress is the
// transcript's job (live work rows, answer drafting); folding `working` in here made the send
// button spin and announce "sending" for the entire run, so the moment Core accepted the
// message was indistinguishable from the moment the answer arrived. Stopping a run reads
// `canStop`, which spans the run as it should.
//
// Supervision escalations reach the flat feed through its own reducer (they are run state
// there), so this card no longer rebuilds them from the activity store. That map only ever
// held runs the activity composable had last selected, which is why an escalation on any
// other run had no visible gate. Space mode still reads the composable directly.
const conversation = computed(() => ({
  feed: c.conversationFeed,
  runs: c.runs.value,
  streamingReplies: c.streamingReplies.value,
  approvals: c.approvals.value,
  workspaceRoot: c.currentProject.value?.path ?? null,
}))

const primaryLiveDigestRunId = computed<string | null>(() => {
  if (!c.selectedSessionId.value) return null
  return c.stoppableRunId?.value ?? selectPrimaryRunId(c.runs.value)
})

const liveDigestStorageId = computed<string | undefined>(() => {
  const sessionKey = c.selectedSessionId.value
  if (!sessionKey) return undefined
  return selectionIdentity(sessionKey).storageId
})
</script>

<template>
  <div class="chat-card-shell">
    <aside
      v-if="streamDockCards.length > 0"
      class="chat-dock-sync-bar"
      data-testid="chat-stream-dock-bar"
    >
      <div
        v-for="item in streamDockCards"
        :key="item.id"
        class="chat-dock-card"
        :class="[`is-${item.kind}`, { 'is-urgent': item.urgent }]"
        :data-dock-kind="item.kind"
        data-testid="chat-stream-dock-card"
      >
        <button type="button" class="chat-dock-card-main" @click="openStreamDockCard(item)">
          <Send v-if="item.kind === 'dispatch'" :size="11" aria-hidden="true" />
          <GitBranch v-else-if="item.kind === 'worktree'" :size="11" aria-hidden="true" />
          <ShieldAlert v-else :size="11" aria-hidden="true" />
          <span class="chat-dock-card-title">{{ item.title }}</span>
          <span v-if="item.badge" class="chat-dock-card-badge">{{ item.badge }}</span>
          <span v-if="item.subtitle" class="chat-dock-card-sub">{{ item.subtitle }}</span>
        </button>
        <button
          v-if="item.workerLabel || item.workerHandle || item.taskId"
          type="button"
          class="chat-dock-worker-badge"
          data-testid="chat-stream-worker-badge"
          @click.stop="openStreamWorkerBadge(item)"
        >
          <Bot :size="10" aria-hidden="true" />
          <span>{{ item.workerLabel ?? item.workerHandle ?? item.taskId }}</span>
        </button>
      </div>
    </aside>
    <div
      v-if="primaryLiveDigestRunId"
      class="chat-live-digest-strip"
      :class="{ 'has-dock-cards': streamDockCards.length > 0 }"
    >
      <LiveDigestHeartbeatStrip
        :run-id="primaryLiveDigestRunId"
        :storage-id="liveDigestStorageId"
      />
    </div>
    <ChatPanel
      class="chat-card-panel"
      :messages="c.messages.value"
      :sessions="c.sessions.value"
      :projects="c.projects.value"
      :current-session="c.currentSession.value"
      :current-project="c.currentProject.value"
      :selected-project-id="c.selectedProjectId.value"
      :model-name="c.modelName.value"
      :orchestration="c.orchestration.value"
      :busy="c.busy.value"
      :draft="c.draft.value"
      :permission="c.currentPermission.value"
      :composer-settings="c.composerSettings.value"
      :settings-saving="c.settingsSaving.value"
      :settings-error="c.settingsError.value"
      :runs-for-composer="c.runs.value"
      :can-stop="Boolean(c.stoppableRunId.value)"
      :conversation="conversation"
      :loading="c.transcriptLoading.value"
      @update:draft="c.updateDraft($event)"
      @update:permission="c.updatePermission($event)"
      @update:settings="c.updateComposerSettings($event)"
      @send="c.sendMessage($event)"
      @welcome-send="c.handleWelcomeSend($event)"
      @create-project="c.openProject()"
      @select-project="c.setSelectedProject($event)"
      @edit-message="c.editAndResend($event)"
      @stop="c.stopRun()"
    />
  </div>
</template>

<style scoped>
.chat-card-shell {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-width: 0;
  max-width: 100%;
  min-height: 0;
  overflow: hidden;
  box-sizing: border-box;
}

.chat-card-panel {
  flex: 1;
  min-width: 0;
  max-width: 100%;
  min-height: 0;
}

.chat-dock-sync-bar {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 40px 12px 4px;
  min-width: 0;
  max-width: 100%;
  box-sizing: border-box;
  max-height: 88px;
  overflow-y: auto;
  flex-shrink: 0;
}

.chat-live-digest-strip {
  padding: 40px 12px 4px;
  min-width: 0;
  max-width: 100%;
  box-sizing: border-box;
  flex-shrink: 0;
}

.chat-live-digest-strip.has-dock-cards {
  padding-top: 2px;
}

.chat-dock-card {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  max-width: 100%;
  box-sizing: border-box;
  padding: 3px 8px;
  border-radius: 8px;
  border: 1px solid var(--border-card);
  background: var(--surface-section);
  font-size: 11px;
}

.chat-dock-card.is-dispatch {
  border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-card));
  background: color-mix(in srgb, var(--accent-primary) 6%, var(--surface-section));
}

.chat-dock-card.is-worktree {
  border-color: color-mix(in srgb, var(--accent-success) 28%, var(--border-card));
  background: color-mix(in srgb, var(--accent-success) 6%, var(--surface-section));
}

.chat-dock-card.is-approval {
  border-color: color-mix(in srgb, var(--accent-warning) 34%, var(--border-card));
  background: color-mix(in srgb, var(--accent-warning) 9%, var(--surface-section));
}

.chat-dock-card.is-dispatch .chat-dock-card-main > svg {
  color: var(--accent-primary);
}

.chat-dock-card.is-worktree .chat-dock-card-main > svg {
  color: var(--accent-success);
}

.chat-dock-card.is-approval .chat-dock-card-main > svg {
  color: var(--accent-warning);
}

.chat-dock-card-main {
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

.chat-dock-card-main:hover {
  color: var(--accent-primary);
}

.chat-dock-card-title {
  font-weight: 600;
  white-space: nowrap;
}

.chat-dock-card-badge {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 10px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  max-width: 130px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.chat-dock-card-sub {
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 10px;
  color: var(--text-muted);
}

.chat-dock-worker-badge {
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

.chat-dock-worker-badge:hover {
  background: color-mix(in srgb, var(--accent-primary) 18%, transparent);
}
</style>
