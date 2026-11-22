<script setup lang="ts">
import { Check, Laptop, Moon, Sun } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiSelectField } from '@/components/ui'
import {
  MONOCHROME_ACCENT_KEY,
  WARM_ACCENT_KEY,
  useTheme,
  type Theme,
} from '@/composables/useTheme'

const { t, te, locale } = useI18n()
const { theme, setTheme, accentColor, setAccentColor } = useTheme()

function label(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

const languageOptions = [
  { value: 'zh-CN', label: '简体中文' },
  { value: 'zh-TW', label: '繁體中文' },
  { value: 'en', label: 'English' },
  { value: 'ja', label: '日本語' },
  { value: 'ru', label: 'Русский' },
]

const palettePresets = [
  {
    key: MONOCHROME_ACCENT_KEY,
    titleKey: 'accentColors.monochrome',
    titleFallback: '黑白极简',
    descKey: 'oobe.welcome.paletteMonochromeDesc',
    descFallback: '纯粹无色相中性灰，克制利落',
    previewClass: 'swatch-preview--monochrome',
  },
  {
    key: WARM_ACCENT_KEY,
    titleKey: 'accentColors.warm',
    titleFallback: '暖墨纸白',
    descKey: 'oobe.welcome.paletteWarmDesc',
    descFallback: '温润纸感与暖墨调，柔和耐看',
    previewClass: 'swatch-preview--warm',
  },
] as const

const themeModes: Array<{ value: Theme; icon: typeof Sun; key: string; fallback: string }> = [
  { value: 'system', icon: Laptop, key: 'settings.themeSystem', fallback: '跟随系统' },
  { value: 'light', icon: Sun, key: 'settings.themeLight', fallback: '浅色' },
  { value: 'dark', icon: Moon, key: 'settings.themeDark', fallback: '深色' },
]

function onChangeLocale(nextLocale: string): void {
  if (!nextLocale) return
  locale.value = nextLocale
  try {
    localStorage.setItem('tinadec-locale', nextLocale)
  } catch {
    // Ignore storage quota errors.
  }
  window.tinadec?.setInterfaceLocale?.(nextLocale)
}
</script>

<template>
  <section class="oobe-stage-panel" data-testid="oobe-step-welcome">
    <header class="oobe-stage-head">
      <span class="oobe-eyebrow">01 / 03</span>
      <h1>{{ label('oobe.welcome.title', '设定你的工作台基调') }}</h1>
      <p>{{ label('oobe.welcome.subtitle', '选择偏好的视觉气质、明暗模式与界面语言，所有设定实时生效，也可随时在设置中心调整。') }}</p>
    </header>

    <div
      class="oobe-palette-grid"
      role="radiogroup"
      :aria-label="label('oobe.welcome.paletteLabel', '视觉风格')"
    >
      <button
        v-for="preset in palettePresets"
        :key="preset.key"
        type="button"
        role="radio"
        class="oobe-palette-card"
        :class="{ 'is-selected': accentColor === preset.key }"
        :aria-checked="accentColor === preset.key"
        :data-testid="`oobe-palette-${preset.key}`"
        @click="setAccentColor(preset.key)"
      >
        <div class="oobe-swatch-preview" :class="preset.previewClass" aria-hidden="true">
          <div class="swatch-sidebar">
            <span class="swatch-dot" />
            <span class="swatch-nav-line" />
            <span class="swatch-nav-line swatch-nav-line--short" />
          </div>
          <div class="swatch-canvas">
            <div class="swatch-msg swatch-msg--muted" />
            <div class="swatch-msg swatch-msg--wide" />
            <div class="swatch-composer">
              <span class="swatch-composer-line" />
              <span class="swatch-cta" />
            </div>
          </div>
        </div>

        <div class="oobe-palette-meta">
          <div class="oobe-palette-title-row">
            <strong>{{ label(preset.titleKey, preset.titleFallback) }}</strong>
            <span class="oobe-palette-check" aria-hidden="true">
              <Check v-if="accentColor === preset.key" :size="11" />
            </span>
          </div>
          <span class="oobe-palette-desc">{{ label(preset.descKey, preset.descFallback) }}</span>
        </div>
      </button>
    </div>

    <div class="oobe-hairline-rows">
      <div class="oobe-hairline-row">
        <div class="oobe-row-copy">
          <span class="oobe-row-label">{{ label('oobe.welcome.themeLabel', '明暗模式') }}</span>
        </div>
        <div
          class="oobe-mode-segmented"
          role="radiogroup"
          :aria-label="label('oobe.welcome.themeLabel', '明暗模式')"
        >
          <button
            v-for="item in themeModes"
            :key="item.value"
            type="button"
            role="radio"
            class="oobe-mode-btn"
            :class="{ 'is-active': theme === item.value }"
            :aria-checked="theme === item.value"
            :data-testid="`oobe-theme-${item.value}`"
            @click="setTheme(item.value)"
          >
            <component :is="item.icon" :size="13" aria-hidden="true" />
            <span>{{ label(item.key, item.fallback) }}</span>
          </button>
        </div>
      </div>

      <div class="oobe-hairline-row">
        <div class="oobe-row-copy">
          <span class="oobe-row-label">{{ label('oobe.welcome.languageLabel', '界面语言') }}</span>
        </div>
        <div class="oobe-lang-select">
          <UiSelectField
            :model-value="String(locale)"
            :options="languageOptions"
            :aria-label="label('oobe.welcome.languageLabel', '界面语言')"
            data-testid="oobe-locale-select"
            @update:model-value="onChangeLocale"
          />
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.oobe-stage-panel {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.oobe-stage-head {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.oobe-eyebrow {
  font-family: var(--font-mono, ui-monospace, monospace);
  font-size: 11px;
  font-weight: 500;
  letter-spacing: 0.08em;
  color: var(--text-muted);
}

.oobe-stage-head h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
  letter-spacing: -0.025em;
  line-height: 1.25;
  color: var(--text-primary);
}

.oobe-stage-head p {
  margin: 2px 0 0;
  font-size: 13.5px;
  line-height: 1.6;
  color: var(--text-secondary);
}

.oobe-palette-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

@media (max-width: 520px) {
  .oobe-palette-grid {
    grid-template-columns: 1fr;
  }
}

.oobe-palette-card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px;
  border-radius: 12px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  text-align: left;
  cursor: pointer;
  transition:
    border-color 140ms ease,
    background-color 140ms ease,
    transform 140ms cubic-bezier(0.2, 0, 0, 1);
}

.oobe-palette-card:hover {
  border-color: var(--border-default, var(--border-muted));
  background: var(--surface-raised);
}

.oobe-palette-card:active {
  transform: scale(0.99);
}

.oobe-palette-card.is-selected {
  border-color: var(--text-primary);
  background: var(--surface-raised);
}

.oobe-palette-card:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 2px;
}

