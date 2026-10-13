import { onScopeDispose, ref, shallowRef, watch, type Ref } from 'vue'
import type { EventEnvelope } from '@/api'
import { subscribeToSessionEvents } from '@/lib/sessionEventBus'
import { applyEvents, createFeedState, type FeedState } from '@/lib/conversation/reducer'

/**
 * The conversation feed's live model of a session, fed only by the window's single session
 * SSE connection (`sessionEventBus`); it never fetches REST per event.
 *
 * Events are queued and applied once per animation frame. During the initial durable
 * replay nothing is published: the whole history is applied in one batch when Core's
 * post-replay heartbeat arrives (or after `hydrationTimeoutMs`, for a stream that never
 * sends one), so opening a long session costs one render instead of one per event.
 * Switching sessions starts a new state and drops anything queued for the old one.
 *
 * Readers depend on `revision` and re-derive a run only when its `version` changed.
 */
export interface ConversationFeed {
  state: Readonly<Ref<FeedState>>
  /** Bumped once per published batch. */
  revision: Readonly<Ref<number>>
  /** False until the session's history has been applied. */
  hydrated: Readonly<Ref<boolean>>
}

export interface ConversationFeedOptions {
  schedule?: (callback: () => void) => unknown
  hydrationTimeoutMs?: number
}

const frame = (callback: () => void) =>
  typeof requestAnimationFrame === 'function' ? requestAnimationFrame(() => callback()) : setTimeout(callback, 16)

export function useConversationFeed(sessionId: Ref<string | null>, options: ConversationFeedOptions = {}): ConversationFeed {
  const schedule = options.schedule ?? frame
  const timeoutMs = options.hydrationTimeoutMs ?? 2000
  const state = shallowRef<FeedState>(createFeedState())
  const revision = ref(0)
  const hydrated = ref(false)
  let pending: EventEnvelope[] = []
  let scheduled = false
  let epoch = 0
  let hydrationTimer: ReturnType<typeof setTimeout> | null = null

  function publish() {
    const batch = pending
    pending = []
    if (!batch.length) return
    if (applyEvents(state.value, batch).size) revision.value += 1
  }

  function finishHydration() {
    if (hydrated.value) return
    if (hydrationTimer) clearTimeout(hydrationTimer)
    hydrationTimer = null
    publish()
    hydrated.value = true
    revision.value += 1
  }

  function enqueue(event: EventEnvelope) {
    const session = sessionId.value
    if (!session || (event.session_id && event.session_id !== session)) return
    pending.push(event)
    if (!hydrated.value || scheduled) return
    scheduled = true
    const queuedIn = epoch
    schedule(() => {
      scheduled = false
      // A batch queued for a session the window has left is dropped, not applied to the new one.
      if (queuedIn === epoch) publish()
    })
  }

  function reset() {
    epoch += 1
    pending = []
    scheduled = false
    state.value = createFeedState()
    hydrated.value = false
    revision.value += 1
    if (hydrationTimer) clearTimeout(hydrationTimer)
    hydrationTimer = sessionId.value ? setTimeout(finishHydration, timeoutMs) : null
  }

  watch(sessionId, reset, { immediate: true })
  const unsubscribe = subscribeToSessionEvents(enqueue, { scope: sessionId, onHeartbeat: finishHydration })
  onScopeDispose(() => {
    unsubscribe()
    if (hydrationTimer) clearTimeout(hydrationTimer)
  })

  return { state, revision, hydrated }
}
