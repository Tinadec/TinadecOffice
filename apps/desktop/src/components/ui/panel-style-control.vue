<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { RotateCcw, Sparkles } from '@lucide/vue'
import {
  PANEL_MATERIAL_PRESETS,
  computePanelStyle,
  type PanelEffect,
  type PanelMaterialPresetId,
  type PanelStyleSettings,
} from '@/composables/usePanelStyles'

const { t, te } = useI18n()

const props = defineProps<{
  label: string
  settings: PanelStyleSettings
}>()

const emit = defineEmits<{
  update: [settings: Partial<PanelStyleSettings>]
}>()

function tr(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

interface PresetCardItem {
  id: PanelMaterialPresetId
  title: string
  desc: string
  chip: string
  recommended?: boolean
}

const customChipLabel = computed(() => {
  if (props.settings.preset === 'custom') {
    if (props.settings.effect === 'blur') {
      return `${props.settings.opacity}% · ${props.settings.blur}px · ${props.settings.saturation}%`
    }
    return `${props.settings.opacity}% · 0px`
  }
  return tr('settings.materialPresetCustomChip', '0–32px · 精细调节')
})

const presetCards = computed<PresetCardItem[]>(() => [
  {
    id: 'solid',
    title: tr('settings.materialPresetSolid', '纯实心'),
    desc: tr('settings.materialPresetSolidDesc', '零透明零模糊，纯净利落'),
    chip: `${PANEL_MATERIAL_PRESETS.solid.opacity}% · 0px`,
  },
  {
    id: 'frosted',
    title: tr('settings.materialPresetFrosted', '柔和磨砂'),
    desc: tr('settings.materialPresetFrostedDesc', '柔化底图，阅读与质感均衡'),
    chip: `${PANEL_MATERIAL_PRESETS.frosted.opacity}% · ${PANEL_MATERIAL_PRESETS.frosted.blur}px`,
    recommended: true,
  },
  {
    id: 'crystal',
    title: tr('settings.materialPresetCrystal', '清透玻璃'),
    desc: tr('settings.materialPresetCrystalDesc', '高通透空气感，凸显壁纸'),
    chip: `${PANEL_MATERIAL_PRESETS.crystal.opacity}% · ${PANEL_MATERIAL_PRESETS.crystal.blur}px`,
  },
  {
    id: 'mica',
    title: tr('settings.materialPresetMica', '沉稳厚砂'),
    desc: tr('settings.materialPresetMicaDesc', '温润厚重，低干扰长时沉浸'),
    chip: `${PANEL_MATERIAL_PRESETS.mica.opacity}% · ${PANEL_MATERIAL_PRESETS.mica.blur}px`,
  },
  {
    id: 'translucent',
    title: tr('settings.materialPresetTranslucent', '轻半透明'),
    desc: tr('settings.materialPresetTranslucentDesc', '轻盈微透，省去模糊滤镜'),
    chip: `${PANEL_MATERIAL_PRESETS.translucent.opacity}% · 0px`,
  },
  {
    id: 'custom',
    title: tr('settings.materialPresetCustom', '自定义'),
    desc: tr('settings.materialPresetCustomDesc', '自由调节模糊、透明与饱和度'),
    chip: customChipLabel.value,
  },
])

// STRICT RULE: Sliders appear ONLY when settings.preset === 'custom', and
// Saturation (100%–200%) appears STRICTLY ONLY when settings.preset === 'custom' && settings.effect === 'blur'.
const isCustom = computed(() => props.settings.preset === 'custom')
const isCustomBlur = computed(() => props.settings.preset === 'custom' && props.settings.effect === 'blur')

const customEffectOptions = computed(() => [
  { value: 'translucent' as PanelEffect, label: tr('settings.effectTranslucent', '半透明') },
  { value: 'blur' as PanelEffect, label: tr('settings.materialCustomEffectBlur', '毛玻璃') },
])

// Checkerboard preview box directly uses computePanelStyle() so opacity, blur,
// saturation, and glass edge border match live panel rendering 1:1.
const previewStyle = computed(() => {
  const style: Record<string, string> = {
    borderRadius: '8px',
    transition: 'background-color 0.16s ease, backdrop-filter 0.16s ease, border-color 0.16s ease',
    ...computePanelStyle(props.settings),
  }

  if (props.settings.effect === 'opaque') {
    style.background = 'var(--bg-primary)'
  }

  return style
})

function selectPreset(presetId: PanelMaterialPresetId): void {
  if (presetId === 'custom') {
    if (props.settings.effect === 'opaque') {
      emit('update', {
        preset: 'custom',
        effect: 'blur',
        opacity: PANEL_MATERIAL_PRESETS.frosted.opacity,
        blur: PANEL_MATERIAL_PRESETS.frosted.blur,
        saturation: PANEL_MATERIAL_PRESETS.frosted.saturation,
      })
    } else {
      emit('update', { preset: 'custom' })
    }
    return
  }

  const spec = PANEL_MATERIAL_PRESETS[presetId]
  emit('update', {
    preset: spec.id,
    effect: spec.effect,
    opacity: spec.opacity,
    blur: spec.blur,
    saturation: spec.saturation,
  })
}

function setCustomEffect(effect: PanelEffect): void {
  if (effect === 'blur') {
    emit('update', {
      preset: 'custom',
      effect: 'blur',
      blur: props.settings.blur > 0 ? props.settings.blur : PANEL_MATERIAL_PRESETS.frosted.blur,
      saturation: props.settings.saturation > 100
        ? props.settings.saturation
        : PANEL_MATERIAL_PRESETS.frosted.saturation,
    })
    return
  }
  emit('update', {
    preset: 'custom',
    effect: 'translucent',
  })
}

function setOpacity(event: Event): void {
  const nextOpacity = Number.parseInt((event.target as HTMLInputElement).value, 10)
  if (!Number.isFinite(nextOpacity)) return
  emit('update', { preset: 'custom', opacity: nextOpacity })
}

function setBlur(event: Event): void {
  const nextBlur = Number.parseInt((event.target as HTMLInputElement).value, 10)
  if (!Number.isFinite(nextBlur)) return
  emit('update', { preset: 'custom', blur: nextBlur })
}

function setSaturation(event: Event): void {
  const nextSaturation = Number.parseInt((event.target as HTMLInputElement).value, 10)
  if (!Number.isFinite(nextSaturation)) return
  emit('update', { preset: 'custom', saturation: nextSaturation })
}

function restoreRecommended(): void {
  const frosted = PANEL_MATERIAL_PRESETS.frosted
  emit('update', {
    preset: 'frosted',
    effect: frosted.effect,
    opacity: frosted.opacity,
    blur: frosted.blur,
    saturation: frosted.saturation,
  })
}
</script>

<template>
  <div class="panel-style-control" data-testid="panel-style-control">
    <!-- Header + 1-click Restore Recommended (Frosted) -->
    <div class="control-header">
      <span class="control-label">{{ props.label }}</span>
      <button
        type="button"
        class="control-restore-btn"
        :disabled="settings.preset === 'frosted'"
        data-testid="panel-style-restore-recommended"
        @click="restoreRecommended"
      >
        <RotateCcw :size="12" aria-hidden="true" />
        <span>{{ tr('settings.materialRestoreRecommended', '恢复推荐（柔和磨砂）') }}</span>
      </button>
    </div>

    <!-- 6 Preset Cards -->
    <div
      class="preset-grid"
      role="radiogroup"
      :aria-label="props.label"
      data-testid="panel-preset-grid"
    >
      <button
        v-for="card in presetCards"
        :key="card.id"
        type="button"
        role="radio"
        :aria-checked="settings.preset === card.id"
        :class="['preset-card', { active: settings.preset === card.id }]"
        :data-testid="`panel-preset-${card.id}`"
        @click="selectPreset(card.id)"
      >
        <div class="preset-card__head">
          <span class="preset-card__title">
            {{ card.title }}
            <span v-if="card.recommended" class="preset-card__star" aria-hidden="true">★</span>
          </span>
          <span v-if="card.recommended" class="preset-card__badge">
            {{ tr('settings.materialRecommendedBadge', '推荐') }}
          </span>
        </div>
        <span class="preset-card__desc">{{ card.desc }}</span>
        <span class="preset-card__chip">{{ card.chip }}</span>
      </button>
    </div>

    <!-- Custom Controls: STRICTLY ONLY rendered when settings.preset === 'custom' -->
    <div
      v-if="isCustom"
      class="custom-controls"
      data-testid="panel-custom-controls"
    >
      <div class="custom-mode-row">
        <span class="slider-label">{{ tr('settings.materialCustomModeLabel', '效果模式') }}</span>
        <div
          class="effect-selector"
          role="radiogroup"
          :aria-label="tr('settings.materialCustomModeLabel', '效果模式')"
          data-testid="panel-custom-effect-selector"
        >
          <button
            v-for="option in customEffectOptions"
            :key="option.value"
            type="button"
            role="radio"
            :aria-checked="settings.effect === option.value"
            :class="['effect-option', { active: settings.effect === option.value }]"
            :data-testid="`panel-custom-effect-${option.value}`"
            @click="setCustomEffect(option.value)"
          >
            {{ option.label }}
          </button>
        </div>
      </div>

      <!-- Opacity slider (0%–100%) -->
      <div class="slider-row" data-testid="panel-slider-row-opacity">
        <label class="slider-label" for="panel-custom-opacity">{{ tr('settings.opacity', '不透明度') }}</label>
        <input
          id="panel-custom-opacity"
          type="range"
          min="0"
          max="100"
          step="1"
          :value="settings.opacity"
          class="slider-input"
          data-testid="panel-slider-opacity"
          @input="setOpacity"
        />
        <span class="slider-value">{{ settings.opacity }}%</span>
      </div>

      <!-- Blur radius slider (0–32px, custom + blur only) -->
      <div v-if="isCustomBlur" class="slider-row" data-testid="panel-slider-row-blur">
        <label class="slider-label" for="panel-custom-blur">{{ tr('settings.blur', '模糊度') }}</label>
        <input
          id="panel-custom-blur"
          type="range"
          min="0"
          max="32"
          step="1"
          :value="settings.blur"
          class="slider-input"
          data-testid="panel-slider-blur"
          @input="setBlur"
        />
        <span class="slider-value">{{ settings.blur }}px</span>
      </div>

      <!-- Color Saturation slider (100%–200%, STRICTLY ONLY in custom + blur!) -->
      <div
        v-if="settings.preset === 'custom' && settings.effect === 'blur'"
        class="slider-row"
        data-testid="panel-slider-row-saturation"
      >
        <label class="slider-label" for="panel-custom-saturation">
          {{ tr('settings.materialSaturation', '色彩饱和度') }}
        </label>
        <input
          id="panel-custom-saturation"
          type="range"
          min="100"
          max="200"
          step="1"
          :value="settings.saturation"
          class="slider-input"
          data-testid="panel-slider-saturation"
          @input="setSaturation"
        />
        <span class="slider-value">{{ settings.saturation }}%</span>
      </div>
    </div>

    <!-- Live Checkerboard + Chromatic Preview -->
    <div class="preview-container" data-testid="panel-style-preview">
      <div class="preview-checkerboard" aria-hidden="true" />
      <div class="preview-chromatic-orbs" aria-hidden="true" />
      <div :style="previewStyle" class="preview-panel" data-testid="panel-style-preview-surface">
        <div class="preview-content">
          <Sparkles :size="13" class="preview-icon" aria-hidden="true" />
          <span class="preview-text">{{ tr('settings.preview', '预览') }}</span>
          <span class="preview-readout">
            {{ settings.effect === 'opaque'
              ? '100%'
              : settings.effect === 'blur'
                ? `${settings.opacity}% · ${settings.blur}px · ${settings.saturation}%`
                : `${settings.opacity}%` }}
          </span>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.panel-style-control {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px;
  border: 1px solid var(--border-card);
  border-radius: 10px;
  background: var(--surface-section);
}

.control-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}

