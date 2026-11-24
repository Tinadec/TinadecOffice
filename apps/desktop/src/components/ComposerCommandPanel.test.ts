// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import type { AgentModeTopologyDto, SpaceOptionsDto } from '@/api'
import type { CommandHost } from '@/lib/appCommands'
import { defaultSpaceOptions } from '@/lib/composerSettings'

const apiMocks = vi.hoisted(() => ({
  listAgentModeTopologies: vi.fn<() => Promise<AgentModeTopologyDto[]>>(),
}))

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      listAgentModeTopologies: apiMocks.listAgentModeTopologies,
    },
  }
})

vi.mock('@/composables/useWorkspaceCommands', () => ({
  useWorkspaceCommands: () => ({
    commands: { value: [] },
    refresh: vi.fn(),
  }),
  renderWorkspaceCommandTemplate: (cmd: { template?: string }) => cmd.template ?? '',
}))

// Classic-VDOM stubs for `<template vapor>` primitives so @vue/test-utils renders cleanly in Node CJS/ESM.
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
    props: {
      modelValue: String,
      disabled: Boolean,
      options: { type: Array as () => Array<{ value: string; label: string; disabled?: boolean }>, default: () => [] },
    },
    emits: ['update:modelValue'],
    setup(props, { attrs, emit }) {
      return () =>
        h(
          'select',
          {
            ...attrs,
            value: props.modelValue,
            disabled: props.disabled,
            onChange: (e: Event) => emit('update:modelValue', (e.target as HTMLSelectElement).value),
          },
          props.options.map((opt) => h('option', { value: opt.value, disabled: opt.disabled }, opt.label)),
        )
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
          'aria-checked': String(Boolean(props.modelValue)),
          disabled: props.disabled,
          onClick: () => emit('update:modelValue', !props.modelValue),
        })
    },
  }),
  UiCheckbox: defineComponent({
    name: 'UiCheckbox',
    props: { modelValue: Boolean, disabled: Boolean },
    emits: ['update:modelValue'],
    setup(props, { attrs, emit }) {
      return () =>
        h('input', {
          ...attrs,
          type: 'checkbox',
          checked: Boolean(props.modelValue),
          disabled: props.disabled,
          onChange: (e: Event) => emit('update:modelValue', (e.target as HTMLInputElement).checked),
        })
    },
  }),
}))

import ComposerCommandPanel from './ComposerCommandPanel.vue'
import ModeInspectorDrawer from './modeEditor/ModeInspectorDrawer.vue'
import { modeCatalogRevision, type ModeDraftState } from '@/composables/useAgentModesStore'
import { __resetNotificationsForTests, useNotifications } from '@/composables/useNotifications'
import { resetHostAccessForTests, setGatewayConnectionLiveness, setHostAccessStatus } from '@/lib/hostAccess'

const SOLO_MODE: AgentModeTopologyDto = {
  id: 'mode-solo',
  slug: 'solo',
  display_name: 'Solo',
  description: 'Single-agent focused execution',
  status: 'published',
  latest_published_mode_version_id: 'mv-solo-1',
  default_options: {
    plan_first: false,
    spec_enabled: false,
    multi_agent: false,
    bulletin_board: false,
    worktree: false,
    workflow_mode_version_id: null,
    context_strategy: null,
    context_advanced: null,
  },
  nodes: [],
  edges: [],
}

const RESEARCH_MODE: AgentModeTopologyDto = {
  id: 'mode-research',
  slug: 'research',
  display_name: 'Deep Research',
  description: 'Multi-agent curated investigation',
  status: 'published',
  latest_published_mode_version_id: 'mv-research-1',
  default_options: {
    plan_first: true,
    spec_enabled: false,
    multi_agent: true,
    bulletin_board: true,
    worktree: false,
    workflow_mode_version_id: null,
    context_strategy: 'deep_curator',
    context_advanced: {
      compaction_trigger_ratio: 0.85,
      allow_cross_session_recall: true,
    },
  },
  nodes: [],
  edges: [],
}

const commandHostStub: CommandHost = {
  canStop: () => false,
  draft: () => '',
  setDraft: () => {},
  send: () => {},
  stopRun: () => {},
  newSession: () => {},
  navigate: () => {},
  routeName: () => 'home',
  toggleSessionPartner: () => {},
}

const wrappers: VueWrapper[] = []

