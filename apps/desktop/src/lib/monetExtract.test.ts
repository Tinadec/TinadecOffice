import { describe, expect, it } from 'vitest'
import {
  argbFromHex,
  buildDynamicVars,
  buildMonochromeVars,
  contrastForeground,
  contrastRatio,
  DYNAMIC_VAR_NAMES,
  extractSourceColor,
  hexFromArgbInt,
  hexFromHsl,
  hslFromHex,
  isValidHexColor,
  previewSwatches,
  relativeLuminance,
} from './monetExtract'

/** Pack an opaque ARGB int (unsigned). */
function argb(r: number, g: number, b: number): number {
  return ((0xff << 24) | (r << 16) | (g << 8) | b) >>> 0
}

describe('extractSourceColor', () => {
  it('returns null for empty pixel lists', () => {
    expect(extractSourceColor([])).toBeNull()
  })

  it('picks a color from the dominant hue family (Monet score)', () => {
    // A sea of teal with a tiny red patch: source must stay in the teal family.
    const pixels = [
      ...Array.from({ length: 500 }, () => argb(46, 196, 182)),
      ...Array.from({ length: 5 }, () => argb(220, 40, 40)),
    ]
    const source = extractSourceColor(pixels)
    expect(source).not.toBeNull()
    const r = source! >> 16 & 0xff
    const g = source! >> 8 & 0xff
    // Teal family: green channel dominates red.
    expect(g).toBeGreaterThan(r)
  })

  it('is deterministic for identical inputs', () => {
    const pixels = Array.from({ length: 64 }, (_, i) => argb(i * 4 % 256, 100 + i % 50, 200 - i))
    expect(extractSourceColor(pixels)).toBe(extractSourceColor(pixels))
  })

  it('returns null for alpha-less pixels (Wu skips transparent entries)', () => {
    // Regression: the canvas packer once dropped the 0xff<<24 byte, making
    // every pixel transparent and extraction permanently fail.
    const alphaless = Array.from({ length: 256 }, (_, i) => (i * 7 % 256) << 16 | 200 << 8 | 180)
    expect(extractSourceColor(alphaless)).toBeNull()
  })
})

