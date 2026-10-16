// @vitest-environment happy-dom

import { afterEach, describe, expect, it, vi } from 'vitest'
import { generatedApi } from './client'
import { setHostAccessStatus } from '@/lib/hostAccess'
import { ApiError } from '@/lib/apiError'
const originalBridge = window.tinadec
import { GRAPH_SEED_PACK_ID, graphSeedPackEnvelope } from '@/agentPacks/GraphSeedPack'

afterEach(() => {
  vi.unstubAllGlobals()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: originalBridge })
  setHostAccessStatus({ state: 'ready', managed: true })
})

describe('generated agent pack client', () => {
  it.each(['ordinary', 'installation'] as const)('preserves and caches a missing host IPC contract starting with an %s request', async first => {
    setHostAccessStatus({ state: 'ready', managed: true })
    const getHostStatus = vi.fn().mockRejectedValue(new Error("Error invoking remote method 'tinadec:host-status': Error: No handler registered for 'tinadec:host-status'"))
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus } })
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    const ordinary = () => generatedApi.listAgentPacks()
    const installation = () => generatedApi.installAgentPack('pack', { preview_id: 'preview', envelope: graphSeedPackEnvelope }, { idempotency_key: 'host-contract-test' })
    const operations = first === 'ordinary' ? [ordinary, installation] : [installation, ordinary]
    for (const operation of [...operations, () => generatedApi.getAgentPack('pack'), ...operations]) {
      const error = await operation().catch((reason: unknown) => reason)
      expect(error).toBeInstanceOf(ApiError)
      expect(error).toMatchObject({ code: 'desktop_restart_required', status: 503, category: 'environment_unavailable', retryable: false, actions: [] })
      expect((error as Error).message).not.toContain('Cannot connect to backend')
    }
    expect(getHostStatus).toHaveBeenCalledOnce()
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it('blocks ETag reads and installation requests before fetch in preview', async () => {
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { getHostStatus: async () => ({ state: 'preview', managed: false }) } })
    const fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
    await expect(generatedApi.getAgentPack('pack')).rejects.toMatchObject({ code: 'desktop_host_required' })
    await expect(generatedApi.installAgentPack('pack', { preview_id: 'preview', envelope: graphSeedPackEnvelope }, { idempotency_key: 'preview-test' })).rejects.toMatchObject({ code: 'desktop_host_required' })
    expect(fetchMock).not.toHaveBeenCalled()
  })
  it('keeps public problem diagnostics for both ETag and ordinary requests', async () => {
    const diagnostic = { code: 'configuration_unique', message: 'Duplicate draft slug.', severity: 'error' }
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ code: 'configuration_invalid',
      detail: 'Configuration validation failed.', trace_id: 'trace-validation', diagnostics: [diagnostic],
    }), { status: 400, headers: { 'content-type': 'application/problem+json' } })))
    for (const operation of [() => generatedApi.getAgentPack('pack'), () => generatedApi.listAgentPacks()]) {
      await expect(operation()).rejects.toMatchObject({ status: 400, code: 'configuration_invalid',
        serverMessage: 'Configuration validation failed.', trace_id: 'trace-validation', diagnostics: [diagnostic] })
    }
  })

  it('uses the Gateway pack routes and preserves concurrency headers and response ETag', async () => {
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

    const result = await generatedApi.installAgentPack(
      GRAPH_SEED_PACK_ID,
      { preview_id: 'preview-1', envelope: graphSeedPackEnvelope },
      { if_match: '"1"', idempotency_key: 'graph-seed-pack-2.0.1' },
    )

    expect(result.etag).toBe('"2"')
    const [url, init] = fetchMock.mock.calls[0]!
    expect(String(url)).toContain(`/api/v1/agent-packs/${encodeURIComponent(GRAPH_SEED_PACK_ID)}`)
    expect(init?.method).toBe('PUT')
    expect(new Headers(init?.headers).get('if-match')).toBe('"1"')
    expect(new Headers(init?.headers).get('idempotency-key')).toBe('graph-seed-pack-2.0.1')
    expect(JSON.parse(String(init?.body))).toEqual({ preview_id: 'preview-1', envelope: graphSeedPackEnvelope })
  })
})

describe('generated client cancellation semantics', () => {
  it('keeps an AbortError from req', async () => {
    const abortError = Object.assign(new Error('signal is aborted without reason'), { name: 'AbortError' })
    vi.stubGlobal('fetch', vi.fn(async () => {
      throw abortError
    }))

    const error = await generatedApi.listProjects().catch((reason: unknown) => reason)

    expect(error).toBe(abortError)
    expect((error as Error).message).not.toContain('Cannot connect to backend')
  })

  it('keeps an AbortError from reqWithEtag', async () => {
    const abortError = new DOMException('signal is aborted without reason', 'AbortError')
    vi.stubGlobal('fetch', vi.fn(async () => {
      throw abortError
    }))

    const error = await generatedApi.getAgentPack('pack-1').catch((reason: unknown) => reason)

    expect(error).toBe(abortError)
    expect((error as Error).message).not.toContain('Cannot connect to backend')
  })
})
