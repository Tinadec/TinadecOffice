<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import {
  AlertCircle,
  Check,
  CheckCircle2,
  ExternalLink,
  FolderGit2,
  Globe,
  Layers,
  ListChecks,
  Loader2,
  ShieldAlert,
  Square,
  X,
} from '@lucide/vue'
import { UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { pendingConversationId } from '@/lib/pageRequests'
import {
  companionApi,
  type CompanionConnectedWorkSession,
  type CompanionWorkSessionPendingApproval,
} from '@/lib/companionApi'
import { parseWorkSessionPlanProgress } from '@/lib/companionWorkSession'
import {
  applyApprovalDecided,
  applyBrowserTakenOver,
  applyRunCancelled,
  liveWorkSessions,
} from '@/lib/companionStore'

interface AgentBrowserBridge {
  openWindow?: (sessionId: string) => Promise<unknown>
  handOff?: (sessionId: string) => Promise<unknown>
  takeOver?: (sessionId: string) => Promise<unknown>
}

const props = withDefaults(defineProps<{
  session: CompanionConnectedWorkSession
  compact?: boolean
}>(), {
  compact: false,
})

const emit = defineEmits<{
  'approval-decided': [payload: { sessionId: string; approvalId: string; approved: boolean }]
  'run-cancelled': [payload: { sessionId: string; runId: string }]
  'browser-takeover': [payload: { sessionId: string; runId: string | null; spaceId: string }]
  'open-session': [payload: { sessionId: string; route: '/' | '/space' }]
}>()

const { t } = useI18n()
const router = useRouter()
const { notify } = useNotifications()

const decidingIds = ref<Record<string, boolean>>({})
const cancelling = ref(false)
const browserBusy = ref(false)

/** Overlay the newest SSE work-session snapshot on top of the passed prop so cards update without polling. */
const resolved = computed<CompanionConnectedWorkSession>(() =>
  liveWorkSessions.value[props.session.session_id] ?? props.session)

const normalizedStatus = computed(() => (resolved.value.latest_run_status ?? 'idle').toLowerCase())

const isAwaitingApproval = computed(() =>
  normalizedStatus.value === 'awaiting_approval' || resolved.value.pending_approvals.length > 0)

const isBrowserHandoff = computed(() => normalizedStatus.value === 'browser_handoff')

const isRunning = computed(() => {
  const s = normalizedStatus.value
  return s === 'running'
    || s === 'queued'
    || s === 'pending'
    || s === 'starting'
    || s === 'in_progress'
    || s === 'thinking'
    || s === 'planning'
    || s === 'executing'
})

const canCancelRun = computed(() =>
  Boolean(resolved.value.latest_run_id) && (isRunning.value || isAwaitingApproval.value || isBrowserHandoff.value))

const statusTone = computed<'warning' | 'running' | 'success' | 'danger' | 'idle'>(() => {
  if (isAwaitingApproval.value || isBrowserHandoff.value) return 'warning'
  if (isRunning.value) return 'running'
  if (normalizedStatus.value === 'completed') return 'success'
  if (normalizedStatus.value === 'failed') return 'danger'
  return 'idle'
})

const statusLabel = computed(() => {
  switch (normalizedStatus.value) {
    case 'awaiting_approval':
      return t('companionCenter.workSession.statusAwaitingApproval')
    case 'browser_handoff':
      return t('companionCenter.workSession.statusBrowserHandoff')
    case 'running':
    case 'in_progress':
    case 'thinking':
    case 'planning':
    case 'executing':
    case 'starting':
      return t('companionCenter.workSession.statusRunning')
    case 'queued':
    case 'pending':
      return t('companionCenter.workSession.statusQueued')
    case 'completed':
      return t('companionCenter.workSession.statusCompleted')
    case 'failed':
      return t('companionCenter.workSession.statusFailed')
    case 'cancelled':
    case 'canceled':
    case 'stopped':
      return t('companionCenter.workSession.statusCancelled')
    default:
      return t('companionCenter.workSession.statusIdle')
  }
})

const workspaceLabel = computed(() =>
  resolved.value.project_name
    || (resolved.value.project_id ? resolved.value.project_id.slice(0, 8) : t('companionCenter.workSession.freeSession')))

const planProgress = computed(() =>
  resolved.value.plan_progress ?? parseWorkSessionPlanProgress(resolved.value.plan_summary))

const planPercent = computed(() => {
  const progress = planProgress.value
  if (!progress || progress.total <= 0) return 0
  return Math.round((progress.done / progress.total) * 100)
})

const browserDriverLabel = computed(() => {
  const driver = resolved.value.browser_driver
  if (!driver) return null
  if (driver === 'ego_lite') return 'Ego Lite'
  if (driver === 'builtin_electron') return t('companionCenter.workSession.driverBuiltin')
  return driver
})

function agentBrowserBridge(): AgentBrowserBridge | undefined {
  if (typeof window === 'undefined') return undefined
  return (window as unknown as { tinadec?: { agentBrowser?: AgentBrowserBridge } }).tinadec?.agentBrowser
}

const canOpenBrowserWindow = computed(() => Boolean(agentBrowserBridge()?.openWindow))

function riskLabel(risk: string | null | undefined): string | null {
  if (!risk) return null
  const r = risk.toLowerCase()
  if (r === 'high' || r === 'critical') return t('companionCenter.workSession.riskHigh')
  if (r === 'medium') return t('companionCenter.workSession.riskMedium')
  if (r === 'low') return t('companionCenter.workSession.riskLow')
  return risk
}

async function decideApproval(item: CompanionWorkSessionPendingApproval, approved: boolean) {
  if (decidingIds.value[item.approval_id]) return
  decidingIds.value = { ...decidingIds.value, [item.approval_id]: true }
  try {
    await companionApi.decideApproval(item.approval_id, approved)
    applyApprovalDecided(resolved.value.session_id, item.approval_id, approved)
    emit('approval-decided', {
      sessionId: resolved.value.session_id,
      approvalId: item.approval_id,
      approved,
    })
  } catch (error) {
    notify.error(error)
  } finally {
    const next = { ...decidingIds.value }
    delete next[item.approval_id]
    decidingIds.value = next
  }
}

async function cancelActiveRun() {
  const runId = resolved.value.latest_run_id
  if (!runId || cancelling.value) return
  cancelling.value = true
  try {
    await companionApi.cancelRun(runId)
    applyRunCancelled(resolved.value.session_id, runId)
    emit('run-cancelled', { sessionId: resolved.value.session_id, runId })
  } catch (error) {
    notify.error(error)
  } finally {
    cancelling.value = false
  }
}

async function openBrowserWindow() {
  const bridge = agentBrowserBridge()
  const spaceId = resolved.value.browser_space_id ?? resolved.value.session_id
  if (!bridge?.openWindow || browserBusy.value) return
  browserBusy.value = true
  try {
    await bridge.openWindow(spaceId)
  } catch (error) {
    notify.error(error)
  } finally {
    browserBusy.value = false
  }
}

async function handBackBrowserToAgent() {
  if (browserBusy.value) return
  const spaceId = resolved.value.browser_space_id ?? resolved.value.session_id
  browserBusy.value = true
  try {
    const bridge = agentBrowserBridge()
    if (bridge?.takeOver) {
      await bridge.takeOver(spaceId)
    }
    applyBrowserTakenOver(resolved.value.session_id)
    emit('browser-takeover', {
      sessionId: resolved.value.session_id,
      runId: resolved.value.latest_run_id,
      spaceId,
    })
  } catch (error) {
    notify.error(error)
  } finally {
    browserBusy.value = false
  }
}

function openWorkspace() {
  const session = resolved.value
  // HomePage consumes pendingConversationId, selects the project/session, and auto-routes to /space when view_mode === 'space'.
  pendingConversationId.value = session.session_id
  const target: '/' | '/space' = session.view_mode === 'space' ? '/space' : '/'
  emit('open-session', { sessionId: session.session_id, route: target })
  void router.push('/')
}
</script>

<template>
  <section
    class="ws-card"
    :class="[`is-${statusTone}`, { 'is-compact': compact }]"
    :data-testid="`companion-work-session-card-${resolved.session_id}`"
  >
    <header class="ws-card-head">
      <div class="ws-badges">
        <span class="ws-badge ws-badge-workspace" :title="workspaceLabel">
          <FolderGit2 :size="12" aria-hidden="true" />
          <span class="ws-badge-text">{{ workspaceLabel }}</span>
        </span>
        <span v-if="resolved.mode_name" class="ws-badge ws-badge-mode">
          <Layers :size="11" aria-hidden="true" />
          <span>{{ resolved.mode_name }}</span>
        </span>
      </div>

      <span class="ws-status-pill" :class="`is-${statusTone}`" data-testid="work-session-status-pill">
        <Loader2 v-if="statusTone === 'running'" :size="12" class="ws-spin" aria-hidden="true" />
        <Globe v-else-if="isBrowserHandoff" :size="12" aria-hidden="true" />
        <ShieldAlert v-else-if="isAwaitingApproval" :size="12" aria-hidden="true" />
        <CheckCircle2 v-else-if="statusTone === 'success'" :size="12" aria-hidden="true" />
        <AlertCircle v-else-if="statusTone === 'danger'" :size="12" aria-hidden="true" />
        <Square v-else :size="10" aria-hidden="true" />
        <span>{{ statusLabel }}</span>
      </span>
    </header>

    <div class="ws-title-row">
      <h4 class="ws-title" :title="resolved.title">{{ resolved.title }}</h4>
      <span v-if="resolved.pending_approvals.length" class="ws-pending-count">
        {{ t('companionCenter.workSession.pendingCount', { count: resolved.pending_approvals.length }) }}
      </span>
    </div>

    <div v-if="planProgress" class="ws-plan-box" data-testid="work-session-plan-progress">
      <div class="ws-plan-head">
        <span class="ws-plan-label">
          <ListChecks :size="12" aria-hidden="true" />
          <span>{{ t('companionCenter.workSession.planProgress') }}</span>
        </span>
        <span v-if="planProgress.total > 0" class="ws-plan-ratio">
          {{ planProgress.done }} / {{ planProgress.total }}
        </span>
      </div>
      <div v-if="planProgress.total > 0" class="ws-plan-bar" role="progressbar" :aria-valuenow="planProgress.done" :aria-valuemax="planProgress.total" aria-valuemin="0">
        <div class="ws-plan-bar-fill" :style="{ width: `${planPercent}%` }" />
      </div>
      <p v-if="planProgress.current_step" class="ws-plan-step">
        {{ planProgress.current_step }}
      </p>
    </div>

    <p v-if="resolved.latest_run_summary && resolved.latest_run_summary !== resolved.plan_summary" class="ws-summary">
      {{ resolved.latest_run_summary }}
    </p>

    <div v-if="isBrowserHandoff" class="ws-handoff-box" data-testid="work-session-browser-handoff">
      <div class="ws-handoff-head">
        <Globe :size="13" class="ws-warning-icon" aria-hidden="true" />
        <strong>{{ t('companionCenter.workSession.browserHandoffTitle') }}</strong>
        <span v-if="browserDriverLabel" class="ws-badge ws-badge-mode">{{ browserDriverLabel }}</span>
      </div>
      <p v-if="resolved.browser_handoff_reason || resolved.browser_url" class="ws-handoff-meta">
        <span v-if="resolved.browser_handoff_reason">{{ resolved.browser_handoff_reason }}</span>
        <span v-if="resolved.browser_url" class="ws-handoff-url">{{ resolved.browser_url }}</span>
      </p>
      <div class="ws-handoff-actions">
        <UiButton
          v-if="canOpenBrowserWindow"
          size="sm"
          variant="outline"
          class="ws-btn"
          :disabled="browserBusy"
          data-testid="work-session-browser-open-btn"
          @click="openBrowserWindow"
        >
          <ExternalLink :size="12" aria-hidden="true" />
          <span>{{ t('companionCenter.workSession.openBrowserWindow') }}</span>
        </UiButton>
        <UiButton
          size="sm"
          variant="default"
          class="ws-btn"
          :disabled="browserBusy"
          data-testid="work-session-browser-takeover-btn"
          @click="handBackBrowserToAgent"
        >
          <Check :size="12" aria-hidden="true" />
          <span>{{ t('companionCenter.workSession.browserTakeoverDone') }}</span>
        </UiButton>
      </div>
    </div>

    <div v-if="resolved.pending_approvals.length" class="ws-approvals" data-testid="work-session-approvals">
      <div
        v-for="item in resolved.pending_approvals"
        :key="item.approval_id"
        class="ws-approval-row"
        :data-testid="`work-session-approval-${item.approval_id}`"
      >
        <div class="ws-approval-copy">
          <div class="ws-approval-head">
            <ShieldAlert :size="12" class="ws-warning-icon" aria-hidden="true" />
            <code class="ws-tool-id">{{ item.tool_id }}</code>
            <span v-if="riskLabel(item.risk_level)" class="ws-risk-pill" :class="`is-${(item.risk_level || '').toLowerCase()}`">
              {{ riskLabel(item.risk_level) }}
            </span>
          </div>
          <p v-if="item.summary" class="ws-approval-summary">{{ item.summary }}</p>
        </div>
        <div class="ws-approval-actions">
          <UiButton
            size="sm"
            variant="default"
            class="ws-btn"
            :disabled="Boolean(decidingIds[item.approval_id])"
            data-testid="work-session-approve-btn"
            @click="decideApproval(item, true)"
          >
            <Check :size="12" aria-hidden="true" />
            <span>{{ t('companionCenter.chat.approve') }}</span>
          </UiButton>
          <UiButton
            size="sm"
            variant="outline"
            class="ws-btn"
            :disabled="Boolean(decidingIds[item.approval_id])"
            data-testid="work-session-reject-btn"
            @click="decideApproval(item, false)"
          >
            <X :size="12" aria-hidden="true" />
            <span>{{ t('companionCenter.chat.deny') }}</span>
          </UiButton>
        </div>
      </div>
    </div>

    <footer class="ws-card-actions">
      <UiButton
        v-if="canCancelRun"
        size="sm"
        variant="outline"
        class="ws-btn ws-btn-stop"
        :disabled="cancelling"
        data-testid="work-session-cancel-btn"
        @click="cancelActiveRun"
      >
        <Square :size="11" aria-hidden="true" />
        <span>{{ t('companionCenter.workSession.cancelRun') }}</span>
      </UiButton>
      <UiButton
        size="sm"
        variant="outline"
        class="ws-btn"
        data-testid="work-session-open-btn"
        @click="openWorkspace"
      >
        <ExternalLink :size="12" aria-hidden="true" />
        <span>{{ t('companionCenter.workSession.enterWorkspace') }}</span>
      </UiButton>
    </footer>
  </section>
</template>

<style scoped>
.ws-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-radius: var(--radius-card, 10px);
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-raised);
  color: var(--text-primary);
  min-width: 0;
  transition: border-color 140ms ease, background-color 140ms ease;
}

