<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Info, Search } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSheet } from '@/components/ui'
import { api, type AgentRuntimeInstanceDto } from '@/api'
import { generatedApi } from '@/generated/client'
import { useNotifications } from '@/composables/useNotifications'

const { t } = useI18n()
const { notify } = useNotifications()

const runtimeInstances = ref<AgentRuntimeInstanceDto[]>([])
const runtimeLoaded = ref(false)
const runtimeSessionFilter = ref('')
const runtimeStatusFilter = ref('all')
const runtimePage = ref(1)
const runtimePageSize = ref(10)
const runtimeTimer: ReturnType<typeof setInterval> | null = null
const runtimeControlBusy = ref<string | null>(null)
const showReassignSheet = ref(false)
const reassignForm = ref({ session_id: '', interaction_id: '', target_run_id: '' })

const runtimeStatusOptions = computed(() => ['all', ...Array.from(new Set(runtimeInstances.value.map((r) => String(r.status ?? '')).filter(Boolean)))])
const filteredRuntimeInstances = computed(() => {
  const qs = runtimeSessionFilter.value.trim().toLowerCase()
  const st = runtimeStatusFilter.value
  return runtimeInstances.value.filter((r) => {
    if (st !== 'all' && String(r.status) !== st) return false
    if (qs && !String(r.session_id ?? r.run_id ?? '').toLowerCase().includes(qs)) return false
    return true
  })
})
const runtimeTotalPages = computed(() => Math.max(1, Math.ceil(filteredRuntimeInstances.value.length / runtimePageSize.value)))
const pagedRuntimeInstances = computed(() => {
  const start = (runtimePage.value - 1) * runtimePageSize.value
  return filteredRuntimeInstances.value.slice(start, start + runtimePageSize.value)
})

watch([runtimeSessionFilter, runtimeStatusFilter], () => { runtimePage.value = 1 })
watch(filteredRuntimeInstances, () => { if (runtimePage.value > runtimeTotalPages.value) runtimePage.value = runtimeTotalPages.value })

function readinessVariant(status?: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  const s = String(status ?? '')
  if (['running', 'completed'].includes(s)) return 'default'
  if (['failed', 'cancelled', 'blocked'].includes(s)) return 'destructive'
  if (['queued', 'pending', 'waiting'].includes(s)) return 'outline'
  return 'secondary'
}

async function loadRuntimeInstances() {
  try {
    runtimeInstances.value = await api.listRuntimeInstances()
    runtimeLoaded.value = true
  } catch (e) {
    runtimeLoaded.value = true
    notify.error(e, { title: t('agentCenter.runtimeLoadFailed') })
  }
}

async function controlInstance(runId: string, action: 'pause' | 'resume' | 'cancel') {
  const labels: Record<string, string> = { pause: t('settings.pauseAction'), resume: t('settings.resumeAction'), cancel: t('settings.cancelAction') }
  runtimeControlBusy.value = `${runId}:${action}`
  try {
    await generatedApi.controlRun(runId, { action })
    notify.success(t('settings.runControlDone', { action: labels[action] }))
    await loadRuntimeInstances()
  } catch (e) {
    notify.error(e, { title: `${labels[action]} ✕` })
  } finally {
    runtimeControlBusy.value = null
  }
}

function openReassignForm(targetRunId: string) {
  reassignForm.value = { session_id: '', interaction_id: '', target_run_id: targetRunId }
  showReassignSheet.value = true
}

async function submitReassign() {
  const { session_id, interaction_id, target_run_id } = reassignForm.value
  if (!session_id.trim() || !interaction_id.trim() || !target_run_id.trim()) {
    notify.error(new Error(t('settings.reassignFieldsRequired')))
    return
  }
  try {
    await api.reassignInteraction(session_id.trim(), interaction_id.trim(), { target_run_id: target_run_id.trim() })
    notify.success(t('settings.reassignDone'))
    showReassignSheet.value = false
    await loadRuntimeInstances()
  } catch (e) {
    notify.error(e, { title: t('settings.reassignFailed') })
  }
}

onBeforeUnmount(() => { if (runtimeTimer) clearInterval(runtimeTimer) })

defineExpose({ loadRuntimeInstances })
</script>

