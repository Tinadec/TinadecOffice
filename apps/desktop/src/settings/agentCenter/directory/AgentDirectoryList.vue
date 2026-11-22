<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { CopyPlus, Sparkles } from '@lucide/vue'
import type { AgentDirectoryItemDto, AgentViewDto } from '@/api'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { UiBadge, UiButton } from '@/components/ui'
import {
  agentDisplayDescription,
  agentDisplayName,
  agentSourceKindLabelKey,
  agentTypeLabelKey,
  isAgentReadOnly as checkAgentReadOnly,
  runtimeChannelBadgeLabel,
  sourceBadgeVariant,
  type AgentDirectoryViewMode,
  type RuntimeChannelKind,
} from './agentDirectoryHelpers'

const props = defineProps<{
  viewMode: AgentDirectoryViewMode
  agentLayerFilter: 'all' | 'operation' | 'execution'
  filteredOperationAgents: AgentViewDto[]
  filteredExecutionAgents: AgentViewDto[]
  directoryById: Map<string, AgentDirectoryItemDto>
  agents: AgentViewDto[]
  selectedAgentId: string
  agentGeneratedCount: number
  canAccessBackend: boolean
  agentCloneBusy: boolean
  manifestToolCount: number
  agentAvatarSlug: (agent: AgentViewDto) => string
  agentRuntimeSummary: (agentId: string) => string
  agentRuntimeChannel?: (agentId: string) => RuntimeChannelKind | null
}>()

const emit = defineEmits<{
  select: [agent: AgentViewDto]
  clone: [agent: AgentViewDto]
  'launch-architect': []
}>()

const { t, te, locale } = useI18n()

function displayName(agent: AgentViewDto): string {
  return agentDisplayName(agent, props.directoryById.get(agent.id), locale.value)
}

function displayDescription(agent: AgentViewDto): string {
  return agentDisplayDescription(agent, props.directoryById.get(agent.id), locale.value)
}

function sourceKindFor(agentId: string): string {
  return props.directoryById.get(agentId)?.source_kind ?? props.agents.find((a) => a.id === agentId)?.source_kind ?? 'custom'
}

function sourceKindLabel(kind: string): string {
  const key = agentSourceKindLabelKey(kind)
  return key ? t(key) : kind
}

function typeLabel(type: string): string {
  const key = agentTypeLabelKey(type)
  return key ? t(key) : type
}

function isReadOnly(agent: AgentViewDto): boolean {
  return checkAgentReadOnly(agent, props.directoryById, props.agents)
}

function definitionStatusLabel(value: string): string {
  return te(`agentCenter.filter.${value}`) ? t(`agentCenter.filter.${value}`) : value
}

function toolSummaryText(agent: AgentViewDto): string {
  const tools = agent.allowed_tools ?? []
  if (tools.includes('*')) return `* (${props.manifestToolCount || '*'})`
  return String(tools.length)
}
</script>

