// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'

const mocks = vi.hoisted(() => ({
  listConfigurationDocuments: vi.fn(),
  getConfigurationDocument: vi.fn(),
  validateConfigurationDocument: vi.fn(),
  saveConfigurationDocument: vi.fn(),
  confirm: vi.fn(),
  notify: { error: vi.fn(), success: vi.fn(), info: vi.fn() },
  getMonaco: vi.fn(),
}))

vi.mock('@/api', () => ({ api: mocks }))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: mocks.notify, confirm: mocks.confirm }),
}))
vi.mock('@/composables/useMonaco', () => ({
  detectLanguage: () => 'ini',
  useMonaco: () => ({ getMonaco: mocks.getMonaco, isDark: { value: false } }),
}))
vi.mock('@/components/ui', () => ({
  UiBadge: { template: '<span class="ui-badge"><slot /></span>' },
  UiButton: {
    props: ['disabled'],
    emits: ['click'],
    template: '<button :disabled="disabled" @click="$emit(\'click\', $event)"><slot /></button>',
  },
  UiSelectField: {
    props: ['modelValue', 'options', 'disabled'],
    emits: ['update:modelValue'],
    template:
      '<select class="ui-select" :value="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.value)"><option v-for="o in options" :key="o.value" :value="o.value">{{ o.label }}</option></select>',
  },
}))

const { default: TomlDocumentPanel } = await import('./TomlDocumentPanel.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })

describe('TomlDocumentPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listConfigurationDocuments.mockResolvedValue(['runtime', 'models'])
    mocks.getConfigurationDocument.mockImplementation(async (_storage: string, id: string) => ({
      document_id: id,
      text: id === 'runtime' ? '[spawn]\nmax_depth = 2\n' : '[models]\ndefault = "gpt"\n',
      content_hash: id === 'runtime' ? 'hash-runtime-123456' : 'hash-models-654321',
    }))
    mocks.validateConfigurationDocument.mockResolvedValue({ valid: true, diagnostics: [] })
    mocks.saveConfigurationDocument.mockResolvedValue({
      document_id: 'runtime',
      text: '[spawn]\nmax_depth = 3\n',
      content_hash: 'hash-runtime-999999',
    })
    mocks.confirm.mockResolvedValue(false)
  })

  it('uses safe textarea fallback in happy-dom without invoking Monaco, edits, saves, and emits changed', async () => {
    const wrapper = mount(TomlDocumentPanel, { global: { plugins: [i18n] } })
    await flushPromises()

    expect(mocks.getMonaco).not.toHaveBeenCalled()
    const editor = wrapper.get<HTMLTextAreaElement>('textarea.toml-editor')
    expect(editor.element.value).toContain('max_depth = 2')
    expect(editor.element.readOnly).toBe(true)

    const editBtn = wrapper.findAll('button').find((b) => b.text().includes('Edit'))!
    await editBtn.trigger('click')
    expect(editor.element.readOnly).toBe(false)

    await editor.setValue('[spawn]\nmax_depth = 3\n')
    const saveBtn = wrapper.findAll('button').find((b) => b.text().includes('Save'))!
    await saveBtn.trigger('click')
    await flushPromises()

    expect(mocks.validateConfigurationDocument).toHaveBeenCalledWith('user', 'runtime', '[spawn]\nmax_depth = 3\n')
    expect(mocks.saveConfigurationDocument).toHaveBeenCalledWith(
      'user',
      'runtime',
      '[spawn]\nmax_depth = 3\n',
      'hash-runtime-123456',
    )
    expect(wrapper.emitted('changed')).toHaveLength(1)
    wrapper.unmount()
  })

  it('guards unsaved edits when switching documents and supports programmatic selectDocument()', async () => {
    const wrapper = mount(TomlDocumentPanel, { global: { plugins: [i18n] } })
    await flushPromises()

    const editBtn = wrapper.findAll('button').find((b) => b.text().includes('Edit'))!
    await editBtn.trigger('click')
    await wrapper.get<HTMLTextAreaElement>('textarea.toml-editor').setValue('[spawn]\nmax_depth = 9\n')

    // Reject discard -> stays on runtime
    mocks.confirm.mockResolvedValueOnce(false)
    await wrapper.get<HTMLSelectElement>('select.ui-select').setValue('models')
    await flushPromises()
    expect(mocks.confirm).toHaveBeenCalledTimes(1)
    expect(mocks.getConfigurationDocument).toHaveBeenCalledTimes(1)

    // Accept discard -> switches to models via selectDocument
    mocks.confirm.mockResolvedValueOnce(true)
    await (wrapper.vm as unknown as { selectDocument: (id: string) => Promise<void> }).selectDocument('models')
    await flushPromises()
    expect(mocks.getConfigurationDocument).toHaveBeenLastCalledWith('user', 'models')
    expect(wrapper.get<HTMLTextAreaElement>('textarea.toml-editor').element.value).toContain('[models]')
    wrapper.unmount()
  })
})
