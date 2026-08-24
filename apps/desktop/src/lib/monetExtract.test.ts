import { describe, expect, it } from 'vitest'
import {
  buildDynamicVars,
  DYNAMIC_VAR_NAMES,
  extractSourceColor,
} from './monetExtract'

/** Pack an opaque ARGB int. */
function argb(r: number, g: number, b: number): number {
  return (0xff << 24) | (r << 16) | (g << 8) | b
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
    for (const forbidden of ['--text-error', '--accent-danger', '--bg-status-ok', '--border-error']) {
      expect(names).not.toContain(forbidden)
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
})
