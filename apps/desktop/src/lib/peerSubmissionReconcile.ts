import { computed, ref, type ComputedRef, type Ref } from 'vue'
import {
  api,
  type EndpointId,
  type EndpointInput,
  type PeerIngressStatusDto,
} from '@/api'
import { isSubmissionUncertainError } from '@/lib/apiError'
import {
  newClientRequestId,
  normalizeEndpointScope,
  peerDeviceIdOf,
  readEndpointEventCursor,
  toEndpointId,
  withEndpointScope,
  writeEndpointEventCursor,
} from '@/lib/endpointScope'
import { scopedApi } from '@/lib/storageScope'

export type PeerSubmissionStatus = 'uncertain' | 'accepted' | 'retry_ready'

export interface PeerUncertainSubmissionRecord {
  endpointId: EndpointId
  deviceId: string
  sessionId: string
  storageId?: string
  clientRequestId: string
  text: string
  stagedAttachmentIds: string[]
  createdAt: number
  updatedAt: number
  status: PeerSubmissionStatus
  remoteStage: 'received' | 'processing' | 'processed' | 'rejected' | null
  runId: string | null
  turnId: string | null
  messageId: string | null
  afterSeq: number | null
  rejectErrorCode: string | null
  reconciling: boolean
  resending: boolean
  lastCheckedAt: number | null
  lastError: string | null
}

export interface RecordPeerSubmissionDraftInput {
  endpoint: EndpointInput
  sessionId: string
  storageId?: string
  clientRequestId: string
  text: string
  stagedAttachmentIds?: readonly string[]
  createdAt?: number
}

export interface ReconcileSubmissionResult {
  outcome: 'accepted' | 'retry_ready' | 'still_uncertain' | 'not_tracked'
  record: PeerUncertainSubmissionRecord | null
  afterSeq: number | null
  ingress: PeerIngressStatusDto | null
}

export interface ReconcileSubmissionOptions {
  checkIngress?: (
    sessionId: string,
    clientRequestId: string,
    options: { endpointId: EndpointId; storageId?: string },
  ) => Promise<PeerIngressStatusDto>
  onAccepted?: (payload: {
    record: PeerUncertainSubmissionRecord
    ingress: PeerIngressStatusDto
    afterSeq: number
  }) => void | Promise<void>
  streamKey?: string
}

export interface ResendUncertainSubmissionOptions {
  /**
   * When true, reuses the original `clientRequestId` (safe when `found: false`).
   * Defaults to true when `remoteStage !== 'rejected'`, and false when `remoteStage === 'rejected'`
   * so a previously rejected request ID does not collide with a rejected row on the peer.
   */
  reuseClientRequestId?: boolean
  send?: (payload: {
    endpointId: EndpointId
    deviceId: string
    sessionId: string
    storageId?: string
    clientRequestId: string
    text: string
    stagedAttachmentIds: string[]
  }) => Promise<unknown>
}

type AcceptedListener = (payload: {
  record: PeerUncertainSubmissionRecord
  ingress: PeerIngressStatusDto
  afterSeq: number
}) => void

const uncertainRecords = ref<PeerUncertainSubmissionRecord[]>([])
const inFlightDrafts = new Map<string, PeerUncertainSubmissionRecord>()
const acceptedListeners = new Set<AcceptedListener>()

function recordKey(endpointId: EndpointId, sessionId: string, clientRequestId: string): string {
  return `${endpointId}::${sessionId}::${clientRequestId}`
}

function buildDraftRecord(input: RecordPeerSubmissionDraftInput): PeerUncertainSubmissionRecord | null {
  const scope = normalizeEndpointScope(input.endpoint)
  if (scope.kind !== 'peer') return null
  const endpointId = toEndpointId(scope)
  const now = input.createdAt ?? Date.now()
  return {
    endpointId,
    deviceId: scope.deviceId,
    sessionId: input.sessionId,
    ...(input.storageId ? { storageId: input.storageId } : {}),
    clientRequestId: input.clientRequestId,
    text: input.text,
    stagedAttachmentIds: [...(input.stagedAttachmentIds ?? [])],
    createdAt: now,
    updatedAt: now,
    status: 'uncertain',
    remoteStage: null,
    runId: null,
    turnId: null,
    messageId: null,
    afterSeq: null,
    rejectErrorCode: null,
    reconciling: false,
    resending: false,
    lastCheckedAt: null,
    lastError: null,
  }
}

