// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import { __resetNotificationsForTests, useNotifications } from '@/composables/useNotifications'
import { resetHostAccessForTests, setGatewayConnectionLiveness, setHostAccessStatus } from '@/lib/hostAccess'

const apiMocks = vi.hoisted(() => ({
  listAgentPacks: vi.fn(),
  getWorkspaceDefaults: vi.fn(),
  listLifecycleHooks: vi.fn(),
  listConfiguredMcpServers: vi.fn(),
  listToolSkills: vi.fn(),
  getToolSettingsDefaults: vi.fn(),
  getEffectiveToolSettings: vi.fn(),
  getToolCapabilities: vi.fn(),
}))

const generatedApiMocks = vi.hoisted(() => ({
  listProjects: vi.fn(),
}))

const sessionRosterMocks = vi.hoisted(() => ({
  loadSessionCatalog: vi.fn(),
}))

const terminalMocks = vi.hoisted(() => ({
  creationError: null as { value: { message: string; retryable: boolean } | null } | null,
  terminals: null as { value: Array<{ id: string; title: string; exited: boolean; sourceKind: string }> } | null,
  activeTerminalId: null as { value: string | null } | null,
  createTerminal: vi.fn(),
}))

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      listAgentPacks: apiMocks.listAgentPacks,
      getWorkspaceDefaults: apiMocks.getWorkspaceDefaults,
      listLifecycleHooks: apiMocks.listLifecycleHooks,
      listConfiguredMcpServers: apiMocks.listConfiguredMcpServers,
      listToolSkills: apiMocks.listToolSkills,
      getToolSettingsDefaults: apiMocks.getToolSettingsDefaults,
      getEffectiveToolSettings: apiMocks.getEffectiveToolSettings,
      getToolCapabilities: apiMocks.getToolCapabilities,
    },
  }
})

vi.mock('@/generated/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/generated/client')>()
  return {
    ...actual,
    generatedApi: {
      ...actual.generatedApi,
      listProjects: generatedApiMocks.listProjects,
    },
  }
})

vi.mock('@/lib/sessionRoster', () => ({
  loadSessionCatalog: sessionRosterMocks.loadSessionCatalog,
}))

vi.mock('@/composables/useTerminal', async () => {
  const { ref: vueRef, shallowRef } = await import('vue')
  const creationError = vueRef<{ message: string; retryable: boolean } | null>(null)
  const terminals = vueRef<Array<{ id: string; title: string; exited: boolean; sourceKind: string }>>([])
  const activeTerminalId = vueRef<string | null>(null)
  terminalMocks.creationError = creationError
  terminalMocks.terminals = terminals
  terminalMocks.activeTerminalId = activeTerminalId
  return {
    useTerminal: () => ({
      terminals,
      activeTerminalId,
      availableShells: shallowRef([]),
      shellsLoaded: vueRef(true),
      creationError,
      loadShells: vi.fn(async () => {}),
      createTerminal: terminalMocks.createTerminal,
      closeTerminal: vi.fn(),
      killTerminal: vi.fn(),
      setActiveTerminal: vi.fn(),
      fitTerminal: vi.fn(),
      focusTerminal: vi.fn(),
      fitAllTerminals: vi.fn(),
      clearCreationError: () => {
        creationError.value = null
      },
      isTerminalAvailable: () => true,
    }),
  }
})

