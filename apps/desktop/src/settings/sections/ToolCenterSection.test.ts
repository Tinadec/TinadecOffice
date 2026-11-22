// @vitest-environment happy-dom
import { defineComponent } from 'vue'
import { mount, flushPromises } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import zh from '@/locales/zh-CN'
import { api } from '@/api'
import ToolCenterSection from './ToolCenterSection.vue'

const notify = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => ({ notify }) }))

vi.mock('@/components/ui', () => ({
  UiButton: { template: '<button v-bind="$attrs"><slot /></button>' },
  UiBadge: { template: '<span><slot /></span>' },
  UiInput: {
    props: ['modelValue'],
    emits: ['update:modelValue'],
    template: '<input v-bind="$attrs" :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />',
  },
  UiTextarea: {
    props: ['modelValue'],
    emits: ['update:modelValue'],
    template: '<textarea v-bind="$attrs" :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />',
  },
  UiSwitch: {
    props: ['modelValue', 'disabled'],
    emits: ['update:modelValue'],
    template: '<input type="checkbox" v-bind="$attrs" :checked="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.checked)" />',
  },
  UiSheet: {
    props: ['open'],
    emits: ['update:open'],
    template: '<div v-if="open"><slot /></div>',
  },
  UiDropdownMenu: { template: '<div><slot name="trigger" /><slot /></div>' },
  UiSelectField: {
    props: ['value', 'modelValue', 'options', 'disabled'],
    emits: ['update:modelValue', 'update:value', 'change'],
    setup(_props: unknown, { emit }: { emit: (event: string, value: string) => void }) {
      const pick = (event: Event) => {
        const next = (event.target as HTMLSelectElement).value
        emit('update:modelValue', next)
        emit('update:value', next)
        emit('change', next)
      }
      return { pick }
    },
    template: '<span class="ui-select"><select :value="value ?? modelValue" :disabled="disabled" @change="pick"><option v-for="option in options ?? []" :key="option.value" :value="option.value">{{ option.label }}</option></select></span>',
  },
}))

vi.mock('@/api', () => ({
  api: {
    getToolSettingsSchema: vi.fn(),
    getToolSettingsDefaults: vi.fn(),
    getAgentToolSettings: vi.fn(),
    getEffectiveToolSettings: vi.fn(),
    saveToolSettingsDefaults: vi.fn(),
    saveAgentToolSettings: vi.fn(),
    listAgentDefinitions: vi.fn(),
    listProjects: vi.fn(),
    listTools: vi.fn(),
    getToolCapabilities: vi.fn(),
    listConfiguredMcpServers: vi.fn(),
    setMcpServerEnabled: vi.fn(),
    testMcpServer: vi.fn(),
    createMcpServer: vi.fn(),
    updateMcpServer: vi.fn(),
    importMcpServersJson: vi.fn(),
    deleteMcpServer: vi.fn(),
    listToolSkills: vi.fn(),
    getToolSkill: vi.fn(),
    getToolSkillFile: vi.fn(),
    saveToolSkill: vi.fn(),
    deleteToolSkill: vi.fn(),
    copyToolSkillToProject: vi.fn(),
    setToolSkillEnabled: vi.fn(),
    reloadToolSkills: vi.fn(),
    discoverExternalCliTools: vi.fn(),
    installToolSkill: vi.fn(),
  },
}))

const initial = { shell: { enabled: true, timeout_ms: 100 } }
const contract = {
  schema_version: 1,
  defaults: initial,
  schema: {
    type: 'object',
    additionalProperties: false,
    properties: {
      shell: {
        type: 'object',
        additionalProperties: false,
        properties: {
          enabled: { type: 'boolean' },
          timeout_ms: { type: 'integer', minimum: 1, maximum: 1000 },
        },
      },
    },
  },
}

const JsonEditor = defineComponent({
  props: ['modelValue'],
  emits: ['update:modelValue', 'save'],
  template: '<textarea class="editor" :value="modelValue" @input="$emit(\'update:modelValue\', $event.target.value)" />',
})
const DraftDialog = defineComponent({
  emits: ['choice'],
  template: '<div role="dialog"><button @click="$emit(\'choice\', \'save\')">Save</button><button @click="$emit(\'choice\', \'discard\')">Discard</button><button @click="$emit(\'choice\', \'cancel\')">Cancel</button></div>',
})

