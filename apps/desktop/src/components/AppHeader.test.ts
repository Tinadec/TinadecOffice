// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import AppHeader from './AppHeader.vue'
import BrandLogo from './BrandLogo.vue'
import CommandPaletteButton from './CommandPaletteButton.vue'
import TinadecCalligraphy from './TinadecCalligraphy.vue'
import { UiButton } from './ui'
import { __resetPlatformForTests } from '@/composables/usePlatform'
import { setHostAccessStatus } from '@/lib/hostAccess'

const mountOptions = {
  global: {
    // BrandLogo uses `<template vapor>`, which hits the Vue 3.6-rc CJS/ESM interop split under Vitest.
    stubs: {
      BrandLogo: { name: 'BrandLogo', template: '<svg class="brand-logo" aria-hidden="true" />' },
    },
  },
}

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key }),
}))

describe('AppHeader command palette entry', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    __resetPlatformForTests()
  })

  // What the entry says and does is the shared button's own contract
  // (`CommandPaletteButton.test.ts`); this file only proves the live window chrome
  // actually renders it — the defect being guarded against is a header that quietly
  // drops the button, or re-inlines a second copy of its markup.
  it('renders the shared palette entry, not a private copy of it', () => {
    const wrapper = mount(AppHeader, mountOptions)

    expect(wrapper.findComponent(CommandPaletteButton).exists()).toBe(true)
    expect(wrapper.findAllComponents(CommandPaletteButton)).toHaveLength(1)
    expect(wrapper.find('[data-testid="macos-topbar-brand"]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('AppHeader window controls', () => {
  const originalHref = window.location.href

  afterEach(() => {
    vi.unstubAllGlobals()
    window.history.replaceState({}, '', originalHref)
    setHostAccessStatus({ state: 'ready', managed: true })
    __resetPlatformForTests()
  })

  it('keeps the win32 controls accessible and sends each click to its window IPC action', async () => {
    const controls = {
      platform: 'win32',
      minimizeWindow: vi.fn(),
      maximizeWindow: vi.fn(),
      closeWindow: vi.fn(),
    }
    vi.stubGlobal('tinadec', controls)
    const wrapper = mount(AppHeader, mountOptions)

    expect(wrapper.find('[data-testid="macos-topbar-brand"]').exists()).toBe(false)

    for (const [selector, action] of [
      ['.minimize', 'minimizeWindow'],
      ['.maximize', 'maximizeWindow'],
      ['.close', 'closeWindow'],
    ] as const) {
      const button = wrapper.get(selector)
      expect(button.attributes('aria-label')).toBe(button.attributes('title'))
      expect(button.attributes('aria-label')).toBeTruthy()
      expect(button.get('svg').attributes('aria-hidden')).toBe('true')
      expect(wrapper.findComponent<typeof UiButton>(selector).props('variant')).toBe('ghost')
      await button.trigger('click')
      expect(controls[action]).toHaveBeenCalledOnce()
    }
    wrapper.unmount()
  })

  it('hides Windows min/max/close on darwin and renders exact [⌘K] -> [BrandLogo + TinadecCalligraphy] order', () => {
    vi.stubGlobal('tinadec', {
      platform: 'darwin',
      minimizeWindow: vi.fn(),
      maximizeWindow: vi.fn(),
      closeWindow: vi.fn(),
    })
    const wrapper = mount(AppHeader, mountOptions)

    expect(wrapper.find('.minimize').exists()).toBe(false)
    expect(wrapper.find('.maximize').exists()).toBe(false)
    expect(wrapper.find('.close').exists()).toBe(false)

    expect(wrapper.findComponent(BrandLogo).exists()).toBe(true)
    expect(wrapper.findComponent(TinadecCalligraphy).exists()).toBe(true)

    const controlsEl = wrapper.get('.window-controls').element
    const topChildren = Array.from(controlsEl.children) as HTMLElement[]
    expect(topChildren).toHaveLength(2)

    // 1. CommandPaletteButton (⌘K) on the LEFT
    expect(topChildren[0]?.getAttribute('data-testid')).toBe('command-palette-button')
    expect(topChildren[0]?.getAttribute('aria-keyshortcuts')).toBe('⌘K')

    // 2. Brand block on the RIGHT, with BrandLogo icon on the LEFT + TinadecCalligraphy text on the RIGHT
    expect(topChildren[1]?.getAttribute('data-testid')).toBe('macos-topbar-brand')
    const brandChildren = Array.from(topChildren[1]!.children) as SVGElement[]
    expect(brandChildren).toHaveLength(2)
    expect(brandChildren[0]?.classList.contains('brand-logo')).toBe(true)
    expect(brandChildren[1]?.classList.contains('tinadec-calligraphy')).toBe(true)

    wrapper.unmount()
  })

  it('supports ?platform=darwin URL preview override with identical chrome ordering', () => {
    window.history.replaceState({}, '', '/?platform=darwin')
    setHostAccessStatus({ state: 'preview', managed: false })
    const wrapper = mount(AppHeader, mountOptions)

    expect(wrapper.find('.minimize').exists()).toBe(false)
    const topChildren = Array.from(wrapper.get('.window-controls').element.children) as HTMLElement[]
    expect(topChildren.map((el) => el.getAttribute('data-testid'))).toEqual([
      'command-palette-button',
      'macos-topbar-brand',
    ])
    wrapper.unmount()
  })
})
