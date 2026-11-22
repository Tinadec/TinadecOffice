<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Clock,
  MessageSquare,
  Pause,
  Pin,
  Play,
  RotateCcw,
  RotateCw,
  Sparkles,
  Wrench,
  X,
} from '@lucide/vue'
import TeammateSteerPopover, { type TeammateSteerTarget } from '../teammate/TeammateSteerPopover.vue'
import {
  api,
  type ModelInvocationDto,
  type OrchestrationSnapshotDto,
  type ToolExecutionTimelineItemDto,
  type TopologyRunDto,
} from '../../api'
import { useAgentDirectory } from '../../composables/useAgentDirectory'
import {
  buildAgentTimeline,
  computeCausalChain,
  type AgentTimelineEdge,
  type AgentTimelineModel,
  type AgentTimelineRow,
  type AgentTimelineSpan,
} from './agentTimelineModel'

export interface AgentTimelineProps {
  snapshot: OrchestrationSnapshotDto | null
  topologyRun?: TopologyRunDto | null
  toolExecutions?: ToolExecutionTimelineItemDto[]
  modelInvocations?: ModelInvocationDto[]
  selectedNodeId?: string | null
  live?: boolean
  nowMs?: number
}

const props = withDefaults(defineProps<AgentTimelineProps>(), {
  topologyRun: null,
  toolExecutions: () => [],
  modelInvocations: () => [],
  selectedNodeId: null,
  live: undefined,
  nowMs: undefined,
})

const emit = defineEmits<{
  'select-node': [nodeId: string | null]
  'pin-span': [spanId: string | null]
  'view-tool-details': [toolExecutionId: string]
}>()

const ROW_HEIGHT_PX = 40
const COMPACT_BREAKPOINT_PX = 520
const LIVE_CLOCK_THROTTLE_MS = 50

const { t, te, locale } = useI18n()
const { nameFor } = typeof api.listAgents === 'function'
  ? useAgentDirectory()
  : { nameFor: (slug: string | null | undefined, fallback?: string | null) => fallback ?? slug ?? '' }

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? t(key, params) : t(key)
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const steerTarget = ref<TeammateSteerTarget | null>(null)
const steerOpen = ref(false)
const pausedOverrides = ref<Record<string, boolean>>({})

const timelineRootRef = ref<HTMLElement | null>(null)
const trackColumnRef = ref<HTMLElement | null>(null)

const containerWidth = ref<number>(640)
const trackWidthPx = ref<number>(460)
const isCompactDock = computed(() => containerWidth.value > 0 && containerWidth.value < COMPACT_BREAKPOINT_PX)

const pinnedSpanId = ref<string | null>(null)
const hoveredSpanId = ref<string | null>(null)
const hoveredRowId = ref<string | null>(null)

// Replay state (for settled runs)
const replayPct = ref<number>(100)
const isReplaying = ref<boolean>(false)
const replaySpeed = ref<1 | 2>(1)

// Throttled numeric clock state for live mode (~20Hz max reactive writes)
const liveElapsedMs = ref<number>(0)

// rAF handles declared upfront before any immediate watchers to avoid TDZ
let liveRafId: number | null = null
let liveAnchorPerfMs = 0
let liveAnchorElapsedMs = 0
let lastClockReactiveWriteMs = 0
let replayRafId: number | null = null
let replayLastFrameMs = 0

const model = computed<AgentTimelineModel>(() =>
  buildAgentTimeline({
    snapshot: props.snapshot,
    topologyRun: props.topologyRun,
    toolExecutions: props.toolExecutions,
    modelInvocations: props.modelInvocations,
    nowMs: props.nowMs,
    coordinatorFallbackLabel: t('agentTimeline.role.coordinator'),
    locale: locale.value,
    resolveAgentName: nameFor,
  }),
)

const effectiveLive = computed<boolean>(() =>
  props.live !== undefined ? props.live : model.value.live,
)

const causalChain = computed(() => {
  if (!pinnedSpanId.value) return null
  return computeCausalChain(pinnedSpanId.value, model.value.spans, model.value.edges)
})

const rowIndexMap = computed(() => {
  const map = new Map<string, number>()
  model.value.rows.forEach((r, idx) => map.set(r.id, idx))
  return map
})

const spanByIdMap = computed(() => {
  const map = new Map<string, AgentTimelineSpan>()
  for (const s of model.value.spans) map.set(s.id, s)
  return map
})

const rowByIdMap = computed(() => {
  const map = new Map<string, AgentTimelineRow>()
  for (const r of model.value.rows) map.set(r.id, r)
  return map
})

