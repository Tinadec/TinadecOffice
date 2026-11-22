<script setup lang="ts">
import {
  AlertTriangle,
  Archive,
  GitBranch,
  GitCommit,
  RefreshCw,
  ArrowDown,
  ArrowUp,
  Download,
  FolderTree,
  FileCode2,
  Search,
  ShieldCheck,
  ShieldX,
  ChevronDown,
  ChevronRight,
  Trash2,
  Eye,
  Undo2,
  Upload,
  X,
  Filter,
} from '@lucide/vue'
import { computed, nextTick, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  api,
  type ApprovalDto,
  type ModelProviderInstanceDto,
  type OrchestrationSnapshotDto,
  type ToolExecutionTimelineItemDto,
} from '../api'
import { useGitOperation } from '../composables/useGitOperation'
import { useAgentDirectory } from '../composables/useAgentDirectory'
import type { ConversationFeed } from '../composables/useConversationFeed'
import type { TurnRunInput } from '../lib/conversation/turns'
import {
  isPathWithinRoot,
  pathComparisonKey,
  useSessionWorktrees,
  type RawWorktreeEntry,
  type WorkerWorktreeGroup,
  type WorktreeDiffFile,
  type WorktreeDiffSnapshot,
} from '../composables/useSessionWorktrees'
import {
  activeDockFocus,
  clearDockWorkerFilter,
  matchesWorkerTarget,
} from '../lib/homeDockSync'
import { openInEditor } from '../lib/documentWorkspace'
import GitChangesView from './git/GitChangesView.vue'
import DiffViewer from './git/DiffViewer.vue'
import SnapshotOverrideDialog from './governance/SnapshotOverrideDialog.vue'
import GitHistoryView from './git/GitHistoryView.vue'
import GitBranchView from './git/GitBranchView.vue'
import GitStashView from './git/GitStashView.vue'
import GitFileHistoryBlameDrawer from './git/GitFileHistoryBlameDrawer.vue'
import SnapshotsSubpanel from './git/SnapshotsSubpanel.vue'
import { useResponsiveMode } from '../composables/useElementSize'

const { t, te } = useI18n()
const lbl = (key: string, fallback: string) => (typeof te === 'function' && te(key) ? t(key) : fallback)

type GitTab = 'changes' | 'history' | 'branches' | 'stash'

const props = defineProps<{
  approvals: ApprovalDto[]
  currentProjectPath?: string
  selectedSessionId?: string | null
  runs?: readonly TurnRunInput[]
  feed?: ConversationFeed
  orchestration?: OrchestrationSnapshotDto | null
  toolExecutions?: readonly ToolExecutionTimelineItemDto[]
}>()

const emit = defineEmits<{
  'approval-created': [approval: ApprovalDto]
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected']
}>()

// Worktree context: allows inspecting either Main Workspace or any worker's isolated worktree.
const inspectedWorktreePath = ref<string | null>(null)
const effectiveCwd = computed(() => inspectedWorktreePath.value ?? props.currentProjectPath)
watch(() => props.currentProjectPath, () => {
  inspectedWorktreePath.value = null
  inspectedDiff.value = null
  workerFilter.value = null
})

// ---- Git operation composable ----
const {
  // State
  loading,
  operationLoading,
  commitMessage,
  amendCommit,
  pullStrategy,
  pendingConfirmation,
  selectedPaths,
  selectAll,
  selectAllIndeterminate,
  // Data
  previewData,
  pushData,
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
  discardApproval,
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
  canDecideDiscardApproval,
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
  canRequestDiscardApproval,
  canRequestCommitApproval,
  canQuickCommit,
  canQuickStage,
  canQuickDiscard,
  commitUsesSelectedPaths,
  canRequestPushApproval,
  canQuickPush,
  canRequestPullApproval,
  canQuickPull,
  canRequestFetchApproval,
  canQuickFetch,
  // Actions
  loadStatus,
  loadLog,
  loadBranches,
  refreshAll,
  togglePath,
  toggleSelectAll,
  requestIndexApproval,
  executeApprovedIndexUpdate,
  requestDiscardApproval,
  executeApprovedDiscard,
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
  // 1-click governed human Git & explicit confirmation flows
  quickStage,
  quickUnstage,
  quickCommit,
  quickCheckout,
  quickCreateBranch,
  quickFetch,
  quickPull,
  quickResolveConflict,
  quickStashPush,
  quickStashApply,
  quickStashPop,
  confirmPush,
  confirmDiscard,
  confirmDeleteBranch,
  confirmMerge,
  confirmRebase,
  confirmDropStash,
  confirmPendingAction,
  cancelConfirmation,
  // Utils
  approvalStatusLabel,
  decideGitApproval,
  snapshotOverrideCandidate,
  overrideSnapshotAndResume,
} = useGitOperation(
  () => effectiveCwd.value,
  () => props.selectedSessionId ?? null,
  () => props.approvals,
  () => props.currentProjectPath,
)

const cachedMainPreview = shallowRef<WorktreeDiffSnapshot | null>(null)
watch(previewData, (next) => {
  if (!inspectedWorktreePath.value && next && (Array.isArray(next.files) || Array.isArray(next.sections))) {
    cachedMainPreview.value = {
      branch: next.branch ?? null,
      upstream: next.upstream ?? null,
      ahead: next.ahead ?? 0,
      behind: next.behind ?? 0,
      files: Array.isArray(next.files) ? next.files : [],
      sections: Array.isArray(next.sections) ? next.sections : [],
    }
  }
}, { immediate: true })

const agentDirectory = typeof api?.listAgents === 'function' ? useAgentDirectory() : null
const resolveAgentName = agentDirectory
  ? agentDirectory.nameFor
  : ((slugOrHandle: string | null | undefined) => slugOrHandle ?? '')

type RuntimeEngineKind = 'acp' | 'cli' | 'tui' | 'api'
interface RuntimeEngineBadge {
  label: string
  kind: RuntimeEngineKind
}
interface RuntimeEngineMetaCandidate {
  runtime_badge?: string | null
  runtimeBadge?: string | null
  protocol?: string | null
  channel?: string | null
  connection_kind?: string | null
  provider?: string | null
  provider_name?: string | null
  provider_display_name?: string | null
  driver?: string | null
  model?: string | null
  model_id?: string | null
  provider_instance_id?: string | null
  providerInstanceId?: string | null
  recent_actual_model?: {
    protocol?: string | null
    provider_instance_id?: string | null
    model?: string | null
  } | null
}

const KNOWN_GIT_DRIVER_LABELS: Record<string, string> = {
  antigravity: 'Antigravity',
  'claude-code': 'Claude Code',
  claude_code: 'Claude Code',
  claude: 'Claude Code',
  codex: 'Codex',
  'codex-cli': 'Codex',
  'gemini-cli': 'Gemini CLI',
  gemini: 'Gemini CLI',
  opencode: 'OpenCode',
  openai: 'OpenAI',
  'openai-chat': 'OpenAI',
  'openai-responses': 'OpenAI',
  anthropic: 'Anthropic',
  'anthropic-messages': 'Anthropic',
}

function humanizeGitDriver(driver: string): string {
  const key = driver.trim().toLowerCase()
  if (!key) return ''
  if (KNOWN_GIT_DRIVER_LABELS[key]) return KNOWN_GIT_DRIVER_LABELS[key]!
  return driver
    .trim()
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((part) => (part.length <= 3 ? part.toUpperCase() : part[0]!.toUpperCase() + part.slice(1)))
    .join(' ')
}

function classifyGitEngineKind(text: string): RuntimeEngineKind {
  const head = text.trim().toLowerCase()
  if (head.startsWith('acp') || head.includes('acp')) return 'acp'
  if (head.startsWith('tui') || head.includes('tui')) return 'tui'
  if (head.startsWith('cli') || head.includes('cli') || head.includes('headless')) return 'cli'
  return 'api'
}

