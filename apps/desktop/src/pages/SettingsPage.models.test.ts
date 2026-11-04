// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { defineComponent, h, Fragment, vaporInteropPlugin } from 'vue'
import en from '@/locales/en'
import SettingsPage from './SettingsPage.vue'
import { setHostAccessStatus } from '@/lib/hostAccess'

const mocks = vi.hoisted(() => ({
  providers: vi.fn(),
  routes: vi.fn(),
  save: vi.fn(),
  refreshModels: vi.fn(),
  references: vi.fn(async (): Promise<Array<{ reference_kind: string; reference_id?: string; provider_instance_id: string; model?: string }>> => []),
  statusError: vi.fn(),
  confirm: vi.fn(async (_options?: { title?: string; message?: string }) => true),
}))
vi.mock('@/api', () => ({ api: new Proxy({}, { get(_target, key) {
  if (key === 'listModelProviders') return mocks.providers
  if (key === 'listModelRoutes') return mocks.routes
  if (key === 'saveModelProvider') return mocks.save
  if (key === 'refreshProviderModels') return mocks.refreshModels
  if (key === 'listModelReferences') return mocks.references
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
  dismissByKey: vi.fn(), confirm: mocks.confirm,
}) }))

const provider = { id: 'p1', driver: 'openai', protocol: 'openai-responses', display_name: 'Provider A', connection_kind: 'api-key',
  base_url: 'https://example.test/v1', model: 'model-one', models: ['model-one'], has_api_key: true, enabled: true, capabilities: ['chat'],
  status: 'ready', status_message: '', revision: 7, created_at: '', updated_at: '', model_parameters: { 'model-one': { reasoning_effort: 'high', max_output_tokens: 8192 } },
}
beforeEach(() => {
  vi.resetAllMocks()
  mocks.providers.mockResolvedValue([provider])
  mocks.routes.mockResolvedValue([])
  mocks.references.mockResolvedValue([])
  mocks.refreshModels.mockResolvedValue({ models: [] })
  const storage = new Map<string, string>()
  Object.defineProperty(window, 'localStorage', { configurable: true, value: { getItem: (key: string) => storage.get(key) ?? null, setItem: (key: string, value: string) => storage.set(key, value), removeItem: (key: string) => storage.delete(key), clear: () => storage.clear() } })
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { gatewayUrl: () => 'http://127.0.0.1:48730', getAppConfig: vi.fn(async () => ({})) } })
  setHostAccessStatus({ state: 'ready', managed: true })
})

async function mountSettings() {
  const transition = defineComponent({ inheritAttrs: false, setup: (_, { slots }) => () => h(Fragment, null, slots.default?.()) })
  // reka dialogs portal to <body>; inline Teleport keeps the wrapper's own queries able to see them.
  const teleport = defineComponent({ name: 'Teleport', setup: (_, { slots }) => () => h(Fragment, null, slots.default?.()) })
  // Attached: validation moves focus by querying the document, which a detached tree cannot serve.
  const wrapper = mount(SettingsPage, { attachTo: document.body, global: { plugins: [vaporInteropPlugin, createPinia(), createI18n({ legacy: false, locale: 'en', messages: { en } })], stubs: { PersonalSection: true, Transition: transition, transition, Teleport: teleport } } })
  await flushPromises()
  await wrapper.findAll('.settings-nav-item').find(button => button.attributes('aria-label') === en.settings.model)!.trigger('click')
  await flushPromises()
  return wrapper
}

async function openModels() {
  const wrapper = await mountSettings()
  await wrapper.findAll('.model-center-tabs button').find(button => button.text().includes(en.settings.centerModels))!.trigger('click')
  await flushPromises()
  return wrapper
}

/** The provider card's editor: the surface where a typed API key used to be one stray click away. */
async function openProviderEditor(wrapper: Awaited<ReturnType<typeof mountSettings>>) {
  await wrapper.get('.model-provider-identity').trigger('click')
  await flushPromises()
  await wrapper.findAll('button').find(button => button.text().trim() === en.settings.editConfig)!.trigger('click')
  await flushPromises()
  return wrapper
}

