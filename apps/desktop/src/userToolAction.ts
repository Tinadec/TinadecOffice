import type { ApprovalDto, UserToolActionDto } from './api'
import { errorCategoryMessage, errorCodeMessage, type ErrorWords } from './lib/apiError'

const TERMINAL_STATUSES = new Set(['completed', 'blocked', 'failed', 'outcome_unknown'])
const DECISION_STATUSES = new Set(['awaiting_delegate', 'awaiting_user', 'awaiting_approval'])

const GIT_CONFIRMATION_FIELDS: Readonly<Record<string, string>> = Object.freeze({
  git_commit: 'confirm_commit',
  git_fetch: 'confirm_fetch',
  git_push: 'confirm_push',
  git_pull: 'confirm_pull',
  git_checkout: 'confirm_checkout',
  git_branch_create: 'confirm_branch_create',
  git_branch_delete: 'confirm_branch_delete',
  git_branch_rename: 'confirm_branch_rename',
  git_merge: 'confirm_merge',
  git_rebase: 'confirm_rebase',
  git_conflict_resolve: 'confirm_resolve',
  git_discard: 'confirm_discard',
  git_worktree_create: 'confirm_worktree_create',
  git_worktree_remove: 'confirm_worktree_remove',
})

/**
 * Return the public approval identity for a Core user action.
 *
 * The action id is only a fallback for actions that are still waiting on a
 * permission decision. Internal action-approval nonce material never crosses
 * this boundary.
 */
export function userToolApprovalId(action: UserToolActionDto): string {
  return action.action_approval_id ?? action.permission_request_id ?? action.id
}

export function isUserToolActionTerminal(status: string): boolean {
  return TERMINAL_STATUSES.has(status)
}

export function userToolActionNeedsDecision(status: string): boolean {
  return DECISION_STATUSES.has(status)
}

/** One line on where `operation` stands, in the caller's language. */
export function userToolActionStatusMessage(
  action: UserToolActionDto,
  operation: string,
  words: ErrorWords,
): string {
  const { t } = words
  const detail = userToolActionReason(action, words)
  switch (action.status) {
    case 'snapshot_required':
      return t('userAction.status.snapshotRequired', { operation })
    case 'awaiting_delegate':
      return t('userAction.status.awaitingDelegate', { operation })
    case 'awaiting_user':
      return t('userAction.status.awaitingUser', { operation })
    case 'awaiting_approval':
      return t('userAction.status.awaitingApproval', { operation })
    case 'running':
      return t('userAction.status.running', { operation })
    case 'completed':
      return t('userAction.status.completed', { operation })
    case 'outcome_unknown':
      return t('userAction.status.outcomeUnknown', { operation })
    case 'blocked':
      return detail ? t('userAction.status.blockedDetail', { operation, detail }) : t('userAction.status.blocked', { operation })
    case 'failed':
      return detail ? t('userAction.status.failedDetail', { operation, detail }) : t('userAction.status.failed', { operation })
    default:
      return detail ? t('userAction.status.otherDetail', { operation, detail }) : t('userAction.status.other', { operation, status: action.status })
  }
}

/**
 * Why an action stopped, in the interface language when this build has words for its
 * `error_category`; Core's message (diagnostic text, such as a tool's own output) otherwise.
 */
export function userToolActionReason(action: Pick<UserToolActionDto, 'error_category' | 'message'>, words: ErrorWords): string | undefined {
  return errorCodeMessage(action.error_category, undefined, words)
    ?? errorCategoryMessage(action.error_category, words)
    ?? (action.message?.trim() || undefined)
}

/** An approval's summary in the interface language when it is one of Core's fixed sentences; the projected fact otherwise. */
export function approvalSummary(approval: Pick<ApprovalDto, 'summary' | 'summary_code' | 'tool_id'>, words: ErrorWords): string {
  const key = `approval.summaryCode.${approval.summary_code}`
  return approval.summary_code && words.te(key) ? words.t(key, { tool: approval.tool_id ?? '' }) : approval.summary
}

