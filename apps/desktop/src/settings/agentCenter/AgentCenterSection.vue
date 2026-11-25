<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { Activity, ArrowUpRight, PackageCheck, RefreshCw, X } from '@lucide/vue'
import { UiBadge, UiButton } from '@/components/ui'
import { focusDockCard } from '@/lib/homeDockSync'
import { graphSeedPackState } from '@/agentPacks/graphSeedPackBootstrap'
import AgentPacksPanel from '@/settings/sections/AgentPacksPanel.vue'
import AgentEvolutionPanel from '@/components/AgentEvolutionPanel.vue'
import GovernanceRolesPanel from '@/components/agentCenter/GovernanceRolesPanel.vue'
import LifecycleHooksPanel from './LifecycleHooksPanel.vue'
import TeamsModesMerged from './TeamsModesMerged.vue'
import TomlDocumentPanel from './TomlDocumentPanel.vue'
import WorkspaceDefaultsPanel from './WorkspaceDefaultsPanel.vue'
import ContextOverlapPanel from '@/components/trajectory/ContextOverlapPanel.vue'
import { triggerDiagnoseInComposer } from '@/lib/offlineDiagnosticReport'
import type { ContextOverlapAuditResult, ContextOverlapPairSummary } from '@/lib/contextOverlapWinnowing'
import type { AgentModeDto, AgentModeTopologyDto, AgentViewDto, PromptPipelineDto } from '@/api'

export type AgentCenterTabKey = 'teams' | 'modes' | 'agents' | 'prompts' | 'advanced'

