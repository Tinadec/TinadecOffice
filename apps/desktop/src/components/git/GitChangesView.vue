<script setup lang="ts">
import {
  AlertTriangle,
  ArrowDownToLine,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Download,
  FilePlus,
  FileText,
  FileX,
  FileCog,
  Folder,
  FolderOpen,
  GitCommitHorizontal,
  List,
  FolderTree,
  Loader2,
  Minus,
  Plus,
  RotateCcw,
  Sparkles,
  ShieldCheck,
  ShieldX,
  Trash2,
  Upload,
  RefreshCw,
  ExternalLink,
} from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto } from '../../api'
import {
  type GitStatusFile,
  type GitDiffSection,
  statusToLabel,
  statusColor,
} from '../../composables/useGitOperation'
import { useAiCommitMessage } from '../../composables/useAiCommitMessage'
import { useAiChangeAnalysis, type AiRiskLevel } from '../../composables/useAiChangeAnalysis'
import CommitMessageEditor from './CommitMessageEditor.vue'
import DiffViewer from './DiffViewer.vue'
import { UiCheckbox, UiIslandCard } from '../ui'
import {
  reconstructFromHunks,
  buildFileTree,
  type DiffFileEntry,
  type GitTreeNode,
} from './diffUtils'
import GitTreeNodeRow from './GitTreeNodeRow.vue'
import { parseUnifiedDiff } from '../../gitDiffParser'
import { buildGitIndexPatch, changeBlockLineIds } from '../../gitIndexPatch'

interface Props {
  cwd: string | undefined
  sessionId: string | null
  // Git operation state (injected from parent composable)
  loading: boolean
  operationLoading: boolean
  statusFiles: GitStatusFile[]
  commitMessage: string
  selectedPaths: Set<string>
  selectAll: boolean
  selectAllIndeterminate: boolean
  // Diff sections
  diffText: string
  diffFiles: Array<{
    path: string
    previous_path?: string | null
    change_type: string
    additions: number
    deletions: number
    binary: boolean
    truncated: boolean
  }>
  stagedDiffText: string
  stagedDiffFiles: Array<{
    path: string
    previous_path?: string | null
    change_type: string
    additions: number
    deletions: number
    binary: boolean
    truncated: boolean
  }>
  // Push state
  pushReady: boolean
  pushBlockers: string[]
  hasPushCandidate: boolean
  canRequestPushApproval: boolean
  canRequestPullApproval: boolean
  canRequestFetchApproval: boolean
  behind: number
  stagedCount: number
  commitUsesSelectedPaths: boolean
  pullStrategy: 'ff-only' | 'merge' | 'rebase'
  // Approvals
  indexApproval: ApprovalDto | null
  commitApproval: ApprovalDto | null
  pushApproval: ApprovalDto | null
  pullApproval: ApprovalDto | null
  fetchApproval: ApprovalDto | null
  discardApproval: ApprovalDto | null
  resolveConflictApproval: ApprovalDto | null
  canRequestIndexApproval: boolean
  canRequestDiscardApproval: boolean
  canRequestCommitApproval: boolean
  canDecideIndexApproval: boolean
  canDecideCommitApproval: boolean
  canDecidePushApproval: boolean
  canDecidePullApproval: boolean
  canDecideFetchApproval: boolean
  canDecideDiscardApproval: boolean
  canDecideResolveConflictApproval: boolean
  recentCommits: string[]
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:commitMessage': [value: string]
  'update:pullStrategy': [value: 'ff-only' | 'merge' | 'rebase']
  'refresh': []
  'toggle-path': [path: string]
  'toggle-select-all': []
  'request-stage': [selection?: { patch?: string; paths?: string[] }]
  'request-unstage': [selection?: { patch?: string; paths?: string[] }]
  'execute-index': []
  'request-commit': []
  'execute-commit': []
  'request-discard': [paths: string[], includeUntracked: boolean]
  'execute-discard': []
  'request-push': []
  'execute-push': []
  'request-pull': []
  'execute-pull': []
  'request-fetch': []
  'execute-fetch': []
  'request-resolve-conflict': [path: string, strategy: 'ours' | 'theirs' | 'both']
  'execute-resolve-conflict': []
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected']
}>()

const { t } = useI18n()

// ---- Diff preview ----
const showDiffPreview = ref(false)
const selectedDiffFile = ref<string | null>(null)
const indexMode = ref<'stage' | 'unstage'>('stage')

const indexDiffText = computed(() => indexMode.value === 'stage' ? props.diffText : props.stagedDiffText)
const indexDiffFiles = computed(() => indexMode.value === 'stage' ? props.diffFiles : props.stagedDiffFiles)
const parsedDiff = computed(() => parseUnifiedDiff(indexDiffText.value))
const selectedLineIds = ref<Set<string>>(new Set())
const selectedTextPatch = computed(() => buildGitIndexPatch(parsedDiff.value, selectedLineIds.value))

const diffSections = computed<GitDiffSection[]>(() => [
  {
    id: 'working-tree',
    kind: 'working_tree',
    title: 'Working tree diff',
    diff: indexDiffText.value,
    files: indexDiffFiles.value,
    file_count: indexDiffFiles.value.length,
    additions: indexDiffFiles.value.reduce((a, f) => a + (f.additions ?? 0), 0),
    deletions: indexDiffFiles.value.reduce((a, f) => a + (f.deletions ?? 0), 0),
    notices: [],
  },
])

const diffEntries = computed<DiffFileEntry[]>(() => {
  return parsedDiff.value.files.map((file) => {
    const meta = indexDiffFiles.value.find((item) => item.path === file.path)
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
      changeType: meta?.change_type ?? file.change_type,
    }
  })
})

watch(diffEntries, (entries) => {
  if (!entries.some((e) => e.path === selectedDiffFile.value)) {
    selectedDiffFile.value = entries[0]?.path ?? null
  }
}, { immediate: true })

watch(indexDiffText, () => {
  selectedLineIds.value = new Set()
})

function toggleChangeLine(hunkId: string, lineId: string) {
  const hunk = parsedDiff.value.files.flatMap((file) => file.hunks).find((item) => item.id === hunkId)
  if (!hunk) return
  const block = changeBlockLineIds(hunk, lineId)
  if (block.length === 0) return
  const next = new Set(selectedLineIds.value)
  const shouldSelect = !block.every((id) => next.has(id))
  for (const id of block) shouldSelect ? next.add(id) : next.delete(id)
  selectedLineIds.value = next
}

function toggleHunk(hunkId: string) {
  const hunk = parsedDiff.value.files.flatMap((file) => file.hunks).find((item) => item.id === hunkId)
  if (!hunk) return
  const ids = hunk.lines.filter((line) => line.change !== 'context').map((line) => line.id)
  const next = new Set(selectedLineIds.value)
  const shouldSelect = !ids.every((id) => next.has(id))
  for (const id of ids) shouldSelect ? next.add(id) : next.delete(id)
  selectedLineIds.value = next
}

function requestSelectedLines() {
  if (!selectedTextPatch.value) return
  if (indexMode.value === 'stage') emit('request-stage', { patch: selectedTextPatch.value })
  else emit('request-unstage', { patch: selectedTextPatch.value })
}

function requestFileHunks(path: string) {
  const next = new Set(selectedLineIds.value)
  for (const file of parsedDiff.value.files.filter((file) => file.path === path)) {
    for (const hunk of file.hunks) {
      for (const line of hunk.lines) if (line.change !== 'context') next.add(line.id)
    }
  }
  selectedLineIds.value = next
  const patch = buildGitIndexPatch(parsedDiff.value, next)
  if (patch) emit('request-stage', { patch })
}

// ---- AI commit message ----
const sessionIdRef = computed(() => props.sessionId)
const {
  generating: aiGenerating,
  aiSuggestion,
  canGenerate: canAiGenerate,
  generate: aiGenerate,
  generateLocalSuggestion,
} = useAiCommitMessage(sessionIdRef)

const showAiPanel = ref(false)

// ---- AI change analysis ----
const {
  analyzing: aiAnalyzing,
  analysis: aiAnalysis,
  canAnalyze: canAiAnalyze,
  analyze: aiAnalyze,
} = useAiChangeAnalysis(sessionIdRef)

const showAiAnalysis = ref(false)

async function handleAiAnalyze() {
  showAiAnalysis.value = true
  await aiAnalyze(props.statusFiles, diffSections.value, props.statusFiles[0]?.path)
}