function upsertRecord(next: PeerUncertainSubmissionRecord): PeerUncertainSubmissionRecord {
  const key = recordKey(next.endpointId, next.sessionId, next.clientRequestId)
  const idx = uncertainRecords.value.findIndex(
    (item) => recordKey(item.endpointId, item.sessionId, item.clientRequestId) === key,
  )
  if (idx >= 0) {
    const copy = [...uncertainRecords.value]
    copy[idx] = next
    uncertainRecords.value = copy
  } else {
    uncertainRecords.value = [...uncertainRecords.value, next]
  }
  return next
}

function patchRecord(
  endpointId: EndpointId,
  sessionId: string,
  clientRequestId: string,
  changes: Partial<PeerUncertainSubmissionRecord>,
): PeerUncertainSubmissionRecord | null {
  const key = recordKey(endpointId, sessionId, clientRequestId)
  let updated: PeerUncertainSubmissionRecord | null = null
  uncertainRecords.value = uncertainRecords.value.map((item) => {
    if (recordKey(item.endpointId, item.sessionId, item.clientRequestId) !== key) return item
    updated = { ...item, ...changes, updatedAt: Date.now() }
    return updated
  })
  return updated
}

/**
 * Saves an in-flight draft snapshot right before a peer message submission leaves the client.
 * Never surfaces in UI unless the transport drops with `submission_uncertain`.
 */
export function beginPeerSubmissionDraft(input: RecordPeerSubmissionDraftInput): PeerUncertainSubmissionRecord | null {
  const draft = buildDraftRecord(input)
  if (!draft) return null
  inFlightDrafts.set(recordKey(draft.endpointId, draft.sessionId, draft.clientRequestId), draft)
  return draft
}

/**
 * Clears an in-flight draft once a deterministic response (2xx or deterministic 4xx refusal) arrives.
 */
export function clearInFlightPeerSubmissionDraft(
  endpoint: EndpointInput,
  sessionId: string,
  clientRequestId: string,
): void {
  const scope = normalizeEndpointScope(endpoint)
  if (scope.kind !== 'peer') return
  inFlightDrafts.delete(recordKey(toEndpointId(scope), sessionId, clientRequestId))
}

/**
 * Promotes a peer submission into `status: 'uncertain'` upon network drop / timeout (`submission_uncertain`).
 * Strictly NEVER triggers automatic blind resend.
 */
export function markPeerSubmissionUncertain(
  input: RecordPeerSubmissionDraftInput,
  errorMessage?: string | null,
): PeerUncertainSubmissionRecord | null {
  const scope = normalizeEndpointScope(input.endpoint)
  if (scope.kind !== 'peer') return null
  const endpointId = toEndpointId(scope)
  const key = recordKey(endpointId, input.sessionId, input.clientRequestId)
  const inFlight = inFlightDrafts.get(key)
  inFlightDrafts.delete(key)

  const existing = uncertainRecords.value.find(
    (item) => recordKey(item.endpointId, item.sessionId, item.clientRequestId) === key,
  )
  const base = existing ?? inFlight ?? buildDraftRecord(input)
  if (!base) return null

  return upsertRecord({
    ...base,
    text: input.text || base.text,
    stagedAttachmentIds:
      input.stagedAttachmentIds && input.stagedAttachmentIds.length > 0
        ? [...input.stagedAttachmentIds]
        : base.stagedAttachmentIds,
    status: 'uncertain',
    reconciling: false,
    resending: false,
    lastError: errorMessage ?? base.lastError,
    updatedAt: Date.now(),
  })
}

export function dismissUncertainSubmission(
  clientRequestId: string,
  options?: { endpoint?: EndpointInput; sessionId?: string },
): void {
  const targetEndpointId = options?.endpoint ? toEndpointId(options.endpoint) : null
  uncertainRecords.value = uncertainRecords.value.filter((item) => {
    if (item.clientRequestId !== clientRequestId) return true
    if (targetEndpointId && item.endpointId !== targetEndpointId) return true
    if (options?.sessionId && item.sessionId !== options.sessionId) return true
    return false
  })
}

