<script setup lang="ts">
import {
  AlertTriangle,
  ArrowLeft,
  ArrowRight,
  Bot,
  ExternalLink,
  Globe,
  Hand,
  Home as HomeIcon,
  Layers,
  Link2,
  Monitor,
  Play,
  Plus,
  RefreshCw,
  RotateCw,
  SlidersHorizontal,
  Smartphone,
  Tablet,
  X,
} from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { usePeerDevices, type PeerDevServerLeaseDto } from '@/composables/usePeerDevices'

type ViewportPreset = 'desktop' | 'tablet' | 'mobile' | 'custom'
type PanelMode = 'tabs' | 'mirror'
type ControlState = 'agent' | 'user' | 'finished'

interface BrowserTab {
  id: string
  urlInput: string
  currentUrl: string
  history: string[]
  historyIndex: number
  loading: boolean
  iframeKey: number
}

interface AgentConsoleError {
  seq?: number
  at?: string
  level?: string
  source?: string
  message: string
  url?: string
  line?: number
}

interface AgentDialogInfo {
  type?: string
  message?: string
  url?: string
  tabId?: string
}

interface AgentSpaceSummary {
  space_id: string
  session_id: string
  base_session_id?: string
  task_space?: string
  control: ControlState
  handoff_reason?: string | null
  finished?: boolean
  active_tab: string | null
  url: string
  title: string
  loading: boolean
  tab_count: number
  dialog: AgentDialogInfo | null
  console_error_count?: number
  console_errors?: AgentConsoleError[]
}

interface AgentPreviewFrame {
  space_id: string
  session_id: string
  control: ControlState
  tab_id: string | null
  url: string
  title: string
  loading: boolean
  dialog: AgentDialogInfo | null
  data_url: string | null
  captured_at: string
  console_errors?: AgentConsoleError[]
}

interface AgentBrowserActionResult {
  ok: boolean
  code?: string
  message?: string
  state?: AgentSpaceSummary
}

interface AgentBrowserMirrorBridge {
  listSpaces: () => Promise<AgentSpaceSummary[]>
  capturePreview: (sessionId: string) => Promise<AgentPreviewFrame | null>
  openWindow: (sessionId: string) => Promise<AgentBrowserActionResult>
  handOff: (sessionId: string, reason?: string) => Promise<AgentBrowserActionResult>
  takeOver: (sessionId: string) => Promise<AgentBrowserActionResult>
  onStateChanged: (callback: (payload: { space?: AgentSpaceSummary; spaces?: AgentSpaceSummary[] }) => void) => () => void
}

function getAgentBrowserBridge(): AgentBrowserMirrorBridge | null {
  const host = (window as unknown as { tinadec?: { agentBrowser?: AgentBrowserMirrorBridge } }).tinadec
  const bridge = host?.agentBrowser
  if (!bridge || typeof bridge.listSpaces !== 'function') return null
  return bridge
}

const { t, te } = useI18n()
const { notify } = useNotifications()

