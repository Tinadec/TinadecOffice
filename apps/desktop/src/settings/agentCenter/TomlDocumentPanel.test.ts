// @vitest-environment happy-dom
/**
 * Part 5 of the brief, both halves:
 *
 * 1. `selectDocument(documentId)` is the entry point "Edit runtime.toml" uses. It has to
 *    resolve the id the way Core names documents (`runtime`, `runtime.toml`, `runtime.*`) and
 *    report whether it found one — a silent fallback to "first document" opens the wrong file
 *    and looks like success.
 * 2. Monaco must not break happy-dom/jsdom. The composable it lives behind pulls
 *    `@/monaco.config`, which imports five `?worker` entrypoints that Vite cannot resolve
 *    under a test DOM. Reaching it at module scope fails the whole module graph, so this
 *    suite asserts the textarea fallback is what renders and that no editor host is mounted.
 */
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import en from '@/locales/en'

const mocks = vi.hoisted(() => ({
  listConfigurationDocuments: vi.fn(),
  getConfigurationDocument: vi.fn(),
  validateConfigurationDocument: vi.fn(),
  saveConfigurationDocument: vi.fn(),
  notify: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
  confirm: vi.fn(async () => true),
}))

vi.mock('@/api', () => ({ api: mocks }))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: mocks.notify, confirm: mocks.confirm }),
}))
vi.mock('@/lib/hostAccess', async () => {
  const { ref } = await import('vue')
  return { useHostAccess: () => ({ canAccessBackend: ref(true) }) }
})
/**
 * `UiBadge` and `UiLabel` are Vapor SFCs. Under the classic runtime VTU mounts,
 * `vaporInteropPlugin` reaches `@vue/runtime-vapor`, whose `simpleSetCurrentInstance` is not
 * exported by the `@vue/runtime-dom` build vitest resolves — mounting a real one throws
 * `Cannot read properties of undefined (reading 'mount')`. Every happy-dom suite in this repo
 * replaces exactly these primitives for the same reason; `UiButton` is added because this
 * panel's edit/save flow needs its `disabled` state in the DOM.
 */
vi.mock('@/components/ui', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>()
  const { defineComponent, h } = await import('vue')
  const passThrough = defineComponent({
    inheritAttrs: false,
    setup: (_, { slots, attrs }) => () => h('span', attrs, slots.default?.()),
  })
  const UiButton = defineComponent({
    name: 'UiButton',
    inheritAttrs: false,
    props: ['disabled'],
    emits: ['click'],
    setup: (props, { emit, slots, attrs }) => () =>
      h('button', { ...attrs, disabled: props.disabled, onClick: () => emit('click') }, slots.default?.()),
  })
  const UiSelectField = defineComponent({
    name: 'UiSelectField',
    inheritAttrs: false,
    props: ['modelValue', 'options'],
    emits: ['update:modelValue'],
    setup: (props, { emit, attrs }) => () =>
      h(
        'select',
        {
          ...attrs,
          value: props.modelValue,
          onChange: (event: Event) => emit('update:modelValue', (event.target as HTMLSelectElement).value),
        },
        ((props.options as Array<{ value: string; label: string }>) ?? []).map((o) =>
          h('option', { value: o.value }, o.label),
        ),
      ),
  })
  return { ...actual, UiBadge: passThrough, UiLabel: passThrough, UiButton, UiSelectField }
})
/**
 * The Monaco composable is mocked to THROW on import, which is the strongest assertion
 * available: if the panel reached it eagerly, this module graph would fail rather than
 * quietly render. `monacoFallback` must therefore be true and the textarea must be present.
 */
vi.mock('@/composables/useMonaco', () => {
  throw new Error('Monaco must not be imported eagerly by TomlDocumentPanel')
})

const { default: TomlDocumentPanel } = await import('./TomlDocumentPanel.vue')

const global = { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] }

function doc(id: string) {
  return { document_id: id, text: `# ${id}\nkey = 1\n`, content_hash: `hash-${id}`, revision: 1 }
}

async function mountPanel(): Promise<VueWrapper> {
  const wrapper = mount(TomlDocumentPanel, { global })
  await flushPromises()
  return wrapper
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
  mocks.listConfigurationDocuments.mockResolvedValue(['agent.toml', 'runtime.toml', 'storage.toml'])
  mocks.getConfigurationDocument.mockImplementation(async (_storage: string, id: string) => doc(id))
  mocks.confirm.mockResolvedValue(true)
})

