// @vitest-environment happy-dom
/**
 * The workbench's job is to be honest about what the backend can do. These assertions cover
 * the three things that were previously wrong or missing: the node inspector only knew four
 * node kinds, a 501 preview rendered as an empty (apparently successful) panel, and a
 * read-only pack pipeline still offered a save button.
 */
import { createPinia, setActivePinia } from 'pinia'
import { createI18n } from 'vue-i18n'
import { ref, vaporInteropPlugin } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import en from '@/locales/en'

const mocks = vi.hoisted(() => ({
  listPromptPipelines: vi.fn(),
  listPromptFragments: vi.fn(),
  listPromptVersions: vi.fn(),
  getPromptFragmentEffectiveness: vi.fn(),
  listPromptPipelineVersions: vi.fn(),
  updatePromptPipelineDraft: vi.fn(),
  previewPromptContext: vi.fn(),
  listAgents: vi.fn(),
  listAgentModes: vi.fn(),
  createPromptFragment: vi.fn(),
  savePromptFragment: vi.fn(),
  clonePrompt: vi.fn(),
  deletePromptFragment: vi.fn(),
  rollbackPrompt: vi.fn(),
  publishPromptPipeline: vi.fn(),
  createPromptPipelineDraft: vi.fn(),
  getPromptPipeline: vi.fn(),
  notify: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
  confirm: vi.fn(async () => true),
}))

vi.mock('@/api', () => ({ api: mocks }))
vi.mock('@/lib/storageScope', () => ({ scopedApi: (api: unknown) => api }))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: mocks.notify, confirm: mocks.confirm }),
}))
vi.mock('@/lib/hostAccess', async () => {
  // A real ref is required: the component passes `canAccessBackend` to `watch()`.
  const { ref } = await import('vue')
  return { useHostAccess: () => ({ canAccessBackend: ref(true) }) }
})
/**
 * The UI barrel is Vapor-compiled; rendering it inside VTU's vdom runtime needs
 * `vaporInteropPlugin`, which VTU does not install. Only display + control primitives are
 * stubbed, and each keeps the behaviour under test: `UiButton` forwards `disabled`, and
 * `UiSelectField` renders a real trigger plus a list of options so the inspector's kind
 * switching can be driven from the DOM.
 */
