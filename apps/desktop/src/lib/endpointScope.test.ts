// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  buildEndpointReadCacheKey,
  clearEndpointEventCursors,
  endpointScopedApi,
  normalizeEndpointScope,
  parseEndpointId,
  peerDeviceIdOf,
  resetEndpointScopeForTests,
  resolveEndpointHttpUrl,
  resolveEndpointTerminalWsUrl,
  rewriteApiPathForEndpoint,
  rewriteTerminalWsPathForEndpoint,
  setActiveEndpoint,
  toEndpointId,
} from './endpointScope'
import {
  ApiError,
  isPeerError,
  isSubmissionUncertainError,
  PEER_ERROR_CODES,
  PEER_ERROR_DEFAULTS,
} from './apiError'
import { cachedRead, invalidateEndpointReadCache, invalidateReadCache, peekReadCacheKeys } from './readCache'
import { ScopedEventSource } from './scopedEventSource'
import { setHostAccessStatus } from './hostAccess'
import {
  captureStorageId,
  clearEndpointStorageScope,
  scopedApi,
  setSelectedStorage,
} from './storageScope'
import { api } from '@/api'

const originalTinadec = window.tinadec

beforeEach(() => {
  Object.defineProperty(window, 'tinadec', {
    configurable: true,
    value: {
      gatewayUrl: () => 'http://127.0.0.1:48730',
      getHostStatus: async () => ({ state: 'ready' as const, managed: true }),
    },
  })
  setHostAccessStatus({ state: 'ready', managed: true })
  resetEndpointScopeForTests()
  setSelectedStorage('user')
  invalidateReadCache()
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: originalTinadec })
  resetEndpointScopeForTests()
  setSelectedStorage('user')
  invalidateReadCache()
})

describe('EndpointScope routing & zero-trust loopback boundary', () => {
  it('normalizes local and peer endpoint identifiers and rejects empty peer deviceIds', () => {
    expect(normalizeEndpointScope()).toEqual({ kind: 'local' })
    expect(normalizeEndpointScope('local')).toEqual({ kind: 'local' })
    expect(parseEndpointId('peer:mac-mini-01')).toEqual({ kind: 'peer', deviceId: 'mac-mini-01' })
    expect(toEndpointId({ kind: 'peer', deviceId: 'mac-mini-01' })).toBe('peer:mac-mini-01')
    expect(peerDeviceIdOf('peer:mac-mini-01')).toBe('mac-mini-01')
    expect(peerDeviceIdOf('local')).toBeNull()
    expect(() => normalizeEndpointScope('peer:   ')).toThrow(/deviceId/i)
    expect(() => parseEndpointId('invalid:abc')).toThrow(/Invalid endpoint/i)
  })

  it('always targets local 127.0.0.1:48730 and strictly forbids direct remote peer origins', () => {
    setActiveEndpoint({ kind: 'peer', deviceId: 'worker-b' })
    const routed = resolveEndpointHttpUrl('/api/v1/projects?lifecycle_status=active', {
      gatewayBase: 'http://127.0.0.1:48730',
    })
    expect(routed).toBe('http://127.0.0.1:48730/api/v1/peers/worker-b/proxy/api/v1/projects?lifecycle_status=active')

    expect(() =>
      rewriteApiPathForEndpoint('http://192.168.1.88:48735/api/v1/projects', 'peer:worker-b', {
        gatewayBase: 'http://127.0.0.1:48730',
      }),
    ).toThrow(/Security boundary violation/i)
  })

  it('rewrites business API and terminal WS paths for peers while never rewriting management plane or double-proxying', () => {
    const peer = { kind: 'peer' as const, deviceId: 'dev/alpha 1' }
    expect(rewriteApiPathForEndpoint('/api/v1/sessions/s1/messages', 'local')).toBe('/api/v1/sessions/s1/messages')
    expect(rewriteApiPathForEndpoint('/api/v1/sessions/s1/messages', peer)).toBe(
      '/api/v1/peers/dev%2Falpha%201/proxy/api/v1/sessions/s1/messages',
    )
    expect(rewriteApiPathForEndpoint('/api/v1/code/tools/read_file/execute', peer)).toBe(
      '/api/v1/peers/dev%2Falpha%201/proxy/api/v1/code/tools/read_file/execute',
    )
    expect(rewriteApiPathForEndpoint('/api/v1/approvals?status=pending', peer)).toBe(
      '/api/v1/peers/dev%2Falpha%201/proxy/api/v1/approvals?status=pending',
    )

    // Management plane & local auth MUST stay local and NEVER double-proxy
    for (const localPath of [
      '/api/v1/peers/inbound',
      '/api/v1/peers/pairing-code',
      '/api/v1/peers/devices',
      '/api/v1/peers/devices/dev-1/connect',
      '/api/v1/peers/grants',
      '/api/v1/peers/dev%2Falpha%201/proxy/api/v1/projects',
      '/api/v1/host-challenge',
      '/api/v1/web-auth/status',
    ]) {
      expect(rewriteApiPathForEndpoint(localPath, peer)).toBe(localPath)
    }

    // Terminal WS rewriting
    expect(rewriteTerminalWsPathForEndpoint('/ws/terminal?shell=pwsh', 'local')).toBe('/ws/terminal?shell=pwsh')
    expect(rewriteTerminalWsPathForEndpoint('/ws/terminal?shell=pwsh', peer)).toBe(
      '/ws/peers/dev%2Falpha%201/terminal?shell=pwsh',
    )
    expect(
      resolveEndpointTerminalWsUrl({
        path: '/ws/terminal?cwd=%2Frepo',
        endpoint: peer,
        gatewayBase: 'http://127.0.0.1:48730',
      }),
    ).toBe('ws://127.0.0.1:48730/ws/peers/dev%2Falpha%201/terminal?cwd=%2Frepo')
  })
})

