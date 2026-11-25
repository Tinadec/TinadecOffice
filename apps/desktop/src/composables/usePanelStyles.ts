/**
 * Panel styles management composable for TinadecOffice Desktop.
 *
 * Implements the three-layer global material engine:
 * - Layer 1 (Panel shells): inline styles from computePanelStyle() + data-panel-effect
 *   + --material-glass-border / --material-glass-highlight in blur mode.
 * - Layer 2 (Interior canvas & surfaces): --surface-canvas (var(--bg-primary) when
 *   opaque, transparent when translucent/blur) and --surface-* alpha tiers in styles.css.
 * - Layer 3 (Floating islands, popovers, dialogs, spatial chrome): reactive :root
 *   synchronization of data-global-panel-effect, data-panel-preset, and
 *   --material-floating-{bg,filter,border}.
 */

import { useStorage } from '@vueuse/core'
import { effectScope, ref, watch, type EffectScope, type Ref } from 'vue'
import {
  type NonCustomPanelMaterialPresetId,
  type PanelEffect,
  type PanelMaterialPresetId,
  type PanelMaterialPresetSpec,
  type PanelStyleSettings,
  DEFAULT_PANEL_STYLE_SETTINGS,
  PANEL_MATERIAL_PRESETS,
  PANEL_MATERIAL_PRESET_ORDER,
} from '../types/background'

export {
  DEFAULT_PANEL_STYLE_SETTINGS,
  PANEL_MATERIAL_PRESETS,
  PANEL_MATERIAL_PRESET_ORDER,
  type NonCustomPanelMaterialPresetId,
  type PanelEffect,
  type PanelMaterialPresetId,
  type PanelMaterialPresetSpec,
  type PanelStyleSettings,
}

// Storage key for the global panel style
const STORAGE_KEY = 'tinadec-panel-style'
const PANEL_EFFECTS = new Set<PanelEffect>(['opaque', 'translucent', 'blur'])
const PANEL_PRESET_IDS = new Set<PanelMaterialPresetId>([
  'solid',
  'frosted',
  'crystal',
  'mica',
  'translucent',
  'custom',
])

/**
 * Clamp a number to the given range.
 */
function clamp(val: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, val))
}

function isNonCustomPresetId(value: unknown): value is NonCustomPanelMaterialPresetId {
  return typeof value === 'string' && value !== 'custom' && PANEL_PRESET_IDS.has(value as PanelMaterialPresetId)
}

/**
 * Match exact preset parameters against the 5 canonical presets.
 * When `explicitSaturation` is omitted (legacy `{ effect, opacity, blur }` payloads),
 * matches on `(effect, opacity, blur)` and adopts that preset's optical saturation.
 */
export function matchPanelMaterialPreset(
  effect: PanelEffect,
  opacity: number,
  blur: number,
  explicitSaturation?: number,
): PanelMaterialPresetSpec | null {
  for (const id of PANEL_MATERIAL_PRESET_ORDER) {
    const spec = PANEL_MATERIAL_PRESETS[id]
    if (
      spec.effect === effect
      && spec.opacity === opacity
      && spec.blur === blur
      && (explicitSaturation === undefined || spec.saturation === explicitSaturation)
    ) {
      return spec
    }
  }
  return null
}

/**
 * Normalize & migrate raw persisted or partial panel style settings.
 *
 * Legacy migration contract:
 * `{ effect, opacity, blur }` without `preset` / `saturation` matches a canonical
 * preset if exact, else resolves to `'custom'` with `saturation = effect === 'blur' ? 160 : 100`.
 */
