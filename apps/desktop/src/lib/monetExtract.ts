/**
 * Monet-style (Material You) background color extraction.
 *
 * Uses Google's official @material/material-color-utilities — the same
 * quantize + score + HCT tonal-palette machinery as Android 12 wallpaper
 * theming — and maps the resulting palettes onto the app's CSS custom
 * property families defined in styles.css ("Legacy CSS variables" blocks).
 *
 * Pure module: no DOM access, fully unit-testable.
 *
 * Token mapping contract:
 * - Neutral families (--bg-*, neutral --border-*, --text-primary/secondary)
 *   are re-derived from Monet "neutral"/"neutralVariant" tonal palettes so
 *   surfaces carry a subtle tint of the source image's hue.
 * - Accent identity (--accent-primary/brand/success, --text-brand,
 *   primary buttons, selection, focus ring) comes from the Monet "primary"
 *   palette at M3 tones (dark: 80, light: 40) with guaranteed contrast.
 * - Semantic solids stay untouched: error/warning/danger/info/recovery
 *   accents, status backgrounds, --text-error/--text-reject/--text-link
 *   and scrollbar colors keep their styles.css values.
 */

import {
  Hct,
  QuantizerWu,
  Score,
  TonalPalette,
  hexFromArgb,
} from '@material/material-color-utilities'

/** A full set of CSS custom properties to inject for one resolved theme. */
export type DynamicVars = Record<string, string>

/** Cached extraction result persisted under `tinadec-dynamic-palette`. */
export interface DynamicPalette {
  /** Background source string the palette was extracted from. */
  source: string
  dark: DynamicVars
  light: DynamicVars
}

const PRIMARY_MAX_CHROMA = 48
const PRIMARY_MIN_CHROMA = 32

