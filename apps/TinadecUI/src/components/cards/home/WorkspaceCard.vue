<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronRight, Eye, GitBranch, Trash2 } from '@lucide/vue'
import i18n from '@/i18n'
import WorkspaceRootsPanel from '@/components/WorkspaceRootsPanel.vue'
import { homeController } from '@/controllers/HomeController'
import { api } from '@/api'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { useSessionWorktrees, type WorkerWorktreeGroup } from '@/composables/useSessionWorktrees'
import { activeDockFocus, focusDockCard, highlightedDockCard } from '@/lib/homeDockSync'

function resolveTranslator(): (key: string, params?: Record<string, unknown>) => string {
  try {
    const inst = useI18n()
    return (key, params) => (params ? inst.t(key, params) : inst.t(key))
  } catch {
    return (key, params) => (params ? i18n.global.t(key, params) : i18n.global.t(key))
  }
}

const t = resolveTranslator()
const c = homeController
const projectPath = computed(() => c?.currentProject?.value?.path ?? null)
const sessionId = computed(() => c?.selectedSessionId?.value ?? null)
const resolveAgentName = typeof api?.listAgents === 'function'
  ? useAgentDirectory().nameFor
  : ((slugOrHandle: string | null | undefined) => slugOrHandle ?? '')

const { groups: worktreeGroups, isolatedGroups } = useSessionWorktrees({
  projectPath: () => projectPath.value,
  sessionId: () => sessionId.value,
  runs: () => c?.runs?.value,
  feed: () => c?.conversationFeed,
  orchestration: () => c?.orchestration?.value,
  toolExecutions: () => c?.toolExecutions?.value,
  resolveAgentName,
})

const expanded = ref(true)

function inspectWorktree(group: WorkerWorktreeGroup): void {
  focusDockCard({
    card: 'git',
    reason: 'worktree',
    runId: group.runId,
    taskId: group.taskId,
    workerHandle: group.workerHandle,
    workerLabel: group.workerLabel,
    worktreePath: group.worktreePath,
    branch: group.branch,
    filePath: group.files[0]?.path ?? null,
  }, { title: t('uie.cardTitle.git') })
}

function cleanupWorktree(group: WorkerWorktreeGroup): void {
  focusDockCard({
    card: 'git',
    reason: 'worktree',
    runId: group.runId,
    taskId: group.taskId,
    workerHandle: group.workerHandle,
    worktreePath: group.worktreePath,
    branch: group.branch,
    requestCleanupPath: group.worktreePath,
  }, { title: t('uie.cardTitle.git') })
}

function isFocusedGroup(group: WorkerWorktreeGroup): boolean {
  const focus = activeDockFocus.value
  if (!focus || focus.card !== 'workspace') return false
  return Boolean(
    (focus.worktreePath && focus.worktreePath === group.worktreePath)
    || (focus.taskId && focus.taskId === group.taskId)
    || (focus.workerHandle && focus.workerHandle === group.workerHandle),
  )
}
</script>