describe('TomlDocumentPanel — Monaco must not break a test DOM', () => {
  it('renders the plain textarea fallback and never the Monaco host', async () => {
    const wrapper = await mountPanel()

    expect(wrapper.find('[data-testid="toml-editor-fallback"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="toml-editor-monaco"]').exists()).toBe(false)
    // The fallback is a real editable surface, not a placeholder.
    const area = wrapper.get('[data-testid="toml-editor-fallback"]').element as HTMLTextAreaElement
    expect(area.tagName).toBe('TEXTAREA')
    expect(area.value).toContain('# agent.toml')
    wrapper.unmount()
  })
})

describe('TomlDocumentPanel — selectDocument', () => {
  it('opens the document named by the caller', async () => {
    const wrapper = await mountPanel()
    const vm = wrapper.vm as unknown as { selectDocument: (id: string) => Promise<boolean> }

    const found = await vm.selectDocument('runtime')
    await flushPromises()

    expect(found).toBe(true)
    expect(mocks.getConfigurationDocument).toHaveBeenLastCalledWith('user', 'runtime.toml')
    expect((wrapper.get('[data-testid="toml-editor-fallback"]').element as HTMLTextAreaElement).value)
      .toContain('# runtime.toml')
    wrapper.unmount()
  })

  it('accepts the id with or without its extension', async () => {
    const wrapper = await mountPanel()
    const vm = wrapper.vm as unknown as { selectDocument: (id: string) => Promise<boolean> }

    expect(await vm.selectDocument('storage.toml')).toBe(true)
    await flushPromises()
    expect(mocks.getConfigurationDocument).toHaveBeenLastCalledWith('user', 'storage.toml')
    wrapper.unmount()
  })

  it('reports a miss instead of silently opening the first document', async () => {
    const wrapper = await mountPanel()
    const vm = wrapper.vm as unknown as { selectDocument: (id: string) => Promise<boolean> }

    // The panel is already showing agent.toml; a miss must not change that and must say so.
    const found = await vm.selectDocument('does-not-exist')
    await flushPromises()

    expect(found).toBe(false)
    expect((wrapper.get('[data-testid="toml-editor-fallback"]').element as HTMLTextAreaElement).value)
      .toContain('# agent.toml')
    wrapper.unmount()
  })

  it('does not re-read a document that is already open', async () => {
    const wrapper = await mountPanel()
    const vm = wrapper.vm as unknown as { selectDocument: (id: string) => Promise<boolean> }

    const before = mocks.getConfigurationDocument.mock.calls.length
    expect(await vm.selectDocument('agent')).toBe(true)
    await flushPromises()
    // Same document, already loaded: re-reading would discard an in-progress edit.
    expect(mocks.getConfigurationDocument.mock.calls.length).toBe(before)
    wrapper.unmount()
  })
})

describe('TomlDocumentPanel — save notifies the parent', () => {
  it('emits changed after a successful save so the ceilings can resync', async () => {
    mocks.validateConfigurationDocument.mockResolvedValue({ valid: true, diagnostics: [] })
    mocks.saveConfigurationDocument.mockImplementation(async (_s: string, id: string, text: string) => ({
      document_id: id,
      text,
      content_hash: 'hash-new',
      revision: 2,
    }))
    const wrapper = await mountPanel()

    // Enter edit mode, change the text, save.
    const editButton = wrapper.findAll('button').find((b) => b.text().includes(en.settings.tomlEdit))
    expect(editButton, 'edit button must exist').toBeTruthy()
    await editButton!.trigger('click')
    await flushPromises()

    const area = wrapper.get('[data-testid="toml-editor-fallback"]')
    await area.setValue('# edited\nkey = 2\n')
    const saveButton = wrapper.findAll('button').find((b) => b.text().includes(en.settings.tomlSave))
    expect(saveButton, 'save button must exist').toBeTruthy()
    await saveButton!.trigger('click')
    await flushPromises()

    expect(mocks.saveConfigurationDocument).toHaveBeenCalledTimes(1)
    // `changed` is what makes WorkspaceDefaultsPanel reload its ceilings after a save.
    expect(wrapper.emitted('changed')).toBeTruthy()
    wrapper.unmount()
  })
})
