<script setup lang="ts">
import { Check, Info, Monitor, Moon, Sun } from '@lucide/vue'
import { UiButton, UiInput } from '@/components/ui'
import BackgroundPreview from '@/components/ui/background-preview.vue'
import PanelStyleControl from '@/components/ui/panel-style-control.vue'
import { useAppearanceSettings } from './appearanceSettings'

const {
  accentColor,
  accentColors,
  backgroundSettings,
  backgroundSource,
  changeAccentColor,
  changeTheme,
  panelStyle,
  resetBackground,
  resetPanelStyle,
  selectBackgroundFile,
  setBackgroundBlurFromEvent,
  setBackgroundOpacityFromEvent,
  setBackgroundPositionFromEvent,
  setBackgroundRepeatFromEvent,
  setBackgroundSizeFromEvent,
  setBackgroundType,
  t,
  theme,
  updatePanelStyle,
} = useAppearanceSettings()
</script>

<template>
  <h1>{{ t('settings.appearance') }}</h1>

            <h2>{{ t('settings.theme') }}</h2>
            <div class="theme-options">
              <button
                :class="['theme-option', { active: theme === 'dark' }]"
                @click="changeTheme('dark')"
              >
                <Moon :size="18" />
                {{ t('settings.dark') }}
              </button>
              <button
                :class="['theme-option', { active: theme === 'light' }]"
                @click="changeTheme('light')"
              >
                <Sun :size="18" />
                {{ t('settings.light') }}
              </button>
              <button
                :class="['theme-option', { active: theme === 'system' }]"
                @click="changeTheme('system')"
              >
                <Monitor :size="18" />
                {{ t('settings.system') }}
              </button>
            </div>

            <h2>{{ t('settings.accentColor') }}</h2>
            <p class="accent-color-hint">{{ t('settings.accentColorHint') }}</p>
            <div class="accent-color-grid">
              <button
                v-for="color in accentColors"
                :key="color.key"
                :class="['accent-color-swatch', { active: accentColor === color.key }]"
                :style="{ '--swatch-color': color.dark.accentPrimary }"
                :title="t(color.labelKey)"
                @click="changeAccentColor(color.key)"
              >
                <span class="accent-color-dot"></span>
                <span class="accent-color-label">{{ t(color.labelKey) }}</span>
                <Check v-if="accentColor === color.key" :size="14" class="accent-color-check" />
              </button>
            </div>

            <!-- Global Material Effect Section -->
            <h2>{{ t('settings.globalMaterial') }}</h2>
            <p class="accent-color-hint">{{ t('settings.globalMaterialHint') }}</p>
            <div class="panel-styles-grid">
              <PanelStyleControl
                :label="t('settings.globalMaterial')"
                :settings="panelStyle"
                @update="updatePanelStyle($event)"
              />
            </div>
            <div class="panel-styles-actions">
              <UiButton variant="outline" size="sm" @click="resetPanelStyle">
                {{ t('settings.resetPanelStyles') }}
              </UiButton>
            </div>

            <!-- Background Settings Section -->
            <h2>{{ t('settings.background') }}</h2>

            <!-- Background Type Selection -->
            <h2>{{ t('settings.backgroundType') }}</h2>
            <div class="background-type-options">
              <button
                :class="['bg-type-option', { active: backgroundSettings.type === 'none' }]"
                @click="setBackgroundType('none')"
              >
                {{ t('settings.bgNone') }}
              </button>
              <button
                :class="['bg-type-option', { active: backgroundSettings.type === 'image' }]"
                @click="setBackgroundType('image')"
              >
                {{ t('settings.bgImage') }}
              </button>
              <button
                :class="['bg-type-option', { active: backgroundSettings.type === 'video' }]"
                @click="setBackgroundType('video')"
              >
                {{ t('settings.bgVideo') }}
              </button>
              <button
                :class="['bg-type-option', { active: backgroundSettings.type === 'html' }]"
                @click="setBackgroundType('html')"
              >
                {{ t('settings.bgHtml') }}
              </button>
            </div>

            <!-- File/URL Input (for image and video) -->
            <div v-if="backgroundSettings.type !== 'none'" class="background-source-section">
              <h2>{{ t('settings.backgroundSource') }}</h2>
              <div class="source-input-row">
                <UiInput
                  v-model="backgroundSource"
                  :placeholder="t('settings.bgSourcePlaceholder')"
                  class="source-input"
                />
                <UiButton
                  v-if="backgroundSettings.type === 'image' || backgroundSettings.type === 'video'"
                  variant="outline"
                  @click="selectBackgroundFile"
                >
                  {{ t('settings.browse') }}
                </UiButton>
              </div>
              <p v-if="backgroundSettings.type === 'image'" class="source-hint">
                {{ t('settings.bgImageFormats') }}
              </p>
              <p v-else-if="backgroundSettings.type === 'video'" class="source-hint">
                {{ t('settings.bgVideoFormats') }}
              </p>
              <p v-else-if="backgroundSettings.type === 'html'" class="source-hint">
                {{ t('settings.bgHtmlHint') }}
              </p>
            </div>

            <!-- Background Parameters -->
            <div v-if="backgroundSettings.type !== 'none'" class="background-params-section">
              <h2>{{ t('settings.backgroundParams') }}</h2>

              <!-- Opacity -->
              <div class="param-row">
                <label class="param-label">{{ t('settings.opacity') }}</label>
                <input
                  type="range"
                  min="0"
                  max="100"
                  :value="backgroundSettings.opacity"
                  class="param-slider"
                  @input="setBackgroundOpacityFromEvent"
                />
                <span class="param-value">{{ backgroundSettings.opacity }}%</span>
              </div>

              <!-- Blur -->
              <div class="param-row">
                <label class="param-label">{{ t('settings.blur') }}</label>
                <input
                  type="range"
                  min="0"
                  max="20"
                  :value="backgroundSettings.blur"
                  class="param-slider"
                  @input="setBackgroundBlurFromEvent"
                />
                <span class="param-value">{{ backgroundSettings.blur }}px</span>
              </div>

              <!-- Size -->
              <div v-if="backgroundSettings.type === 'image'" class="param-row">
                <label class="param-label">{{ t('settings.bgSize') }}</label>
                <select
                  :value="backgroundSettings.size"
                  class="param-select"
                  @change="setBackgroundSizeFromEvent"
                >
                  <option value="cover">{{ t('settings.bgSizeCover') }}</option>
                  <option value="contain">{{ t('settings.bgSizeContain') }}</option>
                  <option value="auto">{{ t('settings.bgSizeAuto') }}</option>
                </select>
              </div>

              <!-- Position (for image) -->
              <div v-if="backgroundSettings.type === 'image'" class="param-row">
                <label class="param-label">{{ t('settings.bgPosition') }}</label>
                <select
                  :value="backgroundSettings.position"
                  class="param-select"
                  @change="setBackgroundPositionFromEvent"
                >
                  <option value="center">{{ t('settings.bgPositionCenter') }}</option>
                  <option value="top">{{ t('settings.bgPositionTop') }}</option>
                  <option value="bottom">{{ t('settings.bgPositionBottom') }}</option>
                  <option value="left">{{ t('settings.bgPositionLeft') }}</option>
                  <option value="right">{{ t('settings.bgPositionRight') }}</option>
                </select>
              </div>

              <!-- Repeat (for image) -->
              <div v-if="backgroundSettings.type === 'image'" class="param-row">
                <label class="param-label">{{ t('settings.bgRepeat') }}</label>
                <select
                  :value="backgroundSettings.repeat"
                  class="param-select"
                  @change="setBackgroundRepeatFromEvent"
                >
                  <option value="no-repeat">{{ t('settings.bgRepeatNoRepeat') }}</option>
                  <option value="repeat">{{ t('settings.bgRepeatRepeat') }}</option>
                  <option value="repeat-x">{{ t('settings.bgRepeatRepeatX') }}</option>
                  <option value="repeat-y">{{ t('settings.bgRepeatRepeatY') }}</option>
                </select>
              </div>
            </div>

            <!-- Background Preview -->
            <div v-if="backgroundSettings.type !== 'none'" class="background-preview-section">
              <h2>{{ t('settings.preview') }}</h2>
              <BackgroundPreview :settings="backgroundSettings" :height="150" />
            </div>

            <!-- Reset Button -->
            <div class="background-actions">
              <UiButton variant="outline" size="sm" @click="resetBackground">
                {{ t('settings.resetBackground') }}
              </UiButton>
            </div>

            <!-- Performance Warning -->
            <div v-if="backgroundSettings.type !== 'none'" class="performance-warning">
              <Info :size="14" />
              <span>{{ t('settings.bgPerformanceWarning') }}</span>
            </div>
</template>

<style scoped>
@media (max-width: 640px) {
  .theme-option {
    min-width: 0;
    min-height: 40px;
    gap: 4px;
    padding: 8px;
    font-size: 12px;
    white-space: nowrap;
  }
}
</style>
