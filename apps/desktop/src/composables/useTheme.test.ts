// @vitest-environment happy-dom
import { nextTick } from 'vue'
import { beforeEach, describe, expect, it } from 'vitest'
import { contrastRatio } from '../lib/monetExtract'
import { __resetDynamicPaletteForTests } from './useDynamicPalette'
import {
  __resetThemeForTests,
  ACCENT_COLORS,
  CUSTOM_ACCENT_KEY,
  DEFAULT_ACCENT,
  DYNAMIC_ACCENT_KEY,
  getCustomAccentHex,
  MONOCHROME_ACCENT_KEY,
  useTheme,
  WARM_ACCENT_KEY,
} from './useTheme'

const STORAGE_KEYS = [
  'tinadec-theme',
  'tinadec-accent-color',
  'tinadec-custom-accent',
  'tinadec-accent-migrated-monochrome',
  'tinadec-accent-migrated-lilac',
  'tinadec-dynamic-palette',
]

beforeEach(() => {
  localStorage.clear()
  for (const key of STORAGE_KEYS) localStorage.removeItem(key)
  document.documentElement.removeAttribute('data-theme')
  document.documentElement.removeAttribute('style')
  __resetThemeForTests()
  __resetDynamicPaletteForTests()
})

describe('useTheme monochrome default & palette pipeline', () => {
  it('defaults to monochrome and places monochrome and warm first in ACCENT_COLORS', () => {
    expect(DEFAULT_ACCENT).toBe('monochrome')
    expect(ACCENT_COLORS.slice(0, 2).map((c) => c.key)).toEqual([
      MONOCHROME_ACCENT_KEY,
      WARM_ACCENT_KEY,
    ])
    for (const chromatic of ['blue', 'green', 'orange', 'purple', 'pink', 'red', 'cyan', 'yellow']) {
      expect(ACCENT_COLORS.some((c) => c.key === chromatic)).toBe(true)
    }
  })

  it('applies pure neutral zinc monochrome tokens on fresh dark and light boot', async () => {
    const { accentColor, setTheme } = useTheme()
    const rootStyle = document.documentElement.style

    expect(accentColor.value).toBe('monochrome')
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
    expect(rootStyle.getPropertyValue('--bg-primary')).toBe('#09090b')
    expect(rootStyle.getPropertyValue('--accent-primary')).toBe('#fafafa')
    expect(rootStyle.getPropertyValue('--bg-primary-button')).toBe('#fafafa')
    expect(rootStyle.getPropertyValue('--text-on-primary')).toBe('#09090b')
    expect(rootStyle.getPropertyValue('--primary-button-fg')).toBe('#09090b')

    setTheme('light')
    await nextTick()

    expect(document.documentElement.getAttribute('data-theme')).toBe('light')
    expect(rootStyle.getPropertyValue('--bg-primary')).toBe('#ffffff')
    expect(rootStyle.getPropertyValue('--accent-primary')).toBe('#18181b')
    expect(rootStyle.getPropertyValue('--bg-primary-button')).toBe('#18181b')
    expect(rootStyle.getPropertyValue('--text-on-primary')).toBe('#ffffff')
    expect(rootStyle.getPropertyValue('--primary-button-fg')).toBe('#ffffff')
  })

  it('applies Anthropic Warm Ink & Paper tokens for the warm preset', async () => {
    const { setAccentColor, setTheme } = useTheme()
    const rootStyle = document.documentElement.style

    setAccentColor(WARM_ACCENT_KEY)
    await nextTick()

    expect(rootStyle.getPropertyValue('--bg-primary')).toBe('#141210')
    expect(rootStyle.getPropertyValue('--accent-brand')).toBe('#CA7554')
    expect(rootStyle.getPropertyValue('--bg-primary-button')).toBe('#f5f2eb')
    expect(rootStyle.getPropertyValue('--text-on-primary')).toBe('#141210')

    setTheme('light')
    await nextTick()

    expect(rootStyle.getPropertyValue('--bg-primary')).toBe('#f8f6f1')
    expect(rootStyle.getPropertyValue('--accent-brand')).toBe('#CA7554')
    expect(rootStyle.getPropertyValue('--bg-primary-button')).toBe('#1c1917')
    expect(rootStyle.getPropertyValue('--text-on-primary')).toBe('#ffffff')
  })

  it('strips monochrome-only overrides when switching to a chromatic Monet preset', async () => {
    const { setAccentColor } = useTheme()
    const rootStyle = document.documentElement.style

    expect(rootStyle.getPropertyValue('--surface-raised')).toBe('#18181b')
    expect(rootStyle.getPropertyValue('--text-link')).toBe('#e4e4e7')

    setAccentColor('blue')
    await nextTick()

    expect(rootStyle.getPropertyValue('--surface-raised')).toBe('')
    expect(rootStyle.getPropertyValue('--text-link')).toBe('')
    const btnBg = rootStyle.getPropertyValue('--bg-primary-button')
    const btnFg = rootStyle.getPropertyValue('--text-on-primary')
    expect(btnBg).toMatch(/^#[0-9a-f]{6}$/)
    expect(btnFg).toBe(rootStyle.getPropertyValue('--primary-button-fg'))
    expect(contrastRatio(btnBg, btnFg)).toBeGreaterThanOrEqual(4.5)
  })

  it('uses neutral #18181b as custom accent fallback instead of lilac', () => {
    expect(getCustomAccentHex().value).toBe('#18181b')
  })
})

describe('useTheme one-shot monochrome migration', () => {
  it('migrates legacy purple and blue defaults once to monochrome', () => {
    for (const legacy of ['purple', 'blue']) {
      localStorage.clear()
      __resetThemeForTests()
      localStorage.setItem('tinadec-accent-color', legacy)

      const { accentColor } = useTheme()
      expect(accentColor.value, `${legacy} should migrate once`).toBe('monochrome')
      expect(localStorage.getItem('tinadec-accent-migrated-monochrome')).toBe('1')
    }
  })

  it('preserves user-chosen non-default accents during migration', () => {
    for (const customPick of ['green', 'orange', 'warm', DYNAMIC_ACCENT_KEY, CUSTOM_ACCENT_KEY]) {
      localStorage.clear()
      __resetThemeForTests()
      localStorage.setItem('tinadec-accent-color', customPick)

      const { accentColor } = useTheme()
      expect(accentColor.value, `${customPick} must not be migrated`).toBe(customPick)
      expect(localStorage.getItem('tinadec-accent-migrated-monochrome')).toBe('1')
    }
  })

  it('does not overwrite purple or blue after the one-shot migration marker exists', () => {
    for (const rePicked of ['purple', 'blue']) {
      localStorage.clear()
      __resetThemeForTests()
      localStorage.setItem('tinadec-accent-migrated-monochrome', '1')
      localStorage.setItem('tinadec-accent-color', rePicked)

      const { accentColor } = useTheme()
      expect(accentColor.value, `post-migration ${rePicked} stays untouched`).toBe(rePicked)
    }
  })
})
