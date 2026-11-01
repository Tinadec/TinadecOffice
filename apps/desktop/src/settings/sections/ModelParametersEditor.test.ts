// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import type { ModelProviderInstanceDto } from '@/api'
import ModelParametersEditor from './ModelParametersEditor.vue'
import { captureStorageId, setSelectedStorage } from '@/lib/storageScope'

const mocks = vi.hoisted(() => ({ save: vi.fn() }))
vi.mock('@/api', () => ({ api: { saveModelProvider: mocks.save } }))
const provider: ModelProviderInstanceDto = {
  id: 'provider-a', driver: 'openai', protocol: 'openai-responses', display_name: 'A', connection_kind: 'api-key',
  has_api_key: true, enabled: true, capabilities: ['chat'], status: 'ready', status_message: '', revision: 7, created_at: '', updated_at: '',
  model_parameters: { 'test-model': { reasoning_effort: 'high', max_output_tokens: 8192 }, other: { temperature: 0.2 } },
}
const i18n = () => createI18n({ legacy: false, locale: 'en', messages: { en } })
afterEach(() => vi.resetAllMocks())
/** The slider's position for an effort: model default first, then none → xhigh. */
const stop = (effort: string) => ['', 'none', 'low', 'medium', 'high', 'xhigh'].indexOf(effort)

describe('model parameter editor', () => {
  it('writes user defaults even while Home shows a project', async () => {
    const storage: string[] = []
    mocks.save.mockImplementation(async () => { storage.push(captureStorageId('/api/v1/model-providers/provider-a')); return provider })
    setSelectedStorage('active-project')
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.get('#model-reasoning').setValue(String(stop('low'))); await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(storage).toEqual(['user'])
    wrapper.unmount(); setSelectedStorage('user')
  })
  it('saves per model with the provider revision and keeps sibling parameters', async () => {
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.get('#model-reasoning').setValue(String(stop('xhigh')))
    await wrapper.get('#model-parameter-temperature').setValue('0')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(mocks.save).toHaveBeenCalledWith('provider-a', expect.objectContaining({
      model_parameters: { 'test-model': { reasoning_effort: 'xhigh', temperature: 0, max_output_tokens: 8192 }, other: { temperature: 0.2 } },
    }), { expected_revision: 7 })
    expect(wrapper.emitted('saved')).toHaveLength(1)
    wrapper.unmount()
  })
  it('resets this model without erasing sibling settings', async () => {
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.get('button[type="button"]').trigger('click')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(mocks.save.mock.calls[0]?.[1].model_parameters).toEqual({ other: { temperature: 0.2 } })
    wrapper.unmount()
  })
  it('refuses fractional output limits and exposes an accessible field error', async () => {
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.get('#model-parameter-maxTokens').setValue('1.5')
    expect(wrapper.get('#model-parameter-maxTokens').attributes('aria-invalid')).toBe('true')
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined()
    await wrapper.get('form').trigger('submit')
    expect(mocks.save).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('keeps the editor and unsaved choices after a stale-write rejection', async () => {
    mocks.save.mockRejectedValue(Object.assign(new Error('stale'), { status: 412 }))
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.get('#model-reasoning').setValue(String(stop('low')))
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('Refresh')
    expect((wrapper.get('#model-reasoning').element as HTMLInputElement).value).toBe(String(stop('low')))
    expect(wrapper.emitted('saved')).toBeUndefined()
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })
  it('keeps a dirty draft when the provider refreshes, and saves with the revision it was opened on', async () => {
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.get('#model-reasoning').setValue(String(stop('low')))
    // A background refresh replaced the saved projection: the draft must not be overwritten, and its
    // write must carry revision 7 — sending 8 would silently overwrite what the user never saw.
    await wrapper.setProps({ provider: { ...provider, revision: 8, model_parameters: { 'test-model': { reasoning_effort: 'medium', max_output_tokens: 4096 } } } })
    expect(wrapper.text()).toContain(en.settings.modelParametersDraftKept)
    expect((wrapper.get('#model-reasoning').element as HTMLInputElement).value).toBe(String(stop('low')))
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(mocks.save).toHaveBeenCalledWith('provider-a', expect.objectContaining({
      model_parameters: { 'test-model': { reasoning_effort: 'low', max_output_tokens: 8192 } },
    }), { expected_revision: 7 })
    wrapper.unmount()
  })
  it('adopts refreshed saved values while the editor is pristine', async () => {
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.setProps({ provider: { ...provider, revision: 8, model_parameters: { 'test-model': { reasoning_effort: 'low', max_output_tokens: 4096 } } } })
    expect((wrapper.get('#model-reasoning').element as HTMLInputElement).value).toBe(String(stop('low')))
    expect((wrapper.get('#model-parameter-maxTokens').element as HTMLInputElement).value).toBe('4096')
    expect(wrapper.text()).not.toContain(en.settings.modelParametersDraftKept)
    wrapper.unmount()
  })
  it('loads latest only on request, discarding the stale draft', async () => {
    mocks.save.mockRejectedValue(Object.assign(new Error('stale'), { status: 412 }))
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.get('#model-reasoning').setValue(String(stop('low')))
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const loadLatest = wrapper.findAll('button').find(button => button.text().includes(en.settings.modelParametersLoadLatest))
    expect(loadLatest, 'load-latest action').toBeDefined()
    await loadLatest!.trigger('click')
    expect(wrapper.emitted('reload')).toHaveLength(1)
    expect((wrapper.get('#model-reasoning').element as HTMLInputElement).value).toBe(String(stop('high')))
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    wrapper.unmount()
  })
  it('omits sampling for OpenAI reasoning models and hides unsupported reasoning on Anthropic', async () => {
    const wrapper = mount(ModelParametersEditor, { props: { provider, model: 'gpt-5.4' }, global: { plugins: [i18n()] } })
    await wrapper.get('#model-reasoning').setValue(String(stop('high')))
    expect(wrapper.get('#model-parameter-temperature').attributes('disabled')).toBeDefined()
    await wrapper.setProps({ provider: { ...provider, protocol: 'anthropic-messages', revision: 8 } })
    expect(wrapper.find('#model-reasoning').exists()).toBe(false)
    await wrapper.get('#model-parameter-temperature').setValue('1.5')
    expect(wrapper.get('#model-parameter-temperature').attributes('aria-invalid')).toBe('true')
    wrapper.unmount()
  })
  it('writes a per-model image capability override for an HTTP provider', async () => {
    const imageProvider: ModelProviderInstanceDto = {
      ...provider,
      image_input: {
        enabled: false, max_image_bytes: 20 * 1024 * 1024, max_images_per_turn: 8,
        base_enabled: false, base_max_image_bytes: 20 * 1024 * 1024, base_max_images_per_turn: 8,
        editable: true, source: 'config', model_overrides: {},
      },
    }
    const wrapper = mount(ModelParametersEditor, { props: { provider: imageProvider, model: 'test-model' }, global: { plugins: [i18n()] } })
    await wrapper.get('.model-image-toggle input').setValue(true)
    await wrapper.get('#model-image-max-bytes').setValue('1048576')
    await wrapper.get('#model-image-max-count').setValue('2')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(mocks.save).toHaveBeenCalledWith('provider-a', expect.objectContaining({
      image_input: {
        enabled: false,
        max_image_bytes: 20 * 1024 * 1024,
        max_images_per_turn: 8,
        model_overrides: { 'test-model': { enabled: true, max_image_bytes: 1048576, max_images_per_turn: 2 } },
      },
    }), { expected_revision: 7 })
    wrapper.unmount()
  })
})