export function normalizePanelStyle(value: unknown): PanelStyleSettings {
  const candidate = typeof value === 'object' && value !== null
    ? value as Partial<PanelStyleSettings>
    : {}

  const rawPreset = typeof candidate.preset === 'string' && PANEL_PRESET_IDS.has(candidate.preset as PanelMaterialPresetId)
    ? candidate.preset as PanelMaterialPresetId
    : undefined

  const fallbackSpec = rawPreset && rawPreset !== 'custom'
    ? PANEL_MATERIAL_PRESETS[rawPreset]
    : DEFAULT_PANEL_STYLE_SETTINGS

  const effect: PanelEffect = PANEL_EFFECTS.has(candidate.effect as PanelEffect)
    ? candidate.effect as PanelEffect
    : fallbackSpec.effect

  const opacity = typeof candidate.opacity === 'number' && Number.isFinite(candidate.opacity)
    ? clamp(candidate.opacity, 0, 100)
    : fallbackSpec.opacity

  const blur = typeof candidate.blur === 'number' && Number.isFinite(candidate.blur)
    ? clamp(candidate.blur, 0, 32)
    : fallbackSpec.blur

  const explicitSaturation = typeof candidate.saturation === 'number' && Number.isFinite(candidate.saturation)
    ? clamp(candidate.saturation, 100, 200)
    : undefined

  if (rawPreset === 'custom') {
    return {
      preset: 'custom',
      effect,
      opacity,
      blur,
      saturation: explicitSaturation ?? (effect === 'blur' ? 160 : 100),
    }
  }

  if (rawPreset) {
    const spec = PANEL_MATERIAL_PRESETS[rawPreset]
    const saturation = explicitSaturation ?? spec.saturation
    if (
      effect === spec.effect
      && opacity === spec.opacity
      && blur === spec.blur
      && saturation === spec.saturation
    ) {
      return {
        preset: rawPreset,
        effect: spec.effect,
        opacity: spec.opacity,
        blur: spec.blur,
        saturation: spec.saturation,
      }
    }
    const otherMatch = matchPanelMaterialPreset(effect, opacity, blur, saturation)
    if (otherMatch) {
      return {
        preset: otherMatch.id,
        effect: otherMatch.effect,
        opacity: otherMatch.opacity,
        blur: otherMatch.blur,
        saturation: otherMatch.saturation,
      }
    }
    return {
      preset: 'custom',
      effect,
      opacity,
      blur,
      saturation,
    }
  }

  // Legacy or preset-less payload: match exact preset or mark 'custom'
  const matched = matchPanelMaterialPreset(effect, opacity, blur, explicitSaturation)
  if (matched) {
    return {
      preset: matched.id,
      effect: matched.effect,
      opacity: matched.opacity,
      blur: matched.blur,
      saturation: matched.saturation,
    }
  }

  return {
    preset: 'custom',
    effect,
    opacity,
    blur,
    saturation: explicitSaturation ?? (effect === 'blur' ? 160 : 100),
  }
}

/**
 * Synchronize global material dataset attributes and Layer 1 / Layer 3 CSS custom
 * properties onto `:root` (`document.documentElement`).
 */
export function syncPanelStyleToRoot(
  settings: PanelStyleSettings,
  root: HTMLElement | null = typeof document !== 'undefined' ? document.documentElement : null,
): void {
  if (!root) return

  const alpha = clamp(Number.isFinite(settings.opacity) ? settings.opacity : 100, 0, 100) / 100
  const blur = clamp(Number.isFinite(settings.blur) ? settings.blur : 0, 0, 32)
  const saturation = clamp(Number.isFinite(settings.saturation) ? settings.saturation : 160, 100, 200)

  root.setAttribute('data-global-panel-effect', settings.effect)
  root.setAttribute('data-panel-preset', settings.preset)

  switch (settings.effect) {
    case 'opaque':
      root.style.setProperty('--material-floating-bg', 'var(--bg-secondary)')
      root.style.setProperty('--material-floating-filter', 'none')
      root.style.setProperty('--material-floating-border', 'var(--border-default)')
      root.style.setProperty('--material-panel-bg', 'var(--bg-secondary)')
      root.style.setProperty('--material-panel-filter', 'none')
      root.style.setProperty('--material-panel-border', 'var(--border-card)')
      break
    case 'translucent': {
      const bg = `rgba(var(--bg-primary-rgb, 10, 14, 20), ${alpha})`
      root.style.setProperty('--material-floating-bg', bg)
      root.style.setProperty('--material-floating-filter', 'none')
      root.style.setProperty('--material-floating-border', 'var(--border-muted)')
      root.style.setProperty('--material-panel-bg', bg)
      root.style.setProperty('--material-panel-filter', 'none')
      root.style.setProperty('--material-panel-border', 'var(--border-card)')
      break
    }
    case 'blur': {
      const bg = `rgba(var(--bg-primary-rgb, 10, 14, 20), ${alpha})`
      const filter = `blur(${blur}px) saturate(${saturation}%)`
      root.style.setProperty('--material-floating-bg', bg)
      root.style.setProperty('--material-floating-filter', filter)
      root.style.setProperty('--material-floating-border', 'var(--material-glass-border)')
      root.style.setProperty('--material-panel-bg', bg)
      root.style.setProperty('--material-panel-filter', filter)
      root.style.setProperty('--material-panel-border', 'var(--material-glass-border)')
      break
    }
  }
}

