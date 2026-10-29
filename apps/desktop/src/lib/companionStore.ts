import { computed, onBeforeUnmount, onMounted, ref, type ComputedRef, type Ref } from 'vue'
import {
  companionApi,
  type CompanionActivity,
  type CompanionConnectionOverview,
  type CompanionConversation,
  type CompanionSummary,
} from './companionApi'
import type { CompanionFeedFilter } from './companionView'

/**
 * One snapshot of the companion domain for the whole centre.
 *
 * The four centre cards used to fetch for themselves: three of them asked `listCompanions`
 * separately and the group card asked once per companion, so a single refresh cost `4 + N`
 * requests and the panels could show different generations of the same data. This store owns a
 * single poll, hands every card the same snapshot, and holds the selection that the cards share.
 *
 * Module-level on purpose, like `homeController`: the centre is one page, and a second instance
 * of this state would be a second answer to "which companion is selected".
 */

export const COMPANION_CENTER_POLL_MS = 10_000

export type CompanionSource = 'companions' | 'activity' | 'conversations' | 'connections'

export const companions: Ref<CompanionSummary[]> = ref([])
export const activity: Ref<CompanionActivity[]> = ref([])
export const conversations: Ref<CompanionConversation[]> = ref([])
export const connections: Ref<CompanionConnectionOverview[]> = ref([])
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
  if (inflight) return inflight
  const token = ++generation
  inflight = (async () => {
    const [companionPage, activityPage, conversationPage, connectionPage] = await Promise.allSettled([
      companionApi.listCompanions(),
      companionApi.listActivity(100),
      companionApi.listAllConversations({ limit: 100 }),
      companionApi.listConnectionsOverview(),
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
    if (token !== generation) return
    // A selected companion that no longer exists (archived elsewhere) must not stay selected:
    // the cards would filter to nothing and look broken.
    if (companionPage.status === 'fulfilled' && selectedCompanionId.value
      && !companionPage.value.some((item) => item.id === selectedCompanionId.value)) {
      selectedCompanionId.value = null
    }
    if (companionPage.status === 'fulfilled' || activityPage.status === 'fulfilled'
      || conversationPage.status === 'fulfilled' || connectionPage.status === 'fulfilled') {
      loadedAt.value = new Date()
    }
    loading.value = false
  })().finally(() => { inflight = null })
  return inflight
}

function schedule(): void {
  if (stopped || subscribers === 0) return
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => {
    timer = null
    if (!stopped && visible()) void refreshCompanionCenter()
    schedule()
  }, COMPANION_CENTER_POLL_MS)
}

function onVisibilityChange(): void {
  if (!visible() || subscribers === 0) return
  void refreshCompanionCenter()
  schedule()
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

/** Groups only; private chats belong to the companion window, not to the centre's list. */
export const groupConversations: ComputedRef<CompanionConversation[]> = computed(() =>
  conversations.value.filter((item) => item.kind === 'group'))

/**
 * Everything the centre needs to poll and to release. Each card mounts its own subscription; the
 * timer exists while at least one is alive and stops when the page is gone, so a closed centre
 * costs nothing.
 */
export function useCompanionCenter() {
  onMounted(() => {
    subscribers++
    if (subscribers === 1) {
      loading.value = loading.value && loadedAt.value === null
      void refreshCompanionCenter()
      schedule()
      if (typeof document !== 'undefined') document.addEventListener('visibilitychange', onVisibilityChange)
    }
  })
  onBeforeUnmount(() => {
    subscribers = Math.max(0, subscribers - 1)
    if (subscribers > 0) return
    stopped = true
    generation++
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
    selectedCompanionId,
    selectedCompanion,
    feedFilter,
    loading,
    failures,
    loadedAt,
    refresh: refreshCompanionCenter,
  }
}

/** Test seam: drop every cached snapshot and stop the timer. */
export function resetCompanionStore(): void {
  companions.value = []
  activity.value = []
  conversations.value = []
  connections.value = []
  selectedCompanionId.value = null
  loading.value = true
  failures.value = {}
  loadedAt.value = null
  feedFilter.value = 'all'
  subscribers = 0
  generation++
  if (timer) clearTimeout(timer)
  timer = null
  inflight = null
}
