import i18n from '@/i18n'
import type { components } from '@/generated/schema'
import {
  COMPANION_ACTIVITY_EVENT,
  COMPANION_DELTA_EVENT,
  COMPANION_EVENT_CHANNEL,
  COMPANION_EVENT_TYPES,
  COMPANION_PROPOSAL_EVENT,
  COMPANION_RUN_EVENT,
  COMPANION_SETTINGS_EVENT,
  COMPANION_STAGE_CUE_EVENT,
  COMPANION_TOOL_CALL_EVENT,
  COMPANION_WORK_SESSION_EVENT,
  isCompanionEventBroadcast,
  type CompanionEventBroadcast,
  type CompanionEventNotice,
  type CompanionEventPayload,
  type CompanionEventType,
  type CompanionHeartbeatPayload,
  type CompanionWorkSessionApprovalNotice,
  type CompanionWorkSessionNotice,
} from '@/events/companionEventTypes'
import { ApiError, apiErrorMessage } from './apiError'
import { assertHostAccess } from './hostAccess'
import { ScopedEventSource } from './scopedEventSource'
import { captureStorageId } from './storageScope'
import {
  asNonEmptyString,
  asRecord,
  asViewMode,
  encodeBrowserConnectionEndpoint,
  normalizeBrowserProfile,
  normalizeConnectedWorkSession,
  normalizeWorkSessionApproval,
  parseBrowserConnectionEndpoint,
  parseWorkSessionPlanProgress,
  type CompanionBrowserDriver,
  type CompanionBrowserProfile,
  type CompanionConnectedWorkSession,
  type CompanionConnectionAuthorization,
  type CompanionConnectionKind,
  type CompanionConnectionRuntimeStatus,
  type CompanionWorkSessionPendingApproval,
  type CompanionWorkSessionPlanProgress,
  type CompanionWorkSessionRunSummary,
} from './companionWorkSession'

export {
  encodeBrowserConnectionEndpoint,
  normalizeBrowserProfile,
  normalizeConnectedWorkSession,
  normalizeWorkSessionApproval,
  parseBrowserConnectionEndpoint,
  parseWorkSessionPlanProgress,
  type CompanionBrowserDriver,
  type CompanionBrowserProfile,
  type CompanionConnectedWorkSession,
  type CompanionConnectionAuthorization,
  type CompanionConnectionKind,
  type CompanionConnectionRuntimeStatus,
  type CompanionWorkSessionPendingApproval,
  type CompanionWorkSessionPlanProgress,
  type CompanionWorkSessionRunSummary,
}

/**
 * Companion domain client:
 * - `companionApi` runs in the trusted main window and talks directly to the Gateway.
 * - `companionBridge` runs in the satellite companion window over the main-process proxy.
 */

export type CompanionRole = 'autonomous' | 'session_partner'
export type CompanionChannelKind = 'desktop_chat' | 'session_partner' | 'schedule_tick' | 'external_im'

type SchemaCompanionSummary = components['schemas']['CompanionSummary']
type SchemaCompanionDetail = components['schemas']['CompanionDetail']
type SchemaCompanionRun = components['schemas']['CompanionRun']
type SchemaCompanionSettings = components['schemas']['CompanionSettings']
type SchemaCompanionSendMessageResult = components['schemas']['CompanionSendMessageResult']
type SchemaCompanionConnection = components['schemas']['CompanionConnection']
type SchemaCompanionConnectionOverview = components['schemas']['CompanionConnectionOverview']

type StripIndex<T> = { [K in keyof T as string extends K ? never : number extends K ? never : K]: T[K] }

export interface CompanionSummary extends Omit<StripIndex<SchemaCompanionSummary>, 'role'> {
  role: CompanionRole | string
}
export interface CompanionDetail extends Omit<StripIndex<SchemaCompanionDetail>, 'role'> {
  role: CompanionRole | string
}
export type CompanionAutonomy = components['schemas']['CompanionAutonomy']
export type CompanionMemory = components['schemas']['CompanionMemory']
export type CompanionSkill = components['schemas']['CompanionSkill']
export interface CompanionConnection extends Omit<StripIndex<SchemaCompanionConnection>, 'kind' | 'runtime_status'> {
  kind: CompanionConnectionKind | string
  runtime_status: CompanionConnectionRuntimeStatus
}
export interface CompanionConnectionOverview extends Omit<StripIndex<SchemaCompanionConnectionOverview>, 'kind' | 'runtime_status'> {
  kind: CompanionConnectionKind | string
  runtime_status: CompanionConnectionRuntimeStatus
}
export type CompanionMember = components['schemas']['CompanionMember']
export type CompanionConversation = components['schemas']['CompanionConversation']
export type CompanionMessage = components['schemas']['CompanionMessage']
export interface CompanionRun extends StripIndex<SchemaCompanionRun> {
  hop_depth?: number
  channel_kind?: string
  dispatched_session_id?: string | null
}
export type CompanionActivity = components['schemas']['CompanionActivity']
export interface CompanionSettings extends Omit<StripIndex<SchemaCompanionSettings>, 'buddy_companion_id' | 'max_bot_hop_depth'> {
  max_bot_hop_depth?: number
}
export interface CompanionSendMessageResult extends Omit<StripIndex<SchemaCompanionSendMessageResult>, 'skipped_runs'> {
  skipped_runs?: Array<{ companion_id: string; reason: string }>
}
export type CompanionAutonomyTickResult = components['schemas']['CompanionAutonomyTickResult']

