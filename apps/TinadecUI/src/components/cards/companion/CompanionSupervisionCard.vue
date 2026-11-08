<script setup lang="ts">
/**
 * The supervision board: what every session a companion is involved with is doing right now.
 *
 * Two sources feed it. A session partner window means the user is watching that session from
 * outside the app; a `companion_dispatched` activity means a companion handed work to it. A
 * session with neither is not listed — the sidebar is already a session list, and repeating it
 * here would turn a supervision surface into a second one.
 *
 * Activity is event-driven through the companion store. Only run/approval status is polled,
 * because those are runtime facts the activity journal does not rewrite on every transition.
 */
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertCircle, CheckCircle2, Loader2, MonitorSmartphone } from '@lucide/vue'
import { api, type OrchestrationRunDto, type SessionDto } from '@/api'
import { activity } from '@/lib/companionStore'
import { activeSessionPartners } from '@/lib/sessionPartner'
import { useHostAccess } from '@/lib/hostAccess'

const { t } = useI18n()
const { canAccessBackend } = useHostAccess()

type PartnerState = 'needs-approval' | 'working' | 'done' | 'idle' | 'unknown'

/** A row can come from either source or both; the tags tell the user which. */
interface RowSources {
  partner: boolean
  dispatch: boolean
}

interface PartnerRow {
  sessionId: string
  title: string
  state: PartnerState
  detail: string
  sources: RowSources
  /** The session read succeeded but did not contain this id, so the session is gone. */
  unknown: boolean
}

interface DispatchRef {
  sessionId: string
  runId: string | null
  title: string | null
  at: string
}

const rows = ref<PartnerRow[]>([])
const failure = ref('')
let timer: ReturnType<typeof setTimeout> | null = null
let disposed = false
let generation = 0
/** Session titles, re-read only when the supervised set changes (see refresh). */
let titleCache: Map<string, string> | null = null
let titleCacheRead = false
let titleCacheKey = ''

/**
 * Sessions a companion has dispatched to, newest record per session.
 *
 * The feed arrives newest-first today, but the board must not depend on that ordering: the
 * timestamp comparison keeps the latest dispatch whichever direction Core uses.
 */
/**
 * How long a dispatch keeps a session on the board.
 *
 * A dispatch is an event, not a state: once the run it started is finished there is nothing left
 * to supervise, and the activity feed never rewrites the row to say so. Without this window the
 * board would keep listing Monday's dispatch on Friday (as "done") and never return to empty.
 * A session with a partner window stays regardless — that one is a live state.
 */
const DISPATCH_VISIBLE_MS = 6 * 60 * 60 * 1000

const dispatched = computed(() => {
  const map = new Map<string, DispatchRef>()
  const cutoff = Date.now() - DISPATCH_VISIBLE_MS
  for (const item of activity.value) {
    if (item.kind !== 'companion_dispatched') continue
    const at = typeof item.created_at === 'string' ? Date.parse(item.created_at) : Number.NaN
    if (Number.isFinite(at) && at < cutoff) continue
    const payload = item.payload ?? {}
    const sessionId = typeof payload.session_id === 'string' ? payload.session_id : ''
    if (!sessionId) continue
    const next: DispatchRef = {
      sessionId,
      runId: typeof payload.run_id === 'string' && payload.run_id ? payload.run_id : null,
      title: typeof payload.session_title === 'string' && payload.session_title.trim() ? payload.session_title : null,
      at: typeof item.created_at === 'string' ? item.created_at : '',
    }
    const current = map.get(sessionId)
    if (!current || next.at >= current.at) map.set(sessionId, next)
  }
  return map
})

/**
 * Only a change in the dispatched set is worth rereading; other activity kinds (messages,
 * run completions) are the feed's business, not this board's.
 */
const dispatchSignature = computed(() =>
  [...dispatched.value.values()]
    .map((item) => `${item.sessionId}:${item.runId ?? ''}`)
    .sort()
    .join('|'),
)

