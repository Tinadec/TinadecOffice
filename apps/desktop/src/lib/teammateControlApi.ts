import i18n from '@/i18n'
import { ApiError, apiErrorMessage } from './apiError'
import { readBackendResponse } from './backendRequest'
import { isElectronHost, resolveWebGatewayBase } from './hostConnection'
import { invalidateReadCache } from './readCache'
import {
  captureStorageId,
  normalizeStorageRequest,
  rememberStorageResult,
  storageHeaders,
  type StorageRequestOptions,
} from './storageScope'

export type TeammateNormalizedStatus =
  | 'running'
  | 'paused'
  | 'idle'
  | 'awaiting_approval'
  | 'completed'
  | 'failed'
  | 'queued'

export type TeammateSteerDeliveryMode =
  | 'injected_next_turn'
  | 'woken'
  | 'queued_while_paused'
  | (string & {})

export interface RunTeammateStatusDto {
  handle: string
  display_name?: string | null
  agent_slug?: string | null
  role?: string | null
  mode?: 'teammate' | 'ephemeral' | string | null
  status: string
  paused: boolean
  awaiting_approval: boolean
  current_task_id?: string | null
  current_task_title?: string | null
  claimed_step_id?: string | null
  latest_summary?: string | null
  runtime_badge?: string | null
  worktree_path?: string | null
  branch_name?: string | null
  write_scope?: string[]
  relay_depth?: number
  pending_steer_count: number
  updated_at?: string | null
}

export interface RunTeammatesSnapshotDto {
  run_id: string
  session_id?: string | null
  run_status?: string | null
  teammates: RunTeammateStatusDto[]
}

export interface SteerTeammateInput {
  message: string
  urgent?: boolean
  client_control_id?: string
}

export interface TeammateSteerReceiptDto {
  accepted: boolean
  run_id: string
  handle: string
  status: string
  paused: boolean
  urgent: boolean
  woken: boolean
  delivery_mode: TeammateSteerDeliveryMode
  board_seq?: number | null
  pending_steer_count: number
  task_id?: string | null
  delivered_at?: string | null
}

export interface TeammateControlStateReceiptDto {
  accepted: boolean
  run_id: string
  handle: string
  action: 'pause' | 'resume'
  status: string
  paused: boolean
  current_task_id?: string | null
  updated_at?: string | null
}

export interface TeammateControlRequestOptions {
  signal?: AbortSignal
  storageId?: string
}

export function resolveTeammateControlGatewayUrl(): string {
  if (typeof window !== 'undefined') {
    const bridgeUrl = window.tinadec?.gatewayUrl?.()
    if (bridgeUrl) return bridgeUrl.replace(/\/+$/, '')
    if (!isElectronHost()) {
      return resolveWebGatewayBase().replace(/\/+$/, '')
    }
  }
  return 'http://127.0.0.1:48730'
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {}
}

function asOptionalString(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null
}

function asOptionalNumber(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  return null
}

function asStringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) return undefined
  return value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
}

export function normalizeTeammateHandle(raw: string): string {
  const trimmed = raw.trim()
  return trimmed.startsWith('@') ? trimmed.slice(1).trim() : trimmed
}

export function normalizeTeammateStatusKind(
  rawStatus?: string | null,
  paused = false,
  awaitingApproval = false,
): TeammateNormalizedStatus {
  if (paused) return 'paused'
  if (awaitingApproval) return 'awaiting_approval'
  const status = String(rawStatus ?? '').trim().toLowerCase()
  if (status === 'paused' || status === 'suspended') return 'paused'
  if (
    status === 'awaiting_approval'
    || status === 'waiting_approval'
    || status === 'awaiting_user'
    || status === 'awaiting_delegate'
    || status === 'gate_review'
  ) {
    return 'awaiting_approval'
  }
  if (
    status === 'running'
    || status === 'working'
    || status === 'active'
    || status === 'executing'
    || status === 'in_progress'
    || status === 'in-progress'
    || status === 'planning'
    || status === 'reviewing'
    || status === 'assigned'
  ) {
    return 'running'
  }
  if (status === 'completed' || status === 'done' || status === 'succeeded' || status === 'terminated') {
    return 'completed'
  }
  if (status === 'failed' || status === 'error' || status === 'cancelled' || status === 'blocked') {
    return 'failed'
  }
  if (status === 'queued' || status === 'pending') return 'queued'
  return 'idle'
}