function riskLabel(level: AiRiskLevel): string {
  switch (level) {
    case 'low': return t('context.gitAiRiskLow')
    case 'medium': return t('context.gitAiRiskMedium')
    case 'high': return t('context.gitAiRiskHigh')
    case 'critical': return t('context.gitAiRiskCritical')
  }
}

function riskIcon(level: AiRiskLevel) {
  switch (level) {
    case 'low': return CheckCircle2
    case 'medium':
    case 'high': return AlertTriangle
    case 'critical': return ShieldX
  }
}

async function handleAiGenerate() {
  showAiPanel.value = true
  // Generate local suggestion first for immediate feedback
  const local = generateLocalSuggestion(props.statusFiles, diffSections.value)
  if (local) {
    emit('update:commitMessage', local.fullMessage)
  }
  // Then try AI generation with diff context
  await aiGenerate(props.statusFiles, diffSections.value, props.statusFiles[0]?.path)
  if (aiSuggestion.value) {
    emit('update:commitMessage', aiSuggestion.value.fullMessage)
  }
}

// ---- Commit convention check ----
const conventionCheck = computed(() => {
  const msg = props.commitMessage.trim()
  if (!msg) return null
  const lines = msg.split(/\r?\n/)
  const header = lines[0] ?? ''
  const match = /^(\w+)(?:\(([^)]*)\))?!?\s*:\s*(.+)$/.exec(header)
  if (!match) {
    return {
      valid: false,
      message: t('context.gitConvInvalidHeader'),
    }
  }
  const subject = match[3] ?? ''
  if (subject.length > 72) {
    return {
      valid: false,
      message: t('context.gitConvSubjectTooLong'),
    }
  }
  if (subject.length > 50) {
    return {
      valid: true,
      warning: t('context.gitConvSubjectWarn'),
    }
  }
  const validTypes = ['feat', 'fix', 'docs', 'style', 'refactor', 'perf', 'test', 'build', 'ci', 'chore', 'revert']
  if (!validTypes.includes(match[1] ?? '')) {
    return {
      valid: false,
      message: t('context.gitConvInvalidType'),
    }
  }
  // Check body line length
  const bodyLines = lines.slice(1).filter((l) => l.trim())
  const longBodyLines = bodyLines.filter((l) => l.length > 72)
  if (longBodyLines.length > 0) {
    return {
      valid: true,
      warning: t('context.gitConvBodyTooLong'),
    }
  }
  return { valid: true }
})

// ---- File status icon helper ----
function statusIcon(status?: string) {
  const label = statusToLabel(status)
  switch (label) {
    case 'A': return FilePlus
    case 'D': return FileX
    case 'R':
    case 'C': return FileCog
    case 'M': return FileText
    default: return FileText
  }
}

function statusColorClass(status?: string) {
  return `status-${statusColor(status)}`
}

// ---- Collapsible sections ----
const filesExpanded = ref(true)
const commitExpanded = ref(true)
const pushExpanded = ref(true)

// ---- Discard inline confirm (second explicit step before approval) ----
const discardConfirm = ref<{ paths: string[]; includeUntracked: boolean } | null>(null)

function isUntrackedTarget(path: string): boolean {
  const file = props.statusFiles.find((item) => item.path === path)
  return file?.is_untracked === true || (file?.status ?? file?.unstaged_status) === '?'
}

function openDiscardConfirm(paths: string[]) {
  const targets = [...new Set(paths.map((p) => p.trim()).filter(Boolean))]
  if (targets.length === 0 || props.operationLoading) return
  discardConfirm.value = {
    paths: targets,
    // 未跟踪文件必须勾选才会删除：默认按目标是否含未跟踪自动勾选。
    includeUntracked: targets.some((p) => isUntrackedTarget(p)),
  }
}

// ---- Conflict handling ----
const conflictedFiles = computed(() => props.statusFiles.filter((f) => f.is_conflicted))
const hasConflicts = computed(() => conflictedFiles.value.length > 0)
const sortedStatusFiles = computed(() => {
  const conflicts = props.statusFiles.filter((f) => f.is_conflicted)
  const others = props.statusFiles.filter((f) => !f.is_conflicted)
  return [...conflicts, ...others]
})

function stageSingleFile(path: string) {
  emit('toggle-path', path)
  if (!props.selectedPaths.has(path)) {
    // will be added
  }
  emit('request-stage')
}

function unstageSingleFile(path: string) {
  emit('toggle-path', path)
  emit('request-unstage')
}

function stageAllUnstaged() {
  unstagedFiles.value.forEach((f) => {
    if (!props.selectedPaths.has(f.path)) {
      emit('toggle-path', f.path)
    }
  })
  emit('request-stage')
}

function unstageAllStaged() {
  stagedFiles.value.forEach((f) => {
    if (!props.selectedPaths.has(f.path)) {
      emit('toggle-path', f.path)
    }
  })
  emit('request-unstage')
}

function stageFolder(paths: string[]) {
  paths.forEach((p) => {
    if (!props.selectedPaths.has(p)) {
      emit('toggle-path', p)
    }
  })
  emit('request-stage')
}

function unstageFolder(paths: string[]) {
  paths.forEach((p) => {
    if (!props.selectedPaths.has(p)) {
      emit('toggle-path', p)
    }
  })
  emit('request-unstage')
}

function selectAndOpenDiff(path: string) {
  selectedDiffFile.value = path
  showDiffPreview.value = true
}


// ---- Sync: pull-only never pushes; dirty tree disables pull with an explicit reason ----
const hasDirtyTree = computed(() => props.statusFiles.length > 0)
const pullDisabledReason = computed(() => {
  if (props.operationLoading) return ''
  if (!props.canRequestPullApproval) {
    if (hasDirtyTree.value) return t('context.gitPullDirtyHint')
    if (props.behind <= 0) return t('context.gitPullUpToDate')
    return t('context.gitPullNeedsSession')
  }
  return ''
})

// ---- View Mode (Tree / Flat) & Sections State ----
const viewMode = ref<'tree' | 'flat'>((localStorage.getItem('git_changes_view_mode') as 'tree' | 'flat') || 'tree')
function toggleViewMode() {
  viewMode.value = viewMode.value === 'tree' ? 'flat' : 'tree'
  localStorage.setItem('git_changes_view_mode', viewMode.value)
}

const stagedExpanded = ref(true)
const unstagedExpanded = ref(true)
const stagedFiles = computed(() => props.statusFiles.filter((f) => f.is_staged))
const unstagedFiles = computed(() => props.statusFiles.filter((f) => !f.is_staged))

const stagedTree = computed(() => buildFileTree(stagedFiles.value))
const unstagedTree = computed(() => buildFileTree(unstagedFiles.value))

const collapsedFolders = ref<Set<string>>(new Set())
function toggleFolder(path: string) {
  const next = new Set(collapsedFolders.value)
  if (next.has(path)) next.delete(path)
  else next.add(path)
  collapsedFolders.value = next
}
function collapseAllFolders() {
  const allFolders = new Set<string>()
  function collect(nodes: GitTreeNode[]) {
    for (const node of nodes) {
      if (node.isFolder) {
        allFolders.add(node.path)
        if (node.children) collect(node.children)
      }
    }
  }
  collect(stagedTree.value)
  collect(unstagedTree.value)
  collapsedFolders.value = allFolders
}
function expandAllFolders() {
  collapsedFolders.value = new Set()
}

// ---- Split Commit Button State ----
const splitMenuOpen = ref(false)
function toggleSplitMenu() {
  splitMenuOpen.value = !splitMenuOpen.value
}
function handlePrimaryCommit() {
  if (props.commitApproval?.status === 'approved') {
    emit('execute-commit')
  } else {
    emit('request-commit')
  }
}
function handleCommitAndPush() {
  splitMenuOpen.value = false
  if (props.commitApproval?.status === 'approved') {
    emit('execute-commit')
    emit('request-push')
  } else {
    emit('request-commit')
    emit('request-push')
  }
}
function handleCommitKeydown(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
    e.preventDefault()
    handlePrimaryCommit()
  }
}

</script>