function label(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

const props = defineProps<{
  initialUrl?: string
}>()

const emit = defineEmits<{
  'update:url': [url: string]
}>()

const gatewayBase = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
const gatewayDocsUrl = `${gatewayBase}/docs`
const VITE_DEV_URL = 'http://localhost:5173'

const panelMode = ref<PanelMode>('tabs')

let tabCounter = 1
function createTab(initial = ''): BrowserTab {
  const normalized = initial ? normalizeUrl(initial) : ''
  return {
    id: `tab-${tabCounter++}`,
    urlInput: normalized,
    currentUrl: normalized,
    history: normalized ? [normalized] : [],
    historyIndex: normalized ? 0 : -1,
    loading: Boolean(normalized),
    iframeKey: 0,
  }
}

const tabs = ref<BrowserTab[]>([createTab(props.initialUrl ?? '')])
const activeTabId = ref(tabs.value[0].id)
const viewport = ref<ViewportPreset>('desktop')
const customWidth = ref(1280)
const customHeight = ref(800)
const customAutoHeight = ref(true)

function clampDimension(value: number, min: number, max: number, fallback: number): number {
  if (!Number.isFinite(value)) return fallback
  return Math.min(max, Math.max(min, Math.round(value)))
}

function updateCustomWidth(raw: string): void {
  const parsed = Number.parseInt(raw, 10)
  if (!Number.isNaN(parsed)) {
    customWidth.value = clampDimension(parsed, 240, 2560, 1280)
  }
}

function updateCustomHeight(raw: string): void {
  const parsed = Number.parseInt(raw, 10)
  if (!Number.isNaN(parsed)) {
    customHeight.value = clampDimension(parsed, 320, 2160, 800)
    customAutoHeight.value = false
  }
}

function swapCustomDimensions(): void {
  const nextWidth = clampDimension(customHeight.value, 240, 2560, 800)
  const nextHeight = clampDimension(customWidth.value, 320, 2160, 1280)
  customWidth.value = nextWidth
  customHeight.value = nextHeight
  customAutoHeight.value = false
}

const previewFrameStyle = computed<Record<string, string> | undefined>(() => {
  if (viewport.value !== 'custom') return undefined
  return {
    width: `${customWidth.value}px`,
    maxWidth: '100%',
    height: customAutoHeight.value ? '100%' : `${customHeight.value}px`,
  }
})

const activeTab = computed<BrowserTab>(
  () => tabs.value.find((tab) => tab.id === activeTabId.value) ?? tabs.value[0],
)

const canGoBack = computed(() => activeTab.value.historyIndex > 0)
const canGoForward = computed(() => activeTab.value.historyIndex < activeTab.value.history.length - 1)

const quickLinks = computed(() => [
  { label: label('context.previewGatewayApi', 'Gateway API'), url: gatewayDocsUrl },
  { label: label('context.previewGatewayHealth', 'Gateway Health'), url: `${gatewayBase}/api/v1/health` },
  { label: label('context.previewDevServer', 'Dev Server'), url: VITE_DEV_URL },
  { label: 'Local App :3000', url: 'http://localhost:3000' },
  { label: 'Local App :8080', url: 'http://localhost:8080' },
])

function normalizeUrl(input: string): string {
  const trimmed = input.trim()
  if (!trimmed) return ''
  if (/^https?:\/\//i.test(trimmed)) return trimmed
  if (/^localhost/i.test(trimmed)) return `http://${trimmed}`
  if (/^\d+/.test(trimmed)) return `http://localhost:${trimmed}`
  return `https://${trimmed}`
}

function tabTitle(tab: BrowserTab): string {
  if (!tab.currentUrl) return label('context.previewStartTitle', 'New Tab')
  try {
    const parsed = new URL(tab.currentUrl)
    return parsed.pathname && parsed.pathname !== '/' ? `${parsed.host}${parsed.pathname}` : parsed.host
  } catch {
    return tab.currentUrl
  }
}

function addTab(url = ''): void {
  const next = createTab(url)
  tabs.value = [...tabs.value, next]
  activeTabId.value = next.id
  if (next.currentUrl) emit('update:url', next.currentUrl)
}

function closeTab(tabId: string): void {
  if (tabs.value.length <= 1) {
    goHome()
    return
  }
  const idx = tabs.value.findIndex((item) => item.id === tabId)
  tabs.value = tabs.value.filter((item) => item.id !== tabId)
  if (activeTabId.value === tabId) {
    const fallback = tabs.value[Math.max(0, idx - 1)] ?? tabs.value[0]
    activeTabId.value = fallback.id
    emit('update:url', fallback.currentUrl)
  }
}

function navigate(url?: string): void {
  const tab = activeTab.value
  const target = normalizeUrl(url ?? tab.urlInput)
  if (!target) return

  if (tab.historyIndex < tab.history.length - 1) {
    tab.history = tab.history.slice(0, tab.historyIndex + 1)
  }
  tab.history.push(target)
  tab.historyIndex = tab.history.length - 1

  tab.currentUrl = target
  tab.urlInput = target
  tab.loading = true
  tab.iframeKey++
  emit('update:url', target)
}

function goBack(): void {
  const tab = activeTab.value
  if (!canGoBack.value) return
  tab.historyIndex--
  const url = tab.history[tab.historyIndex]
  tab.currentUrl = url
  tab.urlInput = url
  tab.loading = true
  tab.iframeKey++
  emit('update:url', url)
}

function goForward(): void {
  const tab = activeTab.value
  if (!canGoForward.value) return
  tab.historyIndex++
  const url = tab.history[tab.historyIndex]
  tab.currentUrl = url
  tab.urlInput = url
  tab.loading = true
  tab.iframeKey++
  emit('update:url', url)
}

function refresh(): void {
  const tab = activeTab.value
  if (!tab.currentUrl) return
  tab.loading = true
  tab.iframeKey++
}

function onLoad(tab: BrowserTab): void {
  tab.loading = false
}

function onError(tab: BrowserTab): void {
  tab.loading = false
  notify.error(label('context.previewLoadError', 'Failed to load preview page'), { source: 'preview' })
}

function openExternal(): void {
  if (!activeTab.value.currentUrl) return
  window.open(activeTab.value.currentUrl, '_blank', 'noopener')
}

function goHome(): void {
  const tab = activeTab.value
  tab.urlInput = ''
  tab.currentUrl = ''
  tab.loading = false
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Enter') {
    event.preventDefault()
    navigate()
  }
}

watch(
  () => props.initialUrl,
  (url) => {
    if (url && !activeTab.value.currentUrl) {
      navigate(url)
    }
  },
  { immediate: true },
)

// --- Agent Browser Live Mirror state ---
const mirrorBridge = getAgentBrowserBridge()
const isElectronMirrorAvailable = Boolean(mirrorBridge)
const spaces = ref<AgentSpaceSummary[]>([])
const selectedSpaceId = ref<string | null>(null)
const previewFrame = ref<AgentPreviewFrame | null>(null)
const mirrorLoading = ref(false)
const mirrorBusy = ref(false)
const showConsoleErrors = ref(false)

let unsubscribeMirrorState: (() => void) | null = null
let mirrorTimer: number | null = null

const selectedSpace = computed<AgentSpaceSummary | null>(() => {
  if (spaces.value.length === 0) return null
  return spaces.value.find((item) => item.space_id === selectedSpaceId.value) ?? spaces.value[0]
})

const activeConsoleErrors = computed<AgentConsoleError[]>(() => {
  if (previewFrame.value?.console_errors?.length) return previewFrame.value.console_errors
  return selectedSpace.value?.console_errors ?? []
})

function formatSpaceLabel(space: AgentSpaceSummary): string {
  if (space.task_space && space.task_space !== 'default') {
    return space.task_space
  }
  const id = space.space_id || space.session_id
  return id.length > 18 ? `${id.slice(0, 18)}…` : id
}

function controlBadgeText(control: ControlState): string {
  if (control === 'user') return label('context.previewMirrorWaitingUser', '等待人工接管')
  if (control === 'finished') return label('context.previewMirrorFinished', '任务已结束')
  return label('context.previewMirrorAgentControl', 'Agent 控制中')
}

function formatCapturedTime(iso?: string): string {
  if (!iso) return ''
  try {
    return new Date(iso).toLocaleTimeString()
  } catch {
    return ''
  }
}

function syncSelectedSpace(list: AgentSpaceSummary[]): void {
  spaces.value = Array.isArray(list) ? list : []
  if (spaces.value.length === 0) {
    selectedSpaceId.value = null
    previewFrame.value = null
    return
  }
  if (!selectedSpaceId.value || !spaces.value.some((item) => item.space_id === selectedSpaceId.value)) {
    selectedSpaceId.value = spaces.value[0].space_id
  }
}

async function refreshSpaces(): Promise<void> {
  if (!mirrorBridge) return
  try {
    const list = await mirrorBridge.listSpaces()
    syncSelectedSpace(list)
  } catch {
    // Graceful degradation when host bridge is unavailable.
  }
}

async function refreshMirrorPreview(): Promise<void> {
  if (!mirrorBridge || !selectedSpace.value) {
    previewFrame.value = null
    return
  }
  const targetId = selectedSpace.value.space_id
  mirrorLoading.value = true
  try {
    const frame = await mirrorBridge.capturePreview(targetId)
    if (selectedSpace.value?.space_id === targetId) {
      previewFrame.value = frame
    }
  } catch {
    // Ignore transient capture errors if window closed mid-capture.
  } finally {
    mirrorLoading.value = false
  }
}

async function refreshMirrorAll(): Promise<void> {
  await refreshSpaces()
  await refreshMirrorPreview()
}

function startMirrorPolling(): void {
  if (!mirrorBridge || mirrorTimer !== null) return
  mirrorTimer = window.setInterval(() => {
    if (panelMode.value === 'mirror' && selectedSpace.value) {
      void refreshMirrorPreview()
    }
  }, 1500)
}

function stopMirrorPolling(): void {
  if (mirrorTimer !== null) {
    window.clearInterval(mirrorTimer)
    mirrorTimer = null
  }
}

async function handleOpenSpaceWindow(): Promise<void> {
  if (!mirrorBridge || !selectedSpace.value) return
  mirrorBusy.value = true
  try {
    const result = await mirrorBridge.openWindow(selectedSpace.value.space_id)
    if (!result?.ok) {
      notify.error(result?.message || label('context.previewMirrorWindowError', '无法打开独立浏览器窗口'), { source: 'preview' })
    }
  } catch {
    notify.error(label('context.previewMirrorWindowError', '无法打开独立浏览器窗口'), { source: 'preview' })
  } finally {
    mirrorBusy.value = false
  }
}

async function handleHandOff(): Promise<void> {
  if (!mirrorBridge || !selectedSpace.value) return
  mirrorBusy.value = true
  try {
    const result = await mirrorBridge.handOff(selectedSpace.value.space_id, 'manual_takeover')
    if (!result?.ok) {
      notify.error(result?.message || label('context.previewMirrorHandoffError', '接管控制失败'), { source: 'preview' })
    }
    await refreshMirrorAll()
  } catch {
    notify.error(label('context.previewMirrorHandoffError', '接管控制失败'), { source: 'preview' })
  } finally {
    mirrorBusy.value = false
  }
}

async function handleTakeOver(): Promise<void> {
  if (!mirrorBridge || !selectedSpace.value) return
  mirrorBusy.value = true
  try {
    const result = await mirrorBridge.takeOver(selectedSpace.value.space_id)
    if (!result?.ok) {
      notify.error(result?.message || label('context.previewMirrorTakeoverError', '交回智能体失败'), { source: 'preview' })
    }
    await refreshMirrorAll()
  } catch {
    notify.error(label('context.previewMirrorTakeoverError', '交回智能体失败'), { source: 'preview' })
  } finally {
    mirrorBusy.value = false
  }
}

watch(panelMode, (mode) => {
  if (mode === 'mirror') {
    void refreshMirrorAll()
    startMirrorPolling()
  } else {
    stopMirrorPolling()
  }
})

watch(selectedSpaceId, () => {
  if (panelMode.value === 'mirror') {
    void refreshMirrorPreview()
  }
})

onMounted(() => {
  if (!mirrorBridge) return
  void refreshSpaces()
  try {
    unsubscribeMirrorState = mirrorBridge.onStateChanged((payload) => {
      if (Array.isArray(payload?.spaces)) {
        syncSelectedSpace(payload.spaces)
      } else {
        void refreshSpaces()
      }
      if (panelMode.value === 'mirror' && selectedSpace.value) {
        void refreshMirrorPreview()
      }
    })
  } catch {
    unsubscribeMirrorState = null
  }
})

const {
  activeEndpoint,
  activePeerDevice,
  activePeerDevServerLeases,
  isActivePeerOfflineReadonly,
  requestDevServerLease,
  releaseDevServerLease,
} = usePeerDevices()

const isPeerEndpointActive = computed(() => activeEndpoint.value.kind === 'peer')
const remotePortInput = ref('5173')
const leaseMappingBusy = ref(false)
const leaseReleasingBusy = ref(false)
const leaseClockNow = ref(Date.now())
let leaseCountdownTimer: number | null = null

const currentPeerLease = computed<PeerDevServerLeaseDto | null>(
  () => activePeerDevServerLeases.value[0] ?? null,
)

const leaseRemainingSeconds = computed<number>(() => {
  const lease = currentPeerLease.value
  if (!lease) return 0
  return Math.max(0, Math.floor((lease.expires_at_ms - leaseClockNow.value) / 1000))
})

const leaseCountdownText = computed<string>(() => {
  const total = leaseRemainingSeconds.value
  const mins = Math.floor(total / 60)
  const secs = total % 60
  return `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`
})

function syncLeaseTimer(): void {
  if (currentPeerLease.value && leaseCountdownTimer === null) {
    leaseClockNow.value = Date.now()
    leaseCountdownTimer = window.setInterval(() => {
      leaseClockNow.value = Date.now()
    }, 1000)
  } else if (!currentPeerLease.value && leaseCountdownTimer !== null) {
    window.clearInterval(leaseCountdownTimer)
    leaseCountdownTimer = null
  }
}

watch(currentPeerLease, () => {
  syncLeaseTimer()
}, { immediate: true })

async function handleMapRemoteDevServer(presetPort?: number): Promise<void> {
  if (activeEndpoint.value.kind !== 'peer' || leaseMappingBusy.value) return
  if (presetPort !== undefined) {
    remotePortInput.value = String(presetPort)
  }
  const parsedPort = Number.parseInt(remotePortInput.value.trim(), 10)
  if (!Number.isInteger(parsedPort) || parsedPort < 1024 || parsedPort > 65535) {
    notify.error(label('peers.devServer.invalidPortError', 'Enter a valid remote user port between 1024 and 65535.'), {
      source: 'preview',
    })
    return
  }
  leaseMappingBusy.value = true
  try {
    const lease = await requestDevServerLease(activeEndpoint.value.deviceId, parsedPort)
    panelMode.value = 'tabs'
    navigate(lease.preview_url)
  } catch (err) {
    notify.error(err instanceof Error ? err.message : label('peers.devServer.mapFailedError', 'Failed to map remote DevServer preview.'), {
      source: 'preview',
    })
  } finally {
    leaseMappingBusy.value = false
  }
}

async function handleReleaseRemoteDevServer(): Promise<void> {
  const lease = currentPeerLease.value
  if (!lease || leaseReleasingBusy.value) return
  leaseReleasingBusy.value = true
  try {
    await releaseDevServerLease(lease.device_id, lease.lease_id)
    if (activeTab.value.currentUrl.startsWith(lease.preview_url)) {
      goHome()
    }
  } catch (err) {
    notify.error(err instanceof Error ? err.message : label('peers.devServer.releaseFailedError', 'Failed to close DevServer mapping.'), {
      source: 'preview',
    })
  } finally {
    leaseReleasingBusy.value = false
  }
}

onBeforeUnmount(() => {
  stopMirrorPolling()
  if (leaseCountdownTimer !== null) {
    window.clearInterval(leaseCountdownTimer)
    leaseCountdownTimer = null
  }
  try {
    unsubscribeMirrorState?.()
  } catch {
    // Best effort cleanup.
  }
  unsubscribeMirrorState = null
})
</script>

<template>
  <section class="panel preview-browser-panel" data-testid="preview-browser-panel">
    <!-- Top mode switch bar -->
    <div class="preview-mode-bar">
      <div class="preview-mode-segmented" role="tablist" :aria-label="label('context.previewModeToggle', 'Preview mode')">
        <button
          type="button"
          role="tab"
          class="preview-mode-btn"
          :class="{ active: panelMode === 'tabs' }"
          :aria-selected="panelMode === 'tabs'"
          data-testid="preview-mode-tabs"
          @click="panelMode = 'tabs'"
        >
          <Layers :size="13" aria-hidden="true" />
          <span>{{ label('context.previewTabMode', '预览标签页') }}</span>
        </button>
        <button
          type="button"
          role="tab"
          class="preview-mode-btn"
          :class="{ active: panelMode === 'mirror' }"
          :aria-selected="panelMode === 'mirror'"
          data-testid="preview-mode-mirror"
          @click="panelMode = 'mirror'"
        >
          <Bot :size="13" aria-hidden="true" />
          <span>{{ label('context.previewMirrorMode', 'Agent 实况镜像') }}</span>
          <span v-if="spaces.length > 0" class="preview-mode-count">{{ spaces.length }}</span>
        </button>
      </div>
    </div>

    <!-- Mode 1: Multi-tab Preview Browser -->
    <template v-if="panelMode === 'tabs'">
      <!-- Multi-tab bar -->
      <div class="preview-tabbar" role="tablist">
        <div
          v-for="tab in tabs"
          :key="tab.id"
          class="preview-tab"
          :class="{ active: tab.id === activeTab.id }"
          role="tab"
          :aria-selected="tab.id === activeTab.id"
          @click="activeTabId = tab.id"
        >
          <Globe :size="12" class="preview-tab-icon" aria-hidden="true" />
          <span class="preview-tab-title" :title="tab.currentUrl || tabTitle(tab)">{{ tabTitle(tab) }}</span>
          <button
            type="button"
            class="preview-tab-close"
            :aria-label="label('context.previewCloseTab', 'Close tab')"
            @click.stop="closeTab(tab.id)"
          >
            <X :size="11" aria-hidden="true" />
          </button>
        </div>
        <button
          type="button"
          class="preview-tab-add"
          :title="label('context.previewNewTab', 'New tab')"
          :aria-label="label('context.previewNewTab', 'New tab')"
          data-testid="preview-add-tab"
          @click="addTab()"
        >
          <Plus :size="13" aria-hidden="true" />
        </button>
      </div>

      <!-- Navigation & viewport toolbar -->
      <div class="preview-toolbar">
        <button class="preview-nav-btn" :disabled="!canGoBack" :title="label('context.previewBack', 'Back')" @click="goBack">
          <ArrowLeft :size="15" />
        </button>
        <button class="preview-nav-btn preview-nav-secondary" :disabled="!canGoForward" :title="label('context.previewForward', 'Forward')" @click="goForward">
          <ArrowRight :size="15" />
        </button>
        <button class="preview-nav-btn" :title="label('context.previewRefresh', 'Refresh')" @click="refresh">
          <RefreshCw :size="15" :class="{ spinning: activeTab.loading }" />
        </button>
        <button class="preview-nav-btn preview-nav-secondary" :title="label('context.previewHome', 'Home')" @click="goHome">
          <HomeIcon :size="15" />
        </button>

        <div class="preview-url-bar">
          <Globe :size="13" class="preview-url-icon" />
          <input
            v-model="activeTab.urlInput"
            class="preview-url-input"
            :placeholder="label('context.previewUrlPlaceholder', 'Enter URL...')"
            @keydown="onKeydown"
          />
        </div>

        <div class="preview-viewport-group" role="group">
          <button
            type="button"
            class="preview-nav-btn"
            :class="{ active: viewport === 'desktop' }"
            :title="label('context.previewViewportDesktop', 'Desktop')"
            data-testid="preview-viewport-desktop"
            @click="viewport = 'desktop'"
          >
            <Monitor :size="14" />
          </button>
          <button
            type="button"
            class="preview-nav-btn"
            :class="{ active: viewport === 'tablet' }"
            :title="label('context.previewViewportTablet', 'Tablet')"
            data-testid="preview-viewport-tablet"
            @click="viewport = 'tablet'"
          >
            <Tablet :size="14" />
          </button>
          <button
            type="button"
            class="preview-nav-btn"
            :class="{ active: viewport === 'mobile' }"
            :title="label('context.previewViewportMobile', 'Mobile')"
            data-testid="preview-viewport-mobile"
            @click="viewport = 'mobile'"
          >
            <Smartphone :size="14" />
          </button>
          <button
            type="button"
            class="preview-nav-btn"
            :class="{ active: viewport === 'custom' }"
            :title="label('context.previewViewportCustom', 'Custom size')"
            data-testid="preview-viewport-custom"
            @click="viewport = 'custom'"
          >
            <SlidersHorizontal :size="14" />
          </button>
        </div>

        <button class="preview-nav-btn" :title="label('context.previewOpenExternal', 'Open external')" @click="openExternal">
          <ExternalLink :size="15" />
        </button>
      </div>

      <!-- Remote Peer DevServer ALPN Tunnel Mapping Bar (shown ONLY when activeEndpoint.kind === 'peer') -->
      <div
        v-if="isPeerEndpointActive"
        class="preview-peer-lease-bar"
        data-testid="preview-peer-lease-bar"
      >
        <span class="preview-peer-lease-title">
          <Link2 :size="12" aria-hidden="true" />
          <span>{{ label('peers.devServer.barTitle', 'Map Remote Service') }}</span>
          <strong class="preview-peer-device-tag">{{ activePeerDevice?.display_name || (activeEndpoint.kind === 'peer' ? activeEndpoint.deviceId : '') }}</strong>
        </span>

        <div class="preview-peer-port-controls">
          <input
            v-model="remotePortInput"
            type="number"
            min="1024"
            max="65535"
            class="preview-peer-port-input"
            :disabled="isActivePeerOfflineReadonly || leaseMappingBusy"
            :placeholder="label('peers.devServer.portPlaceholder', '5173')"
            :aria-label="label('peers.devServer.portLabel', 'Remote DevServer port')"
            data-testid="preview-peer-port-input"
            @keydown.enter.prevent="handleMapRemoteDevServer()"
          />
          <button
            type="button"
            class="preview-peer-port-preset"
            :disabled="isActivePeerOfflineReadonly || leaseMappingBusy"
            @click="handleMapRemoteDevServer(5173)"
          >:5173</button>
          <button
            type="button"
            class="preview-peer-port-preset"
            :disabled="isActivePeerOfflineReadonly || leaseMappingBusy"
            @click="handleMapRemoteDevServer(3000)"
          >:3000</button>
          <button
            type="button"
            class="preview-peer-map-btn"
            :disabled="isActivePeerOfflineReadonly || leaseMappingBusy"
            data-testid="preview-map-remote-btn"
            @click="handleMapRemoteDevServer()"
          >
            <span>{{ leaseMappingBusy ? label('peers.devServer.mappingBtn', 'Mapping…') : label('peers.devServer.mapBtn', 'Map Preview') }}</span>
          </button>
        </div>

        <div
          v-if="currentPeerLease"
          class="preview-peer-active-lease"
          data-testid="preview-active-lease-pill"
        >
          <button
            type="button"
            class="preview-peer-lease-link"
            :title="currentPeerLease.preview_url"
            @click="navigate(currentPeerLease.preview_url)"
          >
            <code>:{{ currentPeerLease.target_port }} → 127.0.0.1:{{ currentPeerLease.local_port }}</code>
          </button>
          <span class="preview-peer-lease-ttl" data-testid="preview-lease-countdown">
            {{ leaseCountdownText }}
          </span>
          <button
            type="button"
            class="preview-peer-release-btn"
            :disabled="leaseReleasingBusy"
            :title="label('peers.devServer.releaseBtn', 'Close mapping')"
            data-testid="preview-release-lease-btn"
            @click="handleReleaseRemoteDevServer"
          >
            <X :size="11" aria-hidden="true" />
            <span>{{ label('peers.devServer.releaseBtn', 'Close mapping') }}</span>
          </button>
        </div>
      </div>

      <div
        v-if="viewport === 'custom'"
        class="preview-custom-bar"
        data-testid="preview-custom-viewport-bar"
      >
        <label class="preview-dim-field">
          <span>{{ label('context.previewCustomWidth', 'W') }}</span>
          <input
            type="number"
            min="240"
            max="2560"
            step="10"
            class="preview-dim-input"
            :value="customWidth"
            :aria-label="label('context.previewCustomWidth', 'Width')"
            data-testid="preview-custom-width"
            @change="updateCustomWidth(($event.target as HTMLInputElement).value)"
          />
        </label>
        <span class="preview-dim-sep" aria-hidden="true">×</span>
        <label class="preview-dim-field" :class="{ 'is-muted': customAutoHeight }">
          <span>{{ label('context.previewCustomHeight', 'H') }}</span>
          <input
            type="number"
            min="320"
            max="2160"
            step="10"
            class="preview-dim-input"
            :value="customHeight"
            :disabled="customAutoHeight"
            :aria-label="label('context.previewCustomHeight', 'Height')"
            data-testid="preview-custom-height"
            @change="updateCustomHeight(($event.target as HTMLInputElement).value)"
          />
        </label>
        <button
          type="button"
          class="preview-dim-chip"
          :class="{ active: customAutoHeight }"
          data-testid="preview-custom-auto-height"
          @click="customAutoHeight = !customAutoHeight"
        >
          {{ label('context.previewCustomAutoHeight', 'Auto H') }}
        </button>
        <button
          type="button"
          class="preview-nav-btn"
          :title="label('context.previewCustomRotate', 'Swap width & height')"
          data-testid="preview-custom-rotate"
          @click="swapCustomDimensions"
        >
          <RotateCw :size="13" />
        </button>
        <span class="preview-dim-summary">
          {{ customWidth }} × {{ customAutoHeight ? '100%' : `${customHeight}px` }}
        </span>
      </div>

      <!-- Content area -->
      <div class="preview-content" :class="`viewport-${viewport}`">
        <div v-if="!activeTab.currentUrl" class="preview-start">
          <Globe :size="36" class="preview-start-icon" />
          <h3>{{ label('context.previewStartTitle', 'Preview Browser') }}</h3>
          <p>{{ label('context.previewStartHint', 'Enter a local or web address to preview.') }}</p>
          <div class="preview-quick-links">
            <button
              v-for="link in quickLinks"
              :key="link.url"
              type="button"
              class="preview-quick-link"
              :title="link.url"
              @click="navigate(link.url)"
            >
              <Globe :size="14" />
              <span>{{ link.label }}</span>
              <small>{{ link.url }}</small>
            </button>
          </div>
        </div>

        <div v-if="activeTab.loading && activeTab.currentUrl" class="preview-loading">
          <RefreshCw :size="20" class="spinning" />
          <span>{{ label('context.previewLoading', 'Loading…') }}</span>
        </div>

        <div
          v-if="activeTab.currentUrl"
          class="preview-frame-shell"
          :class="`is-${viewport}`"
          :style="previewFrameStyle"
        >
          <iframe
            :key="`${activeTab.id}-${activeTab.iframeKey}`"
            :src="activeTab.currentUrl"
            class="preview-iframe"
            sandbox="allow-scripts allow-same-origin allow-forms allow-popups"
            referrerpolicy="no-referrer"
            @load="onLoad(activeTab)"
            @error="onError(activeTab)"
          />
        </div>
      </div>
    </template>

    <!-- Mode 2: Agent Browser Live Mirror -->
    <div v-else class="mirror-workspace" data-testid="agent-browser-mirror">
      <!-- Standalone Web-mode graceful fallback -->
      <div v-if="!isElectronMirrorAvailable" class="mirror-empty-card" data-testid="mirror-web-fallback">
        <div class="mirror-empty-icon">
          <Monitor :size="24" aria-hidden="true" />
        </div>
        <h3>{{ label('context.previewMirrorWebTitle', 'Agent 实况镜像仅在桌面客户端可用') }}</h3>
        <p>{{ label('context.previewMirrorWebDesc', '当前运行于独立 Web 模式，未检测到 Electron 浏览器宿主。请使用 TinadecOffice 桌面客户端查看智能体实时画面并一键接管控制，或切换回预览标签页继续浏览。') }}</p>
        <button type="button" class="mirror-action-btn" @click="panelMode = 'tabs'">
          <Layers :size="13" aria-hidden="true" />
          <span>{{ label('context.previewBackToTabs', '返回预览标签页') }}</span>
        </button>
      </div>

      <!-- Electron active, no TaskSpaces running yet -->
      <div v-else-if="!selectedSpace" class="mirror-empty-card" data-testid="mirror-empty-state">
        <div class="mirror-empty-icon">
          <Bot :size="24" aria-hidden="true" />
        </div>
        <h3>{{ label('context.previewMirrorEmptyTitle', '暂无运行中的 Agent 任务空间') }}</h3>
        <p>{{ label('context.previewMirrorEmptyDesc', '当智能体启动浏览器任务时，这里会自动显示每个隔离 TaskSpace 的实时画面、URL 状态与人工接管控制入口。') }}</p>
        <button type="button" class="mirror-action-btn" @click="refreshMirrorAll">
          <RefreshCw :size="13" :class="{ spinning: mirrorLoading }" aria-hidden="true" />
          <span>{{ label('context.previewMirrorRefresh', '刷新列表') }}</span>
        </button>
      </div>

      <!-- Active Electron TaskSpaces mirror view -->
      <template v-else>
        <!-- TaskSpace selector bar -->
        <div class="mirror-space-strip" role="tablist">
          <button
            v-for="space in spaces"
            :key="space.space_id"
            type="button"
            role="tab"
            class="mirror-space-pill"
            :class="{ active: space.space_id === selectedSpace.space_id }"
            :aria-selected="space.space_id === selectedSpace.space_id"
            :title="space.space_id"
            @click="selectedSpaceId = space.space_id"
          >
            <span class="mirror-dot" :class="`is-${space.control}`" aria-hidden="true" />
            <span class="mirror-space-name">{{ formatSpaceLabel(space) }}</span>
            <span class="mirror-space-tabs">{{ space.tab_count }}</span>
          </button>
        </div>

        <!-- Active space info & control bar -->
        <div class="mirror-header-card">
          <div class="mirror-meta-row">
            <span class="mirror-control-badge" :class="`is-${selectedSpace.control}`" data-testid="mirror-control-badge">
              {{ controlBadgeText(selectedSpace.control) }}
            </span>
            <div class="mirror-title-block">
              <strong class="mirror-page-title" :title="selectedSpace.title || selectedSpace.url">
                {{ selectedSpace.title || label('context.previewMirrorUntitled', '未命名页面') }}
              </strong>
              <span class="mirror-page-url" :title="selectedSpace.url">
                {{ selectedSpace.url || 'about:blank' }}
              </span>
            </div>
            <button
              type="button"
              class="preview-nav-btn"
              :title="label('context.previewMirrorRefresh', '刷新画面')"
              @click="refreshMirrorAll"
            >
              <RefreshCw :size="14" :class="{ spinning: mirrorLoading }" />
            </button>
          </div>

          <div class="mirror-actions-row">
            <button
              type="button"
              class="mirror-action-btn"
              :disabled="mirrorBusy"
              data-testid="mirror-open-window"
              @click="handleOpenSpaceWindow"
            >
              <ExternalLink :size="13" aria-hidden="true" />
              <span>{{ label('context.previewMirrorOpenWindow', '打开独立窗口') }}</span>
            </button>

            <button
              v-if="selectedSpace.control === 'agent'"
              type="button"
              class="mirror-action-btn is-handoff"
              :disabled="mirrorBusy"
              data-testid="mirror-handoff-btn"
              @click="handleHandOff"
            >
              <Hand :size="13" aria-hidden="true" />
              <span>{{ label('context.previewMirrorHandOff', '接管控制 (Hand Off)') }}</span>
            </button>

            <button
              v-if="selectedSpace.control === 'user'"
              type="button"
              class="mirror-action-btn is-takeover"
              :disabled="mirrorBusy"
              data-testid="mirror-takeover-btn"
              @click="handleTakeOver"
            >
              <Play :size="13" aria-hidden="true" />
              <span>{{ label('context.previewMirrorTakeOver', '交回智能体 (Take Over)') }}</span>
            </button>

            <span v-if="selectedSpace.control === 'finished'" class="mirror-finished-hint">
              {{ label('context.previewMirrorFinishedHint', '任务空间已终结，不可恢复') }}
            </span>

            <button
              v-if="activeConsoleErrors.length > 0"
              type="button"
              class="mirror-error-pill"
              @click="showConsoleErrors = !showConsoleErrors"
            >
              <AlertTriangle :size="12" aria-hidden="true" />
              <span>{{ activeConsoleErrors.length }} {{ label('context.previewMirrorConsoleErrors', '控制台错误') }}</span>
            </button>
          </div>
        </div>

        <!-- Open JS Dialog notice banner -->
        <div v-if="selectedSpace.dialog" class="mirror-dialog-banner" role="status">
          <AlertTriangle :size="14" aria-hidden="true" />
          <span>
            {{ label('context.previewMirrorDialogBanner', '页面弹出对话框，已自动拦截阻塞型 CDP 调用') }}
            <template v-if="selectedSpace.dialog.message">：{{ selectedSpace.dialog.message }}</template>
          </span>
        </div>

        <!-- Optional console errors diagnostics drawer -->
        <div v-if="showConsoleErrors && activeConsoleErrors.length > 0" class="mirror-errors-card">
          <div v-for="(err, idx) in activeConsoleErrors" :key="err.seq ?? idx" class="mirror-error-line">
            <span class="mirror-error-src">{{ err.source || err.level || 'error' }}</span>
            <span class="mirror-error-msg">{{ err.message }}</span>
          </div>
        </div>

        <!-- Live screenshot / frame surface -->
        <div class="mirror-stage">
          <img
            v-if="previewFrame?.data_url"
            :src="previewFrame.data_url"
            :alt="selectedSpace.title || selectedSpace.url || 'Agent Browser Mirror'"
            class="mirror-image"
          />
          <div v-else class="mirror-stage-placeholder">
            <RefreshCw v-if="mirrorLoading" :size="18" class="spinning" aria-hidden="true" />
            <Globe v-else :size="22" aria-hidden="true" />
            <span>{{ mirrorLoading ? label('context.previewLoading', '正在捕获实况画面…') : label('context.previewMirrorNoFrame', '等待页面首帧画面') }}</span>
          </div>
          <div v-if="previewFrame?.captured_at" class="mirror-stage-footer">
            <span>{{ selectedSpace.space_id }}</span>
            <span>{{ formatCapturedTime(previewFrame.captured_at) }}</span>
          </div>
        </div>
      </template>
    </div>
  </section>
</template>

<style scoped>
.preview-browser-panel {
  display: flex;
  flex-direction: column;
  min-width: 0;
  height: 100%;
  overflow: hidden;
}

.preview-mode-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 8px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border-muted);
  flex-shrink: 0;
}

