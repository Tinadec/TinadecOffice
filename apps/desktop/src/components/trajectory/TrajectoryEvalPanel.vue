<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Download } from '@lucide/vue'
import type {
  ModelInvocationDto,
  RunContextDiagnosticsDto,
  ToolExecutionTimelineItemDto,
} from '@/api'
import type { ObservabilityTurnEntry } from '@/composables/useObservabilityRunSelection'
import type { ContextTreemapModel } from '@/lib/contextTreemapLayout'
import {
  exportOfflineDiagnosticHtml,
  triggerDiagnoseInComposer,
  type ExportOfflineHtmlResult,
} from '@/lib/offlineDiagnosticReport'
import {
  resolveTrajectoryCopy,
  type ContextWindowBucketReport,
  type TrajectorySummary,
} from '@/lib/trajectoryBuilder'
import {
  aggregateTrajectoryEval,
  buildEvalCompareDraft,
  buildEvalLogScale,
  computeEvalCompareDelta,
  EVAL_DURATION_TICK_CANDIDATES,
  EVAL_TOKEN_TICK_CANDIDATES,
  formatEvalDuration,
  formatEvalSeconds,
  formatEvalTickDuration,
  formatEvalTokensWithHonesty,
  pickDefaultEvalComparePair,
  type EvalTurnScatterPoint,
} from '@/lib/trajectoryEval'
import ContextWindowBucketChart from './ContextWindowBucketChart.vue'

const props = withDefaults(defineProps<{
  trajectory: TrajectorySummary
  buckets: ContextWindowBucketReport
  runId?: string | null
  sessionId?: string | null
  modelInvocations?: ModelInvocationDto[]
  toolExecutions?: ToolExecutionTimelineItemDto[]
  turns?: readonly ObservabilityTurnEntry[]
  diagnostics?: RunContextDiagnosticsDto | null
  treemap?: ContextTreemapModel | null
  stale?: boolean
}>(), {
  runId: null,
  sessionId: null,
  modelInvocations: () => [],
  toolExecutions: () => [],
  turns: () => [],
  diagnostics: null,
  treemap: null,
  stale: false,
})

const emit = defineEmits<{
  'diagnose-draft': [draft: string]
  'jump-turn': [turnNo: number]
  'jump-code': [payload: { turnNo?: number; runId?: string | null }]
  'export-report': [result: ExportOfflineHtmlResult]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  return resolveTrajectoryCopy(t, te, key, fallback, params)
}

const effectiveRunId = computed<string | null>(
  () => props.runId ?? props.diagnostics?.run_id ?? props.trajectory.runId ?? null,
)

const effectiveSessionId = computed<string | null>(
  () => props.sessionId ?? props.diagnostics?.session_id ?? props.trajectory.sessionId ?? null,
)

const evalAggregation = computed(() =>
  aggregateTrajectoryEval({
    trajectory: props.trajectory,
    turns: props.turns,
    modelInvocations: props.modelInvocations,
    toolExecutions: props.toolExecutions,
    buckets: props.buckets,
    diagnostics: props.diagnostics,
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
  }),
)

const points = computed<EvalTurnScatterPoint[]>(() => evalAggregation.value.points)
const summary = computed(() => evalAggregation.value.summary)
const hasAnyEstimatedTokens = computed(() => evalAggregation.value.hasAnyEstimatedTokens)

// Side A / Side B comparison state
const sideATurnNo = ref<number | null>(null)
const sideBTurnNo = ref<number | null>(null)
const nextSlot = ref<'A' | 'B'>('A')
const explicitSlotLock = ref<'A' | 'B' | null>(null)

function syncComparePairWithPoints(list: readonly EvalTurnScatterPoint[]): void {
  if (list.length === 0) {
    sideATurnNo.value = null
    sideBTurnNo.value = null
    nextSlot.value = 'A'
    explicitSlotLock.value = null
    return
  }
  if (list.length === 1) {
    sideATurnNo.value = list[0]!.turnNo
    sideBTurnNo.value = null
    nextSlot.value = 'A'
    explicitSlotLock.value = null
    return
  }

  const byNo = new Set(list.map((p) => p.turnNo))
  const validA = sideATurnNo.value !== null && byNo.has(sideATurnNo.value)
  const validB =
    sideBTurnNo.value !== null
    && byNo.has(sideBTurnNo.value)
    && sideBTurnNo.value !== sideATurnNo.value

  if (validA && validB) return

  const defaultPair = pickDefaultEvalComparePair(list)
  if (defaultPair) {
    sideATurnNo.value = validA ? sideATurnNo.value : defaultPair[0].turnNo
    const candidateB = defaultPair[1].turnNo !== sideATurnNo.value ? defaultPair[1].turnNo : defaultPair[0].turnNo
    sideBTurnNo.value = candidateB !== sideATurnNo.value ? candidateB : null
  } else {
    sideATurnNo.value = list[0]!.turnNo
    sideBTurnNo.value = list[1]?.turnNo ?? null
  }
  nextSlot.value = 'A'
  explicitSlotLock.value = null
}