<template>
  <div class="git-changes-view">
    <!-- File changes section -->
    <UiIslandCard padding="none">
      <button class="git-section-header" @click="filesExpanded = !filesExpanded">
        <component :is="filesExpanded ? ChevronDown : ChevronRight" :size="14" />
        <GitCommitHorizontal :size="14" />
        <span>{{ t('context.gitChanges') }}</span>
        <span class="git-section-count">{{ statusFiles.length }}</span>
        <div class="git-section-actions" @click.stop>
          <UiCheckbox
            :model-value="selectAll"
            :indeterminate="selectAllIndeterminate"
            :aria-label="t('context.gitChanges')"
            class="git-checkbox"
            @update:model-value="emit('toggle-select-all')"
          />
          <button
            class="icon-button git-section-refresh"
            :title="t('context.refreshGitPlan')"
            :disabled="loading"
            @click="emit('refresh')"
          >
            <RefreshCw :size="12" :class="{ spinning: loading }" />
          </button>
        </div>
      </button>

      <div v-show="filesExpanded" class="git-section-body">
        <div v-if="hasConflicts" class="git-conflict-banner">
          <AlertTriangle :size="14" />
          <span>{{ conflictedFiles.length }} file{{ conflictedFiles.length === 1 ? '' : 's' }} with merge conflicts require resolution.</span>
        </div>
        <!-- Controls bar: Tree/List toggle + Select All + Refresh -->
        <div class="git-view-controls-bar">
          <div class="git-mode-switch-group">
            <button
              type="button"
              class="git-mode-btn"
              :class="{ active: viewMode === 'tree' }"
              :title="t('context.gitViewModeTree')"
              @click="viewMode = 'tree'"
            >
              <FolderTree :size="13" />
              <span>{{ t('context.gitViewModeTree') }}</span>
            </button>
            <button
              type="button"
              class="git-mode-btn"
              :class="{ active: viewMode === 'list' }"
              :title="t('context.gitViewModeList')"
              @click="viewMode = 'list'"
            >
              <List :size="13" />
              <span>{{ t('context.gitViewModeList') }}</span>
            </button>
          </div>
        </div>

        <div v-if="statusFiles.length === 0" class="git-empty-state">
          {{ t('context.gitNoChanges') }}
        </div>
        <div v-else class="git-sections-container">
          <!-- STAGED CHANGES SECTION -->
          <div class="git-sub-section staged-sub-section">
            <div class="git-sub-header" @click="stagedExpanded = !stagedExpanded">
              <component :is="stagedExpanded ? ChevronDown : ChevronRight" :size="13" />
              <span class="git-sub-title">{{ t('context.gitStagedChanges') }}</span>
              <span class="git-sub-count">{{ stagedFiles.length }}</span>
              <div class="git-sub-actions" @click.stop>
                <button
                  type="button"
                  class="git-sub-action-btn"
                  :title="t('context.gitUnstageAll')"
                  :disabled="operationLoading || stagedFiles.length === 0"
                  @click.stop="emit('request-unstage')"
                >
                  <Minus :size="12" />
                </button>
              </div>
            </div>

            <div v-show="stagedExpanded" class="git-sub-body">
              <div v-if="stagedFiles.length === 0" class="git-sub-empty">
                0 files staged
              </div>
              <!-- Staged Tree View -->
              <div v-else-if="viewMode === 'tree'" class="git-tree-container">
                <GitTreeNodeRow
                  v-for="rootNode in stagedTreeRoots"
                  :key="rootNode.path"
                  :node="rootNode"
                  :depth="0"
                  :selected-paths="selectedPaths"
                  :active-diff-path="selectedDiffFile?.path"
                  :operation-loading="operationLoading"
                  @toggle-select="emit('toggle-path', $event)"
                  @select-diff="selectAndOpenDiff($event)"
                  @stage-file="emit('toggle-path', $event); emit('request-stage')"
                  @unstage-file="emit('toggle-path', $event); emit('request-unstage')"
                  @discard-file="openDiscardConfirm([$event])"
                  @stage-folder="stageFolder($event)"
                  @unstage-folder="unstageFolder($event)"
                />
              </div>
              <!-- Staged Flat List -->
              <div v-else class="git-file-list">
                <div
                  v-for="file in stagedFiles"
                  :key="file.path"
                  class="git-file-row"
                  :class="[statusColorClass(file.status ?? file.unstaged_status), { 'is-active-diff': selectedDiffFile?.path === file.path }]"
                >
                  <div class="git-file-row-main" @click="selectAndOpenDiff(file.path)">
                    <UiCheckbox
                      :model-value="selectedPaths.has(file.path)"
                      :aria-label="file.path"
                      class="git-checkbox"
                      @click.stop
                      @update:model-value="emit('toggle-path', file.path)"
                    />
                    <component :is="statusIcon(file.status ?? file.unstaged_status)" :size="13" class="git-file-icon" />
                    <span class="git-file-path" :title="file.path">{{ file.path }}</span>
                    <span class="git-file-status-badge">{{ statusToLabel(file.status ?? file.unstaged_status) }}</span>
                    
                    <div class="git-row-hover-actions" @click.stop>
                      <button
                        type="button"
                        class="git-hover-btn"
                        :title="t('context.gitUnstageSelected')"
                        :disabled="operationLoading"
                        @click.stop="emit('toggle-path', file.path); emit('request-unstage')"
                      >
                        <Minus :size="12" />
                      </button>
                      <button
                        v-if="!file.is_conflicted"
                        type="button"
                        class="git-hover-btn is-discard"
                        :title="t('context.gitDiscardFile')"
                        :disabled="operationLoading"
                        @click.stop="openDiscardConfirm([file.path])"
                      >
                        <RotateCcw :size="12" />
                      </button>
                      <button
                        type="button"
                        class="git-hover-btn"
                        :title="t('context.gitQuickDiff')"
                        @click.stop="selectAndOpenDiff(file.path)"
                      >
                        <ExternalLink :size="12" />
                      </button>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <!-- UNSTAGED CHANGES SECTION -->
          <div class="git-sub-section unstaged-sub-section">
            <div class="git-sub-header" @click="unstagedExpanded = !unstagedExpanded">
              <component :is="unstagedExpanded ? ChevronDown : ChevronRight" :size="13" />
              <span class="git-sub-title">{{ t('context.gitUnstagedChanges') }}</span>
              <span class="git-sub-count">{{ unstagedFiles.length }}</span>
              <div class="git-sub-actions" @click.stop>
                <button
                  type="button"
                  class="git-sub-action-btn"
                  :title="t('context.gitStageAll')"
                  :disabled="operationLoading || unstagedFiles.length === 0"
                  @click.stop="emit('request-stage')"
                >
                  <Plus :size="12" />
                </button>
                <button
                  type="button"
                  class="git-sub-action-btn is-discard"
                  :title="t('context.gitDiscardAll')"
                  :disabled="operationLoading || unstagedFiles.length === 0"
                  @click.stop="openDiscardConfirm(unstagedFiles.map((f) => f.path))"
                >
                  <RotateCcw :size="12" />
                </button>
              </div>
            </div>

            <div v-show="unstagedExpanded" class="git-sub-body">
              <div v-if="unstagedFiles.length === 0" class="git-sub-empty">
                0 uncommitted changes
              </div>
              <!-- Unstaged Tree View -->
              <div v-else-if="viewMode === 'tree'" class="git-tree-container">
                <GitTreeNodeRow
                  v-for="rootNode in unstagedTreeRoots"
                  :key="rootNode.path"
                  :node="rootNode"
                  :depth="0"
                  :selected-paths="selectedPaths"
                  :active-diff-path="selectedDiffFile?.path"
                  :operation-loading="operationLoading"
                  @toggle-select="emit('toggle-path', $event)"
                  @select-diff="selectAndOpenDiff($event)"
                  @stage-file="emit('toggle-path', $event); emit('request-stage')"
                  @unstage-file="emit('toggle-path', $event); emit('request-unstage')"
                  @discard-file="openDiscardConfirm([$event])"
                  @stage-folder="stageFolder($event)"
                  @unstage-folder="unstageFolder($event)"
                />
              </div>
              <!-- Unstaged Flat List -->
              <div v-else class="git-file-list">
                <div
                  v-for="file in unstagedFiles"
                  :key="file.path"
                  class="git-file-row"
                  :class="[statusColorClass(file.status ?? file.unstaged_status), { 'is-active-diff': selectedDiffFile?.path === file.path }]"
                >
                  <div class="git-file-row-main" @click="selectAndOpenDiff(file.path)">
                    <UiCheckbox
                      :model-value="selectedPaths.has(file.path)"
                      :aria-label="file.path"
                      class="git-checkbox"
                      @click.stop
                      @update:model-value="emit('toggle-path', file.path)"
                    />
                    <component :is="statusIcon(file.status ?? file.unstaged_status)" :size="13" class="git-file-icon" />
                    <span class="git-file-path" :title="file.path">{{ file.path }}</span>
                    <span class="git-file-status-badge">{{ statusToLabel(file.status ?? file.unstaged_status) }}</span>

                    <div class="git-row-hover-actions" @click.stop>
                      <button
                        type="button"
                        class="git-hover-btn"
                        :title="t('context.gitStageSelected')"
                        :disabled="operationLoading"
                        @click.stop="emit('toggle-path', file.path); emit('request-stage')"
                      >
                        <Plus :size="12" />
                      </button>
                      <button
                        v-if="!file.is_conflicted"
                        type="button"
                        class="git-hover-btn is-discard"
                        :title="t('context.gitDiscardFile')"
                        :disabled="operationLoading"
                        @click.stop="openDiscardConfirm([file.path])"
                      >
                        <RotateCcw :size="12" />
                      </button>
                      <button
                        type="button"
                        class="git-hover-btn"
                        :title="t('context.gitQuickDiff')"
                        @click.stop="selectAndOpenDiff(file.path)"
                      >
                        <ExternalLink :size="12" />
                      </button>
                    </div>
                  </div>

                  <div v-if="file.is_conflicted" class="git-conflict-actions">
                    <button
                      class="git-conflict-btn"
                      :disabled="operationLoading"
                      :title="t('context.gitConflictOurs')"
                      @click="emit('request-resolve-conflict', file.path, 'ours')"
                    >
                      {{ t('context.gitConflictOurs') }}
                    </button>
                    <button
                      class="git-conflict-btn"
                      :disabled="operationLoading"
                      :title="t('context.gitConflictTheirs')"
                      @click="emit('request-resolve-conflict', file.path, 'theirs')"
                    >
                      {{ t('context.gitConflictTheirs') }}
                    </button>
                    <button
                      class="git-conflict-btn"
                      :disabled="operationLoading"
                      :title="t('context.gitConflictBoth')"
                      @click="emit('request-resolve-conflict', file.path, 'both')"
                    >
                      {{ t('context.gitConflictBoth') }}
                    </button>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- Conflict resolution approval -->
        <div v-if="resolveConflictApproval" class="git-conflict-approval">
          <div class="git-conflict-approval-info">
            <ShieldCheck :size="13" />
            <span>{{ resolveConflictApproval.summary }}</span>
            <span class="git-conflict-approval-status">{{ resolveConflictApproval.status }}</span>
          </div>
          <div class="git-conflict-approval-actions">
            <div v-if="canDecideResolveConflictApproval" class="git-approval-decide">
              <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', resolveConflictApproval!, 'approved')">
                <CheckCircle2 :size="14" />
              </button>
              <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', resolveConflictApproval!, 'rejected')">
                <ShieldX :size="14" />
              </button>
            </div>
            <button
              v-if="resolveConflictApproval.status === 'approved'"
              class="secondary-button git-action-btn git-execute-btn"
              :disabled="operationLoading"
              @click="emit('execute-resolve-conflict')"
            >
              <CheckCircle2 :size="13" />
              <span>{{ t('context.gitExecuteResolveConflict') }}</span>
            </button>
          </div>
        </div>

        <!-- Diff preview toggle -->
        <button
          v-if="diffEntries.length > 0"
          class="git-diff-toggle"
          @click="showDiffPreview = !showDiffPreview"
        >
          <component :is="showDiffPreview ? ChevronDown : ChevronRight" :size="12" />
          <span>{{ t('context.gitDiffPreview') }}</span>
          <small>{{ diffEntries.length }} {{ t('context.gitDiffFiles') }}</small>
        </button>
        <div v-if="showDiffPreview && diffEntries.length > 0" class="git-diff-preview">
          <DiffViewer
            :files="diffEntries"
            :selected-file-path="selectedDiffFile"
            :enable-hunk-actions="indexMode === 'stage'"
            @update:selected-file-path="selectedDiffFile = $event"
            @stage-hunk="(payload) => requestFileHunks(payload.filePath)"
            @discard-hunk="(payload) => openDiscardConfirm([payload.filePath])"
          />
          <div v-if="parsedDiff.files.length > 0" class="git-line-shelf">
            <div class="git-line-shelf-head">
              <div class="git-line-shelf-mode">
                <button type="button" :class="{ active: indexMode === 'stage' }" @click="indexMode = 'stage'">Working tree</button>
                <button type="button" :class="{ active: indexMode === 'unstage' }" :disabled="!stagedDiffText" @click="indexMode = 'unstage'">Staged index</button>
              </div>
              <button
                type="button"
                class="secondary-button git-action-btn"
                :disabled="operationLoading || !selectedTextPatch"
                @click="requestSelectedLines"
              >
                <Plus :size="13" />
                <span>{{ indexMode === 'stage' ? 'Stage selected lines' : 'Unstage selected lines' }}</span>
              </button>
            </div>
            <div v-for="file in parsedDiff.files" :key="file.id" class="git-line-shelf-file">
              <strong>{{ file.path }}</strong>
              <template v-for="hunk in file.hunks" :key="hunk.id">
                <div class="git-line-shelf-hunk" @click="toggleHunk(hunk.id)">
                  <UiCheckbox
                    :model-value="hunk.lines.filter((line) => line.change !== 'context').every((line) => selectedLineIds.has(line.id))"
                    :aria-label="hunk.header"
                    class="git-checkbox"
                    @click.stop
                    @update:model-value="toggleHunk(hunk.id)"
                  />
                  <code>{{ hunk.header }}</code>
                </div>
                <div
                  v-for="line in hunk.lines.filter((item) => item.change !== 'context')"
                  :key="line.id"
                  class="git-line-shelf-line"
                  :class="`is-${line.change}`"
                  @click="toggleChangeLine(hunk.id, line.id)"
                >
                  <UiCheckbox :model-value="selectedLineIds.has(line.id)" :aria-label="line.content" class="git-checkbox" @click.stop @update:model-value="toggleChangeLine(hunk.id, line.id)" />
                  <code>{{ line.change === 'add' ? '+' : '-' }}{{ line.content }}</code>
                </div>
              </template>
            </div>
          </div>
        </div>
      </div>
      <template #footer>
        <!-- Stage/Unstage/Discard actions -->
        <div class="git-stage-actions">
          <button
            class="secondary-button git-action-btn"
            :disabled="operationLoading || !canRequestIndexApproval"
            @click="emit('request-stage')"
          >
            <Plus :size="13" />
            <span>{{ t('context.gitStage') }}</span>
          </button>
          <button
            class="secondary-button git-action-btn"
            :disabled="operationLoading || !canRequestIndexApproval"
            @click="emit('request-unstage')"
          >
            <span>{{ t('context.gitUnstage') }}</span>
          </button>
          <button
            class="secondary-button git-action-btn git-discard-btn"
            :disabled="operationLoading || !canRequestDiscardApproval"
            :title="t('context.gitDiscardSelectedHint')"
            @click="openDiscardConfirm([...selectedPaths])"
          >
            <Trash2 :size="13" />
            <span>{{ t('context.gitDiscardSelected') }}</span>
          </button>
          <button
            v-if="indexApproval?.status === 'approved'"
            class="secondary-button git-action-btn git-execute-btn"
            :disabled="operationLoading"
            @click="emit('execute-index')"
          >
            <CheckCircle2 :size="13" />
            <span>{{ t('context.gitExecuteIndexUpdate') }}</span>
          </button>
          <div v-if="canDecideIndexApproval" class="git-approval-decide">
            <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', indexApproval!, 'approved')">
              <CheckCircle2 :size="14" />
            </button>
            <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', indexApproval!, 'rejected')">
              <ShieldX :size="14" />
            </button>
          </div>
          <button
            v-if="canDecideIndexApproval"
            class="secondary-button git-action-btn git-execute-btn"
            :disabled="operationLoading"
            @click="emit('decide-approval', indexApproval!, 'approved'); $nextTick(() => emit('execute-index'))"
          >
            <CheckCircle2 :size="13" />
            <span>{{ t('context.gitApproveAndExecute') }}</span>
          </button>
        </div>

        <!-- Discard inline confirm (destructive: explicit second step before any approval) -->
        <div v-if="discardConfirm" class="git-discard-confirm">
          <div class="git-discard-confirm-head">
            <AlertTriangle :size="14" />
            <span>{{ t('context.gitDiscardConfirmTitle', { count: discardConfirm.paths.length }) }}</span>
          </div>
          <div class="git-discard-confirm-files">
            <code v-for="path in discardConfirm.paths.slice(0, 3)" :key="path" :title="path">{{ path }}</code>
            <small v-if="discardConfirm.paths.length > 3">+{{ discardConfirm.paths.length - 3 }}</small>
          </div>
          <label class="git-discard-confirm-untracked" @click.stop="discardConfirm = { ...discardConfirm!, includeUntracked: !discardConfirm!.includeUntracked }">
            <UiCheckbox
              :model-value="discardConfirm.includeUntracked"
              :aria-label="t('context.gitDiscardIncludeUntracked')"
              class="git-checkbox"
              @update:model-value="discardConfirm = { ...discardConfirm!, includeUntracked: $event }"
            />
            <span>{{ t('context.gitDiscardIncludeUntracked') }}</span>
          </label>
          <small class="git-discard-confirm-warn">{{ t('context.gitDiscardIrreversible') }}</small>
          <div class="git-discard-confirm-actions">
            <button
              class="secondary-button git-action-btn git-discard-execute"
              :disabled="operationLoading"
              @click="emit('request-discard', discardConfirm.paths, discardConfirm.includeUntracked); discardConfirm = null"
            >
              <Trash2 :size="13" />
              <span>{{ t('context.gitDiscardConfirm') }}</span>
            </button>
            <button
              class="secondary-button git-action-btn"
              @click="discardConfirm = null"
            >
              <span>{{ t('context.gitCompareCancel') }}</span>
            </button>
          </div>
        </div>

        <!-- Discard approval -->
        <div v-if="discardApproval" class="git-discard-approval">
          <div class="git-discard-approval-info">
            <ShieldCheck :size="13" />
            <span>{{ discardApproval.summary }}</span>
            <span class="git-discard-approval-status">{{ discardApproval.status }}</span>
          </div>
          <div class="git-discard-approval-actions">
            <div v-if="canDecideDiscardApproval" class="git-approval-decide">
              <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', discardApproval!, 'approved')">
                <CheckCircle2 :size="14" />
              </button>
              <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', discardApproval!, 'rejected')">
                <ShieldX :size="14" />
              </button>
            </div>
            <button
              v-if="discardApproval.status === 'approved'"
              class="secondary-button git-action-btn git-execute-btn"
              :disabled="operationLoading"
              @click="emit('execute-discard')"
            >
              <CheckCircle2 :size="13" />
              <span>{{ t('context.gitExecuteDiscard') }}</span>
            </button>
          </div>
        </div>
      </template>
    </UiIslandCard>

    <!-- AI change analysis section -->
    <UiIslandCard padding="none">
      <button class="git-section-header" @click="showAiAnalysis = !showAiAnalysis">
        <component :is="showAiAnalysis ? ChevronDown : ChevronRight" :size="14" />
        <Sparkles :size="14" />
        <span>{{ t('context.gitAiAnalysisTitle') }}</span>
      </button>

      <div v-show="showAiAnalysis" class="git-section-body">
        <button
          class="secondary-button git-ai-btn"
          :disabled="!canAiAnalyze || statusFiles.length === 0"
          @click="handleAiAnalyze"
        >
          <component :is="aiAnalyzing ? Loader2 : Sparkles" :size="13" :class="{ spinning: aiAnalyzing }" />
          <span>{{ aiAnalyzing ? t('context.gitAiAnalyzing') : t('context.gitAiAnalyze') }}</span>
        </button>

        <div v-if="aiAnalysis" class="git-ai-analysis">
          <div class="git-ai-analysis-header" :class="`risk-${aiAnalysis.riskLevel}`">
            <component :is="riskIcon(aiAnalysis.riskLevel)" :size="16" />
            <div class="git-ai-analysis-title">
              <strong>{{ riskLabel(aiAnalysis.riskLevel) }}</strong>
              <small>{{ aiAnalysis.summary }}</small>
            </div>
            <div class="git-ai-analysis-score">
              <span>{{ t('context.gitAiRiskScore') }}</span>
              <strong>{{ aiAnalysis.riskScore }}</strong>
            </div>
          </div>

          <div v-if="aiAnalysis.affectedAreas.length > 0" class="git-ai-analysis-block">
            <small>{{ t('context.gitAiAffectedAreas') }}</small>
            <div class="git-ai-tags">
              <span v-for="area in aiAnalysis.affectedAreas" :key="area" class="git-ai-tag">{{ area }}</span>
            </div>
          </div>

          <div v-if="aiAnalysis.concerns.length > 0" class="git-ai-analysis-block">
            <small>{{ t('context.gitAiConcerns') }}</small>
            <ul class="git-ai-list">
              <li v-for="(concern, idx) in aiAnalysis.concerns" :key="idx" :class="`severity-${concern.severity}`">
                <strong>{{ concern.category }}:</strong> {{ concern.description }}
              </li>
            </ul>
          </div>

          <div v-if="aiAnalysis.testSuggestions.length > 0" class="git-ai-analysis-block">
            <small>{{ t('context.gitAiTestSuggestions') }}</small>
            <ul class="git-ai-list">
              <li v-for="(suggestion, idx) in aiAnalysis.testSuggestions" :key="idx">{{ suggestion }}</li>
            </ul>
          </div>
        </div>
      </div>
    </UiIslandCard>

    <!-- Commit message section -->
    <UiIslandCard padding="none">
      <button class="git-section-header" @click="commitExpanded = !commitExpanded">
        <component :is="commitExpanded ? ChevronDown : ChevronRight" :size="14" />
        <GitCommitHorizontal :size="14" />
        <span>{{ t('context.gitCommitMessage') }}</span>
      </button>

      <div v-show="commitExpanded" class="git-section-body">
        <!-- AI generation panel -->
        <div class="git-ai-bar">
          <button
            class="secondary-button git-ai-btn"
            :disabled="!canAiGenerate || statusFiles.length === 0"
            @click="handleAiGenerate"
          >
            <component :is="aiGenerating ? Loader2 : Sparkles" :size="13" :class="{ spinning: aiGenerating }" />
            <span>{{ aiGenerating ? t('context.gitAiGenerating') : t('context.gitAiGenerate') }}</span>
          </button>
        </div>

        <CommitMessageEditor
          :model-value="commitMessage"
          :recent-commits="recentCommits"
          @update:model-value="emit('update:commitMessage', $event)"
        />

        <!-- Convention check -->
        <div v-if="conventionCheck" class="git-convention-check" :class="{ valid: conventionCheck.valid, invalid: !conventionCheck.valid }">
          <component :is="conventionCheck.valid ? CheckCircle2 : AlertTriangle" :size="13" />
          <span>{{ conventionCheck.message ?? conventionCheck.warning }}</span>
        </div>

        <!-- Commit scope hint: staged vs auto-stage selected -->
        <div v-if="commitUsesSelectedPaths && selectedPaths.size > 0" class="git-commit-scope-hint">
          <Plus :size="12" />
          <span>{{ t('context.gitCommitAutoStageHint', { count: selectedPaths.size }) }}</span>
        </div>
        <div v-else-if="stagedCount > 0" class="git-commit-scope-hint is-staged">
          <CheckCircle2 :size="12" />
          <span>{{ t('context.gitCommitStagedHint', { count: stagedCount }) }}</span>
        </div>

        <!-- Split Commit Action (VSCode/openchamber/t3code style) -->
        <div class="git-commit-split-wrapper">
          <div class="git-split-button-group">
            <button
              type="button"
              class="git-split-main-btn"
              :disabled="operationLoading || !canRequestCommitApproval"
              @click="handlePrimaryCommit"
            >
              <GitCommitHorizontal :size="13" />
              <span>{{ t('context.gitCommitStaged') }}</span>
            </button>
            <button
              type="button"
              class="git-split-menu-btn"
              :disabled="operationLoading"
              @click.stop="splitMenuOpen = !splitMenuOpen"
            >
              <ChevronDown :size="12" />
            </button>
          </div>

          <div v-if="splitMenuOpen" class="git-split-dropdown-menu" @click.stop>
            <button
              type="button"
              class="git-split-menu-item"
              :disabled="operationLoading || !canRequestCommitApproval"
              @click="handleCommitAndPush"
            >
              <Upload :size="13" />
              <span>{{ t('context.gitCommitAndPush') }}</span>
            </button>
            <button
              type="button"
              class="git-split-menu-item"
              :disabled="operationLoading || !canRequestCommitApproval"
              @click="handlePrimaryCommit"
            >
              <CheckCircle2 :size="13" />
              <span>{{ t('context.gitCommitAll') }}</span>
            </button>
          </div>
        </div>

        <!-- Commit actions -->
        <div class="git-commit-actions">
          <button
            class="secondary-button git-action-btn"
            :disabled="operationLoading || !canRequestCommitApproval"
            @click="emit('request-commit')"
          >
            <ShieldCheck :size="13" />
            <span>{{ t('context.gitRequestCommitApproval') }}</span>
          </button>
          <button
            class="secondary-button git-action-btn git-execute-btn"
            :disabled="operationLoading || commitApproval?.status !== 'approved'"
            @click="emit('execute-commit')"
          >
            <CheckCircle2 :size="13" />
            <span>{{ t('context.gitExecuteCommit') }}</span>
          </button>
        </div>
        <div v-if="canDecideCommitApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', commitApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', commitApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <button
          v-if="canDecideCommitApproval"
          class="secondary-button git-action-btn git-execute-btn"
          :disabled="operationLoading"
          @click="emit('decide-approval', commitApproval!, 'approved'); $nextTick(() => emit('execute-commit'))"
        >
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitApproveAndExecute') }}</span>
        </button>
      </div>
    </UiIslandCard>

    <!-- Push/Pull section -->
    <UiIslandCard padding="none">
      <button class="git-section-header" @click="pushExpanded = !pushExpanded">
        <component :is="pushExpanded ? ChevronDown : ChevronRight" :size="14" />
        <Upload :size="14" />
        <span>{{ t('context.gitSync') }}</span>
        <div class="git-section-actions" @click.stop>
          <span v-if="behind > 0" class="git-behind-badge" :title="t('context.gitBehind')">
            ↓{{ behind }}
          </span>
        </div>
      </button>

      <div v-show="pushExpanded" class="git-section-body">
        <!-- Pull-only notice: remote behind can be pulled without pushing local work -->
        <div v-if="behind > 0" class="git-pull-notice">
          <ArrowDownToLine :size="14" />
          <span>{{ t('context.gitPullOnlyHint', { count: behind }) }}</span>
        </div>

        <!-- Push readiness -->
        <div class="git-push-status" :class="{ ready: pushReady, blocked: !pushReady }">
          <component :is="pushReady ? CheckCircle2 : AlertTriangle" :size="16" />
          <div>
            <strong>{{ pushReady ? t('context.gitPushReady') : t('context.gitPushBlocked') }}</strong>
          </div>
        </div>

        <!-- Blockers -->
        <div v-if="pushBlockers.length > 0" class="git-blockers">
          <small v-for="blocker in pushBlockers" :key="blocker">{{ blocker }}</small>
        </div>

        <!-- 1) Fetch: safe preview, always available -->
        <div class="git-sync-group">
          <div class="git-sync-group-title">{{ t('context.gitFetchTitle') }}</div>
          <div class="git-sync-actions">
            <button
              class="secondary-button git-action-btn"
              :disabled="operationLoading || !canRequestFetchApproval"
              :title="t('context.gitFetchHint')"
              @click="emit('request-fetch')"
            >
              <RefreshCw :size="13" />
              <span>{{ t('context.gitRequestFetchApproval') }}</span>
            </button>
            <button
              v-if="fetchApproval?.status === 'approved'"
              class="secondary-button git-action-btn git-execute-btn"
              :disabled="operationLoading"
              @click="emit('execute-fetch')"
            >
              <CheckCircle2 :size="13" />
              <span>{{ t('context.gitExecuteFetch') }}</span>
            </button>
          </div>
          <div v-if="canDecideFetchApproval" class="git-approval-decide">
            <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', fetchApproval!, 'approved')">
              <CheckCircle2 :size="14" />
            </button>
            <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', fetchApproval!, 'rejected')">
              <ShieldX :size="14" />
            </button>
          </div>
        </div>

        <!-- 2) Pull-only: fetch + merge remote, never pushes local commits -->
        <div class="git-sync-group is-pull">
          <div class="git-sync-group-title">
            <span>{{ t('context.gitPullOnlyTitle') }}</span>
            <small>{{ t('context.gitPullOnlySubtitle') }}</small>
          </div>
          <div class="git-sync-actions">
            <button
              class="secondary-button git-action-btn git-pull-btn"
              :disabled="operationLoading || !canRequestPullApproval"
              :title="pullDisabledReason || t('context.gitPullOnlyHintShort')"
              @click="emit('request-pull')"
            >
              <Download :size="13" />
              <span>{{ t('context.gitRequestPullApproval') }}</span>
            </button>
            <label class="git-strategy-select" :title="t('context.gitPullStrategyHint')">
              <span>{{ t('context.gitPullStrategy') }}</span>
              <select
                :value="pullStrategy"
                :disabled="operationLoading"
                @change="emit('update:pullStrategy', (($event.target as HTMLSelectElement).value as 'ff-only' | 'merge' | 'rebase'))"
              >
                <option value="ff-only">{{ t('context.gitPullStrategyFfOnly') }}</option>
                <option value="merge" disabled>{{ t('context.gitPullStrategyMerge') }}</option>
                <option value="rebase" disabled>{{ t('context.gitPullStrategyRebase') }}</option>
              </select>
            </label>
          </div>
          <small v-if="pullDisabledReason" class="git-sync-hint">{{ pullDisabledReason }}</small>
          <small v-else class="git-sync-hint">{{ t('context.gitPullStrategyFfNote') }}</small>
          <button
            v-if="pullApproval?.status === 'approved'"
            class="secondary-button git-action-btn git-execute-btn"
            :disabled="operationLoading"
            @click="emit('execute-pull')"
          >
            <Download :size="13" />
            <span>{{ t('context.gitExecutePull') }}</span>
          </button>
          <div v-if="canDecidePullApproval" class="git-approval-decide">
            <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', pullApproval!, 'approved')">
              <CheckCircle2 :size="14" />
            </button>
            <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', pullApproval!, 'rejected')">
              <ShieldX :size="14" />
            </button>
          </div>
        </div>

        <!-- 3) Push: local -> remote -->
        <div class="git-sync-group">
          <div class="git-sync-group-title">{{ t('context.gitPushTitle') }}</div>
          <div class="git-sync-actions">
            <button
              class="secondary-button git-action-btn"
              :disabled="operationLoading || !canRequestPushApproval"
              @click="emit('request-push')"
            >
              <Upload :size="13" />
              <span>{{ t('context.gitRequestPushApproval') }}</span>
            </button>
            <button
              v-if="pushApproval?.status === 'approved'"
              class="secondary-button git-action-btn git-execute-btn"
              :disabled="operationLoading"
              @click="emit('execute-push')"
            >
              <Upload :size="13" />
              <span>{{ t('context.gitExecutePush') }}</span>
            </button>
          </div>
        </div>
        <div v-if="canDecidePushApproval" class="git-approval-decide">
          <button class="icon-button approve" :title="t('approval.approve')" @click="emit('decide-approval', pushApproval!, 'approved')">
            <CheckCircle2 :size="14" />
          </button>
          <button class="icon-button reject" :title="t('approval.reject')" @click="emit('decide-approval', pushApproval!, 'rejected')">
            <ShieldX :size="14" />
          </button>
        </div>
        <button
          v-if="canDecidePushApproval"
          class="secondary-button git-action-btn git-execute-btn"
          :disabled="operationLoading"
          @click="emit('decide-approval', pushApproval!, 'approved'); $nextTick(() => emit('execute-push'))"
        >
          <CheckCircle2 :size="13" />
          <span>{{ t('context.gitApproveAndExecute') }}</span>
        </button>
      </div>
    </UiIslandCard>

  </div>
