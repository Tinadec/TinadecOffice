<script setup lang="ts">
import { onErrorCaptured, ref, watch } from 'vue'
import { AlertTriangle, RefreshCw } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiAlert, UiButton } from '@/components/ui'
import type { SettingsSection } from '@/workbench/settingsController'

const props = defineProps<{ moduleId: SettingsSection }>()
defineSlots<{ default(props: { retryKey: number }): unknown }>()

const { t } = useI18n()
const error = ref<Error | null>(null)
const retryKey = ref(0)

onErrorCaptured((reason) => {
  error.value = reason instanceof Error ? reason : new Error(String(reason))
  return false
})

watch(() => props.moduleId, () => {
  error.value = null
})

function retry(): void {
  error.value = null
  retryKey.value += 1
}
</script>

<template>
  <div class="settings-module-boundary" :data-settings-boundary="moduleId">
    <UiAlert v-if="error" variant="destructive" class="settings-module-error">
      <AlertTriangle />
      <div>
        <strong>{{ t('settings.moduleLoadFailed') }}</strong>
        <p>{{ error.message }}</p>
        <UiButton variant="outline" size="sm" @click="retry">
          <RefreshCw data-icon="inline-start" />
          {{ t('settings.retry') }}
        </UiButton>
      </div>
    </UiAlert>
    <slot v-else :retry-key="retryKey" />
  </div>
</template>

<style scoped>
.settings-module-boundary { min-width: 0; min-height: 100%; }
.settings-module-error { margin: 24px; width: auto; }
.settings-module-error p { margin: 6px 0 12px; color: var(--text-secondary); }
</style>
