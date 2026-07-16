/**
 * Panel style control component — simplified to three material effects.
 *
 * - Opaque:      Solid background, no parameters
 * - Translucent: Adjustable opacity (0-100%)
 * - Blur:        Adjustable opacity (0-100%) and blur strength (0-20px)
 */

<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { PanelEffect, PanelStyleSettings } from '@/types/background'

const { t } = useI18n()

const props = defineProps<{
  label: string
  settings: PanelStyleSettings
}>()

const emit = defineEmits<{
  update: [settings: Partial<PanelStyleSettings>]
}>()

// Effect options — only three
const effectOptions = computed(() => [
  { value: 'opaque' as PanelEffect, label: t('settings.effectOpaque') },
  { value: 'translucent' as PanelEffect, label: t('settings.effectTranslucent') },
  { value: 'blur' as PanelEffect, label: t('settings.effectBlur') },
])

// Whether to show the opacity slider
const showOpacity = computed(() =>
  props.settings.effect === 'translucent' || props.settings.effect === 'blur'
)

// Whether to show the blur slider
const showBlur = computed(() => props.settings.effect === 'blur')

// Preview style — mirrors computePanelStyle logic for real-time feedback
const previewStyle = computed(() => {
  const style: Record<string, string> = {
    height: '56px',
    borderRadius: '8px',
    transition: 'all 0.2s ease',
  }
  const alpha = props.settings.opacity / 100

  switch (props.settings.effect) {
    case 'opaque':
      style.background = 'var(--bg-primary)'
      break
    case 'translucent':
      style.backgroundColor = `rgba(var(--bg-primary-rgb, 10, 14, 20), ${alpha})`
      break
    case 'blur':
      style.backdropFilter = `blur(${props.settings.blur}px)`
      style.WebkitBackdropFilter = `blur(${props.settings.blur}px)`
      style.backgroundColor = `rgba(var(--bg-primary-rgb, 10, 14, 20), ${alpha})`
      break
  }

  return style
})

function setEffect(effect: PanelEffect): void {
  emit('update', { effect })
}

function setOpacity(event: Event): void {
  emit('update', { opacity: parseInt((event.target as HTMLInputElement).value) })
}

function setBlur(event: Event): void {
  emit('update', { blur: parseInt((event.target as HTMLInputElement).value) })
}

function reset(): void {
  emit('update', { effect: 'opaque', opacity: 80, blur: 8 })
}
</script>

<template>
  <div class="panel-style-control">
    <!-- Header -->
    <div class="control-header">
      <span class="control-label">{{ label }}</span>
      <button class="control-reset" :title="t('settings.reset')" @click="reset">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
          <path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" />
          <path d="M3 3v5h5" />
        </svg>
      </button>
    </div>

    <!-- Effect selector — three buttons -->
    <div class="effect-selector">
      <button
        v-for="option in effectOptions"
        :key="option.value"
        :class="['effect-option', { active: settings.effect === option.value }]"
        @click="setEffect(option.value)"
      >
        {{ option.label }}
      </button>
    </div>

    <!-- Opacity slider (translucent + blur) -->
    <div v-if="showOpacity" class="slider-row">
      <label class="slider-label">{{ t('settings.opacity') }}</label>
      <input
        type="range"
        min="0"
        max="100"
        :value="settings.opacity"
        class="slider-input"
        @input="setOpacity"
      />
      <span class="slider-value">{{ settings.opacity }}%</span>
    </div>

    <!-- Blur slider (blur only) -->
    <div v-if="showBlur" class="slider-row">
      <label class="slider-label">{{ t('settings.blur') }}</label>
      <input
        type="range"
        min="0"
        max="20"
        :value="settings.blur"
        class="slider-input"
        @input="setBlur"
      />
      <span class="slider-value">{{ settings.blur }}px</span>
    </div>

    <!-- Preview -->
    <div class="preview-container">
      <div class="preview-pattern" />
      <div :style="previewStyle" class="preview-panel">
        <span class="preview-text">{{ t('settings.preview') }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.panel-style-control {
  padding: 12px;
  border: 1px solid transparent;
  border-radius: 10px;
  background: var(--bg-secondary);
  box-shadow: var(--shadow-subtle);
}

.control-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 10px;
}

.control-label {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.control-reset {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  color: var(--text-muted);
  background: transparent;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  transition: color 0.1s, background 0.1s;
}

.control-reset:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.effect-selector {
  display: flex;
  gap: 4px;
  margin-bottom: 10px;
}

.effect-option {
  flex: 1;
  padding: 6px 4px;
  font-size: 11px;
  font-weight: 500;
  color: var(--text-secondary);
  background: var(--bg-tertiary);
  border: 1px solid transparent;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.1s;
}

.effect-option:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.effect-option.active {
  color: var(--accent-primary);
  background: var(--accent-soft);
  border-color: var(--accent-primary);
}

.slider-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.slider-label {
  min-width: 50px;
  font-size: 12px;
  color: var(--text-secondary);
}

.slider-input {
  flex: 1;
  height: 4px;
  -webkit-appearance: none;
  appearance: none;
  background: var(--bg-tertiary);
  border-radius: 2px;
  outline: none;
}

.slider-input::-webkit-slider-thumb {
  -webkit-appearance: none;
  width: 14px;
  height: 14px;
  background: var(--accent-primary);
  border-radius: 50%;
  cursor: pointer;
  transition: transform 0.1s;
}

.slider-input::-webkit-slider-thumb:hover {
  transform: scale(1.2);
}

.slider-value {
  min-width: 36px;
  font-size: 12px;
  color: var(--text-muted);
  text-align: right;
}

.preview-container {
  position: relative;
  height: 56px;
  margin-top: 10px;
  border-radius: 6px;
  overflow: hidden;
}

.preview-pattern {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  height: 100%;
  background:
    repeating-linear-gradient(
      45deg,
      var(--accent-soft),
      var(--accent-soft) 10px,
      transparent 10px,
      transparent 20px
    );
}

.preview-panel {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
}

.preview-text {
  font-size: 12px;
  color: var(--text-muted);
}
</style>