</template>

<style scoped>
.git-changes-view {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

/* Section islands (UiIslandCard) */
.git-changes-view :deep(.island-card) {
  overflow: visible;
  backdrop-filter: var(--material-filter-section, none);
  -webkit-backdrop-filter: var(--material-filter-section, none);
}

.git-section-header {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 10px 12px;
  background: transparent;
  border: 0;
  border-radius: 12px;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
  cursor: pointer;
  text-align: left;
  transition: background-color 0.12s ease;
}

.git-section-header:hover {
  background: var(--surface-hover);
}

.git-section-count {
  margin-left: auto;
  padding: 1px 6px;
  background: var(--surface-button);
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  color: var(--text-secondary);
}

.git-section-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-left: 8px;
}

/* Unified rounded-rect checkbox: fixed 16px box, never stretches inside flex rows. */
.git-checkbox {
  flex: none;
}

.git-changes-view input[type='checkbox'] {
  flex: none;
  width: 16px;
  height: 16px;
  margin: 0;
  accent-color: var(--accent-primary);
}

.git-section-refresh {
  width: 24px;
  height: 24px;
  display: flex;
  align-items: center;
  justify-content: center;
}

.git-section-body {
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

/* pad-none islands: slots manage their own insets; restore the footer inset. */
.git-changes-view :deep(.island-footer) {
  padding: 10px 12px 12px;
}

.git-empty-state {
  padding: 16px 10px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
}

.git-file-list {
  display: flex;
  flex-direction: column;
  gap: 1px;
  max-height: 280px;
  overflow-y: auto;
}

.git-file-row {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-height: 28px;
  justify-content: center;
  padding: 5px 8px;
  border-radius: 8px;
  transition: background 0.1s;
}

.git-file-row:hover {
  background: var(--surface-hover);
}

.git-file-row-main {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  cursor: pointer;
}

.git-conflict-actions {
  display: flex;
  gap: 4px;
  padding-left: 26px;
}

.git-conflict-btn {
  padding: 2px 6px;
  font-size: 10px;
  font-weight: 600;
  color: var(--text-secondary);
  background: var(--surface-button);
  border: 1px solid var(--border-muted);
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.1s;
}

.git-conflict-btn:hover:not(:disabled) {
  color: var(--text-primary);
  background: var(--surface-button-hover);
  border-color: var(--border-default);
}

.git-conflict-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.git-conflict-approval {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  background: var(--surface-raised);
  border: 1px solid var(--border-card);
  border-radius: 6px;
}

.git-conflict-approval-info {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--text-primary);
}