watch(
  points,
  (list) => {
    syncComparePairWithPoints(list)
  },
  { immediate: true },
)

const turnA = computed<EvalTurnScatterPoint | null>(() => {
  if (points.value.length === 0) return null
  return points.value.find((p) => p.turnNo === sideATurnNo.value) ?? points.value[0] ?? null
})

const turnB = computed<EvalTurnScatterPoint | null>(() => {
  if (points.value.length < 2 || sideBTurnNo.value === null) return null
  return points.value.find((p) => p.turnNo === sideBTurnNo.value && p.turnNo !== turnA.value?.turnNo) ?? null
})

const compareDelta = computed(() => {
  if (!turnA.value || !turnB.value) return null
  return computeEvalCompareDelta(turnA.value, turnB.value)
})

function selectCompareSlot(slot: 'A' | 'B'): void {
  explicitSlotLock.value = slot
  nextSlot.value = slot
}

function handlePointClick(turnNo: number): void {
  if (points.value.length <= 1) {
    sideATurnNo.value = turnNo
    return
  }

  if (explicitSlotLock.value === 'B') {
    if (turnNo === sideATurnNo.value && sideBTurnNo.value !== null) {
      const prevB = sideBTurnNo.value
      sideBTurnNo.value = turnNo
      sideATurnNo.value = prevB
    } else {
      sideBTurnNo.value = turnNo
    }
    explicitSlotLock.value = null
    nextSlot.value = 'A'
    return
  }

  if (explicitSlotLock.value === 'A') {
    if (turnNo === sideBTurnNo.value && sideATurnNo.value !== null) {
      const prevA = sideATurnNo.value
      sideATurnNo.value = turnNo
      sideBTurnNo.value = prevA
    } else {
      sideATurnNo.value = turnNo
    }
    explicitSlotLock.value = null
    nextSlot.value = 'B'
    return
  }

  if (nextSlot.value === 'A') {
    if (turnNo === sideATurnNo.value) {
      nextSlot.value = 'B'
      return
    }
    if (turnNo === sideBTurnNo.value) {
      const prevA = sideATurnNo.value
      sideATurnNo.value = turnNo
      if (prevA !== null && prevA !== turnNo) {
        sideBTurnNo.value = prevA
      }
      nextSlot.value = 'B'
      return
    }
    sideATurnNo.value = turnNo
    nextSlot.value = 'B'
    return
  }

  // nextSlot.value === 'B'
  if (turnNo === sideATurnNo.value) {
    return
  }
  sideBTurnNo.value = turnNo
  nextSlot.value = 'A'
}

// SVG Log-Compressed Scatter geometry
const SVG_W = 460
const SVG_H = 228
const PAD_L = 42
const PAD_R = 18
const PAD_T = 14
const PAD_B = 28
const PLOT_W = SVG_W - PAD_L - PAD_R
const PLOT_H = SVG_H - PAD_T - PAD_B

const xScale = computed(() => {
  const maxOut = points.value.reduce((m, p) => Math.max(m, p.outputTokens), 0)
  return buildEvalLogScale(Math.max(maxOut, 300), 1_000, EVAL_TOKEN_TICK_CANDIDATES)
})

const yScale = computed(() => {
  const maxDur = points.value.reduce((m, p) => Math.max(m, p.durationMs), 0)
  return buildEvalLogScale(Math.max(maxDur, 3_000), 10_000, EVAL_DURATION_TICK_CANDIDATES)
})

function mapX(tokens: number): number {
  return PAD_L + (xScale.value.place(tokens) / 100) * PLOT_W
}

function mapY(durationMs: number): number {
  return PAD_T + PLOT_H - (yScale.value.place(durationMs) / 100) * PLOT_H
}

interface PlottedScatterPoint {
  point: EvalTurnScatterPoint
  cx: number
  cy: number
  isSideA: boolean
  isSideB: boolean
  pillX: number
  pillY: number
  pillW: number
  pillText: string
}

const plottedPoints = computed<PlottedScatterPoint[]>(() =>
  points.value.map((pt) => {
    const cx = Number(mapX(pt.outputTokens).toFixed(1))
    const cy = Number(mapY(pt.durationMs).toFixed(1))
    const isSideA = turnA.value?.turnNo === pt.turnNo
    const isSideB = turnB.value?.turnNo === pt.turnNo
    const pillText = tr('trajectoryEval.scatter.turnTag', '第 {n} 轮', { n: pt.turnNo })
    const pillW = Math.max(38, pillText.length * 9 + 10)
    const placeLeft = cx + 8 + pillW > SVG_W - 6
    const pillX = placeLeft ? Math.max(PAD_L + 2, cx - 8 - pillW) : cx + 8
    const pillY = Math.min(PAD_T + PLOT_H - 17, Math.max(PAD_T + 1, cy - 8))
    return {
      point: pt,
      cx,
      cy,
      isSideA,
      isSideB,
      pillX,
      pillY,
      pillW,
      pillText,
    }
  }),
)

