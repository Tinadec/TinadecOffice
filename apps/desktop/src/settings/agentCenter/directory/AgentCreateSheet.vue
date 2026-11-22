<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { UiButton, UiInput, UiLabel, UiSelectField, UiSheet } from '@/components/ui'

defineProps<{
  open: boolean
  name: string
  slug: string
  layer: 'operation' | 'execution'
  role: string
  pipelineId: string
  description: string
  busy: boolean
  pipelineOptions: Array<{ value: string; label: string }>
}>()

const emit = defineEmits<{
  'update:open': [open: boolean]
  'update:name': [value: string]
  'update:slug': [value: string]
  'update:layer': [value: 'operation' | 'execution']
  'update:role': [value: string]
  'update:pipelineId': [value: string]
  'update:description': [value: string]
  create: [publishImmediately: boolean]
}>()

const { t } = useI18n()
</script>

<template>
  <UiSheet :open="open" side="right" @update:open="emit('update:open', $event)">
    <div class="ac-sheet-body">
      <h3>{{ t('settings.newAgent') }}</h3>
      <div>
        <UiLabel>{{ t('settings.agentName') }}</UiLabel>
        <UiInput
          :model-value="name"
          :placeholder="t('settings.agentName')"
          @update:model-value="emit('update:name', String($event ?? ''))"
        />
      </div>
      <div>
        <UiLabel>{{ t('settings.agentSlugLabel') }}</UiLabel>
        <UiInput
          :model-value="slug"
          :placeholder="t('settings.agentSlugOptionalPlaceholder')"
          @update:model-value="emit('update:slug', String($event ?? ''))"
        />
      </div>
      <div>
        <UiLabel>{{ t('settings.agentLayer') }}</UiLabel>
        <UiSelectField
          :model-value="layer"
          :options="[
            { value: 'operation', label: t('settings.agentLayerPlanning') },
            { value: 'execution', label: t('settings.agentLayerExecution') },
          ]"
          @update:model-value="emit('update:layer', $event === 'execution' ? 'execution' : 'operation')"
        />
      </div>
      <div>
        <UiLabel>{{ t('settings.agentRole') }}</UiLabel>
        <UiInput
          :model-value="role"
          :placeholder="layer === 'operation' ? 'session_coordinator' : 'code_writer'"
          @update:model-value="emit('update:role', String($event ?? ''))"
        />
      </div>
      <div>
        <UiLabel>{{ t('settings.agentBasePipeline') }}</UiLabel>
        <UiSelectField
          :model-value="pipelineId"
          :options="pipelineOptions"
          @update:model-value="emit('update:pipelineId', String($event ?? ''))"
        />
      </div>
      <div>
        <UiLabel>{{ t('settings.agentDescription') }}</UiLabel>
        <textarea
          :value="description"
          class="settings-textarea"
          rows="2"
          :placeholder="t('settings.agentDescriptionPlaceholder')"
          @input="emit('update:description', ($event.target as HTMLTextAreaElement).value)"
        ></textarea>
      </div>
      <div class="ac-sheet-actions">
        <UiButton variant="outline" @click="emit('update:open', false)">{{ t('common.cancel') }}</UiButton>
        <UiButton variant="outline" :disabled="busy || !name.trim()" @click="emit('create', false)">
          {{ t('settings.saveAgentDraftOnly') }}
        </UiButton>
        <UiButton :disabled="busy || !name.trim()" @click="emit('create', true)">
          {{ t('settings.createAndPublishAgent') }}
        </UiButton>
      </div>
    </div>
  </UiSheet>
</template>