vi.mock('@/components/ui', async () => {
  const { defineComponent, h } = await import('vue')
  // UiCard / UiSheet take a NAMED `content` slot, so a stub that renders only the default
  // slot silently drops the whole card body — which is where every control under test lives.
  // `inheritAttrs: false` is needed so the attrs do not land twice, but it also means the
  // stub must forward them itself: `data-testid` on <UiCard> is a fallthrough attribute, and
  // without an explicit `h(tag, attrs, …)` it is silently dropped.
  const withContent = (name: string, tag = 'div') =>
    defineComponent({
      name,
      inheritAttrs: false,
      setup: (_, { slots, attrs }) => () =>
        h(tag, attrs, slots.content ? slots.content() : slots.default?.()),
    })
  return {
    UiCard: withContent('UiCard'),
    UiSheet: withContent('UiSheet'),
    UiBadge: withContent('UiBadge', 'span'),
    UiLabel: withContent('UiLabel', 'label'),
    UiInput: defineComponent({
      name: 'UiInput',
      props: ['modelValue', 'disabled', 'type', 'placeholder', 'ariaLabel'],
      emits: ['update:modelValue'],
      setup: (props, { emit }) => () =>
        h('input', {
          value: props.modelValue,
          disabled: props.disabled,
          type: props.type,
          onInput: (event: Event) => emit('update:modelValue', (event.target as HTMLInputElement).value),
        }),
    }),
    UiSwitch: defineComponent({
      name: 'UiSwitch',
      props: ['modelValue', 'disabled'],
      emits: ['update:modelValue'],
      setup: (props, { emit }) => () =>
        h('button', {
          class: 'ui-switch',
          disabled: props.disabled,
          onClick: () => emit('update:modelValue', !props.modelValue),
        }),
    }),
    UiButton: defineComponent({
      name: 'UiButton',
      props: ['disabled', 'variant', 'size'],
      emits: ['click'],
      setup: (props, { emit, slots }) => () =>
        h('button', { disabled: props.disabled, onClick: () => emit('click') }, slots.default?.()),
    }),
    UiSelectField: defineComponent({
      name: 'UiSelectField',
      props: ['modelValue', 'value', 'options', 'disabled', 'ariaLabel', 'placeholder'],
      emits: ['update:modelValue', 'change'],
      setup: (props, { emit }) => {
        const current = () => String(props.modelValue ?? props.value ?? '')
        return () =>
          h('div', { class: 'ui-select' }, [
            h(
              'button',
              {
                class: 'ui-select-trigger',
                type: 'button',
                disabled: props.disabled,
                'aria-label': props.ariaLabel,
              },
              (props.options as Array<{ value: string; label: string }>).find((o) => o.value === current())
                ?.label ?? props.placeholder ?? current(),
            ),
            ...(props.options as Array<{ value: string; label: string }>).map((option) =>
              h(
                'button',
                {
                  class: 'ui-select-option',
                  type: 'button',
                  disabled: props.disabled,
                  'data-value': option.value,
                  onClick: () => {
                    emit('update:modelValue', option.value)
                    emit('change', option.value)
                  },
                },
                option.label,
              ),
            ),
          ])
      },
    }),
  }
})
// Vue Flow has no DOM-safe rendering under happy-dom. The panel's contract with the canvas is
// props in / events out, which is exactly what these tests drive.
vi.mock('@/components/canvas/PromptPipelineCanvas.vue', () => ({
  default: {
    name: 'PromptPipelineCanvas',
    props: ['pipeline', 'selectedNodeId', 'selectedEdgeId', 'readonly', 'dirty', 'saving', 'publishing'],
    emits: ['update:nodes', 'update:edges', 'select-node', 'select-edge', 'save-draft', 'clone', 'publish'],
    template: '<div data-testid="pipeline-canvas" />',
  },
}))

const { default: PromptEngineeringMerged } = await import('./PromptEngineeringMerged.vue')

/**
 * The panel is a Vapor SFC. `vaporInteropPlugin` is what lets the classic runtime mount it
 * AND flush its updates — without it the first paint renders but a later `v-if` change (the
 * inspector appearing after `select-node`) never reaches the DOM. It only works once every
 * Vapor primitive is replaced with a classic stub, which the `@/components/ui` mock above
 * does; with real Vapor primitives it throws `simpleSetCurrentInstance is not a function`.
 */
const global = { plugins: [vaporInteropPlugin, createI18n({ legacy: false, locale: 'en', messages: { en } })] }

const fragment = (over: Record<string, unknown> = {}) => ({
  id: 'frag-1',
  key: 'system_core',
  title: 'System core',
  scope: 'global',
  target_agent_id: null,
  category: 'system',
  content: 'You are a careful agent.',
  priority: 50,
  enabled: true,
  is_builtin: false,
  revision: 3,
  created_at: '2026-01-01T00:00:00Z',
  updated_at: '2026-01-01T00:00:00Z',
  ...over,
})

const pipeline = (over: Record<string, unknown> = {}) => ({
  id: 'pipe-1',
  display_name: 'Custom pipeline',
  description: 'demo',
  status: 'draft',
  revision: 2,
  pack_managed: false,
  graph: {
    nodes: [
      { id: 'template_1', type: 'template', label: 'template_1', position: { x: 0, y: 0 }, config: { content: 'hello' } },
      { id: 'assemble_1', type: 'assemble', label: 'assemble_1', position: { x: 240, y: 0 }, config: {} },
    ],
    edges: [{ id: 'e-template_1-assemble_1', source: 'template_1', target: 'assemble_1' }],
  },
  ...over,
})

