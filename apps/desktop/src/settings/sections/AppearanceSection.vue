<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Moon, Monitor, Pipette, Plus, Sun } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import BackgroundPreview from '@/components/ui/background-preview.vue'
import PanelStyleControl from '@/components/ui/panel-style-control.vue'
import { CUSTOM_ACCENT_KEY, DYNAMIC_ACCENT_KEY, useTheme } from '@/composables/useTheme'
import { getDynamicPaletteRef } from '@/composables/useDynamicPalette'
import {
  contrastRatio,
  hexFromHsl,
  hslFromHex,
  isValidHexColor,
  previewSwatches,
} from '@/lib/monetExtract'
import { useBackground } from '@/composables/useBackground'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useNotifications } from '@/composables/useNotifications'

/**
 * Appearance section (D7.2, relaid 2026-09).
 *
 * Owns: theme/accent selection (with cross-window broadcast), custom accent
 * picking, global panel material control, and the custom background manager.
 * useBackground / usePanelStyles are module-level singletons shared with
 * App.vue, so this component mutates the same global state the shell reads.
 *
 * Layout contract: four grouped cards in causal order — theme, accent,
 * material, background — each following "title → hint → controls". The
 * background preview sits next to its own parameters so adjusting a slider
 * does not move the thing you are looking at.
 */
const { t } = useI18n()
const {
  theme,
  setTheme,
  accentColor,
  setAccentColor,
  customAccent,
  setCustomAccent,
  accentColors,
} = useTheme()

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

const isHtmlBackground = computed(() => backgroundSettings.value.type === 'html')

/** Format hint per background type — HTML is markup, not a media file. */
const sourceHint = computed(() => {
  switch (backgroundSettings.value.type) {
    case 'image':
      return t('settings.bgImageFormats')
    case 'video':
      return t('settings.bgVideoFormats')
    case 'html':
      return t('settings.bgHtmlHint')
    default:
      return ''
  }
})

function changeTheme(newTheme: 'dark' | 'light' | 'system'): void {
  setTheme(newTheme)
  window.tinadec?.broadcastTheme?.(newTheme, accentColor.value)
}

function changeAccentColor(key: string): void {
  setAccentColor(key)
  window.tinadec?.broadcastTheme?.(theme.value, key)
}

// ── Custom accent picker ────────────────────────────────────────────────
// Draft state so dragging a slider previews live but only "应用" commits.
// `draftHex` is the source of truth for the color: the sliders are a rounded
// *view* of it (integer steps), so typing a hex keeps the exact value instead
// of being re-derived through lossy slider positions.
const pickerOpen = ref(false)
const pickerAnchor = ref<HTMLElement | null>(null)
const draftHue = ref(0)
const draftSat = ref(0)
const draftLig = ref(0)
const draftHex = ref('#58a6ff')
const canUseEyeDropper = typeof window !== 'undefined' && 'EyeDropper' in window

/** Sliders moved → recompute the hex from their (rounded) positions. */
const sliderColor = computed(() => hexFromHsl(draftHue.value, draftSat.value, draftLig.value))
/**
 * Guards the slider → hex sync. `openPicker` and `onHexInput` set the sliders
 * themselves; without this flag the watcher would immediately rewrite the hex
 * with the *rounded* slider value and destroy a typed/seeded color
 * (#123456 became #123354).
 */
let syncingFromSliders = false
const draftSwatches = computed(() =>
  previewSwatches((Number.parseInt(draftHex.value.slice(1), 16) | 0xff000000) >>> 0),
)
/** Contrast of the drafted accent against the page surface it will sit on. */
const draftContrast = computed(() => {
  const surface = resolvedTheme.value === 'dark' ? '#0a0e14' : '#ffffff'
  return contrastRatio(draftHex.value, surface)
})
const contrastWarning = computed(() => draftContrast.value < 4.5)

function openPicker(): void {
  const [h, s, l] = hslFromHex(customAccent.value)
  syncingFromSliders = false
  draftHue.value = h
  draftSat.value = s
  draftLig.value = l
  draftHex.value = customAccent.value
  pickerOpen.value = true
}

function closePicker(): void {
  pickerOpen.value = false
}

/** Slider input → keep the hex field in sync with the slider positions. */
watch([draftHue, draftSat, draftLig], () => {
  if (!syncingFromSliders) return
  syncingFromSliders = false
  draftHex.value = sliderColor.value
})