function prefersReducedMotion(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

// Reset pin, steer popover & replay state when run changes
watch(
  () => model.value.runId,
  () => {
    pinnedSpanId.value = null
    hoveredSpanId.value = null
    hoveredRowId.value = null
    steerOpen.value = false
    steerTarget.value = null
    pausedOverrides.value = {}
    stopReplayRaf()
    isReplaying.value = false
    replayPct.value = 100
  },
)

// Clear stale pinnedSpanId if span disappears
watch(
  () => model.value.spans,
  (spans) => {
    if (pinnedSpanId.value && !spans.some((s) => s.id === pinnedSpanId.value)) {
      pinnedSpanId.value = null
    }
  },
)

// ── Live rAF driver (single CSS custom property `--timeline-now-pct`, zero per-frame Vue re-renders) ──
function setNowPctCssVar(pct: number): void {
  const root = timelineRootRef.value
  if (!root) return
  const clamped = Math.max(0, Math.min(100, pct))
  root.style.setProperty('--timeline-now-pct', clamped.toFixed(3))
}

function stopLiveRaf(): void {
  if (liveRafId !== null && typeof cancelAnimationFrame === 'function') {
    cancelAnimationFrame(liveRafId)
  }
  liveRafId = null
}

function startLiveRaf(): void {
  stopLiveRaf()
  const m = model.value
  liveAnchorPerfMs = typeof performance !== 'undefined' ? performance.now() : Date.now()
  liveAnchorElapsedMs = Math.max(100, Math.round(m.wallDurationMs * 0.86))
  liveElapsedMs.value = liveAnchorElapsedMs
  lastClockReactiveWriteMs = liveAnchorPerfMs

  if (prefersReducedMotion() || typeof requestAnimationFrame !== 'function') {
    setNowPctCssVar(100)
    return
  }

  setNowPctCssVar(86)

  const tick = (frameNow: number) => {
    if (!effectiveLive.value) {
      liveRafId = null
      return
    }
    const delta = Math.max(0, frameNow - liveAnchorPerfMs)
    const currentElapsed = liveAnchorElapsedMs + delta
    // Smooth monotone asymptotic growth from 86% -> 100% until Core settles the span
    const pct = 100 - 14 * Math.exp(-delta / 1800)

    // Strictly mutate ONE CSS custom property on DOM ref — zero Vue reactivity invalidation!
    setNowPctCssVar(pct)

    // Throttle numeric clock text updates to ~20Hz (every 50ms)
    if (frameNow - lastClockReactiveWriteMs >= LIVE_CLOCK_THROTTLE_MS) {
      lastClockReactiveWriteMs = frameNow
      liveElapsedMs.value = Math.round(currentElapsed)
    }

    liveRafId = requestAnimationFrame(tick)
  }

  liveRafId = requestAnimationFrame(tick)
}

watch(
  [effectiveLive, () => model.value.runId, () => model.value.endMs],
  ([liveNow]) => {
    if (liveNow) {
      stopReplayRaf()
      isReplaying.value = false
      startLiveRaf()
    } else {
      stopLiveRaf()
      setNowPctCssVar(100)
    }
  },
  { immediate: true },
)

// ── Settled Replay rAF Scrubber ──
function stopReplayRaf(): void {
  if (replayRafId !== null && typeof cancelAnimationFrame === 'function') {
    cancelAnimationFrame(replayRafId)
  }
  replayRafId = null
}

function startReplayRaf(): void {
  stopReplayRaf()
  if (prefersReducedMotion() || typeof requestAnimationFrame !== 'function') {
    replayPct.value = 100
    isReplaying.value = false
    return
  }

  if (replayPct.value >= 99.8) {
    replayPct.value = 0
  }
  isReplaying.value = true
  replayLastFrameMs = typeof performance !== 'undefined' ? performance.now() : Date.now()

  // Normalize playback duration between 2.2s and 8s at 1x so both sub-second and multi-minute runs scrub smoothly
  const playbackBaseMs = Math.min(8000, Math.max(2200, model.value.wallDurationMs))

  const step = (now: number) => {
    if (!isReplaying.value) {
      replayRafId = null
      return
    }
    const dt = Math.max(0, now - replayLastFrameMs)
    replayLastFrameMs = now
    const advancePct = ((dt * replaySpeed.value) / playbackBaseMs) * 100
    const next = replayPct.value + advancePct
    if (next >= 100) {
      replayPct.value = 100
      isReplaying.value = false
      replayRafId = null
      return
    }
    replayPct.value = Number(next.toFixed(2))
    replayRafId = requestAnimationFrame(step)
  }

  replayRafId = requestAnimationFrame(step)
}

function toggleReplayPlay(): void {
  if (effectiveLive.value) return
  if (isReplaying.value) {
    isReplaying.value = false
    stopReplayRaf()
  } else {
    startReplayRaf()
  }
}

function restartReplay(): void {
  if (effectiveLive.value) return
  stopReplayRaf()
  if (prefersReducedMotion()) {
    replayPct.value = 100
    isReplaying.value = false
    return
  }
  replayPct.value = 0
  startReplayRaf()
}

function toggleReplaySpeed(): void {
  replaySpeed.value = replaySpeed.value === 1 ? 2 : 1
}

function onScrubberInput(event: Event): void {
  const target = event.target as HTMLInputElement | null
  if (!target) return
  const parsed = Number(target.value)
  if (!Number.isFinite(parsed)) return
  if (isReplaying.value) {
    isReplaying.value = false
    stopReplayRaf()
  }
  replayPct.value = Math.max(0, Math.min(100, parsed))
}

function onScrubberKeydown(event: KeyboardEvent): void {
  if (event.key === 'Home') {
    event.preventDefault()
    isReplaying.value = false
    stopReplayRaf()
    replayPct.value = 0
  } else if (event.key === 'End') {
    event.preventDefault()
    isReplaying.value = false
    stopReplayRaf()
    replayPct.value = 100
  } else if (event.key === 'ArrowLeft' || event.key === 'ArrowDown') {
    event.preventDefault()
    isReplaying.value = false
    stopReplayRaf()
    replayPct.value = Math.max(0, Number((replayPct.value - 2).toFixed(1)))
  } else if (event.key === 'ArrowRight' || event.key === 'ArrowUp') {
    event.preventDefault()
    isReplaying.value = false
    stopReplayRaf()
    replayPct.value = Math.min(100, Number((replayPct.value + 2).toFixed(1)))
  } else if (event.key === 'Escape' && pinnedSpanId.value) {
    event.preventDefault()
    unpinSpan()
  }
}

// ── ResizeObserver for responsive 420px dock & crisp SVG connector pixel coordinates ──
let resizeObserver: ResizeObserver | null = null

function measureDimensions(explicitRootWidth?: number): void {
  const root = timelineRootRef.value
  if (typeof explicitRootWidth === 'number' && explicitRootWidth > 0) {
    containerWidth.value = Math.round(explicitRootWidth)
  } else if (root && root.clientWidth > 0) {
    containerWidth.value = Math.round(root.clientWidth)
  }
  const track = trackColumnRef.value
  if (track && track.clientWidth > 0) {
    trackWidthPx.value = Math.round(track.clientWidth)
  } else {
    const labelCol = containerWidth.value < COMPACT_BREAKPOINT_PX ? 104 : 172
    trackWidthPx.value = Math.max(200, containerWidth.value - labelCol - 16)
  }
}

onMounted(() => {
  measureDimensions()
  if (effectiveLive.value) {
    startLiveRaf()
  } else {
    setNowPctCssVar(100)
  }
  if (typeof ResizeObserver !== 'undefined' && timelineRootRef.value) {
    resizeObserver = new ResizeObserver((entries) => {
      for (const entry of entries) {
        if (entry.target === timelineRootRef.value) {
          measureDimensions(entry.contentRect.width)
        } else if (entry.target === trackColumnRef.value && entry.contentRect.width > 0) {
          trackWidthPx.value = Math.round(entry.contentRect.width)
        }
      }
    })
    resizeObserver.observe(timelineRootRef.value)
    if (trackColumnRef.value) {
      resizeObserver.observe(trackColumnRef.value)
    }
  }
})

onBeforeUnmount(() => {
  stopLiveRaf()
  stopReplayRaf()
  resizeObserver?.disconnect()
  resizeObserver = null
})

// ── Pin & Hover Interaction ──
function togglePinSpan(spanId: string): void {
  const next = pinnedSpanId.value === spanId ? null : spanId
  pinnedSpanId.value = next
  emit('pin-span', next)
}

function unpinSpan(): void {
  if (!pinnedSpanId.value) return
  pinnedSpanId.value = null
  emit('pin-span', null)
}

function onTimelineKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && pinnedSpanId.value) {
    event.preventDefault()
    event.stopPropagation()
    unpinSpan()
  }
}

function canSteerRow(row: AgentTimelineRow): boolean {
  return Boolean(model.value.runId && row.roleKind !== 'coordinator' && (row.handle || row.agentSlug))
}

function rowHandleFor(row: AgentTimelineRow): string {
  return (row.handle || row.agentSlug || '').trim()
}

function isRowPaused(row: AgentTimelineRow): boolean {
  const handle = rowHandleFor(row)
  if (handle && pausedOverrides.value[handle] !== undefined) return Boolean(pausedOverrides.value[handle])
  return false
}

function openRowSteerPopover(row: AgentTimelineRow, event?: Event): void {
  if (!canSteerRow(row) || !model.value.runId) return
  const handle = rowHandleFor(row)
  const el = (event?.currentTarget as HTMLElement | null) ?? null
  const rect = el?.getBoundingClientRect?.()
  const lastSpan = row.spans[row.spans.length - 1]
  steerTarget.value = {
    runId: model.value.runId,
    handle,
    displayName: row.label,
    status: isRowPaused(row) ? 'paused' : row.status,
    paused: isRowPaused(row),
    taskSummary: lastSpan ? displaySpanLabel(lastSpan) : null,
    anchorRect: rect && (rect.width > 0 || rect.height > 0)
      ? { top: rect.top, left: rect.left, width: rect.width, height: rect.height, right: rect.right, bottom: rect.bottom }
      : null,
  }
  steerOpen.value = true
}

