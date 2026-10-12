<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Loader2, Minus, PanelRightOpen, Square, X } from '@lucide/vue'
import { api as baseApi, createUserToolActionForPath, type ApprovalDto, type ApprovalRuleDto, type CreateApprovalRuleInput, type DoctorReportDto, type EventEnvelope, type OrchestrationSnapshotDto, type RuntimeReadinessReceiptDto, type ToolExecutionTimelineItemDto } from '@/api'
import { setSelectedStorage, registerSessionStorage, scopedApi } from '@/lib/storageScope'
import { useTheme } from '@/composables/useTheme'
import { useAgentActivity } from '@/composables/useAgentActivity'
import { followSession, subscribeToSessionEvents } from '@/lib/sessionEventBus'
import { createCoalescedRead } from '@/lib/coalescedRead'
import { isAbortError } from '@/lib/isAbortError'
import { useNotifications } from '@/composables/useNotifications'
import GitPanel from '@/components/GitPanel.vue'
import ApprovalTab from '@/components/ApprovalTab.vue'
import EventsTab from '@/components/EventsTab.vue'
import DoctorTab from '@/components/DoctorTab.vue'
import OrchestrationTab from '@/components/OrchestrationTab.vue'
import OrganizationPanel from '@/components/organization/OrganizationPanel.vue'
import PreviewBrowserPanel from '@/components/PreviewBrowserPanel.vue'
import AgentActivityPanel from '@/components/AgentActivityPanel.vue'
import TerminalPanel from '@/components/TerminalPanel.vue'
import { userToolActionIdempotencyKey, userToolActionToApproval } from '@/userToolAction'

const route = useRoute()
const { t } = useI18n()
const { notify, status, dismissByKey } = useNotifications()

// ---- Parse query params ----
const tabId = computed(() => (route.query.tabId as string) ?? '')
const tabType = computed(() => (route.query.type as string) ?? '')
const tabTitle = computed(() => (route.query.title as string) ?? '')
const tabState = computed<Record<string, unknown>>(() => {
  try {
    return JSON.parse((route.query.state as string) ?? '{}')
  } catch {
    return {}
  }
})

const sessionId = computed(() => (tabState.value.sessionId as string) ?? null)
const projectPath = computed(() => (tabState.value.projectPath as string) ?? undefined)
const storageId = computed(() => typeof tabState.value.storageId === 'string' ? tabState.value.storageId : 'user')
setSelectedStorage(storageId.value)
if (sessionId.value) registerSessionStorage(sessionId.value, storageId.value)
const api = scopedApi(baseApi, () => storageId.value)

// ---- Initialize theme (independent window) ----
const { applyInitialTheme, theme, accentColor } = useTheme()

// ---- Loading and error state ----
const loading = ref(true)

// ---- Data refs (loaded independently, not via HomePage props) ----
const approvals = ref<ApprovalDto[]>([])
const approvalRules = ref<ApprovalRuleDto[]>([])
const events = ref<EventEnvelope[]>([])
const orchestration = ref<OrchestrationSnapshotDto | null>(null)
const toolExecutions = ref<ToolExecutionTimelineItemDto[]>([])
const doctor = ref<DoctorReportDto | null>(null)
const readiness = ref<RuntimeReadinessReceiptDto | null>(null)
const shellCommand = ref('npm test')
const busy = ref(false)

const sessionIdRef = computed(() => sessionId.value)
const activityOwner = useAgentActivity(sessionIdRef, orchestration, { managedReads: true })
const {
  activity: agentActivity,
  thinkingSteps: agentThinkingSteps,
  agentStates: agentStatesMap,
  progressEvents: agentProgressEvents,
} = activityOwner

