<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Moon, Monitor, Sun } from '@lucide/vue'
import { UiButton, UiInput, UiLabel } from '@/components/ui'
import BackgroundPreview from '@/components/ui/background-preview.vue'
import PanelStyleControl from '@/components/ui/panel-style-control.vue'
import { DYNAMIC_ACCENT_KEY, useTheme } from '@/composables/useTheme'
import { getDynamicPaletteRef } from '@/composables/useDynamicPalette'
import { previewSwatches } from '@/lib/monetExtract'
import { useBackground } from '@/composables/useBackground'
import { usePanelStyles } from '@/composables/usePanelStyles'

/**
 * Appearance section extracted from SettingsPage (D7.2).
 *
 * Owns: theme/accent selection (with cross-window broadcast), global panel
 * material control, and the custom background manager. useBackground and
 * usePanelStyles are module-level singletons shared with App.vue, so this
 * component mutates the same global state the old inline section did.
 */
const { t } = useI18n()
const { theme, setTheme, accentColor, setAccentColor, accentColors } = useTheme()

const {
  settings: backgroundSettings,
  setBackgroundType,
  setBackgroundSource,
  setBackgroundOpacity,
  setBackgroundBlur,
  setBackgroundSize,
  setBackgroundPosition,
  setBackgroundRepeat,
  selectFile: selectBackgroundFile,
  resetBackground,
} = useBackground()

const {
  panelStyle,
  updatePanelStyle,
  resetPanelStyle,
} = usePanelStyles()

// Monet "follow background" accent: palette is extracted by the global
// watcher armed at startup; this section only renders its current state.
const dynamicPalette = getDynamicPaletteRef()
const hasPalette = computed(() => dynamicPalette.value !== null)
const resolvedTheme = computed<'dark' | 'light'>(() => {
  if (theme.value === 'system') {
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  }
  return theme.value
})
// Five standalone circles: dark accent / light accent / primary button /
// dark surface / light surface — the roles the extraction drives.
const dynamicCircles = computed(() =>
  dynamicPalette.value ? previewSwatches(dynamicPalette.value.sourceColor) : [],
)
const isFrozen = computed(
  () => accentColor.value === DYNAMIC_ACCENT_KEY && backgroundSettings.value.type !== 'image',
)

const backgroundSource = computed({
  get: () => backgroundSettings.value.source,
  set: (val: string) => setBackgroundSource(val),
})

function changeTheme(newTheme: 'dark' | 'light' | 'system'): void {
  setTheme(newTheme)
  window.tinadec?.broadcastTheme?.(newTheme, accentColor.value)
}

function changeAccentColor(key: string): void {
  setAccentColor(key)
  window.tinadec?.broadcastTheme?.(theme.value, key)
}
</script>

