<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { ArrowUpRight, Shuffle, Sparkles } from '@lucide/vue'
import type { AgentDirectoryItemDto, AgentProvenanceDto, AgentRuntimeBindingDto, AgentViewDto } from '@/api'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { UiInput, UiLabel, UiSelectField, UiSwitch } from '@/components/ui'
import {
  agentDisplayName,
  agentSourceKindLabelKey,
} from './agentDirectoryHelpers'

export interface UsedByModeChip {
  modeId: string
  label: string
  nodeKey?: string | null
  status?: string | null
}

const props = defineProps<{
  configuringAgent: AgentViewDto
  configuringDirectoryItem: AgentDirectoryItemDto | null
  configuringRuntimeBinding: AgentRuntimeBindingDto | null
  configuringIsReadOnly: boolean
  showProvenanceCard: boolean
  configuringProvenance: AgentProvenanceDto | null
  directoryById: Map<string, AgentDirectoryItemDto>
  agents: AgentViewDto[]
  usedByModes: UsedByModeChip[]
  editName: string
  editLayer: 'operation' | 'execution'
  editRole: string
  editDescription: string
  busy: boolean
  agentAvatarBusy: boolean
  configuringAvatarPreference: { mode: 'random' | 'fixed'; petSlug?: string }
  agentAvatarPool: Array<{ slug: string; displayName?: string }>
}>()

const emit = defineEmits<{
  'update:editName': [value: string]
  'update:editLayer': [value: 'operation' | 'execution']
  'update:editRole': [value: string]
  'update:editDescription': [value: string]
  'set-enabled': [enabled: boolean]
  'choose-avatar': [mode: 'random' | 'fixed', petSlug?: string]
  'open-mode': [modeId: string]
}>()

const { t, te, locale } = useI18n()

function displayName(agent: AgentViewDto): string {
  return agentDisplayName(agent, props.directoryById.get(agent.id), locale.value)
}

function creatorAgentLabel(creatorId: string | null | undefined): string {
  if (!creatorId) return '—'
  const found = props.agents.find((a) => a.id === creatorId || a.slug === creatorId)
  return found ? `${displayName(found)} (${found.slug ?? creatorId})` : creatorId
}

function sourceKindLabel(kind: string): string {
  const key = agentSourceKindLabelKey(kind)
  return key ? t(key) : kind
}

function definitionStatusLabel(value: string): string {
  return te(`agentCenter.filter.${value}`) ? t(`agentCenter.filter.${value}`) : value
}
</script>

