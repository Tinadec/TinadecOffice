<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowLeft,
  ChevronLeft,
  ChevronRight,
  GitCommitHorizontal,
  ListTree,
  Minus,
  Plus,
  Sparkles,
  X,
} from '@lucide/vue'
import type { RunContextDiagnosticsDto } from '@/api'
import type { ContextTreemapModel } from '@/lib/contextTreemapLayout'
import {
  resolveTrajectoryCopy,
  type ContextWindowBucketReport,
  type TrajectoryNode,
  type TrajectorySummary,
} from '@/lib/trajectoryBuilder'
import TrajectoryDiagnosisPopover, { type DiagnosisPresetKey } from './TrajectoryDiagnosisPopover.vue'
import TrajectoryTreePanel from './TrajectoryTreePanel.vue'

const props = withDefaults(defineProps<{
  trajectory: TrajectorySummary
  runId?: string | null
  sessionId?: string | null
  treemap?: ContextTreemapModel | null
  buckets?: ContextWindowBucketReport | null
  diagnostics?: RunContextDiagnosticsDto | null
  staleCachedAt?: string | null
  focusTurnNo?: number | null
  focusNodeId?: string | null
  focusNonce?: number
}>(), {
  runId: null,
  sessionId: null,
  treemap: null,
  buckets: null,
  diagnostics: null,
  staleCachedAt: null,
  focusTurnNo: null,
  focusNodeId: null,
  focusNonce: 0,
})

const emit = defineEmits<{
  'select-node': [node: TrajectoryNode | null]
  'diagnose-draft': [draft: string]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  return resolveTrajectoryCopy(t, te, key, fallback, params)
}

type ViewStyle = 'graph' | 'tree'
type GraphMode = 'overview' | 'turn'
type StepSemanticKind = 'read' | 'edit' | 'command' | 'search' | 'model' | 'step'

const viewStyle = ref<ViewStyle>('graph')
const graphMode = ref<GraphMode>('overview')
const activeTurnIdx = ref<number>(0)
const selectedStepIdx = ref<number>(0)
const selectedFileFilter = ref<string | null>(null)
const zoom = ref<number>(1)
const showRawEvidence = ref<boolean>(false)
const showLegendHelp = ref<boolean>(false)

const layers = ref({
  toolResults: true,
  duration: true,
  retries: true,
  contextDelta: true,
  files: true,
})

// Drag-to-pan state on the SVG scroll container
const scrollContainerRef = ref<HTMLElement | null>(null)
const isDragging = ref(false)
let dragStartX = 0
let dragStartScrollLeft = 0

// Diagnosis popover state
const diagnosisOpen = ref(false)
const diagnosisTarget = ref<{
  turnNo: number | null
  turnLabel: string | null
  stepNo: number | null
  node: TrajectoryNode | null
  preset?: DiagnosisPresetKey | null
}>({
  turnNo: null,
  turnLabel: null,
  stepNo: null,
  node: null,
  preset: null,
})

const turns = computed<TrajectoryNode[]>(() => props.trajectory.root.children)

watch(
  () => [props.trajectory.root.id, turns.value.length] as const,
  ([, len]) => {
    selectedFileFilter.value = null
    diagnosisOpen.value = false
    showRawEvidence.value = false
    if (len > 1) {
      graphMode.value = 'overview'
      activeTurnIdx.value = 0
      selectedStepIdx.value = 0
    } else if (len === 1) {
      // Single-turn run defaults to Overview when explicitly toggled, otherwise Overview if >1 else single-turn action track
      graphMode.value = 'turn'
      activeTurnIdx.value = 0
      selectedStepIdx.value = 0
    } else {
      graphMode.value = 'overview'
      activeTurnIdx.value = 0
      selectedStepIdx.value = 0
    }
  },
  { immediate: true },
)

watch(
  () => [props.focusTurnNo, props.focusNodeId, props.focusNonce] as const,
  ([turnNo, nodeId]) => {
    if (nodeId) {
      for (let ti = 0; ti < turns.value.length; ti++) {
        const turn = turns.value[ti]!
        if (turn.id === nodeId || turn.taskId === nodeId || turn.agentId === nodeId) {
          activeTurnIdx.value = ti
          selectedStepIdx.value = 0
          graphMode.value = 'turn'
          viewStyle.value = 'graph'
          emit('select-node', turn)
          return
        }
        const si = turn.children.findIndex(
          (c) => c.id === nodeId || c.taskId === nodeId || c.toolId === nodeId,
        )
        if (si >= 0) {
          activeTurnIdx.value = ti
          selectedStepIdx.value = si
          graphMode.value = 'turn'
          viewStyle.value = 'graph'
          emit('select-node', turn.children[si]!)
          return
        }
      }
    }
    if (turnNo !== null && turnNo !== undefined && turnNo >= 1 && turnNo <= turns.value.length) {
      activeTurnIdx.value = turnNo - 1
      selectedStepIdx.value = 0
      graphMode.value = 'turn'
      viewStyle.value = 'graph'
    }
  },
  { immediate: true },
)

function classifySemanticKind(node: TrajectoryNode): StepSemanticKind {
  if (node.kind === 'model_call') return 'model'
  if (node.kind === 'step') return 'step'
  const id = (node.toolId || node.label || '').toLowerCase()
  if (node.editStats || /(write|edit|patch|replace|create_file|delete_file|rename)/.test(id)) {
    return 'edit'
  }
  if (/(bash|pwsh|powershell|cmd|sh|exec|terminal|run_command|python|node)/.test(id) || node.command) {
    return 'command'
  }
  if (/(grep|rg|search|find|glob|web)/.test(id)) {
    return 'search'
  }
  if (/(read|view|cat|open|inspect|list)/.test(id) || node.resourcePath) {
    return 'read'
  }
  return 'step'
}

function shortToolName(node: TrajectoryNode): string {
  if (node.kind === 'model_call') return 'model'
  if (node.kind === 'step') return 'step'
  const raw = (node.toolId || node.label || 'tool').trim()
  const lower = raw.toLowerCase()
  if (lower.includes('read_file') || lower === 'view_file') return 'read'
  if (lower.includes('edit_file') || lower.includes('replace')) return 'edit'
  if (lower.includes('write_file')) return 'write'
  if (lower.includes('powershell') || lower === 'pwsh') return 'pwsh'
  if (lower.includes('run_command') || lower === 'bash') return 'bash'
  if (lower.includes('grep') || lower.includes('ripgrep')) return 'rg'
  if (lower.includes('search')) return 'search'
  return raw.length > 12 ? raw.slice(0, 11) + '…' : raw
}

function basenameOf(filePath: string): string {
  const normalized = filePath.replace(/\\/g, '/')
  const idx = normalized.lastIndexOf('/')
  return idx >= 0 ? normalized.slice(idx + 1) || filePath : filePath
}

function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || !Number.isFinite(ms) || ms < 0) return '0ms'
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  const mins = Math.floor(ms / 60_000)
  const secs = Math.round((ms % 60_000) / 1000)
  return `${mins}m${secs}s`
}

function formatTokensCompact(tokens: number): string {
  if (tokens <= 0) return '0'
  if (tokens < 1000) return `${Math.round(tokens)}`
  return `${(tokens / 1000).toFixed(1)}k`
}

interface StackedSlice {
  key: string
  color: string
  height: number
}

interface OverviewTurnVm {
  index: number
  turnNo: number
  node: TrajectoryNode
  cx: number
  colX: number
  colW: number
  branchAbove: boolean
  branchPath: string
  tipX: number
  tipY: number
  toolMixLabel: string
  dominantKind: StepSemanticKind
  blockedCount: number
  recoveredCount: number
  unrecoveredCount: number
  hasGovShield: boolean
  govSummary: string
  deltaTokens: number
  barHeight: number
  barSlices: StackedSlice[]
  modifiedFiles: string[]
  primaryFile: string | null
  filePillLabel: string | null
  cumulativeMs: number
  isDimmed: boolean
  isFileRelated: boolean
}

