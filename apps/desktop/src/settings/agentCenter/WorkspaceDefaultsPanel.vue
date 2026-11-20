<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { FileCode2, RefreshCw, Save, Shield } from '@lucide/vue'
import { UiBadge, UiButton, UiLabel, UiSelectField } from '@/components/ui'
import {
  api as baseApi,
  type AgentDefinitionDto,
  type AgentModeDto,
  type AgentRuntimePolicyDto,
  type AgentViewDto,
  type PackI18n,
  type PromptPipelineDto,
  type WorkspaceDefaultsDto,
} from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'
import { pickLocalized } from '@/lib/packI18n'

const api = scopedApi(baseApi, () => 'user')

type AgentLike = AgentViewDto & { i18n?: PackI18n | null }
type PipelineLike = PromptPipelineDto & { i18n?: PackI18n | null }

const props = defineProps<{
  agents: AgentLike[]
  modes: AgentModeDto[]
  pipelines?: PipelineLike[]
}>()

/**
 * `focus-toml` is emitted with the document id the TOML panel should select. It is the
 * document *key* (`runtime`, `agent`, …) as Core names it, because that is what
 * `TomlDocumentPanel.selectDocument(id)` resolves — passing a filename would make the
 * lookup fall back to "first document" and silently open the wrong file.
 */
const emit = defineEmits<{
  changed: []
  'focus-toml': [documentId: string]
}>()

/** The document that owns the ceilings this card reports. */
const RUNTIME_DOCUMENT_ID = 'runtime'

const { t, locale } = useI18n()
const { notify } = useNotifications()
const { canAccessBackend } = useHostAccess()

const defaults = ref<WorkspaceDefaultsDto | null>(null)
const fetchedPipelines = ref<PipelineLike[]>([])
const selectedModeId = ref('')
const selectedAgentId = ref('')
const selectedPipelineId = ref('')
const loading = ref(false)
const busy = ref(false)

const runtimePolicy = ref<AgentRuntimePolicyDto | null>(null)
const runtimePolicyLoaded = ref(false)
const runtimePolicyFailed = ref(false)

interface PolicyRow {
  key: string
  label: string
  value: string
  help?: string
  changed: boolean
}

const sourcePipelines = computed<PipelineLike[]>(() => props.pipelines ?? fetchedPipelines.value)

function modeDisplayName(mode: AgentModeDto): string {
  return pickLocalized(mode.i18n, locale.value, 'display_name', mode.display_name) ?? mode.display_name
}

function agentDisplayName(agent: AgentLike): string {
  const baseline = agent.display_name ?? agent.name ?? agent.slug ?? agent.id
  return pickLocalized((agent as AgentDefinitionDto).i18n, locale.value, 'display_name', baseline) ?? baseline
}

function pipelineDisplayName(pipeline: PipelineLike): string {
  const baseline = pipeline.display_name ?? pipeline.name ?? pipeline.title ?? pipeline.slug ?? pipeline.id
  return pickLocalized(pipeline.i18n, locale.value, 'display_name', baseline) ?? baseline
}

const publishedModes = computed(() =>
  props.modes.filter((mode) => mode.status === 'published' && mode.latest_published_mode_version_id),
)
const publishedAgents = computed(() =>
  props.agents.filter((agent) => (agent.status ? agent.status === 'published' : agent.enabled !== false)),
)
const publishedPipelines = computed(() =>
  sourcePipelines.value.filter((pipeline) => !pipeline.status || pipeline.status === 'published'),
)

const modeOptions = computed(() => [
  { value: '', label: t('settings.workspaceDefaultNone') },
  ...publishedModes.value.map((mode) => ({ value: mode.id, label: modeDisplayName(mode) })),
])
const agentOptions = computed(() => [
  { value: '', label: t('settings.workspaceDefaultNone') },
  ...publishedAgents.value.map((agent) => ({ value: agent.id, label: agentDisplayName(agent) })),
])
const pipelineOptions = computed(() => [
  { value: '', label: t('settings.workspaceDefaultNone') },
  ...publishedPipelines.value.map((pipeline) => ({ value: pipeline.id, label: pipelineDisplayName(pipeline) })),
])

