// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick, ref } from 'vue'
import { mount } from '@vue/test-utils'
import { useChatResponsiveMode, useElementSize, useResponsiveMode } from './useElementSize'

type ObserverCallback = (entries: ResizeObserverEntry[]) => void

let latestCallback: ObserverCallback | null = null
let disconnectSpy = vi.fn()
let rafQueue: Array<{ id: number; cb: FrameRequestCallback }> = []
let nextRafId = 1

function emitResize(width: number, height = 400) {
  if (!latestCallback) return
  latestCallback([
    {
      contentRect: { width, height } as DOMRectReadOnly,
    } as ResizeObserverEntry,
  ])
}

function flushAllRaf() {
  const pending = rafQueue.splice(0, rafQueue.length)
  for (const item of pending) {
    item.cb(16)
  }
}

describe('useElementSize & responsive hysteresis', () => {
  const originalResizeObserver = globalThis.ResizeObserver
  const originalRaf = globalThis.requestAnimationFrame
  const originalCancelRaf = globalThis.cancelAnimationFrame

  beforeEach(() => {
    latestCallback = null
    disconnectSpy = vi.fn()
    rafQueue = []
    nextRafId = 1

    globalThis.ResizeObserver = class MockResizeObserver {
      constructor(cb: ObserverCallback) {
        latestCallback = cb
      }
      observe = vi.fn()
      unobserve = vi.fn()
      disconnect = disconnectSpy
    } as unknown as typeof ResizeObserver

    globalThis.requestAnimationFrame = ((cb: FrameRequestCallback) => {
      const id = nextRafId++
      rafQueue.push({ id, cb })
      return id
    }) as typeof requestAnimationFrame

    globalThis.cancelAnimationFrame = ((id: number) => {
      rafQueue = rafQueue.filter((item) => item.id !== id)
    }) as typeof cancelAnimationFrame
  })

  afterEach(() => {
    globalThis.ResizeObserver = originalResizeObserver
    globalThis.requestAnimationFrame = originalRaf
    globalThis.cancelAnimationFrame = originalCancelRaf
  })

  it('rounds measurements to integers, coalesces bursts on rAF, and cleans up on unmount', async () => {
    let exposed: ReturnType<typeof useElementSize> | null = null
    const Comp = defineComponent({
      setup() {
        const elRef = ref<HTMLElement | null>(null)
        if (elRef.value) {
          // noop
        }
        exposed = useElementSize(elRef)
        return () =>
          h('div', {
            ref: (el) => {
              const htmlEl = el as HTMLElement | null
              if (htmlEl) {
                vi.spyOn(htmlEl, 'getBoundingClientRect').mockReturnValue({
                  width: 418.4,
                  height: 459.7,
                  top: 0,
                  left: 0,
                  right: 418.4,
                  bottom: 459.7,
                  x: 0,
                  y: 0,
                  toJSON: () => ({}),
                })
              }
              elRef.value = htmlEl
            },
          })
      },
    })

    const wrapper = mount(Comp)
    await nextTick()
    expect(exposed!.width.value).toBe(418)
    expect(exposed!.height.value).toBe(460)

    // Burst multiple ResizeObserver callbacks within a single frame — only last wins on rAF flush
    emitResize(399.6, 450.2)
    emitResize(401.7, 452.8)
    expect(exposed!.width.value).toBe(418)
    expect(rafQueue.length).toBe(1)

    flushAllRaf()
    expect(exposed!.width.value).toBe(402)
    expect(exposed!.height.value).toBe(453)

    // Queue one more frame then unmount before flush — cancelAnimationFrame + disconnect must run
    emitResize(500, 500)
    expect(rafQueue.length).toBe(1)
    wrapper.unmount()
    expect(rafQueue.length).toBe(0)
    expect(disconnectSpy).toHaveBeenCalledTimes(1)
  })

  it('applies 12px hysteresis around 400px and 560px in useChatResponsiveMode', async () => {
    let exposed: ReturnType<typeof useChatResponsiveMode> | null = null
    const Comp = defineComponent({
      setup() {
        const elRef = ref<HTMLElement | null>(null)
        exposed = useChatResponsiveMode(elRef)
        return () =>
          h('div', {
            ref: (el) => {
              const htmlEl = el as HTMLElement | null
              if (htmlEl) {
                vi.spyOn(htmlEl, 'getBoundingClientRect').mockReturnValue({
                  width: 405,
                  height: 450,
                  top: 0,
                  left: 0,
                  right: 405,
                  bottom: 450,
                  x: 0,
                  y: 0,
                  toJSON: () => ({}),
                })
              }
              elRef.value = htmlEl
            },
          })
      },
    })

    const wrapper = mount(Comp)
    await nextTick()
    // Initial non-zero width 405 -> narrow (>= 400 and < 560)
    expect(exposed!.mode.value).toBe('narrow')

    // Small drop below 400 (398, 395) inside deadband stays narrow!
    emitResize(398)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('narrow')

    emitResize(395)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('narrow')

    // Dropping <= 394 enters ultra
    emitResize(394)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('ultra')

    // Rising back up to 402 (still < 406) stays ultra without flipping back!
    emitResize(402)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('ultra')

    // Rising >= 406 enters narrow
    emitResize(406)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('narrow')

    // Crossing 560 deadband cleanly
    emitResize(562)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('narrow')

    emitResize(566)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('normal')

    emitResize(558)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('normal')

    emitResize(554)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('narrow')

    wrapper.unmount()
  })

  it('applies 10px hysteresis in useResponsiveMode around 340px and 420px', async () => {
    let exposed: ReturnType<typeof useResponsiveMode> | null = null
    const Comp = defineComponent({
      setup() {
        const elRef = ref<HTMLElement | null>(null)
        exposed = useResponsiveMode(elRef)
        return () =>
          h('div', {
            ref: (el) => {
              const htmlEl = el as HTMLElement | null
              if (htmlEl) {
                vi.spyOn(htmlEl, 'getBoundingClientRect').mockReturnValue({
                  width: 422,
                  height: 500,
                  top: 0,
                  left: 0,
                  right: 422,
                  bottom: 500,
                  x: 0,
                  y: 0,
                  toJSON: () => ({}),
                })
              }
              elRef.value = htmlEl
            },
          })
      },
    })

    const wrapper = mount(Comp)
    await nextTick()
    expect(exposed!.mode.value).toBe('normal')

    emitResize(418)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('normal')

    emitResize(415)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('compact')

    emitResize(422)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('compact')

    emitResize(425)
    flushAllRaf()
    await nextTick()
    expect(exposed!.mode.value).toBe('normal')

    wrapper.unmount()
  })
})