let unsubscribeEvents: (() => void) | null = null
let readEpoch = 0
let readAbort = new AbortController()
let disposed = false
type PanelRead = 'approvals' | 'activity' | 'orchestration' | 'doctor'
const activityRuns = new Set<string>()
let activityNeedsPage = true
let revertEpoch: number | null = null
async function readPanelGroups(groups: ReadonlySet<PanelRead>) {
  const epoch = readEpoch, session = sessionId.value, storage = storageId.value, type = tabType.value
  const signal = readAbort.signal
  const requestSignal = AbortSignal.any([signal, AbortSignal.timeout(30_000)])
  const current = () => !disposed && !signal.aborted && epoch === readEpoch && session === sessionId.value && storage === storageId.value && type === tabType.value
  const jobs: Promise<void>[] = []
  if (groups.has('doctor')) jobs.push(Promise.all([api.doctor(), api.readiness()]).then(([report, receipt]) => { if (current()) { doctor.value = report; readiness.value = receipt } }))
  if (session && groups.has('approvals')) jobs.push(Promise.all([api.listApprovals(session, undefined, requestSignal), api.listApprovalRules(session, requestSignal)]).then(([items, rules]) => { if (current()) { approvals.value = items; approvalRules.value = rules } }))
  if (session && groups.has('orchestration')) jobs.push(api.getOrchestrationSnapshot(session, requestSignal).then(snapshot => { if (current()) orchestration.value = snapshot }))
  if (session && groups.has('activity')) {
    jobs.push((async () => {
      if (activityNeedsPage) {
        let page = await api.listMessagePage(session, { limit: 100 }, requestSignal)
        if (!current()) return
        if (revertEpoch !== null && page.revert_epoch !== revertEpoch) {
          // One recovery belongs to this read lane and deadline. Do not recursively
          // start a new lane when another window keeps editing the history.
          const expectedEpoch = page.revert_epoch
          page = await api.listMessagePage(session, { limit: 100 }, requestSignal)
          if (!current()) return
          if (page.revert_epoch !== expectedEpoch) throw new Error('会话历史再次更新，请重试读取。')
          resetPanelHistory(page.revert_epoch)
        }
        revertEpoch = page.revert_epoch; activityNeedsPage = false
        for (const run of page.runs) activityRuns.add(run.id)
      }
      const ids = [...activityRuns], activityJobs: Promise<void>[] = []
      for (let offset = 0; offset < ids.length; offset += 100) {
        const batch = ids.slice(offset, offset + 100)
        activityJobs.push(api.getSessionActivity(session, batch, requestSignal).then(snapshot => {
          if (!current()) return
          activityOwner.hydrate(snapshot.events, snapshot.tool_executions, batch)
          const retained = toolExecutions.value.filter(tool => !batch.includes(tool.run_id))
          toolExecutions.value = [...retained, ...snapshot.tool_executions]
        }))
      }
      const results = await Promise.allSettled(activityJobs)
      const failure = results.find((result): result is PromiseRejectedResult => result.status === 'rejected')
      if (failure) throw failure.reason
    })())
  }
  const results = await Promise.allSettled(jobs)
  if (!current()) return
  loading.value = false
  const failure = results.find((result): result is PromiseRejectedResult => result.status === 'rejected' && !isAbortError(result.reason))
  if (failure) throw failure.reason
  dismissByKey('detached-panel')
}
const activityReads = createCoalescedRead<PanelRead>(readPanelGroups)
const auxiliaryReads = createCoalescedRead<PanelRead>(readPanelGroups)
const panelReads = {
  request(...groups: PanelRead[]) {
    const activity = groups.filter(group => group === 'activity')
    const auxiliary = groups.filter(group => group !== 'activity')
    return Promise.all([
      ...(activity.length ? [activityReads.request(...activity)] : []),
      ...(auxiliary.length ? [auxiliaryReads.request(...auxiliary)] : []),
    ]).then(() => {})
  },
  reset() { activityReads.reset(); auxiliaryReads.reset() },
}

