<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Activity, AlertTriangle, Database } from '@lucide/vue'
import type { RunContextDiagnosticsDto } from '@/api'
import {
  fromCoreWindowBucketsDto,
  type ContextWindowBucket,
  type ContextWindowBucketReport,
} from '@/lib/trajectoryBuilder'

const props = withDefaults(defineProps<{
  report: ContextWindowBucketReport
  diagnostics?: RunContextDiagnosticsDto | null
  stale?: boolean
  compact?: boolean
}>(), {
  diagnostics: null,
  stale: false,
  compact: false,
})

const { t } = useI18n()

const activeIndex = ref<number | null>(null)

const effectiveReport = computed<ContextWindowBucketReport>(() => {
  if (props.diagnostics?.window_buckets) {
    const core = fromCoreWindowBucketsDto(props.diagnostics.window_buckets)
    if (core) return core
  }
  return props.report
})

const degradationReasonCodes = computed<string[]>(
  () => props.diagnostics?.window_buckets?.degradation_reason_codes ?? [],
)

const SVG_W = 400
const SVG_H = 128
const PAD_L = 28
const PAD_R = 28
const PAD_T = 10
const PAD_B = 22
const PLOT_W = SVG_W - PAD_L - PAD_R
const PLOT_H = SVG_H - PAD_T - PAD_B
const SLOT_W = PLOT_W / 20

const maxLatencyMs = computed(() => {
  let max = 0
  for (const b of effectiveReport.value.buckets) {
    if (b.avgLatencyMs > max) max = b.avgLatencyMs
  }
  return max > 0 ? max : 1000
})

const plotted = computed(() =>
  effectiveReport.value.buckets.map((bucket) => {
    const x = PAD_L + bucket.index * SLOT_W
    const barW = Math.max(4, SLOT_W - 3)
    const errH = bucket.sampleCount > 0 ? Math.max(2, Math.round(bucket.errorRate * PLOT_H)) : 0
    const sampleH = bucket.sampleCount > 0 ? Math.max(2, Math.min(PLOT_H, Math.round((bucket.sampleCount / Math.max(1, effectiveReport.value.totalSamples)) * PLOT_H))) : 0
    const latY = PAD_T + PLOT_H - Math.round((bucket.avgLatencyMs / maxLatencyMs.value) * PLOT_H)
    return {
      bucket,
      x: x + 1.5,
      cx: x + SLOT_W / 2,
      barW,
      errY: PAD_T + PLOT_H - errH,
      errH,
      sampleY: PAD_T + PLOT_H - sampleH,
      sampleH,
      latY,
    }
  }),
)

const latencyPolyline = computed(() =>
  plotted.value
    .filter((p) => p.bucket.sampleCount > 0)
    .map((p) => `${p.cx.toFixed(1)},${p.latY.toFixed(1)}`)
    .join(' '),
)

const activeBucket = computed<ContextWindowBucket | null>(() => {
  if (activeIndex.value !== null) {
    return effectiveReport.value.buckets[activeIndex.value] ?? null
  }
  // Default highlight: highest-occupancy populated bucket
  for (let i = effectiveReport.value.buckets.length - 1; i >= 0; i--) {
    if (effectiveReport.value.buckets[i]!.sampleCount > 0) return effectiveReport.value.buckets[i]!
  }
  return null
})

function formatPct(rate: number): string {
  return `${Math.round(rate * 1000) / 10}%`
}
</script>