.preview-mode-segmented {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-primary));
}

.preview-mode-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.15s, color 0.15s, border-color 0.15s;
}

.preview-mode-btn:hover {
  color: var(--text-primary);
}

.preview-mode-btn.active {
  background: var(--surface-active, var(--bg-secondary));
  border-color: var(--border-muted);
  color: var(--text-primary);
  font-weight: 600;
}

.preview-mode-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
  font-size: 10px;
  font-weight: 600;
}

.preview-tabbar {
  display: flex;
  align-items: center;
  gap: 3px;
  padding: 4px 6px 0;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border-muted);
  overflow-x: auto;
  flex-shrink: 0;
}

.preview-tab {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: 170px;
  padding: 5px 8px;
  border-radius: 10px 10px 0 0;
  border: 1px solid transparent;
  border-bottom: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.preview-tab.active {
  background: var(--surface-active, var(--bg-secondary));
  border-color: var(--border-muted);
  color: var(--text-primary);
  font-weight: 600;
}

.preview-tab-icon { flex-shrink: 0; color: var(--text-muted); }
.preview-tab-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.preview-tab-close {
  display: grid; place-items: center; width: 16px; height: 16px;
  border: 0; border-radius: 4px; background: transparent; color: var(--text-muted); cursor: pointer;
}
.preview-tab-close:hover { background: var(--surface-hover); color: var(--text-primary); }

.preview-tab-add {
  display: grid; place-items: center; width: 24px; height: 24px;
  border: 0; border-radius: 6px; background: transparent; color: var(--text-secondary); cursor: pointer; flex-shrink: 0;
}
.preview-tab-add:hover { background: var(--surface-hover); color: var(--text-primary); }

.preview-toolbar {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 6px 8px;
  background: var(--surface-section, var(--bg-secondary));
  border-bottom: 1px solid var(--border-muted);
  flex-shrink: 0;
  flex-wrap: wrap;
}

.preview-nav-btn {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
  flex-shrink: 0;
}

.preview-nav-btn:hover:not(:disabled),
.preview-nav-btn.active {
  background: var(--surface-hover);
  color: var(--text-primary);
}
.preview-nav-btn.active { color: var(--accent-primary); }
.preview-nav-btn:disabled { opacity: 0.35; cursor: not-allowed; }

.preview-url-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  flex: 1 1 120px;
  min-width: 0;
  padding: 4px 8px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
}

