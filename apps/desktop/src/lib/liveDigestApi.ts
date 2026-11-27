import i18n from '@/i18n'
import { ApiError, apiErrorMessage } from './apiError'
import { assertHostAccess } from './hostAccess'
import { consumeSseResponseBody } from './sseStream'
import { readBackendResponse } from './backendRequest'
import {
  captureStorageId,
  normalizeStorageRequest,
  runStorageId,
  selectionIdentity,
  storageHeaders,
  type StorageRequestOptions,
} from './storageScope'

// ---------------------------------------------------------------------------
// Live Digest REST & SSE Contract Types (100% aligned with Core / Gateway)
// ---------------------------------------------------------------------------

export type LiveDigestMode = 'off' | 'auto' | 'always'

export type LiveDigestResolvedSource =
  | 'explicit_route'
  | 'auto_fast_model'
  | 'chat_fallback'
  | 'unconfigured'
  | (string & {})

export type LiveDigestRunStatus =
  | 'running'
  | 'awaiting_approval'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | (string & {})

export type LiveDigestPhase = 'hypothesis' | 'verification' | 'conclusion' | (string & {})

export type LiveDigestChapterSource =
  | 'l0_structural'
  | 'l1_narrator'
  | 'l2_scout'
  | (string & {})

export type LiveDigestGovernanceSignalCode =
  | 'APPROVAL_REJECTED'
  | 'FS_STALE_VERSION'
  | 'SAME_ARGS_RETRY'
  | 'TIMEOUT'
  | 'WORKTREE_GUARD'
  | 'LOOP_DETECTED'
  | 'CONTEXT_PRESSURE'
  | (string & {})

export type LiveDigestSignalSeverity = 'info' | 'warning' | 'critical' | (string & {})

export type LiveDigestTeammateStatus =
  | 'working'
  | 'waiting'
  | 'paused'
  | 'awaiting_approval'
  | 'completed'
  | 'failed'
  | (string & {})

export interface LiveDigestSettingsDto {
  mode: LiveDigestMode
  route_purpose: string
  provider_instance_id: string | null
  model: string | null
  resolved_provider_instance_id: string | null
  resolved_model: string | null
  resolved_source: LiveDigestResolvedSource
  revision: number
  updated_at: string
}

export interface UpdateLiveDigestSettingsInput {
  mode: LiveDigestMode
  provider_instance_id?: string | null
  model?: string | null
  clear_model_binding?: boolean
}

export interface LiveDigestCompactionPointDto {
  seq: number
  before_tokens: number
  after_tokens: number
  occurred_at: string
  summary?: string | null
}

export interface LiveDigestContextWaterlineDto {
  used_tokens: number
  max_tokens: number
  usage_percent: number
  compaction_count: number
  compaction_points: LiveDigestCompactionPointDto[]
}

export interface LiveDigestGovernanceSignalDto {
  id: string
  code: LiveDigestGovernanceSignalCode
  severity: LiveDigestSignalSeverity
  summary: string
  subject_id?: string | null
  occurred_at: string
}

export interface LiveDigestTeammateThoughtDto {
  handle: string
  display_name: string
  status: LiveDigestTeammateStatus
  current_task?: string | null
  thought: string
  updated_at: string
}

export interface LiveDigestToolCallSummaryDto {
  tool_call_id: string
  tool_name: string
  status: 'running' | 'succeeded' | 'failed' | (string & {})
  compact_summary: string
}

export interface LiveDigestTouchedFileDto {
  path: string
  added_lines: number
  removed_lines: number
}

export interface LiveDigestChapterDto {
  id: string
  index: number
  phase: LiveDigestPhase
  title: string
  narrative: string
  source: LiveDigestChapterSource
  raw_thought_excerpt?: string | null
  scout_note?: string | null
  tool_calls: LiveDigestToolCallSummaryDto[]
  touched_files: LiveDigestTouchedFileDto[]
  started_at: string
  updated_at: string
}