async function saveProviderForm(wrapper: Awaited<ReturnType<typeof mountSettings>>) {
  const footer = wrapper.get('.model-provider-form .modal-actions')
  await footer.findAll('button').find(button => button.text().trim() === en.settings.save)!.trigger('click')
  await flushPromises()
}

async function cancelProviderForm(wrapper: Awaited<ReturnType<typeof mountSettings>>) {
  const footer = wrapper.get('.model-provider-form .modal-actions')
  await footer.findAll('button').find(button => button.text().trim() === en.settings.cancel)!.trigger('click')
  await flushPromises()
}

async function providerAction(wrapper: Awaited<ReturnType<typeof mountSettings>>, label: string) {
  const actions = wrapper.get('.provider-detail-actions')
  await actions.findAll('button').find(button => button.text().includes(label))!.trigger('click')
  await flushPromises()
}

async function openAddModelDialog(wrapper: Awaited<ReturnType<typeof mountSettings>>) {
  await wrapper.findAll('.model-provider-group-head button').find(button => button.text().includes(en.settings.addModel))!.trigger('click')
  await flushPromises()
}

/** The first fetch: its label flips to "Refresh models" once an answer has landed. */
async function fetchModels(wrapper: Awaited<ReturnType<typeof mountSettings>>) {
  await wrapper.get('.model-provider-modal').findAll('button').find(button => button.text().includes(en.settings.fetchModels))!.trigger('click')
  await flushPromises()
}

async function clickModalButton(wrapper: Awaited<ReturnType<typeof mountSettings>>, label: string) {
  await wrapper.get('.modal-actions').findAll('button').find(button => button.text().trim() === label)!.trigger('click')
  await flushPromises()
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((res) => { resolve = res })
  return { promise, resolve }
}

