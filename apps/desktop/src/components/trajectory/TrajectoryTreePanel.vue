<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  Bot,
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Cpu,
  Layers3,
  ShieldAlert,
  Sparkles,
  Wrench,
} from '@lucide/vue'
import {
  findTrajectoryNode,
  type GovernanceSignalCode,
  type TrajectoryNode,
  type TrajectorySummary,
} from '@/lib/trajectoryBuilder'

const props = withDefaults(defineProps<{
  trajectory: TrajectorySummary
  selectedNodeId?: string | null
  compact?: boolean
}>(), {
  selectedNodeId: null,
  compact: false,
})

const emit = defineEmits<{
  'select-node': [node: TrajectoryNode | null]
  'diagnose-node': [node: TrajectoryNode]
}>()

const { t } = useI18n()

const expandedIds = ref<Set<string>>(new Set())
const activeSignalFilter = ref<GovernanceSignalCode | null>(null)
const errorsOnly = ref(false)
const internalSelectedId = ref<string | null>(props.selectedNodeId ?? null)

watch(
  () => props.selectedNodeId,
  (next) => {
    internalSelectedId.value = next ?? null
  },
)

watch(
  () => props.trajectory.root.id,
  () => {
    const seeded = new Set<string>([props.trajectory.root.id])
    for (const turn of props.trajectory.root.children) {
      seeded.add(turn.id)
    }
    expandedIds.value = seeded
    if (internalSelectedId.value && !findTrajectoryNode(props.trajectory.root, internalSelectedId.value)) {
      internalSelectedId.value = null
    }
  },
  { immediate: true },
)

const selectedNode = computed<TrajectoryNode | null>(() =>
  findTrajectoryNode(props.trajectory.root, internalSelectedId.value),
)

interface VisibleRow {
  node: TrajectoryNode
  depth: number
  hasChildren: boolean
  expanded: boolean
}

function matchesFilter(node: TrajectoryNode): boolean {
  if (errorsOnly.value && !node.isError) return false
  if (activeSignalFilter.value && !node.signals.some((s) => s.code === activeSignalFilter.value)) {
    return false
  }
  return true
}

function subtreeMatches(node: TrajectoryNode): boolean {
  if (matchesFilter(node)) return true
  return node.children.some(subtreeMatches)
}

const visibleRows = computed<VisibleRow[]>(() => {
  const rows: VisibleRow[] = []
  const isFiltered = errorsOnly.value || activeSignalFilter.value !== null

  const visit = (node: TrajectoryNode, depth: number) => {
    if (isFiltered && !subtreeMatches(node)) return
    const filteredChildren = isFiltered ? node.children.filter(subtreeMatches) : node.children
    const hasChildren = filteredChildren.length > 0
    const expanded = isFiltered ? true : expandedIds.value.has(node.id)
    rows.push({ node, depth, hasChildren, expanded })
    if (hasChildren && expanded) {
      for (const child of filteredChildren) {
        visit(child, depth + 1)
      }
    }
  }

  visit(props.trajectory.root, 0)
  return rows
})

const maxDurationMs = computed<number>(() => {
  let max = 0
  for (const row of visibleRows.value) {
    if (row.node.kind !== 'run' && (row.node.durationMs ?? 0) > max) {
      max = row.node.durationMs ?? 0
    }
  }
  return Math.max(100, max)
})

function durationBarPct(node: TrajectoryNode): number {
  if (node.kind === 'run' || !node.durationMs || node.durationMs <= 0) return 0
  return Math.min(100, Math.max(6, Math.round((node.durationMs / maxDurationMs.value) * 100)))
}

function toggleExpand(nodeId: string): void {
  const next = new Set(expandedIds.value)
  if (next.has(nodeId)) next.delete(nodeId)
  else next.add(nodeId)
  expandedIds.value = next
}

function selectRow(node: TrajectoryNode): void {
  internalSelectedId.value = internalSelectedId.value === node.id ? null : node.id
  emit('select-node', internalSelectedId.value ? node : null)
}

function toggleSignalFilter(code: GovernanceSignalCode): void {
  activeSignalFilter.value = activeSignalFilter.value === code ? null : code
}