const selectedMode = computed(() => publishedModes.value.find((mode) => mode.id === selectedModeId.value) ?? null)

const defaultAgentName = computed(() => {
  const id = defaults.value?.default_agent_definition_id
  if (!id) return null
  const agent = props.agents.find((item) => item.id === id)
  return agent ? agentDisplayName(agent) : id
})

const defaultModeName = computed(() => {
  const modeId = defaults.value?.default_agent_mode_id
  const versionId = defaults.value?.default_mode_version_id
  if (!modeId && !versionId) return null
  const mode = (modeId ? props.modes.find((item) => item.id === modeId) : null)
    ?? (versionId ? props.modes.find((item) => item.latest_published_mode_version_id === versionId || item.id === versionId) : null)
  return mode ? modeDisplayName(mode) : (modeId ?? versionId ?? null)
})

const defaultPipelineName = computed(() => {
  const id = defaults.value?.default_prompt_pipeline_id
  if (!id) return null
  const pipeline = sourcePipelines.value.find((item) => item.id === id)
  return pipeline ? pipelineDisplayName(pipeline) : id
})

const runtimePolicyRows = computed<PolicyRow[]>(() => {
  const policy = runtimePolicy.value
  if (!policy?.spawn || !policy?.scheduling || !policy?.supervision || !policy?.context || !policy?.tools) return []
  const deviations = new Set(policy.deviations ?? [])
  const disabled = t('settings.runtimeCeilings.disabled')
  const rows: Omit<PolicyRow, 'changed'>[] = [
    { key: 'spawn.max_depth', label: t('settings.runtimeCeilings.depth'), value: String(policy.spawn.max_depth), help: t('settings.runtimeCeilings.depthHint') },
    { key: 'spawn.max_agents_per_run', label: t('settings.runtimeCeilings.agentsPerRun'), value: String(policy.spawn.max_agents_per_run) },
    { key: 'spawn.max_parallel_workers', label: t('settings.runtimeCeilings.parallelWorkers'), value: String(policy.spawn.max_parallel_workers) },
    { key: 'scheduling.max_active_runs_per_session', label: t('settings.runtimeCeilings.activeRuns'), value: String(policy.scheduling.max_active_runs_per_session), help: t('settings.runtimeCeilings.activeRunsHint') },
    { key: 'scheduling.worker_retry_limit', label: t('settings.runtimeCeilings.workerRetries'), value: String(policy.scheduling.worker_retry_limit) },
    { key: 'supervision.max_revision_rounds', label: t('settings.runtimeCeilings.revisionRounds'), value: String(policy.supervision.max_revision_rounds), help: t('settings.runtimeCeilings.revisionRoundsHint') },
    { key: 'context.default_token_budget', label: t('settings.runtimeCeilings.contextWindow'), value: String(policy.context.default_token_budget), help: t('settings.runtimeCeilings.contextWindowHint') },
    {
      key: 'context.run_token_budget',
      label: t('settings.runtimeCeilings.runBudget'),
      value: policy.context.run_token_budget > 0 ? String(policy.context.run_token_budget) : disabled,
      help: t('settings.runtimeCeilings.runBudgetHint'),
    },
    { key: 'tools.max_tool_calls', label: t('settings.runtimeCeilings.toolCalls'), value: String(policy.tools.max_tool_calls), help: t('settings.runtimeCeilings.toolCallsHint') },
  ]
  return rows.map((row) => ({ ...row, changed: deviations.has(row.key) }))
})

async function loadRuntimePolicy() {
  if (!canAccessBackend.value) return
  try {
    runtimePolicy.value = await api.getAgentRuntimePolicy()
    runtimePolicyFailed.value = false
  } catch {
    runtimePolicyFailed.value = true
  } finally {
    runtimePolicyLoaded.value = true
  }
}

