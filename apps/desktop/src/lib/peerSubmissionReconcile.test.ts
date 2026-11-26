// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import i18n from '@/i18n'
import { api, PEER_ATTACHMENT_CHUNK_SIZE_BYTES } from '@/api'
import { isSubmissionUncertainError } from '@/lib/apiError'
import {
  endpointScopedApi,
  readEndpointEventCursor,
  resetEndpointScopeForTests,
  setActiveEndpoint,
  writeEndpointEventCursor,
} from '@/lib/endpointScope'
import { setHostAccessStatus } from '@/lib/hostAccess'
import { invalidateReadCache } from '@/lib/readCache'
import { setSelectedStorage } from '@/lib/storageScope'
import {
  markPeerSubmissionUncertain,
  reconcileDeviceUncertainSubmissions,
  reconcilePeerSubmission,
   resendUncertainSubmission,
  resetPeerSubmissionReconcileForTests,
  usePeerSubmissionReconcile,
} from '@/lib/peerSubmissionReconcile'
import { resetPeerDevicesStateForTests, usePeerDevices } from '@/composables/usePeerDevices'
import PeerOfflineShadowBanner from '@/components/peers/PeerOfflineShadowBanner.vue'
import PeerUncertainBubbleNotice from '@/components/peers/PeerUncertainBubbleNotice.vue'
import PreviewBrowserPanel from '@/components/PreviewBrowserPanel.vue'

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
  resetPeerSubmissionReconcileForTests()
  resetPeerDevicesStateForTests()
  setSelectedStorage('user')
  invalidateReadCache()
})

afterEach(() => {
  vi.unstubAllGlobals()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: originalTinadec })
  resetEndpointScopeForTests()
  resetPeerSubmissionReconcileForTests()
  resetPeerDevicesStateForTests()
  setSelectedStorage('user')
  invalidateReadCache()
})