export interface LiveDigestSentinelAlertDto {
  id: string
  code: LiveDigestGovernanceSignalCode
  severity: 'warning' | 'critical' | (string & {})
  title: string
  message: string
  suggested_steer_prompt: string
  teammate_handle?: string | null
  created_at: string
}

export interface LiveDigestSnapshotDto {
  run_id: string
  session_id: string
  status: LiveDigestRunStatus
  mode: LiveDigestMode
  explainer_active: boolean
  explainer_model?: string | null
  seq: number
  headline: string
  current_phase: LiveDigestPhase
  context_waterline: LiveDigestContextWaterlineDto
  governance_signals: LiveDigestGovernanceSignalDto[]
  teammate_thoughts: LiveDigestTeammateThoughtDto[]
  chapters: LiveDigestChapterDto[]
  touched_files: LiveDigestTouchedFileDto[]
  sentinel_alerts: LiveDigestSentinelAlertDto[]
  updated_at: string
}

export interface LiveDigestDeltaDto {
  seq: number
  run_id: string
  session_id?: string
  status?: LiveDigestRunStatus
  mode?: LiveDigestMode
  explainer_active?: boolean
  explainer_model?: string | null
  headline?: string | null
  current_phase?: LiveDigestPhase | null
  context_waterline?: LiveDigestContextWaterlineDto | null
  governance_signal?: LiveDigestGovernanceSignalDto | null
  governance_signals?: LiveDigestGovernanceSignalDto[] | null
  teammate_thoughts?: LiveDigestTeammateThoughtDto[] | null
  chapter_upsert?: LiveDigestChapterDto | null
  chapters?: LiveDigestChapterDto[] | null
  touched_files?: LiveDigestTouchedFileDto[] | null
  sentinel_alert?: LiveDigestSentinelAlertDto | null
  sentinel_alerts?: LiveDigestSentinelAlertDto[] | null
  occurred_at?: string | null
  updated_at?: string | null
}

export type LiveDigestStreamEventName = 'snapshot' | 'l0_delta' | 'l1_narration' | 'done'

export interface LiveDigestStreamHandlers {
  onOpen?: () => void
  onSnapshot?: (snapshot: LiveDigestSnapshotDto) => void
  onL0Delta?: (delta: LiveDigestDeltaDto) => void
  onL1Narration?: (delta: LiveDigestDeltaDto) => void
  onDone?: (snapshot: LiveDigestSnapshotDto) => void
  onError?: (error: Error) => void
}

export interface LiveDigestStreamOptions {
  afterSeq?: number
  storageId?: string
  fetchImpl?: typeof fetch
  idleTimeoutMs?: number
  reconnectDelayMs?: number
  maxReconnectDelayMs?: number
}

export interface LiveDigestStreamHandle {
  close: () => void
  readonly closed: boolean
  readonly lastSeq: number
}

export const TERMINAL_LIVE_DIGEST_STATUSES: ReadonlySet<string> = new Set([
  'completed',
  'failed',
  'cancelled',
])

export const LIVE_DIGEST_STREAM_IDLE_TIMEOUT_MS = 45_000

export function resolveLiveDigestGatewayUrl(): string {
  if (typeof window !== 'undefined' && typeof window.tinadec?.gatewayUrl === 'function') {
    return window.tinadec.gatewayUrl()
  }
  return 'http://127.0.0.1:48730'
}

export function isTerminalLiveDigestStatus(status: string | null | undefined): boolean {
  if (!status) return false
  return TERMINAL_LIVE_DIGEST_STATUSES.has(status.trim().toLowerCase())
}

// ---------------------------------------------------------------------------
// Snapshot normalization & immutable delta folding
// ---------------------------------------------------------------------------

function emptyWaterline(): LiveDigestContextWaterlineDto {
  return {
    used_tokens: 0,
    max_tokens: 0,
    usage_percent: 0,
    compaction_count: 0,
    compaction_points: [],
  }
}

