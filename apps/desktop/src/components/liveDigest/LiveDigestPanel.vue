<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  ArrowDown,
  ArrowUpRight,
  Check,
  CornerDownLeft,
  FileDiff,
  Minimize2,
  Radio,
  RefreshCw,
  ShieldAlert,
  Sparkles,
} from '@lucide/vue'
import {
  useLiveDigest,
  type LiveDigestChapterDto,
  type LiveDigestGovernanceSignalDto,
  type LiveDigestSentinelAlertDto,
  type LiveDigestSnapshotDto,
  type LiveDigestTeammateThoughtDto,
} from '@/composables/useLiveDigest'
import { useObservabilityRunSelection } from '@/composables/useObservabilityRunSelection'
import ObservabilityTurnBar from '@/components/observability/ObservabilityTurnBar.vue'
import { focusDockCard } from '@/lib/homeDockSync'
import { UiTransition } from '@/components/ui/ui-transition'
import type { TeammateSteerAnchorRect } from '@/components/teammate/TeammateSteerPopover.vue'
import LiveDigestChapterItem from './LiveDigestChapterItem.vue'
import LiveDigestTeammatesStrip from './LiveDigestTeammatesStrip.vue'

const props = withDefaults(defineProps<{
  runId?: string | null
  sessionId?: string | null
  runs?: readonly unknown[] | null
  activeRunId?: string | null
  /** Optional pre-hydrated snapshot override (useful for unit tests or replay previews). */
  snapshotOverride?: LiveDigestSnapshotDto | null
  /** Compact density for embedded spatial inspector tabs. */
  compact?: boolean
  /** Embedded mode hides outer dock navigation triggers when hosted outside Home dock. */
  embedded?: boolean
  /** Whether clicking sentinel steer button dispatches the global composer fill event. */
  dispatchComposerEvent?: boolean
  /** Optional storage scope id for multi-storage runs. */
  storageId?: string
}>(), {
  runId: null,
  sessionId: null,
  runs: null,
  activeRunId: null,
  snapshotOverride: null,
  compact: false,
  embedded: false,
  dispatchComposerEvent: true,
  storageId: undefined,
})

const emit = defineEmits<{
  'fill-steer': [payload: { prompt: string; alert: LiveDigestSentinelAlertDto }]
  'select-teammate': [payload: { teammate: LiveDigestTeammateThoughtDto; anchorRect: TeammateSteerAnchorRect | null }]
  'open-orchestration': [payload: { runId: string | null; chapter?: LiveDigestChapterDto | null }]
  'open-trajectory': [payload: { runId: string | null; chapter?: LiveDigestChapterDto | null }]
}>()

const { t, te, locale } = useI18n()
const isZh = computed(() => String(locale.value || '').toLowerCase().startsWith('zh'))

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? String(t(key, params)) : String(t(key))
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const {
  turns: observabilityTurns,
  isFollowingLatest,
  effectiveRunId: selectionEffectiveRunId,
  selectedTurn,
  selectRun,
  followLatest,
} = useObservabilityRunSelection({
  sessionId: () => props.sessionId ?? props.snapshotOverride?.session_id ?? null,
  runs: () => props.runs,
  activeRunId: () => props.activeRunId ?? props.runId ?? null,
})

const targetDigestRunId = computed<string | null>(
  () => selectionEffectiveRunId.value ?? props.runId ?? props.activeRunId ?? null,
)

const live = useLiveDigest(() => (props.snapshotOverride ? null : targetDigestRunId.value), {
  autoStream: () => !props.snapshotOverride,
  storageId: () => props.storageId,
})

const snapshot = computed<LiveDigestSnapshotDto | null>(
  () => props.snapshotOverride ?? live.snapshot.value,
)

const effectiveRunId = computed<string | null>(() => {
  if (props.snapshotOverride && (!props.runs || props.runs.length === 0)) {
    return props.snapshotOverride.run_id || selectionEffectiveRunId.value || props.runId || null
  }
  return selectionEffectiveRunId.value ?? snapshot.value?.run_id ?? props.runId ?? null
})

const isLoading = computed<boolean>(() => !props.snapshotOverride && live.loading.value)
const errorMessage = computed<string | null>(() => (props.snapshotOverride ? null : live.error.value))
const isTerminal = computed<boolean>(() => {
  const status = String(snapshot.value?.status ?? '').toLowerCase()
  return ['completed', 'failed', 'cancelled'].includes(status)
})
const isRunning = computed<boolean>(() => Boolean(snapshot.value && !isTerminal.value))

const chapters = computed<LiveDigestChapterDto[]>(() => snapshot.value?.chapters ?? [])
const teammateThoughts = computed<LiveDigestTeammateThoughtDto[]>(() => snapshot.value?.teammate_thoughts ?? [])
const governanceSignals = computed<LiveDigestGovernanceSignalDto[]>(() => snapshot.value?.governance_signals ?? [])
const sentinelAlerts = computed<LiveDigestSentinelAlertDto[]>(() => snapshot.value?.sentinel_alerts ?? [])
const touchedFiles = computed(() => snapshot.value?.touched_files ?? [])
const waterline = computed(() => snapshot.value?.context_waterline ?? null)

