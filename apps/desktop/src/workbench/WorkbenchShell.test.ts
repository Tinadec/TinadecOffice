// @vitest-environment happy-dom

import {
  createApp,
  defineComponent,
  h,
  nextTick,
  onMounted,
  onUnmounted,
  vaporInteropPlugin,
} from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import WorkbenchShell from './WorkbenchShell.vue'
import { useWorkbenchHost, type WorkbenchHostApi } from './host'

describe('WorkbenchShell compatibility host', () => {
  let app: ReturnType<typeof createApp> | null = null
  let container: HTMLDivElement | null = null

  afterEach(() => {
    app?.unmount()
    app = null
    container?.remove()
    container = null
    vi.restoreAllMocks()
  })

  async function settle(): Promise<void> {
    await Promise.resolve()
    await nextTick()
    await new Promise<void>((resolve) => setTimeout(resolve, 0))
    await nextTick()
  }

  it('runs the Vapor workbench without changing the visible page shell', async () => {
    const load = vi.fn(async () => null)
    const save = vi.fn(async (document: unknown) => document)
    Object.defineProperty(window, 'tinadec', {
      configurable: true,
      value: {
        workbenchLayout: { load, save },
        onPanelReattach: vi.fn(() => () => undefined),
      },
    })

    let host: WorkbenchHostApi | null = null
    let homeMounts = 0
    let homeUnmounts = 0
    const HomePage = defineComponent({
      name: 'WorkbenchHostTestHome',
      setup() {
        host = useWorkbenchHost()
        onMounted(() => homeMounts++)
        onUnmounted(() => homeUnmounts++)
        return () => h('section', { 'data-test-page': 'home' }, 'Home')
      },
    })
    const SettingsPage = defineComponent({
      name: 'WorkbenchHostTestSettings',
      setup: () => () => h('section', { 'data-test-page': 'settings' }, 'Settings'),
    })
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: HomePage },
        { path: '/settings', name: 'settings', component: SettingsPage },
      ],
    })
    await router.push('/')
    await router.isReady()

    const Root = defineComponent({
      name: 'WorkbenchHostTestRoot',
      setup: () => () => h(WorkbenchShell, { transitionName: 'no-transition' }),
    })
    container = document.createElement('div')
    document.body.appendChild(container)
    app = createApp(Root)
    app.use(vaporInteropPlugin)
    app.use(router)
    app.mount(container)
    await settle()

    expect(load).toHaveBeenCalledOnce()
    expect(container.querySelector('.main-content')).not.toBeNull()
    expect(container.querySelector('[data-test-page="home"]')).not.toBeNull()
    expect(container.querySelector('.workbench-layout-toolbar')).toBeNull()
    expect(container.querySelector('.workbench-card-surface')).toBeNull()
    expect(container.querySelector('.workbench-card-grip')).toBeNull()
    expect(container.querySelector('.workbench-column-resizer')).toBeNull()
    expect(host).not.toBeNull()
    expect(host!.openCard('home.git')).toBe('home:home.git')

    await router.push('/settings')
    await settle()
    await vi.waitFor(() => {
      expect(container?.querySelector('[data-test-page="settings"]')).not.toBeNull()
    })
    expect(host!.activePage.value).toBe('settings')

    await router.push('/')
    await settle()
    await vi.waitFor(() => {
      expect(container?.querySelector('[data-test-page="home"]')).not.toBeNull()
    })
    expect(homeMounts).toBe(1)
    expect(homeUnmounts).toBe(0)
  })
})