<template>
  <section class="bucket-card" :class="{ 'is-compact': compact }" data-testid="context-window-bucket-chart">
    <header class="bucket-head">
      <div class="bucket-title">
        <Activity :size="14" aria-hidden="true" />
        <strong>{{ t('trajectoryEval.buckets.title') }}</strong>
      </div>
      <div class="bucket-kpis">
        <span v-if="stale" class="bucket-pill is-stale mono" data-testid="bucket-cached-badge">
          <Database :size="11" aria-hidden="true" />
          <span>{{ t('trajectoryEval.offline.cachedBadge') }}</span>
        </span>
        <span class="bucket-pill mono" data-testid="bucket-peak">
          {{ t('trajectoryEval.buckets.peak', { pct: effectiveReport.peakOccupancyPct }) }}
        </span>
        <span class="bucket-pill mono">
          {{ t('trajectoryEval.buckets.errorRate', { pct: formatPct(effectiveReport.overallErrorRate) }) }}
        </span>
        <span class="bucket-pill mono">
          {{ t('trajectoryEval.buckets.avgLatency', { ms: effectiveReport.avgLatencyMs }) }}
        </span>
        <span
          v-if="effectiveReport.degradationDetected"
          class="bucket-pill is-warn"
          :title="degradationReasonCodes.length ? degradationReasonCodes.join(', ') : undefined"
          data-testid="bucket-degradation-badge"
        >
          <AlertTriangle :size="11" aria-hidden="true" />
          <span>{{ t('trajectoryEval.buckets.degradation') }}</span>
        </span>
      </div>
    </header>

    <div v-if="effectiveReport.totalSamples === 0" class="bucket-empty" data-testid="bucket-empty">
      {{ t('trajectoryEval.buckets.empty') }}
    </div>

    <template v-else>
      <div class="bucket-svg-wrap">
        <svg
          :viewBox="`0 0 ${SVG_W} ${SVG_H}`"
          class="bucket-svg"
          role="img"
          :aria-label="t('trajectoryEval.buckets.ariaLabel')"
        >
          <!-- Horizontal hairline guides -->
          <line :x1="PAD_L" :y1="PAD_T" :x2="SVG_W - PAD_R" :y2="PAD_T" class="grid-line" />
          <line :x1="PAD_L" :y1="PAD_T + PLOT_H / 2" :x2="SVG_W - PAD_R" :y2="PAD_T + PLOT_H / 2" class="grid-line" />
          <line :x1="PAD_L" :y1="PAD_T + PLOT_H" :x2="SVG_W - PAD_R" :y2="PAD_T + PLOT_H" class="axis-line" />

          <!-- Left Y labels (error rate) -->
          <text :x="PAD_L - 4" :y="PAD_T + 4" class="axis-text" text-anchor="end">100%</text>
          <text :x="PAD_L - 4" :y="PAD_T + PLOT_H / 2 + 3" class="axis-text" text-anchor="end">50%</text>
          <text :x="PAD_L - 4" :y="PAD_T + PLOT_H + 3" class="axis-text" text-anchor="end">0%</text>

          <!-- Right Y label (max latency) -->
          <text :x="SVG_W - PAD_R + 4" :y="PAD_T + 4" class="axis-text" text-anchor="start">{{ maxLatencyMs }}ms</text>
          <text :x="SVG_W - PAD_R + 4" :y="PAD_T + PLOT_H + 3" class="axis-text" text-anchor="start">0ms</text>

          <!-- 20 buckets (0-5% .. 95-100%) -->
          <g
            v-for="p in plotted"
            :key="p.bucket.index"
            class="bucket-slot"
            :class="{ 'is-active': activeBucket?.index === p.bucket.index }"
            :data-testid="`bucket-bar-${p.bucket.index}`"
            @mouseenter="activeIndex = p.bucket.index"
            @click="activeIndex = p.bucket.index"
          >
            <rect
              :x="p.x"
              :y="PAD_T"
              :width="p.barW"
              :height="PLOT_H"
              class="slot-hit"
              rx="2"
            />
            <!-- Sample density muted backdrop -->
            <rect
              v-if="p.bucket.sampleCount > 0"
              :x="p.x"
              :y="p.sampleY"
              :width="p.barW"
              :height="p.sampleH"
              class="sample-bar"
              rx="2"
            />
            <!-- Error rate foreground bar -->
            <rect
              v-if="p.bucket.errorCount > 0"
              :x="p.x"
              :y="p.errY"
              :width="p.barW"
              :height="p.errH"
              class="error-bar"
              rx="2"
            />
          </g>

          <!-- Latency scatter + hairline polyline -->
          <polyline
            v-if="latencyPolyline"
            :points="latencyPolyline"
            class="latency-line"
            fill="none"
          />
          <circle
            v-for="p in plotted.filter((item) => item.bucket.sampleCount > 0)"
            :key="`dot-${p.bucket.index}`"
            :cx="p.cx"
            :cy="p.latY"
            :r="activeBucket?.index === p.bucket.index ? 3.5 : 2.3"
            class="latency-dot"
          />

          <!-- X ticks -->
          <text :x="PAD_L" :y="SVG_H - 5" class="axis-text" text-anchor="start">0%</text>
          <text :x="PAD_L + PLOT_W * 0.25" :y="SVG_H - 5" class="axis-text" text-anchor="middle">25%</text>
          <text :x="PAD_L + PLOT_W * 0.5" :y="SVG_H - 5" class="axis-text" text-anchor="middle">50%</text>
          <text :x="PAD_L + PLOT_W * 0.75" :y="SVG_H - 5" class="axis-text" text-anchor="middle">75%</text>
          <text :x="SVG_W - PAD_R" :y="SVG_H - 5" class="axis-text" text-anchor="end">100%</text>
        </svg>
      </div>

      <div v-if="activeBucket" class="bucket-readout mono" data-testid="bucket-readout">
        <span class="bucket-readout__range">{{ activeBucket.label }}</span>
        <span>{{ t('trajectoryEval.buckets.readoutSamples', { count: activeBucket.sampleCount }) }}</span>
        <span>{{ t('trajectoryEval.buckets.readoutErrors', { errors: activeBucket.errorCount, rate: formatPct(activeBucket.errorRate) }) }}</span>
        <span>{{ t('trajectoryEval.buckets.readoutLatency', { ms: activeBucket.avgLatencyMs }) }}</span>
      </div>
    </template>
  </section>
