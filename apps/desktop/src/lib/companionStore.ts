import { computed, onBeforeUnmount, onMounted, ref, watch, type ComputedRef, type Ref } from 'vue'
import {
  companionApi,
  companionStreamSupported,
  subscribeCompanionFeed,
  type CompanionActivity,
  type CompanionConnectionOverview,
  type CompanionConversation,
  type CompanionSettings,
  type CompanionSummary,
} from './companionApi'
import { useHostAccess } from './hostAccess'
import type { CompanionEventNotice } from '@/events/companionEventTypes'
import type { CompanionFeedFilter } from './companionView'

const { canAccessBackend } = useHostAccess()

/**
 * One snapshot of the companion domain for the whole centre.
 *
 * The four centre cards used to fetch for themselves: three of them asked `listCompanions`
 * separately and the group card asked once per companion, so a single refresh cost `4 + N`
 * requests and the panels could show different generations of the same data. This store owns a
 * single poll, hands every card the same snapshot, and holds the selection that the cards share.
 *
 * The refresh is now driven by `GET /api/v1/companion-events` rather than a 10s full sweep: the
 * server names the fact that changed, and only the sources that fact can touch are reread. The
 * fallback timer below exists for the one failure the stream cannot report on its own.
 *
 * Module-level on purpose, like `homeController`: the centre is one page, and a second instance
 * of this state would be a second answer to "which companion is selected".
 */

/**
 * The fallback cadence, not the refresh cadence.
 *
 * A live stream that dies silently — a half-open socket, a proxy dropping the body without a FIN,
 * a reconnect loop that keeps failing — never fires an error anyone sees, and the panels would
 * then show yesterday's snapshot forever with no indication anything stopped. One slow full read
 * bounds how stale the centre can look, and it is cheap enough that nobody notices it.
 */
export const COMPANION_CENTER_FALLBACK_MS = 60_000
/** Bursts of journal rows (a run transition writes several) coalesce into one reread. */
const NOTICE_DEBOUNCE_MS = 250

export type CompanionSource = 'companions' | 'activity' | 'conversations' | 'connections' | 'settings'

export const companions: Ref<CompanionSummary[]> = ref([])
export const activity: Ref<CompanionActivity[]> = ref([])
export const conversations: Ref<CompanionConversation[]> = ref([])
export const connections: Ref<CompanionConnectionOverview[]> = ref([])
/** User-wide companion settings (main companion, sidebar buddy, global pause). */
export const settings: Ref<CompanionSettings | null> = ref(null)
/** Selected companion id; the feed and the two right-hand cards narrow to it. */
export const selectedCompanionId: Ref<string | null> = ref(null)
export const loading = ref(true)
/** Per-source failure, so one degraded panel never claims the whole centre failed. */
export const failures = ref<Partial<Record<CompanionSource, unknown>>>({})
/** Local clock of the last applied snapshot; null until the first one lands. */
export const loadedAt = ref<Date | null>(null)
/**
 * The feed's filter lives here because two surfaces drive it: the chips inside the feed card and
 * the companion nav in the sidebar. A card-local ref could only answer one of them.
 */
export const feedFilter: Ref<CompanionFeedFilter> = ref('all')

let subscribers = 0
let timer: ReturnType<typeof setTimeout> | null = null
let noticeTimer: ReturnType<typeof setTimeout> | null = null
let pendingSources: Set<CompanionSource> | null = null
/** Notices that arrived while a round was already running. */
const queuedSources = new Set<CompanionSource>()
let unsubscribeFeed: (() => void) | null = null
let inflight: Promise<void> | null = null
let generation = 0
let stopped = false

function visible(): boolean {
  return typeof document === 'undefined' || document.visibilityState === 'visible'
}

function apply<T>(source: CompanionSource, value: T[] | null, target: Ref<T[]>): void {
  if (value === null) return
  target.value = value
  const next = { ...failures.value }
  delete next[source]
  failures.value = next
}

function fail(source: CompanionSource, reason: unknown): void {
  failures.value = { ...failures.value, [source]: reason }
}

/** Single-flight: four cards mounting together still cause exactly one round of reads. */
export async function refreshCompanionCenter(): Promise<void> {
  return refreshCompanionSources(null)
}

/**
 * Reread the named sources only. A journal notice says *which* fact changed, so rereading the
 * other four would spend four requests to learn nothing — and the stream makes that difference
 * visible for the first time.
 */