function formatGitEngineBadge(
  meta?: RuntimeEngineMetaCandidate | null,
  providers?: ReadonlyMap<string, ModelProviderInstanceDto>,
): RuntimeEngineBadge | null {
  if (!meta) return null
  const explicit = (meta.runtime_badge ?? meta.runtimeBadge ?? '').trim()
  if (explicit) {
    const normalized = explicit.replace(/\s*[·•|/]\s*/g, ' · ')
    return { label: normalized, kind: classifyGitEngineKind(normalized) }
  }
  const providerId = (meta.provider_instance_id ?? meta.providerInstanceId ?? meta.recent_actual_model?.provider_instance_id ?? '').trim()
  const providerRow = providerId && providers ? providers.get(providerId) : undefined
  const channel = (meta.channel ?? providerRow?.channel ?? '').trim().toLowerCase()
  const protocol = (meta.protocol ?? meta.recent_actual_model?.protocol ?? providerRow?.protocol ?? '').trim().toLowerCase()
  const connKind = (meta.connection_kind ?? providerRow?.connection_kind ?? '').trim().toLowerCase()
  const driver = (meta.driver ?? providerRow?.driver ?? '').trim()
  const displayName = (meta.provider_display_name ?? meta.provider_name ?? meta.provider ?? providerRow?.display_name ?? '').trim()
  const model = (meta.model ?? meta.model_id ?? meta.recent_actual_model?.model ?? providerRow?.model ?? '').trim()

  let prefix: string | null = null
  let kind: RuntimeEngineKind = 'api'
  if (channel === 'acp' || protocol === 'acp' || protocol.includes('acp')) {
    prefix = 'ACP'
    kind = 'acp'
  } else if (channel === 'tui' || protocol === 'tui' || protocol.includes('tui')) {
    prefix = 'TUI'
    kind = 'tui'
  } else if (channel === 'cli' || connKind === 'cli' || protocol === 'cli' || protocol.includes('cli') || protocol.includes('headless')) {
    prefix = 'CLI'
    kind = 'cli'
  } else if (protocol || channel === 'http' || channel === 'api' || driver || displayName) {
    prefix = 'API'
    kind = 'api'
  }
  if (!prefix) return null
  const name = displayName || humanizeGitDriver(driver) || model
  if (!name) return { label: prefix, kind }
  if (name.toUpperCase().startsWith(`${prefix} ·`) || name.toUpperCase() === prefix) return { label: name, kind }
  return { label: `${prefix} · ${name}`, kind }
}

const providerCatalog = shallowRef<ModelProviderInstanceDto[]>([])
onMounted(() => {
  if (typeof api?.listModelProviders === 'function') {
    void api.listModelProviders().then((rows) => {
      if (Array.isArray(rows)) providerCatalog.value = rows
    }).catch(() => {})
  }
})
const providerById = computed(() => {
  const map = new Map<string, ModelProviderInstanceDto>()
  for (const row of providerCatalog.value) {
    if (row?.id) map.set(row.id, row)
  }
  return map
})

function toRawWorktrees(rows: unknown): RawWorktreeEntry[] | undefined {
  if (!Array.isArray(rows)) return undefined
  return rows
    .filter((row): row is Record<string, unknown> => Boolean(row) && typeof row === 'object')
    .map((row) => ({
      path: String(row.path ?? row.worktree ?? ''),
      branch: typeof row.branch === 'string' ? row.branch : null,
      detached: typeof row.detached === 'string' ? row.detached : null,
      head: typeof row.head === 'string' ? row.head : typeof row.commit === 'string' ? row.commit : null,
      is_current: row.is_current === true || row.current === true,
      is_main: row.is_main === true || row.main === true,
    }))
    .filter((entry) => entry.path.length > 0)
}

const worktreeState = useSessionWorktrees({
  projectPath: () => props.currentProjectPath ?? null,
  sessionId: () => props.selectedSessionId ?? null,
  runs: () => props.runs,
  feed: () => props.feed,
  orchestration: () => props.orchestration,
  toolExecutions: () => props.toolExecutions,
  mainPreviewData: () => (!inspectedWorktreePath.value ? previewData.value : cachedMainPreview.value),
  externalWorktrees: () => toRawWorktrees(pushData.value?.worktrees),
  deferInitialProbe: true,
  resolveAgentName,
})

const workerWorktrees = worktreeState.groups
const isolatedWorktreeCount = computed(() => worktreeState.isolatedGroups.value.length)
const workerFilter = ref<string | null>(null)
const worktreeSectionExpanded = ref(false)
const inspectedDiff = ref<{ group: WorkerWorktreeGroup; file: WorktreeDiffFile } | null>(null)

watch(isolatedWorktreeCount, (count) => {
  if (count > 0) worktreeSectionExpanded.value = true
}, { immediate: true })

const visibleWorkerGroups = computed(() => {
  if (!workerFilter.value) return workerWorktrees.value
  const needle = workerFilter.value.toLowerCase()
  const filtered = workerWorktrees.value.filter((group) =>
    group.workerHandle?.toLowerCase() === needle
    || group.taskId === workerFilter.value
    || group.workerLabel?.toLowerCase().includes(needle)
    || group.branch.toLowerCase().includes(needle)
    || pathComparisonKey(group.worktreePath) === pathComparisonKey(workerFilter.value),
  )
  return filtered.length ? filtered : workerWorktrees.value
})

const inspectedGroup = computed<WorkerWorktreeGroup | null>(() => {
  if (!inspectedWorktreePath.value) return null
  const key = pathComparisonKey(inspectedWorktreePath.value)
  return workerWorktrees.value.find((group) => pathComparisonKey(group.worktreePath) === key) ?? null
})

