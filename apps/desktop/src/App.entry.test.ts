// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick, ref, shallowRef, type Component } from 'vue'
import { mount } from '@vue/test-utils'
import App from './App.vue'
import { ensureGraphSeedPack } from '@/agentPacks/graphSeedPackBootstrap'

const connection = vi.hoisted(() => ({ state: { value: 'connecting' }, hostStatus: { value: { state: 'checking', managed: true } }, start: vi.fn() }))
const route = vi.hoisted(() => ({ name: 'home' as string | undefined }))
const notifications = vi.hoisted(() => ({ error: vi.fn(), dismissByKey: vi.fn() }))
vi.mock('@/composables/useConnection', () => ({
  useConnection: () => ({ connectionState: connection.state, hostStatus: connection.hostStatus, start: connection.start }),
  retryConnection: vi.fn(), CONNECTION_BANNER_KEY: 'backend',
}))
vi.mock('vue-router', () => ({
  useRoute: () => route,
  RouterView: defineComponent({ name: 'RouterView', render: () => null }),
}))
vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()), useI18n: () => ({ t: (key: string) => key }) }))
vi.mock('@/composables/useBackground', () => ({
  useBackground: () => ({ settings: ref({ type: 'none' }), applyBackground: vi.fn() }),
}))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ status: { error: notifications.error }, dismissByKey: notifications.dismissByKey }),
  startStatusSync: () => vi.fn(),
}))
vi.mock('@/agentPacks/graphSeedPackBootstrap', () => ({ ensureGraphSeedPack: vi.fn(), setGraphSeedPackTranslator: vi.fn() }))
vi.mock('@/composables/useCommandPalette', () => ({ installPaletteKeybinding: () => vi.fn() }))
vi.mock('@/components/HostAvailabilityBanner.vue', () => ({ default: defineComponent({ render: () => null }) }))
vi.mock('@/components/AppSplash.vue', () => ({ default: defineComponent({ render: () => h('div', { class: 'app-splash' }) }) }))
vi.mock('@/components/NotificationIslandHost.vue', () => ({ default: defineComponent({ render: () => h('div', { class: 'notifications' }) }) }))
vi.mock('@/components/NotificationDetailDialog.vue', () => ({ default: defineComponent({ render: () => null }) }))
vi.mock('@/components/SelectionContextMenu.vue', () => ({ default: defineComponent({ render: () => null }) }))
vi.mock('@/components/CommandPalette.vue', () => ({ default: defineComponent({ render: () => null }) }))

