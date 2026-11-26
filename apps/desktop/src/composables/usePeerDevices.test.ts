// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { formatPeerFingerprint, resetPeerDevicesStateForTests, usePeerDevices } from './usePeerDevices'
import { setHostAccessStatus } from '@/lib/hostAccess'
import { cachedRead, invalidateReadCache, peekReadCacheKeys } from '@/lib/readCache'
import { registerProjectStorage, captureStorageId, setSelectedStorage } from '@/lib/storageScope'
import { writeEndpointEventCursor, readEndpointEventCursor, resetEndpointScopeForTests } from '@/lib/endpointScope'

const originalTinadec = window.tinadec

const SAMPLE_FP = 'a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90'

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
  resetPeerDevicesStateForTests()
  setSelectedStorage('user')
  invalidateReadCache()
})

afterEach(() => {
  vi.unstubAllGlobals()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: originalTinadec })
  resetEndpointScopeForTests()
  resetPeerDevicesStateForTests()
  invalidateReadCache()
})

describe('usePeerDevices state & lifecycle', () => {
  it('starts closed by default and formats 64-char SHA-256 fingerprints into 4-char blocks', () => {
    const peers = usePeerDevices()
    expect(peers.inboundSettings.value.enabled).toBe(false)
    expect(peers.inboundSettings.value.listen_port).toBe(48735)
    expect(peers.activeEndpoint.value).toEqual({ kind: 'local' })
    expect(peers.activeEndpointId.value).toBe('local')
    expect(formatPeerFingerprint(SAMPLE_FP)).toBe(
      'a1b2 c3d4 e5f6 0718 293a 4b5c 6d7e 8f90 a1b2 c3d4 e5f6 0718 293a 4b5c 6d7e 8f90',
    )
  })

  it('manages inbound settings, pairing codes, probe/pair, grants, mounted workspaces, cache cleanup, and endpoint switching', async () => {
    const calls: Array<{ url: string; method: string; body?: unknown }> = []

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input)
        const method = (init?.method ?? 'GET').toUpperCase()
        const body = typeof init?.body === 'string' && init.body ? JSON.parse(init.body) : undefined
        calls.push({ url, method, body })

        if (url.endsWith('/api/v1/peers/inbound') && method === 'GET') {
          return new Response(
            JSON.stringify({
              enabled: false,
              listening: false,
              display_name: 'Dev-Workstation',
              listen_port: 48735,
              local_device_id: 'local-dev-01',
              local_fingerprint: SAMPLE_FP,
              reachable_addresses: ['192.168.1.10:48735', '100.64.0.8:48735'],
              firewall_hint: null,
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/inbound') && method === 'PUT') {
          return new Response(
            JSON.stringify({
              enabled: Boolean((body as Record<string, unknown>)?.enabled),
              listening: Boolean((body as Record<string, unknown>)?.enabled),
              display_name: ((body as Record<string, unknown>)?.display_name as string) ?? 'Dev-Workstation',
              listen_port: 48736,
              configured_port: 48735,
              port_conflict: true,
              local_device_id: 'local-dev-01',
              local_fingerprint: SAMPLE_FP,
              reachable_addresses: ['192.168.1.10:48736'],
              firewall_hint: 'Windows Defender Firewall rule required',
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/pairing-code') && method === 'GET') {
          return new Response(JSON.stringify({ active: false }), {
            status: 200,
            headers: { 'content-type': 'application/json' },
          })
        }
        if (url.endsWith('/api/v1/peers/pairing-code') && method === 'POST') {
          return new Response(
            JSON.stringify({
              active: true,
              pairing_code: '842917',
              expires_at_ms: Date.now() + 300_000,
              remaining_attempts: 5,
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/pairing-code') && method === 'DELETE') {
          return new Response('', { status: 204 })
        }
        if (url.endsWith('/api/v1/peers/devices') && method === 'GET') {
          return new Response(
            JSON.stringify({
              devices: [
                {
                  device_id: 'dev-mac-1',
                  display_name: 'MacBuildBox',
                  transport: 'lan',
                  address: '192.168.1.25:48735',
                  remote_device_id: 'remote-mac-uuid',
                  certificate_fingerprint: SAMPLE_FP,
                  protocol_major: 1,
                  protocol_minor: 0,
                  paired_at: 1700000000000,
                  last_connected_at: 1700000050000,
                  connection_desired: 1,
                  adopt_sessions: 1,
                  last_state: 'ready',
                  last_failure_code: null,
                  access_revision: 'rev-1',
                },
              ],
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/probe') && method === 'POST') {
          return new Response(
            JSON.stringify({
              reachable: true,
              address: '192.168.1.30:48735',
              remote_device_id: 'remote-linux-uuid',
              display_name: 'LinuxRunner',
              certificate_fingerprint: SAMPLE_FP,
              protocol_major: 1,
              protocol_minor: 0,
              capabilities: ['proxy_v1'],
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/pair') && method === 'POST') {
          return new Response(
            JSON.stringify({
              device_id: 'dev-linux-2',
              display_name: 'LinuxRunner',
              transport: 'lan',
              address: '192.168.1.30:48735',
              remote_device_id: 'remote-linux-uuid',
              certificate_fingerprint: SAMPLE_FP,
              protocol_major: 1,
              protocol_minor: 0,
              paired_at: 1700000100000,
              last_connected_at: 1700000100000,
              connection_desired: true,
              adopt_sessions: false,
              last_state: 'ready',
              last_failure_code: null,
              access_revision: 'rev-1',
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-mac-1/disconnect') && method === 'POST') {
          return new Response(
            JSON.stringify({
              device_id: 'dev-mac-1',
              display_name: 'MacBuildBox',
              transport: 'lan',
              address: '192.168.1.25:48735',
              remote_device_id: 'remote-mac-uuid',
              certificate_fingerprint: SAMPLE_FP,
              protocol_major: 1,
              protocol_minor: 0,
              paired_at: 1700000000000,
              last_connected_at: 1700000050000,
              connection_desired: false,
              adopt_sessions: true,
              last_state: 'disconnected',
              last_failure_code: null,
              access_revision: 'rev-1',
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-mac-1/connect') && method === 'POST') {
          return new Response(
            JSON.stringify({
              device_id: 'dev-mac-1',
              display_name: 'MacBuildBox',
              transport: 'lan',
              address: '192.168.1.25:48735',
              remote_device_id: 'remote-mac-uuid',
              certificate_fingerprint: SAMPLE_FP,
              protocol_major: 1,
              protocol_minor: 0,
              paired_at: 1700000000000,
              last_connected_at: 1700000090000,
              connection_desired: true,
              adopt_sessions: true,
              last_state: 'ready',
              last_failure_code: null,
              access_revision: 'rev-2',
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-mac-1') && method === 'PATCH') {
          return new Response(
            JSON.stringify({
              device_id: 'dev-mac-1',
              display_name: 'MacBuildBox-Renamed',
              transport: 'lan',
              address: '192.168.1.25:48735',
              remote_device_id: 'remote-mac-uuid',
              certificate_fingerprint: SAMPLE_FP,
              protocol_major: 1,
              protocol_minor: 0,
              paired_at: 1700000000000,
              last_connected_at: 1700000090000,
              connection_desired: true,
              adopt_sessions: true,
              last_state: 'ready',
              last_failure_code: null,
              access_revision: 'rev-2',
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-mac-1/workspaces') && method === 'GET') {
          return new Response(
            JSON.stringify({
              workspaces: [
                {
                  remote_project_id: 'proj-ios',
                  name: 'iOSApp',
                  remote_root_path: '/Users/dev/iOSApp',
                  mounted: true,
                  alias_workspace_id: 'alias-ios',
                },
                {
                  remote_project_id: 'proj-secret',
                  name: 'UnmountedRepo',
                  remote_root_path: '/Users/dev/Secret',
                  mounted: false,
                },
              ],
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-mac-1/workspaces/mount') && method === 'PUT') {
          return new Response(
            JSON.stringify({
              workspaces: [
                {
                  remote_project_id: 'proj-ios',
                  name: 'iOSApp',
                  remote_root_path: '/Users/dev/iOSApp',
                  mounted: true,
                  alias_workspace_id: 'alias-ios',
                },
                {
                  remote_project_id: 'proj-secret',
                  name: 'UnmountedRepo',
                  remote_root_path: '/Users/dev/Secret',
                  mounted: true,
                  alias_workspace_id: 'alias-secret',
                },
              ],
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/devices/dev-mac-1/local-cache') && method === 'DELETE') {
          return new Response('', { status: 204 })
        }
        if (url.endsWith('/api/v1/peers/devices/dev-mac-1') && method === 'DELETE') {
          return new Response('', { status: 204 })
        }
        if (url.endsWith('/api/v1/peers/grants') && method === 'GET') {
          return new Response(
            JSON.stringify({
              grants: [
                {
                  grant_id: 'g-1',
                  controller_device_id: 'ctrl-laptop-1',
                  display_name: 'OfficeLaptop',
                  transport: 'lan',
                  certificate_fingerprint: SAMPLE_FP,
                  granted_at: 1700000000000,
                  last_controlled_at: 1700000040000,
                  revoked_at: null,
                  policy: {
                    controller_device_id: 'ctrl-laptop-1',
                    role: 'operator',
                    workspace_mode: 'all',
                    workspace_ids: [],
                    allow_non_workspace_sessions: false,
                    sync_all_sessions: false,
                    allow_agent_delegation: false,
                    allow_provider_proxy: false,
                    revision: '1',
                  },
                },
              ],
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/grants/ctrl-laptop-1/policy') && method === 'PUT') {
          return new Response(
            JSON.stringify({
              grant_id: 'g-1',
              controller_device_id: 'ctrl-laptop-1',
              display_name: 'OfficeLaptop',
              transport: 'lan',
              certificate_fingerprint: SAMPLE_FP,
              granted_at: 1700000000000,
              last_controlled_at: 1700000040000,
              revoked_at: null,
              policy: {
                controller_device_id: 'ctrl-laptop-1',
                role: 'observer',
                workspace_mode: 'selected',
                workspace_ids: ['proj-allowed'],
                allow_non_workspace_sessions: false,
                sync_all_sessions: true,
                allow_agent_delegation: false,
                allow_provider_proxy: false,
                revision: '2',
              },
            }),
            { status: 200, headers: { 'content-type': 'application/json' } },
          )
        }
        if (url.endsWith('/api/v1/peers/grants/ctrl-laptop-1') && method === 'DELETE') {
          return new Response('', { status: 204 })
        }
        return new Response(JSON.stringify({}), { status: 200, headers: { 'content-type': 'application/json' } })
      }),
    )

    const peers = usePeerDevices()
    await peers.refreshAll()
    expect(peers.inboundSettings.value.local_device_id).toBe('local-dev-01')
    expect(peers.inboundSettings.value.reachable_addresses).toEqual(['192.168.1.10:48735', '100.64.0.8:48735'])
    expect(peers.pairingCode.value).toBeNull()
    expect(peers.devices.value).toHaveLength(1)
    expect(peers.mountedWorkspaces.value).toHaveLength(1)
    expect(peers.mountedWorkspaces.value[0]).toMatchObject({
      alias_workspace_id: 'alias-ios',
      peer_device_id: 'dev-mac-1',
      device_display_name: 'MacBuildBox',
      offline_readonly: false,
    })

    // Inbound update + auto port conflict reflection
    const updatedInbound = await peers.updateInbound({ enabled: true, display_name: 'StudioHost' })
    expect(updatedInbound.enabled).toBe(true)
    expect(updatedInbound.listen_port).toBe(48736)
    expect(updatedInbound.port_conflict).toBe(true)

    // Generate & revoke 6-digit pairing code
    const code = await peers.generatePairingCode()
    expect(code.pairing_code).toBe('842917')
    expect(peers.pairingCode.value?.pairing_code).toBe('842917')
    await peers.revokePairingCode()
    expect(peers.pairingCode.value).toBeNull()

    // Probe & pair new remote device
    const probed = await peers.probeDevice('192.168.1.30:48735')
    expect(probed.certificate_fingerprint).toBe(SAMPLE_FP)
    const paired = await peers.pairDevice({
      address: '192.168.1.30:48735',
      pairing_code: '842917',
      expected_fingerprint: SAMPLE_FP,
      display_name: 'LinuxRunner',
    })
    expect(paired.device_id).toBe('dev-linux-2')
    expect(peers.devices.value).toHaveLength(2)

    // Switch active endpoint to dev-mac-1; management plane MUST still call local /api/v1/peers/* without proxy!
    peers.switchEndpoint({ kind: 'peer', deviceId: 'dev-mac-1' })
    expect(peers.activeEndpointId.value).toBe('peer:dev-mac-1')
    expect(peers.activePeerDevice.value?.display_name).toBe('MacBuildBox')

    // Disconnect flips mountedWorkspaces to offline_readonly = true
    await peers.disconnectDevice('dev-mac-1')
    expect(peers.mountedWorkspaces.value[0]?.offline_readonly).toBe(true)
    await peers.connectDevice('dev-mac-1')
    expect(peers.mountedWorkspaces.value[0]?.offline_readonly).toBe(false)

    // Mount second workspace & rename device
    await peers.mountRemoteWorkspaces('dev-mac-1', [
      { remote_project_id: 'proj-ios', mounted: true },
      { remote_project_id: 'proj-secret', mounted: true },
    ])
    expect(peers.mountedWorkspaces.value).toHaveLength(2)
    await peers.updateDevice('dev-mac-1', { display_name: 'MacBuildBox-Renamed' })
    expect(peers.mountedWorkspaces.value[0]?.device_display_name).toBe('MacBuildBox-Renamed')

    // Seed local & peer caches to verify clearDeviceLocalCache isolates cleanup to dev-mac-1 ONLY
    await cachedRead('models', async () => ['local-m'], undefined, 3000, { endpoint: 'local', storageId: 'user' })
    await cachedRead('models', async () => ['mac-m'], undefined, 3000, { endpoint: 'peer:dev-mac-1', storageId: 'user' })
    registerProjectStorage('p-same', 'local-scope', 'local')
    registerProjectStorage('p-same', 'mac-scope', 'peer:dev-mac-1')
    writeEndpointEventCursor('peer:dev-mac-1', 'user', '/api/v1/events', {
      watermarks: {},
      lastFrameId: '10',
      afterSeq: 10,
    })

    await peers.clearDeviceLocalCache('dev-mac-1')
    expect(peekReadCacheKeys()).toEqual(['local:user:models'])
    expect(captureStorageId('/api/v1/projects/p-same', { endpointId: 'local' })).toBe('local-scope')
    expect(readEndpointEventCursor('peer:dev-mac-1', 'user', '/api/v1/events')).toBeUndefined()
    // Device pairing itself is NOT deleted by clearDeviceLocalCache
    expect(peers.devices.value.some((d) => d.device_id === 'dev-mac-1')).toBe(true)

    // Grants policy update & revoke
    const updatedGrant = await peers.updateGrantPolicy('ctrl-laptop-1', {
      role: 'observer',
      workspace_mode: 'selected',
      workspace_ids: ['proj-allowed'],
      sync_all_sessions: true,
    })
    expect(updatedGrant.policy.role).toBe('observer')
    expect(updatedGrant.policy.workspace_ids).toEqual(['proj-allowed'])
    await peers.revokeGrant('ctrl-laptop-1')
    expect(peers.grants.value).toHaveLength(0)

    // Deleting active peer device resets activeEndpoint back to local
    await peers.deleteDevice('dev-mac-1')
    expect(peers.activeEndpoint.value).toEqual({ kind: 'local' })
    expect(peers.devices.value.map((d) => d.device_id)).toEqual(['dev-linux-2'])

    // Verify zero double-proxying on management plane calls even while activeEndpoint was peer:dev-mac-1
    for (const c of calls) {
      expect(c.url).not.toContain('/proxy/api/v1/peers')
    }
  })
})
