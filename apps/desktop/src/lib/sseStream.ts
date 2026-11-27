export interface ConsumeSseBodyOptions {
  signal: AbortSignal
  idleTimeoutMs?: number
  isClosed?: () => boolean
  onBlock: (block: string) => void
  onActivity?: () => void
}

/**
 * Shared SSE stream consumer:
 * - normalizes `\r\n` and bare `\r` frame boundaries cleanly across chunk splits
 * - flushes trailing TextDecoder bytes and partial trailing buffer at EOF
 * - detaches abort listener and cancels + releases the stream reader in `finally`
 */
export async function consumeSseResponseBody(
  body: ReadableStream<Uint8Array>,
  options: ConsumeSseBodyOptions,
): Promise<void> {
  const reader = body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let trailingCr = false
  let idleTimer: ReturnType<typeof setTimeout> | null = null

  const clearIdle = () => {
    if (idleTimer !== null) {
      clearTimeout(idleTimer)
      idleTimer = null
    }
  }

  const armIdle = () => {
    if (!options.idleTimeoutMs || options.idleTimeoutMs <= 0) return
    clearIdle()
    idleTimer = setTimeout(() => {
      void reader.cancel().catch(() => {})
    }, options.idleTimeoutMs)
  }

  const onAbort = () => {
    clearIdle()
    void reader.cancel().catch(() => {})
  }

  const appendNormalized = (chunk: string, final = false) => {
    if (trailingCr) {
      chunk = chunk.startsWith('\n') ? chunk.slice(1) : chunk
      buffer += '\n'
      trailingCr = false
    }
    if (!chunk) return
    if (!final && chunk.endsWith('\r')) {
      trailingCr = true
      chunk = chunk.slice(0, -1)
    }
    if (chunk.indexOf('\r') !== -1) {
      chunk = chunk.replace(/\r\n/g, '\n').replace(/\r/g, '\n')
    }
    buffer += chunk
  }

  options.signal.addEventListener('abort', onAbort, { once: true })
  armIdle()

  try {
    while (!options.isClosed?.() && !options.signal.aborted) {
      const { done, value } = await reader.read()
      armIdle()
      if (options.isClosed?.() || options.signal.aborted) break
      options.onActivity?.()
      if (done) {
        appendNormalized(decoder.decode(undefined, { stream: false }), true)
        if (trailingCr) {
          buffer += '\n'
          trailingCr = false
        }
        let boundary: number
        while ((boundary = buffer.indexOf('\n\n')) >= 0) {
          options.onBlock(buffer.slice(0, boundary))
          buffer = buffer.slice(boundary + 2)
          if (options.isClosed?.()) break
        }
        if (buffer.trim() && !options.isClosed?.()) {
          options.onBlock(buffer)
        }
        break
      }
      if (value) {
        appendNormalized(decoder.decode(value, { stream: true }))
        let boundary: number
        while ((boundary = buffer.indexOf('\n\n')) >= 0) {
          options.onBlock(buffer.slice(0, boundary))
          buffer = buffer.slice(boundary + 2)
          if (options.isClosed?.()) break
        }
      }
    }
  } finally {
    clearIdle()
    options.signal.removeEventListener('abort', onAbort)
    try {
      await reader.cancel().catch(() => {})
    } catch {
      // ignore reader cancellation failure
    }
    try {
      reader.releaseLock()
    } catch {
      // ignore if stream already closed/unlocked
    }
  }
}