async function mountWorkbench(): Promise<VueWrapper> {
  const wrapper = mount(PromptEngineeringMerged, { global })
  await flushPromises()
  return wrapper
}

/**
 * The view toggle. `.ac-prompt-toolbar` is the panel's own tab bar; the preview aside has
 * its own `.model-provider-filters` for status pills, so targeting the shared class alone
 * picks the wrong buttons.
 */
async function openCanvas(wrapper: VueWrapper) {
  const toggle = wrapper.get('.ac-prompt-toolbar')
  const buttons = toggle.findAll('button')
  expect(buttons.length, 'view toggle must have two buttons').toBe(2)
  await buttons[1].trigger('click')
  await flushPromises()
}

async function openPreview(wrapper: VueWrapper) {
  const button = wrapper.findAll('button').find((b) => b.text().includes(en.settings.generatePreview))
  expect(button, 'generate-preview button must exist').toBeTruthy()
  await button!.trigger('click')
  await flushPromises()
}

/**
 * Select a node the way the canvas does: it emits the node object it was given, and the panel
 * matches it by `id` against its own draft. Passing the canvas's own prop back is what a real
 * `select-node` emit looks like, so that is what this drives.
 */
/**
 * Select a node the way the canvas does. The panel opens on the first node already, so this
 * only needs to move the selection; `handleSelectNode` keys off `node.id`, which is why a
 * plain `{ id }` is a faithful stand-in for the canvas's own emit.
 */
async function selectNode(wrapper: VueWrapper, nodeId: string) {
  const canvas = wrapper.getComponent({ name: 'PromptPipelineCanvas' })
  const nodes = (canvas.props('pipeline') as { graph: { nodes: Array<{ id: string }> } }).graph.nodes
  expect(nodes.some((n) => n.id === nodeId), `node ${nodeId} must exist on the canvas`).toBe(true)
  await canvas.vm.$emit('select-node', { id: nodeId })
  await flushPromises()
}

/**
 * Pick an option out of the (stubbed) `UiSelectField`. The stub renders its options inline,
 * so no teleport or menu-opening step is involved. Scoped to the wrapper rather than
 * `document`: VTU does not attach to the document by default, so `querySelector` on
 * `document` sees nothing.
 */
async function chooseOption(wrapper: VueWrapper, scope: string, label: string) {
  const options = wrapper.findAll(`${scope} .ui-select-option`)
  const option = options.find((node) => node.text().trim() === label)
  expect(option, `option "${label}" must be offered in ${scope}`).toBeTruthy()
  await option!.trigger('click')
  await flushPromises()
  // The panel is Vapor-compiled; its render effect is not on the classic microtask queue, so
  // a branch swap (`v-if` on the node kind) needs one macrotask turn to reach the DOM.
  await new Promise((resolve) => setTimeout(resolve, 0))
  await flushPromises()
}

beforeEach(() => {
  setActivePinia(createPinia())
  vi.clearAllMocks()
  document.body.innerHTML = ''
  mocks.listPromptPipelines.mockResolvedValue([pipeline()])
  mocks.listPromptFragments.mockResolvedValue([fragment()])
  mocks.listPromptVersions.mockResolvedValue([])
  mocks.listPromptPipelineVersions.mockResolvedValue([])
  mocks.getPromptFragmentEffectiveness.mockResolvedValue(null)
  mocks.listAgents.mockResolvedValue([])
  mocks.listAgentModes.mockResolvedValue([])
  mocks.confirm.mockResolvedValue(true)
})

