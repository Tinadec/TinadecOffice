<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle, ChevronRight, Radio, Users } from '@lucide/vue'
import { useLiveDigest, type LiveDigestSnapshotDto } from '@/composables/useLiveDigest'
import { focusDockCard } from '@/lib/homeDockSync'

const props = withDefaults(defineProps<{
  runId: string | null | undefined
  /** Optional external snapshot override (primarily for tests or pre-hydrated hosts). */
  snapshotOverride?: LiveDigestSnapshotDto | null
  /** Whether clicking the strip automatically opens the Home right dock 'live-digest' card. */
  openDockOnClick?: boolean
  /** Optional storage scope id for multi-storage sessions. */
  storageId?: string
}>(), {
  snapshotOverride: null,
  openDockOnClick: true,
  storageId: undefined,
})

const emit = defineEmits<{
  'open-panel': [runId: string | null]
}>()

const { t } = useI18n()

const live = useLiveDigest(() => (props.snapshotOverride ? null : props.runId), {
  autoStream: () => !props.snapshotOverride,
  storageId: () => props.storageId,
})

const activeSnapshot = computed<LiveDigestSnapshotDto | null>(
  () => props.snapshotOverride ?? live.snapshot.value,
)

const visible = computed<boolean>(() => Boolean(props.runId || activeSnapshot.value))

const isRunning = computed<boolean>(() => {
  const status = String(activeSnapshot.value?.status ?? '').toLowerCase()
  if (!status) return live.streaming.value
  return !['completed', 'failed', 'cancelled'].includes(status)
})

const statusTone = computed<string>(() => {
  const status = String(activeSnapshot.value?.status ?? '').toLowerCase()
  if (status === 'failed') return 'is-failed'
  if (status === 'awaiting_approval') return 'is-approval'
  if (status === 'completed' || status === 'cancelled') return 'is-done'
  return isRunning.value ? 'is-live' : 'is-idle'
})

const headlineText = computed<string>(() => {
  const text = activeSnapshot.value?.headline?.trim()
  if (text) return text
  if (live.loading.value || live.streaming.value) {
    return t('liveDigest.heartbeat.connecting')
  }
  return t('liveDigest.heartbeat.ready')
})

const usagePercent = computed<number | null>(() => {
  const wl = activeSnapshot.value?.context_waterline
  if (!wl) return null
  if (wl.max_tokens <= 0 && wl.usage_percent <= 0) return null
  const clamped = Math.max(0, Math.min(100, Math.round(wl.usage_percent)))
  return clamped
})

const waterlineTone = computed<string>(() => {
  const pct = usagePercent.value ?? 0
  if (pct >= 85) return 'is-critical'
  if (pct >= 68) return 'is-warn'
  return 'is-normal'
})

const alertCount = computed<number>(() => activeSnapshot.value?.sentinel_alerts?.length ?? 0)
const activeTeammateCount = computed<number>(() => {
  const list = activeSnapshot.value?.teammate_thoughts ?? []
  return list.filter((item) => !['completed', 'failed'].includes(String(item.status).toLowerCase())).length
    || list.length
})

const phaseShortLabel = computed<string>(() => {
  const phase = String(activeSnapshot.value?.current_phase ?? '').toLowerCase()
  if (phase === 'verification') return t('liveDigest.phase.verification')
  if (phase === 'conclusion') return t('liveDigest.phase.conclusion')
  if (phase === 'hypothesis') return t('liveDigest.phase.hypothesis')
  return t('liveDigest.title')
})

function handleOpenPanel(): void {
  const targetRunId = activeSnapshot.value?.run_id ?? props.runId ?? null
  emit('open-panel', targetRunId)
  if (props.openDockOnClick) {
    focusDockCard(
      { card: 'live-digest', reason: 'digest', runId: targetRunId },
      { title: t('uie.cardTitle.live-digest') },
    )
    if (typeof window !== 'undefined') {
      window.dispatchEvent(new CustomEvent('tinadec:focus-home-dock', { detail: { card: 'live-digest' } }))
    }
  }
}
</script>

