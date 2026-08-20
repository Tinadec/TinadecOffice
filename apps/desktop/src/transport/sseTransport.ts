import type { Transport } from './types'
import { httpTransport } from './httpTransport'

/** SSE via fetch streaming — placeholder that reuses httpTransport for JSON; real streaming is in useRunStream. */
export const sseTransport: Transport = {
  kind: 'sse',
  request: httpTransport.request,
  sse(_req, _onChunk, _onError) {
    // ponytail: real SSE is in composables/useRunStream.ts; this is the transport seam placeholder
    return new AbortController()
  },
}
