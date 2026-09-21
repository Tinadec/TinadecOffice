// No DOM pragma: this runs in the default node environment, which is also what makes
// import.meta.url a file URL for the Core source read below.
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { api } from '@/api'
import type { MessageAttachmentDto } from '@/generated/client'
import {
  attachFiles,
  formatAttachmentBytes,
  MAX_ATTACHMENT_BYTES,
  pendingAttachments,
  reconcileSession,
  removePendingAttachment,
  TOO_LARGE_CODE,
  type AttachableFile,
} from './pendingAttachments'

vi.mock('@/api', () => ({
  api: {
    uploadAttachment: vi.fn(),
    deleteAttachment: vi.fn(),
  },
}))

const upload = vi.mocked(api.uploadAttachment)
const remove = vi.mocked(api.deleteAttachment)

function storedRow(id: string): MessageAttachmentDto {
  return {
    id,
    session_id: 'sess-1',
    message_id: null,
    file_name: 'notes.txt',
    media_type: 'text/plain',
    content_hash: 'beef',
    content_length: 4,
    created_at: '2026-09-21T00:00:00Z',
    bound_at: null,
  }
}

function file(overrides: Partial<AttachableFile> = {}): AttachableFile {
  return {
    name: 'notes.txt',
    type: 'text/plain',
    size: 4,
    arrayBuffer: async () => new Uint8Array([1, 2, 3, 4]).buffer,
    ...overrides,
  }
}

/** A macrotask boundary: attachFiles pushes chips synchronously but the api call only
 * starts once the awaited arrayBuffer resolves, so a test cannot reach the mock in the
 * same tick it made the call. */
function tick(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0))
}

/**
 * The strip is module state on purpose (it survives a composer unmount). Reconciling to
 * null doubles as the assertion that no earlier case left rows under a null owner, which
 * reconcile would not clear.
 *
 * Implementations are re-installed rather than only cleared: mockClear leaves a prior
 * case's mockImplementation in place, and a deferred upload leaking into the next test
 * would hang it instead of failing it. Call history is cleared after the flush, because
 * the cleanup deletes reconcile fires are exactly what would otherwise land in this
 * case's assertion as a phantom call.
 */
beforeEach(async () => {
  upload.mockImplementation(async () => storedRow('row-1'))
  remove.mockImplementation(async () => undefined)
  reconcileSession(null)
  await tick()
  upload.mockClear()
  remove.mockClear()
  expect(pendingAttachments.value).toEqual([])
})

describe('attachment ceiling parity with Core', () => {
  const coreSource = readFileSync(
    fileURLToPath(new URL('../../../../TinadecCore/AspNetCore/Endpoints/AttachmentEndpoints.cs', import.meta.url)),
    'utf8',
  )

  it('reads the real endpoint source, so a moved file cannot blank the check', () => {
    expect(coreSource).toContain('MaxAttachmentBytes')
    expect(coreSource.length).toBeGreaterThan(1000)
  })

  it('declares the same byte ceiling as the endpoint that enforces it', () => {
    const declared = coreSource.match(/MaxAttachmentBytes\s*=\s*([0-9L_*\s]+)/)
    expect(declared, 'Core no longer declares MaxAttachmentBytes as a literal').not.toBeNull()
    const factors = declared![1]!
      .split('*')
      .map((part) => Number(part.replace(/[^0-9]/g, '')))
    expect(factors.every((n) => Number.isFinite(n) && n > 0), declared![1]).toBe(true)
    expect(MAX_ATTACHMENT_BYTES).toBe(factors.reduce((a, b) => a * b, 1))
  })

  it('reuses Core machine code for an oversized pick', () => {
    expect(coreSource).toContain(`"${TOO_LARGE_CODE}"`)
  })
})