vi.mock('@/components/ui', () => ({
  UiBadge: defineComponent({
    name: 'UiBadge',
    setup(_, { slots, attrs }) {
      return () => h('span', attrs, slots.default?.())
    },
  }),
  UiButton: defineComponent({
    name: 'UiButton',
    props: { disabled: Boolean },
    emits: ['click'],
    setup(props, { slots, attrs, emit }) {
      return () =>
        h(
          'button',
          { ...attrs, type: 'button', disabled: props.disabled, onClick: (e: MouseEvent) => emit('click', e) },
          slots.default?.(),
        )
    },
  }),
  UiLabel: defineComponent({
    name: 'UiLabel',
    setup(_, { slots, attrs }) {
      return () => h('label', attrs, slots.default?.())
    },
  }),
  UiInput: defineComponent({
    name: 'UiInput',
    props: { modelValue: [String, Number], disabled: Boolean },
    emits: ['update:modelValue'],
    setup(props, { attrs, emit }) {
      return () =>
        h('input', {
          ...attrs,
          value: props.modelValue,
          disabled: props.disabled,
          onInput: (e: Event) => emit('update:modelValue', (e.target as HTMLInputElement).value),
        })
    },
  }),
  UiTextarea: defineComponent({
    name: 'UiTextarea',
    props: { modelValue: String, disabled: Boolean },
    emits: ['update:modelValue'],
    setup(props, { attrs, emit }) {
      return () =>
        h('textarea', {
          ...attrs,
          value: props.modelValue,
          disabled: props.disabled,
          onInput: (e: Event) => emit('update:modelValue', (e.target as HTMLTextAreaElement).value),
        })
    },
  }),
  UiSelectField: defineComponent({
    name: 'UiSelectField',
    props: { modelValue: String, disabled: Boolean, options: Array },
    emits: ['update:modelValue'],
    setup(props, { attrs }) {
      return () => h('select', { ...attrs, value: props.modelValue, disabled: props.disabled })
    },
  }),
  UiSwitch: defineComponent({
    name: 'UiSwitch',
    props: { modelValue: Boolean, disabled: Boolean },
    emits: ['update:modelValue'],
    setup(props, { attrs, emit }) {
      return () =>
        h('button', {
          ...attrs,
          type: 'button',
          role: 'switch',
          disabled: props.disabled,
          onClick: () => emit('update:modelValue', !props.modelValue),
        })
    },
  }),
  UiSheet: defineComponent({
    name: 'UiSheet',
    setup(_, { slots }) {
      return () => h('div', slots.default?.())
    },
  }),
  UiDropdownMenu: defineComponent({
    name: 'UiDropdownMenu',
    setup(_, { slots }) {
      return () => h('div', [slots.trigger?.(), slots.default?.()])
    },
  }),
}))

import AgentPacksPanel from './AgentPacksPanel.vue'
import ArchiveTrashSection from './ArchiveTrashSection.vue'
import WebAccessSection from './WebAccessSection.vue'
import LifecycleHooksPanel from '@/settings/agentCenter/LifecycleHooksPanel.vue'
import McpServersPanel from '@/settings/toolCenter/McpServersPanel.vue'
import SkillsLibraryPanel from '@/settings/toolCenter/SkillsLibraryPanel.vue'
import ToolsAdvancedPanel from '@/settings/toolCenter/ToolsAdvancedPanel.vue'
import TerminalPanel from '@/components/TerminalPanel.vue'

const wrappers: VueWrapper[] = []

function buildI18n() {
  return createI18n({
    legacy: false,
    locale: 'en',
    messages: { en, 'zh-CN': zhCN },
  })
}

function mountWithI18n(component: unknown, props: Record<string, unknown> = {}) {
  const wrapper = mount(component as ReturnType<typeof defineComponent>, {
    props,
    global: {
      plugins: [buildI18n()],
      stubs: {
        TerminalView: true,
        McpEditorModal: true,
        SkillDetailDrawer: true,
        SkillInstallModal: true,
        ToolJsonEditor: true,
      },
    },
  })
  wrappers.push(wrapper)
  return wrapper
}

beforeEach(() => {
  vi.clearAllMocks()
  __resetNotificationsForTests()
  resetHostAccessForTests()
  setHostAccessStatus({ state: 'ready', managed: true })
  if (terminalMocks.creationError) terminalMocks.creationError.value = null
  if (terminalMocks.terminals) terminalMocks.terminals.value = []
  if (terminalMocks.activeTerminalId) terminalMocks.activeTerminalId.value = null
})