.preview-url-icon { color: var(--text-muted); flex-shrink: 0; }
.preview-url-input {
  width: 100%;
  min-width: 0;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  outline: none;
}

.preview-viewport-group {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 0 2px;
  border-left: 1px solid var(--border-muted);
  border-right: 1px solid var(--border-muted);
}

.preview-custom-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  padding: 5px 10px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border-muted);
  font-size: 11px;
  color: var(--text-secondary);
  flex-shrink: 0;
}

.preview-dim-field {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
}

.preview-dim-field.is-muted {
  opacity: 0.55;
}

.preview-dim-field span {
  font-size: 10px;
  font-weight: 600;
  color: var(--text-muted);
}

.preview-dim-input {
  width: 54px;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  outline: none;
}

.preview-dim-sep {
  color: var(--text-muted);
  font-size: 11px;
}

.preview-dim-chip {
  padding: 3px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.14s, color 0.14s, border-color 0.14s;
}

.preview-dim-chip:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.preview-dim-chip.active {
  background: var(--surface-section);
  border-color: var(--border-default, var(--border-muted));
  color: var(--text-primary);
  font-weight: 600;
}

.preview-dim-summary {
  margin-left: auto;
  font-family: var(--font-mono, monospace);
  font-size: 10.5px;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.preview-content {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  justify-content: center;
  background: var(--surface-canvas, var(--bg-primary));
  overflow: auto;
}

.preview-frame-shell {
  width: 100%;
  height: 100%;
  display: flex;
}
.preview-frame-shell.is-tablet {
  width: min(768px, 100%);
  border-left: 1px solid var(--border-muted);
  border-right: 1px solid var(--border-muted);
  box-shadow: var(--shadow-panel);
}
.preview-frame-shell.is-mobile {
  width: min(390px, 100%);
  border-left: 1px solid var(--border-muted);
  border-right: 1px solid var(--border-muted);
  box-shadow: var(--shadow-panel);
}
.preview-frame-shell.is-custom {
  border-left: 1px solid var(--border-muted);
  border-right: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
  box-shadow: var(--shadow-panel);
  flex-shrink: 0;
}

.preview-iframe {
  width: 100%;
  height: 100%;
  border: 0;
  background: #fff;
}

.preview-start {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 24px 16px;
  text-align: center;
  max-width: 420px;
  margin: auto;
}
.preview-start-icon { color: var(--text-muted); opacity: 0.7; }
.preview-start h3 { margin: 0; font-size: 15px; color: var(--text-primary); }
.preview-start p { margin: 0; font-size: 12px; color: var(--text-secondary); line-height: 1.5; }
.preview-quick-links {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 8px;
}
.preview-quick-link {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 12px;
  cursor: pointer;
  text-align: left;
}
.preview-quick-link:hover { background: var(--surface-hover); border-color: var(--accent-primary); }
.preview-quick-link small { margin-left: auto; color: var(--text-muted); font-family: var(--font-mono); font-size: 10.5px; }

.preview-loading {
  position: absolute;
  inset: 0;
  z-index: 2;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  background: var(--surface-section, color-mix(in srgb, var(--bg-primary) 75%, transparent));
  color: var(--text-secondary);
  font-size: 12px;
}

/* --- Agent Browser Live Mirror --- */
.mirror-workspace {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  background: var(--surface-canvas, var(--bg-primary));
  overflow: auto;
}

.mirror-empty-card {
  margin: auto;
  max-width: 380px;
  padding: 22px 20px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
  gap: 10px;
}

.mirror-empty-icon {
  width: 44px;
  height: 44px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  display: grid;
  place-items: center;
}

.mirror-empty-card h3 {
  margin: 0;
  font-size: 14px;
  color: var(--text-primary);
}

.mirror-empty-card p {
  margin: 0;
  font-size: 12px;
  line-height: 1.55;
  color: var(--text-secondary);
}

.mirror-space-strip {
  display: flex;
  align-items: center;
  gap: 6px;
  overflow-x: auto;
  flex-shrink: 0;
}

.mirror-space-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 10px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
  white-space: nowrap;
}