function worktreeRuntimeBadge(group: WorkerWorktreeGroup | null | undefined): RuntimeEngineBadge | null {
  if (!group || group.isMain) return null
  const direct = formatGitEngineBadge(group as unknown as RuntimeEngineMetaCandidate, providerById.value)
  if (direct) return direct

  const wtKey = pathComparisonKey(group.worktreePath)
  const slugKey = (group.agentSlug ?? group.workerHandle?.split('#')[0] ?? '').trim()

  const snap = props.orchestration ?? null
  if (snap) {
    const snapAny = snap as unknown as {
      teammates?: Array<RuntimeEngineMetaCandidate & {
        handle?: string | null
        agent_slug?: string | null
        agentSlug?: string | null
        worktree_path?: string | null
        worktreePath?: string | null
        branch_name?: string | null
        branchName?: string | null
        task_id?: string | null
        taskId?: string | null
      }>
      agent_instances?: Array<RuntimeEngineMetaCandidate & {
        id?: string
        instance_id?: string
        task_id?: string | null
        role?: string | null
        agent_slug?: string | null
      }>
    }
    for (const tm of snapAny.teammates ?? []) {
      const tmPath = tm.worktree_path ?? tm.worktreePath
      const tmBranch = tm.branch_name ?? tm.branchName
      const tmTask = tm.task_id ?? tm.taskId
      const tmSlug = tm.agent_slug ?? tm.agentSlug
      if (
        (tmPath && wtKey && pathComparisonKey(tmPath) === wtKey)
        || (tmBranch && group.branch && tmBranch === group.branch)
        || (group.taskId && tmTask === group.taskId)
        || (group.workerHandle && tm.handle === group.workerHandle)
        || (slugKey && tmSlug === slugKey)
      ) {
        const hit = formatGitEngineBadge(tm, providerById.value)
        if (hit) return hit
      }
    }
    if (group.taskId) {
      const node = snap.nodes?.find((n) => n.id === group.taskId) as RuntimeEngineMetaCandidate | undefined
      const nodeHit = formatGitEngineBadge(node, providerById.value)
      if (nodeHit) return nodeHit
      const assign = snap.assignments?.find((a) => String(a.task_node_id) === group.taskId) as RuntimeEngineMetaCandidate | undefined
      const assignHit = formatGitEngineBadge(assign, providerById.value)
      if (assignHit) return assignHit
    }
    for (const inst of snapAny.agent_instances ?? []) {
      if (
        (group.taskId && inst.task_id && String(inst.task_id) === group.taskId)
        || (slugKey && (inst.agent_slug === slugKey || inst.role === slugKey))
      ) {
        const hit = formatGitEngineBadge(inst, providerById.value)
        if (hit) return hit
      }
    }
  }

  for (const run of worktreeState.topology.value?.runs ?? []) {
    if (group.runId && run.run_id !== group.runId) continue
    for (const task of run.tasks ?? []) {
      if (
        (group.taskId && task.task_id === group.taskId)
        || (group.workerHandle && task.handle === group.workerHandle)
      ) {
        const hit = formatGitEngineBadge(task as unknown as RuntimeEngineMetaCandidate, providerById.value)
        if (hit) return hit
      }
    }
    for (const inst of run.instances ?? []) {
      if (
        (group.taskId && inst.task_id === group.taskId)
        || (slugKey && (inst.agent_slug === slugKey || inst.role === slugKey))
      ) {
        const hit = formatGitEngineBadge(inst as unknown as RuntimeEngineMetaCandidate, providerById.value)
        if (hit) return hit
      }
    }
  }

  if (slugKey && agentDirectory) {
    const agentRow = agentDirectory.items.value.find((row) => row.slug === slugKey || row.id === slugKey)
    if (agentRow) {
      if (agentRow.recent_invocation && (!group.runId || agentRow.recent_invocation.run_id === group.runId)) {
        const hit = formatGitEngineBadge({
          protocol: agentRow.recent_invocation.protocol,
          provider_instance_id: agentRow.recent_invocation.provider_instance_id,
          model: agentRow.recent_invocation.model ?? null,
        }, providerById.value)
        if (hit) return hit
      }
      if (agentRow.model_binding?.mode === 'fixed' && agentRow.model_binding.provider_instance_id) {
        const hit = formatGitEngineBadge({
          provider_instance_id: agentRow.model_binding.provider_instance_id,
          model: agentRow.model_binding.model ?? null,
        }, providerById.value)
        if (hit) return hit
      }
      if (agentRow.configured_strategy?.kind === 'fixed' && agentRow.configured_strategy.provider_instance_id) {
        return formatGitEngineBadge({
          provider_instance_id: agentRow.configured_strategy.provider_instance_id,
          model: agentRow.configured_strategy.model ?? null,
        }, providerById.value)
      }
    }
  }

  return null
}

function isInspectingGroup(group: WorkerWorktreeGroup): boolean {
  if (!inspectedWorktreePath.value) return group.isMain
  return pathComparisonKey(group.worktreePath) === pathComparisonKey(inspectedWorktreePath.value)
}

function isGroupDockFocused(group: WorkerWorktreeGroup): boolean {
  const focus = activeDockFocus.value
  if (!focus || focus.card !== 'git') return false
  if (focus.worktreePath && pathComparisonKey(focus.worktreePath) === pathComparisonKey(group.worktreePath)) return true
  if (focus.branch && group.branch && focus.branch === group.branch) return true
  return matchesWorkerTarget(focus, {
    taskId: group.taskId,
    taskKey: group.taskKey,
    handle: group.workerHandle,
    agentSlug: group.agentSlug,
    label: group.workerLabel,
  })
}

function switchToWorktreePath(path: string): void {
  if (!props.currentProjectPath) return
  if (pathComparisonKey(path) === pathComparisonKey(props.currentProjectPath)) {
    inspectedWorktreePath.value = null
    return
  }
  if (isPathWithinRoot(path, props.currentProjectPath)) {
    inspectedWorktreePath.value = path
  }
}

function inspectGroupWorktree(group: WorkerWorktreeGroup): void {
  if (!group.isMain && (!props.currentProjectPath || !isPathWithinRoot(group.worktreePath, props.currentProjectPath))) {
    return
  }
  inspectedWorktreePath.value = group.isMain ? null : group.worktreePath
}

function returnToMainWorkspace(): void {
  inspectedWorktreePath.value = null
}

function openFileDiff(group: WorkerWorktreeGroup, file: WorktreeDiffFile): void {
  if (group.isMain || (props.currentProjectPath && isPathWithinRoot(group.worktreePath, props.currentProjectPath))) {
    inspectedWorktreePath.value = group.isMain ? null : group.worktreePath
  }
  inspectedDiff.value = { group, file }
}

async function cleanupWorktree(worktreePath: string): Promise<void> {
  if (inspectedWorktreePath.value && pathComparisonKey(inspectedWorktreePath.value) === pathComparisonKey(worktreePath)) {
    inspectedWorktreePath.value = null
    await nextTick()
  }
  await requestRemoveWorktreeApproval(worktreePath, emitApproval)
}

function clearFilter(): void {
  workerFilter.value = null
  clearDockWorkerFilter()
}

function refreshEverything(): void {
  void refreshAll()
  void worktreeState.refresh(true)
}

const overrideDialogOpen = computed({
  get: () => snapshotOverrideCandidate.value !== null,
  set: (open: boolean) => {
    if (!open) snapshotOverrideCandidate.value = null
  },
})

// ---- Tab navigation & JetBrains workbench UI ----
const activeTab = ref<GitTab>('changes')
const stashSubMode = ref<'stash' | 'snapshots'>('stash')
const selectedChangesFile = ref<string | null>(null)
const selectedHistoryHash = ref<string | null>(null)
const historyViewRef = ref<InstanceType<typeof GitHistoryView> | null>(null)
const branchViewRef = ref<InstanceType<typeof GitBranchView> | null>(null)
const stashViewRef = ref<InstanceType<typeof GitStashView> | null>(null)

async function handleStashPush(payload: { message: string; includeUntracked: boolean }) {
  const ok = await quickStashPush(payload.message, payload.includeUntracked)
  if (ok) await stashViewRef.value?.reload()
}

async function handleStashApply(stashRef: string, pop: boolean) {
  const ok = pop ? await quickStashPop(stashRef) : await quickStashApply(stashRef, false)
  if (ok) await stashViewRef.value?.reload()
}

const branchPopoverOpen = ref(false)
const branchSearch = ref('')
const filteredLocalBranches = computed(() => {
  const q = branchSearch.value.trim().toLowerCase()
  return branches.value.filter((b) => !b.is_remote && (!q || b.name.toLowerCase().includes(q)))
})
const filteredRemoteBranches = computed(() => {
  const q = branchSearch.value.trim().toLowerCase()
  return branches.value.filter((b) => b.is_remote && (!q || b.name.toLowerCase().includes(q)))
})
const filteredWorktreeGroups = computed(() => {
  const q = branchSearch.value.trim().toLowerCase()
  return workerWorktrees.value.filter(
    (g) =>
      !q
      || g.branch.toLowerCase().includes(q)
      || (g.workerLabel ?? '').toLowerCase().includes(q)
      || g.worktreePath.toLowerCase().includes(q),
  )
})

function toggleBranchPopover() {
  branchPopoverOpen.value = !branchPopoverOpen.value
  if (branchPopoverOpen.value) {
    branchSearch.value = ''
    if (branches.value.length === 0) void loadBranches()
  }
}

function selectPopoverBranch(branchName: string) {
  branchPopoverOpen.value = false
  if (branchName !== repoSummary.value.branch) {
    void quickCheckout(branchName)
  }
}

function selectPopoverWorktree(group: WorkerWorktreeGroup) {
  branchPopoverOpen.value = false
  inspectGroupWorktree(group)
}

