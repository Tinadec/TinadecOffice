/**
 * The desktop palette has to have exactly one default floor answer, and it lives
 * in two places: the `:root[data-theme]` floor in `styles.css`, and the default
 * accent in `useTheme.ts` (`monochrome` -> `buildMonochromeVars`).
 *
 * These assertions pin the neutral zero-hue zinc monochrome floor, verify the
 * `--text-on-primary` / `--primary-button-fg` contrast tokens, and ensure none
 * of the retired teal/navy/lilac floor values sneak back into `styles.css`.
 */
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

import { ACCENT_COLORS, DEFAULT_ACCENT } from './composables/useTheme'

const css = readFileSync(new URL('./styles.css', import.meta.url), 'utf8')

function themeBlock(theme: 'dark' | 'light'): Record<string, string> {
  const marker = `:root[data-theme="${theme}"] {`
  const start = css.indexOf(marker)
  if (start < 0) throw new Error(`styles.css is missing ${marker}`)
  const end = css.indexOf('\n}', start)
  if (end < 0) throw new Error(`unterminated ${marker}`)

  const vars: Record<string, string> = {}
  for (const line of css.slice(start + marker.length, end).split('\n')) {
    const match = /^\s*(--[\w-]+):\s*(.+?);\s*$/.exec(line)
    if (match) vars[match[1]!] = match[2]!.trim()
  }
  return vars
}

const dark = themeBlock('dark')
const light = themeBlock('light')

const BRAND_DARK = '#fafafa'
const BRAND_LIGHT = '#18181b'

describe('styles.css accent floor', () => {
  it('defaults to the same pure monochrome zinc palette the theme pipeline picks', () => {
    expect(dark['--accent-primary']).toBe(BRAND_DARK)
    expect(dark['--accent-brand']).toBe(BRAND_DARK)
    expect(dark['--text-brand']).toBe(BRAND_DARK)
    expect(dark['--bg-primary-button']).toBe('#fafafa')
    expect(dark['--text-on-primary']).toBe('#09090b')
    expect(dark['--primary-button-fg']).toBe('#09090b')

    expect(light['--accent-primary']).toBe(BRAND_LIGHT)
    expect(light['--accent-brand']).toBe(BRAND_LIGHT)
    expect(light['--text-brand']).toBe(BRAND_LIGHT)
    expect(light['--bg-primary-button']).toBe('#18181b')
    expect(light['--text-on-primary']).toBe('#ffffff')
    expect(light['--primary-button-fg']).toBe('#ffffff')
  })

  it('agrees with the accent preset useTheme ships as default', () => {
    // If these drift, an install that has never written localStorage renders
    // one colour before the first injection and another one after it.
    const preset = ACCENT_COLORS.find((c) => c.key === DEFAULT_ACCENT)
    expect(preset, `DEFAULT_ACCENT "${DEFAULT_ACCENT}" has no preset`).toBeTruthy()
    expect(preset!.dark.accentPrimary).toBe(BRAND_DARK)
    expect(preset!.light.accentPrimary).toBe(BRAND_LIGHT)
  })

  it('keeps links crisp neutral monochrome on dark and light surfaces', () => {
    expect(dark['--text-link']).toBe('#e4e4e7')
    expect(light['--text-link']).toBe('#18181b')
  })

  it('carries none of the retired teal, navy, or purple/lilac palettes', () => {
    const lower = css.toLowerCase()
    const retired = [
      '#2ec4b6', '#1f8f80', '#1f6f68', '#58a6ff', '#0969da', '#0a0e14', '#11151c', '#1a1f29',
      '#15121a', '#d2bdf5', '#746294', '#675687', '#faf7fd',
    ]
    for (const value of retired) {
      expect(lower, `styles.css still ships the retired value ${value}`).not.toContain(value)
    }
  })

  it('keeps semantic colors away from the accent family', () => {
    // Success/approval green must read the same under every accent.
    expect(dark['--accent-success']).toBe('#3fb950')
    expect(light['--accent-success']).toBe('#1a7f37')
    expect(dark['--text-error']).toBe('#f85149')
  })

  it('uses pure zero-hue zinc surfaces on dark and light floors', () => {
    expect(dark['--bg-primary']).toBe('#09090b')
    expect(dark['--bg-secondary']).toBe('#111113')
    expect(light['--bg-primary']).toBe('#ffffff')
    expect(light['--bg-secondary']).toBe('#fafafa')
  })
})
