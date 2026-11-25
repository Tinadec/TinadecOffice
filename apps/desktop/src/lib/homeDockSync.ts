import { ref } from 'vue'
import { useUie, type UieStore } from '@tinadec/ui'
import type { ToolCall } from '@/composables/useAgentActivity'
import { selectSubAgent } from '@/lib/conversation/selection'

export type HomeDockCardId = 'live-digest' | 'trajectory' | 'agent' | 'git' | 'orchestration' | 'approval' | 'workspace'
export type DockSyncReason = 'digest' | 'trajectory' | 'dispatch' | 'worktree' | 'approval' | 'worker' | 'code'
export type TrajectoryDockTab = 'path' | 'context' | 'eval' | 'code'
export type GitDockTab = 'changes' | 'history' | 'branches' | 'stash'
export type GitDockSubView = 'diff' | 'file_history' | 'blame'
export type ApprovalDockTab = 'pending' | 'rules' | 'history' | 'approvals'

export interface DockFocusTarget {
  card: HomeDockCardId
  reason?: DockSyncReason
  runId?: string | null
  taskId?: string | null
  taskKey?: string | null
  workerHandle?: string | null
  workerLabel?: string | null
  agentSlug?: string | null
  worktreePath?: string | null
  branch?: string | null
  filePath?: string | null
  gitTab?: GitDockTab | null
  gitSubView?: GitDockSubView | null
  approvalId?: string | null
  approvalTab?: ApprovalDockTab | null
  requestCleanupPath?: string | null
  trajectoryTab?: TrajectoryDockTab | null
  trajectoryTurnNo?: number | null
  trajectoryNodeId?: string | null
  trajectoryCategory?: string | null
  nonce: number
  requestedAt: number
}

export type DockFocusRequest = Omit<DockFocusTarget, 'nonce' | 'requestedAt'>

export interface InlineDockCardItem {
  id: string
  kind: 'dispatch' | 'worktree' | 'approval'
  card: HomeDockCardId
  title: string
  subtitle?: string | null
  badge?: string | null
  status?: string | null
  runId?: string | null
  taskId?: string | null
  taskKey?: string | null
  workerHandle?: string | null
  workerLabel?: string | null
  agentSlug?: string | null
  worktreePath?: string | null
  branch?: string | null
  filePath?: string | null
  approvalId?: string | null
  urgent?: boolean
}

const DISPATCH_TOOL_IDS = new Set([
  'task_dispatch',
  'subagent_dispatch',
  'dispatch_task',
  'agent_dispatch',
])

const WORKTREE_TOOL_IDS = new Set([
  'git_worktree_create',
  'git_worktree_remove',
  'git_checkout',
  'git_branch_create',
  'git_branch_rename',
])

const HIGHLIGHT_DURATION_MS = 2600

export const activeDockFocus = ref<DockFocusTarget | null>(null)
export const highlightedDockCard = ref<HomeDockCardId | null>(null)

let focusNonce = 0
let highlightTimer: ReturnType<typeof setTimeout> | null = null

function clearHighlightTimer(): void {
  if (highlightTimer !== null) {
    clearTimeout(highlightTimer)
    highlightTimer = null
  }
}

export function tryGetUieStore(): UieStore | null {
  try {
    return useUie()
  } catch {
    return null
  }
}

/**
 * Ensure the requested singleton card is expanded, opened, and focused in the Home right dock.
 * Returns true if the layout store accepted the command, false when no UIE host is mounted.
 */
