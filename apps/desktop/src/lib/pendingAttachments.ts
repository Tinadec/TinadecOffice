import { computed, ref } from 'vue'
import { api } from '@/api'
import type { MessageAttachmentDto, MessageAttachmentSummaryDto } from '@/generated/client'

/**
 * Mirror of Core's `AttachmentEndpoints.MaxAttachmentBytes`. `pendingAttachments.test.ts`
 * reads the C# source and fails if the two drift, which is what makes the local
 * pre-check below honest instead of a guess that can outlive the server's ceiling.
 */
export const MAX_ATTACHMENT_BYTES = 32 * 1024 * 1024

/** Same machine code Core answers with, so the UI needs one failure branch, not two. */
export const TOO_LARGE_CODE = 'attachment_too_large'

export type PendingAttachmentStatus = 'uploading' | 'ready' | 'failed'

export interface PendingAttachment {
  readonly clientId: string
  readonly fileName: string
  readonly size: number
  readonly mediaType: string
  status: PendingAttachmentStatus
  /** Core row id; null until the upload is accepted. */
  attachmentId: string | null
  errorCode: string | null
  /**
   * The row Core answered with, kept so an optimistic message bubble can show the
   * same projection a reload would. Null while uploading or after a failure.
   */
  storedRow: MessageAttachmentDto | null
}

/**
 * The part of `File` this module actually uses. Naming it keeps the caller free to
 * hand over a dragged item or a clipboard blob, and lets the tests build a selection
 * without a DOM File implementation whose arrayBuffer support varies by environment.
 */
export interface AttachableFile {
  readonly name: string
  readonly type: string
  readonly size: number
  arrayBuffer(): Promise<ArrayBuffer>
}