<template>
  <!-- Provenance (`agent_generated`) -->
  <div v-if="showProvenanceCard" class="agent-config-section agent-provenance-box" data-testid="agent-provenance-section">
    <div class="agent-config-section-title">
      <Sparkles :size="14" />
      <span>{{ t('settings.agentProvenanceTitle') }}</span>
    </div>
    <div class="agent-detail-grid">
      <div>
        <span>{{ t('settings.agentProvenanceCreator') }}</span>
        <strong>{{ creatorAgentLabel(configuringProvenance?.created_by_agent_id) }}</strong>
      </div>
      <div>
        <span>{{ t('settings.agentProvenanceRun') }}</span>
        <strong>{{ configuringProvenance?.created_by_run_id || '—' }}</strong>
      </div>
    </div>
    <p v-if="configuringProvenance?.reason" class="agent-config-hint">
      {{ t('settings.agentProvenanceReason') }}: {{ configuringProvenance.reason }}
    </p>
  </div>

  <!-- Basic Identity -->
  <div class="agent-config-section" data-testid="agent-section-identity">
    <div class="agent-config-section-title">{{ t('settings.agentSectionIdentity') }}</div>
    <div class="agent-identity-grid">
      <div>
        <span>{{ t('settings.agentSlugLabel') }}</span>
        <strong>{{ configuringAgent.slug ?? configuringAgent.id.slice(0, 8) }}</strong>
      </div>
      <div>
        <span>{{ t('settings.routePurpose') }}</span>
        <strong>{{ configuringRuntimeBinding?.route_purpose ?? configuringAgent.model_route_purpose ?? '—' }}</strong>
      </div>
    </div>

    <div class="agent-definition-fields">
      <div class="settings-field">
        <UiLabel>{{ t('settings.agentName') }}</UiLabel>
        <UiInput
          :model-value="editName"
          data-testid="agent-edit-name"
          :disabled="configuringIsReadOnly"
          :placeholder="t('settings.agentName')"
          @update:model-value="emit('update:editName', String($event ?? ''))"
        />
      </div>
      <div class="agent-identity-grid">
        <div class="settings-field">
          <UiLabel>{{ t('settings.agentLayer') }}</UiLabel>
          <UiSelectField
            :model-value="editLayer"
            data-testid="agent-edit-layer"
            :disabled="configuringIsReadOnly"
            :aria-label="t('settings.agentLayer')"
            :options="[
              { value: 'operation', label: t('settings.agentLayerPlanning') },
              { value: 'execution', label: t('settings.agentLayerExecution') },
            ]"
            @update:model-value="emit('update:editLayer', $event === 'execution' ? 'execution' : 'operation')"
          />
        </div>
        <div class="settings-field">
          <UiLabel>{{ t('settings.agentRole') }}</UiLabel>
          <UiInput
            :model-value="editRole"
            data-testid="agent-edit-role"
            :disabled="configuringIsReadOnly"
            placeholder="session_coordinator / code_writer"
            @update:model-value="emit('update:editRole', String($event ?? ''))"
          />
        </div>
      </div>
      <div class="settings-field">
        <UiLabel>{{ t('settings.agentDescription') }}</UiLabel>
        <textarea
          :value="editDescription"
          class="settings-textarea"
          rows="2"
          :disabled="configuringIsReadOnly"
          :placeholder="t('settings.agentDescriptionPlaceholder')"
          @input="emit('update:editDescription', ($event.target as HTMLTextAreaElement).value)"
        ></textarea>
      </div>
    </div>

    <div class="agent-config-switch">
      <div>
        <strong>{{ t('settings.agentEnabled') }}</strong>
        <span>{{ configuringIsReadOnly ? t('settings.builtInAgent') : t('settings.agentEnabledHint') }}</span>
        <small v-if="configuringIsReadOnly" class="agent-builtin-label">{{ t('settings.builtInCloneHint') }}</small>
      </div>
      <UiSwitch
        :model-value="configuringAgent.enabled"
        :disabled="busy || configuringIsReadOnly"
        @update:model-value="emit('set-enabled', $event)"
      />
    </div>
  </div>

  <!-- Avatar -->
  <div class="agent-config-section" data-testid="agent-avatar-section">
    <div class="agent-config-section-title">{{ t('settings.agentAvatarSection') }}</div>
    <p class="agent-config-hint">{{ t('settings.agentAvatarHint') }}</p>
    <div class="agent-avatar-choices" role="radiogroup" :aria-label="t('settings.agentAvatarSection')">
      <button
        type="button"
        role="radio"
        class="agent-avatar-choice"
        :class="{ active: configuringAvatarPreference.mode !== 'fixed' }"
        :aria-checked="configuringAvatarPreference.mode !== 'fixed'"
        :disabled="agentAvatarBusy"
        data-testid="agent-avatar-random"
        @click="emit('choose-avatar', 'random')"
      >
        <span class="agent-avatar-choice-glyph"><Shuffle :size="18" /></span>
        <span class="agent-avatar-choice-name">{{ t('settings.agentAvatarRandom') }}</span>
      </button>
      <button
        v-for="pet in agentAvatarPool"
        :key="pet.slug"
        type="button"
        role="radio"
        class="agent-avatar-choice"
        :class="{ active: configuringAvatarPreference.mode === 'fixed' && configuringAvatarPreference.petSlug === pet.slug }"
        :aria-checked="configuringAvatarPreference.mode === 'fixed' && configuringAvatarPreference.petSlug === pet.slug"
        :disabled="agentAvatarBusy"
        :data-testid="`agent-avatar-${pet.slug}`"
        @click="emit('choose-avatar', 'fixed', pet.slug)"
      >
        <AgentPetAvatar :pet-slug="pet.slug" :size="26" :alt="pet.displayName ?? pet.slug" />
        <span class="agent-avatar-choice-name">{{ pet.displayName ?? pet.slug }}</span>
      </button>
    </div>
    <p v-if="agentAvatarPool.length === 0" class="agent-config-hint">{{ t('settings.agentAvatarEmpty') }}</p>
  </div>

  <!-- Reverse References: Used by Teams & Modes -->
  <div class="agent-config-section" data-testid="agent-used-by-modes">
    <div class="agent-config-section-title">{{ t('settings.agentUsedByModes') }}</div>
    <div v-if="configuringDirectoryItem" class="agent-detail-grid">
      <div>
        <span>{{ t('settings.agentSourceLabel') }}</span>
        <strong>
          {{ sourceKindLabel(configuringDirectoryItem.source_kind) }}
          <template v-if="configuringDirectoryItem.managed"> · {{ t('settings.agentSourceManaged') }}</template>
        </strong>
      </div>
      <div>
        <span>{{ t('settings.agentStatus') }}</span>
        <strong>{{ definitionStatusLabel(configuringDirectoryItem.status) }}</strong>
      </div>
    </div>
    <div v-if="usedByModes.length > 0" class="agent-used-by-chips">
      <button
        v-for="chip in usedByModes"
        :key="`${chip.modeId}:${chip.nodeKey ?? ''}`"
        type="button"
        class="agent-used-by-chip"
        :data-testid="`agent-mode-chip-${chip.modeId}`"
        :title="t('settings.agentJumpToMode')"
        @click="emit('open-mode', chip.modeId)"
      >
        <span>{{ chip.label }}</span>
        <small v-if="chip.nodeKey">· {{ chip.nodeKey }}</small>
        <ArrowUpRight :size="12" aria-hidden="true" />
      </button>
    </div>
    <p v-else class="agent-config-hint">{{ t('settings.agentUsedByModesEmpty') }}</p>
  </div>
</template>

<style scoped>
.agent-definition-fields {
  display: grid;
  gap: 10px;
  margin: 10px 0;
}

.agent-provenance-box {
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.agent-used-by-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 8px;
}

.agent-used-by-chip {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  gap: 4px;
  min-height: var(--control-height-xs, 26px);
  padding: 3px 10px;
  border-radius: 999px;
  border: 1px solid var(--border-default);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 12px;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color 0.15s ease, border-color 0.15s ease;
}

.agent-used-by-chip:hover {
  background: var(--surface-hover);
  border-color: var(--accent-primary);
}

.agent-used-by-chip:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}

.agent-used-by-chip small {
  color: var(--text-muted);
  font-size: 11px;
}
</style>
