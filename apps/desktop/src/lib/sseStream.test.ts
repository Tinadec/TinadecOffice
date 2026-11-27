// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { consumeSseResponseBody } from './sseStream'

describe('consumeSseResponseBody', () => {
  it('normalizes CRLF split across chunk boundaries and bare CR separators', async () => {
    const encoder = new TextEncoder()
    const blocks: string[] = []
    const stream = new ReadableStream<Uint8Array>({
      start(ctrl) {
        ctrl.enqueue(encoder.encode('event: a\rdata: 1\r'))
        ctrl.enqueue(encoder.encode('\revent: b\r\ndata: 2\r'))
        ctrl.enqueue(encoder.encode('\n\r\nevent: c\ndata: 3'))
        ctrl.close()
      },
    })

    const ac = new AbortController()
    await consumeSseResponseBody(stream, {
      signal: ac.signal,
      onBlock: (b) => blocks.push(b),
    })

    expect(blocks).toEqual([
      'event: a\ndata: 1',
      'event: b\ndata: 2',
      'event: c\ndata: 3',
    ])
  })

  it('detaches abort listener and cancels reader cleanly on early close', async () => {
    let cancelled = false
    let added = 0
    let removed = 0
    const ac = new AbortController()
    const origAdd = ac.signal.addEventListener.bind(ac.signal)
    const origRemove = ac.signal.removeEventListener.bind(ac.signal)
    ac.signal.addEventListener = ((...args: Parameters<AbortSignal['addEventListener']>) => {
      if (args[0] === 'abort') added++
      return origAdd(...args)
    }) as AbortSignal['addEventListener']
    ac.signal.removeEventListener = ((...args: Parameters<AbortSignal['removeEventListener']>) => {
      if (args[0] === 'abort') removed++
      return origRemove(...args)
    }) as AbortSignal['removeEventListener']

    const encoder = new TextEncoder()
    let closedByConsumer = false
    const stream = new ReadableStream<Uint8Array>({
      start(ctrl) {
        ctrl.enqueue(encoder.encode('data: first\n\ndata: second\n\n'))
      },
      cancel() {
        cancelled = true
      },
    })

    const received: string[] = []
    await consumeSseResponseBody(stream, {
      signal: ac.signal,
      isClosed: () => closedByConsumer,
      onBlock: (b) => {
        received.push(b)
        closedByConsumer = true
      },
    })

    expect(received).toEqual(['data: first'])
    expect(cancelled).toBe(true)
    expect(added).toBe(1)
    expect(removed).toBe(1)
  })
})
