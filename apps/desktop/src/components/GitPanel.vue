<script setup lang="ts">
import { AlertTriangle, CheckCircle2, FileCode2, GitBranch, GitCommitHorizontal, GitPullRequest, GitCompare, RefreshCw, ShieldCheck, ShieldX, Upload } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type ApprovalDto, type CodeToolExecuteResultDto } from '../api'
import { parseUnifiedDiff, type GitDiffFile } from '../gitDiffParser'
import CommitCompare from './git/CommitCompare.vue'
import CommitMessageEditor from './git/CommitMessageEditor.vue'
import DiffViewer from './git/DiffViewer.vue'
import WorktreeManager from './git/WorktreeManager.vue'
import { reconstructFromHunks, type DiffFileEntry } from './git/diffUtils'

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

interface GitDiffSection {
  id: string
  kind: 'working_tree' | 'staged' | 'branch_range' | string
  title: string
  subtitle?: string | null
  base_ref?: string | null
  head_ref?: string | null
  diff: string
  files: Array<{
    path: string
    previous_path?: string | null
    change_type: string
    additions: number
    deletions: number
    binary: boolean
    truncated: boolean
  }>
  file_count: number
  additions: number
  deletions: number
  notices: string[]
}

interface GitPreviewData {
  git_root?: string
  branch?: string
  upstream?: string | null
  ahead?: number
  behind?: number
  has_uncommitted_changes?: boolean
  files?: GitStatusFile[]
  sections?: GitDiffSection[]
}

interface GitPushPlanData extends GitPreviewData {
  diff_stat?: string
  recent_commits?: string[]
  remotes?: string[]
  push_ready?: boolean
  push_blockers?: string[]
  suggested_commands?: string[]
  needs_push?: boolean
  worktrees?: Array<Record<string, unknown>>
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
const selectedSectionId = ref('working_tree')
const selectedFilePath = ref<string | null>(null)
const selectedPaths = ref<Set<string>>(new Set())
const indexApprovalId = ref<string | null>(null)
const indexAction = ref<'stage' | 'unstage' | null>(null)
const commitApprovalId = ref<string | null>(null)
const pushApprovalId = ref<string | null>(null)
const preview = ref<CodeToolExecuteResultDto | null>(null)
const pushPlan = ref<CodeToolExecuteResultDto | null>(null)
const topTab = ref<'changes' | 'compare' | 'worktrees'>('changes')

const previewData = computed(() => (preview.value?.data ?? {}) as GitPreviewData)
const pushData = computed(() => (pushPlan.value?.data ?? {}) as GitPushPlanData)
const sections = computed(() => Array.isArray(previewData.value.sections) ? previewData.value.sections : [])
const statusFiles = computed(() => Array.isArray(previewData.value.files) ? previewData.value.files : [])
const activeSection = computed(() => sections.value.find((section) => section.id === selectedSectionId.value) ?? sections.value[0] ?? null)
const parsedActiveSection = computed(() => parseUnifiedDiff(activeSection.value?.diff ?? ''))
const activeParsedFiles = computed(() => parsedActiveSection.value.files)
const selectedParsedFile = computed(() => {
  if (selectedFilePath.value) {
    const found = activeParsedFiles.value.find((file) => file.path === selectedFilePath.value)
    if (found) return found
  }
  return activeParsedFiles.value[0] ?? null
})
const pushBlockers = computed(() => Array.isArray(pushData.value.push_blockers) ? pushData.value.push_blockers : [])
const pushReady = computed(() => pushData.value.push_ready === true)
const noUpstreamOnly = computed(() => pushBlockers.value.length === 1 && pushBlockers.value[0] === 'no upstream')
const hasPushCandidate = computed(() => {
  const ahead = typeof pushData.value.ahead === 'number' ? pushData.value.ahead : 0
  return (pushReady.value && ahead > 0) || noUpstreamOnly.value
})
const selectedCommitPaths = computed(() => [...selectedPaths.value])
const canRequestIndexApproval = computed(() =>
  Boolean(props.currentProjectPath && props.selectedSessionId && selectedCommitPaths.value.length > 0)
)
const canRequestCommitApproval = computed(() =>
  Boolean(props.currentProjectPath && props.selectedSessionId && commitMessage.value.trim() && selectedCommitPaths.value.length > 0)
)
const canRequestPushApproval = computed(() =>
  Boolean(props.currentProjectPath && props.selectedSessionId && hasPushCandidate.value)
)
const commitApproval = computed(() => props.approvals.find((approval) => approval.id === commitApprovalId.value) ?? null)
const pushApproval = computed(() => props.approvals.find((approval) => approval.id === pushApprovalId.value) ?? null)
const indexApproval = computed(() => props.approvals.find((approval) => approval.id === indexApprovalId.value) ?? null)
const canDecideIndexApproval = computed(() => indexApproval.value?.status === 'pending')
const canDecideCommitApproval = computed(() => commitApproval.value?.status === 'pending')
const canDecidePushApproval = computed(() => pushApproval.value?.status === 'pending')
const pushCommand = computed(() => noUpstreamOnly.value ? `git push -u origin ${pushData.value.branch ?? 'HEAD'}` : 'git push')
const totalChanges = computed(() => statusFiles.value.length)
const worktrees = computed(() => Array.isArray(pushData.value.worktrees) ? pushData.value.worktrees as Array<Record<string, unknown>> : [])
const diffStatLines = computed(() => stringListFromText(pushData.value.diff_stat))
const recentCommits = computed(() => stringList(pushData.value.recent_commits).slice(0, 5))
const remotes = computed(() => stringList(pushData.value.remotes))
const suggestedCommands = computed(() => stringList(pushData.value.suggested_commands))
const hasRepositoryDetails = computed(() =>
  diffStatLines.value.length > 0 ||
  recentCommits.value.length > 0 ||
  remotes.value.length > 0 ||
  suggestedCommands.value.length > 0
)

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
        arguments: { action: 'diff_preview', max_files: 120, max_diff_bytes: 180000 }
      }),
      api.executeCodeTool('git_worktree_manager', {
        cwd: props.currentProjectPath,
        arguments: { action: 'push_plan' }
      })
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
  selectedSectionId.value = sections.value[0]?.id ?? 'working_tree'
  selectedFilePath.value = activeParsedFiles.value[0]?.path ?? null
  selectedPaths.value = new Set(statusFiles.value.map((file) => file.path))
}