describe('settings model configuration through the shipping page', () => {
  it('opens persisted reasoning settings and saves with the provider revision', async () => {
    const wrapper = await openModels()
    await wrapper.findAll('.model-group-model button').find(button => button.text().includes(en.settings.modelParameters))!.trigger('click')
    expect(wrapper.get('#model-reasoning').attributes('aria-valuetext')).toBe(en.settings.reasoningTick.high)
    expect((wrapper.get('#model-parameter-maxTokens').element as HTMLInputElement).value).toBe('8192')
    await wrapper.get('#model-reasoning').setValue('2') // the slider: default, none, low, …
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
    expect(wrapper.get('.model-discovered-list').text()).toContain('model-two')
    expect(mocks.save.mock.calls[0]?.[1].model).toBe('model-one')
    wrapper.unmount()
  })
  it('opens diagnostics on demand instead of filling the work page with receipts', async () => {
    const wrapper = await mountSettings()
    expect(wrapper.find('.model-diagnostics-dialog').exists()).toBe(false)

    await wrapper.findAll('.center-command-actions button').find(button => button.text().includes(en.settings.advancedDiagnostics))!.trigger('click')
    await flushPromises()

    expect(wrapper.find('.model-diagnostics-dialog').exists()).toBe(true)
    expect(wrapper.get('.model-diagnostics-dialog').text()).toContain(en.settings.diagnosticsDescription)
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

  it('unifies saved and discovered models into one selectable list', async () => {
    mocks.refreshModels.mockResolvedValue({ models: [
      { id: 'model-one', display_name: 'Model One' },
      { id: 'model-two', display_name: 'Model Two' },
    ] })
    const wrapper = await openModels()
    await openAddModelDialog(wrapper)
    await fetchModels(wrapper)

    expect(mocks.refreshModels).toHaveBeenCalledWith('p1')
    // The saved model is in the same list, ticked and fixed; the new one arrives pre-selected.
    const saved = wrapper.get('[role="checkbox"][aria-label="model-one"]')
    expect(saved.attributes('aria-checked')).toBe('true')
    expect(saved.attributes('disabled')).toBeDefined()
    const fresh = wrapper.get('[role="checkbox"][aria-label="model-two"]')
    expect(fresh.attributes('aria-checked')).toBe('true')
    expect(fresh.attributes('disabled')).toBeUndefined()

    await clickModalButton(wrapper, en.settings.save)
    await flushPromises()
    expect(mocks.save).toHaveBeenCalledWith('p1', expect.objectContaining({ models: ['model-one', 'model-two'], model: 'model-one' }), { expected_revision: 7 })
    wrapper.unmount()
  })

  it('filters the unified model list by name', async () => {
    mocks.refreshModels.mockResolvedValue({ models: [
      { id: 'model-one', display_name: 'Model One' },
      { id: 'model-two', display_name: 'Model Two' },
    ] })
    const wrapper = await openModels()
    await openAddModelDialog(wrapper)
    await fetchModels(wrapper)

    await wrapper.get('.model-modal-search input').setValue('two')
    expect(wrapper.findAll('.model-discovered-row')).toHaveLength(1)
    expect(wrapper.get('.model-discovered-row').text()).toContain('model-two')

    await wrapper.get('.model-modal-search input').setValue('nothing-matches')
    expect(wrapper.find('.model-discovered-row').exists()).toBe(false)
    expect(wrapper.get('.model-provider-modal .center-empty-state').text()).toContain(en.settings.modelFilterEmpty)
    wrapper.unmount()
  })

  it('drops a discovery that answers after its dialog closed', async () => {
    const pending = deferred<{ models: Array<{ id: string; display_name: string }> }>()
    mocks.refreshModels.mockReturnValue(pending.promise)
    const wrapper = await openModels()
    await openAddModelDialog(wrapper)
    await fetchModels(wrapper)
    await clickModalButton(wrapper, en.settings.cancel)
    expect(wrapper.find('.model-provider-modal').exists()).toBe(false)

    await openAddModelDialog(wrapper)
    pending.resolve({ models: [{ id: 'late-model', display_name: 'Late' }] })
    await flushPromises()

    // The answer belonged to the dialog that asked; the new one starts empty, not half-filled.
    expect(wrapper.findAll('.model-discovered-row').map(row => row.text()).join(' ')).not.toContain('late-model')
    expect(wrapper.findAll('button').find(button => button.text().trim() === en.settings.save)!.attributes('disabled')).toBeDefined()
    expect(mocks.save).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('does not add a model that is already saved, and cancelling writes nothing', async () => {
    const wrapper = await openModels()
    await openAddModelDialog(wrapper)
    await wrapper.get('.model-manual-row input').setValue('model-one')
    await wrapper.get('.model-manual-row button').trigger('click')

    // Deduped against the saved list: one row, and there is nothing new to save.
    expect(wrapper.findAll('.model-discovered-row')).toHaveLength(1)
    expect((wrapper.get('.model-manual-row input').element as HTMLInputElement).value).toBe('')
    expect(wrapper.findAll('button').find(button => button.text().trim() === en.settings.save)!.attributes('disabled')).toBeDefined()

    await clickModalButton(wrapper, en.settings.cancel)
    await flushPromises()
    expect(mocks.save).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('asks before a cancel throws away typed provider settings', async () => {
    mocks.confirm.mockResolvedValueOnce(false)
    const wrapper = await mountSettings()
    await openProviderEditor(wrapper)

    await wrapper.get('.model-provider-form input').setValue('Renamed provider')
    await cancelProviderForm(wrapper)

    expect(mocks.confirm).toHaveBeenCalled()
    // "Keep editing" wins: the form stays and the typed name is still in it.
    expect(wrapper.find('.model-provider-form').exists()).toBe(true)
    expect((wrapper.get('.model-provider-form input').element as HTMLInputElement).value).toBe('Renamed provider')
    wrapper.unmount()
  })

  it('closes a pristine provider form without asking', async () => {
    mocks.confirm.mockClear()
    const wrapper = await mountSettings()
    await openProviderEditor(wrapper)

    await cancelProviderForm(wrapper)

    // Nothing was typed, so there is nothing to warn about.
    expect(mocks.confirm).not.toHaveBeenCalled()
    expect(wrapper.find('.model-provider-form').exists()).toBe(false)
    wrapper.unmount()
  })

  it('keeps the draft when switching provider or tab would discard it', async () => {
    mocks.providers.mockResolvedValue([provider, { ...provider, id: 'p2', display_name: 'Provider B' }])
    mocks.confirm.mockResolvedValueOnce(false)
    const wrapper = await mountSettings()
    await openProviderEditor(wrapper)

    await wrapper.get('.model-provider-form input').setValue('Renamed provider')
    await wrapper.get('.model-center-provider-row [data-provider-id="p2"]').trigger('click')
    await flushPromises()
    expect(mocks.confirm).toHaveBeenCalled()
    expect(wrapper.find('.model-provider-form').exists()).toBe(true)
    expect((wrapper.get('.model-provider-form input').element as HTMLInputElement).value).toBe('Renamed provider')

    mocks.confirm.mockResolvedValueOnce(false)
    await wrapper.findAll('.model-center-tabs button').find(button => button.text().includes(en.settings.centerAdvanced))!.trigger('click')
    await flushPromises()
    // Still on the supplier page: the tab switch was refused, so the draft survived.
    expect(wrapper.find('.model-provider-form').exists()).toBe(true)
    wrapper.unmount()
  })

  it('returns from a supplier detail to a list that kept its search and focus', async () => {
    const wrapper = await mountSettings()
    await wrapper.get('.model-provider-search input').setValue('prov')

    await wrapper.get('.model-center-provider-row [data-provider-id="p1"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('.model-center-provider-workspace').classes()).toContain('show-detail')

    await wrapper.get('.model-center-provider-back').trigger('click')
    await flushPromises()

    // Narrow panes are CSS, but the state that drives them is this class; the rail was
    // hidden, not unmounted, so the query is still there and focus is back on the row.
    expect(wrapper.get('.model-center-provider-workspace').classes()).not.toContain('show-detail')
    expect((wrapper.get('.model-provider-search input').element as HTMLInputElement).value).toBe('prov')
    expect(document.activeElement).toBe(wrapper.get('.model-center-provider-row [data-provider-id="p1"]').element)
    wrapper.unmount()
  })

  it('refuses a nameless or malformed provider and points at the field to fix', async () => {
    const focus = vi.spyOn(HTMLElement.prototype, 'focus')
    const wrapper = await mountSettings()
    await openProviderEditor(wrapper)

    await wrapper.get('#provider-display-name').setValue('   ')
    await saveProviderForm(wrapper)
    expect(mocks.save).not.toHaveBeenCalled()
    expect(wrapper.get('#provider-display-name').attributes('aria-invalid')).toBe('true')
    expect(wrapper.get('#provider-display-name-error').text()).toBe(en.settings.fieldRequired)
    expect(focus.mock.instances).toContain(wrapper.get('#provider-display-name').element)

    await wrapper.get('#provider-display-name').setValue('Provider A')
    await wrapper.get('#provider-base-url').setValue('example.test/v1')
    await saveProviderForm(wrapper)
    expect(mocks.save).not.toHaveBeenCalled()
    expect(wrapper.get('#provider-base-url').attributes('aria-invalid')).toBe('true')
    expect(wrapper.get('#provider-base-url-error').text()).toBe(en.settings.fieldUrlInvalid)

    // The errors clear as the fields become valid, and the save goes through.
    await wrapper.get('#provider-base-url').setValue('https://example.test/v1')
    await saveProviderForm(wrapper)
    expect(mocks.save).toHaveBeenCalledWith('p1', expect.objectContaining({ display_name: 'Provider A', base_url: 'https://example.test/v1' }), { expected_revision: 7 })
    focus.mockRestore()
    wrapper.unmount()
  })

  it('reveals only what was typed and never reads the stored key into the field', async () => {
    const wrapper = await mountSettings()
    await openProviderEditor(wrapper)

    const keyField = () => wrapper.get('#provider-api-key')
    expect(keyField().attributes('type')).toBe('password')
    // Has a stored key, yet the field starts empty: the value is write-only from here.
    expect((keyField().element as HTMLInputElement).value).toBe('')
    expect(wrapper.get('.model-key-row button').attributes('aria-label')).toBe(en.settings.showApiKey)

    await keyField().setValue('sk-typed')
    await wrapper.get('.model-key-row button').trigger('click')
    expect(keyField().attributes('type')).toBe('text')
    expect(wrapper.get('.model-key-row button').attributes('aria-label')).toBe(en.settings.hideApiKey)
    expect(wrapper.get('.model-key-row button').attributes('aria-pressed')).toBe('true')
    expect((keyField().element as HTMLInputElement).value).toBe('sk-typed')

    await saveProviderForm(wrapper)
    expect(mocks.save).toHaveBeenCalledWith('p1', expect.objectContaining({ api_key: 'sk-typed', clear_api_key: false }), { expected_revision: 7 })
    wrapper.unmount()
  })

  it('names the references that a provider deletion affects', async () => {
    mocks.references.mockResolvedValue([
      { reference_kind: 'route', reference_id: 'route-chat', provider_instance_id: 'p1', model: 'model-one' },
      { reference_kind: 'agent_default', reference_id: 'agent-1', provider_instance_id: 'p1', model: 'model-one' },
    ])
    const wrapper = await mountSettings()
    await wrapper.get('.model-provider-identity').trigger('click')
    await flushPromises()
    await providerAction(wrapper, en.settings.delete)

    const message = mocks.confirm.mock.calls[0]?.[0]?.message as string
    expect(message).toContain(`${en.settings.centerRoutes} ×1`)
    expect(message).toContain(`${en.settings.referenceAgentDefault} ×1`)
    expect(message).toContain('2')
    wrapper.unmount()
  })

  it('warns about referenced models before disabling a provider', async () => {
    mocks.references.mockResolvedValue([
      { reference_kind: 'route', reference_id: 'route-chat', provider_instance_id: 'p1', model: 'model-one' },
    ])
    const wrapper = await mountSettings()
    await wrapper.get('.model-provider-identity').trigger('click')
    await flushPromises()
    await providerAction(wrapper, en.settings.disable)

    const message = mocks.confirm.mock.calls[0]?.[0]?.message as string
    expect(message).toContain(en.settings.centerRoutes)
    expect(mocks.save).toHaveBeenCalledWith('p1', expect.objectContaining({ enabled: false }), { expected_revision: 7 })
    wrapper.unmount()
  })

  it('says so when the affected references cannot be read, instead of calling it clear', async () => {
    mocks.references.mockRejectedValue(new Error('offline'))
    const wrapper = await mountSettings()
    await wrapper.get('.model-provider-identity').trigger('click')
    await flushPromises()
    await providerAction(wrapper, en.settings.delete)

    const message = mocks.confirm.mock.calls[0]?.[0]?.message as string
    expect(message).toContain(en.settings.providerReferencesUnreadable)
    expect(message).not.toContain(en.settings.providerNoReferences)
    wrapper.unmount()
  })

  it('points a saved-but-modelless provider at its next step', async () => {
    mocks.providers.mockResolvedValue([{ ...provider, model: null, models: [], model_parameters: {} }])
    const wrapper = await mountSettings()
    await wrapper.get('.model-provider-identity').trigger('click')
    await flushPromises()

    const nextStep = wrapper.get('.provider-next-step')
    expect(nextStep.text()).toContain(en.settings.providerNextStep)
    await nextStep.findAll('button').find(button => button.text().includes(en.settings.addModel))!.trigger('click')
    await flushPromises()
    expect(wrapper.find('.model-manual-row').exists()).toBe(true)
    wrapper.unmount()
  })
})
