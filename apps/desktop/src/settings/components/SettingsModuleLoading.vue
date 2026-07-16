<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { UiSkeleton } from '@/components/ui'

const { t } = useI18n()
</script>

<template>
  <div class="flex flex-col gap-3 p-4" role="status" :aria-label="t('settings.moduleLoading')">
    <UiSkeleton class="h-6 w-48" />
    <UiSkeleton class="h-4 w-full" />
    <UiSkeleton class="h-4 w-3/4" />
  </div>
</template>
