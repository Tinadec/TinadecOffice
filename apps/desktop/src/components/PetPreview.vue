<script setup lang="ts">
import { LoaderCircle, PawPrint, RefreshCw } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiButton, UiSkeleton } from '@/components/ui'

const { t } = useI18n()

const props = withDefaults(defineProps<{
  src?: string
  alt: string
  loading?: 'eager' | 'lazy'
}>(), {
  src: '',
  loading: 'lazy',
})

const loaded = ref(false)
const failed = ref(false)
const shouldLoad = ref(props.loading === 'eager')
const rootRef = ref<HTMLElement | null>(null)
const retryVersion = ref(0)
let timeout: ReturnType<typeof setTimeout> | null = null
let observer: IntersectionObserver | null = null

const actualSrc = computed(() => {
  if (!props.src || props.src.startsWith('data:')) return props.src
  const separator = props.src.includes('?') ? '&' : '?'
  return `${props.src}${separator}retry=${retryVersion.value}`
})

function clearLoadTimeout() {
  if (timeout) clearTimeout(timeout)
  timeout = null
}

function beginLoading() {
  clearLoadTimeout()
  loaded.value = false
  failed.value = !props.src
  if (!props.src) return
  timeout = setTimeout(() => {
    failed.value = true
  }, 20_000)
}

function markLoaded() {
  clearLoadTimeout()
  loaded.value = true
  failed.value = false
}

function markFailed() {
  clearLoadTimeout()
  failed.value = true
}

function retry() {
  shouldLoad.value = true
  retryVersion.value += 1
  beginLoading()
}

watch(() => props.src, () => {
  retryVersion.value = 0
  loaded.value = false
  failed.value = false
  if (shouldLoad.value) beginLoading()
}, { immediate: true })

onMounted(() => {
  if (shouldLoad.value || !rootRef.value) return
  observer = new IntersectionObserver((entries) => {
    if (!entries.some((entry) => entry.isIntersecting)) return
    shouldLoad.value = true
    observer?.disconnect()
    beginLoading()
  }, { rootMargin: '320px 0px' })
  observer.observe(rootRef.value)
})

onBeforeUnmount(() => {
  observer?.disconnect()
  clearLoadTimeout()
})
</script>

<template>
  <div ref="rootRef" class="pet-preview-loader">
    <template v-if="failed">
      <PawPrint :size="28" class="pet-preview-placeholder" />
      <UiButton v-if="src" variant="ghost" size="icon" class="pet-preview-retry" :title="t('settings.retryPetPreview')" @click="retry">
        <RefreshCw :size="15" />
      </UiButton>
    </template>
    <template v-else>
      <UiSkeleton v-if="!loaded" class="pet-preview-skeleton" />
      <LoaderCircle v-if="!loaded" :size="18" class="pet-preview-spinner" />
      <div v-if="shouldLoad" class="pet-preview-frame" :class="{ ready: loaded }">
        <img
          :key="retryVersion"
          :src="actualSrc"
          :alt="alt"
          :loading="loading"
          draggable="false"
          @load="markLoaded"
          @error="markFailed"
        />
      </div>
    </template>
  </div>
</template>

<style scoped>
.pet-preview-loader {
  position: relative;
  display: grid;
  width: 100%;
  height: 100%;
  place-items: center;
  overflow: hidden;
}

.pet-preview-skeleton {
  position: absolute;
  inset: 12px;
}

.pet-preview-spinner {
  position: absolute;
  z-index: 1;
  color: var(--text-muted);
  animation: pet-preview-spin 0.9s linear infinite;
}

.pet-preview-frame {
  width: 92px;
  aspect-ratio: 192 / 208;
  overflow: hidden;
  opacity: 0;
  filter: drop-shadow(0 8px 16px rgb(0 0 0 / 22%));
  transition: opacity 140ms ease;
}

.pet-preview-frame.ready {
  opacity: 1;
}

.pet-preview-frame img {
  display: block;
  width: 800%;
  max-width: none;
  height: auto;
  user-select: none;
}

.pet-preview-placeholder {
  color: var(--text-muted);
}

.pet-preview-retry {
  position: absolute;
  right: 8px;
  bottom: 8px;
}

@keyframes pet-preview-spin {
  to { transform: rotate(360deg); }
}
</style>