const OVERVIEW_SVG_H = 278
const OVERVIEW_TRUNK_Y = 144
const PAD_L = 52
const PAD_R = 42

const colWidth = computed(() => Math.max(88, Math.round(132 * zoom.value)))

const overviewSvgWidth = computed(() =>
  Math.max(360, PAD_L + Math.max(1, turns.value.length) * colWidth.value + PAD_R),
)

const maxTurnTokens = computed(() => {
  let max = 0
  for (const turn of turns.value) {
    const est =
      (turn.tokensUsed ?? 0)
      || turn.children.reduce((s, c) => s + (c.tokensUsed ?? 120), 0)
      || 120
    if (est > max) max = est
  }
  return Math.max(100, max)
})

const overviewTurns = computed<OverviewTurnVm[]>(() => {
  const cw = colWidth.value
  let runningMs = 0

  return turns.value.map((turn, idx) => {
    const colX = PAD_L + idx * cw
    const cx = colX + cw / 2
    const branchAbove = idx % 2 === 0

    // Summarize tool mix + blocked / recovered counts
    const toolCounts = new Map<string, number>()
    let blockedCount = 0
    let recoveredCount = 0
    let pendingFailure = false
    let dominantKind: StepSemanticKind = 'step'
    const modifiedSet = new Set<string>()
    let modelTokens = 0
    let outputTokens = 0
    let toolOutputEst = 0

    for (const child of turn.children) {
      const sem = classifySemanticKind(child)
      if (dominantKind === 'step' && sem !== 'step') dominantKind = sem

      if (child.kind === 'tool_call') {
        const short = shortToolName(child)
        toolCounts.set(short, (toolCounts.get(short) ?? 0) + 1)
        const textLen = (child.summary?.length ?? 0) + (child.argumentsSummary?.length ?? 0)
        toolOutputEst += Math.max(40, Math.ceil(textLen / 4))
      } else if (child.kind === 'model_call') {
        modelTokens += child.inputTokens ?? child.tokensUsed ?? 180
        outputTokens += child.outputTokens ?? 90
      }

      if (child.isError) {
        blockedCount += 1
        pendingFailure = true
      } else if (pendingFailure) {
        recoveredCount += 1
        pendingFailure = false
      }

      const modFile = child.editStats?.filePath || (sem === 'edit' ? child.resourcePath : null)
      if (modFile) modifiedSet.add(modFile)
    }

    if (turn.isError && blockedCount === 0) blockedCount = 1
    const unrecoveredCount = Math.max(0, blockedCount - recoveredCount)

    const mixParts: string[] = []
    for (const [name, cnt] of [...toolCounts.entries()].slice(0, 2)) {
      mixParts.push(cnt > 1 ? `${name} ×${cnt}` : name)
    }
    if (mixParts.length === 0) {
      mixParts.push(turn.children.some((c) => c.kind === 'model_call') ? 'model' : turn.label.slice(0, 14))
    }
    if (layers.value.retries) {
      if (recoveredCount > 0) {
        mixParts.push(tr('trajectoryPath.recoveredCount', '{n} 处已恢复', { n: recoveredCount }))
      } else if (unrecoveredCount > 0) {
        mixParts.push(tr('trajectoryPath.blockedCount', '{n} 处受阻', { n: unrecoveredCount }))
      }
    }
    const toolMixLabel = mixParts.join(' · ')

    const rawTokens =
      (turn.tokensUsed ?? 0) > 0
        ? (turn.tokensUsed ?? 0)
        : modelTokens + outputTokens + toolOutputEst || Math.max(80, turn.children.length * 110)

    const barHeight = layers.value.contextDelta
      ? Math.max(8, Math.round((rawTokens / maxTurnTokens.value) * 38))
      : 0

    // 7-color miniature stacked bar slices
    const sysH = Math.max(1, Math.round(barHeight * 0.16))
    const outH = Math.max(2, Math.round(barHeight * 0.48))
    const toolH = Math.max(1, barHeight - sysH - outH)
    const barSlices: StackedSlice[] = barHeight > 0
      ? [
          { key: 'system', color: 'var(--obs-ctx-system, #8053e3)', height: sysH },
          { key: 'assistant', color: 'var(--obs-ctx-assistant, #5b9fe8)', height: outH },
          { key: 'tool_output', color: 'var(--obs-ctx-tool-output, #3ab4a4)', height: toolH },
        ]
      : []

    const tipY = branchAbove ? 66 + (idx % 4 === 2 ? 16 : 0) : 196 + (idx % 4 === 3 ? 16 : 0)
    const tipX = cx + 22
    const startY = branchAbove ? OVERVIEW_TRUNK_Y - barHeight - 10 : OVERVIEW_TRUNK_Y + 11
    const r = 7
    const branchPath = branchAbove
      ? `M ${cx} ${startY} V ${tipY + r} Q ${cx} ${tipY} ${cx + r} ${tipY} H ${tipX}`
      : `M ${cx} ${startY} V ${tipY - r} Q ${cx} ${tipY} ${cx + r} ${tipY} H ${tipX}`

    const modifiedFiles = [...modifiedSet]
    const primaryFile = modifiedFiles[0] ?? null
    const filePillLabel = primaryFile
      ? modifiedFiles.length > 1
        ? tr('trajectoryPath.fileAndMore', '{file} 等 {n} 个', {
            file: basenameOf(primaryFile),
            n: modifiedFiles.length,
          })
        : basenameOf(primaryFile)
      : null

    runningMs += Math.max(0, turn.durationMs ?? 0)

    const filterFile = selectedFileFilter.value
    const isFileRelated = Boolean(filterFile && modifiedFiles.includes(filterFile))
    const isDimmed = Boolean(filterFile && !isFileRelated)

    return {
      index: idx,
      turnNo: idx + 1,
      node: turn,
      cx,
      colX,
      colW: cw,
      branchAbove,
      branchPath,
      tipX,
      tipY,
      toolMixLabel,
      dominantKind,
      blockedCount,
      recoveredCount,
      unrecoveredCount,
      hasGovShield: turn.signals.length > 0,
      govSummary: turn.signals.map((s) => s.code).join(', '),
      deltaTokens: rawTokens,
      barHeight,
      barSlices,
      modifiedFiles,
      primaryFile,
      filePillLabel,
      cumulativeMs: runningMs,
      isDimmed,
      isFileRelated,
    }
  })
})

const allModifiedFiles = computed<string[]>(() => {
  const set = new Set<string>()
  for (const vm of overviewTurns.value) {
    for (const f of vm.modifiedFiles) set.add(f)
  }
  return [...set]
})

const relatedTurnCountForSelectedFile = computed<number>(() => {
  if (!selectedFileFilter.value) return 0
  return overviewTurns.value.filter((vm) => vm.isFileRelated).length
})

// --- Single-Turn Action Mode ---
const activeTurn = computed<TrajectoryNode | null>(
  () => turns.value[activeTurnIdx.value] ?? turns.value[0] ?? null,
)

const turnSteps = computed<TrajectoryNode[]>(() => {
  const tNode = activeTurn.value
  if (!tNode) return []
  return tNode.children.length > 0 ? tNode.children : [tNode]
})

watch(turnSteps, (list) => {
  if (selectedStepIdx.value >= list.length) {
    selectedStepIdx.value = Math.max(0, list.length - 1)
  }
})

const ACTION_SVG_H = 228
const ACTION_TRUNK_Y = 144
const stepColWidth = computed(() => Math.max(104, Math.round(138 * zoom.value)))

const actionSvgWidth = computed(() =>
  Math.max(360, PAD_L + Math.max(1, turnSteps.value.length) * stepColWidth.value + PAD_R),
)

interface ActionStepVm {
  index: number
  stepNo: number
  node: TrajectoryNode
  cx: number
  colX: number
  colW: number
  semanticKind: StepSemanticKind
  shortName: string
  totalMs: number
  modelSharePct: number
  toolSharePct: number
  deltaTokens: number
  barHeight: number
  readFile: string | null
  writeFile: string | null
  hasGovShield: boolean
}

