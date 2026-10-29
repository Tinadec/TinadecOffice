import i18n from '@/i18n'
import type { components } from '@/generated/schema'
import { ApiError, apiErrorMessage } from './apiError'
import { assertHostAccess } from './hostAccess'

/**
 * Companion domain client (spec: add-companion-mode, Task 5).
 *
 * Two callers, one contract:
 * - `companionApi` runs in the trusted main window and talks to the Gateway directly, exactly like
 *   `api.ts` (same origin, same host-access gate, no storage identity: Core pins the companion
 *   surface to the user scope).
 * - `companionBridge` runs in the companion window, which is NOT a trusted host. It reaches the
 *   Gateway only through the main-process whitelist proxy and exposes the chat subset.
 */

export type CompanionSummary = components['schemas']['CompanionSummary']
export type CompanionDetail = components['schemas']['CompanionDetail']
export type CompanionAutonomy = components['schemas']['CompanionAutonomy']
export type CompanionMemory = components['schemas']['CompanionMemory']
export type CompanionSkill = components['schemas']['CompanionSkill']
export type CompanionConnection = components['schemas']['CompanionConnection']
export type CompanionConnectionOverview = components['schemas']['CompanionConnectionOverview']
export type CompanionMember = components['schemas']['CompanionMember']
export type CompanionConversation = components['schemas']['CompanionConversation']
export type CompanionMessage = components['schemas']['CompanionMessage']
export type CompanionRun = components['schemas']['CompanionRun']
export type CompanionActivity = components['schemas']['CompanionActivity']
export type CompanionSettings = components['schemas']['CompanionSettings']
export type CompanionSendMessageResult = components['schemas']['CompanionSendMessageResult']
export type CompanionAutonomyTickResult = components['schemas']['CompanionAutonomyTickResult']

/** Request bodies are not in the external OpenAPI (the Gateway forwards Core's bytes verbatim). */
export interface CompanionAutonomyInput {
  enabled?: boolean
  paused?: boolean
  interval_minutes?: number
  daily_action_budget?: number
}

export interface CompanionCreateInput {
  name?: string
  tagline?: string | null
  avatar?: string | null
  persona_document?: string | null
  model_purpose?: string
  /** "<providerInstanceId>|<model>"; empty clears the pin back to the route purpose. */
  model?: string | null
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
  kind?: 'mcp' | 'server' | 'phone' | 'external_agent'
  name?: string
  endpoint?: string | null
  credential_ref?: string | null
  authorization?: 'configured' | 'authorized' | 'revoked'
}

export interface CompanionConnectionPatchInput {
  authorization?: 'configured' | 'authorized' | 'revoked'
  enabled?: boolean
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
}

export interface CompanionSettingsPatchInput {
  main_companion_id?: string | null
  autonomy_paused?: boolean
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
  // Same host gate as `api.ts`. No storage identity is attached: Core pins the companion domain
  // to the user scope, so a scope header here would be a second, wrong answer.
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

  listConversations: (id: string) =>
    request<CompanionConversation[]>(`/api/v1/companions/${encodeURIComponent(id)}/conversations`),
  /**
   * One page of conversations across every companion. The centre used to ask once per companion
   * and merge client-side, which cost one request per companion on every poll and let two panels
   * disagree inside a single refresh.
   */
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
 * The companion window's chat subset. The main process owns the credential and the path
 * whitelist; nothing here may read or write memories, skills or connection bindings, and every
 * path is relative so the proxy can never be aimed at another origin.
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
}
