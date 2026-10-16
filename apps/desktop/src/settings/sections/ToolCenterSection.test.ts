// @vitest-environment happy-dom
import { defineComponent } from 'vue'
import { mount, flushPromises } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import zh from '@/locales/zh-CN'
import { api } from '@/api'
import ToolCenterSection from './ToolCenterSection.vue'
const notify = vi.hoisted(() => ({ success: vi.fn() }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify }) }))
// UI primitives use Vapor; their separate tests cover rendering. This fixture exercises settings state.
vi.mock('@/components/ui', () => ({
  UiButton: { template: '<button v-bind="$attrs"><slot /></button>' },
  UiBadge: { template: '<span><slot /></span>' },
  UiSelectField: {
    props: ['value', 'modelValue', 'options', 'disabled'],
    emits: ['update:modelValue', 'update:value', 'change'],
    setup(props: { value?: string; modelValue?: string; options?: Array<{ value: string; label: string }> }, { emit }: { emit: (event: string, value: string) => void }) {
      const pick = (event: Event) => {
        const next = (event.target as HTMLSelectElement).value
        emit('update:modelValue', next); emit('update:value', next); emit('change', next)
      }
      return { pick }
    },
    template: '<span class="ui-select"><select :value="value ?? modelValue" :disabled="disabled" @change="pick"><option v-for="option in options ?? []" :key="option.value" :value="option.value">{{ option.label }}</option></select></span>',
  },
}))
vi.mock('@/api', () => ({ api: {
  getToolSettingsSchema: vi.fn(), getToolSettingsDefaults: vi.fn(), getAgentToolSettings: vi.fn(), getEffectiveToolSettings: vi.fn(),
  saveToolSettingsDefaults: vi.fn(), saveAgentToolSettings: vi.fn(), listAgentDefinitions: vi.fn(), listProjects: vi.fn(), listTools: vi.fn(),
  getToolCapabilities: vi.fn(),
} }))
const initial = { shell: { enabled: true, timeout_ms: 100 }, mcp: { enabled: true, server_resource_ids: null }, skills: { enabled: true, resource_ids: null } }
const contract = { schema_version: 1, defaults: initial, schema: { type: 'object', additionalProperties: false, properties: {
  shell: { type: 'object', additionalProperties: false, properties: { enabled: { type: 'boolean' }, timeout_ms: { type: 'integer', minimum: 1, maximum: 1000 } } },
  mcp: { type: 'object', properties: { enabled: { type: 'boolean' }, server_resource_ids: { type: ['array', 'null'], items: { type: 'string' } } } },
  skills: { type: 'object', properties: { enabled: { type: 'boolean' }, resource_ids: { type: ['array', 'null'], items: { type: 'string' } } } },
} } }
const JsonEditor = defineComponent({ props: ['modelValue', 'focusPath'], emits: ['update:modelValue'], template: '<textarea class="editor" :data-focus-path="focusPath" :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />' })
const DraftDialog = defineComponent({ emits: ['choice'], template: '<div role="dialog"><button @click="$emit(\'choice\', \'save\')">Save and leave</button><button @click="$emit(\'choice\', \'discard\')">Discard</button><button @click="$emit(\'choice\', \'cancel\')">Continue</button></div>' })
const ResourcesPanel = defineComponent({ setup(_, { expose }) { expose({ isBusy: () => false, discard: () => {} }); return () => null } })
function create(attach = false) { return mount(ToolCenterSection, { ...(attach ? { attachTo: document.body } : {}), global: { plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })], stubs: { ToolsOverviewPanel: true, ToolResourcesPanel: ResourcesPanel, ToolJsonEditor: JsonEditor, ToolDraftDialog: DraftDialog } } }) }
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.getToolSettingsSchema).mockResolvedValue(contract)
  vi.mocked(api.getToolSettingsDefaults).mockResolvedValue({ schema_version: 1, revision: 4, settings: initial, effective_settings: initial, settings_hash: 'shared' })
  vi.mocked(api.getAgentToolSettings).mockResolvedValue({ schema_version: 1, revision: 0, settings: {}, effective_settings: initial, settings_hash: 'agent' })
  vi.mocked(api.getEffectiveToolSettings).mockResolvedValue({ settings_hash: 'effective', settings: initial, allowed_tool_ids: ['shell'] })
  vi.mocked(api.listAgentDefinitions).mockResolvedValue([{ id: 'agent-a', display_name: 'Research', slug: 'research' }] as never)
  vi.mocked(api.listProjects).mockResolvedValue([{ id: 'project-a', name: 'Project A' }] as never)
  vi.mocked(api.listTools).mockResolvedValue([])
  vi.mocked(api.getToolCapabilities).mockResolvedValue({ status: 'unavailable', reason: 'test host' })
  vi.mocked(api.saveToolSettingsDefaults).mockImplementation(async settings => ({ schema_version: 1, revision: 5, settings, effective_settings: settings, settings_hash: 'saved' }))
})
describe('Tools configuration', () => {
  it('keeps keyboard tab navigation separate from the editor category focus', async () => {
    const wrapper = create(true); await flushPromises()
    await wrapper.get('#tools-tab-skills').trigger('click'); await flushPromises()
    await wrapper.get('#tools-tab-skills').trigger('keydown', { key: 'ArrowRight' }); await flushPromises()
    expect(wrapper.get('#tools-tab-advanced').attributes('aria-selected')).toBe('true')
    expect(wrapper.get('textarea.editor').attributes('data-focus-path')).toBe('')
    expect(document.activeElement?.id).toBe('tools-tab-advanced')
    await wrapper.get('#tools-tab-advanced').trigger('keydown', { key: 'ArrowLeft' }); await flushPromises()
    expect(wrapper.get('#tools-tab-skills').attributes('aria-selected')).toBe('true')
    expect(document.activeElement?.id).toBe('tools-tab-skills')
    wrapper.unmount()
  })
  it('opens advanced configuration at the current category while preserving the selected Agent', async () => {
    const wrapper = create(); await flushPromises()
    await wrapper.findAll('select')[0]!.setValue('agent-a'); await flushPromises()
    await wrapper.get('#tools-tab-files').trigger('click')
    await wrapper.get('.tools-command-bar button').trigger('click'); await flushPromises()
    expect(wrapper.get('textarea.editor').attributes('data-focus-path')).toBe('read')
    expect((wrapper.findAll('select')[0]!.element as HTMLSelectElement).value).toBe('agent-a')
    expect(wrapper.get('.tools-command-bar').text()).toContain('结构版本 1')
    expect(api.saveAgentToolSettings).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('shows stale resource diagnostics while leaving the effective settings view readable', async () => {
    vi.mocked(api.getEffectiveToolSettings).mockResolvedValueOnce({ settings_hash: 'effective', settings: initial, resource_diagnostics: [{ kind: 'skills', resource_id: 'removed-resource', status: 'missing', reason: 'Selected skill was deleted.' }] })
    const wrapper = create(); await flushPromises()
    expect(wrapper.get('[role="status"]').text()).toContain(zh.toolsSettings.diagnostic.skills_missing)
    expect(wrapper.get('[role="status"] p').attributes('title')).toBe('Selected skill was deleted.')
    expect(wrapper.find('.tools-value-list').exists()).toBe(false)
    await wrapper.get('#tools-tab-shell').trigger('click')
    expect(wrapper.get('.tools-value-list').text()).toContain('shell.timeout_ms')
    wrapper.unmount()
  })
  it('has the nine requested tabs, read-only behavior values and saves only explicitly', async () => {
    const wrapper = create(); await flushPromises()
    expect(wrapper.findAll('[role="tab"]')).toHaveLength(9)
    await wrapper.get('#tools-tab-shell').trigger('click')
    expect(wrapper.get('.tools-value-list').text()).toContain('shell.timeout_ms')
    expect(wrapper.findAll('.tools-value-list input')).toHaveLength(0)
    await wrapper.get('#tools-tab-advanced').trigger('click'); await flushPromises()
    await wrapper.get('textarea.editor').setValue(JSON.stringify({ ...initial, shell: { enabled: false, timeout_ms: 200 } }))
    expect(api.saveToolSettingsDefaults).not.toHaveBeenCalled()
    expect(wrapper.get('.tools-diff').text()).toContain('shell.enabled')
    await wrapper.get('.tools-save-bar button').trigger('click'); await flushPromises()
    expect(api.saveToolSettingsDefaults).toHaveBeenCalledWith({ ...initial, shell: { enabled: false, timeout_ms: 200 } }, 4, undefined)
    wrapper.unmount()
  })
  it('invalid values disable save and conflict responses preserve the draft', async () => {
    const wrapper = create(); await flushPromises(); await wrapper.get('#tools-tab-advanced').trigger('click'); await flushPromises()
    await wrapper.get('textarea.editor').setValue('{"shell":{"timeout_ms":9999}}')
    expect(wrapper.get('.tools-save-bar button').attributes('disabled')).toBeDefined()
    await wrapper.get('textarea.editor').setValue('{"shell":{"timeout_ms":200}}')
    vi.mocked(api.saveToolSettingsDefaults).mockRejectedValueOnce(Object.assign(new Error('conflict'), { status: 412 }))
    await wrapper.get('.tools-save-bar button').trigger('click'); await flushPromises()
    expect((wrapper.get('textarea.editor').element as HTMLTextAreaElement).value).toContain('200')
    expect(wrapper.get('[role="alert"]').text()).toContain('草稿仍保留')
    wrapper.unmount()
  })
  it('loads sparse Agent overrides and guards leaving with save/discard/continue choices', async () => {
    const wrapper = create(); await flushPromises()
    await wrapper.findAll('select')[0]!.setValue('agent-a'); await flushPromises()
    expect(api.getAgentToolSettings).toHaveBeenCalledWith('agent-a')
    await wrapper.get('#tools-tab-advanced').trigger('click'); await flushPromises()
    await wrapper.get('textarea.editor').setValue('{"shell":{"enabled":false}}')
    const guard = (wrapper.vm as unknown as { canLeave(): Promise<boolean> }).canLeave()
    await flushPromises(); await wrapper.findAll('[role="dialog"] button')[2]!.trigger('click')
    expect(await guard).toBe(false)
    const discard = (wrapper.vm as unknown as { canLeave(): Promise<boolean> }).canLeave()
    await flushPromises(); await wrapper.findAll('[role="dialog"] button')[1]!.trigger('click')
    expect(await discard).toBe(true)
    expect((wrapper.get('textarea.editor').element as HTMLTextAreaElement).value).toBe('{}')
    expect(api.saveAgentToolSettings).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
