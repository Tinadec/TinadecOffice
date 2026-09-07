import { ref, onUnmounted } from 'vue'
import type { SseChunk } from '@/generated/client'

function gatewayUrl(): string {
  const g = globalThis as unknown as { window?: { tinadec?: { gatewayUrl?: () => string } } }
  return g.window?.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
}

export type RunStreamStatus = 'idle' | 'connecting' | 'open' | 'reconnecting' | 'closed' | 'error'

export interface RunStreamOptions {
  runId: string
  cursor?: string | number | null
  onChunk?: (chunk: SseChunk) => void
  /** 收到所有去重后的 chunk（含 heartbeat/ack 等状态信号），供活性指示；不影响 onChunk 契约。 */
  onActivity?: (chunk: SseChunk) => void
  onError?: (error: Error) => void
  autoReconnect?: boolean
  fetchImpl?: typeof fetch
  reconnectDelayMs?: number
}

export interface RunStreamHandle {
  readonly status: { value: RunStreamStatus }
  readonly lastSeq: { value: number | null }
  readonly error: { value: Error | null }
  readonly seen: Set<string>
  connect: (cursor?: string | number | null) => void
  disconnect: () => void
  resetDedup: () => void
  pushChunkForTest: (chunk: SseChunk) => boolean
}

function dedupKey(chunk: SseChunk): string {
  return `${chunk.run_id}:${chunk.seq}`
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? value as Record<string, unknown> : {}
}

/** Parse one complete SSE event. Fields may be split across multiple data lines. */
export function parseRunSseBlock(block: string, fallbackRunId?: string): SseChunk | null {
  let id: string | null = null
  let event: string | null = null
  const dataLines: string[] = []
  for (const rawLine of block.replace(/\r/g, '').split('\n')) {
    if (!rawLine || rawLine.startsWith(':')) continue
    const separator = rawLine.indexOf(':')
    const field = separator === -1 ? rawLine : rawLine.slice(0, separator)
    const value = separator === -1 ? '' : rawLine.slice(separator + 1).replace(/^ /, '')
    if (field === 'id') id = value.trim()
    else if (field === 'event') event = value.trim()
    else if (field === 'data') dataLines.push(value)
  }
  const data = dataLines.join('\n')
  if (!data) return null
  try {
    const root = asRecord(JSON.parse(data))
    const payload = asRecord(root.payload)
    const seqValue = root.seq ?? payload.seq ?? id
    const runId = String(root.run_id ?? root.runId ?? payload.run_id ?? payload.runId ?? fallbackRunId ?? '')
    const kind = String(root.kind ?? root.event ?? payload.kind ?? event ?? 'delta')
    const chunk: SseChunk = {
      run_id: runId,
      turn_id: (root.turn_id ?? root.turnId ?? payload.turn_id ?? payload.turnId ?? null) as string | null,
      message_id: (root.message_id ?? root.messageId ?? payload.message_id ?? payload.messageId ?? null) as string | null,
      seq: Number(seqValue ?? 0),
      kind,
      occurred_at: String(root.occurred_at ?? root.occurredAt ?? payload.occurred_at ?? payload.occurredAt ?? new Date().toISOString()),
      payload: Object.keys(payload).length > 0 ? payload : root,
    }
    return Number.isFinite(chunk.seq) ? chunk : null
  } catch {
    return null
  }
}

function isTerminal(kind: string): boolean {
  return kind === 'done' || kind === 'error'
}

/**
 * Lifecycle-free durable run stream. HomeController owns one handle per run;
 * the Vue composable below only adds component unmount cleanup.
 */