export function normalizeLiveDigestSnapshot(
  raw: Partial<LiveDigestSnapshotDto> & { run_id: string },
): LiveDigestSnapshotDto {
  const waterline = raw.context_waterline
  return {
    run_id: raw.run_id,
    session_id: raw.session_id ?? '',
    status: raw.status ?? 'running',
    mode: raw.mode ?? 'auto',
    explainer_active: Boolean(raw.explainer_active),
    explainer_model: raw.explainer_model ?? null,
    seq: typeof raw.seq === 'number' && Number.isFinite(raw.seq) ? raw.seq : 0,
    headline: raw.headline ?? '',
    current_phase: raw.current_phase ?? 'hypothesis',
    context_waterline: waterline
      ? {
          used_tokens: waterline.used_tokens ?? 0,
          max_tokens: waterline.max_tokens ?? 0,
          usage_percent: waterline.usage_percent ?? 0,
          compaction_count: waterline.compaction_count ?? (waterline.compaction_points?.length ?? 0),
          compaction_points: Array.isArray(waterline.compaction_points)
            ? [...waterline.compaction_points]
            : [],
        }
      : emptyWaterline(),
    governance_signals: Array.isArray(raw.governance_signals) ? [...raw.governance_signals] : [],
    teammate_thoughts: Array.isArray(raw.teammate_thoughts) ? [...raw.teammate_thoughts] : [],
    chapters: Array.isArray(raw.chapters)
      ? [...raw.chapters]
          .map((ch) => ({
            ...ch,
            tool_calls: Array.isArray(ch.tool_calls) ? [...ch.tool_calls] : [],
            touched_files: Array.isArray(ch.touched_files) ? [...ch.touched_files] : [],
          }))
          .sort((a, b) => a.index - b.index)
      : [],
    touched_files: Array.isArray(raw.touched_files) ? [...raw.touched_files] : [],
    sentinel_alerts: Array.isArray(raw.sentinel_alerts) ? [...raw.sentinel_alerts] : [],
    updated_at: raw.updated_at ?? new Date().toISOString(),
  }
}

function upsertByKey<T>(
  base: readonly T[],
  incoming: readonly T[],
  keyOf: (item: T) => string,
): T[] {
  if (incoming.length === 0) return [...base]
  const out = [...base]
  const indexByKey = new Map<string, number>()
  out.forEach((item, idx) => indexByKey.set(keyOf(item), idx))
  for (const item of incoming) {
    const key = keyOf(item)
    const existingIdx = indexByKey.get(key)
    if (existingIdx !== undefined) {
      out[existingIdx] = item
    } else {
      indexByKey.set(key, out.length)
      out.push(item)
    }
  }
  return out
}

function mergeChapters(
  base: readonly LiveDigestChapterDto[],
  incoming: readonly LiveDigestChapterDto[],
): LiveDigestChapterDto[] {
  if (incoming.length === 0) return [...base]
  const out = [...base]
  const indexById = new Map<string, number>()
  out.forEach((item, idx) => indexById.set(item.id, idx))
  for (const rawChapter of incoming) {
    const normalized: LiveDigestChapterDto = {
      ...rawChapter,
      tool_calls: Array.isArray(rawChapter.tool_calls) ? [...rawChapter.tool_calls] : [],
      touched_files: Array.isArray(rawChapter.touched_files) ? [...rawChapter.touched_files] : [],
    }
    const existingIdx = indexById.get(normalized.id)
    if (existingIdx !== undefined) {
      const prev = out[existingIdx]
      out[existingIdx] = {
        ...prev,
        ...normalized,
        tool_calls: normalized.tool_calls.length > 0 ? normalized.tool_calls : prev.tool_calls,
        touched_files: normalized.touched_files.length > 0 ? normalized.touched_files : prev.touched_files,
        raw_thought_excerpt: normalized.raw_thought_excerpt ?? prev.raw_thought_excerpt ?? null,
        scout_note: normalized.scout_note ?? prev.scout_note ?? null,
      }
    } else {
      indexById.set(normalized.id, out.length)
      out.push(normalized)
    }
  }
  return out.sort((a, b) => a.index - b.index)
}