describe('PromptEngineeringMerged — pipeline persistence', () => {
  it('saves the real graph shape through updatePromptPipelineDraft, never a flattened body', async () => {
    mocks.updatePromptPipelineDraft.mockImplementation(async (id: string, payload: Record<string, unknown>) => ({
      ...pipeline(),
      id,
      ...payload,
    }))
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)

    const canvas = wrapper.getComponent({ name: 'PromptPipelineCanvas' })
    await canvas.vm.$emit('update:nodes', [
      { id: 'template_1', type: 'template', label: 'template_1', config: { content: 'edited' } },
      { id: 'assemble_1', type: 'assemble', label: 'assemble_1', config: {} },
    ])
    await flushPromises()
    await canvas.vm.$emit('save-draft')
    await flushPromises()

    expect(mocks.updatePromptPipelineDraft).toHaveBeenCalledTimes(1)
    const [id, payload, revision] = mocks.updatePromptPipelineDraft.mock.calls[0]
    expect(id).toBe('pipe-1')
    // The revision travels as the If-Match precondition, so a concurrent edit is refused.
    expect(revision).toBe(2)
    const body = payload as Record<string, unknown>
    // The contract is graph.nodes / graph.edges. A flattened body saves an empty pipeline.
    expect(body.nodes).toBeUndefined()
    expect(body.edges).toBeUndefined()
    const graph = body.graph as { nodes: Array<{ config: { content: string } }>; edges: unknown[] }
    expect(graph.edges).toHaveLength(1)
    expect(graph.nodes[0].config.content).toBe('edited')
    expect(mocks.notify.success).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('surfaces a failed save instead of claiming success', async () => {
    mocks.updatePromptPipelineDraft.mockRejectedValue(new Error('revision conflict'))
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)

    const canvas = wrapper.getComponent({ name: 'PromptPipelineCanvas' })
    await canvas.vm.$emit('update:nodes', [{ id: 'template_1', type: 'template', label: 'x', config: {} }])
    await flushPromises()
    await canvas.vm.$emit('save-draft')
    await flushPromises()

    expect(mocks.notify.error).toHaveBeenCalled()
    expect(mocks.notify.success).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('marks a pack pipeline read-only and does not offer a save', async () => {
    mocks.listPromptPipelines.mockResolvedValue([pipeline({ pack_managed: true })])
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)

    expect(wrapper.find('[data-testid="pipeline-readonly-badge"]').exists()).toBe(true)
    expect(wrapper.getComponent({ name: 'PromptPipelineCanvas' }).props('readonly')).toBe(true)
    wrapper.unmount()
  })

  it('reads a pipeline whose nodes arrive at the top level instead of under graph', async () => {
    mocks.listPromptPipelines.mockResolvedValue([
      pipeline({
        id: 'pipe-flat',
        graph: undefined,
        nodes: [{ id: 'template_9', type: 'template', label: 'template_9', config: { content: 'flat' } }],
        edges: [],
      }),
    ])
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)

    const canvas = wrapper.getComponent({ name: 'PromptPipelineCanvas' })
    const nodes = (canvas.props('pipeline') as { graph: { nodes: Array<{ id: string }> } }).graph.nodes
    expect(nodes.map((n) => n.id)).toEqual(['template_9'])
    wrapper.unmount()
  })

  it('reports a pipeline-versions failure instead of an empty history', async () => {
    mocks.listPromptPipelineVersions.mockRejectedValue(new Error('capability_unavailable: versions'))
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)

    expect(wrapper.get('[data-testid="pipeline-versions-error"]').text()).toContain('capability_unavailable')
    wrapper.unmount()
  })
})

