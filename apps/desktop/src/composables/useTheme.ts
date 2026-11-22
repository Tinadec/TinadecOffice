import { useStorage } from '@vueuse/core'
import { ref, watch, type Ref } from 'vue'
import {
  argbFromHex,
  buildDynamicVars,
  buildMonochromeVars,
  DYNAMIC_VAR_NAMES,
  isValidHexColor,
  type DynamicVars,
} from '../lib/monetExtract'
import { getDynamicPaletteRef } from './useDynamicPalette'

export type Theme = 'dark' | 'light' | 'system'

/** Built-in pure neutral zinc monochrome preset (shipped default). */
export const MONOCHROME_ACCENT_KEY = 'monochrome'

/** Built-in Anthropic Warm Ink & Paper minimalist preset. */
export const WARM_ACCENT_KEY = 'warm'

/** Pseudo accent key: colors derived from the image background (Monet). */
export const DYNAMIC_ACCENT_KEY = 'dynamic'

/** Pseudo accent key: colors derived from the user's own picked hex. */
export const CUSTOM_ACCENT_KEY = 'custom'

/** Seed used when `custom` is selected before a color was ever picked. */
const CUSTOM_ACCENT_FALLBACK = '#18181b'

/**
 * The shipped default. Pure zero-hue zinc monochrome; the CSS floor in
 * styles.css carries the identical dark/light neutral tokens so the first
 * paint matches before runtime token injection runs.
 */
export const DEFAULT_ACCENT = MONOCHROME_ACCENT_KEY

/**
 * Earlier builds persisted the old shipped defaults (`blue`, then `purple`) into
 * localStorage on first boot, so changing `DEFAULT_ACCENT` alone would leave
 * existing installs on those retired defaults. This one-shot marker migrates
 * `purple` / `blue` once to `monochrome`, leaving `dynamic`, `custom`, `green`,
 * `orange`, `warm`, or any post-migration manual pick untouched.
 */
const ACCENT_MIGRATION_KEY = 'tinadec-accent-migrated-monochrome'
const LEGACY_DEFAULT_ACCENTS = new Set(['purple', 'blue'])

export interface AccentColor {
  key: string
  labelKey: string
  /** Optional diagonal dark/light split swatch `[darkHalf, lightHalf]` for neutral presets. */
  swatchSplit?: [string, string]
  dark: {
    accentPrimary: string
    accentBrand: string
    textBrand: string
    borderInputFocus: string
    shadowFocus: string
  }
  light: {
    accentPrimary: string
    accentBrand: string
    textBrand: string
    borderInputFocus: string
    shadowFocus: string
  }
}