function togglePath(path: string) {
  const next = new Set(selectedPaths.value)
  if (next.has(path)) {
    next.delete(path)
  } else {
    next.add(path)
  }
  selectedPaths.value = next
}

async function requestIndexApproval(action: 'stage' | 'unstage') {
  if (!props.currentProjectPath || !props.selectedSessionId || !canRequestIndexApproval.value) return
  operationLoading.value = true
  feedback.value = null
  try {
    const paths = selectedCommitPaths.value
    const isStage = action === 'stage'
    const approval = await api.createApproval({
      session_id: props.selectedSessionId,
      kind: 'git',
      summary: `${isStage ? 'Stage' : 'Unstage'} ${paths.length} file${paths.length === 1 ? '' : 's'} on ${previewData.value.branch ?? 'HEAD'}`,
      command: `${isStage ? 'git add' : 'git restore --staged'} -- ${paths.join(' ')}`,
      cwd: props.currentProjectPath
    })
    indexApprovalId.value = approval.id
    indexAction.value = action
    feedback.value = t('context.gitIndexApprovalRequested')
    emit('approval-created', approval)
  } catch (err) {
    feedback.value = err instanceof Error ? err.message : t('context.gitApprovalRequestFailed')
  } finally {
    operationLoading.value = false
  }
}

