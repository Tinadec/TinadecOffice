<script setup lang="ts">
import { Bot, Eye, Filter, GitBranch, Trash2, X } from '@lucide/vue'
import { computed, inject, nextTick, ref, watch, type Ref } from 'vue'
import { useI18n } from 'vue-i18n'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import DiffViewer from '@/components/git/DiffViewer.vue'
import { instanceKeyOf } from '@/lib/avatarAssignment'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import {
  useSessionWorktrees,
  type WorkerWorktreeGroup,
  type WorktreeDiffFile,
} from '@/composables/useSessionWorktrees'
import { homeController } from '@/controllers/HomeController'
import { groupRun, type SubAgentNode } from '@/lib/conversation/grouping'
import { conversationSelection, selectSubAgent } from '@/lib/conversation/selection'
import { isLiveRunStatus, type TurnRunInput } from '@/lib/conversation/turns'
import {
  activeDockFocus,
  clearDockWorkerFilter,
  focusDockCard,
  matchesWorkerTarget,
} from '@/lib/homeDockSync'
import SubAgentCard from './SubAgentCard.vue'

/**
 * The right-column "Sub-agents" card: every sub-agent of the session, working ones first,
 * and the selected one's whole activity. It reads the same feed as the conversation, so the
 * agent opened from the feed is the agent shown here.
 */
const props = defineProps<{
  feed: ConversationFeed
  runs: readonly TurnRunInput[]
  workspaceRoot?: string | null
}>()
const emit = defineEmits<{ 'roster-size': [count: number] }>()
const { t } = useI18n()
const { nameFor } = useAgentDirectory()
const panelRef = ref<HTMLElement | null>(null)

const injectedSessionId = inject<Ref<string | null> | null>('agentCard:sessionId', null)
const sessionId = computed(() => injectedSessionId?.value ?? homeController?.currentSession?.value?.id ?? null)

const { groups: worktreeGroups } = useSessionWorktrees({
  projectPath: () => props.workspaceRoot ?? null,
  sessionId: () => sessionId.value,
  runs: () => props.runs,
  feed: () => props.feed,
  resolveAgentName: nameFor,
})

interface RosterItem { runId: string; node: SubAgentNode; order: number }

const roster = computed<RosterItem[]>(() => {
  void props.feed.revision.value
  const items: RosterItem[] = []
  props.runs.forEach((run, runIndex) => {
    const activity = props.feed.state.value.runs.get(run.id)
    if (!activity) return
    groupRun(activity, isLiveRunStatus(run.status)).agents.forEach((node, index) => {
      items.push({ runId: run.id, node, order: runIndex * 1000 + index })
    })
  })
  const working = (item: RosterItem) => item.node.status === 'running' || item.node.status === 'awaiting_approval'
  return items.sort((a, b) => Number(working(b)) - Number(working(a)) || a.order - b.order)
})
watch(() => roster.value.length, count => emit('roster-size', count), { immediate: true })

const workerFilter = ref<string | null>(null)
const filteredRoster = computed(() => {
  if (!workerFilter.value) return roster.value
  const needle = workerFilter.value.toLowerCase()
  const matched = roster.value.filter((item) =>
    item.node.agent.taskId === workerFilter.value
    || item.node.agent.handle?.toLowerCase() === needle
    || item.node.agent.agentSlug?.toLowerCase() === needle
    || nameFor(item.node.agent.handle ?? item.node.agent.agentSlug).toLowerCase().includes(needle),
  )
  return matched.length ? matched : roster.value
})

const selected = computed(() => {
  const choice = conversationSelection.value
  return roster.value.find((item) => item.runId === choice?.runId && item.node.agent.taskId === choice?.taskId)
    ?? filteredRoster.value[0]
    ?? roster.value[0]
    ?? null
})

function worktreeForItem(item: RosterItem | null): WorkerWorktreeGroup | null {
  if (!item) return null
  const taskId = item.node.agent.taskId
  const handle = item.node.agent.handle
  const isolated = worktreeGroups.value.find((group) =>
    !group.isMain
    && (
      (group.taskId && group.taskId === taskId)
      || (group.workerHandle && handle && group.workerHandle === handle)
    ),
  )
  if (isolated) return isolated
  return worktreeGroups.value.find((group) => group.isMain) ?? null
}

const selectedWorktree = computed(() => worktreeForItem(selected.value))