function buildI18n(locale: 'zh-CN' | 'en' = 'zh-CN') {
  return createI18n({
    legacy: false,
    locale,
    messages: { 'zh-CN': zhCN, en },
  })
}

function mountPanel(props: Record<string, unknown> = {}, locale: 'zh-CN' | 'en' = 'zh-CN') {
  const anchor = document.createElement('div')
  document.body.appendChild(anchor)
  const wrapper = mount(ComposerCommandPanel, {
    attachTo: document.body,
    props: {
      open: true,
      anchor,
      canAttach: true,
      permission: 'default',
      modeVersionId: 'mv-solo-1',
      spaceOptions: defaultSpaceOptions(),
      commandHost: commandHostStub,
      ...props,
    },
    global: {
      plugins: [buildI18n(locale)],
      stubs: { Teleport: true },
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
  apiMocks.listAgentModeTopologies.mockResolvedValue([SOLO_MODE, RESEARCH_MODE])
})

afterEach(() => {
  wrappers.splice(0).forEach((w) => w.unmount())
  __resetNotificationsForTests()
  resetHostAccessForTests()
  document.body.innerHTML = ''
})

describe('ComposerCommandPanel context-strategy submenu & session overlay', () => {
  it('promotes Context Governance Strategy to root with active tier value and purifies the mode submenu', async () => {
    const wrapper = mountPanel({}, 'zh-CN')
    await flushPromises()

    const ctxRow = wrapper.get('[data-testid="command-panel-context-strategy"]')
    expect(ctxRow.text()).toContain('上下文治理策略')
    expect(ctxRow.text()).toContain('标准托管 (managed)')

    // Open mode submenu: no ModeContextStrategySection and no create-mode UI
    await wrapper.get('[data-testid="command-panel-mode"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="mode-context-strategy-section"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="command-panel-context-strategy-page"]').exists()).toBe(false)
    const modeItems = wrapper.findAll('.mode-selector-item')
    expect(modeItems).toHaveLength(2)
    expect(modeItems[0]!.text()).toContain('Solo')
    expect(modeItems[1]!.text()).toContain('Deep Research')
  })

  it('opens dedicated context-strategy submenu without search/empty noise, shows override bar, and resets cleanly to preset', async () => {
    const wrapper = mountPanel({}, 'zh-CN')
    await flushPromises()

    expect(wrapper.find('[data-testid="command-panel-overrides"]').exists()).toBe(false)

    await wrapper.get('[data-testid="command-panel-context-strategy"]').trigger('click')
    await flushPromises()

    // Title uses commandPanel.contextStrategy (never missing commandPanel.context-strategy)
    expect(wrapper.get('.command-panel-header strong').text()).toBe('上下文治理策略')
    // Search input and empty list note are hidden on context-strategy page
    expect(wrapper.find('.command-panel-search').exists()).toBe(false)
    expect(wrapper.find('.command-panel-list').exists()).toBe(false)
    expect(wrapper.find('[data-testid="mode-context-strategy-section"]').exists()).toBe(true)

    // Pick direct tier -> emits update:spaceOptions overlay
    await wrapper.get('[data-testid="context-strategy-tier-direct"]').trigger('click')
    const emittedAfterTier = wrapper.emitted('update:spaceOptions')
    expect(emittedAfterTier).toBeTruthy()
    const nextOverlay = emittedAfterTier![emittedAfterTier!.length - 1]![0] as SpaceOptionsDto
    expect(nextOverlay.context_strategy).toBe('direct')
    expect(nextOverlay.context_advanced).toBeNull()

    // Simulate parent passing updated session overlay prop
    await wrapper.setProps({ spaceOptions: nextOverlay })
    await flushPromises()

    // Override bar shows on context-strategy page with "临时覆盖" + "恢复预设默认"
    const overrideBar = wrapper.get('[data-testid="command-panel-overrides"]')
    expect(overrideBar.text()).toContain('临时覆盖')
    expect(overrideBar.text()).toContain('恢复预设默认')

    // Expand advanced parameters, customize a field, and verify section reset emits null (not {})
    await wrapper.get('[data-testid="context-advanced-toggle"]').trigger('click')
    expect(wrapper.find('[data-testid="context-advanced-panel"]').exists()).toBe(true)
    await wrapper.get('[data-testid="adv-allow-cross-session"]').trigger('click')
    const afterAdv = wrapper.emitted('update:spaceOptions')!
    const advOverlay = afterAdv[afterAdv.length - 1]![0] as SpaceOptionsDto
    expect(advOverlay.context_advanced).toEqual({ allow_cross_session_recall: true })

    await wrapper.setProps({ spaceOptions: advOverlay })
    await flushPromises()

    await wrapper.get('[data-testid="context-strategy-reset-btn"]').trigger('click')
    const emittedAfterSectionReset = wrapper.emitted('update:spaceOptions')!
    const afterSectionReset = emittedAfterSectionReset[emittedAfterSectionReset.length - 1]![0] as SpaceOptionsDto
    expect(afterSectionReset.context_strategy).toBeNull()
    expect(afterSectionReset.context_advanced).toBeNull()

    // Also verify top override bar one-click reset restores full mode preset defaults
    await wrapper.setProps({
      spaceOptions: {
        ...defaultSpaceOptions(),
        plan_first: true,
        context_strategy: 'deep_curator',
        context_advanced: { recent_message_limit: 64 },
      },
    })
    await flushPromises()

    // Override bar also shows on root and mode pages
    await wrapper.get('.command-panel-back').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="command-panel-overrides"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="command-panel-context-strategy"]').text()).toContain('深度策展 (deep_curator)')

    await wrapper.get('[data-testid="command-panel-reset-options"]').trigger('click')
    const emittedAfterTopReset = wrapper.emitted('update:spaceOptions')!
    expect(emittedAfterTopReset[emittedAfterTopReset.length - 1]![0]).toEqual(defaultSpaceOptions())
  })

  it('destroys session overlay and resets cleanly to new mode defaults on mode switch', async () => {
    const wrapper = mountPanel({
      modeVersionId: 'mv-solo-1',
      spaceOptions: {
        ...defaultSpaceOptions(),
        worktree: true,
        context_strategy: 'direct',
        context_advanced: { recent_message_limit: 12 },
      },
    })
    await flushPromises()

    expect(wrapper.find('[data-testid="command-panel-overrides"]').exists()).toBe(true)

    await wrapper.get('[data-testid="command-panel-mode"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="command-panel-overrides"]').exists()).toBe(true)

    // Select Deep Research mode
    await wrapper.get('[data-testid="command-panel-mv-research-1"]').trigger('click')
    const selectEvents = wrapper.emitted('select-mode')
    expect(selectEvents).toHaveLength(1)
    const selectedPayload = selectEvents![0]![0] as { mode_version_id: string | null; space_options: SpaceOptionsDto }
    expect(selectedPayload).toEqual({
      mode_version_id: 'mv-research-1',
      space_options: {
        plan_first: true,
        spec_enabled: false,
        multi_agent: true,
        bulletin_board: true,
        worktree: false,
        workflow_mode_version_id: null,
        context_strategy: 'deep_curator',
        context_advanced: {
          compaction_trigger_ratio: 0.85,
          allow_cross_session_recall: true,
        },
      },
    })

    // Once session applies the new mode & defaults and panel reopens on root, override bar disappears and root row shows new mode's tier
    await wrapper.setProps({ open: false })
    await wrapper.setProps({
      open: true,
      modeVersionId: selectedPayload.mode_version_id,
      spaceOptions: selectedPayload.space_options,
    })
    await flushPromises()
    expect(wrapper.find('[data-testid="command-panel-overrides"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="command-panel-context-strategy"]').text()).toContain('深度策展 (deep_curator)')
  })

  it('reflects Mode Editor defaultOptions updates automatically when modeCatalogRevision increments', async () => {
    const wrapper = mountPanel({
      modeVersionId: 'mv-solo-1',
      spaceOptions: null,
    })
    await flushPromises()
    expect(wrapper.get('[data-testid="command-panel-context-strategy"]').text()).toContain('标准托管 (managed)')

    apiMocks.listAgentModeTopologies.mockResolvedValueOnce([
      {
        ...SOLO_MODE,
        default_options: {
          ...defaultSpaceOptions(),
          context_strategy: 'direct',
        },
      },
      RESEARCH_MODE,
    ])
    modeCatalogRevision.value += 1
    await flushPromises()

    expect(wrapper.get('[data-testid="command-panel-context-strategy"]').text()).toContain('轻量直通 (direct)')
  })

  it('never renders a red alert banner inside the menu on catalog failure, routes to composer-command-catalog, and recovers on canAccessBackend', async () => {
    const { items } = useNotifications()
    apiMocks.listAgentModeTopologies.mockRejectedValueOnce(new Error('Catalog service error'))

    const wrapper = mountPanel({ initialPage: 'mode', modeVersionId: null }, 'en')
    await flushPromises()

    // Zero wild red alert bars inside the command palette menu
    expect(wrapper.find('.command-panel-feedback').exists()).toBe(false)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)

    // Calm minimal empty state + Retry button rendered inside the menu
    expect(wrapper.find('[data-testid="command-panel-empty"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="command-panel-retry"]').exists()).toBe(true)

    // Error routed to Dynamic Island notification with key 'composer-command-catalog'
    expect(items.value.some((n) => n.key === 'composer-command-catalog' && n.level === 'error')).toBe(true)

    // Simulate Gateway disconnect -> reconnect (`canAccessBackend`: false -> true)
    apiMocks.listAgentModeTopologies.mockResolvedValueOnce([SOLO_MODE, RESEARCH_MODE])
    setGatewayConnectionLiveness(true, false)
    await flushPromises()
    setGatewayConnectionLiveness(true, true)
    await flushPromises()

    expect(items.value.some((n) => n.key === 'composer-command-catalog')).toBe(false)
    expect(wrapper.findAll('.mode-selector-item')).toHaveLength(2)
  })
})