.oobe-swatch-preview {
  height: 88px;
  border-radius: 8px;
  border: 1px solid rgba(128, 128, 128, 0.18);
  display: grid;
  grid-template-columns: 52px 1fr;
  overflow: hidden;
}

.swatch-preview--monochrome {
  --sw-bg: #09090b;
  --sw-rail: #121215;
  --sw-line: rgba(250, 250, 250, 0.14);
  --sw-line-strong: rgba(250, 250, 250, 0.34);
  --sw-box: #18181b;
  --sw-cta: #fafafa;
}

:global([data-theme='light']) .swatch-preview--monochrome {
  --sw-bg: #ffffff;
  --sw-rail: #f4f4f5;
  --sw-line: rgba(24, 24, 27, 0.12);
  --sw-line-strong: rgba(24, 24, 27, 0.28);
  --sw-box: #f4f4f5;
  --sw-cta: #18181b;
}

.swatch-preview--warm {
  --sw-bg: #151311;
  --sw-rail: #1e1b18;
  --sw-line: rgba(231, 226, 216, 0.15);
  --sw-line-strong: rgba(231, 226, 216, 0.36);
  --sw-box: #221f1b;
  --sw-cta: #ca7554;
}

:global([data-theme='light']) .swatch-preview--warm {
  --sw-bg: #faf7f2;
  --sw-rail: #f1ebe2;
  --sw-line: rgba(28, 25, 23, 0.12);
  --sw-line-strong: rgba(28, 25, 23, 0.28);
  --sw-box: #f2ece3;
  --sw-cta: #ca7554;
}

.swatch-sidebar {
  background: var(--sw-rail);
  border-right: 1px solid var(--sw-line);
  padding: 10px 8px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.swatch-dot {
  width: 8px;
  height: 8px;
  border-radius: 999px;
  background: var(--sw-line-strong);
  margin-bottom: 2px;
}

.swatch-nav-line {
  height: 4px;
  border-radius: 2px;
  background: var(--sw-line);
}

.swatch-nav-line--short {
  width: 68%;
}

.swatch-canvas {
  background: var(--sw-bg);
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
}

.swatch-msg {
  height: 5px;
  border-radius: 999px;
  background: var(--sw-line);
}

.swatch-msg--muted {
  width: 48%;
}

.swatch-msg--wide {
  width: 76%;
  background: var(--sw-line-strong);
}

.swatch-composer {
  height: 22px;
  border-radius: 6px;
  background: var(--sw-box);
  border: 1px solid var(--sw-line);
  padding: 0 6px;
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.swatch-composer-line {
  width: 42%;
  height: 4px;
  border-radius: 2px;
  background: var(--sw-line);
}

.swatch-cta {
  width: 16px;
  height: 10px;
  border-radius: 999px;
  background: var(--sw-cta);
}

.oobe-palette-meta {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.oobe-palette-title-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.oobe-palette-title-row strong {
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.oobe-palette-check {
  width: 18px;
  height: 18px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: var(--text-on-primary, #ffffff);
  background: transparent;
  transition: background-color 140ms ease, border-color 140ms ease;
}

.oobe-palette-card.is-selected .oobe-palette-check {
  border-color: var(--bg-primary-button, var(--text-primary));
  background: var(--bg-primary-button, var(--text-primary));
}

.oobe-palette-desc {
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.oobe-hairline-rows {
  display: flex;
  flex-direction: column;
  border-top: 1px solid var(--border-muted);
  border-bottom: 1px solid var(--border-muted);
}

.oobe-hairline-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 13px 0;
  flex-wrap: wrap;
}

.oobe-hairline-row + .oobe-hairline-row {
  border-top: 1px solid var(--border-muted);
}

.oobe-row-label {
  font-size: 13px;
  font-weight: 500;
  color: var(--text-primary);
}

.oobe-mode-segmented {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 3px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}

.oobe-mode-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease;
}

.oobe-mode-btn:hover {
  color: var(--text-primary);
}

.oobe-mode-btn.is-active {
  background: var(--surface-raised);
  color: var(--text-primary);
  box-shadow: var(--shadow-subtle, 0 1px 2px rgba(0, 0, 0, 0.08));
}

.oobe-lang-select {
  width: 184px;
}
</style>
