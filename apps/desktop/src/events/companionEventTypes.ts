/**
 * The companion feed's event names, in one place.
 *
 * Core writes NAMED SSE frames on `GET /api/v1/companion-events`:
 * - durable journal rows: `companion.activity`, `companion.run`, `companion.settings`
 * - real-time stream frames (`ICompanionStreamBroker`): `companion.delta`,
 *   `companion.tool_call`, `companion.proposal`, `companion.stage_cue`, `companion.work_session`
 */

/** One durable journal row of the companion domain: messages, group membership, autonomy. */
export const COMPANION_ACTIVITY_EVENT = 'companion.activity'
/** A run transition (`run_started` / `run_completed` / `run_failed`), projected from the same rows. */
export const COMPANION_RUN_EVENT = 'companion.run'
/** A user-wide settings change (main companion, max_bot_hop_depth, global pause). */
export const COMPANION_SETTINGS_EVENT = 'companion.settings'
/** Incremental reply token chunk for a running companion turn. */
export const COMPANION_DELTA_EVENT = 'companion.delta'
/** Live tool invocation status inside an active companion turn. */
export const COMPANION_TOOL_CALL_EVENT = 'companion.tool_call'
/** Live autonomy mandate / recruitment proposal waiting on user confirmation. */
export const COMPANION_PROPOSAL_EVENT = 'companion.proposal'
/** Expressive stage cue (emotion / speaking / intensity / gesture) for avatar & voice sync. */
export const COMPANION_STAGE_CUE_EVENT = 'companion.stage_cue'
/** Live work-session milestone & pending-approval backflow pushed from Core's run engine. */
export const COMPANION_WORK_SESSION_EVENT = 'companion.work_session'

export const COMPANION_EVENT_TYPES = [
  COMPANION_ACTIVITY_EVENT,
  COMPANION_RUN_EVENT,
  COMPANION_SETTINGS_EVENT,
  COMPANION_DELTA_EVENT,
  COMPANION_TOOL_CALL_EVENT,
  COMPANION_PROPOSAL_EVENT,
  COMPANION_STAGE_CUE_EVENT,
  COMPANION_WORK_SESSION_EVENT,
] as const

export type CompanionEventType = (typeof COMPANION_EVENT_TYPES)[number]

export interface CompanionWorkSessionApprovalNotice {
  approval_id: string
  tool_id: string
  summary: string | null
  risk_level: string | null
  created_at: string
}

export interface CompanionWorkSessionNotice {
  sessionId: string
  sessionTitle: string
  workspaceId: string | null
  projectId: string | null
  projectName: string | null
  runId: string | null
  status: string
  modeName: string | null
  viewMode: 'flat' | 'space' | null
  summary: string | null
  planSummary: string | null
  pendingApprovals: CompanionWorkSessionApprovalNotice[]
  browserSpaceId: string | null
  browserUrl: string | null
  browserDriver: string | null
  browserHandoffReason: string | null
  updatedAt: string | null
}

/**
 * The `data:` body of one SSE frame. Field names match Core's snake_case wire shape across both
 * durable journal rows and ephemeral `ICompanionStreamBroker` frames.
 */
export interface CompanionEventPayload {
  seq?: number
  id?: string
  kind?: string
  companion_id?: string | null
  conversation_id?: string | null
  run_id?: string | null
  summary?: string | null
  detail?: string | null
  payload?: Record<string, unknown> | null
  created_at?: string
  delta?: string | null
  call_id?: string | null
  tool_name?: string | null
  status?: string | null
  arguments_preview?: string | null
  result_preview?: string | null
  proposal_id?: string | null
  action?: string | null
  emotion?: string | null
  speaking?: boolean | null
  intensity?: number | null
  gesture?: string | null
  workspace_id?: string | null
  project_id?: string | null
  project_name?: string | null
  session_id?: string | null
  session_title?: string | null
  title?: string | null
  mode_name?: string | null
  view_mode?: 'flat' | 'space' | string | null
  plan_summary?: string | null
  pending_approvals?: Array<Record<string, unknown>> | null
  browser_space_id?: string | null
  space_id?: string | null
  browser_url?: string | null
  url?: string | null
  browser_driver?: string | null
  driver?: string | null
  handoff_reason?: string | null
  updated_at?: string | null
}

/** `event: heartbeat` carries the cursor the subscriber stands at, nothing else. */
export interface CompanionHeartbeatPayload {
  after_seq?: number
}

/**
 * Projected frame dispatched across companion UI surfaces and relayed to the satellite window.
 */
export interface CompanionEventNotice {
  type: CompanionEventType
  seq: number
  kind: string | null
  companionId: string | null
  conversationId: string | null
  /** The run named by `companion.run` or a live stream frame, when present. */
  runId: string | null
  delta?: string
  callId?: string | null
  toolName?: string | null
  toolStatus?: 'started' | 'completed' | 'failed'
  argumentsPreview?: string | null
  resultPreview?: string | null
  proposalKind?: 'mandate' | 'recruitment'
  proposalId?: string | null
  proposalAction?: string | null
  proposalStatus?: string | null
  proposalSummary?: string | null
  proposalPayload?: Record<string, unknown> | null
  emotion?: string | null
  speaking?: boolean
  intensity?: number | null
  gesture?: string | null
  workSession?: CompanionWorkSessionNotice | null
}

export const COMPANION_EVENT_CHANNEL = 'tinadec.companion-events'

export interface CompanionEventBroadcast {
  type: 'notice'
  notice: CompanionEventNotice
}

/** A relayed message is untrusted input like any other: check the shape before acting on it. */
export function isCompanionEventBroadcast(value: unknown): value is CompanionEventBroadcast {
  if (!value || typeof value !== 'object') return false
  const message = value as { type?: unknown; notice?: unknown }
  if (message.type !== 'notice' || !message.notice || typeof message.notice !== 'object') return false
  const notice = message.notice as Partial<CompanionEventNotice>
  return typeof notice.type === 'string'
    && (COMPANION_EVENT_TYPES as readonly string[]).includes(notice.type)
    && typeof notice.seq === 'number'
    && (notice.kind === null || typeof notice.kind === 'string')
    && (notice.companionId === null || typeof notice.companionId === 'string')
    && (notice.conversationId === null || typeof notice.conversationId === 'string')
    && (notice.runId === null || typeof notice.runId === 'string')
}