const actionSteps = computed<ActionStepVm[]>(() => {
  const sw = stepColWidth.value
  const steps = turnSteps.value
  let maxDelta = 100
  const rawDeltas = steps.map((step) => {
    if (step.tokensUsed && step.tokensUsed > 0) return step.tokensUsed
    const chars =
      (step.summary?.length ?? 0)
      + (step.argumentsSummary?.length ?? 0)
      + step.evidence.reduce((s, e) => s + e.length, 0)
    return Math.max(32, Math.ceil(chars / 4))
  })
  for (const d of rawDeltas) {
    if (d > maxDelta) maxDelta = d
  }

  return steps.map((step, idx) => {
    const colX = PAD_L + idx * sw
    const cx = colX + sw / 2
    const sem = classifySemanticKind(step)
    const dur = Math.max(0, step.durationMs ?? 0)

    // Dual-color timing bar: blue model share + amber tool share
    let modelMs = 0
    let toolMs = 0
    if (step.kind === 'model_call') {
      modelMs = dur || 100
    } else if (step.kind === 'tool_call') {
      const prev = idx > 0 ? steps[idx - 1] : null
      modelMs = prev?.kind === 'model_call' ? Math.max(40, Math.round((prev.durationMs ?? 200) * 0.35)) : Math.round((dur || 100) * 0.25)
      toolMs = Math.max(20, dur || 80)
    } else {
      modelMs = Math.round((dur || 100) * 0.6)
      toolMs = Math.max(0, (dur || 100) - modelMs)
    }
    const sumPair = Math.max(1, modelMs + toolMs)
    const modelSharePct = Math.round((modelMs / sumPair) * 100)
    const toolSharePct = Math.max(0, 100 - modelSharePct)

    const delta = rawDeltas[idx] ?? 40
    const barHeight = layers.value.contextDelta
      ? Math.max(10, Math.round((delta / maxDelta) * 44))
      : 0

    const writeFile = step.editStats?.filePath || (sem === 'edit' ? step.resourcePath ?? null : null)
    const readFile = !writeFile && sem === 'read' && step.resourcePath ? step.resourcePath : null

    return {
      index: idx,
      stepNo: idx + 1,
      node: step,
      cx,
      colX,
      colW: sw,
      semanticKind: sem,
      shortName: shortToolName(step),
      totalMs: dur,
      modelSharePct,
      toolSharePct,
      deltaTokens: delta,
      barHeight,
      readFile,
      writeFile,
      hasGovShield: step.signals.length > 0,
    }
  })
})

const activeStep = computed<TrajectoryNode | null>(
  () => turnSteps.value[selectedStepIdx.value] ?? null,
)

function enterTurnMode(turnIndex: number): void {
  activeTurnIdx.value = turnIndex
  selectedStepIdx.value = 0
  showRawEvidence.value = false
  graphMode.value = 'turn'
  const stepOrTurn = turns.value[turnIndex]?.children[0] ?? turns.value[turnIndex] ?? null
  emit('select-node', stepOrTurn)
}

function returnToOverview(): void {
  graphMode.value = 'overview'
  diagnosisOpen.value = false
  emit('select-node', null)
}

function selectActionStep(idx: number): void {
  selectedStepIdx.value = idx
  showRawEvidence.value = false
  emit('select-node', turnSteps.value[idx] ?? null)
}

function stepActionDelta(delta: -1 | 1): void {
  const next = selectedStepIdx.value + delta
  if (next >= 0 && next < turnSteps.value.length) {
    selectActionStep(next)
  }
}

function toggleFileFilter(filePath: string | null): void {
  if (!filePath) return
  selectedFileFilter.value = selectedFileFilter.value === filePath ? null : filePath
}

function openDiagnosisFor(opts: {
  turnNo: number | null
  turnLabel: string | null
  stepNo: number | null
  node: TrajectoryNode | null
  preset?: DiagnosisPresetKey | null
}): void {
  diagnosisTarget.value = opts
  diagnosisOpen.value = true
}

function adjustZoom(delta: number): void {
  zoom.value = Math.min(2.2, Math.max(0.65, Math.round((zoom.value + delta) * 100) / 100))
}

function resetZoomAndFit(): void {
  zoom.value = 1
  if (scrollContainerRef.value) {
    scrollContainerRef.value.scrollLeft = 0
  }
}

function onPointerDown(event: PointerEvent): void {
  if (event.button !== 0) return
  const target = event.target as HTMLElement | null
  if (target?.closest('button, a, input, textarea, [data-interactive="true"]')) return
  const el = scrollContainerRef.value
  if (!el) return
  isDragging.value = true
  dragStartX = event.clientX
  dragStartScrollLeft = el.scrollLeft
}

function onPointerMove(event: PointerEvent): void {
  if (!isDragging.value || !scrollContainerRef.value) return
  const dx = event.clientX - dragStartX
  scrollContainerRef.value.scrollLeft = dragStartScrollLeft - dx
}

function onPointerUp(): void {
  isDragging.value = false
}
</script>

