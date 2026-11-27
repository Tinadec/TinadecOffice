import { assertHostAccess } from './hostAccess'
import { consumeSseResponseBody } from './sseStream'
import {
  currentEndpointId,
  readEndpointEventCursor,
  resolveLocalGatewayBase,
  rewriteApiPathForEndpoint,
  unwrapPeerProxyPath,
  writeEndpointEventCursor,
  type EndpointId,
  type EndpointInput,
} from './endpointScope'
/** Fetch-based SSE keeps the captured storage and endpoint headers on reconnects. */
/**
 * Core writes a heartbeat every 15s. A stream that stays silent for three of them is treated as
 * dead: a half-open connection (sleep/wake, a proxy or network leg dropping it without a FIN)
 * otherwise leaves read() waiting forever, and the window silently stops updating.
 */
export const STREAM_IDLE_TIMEOUT_MS = 45_000

export interface ScopedEventSourceOptions {
  endpoint?: EndpointInput
  afterSeq?: number | null
  persistCursor?: boolean
  cursorScopeKey?: string
}

function routeEventStreamUrl(rawUrl: string, endpointId: EndpointId): {
  routedUrl: string
  canonicalStreamKey: string
} {
  if (/^[a-zA-Z][a-zA-Z\d+.-]*:\/\//.test(rawUrl)) {
    const parsed = new URL(rawUrl)
    const rawPath = `${parsed.pathname}${parsed.search}`
    const unwrapped = unwrapPeerProxyPath(rawPath)
    const rewritten = rewriteApiPathForEndpoint(rawPath, endpointId, { gatewayBase: parsed.origin })
    return {
      routedUrl: `${parsed.origin}${rewritten}`,
      canonicalStreamKey: unwrapped.innerPath,
    }
  }
  const base = resolveLocalGatewayBase()
  const unwrapped = unwrapPeerProxyPath(rawUrl)
  const rewritten = rewriteApiPathForEndpoint(rawUrl, endpointId, { gatewayBase: base })
  return {
    routedUrl: `${base}${rewritten}`,
    canonicalStreamKey: unwrapped.innerPath,
  }
}