describe('PromptEngineeringMerged — node inspector', () => {
  it('offers every node kind Core actually executes', async () => {
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)
    await selectNode(wrapper, 'template_1')

    await chooseOption(wrapper, '[data-testid="pipeline-node-inspector"]', 'condition')
    // Switching kinds replaces the inspector body: template content is gone, condition is shown.
    expect(wrapper.find('[data-testid="inspector-template"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="inspector-condition"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="inspector-node-type"]').text()).toBe('condition')
    wrapper.unmount()
  })

  it('lists exactly the six executable kinds, not the four the toolbar used to offer', async () => {
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)
    await selectNode(wrapper, 'template_1')

    const labels = wrapper
      .findAll('[data-testid="pipeline-node-inspector"] .ui-select-option')
      .map((n) => n.text().trim())
    expect(labels).toEqual(['template', 'fragment_ref', 'condition', 'workspace_rules', 'skills_index', 'assemble'])
    wrapper.unmount()
  })

  it('shows the documented template variables so an author does not invent one', async () => {
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)
    await selectNode(wrapper, 'template_1')

    const inspector = wrapper.get('[data-testid="pipeline-node-inspector"]')
    const area = inspector.get('[data-testid="inspector-template"] textarea')
    expect((area.element as HTMLTextAreaElement).value).toBe('hello')
    for (const variable of ['{{agent_name}}', '{{agent_slug}}', '{{mode_name}}', '{{mode_slug}}', '{{workspace_root}}']) {
      expect(inspector.text()).toContain(variable)
    }
    wrapper.unmount()
  })

  it('renders a fragment picker for fragment_ref nodes', async () => {
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)
    await selectNode(wrapper, 'template_1')
    await chooseOption(wrapper, '[data-testid="pipeline-node-inspector"]', 'fragment_ref')

    expect(wrapper.find('[data-testid="inspector-fragment-ref"]').exists()).toBe(true)
    // The picker is backed by real fragment rows, not free text.
    expect(mocks.listPromptFragments).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('renders condition field and value for condition nodes', async () => {
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)
    await selectNode(wrapper, 'template_1')
    await chooseOption(wrapper, '[data-testid="pipeline-node-inspector"]', 'condition')

    const condition = wrapper.get('[data-testid="inspector-condition"]')
    // The field is always a closed set of the predicates Core evaluates.
    expect(condition.findAll('.ui-select-trigger')).toHaveLength(1)
    // `layer` compares against a string, so the value is free text.
    expect(condition.find('input').exists()).toBe(true)

    // `has_workspace` compares booleans in Core, so a typed "true" would be a trap: the value
    // control must become a select offering exactly true/false.
    await chooseOption(wrapper, '[data-testid="inspector-condition"]', 'has_workspace')
    const switched = wrapper.get('[data-testid="inspector-condition"]')
    expect(switched.findAll('.ui-select-trigger')).toHaveLength(2)
    const values = switched.findAll('.ui-select-option').map((o) => o.text().trim())
    expect(values).toContain('true')
    expect(values).toContain('false')
    wrapper.unmount()
  })

  it('renders workspace_rules and skills_index bodies', async () => {
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)
    await selectNode(wrapper, 'template_1')

    await chooseOption(wrapper, '[data-testid="pipeline-node-inspector"]', 'workspace_rules')
    expect(wrapper.find('[data-testid="inspector-workspace-rules"]').exists()).toBe(true)

    await chooseOption(wrapper, '[data-testid="pipeline-node-inspector"]', 'skills_index')
    expect(wrapper.find('[data-testid="inspector-skills-index"]').exists()).toBe(true)
    wrapper.unmount()
  })
})

describe('PromptEngineeringMerged — fragment library', () => {
  it('marks a built-in fragment read-only and explains clone-to-customize', async () => {
    mocks.listPromptFragments.mockResolvedValue([fragment({ is_builtin: true })])
    const wrapper = await mountWorkbench()

    expect(wrapper.get('[data-testid="fragment-readonly-banner"]').text()).toContain('Clone')
    // A built-in has no editable revision history, so no version read is issued at all.
    expect(mocks.listPromptVersions).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows scope, target agent, category and priority on the list row', async () => {
    mocks.listPromptFragments.mockResolvedValue([
      fragment({ scope: 'agent', target_agent_id: 'agent-9', category: 'task', priority: 80, enabled: false }),
    ])
    mocks.listAgents.mockResolvedValue([{ id: 'agent-9', slug: 'writer', display_name: 'Writer', i18n: null }])
    const wrapper = await mountWorkbench()

    const card = wrapper.get('.pe-fragment-card')
    expect(card.text()).toContain(en.settings.promptStudio.scopeAgent)
    expect(card.text()).toContain('task')
    expect(card.text()).toContain('Writer')
    expect(card.text()).toContain('prio 80')
    wrapper.unmount()
  })

  it('loads the version list for an editable fragment and offers rollback on inactive ones', async () => {
    mocks.listPromptVersions.mockResolvedValue([
      { id: 'v2', fragment_id: 'frag-1', version: 2, content: 'new', changed_fields: [], change_summary: 'second', is_active: true, created_at: '2026-01-02T00:00:00Z' },
      { id: 'v1', fragment_id: 'frag-1', version: 1, content: 'old', changed_fields: [], change_summary: 'first', is_active: false, created_at: '2026-01-01T00:00:00Z' },
    ])
    const wrapper = await mountWorkbench()

    const rows = wrapper.findAll('.pe-version-row')
    expect(rows).toHaveLength(2)
    // Only the inactive version is rollback-able; rolling back to the active one is a no-op.
    expect(rows[0].text()).toContain(en.settings.activeVersion)
    expect(rows[1].find('button').exists()).toBe(true)
    wrapper.unmount()
  })
})