async function refreshCompanionSources(only: CompanionSource[] | null): Promise<void> {
  if (inflight) {
    // A notice that arrives while a round is running must not be swallowed by single-flight:
    // that round may not even have asked for the source this notice named. Queue it and run one
    // follow-up round instead of waiting for the 60s fallback to notice.
    if (only) for (const source of only) queuedSources.add(source)
    return inflight
  }
  const wants = (source: CompanionSource) => !only || only.includes(source)
  const token = ++generation
  inflight = (async () => {
    const [companionPage, activityPage, conversationPage, connectionPage, settingsResult] = await Promise.allSettled([
      wants('companions') ? companionApi.listCompanions() : Promise.resolve(null),
      wants('activity') ? companionApi.listActivity(100) : Promise.resolve(null),
      wants('conversations') ? companionApi.listAllConversations({ limit: 100 }) : Promise.resolve(null),
      wants('connections') ? companionApi.listConnectionsOverview() : Promise.resolve(null),
      wants('settings') ? companionApi.getSettings() : Promise.resolve(null),
    ])
    if (token !== generation) return
    if (companionPage.status === 'fulfilled') apply('companions', companionPage.value, companions)
    else fail('companions', companionPage.reason)
    if (activityPage.status === 'fulfilled') apply('activity', activityPage.value, activity)
    else fail('activity', activityPage.reason)
    if (conversationPage.status === 'fulfilled') apply('conversations', conversationPage.value, conversations)
    else fail('conversations', conversationPage.reason)
    if (connectionPage.status === 'fulfilled') apply('connections', connectionPage.value, connections)
    else fail('connections', connectionPage.reason)
    if (wants('settings')) {
      if (settingsResult.status === 'fulfilled') {
        if (settingsResult.value) settings.value = settingsResult.value
        const next = { ...failures.value }
        delete next.settings
        failures.value = next
      } else fail('settings', settingsResult.reason)
    }
    if (token !== generation) return
    // A selected companion that no longer exists (archived elsewhere) must not stay selected:
    // the cards would filter to nothing and look broken.
    if (companionPage.status === 'fulfilled' && companionPage.value && selectedCompanionId.value
      && !companionPage.value.some((item) => item.id === selectedCompanionId.value)) {
      selectedCompanionId.value = null
    }
    // `null` is "not requested in this round", never "Core returned nothing": counting it would
    // move `loadedAt` forward on a round that read nothing.
    const applied = [companionPage, activityPage, conversationPage, connectionPage, settingsResult]
      .some((result) => result.status === 'fulfilled' && result.value !== null)
    if (applied) loadedAt.value = new Date()
    loading.value = false
  })().finally(() => {
    inflight = null
    // A notice queued during the round gets its own round; without this it would wait for the
    // fallback sweep, which is the very delay the stream exists to remove.
    if (queuedSources.size === 0) return
    const queued = [...queuedSources]
    queuedSources.clear()
    void refreshCompanionSources(queued)
  })
  return inflight
}

/**
 * Which sources a notice can have changed. A settings row is its own source; every other journal
 * row (a message, a group change, a run transition) can move the activity feed, and the ones that
 * name a conversation can also change that conversation's row. Companions themselves are only
 * reread when nothing narrower explains the row, because the summary carries the live run state.
 */
function sourcesFor(notice: CompanionEventNotice): CompanionSource[] {
  if (notice.type === 'companion.settings') return ['settings', 'companions']
  const sources: CompanionSource[] = ['activity']
  if (notice.conversationId) sources.push('conversations')
  if (notice.type === 'companion.run' || notice.kind === 'message_sent' || notice.kind?.startsWith('autonomy_')) {
    sources.push('companions')
  }
  return sources
}

/**
 * Apply one journal row. The notice says *what* changed, never *what it says* — the authoritative
 * list is still read from Core, so a lost frame can never leave a half-applied row on screen.
 * Bursts coalesce: a single run transition writes `run_started` and later `run_completed`, and a
 * group invite writes two rows in one transaction.
 */
function onNotice(notice: CompanionEventNotice): void {
  if (stopped || subscribers === 0) return
  pendingSources = new Set([...(pendingSources ?? []), ...sourcesFor(notice)])
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => {
    noticeTimer = null
    const only = pendingSources ? [...pendingSources] : []
    pendingSources = null
    if (stopped || !visible()) return
    void refreshCompanionSources(only)
  }, NOTICE_DEBOUNCE_MS)
}

/**
 * The low-frequency safety net.
 *
 * It is deliberately not the refresh mechanism: events do that. It exists so that a stream which
 * stops delivering without ever reporting an error — a half-open socket, a proxy that keeps the
 * connection open but silent, a reconnect that keeps failing — cannot leave the centre frozen on
 * an old snapshot with nothing on screen saying so.
 */
function schedule(): void {
  if (stopped || subscribers === 0) return
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => {
    timer = null
    if (!stopped && visible()) void refreshCompanionCenter()
    schedule()
  }, COMPANION_CENTER_FALLBACK_MS)
}