export interface CompanionAutonomyInput {
  enabled?: boolean
  paused?: boolean
  interval_minutes?: number
  daily_action_budget?: number
}

export interface CompanionRecruitment {
  id: string
  proposer_id: string
  name: string
  persona_document: string | null
  rationale: string | null
  status: 'proposed' | 'approved' | 'denied'
  created_companion_id: string | null
  revision: number
  created_at: string
  updated_at: string
  decided_at: string | null
}

export interface CompanionMandate {
  id: string
  companion_id: string
  action: string
  status: 'proposed' | 'approved' | 'denied'
  summary: string | null
  proposal: Record<string, unknown> | null
  revision: number
  created_at: string
  updated_at: string
  decided_at: string | null
}

export interface CompanionCreateInput {
  name?: string
  tagline?: string | null
  avatar?: string | null
  persona_document?: string | null
  model_purpose?: string
  /** "<providerInstanceId>|<model>"; empty clears the pin back to the route purpose. */
  model?: string | null
  role?: CompanionRole
  autonomy?: CompanionAutonomyInput | null
}

export type CompanionPatchInput = CompanionCreateInput

export interface CompanionMemoryCreateInput {
  content?: string
  source_kind?: 'user' | 'companion_run' | 'group_message'
  source_conversation_id?: string | null
  source_message_id?: string | null
}

export interface CompanionMemoryPatchInput {
  content?: string
}

export interface CompanionSkillCreateInput {
  name?: string
  source_kind?: 'manual' | 'pack'
  pack_id?: string | null
  pack_version?: string | null
  instructions?: string | null
  enabled?: boolean
}

export interface CompanionSkillPatchInput {
  enabled?: boolean
  instructions?: string | null
}

export interface CompanionConnectionCreateInput {
  kind?: CompanionConnectionKind
  name?: string
  endpoint?: string | null
  credential_ref?: string | null
  authorization?: CompanionConnectionAuthorization
  /** Optional browser connector driver (`builtin_electron` | `ego_lite`) when `kind === 'browser'`. */
  driver?: CompanionBrowserDriver | string | null
  /** Optional browser profile id when `kind === 'browser'`. */
  profile_id?: string | null
}

export interface CompanionConnectionPatchInput {
  authorization?: CompanionConnectionAuthorization
  enabled?: boolean
}

export interface CompanionApprovalDecisionResult {
  id: string
  session_id?: string | null
  run_id?: string | null
  tool_id?: string | null
  status?: string | null
  decision?: string | null
  decision_reason?: string | null
}

export interface CompanionRunCancelResult {
  run_id: string
  status: string
  action: string
  accepted: boolean
  killed_terminal_sessions?: number
}

export interface CompanionProjectSummary {
  id: string
  name: string
  path?: string | null
}

export interface CompanionGroupConversationInput {
  title?: string
  companion_ids?: string[]
}

export interface CompanionInviteInput {
  companion_id: string
  share_history?: boolean
}

export interface CompanionMessagePostInput {
  body?: string
  addressed_to?: string[]
  idempotency_key?: string
  channel_kind?: CompanionChannelKind
  thread_key?: string | null
}

export interface CompanionSettingsPatchInput {
  main_companion_id?: string | null
  /** How many companions other companions may recruit; 0 turns recruiting off. */
  recruit_limit?: number
  /** Maximum consecutive bot-to-bot auto-relay replies in group/IM threads (0..10, default 3). */
  max_bot_hop_depth?: number
  autonomy_paused?: boolean
}

export interface CompanionSchedule {
  id: string
  companion_id: string
  title: string
  prompt: string
  cron_expression: string | null
  run_at: string | null
  next_run_at: string | null
  last_run_at: string | null
  target_conversation_id: string | null
  one_shot: boolean
  enabled: boolean
  status: string
  revision: number
  created_at: string
  updated_at: string
}

export interface CompanionScheduleInput {
  title: string
  prompt: string
  cron_expression?: string | null
  run_at?: string | null
  target_conversation_id?: string | null
  one_shot?: boolean
  enabled?: boolean
}

export interface CompanionSchedulePatchInput {
  title?: string
  prompt?: string
  cron_expression?: string | null
  run_at?: string | null
  target_conversation_id?: string | null
  one_shot?: boolean
  enabled?: boolean
}

export interface CompanionScheduleTriggerResult {
  schedule: CompanionSchedule
  run_ids: string[]
  conversation_id: string
}

export interface CompanionPlanStep {
  id: string
  title: string
  status: 'pending' | 'in_progress' | 'completed' | 'blocked'
  note?: string | null
}

export interface CompanionPlan {
  id: string
  companion_id: string
  title: string
  summary: string | null
  status: 'active' | 'completed' | 'paused' | 'archived'
  steps: CompanionPlanStep[]
  revision: number
  created_at: string
  updated_at: string
}

export interface CompanionPlanInput {
  title: string
  summary?: string | null
  status?: 'active' | 'completed' | 'paused' | 'archived'
  steps?: CompanionPlanStep[]
}

export type CompanionPlanPatchInput = Partial<CompanionPlanInput>

export interface CompanionTodo {
  id: string
  companion_id: string
  title: string
  note: string | null
  priority: 'low' | 'medium' | 'high'
  status: 'pending' | 'in_progress' | 'done' | 'cancelled'
  due_at: string | null
  plan_id: string | null
  revision: number
  created_at: string
  updated_at: string
}