async function executeApprovedIndexUpdate() {
  if (!props.currentProjectPath || !props.selectedSessionId || !indexApproval.value || indexApproval.value.status !== 'approved' || !indexAction.value) return
  operationLoading.value = true
  feedback.value = null
  try {
    const confirmKey = indexAction.value === 'stage' ? 'confirm_stage' : 'confirm_unstage'
    const result = await api.executeCodeTool('git_worktree_manager', {
      session_id: props.selectedSessionId,
      approval_id: indexApproval.value.id,
      cwd: props.currentProjectPath,
      arguments: {
        action: indexAction.value,
        [confirmKey]: true,
        paths: selectedCommitPaths.value
      }
    })
    feedback.value = result.summary
    indexApprovalId.value = null
    indexAction.value = null
    await loadGit()
  } catch (err) {
    feedback.value = err instanceof Error ? err.message : t('context.gitIndexUpdateFailed')
  } finally {
    operationLoading.value = false
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
      cwd: props.currentProjectPath
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
    const result = await api.executeCodeTool('git_worktree_manager', {
      session_id: props.selectedSessionId,
      approval_id: commitApproval.value.id,
      cwd: props.currentProjectPath,
      arguments: {
        action: 'commit',
        confirm_commit: true,
        paths: selectedCommitPaths.value,
        message: commitMessage.value.trim()
      }
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
      cwd: props.currentProjectPath
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
        remote: 'origin'
      }
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

function stringList(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((entry): entry is string => typeof entry === 'string' && entry.trim().length > 0)
    : []
}

function stringListFromText(value: unknown): string[] {
  return typeof value === 'string'
    ? value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
    : []
}

function decideGitApproval(approval: ApprovalDto | null, decision: 'approved' | 'rejected') {
  if (!approval || approval.status !== 'pending') return
  emit('decide-approval', approval, decision)
}

function sectionFileMeta(file: GitDiffFile) {
  return activeSection.value?.files.find((item) => item.path === file.path) ?? null
}

const diffEntries = computed<DiffFileEntry[]>(() => {
  return activeParsedFiles.value.map((file) => {
    const meta = sectionFileMeta(file)
    const { original, modified } = reconstructFromHunks(file)
    return {
      path: file.path,
      previousPath: file.previous_path,
      originalContent: original,
      modifiedContent: modified,
      additions: meta?.additions,
      deletions: meta?.deletions,
      binary: file.binary,
      truncated: meta?.truncated,
      changeType: meta?.change_type ?? file.change_type
    }
  })
})

function onStageHunk(payload: { filePath: string; hunkHeader: string | null }) {
  const next = new Set(selectedPaths.value)
  next.add(payload.filePath)
  selectedPaths.value = next
  void requestIndexApproval('stage')
}

function onDiscardHunk(payload: { filePath: string; hunkHeader: string | null }) {
  feedback.value = `${t('context.gitDiffDiscardHunk')} ${payload.filePath}: ${t('context.gitDiscardNeedsApproval')}`
}

function onSwitchWorktree(path: string) {
  feedback.value = `${t('context.gitWorktreeSwitch')}: ${path}`
}

function onCreateWorktree(payload: { branch: string; path: string }) {
  feedback.value = `${t('context.gitWorktreeCreate')} ${payload.branch} → ${payload.path}: ${t('context.gitWorktreeNeedsApproval')}`
}

function onRemoveWorktree(path: string) {
  feedback.value = `${t('context.gitWorktreeRemove')} ${path}: ${t('context.gitWorktreeNeedsApproval')}`
}

watch(() => props.currentProjectPath, () => {
  indexApprovalId.value = null
  indexAction.value = null
  commitApprovalId.value = null
  pushApprovalId.value = null
  void loadGit()
}, { immediate: true })

watch(sections, () => {
  if (!sections.value.some((section) => section.id === selectedSectionId.value)) {
    selectedSectionId.value = sections.value[0]?.id ?? 'working_tree'
  }
})

watch(activeParsedFiles, () => {
  if (!activeParsedFiles.value.some((file) => file.path === selectedFilePath.value)) {
    selectedFilePath.value = activeParsedFiles.value[0]?.path ?? null
  }
})
</script>

<template>
  <section class="panel git-manager-view">
    <div class="git-panel-head">
      <div class="panel-title">
        <GitBranch :size="15" />
        <span>{{ t('context.git') }}</span>
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

    <div v-if="!props.currentProjectPath" class="diff-box">
      <span>{{ t('context.diffPlaceholder') }}</span>
    </div>
    <div v-else-if="loading" class="diff-box">
      <span>{{ t('context.loadingGitPlan') }}</span>
    </div>
    <div v-else-if="error" class="git-plan-state risky">
      <AlertTriangle :size="15" />
      <span>{{ error }}</span>
    </div>

    <template v-else>
      <div class="git-summary-grid">
        <div>
          <span>{{ t('context.gitBranch') }}</span>
          <strong>{{ previewData.branch ?? '-' }}</strong>
        </div>
        <div>
          <span>{{ t('context.gitUpstream') }}</span>
          <strong>{{ previewData.upstream ?? '-' }}</strong>
        </div>
        <div>
          <span>{{ t('context.gitAheadBehind') }}</span>
          <strong>{{ previewData.ahead ?? 0 }} / {{ previewData.behind ?? 0 }}</strong>
        </div>
        <div>
          <span>{{ t('context.gitChangedFiles') }}</span>
          <strong>{{ totalChanges }}</strong>
        </div>
      </div>

      <div class="git-top-tabs">
        <button
          class="git-top-tab"
          :class="{ active: topTab === 'changes' }"
          @click="topTab = 'changes'"
        >
          <FileCode2 :size="13" />
          <span>{{ t('context.gitTabChanges') }}</span>
        </button>
        <button
          class="git-top-tab"
          :class="{ active: topTab === 'compare' }"
          @click="topTab = 'compare'"
        >
          <GitCompare :size="13" />
          <span>{{ t('context.gitTabCompare') }}</span>
        </button>
        <button
          class="git-top-tab"
          :class="{ active: topTab === 'worktrees' }"
          @click="topTab = 'worktrees'"
        >
          <GitBranch :size="13" />
          <span>{{ t('context.gitTabWorktrees') }}</span>
        </button>
      </div>

      <template v-if="topTab === 'changes'">
      <div v-if="hasRepositoryDetails" class="git-info-grid">
        <section v-if="diffStatLines.length" class="git-info-panel">
          <div class="git-panel-subtitle">{{ t('context.gitDiffStat') }}</div>
          <code v-for="line in diffStatLines" :key="line">{{ line }}</code>
        </section>
        <section v-if="remotes.length" class="git-info-panel">
          <div class="git-panel-subtitle">{{ t('context.gitRemotes') }}</div>
          <span v-for="remote in remotes" :key="remote">{{ remote }}</span>
        </section>
        <section v-if="recentCommits.length" class="git-info-panel">
          <div class="git-panel-subtitle">{{ t('context.gitRecentCommits') }}</div>
          <code v-for="commit in recentCommits" :key="commit">{{ commit }}</code>
        </section>
        <section v-if="suggestedCommands.length" class="git-info-panel">
          <div class="git-panel-subtitle">{{ t('context.gitSuggestedCommands') }}</div>
          <code v-for="command in suggestedCommands" :key="command">{{ command }}</code>
        </section>
      </div>

      <div class="git-sections">
        <button
          v-for="section in sections"
          :key="section.id"
          class="git-section-tab"
          :class="{ active: selectedSectionId === section.id }"
          @click="selectedSectionId = section.id"
        >
          <GitCompare :size="13" />
          <span>{{ section.title }}</span>
          <small>{{ section.file_count }}</small>
        </button>
      </div>

      <div v-if="activeSection?.notices?.length" class="git-notices">
        <span v-for="notice in activeSection.notices" :key="notice">{{ notice }}</span>
      </div>

      <div class="git-diff-wrap">
        <DiffViewer
          v-if="diffEntries.length > 0"
          :files="diffEntries"
          :selected-file-path="selectedFilePath"
          :enable-hunk-actions="true"
          @update:selected-file-path="selectedFilePath = $event"
          @stage-hunk="onStageHunk"
          @discard-hunk="onDiscardHunk"
        />
        <div v-else class="git-empty-inline">
          {{ parsedActiveSection.notice ?? t('context.gitNoDiff') }}
        </div>
      </div>

      <div class="git-status-list">
        <div class="git-panel-subtitle">
          <GitCommitHorizontal :size="14" />
          <span>{{ t('context.gitCommit') }}</span>
        </div>
        <label
          v-for="file in statusFiles"
          :key="file.path"
          class="git-commit-file"
        >
          <input type="checkbox" :checked="selectedPaths.has(file.path)" @change="togglePath(file.path)" />
          <span>{{ file.path }}</span>
          <small>{{ file.status }}</small>
        </label>
        <div class="git-action-row">
          <button
            class="secondary-button git-action-button git-stage-approval-button"
            :disabled="operationLoading || !canRequestIndexApproval"
            @click="requestIndexApproval('stage')"
          >
            <ShieldCheck :size="14" />
            <span>{{ t('context.gitRequestStageApproval') }}</span>
          </button>
          <button
            class="secondary-button git-action-button git-unstage-approval-button"
            :disabled="operationLoading || !canRequestIndexApproval"
            @click="requestIndexApproval('unstage')"
          >
            <ShieldCheck :size="14" />
            <span>{{ t('context.gitRequestUnstageApproval') }}</span>
          </button>
        </div>
        <div class="git-action-row">
          <button
            class="secondary-button git-action-button git-index-execute-button"
            :disabled="operationLoading || indexApproval?.status !== 'approved'"
            @click="executeApprovedIndexUpdate"
          >
            <CheckCircle2 :size="14" />
            <span>{{ t('context.gitExecuteIndexUpdate') }}</span>
          </button>
        </div>
        <span class="git-action-note">{{ t('context.gitIndexAction') }}: {{ indexAction ?? '-' }} / {{ t('context.gitApprovalStatus') }}: {{ approvalStatusLabel(indexApproval) }}</span>
        <div v-if="canDecideIndexApproval" class="git-approval-inline-actions">
          <button class="icon-button approve" :title="t('approval.approve')" @click="decideGitApproval(indexApproval, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="decideGitApproval(indexApproval, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <CommitMessageEditor
          v-model="commitMessage"
          :recent-commits="recentCommits"
        />
        <div class="git-action-row">
          <button
            class="secondary-button git-action-button git-commit-approval-button"
            :disabled="operationLoading || !canRequestCommitApproval"
            @click="requestCommitApproval"
          >
            <ShieldCheck :size="14" />
            <span>{{ t('context.gitRequestCommitApproval') }}</span>
          </button>
          <button
            class="secondary-button git-action-button git-commit-execute-button"
            :disabled="operationLoading || commitApproval?.status !== 'approved'"
            @click="executeApprovedCommit"
          >
            <CheckCircle2 :size="14" />
            <span>{{ t('context.gitExecuteCommit') }}</span>
          </button>
        </div>
        <span class="git-action-note">{{ t('context.gitApprovalStatus') }}: {{ approvalStatusLabel(commitApproval) }}</span>
        <div v-if="canDecideCommitApproval" class="git-approval-inline-actions">
          <button class="icon-button approve" :title="t('approval.approve')" @click="decideGitApproval(commitApproval, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="decideGitApproval(commitApproval, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
      </div>

      <div class="git-push-panel">
        <div class="git-panel-subtitle">
          <Upload :size="14" />
          <span>{{ t('context.gitPushReadiness') }}</span>
        </div>
        <div class="git-plan-status" :class="{ ready: pushReady, blocked: !pushReady }">
          <component :is="pushReady ? CheckCircle2 : AlertTriangle" :size="18" />
          <div>
            <strong>{{ pushReady ? t('context.gitPushReady') : t('context.gitPushBlocked') }}</strong>
            <span>{{ pushPlan?.summary }}</span>
          </div>
        </div>
        <div v-if="pushBlockers.length > 0" class="git-plan-tags">
          <small v-for="blocker in pushBlockers" :key="blocker">{{ blocker }}</small>
        </div>
        <div class="git-action-row">
          <button
            class="secondary-button git-action-button git-push-approval-button"
            :disabled="operationLoading || !canRequestPushApproval"
            @click="requestPushApproval"
          >
            <ShieldCheck :size="14" />
            <span>{{ t('context.gitRequestPushApproval') }}</span>
          </button>
          <button
            class="secondary-button git-action-button git-push-execute-button"
            :disabled="operationLoading || pushApproval?.status !== 'approved'"
            @click="executeApprovedPush"
          >
            <Upload :size="14" />
            <span>{{ t('context.gitExecutePush') }}</span>
          </button>
        </div>
        <span class="git-action-note">{{ t('context.gitApprovalStatus') }}: {{ approvalStatusLabel(pushApproval) }}</span>
        <div v-if="canDecidePushApproval" class="git-approval-inline-actions">
          <button class="icon-button approve" :title="t('approval.approve')" @click="decideGitApproval(pushApproval, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="decideGitApproval(pushApproval, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
      </div>

      <div v-if="worktrees.length > 0" class="git-worktrees">
        <div class="git-panel-subtitle">
          <GitPullRequest :size="14" />
          <span>{{ t('context.gitWorktrees') }}</span>
        </div>
        <div v-for="worktree in worktrees" :key="String(worktree.path)" class="git-worktree-row">
          <strong>{{ worktree.branch ?? worktree.detached ?? '-' }}</strong>
          <span>{{ worktree.path }}</span>
        </div>
      </div>
      </template>

      <CommitCompare
        v-else-if="topTab === 'compare'"
        :cwd="props.currentProjectPath!"
      />

      <WorktreeManager
        v-else-if="topTab === 'worktrees'"
        :cwd="props.currentProjectPath!"
        :current-path="props.currentProjectPath"
        @switch="onSwitchWorktree"
        @create="onCreateWorktree"
        @remove="onRemoveWorktree"
      />

      <div v-if="feedback" class="git-plan-approval">
        <ShieldCheck :size="14" />
        <span>{{ feedback }}</span>
      </div>

      <div class="git-plan-approval">
        <ShieldCheck :size="14" />
        <span>{{ t('context.gitPlanApproval') }}</span>
      </div>
    </template>
  </section>
</template>

<style scoped>
.git-top-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.git-top-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  color: var(--text-secondary);
  background: var(--bg-secondary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.git-top-tab:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.git-top-tab.active {
  color: var(--text-primary);
  border-color: var(--bg-selected-outline);
  background: var(--bg-selected);
}

.git-diff-wrap {
  min-height: 320px;
  height: 480px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  overflow: hidden;
  background: var(--bg-primary);
}
</style>