export function ensureHomeDockCardOpen(
  card: HomeDockCardId,
  options?: { uie?: UieStore | null; title?: string },
): boolean {
  const uie = options?.uie ?? tryGetUieStore()
  if (!uie || !uie.bus || typeof uie.bus.dispatch !== 'function' || !uie.snapshot?.value || !uie.scope?.value) {
    return false
  }
  const rightColumn = uie.snapshot.value.columns?.right
  if (rightColumn?.collapsed) {
    uie.bus.dispatch({
      command: {
        type: 'collapseColumn',
        scope: uie.scope.value,
        slotId: 'right',
        collapsed: false,
      },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
  }
  return uie.bus.dispatch({
    command: {
      type: 'openCard',
      scope: uie.scope.value,
      descriptorId: card,
      slotId: 'right',
      ...(options?.title ? { title: options.title } : {}),
    },
    source: 'user',
    expectedRevision: uie.snapshot.value.revision,
  })
}

/**
 * Focus and highlight a right-side Dock card from an inline chat stream event or worker badge.
 * Also broadcasts sub-agent selection and the orchestration focus event for peer cards.
 */
export function focusDockCard(
  request: DockFocusRequest,
  options?: { uie?: UieStore | null; title?: string },
): DockFocusTarget {
  focusNonce += 1
  const target: DockFocusTarget = {
    ...request,
    nonce: focusNonce,
    requestedAt: Date.now(),
  }
  activeDockFocus.value = target
  highlightedDockCard.value = target.card
  clearHighlightTimer()
  highlightTimer = setTimeout(() => {
    highlightedDockCard.value = null
    highlightTimer = null
  }, HIGHLIGHT_DURATION_MS)

  if (target.runId && target.taskId) {
    selectSubAgent({ runId: target.runId, taskId: target.taskId })
  }

  if (typeof window !== 'undefined') {
    const targetName =
      target.taskId ??
      target.taskKey ??
      target.workerHandle ??
      target.agentSlug ??
      target.workerLabel ??
      null
    if (targetName) {
      window.dispatchEvent(new CustomEvent('tinadec:focus-orchestration-node', {
        detail: {
          target: targetName,
          taskId: target.taskId ?? null,
          taskKey: target.taskKey ?? null,
          handle: target.workerHandle ?? null,
          agentSlug: target.agentSlug ?? null,
        },
      }))
    }
    if (
      target.card === 'trajectory' ||
      target.trajectoryTab ||
      target.trajectoryTurnNo != null ||
      target.trajectoryNodeId ||
      target.trajectoryCategory
    ) {
      window.dispatchEvent(new CustomEvent('tinadec:focus-trajectory', {
        detail: {
          runId: target.runId ?? null,
          trajectoryTab: target.trajectoryTab ?? null,
          trajectoryTurnNo: target.trajectoryTurnNo ?? null,
          trajectoryNodeId: target.trajectoryNodeId ?? null,
          trajectoryCategory: target.trajectoryCategory ?? null,
          nonce: target.nonce,
        },
      }))
    }
  }

  ensureHomeDockCardOpen(target.card, options)
  return target
}

/** Clear the active worker/worktree filter while leaving the right dock untouched. */
export function clearDockWorkerFilter(): void {
  if (!activeDockFocus.value) return
  activeDockFocus.value = {
    ...activeDockFocus.value,
    taskId: null,
    taskKey: null,
    workerHandle: null,
    workerLabel: null,
    agentSlug: null,
    worktreePath: null,
    branch: null,
    filePath: null,
    approvalId: null,
    requestCleanupPath: null,
    trajectoryNodeId: null,
    nonce: ++focusNonce,
  }
}

export function resetDockSyncForTests(): void {
  clearHighlightTimer()
  activeDockFocus.value = null
  highlightedDockCard.value = null
  focusNonce = 0
}

export interface ParsedToolDockInfo {
  kind: 'dispatch' | 'worktree' | 'approval'
  workerHandle: string | null
  taskKey: string | null
  branch: string | null
  worktreePath: string | null
}

/** Classify whether a tool call represents an inline dispatch, worktree switch, or approval card. */
export function classifyToolDockKind(
  toolId: string,
  status?: string | null,
  argsSummary?: string | null,
): 'dispatch' | 'worktree' | 'approval' | null {
  if (status === 'waiting_approval') return 'approval'
  const normalized = toolId.trim().toLowerCase()
  if (DISPATCH_TOOL_IDS.has(normalized)) return 'dispatch'
  if (WORKTREE_TOOL_IDS.has(normalized)) return 'worktree'
  if (normalized === 'git_worktree_manager') {
    const summary = (argsSummary ?? '').toLowerCase()
    if (/\b(worktree|checkout|branch_create|branch_rename|switch)\b/.test(summary)) {
      return 'worktree'
    }
  }
  return null
}

function matchFirst(text: string, patterns: RegExp[]): string | null {
  for (const pattern of patterns) {
    const match = text.match(pattern)
    const captured = match?.[1]?.trim()
    if (captured) return captured
  }
  return null
}

/** Extract worker handle, task key, branch, or worktree path hints from tool summaries. */
export function parseToolDockInfo(
  toolCall: Pick<ToolCall, 'toolId' | 'status' | 'argsSummary' | 'resultSummary'>,
): ParsedToolDockInfo | null {
  const kind = classifyToolDockKind(toolCall.toolId, toolCall.status, toolCall.argsSummary)
  if (!kind) return null
  const combined = [toolCall.argsSummary, toolCall.resultSummary].filter(Boolean).join(' ')
  const workerHandle = matchFirst(combined, [
    /\bhandle[:=\s]+['"`]?([A-Za-z0-9._#-]+)/i,
    /\bagent[:=\s]+['"`]?([A-Za-z0-9._#-]+)/i,
    /\bworker[:=\s]+['"`]?([A-Za-z0-9._#-]+)/i,
  ])
  const taskKey = matchFirst(combined, [
    /\btask_key[:=\s]+['"`]?([A-Za-z0-9._-]+)/i,
  ])
  const branch = matchFirst(combined, [
    /\bbranch[:=\s]+['"`]?([A-Za-z0-9._/-]+)/i,
    /\b(tinadec\/(?:task|run)-[A-Za-z0-9._/-]+)/i,
    /\bon\s+branch\s+['"`]?([A-Za-z0-9._/-]+)/i,
    /\bcheckout\s+['"`]?([A-Za-z0-9._/-]+)/i,
  ])
  const worktreePath = matchFirst(combined, [
    /\bpath[:=\s]+['"`]?([^\s,'"`]+)/i,
    /([A-Za-z]:\\[^\s'"`]+\.tinadec\\worktrees\\[^\s'"`]+|[^\s'"`]*\/\.tinadec\/worktrees\/[^\s'"`]+)/i,
  ])
  return { kind, workerHandle, taskKey, branch, worktreePath }
}

/** Normalize worker target matching across taskId, handle, taskKey, or agentSlug. */
export function matchesWorkerTarget(
  target: Pick<DockFocusTarget, 'taskId' | 'taskKey' | 'workerHandle' | 'agentSlug' | 'workerLabel'> | null | undefined,
  candidate: {
    taskId?: string | null
    taskKey?: string | null
    handle?: string | null
    agentSlug?: string | null
    label?: string | null
  },
): boolean {
  if (!target) return false
  if (target.taskId && candidate.taskId && target.taskId === candidate.taskId) return true
  if (target.taskKey && candidate.taskKey && target.taskKey === candidate.taskKey) return true
  if (target.workerHandle && candidate.handle && candidate.handle.toLowerCase() === target.workerHandle.toLowerCase()) return true
  if (target.agentSlug && candidate.agentSlug && candidate.agentSlug.toLowerCase() === target.agentSlug.toLowerCase()) return true
  if (target.workerLabel && candidate.label && candidate.label.toLowerCase() === target.workerLabel.toLowerCase()) return true
  return false
}