export const ACCENT_COLORS: AccentColor[] = [
  {
    key: MONOCHROME_ACCENT_KEY,
    labelKey: 'accentColors.monochrome',
    swatchSplit: ['#18181b', '#fafafa'],
    dark: {
      accentPrimary: '#fafafa',
      accentBrand: '#fafafa',
      textBrand: '#fafafa',
      borderInputFocus: '#a1a1aa',
      shadowFocus: '0 0 0 2px rgba(250, 250, 250, 0.24)',
    },
    light: {
      accentPrimary: '#18181b',
      accentBrand: '#18181b',
      textBrand: '#18181b',
      borderInputFocus: '#18181b',
      shadowFocus: '0 0 0 2px rgba(24, 24, 27, 0.16)',
    },
  },
  {
    key: WARM_ACCENT_KEY,
    labelKey: 'accentColors.warm',
    swatchSplit: ['#1c1917', '#f1e7da'],
    dark: {
      accentPrimary: '#e7e2d8',
      accentBrand: '#CA7554',
      textBrand: '#ca7554',
      borderInputFocus: '#b8afa2',
      shadowFocus: '0 0 0 2px rgba(231, 226, 216, 0.24)',
    },
    light: {
      accentPrimary: '#1c1917',
      accentBrand: '#CA7554',
      textBrand: '#ca7554',
      borderInputFocus: '#1c1917',
      shadowFocus: '0 0 0 2px rgba(28, 25, 23, 0.16)',
    },
  },
  {
    key: 'blue',
    labelKey: 'accentColors.blue',
    dark: {
      accentPrimary: '#58a6ff',
      accentBrand: '#58a6ff',
      textBrand: '#58a6ff',
      borderInputFocus: '#58a6ff',
      shadowFocus: '0 0 0 3px rgba(88, 166, 255, 0.3)',
    },
    light: {
      accentPrimary: '#0969da',
      accentBrand: '#1f6feb',
      textBrand: '#0969da',
      borderInputFocus: '#0969da',
      shadowFocus: '0 0 0 3px rgba(9, 105, 218, 0.14)',
    },
  },
  {
    key: 'green',
    labelKey: 'accentColors.green',
    dark: {
      accentPrimary: '#3fb950',
      accentBrand: '#3fb950',
      textBrand: '#3fb950',
      borderInputFocus: '#3fb950',
      shadowFocus: '0 0 0 3px rgba(63, 185, 80, 0.3)',
    },
    light: {
      accentPrimary: '#1a7f37',
      accentBrand: '#1a7f37',
      textBrand: '#1a7f37',
      borderInputFocus: '#1a7f37',
      shadowFocus: '0 0 0 3px rgba(26, 127, 55, 0.14)',
    },
  },
  {
    key: 'orange',
    labelKey: 'accentColors.orange',
    dark: {
      accentPrimary: '#f0883e',
      accentBrand: '#f0883e',
      textBrand: '#f0883e',
      borderInputFocus: '#f0883e',
      shadowFocus: '0 0 0 3px rgba(240, 136, 62, 0.3)',
    },
    light: {
      accentPrimary: '#bc4c00',
      accentBrand: '#bc4c00',
      textBrand: '#953800',
      borderInputFocus: '#bc4c00',
      shadowFocus: '0 0 0 3px rgba(188, 76, 0, 0.14)',
    },
  },
  {
    key: 'purple',
    labelKey: 'accentColors.purple',
    dark: {
      accentPrimary: '#d2bdf5',
      accentBrand: '#d2bdf5',
      textBrand: '#d2bdf5',
      borderInputFocus: '#d2bdf5',
      shadowFocus: '0 0 0 3px rgba(210, 189, 245, 0.3)',
    },
    light: {
      accentPrimary: '#675687',
      accentBrand: '#675687',
      textBrand: '#675687',
      borderInputFocus: '#675687',
      shadowFocus: '0 0 0 3px rgba(103, 86, 135, 0.14)',
    },
  },
  {
    key: 'pink',
    labelKey: 'accentColors.pink',
    dark: {
      accentPrimary: '#f778ba',
      accentBrand: '#f778ba',
      textBrand: '#f778ba',
      borderInputFocus: '#f778ba',
      shadowFocus: '0 0 0 3px rgba(247, 120, 186, 0.3)',
    },
    light: {
      accentPrimary: '#bf3989',
      accentBrand: '#bf3989',
      textBrand: '#953074',
      borderInputFocus: '#bf3989',
      shadowFocus: '0 0 0 3px rgba(191, 57, 137, 0.14)',
    },
  },
  {
    key: 'red',
    labelKey: 'accentColors.red',
    dark: {
      accentPrimary: '#f85149',
      accentBrand: '#f85149',
      textBrand: '#f85149',
      borderInputFocus: '#f85149',
      shadowFocus: '0 0 0 3px rgba(248, 81, 73, 0.3)',
    },
    light: {
      accentPrimary: '#cf222e',
      accentBrand: '#cf222e',
      textBrand: '#a40e26',
      borderInputFocus: '#cf222e',
      shadowFocus: '0 0 0 3px rgba(207, 34, 46, 0.14)',
    },
  },
  {
    key: 'cyan',
    labelKey: 'accentColors.cyan',
    dark: {
      accentPrimary: '#56d4dd',
      accentBrand: '#56d4dd',
      textBrand: '#56d4dd',
      borderInputFocus: '#56d4dd',
      shadowFocus: '0 0 0 3px rgba(86, 212, 221, 0.3)',
    },
    light: {
      accentPrimary: '#087990',
      accentBrand: '#087990',
      textBrand: '#065975',
      borderInputFocus: '#087990',
      shadowFocus: '0 0 0 3px rgba(8, 121, 144, 0.14)',
    },
  },
  {
    key: 'yellow',
    labelKey: 'accentColors.yellow',
    dark: {
      accentPrimary: '#d29922',
      accentBrand: '#d29922',
      textBrand: '#d29922',
      borderInputFocus: '#d29922',
      shadowFocus: '0 0 0 3px rgba(210, 153, 34, 0.3)',
    },
    light: {
      accentPrimary: '#9a6700',
      accentBrand: '#9a6700',
      textBrand: '#7c5200',
      borderInputFocus: '#9a6700',
      shadowFocus: '0 0 0 3px rgba(154, 103, 0, 0.14)',
    },
  },
]