const waterlineJumpTitle = computed(() =>
  tr(
    'liveDigest.waterline.openContextTitle',
    isZh.value ? '点击打开轨迹 · 上下文矩阵' : 'Open Trajectory · Context Matrix',
  ),
)

const totalDiffStats = computed(() => {
  let added = 0
  let removed = 0
  for (const file of touchedFiles.value) {
    added += file.added_lines || 0
    removed += file.removed_lines || 0
  }
  return { count: touchedFiles.value.length, added, removed }
})

const usagePercent = computed<number>(() => {
  const pct = waterline.value?.usage_percent ?? 0
  if (!Number.isFinite(pct)) return 0
  return Math.max(0, Math.min(100, Math.round(pct * 10) / 10))
})

const waterlineTone = computed<string>(() => {
  if (usagePercent.value >= 85) return 'is-critical'
  if (usagePercent.value >= 68) return 'is-warn'
  return 'is-normal'
})

const compactionMarkers = computed(() => {
  const wl = waterline.value
  if (!wl || !Array.isArray(wl.compaction_points) || wl.compaction_points.length === 0) return []
  const maxTokens = wl.max_tokens > 0 ? wl.max_tokens : 100_000
  return wl.compaction_points.map((point, idx) => {
    const ratio = maxTokens > 0 ? (point.before_tokens / maxTokens) * 100 : ((idx + 1) / (wl.compaction_points.length + 1)) * 100
    const position = Math.max(6, Math.min(96, Math.round(ratio)))
    return {
      ...point,
      key: `${point.seq}:${idx}`,
      position,
    }
  })
})

const selectedCompactionKey = ref<string | null>(null)
const selectedCompaction = computed(() =>
  compactionMarkers.value.find((item) => item.key === selectedCompactionKey.value) ?? null,
)

function toggleCompactionMarker(key: string): void {
  selectedCompactionKey.value = selectedCompactionKey.value === key ? null : key
}

const KNOWN_SIGNAL_ORDER = [
  'APPROVAL_REJECTED',
  'FS_STALE_VERSION',
  'SAME_ARGS_RETRY',
  'TIMEOUT',
  'WORKTREE_GUARD',
  'LOOP_DETECTED',
  'CONTEXT_PRESSURE',
] as const

const groupedSignals = computed(() => {
  const map = new Map<string, { code: string; severity: string; items: LiveDigestGovernanceSignalDto[] }>()
  for (const sig of governanceSignals.value) {
    const code = sig.code || 'UNKNOWN'
    const existing = map.get(code)
    if (!existing) {
      map.set(code, { code, severity: sig.severity || 'info', items: [sig] })
    } else {
      existing.items.push(sig)
      if (sig.severity === 'critical' || (sig.severity === 'warning' && existing.severity === 'info')) {
        existing.severity = sig.severity
      }
    }
  }
  const orderMap = new Map<string, number>(KNOWN_SIGNAL_ORDER.map((c, idx) => [c, idx]))
  return [...map.values()].sort((a, b) => (orderMap.get(a.code) ?? 99) - (orderMap.get(b.code) ?? 99))
})

const selectedSignalCode = ref<string | null>(null)
const selectedSignalItems = computed(() => {
  if (!selectedSignalCode.value) return []
  return groupedSignals.value.find((group) => group.code === selectedSignalCode.value)?.items ?? []
})

function toggleSignalCode(code: string): void {
  selectedSignalCode.value = selectedSignalCode.value === code ? null : code
}

function signalLabel(code: string): string {
  const key = `liveDigest.signals.${code}`
  if (te(key)) return String(t(key))
  return code
}

const statusMeta = computed(() => {
  const status = String(snapshot.value?.status ?? '').toLowerCase()
  if (status === 'failed') {
    return { cls: 'is-failed', label: t('liveDigest.status.failed') }
  }
  if (status === 'cancelled') {
    return { cls: 'is-done', label: t('liveDigest.status.cancelled') }
  }
  if (status === 'completed') {
    return { cls: 'is-done', label: t('liveDigest.status.completed') }
  }
  if (status === 'awaiting_approval') {
    return { cls: 'is-approval', label: t('liveDigest.status.awaitingApproval') }
  }
  return { cls: 'is-live', label: t('liveDigest.status.live') }
})

const modeBadgeText = computed(() => {
  const snap = snapshot.value
  if (!snap) return ''
  if (snap.mode === 'off' || !snap.explainer_active) {
    return t('liveDigest.modeBadge.l0Only')
  }
  if (snap.explainer_model) {
    return snap.explainer_model
  }
  return t('liveDigest.modeBadge.explainerActive')
})

const PHASE_STEPS = ['hypothesis', 'verification', 'conclusion'] as const
const activePhaseIndex = computed(() => {
  const phase = String(snapshot.value?.current_phase ?? 'hypothesis').toLowerCase()
  const idx = PHASE_STEPS.indexOf(phase as typeof PHASE_STEPS[number])
  return idx >= 0 ? idx : 0
})