export interface CompanionTodoInput {
  title: string
  note?: string | null
  priority?: 'low' | 'medium' | 'high'
  status?: 'pending' | 'in_progress' | 'done' | 'cancelled'
  due_at?: string | null
  plan_id?: string | null
}

export type CompanionTodoPatchInput = Partial<CompanionTodoInput>

/**
 * Capabilities parsed from a Live2D `model3.json`. Sections the file does not declare stay empty;
 * nothing here is inferred or synthesized.
 */
export interface CompanionAvatarCapabilities {
  motions: Record<string, string[]>
  expressions: string[]
  parameters: Record<string, { min: number; max: number; default: number }>
  hit_areas: string[]
  groups: Record<string, string[]>
}

/** One imported Live2D model, owned by Core's storage after import. */
export interface CompanionAvatar {
  id: string
  name: string
  kind: 'live2d'
  source_path: string
  entry_model_json?: string | null
  capabilities: CompanionAvatarCapabilities
  created_at: string
}

export interface CompanionLive2dSdkStatus {
  ready: boolean
  installed?: boolean
  file_name?: string | null
  sha256?: string | null
  size_bytes?: number
  has_wasm?: boolean
  script_url?: string | null
  wasm_url?: string | null
  updated_at?: string | null
}

const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'

function query(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value))
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}

/** Companion writes are conditional on the revision Core returned last; no storage id is sent. */
function ifMatch(revision: number): Record<string, string> {
  return { 'If-Match': String(revision) }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers)
  headers.set('accept', 'application/json')
  if (init?.body && !headers.has('content-type')) headers.set('content-type', 'application/json')
  await assertHostAccess(path, init?.signal ?? undefined)
  let response: Response
  try {
    response = await fetch(`${gatewayUrl}${path}`, { ...init, headers })
  } catch (error) {
    init?.signal?.throwIfAborted()
    if (error instanceof TypeError) {
      throw new ApiError(
        i18n.global.t('common.backendUnreachable', { origin: new URL(gatewayUrl).origin, reason: error.message }),
        0,
        { code: 'backend_network_unavailable', category: 'retryable', retryable: true, actions: ['retry'] },
      )
    }
    throw error
  }
  const text = await response.text()
  init?.signal?.throwIfAborted()
  let data: unknown = null
  if (text.length > 0) {
    try {
      data = JSON.parse(text)
    } catch {
      throw new Error(i18n.global.t('errors.invalidServerResponse', { text: text.substring(0, 200) }))
    }
  }
  if (!response.ok) throw new ApiError(apiErrorMessage(data, response.statusText), response.status, data)
  return data as T
}

