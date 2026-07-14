<script setup lang="ts">
import {
  AlertTriangle,
  CheckCircle2,
  GitBranch,
  GitCommitHorizontal,
  RefreshCw,
  ShieldCheck,
  ShieldX,
  Upload,
} from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type ApprovalDto, type CodeToolExecuteResultDto } from '../api'
import CommitMessageEditor from './git/CommitMessageEditor.vue'

const { t } = useI18n()

interface GitStatusFile {
  path: string
  previous_path?: string | null
  staged_status?: string
  unstaged_status?: string
  status?: string
  is_untracked?: boolean
  is_conflicted?: boolean
  is_renamed?: boolean
}

interface GitPreviewData {
  git_root?: string
  branch?: string
  upstream?: string | null
  ahead?: number
  behind?: number
  has_uncommitted_changes?: boolean
  files?: GitStatusFile[]
}

interface GitPushPlanData extends GitPreviewData {
  push_ready?: boolean
  push_blockers?: string[]
  needs_push?: boolean
  recent_commits?: string[]
}

const props = defineProps<{
  approvals: ApprovalDto[]
  currentProjectPath?: string
  selectedSessionId?: string | null
}>()

const emit = defineEmits<{
  'approval-created': [approval: ApprovalDto]
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected']
}>()

const loading = ref(false)
const operationLoading = ref(false)
const error = ref<string | null>(null)
const feedback = ref<string | null>(null)
const commitMessage = ref('')
const selectedPaths = ref<Set<string>>(new Set())
const preview = ref<CodeToolExecuteResultDto | null>(null)
const pushPlan = ref<CodeToolExecuteResultDto | null>(null)
const commitApprovalId = ref<string | null>(null)
const pushApprovalId = ref<string | null>(null)
const selectAll = ref(true)

const previewData = computed(() => (preview.value?.data ?? {}) as GitPreviewData)
const pushData = computed(() => (pushPlan.value?.data ?? {}) as GitPushPlanData)
const statusFiles = computed(() => Array.isArray(previewData.value.files) ? previewData.value.files : [])
const totalChanges = computed(() => statusFiles.value.length)
const pushReady = computed(() => pushData.value.push_ready === true)
const pushBlockers = computed(() => Array.isArray(pushData.value.push_blockers) ? pushData.value.push_blockers : [])
const noUpstreamOnly = computed(() => pushBlockers.value.length === 1 && pushBlockers.value[0] === 'no upstream')
const hasPushCandidate = computed(() => {
  const ahead = typeof pushData.value.ahead === 'number' ? pushData.value.ahead : 0
  return (pushReady.value && ahead > 0) || noUpstreamOnly.value
})
const pushCommand = computed(() => noUpstreamOnly.value ? `git push -u origin ${pushData.value.branch ?? 'HEAD'}` : 'git push')
const selectedCommitPaths = computed(() => [...selectedPaths.value])
const canRequestCommitApproval = computed(() =>
  Boolean(props.currentProjectPath && props.selectedSessionId && commitMessage.value.trim() && selectedCommitPaths.value.length > 0),
)
const canRequestPushApproval = computed(() =>
  Boolean(props.currentProjectPath && props.selectedSessionId && hasPushCandidate.value),
)
const commitApproval = computed(() => props.approvals.find((a) => a.id === commitApprovalId.value) ?? null)
const pushApproval = computed(() => props.approvals.find((a) => a.id === pushApprovalId.value) ?? null)
const canDecideCommitApproval = computed(() => commitApproval.value?.status === 'pending')
const canDecidePushApproval = computed(() => pushApproval.value?.status === 'pending')
const recentCommits = computed(() => {
  const list = pushData.value.recent_commits
  return Array.isArray(list) ? list.filter((c): c is string => typeof c === 'string').slice(0, 5) : []
})

async function loadGit() {
  if (!props.currentProjectPath) {
    preview.value = null
    pushPlan.value = null
    selectedPaths.value = new Set()
    error.value = null
    return
  }

  loading.value = true
  error.value = null
  feedback.value = null
  try {
    const [nextPreview, nextPushPlan] = await Promise.all([
      api.executeCodeTool('git_worktree_manager', {
        cwd: props.currentProjectPath,
        arguments: { action: 'diff_preview', max_files: 120, max_diff_bytes: 180000 },
      }),
      api.executeCodeTool('git_worktree_manager', {
        cwd: props.currentProjectPath,
        arguments: { action: 'push_plan' },
      }),
    ])
    preview.value = nextPreview
    pushPlan.value = nextPushPlan
    syncSelection()
  } catch (err) {
    error.value = err instanceof Error ? err.message : t('context.gitLoadFailed')
  } finally {
    loading.value = false
  }
}