const pairLineCoords = computed(() => {
  if (!turnA.value || !turnB.value) return null
  const a = plottedPoints.value.find((p) => p.point.turnNo === turnA.value!.turnNo)
  const b = plottedPoints.value.find((p) => p.point.turnNo === turnB.value!.turnNo)
  if (!a || !b) return null
  return { x1: a.cx, y1: a.cy, x2: b.cx, y2: b.cy }
})

const medianLineX = computed<number | null>(() => {
  if (points.value.length < 2 || summary.value.medianOutputTokens <= 0) return null
  return Number(mapX(summary.value.medianOutputTokens).toFixed(1))
})

function diamondPoints(cx: number, cy: number, r: number): string {
  return `${cx},${(cy - r).toFixed(1)} ${(cx + r).toFixed(1)},${cy} ${cx},${(cy + r).toFixed(1)} ${(cx - r).toFixed(1)},${cy}`
}

function formatCompactTick(val: number): string {
  if (val === 0) return '0'
  if (val >= 1_000_000) return `${val / 1_000_000}M`
  if (val >= 10_000) return `${val / 1_000}k`
  return String(val)
}

async function handleAskTurn(): Promise<void> {
  if (!turnA.value) return
  const draft = buildEvalCompareDraft({
    turnA: turnA.value,
    turnB: null,
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
  })
  await triggerDiagnoseInComposer({
    customDraft: draft,
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
    trajectory: props.trajectory,
    buckets: props.buckets,
    treemap: props.treemap,
    diagnostics: props.diagnostics,
  })
  emit('diagnose-draft', draft)
}

async function handleAskCompare(): Promise<void> {
  if (!turnA.value || !turnB.value) return
  const draft = buildEvalCompareDraft({
    turnA: turnA.value,
    turnB: turnB.value,
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
  })
  await triggerDiagnoseInComposer({
    customDraft: draft,
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
    trajectory: props.trajectory,
    buckets: props.buckets,
    treemap: props.treemap,
    diagnostics: props.diagnostics,
  })
  emit('diagnose-draft', draft)
}

function handleJumpTurnTools(): void {
  if (!turnA.value) return
  emit('jump-turn', turnA.value.turnNo)
}

function handleJumpTurnCode(): void {
  if (!turnA.value) return
  emit('jump-code', {
    turnNo: turnA.value.turnNo,
    runId: turnA.value.runId ?? effectiveRunId.value,
  })
}

function handleExportOfflineReport(): void {
  const result = exportOfflineDiagnosticHtml({
    runId: effectiveRunId.value,
    sessionId: effectiveSessionId.value,
    trajectory: props.trajectory,
    buckets: props.buckets,
    treemap: props.treemap,
    diagnostics: props.diagnostics,
  })
  emit('export-report', result)
}
</script>

