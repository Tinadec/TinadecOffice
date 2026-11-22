<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronLeft, ChevronRight, Download, RotateCcw, X, ZoomIn, ZoomOut } from '@lucide/vue'

export interface LightboxImageItem {
  src: string
  name: string
  downloadUrl?: string
}

const props = withDefaults(defineProps<{
  open: boolean
  images: readonly LightboxImageItem[]
  initialIndex?: number
}>(), {
  initialIndex: 0,
})

const emit = defineEmits<{ close: [] }>()
const { t } = useI18n()

const MIN_SCALE = 0.5
const MAX_SCALE = 4
const SCALE_STEP = 0.25

const activeIndex = ref(0)
const scale = ref(1)
const closeButtonRef = ref<HTMLButtonElement | null>(null)

const clampedInitialIndex = computed(() => {
  if (!props.images.length) return 0
  return Math.max(0, Math.min(props.initialIndex, props.images.length - 1))
})

const currentImage = computed(() => props.images[activeIndex.value] ?? null)
const hasMultiple = computed(() => props.images.length > 1)
const zoomPercent = computed(() => `${Math.round(scale.value * 100)}%`)

function clampScale(next: number): number {
  return Math.round(Math.max(MIN_SCALE, Math.min(MAX_SCALE, next)) * 100) / 100
}

function zoomIn() {
  scale.value = clampScale(scale.value + SCALE_STEP)
}

function zoomOut() {
  scale.value = clampScale(scale.value - SCALE_STEP)
}

function resetZoom() {
  scale.value = 1
}

function toggleDoubleZoom() {
  scale.value = scale.value > 1 ? 1 : 2
}

function showPrevious() {
  if (!hasMultiple.value) return
  activeIndex.value = (activeIndex.value - 1 + props.images.length) % props.images.length
  scale.value = 1
}

function showNext() {
  if (!hasMultiple.value) return
  activeIndex.value = (activeIndex.value + 1) % props.images.length
  scale.value = 1
}

function onWheel(event: WheelEvent) {
  if (!props.open || !currentImage.value) return
  event.preventDefault()
  const delta = event.deltaY < 0 ? SCALE_STEP : -SCALE_STEP
  scale.value = clampScale(scale.value + delta)
}

function onKeydown(event: KeyboardEvent) {
  if (!props.open || !currentImage.value) return
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    emit('close')
    return
  }
  if (event.key === 'ArrowLeft') {
    event.preventDefault()
    showPrevious()
    return
  }
  if (event.key === 'ArrowRight') {
    event.preventDefault()
    showNext()
    return
  }
  if (event.key === '+' || event.key === '=') {
    event.preventDefault()
    zoomIn()
    return
  }
  if (event.key === '-' || event.key === '_') {
    event.preventDefault()
    zoomOut()
    return
  }
  if (event.key === '0') {
    event.preventDefault()
    resetZoom()
  }
}

function bindKeys(active: boolean) {
  document.removeEventListener('keydown', onKeydown)
  if (active) document.addEventListener('keydown', onKeydown)
}

watch(() => [props.open, props.initialIndex, props.images.length] as const, ([open]) => {
  if (open) {
    activeIndex.value = clampedInitialIndex.value
    scale.value = 1
    bindKeys(true)
    void nextTick(() => closeButtonRef.value?.focus({ preventScroll: true }))
  } else {
    bindKeys(false)
  }
}, { immediate: true })

