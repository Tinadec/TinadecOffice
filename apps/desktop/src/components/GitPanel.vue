<script setup lang="ts">
import {
  AlertTriangle,
  GitBranch,
  GitCommit,
  RefreshCw,
  ArrowDown,
  ArrowUp,
  FolderTree,
  FileCode2,
  ShieldCheck,
} from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto } from '../api'
import { useGitOperation } from '../composables/useGitOperation'
import GitChangesView from './git/GitChangesView.vue'
import GitHistoryView from './git/GitHistoryView.vue'
import GitBranchView from './git/GitBranchView.vue'
import { useResponsiveMode } from '../composables/useElementSize'

const { t } = useI18n()

type GitTab = 'changes' | 'history' | 'branches'

const props = defineProps<{
  approvals: ApprovalDto[]
  currentProjectPath?: string
  selectedSessionId?: string | null
}>()

const emit = defineEmits<{
  'approval-created': [approval: ApprovalDto]
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected']
}>()

// ---- Git operation composable ----
const {
  // State
  loading,
  operationLoading,
  error,
  feedback,
  commitMessage,
  selectedPaths,
  selectAll,
  selectAllIndeterminate,
  // Data
  statusFiles,
  sections,
  repoSummary,
  recentCommits,
  logCommits,
  branches,
  pushBlockers,
  pushReady,
  hasPushCandidate,
  // Approvals
  indexApproval,
  commitApproval,
  pushApproval,
  pullApproval,
  checkoutApproval,
  branchApproval,
  fetchApproval,
  mergeApproval,
  rebaseApproval,
  resolveConflictApproval,
  deleteBranchApproval,
  renameBranchApproval,
  worktreeApproval,
  canDecideIndexApproval,
  canDecideCommitApproval,
  canDecidePushApproval,
  canDecidePullApproval,
  canDecideCheckoutApproval,
  canDecideBranchApproval,
  canDecideFetchApproval,
  canDecideMergeApproval,
  canDecideRebaseApproval,
  canDecideResolveConflictApproval,
  canDecideDeleteBranchApproval,
  canDecideRenameBranchApproval,
  canDecideWorktreeApproval,
  // Validation
  canRequestIndexApproval,
  canRequestCommitApproval,
  canRequestPushApproval,
  canRequestPullApproval,
  canRequestFetchApproval,
  // Actions
  loadStatus,
  loadLog,
  loadBranches,
  refreshAll,
  togglePath,
  toggleSelectAll,
  requestIndexApproval,
  executeApprovedIndexUpdate,
  requestCommitApproval,
  executeApprovedCommit,
  requestPushApproval,
  executeApprovedPush,
  requestPullApproval,
  executeApprovedPull,
  requestCheckoutApproval,
  executeApprovedCheckout,
  requestCreateBranchApproval,
  executeApprovedCreateBranch,
  requestFetchApproval,
  executeApprovedFetch,
  requestMergeApproval,
  executeApprovedMerge,
  requestRebaseApproval,
  executeApprovedRebase,
  requestResolveConflictApproval,
  executeApprovedResolveConflict,
  requestDeleteBranchApproval,
  executeApprovedDeleteBranch,
  requestRenameBranchApproval,
  executeApprovedRenameBranch,
  requestCreateWorktreeApproval,
  requestRemoveWorktreeApproval,
  executeApprovedWorktreeOperation,
  // Utils
  approvalStatusLabel,
  decideGitApproval,
} = useGitOperation(
  () => props.currentProjectPath,
  () => props.selectedSessionId ?? null,
  () => props.approvals,
)

// ---- Tab navigation ----
const activeTab = ref<GitTab>('changes')
const historyViewRef = ref<InstanceType<typeof GitHistoryView> | null>(null)
const branchViewRef = ref<InstanceType<typeof GitBranchView> | null>(null)

// ---- Responsive mode ----
const panelRef = ref<HTMLElement | null>(null)
const { mode: responsiveMode } = useResponsiveMode(panelRef)
const isCompact = computed(() => responsiveMode.value !== 'normal')