<template>
  <section class="eval-root" data-testid="trajectory-eval-panel">
    <!-- 1. Top Geist Hairline KPI Summary Strip -->
    <header class="eval-summary-strip" data-testid="eval-summary-strip">
      <div class="eval-kpi-item">
        <span class="eval-kpi-label">{{ tr('trajectoryEval.summary.durationLabel', '各轮耗时合计') }}</span>
        <strong class="eval-kpi-val mono">{{ formatEvalDuration(summary.totalDurationMs) }}</strong>
        <span v-if="summary.totalTurns > 0" class="eval-kpi-sub mono">
          {{ summary.timedTurnCount }} / {{ summary.totalTurns }} {{ tr('trajectoryEval.summary.timedTurnsUnit', '轮有计时') }}
        </span>
      </div>

      <div class="eval-kpi-item eval-kpi-item--calls">
        <span class="eval-kpi-label">{{ tr('trajectoryEval.summary.callsLabel', '调用总数') }}</span>
        <strong class="eval-kpi-val mono">{{ summary.totalCalls }}</strong>
        <span class="eval-call-breakdown mono">
          <span class="eval-dot-stat">
            <i class="eval-swatch is-ok" aria-hidden="true" />
            {{ tr('trajectoryEval.summary.okShort', '成功') }} {{ summary.okCalls }}
          </span>
          <span class="eval-dot-stat" :class="{ 'is-danger': summary.errorCalls > 0 }">
            <i class="eval-swatch is-err" aria-hidden="true" />
            {{ tr('trajectoryEval.summary.errShort', '报错') }} {{ summary.errorCalls }}
          </span>
          <span class="eval-dot-stat">
            <i class="eval-swatch is-unrec" aria-hidden="true" />
            {{ tr('trajectoryEval.summary.unrecShort', '无记录') }} {{ summary.unrecordedCalls }}
          </span>
        </span>
      </div>

      <div class="eval-kpi-item">
        <span class="eval-kpi-label">{{ tr('trajectoryEval.summary.avgLatencyLabel', '平均延迟') }}</span>
        <strong class="eval-kpi-val mono">{{ summary.avgLatencyMs }} ms</strong>
      </div>

      <div class="eval-kpi-item">
        <span class="eval-kpi-label">{{ tr('trajectoryEval.summary.peakWaterlineLabel', '峰值上下文水位') }}</span>
        <strong class="eval-kpi-val mono">{{ summary.peakOccupancyPct }}%</strong>
      </div>

      <button
        type="button"
        class="eval-export-btn"
        data-testid="eval-export-report-btn"
        @click="handleExportOfflineReport"
      >
        <Download :size="12" aria-hidden="true" />
        <span>{{ tr('trajectoryEval.panel.exportHtml', '导出 HTML') }}</span>
      </button>
    </header>

    <!-- 2. Scatter + Two-Turn Compare Section -->
    <div class="eval-scatter-section">
      <div class="eval-section-head">
        <div class="eval-section-head__main">
          <h3 class="eval-section-title">
            {{ tr('trajectoryEval.scatter.heading', '输出 token × 轮次耗时') }}
          </h3>
          <p v-if="points.length > 0 && summary.errorTurnCount > 0" class="eval-section-sub">
            <strong class="mono">{{ summary.errorTurnCount }} / {{ summary.comparableTurnCount }}</strong>
            {{ tr('trajectoryEval.scatter.errorSharePrefix', '轮记录了工具报错，合计占可比轮次输出的') }}
            <strong class="mono">{{ summary.errorOutputSharePct }}%</strong>
          </p>
          <p v-else class="eval-section-sub">
            {{ tr('trajectoryEval.scatter.cleanSubtitle', '每个散点对应一轮输出词元与耗时，点选任意两轮连线对照。') }}
          </p>
        </div>

        <span v-if="points.length > 0" class="eval-coverage-tag mono">
          {{ summary.comparableTurnCount }} / {{ summary.totalTurns }}
          {{ tr('trajectoryEval.scatter.comparableUnit', '轮可比') }}
        </span>
      </div>

      <!-- Geist Empty State when 0 turn points exist -->
      <div v-if="points.length === 0" class="eval-empty" data-testid="eval-scatter-empty">
        {{ tr('trajectoryEval.scatter.empty', '暂无足够轮次样本绘制耗时与输出散点图。') }}
      </div>

      <!-- Responsive Split (>540px side-by-side, <=540px stacked cleanly for 360px dock) -->
      <div v-else class="eval-split-grid">
        <div class="eval-plot-col">
          <div class="eval-plot-legend-row">
            <span class="eval-axis-caption">{{ tr('trajectoryEval.scatter.yCaption', '单轮耗时') }}</span>
            <div class="eval-legend">
              <span class="eval-legend__item">
                <i class="eval-marker eval-marker--clean" aria-hidden="true" />
                {{ tr('trajectoryEval.scatter.legendNormal', '无工具报错') }}
              </span>
              <span class="eval-legend__item">
                <i class="eval-marker eval-marker--error" aria-hidden="true" />
                {{ tr('trajectoryEval.scatter.legendError', '含工具报错') }}
              </span>
            </div>
          </div>

          <svg
            :viewBox="`0 0 ${SVG_W} ${SVG_H}`"
            class="eval-scatter-svg"
            role="group"
            :aria-label="tr('trajectoryEval.scatter.title', '输出 token × 轮次耗时')"
            data-testid="eval-scatter-svg"
          >
            <!-- Horizontal dashed hairline gridlines & Y ticks (duration) -->
            <g v-for="tick in yScale.ticks" :key="`ytick-${tick}`">
              <line
                v-if="tick > 0"
                :x1="PAD_L"
                :y1="mapY(tick)"
                :x2="SVG_W - PAD_R"
                :y2="mapY(tick)"
                class="eval-grid-line"
              />
              <text
                :x="PAD_L - 6"
                :y="mapY(tick) + 3.5"
                class="eval-axis-tick mono"
                text-anchor="end"
              >
                {{ formatEvalTickDuration(tick) }}
              </text>
            </g>

            <!-- Vertical dashed hairline gridlines & X ticks (output tokens) -->
            <g v-for="tick in xScale.ticks" :key="`xtick-${tick}`">
              <line
                v-if="tick > 0"
                :x1="mapX(tick)"
                :y1="PAD_T"
                :x2="mapX(tick)"
                :y2="PAD_T + PLOT_H"
                class="eval-grid-line"
              />
              <text
                :x="mapX(tick)"
                :y="PAD_T + PLOT_H + 14"
                class="eval-axis-tick mono"
                :text-anchor="tick === 0 ? 'start' : mapX(tick) > SVG_W - 28 ? 'end' : 'middle'"
              >
                {{ formatCompactTick(tick) }}
              </text>
            </g>

            <!-- Axes baselines -->
            <line :x1="PAD_L" :y1="PAD_T" :x2="PAD_L" :y2="PAD_T + PLOT_H" class="eval-axis-line" />
            <line
              :x1="PAD_L"
              :y1="PAD_T + PLOT_H"
              :x2="SVG_W - PAD_R"
              :y2="PAD_T + PLOT_H"
              class="eval-axis-line"
            />

            <!-- Median output vertical dashed reference -->
            <line
              v-if="medianLineX !== null"
              :x1="medianLineX"
              :y1="PAD_T"
              :x2="medianLineX"
              :y2="PAD_T + PLOT_H"
              class="eval-median-line"
            />

            <!-- Crisp #0068d6 A/B comparison connecting line -->
            <line
              v-if="pairLineCoords"
              :x1="pairLineCoords.x1"
              :y1="pairLineCoords.y1"
              :x2="pairLineCoords.x2"
              :y2="pairLineCoords.y2"
              class="eval-pair-link"
              data-testid="eval-pair-link"
            />

            <!-- Scatter points: Blue circle (clean) vs Red diamond (tool error) -->
            <g
              v-for="item in plottedPoints"
              :key="item.point.id"
              class="eval-point-group"
              :class="{
                'is-side-a': item.isSideA,
                'is-side-b': item.isSideB,
                'is-error': item.point.hasToolError,
              }"
              role="button"
              tabindex="0"
              :aria-pressed="item.isSideA || item.isSideB"
              :data-testid="`eval-scatter-point-${item.point.turnNo}`"
              @click="handlePointClick(item.point.turnNo)"
              @keydown.enter.prevent="handlePointClick(item.point.turnNo)"
              @keydown.space.prevent="handlePointClick(item.point.turnNo)"
            >
              <circle :cx="item.cx" :cy="item.cy" r="10" class="eval-point-hit" />

              <circle
                v-if="item.isSideA || item.isSideB"
                :cx="item.cx"
                :cy="item.cy"
                :r="item.isSideA ? 7.2 : 6.6"
                class="eval-point-halo"
                :class="{ 'is-peer': item.isSideB }"
              />

              <polygon
                v-if="item.point.hasToolError"
                :points="diamondPoints(item.cx, item.cy, 5)"
                class="eval-shape-error"
              />
              <circle
                v-else
                :cx="item.cx"
                :cy="item.cy"
                r="4.2"
                class="eval-shape-clean"
              />
            </g>

            <!-- Crisp pill tags for active A / B turns -->
            <g
              v-for="item in plottedPoints.filter((p) => p.isSideA || p.isSideB)"
              :key="`pill-${item.point.id}`"
              class="eval-pill-group"
              pointer-events="none"
            >
              <rect
                :x="item.pillX"
                :y="item.pillY"
                :width="item.pillW"
                height="16"
                rx="3"
                :class="item.isSideA ? 'eval-pill-bg--a' : 'eval-pill-bg--b'"
              />
              <text
                :x="item.pillX + item.pillW / 2"
                :y="item.pillY + 11.5"
                text-anchor="middle"
                :class="item.isSideA ? 'eval-pill-text--a' : 'eval-pill-text--b'"
              >
                {{ item.pillText }}
              </text>
            </g>

            <!-- Bottom-right X axis label -->
            <text
              :x="SVG_W - PAD_R"
              :y="SVG_H - 2"
              class="eval-axis-unit"
              text-anchor="end"
            >
              {{ tr('trajectoryEval.scatter.xUnit', '单轮输出 token') }}
            </text>
          </svg>

          <div class="eval-plot-foot">
            <span v-if="pairLineCoords" class="eval-foot-link-note">
              <i aria-hidden="true" />
              {{ tr('trajectoryEval.scatter.pairLineNote', '连线标出两轮对照') }}
            </span>
            <span>{{ tr('trajectoryEval.scatter.logScaleNote', '两轴对数压缩') }}</span>
            <span v-if="hasAnyEstimatedTokens" class="mono" data-testid="eval-estimated-note">
              {{ tr('trajectoryEval.scatter.estimatedNote', '≈ 含估算输出词元') }}
            </span>
          </div>
        </div>

        <!-- Right / Below: Two-Turn Compare Card -->
        <aside v-if="turnA" class="eval-compare-card" data-testid="eval-compare-card">
          <template v-if="turnB && compareDelta">
            <h4 class="eval-compare-title">
              {{ tr('trajectoryEval.compare.title', '两轮对照') }}
            </h4>
            <p class="eval-compare-delta mono" data-testid="eval-compare-delta">
              <span>
                {{
                  tr('trajectoryEval.compare.tokenDiff', '输出相差 {tokens} token', {
                    tokens: formatEvalTokensWithHonesty(
                      compareDelta.tokenDiff,
                      compareDelta.anyEstimatedTokens,
                    ),
                  })
                }}
              </span>
              <span aria-hidden="true"> · </span>
              <span>
                {{
                  tr('trajectoryEval.compare.durationDiff', '耗时相差 {seconds} 秒', {
                    seconds: compareDelta.durationDiffSec,
                  })
                }}
              </span>
            </p>

            <div class="eval-compare-slots">
              <button
                type="button"
                class="eval-slot-btn is-active"
                :class="{ 'is-locked': explicitSlotLock === 'A' }"
                data-testid="eval-compare-slot-a"
                @click="selectCompareSlot('A')"
              >
                <span class="eval-slot-turn">
                  {{ tr('trajectoryEval.scatter.turnTag', '第 {n} 轮', { n: turnA.turnNo }) }}
                </span>
                <strong class="eval-slot-dur mono">{{ formatEvalSeconds(turnA.durationMs) }}</strong>
                <span class="eval-slot-tok mono">
                  {{ formatEvalTokensWithHonesty(turnA.outputTokens, turnA.estimatedTokens) }} token
                </span>
              </button>

              <button
                type="button"
                class="eval-slot-btn"
                :class="{ 'is-locked': explicitSlotLock === 'B' }"
                data-testid="eval-compare-slot-b"
                @click="selectCompareSlot('B')"
              >
                <span class="eval-slot-turn">
                  {{ tr('trajectoryEval.scatter.turnTag', '第 {n} 轮', { n: turnB.turnNo }) }}
                </span>
                <strong class="eval-slot-dur mono">{{ formatEvalSeconds(turnB.durationMs) }}</strong>
                <span class="eval-slot-tok mono">
                  {{ formatEvalTokensWithHonesty(turnB.outputTokens, turnB.estimatedTokens) }} token
                </span>
              </button>
            </div>

            <p class="eval-compare-hint">
              {{ tr('trajectoryEval.compare.slotHint', '点轮次卡选中一侧，再点散点更换。') }}
            </p>
          </template>

          <!-- Primary Turn (Side A) Detail -->
          <div class="eval-turn-detail" data-testid="eval-turn-detail">
            <div class="eval-turn-detail__head">
              <strong>
                {{ tr('trajectoryEval.scatter.turnTag', '第 {n} 轮', { n: turnA.turnNo }) }}
              </strong>
              <span v-if="!turnB" class="mono eval-single-metrics">
                {{ formatEvalSeconds(turnA.durationMs) }} ·
                {{ formatEvalTokensWithHonesty(turnA.outputTokens, turnA.estimatedTokens) }} token
              </span>
            </div>

            <p class="eval-turn-meta mono">
              {{ turnA.modelCallCount }} {{ tr('trajectoryEval.compare.modelCallsUnit', '次模型调用') }}
              · {{ turnA.toolCallCount }} {{ tr('trajectoryEval.compare.toolCallsUnit', '次工具调用') }}
              · <span :class="{ 'is-danger': turnA.errorCount > 0 }">
                {{ turnA.errorCount }} {{ tr('trajectoryEval.compare.errorsUnit', '次报错') }}
              </span>
            </p>

            <div v-if="turnA.slowestTool" class="eval-slow-tool" data-testid="eval-slowest-tool">
              <span class="eval-slow-tool__label">
                {{ tr('trajectoryEval.compare.slowestHeading', '该轮已测最慢调用') }}
              </span>
              <div class="eval-slow-tool__row">
                <button
                  type="button"
                  class="eval-tool-link mono"
                  @click="handleJumpTurnCode"
                >
                  {{ turnA.slowestTool.toolId }}
                </button>
                <strong class="mono">{{ formatEvalSeconds(turnA.slowestTool.durationMs) }}</strong>
              </div>
              <div class="eval-slow-tool__seq mono">
                <span>{{ tr('trajectoryEval.compare.seqStart', '发起') }} #{{ turnA.slowestTool.seq }}</span>
                <span class="eval-seq-line" aria-hidden="true" />
                <span v-if="turnA.slowestTool.resultSeq">
                  {{ tr('trajectoryEval.compare.seqEnd', '结果') }} #{{ turnA.slowestTool.resultSeq }}
                </span>
              </div>
            </div>

            <div class="eval-actions-row">
              <button
                type="button"
                class="eval-text-action"
                data-testid="eval-ask-turn"
                @click="handleAskTurn"
              >
                {{ tr('trajectoryEval.compare.askTurn', '问这轮') }}
              </button>

              <button
                v-if="turnB"
                type="button"
                class="eval-text-action"
                data-testid="eval-ask-compare"
                @click="handleAskCompare"
              >
                {{ tr('trajectoryEval.compare.compareTurns', '对照这两轮') }}
              </button>
            </div>

            <div class="eval-actions-row eval-actions-row--nav">
              <button
                type="button"
                class="eval-text-action"
                data-testid="eval-jump-turn"
                @click="handleJumpTurnTools"
              >
                {{ tr('trajectoryEval.compare.viewTurnTools', '查看该轮工具') }}
              </button>

              <button
                type="button"
                class="eval-text-action is-quiet"
                data-testid="eval-jump-code"
                @click="handleJumpTurnCode"
              >
                {{ tr('trajectoryEval.compare.viewTurnCode', '代码执行 ↗') }}
              </button>
            </div>
          </div>
        </aside>
      </div>
    </div>

    <!-- 3. Seamless Borderless 20-Bucket Context Window Chart -->
    <div class="eval-buckets-section">
      <ContextWindowBucketChart
        :report="buckets"
        :diagnostics="diagnostics"
        :stale="stale"
      />
    </div>
  </section>