describe('ModeInspectorDrawer context_strategy persistence on defaultOptions', () => {
  it('reads legacy canvasLayout fallback, writes to defaultOptions, and resets to null without {}', async () => {
    const baseDraft: ModeDraftState = {
      displayName: 'Custom Mode',
      description: '',
      instructions: '',
      orchestrationTier: 'solo_dispatch',
      defaultOptions: null,
      runtimePolicyKind: 'none',
      runtimePolicyFeatures: [],
      runtimePolicySettingsJson: '',
      nodes: [],
      edges: [],
      bindingsDraft: '[]',
      canvasLayout: {
        zoom: 1,
        context_strategy: 'managed',
        context_advanced: { recent_message_limit: 30 },
      },
    }

    const drawer = mount(ModeInspectorDrawer, {
      props: {
        panel: 'overview',
        readonly: false,
        selectedModeId: 'mode-custom',
        selectedMode: SOLO_MODE,
        draft: baseDraft,
        selectedNode: null,
        selectedEdge: null,
        addNodeLane: 'operation',
        agents: [],
        providers: [],
        routes: [],
        runtimePolicyDefinitions: [],
        policySettingsError: '',
        bindingsError: null,
        modeVersions: [],
        modeVersionsLoading: false,
        modeBusy: false,
      },
      global: {
        plugins: [buildI18n('zh-CN')],
      },
    })
    wrappers.push(drawer)

    // Fallback read from canvasLayout marks section overridden
    expect(drawer.get('[data-testid="context-strategy-state-pill"]').text()).toBe('已覆写')

    // Picking deep_curator writes to defaultOptions AND strips legacy keys from canvasLayout
    await drawer.get('[data-testid="context-strategy-tier-deep_curator"]').trigger('click')
    const updates = drawer.emitted('update-overview')!
    expect(updates).toHaveLength(1)
    expect(updates[0]![0]).toEqual({
      defaultOptions: {
        ...defaultSpaceOptions(),
        context_strategy: 'deep_curator',
        context_advanced: { recent_message_limit: 30 },
      },
      canvasLayout: { zoom: 1 },
    })

    // Resetting restores null (not {}) on defaultOptions in a single emit
    await drawer.setProps({
      draft: {
        ...baseDraft,
        defaultOptions: {
          ...defaultSpaceOptions(),
          plan_first: true,
          context_strategy: 'deep_curator',
          context_advanced: { recent_message_limit: 30 },
        },
        canvasLayout: { zoom: 1 },
      },
    })
    await drawer.get('[data-testid="context-strategy-reset-btn"]').trigger('click')
    const afterReset = drawer.emitted('update-overview')!
    expect(afterReset).toHaveLength(2)
    expect(afterReset[1]![0]).toEqual({
      defaultOptions: {
        ...defaultSpaceOptions(),
        plan_first: true,
        context_strategy: null,
        context_advanced: null,
      },
    })
  })
})
