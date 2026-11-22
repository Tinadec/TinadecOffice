/**
 * Unified color token builder: Monochrome / Warm Ink minimalist palettes
 * alongside Monet-style (Material You) chromatic & wallpaper extraction.
 *
 * Uses Google's official @material/material-color-utilities — the same
 * quantize + score + HCT tonal-palette machinery as Android 12 wallpaper
 * theming — for chromatic presets, custom colors, and wallpapers, while
 * decoupling achromatic / neutral palettes (`buildMonochromeVars`) from
 * Monet's minimum chroma floor so default surfaces stay pure zero-hue zinc
 * or warm paper instead of forcing a chromatic tint.
 *
 * Pure module: no DOM access, fully unit-testable.
 *
 * Token mapping contract:
 * - Monochrome/Warm (`buildMonochromeVars`) emits pure neutral zinc or warm
 *   ink & paper scales with crisp high-contrast primary buttons and paired
 *   `--text-on-primary` / `--primary-button-fg` foreground tokens.
 * - Chromatic/Dynamic (`buildDynamicVars`) re-derives neutral/neutralVariant
 *   surfaces and primary accents from Monet tonal palettes, decoupling the
 *   chroma floor for achromatic sources (`chroma < 8`) and always computing
 *   `--text-on-primary` / `--primary-button-fg` via WCAG relative luminance.
 * - shadcn/Tailwind tokens (--background/--primary/--card/--popover/...)
 *   are emitted as "H S% L%" triplets from the same palettes so the body
 *   base, UI primitives, and bg-card/bg-popover/bg-accent utilities join
 *   the one color system instead of a fixed hue.
 * - Semantic solids stay untouched: error/warning/danger/info/recovery
 *   accents, success and approval green, status backgrounds,
 *   --text-error/--text-reject and scrollbar colors keep their
 *   styles.css values. A status color must mean the same thing regardless
 *   of which accent the user picked.
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

/** Curated minimalist monochrome variants decoupled from Monet chroma locking. */
export type MonochromeVariant = 'neutral' | 'warm'

/** Cached extraction result persisted under `tinadec-dynamic-palette`. */
export interface DynamicPalette {
  /** Background source string the palette was extracted from. */
  source: string
  /** Winning source color (ARGB) — seeds previews and preset-style reuse. */
  sourceColor: number
  dark: DynamicVars
  light: DynamicVars
}

const PRIMARY_MAX_CHROMA = 48
const PRIMARY_MIN_CHROMA = 32
/**
 * HCT chroma below which a source color is perceptually achromatic (gray/ink/paper).
 * Decouples pure monochrome inputs from Monet's minimum chroma clamp so selecting
 * `#18181b`, `#808080`, or a grayscale wallpaper never invents a chromatic hue.
 */
const ACHROMATIC_CHROMA_THRESHOLD = 8
/** WCAG relative luminance cutoff separating dark-ink vs white foreground text. */
const CONTRAST_LUMINANCE_THRESHOLD = 0.36

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
 * Build a curated monochrome minimalist palette decoupled from Monet's chroma
 * locking. Covers both pure zero-hue zinc (`neutral`, Codex/Geist style) and
 * Anthropic Warm Ink & Paper (`warm`), emitting matching `--text-on-primary`
 * and `--primary-button-fg` contrast foregrounds plus clean HSL Tailwind tokens.
 */
