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
  onHeartbeat?: () => void
}

const subscriptions = new Set<Subscription>()
let source: EventSource | null = null
let paused = false
let sourceStorageId: string | null = null

function deliver(event: EventEnvelope) {
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
      if (outcome instanceof Promise) outcome.catch(() => {})
    } catch {
      // One broken subscriber must not take the window's only event pipeline with it.
    }
  }
}

/**
 * Core sends a heartbeat right after the initial durable replay (and every 15s after), so
 * the first one after a (re)connect tells a subscriber that history has been delivered.
 */
function heartbeat() {
  for (const subscription of [...subscriptions]) {
    if (subscription.scope && !subscription.scope.value) continue
    try {
      subscription.onHeartbeat?.()
    } catch {
      // Same isolation as deliver().
    }
  }
}

function open() {
  close()
  if (subscriptions.size === 0 || paused || !canAccessBackend.value) return
  try {
    sourceStorageId = followed.value ? sessionStorageId(followed.value) : selectedStorageId()
    source = api.connectEvents(followed.value, deliver, heartbeat)
  } catch {
    // A stream that cannot start is a degraded live view, not a dead panel: the
    // REST reads that seeded it stay valid.
    source = null
  }
}

function close() {
  source?.close()
  source = null
}

/** Point this window's stream at a session. Null is the unfiltered feed. */
export function followSession(sessionId: string | null): void {
  if (followed.value === sessionId && !paused) return
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
  options: { scope?: Ref<string | null>; onHeartbeat?: () => void } = {},
): () => void {
  const subscription: Subscription = { handler, scope: options.scope, onHeartbeat: options.onHeartbeat }
  subscriptions.add(subscription)
  if (!source) open()
  return () => {
    subscriptions.delete(subscription)
    if (subscriptions.size === 0) close()
  }
}

watch(canAccessBackend, ready => { if (ready && !paused) open(); else if (!ready) close() })