<template>
  <div
    v-if="visible"
    class="ld-heartbeat-wrap"
    data-testid="live-digest-heartbeat-strip"
  >
    <button
      type="button"
      class="ld-heartbeat-bar ui-pressable"
      :class="statusTone"
      :title="t('liveDigest.heartbeat.openTooltip')"
      data-testid="live-digest-heartbeat-trigger"
      @click="handleOpenPanel"
    >
      <span class="ld-hb-pulse-wrap" aria-hidden="true">
        <span class="ld-hb-dot" :class="statusTone" />
        <Radio :size="13" class="ld-hb-radio" />
      </span>

      <span class="ld-hb-phase">{{ phaseShortLabel }}</span>

      <span class="ld-hb-headline" data-testid="live-digest-heartbeat-headline">
        {{ headlineText }}
      </span>

      <span class="ld-hb-metrics">
        <span
          v-if="alertCount > 0"
          class="ld-hb-badge is-alert"
          data-testid="live-digest-heartbeat-alerts"
        >
          <AlertTriangle :size="12" aria-hidden="true" />
          <span>{{ alertCount }}</span>
        </span>

        <span
          v-if="activeTeammateCount > 0"
          class="ld-hb-badge"
          data-testid="live-digest-heartbeat-teammates"
        >
          <Users :size="12" aria-hidden="true" />
          <span>{{ activeTeammateCount }}</span>
        </span>

        <span
          v-if="usagePercent !== null"
          class="ld-hb-waterline"
          :class="waterlineTone"
          data-testid="live-digest-heartbeat-waterline"
        >
          <span class="ld-hb-waterline-track" aria-hidden="true">
            <span class="ld-hb-waterline-fill" :style="{ width: `${usagePercent}%` }" />
          </span>
          <span class="ld-hb-waterline-pct">{{ usagePercent }}%</span>
        </span>

        <span class="ld-hb-cta">
          <span>{{ t('liveDigest.heartbeat.open') }}</span>
          <ChevronRight :size="13" aria-hidden="true" />
        </span>
      </span>
    </button>
  </div>
</template>

<style scoped>
.ld-heartbeat-wrap {
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
  flex-shrink: 0;
}

.ld-heartbeat-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-width: 0;
  height: 32px;
  padding: 0 10px;
  border-radius: var(--radius-control-md, 8px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-primary);
  font-size: 12px;
  line-height: 1;
  text-align: left;
  cursor: pointer;
}

.ld-heartbeat-bar:hover {
  background: var(--surface-hover, var(--bg-hover));
  border-color: var(--border-default);
}

.ld-heartbeat-bar.is-approval {
  border-color: color-mix(in srgb, var(--accent-warning) 44%, var(--border-muted));
}

.ld-heartbeat-bar.is-failed {
  border-color: color-mix(in srgb, var(--accent-danger) 44%, var(--border-muted));
}

.ld-hb-pulse-wrap {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-shrink: 0;
}

.ld-hb-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: var(--text-muted);
}

.ld-hb-dot.is-live {
  background: var(--accent-success);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-success) 24%, transparent);
  animation: ld-hb-pulse 1.8s ease-in-out infinite;
}

.ld-hb-dot.is-approval {
  background: var(--accent-warning);
}

.ld-hb-dot.is-failed {
  background: var(--accent-danger);
}

.ld-hb-radio {
  color: var(--text-secondary);
}

.ld-hb-phase {
  display: inline-flex;
  align-items: center;
  padding: 2px 6px;
  border-radius: 999px;
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  flex-shrink: 0;
}

.ld-hb-headline {
  flex: 1;
  min-width: 0;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ld-hb-metrics {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.ld-hb-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 2px 6px;
  border-radius: 999px;
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-secondary);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.ld-hb-badge.is-alert {
  background: var(--bg-status-warn);
  color: var(--accent-warning);
  font-weight: 600;
}

.ld-hb-waterline {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  color: var(--text-muted);
}

.ld-hb-waterline-track {
  width: 34px;
  height: 4px;
  border-radius: 999px;
  background: var(--surface-raised, var(--bg-tertiary));
  overflow: hidden;
}

.ld-hb-waterline-fill {
  display: block;
  height: 100%;
  border-radius: 999px;
  background: var(--accent-primary);
  transition: width var(--duration-panel, 240ms) var(--ease-snappy, ease);
}

.ld-hb-waterline.is-warn .ld-hb-waterline-fill {
  background: var(--accent-warning);
}

.ld-hb-waterline.is-critical .ld-hb-waterline-fill {
  background: var(--accent-danger);
}

.ld-hb-waterline.is-critical .ld-hb-waterline-pct {
  color: var(--accent-danger);
  font-weight: 600;
}

.ld-hb-cta {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-secondary);
  padding-left: 2px;
}

.ld-heartbeat-bar:hover .ld-hb-cta {
  color: var(--text-primary);
}

@keyframes ld-hb-pulse {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.42;
  }
}

@media (prefers-reduced-motion: reduce) {
  .ld-hb-dot.is-live {
    animation: none;
  }
}
</style>