async function loadData(snapshotOnly = false) {
  const groups: PanelRead[] = tabType.value === 'doctor' ? ['doctor']
    : tabType.value === 'approval' || tabType.value === 'git' ? ['approvals']
      : tabType.value === 'orchestration' ? ['orchestration', 'activity']
        : tabType.value === 'agent' ? ['activity'] : []
  if (!groups.length) { loading.value = false; return }
  const epoch = readEpoch
  async function read(wanted: PanelRead[]) {
    try {
      await panelReads.request(...wanted)
    } catch (err) {
      if (disposed || epoch !== readEpoch || isAbortError(err)) return
      status.error({
        key: 'detached-panel',
        title: t('context.loadError'),
        message: err instanceof Error ? err.message : String(err),
        source: 'panel',
        action: { label: t('context.retry'), run: () => loadData() }
      })
    } finally {
      if (!disposed && epoch === readEpoch) loading.value = false
    }
  }
  if (!snapshotOnly) return read(groups)
  const auxiliary = groups.filter(group => group !== 'activity')
  if (auxiliary.length) void read(auxiliary)
  if (groups.includes('activity')) await read(['activity'])
}

function connectSSE() {
  unsubscribeEvents?.()
  unsubscribeEvents = null
  // The detached window follows its own session; useAgentActivity subscribes to the
  // same bus underneath, so this page used to be the second of two connections here.
  followSession(sessionId.value ? `${storageId.value}::${sessionId.value}` : null)
  if (!sessionId.value) return

  unsubscribeEvents = subscribeToSessionEvents(async (event) => {
    if (event.type === 'stream.ready' || event.type === 'history.changed') {
      const epoch = event.payload?.['revert_epoch']
      const changed = event.type === 'history.changed' || (typeof epoch === 'number' && revertEpoch !== null && epoch !== revertEpoch)
      resetPanelReads()
      if (changed) resetPanelHistory(typeof epoch === 'number' ? epoch : null)
      else { activityNeedsPage = true; if (typeof epoch === 'number') revertEpoch = epoch }
      return loadData(true)
    }
    events.value = [...events.value.slice(-79), event].sort((a, b) => a.seq - b.seq)

    if (event.run_id) activityRuns.add(event.run_id)
    const groups: PanelRead[] = []
    if ((tabType.value === 'approval' || tabType.value === 'git') && (event.type.startsWith('approval.') || event.type.startsWith('governance.permission'))) groups.push('approvals')
    if ((tabType.value === 'agent' || tabType.value === 'orchestration') && (event.type.startsWith('tool.') || event.type.startsWith('run.') || event.type.startsWith('task') || event.type.startsWith('step.'))) groups.push('activity')
    if (tabType.value === 'orchestration' && (event.type.startsWith('run.') || event.type.startsWith('task') || event.type.startsWith('context.') || event.type.startsWith('step.'))) groups.push('orchestration')
    if (groups.length) await panelReads.request(...groups)
  }, { scope: sessionId })
}

function resetPanelReads() {
  readEpoch++; readAbort.abort(); readAbort = new AbortController(); panelReads.reset()
}
function resetPanelHistory(epoch: number | null = null) {
  activityNeedsPage = true; revertEpoch = epoch
  activityRuns.clear(); activityOwner.reset(); toolExecutions.value = []; orchestration.value = null; events.value = []
}

// ---- Window controls ----
function minimizeWindow() {
  window.tinadec?.minimizeWindow?.()
}

function maximizeWindow() {
  window.tinadec?.maximizeWindow?.()
}

function closeWindow() {
  window.tinadec?.closeWindow?.()
}

// ---- Reattach to main window ----
function reattach() {
  if (!tabId.value) return
  window.tinadec?.reattachPanel?.(tabId.value, tabType.value, tabTitle.value, tabState.value)
}

// ---- Listen for theme broadcasts from main window ----
let removeThemeListener: (() => void) | null = null