const selectedLineageLabels = computed<string[]>(() => {
  const item = selected.value
  if (!item) return []
  const wt = selectedWorktree.value
  if (wt && !wt.isMain && wt.lineage.length > 0) {
    return wt.lineage.map((step) => step.label)
  }
  const chain: string[] = []
  if (item.node.agent.dispatchedBy) chain.push(nameFor(item.node.agent.dispatchedBy))
  if (item.node.agent.followUpOf) chain.push(nameFor(item.node.agent.followUpOf))
  chain.push(nameFor(item.node.agent.handle ?? item.node.agent.agentSlug))
  return chain.filter(Boolean)
})

const inlineDiffPreview = ref<{ group: WorkerWorktreeGroup; file: WorktreeDiffFile } | null>(null)
watch(() => selected.value?.node.agent.taskId, () => {
  inlineDiffPreview.value = null
})

function inspectInGitDock(group: WorkerWorktreeGroup, filePath?: string | null): void {
  const item = selected.value
  focusDockCard({
    card: 'git',
    reason: 'worktree',
    runId: item?.runId ?? group.runId,
    taskId: item?.node.agent.taskId ?? group.taskId,
    workerHandle: item?.node.agent.handle ?? group.workerHandle,
    workerLabel: item ? nameFor(item.node.agent.handle ?? item.node.agent.agentSlug) : group.workerLabel,
    worktreePath: group.worktreePath,
    branch: group.branch,
    filePath: filePath ?? null,
  }, { title: t('uie.cardTitle.git') })
}

function requestCleanup(group: WorkerWorktreeGroup): void {
  const item = selected.value
  focusDockCard({
    card: 'git',
    reason: 'worktree',
    runId: item?.runId ?? group.runId,
    taskId: item?.node.agent.taskId ?? group.taskId,
    workerHandle: item?.node.agent.handle ?? group.workerHandle,
    worktreePath: group.worktreePath,
    branch: group.branch,
    requestCleanupPath: group.worktreePath,
  }, { title: t('uie.cardTitle.git') })
}

function choose(item: RosterItem) {
  inlineDiffPreview.value = null
  selectSubAgent({ runId: item.runId, taskId: item.node.agent.taskId })
}

function clearFilter() {
  workerFilter.value = null
  clearDockWorkerFilter()
}

watch(
  () => activeDockFocus.value?.nonce,
  async () => {
    const focus = activeDockFocus.value
    if (!focus || focus.card !== 'agent') return
    const target = focus.workerHandle ?? focus.taskId ?? focus.workerLabel ?? null
    if (target) workerFilter.value = target
    const matched = roster.value.find((item) =>
      matchesWorkerTarget(focus, {
        taskId: item.node.agent.taskId,
        taskKey: item.node.agent.taskKey,
        handle: item.node.agent.handle,
        agentSlug: item.node.agent.agentSlug,
        label: nameFor(item.node.agent.handle ?? item.node.agent.agentSlug),
      }) || (focus.runId && item.runId === focus.runId),
    )
    if (matched) {
      selectSubAgent({ runId: matched.runId, taskId: matched.node.agent.taskId })
    }
    await nextTick()
    const selectedEl = panelRef.value?.querySelector<HTMLElement>('.conversation-sub-agents-item[aria-selected="true"]')
    if (selectedEl && typeof selectedEl.scrollIntoView === 'function') {
      selectedEl.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
    }
  },
  { immediate: true },
)
</script>