.git-conflict-approval-status {
  margin-left: auto;
  padding: 1px 5px;
  font-size: 10px;
  text-transform: uppercase;
  font-weight: 700;
  color: var(--text-muted);
  background: var(--surface-button);
  border-radius: 4px;
}

.git-conflict-approval-actions {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
}

.git-file-icon {
  flex-shrink: 0;
}

/* Per-row discard: quiet until hover, danger on hover. */
.git-file-discard {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-muted);
  opacity: 0;
  cursor: pointer;
  transition: opacity 0.12s ease, background 0.12s ease, color 0.12s ease;
}

.git-file-row:hover .git-file-discard,
.git-file-discard:focus-visible {
  opacity: 1;
}

.git-file-discard:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent-danger) 12%, transparent);
  color: var(--text-reject);
}

.git-file-discard:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.git-file-path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-primary);
  font-family: 'Geist Variable', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', 'Noto Sans SC', 'PingFang SC', 'Microsoft YaHei', sans-serif;
  font-size: 12px;
}

.git-file-status-badge {
  flex-shrink: 0;
  min-width: 16px;
  text-align: center;
  font-size: 10px;
  font-weight: 700;
  font-family: 'Geist Variable', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', 'Noto Sans SC', sans-serif;
  padding: 1px 4px;
  border-radius: 3px;
}