// ---- Active diff section ----
const workingSection = computed(() => sections.value.find((section) => section.kind === 'working_tree') ?? null)
const stagedSection = computed(() => sections.value.find((section) => section.kind === 'staged') ?? null)
const activeDiffText = computed(() => workingSection.value?.diff ?? '')
const activeDiffFiles = computed(() => workingSection.value?.files ?? [])

// ---- Tab config ----
const tabs = computed(() => [
  {
    id: 'changes' as const,
    label: t('context.gitTabChanges'),
    icon: FileCode2,
    badge: repoSummary.value.totalChanges,
  },
  {
    id: 'history' as const,
    label: t('context.gitHistory'),
    icon: GitCommit,
    badge: undefined,
  },
  {
    id: 'branches' as const,
    label: t('context.gitBranches'),
    icon: GitBranch,
    badge: undefined,
  },
])

// ---- Watchers ----
watch(activeTab, (tab) => {
  if (tab === 'history') {
    void loadLog(50)
  } else if (tab === 'branches') {
    void loadBranches()
  }
})

// ---- Emit helpers ----
function emitApproval(a: ApprovalDto) {
  emit('approval-created', a)
}

function decideApproval(a: ApprovalDto | null, decision: 'approved' | 'rejected') {
  decideGitApproval(a, decision, (approval, dec) => emit('decide-approval', approval, dec))
}

// ---- Sync action (pull + push) ----
const canSync = computed(() => canRequestPullApproval.value || canRequestPushApproval.value)
</script>

