<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { RefreshCw } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import AgentPacksPanel from '@/settings/sections/AgentPacksPanel.vue'
import TeamsTab from './TeamsTab.vue'
import TomlDocumentPanel from './TomlDocumentPanel.vue'
import WorkspaceDefaultsPanel from './WorkspaceDefaultsPanel.vue'
import type { AgentModeDto, AgentViewDto } from '@/api'

export type AgentCenterTabKey =
  | 'teams' | 'agents' | 'modes' | 'prompts' | 'evolution' | 'runtime' | 'advanced'

const props = defineProps<{
  tab: AgentCenterTabKey
  agents: AgentViewDto[]
  modes: AgentModeDto[]
  canAccessBackend: boolean
  loading: boolean
}>()

const emit = defineEmits<{
  'update:tab': [tab: AgentCenterTabKey]
  changed: []
  'clone-pack': []
  'refresh-legacy': [tab: AgentCenterTabKey]
  'open-graph': [id: string]
}>()

const { t } = useI18n()
const teamsTabRef = ref<InstanceType<typeof TeamsTab> | null>(null)
const defaultsRef = ref<InstanceType<typeof WorkspaceDefaultsPanel> | null>(null)
const tomlRef = ref<InstanceType<typeof TomlDocumentPanel> | null>(null)

const tabs = computed(() => [
  { key: 'teams' as const, label: t('settings.agentTeams') },
  { key: 'agents' as const, label: t('settings.agents') },
  { key: 'modes' as const, label: t('settings.agentModes') },
  { key: 'prompts' as const, label: t('settings.promptEngineering') },
  { key: 'evolution' as const, label: t('settings.agentEvolution') },
  { key: 'runtime' as const, label: t('settings.runtimeInstances') },
  { key: 'advanced' as const, label: t('settings.advanced') },
])

function refresh() {
  if (props.tab === 'teams') {
    void teamsTabRef.value?.reload()
    emit('changed')
  } else if (props.tab === 'advanced') {
    void defaultsRef.value?.load()
    void tomlRef.value?.reload()
  } else {
    emit('refresh-legacy', props.tab)
  }
}

defineExpose({ refresh })
</script>

<template>
  <div class="agent-center-merged" data-testid="agent-center-section">
    <div class="center-command-bar">
      <div>
        <h2>{{ t('settings.agentCenter') }}</h2>
        <p>{{ t('settings.agentCenterSubtitle') }}</p>
      </div>
      <div class="center-command-actions">
        <UiButton variant="outline" size="sm" :disabled="loading || !canAccessBackend" @click="refresh">
          <RefreshCw :size="14" />
          <span>{{ t('settings.refresh') }}</span>
        </UiButton>
      </div>
    </div>

    <AgentPacksPanel @clone="emit('clone-pack')" @uninstalled="emit('changed')" />

    <div class="ac-subtabs" role="tablist" data-testid="agent-center-subtabs">
      <button
        v-for="item in tabs"
        :key="item.key"
        :class="['ac-subtab', { active: tab === item.key }]"
        role="tab"
        :aria-selected="tab === item.key"
        @click="emit('update:tab', item.key)"
      >
        {{ item.label }}
      </button>
    </div>

    <template v-if="tab === 'teams'">
      <TeamsTab
        ref="teamsTabRef"
        :modes="modes"
        :agents="agents"
        :can-access-backend="canAccessBackend"
        :loading="loading"
        @changed="emit('changed')"
        @open-graph="emit('open-graph', $event)"
      />
    </template>
    <template v-else-if="tab === 'advanced'">
      <div class="agent-advanced">
        <WorkspaceDefaultsPanel ref="defaultsRef" :agents="agents" :modes="modes" />
        <TomlDocumentPanel ref="tomlRef" />
        <div class="agent-advanced-links">
          <p>{{ t('settings.agentAdvancedHint') }}</p>
          <UiButton variant="outline" size="sm" @click="emit('update:tab', 'evolution')">{{ t('settings.agentEvolution') }}</UiButton>
          <UiButton variant="outline" size="sm" @click="emit('update:tab', 'runtime')">{{ t('settings.runtimeInstances') }}</UiButton>
        </div>
      </div>
    </template>
    <slot v-else :name="tab" />
  </div>
</template>
