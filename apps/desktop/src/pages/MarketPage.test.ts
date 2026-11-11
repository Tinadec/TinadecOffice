// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import MarketPage from './MarketPage.vue'

const state = vi.hoisted(() => ({
  ready: Promise.resolve(), page: 'home', seen: [] as string[],
  start: vi.fn(), stop: vi.fn(), showPage: vi.fn((page: string) => { state.page = page }),
  setKind: vi.fn(),
  route: { meta: {} as Record<string, unknown> },
}))
vi.mock('vue-router', () => ({ useRoute: () => state.route }))
vi.mock('@/controllers/MarketController', () => ({ marketController: { start: state.start, stop: state.stop, setKind: state.setKind } }))
vi.mock('@/lib/uiEngine', () => ({ useUiePage: () => ({ ready: state.ready, showPage: state.showPage }) }))
vi.mock('@/components/AppHeader.vue', () => ({ default: defineComponent({ render: () => h('header') }) }))
vi.mock('@tinadec/ui', () => ({
  UieCanvas: defineComponent({
    setup() { state.seen.push(state.page); return () => h('div', { class: 'canvas-probe' }) },
  }),
}))

describe('Market route layout lifecycle', () => {
  let wrapper: ReturnType<typeof mount> | undefined
  afterEach(() => {
    wrapper?.unmount(); wrapper = undefined
    state.ready = Promise.resolve(); state.page = 'home'; state.seen = []
    state.route = { meta: {} }
    vi.clearAllMocks()
  })

  it('mounts its first canvas on Market, never on the previously shared Home layout', async () => {
    wrapper = mount(MarketPage)
    await flushPromises()
    expect(state.seen).toEqual(['market'])
    expect(state.showPage).toHaveBeenCalledWith('market')
    expect(state.start).toHaveBeenCalledOnce()
    expect(wrapper.emitted('ready')).toHaveLength(1)
    wrapper.unmount()
    expect(state.stop).toHaveBeenCalledOnce()
  })

  it('opens the Skill surface with its own UIE layout and a locked kind', async () => {
    state.route = { meta: { uiePage: 'marketSkills', marketKind: 'skill' } }
    wrapper = mount(MarketPage)
    await flushPromises()
    expect(state.seen).toEqual(['marketSkills'])
    expect(state.showPage).toHaveBeenCalledWith('marketSkills')
    expect(state.setKind).toHaveBeenCalledWith('skill')
    expect(state.start).toHaveBeenCalledOnce()
  })

  it('waits for a slow persisted layout before creating cards or starting the controller', async () => {
    let complete!: () => void
    state.ready = new Promise<void>((resolve) => { complete = resolve })
    wrapper = mount(MarketPage)
    await flushPromises()
    expect(wrapper.find('.canvas-probe').exists()).toBe(false)
    expect(state.seen).toEqual([])
    expect(state.start).not.toHaveBeenCalled()
    expect(wrapper.emitted('ready')).toBeUndefined()
    complete(); await flushPromises()
    expect(state.seen).toEqual(['market'])
    expect(wrapper.emitted('ready')).toHaveLength(1)
  })

  it('does not mount cards or restart polling after leaving while hydration is pending', async () => {
    let complete!: () => void
    state.ready = new Promise<void>((resolve) => { complete = resolve })
    wrapper = mount(MarketPage)
    wrapper.unmount(); complete()
    await flushPromises()
    expect(state.seen).toEqual([])
    expect(state.start).not.toHaveBeenCalled()
    expect(state.showPage).not.toHaveBeenCalled()
    expect(wrapper.emitted('ready')).toBeUndefined()
    expect(state.stop).toHaveBeenCalledOnce()
  })
})