export function normalizeTeammateStatus(raw: unknown): RunTeammateStatusDto | null {
  const rec = asRecord(raw)
  const handle = asOptionalString(rec.handle ?? rec.teammate_handle ?? rec.teammateHandle)
  if (!handle) return null
  const rawStatus = asOptionalString(rec.status ?? rec.state ?? rec.presence) ?? 'waiting'
  const paused = Boolean(rec.paused ?? rec.is_paused ?? rec.isPaused ?? rawStatus.toLowerCase() === 'paused')
  const awaitingApproval = Boolean(
    rec.awaiting_approval
    ?? rec.awaitingApproval
    ?? rawStatus.toLowerCase() === 'awaiting_approval',
  )
  const pendingCount = asOptionalNumber(rec.pending_steer_count ?? rec.pendingSteerCount)
  const relayDepth = asOptionalNumber(rec.relay_depth ?? rec.relayDepth)

  return {
    handle,
    display_name: asOptionalString(rec.display_name ?? rec.displayName ?? rec.name),
    agent_slug: asOptionalString(rec.agent_slug ?? rec.agentSlug),
    role: asOptionalString(rec.role),
    mode: asOptionalString(rec.mode),
    status: rawStatus,
    paused,
    awaiting_approval: awaitingApproval,
    current_task_id: asOptionalString(rec.current_task_id ?? rec.currentTaskId ?? rec.task_id ?? rec.taskId),
    current_task_title: asOptionalString(
      rec.current_task_title ?? rec.currentTaskTitle ?? rec.current_task ?? rec.currentTask,
    ),
    claimed_step_id: asOptionalString(rec.claimed_step_id ?? rec.claimedStepId),
    latest_summary: asOptionalString(rec.latest_summary ?? rec.latestSummary ?? rec.summary ?? rec.thought),
    runtime_badge: asOptionalString(rec.runtime_badge ?? rec.runtimeBadge),
    worktree_path: asOptionalString(rec.worktree_path ?? rec.worktreePath),
    branch_name: asOptionalString(rec.branch_name ?? rec.branchName),
    write_scope: asStringArray(rec.write_scope ?? rec.writeScope),
    relay_depth: relayDepth !== null ? Math.max(0, Math.round(relayDepth)) : undefined,
    pending_steer_count: pendingCount !== null ? Math.max(0, Math.round(pendingCount)) : 0,
    updated_at: asOptionalString(rec.updated_at ?? rec.updatedAt),
  }
}

export function normalizeSteerReceipt(
  raw: unknown,
  fallbackRunId: string,
  fallbackHandle: string,
  fallbackUrgent = true,
): TeammateSteerReceiptDto {
  const rec = asRecord(raw)
  const rawDelivery = asOptionalString(rec.delivery_mode ?? rec.deliveryMode ?? rec.delivery)
  const woken = Boolean(rec.woken ?? rawDelivery === 'woken')
  const status = asOptionalString(rec.status ?? rec.state) ?? (woken ? 'working' : 'waiting')
  const paused = Boolean(
    rec.paused
    ?? rec.is_paused
    ?? (rawDelivery === 'queued_while_paused' || status.toLowerCase() === 'paused'),
  )
  const deliveryMode: TeammateSteerDeliveryMode = rawDelivery
    ?? (woken ? 'woken' : paused ? 'queued_while_paused' : 'injected_next_turn')
  const boardSeq = asOptionalNumber(rec.board_seq ?? rec.boardSeq)
  const pendingCount = asOptionalNumber(rec.pending_steer_count ?? rec.pendingSteerCount)

  return {
    accepted: rec.accepted === undefined ? true : Boolean(rec.accepted),
    run_id: asOptionalString(rec.run_id ?? rec.runId) ?? fallbackRunId,
    handle: asOptionalString(rec.handle ?? rec.teammate_handle ?? rec.teammateHandle) ?? fallbackHandle,
    status,
    paused,
    urgent: typeof rec.urgent === 'boolean' ? rec.urgent : fallbackUrgent,
    woken,
    delivery_mode: deliveryMode,
    board_seq: boardSeq !== null ? Math.round(boardSeq) : null,
    pending_steer_count: pendingCount !== null ? Math.max(0, Math.round(pendingCount)) : 0,
    task_id: asOptionalString(rec.task_id ?? rec.taskId),
    delivered_at: asOptionalString(rec.delivered_at ?? rec.deliveredAt ?? rec.occurred_at ?? rec.created_at),
  }
}