/* Status color variants (semantic solids — never tinted by the palette) */
.status-added .git-file-icon { color: var(--accent-success); }
.status-added .git-file-status-badge { color: var(--accent-success); background: color-mix(in srgb, var(--accent-success) 12%, transparent); }

.status-modified .git-file-icon { color: var(--accent-warning); }
.status-modified .git-file-status-badge { color: var(--accent-warning); background: color-mix(in srgb, var(--accent-warning) 12%, transparent); }

.status-deleted .git-file-icon { color: var(--accent-danger); }
.status-deleted .git-file-status-badge { color: var(--accent-danger); background: color-mix(in srgb, var(--accent-danger) 12%, transparent); }

.status-renamed .git-file-icon { color: var(--accent-info); }
.status-renamed .git-file-status-badge { color: var(--accent-info); background: color-mix(in srgb, var(--accent-info) 12%, transparent); }

.status-untracked .git-file-icon { color: var(--text-muted); }
.status-untracked .git-file-status-badge { color: var(--text-muted); background: color-mix(in srgb, var(--text-muted) 12%, transparent); }

.status-conflict .git-file-icon { color: var(--accent-danger); }
.status-conflict .git-file-status-badge { color: var(--accent-danger); background: color-mix(in srgb, var(--accent-danger) 20%, transparent); }