const fileInspector = ref<{ open: boolean; filePath: string | null; tab: 'history' | 'blame' }>({
  open: false,
  filePath: null,
  tab: 'history',
})

function openFileHistory(path: string) {
  fileInspector.value = { open: true, filePath: path, tab: 'history' }
}

function openFileBlame(path: string) {
  fileInspector.value = { open: true, filePath: path, tab: 'blame' }
}

function openFileInCodeEditor(path: string) {
  if (!effectiveCwd.value) return
  openInEditor({ path, cwd: effectiveCwd.value })
}

function inspectCommitInHistory(hash: string) {
  fileInspector.value.open = false
  selectedHistoryHash.value = hash
  activeTab.value = 'history'
}

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
  {
    id: 'stash' as const,
    label: lbl('context.gitTabStashSnapshots', t('governance.snapshotsTitle')),
    icon: Archive,
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

watch(
  () => activeDockFocus.value?.nonce,
  async () => {
    const focus = activeDockFocus.value
    if (!focus || focus.card !== 'git') return
    if (focus.gitTab && (focus.gitTab === 'changes' || focus.gitTab === 'history' || focus.gitTab === 'branches' || focus.gitTab === 'stash')) {
      activeTab.value = focus.gitTab
    }
    if (focus.gitSubView === 'file_history' && focus.filePath) {
      openFileHistory(focus.filePath)
    } else if (focus.gitSubView === 'blame' && focus.filePath) {
      openFileBlame(focus.filePath)
    } else if (focus.gitSubView === 'diff' && focus.filePath) {
      activeTab.value = 'changes'
      selectedChangesFile.value = focus.filePath
    }
    worktreeSectionExpanded.value = true
    if (focus.workerHandle || focus.taskId || focus.worktreePath) {
      workerFilter.value = focus.workerHandle ?? focus.taskId ?? focus.worktreePath ?? null
    }
    if (focus.worktreePath) {
      const mainKey = pathComparisonKey(props.currentProjectPath)
      const targetKey = pathComparisonKey(focus.worktreePath)
      if (mainKey && targetKey === mainKey) {
        inspectedWorktreePath.value = null
      } else if (props.currentProjectPath && isPathWithinRoot(focus.worktreePath, props.currentProjectPath)) {
        inspectedWorktreePath.value = focus.worktreePath
      }
    }
    await worktreeState.refresh(true)
    const matchedGroup = visibleWorkerGroups.value.find((group) => isGroupDockFocused(group)) ?? null
    if (focus.filePath && matchedGroup) {
      const matchedFile = matchedGroup.files.find((f) => pathComparisonKey(f.path) === pathComparisonKey(focus.filePath))
      if (matchedFile) inspectedDiff.value = { group: matchedGroup, file: matchedFile }
    }
    if (focus.requestCleanupPath) {
      void cleanupWorktree(focus.requestCleanupPath)
    }
    await nextTick()
    const el = panelRef.value?.querySelector<HTMLElement>('.git-worktree-group.is-focused, .git-worktree-group.is-active')
    if (el && typeof el.scrollIntoView === 'function') {
      el.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
    }
  },
)

// ---- Emit helpers ----
function emitApproval(a: ApprovalDto) {
  emit('approval-created', a)
}

function decideApproval(a: ApprovalDto | null, decision: 'approved' | 'rejected') {
  decideGitApproval(a, decision, (approval, dec) => emit('decide-approval', approval, dec))
}
</script>

<template>
  <section ref="panelRef" class="panel git-manager-view" :class="{ 'git-compact': isCompact }">
    <!-- Header: JetBrains Branch/Worktree Popover + Quick Sync Toolbar -->
    <div class="git-header">
      <div class="git-header-left">
        <GitBranch :size="15" class="git-header-icon" />
        <div class="git-branch-info">
          <div class="git-branch-title-row">
            <button
              type="button"
              class="git-branch-trigger"
              data-testid="git-branch-popover-trigger"
              :title="lbl('context.gitBranchSwitchPopover', 'Switch branch or worktree')"
              @click="toggleBranchPopover"
            >
              <strong class="git-branch-name">{{ repoSummary.branch }}</strong>
              <ChevronDown :size="12" class="git-branch-chevron" />
            </button>
            <span
              class="git-scope-pill"
              :class="inspectedGroup && !inspectedGroup.isMain ? 'is-isolated' : 'is-main'"
              data-testid="git-active-scope-badge"
            >
              {{ inspectedGroup && !inspectedGroup.isMain ? t('worktreeDock.isolatedWorktree') : t('worktreeDock.mainWorkspace') }}
            </span>
            <span
              v-if="inspectedGroup && !inspectedGroup.isMain && worktreeRuntimeBadge(inspectedGroup)"
              class="git-worktree-engine-badge"
              :data-engine-kind="worktreeRuntimeBadge(inspectedGroup)!.kind"
              :title="worktreeRuntimeBadge(inspectedGroup)!.label"
              data-testid="git-active-engine-badge"
            >
              {{ worktreeRuntimeBadge(inspectedGroup)!.label }}
            </span>
          </div>
          <span v-if="inspectedGroup && !inspectedGroup.isMain" class="git-branch-upstream">
            {{ inspectedGroup.workerLabel ?? t('worktreeDock.isolatedWorktree') }} · {{ inspectedGroup.worktreePath }}
          </span>
          <span v-else-if="repoSummary.upstream" class="git-branch-upstream">
            ← {{ repoSummary.upstream }}
          </span>
        </div>

        <!-- Searchable Branch & Worktree Switch Popover -->
        <div v-if="branchPopoverOpen" class="git-branch-popover" data-testid="git-branch-popover" @click.stop>
          <div class="git-branch-popover-search">
            <Search :size="12" aria-hidden="true" />
            <input
              v-model="branchSearch"
              type="text"
              class="git-branch-popover-input"
              :placeholder="t('context.gitBranchFilter')"
              data-testid="git-branch-popover-search"
            />
          </div>
          <div class="git-branch-popover-body">
            <div v-if="filteredLocalBranches.length > 0" class="git-branch-popover-group">
              <div class="git-branch-popover-title">{{ t('context.gitLocalBranches') }}</div>
              <button
                v-for="b in filteredLocalBranches"
                :key="`local-${b.name}`"
                type="button"
                class="git-branch-popover-item"
                :class="{ current: b.is_current || b.name === repoSummary.branch }"
                @click="selectPopoverBranch(b.name)"
              >
                <GitBranch :size="12" />
                <span>{{ b.name }}</span>
              </button>
            </div>
            <div v-if="filteredRemoteBranches.length > 0" class="git-branch-popover-group">
              <div class="git-branch-popover-title">{{ t('context.gitRemoteBranches') }}</div>
              <button
                v-for="b in filteredRemoteBranches"
                :key="`remote-${b.name}`"
                type="button"
                class="git-branch-popover-item"
                @click="selectPopoverBranch(b.name)"
              >
                <GitBranch :size="12" />
                <span>{{ b.name }}</span>
              </button>
            </div>
            <div v-if="filteredWorktreeGroups.length > 0" class="git-branch-popover-group">
              <div class="git-branch-popover-title">{{ t('context.gitTabWorktrees') }}</div>
              <button
                v-for="g in filteredWorktreeGroups"
                :key="`wt-${g.id}`"
                type="button"
                class="git-branch-popover-item"
                :class="{ current: isInspectingGroup(g) }"
                @click="selectPopoverWorktree(g)"
              >
                <FolderTree :size="12" />
                <span>{{ g.workerLabel ?? g.branch }} ({{ g.branch }})</span>
              </button>
            </div>
          </div>
        </div>
      </div>
      <div class="git-header-right">
        <button
          v-if="inspectedWorktreePath"
          type="button"
          class="git-scope-reset-btn"
          data-testid="git-back-to-main"
          @click="returnToMainWorkspace"
        >
          <Undo2 :size="12" />
          <span>{{ t('worktreeDock.backToMain') }}</span>
        </button>
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
        <div v-if="currentProjectPath" class="git-header-sync-btns">
          <button
            type="button"
            class="icon-button git-header-sync-btn"
            data-testid="git-header-fetch-btn"
            :title="t('context.gitFetchTitle')"
            :disabled="operationLoading || !canQuickFetch"
            @click="quickFetch"
          >
            <RefreshCw :size="12" />
          </button>
          <button
            type="button"
            class="icon-button git-header-sync-btn"
            data-testid="git-header-pull-btn"
            :title="t('context.gitPullOnlyTitle')"
            :disabled="operationLoading || !canQuickPull"
            @click="quickPull"
          >
            <Download :size="12" />
          </button>
          <button
            type="button"
            class="icon-button git-header-sync-btn"
            data-testid="git-header-push-btn"
            :title="t('context.gitPushTitle')"
            :disabled="operationLoading || !canQuickPush"
            @click="confirmPush"
          >
            <Upload :size="12" />
          </button>
        </div>
        <button
          class="icon-button git-header-refresh"
          :title="t('context.refreshGitPlan')"
          :disabled="loading || !currentProjectPath"
          @click="refreshEverything"
        >
          <RefreshCw :size="14" :class="{ spinning: loading }" />
        </button>
      </div>
    </div>

    <!-- Explicit In-Panel Confirmation Dialog for destructive / remote actions -->
    <div
      v-if="pendingConfirmation"
      class="git-confirm-dialog"
      role="alertdialog"
      data-testid="git-confirm-dialog"
    >
      <div class="git-confirm-head">
        <AlertTriangle :size="15" class="git-confirm-icon" />
        <strong>{{ pendingConfirmation.title || lbl('context.gitConfirmActionTitle', 'Confirm Git Action') }}</strong>
      </div>
      <p class="git-confirm-detail">{{ pendingConfirmation.detail }}</p>
      <div v-if="pendingConfirmation.paths && pendingConfirmation.paths.length > 0" class="git-confirm-paths">
        <code v-for="p in pendingConfirmation.paths.slice(0, 4)" :key="p">{{ p }}</code>
        <small v-if="pendingConfirmation.paths.length > 4">+{{ pendingConfirmation.paths.length - 4 }}</small>
      </div>
      <div class="git-confirm-actions">
        <button
          type="button"
          class="secondary-button git-confirm-cancel-btn"
          data-testid="git-confirm-cancel-btn"
          :disabled="operationLoading"
          @click="cancelConfirmation"
        >
          {{ t('context.gitCompareCancel') }}
        </button>
        <button
          type="button"
          class="primary-button git-confirm-accept-btn"
          data-testid="git-confirm-accept-btn"
          :disabled="operationLoading"
          @click="confirmPendingAction"
        >
          {{ pendingConfirmation.title }}
        </button>
      </div>
    </div>

    <!-- No project state -->
    <div v-if="!currentProjectPath" class="git-empty">
      <FolderTree :size="32" />
      <span>{{ t('context.diffPlaceholder') }}</span>
    </div>

    <!-- Loading state -->
    <div v-else-if="loading && statusFiles.length === 0 && workerWorktrees.length === 0" class="git-empty">
      <RefreshCw :size="24" class="spinning" />
      <span>{{ t('context.loadingGitPlan') }}</span>
    </div>

    <!-- Main content -->
    <template v-else>
      <!-- Worker-grouped Worktree summary & 1-click diff inspector -->
      <div
        v-if="workerWorktrees.length > 0"
        class="git-worktree-section"
        data-testid="git-worker-worktree-section"
      >
        <div class="git-worktree-section-bar">
          <button
            type="button"
            class="git-worktree-section-toggle"
            :aria-expanded="worktreeSectionExpanded"
            data-testid="git-worker-worktree-toggle"
            @click="worktreeSectionExpanded = !worktreeSectionExpanded"
          >
            <ChevronDown v-if="worktreeSectionExpanded" :size="13" />
            <ChevronRight v-else :size="13" />
            <span>{{ t('worktreeDock.sectionTitle') }}</span>
            <span v-if="isolatedWorktreeCount > 0" class="git-worktree-count-pill">
              {{ t('worktreeDock.isolatedCount', { count: isolatedWorktreeCount }) }}
            </span>
          </button>
          <button
            v-if="workerFilter"
            type="button"
            class="git-worker-filter-chip"
            data-testid="git-worker-filter-clear"
            @click="clearFilter"
          >
            <Filter :size="11" />
            <span>{{ t('worktreeDock.filterWorker', { worker: workerFilter }) }}</span>
            <span class="git-filter-clear-text">{{ t('worktreeDock.clearFilter') }}</span>
          </button>
        </div>

        <div v-if="worktreeSectionExpanded" class="git-worktree-groups" data-testid="git-worker-worktree-list">
          <article
            v-for="group in visibleWorkerGroups"
            :key="group.id"
            class="git-worktree-group"
            :class="{
              'is-main': group.isMain,
              'is-isolated': !group.isMain,
              'is-active': isInspectingGroup(group),
              'is-focused': isGroupDockFocused(group),
            }"
            :data-worker-task-id="group.taskId ?? undefined"
            :data-worktree-path="group.worktreePath"
            data-testid="git-worker-worktree-group"
          >
            <header class="git-worktree-group-head">
              <div class="git-worktree-group-identity">
                <span class="git-scope-pill" :class="group.isMain ? 'is-main' : 'is-isolated'">
                  {{ group.isMain ? t('worktreeDock.mainWorkspace') : t('worktreeDock.isolatedWorktree') }}
                </span>
                <strong class="git-worktree-worker-name">
                  {{ group.workerLabel ?? (group.isMain ? t('worktreeDock.mainWorkspace') : t('worktreeDock.unassignedWorker')) }}
                </strong>
                <span
                  v-if="!group.isMain && worktreeRuntimeBadge(group)"
                  class="git-worktree-engine-badge"
                  :data-engine-kind="worktreeRuntimeBadge(group)!.kind"
                  :title="worktreeRuntimeBadge(group)!.label"
                  data-testid="git-worktree-engine-badge"
                >
                  {{ worktreeRuntimeBadge(group)!.label }}
                </span>
                <span v-if="group.branch" class="git-worktree-branch-pill" data-testid="git-worker-branch-name">
                  <GitBranch :size="11" />
                  <span>{{ group.branch }}</span>
                </span>
              </div>
              <span class="git-worktree-diff-counts" data-testid="git-worker-diff-counts">
                <span class="count-add">+{{ group.counts.added }}</span>
                <span class="count-mod">~{{ group.counts.modified }}</span>
                <span class="count-del">-{{ group.counts.deleted }}</span>
              </span>
            </header>

            <p v-if="group.taskTitle" class="git-worktree-task-title">{{ group.taskTitle }}</p>

            <div v-if="group.lineage.length > 0" class="git-worktree-lineage" data-testid="git-worker-lineage">
              <span class="git-lineage-label">{{ t('worktreeDock.lineageLabel') }}:</span>
              <span class="git-lineage-chain">
                <template v-for="(step, idx) in group.lineage" :key="step.taskId">
                  <span v-if="idx > 0" class="git-lineage-sep">›</span>
                  <span class="git-lineage-node">{{ step.label }}</span>
                </template>
              </span>
            </div>

            <div v-if="group.isMain && group.sharedWorkers.length > 0" class="git-shared-workers">
              <span class="git-lineage-label">{{ t('worktreeDock.sharedWorkspaceHint') }}:</span>
              <span
                v-for="worker in group.sharedWorkers"
                :key="worker.taskId"
                class="git-shared-worker-chip"
                :data-worker-task-id="worker.taskId"
              >
                {{ worker.label }}
              </span>
            </div>

            <div v-if="group.files.length > 0" class="git-worktree-files" data-testid="git-worker-file-list">
              <button
                v-for="file in group.files.slice(0, 8)"
                :key="file.path"
                type="button"
                class="git-worktree-file-chip"
                :class="{ active: inspectedDiff?.file.path === file.path && inspectedDiff?.group.id === group.id }"
                data-testid="git-worker-file-diff-btn"
                @click="openFileDiff(group, file)"
              >
                <span class="git-file-status-dot" :data-kind="file.statusBadge">{{ file.statusBadge }}</span>
                <span class="git-file-chip-path">{{ file.path }}</span>
                <span v-if="file.additions || file.deletions" class="git-file-chip-delta">+{{ file.additions }}/-{{ file.deletions }}</span>
              </button>
            </div>
            <p v-else class="git-worktree-clean-hint">{{ t('worktreeDock.noChangedFiles') }}</p>

            <footer class="git-worktree-group-actions">
              <button
                type="button"
                class="git-worktree-action-btn"
                data-testid="git-inspect-worktree-btn"
                @click="inspectGroupWorktree(group)"
              >
                <Eye :size="12" />
                <span>{{ isInspectingGroup(group) ? t('worktreeDock.activeScope') : t('worktreeDock.inspectWorktree') }}</span>
              </button>
              <button
                v-if="group.canCleanup"
                type="button"
                class="git-worktree-action-btn is-danger"
                :disabled="operationLoading"
                data-testid="git-cleanup-worktree-btn"
                @click="cleanupWorktree(group.worktreePath)"
              >
                <Trash2 :size="12" />
                <span>{{ t('worktreeDock.cleanupWorktree') }}</span>
              </button>
            </footer>
          </article>

          <div v-if="worktreeApproval" class="git-worktree-approval-bar" data-testid="git-worktree-approval-bar">
            <ShieldCheck :size="14" />
            <span class="git-worktree-approval-text">{{ worktreeApproval.summary }}</span>
            <span class="git-worktree-approval-status">{{ approvalStatusLabel(worktreeApproval) }}</span>
            <div v-if="canDecideWorktreeApproval" class="git-worktree-approval-actions">
              <button
                type="button"
                class="icon-button approve"
                :title="t('approval.approve')"
                @click="decideApproval(worktreeApproval, 'approved')"
              >
                <ShieldCheck :size="13" />
              </button>
              <button
                type="button"
                class="icon-button reject"
                :title="t('approval.reject')"
                @click="decideApproval(worktreeApproval, 'rejected')"
              >
                <ShieldX :size="13" />
              </button>
            </div>
            <button
              v-if="worktreeApproval.status === 'approved'"
              type="button"
              class="git-worktree-action-btn"
              :disabled="operationLoading"
              data-testid="git-execute-worktree-btn"
              @click="executeApprovedWorktreeOperation"
            >
              <span>{{ t('context.gitWorktreeExecute') }}</span>
            </button>
          </div>

          <div v-if="inspectedDiff" class="git-worktree-diff-drawer" data-testid="git-worktree-diff-drawer">
            <div class="git-worktree-diff-drawer-head">
              <span class="git-worktree-branch-pill">
                <GitBranch :size="11" />
                <span>{{ inspectedDiff.group.branch }}</span>
              </span>
              <strong class="git-worktree-diff-file-title">{{ inspectedDiff.file.path }}</strong>
              <button
                type="button"
                class="icon-button git-worktree-diff-close"
                :title="t('worktreeDock.closeDiff')"
                @click="inspectedDiff = null"
              >
                <X :size="13" />
              </button>
            </div>
            <div class="git-worktree-diff-viewer-wrap">
              <DiffViewer
                :file-path="inspectedDiff.file.path"
                :diff-text="inspectedDiff.file.diffText || inspectedDiff.group.rawDiff"
                :additions="inspectedDiff.file.additions"
                :deletions="inspectedDiff.file.deletions"
                :binary="inspectedDiff.file.binary"
                :truncated="inspectedDiff.file.truncated"
              />
            </div>
          </div>
        </div>
      </div>

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
          :cwd="effectiveCwd"
          :session-id="selectedSessionId ?? null"
          :loading="loading"
          :operation-loading="operationLoading"
          :status-files="statusFiles"
          :commit-message="commitMessage"
          :amend-commit="amendCommit"
          :compact="isCompact"
          :initial-selected-file="selectedChangesFile"
          :use-quick-flow="true"
          :can-quick-stage="canQuickStage"
          :can-quick-commit="canQuickCommit"
          :can-quick-discard="canQuickDiscard"
          :can-quick-push="canQuickPush"
          :can-quick-pull="canQuickPull"
          :can-quick-fetch="canQuickFetch"
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
          :can-request-fetch-approval="canRequestFetchApproval"
          :behind="repoSummary.behind"
          :staged-count="repoSummary.stagedCount"
          :commit-uses-selected-paths="commitUsesSelectedPaths"
          :pull-strategy="pullStrategy"
          :index-approval="indexApproval"
          :commit-approval="commitApproval"
          :push-approval="pushApproval"
          :pull-approval="pullApproval"
          :fetch-approval="fetchApproval"
          :discard-approval="discardApproval"
          :resolve-conflict-approval="resolveConflictApproval"
          :can-request-index-approval="canRequestIndexApproval"
          :can-request-discard-approval="canRequestDiscardApproval"
          :can-request-commit-approval="canRequestCommitApproval"
          :can-decide-index-approval="canDecideIndexApproval"
          :can-decide-commit-approval="canDecideCommitApproval"
          :can-decide-push-approval="canDecidePushApproval"
          :can-decide-pull-approval="canDecidePullApproval"
          :can-decide-fetch-approval="canDecideFetchApproval"
          :can-decide-discard-approval="canDecideDiscardApproval"
          :can-decide-resolve-conflict-approval="canDecideResolveConflictApproval"
          :recent-commits="recentCommits"
          @update:commit-message="commitMessage = $event"
          @update:amend-commit="amendCommit = $event"
          @update:pull-strategy="pullStrategy = $event"
          @refresh="loadStatus"
          @toggle-path="togglePath"
          @toggle-select-all="toggleSelectAll"
          @quick-stage="quickStage($event)"
          @quick-unstage="quickUnstage($event)"
          @quick-commit="quickCommit($event)"
          @quick-discard="(paths, includeUntracked) => confirmDiscard(paths, includeUntracked)"
          @quick-resolve-conflict="(path, strategy) => quickResolveConflict(path, strategy)"
          @quick-fetch="quickFetch"
          @quick-pull="quickPull"
          @quick-push="confirmPush"
          @show-history="openFileHistory($event)"
          @annotate-blame="openFileBlame($event)"
          @open-in-editor="openFileInCodeEditor($event)"
          @request-stage="(selection) => requestIndexApproval('stage', emitApproval, selection)"
          @request-unstage="(selection) => requestIndexApproval('unstage', emitApproval, selection)"
          @execute-index="executeApprovedIndexUpdate"
          @request-commit="requestCommitApproval(emitApproval)"
          @execute-commit="executeApprovedCommit"
          @request-discard="(paths, includeUntracked) => requestDiscardApproval(paths, includeUntracked, emitApproval)"
          @execute-discard="executeApprovedDiscard"
          @request-push="requestPushApproval(emitApproval)"
          @execute-push="executeApprovedPush"
          @request-pull="requestPullApproval(emitApproval)"
          @execute-pull="executeApprovedPull"
          @request-fetch="requestFetchApproval(emitApproval)"
          @execute-fetch="executeApprovedFetch"
          @request-resolve-conflict="(path, strategy) => requestResolveConflictApproval(path, strategy, emitApproval)"
          @execute-resolve-conflict="executeApprovedResolveConflict"
          @decide-approval="decideApproval"
        />

        <!-- History view -->
        <GitHistoryView
          v-show="activeTab === 'history'"
          ref="historyViewRef"
          :cwd="effectiveCwd"
          :commits="logCommits"
          :branches="branches"
          :initial-hash="selectedHistoryHash"
          :loading="false"
          @refresh="loadLog(50)"
        />

        <!-- Branch view -->
        <GitBranchView
          v-show="activeTab === 'branches'"
          ref="branchViewRef"
          :cwd="effectiveCwd"
          :current-branch="repoSummary.branch"
          :branches="branches"
          :use-quick-flow="true"
          :can-quick-fetch="canQuickFetch"
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
          @checkout="quickCheckout($event)"
          @create-branch="quickCreateBranch($event)"
          @fetch="quickFetch()"
          @delete-branch="confirmDeleteBranch($event.branch, $event.force)"
          @rename-branch="requestRenameBranchApproval($event, emitApproval)"
          @create-worktree="requestCreateWorktreeApproval($event, emitApproval)"
          @remove-worktree="requestRemoveWorktreeApproval($event, emitApproval)"
          @merge-branch="confirmMerge($event)"
          @rebase-branch="confirmRebase($event)"
          @switch-worktree="switchToWorktreePath($event)"
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

        <!-- Stash & Snapshots combined view -->
        <div v-if="activeTab === 'stash'" class="git-stash-tab-wrap" data-testid="git-stash-tab-panel">
          <div class="git-stash-subbar">
            <button
              type="button"
              class="git-stash-subpill"
              :class="{ active: stashSubMode === 'stash' }"
              data-testid="git-subpill-stash"
              @click="stashSubMode = 'stash'"
            >
              {{ lbl('context.gitStashTitle', 'Git Stash') }}
            </button>
            <button
              type="button"
              class="git-stash-subpill"
              :class="{ active: stashSubMode === 'snapshots' }"
              data-testid="git-subpill-snapshots"
              @click="stashSubMode = 'snapshots'"
            >
              {{ t('governance.snapshotsTitle') }}
            </button>
          </div>
          <GitStashView
            v-if="stashSubMode === 'stash'"
            ref="stashViewRef"
            :cwd="effectiveCwd"
            :operation-loading="operationLoading"
            @stash-push="handleStashPush($event)"
            @stash-apply="handleStashApply($event, false)"
            @stash-pop="handleStashApply($event, true)"
            @stash-drop="(ref, reload) => confirmDropStash(ref, reload)"
          />
          <SnapshotsSubpanel
            v-else
            :compact="isCompact"
          />
        </div>
      </div>

      <!-- Approval disclaimer -->
      <div class="git-disclaimer">
        <ShieldCheck :size="12" />
        <span>{{ t('context.gitPlanApproval') }}</span>
      </div>
    </template>

    <!-- File History & Blame slide-in drawer -->
    <GitFileHistoryBlameDrawer
      :open="fileInspector.open"
      :cwd="effectiveCwd"
      :file-path="fileInspector.filePath"
      :initial-tab="fileInspector.tab"
      @close="fileInspector.open = false"
      @open-commit="inspectCommitInHistory($event)"
    />

    <!-- One-shot snapshot override (blocked + snapshot_failed only) -->
    <SnapshotOverrideDialog
      v-model:open="overrideDialogOpen"
      :action-id="snapshotOverrideCandidate?.id ?? ''"
      :tool-id="snapshotOverrideCandidate?.tool_id ?? ''"
      @confirmed="overrideSnapshotAndResume"
    />
  </section>
