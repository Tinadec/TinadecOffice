// @vitest-environment happy-dom
/**
 * The canvas is the only place a pipeline graph is authored, so its contract with the panel
 * is what these tests pin: the six node kinds Core actually executes are the six the toolbar
 * offers, a new node starts with config the assembler can use, and edge staging refuses the
 * same pairs a live drag would.
 */
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import en from '@/locales/en'
import type { PromptPipelineDto } from '@/api'
import PromptPipelineCanvas from './PromptPipelineCanvas.vue'

// Vue Flow measures the DOM and spawns observers; happy-dom has no layout engine. The
// component's own logic (toolbar, staging, validation) is what matters here, so the flow
// surface is stubbed while its slots still render.
vi.mock('@vue-flow/core', async () => {
  const { defineComponent, h } = await import('vue')
  const passthrough = (name: string) =>
    defineComponent({
      name,
      inheritAttrs: false,
      setup: (_, { slots }) => () => h('div', { 'data-stub': name }, slots.default?.()),
    })
  return {
    // Renders one wrapper per node through the `node-custom` slot, exactly as Vue Flow would,
    // so node markup (and the source handle that stages an edge) is reachable.
    VueFlow: defineComponent({
      name: 'VueFlow',
      props: { nodes: { type: Array, default: () => [] }, edges: { type: Array, default: () => [] } },
      emits: ['connect', 'node-click', 'edge-click', 'pane-click', 'node-drag-stop', 'nodes-initialized'],
      setup: (props, { slots }) => () =>
        h('div', { 'data-stub': 'VueFlow' }, [
          slots.default?.(),
          ...(props.nodes as Array<Record<string, unknown>>).map((node) =>
            h('div', { 'data-node-wrapper': String(node.id) }, slots['node-custom']?.({ data: node.data } as never)),
          ),
        ]),
    }),
    Background: passthrough('Background'),
    Panel: passthrough('Panel'),
    Handle: defineComponent({
      name: 'Handle',
      props: ['type', 'position'],
      emits: ['click'],
      setup: (props, { emit }) => () =>
        h('button', {
          class: 'vf-handle',
          'data-handle': props.type,
          onClick: (event: Event) => emit('click', event),
        }),
    }),
    Position: { Left: 'left', Right: 'right', Top: 'top', Bottom: 'bottom' },
    MarkerType: { ArrowClosed: 'arrowclosed' },
    useVueFlow: () => ({ fitView: vi.fn(async () => undefined) }),
  }
})
vi.mock('@vue-flow/background', () => ({
  Background: { name: 'Background', setup: () => () => null },
}))
// dagre is only used to place unpositioned nodes; a linear fallback keeps this test focused.
vi.mock('@dagrejs/dagre', () => {
  class Graph {
    private nodes = new Map<string, { x: number; y: number }>()
    setGraph() {}
    setDefaultEdgeLabel() {}
    setNode(id: string) { this.nodes.set(id, { x: 0, y: 0 }) }
    setEdge() {}
    node(id: string) { return this.nodes.get(id) }
  }
  return { Graph, layout: () => undefined }
})

const global = { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] }

function makePipeline(
  nodes: Array<Record<string, unknown>> = [],
  edges: Array<Record<string, unknown>> = [],
  over: Record<string, unknown> = {},
): PromptPipelineDto {
  return {
    id: 'pipe-1',
    display_name: 'Pipeline',
    status: 'draft',
    pack_managed: false,
    graph: { nodes, edges },
    ...over,
  } as unknown as PromptPipelineDto
}

/** Props accepted by the canvas; the helper is typed so a bad prop bag fails typecheck. */
type CanvasProps = {
  pipeline: PromptPipelineDto | null
  selectedNodeId?: string | null
  selectedEdgeId?: string | null
  readonly?: boolean
  dirty?: boolean
  saving?: boolean
  publishing?: boolean
}

function mountCanvas(props: CanvasProps) {
  return mount(PromptPipelineCanvas, { props, global })
}

beforeEach(() => vi.clearAllMocks())