</template>

<style scoped>
.eval-root {
  --eval-blue: #0068d6;
  --eval-red: #c72b31;
  --eval-amber: #b87800;

  container-type: inline-size;
  display: flex;
  flex-direction: column;
  width: 100%;
  min-width: 0;
  background: var(--bg-primary);
  color: var(--text-primary);
}

:global(:root[data-theme='dark']) .eval-root,
:global(.dark) .eval-root {
  --eval-blue: #58a6ff;
  --eval-red: #f85149;
  --eval-amber: #d29922;
}

/* 1. Top Geist Hairline KPI Strip */
.eval-summary-strip {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 8px 18px;
  padding: 10px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
}

.eval-kpi-item {
  display: inline-flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: 4px 6px;
  min-width: 0;
}

.eval-kpi-label {
  font-size: 11px;
  color: var(--text-muted);
  white-space: nowrap;
}

.eval-kpi-val {
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text-primary);
  letter-spacing: -0.02em;
  white-space: nowrap;
}

.eval-kpi-sub {
  font-size: 10.5px;
  color: var(--text-muted);
}

.eval-call-breakdown {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px 9px;
  font-size: 11px;
  color: var(--text-secondary);
}

.eval-dot-stat {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
}

.eval-swatch {
  width: 6px;
  height: 6px;
  border-radius: 1.5px;
  display: inline-block;
  flex: none;
}