function create(attach = false) {
  return mount(ToolCenterSection, {
    ...(attach ? { attachTo: document.body } : {}),
    global: {
      plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })],
      stubs: { ToolJsonEditor: JsonEditor, ToolDraftDialog: DraftDialog },
    },
  })
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.getToolSettingsSchema).mockResolvedValue(contract)
  vi.mocked(api.getToolSettingsDefaults).mockResolvedValue({
    schema_version: 1,
    revision: 4,
    settings: initial,
    effective_settings: initial,
    settings_hash: 'shared',
  })
  vi.mocked(api.getAgentToolSettings).mockResolvedValue({
    schema_version: 1,
    revision: 0,
    settings: {},
    effective_settings: initial,
    settings_hash: 'agent',
  })
  vi.mocked(api.getEffectiveToolSettings).mockResolvedValue({
    settings_hash: 'effective',
    settings: initial,
    allowed_tool_ids: ['read_file', 'shell'],
  })
  vi.mocked(api.listAgentDefinitions).mockResolvedValue([{ id: 'agent-a', display_name: 'Research', slug: 'research' }] as never)
  vi.mocked(api.listProjects).mockResolvedValue([{ id: 'project-a', name: 'Project A' }] as never)
  vi.mocked(api.listTools).mockResolvedValue([
    { id: 'read_file', description: 'Read a workspace file', readonly: true, risk_level: 'low' },
    { id: 'shell', description: 'Run a shell command', requires_approval: true, risk_level: 'high' },
  ] as never)
  vi.mocked(api.getToolCapabilities).mockResolvedValue({ status: 'unavailable', reason: 'test host' })
  vi.mocked(api.listConfiguredMcpServers).mockResolvedValue([
    {
      resource_id: 'mcp-1',
      id: 'fs',
      name: 'Filesystem',
      enabled: true,
      transport: 'stdio',
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-filesystem'],
      env: { API_TOKEN: '********' },
      revision: 1,
      configuration_hash: 'h1',
    },
  ])
  vi.mocked(api.setMcpServerEnabled).mockImplementation(async (_id, enabled) => ({
    resource_id: 'mcp-1',
    id: 'fs',
    name: 'Filesystem',
    enabled,
    transport: 'stdio',
    command: 'npx',
    args: [],
    env: {},
    revision: 2,
    configuration_hash: 'h2',
  }))
  vi.mocked(api.testMcpServer).mockResolvedValue({
    resource_id: 'mcp-1',
    id: 'fs',
    name: 'Filesystem',
    enabled: true,
    status: 'connected',
    tool_count: 1,
    tools: [{ id: 'read_file', name: 'read_file', description: 'Reads a file' }],
  })
  vi.mocked(api.listToolSkills).mockResolvedValue({
    skills: [
      {
        resource_id: 'skill-1',
        name: 'code-review',
        description: 'Structured review checklist',
        enabled: true,
        scope: 'user',
        source_kind: 'user',
        content: '# Code Review',
      },
    ],
    diagnostics: [],
    source: 'merged',
  })
  vi.mocked(api.saveToolSettingsDefaults).mockImplementation(async settings => ({
    schema_version: 1,
    revision: 5,
    settings,
    effective_settings: settings,
    settings_hash: 'saved',
  }))
})

