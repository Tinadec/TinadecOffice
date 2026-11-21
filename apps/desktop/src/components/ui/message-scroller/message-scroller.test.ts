// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick, ref } from 'vue'
import { mount } from '@vue/test-utils'
import { MessageScroller, MessageScrollerContent, MessageScrollerItem, MessageScrollerProvider, MessageScrollerViewport } from '.'

/**
 * happy-dom does no layout, so these tests install a stacked one: every row is ROW px tall,
 * rows sit one under the other inside a VIEWPORT px viewport, and the viewport scrolls by
 * its scrollTop. That is enough to pin following, not stealing the reader's position,
 * anchoring a new turn, and how many rows the engine measures per update.
 */
const ROW = 100
const VIEWPORT = 300

let rowMeasurements = 0
const originalRect = HTMLElement.prototype.getBoundingClientRect

function viewportOf(el: Element): HTMLElement | null {
  return el.closest('[data-slot="message-scroller-viewport"]') as HTMLElement | null
}

function rowsOf(content: Element): HTMLElement[] {
  return [...content.children].filter((c) => (c as HTMLElement).dataset.slot === 'message-scroller-item') as HTMLElement[]
}

function spacerHeight(content: Element): number {
  const spacer = content.querySelector('[data-message-scroller-spacer]') as HTMLElement | null
  return spacer && !spacer.hidden ? Number.parseFloat(spacer.style.height) || 0 : 0
}

function contentHeight(viewport: HTMLElement): number {
  const content = viewport.querySelector('[data-slot="message-scroller-content"]')
  return content ? rowsOf(content).length * ROW + spacerHeight(content) : 0
}

function rect(top: number, height: number): DOMRect {
  return { top, bottom: top + height, height, left: 0, right: 400, width: 400, x: 0, y: top, toJSON() {} } as DOMRect
}

beforeEach(() => {
  rowMeasurements = 0
  HTMLElement.prototype.getBoundingClientRect = function (this: HTMLElement) {
    if (this.dataset.slot === 'message-scroller-viewport') return rect(0, VIEWPORT)
    const viewport = viewportOf(this)
    if (this.dataset.slot === 'message-scroller-item' && viewport) {
      rowMeasurements += 1
      const index = rowsOf(this.parentElement!).indexOf(this)
      return rect(index * ROW - viewport.scrollTop, ROW)
    }
    if (this.dataset.slot === 'message-scroller-content' && viewport) return rect(-viewport.scrollTop, contentHeight(viewport))
    return rect(0, 0)
  }
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
    configurable: true,
    get(this: HTMLElement) { return this.dataset.slot === 'message-scroller-viewport' ? VIEWPORT : 0 },
  })
  Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get(this: HTMLElement) { return this.dataset.slot === 'message-scroller-viewport' ? Math.max(VIEWPORT, contentHeight(this)) : 0 },
  })
  HTMLElement.prototype.scrollTo = function (this: HTMLElement, options?: ScrollToOptions | number) {
    const top = typeof options === 'number' ? options : options?.top ?? 0
    this.scrollTop = Math.min(top, Math.max(0, this.scrollHeight - VIEWPORT))
    this.dispatchEvent(new Event('scroll'))
  } as typeof HTMLElement.prototype.scrollTo
})

afterEach(() => {
  HTMLElement.prototype.getBoundingClientRect = originalRect
  delete (HTMLElement.prototype as { clientHeight?: number }).clientHeight
  delete (HTMLElement.prototype as { scrollHeight?: number }).scrollHeight
})

const frames = async (count = 4) => {
  for (let i = 0; i < count; i += 1) {
    await nextTick()
    await new Promise((resolve) => setTimeout(resolve, 20))
  }
}

function harness(initial: { id: string; anchor?: boolean }[], props: Record<string, unknown> = {}) {
  const rows = ref(initial)
  const Harness = defineComponent({
    setup: () => () => h(MessageScrollerProvider, props, () =>
      h(MessageScroller, null, () =>
        h(MessageScrollerViewport, null, () =>
          h(MessageScrollerContent, null, () =>
            rows.value.map((row) => h(MessageScrollerItem, { key: row.id, messageId: row.id, scrollAnchor: !!row.anchor }, () => row.id)),
          ),
        ),
      ),
    ),
  })
  const wrapper = mount(Harness, { attachTo: document.body })
  const viewport = () => wrapper.find('[data-slot="message-scroller-viewport"]').element as HTMLElement
  return { rows, wrapper, viewport }
}