onUnmounted(() => {
  bindKeys(false)
})
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open && currentImage"
      class="image-lightbox-overlay"
      role="dialog"
      aria-modal="true"
      :aria-label="currentImage.name"
      data-testid="image-lightbox"
      @click.self="emit('close')"
      @wheel="onWheel"
    >
      <div class="image-lightbox-topbar">
        <div class="image-lightbox-meta">
          <span class="image-lightbox-name" :title="currentImage.name">{{ currentImage.name }}</span>
          <span v-if="hasMultiple" class="image-lightbox-counter">
            {{ t('chat.lightbox.counter', { current: activeIndex + 1, total: images.length }) }}
          </span>
        </div>
        <div class="image-lightbox-controls">
          <button
            type="button"
            class="image-lightbox-btn"
            :disabled="scale <= MIN_SCALE"
            :aria-label="t('chat.lightbox.zoomOut')"
            :title="t('chat.lightbox.zoomOut')"
            data-testid="lightbox-zoom-out"
            @click="zoomOut"
          >
            <ZoomOut :size="16" aria-hidden="true" />
          </button>
          <button
            type="button"
            class="image-lightbox-btn is-label"
            :disabled="scale === 1"
            :aria-label="t('chat.lightbox.resetZoom')"
            :title="t('chat.lightbox.resetZoom')"
            data-testid="lightbox-zoom-reset"
            @click="resetZoom"
          >
            <RotateCcw :size="13" aria-hidden="true" />
            <span>{{ zoomPercent }}</span>
          </button>
          <button
            type="button"
            class="image-lightbox-btn"
            :disabled="scale >= MAX_SCALE"
            :aria-label="t('chat.lightbox.zoomIn')"
            :title="t('chat.lightbox.zoomIn')"
            data-testid="lightbox-zoom-in"
            @click="zoomIn"
          >
            <ZoomIn :size="16" aria-hidden="true" />
          </button>
          <a
            v-if="currentImage.downloadUrl"
            class="image-lightbox-btn"
            :href="currentImage.downloadUrl"
            :download="currentImage.name"
            :aria-label="t('chat.lightbox.download')"
            :title="t('chat.lightbox.download')"
            data-testid="lightbox-download"
          >
            <Download :size="16" aria-hidden="true" />
          </a>
          <button
            ref="closeButtonRef"
            type="button"
            class="image-lightbox-btn"
            :aria-label="t('chat.lightbox.close')"
            :title="t('chat.lightbox.close')"
            data-testid="lightbox-close"
            @click="emit('close')"
          >
            <X :size="16" aria-hidden="true" />
          </button>
        </div>
      </div>

      <div class="image-lightbox-stage" @click.self="emit('close')">
        <button
          v-if="hasMultiple"
          type="button"
          class="image-lightbox-nav is-prev"
          :aria-label="t('chat.lightbox.previous')"
          :title="t('chat.lightbox.previous')"
          data-testid="lightbox-prev"
          @click="showPrevious"
        >
          <ChevronLeft :size="22" aria-hidden="true" />
        </button>

        <img
          :src="currentImage.src"
          :alt="currentImage.name"
          class="image-lightbox-img"
          :style="{ transform: `scale(${scale})` }"
          data-testid="lightbox-image"
          @dblclick="toggleDoubleZoom"
        />

        <button
          v-if="hasMultiple"
          type="button"
          class="image-lightbox-nav is-next"
          :aria-label="t('chat.lightbox.next')"
          :title="t('chat.lightbox.next')"
          data-testid="lightbox-next"
          @click="showNext"
        >
          <ChevronRight :size="22" aria-hidden="true" />
        </button>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.image-lightbox-overlay {
  position: fixed;
  inset: 0;
  z-index: 10020;
  display: flex;
  flex-direction: column;
  background: rgba(10, 12, 16, 0.86);
  backdrop-filter: blur(8px);
  color: rgba(255, 255, 255, 0.94);
  user-select: none;
}

.image-lightbox-topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 12px 16px;
  background: linear-gradient(to bottom, rgba(10, 12, 16, 0.68), transparent);
}

.image-lightbox-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.image-lightbox-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13px;
  font-weight: 500;
}

.image-lightbox-counter {
  flex-shrink: 0;
  font-size: 12px;
  color: rgba(255, 255, 255, 0.68);
  font-variant-numeric: tabular-nums;
}

.image-lightbox-controls {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.image-lightbox-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  min-width: 32px;
  height: 32px;
  padding: 0 8px;
  border: 1px solid rgba(255, 255, 255, 0.14);
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.08);
  color: rgba(255, 255, 255, 0.92);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  text-decoration: none;
  cursor: pointer;
  transition: background-color 120ms ease, border-color 120ms ease;
}

.image-lightbox-btn:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.16);
  border-color: rgba(255, 255, 255, 0.28);
}

.image-lightbox-btn:disabled {
  opacity: 0.45;
  cursor: default;
}

.image-lightbox-stage {
  position: relative;
  flex: 1;
  min-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px 64px 28px;
  overflow: auto;
}

.image-lightbox-img {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
  border-radius: 8px;
  box-shadow: 0 18px 48px rgba(0, 0, 0, 0.45);
  transition: transform 140ms cubic-bezier(0.2, 0, 0, 1);
  transform-origin: center center;
}

.image-lightbox-nav {
  position: absolute;
  top: 50%;
  transform: translateY(-50%);
  z-index: 2;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 38px;
  height: 38px;
  border: 1px solid rgba(255, 255, 255, 0.16);
  border-radius: 999px;
  background: rgba(16, 18, 24, 0.72);
  color: rgba(255, 255, 255, 0.94);
  cursor: pointer;
  transition: background-color 120ms ease;
}

.image-lightbox-nav:hover {
  background: rgba(34, 38, 48, 0.92);
}

.image-lightbox-nav.is-prev {
  left: 16px;
}

.image-lightbox-nav.is-next {
  right: 16px;
}
</style>