export function buildMonochromeVars(
  theme: 'dark' | 'light',
  variant: MonochromeVariant,
): DynamicVars {
  if (variant === 'warm') {
    if (theme === 'dark') {
      return {
        '--bg-primary': '#141210',
        '--bg-secondary': '#1c1917',
        '--bg-tertiary': '#26221e',
        '--bg-overlay': '#201c19',
        '--bg-hover': '#2e2924',
        '--surface-raised': '#26221e',
        '--surface-hover': '#2e2924',
        '--bg-selected': '#322c26',
        '--bg-selected-outline': '#d6cfc2',
        '--bg-assistant-msg': '#1c1917',
        '--bg-assistant-msg-border': '#2e2924',
        '--bg-avatar': '#26221e',
        '--bg-empty-action': '#1c1917',
        '--bg-diff': '#141210',
        '--bg-input': '#141210',
        '--bg-button': '#26221e',
        '--bg-button-hover': '#2e2924',
        '--bg-secondary-button': '#26221e',
        '--bg-secondary-button-hover': '#2e2924',
        '--bg-primary-button': '#f5f2eb',
        '--bg-primary-button-hover': '#e7e2d8',
        '--text-on-primary': '#141210',
        '--primary-button-fg': '#141210',
        '--bg-immersive': 'rgba(28, 25, 23, 0.42)',

        '--border-default': '#332e29',
        '--border-muted': 'rgba(245, 242, 235, 0.09)',
        '--border-input': '#332e29',
        '--border-input-focus': '#b8afa2',
        '--border-dashed': '#332e29',
        '--border-empty-action': '#474039',

        '--text-primary': '#f5f2eb',
        '--text-secondary': '#a8a095',
        '--text-muted': '#7d756b',
        '--text-brand': '#ca7554',
        '--text-link': '#e7e2d8',

        '--accent-primary': '#e7e2d8',
        '--accent-brand': '#CA7554',
        '--accent-soft': 'rgba(202, 117, 84, 0.14)',
        '--shadow-focus': '0 0 0 2px rgba(231, 226, 216, 0.24)',

        '--bg-primary-rgb': '20, 18, 16',
        '--bg-secondary-rgb': '28, 25, 23',
        '--bg-tertiary-rgb': '38, 34, 30',
        '--bg-hover-rgb': '46, 41, 36',
        '--bg-selected-rgb': '50, 44, 38',
        '--bg-input-rgb': '20, 18, 16',
        '--bg-button-rgb': '38, 34, 30',
        '--bg-button-hover-rgb': '46, 41, 36',

        // shadcn/Tailwind family — consumed as hsl(var(--token)) triplets.
        '--background': '30 11% 7%',
        '--foreground': '42 33% 94%',
        '--card': '30 12% 13%',
        '--card-foreground': '42 33% 94%',
        '--popover': '26 12% 11%',
        '--popover-foreground': '42 33% 94%',
        '--primary': '42 33% 94%',
        '--primary-foreground': '30 11% 7%',
        '--secondary': '30 12% 16%',
        '--secondary-foreground': '42 33% 94%',
        '--muted': '30 12% 16%',
        '--muted-foreground': '35 10% 62%',
        '--accent': '30 12% 16%',
        '--accent-foreground': '42 33% 94%',
        '--border': '30 11% 18%',
        '--input': '30 11% 18%',
        '--ring': '35 13% 68%',
      }
    }

    return {
      '--bg-primary': '#f8f6f1',
      '--bg-secondary': '#f1eee7',
      '--bg-tertiary': '#eae5dc',
      '--bg-overlay': '#ffffff',
      '--bg-hover': '#eae5dc',
      '--surface-raised': '#ffffff',
      '--surface-hover': '#eae5dc',
      '--bg-selected': '#e7e1d6',
      '--bg-selected-outline': '#1c1917',
      '--bg-assistant-msg': '#ffffff',
      '--bg-assistant-msg-border': '#e2dcd2',
      '--bg-avatar': '#eae5dc',
      '--bg-empty-action': '#f1eee7',
      '--bg-diff': '#f1eee7',
      '--bg-input': '#ffffff',
      '--bg-button': '#eae5dc',
      '--bg-button-hover': '#e0d9ce',
      '--bg-secondary-button': '#eae5dc',
      '--bg-secondary-button-hover': '#e0d9ce',
      '--bg-primary-button': '#1c1917',
      '--bg-primary-button-hover': '#292524',
      '--text-on-primary': '#ffffff',
      '--primary-button-fg': '#ffffff',
      '--bg-immersive': 'rgba(241, 238, 231, 0.75)',

      '--border-default': '#d6cfc4',
      '--border-muted': '#e5dfd5',
      '--border-input': '#d6cfc4',
      '--border-input-focus': '#1c1917',
      '--border-dashed': '#d6cfc4',
      '--border-empty-action': '#b8afa2',

      '--text-primary': '#1c1917',
      '--text-secondary': '#57534e',
      '--text-muted': '#78716c',
      '--text-brand': '#ca7554',
      '--text-link': '#1c1917',

      '--accent-primary': '#1c1917',
      '--accent-brand': '#CA7554',
      '--accent-soft': 'rgba(202, 117, 84, 0.12)',
      '--shadow-focus': '0 0 0 2px rgba(28, 25, 23, 0.16)',

      '--bg-primary-rgb': '248, 246, 241',
      '--bg-secondary-rgb': '241, 238, 231',
      '--bg-tertiary-rgb': '234, 229, 220',
      '--bg-hover-rgb': '234, 229, 220',
      '--bg-selected-rgb': '231, 225, 214',
      '--bg-input-rgb': '255, 255, 255',
      '--bg-button-rgb': '234, 229, 220',
      '--bg-button-hover-rgb': '224, 217, 206',

      // shadcn/Tailwind family — consumed as hsl(var(--token)) triplets.
      '--background': '43 30% 96%',
      '--foreground': '24 10% 10%',
      '--card': '0 0% 100%',
      '--card-foreground': '24 10% 10%',
      '--popover': '0 0% 100%',
      '--popover-foreground': '24 10% 10%',
      '--primary': '24 10% 10%',
      '--primary-foreground': '0 0% 100%',
      '--secondary': '40 20% 92%',
      '--secondary-foreground': '24 10% 10%',
      '--muted': '40 20% 92%',
      '--muted-foreground': '25 5% 45%',
      '--accent': '39 20% 89%',
      '--accent-foreground': '24 10% 10%',
      '--border': '37 18% 85%',
      '--input': '37 18% 85%',
      '--ring': '24 10% 10%',
    }
  }

  if (theme === 'dark') {
    return {
      '--bg-primary': '#09090b',
      '--bg-secondary': '#111113',
      '--bg-tertiary': '#18181b',
      '--bg-overlay': '#141417',
      '--bg-hover': '#27272a',
      '--surface-raised': '#18181b',
      '--surface-hover': '#27272a',
      '--bg-selected': '#27272a',
      '--bg-selected-outline': '#a1a1aa',
      '--bg-assistant-msg': '#111113',
      '--bg-assistant-msg-border': '#27272a',
      '--bg-avatar': '#18181b',
      '--bg-empty-action': '#111113',
      '--bg-diff': '#09090b',
      '--bg-input': '#09090b',
      '--bg-button': '#18181b',
      '--bg-button-hover': '#27272a',
      '--bg-secondary-button': '#18181b',
      '--bg-secondary-button-hover': '#27272a',
      '--bg-primary-button': '#fafafa',
      '--bg-primary-button-hover': '#e4e4e7',
      '--text-on-primary': '#09090b',
      '--primary-button-fg': '#09090b',
      '--bg-immersive': 'rgba(17, 17, 19, 0.38)',

      '--border-default': '#27272a',
      '--border-muted': 'rgba(255, 255, 255, 0.08)',
      '--border-input': '#27272a',
      '--border-input-focus': '#a1a1aa',
      '--border-dashed': '#27272a',
      '--border-empty-action': '#3f3f46',

      '--text-primary': '#fafafa',
      '--text-secondary': '#a1a1aa',
      '--text-muted': '#71717a',
      '--text-brand': '#fafafa',
      '--text-link': '#e4e4e7',

      '--accent-primary': '#fafafa',
      '--accent-brand': '#fafafa',
      '--accent-soft': 'rgba(250, 250, 250, 0.09)',
      '--shadow-focus': '0 0 0 2px rgba(250, 250, 250, 0.24)',

      '--bg-primary-rgb': '9, 9, 11',
      '--bg-secondary-rgb': '17, 17, 19',
      '--bg-tertiary-rgb': '24, 24, 27',
      '--bg-hover-rgb': '39, 39, 42',
      '--bg-selected-rgb': '39, 39, 42',
      '--bg-input-rgb': '9, 9, 11',
      '--bg-button-rgb': '24, 24, 27',
      '--bg-button-hover-rgb': '39, 39, 42',

      // shadcn/Tailwind family — consumed as hsl(var(--token)) triplets.
      '--background': '240 10% 4%',
      '--foreground': '0 0% 98%',
      '--card': '240 6% 10%',
      '--card-foreground': '0 0% 98%',
      '--popover': '240 6% 9%',
      '--popover-foreground': '0 0% 98%',
      '--primary': '0 0% 98%',
      '--primary-foreground': '240 10% 4%',
      '--secondary': '240 4% 16%',
      '--secondary-foreground': '0 0% 98%',
      '--muted': '240 4% 16%',
      '--muted-foreground': '240 5% 65%',
      '--accent': '240 4% 16%',
      '--accent-foreground': '0 0% 98%',
      '--border': '240 4% 16%',
      '--input': '240 4% 16%',
      '--ring': '240 5% 65%',
    }
  }

  return {
    '--bg-primary': '#ffffff',
    '--bg-secondary': '#fafafa',
    '--bg-tertiary': '#f4f4f5',
    '--bg-overlay': '#ffffff',
    '--bg-hover': '#f4f4f5',
    '--surface-raised': '#ffffff',
    '--surface-hover': '#f4f4f5',
    '--bg-selected': '#f4f4f5',
    '--bg-selected-outline': '#18181b',
    '--bg-assistant-msg': '#ffffff',
    '--bg-assistant-msg-border': '#e4e4e7',
    '--bg-avatar': '#f4f4f5',
    '--bg-empty-action': '#fafafa',
    '--bg-diff': '#fafafa',
    '--bg-input': '#ffffff',
    '--bg-button': '#f4f4f5',
    '--bg-button-hover': '#e4e4e7',
    '--bg-secondary-button': '#f4f4f5',
    '--bg-secondary-button-hover': '#e4e4e7',
    '--bg-primary-button': '#18181b',
    '--bg-primary-button-hover': '#27272a',
    '--text-on-primary': '#ffffff',
    '--primary-button-fg': '#ffffff',
    '--bg-immersive': 'rgba(250, 250, 250, 0.72)',

    '--border-default': '#d4d4d8',
    '--border-muted': '#e4e4e7',
    '--border-input': '#d4d4d8',
    '--border-input-focus': '#18181b',
    '--border-dashed': '#d4d4d8',
    '--border-empty-action': '#a1a1aa',

    '--text-primary': '#09090b',
    '--text-secondary': '#52525b',
    '--text-muted': '#71717a',
    '--text-brand': '#18181b',
    '--text-link': '#18181b',

    '--accent-primary': '#18181b',
    '--accent-brand': '#18181b',
    '--accent-soft': 'rgba(24, 24, 27, 0.08)',
    '--shadow-focus': '0 0 0 2px rgba(24, 24, 27, 0.16)',

    '--bg-primary-rgb': '255, 255, 255',
    '--bg-secondary-rgb': '250, 250, 250',
    '--bg-tertiary-rgb': '244, 244, 245',
    '--bg-hover-rgb': '244, 244, 245',
    '--bg-selected-rgb': '244, 244, 245',
    '--bg-input-rgb': '255, 255, 255',
    '--bg-button-rgb': '244, 244, 245',
    '--bg-button-hover-rgb': '228, 228, 231',

    // shadcn/Tailwind family — consumed as hsl(var(--token)) triplets.
    '--background': '0 0% 100%',
    '--foreground': '240 10% 4%',
    '--card': '0 0% 100%',
    '--card-foreground': '240 10% 4%',
    '--popover': '0 0% 100%',
    '--popover-foreground': '240 10% 4%',
    '--primary': '240 6% 10%',
    '--primary-foreground': '0 0% 100%',
    '--secondary': '240 5% 96%',
    '--secondary-foreground': '240 6% 10%',
    '--muted': '240 5% 96%',
    '--muted-foreground': '240 4% 46%',
    '--accent': '240 5% 96%',
    '--accent-foreground': '240 6% 10%',
    '--border': '240 6% 90%',
    '--input': '240 6% 90%',
    '--ring': '240 6% 10%',
  }
}