.mirror-space-pill.active {
  background: var(--surface-active, var(--bg-secondary));
  border-color: var(--accent-primary);
  color: var(--text-primary);
  font-weight: 600;
}

.mirror-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--accent-primary);
  flex-shrink: 0;
}

.mirror-dot.is-user {
  background: #d29922;
}

.mirror-dot.is-finished {
  background: var(--text-muted);
}

.mirror-space-tabs {
  padding: 0 5px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  font-size: 10px;
  color: var(--text-muted);
}

.mirror-header-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  flex-shrink: 0;
}

.mirror-meta-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.mirror-control-badge {
  display: inline-flex;
  align-items: center;
  padding: 3px 8px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  font-size: 11px;
  font-weight: 600;
  white-space: nowrap;
  flex-shrink: 0;
}

.mirror-control-badge.is-agent {
  border-color: color-mix(in srgb, var(--accent-primary) 45%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
  color: var(--accent-primary);
}

.mirror-control-badge.is-user {
  border-color: color-mix(in srgb, #d29922 50%, var(--border-muted));
  background: color-mix(in srgb, #d29922 14%, transparent);
  color: #d29922;
}

.mirror-control-badge.is-finished {
  border-color: var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-muted);
}

.mirror-title-block {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.mirror-page-title {
  font-size: 12.5px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mirror-page-url {
  font-size: 11px;
  font-family: var(--font-mono);
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mirror-actions-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
}

.mirror-action-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 10px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-button, var(--bg-secondary));
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}

.mirror-action-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  border-color: var(--accent-primary);
}

.mirror-action-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.mirror-action-btn.is-handoff {
  border-color: color-mix(in srgb, #d29922 45%, var(--border-muted));
  color: #d29922;
}

.mirror-action-btn.is-takeover {
  border-color: color-mix(in srgb, var(--accent-primary) 50%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
  color: var(--accent-primary);
}

.mirror-finished-hint {
  font-size: 11px;
  color: var(--text-muted);
}

.mirror-error-pill {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 8px;
  border-radius: 10px;
  border: 1px solid color-mix(in srgb, #f85149 45%, var(--border-muted));
  background: color-mix(in srgb, #f85149 12%, transparent);
  color: #f85149;
  font-size: 11px;
  cursor: pointer;
}

.mirror-dialog-banner {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 10px;
  border: 1px solid color-mix(in srgb, #d29922 45%, var(--border-muted));
  background: color-mix(in srgb, #d29922 10%, transparent);
  color: var(--text-primary);
  font-size: 11.5px;
  flex-shrink: 0;
}

.mirror-errors-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 120px;
  overflow: auto;
  padding: 8px 10px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  font-family: var(--font-mono);
  font-size: 11px;
  flex-shrink: 0;
}

.mirror-error-line {
  display: flex;
  gap: 6px;
  color: var(--text-secondary);
}

.mirror-error-src {
  color: #f85149;
  flex-shrink: 0;
}

.mirror-error-msg {
  overflow-wrap: anywhere;
}

.mirror-stage {
  position: relative;
  flex: 1;
  min-height: 220px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.mirror-image {
  flex: 1;
  min-height: 0;
  width: 100%;
  object-fit: contain;
  background: #111015;
}

.mirror-stage-placeholder {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--text-muted);
  font-size: 12px;
}

.mirror-stage-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 5px 10px;
  border-top: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-muted);
  font-family: var(--font-mono);
  font-size: 10.5px;
}

.spinning { animation: preview-spin 1s linear infinite; }
@keyframes preview-spin { to { transform: rotate(360deg); } }

.preview-peer-lease-bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 8px;
  padding: 5px 8px;
  background: var(--surface-raised);
  border-bottom: 1px solid var(--border-muted);
  font-size: 11px;
  color: var(--text-secondary);
  flex-shrink: 0;
}

.preview-peer-lease-title {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: var(--text-secondary);
  white-space: nowrap;
}

.preview-peer-device-tag {
  color: var(--text-primary);
  font-weight: 600;
}

.preview-peer-port-controls {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.preview-peer-port-input {
  width: 64px;
  padding: 3px 6px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
  color: var(--text-primary);
  font-size: 11px;
  font-family: var(--font-mono, monospace);
  font-variant-numeric: tabular-nums;
  outline: none;
}

.preview-peer-port-preset {
  padding: 2px 6px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, transparent);
  color: var(--text-secondary);
  font-family: var(--font-mono, monospace);
  font-size: 10.5px;
  cursor: pointer;
}

.preview-peer-port-preset:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.preview-peer-map-btn {
  padding: 3px 8px;
  border-radius: 6px;
  border: 1px solid transparent;
  background: var(--bg-primary-button, var(--text-primary));
  color: var(--text-on-primary, var(--bg-primary));
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}

.preview-peer-map-btn:disabled,
.preview-peer-port-preset:disabled,
.preview-peer-port-input:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.preview-peer-active-lease {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 2px 6px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.preview-peer-lease-link {
  border: 0;
  padding: 0;
  background: transparent;
  color: var(--text-primary);
  cursor: pointer;
  font-size: 11px;
}

.preview-peer-lease-link code {
  font-family: var(--font-mono, monospace);
  font-size: 10.5px;
}

.preview-peer-lease-ttl {
  color: var(--text-muted);
  font-family: var(--font-mono, monospace);
  font-size: 10.5px;
  font-variant-numeric: tabular-nums;
}

.preview-peer-release-btn {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 5px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 10.5px;
  cursor: pointer;
}

.preview-peer-release-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

@media (prefers-reduced-motion: reduce) {
  .spinning {
    animation: none;
  }
  .preview-mode-btn,
  .preview-nav-btn,
  .preview-dim-chip,
  .mirror-action-btn {
    transition: none;
  }
}
</style>