<template>
  <div class="tpc-root" data-testid="trajectory-path-canvas">
    <!-- Top mode / summary bar -->
    <div class="tpc-topbar">
      <div class="tpc-topbar__left">
        <button
          v-if="viewStyle === 'graph' && graphMode === 'turn'"
          type="button"
          class="tpc-back-btn"
          data-testid="path-back-overview"
          @click="returnToOverview"
        >
          <ArrowLeft :size="13" aria-hidden="true" />
          <span>{{ tr('trajectoryPath.backOverview', '全览') }}</span>
        </button>

        <template v-if="viewStyle === 'graph' && graphMode === 'overview'">
          <span class="tpc-meta-count mono">
            {{ tr('trajectoryPath.turnsCount', '{n} 轮执行记录', { n: turns.length }) }}
          </span>
        </template>
        <template v-else-if="viewStyle === 'graph' && activeTurn">
          <strong class="tpc-turn-heading">
            {{ tr('trajectoryPath.turnHeading', '第 {n} 轮', { n: activeTurnIdx + 1 }) }}
            <span class="tpc-turn-heading__sub">· {{ activeTurn.label }}</span>
          </strong>
        </template>
      </div>

      <div class="tpc-topbar__right">
        <span class="tpc-meta-dur mono">
          {{ tr('trajectoryPath.totalDuration', '总耗时 {dur}', { dur: formatDuration(trajectory.totalDurationMs) }) }}
        </span>

        <div class="tpc-view-switch" role="group" :aria-label="tr('trajectoryPath.viewSwitchAria', '视图模式')">
          <button
            type="button"
            class="tpc-view-btn"
            :class="{ 'is-active': viewStyle === 'graph' }"
            :aria-pressed="viewStyle === 'graph'"
            data-testid="path-view-graph"
            @click="viewStyle = 'graph'"
          >
            <GitCommitHorizontal :size="12" aria-hidden="true" />
            <span>{{ tr('trajectoryPath.graphView', '路径图') }}</span>
          </button>
          <button
            type="button"
            class="tpc-view-btn"
            :class="{ 'is-active': viewStyle === 'tree' }"
            :aria-pressed="viewStyle === 'tree'"
            data-testid="path-view-tree"
            @click="viewStyle = 'tree'"
          >
            <ListTree :size="12" aria-hidden="true" />
            <span>{{ tr('trajectoryPath.treeView', '明细树') }}</span>
          </button>
        </div>
      </div>
    </div>

    <!-- Compact Text Tree Fallback Toggle -->
    <div v-if="viewStyle === 'tree'" class="tpc-tree-wrap">
      <TrajectoryTreePanel
        :trajectory="trajectory"
        compact
        @select-node="emit('select-node', $event)"
        @diagnose-node="openDiagnosisFor({ turnNo: null, turnLabel: null, stepNo: null, node: $event })"
      />
    </div>

    <!-- Empty state -->
    <div v-else-if="turns.length === 0" class="tpc-empty" data-testid="trajectory-path-empty">
      {{ tr('trajectoryPath.empty', '当前回合暂无执行步骤记录') }}
    </div>

    <!-- SVG Graph Canvas (Overview Mode vs Single-Turn Action Mode) -->
    <template v-else>
      <div
        ref="scrollContainerRef"
        class="tpc-scroll"
        :class="{ 'is-dragging': isDragging }"
        @pointerdown="onPointerDown"
        @pointermove="onPointerMove"
        @pointerup="onPointerUp"
        @pointercancel="onPointerUp"
        @pointerleave="onPointerUp"
      >
        <!-- 1. OVERVIEW MODE -->
        <svg
          v-if="graphMode === 'overview'"
          :viewBox="`0 0 ${overviewSvgWidth} ${OVERVIEW_SVG_H}`"
          :width="overviewSvgWidth"
          :height="OVERVIEW_SVG_H"
          class="tpc-svg"
          data-testid="trajectory-overview-svg"
        >
          <!-- Context horizontal grid lines -->
          <template v-if="layers.contextDelta">
            <line
              :x1="PAD_L - 8"
              y1="98"
              :x2="overviewSvgWidth - PAD_R"
              y2="98"
              class="svg-grid-line"
            />
            <text :x="PAD_L - 12" y="101" text-anchor="end" class="svg-axis-label">
              +{{ formatTokensCompact(maxTurnTokens) }}
            </text>
            <text :x="PAD_L - 12" :y="OVERVIEW_TRUNK_Y + 3" text-anchor="end" class="svg-axis-label">
              0
            </text>
          </template>

          <!-- File lane left label -->
          <text
            v-if="layers.files && allModifiedFiles.length > 0"
            x="10"
            y="247"
            class="svg-lane-label is-change"
          >
            {{ tr('trajectoryPath.laneModified', '修改') }}
          </text>

          <!-- Main horizontal trunk -->
          <line
            :x1="PAD_L - 8"
            :y1="OVERVIEW_TRUNK_Y"
            :x2="overviewSvgWidth - PAD_R + 8"
            :y2="OVERVIEW_TRUNK_Y"
            class="svg-trunk"
          />

          <!-- Turn columns -->
          <g
            v-for="vm in overviewTurns"
            :key="vm.node.id"
            class="svg-turn-group"
            :class="{
              'is-dimmed': vm.isDimmed,
              'is-file-related': vm.isFileRelated,
              'is-error': vm.node.isError,
            }"
            data-interactive="true"
            :data-testid="`overview-turn-${vm.index}`"
          >
            <!-- Vertical column divider -->
            <line
              v-if="vm.index > 0"
              :x1="vm.colX"
              y1="44"
              :x2="vm.colX"
              y2="256"
              class="svg-col-divider"
            />
            <!-- Trunk '//' separator ticks -->
            <text
              v-if="vm.index > 0"
              :x="vm.colX"
              :y="OVERVIEW_TRUNK_Y + 3.5"
              text-anchor="middle"
              class="svg-trunk-break"
            >
              //
            </text>

            <!-- Top turn duration bracket -->
            <g
              v-if="layers.duration"
              class="svg-bracket"
              @click="enterTurnMode(vm.index)"
            >
              <text :x="vm.cx" y="16" text-anchor="middle" class="svg-bracket-title">
                {{ tr('trajectoryPath.turnShort', '第 {n} 轮', { n: vm.turnNo }) }}
              </text>
              <text :x="vm.cx" y="29" text-anchor="middle" class="svg-bracket-dur">
                {{ formatDuration(vm.node.durationMs) }}
              </text>
              <path
                :d="`M ${vm.colX + 8} 39 V 34 H ${vm.colX + vm.colW - 8} V 39`"
                class="svg-bracket-line"
                fill="none"
              />
            </g>

            <!-- Upward 7-color stacked context-delta bar -->
            <g
              v-if="layers.contextDelta && vm.barHeight > 0"
              class="svg-ctx-stack"
              @click="enterTurnMode(vm.index)"
            >
              <rect
                v-if="vm.barSlices[0]"
                :x="vm.cx - 13"
                :y="OVERVIEW_TRUNK_Y - vm.barHeight"
                width="26"
                :height="vm.barSlices[0].height"
                :style="{ fill: vm.barSlices[0].color }"
                rx="1.5"
              />
              <rect
                v-if="vm.barSlices[1]"
                :x="vm.cx - 13"
                :y="OVERVIEW_TRUNK_Y - vm.barHeight + (vm.barSlices[0]?.height ?? 0)"
                width="26"
                :height="vm.barSlices[1].height"
                :style="{ fill: vm.barSlices[1].color }"
              />
              <rect
                v-if="vm.barSlices[2]"
                :x="vm.cx - 13"
                :y="OVERVIEW_TRUNK_Y - (vm.barSlices[2]?.height ?? 0)"
                width="26"
                :height="vm.barSlices[2].height"
                :style="{ fill: vm.barSlices[2].color }"
              />
            </g>

            <!-- Numbered turn pill base on trunk -->
            <g
              class="svg-turn-pill"
              :data-testid="`overview-turn-pill-${vm.index}`"
              @click="enterTurnMode(vm.index)"
            >
              <rect
                :x="vm.cx - 13"
                :y="OVERVIEW_TRUNK_Y - 9"
                width="26"
                height="18"
                rx="4"
                class="svg-turn-pill__bg"
                :class="{ 'is-error': vm.node.isError }"
              />
              <text
                :x="vm.cx"
                :y="OVERVIEW_TRUNK_Y + 3.5"
                text-anchor="middle"
                class="svg-turn-pill__num"
              >
                {{ vm.turnNo }}
              </text>
            </g>

            <!-- Orthogonal rounded elbow branch (above or below trunk) -->
            <g
              v-if="layers.toolResults || layers.retries"
              class="svg-branch"
              :data-testid="`overview-branch-${vm.index}`"
              @click="enterTurnMode(vm.index)"
            >
              <path :d="vm.branchPath" class="svg-branch__line" fill="none" />

              <!-- Blocked red '✕' marker vs semantic endpoint dot -->
              <g
                v-if="layers.retries && vm.unrecoveredCount > 0"
                :transform="`translate(${vm.tipX + 4}, ${vm.tipY})`"
                data-testid="branch-blocked-marker"
              >
                <path d="M -4 -4 L 4 4 M 4 -4 L -4 4" class="svg-blocked-x" />
              </g>
              <circle
                v-else
                :cx="vm.tipX + 4"
                :cy="vm.tipY"
                r="3.5"
                class="svg-branch__dot"
                :class="`kind-${vm.dominantKind}`"
              />

              <!-- Governance shield indicator -->
              <g
                v-if="vm.hasGovShield"
                :transform="`translate(${vm.tipX + 15}, ${vm.tipY})`"
                data-testid="branch-gov-shield"
              >
                <title>{{ vm.govSummary }}</title>
                <path
                  d="M 0 -5 L 4.5 -3 V 1 C 4.5 3.6 2.4 5.2 0 6 C -2.4 5.2 -4.5 3.6 -4.5 1 V -3 Z"
                  class="svg-gov-shield"
                />
              </g>

              <text
                :x="vm.cx"
                :y="vm.branchAbove ? vm.tipY - 8 : vm.tipY + 15"
                text-anchor="middle"
                class="svg-branch__label"
              >
                {{ vm.toolMixLabel }}
              </text>
            </g>

            <!-- Quick diagnose trigger on turn -->
            <g
              class="svg-diagnose-hit"
              :transform="`translate(${vm.cx + 18}, ${OVERVIEW_TRUNK_Y - 12})`"
              :data-testid="`overview-diagnose-turn-${vm.index}`"
              @click.stop="openDiagnosisFor({ turnNo: vm.turnNo, turnLabel: vm.node.label, stepNo: null, node: vm.node })"
            >
              <title>{{ tr('trajectoryPath.diagnoseTurn', '诊断第 {n} 轮', { n: vm.turnNo }) }}</title>
              <rect x="-7" y="-7" width="14" height="14" rx="3" class="svg-diagnose-hit__bg" />
              <text x="0" y="3.5" text-anchor="middle" class="svg-diagnose-hit__txt">?</text>
            </g>

            <!-- Bottom modified-file lane pill -->
            <g
              v-if="layers.files && vm.primaryFile && vm.filePillLabel"
              class="svg-file-pill is-change"
              :class="{ 'is-selected': selectedFileFilter === vm.primaryFile }"
              data-testid="overview-file-pill"
              @click.stop="toggleFileFilter(vm.primaryFile)"
            >
              <rect
                :x="vm.cx - Math.min(58, Math.max(36, vm.filePillLabel.length * 4.2 + 16))"
                y="234"
                :width="Math.min(116, Math.max(72, vm.filePillLabel.length * 8.4 + 32))"
                height="20"
                rx="5"
                class="svg-file-pill__bg"
              />
              <text :x="vm.cx" y="247.5" text-anchor="middle" class="svg-file-pill__txt">
                {{ tr('trajectoryPath.tagModified', '[改]') }} {{ vm.filePillLabel }}
              </text>
            </g>

            <!-- Time axis tick -->
            <text
              :x="vm.cx"
              y="271"
              text-anchor="middle"
              class="svg-axis-label"
            >
              {{ formatDuration(vm.cumulativeMs) }}
            </text>
          </g>
        </svg>

        <!-- 2. SINGLE-TURN ACTION MODE -->
        <svg
          v-else
          :viewBox="`0 0 ${actionSvgWidth} ${ACTION_SVG_H}`"
          :width="actionSvgWidth"
          :height="ACTION_SVG_H"
          class="tpc-svg"
          data-testid="trajectory-action-svg"
        >
          <!-- Lane labels -->
          <text v-if="layers.files" x="10" y="54" class="svg-lane-label is-read">
            {{ tr('trajectoryPath.laneRead', '读取') }}
          </text>
          <text v-if="layers.files" x="10" y="205" class="svg-lane-label is-change">
            {{ tr('trajectoryPath.laneModified', '修改') }}
          </text>

          <!-- Context baseline dashed guide -->
          <line
            v-if="layers.contextDelta"
            :x1="PAD_L - 8"
            y1="82"
            :x2="actionSvgWidth - PAD_R"
            y2="82"
            class="svg-grid-line"
          />

          <!-- Main horizontal trunk -->
          <line
            :x1="PAD_L - 8"
            :y1="ACTION_TRUNK_Y"
            :x2="actionSvgWidth - PAD_R + 8"
            :y2="ACTION_TRUNK_Y"
            class="svg-trunk"
          />

          <!-- Step columns -->
          <g
            v-for="step in actionSteps"
            :key="step.node.id"
            class="svg-step-group"
            :class="{
              'is-selected': selectedStepIdx === step.index,
              'is-error': step.node.isError,
            }"
            data-interactive="true"
            :data-testid="`action-step-node-${step.index}`"
            @click="selectActionStep(step.index)"
          >
            <!-- Top step label + Dual-color timing bar (blue model + amber tool) -->
            <g v-if="layers.duration">
              <text :x="step.cx" y="18" text-anchor="middle" class="svg-bracket-title">
                {{ tr('trajectoryPath.stepTimingTitle', '第 {n} 步 · {dur}', {
                  n: step.stepNo,
                  dur: formatDuration(step.totalMs),
                }) }}
              </text>
              <rect
                :x="step.cx - 44"
                y="24"
                :width="(88 * step.modelSharePct) / 100"
                height="3.5"
                rx="1.5"
                class="svg-timing-model"
              />
              <rect
                :x="step.cx - 44 + (88 * step.modelSharePct) / 100"
                y="24"
                :width="(88 * step.toolSharePct) / 100"
                height="3.5"
                rx="1.5"
                class="svg-timing-tool"
              />
            </g>

            <!-- Above-step read file pill -->
            <g v-if="layers.files && step.readFile" class="svg-file-pill is-read" data-testid="action-read-pill">
              <rect
                :x="step.cx - 48"
                y="38"
                width="96"
                height="19"
                rx="4.5"
                class="svg-file-pill__bg"
              />
              <text :x="step.cx" y="50.5" text-anchor="middle" class="svg-file-pill__txt">
                {{ tr('trajectoryPath.tagRead', '[读]') }} {{ basenameOf(step.readFile) }}
              </text>
            </g>

            <!-- Upward +XX context increment bar -->
            <g v-if="layers.contextDelta && step.barHeight > 0">
              <rect
                :x="step.cx - 12"
                :y="ACTION_TRUNK_Y - 16 - step.barHeight"
                width="24"
                :height="Math.round(step.barHeight * 0.6)"
                style="fill: var(--obs-ctx-assistant, #5b9fe8)"
                rx="1.5"
              />
              <rect
                :x="step.cx - 12"
                :y="ACTION_TRUNK_Y - 16 - Math.round(step.barHeight * 0.4)"
                width="24"
                :height="Math.round(step.barHeight * 0.4)"
                style="fill: var(--obs-ctx-tool-output, #3ab4a4)"
              />
              <text
                :x="step.cx"
                :y="ACTION_TRUNK_Y - 20 - step.barHeight"
                text-anchor="middle"
                class="svg-step-delta"
              >
                +{{ formatTokensCompact(step.deltaTokens) }}
              </text>
            </g>

            <!-- Circular semantic node on trunk -->
            <circle
              :cx="step.cx"
              :cy="ACTION_TRUNK_Y"
              r="15"
              class="svg-step-circle"
              :class="[`kind-${step.semanticKind}`, { 'is-error': step.node.isError, 'is-selected': selectedStepIdx === step.index }]"
            />

            <!-- Inner semantic glyph -->
            <g :transform="`translate(${step.cx}, ${ACTION_TRUNK_Y})`" class="svg-step-glyph" :class="{ 'is-error': step.node.isError }">
              <!-- Read file icon -->
              <path
                v-if="step.semanticKind === 'read'"
                d="M -4 -5.5 H 1.5 L 4.5 -2.5 V 5.5 H -4 Z M 1.5 -5.5 V -2.5 H 4.5"
                fill="none"
              />
              <!-- Edit pencil icon -->
              <path
                v-else-if="step.semanticKind === 'edit'"
                d="M -4 4.5 L -3 1 L 2.5 -4.5 L 4.5 -2.5 L -1 3 Z"
                fill="none"
              />
              <!-- Command terminal >_ icon -->
              <path
                v-else-if="step.semanticKind === 'command'"
                d="M -4.5 -3 L -1 0 L -4.5 3 M 0.5 3.5 H 4.5"
                fill="none"
              />
              <!-- Search magnifier -->
              <g v-else-if="step.semanticKind === 'search'">
                <circle cx="-1" cy="-1" r="3.5" fill="none" />
                <line x1="1.6" y1="1.6" x2="4.5" y2="4.5" />
              </g>
              <!-- Model CPU -->
              <rect
                v-else-if="step.semanticKind === 'model'"
                x="-4"
                y="-4"
                width="8"
                height="8"
                rx="1.5"
                fill="none"
              />
              <!-- Step check -->
              <path
                v-else
                d="M -4 0.5 L -1 3.5 L 4.5 -3"
                fill="none"
              />
            </g>

            <!-- Governance shield on circle corner -->
            <g
              v-if="step.hasGovShield"
              :transform="`translate(${step.cx + 12}, ${ACTION_TRUNK_Y - 12})`"
              data-testid="step-gov-shield"
            >
              <path
                d="M 0 -4.5 L 4 -2.5 V 1 C 4 3.2 2 4.6 0 5.4 C -2 4.6 -4 3.2 -4 1 V -2.5 Z"
                class="svg-gov-shield"
              />
            </g>

            <!-- Short semantic label under node -->
            <text
              :x="step.cx"
              :y="ACTION_TRUNK_Y + 29"
              text-anchor="middle"
              class="svg-step-name"
              :class="[`kind-${step.semanticKind}`, { 'is-error': step.node.isError }]"
            >
              {{ step.shortName }}
            </text>

            <!-- Below-step modified file pill -->
            <g v-if="layers.files && step.writeFile" class="svg-file-pill is-change" data-testid="action-write-pill">
              <rect
                :x="step.cx - 48"
                y="191"
                width="96"
                height="19"
                rx="4.5"
                class="svg-file-pill__bg"
              />
              <text :x="step.cx" y="203.5" text-anchor="middle" class="svg-file-pill__txt">
                {{ tr('trajectoryPath.tagModified', '[改]') }} {{ basenameOf(step.writeFile) }}
              </text>
            </g>
          </g>
        </svg>
      </div>

      <!-- Active File Selection Strip (Overview Mode) -->
      <div
        v-if="graphMode === 'overview' && selectedFileFilter"
        class="tpc-file-filter-bar"
        data-testid="active-file-filter-bar"
      >
        <div class="tpc-file-filter-bar__info">
          <span class="tpc-muted">{{ tr('trajectoryPath.selectedFile', '已选文件') }}</span>
          <strong class="mono">{{ selectedFileFilter }}</strong>
          <span class="tpc-muted mono">
            · {{ tr('trajectoryPath.touchingTurns', '关联 {n} 轮', { n: relatedTurnCountForSelectedFile }) }}
          </span>
        </div>
        <button
          type="button"
          class="tpc-clear-btn"
          data-testid="clear-file-filter"
          @click="selectedFileFilter = null"
        >
          <X :size="11" aria-hidden="true" />
          <span>{{ tr('trajectoryPath.clearFileFilter', '清除过滤') }}</span>
        </button>
      </div>

      <!-- Inline Diagnosis Popover Overlay -->
      <div v-if="diagnosisOpen" class="tpc-popover-anchor">
        <TrajectoryDiagnosisPopover
          :open="diagnosisOpen"
          :run-id="runId ?? trajectory.runId"
          :session-id="sessionId ?? trajectory.sessionId"
          :turn-no="diagnosisTarget.turnNo"
          :turn-label="diagnosisTarget.turnLabel"
          :step-no="diagnosisTarget.stepNo"
          :node="diagnosisTarget.node"
          :trajectory="trajectory"
          :treemap="treemap"
          :buckets="buckets"
          :diagnostics="diagnostics"
          :stale-cached-at="staleCachedAt"
          :initial-preset="diagnosisTarget.preset"
          @close="diagnosisOpen = false"
          @injected="emit('diagnose-draft', $event.draft)"
        />
      </div>

      <!-- Borderless Step Detail Pane (Single-Turn Action Mode) -->
      <section
        v-if="graphMode === 'turn' && activeStep"
        class="tpc-step-detail"
        data-testid="action-step-detail"
      >
        <div class="tpc-step-detail__meta">
          <div class="tpc-step-detail__meta-left mono">
            <strong>{{ tr('trajectoryPath.stepIndex', '第 {n} 步', { n: selectedStepIdx + 1 }) }}</strong>
            <span>{{ formatDuration(activeStep.durationMs) }}</span>
            <span class="tpc-status-tag" :class="{ 'is-error': activeStep.isError }">
              {{ activeStep.status }}
            </span>
            <span v-if="activeStep.editStats" class="tpc-diff-inline">
              <span class="diff-add">+{{ activeStep.editStats.added }}</span>
              <span>/</span>
              <span class="diff-del">-{{ activeStep.editStats.removed }}</span>
            </span>
          </div>

          <div class="tpc-step-detail__actions">
            <button
              type="button"
              class="tpc-diagnose-btn"
              data-testid="step-diagnose-btn"
              @click="openDiagnosisFor({
                turnNo: activeTurnIdx + 1,
                turnLabel: activeTurn?.label ?? null,
                stepNo: selectedStepIdx + 1,
                node: activeStep,
              })"
            >
              <Sparkles :size="12" aria-hidden="true" />
              <span>{{ tr('trajectoryPath.diagnoseBtn', '诊断') }}</span>
            </button>

            <div class="tpc-step-nav">
              <button
                type="button"
                class="tpc-nav-btn"
                :disabled="selectedStepIdx <= 0"
                :aria-label="tr('trajectoryPath.prevStep', '上一步')"
                data-testid="step-prev-btn"
                @click="stepActionDelta(-1)"
              >
                <ChevronLeft :size="15" />
              </button>
              <button
                type="button"
                class="tpc-nav-btn"
                :disabled="selectedStepIdx >= turnSteps.length - 1"
                :aria-label="tr('trajectoryPath.nextStep', '下一步')"
                data-testid="step-next-btn"
                @click="stepActionDelta(1)"
              >
                <ChevronRight :size="15" />
              </button>
            </div>
          </div>
        </div>

        <div class="tpc-step-detail__body">
          <div class="tpc-op-header">
            <span class="tpc-op-tool mono">{{ activeStep.toolId || activeStep.label }}</span>
            <span v-if="activeStep.resourcePath" class="tpc-op-path mono">{{ activeStep.resourcePath }}</span>
          </div>

          <p v-if="activeStep.command" class="tpc-op-cmd mono">$ {{ activeStep.command }}</p>
          <p v-else-if="activeStep.argumentsSummary" class="tpc-op-args mono">{{ activeStep.argumentsSummary }}</p>

          <p v-if="activeStep.summary" class="tpc-op-summary" :class="{ 'is-error': activeStep.isError }">
            {{ activeStep.summary }}
          </p>

          <div v-if="activeStep.signals.length > 0" class="tpc-step-signals">
            <span
              v-for="sig in activeStep.signals"
              :key="sig.code"
              class="tpc-sig-pill mono"
              :class="`sev-${sig.severity}`"
            >
              {{ sig.code }}<template v-if="sig.count && sig.count > 1"> ×{{ sig.count }}</template>
            </span>
          </div>

          <!-- Error Code Frames -->
          <div
            v-for="frame in activeStep.codeFrames"
            :key="frame.id"
            class="tpc-frame mono"
            data-testid="step-code-frame"
          >
            <div class="tpc-frame__head">
              <span>{{ frame.title }}</span>
              <span v-if="frame.errorCategory" class="tpc-frame__err">{{ frame.errorCategory }}</span>
            </div>
            <pre class="tpc-frame__lines"><div
              v-for="(fl, lIdx) in frame.lines"
              :key="lIdx"
              class="tpc-frame__line"
              :class="{ 'is-hi': fl.highlight }"
            ><span class="tpc-frame__ln">{{ fl.lineNumber ?? '·' }}</span><span>{{ fl.text }}</span></div></pre>
          </div>

          <!-- Raw Output Toggle -->
          <div v-if="activeStep.evidence.length > 0" class="tpc-raw-block">
            <button
              type="button"
              class="tpc-raw-toggle"
              data-testid="step-raw-toggle"
              @click="showRawEvidence = !showRawEvidence"
            >
              {{ showRawEvidence
                ? tr('trajectoryPath.hideRawOutput', '收起原始输出')
                : tr('trajectoryPath.showRawOutput', '查看原始输出 ({n})', { n: activeStep.evidence.length }) }}
            </button>
            <pre v-if="showRawEvidence" class="tpc-raw-pre mono" data-testid="step-raw-output">{{ activeStep.evidence.join('\n') }}</pre>
          </div>
        </div>
      </section>

      <!-- Layer Toggles + Zoom Footer -->
      <footer class="tpc-observe-bar">
        <div class="tpc-layers" role="group" :aria-label="tr('trajectoryPath.layersLabel', '观察图层')">
          <span class="tpc-observe-label">{{ tr('trajectoryPath.observeLabel', '观察') }}</span>
          <label class="tpc-layer-chk">
            <input v-model="layers.toolResults" type="checkbox" data-testid="layer-toggle-tools" />
            <span>{{ tr('trajectoryPath.layerTools', '工具结果') }}</span>
          </label>
          <label class="tpc-layer-chk">
            <input v-model="layers.duration" type="checkbox" data-testid="layer-toggle-duration" />
            <span>{{ tr('trajectoryPath.layerDuration', '耗时') }}</span>
          </label>
          <label class="tpc-layer-chk">
            <input v-model="layers.retries" type="checkbox" data-testid="layer-toggle-retries" />
            <span>{{ tr('trajectoryPath.layerRetries', '停顿与重试') }}</span>
          </label>
          <label class="tpc-layer-chk">
            <input v-model="layers.contextDelta" type="checkbox" data-testid="layer-toggle-context" />
            <span>{{ tr('trajectoryPath.layerContext', '上下文增量') }}</span>
          </label>
          <label class="tpc-layer-chk">
            <input v-model="layers.files" type="checkbox" data-testid="layer-toggle-files" />
            <span>{{ tr('trajectoryPath.layerFiles', '文件') }}</span>
          </label>
        </div>

        <div class="tpc-zoom-controls">
          <button
            type="button"
            class="tpc-help-toggle"
            @click="showLegendHelp = !showLegendHelp"
          >
            {{ tr('trajectoryPath.legendHelp', '符号说明') }}
          </button>

          <div class="tpc-zoom-box mono" role="group" :aria-label="tr('trajectoryPath.zoomAria', '缩放控制')">
            <button
              type="button"
              class="tpc-zoom-btn"
              data-testid="path-zoom-out"
              @click="adjustZoom(-0.2)"
            >
              <Minus :size="11" />
            </button>
            <button
              type="button"
              class="tpc-zoom-fit"
              data-testid="path-zoom-reset"
              @click="resetZoomAndFit"
            >
              {{ tr('trajectoryPath.zoomFit', '全览') }}
            </button>
            <button
              type="button"
              class="tpc-zoom-btn"
              data-testid="path-zoom-in"
              @click="adjustZoom(0.2)"
            >
              <Plus :size="11" />
            </button>
          </div>
        </div>

        <p v-if="showLegendHelp" class="tpc-legend-note">
          {{ tr('trajectoryPath.legendText', '主干柱表示各轮上下文增量；折线分支标出工具调用、红色 ✕ 受阻与恢复结果；点击任意轮次进入单步轨，点击「诊断」生成带证据的对话草稿。') }}
        </p>
      </footer>
    </template>
  </div>
