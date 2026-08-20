<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { useWorkbenchStore } from '@/stores/workbench'
import { useRunStore } from '@/stores/run'
import { homeController } from '@/controllers/HomeController'
import { UiCard, UiButton, UiBadge } from '@/components/ui'
import TaskGraphPanel from '@/components/TaskGraphPanel.vue'

const wb = useWorkbenchStore()
const runStore = useRunStore()

const sessionId = computed(() => homeController.selectedSessionId.value)
// Pinia unwraps refs: access as plain values
const runId = computed(() => (runStore.selectedRunId as string | null) ?? (wb.snapshot?.run?.id as string | null) ?? null)
const status = computed(() => wb.status || runStore.status || 'idle')
const isTerminal = computed(() => ['completed','failed','cancelled'].includes(status.value))

async function refresh() {
  if (!sessionId.value) return
  await wb.fetchAll(sessionId.value, runId.value)
  if (sessionId.value) await runStore.fetchRuns(sessionId.value)
}

onMounted(refresh)
watch(sessionId, refresh)
watch(runId, () => { if (sessionId.value) wb.fetchAll(sessionId.value, runId.value) })

async function control(action: 'cancel'|'pause'|'resume') {
  if (!runId.value) return
  await wb.control(runId.value, action)
  await refresh()
}
</script>

<template>
  <div class="workbench-page">
    <div class="workbench-head">
      <h2 class="workbench-title">Workbench</h2>
      <div class="workbench-status">
        <UiBadge :variant="isTerminal ? 'secondary' : 'default'">{{ status }}</UiBadge>
        <span v-if="wb.cursor != null" class="workbench-cursor">cursor {{ wb.cursor }}</span>
        <span v-if="runId" class="workbench-run">run {{ runId.slice(0, 8) }}</span>
      </div>
      <div class="workbench-controls">
        <UiButton size="sm" variant="outline" :disabled="!runId || isTerminal" @click="control('pause')">Pause</UiButton>
        <UiButton size="sm" variant="outline" :disabled="!runId || status !== 'paused'" @click="control('resume')">Resume</UiButton>
        <UiButton size="sm" variant="destructive" :disabled="!runId || isTerminal" @click="control('cancel')">Cancel</UiButton>
      </div>
    </div>

    <div v-if="!sessionId" class="workbench-empty">Select a session to view workbench.</div>
    <template v-else>
      <UiCard class="workbench-card">
        <!-- ponytail: bridge generated snapshot to legacy panel type via unknown cast; single canonical DTO is generated/client.ts -->
        <TaskGraphPanel :snapshot="(wb.snapshot as unknown as never)" />
        <div v-if="wb.nodes.length" class="workbench-nodes-note">
          {{ wb.nodes.length }} nodes · {{ wb.findings.length }} findings · {{ wb.contextVersions.length }} context versions
        </div>
      </UiCard>

      <div class="workbench-sections">
        <UiCard class="workbench-section">
          <h3>Supervision findings</h3>
          <div v-if="wb.findings.length === 0" class="quiet">No findings.</div>
          <div v-for="f in wb.findings" :key="f.id" class="finding-row">
            <UiBadge variant="outline">{{ f.severity }} · {{ f.category }}</UiBadge>
            <p>{{ f.summary }}</p>
            <small>{{ f.recommendation }}</small>
          </div>
        </UiCard>

        <UiCard class="workbench-section">
          <h3>Context versions</h3>
          <div v-if="wb.contextVersions.length === 0" class="quiet">No versions.</div>
          <div v-for="v in wb.contextVersions" :key="v.id" class="version-row">
            <span class="mono">r{{ v.revision }}</span>
            <span>{{ v.kind }} · {{ v.status }}</span>
            <span v-if="v.base_revision != null" class="mono">base {{ v.base_revision }}</span>
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
.mono { font-family: ui-monospace, monospace; }
.workbench-error { color: #c00; font-size: 12px; }
</style>