</template>

<style scoped>
.bucket-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: var(--radius-card, 10px);
  background: var(--bg-secondary);
  min-width: 0;
}

.bucket-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.bucket-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-primary);
}

.bucket-kpis {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.bucket-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 21px;
  padding: 0 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--bg-primary);
  color: var(--text-secondary);
  font-size: 10.5px;
}

.bucket-pill.is-warn {
  background: var(--bg-status-warn);
  color: var(--text-primary);
  border-color: color-mix(in srgb, var(--accent-warning) 36%, var(--border-muted));
}

.bucket-pill.is-stale {
  background: var(--bg-tertiary);
  color: var(--text-secondary);
  border-style: dashed;
}

.bucket-empty {
  padding: 12px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
}

.bucket-svg-wrap {
  width: 100%;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-primary);
  padding: 6px 6px 4px;
}

.bucket-svg {
  width: 100%;
  height: auto;
  display: block;
}

.grid-line {
  stroke: var(--border-muted);
  stroke-width: 1;
  stroke-dasharray: 2 2;
}

.axis-line {
  stroke: var(--border-default);
  stroke-width: 1;
}

.axis-text {
  fill: var(--text-muted);
  font-size: 8.5px;
  font-family: var(--font-mono);
}

.bucket-slot {
  cursor: pointer;
}

.slot-hit {
  fill: transparent;
}

.bucket-slot:hover .slot-hit,
.bucket-slot.is-active .slot-hit {
  fill: color-mix(in srgb, var(--accent-primary) 10%, transparent);
}

.sample-bar {
  fill: color-mix(in srgb, var(--text-secondary) 24%, transparent);
  stroke: var(--border-muted);
  stroke-width: 0.75;
}

.error-bar {
  fill: color-mix(in srgb, var(--accent-danger) 72%, transparent);
  stroke: var(--accent-danger);
  stroke-width: 0.75;
}

.latency-line {
  stroke: var(--accent-primary);
  stroke-width: 1.25;
}

.latency-dot {
  fill: var(--bg-primary);
  stroke: var(--accent-primary);
  stroke-width: 1.5;
}

.bucket-readout {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 6px 9px;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  background: var(--bg-primary);
  font-size: 11px;
  color: var(--text-secondary);
}

.bucket-readout__range {
  font-weight: 600;
  color: var(--text-primary);
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