export function clearUncertainSubmissionsForDevice(deviceId: string): void {
  uncertainRecords.value = uncertainRecords.value.filter((item) => item.deviceId !== deviceId)
  for (const [key, draft] of [...inFlightDrafts.entries()]) {
    if (draft.deviceId === deviceId) inFlightDrafts.delete(key)
  }
}

export function onPeerSubmissionAccepted(listener: AcceptedListener): () => void {
  acceptedListeners.add(listener)
  return () => {
    acceptedListeners.delete(listener)
  }
}

function advanceEventCursorAfterAccept(
  record: PeerUncertainSubmissionRecord,
  ingressSeq: number | null | undefined,
  explicitStreamKey?: string,
): number {
  const storageId = record.storageId || 'user'
  const streamKey = explicitStreamKey ?? `/api/v1/events?session_id=${record.sessionId}`
  const existing = readEndpointEventCursor(record.endpointId, storageId, streamKey)
  const previousSeq = existing?.afterSeq ?? 0
  const resolvedSeq =
    typeof ingressSeq === 'number' && Number.isFinite(ingressSeq) && ingressSeq >= 0
      ? Math.max(previousSeq, ingressSeq)
      : previousSeq

  writeEndpointEventCursor(record.endpointId, storageId, streamKey, {
    watermarks: existing?.watermarks ?? {},
    lastFrameId: resolvedSeq > 0 ? String(resolvedSeq) : (existing?.lastFrameId ?? ''),
    afterSeq: resolvedSeq,
  })
  return resolvedSeq
}

/**
 * Queries `GET /api/v1/sessions/:sessionId/ingress/:clientRequestId` via the peer proxy to reconcile
 * an uncertain submission without ever resending bytes:
 * - `received` | `processing` | `processed`: transitions to `accepted`, removes the uncertain notice,
 *   and advances `after_seq` to resume stream catch-up.
 * - `found: false` | `rejected`: transitions to `retry_ready` so the user may explicitly resend.
 * - Network / offline errors: stays `uncertain` with resend still locked.
 */
export async function reconcilePeerSubmission(
  clientRequestId: string,
  options: ReconcileSubmissionOptions & { endpoint?: EndpointInput; sessionId?: string } = {},
): Promise<ReconcileSubmissionResult> {
  const targetEndpointId = options.endpoint ? toEndpointId(options.endpoint) : null
  const record = uncertainRecords.value.find((item) => {
    if (item.clientRequestId !== clientRequestId) return false
    if (targetEndpointId && item.endpointId !== targetEndpointId) return false
    if (options.sessionId && item.sessionId !== options.sessionId) return false
    return true
  })

  if (!record) {
    return { outcome: 'not_tracked', record: null, afterSeq: null, ingress: null }
  }

  patchRecord(record.endpointId, record.sessionId, record.clientRequestId, {
    reconciling: true,
    lastError: null,
  })

  const checkFn =
    options.checkIngress ??
    ((sid, reqId, opts) => {
      const streamKey = options.streamKey ?? `/api/v1/events?session_id=${sid}`
      const cursor = readEndpointEventCursor(opts.endpointId, opts.storageId || 'user', streamKey)
      return api.getPeerSubmissionIngressStatus(sid, reqId, {
        endpointId: opts.endpointId,
        storageId: opts.storageId,
        ...(cursor?.afterSeq != null && cursor.afterSeq > 0 ? { afterSeq: cursor.afterSeq } : {}),
      })
    })

  try {
    const ingress = await checkFn(record.sessionId, record.clientRequestId, {
      endpointId: record.endpointId,
      storageId: record.storageId,
    })

    const stage = ingress.stage
    if (ingress.found && (stage === 'processed' || stage === 'processing' || stage === 'received')) {
      const afterSeq = advanceEventCursorAfterAccept(record, ingress.last_event_seq, options.streamKey)
      const acceptedRecord: PeerUncertainSubmissionRecord = {
        ...record,
        status: 'accepted',
        remoteStage: stage,
        runId: ingress.run_id ?? null,
        turnId: ingress.turn_id ?? null,
        messageId: ingress.message_id ?? null,
        afterSeq,
        reconciling: false,
        lastCheckedAt: Date.now(),
        lastError: null,
        updatedAt: Date.now(),
      }
      // Remove from uncertain notices once accepted by the remote worker
      dismissUncertainSubmission(record.clientRequestId, {
        endpoint: record.endpointId,
        sessionId: record.sessionId,
      })
      for (const listener of acceptedListeners) {
        try {
          listener({ record: acceptedRecord, ingress, afterSeq })
        } catch {
          // Listener errors must not break reconciliation.
        }
      }
      await options.onAccepted?.({ record: acceptedRecord, ingress, afterSeq })
      return { outcome: 'accepted', record: acceptedRecord, afterSeq, ingress }
    }

    if (!ingress.found || stage === 'rejected') {
      const retryRecord = patchRecord(record.endpointId, record.sessionId, record.clientRequestId, {
        status: 'retry_ready',
        remoteStage: stage === 'rejected' ? 'rejected' : null,
        rejectErrorCode: ingress.reject_error_code ?? null,
        reconciling: false,
        lastCheckedAt: Date.now(),
        lastError: null,
      })
      return { outcome: 'retry_ready', record: retryRecord, afterSeq: null, ingress }
    }

    const unchanged = patchRecord(record.endpointId, record.sessionId, record.clientRequestId, {
      reconciling: false,
      lastCheckedAt: Date.now(),
    })
    return { outcome: 'still_uncertain', record: unchanged, afterSeq: null, ingress }
  } catch (caught) {
    const errMsg = caught instanceof Error ? caught.message : String(caught)
    const stillUncertain = patchRecord(record.endpointId, record.sessionId, record.clientRequestId, {
      status: 'uncertain',
      reconciling: false,
      lastCheckedAt: Date.now(),
      lastError: errMsg,
    })
    return { outcome: 'still_uncertain', record: stillUncertain, afterSeq: null, ingress: null }
  }
}