/**
 * Build the complete dynamic token map for one resolved theme from a
 * source ARGB color. Deterministic — every window computes identical
 * values for the same input, which is what keeps cross-window sync free.
 *
 * Achromatic inputs (`hct.chroma < 8`, e.g. `#18181b`, `#808080`, grayscale
 * wallpapers) bypass Monet's chroma floor and delegate to `buildMonochromeVars`.
 */
export function buildDynamicVars(source: number, theme: 'dark' | 'light'): DynamicVars {
  const hct = Hct.fromInt(source)
  if (hct.chroma < ACHROMATIC_CHROMA_THRESHOLD) {
    return buildMonochromeVars(theme, 'neutral')
  }
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
    const primaryButton = toneHex(primary, 45)
    const onPrimary = contrastForeground(primaryButton)
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
      '--bg-primary-button': primaryButton,
      '--bg-primary-button-hover': toneHex(primary, 55),
      '--text-on-primary': onPrimary,
      '--primary-button-fg': onPrimary,
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

      '--accent-primary': accent,
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

      // shadcn/Tailwind family — consumed as hsl(var(--token)) triplets.
      '--background': hslTriplet(neutral.tone(6)),
      '--foreground': hslTriplet(variant.tone(90)),
      '--card': hslTriplet(neutral.tone(9)),
      '--card-foreground': hslTriplet(variant.tone(90)),
      '--popover': hslTriplet(neutral.tone(12)),
      '--popover-foreground': hslTriplet(variant.tone(90)),
      '--primary': hslTriplet(primary.tone(80)),
      '--primary-foreground': hslTriplet(primary.tone(20)),
      '--secondary': hslTriplet(neutral.tone(15)),
      '--secondary-foreground': hslTriplet(variant.tone(90)),
      '--muted': hslTriplet(neutral.tone(15)),
      '--muted-foreground': hslTriplet(variant.tone(65)),
      '--accent': hslTriplet(neutral.tone(18)),
      '--accent-foreground': hslTriplet(variant.tone(90)),
      '--border': hslTriplet(variant.tone(25)),
      '--input': hslTriplet(variant.tone(25)),
      '--ring': hslTriplet(primary.tone(70)),
    }
  }

  const accent = toneHex(primary, 40)
  const focus = toneHex(primary, 40)
  const primaryButton = accent
  const onPrimary = contrastForeground(primaryButton)
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
    '--bg-primary-button': primaryButton,
    '--bg-primary-button-hover': toneHex(primary, 50),
    '--text-on-primary': onPrimary,
    '--primary-button-fg': onPrimary,
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

    '--accent-primary': accent,
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

    // shadcn/Tailwind family — consumed as hsl(var(--token)) triplets.
    '--background': hslTriplet(neutral.tone(100)),
    '--foreground': hslTriplet(variant.tone(15)),
    '--card': hslTriplet(neutral.tone(99)),
    '--card-foreground': hslTriplet(variant.tone(15)),
    '--popover': hslTriplet(neutral.tone(100)),
    '--popover-foreground': hslTriplet(variant.tone(15)),
    '--primary': hslTriplet(primary.tone(40)),
    '--primary-foreground': hslTriplet(primary.tone(100)),
    '--secondary': hslTriplet(neutral.tone(94)),
    '--secondary-foreground': hslTriplet(variant.tone(15)),
    '--muted': hslTriplet(neutral.tone(94)),
    '--muted-foreground': hslTriplet(variant.tone(45)),
    '--accent': hslTriplet(neutral.tone(92)),
    '--accent-foreground': hslTriplet(variant.tone(15)),
    '--border': hslTriplet(variant.tone(85)),
    '--input': hslTriplet(variant.tone(82)),
    '--ring': hslTriplet(primary.tone(40)),
  }
}

