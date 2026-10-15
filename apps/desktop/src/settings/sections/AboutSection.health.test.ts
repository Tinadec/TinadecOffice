// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ healthProbe: vi.fn(), gatewayUrl: 'http://127.0.0.1:48730' }))
vi.mock('@/api', () => ({ api }))
vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()), useI18n: () => ({ t: (key: string) => key }) }))

afterEach(() => { vi.clearAllMocks(); Reflect.deleteProperty(window, 'tinadec') })

async function states() {
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { getAppConfig: async () => ({ debug_studio_enabled: false }), onDebugStudioEnabledChanged: () => () => {} } })
  const AboutSection = (await import('./AboutSection.vue')).default
  const wrapper = mount(AboutSection, { global: { stubs: { AboutHero: true } } })
  await flushPromises()
  const items = wrapper.get('[data-testid="about-health"]').findAll('li')
  const result = Object.fromEntries(items.map(item => [item.get('strong').text(), item.attributes('data-state')]))
  wrapper.unmount()
  return result
}

describe('About components', () => {
  it("says Gateway is up when only Core is unreachable, instead of calling both down", async () => {
    // What api.healthProbe reads from the 503 Gateway answers when it cannot reach Core.
    api.healthProbe.mockResolvedValue({ gateway: true, core: false, core_version: null })
    expect(await states()).toEqual({ Desktop: 'running', Gateway: 'running', Core: 'unreachable' })
  })

  it('shows both down when Gateway itself cannot be reached', async () => {
    api.healthProbe.mockRejectedValue(new TypeError('Failed to fetch'))
    expect(await states()).toEqual({ Desktop: 'running', Gateway: 'unreachable', Core: 'unreachable' })
  })
})
