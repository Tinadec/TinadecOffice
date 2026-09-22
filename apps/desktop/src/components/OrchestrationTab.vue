<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { AlertTriangle, Archive, BarChart3, CheckCircle2, GitBranch, Layers3, ListTree, Package, Wrench } from '@lucide/vue'
import { api, type ContextBudgetShareDto, type ContextPackDto, type ModelInvocationDto, type OrchestrationSnapshotDto, type ToolExecutionTimelineItemDto, type ToolDescriptorDto } from '../api'
import { summarizeModelInvocations, type ModelUsageGroup, type ModelUsageSummary } from '../lib/modelUsage'
import DeclaredGraphCanvas from './canvas/DeclaredGraphCanvas.vue'
import ToolExecutionTimeline from './tools/ToolExecutionTimeline.vue'
import ToolCatalogBrowser from './tools/ToolCatalogBrowser.vue'
import ToolStatsDashboard from './tools/ToolStatsDashboard.vue'

const props = defineProps<{
  snapshot: OrchestrationSnapshotDto | null
  toolExecutions: ToolExecutionTimelineItemDto[]
  tools?: ToolDescriptorDto[]
}>()

const emit = defineEmits<{
  'rerun-tool': [toolExecution: ToolExecutionTimelineItemDto]
  'view-tool-details': [toolExecution: ToolExecutionTimelineItemDto]
  'execute-tool': [tool: ToolDescriptorDto]
}>()

type TabKey = 'timeline' | 'catalog' | 'stats'

const activeTab = ref<TabKey>('timeline')

const hasSnapshot = computed(() => Boolean(props.snapshot?.run))

/**
 * Core caps a page of the invocation audit at 200 and pages by cursor only, so a run total is a
 * client-side walk. Five pages is already 1000 calls: enough for any run that a human reads, and
 * small enough that opening this panel cannot fan out into dozens of requests. Past the cap the
 * panel says it stopped — a walk cut short printed as a total would be a smaller lie than a wrong
 * one only to someone who cannot tell the two apart.
 */
const USAGE_PAGE_SIZE = 200
const USAGE_PAGE_LIMIT = 5

const usage = ref<ModelUsageSummary | null>(null)
const usageState = ref<'idle' | 'loading' | 'ready' | 'unavailable'>('idle')

async function loadModelUsage(runId?: string | null): Promise<void> {
  usage.value = null
  if (!runId) {
    usageState.value = 'idle'
    return
  }
  usageState.value = 'loading'
  try {
    const rows: ModelInvocationDto[] = []
    let cursor: string | undefined
    for (let page = 0; page < USAGE_PAGE_LIMIT; page++) {
      const result = await api.listModelInvocations({ run_id: runId, limit: USAGE_PAGE_SIZE, cursor })
      rows.push(...result.items)
      cursor = result.next_cursor ?? undefined
      if (!cursor) break
    }
    usage.value = summarizeModelInvocations(rows, { truncated: Boolean(cursor) })
    usageState.value = 'ready'
  } catch {
    usageState.value = 'unavailable'
  }
}

watch(() => props.snapshot?.run?.id, (runId) => { void loadModelUsage(runId) }, { immediate: true })

/** The provider is named only when two rows would otherwise read as one model. */
const ambiguousModels = computed(() => {
  const seen = new Set<string>()
  const shared = new Set<string>()
  for (const group of usage.value?.groups ?? []) {
    const key = group.model ?? ''
    if (seen.has(key)) shared.add(key)
    else seen.add(key)
  }
  return shared
})

function usageGroupLabel(group: ModelUsageGroup): string {
  const name = group.model ?? 'unnamed model'
  const via = ambiguousModels.value.has(group.model ?? '') ? ` via ${group.providerId.slice(0, 8)}` : ''
  const calls = `${group.calls} ${group.calls === 1 ? 'call' : 'calls'}`
  if (group.totalTokens === null) return `${name}${via} · no token usage reported · ${calls}`
  const partial = group.unpricedCalls > 0 ? ` (${group.unpricedCalls} unreported)` : ''
  return `${name}${via} · ${group.totalTokens.toLocaleString('en-US')} tokens${partial} · ${calls}`
}

/**
 * A context pack has no summary on the wire — the sentence the engine hands `AppendEventAsync` never
 * reaches the event envelope — so the row is labelled from the one field that distinguishes packs:
 * which lane it was assembled for, or none for the main planner.
 */
function packLabel(pack: ContextPackDto): string {
  return pack.lane_key ? `Lane '${pack.lane_key}' pack` : 'Planner pack'
}

/**
 * One source can contribute several items (`reviewed_memory` adds one per promoted
 * entry), so the cost is summed per name. Listing every item separately would show
 * the same source at several prices and hide the number that actually matters —
 * what that source cost the pack in total.
 */
