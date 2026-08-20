import { ref, onUnmounted } from 'vue'
import type { SseChunk } from '@/generated/client'

/**
 * SSE encapsulation: cursor/Last-Event-ID, run_id+seq dedup, heartbeat, exponential backoff, replay-then-follow.
 * Reuses gatewayUrl from generated client; does not add new deps.
 */
// ponytail: fetch+ReadableStream over EventSource to control Last-Event-ID/?cursor= & id=seq dedup

function gatewayUrl(): string {
  const w = window as unknown as { tinadec?: { gatewayUrl?: () => string } }
  return w.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
}

export interface UseRunStreamOptions {
  sessionId?: string
  runId?: string
  cursor?: string | number | null
  onChunk?: (c: SseChunk) => void
  onError?: (e: Error) => void
  autoReconnect?: boolean
}

export function useRunStream(opts: UseRunStreamOptions = {}) {
  const chunks = ref<SseChunk[]>([])
  const status = ref<'idle'|'connecting'|'open'|'reconnecting'|'closed'|'error'>('idle')
  const lastSeq = ref<number | null>(opts.cursor != null ? Number(opts.cursor) : null)
  const error = ref<Error | null>(null)

  const seen = new Set<string>() // run_id+seq dedup
  let abort: AbortController | null = null
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null
  let attempt = 0
  let stopped = false

  function dedupKey(c: SseChunk): string { return `${c.run_id}:${c.seq}` }

  function resetDedup() { seen.clear(); chunks.value = [] }

  function parseSseBlock(block: string): SseChunk | null {
    // block: id: seq\nevent: kind\ndata: json
    let id: string | null = null
    let kind: string | null = null
    let data = ''
    for (const line of block.split('\n')) {
      if (line.startsWith('id:')) id = line.slice(3).trim()
      else if (line.startsWith('event:')) kind = line.slice(7).trim()
      else if (line.startsWith('data:')) data += line.slice(5).trim()
    }
    if (!data) return null
    try {
      const obj = JSON.parse(data) as Record<string, unknown>
      // external shape already has run_id/seq/kind etc; fallback to id/kind lines
      const seq = Number((obj.seq as number) ?? id ?? 0)
      const runId = String((obj.run_id as string) ?? (obj.runId as string) ?? opts.runId ?? opts.sessionId ?? 'unknown')
      const k = String((obj.kind as string) ?? kind ?? 'delta')
      const chunk: SseChunk = {
        run_id: runId,
        turn_id: (obj.turn_id as string) ?? (obj.turnId as string) ?? null,
        message_id: (obj.message_id as string) ?? (obj.messageId as string) ?? null,
        seq,
        kind: k,
        occurred_at: (obj.occurred_at as string) ?? (obj.occurredAt as string) ?? new Date().toISOString(),
        payload: (obj.payload as Record<string, unknown>) ?? obj,
      }
      if (k === 'heartbeat') return chunk // still dedup but caller may ignore
      return chunk
    } catch { return null }
  }

  async function connectWithCursor(cursor: string | number | null) {
    if (stopped) return
    abort?.abort()
    abort = new AbortController()
    status.value = attempt === 0 ? 'connecting' : 'reconnecting'

    // Two modes: invoke-stream is POST, run stream is GET /runs/{runId}/stream?cursor=
    // This composable is for GET follow; invoke uses api.invokeStream directly but shares dedup/heartbeat contract.
    const runId = opts.runId
    const path = runId
      ? `/api/v1/runs/${encodeURIComponent(runId)}/stream${cursor != null ? `?cursor=${encodeURIComponent(String(cursor))}` : ''}`
      : `/api/v1/events${cursor != null ? `?cursor=${encodeURIComponent(String(cursor))}` : ''}`

    const headers: Record<string, string> = { accept: 'text/event-stream' }
    if (cursor != null) headers['last-event-id'] = String(cursor)

    let res: Response
    try {
      res = await fetch(`${gatewayUrl()}${path}`, { headers, signal: abort.signal })
    } catch (e) {
      if ((e as DOMException).name === 'AbortError') return
      handleDisconnect(e instanceof Error ? e : new Error(String(e)))
      return
    }
    if (!res.ok) {
      const text = await res.text().catch(() => '')
      handleDisconnect(new Error(text || `SSE ${res.status} ${res.statusText}`))
      return
    }
    status.value = 'open'
    attempt = 0
    error.value = null

    const reader = res.body?.getReader()
    if (!reader) { handleDisconnect(new Error('No SSE body')); return }
    const decoder = new TextDecoder()
    let buffer = ''
    let block = ''

    try {
      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        // SSE blocks delimited by \n\n
        let idx: number
        while ((idx = buffer.indexOf('\n\n')) !== -1) {
          block = buffer.slice(0, idx)
          buffer = buffer.slice(idx + 2)
          if (!block.trim()) continue
          // heartbeat may be comment : keep-alive
          if (block.startsWith(':')) continue
          const chunk = parseSseBlock(block)
          if (!chunk) continue
          if (chunk.kind === 'heartbeat') { lastSeq.value = chunk.seq; continue }
          const key = dedupKey(chunk)
          if (seen.has(key)) continue
          seen.add(key)
          lastSeq.value = chunk.seq
          chunks.value.push(chunk)
          opts.onChunk?.(chunk)
        }
      }
      // normal close -> reconnect if not stopped
      if (!stopped && status.value === 'open') handleDisconnect(new Error('SSE closed'))
    } catch (e) {
      if ((e as DOMException).name === 'AbortError') return
      handleDisconnect(e instanceof Error ? e : new Error(String(e)))
    }
  }

  function handleDisconnect(e: Error) {
    error.value = e
    opts.onError?.(e)
    if (stopped || opts.autoReconnect === false) { status.value = 'error'; return }
    status.value = 'reconnecting'
    const delay = Math.min(30000, 500 * Math.pow(2, attempt++)) + Math.random() * 200
    reconnectTimer = setTimeout(() => connectWithCursor(lastSeq.value), delay)
  }

  function connect(cursor?: string | number | null) {
    stopped = false
    if (cursor !== undefined) lastSeq.value = cursor as number
    connectWithCursor(lastSeq.value)
  }

  function disconnect() {
    stopped = true
    if (reconnectTimer) clearTimeout(reconnectTimer)
    abort?.abort()
    status.value = 'closed'
  }

  function pushChunkForTest(raw: SseChunk) {
    const key = dedupKey(raw)
    if (seen.has(key)) return false
    seen.add(key)
    chunks.value.push(raw)
    lastSeq.value = raw.seq
    return true
  }

  onUnmounted(disconnect)

  return { chunks, status, lastSeq, error, connect, disconnect, resetDedup, pushChunkForTest, seen }
}
