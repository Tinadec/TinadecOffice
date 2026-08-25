<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { usePanelStyles } from '@/composables/usePanelStyles'
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
import PreviewIslandCard from './preview/PreviewIslandCard.vue'
import { Bug, Minus, Square, X, LayoutDashboard } from '@lucide/vue'
import type { ForceApprovalDecisionRequest, SimulateMessageRequest } from './types/simulation'

const { t } = useI18n()
const { notify, confirm } = useNotifications()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const ws = useDebugWebSocket()
const traceData = useTraceData()
const simulation = useSimulation()
const metrics = useMetrics()

const activeTab = ref<'timeline' | 'graph' | 'metrics' | 'diagnostics' | 'preview'>('timeline')

const shellStyle = computed(() => getPanelStyle())
const shellAttrs = computed(() => getPanelDataAttributes())
const immersiveStyle = computed(() => {
  const s = getPanelStyle() as Record<string, string>
  const { backgroundColor, backdropFilter, WebkitBackdropFilter, ...rest } = s
  return rest
})
const immersiveAttrs = computed(() => getPanelDataAttributes())

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
  if (response?.simulated) notify.success({ message: 'Simulation message injected', source: 'debug' })
  else notify.error(response ? 'Simulation message was rejected' : simulation.error.value ?? 'Simulation message failed', { source: 'debug' })
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
  if (response?.simulated) notify.success({ message: `Simulated approval ${request.decision}`, source: 'debug' })
  else notify.error(response ? 'Approval decision was rejected' : simulation.error.value ?? 'Approval decision failed', { source: 'debug' })
}

onMounted(() => {
  ws.connect()
  traceData.fetchTraces()
  metrics.fetchDiagnostics()
})
</script>

<template>
  <div class="debug-studio" v-bind="shellAttrs" :style="shellStyle">
    <!-- Title + Tab 岛 -->
    <PreviewIslandCard variant="section" :hoverable="false" padding="none" class="debug-shell-island">
      <template #header>
        <div class="debug-titlebar">
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
        </div>
      </template>
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
    </PreviewIslandCard>

    <!-- Main content area -->
    <main class="debug-main">
      <div v-if="activeTab === 'timeline'" class="debug-timeline-layout">
        <PreviewIslandCard variant="section" padding="none" class="debug-timeline-left">
          <template #header>
            <span class="island-title">{{ t('debugStudio.tabTimeline') }}</span>
          </template>
          <TraceTimeline
            :traces="traceData.traces.value"
            :current-trace="traceData.currentTrace.value"
            :selected-span="traceData.selectedSpan.value"
            :loading="traceData.loading.value"
            @select-trace="traceData.fetchTraceDetail"
            @select-span="traceData.selectSpan"
          />
        </PreviewIslandCard>
        <PreviewIslandCard variant="section" padding="none" class="debug-timeline-right">
          <template #header>
            <span class="island-title">{{ t('debugStudio.attributes') }}</span>
          </template>
          <InspectorPanel :span="traceData.selectedSpan.value" />
        </PreviewIslandCard>
      </div>

      <PreviewIslandCard v-else-if="activeTab === 'graph'" variant="raised" padding="none" class="debug-graph-layout">
        <template #header>
          <span class="island-title">{{ t('debugStudio.tabAgentGraph') }}</span>
          <span class="island-subtitle">{{ t('debugStudio.loadingGraph') }}</span>
        </template>
        <AgentGraphCanvas />
      </PreviewIslandCard>

      <div v-else-if="activeTab === 'metrics'" class="debug-metrics-layout" v-bind="immersiveAttrs" :style="immersiveStyle">
        <MetricsDashboard :diagnostics="metrics.diagnostics.value" />
      </div>

      <div v-else-if="activeTab === 'diagnostics'" class="debug-diagnostics-layout" v-bind="immersiveAttrs" :style="immersiveStyle">
        <DiagnosticsReport :report="metrics.diagnostics.value" />
      </div>

      <div v-else-if="activeTab === 'preview'" class="debug-preview-layout" v-bind="immersiveAttrs" :style="immersiveStyle">
        <PreviewGallery />
      </div>
    </main>

    <!-- Bottom simulator bar 岛 -->
    <PreviewIslandCard variant="section" :hoverable="false" padding="none" class="debug-sim-island">
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
    </PreviewIslandCard>
  </div>