.git-conflict-banner {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  color: var(--text-reject);
  background: var(--bg-status-danger);
  border-radius: 6px;
  font-size: 12px;
}

.git-diff-toggle {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  background: transparent;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  transition: background 0.1s;
}

.git-diff-toggle:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.git-diff-toggle small {
  margin-left: auto;
  color: var(--text-muted);
  font-size: 10px;
}

.git-diff-preview {
  min-height: 280px;
  height: 400px;
  border-radius: 8px;
  overflow: hidden;
}

.git-stage-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
}

.git-action-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  padding: 6px 10px;
  font-size: 12px;
  white-space: nowrap;
}

.git-execute-btn {
  background: var(--bg-primary-button);
  color: hsl(var(--primary-foreground));
  border-color: var(--bg-primary-button);
}

.git-execute-btn:hover:not(:disabled) {
  background: var(--bg-primary-button-hover);
}

.git-approval-decide {
  display: flex;
  gap: 4px;
}

/* Discard: destructive entry points share the danger tint. */
.git-discard-btn {
  color: var(--text-reject);
}

.git-discard-btn:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent-danger) 10%, var(--surface-button-hover));
}

.git-discard-confirm {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px;
  border: 1px solid color-mix(in srgb, var(--accent-danger) 40%, var(--border-card));
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent-danger) 6%, var(--surface-raised));
}

.git-discard-confirm-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 700;
  color: var(--text-reject);
}

.git-discard-confirm-files {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  align-items: center;
}

.git-discard-confirm-files code {
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--surface-button);
  color: var(--text-primary);
  font-size: 11px;
}

.git-discard-confirm-files small {
  color: var(--text-muted);
  font-size: 10px;
}

.git-discard-confirm-untracked {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-primary);
  cursor: pointer;
}

.git-discard-confirm-warn {
  font-size: 10px;
  color: var(--text-muted);
}

.git-discard-confirm-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.git-discard-execute {
  color: #fff;
  background: var(--accent-danger);
  border-color: var(--accent-danger);
}

.git-discard-execute:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent-danger) 85%, #000);
}

.git-discard-approval {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  background: var(--surface-raised);
  border: 1px solid var(--border-card);
  border-radius: 8px;
}

.git-discard-approval-info {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--text-primary);
}

.git-discard-approval-status {
  margin-left: auto;
  padding: 1px 5px;
  font-size: 10px;
  text-transform: uppercase;
  font-weight: 700;
  color: var(--text-muted);
  background: var(--surface-button);
  border-radius: 4px;
}

.git-discard-approval-actions {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
}

.git-ai-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.git-ai-btn {
  background: linear-gradient(135deg, color-mix(in srgb, var(--accent-info) 12%, transparent), color-mix(in srgb, var(--accent-recovery) 12%, transparent));
  border-color: color-mix(in srgb, var(--accent-info) 30%, transparent);
  color: var(--accent-primary);
}

.git-ai-btn:hover:not(:disabled) {
  background: linear-gradient(135deg, color-mix(in srgb, var(--accent-info) 20%, transparent), color-mix(in srgb, var(--accent-recovery) 20%, transparent));
}

.git-convention-check {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  border-radius: 6px;
  font-size: 11px;
}

.git-convention-check.valid {
  color: var(--accent-success);
  background: color-mix(in srgb, var(--accent-success) 8%, transparent);
}

.git-convention-check.invalid {
  color: var(--text-reject);
  background: color-mix(in srgb, var(--accent-danger) 8%, transparent);
}

.git-commit-actions {
  display: flex;
  gap: 6px;
  position: sticky;
  bottom: 0;
  padding: 6px 0 2px;
  background: linear-gradient(transparent, var(--surface-section) 40%);
}

.git-commit-scope-hint {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  border-radius: 8px;
  font-size: 11px;
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 8%, transparent);
}

.git-commit-scope-hint.is-staged {
  color: var(--accent-success);
  background: color-mix(in srgb, var(--accent-success) 8%, transparent);
}

.git-push-status {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 6px;
}

.git-push-status.ready {
  background: var(--bg-status-ok);
  color: var(--accent-success);
}

.git-push-status.blocked {
  background: var(--bg-status-warn);
  color: var(--accent-warning);
}

.git-push-status strong {
  font-size: 12px;
  color: var(--text-primary);
}

.git-blockers {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.git-blockers small {
  padding: 2px 6px;
  background: var(--surface-button);
  border-radius: 4px;
  font-size: 10px;
  color: var(--text-muted);
}

.git-sync-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.git-sync-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--surface-raised);
}

.git-sync-group.is-pull {
  border-color: color-mix(in srgb, var(--accent-info) 35%, var(--border-muted));
}

