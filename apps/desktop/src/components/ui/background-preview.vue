/**
 * Background preview component for settings UI
 * Shows real-time preview of background settings
 */

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { BackgroundSettings } from '@/types/background'

const { t } = useI18n()

const props = defineProps<{
  settings: BackgroundSettings
  height?: number
}>()

// Compute preview container style
const containerStyle = computed(() => ({
  height: `${props.height || 200}px`,
  position: 'relative' as const,
  overflow: 'hidden',
  borderRadius: '8px',
  border: '1px solid var(--border-default)',
  background: 'var(--surface-section)',
}))

// Compute background style based on settings
const backgroundStyle = computed(() => {
  if (props.settings.type === 'none') {
    return {}
  }
  
  const baseStyle: Record<string, string> = {
    position: 'absolute',
    top: '0',
    left: '0',
    width: '100%',
    height: '100%',
    opacity: `${props.settings.opacity / 100}`,
    filter: props.settings.blur > 0 ? `blur(${props.settings.blur}px)` : 'none',
    objectFit: props.settings.size,
    objectPosition: props.settings.position,
    backgroundRepeat: props.settings.repeat,
  }
  
  return baseStyle
})

// Compute image background style
const imageStyle = computed(() => {
  if (props.settings.type !== 'image' || !props.settings.source) {
    return {}
  }
  
  return {
    ...backgroundStyle.value,
    backgroundImage: `url('${props.settings.source}')`,
    backgroundSize: props.settings.size,
    backgroundPosition: props.settings.position,
    backgroundRepeat: props.settings.repeat,
  }
})

// Check if source is valid
const hasSource = computed(() => {
  return props.settings.type !== 'none' && props.settings.source
})

// Compute placeholder text
const placeholderText = computed(() => {
  switch (props.settings.type) {
    case 'none':
      return t('settings.bgPreviewNone')
    case 'image':
      return hasSource.value ? '' : t('settings.bgPreviewImage')
    case 'video':
      return hasSource.value ? '' : t('settings.bgPreviewVideo')
    case 'html':
      return hasSource.value ? '' : t('settings.bgPreviewHtml')
    default:
      return ''
  }
})
</script>

<template>
  <div class="background-preview" :style="containerStyle">
    <!-- No background -->
    <div
      v-if="settings.type === 'none'"
      class="preview-placeholder"
    >
      <span>{{ placeholderText }}</span>
    </div>
    
    <!-- Image background -->
    <div
      v-else-if="settings.type === 'image'"
      class="preview-image"
      :style="imageStyle"
    >
      <div v-if="!hasSource" class="preview-placeholder">
        <span>{{ placeholderText }}</span>
      </div>
    </div>
    
    <!-- Video background -->
    <video
      v-else-if="settings.type === 'video' && hasSource"
      class="preview-video"
      :style="backgroundStyle"
      :src="settings.source"
      autoplay
      loop
      muted
    />
    <div
      v-else-if="settings.type === 'video' && !hasSource"
      class="preview-placeholder"
    >
      <span>{{ placeholderText }}</span>
    </div>
    
    <!-- HTML background -->
    <div
      v-else-if="settings.type === 'html' && hasSource"
      class="preview-html"
      :style="backgroundStyle"
      v-html="settings.source"
    />
    <div
      v-else-if="settings.type === 'html' && !hasSource"
      class="preview-placeholder"
    >
      <span>{{ placeholderText }}</span>
    </div>
    
    <!-- Overlay info -->
    <div class="preview-overlay">
      <span class="preview-type">{{ settings.type }}</span>
      <span v-if="settings.opacity < 100" class="preview-opacity">
        {{ settings.opacity }}%
      </span>
      <span v-if="settings.blur > 0" class="preview-blur">
        {{ settings.blur }}px
      </span>
    </div>
  </div>
</template>

<style scoped>
.background-preview {
  position: relative;
  overflow: hidden;
}

.preview-placeholder {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 100%;
  height: 100%;
  color: var(--text-muted);
  font-size: 14px;
  background: 
    repeating-linear-gradient(
      45deg,
      transparent,
      transparent 10px,
      var(--bg-tertiary) 10px,
      var(--bg-tertiary) 20px
    );
}

.preview-image {
  width: 100%;
  height: 100%;
  background-size: cover;
  background-position: center;
}

.preview-video {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.preview-html {
  width: 100%;
  height: 100%;
  overflow: hidden;
  pointer-events: none;
}

.preview-overlay {
  position: absolute;
  bottom: 8px;
  right: 8px;
  display: flex;
  gap: 4px;
  align-items: center;
}

.preview-type,
.preview-opacity,
.preview-blur {
  padding: 2px 6px;
  font-size: 11px;
  font-weight: 500;
  border-radius: 4px;
  background: rgba(0, 0, 0, 0.6);
  color: #fff;
}

.preview-type {
  text-transform: uppercase;
  letter-spacing: 0.5px;
}
</style>