async function load() {
  if (!canAccessBackend.value) return
  loading.value = true
  void loadRuntimePolicy()
  try {
    const needsPipelines = props.pipelines === undefined
    const [loaded, pipelineList] = await Promise.all([
      api.getWorkspaceDefaults(),
      needsPipelines ? api.listPromptPipelines().catch(() => [] as PipelineLike[]) : Promise.resolve(props.pipelines ?? []),
    ])
    defaults.value = loaded
    if (needsPipelines) fetchedPipelines.value = Array.isArray(pipelineList) ? pipelineList : []
    selectedModeId.value = loaded.default_agent_mode_id ?? ''
    selectedAgentId.value = loaded.default_agent_definition_id ?? ''
    selectedPipelineId.value = loaded.default_prompt_pipeline_id ?? ''
  } catch (error) {
    defaults.value = null
    notify.error(error, { title: t('settings.workspaceDefaultsLoadFailed') })
  } finally {
    loading.value = false
  }
}

async function saveAndPublish() {
  if (!canAccessBackend.value) return
  busy.value = true
  try {
    const mode = selectedMode.value
    const nextAgentId = selectedAgentId.value || null
    const nextPipelineId = selectedPipelineId.value || null
    const agentChanged = nextAgentId !== (defaults.value?.default_agent_definition_id ?? null)
    const pipelineChanged = nextPipelineId !== (defaults.value?.default_prompt_pipeline_id ?? null)
    const body: Partial<WorkspaceDefaultsDto> = {
      default_agent_definition_id: nextAgentId,
      default_agent_version_id: nextAgentId ? (agentChanged ? null : defaults.value?.default_agent_version_id ?? null) : null,
      default_agent_mode_id: mode?.id ?? null,
      default_mode_version_id: mode?.latest_published_mode_version_id ?? null,
      default_prompt_pipeline_id: nextPipelineId,
      default_prompt_version_id: nextPipelineId ? (pipelineChanged ? null : defaults.value?.default_prompt_version_id ?? null) : null,
    }
    const draft = await api.updateWorkspaceDefaultsDraft(body, defaults.value?.revision)
    const published = await api.publishWorkspaceDefaults(draft.revision ?? null)
    defaults.value = published
    selectedModeId.value = published.default_agent_mode_id ?? ''
    selectedAgentId.value = published.default_agent_definition_id ?? ''
    selectedPipelineId.value = published.default_prompt_pipeline_id ?? ''
    notify.success(t('settings.workspaceDefaultsSaved'))
    emit('changed')
  } catch (error) {
    notify.error(error, { title: t('settings.workspaceDefaultsSaveFailed') })
  } finally {
    busy.value = false
  }
}

onMounted(() => { void load() })
watch(canAccessBackend, (ready) => { if (ready) void load() })
defineExpose({ load })
</script>