function formatDuration(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms)) return ''
  if (ms < 1000) return `${Math.round(ms)}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

function kindIcon(kind: TrajectoryNode['kind']) {
  if (kind === 'run') return Layers3
  if (kind === 'turn') return Bot
  if (kind === 'model_call') return Cpu
  if (kind === 'tool_call') return Wrench
  return CheckCircle2
}
</script>

<template>
  <section class="traj-panel" :class="{ 'is-compact': compact }" data-testid="trajectory-tree-panel">
    <header class="traj-head">
      <div class="traj-title">
        <Layers3 :size="14" aria-hidden="true" />
        <strong>{{ t('trajectoryEval.tree.title') }}</strong>
      </div>
      <div class="traj-kpis">
        <span class="traj-kpi-pill mono" data-testid="traj-kpi-calls">
          {{ t('trajectoryEval.tree.toolCallsCount', { count: trajectory.totalToolCalls }) }}
        </span>
        <span v-if="trajectory.totalModelCalls > 0" class="traj-kpi-pill mono">
          {{ t('trajectoryEval.tree.modelCallsCount', { count: trajectory.totalModelCalls }) }}
        </span>
        <span
          v-if="trajectory.totalAddedLines > 0 || trajectory.totalRemovedLines > 0"
          class="traj-kpi-pill mono"
          data-testid="traj-kpi-edits"
        >
          <span class="edit-add">+{{ trajectory.totalAddedLines }}</span>
          <span class="edit-sep">/</span>
          <span class="edit-del">-{{ trajectory.totalRemovedLines }}</span>
        </span>
        <button
          type="button"
          class="traj-filter-btn mono"
          :class="{ active: errorsOnly, 'is-danger': trajectory.errorCount > 0 }"
          :aria-pressed="errorsOnly"
          data-testid="traj-errors-filter"
          @click="errorsOnly = !errorsOnly"
        >
          <AlertTriangle :size="11" aria-hidden="true" />
          <span>{{ t('trajectoryEval.tree.errorsCount', { count: trajectory.errorCount }) }}</span>
        </button>
      </div>
    </header>

    <div
      v-if="trajectory.activeSignals.length > 0"
      class="traj-signal-bar"
      role="group"
      :aria-label="t('trajectoryEval.tree.governanceSignals')"
      data-testid="traj-signal-filters"
    >
      <ShieldAlert :size="12" class="traj-signal-icon" aria-hidden="true" />
      <button
        v-for="code in trajectory.activeSignals"
        :key="code"
        type="button"
        class="traj-signal-chip"
        :class="{ active: activeSignalFilter === code }"
        :aria-pressed="activeSignalFilter === code"
        :data-testid="`traj-signal-filter-${code}`"
        @click="toggleSignalFilter(code)"
      >
        <span>{{ code }}</span>
        <span class="traj-signal-count">{{ trajectory.signalCounts[code] }}</span>
      </button>
    </div>

    <div v-if="visibleRows.length === 0" class="traj-empty" data-testid="traj-empty">
      {{ t('trajectoryEval.tree.emptyFiltered') }}
    </div>

    <ul v-else class="traj-list" role="tree" data-testid="traj-node-list">
      <li
        v-for="row in visibleRows"
        :key="row.node.id"
        role="treeitem"
        :aria-expanded="row.hasChildren ? row.expanded : undefined"
        :aria-selected="internalSelectedId === row.node.id"
        class="traj-row"
        :class="{
          'is-selected': internalSelectedId === row.node.id,
          'is-error': row.node.isError,
          [`kind-${row.node.kind}`]: true,
        }"
        :style="{ paddingLeft: `${10 + row.depth * 14}px` }"
        :data-testid="`traj-row-${row.node.id}`"
        @click="selectRow(row.node)"
      >
        <button
          v-if="row.hasChildren"
          type="button"
          class="traj-chevron"
          :aria-label="row.expanded ? t('trajectoryEval.tree.collapse') : t('trajectoryEval.tree.expand')"
          @click.stop="toggleExpand(row.node.id)"
        >
          <ChevronDown v-if="row.expanded" :size="12" />
          <ChevronRight v-else :size="12" />
        </button>
        <span v-else class="traj-chevron-spacer" aria-hidden="true" />

        <component :is="kindIcon(row.node.kind)" :size="13" class="traj-kind-icon" aria-hidden="true" />

        <div class="traj-row-main">
          <div class="traj-row-top">
            <span class="traj-row-label" :title="row.node.label">{{ row.node.label }}</span>
            <span class="traj-status-pill" :class="{ 'is-error': row.node.isError }">
              {{ row.node.status }}
            </span>
            <span
              v-if="row.node.editStats"
              class="traj-edit-pill mono"
              :data-testid="`traj-edit-${row.node.id}`"
            >
              <span class="edit-add">+{{ row.node.editStats.added }}</span>/<span class="edit-del">-{{ row.node.editStats.removed }}</span>
            </span>

            <div class="traj-row-right mono">
              <span v-if="row.node.tokensUsed" class="traj-meta">
                {{ t('trajectoryEval.tree.tokensShort', { tokens: row.node.tokensUsed.toLocaleString() }) }}
              </span>
              <span v-if="row.node.durationMs !== null" class="traj-meta">
                {{ formatDuration(row.node.durationMs) }}
              </span>
              <span
                v-if="durationBarPct(row.node) > 0"
                class="traj-waterfall"
                aria-hidden="true"
              >
                <i
                  class="traj-waterfall__fill"
                  :class="{ 'is-model': row.node.kind === 'model_call', 'is-error': row.node.isError }"
                  :style="{ width: `${durationBarPct(row.node)}%` }"
                />
              </span>
            </div>
          </div>

          <div v-if="row.node.subtitle" class="traj-row-sub mono" :title="row.node.subtitle">
            {{ row.node.subtitle }}
          </div>

          <div v-if="row.node.signals.length > 0" class="traj-row-signals">
            <span
              v-for="sig in row.node.signals"
              :key="sig.code"
              class="traj-gov-badge"
              :class="`sev-${sig.severity}`"
              :title="sig.detail || sig.code"
              :data-testid="`traj-badge-${sig.code}`"
            >
              {{ sig.code }}<template v-if="sig.count && sig.count > 1"> &times;{{ sig.count }}</template>
            </span>
          </div>
        </div>
      </li>
    </ul>

    <div v-if="selectedNode" class="traj-detail" data-testid="traj-node-detail">
      <div class="traj-detail-head">
        <div class="traj-detail-title">
          <span class="traj-kind-tag">{{ t(`trajectoryEval.tree.kind.${selectedNode.kind}`) }}</span>
          <strong>{{ selectedNode.label }}</strong>
        </div>
        <button
          type="button"
          class="traj-diagnose-btn"
          data-testid="traj-diagnose-btn"
          @click="emit('diagnose-node', selectedNode)"
        >
          <Sparkles :size="12" aria-hidden="true" />
          <span>{{ t('trajectoryEval.actions.diagnoseInComposer') }}</span>
        </button>
      </div>

      <div class="traj-detail-meta mono">
        <span v-if="selectedNode.toolId">{{ t('trajectoryEval.tree.toolLabel') }}: {{ selectedNode.toolId }}</span>
        <span v-if="selectedNode.resourcePath">{{ t('trajectoryEval.tree.pathLabel') }}: {{ selectedNode.resourcePath }}</span>
        <span v-if="selectedNode.command">$ {{ selectedNode.command }}</span>
        <span v-if="selectedNode.editStats">
          {{ t('trajectoryEval.tree.editsLabel') }}: +{{ selectedNode.editStats.added }} / -{{ selectedNode.editStats.removed }}
        </span>
      </div>

      <p v-if="selectedNode.summary" class="traj-detail-summary">
        {{ selectedNode.summary }}
      </p>

      <div
        v-for="frame in selectedNode.codeFrames"
        :key="frame.id"
        class="traj-code-frame"
        data-testid="traj-code-frame"
      >
        <div class="traj-code-frame__head mono">
          <span>{{ frame.title }}</span>
          <span v-if="frame.errorCategory" class="traj-frame-cat">{{ frame.errorCategory }}</span>
        </div>
        <pre class="traj-code-frame__body mono"><div
          v-for="(fl, idx) in frame.lines"
          :key="idx"
          class="traj-code-frame__line"
          :class="{ 'is-hi': fl.highlight }"
        ><span class="traj-code-frame__ln">{{ fl.lineNumber !== null && fl.lineNumber !== undefined ? fl.lineNumber : '·' }}</span><span class="traj-code-frame__txt">{{ fl.text }}</span></div></pre>
      </div>
    </div>
  </section>
</template>

<style scoped>
.traj-panel {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 14px;
  background: var(--surface-canvas, var(--bg-primary));
  border-bottom: 1px solid var(--border-muted);
  min-width: 0;
}

.traj-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.traj-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
  color: var(--text-primary);
}

.traj-kpis {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.traj-kpi-pill,
.traj-filter-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 21px;
  padding: 0 7px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 10.5px;
  white-space: nowrap;
}

.traj-filter-btn {
  cursor: pointer;
}

.traj-filter-btn.is-danger {
  color: var(--text-error);
  background: var(--bg-status-danger);
  border-color: color-mix(in srgb, var(--accent-danger) 35%, var(--border-muted));
}

.traj-filter-btn.active {
  border-color: var(--text-primary);
  background: var(--surface-selected, var(--bg-selected));
  color: var(--text-primary);
}

.traj-signal-bar {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
  padding: 5px 0;
  border-top: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
}

.traj-signal-icon {
  color: var(--text-secondary);
  flex-shrink: 0;
}

.traj-signal-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 6px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--bg-status-warn);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 10px;
  cursor: pointer;
}

.traj-signal-chip.active {
  border-color: var(--text-primary);
  background: var(--surface-selected, var(--bg-selected));
}

.traj-signal-count {
  opacity: 0.78;
}

.traj-empty {
  padding: 14px;
  text-align: center;
  color: var(--text-muted);
  font-size: 12px;
}

.traj-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  max-height: 340px;
  overflow-y: auto;
}

.traj-row {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  padding: 6px 8px;
  border-bottom: 1px solid var(--border-muted);
  background: transparent;
  cursor: pointer;
  transition: background-color 100ms ease;
}

.traj-row:last-child {
  border-bottom: 0;
}

.traj-row:hover {
  background: var(--surface-hover, var(--bg-hover));
}

.traj-row.is-selected {
  background: var(--surface-selected, var(--bg-selected));
}

.traj-row.is-error:not(.is-selected) {
  background: color-mix(in srgb, var(--bg-status-danger) 45%, transparent);
}

.traj-chevron {
  width: 16px;
  height: 16px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  padding: 0;
  margin-top: 1px;
  flex-shrink: 0;
}

.traj-chevron-spacer {
  width: 16px;
  flex-shrink: 0;
}

.traj-kind-icon {
  color: var(--text-secondary);
  margin-top: 2px;
  flex-shrink: 0;
}

.traj-row-main {
  min-width: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.traj-row-top {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.traj-row-label {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 220px;
}

.traj-status-pill {
  padding: 0 5px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 10px;
}

.traj-status-pill.is-error {
  background: var(--bg-status-danger);
  color: var(--text-error);
  border-color: color-mix(in srgb, var(--accent-danger) 35%, var(--border-muted));
}

.traj-row-right {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
}

.traj-meta {
  font-size: 10.5px;
  color: var(--text-muted);
}

.traj-waterfall {
  width: 38px;
  height: 4px;
  border-radius: 2px;
  background: var(--surface-raised, var(--bg-tertiary));
  overflow: hidden;
  display: inline-flex;
}

.traj-waterfall__fill {
  display: block;
  height: 100%;
  background: #d49b00;
  border-radius: 2px;
}

.traj-waterfall__fill.is-model {
  background: var(--obs-ctx-assistant, #5b9fe8);
}

.traj-waterfall__fill.is-error {
  background: var(--accent-danger, #c72b31);
}

.traj-edit-pill {
  font-size: 10.5px;
}

.edit-add {
  color: var(--accent-success);
}

.edit-del {
  color: var(--text-error);
}

.edit-sep {
  color: var(--text-muted);
  margin: 0 1px;
}

.traj-row-sub {
  font-size: 10.5px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.traj-row-signals {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.traj-gov-badge {
  display: inline-flex;
  align-items: center;
  padding: 1px 5px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  font-family: var(--font-mono);
  font-size: 10px;
}

.traj-gov-badge.sev-danger {
  background: var(--bg-status-danger);
  color: var(--text-error);
}

.traj-gov-badge.sev-warn {
  background: var(--bg-status-warn);
  color: var(--text-primary);
}

.traj-gov-badge.sev-neutral {
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
}

.traj-detail {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-top: 10px;
  border-top: 1px solid var(--border-muted);
}

.traj-detail-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.traj-detail-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  font-size: 12px;
}

.traj-kind-tag {
  padding: 1px 5px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 10px;
  text-transform: uppercase;
}

.traj-diagnose-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 25px;
  padding: 0 9px;
  border-radius: 6px;
  border: 1px solid var(--border-default);
  background: var(--surface-button, var(--bg-primary));
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}

.traj-diagnose-btn:hover {
  border-color: var(--text-primary);
  background: var(--surface-button-hover, var(--bg-secondary));
}

.traj-detail-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  font-size: 11px;
  color: var(--text-secondary);
  word-break: break-all;
}

.traj-detail-summary {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
  line-height: 1.45;
}

.traj-code-frame {
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  overflow: hidden;
  background: var(--surface-section, var(--bg-secondary));
}

.traj-code-frame__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 8px;
  border-bottom: 1px solid var(--border-muted);
  font-size: 10.5px;
  color: var(--text-secondary);
}

.traj-frame-cat {
  color: var(--text-error);
}

.traj-code-frame__body {
  margin: 0;
  padding: 5px 0;
  font-size: 11px;
  overflow-x: auto;
}

.traj-code-frame__line {
  display: grid;
  grid-template-columns: 36px 1fr;
  gap: 8px;
  padding: 1px 8px;
}

.traj-code-frame__line.is-hi {
  background: var(--bg-status-danger);
  color: var(--text-error);
}

.traj-code-frame__ln {
  text-align: right;
  color: var(--text-muted);
  user-select: none;
}

.traj-code-frame__txt {
  white-space: pre-wrap;
  word-break: break-word;
}

.mono {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
}
</style>