export function applyLiveDigestDelta(
  current: LiveDigestSnapshotDto | null,
  delta: LiveDigestDeltaDto,
): LiveDigestSnapshotDto {
  const base = current ?? normalizeLiveDigestSnapshot({
    run_id: delta.run_id,
    session_id: delta.session_id ?? '',
    status: delta.status ?? 'running',
    mode: delta.mode ?? 'auto',
    explainer_active: Boolean(delta.explainer_active),
    explainer_model: delta.explainer_model ?? null,
    seq: 0,
    headline: delta.headline ?? '',
    current_phase: delta.current_phase ?? 'hypothesis',
    updated_at: delta.updated_at ?? delta.occurred_at ?? new Date().toISOString(),
  })

  const incomingSignals: LiveDigestGovernanceSignalDto[] = []
  if (delta.governance_signal) incomingSignals.push(delta.governance_signal)
  if (Array.isArray(delta.governance_signals)) incomingSignals.push(...delta.governance_signals)

  const incomingChapters: LiveDigestChapterDto[] = []
  if (delta.chapter_upsert) incomingChapters.push(delta.chapter_upsert)
  if (Array.isArray(delta.chapters)) incomingChapters.push(...delta.chapters)

  const incomingAlerts: LiveDigestSentinelAlertDto[] = []
  if (delta.sentinel_alert) incomingAlerts.push(delta.sentinel_alert)
  if (Array.isArray(delta.sentinel_alerts)) incomingAlerts.push(...delta.sentinel_alerts)

  const nextTeammates = Array.isArray(delta.teammate_thoughts) && delta.teammate_thoughts.length > 0
    ? upsertByKey(base.teammate_thoughts, delta.teammate_thoughts, (item) => item.handle)
    : base.teammate_thoughts

  const nextFiles = Array.isArray(delta.touched_files) && delta.touched_files.length > 0
    ? upsertByKey(base.touched_files, delta.touched_files, (item) => item.path)
    : base.touched_files

  const nextWaterline = delta.context_waterline
    ? {
        used_tokens: delta.context_waterline.used_tokens ?? base.context_waterline.used_tokens,
        max_tokens: delta.context_waterline.max_tokens ?? base.context_waterline.max_tokens,
        usage_percent: delta.context_waterline.usage_percent ?? base.context_waterline.usage_percent,
        compaction_count:
          delta.context_waterline.compaction_count
          ?? delta.context_waterline.compaction_points?.length
          ?? base.context_waterline.compaction_count,
        compaction_points: Array.isArray(delta.context_waterline.compaction_points)
          ? [...delta.context_waterline.compaction_points]
          : base.context_waterline.compaction_points,
      }
    : base.context_waterline

  return {
    ...base,
    run_id: delta.run_id || base.run_id,
    session_id: delta.session_id ?? base.session_id,
    status: delta.status ?? base.status,
    mode: delta.mode ?? base.mode,
    explainer_active:
      typeof delta.explainer_active === 'boolean' ? delta.explainer_active : base.explainer_active,
    explainer_model:
      delta.explainer_model !== undefined ? delta.explainer_model : base.explainer_model,
    seq: Math.max(base.seq, typeof delta.seq === 'number' ? delta.seq : base.seq),
    headline:
      typeof delta.headline === 'string' && delta.headline.trim().length > 0
        ? delta.headline
        : base.headline,
    current_phase: delta.current_phase ?? base.current_phase,
    context_waterline: nextWaterline,
    governance_signals: upsertByKey(base.governance_signals, incomingSignals, (item) => item.id),
    teammate_thoughts: nextTeammates,
    chapters: mergeChapters(base.chapters, incomingChapters),
    touched_files: nextFiles,
    sentinel_alerts: upsertByKey(base.sentinel_alerts, incomingAlerts, (item) => item.id),
    updated_at: delta.updated_at ?? delta.occurred_at ?? base.updated_at,
  }
}

// ---------------------------------------------------------------------------
// HTTP Helpers (reuses desktop storageScope & backendRequest without touching api.ts)
// ---------------------------------------------------------------------------