function rgbTriplet(color: number): string {
  return `${(color >> 16) & 0xff}, ${(color >> 8) & 0xff}, ${color & 0xff}`
}

/** Parse `#rrggbb` into an opaque ARGB int (unsigned). */
export function argbFromHex(hex: string): number {
  const n = Number.parseInt(hex.replace('#', ''), 16)
  return ((0xff << 24) | (n & 0xffffff)) >>> 0
}

/** Serialize an ARGB int back to lowercase `#rrggbb`. */
export function hexFromArgbInt(argb: number): string {
  return `#${(argb & 0xffffff).toString(16).padStart(6, '0')}`
}

/** True for a syntactically valid `#rrggbb` (case-insensitive, `#` required). */
export function isValidHexColor(hex: string): boolean {
  return /^#[0-9a-fA-F]{6}$/.test(hex)
}

/**
 * HSL (h 0-360, s/l 0-100) → `#rrggbb`. The picker's three sliders are the
 * user-facing control; this is the single conversion into the pipeline's
 * ARGB world so every custom color travels the exact same path as a preset.
 */
export function hexFromHsl(h: number, s: number, l: number): string {
  const hue = ((h % 360) + 360) % 360
  const sat = Math.min(Math.max(s, 0), 100) / 100
  const lum = Math.min(Math.max(l, 0), 100) / 100
  const k = (n: number): number => (n + hue / 30) % 12
  const a = sat * Math.min(lum, 1 - lum)
  const f = (n: number): number =>
    lum - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)))
  const channel = (n: number): string =>
    Math.round(255 * f(n)).toString(16).padStart(2, '0')
  return `#${channel(0)}${channel(8)}${channel(4)}`
}