export function createRunStream(options: RunStreamOptions): RunStreamHandle {
  const status = ref<RunStreamStatus>('idle')
  const lastSeq = ref<number | null>(options.cursor == null ? null : Number(options.cursor))
  const error = ref<Error | null>(null)
  const seen = new Set<string>()
  const fetchImpl = options.fetchImpl ?? fetch
  let abort: AbortController | null = null
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null
  let attempt = 0
  let stopped = true
  let connecting = false

  function resetDedup() {
    seen.clear()
  }

  function dispatch(chunk: SseChunk): boolean {
    const key = dedupKey(chunk)
    if (seen.has(key)) return false
    seen.add(key)
    if (lastSeq.value == null || chunk.seq > lastSeq.value) lastSeq.value = chunk.seq
    // onActivity 收到所有去重后的 chunk（含 ack/heartbeat 等状态信号），供上层做
    // 活性指示；onChunk 仍只收业务 chunk（heartbeat 被过滤），保持既有消费者契约。
    options.onActivity?.(chunk)
    if (chunk.kind !== 'heartbeat') options.onChunk?.(chunk)
    return true
  }

  function scheduleReconnect(reason: Error) {
    error.value = reason
    options.onError?.(reason)
    if (stopped || options.autoReconnect === false) {
      status.value = 'error'
      return
    }
    status.value = 'reconnecting'
    const base = options.reconnectDelayMs ?? 500
    const delay = Math.min(30_000, base * Math.pow(2, attempt++))
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null
      void connectWithCursor(lastSeq.value)
    }, delay)
  }

  async function connectWithCursor(cursor: string | number | null) {
    if (stopped || connecting) return
    connecting = true
    abort?.abort()
    abort = new AbortController()
    status.value = attempt === 0 ? 'connecting' : 'reconnecting'
    const search = cursor == null ? '' : `?after_seq=${encodeURIComponent(String(cursor))}`
    const headers: Record<string, string> = { accept: 'text/event-stream' }
    if (cursor != null) headers['last-event-id'] = String(cursor)
    try {
      const response = await fetchImpl(`${gatewayUrl()}/api/v1/runs/${encodeURIComponent(options.runId)}/stream${search}`, {
        headers,
        signal: abort.signal,
      })
      if (!response.ok) {
        const text = await response.text().catch(() => '')
        throw new Error(text || `SSE ${response.status} ${response.statusText}`)
      }
      const reader = response.body?.getReader()
      if (!reader) throw new Error('No SSE body')
      status.value = 'open'
      attempt = 0
      error.value = null
      const decoder = new TextDecoder()
      let buffer = ''
      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        buffer = buffer.replace(/\r\n/g, '\n').replace(/\r/g, '\n')
        let boundary: number
        while ((boundary = buffer.indexOf('\n\n')) !== -1) {
          const block = buffer.slice(0, boundary)
          buffer = buffer.slice(boundary + 2)
          const chunk = parseRunSseBlock(block, options.runId)
          if (!chunk) continue
          dispatch(chunk)
          if (isTerminal(chunk.kind)) {
            stopped = true
            status.value = 'closed'
            return
          }
        }
      }
      buffer += decoder.decode()
      const finalBlock = buffer.trim()
      if (finalBlock && !stopped) {
        const chunk = parseRunSseBlock(finalBlock, options.runId)
        if (chunk) {
          dispatch(chunk)
          if (isTerminal(chunk.kind)) {
            stopped = true
            status.value = 'closed'
            return
          }
        }
      }
      if (!stopped) scheduleReconnect(new Error('SSE closed before terminal event'))
    } catch (cause) {
      if (cause instanceof DOMException && cause.name === 'AbortError') return
      scheduleReconnect(cause instanceof Error ? cause : new Error(String(cause)))
    } finally {
      connecting = false
    }
  }

  function connect(cursor?: string | number | null) {
    stopped = false
    if (cursor !== undefined) lastSeq.value = cursor == null ? null : Number(cursor)
    void connectWithCursor(lastSeq.value)
  }

  function disconnect() {
    stopped = true
    if (reconnectTimer) clearTimeout(reconnectTimer)
    reconnectTimer = null
    abort?.abort()
    abort = null
    status.value = 'closed'
  }

  return {
    status,
    lastSeq,
    error,
    seen,
    connect,
    disconnect,
    resetDedup,
    pushChunkForTest: dispatch,
  }
}

export interface UseRunStreamOptions extends Omit<RunStreamOptions, 'runId'> {
  runId?: string
  sessionId?: string
}

/** Vue lifecycle wrapper retained for feature panels and existing callers. */
export function useRunStream(options: UseRunStreamOptions = {}): RunStreamHandle & { chunks: ReturnType<typeof ref<SseChunk[]>> } {
  const chunks = ref<SseChunk[]>([])
  if (!options.runId) {
    return {
      ...createRunStream({ runId: options.sessionId ?? 'missing-run', ...options, autoReconnect: false }),
      chunks,
    }
  }
  const runner = createRunStream({
    ...options,
    runId: options.runId,
    onChunk: (chunk) => {
      chunks.value.push(chunk)
      options.onChunk?.(chunk)
    },
  })
  onUnmounted(runner.disconnect)
  return { ...runner, chunks }
}