function syncSelection() {
  selectedPaths.value = new Set(statusFiles.value.map((file) => file.path))
  selectAll.value = true
}

function togglePath(path: string) {
  const next = new Set(selectedPaths.value)
  if (next.has(path)) {
    next.delete(path)
  } else {
    next.add(path)
  }
  selectedPaths.value = next
  selectAll.value = statusFiles.value.length > 0 && next.size === statusFiles.value.length
}

function toggleSelectAll() {
  if (selectAll.value) {
    selectedPaths.value = new Set()
    selectAll.value = false
  } else {
    selectedPaths.value = new Set(statusFiles.value.map((f) => f.path))
    selectAll.value = true
  }
}

async function requestCommitApproval() {
  if (!props.currentProjectPath || !props.selectedSessionId || !canRequestCommitApproval.value) return
  operationLoading.value = true
  feedback.value = null
  try {
    const paths = selectedCommitPaths.value
    const approval = await api.createApproval({
      session_id: props.selectedSessionId,
      kind: 'git',
      summary: `Commit ${paths.length} file${paths.length === 1 ? '' : 's'} on ${previewData.value.branch ?? 'HEAD'}`,
      command: `git add -- ${paths.join(' ')} && git commit -m "${commitMessage.value.trim()}"`,
      cwd: props.currentProjectPath,
    })
    commitApprovalId.value = approval.id
    feedback.value = t('context.gitCommitApprovalRequested')
    emit('approval-created', approval)
  } catch (err) {
    feedback.value = err instanceof Error ? err.message : t('context.gitApprovalRequestFailed')
  } finally {
    operationLoading.value = false
  }
}

async function executeApprovedCommit() {
  if (!props.currentProjectPath || !props.selectedSessionId || !commitApproval.value || commitApproval.value.status !== 'approved') return
  operationLoading.value = true
  feedback.value = null
  try {
    const result = await api.executeCodeTool('git_commit', {
      session_id: props.selectedSessionId,
      approval_id: commitApproval.value.id,
      cwd: props.currentProjectPath,
      arguments: {
        confirm_commit: true,
        paths: selectedCommitPaths.value,
        message: commitMessage.value.trim(),
      },
    })
    feedback.value = result.summary
    commitMessage.value = ''
    commitApprovalId.value = null
    await loadGit()
  } catch (err) {
    feedback.value = err instanceof Error ? err.message : t('context.gitCommitFailed')
  } finally {
    operationLoading.value = false
  }
}

async function requestPushApproval() {
  if (!props.currentProjectPath || !props.selectedSessionId || !canRequestPushApproval.value) return
  operationLoading.value = true
  feedback.value = null
  try {
    const branch = pushData.value.branch ?? 'HEAD'
    const upstream = pushData.value.upstream ?? 'origin'
    const ahead = typeof pushData.value.ahead === 'number' ? pushData.value.ahead : 0
    const approval = await api.createApproval({
      session_id: props.selectedSessionId,
      kind: 'git',
      summary: `Push ${branch} to ${upstream} (${ahead} ahead)`,
      command: pushCommand.value,
      cwd: props.currentProjectPath,
    })
    pushApprovalId.value = approval.id
    feedback.value = t('context.gitApprovalRequested')
    emit('approval-created', approval)
  } catch (err) {
    feedback.value = err instanceof Error ? err.message : t('context.gitApprovalRequestFailed')
  } finally {
    operationLoading.value = false
  }
}

async function executeApprovedPush() {
  if (!props.currentProjectPath || !props.selectedSessionId || !pushApproval.value || pushApproval.value.status !== 'approved') return
  operationLoading.value = true
  feedback.value = null
  try {
    const result = await api.executeCodeTool('git_worktree_manager', {
      session_id: props.selectedSessionId,
      approval_id: pushApproval.value.id,
      cwd: props.currentProjectPath,
      arguments: {
        action: 'push',
        confirm_push: true,
        set_upstream: noUpstreamOnly.value,
        remote: 'origin',
      },
    })
    feedback.value = result.summary
    pushApprovalId.value = null
    await loadGit()
  } catch (err) {
    feedback.value = err instanceof Error ? err.message : t('context.gitPushFailed')
  } finally {
    operationLoading.value = false
  }
}

function approvalStatusLabel(approval: ApprovalDto | null): string {
  if (!approval) return t('context.gitNoApproval')
  return `${approval.id} / ${approval.status}`
}

function decideGitApproval(approval: ApprovalDto | null, decision: 'approved' | 'rejected') {
  if (!approval || approval.status !== 'pending') return
  emit('decide-approval', approval, decision)
}

