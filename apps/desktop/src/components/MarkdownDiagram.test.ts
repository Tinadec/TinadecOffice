// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { afterEach, describe, expect, it, vi } from 'vitest'
import mermaid, { type ParseResult } from 'mermaid'
import MarkdownDiagram, { diagramMinWidth } from './MarkdownDiagram.vue'

vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    parse: vi.fn(async () => true),
    render: vi.fn(async () => ({ svg: '<svg data-testid="diagram"></svg>' })),
  },
}))

const diagram = vi.mocked(mermaid)

function render(code: string) {
  return mount(MarkdownDiagram, {
    props: { code },
    attachTo: document.body,
    global: { plugins: [createI18n({
      legacy: false, locale: 'en',
      messages: { en: { chat: { markdownDiagram: 'Diagram', markdownDiagramError: 'Diagram failed' } } },
    })] },
  })
}

afterEach(() => {
  vi.clearAllMocks()
})

const SOURCE = 'graph TD\n  A --> B'

describe('MarkdownDiagram', () => {
  it('renders the diagram with a per-diagram id and the strict security level', async () => {
    const wrapper = render(SOURCE)
    await flushPromises()
    expect(wrapper.find('.markdown-diagram-svg svg').exists()).toBe(true)
    expect(diagram.initialize).toHaveBeenCalledWith(expect.objectContaining({ securityLevel: 'strict', startOnLoad: false }))
    expect(diagram.render).toHaveBeenCalledWith(expect.stringMatching(/^md-diagram-\d+$/), SOURCE)
    wrapper.unmount()
  })

  it('gives every diagram on the page its own id and renders them one at a time', async () => {
    // Mermaid scopes styles and its scratch node by id; a shared id drew diagrams into each other.
    let active = 0
    let overlapped = false
    diagram.render.mockImplementation(async () => {
      active += 1
      if (active > 1) overlapped = true
      await new Promise((resolve) => setTimeout(resolve, 5))
      active -= 1
      return { svg: '<svg data-testid="diagram"></svg>' } as never
    })
    const first = render(SOURCE)
    const second = render('graph LR\n  C --> D')
    await new Promise((resolve) => setTimeout(resolve, 40))
    await flushPromises()
    const ids = diagram.render.mock.calls.map((call) => call[0])
    expect(ids).toHaveLength(2)
    expect(new Set(ids).size).toBe(2)
    expect(overlapped).toBe(false)
    expect(first.find('.markdown-diagram-svg svg').exists()).toBe(true)
    expect(second.find('.markdown-diagram-svg svg').exists()).toBe(true)
    first.unmount()
    second.unmount()
    diagram.render.mockImplementation(async () => ({ svg: '<svg data-testid="diagram"></svg>' }) as never)
  })

  it('keeps the source visible when the diagram cannot be parsed', async () => {
    // mermaid resolves `false` for an invalid diagram when errors are suppressed.
    diagram.parse.mockResolvedValueOnce(false as unknown as ParseResult)
    const wrapper = render(SOURCE)
    await flushPromises()
    expect(wrapper.find('.markdown-diagram-svg').exists()).toBe(false)
    expect(wrapper.get('.markdown-diagram-fallback code').text()).toBe(SOURCE)
    expect(wrapper.get('.markdown-diagram-note').text()).toContain('Diagram failed')
    wrapper.unmount()
  })

  it('does not hand an oversized diagram to mermaid', async () => {
    const wrapper = render(`graph TD\n${'A-->B\n'.repeat(4000)}`)
    await flushPromises()
    expect(diagram.render).not.toHaveBeenCalled()
    expect(wrapper.find('.markdown-diagram-note').exists()).toBe(true)
    wrapper.unmount()
  })
})

describe('diagram width on a narrow column', () => {
  it('lets a diagram shrink to 80% of its laid-out width, then scroll', async () => {
    expect(diagramMinWidth('<svg id="d" width="100%" style="max-width: 640px;" viewBox="0 0 640 900">')).toBe('512px')
    expect(diagramMinWidth('<svg id="d" viewBox="0 0 10 10">')).toBeNull()
    diagram.render.mockResolvedValueOnce({ svg: '<svg style="max-width: 400px;"></svg>' } as never)
    const wrapper = render(SOURCE)
    await flushPromises()
    expect(wrapper.get('.markdown-diagram-svg').attributes('style')).toContain('--diagram-min-width: 320px')
    wrapper.unmount()
  })
})