async function requestJson<T>(path: string, init?: StorageRequestOptions): Promise<T> {
  const gatewayUrl = resolveLiveDigestGatewayUrl()
  const storageId = captureStorageId(path, init)
  const normalized = normalizeStorageRequest(path, init)
  const headers = storageHeaders(path, { ...init, storageId })
  headers.set('accept', 'application/json')
  if (normalized.body && !headers.has('content-type')) {
    headers.set('content-type', 'application/json')
  }
  const { response, text } = await readBackendResponse(
    `${gatewayUrl}${normalized.path}`,
    path,
    { ...init, body: normalized.body, headers },
    { storageId },
  )
  let data: unknown = null
  if (text.length > 0) {
    try {
      data = JSON.parse(text)
    } catch {
      throw new Error(i18n.global.t('errors.invalidServerResponse', { text: text.substring(0, 200) }))
    }
  }
  if (!response.ok) {
    throw new ApiError(apiErrorMessage(data, response.statusText), response.status, data, { storageId })
  }
  return data as T
}

export async function getLiveDigestSettings(
  options?: StorageRequestOptions,
): Promise<LiveDigestSettingsDto> {
  return requestJson<LiveDigestSettingsDto>('/api/v1/live-digest/settings', {
    storageId: 'user',
    ...options,
    method: 'GET',
  })
}

export async function updateLiveDigestSettings(
  input: UpdateLiveDigestSettingsInput,
  options?: StorageRequestOptions,
): Promise<LiveDigestSettingsDto> {
  return requestJson<LiveDigestSettingsDto>('/api/v1/live-digest/settings', {
    storageId: 'user',
    ...options,
    method: 'PUT',
    body: JSON.stringify(input),
  })
}

export async function getRunLiveDigest(
  runId: string,
  options?: StorageRequestOptions,
): Promise<LiveDigestSnapshotDto | null> {
  const identity = selectionIdentity(runId)
  const storageId = options?.storageId ?? identity.storageId ?? runStorageId(runId)
  try {
    const raw = await requestJson<LiveDigestSnapshotDto>(
      `/api/v1/runs/${encodeURIComponent(identity.id)}/live-digest`,
      { ...options, storageId, method: 'GET' },
    )
    return raw ? normalizeLiveDigestSnapshot(raw) : null
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null
    throw error
  }
}

// ---------------------------------------------------------------------------
// Resumable Fetch-based SSE Client for `/api/v1/runs/{runId}/live-digest/stream`
// ---------------------------------------------------------------------------

