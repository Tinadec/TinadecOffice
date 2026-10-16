// @vitest-environment happy-dom

import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import { ApiError } from '@/lib/apiError'
import { GRAPH_SEED_PACK_ID, graphSeedPackEnvelope } from './GraphSeedPack'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('agent pack API client', () => {
  it('keeps structured configuration diagnostics and trace without response extensions', async () => {
    const diagnostic = { code: 'configuration_unique', message: 'Duplicate draft slug meeting.', severity: 'error', line: 12, column: 4 }
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({
      code: 'configuration_invalid', detail: 'Configuration validation failed.', trace_id: 'trace-validation',
      diagnostics: [{ ...diagnostic, private_extension: 'not-public' }, { code: 1, message: 'invalid', severity: 'error' }],
      private_extension: 'not-public',
    }), { status: 400, headers: { 'content-type': 'application/problem+json' } })))
    const error = await api.installAgentPack(GRAPH_SEED_PACK_ID, { preview_id: 'preview-1', envelope: graphSeedPackEnvelope }, { idempotency_key: 'validation-test' }).catch((error: unknown) => error)
    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ serverMessage: 'Configuration validation failed.', code: 'configuration_invalid', status: 400, trace_id: 'trace-validation', diagnostics: [diagnostic] })
    expect(JSON.stringify(error)).not.toContain('not-public')
  })

  it('posts the envelope directly for preview and preserves the response ETag', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
      action: 'install',
      preview_id: 'preview-1',
      pack_id: GRAPH_SEED_PACK_ID,
      owner: 'tinadec',
      bundled_version: '0.1.0',
      installed_version: null,
      integrity_digest: graphSeedPackEnvelope.integrity.digest,
      revision: 0,
      expires_at: '2026-08-25T13:00:00Z',
      counts: { agents: 14, prompt_pipelines: 1, modes: 1, created: 16, adopted: 0, reused: 0, updated: 0 },
      required_core_version: '0.1.0',
      current_core_version: '0.1.0',
      warnings: [],
    }), { status: 200, headers: { 'content-type': 'application/json', etag: '"0"' } }))
    vi.stubGlobal('fetch', fetchMock)

    const result = await api.previewAgentPackInstall(graphSeedPackEnvelope)

    expect(result.etag).toBe('"0"')
    const [, init] = fetchMock.mock.calls[0]!
    expect(JSON.parse(String(init?.body))).toEqual(graphSeedPackEnvelope)
  })

  it('puts preview plus envelope with idempotency and optional upgrade revision', async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
      status: 'updated',
      pack_id: GRAPH_SEED_PACK_ID,
      owner: 'tinadec',
      active_version: '0.1.0',
      integrity_digest: graphSeedPackEnvelope.integrity.digest,
      revision: 2,
      counts: { agents: 14, prompt_pipelines: 1, modes: 1 },
      installed_at: '2026-08-24T12:00:00Z',
      updated_at: '2026-08-25T12:00:00Z',
    }), { status: 200, headers: { 'content-type': 'application/json', etag: '"2"' } }))
    vi.stubGlobal('fetch', fetchMock)

    await api.installAgentPack(
      GRAPH_SEED_PACK_ID,
      { preview_id: 'preview-1', envelope: graphSeedPackEnvelope },
      { if_match: '"1"', idempotency_key: 'graph-seed-pack-2.0.1' },
    )

    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toContain(`/api/v1/agent-packs/${encodeURIComponent(GRAPH_SEED_PACK_ID)}`)
    expect(new Headers(init?.headers).get('if-match')).toBe('"1"')
    expect(new Headers(init?.headers).get('idempotency-key')).toBe('graph-seed-pack-2.0.1')
    expect(JSON.parse(String(init?.body))).toEqual({ preview_id: 'preview-1', envelope: graphSeedPackEnvelope })
  })
})
