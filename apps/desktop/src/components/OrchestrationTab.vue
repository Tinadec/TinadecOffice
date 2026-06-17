<script setup lang="ts">
import { computed, ref } from 'vue'
import { AlertTriangle, Archive, BarChart3, CheckCircle2, Layers3, ListTree, Package, Wrench } from '@lucide/vue'
import type { OrchestrationSnapshotDto, ToolExecutionTimelineItemDto, ToolDescriptorDto } from '../api'
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
        <div v-for="pack in snapshot.context_packs" :key="pack.id" class="context-pack-row">
          <p>{{ pack.summary }}</p>
          <div class="orchestration-tags">
            <span>{{ pack.token_budget }} tokens</span>
            <span>{{ Math.round(pack.compression_ratio * 100) }}%</span>
          </div>
        </div>
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
  background: var(--bg-tertiary);
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  padding: 3px 7px;
}

.orchestration-finding {
  background: var(--bg-secondary);
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
  background: var(--bg-tertiary);
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
  background: var(--bg-secondary);
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