</template>

<style scoped>
.git-manager-view {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  height: 100%;
  overflow: hidden;
}

/* ---- Header island ---- */
.git-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 10px 12px;
  background: var(--surface-section);
  border: 1px solid var(--border-card);
  border-radius: 12px;
  box-shadow: var(--shadow-card-subtle);
  backdrop-filter: var(--material-filter-section, none);
  -webkit-backdrop-filter: var(--material-filter-section, none);
  flex-shrink: 0;
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
  font-family: 'Geist Variable', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', 'Noto Sans SC', 'PingFang SC', 'Microsoft YaHei', sans-serif;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-branch-upstream {
  font-size: 10px;
  color: var(--text-muted);
  font-family: 'Geist Variable', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', 'Noto Sans SC', 'PingFang SC', 'Microsoft YaHei', sans-serif;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
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
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  color: var(--text-approve);
  background: color-mix(in srgb, var(--text-approve) 12%, transparent);
}

.git-behind {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  color: var(--accent-warning);
  background: color-mix(in srgb, var(--accent-warning) 12%, transparent);
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

/* ---- Tab navigation (segmented island) ---- */
.git-nav {
  display: flex;
  gap: 3px;
  padding: 3px;
  background: var(--surface-section);
  border: 1px solid var(--border-card);
  border-radius: 10px;
  box-shadow: var(--shadow-card-subtle);
  backdrop-filter: var(--material-filter-section, none);
  -webkit-backdrop-filter: var(--material-filter-section, none);
  overflow-x: auto;
  flex-shrink: 0;
}

.git-nav-item {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 6px 10px;
  background: transparent;
  border: 0;
  border-radius: 8px;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
  transition: background-color 0.15s ease, color 0.15s ease, box-shadow 0.15s ease;
}

.git-nav-item:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.git-nav-item:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: -1px;
}