.eval-swatch.is-ok {
  background: var(--eval-blue);
}

.eval-swatch.is-err {
  background: var(--eval-red);
}

.eval-swatch.is-unrec {
  background: var(--eval-amber);
}

.eval-export-btn {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 25px;
  padding: 0 9px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: var(--bg-primary);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
}

.eval-export-btn:hover {
  border-color: var(--border-default);
  background: var(--bg-secondary);
  color: var(--text-primary);
}

/* 2. Scatter + Compare Section */
.eval-scatter-section {
  display: flex;
  flex-direction: column;
  border-bottom: 1px solid var(--border-muted);
}

.eval-section-head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 4px 12px;
  padding: 12px 14px 8px;
}

.eval-section-head__main {
  min-width: 0;
}

.eval-section-title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  letter-spacing: -0.02em;
  color: var(--text-primary);
}

.eval-section-sub {
  margin: 3px 0 0;
  font-size: 11.5px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.eval-section-sub strong {
  color: var(--text-primary);
  font-weight: 600;
}

.eval-coverage-tag {
  font-size: 11px;
  color: var(--text-muted);
  white-space: nowrap;
}

.eval-empty {
  padding: 28px 16px;
  text-align: center;
  font-size: 12px;
  color: var(--text-muted);
}

/* Container-responsive split (>540px side-by-side; <=540px stacked) */
.eval-split-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
}