function sumBySource(shares: ContextBudgetShareDto[] | undefined): Map<string, number> {
  const totals = new Map<string, number>()
  for (const share of shares ?? []) {
    totals.set(share.source, (totals.get(share.source) ?? 0) + share.tokens)
  }
  return totals
}

/**
 * The wire lists one entry per evidence item, so a source that contributed several
 * entries appears several times. It is rendered once, with its summed price: the
 * question this row answers is which sources the model was told, not how many items
 * each one happened to add — the "N evidence" chip already says that.
 */
function sourceNames(pack: ContextPackDto): string[] {
  return [...new Set(pack.sources)]
}

/**
 * Absent on events written before the pack reported costs, so a missing price
 * renders as a bare name rather than as "0 tokens" — the latter would read as a
 * measured free item.
 */
function sourceLabel(pack: ContextPackDto, source: string): string {
  const tokens = sumBySource(pack.source_tokens).get(source)
  return tokens === undefined ? source : `${source} · ${tokens}`
}

function droppedShares(pack: ContextPackDto): Array<{ source: string; tokens: number }> {
  return [...sumBySource(pack.dropped_sources)].map(([source, tokens]) => ({ source, tokens }))
}

const tabs: Array<{ key: TabKey; label: string; icon: typeof ListTree }> = [
  { key: 'timeline', label: 'Timeline', icon: ListTree },
  { key: 'catalog', label: 'Catalog', icon: Package },
  { key: 'stats', label: 'Stats', icon: BarChart3 }
]

function onRerun(exec: ToolExecutionTimelineItemDto) {
  emit('rerun-tool', exec)
}

function onViewDetails(exec: ToolExecutionTimelineItemDto) {
  emit('view-tool-details', exec)
}

function onExecuteTool(tool: ToolDescriptorDto) {
  emit('execute-tool', tool)
}
</script>