// ---- Shell approval actions (for ApprovalTab) ----
async function requestShellApproval() {
  if (!projectPath.value) return
  busy.value = true
  try {
    const command = shellCommand.value.trim()
    if (!command) throw new Error('Enter a command before requesting a shell action.')
    const windows = typeof navigator !== 'undefined' && /windows/i.test(navigator.userAgent)
    const params = {
      executable: windows ? 'cmd.exe' : '/bin/sh',
      arguments: windows ? ['/d', '/c', command] : ['-lc', command],
      working_directory: projectPath.value,
    }
    const idempotencyKey = await userToolActionIdempotencyKey('desktop:detached:shell', {
      project_path: projectPath.value,
      command,
    })
    const action = await createUserToolActionForPath(projectPath.value, 'command_run', params, idempotencyKey)
    const approval = userToolActionToApproval(action, `Run command: ${command}`, {
      sessionId: sessionId.value,
      cwd: projectPath.value,
    })
    approvals.value = [approval, ...approvals.value]
  } catch (err) {
    notify.error(err, { title: 'Failed to request shell approval' })
  } finally {
    busy.value = false
  }
}

async function decideApproval(
  approval: ApprovalDto,
  decision: 'approved' | 'rejected',
  scope?: 'once' | 'run',
) {
  try {
    await api.decideApproval(approval.id, decision, null, scope)
    await loadData()
  } catch (err) {
    notify.error(err, { title: 'Failed to decide approval' })
  }
}

async function revokeApprovalRule(rule: ApprovalRuleDto) {
  try {
    await api.revokeApprovalRule(rule.id)
    approvalRules.value = approvalRules.value.filter((item) => item.id !== rule.id)
  } catch (err) {
    notify.error(err, { title: t('approval.revokeRule') })
  }
}

async function createApprovalRule(input: CreateApprovalRuleInput) {
  try {
    const created = await api.createApprovalRule(input)
    approvalRules.value = [created, ...approvalRules.value.filter((item) => item.id !== created.id)]
  } catch (err) {
    notify.error(err, { title: t('approval.rememberCommand') })
  }
}

function recordApproval(approval: ApprovalDto) {
  approvals.value = [approval, ...approvals.value.filter((item) => item.id !== approval.id)]
}

onMounted(async () => {
  applyInitialTheme?.()
  connectSSE()
  await loadData()
  if (disposed) return

  removeThemeListener = window.tinadec?.onPanelThemeChanged?.((data) => {
    if (data.theme) {
      theme.value = data.theme as 'dark' | 'light' | 'system'
    }
    if (data.accentColor) {
      accentColor.value = data.accentColor
    }
  }) ?? null
})

onUnmounted(() => {
  disposed = true; readEpoch++; readAbort.abort(); panelReads.reset()
  unsubscribeEvents?.()
  removeThemeListener?.()
})

watch([sessionId, storageId, tabType], () => {
  resetPanelReads()
  setSelectedStorage(storageId.value)
  if (sessionId.value) registerSessionStorage(sessionId.value, storageId.value)
  resetPanelHistory(); approvals.value = []; approvalRules.value = []
  loading.value = true
  void loadData()
  connectSSE()
})
</script>

