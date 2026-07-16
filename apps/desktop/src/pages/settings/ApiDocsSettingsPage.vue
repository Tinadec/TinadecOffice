<script setup lang="ts">
import { ExternalLink, FileText, RefreshCw } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { UiButton, UiCard } from '@/components/ui'

const { t } = useI18n()
const docsUrl = `${api.gatewayUrl}/docs`
const loading = ref(true)
const available = ref(false)

async function loadDocs() {
  loading.value = true
  available.value = false
  try {
    available.value = (await fetch(docsUrl)).ok
  } catch {
    available.value = false
  } finally {
    loading.value = false
  }
}

function handleLoad() {
  loading.value = false
}

function handleError() {
  available.value = false
  loading.value = false
}

function openExternal() {
  window.open(docsUrl, '_blank')
}

onMounted(loadDocs)
</script>

<template>
  <h1>{{ t('settings.apiDocs') }}</h1>
  <p class="api-docs-description">{{ t('settings.apiDocsDescription') }}</p>

  <p v-if="loading" class="api-docs-status">{{ t('settings.apiDocsLoading') }}</p>
  <iframe
    v-else-if="available"
    class="api-docs-frame"
    :src="docsUrl"
    :title="t('settings.apiDocs')"
    @load="handleLoad"
    @error="handleError"
  />
  <UiCard v-else class="api-docs-fallback">
    <template #content>
      <FileText :size="24" />
      <div>
        <h2>{{ t('settings.apiDocsUnavailable') }}</h2>
        <p>{{ t('settings.apiDocsUnavailableHint') }}</p>
      </div>
      <div class="api-docs-actions">
        <UiButton variant="outline" size="sm" @click="loadDocs">
          <RefreshCw :size="14" />
          <span>{{ t('settings.retry') }}</span>
        </UiButton>
        <UiButton size="sm" @click="openExternal">
          <ExternalLink :size="14" />
          <span>{{ t('settings.apiDocsOpenExternal') }}</span>
        </UiButton>
      </div>
    </template>
  </UiCard>
</template>

<style scoped>
.api-docs-description,
.api-docs-status,
.api-docs-fallback p {
  color: var(--text-muted);
  font-size: 13px;
}

.api-docs-frame {
  width: 100%;
  min-height: 640px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: white;
}

.api-docs-fallback :deep(.ui-card-content) {
  display: grid;
  grid-template-columns: auto 1fr auto;
  align-items: center;
  gap: 16px;
  padding: 20px;
}

.api-docs-fallback h3,
.api-docs-fallback p {
  margin: 0;
}

.api-docs-fallback p {
  margin-top: 4px;
}

.api-docs-actions {
  display: flex;
  gap: 8px;
}

@media (max-width: 640px) {
  .api-docs-fallback :deep(.ui-card-content) {
    grid-template-columns: 1fr;
  }

  .api-docs-actions {
    flex-wrap: wrap;
  }
}
</style>