describe('buildDynamicVars', () => {
  const teal = argb(46, 196, 182)

  it('produces dark surfaces near the app tone ladder and light surfaces near white', () => {
    const dark = buildDynamicVars(teal, 'dark')
    const light = buildDynamicVars(teal, 'light')

    const luminance = (hex: string): number => {
      const n = Number.parseInt(hex.slice(1), 16)
      return ((n >> 16 & 0xff) + (n >> 8 & 0xff) + (n & 0xff)) / 3
    }
    // Dark theme bg-primary stays very dark; light stays near-white.
    expect(luminance(dark['--bg-primary']!)).toBeLessThan(30)
    expect(luminance(light['--bg-primary']!)).toBeGreaterThan(220)
    // Surfaces ascend monotonically within each theme.
    const d = (h: string) => luminance(h)
    expect(d(dark['--bg-secondary']!)).toBeGreaterThanOrEqual(d(dark['--bg-primary']!))
    expect(d(dark['--bg-tertiary']!)).toBeGreaterThanOrEqual(d(dark['--bg-secondary']!))
    expect(d(light['--bg-tertiary']!)).toBeLessThanOrEqual(d(light['--bg-secondary']!))
  })

  it('carries the source hue into accent identity and surface tint', () => {
    const vars = buildDynamicVars(teal, 'dark')
    const hexHue = (hex: string): number => {
      const n = Number.parseInt(hex.slice(1), 16)
      const r = (n >> 16 & 0xff) / 255
      const g = (n >> 8 & 0xff) / 255
      const b = (n & 0xff) / 255
      const max = Math.max(r, g, b)
      const min = Math.min(r, g, b)
      const d = max - min
      if (d === 0) return 0
      if (max === r) return ((g - b) / d + 6) % 6 * 60
      if (max === g) return (b - r) / d * 60 + 120
      return (r - g) / d * 60 + 240
    }
    const accent = vars['--accent-primary']!
    // Teal source ≈ 174° — accent must stay in the same half of the wheel.
    expect(Math.abs(hexHue(accent) - hexHue('#2ec4b6'))).toBeLessThan(45)
    // Neutral surfaces are tinted toward the hue too (not pure gray).
    expect(Math.abs(hexHue(vars['--bg-secondary']!) - hexHue('#2ec4b6'))).toBeLessThan(45)
  })

  it('keeps semantic solids out of the dynamic set', () => {
    const names = DYNAMIC_VAR_NAMES
    for (const forbidden of [
      '--text-error', '--accent-danger', '--bg-status-ok', '--border-error',
      // Success/approval are status colors: they must read the same green
      // regardless of the chosen accent, so they are never emitted here.
      '--accent-success', '--text-approve',
    ]) {
      expect(names).not.toContain(forbidden)
    }
  })

  it('leaves success and approval colors untouched for any accent', () => {
    for (const seed of [argb(46, 196, 182), argb(88, 166, 255), argb(248, 81, 73)]) {
      for (const theme of ['dark', 'light'] as const) {
        const vars = buildDynamicVars(seed, theme)
        expect(vars['--accent-success']).toBeUndefined()
        expect(vars['--text-approve']).toBeUndefined()
      }
    }
  })

  it('emits the panel-material rgb triplets and focus ring', () => {
    const dark = buildDynamicVars(teal, 'dark')
    expect(dark['--bg-primary-rgb']).toMatch(/^\d+, \d+, \d+$/)
    expect(dark['--shadow-focus']).toMatch(/^0 0 0 2px rgba\(\d+, \d+, \d+, /)
    expect(dark['--bg-immersive']).toContain('rgba(')
  })

  it('covers the identical property name set across themes', () => {
    expect(Object.keys(buildDynamicVars(teal, 'dark')).sort()).toEqual(
      Object.keys(buildDynamicVars(teal, 'light')).sort(),
    )
  })

  it('emits the shadcn token family as HSL triplets', () => {
    const hexHue = (hex: string): number => {
      const n = Number.parseInt(hex.slice(1), 16)
      const r = (n >> 16 & 0xff) / 255
      const g = (n >> 8 & 0xff) / 255
      const b = (n & 0xff) / 255
      const max = Math.max(r, g, b)
      const min = Math.min(r, g, b)
      const d = max - min
      if (d === 0) return 0
      if (max === r) return ((g - b) / d + 6) % 6 * 60
      if (max === g) return (b - r) / d * 60 + 120
      return (r - g) / d * 60 + 240
    }
    for (const theme of ['dark', 'light'] as const) {
      const vars = buildDynamicVars(teal, theme)
      for (const name of ['--background', '--foreground', '--card', '--popover', '--primary', '--secondary', '--muted', '--accent', '--border', '--input', '--ring']) {
        expect(vars[name]!, `${theme} ${name}`).toMatch(/^-?\d+(\.\d+)? \d+(\.\d+)?% \d+(\.\d+)?%$/)
      }
      // Primary follows the source hue family, not a fixed blue. The H
      // component of an "H S% L%" triplet IS its hue; compare against the
      // source color's RGB-space hue (~174° for the teal fixture).
      const primaryHue = Number.parseFloat(vars['--primary']!.split(' ')[0]!)
      const srcHue = hexHue('#2ec4b6')
      const delta = Math.abs(primaryHue - srcHue)
      expect(Math.min(delta, 360 - delta)).toBeLessThan(45)
    }
  })

  it('argbFromHex parses opaque colors', () => {
    expect(argbFromHex('#2ec4b6')).toBe(argb(46, 196, 182))
    expect(argbFromHex('ffffff')).toBe(0xffffffff)
  })

  it('previewSwatches returns five distinct role circles', () => {
    const swatches = previewSwatches(teal)
    expect(swatches).toHaveLength(5)
    for (const s of swatches) expect(s).toMatch(/^#[0-9a-f]{6}$/i)
    // Dark accent (tone 80) is lighter than the light-theme accent (tone 40).
    const lum = (hex: string): number => {
      const n = Number.parseInt(hex.slice(1), 16)
      return ((n >> 16 & 255) + (n >> 8 & 255) + (n & 255)) / 3
    }
    expect(lum(swatches[0]!)).toBeGreaterThan(lum(swatches[1]!))
    // Surfaces span both extremes.
    expect(lum(swatches[3]!)).toBeLessThan(30)
    expect(lum(swatches[4]!)).toBeGreaterThan(220)
  })
})

/** Hex/HSL conversion used by the custom accent picker. */
describe('custom accent color helpers', () => {
  it('validates #rrggbb only', () => {
    expect(isValidHexColor('#58a6ff')).toBe(true)
    expect(isValidHexColor('#58A6FF')).toBe(true)
    expect(isValidHexColor('58a6ff')).toBe(false)
    expect(isValidHexColor('#58a6f')).toBe(false)
    expect(isValidHexColor('#58a6ff00')).toBe(false)
    expect(isValidHexColor('rgb(88,166,255)')).toBe(false)
  })

  it('round-trips hex → hsl → hex within channel tolerance', () => {
    // HSL is stored with integer steps, so a hex cannot always be reproduced
    // byte-exactly. The picker keeps the typed hex as the source of truth;
    // this only guarantees the sliders stay visually on the same color.
    const channels = (hex: string): number[] => {
      const n = Number.parseInt(hex.slice(1), 16)
      return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
    }
    for (const hex of ['#58a6ff', '#3fb950', '#ffffff', '#000000', '#f0883e']) {
      const [h, s, l] = hslFromHex(hex)
      const back = hexFromHsl(h, s, l)
      const [a, b] = [channels(hex), channels(back)]
      for (let i = 0; i < 3; i++) {
        expect(Math.abs(a[i]! - b[i]!), `${hex} channel ${i}`).toBeLessThanOrEqual(2)
      }
    }
  })

  it('round-trips hsl → hex → hsl within rounding tolerance', () => {
    for (const [h, s, l] of [[210, 100, 50], [0, 0, 100], [120, 50, 40], [330, 80, 60]]) {
      const hex = hexFromHsl(h!, s!, l!)
      expect(hex).toMatch(/^#[0-9a-f]{6}$/)
      const [h2, s2, l2] = hslFromHex(hex)
      expect(Math.abs(h2 - h!)).toBeLessThanOrEqual(2)
      expect(Math.abs(s2 - s!)).toBeLessThanOrEqual(2)
      expect(Math.abs(l2 - l!)).toBeLessThanOrEqual(2)
    }
  })

  it('clamps out-of-range HSL input instead of producing garbage', () => {
    expect(hexFromHsl(400, 150, 150)).toMatch(/^#[0-9a-f]{6}$/)
    expect(hexFromHsl(-30, -20, -10)).toMatch(/^#[0-9a-f]{6}$/)
  })

  it('serializes ARGB back to lowercase hex', () => {
    expect(hexFromArgbInt(argb(46, 196, 182))).toBe('#2ec4b6')
    expect(hexFromArgbInt(argb(255, 255, 255))).toBe('#ffffff')
  })

  it('computes WCAG contrast for the low-contrast warning', () => {
    // White on black is the maximum 21:1; identical colors are 1:1.
    expect(contrastRatio('#ffffff', '#000000')).toBeCloseTo(21, 1)
    expect(contrastRatio('#58a6ff', '#58a6ff')).toBeCloseTo(1, 2)
    // A dark blue on a dark surface fails the 4.5:1 text threshold.
    expect(contrastRatio('#101820', '#0a0e14')).toBeLessThan(4.5)
    // Mid blue on white passes it.
    expect(contrastRatio('#0969da', '#ffffff')).toBeGreaterThan(4.5)
  })
})

describe('buildMonochromeVars & contrast foreground tokens', () => {
  it('builds pure zero-hue zinc neutral dark and light palettes', () => {
    const dark = buildMonochromeVars('dark', 'neutral')
    expect(dark['--bg-primary']).toBe('#09090b')
    expect(dark['--bg-secondary']).toBe('#111113')
    expect(dark['--surface-raised']).toBe('#18181b')
    expect(dark['--surface-hover']).toBe('#27272a')
    expect(dark['--text-primary']).toBe('#fafafa')
    expect(dark['--text-secondary']).toBe('#a1a1aa')
    expect(dark['--text-muted']).toBe('#71717a')
    expect(dark['--border-muted']).toBe('rgba(255, 255, 255, 0.08)')
    expect(dark['--border-default']).toBe('#27272a')
    expect(dark['--border-input-focus']).toBe('#a1a1aa')
    expect(dark['--accent-primary']).toBe('#fafafa')
    expect(dark['--accent-brand']).toBe('#fafafa')
    expect(dark['--text-brand']).toBe('#fafafa')
    expect(dark['--text-link']).toBe('#e4e4e7')
    expect(dark['--bg-primary-button']).toBe('#fafafa')
    expect(dark['--bg-primary-button-hover']).toBe('#e4e4e7')
    expect(dark['--text-on-primary']).toBe('#09090b')
    expect(dark['--primary-button-fg']).toBe('#09090b')
    expect(dark['--shadow-focus']).toBe('0 0 0 2px rgba(250, 250, 250, 0.24)')
    expect(dark['--background']).toBe('240 10% 4%')
    expect(dark['--card']).toBe('240 6% 10%')
    expect(dark['--primary']).toBe('0 0% 98%')
    expect(dark['--primary-foreground']).toBe('240 10% 4%')

    const light = buildMonochromeVars('light', 'neutral')
    expect(light['--bg-primary']).toBe('#ffffff')
    expect(light['--bg-secondary']).toBe('#fafafa')
    expect(light['--surface-raised']).toBe('#ffffff')
    expect(light['--surface-hover']).toBe('#f4f4f5')
    expect(light['--text-primary']).toBe('#09090b')
    expect(light['--text-secondary']).toBe('#52525b')
    expect(light['--text-muted']).toBe('#71717a')
    expect(light['--border-muted']).toBe('#e4e4e7')
    expect(light['--border-default']).toBe('#d4d4d8')
    expect(light['--border-input-focus']).toBe('#18181b')
    expect(light['--accent-primary']).toBe('#18181b')
    expect(light['--accent-brand']).toBe('#18181b')
    expect(light['--text-brand']).toBe('#18181b')
    expect(light['--text-link']).toBe('#18181b')
    expect(light['--bg-primary-button']).toBe('#18181b')
    expect(light['--bg-primary-button-hover']).toBe('#27272a')
    expect(light['--text-on-primary']).toBe('#ffffff')
    expect(light['--primary-button-fg']).toBe('#ffffff')
    expect(light['--shadow-focus']).toBe('0 0 0 2px rgba(24, 24, 27, 0.16)')
    expect(light['--background']).toBe('0 0% 100%')
    expect(light['--primary']).toBe('240 6% 10%')
    expect(light['--primary-foreground']).toBe('0 0% 100%')
  })

  it('builds Anthropic Warm Ink & Paper dark and light palettes', () => {
    const dark = buildMonochromeVars('dark', 'warm')
    expect(dark['--bg-primary']).toBe('#141210')
    expect(dark['--bg-secondary']).toBe('#1c1917')
    expect(dark['--surface-raised']).toBe('#26221e')
    expect(dark['--text-primary']).toBe('#f5f2eb')
    expect(dark['--accent-primary']).toBe('#e7e2d8')
    expect(dark['--accent-brand']).toBe('#CA7554')
    expect(dark['--bg-primary-button']).toBe('#f5f2eb')
    expect(dark['--bg-primary-button-hover']).toBe('#e7e2d8')
    expect(dark['--text-on-primary']).toBe('#141210')
    expect(dark['--primary-button-fg']).toBe('#141210')

    const light = buildMonochromeVars('light', 'warm')
    expect(light['--bg-primary']).toBe('#f8f6f1')
    expect(light['--bg-secondary']).toBe('#f1eee7')
    expect(light['--surface-raised']).toBe('#ffffff')
    expect(light['--text-primary']).toBe('#1c1917')
    expect(light['--accent-primary']).toBe('#1c1917')
    expect(light['--accent-brand']).toBe('#CA7554')
    expect(light['--bg-primary-button']).toBe('#1c1917')
    expect(light['--bg-primary-button-hover']).toBe('#292524')
    expect(light['--text-on-primary']).toBe('#ffffff')
    expect(light['--primary-button-fg']).toBe('#ffffff')
  })

  it('covers identical keys across all monochrome variants and keeps semantic solids out', () => {
    const baseKeys = Object.keys(buildMonochromeVars('dark', 'neutral')).sort()
    expect(Object.keys(buildMonochromeVars('light', 'neutral')).sort()).toEqual(baseKeys)
    expect(Object.keys(buildMonochromeVars('dark', 'warm')).sort()).toEqual(baseKeys)
    expect(Object.keys(buildMonochromeVars('light', 'warm')).sort()).toEqual(baseKeys)
    expect(DYNAMIC_VAR_NAMES).toContain('--text-on-primary')
    expect(DYNAMIC_VAR_NAMES).toContain('--primary-button-fg')
    for (const forbidden of ['--accent-success', '--text-approve', '--text-error', '--accent-danger']) {
      expect(baseKeys).not.toContain(forbidden)
    }
  })

  it('decouples achromatic sources from Monet chroma locking', () => {
    for (const hex of ['#18181b', '#808080', '#fafafa', '#000000']) {
      const dark = buildDynamicVars(argbFromHex(hex), 'dark')
      const light = buildDynamicVars(argbFromHex(hex), 'light')
      expect(dark['--bg-primary']).toBe('#09090b')
      expect(dark['--text-on-primary']).toBe('#09090b')
      expect(light['--bg-primary']).toBe('#ffffff')
      expect(light['--text-on-primary']).toBe('#ffffff')
      expect(previewSwatches(argbFromHex(hex))).toEqual(['#fafafa', '#18181b', '#27272a', '#09090b', '#fafafa'])
    }
  })

  it('guarantees WCAG >= 4.5:1 primary button foreground contrast across monochrome and chromatic presets', () => {
    expect(relativeLuminance('#ffffff')).toBeCloseTo(1, 4)
    expect(relativeLuminance('#000000')).toBeCloseTo(0, 4)
    expect(contrastForeground('#fafafa')).toBe('#09090b')
    expect(contrastForeground('#18181b')).toBe('#ffffff')

    for (const variant of ['neutral', 'warm'] as const) {
      for (const theme of ['dark', 'light'] as const) {
        const vars = buildMonochromeVars(theme, variant)
        expect(vars['--text-on-primary']).toBe(vars['--primary-button-fg'])
        expect(
          contrastRatio(vars['--bg-primary-button']!, vars['--text-on-primary']!),
          `${variant}/${theme} primary button contrast`,
        ).toBeGreaterThanOrEqual(4.5)
      }
    }

    const chromaticSeeds = [
      '#58a6ff', '#3fb950', '#f0883e', '#d2bdf5',
      '#f778ba', '#f85149', '#56d4dd', '#d29922',
    ]
    for (const hex of chromaticSeeds) {
      for (const theme of ['dark', 'light'] as const) {
        const vars = buildDynamicVars(argbFromHex(hex), theme)
        expect(vars['--text-on-primary']).toBe(vars['--primary-button-fg'])
        expect(
          contrastRatio(vars['--bg-primary-button']!, vars['--text-on-primary']!),
          `${hex}/${theme} primary button contrast`,
        ).toBeGreaterThanOrEqual(4.5)
      }
    }
  })
})
