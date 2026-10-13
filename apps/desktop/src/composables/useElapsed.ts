import { computed, onScopeDispose, ref, type ComputedRef, type Ref } from 'vue'

/**
 * Live elapsed time for running steps ("12s"). Every caller shares one 1-second ticker that
 * runs only while something live is subscribed, so a feed with many running rows still
 * wakes once per second.
 */
const now = ref(Date.now())
let subscribers = 0
let ticker: ReturnType<typeof setInterval> | null = null

function subscribe() {
  subscribers += 1
  if (!ticker) {
    now.value = Date.now()
    ticker = setInterval(() => { now.value = Date.now() }, 1000)
  }
}

function unsubscribe() {
  subscribers = Math.max(0, subscribers - 1)
  if (subscribers === 0 && ticker) {
    clearInterval(ticker)
    ticker = null
  }
}

/** Milliseconds from `start` to `end`, or to now while `end` is empty. Null without a start. */
export function useElapsed(start: Ref<string | null | undefined>, end: Ref<string | null | undefined>): ComputedRef<number | null> {
  subscribe()
  onScopeDispose(unsubscribe)
  return computed(() => {
    const from = start.value ? Date.parse(start.value) : Number.NaN
    if (!Number.isFinite(from)) return null
    const to = end.value ? Date.parse(end.value) : now.value
    return Number.isFinite(to) ? Math.max(0, to - from) : null
  })
}

/** For tests: how many live subscribers keep the shared ticker running. */
export function elapsedSubscriberCount(): number {
  return subscribers
}