describe('ToolCenterSection (Wave 3 4-tab rebuild)', () => {
  it('renders the 4 functional tabs and supports keyboard arrow navigation', async () => {
    const wrapper = create(true)
    await flushPromises()
    expect(wrapper.findAll('[role="tab"]')).toHaveLength(4)
    expect(wrapper.get('#tools-tab-mcp').attributes('aria-selected')).toBe('true')
    await wrapper.get('#tools-tab-mcp').trigger('keydown', { key: 'ArrowRight' })
    await flushPromises()
    expect(wrapper.get('#tools-tab-skills').attributes('aria-selected')).toBe('true')
    expect(document.activeElement?.id).toBe('tools-tab-skills')
    await wrapper.get('#tools-tab-skills').trigger('keydown', { key: 'End' })
    await flushPromises()
    expect(wrapper.get('#tools-tab-advanced').attributes('aria-selected')).toBe('true')
    wrapper.unmount()
  })

  it('lists MCP servers, toggles enabled state, and opens live tools drawer after testing', async () => {
    const wrapper = create()
    await flushPromises()
    const card = wrapper.get('[data-testid="mcp-card-fs"]')
    expect(card.text()).toContain('Filesystem')

    await wrapper.get('[data-testid="mcp-card-fs"] button').trigger('click')
    await flushPromises()
    expect(api.testMcpServer).toHaveBeenCalledWith('fs', undefined)
    expect(wrapper.text()).toContain(zh.toolsSettings.center.mcp.liveToolsTitle)

    await card.find('input[type="checkbox"]').setValue(false)
    await flushPromises()
    expect(api.setMcpServerEnabled).toHaveBeenCalledWith('fs', false, undefined)
    wrapper.unmount()
  })

  it('renders built-in tools search & category table in builtin tab', async () => {
    const wrapper = create()
    await flushPromises()
    await wrapper.get('#tools-tab-builtin').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('.tool-discovery-card')).toHaveLength(1)
    expect(wrapper.get('.tool-discovery-card').text()).toContain('read_file')
    expect(wrapper.findAll('.tool-builtin-row')).toHaveLength(2)
    wrapper.unmount()
  })

  it('shows diagnostics and guards unsaved strict JSON edits in advanced tab via canLeave()', async () => {
    vi.mocked(api.getEffectiveToolSettings).mockResolvedValueOnce({
      settings_hash: 'effective',
      settings: initial,
      resource_diagnostics: [{ kind: 'skills', resource_id: 'removed', status: 'missing', reason: 'Missing file' }],
    })
    const wrapper = create()
    await flushPromises()
    await wrapper.get('#tools-tab-advanced').trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="status"]').text()).toContain(zh.toolsSettings.diagnostic.skills_missing)

    await wrapper.get('textarea.editor').setValue('{"shell":{"enabled":true,"timeout_ms":250}}')
    await flushPromises()
    const leavePromise = (wrapper.vm as unknown as { canLeave: () => Promise<boolean> }).canLeave()
    await flushPromises()
    expect(wrapper.find('[role="dialog"]').exists()).toBe(true)
    await wrapper.get('[role="dialog"] button:nth-child(2)').trigger('click')
    expect(await leavePromise).toBe(true)
    wrapper.unmount()
  })

  it('imports apm.yml MCP config with ${VAR} secret_values routed to Core', async () => {
    vi.mocked(api.importMcpServersJson).mockResolvedValueOnce({
      imported: [
        {
          resource_id: 'mcp-gh',
          id: 'github',
          name: 'GitHub',
          enabled: true,
          transport: 'stdio',
          command: 'npx',
          args: ['-y', '@modelcontextprotocol/server-github'],
          env: { GITHUB_TOKEN: '********' },
          revision: 1,
          configuration_hash: 'h-gh',
        },
      ],
      skipped: [],
      imported_count: 1,
      skipped_count: 0,
      secret_placeholders: ['GITHUB_TOKEN'],
    } as never)

    const wrapper = create()
    await flushPromises()
    await wrapper.get('[data-testid="add-mcp-btn"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="mcp-mode-import-tab"]').trigger('click')
    await flushPromises()

    const apmYml = [
      'dependencies:',
      '  mcp:',
      '    - name: github',
      '      command: npx',
      '      env:',
      '        GITHUB_TOKEN: "${GITHUB_TOKEN}"',
      '        SLACK_HOOK: "${env:SLACK_SECRET}"',
    ].join('\n')

    await wrapper.get('[data-testid="mcp-import-textarea"]').setValue(apmYml)
    await flushPromises()
    expect(wrapper.get('[data-testid="mcp-detected-format"]').text()).toContain('apm.yml')
    expect(wrapper.find('[data-testid="mcp-secret-placeholders"]').exists()).toBe(true)

    await wrapper.get('[data-testid="mcp-secret-input-GITHUB_TOKEN"]').setValue('ghp_secret_42')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(api.importMcpServersJson).toHaveBeenCalledWith(
      expect.objectContaining({
        json: apmYml,
        import_source: 'apm',
        secret_values: { GITHUB_TOKEN: 'ghp_secret_42' },
      }),
    )
    wrapper.unmount()
  })

  it('surfaces Microsoft APM discovery, hidden Unicode warnings, Commit/SHA-256 provenance, and one-click APM agent import', async () => {
    vi.mocked(api.listToolSkills).mockResolvedValue({
      skills: [
        {
          resource_id: 'skill-apm',
          name: 'apm-audit',
          description: 'Supply chain checklist',
          enabled: true,
          scope: 'project',
          source: 'apm',
          source_kind: 'project',
          commit: 'a1b2c3d4e5f67890',
          package_hash: 'sha256:9f86d081884c7d659a2feaa0c55ad015',
          has_hidden_unicode: true,
          security_warnings: ['Zero-width character detected in SKILL.md'],
        } as never,
      ],
      diagnostics: [],
      source: 'merged',
    })
    vi.mocked(api.discoverExternalCliTools).mockResolvedValue({
      user_home: 'C:/Users/tester',
      workspace_root: 'C:/repo',
      ecosystems: [
        { id: 'apm', label: 'Microsoft APM', detected: true, paths_scanned: ['C:/repo/apm.yml'] },
        { id: 'claude_code', label: 'Claude Code', detected: false, paths_scanned: [] },
      ],
      items: [
        {
          item_id: 'apm:skill:audit',
          ecosystem: 'apm',
          kind: 'skill',
          name: 'audit',
          display_name: 'APM Audit Skill',
          source_path: '.apm/skills/audit/SKILL.md',
          has_secrets: false,
          default_enabled: true,
          already_imported: false,
          commit: 'deadbeefcafebabe',
          content_hash: 'sha256:abcdef0123456789abcdef',
          has_hidden_unicode: true,
          security_warnings: ['Bidi override detected'],
        },
        {
          item_id: 'apm:agent:reviewer',
          ecosystem: 'apm',
          kind: 'agent',
          name: 'reviewer',
          display_name: 'APM Security Reviewer',
          source_path: '.apm/agents/reviewer.agent.md',
          has_secrets: false,
          default_enabled: true,
          already_imported: false,
        },
      ] as never,
    })
    vi.mocked(api.installToolSkill).mockResolvedValueOnce({
      source_type: 'external_cli_import',
      installed_skills: [],
      installed_mcp_servers: [],
      installed_commands: [],
      imported_agents: [{ id: 'agent-apm-1', slug: 'reviewer', display_name: 'APM Security Reviewer' }],
      skipped: [],
    } as never)

    const wrapper = create()
    await flushPromises()
    await wrapper.get('#tools-tab-skills').trigger('click')
    await flushPromises()

    const card = wrapper.get('[data-testid="skill-card-apm-audit"]')
    expect(card.get('[data-testid="skill-unicode-warning-apm-audit"]').text()).toContain('隐藏 Unicode 风险')
    expect(card.get('[data-testid="skill-commit-apm-audit"]').text()).toContain('a1b2c3d4')
    expect(card.get('[data-testid="skill-digest-apm-audit"]').text()).toContain('SHA-256')

    await wrapper.get('[data-testid="add-skill-external-cli"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="ecosystem-chip-apm"]').text()).toContain('Microsoft APM')
    expect(wrapper.get('[data-testid="external-unicode-warning-apm:skill:audit"]').text()).toContain('隐藏 Unicode 风险')
    expect(wrapper.get('[data-testid="external-commit-apm:skill:audit"]').text()).toContain('deadbeef')
    expect(wrapper.get('[data-testid="external-digest-apm:skill:audit"]').text()).toContain('SHA-256')

    await wrapper.get('[data-testid="import-apm-agent-reviewer"]').trigger('click')
    await flushPromises()

    expect(api.installToolSkill).toHaveBeenCalledWith(
      expect.objectContaining({
        source_type: 'external_cli_import',
        item_ids: ['apm:agent:reviewer'],
      }),
    )
    expect(api.listAgentDefinitions).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })
})