.git-nav-item.active {
  color: var(--accent-primary);
  background: var(--surface-raised);
  box-shadow: var(--shadow-subtle);
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
  font-variant-numeric: tabular-nums;
  color: hsl(var(--primary-foreground));
  background: var(--accent-primary);
  border-radius: 999px;
}

/* ---- Tab content (subviews own their island spacing) ---- */
.git-tab-content {
  flex: 1;
  overflow-y: auto;
  padding: 0 0 8px;
}

/* ---- Disclaimer ---- */
.git-disclaimer {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 2px 12px 0;
  font-size: 10px;
  color: var(--text-muted);
  flex-shrink: 0;
}

/* ---- Compact mode ---- */
.git-compact {
  padding: 8px;
  gap: 6px;
}

.git-compact .git-nav-item {
  padding: 6px 8px;
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

/* ---- Worker Worktrees & Scope Highlights ---- */
.git-branch-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
}

.git-scope-pill {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
  line-height: 1.4;
  white-space: nowrap;
  flex-shrink: 0;
}

.git-scope-pill.is-main {
  color: var(--text-secondary);
  background: color-mix(in srgb, var(--border-muted) 45%, transparent);
}

.git-scope-pill.is-isolated {
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 15%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-primary) 35%, transparent);
}

.git-scope-reset-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 26px;
  padding: 0 8px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 38%, var(--border-card));
  border-radius: 6px;
  background: color-mix(in srgb, var(--accent-primary) 10%, transparent);
  color: var(--accent-primary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
  flex-shrink: 0;
}