.control-label {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.control-restore-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 9px;
  border: 1px solid var(--border-card);
  border-radius: 6px;
  background: var(--surface-button);
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: color 120ms ease, background-color 120ms ease, border-color 120ms ease, opacity 120ms ease;
}

.control-restore-btn:hover:not(:disabled) {
  color: var(--text-primary);
  background: var(--surface-button-hover);
  border-color: var(--border-default);
}

.control-restore-btn:disabled {
  opacity: 0.52;
  cursor: default;
}

.preset-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
}

@media (max-width: 600px) {
  .preset-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

.preset-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  justify-content: space-between;
  gap: 5px;
  padding: 10px 11px;
  border-radius: 9px;
  border: 1px solid var(--border-card);
  background: var(--surface-button);
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
  transition:
    border-color 130ms ease,
    background-color 130ms ease,
    box-shadow 130ms ease;
}

.preset-card:hover {
  background: var(--surface-button-hover);
  border-color: var(--border-default);
}

.preset-card.active {
  border-color: var(--accent-primary);
  background: var(--surface-selected);
  box-shadow: 0 0 0 1px color-mix(in srgb, var(--accent-primary) 28%, transparent);
}

.preset-card:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 2px;
}

.preset-card__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  width: 100%;
}

.preset-card__title {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.preset-card__star {
  font-size: 11px;
  color: var(--accent-warning, #d29922);
}

.preset-card__badge {
  display: inline-flex;
  align-items: center;
  height: 16px;
  padding: 0 5px;
  border-radius: 999px;
  background: var(--accent-soft);
  color: var(--text-primary);
  font-size: 10px;
  font-weight: 600;
  flex-shrink: 0;
}

.preset-card__desc {
  font-size: 11.5px;
  line-height: 1.38;
  color: var(--text-secondary);
}

.preset-card__chip {
  margin-top: 2px;
  padding: 1px 6px;
  border-radius: 4px;
  background: var(--surface-section);
  border: 1px solid var(--border-muted);
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 10.5px;
  font-variant-numeric: tabular-nums;
  color: var(--text-muted);
}

.custom-controls {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.custom-mode-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.effect-selector {
  display: inline-flex;
  gap: 4px;
  padding: 2px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.effect-option {
  padding: 4px 12px;
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-secondary);
  background: transparent;
  border: 1px solid transparent;
  border-radius: 5px;
  cursor: pointer;
  transition: color 120ms ease, background-color 120ms ease, border-color 120ms ease;
}

.effect-option:hover {
  color: var(--text-primary);
}

.effect-option.active {
  color: var(--text-primary);
  background: var(--surface-selected);
  border-color: var(--border-default);
}

.slider-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.slider-label {
  min-width: 72px;
  font-size: 12px;
  font-weight: 500;
  color: var(--text-secondary);
}

.slider-input {
  flex: 1;
  height: 4px;
  -webkit-appearance: none;
  appearance: none;
  background: var(--surface-section);
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
}

.slider-value {
  min-width: 44px;
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
  color: var(--text-muted);
  text-align: right;
}

.preview-container {
  position: relative;
  height: 68px;
  padding: 8px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  overflow: hidden;
}

.preview-checkerboard {
  position: absolute;
  inset: 0;
  background-color: var(--bg-secondary);
  background-image:
    linear-gradient(45deg, var(--accent-soft) 25%, transparent 25%),
    linear-gradient(-45deg, var(--accent-soft) 25%, transparent 25%),
    linear-gradient(45deg, transparent 75%, var(--accent-soft) 75%),
    linear-gradient(-45deg, transparent 75%, var(--accent-soft) 75%);
  background-size: 16px 16px;
  background-position: 0 0, 0 8px, 8px -8px, -8px 0;
}

.preview-chromatic-orbs {
  position: absolute;
  inset: 0;
  pointer-events: none;
  background:
    radial-gradient(circle 48px at 24% 52%, rgba(56, 189, 248, 0.42), transparent 72%),
    radial-gradient(circle 52px at 54% 48%, rgba(168, 85, 247, 0.36), transparent 72%),
    radial-gradient(circle 46px at 78% 54%, rgba(251, 146, 60, 0.38), transparent 72%);
}

.preview-panel {
  position: relative;
  width: 100%;
  height: 100%;
  border: 1px solid var(--border-card);
  display: flex;
  align-items: center;
  justify-content: center;
}

.preview-content {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 0 10px;
}

.preview-icon {
  color: var(--text-secondary);
}

.preview-text {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
}

.preview-readout {
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  color: var(--text-secondary);
}
</style>
