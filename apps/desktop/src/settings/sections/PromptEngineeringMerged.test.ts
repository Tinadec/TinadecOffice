// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'

const mocks = vi.hoisted(() => ({
  listAgents: vi.fn(),
  listAgentModes: vi.fn(),
  listPromptPipelines: vi.fn(),
  listPromptFragments: vi.fn(),
  listPromptVersions: vi.fn(),
  getPromptFragmentEffectiveness: vi.fn(),
  previewPromptContext: vi.fn(),
  updatePromptPipelineDraft: vi.fn(),
  publishPromptPipeline: vi.fn(),
  createPromptPipelineDraft: vi.fn(),
  clonePromptPipeline: vi.fn(),
  createPromptFragment: vi.fn(),
  savePromptFragment: vi.fn(),
  deletePromptFragment: vi.fn(),
  clonePrompt: vi.fn(),
  rollbackPrompt: vi.fn(),
  notify: { error: vi.fn(), success: vi.fn(), info: vi.fn() },
  confirm: vi.fn(),
}))

vi.mock('@/api', () => ({ api: mocks }))
vi.mock('@/lib/storageScope', () => ({ scopedApi: (api: unknown) => api }))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: mocks.notify, confirm: mocks.confirm }),
}))
vi.mock('@/components/canvas/PromptPipelineCanvas.vue', () => ({
  default: {
    name: 'PromptPipelineCanvas',
    props: ['pipeline', 'selectedNodeId', 'selectedEdgeId', 'readonly', 'dirty', 'saving', 'publishing'],
    emits: ['update:nodes', 'update:edges', 'select-node', 'select-edge', 'save-draft', 'publish'],
    template: `
      <div data-testid="mock-pipeline-canvas">
        <span class="canvas-name">{{ pipeline?.display_name }}</span>
        <span class="canvas-node-count">{{ pipeline?.graph?.nodes?.length ?? 0 }}</span>
        <button type="button" class="select-first-node" @click="$emit('select-node', pipeline?.graph?.nodes?.[0] ?? null)">select</button>
        <button type="button" class="save-draft-btn" @click="$emit('save-draft')">save</button>
      </div>
    `,
  },
}))
vi.mock('@/components/ui', () => ({
  UiBadge: { template: '<span class="ui-badge"><slot /></span>' },
  UiButton: {
    props: ['disabled'],
    emits: ['click'],
    template: '<button :disabled="disabled" @click="$emit(\'click\', $event)"><slot /></button>',
  },
  UiCard: { template: '<div class="ui-card"><slot name="content" /><slot /></div>' },
  UiInput: {
    props: ['modelValue', 'disabled', 'placeholder', 'type'],
    emits: ['update:modelValue'],
    template: '<input :value="modelValue" :disabled="disabled" @input="$emit(\'update:modelValue\', $event.target.value)" />',
  },
  UiLabel: { template: '<label><slot /></label>' },
  UiSelectField: {
    props: ['modelValue', 'options', 'disabled'],
    emits: ['update:modelValue'],
    template:
      '<select :value="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.value)"><option v-for="o in options" :key="o.value" :value="o.value">{{ o.label }}</option></select>',
  },
  UiSheet: { props: ['open'], template: '<div v-if="open" class="ui-sheet"><slot /></div>' },
  UiSwitch: {
    props: ['modelValue', 'disabled'],
    emits: ['update:modelValue'],
    template: '<input type="checkbox" :checked="modelValue" :disabled="disabled" @change="$emit(\'update:modelValue\', $event.target.checked)" />',
  },
}))