.git-scope-reset-btn:hover {
  background: color-mix(in srgb, var(--accent-primary) 18%, transparent);
}

.git-scope-reset-btn:focus-visible,
.git-worktree-section-toggle:focus-visible,
.git-worker-filter-chip:focus-visible,
.git-worktree-file-chip:focus-visible,
.git-worktree-action-btn:focus-visible,
.git-branch-trigger:focus-visible,
.git-branch-popover-item:focus-visible,
.git-stash-subpill:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
}

.git-worktree-section {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 6px 8px;
  background: var(--surface-section);
  border: 1px solid var(--border-card);
  border-radius: 10px;
  flex-shrink: 0;
}

.git-worktree-section-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  min-width: 0;
}

.git-worktree-section-toggle {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  padding: 2px 4px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
}

.git-worktree-section-toggle:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.git-worktree-count-pill {
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  flex-shrink: 0;
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
}

.git-worker-filter-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 35%, transparent);
  border-radius: 999px;
  background: color-mix(in srgb, var(--accent-primary) 10%, transparent);
  color: var(--accent-primary);
  font-size: 10px;
  font-weight: 600;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
}

.git-filter-clear-text {
  text-decoration: underline;
  text-underline-offset: 2px;
}

.git-worktree-groups {
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 280px;
  overflow-y: auto;
  overscroll-behavior: contain;
}