afterEach(() => {
  wrappers.splice(0).forEach((w) => w.unmount())
  __resetNotificationsForTests()
  resetHostAccessForTests()
  vi.unstubAllGlobals()
})

describe('Settings, Tool Center & Terminal panels notification unification', () => {
  it('AgentPacksPanel: removes agent-pack-error inline banner, emits key agent-packs-panel, and clears on recovery', async () => {
    const { items } = useNotifications()
    apiMocks.listAgentPacks.mockRejectedValueOnce(new Error('Pack inventory offline'))
    apiMocks.getWorkspaceDefaults.mockResolvedValueOnce({ default_mode_version_id: null })

    const wrapper = mountWithI18n(AgentPacksPanel)
    await flushPromises()

    expect(wrapper.find('.agent-pack-error').exists()).toBe(false)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(items.value.some((n) => n.key === 'agent-packs-panel')).toBe(true)

    apiMocks.listAgentPacks.mockResolvedValueOnce([])
    apiMocks.getWorkspaceDefaults.mockResolvedValueOnce({ default_mode_version_id: null })
    setGatewayConnectionLiveness(true, false)
    await flushPromises()
    setGatewayConnectionLiveness(true, true)
    await flushPromises()

    expect(items.value.some((n) => n.key === 'agent-packs-panel')).toBe(false)
  })

  it('ArchiveTrashSection: removes archive-trash-failures banner, reports key archive-trash-load, and clears on recovery', async () => {
    const { items } = useNotifications()
    generatedApiMocks.listProjects.mockRejectedValue(new Error('Archive read failed'))
    sessionRosterMocks.loadSessionCatalog.mockResolvedValue({ rows: [], failures: [] })

    const wrapper = mountWithI18n(ArchiveTrashSection)
    await flushPromises()

    expect(wrapper.find('.archive-trash-failures').exists()).toBe(false)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(items.value.some((n) => n.key === 'archive-trash-load')).toBe(true)

    generatedApiMocks.listProjects.mockResolvedValue([])
    setGatewayConnectionLiveness(true, false)
    await flushPromises()
    setGatewayConnectionLiveness(true, true)
    await flushPromises()

    expect(items.value.some((n) => n.key === 'archive-trash-load')).toBe(false)
  })

  it('LifecycleHooksPanel & WebAccessSection: remove lh-error / web-access-alert banners and recover on canAccessBackend', async () => {
    const { items } = useNotifications()
    apiMocks.listLifecycleHooks.mockRejectedValueOnce(new Error('Hooks API failed'))

    const hooksWrapper = mountWithI18n(LifecycleHooksPanel)
    await flushPromises()

    expect(hooksWrapper.find('.lh-error').exists()).toBe(false)
    expect(hooksWrapper.find('[role="alert"]').exists()).toBe(false)
    expect(items.value.some((n) => n.key === 'lifecycle-hooks')).toBe(true)

    const fetchMock = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ detail: 'Gateway bad' }), { status: 503 }))
    vi.stubGlobal('fetch', fetchMock)
    const webWrapper = mountWithI18n(WebAccessSection)
    await flushPromises()

    expect(webWrapper.find('.web-access-alert').exists()).toBe(false)
    expect(items.value.some((n) => n.key === 'web-access')).toBe(true)

    apiMocks.listLifecycleHooks.mockResolvedValueOnce({ hooks: [], revision: 1, workspace_root: null })
    fetchMock.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          allow_loopback_auto_login: true,
          password_configured: false,
          oidc_enabled: false,
          allowed_subjects: [],
          allowed_emails: [],
          cloudflare_access_enabled: false,
          cloudflare_access_team_domain: '',
          cloudflare_access_aud: '',
          session_ttl_seconds: 3600,
          trust_proxy: 'false',
          updated_at: '2026-11-24T10:00:00Z',
        }),
        { status: 200 },
      ),
    )

    setGatewayConnectionLiveness(true, false)
    await flushPromises()
    setGatewayConnectionLiveness(true, true)
    await flushPromises()

    expect(items.value.some((n) => n.key === 'lifecycle-hooks')).toBe(false)
    expect(items.value.some((n) => n.key === 'web-access')).toBe(false)
  })

  it('McpServersPanel, SkillsLibraryPanel & ToolsAdvancedPanel: remove error-hint banners, use calm empty/retry, and recover', async () => {
    const { items } = useNotifications()
    apiMocks.listConfiguredMcpServers.mockRejectedValueOnce(new Error('MCP list error'))
    apiMocks.listToolSkills.mockRejectedValueOnce(new Error('Skills list error'))

    const mcpWrapper = mountWithI18n(McpServersPanel)
    const skillsWrapper = mountWithI18n(SkillsLibraryPanel)
    const advWrapper = mountWithI18n(ToolsAdvancedPanel, {
      agentId: '',
      agents: [],
      loading: false,
      saving: false,
      dirty: false,
      stale: false,
      error: 'Tool settings load error',
      editorSchema: null,
      draft: '',
      validationIssues: [{ path: 'shell.timeout', message: 'Must be positive' }],
      diff: [],
      document: null,
      effective: null,
      capabilities: null,
    })
    await flushPromises()

    expect(mcpWrapper.find('.error-hint').exists()).toBe(false)
    expect(skillsWrapper.find('.error-hint').exists()).toBe(false)
    expect(advWrapper.find('.error-hint').exists()).toBe(false)

    expect(items.value.some((n) => n.key === 'mcp-servers')).toBe(true)
    expect(items.value.some((n) => n.key === 'skills-library')).toBe(true)
    expect(items.value.some((n) => n.key === 'tools-advanced')).toBe(true)

    apiMocks.listConfiguredMcpServers.mockResolvedValueOnce([])
    apiMocks.listToolSkills.mockResolvedValueOnce({ skills: [] })
    apiMocks.getToolSettingsDefaults.mockResolvedValueOnce({ revision: 2, settings: {} })
    apiMocks.getEffectiveToolSettings.mockResolvedValueOnce({ resource_diagnostics: [] })
    apiMocks.getToolCapabilities.mockResolvedValueOnce({ capabilities: null })

    setGatewayConnectionLiveness(true, false)
    await flushPromises()
    setGatewayConnectionLiveness(true, true)
    await flushPromises()

    expect(items.value.some((n) => n.key === 'mcp-servers')).toBe(false)
    expect(items.value.some((n) => n.key === 'skills-library')).toBe(false)
    expect(items.value.some((n) => n.key === 'tools-advanced')).toBe(false)
    // Once document recovered, inline JSON schema validation list remains preserved
    expect(advWrapper.find('.tool-center-validation-list').exists()).toBe(true)
  })

  it('TerminalPanel: removes terminal-error inline banner, reports key terminal-create, and clears on successful retry or unmount', async () => {
    const { items } = useNotifications()
    terminalMocks.createTerminal.mockImplementationOnce(async () => {
      terminalMocks.creationError!.value = { message: 'PTY spawn failed', retryable: true }
      return null
    })

    const wrapper = mountWithI18n(TerminalPanel, { visible: true })
    await flushPromises()

    expect(wrapper.find('.terminal-error').exists()).toBe(false)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="terminal-empty-action"]').text()).toContain('Retry')
    expect(items.value.some((n) => n.key === 'terminal-create')).toBe(true)

    // Retry succeeds -> clears 'terminal-create'
    terminalMocks.createTerminal.mockImplementationOnce(async () => {
      terminalMocks.creationError!.value = null
      const inst = { id: 'term-1', title: 'pwsh', exited: false, sourceKind: 'local' }
      terminalMocks.terminals!.value = [inst]
      terminalMocks.activeTerminalId!.value = 'term-1'
      return inst
    })
    await wrapper.get('[data-testid="terminal-empty-action"]').trigger('click')
    await flushPromises()

    expect(items.value.some((n) => n.key === 'terminal-create')).toBe(false)
  })
})
