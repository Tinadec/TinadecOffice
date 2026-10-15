// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ref, type Ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zh from '@/locales/zh-CN'
import type { HostConnectionStatus } from '@/lib/hostConnection'
import HostAvailabilityBanner from './HostAvailabilityBanner.vue'
import { retryConnection } from '@/composables/useConnection'
const shared = vi.hoisted(() => ({ status: null as unknown as Ref<HostConnectionStatus> }))
const originalHost = window.tinadec
vi.mock('@/lib/hostAccess', () => ({ useHostAccess: () => ({ status: shared.status }) }))
vi.mock('@/composables/useConnection', () => ({ retryConnection: vi.fn() }))
vi.mock('@/composables/usePanelStyles', () => ({ usePanelStyles: () => ({
  getPanelStyle: () => ({ backdropFilter: 'blur(8px)' }), getPanelDataAttributes: () => ({ 'data-panel-effect': 'blur' }),
}) }))
vi.mock('@/components/ui', () => ({ UiButton: { template: '<button><slot /></button>' } }))
describe('HostAvailabilityBanner', () => {
  let wrapper: ReturnType<typeof mount> | undefined
  beforeEach(() => { shared.status = ref({ state: 'ready', managed: true }); vi.clearAllMocks(); vi.stubEnv('DEV', false) })
  afterEach(() => { wrapper?.unmount(); Object.defineProperty(window, 'tinadec', { configurable: true, value: originalHost }); vi.unstubAllEnvs() })
  function render(locale = 'zh-CN') {
    wrapper = mount(HostAvailabilityBanner, { global: { plugins: [createI18n({ legacy: false, locale, fallbackLocale: 'en', messages: { en, 'zh-CN': zh } })] } })
    return wrapper
  }
  it('keeps healthy host clear and explains preview without a fake retry action', async () => {
    const w = render(); expect(w.find('aside').exists()).toBe(false)
    shared.status.value = { state: 'preview', managed: false }; await w.vm.$nextTick()
    expect(w.text()).toContain('界面预览'); expect(w.text()).toContain('不连接用户数据')
    expect(w.find('button').exists()).toBe(false)
    expect(w.get('aside').attributes('data-panel-effect')).toBe('blur')
    expect(w.get('aside').attributes('style')).toContain('blur(8px)')
  })
  it('retries a real rejected host and prevents repeated clicks while pending', async () => {
    shared.status.value = { state: 'rejected', managed: true }
    let release!: (result: boolean) => void
    vi.mocked(retryConnection).mockImplementation(() => new Promise(resolve => { release = resolve }))
    const w = render(); expect(w.text()).toContain('身份验证失败')
    await w.get('button').trigger('click'); await w.get('button').trigger('click')
    expect(retryConnection).toHaveBeenCalledTimes(1); expect(w.get('button').attributes('disabled')).toBeDefined()
    shared.status.value = { state: 'ready', managed: true }; release(true)
    await vi.waitFor(() => expect(w.find('aside').exists()).toBe(false))
  })
  it('shows the unavailable host in English and reports background verification', async () => {
    shared.status.value = { state: 'unavailable', managed: true }
    const w = render('en'); expect(w.text()).toContain('Desktop host disconnected')
    expect(w.get('button').text()).toBe('Retry host connection')
    shared.status.value = { state: 'checking', managed: true }; await w.vm.$nextTick()
    expect(w.text()).toContain('Verifying the desktop host again')
  })
  it('restarts the desktop once for a version mismatch without retrying the backend', async () => {
    shared.status.value = { state: 'restart_required', managed: true }
    let release!: () => void
    const restartApp = vi.fn(() => new Promise<void>(resolve => { release = resolve }))
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { restartApp } })
    const w = render()
    expect(w.text()).toContain('桌面与界面版本不一致')
    expect(w.get('button').text()).toBe('重启桌面应用')
    await w.get('button').trigger('click'); await w.get('button').trigger('click')
    expect(restartApp).toHaveBeenCalledOnce()
    expect(w.get('button').attributes('disabled')).toBeDefined()
    expect(w.get('button').text()).toBe('正在重启…')
    expect(retryConnection).not.toHaveBeenCalled()
    release(); await flushPromises()
    expect(w.get('button').attributes('disabled')).toBeUndefined()
  })
  it.each([
    ['zh-CN', '自动重启未完成。请关闭桌面应用，再重新打开。', '重启桌面应用'],
    ['en', 'Automatic restart did not complete. Close the desktop app and open it again.', 'Restart desktop app'],
  ])('shows local restart failure guidance in %s and keeps the action available', async (locale, guidance, action) => {
    shared.status.value = { state: 'restart_required', managed: true }
    const restartApp = vi.fn().mockRejectedValueOnce(new Error('No handler registered')).mockResolvedValueOnce(undefined)
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { restartApp } })
    const w = render(locale)
    await w.get('button').trigger('click'); await flushPromises()
    expect(w.text()).toContain(guidance)
    expect(w.get('button').text()).toBe(action)
    expect(w.get('button').attributes('disabled')).toBeUndefined()
    await w.get('button').trigger('click'); await flushPromises()
    expect(restartApp).toHaveBeenCalledTimes(2)
    expect(w.text()).not.toContain(guidance)
    expect(retryConnection).not.toHaveBeenCalled()
  })
  it.each([
    ['zh-CN', '当前桌面宿主不支持自动重启。请关闭桌面应用，再重新打开。'],
    ['en', 'This desktop host cannot restart automatically. Close the desktop app and open it again.'],
  ])('provides only manual restart guidance in %s when the host has no restart method', (locale, guidance) => {
    shared.status.value = { state: 'restart_required', managed: true }
    Object.defineProperty(window, 'tinadec', { configurable: true, value: {} })
    const w = render(locale)
    expect(w.text()).toContain(guidance)
    expect(w.find('button').exists()).toBe(false)
    expect(retryConnection).not.toHaveBeenCalled()
  })
  it.each([
    ['zh-CN', '请关闭当前开发进程，然后从仓库根目录重新运行 npm run dev'],
    ['en', 'Close the current development processes, then run npm run dev again from the repository root'],
  ])('requires restarting the whole development stack in %s instead of relaunching the old main process', (locale, guidance) => {
    vi.stubEnv('DEV', true)
    shared.status.value = { state: 'restart_required', managed: true }
    const restartApp = vi.fn()
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { restartApp } })
    const w = render(locale)
    expect(w.text()).toContain(guidance)
    expect(w.find('button').exists()).toBe(false)
    expect(restartApp).not.toHaveBeenCalled()
    expect(retryConnection).not.toHaveBeenCalled()
  })
})