export function normalizeControlStateReceipt(
  raw: unknown,
  fallbackRunId: string,
  fallbackHandle: string,
  expectedAction: 'pause' | 'resume',
): TeammateControlStateReceiptDto {
  const rec = asRecord(raw)
  const expectedPaused = expectedAction === 'pause'
  const rawAction = asOptionalString(rec.action)
  const action: 'pause' | 'resume' = rawAction === 'pause' || rawAction === 'resume' ? rawAction : expectedAction
  const status = asOptionalString(rec.status ?? rec.state) ?? (expectedPaused ? 'paused' : 'working')
  const paused = typeof rec.paused === 'boolean'
    ? rec.paused
    : typeof rec.is_paused === 'boolean'
      ? rec.is_paused
      : expectedPaused

  return {
    accepted: rec.accepted === undefined ? true : Boolean(rec.accepted),
    run_id: asOptionalString(rec.run_id ?? rec.runId) ?? fallbackRunId,
    handle: asOptionalString(rec.handle ?? rec.teammate_handle ?? rec.teammateHandle) ?? fallbackHandle,
    action,
    status,
    paused,
    current_task_id: asOptionalString(rec.current_task_id ?? rec.currentTaskId ?? rec.task_id),
    updated_at: asOptionalString(rec.updated_at ?? rec.updatedAt ?? rec.occurred_at),
  }
}

async function requestJson<T>(path: string, init?: StorageRequestOptions): Promise<T> {
  const gatewayUrl = resolveTeammateControlGatewayUrl()
  const storageId = captureStorageId(path, init)
  const normalized = normalizeStorageRequest(path, init)
  const headers = storageHeaders(path, { ...init, storageId })
  if (!headers.has('accept')) headers.set('accept', 'application/json')
  if (init?.body && !headers.has('content-type')) headers.set('content-type', 'application/json')

  const { response, text } = await readBackendResponse(
    `${gatewayUrl}${normalized.path}`,
    path,
    {
      ...init,
      body: normalized.body,
      headers,
    },
    { storageId },
  )

  let data: unknown = null
  if (text.length > 0) {
    try {
      data = JSON.parse(text)
    } catch {
      const msg = i18n.global.te('errors.invalidServerResponse')
        ? i18n.global.t('errors.invalidServerResponse', { text: text.substring(0, 200) })
        : `Invalid server response: ${text.substring(0, 200)}`
      throw new Error(msg)
    }
  }

  if (!response.ok) {
    throw new ApiError(apiErrorMessage(data, response.statusText), response.status, data, { storageId })
  }

  rememberStorageResult(path, data, storageId)
  if (init?.method && init.method !== 'GET' && init.method !== 'HEAD') {
    invalidateReadCache()
  }

  return data as T
}

function teammatePath(runId: string, handle?: string, action?: 'steer' | 'pause' | 'resume'): string {
  const base = `/api/v1/runs/${encodeURIComponent(runId.trim())}/teammates`
  if (!handle) return base
  const cleanHandle = normalizeTeammateHandle(handle)
  const withHandle = `${base}/${encodeURIComponent(cleanHandle)}`
  return action ? `${withHandle}/${action}` : withHandle
}

/**
 * Fetches live runtime snapshot for all teammates in a run.
 * GET /api/v1/runs/{runId}/teammates
 */