/** A run still moving is "working"; the newest terminal run is "done"; no run at all is "idle". */
function stateOf(
  runs: OrchestrationRunDto[],
  pendingApprovals: number,
  dispatchedRunId: string | null,
): { state: PartnerState; detail: string } {
  if (pendingApprovals > 0) return { state: 'needs-approval', detail: String(pendingApprovals) }
  const latest = runs[0]
  if (!latest) {
    // A dispatch record names a run this read cannot see. Calling the session idle would
    // contradict the record; unknown is the honest answer.
    if (dispatchedRunId) return { state: 'unknown', detail: '' }
    return { state: 'idle', detail: '' }
  }
  if (latest.status === 'completed') return { state: 'done', detail: latest.summary || '' }
  if (latest.status === 'failed' || latest.status === 'cancelled') return { state: 'unknown', detail: latest.summary || latest.status }
  return { state: 'working', detail: latest.summary || latest.status }
}

/**
 * The title for one row, and whether the session still exists.
 *
 * A failed session read is not a deleted session. When the read failed, the title recorded at
 * dispatch time is the only real fact available; when the read succeeded and the id is absent,
 * the session is gone and the id prefix is the only honest label — never the dispatch-time title.
 */
function resolveTitle(
  sessionId: string,
  titleById: Map<string, string>,
  sessionsRead: boolean,
  dispatchTitle: string | null,
): { title: string; unknown: boolean } {
  const live = titleById.get(sessionId)
  if (live && live.trim()) return { title: live, unknown: false }
  if (sessionsRead && !titleById.has(sessionId)) return { title: sessionId.slice(0, 8), unknown: true }
  if (!sessionsRead && dispatchTitle) return { title: dispatchTitle, unknown: false }
  return { title: sessionId.slice(0, 8), unknown: false }
}

async function refresh(): Promise<void> {
  if (!canAccessBackend.value) return
  const token = ++generation
  const partnerIds = activeSessionPartners.value
  const dispatchById = dispatched.value
  const ids = new Set<string>([...partnerIds, ...dispatchById.keys()])
  if (ids.size === 0) {
    rows.value = []
    failure.value = ''
    return
  }
  try {
    // The titles come from one session read rather than from the work page's controller: this
    // card must stand on its own when the centre is opened without Home ever having started.
    // `null` means the read failed; `[]` means it succeeded and found nothing. The difference
    // decides whether a missing id is "deleted" or merely unverified.
    //
    // The read is cached until the set of supervised sessions changes. This runs every 10 seconds
    // for run status, and re-reading the whole session roster each tick would be six list reads a
    // minute for titles that only change when a session is created, renamed or removed.
    const sessionIdsKey = [...ids].sort().join('|')
    if (!titleCache || sessionIdsKey !== titleCacheKey) {
      const sessions: SessionDto[] | null = await api.listSessions().catch(() => null)
      titleCache = new Map((sessions ?? []).map((item) => [item.id, item.title]))
      titleCacheRead = sessions !== null
      titleCacheKey = sessionIdsKey
    }
    const titleById: Map<string, string> = titleCache ?? new Map()
    const sessionsRead = titleCache !== null && titleCacheRead
    const next: PartnerRow[] = []
    for (const sessionId of ids) {
      const dispatch = dispatchById.get(sessionId)
      const [runs, approvals] = await Promise.all([
        api.listRuns(sessionId).catch(() => [] as OrchestrationRunDto[]),
        api.listApprovals(sessionId, 'pending').catch(() => []),
      ])
      const { state, detail } = stateOf(runs, approvals.length, dispatch?.runId ?? null)
      const { title, unknown } = resolveTitle(sessionId, titleById, sessionsRead, dispatch?.title ?? null)
      next.push({
        sessionId,
        title,
        state,
        detail,
        sources: { partner: partnerIds.has(sessionId), dispatch: Boolean(dispatch) },
        unknown,
      })
    }
    if (token !== generation || disposed) return
    // "Needs approval" first: the board exists to route attention, not to be alphabetical.
    const order: Record<PartnerState, number> = { 'needs-approval': 0, working: 1, unknown: 2, done: 3, idle: 4 }
    rows.value = next.sort((left, right) => order[left.state] - order[right.state])
    failure.value = ''
  } catch {
    if (token !== generation) return
    failure.value = t('companion.center.supervisionFailed')
  }
}