// 使用 ref 延迟初始化，避免在模块加载时访问 localStorage
let stored: Ref<Theme> | null = null
let storedAccentColor: Ref<string> | null = null
let storedCustomAccent: Ref<string> | null = null

function getStoredTheme(): Ref<Theme> {
  if (!stored) {
    stored = useStorage<Theme>('tinadec-theme', 'dark')
  }
  return stored
}

function getStoredAccentColor(): Ref<string> {
  if (!storedAccentColor) {
    const ref0 = useStorage<string>('tinadec-accent-color', DEFAULT_ACCENT)
    try {
      if (!localStorage.getItem(ACCENT_MIGRATION_KEY)) {
        localStorage.setItem(ACCENT_MIGRATION_KEY, '1')
        // Only the values previous builds wrote as shipped defaults are migrated
        // once — users who picked dynamic/custom/green/orange/warm stay untouched.
        if (LEGACY_DEFAULT_ACCENTS.has(ref0.value)) ref0.value = DEFAULT_ACCENT
      }
    } catch {
      // Private mode / storage disabled: the stored value still wins.
    }
    storedAccentColor = ref0
  }
  return storedAccentColor
}

/** The user's own accent hex; only consulted when the key is `custom`. */
export function getCustomAccentHex(): Ref<string> {
  if (!storedCustomAccent) {
    const ref0 = useStorage<string>('tinadec-custom-accent', CUSTOM_ACCENT_FALLBACK)
    if (!isValidHexColor(ref0.value)) ref0.value = CUSTOM_ACCENT_FALLBACK
    storedCustomAccent = ref0
  }
  return storedCustomAccent
}

/**
 * Test-only: drop the module-level storage singletons so each case re-reads
 * localStorage. Without this, the first `useTheme()` in a file pins the refs
 * and later cases observe the previous case's values.
 */
export function __resetThemeForTests(): void {
  stored = null
  storedAccentColor = null
  storedCustomAccent = null
}

/**
 * Chromatic preset seeds feeding the Monet tonal builder. Monochrome & Warm
 * bypass Monet completely via `buildMonochromeVars`.
 */
const PRESET_SEEDS: Record<string, number> = Object.fromEntries(
  ACCENT_COLORS
    .filter((c) => c.key !== MONOCHROME_ACCENT_KEY && c.key !== WARM_ACCENT_KEY)
    .map((c) => [c.key, argbFromHex(c.dark.accentPrimary)]),
)

/** Presets are pure functions of (key, theme) — build each combo once. */
const presetVarsCache = new Map<string, DynamicVars>()