export function openLiveDigestStream(
  runId: string,
  handlers: LiveDigestStreamHandlers,
  options: LiveDigestStreamOptions = {},
): LiveDigestStreamHandle {
  const identity = selectionIdentity(runId)
  const storageId = options.storageId ?? identity.storageId ?? runStorageId(runId)
  const fetchImpl = options.fetchImpl ?? fetch
  const idleTimeoutMs = options.idleTimeoutMs ?? LIVE_DIGEST_STREAM_IDLE_TIMEOUT_MS
  const baseReconnectDelayMs = options.reconnectDelayMs ?? 1_000
  const maxReconnectDelayMs = options.maxReconnectDelayMs ?? 8_000

  let closed = false
  let finishedByDone = false
  let lastSeq = Math.max(0, options.afterSeq ?? 0)
  let reconnectAttempt = 0
  let abortController: AbortController | null = null
  let retryTimer: ReturnType<typeof setTimeout> | null = null

  function close(): void {
    closed = true
    if (retryTimer !== null) {
      clearTimeout(retryTimer)
      retryTimer = null
    }
    abortController?.abort()
    abortController = null
  }

  function dispatchBlock(block: string): void {
    let eventName = 'message'
    const dataLines: string[] = []
    let frameId = ''
    for (const line of block.replace(/\r/g, '').split('\n')) {
      if (!line || line.startsWith(':')) continue
      const sep = line.indexOf(':')
      const key = sep < 0 ? line : line.slice(0, sep)
      const value = sep < 0 ? '' : line.slice(sep + 1).replace(/^ /, '')
      if (key === 'event') eventName = value || 'message'
      else if (key === 'id') frameId = value
      else if (key === 'data') dataLines.push(value)
    }

    if (frameId && /^\d+$/.test(frameId)) {
      const parsedSeq = Number.parseInt(frameId, 10)
      if (Number.isFinite(parsedSeq) && parsedSeq > lastSeq) {
        lastSeq = parsedSeq
      }
    }

    if (dataLines.length === 0) return
    let parsed: unknown
    try {
      parsed = JSON.parse(dataLines.join('\n'))
    } catch {
      return
    }

    if (parsed && typeof parsed === 'object' && 'seq' in parsed) {
      const payloadSeq = (parsed as { seq?: unknown }).seq
      if (typeof payloadSeq === 'number' && Number.isFinite(payloadSeq) && payloadSeq > lastSeq) {
        lastSeq = payloadSeq
      }
    }

    if (eventName === 'snapshot') {
      handlers.onSnapshot?.(normalizeLiveDigestSnapshot(parsed as LiveDigestSnapshotDto))
    } else if (eventName === 'l0_delta') {
      handlers.onL0Delta?.(parsed as LiveDigestDeltaDto)
    } else if (eventName === 'l1_narration') {
      handlers.onL1Narration?.(parsed as LiveDigestDeltaDto)
    } else if (eventName === 'done') {
      finishedByDone = true
      handlers.onDone?.(normalizeLiveDigestSnapshot(parsed as LiveDigestSnapshotDto))
      close()
    }
  }

  async function connect(): Promise<void> {
    if (closed) return
    const controller = new AbortController()
    abortController = controller
    const gatewayUrl = resolveLiveDigestGatewayUrl()
    const basePath = `/api/v1/runs/${encodeURIComponent(identity.id)}/live-digest/stream`
    const query = lastSeq > 0 ? `?after_seq=${lastSeq}` : ''
    const pathWithQuery = `${basePath}${query}`
    const fullUrl = `${gatewayUrl}${pathWithQuery}`

    try {
      await assertHostAccess(pathWithQuery, controller.signal)
      if (closed || controller.signal.aborted) return

      const headers: Record<string, string> = {
        accept: 'text/event-stream',
        'x-tinadec-storage-id': storageId,
      }
      if (lastSeq > 0) {
        headers['last-event-id'] = String(lastSeq)
      }

      const response = await fetchImpl(fullUrl, {
        method: 'GET',
        headers,
        signal: controller.signal,
      })

      if (closed || controller.signal.aborted) {
        await response.body?.cancel().catch(() => undefined)
        return
      }
      if (response.status === 404) {
        // Run does not exist or has no live digest endpoint yet; stop retrying aggressively.
        close()
        return
      }
      if (!response.ok || !response.body) {
        throw new Error(`Live digest stream returned ${response.status}`)
      }

      reconnectAttempt = 0
      handlers.onOpen?.()

      await consumeSseResponseBody(response.body, {
        signal: controller.signal,
        idleTimeoutMs,
        isClosed: () => closed,
        onBlock: (block) => dispatchBlock(block),
      })
    } catch (error) {
      if (!closed && !finishedByDone) {
        handlers.onError?.(error instanceof Error ? error : new Error(String(error)))
      }
    }

    if (!closed && !finishedByDone) {
      const delay = Math.min(baseReconnectDelayMs * Math.pow(2, reconnectAttempt), maxReconnectDelayMs)
      reconnectAttempt += 1
      retryTimer = setTimeout(() => {
        retryTimer = null
        void connect()
      }, delay)
    }
  }

  void connect()

  return {
    close,
    get closed() {
      return closed
    },
    get lastSeq() {
      return lastSeq
    },
  }
}

export const liveDigestApi = {
  getSettings: getLiveDigestSettings,
  updateSettings: updateLiveDigestSettings,
  getRunLiveDigest,
  openLiveDigestStream,
}