<template>
  <section ref="panelRef" class="panel git-manager-view" :class="{ 'git-compact': isCompact }">
    <!-- Header: Branch summary bar -->
    <div class="git-header">
      <div class="git-header-left">
        <GitBranch :size="15" class="git-header-icon" />
        <div class="git-branch-info">
          <strong class="git-branch-name">{{ repoSummary.branch }}</strong>
          <span v-if="repoSummary.upstream" class="git-branch-upstream">
            ← {{ repoSummary.upstream }}
          </span>
        </div>
      </div>
      <div class="git-header-right">
        <div class="git-ahead-behind">
          <span v-if="repoSummary.ahead > 0" class="git-ahead" :title="t('context.gitAhead')">
            <ArrowUp :size="11" />
            {{ repoSummary.ahead }}
          </span>
          <span v-if="repoSummary.behind > 0" class="git-behind" :title="t('context.gitBehind')">
            <ArrowDown :size="11" />
            {{ repoSummary.behind }}
          </span>
        </div>
        <button
          class="icon-button git-header-refresh"
          :title="t('context.refreshGitPlan')"
          :disabled="loading || !currentProjectPath"
          @click="refreshAll"
        >
          <RefreshCw :size="14" :class="{ spinning: loading }" />
        </button>
      </div>
    </div>

    <!-- No project state -->
    <div v-if="!currentProjectPath" class="git-empty">
      <FolderTree :size="32" />
      <span>{{ t('context.diffPlaceholder') }}</span>
    </div>

    <!-- Loading state -->
    <div v-else-if="loading && statusFiles.length === 0" class="git-empty">
      <RefreshCw :size="24" class="spinning" />
      <span>{{ t('context.loadingGitPlan') }}</span>
    </div>

    <!-- Error state -->
    <div v-else-if="error" class="git-error-banner">
      <AlertTriangle :size="14" />
      <span>{{ error }}</span>
    </div>

    <!-- Main content -->
    <template v-else>
      <!-- Tab navigation -->
      <nav class="git-nav">
        <button
          v-for="tab in tabs"
          :key="tab.id"
          class="git-nav-item"
          :class="{ active: activeTab === tab.id }"
          @click="activeTab = tab.id"
        >
          <component :is="tab.icon" :size="14" />
          <span class="git-nav-label">{{ tab.label }}</span>
          <span v-if="tab.badge !== undefined && tab.badge > 0" class="git-nav-badge">
            {{ tab.badge }}
          </span>
        </button>
      </nav>

      <!-- Tab content -->
      <div class="git-tab-content">
        <!-- Changes view -->
        <GitChangesView
          v-show="activeTab === 'changes'"
          :cwd="currentProjectPath"
          :session-id="selectedSessionId ?? null"
          :loading="loading"
          :operation-loading="operationLoading"
          :error="null"
          :feedback="feedback"
          :status-files="statusFiles"
          :commit-message="commitMessage"
          :selected-paths="selectedPaths"
          :select-all="selectAll"
          :select-all-indeterminate="selectAllIndeterminate"
          :diff-text="activeDiffText"
          :diff-files="activeDiffFiles"
          :staged-diff-text="stagedSection?.diff ?? ''"
          :staged-diff-files="stagedSection?.files ?? []"
          :push-ready="pushReady"
          :push-blockers="pushBlockers"
          :has-push-candidate="hasPushCandidate"
          :can-request-push-approval="canRequestPushApproval"
          :can-request-pull-approval="canRequestPullApproval"
          :behind="repoSummary.behind"
          :index-approval="indexApproval"
          :commit-approval="commitApproval"
          :push-approval="pushApproval"
          :resolve-conflict-approval="resolveConflictApproval"
          :can-request-index-approval="canRequestIndexApproval"
          :can-request-commit-approval="canRequestCommitApproval"
          :can-decide-index-approval="canDecideIndexApproval"
          :can-decide-commit-approval="canDecideCommitApproval"
          :can-decide-push-approval="canDecidePushApproval"
          :can-decide-resolve-conflict-approval="canDecideResolveConflictApproval"
          :recent-commits="recentCommits"
          @update:commit-message="commitMessage = $event"
          @refresh="loadStatus"
          @toggle-path="togglePath"
          @toggle-select-all="toggleSelectAll"
          @request-stage="(selection) => requestIndexApproval('stage', emitApproval, selection)"
          @request-unstage="(selection) => requestIndexApproval('unstage', emitApproval, selection)"
          @execute-index="executeApprovedIndexUpdate"
          @request-commit="requestCommitApproval(emitApproval)"
          @execute-commit="executeApprovedCommit"
          @request-push="requestPushApproval(emitApproval)"
          @execute-push="executeApprovedPush"
          @request-pull="requestPullApproval(emitApproval)"
          @request-resolve-conflict="(path, strategy) => requestResolveConflictApproval(path, strategy, emitApproval)"
          @execute-resolve-conflict="executeApprovedResolveConflict"
          @decide-approval="decideApproval"
        />

        <!-- History view -->
        <GitHistoryView
          v-show="activeTab === 'history'"
          ref="historyViewRef"
          :cwd="currentProjectPath"
          :commits="logCommits"
          :loading="false"
          @refresh="loadLog(50)"
        />

        <!-- Branch view -->
        <GitBranchView
          v-show="activeTab === 'branches'"
          ref="branchViewRef"
          :cwd="currentProjectPath"
          :current-branch="repoSummary.branch"
          :branches="branches"
          :checkout-approval="checkoutApproval"
          :branch-approval="branchApproval"
          :fetch-approval="fetchApproval"
          :delete-branch-approval="deleteBranchApproval"
          :rename-branch-approval="renameBranchApproval"
          :worktree-approval="worktreeApproval"
          :merge-approval="mergeApproval"
          :rebase-approval="rebaseApproval"
          :can-decide-checkout-approval="canDecideCheckoutApproval"
          :can-decide-branch-approval="canDecideBranchApproval"
          :can-decide-fetch-approval="canDecideFetchApproval"
          :can-decide-delete-branch-approval="canDecideDeleteBranchApproval"
          :can-decide-rename-branch-approval="canDecideRenameBranchApproval"
          :can-decide-worktree-approval="canDecideWorktreeApproval"
          :can-decide-merge-approval="canDecideMergeApproval"
          :can-decide-rebase-approval="canDecideRebaseApproval"
          :can-request-fetch-approval="canRequestFetchApproval"
          :operation-loading="operationLoading"
          @checkout="requestCheckoutApproval($event, emitApproval)"
          @create-branch="requestCreateBranchApproval($event, emitApproval)"
          @fetch="requestFetchApproval(emitApproval)"
          @delete-branch="requestDeleteBranchApproval($event.branch, $event.force, emitApproval)"
          @rename-branch="requestRenameBranchApproval($event, emitApproval)"
          @create-worktree="requestCreateWorktreeApproval($event, emitApproval)"
          @remove-worktree="requestRemoveWorktreeApproval($event, emitApproval)"
          @merge-branch="requestMergeApproval($event, emitApproval)"
          @rebase-branch="requestRebaseApproval($event, emitApproval)"
          @execute-checkout="executeApprovedCheckout"
          @execute-create-branch="executeApprovedCreateBranch"
          @execute-fetch="executeApprovedFetch"
          @execute-delete-branch="executeApprovedDeleteBranch"
          @execute-rename-branch="executeApprovedRenameBranch"
          @execute-worktree="executeApprovedWorktreeOperation"
          @execute-merge="executeApprovedMerge"
          @execute-rebase="executeApprovedRebase($event)"
          @decide-approval="decideApproval"
        />
      </div>

      <!-- Approval disclaimer -->
      <div class="git-disclaimer">
        <ShieldCheck :size="12" />
        <span>{{ t('context.gitPlanApproval') }}</span>
      </div>
    </template>
  </section>