<template>
  <div
    class="workspace-card-shell"
    data-dock-card="workspace"
    :data-dock-highlighted="highlightedDockCard === 'workspace' ? 'true' : undefined"
  >
    <section
      v-if="projectPath && worktreeGroups.length > 0"
      class="ws-worktrees-box"
      data-testid="workspace-worktrees-section"
    >
      <button
        type="button"
        class="ws-worktrees-toggle"
        :aria-expanded="expanded"
        @click="expanded = !expanded"
      >
        <ChevronDown v-if="expanded" :size="13" aria-hidden="true" />
        <ChevronRight v-else :size="13" aria-hidden="true" />
        <span>{{ t('worktreeDock.sectionTitle') }}</span>
        <span v-if="isolatedGroups.length > 0" class="ws-worktrees-isolated-count">
          {{ t('worktreeDock.isolatedCount', { count: isolatedGroups.length }) }}
        </span>
      </button>

      <div v-if="expanded" class="ws-worktrees-list">
        <article
          v-for="group in worktreeGroups"
          :key="group.id"
          class="ws-worktree-row"
          :class="{ 'is-isolated': !group.isMain, 'is-focused': isFocusedGroup(group) }"
          :data-worker-task-id="group.taskId ?? undefined"
          :data-worktree-path="group.worktreePath"
          data-testid="workspace-worktree-item"
        >
          <div class="ws-worktree-row-top">
            <span class="ws-worktree-scope-pill" :class="group.isMain ? 'is-main' : 'is-isolated'">
              {{ group.isMain ? t('worktreeDock.mainWorkspace') : t('worktreeDock.isolatedWorktree') }}
            </span>
            <strong class="ws-worktree-worker">
              {{ group.workerLabel ?? (group.isMain ? t('worktreeDock.mainWorkspace') : t('worktreeDock.unassignedWorker')) }}
            </strong>
            <span v-if="group.branch" class="ws-worktree-branch">
              <GitBranch :size="10" aria-hidden="true" />
              <span>{{ group.branch }}</span>
            </span>
          </div>

          <div class="ws-worktree-row-meta">
            <span class="ws-worktree-diff-counts" data-testid="workspace-worktree-diff-counts">
              {{ t('worktreeDock.diffSummary', {
                added: group.counts.added,
                modified: group.counts.modified,
                deleted: group.counts.deleted,
              }) }}
            </span>
            <span v-if="group.lineage.length > 0" class="ws-worktree-lineage">
              {{ group.lineage.map((step) => step.label).join(' › ') }}
            </span>
            <span v-else-if="group.isMain" class="ws-worktree-lineage">
              {{ t('worktreeDock.sharedWorkspaceHint') }}
            </span>
          </div>

          <div class="ws-worktree-row-actions">
            <button
              type="button"
              class="ws-worktree-btn"
              data-testid="workspace-worktree-inspect-btn"
              @click="inspectWorktree(group)"
            >
              <Eye :size="11" aria-hidden="true" />
              <span>{{ t('worktreeDock.inspectDiff') }}</span>
            </button>
            <button
              v-if="group.canCleanup"
              type="button"
              class="ws-worktree-btn is-danger"
              data-testid="workspace-worktree-cleanup-btn"
              @click="cleanupWorktree(group)"
            >
              <Trash2 :size="11" aria-hidden="true" />
              <span>{{ t('worktreeDock.cleanupWorktree') }}</span>
            </button>
          </div>
        </article>
      </div>
    </section>

    <div class="ws-card-roots-body">
      <WorkspaceRootsPanel />
    </div>
  </div>
</template>

<style scoped>
.workspace-card-shell {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

.ws-worktrees-box {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin: 10px 12px 0;
  padding: 8px;
  border: 1px solid var(--border-card);
  border-radius: 10px;
  background: var(--surface-section);
  flex-shrink: 0;
}

.ws-worktrees-toggle {
  display: flex;
  align-items: center;
  gap: 6px;
  border: 0;
  padding: 2px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 700;
  cursor: pointer;
}

.ws-worktrees-isolated-count {
  margin-left: auto;
  padding: 1px 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
  color: var(--accent-primary);
  font-size: 10px;
}

.ws-worktrees-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 180px;
  overflow-y: auto;
}

.ws-worktree-row {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 6px 8px;
  border: 1px solid var(--border-card);
  border-radius: 8px;
  background: var(--surface-raised);
}

.ws-worktree-row.is-isolated {
  border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-card));
  background: color-mix(in srgb, var(--accent-primary) 4%, var(--surface-raised));
}

.ws-worktree-row.is-focused {
  border-color: var(--accent-primary);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 22%, transparent);
}

.ws-worktree-row-top {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.ws-worktree-scope-pill {
  padding: 1px 5px;
  border-radius: 999px;
  font-size: 9.5px;
  font-weight: 700;
}

.ws-worktree-scope-pill.is-main {
  color: var(--text-secondary);
  background: color-mix(in srgb, var(--border-muted) 45%, transparent);
}

.ws-worktree-scope-pill.is-isolated {
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
}

.ws-worktree-worker {
  font-size: 11px;
  color: var(--text-primary);
}

.ws-worktree-branch {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 10px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}

.ws-worktree-row-meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  flex-wrap: wrap;
  font-size: 10px;
  color: var(--text-muted);
}

.ws-worktree-diff-counts {
  font-weight: 700;
  color: var(--text-secondary);
  font-variant-numeric: tabular-nums;
}

.ws-worktree-row-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
}

.ws-worktree-btn {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px 6px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 600;
  cursor: pointer;
}

.ws-worktree-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.ws-worktree-btn.is-danger {
  color: var(--accent-danger);
  border-color: color-mix(in srgb, var(--accent-danger) 35%, transparent);
}

.ws-card-roots-body {
  flex: 1;
  min-height: 0;
}
</style>
