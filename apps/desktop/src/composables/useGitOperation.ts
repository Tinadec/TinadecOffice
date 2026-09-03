import { computed, ref, watch, nextTick } from 'vue'
import { api, createUserToolActionForPath, type ApprovalDto, type CodeToolExecuteResultDto, type UserToolActionDto } from '../api'
import { useI18n } from 'vue-i18n'
import { useNotifications } from './useNotifications'
import { useUserActionStore } from '@/stores/userAction'
import {
  userToolActionIdempotencyKey,
  userToolActionStatusMessage,
  userToolActionToApproval,
  withGitToolConfirmation,
} from '../userToolAction'

// ---- Type definitions ----

export interface GitStatusFile {
  path: string
  previous_path?: string | null
  staged_status?: string
  unstaged_status?: string
  status?: string
  is_untracked?: boolean
  is_conflicted?: boolean
  is_renamed?: boolean
}

export interface GitDiffSection {
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

export interface GitIndexSelection {
  paths?: string[]
  patch?: string
}

export interface GitPreviewData {
  git_root?: string
  branch?: string
  upstream?: string | null
  ahead?: number
  behind?: number
  has_uncommitted_changes?: boolean
  files?: GitStatusFile[]
  sections?: GitDiffSection[]
}

export interface GitPushPlanData extends GitPreviewData {
  diff_stat?: string
  recent_commits?: string[]
  remotes?: string[]
  push_ready?: boolean
  push_blockers?: string[]
  suggested_commands?: string[]
  needs_push?: boolean
  worktrees?: Array<Record<string, unknown>>
}

export interface GitLogCommit {
  hash: string
  short_hash: string
  author: string
  email: string
  date: string
  subject: string
}

export interface GitBranch {
  name: string
  is_current: boolean
  is_remote: boolean
  upstream?: string | null
  ahead?: number
  behind?: number
  last_subject?: string
  last_date?: string
}

export interface GitRepoSummary {
  branch: string
  upstream: string | null
  ahead: number
  behind: number
  totalChanges: number
  stagedCount: number
  unstagedCount: number
  untrackedCount: number
}

// ---- Helper functions ----

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

/**
 * Parse a Git status code (e.g. "M", "A", "D", "R", "?", "UU") into a
 * human-readable change type label.
 */
export function statusToLabel(status?: string): string {
  if (!status) return '?'
  const code = status.trim()
  switch (code) {
    case 'M':
      return 'M'
    case 'A':
      return 'A'
    case 'D':
      return 'D'
    case 'R':
      return 'R'
    case 'C':
      return 'C'
    case 'U':
    case 'UU':
    case 'AA':
    case 'DD':
      return 'U'
    case '?':
      return '?'
    case '!':
      return 'I'
    default:
      return code.charAt(0) || '?'
  }
}

/**
 * Map a status code to a color category for UI rendering.
 */
export function statusColor(status?: string): 'added' | 'modified' | 'deleted' | 'renamed' | 'untracked' | 'conflict' | 'ignored' {
  if (!status) return 'untracked'
  const code = status.trim()
  if (code === '?' || code === '!!') return 'untracked'
  if (code === 'A') return 'added'
  if (code === 'D') return 'deleted'
  if (code === 'R' || code === 'C') return 'renamed'
  if (code.startsWith('U') || code === 'AA' || code === 'DD') return 'conflict'
  if (code === '!') return 'ignored'
  return 'modified'
}

// ---- Composable ----

export function useGitOperation(
  currentProjectPath: () => string | undefined,
  selectedSessionId: () => string | null,
  approvals: () => ApprovalDto[],
) {
  const { t } = useI18n()
  const { notify } = useNotifications()
  // Every Git mutation mirrors into the unified store so approval panels and
  // the recovery page observe one source of client-side action state.
  const userActionStore = useUserActionStore()

  // ---- Reactive state ----
  const loading = ref(false)
  const operationLoading = ref(false)


  const preview = ref<CodeToolExecuteResultDto | null>(null)
  const pushPlan = ref<CodeToolExecuteResultDto | null>(null)
  const logResult = ref<CodeToolExecuteResultDto | null>(null)
  const branchResult = ref<CodeToolExecuteResultDto | null>(null)

  const commitMessage = ref('')
  const selectedPaths = ref<Set<string>>(new Set())
  const selectAll = ref(true)
  // 拉取策略：后端当前仅支持 ff-only，merge/rebase 在 UI 上预留并禁用，避免静默行为不一致。
  const pullStrategy = ref<'ff-only' | 'merge' | 'rebase'>('ff-only')

  // Approval tracking
  const indexApprovalId = ref<string | null>(null)
  const indexAction = ref<'stage' | 'unstage' | null>(null)
  const indexSelection = ref<GitIndexSelection | null>(null)
  const commitApprovalId = ref<string | null>(null)
  const pushApprovalId = ref<string | null>(null)
  const pullApprovalId = ref<string | null>(null)
  const checkoutApprovalId = ref<string | null>(null)
  const branchApprovalId = ref<string | null>(null)
  const fetchApprovalId = ref<string | null>(null)
  const mergeApprovalId = ref<string | null>(null)
  const rebaseApprovalId = ref<string | null>(null)
  const resolveConflictApprovalId = ref<string | null>(null)
  const resolveConflictPath = ref<string | null>(null)
  const resolveConflictStrategy = ref<'ours' | 'theirs' | 'both' | null>(null)
  const discardApprovalId = ref<string | null>(null)
  const discardPaths = ref<string[]>([])
  const discardIncludeUntracked = ref(false)
  const deleteBranchApprovalId = ref<string | null>(null)
  const renameBranchApprovalId = ref<string | null>(null)
  const worktreeApprovalId = ref<string | null>(null)
  const worktreeOperation = ref<{ action: 'create'; branch: string; path: string } | { action: 'remove'; path: string } | null>(null)
  const userActions = new Map<string, UserToolActionDto>()

  // ---- Computed ----
  const previewData = computed(() => (preview.value?.data ?? {}) as GitPreviewData)
  const pushData = computed(() => (pushPlan.value?.data ?? {}) as GitPushPlanData)

  const statusFiles = computed(() =>
    Array.isArray(previewData.value.files) ? previewData.value.files : [],
  )
  const sections = computed(() =>
    Array.isArray(previewData.value.sections) ? previewData.value.sections : [],
  )
  const totalChanges = computed(() => statusFiles.value.length)

  const recentCommits = computed(() => stringList(pushData.value.recent_commits).slice(0, 10))

  const repoSummary = computed<GitRepoSummary>(() => {
    const files = statusFiles.value
    let staged = 0
    let unstaged = 0
    let untracked = 0
    for (const file of files) {
      if (file.is_untracked || file.status === '?') {
        untracked++
      } else if (file.staged_status && file.staged_status !== ' ' && file.staged_status !== '?') {
        staged++
      } else if (file.unstaged_status && file.unstaged_status !== ' ' && file.unstaged_status !== '?') {
        unstaged++
      } else if (file.status && file.status !== ' ') {
        // Fallback: count as unstaged
        unstaged++
      }
    }
    return {
      branch: previewData.value.branch ?? '-',
      upstream: previewData.value.upstream ?? null,
      ahead: typeof previewData.value.ahead === 'number' ? previewData.value.ahead : 0,
      behind: typeof previewData.value.behind === 'number' ? previewData.value.behind : 0,
      totalChanges: files.length,
      stagedCount: staged,
      unstagedCount: unstaged,
      untrackedCount: untracked,
    }
  })

  const pushBlockers = computed(() =>
    Array.isArray(pushData.value.push_blockers) ? pushData.value.push_blockers : [],
  )
  const pushReady = computed(() => pushData.value.push_ready === true)
  const noUpstreamOnly = computed(
    () => pushBlockers.value.length === 1 && pushBlockers.value[0] === 'no upstream',
  )
  const hasPushCandidate = computed(() => {
    const ahead = typeof pushData.value.ahead === 'number' ? pushData.value.ahead : 0
    return (pushReady.value && ahead > 0) || noUpstreamOnly.value
  })
  const pushCommand = computed(() =>
    noUpstreamOnly.value ? `git push -u origin ${pushData.value.branch ?? 'HEAD'}` : 'git push',
  )

  const selectedCommitPaths = computed(() => [...selectedPaths.value])
  const selectAllIndeterminate = computed(
    () => selectedPaths.value.size > 0 && selectedPaths.value.size < statusFiles.value.length,
  )

  const logCommits = computed<GitLogCommit[]>(() => {
    const data = (logResult.value?.data ?? {}) as { commits?: unknown }
    if (!Array.isArray(data.commits)) return []
    const out: GitLogCommit[] = []
    for (const entry of data.commits as Array<unknown>) {
      if (typeof entry === 'string') {
        // 兼容旧 mock： "abc1234 subject..." 字符串形态
        const text = entry.trim()
        if (!text) continue
        const [maybeHash, ...rest] = text.split(/\s+/)
        const hash = maybeHash ?? ''
        out.push({
          hash,
          short_hash: hash.slice(0, 7),
          author: '',
          email: '',
          date: '',
          subject: rest.join(' ') || text,
        })
        continue
      }
      if (entry && typeof entry === 'object') {
        const raw = entry as Record<string, unknown>
        const hash = typeof raw.hash === 'string' ? raw.hash : ''
        if (!hash) continue
        const shortHash = typeof raw.short_hash === 'string' && raw.short_hash
          ? raw.short_hash
          : hash.slice(0, 7)
        out.push({
          hash,
          short_hash: shortHash,
          author: typeof raw.author === 'string' ? raw.author : '',
          email: typeof raw.email === 'string'
            ? raw.email
            : typeof raw.author_email === 'string' ? raw.author_email : '',
          date: typeof raw.date === 'string'
            ? raw.date
            : typeof raw.author_date === 'string'
              ? raw.author_date
              : typeof raw.committer_date === 'string' ? raw.committer_date : '',
          subject: typeof raw.subject === 'string' ? raw.subject : '',
        })
      }
    }
    return out
  })

  const branches = computed<GitBranch[]>(() => {
    const data = (branchResult.value?.data ?? {}) as { branches?: unknown }
    return Array.isArray(data.branches) ? (data.branches as GitBranch[]) : []
  })

  // Approval lookups
  const allApprovals = computed(() => approvals())
  const indexApproval = computed(
    () => allApprovals.value.find((a) => a.id === indexApprovalId.value) ?? null,
  )
  const commitApproval = computed(
    () => allApprovals.value.find((a) => a.id === commitApprovalId.value) ?? null,
  )
  const pushApproval = computed(
    () => allApprovals.value.find((a) => a.id === pushApprovalId.value) ?? null,
  )
  const pullApproval = computed(
    () => allApprovals.value.find((a) => a.id === pullApprovalId.value) ?? null,
  )
  const checkoutApproval = computed(
    () => allApprovals.value.find((a) => a.id === checkoutApprovalId.value) ?? null,
  )
  const branchApproval = computed(
    () => allApprovals.value.find((a) => a.id === branchApprovalId.value) ?? null,
  )
  const fetchApproval = computed(
    () => allApprovals.value.find((a) => a.id === fetchApprovalId.value) ?? null,
  )
  const mergeApproval = computed(
    () => allApprovals.value.find((a) => a.id === mergeApprovalId.value) ?? null,
  )
  const rebaseApproval = computed(
    () => allApprovals.value.find((a) => a.id === rebaseApprovalId.value) ?? null,
  )
  const resolveConflictApproval = computed(
    () => allApprovals.value.find((a) => a.id === resolveConflictApprovalId.value) ?? null,
  )
  const discardApproval = computed(
    () => allApprovals.value.find((a) => a.id === discardApprovalId.value) ?? null,
  )
  const deleteBranchApproval = computed(
    () => allApprovals.value.find((a) => a.id === deleteBranchApprovalId.value) ?? null,
  )
  const renameBranchApproval = computed(
    () => allApprovals.value.find((a) => a.id === renameBranchApprovalId.value) ?? null,
  )
  const worktreeApproval = computed(
    () => allApprovals.value.find((a) => a.id === worktreeApprovalId.value) ?? null,
  )

  const canDecideIndexApproval = computed(() => indexApproval.value?.status === 'pending')
  const canDecideCommitApproval = computed(() => commitApproval.value?.status === 'pending')
  const canDecidePushApproval = computed(() => pushApproval.value?.status === 'pending')
  const canDecidePullApproval = computed(() => pullApproval.value?.status === 'pending')
  const canDecideCheckoutApproval = computed(() => checkoutApproval.value?.status === 'pending')
  const canDecideBranchApproval = computed(() => branchApproval.value?.status === 'pending')
  const canDecideFetchApproval = computed(() => fetchApproval.value?.status === 'pending')
  const canDecideMergeApproval = computed(() => mergeApproval.value?.status === 'pending')
  const canDecideRebaseApproval = computed(() => rebaseApproval.value?.status === 'pending')
  const canDecideResolveConflictApproval = computed(() => resolveConflictApproval.value?.status === 'pending')
  const canDecideDiscardApproval = computed(() => discardApproval.value?.status === 'pending')
  const canDecideDeleteBranchApproval = computed(() => deleteBranchApproval.value?.status === 'pending')
  const canDecideRenameBranchApproval = computed(() => renameBranchApproval.value?.status === 'pending')
  const canDecideWorktreeApproval = computed(() => worktreeApproval.value?.status === 'pending')

  // ---- Validation computed ----
  const cwd = computed(() => currentProjectPath())
  const sid = computed(() => selectedSessionId())

  const canRequestIndexApproval = computed(() =>
    Boolean(cwd.value && sid.value && selectedCommitPaths.value.length > 0),
  )
  const canRequestDiscardApproval = computed(() =>
    Boolean(cwd.value && sid.value && selectedCommitPaths.value.length > 0),
  )
  // 勾选即提交范围：有已暂存直接提交暂存区；无暂存但有勾选时用 paths 模式一次审批自动暂存勾选文件。
  const commitUsesSelectedPaths = computed(() =>
    repoSummary.value.stagedCount === 0 && selectedCommitPaths.value.length > 0,
  )
  const canRequestCommitApproval = computed(() =>
    Boolean(cwd.value && sid.value && commitMessage.value.trim() && (repoSummary.value.stagedCount > 0 || selectedCommitPaths.value.length > 0)),
  )
  const canRequestPushApproval = computed(() =>
    Boolean(cwd.value && sid.value && hasPushCandidate.value),
  )
  const canRequestPullApproval = computed(() =>
    Boolean(cwd.value && sid.value && repoSummary.value.behind > 0),
  )
  const canRequestFetchApproval = computed(() => Boolean(cwd.value && sid.value))

  function mutationCompleted(result: CodeToolExecuteResultDto): boolean {
    if (result.status !== 'completed') {
      notify.warning({ message: result.summary, source: 'git' })
      return false
    }
    notify.success({ message: result.summary, source: 'git' })
    return true
  }

  function notifyOperationError(err: unknown, fallback: string) {
    notify.error(err instanceof Error ? err : fallback, { source: 'git' })
  }

  function actionApprovalId(action: UserToolActionDto): string {
    return action.action_approval_id ?? action.permission_request_id ?? action.id
  }

  function actionToApproval(action: UserToolActionDto, summary: string): ApprovalDto {
    const projected = userToolActionToApproval(action, summary, {
      sessionId: sid.value,
      cwd: cwd.value,
    })
    const id = projected.id
    userActions.set(id, action)
    return projected
  }

  async function createGitAction(toolId: string, args: Record<string, unknown>, summary: string): Promise<ApprovalDto> {
    if (!cwd.value) throw new Error('A project workspace is required.')
    const parameters = withGitToolConfirmation(toolId, {
      repository_path: cwd.value,
      ...args,
    })
    const idempotencyKey = await userToolActionIdempotencyKey(`desktop:git:${toolId}`, {
      project_path: cwd.value,
      parameters,
    })
    const action = await createUserToolActionForPath(cwd.value, toolId, parameters, idempotencyKey)
    return actionToApproval(action, summary)
  }

  async function resumeGitAction(approval: ApprovalDto | null): Promise<UserToolActionDto | null> {
    if (!approval) return null
    const current = userActions.get(approval.id)
    if (!current) return null
    const action = await api.resumeUserToolAction(current.id)
    userActions.set(approval.id, action)
    userActionStore.actions = { ...userActionStore.actions, [action.id]: action }
    return action
  }

  /** Only `blocked + error_category=snapshot_failed` may offer the one-shot override (docs/app-core-ui.md §6.2). */
  const snapshotOverrideCandidate = ref<UserToolActionDto | null>(null)

  async function overrideSnapshotAndResume(): Promise<void> {
    const candidate = snapshotOverrideCandidate.value
    if (!candidate) return
    try {
      const fresh = await api.overrideUserToolActionSnapshot(
        candidate.id,
        'User acknowledged non-reversible proceed-without-snapshot.',
      )
      userActions.set(fresh.id, fresh)
      const resumed = await api.resumeUserToolAction(fresh.id)
      userActions.set(resumed.id, resumed)
      actionCompleted(resumed)
      await refreshAll()
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      snapshotOverrideCandidate.value = null
    }
  }

  function actionCompleted(action: UserToolActionDto | null): boolean {
    if (!action) return false
    const message = userToolActionStatusMessage(action, action.tool_id)
    if (action.status === 'blocked' && action.error_category === 'snapshot_failed') {
      // Surface the one-time override instead of a generic warning.
      snapshotOverrideCandidate.value = action
      notify.warning({ message, source: 'git', persistence: 'sticky' })
      return false
    }
    if (action.status !== 'completed') {
      notify.warning({ message, source: 'git' })
      return false
    }
    notify.success({ message, source: 'git' })
    return true
  }

  // ---- Actions ----

  async function loadStatus() {
    const path = cwd.value
    if (!path) {
      preview.value = null
      pushPlan.value = null
      selectedPaths.value = new Set()
      return
    }
    loading.value = true
    try {
      const [nextPreview, nextPushPlan] = await Promise.all([
        api.executeCodeTool('git_worktree_manager', {
          cwd: path,
          arguments: { action: 'diff_preview', max_files: 120, max_diff_bytes: 180000 },
        }),
        api.executeCodeTool('git_worktree_manager', {
          cwd: path,
          arguments: { action: 'push_plan' },
        }),
      ])
      preview.value = nextPreview
      pushPlan.value = nextPushPlan
      syncSelection()
    } catch (err) {
      notifyOperationError(err, t('context.gitLoadFailed'))
    } finally {
      loading.value = false
    }
  }

  async function loadLog(limit = 50, ref?: string) {
    const path = cwd.value
    if (!path) return
    try {
      logResult.value = await api.gitLog(path, limit, ref)
    } catch (err) {
      logResult.value = null
      notifyOperationError(err, t('context.gitLoadFailed'))
    }
  }

  async function loadBranches() {
    const path = cwd.value
    if (!path) return
    try {
      const res = await api.executeCodeTool('git_worktree_manager', {
        cwd: path,
        arguments: { action: 'branch_list', all: true },
      })
      branchResult.value = res
    } catch (err) {
      notifyOperationError(err, t('context.gitLoadFailed'))
    }
  }

  async function refreshAll() {
    await Promise.all([loadStatus(), loadLog(), loadBranches()])
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
    if (selectAll.value || selectAllIndeterminate.value) {
      selectedPaths.value = new Set()
      selectAll.value = false
    } else {
      selectedPaths.value = new Set(statusFiles.value.map((f) => f.path))
      selectAll.value = true
    }
  }

  function selectAllFiles() {
    selectedPaths.value = new Set(statusFiles.value.map((f) => f.path))
    selectAll.value = true
  }

  // ---- Approval-gated operations ----

  async function requestIndexApproval(action: 'stage' | 'unstage', emitApproval: (a: ApprovalDto) => void, selection?: GitIndexSelection) {
    const selected = selection?.patch
      ? { patch: selection.patch }
      : { paths: selection?.paths?.length ? selection.paths : selectedCommitPaths.value }
    const paths = selected.paths ?? []
    if (!cwd.value || !sid.value || (!selected.patch && paths.length === 0)) return
    operationLoading.value = true
    try {
      const isStage = action === 'stage'
      const approval = await createGitAction(
        isStage ? 'git_stage' : 'git_unstage',
        selected.patch ? { patch: selected.patch } : { paths },
        selected.patch
          ? `${isStage ? 'Stage' : 'Unstage'} selected text hunks on ${previewData.value.branch ?? 'HEAD'}`
          : `${isStage ? 'Stage' : 'Unstage'} ${paths.length} file${paths.length === 1 ? '' : 's'} on ${previewData.value.branch ?? 'HEAD'}`,
      )
      indexApprovalId.value = approval.id
      indexAction.value = action
      indexSelection.value = selected
      notify.info({ message: t('context.gitIndexApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedIndexUpdate() {
    if (!cwd.value || !indexApproval.value || !indexAction.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(indexApproval.value)
      if (!actionCompleted(result)) return
      indexApprovalId.value = null
      indexAction.value = null
      indexSelection.value = null
      await loadStatus()
    } catch (err) {
      notifyOperationError(err, t('context.gitIndexUpdateFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestCommitApproval(emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !canRequestCommitApproval.value) return
    operationLoading.value = true
    try {
      const branch = previewData.value.branch ?? 'HEAD'
      const usePaths = commitUsesSelectedPaths.value
      const paths = [...selectedCommitPaths.value]
      const approval = usePaths
        ? await createGitAction('git_commit', {
            message: commitMessage.value.trim(), paths,
          }, `Commit ${paths.length} selected file${paths.length === 1 ? '' : 's'} on ${branch} (auto-stage)`)
        : await createGitAction('git_commit', {
            commit_staged_only: true, message: commitMessage.value.trim(),
          }, `Commit ${repoSummary.value.stagedCount} staged file${repoSummary.value.stagedCount === 1 ? '' : 's'} on ${branch}`)
      commitApprovalId.value = approval.id
      notify.info({ message: t('context.gitCommitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedCommit() {
    if (!cwd.value || !commitApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(commitApproval.value)
      if (!actionCompleted(result)) return
      commitMessage.value = ''
      commitApprovalId.value = null
      await loadStatus()
      await loadLog()
    } catch (err) {
      notifyOperationError(err, t('context.gitCommitFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestPushApproval(emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !canRequestPushApproval.value) return
    operationLoading.value = true
    try {
      const branch = pushData.value.branch ?? 'HEAD'
      const upstream = pushData.value.upstream ?? 'origin'
      const ahead = typeof pushData.value.ahead === 'number' ? pushData.value.ahead : 0
      const approval = await createGitAction('git_push', {
        set_upstream: noUpstreamOnly.value, remote: 'origin',
      }, `Push ${branch} to ${upstream} (${ahead} ahead)`)
      pushApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedPush() {
    if (!cwd.value || !pushApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(pushApproval.value)
      if (!actionCompleted(result)) return
      pushApprovalId.value = null
      await loadStatus()
    } catch (err) {
      notifyOperationError(err, t('context.gitPushFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestPullApproval(emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !canRequestPullApproval.value) return
    operationLoading.value = true
    try {
      const branch = previewData.value.branch ?? 'HEAD'
      const upstream = previewData.value.upstream ?? 'origin'
      const behind = repoSummary.value.behind
      // 只拉不推：git_pull 仅更新本地，不会把本地提交推到远端。
      const approval = await createGitAction('git_pull', {
        branch, remote: upstream.split('/')[0] ?? 'origin',
      }, `Pull ${branch} from ${upstream} (${behind} behind, ${pullStrategy.value}, no push)`)
      pullApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedPull() {
    if (!cwd.value || !pullApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(pullApproval.value)
      if (!actionCompleted(result)) return
      pullApprovalId.value = null
      await loadStatus()
    } catch (err) {
      notifyOperationError(err, t('context.gitPullFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestCheckoutApproval(branch: string, emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value) return
    operationLoading.value = true
    try {
      const approval = await createGitAction('git_checkout', { branch }, `Checkout branch ${branch}`)
      checkoutApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedCheckout() {
    if (!cwd.value || !checkoutApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(checkoutApproval.value)
      if (!actionCompleted(result)) return
      checkoutApprovalId.value = null
      await refreshAll()
    } catch (err) {
      notifyOperationError(err, t('context.gitCheckoutFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestCreateBranchApproval(branchName: string, emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !branchName.trim()) return
    operationLoading.value = true
    try {
      const approval = await createGitAction('git_branch_create', { branch: branchName }, `Create and checkout branch ${branchName}`)
      branchApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedCreateBranch() {
    if (!cwd.value || !branchApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(branchApproval.value)
      if (!actionCompleted(result)) return
      branchApprovalId.value = null
      await refreshAll()
    } catch (err) {
      notifyOperationError(err, t('context.gitBranchCreateFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestFetchApproval(emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !canRequestFetchApproval.value) return
    operationLoading.value = true
    try {
      const approval = await createGitAction('git_fetch', {}, `Fetch remote updates for ${previewData.value.branch ?? 'HEAD'}`)
      fetchApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedFetch() {
    if (!cwd.value || !fetchApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(fetchApproval.value)
      if (!actionCompleted(result)) return
      fetchApprovalId.value = null
      await loadBranches()
      await loadStatus()
    } catch (err) {
      notifyOperationError(err, t('context.gitFetchFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestMergeApproval(branch: string, emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !branch.trim()) return
    operationLoading.value = true
    try {
      const approval = await createGitAction('git_merge', { branch }, `Merge branch ${branch} into ${previewData.value.branch ?? 'HEAD'}`)
      mergeApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedMerge() {
    if (!cwd.value || !mergeApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(mergeApproval.value)
      if (!actionCompleted(result)) return
      mergeApprovalId.value = null
      await refreshAll()
    } catch (err) {
      notifyOperationError(err, t('context.gitMergeFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestRebaseApproval(branch: string, emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !branch.trim()) return
    operationLoading.value = true
    try {
      const approval = await createRebaseAction('start', { branch }, `Rebase ${previewData.value.branch ?? 'HEAD'} onto ${branch}`)
      rebaseApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  /**
   * Each rebase sub-command creates its own action with its own idempotency
   * key (`desktop:git-rebase:<op>`), so retrying "continue" never replays the
   * original "start", and abort/skip cannot collide with each other.
   */
  async function createRebaseAction(
    operation: 'start' | 'continue' | 'abort' | 'skip',
    args: Record<string, unknown>,
    summary: string,
  ): Promise<ApprovalDto> {
    if (!cwd.value) throw new Error('A project workspace is required.')
    const parameters = withGitToolConfirmation('git_rebase', {
      repository_path: cwd.value,
      operation,
      ...args,
    })
    const idempotencyKey = await userToolActionIdempotencyKey(`desktop:git-rebase:${operation}`, {
      project_path: cwd.value,
      parameters,
    })
    const action = await createUserToolActionForPath(cwd.value, 'git_rebase', parameters, idempotencyKey)
    return actionToApproval(action, summary)
  }

  async function executeApprovedRebase(operation: 'start' | 'continue' | 'abort' | 'skip' = 'start') {
    if (!cwd.value) return
    operationLoading.value = true
    try {
      // start resumes the approved action; continue/abort/skip are new actions.
      if (operation === 'start') {
        if (!rebaseApproval.value) return
        const result = await resumeGitAction(rebaseApproval.value)
        if (!actionCompleted(result)) return
      } else {
        const approval = await createRebaseAction(operation, {}, `Rebase ${operation} on ${previewData.value.branch ?? 'HEAD'}`)
        rebaseApprovalId.value = approval.id
      }
      await refreshAll()
    } catch (err) {
      notifyOperationError(err, t('context.gitRebaseFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestResolveConflictApproval(
    filePath: string,
    strategy: 'ours' | 'theirs' | 'both',
    emitApproval: (a: ApprovalDto) => void,
  ) {
    if (!cwd.value || !sid.value || !filePath.trim()) return
    operationLoading.value = true
    try {
      const approval = await createGitAction('git_conflict_resolve', { path: filePath, strategy }, `Resolve conflict in ${filePath} using ${strategy}`)
      resolveConflictApprovalId.value = approval.id
      resolveConflictPath.value = filePath
      resolveConflictStrategy.value = strategy
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedResolveConflict() {
    if (!cwd.value || !resolveConflictApproval.value) return
    if (!resolveConflictPath.value || !resolveConflictStrategy.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(resolveConflictApproval.value)
      if (!actionCompleted(result)) return
      resolveConflictApprovalId.value = null
      resolveConflictPath.value = null
      resolveConflictStrategy.value = null
      await loadStatus()
    } catch (err) {
      notifyOperationError(err, t('context.gitResolveConflictFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  // ---- Discard (destructive, irreversible): explicit paths + opt-in untracked delete ----
  async function requestDiscardApproval(
    paths: string[],
    includeUntracked: boolean,
    emitApproval: (a: ApprovalDto) => void,
  ) {
    const targets = [...new Set(paths.map((p) => p.trim()).filter(Boolean))]
    if (!cwd.value || !sid.value || targets.length === 0) return
    operationLoading.value = true
    try {
      const approval = await createGitAction('git_discard', {
        paths: targets, include_untracked: includeUntracked,
      }, `Discard ${targets.length} file${targets.length === 1 ? '' : 's'}${includeUntracked ? ' (incl. untracked)' : ''} on ${previewData.value.branch ?? 'HEAD'}`)
      discardApprovalId.value = approval.id
      discardPaths.value = targets
      discardIncludeUntracked.value = includeUntracked
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedDiscard() {
    if (!cwd.value || !discardApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(discardApproval.value)
      if (!actionCompleted(result)) return
      discardApprovalId.value = null
      discardPaths.value = []
      discardIncludeUntracked.value = false
      await loadStatus()
    } catch (err) {
      notifyOperationError(err, t('context.gitDiscardFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestDeleteBranchApproval(branch: string, force: boolean, emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !branch.trim()) return
    operationLoading.value = true
    try {
      const approval = await createGitAction('git_branch_delete', { branch, force }, `Delete branch ${branch}${force ? ' (force)' : ''}`)
      deleteBranchApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedDeleteBranch() {
    if (!cwd.value || !deleteBranchApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(deleteBranchApproval.value)
      if (!actionCompleted(result)) return
      deleteBranchApprovalId.value = null
      await loadBranches()
      await loadStatus()
    } catch (err) {
      notifyOperationError(err, t('context.gitDeleteBranchFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestRenameBranchApproval(newName: string, emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !newName.trim()) return
    operationLoading.value = true
    try {
      const approval = await createGitAction('git_branch_rename', { new_name: newName }, `Rename current branch to ${newName}`)
      renameBranchApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) {
      notifyOperationError(err, t('context.gitApprovalRequestFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function executeApprovedRenameBranch() {
    if (!cwd.value || !renameBranchApproval.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(renameBranchApproval.value)
      if (!actionCompleted(result)) return
      renameBranchApprovalId.value = null
      await refreshAll()
    } catch (err) {
      notifyOperationError(err, t('context.gitRenameBranchFailed'))
    } finally {
      operationLoading.value = false
    }
  }

  async function requestCreateWorktreeApproval(payload: { branch: string; path: string }, emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !payload.branch.trim() || !payload.path.trim()) return
    operationLoading.value = true
    try {
      const operation = { action: 'create' as const, branch: payload.branch.trim(), path: payload.path.trim() }
      const approval = await createGitAction('git_worktree_create', { branch: operation.branch, path: operation.path }, `Create worktree ${operation.path} for ${operation.branch}`)
      worktreeOperation.value = operation
      worktreeApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) { notifyOperationError(err, t('context.gitApprovalRequestFailed')) }
    finally { operationLoading.value = false }
  }

  async function requestRemoveWorktreeApproval(path: string, emitApproval: (a: ApprovalDto) => void) {
    if (!cwd.value || !sid.value || !path.trim()) return
    operationLoading.value = true
    try {
      const operation = { action: 'remove' as const, path: path.trim() }
      const approval = await createGitAction('git_worktree_remove', { path: operation.path }, `Remove worktree ${operation.path}`)
      worktreeOperation.value = operation
      worktreeApprovalId.value = approval.id
      notify.info({ message: t('context.gitApprovalRequested'), source: 'git' })
      emitApproval(approval)
    } catch (err) { notifyOperationError(err, t('context.gitApprovalRequestFailed')) }
    finally { operationLoading.value = false }
  }

  async function executeApprovedWorktreeOperation() {
    if (!cwd.value || !worktreeApproval.value || !worktreeOperation.value) return
    operationLoading.value = true
    try {
      const result = await resumeGitAction(worktreeApproval.value)
      if (!actionCompleted(result)) return
      worktreeApprovalId.value = null
      worktreeOperation.value = null
      await refreshAll()
    } catch (err) { notifyOperationError(err, t('context.gitWorktreeNeedsApproval')) }
    finally { operationLoading.value = false }
  }

  // ---- One-click approve & execute ----
  // Reduces the "request -> approve -> execute" flow to "request -> approve+execute".

  async function approveAndExecuteIndex(
    approval: ApprovalDto | null,
    decide: (approval: ApprovalDto, decision: 'approved' | 'rejected') => void,
  ) {
    if (!approval || approval.status !== 'pending') return
    decide(approval, 'approved')
    // Wait for the approval state to propagate through props.
    await nextTick()
    await executeApprovedIndexUpdate()
  }

  async function approveAndExecuteCommit(
    approval: ApprovalDto | null,
    decide: (approval: ApprovalDto, decision: 'approved' | 'rejected') => void,
  ) {
    if (!approval || approval.status !== 'pending') return
    decide(approval, 'approved')
    await nextTick()
    await executeApprovedCommit()
  }

  async function approveAndExecutePush(
    approval: ApprovalDto | null,
    decide: (approval: ApprovalDto, decision: 'approved' | 'rejected') => void,
  ) {
    if (!approval || approval.status !== 'pending') return
    decide(approval, 'approved')
    await nextTick()
    await executeApprovedPush()
  }

  async function approveAndExecutePull(
    approval: ApprovalDto | null,
    decide: (approval: ApprovalDto, decision: 'approved' | 'rejected') => void,
  ) {
    if (!approval || approval.status !== 'pending') return
    decide(approval, 'approved')
    await nextTick()
    await executeApprovedPull()
  }

  async function approveAndExecuteCheckout(
    approval: ApprovalDto | null,
    decide: (approval: ApprovalDto, decision: 'approved' | 'rejected') => void,
  ) {
    if (!approval || approval.status !== 'pending') return
    decide(approval, 'approved')
    await nextTick()
    await executeApprovedCheckout()
  }

  async function approveAndExecuteCreateBranch(
    approval: ApprovalDto | null,
    decide: (approval: ApprovalDto, decision: 'approved' | 'rejected') => void,
  ) {
    if (!approval || approval.status !== 'pending') return
    decide(approval, 'approved')
    await nextTick()
    await executeApprovedCreateBranch()
  }

  async function approveAndExecuteFetch(
    approval: ApprovalDto | null,
    decide: (approval: ApprovalDto, decision: 'approved' | 'rejected') => void,
  ) {
    if (!approval || approval.status !== 'pending') return
    decide(approval, 'approved')
    await nextTick()
    await executeApprovedFetch()
  }

  // ---- Utility ----

  function approvalStatusLabel(approval: ApprovalDto | null): string {
    if (!approval) return t('context.gitNoApproval')
    return `${approval.id} / ${approval.governance_status ?? approval.status}`
  }

  function decideGitApproval(
    approval: ApprovalDto | null,
    decision: 'approved' | 'rejected',
    decide: (approval: ApprovalDto, decision: 'approved' | 'rejected') => void,
  ) {
    if (!approval || approval.status !== 'pending') return
    decide(approval, decision)
  }

  function resetApprovals() {
    indexApprovalId.value = null
    indexAction.value = null
    commitApprovalId.value = null
    pushApprovalId.value = null
    pullApprovalId.value = null
    checkoutApprovalId.value = null
    branchApprovalId.value = null
    fetchApprovalId.value = null
    mergeApprovalId.value = null
    rebaseApprovalId.value = null
    resolveConflictApprovalId.value = null
    resolveConflictPath.value = null
    resolveConflictStrategy.value = null
    discardApprovalId.value = null
    discardPaths.value = []
    discardIncludeUntracked.value = false
    deleteBranchApprovalId.value = null
    renameBranchApprovalId.value = null
    worktreeApprovalId.value = null
    worktreeOperation.value = null
  }

  // ---- Watch ----
  watch(
    () => cwd.value,
    () => {
      resetApprovals()
      void loadStatus()
    },
    { immediate: true },
  )

  return {
    // State
    loading,
    operationLoading,
    commitMessage,
    pullStrategy,
    discardPaths,
    discardIncludeUntracked,
    selectedPaths,
    selectAll,
    selectAllIndeterminate,
    preview,
    pushPlan,
    logResult,
    branchResult,
    // Computed - data
    previewData,
    pushData,
    statusFiles,
    sections,
    totalChanges,
    repoSummary,
    recentCommits,
    logCommits,
    branches,
    pushBlockers,
    pushReady,
    noUpstreamOnly,
    hasPushCandidate,
    pushCommand,
    selectedCommitPaths,
    // Computed - approvals
    indexApproval,
    commitApproval,
    pushApproval,
    pullApproval,
    checkoutApproval,
    branchApproval,
    fetchApproval,
    mergeApproval,
    rebaseApproval,
    snapshotOverrideCandidate,
    resolveConflictApproval,
    discardApproval,
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
    canDecideDiscardApproval,
    canDecideDeleteBranchApproval,
    canDecideRenameBranchApproval,
    canDecideWorktreeApproval,
    // Computed - validation
    canRequestIndexApproval,
    canRequestDiscardApproval,
    canRequestCommitApproval,
    commitUsesSelectedPaths,
    canRequestPushApproval,
    canRequestPullApproval,
    canRequestFetchApproval,
    // Actions
    loadStatus,
    loadLog,
    loadBranches,
    refreshAll,
    overrideSnapshotAndResume,
    syncSelection,
    togglePath,
    toggleSelectAll,
    selectAllFiles,
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
    requestDiscardApproval,
    executeApprovedDiscard,
    requestDeleteBranchApproval,
    executeApprovedDeleteBranch,
    requestRenameBranchApproval,
    executeApprovedRenameBranch,
    requestCreateWorktreeApproval,
    requestRemoveWorktreeApproval,
    executeApprovedWorktreeOperation,
    // One-click approve & execute
    approveAndExecuteIndex,
    approveAndExecuteCommit,
    approveAndExecutePush,
    approveAndExecutePull,
    approveAndExecuteCheckout,
    approveAndExecuteCreateBranch,
    approveAndExecuteFetch,
    // Utils
    approvalStatusLabel,
    decideGitApproval,
    resetApprovals,
  }
}