function onVisibilityChange(): void {
  if (!visible() || subscribers === 0) return
  // Coming back from hidden is exactly when the stream was closed, so reread before waiting for
  // the resumed subscription to replay: the panels should never look empty while that happens.
  void refreshCompanionCenter()
  schedule()
}

function startFeed(): void {
  if (unsubscribeFeed || !companionStreamSupported() || !canAccessBackend.value) return
  unsubscribeFeed = subscribeCompanionFeed(onNotice)
}

function stopFeed(): void {
  // Only the pending refresh is dropped. The subscription itself outlives the centre: the main
  // window keeps relaying journal rows to the satellite companion window while the user is on any
  // other page, so tearing the stream down on unmount would silence that window too.
  if (noticeTimer) clearTimeout(noticeTimer)
  noticeTimer = null
  pendingSources = null
}

function disposeFeed(): void {
  stopFeed()
  unsubscribeFeed?.()
  unsubscribeFeed = null
}

/**
 * Only the trusted main window owns the app-wide subscription, because only it can also relay the
 * rows to the satellite companion window (which is not a trusted host and cannot subscribe itself).
 * A detached panel that shows companion data subscribes for itself through `useCompanionCenter`,
 * so it is not opened twice.
 */
function isMainWindow(): boolean {
  if (typeof window === 'undefined') return false
  const path = window.location.hash.split('?')[0]
  return path !== '#/companion' && path !== '#/pet' && path !== '#/panel'
}

if (isMainWindow()) {
  // Follow the host gate instead of assuming access at import time: the main window publishes
  // `checking` before any page's setup runs, and a subscription opened then is refused. A later
  // revocation closes the source; regained access reopens it from the cursor the stream kept.
  watch(canAccessBackend, (ready) => {
    if (ready) { startFeed(); return }
    unsubscribeFeed?.()
    unsubscribeFeed = null
  }, { immediate: true })
}

export function selectCompanion(id: string | null): void {
  selectedCompanionId.value = selectedCompanionId.value === id ? null : id
}

export function setFeedFilter(next: CompanionFeedFilter): void {
  feedFilter.value = next
}

export const selectedCompanion: ComputedRef<CompanionSummary | null> = computed(() =>
  companions.value.find((item) => item.id === selectedCompanionId.value) ?? null)

export const activeCompanions: ComputedRef<CompanionSummary[]> = computed(() =>
  companions.value.filter((item) => item.status !== 'archived'))

/** The sidebar buddy: the companion designated by settings, if it still exists and is active. */
export const buddyCompanion: ComputedRef<CompanionSummary | null> = computed(() => {
  const id = settings.value?.buddy_companion_id
  if (!id) return null
  return companions.value.find((item) => item.id === id && item.status !== 'archived') ?? null
})

/** Groups only; private chats belong to the companion window, not to the centre's list. */
export const groupConversations: ComputedRef<CompanionConversation[]> = computed(() =>
  conversations.value.filter((item) => item.kind === 'group'))

/**
 * Everything the centre needs to subscribe and to release. Each card mounts its own subscription;
 * the event feed and the fallback timer exist while at least one is alive and stop when the page
 * is gone, so a closed centre costs nothing.
 */
export function useCompanionCenter() {
  onMounted(() => {
    subscribers++
    if (subscribers === 1) {
      loading.value = loading.value && loadedAt.value === null
      // The first paint is still a full read: the stream replays the journal from cursor 0, but
      // the cards need a snapshot now, not after the replay finishes frame by frame.
      void refreshCompanionCenter()
      startFeed()
      schedule()
      if (typeof document !== 'undefined') document.addEventListener('visibilitychange', onVisibilityChange)
    }
  })
  onBeforeUnmount(() => {
    subscribers = Math.max(0, subscribers - 1)
    if (subscribers > 0) return
    stopped = true
    generation++
    stopFeed()
    if (timer) clearTimeout(timer)
    timer = null
    if (typeof document !== 'undefined') document.removeEventListener('visibilitychange', onVisibilityChange)
    stopped = false
  })
  return {
    companions,
    activity,
    conversations,
    connections,
    settings,
    buddyCompanion,
    selectedCompanionId,
    selectedCompanion,
    feedFilter,
    loading,
    failures,
    loadedAt,
    refresh: refreshCompanionCenter,
  }
}

/** Test seam: drop every cached snapshot and stop the stream and the fallback timer. */
export function resetCompanionStore(): void {
  companions.value = []
  activity.value = []
  conversations.value = []
  connections.value = []
  settings.value = null
  selectedCompanionId.value = null
  loading.value = true
  failures.value = {}
  loadedAt.value = null
  feedFilter.value = 'all'
  subscribers = 0
  generation++
  disposeFeed()
  if (timer) clearTimeout(timer)
  timer = null
  inflight = null
  queuedSources.clear()
}
