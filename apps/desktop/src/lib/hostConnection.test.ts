import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  getWebUploadLimitWarning,
  hostBridgeFailure,
  isElectronHost,
  probeWebAuthStatus,
  readHostStatus,
  retryHostStatus,
  toWebSocketUrl,
  WEB_PROXY_UPLOAD_LIMIT_BYTES,
} from './hostConnection'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

describe('local host bridge contract', () => {
  it.each(['status', 'retry'])('classifies the real missing %s IPC without exposing raw details', async channel => {
    const error = new Error(`Error invoking remote method 'tinadec:host-${channel}': Error: No handler registered for 'tinadec:host-${channel}'`)
    const invoke = vi.fn().mockRejectedValue(error)
    const snapshot = channel === 'status'
      ? await readHostStatus({ getHostStatus: invoke }) : await retryHostStatus({ retryHostConnection: invoke })
    expect(snapshot).toMatchObject({ state: 'restart_required', managed: true, error: { code: 'desktop_restart_required' } })
    expect(snapshot.error?.message).not.toContain('No handler')
    expect(invoke).toHaveBeenCalledTimes(1)
  })
  it('keeps permission refusals and other missing channels distinct from version mismatch', () => {
    for (const message of ['Host status requires a trusted host page.', "No handler registered for 'terminal:create'", 'private-error-value']) {
      expect(hostBridgeFailure(new Error(message))).toMatchObject({ state: 'unavailable', error: { code: 'host_bridge_unavailable' } })
      expect(hostBridgeFailure(new Error(message)).error?.message).not.toContain(message)
    }
  })
  it('requires restart for an older preload but treats an absent bridge as browser preview', async () => {
    expect(await readHostStatus({})).toMatchObject({ state: 'restart_required' })
    expect(await retryHostStatus({})).toMatchObject({ state: 'restart_required' })
    expect(await readHostStatus()).toEqual({ state: 'preview', managed: false })
  })

  it('transitions to ready or auth_required when Gateway web-auth status responds', async () => {
    vi.stubGlobal('window', { location: { protocol: 'http:', origin: 'http://127.0.0.1:5173' } })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({
      authenticated: true,
      auth_required: false,
      tier: 'loopback',
      principal: { user_id: 'owner', tenant_id: 'default', roles: ['owner'], auth_method: 'loopback_local' },
      available_providers: ['local_password'],
      allow_loopback_auto_login: true,
      password_configured: false,
      oidc_enabled: false,
      cloudflare_access_enabled: false,
      trust_proxy: 'false',
    }), { status: 200 })))
    const ready = await probeWebAuthStatus('http://127.0.0.1:5173')
    expect(ready?.state).toBe('ready')
    expect(ready?.webAuth?.tier).toBe('loopback')

    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({
      authenticated: false,
      auth_required: true,
      tier: 'remote',
      principal: null,
      available_providers: ['local_password'],
      allow_loopback_auto_login: true,
      password_configured: true,
      oidc_enabled: false,
      cloudflare_access_enabled: false,
      trust_proxy: 'cloudflare',
    }), { status: 200 })))
    const authRequired = await probeWebAuthStatus('https://tinadec.example.com')
    expect(authRequired?.state).toBe('auth_required')
    expect(authRequired?.webAuth?.tier).toBe('remote')
  })

  it('detects Electron vs WebShim, builds protocol-aware WS URLs, and warns over 100MB in Web', () => {
    expect(toWebSocketUrl('https://tinadec.example.com', '/ws/terminal?shell=bash')).toBe('wss://tinadec.example.com/ws/terminal?shell=bash')
    expect(toWebSocketUrl('http://127.0.0.1:48730', '/ws/terminal')).toBe('ws://127.0.0.1:48730/ws/terminal')

    vi.stubGlobal('window', { tinadec: { isWebShim: true } })
    expect(isElectronHost()).toBe(false)
    expect(getWebUploadLimitWarning(WEB_PROXY_UPLOAD_LIMIT_BYTES + 1)).toBeTruthy()
    expect(getWebUploadLimitWarning(1024)).toBeNull()

    vi.stubGlobal('window', { tinadec: { gatewayUrl: () => 'http://127.0.0.1:48730' } })
    expect(isElectronHost()).toBe(true)
  })
})