describe('attachFiles', () => {
  it('needs a session and never buffers bytes for one that does not exist', async () => {
    await attachFiles([file()], null)
    expect(upload).not.toHaveBeenCalled()
    expect(pendingAttachments.value).toEqual([])
  })

  it('shows an uploading chip immediately and a ready chip with the Core row id', async () => {
    let release: ((value: MessageAttachmentDto) => void) | null = null
    upload.mockImplementationOnce(() => new Promise<MessageAttachmentDto>((r) => { release = r }))

    const settled = attachFiles([file()], 'sess-1')
    expect(pendingAttachments.value).toHaveLength(1)
    expect(pendingAttachments.value[0]).toMatchObject({
      fileName: 'notes.txt',
      mediaType: 'text/plain',
      status: 'uploading',
      attachmentId: null,
    })

    await tick()
    release!(storedRow('row-9'))
    await settled
    expect(pendingAttachments.value[0]).toMatchObject({ status: 'ready', attachmentId: 'row-9' })
  })

  it('posts the picked bytes under the session being composed into', async () => {
    await attachFiles([file()], 'sess-1')
    expect(upload).toHaveBeenCalledWith('sess-1', new Uint8Array([1, 2, 3, 4]), 'notes.txt', 'text/plain')
  })

  it('lets Core pick the media type when the picker reports none', async () => {
    await attachFiles([file({ type: '' })], 'sess-1')
    expect(upload.mock.calls[0]![3]).toBeUndefined()
    expect(pendingAttachments.value[0]!.mediaType).toBe('application/octet-stream')
  })

  it('rejects an oversized pick locally instead of uploading it to fail', async () => {
    await attachFiles([file({ size: MAX_ATTACHMENT_BYTES + 1 })], 'sess-1')
    expect(upload).not.toHaveBeenCalled()
    expect(pendingAttachments.value[0]).toMatchObject({ status: 'failed', errorCode: TOO_LARGE_CODE })
  })

  it('surfaces Core machine code when the upload is refused', async () => {
    upload.mockRejectedValueOnce(Object.assign(new Error('No session has that id.'), { code: 'session_not_found' }))
    await attachFiles([file()], 'sess-1')
    expect(pendingAttachments.value[0]).toMatchObject({ status: 'failed', errorCode: 'session_not_found' })
    expect(remove).not.toHaveBeenCalled()
  })

  it('settles each chip on its own rather than gating the strip on the slowest', async () => {
    upload
      .mockImplementationOnce(async () => storedRow('slow'))
      .mockImplementationOnce(async () => storedRow('fast'))
    await attachFiles([file({ name: 'slow.txt' }), file({ name: 'fast.txt' })], 'sess-1')
    expect(pendingAttachments.value.map((item) => [item.fileName, item.status, item.attachmentId])).toEqual([
      ['slow.txt', 'ready', 'slow'],
      ['fast.txt', 'ready', 'fast'],
    ])
  })
})

describe('orphan handling', () => {
  it('deletes a row whose chip was removed while it was still uploading', async () => {
    let release: ((value: MessageAttachmentDto) => void) | null = null
    upload.mockImplementation(() => new Promise<MessageAttachmentDto>((r) => { release = r }))

    const settled = attachFiles([file()], 'sess-1')
    const dropped = removePendingAttachment(pendingAttachments.value[0]!.clientId)
    expect(pendingAttachments.value).toEqual([])

    await tick()
    release!(storedRow('row-7'))
    await settled
    await dropped
    expect(remove).toHaveBeenCalledWith('row-7')
  })

  it('drops a row when the session changes under an in-flight upload', async () => {
    let release: ((value: MessageAttachmentDto) => void) | null = null
    upload.mockImplementation(() => new Promise<MessageAttachmentDto>((r) => { release = r }))

    const settled = attachFiles([file()], 'sess-1')
    await tick()
    reconcileSession('sess-2')
    release!(storedRow('row-8'))
    await settled
    expect(remove).toHaveBeenCalledWith('row-8')
    expect(pendingAttachments.value).toEqual([])
  })

  it('removes a ready chip and its Core row together', async () => {
    await attachFiles([file()], 'sess-1')
    await removePendingAttachment(pendingAttachments.value[0]!.clientId)
    expect(pendingAttachments.value).toEqual([])
    expect(remove).toHaveBeenCalledWith('row-1')
  })

  it('abandons uploaded rows when the composer moves to another session', async () => {
    upload.mockImplementationOnce(async () => storedRow('row-a'))
    await attachFiles([file()], 'sess-1')
    reconcileSession('sess-2')
    expect(pendingAttachments.value).toEqual([])
    expect(remove).toHaveBeenCalledWith('row-a')
  })

  it('does nothing when the same session is reported again', async () => {
    await attachFiles([file()], 'sess-1')
    remove.mockClear()
    reconcileSession('sess-1')
    expect(remove).not.toHaveBeenCalled()
    expect(pendingAttachments.value).toHaveLength(1)
  })

  it('keeps a failed upload from triggering a delete of a row that was never created', async () => {
    upload.mockRejectedValueOnce(Object.assign(new Error('body was empty'), { code: 'attachment_empty' }))
    await attachFiles([file()], 'sess-1')
    reconcileSession(null)
    expect(remove).not.toHaveBeenCalled()
  })

  it('swallows a cleanup failure so the composer stays usable', async () => {
    await attachFiles([file()], 'sess-1')
    remove.mockRejectedValueOnce(new Error('gateway down'))
    await expect(removePendingAttachment(pendingAttachments.value[0]!.clientId)).resolves.toBeUndefined()
  })
})

describe('formatAttachmentBytes', () => {
  const cases: Array<[number, string]> = [
    [0, '0 B'],
    [999, '999 B'],
    [1024, '1 KB'],
    [1536, '1.5 KB'],
    [10_485_760, '10 MB'],
    // A literal, not MAX_ATTACHMENT_BYTES: a label test that reads the constant would
    // break when Core raises the ceiling for an unrelated reason.
    [33_554_432, '32 MB'],
    [Number.NaN, '0 B'],
    [-1, '0 B'],
  ]

  it.each(cases)('labels %i bytes as %s', (bytes, expected) => {
    expect(formatAttachmentBytes(bytes)).toBe(expected)
  })
})