function formatTokens(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return '0'
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`
  if (value >= 1_000) return `${(value / 1_000).toFixed(1)}k`
  return String(Math.round(value))
}

// ---------------------------------------------------------------------------
// Live Streaming Tail (auto-scroll with respectful manual-scroll disengage)
// ---------------------------------------------------------------------------

const scrollContainerRef = ref<HTMLElement | null>(null)
const autoFollow = ref(true)
const hasUnreadUpdates = ref(false)
let programmaticScrollTimer: ReturnType<typeof setTimeout> | null = null
let isProgrammaticScroll = false

const DISENGAGE_THRESHOLD_PX = 36
const REENGAGE_THRESHOLD_PX = 20

function scrollToBottom(behavior: ScrollBehavior = 'smooth'): void {
  const el = scrollContainerRef.value
  if (!el) return
  isProgrammaticScroll = true
  if (programmaticScrollTimer !== null) clearTimeout(programmaticScrollTimer)
  if (typeof el.scrollTo === 'function') {
    el.scrollTo({ top: el.scrollHeight, behavior })
  } else {
    el.scrollTop = el.scrollHeight
  }
  programmaticScrollTimer = setTimeout(() => {
    isProgrammaticScroll = false
    programmaticScrollTimer = null
  }, 220)
}

function onTimelineScroll(event?: Event): void {
  if (isProgrammaticScroll) return
  const el = (event?.currentTarget as HTMLElement | null) ?? scrollContainerRef.value
  if (!el) return
  const distanceToBottom = el.scrollHeight - el.scrollTop - el.clientHeight
  if (distanceToBottom > DISENGAGE_THRESHOLD_PX) {
    if (autoFollow.value) autoFollow.value = false
  } else if (distanceToBottom <= REENGAGE_THRESHOLD_PX) {
    if (!autoFollow.value) autoFollow.value = true
    hasUnreadUpdates.value = false
  }
}

function jumpToLiveTail(): void {
  autoFollow.value = true
  hasUnreadUpdates.value = false
  scrollToBottom('smooth')
}

watch(
  () => effectiveRunId.value,
  () => {
    autoFollow.value = true
    hasUnreadUpdates.value = false
    selectedSignalCode.value = null
    selectedCompactionKey.value = null
  },
)

watch(
  () => [snapshot.value?.seq ?? 0, chapters.value.length, snapshot.value?.updated_at ?? ''],
  async () => {
    if (chapters.value.length === 0) return
    await nextTick()
    if (autoFollow.value) {
      scrollToBottom('smooth')
    } else {
      hasUnreadUpdates.value = true
    }
  },
)

// ---------------------------------------------------------------------------
// Sentinel Steer One-Click Fill + Cross-Panel Deep Links
// ---------------------------------------------------------------------------

const recentlyFilledAlertId = ref<string | null>(null)
let fillFeedbackTimer: ReturnType<typeof setTimeout> | null = null

function handleFillSteer(alert: LiveDigestSentinelAlertDto): void {
  const prompt = alert.suggested_steer_prompt?.trim()
  if (!prompt) return
  emit('fill-steer', { prompt, alert })
  if (props.dispatchComposerEvent && typeof window !== 'undefined') {
    window.dispatchEvent(
      new CustomEvent('tinadec:live-digest-fill-steer', {
        detail: {
          prompt,
          runId: effectiveRunId.value,
          alertId: alert.id,
          teammateHandle: alert.teammate_handle ?? null,
        },
      }),
    )
  }
  recentlyFilledAlertId.value = alert.id
  if (fillFeedbackTimer !== null) clearTimeout(fillFeedbackTimer)
  fillFeedbackTimer = setTimeout(() => {
    recentlyFilledAlertId.value = null
    fillFeedbackTimer = null
  }, 1600)
}

function handleOpenOrchestration(chapter?: LiveDigestChapterDto | null): void {
  emit('open-orchestration', { runId: effectiveRunId.value, chapter: chapter ?? null })
  if (props.embedded) return
  focusDockCard(
    { card: 'orchestration', reason: 'digest', runId: effectiveRunId.value },
    { title: t('uie.cardTitle.orchestration') },
  )
}

function handleOpenTrajectory(chapter?: LiveDigestChapterDto | null): void {
  emit('open-trajectory', { runId: effectiveRunId.value, chapter: chapter ?? null })
  if (props.embedded) return
  focusDockCard(
    {
      card: 'trajectory',
      reason: 'digest',
      trajectoryTab: 'path',
      trajectoryTurnNo: selectedTurn.value?.turnNo ?? undefined,
      runId: effectiveRunId.value,
    },
    { title: tr('uie.cardTitle.trajectory', isZh.value ? '轨迹' : 'Trajectory') },
  )
}

function handleOpenContextWaterline(): void {
  emit('open-trajectory', { runId: effectiveRunId.value, chapter: null })
  if (props.embedded) return
  focusDockCard(
    {
      card: 'trajectory',
      reason: 'digest',
      trajectoryTab: 'context',
      trajectoryTurnNo: selectedTurn.value?.turnNo ?? undefined,
      runId: effectiveRunId.value,
    },
    { title: tr('uie.cardTitle.trajectory', isZh.value ? '轨迹' : 'Trajectory') },
  )
}

onBeforeUnmount(() => {
  if (programmaticScrollTimer !== null) clearTimeout(programmaticScrollTimer)
  if (fillFeedbackTimer !== null) clearTimeout(fillFeedbackTimer)
})

defineExpose({
  autoFollow,
  jumpToLiveTail,
  refresh: () => live.refresh(),
})
</script>

<template>
  <section
    class="ld-panel obs-geist-surface"
    :class="{ 'is-compact': compact, 'is-embedded': embedded }"
    data-dock-card="live-digest"
    data-testid="live-digest-panel"
  >
    <!-- Shared Turn Bar (synced across live-digest, trajectory, orchestration) -->
    <ObservabilityTurnBar
      v-if="!embedded"
      :turns="observabilityTurns"
      :effective-run-id="effectiveRunId"
      :is-following-latest="isFollowingLatest"
      :compact="compact"
      @select-run="selectRun"
      @follow-latest="followLatest"
    >
      <template v-if="effectiveRunId && !snapshotOverride" #actions>
        <button
          type="button"
          class="ld-icon-btn ui-pressable"
          :disabled="isLoading"
          :title="t('liveDigest.actions.refresh')"
          :aria-label="t('liveDigest.actions.refresh')"
          data-testid="live-digest-refresh-btn"
          @click="live.refresh()"
        >
          <RefreshCw :size="13" :class="{ 'is-spinning': isLoading }" aria-hidden="true" />
        </button>
      </template>
    </ObservabilityTurnBar>

    <!-- Crisp Top Status, Headline & Clickable Context Waterline -->
    <header class="ld-header">
      <div class="ld-header-top">
        <div class="ld-title-group">
          <span class="ld-status-dot" :class="statusMeta.cls" aria-hidden="true" />
          <span class="ld-status-pill" :class="statusMeta.cls" data-testid="live-digest-status-pill">
            {{ statusMeta.label }}
          </span>
          <div
            v-if="snapshot"
            class="ld-phase-stepper"
            role="status"
            :aria-label="t('liveDigest.phase.progressLabel')"
          >
            <span
              v-for="(step, idx) in PHASE_STEPS"
              :key="step"
              class="ld-phase-step"
              :class="{
                'is-active': idx === activePhaseIndex,
                'is-done': idx < activePhaseIndex,
              }"
            >
              {{ t(`liveDigest.phase.${step}`) }}
            </span>
          </div>
        </div>

        <div class="ld-header-actions">
          <span
            v-if="modeBadgeText"
            class="ld-mode-pill"
            :class="{ 'is-explainer': snapshot?.explainer_active }"
            :title="t('liveDigest.modeBadge.tooltip')"
            data-testid="live-digest-mode-badge"
          >
            <Sparkles v-if="snapshot?.explainer_active" :size="11" aria-hidden="true" />
            <span>{{ modeBadgeText }}</span>
          </span>

          <button
            v-if="embedded && effectiveRunId && !snapshotOverride"
            type="button"
            class="ld-icon-btn ui-pressable"
            :disabled="isLoading"
            :title="t('liveDigest.actions.refresh')"
            :aria-label="t('liveDigest.actions.refresh')"
            data-testid="live-digest-refresh-btn"
            @click="live.refresh()"
          >
            <RefreshCw :size="13" :class="{ 'is-spinning': isLoading }" aria-hidden="true" />
          </button>
        </div>
      </div>

      <!-- Crisp one-line / multi-line headline -->
      <p v-if="snapshot" class="ld-headline" data-testid="live-digest-headline">
        {{ snapshot.headline || t('liveDigest.headlineFallback') }}
      </p>

      <!-- Clickable Mini Context Waterline Bar -> opens 'trajectory' (context tab) -->
      <div
        v-if="snapshot && waterline"
        class="ld-waterline"
        :class="{ 'is-interactive': !embedded }"
        :role="!embedded ? 'button' : undefined"
        :tabindex="!embedded ? 0 : undefined"
        :title="!embedded ? waterlineJumpTitle : undefined"
        data-testid="live-digest-waterline"
        @click="handleOpenContextWaterline"
        @keydown.enter.prevent="handleOpenContextWaterline"
        @keydown.space.prevent="handleOpenContextWaterline"
      >
        <div class="ld-waterline-head">
          <span class="ld-waterline-label">{{ t('liveDigest.waterline.label') }}</span>
          <span class="ld-waterline-tokens">
            {{ formatTokens(waterline.used_tokens) }} / {{ formatTokens(waterline.max_tokens) }}
          </span>
          <span
            v-if="waterline.compaction_count > 0"
            class="ld-compaction-count"
            data-testid="live-digest-compaction-count"
          >
            <Minimize2 :size="10" aria-hidden="true" />
            <span>{{ t('liveDigest.waterline.compacted', { count: waterline.compaction_count }) }}</span>
          </span>
          <span class="ld-waterline-pct" :class="waterlineTone">{{ usagePercent }}%</span>
          <ArrowUpRight v-if="!embedded" :size="11" class="ld-waterline-jump-icon" aria-hidden="true" />
        </div>

        <div
          class="ld-waterline-track"
          role="progressbar"
          :aria-valuenow="usagePercent"
          aria-valuemin="0"
          aria-valuemax="100"
          :aria-label="t('liveDigest.waterline.ariaLabel')"
        >
          <div class="ld-waterline-fill" :class="waterlineTone" :style="{ width: `${usagePercent}%` }" />
          <button
            v-for="marker in compactionMarkers"
            :key="marker.key"
            type="button"
            class="ld-compaction-marker"
            :class="{ 'is-selected': selectedCompactionKey === marker.key }"
            :style="{ left: `${marker.position}%` }"
            :title="t('liveDigest.waterline.compactionPointTitle', {
              before: formatTokens(marker.before_tokens),
              after: formatTokens(marker.after_tokens),
            })"
            data-testid="live-digest-compaction-marker"
            @click.stop="toggleCompactionMarker(marker.key)"
            @keydown.enter.stop.prevent="toggleCompactionMarker(marker.key)"
            @keydown.space.stop.prevent="toggleCompactionMarker(marker.key)"
          />
        </div>

        <div
          v-if="selectedCompaction"
          class="ld-compaction-detail"
          data-testid="live-digest-compaction-detail"
          @click.stop
        >
          <span class="ld-compaction-range">
            {{ formatTokens(selectedCompaction.before_tokens) }} → {{ formatTokens(selectedCompaction.after_tokens) }}
          </span>
          <span v-if="selectedCompaction.summary" class="ld-compaction-summary">
            {{ selectedCompaction.summary }}
          </span>
        </div>
      </div>

      <!-- 7-Class Governance Signal Badges -->
      <div
        v-if="groupedSignals.length > 0"
        class="ld-signals-wrap"
        data-testid="live-digest-governance-signals"
      >
        <div class="ld-signals-row">
          <button
            v-for="group in groupedSignals"
            :key="group.code"
            type="button"
            class="ld-signal-badge ui-pressable"
            :class="[
              `is-${group.severity}`,
              { 'is-active': selectedSignalCode === group.code },
            ]"
            :data-signal-code="group.code"
            data-testid="live-digest-signal-badge"
            @click="toggleSignalCode(group.code)"
          >
            <ShieldAlert :size="11" aria-hidden="true" />
            <span>{{ signalLabel(group.code) }}</span>
            <span v-if="group.items.length > 1" class="ld-signal-count">×{{ group.items.length }}</span>
          </button>
        </div>

        <div class="ui-collapse" :class="{ 'is-open': selectedSignalItems.length > 0 }">
          <div class="ui-collapse-inner">
            <ul v-if="selectedSignalItems.length > 0" class="ld-signal-detail-list">
              <li v-for="sig in selectedSignalItems" :key="sig.id" class="ld-signal-detail-item">
                <span class="ld-signal-detail-summary">{{ sig.summary }}</span>
              </li>
            </ul>
          </div>
        </div>
      </div>
    </header>

    <!-- Main Scrollable Body (Live Streaming Tail) -->
    <div class="ld-body-wrap">
      <div
        ref="scrollContainerRef"
        class="ld-scroll-body"
        data-testid="live-digest-scroll-container"
        @scroll="onTimelineScroll"
      >
        <!-- Empty / Unselected State -->
        <div v-if="!effectiveRunId && !snapshot" class="ld-empty" data-testid="live-digest-empty">
          <Radio :size="18" class="ld-empty-icon" aria-hidden="true" />
          <p class="ld-empty-title">{{ t('liveDigest.empty.title') }}</p>
          <p class="ld-empty-desc">
            {{ t('liveDigest.empty.desc') }}
          </p>
        </div>

        <!-- Error State (only when no cached snapshot is available) -->
        <div v-else-if="errorMessage && !snapshot" class="ld-empty" data-testid="live-digest-error">
          <AlertTriangle :size="18" class="ld-empty-icon is-warn" aria-hidden="true" />
          <p class="ld-empty-title">{{ t('liveDigest.error.title') }}</p>
          <p class="ld-empty-desc">{{ errorMessage }}</p>
          <button
            type="button"
            class="ld-primary-btn ui-pressable"
            @click="live.reconnect()"
          >
            <RefreshCw :size="13" aria-hidden="true" />
            <span>{{ t('liveDigest.actions.reconnect') }}</span>
          </button>
        </div>

        <!-- Active / Replay Content -->
        <template v-else-if="snapshot">
          <!-- Multi-Teammate Live Thoughts Strip -->
          <LiveDigestTeammatesStrip
            v-if="teammateThoughts.length > 0"
            :run-id="effectiveRunId"
            :teammates="teammateThoughts"
            :compact="compact"
            :storage-id="storageId"
            @select-teammate="emit('select-teammate', $event)"
          />

          <!-- Sentinel Gentle Alerts with One-Click Steer Fill -->
          <section
            v-if="sentinelAlerts.length > 0"
            class="ld-sentinels"
            :aria-label="t('liveDigest.sentinel.sectionLabel')"
            data-testid="live-digest-sentinel-list"
          >
            <div
              v-for="alert in sentinelAlerts"
              :key="alert.id"
              class="ld-sentinel-item"
              :class="`is-${alert.severity}`"
              :data-alert-id="alert.id"
              data-testid="live-digest-sentinel-alert"
            >
              <div class="ld-sentinel-head">
                <AlertTriangle :size="13" class="ld-sentinel-icon" aria-hidden="true" />
                <strong class="ld-sentinel-title">{{ alert.title }}</strong>
                <span v-if="alert.teammate_handle" class="ld-sentinel-target">
                  @{{ alert.teammate_handle }}
                </span>
              </div>
              <p class="ld-sentinel-message">{{ alert.message }}</p>
              <div v-if="alert.suggested_steer_prompt" class="ld-sentinel-actions">
                <button
                  type="button"
                  class="ld-steer-fill-btn ui-pressable"
                  data-testid="live-digest-fill-steer-btn"
                  @click="handleFillSteer(alert)"
                >
                  <Check v-if="recentlyFilledAlertId === alert.id" :size="13" aria-hidden="true" />
                  <CornerDownLeft v-else :size="13" aria-hidden="true" />
                  <span>
                    {{
                      recentlyFilledAlertId === alert.id
                        ? t('liveDigest.sentinel.filled')
                        : t('liveDigest.sentinel.fillSteer')
                    }}
                  </span>
                </button>
                <span class="ld-steer-preview" :title="alert.suggested_steer_prompt">
                  {{ alert.suggested_steer_prompt }}
                </span>
              </div>
            </div>
          </section>

          <!-- Chapters Timeline (1.5px spine) -->
          <section
            class="ld-chapters"
            :aria-label="t('liveDigest.chapter.timelineLabel')"
            data-testid="live-digest-chapters"
          >
            <div v-if="chapters.length === 0" class="ld-chapters-waiting">
              <span>{{ t('liveDigest.chapter.waiting') }}</span>
            </div>

            <LiveDigestChapterItem
              v-for="chapter in chapters"
              :key="chapter.id"
              :chapter="chapter"
              :compact="compact"
              @open-orchestration="handleOpenOrchestration"
              @open-trajectory="handleOpenTrajectory"
            />
          </section>

          <!-- Touched Files Summary Footer -->
          <section
            v-if="totalDiffStats.count > 0"
            class="ld-files-summary"
            data-testid="live-digest-files-summary"
          >
            <div class="ld-files-summary-head">
              <FileDiff :size="13" aria-hidden="true" />
              <span>
                {{
                  t('liveDigest.files.summaryTitle', {
                    count: totalDiffStats.count,
                  })
                }}
              </span>
              <span class="ld-files-totals">
                <span v-if="totalDiffStats.added > 0" class="ld-diff-add">+{{ totalDiffStats.added }}</span>
                <span v-if="totalDiffStats.removed > 0" class="ld-diff-del">-{{ totalDiffStats.removed }}</span>
              </span>
            </div>
            <div class="ld-files-grid">
              <span
                v-for="file in touchedFiles"
                :key="file.path"
                class="ld-files-item"
                :title="file.path"
              >
                <code class="ld-files-path">{{ file.path }}</code>
                <span class="ld-files-item-diff">
                  <span v-if="file.added_lines > 0" class="ld-diff-add">+{{ file.added_lines }}</span>
                  <span v-if="file.removed_lines > 0" class="ld-diff-del">-{{ file.removed_lines }}</span>
                </span>
              </span>
            </div>
          </section>
        </template>

        <!-- Connecting Placeholder -->
        <div v-else class="ld-empty" data-testid="live-digest-loading">
          <Radio :size="18" class="ld-empty-icon is-pulse" aria-hidden="true" />
          <p class="ld-empty-title">{{ t('liveDigest.loading.title') }}</p>
        </div>
      </div>

      <!-- Floating "Return to Live Tail" Pill when user scrolls up -->
      <UiTransition name="ui-pop">
        <button
          v-if="!autoFollow && chapters.length > 0"
          type="button"
          class="ld-tail-pill ui-pressable"
          data-testid="live-digest-back-to-tail"
          @click="jumpToLiveTail"
        >
          <ArrowDown :size="13" aria-hidden="true" />
          <span>{{ t('liveDigest.tail.backToLive') }}</span>
          <span v-if="hasUnreadUpdates && isRunning" class="ld-tail-unread-dot" aria-hidden="true" />
        </button>
      </UiTransition>
    </div>
  </section>
</template>

<style scoped>
.ld-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  min-width: 0;
  background: var(--bg-primary);
  color: var(--text-primary);
  overflow: hidden;
  font-variant-numeric: tabular-nums;
}

.ld-panel.is-embedded {
  background: transparent;
}

/* ── Top Status, Headline & Waterline Header (Geist Open Surface) ── */
.ld-header {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-bottom: 1px solid var(--obs-hairline, var(--border-muted));
  flex-shrink: 0;
}

.ld-panel.is-compact .ld-header {
  padding: 8px 10px;
  gap: 6px;
}

.ld-header-top {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-width: 0;
}

.ld-title-group {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  min-width: 0;
}

.ld-status-dot {
  width: 7px;
  height: 7px;
  border-radius: 999px;
  background: var(--text-muted);
  flex-shrink: 0;
}

.ld-status-dot.is-live {
  background: var(--obs-status-ok, var(--accent-success));
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--obs-status-ok, var(--accent-success)) 20%, transparent);
}

.ld-status-dot.is-approval {
  background: var(--obs-status-warn, var(--accent-warning));
}

.ld-status-dot.is-failed {
  background: var(--obs-status-danger, var(--accent-danger));
}

.ld-status-pill {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10.5px;
  font-weight: 600;
  background: var(--bg-secondary);
  color: var(--text-secondary);
  flex-shrink: 0;
}

.ld-status-pill.is-live {
  background: var(--obs-status-ok-soft, color-mix(in srgb, var(--accent-success) 12%, transparent));
  color: var(--obs-status-ok, var(--accent-success));
}

.ld-status-pill.is-approval {
  background: var(--obs-status-warn-soft, color-mix(in srgb, var(--accent-warning) 13%, transparent));
  color: var(--obs-status-warn, var(--accent-warning));
}

.ld-status-pill.is-failed {
  background: var(--obs-status-danger-soft, color-mix(in srgb, var(--accent-danger) 12%, transparent));
  color: var(--obs-status-danger, var(--accent-danger));
}

.ld-phase-stepper {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  flex-wrap: wrap;
}

.ld-phase-step {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 4px;
  font-size: 10.5px;
  color: var(--text-muted);
  background: transparent;
  transition: background-color 0.12s ease, color 0.12s ease;
}

.ld-phase-step.is-done {
  color: var(--text-secondary);
}

.ld-phase-step.is-active {
  background: var(--bg-primary-button);
  color: var(--text-on-primary);
  font-weight: 600;
}

.ld-header-actions {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.ld-mode-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 140px;
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--bg-secondary);
  color: var(--text-muted);
  font-size: 10.5px;
  font-family: var(--font-mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ld-mode-pill.is-explainer {
  color: var(--text-secondary);
}

.ld-icon-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border-radius: 5px;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.ld-icon-btn:hover:not(:disabled) {
  background: var(--bg-secondary);
  color: var(--text-primary);
}

.ld-icon-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.ld-headline {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  line-height: 1.45;
  letter-spacing: -0.01em;
  color: var(--text-primary);
  word-break: break-word;
}

/* ── Clickable Mini Context Waterline ──────────────────────────── */
.ld-waterline {
  display: flex;
  flex-direction: column;
  gap: 5px;
  padding: 5px 7px;
  margin: 0 -7px;
  border-radius: 6px;
  transition: background-color 0.12s ease;
}

.ld-waterline.is-interactive {
  cursor: pointer;
}

.ld-waterline.is-interactive:hover {
  background: var(--bg-secondary);
}

.ld-waterline.is-interactive:focus-visible {
  outline: 2px solid var(--border-input-focus);
  outline-offset: 1px;
}

.ld-waterline-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--text-muted);
}

.ld-waterline-label {
  font-weight: 500;
  color: var(--text-secondary);
}

.ld-waterline-tokens {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}

.ld-compaction-count {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 0 5px;
  border-radius: 4px;
  background: var(--bg-secondary);
  color: var(--text-secondary);
  font-size: 10.5px;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}

.ld-waterline-pct {
  margin-left: auto;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-weight: 600;
  color: var(--text-primary);
}

.ld-waterline-pct.is-warn {
  color: var(--obs-status-warn, var(--accent-warning));
}

.ld-waterline-pct.is-critical {
  color: var(--obs-status-danger, var(--accent-danger));
}

.ld-waterline-jump-icon {
  color: var(--text-muted);
  flex-shrink: 0;
  transition: transform 0.12s ease, color 0.12s ease;
}

.ld-waterline.is-interactive:hover .ld-waterline-jump-icon {
  color: var(--text-primary);
  transform: translate(1px, -1px);
}

.ld-waterline-track {
  position: relative;
  height: 5px;
  border-radius: 999px;
  background: var(--bg-tertiary, var(--bg-secondary));
}

.ld-waterline-fill {
  height: 100%;
  border-radius: 999px;
  background: var(--text-primary);
  transition: width var(--duration-panel, 240ms) var(--ease-snappy, ease);
}

.ld-waterline-fill.is-warn {
  background: var(--obs-status-warn, var(--accent-warning));
}

.ld-waterline-fill.is-critical {
  background: var(--obs-status-danger, var(--accent-danger));
}

.ld-compaction-marker {
  position: absolute;
  top: 50%;
  width: 9px;
  height: 9px;
  padding: 0;
  border-radius: 999px;
  border: 1.5px solid var(--bg-primary);
  background: var(--obs-status-warn, var(--accent-warning));
  transform: translate(-50%, -50%);
  cursor: pointer;
  transition: transform var(--duration-tap, 120ms) var(--ease-spring-soft, ease);
}

.ld-compaction-marker:hover,
.ld-compaction-marker.is-selected {
  transform: translate(-50%, -50%) scale(1.3);
}

.ld-compaction-detail {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 6px;
  border-radius: 5px;
  background: var(--bg-secondary);
  font-size: 11px;
  color: var(--text-secondary);
  cursor: default;
}

.ld-compaction-range {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-weight: 600;
  color: var(--text-primary);
  flex-shrink: 0;
}

.ld-compaction-summary {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── Governance Signal Badges ──────────────────────────────────── */
.ld-signals-wrap {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.ld-signals-row {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.ld-signal-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  border-radius: 999px;
  border: 0;
  background: var(--bg-secondary);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}

.ld-signal-badge.is-warning {
  background: var(--obs-status-warn-soft, color-mix(in srgb, var(--accent-warning) 13%, transparent));
  color: var(--obs-status-warn, var(--accent-warning));
}

.ld-signal-badge.is-critical {
  background: var(--obs-status-danger-soft, color-mix(in srgb, var(--accent-danger) 12%, transparent));
  color: var(--obs-status-danger, var(--accent-danger));
}

.ld-signal-badge.is-active {
  box-shadow: inset 0 0 0 1px currentColor;
}

.ld-signal-count {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 10.5px;
  font-weight: 700;
}

.ld-signal-detail-list {
  list-style: none;
  margin: 0;
  padding: 5px 8px;
  border-radius: 5px;
  background: var(--bg-secondary);
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.ld-signal-detail-item {
  font-size: 11.5px;
  color: var(--text-secondary);
  line-height: 1.4;
}

/* ── Scrollable Body & Live Tail Floating Pill ─────────────────── */
.ld-body-wrap {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.ld-scroll-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 12px 12px 28px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.ld-panel.is-compact .ld-scroll-body {
  padding: 9px 10px 24px;
  gap: 10px;
}

/* ── Sentinel Gentle Alerts ────────────────────────────────────── */
.ld-sentinels {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-bottom: 10px;
  border-bottom: 1px solid var(--obs-hairline, var(--border-muted));
}

.ld-sentinel-item {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 10px;
  border-radius: 6px;
  background: var(--obs-status-warn-soft, color-mix(in srgb, var(--accent-warning) 11%, var(--bg-secondary)));
}

.ld-sentinel-item.is-critical {
  background: var(--obs-status-danger-soft, color-mix(in srgb, var(--accent-danger) 11%, var(--bg-secondary)));
}

.ld-sentinel-head {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.ld-sentinel-icon {
  color: var(--obs-status-warn, var(--accent-warning));
  flex-shrink: 0;
}

.ld-sentinel-item.is-critical .ld-sentinel-icon {
  color: var(--obs-status-danger, var(--accent-danger));
}

.ld-sentinel-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  flex: 1;
  min-width: 0;
}

.ld-sentinel-target {
  font-family: var(--font-mono);
  font-size: 10.5px;
  color: var(--text-secondary);
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--bg-primary);
  flex-shrink: 0;
}

.ld-sentinel-message {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-secondary);
}

.ld-sentinel-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.ld-steer-fill-btn,
.ld-primary-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 10px;
  border-radius: 6px;
  border: 0;
  background: var(--bg-primary-button);
  color: var(--text-on-primary);
  font-size: 11.5px;
  font-weight: 600;
  line-height: 1.2;
  white-space: nowrap;
  cursor: pointer;
  flex-shrink: 0;
}

.ld-steer-fill-btn:hover,
.ld-primary-btn:hover {
  background: var(--bg-primary-button-hover);
}

.ld-steer-preview {
  flex: 1;
  min-width: 0;
  font-size: 11.5px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ── Chapters Timeline ─────────────────────────────────────────── */
.ld-chapters {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.ld-chapters-waiting {
  padding: 16px 12px;
  border-radius: 6px;
  background: var(--bg-secondary);
  text-align: center;
  font-size: 12px;
  color: var(--text-muted);
}

/* ── Touched Files Summary ─────────────────────────────────────── */
.ld-files-summary {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-top: 10px;
  border-top: 1px solid var(--obs-hairline, var(--border-muted));
}

.ld-files-summary-head {
  display: flex;
  align-items: center;
  gap: 5px;
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-secondary);
}

.ld-files-totals {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 11px;
}

.ld-files-grid {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.ld-files-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 3px 6px;
  border-radius: 4px;
  font-size: 11.5px;
  min-width: 0;
}

.ld-files-item:hover {
  background: var(--bg-secondary);
}

.ld-files-path {
  font-family: var(--font-mono);
  color: var(--text-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}

.ld-files-item-diff {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 11px;
  flex-shrink: 0;
}

.ld-diff-add {
  color: var(--obs-status-ok, var(--accent-success));
  font-weight: 600;
}

.ld-diff-del {
  color: var(--obs-status-danger, var(--accent-danger));
  font-weight: 600;
}

/* ── Empty / Loading States ────────────────────────────────────── */
.ld-empty {
  margin: auto 0;
  padding: 28px 16px;
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
  gap: 8px;
}

.ld-empty-icon {
  color: var(--text-muted);
}

.ld-empty-icon.is-warn {
  color: var(--obs-status-warn, var(--accent-warning));
}

.ld-empty-icon.is-pulse {
  animation: ld-pulse-fade 1.6s ease-in-out infinite;
}

.ld-empty-title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.ld-empty-desc {
  margin: 0;
  max-width: 280px;
  font-size: 12px;
  line-height: 1.5;
  color: var(--text-muted);
}

/* ── Floating Return to Live Tail Pill ─────────────────────────── */
.ld-tail-pill {
  position: absolute;
  left: 50%;
  bottom: 12px;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border-radius: 999px;
  border: 0;
  background: var(--bg-primary-button);
  color: var(--text-on-primary);
  font-size: 12px;
  font-weight: 600;
  line-height: 1.2;
  box-shadow: var(--shadow-panel);
  cursor: pointer;
  z-index: 4;
}

.ld-tail-pill:hover {
  background: var(--bg-primary-button-hover);
}

.ld-tail-pill.ui-pressable:active:not(:disabled) {
  transform: translateX(-50%) scale(0.96);
}

.ld-tail-unread-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: var(--obs-status-ok, var(--accent-success));
}

.is-spinning {
  animation: ld-spin 0.9s linear infinite;
}

@keyframes ld-spin {
  to {
    transform: rotate(360deg);
  }
}

@keyframes ld-pulse-fade {
  0%,
  100% {
    opacity: 0.9;
  }
  50% {
    opacity: 0.35;
  }
}

@media (prefers-reduced-motion: reduce) {
  .is-spinning,
  .ld-empty-icon.is-pulse {
    animation: none;
  }
}
</style>