watch(() => props.currentProjectPath, () => {
  commitApprovalId.value = null
  pushApprovalId.value = null
  void loadGit()
}, { immediate: true })
</script>

<template>
  <section class="panel commit-panel">
    <div class="commit-panel-head">
      <div class="panel-title">
        <GitCommitHorizontal :size="15" />
        <span>{{ t('context.homeCommit') }}</span>
      </div>
      <button
        class="icon-button"
        :title="t('context.refreshGitPlan')"
        :disabled="loading || !props.currentProjectPath"
        @click="loadGit"
      >
        <RefreshCw :size="14" />
      </button>
    </div>

    <div v-if="!props.currentProjectPath" class="commit-empty">
      <span>{{ t('context.diffPlaceholder') }}</span>
    </div>
    <div v-else-if="loading" class="commit-empty">
      <span>{{ t('context.loadingGitPlan') }}</span>
    </div>
    <div v-else-if="error" class="commit-state risky">
      <AlertTriangle :size="15" />
      <span>{{ error }}</span>
    </div>

    <template v-else>
      <!-- Branch summary -->
      <div class="commit-branch-bar">
        <GitBranch :size="13" />
        <strong>{{ previewData.branch ?? '-' }}</strong>
        <span class="commit-upstream">{{ previewData.upstream ?? '-' }}</span>
        <span class="commit-ahead-behind">
          ↑{{ previewData.ahead ?? 0 }} ↓{{ previewData.behind ?? 0 }}
        </span>
        <span class="commit-file-count">{{ totalChanges }} {{ t('context.gitChangedFiles') }}</span>
      </div>

      <!-- File selection list -->
      <div class="commit-files-section">
        <div class="commit-section-head">
          <label class="commit-select-all">
            <input type="checkbox" :checked="selectAll" @change="toggleSelectAll" />
            <span>{{ t('context.gitCommit') }}</span>
          </label>
          <small>{{ selectedCommitPaths.length }} / {{ totalChanges }}</small>
        </div>
        <div class="commit-file-list">
          <label
            v-for="file in statusFiles"
            :key="file.path"
            class="commit-file-row"
            :class="{ selected: selectedPaths.has(file.path) }"
          >
            <input type="checkbox" :checked="selectedPaths.has(file.path)" @change="togglePath(file.path)" />
            <span class="commit-file-path">{{ file.path }}</span>
            <small class="commit-file-status">{{ file.status ?? file.unstaged_status ?? '?' }}</small>
          </label>
          <div v-if="statusFiles.length === 0" class="commit-empty-inline">
            {{ t('context.gitNoDiff') }}
          </div>
        </div>
      </div>

      <!-- Commit message editor -->
      <CommitMessageEditor
        v-model="commitMessage"
        :recent-commits="recentCommits"
      />

      <!-- Commit actions -->
      <div class="commit-actions">
        <button
          class="secondary-button commit-action-btn"
          :disabled="operationLoading || !canRequestCommitApproval"
          @click="requestCommitApproval"
        >
          <ShieldCheck :size="14" />
          <span>{{ t('context.gitRequestCommitApproval') }}</span>
        </button>
        <button
          class="secondary-button commit-action-btn commit-execute-btn"
          :disabled="operationLoading || commitApproval?.status !== 'approved'"
          @click="executeApprovedCommit"
        >
          <CheckCircle2 :size="14" />
          <span>{{ t('context.gitExecuteCommit') }}</span>
        </button>
      </div>
      <span class="commit-action-note">{{ t('context.gitApprovalStatus') }}: {{ approvalStatusLabel(commitApproval) }}</span>
      <div v-if="canDecideCommitApproval" class="commit-approval-inline">
        <button class="icon-button approve" :title="t('approval.approve')" @click="decideGitApproval(commitApproval, 'approved')">
          <CheckCircle2 :size="14" />
        </button>
        <button class="icon-button reject" :title="t('approval.reject')" @click="decideGitApproval(commitApproval, 'rejected')">
          <ShieldX :size="14" />
        </button>
      </div>

      <!-- Push section -->
      <div class="commit-push-section">
        <div class="commit-section-head">
          <Upload :size="14" />
          <span>{{ t('context.gitPushReadiness') }}</span>
        </div>
        <div class="commit-push-status" :class="{ ready: pushReady, blocked: !pushReady }">
          <component :is="pushReady ? CheckCircle2 : AlertTriangle" :size="18" />
          <div>
            <strong>{{ pushReady ? t('context.gitPushReady') : t('context.gitPushBlocked') }}</strong>
            <span>{{ pushPlan?.summary }}</span>
          </div>
        </div>
        <div v-if="pushBlockers.length > 0" class="commit-push-blockers">
          <small v-for="blocker in pushBlockers" :key="blocker">{{ blocker }}</small>
        </div>
        <div class="commit-actions">
          <button
            class="secondary-button commit-action-btn"
            :disabled="operationLoading || !canRequestPushApproval"
            @click="requestPushApproval"
          >
            <ShieldCheck :size="14" />
            <span>{{ t('context.gitRequestPushApproval') }}</span>
          </button>
          <button
            class="secondary-button commit-action-btn commit-execute-btn"
            :disabled="operationLoading || pushApproval?.status !== 'approved'"
            @click="executeApprovedPush"
          >
            <Upload :size="14" />
            <span>{{ t('context.gitExecutePush') }}</span>
          </button>
        </div>
        <span class="commit-action-note">{{ t('context.gitApprovalStatus') }}: {{ approvalStatusLabel(pushApproval) }}</span>
        <div v-if="canDecidePushApproval" class="commit-approval-inline">
          <button class="icon-button approve" :title="t('approval.approve')" @click="decideGitApproval(pushApproval, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="decideGitApproval(pushApproval, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
      </div>

      <div v-if="feedback" class="commit-feedback">
        <ShieldCheck :size="14" />
        <span>{{ feedback }}</span>
      </div>

      <div class="commit-disclaimer">
        <ShieldCheck :size="14" />
        <span>{{ t('context.gitPlanApproval') }}</span>
      </div>
    </template>
  </section>
</template>

<style scoped>
.commit-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  height: 100%;
  overflow-y: auto;
}