<template>
  <aside v-if="viewMode === 'list'" class="center-resource-rail" :aria-label="t('settings.centerResources')">
    <div class="center-pane-heading">
      <div>
        <span>{{ t('settings.centerResources') }}</span>
        <strong>{{ t('settings.agentProfiles') }}</strong>
      </div>
      <UiBadge v-if="agentGeneratedCount > 0" variant="default">{{ agentGeneratedCount }}</UiBadge>
    </div>

    <section v-if="agentLayerFilter !== 'execution'" class="agent-column compact">
      <div class="model-section-header">
        <h3>{{ t('settings.planningLayer') }}</h3>
        <UiBadge variant="secondary">{{ filteredOperationAgents.length }}</UiBadge>
      </div>
      <article
        v-for="agent in filteredOperationAgents"
        :key="agent.id"
        class="agent-card"
        :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
      >
        <button type="button" class="agent-card-select" @click="emit('select', agent)">
          <div class="agent-card-icon pet">
            <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="30" :alt="displayName(agent)" />
          </div>
          <div class="agent-card-main">
            <div class="agent-card-title-row">
              <strong>{{ displayName(agent) || typeLabel(agent.agent_type) }}</strong>
              <UiBadge :variant="sourceBadgeVariant(sourceKindFor(agent.id))" class="agent-source-pill">
                {{ sourceKindLabel(sourceKindFor(agent.id)) }}
              </UiBadge>
            </div>
            <span>{{ displayDescription(agent) || typeLabel(agent.role ?? agent.agent_type) }}</span>
            <small class="roster-runtime-line" :title="agentRuntimeSummary(agent.id)">
              <span
                v-if="agentRuntimeChannel?.(agent.id)"
                :class="['runtime-channel-badge', `channel-${agentRuntimeChannel(agent.id)}`]"
              >
                {{ runtimeChannelBadgeLabel(agentRuntimeChannel(agent.id)!) }}
              </span>
              <span class="roster-runtime-text">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</span>
            </small>
          </div>
          <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
            {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
          </UiBadge>
        </button>
      </article>
    </section>

    <section v-if="agentLayerFilter !== 'operation'" class="agent-column compact">
      <div class="model-section-header">
        <h3>{{ t('settings.executionLayer') }}</h3>
        <UiBadge variant="secondary">{{ filteredExecutionAgents.length }}</UiBadge>
      </div>
      <article
        v-for="agent in filteredExecutionAgents"
        :key="agent.id"
        class="agent-card"
        :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
      >
        <button type="button" class="agent-card-select" @click="emit('select', agent)">
          <div class="agent-card-icon pet execution">
            <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="30" :alt="displayName(agent)" />
          </div>
          <div class="agent-card-main">
            <div class="agent-card-title-row">
              <strong>{{ displayName(agent) || typeLabel(agent.agent_type) }}</strong>
              <UiBadge :variant="sourceBadgeVariant(sourceKindFor(agent.id))" class="agent-source-pill">
                {{ sourceKindLabel(sourceKindFor(agent.id)) }}
              </UiBadge>
            </div>
            <span>{{ displayDescription(agent) || typeLabel(agent.role ?? agent.agent_type) }}</span>
            <small class="roster-runtime-line" :title="agentRuntimeSummary(agent.id)">
              <span
                v-if="agentRuntimeChannel?.(agent.id)"
                :class="['runtime-channel-badge', `channel-${agentRuntimeChannel(agent.id)}`]"
              >
                {{ runtimeChannelBadgeLabel(agentRuntimeChannel(agent.id)!) }}
              </span>
              <span class="roster-runtime-text">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</span>
            </small>
          </div>
          <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
            {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
          </UiBadge>
        </button>
      </article>
    </section>

    <p v-if="!filteredOperationAgents.length && !filteredExecutionAgents.length" class="quiet">
      {{ t('settings.noFilteredAgents') }}
    </p>
  </aside>

  <main v-else class="center-resource-stage" data-testid="agent-role-roster">
    <div class="roster-stage-head">
      <div>
        <h3>{{ t('settings.roleRosterTitle') }}</h3>
        <p class="quiet">{{ t('settings.roleRosterHint') }}</p>
      </div>
      <UiButton variant="ghost" size="sm" :disabled="!canAccessBackend" @click="emit('launch-architect')">
        <Sparkles :size="14" />
        <span>{{ t('settings.agentArchitectHintShort') }}</span>
      </UiButton>
    </div>

    <section v-if="agentLayerFilter !== 'execution'" class="roster-layer-section">
      <div class="model-section-header">
        <h3>{{ t('settings.planningLayer') }}</h3>
        <UiBadge variant="secondary">{{ filteredOperationAgents.length }}</UiBadge>
      </div>
      <div class="roster-card-grid">
        <article
          v-for="agent in filteredOperationAgents"
          :key="agent.id"
          class="agent-card roster-card"
          :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
        >
          <button type="button" class="agent-card-select roster-card-select" @click="emit('select', agent)">
            <div class="roster-card-top">
              <div class="agent-card-icon pet">
                <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="34" :alt="displayName(agent)" />
              </div>
              <div class="roster-card-identity">
                <strong>{{ displayName(agent) || typeLabel(agent.agent_type) }}</strong>
                <span>{{ agent.slug ?? agent.id.slice(0, 8) }} · {{ typeLabel(agent.role ?? agent.agent_type) }}</span>
              </div>
              <UiBadge :variant="sourceBadgeVariant(sourceKindFor(agent.id))">
                {{ sourceKindLabel(sourceKindFor(agent.id)) }}
              </UiBadge>
            </div>
            <p class="roster-card-desc">{{ displayDescription(agent) || t('settings.agentNoDescription') }}</p>
            <div class="roster-card-foot">
              <small class="roster-runtime-line" :title="agentRuntimeSummary(agent.id)">
                <span
                  v-if="agentRuntimeChannel?.(agent.id)"
                  :class="['runtime-channel-badge', `channel-${agentRuntimeChannel(agent.id)}`]"
                >
                  {{ runtimeChannelBadgeLabel(agentRuntimeChannel(agent.id)!) }}
                </span>
                <span class="roster-runtime-text">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</span>
              </small>
              <div class="roster-card-badges">
                <UiBadge variant="outline">{{ toolSummaryText(agent) }}</UiBadge>
                <UiBadge :variant="agent.status === 'published' ? 'default' : 'secondary'">
                  {{ definitionStatusLabel(agent.status ?? 'draft') }}
                </UiBadge>
              </div>
            </div>
          </button>
          <button
            v-if="isReadOnly(agent)"
            type="button"
            class="roster-quick-clone"
            :title="t('settings.cloneAgent')"
            :disabled="agentCloneBusy"
            @click.stop="emit('clone', agent)"
          >
            <CopyPlus :size="13" />
            <span>{{ t('settings.cloneAgent') }}</span>
          </button>
        </article>
      </div>
    </section>

    <section v-if="agentLayerFilter !== 'operation'" class="roster-layer-section">
      <div class="model-section-header">
        <h3>{{ t('settings.executionLayer') }}</h3>
        <UiBadge variant="secondary">{{ filteredExecutionAgents.length }}</UiBadge>
      </div>
      <div class="roster-card-grid">
        <article
          v-for="agent in filteredExecutionAgents"
          :key="agent.id"
          class="agent-card roster-card"
          :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
        >
          <button type="button" class="agent-card-select roster-card-select" @click="emit('select', agent)">
            <div class="roster-card-top">
              <div class="agent-card-icon pet execution">
                <AgentPetAvatar :pet-slug="agentAvatarSlug(agent)" :size="34" :alt="displayName(agent)" />
              </div>
              <div class="roster-card-identity">
                <strong>{{ displayName(agent) || typeLabel(agent.agent_type) }}</strong>
                <span>{{ agent.slug ?? agent.id.slice(0, 8) }} · {{ typeLabel(agent.role ?? agent.agent_type) }}</span>
              </div>
              <UiBadge :variant="sourceBadgeVariant(sourceKindFor(agent.id))">
                {{ sourceKindLabel(sourceKindFor(agent.id)) }}
              </UiBadge>
            </div>
            <p class="roster-card-desc">{{ displayDescription(agent) || t('settings.agentNoDescription') }}</p>
            <div class="roster-card-foot">
              <small class="roster-runtime-line" :title="agentRuntimeSummary(agent.id)">
                <span
                  v-if="agentRuntimeChannel?.(agent.id)"
                  :class="['runtime-channel-badge', `channel-${agentRuntimeChannel(agent.id)}`]"
                >
                  {{ runtimeChannelBadgeLabel(agentRuntimeChannel(agent.id)!) }}
                </span>
                <span class="roster-runtime-text">{{ agentRuntimeSummary(agent.id) || t('settings.runtimeUnresolved') }}</span>
              </small>
              <div class="roster-card-badges">
                <UiBadge variant="outline">{{ toolSummaryText(agent) }}</UiBadge>
                <UiBadge :variant="agent.status === 'published' ? 'default' : 'secondary'">
                  {{ definitionStatusLabel(agent.status ?? 'draft') }}
                </UiBadge>
              </div>
            </div>
          </button>
          <button
            v-if="isReadOnly(agent)"
            type="button"
            class="roster-quick-clone"
            :title="t('settings.cloneAgent')"
            :disabled="agentCloneBusy"
            @click.stop="emit('clone', agent)"
          >
            <CopyPlus :size="13" />
            <span>{{ t('settings.cloneAgent') }}</span>
          </button>
        </article>
      </div>
    </section>

    <p v-if="!filteredOperationAgents.length && !filteredExecutionAgents.length" class="quiet">
      {{ t('settings.noFilteredAgents') }}
    </p>
  </main>
</template>

<style scoped>
.roster-stage-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 12px;
}

.roster-stage-head h3 {
  margin: 0;
  font-size: 14px;
}

.roster-stage-head p {
  margin: 2px 0 0;
  font-size: 12px;
}

.roster-layer-section {
  display: grid;
  gap: 8px;
  margin-bottom: 16px;
}

.roster-card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(230px, 1fr));
  gap: 10px;
}

.roster-card {
  position: relative;
  display: flex;
  flex-direction: column;
  padding: 0;
}

.roster-card-select {
  display: grid;
  gap: 8px;
  padding: 12px;
  text-align: left;
  width: 100%;
}

.roster-card-top {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}

.roster-card-identity {
  flex: 1;
  min-width: 0;
  display: grid;
  gap: 2px;
}

.roster-card-identity strong {
  font-size: 13px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.roster-card-identity span {
  font-size: 11px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.roster-card-desc {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  min-height: 32px;
}

.roster-card-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted);
}

.roster-card-foot small {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-muted);
  font-size: 11px;
}

.roster-card-badges {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.roster-runtime-line {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  max-width: 100%;
}

.roster-runtime-text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.roster-quick-clone {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  gap: 4px;
  height: var(--control-height-xs, 26px);
  margin: 0 12px 10px;
  padding: 0 8px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-default);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
  white-space: nowrap;
  cursor: pointer;
}

.roster-quick-clone:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.roster-quick-clone:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.agent-card-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.agent-card-title-row strong {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-source-pill {
  flex-shrink: 0;
}
</style>