</template>

<style scoped>
.git-manager-view {
  display: flex;
  flex-direction: column;
  gap: 0;
  height: 100%;
  overflow: hidden;
}

/* ---- Header ---- */
.git-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 10px 12px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-secondary);
}

.git-header-left {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.git-header-icon {
  color: var(--accent-primary);
  flex-shrink: 0;
}

.git-branch-info {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
}

.git-branch-name {
  font-size: 13px;
  font-weight: 700;
  color: var(--text-primary);
  font-family: 'Geist Mono', ui-monospace, monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-branch-upstream {
  font-size: 10px;
  color: var(--text-muted);
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.git-header-right {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.git-ahead-behind {
  display: flex;
  align-items: center;
  gap: 6px;
}

.git-ahead {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  color: #3fb950;
  background: rgba(63, 185, 80, 0.12);
}

.git-behind {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  color: #d29922;
  background: rgba(210, 153, 34, 0.12);
}

.git-header-refresh {
  width: 28px;
  height: 28px;
  display: flex;
  align-items: center;
  justify-content: center;
}

/* ---- Empty / loading ---- */
.git-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 48px 24px;
  color: var(--text-muted);
  font-size: 13px;
  flex: 1;
}

.git-error-banner {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  color: var(--text-reject, #f85149);
  background: var(--bg-status-warn);
  border-bottom: 1px solid rgba(248, 81, 73, 0.25);
  font-size: 12px;
}

/* ---- Tab navigation ---- */
.git-nav {
  display: flex;
  gap: 0;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
  overflow-x: auto;
}

.git-nav-item {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 14px;
  background: transparent;
  border: 0;
  border-bottom: 2px solid transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
  transition: all 0.15s;
}

.git-nav-item:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.git-nav-item.active {
  color: var(--accent-primary);
  border-bottom-color: var(--accent-primary);
}

.git-nav-label {
  font-size: 12px;
}

.git-nav-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  font-size: 10px;
  font-weight: 700;
  color: #fff;
  background: var(--accent-primary);
  border-radius: 999px;
}

/* ---- Tab content ---- */
.git-tab-content {
  flex: 1;
  overflow-y: auto;
  padding: 10px;
}

/* ---- Disclaimer ---- */
.git-disclaimer {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  border-top: 1px solid var(--border-muted);
  font-size: 10px;
  color: var(--text-muted);
  background: var(--bg-secondary);
}

/* ---- Compact mode ---- */
.git-compact .git-nav-item {
  padding: 8px 10px;
}

.git-compact .git-nav-label {
  display: none;
}

.git-compact .git-header {
  padding: 8px 10px;
}

.git-compact .git-branch-name {
  font-size: 12px;
}

/* ---- Animations ---- */
.spinning {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}
</style>