/**
 * Get stored global panel style reference (lazy singleton initialization)
 */
let stored: Ref<PanelStyleSettings> | null = null
let storageScope: EffectScope | null = null

export function __resetPanelStylesForTests(): void {
  storageScope?.stop()
  storageScope = null
  stored = null
  if (typeof document !== 'undefined' && document.documentElement) {
    const root = document.documentElement
    root.removeAttribute('data-global-panel-effect')
    root.removeAttribute('data-panel-preset')
    root.style.removeProperty('--material-floating-bg')
    root.style.removeProperty('--material-floating-filter')
    root.style.removeProperty('--material-floating-border')
    root.style.removeProperty('--material-panel-bg')
    root.style.removeProperty('--material-panel-filter')
    root.style.removeProperty('--material-panel-border')
  }
}

function getStoredPanelStyle(): Ref<PanelStyleSettings> {
  if (!stored) {
    const scope = effectScope(true)
    const storageRef = scope.run(() => {
      const ref0 = useStorage<PanelStyleSettings>(
        STORAGE_KEY,
        { ...DEFAULT_PANEL_STYLE_SETTINGS },
        undefined,
        {
          flush: 'sync',
          mergeDefaults: (storageValue) => normalizePanelStyle(storageValue),
        },
      )
      ref0.value = normalizePanelStyle(ref0.value)
      watch(
        ref0,
        (next) => {
          syncPanelStyleToRoot(next)
        },
        { immediate: true, flush: 'sync' },
      )
      return ref0
    })

    if (!storageRef) {
      scope.stop()
      throw new Error('Failed to initialize panel style storage')
    }

    storageScope = scope
    stored = storageRef
  }
  return stored
}

/**
 * Compute inline CSS style object based on the global effect settings.
 *
 * - opaque:      No special styling (solid background from CSS)
 * - translucent: Semi-transparent background using rgba() with configured opacity
 * - blur:        Frosted glass: backdrop-filter blur + saturate, semi-transparent
 *                background, glass edge border, and subtle inner top highlight
 */
export function computePanelStyle(
  settings: Pick<PanelStyleSettings, 'effect' | 'opacity' | 'blur'> & Partial<Pick<PanelStyleSettings, 'preset' | 'saturation'>>,
): Record<string, string> {
  const style: Record<string, string> = {}
  const rawOpacity = typeof settings.opacity === 'number' && Number.isFinite(settings.opacity) ? settings.opacity : 100
  const alpha = clamp(rawOpacity, 0, 100) / 100

  switch (settings.effect) {
    case 'opaque':
      // Solid background — no inline overrides needed
      break
    case 'translucent':
      style.backgroundColor = `rgba(var(--bg-primary-rgb, 10, 14, 20), ${alpha})`
      break
    case 'blur': {
      const rawBlur = typeof settings.blur === 'number' && Number.isFinite(settings.blur) ? settings.blur : 0
      const blur = clamp(rawBlur, 0, 32)
      const rawSat = typeof settings.saturation === 'number' && Number.isFinite(settings.saturation)
        ? settings.saturation
        : 160
      const saturation = clamp(rawSat, 100, 200)
      const filterValue = `blur(${blur}px) saturate(${saturation}%)`

      // Use both standard and -webkit prefixed for Chromium/Electron compatibility
      style.backdropFilter = filterValue
      style.WebkitBackdropFilter = filterValue
      style.backgroundColor = `rgba(var(--bg-primary-rgb, 10, 14, 20), ${alpha})`
      style.borderColor = 'var(--material-glass-border)'
      style.boxShadow = 'var(--material-glass-highlight)'

      if (blur > 0) {
        const sectionBlur = Math.round(blur * 20) / 100
        const raisedBlur = Math.round(blur * 35) / 100
        const sectionSat = Math.round(100 + (saturation - 100) * 0.25)
        const raisedSat = Math.round(100 + (saturation - 100) * 0.4)
        style['--material-filter-section'] = `blur(${sectionBlur}px) saturate(${sectionSat}%)`
        style['--material-filter-raised'] = `blur(${raisedBlur}px) saturate(${raisedSat}%)`
      } else {
        style['--material-filter-section'] = 'none'
        style['--material-filter-raised'] = 'none'
      }
      break
    }
  }

  return style
}