/** `#rrggbb` → `[h 0-360, s 0-100, l 0-100]`. Inverse of `hexFromHsl`. */
export function hslFromHex(hex: string): [number, number, number] {
  const n = Number.parseInt(hex.replace('#', ''), 16)
  const r = ((n >> 16) & 0xff) / 255
  const g = ((n >> 8) & 0xff) / 255
  const b = (n & 0xff) / 255
  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2
  const d = max - min
  if (d === 0) return [0, 0, Math.round(l * 100)]
  const s = d / (1 - Math.abs(2 * l - 1))
  let h: number
  if (max === r) h = ((g - b) / d + 6) % 6
  else if (max === g) h = (b - r) / d + 2
  else h = (r - g) / d + 4
  return [Math.round(h * 60), Math.round(s * 100), Math.round(l * 100)]
}

/** WCAG 2.x sRGB relative luminance (`0.0` black .. `1.0` white) for `#rrggbb`. */
export function relativeLuminance(hex: string): number {
  const n = Number.parseInt(hex.replace('#', ''), 16)
  const channel = (v: number): number => {
    const c = v / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * channel((n >> 16) & 0xff)
    + 0.7152 * channel((n >> 8) & 0xff)
    + 0.0722 * channel(n & 0xff)
}

/**
 * Pick a guaranteed-contrast ink foreground (`#09090b` or `#ffffff`) for a
 * solid primary/accent button fill using WCAG relative luminance.
 */
export function contrastForeground(bgHex: string): '#09090b' | '#ffffff' {
  return relativeLuminance(bgHex) > CONTRAST_LUMINANCE_THRESHOLD ? '#09090b' : '#ffffff'
}

/**
 * WCAG relative-luminance contrast ratio between two `#rrggbb` colors.
 * Used to warn (never block) when a custom accent would make brand text
 * unreadable against the surface it sits on.
 */
export function contrastRatio(a: string, b: string): number {
  const la = relativeLuminance(a)
  const lb = relativeLuminance(b)
  const [hi, lo] = la > lb ? [la, lb] : [lb, la]
  return (hi + 0.05) / (lo + 0.05)
}

/**
 * shadcn/Tailwind token family consumed as `hsl(var(--token))` — emit
 * "H S% L%" triplets derived from the same tonal palettes so primitives
 * (buttons, switches, badges, bg-card/bg-popover utilities, the body base)
 * follow the active color system instead of a fixed hue.
 */
function hslTriplet(color: number): string {
  const r = ((color >> 16) & 0xff) / 255
  const g = ((color >> 8) & 0xff) / 255
  const b = (color & 0xff) / 255
  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2
  let h = 0
  let s = 0
  if (max !== min) {
    const d = max - min
    s = d / (1 - Math.abs(2 * l - 1))
    if (max === r) h = ((g - b) / d + 6) % 6
    else if (max === g) h = (b - r) / d + 2
    else h = (r - g) / d + 4
    h *= 60
  }
  return `${h.toFixed(1)} ${(s * 100).toFixed(1)}% ${(l * 100).toFixed(1)}%`
}

/**
 * The five preview circles for the "follow background" / custom picker option:
 * dark accent / light accent / primary button / dark surface / light surface.
 */
export function previewSwatches(source: number): string[] {
  const hct = Hct.fromInt(source)
  if (hct.chroma < ACHROMATIC_CHROMA_THRESHOLD) {
    return ['#fafafa', '#18181b', '#27272a', '#09090b', '#fafafa']
  }
  const chroma = Math.min(Math.max(hct.chroma, PRIMARY_MIN_CHROMA), PRIMARY_MAX_CHROMA)
  const primary = TonalPalette.fromHueAndChroma(hct.hue, chroma)
  const neutral = TonalPalette.fromHueAndChroma(hct.hue, 8)
  return [
    hexFromArgb(primary.tone(80)),
    hexFromArgb(primary.tone(40)),
    hexFromArgb(primary.tone(45)),
    hexFromArgb(neutral.tone(6)),
    hexFromArgb(neutral.tone(96)),
  ]
}

/** Every property buildDynamicVars or buildMonochromeVars may emit — used to strip injected values. */
export const DYNAMIC_VAR_NAMES: readonly string[] = Object.keys({
  ...buildDynamicVars(0xff58a6ff, 'dark'),
  ...buildDynamicVars(0xff58a6ff, 'light'),
  ...buildMonochromeVars('dark', 'neutral'),
  ...buildMonochromeVars('light', 'neutral'),
  ...buildMonochromeVars('dark', 'warm'),
  ...buildMonochromeVars('light', 'warm'),
})
