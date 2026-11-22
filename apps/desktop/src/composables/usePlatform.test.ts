// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { __resetPlatformForTests, initPlatform, usePlatform } from './usePlatform'
import { setHostAccessStatus } from '@/lib/hostAccess'
import CompanionContactsRail from '@/components/companion/CompanionContactsRail.vue'
import DetachedPanelPage from '@/pages/DetachedPanelPage.vue'
import detachedPanelSource from '@/pages/DetachedPanelPage.vue?raw'
import { createUie, __resetUieForTests } from '../../../TinadecUI/src/components/useUie'
import { createCardRegistry } from '../../../TinadecUI/src/engine/registry'

vi.mock('vue-router', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-router')>()),
  useRoute: () => ({
    query: { tabId: 'tab-1', type: 'events', title: 'Events', state: '{}' },
  }),
}))

vi.mock('vue-i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key }),
}))

const mountOptions = {
  global: {
    stubs: {
      BrandLogo: { name: 'BrandLogo', template: '<svg class="brand-logo" aria-hidden="true" />' },
    },
  },
}

describe('usePlatform', () => {
  const originalHref = window.location.href

  afterEach(() => {
    vi.unstubAllGlobals()
    window.history.replaceState({}, '', originalHref)
    setHostAccessStatus({ state: 'ready', managed: true })
    __resetPlatformForTests()
  })

  it('resolves Electron darwin platform, modKey ⌘, and syncs root dataset attributes', () => {
    vi.stubGlobal('tinadec', {
      platform: 'darwin',
      minimizeWindow: vi.fn(),
      isFullScreen: vi.fn().mockResolvedValue(false),
      onFullScreenChanged: vi.fn(() => () => {}),
    })

    const { platform, isMac, isElectronMac, isFullScreen, modKey } = usePlatform()
    expect(platform.value).toBe('darwin')
    expect(isMac.value).toBe(true)
    expect(isElectronMac.value).toBe(true)
    expect(isFullScreen.value).toBe(false)
    expect(modKey.value).toBe('⌘')
    expect(document.documentElement.dataset.platform).toBe('darwin')
    expect(document.documentElement.dataset.fullscreen).toBe('false')
  })

  it('resolves Electron win32 platform with Ctrl+ modKey', () => {
    vi.stubGlobal('tinadec', {
      platform: 'win32',
      minimizeWindow: vi.fn(),
    })

    const { platform, isMac, isElectronMac, modKey } = usePlatform()
    expect(platform.value).toBe('win32')
    expect(isMac.value).toBe(false)
    expect(isElectronMac.value).toBe(false)
    expect(modKey.value).toBe('Ctrl+')
    expect(document.documentElement.dataset.platform).toBe('win32')
  })

  it('honors URL query overrides ?platform=darwin&fullscreen=true for browser previews', () => {
    window.history.replaceState({}, '', '/?platform=darwin&fullscreen=true')
    setHostAccessStatus({ state: 'preview', managed: false })

    const { platform, isMac, isElectronMac, isFullScreen, modKey } = usePlatform()
    expect(platform.value).toBe('darwin')
    expect(isMac.value).toBe(true)
    expect(isElectronMac.value).toBe(true)
    expect(isFullScreen.value).toBe(true)
    expect(modKey.value).toBe('⌘')
    expect(document.documentElement.dataset.platform).toBe('darwin')
    expect(document.documentElement.dataset.fullscreen).toBe('true')
  })

  it('tracks initial isFullScreen() and onFullScreenChanged IPC events', async () => {
    let listener: ((fs: boolean) => void) | undefined
    vi.stubGlobal('tinadec', {
      platform: 'darwin',
      minimizeWindow: vi.fn(),
      isFullScreen: vi.fn().mockResolvedValue(true),
      onFullScreenChanged: vi.fn((cb: (fs: boolean) => void) => {
        listener = cb
        return () => {
          listener = undefined
        }
      }),
    })

    const dispose = initPlatform()
    const { isFullScreen } = usePlatform()

    await Promise.resolve()
    await Promise.resolve()
    expect(isFullScreen.value).toBe(true)
    expect(document.documentElement.dataset.fullscreen).toBe('true')

    listener?.(false)
    expect(isFullScreen.value).toBe(false)
    expect(document.documentElement.dataset.fullscreen).toBe('false')

    dispose()
    expect(listener).toBeUndefined()
  })

  it('keeps pure web layout non-darwin even on a macOS browser while preserving ⌘ modKey', () => {
    vi.stubGlobal('navigator', {
      platform: 'MacIntel',
      userAgent: 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15',
    })

    const { platform, isMac, isElectronMac, modKey } = usePlatform()
    expect(platform.value).toBe('web')
    expect(isElectronMac.value).toBe(false)
    expect(isMac.value).toBe(true)
    expect(modKey.value).toBe('⌘')
    expect(document.documentElement.dataset.platform).toBe('web')
  })

  it('reactively updates useUie collapsed left.topInset when dataset.platform and dataset.fullscreen change', async () => {
    __resetUieForTests()
    vi.stubGlobal('tinadec', { platform: 'darwin', minimizeWindow: vi.fn() })
    usePlatform()

    const reg = createCardRegistry()
    reg.register({
      type: 'nav',
      component: {} as never,
      minWidth: 220,
      minHeight: 120,
      singleton: true,
      movable: false,
      closable: false,
      detachable: false,
      defaultTitle: 'Nav',
    })
    const uie = createUie({ registry: reg })
    expect(uie.geometry.value.columns.left.topInset).toBe(8)

    uie.dispatch({
      command: { type: 'collapseColumn', scope: uie.scope.value, slotId: 'left', collapsed: true },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
    expect(uie.geometry.value.columns.left.topInset).toBe(48)

    // Enter macOS native fullscreen: traffic lights retract -> left.topInset returns to 8
    document.documentElement.dataset.fullscreen = 'true'
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(uie.geometry.value.columns.left.topInset).toBe(8)

    // Leave macOS native fullscreen: collapsed rail drops back to topInset 48
    document.documentElement.dataset.fullscreen = 'false'
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(uie.geometry.value.columns.left.topInset).toBe(48)

    __resetUieForTests()
  })

  it('adapts CompanionContactsRail top bar across win32, darwin non-fullscreen, and darwin fullscreen', () => {
    vi.stubGlobal('tinadec', { platform: 'win32', minimizeWindow: vi.fn() })
    const winWrapper = mount(CompanionContactsRail, mountOptions)
    expect(winWrapper.find('.companion-rail-brand').exists()).toBe(true)
    expect(winWrapper.get('[data-testid="companion-rail-top"]').classes()).not.toContain('companion-rail-top--mac-lights')
    winWrapper.unmount()

    vi.stubGlobal('tinadec', { platform: 'darwin', minimizeWindow: vi.fn() })
    const macWrapper = mount(CompanionContactsRail, mountOptions)
    expect(macWrapper.find('.companion-rail-brand').exists()).toBe(false)
    expect(macWrapper.get('[data-testid="companion-rail-top"]').classes()).toContain('companion-rail-top--mac-lights')
    macWrapper.unmount()

    window.history.replaceState({}, '', '/?platform=darwin&fullscreen=true')
    const fsWrapper = mount(CompanionContactsRail, mountOptions)
    expect(fsWrapper.find('.companion-rail-brand').exists()).toBe(false)
    expect(fsWrapper.get('[data-testid="companion-rail-top"]').classes()).toContain('companion-rail-top--mac-fullscreen')
    expect(fsWrapper.get('[data-testid="companion-rail-top"]').classes()).not.toContain('companion-rail-top--mac-lights')
    fsWrapper.unmount()
  })

  it('polishes DetachedPanelPage header (38px blurred island, icon+text reattach pill, darwin traffic-light inset)', () => {
    expect(detachedPanelSource).toContain('height: 38px;')
    expect(detachedPanelSource).toContain('backdrop-filter: blur(16px);')
    expect(detachedPanelSource).toContain('border-bottom: none;')

    vi.stubGlobal('tinadec', {
      platform: 'win32',
      minimizeWindow: vi.fn(),
      maximizeWindow: vi.fn(),
      closeWindow: vi.fn(),
      reattachPanel: vi.fn(),
    })
    const winPanel = mount(DetachedPanelPage, mountOptions)
    expect(winPanel.findAll('.detached-titlebar-btn')).toHaveLength(4)
    expect(winPanel.get('[data-testid="detached-reattach-btn"]').text()).toContain('context.reattachTab')
    expect(winPanel.find('.brand-logo').exists()).toBe(false)
    winPanel.unmount()

    vi.stubGlobal('tinadec', {
      platform: 'darwin',
      minimizeWindow: vi.fn(),
      maximizeWindow: vi.fn(),
      closeWindow: vi.fn(),
      reattachPanel: vi.fn(),
    })
    const macPanel = mount(DetachedPanelPage, mountOptions)
    expect(macPanel.findAll('.detached-titlebar-btn')).toHaveLength(1)
    expect(macPanel.get('[data-testid="detached-reattach-btn"]').text()).toContain('context.reattachTab')
    expect(macPanel.get('[data-testid="detached-titlebar"]').classes()).toContain('detached-titlebar--mac-lights')
    expect(macPanel.find('.brand-logo').exists()).toBe(false)
    macPanel.unmount()

    window.history.replaceState({}, '', '/?platform=darwin&fullscreen=true')
    const macFsPanel = mount(DetachedPanelPage, mountOptions)
    expect(macFsPanel.get('[data-testid="detached-titlebar"]').classes()).toContain('detached-titlebar--mac')
    expect(macFsPanel.get('[data-testid="detached-titlebar"]').classes()).not.toContain('detached-titlebar--mac-lights')
    macFsPanel.unmount()
  })
})
