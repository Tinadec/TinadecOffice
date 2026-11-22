import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  canPickHostDirectories,
  getWebUploadLimitWarning,
  hasServerDirectoryPicker,
  hostBridgeFailure,
  isElectronHost,
  pickCompanionAvatarDirectory,
  pickHostFiles,
  pickProjectDirectory,
  pickWorkspaceFolders,
  probeWebAuthStatus,
  readHostStatus,
  registerServerDirectoryPicker,
  retryHostStatus,
  toWebSocketUrl,
  WEB_PROXY_UPLOAD_LIMIT_BYTES,
  type ServerDirectoryPickerOptions,
} from './hostConnection'

afterEach(() => {
  registerServerDirectoryPicker(null)
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

    vi.stubGlobal('window', { tinadec: { isPreviewShim: true, gatewayUrl: () => 'http://127.0.0.1:48730' } })
    expect(isElectronHost()).toBe(false)
  })

  it('routes unified directory & file pickers through the built-in modal with Electron-only nativeFallback', async () => {
    const nativeFolders = vi.fn(async () => ['C:/native-workspace'])
    const nativeAvatar = vi.fn(async () => 'C:/native-avatar')
    const nativeProject = vi.fn(async () => 'C:/native-project')
    const frozenBridge = Object.freeze({
      gatewayUrl: () => 'http://127.0.0.1:48730',
      selectWorkspaceFolders: nativeFolders,
      selectCompanionAvatarDirectory: nativeAvatar,
      openProjectDialog: nativeProject,
    })
    vi.stubGlobal('window', { tinadec: frozenBridge })

    const captured: ServerDirectoryPickerOptions[] = []
    registerServerDirectoryPicker(async (opts) => {
      captured.push(opts)
      return opts.nativeFallback ? opts.nativeFallback() : ['C:/builtin']
    })

    expect(hasServerDirectoryPicker()).toBe(true)
    expect(canPickHostDirectories()).toBe(true)

    expect(await pickWorkspaceFolders('Pick WS')).toEqual(['C:/native-workspace'])
    expect(await pickProjectDirectory('Pick Proj')).toBe('C:/native-project')
    expect(await pickCompanionAvatarDirectory('Pick Avatar')).toBe('C:/native-avatar')
    expect(await pickHostFiles({
      title: 'Pick Image',
      acceptExtensions: ['.png'],
      nativeFallback: async () => ['C:/bg.png'],
    })).toEqual(['C:/bg.png'])

    expect(captured[0]).toMatchObject({ mode: 'directory', multiple: true, title: 'Pick WS' })
    expect(typeof captured[0]?.nativeFallback).toBe('function')
    expect(captured[1]).toMatchObject({ mode: 'directory', multiple: false, title: 'Pick Proj' })
    expect(captured[2]).toMatchObject({ mode: 'directory', multiple: false, title: 'Pick Avatar' })
    expect(captured[3]).toMatchObject({ mode: 'file', multiple: false, acceptExtensions: ['.png'] })
    expect(typeof captured[3]?.nativeFallback).toBe('function')
  })

  it('never exposes nativeFallback in Web or previewShim mode and falls back cleanly in standalone unit tests', async () => {
    const captured: ServerDirectoryPickerOptions[] = []
    registerServerDirectoryPicker(async (opts) => {
      captured.push(opts)
      return ['/srv/project']
    })

    // WebShim: no nativeFallback passed to picker.
    vi.stubGlobal('window', {
      tinadec: {
        isWebShim: true,
        selectWorkspaceFolders: vi.fn(async () => ['/wrong']),
      },
    })
    expect(await pickWorkspaceFolders('Web WS')).toEqual(['/srv/project'])
    expect(captured[0]?.nativeFallback).toBeUndefined()

    // PreviewShim: never mistaken for real Electron dialog.
    vi.stubGlobal('window', {
      tinadec: {
        isPreviewShim: true,
        selectWorkspaceFolders: vi.fn(() => Promise.reject(new Error('preview'))),
      },
    })
    expect(await pickWorkspaceFolders('Preview WS')).toEqual(['/srv/project'])
    expect(captured[1]?.nativeFallback).toBeUndefined()
    expect(await pickHostFiles({ nativeFallback: async () => ['/nope'] })).toEqual(['/srv/project'])
    expect(captured[2]?.nativeFallback).toBeUndefined()

    // Standalone unit test (no ServerDirectoryPickerModal mounted): falls back cleanly to stubbed window.tinadec.
    registerServerDirectoryPicker(null)
    expect(hasServerDirectoryPicker()).toBe(false)
    expect(canPickHostDirectories()).toBe(false) // previewShim without server picker

    const stubFolders = vi.fn(async () => ['/test/dir'])
    const stubAvatar = vi.fn(async () => '/test/avatar')
    vi.stubGlobal('window', {
      tinadec: {
        selectWorkspaceFolders: stubFolders,
        selectCompanionAvatarDirectory: stubAvatar,
      },
    })
    expect(canPickHostDirectories()).toBe(true)
    expect(await pickWorkspaceFolders('Test Title')).toEqual(['/test/dir'])
    expect(stubFolders).toHaveBeenCalledWith('Test Title')
    expect(await pickCompanionAvatarDirectory('Avatar Title')).toBe('/test/avatar')
    expect(stubAvatar).toHaveBeenCalledWith('Avatar Title')
    expect(await pickHostFiles({ nativeFallback: async () => ['/test/file.png'] })).toEqual(['/test/file.png'])
  })

  it('does not throw when probeWebAuthStatus encounters a contextBridge-frozen window.tinadec', async () => {
    const frozenBridge = Object.freeze({ gatewayUrl: () => 'http://127.0.0.1:48730' })
    vi.stubGlobal('window', {
      location: { protocol: 'http:', origin: 'http://127.0.0.1:5173' },
      tinadec: frozenBridge,
    })
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

    const status = await probeWebAuthStatus('http://127.0.0.1:5173')
    expect(status?.state).toBe('ready')
  })
})
