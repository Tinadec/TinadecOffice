<script setup lang="ts">
import { ArrowLeft, ArrowRight, Globe, RefreshCw, ExternalLink, Home as HomeIcon } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const props = defineProps<{
  initialUrl?: string
}>()

const emit = defineEmits<{
  'update:url': [url: string]
}>()

const gatewayDocsUrl = (window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730') + '/docs'
const VITE_DEV_URL = 'http://localhost:5173'

const urlInput = ref(props.initialUrl ?? '')
const currentUrl = ref(props.initialUrl ?? '')
const history = ref<string[]>([])
const historyIndex = ref(-1)
const loading = ref(false)
const loadError = ref<string | null>(null)
const iframeKey = ref(0)

const canGoBack = computed(() => historyIndex.value > 0)
const canGoForward = computed(() => historyIndex.value < history.value.length - 1)

const quickLinks = computed(() => [
  { label: 'Gateway API', url: gatewayDocsUrl },
  { label: 'Dev Server', url: VITE_DEV_URL },
  { label: 'Core Health', url: 'http://localhost:48731/api/v1/health' },
])

function normalizeUrl(input: string): string {
  const trimmed = input.trim()
  if (!trimmed) return ''
  if (/^https?:\/\//i.test(trimmed)) return trimmed
  if (/^localhost/i.test(trimmed)) return `http://${trimmed}`
  if (/^\d+/.test(trimmed)) return `http://localhost:${trimmed}`
  return `https://${trimmed}`
}

function navigate(url?: string) {
  const target = normalizeUrl(url ?? urlInput.value)
  if (!target) return

  loadError.value = null

  // Push to history
  if (historyIndex.value < history.value.length - 1) {
    history.value = history.value.slice(0, historyIndex.value + 1)
  }
  history.value.push(target)
  historyIndex.value = history.value.length - 1

  currentUrl.value = target
  urlInput.value = target
  loading.value = true
  iframeKey.value++
  emit('update:url', target)
}

function goBack() {
  if (!canGoBack.value) return
  historyIndex.value--
  const url = history.value[historyIndex.value]
  currentUrl.value = url
  urlInput.value = url
  loading.value = true
  iframeKey.value++
  emit('update:url', url)
}

function goForward() {
  if (!canGoForward.value) return
  historyIndex.value++
  const url = history.value[historyIndex.value]
  currentUrl.value = url
  urlInput.value = url
  loading.value = true
  iframeKey.value++
  emit('update:url', url)
}

function refresh() {
  if (!currentUrl.value) return
  loading.value = true
  loadError.value = null
  iframeKey.value++
}

function onLoad() {
  loading.value = false
}

function onError() {
  loading.value = false
  loadError.value = t('context.previewLoadError')
}

function openExternal() {
  if (!currentUrl.value) return
  window.open(currentUrl.value, '_blank', 'noopener')
}

function goHome() {
  urlInput.value = ''
  currentUrl.value = ''
  loadError.value = null
  loading.value = false
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter') {
    event.preventDefault()
    navigate()
  }
}

// Initialize with default URL if provided
watch(
  () => props.initialUrl,
  (url) => {
    if (url && !currentUrl.value) {
      navigate(url)
    }
  },
  { immediate: true },
)
</script>

<template>
  <section class="panel preview-browser-panel">
    <!-- Toolbar -->
    <div class="preview-toolbar">
      <button
        class="preview-nav-btn"
        :disabled="!canGoBack"
        :title="t('context.previewBack')"
        @click="goBack"
      >
        <ArrowLeft :size="15" />
      </button>
      <button
        class="preview-nav-btn preview-nav-secondary"
        :disabled="!canGoForward"
        :title="t('context.previewForward')"
        @click="goForward"
      >
        <ArrowRight :size="15" />
      </button>
      <button
        class="preview-nav-btn"
        :title="t('context.previewRefresh')"
        @click="refresh"
      >
        <RefreshCw :size="15" :class="{ spinning: loading }" />
      </button>
      <button
        class="preview-nav-btn preview-nav-secondary"
        :title="t('context.previewHome')"
        @click="goHome"
      >
        <HomeIcon :size="15" />
      </button>

      <div class="preview-url-bar">
        <Globe :size="13" class="preview-url-icon" />
        <input
          v-model="urlInput"
          class="preview-url-input"
          :placeholder="t('context.previewUrlPlaceholder')"
          @keydown="onKeydown"
        />
      </div>

      <button
        class="preview-nav-btn"
        :title="t('context.previewOpenExternal')"
        @click="openExternal"
      >
        <ExternalLink :size="15" />
      </button>
    </div>

    <!-- Content area -->
    <div class="preview-content">
      <!-- Start page / quick links -->
      <div v-if="!currentUrl" class="preview-start">
        <Globe :size="40" class="preview-start-icon" />
        <h3>{{ t('context.previewStartTitle') }}</h3>
        <p>{{ t('context.previewStartHint') }}</p>
        <div class="preview-quick-links">
          <button
            v-for="link in quickLinks"
            :key="link.url"
            class="preview-quick-link"
            @click="navigate(link.url)"
          >
            <Globe :size="14" />
            <span>{{ link.label }}</span>
            <small>{{ link.url }}</small>
          </button>
        </div>
      </div>

      <!-- Loading overlay -->
      <div v-if="loading && currentUrl" class="preview-loading">
        <RefreshCw :size="20" class="spinning" />
        <span>{{ t('context.previewLoading') }}</span>
      </div>

      <!-- Error state -->
      <div v-if="loadError" class="preview-error">
        <span>{{ loadError }}</span>
        <p>{{ t('context.previewErrorHint') }}</p>
      </div>

      <!-- Iframe -->
      <iframe
        v-if="currentUrl"
        :key="iframeKey"
        :src="currentUrl"
        class="preview-iframe"
        sandbox="allow-scripts allow-same-origin allow-forms allow-popups"
        referrerpolicy="no-referrer"
        @load="onLoad"
        @error="onError"
      />
    </div>
  </section>
</template>

<style scoped>
.preview-browser-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

.preview-toolbar {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 6px 8px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border-muted);
  flex-shrink: 0;
}