.ws-card.is-compact {
  padding: 8px 10px;
  gap: 6px;
}

.ws-card.is-warning {
  border-color: color-mix(in srgb, var(--accent-warning, #d97706) 34%, var(--border-card, var(--border-muted)));
  background: color-mix(in srgb, var(--accent-warning, #d97706) 4%, var(--surface-raised));
}

.ws-card.is-running {
  border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-card, var(--border-muted)));
}

.ws-card.is-danger {
  border-color: color-mix(in srgb, var(--accent-danger) 32%, var(--border-card, var(--border-muted)));
  background: color-mix(in srgb, var(--accent-danger) 4%, var(--surface-raised));
}

.ws-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-width: 0;
}

.ws-badges {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  overflow: hidden;
}

.ws-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 7px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  line-height: 1;
  white-space: nowrap;
  max-width: 100%;
}

.ws-badge-workspace {
  min-width: 0;
  overflow: hidden;
}

.ws-badge-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-badge-mode {
  flex-shrink: 0;
  color: var(--text-primary);
  background: color-mix(in srgb, var(--accent-primary) 8%, var(--surface-section));
  border-color: color-mix(in srgb, var(--accent-primary) 22%, var(--border-muted));
}

.ws-status-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 8px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 600;
  white-space: nowrap;
  flex-shrink: 0;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
}