function onSteerStatusChange(payload: { handle: string; status: string; paused: boolean }): void {
  pausedOverrides.value = {
    ...pausedOverrides.value,
    [payload.handle]: payload.paused,
  }
}

function onRowHeaderClick(row: AgentTimelineRow, event?: Event): void {
  if (row.topologyNodeId) {
    const next = props.selectedNodeId === row.topologyNodeId ? null : row.topologyNodeId
    emit('select-node', next)
  }
  if (canSteerRow(row)) {
    openRowSteerPopover(row, event)
  }
}

// ── Formatting & Labels ──
function formatDuration(ms: number): string {
  const safe = Math.max(0, Math.round(ms))
  if (safe < 1000) return `${safe}ms`
  const sec = safe / 1000
  if (sec < 60) return `${sec < 10 ? sec.toFixed(1) : Math.round(sec)}s`
  const mins = Math.floor(sec / 60)
  const remSec = Math.round(sec % 60)
  return remSec > 0 ? `${mins}m ${remSec}s` : `${mins}m`
}

function formatCompactTokens(tokens: number): string {
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(1)}M`
  if (tokens >= 1_000) return `${(tokens / 1_000).toFixed(1)}k`
  return tokens.toLocaleString(locale.value)
}

function formatCostLabel(usd: number): string {
  if (usd <= 0) return '$0.00'
  if (usd < 0.01) return t('agentTimeline.costUnderCent')
  return t('agentTimeline.costApprox', { cost: `$${usd.toFixed(2)}` })
}

const replayHeaderSummary = computed<string>(() => {
  const m = model.value
  const parts: string[] = [
    t('agentTimeline.agentsCount', m.agentCount),
    formatDuration(m.wallDurationMs),
    m.speedupFactor > 1.04
      ? t('agentTimeline.speedupFaster', { factor: m.speedupFactor.toFixed(1) })
      : t('agentTimeline.speedupSequential', { factor: m.speedupFactor.toFixed(1) }),
  ]
  if (m.totalTokens !== null) {
    parts.push(t('agentTimeline.tokensCompact', { tokens: formatCompactTokens(m.totalTokens) }))
  }
  if (m.estimatedCostUsd !== null) {
    parts.push(formatCostLabel(m.estimatedCostUsd))
  }
  return parts.join(' · ')
})

const replayScrubberTimeLabel = computed<string>(() => {
  const currentMs = (replayPct.value / 100) * model.value.wallDurationMs
  return `${formatDuration(currentMs)} / ${formatDuration(model.value.wallDurationMs)}`
})

const axisTicks = computed(() => {
  const total = model.value.wallDurationMs
  return [0, 25, 50, 75, 100].map((pct) => ({
    pct,
    label: pct === 0 ? '0s' : formatDuration((pct / 100) * total),
  }))
})

function roleLabel(roleKind: AgentTimelineRow['roleKind']): string {
  return t(`agentTimeline.role.${roleKind}`)
}

function spanKindLabel(kind: AgentTimelineSpan['kind']): string {
  return t(`agentTimeline.state.${kind}`)
}

function displaySpanLabel(span: AgentTimelineSpan): string {
  if (span.toolName) return span.toolName
  if (span.defaultLabelKey) {
    if (span.defaultLabelKey === 'retryAttempt') {
      return t('agentTimeline.spanDefault.retryAttempt', { attempt: span.attempt ?? 2 })
    }
    if (span.defaultLabelKey === 'fallbackAttempt') {
      return t('agentTimeline.spanDefault.fallbackAttempt', { position: span.fallbackPosition ?? 1 })
    }
    return t(`agentTimeline.spanDefault.${span.defaultLabelKey}`)
  }
  return span.label
}

// ── SVG Orthogonal Elbow Connectors ──
interface RenderedSvgEdge {
  id: string
  edge: AgentTimelineEdge
  pathD: string
  x1: number
  y1: number
  x2: number
  y2: number
  isDimmed: boolean
  isActive: boolean
}

function buildOrthogonalElbowPath(x1: number, y1: number, x2: number, y2: number, maxWidth: number): string {
  const dx = x2 - x1
  const dy = y2 - y1
  if (Math.abs(dy) < 1) {
    return `M ${x1.toFixed(1)} ${y1.toFixed(1)} L ${x2.toFixed(1)} ${y2.toFixed(1)}`
  }
  if (Math.abs(dx) < 6) {
    const jogX = Math.min(maxWidth - 3, Math.max(3, x1 + 6))
    return `M ${x1.toFixed(1)} ${y1.toFixed(1)} L ${jogX.toFixed(1)} ${y1.toFixed(1)} L ${jogX.toFixed(1)} ${y2.toFixed(1)} L ${x2.toFixed(1)} ${y2.toFixed(1)}`
  }
  const midX = x1 + dx * 0.5
  const r = Math.min(4, Math.abs(dx) * 0.25, Math.abs(dy) * 0.35)
  const sx = dx >= 0 ? 1 : -1
  const sy = dy >= 0 ? 1 : -1
  return [
    `M ${x1.toFixed(1)} ${y1.toFixed(1)}`,
    `L ${(midX - sx * r).toFixed(1)} ${y1.toFixed(1)}`,
    `Q ${midX.toFixed(1)} ${y1.toFixed(1)} ${midX.toFixed(1)} ${(y1 + sy * r).toFixed(1)}`,
    `L ${midX.toFixed(1)} ${(y2 - sy * r).toFixed(1)}`,
    `Q ${midX.toFixed(1)} ${y2.toFixed(1)} ${(midX + sx * r).toFixed(1)} ${y2.toFixed(1)}`,
    `L ${x2.toFixed(1)} ${y2.toFixed(1)}`,
  ].join(' ')
}

const svgHeightPx = computed(() => Math.max(ROW_HEIGHT_PX, model.value.rows.length * ROW_HEIGHT_PX))

const renderedEdges = computed<RenderedSvgEdge[]>(() => {
  const width = Math.max(160, trackWidthPx.value)
  const idxMap = rowIndexMap.value
  const chain = causalChain.value
  const out: RenderedSvgEdge[] = []

  for (const edge of model.value.edges) {
    const fromIdx = idxMap.get(edge.fromRowId)
    const toIdx = idxMap.get(edge.toRowId)
    if (fromIdx === undefined || toIdx === undefined) continue

    const x1 = (edge.fromPct / 100) * width
    const x2 = (edge.toPct / 100) * width
    const y1 = fromIdx * ROW_HEIGHT_PX + ROW_HEIGHT_PX / 2
    const y2 = toIdx * ROW_HEIGHT_PX + ROW_HEIGHT_PX / 2

    const inCausal = chain ? chain.edgeIds.has(edge.id) : false
    const dimmedByPin = Boolean(chain && !inCausal)
    const dimmedByReplay = !effectiveLive.value && replayPct.value < 99.9 && edge.fromPct > replayPct.value

    out.push({
      id: edge.id,
      edge,
      pathD: buildOrthogonalElbowPath(x1, y1, x2, y2, width),
      x1,
      y1,
      x2,
      y2,
      isDimmed: dimmedByPin || dimmedByReplay,
      isActive: inCausal,
    })
  }
  return out
})

// ── Detail Card Resolution (Hover / Focus / Pinned) ──
const activeDetailSpan = computed<AgentTimelineSpan | null>(() => {
  const id = hoveredSpanId.value ?? pinnedSpanId.value
  if (id) return spanByIdMap.value.get(id) ?? null
  if (hoveredRowId.value) {
    const r = rowByIdMap.value.get(hoveredRowId.value)
    return r?.spans[0] ?? null
  }
  return null
})

const activeDetailRow = computed<AgentTimelineRow | null>(() => {
  if (activeDetailSpan.value) {
    return rowByIdMap.value.get(activeDetailSpan.value.rowId) ?? null
  }
  if (hoveredRowId.value) {
    return rowByIdMap.value.get(hoveredRowId.value) ?? null
  }
  return null
})

const activeDetailHandoffNote = computed<string | null>(() => {
  const span = activeDetailSpan.value
  if (!span) return null
  if (span.handoffNote) return span.handoffNote
  if (span.errorMessage) return span.errorMessage

  const relatedEdge = model.value.edges.find(
    (e) => e.fromSpanId === span.id || e.toSpanId === span.id,
  )
  if (!relatedEdge) return null
  if (relatedEdge.note) return relatedEdge.note
  const target = relatedEdge.targetLabel || ''
  if (relatedEdge.kind === 'handoff') return t('agentTimeline.edge.handoff', { target })
  if (relatedEdge.kind === 'completion') return t('agentTimeline.edge.completion', { target })
  return t('agentTimeline.edge.dispatch', { target })
})

function isRowCausalDimmed(rowId: string): boolean {
  if (!causalChain.value) return false
  return !causalChain.value.rowIds.has(rowId)
}

function isSpanCausalDimmed(spanId: string): boolean {
  if (!causalChain.value) return false
  return !causalChain.value.spanIds.has(spanId)
}

function isSpanReplayFuture(span: AgentTimelineSpan): boolean {
  if (effectiveLive.value || replayPct.value >= 99.9) return false
  return span.startPct > replayPct.value
}
</script>

<template>
  <div
    ref="timelineRootRef"
    class="agent-timeline"
    :class="{
      'is-live': effectiveLive,
      'is-compact-dock': isCompactDock,
      'has-pinned-span': Boolean(pinnedSpanId),
    }"
    :data-compact="isCompactDock ? 'true' : 'false'"
    :data-live="effectiveLive ? 'true' : 'false'"
    role="region"
    :aria-label="t('agentTimeline.ariaLabel')"
    data-testid="agent-timeline"
    @keydown="onTimelineKeydown"
  >
    <div v-if="model.rows.length === 0" class="agent-timeline__empty" data-testid="agent-timeline-empty">
      {{ t('agentTimeline.empty') }}
    </div>

    <template v-else>
      <!-- Header: Live pulse clock OR Settled replay summary + controls -->
      <header class="agent-timeline__header">
        <div v-if="effectiveLive" class="agent-timeline__live-bar" data-testid="timeline-live-header">
          <span class="agent-timeline__live-pill">
            <span class="agent-timeline__live-dot" aria-hidden="true" />
            <span>{{ t('agentTimeline.liveBadge') }}</span>
          </span>
          <span class="agent-timeline__live-clock" data-testid="timeline-live-clock">
            {{ formatDuration(liveElapsedMs) }}
          </span>
          <span class="agent-timeline__summary-sep" aria-hidden="true">·</span>
          <span class="agent-timeline__summary-meta">
            {{ t('agentTimeline.agentsCount', model.agentCount) }}
          </span>
          <template v-if="model.totalTokens !== null">
            <span class="agent-timeline__summary-sep" aria-hidden="true">·</span>
            <span class="agent-timeline__summary-meta">
              {{ t('agentTimeline.tokensCompact', { tokens: formatCompactTokens(model.totalTokens) }) }}
            </span>
          </template>
        </div>

        <div v-else class="agent-timeline__replay-head">
          <div
            class="agent-timeline__summary-text"
            :title="replayHeaderSummary"
            data-testid="timeline-replay-summary"
          >
            {{ replayHeaderSummary }}
          </div>

          <div class="agent-timeline__replay-controls" data-testid="timeline-replay-controls">
            <button
              type="button"
              class="agent-timeline__ctrl-btn"
              :aria-label="isReplaying ? t('agentTimeline.pause') : t('agentTimeline.play')"
              :title="isReplaying ? t('agentTimeline.pause') : t('agentTimeline.play')"
              data-testid="timeline-replay-play"
              @click="toggleReplayPlay"
            >
              <Pause v-if="isReplaying" :size="12" />
              <Play v-else :size="12" />
            </button>
            <button
              type="button"
              class="agent-timeline__ctrl-btn"
              :aria-label="t('agentTimeline.restart')"
              :title="t('agentTimeline.restart')"
              data-testid="timeline-replay-restart"
              @click="restartReplay"
            >
              <RotateCcw :size="12" />
            </button>
            <button
              type="button"
              class="agent-timeline__ctrl-btn agent-timeline__speed-btn"
              :aria-label="t('agentTimeline.speedToggle', { speed: replaySpeed })"
              :title="t('agentTimeline.speedToggle', { speed: replaySpeed })"
              data-testid="timeline-replay-speed"
              @click="toggleReplaySpeed"
            >
              {{ replaySpeed === 1 ? t('agentTimeline.speed1x') : t('agentTimeline.speed2x') }}
            </button>
            <span class="agent-timeline__scrubber-time" data-testid="timeline-scrubber-time">
              {{ replayScrubberTimeLabel }}
            </span>
          </div>
        </div>

        <button
          v-if="pinnedSpanId"
          type="button"
          class="agent-timeline__unpin-btn"
          data-testid="timeline-unpin-btn"
          @click="unpinSpan"
        >
          <Pin :size="11" />
          <span>{{ t('agentTimeline.unpinHint') }}</span>
        </button>
      </header>

      <!-- Settled keyboard-accessible range scrubber -->
      <div v-if="!effectiveLive" class="agent-timeline__scrubber-row">
        <input
          type="range"
          min="0"
          max="100"
          step="0.5"
          :value="replayPct"
          class="agent-timeline__scrubber"
          :aria-label="t('agentTimeline.scrubberLabel')"
          :aria-valuemin="0"
          :aria-valuemax="100"
          :aria-valuenow="Math.round(replayPct)"
          data-testid="timeline-scrubber"
          @input="onScrubberInput"
          @keydown="onScrubberKeydown"
        >
      </div>

      <!-- Horizontal scroll viewport with sticky left agent column -->
      <div class="agent-timeline__viewport" data-testid="agent-timeline-viewport">
        <div class="agent-timeline__grid">
          <!-- Shared Time Axis Ruler -->
          <div class="agent-timeline__ruler-row" aria-hidden="true">
            <div class="agent-timeline__sticky-corner" />
            <div class="agent-timeline__ruler-track">
              <span
                v-for="tick in axisTicks"
                :key="tick.pct"
                class="agent-timeline__tick"
                :style="{ left: `${tick.pct}%` }"
                :class="{
                  'is-start': tick.pct === 0,
                  'is-end': tick.pct === 100,
                }"
              >
                {{ tick.label }}
              </span>
            </div>
          </div>

          <!-- Lanes Container (Left Sticky Labels + Right Shared Track + SVG Elbow Overlay) -->
          <div class="agent-timeline__body">
            <!-- Left Sticky Label Column -->
            <div class="agent-timeline__labels-col" data-testid="timeline-labels-col">
              <div
                v-for="row in model.rows"
                :key="row.id"
                class="agent-timeline__row-label"
                :class="{
                  'is-coordinator': row.roleKind === 'coordinator',
                  'is-selected-node': Boolean(selectedNodeId && row.topologyNodeId === selectedNodeId),
                  'is-causal-dimmed': isRowCausalDimmed(row.id),
                }"
                :style="{ '--agent-color': row.color, height: `${ROW_HEIGHT_PX}px` }"
                role="button"
                tabindex="0"
                :aria-pressed="Boolean(selectedNodeId && row.topologyNodeId === selectedNodeId)"
                data-testid="timeline-agent-row-label"
                :data-row-id="row.id"
                :data-role-kind="row.roleKind"
                :data-steerable="canSteerRow(row) ? 'true' : 'false'"
                @click="onRowHeaderClick(row, $event)"
                @keydown.enter.prevent="onRowHeaderClick(row, $event)"
                @keydown.space.prevent="onRowHeaderClick(row, $event)"
                @mouseenter="hoveredRowId = row.id"
                @mouseleave="hoveredRowId = null"
                @focus="hoveredRowId = row.id"
                @blur="hoveredRowId = null"
              >
                <div class="agent-timeline__row-top">
                  <span
                    class="agent-timeline__agent-dot"
                    :class="{
                      'is-pulsing': effectiveLive && row.status === 'running' && !isRowPaused(row),
                      'is-paused': isRowPaused(row),
                    }"
                    data-testid="timeline-agent-dot"
                  />
                  <span class="agent-timeline__agent-name" :title="row.label">
                    {{ isCompactDock ? row.shortLabel : row.label }}
                  </span>
                  <span
                    v-if="isRowPaused(row)"
                    class="agent-timeline__paused-tag"
                    data-testid="timeline-row-paused"
                  >
                    {{ tr('teammateControl.status.paused', 'Paused') }}
                  </span>
                  <span
                    v-else-if="!isCompactDock && (row.roleKind === 'coordinator' || row.isPersistentTeammate)"
                    class="agent-timeline__role-tag"
                  >
                    {{ roleLabel(row.roleKind) }}
                  </span>
                  <button
                    v-if="canSteerRow(row)"
                    type="button"
                    class="agent-timeline__steer-trigger ui-pressable"
                    :title="tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: row.label })"
                    :aria-label="tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: row.label })"
                    data-testid="timeline-row-steer-btn"
                    @click.stop="openRowSteerPopover(row, $event)"
                  >
                    <MessageSquare :size="11" aria-hidden="true" />
                  </button>
                </div>

                <div v-if="!isCompactDock" class="agent-timeline__row-sub" data-testid="timeline-row-meta">
                  <span
                    v-if="row.runtimeBadge"
                    class="agent-timeline__engine-badge"
                    :data-engine-kind="row.runtimeKind || 'api'"
                    :title="row.runtimeBadge"
                    data-testid="timeline-engine-badge"
                  >
                    {{ row.runtimeBadge }}
                  </span>
                  <span
                    v-if="row.modelLabel"
                    class="agent-timeline__model-label"
                    :title="row.modelLabel"
                    data-testid="timeline-model-label"
                  >
                    {{ row.modelLabel }}
                  </span>
                </div>
              </div>
            </div>

            <!-- Right Track Column + Orthogonal Elbow SVG Layer -->
            <div
              ref="trackColumnRef"
              class="agent-timeline__tracks-col"
              data-testid="timeline-tracks-col"
            >
              <!-- Replay vertical playhead -->
              <div
                v-if="!effectiveLive && replayPct < 99.9"
                class="agent-timeline__playhead"
                :style="{ left: `${replayPct}%` }"
                aria-hidden="true"
                data-testid="timeline-playhead"
              />

              <!-- Row Track Lanes -->
              <div
                v-for="row in model.rows"
                :key="row.id"
                class="agent-timeline__track-row"
                :class="{
                  'is-coordinator': row.roleKind === 'coordinator',
                  'is-causal-dimmed': isRowCausalDimmed(row.id),
                }"
                :style="{ '--agent-color': row.color, height: `${ROW_HEIGHT_PX}px` }"
                data-testid="timeline-track-row"
                :data-row-id="row.id"
              >
                <div
                  v-for="span in row.spans"
                  :key="span.id"
                  class="agent-timeline__span"
                  :class="[
                    `kind--${span.kind}`,
                    {
                      'is-live-active': effectiveLive && span.isLiveActive,
                      'is-pinned': pinnedSpanId === span.id,
                      'is-causal-dimmed': isSpanCausalDimmed(span.id),
                      'is-after-playhead': isSpanReplayFuture(span),
                    },
                  ]"
                  :style="{
                    '--span-start-pct': span.startPct,
                    '--span-end-pct': span.endPct,
                    '--span-width-pct': span.widthPct,
                  }"
                  role="button"
                  tabindex="0"
                  :aria-pressed="pinnedSpanId === span.id"
                  :aria-label="`${row.label} · ${spanKindLabel(span.kind)} · ${displaySpanLabel(span)}`"
                  data-testid="timeline-span"
                  :data-span-id="span.id"
                  :data-span-kind="span.kind"
                  :data-row-id="row.id"
                  @click.stop="togglePinSpan(span.id)"
                  @keydown.enter.prevent="togglePinSpan(span.id)"
                  @keydown.space.prevent="togglePinSpan(span.id)"
                  @mouseenter="hoveredSpanId = span.id"
                  @mouseleave="hoveredSpanId = null"
                  @focus="hoveredSpanId = span.id"
                  @blur="hoveredSpanId = null"
                >
                  <template v-if="span.kind === 'failed'">
                    <X :size="11" class="agent-timeline__span-icon" aria-hidden="true" />
                    <span class="agent-timeline__span-fail-text" data-testid="timeline-failed-label">
                      <span aria-hidden="true">×</span>
                      <span>{{ t('agentTimeline.state.failed') }}</span>
                    </span>
                  </template>
                  <template v-else-if="span.kind === 'retry'">
                    <RotateCw :size="10" class="agent-timeline__span-icon" aria-hidden="true" />
                    <span class="agent-timeline__span-text" data-testid="timeline-retry-label">
                      {{ span.widthPct >= 14 ? displaySpanLabel(span) : t('agentTimeline.state.retry') }}
                    </span>
                  </template>
                  <template v-else-if="span.kind === 'tool'">
                    <Wrench :size="10" class="agent-timeline__span-icon" aria-hidden="true" />
                    <span v-if="span.widthPct >= 11" class="agent-timeline__span-text">
                      {{ displaySpanLabel(span) }}
                    </span>
                  </template>
                  <template v-else-if="span.kind === 'waiting'">
                    <Clock v-if="span.widthPct >= 9" :size="10" class="agent-timeline__span-icon" aria-hidden="true" />
                    <span v-if="span.widthPct >= 18" class="agent-timeline__span-text">
                      {{ displaySpanLabel(span) }}
                    </span>
                  </template>
                  <template v-else>
                    <Sparkles v-if="span.widthPct >= 10" :size="10" class="agent-timeline__span-icon" aria-hidden="true" />
                    <span v-if="span.widthPct >= 16" class="agent-timeline__span-text">
                      {{ displaySpanLabel(span) }}
                    </span>
                  </template>
                </div>
              </div>

              <!-- Orthogonal SVG Elbow Dispatch / Completion / Handoff Connectors -->
              <svg
                v-if="renderedEdges.length > 0"
                class="agent-timeline__connectors-svg"
                :width="Math.max(160, trackWidthPx)"
                :height="svgHeightPx"
                :viewBox="`0 0 ${Math.max(160, trackWidthPx)} ${svgHeightPx}`"
                aria-hidden="true"
                data-testid="timeline-connectors-svg"
              >
                <g
                  v-for="item in renderedEdges"
                  :key="item.id"
                  class="agent-timeline__edge-group"
                  :class="[
                    `edge--${item.edge.kind}`,
                    `dir--${item.edge.direction}`,
                    {
                      'is-causal-dimmed': item.isDimmed,
                      'is-causal-active': item.isActive,
                    },
                  ]"
                  data-testid="timeline-connector-edge"
                  :data-edge-id="item.id"
                  :data-edge-kind="item.edge.kind"
                  :data-edge-direction="item.edge.direction"
                >
                  <path class="agent-timeline__edge-path" :d="item.pathD" fill="none" />
                  <circle class="agent-timeline__edge-dot" :cx="item.x1" :cy="item.y1" r="2.3" />
                  <circle class="agent-timeline__edge-dot is-target" :cx="item.x2" :cy="item.y2" r="2.7" />
                </g>
              </svg>
            </div>
          </div>
        </div>
      </div>

      <!-- Hover / Focus / Pin Detail Card -->
      <aside
        v-if="activeDetailRow && activeDetailSpan"
        class="agent-timeline__detail-card"
        :style="{ '--agent-color': activeDetailRow.color }"
        data-testid="timeline-detail-card"
      >
        <div class="agent-timeline__detail-head">
          <div class="agent-timeline__detail-identity">
            <span class="agent-timeline__agent-dot" />
            <strong>{{ activeDetailRow.label }}</strong>
            <span class="agent-timeline__role-tag">{{ roleLabel(activeDetailRow.roleKind) }}</span>
            <span
              v-if="activeDetailRow.runtimeBadge"
              class="agent-timeline__engine-badge"
              :data-engine-kind="activeDetailRow.runtimeKind || 'api'"
              data-testid="timeline-detail-runtime"
            >
              {{ activeDetailRow.runtimeBadge }}
            </span>
            <span
              v-if="activeDetailRow.modelLabel"
              class="agent-timeline__detail-model"
              data-testid="timeline-detail-model"
            >
              {{ activeDetailRow.modelLabel }}
            </span>
          </div>

          <span class="agent-timeline__detail-state" :class="`state--${activeDetailSpan.kind}`">
            <template v-if="activeDetailSpan.kind === 'failed'">
              × {{ spanKindLabel(activeDetailSpan.kind) }}
            </template>
            <template v-else>
              {{ spanKindLabel(activeDetailSpan.kind) }}
            </template>
          </span>
        </div>

        <div class="agent-timeline__detail-body">
          <span class="agent-timeline__detail-action">{{ displaySpanLabel(activeDetailSpan) }}</span>
          <div class="agent-timeline__detail-metrics">
            <span v-if="activeDetailSpan.toolName">
              {{ t('agentTimeline.detail.tool') }}: {{ activeDetailSpan.toolName }}
            </span>
            <span>
              {{ t('agentTimeline.detail.duration') }}: {{ formatDuration(activeDetailSpan.durationMs) }}
            </span>
            <span v-if="activeDetailSpan.tokens !== null">
              {{ t('agentTimeline.detail.tokens') }}: {{ activeDetailSpan.tokens.toLocaleString(locale) }}
            </span>
          </div>
          <p v-if="activeDetailHandoffNote" class="agent-timeline__detail-note" data-testid="timeline-detail-handoff">
            {{ activeDetailHandoffNote }}
          </p>
          <small class="agent-timeline__detail-hint">
            {{ pinnedSpanId === activeDetailSpan.id ? t('agentTimeline.detail.pinnedActiveHint') : t('agentTimeline.detail.pinHint') }}
          </small>
        </div>
      </aside>
    </template>

    <TeammateSteerPopover
      v-if="steerTarget"
      v-model:open="steerOpen"
      :run-id="steerTarget.runId"
      :handle="steerTarget.handle"
      :display-name="steerTarget.displayName"
      :status="steerTarget.status"
      :paused="steerTarget.paused"
      :task-summary="steerTarget.taskSummary"
      :anchor-rect="steerTarget.anchorRect"
      @status-change="onSteerStatusChange"
    />
  </div>
</template>

<style scoped>
.agent-timeline {
  --timeline-now-pct: 100;
  --label-col-width: 172px;
  display: grid;
  gap: 8px;
  min-width: 0;
  width: 100%;
  padding: 8px;
  border: 1px solid var(--border-muted);
  border-radius: 9px;
  background: var(--surface-section);
  color: var(--text-primary);
}

.agent-timeline.is-compact-dock {
  --label-col-width: 104px;
}

.agent-timeline__empty {
  padding: 14px 10px;
  font-size: 12px;
  color: var(--text-muted);
  text-align: center;
}

/* Header */
.agent-timeline__header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  min-width: 0;
}

.agent-timeline__live-bar {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  min-width: 0;
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.agent-timeline__live-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 1px 7px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  color: var(--accent-primary, #58a6ff);
  background: color-mix(in srgb, var(--accent-primary, #58a6ff) 14%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-primary, #58a6ff) 38%, var(--border-muted));
}

.agent-timeline__live-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--accent-primary, #58a6ff);
  animation: timeline-live-pulse 1.5s ease-in-out infinite;
}

.agent-timeline__live-clock {
  font-weight: 700;
  color: var(--text-primary);
}

.agent-timeline__summary-sep,
.agent-timeline__summary-meta {
  color: var(--text-muted);
}

.agent-timeline__replay-head {
  display: flex;
  flex: 1 1 auto;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  min-width: 0;
}

.agent-timeline__summary-text {
  font-size: 11px;
  font-weight: 600;
  line-height: 1.35;
  color: var(--text-secondary);
  font-variant-numeric: tabular-nums;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-timeline__replay-controls {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.agent-timeline__ctrl-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 24px;
  height: 22px;
  padding: 0 5px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
  transition: border-color 0.14s ease, color 0.14s ease, background 0.14s ease;
}

.agent-timeline__ctrl-btn:hover {
  color: var(--text-primary);
  border-color: var(--border-default);
  background: var(--surface-hover);
}

.agent-timeline__ctrl-btn:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 1px;
}

.agent-timeline__speed-btn {
  min-width: 28px;
}

.agent-timeline__scrubber-time {
  font-size: 10px;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
  margin-left: 2px;
}

.agent-timeline__unpin-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 7px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-primary, #58a6ff) 42%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary, #58a6ff) 12%, transparent);
  color: var(--accent-primary, #58a6ff);
  font-size: 10px;
  font-weight: 600;
  cursor: pointer;
}

/* Scrubber */
.agent-timeline__scrubber-row {
  display: flex;
  align-items: center;
  min-width: 0;
}

.agent-timeline__scrubber {
  width: 100%;
  height: 14px;
  margin: 0;
  accent-color: var(--accent-primary);
  cursor: pointer;
}

.agent-timeline__scrubber:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 2px;
  border-radius: 4px;
}

/* Viewport & Sticky Grid */
.agent-timeline__viewport {
  overflow-x: auto;
  overflow-y: hidden;
  min-width: 0;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  background: var(--surface-active, var(--bg-primary));
}

.agent-timeline__grid {
  display: grid;
  min-width: 360px;
}

.agent-timeline__ruler-row {
  display: grid;
  grid-template-columns: var(--label-col-width) minmax(220px, 1fr);
  height: 20px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.agent-timeline__sticky-corner {
  position: sticky;
  left: 0;
  z-index: 4;
  background: var(--surface-section);
  border-right: 1px solid var(--border-muted);
}

.agent-timeline__ruler-track {
  position: relative;
  margin: 0 8px;
}

.agent-timeline__tick {
  position: absolute;
  top: 3px;
  transform: translateX(-50%);
  font-size: 9px;
  line-height: 1.2;
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.agent-timeline__tick.is-start {
  transform: none;
}

.agent-timeline__tick.is-end {
  transform: translateX(-100%);
}

.agent-timeline__body {
  display: grid;
  grid-template-columns: var(--label-col-width) minmax(220px, 1fr);
  align-items: stretch;
}

/* Sticky Left Labels Column */
.agent-timeline__labels-col {
  position: sticky;
  left: 0;
  z-index: 3;
  background: var(--surface-section);
  border-right: 1px solid var(--border-muted);
}

.agent-timeline__row-label {
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 2px;
  padding: 4px 8px;
  box-sizing: border-box;
  border-bottom: 1px solid var(--border-muted);
  cursor: pointer;
  min-width: 0;
  transition: background 0.14s ease, opacity 0.16s ease;
}

.agent-timeline__row-label:last-child {
  border-bottom: none;
}

.agent-timeline__row-label:hover {
  background: var(--surface-hover);
}

.agent-timeline__row-label:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: -2px;
}

.agent-timeline__row-label.is-coordinator {
  background: color-mix(in srgb, var(--accent-recovery, #9b7bf7) 5%, var(--surface-section));
}

.agent-timeline__row-label.is-selected-node {
  background: color-mix(in srgb, var(--accent-primary, #58a6ff) 11%, var(--surface-section));
}

.agent-timeline__row-top {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
}

.agent-timeline__agent-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--agent-color, var(--accent-primary));
}

.agent-timeline__agent-dot.is-pulsing {
  animation: timeline-live-pulse 1.5s ease-in-out infinite;
}

.agent-timeline__agent-name {
  flex: 1;
  min-width: 0;
  font-size: 11px;
  font-weight: 600;
  line-height: 1.2;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-timeline__role-tag {
  flex-shrink: 0;
  font-size: 9px;
  font-weight: 600;
  line-height: 1.2;
  padding: 1px 4px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-muted);
}

.agent-timeline__row-sub {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  padding-left: 12px;
}

.agent-timeline__engine-badge {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  max-width: 96px;
  padding: 0 4px;
  border-radius: 4px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 9px;
  font-weight: 600;
  line-height: 1.3;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-timeline__engine-badge[data-engine-kind='acp'] {
  border-color: color-mix(in srgb, var(--accent-primary, #58a6ff) 32%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary, #58a6ff) 10%, transparent);
  color: var(--accent-primary, #58a6ff);
}

.agent-timeline__engine-badge[data-engine-kind='cli'],
.agent-timeline__engine-badge[data-engine-kind='tui'] {
  border-color: color-mix(in srgb, var(--accent-info, #4a9eff) 32%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-info, #4a9eff) 10%, transparent);
  color: var(--accent-info, var(--text-primary));
}

.agent-timeline__model-label {
  flex: 1;
  min-width: 0;
  font-size: 9px;
  line-height: 1.2;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* Right Tracks & Spans */
.agent-timeline__tracks-col {
  position: relative;
  margin: 0 8px;
  min-width: 0;
}

.agent-timeline__playhead {
  position: absolute;
  top: 0;
  bottom: 0;
  width: 1px;
  z-index: 2;
  background: var(--accent-primary, #58a6ff);
  pointer-events: none;
}

.agent-timeline__track-row {
  position: relative;
  box-sizing: border-box;
  border-bottom: 1px solid var(--border-muted);
  transition: opacity 0.16s ease;
}

.agent-timeline__track-row:last-child {
  border-bottom: none;
}

.agent-timeline__track-row.is-coordinator {
  background: color-mix(in srgb, var(--accent-recovery, #9b7bf7) 3%, transparent);
}

/* Span Bars (strictly 1px hairlines, zero thick left stripes) */
.agent-timeline__span {
  position: absolute;
  top: 8px;
  height: 24px;
  left: calc(var(--span-start-pct) * 1%);
  width: max(8px, calc(var(--span-width-pct) * 1%));
  box-sizing: border-box;
  padding: 0 6px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 10px;
  font-weight: 600;
  line-height: 1;
  overflow: hidden;
  white-space: nowrap;
  cursor: pointer;
  z-index: 1;
  transition: opacity 0.16s ease, border-radius 0.18s ease, box-shadow 0.14s ease, filter 0.16s ease;
}

/* Active growing live span: driven by ONE `--timeline-now-pct` CSS custom property, square leading edge + shimmer */
.agent-timeline__span.is-live-active {
  width: max(
    8px,
    calc((max(var(--span-start-pct), var(--timeline-now-pct)) - var(--span-start-pct)) * 1%)
  );
  border-radius: 5px 0 0 5px;
}

.agent-timeline__span.is-live-active::after {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(
    90deg,
    transparent 0%,
    rgba(255, 255, 255, 0.14) 50%,
    transparent 100%
  );
  animation: timeline-span-shimmer 1.8s linear infinite;
  pointer-events: none;
}

/* State: thinking (soft translucent tint of agent color) */
.agent-timeline__span.kind--thinking {
  background: color-mix(in srgb, var(--agent-color, var(--accent-primary)) 16%, var(--surface-raised));
  border-color: color-mix(in srgb, var(--agent-color, var(--accent-primary)) 42%, var(--border-muted));
  color: var(--text-primary);
}

/* State: tool (solid accent fill + tool icon) */
.agent-timeline__span.kind--tool {
  background: color-mix(in srgb, var(--agent-color, var(--accent-info)) 72%, var(--surface-section));
  border-color: color-mix(in srgb, var(--agent-color, var(--accent-info)) 88%, var(--border-default));
  color: rgba(250, 250, 250, 0.96);
}

/* State: waiting (diagonal hatch exposing blocking/waiting bottlenecks) */
.agent-timeline__span.kind--waiting {
  background-color: color-mix(in srgb, var(--accent-warning, #d29922) 7%, var(--surface-section));
  background-image: repeating-linear-gradient(
    -45deg,
    color-mix(in srgb, var(--accent-warning, #d29922) 20%, transparent) 0px,
    color-mix(in srgb, var(--accent-warning, #d29922) 20%, transparent) 4px,
    transparent 4px,
    transparent 8px
  );
  border: 1px dashed color-mix(in srgb, var(--accent-warning, #d29922) 52%, var(--border-muted));
  color: var(--text-secondary);
}

/* State: failed (explicit cross icon + Failed label, never color alone) */
.agent-timeline__span.kind--failed {
  min-width: 56px;
  background: color-mix(in srgb, var(--accent-danger, #f85149) 18%, var(--surface-raised));
  border-color: color-mix(in srgb, var(--accent-danger, #f85149) 65%, var(--border-muted));
  color: var(--accent-danger, #f85149);
}

/* State: retry (explicit retry icon + badge) */
.agent-timeline__span.kind--retry {
  min-width: 46px;
  background: color-mix(in srgb, var(--accent-warning, #d29922) 18%, var(--surface-raised));
  border-color: color-mix(in srgb, var(--accent-warning, #d29922) 58%, var(--border-muted));
  color: var(--accent-warning, #d29922);
}

.agent-timeline__span:hover {
  z-index: 2;
  filter: brightness(1.06);
}

.agent-timeline__span:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 1px;
  z-index: 2;
}

.agent-timeline__span.is-pinned {
  z-index: 2;
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary, #58a6ff) 38%, transparent);
}

.agent-timeline__span-icon {
  flex-shrink: 0;
}

.agent-timeline__span-text,
.agent-timeline__span-fail-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-timeline__span-fail-text {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-weight: 700;
}

/* Causal Pin Dimming & Replay Dimming */
.agent-timeline__row-label.is-causal-dimmed,
.agent-timeline__track-row.is-causal-dimmed {
  opacity: 0.34;
}

.agent-timeline__span.is-causal-dimmed {
  opacity: 0.22;
  filter: saturate(0.35);
}

.agent-timeline__span.is-after-playhead {
  opacity: 0.28;
}

/* SVG Orthogonal Elbow Connectors */
.agent-timeline__connectors-svg {
  position: absolute;
  top: 0;
  left: 0;
  pointer-events: none;
  z-index: 2;
  overflow: visible;
}

.agent-timeline__edge-group {
  color: color-mix(in srgb, var(--text-muted) 68%, transparent);
  transition: opacity 0.16s ease, color 0.16s ease;
}

.agent-timeline__edge-group.edge--handoff {
  color: color-mix(in srgb, var(--accent-info, #4a9eff) 78%, var(--text-muted));
}

.agent-timeline__edge-group.edge--completion {
  color: color-mix(in srgb, var(--accent-success, #3fb950) 68%, var(--text-muted));
}

.agent-timeline__edge-path {
  stroke: currentColor;
  stroke-width: 1.2px;
}

.agent-timeline__edge-group.edge--completion .agent-timeline__edge-path {
  stroke-dasharray: 3 2;
}

.agent-timeline__edge-dot {
  fill: currentColor;
}

.agent-timeline__edge-group.is-causal-active {
  color: var(--accent-primary, #58a6ff);
  opacity: 1;
}

.agent-timeline__edge-group.is-causal-active .agent-timeline__edge-path {
  stroke-width: 1.65px;
  stroke-dasharray: none;
}

.agent-timeline__edge-group.is-causal-dimmed {
  opacity: 0.12;
}

/* Detail Card */
.agent-timeline__detail-card {
  display: grid;
  gap: 5px;
  padding: 8px 10px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  min-width: 0;
}

.agent-timeline__detail-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  min-width: 0;
}

.agent-timeline__detail-identity {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 5px;
  min-width: 0;
  font-size: 11px;
}

.agent-timeline__detail-model {
  font-size: 10px;
  color: var(--text-muted);
}

.agent-timeline__detail-state {
  flex-shrink: 0;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
}

.agent-timeline__detail-state.state--failed {
  color: var(--accent-danger, #f85149);
  border-color: color-mix(in srgb, var(--accent-danger, #f85149) 45%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-danger, #f85149) 12%, transparent);
}

.agent-timeline__detail-state.state--retry,
.agent-timeline__detail-state.state--waiting {
  color: var(--accent-warning, #d29922);
  border-color: color-mix(in srgb, var(--accent-warning, #d29922) 45%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning, #d29922) 12%, transparent);
}

.agent-timeline__detail-body {
  display: grid;
  gap: 3px;
  min-width: 0;
}

.agent-timeline__detail-action {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.agent-timeline__detail-metrics {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  font-size: 10px;
  color: var(--text-secondary);
  font-variant-numeric: tabular-nums;
}

.agent-timeline__detail-note {
  margin: 0 !important;
  font-size: 11px !important;
  line-height: 1.35 !important;
  color: var(--text-secondary) !important;
  overflow-wrap: anywhere;
}

.agent-timeline__agent-dot.is-paused {
  background: var(--accent-warning) !important;
  animation: none !important;
}

.agent-timeline__paused-tag {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  padding: 0 5px;
  height: 15px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--accent-warning) 16%, transparent);
  color: var(--accent-warning);
  font-size: 9.5px;
  font-weight: 600;
  line-height: 1;
}

.agent-timeline__steer-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  margin-left: auto;
  flex-shrink: 0;
  border: 1px solid transparent;
  border-radius: 5px;
  background: transparent;
  color: var(--text-muted);
  opacity: 0;
  cursor: pointer;
  transition:
    opacity var(--duration-tap, 120ms) var(--ease-snappy, ease),
    background-color var(--duration-tap, 120ms) var(--ease-snappy, ease),
    color var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.agent-timeline__row-label:hover .agent-timeline__steer-trigger,
.agent-timeline__row-label:focus-within .agent-timeline__steer-trigger,
.agent-timeline__row-label.is-selected-node .agent-timeline__steer-trigger {
  opacity: 1;
}

.agent-timeline__steer-trigger:hover {
  border-color: var(--border-default);
  background: var(--bg-hover);
  color: var(--text-primary);
}

.agent-timeline__detail-hint {
  font-size: 10px;
  color: var(--text-muted);
}

@keyframes timeline-live-pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.45;
  }
}

@keyframes timeline-span-shimmer {
  0% {
    transform: translateX(-100%);
  }
  100% {
    transform: translateX(100%);
  }
}

@media (prefers-reduced-motion: reduce) {
  .agent-timeline__live-dot,
  .agent-timeline__agent-dot.is-pulsing,
  .agent-timeline__span.is-live-active::after {
    animation: none !important;
  }

  .agent-timeline__row-label,
  .agent-timeline__track-row,
  .agent-timeline__span,
  .agent-timeline__edge-group,
  .agent-timeline__ctrl-btn {
    transition: none !important;
  }
}
</style>
