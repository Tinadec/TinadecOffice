<script setup lang="ts">
import { ref, onBeforeUnmount, onMounted, watch, type Ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { useDebugWebSocket } from './composables/useDebugWebSocket'
import { useTraceData } from './composables/useTraceData'
import { useSimulation } from './composables/useSimulation'
import { useMetrics } from './composables/useMetrics'
import TraceTimeline from './components/TraceTimeline.vue'
import InspectorPanel from './components/InspectorPanel.vue'
import AgentGraphCanvas from './components/AgentGraphCanvas.vue'
import SimulatorBar from './components/SimulatorBar.vue'
import MetricsDashboard from './components/MetricsDashboard.vue'
import DiagnosticsReport from './components/DiagnosticsReport.vue'
import SessionSelector from './components/SessionSelector.vue'
import LiveReplayToggle from './components/LiveReplayToggle.vue'
import PreviewGallery from './preview/PreviewGallery.vue'
import { Bug, Minus, Square, X, LayoutDashboard } from '@lucide/vue'
import type { ForceApprovalDecisionRequest, SimulateMessageRequest } from './types/simulation'

const { t } = useI18n()
const { notify, banner, dismiss, confirm } = useNotifications()
const ws = useDebugWebSocket()
const traceData = useTraceData()
const simulation = useSimulation()
const metrics = useMetrics()

const activeTab = ref<'timeline' | 'graph' | 'metrics' | 'diagnostics' | 'preview'>('timeline')

function mirrorErrorToBanner(source: Ref<string | null>) {
  let id: string | null = null
  const stop = watch(source, (message) => {
    if (id) dismiss(id)
    id = message ? banner.error(message) : null
  })
  onBeforeUnmount(() => {
    stop()
    if (id) dismiss(id)
  })
}

mirrorErrorToBanner(traceData.error)
mirrorErrorToBanner(metrics.error)

function minimizeWindow() {
  window.tinadec?.minimizeWindow?.()
}
function maximizeWindow() {
  window.tinadec?.maximizeWindow?.()
}
function closeWindow() {
  window.tinadec?.closeWindow?.()
}

async function injectMessage(request: SimulateMessageRequest) {
  const response = await simulation.injectMessage(request)
  if (response?.simulated) notify.success('Simulation message injected')
  else notify.error(response ? 'Simulation message was rejected' : simulation.error.value ?? 'Simulation message failed')
}

async function forceApproval(request: ForceApprovalDecisionRequest) {
  const confirmed = await confirm({
    title: 'Force simulated approval decision',
    message: `Force this approval to be ${request.decision}?`,
    confirmLabel: request.decision === 'approved' ? 'Approve' : 'Reject',
    destructive: true,
  })
  if (!confirmed) return

  const response = await simulation.forceApprovalDecision(request)
  if (response?.simulated) notify.success(`Simulated approval ${request.decision}`)
  else notify.error(response ? 'Approval decision was rejected' : simulation.error.value ?? 'Approval decision failed')
}

onMounted(() => {
  ws.connect()
  traceData.fetchTraces()
  metrics.fetchDiagnostics()
})
</script>

<template>
  <div class="debug-studio">
    <!-- Title bar -->
    <header class="debug-titlebar">
      <div class="debug-titlebar-left">
        <Bug :size="16" class="debug-icon" />
        <span class="debug-title">{{ t('debugStudio.title') }}</span>
        <div class="titlebar-divider" />
        <SessionSelector />
        <LiveReplayToggle />
      </div>
      <div class="debug-titlebar-right">
        <span class="ws-status" :class="{ connected: ws.connected.value }">
          {{ ws.connected.value ? t('debugStudio.live') : t('debugStudio.offline') }}
        </span>
        <div class="titlebar-divider" />
        <button class="window-btn" @click="minimizeWindow" :title="t('app.minimize')"><Minus :size="14" /></button>
        <button class="window-btn" @click="maximizeWindow" :title="t('app.maximize')"><Square :size="12" /></button>
        <button class="window-btn close" @click="closeWindow" :title="t('app.close')"><X :size="14" /></button>
      </div>
    </header>

    <!-- Tab bar -->
    <nav class="debug-tabs">
      <button class="debug-tab" :class="{ active: activeTab === 'timeline' }" @click="activeTab = 'timeline'">
        {{ t('debugStudio.tabTimeline') }}
      </button>
      <button class="debug-tab" :class="{ active: activeTab === 'graph' }" @click="activeTab = 'graph'">
        {{ t('debugStudio.tabAgentGraph') }}
      </button>
      <button class="debug-tab" :class="{ active: activeTab === 'metrics' }" @click="activeTab = 'metrics'">
        {{ t('debugStudio.tabMetrics') }}
      </button>
      <button class="debug-tab" :class="{ active: activeTab === 'diagnostics' }" @click="activeTab = 'diagnostics'">
        {{ t('debugStudio.tabDiagnostics') }}
      </button>
      <button class="debug-tab" :class="{ active: activeTab === 'preview' }" @click="activeTab = 'preview'">
        <LayoutDashboard :size="12" style="margin-right: 4px; vertical-align: middle;" />
        预览
      </button>
    </nav>

    <!-- Main content area -->
    <main class="debug-main">
      <div v-if="activeTab === 'timeline'" class="debug-timeline-layout">
        <div class="debug-timeline-left">
          <TraceTimeline
            :traces="traceData.traces.value"
            :current-trace="traceData.currentTrace.value"
            :selected-span="traceData.selectedSpan.value"
            :loading="traceData.loading.value"
            @select-trace="traceData.fetchTraceDetail"
            @select-span="traceData.selectSpan"
          />
        </div>
        <div class="debug-timeline-right">
          <InspectorPanel :span="traceData.selectedSpan.value" />
        </div>
      </div>

      <div v-else-if="activeTab === 'graph'" class="debug-graph-layout">
        <AgentGraphCanvas />
      </div>

      <div v-else-if="activeTab === 'metrics'" class="debug-metrics-layout">
        <MetricsDashboard :diagnostics="metrics.diagnostics.value" />
      </div>

      <div v-else-if="activeTab === 'diagnostics'" class="debug-diagnostics-layout">
        <DiagnosticsReport :report="metrics.diagnostics.value" />
      </div>

      <div v-else-if="activeTab === 'preview'" class="debug-preview-layout">
        <PreviewGallery />
      </div>
    </main>

    <!-- Bottom simulator bar -->
    <SimulatorBar
      :mode="simulation.mode.value"
      :current-step="simulation.currentStep.value"
      :total-steps="simulation.totalSteps.value"
      @step="ws.stepSimulation"
      @run="ws.resumeSimulation"
      @pause="() => { simulation.mode.value = 'paused' }"
      @reset="ws.resetSimulation"
      @inject-message="injectMessage"
      @inject-tool-result="simulation.injectToolResult"
      @force-approval="forceApproval"
    />
  </div>
</template>

<style scoped>
/* ============================================================
   Debug Studio – Layout & Styling
   ============================================================ */

.debug-studio {
  display: flex;
  flex-direction: column;
  height: 100vh;
  background: #0d1117;
  color: #e6edf3;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans SC', sans-serif;
  -webkit-font-smoothing: antialiased;
}

/* ---- Title Bar ---- */
.debug-titlebar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 8px 0 12px;
  height: 36px;
  background: #161b22;
  border-bottom: 1px solid #30363d;
  -webkit-app-region: drag;
  user-select: none;
  flex-shrink: 0;
}