.commit-panel-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.commit-empty {
  padding: 24px 12px;
  text-align: center;
  color: var(--text-muted);
  font-size: 13px;
}

.commit-state {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px;
  border-radius: 8px;
  font-size: 12px;
}

.commit-state.risky {
  color: var(--text-error, #f85149);
  background: var(--bg-error, rgba(248, 81, 73, 0.1));
}

.commit-branch-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  background: var(--bg-tertiary);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  font-size: 12px;
  color: var(--text-secondary);
}

.commit-branch-bar strong {
  color: var(--text-primary);
}

.commit-upstream {
  color: var(--text-muted);
}

.commit-ahead-behind {
  margin-left: auto;
  font-weight: 600;
  color: var(--text-secondary);
}

.commit-file-count {
  color: var(--accent-primary);
  font-weight: 600;
}

.commit-files-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.commit-section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
  gap: 6px;
}

.commit-select-all {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
}

.commit-file-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 220px;
  overflow-y: auto;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-primary);
}

.commit-file-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 10px;
  font-size: 12px;
  cursor: pointer;
  transition: background 0.12s;
}

.commit-file-row:hover {
  background: var(--bg-hover);
}

.commit-file-row.selected {
  background: var(--bg-selected);
}

.commit-file-path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-primary);
  font-family: 'Fira Code', 'Consolas', monospace;
  font-size: 11.5px;
}

.commit-file-status {
  font-size: 10px;
  font-weight: 700;
  color: var(--text-muted);
  text-transform: uppercase;
}

.commit-empty-inline {
  padding: 16px 10px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
}

.commit-actions {
  display: flex;
  gap: 6px;
}

.commit-action-btn {
  flex: 1;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 7px 10px;
  font-size: 12px;
}

.commit-execute-btn {
  background: var(--bg-primary-button);
  color: #fff;
  border-color: var(--bg-primary-button);
}

.commit-execute-btn:hover:not(:disabled) {
  background: var(--bg-primary-button-hover);
}

.commit-action-note {
  font-size: 11px;
  color: var(--text-muted);
}

.commit-approval-inline {
  display: flex;
  gap: 6px;
}

.commit-push-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-top: 10px;
  border-top: 1px solid var(--border-muted);
}

.commit-push-status {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px;
  border-radius: 8px;
}

.commit-push-status.ready {
  background: rgba(63, 185, 80, 0.08);
  color: #3fb950;
}

.commit-push-status.blocked {
  background: rgba(210, 153, 34, 0.08);
  color: #d29922;
}

.commit-push-status strong {
  display: block;
  font-size: 12px;
  color: var(--text-primary);
}

.commit-push-status span {
  font-size: 11px;
  color: var(--text-muted);
}

.commit-push-blockers {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.commit-push-blockers small {
  padding: 2px 6px;
  background: var(--bg-tertiary);
  border-radius: 4px;
  font-size: 10px;
  color: var(--text-muted);
}

.commit-feedback {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  background: var(--bg-selected);
  border: 1px solid var(--bg-selected-outline);
  border-radius: 8px;
  font-size: 12px;
  color: var(--text-primary);
}

.commit-disclaimer {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  font-size: 11px;
  color: var(--text-muted);
  border-top: 1px solid var(--border-muted);
}
</style>