.preview-nav-btn {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
  flex-shrink: 0;
}

.preview-nav-btn:hover:not(:disabled) {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.preview-nav-btn:disabled {
  opacity: 0.35;
  cursor: not-allowed;
}

.preview-url-bar {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 10px;
  height: 28px;
  background: var(--bg-tertiary);
  border: 1px solid var(--border-muted);
  border-radius: 14px;
  min-width: 0;
}

.preview-url-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.preview-url-input {
  flex: 1;
  min-width: 0;
  background: transparent;
  border: none;
  outline: none;
  font-size: 12px;
  color: var(--text-primary);
}

.preview-url-input::placeholder {
  color: var(--text-muted);
}

.preview-content {
  flex: 1;
  position: relative;
  min-height: 0;
  background: var(--bg-primary);
  overflow: hidden;
}

.preview-start {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 32px 20px;
  height: 100%;
  overflow-y: auto;
}

.preview-start-icon {
  color: var(--text-muted);
  opacity: 0.5;
}

.preview-start h3 {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
  color: var(--text-primary);
}

.preview-start p {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
  text-align: center;
}

.preview-quick-links {
  display: flex;
  flex-direction: column;
  gap: 6px;
  width: 100%;
  max-width: 280px;
  margin-top: 8px;
}

.preview-quick-link {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  background: var(--bg-tertiary);
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  cursor: pointer;
  transition: all 0.15s;
  text-align: left;
}

.preview-quick-link:hover {
  background: var(--bg-hover);
  border-color: var(--accent-primary);
}

.preview-quick-link span {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.preview-quick-link small {
  margin-left: auto;
  font-size: 10px;
  color: var(--text-muted);
  font-family: monospace;
}

.preview-loading {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 8px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border-muted);
  font-size: 12px;
  color: var(--text-secondary);
  z-index: 5;
}

.preview-error {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 20px;
  text-align: center;
}

.preview-error span {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-error, #f85149);
}

.preview-error p {
  font-size: 12px;
  color: var(--text-muted);
  margin: 0;
  max-width: 240px;
}

.preview-iframe {
  width: 100%;
  height: 100%;
  border: none;
  background: #fff;
}

.spinning {
  animation: spin 0.8s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