function rgba(argb: number, alpha: number): string {
  const r = (argb >> 16) & 0xff
  const g = (argb >> 8) & 0xff
  const b = argb & 0xff
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

/** Cluster budget handed to Wu quantization (Monet uses 128 for Celebi). */
const MAX_CLUSTERS = 128

/**
 * Quantize raw pixels and score them exactly like Material You, minus one
 * pass: Wu histogram quantization (stage 1 of Monet's Celebi pipeline)
 * followed by Score.source(). The random-init K-means refinement in
 * QuantizerCelebi is deliberately skipped — it draws from Math.random(),
 * and every renderer window must derive byte-identical palettes without a
 * cross-window sync protocol.
 */
export function extractSourceColor(argbPixels: number[]): number | null {
  if (argbPixels.length === 0) return null
  const clusters = new QuantizerWu()
    .quantize(argbPixels, MAX_CLUSTERS)
    .map((c) => c >>> 0)
  if (clusters.length === 0) return null

  // Weight each cluster by its assigned pixel population.
  const counts = new Map<number, number>()
  for (const p of argbPixels) {
    let best = clusters[0]!
    let bestDist = Infinity
    for (let i = 0; i < clusters.length; i++) {
      const c = clusters[i]!
      const dr = (p >> 16 & 255) - (c >> 16 & 255)
      const dg = (p >> 8 & 255) - (c >> 8 & 255)
      const db = (p & 255) - (c & 255)
      const dist = dr * dr + dg * dg + db * db
      if (dist < bestDist) {
        bestDist = dist
        best = c
      }
    }
    counts.set(best, (counts.get(best) ?? 0) + 1)
  }

  const ranked = Score.score(counts)
  return ranked.length > 0 ? ranked[0] : null
}

/**
 * Build the complete dynamic token map for one resolved theme from a
 * source ARGB color. Deterministic — every window computes identical
 * values for the same input, which is what keeps cross-window sync free.
 */
export function buildDynamicVars(source: number, theme: 'dark' | 'light'): DynamicVars {
  const hct = Hct.fromInt(source)
  const chroma = Math.min(Math.max(hct.chroma, PRIMARY_MIN_CHROMA), PRIMARY_MAX_CHROMA)
  // Monet TonalSpot proportions: vivid primary on the source hue, muted
  // secondary/neutral/neutralVariant companions on the same hue.
  const primary = TonalPalette.fromHueAndChroma(hct.hue, chroma)
  const neutral = TonalPalette.fromHueAndChroma(hct.hue, 8)
  const variant = TonalPalette.fromHueAndChroma(hct.hue, 16)

  const toneHex = (p: TonalPalette, tone: number): string => hexFromArgb(p.tone(tone))

  if (theme === 'dark') {
    const accent = toneHex(primary, 80)
    const focus = toneHex(primary, 70)
    return {
      '--bg-primary': toneHex(neutral, 6),
      '--bg-secondary': toneHex(neutral, 10),
      '--bg-tertiary': toneHex(neutral, 15),
      '--bg-overlay': toneHex(neutral, 12),
      '--bg-hover': toneHex(neutral, 18),
      '--bg-selected': toneHex(primary, 30),
      '--bg-selected-outline': focus,
      '--bg-assistant-msg': toneHex(neutral, 10),
      '--bg-assistant-msg-border': toneHex(neutral, 15),
      '--bg-avatar': toneHex(neutral, 15),
      '--bg-empty-action': toneHex(neutral, 10),
      '--bg-diff': toneHex(neutral, 6),
      '--bg-input': toneHex(neutral, 6),
      '--bg-button': toneHex(neutral, 15),
      '--bg-button-hover': toneHex(neutral, 20),
      '--bg-secondary-button': toneHex(neutral, 15),
      '--bg-secondary-button-hover': toneHex(neutral, 20),
      '--bg-primary-button': toneHex(primary, 45),
      '--bg-primary-button-hover': toneHex(primary, 55),
      '--bg-immersive': rgba(neutral.tone(10), 0.38),

      '--border-default': toneHex(variant, 25),
      '--border-muted': toneHex(variant, 20),
      '--border-input': toneHex(variant, 25),
      '--border-input-focus': focus,
      '--border-dashed': toneHex(variant, 25),
      '--border-empty-action': toneHex(variant, 35),

      '--text-primary': toneHex(variant, 90),
      '--text-secondary': toneHex(variant, 65),
      '--text-muted': toneHex(variant, 58),
      '--text-brand': accent,
      '--text-approve': accent,

      '--accent-primary': accent,
      '--accent-success': accent,
      '--accent-brand': accent,
      '--accent-soft': rgba(primary.tone(70), 0.12),
      '--shadow-focus': `0 0 0 2px ${rgba(primary.tone(70), 0.3)}`,

      '--bg-primary-rgb': rgbTriplet(neutral.tone(6)),
      '--bg-secondary-rgb': rgbTriplet(neutral.tone(10)),
      '--bg-tertiary-rgb': rgbTriplet(neutral.tone(15)),
      '--bg-hover-rgb': rgbTriplet(neutral.tone(18)),
      '--bg-selected-rgb': rgbTriplet(primary.tone(30)),
      '--bg-input-rgb': rgbTriplet(neutral.tone(6)),
      '--bg-button-rgb': rgbTriplet(neutral.tone(15)),
      '--bg-button-hover-rgb': rgbTriplet(neutral.tone(20)),
    }
  }

  const accent = toneHex(primary, 40)
  const focus = toneHex(primary, 40)
  return {
    '--bg-primary': toneHex(neutral, 100),
    '--bg-secondary': toneHex(neutral, 96),
    '--bg-tertiary': toneHex(neutral, 93),
    '--bg-overlay': toneHex(neutral, 100),
    '--bg-hover': toneHex(neutral, 94),
    '--bg-selected': toneHex(primary, 90),
    '--bg-selected-outline': focus,
    '--bg-assistant-msg': toneHex(neutral, 100),
    '--bg-assistant-msg-border': toneHex(variant, 80),
    '--bg-avatar': toneHex(neutral, 95),
    '--bg-empty-action': toneHex(neutral, 96),
    '--bg-diff': toneHex(neutral, 96),
    '--bg-input': toneHex(neutral, 99),
    '--bg-button': toneHex(neutral, 94),
    '--bg-button-hover': toneHex(variant, 88),
    '--bg-secondary-button': toneHex(neutral, 95),
    '--bg-secondary-button-hover': toneHex(variant, 88),
    '--bg-primary-button': accent,
    '--bg-primary-button-hover': toneHex(primary, 50),
    '--bg-immersive': rgba(neutral.tone(96), 0.7),

    '--border-default': toneHex(variant, 84),
    '--border-muted': toneHex(variant, 92),
    '--border-input': toneHex(variant, 82),
    '--border-input-focus': focus,
    '--border-dashed': toneHex(variant, 82),
    '--border-empty-action': toneHex(variant, 72),

    '--text-primary': toneHex(variant, 15),
    '--text-secondary': toneHex(variant, 40),
    '--text-muted': toneHex(variant, 45),
    '--text-brand': accent,
    '--text-approve': toneHex(primary, 32),

    '--accent-primary': accent,
    '--accent-success': accent,
    '--accent-brand': accent,
    '--accent-soft': rgba(primary.tone(40), 0.1),
    '--shadow-focus': `0 0 0 2px ${rgba(primary.tone(40), 0.18)}`,

    '--bg-primary-rgb': rgbTriplet(neutral.tone(100)),
    '--bg-secondary-rgb': rgbTriplet(neutral.tone(96)),
    '--bg-tertiary-rgb': rgbTriplet(neutral.tone(93)),
    '--bg-hover-rgb': rgbTriplet(neutral.tone(94)),
    '--bg-selected-rgb': rgbTriplet(primary.tone(90)),
    '--bg-input-rgb': rgbTriplet(neutral.tone(99)),
    '--bg-button-rgb': rgbTriplet(neutral.tone(94)),
    '--bg-button-hover-rgb': rgbTriplet(variant.tone(88)),
  }
}

function rgbTriplet(color: number): string {
  return `${(color >> 16) & 0xff}, ${(color >> 8) & 0xff}, ${color & 0xff}`
}

/** Every property buildDynamicVars may emit — used to strip injected values. */
export const DYNAMIC_VAR_NAMES: readonly string[] = Object.keys({
  ...buildDynamicVars(0xff2ec4b6, 'dark'),
  ...buildDynamicVars(0xff2ec4b6, 'light'),
})
