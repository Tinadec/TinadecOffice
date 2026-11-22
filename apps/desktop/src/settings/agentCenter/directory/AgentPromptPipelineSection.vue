<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { Info } from '@lucide/vue'
import type { AgentVersionDto, AgentViewDto } from '@/api'
import type { ErrorState } from '@/composables/useErrorState'
import { UiBadge, UiButton, UiLabel, UiSelectField, UiSkeleton } from '@/components/ui'

defineProps<{
  configuringAgent: AgentViewDto
  configuringIsReadOnly: boolean
  editBasePromptPipelineId: string
  editSystemPrompt: string
  pipelineOptions: Array<{ value: string; label: string }>
  versionHistory: AgentVersionDto[]
  versionHistoryError: ErrorState | null
  versionsLoading: boolean
}>()

const emit = defineEmits<{
  'update:editBasePromptPipelineId': [value: string]
  'update:editSystemPrompt': [value: string]
  'reload-versions': []
}>()

const { t, locale } = useI18n()
</script>

<template>
  <div class="agent-config-section" data-testid="agent-section-prompt-pipeline">
    <div class="agent-config-section-title">{{ t('settings.agentSectionPromptPipeline') }}</div>

    <div class="settings-field">
      <UiLabel>{{ t('settings.agentBasePipeline') }}</UiLabel>
      <UiSelectField
        :model-value="editBasePromptPipelineId"
        data-testid="agent-edit-pipeline"
        :disabled="configuringIsReadOnly"
        :aria-label="t('settings.agentBasePipeline')"
        :options="pipelineOptions"
        @update:model-value="emit('update:editBasePromptPipelineId', String($event ?? ''))"
      />
    </div>

    <div class="agent-config-section-title secondary">{{ t('settings.agentSystemPrompt') }}</div>
    <p class="agent-config-hint">{{ t('settings.agentSystemPromptOverrideHint') }}</p>
    <div class="settings-field">
      <textarea
        :value="editSystemPrompt"
        class="settings-textarea prompt-editor"
        rows="6"
        :disabled="configuringIsReadOnly"
        :placeholder="t('settings.agentSystemPromptPlaceholder')"
        @input="emit('update:editSystemPrompt', ($event.target as HTMLTextAreaElement).value)"
      ></textarea>
    </div>

    <div class="agent-config-section-title secondary">
      {{ t('settings.agentVersionHistory') }}
      <UiBadge variant="outline">{{ versionHistory.length }}</UiBadge>
    </div>
    <p class="agent-config-hint">{{ t('settings.agentVersionHistoryHint') }}</p>
    <div v-if="versionsLoading" class="center-loading-state" aria-live="polite">
      <UiSkeleton v-for="index in 2" :key="index" class="center-loading-line agent-version-loading-line" />
    </div>
    <div v-else-if="versionHistoryError" class="center-message" role="alert" data-testid="agent-version-read-failure">
      <Info :size="16" aria-hidden="true" />
      <div class="center-message-content">
        <strong>{{ t('settings.centerLoadFailed') }}</strong>
        <span>{{ versionHistoryError.message }}</span>
      </div>
      <UiButton variant="outline" size="sm" @click="emit('reload-versions')">{{ t('settings.retry') }}</UiButton>
    </div>
    <div v-else-if="versionHistory.length > 0" class="agent-version-list">
      <div
        v-for="version in [...versionHistory].reverse()"
        :key="version.id"
        class="center-list-row agent-version-row"
        :class="{ latest: version.version === configuringAgent.version }"
      >
        <strong>v{{ version.version }}</strong>
        <span class="agent-version-hash">{{ (version as AgentVersionDto & { content_hash?: string }).content_hash?.slice(0, 8) ?? '—' }}</span>
        <time>{{ new Date(version.created_at).toLocaleString(locale) }}</time>
        <UiBadge v-if="version.version === configuringAgent.version" variant="default">{{ t('settings.agentVersionCurrent') }}</UiBadge>
      </div>
    </div>
    <p v-else class="quiet">{{ t('settings.noVersionsPublished') }}</p>
  </div>
</template>