<template>
  <div ref="panelRef" class="conversation-sub-agents" data-testid="sub-agents-panel">
    <p v-if="roster.length === 0" class="conversation-sub-agents-empty">
      <Bot class="conversation-glyph" aria-hidden="true" />{{ t('conversation.agents.none') }}
    </p>
    <template v-else>
      <div v-if="workerFilter" class="conversation-sub-agents-filter-bar" data-testid="sub-agents-filter-bar">
        <Filter :size="11" aria-hidden="true" />
        <span class="filter-label">{{ t('worktreeDock.filterWorker', { worker: workerFilter }) }}</span>
        <button type="button" class="filter-clear-btn" @click="clearFilter">{{ t('worktreeDock.clearFilter') }}</button>
      </div>
      <ul class="conversation-sub-agents-roster" role="listbox" :aria-label="t('conversation.agents.panelTitle')">
        <li
          v-for="item in filteredRoster"
          :key="`${item.runId}:${item.node.agent.taskId}`"
          role="option"
          tabindex="0"
          class="conversation-sub-agents-item"
          :aria-selected="selected?.node.agent.taskId === item.node.agent.taskId && selected?.runId === item.runId"
          :data-worker-task-id="item.node.agent.taskId"
          :data-worktree-path="worktreeForItem(item)?.worktreePath ?? undefined"
          data-testid="sub-agents-roster-item"
          @click="choose(item)"
          @keydown.enter.prevent="choose(item)"
          @keydown.space.prevent="choose(item)"
        >
          <span class="conversation-sub-agents-identity">
            <AgentPetAvatar
              :instance-key="instanceKeyOf(item.runId, item.node.agent.taskId)"
              :agent-id="item.node.agent.agentSlug"
              :size="16"
            />
            <span class="conversation-sub-agents-name">{{ nameFor(item.node.agent.handle ?? item.node.agent.agentSlug) }}</span>
            <span
              v-if="worktreeForItem(item)?.branch"
              class="conversation-sub-agent-branch-tag"
              :class="{ 'is-isolated': worktreeForItem(item)?.isMain === false }"
            >
              <GitBranch :size="10" aria-hidden="true" />
              <span>{{ worktreeForItem(item)?.branch }}</span>
            </span>
          </span>
          <span class="conversation-sub-agents-meta">
            <span
              v-if="worktreeForItem(item) && worktreeForItem(item)!.counts.totalFiles > 0"
              class="conversation-sub-agent-diff-pill"
              data-testid="sub-agent-roster-diff-pill"
            >
              {{ t('worktreeDock.diffSummary', {
                added: worktreeForItem(item)!.counts.added,
                modified: worktreeForItem(item)!.counts.modified,
                deleted: worktreeForItem(item)!.counts.deleted,
              }) }}
            </span>
            <span class="conversation-sub-agent-badge" :data-status="item.node.status">{{ t(`conversation.agents.status.${item.node.status}`) }}</span>
          </span>
        </li>
      </ul>

      <section
        v-if="selected && (selectedWorktree || selectedLineageLabels.length > 0)"
        class="conversation-sub-agent-worktree"
        :class="{ 'is-isolated': selectedWorktree && !selectedWorktree.isMain }"
        data-testid="sub-agent-worktree-summary"
      >
        <div class="sub-agent-worktree-top">
          <span
            class="sub-agent-scope-badge"
            :class="selectedWorktree && !selectedWorktree.isMain ? 'is-isolated' : 'is-main'"
            data-testid="sub-agent-worktree-scope"
          >
            {{ selectedWorktree && !selectedWorktree.isMain ? t('worktreeDock.isolatedWorktree') : t('worktreeDock.mainWorkspace') }}
          </span>
          <span v-if="selectedWorktree?.branch" class="sub-agent-branch-pill" data-testid="sub-agent-worktree-branch">
            <GitBranch :size="11" aria-hidden="true" />
            <span>{{ selectedWorktree.branch }}</span>
          </span>
          <span
            v-if="selectedWorktree"
            class="sub-agent-diff-counts"
            data-testid="sub-agent-worktree-diff-counts"
          >
            {{ t('worktreeDock.diffSummary', {
              added: selectedWorktree.counts.added,
              modified: selectedWorktree.counts.modified,
              deleted: selectedWorktree.counts.deleted,
            }) }}
          </span>
        </div>

        <p v-if="selectedLineageLabels.length > 0" class="sub-agent-lineage" data-testid="sub-agent-lineage">
          <span class="sub-agent-lineage-label">{{ t('worktreeDock.lineageLabel') }}:</span>
          <span>{{ selectedLineageLabels.join(' › ') }}</span>
        </p>

        <div v-if="selectedWorktree && selectedWorktree.files.length > 0" class="sub-agent-files" data-testid="sub-agent-worktree-files">
          <button
            v-for="file in selectedWorktree.files.slice(0, 6)"
            :key="file.path"
            type="button"
            class="sub-agent-file-chip"
            data-testid="sub-agent-file-diff-btn"
            @click="inlineDiffPreview = { group: selectedWorktree, file }"
          >
            <span class="sub-agent-file-dot" :data-kind="file.statusBadge">{{ file.statusBadge }}</span>
            <span class="sub-agent-file-path">{{ file.path }}</span>
          </button>
        </div>

        <div v-if="selectedWorktree" class="sub-agent-worktree-actions">
          <button
            type="button"
            class="sub-agent-wt-btn"
            data-testid="sub-agent-inspect-git-btn"
            @click="inspectInGitDock(selectedWorktree)"
          >
            <Eye :size="11" aria-hidden="true" />
            <span>{{ t('worktreeDock.inspectDiff') }}</span>
          </button>
          <button
            v-if="selectedWorktree.canCleanup && selected.node.status !== 'running' && selected.node.status !== 'awaiting_approval'"
            type="button"
            class="sub-agent-wt-btn is-danger"
            data-testid="sub-agent-cleanup-worktree-btn"
            @click="requestCleanup(selectedWorktree)"
          >
            <Trash2 :size="11" aria-hidden="true" />
            <span>{{ t('worktreeDock.cleanupWorktree') }}</span>
          </button>
        </div>

        <div v-if="inlineDiffPreview" class="sub-agent-diff-drawer" data-testid="sub-agent-diff-drawer">
          <div class="sub-agent-diff-drawer-head">
            <span class="sub-agent-diff-path">{{ inlineDiffPreview.file.path }}</span>
            <button type="button" class="sub-agent-diff-close" :title="t('worktreeDock.closeDiff')" @click="inlineDiffPreview = null">
              <X :size="12" />
            </button>
          </div>
          <div class="sub-agent-diff-wrap">
            <DiffViewer
              :file-path="inlineDiffPreview.file.path"
              :diff-text="inlineDiffPreview.file.diffText || inlineDiffPreview.group.rawDiff"
              :additions="inlineDiffPreview.file.additions"
              :deletions="inlineDiffPreview.file.deletions"
              :binary="inlineDiffPreview.file.binary"
              :truncated="inlineDiffPreview.file.truncated"
            />
          </div>
        </div>
      </section>

      <SubAgentCard
        v-if="selected"
        :key="`${selected.runId}:${selected.node.agent.taskId}`"
        :node="selected.node"
        :run-id="selected.runId"
        :workspace-root="workspaceRoot"
        expanded
        in-panel
        data-testid="sub-agents-detail"
      />
    </template>
  </div>