function resolveAccentVars(key: string, theme: 'dark' | 'light'): DynamicVars | null {
  if (key === MONOCHROME_ACCENT_KEY) {
    const cacheKey = `${key}:${theme}`
    let vars = presetVarsCache.get(cacheKey)
    if (!vars) {
      vars = buildMonochromeVars(theme, 'neutral')
      presetVarsCache.set(cacheKey, vars)
    }
    return vars
  }
  if (key === WARM_ACCENT_KEY) {
    const cacheKey = `${key}:${theme}`
    let vars = presetVarsCache.get(cacheKey)
    if (!vars) {
      vars = buildMonochromeVars(theme, 'warm')
      presetVarsCache.set(cacheKey, vars)
    }
    return vars
  }
  if (key === DYNAMIC_ACCENT_KEY) {
    // Not memoized: a fresh extraction must take effect immediately.
    // Before the first extraction lands (no wallpaper, or still booting), fall
    // back to neutral monochrome rather than a hardcoded chromatic hue.
    const sourceColor = getDynamicPaletteRef().value?.sourceColor
    if (typeof sourceColor === 'number') {
      return buildDynamicVars(sourceColor >>> 0, theme)
    }
    return buildMonochromeVars(theme, 'neutral')
  }
  if (key === CUSTOM_ACCENT_KEY) {
    // Not memoized: the seed changes every time the user moves a slider.
    return buildDynamicVars(argbFromHex(getCustomAccentHex().value), theme)
  }
  const cacheKey = `${key}:${theme}`
  let vars = presetVarsCache.get(cacheKey)
  if (!vars) {
    const seed = PRESET_SEEDS[key]
    vars = seed !== undefined ? buildDynamicVars(seed, theme) : buildMonochromeVars(theme, 'neutral')
    presetVarsCache.set(cacheKey, vars)
  }
  return vars
}

function applyTheme(theme: Theme) {
  let resolved: 'dark' | 'light'
  if (theme === 'system') {
    resolved = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  } else {
    resolved = theme
  }
  document.documentElement.setAttribute('data-theme', resolved)
}

function applyAccentColor(colorKey: string) {
  const resolved = document.documentElement.getAttribute('data-theme') as 'dark' | 'light' ?? 'dark'
  const root = document.documentElement

  removeDynamicVars(root)
  const vars = resolveAccentVars(colorKey, resolved)
  if (!vars) return
  for (const [name, value] of Object.entries(vars)) {
    root.style.setProperty(name, value)
  }
}

function removeDynamicVars(root: HTMLElement) {
  for (const name of DYNAMIC_VAR_NAMES) {
    root.style.removeProperty(name)
  }
}

export function useTheme() {
  const themeRef = getStoredTheme()
  const accentColorRef = getStoredAccentColor()
  const customAccentRef = getCustomAccentHex()

  function applyInitialTheme() {
    applyTheme(themeRef.value)
    applyAccentColor(accentColorRef.value)
  }

  // 立即应用主题
  applyInitialTheme()

  watch(themeRef, (val) => {
    applyTheme(val)
    applyAccentColor(accentColorRef.value)
  })

  watch(accentColorRef, (val) => {
    applyAccentColor(val)
  })

  // Re-apply when a new extraction lands while "follow background" is on.
  watch(getDynamicPaletteRef(), () => {
    if (accentColorRef.value === DYNAMIC_ACCENT_KEY) {
      applyAccentColor(DYNAMIC_ACCENT_KEY)
    }
  })

  // Re-apply when the custom hex changes while "custom" is on.
  watch(customAccentRef, () => {
    if (accentColorRef.value === CUSTOM_ACCENT_KEY) {
      applyAccentColor(CUSTOM_ACCENT_KEY)
    }
  })

  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (themeRef.value === 'system') {
      applyTheme('system')
      applyAccentColor(accentColorRef.value)
    }
  })

  return {
    theme: themeRef,
    setTheme: (t: Theme) => {
      themeRef.value = t
    },
    accentColor: accentColorRef,
    setAccentColor: (key: string) => {
      accentColorRef.value = key
    },
    /** The custom hex ref — bind the picker to it and previews stay live. */
    customAccent: customAccentRef,
    setCustomAccent: (hex: string) => {
      if (isValidHexColor(hex)) customAccentRef.value = hex.toLowerCase()
    },
    accentColors: ACCENT_COLORS,
    applyInitialTheme,
  }
}
