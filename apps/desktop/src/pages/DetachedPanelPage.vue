<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Loader2, Minus, PanelRightOpen, Square, X } from '@lucide/vue'
import { api, type ApprovalDto, type EventEnvelope, type OrchestrationSnapshotDto, type ToolExecutionTimelineItemDto } from '@/api'
import { useTheme } from '@/composables/useTheme'
import { useAgentActivity } from '@/composables/useAgentActivity'
import { useNotifications } from '@/composables/useNotifications'
import GitPanel from '@/components/GitPanel.vue'
import ApprovalTab from '@/components/ApprovalTab.vue'
import EventsTab from '@/components/EventsTab.vue'
import DoctorTab from '@/components/DoctorTab.vue'
import OrchestrationTab from '@/components/OrchestrationTab.vue'
import PreviewBrowserPanel from '@/components/PreviewBrowserPanel.vue'
import AgentActivityPanel from '@/components/AgentActivityPanel.vue'
import TerminalPanel from '@/components/TerminalPanel.vue'

const route = useRoute()
const { t } = useI18n()
const { notify } = useNotifications()

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

// ---- Initialize theme (independent window) ----
const { applyInitialTheme, theme, accentColor } = useTheme()

// ---- Loading and error state ----
const loading = ref(true)
const loadError = ref<string | null>(null)

// ---- Data refs (loaded independently, not via HomePage props) ----
const approvals = ref<ApprovalDto[]>([])
const events = ref<EventEnvelope[]>([])
const orchestration = ref<OrchestrationSnapshotDto | null>(null)
const toolExecutions = ref<ToolExecutionTimelineItemDto[]>([])
const shellCommand = ref('npm test')
const busy = ref(false)

const sessionIdRef = computed(() => sessionId.value)
const {
  activity: agentActivity,
  thinkingSteps: agentThinkingSteps,
  agentStates: agentStatesMap,
  progressEvents: agentProgressEvents,
} = useAgentActivity(sessionIdRef, orchestration)

let eventSource: EventSource | null = null

async function loadData() {
  if (!sessionId.value) {
    loading.value = false
    return
  }

  try {
    const [approvalList, orchestrationSnapshot, toolTimeline] = await Promise.all([
      api.listApprovals(sessionId.value),
      api.getOrchestrationSnapshot(sessionId.value).catch(() => null),
      api.listToolExecutions(sessionId.value, { limit: 12 }).catch(() => []),
    ])

    approvals.value = approvalList
    orchestration.value = orchestrationSnapshot
    toolExecutions.value = toolTimeline
    loadError.value = null
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : 'Failed to load data'
  } finally {
    loading.value = false
  }
}

function connectSSE() {
  eventSource?.close()
  if (!sessionId.value) return

  try {
    eventSource = api.connectEvents(sessionId.value, async (event) => {
      events.value = [...events.value.slice(-79), event].sort((a, b) => a.seq - b.seq)

      if (
        event.type.startsWith('message.') ||
        event.type.startsWith('approval.') ||
        event.type.startsWith('tool.') ||
        event.type.startsWith('run.') ||
        event.type.startsWith('task') ||
        event.type.startsWith('supervision.') ||
        event.type.startsWith('context.') ||
        event.type.startsWith('step.')
      ) {
        await loadData()
      }
    })
  } catch {
    // SSE connection failure is non-fatal; data was loaded via REST
  }
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
  if (!sessionId.value) return
  busy.value = true
  try {
    const approval = await api.createShellApproval(sessionId.value, shellCommand.value, projectPath.value)
    approvals.value = [approval, ...approvals.value]
  } catch (err) {
    notify.error(err, { title: 'Failed to request shell approval' })
  } finally {
    busy.value = false
  }
}

async function decideApproval(approval: ApprovalDto, decision: 'approved' | 'rejected') {
  try {
    await api.decideApproval(approval.id, decision)
    await loadData()
  } catch (err) {
    notify.error(err, { title: 'Failed to decide approval' })
  }
}

function recordApproval(approval: ApprovalDto) {
  approvals.value = [approval, ...approvals.value.filter((item) => item.id !== approval.id)]
}

onMounted(async () => {
  applyInitialTheme?.()
  await loadData()
  connectSSE()

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
  eventSource?.close()
  removeThemeListener?.()
})

watch(sessionId, () => {
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

      <!-- Error state -->
      <div v-else-if="loadError" class="detached-panel-error">
        <p class="detached-panel-error-title">{{ t('context.loadError') }}</p>
        <p class="detached-panel-error-msg">{{ loadError }}</p>
        <button class="detached-panel-retry" @click="loadData">{{ t('context.retry') }}</button>
      </div>

      <!-- Panel content by type -->
      <template v-else>
        <GitPanel
          v-if="tabType === 'git'"
          :approvals="approvals"
          :current-project-path="projectPath"
          :selected-session-id="sessionId"
          @decide-approval="decideApproval"
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

        <EventsTab
          v-else-if="tabType === 'events'"
          :events="events"
        />

        <DoctorTab
          v-else-if="tabType === 'doctor'"
          :doctor="null"
          :readiness="null"
        />

        <PreviewBrowserPanel
          v-else-if="tabType === 'preview'"
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

.detached-panel-error {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  height: 100%;
  color: var(--text-muted);
  font-size: 13px;
  padding: 20px;
  text-align: center;
}

.detached-panel-error-title {
  font-weight: 600;
  color: var(--text-primary);
  margin: 0;
}

.detached-panel-error-msg {
  color: var(--text-muted);
  margin: 0;
  word-break: break-word;
}

.detached-panel-retry {
  margin-top: 8px;
  padding: 6px 16px;
  background: var(--bg-tertiary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  color: var(--text-primary);
  font-size: 12px;
  cursor: pointer;
  transition: background 0.12s;
}

.detached-panel-retry:hover {
  background: var(--bg-hover);
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