</template>

<style scoped>
.tpc-root {
  position: relative;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--bg-primary);
}

.tpc-topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 14px;
  border-bottom: 1px solid var(--border-muted);
  flex-wrap: wrap;
}

.tpc-topbar__left,
.tpc-topbar__right {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.tpc-back-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 8px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
}

.tpc-back-btn:hover {
  border-color: var(--border-default);
  background: var(--bg-secondary);
}

.tpc-meta-count,
.tpc-meta-dur {
  font-size: 11px;
  color: var(--text-muted);
}

.tpc-turn-heading {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 220px;
}

.tpc-turn-heading__sub {
  font-weight: 400;
  color: var(--text-secondary);
}

.tpc-view-switch {
  display: inline-flex;
  align-items: center;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  padding: 1px;
  background: var(--bg-secondary);
}

.tpc-view-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 7px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.tpc-view-btn.is-active {
  background: var(--bg-primary);
  color: var(--text-primary);
  font-weight: 500;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.05);
}

.tpc-tree-wrap {
  padding: 4px 0;
}

.tpc-empty {
  padding: 28px 16px;
  text-align: center;
  font-size: 12px;
  color: var(--text-muted);
}

.tpc-scroll {
  width: 100%;
  overflow-x: auto;
  overflow-y: hidden;
  cursor: grab;
  user-select: none;
  border-bottom: 1px solid var(--border-muted);
}

