import { onMounted, onUnmounted, ref, watch } from 'vue'
import { api, type OrchestrationRunDto, type SessionDto } from '@/api'
import { scopedApi, selectedStorage, selectionKey } from '@/lib/storageScope'

export interface ActivityCandidate {
  session: SessionDto
  /** Workspace the conversation lives in: the card names it instead of drawing an icon. */
  projectName: string
}

/** The 最近活动 block is five cards; the wider views decide for themselves what they read. */
export const ACTIVITY_LIMIT = 5
/** A move in the session list (rename, reorder, a run landing) should not fire a burst of reads. */
export const ACTIVITY_DEBOUNCE_MS = 300
/** Safety net for clock skew and long live runs: a read older than this is refreshed anyway. */
export const ACTIVITY_TTL_MS = 60_000
/** 所有活动 reads every conversation, so the fan-out runs in batches instead of all at once. */
export const ACTIVITY_CONCURRENCY = 8

/** Newest run of a roster; the roster order is Core's, so it is ranked here rather than trusted. */
export function latestRun(runs: OrchestrationRunDto[]): OrchestrationRunDto | null {
  let latest: OrchestrationRunDto | null = null
  for (const run of runs) {
    if (!latest || Date.parse(run.created_at) >= Date.parse(latest.created_at)) latest = run
  }
  return latest
}

interface Reading {
  readAt: number
  /** The `updated_at` this read answered for; a different one means the session moved. */
  updatedAt: string
}

/**
 * The latest run of each conversation the activity views render, keyed by `selectionKey`.
 *
 * There is no cross-session activity endpoint, so this walks `sessions/{id}/runs` for exactly the
 * conversations it is handed: the caller decides how wide the view is (five cards, every
 * conversation, every project) and this reads no more than that. Three rules keep it cheap: every
 * activity view being off means no request at all, a read is reused until the session's
 * `updated_at` moves, and a TTL covers the cases where the clock and the server disagree.
 */
export function useRecentActivity(candidates: () => ActivityCandidate[], options: {
  enabled: () => boolean
  /** Optional hardening for callers that hand over an unbounded roster. */
  limit?: () => number
}) {
  const runs = ref<Record<string, OrchestrationRunDto | null>>({})
  const readings = ref<Record<string, Reading>>({})
  let generation = 0
  let controller: AbortController | null = null
  let timer: ReturnType<typeof setTimeout> | null = null

  function targets(): ActivityCandidate[] {
    if (!options.enabled()) return []
    const rows = candidates()
    const limit = options.limit?.()
    return limit === undefined ? rows : rows.slice(0, limit)
  }

  function stale(candidate: ActivityCandidate): boolean {
    const key = selectionKey(candidate.session)
    if (!(key in runs.value)) return true
    const reading = readings.value[key]
    if (!reading) return true
    if (reading.updatedAt !== candidate.session.updated_at) return true
    return Date.now() - reading.readAt > ACTIVITY_TTL_MS
  }

  async function refresh(): Promise<void> {
    const wanted = targets().filter(stale)
    if (wanted.length === 0) return
    const current = ++generation
    controller?.abort()
    controller = new AbortController()
    const { signal } = controller
    // Pin the fallback storage for the whole fan-out while honoring each session's own storage_id.
    const storage = selectedStorage.value
    type Result = PromiseSettledResult<{ key: string; updatedAt: string; list: OrchestrationRunDto[] }>
    const results: Result[] = []
    // A wide view (所有活动) can hand over every conversation; batching keeps the gateway from
    // receiving one burst of identical requests and lets an abandoned run stop early.
    for (let index = 0; index < wanted.length; index += ACTIVITY_CONCURRENCY) {
      const batch = wanted.slice(index, index + ACTIVITY_CONCURRENCY)
      results.push(...await Promise.allSettled(batch.map(row =>
        scopedApi(api, () => row.session.storage_id || storage).listRuns(row.session.id, signal)
          .then(list => ({ key: selectionKey(row.session), updatedAt: row.session.updated_at, list })))))
      if (current !== generation) return
    }
    if (storage !== selectedStorage.value) return
    const nextRuns = { ...runs.value }
    const nextReadings = { ...readings.value }
    const readAt = Date.now()
    for (const result of results) {
      // One unreadable roster must not blank the others; that session keeps its previous read.
      if (result.status !== 'fulfilled') continue
      const { key, updatedAt, list } = result.value
      nextRuns[key] = latestRun(list)
      nextReadings[key] = { readAt, updatedAt }
    }
    runs.value = nextRuns
    readings.value = nextReadings
  }

  function schedule(): void {
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => { timer = null; void refresh() }, ACTIVITY_DEBOUNCE_MS)
  }

  // Registered on mount, not in setup: the candidate getter walks the owner's computeds, and
  // reading them before the component is on screen is how a setup-order bug turns into a crash.
  onMounted(() => {
    watch(
      () => targets().map(row => `${selectionKey(row.session)}@${row.session.updated_at}`).join('|'),
      schedule,
    )
  })

  onUnmounted(() => {
    if (timer) clearTimeout(timer)
    controller?.abort()
    generation += 1
  })

  return { runs, refresh, schedule }
}
