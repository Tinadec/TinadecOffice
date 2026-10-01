// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'

const fetchMock = vi.fn()

beforeEach(() => {
  fetchMock.mockReset()
  fetchMock.mockResolvedValue({
    ok: true,
    status: 201,
    headers: { get: () => null },
    text: async () => JSON.stringify({
      id: 'a1b2c3d4-0000-0000-0000-000000000000',
      session_id: 'sess-1',
      file_name: 'notes.txt',
      media_type: 'text/plain',
      content_hash: 'beef',
      content_length: 4,
      created_at: '2026-09-21T00:00:00Z',
    }),
  })
  vi.stubGlobal('fetch', fetchMock)
  vi.stubGlobal('navigator', { onLine: true })
})

// Reading the LAST call lets one test exercise several routes in sequence; pinning to
// calls[0] would make every assertion after the first request describe the first one.
function lastCall(): [unknown, unknown] {
  return fetchMock.mock.calls[fetchMock.mock.calls.length - 1] as [unknown, unknown]
}

function url(): string {
  return String(lastCall()[0])
}

function init(): RequestInit {
  return lastCall()[1] as RequestInit
}

function headers(): Record<string, string> {
  return init().headers as Record<string, string>
}

/**
 * The upload contract is the one place in api.ts where the body is NOT JSON, so these
 * assertions exist to keep it that way. A future "consistency" refactor that wrapped the
 * bytes in a JSON field would still typecheck, would still return 200 in a mock, and
 * would store base64 text as the file.
 */
describe('attachment upload wire contract', () => {
  it('posts the raw bytes to the session-scoped route with the name in the query', async () => {
    const bytes = new Uint8Array([1, 2, 3, 4])
    await api.uploadAttachment('sess-1', bytes, 'notes.txt', 'text/plain')

    expect(url()).toContain('/api/v1/sessions/sess-1/attachments?filename=notes.txt&media_type=text%2Fplain')
    expect(init().method).toBe('POST')
    expect(init().body).toBe(bytes)
    expect(headers()['content-type']).toBe('application/octet-stream')
  })

  it('omits media_type rather than inventing a default on the client', async () => {
    await api.uploadAttachment('sess-1', new Uint8Array([1]), 'a.bin')
    expect(url()).toBe(
      `${'http://127.0.0.1:48730'}/api/v1/sessions/sess-1/attachments?filename=a.bin`,
    )
  })

  it('encodes a session id that contains a path separator', async () => {
    await api.uploadAttachment('a/b', new Uint8Array([1]), 'x.txt')
    expect(url()).toContain('/api/v1/sessions/a%2Fb/attachments')
    expect(url()).not.toContain('/api/v1/sessions/a/b/')
  })

  it('percent-encodes a file name that would otherwise forge query parameters', async () => {
    await api.uploadAttachment('sess-1', new Uint8Array([1]), 'a&media_type=text/html.txt')
    const parsed = new URL(url())
    expect(parsed.searchParams.get('filename')).toBe('a&media_type=text/html.txt')
    expect(parsed.searchParams.getAll('media_type')).toHaveLength(0)
  })
})

describe('attachment read and discard routes', () => {
  it('lists per session, and reads or deletes by attachment id', async () => {
    await api.listAttachments('sess/2')
    expect(url()).toContain('/api/v1/sessions/sess%2F2/attachments')

    await api.getAttachment('att-1')
    expect(url()).toContain('/api/v1/attachments/att-1')

    await api.deleteAttachment('att-1')
    expect(url()).toContain('/api/v1/attachments/att-1')
    expect(init().method).toBe('DELETE')
  })

  it('builds a content URL on the gateway route, never a filesystem path', () => {
    const contentUrl = api.attachmentContentUrl('att 1')
    expect(contentUrl).toBe('http://127.0.0.1:48730/api/v1/attachments/att%201/content')
    // A drive-letter path only: "127.0.0.1:48730" is a host and port, not C:\ style.
    expect(contentUrl).not.toMatch(/(^|\/|\s)[A-Za-z]:[\\/]/)
  })
})

/**
 * Core refuses an over-ceiling upload as { code: 'attachment_too_large' } with a 413.
 * The UI has to branch on that code to say "too big" instead of "upload failed", so the
 * code must survive the transport wrapper.
 */
describe('coded attachment errors survive the transport', () => {
  it('surfaces the machine code and status from a 413', async () => {
    fetchMock.mockResolvedValue({
      ok: false,
      status: 413,
      statusText: 'Payload Too Large',
      headers: { get: () => null },
      text: async () => JSON.stringify({ code: 'attachment_too_large', message: 'too big', max_bytes: 1024 }),
    })

    const error = await api.uploadAttachment('sess-1', new Uint8Array([1]), 'big.bin').catch((e: unknown) => e)
    expect((error as { code?: string }).code).toBe('attachment_too_large')
    expect((error as { status?: number }).status).toBe(413)
  })
})