describe('startup handoff', () => {
  let wrapper: ReturnType<typeof mount> | undefined
  afterEach(() => { wrapper?.unmount(); vi.clearAllMocks(); window.history.replaceState(null, '', '/') })

  function start(name: string | undefined = 'home', pending = false) {
    route.name = name
    connection.state = ref('connecting')
    const page = defineComponent({ name: 'EntryPage', emits: ['ready'], render: () => h('div', { class: 'page' }) })
    const component = shallowRef<Component | undefined>(pending ? undefined : page)
    const view = defineComponent({ render() { return this.$slots.default?.({ Component: component.value }) } })
    wrapper = mount(App, { global: { stubs: { RouterView: view } } })
    return { wrapper, page, load: () => { route.name = 'home'; component.value = page } }
  }

  it('holds the same splash after health succeeds until Home has a prepared frame', async () => {
    const { wrapper, page } = start()
    const splash = wrapper.get('.app-splash').element
    expect(wrapper.find('.main-content').exists()).toBe(false)
    connection.state.value = 'connected'
    await nextTick()
    expect(wrapper.find('.page').exists()).toBe(true)
    expect(wrapper.get('.app-splash').element).toBe(splash)
    expect(wrapper.get('.app-splash').classes()).not.toContain('app-splash--leaving')
    expect(wrapper.find('.notifications').exists()).toBe(false)
    wrapper.findComponent(page).vm.$emit('ready')
    await nextTick()
    expect(wrapper.get('.app-splash').classes()).toContain('app-splash--leaving')
    expect(wrapper.get('.app-splash').element).toBe(splash)
    expect(wrapper.find('.notifications').exists()).toBe(true)
  })

  it('allows a cold settings route to dismiss startup without waiting for a Home event', async () => {
    const { wrapper } = start('settings')
    connection.state.value = 'timeout'
    await nextTick(); await nextTick()
    expect(wrapper.get('.app-splash').classes()).toContain('app-splash--leaving')
    expect(wrapper.find('.page').exists()).toBe(true)
  })

  it('does not treat an unresolved initial route comment as a ready page', async () => {
    const { wrapper, page, load } = start('pending', true)
    route.name = undefined
    connection.state.value = 'connected'
    await nextTick(); await nextTick()
    expect(wrapper.find('.page').exists()).toBe(false)
    expect(wrapper.get('.app-splash').classes()).not.toContain('app-splash--leaving')
    load()
    await nextTick()
    expect(wrapper.find('.page').exists()).toBe(true)
    expect(wrapper.get('.app-splash').classes()).not.toContain('app-splash--leaving')
    wrapper.findComponent(page).vm.$emit('ready')
    await nextTick()
    expect(wrapper.get('.app-splash').classes()).toContain('app-splash--leaving')
  })

  it('holds splash on a cold Market route until the Market canvas reports readiness', async () => {
    const { wrapper, page } = start('market')
    connection.state.value = 'connected'
    await nextTick(); await nextTick()
    expect(wrapper.find('.page').exists()).toBe(true)
    expect(wrapper.get('.app-splash').classes()).not.toContain('app-splash--leaving')
    wrapper.findComponent(page).vm.$emit('ready')
    await nextTick()
    expect(wrapper.get('.app-splash').classes()).toContain('app-splash--leaving')
  })

  it('preserves the live page and dismissed splash when the connection is retried', async () => {
    const { wrapper, page } = start()
    connection.state.value = 'connected'
    await nextTick()
    wrapper.findComponent(page).vm.$emit('ready')
    await nextTick()
    const pageElement = wrapper.get('.page').element
    connection.state.value = 'connecting'
    await nextTick()
    expect(wrapper.get('.page').element).toBe(pageElement)
    expect(wrapper.get('.app-splash').classes()).toContain('app-splash--leaving')
  })

  it('does not bootstrap packages from preview or failed host authentication', async () => {
    start()
    connection.state.value = 'preview'
    await nextTick()
    expect(ensureGraphSeedPack).not.toHaveBeenCalled()
    connection.state.value = 'host_unavailable'
    await nextTick()
    expect(ensureGraphSeedPack).not.toHaveBeenCalled()
    connection.state.value = 'host_rejected'
    await nextTick()
    expect(ensureGraphSeedPack).not.toHaveBeenCalled()
    connection.state.value = 'host_restart_required'
    await nextTick()
    expect(ensureGraphSeedPack).not.toHaveBeenCalled()
  })
  it('clears backend alerts for a host version mismatch without reporting a backend failure', async () => {
    const { wrapper, page } = start()
    connection.state.value = 'host_restart_required'
    await nextTick()
    expect(wrapper.find('.page').exists()).toBe(true)
    expect(notifications.dismissByKey).toHaveBeenLastCalledWith('backend')
    expect(notifications.error).not.toHaveBeenCalled()
    expect(ensureGraphSeedPack).not.toHaveBeenCalled()
    wrapper.findComponent(page).vm.$emit('ready'); await nextTick()
    const pageElement = wrapper.get('.page').element
    connection.state.value = 'timeout'; await nextTick()
    expect(notifications.error).toHaveBeenCalledOnce()
    notifications.error.mockClear(); notifications.dismissByKey.mockClear()
    connection.state.value = 'host_restart_required'; await nextTick()
    expect(notifications.dismissByKey).toHaveBeenCalledWith('backend')
    expect(notifications.error).not.toHaveBeenCalled()
    expect(wrapper.get('.page').element).toBe(pageElement)
  })
  it('bootstraps once on authenticated startup and preserves failures across reconnect', async () => {
    start()
    connection.state.value = 'connected'
    await nextTick()
    expect(ensureGraphSeedPack).toHaveBeenCalledTimes(1)
    connection.state.value = 'host_unavailable'
    await nextTick()
    connection.state.value = 'connected'
    await nextTick()
    expect(ensureGraphSeedPack).toHaveBeenCalledTimes(1)
  })

  it('child windows skip startup splash while subscribing to host readiness', () => {
    window.history.replaceState(null, '', '/?splash=0#/panel')
    const { wrapper } = start('panel')
    expect(wrapper.find('.app-splash').exists()).toBe(false)
    expect(wrapper.find('.page').exists()).toBe(true)
    expect(connection.start).toHaveBeenCalledTimes(1)
    expect(ensureGraphSeedPack).not.toHaveBeenCalled()
  })
})