<template>
  <div>
    <h2>{{ t('settings.appearance') }}</h2>

    <h3>{{ t('settings.theme') }}</h3>
    <div class="theme-options" data-testid="theme-options">
      <button :class="['theme-option', { active: theme === 'dark' }]" @click="changeTheme('dark')">
        <Moon :size="18" />
        {{ t('settings.dark') }}
      </button>
      <button :class="['theme-option', { active: theme === 'light' }]" @click="changeTheme('light')">
        <Sun :size="18" />
        {{ t('settings.light') }}
      </button>
      <button :class="['theme-option', { active: theme === 'system' }]" @click="changeTheme('system')">
        <Monitor :size="18" />
        {{ t('settings.system') }}
      </button>
    </div>

    <h3>{{ t('settings.accentColor') }}</h3>
    <p class="accent-color-hint">{{ t('settings.accentColorHint') }}</p>
    <div class="accent-color-grid" data-testid="accent-colors">
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
      <button
        :class="['accent-dynamic-option', { active: accentColor === DYNAMIC_ACCENT_KEY }]"
        :disabled="!hasPalette"
        data-testid="accent-dynamic"
        :title="t('settings.accentFollowBackground')"
        @click="changeAccentColor(DYNAMIC_ACCENT_KEY)"
      >
        <span class="accent-dynamic-info">
          <Check v-if="accentColor === DYNAMIC_ACCENT_KEY" :size="14" class="accent-dynamic-check" />
          <span class="accent-dynamic-label">{{ t('settings.accentFollowBackground') }}</span>
        </span>
        <span class="accent-dynamic-circles">
          <span
            v-for="(circle, i) in dynamicCircles"
            :key="i"
            class="accent-dynamic-circle"
            :style="{ background: circle }"
          ></span>
        </span>
      </button>
    </div>
    <p v-if="isFrozen" class="accent-color-hint" data-testid="dynamic-frozen-hint">
      {{ t('settings.accentDynamicFrozen') }}
    </p>

    <!-- Global panel material + background manager -->
    <h3>{{ t('settings.globalMaterial') }}</h3>
    <p class="accent-color-hint">{{ t('settings.globalMaterialHint') }}</p>
    <div class="panel-styles-grid">
      <PanelStyleControl
        :label="t('settings.globalMaterial')"
        :settings="panelStyle"
        @update="updatePanelStyle($event)"
      />
      <UiButton variant="outline" size="sm" @click="resetPanelStyle()">
        {{ t('settings.resetPanelStyles') }}
      </UiButton>
    </div>

    <h3>{{ t('settings.background') }}</h3>
    <BackgroundPreview :settings="backgroundSettings" />

    <h4 class="bg-group-title">{{ t('settings.backgroundType') }}</h4>
    <div class="bg-type-options" data-testid="background-types">
      <button :class="['bg-type-option', { active: backgroundSettings.type === 'none' }]" @click="setBackgroundType('none')">
        {{ t('settings.bgNone') }}
      </button>
      <button :class="['bg-type-option', { active: backgroundSettings.type === 'image' }]" @click="setBackgroundType('image')">
        {{ t('settings.bgImage') }}
      </button>
      <button :class="['bg-type-option', { active: backgroundSettings.type === 'video' }]" @click="setBackgroundType('video')">
        {{ t('settings.bgVideo') }}
      </button>
      <button :class="['bg-type-option', { active: backgroundSettings.type === 'html' }]" @click="setBackgroundType('html')">
        {{ t('settings.bgHtml') }}
      </button>
    </div>

    <div v-if="backgroundSettings.type !== 'none'" class="background-source-section">
      <h3>{{ t('settings.backgroundSource') }}</h3>
      <div class="source-input-row">
        <UiInput
          v-model="backgroundSource"
          :placeholder="t('settings.bgSourcePlaceholder')"
          class="source-input"
        />
        <UiButton variant="outline" size="sm" @click="selectBackgroundFile()">
          {{ t('settings.browse') }}
        </UiButton>
      </div>
      <p class="bg-format-hint">
        {{ backgroundSettings.type === 'image' ? t('settings.bgImageFormats') : t('settings.bgVideoFormats') }}
      </p>
    </div>

    <template v-if="backgroundSettings.type !== 'none'">
      <h4 class="bg-group-title">{{ t('settings.backgroundParams') }}</h4>
      <div class="bg-params">
        <div class="bg-param">
          <UiLabel for="bg-opacity">{{ t('settings.opacity') }}: {{ Math.round(backgroundSettings.opacity * 100) }}%</UiLabel>
          <input id="bg-opacity" type="range" min="0" max="1" step="0.05" :value="backgroundSettings.opacity" @input="setBackgroundOpacity(Number(($event.target as HTMLInputElement).value))" />
        </div>
        <div class="bg-param">
          <UiLabel for="bg-blur">{{ t('settings.blur') }}px: {{ backgroundSettings.blur }}</UiLabel>
          <input id="bg-blur" type="range" min="0" max="30" step="1" :value="backgroundSettings.blur" @input="setBackgroundBlur(Number(($event.target as HTMLInputElement).value))" />
        </div>
        <div class="bg-param-row">
          <div class="bg-param">
            <UiLabel for="bg-size">{{ t('settings.bgSize') }}</UiLabel>
            <select id="bg-size" class="settings-select" :value="backgroundSettings.size" @change="setBackgroundSize(($event.target as HTMLSelectElement).value as 'cover' | 'contain' | 'auto')">
              <option value="cover">{{ t('settings.bgSizeCover') }}</option>
              <option value="contain">{{ t('settings.bgSizeContain') }}</option>
              <option value="auto">{{ t('settings.bgSizeAuto') }}</option>
            </select>
          </div>
          <div class="bg-param">
            <UiLabel for="bg-position">{{ t('settings.bgPosition') }}</UiLabel>
            <select id="bg-position" class="settings-select" :value="backgroundSettings.position" @change="setBackgroundPosition(($event.target as HTMLSelectElement).value as 'center' | 'top' | 'bottom' | 'left' | 'right')">
              <option value="center">{{ t('settings.bgPositionCenter') }}</option>
              <option value="top">{{ t('settings.bgPositionTop') }}</option>
              <option value="bottom">{{ t('settings.bgPositionBottom') }}</option>
              <option value="left">{{ t('settings.bgPositionLeft') }}</option>
              <option value="right">{{ t('settings.bgPositionRight') }}</option>
            </select>
          </div>
          <div class="bg-param">
            <UiLabel for="bg-repeat">{{ t('settings.bgRepeat') }}</UiLabel>
            <select id="bg-repeat" class="settings-select" :value="backgroundSettings.repeat" @change="setBackgroundRepeat(($event.target as HTMLSelectElement).value as 'repeat' | 'no-repeat' | 'repeat-x' | 'repeat-y')">
              <option value="repeat">{{ t('settings.bgRepeatRepeat') }}</option>
              <option value="no-repeat">{{ t('settings.bgRepeatNoRepeat') }}</option>
              <option value="repeat-x">{{ t('settings.bgRepeatRepeatX') }}</option>
              <option value="repeat-y">{{ t('settings.bgRepeatRepeatY') }}</option>
            </select>
          </div>
        </div>
        <UiButton variant="outline" size="sm" @click="resetBackground()">
          {{ t('settings.resetBackground') }}
        </UiButton>
      </div>
    </template>
  </div>
</template>