/** Core's own message, for a details line or tooltip, when the reason shown is a translation of it. */
export function userToolActionDetail(action: Pick<UserToolActionDto, 'error_category' | 'message'>, words: ErrorWords): string | undefined {
  const message = action.message?.trim() || undefined
  return message && message !== userToolActionReason(action, words) ? message : undefined
}

/**
 * Create a deterministic, bounded idempotency key for a user action.
 *
 * The key is a client retry identity, not an authorization secret. Payload
 * fields are canonicalized before hashing so object insertion order cannot
 * create duplicate actions. A small deterministic fallback keeps the UI
 * usable in embedded webviews that do not expose SubtleCrypto.
 */
export async function userToolActionIdempotencyKey(scope: string, payload: unknown): Promise<string> {
  const canonical = canonicalJson(payload)
  const bytes = new TextEncoder().encode(canonical)
  let digest: string | null = null
  try {
    const subtle = globalThis.crypto?.subtle
    if (subtle) {
      const hash = await subtle.digest('SHA-256', bytes)
      digest = Array.from(new Uint8Array(hash), (value) => value.toString(16).padStart(2, '0')).join('')
    }
  } catch {
    // Fall through to the deterministic non-cryptographic fallback.
  }
  return `${scope}:${digest ?? fallbackHash(canonical)}`.slice(0, 256)
}

/**
 * Add the explicit user-intent field published by the TinadecTools Git
 * manifest. The value is not an authorization fact: Core still owns the
 * PermissionRequest, CapabilityLease, and ActionApproval state machines.
 */
export function withGitToolConfirmation(
  toolId: string,
  parameters: Record<string, unknown>,
): Record<string, unknown> {
  const field = GIT_CONFIRMATION_FIELDS[toolId]
  return field
    ? { ...parameters, [field]: `desktop:${toolId}` }
    : { ...parameters }
}

export function userToolActionApprovalStatus(action: UserToolActionDto): ApprovalDto['status'] {
  if (action.status === 'completed') return 'approved'
  if (isUserToolActionTerminal(action.status)) return 'rejected'
  if (!userToolActionNeedsDecision(action.status)) return action.status
  return 'pending'
}

/**
 * Keep existing approval-panel consumers working while Core owns the real
 * action and governance records. This is a display/event projection only.
 */
export function userToolActionToApproval(
  action: UserToolActionDto,
  summary: string,
  context: { sessionId?: string | null; cwd?: string | null } = {},
): ApprovalDto {
  return {
    id: userToolApprovalId(action),
    session_id: context.sessionId ?? null,
    kind: action.action_approval_id ? 'user_tool' : 'permission',
    summary,
    command: action.tool_id,
    cwd: context.cwd ?? null,
    status: userToolActionApprovalStatus(action),
    governance_status: action.status,
    created_at: action.created_at,
    decided_at: action.completed_at ?? null,
  }
}

function canonicalJson(value: unknown): string {
  if (value === null || value === undefined) return 'null'
  if (typeof value === 'number' || typeof value === 'boolean' || typeof value === 'string') return JSON.stringify(value)
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`
  if (typeof value === 'object') {
    const record = value as Record<string, unknown>
    return `{${Object.keys(record).sort().map((key) => `${JSON.stringify(key)}:${canonicalJson(record[key])}`).join(',')}}`
  }
  return JSON.stringify(String(value))
}

function fallbackHash(value: string): string {
  let first = 0x811c9dc5
  let second = 0x9e3779b9
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index)
    first ^= code
    first = Math.imul(first, 0x01000193)
    second ^= code + index
    second = Math.imul(second, 0x85ebca6b)
  }
  return `${(first >>> 0).toString(16).padStart(8, '0')}${(second >>> 0).toString(16).padStart(8, '0')}`
}