export class ScopedEventSource {
  private readonly events = new EventTarget()
  readonly url: string
  readonly endpointId: EndpointId
  onmessage: ((event: MessageEvent) => void) | null = null
  onerror: ((event: Event) => void) | null = null
  readyState = 0
  private abort: AbortController | null = null
  private closed = false
  /**
   * Per-run resume positions. Event frames carry only the run they advance, heartbeats carry the
   * whole vector; merging both is what lets a reconnect resume exactly. Keeping just the last id
   * would lose every other run's position and replay those runs from zero.
   */
  private watermarks = new Map<string, string>()
  private lastFrameId = ''
  private afterSeq: number | null = null
  private readonly persistCursor: boolean
  private readonly cursorStreamKey: string
  private readonly trackAfterSeqQuery: boolean
  private retryTimer: ReturnType<typeof setTimeout> | null = null
  private reconnectAttempts = 0
  private static readonly BASE_RETRY_MS = 1_000
  private static readonly MAX_RETRY_MS = 30_000
  constructor(url: string, private readonly storageId: string, private readonly fetchImpl: typeof fetch = fetch,
    private readonly idleTimeoutMs = STREAM_IDLE_TIMEOUT_MS, options?: ScopedEventSourceOptions) {
    const inferredFromUrl = /^[a-zA-Z][a-zA-Z\d+.-]*:\/\//.test(url)
      ? unwrapPeerProxyPath(new URL(url).pathname + new URL(url).search).endpointScope
      : unwrapPeerProxyPath(url).endpointScope
    this.endpointId = currentEndpointId(
      options?.endpoint ?? (inferredFromUrl.kind === 'peer' ? inferredFromUrl : undefined),
    )
    const routed = routeEventStreamUrl(url, this.endpointId)
    this.url = routed.routedUrl
    this.cursorStreamKey = options?.cursorScopeKey ?? routed.canonicalStreamKey
    this.persistCursor = Boolean(options?.persistCursor || options?.cursorScopeKey || options?.endpoint != null)
    this.trackAfterSeqQuery = options?.afterSeq !== undefined
    if (options?.afterSeq != null && Number.isFinite(options.afterSeq)) {
      this.afterSeq = Math.max(0, Number(options.afterSeq))
    }
    if (this.persistCursor) {
      const saved = readEndpointEventCursor(this.endpointId, this.storageId, this.cursorStreamKey)
      if (saved) {
        for (const [run, seq] of Object.entries(saved.watermarks)) this.watermarks.set(run, seq)
        if (saved.lastFrameId) this.lastFrameId = saved.lastFrameId
        if (this.afterSeq === null && saved.afterSeq !== null) this.afterSeq = saved.afterSeq
      }
    }
    void this.connect()
  }
  get lastAfterSeq(): number | null { return this.afterSeq }
  close() { this.closed = true; this.readyState = 2; this.abort?.abort(); if (this.retryTimer) { clearTimeout(this.retryTimer); this.retryTimer = null } }
  addEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | AddEventListenerOptions) { this.events.addEventListener(type, listener, options) }
  removeEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | EventListenerOptions) { this.events.removeEventListener(type, listener, options) }
  dispatchEvent(event: Event) { return this.events.dispatchEvent(event) }
  private saveCursorSnapshot() {
    if (!this.persistCursor) return
    writeEndpointEventCursor(this.endpointId, this.storageId, this.cursorStreamKey, {
      watermarks: Object.fromEntries(this.watermarks.entries()),
      lastFrameId: this.lastFrameId,
      afterSeq: this.afterSeq,
    })
  }
  private dispatchBlock(block: string) {
    let type = 'message'; const data: string[] = []
    for (const line of block.replace(/\r/g, '').split('\n')) {
      if (!line || line.startsWith(':')) continue
      const separator = line.indexOf(':'); const key = separator < 0 ? line : line.slice(0, separator)
      const value = separator < 0 ? '' : line.slice(separator + 1).replace(/^ /, '')
      if (key === 'event') type = value || 'message'
      else if (key === 'id') { this.lastFrameId = value; this.applyCursor(value) }
      else if (key === 'data') data.push(value)
    }
    if (!data.length) return
    const payloadText = data.join('\n')
    try {
      const parsed = JSON.parse(payloadText) as Record<string, unknown>
      const candidateSeq = typeof parsed?.after_seq === 'number'
        ? parsed.after_seq
        : typeof parsed?.seq === 'number'
          ? parsed.seq
          : typeof (parsed?.payload as Record<string, unknown> | undefined)?.sequence === 'number'
            ? Number((parsed.payload as Record<string, unknown>).sequence)
            : null
      if (candidateSeq !== null && Number.isFinite(candidateSeq) && (this.afterSeq === null || candidateSeq > this.afterSeq)) {
        this.afterSeq = candidateSeq
      }
    } catch { /* non-JSON SSE frame */ }
    this.saveCursorSnapshot()
    const event = new MessageEvent(type, { data: payloadText, lastEventId: this.lastFrameId })
    if (type === 'message') this.onmessage?.(event)
    this.dispatchEvent(event)
  }
  private applyCursor(value: string) {
    if (!value) return
    if (/^\d+$/.test(value)) {
      const numeric = Number.parseInt(value, 10)
      if (Number.isFinite(numeric) && (this.afterSeq === null || numeric > this.afterSeq)) {
        this.afterSeq = numeric
      }
      this.saveCursorSnapshot()
      return
    }
    if (value.includes(',')) {
      this.watermarks.clear()
      for (const segment of value.split(',')) this.applyPair(segment)
      this.saveCursorSnapshot()
      return
    }
    this.applyPair(value)
    this.saveCursorSnapshot()
  }
  private applyPair(segment: string) {
    const separator = segment.lastIndexOf(':')
    if (separator <= 0) return
    const run = segment.slice(0, separator); const sequence = segment.slice(separator + 1)
    if (!/^[0-9a-f]{32}$/.test(run) || !/^\d+$/.test(sequence)) return
    this.watermarks.set(run, sequence)
  }
  private encodeWatermarks() {
    return [...this.watermarks.entries()].map(([run, sequence]) => `${run}:${sequence}`).join(',')
  }
  private buildConnectUrl(): string {
    if (!this.trackAfterSeqQuery || this.afterSeq === null || this.afterSeq <= 0) return this.url
    const parsed = new URL(this.url)
    parsed.searchParams.set('after_seq', String(this.afterSeq))
    return parsed.toString()
  }
  private async connect() {
    if (this.closed) return
    const request = new AbortController()
    this.abort = request
    try {
      const targetUrl = this.buildConnectUrl()
      await assertHostAccess(targetUrl, request.signal)
      if (this.closed || request.signal.aborted) return
      // Native fetch is a Window method in Electron. Calling it as `this.fetchImpl(...)`
      // binds `this` to ScopedEventSource and throws "Illegal invocation", which the
      // reconnect catch below then swallowed forever. Keep the injected function, but
      // invoke it unbound so both the browser implementation and test doubles work.
      const fetchImpl = this.fetchImpl
      const cursor = this.encodeWatermarks() || (this.trackAfterSeqQuery && this.afterSeq && this.afterSeq > 0 ? String(this.afterSeq) : '')
      const response = await fetchImpl(targetUrl, { signal: request.signal, headers: {
        accept: 'text/event-stream',
        'x-tinadec-storage-id': this.storageId,
        'x-tinadec-endpoint-id': this.endpointId,
        ...(cursor ? { 'last-event-id': cursor } : {}),
      } })
      if (this.closed || request.signal.aborted) { await response.body?.cancel().catch(() => {}); return }
      // A rejected cursor must be dropped, not resent: retrying with the same unreadable value
      // would loop on 400 forever. Falling back to a full replay is the pre-cursor behaviour.
      if (response.status === 400) {
        this.watermarks.clear()
        this.afterSeq = null
        this.lastFrameId = ''
        this.saveCursorSnapshot()
      }
      if (!response.ok || !response.body) throw new Error(`Event stream returned ${response.status}`)
      this.readyState = 1
      this.reconnectAttempts = 0
      await consumeSseResponseBody(response.body, {
        signal: request.signal,
        idleTimeoutMs: this.idleTimeoutMs,
        isClosed: () => this.closed,
        onBlock: (block) => this.dispatchBlock(block),
      })
    } catch { if (!this.closed) this.onerror?.(new Event('error')) }
    if (!this.closed) {
      this.readyState = 0
      const delayMs = this.reconnectAttempts < 2
        ? ScopedEventSource.BASE_RETRY_MS
        : Math.min(
            ScopedEventSource.MAX_RETRY_MS,
            Math.round(ScopedEventSource.BASE_RETRY_MS * (2 ** Math.min(this.reconnectAttempts - 1, 5)) * (0.75 + Math.random() * 0.5)),
          )
      this.reconnectAttempts += 1
      this.retryTimer = setTimeout(() => { this.retryTimer = null; void this.connect() }, delayMs)
    }
  }
}
