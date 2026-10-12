import { ref, watch, type Ref } from 'vue'
import { useHostAccess } from './hostAccess'
import { api, type EventEnvelope } from '@/api'
import { sessionStorageId, selectedStorageId } from './storageScope'

type Handler = (event: EventEnvelope) => unknown

/**
 * One session event stream per renderer window.
 *
 * The fan-out shape already existed — terminal widgets subscribe through
 * `HomeController.onEvent` precisely because the controller owns the connection — but
 * two other owners each kept their own `EventSource` against the same URL, so the main
 * window and every detached panel carried two live SSE connections and parsed each
 * frame twice. After this module, `api.connectEvents` has exactly one caller outside
 * the api module itself: here.
 *
 * The connection follows the session the window is showing (`followSession`), while
 * each subscriber may narrow delivery to the session it actually cares about. A
 * subscriber scoped to a session that is not open receives nothing, which is what the
 * per-session composable did before by not connecting at all.
 */
const followed = ref<string | null>(null)
const { canAccessBackend } = useHostAccess()

interface Subscription {
  handler: Handler
  scope?: Ref<string | null>
}

const subscriptions = new Set<Subscription>()
let source: EventSource | null = null
let paused = false
let sourceStorageId: string | null = null
let connection = 0
let snapshotRead = 0
let historyEpoch: number | null = null
let buffering = false
let buffered: EventEnvelope[] = []
const seen = new Set<string>()

function eventKey(event: EventEnvelope): string | null {
  if (event.run_id && event.seq > 0) return `${event.run_id}:${event.seq}`
  return typeof (event as unknown as Record<string, unknown>).event_id === 'string'
    ? String((event as unknown as Record<string, unknown>).event_id) : null
}

// Activity snapshots hydrate their own reducer. Only frames delivered through this
// bus enter `seen`: later snapshots cannot suppress pending message/status refreshes.

function deliver(event: EventEnvelope): Promise<void> {
  const outcomes: Promise<unknown>[] = []
  for (const subscription of [...subscriptions]) {
    if (subscription.scope) {
      const wanted = subscription.scope.value
      // An owner whose session is closed receives nothing, which is what it did before
      // by not connecting at all. Beyond that only frames that *name* a different
      // session are dropped: the durable journal also carries session-less rows, and
      // reading "unknown" as "not mine" would let one omission black out a panel.
      if (!wanted) continue
      if (event.session_id && event.session_id !== wanted) continue
    }
    try {
      const outcome = subscription.handler(event)
      // Handlers that reload state do it asynchronously; a rejection inside one of them
      // must not come back as an unhandled rejection on the window's only pipeline.
      if (outcome instanceof Promise) outcomes.push(outcome.catch(() => {}))
    } catch {
      // One broken subscriber must not take the window's only event pipeline with it.
    }
  }
  return Promise.all(outcomes).then(() => {})
}

function receive(event: EventEnvelope, owner: number) {
  if (owner !== connection) return
  if (event.type === 'stream.ready' || event.type === 'history.changed') {
    const epoch = event.payload?.['revert_epoch']
    if (event.type === 'history.changed' || (typeof epoch === 'number' && historyEpoch !== null && epoch !== historyEpoch)) { buffered = []; seen.clear() }
    if (typeof epoch === 'number') historyEpoch = epoch
    const read = ++snapshotRead
    buffering = true
    void deliver(event).finally(() => {
      if (owner !== connection || read !== snapshotRead) return
      buffering = false
      const pending = buffered; buffered = []
      for (const frame of pending) receive(frame, owner)
    })
    return
  }
  if (buffering) { buffered.push(event); return }
  const key = eventKey(event)
  if (key && seen.has(key)) return
  if (key) seen.add(key)
  void deliver(event)
}

function open() {
  close()
  if (subscriptions.size === 0 || paused || !canAccessBackend.value) return
  try {
    sourceStorageId = followed.value ? sessionStorageId(followed.value) : selectedStorageId()
    const owner = connection
    source = api.connectEvents(followed.value, event => receive(event, owner), { replay: false })
  } catch {
    // A stream that cannot start is a degraded live view, not a dead panel: the
    // REST reads that seeded it stay valid.
    source = null
  }
}

function close() {
  connection++; snapshotRead++; historyEpoch = null; buffering = false; buffered = []; seen.clear()
  source?.close()
  source = null
}

/** Point this window's stream at a session. Null is the unfiltered feed. */
export function followSession(sessionId: string | null): void {
  const storageId = sessionId ? sessionStorageId(sessionId) : selectedStorageId()
  if (followed.value === sessionId && sourceStorageId === storageId && !paused) return
  paused = false
  followed.value = sessionId
  open()
}

/** A transfer closes its source feed while Core acquires exclusive scope leases. */
export function suspendFollowingSession(sessionId: string): void {
  if (followed.value !== sessionId) return
  paused = true
  close()
}

/** Release this window's scope lease before a host close; resume only on failure. */
export function suspendFollowingStorage(storageId: string): () => void {
  if (!source || sourceStorageId !== storageId) return () => {}
  const session = followed.value
  paused = true
  close()
  return () => { if (paused && followed.value === session) { paused = false; open() } }
}

/**
 * Receive session events. Without `scope` the subscriber sees every frame the
 * connection carries, including the unfiltered feed before a session exists.
 */
export function subscribeToSessionEvents(
  handler: Handler,
  options: { scope?: Ref<string | null> } = {},
): () => void {
  const subscription: Subscription = { handler, scope: options.scope }
  subscriptions.add(subscription)
  if (!source) open()
  return () => {
    subscriptions.delete(subscription)
    if (subscriptions.size === 0) close()
  }
}

watch(canAccessBackend, ready => { if (ready && !paused) open(); else if (!ready) close() })