function schedule(): void {
  if (disposed) return
  timer = setTimeout(async () => {
    timer = null
    if (disposed) return
    if (typeof document === 'undefined' || document.visibilityState === 'visible') await refresh()
    schedule()
  }, 10_000)
}

// The dispatched set and the partner toggles are event-driven; no second poll is added for them.
// The 10s timer below still refreshes run/approval status, which activity does not carry.
watch(dispatchSignature, () => { void refresh() })
watch(activeSessionPartners, () => { void refresh() })

onMounted(() => {
  void refresh()
  schedule()
})

onBeforeUnmount(() => {
  disposed = true
  if (timer) clearTimeout(timer)
  timer = null
})

function label(state: PartnerState): string {
  if (state === 'needs-approval') return t('companion.center.supervisionNeedsApproval')
  if (state === 'working') return t('companion.center.supervisionWorking')
  if (state === 'done') return t('companion.center.supervisionDone')
  if (state === 'idle') return t('companion.center.supervisionIdle')
  return t('companion.center.statusUnknown')
}

function sourceAttr(sources: RowSources): string {
  const parts: string[] = []
  if (sources.partner) parts.push('partner')
  if (sources.dispatch) parts.push('dispatch')
  return parts.join(' ')
}

const count = computed(() => rows.value.length)
</script>

<template>
  <section class="companion-card">
    <header class="companion-card-head">
      <h2>{{ t('companion.center.supervisionTitle') }}</h2>
      <span class="companion-card-count">{{ count }}</span>
    </header>

    <p v-if="failure" class="companion-card-error" role="alert">{{ failure }}</p>
    <p v-else-if="count === 0" class="companion-card-note">{{ t('companion.center.supervisionEmpty') }}</p>

    <ul v-else class="companion-supervision-list">
      <li
        v-for="row in rows"
        :key="row.sessionId"
        class="companion-supervision-row"
        :class="`companion-supervision-${row.state}`"
        :data-testid="`supervision-${row.sessionId}`"
        :data-sources="sourceAttr(row.sources)"
        :title="row.detail || undefined"
      >
        <span class="companion-supervision-icon" aria-hidden="true">
          <AlertCircle v-if="row.state === 'needs-approval'" :size="14" />
          <Loader2 v-else-if="row.state === 'working'" :size="14" />
          <CheckCircle2 v-else-if="row.state === 'done'" :size="14" />
          <MonitorSmartphone v-else :size="14" />
        </span>
        <span class="companion-supervision-body">
          <span class="companion-supervision-title">
            {{ row.title }}
            <span v-if="row.unknown" class="companion-supervision-unknown">{{ t('companion.center.supervisionUnknownSession') }}</span>
          </span>
          <span class="companion-supervision-state">{{ label(row.state) }}</span>
          <span class="companion-supervision-sources">
            <span v-if="row.sources.partner" class="companion-supervision-source">{{ t('companion.center.supervisionSourcePartner') }}</span>
            <span v-if="row.sources.dispatch" class="companion-supervision-source">{{ t('companion.center.supervisionSourceDispatch') }}</span>
          </span>
        </span>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.companion-supervision-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.companion-supervision-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 7px;
  font-size: 12px;
  color: var(--text-primary);
  background: var(--surface-raised);
}

.companion-supervision-icon {
  display: inline-flex;
  flex: none;
  color: var(--text-muted);
}

.companion-supervision-needs-approval .companion-supervision-icon {
  color: #d29922;
}

.companion-supervision-working .companion-supervision-icon {
  color: #58a6ff;
}

.companion-supervision-done .companion-supervision-icon {
  color: #3fb950;
}

.companion-supervision-body {
  display: flex;
  flex: 1;
  min-width: 0;
  flex-direction: column;
  gap: 1px;
}

.companion-supervision-title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.companion-supervision-unknown {
  font-size: 10.5px;
  color: var(--text-muted);
}

.companion-supervision-state {
  font-size: 10.5px;
  color: var(--text-muted);
}

.companion-supervision-sources {
  display: flex;
  gap: 4px;
}

.companion-supervision-source {
  padding: 0 4px;
  border: 1px solid var(--border-muted);
  border-radius: 4px;
  font-size: 9.5px;
  line-height: 1.5;
  color: var(--text-muted);
}
</style>
