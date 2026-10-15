// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ healthProbe: vi.fn().mockResolvedValue({ gateway: true, core: true, core_version: '0.1.0' }), gatewayUrl: 'http://127.0.0.1:48700' }))
vi.mock('@/api', () => ({ api }))
vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()), useI18n: () => ({ t: (key: string) => key }) }))

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (cause: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

let changed: (() => void) | undefined
let host: { getAppConfig: ReturnType<typeof vi.fn>; saveDebugStudioEnabled: ReturnType<typeof vi.fn>; onDebugStudioEnabledChanged: ReturnType<typeof vi.fn> }
let wrapper: ReturnType<typeof mount> | undefined

beforeEach(() => {
  vi.resetModules()
  changed = undefined
  host = {
    getAppConfig: vi.fn().mockResolvedValue({ debug_studio_enabled: false }),
    saveDebugStudioEnabled: vi.fn().mockImplementation(async (value: boolean) => ({ debug_studio_enabled: value })),
    onDebugStudioEnabledChanged: vi.fn((callback: () => void) => { changed = callback; return vi.fn() }),
  }
  Object.defineProperty(window, 'tinadec', { configurable: true, value: host })
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  Reflect.deleteProperty(window, 'tinadec')
  vi.restoreAllMocks()
})

async function section() {
  const AboutSection = (await import('./AboutSection.vue')).default
  // Use the actual switch and preference module so disabled/checked DOM states
  // must survive a real asynchronous host write, rather than a prop-only stub.
  wrapper = mount(AboutSection, { global: { stubs: { BrandLogo: true } } })
  return wrapper
}

describe('About developer preference switch', () => {
  it('waits for config loading and save acknowledgement before changing the displayed preference', async () => {
    const read = deferred<{ debug_studio_enabled: boolean }>()
    host.getAppConfig.mockReturnValueOnce(read.promise)
    const view = await section()
    const control = view.get<HTMLButtonElement>('[role="switch"]')
    expect(control.element.disabled).toBe(true)
    expect(control.attributes('aria-checked')).toBe('false')
    read.resolve({ debug_studio_enabled: false })
    await flushPromises()
    expect(control.element.disabled).toBe(false)
    const save = deferred<{ debug_studio_enabled: boolean }>()
    host.saveDebugStudioEnabled.mockReturnValueOnce(save.promise)
    await control.trigger('click')
    expect(host.saveDebugStudioEnabled).toHaveBeenCalledOnce()
    expect(host.saveDebugStudioEnabled).toHaveBeenCalledWith(true)
    expect(control.element.disabled).toBe(true)
    expect(control.attributes('aria-checked')).toBe('false')
    save.resolve({ debug_studio_enabled: true })
    await flushPromises()
    expect(control.element.disabled).toBe(false)
    expect(control.attributes('aria-checked')).toBe('true')
    expect(view.find('[role="alert"]').exists()).toBe(false)
  })

  it('preserves the checked state after save failure and permits an explicit retry', async () => {
    host.getAppConfig.mockResolvedValueOnce({ debug_studio_enabled: true })
    host.saveDebugStudioEnabled.mockRejectedValueOnce(new Error('host settings are read-only'))
    const view = await section()
    await flushPromises()
    const control = view.get<HTMLButtonElement>('[role="switch"]')
    expect(control.attributes('aria-checked')).toBe('true')
    await control.trigger('click')
    await flushPromises()
    expect(control.attributes('aria-checked')).toBe('true')
    expect(control.element.disabled).toBe(false)
    expect(view.get('[role="alert"]').text()).toContain('aboutPage.debugStudioSaveFailed')
    expect(view.get('[role="alert"]').text()).toContain('host settings are read-only')
    await control.trigger('click')
    await flushPromises()
    expect(host.saveDebugStudioEnabled).toHaveBeenNthCalledWith(2, false)
    expect(control.attributes('aria-checked')).toBe('false')
    expect(view.find('[role="alert"]').exists()).toBe(false)
  })

  it('blocks enabling after a load error and recovers only after a successful host refresh', async () => {
    host.getAppConfig.mockRejectedValueOnce(new Error('cannot read host settings'))
    const view = await section()
    await flushPromises()
    const control = view.get<HTMLButtonElement>('[role="switch"]')
    expect(control.element.disabled).toBe(true)
    expect(control.attributes('aria-checked')).toBe('false')
    expect(view.get('[role="alert"]').text()).toContain('aboutPage.debugStudioLoadFailed')
    control.element.click()
    await flushPromises()
    expect(host.saveDebugStudioEnabled).not.toHaveBeenCalled()
    host.getAppConfig.mockResolvedValueOnce({ debug_studio_enabled: true })
    changed!()
    await flushPromises()
    expect(control.element.disabled).toBe(false)
    expect(control.attributes('aria-checked')).toBe('true')
    expect(view.find('[role="alert"]').exists()).toBe(false)
  })
})