.eval-plot-col {
  min-width: 0;
  padding: 4px 14px 12px;
}

.eval-compare-card {
  min-width: 0;
  padding: 12px 14px 14px;
  border-top: 1px solid var(--border-muted);
  display: flex;
  flex-direction: column;
  gap: 8px;
}

@container (min-width: 541px) {
  .eval-split-grid {
    grid-template-columns: minmax(0, 1.45fr) minmax(220px, 1fr);
  }

  .eval-compare-card {
    border-top: 0;
    border-left: 1px solid var(--border-muted);
  }
}

.eval-plot-legend-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 4px 10px;
  margin-bottom: 4px;
  font-size: 11px;
  color: var(--text-muted);
}

.eval-legend {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px 12px;
}

.eval-legend__item {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  white-space: nowrap;
}

.eval-marker {
  width: 7px;
  height: 7px;
  display: inline-block;
  flex: none;
}

.eval-marker--clean {
  border-radius: 50%;
  background: var(--eval-blue);
}

.eval-marker--error {
  border-radius: 1px;
  background: var(--eval-red);
  transform: rotate(45deg);
}

.eval-scatter-svg {
  width: 100%;
  height: auto;
  display: block;
  overflow: visible;
}

.eval-grid-line {
  stroke: var(--border-muted);
  stroke-width: 1;
  stroke-dasharray: 3 3;
}

