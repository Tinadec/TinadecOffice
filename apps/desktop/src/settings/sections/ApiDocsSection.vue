<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { api } from '@/api'

/** API docs section: embeds the Gateway OpenAPI viewer (D7.2). */
const { t } = useI18n()
</script>

<template>
  <div>
    <h2>{{ t('settings.apiDocs') }}</h2>
    <iframe class="api-docs-frame" :src="`${api.gatewayUrl}/docs`" data-testid="api-docs-frame" />
  </div>
</template>