/**
 * Reconciles every pending `uncertain` submission for a given peer device (e.g. when connection returns to `ready`).
 */
export async function reconcileDeviceUncertainSubmissions(
  deviceId: string,
  options: ReconcileSubmissionOptions = {},
): Promise<ReconcileSubmissionResult[]> {
  const targets = uncertainRecords.value.filter(
    (item) => item.deviceId === deviceId && item.status === 'uncertain' && !item.reconciling,
  )
  const results: ReconcileSubmissionResult[] = []
  for (const item of targets) {
    results.push(
      await reconcilePeerSubmission(item.clientRequestId, {
        ...options,
        endpoint: item.endpointId,
        sessionId: item.sessionId,
      }),
    )
  }
  return results
}

/**
 * User-initiated explicit resend. Strictly refuses execution unless reconciliation has confirmed
 * `status === 'retry_ready'` (`found: false` or `rejected`).
 */
export async function resendUncertainSubmission(
  clientRequestId: string,
  options: ResendUncertainSubmissionOptions & { endpoint?: EndpointInput; sessionId?: string } = {},
): Promise<unknown> {
  const targetEndpointId = options.endpoint ? toEndpointId(options.endpoint) : null
  const record = uncertainRecords.value.find((item) => {
    if (item.clientRequestId !== clientRequestId) return false
    if (targetEndpointId && item.endpointId !== targetEndpointId) return false
    if (options.sessionId && item.sessionId !== options.sessionId) return false
    return true
  })

  if (!record) {
    throw new Error(`Uncertain submission "${clientRequestId}" was not found.`)
  }
  if (record.status !== 'retry_ready') {
    throw new Error(
      `Cannot resend submission "${clientRequestId}" while status is "${record.status}". Reconcile ingress status first.`,
    )
  }

  const reuseId = options.reuseClientRequestId ?? record.remoteStage !== 'rejected'
  const nextRequestId = reuseId ? record.clientRequestId : newClientRequestId()

  patchRecord(record.endpointId, record.sessionId, record.clientRequestId, {
    resending: true,
    lastError: null,
  })

  const sendFn =
    options.send ??
    (async (payload) => {
      const bound = payload.storageId ? scopedApi(api, () => payload.storageId!) : api
      return withEndpointScope(payload.endpointId, () =>
        bound.createInteraction(payload.sessionId, {
          content: payload.text,
          client_message_id: payload.clientRequestId,
          dispatch_mode: 'queued',
          ...(payload.stagedAttachmentIds.length > 0
            ? {
                attachment_ids: payload.stagedAttachmentIds,
                staged_attachment_ids: payload.stagedAttachmentIds,
              }
            : {}),
        }),
      )
    })

  try {
    const response = await sendFn({
      endpointId: record.endpointId,
      deviceId: record.deviceId,
      sessionId: record.sessionId,
      storageId: record.storageId,
      clientRequestId: nextRequestId,
      text: record.text,
      stagedAttachmentIds: [...record.stagedAttachmentIds],
    })
    dismissUncertainSubmission(record.clientRequestId, {
      endpoint: record.endpointId,
      sessionId: record.sessionId,
    })
    if (nextRequestId !== record.clientRequestId) {
      dismissUncertainSubmission(nextRequestId, {
        endpoint: record.endpointId,
        sessionId: record.sessionId,
      })
    }
    return response
  } catch (caught) {
    if (isSubmissionUncertainError(caught)) {
      if (nextRequestId !== record.clientRequestId) {
        dismissUncertainSubmission(record.clientRequestId, {
          endpoint: record.endpointId,
          sessionId: record.sessionId,
        })
      }
      markPeerSubmissionUncertain(
        {
          endpoint: record.endpointId,
          sessionId: record.sessionId,
          storageId: record.storageId,
          clientRequestId: nextRequestId,
          text: record.text,
          stagedAttachmentIds: record.stagedAttachmentIds,
        },
        caught.message,
      )
    } else {
      patchRecord(record.endpointId, record.sessionId, record.clientRequestId, {
        resending: false,
        lastError: caught instanceof Error ? caught.message : String(caught),
      })
    }
    throw caught
  }
}

