import { ApiError } from './apiError'
import { assertHostAccess } from './hostAccess'
import { isAbortError } from './isAbortError'

const READ_RETRY_DELAYS = [250, 750] as const

function waitForRetry(delay: number, signal?: AbortSignal | null): Promise<void> {
  signal?.throwIfAborted()
  return new Promise((resolve, reject) => {
    const abort = () => { clearTimeout(timer); signal?.removeEventListener('abort', abort); reject(signal?.reason) }
    const timer = setTimeout(() => { signal?.removeEventListener('abort', abort); resolve() }, delay)
    signal?.addEventListener('abort', abort, { once: true })
  })
}

/** Retry only a failed read transport. HTTP/config errors and writes are never replayed. */
export async function readBackendResponse(
  url: string,
  path: string,
  init: RequestInit,
  context: { storageId: string },
): Promise<{ response: Response; text: string }> {
  const request = { ...init, headers: new Headers(init.headers) }
  const read = ['GET', 'HEAD'].includes((request.method ?? 'GET').toUpperCase())
  const delays = read ? READ_RETRY_DELAYS : []
  for (let attempt = 0; ; attempt++) {
    // Host authorization and the captured scope apply to every attempt. A retry
    // cannot turn a rejected/old host into a public-health fallback.
    await assertHostAccess(path, request.signal ?? undefined)
    try {
      const response = await fetch(url, request)
      // A connection can fail after headers arrive. Include body transport in
      // the same budget, before JSON parsing and application error handling.
      const text = await response.text()
      request.signal?.throwIfAborted()
      return { response, text }
    } catch (error) {
      request.signal?.throwIfAborted()
      if (isAbortError(error) || error instanceof ApiError) throw error
      if (!(error instanceof TypeError) && !(error instanceof Error && error.name === 'NetworkError')) throw error
      if (attempt < delays.length) { await waitForRetry(delays[attempt]!, request.signal); continue }
      const message = `Cannot connect to backend (${new URL(url).origin}): ${error.message}`
      throw new ApiError(message, 0, {
        code: read ? 'backend_network_unavailable' : 'backend_write_outcome_unknown',
        category: read ? 'retryable' : 'environment_unavailable',
        retryable: read,
        actions: read ? ['retry'] : [],
      }, context)
    }
  }
}
