<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'
import type { SettingsId } from '../types'

defineProps<{
  sectionId: SettingsId
}>()

const emit = defineEmits<{
  retry: []
}>()

const { t } = useI18n()
</script>

<template>
  <div role="alert" class="flex flex-col items-center justify-center gap-3 p-8 text-center">
    <p class="text-sm text-muted-foreground">
      {{ t('settings.moduleError') }}
    </p>
    <UiButton
      variant="outline"
      size="sm"
      :aria-label="t('settings.retryModule')"
      @click="emit('retry')"
    >
      {{ t('settings.retryModule') }}
    </UiButton>
  </div>
</template>