const props = withDefaults(defineProps<{
  tab: AgentCenterTabKey
  agents: AgentViewDto[]
  modes: Array<AgentModeDto | AgentModeTopologyDto>
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

function openGovernanceRuntimeDrawer() {
  focusDockCard({ card: 'approval', approvalTab: 'pending' })
  void router.push({ name: 'home' })
}

function handleDiagnoseOverlap(audit: ContextOverlapAuditResult, pair?: ContextOverlapPairSummary) {
  void triggerDiagnoseInComposer({ overlap: audit, selectedOverlapPair: pair ?? null })
}

const teamsModesRef = ref<InstanceType<typeof TeamsModesMerged> | null>(null)
const packsPanelRef = ref<InstanceType<typeof AgentPacksPanel> | null>(null)
const defaultsRef = ref<InstanceType<typeof WorkspaceDefaultsPanel> | null>(null)
const hooksRef = ref<InstanceType<typeof LifecycleHooksPanel> | null>(null)
const evolutionRef = ref<InstanceType<typeof AgentEvolutionPanel> | null>(null)
const tomlRef = ref<InstanceType<typeof TomlDocumentPanel> | null>(null)

const packsDrawerOpen = ref(false)

const activeTab = computed<'teams' | 'agents' | 'prompts' | 'advanced'>(() =>
  props.tab === 'modes' ? 'teams' : props.tab,
)

const tabs = computed(() => [
  { key: 'teams' as const, label: t('settings.agentTeamsAndModes') },
  { key: 'agents' as const, label: t('settings.agents') },
  { key: 'prompts' as const, label: t('settings.promptEngineering') },
  { key: 'advanced' as const, label: t('settings.agentAdvancedTab') },
])

const packPhase = computed(() => graphSeedPackState.value.phase)
const packStatusLabel = computed(() => t(`agentPack.status.${packPhase.value}`))
const packBadgeVariant = computed<'default' | 'secondary' | 'outline'>(() => {
  if (packPhase.value === 'up_to_date') return 'default'
  if (packPhase.value === 'install' || packPhase.value === 'upgrade') return 'secondary'
  return 'outline'
})

function togglePacksDrawer() {
  packsDrawerOpen.value = !packsDrawerOpen.value
}

function closePacksDrawer() {
  packsDrawerOpen.value = false
}

function handlePackClone(slug?: string) {
  packsDrawerOpen.value = false
  emit('clone-pack', slug)
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && packsDrawerOpen.value) {
    packsDrawerOpen.value = false
  }
}

function refresh() {
  if (activeTab.value === 'teams') {
    void teamsModesRef.value?.reload()
    emit('changed')
  } else if (activeTab.value === 'advanced') {
    void defaultsRef.value?.load()
    void hooksRef.value?.reload()
    void evolutionRef.value?.loadProposals()
    void tomlRef.value?.reload()
    emit('changed')
  } else {
    emit('refresh-legacy', activeTab.value)
  }
  if (packsDrawerOpen.value) {
    void packsPanelRef.value?.reloadPackInventory()
  }
}

onMounted(() => {
  window.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
})

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
        <button
          type="button"
          class="agent-packs-pill"
          data-testid="agent-packs-pill"
          :aria-expanded="packsDrawerOpen"
          aria-controls="agent-packs-drawer-panel"
          @click="togglePacksDrawer"
        >
          <PackageCheck :size="14" />
          <span>{{ t('agentPack.pillLabel') }}</span>
          <UiBadge :variant="packBadgeVariant">{{ packStatusLabel }}</UiBadge>
        </button>

        <UiButton variant="outline" size="sm" :disabled="loading || !canAccessBackend" @click="refresh">
          <RefreshCw :size="14" />
          <span>{{ t('settings.refresh') }}</span>
        </UiButton>
      </div>
    </div>

    <div
      v-if="packsDrawerOpen"
      id="agent-packs-drawer-panel"
      class="agent-packs-drawer"
      data-testid="agent-packs-drawer"
      role="region"
      :aria-label="t('agentPack.drawerTitle')"
    >
      <div class="agent-packs-drawer-head">
        <strong>{{ t('agentPack.drawerTitle') }}</strong>
        <button
          type="button"
          class="agent-packs-drawer-close"
          data-testid="agent-packs-drawer-close"
          :aria-label="t('common.close')"
          @click="closePacksDrawer"
        >
          <X :size="14" />
        </button>
      </div>
      <div class="agent-packs-drawer-body">
        <AgentPacksPanel
          ref="packsPanelRef"
          @clone="handlePackClone"
          @changed="emit('changed')"
          @uninstalled="emit('changed')"
        />
      </div>
    </div>

    <div class="ac-subtabs" role="tablist" data-testid="agent-center-subtabs">
      <button
        v-for="item in tabs"
        :key="item.key"
        :class="['ac-subtab', { active: activeTab === item.key }]"
        role="tab"
        :aria-selected="activeTab === item.key"
        @click="emit('update:tab', item.key)"
      >
        {{ item.label }}
      </button>
    </div>

    <Transition name="center-stage-fade" mode="out-in">
      <div :key="activeTab" class="agent-center-stage-pane">
        <template v-if="activeTab === 'teams'">
          <TeamsModesMerged
            ref="teamsModesRef"
            :modes="modes"
            :agents="agents"
            :can-access-backend="canAccessBackend"
            :loading="loading"
            :preferred-view="tab === 'modes' ? 'topology' : undefined"
            @changed="emit('changed')"
            @open-graph="emit('open-graph', $event)"
          />
        </template>
        <template v-else-if="activeTab === 'advanced'">
          <div class="agent-advanced">
            <div class="agent-advanced-links">
              <div class="agent-advanced-link-copy">
                <Activity :size="15" />
                <p>{{ t('settings.agentGovernanceLiveHint') }}</p>
              </div>
              <UiButton variant="outline" size="sm" data-testid="open-governance-runtime-link" @click="openGovernanceRuntimeDrawer">
                <span>{{ t('settings.openGovernanceRuntime') }}</span>
                <ArrowUpRight :size="14" />
              </UiButton>
            </div>
            <WorkspaceDefaultsPanel
              ref="defaultsRef"
              :agents="agents"
              :modes="(modes as AgentModeDto[])"
              :pipelines="promptPipelines"
              @changed="emit('changed')"
              @focus-toml="tomlRef?.selectDocument($event)"
            />
            <GovernanceRolesPanel :manifest-tool-ids="manifestToolIds" :agents="agents" />
            <LifecycleHooksPanel ref="hooksRef" @changed="emit('changed')" />
            <AgentEvolutionPanel ref="evolutionRef" @changed="emit('changed')" />
            <TomlDocumentPanel ref="tomlRef" @changed="defaultsRef?.load()" />
          </div>
        </template>
        <template v-else-if="activeTab === 'prompts'">
          <div class="agent-prompts-stage">
            <slot name="prompts" />
            <ContextOverlapPanel
              auto-load-mode="prompts"
              :agents="agents"
              :prompt-pipelines="promptPipelines"
              :can-access-backend="canAccessBackend"
              collapsible
              :default-open="true"
              @diagnose-overlap="handleDiagnoseOverlap"
            />
          </div>
        </template>
        <slot v-else :name="activeTab" />
      </div>
    </Transition>
  </div>
</template>

<style scoped>
.agent-packs-pill {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 6px;
  height: var(--control-height-sm, 30px);
  padding: 0 10px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease;
}

.agent-packs-pill:hover,
.agent-packs-pill[aria-expanded='true'] {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
}

.agent-packs-pill:focus-visible,
.agent-packs-drawer-close:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.agent-packs-drawer {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 14px;
  margin-bottom: 10px;
  border-radius: 10px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 38%, var(--border-muted));
  background: var(--surface-raised);
  box-shadow: 0 6px 20px color-mix(in srgb, #000 12%, transparent);
}

.agent-packs-drawer-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.agent-packs-drawer-head strong {
  font-size: 13px;
}

.agent-packs-drawer-close {
  width: 26px;
  height: 26px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.agent-packs-drawer-close:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.agent-packs-drawer-body {
  display: grid;
  gap: 10px;
  min-width: 0;
}

.agent-center-stage-pane {
  min-width: 0;
}

.agent-prompts-stage {
  display: flex;
  flex-direction: column;
  gap: 14px;
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