const { default: PromptEngineeringMerged } = await import('./PromptEngineeringMerged.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })

describe('PromptEngineeringMerged', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listAgents.mockResolvedValue([{ id: 'ag-1', slug: 'meeting', display_name: 'Meeting', layer: 'operation' }])
    mocks.listAgentModes.mockResolvedValue([{ id: 'm-1', slug: 'plan', display_name: 'Plan', status: 'published' }])
    mocks.listPromptPipelines.mockResolvedValue([
      {
        id: 'pipe-1',
        slug: 'custom_pipe',
        display_name: 'Custom Pipeline',
        description: 'Draft pipeline',
        status: 'draft',
        pack_managed: false,
        revision: 2,
        graph: {
          nodes: [
            { id: 'tpl_1', type: 'template', label: 'Intro', config: { content: 'Hello {{agent_name}}' } },
            { id: 'asm_1', type: 'assemble', label: 'Assemble', config: { separator: '\n\n' } },
          ],
          edges: [{ id: 'e-1', source: 'tpl_1', target: 'asm_1' }],
        },
      },
    ])
    mocks.listPromptFragments.mockResolvedValue([
      {
        id: 'frag-1',
        key: 'sys.greeting',
        title: 'Greeting',
        scope: 'global',
        category: 'system',
        content: 'Be concise.',
        priority: 10,
        enabled: true,
        is_builtin: false,
        revision: 1,
      },
    ])
    mocks.listPromptVersions.mockResolvedValue([
      { version: 1, content: 'Be concise.', created_at: '2026-10-01T00:00:00Z' },
    ])
    mocks.getPromptFragmentEffectiveness.mockResolvedValue(null)
    mocks.updatePromptPipelineDraft.mockImplementation(async (id: string, body: Record<string, unknown>) => ({
      id,
      slug: 'custom_pipe',
      status: 'draft',
      pack_managed: false,
      revision: 3,
      ...body,
    }))
    mocks.previewPromptContext.mockResolvedValue({
      assembled_prompt: 'Hello Meeting\n\nBe concise.',
      total_estimated_tokens: 42,
      layers: [
        { id: 'l-1', kind: 'pipeline', source: 'pipeline', title: 'Pipeline Template', estimated_tokens: 30, content: 'Hello Meeting' },
        { id: 'l-2', kind: 'fragment', source: 'fragment', title: 'Greeting', estimated_tokens: 12, content: 'Be concise.' },
      ],
    })
  })

  it('clones reactive pipeline DTOs safely without DataCloneError when editing nodes and saving draft', async () => {
    const wrapper = mount(PromptEngineeringMerged, { global: { plugins: [i18n] } })
    await flushPromises()

    // Switch to Pipeline Canvas view
    const viewButtons = wrapper.findAll('.ac-prompt-toolbar button')
    await viewButtons[1].trigger('click')
    await flushPromises()

    expect(wrapper.get('.canvas-name').text()).toBe('Custom Pipeline')
    expect(wrapper.get('.canvas-node-count').text()).toBe('2')

    // Select first node and edit its template content (exercises plainClone on Vue reactive Proxy)
    await wrapper.get('.select-first-node').trigger('click')
    const tplTextarea = wrapper.get<HTMLTextAreaElement>('.pe-template-textarea')
    expect(tplTextarea.element.value).toBe('Hello {{agent_name}}')
    await tplTextarea.setValue('Updated template {{agent_name}}')

    // Save draft
    await wrapper.get('.save-draft-btn').trigger('click')
    await flushPromises()

    expect(mocks.updatePromptPipelineDraft).toHaveBeenCalledTimes(1)
    const [, payload] = mocks.updatePromptPipelineDraft.mock.calls[0]
    expect(payload.graph.nodes[0].config.content).toBe('Updated template {{agent_name}}')
    wrapper.unmount()
  })

  it('runs Live Context Preview and displays per-layer token breakdown', async () => {
    const wrapper = mount(PromptEngineeringMerged, { global: { plugins: [i18n] } })
    await flushPromises()

    const previewPanel = wrapper.get('[data-testid="prompt-context-preview-panel"]')
    const runPreviewBtn = previewPanel.findAll('button')[0]
    await runPreviewBtn.trigger('click')
    await flushPromises()

    expect(mocks.previewPromptContext).toHaveBeenCalledTimes(1)
    expect(previewPanel.text()).toContain('42')
    expect(previewPanel.text()).toContain('Pipeline Template')
    expect(previewPanel.text()).toContain('Greeting')
    expect(previewPanel.get<HTMLTextAreaElement>('.pe-preview-textarea').element.value).toBe('Hello Meeting\n\nBe concise.')
    wrapper.unmount()
  })
})