.eval-axis-line {
  stroke: var(--border-default);
  stroke-width: 1;
}

.eval-median-line {
  stroke: var(--border-default);
  stroke-width: 1;
  stroke-dasharray: 4 4;
}

.eval-axis-tick {
  fill: var(--text-muted);
  font-size: 9.5px;
}

.eval-axis-unit {
  fill: var(--text-muted);
  font-size: 10px;
}

.eval-pair-link {
  stroke: var(--eval-blue);
  stroke-width: 1.45;
  stroke-linecap: round;
}

.eval-point-group {
  cursor: pointer;
  outline: none;
}

.eval-point-hit {
  fill: transparent;
}

.eval-point-halo {
  fill: none;
  stroke: var(--eval-blue);
  stroke-width: 1.5;
}

.eval-point-halo.is-peer {
  stroke-opacity: 0.55;
}

.eval-point-group:focus-visible .eval-point-hit {
  stroke: var(--eval-blue);
  stroke-width: 1.5;
}

.eval-shape-clean {
  fill: var(--eval-blue);
  stroke: var(--bg-primary);
  stroke-width: 1;
}

.eval-shape-error {
  fill: var(--eval-red);
  stroke: var(--bg-primary);
  stroke-width: 1;
}

.eval-pill-bg--a {
  fill: var(--text-primary);
}

.eval-pill-text--a {
  fill: var(--bg-primary);
  font-size: 9.5px;
  font-weight: 600;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}

.eval-pill-bg--b {
  fill: var(--bg-primary);
  stroke: var(--eval-blue);
  stroke-width: 1;
}

.eval-pill-text--b {
  fill: var(--eval-blue);
  font-size: 9.5px;
  font-weight: 600;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}

.eval-plot-foot {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px 12px;
  margin-top: 6px;
  font-size: 10.5px;
  color: var(--text-muted);
}

.eval-foot-link-note {
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.eval-foot-link-note i {
  width: 10px;
  border-top: 1.5px solid var(--eval-blue);
  display: inline-block;
}

/* Compare Card */
.eval-compare-title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.eval-compare-delta {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.eval-compare-slots {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px;
}

.eval-slot-btn {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 1px;
  padding: 4px 0 6px;
  border: 0;
  border-bottom: 1.5px solid var(--border-muted);
  border-radius: 0;
  background: transparent;
  text-align: left;
  cursor: pointer;
}

.eval-slot-btn.is-active,
.eval-slot-btn.is-locked,
.eval-slot-btn:hover {
  border-bottom-color: var(--eval-blue);
}

.eval-slot-btn.is-active .eval-slot-turn,
.eval-slot-btn.is-locked .eval-slot-turn {
  color: var(--eval-blue);
}

.eval-slot-turn {
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-primary);
}

.eval-slot-dur {
  font-size: 16px;
  font-weight: 600;
  color: var(--text-primary);
  letter-spacing: -0.03em;
  line-height: 1.3;
}

.eval-slot-tok {
  font-size: 10.5px;
  color: var(--text-muted);
}

.eval-compare-hint {
  margin: 0;
  font-size: 10.5px;
  color: var(--text-muted);
}

.eval-turn-detail {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}

.eval-compare-card > .eval-turn-detail:first-child {
  padding-top: 0;
  border-top: 0;
}

.eval-turn-detail__head {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  font-size: 13px;
}

.eval-single-metrics {
  font-size: 11.5px;
  color: var(--text-secondary);
}

.eval-turn-meta {
  margin: 0;
  font-size: 11px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.eval-slow-tool {
  display: flex;
  flex-direction: column;
  gap: 3px;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}

.eval-slow-tool__label {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-primary);
}

.eval-slow-tool__row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 8px;
  font-size: 12px;
}

.eval-tool-link {
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--eval-blue);
  font-size: 12px;
  cursor: pointer;
  text-align: left;
  overflow-wrap: anywhere;
}

.eval-tool-link:hover {
  text-decoration: underline;
  text-underline-offset: 3px;
}

.eval-slow-tool__seq {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 10.5px;
  color: var(--text-muted);
}

.eval-seq-line {
  flex: 1;
  min-width: 8px;
  border-top: 1px solid var(--border-muted);
}

.eval-actions-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px 14px;
  padding-top: 2px;
}

.eval-text-action {
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--eval-blue);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
}

.eval-text-action:hover {
  text-decoration: underline;
  text-underline-offset: 3px;
}

.eval-text-action.is-quiet {
  color: var(--text-secondary);
  font-weight: 500;
}

.eval-text-action.is-quiet:hover {
  color: var(--text-primary);
}

/* 3. Seamless Bucket Section */
.eval-buckets-section {
  min-width: 0;
}

.is-danger {
  color: var(--eval-red) !important;
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
