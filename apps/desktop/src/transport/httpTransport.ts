import type { Transport, TransportRequest } from './types'

function gatewayUrl(): string {
  const w = window as unknown as { tinadec?: { gatewayUrl?: () => string } }
  return w.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
}

export const httpTransport: Transport = {
  kind: 'http',
  async request<T>(req: TransportRequest): Promise<T> {
    const url = `${gatewayUrl()}${req.path}`
    let res: Response
    try {
      res = await fetch(url, {
        method: req.method ?? 'GET',
        headers: { accept: 'application/json', ...(req.body ? { 'content-type': 'application/json' } : {}), ...(req.headers ?? {}) },
        body: req.body ? JSON.stringify(req.body) : undefined,
        signal: req.signal,
      })
    } catch (e) {
      throw new Error(`Cannot connect to backend (${gatewayUrl()}): ${e instanceof Error ? e.message : String(e)}`)
    }
    const text = await res.text()
    let data: unknown = null
    if (text) { try { data = JSON.parse(text) } catch { throw new Error(`Invalid JSON: ${text.slice(0,200)}`) } }
    if (!res.ok) {
      const rec = data as Record<string, unknown> | null
      const msg = rec?.message ?? (rec?.error as Record<string, unknown> | null)?.message ?? res.statusText
      throw new Error(typeof msg === 'string' && msg ? msg : res.statusText)
    }
    return data as T
  },
}
