// @vitest-environment happy-dom

import { afterEach, describe, expect, it, vi } from 'vitest'
import { generatedApi } from './client'
import { OFFICE_AGENT_PACK_ID, officeAgentPackEnvelope } from '@/agentPacks/OfficeAgentPack'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('generated agent pack client', () => {
  it('uses the Gateway pack routes and preserves concurrency headers and response ETag', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
      status: 'updated',
      pack_id: OFFICE_AGENT_PACK_ID,
      owner: 'tinadec.office',
      active_version: '0.1.0',
      integrity_digest: officeAgentPackEnvelope.integrity.digest,
      revision: 2,
      counts: { agents: 14, prompt_pipelines: 1, modes: 1 },
      installed_at: '2026-08-24T12:00:00Z',
      updated_at: '2026-08-25T12:00:00Z',
    }), { status: 200, headers: { 'content-type': 'application/json', etag: '"2"' } }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await generatedApi.installAgentPack(
      OFFICE_AGENT_PACK_ID,
      { preview_id: 'preview-1', envelope: officeAgentPackEnvelope },
      { if_match: '"1"', idempotency_key: 'office-pack-0.1.0' },
    )

    expect(result.etag).toBe('"2"')
    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toContain(`/api/v1/agent-packs/${encodeURIComponent(OFFICE_AGENT_PACK_ID)}`)
    expect(init?.method).toBe('PUT')
    expect(new Headers(init?.headers).get('if-match')).toBe('"1"')
    expect(new Headers(init?.headers).get('idempotency-key')).toBe('office-pack-0.1.0')
    expect(JSON.parse(String(init?.body))).toEqual({ preview_id: 'preview-1', envelope: officeAgentPackEnvelope })
  })
})