export function usePeerSubmissionReconcile(options?: {
  endpoint?: () => EndpointInput
  sessionId?: () => string | null | undefined
}): {
  records: Ref<PeerUncertainSubmissionRecord[]>
  sessionRecords: ComputedRef<PeerUncertainSubmissionRecord[]>
  reconcile: (clientRequestId: string, opts?: ReconcileSubmissionOptions) => Promise<ReconcileSubmissionResult>
  reconcileDevice: (deviceId: string, opts?: ReconcileSubmissionOptions) => Promise<ReconcileSubmissionResult[]>
  resend: (clientRequestId: string, opts?: ResendUncertainSubmissionOptions) => Promise<unknown>
  dismiss: (clientRequestId: string) => void
} {
  const sessionRecords = computed<PeerUncertainSubmissionRecord[]>(() => {
    const endpointInput = options?.endpoint ? options.endpoint() : undefined
    const deviceId = endpointInput !== undefined ? peerDeviceIdOf(endpointInput) : undefined
    const sessionId = options?.sessionId ? options.sessionId() : undefined

    return uncertainRecords.value.filter((item) => {
      if (deviceId !== undefined && item.deviceId !== deviceId) return false
      if (sessionId !== undefined && sessionId !== null && item.sessionId !== sessionId) return false
      return item.status === 'uncertain' || item.status === 'retry_ready'
    })
  })

  return {
    records: uncertainRecords,
    sessionRecords,
    reconcile: (clientRequestId, opts) =>
      reconcilePeerSubmission(clientRequestId, {
        ...opts,
        ...(options?.endpoint ? { endpoint: options.endpoint() } : {}),
        ...(options?.sessionId?.() ? { sessionId: options.sessionId()! } : {}),
      }),
    reconcileDevice: reconcileDeviceUncertainSubmissions,
    resend: (clientRequestId, opts) =>
      resendUncertainSubmission(clientRequestId, {
        ...opts,
        ...(options?.endpoint ? { endpoint: options.endpoint() } : {}),
        ...(options?.sessionId?.() ? { sessionId: options.sessionId()! } : {}),
      }),
    dismiss: (clientRequestId) =>
      dismissUncertainSubmission(clientRequestId, {
        ...(options?.endpoint ? { endpoint: options.endpoint() } : {}),
        ...(options?.sessionId?.() ? { sessionId: options.sessionId()! } : {}),
      }),
  }
}

export function resetPeerSubmissionReconcileForTests(): void {
  uncertainRecords.value = []
  inFlightDrafts.clear()
  acceptedListeners.clear()
}
