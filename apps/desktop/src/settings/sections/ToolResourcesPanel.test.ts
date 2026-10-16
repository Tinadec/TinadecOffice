// @vitest-environment happy-dom
import { defineComponent } from 'vue'
import { mount, flushPromises } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '@/api'
import zh from '@/locales/zh-CN'
import ToolResourcesPanel from './ToolResourcesPanel.vue'
const confirm = vi.hoisted(() => vi.fn())
vi.mock('@/api', () => ({ api: { listToolMcpResources: vi.fn(), saveToolMcpResource: vi.fn(), createToolMcpResource: vi.fn(), listToolSkills: vi.fn(), getToolSkill: vi.fn(), getToolSkillFile: vi.fn(), getUserToolAction: vi.fn(), saveToolSkill: vi.fn(), importToolSkill: vi.fn(), deleteToolSkill: vi.fn() } }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify: { success: vi.fn() }, confirm }) }))
vi.mock('@/components/ui', () => ({
  UiButton: { template: '<button v-bind="$attrs"><slot /></button>' },
  UiBadge: { template: '<span><slot /></span>' },
  UiInput: { props: ['modelValue'], emits: ['update:modelValue'], template: '<input :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />' },
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
const Editor = defineComponent({ props: ['modelValue'], emits: ['update:modelValue'], template: '<textarea :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />' })
const id = '11111111-1111-1111-1111-111111111111'
const mcp = { resource_id: id, id: 'qa', name: 'QA MCP', enabled: true, command: 'node', args: ['server.js'], env: { TOKEN: '***' }, revision: 3, configuration_hash: 'hash' }
const skill = { resource_id: id, name: 'qa-skill', description: 'QA', enabled: true, valid: true, scope: 'project', project_id: 'p1', path: 'skills/qa-skill/SKILL.md', revision: 1, content_hash: 'content', file_hash: 'file-hash', content: '---\nname: qa-skill\ndescription: QA\n---\nOriginal' }
function create(kind: 'mcp' | 'skills', props = {}) { return mount(ToolResourcesPanel, { props: { kind, projectId: 'p1', selection: null, canBind: true, ...props }, global: { plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })], stubs: { ToolJsonEditor: Editor, ToolDraftDialog: true } } }) }
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.listToolMcpResources).mockResolvedValue([mcp])
  vi.mocked(api.listToolSkills).mockResolvedValue({ skills: [skill], diagnostics: [], source: 'core' })
  vi.mocked(api.getToolSkill).mockResolvedValue(skill)
  confirm.mockResolvedValue(true)
})
describe('Tool resource manager', () => {
  it('settles loading after the resource scope changes before the first inventory arrives', async () => {
    let settle!: (value: { skills: typeof skill[]; diagnostics: string[]; source: string }) => void
    vi.mocked(api.listToolSkills).mockReturnValueOnce(new Promise(resolve => { settle = resolve }))
    const wrapper = create('skills')
    await wrapper.get('select').setValue('project'); await flushPromises()
    expect(wrapper.get('.tools-resources').attributes('aria-busy')).toBe('false')
    expect(wrapper.findAll('.tools-resource-row')).toHaveLength(1)
    settle({ skills: [], diagnostics: [], source: 'core' }); await flushPromises()
    expect(wrapper.findAll('.tools-resource-row')).toHaveLength(1)
    expect(wrapper.get('.tools-command-bar button').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })
  it('deletes shared skills with loaded revision and concrete confirmation, and offers no project delete', async () => {
    vi.mocked(api.listToolSkills).mockResolvedValue({ skills: [{ ...skill, scope: 'shared', project_id: null }], diagnostics: [], source: 'core' })
    const shared = create('skills'); await flushPromises()
    await shared.findAll('.tools-resource-row button').find(button => button.text() === '删除')!.trigger('click'); await flushPromises()
    expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ message: `${skill.name}\n${id}` }))
    expect(api.deleteToolSkill).toHaveBeenCalledWith(id, 1, 'p1')
    expect(shared.emitted('changed')).toHaveLength(1); shared.unmount()
    vi.mocked(api.listToolSkills).mockResolvedValue({ skills: [skill], diagnostics: [], source: 'core' })
    const project = create('skills'); await flushPromises(); await project.get('select').setValue('project'); await flushPromises()
    expect(project.findAll('.tools-resource-row button').some(button => button.text() === '删除')).toBe(false)
    project.unmount()
  })
  it('words skill reasons from their codes and keeps Core\'s sentence as the tooltip', async () => {
    const broken = { ...skill, resource_id: '22222222-2222-2222-2222-222222222222', scope: 'shared', project_id: null, valid: false, path: '/skills/other/SKILL.md', reason: "name 'qa' does not match its directory 'other'", reason_code: 'name_directory_mismatch', reason_data: { name: 'qa', directory: 'other' } }
    const newer = { ...broken, resource_id: '33333333-3333-3333-3333-333333333333', path: '/skills/newer/SKILL.md', reason: 'A reason from a newer Core.', reason_code: 'from_a_newer_core', reason_data: null }
    vi.mocked(api.listToolSkills).mockResolvedValue({ skills: [broken, newer], diagnostics: [], source: 'core' })
    const wrapper = create('skills'); await flushPromises()
    const line = wrapper.findAll('.tools-resource-row small').find(small => small.text().includes('不一致'))!
    expect(line.text()).toBe('名称「qa」与所在文件夹「other」不一致。')
    expect(line.attributes('title')).toBe(broken.reason)
    expect(wrapper.findAll('.tools-validation li').map(item => item.text())).toEqual(['/skills/other/SKILL.md: 名称「qa」与所在文件夹「other」不一致。', '/skills/newer/SKILL.md: A reason from a newer Core.'])
    wrapper.unmount()
  })
  it('preserves the masked MCP environment and revision and keeps edits after a save refusal', async () => {
    const wrapper = create('mcp'); await flushPromises()
    await wrapper.get('.tools-resource-row button').trigger('click'); await flushPromises()
    const editor = wrapper.get('textarea'); const draft = JSON.parse((editor.element as HTMLTextAreaElement).value); draft.name = 'Edited MCP'
    await editor.setValue(JSON.stringify(draft)); vi.mocked(api.saveToolMcpResource).mockRejectedValueOnce(new Error('Revision conflict'))
    const buttons = wrapper.findAll('.tools-resource-editor button'); await buttons[buttons.length - 1]!.trigger('click'); await flushPromises()
    expect(api.saveToolMcpResource).toHaveBeenCalledWith(id, { ...draft, env: { TOKEN: '***' } }, 3, 'p1')
    expect((wrapper.get('textarea').element as HTMLTextAreaElement).value).toContain('Edited MCP')
    expect(wrapper.get('[role="alert"]').text()).toContain('Revision conflict')
    wrapper.unmount()
  })
  it('sends the loaded project skill file hash and reports a queued governed write', async () => {
    const wrapper = create('skills'); await flushPromises()
    await wrapper.get('select').setValue('project'); await flushPromises()
    await wrapper.get('.tools-resource-row button').trigger('click'); await flushPromises()
    await wrapper.get('textarea').setValue(skill.content + '\nEdited')
    vi.mocked(api.saveToolSkill).mockResolvedValue({ ...skill, user_action_id: 'action-1', action_status: 'pending_approval' })
    const buttons = wrapper.findAll('.tools-resource-editor button'); await buttons[buttons.length - 1]!.trigger('click'); await flushPromises()
    expect(api.saveToolSkill).toHaveBeenCalledWith(id, { content: skill.content + '\nEdited', enabled: true, expected_file_hash: 'file-hash' }, 1, 'p1')
    expect(wrapper.get('.tools-resource-result').text()).toContain('pending_approval')
    expect(wrapper.get('.tools-resource-result').text()).toContain('action-1')
    wrapper.unmount()
  })
  it('distinguishes inherited empty selection, all resources and explicit none without saving bindings', async () => {
    const wrapper = create('mcp', { inherited: true, agentScope: true, effectiveSelection: [] }); await flushPromises()
    expect((wrapper.get('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(false)
    const controls = wrapper.findAll('.tools-binding-bar button')
    await controls[0]!.trigger('click'); await controls[1]!.trigger('click'); await controls[2]!.trigger('click')
    expect(wrapper.emitted('binding')).toEqual([[null, true], [null], [[]]])
    expect(api.saveToolMcpResource).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('loads existing package files and sends their complete set when an attachment is removed', async () => {
    const shared = { ...skill, scope: 'shared', project_id: null, availability: 'available', package_files: [{ path: 'SKILL.md' }, { path: 'references/one.md', content_hash: 'one-hash' }, { path: 'scripts/two.sh', content_hash: 'two-hash' }] }
    vi.mocked(api.listToolSkills).mockResolvedValue({ skills: [shared], diagnostics: [], source: 'core' })
    vi.mocked(api.getToolSkill).mockResolvedValue(shared)
    vi.mocked(api.getToolSkillFile).mockImplementation(async (_id, path) => ({ path, content_hash: path.startsWith('references/') ? 'one-hash' : 'two-hash', size_bytes: 3, content: null, base64: btoa(path.startsWith('references/') ? 'one' : 'two') }))
    vi.mocked(api.saveToolSkill).mockResolvedValue(shared)
    const wrapper = create('skills'); await flushPromises()
    await wrapper.get('.tools-resource-row button').trigger('click'); await flushPromises()
    expect(wrapper.findAll('code').map(code => code.text())).toEqual(['references/one.md', 'scripts/two.sh'])
    expect(api.getToolSkillFile).toHaveBeenCalledWith(id, 'references/one.md', 'p1')
    expect(wrapper.emitted('dirty')?.at(-1)).toEqual([false])
    await wrapper.findAll('.tools-resource-editor button').find(button => button.text() === '移除此附件')!.trigger('click')
    await wrapper.findAll('.tools-resource-editor button').at(-1)!.trigger('click'); await flushPromises()
    expect(api.saveToolSkill).toHaveBeenCalledWith(id, { content: skill.content, enabled: true, files: { 'scripts/two.sh': btoa('two') }, replace_files: true, expected_file_hash: 'file-hash' }, 1, 'p1')
    wrapper.unmount()
  })
  it('keeps saving disabled when a package file changed after its manifest was read', async () => {
    const shared = { ...skill, scope: 'shared', project_id: null, package_files: [{ path: 'references/one.md', content_hash: 'reviewed-hash' }] }
    vi.mocked(api.listToolSkills).mockResolvedValue({ skills: [shared], diagnostics: [], source: 'core' })
    vi.mocked(api.getToolSkill).mockResolvedValue(shared)
    vi.mocked(api.getToolSkillFile).mockResolvedValue({ path: 'references/one.md', content_hash: 'new-hash', size_bytes: 3, content: 'new', base64: btoa('new') })
    const wrapper = create('skills'); await flushPromises()
    await wrapper.get('.tools-resource-row button').trigger('click'); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('重新打开')
    expect(wrapper.findAll('.tools-resource-editor button').at(-1)!.attributes('disabled')).toBeDefined()
    await (wrapper.vm as unknown as { save: () => Promise<boolean> }).save()
    expect(api.saveToolSkill).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('discards a delayed package file read when the selected project changes', async () => {
    const shared = { ...skill, scope: 'shared', project_id: null, package_files: [{ path: 'references/one.md' }] }
    vi.mocked(api.listToolSkills).mockResolvedValue({ skills: [shared], diagnostics: [], source: 'core' })
    vi.mocked(api.getToolSkill).mockResolvedValue(shared)
    let settle!: (value: Awaited<ReturnType<typeof api.getToolSkillFile>>) => void
    vi.mocked(api.getToolSkillFile).mockReturnValueOnce(new Promise(resolve => { settle = resolve }))
    const wrapper = create('skills'); await flushPromises()
    await wrapper.get('.tools-resource-row button').trigger('click'); await flushPromises()
    await wrapper.setProps({ projectId: 'p2' }); await flushPromises()
    settle({ path: 'references/one.md', content_hash: 'hash', size_bytes: 3, content: 'old', base64: btoa('old') }); await flushPromises()
    expect(wrapper.find('.tools-resource-editor').exists()).toBe(false)
    expect(wrapper.get('.tools-resources').attributes('aria-busy')).toBe('false')
    wrapper.unmount()
  })
  it('refreshes and displays a rejected governed action, then stops polling', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = create('skills'); await flushPromises()
      await wrapper.get('select').setValue('project'); await flushPromises()
      await wrapper.get('.tools-resource-row button').trigger('click'); await flushPromises()
      await wrapper.get('textarea').setValue(skill.content + '\nEdited')
      vi.mocked(api.saveToolSkill).mockResolvedValue({ ...skill, user_action_id: 'action-1', action_status: 'awaiting_user' })
      vi.mocked(api.getUserToolAction).mockResolvedValue({ id: 'action-1', status: 'blocked', message: 'Rejected by owner' } as Awaited<ReturnType<typeof api.getUserToolAction>>)
      await wrapper.findAll('.tools-resource-editor button').at(-1)!.trigger('click'); await flushPromises()
      await vi.advanceTimersByTimeAsync(12_000); await flushPromises()
      expect(wrapper.get('[role="alert"]').text()).toContain('Rejected by owner')
      expect(wrapper.get('.tools-resource-result').text()).toContain('受阻')
      await vi.advanceTimersByTimeAsync(60_000)
      expect(api.getUserToolAction).toHaveBeenCalledTimes(1)
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })
})
