<script setup lang="ts">
/**
 * The supervision board: what every open session partner is doing right now.
 *
 * "Session partner" means a work session whose main agent has a desktop window out — those are
 * the sessions the user is watching from outside the app. The board answers one question per
 * session: does it need me, is it working, or is it done?
 *
 * A session with no partner window is not listed. Showing every session here would turn a
 * supervision surface into a second session list, which the sidebar already is.
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertCircle, CheckCircle2, Loader2, MonitorSmartphone } from '@lucide/vue'
import { api, type OrchestrationRunDto, type SessionDto } from '@/api'
import { activeSessionPartners } from '@/lib/sessionPartner'
import { useHostAccess } from '@/lib/hostAccess'

const { t } = useI18n()
const { canAccessBackend } = useHostAccess()

type PartnerState = 'needs-approval' | 'working' | 'done' | 'idle' | 'unknown'

interface PartnerRow {
  sessionId: string
  title: string
  state: PartnerState
  detail: string
}

const rows = ref<PartnerRow[]>([])
const failure = ref('')
let timer: ReturnType<typeof setTimeout> | null = null
let disposed = false
let generation = 0

/** A run still moving is "working"; the newest terminal run is "done"; no run at all is "idle". */
function stateOf(runs: OrchestrationRunDto[], pendingApprovals: number): { state: PartnerState; detail: string } {
  if (pendingApprovals > 0) return { state: 'needs-approval', detail: String(pendingApprovals) }
  const latest = runs[0]
  if (!latest) return { state: 'idle', detail: '' }
  if (latest.status === 'completed') return { state: 'done', detail: latest.summary || '' }
  if (latest.status === 'failed' || latest.status === 'cancelled') return { state: 'unknown', detail: latest.summary || latest.status }
  return { state: 'working', detail: latest.summary || latest.status }
}

async function refresh(): Promise<void> {
  if (!canAccessBackend.value) return
  const token = ++generation
  const ids = [...activeSessionPartners.value]
  if (ids.length === 0) {
    rows.value = []
    failure.value = ''
    return
  }
  try {
    // The titles come from one session read rather than from the work page's controller: this
    // card must stand on its own when the centre is opened without Home ever having started.
    const sessions = await api.listSessions().catch(() => [] as SessionDto[])
    const titleById = new Map(sessions.map((item) => [item.id, item.title]))
    const next: PartnerRow[] = []
    for (const sessionId of ids) {
      const [runs, approvals] = await Promise.all([
        api.listRuns(sessionId).catch(() => [] as OrchestrationRunDto[]),
        api.listApprovals(sessionId, 'pending').catch(() => []),
      ])
      const { state, detail } = stateOf(runs, approvals.length)
      next.push({ sessionId, title: titleById.get(sessionId) || sessionId.slice(0, 8), state, detail })
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
      >
        <span class="companion-supervision-icon" aria-hidden="true">
          <AlertCircle v-if="row.state === 'needs-approval'" :size="14" />
          <Loader2 v-else-if="row.state === 'working'" :size="14" />
          <CheckCircle2 v-else-if="row.state === 'done'" :size="14" />
          <MonitorSmartphone v-else :size="14" />
        </span>
        <span class="companion-supervision-body">
          <span class="companion-supervision-title">{{ row.title }}</span>
          <span class="companion-supervision-state">{{ label(row.state) }}</span>
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

.companion-supervision-state {
  font-size: 10.5px;
  color: var(--text-muted);
}
</style>
