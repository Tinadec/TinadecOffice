// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { provideMessageScroller } from './useMessageScroller'

afterEach(() => { vi.restoreAllMocks(); document.body.replaceChildren() })

function transcript(count: number) {
  const frames = new Map<number, FrameRequestCallback>()
  let frameId = 0
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => {
    frames.set(++frameId, callback)
    return frameId
  })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(id => { frames.delete(id) })
  let engine!: ReturnType<typeof provideMessageScroller>
  const wrapper = mount(defineComponent({ setup() {
    engine = provideMessageScroller({ defaultScrollPosition: 'start' })
    return () => h('div')
  } }))
  const viewport = document.createElement('div')
  const content = document.createElement('div')
  const spacer = document.createElement('div')
  viewport.append(content)
  document.body.append(viewport)
  let reads = 0
  Object.defineProperties(viewport, {
    clientHeight: { get: () => 500 },
    scrollHeight: { get: () => content.children.length * 100 },
  })
  viewport.getBoundingClientRect = () => { reads++; return new DOMRect(0, 0, 600, 500) }
  viewport.scrollTo = ((options: ScrollToOptions) => { viewport.scrollTop = options.top ?? 0 }) as typeof viewport.scrollTo
  const rows: HTMLElement[] = []
  function addRow(id: string) {
    const row = document.createElement('div')
    row.dataset.messageId = id
    row.dataset.scrollAnchor = 'true'
    row.getBoundingClientRect = () => {
      reads++
      return new DOMRect(0, rows.indexOf(row) * 100 - viewport.scrollTop, 600, 100)
    }
    return row
  }
  for (let i = 0; i < count; i++) {
    const row = addRow(`m${i}`)
    rows.push(row)
    content.append(row)
    engine.registerMessage(`m${i}`, row, null)
  }
  content.append(spacer)
  engine.context.setViewportElement(viewport)
  engine.context.setContentElement(content)
  engine.context.setSpacerElement(spacer)
  engine.context.handleContentChange()
  function flush() {
    for (let pass = 0; frames.size && pass < 10; pass++) {
      const pending = [...frames.values()]
      frames.clear()
      pending.forEach(callback => callback(0))
    }
  }
  flush()
  return { engine, viewport, rows, content, spacer, addRow, flush,
    resetReads: () => { reads = 0 }, reads: () => reads,
    cleanup: () => { wrapper.unmount(); viewport.remove() },
  }
}

describe('message scroller geometry', () => {
  it('coalesces scroll events and bounds layout reads for 5000 rows', () => {
    const fixture = transcript(5000)
    fixture.viewport.scrollTop = 499000
    fixture.resetReads()
    for (let i = 0; i < 100; i++) fixture.engine.context.syncAfterScroll()
    expect(fixture.reads()).toBe(0)
    fixture.flush()
    expect(fixture.reads()).toBeLessThanOrEqual(20)
    expect(fixture.engine.context.scrollable.value).toEqual({ start: true, end: true })
    fixture.cleanup()
  })

  it('holds the visible message at the same position when older rows arrive', () => {
    const fixture = transcript(100)
    fixture.viewport.scrollTop = 5000
    fixture.engine.context.userScrollIntent()
    fixture.engine.context.syncAfterScroll()
    fixture.flush()
    const anchor = fixture.rows[50]!
    const before = anchor.getBoundingClientRect().top
    const older = fixture.addRow('older')
    fixture.rows.unshift(older)
    fixture.content.prepend(older)
    fixture.engine.registerMessage('older', older, null)
    fixture.engine.context.handleContentChange()
    fixture.flush()
    expect(anchor.getBoundingClientRect().top).toBe(before)
    expect(fixture.viewport.scrollTop).toBe(5100)
    fixture.cleanup()
  })

  it('cancels queued measurements when the view is destroyed', () => {
    const fixture = transcript(100)
    fixture.engine.context.syncAfterScroll()
    fixture.cleanup()
    fixture.resetReads()
    fixture.flush()
    expect(fixture.reads()).toBe(0)
  })
})
