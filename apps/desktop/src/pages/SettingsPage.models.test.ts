// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { defineComponent, h, Fragment, vaporInteropPlugin } from 'vue'
import en from '@/locales/en'
import SettingsPage from './SettingsPage.vue'
import { setHostAccessStatus } from '@/lib/hostAccess'

const mocks = vi.hoisted(() => ({ providers: vi.fn(), routes: vi.fn(), save: vi.fn(), statusError: vi.fn() }))
vi.mock('@/api', () => ({ api: new Proxy({}, { get(_target, key) {
  if (key === 'listModelProviders') return mocks.providers
  if (key === 'listModelRoutes') return mocks.routes
  if (key === 'saveModelProvider') return mocks.save
  if (key === 'getModelReadiness' || key === 'getModelCatalogReadiness') return vi.fn(async () => null)
  if (typeof key === 'string' && key.startsWith('list')) return vi.fn(async () => [])
  return vi.fn(async () => ({}))
} }) }))
vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn(), back: vi.fn() }), useRoute: () => ({ query: {}, params: {} }), onBeforeRouteLeave: vi.fn() }))
// Vapor primitives require the browser's single ESM runtime. The page behavior
// under test is the model form; keep these display-only primitives out of VTU's CJS runtime.
vi.mock('@/components/ui', async importOriginal => {
  const actual = await importOriginal<Record<string, unknown>>()
  const { defineComponent, h } = await import('vue')
  const display = defineComponent({ setup: (_, { slots }) => () => h('span', null, slots.default?.()) })
  return { ...actual, UiBadge: display, UiLabel: display, UiSkeleton: display }
})
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({
  notify: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), info: vi.fn() }, status: { error: mocks.statusError },
  dismissByKey: vi.fn(), confirm: vi.fn(async () => true),
}) }))

const provider = { id: 'p1', driver: 'openai', protocol: 'openai-responses', display_name: 'Provider A', connection_kind: 'api-key',
  base_url: 'https://example.test/v1', model: 'model-one', models: ['model-one'], has_api_key: true, enabled: true, capabilities: ['chat'],
  status: 'ready', status_message: '', revision: 7, created_at: '', updated_at: '', model_parameters: { 'model-one': { reasoning_effort: 'high', max_output_tokens: 8192 } },
}
beforeEach(() => {
  vi.resetAllMocks()
  mocks.providers.mockResolvedValue([provider])
  mocks.routes.mockResolvedValue([])
  const storage = new Map<string, string>()
  Object.defineProperty(window, 'localStorage', { configurable: true, value: { getItem: (key: string) => storage.get(key) ?? null, setItem: (key: string, value: string) => storage.set(key, value), removeItem: (key: string) => storage.delete(key), clear: () => storage.clear() } })
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { gatewayUrl: () => 'http://127.0.0.1:48730', getAppConfig: vi.fn(async () => ({})) } })
  setHostAccessStatus({ state: 'ready', managed: true })
})

async function openModels() {
  const transition = defineComponent({ inheritAttrs: false, setup: (_, { slots }) => () => h(Fragment, null, slots.default?.()) })
  const wrapper = mount(SettingsPage, { global: { plugins: [vaporInteropPlugin, createPinia(), createI18n({ legacy: false, locale: 'en', messages: { en } })], stubs: { PersonalSection: true, Transition: transition, transition, Teleport: true } } })
  await flushPromises()
  await wrapper.findAll('.settings-nav-item').find(button => button.attributes('aria-label') === en.settings.model)!.trigger('click')
  await flushPromises()
  await wrapper.findAll('.model-center-tabs button').find(button => button.text().includes(en.settings.centerModels))!.trigger('click')
  await flushPromises()
  return wrapper
}

describe('settings model configuration through the shipping page', () => {
  it('opens persisted reasoning settings and saves with the provider revision', async () => {
    const wrapper = await openModels()
    await wrapper.findAll('.model-group-model button').find(button => button.text().includes(en.settings.modelParameters))!.trigger('click')
    expect((wrapper.get('#model-reasoning').element as HTMLSelectElement).value).toBe('high')
    expect((wrapper.get('#model-parameter-maxTokens').element as HTMLInputElement).value).toBe('8192')
    await wrapper.get('#model-reasoning').setValue('low')
    await wrapper.get('.model-parameters-editor').trigger('submit')
    await flushPromises()
    expect(mocks.save).toHaveBeenCalledWith('p1', expect.objectContaining({ model_parameters: { 'model-one': { reasoning_effort: 'low', max_output_tokens: 8192 } } }), { expected_revision: 7 })
    expect(wrapper.find('.model-parameters-editor').exists()).toBe(false)
    wrapper.unmount()
  })
  it('keeps the add-model dialog and pending model when provider saving fails', async () => {
    mocks.save.mockRejectedValue(new Error('Provider is unavailable'))
    const wrapper = await openModels()
    await wrapper.findAll('.model-provider-group-head button').find(button => button.text().includes(en.settings.addModel))!.trigger('click')
    await flushPromises()
    await wrapper.get('.model-manual-row input').setValue('model-two')
    await wrapper.get('.model-manual-row button').trigger('click')
    await wrapper.findAll('.modal-actions button').find(button => button.text().includes(en.settings.save))!.trigger('click')
    await flushPromises()
    expect(wrapper.find('.model-manual-row').exists()).toBe(true)
    expect(wrapper.get('.model-pending-list').text()).toContain('model-two')
    expect(mocks.save.mock.calls[0]?.[1].model).toBe('model-one')
    wrapper.unmount()
  })
  it('keeps the model inventory visible after refresh fails', async () => {
    const wrapper = await openModels()
    mocks.providers.mockRejectedValueOnce(new Error('Offline'))
    await wrapper.findAll('.center-page button').find(button => button.text().includes(en.settings.refresh))!.trigger('click')
    await flushPromises()
    expect(wrapper.find('.model-group-model').text()).toContain('model-one')
    expect(mocks.statusError).toHaveBeenCalled()
    wrapper.unmount()
  })
})