describe('peerSubmissionReconcile — submission_uncertain guard, ingress reconcile & resend lock', () => {
  it('captures uncertain draft on mid-flight network drop without blind auto-resend, reconciles accepted with after_seq, and only unlocks resend on found:false or rejected', async () => {
    const calls: Array<{ url: string; method: string; body?: Record<string, unknown> }> = []
    let interactionMode: 'drop' | 'ok' = 'drop'
    let ingressResponse: Record<string, unknown> = {
      found: true,
      client_request_id: 'req-accepted-1',
      stage: 'processed',
      run_id: 'run-remote-1',
      turn_id: 'turn-remote-1',
      message_id: 'msg-remote-1',
      last_event_seq: 28,
    }

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        const method = (init?.method ?? 'GET').toUpperCase()
        const body = typeof init?.body === 'string' && init.body ? (JSON.parse(init.body) as Record<string, unknown>) : undefined
        calls.push({ url, method, body })

        if (url.includes('/proxy/api/v1/sessions/sess-1/interactions') && method === 'POST') {
          if (interactionMode === 'drop') {
            throw new TypeError('Failed to fetch')
          }
          return new Response(
            JSON.stringify({
              interaction_id: 'int-1',
              status: 'admitted',
              run_id: 'run-retry-1',
              turn_id: 'turn-retry-1',
              idempotent_replay: false,
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }

        if (url.includes('/proxy/api/v1/sessions/sess-1/ingress/') && method === 'GET') {
          return new Response(JSON.stringify(ingressResponse), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }

        return new Response(JSON.stringify({}), { status: 200, headers: { 'content-type': 'application/json' } })
      }),
    )

    const peerApi = endpointScopedApi(api, () => ({ kind: 'peer', deviceId: 'worker-mac' }))
    const store = usePeerSubmissionReconcile({
      endpoint: () => 'peer:worker-mac',
      sessionId: () => 'sess-1',
    })

    // 1. Mid-flight network drop throws submission_uncertain and stores uncertain draft (NEVER auto-resends)
    const err1 = await peerApi
      .createInteraction('sess-1', {
        content: 'run cargo test',
        client_message_id: 'req-accepted-1',
        dispatch_mode: 'queued',
        staged_attachment_ids: ['stage-att-1'],
      })
      .catch((e: unknown) => e)

    expect(isSubmissionUncertainError(err1)).toBe(true)
    expect(calls.filter((c) => c.method === 'POST')).toHaveLength(1)
    expect(store.sessionRecords.value).toHaveLength(1)
    expect(store.sessionRecords.value[0]).toMatchObject({
      endpointId: 'peer:worker-mac',
      deviceId: 'worker-mac',
      sessionId: 'sess-1',
      clientRequestId: 'req-accepted-1',
      text: 'run cargo test',
      stagedAttachmentIds: ['stage-att-1'],
      status: 'uncertain',
    })

    // Attempting to resend BEFORE ingress status reconciliation is strictly blocked
    await expect(resendUncertainSubmission('req-accepted-1')).rejects.toThrow(/Reconcile ingress status first/i)

    // Render PeerUncertainBubbleNotice: resend button MUST be disabled while status === 'uncertain'
    const bubbleWrapper = mount(PeerUncertainBubbleNotice, {
      props: { record: store.sessionRecords.value[0]! },
      global: { plugins: [i18n] },
    })
    expect(bubbleWrapper.get('[data-testid="peer-uncertain-resend-btn"]').attributes('disabled')).toBeDefined()

    // 2. Reconcile when peer reports stage='processed' -> transitions to accepted, clears notice, updates after_seq=28
    writeEndpointEventCursor('peer:worker-mac', 'user', '/api/v1/events?session_id=sess-1', {
      watermarks: {},
      lastFrameId: '12',
      afterSeq: 12,
    })
    const acceptedEvents: number[] = []
    const resAccepted = await reconcilePeerSubmission('req-accepted-1', {
      onAccepted: ({ afterSeq }) => {
        acceptedEvents.push(afterSeq)
      },
    })
    expect(resAccepted.outcome).toBe('accepted')
    expect(resAccepted.afterSeq).toBe(28)
    expect(acceptedEvents).toEqual([28])
    expect(store.sessionRecords.value).toHaveLength(0)
    expect(
      readEndpointEventCursor('peer:worker-mac', 'user', '/api/v1/events?session_id=sess-1')?.afterSeq,
    ).toBe(28)
    // Verify ingress check carried previous after_seq=12 and went through local gateway proxy
    expect(calls.at(-1)?.url).toBe(
      'http://127.0.0.1:48730/api/v1/peers/worker-mac/proxy/api/v1/sessions/sess-1/ingress/req-accepted-1?after_seq=12',
    )

    // 3. Second message drops, peer explicitly confirms found:false -> transitions to retry_ready & unlocks manual resend
    markPeerSubmissionUncertain({
      endpoint: 'peer:worker-mac',
      sessionId: 'sess-1',
      clientRequestId: 'req-missing-2',
      text: 'deploy preview',
      stagedAttachmentIds: ['stage-att-2'],
    })
    ingressResponse = {
      found: false,
      client_request_id: 'req-missing-2',
    }

    const resMissing = await reconcileDeviceUncertainSubmissions('worker-mac')
    expect(resMissing[0]?.outcome).toBe('retry_ready')
    expect(store.sessionRecords.value[0]?.status).toBe('retry_ready')

    const retryBubble = mount(PeerUncertainBubbleNotice, {
      props: { record: store.sessionRecords.value[0]! },
      global: { plugins: [i18n] },
    })
    expect(retryBubble.get('[data-testid="peer-uncertain-resend-btn"]').attributes('disabled')).toBeUndefined()

    // User manually triggers resend -> reuses req-missing-2 and settles record
    interactionMode = 'ok'
    await resendUncertainSubmission('req-missing-2')
    expect(store.sessionRecords.value).toHaveLength(0)
    const resentCall = calls.at(-1)
    expect(resentCall?.method).toBe('POST')
    expect(resentCall?.url).toBe('http://127.0.0.1:48730/api/v1/peers/worker-mac/proxy/api/v1/sessions/sess-1/interactions')
    expect(resentCall?.body).toMatchObject({
      content: 'deploy preview',
      client_message_id: 'req-missing-2',
      staged_attachment_ids: ['stage-att-2'],
    })
  })
})

describe('stageAttachmentChunks — >256KiB multi-chunk upload & workspace source_path zero-copy', () => {
  it('splits >256KiB peer attachments into sequential chunks and sends zero-copy reference for workspace source_path', async () => {
    const stageRequests: Array<Record<string, unknown>> = []

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        if (url.includes('/proxy/api/v1/attachments/stage')) {
          const payload = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>
          stageRequests.push(payload)
          const chunkIndex = Number(payload.chunk_index ?? 0)
          const totalChunks = Number(payload.total_chunks ?? 1)
          const complete = chunkIndex + 1 >= totalChunks
          return new Response(
            JSON.stringify({
              stage_id: payload.stage_id,
              complete,
              zero_copy: payload.origin === 'workspace',
              received_chunks: chunkIndex + 1,
              total_chunks: totalChunks,
              ...(complete ? { attachment_id: payload.origin === 'workspace' ? 'att-zerocopy-1' : 'att-chunked-1' } : {}),
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        return new Response(JSON.stringify({}), { status: 200, headers: { 'content-type': 'application/json' } })
      }),
    )

    setActiveEndpoint({ kind: 'peer', deviceId: 'worker-linux' })

    // 600 KiB payload -> 3 chunks at 256 KiB threshold
    const bigBytes = new Uint8Array(600 * 1024)
    bigBytes.fill(0x41)
    expect(bigBytes.byteLength).toBeGreaterThan(PEER_ATTACHMENT_CHUNK_SIZE_BYTES * 2)

    const uploadedBig = await api.uploadAttachment('sess-stage', bigBytes, 'screenshot.png', 'image/png', {
      origin: 'composer',
    })
    expect(uploadedBig.id).toBe('att-chunked-1')
    expect(stageRequests).toHaveLength(3)
    expect(stageRequests.map((r) => [r.chunk_index, r.total_chunks])).toEqual([
      [0, 3],
      [1, 3],
      [2, 3],
    ])
    expect(stageRequests[0]?.stage_id).toBe(stageRequests[1]?.stage_id)
    expect(stageRequests[1]?.stage_id).toBe(stageRequests[2]?.stage_id)
    expect(typeof stageRequests[0]?.chunk_base64).toBe('string')

    // Workspace source_path -> 1 zero-copy reference request without chunk_base64
    stageRequests.length = 0
    const zeroCopy = await api.uploadAttachment('sess-stage', new Uint8Array(0), 'Cargo.toml', 'text/plain', {
      origin: 'workspace',
      projectId: 'proj-rust',
      sourcePath: '/home/runner/repo/Cargo.toml',
    })
    expect(zeroCopy.id).toBe('att-zerocopy-1')
    expect(stageRequests).toHaveLength(1)
    expect(stageRequests[0]).toMatchObject({
      origin: 'workspace',
      project_id: 'proj-rust',
      source_path: '/home/runner/repo/Cargo.toml',
      chunk_index: 0,
      total_chunks: 1,
    })
    expect(stageRequests[0]?.chunk_base64).toBeUndefined()
  })
})

describe('Offline read-only shadow fallback & PeerOfflineShadowBanner + PreviewBrowserPanel DevServer lease', () => {
  it('falls back seamlessly to local shadow endpoints when peer is offline and renders PeerOfflineShadowBanner', async () => {
    const requestedUrls: string[] = []

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input)
        requestedUrls.push(url)

        if (url.includes('/proxy/api/v1/sessions')) {
          return new Response(
            JSON.stringify({ code: 'device_offline', detail: 'Worker peer is offline' }),
            { status: 503, headers: { 'content-type': 'application/problem+json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-offline/shadow/sessions?project_id=proj-1')) {
          return new Response(
            JSON.stringify({
              device_id: 'dev-offline',
              sessions: [
                {
                  local_shadow_id: 'sh-1',
                  peer_device_id: 'dev-offline',
                  remote_session_id: 'rsess-1',
                  remote_project_id: 'proj-1',
                  title: 'Cached Architecture Review',
                  run_status: 'completed',
                  last_event_seq: 19,
                  last_synced_at: 1700000500000,
                },
              ],
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-offline/shadow/sessions/rsess-1')) {
          return new Response(
            JSON.stringify({
              session: {
                local_shadow_id: 'sh-1',
                peer_device_id: 'dev-offline',
                remote_session_id: 'rsess-1',
                remote_project_id: 'proj-1',
                title: 'Cached Architecture Review',
                run_status: 'completed',
                last_event_seq: 19,
                last_synced_at: 1700000500000,
              },
              turns: [
                {
                  turn_id: 't-1',
                  digest_json: JSON.stringify({
                    user_content: 'Check build status',
                    assistant_content: 'All 42 checks passed before disconnect.',
                  }),
                  updated_at: 1700000500000,
                },
              ],
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        return new Response(JSON.stringify({}), { status: 200, headers: { 'content-type': 'application/json' } })
      }),
    )

    const peers = usePeerDevices()
    peers.switchEndpoint({ kind: 'peer', deviceId: 'dev-offline' })

    const shadowSessions = await api.listSessions('proj-1')
    expect(shadowSessions).toHaveLength(1)
    expect(shadowSessions[0]?.id).toBe('rsess-1')
    expect(shadowSessions[0]?.title).toBe('Cached Architecture Review')
    expect(peers.isActivePeerOfflineReadonly.value).toBe(true)

    // Subsequent message read goes straight to local shadow cache without hanging on offline proxy
    const shadowMessages = await api.listMessages('rsess-1')
    expect(shadowMessages).toHaveLength(2)
    expect(shadowMessages[0]?.content).toBe('Check build status')
    expect(shadowMessages[1]?.content).toBe('All 42 checks passed before disconnect.')

    const banner = mount(PeerOfflineShadowBanner, {
      props: {
        deviceId: 'dev-offline',
        deviceName: 'MacStudio-Worker',
        state: 'disconnected',
        lastSyncedAt: peers.activePeerLastSyncedAt.value,
      },
      global: { plugins: [i18n] },
    })
    expect(banner.get('[data-testid="peer-offline-shadow-banner"]').text()).toContain('MacStudio-Worker')
    await banner.get('[data-testid="peer-shadow-reconnect-btn"]').trigger('click')
    expect(banner.emitted('reconnect')?.[0]).toEqual(['dev-offline'])

    // Two-step confirmation for clearing local shadow cache
    await banner.get('[data-testid="peer-shadow-clear-cache-btn"]').trigger('click')
    expect(banner.emitted('clear-cache')).toBeUndefined()
    await banner.get('[data-testid="peer-shadow-confirm-clear-btn"]').trigger('click')
    expect(banner.emitted('clear-cache')?.[0]).toEqual(['dev-offline'])
  })

  it('maps remote DevServer port into PreviewBrowserPanel only when peer endpoint is active and releases on close', async () => {
    const calls: Array<{ url: string; method: string; body?: unknown }> = []

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        const method = (init?.method ?? 'GET').toUpperCase()
        const body = typeof init?.body === 'string' && init.body ? JSON.parse(init.body) : undefined
        calls.push({ url, method, body })

        if (url.endsWith('/api/v1/peers/devices/dev-vite/devserver-lease') && method === 'POST') {
          return new Response(
            JSON.stringify({
              lease_id: 'lease-5173',
              device_id: 'dev-vite',
              target_port: 5173,
              local_port: 59123,
              preview_url: 'http://127.0.0.1:59123',
              ttl_seconds: 1800,
              expires_at_ms: Date.now() + 1_800_000,
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-vite/devserver-lease/lease-5173') && method === 'DELETE') {
          return new Response(
            JSON.stringify({ released: true, device_id: 'dev-vite', lease_id: 'lease-5173' }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        return new Response(JSON.stringify({}), { status: 200, headers: { 'content-type': 'application/json' } })
      }),
    )

    const peers = usePeerDevices()
    peers.devices.value = [
      {
        device_id: 'dev-vite',
        display_name: 'RemoteViteBox',
        transport: 'lan',
        address: '192.168.1.44:48735',
        remote_device_id: 'rem-vite',
        certificate_fingerprint: 'a'.repeat(64),
        protocol_major: 1,
        protocol_minor: 0,
        paired_at: Date.now(),
        last_connected_at: Date.now(),
        connection_desired: true,
        adopt_sessions: true,
        last_state: 'ready',
        last_failure_code: null,
        access_revision: '1',
      },
    ]

    // Local endpoint: remote mapping bar is hidden
    const panel = mount(PreviewBrowserPanel, { global: { plugins: [i18n] } })
    expect(panel.find('[data-testid="preview-peer-lease-bar"]').exists()).toBe(false)

    // Switch to peer endpoint: remote mapping bar appears
    peers.switchEndpoint({ kind: 'peer', deviceId: 'dev-vite' })
    await panel.vm.$nextTick()
    expect(panel.find('[data-testid="preview-peer-lease-bar"]').exists()).toBe(true)

    await panel.get('[data-testid="preview-map-remote-btn"]').trigger('click')
    await vi.waitFor(() => {
      expect(panel.find('[data-testid="preview-active-lease-pill"]').exists()).toBe(true)
    })
    expect(panel.find('iframe.preview-iframe').attributes('src')).toBe('http://127.0.0.1:59123')
    expect(calls[0]).toMatchObject({
      url: 'http://127.0.0.1:48730/api/v1/peers/devices/dev-vite/devserver-lease',
      method: 'POST',
      body: { target_port: 5173 },
    })

    // Release lease closes mapping and clears mapped iframe URL
    await panel.get('[data-testid="preview-release-lease-btn"]').trigger('click')
    await vi.waitFor(() => {
      expect(panel.find('[data-testid="preview-active-lease-pill"]').exists()).toBe(false)
    })
    expect(calls[1]).toMatchObject({
      url: 'http://127.0.0.1:48730/api/v1/peers/devices/dev-vite/devserver-lease/lease-5173',
      method: 'DELETE',
    })
    panel.unmount()
  })
})
