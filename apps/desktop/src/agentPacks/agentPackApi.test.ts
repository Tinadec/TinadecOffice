// @vitest-environment happy-dom

import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { OFFICE_AGENT_PACK_ID, officeAgentPackEnvelope } from './OfficeAgentPack'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('agent pack API client', () => {
  it('posts the envelope directly for preview and preserves the response ETag', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
      action: 'install',
      preview_id: 'preview-1',
      pack_id: OFFICE_AGENT_PACK_ID,
      owner: 'tinadec.office',
      bundled_version: '0.1.0',
      installed_version: null,
      integrity_digest: officeAgentPackEnvelope.integrity.digest,
      revision: 0,
      expires_at: '2026-08-25T13:00:00Z',
      counts: { agents: 14, prompt_pipelines: 1, modes: 1, created: 16, adopted: 0, reused: 0, updated: 0 },
      required_core_version: '0.1.0',
      current_core_version: '0.1.0',
      warnings: [],
    }), { status: 200, headers: { 'content-type': 'application/json', etag: '"0"' } }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await api.previewAgentPackInstall(officeAgentPackEnvelope)

    expect(result.etag).toBe('"0"')
    const [, init] = fetchMock.mock.calls[0]!
    expect(JSON.parse(String(init?.body))).toEqual(officeAgentPackEnvelope)
  })

  it('puts preview plus envelope with idempotency and optional upgrade revision', async () => {
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

    await api.installAgentPack(
      OFFICE_AGENT_PACK_ID,
      { preview_id: 'preview-1', envelope: officeAgentPackEnvelope },
      { if_match: '"1"', idempotency_key: 'office-pack-0.1.0' },
    )

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toContain(`/api/v1/agent-packs/${encodeURIComponent(OFFICE_AGENT_PACK_ID)}`)
    expect(new Headers(init?.headers).get('if-match')).toBe('"1"')
    expect(new Headers(init?.headers).get('idempotency-key')).toBe('office-pack-0.1.0')
    expect(JSON.parse(String(init?.body))).toEqual({ preview_id: 'preview-1', envelope: officeAgentPackEnvelope })
  })
})