describe('PromptEngineeringMerged — live preview', () => {
  it('renders the per-layer token breakdown and the assembled prompt', async () => {
    mocks.previewPromptContext.mockResolvedValue({
      assembled_prompt: 'ASSEMBLED PROMPT BODY',
      total_estimated_tokens: 300,
      layers: [
        { id: 'l1', kind: 'system', title: 'System prompt', content: 'sys', estimated_tokens: 100, source: 'mode' },
        { id: 'l2', kind: 'fragment', title: 'Fragment', content: 'frag', estimated_tokens: 200, source: 'fragment:core' },
      ],
    })
    const wrapper = await mountWorkbench()
    await openPreview(wrapper)

    const panel = wrapper.get('[data-testid="prompt-context-preview-panel"]')
    const rows = panel.findAll('[data-testid="preview-layer-row"]')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('100t')
    expect(rows[0].text()).toContain('33%')
    expect(rows[1].text()).toContain('67%')
    expect(panel.text()).toContain('300')
    expect((panel.get('.pe-preview-textarea').element as HTMLTextAreaElement).value).toBe('ASSEMBLED PROMPT BODY')
    expect(wrapper.find('[data-testid="preview-backend-error"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('renders the backend error when the preview endpoint still 501s', async () => {
    mocks.previewPromptContext.mockRejectedValue(new Error('capability_unavailable: prompt assembler not available'))
    const wrapper = await mountWorkbench()
    await openPreview(wrapper)

    expect(wrapper.get('[data-testid="preview-backend-error"]').text()).toContain('capability_unavailable')
    // Honesty: an empty panel must not look like a successful preview of an empty prompt.
    expect(wrapper.findAll('[data-testid="preview-layer-row"]')).toHaveLength(0)
    expect(wrapper.find('.pe-preview-textarea').exists()).toBe(false)
    wrapper.unmount()
  })

  it('sends the unsaved draft graph when include-draft is on', async () => {
    mocks.previewPromptContext.mockResolvedValue({ assembled_prompt: 'x', total_estimated_tokens: 1, layers: [] })
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)
    await openPreview(wrapper)

    const input = mocks.previewPromptContext.mock.calls[0][0] as { draft_pipeline_graph?: { nodes: unknown[] } }
    expect(input.draft_pipeline_graph).toBeTruthy()
    expect(input.draft_pipeline_graph!.nodes).toHaveLength(2)
    wrapper.unmount()
  })

  it('omits the draft graph when include-draft is off', async () => {
    mocks.previewPromptContext.mockResolvedValue({ assembled_prompt: 'x', total_estimated_tokens: 1, layers: [] })
    const wrapper = await mountWorkbench()
    await openCanvas(wrapper)
    const toggle = wrapper.get('.pe-preview-actions input[type="checkbox"]')
    await toggle.setValue(false)
    await openPreview(wrapper)

    const input = mocks.previewPromptContext.mock.calls[0][0] as { draft_pipeline_graph?: unknown }
    expect(input.draft_pipeline_graph).toBeUndefined()
    wrapper.unmount()
  })
})