<template>
  <main class="detached-panel-shell">
    <!-- Custom title bar (frameless window) -->
    <header class="detached-titlebar">
      <div class="detached-titlebar-drag" />
      <div class="detached-titlebar-title">
        <span class="detached-titlebar-label">{{ tabTitle || t('context.detachedPanel') }}</span>
      </div>
      <div class="detached-titlebar-actions">
        <button
          class="detached-titlebar-btn reattach-btn"
          :title="t('context.reattachTab')"
          @click="reattach"
        >
          <PanelRightOpen :size="14" />
        </button>
        <button class="detached-titlebar-btn" :title="t('app.minimize')" @click="minimizeWindow">
          <Minus :size="14" />
        </button>
        <button class="detached-titlebar-btn" :title="t('app.maximize')" @click="maximizeWindow">
          <Square :size="12" />
        </button>
        <button class="detached-titlebar-btn close" :title="t('app.close')" @click="closeWindow">
          <X :size="14" />
        </button>
      </div>
    </header>

    <!-- Panel content -->
    <div class="detached-panel-content">
      <!-- Loading state -->
      <div v-if="loading" class="detached-panel-loading">
        <Loader2 :size="24" class="detached-panel-spinner" />
        <span>{{ t('context.loadingPanel') }}</span>
      </div>

      <!-- Panel content by type -->
      <template v-else>
        <GitPanel
          v-if="tabType === 'git'"
          :approvals="approvals"
          :approval-rules="approvalRules"
          :current-project-path="projectPath"
          :selected-session-id="sessionId"
          @decide-approval="decideApproval"
          @revoke-approval-rule="revokeApprovalRule"
          @create-approval-rule="createApprovalRule"
          @approval-created="recordApproval"
        />

        <ApprovalTab
          v-else-if="tabType === 'approval'"
          :approvals="approvals"
          :shell-command="shellCommand"
          :busy="busy"
          :selected-session-id="sessionId"
          @request-approval="requestShellApproval"
          @decide-approval="decideApproval"
          @update:shell-command="shellCommand = $event"
        />

        <OrchestrationTab
          v-else-if="tabType === 'orchestration'"
          :snapshot="orchestration"
          :tool-executions="toolExecutions"
        />

        <!-- Loads and polls its own data; the window only hands it the session. -->
        <OrganizationPanel
          v-else-if="tabType === 'organization'"
          :session-id="sessionId"
        />

        <EventsTab
          v-else-if="tabType === 'events'"
          :events="events"
        />

        <DoctorTab
          v-else-if="tabType === 'doctor'"
          :doctor="doctor"
          :readiness="readiness"
        />

        <PreviewBrowserPanel
          v-else-if="tabType === 'browser'"
          :initial-url="(tabState.url as string) ?? ''"
        />

        <AgentActivityPanel
          v-else-if="tabType === 'agent'"
          :activity="agentActivity"
          :agent-states="agentStatesMap"
          :thinking-steps="agentThinkingSteps"
          :progress-events="agentProgressEvents"
          :orchestration="orchestration"
        />

        <TerminalPanel
          v-else-if="tabType === 'terminal'"
          :cwd="projectPath"
          :visible="true"
        />

        <div v-else class="detached-panel-unknown">
          <p>{{ t('context.unknownPanelType') }}: {{ tabType }}</p>
        </div>
      </template>
    </div>
  </main>
</template>

<style scoped>
.detached-panel-shell {
  display: flex;
  flex-direction: column;
  height: 100vh;
  background: var(--bg-primary);
  overflow: hidden;
}

.detached-titlebar {
  display: flex;
  align-items: center;
  height: 36px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border-muted);
  flex-shrink: 0;
  position: relative;
}

.detached-titlebar-drag {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  -webkit-app-region: drag;
}

.detached-titlebar-title {
  display: flex;
  align-items: center;
  gap: 6px;
  padding-left: 12px;
  z-index: 1;
  pointer-events: none;
}

.detached-titlebar-label {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.detached-titlebar-actions {
  display: flex;
  align-items: center;
  gap: 2px;
  margin-left: auto;
  padding-right: 4px;
  z-index: 2;
}

.detached-titlebar-btn {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  border-radius: 6px;
  cursor: pointer;
  transition: background 0.12s, color 0.12s;
  -webkit-app-region: no-drag;
}

.detached-titlebar-btn:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.detached-titlebar-btn.close:hover {
  background: #e81123;
  color: #fff;
}

.detached-titlebar-btn.reattach-btn:hover {
  background: color-mix(in srgb, var(--accent-primary) 18%, transparent);
  color: var(--accent-primary);
}

.detached-panel-content {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.detached-panel-loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  height: 100%;
  color: var(--text-muted);
  font-size: 13px;
}

.detached-panel-spinner {
  animation: detached-spin 1s linear infinite;
}

@keyframes detached-spin {
  to {
    transform: rotate(360deg);
  }
}

.detached-panel-unknown {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  color: var(--text-muted);
  font-size: 13px;
}
</style>
