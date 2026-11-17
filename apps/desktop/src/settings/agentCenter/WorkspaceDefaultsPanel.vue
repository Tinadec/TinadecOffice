<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RefreshCw, Save } from '@lucide/vue'
import { UiBadge, UiButton, UiSelectField } from '@/components/ui'
import { api, type AgentModeDto, type AgentViewDto, type WorkspaceDefaultsDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'

const props = defineProps<{ agents: AgentViewDto[]; modes: AgentModeDto[] }>()
const { t } = useI18n()
const { notify } = useNotifications()

const defaults = ref<WorkspaceDefaultsDto | null>(null)
const selectedModeId = ref('')
const loading = ref(false)
const busy = ref(false)

const publishedModes = computed(() => props.modes.filter(mode =>
  mode.status === 'published' && mode.latest_published_mode_version_id))
const modeOptions = computed(() => [
  { value: '', label: t('settings.workspaceDefaultNone') },
  ...publishedModes.value.map(mode => ({ value: mode.id, label: mode.display_name })),
])
const selectedMode = computed(() => publishedModes.value.find(mode => mode.id === selectedModeId.value) ?? null)
const defaultAgentName = computed(() => {
  const id = defaults.value?.default_agent_definition_id
  return id ? props.agents.find(agent => agent.id === id)?.name ?? id : null
})
const defaultModeName = computed(() => {
  const id = defaults.value?.default_mode_version_id
  if (!id) return null
  const mode = props.modes.find(item => item.latest_published_mode_version_id === id) ?? props.modes.find(item => item.id === id)
  return mode?.display_name ?? id
})

async function load() {
  loading.value = true
  try {
    const loaded = await api.getWorkspaceDefaults()
    defaults.value = loaded
    selectedModeId.value = loaded.default_agent_mode_id ?? ''
  } catch (error) {
    defaults.value = null
    notify.error(error, { title: t('settings.workspaceDefaultsLoadFailed') })
  } finally {
    loading.value = false
  }
}

async function saveAndPublish() {
  busy.value = true
  try {
    const mode = selectedMode.value
    const body: Partial<WorkspaceDefaultsDto> = {
      default_agent_definition_id: defaults.value?.default_agent_definition_id ?? null,
      default_agent_version_id: defaults.value?.default_agent_version_id ?? null,
      default_agent_mode_id: mode?.id ?? null,
      default_mode_version_id: mode?.latest_published_mode_version_id ?? null,
      default_prompt_pipeline_id: defaults.value?.default_prompt_pipeline_id ?? null,
      default_prompt_version_id: defaults.value?.default_prompt_version_id ?? null,
    }
    const draft = await api.updateWorkspaceDefaultsDraft(body, defaults.value?.revision)
    const published = await api.publishWorkspaceDefaults(draft.revision ?? null)
    defaults.value = published
    notify.success(t('settings.workspaceDefaultsSaved'))
  } catch (error) {
    notify.error(error, { title: t('settings.workspaceDefaultsSaveFailed') })
  } finally {
    busy.value = false
  }
}

onMounted(() => { void load() })
defineExpose({ load })
</script>

<template>
  <section class="defaults-panel" data-testid="workspace-defaults-panel">
    <div class="defaults-head">
      <div>
        <h3>{{ t('settings.workspaceDefaults') }}</h3>
        <p>{{ t('settings.workspaceDefaultsHint') }}</p>
      </div>
      <UiButton variant="outline" size="sm" :disabled="loading" @click="load">
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
      <UiBadge variant="outline">{{ defaults?.status ?? t('settings.workspaceDefaultNone') }}</UiBadge>
    </div>

    <div class="defaults-actions">
      <UiSelectField
        v-model="selectedModeId"
        :aria-label="t('settings.workspaceDefaultMode')"
        :options="modeOptions"
      />
      <UiButton size="sm" :disabled="busy || loading" @click="saveAndPublish">
        <Save :size="14" />
        <span>{{ t('settings.workspaceDefaultsSave') }}</span>
      </UiButton>
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
.defaults-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.defaults-actions .ui-select { min-width: 220px; }
</style>