<template>
  <section class="center-resource-section agent-runtime-panel">
    <div class="center-resource-heading">
      <div>
        <h3>{{ t('settings.runtimeInstances') }}</h3>
        <p>{{ t('settings.runtimePanelHint') }}</p>
      </div>
      <UiBadge variant="outline">{{ runtimeInstances.length }}</UiBadge>
    </div>

    <div class="model-provider-toolbar ac-runtime-toolbar">
      <div class="model-provider-search">
        <Search :size="15" />
        <UiInput v-model="runtimeSessionFilter" :placeholder="t('agentCenter.filter.searchInstances')" />
      </div>
      <div class="model-provider-filters" role="group" aria-label="runtime-status">
        <button v-for="opt in runtimeStatusOptions" :key="opt" :class="{ active: runtimeStatusFilter === opt }" :aria-pressed="runtimeStatusFilter === opt" @click="runtimeStatusFilter = opt">
          {{ opt === 'all' ? t('settings.allStatuses') : opt }}
        </button>
      </div>
    </div>

    <div v-if="runtimeLoaded && filteredRuntimeInstances.length === 0" class="model-provider-empty">
      <Info :size="24" />
      <span>{{ t('agentCenter.runtimeInfo.noInstances') }}</span>
    </div>

    <div v-else class="ac-runtime-list">
      <div v-for="inst in pagedRuntimeInstances" :key="inst.id" class="ac-instance-row">
        <div class="ac-instance-head">
          <strong>{{ inst.agent_name ?? inst.agent_id ?? inst.id.slice(0, 8) }}</strong>
          <UiBadge :variant="readinessVariant(inst.status)">{{ inst.status }}</UiBadge>
          <span class="ac-instance-ids">run {{ inst.run_id.slice(0, 8) }} · session {{ String(inst.session_id ?? '—').slice(0, 8) }}</span>
        </div>
        <div v-if="inst.source_definition || inst.frozen_version || inst.recent_actual_model" class="ac-instance-evidence">
          <span v-if="inst.source_definition">
            {{ t('agentCenter.runtimeInfo.sourceDefinition') }}: {{ inst.source_definition.display_name }} · {{ inst.source_definition.source_kind }}<template v-if="inst.source_definition.managed"> · {{ t('settings.agentSourceManaged') }}</template>
          </span>
          <span v-if="inst.frozen_version">
            {{ t('agentCenter.runtimeInfo.frozenVersion') }}: {{ inst.frozen_version.agent_version_id.slice(0, 8) }} · {{ inst.frozen_version.content_hash.slice(0, 8) }}
          </span>
          <span v-if="inst.recent_actual_model">
            {{ t('agentCenter.runtimeInfo.recentModel') }}: {{ inst.recent_actual_model.provider_instance_id }}{{ inst.recent_actual_model.model ? ` · ${inst.recent_actual_model.model}` : '' }} · {{ inst.recent_actual_model.strategy_source }}<template v-if="inst.recent_actual_model.fallback_position > 0"> · fallback #{{ inst.recent_actual_model.fallback_position }}</template>
          </span>
          <span v-if="inst.fallback_summary">
            {{ t('agentCenter.runtimeInfo.fallbackSummary') }}: {{ inst.fallback_summary.attempts - inst.fallback_summary.failed_attempts }}/{{ inst.fallback_summary.attempts }}<template v-if="inst.fallback_summary.used_fallback"> · {{ t('agentCenter.runtimeInfo.usedFallback') }}</template>
          </span>
        </div>
        <div class="ac-instance-actions">
          <UiButton size="xs" variant="outline" :disabled="runtimeControlBusy === `${inst.run_id}:pause`" @click="controlInstance(inst.run_id, 'pause')">{{ t('agentCenter.runtimeInfo.actionPause') }}</UiButton>
          <UiButton size="xs" variant="outline" :disabled="runtimeControlBusy === `${inst.run_id}:resume`" @click="controlInstance(inst.run_id, 'resume')">{{ t('agentCenter.runtimeInfo.actionResume') }}</UiButton>
          <UiButton size="xs" variant="outline" :disabled="runtimeControlBusy === `${inst.run_id}:cancel`" @click="controlInstance(inst.run_id, 'cancel')">{{ t('agentCenter.runtimeInfo.actionCancel') }}</UiButton>
          <UiButton size="xs" variant="ghost" @click="openReassignForm(inst.run_id)">{{ t('agentCenter.runtimeInfo.actionSteer') }}</UiButton>
        </div>
      </div>

      <div class="ac-runtime-pager">
        <UiButton size="xs" variant="outline" :disabled="runtimePage <= 1" @click="runtimePage = Math.max(1, runtimePage - 1)">{{ t('settings.prevPage') }}</UiButton>
        <span class="quiet">{{ runtimePage }} / {{ runtimeTotalPages }}</span>
        <UiButton size="xs" variant="outline" :disabled="runtimePage >= runtimeTotalPages" @click="runtimePage = Math.min(runtimeTotalPages, runtimePage + 1)">{{ t('settings.nextPage') }}</UiButton>
      </div>
    </div>

    <UiSheet :open="showReassignSheet" side="right" @update:open="showReassignSheet = $event">
      <div class="ac-sheet-body">
        <h3>{{ t('settings.reassignTitle') }}</h3>
        <p class="quiet">{{ t('settings.reassignHint') }}</p>
        <div><UiLabel>Session ID</UiLabel><UiInput v-model="reassignForm.session_id" placeholder="session_id" /></div>
        <div><UiLabel>Interaction ID</UiLabel><UiInput v-model="reassignForm.interaction_id" placeholder="interaction_id" /></div>
        <div><UiLabel>Target Run ID</UiLabel><UiInput v-model="reassignForm.target_run_id" placeholder="target_run_id" /></div>
        <div class="ac-sheet-actions">
          <UiButton size="sm" variant="outline" @click="showReassignSheet = false">{{ t('settings.cancel') }}</UiButton>
          <UiButton size="sm" @click="submitReassign">{{ t('settings.reassignConfirm') }}</UiButton>
        </div>
      </div>
    </UiSheet>
  </section>
</template>

<style scoped>
.agent-runtime-panel {
  display: grid;
  gap: 12px;
}
.ac-runtime-list {
  display: grid;
  gap: 8px;
}
.ac-instance-row {
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  padding: 12px;
  display: grid;
  gap: 6px;
  background: var(--surface-section);
}
.ac-instance-head {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
}
.ac-instance-ids {
  color: var(--text-muted);
  font-size: 11px;
  font-family: ui-monospace, monospace;
}
.ac-instance-evidence {
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 11px;
  color: var(--text-secondary);
}
.ac-instance-evidence > span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.ac-instance-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.ac-runtime-pager {
  display: flex;
  gap: 8px;
  align-items: center;
  justify-content: center;
  margin-top: 8px;
}
.ac-sheet-body {
  display: grid;
  gap: 12px;
  min-width: 300px;
  padding: 4px 0;
}
.ac-sheet-actions {
  display: flex;
  gap: 8px;
  justify-content: flex-end;
  margin-top: 10px;
}
.quiet {
  color: var(--text-muted);
}
</style>