.ws-status-pill.is-running {
  color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
  border-color: color-mix(in srgb, var(--accent-primary) 28%, transparent);
}

.ws-status-pill.is-warning {
  color: var(--accent-warning, #d97706);
  background: color-mix(in srgb, var(--accent-warning, #d97706) 13%, transparent);
  border-color: color-mix(in srgb, var(--accent-warning, #d97706) 30%, transparent);
}

.ws-status-pill.is-success {
  color: var(--accent-success, #16a34a);
  background: color-mix(in srgb, var(--accent-success, #16a34a) 12%, transparent);
  border-color: color-mix(in srgb, var(--accent-success, #16a34a) 28%, transparent);
}

.ws-status-pill.is-danger {
  color: var(--accent-danger);
  background: color-mix(in srgb, var(--accent-danger) 12%, transparent);
  border-color: color-mix(in srgb, var(--accent-danger) 28%, transparent);
}

.ws-spin {
  animation: ws-spin 1.1s linear infinite;
}

@keyframes ws-spin {
  to { transform: rotate(360deg); }
}

.ws-title-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-width: 0;
}

.ws-title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  line-height: 1.35;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}

.ws-pending-count {
  font-size: 11px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  color: var(--accent-warning, #d97706);
  white-space: nowrap;
  flex-shrink: 0;
}

.ws-plan-box {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 7px 9px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.ws-plan-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 11px;
  color: var(--text-secondary);
}

.ws-plan-label {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-weight: 500;
}

.ws-plan-ratio {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  color: var(--text-primary);
  white-space: nowrap;
}

.ws-plan-bar {
  height: 4px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--border-muted) 80%, transparent);
  overflow: hidden;
}

.ws-plan-bar-fill {
  height: 100%;
  border-radius: 999px;
  background: var(--accent-primary);
  transition: width 220ms cubic-bezier(0.2, 0, 0, 1);
}

.ws-plan-step {
  margin: 0;
  font-size: 12px;
  line-height: 1.4;
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-summary {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-secondary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.ws-handoff-box {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 34%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning, #d97706) 7%, var(--surface-section));
}

.ws-handoff-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.ws-handoff-meta {
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 11px;
  line-height: 1.4;
  color: var(--text-secondary);
}

.ws-handoff-url {
  font-family: var(--font-mono, monospace);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-handoff-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
}

.ws-approvals {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.ws-approval-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  padding: 7px 9px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #d97706) 30%, var(--border-muted));
  background: var(--surface-section);
}

.ws-approval-copy {
  display: flex;
  flex-direction: column;
  gap: 3px;
  flex: 1;
  min-width: 140px;
}

.ws-approval-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.ws-warning-icon {
  color: var(--accent-warning, #d97706);
  flex-shrink: 0;
}

.ws-tool-id {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  font-weight: 600;
  color: var(--text-primary);
  background: color-mix(in srgb, var(--text-primary) 7%, transparent);
  padding: 1px 5px;
  border-radius: 5px;
}

.ws-risk-pill {
  font-size: 10px;
  font-weight: 600;
  padding: 1px 6px;
  border-radius: 999px;
  white-space: nowrap;
  background: color-mix(in srgb, var(--accent-warning, #d97706) 14%, transparent);
  color: var(--accent-warning, #d97706);
}

.ws-risk-pill.is-high,
.ws-risk-pill.is-critical {
  background: color-mix(in srgb, var(--accent-danger) 14%, transparent);
  color: var(--accent-danger);
}

.ws-approval-summary {
  margin: 0;
  font-size: 12px;
  line-height: 1.4;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}

.ws-approval-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.ws-card-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
  flex-wrap: wrap;
}

.ws-btn {
  height: 28px;
  white-space: nowrap;
  flex-shrink: 0;
}

.ws-btn-stop {
  color: var(--accent-danger);
  border-color: color-mix(in srgb, var(--accent-danger) 32%, var(--border-muted));
}

.ws-btn-stop:hover:not(:disabled) {
  background: color-mix(in srgb, var(--accent-danger) 10%, transparent);
}

.ws-card :deep(button:focus-visible) {
  outline: 2px solid var(--accent-primary);
  outline-offset: 2px;
}

@media (prefers-reduced-motion: reduce) {
  .ws-spin { animation: none; }
  .ws-plan-bar-fill { transition: none; }
}
</style>