function onHexInput(value: string): void {
  draftHex.value = value
  if (!isValidHexColor(value)) return
  const [h, s, l] = hslFromHex(value)
  // Programmatic slider update — must not bounce back into draftHex.
  syncingFromSliders = false
  draftHue.value = h
  draftSat.value = s
  draftLig.value = l
}

function onNativeColorInput(value: string): void {
  onHexInput(value)
}

/** A slider the user actually dragged: allow the rounded value to win. */
function onSliderInput(): void {
  syncingFromSliders = true
}

async function pickFromScreen(): Promise<void> {
  const Ctor = (window as unknown as { EyeDropper?: new () => { open: () => Promise<{ sRGBHex: string }> } }).EyeDropper
  if (!Ctor) return
  try {
    const result = await new Ctor().open()
    onHexInput(result.sRGBHex)
  } catch {
    /* user cancelled the eyedropper — leave the draft untouched */
  }
}

function applyCustomAccent(): void {
  // Commit the exact hex the user sees, not a value re-derived from sliders.
  const committed = isValidHexColor(draftHex.value) ? draftHex.value : sliderColor.value
  setCustomAccent(committed)
  changeAccentColor(CUSTOM_ACCENT_KEY)
  pickerOpen.value = false
}

function onDocumentPointerDown(event: PointerEvent): void {
  if (!pickerOpen.value) return
  if (pickerAnchor.value?.contains(event.target as Node)) return
  pickerOpen.value = false
}

onMounted(() => document.addEventListener('pointerdown', onDocumentPointerDown))
onBeforeUnmount(() => document.removeEventListener('pointerdown', onDocumentPointerDown))

// ── Background ──────────────────────────────────────────────────────────
const notifications = useNotifications()

async function browseForBackground(): Promise<void> {
  await selectBackgroundFile()
}

function resetBackgroundToDefault(): void {
  resetBackground()
  notifications.notify.success({ message: t('settings.resetBackground'), source: 'settings' })
}
</script>

