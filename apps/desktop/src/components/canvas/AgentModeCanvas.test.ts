// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { VueFlow } from '@vue-flow/core'
import AgentModeCanvas from './AgentModeCanvas.vue'
import type { AgentDefinitionDto, AgentModeEdgeDto, AgentModeNodeDto } from '@/api'

vi.mock('vue-i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key, locale: ref('zh-CN') }),
}))

beforeEach(() => {
  class ResizeObserverStub {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  ;(globalThis as unknown as { ResizeObserver: unknown }).ResizeObserver = ResizeObserverStub
})

const agent = (id: string, layer: 'planning' | 'execution'): AgentDefinitionDto => ({
  id,
  name: id,
  display_name: id,
  layer,
  agent_type: layer,
  description: '',
  model_route_purpose: '',
  allowed_tools: [],
  capabilities: [],
  enabled: true,
  is_built_in: false,
  updated_at: null,
})

const leader: AgentModeNodeDto = {
  id: 'leader',
  node_key: 'leader',
  agent_id: 'leader',
  lane: 'operation',
  position: { x: 60, y: 40 },
  config: { conversation: true },
}
const worker: AgentModeNodeDto = {
  id: 'worker-a',
  node_key: 'worker-a',
  agent_id: 'worker',
  lane: 'execution',
  position: { x: 300, y: 260 },
}
const secondWorker: AgentModeNodeDto = {
  id: 'worker-b',
  node_key: 'worker-b',
  agent_id: 'worker',
  lane: 'execution',
  position: { x: 300, y: 380 },
}
const edges: AgentModeEdgeDto[] = [{ id: 'leader->worker-a', source: 'leader', target: 'worker-a' }]

function mountCanvas(nodes: AgentModeNodeDto[] = [leader, worker, secondWorker], canvasEdges: AgentModeEdgeDto[] = edges) {
  return mount(AgentModeCanvas, {
    attachTo: document.body,
    props: {
      nodes,
      edges: canvasEdges,
      agents: [agent('leader', 'planning'), agent('worker', 'execution')],
    },
  })
}

describe('AgentModeCanvas', () => {
  it('renders a real connection handle on each side of a custom node', async () => {
    const wrapper = mountCanvas()
    await flushPromises()
    // Without handles Vue Flow has no anchor, so no edge can ever be drawn.
    expect(wrapper.findAll('.vue-flow__handle').length).toBeGreaterThanOrEqual(2)
    expect(wrapper.find('[data-flow-id="agent-mode-editor"]').exists()).toBe(true)
    // The editor's `useVueFlow(id)` must target the same store the component
    // renders; an id-less pair silently failed to deliver connect events.
    expect(wrapper.findComponent(VueFlow).props('id')).toBe('agent-mode-editor')
    wrapper.unmount()
  })

  it('emits a connect intent for a legal operation to execution link', async () => {
    const wrapper = mountCanvas([leader, worker, secondWorker], [edges[0]])
    await flushPromises()
    ;(wrapper.vm as unknown as { connect: (connection: { source: string; target: string }) => void })
      .connect({ source: 'leader', target: 'worker-b' })
    expect(wrapper.emitted('connect')?.[0]).toEqual([{ source: 'leader', target: 'worker-b' }])
    wrapper.unmount()
  })

  it('refuses a backflow link and reports why', async () => {
    const wrapper = mountCanvas([leader, worker], [])
    await flushPromises()
    ;(wrapper.vm as unknown as { connect: (connection: { source: string; target: string }) => void })
      .connect({ source: 'worker-a', target: 'leader' })
    await wrapper.vm.$nextTick()
    expect(wrapper.emitted('connect')).toBeUndefined()
    expect(wrapper.text()).toContain('canvas.issue.backflow')
    wrapper.unmount()
  })

  it('keeps the Vue event and drag-stop wiring in the template', () => {
    const source = readFileSync(resolve(process.cwd(), 'src', 'components', 'canvas', 'AgentModeCanvas.vue'), 'utf8')
    expect(source).toContain('@connect="onConnect"')
    expect(source).toContain('@node-drag-stop="onNodeDragStop"')
    // The old editor wired every drag tick back into props and jittered.
    expect(source).not.toContain('@nodes-change=')
  })

  it('refuses a self link', async () => {
    const wrapper = mountCanvas([leader, worker], [])
    await flushPromises()
    ;(wrapper.vm as unknown as { connect: (connection: { source: string; target: string }) => void })
      .connect({ source: 'leader', target: 'leader' })
    await wrapper.vm.$nextTick()
    expect(wrapper.emitted('connect')).toBeUndefined()
    expect(wrapper.text()).toContain('canvas.issue.self-loop')
    wrapper.unmount()
  })
})