const many = (count: number) => Array.from({ length: count }, (_, i) => ({ id: `m${i}` }))

describe('MessageScroller', () => {
  it('opens at the end and follows new rows while the reader is at the bottom', async () => {
    const { rows, viewport, wrapper } = harness(many(10), { autoScroll: true })
    // Poll rather than read after a fixed sleep: the mount → default-scroll and the append →
    // follow chain each settle a task or two later when the machine is busy.
    await vi.waitFor(() => expect(viewport().scrollTop).toBe(10 * ROW - VIEWPORT), { timeout: 2000, interval: 20 })
    rows.value = [...rows.value, { id: 'm10' }, { id: 'm11' }]
    await vi.waitFor(() => expect(viewport().scrollTop).toBe(12 * ROW - VIEWPORT), { timeout: 2000, interval: 20 })
    wrapper.unmount()
  })

  it('does not take the scroll position away from a reader who scrolled up', async () => {
    const { rows, viewport, wrapper } = harness(many(10), { autoScroll: true })
    await vi.waitFor(() => expect(viewport().scrollTop).toBe(10 * ROW - VIEWPORT), { timeout: 2000, interval: 20 })
    const el = viewport()
    el.dispatchEvent(new WheelEvent('wheel', { deltaY: -200 }))
    el.scrollTop = 100
    el.dispatchEvent(new Event('scroll'))
    await frames()
    rows.value = [...rows.value, { id: 'm10' }]
    // Wait until the appended row is actually in the DOM, so "stayed at 100" cannot be vacuous.
    await vi.waitFor(() => expect(el.querySelectorAll('[data-slot="message-scroller-item"]').length).toBe(11), { timeout: 2000, interval: 20 })
    await frames()
    expect(el.scrollTop).toBe(100)
    wrapper.unmount()
  })

  it('anchors a new turn near the top instead of jumping to the bottom', async () => {
    const { rows, viewport, wrapper } = harness(many(6))
    await frames()
    rows.value = [...rows.value, { id: 'turn', anchor: true }, { id: 'answer' }]
    await frames()
    // The anchor (row 6) lands below the previous-row peek; its spacer keeps that reachable.
    // Poll instead of reading once: the mutation → anchor-scroll chain settles a task or two
    // later under load (a fixed sleep read the pre-scroll position on a busy machine and made
    // this look like a lost anchor). Both bounds still hold, so a truly lost scroll stays red.
    await vi.waitFor(() => {
      const anchorTop = 6 * ROW - viewport().scrollTop
      expect(anchorTop).toBeGreaterThanOrEqual(0)
      expect(anchorTop).toBeLessThanOrEqual(64 + 1)
    }, { timeout: 2000, interval: 20 })
    wrapper.unmount()
  })

  it.each([200, 1000])('measures a bounded number of rows per scroll and append (%i rows)', async (count) => {
    // Upstream read every row above the viewport on each scroll (capturePrependAnchor) and
    // every row for the content height; at 200 rows that was 417 reads for one scroll + append.
    const { rows, viewport, wrapper } = harness(many(count), { autoScroll: true })
    const el = viewport()
    await vi.waitFor(() => expect(el.querySelectorAll('[data-slot="message-scroller-item"]').length).toBe(count), { timeout: 2000, interval: 20 })
    rowMeasurements = 0
    el.scrollTop = (count - 5) * ROW
    el.dispatchEvent(new Event('scroll'))
    await frames()
    rows.value = [...rows.value, { id: 'extra' }]
    // Wait for the append to be processed: an unprocessed append would read 0 rows and pass
    // vacuously.
    await vi.waitFor(() => expect(el.querySelectorAll('[data-slot="message-scroller-item"]').length).toBe(count + 1), { timeout: 2000, interval: 20 })
    await frames()
    expect(rowMeasurements).toBeLessThan(50)
    wrapper.unmount()
  })
})