<template>
  <div class="appearance-section">
    <h2>{{ t('settings.appearance') }}</h2>
    <p class="appearance-lead">{{ t('settings.appearanceLead') }}</p>

    <!-- ── 1. Theme ─────────────────────────────────────────────────── -->
    <section class="appearance-group">
      <div class="appearance-group-head">
        <h3>{{ t('settings.theme') }}</h3>
        <p>{{ t('settings.themeHint') }}</p>
      </div>
      <div class="theme-options" data-testid="theme-options">
        <button :class="['theme-option', { active: theme === 'dark' }]" @click="changeTheme('dark')">
          <Moon :size="16" />
          {{ t('settings.dark') }}
        </button>
        <button :class="['theme-option', { active: theme === 'light' }]" @click="changeTheme('light')">
          <Sun :size="16" />
          {{ t('settings.light') }}
        </button>
        <button :class="['theme-option', { active: theme === 'system' }]" @click="changeTheme('system')">
          <Monitor :size="16" />
          {{ t('settings.system') }}
        </button>
      </div>
    </section>

    <!-- ── 2. Accent ────────────────────────────────────────────────── -->
    <section class="appearance-group">
      <div class="appearance-group-head">
        <h3>{{ t('settings.accentColor') }}</h3>
        <p>{{ t('settings.accentColorHint') }}</p>
      </div>

      <div class="accent-color-grid" data-testid="accent-colors">
        <button
          v-for="color in accentColors"
          :key="color.key"
          :class="['accent-color-swatch', { active: accentColor === color.key }]"
          :style="{ '--swatch-color': color.dark.accentPrimary }"
          :title="t(color.labelKey)"
          :aria-label="t(color.labelKey)"
          @click="changeAccentColor(color.key)"
        >
          <span class="accent-color-dot"></span>
          <Check v-if="accentColor === color.key" :size="14" class="accent-color-check" />
        </button>

        <!-- Custom color entry + popover -->
        <span ref="pickerAnchor" class="custom-accent-anchor">
          <button
            :class="['accent-color-swatch', 'accent-color-swatch--custom', { active: accentColor === CUSTOM_ACCENT_KEY }]"
            :style="{ '--swatch-color': customAccent }"
            :title="t('settings.accentCustom')"
            :aria-label="t('settings.accentCustom')"
            data-testid="accent-custom"
            @click="openPicker()"
          >
            <span class="accent-color-dot"></span>
            <Plus v-if="accentColor !== CUSTOM_ACCENT_KEY" :size="13" class="custom-accent-plus" />
            <Check v-else :size="14" class="accent-color-check" />
          </button>

          <div v-if="pickerOpen" class="custom-accent-pop" data-testid="custom-accent-pop">
            <div class="custom-accent-row">
              <label class="appearance-label" for="ca-hue">{{ t('settings.hue') }}</label>
              <input id="ca-hue" class="param-slider" type="range" min="0" max="360" step="1" v-model.number="draftHue" @input="onSliderInput()" />
              <span class="custom-accent-value">{{ draftHue }}°</span>
            </div>
            <div class="custom-accent-row">
              <label class="appearance-label" for="ca-sat">{{ t('settings.saturation') }}</label>
              <input id="ca-sat" class="param-slider" type="range" min="0" max="100" step="1" v-model.number="draftSat" @input="onSliderInput()" />
              <span class="custom-accent-value">{{ draftSat }}%</span>
            </div>
            <div class="custom-accent-row">
              <label class="appearance-label" for="ca-lig">{{ t('settings.lightness') }}</label>
              <input id="ca-lig" class="param-slider" type="range" min="0" max="100" step="1" v-model.number="draftLig" @input="onSliderInput()" />
              <span class="custom-accent-value">{{ draftLig }}%</span>
            </div>

            <div class="custom-accent-hex">
              <input
                class="custom-accent-native"
                type="color"
                :value="draftHex"
                :aria-label="t('settings.accentCustom')"
                @input="onNativeColorInput(($event.target as HTMLInputElement).value)"
              />
              <input
                class="custom-accent-hexfield"
                type="text"
                spellcheck="false"
                :value="draftHex"
                @input="onHexInput(($event.target as HTMLInputElement).value)"
              />
              <button
                v-if="canUseEyeDropper"
                class="custom-accent-eyedrop"
                type="button"
                :title="t('settings.pickFromScreen')"
                :aria-label="t('settings.pickFromScreen')"
                @click="pickFromScreen()"
              >
                <Pipette :size="14" />
              </button>
            </div>

            <div class="custom-accent-preview">
              <span
                v-for="(swatch, i) in draftSwatches"
                :key="i"
                class="custom-accent-preview-dot"
                :style="{ background: swatch }"
              ></span>
            </div>
            <p v-if="contrastWarning" class="custom-accent-warning" data-testid="contrast-warning">
              {{ t('settings.accentLowContrast') }}
            </p>

            <div class="custom-accent-actions">
              <UiButton variant="outline" size="sm" @click="closePicker()">{{ t('settings.cancel') }}</UiButton>
              <UiButton size="sm" @click="applyCustomAccent()">{{ t('settings.apply') }}</UiButton>
            </div>
          </div>
        </span>
      </div>

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
      <p v-if="isFrozen" class="accent-color-hint" data-testid="dynamic-frozen-hint">
        {{ t('settings.accentDynamicFrozen') }}
      </p>
    </section>

    <!-- ── 3. Material ──────────────────────────────────────────────── -->
    <section class="appearance-group">
      <div class="appearance-group-head">
        <h3>{{ t('settings.globalMaterial') }}</h3>
        <p>{{ t('settings.globalMaterialHint') }}</p>
      </div>
      <div class="panel-styles-grid">
        <PanelStyleControl
          :label="t('settings.globalMaterial')"
          :settings="panelStyle"
          @update="updatePanelStyle($event)"
        />
        <UiButton variant="outline" size="sm" class="panel-styles-reset" @click="resetPanelStyle()">
          {{ t('settings.resetPanelStyles') }}
        </UiButton>
      </div>
    </section>

    <!-- ── 4. Background ────────────────────────────────────────────── -->
    <section class="appearance-group">
      <div class="appearance-group-head">
        <h3>{{ t('settings.background') }}</h3>
        <p>{{ t('settings.backgroundHint') }}</p>
      </div>

      <div class="background-type-options" data-testid="background-types">
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

      <div v-if="backgroundSettings.type !== 'none'" class="bg-editor">
        <!-- Preview stays beside the controls it reflects. -->
        <BackgroundPreview :settings="backgroundSettings" :height="168" />

        <div class="bg-editor-fields">
          <div class="background-source-section">
            <h4 class="bg-group-title">{{ t('settings.backgroundSource') }}</h4>
            <div v-if="isHtmlBackground" class="source-input-row">
              <textarea
                class="bg-html-input"
                spellcheck="false"
                :value="backgroundSource"
                :placeholder="t('settings.bgSourcePlaceholder')"
                @input="setBackgroundSource(($event.target as HTMLTextAreaElement).value)"
              ></textarea>
            </div>
            <div v-else class="source-input-row">
              <input
                class="source-input bg-source-input"
                type="text"
                :value="backgroundSource"
                :placeholder="t('settings.bgSourcePlaceholder')"
                @input="setBackgroundSource(($event.target as HTMLInputElement).value)"
              />
              <UiButton variant="outline" size="sm" @click="browseForBackground()">
                {{ t('settings.browse') }}
              </UiButton>
            </div>
            <p class="bg-format-hint">{{ sourceHint }}</p>
          </div>

          <div class="bg-params">
            <h4 class="bg-group-title">{{ t('settings.backgroundParams') }}</h4>
            <div class="bg-param-row">
              <label class="appearance-label" for="bg-opacity">{{ t('settings.opacity') }}</label>
              <input
                id="bg-opacity"
                class="param-slider"
                type="range"
                min="0"
                max="100"
                step="1"
                :value="backgroundSettings.opacity"
                @input="setBackgroundOpacity(Number(($event.target as HTMLInputElement).value))"
              />
              <span class="param-value">{{ Math.round(backgroundSettings.opacity) }}%</span>
            </div>
            <div class="bg-param-row">
              <label class="appearance-label" for="bg-blur">{{ t('settings.blur') }}</label>
              <input
                id="bg-blur"
                class="param-slider"
                type="range"
                min="0"
                max="20"
                step="1"
                :value="backgroundSettings.blur"
                @input="setBackgroundBlur(Number(($event.target as HTMLInputElement).value))"
              />
              <span class="param-value">{{ backgroundSettings.blur }}px</span>
            </div>

            <div class="bg-param-row bg-param-row--selects">
              <div class="bg-param">
                <label class="appearance-label" for="bg-size">{{ t('settings.bgSize') }}</label>
                <select id="bg-size" class="settings-select" :value="backgroundSettings.size" @change="setBackgroundSize(($event.target as HTMLSelectElement).value as 'cover' | 'contain' | 'auto')">
                  <option value="cover">{{ t('settings.bgSizeCover') }}</option>
                  <option value="contain">{{ t('settings.bgSizeContain') }}</option>
                  <option value="auto">{{ t('settings.bgSizeAuto') }}</option>
                </select>
              </div>
              <div class="bg-param">
                <label class="appearance-label" for="bg-position">{{ t('settings.bgPosition') }}</label>
                <select id="bg-position" class="settings-select" :value="backgroundSettings.position" @change="setBackgroundPosition(($event.target as HTMLSelectElement).value as 'center' | 'top' | 'bottom' | 'left' | 'right')">
                  <option value="center">{{ t('settings.bgPositionCenter') }}</option>
                  <option value="top">{{ t('settings.bgPositionTop') }}</option>
                  <option value="bottom">{{ t('settings.bgPositionBottom') }}</option>
                  <option value="left">{{ t('settings.bgPositionLeft') }}</option>
                  <option value="right">{{ t('settings.bgPositionRight') }}</option>
                </select>
              </div>
              <div class="bg-param">
                <label class="appearance-label" for="bg-repeat">{{ t('settings.bgRepeat') }}</label>
                <select id="bg-repeat" class="settings-select" :value="backgroundSettings.repeat" @change="setBackgroundRepeat(($event.target as HTMLSelectElement).value as 'repeat' | 'no-repeat' | 'repeat-x' | 'repeat-y')">
                  <option value="repeat">{{ t('settings.bgRepeatRepeat') }}</option>
                  <option value="no-repeat">{{ t('settings.bgRepeatNoRepeat') }}</option>
                  <option value="repeat-x">{{ t('settings.bgRepeatRepeatX') }}</option>
                  <option value="repeat-y">{{ t('settings.bgRepeatRepeatY') }}</option>
                </select>
              </div>
            </div>

            <div class="bg-actions">
              <UiButton variant="outline" size="sm" @click="resetBackgroundToDefault()">
                {{ t('settings.resetBackground') }}
              </UiButton>
            </div>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>