</template>

<style scoped>
.conversation-sub-agents { display: flex; flex-direction: column; gap: 10px; height: 100%; padding: 10px; overflow: auto; }
.conversation-sub-agents-empty { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--text-muted); font-size: 12px; }
.conversation-sub-agents-filter-bar { display: flex; align-items: center; gap: 6px; padding: 4px 8px; border-radius: 6px; border: 1px solid color-mix(in srgb, var(--accent-primary) 24%, transparent); background: color-mix(in srgb, var(--accent-primary) 10%, transparent); color: var(--accent-primary); font-size: 11px; }
.conversation-sub-agents-filter-bar .filter-label { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
.conversation-sub-agents-filter-bar .filter-clear-btn { border: 0; border-radius: 4px; background: transparent; color: inherit; font-size: 11px; font-weight: 600; text-decoration: underline; white-space: nowrap; flex-shrink: 0; cursor: pointer; }
.conversation-sub-agents-filter-bar .filter-clear-btn:focus-visible { outline: 1px solid hsl(var(--ring)); }
.conversation-sub-agents-roster { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0; list-style: none; }
.conversation-sub-agents-item { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 4px 8px; border-radius: 6px; font-size: 12px; cursor: pointer; }
.conversation-sub-agents-item:hover { background: var(--surface-hover); }
.conversation-sub-agents-item[aria-selected='true'] { background: var(--surface-selected); }
.conversation-sub-agents-item:focus-visible { outline: 1px solid hsl(var(--ring)); }
.conversation-sub-agents-identity { display: flex; align-items: center; gap: 6px; min-width: 0; }
.conversation-sub-agents-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-primary); }
.conversation-sub-agents-meta { display: inline-flex; align-items: center; gap: 6px; flex-shrink: 0; }
.conversation-sub-agent-branch-tag { display: inline-flex; align-items: center; gap: 3px; max-width: 110px; padding: 1px 5px; border-radius: 4px; background: var(--surface-section); color: var(--text-muted); font-size: 10px; flex-shrink: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.conversation-sub-agent-branch-tag.is-isolated { color: var(--accent-primary); background: color-mix(in srgb, var(--accent-primary) 12%, transparent); }
.conversation-sub-agent-diff-pill { font-size: 10px; color: var(--text-secondary); font-variant-numeric: tabular-nums; white-space: nowrap; flex-shrink: 0; }
.conversation-sub-agent-badge { font-size: 11px; color: var(--text-muted); white-space: nowrap; flex-shrink: 0; }
.conversation-sub-agent-badge[data-status='running'] { color: var(--accent-primary); }
.conversation-sub-agent-badge[data-status='awaiting_approval'] { color: var(--accent-warning); }
.conversation-sub-agent-badge[data-status='failed'], .conversation-sub-agent-badge[data-status='blocked'] { color: var(--accent-danger); }
.conversation-sub-agent-worktree { display: flex; flex-direction: column; gap: 6px; padding: 8px; border: 1px solid var(--border-card); border-radius: 8px; background: var(--surface-section); }
.conversation-sub-agent-worktree.is-isolated { border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-card)); background: color-mix(in srgb, var(--accent-primary) 4%, var(--surface-section)); }
.sub-agent-worktree-top { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; min-width: 0; }
.sub-agent-scope-badge { padding: 1px 6px; border-radius: 999px; font-size: 10px; font-weight: 600; white-space: nowrap; flex-shrink: 0; }
.sub-agent-scope-badge.is-main { color: var(--text-secondary); background: color-mix(in srgb, var(--border-muted) 45%, transparent); }
.sub-agent-scope-badge.is-isolated { color: var(--accent-primary); background: color-mix(in srgb, var(--accent-primary) 14%, transparent); }
.sub-agent-branch-pill { display: inline-flex; align-items: center; gap: 3px; padding: 1px 6px; border-radius: 4px; background: var(--surface-raised); font-size: 10px; color: var(--text-secondary); font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; max-width: 170px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sub-agent-diff-counts { margin-left: auto; font-size: 11px; font-weight: 700; color: var(--text-secondary); font-variant-numeric: tabular-nums; white-space: nowrap; flex-shrink: 0; }
.sub-agent-lineage { margin: 0; font-size: 11px; color: var(--text-muted); overflow-wrap: anywhere; }
.sub-agent-lineage-label { font-weight: 600; margin-right: 4px; }
.sub-agent-files { display: flex; flex-wrap: wrap; gap: 4px; }
.sub-agent-file-chip { display: inline-flex; align-items: center; gap: 4px; padding: 2px 6px; border: 1px solid var(--border-muted); border-radius: 6px; background: var(--surface-raised); font-size: 11px; color: var(--text-primary); white-space: nowrap; cursor: pointer; }
.sub-agent-file-chip:hover { border-color: var(--accent-primary); }
.sub-agent-file-chip:focus-visible { outline: 1px solid hsl(var(--ring)); border-color: var(--accent-primary); }
.sub-agent-file-dot { font-weight: 700; }
.sub-agent-file-dot[data-kind='A'] { color: var(--accent-success); }
.sub-agent-file-dot[data-kind='M'] { color: var(--accent-warning); }
.sub-agent-file-dot[data-kind='D'] { color: var(--accent-danger); }
.sub-agent-file-path { max-width: 150px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sub-agent-worktree-actions { display: flex; align-items: center; gap: 6px; justify-content: flex-end; }
.sub-agent-wt-btn { display: inline-flex; align-items: center; gap: 4px; min-height: 26px; padding: 0 8px; border: 1px solid var(--border-muted); border-radius: 6px; background: transparent; color: var(--text-secondary); font-size: 11px; font-weight: 600; white-space: nowrap; flex-shrink: 0; cursor: pointer; }
.sub-agent-wt-btn:hover { background: var(--surface-hover); color: var(--text-primary); }
.sub-agent-wt-btn:focus-visible { outline: 1px solid hsl(var(--ring)); color: var(--text-primary); }
.sub-agent-wt-btn.is-danger { color: var(--accent-danger); border-color: color-mix(in srgb, var(--accent-danger) 35%, transparent); }
.sub-agent-diff-drawer { display: flex; flex-direction: column; gap: 4px; border-top: 1px solid var(--border-card); padding-top: 6px; }
.sub-agent-diff-drawer-head { display: flex; align-items: center; gap: 6px; min-width: 0; }
.sub-agent-diff-path { flex: 1; min-width: 0; font-size: 11px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sub-agent-diff-close { display: inline-flex; align-items: center; justify-content: center; width: 22px; height: 22px; border: 0; border-radius: 6px; background: transparent; color: var(--text-muted); flex-shrink: 0; cursor: pointer; }
.sub-agent-diff-close:hover { background: var(--surface-hover); color: var(--text-primary); }
.sub-agent-diff-close:focus-visible { outline: 1px solid hsl(var(--ring)); color: var(--text-primary); }
.sub-agent-diff-wrap { height: 180px; border-radius: 6px; overflow: hidden; }
</style>