.tpc-scroll.is-dragging {
  cursor: grabbing;
}

.tpc-svg {
  display: block;
  min-width: 100%;
  font-family: inherit;
}

/* SVG Geist visual primitives */
.svg-grid-line {
  stroke: var(--border-muted);
  stroke-width: 0.9;
  stroke-dasharray: 3 5;
}

.svg-col-divider {
  stroke: var(--border-muted);
  stroke-width: 0.9;
  stroke-dasharray: 4 5;
  opacity: 0.7;
}

.svg-axis-label {
  font-family: var(--font-mono);
  font-size: 9.5px;
  fill: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.svg-lane-label {
  font-size: 10.5px;
  font-weight: 500;
  fill: var(--text-secondary);
}

.svg-lane-label.is-read {
  fill: var(--obs-ctx-assistant, #5b9fe8);
}

.svg-lane-label.is-change {
  fill: #c87419;
}

.svg-trunk {
  stroke: color-mix(in srgb, var(--text-primary) 62%, #204965);
  stroke-width: 2.2;
}

.svg-trunk-break {
  font-family: var(--font-mono);
  font-size: 9px;
  fill: var(--text-muted);
}

.svg-turn-group {
  transition: opacity 140ms ease;
}

.svg-turn-group.is-dimmed {
  opacity: 0.28;
}

.svg-bracket {
  cursor: pointer;
}

.svg-bracket-title {
  font-size: 11px;
  font-weight: 500;
  fill: var(--text-primary);
}

.svg-bracket-dur {
  font-family: var(--font-mono);
  font-size: 10px;
  fill: var(--text-muted);
}

.svg-bracket-line {
  stroke: var(--border-default);
  stroke-width: 1;
}

.svg-ctx-stack {
  cursor: pointer;
}

.svg-turn-pill {
  cursor: pointer;
}

.svg-turn-pill__bg {
  fill: var(--bg-primary);
  stroke: var(--border-default);
  stroke-width: 1.2;
}

.svg-turn-pill__bg.is-error {
  stroke: var(--accent-danger, #c72b31);
}

.svg-turn-pill:hover .svg-turn-pill__bg {
  stroke: var(--text-primary);
  stroke-width: 1.6;
}

.svg-turn-pill__num {
  font-family: var(--font-mono);
  font-size: 10px;
  font-weight: 600;
  fill: var(--text-primary);
}

.svg-branch {
  cursor: pointer;
}

.svg-branch__line {
  stroke: var(--text-muted);
  stroke-width: 1.25;
}

.svg-branch:hover .svg-branch__line {
  stroke: var(--text-primary);
  stroke-width: 1.75;
}

.svg-branch__dot {
  fill: var(--obs-ctx-assistant, #5b9fe8);
  stroke: var(--bg-primary);
  stroke-width: 1;
}

.svg-branch__dot.kind-edit {
  fill: #d49b00;
}

.svg-branch__dot.kind-command {
  fill: var(--text-primary);
}

.svg-branch__dot.kind-search {
  fill: #8053e3;
}

.svg-blocked-x {
  stroke: var(--accent-danger, #c72b31);
  stroke-width: 1.9;
  stroke-linecap: round;
}

.svg-gov-shield {
  fill: color-mix(in srgb, #d49b00 22%, var(--bg-primary));
  stroke: #d49b00;
  stroke-width: 1.2;
}

.svg-branch__label {
  font-size: 11px;
  fill: var(--text-primary);
  paint-order: stroke fill;
  stroke: var(--bg-primary);
  stroke-width: 3px;
  stroke-linejoin: round;
}

.svg-diagnose-hit {
  cursor: pointer;
  opacity: 0.65;
  transition: opacity 120ms ease;
}

.svg-turn-group:hover .svg-diagnose-hit,
.svg-diagnose-hit:hover {
  opacity: 1;
}

.svg-diagnose-hit__bg {
  fill: var(--bg-secondary);
  stroke: var(--border-muted);
  stroke-width: 1;
}

.svg-diagnose-hit__txt {
  font-family: var(--font-mono);
  font-size: 9.5px;
  font-weight: 600;
  fill: var(--text-secondary);
}

.svg-file-pill {
  cursor: pointer;
}

.svg-file-pill.is-read .svg-file-pill__bg {
  fill: color-mix(in srgb, var(--obs-ctx-assistant, #5b9fe8) 12%, var(--bg-primary));
  stroke: color-mix(in srgb, var(--obs-ctx-assistant, #5b9fe8) 55%, var(--border-default));
  stroke-width: 1;
}

.svg-file-pill.is-change .svg-file-pill__bg {
  fill: color-mix(in srgb, #d49b00 12%, var(--bg-primary));
  stroke: color-mix(in srgb, #d49b00 55%, var(--border-default));
  stroke-width: 1;
}

.svg-file-pill.is-selected .svg-file-pill__bg {
  fill: var(--text-primary);
  stroke: var(--text-primary);
}

.svg-file-pill__txt {
  font-size: 10.5px;
  fill: var(--text-primary);
}

.svg-file-pill.is-selected .svg-file-pill__txt {
  fill: var(--text-on-primary, var(--bg-primary));
  font-weight: 500;
}

/* Single-turn Action Mode SVG primitives */
.svg-step-group {
  cursor: pointer;
}

.svg-timing-model {
  fill: var(--obs-ctx-assistant, #5b9fe8);
}

.svg-timing-tool {
  fill: #d49b00;
}

.svg-step-delta {
  font-family: var(--font-mono);
  font-size: 10px;
  fill: var(--text-secondary);
}

.svg-step-circle {
  fill: var(--bg-primary);
  stroke: var(--border-default);
  stroke-width: 1.5;
  transition: stroke-width 110ms ease, stroke 110ms ease;
}

.svg-step-circle.kind-read,
.svg-step-circle.kind-model {
  stroke: var(--obs-ctx-assistant, #5b9fe8);
}

.svg-step-circle.kind-edit {
  stroke: #d49b00;
}

.svg-step-circle.is-error {
  stroke: var(--accent-danger, #c72b31);
}

.svg-step-circle.is-selected {
  stroke-width: 2.4;
}

.svg-step-glyph {
  stroke: var(--text-primary);
  stroke-width: 1.4;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.svg-step-glyph.is-error {
  stroke: var(--accent-danger, #c72b31);
}

.svg-step-name {
  font-family: var(--font-mono);
  font-size: 11px;
  fill: var(--text-secondary);
}

.svg-step-name.kind-edit {
  fill: #b87800;
}

.svg-step-name.is-error {
  fill: var(--accent-danger, #c72b31);
  font-weight: 600;
}

/* File filter banner */
.tpc-file-filter-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 7px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  font-size: 11.5px;
}

.tpc-file-filter-bar__info {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  min-width: 0;
}

.tpc-muted {
  color: var(--text-muted);
}

.tpc-clear-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 23px;
  padding: 0 8px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 11px;
  cursor: pointer;
}

.tpc-popover-anchor {
  padding: 10px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  display: flex;
  justify-content: center;
}

/* Borderless Geist Step Detail Pane */
.tpc-step-detail {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px 14px;
  border-bottom: 1px solid var(--border-muted);
  background: var(--bg-primary);
}

.tpc-step-detail__meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.tpc-step-detail__meta-left {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 11.5px;
  color: var(--text-secondary);
}

.tpc-step-detail__meta-left strong {
  color: var(--text-primary);
}

.tpc-status-tag {
  padding: 1px 6px;
  border: 1px solid var(--border-muted);
  border-radius: 4px;
  font-size: 10px;
  color: var(--text-secondary);
}

.tpc-status-tag.is-error {
  border-color: color-mix(in srgb, var(--accent-danger, #c72b31) 40%, var(--border-muted));
  color: var(--text-error, #c72b31);
  background: var(--bg-status-danger);
}

.tpc-diff-inline {
  display: inline-flex;
  gap: 2px;
  font-size: 11px;
}

.diff-add {
  color: var(--accent-success, #16a34a);
}

.diff-del {
  color: var(--text-error, #c72b31);
}

.tpc-step-detail__actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.tpc-diagnose-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 26px;
  padding: 0 9px;
  border: 1px solid var(--border-default);
  border-radius: 6px;
  background: var(--bg-primary);
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
}

.tpc-diagnose-btn:hover {
  border-color: var(--text-primary);
  background: var(--bg-secondary);
}

.tpc-step-nav {
  display: inline-flex;
  gap: 2px;
}

.tpc-nav-btn {
  width: 26px;
  height: 26px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--text-primary);
  cursor: pointer;
}

.tpc-nav-btn:hover:not(:disabled) {
  background: var(--bg-hover);
}

.tpc-nav-btn:disabled {
  opacity: 0.28;
  cursor: default;
}

.tpc-step-detail__body {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.tpc-op-header {
  display: flex;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
}

.tpc-op-tool {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.tpc-op-path {
  font-size: 11.5px;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}

.tpc-op-cmd,
.tpc-op-args {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}

.tpc-op-summary {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.5;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.tpc-op-summary.is-error {
  color: var(--text-error, #c72b31);
}

.tpc-step-signals {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.tpc-sig-pill {
  padding: 1px 6px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  font-size: 10px;
}

.tpc-sig-pill.sev-danger {
  background: var(--bg-status-danger);
  color: var(--text-error, #c72b31);
}

.tpc-sig-pill.sev-warn {
  background: var(--bg-status-warn);
  color: var(--text-primary);
}

.tpc-frame {
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  overflow: hidden;
  background: var(--bg-secondary);
}

.tpc-frame__head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 8px;
  border-bottom: 1px solid var(--border-muted);
  font-size: 10.5px;
  color: var(--text-secondary);
}

.tpc-frame__err {
  color: var(--text-error, #c72b31);
}

.tpc-frame__lines {
  margin: 0;
  padding: 5px 0;
  font-size: 11px;
  overflow-x: auto;
}

.tpc-frame__line {
  display: grid;
  grid-template-columns: 34px 1fr;
  gap: 8px;
  padding: 1px 8px;
}

.tpc-frame__line.is-hi {
  background: var(--bg-status-danger);
  color: var(--text-error, #c72b31);
}

.tpc-frame__ln {
  text-align: right;
  color: var(--text-muted);
}

.tpc-raw-block {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.tpc-raw-toggle {
  align-self: flex-start;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  text-decoration: underline;
  text-underline-offset: 3px;
  cursor: pointer;
}

.tpc-raw-toggle:hover {
  color: var(--text-primary);
}

.tpc-raw-pre {
  margin: 0;
  padding: 8px 10px;
  border-top: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  border-radius: 6px;
  font-size: 11px;
  line-height: 1.45;
  max-height: 160px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
}

/* Observe & Zoom Footer */
.tpc-observe-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px 14px;
  padding: 8px 14px;
  flex-wrap: wrap;
}

.tpc-layers {
  display: flex;
  align-items: center;
  gap: 6px 12px;
  flex-wrap: wrap;
}

.tpc-observe-label {
  font-size: 11px;
  color: var(--text-muted);
}

.tpc-layer-chk {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 11.5px;
  color: var(--text-primary);
  cursor: pointer;
}

.tpc-layer-chk input {
  width: 13px;
  height: 13px;
  accent-color: var(--text-primary);
  cursor: pointer;
}

.tpc-zoom-controls {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.tpc-help-toggle {
  border: 0;
  padding: 2px 4px;
  background: transparent;
  color: var(--text-muted);
  font-size: 11px;
  cursor: pointer;
}

.tpc-help-toggle:hover {
  color: var(--text-primary);
}

.tpc-zoom-box {
  display: inline-flex;
  align-items: center;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  padding: 1px;
  background: var(--bg-primary);
}

.tpc-zoom-btn,
.tpc-zoom-fit {
  height: 22px;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font-size: 11px;
  cursor: pointer;
  border-radius: 4px;
}

.tpc-zoom-btn {
  width: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.tpc-zoom-fit {
  padding: 0 7px;
}

.tpc-zoom-btn:hover,
.tpc-zoom-fit:hover {
  background: var(--bg-secondary);
}

.tpc-legend-note {
  flex: 1 0 100%;
  margin: 0;
  font-size: 11px;
  line-height: 1.45;
  color: var(--text-muted);
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