describe('readCache & storageScope ${endpointId}:${storageId} isolation (zero cross-talk)', () => {
  it('isolates cachedRead keys by ${endpointId}:${storageId} and invalidates per endpoint without cross-eviction', async () => {
    setSelectedStorage('ws-same')
    const localRead = vi.fn(async () => ['local-agent'])
    const peerARead = vi.fn(async () => ['peer-a-agent'])
    const peerBRead = vi.fn(async () => ['peer-b-agent'])

    expect(buildEndpointReadCacheKey('agents', { endpoint: 'local', storageId: 'ws-same' })).toBe(
      'local:ws-same:agents',
    )
    expect(buildEndpointReadCacheKey('agents', { endpoint: 'peer:dev-a', storageId: 'ws-same' })).toBe(
      'peer:dev-a:ws-same:agents',
    )

    const resLocal = await cachedRead('agents', localRead)
    setActiveEndpoint('peer:dev-a')
    const resPeerA = await cachedRead('agents', peerARead)
    setActiveEndpoint('peer:dev-b')
    const resPeerB = await cachedRead('agents', peerBRead)

    expect(resLocal).toEqual(['local-agent'])
    expect(resPeerA).toEqual(['peer-a-agent'])
    expect(resPeerB).toEqual(['peer-b-agent'])
    expect(peekReadCacheKeys().sort()).toEqual([
      'local:ws-same:agents',
      'peer:dev-a:ws-same:agents',
      'peer:dev-b:ws-same:agents',
    ])

    // Invalidating peer:dev-a MUST NOT evict local or peer:dev-b
    invalidateEndpointReadCache('peer:dev-a')
    expect(peekReadCacheKeys().sort()).toEqual([
      'local:ws-same:agents',
      'peer:dev-b:ws-same:agents',
    ])

    setActiveEndpoint('local')
    await cachedRead('agents', localRead)
    expect(localRead).toHaveBeenCalledTimes(1)
  })

  it('supports concurrent local and peer reads/writes via endpointScopedApi + scopedApi with zero interference', async () => {
    const seenRequests: Array<{ url: string; method: string; endpointHeader: string | null; storageHeader: string | null }> = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        const headers = new Headers(init?.headers)
        seenRequests.push({
          url,
          method: init?.method ?? 'GET',
          endpointHeader: headers.get('x-tinadec-endpoint-id'),
          storageHeader: headers.get('x-tinadec-storage-id'),
        })
        if (url.includes('/api/v1/peers/dev-remote/proxy/api/v1/agents')) {
          return new Response(JSON.stringify([{ id: 'remote-agent', slug: 'ra', display_name: 'Remote' }]), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
        if (url.endsWith('/api/v1/agents')) {
          return new Response(JSON.stringify([{ id: 'local-agent', slug: 'la', display_name: 'Local' }]), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
        if (url.includes('/api/v1/peers/dev-remote/proxy/api/v1/projects')) {
          return new Response(
            JSON.stringify([{ id: 'shared-proj-id', name: 'RemoteProj', path: '/remote/repo', storage_id: 'remote-ws', created_at: '' }]),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/projects')) {
          return new Response(
            JSON.stringify([{ id: 'shared-proj-id', name: 'LocalProj', path: '/local/repo', storage_id: 'local-ws', created_at: '' }]),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        return new Response(JSON.stringify({ id: 'sess-created', project_id: 'shared-proj-id' }), {
          status: 200,
          headers: { 'content-type': 'application/json' },
        })
      }),
    )

    const localBoundApi = scopedApi(endpointScopedApi(api, () => 'local'), () => 'local-ws')
    const peerBoundApi = scopedApi(endpointScopedApi(api, () => ({ kind: 'peer', deviceId: 'dev-remote' })), () => 'remote-ws')

    const [localAgents, remoteAgents, localProjects, remoteProjects] = await Promise.all([
      localBoundApi.listAgents(),
      peerBoundApi.listAgents(),
      api.listProjects(),
      endpointScopedApi(api, () => 'peer:dev-remote').listProjects(),
    ])

    expect(localAgents[0]?.id).toBe('local-agent')
    expect(remoteAgents[0]?.id).toBe('remote-agent')
    expect(localProjects[0]?.name).toBe('LocalProj')
    expect(remoteProjects[0]?.name).toBe('RemoteProj')

    // Same project id ('shared-proj-id') registered on both endpoints MUST resolve independently
    expect(captureStorageId('/api/v1/projects/shared-proj-id', { endpointId: 'local' })).toBe('local-ws')
    expect(captureStorageId('/api/v1/projects/shared-proj-id', { endpointId: 'peer:dev-remote' })).toBe('remote-ws')

    // Peer write invalidates only peer:dev-remote cache, leaving local:local-ws:agents intact!
    await peerBoundApi.createProject('NewRemote', '/remote/new')
    expect(peekReadCacheKeys()).toContain('local:local-ws:agents')
    expect(peekReadCacheKeys()).not.toContain('peer:dev-remote:remote-ws:agents')

    // Clearing peer storage scope does not touch local entity mapping
    clearEndpointStorageScope('peer:dev-remote')
    expect(captureStorageId('/api/v1/projects/shared-proj-id', { endpointId: 'local' })).toBe('local-ws')

    // Every request went strictly to local loopback gateway
    for (const req of seenRequests) {
      expect(req.url.startsWith('http://127.0.0.1:48730/')).toBe(true)
    }
  })
})

describe('ScopedEventSource endpoint headers & cursor isolation', () => {
  it('rewrites peer SSE URL on local gateway, attaches x-tinadec-endpoint-id, and isolates cursors across endpoints', async () => {
    vi.useFakeTimers()
    const RUN_LOCAL = '1'.repeat(32)
    const RUN_PEER = '2'.repeat(32)
    const calls: Array<{ url: string; headers: Headers }> = []

    const fakeFetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      const headers = new Headers(init?.headers)
      calls.push({ url, headers })
      const isPeer = url.includes('/api/v1/peers/dev-1/proxy/')
      const frame = isPeer
        ? `id: ${RUN_PEER}:42\nevent: message\ndata: {"seq":42,"source":"peer"}\n\n`
        : `id: ${RUN_LOCAL}:7\nevent: message\ndata: {"seq":7,"source":"local"}\n\n`
      return new Response(frame, { headers: { 'content-type': 'text/event-stream' } })
    }) as typeof fetch

    const localSource = new ScopedEventSource('http://127.0.0.1:48730/api/v1/events?session_id=s1', 'user', fakeFetch, 45_000, {
      endpoint: 'local',
      persistCursor: true,
    })
    const peerSource = new ScopedEventSource('http://127.0.0.1:48730/api/v1/events?session_id=s1', 'user', fakeFetch, 45_000, {
      endpoint: { kind: 'peer', deviceId: 'dev-1' },
      afterSeq: 10,
      persistCursor: true,
    })

    await vi.advanceTimersByTimeAsync(0)
    localSource.close()
    peerSource.close()

    expect(calls[0]?.url).toBe('http://127.0.0.1:48730/api/v1/events?session_id=s1')
    expect(calls[0]?.headers.get('x-tinadec-endpoint-id')).toBe('local')
    expect(calls[1]?.url).toBe('http://127.0.0.1:48730/api/v1/peers/dev-1/proxy/api/v1/events?session_id=s1&after_seq=10')
    expect(calls[1]?.headers.get('x-tinadec-endpoint-id')).toBe('peer:dev-1')

    // Clearing peer:dev-1 cursors resets only peer:dev-1 while keeping local cursor intact
    clearEndpointEventCursors('peer:dev-1')

    const localReconnect = new ScopedEventSource('http://127.0.0.1:48730/api/v1/events?session_id=s1', 'user', fakeFetch, 45_000, {
      endpoint: 'local',
      persistCursor: true,
    })
    const peerAfterClear = new ScopedEventSource('http://127.0.0.1:48730/api/v1/events?session_id=s1', 'user', fakeFetch, 45_000, {
      endpoint: 'peer:dev-1',
      persistCursor: true,
    })
    await vi.advanceTimersByTimeAsync(0)
    localReconnect.close()
    peerAfterClear.close()

    expect(calls[2]?.headers.get('last-event-id')).toBe(`${RUN_LOCAL}:7`)
    expect(calls[3]?.headers.get('last-event-id')).toBeNull()
  })
})

describe('13 RFC 9457 peer error codes & submission_uncertain message protection', () => {
  it('recognizes and maps all 13 peer RFC 9457 error codes with accurate retryability and categories', () => {
    expect(PEER_ERROR_CODES).toHaveLength(13)
    for (const code of PEER_ERROR_CODES) {
      const err = new ApiError('', 403, { code })
      expect(isPeerError(err, code)).toBe(true)
      expect(err.category).toBe(PEER_ERROR_DEFAULTS[code].category)
      expect(err.retryable).toBe(PEER_ERROR_DEFAULTS[code].retryable)
      expect(err.message.length).toBeGreaterThan(0)
    }

    const mismatch = new ApiError('', 403, { code: 'fingerprint_mismatch' })
    expect(mismatch.retryable).toBe(false)
    expect(mismatch.actions).toContain('open_settings')

    const uncertain = new ApiError('', 504, { code: 'submission_uncertain' })
    expect(isSubmissionUncertainError(uncertain)).toBe(true)
    expect(uncertain.retryable).toBe(false)
  })

  it('attaches UUID client_request_id when sending messages and marks network drops / 502 / 504 as submission_uncertain', async () => {
    const capturedBodies: Array<Record<string, unknown>> = []
    const capturedHeaders: Headers[] = []
    let mode: 'ok' | 'network_drop' | 'gateway_502' | 'gateway_504' | 'policy_403' = 'ok'

    vi.stubGlobal(
      'fetch',
      vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
        capturedHeaders.push(new Headers(init?.headers))
        if (typeof init?.body === 'string') {
          capturedBodies.push(JSON.parse(init.body) as Record<string, unknown>)
        }
        if (mode === 'network_drop') throw new TypeError('Failed to fetch')
        if (mode === 'gateway_502') {
          return new Response(JSON.stringify({ detail: 'Peer tunnel broken' }), {
            status: 502,
            headers: { 'content-type': 'application/problem+json' },
          })
        }
        if (mode === 'gateway_504') {
          return new Response(JSON.stringify({ code: 'timeout', detail: 'Gateway timeout' }), {
            status: 504,
            headers: { 'content-type': 'application/problem+json' },
          })
        }
        if (mode === 'policy_403') {
          return new Response(JSON.stringify({ code: 'policy_denied', detail: 'Observer role cannot send messages' }), {
            status: 403,
            headers: { 'content-type': 'application/problem+json' },
          })
        }
        return new Response(JSON.stringify({ id: 'msg-1', content: 'hello' }), {
          status: 201,
          headers: { 'content-type': 'application/json' },
        })
      }),
    )

    const peerApi = endpointScopedApi(api, () => ({ kind: 'peer', deviceId: 'worker-1' }))
    await peerApi.postMessage('sess-1', 'hello world')
    expect(typeof capturedBodies[0]?.client_request_id).toBe('string')
    expect((capturedBodies[0]?.client_request_id as string).length).toBeGreaterThanOrEqual(16)
    expect(capturedHeaders[0]?.get('x-tinadec-client-request-id')).toBe(capturedBodies[0]?.client_request_id)

    for (const failureMode of ['network_drop', 'gateway_502', 'gateway_504'] as const) {
      mode = failureMode
      const caught = await peerApi.postMessage('sess-1', 'uncertain msg').catch((e: unknown) => e)
      expect(isSubmissionUncertainError(caught)).toBe(true)
      expect((caught as ApiError).code).toBe('submission_uncertain')
      expect((caught as ApiError).retryable).toBe(false)
      expect((caught as ApiError).endpointId).toBe('peer:worker-1')
      expect(typeof (caught as ApiError).clientRequestId).toBe('string')
    }

    // createInteraction keeps Core's strict body schema clean while marking 502/network drops as submission_uncertain
    mode = 'gateway_502'
    const interactionErr = await peerApi
      .createInteraction('sess-1', {
        content: 'run build',
        client_message_id: 'req-uuid-999',
        dispatch_mode: 'queued',
      })
      .catch((e: unknown) => e)
    expect(isSubmissionUncertainError(interactionErr)).toBe(true)
    expect((interactionErr as ApiError).clientRequestId).toBe('req-uuid-999')
    expect(capturedBodies.at(-1)?.client_request_id).toBeUndefined()

    // Explicit 403 policy_denied is deterministic refusal, NEVER submission_uncertain
    mode = 'policy_403'
    await expect(peerApi.postMessage('sess-1', 'denied')).rejects.toMatchObject({
      status: 403,
      code: 'policy_denied',
      retryable: false,
    })
  })
})