<template>
  <section class="defaults-panel" data-testid="workspace-defaults-panel">
    <div class="defaults-head">
      <div>
        <h3>{{ t('settings.workspaceDefaults') }}</h3>
        <p>{{ t('settings.workspaceDefaultsHint') }}</p>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading || !canAccessBackend" @click="load">
        <RefreshCw :size="14" />
        <span>{{ t('settings.refresh') }}</span>
      </UiButton>
    </div>

    <div class="defaults-current">
      <div>
        <span>{{ t('settings.workspaceDefaultMode') }}</span>
        <strong>{{ defaultModeName ?? t('settings.workspaceDefaultNone') }}</strong>
      </div>
      <div>
        <span>{{ t('settings.workspaceDefaultAgent') }}</span>
        <strong>{{ defaultAgentName ?? t('settings.workspaceDefaultNone') }}</strong>
      </div>
      <div>
        <span>{{ t('settings.workspaceDefaultPrompt') }}</span>
        <strong>{{ defaultPipelineName ?? t('settings.workspaceDefaultNone') }}</strong>
      </div>
      <UiBadge v-if="defaults?.status" variant="outline">{{ defaults.status }}</UiBadge>
    </div>

    <div class="defaults-actions">
      <div class="defaults-field">
        <UiLabel>{{ t('settings.workspaceDefaultMode') }}</UiLabel>
        <UiSelectField
          v-model="selectedModeId"
          :aria-label="t('settings.workspaceDefaultMode')"
          :disabled="!canAccessBackend || loading"
          :options="modeOptions"
        />
      </div>
      <div class="defaults-field">
        <UiLabel>{{ t('settings.workspaceDefaultAgent') }}</UiLabel>
        <UiSelectField
          v-model="selectedAgentId"
          :aria-label="t('settings.workspaceDefaultAgent')"
          :disabled="!canAccessBackend || loading"
          :options="agentOptions"
        />
      </div>
      <div class="defaults-field">
        <UiLabel>{{ t('settings.workspaceDefaultPrompt') }}</UiLabel>
        <UiSelectField
          v-model="selectedPipelineId"
          :aria-label="t('settings.workspaceDefaultPrompt')"
          :disabled="!canAccessBackend || loading"
          :options="pipelineOptions"
        />
      </div>
      <UiButton size="sm" :disabled="!canAccessBackend || busy || loading" @click="saveAndPublish">
        <Save :size="14" />
        <span>{{ t('settings.workspaceDefaultsSave') }}</span>
      </UiButton>
    </div>

    <!-- Compact read-only runtime.toml policy ceilings summary with focus shortcut -->
    <div class="defaults-policy-card" data-testid="workspace-runtime-ceilings">
      <div class="defaults-policy-head">
        <div class="defaults-policy-title">
          <Shield :size="14" />
          <strong>{{ t('settings.runtimeCeilings.title') }}</strong>
          <UiBadge v-if="runtimePolicy?.revision != null" variant="outline">{{ t('settings.runtimeCeilings.revision', { revision: runtimePolicy.revision }) }}</UiBadge>
        </div>
        <UiButton
          size="xs"
          variant="outline"
          :disabled="!canAccessBackend"
          data-testid="edit-runtime-toml"
          @click="emit('focus-toml', RUNTIME_DOCUMENT_ID)"
        >
          <FileCode2 :size="12" />
          <span>{{ t('settings.runtimeCeilings.editDocument') }}</span>
        </UiButton>
      </div>
      <p class="defaults-policy-hint">{{ t('settings.runtimeCeilings.hint') }}</p>
      <p v-if="runtimePolicyFailed || (runtimePolicyLoaded && runtimePolicyRows.length === 0)" class="defaults-policy-empty">
        {{ t('settings.runtimeCeilings.unavailable') }}
      </p>
      <div v-else-if="runtimePolicyRows.length > 0" class="defaults-policy-grid">
        <div v-for="row in runtimePolicyRows" :key="row.key" class="defaults-policy-row">
          <span class="defaults-policy-label" :title="row.help">{{ row.label }}</span>
          <strong class="defaults-policy-value">{{ row.value }}</strong>
          <UiBadge v-if="row.changed" variant="secondary">{{ t('settings.runtimeCeilings.changed') }}</UiBadge>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.defaults-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px;
  border: 1px solid var(--border-muted);
  border-radius: 12px;
  background: var(--surface-section);
}
.defaults-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
.defaults-head h3 { margin: 0 0 4px; }
.defaults-head p { margin: 0; color: var(--text-secondary); font-size: 12px; line-height: 1.5; }
.defaults-current { display: flex; gap: 18px; align-items: center; flex-wrap: wrap; }
.defaults-current > div { display: flex; flex-direction: column; gap: 2px; min-width: 140px; }
.defaults-current span { color: var(--text-muted); font-size: 11px; }
.defaults-current strong { font-size: 13px; }
.defaults-actions { display: flex; align-items: flex-end; gap: 10px; flex-wrap: wrap; }
.defaults-field { display: flex; flex-direction: column; gap: 4px; min-width: 190px; flex: 1 1 190px; }
.defaults-policy-card {
  margin-top: 4px;
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-chrome);
  display: grid;
  gap: 8px;
}
.defaults-policy-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; flex-wrap: wrap; }
.defaults-policy-title { display: inline-flex; align-items: center; gap: 6px; font-size: 12px; }
.defaults-policy-hint,
.defaults-policy-empty { margin: 0; color: var(--text-muted); font-size: 11px; line-height: 1.45; }
.defaults-policy-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
  gap: 4px 16px;
}
.defaults-policy-row { display: flex; align-items: center; gap: 6px; font-size: 11px; min-width: 0; }
.defaults-policy-label { color: var(--text-secondary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; cursor: help; }
.defaults-policy-value { font-family: var(--font-mono); color: var(--text-primary); margin-left: auto; }
</style>