export const companionApi = {
  gatewayUrl,
  listCompanionAvatars: () => request<CompanionAvatar[]>('/api/v1/companion-avatars'),
  importCompanionAvatar: (input: { path: string }) =>
    request<CompanionAvatar>('/api/v1/companion-avatars/import', {
      method: 'POST', body: JSON.stringify(input),
    }),
  deleteCompanionAvatar: (id: string) =>
    request<void>(`/api/v1/companion-avatars/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  getLive2dSdkStatus: () => request<CompanionLive2dSdkStatus>('/api/v1/companion-avatars/sdk'),
  importLive2dSdk: (input: { path?: string; file_name?: string; content_base64?: string; wasm_base64?: string | null }) =>
    request<CompanionLive2dSdkStatus>('/api/v1/companion-avatars/sdk', {
      method: 'POST', body: JSON.stringify(input),
    }),
  deleteLive2dSdk: () => request<void>('/api/v1/companion-avatars/sdk', { method: 'DELETE' }),
  live2dSdkScriptUrl: () => `${gatewayUrl}/api/v1/companion-avatars/sdk/live2dcubismcore.min.js`,
  companionAvatarAssetUrl: (avatarId: string, relativePath: string) =>
    `${gatewayUrl}/api/v1/companion-avatars/${encodeURIComponent(avatarId)}/assets/${relativePath.split('/').filter(Boolean).map(encodeURIComponent).join('/')}`,

  listCompanions: (includeArchived?: boolean) =>
    request<CompanionSummary[]>(`/api/v1/companions${query({ include_archived: includeArchived })}`),
  getCompanion: (id: string) => request<CompanionDetail>(`/api/v1/companions/${encodeURIComponent(id)}`),
  createCompanion: (input: CompanionCreateInput) =>
    request<CompanionDetail>('/api/v1/companions', { method: 'POST', body: JSON.stringify(input) }),
  patchCompanion: (id: string, revision: number, patch: CompanionPatchInput) =>
    request<CompanionDetail>(`/api/v1/companions/${encodeURIComponent(id)}`, {
      method: 'PATCH', headers: ifMatch(revision), body: JSON.stringify(patch),
    }),
  archiveCompanion: (id: string, revision: number) =>
    request<CompanionDetail>(`/api/v1/companions/${encodeURIComponent(id)}/archive`, {
      method: 'POST', headers: ifMatch(revision),
    }),

  listMemories: (id: string, q?: string, limit?: number) =>
    request<CompanionMemory[]>(`/api/v1/companions/${encodeURIComponent(id)}/memories${query({ q, limit })}`),
  addMemory: (id: string, input: CompanionMemoryCreateInput) =>
    request<CompanionMemory>(`/api/v1/companions/${encodeURIComponent(id)}/memories`, {
      method: 'POST', body: JSON.stringify(input),
    }),
  patchMemory: (id: string, memoryId: string, revision: number, patch: CompanionMemoryPatchInput) =>
    request<CompanionMemory>(`/api/v1/companions/${encodeURIComponent(id)}/memories/${encodeURIComponent(memoryId)}`, {
      method: 'PATCH', headers: ifMatch(revision), body: JSON.stringify(patch),
    }),
  deleteMemory: (id: string, memoryId: string, revision: number) =>
    request<void>(`/api/v1/companions/${encodeURIComponent(id)}/memories/${encodeURIComponent(memoryId)}`, {
      method: 'DELETE', headers: ifMatch(revision),
    }),

  listSkills: (id: string) => request<CompanionSkill[]>(`/api/v1/companions/${encodeURIComponent(id)}/skills`),
  addSkill: (id: string, input: CompanionSkillCreateInput) =>
    request<CompanionSkill>(`/api/v1/companions/${encodeURIComponent(id)}/skills`, {
      method: 'POST', body: JSON.stringify(input),
    }),
  patchSkill: (id: string, bindingId: string, revision: number, patch: CompanionSkillPatchInput) =>
    request<CompanionSkill>(`/api/v1/companions/${encodeURIComponent(id)}/skills/${encodeURIComponent(bindingId)}`, {
      method: 'PATCH', headers: ifMatch(revision), body: JSON.stringify(patch),
    }),
  deleteSkill: (id: string, bindingId: string, revision: number) =>
    request<void>(`/api/v1/companions/${encodeURIComponent(id)}/skills/${encodeURIComponent(bindingId)}`, {
      method: 'DELETE', headers: ifMatch(revision),
    }),

  listConnections: (id: string) => request<CompanionConnection[]>(`/api/v1/companions/${encodeURIComponent(id)}/connections`),
  addConnection: (id: string, input: CompanionConnectionCreateInput) =>
    request<CompanionConnection>(`/api/v1/companions/${encodeURIComponent(id)}/connections`, {
      method: 'POST', body: JSON.stringify(input),
    }),
  patchConnection: (id: string, bindingId: string, revision: number, patch: CompanionConnectionPatchInput) =>
    request<CompanionConnection>(`/api/v1/companions/${encodeURIComponent(id)}/connections/${encodeURIComponent(bindingId)}`, {
      method: 'PATCH', headers: ifMatch(revision), body: JSON.stringify(patch),
    }),
  deleteConnection: (id: string, bindingId: string, revision: number) =>
    request<void>(`/api/v1/companions/${encodeURIComponent(id)}/connections/${encodeURIComponent(bindingId)}`, {
      method: 'DELETE', headers: ifMatch(revision),
    }),

  listWorkSessions: async (companionId: string): Promise<CompanionConnectedWorkSession[]> => {
    const raw = await request<unknown>(`/api/v1/companions/${encodeURIComponent(companionId)}/work-sessions`)
    const list = Array.isArray(raw)
      ? raw
      : Array.isArray((raw as { sessions?: unknown } | null)?.sessions)
        ? (raw as { sessions: unknown[] }).sessions
        : Array.isArray((raw as { items?: unknown } | null)?.items)
          ? (raw as { items: unknown[] }).items
          : []
    return list
      .map(normalizeConnectedWorkSession)
      .filter((item): item is CompanionConnectedWorkSession => item !== null)
  },

  /** Discover available built-in Electron and external ego-lite browser profiles. */
  listBrowserProfiles: async (): Promise<CompanionBrowserProfile[]> => {
    const raw = await request<unknown>('/api/v1/companions/browser-profiles')
    const list = Array.isArray(raw)
      ? raw
      : Array.isArray((raw as { profiles?: unknown } | null)?.profiles)
        ? (raw as { profiles: unknown[] }).profiles
        : Array.isArray((raw as { items?: unknown } | null)?.items)
          ? (raw as { items: unknown[] }).items
          : []
    return list
      .map(normalizeBrowserProfile)
      .filter((item): item is CompanionBrowserProfile => item !== null)
  },

  /** Active project workspaces available for work_session connector scoping. */
  listProjects: () => request<CompanionProjectSummary[]>('/api/v1/projects?lifecycle_status=active'),

  /** Decide a pending engineering Work Session tool approval directly via Core ControlPlaneEndpoints. */
  decideApproval: (approvalId: string, approved: boolean, note?: string | null) => {
    const path = `/api/v1/approvals/${encodeURIComponent(approvalId)}/decision`
    const body: Record<string, string> = { decision: approved ? 'approved' : 'rejected' }
    if (note && note.trim()) body.reason = note.trim()
    return request<CompanionApprovalDecisionResult>(path, {
      method: 'POST',
      headers: { 'x-tinadec-storage-id': captureStorageId(path) },
      body: JSON.stringify(body),
    })
  },

  /** Cancel/interrupt an active Work Session run directly via Core DmaeaEndpoints. */
  cancelRun: (runId: string) => {
    const path = `/api/v1/runs/${encodeURIComponent(runId)}/control`
    const clientControlId = typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? `companion-cancel-${crypto.randomUUID()}`
      : `companion-cancel-${Date.now()}`
    return request<CompanionRunCancelResult>(path, {
      method: 'POST',
      headers: { 'x-tinadec-storage-id': captureStorageId(path) },
      body: JSON.stringify({ action: 'cancel', client_control_id: clientControlId }),
    })
  },

  listSchedules: (id: string) =>
    request<CompanionSchedule[]>(`/api/v1/companions/${encodeURIComponent(id)}/schedules`),
  createSchedule: (id: string, input: CompanionScheduleInput) =>
    request<CompanionSchedule>(`/api/v1/companions/${encodeURIComponent(id)}/schedules`, {
      method: 'POST', body: JSON.stringify(input),
    }),
  patchSchedule: (id: string, scheduleId: string, revision: number | null | undefined, patch: CompanionSchedulePatchInput) =>
    request<CompanionSchedule>(`/api/v1/companions/${encodeURIComponent(id)}/schedules/${encodeURIComponent(scheduleId)}`, {
      method: 'PATCH',
      headers: typeof revision === 'number' ? ifMatch(revision) : undefined,
      body: JSON.stringify(patch),
    }),
  deleteSchedule: (id: string, scheduleId: string) =>
    request<void>(`/api/v1/companions/${encodeURIComponent(id)}/schedules/${encodeURIComponent(scheduleId)}`, {
      method: 'DELETE',
    }),
  triggerSchedule: (id: string, scheduleId: string) =>
    request<CompanionScheduleTriggerResult>(`/api/v1/companions/${encodeURIComponent(id)}/schedules/${encodeURIComponent(scheduleId)}/trigger`, {
      method: 'POST',
    }),

  listPlans: (id: string) =>
    request<CompanionPlan[]>(`/api/v1/companions/${encodeURIComponent(id)}/plans`),
  createPlan: (id: string, input: CompanionPlanInput) =>
    request<CompanionPlan>(`/api/v1/companions/${encodeURIComponent(id)}/plans`, {
      method: 'POST', body: JSON.stringify(input),
    }),
  patchPlan: (id: string, planId: string, revision: number | null | undefined, patch: CompanionPlanPatchInput) =>
    request<CompanionPlan>(`/api/v1/companions/${encodeURIComponent(id)}/plans/${encodeURIComponent(planId)}`, {
      method: 'PATCH',
      headers: typeof revision === 'number' ? ifMatch(revision) : undefined,
      body: JSON.stringify(patch),
    }),
  deletePlan: (id: string, planId: string) =>
    request<void>(`/api/v1/companions/${encodeURIComponent(id)}/plans/${encodeURIComponent(planId)}`, {
      method: 'DELETE',
    }),

  listTodos: (id: string) =>
    request<CompanionTodo[]>(`/api/v1/companions/${encodeURIComponent(id)}/todos`),
  createTodo: (id: string, input: CompanionTodoInput) =>
    request<CompanionTodo>(`/api/v1/companions/${encodeURIComponent(id)}/todos`, {
      method: 'POST', body: JSON.stringify(input),
    }),
  patchTodo: (id: string, todoId: string, revision: number | null | undefined, patch: CompanionTodoPatchInput) =>
    request<CompanionTodo>(`/api/v1/companions/${encodeURIComponent(id)}/todos/${encodeURIComponent(todoId)}`, {
      method: 'PATCH',
      headers: typeof revision === 'number' ? ifMatch(revision) : undefined,
      body: JSON.stringify(patch),
    }),
  deleteTodo: (id: string, todoId: string) =>
    request<void>(`/api/v1/companions/${encodeURIComponent(id)}/todos/${encodeURIComponent(todoId)}`, {
      method: 'DELETE',
    }),

  listConversations: (id: string) =>
    request<CompanionConversation[]>(`/api/v1/companions/${encodeURIComponent(id)}/conversations`),
  listAllConversations: (params?: { companionId?: string; kind?: 'private' | 'group'; origin?: 'user' | 'companion'; limit?: number }) =>
    request<CompanionConversation[]>(`/api/v1/companion-conversations${query({
      companion_id: params?.companionId,
      kind: params?.kind,
      origin: params?.origin,
      limit: params?.limit,
    })}`),
  ensurePrivateConversation: (id: string) =>
    request<CompanionConversation>(`/api/v1/companions/${encodeURIComponent(id)}/conversations`, {
      method: 'POST', body: JSON.stringify({ kind: 'private' }),
    }),
  createGroupConversation: (id: string, input: CompanionGroupConversationInput) =>
    request<CompanionConversation>(`/api/v1/companions/${encodeURIComponent(id)}/conversations`, {
      method: 'POST', body: JSON.stringify({ kind: 'group', ...input }),
    }),

  getConversation: (id: string) =>
    request<CompanionConversation>(`/api/v1/companion-conversations/${encodeURIComponent(id)}`),
  inviteMember: (conversationId: string, input: CompanionInviteInput) =>
    request<CompanionConversation>(`/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/members`, {
      method: 'POST', body: JSON.stringify(input),
    }),
  leaveMember: (conversationId: string, companionId: string) =>
    request<CompanionConversation>(
      `/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/members/${encodeURIComponent(companionId)}`,
      { method: 'DELETE' },
    ),
  pauseConversation: (conversationId: string, paused: boolean) =>
    request<CompanionConversation>(`/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/pause`, {
      method: 'POST', body: JSON.stringify({ paused }),
    }),

  listMessages: (conversationId: string, afterSeq?: number, limit?: number) =>
    request<CompanionMessage[]>(
      `/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/messages${query({ after_seq: afterSeq, limit })}`,
    ),
  sendMessage: (conversationId: string, input: CompanionMessagePostInput, idempotencyKey?: string) =>
    request<CompanionSendMessageResult>(`/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/messages`, {
      method: 'POST',
      headers: idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : undefined,
      body: JSON.stringify(input),
    }),

  getRun: (runId: string) => request<CompanionRun>(`/api/v1/companion-runs/${encodeURIComponent(runId)}`),
  listActivity: (limit?: number) => request<CompanionActivity[]>(`/api/v1/companions/activity${query({ limit })}`),
  listConnectionsOverview: () => request<CompanionConnectionOverview[]>('/api/v1/companion-connections'),
  getSettings: () => request<CompanionSettings>('/api/v1/companion-settings'),
  patchSettings: (revision: number, patch: CompanionSettingsPatchInput) =>
    request<CompanionSettings>('/api/v1/companion-settings', {
      method: 'PATCH', headers: ifMatch(revision), body: JSON.stringify(patch),
    }),
  tickAutonomy: (id: string) =>
    request<CompanionAutonomyTickResult>(`/api/v1/companions/${encodeURIComponent(id)}/autonomy/tick`, { method: 'POST' }),
  recordEmote: (id: string, emote: 'wave' | 'pat') =>
    request<CompanionActivity>(`/api/v1/companions/${encodeURIComponent(id)}/emote`, {
      method: 'POST', body: JSON.stringify({ emote }),
    }),
  listMandates: (id: string) =>
    request<CompanionMandate[]>(`/api/v1/companions/${encodeURIComponent(id)}/mandates`),
  decideMandate: (id: string, mandateId: string, revision: number, decision: 'approved' | 'denied') =>
    request<CompanionMandate>(`/api/v1/companions/${encodeURIComponent(id)}/mandates/${encodeURIComponent(mandateId)}/decision`, {
      method: 'POST', headers: ifMatch(revision), body: JSON.stringify({ decision }),
    }),
  listRecruitments: () => request<CompanionRecruitment[]>('/api/v1/companion-recruitments'),
  decideRecruitment: (recruitmentId: string, revision: number, decision: 'approved' | 'denied') =>
    request<CompanionRecruitment>(`/api/v1/companion-recruitments/${encodeURIComponent(recruitmentId)}/decision`, {
      method: 'POST', headers: ifMatch(revision), body: JSON.stringify({ decision }),
    }),
}

// ── Live feed ────────────────────────────────────────────────────────

export function companionStreamSupported(): boolean {
  return typeof EventSource !== 'undefined'
}

export interface CompanionFeedHandlers {
  onNotice: (notice: CompanionEventNotice) => void
  onHeartbeat?: (cursor: number) => void
  onError?: () => void
}

export interface CompanionFeedHandle {
  readonly cursor: number
  close(): void
}

function parseEventPayload(text: string): CompanionEventPayload | null {
  try {
    const parsed: unknown = JSON.parse(text)
    return parsed && typeof parsed === 'object' ? parsed as CompanionEventPayload : null
  } catch {
    return null
  }
}

function isDurableJournalEvent(type: CompanionEventType): boolean {
  return type === COMPANION_ACTIVITY_EVENT
    || type === COMPANION_RUN_EVENT
    || type === COMPANION_SETTINGS_EVENT
}

function noticeOf(type: CompanionEventType, payload: CompanionEventPayload, cursor: number): CompanionEventNotice {
  const seq = typeof payload.seq === 'number' && Number.isFinite(payload.seq) ? payload.seq : cursor
  const nestedRunId = payload.payload && typeof payload.payload.run_id === 'string' ? payload.payload.run_id : null
  const runId = typeof payload.run_id === 'string' ? payload.run_id : nestedRunId
  const base: CompanionEventNotice = {
    type,
    seq,
    kind: typeof payload.kind === 'string' ? payload.kind : null,
    companionId: typeof payload.companion_id === 'string' ? payload.companion_id : null,
    conversationId: typeof payload.conversation_id === 'string' ? payload.conversation_id : null,
    runId,
  }
  if (type === COMPANION_DELTA_EVENT) {
    base.delta = typeof payload.delta === 'string' ? payload.delta : ''
  } else if (type === COMPANION_TOOL_CALL_EVENT) {
    base.callId = typeof payload.call_id === 'string' ? payload.call_id : null
    base.toolName = typeof payload.tool_name === 'string' ? payload.tool_name : null
    if (payload.status === 'started' || payload.status === 'completed' || payload.status === 'failed') {
      base.toolStatus = payload.status
    }
    base.argumentsPreview = typeof payload.arguments_preview === 'string' ? payload.arguments_preview : null
    base.resultPreview = typeof payload.result_preview === 'string' ? payload.result_preview : null
  } else if (type === COMPANION_PROPOSAL_EVENT) {
    if (payload.kind === 'mandate' || payload.kind === 'recruitment') base.proposalKind = payload.kind
    base.proposalId = typeof payload.proposal_id === 'string' ? payload.proposal_id : null
    base.proposalAction = typeof payload.action === 'string' ? payload.action : null
    base.proposalStatus = typeof payload.status === 'string' ? payload.status : null
    base.proposalSummary = typeof payload.summary === 'string' ? payload.summary : null
    base.proposalPayload = payload.payload && typeof payload.payload === 'object' ? payload.payload : null
  } else if (type === COMPANION_STAGE_CUE_EVENT) {
    base.emotion = typeof payload.emotion === 'string' ? payload.emotion : null
    if (typeof payload.speaking === 'boolean') base.speaking = payload.speaking
    base.intensity = typeof payload.intensity === 'number' && Number.isFinite(payload.intensity) ? payload.intensity : null
    base.gesture = typeof payload.gesture === 'string' ? payload.gesture : null
  } else if (type === COMPANION_WORK_SESSION_EVENT) {
    const source = asRecord(payload.payload) ? { ...asRecord(payload.payload)!, ...payload } : (payload as unknown as Record<string, unknown>)
    const sessionId = asNonEmptyString(source.session_id ?? source.sessionId ?? source.id)
    if (sessionId) {
      const rawApprovals = Array.isArray(source.pending_approvals)
        ? source.pending_approvals
        : Array.isArray(source.pendingApprovals)
          ? source.pendingApprovals
          : []
      const pendingApprovals: CompanionWorkSessionApprovalNotice[] = rawApprovals
        .map(normalizeWorkSessionApproval)
        .filter((item): item is CompanionWorkSessionApprovalNotice => item !== null)
      const wsRunId = asNonEmptyString(source.run_id ?? source.runId ?? source.latest_run_id ?? source.latestRunId) ?? base.runId
      if (wsRunId && !base.runId) base.runId = wsRunId
      const ws: CompanionWorkSessionNotice = {
        sessionId,
        sessionTitle: asNonEmptyString(source.session_title ?? source.sessionTitle ?? source.title) ?? sessionId.slice(0, 8),
        workspaceId: asNonEmptyString(source.workspace_id ?? source.workspaceId),
        projectId: asNonEmptyString(source.project_id ?? source.projectId),
        projectName: asNonEmptyString(source.project_name ?? source.projectName),
        runId: wsRunId,
        status: asNonEmptyString(source.status ?? source.latest_run_status ?? source.latestRunStatus) ?? 'running',
        modeName: asNonEmptyString(source.mode_name ?? source.modeName),
        viewMode: asViewMode(source.view_mode ?? source.viewMode),
        summary: asNonEmptyString(source.summary ?? source.latest_run_summary ?? source.latestRunSummary),
        planSummary: asNonEmptyString(source.plan_summary ?? source.planSummary),
        pendingApprovals,
        browserSpaceId: asNonEmptyString(source.browser_space_id ?? source.browserSpaceId ?? source.space_id ?? source.spaceId),
        browserUrl: asNonEmptyString(source.browser_url ?? source.browserUrl ?? source.url),
        browserDriver: asNonEmptyString(source.browser_driver ?? source.browserDriver ?? source.driver),
        browserHandoffReason: asNonEmptyString(source.browser_handoff_reason ?? source.browserHandoffReason ?? source.handoff_reason ?? source.handoffReason),
        updatedAt: asNonEmptyString(source.updated_at ?? source.updatedAt ?? source.created_at),
      }
      base.workSession = ws
    } else {
      base.workSession = null
    }
  }
  return base
}

export function connectCompanionEvents(
  handlers: CompanionFeedHandlers,
  afterSeq = 0,
): CompanionFeedHandle {
  let cursor = afterSeq
  const source = new ScopedEventSource(
    `${gatewayUrl}/api/v1/companion-events${query({ after_seq: cursor || undefined })}`,
    'user',
  )
  const handle = (message: MessageEvent<string>) => {
    const type = message.type as CompanionEventType
    if (!(COMPANION_EVENT_TYPES as readonly string[]).includes(type)) return
    const payload = parseEventPayload(message.data)
    if (!payload) return
    const notice = noticeOf(type, payload, cursor)
    if (isDurableJournalEvent(type)) {
      cursor = Math.max(cursor, notice.seq)
    }
    handlers.onNotice(notice)
  }
  for (const type of COMPANION_EVENT_TYPES) source.addEventListener(type, handle as EventListener)
  source.addEventListener('heartbeat', ((message: MessageEvent<string>) => {
    const payload = parseEventPayload(message.data) as CompanionHeartbeatPayload | null
    if (typeof payload?.after_seq === 'number' && Number.isFinite(payload.after_seq)) {
      cursor = Math.max(cursor, payload.after_seq)
    }
    handlers.onHeartbeat?.(cursor)
  }) as EventListener)
  source.onerror = () => handlers.onError?.()
  return {
    get cursor() { return cursor },
    close: () => source.close(),
  }
}

export function broadcastCompanionNotice(notice: CompanionEventNotice): void {
  if (typeof BroadcastChannel === 'undefined') return
  try {
    relayChannel().postMessage({ type: 'notice', notice } satisfies CompanionEventBroadcast)
  } catch {
    // A closed or full channel must not break the stream that produced the notice.
  }
}

let relay: BroadcastChannel | null = null

function relayChannel(): BroadcastChannel {
  relay ??= new BroadcastChannel(COMPANION_EVENT_CHANNEL)
  return relay
}

const feedHandlers = new Set<(notice: CompanionEventNotice) => void>()
let feedSource: CompanionFeedHandle | null = null
let feedCursor = 0
let feedVisibilityBound = false

function onFeedVisibility(): void {
  if (typeof document === 'undefined') return
  if (document.visibilityState === 'hidden') closeFeed()
  else openFeed()
}

function openFeed(): void {
  if (feedSource || feedHandlers.size === 0 || !companionStreamSupported()) return
  if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return
  feedSource = connectCompanionEvents({
    onNotice: (notice) => {
      if (isDurableJournalEvent(notice.type)) {
        feedCursor = Math.max(feedCursor, notice.seq)
      }
      broadcastCompanionNotice(notice)
      for (const handler of [...feedHandlers]) {
        try { handler(notice) } catch { /* One consumer must not take the shared feed down. */ }
      }
    },
    onHeartbeat: (cursor) => { feedCursor = Math.max(feedCursor, cursor) },
    onError: () => {},
  }, feedCursor)
}

function closeFeed(): void {
  feedSource?.close()
  feedSource = null
}

/** Receive every companion notice for as long as the returned unsubscribe is held. */
export function subscribeCompanionFeed(handler: (notice: CompanionEventNotice) => void): () => void {
  feedHandlers.add(handler)
  if (!feedVisibilityBound && typeof document !== 'undefined') {
    feedVisibilityBound = true
    document.addEventListener('visibilitychange', onFeedVisibility)
  }
  openFeed()
  return () => {
    feedHandlers.delete(handler)
    if (feedHandlers.size > 0) return
    closeFeed()
    if (feedVisibilityBound && typeof document !== 'undefined') {
      document.removeEventListener('visibilitychange', onFeedVisibility)
      feedVisibilityBound = false
    }
  }
}

export function subscribeCompanionEventRelay(handler: (notice: CompanionEventNotice) => void): () => void {
  if (typeof BroadcastChannel === 'undefined') return () => {}
  const relay = new BroadcastChannel(COMPANION_EVENT_CHANNEL)
  const listener = (event: MessageEvent<unknown>) => {
    if (!isCompanionEventBroadcast(event.data)) return
    try { handler(event.data.notice) } catch { /* One listener must not take the relay down. */ }
  }
  relay.addEventListener('message', listener as EventListener)
  return () => {
    relay.removeEventListener('message', listener as EventListener)
    relay.close()
  }
}

type CompanionBridgeMethod = 'GET' | 'POST' | 'PATCH'
interface CompanionBridgeResult { status: number; body: unknown }
type CompanionBridgeCall = (method: CompanionBridgeMethod, path: string, body?: unknown) => Promise<CompanionBridgeResult>

function bridgeInvoker(): CompanionBridgeCall {
  const invoke = typeof window === 'undefined' ? undefined : window.tinadec?.companion?.api
  if (typeof invoke !== 'function') {
    const message = i18n.global.t('host.previewAction')
    throw new ApiError(message, 403, { code: 'desktop_host_required', detail: message })
  }
  return invoke as CompanionBridgeCall
}

async function bridged<T>(method: CompanionBridgeMethod, path: string, body?: unknown): Promise<T> {
  const result = await bridgeInvoker()(method, path, body)
  if (result.status < 200 || result.status >= 300) {
    throw new ApiError(apiErrorMessage(result.body, `Companion request failed (${result.status})`), result.status, result.body)
  }
  return result.body as T
}

/**
 * The satellite companion window's chat subset over the main-process proxy.
 */
export const companionBridge = {
  listCompanions: (includeArchived?: boolean) =>
    bridged<CompanionSummary[]>('GET', `/api/v1/companions${query({ include_archived: includeArchived })}`),
  getCompanion: (id: string) => bridged<CompanionDetail>('GET', `/api/v1/companions/${encodeURIComponent(id)}`),

  listConversations: (id: string) =>
    bridged<CompanionConversation[]>('GET', `/api/v1/companions/${encodeURIComponent(id)}/conversations`),
  ensurePrivateConversation: (id: string) =>
    bridged<CompanionConversation>('POST', `/api/v1/companions/${encodeURIComponent(id)}/conversations`, { kind: 'private' }),
  createGroupConversation: (id: string, input: CompanionGroupConversationInput) =>
    bridged<CompanionConversation>('POST', `/api/v1/companions/${encodeURIComponent(id)}/conversations`, {
      kind: 'group', ...input,
    }),

  getConversation: (id: string) =>
    bridged<CompanionConversation>('GET', `/api/v1/companion-conversations/${encodeURIComponent(id)}`),
  inviteMember: (conversationId: string, input: CompanionInviteInput) =>
    bridged<CompanionConversation>('POST', `/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/members`, input),
  pauseConversation: (conversationId: string, paused: boolean) =>
    bridged<CompanionConversation>('POST', `/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/pause`, { paused }),

  listMessages: (conversationId: string, afterSeq?: number, limit?: number) =>
    bridged<CompanionMessage[]>(
      'GET',
      `/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/messages${query({ after_seq: afterSeq, limit })}`,
    ),
  sendMessage: (conversationId: string, input: CompanionMessagePostInput, idempotencyKey?: string) =>
    bridged<CompanionSendMessageResult>(
      'POST',
      `/api/v1/companion-conversations/${encodeURIComponent(conversationId)}/messages`,
      idempotencyKey ? { ...input, idempotency_key: idempotencyKey } : input,
    ),

  getRun: (runId: string) => bridged<CompanionRun>('GET', `/api/v1/companion-runs/${encodeURIComponent(runId)}`),
  listActivity: (limit?: number) => bridged<CompanionActivity[]>('GET', `/api/v1/companions/activity${query({ limit })}`),
  getSettings: () => bridged<CompanionSettings>('GET', '/api/v1/companion-settings'),
  tickAutonomy: (id: string) =>
    bridged<CompanionAutonomyTickResult>('POST', `/api/v1/companions/${encodeURIComponent(id)}/autonomy/tick`),
  recordEmote: (id: string, emote: 'wave' | 'pat') =>
    bridged<CompanionActivity>('POST', `/api/v1/companions/${encodeURIComponent(id)}/emote`, { emote }),
  listMandates: (id: string) =>
    bridged<CompanionMandate[]>('GET', `/api/v1/companions/${encodeURIComponent(id)}/mandates`),
  decideMandate: (id: string, mandateId: string, revision: number, decision: 'approved' | 'denied') =>
    bridged<CompanionMandate>('POST', `/api/v1/companions/${encodeURIComponent(id)}/mandates/${encodeURIComponent(mandateId)}/decision`, {
      revision, decision,
    }),
}
