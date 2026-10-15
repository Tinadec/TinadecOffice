<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useWorkbenchStore } from '@/stores/workbench'
import { useRunStore } from '@/stores/run'
import { homeController } from '@/controllers/HomeController'
import { api, type AgentLineageEntryDto } from '@/api'
import { UiCard, UiButton, UiBadge } from '@/components/ui'
import TaskGraphPanel from '@/components/TaskGraphPanel.vue'
import RunLaneCanvas from '@/components/canvas/RunLaneCanvas.vue'
import RunStatusBadge from '@/components/governance/RunStatusBadge.vue'
import CommandPaletteButton from '@/components/CommandPaletteButton.vue'
import { useStatusLabels } from '@/lib/statusLabels'

const { t } = useI18n()
const { runStatusLabel, severityLabel } = useStatusLabels()
const wb = useWorkbenchStore()
const runStore = useRunStore()

const sessionId = computed(() => homeController.selectedSessionId.value)
// Pinia unwraps refs: access as plain values
const runId = computed(() => (runStore.selectedRunId as string | null) ?? (wb.snapshot?.run?.id as string | null) ?? null)
const status = computed(() => wb.status || runStore.status || 'idle')
const isTerminal = computed(() => ['completed','failed','cancelled'].includes(status.value))

/** Frozen run configuration strip (§4.2): display-only, never editable here. */
const frozenConfig = computed(() => {
  const snap = wb.snapshot as unknown as { run?: Record<string, unknown>; frozen?: Record<string, unknown> } | null
  const runMeta = (snap?.run ?? {}) as Record<string, unknown>
  return {
    mode_id: String(runMeta['mode_id'] ?? '—'),
    config_version: String(runMeta['config_version'] ?? '—'),
    config_hash: String(runMeta['config_hash'] ?? '').slice(0, 12),
    context_revision: String(runMeta['context_revision'] ?? '—'),
    tool_manifest_hash: String(runMeta['tool_manifest_hash'] ?? '').slice(0, 12),
  }
})

const lineage = ref<Array<AgentLineageEntryDto & { display_name?: string }>>([])
const selectedInstanceId = ref<string | null>(null)
const bottomTab = ref<string>('tasks')

const selectedInstance = computed(
  () => lineage.value.find((x) => x.id === selectedInstanceId.value) ?? null,
)

async function loadLineage(): Promise<void> {
  // Read-id guard: a reply for a run the user has already moved away from must not land,
  // otherwise the canvas shows the previous run's agents next to the current run's header.
  const target = runId.value
  lineage.value = []
  selectedInstanceId.value = null
  if (!target) return
  try {
    const rows = await api.getRunAgentLineage(target)
    if (runId.value !== target) return
    // git_steward vs worker.git are distinct roles; label with role when no
    // friendlier name is projected.
    lineage.value = rows.map((row) => ({ ...row, display_name: row.role }))
  } catch {
    if (runId.value !== target) return
    // Lineage is a projection; absence degrades the canvas to empty state.
    lineage.value = []
  }
}

async function refresh() {
  if (!sessionId.value) return
  await wb.fetchAll(sessionId.value, runId.value)
  if (sessionId.value) await runStore.fetchRuns(sessionId.value)
  await loadLineage()
}

onMounted(refresh)
watch(sessionId, refresh)
watch(runId, () => { if (sessionId.value) void refresh() })

async function control(action: 'cancel'|'pause'|'resume') {
  if (!runId.value) return
  await wb.control(runId.value, action)
  await refresh()
}
</script>

