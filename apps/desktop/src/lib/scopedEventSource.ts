import { assertHostAccess } from './hostAccess'
/** Fetch-based SSE keeps the captured storage header on reconnects. */
/**
 * Core writes a heartbeat every 15s. A stream that stays silent for three of them is treated as
 * dead: a half-open connection (sleep/wake, a proxy or network leg dropping it without a FIN)
 * otherwise leaves read() waiting forever, and the window silently stops updating.
 */
export const STREAM_IDLE_TIMEOUT_MS = 45_000

export class ScopedEventSource {
  private readonly events = new EventTarget()
  readonly url: string
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
  private retryTimer: ReturnType<typeof setTimeout> | null = null
  constructor(url: string, private readonly storageId: string, private readonly fetchImpl: typeof fetch = fetch,
    private readonly idleTimeoutMs = STREAM_IDLE_TIMEOUT_MS) {
    this.url = url
    void this.connect()
  }
  close() { this.closed = true; this.readyState = 2; this.abort?.abort(); if (this.retryTimer) clearTimeout(this.retryTimer) }
  addEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | AddEventListenerOptions) { this.events.addEventListener(type, listener, options) }
  removeEventListener(type: string, listener: EventListenerOrEventListenerObject | null, options?: boolean | EventListenerOptions) { this.events.removeEventListener(type, listener, options) }
  dispatchEvent(event: Event) { return this.events.dispatchEvent(event) }
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
    const event = new MessageEvent(type, { data: data.join('\n'), lastEventId: this.lastFrameId })
    if (type === 'message') this.onmessage?.(event)
    this.dispatchEvent(event)
  }
  private applyCursor(value: string) {
    if (!value) return
    if (value.includes(',')) {
      this.watermarks.clear()
      for (const segment of value.split(',')) this.applyPair(segment)
      return
    }
    this.applyPair(value)
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
  private async connect() {
    if (this.closed) return
    const request = new AbortController()
    this.abort = request
    try {
      await assertHostAccess(this.url, request.signal)
      if (this.closed || request.signal.aborted) return
      // Native fetch is a Window method in Electron. Calling it as `this.fetchImpl(...)`
      // binds `this` to ScopedEventSource and throws "Illegal invocation", which the
      // reconnect catch below then swallowed forever. Keep the injected function, but
      // invoke it unbound so both the browser implementation and test doubles work.
      const fetchImpl = this.fetchImpl
      const cursor = this.encodeWatermarks()
      const response = await fetchImpl(this.url, { signal: request.signal, headers: {
        accept: 'text/event-stream', 'x-tinadec-storage-id': this.storageId,
        ...(cursor ? { 'last-event-id': cursor } : {}),
      } })
      if (this.closed || request.signal.aborted) { await response.body?.cancel(); return }
      // A rejected cursor must be dropped, not resent: retrying with the same unreadable value
      // would loop on 400 forever. Falling back to a full replay is the pre-cursor behaviour.
      if (response.status === 400) this.watermarks.clear()
      if (!response.ok || !response.body) throw new Error(`Event stream returned ${response.status}`)
      this.readyState = 1
      const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = ''
      // Any bytes (a frame, a comment, a heartbeat) prove the connection alive; silence past
      // the idle timeout aborts it and the retry below resumes from the last event id.
      let idle: ReturnType<typeof setTimeout> | null = null
      const arm = () => { if (idle) clearTimeout(idle); idle = setTimeout(() => request.abort(), this.idleTimeoutMs) }
      request.signal.addEventListener('abort', () => { if (idle) clearTimeout(idle) }, { once: true })
      arm()
      while (!this.closed && !request.signal.aborted) {
        const { done, value } = await reader.read()
        arm()
        if (this.closed || request.signal.aborted) break
        buffer = (buffer + decoder.decode(value, { stream: !done })).replace(/\r\n/g, '\n')
        let boundary: number
        while ((boundary = buffer.indexOf('\n\n')) >= 0) { this.dispatchBlock(buffer.slice(0, boundary)); buffer = buffer.slice(boundary + 2) }
        if (done) { if (buffer.trim()) this.dispatchBlock(buffer); break }
      }
      if (idle) clearTimeout(idle)
    } catch { if (!this.closed) this.onerror?.(new Event('error')) }
    if (!this.closed) { this.readyState = 0; this.retryTimer = setTimeout(() => void this.connect(), 1000) }
  }
}