.git-sync-group-title {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 11px;
  font-weight: 700;
  color: var(--text-primary);
}

.git-sync-group-title small {
  font-weight: 400;
  color: var(--text-muted);
  font-size: 10px;
}

.git-sync-hint {
  font-size: 10px;
  color: var(--text-muted);
  line-height: 1.4;
}

.git-pull-notice {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  font-size: 11px;
  color: var(--accent-info);
  background: color-mix(in srgb, var(--accent-info) 10%, transparent);
}

.git-pull-btn {
  border-color: color-mix(in srgb, var(--accent-info) 40%, transparent);
}

.git-strategy-select {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--text-secondary);
}

.git-strategy-select select {
  padding: 6px 8px;
  color: var(--text-primary);
  background: var(--surface-input);
  border: 1px solid var(--border-input);
  border-radius: 8px;
  font-size: 11px;
}

.git-behind-badge {
  padding: 1px 6px;
  background: color-mix(in srgb, var(--accent-warning) 15%, transparent);
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  color: var(--accent-warning);
}

.spinning {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

/* ---- AI change analysis ---- */
.git-ai-analysis {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 8px;
  background: var(--surface-raised);
  border: 1px solid var(--border-card);
  border-radius: 8px;
}

.git-ai-analysis-header {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 10px;
  border-radius: 6px;
}

.git-ai-analysis-header.risk-low {
  color: var(--accent-success);
  background: color-mix(in srgb, var(--accent-success) 10%, transparent);
}

.git-ai-analysis-header.risk-medium {
  color: var(--accent-warning);
  background: color-mix(in srgb, var(--accent-warning) 10%, transparent);
}

.git-ai-analysis-header.risk-high {
  color: var(--accent-danger);
  background: color-mix(in srgb, var(--accent-danger) 10%, transparent);
}

.git-ai-analysis-header.risk-critical {
  color: var(--accent-danger);
  background: color-mix(in srgb, var(--accent-danger) 18%, transparent);
}

.git-ai-analysis-title {
  display: flex;
  flex-direction: column;
  gap: 2px;
  flex: 1;
  min-width: 0;
}

.git-ai-analysis-title strong {
  font-size: 13px;
  color: var(--text-primary);
}

.git-ai-analysis-title small {
  font-size: 11px;
  color: var(--text-secondary);
}

.git-ai-analysis-score {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 1px;
  padding: 2px 8px;
  background: var(--surface-button);
  border-radius: 6px;
}

.git-ai-analysis-score span {
  font-size: 9px;
  text-transform: uppercase;
  color: var(--text-muted);
}

.git-ai-analysis-score strong {
  font-size: 16px;
  color: var(--text-primary);
}

.git-ai-analysis-block {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.git-ai-analysis-block > small {
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  color: var(--text-muted);
}

.git-ai-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.git-ai-tag {
  padding: 2px 6px;
  font-size: 10px;
  color: var(--text-secondary);
  background: var(--surface-button);
  border-radius: 4px;
}

.git-ai-list {
  margin: 0;
  padding-left: 16px;
  font-size: 11px;
  color: var(--text-secondary);
}

.git-ai-list li {
  margin-bottom: 3px;
}

.git-ai-list .severity-medium,
.git-ai-list .severity-high {
  color: var(--accent-warning);
}

.git-ai-list .severity-critical {
  color: var(--accent-danger);
}

.git-line-shelf {
  display: grid;
  gap: 8px;
  margin-top: 10px;
  padding: 10px;
  border: 1px solid var(--border-card);
  border-radius: 6px;
  background: var(--surface-raised);
}

.git-line-shelf-head,
.git-line-shelf-hunk,
.git-line-shelf-line {
  display: flex;
  align-items: center;
  gap: 7px;
}

.git-line-shelf-head {
  justify-content: space-between;
  font-size: 11px;
  font-weight: 700;
}

.git-line-shelf-mode {
  display: flex;
  gap: 4px;
}

.git-line-shelf-mode button {
  border: 0;
  padding: 3px 6px;
  color: var(--text-muted);
  background: transparent;
  border-radius: 4px;
  font-size: 10px;
}

.git-line-shelf-mode button.active {
  color: var(--text-primary);
  background: var(--surface-button);
}

.git-line-shelf-file {
  display: grid;
  gap: 3px;
  font-size: 11px;
}

.git-line-shelf-hunk {
  color: var(--text-muted);
  margin-top: 3px;
}

.git-line-shelf-line {
  padding-left: 14px;
  overflow: hidden;
}

.git-line-shelf-line code,
.git-line-shelf-hunk code {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: pre;
}

.git-line-shelf-line.is-add { color: var(--text-approve); }
.git-line-shelf-line.is-delete { color: var(--text-reject); }

/* Controls bar: Tree/List toggle */
.git-view-controls-bar {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  padding: 4px 12px;
  border-bottom: 1px solid var(--border-subtle);
  background: var(--surface-card);
}

.git-mode-switch-group {
  display: flex;
  background: var(--surface-raised);
  border: 1px solid var(--border-card);
  border-radius: 6px;
  padding: 2px;
  gap: 2px;
}

.git-mode-btn {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border: none;
  background: transparent;
  color: var(--text-muted);
  border-radius: 4px;
  font-size: 11px;
  cursor: pointer;
  transition: all 0.15s ease;
}

.git-mode-btn:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.git-mode-btn.active {
  color: var(--accent-primary);
  background: var(--surface-card);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.08);
  font-weight: 500;
}

/* Sections container */
.git-sections-container {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 6px 8px;
}

.git-sub-section {
  border: 1px solid var(--border-card);
  border-radius: 6px;
  background: var(--surface-card);
  overflow: hidden;
}

.git-sub-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  user-select: none;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border-subtle);
}

.git-sub-header:hover {
  background: var(--surface-hover);
}

.git-sub-title {
  color: var(--text-primary);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.git-sub-count {
  background: var(--surface-button);
  color: var(--text-muted);
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 10px;
  margin-right: auto;
}

.git-sub-actions {
  display: flex;
  align-items: center;
  gap: 4px;
}

.git-sub-action-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: none;
  background: transparent;
  color: var(--text-muted);
  border-radius: 4px;
  cursor: pointer;
  transition: all 0.15s ease;
}

.git-sub-action-btn:hover:not(:disabled) {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.git-sub-action-btn.is-discard:hover:not(:disabled) {
  color: var(--accent-danger);
}

.git-sub-body {
  max-height: 280px;
  overflow-y: auto;
}

.git-sub-empty {
  padding: 10px 14px;
  font-size: 11px;
  color: var(--text-muted);
  font-style: italic;
}

.git-tree-container {
  padding: 2px 4px;
}

.git-row-hover-actions {
  display: none;
  align-items: center;
  gap: 2px;
  margin-left: auto;
}

.git-file-row:hover .git-row-hover-actions {
  display: flex;
}

.git-file-row.is-active-diff {
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
}

.git-hover-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: none;
  background: transparent;
  color: var(--text-muted);
  border-radius: 4px;
  cursor: pointer;
}

.git-hover-btn:hover:not(:disabled) {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.git-hover-btn.is-discard:hover:not(:disabled) {
  color: var(--accent-danger);
}

/* Split button styling */
.git-commit-split-wrapper {
  position: relative;
  margin-bottom: 8px;
}

.git-split-button-group {
  display: flex;
  width: 100%;
  border-radius: 6px;
  overflow: hidden;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.08);
}

.git-split-main-btn {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  background: var(--accent-primary);
  color: #fff;
  border: none;
  padding: 7px 12px;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: opacity 0.15s ease;
}

.git-split-main-btn:hover:not(:disabled) {
  opacity: 0.92;
}

.git-split-main-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.git-split-menu-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0 8px;
  background: color-mix(in srgb, var(--accent-primary) 85%, #000);
  color: #fff;
  border: none;
  border-left: 1px solid rgba(255, 255, 255, 0.2);
  cursor: pointer;
}

.git-split-menu-btn:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent-primary) 75%, #000);
}

.git-split-dropdown-menu {
  position: absolute;
  top: calc(100% + 4px);
  right: 0;
  width: 180px;
  background: var(--surface-card);
  border: 1px solid var(--border-card);
  border-radius: 6px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  z-index: 50;
  padding: 4px;
}

.git-split-menu-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 10px;
  font-size: 12px;
  background: transparent;
  border: none;
  color: var(--text-primary);
  border-radius: 4px;
  cursor: pointer;
  text-align: left;
}

.git-split-menu-item:hover:not(:disabled) {
  background: var(--surface-hover);
}
</style>