.git-worktree-group {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 8px;
  border: 1px solid var(--border-card);
  border-radius: 8px;
  background: var(--surface-raised);
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}

.git-worktree-group.is-isolated {
  border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-card));
  background: color-mix(in srgb, var(--accent-primary) 4%, var(--surface-raised));
}

.git-worktree-group.is-active {
  border-color: color-mix(in srgb, var(--accent-primary) 55%, var(--border-card));
}

.git-worktree-group.is-focused {
  border-color: var(--accent-primary);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 24%, transparent);
}

.git-worktree-group-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  flex-wrap: wrap;
}

.git-worktree-group-identity {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
}

.git-worktree-worker-name {
  min-width: 0;
  font-size: 12px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-worktree-engine-badge {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 5px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 600;
  line-height: 1.4;
  white-space: nowrap;
  flex-shrink: 0;
}

.git-worktree-engine-badge[data-engine-kind='acp'] {
  border-color: color-mix(in srgb, var(--accent-primary) 32%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 10%, transparent);
  color: var(--accent-primary);
}

.git-worktree-engine-badge[data-engine-kind='cli'],
.git-worktree-engine-badge[data-engine-kind='tui'] {
  border-color: color-mix(in srgb, var(--accent-info, var(--accent-primary)) 32%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-info, var(--accent-primary)) 10%, transparent);
  color: var(--accent-info, var(--text-primary));
}

.git-worktree-branch-pill {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 6px;
  border-radius: 5px;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  color: var(--text-secondary);
  font-size: 10px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  max-width: 180px;
  flex-shrink: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-worktree-diff-counts {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  flex-shrink: 0;
}

.git-worktree-diff-counts .count-add {
  color: var(--accent-success);
}

.git-worktree-diff-counts .count-mod {
  color: var(--accent-warning);
}

.git-worktree-diff-counts .count-del {
  color: var(--accent-danger);
}

.git-worktree-task-title {
  margin: 0;
  font-size: 11px;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-worktree-lineage,
.git-shared-workers {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
  font-size: 11px;
  color: var(--text-muted);
}

.git-lineage-label {
  font-weight: 600;
}

.git-lineage-chain {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.git-lineage-node,
.git-shared-worker-chip {
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--surface-section);
  color: var(--text-secondary);
  white-space: nowrap;
}

.git-worktree-files {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.git-worktree-file-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  padding: 2px 6px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 11px;
  white-space: nowrap;
  cursor: pointer;
}

.git-worktree-file-chip:hover,
.git-worktree-file-chip.active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-section));
}

.git-file-status-dot {
  font-weight: 700;
  font-size: 10px;
}

.git-file-status-dot[data-kind='A'] { color: var(--accent-success); }
.git-file-status-dot[data-kind='M'] { color: var(--accent-warning); }
.git-file-status-dot[data-kind='D'] { color: var(--accent-danger); }

.git-file-chip-path {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 170px;
}

.git-file-chip-delta {
  font-size: 10px;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.git-worktree-clean-hint {
  margin: 0;
  font-size: 11px;
  color: var(--text-muted);
}

.git-worktree-group-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  justify-content: flex-end;
}

.git-worktree-action-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-height: 26px;
  padding: 0 8px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  white-space: nowrap;
  flex-shrink: 0;
  cursor: pointer;
}

.git-worktree-action-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.git-worktree-action-btn.is-danger {
  color: var(--accent-danger);
  border-color: color-mix(in srgb, var(--accent-danger) 35%, transparent);
}

.git-worktree-action-btn.is-danger:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent-danger) 10%, transparent);
}

.git-worktree-approval-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-warning) 35%, transparent);
  background: color-mix(in srgb, var(--accent-warning) 8%, var(--surface-raised));
  font-size: 11px;
  color: var(--text-primary);
  flex-wrap: wrap;
}

.git-worktree-approval-text {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-worktree-approval-status {
  font-size: 10px;
  color: var(--accent-warning);
  font-weight: 600;
}

.git-worktree-approval-actions {
  display: inline-flex;
  gap: 4px;
}

.git-worktree-diff-drawer {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 40%, var(--border-card));
  border-radius: 8px;
  background: var(--surface-raised);
}

.git-worktree-diff-drawer-head {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.git-worktree-diff-file-title {
  flex: 1;
  min-width: 0;
  font-size: 11px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-worktree-diff-close {
  width: 24px;
  height: 24px;
}

.git-worktree-diff-viewer-wrap {
  height: 220px;
  min-height: 160px;
  border-radius: 6px;
  overflow: hidden;
}

/* ---- JetBrains Header Popover & Confirmation Dialog ---- */
.git-header-left {
  position: relative;
}

.git-branch-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
}

.git-branch-trigger {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  margin-left: -6px;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 6px;
  cursor: pointer;
  color: inherit;
}

.git-branch-trigger:hover {
  background: var(--surface-hover);
  border-color: var(--border-subtle);
}

.git-branch-chevron {
  color: var(--text-muted);
}

.git-branch-popover {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  width: 260px;
  max-height: 300px;
  display: flex;
  flex-direction: column;
  background: var(--surface-raised, var(--surface-card));
  border: 1px solid var(--border-card);
  border-radius: 10px;
  box-shadow: var(--shadow-card);
  z-index: 40;
  overflow: hidden;
}

.git-branch-popover-search {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border-bottom: 1px solid var(--border-subtle);
  color: var(--text-muted);
}

.git-branch-popover-input {
  flex: 1;
  min-width: 0;
  background: transparent;
  border: 0;
  color: var(--text-primary);
  font-size: 12px;
  outline: none;
}

.git-branch-popover-body {
  overflow-y: auto;
  padding: 6px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.git-branch-popover-title {
  padding: 2px 6px;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  color: var(--text-muted);
}

.git-branch-popover-item {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.git-branch-popover-item:hover {
  background: var(--surface-hover);
}

.git-branch-popover-item.current {
  color: var(--accent-primary);
  font-weight: 600;
}

.git-header-sync-btns {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.git-header-sync-btn {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.git-confirm-dialog {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  border-radius: 10px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #f59e0b) 50%, var(--border-card));
  background: color-mix(in srgb, var(--accent-warning, #f59e0b) 10%, var(--surface-section));
  flex-shrink: 0;
}

.git-confirm-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.git-confirm-icon {
  color: var(--accent-warning, #f59e0b);
}

.git-confirm-detail {
  margin: 0;
  font-size: 11px;
  color: var(--text-secondary);
}

.git-confirm-paths {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.git-confirm-paths code {
  font-size: 10px;
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--surface-button);
}

.git-confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 6px;
}

.git-stash-tab-wrap {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.git-stash-subbar {
  display: inline-flex;
  align-self: flex-start;
  gap: 3px;
  padding: 3px;
  background: var(--surface-section);
  border: 1px solid var(--border-card);
  border-radius: 8px;
}

.git-stash-subpill {
  padding: 4px 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
}

.git-stash-subpill.active {
  background: var(--surface-raised);
  color: var(--accent-primary);
  box-shadow: var(--shadow-subtle);
}

/* ---- Animations ---- */
.spinning {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}
</style>
