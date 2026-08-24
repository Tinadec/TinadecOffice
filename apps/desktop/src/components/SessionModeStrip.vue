<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { Settings } from '@lucide/vue'
import { UiInput, UiLabel } from '@/components/ui'
import { api, type AgentModeTopologyDto } from '@/api'

const { t } = useI18n()

const props = defineProps<{
  modelValue: string | null
  meetingModel: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string | null]
  'update:meetingModel': [value: string]
}>()

const modes = ref<AgentModeTopologyDto[]>([])
const loading = ref(false)

async function loadModes() {
  loading.value = true
  try {
    const list = await api.listAgentModeTopologies()
    modes.value = Array.isArray(list) ? (list as AgentModeTopologyDto[]) : []
    if (!props.modelValue && modes.value[0]) emit('update:modelValue', modes.value[0].id)
  } catch { /* gateway offline */ }
  finally { loading.value = false }
}

onMounted(() => { void loadModes() })

function onModeChange(e: Event) {
  const v = (e.target as HTMLSelectElement).value
  emit('update:modelValue', v || null)
}
</script>

<template>
  <div class="session-mode-strip">
    <div class="session-mode-field">
      <UiLabel class="session-mode-label">
        <Settings :size="11" />
        <span>{{ t('chat.modeVersion') }}</span>
      </UiLabel>
      <select
        :value="modelValue ?? ''"
        class="session-mode-select"
        :disabled="loading && modes.length === 0"
        @change="onModeChange"
      >
        <option value="">{{ t('chat.followDefault') }}</option>
        <option v-for="m in modes" :key="m.id" :value="m.id">
          {{ m.display_name }}{{ m.status === 'published' ? ' · 默认' : '' }}
        </option>
      </select>
    </div>
    <div class="session-mode-field session-mode-field--model">
      <UiLabel class="session-mode-label">{{ t('chat.meetingModel') }}</UiLabel>
      <UiInput
        :model-value="meetingModel"
        :placeholder="t('chat.meetingModelPlaceholder')"
        class="session-mode-input"
        @update:model-value="emit('update:meetingModel', $event)"
      />
    </div>
  </div>
</template>

<style scoped>
.session-mode-strip {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  padding: 6px 2px 8px;
  /* transparent strip above composer — no card, no blur */
}
.session-mode-field {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.session-mode-label {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: var(--text-muted);
  white-space: nowrap;
}
.session-mode-select {
  height: 28px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  padding: 0 8px;
  font-size: 12px;
  background: var(--surface-raised);
  min-width: 160px;
  max-width: 220px;
  color: inherit;
}
.session-mode-field--model {
  flex: 0 1 200px;
}
.session-mode-input {
  height: 28px;
  font-size: 12px;
}
.session-mode-input :deep(input) {
  height: 28px;
  font-size: 12px;
}
</style>