export function usePanelStyles() {
  const panelStyle = getStoredPanelStyle()
  const isApplying = ref(false)

  /**
   * Apply a named preset (or enter 'custom' mode while preserving optical continuity).
   */
  function applyPreset(preset: PanelMaterialPresetId): PanelStyleSettings {
    if (preset === 'custom') {
      const current = panelStyle.value
      if (current.effect === 'opaque') {
        const frosted = PANEL_MATERIAL_PRESETS.frosted
        panelStyle.value = {
          preset: 'custom',
          effect: 'blur',
          opacity: frosted.opacity,
          blur: frosted.blur,
          saturation: frosted.saturation,
        }
      } else {
        panelStyle.value = normalizePanelStyle({
          ...current,
          preset: 'custom',
        })
      }
      return panelStyle.value
    }

    if (isNonCustomPresetId(preset)) {
      const spec = PANEL_MATERIAL_PRESETS[preset]
      panelStyle.value = {
        preset: spec.id,
        effect: spec.effect,
        opacity: spec.opacity,
        blur: spec.blur,
        saturation: spec.saturation,
      }
    }
    return panelStyle.value
  }

  /**
   * Update the global material style settings (partial merge).
   * - If `patch.preset` is a named preset, uses that preset's canonical values as base
   *   unless overridden in `patch`.
   * - If `patch.preset === 'custom'`, keeps `'custom'` explicitly.
   * - If `patch.preset` is omitted, manual parameter updates resolve to a matching preset
   *   if exact, or `'custom'` when deviating from presets.
   */
  function updatePanelStyle(patch: Partial<PanelStyleSettings>): PanelStyleSettings {
    if (patch.preset === 'custom') {
      const current = panelStyle.value
      const nextEffect = patch.effect ?? (current.effect === 'opaque' ? 'blur' : current.effect)
      const base = current.effect === 'opaque' && patch.opacity === undefined && patch.blur === undefined
        ? PANEL_MATERIAL_PRESETS.frosted
        : current
      panelStyle.value = normalizePanelStyle({
        ...base,
        ...patch,
        effect: nextEffect,
        preset: 'custom',
      })
      return panelStyle.value
    }

    if (isNonCustomPresetId(patch.preset)) {
      const spec = PANEL_MATERIAL_PRESETS[patch.preset]
      panelStyle.value = normalizePanelStyle({
        preset: spec.id,
        effect: spec.effect,
        opacity: spec.opacity,
        blur: spec.blur,
        saturation: spec.saturation,
        ...patch,
      })
      return panelStyle.value
    }

    const { preset: _previousPreset, ...currentParams } = panelStyle.value
    panelStyle.value = normalizePanelStyle({
      ...currentParams,
      ...patch,
    })
    return panelStyle.value
  }

  /**
   * Set the global effect type.
   */
  function setPanelEffect(effect: PanelEffect): PanelStyleSettings {
    if (effect === 'opaque') {
      return applyPreset('solid')
    }
    if (panelStyle.value.preset !== 'custom') {
      return applyPreset(effect === 'blur' ? 'frosted' : 'translucent')
    }
    if (effect === 'blur') {
      return updatePanelStyle({
        preset: 'custom',
        effect: 'blur',
        blur: panelStyle.value.blur > 0 ? panelStyle.value.blur : PANEL_MATERIAL_PRESETS.frosted.blur,
        saturation: panelStyle.value.saturation > 100
          ? panelStyle.value.saturation
          : PANEL_MATERIAL_PRESETS.frosted.saturation,
      })
    }
    return updatePanelStyle({
      preset: 'custom',
      effect: 'translucent',
    })
  }

  /**
   * Reset the global material style to recommended defaults ('frosted').
   */
  function resetPanelStyle(): PanelStyleSettings {
    panelStyle.value = { ...DEFAULT_PANEL_STYLE_SETTINGS }
    return panelStyle.value
  }

  /**
   * Get computed inline style for a panel (for Vue :style binding).
   * All panels share the same global material setting.
   */
  function getPanelStyle(_panelName?: string): Record<string, string> {
    return computePanelStyle(panelStyle.value)
  }

  /**
   * Get data attributes for CSS targeting.
   * All panels share the same global material setting.
   */
  function getPanelDataAttributes(_panelName?: string): Record<string, string> {
    return {
      'data-panel-effect': panelStyle.value.effect,
      'data-panel-preset': panelStyle.value.preset,
    }
  }

  return {
    // State
    panelStyle,
    isApplying,

    // Methods
    applyPreset,
    updatePanelStyle,
    setPanelEffect,
    resetPanelStyle,
    getPanelStyle,
    getPanelDataAttributes,
  }
}
