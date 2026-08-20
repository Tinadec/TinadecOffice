/** WS reserved abstraction — http/sse is the active path, WS only placeholder per spec. */
export type TransportKind = 'http' | 'sse' | 'ws'

export interface TransportRequest {
  path: string
  method?: string
  body?: unknown
  headers?: Record<string, string>
  signal?: AbortSignal
}

export interface Transport {
  kind: TransportKind
  request<T>(req: TransportRequest): Promise<T>
  sse?(req: TransportRequest, onChunk: (raw: string) => void, onError?: (e: Error) => void): AbortController
}

export interface WsTransport extends Transport {
  kind: 'ws'
  connect(url: string): WebSocket
}