</template>

<style scoped>
/* ============================================================
   Debug Studio – 卡片岛屿布局
   遵循 TinadecUI 正规：float 岛用 var(--surface-section)+border-card+shadow-card
   中心 immersive 透明，仅 PreviewGallery 内层承载岛屿
   ============================================================ */

.debug-studio {
  display: flex;
  flex-direction: column;
  height: 100vh;
  gap: 8px;
  padding: 8px;
  background: var(--bg-primary, #0a0e14);
  color: var(--text-primary, #c9d1d9);
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans SC', sans-serif;
  -webkit-font-smoothing: antialiased;
  overflow: hidden;
}

.debug-shell-island {
  flex-shrink: 0;
}
.debug-shell-island :deep(.island-header) {
  padding: 0;
  border-bottom: 1px solid var(--border-default, #1a1f29);
}

/* ---- Title Bar（岛 header 内） ---- */
.debug-titlebar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 8px 0 12px;
  height: 38px;
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

.debug-icon { color: var(--accent-primary, #2ec4b6); }

.debug-title {
  font-size: 13px;
  font-weight: 600;
  white-space: nowrap;
  color: var(--text-primary);
}

.titlebar-divider {
  width: 1px;
  height: 16px;
  background: var(--border-default, #1a1f29);
  flex-shrink: 0;
}

.ws-status {
  font-size: 10px;
  font-weight: 700;
  padding: 2px 8px;
  border-radius: 10px;
  background: var(--text-muted, #6e7681);
  color: #fff;
  letter-spacing: 0.5px;
}
.ws-status.connected {
  background: var(--accent-success, #238636);
}

.window-btn {
  background: none;
  border: none;
  color: var(--text-secondary, #7d8590);
  cursor: pointer;
  padding: 4px;
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.12s, color 0.12s;
}
.window-btn:hover { background: var(--surface-hover, #1a1f29); color: var(--text-primary); }
.window-btn.close:hover { background: #da3633; color: #fff; }

/* ---- Tab Bar（岛 body 内） ---- */
.debug-tabs {
  display: flex;
  gap: 0;
  padding: 0 12px;
  flex-shrink: 0;
  border-top: 1px solid var(--border-default, #1a1f29);
}

.debug-tab {
  background: none;
  border: none;
  color: var(--text-muted, #6e7681);
  padding: 9px 16px;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  border-bottom: 2px solid transparent;
  transition: color 0.15s, border-color 0.15s;
}
.debug-tab:hover { color: var(--text-secondary); }
.debug-tab.active {
  color: var(--accent-primary, #2ec4b6);
  border-bottom-color: var(--accent-primary, #2ec4b6);
}

.island-title {
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.02em;
  color: var(--text-primary);
}
.island-subtitle {
  margin-left: auto;
  font-size: 11px;
  color: var(--text-muted);
}

/* ---- Main Content ---- */
.debug-main {
  flex: 1;
  overflow: hidden;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.debug-timeline-layout {
  display: flex;
  gap: 8px;
  height: 100%;
  min-height: 0;
}
.debug-timeline-left {
  flex: 3;
  min-width: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.debug-timeline-right {
  flex: 2;
  min-width: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.debug-timeline-left :deep(.island-body),
.debug-timeline-right :deep(.island-body) {
  overflow: auto;
  flex: 1;
  min-height: 0;
}

.debug-graph-layout {
  height: 100%;
  min-height: 0;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
.debug-graph-layout :deep(.island-body) {
  flex: 1;
  min-height: 0;
  overflow: hidden;
  display: flex;
}

.debug-metrics-layout,
.debug-diagnostics-layout {
  height: 100%;
  overflow: auto;
  min-height: 0;
  background: transparent;
}

.debug-preview-layout {
  height: 100%;
  overflow: hidden;
  background: transparent;
}

.debug-sim-island {
  flex-shrink: 0;
}
.debug-sim-island :deep(.island-body) {
  padding: 0;
}
</style>