function newClientId(): string {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

function machineCode(err: unknown): string | null {
  const code = (err as { code?: unknown })?.code
  return typeof code === 'string' ? code : null
}

const pending = ref<PendingAttachment[]>([])
// Plain let, not a ref: only reconcileSession reads it, and making it reactive would
// invite a template to render an internal bookkeeping value.
let ownerSessionId: string | null = null

export const pendingAttachments = computed(() => pending.value)

function patch(clientId: string, changes: Partial<PendingAttachment>) {
  pending.value = pending.value.map((item) =>
    item.clientId === clientId ? { ...item, ...changes } : item,
  )
}

/**
 * Called with the session the composer is now attached to. Every row is scoped to the
 * session it was uploaded into, so a session change invalidates the whole strip: chips
 * are dropped and the rows Core already accepted are deleted best-effort, rather than
 * lingering as attachments no message will ever reference.
 *
 * The state is module-level (it survives a composer unmount across route changes), so
 * the guard has to compare owners instead of trusting that a watcher saw the switch —
 * the session can change while nothing is mounted to observe it.
 */
export function reconcileSession(sessionId: string | null) {
  if (ownerSessionId === sessionId) return
  const uploaded = pending.value
    .filter((item) => item.attachmentId)
    .map((item) => item.attachmentId as string)
  ownerSessionId = sessionId
  pending.value = []
  for (const id of uploaded) void deleteQuietly(id)
}

/**
 * Upload a picked selection into `sessionId`. Returns once every chip has settled;
 * each one flips from `uploading` to `ready` or `failed` as its own response lands,
 * so a slow 20 MB file never blocks a 2 KB one from showing its state.
 *
 * Without a session there is nothing to upload against: Core scopes an attachment to
 * `(tenant, workspace, session)` at write time, and a deferred upload would have to
 * keep the bytes alive across a session change. The composer disables the entry point
 * instead of silently discarding a pick.
 */
export function attachFiles(
  files: readonly AttachableFile[],
  sessionId: string | null,
): Promise<void> {
  if (!sessionId) return Promise.resolve()
  reconcileSession(sessionId)
  const uploads = files.map((file) => {
    const clientId = newClientId()
    pending.value = [
      ...pending.value,
      {
        clientId,
        fileName: file.name,
        size: file.size,
        mediaType: file.type || 'application/octet-stream',
        status: 'uploading',
        attachmentId: null,
        errorCode: null,
        storedRow: null,
      },
    ]
    return uploadOne(clientId, file, sessionId)
  })
  return Promise.all(uploads).then(() => undefined)
}

async function uploadOne(clientId: string, file: AttachableFile, sessionId: string) {
  // File.size is known before a byte crosses the wire: reject locally rather than
  // making the user watch a 40 MB upload fail at the end.
  if (file.size > MAX_ATTACHMENT_BYTES) {
    patch(clientId, { status: 'failed', errorCode: TOO_LARGE_CODE })
    return
  }
  let stored: MessageAttachmentDto
  try {
    stored = await api.uploadAttachment(
      sessionId,
      new Uint8Array(await file.arrayBuffer()),
      file.name,
      file.type || undefined,
    )
  } catch (err) {
    patch(clientId, { status: 'failed', errorCode: machineCode(err) })
    return
  }
  // The chip can be gone before the response lands: the user removed it, sent the
  // message, or switched session. Drop the row Core just created rather than leaking
  // an attachment nothing references.
  if (!pending.value.some((item) => item.clientId === clientId)) {
    await deleteQuietly(stored.id)
    return
  }
  patch(clientId, { status: 'ready', attachmentId: stored.id, errorCode: null, storedRow: stored })
}

async function deleteQuietly(attachmentId: string) {
  try {
    await api.deleteAttachment(attachmentId)
  } catch {
    // A failed cleanup of a row no message references must never surface as a send
    // error or block the composer. The residual row stays invisible until a future
    // GC pass; this is a known, reported gap rather than a silent one.
  }
}

/** Remove one chip, deleting its Core row when the upload had already landed. */
export function removePendingAttachment(clientId: string): Promise<void> {
  const target = pending.value.find((item) => item.clientId === clientId)
  if (!target) return Promise.resolve()
  pending.value = pending.value.filter((item) => item.clientId !== clientId)
  return target.attachmentId ? deleteQuietly(target.attachmentId) : Promise.resolve()
}

/** What one send takes with it: chips to clear, ids to send, rows to paint optimistically. */
export interface OutgoingAttachments {
  readonly clientIds: string[]
  readonly attachmentIds: string[]
  readonly summaries: MessageAttachmentSummaryDto[]
}

/**
 * The ready rows, as a snapshot taken before the request goes out. Deliberately not a
 * mutation: a send can fail, and a chip that quietly vanished would leave the user with
 * nothing to retry. Only settleSentAttachments clears, and only after Core has answered.
 */
export function attachmentsForSend(): OutgoingAttachments {
  const ready = pending.value.filter(
    (item): item is PendingAttachment & { storedRow: MessageAttachmentDto } =>
      item.status === 'ready' && item.storedRow !== null,
  )
  return {
    clientIds: ready.map((item) => item.clientId),
    attachmentIds: ready.map((item) => item.storedRow.id),
    summaries: ready.map((item) => {
      const row = item.storedRow
      return {
        id: row.id,
        file_name: row.file_name,
        media_type: row.media_type,
        content_hash: row.content_hash,
        content_length: row.content_length,
        created_at: row.created_at,
        bound_at: row.bound_at,
      }
    }),
  }
}

/**
 * Drop the chips a send carried away. No DELETE call here, unlike the other two
 * removals: Core has just bound these rows to the appended message, so the transcript
 * needs them to stay alive.
 */
export function settleSentAttachments(outgoing: OutgoingAttachments): void {
  if (outgoing.clientIds.length === 0) return
  pending.value = pending.value.filter((item) => !outgoing.clientIds.includes(item.clientId))
}

const UNITS = ['B', 'KB', 'MB', 'GB']

/** Compact, locale-free size label for a chip; 0 bytes stays visible as '0 B'. */
export function formatAttachmentBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '0 B'
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024
    unit += 1
  }
  const rounded = unit === 0 || value >= 10 ? Math.round(value) : Math.round(value * 10) / 10
  return `${rounded} ${UNITS[unit]}`
}