export async function getRunTeammatesSnapshot(
  runId: string,
  options?: TeammateControlRequestOptions,
): Promise<RunTeammatesSnapshotDto> {
  const cleanRunId = runId.trim()
  if (!cleanRunId) {
    return { run_id: '', teammates: [] }
  }
  const raw = await requestJson<unknown>(teammatePath(cleanRunId), {
    method: 'GET',
    signal: options?.signal,
    storageId: options?.storageId,
  })
  const rec = asRecord(raw)
  const rows = Array.isArray(raw)
    ? raw
    : Array.isArray(rec.teammates)
      ? (rec.teammates as unknown[])
      : Array.isArray(rec.items)
        ? (rec.items as unknown[])
        : []
  return {
    run_id: asOptionalString(rec.run_id ?? rec.runId) ?? cleanRunId,
    session_id: asOptionalString(rec.session_id ?? rec.sessionId),
    run_status: asOptionalString(rec.run_status ?? rec.runStatus),
    teammates: rows
      .map((entry) => normalizeTeammateStatus(entry))
      .filter((entry): entry is RunTeammateStatusDto => entry !== null),
  }
}

/**
 * Convenience wrapper returning just the teammates list for a run.
 * GET /api/v1/runs/{runId}/teammates
 */
export async function listRunTeammates(
  runId: string,
  options?: TeammateControlRequestOptions,
): Promise<RunTeammateStatusDto[]> {
  const snapshot = await getRunTeammatesSnapshot(runId, options)
  return snapshot.teammates
}

/**
 * Sends a direct human steering message to a specific active or sleeping teammate.
 * POST /api/v1/runs/{runId}/teammates/{handle}/steer
 */
export async function steerRunTeammate(
  runId: string,
  handle: string,
  input: SteerTeammateInput,
  options?: TeammateControlRequestOptions,
): Promise<TeammateSteerReceiptDto> {
  const cleanRunId = runId.trim()
  const cleanHandle = normalizeTeammateHandle(handle)
  const message = input.message.trim()
  if (!cleanRunId || !cleanHandle || !message) {
    throw new Error('runId, handle, and non-empty message are required to steer a teammate.')
  }
  const urgent = input.urgent ?? true
  const payload: Record<string, unknown> = {
    message,
    urgent,
  }
  if (input.client_control_id?.trim()) {
    payload.client_control_id = input.client_control_id.trim()
  }
  const raw = await requestJson<unknown>(teammatePath(cleanRunId, cleanHandle, 'steer'), {
    method: 'POST',
    body: JSON.stringify(payload),
    signal: options?.signal,
    storageId: options?.storageId,
  })
  return normalizeSteerReceipt(raw, cleanRunId, cleanHandle, urgent)
}

/**
 * Independently pauses a single teammate without stopping the rest of the run.
 * POST /api/v1/runs/{runId}/teammates/{handle}/pause
 */
export async function pauseRunTeammate(
  runId: string,
  handle: string,
  options?: TeammateControlRequestOptions & { reason?: string },
): Promise<TeammateControlStateReceiptDto> {
  const cleanRunId = runId.trim()
  const cleanHandle = normalizeTeammateHandle(handle)
  if (!cleanRunId || !cleanHandle) {
    throw new Error('runId and handle are required to pause a teammate.')
  }
  const body = options?.reason?.trim() ? JSON.stringify({ reason: options.reason.trim() }) : undefined
  const raw = await requestJson<unknown>(teammatePath(cleanRunId, cleanHandle, 'pause'), {
    method: 'POST',
    ...(body ? { body } : {}),
    signal: options?.signal,
    storageId: options?.storageId,
  })
  return normalizeControlStateReceipt(raw, cleanRunId, cleanHandle, 'pause')
}

/**
 * Resumes an independently paused teammate.
 * POST /api/v1/runs/{runId}/teammates/{handle}/resume
 */
export async function resumeRunTeammate(
  runId: string,
  handle: string,
  options?: TeammateControlRequestOptions,
): Promise<TeammateControlStateReceiptDto> {
  const cleanRunId = runId.trim()
  const cleanHandle = normalizeTeammateHandle(handle)
  if (!cleanRunId || !cleanHandle) {
    throw new Error('runId and handle are required to resume a teammate.')
  }
  const raw = await requestJson<unknown>(teammatePath(cleanRunId, cleanHandle, 'resume'), {
    method: 'POST',
    signal: options?.signal,
    storageId: options?.storageId,
  })
  return normalizeControlStateReceipt(raw, cleanRunId, cleanHandle, 'resume')
}

export const teammateControlApi = {
  getSnapshot: getRunTeammatesSnapshot,
  list: listRunTeammates,
  steer: steerRunTeammate,
  pause: pauseRunTeammate,
  resume: resumeRunTeammate,
}