<template>
  <div class="workbench-page">
    <div class="workbench-head">
      <h2 class="workbench-title">{{ t('workbench.title') }}</h2>
      <div class="workbench-status">
        <RunStatusBadge :status="status" />
        <span v-if="wb.cursor != null" class="workbench-cursor">{{ t('workbench.cursor', { cursor: wb.cursor }) }}</span>
        <span v-if="runId" class="workbench-run">{{ t('workbench.run', { id: runId.slice(0, 8) }) }}</span>
      </div>
      <div class="workbench-controls">
        <CommandPaletteButton />
        <UiButton size="sm" variant="outline" :disabled="!runId || isTerminal" @click="control('pause')">{{ t('workbench.pause') }}</UiButton>
        <UiButton size="sm" variant="outline" :disabled="!runId || status !== 'paused'" @click="control('resume')">{{ t('workbench.resume') }}</UiButton>
        <UiButton size="sm" variant="destructive" :disabled="!runId || isTerminal" @click="control('cancel')">{{ t('common.cancel') }}</UiButton>
      </div>
    </div>

    <div v-if="!sessionId" class="workbench-empty">{{ t('workbench.noSession') }}</div>
    <template v-else>
      <!-- Frozen run configuration: display-only projection (§4.2) -->
      <div class="workbench-frozen" data-testid="frozen-config">
        <span>{{ t('workbench.frozen.mode', { value: frozenConfig.mode_id }) }}</span>
        <span>v{{ frozenConfig.config_version }}</span>
        <span class="mono">{{ t('workbench.frozen.config', { value: frozenConfig.config_hash || '—' }) }}</span>
        <span>{{ t('workbench.frozen.revision', { value: frozenConfig.context_revision }) }}</span>
        <span class="mono">{{ t('workbench.frozen.manifest', { value: frozenConfig.tool_manifest_hash || '—' }) }}</span>
      </div>

      <!-- Dual-lane canvas: operation / execution with spawn lineage -->
      <RunLaneCanvas
        :lineage="lineage"
        :has-orchestration-data="wb.nodes.length > 0"
        @select="(id: string) => { selectedInstanceId = id }"
      />

      <!-- Instance detail drawer (agent instance facts only; not editable) -->
      <UiCard v-if="selectedInstance" class="workbench-section" data-testid="instance-drawer">
        <div class="drawer-head">
          <h3>{{ selectedInstance.display_name }} <span class="mono">{{ selectedInstance.id.slice(0, 8) }}</span></h3>
          <UiButton size="sm" variant="outline" @click="selectedInstanceId = null">{{ t('common.close') }}</UiButton>
        </div>
        <dl class="instance-facts">
          <div><dt>{{ t('workbench.facts.layer') }}</dt><dd>{{ selectedInstance.layer }}</dd></div>
          <div><dt>{{ t('workbench.facts.role') }}</dt><dd>{{ selectedInstance.role }}</dd></div>
          <div><dt>{{ t('workbench.facts.status') }}</dt><dd>{{ runStatusLabel(selectedInstance.status) }}</dd></div>
          <div><dt>{{ t('workbench.facts.depth') }}</dt><dd>{{ selectedInstance.generation_depth }}</dd></div>
          <div v-if="selectedInstance.parent_instance_id"><dt>{{ t('workbench.facts.parent') }}</dt><dd class="mono">{{ selectedInstance.parent_instance_id.slice(0, 8) }}</dd></div>
          <div v-if="selectedInstance.task_id"><dt>{{ t('workbench.facts.task') }}</dt><dd class="mono">{{ selectedInstance.task_id.slice(0, 8) }}</dd></div>
          <div v-if="selectedInstance.capabilities?.length"><dt>{{ t('workbench.facts.capabilities') }}</dt><dd>{{ selectedInstance.capabilities.join(', ') }}</dd></div>
        </dl>
      </UiCard>

      <UiCard class="workbench-card">
        <!-- ponytail: bridge generated snapshot to legacy panel type via unknown cast; single canonical DTO is generated/client.ts -->
        <TaskGraphPanel :snapshot="(wb.snapshot as unknown as never)" />
        <div v-if="wb.nodes.length" class="workbench-nodes-note">
          {{ t('workbench.counts', { nodes: wb.nodes.length, findings: wb.findings.length, versions: wb.contextVersions.length }) }}
        </div>
      </UiCard>

      <div class="workbench-sections">
        <UiCard class="workbench-section">
          <h3>{{ t('workbench.findings') }}</h3>
          <div v-if="wb.findings.length === 0" class="quiet">{{ t('workbench.noFindings') }}</div>
          <div v-for="f in wb.findings" :key="f.id" class="finding-row">
            <UiBadge variant="outline">{{ severityLabel(f.severity) }} · {{ f.category }}</UiBadge>
            <p>{{ f.summary }}</p>
            <small>{{ f.recommendation }}</small>
          </div>
        </UiCard>

        <UiCard class="workbench-section">
          <h3>{{ t('workbench.contextVersions') }}</h3>
          <div v-if="wb.contextVersions.length === 0" class="quiet">{{ t('workbench.noVersions') }}</div>
          <div v-for="v in wb.contextVersions" :key="v.id" class="version-row">
            <span class="mono">r{{ v.revision }}</span>
            <span>{{ v.kind }} · {{ v.status }}</span>
            <span v-if="v.base_revision != null" class="mono">{{ t('workbench.base', { revision: v.base_revision }) }}</span>
          </div>
        </UiCard>
      </div>

      <div v-if="wb.error" class="workbench-error">{{ wb.error }}</div>
    </template>
  </div>
</template>

<style scoped>
.workbench-page { display: grid; gap: 16px; padding: 16px; }
.workbench-head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.workbench-title { font-size: 18px; font-weight: 700; }
.workbench-status { display: flex; gap: 8px; align-items: center; color: var(--text-muted); font-size: 12px; }
.workbench-controls { margin-left: auto; display: flex; gap: 8px; }
.workbench-empty, .quiet { color: var(--text-muted); font-size: 13px; }
.workbench-card { padding: 12px; }
.workbench-nodes-note { color: var(--text-muted); font-size: 11px; margin-top: 8px; }
.workbench-sections { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
@media (max-width: 900px) { .workbench-sections { grid-template-columns: 1fr; } }
.workbench-section { padding: 12px; display: grid; gap: 8px; }
.workbench-section h3 { font-size: 13px; font-weight: 700; }
.finding-row, .version-row { border: 1px solid var(--border-muted); border-radius: 8px; padding: 8px; display: grid; gap: 4px; background: var(--surface-section); }
.finding-row p { font-size: 12px; color: var(--text-secondary); margin: 0; }
.finding-row small { font-size: 11px; color: var(--text-muted); }
.version-row { font-size: 12px; display: flex; gap: 8px; align-items: center; }
.mono { font-family: var(--font-mono); }
.workbench-error { color: #c00; font-size: 12px; }

/* Frozen configuration strip */
.workbench-frozen {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  padding: 8px 12px;
  border-radius: 8px;
  background: var(--surface-section);
  font-size: 12px;
  color: var(--text-secondary);
}

/* Instance drawer */
.drawer-head { display: flex; align-items: center; justify-content: space-between; }
.instance-facts { display: grid; grid-template-columns: max-content 1fr; gap: 6px 14px; margin: 0; font-size: 12px; }
.instance-facts dt { color: var(--text-secondary); font-family: var(--font-mono); font-size: 11px; padding-top: 2px; }
.instance-facts dd { margin: 0; color: var(--text-primary); overflow-wrap: anywhere; }
</style>
