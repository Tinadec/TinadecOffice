<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { Activity, ArrowUpRight, RefreshCw } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import AgentPacksPanel from '@/settings/sections/AgentPacksPanel.vue'
import AgentEvolutionPanel from '@/components/AgentEvolutionPanel.vue'
import GovernanceRolesPanel from '@/components/agentCenter/GovernanceRolesPanel.vue'
import TeamsTab from './TeamsTab.vue'
import TomlDocumentPanel from './TomlDocumentPanel.vue'
import WorkspaceDefaultsPanel from './WorkspaceDefaultsPanel.vue'
import type { AgentModeDto, AgentViewDto, PromptPipelineDto } from '@/api'

export type AgentCenterTabKey = 'teams' | 'modes' | 'agents' | 'prompts' | 'advanced'

const props = withDefaults(defineProps<{
  tab: AgentCenterTabKey
  agents: AgentViewDto[]
  modes: AgentModeDto[]
  promptPipelines?: PromptPipelineDto[]
  manifestToolIds?: string[]
  canAccessBackend: boolean
  loading: boolean
}>(), {
  promptPipelines: () => [],
  manifestToolIds: () => [],
})

const emit = defineEmits<{
  'update:tab': [tab: AgentCenterTabKey]
  changed: []
  'clone-pack': [slug?: string]
  'refresh-legacy': [tab: AgentCenterTabKey]
  'open-graph': [id: string]
}>()

const { t } = useI18n()
const router = useRouter()
const teamsTabRef = ref<InstanceType<typeof TeamsTab> | null>(null)
const defaultsRef = ref<InstanceType<typeof WorkspaceDefaultsPanel> | null>(null)
const evolutionRef = ref<InstanceType<typeof AgentEvolutionPanel> | null>(null)
const tomlRef = ref<InstanceType<typeof TomlDocumentPanel> | null>(null)

const tabs = computed(() => [
  { key: 'teams' as const, label: t('settings.agentTeams') },
  { key: 'modes' as const, label: t('settings.agentModes') },
  { key: 'agents' as const, label: t('settings.agents') },
  { key: 'prompts' as const, label: t('settings.promptEngineering') },
  { key: 'advanced' as const, label: t('settings.agentAdvancedTab') },
])

function refresh() {
  if (props.tab === 'teams') {
    void teamsTabRef.value?.reload()
    emit('changed')
  } else if (props.tab === 'advanced') {
    void defaultsRef.value?.load()
    void evolutionRef.value?.loadProposals()
    void tomlRef.value?.reload()
    emit('changed')
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

    <div v-show="tab === 'agents'" class="agent-center-packs-slot">
      <AgentPacksPanel
        @clone="emit('clone-pack', $event)"
        @changed="emit('changed')"
        @uninstalled="emit('changed')"
      />
    </div>

    <Transition name="center-stage-fade" mode="out-in">
      <div :key="tab" class="agent-center-stage-pane">
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
            <div class="agent-advanced-links">
              <div class="agent-advanced-link-copy">
                <Activity :size="15" />
                <p>{{ t('settings.agentGovernanceLiveHint') }}</p>
              </div>
              <UiButton variant="outline" size="sm" data-testid="open-governance-runtime-link" @click="router.push('/governance')">
                <span>{{ t('settings.openGovernanceRuntime') }}</span>
                <ArrowUpRight :size="14" />
              </UiButton>
            </div>
            <WorkspaceDefaultsPanel
              ref="defaultsRef"
              :agents="agents"
              :modes="modes"
              :pipelines="promptPipelines"
              @changed="emit('changed')"
              @focus-toml="tomlRef?.selectDocument($event)"
            />
            <GovernanceRolesPanel :manifest-tool-ids="manifestToolIds" :agents="agents" />
            <AgentEvolutionPanel ref="evolutionRef" @changed="emit('changed')" />
            <TomlDocumentPanel ref="tomlRef" @changed="defaultsRef?.load()" />
          </div>
        </template>
        <slot v-else :name="tab" />
      </div>
    </Transition>
  </div>
</template>

<style scoped>
.agent-center-packs-slot {
  display: grid;
  gap: 12px;
  min-width: 0;
}

.agent-center-stage-pane {
  min-width: 0;
}

.agent-advanced-link-copy {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-right: auto;
  color: var(--text-secondary);
}
</style>