.debug-titlebar-left,
.debug-titlebar-right {
  display: flex;
  align-items: center;
  gap: 8px;
  -webkit-app-region: no-drag;
}

.debug-icon { color: #58a6ff; }

.debug-title {
  font-size: 13px;
  font-weight: 600;
  white-space: nowrap;
}

.titlebar-divider {
  width: 1px;
  height: 16px;
  background: #30363d;
  flex-shrink: 0;
}

.ws-status {
  font-size: 10px;
  font-weight: 700;
  padding: 2px 8px;
  border-radius: 10px;
  background: #6e7681;
  color: #fff;
  letter-spacing: 0.5px;
}
.ws-status.connected {
  background: #238636;
}

.window-btn {
  background: none;
  border: none;
  color: #8b949e;
  cursor: pointer;
  padding: 4px;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.12s;
}
.window-btn:hover { background: #30363d; color: #e6edf3; }
.window-btn.close:hover { background: #da3633; color: #fff; }

/* ---- Tab Bar ---- */
.debug-tabs {
  display: flex;
  gap: 0;
  background: #161b22;
  border-bottom: 1px solid #30363d;
  padding: 0 12px;
  flex-shrink: 0;
}

.debug-tab {
  background: none;
  border: none;
  color: #8b949e;
  padding: 8px 16px;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  border-bottom: 2px solid transparent;
  transition: color 0.15s, border-color 0.15s;
}
.debug-tab:hover { color: #c9d1d9; }
.debug-tab.active {
  color: #58a6ff;
  border-bottom-color: #58a6ff;
}

/* ---- Main Content ---- */
.debug-main {
  flex: 1;
  overflow: hidden;
  min-height: 0;
}

.debug-timeline-layout {
  display: flex;
  height: 100%;
}
.debug-timeline-left {
  flex: 3;
  min-width: 0;
  border-right: 1px solid #30363d;
  overflow: auto;
}
.debug-timeline-right {
  flex: 2;
  min-width: 0;
  overflow: auto;
}

.debug-graph-layout,
.debug-metrics-layout,
.debug-diagnostics-layout {
  height: 100%;
  overflow: auto;
  padding: 20px;
}

.debug-preview-layout {
  height: 100%;
  overflow: hidden;
}
</style>