<template>
  <section class="orchestration-tab">
    <div v-if="!hasSnapshot" class="orchestration-empty">
      No orchestration run yet.
    </div>

    <template v-else-if="snapshot">
      <article v-if="snapshot.graph" class="orchestration-block">
        <div class="orchestration-block-head">
          <GitBranch :size="15" />
          <strong>Mode Graph</strong>
        </div>
        <DeclaredGraphCanvas :graph="snapshot.graph" :flows="snapshot.flows" />
      </article>

      <article class="orchestration-block">
        <div class="orchestration-block-head">
          <Layers3 :size="15" />
          <strong>Run</strong>
        </div>
        <p>{{ snapshot.run?.summary }}</p>
        <div class="orchestration-tags">
          <span>{{ snapshot.run?.status }}</span>
          <span>{{ snapshot.nodes.length }} nodes</span>
          <span>{{ snapshot.assignments.length }} assignments</span>
        </div>
      </article>

      <article class="orchestration-block">
        <div class="orchestration-block-head">
          <AlertTriangle :size="15" />
          <strong>Supervision</strong>
        </div>
        <div v-if="snapshot.supervision_findings.length === 0" class="quiet">
          No findings.
        </div>
        <div v-for="finding in snapshot.supervision_findings" :key="finding.id" class="orchestration-finding">
          <span>{{ finding.severity }} · {{ finding.category }}</span>
          <p>{{ finding.summary }}</p>
          <small>{{ finding.recommendation }}</small>
        </div>
      </article>

      <article class="orchestration-block">
        <div class="orchestration-block-head">
          <Wrench :size="15" />
          <strong>Tool Executions</strong>
        </div>

        <div class="orchestration-tabs">
          <button
            v-for="tab in tabs"
            :key="tab.key"
            class="orchestration-tab-btn"
            :class="{ active: activeTab === tab.key }"
            @click="activeTab = tab.key"
          >
            <component :is="tab.icon" :size="13" />
            <span>{{ tab.label }}</span>
          </button>
        </div>

        <div class="orchestration-tab-content">
          <ToolExecutionTimeline
            v-if="activeTab === 'timeline'"
            :tool-executions="toolExecutions"
            @rerun="onRerun"
            @view-details="onViewDetails"
          />
          <ToolCatalogBrowser
            v-else-if="activeTab === 'catalog'"
            :tools="tools"
            @execute="onExecuteTool"
          />
          <ToolStatsDashboard
            v-else-if="activeTab === 'stats'"
            :tool-executions="toolExecutions"
          />
        </div>
      </article>

      <article class="orchestration-block">
        <div class="orchestration-block-head">
          <Archive :size="15" />
          <strong>Context Packs</strong>
        </div>
        <div v-if="snapshot.context_packs.length === 0" class="quiet">
          No context packs.
        </div>
        <div v-for="pack in snapshot.context_packs" :key="pack.id" class="context-pack-row" data-testid="context-pack-row">
          <p>{{ packLabel(pack) }}</p>
          <div class="orchestration-tags">
            <span>{{ pack.evidence_count }} evidence</span>
            <span>{{ pack.estimated_tokens }} / {{ pack.token_budget }} tokens</span>
          </div>
          <div
            v-if="pack.sources.length"
            class="orchestration-tags"
            role="list"
            aria-label="Evidence sources this pack carried, with the tokens each cost"
            data-testid="context-pack-sources"
          >
            <span
              v-for="source in sourceNames(pack)"
              :key="source"
              role="listitem"
              data-testid="context-pack-source"
            >{{ sourceLabel(pack, source) }}</span>
          </div>
          <p v-else class="quiet">
            No evidence list was recorded for this pack — it was written before the pack started
            naming its sources, so its contents are unknown here rather than empty.
          </p>
          <!-- Available but cut: the pack answered "which sources" without saying which
               ones the budget removed, and those are two different answers to "why did the
               agent ignore the project rules". -->
          <p v-if="droppedShares(pack).length" class="quiet" data-testid="context-pack-dropped">
            Cut by the token budget:
            <span v-for="share in droppedShares(pack)" :key="share.source" data-testid="context-pack-dropped-source">{{ share.source }} ({{ share.tokens }} tokens)</span>
            — available when this pack was built, but they did not fit, so the model was not told them.
          </p>
        </div>
      </article>

      <article class="orchestration-block" data-testid="model-usage">
        <div class="orchestration-block-head">
          <BarChart3 :size="15" />
          <strong>Model Usage</strong>
        </div>
        <div v-if="usageState === 'loading'" class="quiet">
          Reading this run's model invocation audit…
        </div>
        <div v-else-if="usageState === 'unavailable'" class="quiet" data-testid="model-usage-unavailable">
          The invocation audit could not be read, so no token figures are shown. An unread audit is
          not a zero-cost run.
        </div>
        <div v-else-if="!usage || usage.calls === 0" class="quiet" data-testid="model-usage-empty">
          No model calls recorded for this run yet.
        </div>
        <template v-else>
          <p v-if="usage.truncated" class="quiet" data-testid="model-usage-truncated">
            Partial: this run has more than {{ USAGE_PAGE_LIMIT * USAGE_PAGE_SIZE }} recorded calls
            and the walk stopped there, so the figures below are a floor rather than a total.
          </p>
          <div
            class="orchestration-tags"
            role="list"
            aria-label="Tokens and calls per model in this run"
          >
            <span
              v-for="group in usage.groups"
              :key="group.key"
              role="listitem"
              data-testid="model-usage-group"
            >{{ usageGroupLabel(group) }}</span>
          </div>
          <p v-if="usage.unpricedCalls" class="quiet" data-testid="model-usage-unpriced">
            {{ usage.unpricedCalls }} of {{ usage.calls }} calls reported no token usage at all —
            a failed or unreported call, not a free one.
          </p>
          <p class="quiet">
            Tokens only: this build has no per-model price table, so no amount is shown.
          </p>
        </template>
      </article>

      <article class="orchestration-block">
        <div class="orchestration-block-head">
          <CheckCircle2 :size="15" />
          <strong>Step Results</strong>
        </div>
        <div v-if="snapshot.step_results.length === 0" class="quiet">
          No step results.
        </div>
        <div v-for="result in snapshot.step_results" :key="result.id" class="step-result-row">
          <span>{{ result.status }}</span>
          <p>{{ result.summary }}</p>
        </div>
      </article>
    </template>
  </section>
</template>

<style scoped>
.orchestration-tab {
  display: grid;
  gap: 10px;
  padding: 12px;
}

.orchestration-block {
  border-bottom: 1px solid var(--border-muted);
  display: grid;
  gap: 8px;
  padding-bottom: 12px;
}

.orchestration-block-head {
  align-items: center;
  color: var(--text-primary);
  display: flex;
  gap: 7px;
}

.orchestration-block p {
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.4;
  margin: 3px 0 0;
}

.orchestration-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  color: var(--text-muted);
  font-size: 12px;
}

.orchestration-tags span {
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  padding: 3px 7px;
}

.orchestration-finding {
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  display: grid;
  gap: 5px;
  padding: 9px;
}

.orchestration-finding span {
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
}

.orchestration-finding small {
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.35;
}

.quiet {
  color: var(--text-muted);
  font-size: 13px;
}

.orchestration-tabs {
  display: flex;
  gap: 2px;
  padding: 2px;
  background: var(--surface-raised);
  border-radius: 6px;
}

.orchestration-tab-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 10px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.orchestration-tab-btn:hover {
  color: var(--text-primary);
}

.orchestration-tab-btn.active {
  color: var(--accent-primary);
  background: var(--bg-primary);
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.1);
}

.orchestration-tab-content {
  min-height: 200px;
}

.context-pack-row,
.step-result-row {
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  display: grid;
  gap: 5px;
  padding: 9px;
}

.context-pack-row p,
.step-result-row p {
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.4;
  margin: 3px 0 0;
}

.step-result-row span {
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
}

.orchestration-empty {
  color: var(--text-muted);
  font-size: 12px;
  padding: 16px;
}
</style>