describe('PromptPipelineCanvas — node creation', () => {
  it('offers exactly the node kinds Core executes', () => {
    const wrapper = mountCanvas({ pipeline: makePipeline() })
    const types = wrapper.findAll('[data-node-type]').map((b) => b.attributes('data-node-type'))
    expect(types).toEqual(['template', 'fragment_ref', 'condition', 'workspace_rules', 'skills_index', 'assemble'])
    wrapper.unmount()
  })

  it('creates a node with the config its own inspector expects', async () => {
    const wrapper = mountCanvas({ pipeline: makePipeline() })
    await wrapper.get('[data-node-type="condition"]').trigger('click')
    await flushPromises()

    const emitted = wrapper.emitted('update:nodes')
    expect(emitted).toBeTruthy()
    const nodes = emitted![0][0] as Array<{ id: string; type: string; config: Record<string, unknown> }>
    expect(nodes).toHaveLength(1)
    expect(nodes[0].type).toBe('condition')
    // A condition without a field/value is skipped by the assembler's validator.
    expect(nodes[0].config.field).toBe('layer')
    expect(nodes[0].config.value).toBe('execution')
    // The created node is selected, so the inspector opens on it.
    expect(wrapper.emitted('select-node')).toBeTruthy()
    wrapper.unmount()
  })

  it('refuses to create nodes when the pipeline is read-only', async () => {
    const wrapper = mountCanvas({ pipeline: makePipeline([], [], { pack_managed: true }), readonly: true })
    expect(wrapper.get('[data-node-type="template"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-node-type="template"]').trigger('click')
    expect(wrapper.emitted('update:nodes')).toBeFalsy()
    wrapper.unmount()
  })

  it('gives a pack pipeline a clone affordance instead of save', () => {
    const wrapper = mountCanvas({ pipeline: makePipeline([], [], { pack_managed: true }), readonly: true })
    expect(wrapper.find('[data-testid="pipeline-clone-to-customize"]').exists()).toBe(true)
    // No fake save button on a read-only pipeline.
    expect(wrapper.text()).not.toContain(en.settings.promptStudio.saveDraftAction)
    wrapper.unmount()
  })
})

describe('PromptPipelineCanvas — edge staging', () => {
  const twoNodes = [
    { id: 'a', type: 'template', label: 'a', position: { x: 0, y: 0 }, config: { content: '' } },
    { id: 'b', type: 'assemble', label: 'b', position: { x: 200, y: 0 }, config: {} },
  ]

  /** Node bodies are Vue Flow `node-click` events, not DOM clicks on our own markup. */
  async function clickNode(wrapper: VueWrapper, id: string) {
    const flow = wrapper.getComponent({ name: 'VueFlow' })
    await flow.vm.$emit('node-click', { node: { id } })
    await flushPromises()
  }

  it('stages a source then completes the edge on the next node', async () => {
    const wrapper = mountCanvas({ pipeline: makePipeline(twoNodes) })
    const sourceHandles = wrapper.findAll('[data-handle="source"]')
    expect(sourceHandles.length).toBe(2)
    await sourceHandles[0].trigger('click')
    expect(wrapper.find('[data-testid="pipeline-edge-staging"]').exists()).toBe(true)

    await clickNode(wrapper, 'b')

    const edges = wrapper.emitted('update:edges')
    expect(edges).toBeTruthy()
    expect(edges![0][0]).toEqual([{ id: 'e-a-b', source: 'a', target: 'b' }])
    // Staging is cleared once committed, so a second click cannot create a duplicate.
    expect(wrapper.find('[data-testid="pipeline-edge-staging"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('refuses an edge that would create a cycle', async () => {
    const withEdge = makePipeline(twoNodes, [{ id: 'e-a-b', source: 'a', target: 'b' }])
    const wrapper = mountCanvas({ pipeline: withEdge })
    const handles = wrapper.findAll('[data-handle="source"]')
    await handles[1].trigger('click') // stage b
    await clickNode(wrapper, 'a') // b -> a would close the loop

    expect(wrapper.emitted('update:edges')).toBeFalsy()
    expect(wrapper.get('.pp-warn').text()).toContain(en.canvas.cycleBlocked)
    wrapper.unmount()
  })

  it('refuses a duplicate edge', async () => {
    const withEdge = makePipeline(twoNodes, [{ id: 'e-a-b', source: 'a', target: 'b' }])
    const wrapper = mountCanvas({ pipeline: withEdge })
    await wrapper.findAll('[data-handle="source"]')[0].trigger('click')
    await clickNode(wrapper, 'b')

    expect(wrapper.emitted('update:edges')).toBeFalsy()
    wrapper.unmount()
  })

  it('never stages an edge on a read-only canvas', async () => {
    const wrapper = mountCanvas({ pipeline: makePipeline(twoNodes, [], { pack_managed: true }), readonly: true })
    await wrapper.findAll('[data-handle="source"]')[0].trigger('click')
    expect(wrapper.find('[data-testid="pipeline-edge-staging"]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('PromptPipelineCanvas — node type helper', () => {
  it('labels each node with the kind the assembler dispatches on', () => {
    const wrapper: VueWrapper = mountCanvas({
      pipeline: makePipeline([
        { id: 'w', type: 'workspace_rules', label: 'w', config: {} },
        { id: 's', type: 'skills_index', label: 's', config: {} },
      ]),
    })
    expect(wrapper.get('[data-node-id="w"] .pp-node-type').text()).toBe('workspace_rules')
    expect(wrapper.get('[data-node-id="s"] .pp-node-type').text()).toBe('skills_index')
    wrapper.unmount()
  })
})
