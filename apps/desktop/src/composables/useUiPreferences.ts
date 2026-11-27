import { computed, effectScope, onScopeDispose, reactive, ref, watch, type ComputedRef, type EffectScope, type Ref } from 'vue'
import {
  api,
  type PatchUiPreferencesInput,
  type PutUiPreferencesInput,
  type UiAccentPreference,
  type UiAppearancePreferencesDto,
  type UiInteractionPreferencesDto,
  type UiPanelEffectPreference,
  type UiPanelPresetPreference,
  type UiPreferencesDocumentDto,
  type UiReducedMotionPreference,
  type UiSpatialPromoteMode,
  type UiThemePreference,
  type UiWorkspaceDropAction,
  type UiWorkspaceFolderTone,
  type UiWorkspaceViewPreference,
} from '@/api'
import { useTheme, type Theme } from '@/composables/useTheme'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useHostAccess } from '@/lib/hostAccess'
import { isValidHexColor } from '@/lib/monetExtract'
import { ScopedEventSource } from '@/lib/scopedEventSource'
import type { PanelEffect, PanelStyleSettings } from '@/types/background'

export type {
  PatchUiPreferencesInput,
  PutUiPreferencesInput,
  UiAccentPreference,
  UiAppearancePreferencesDto,
  UiInteractionPreferencesDto,
  UiPanelEffectPreference,
  UiPanelPresetPreference,
  UiPreferencesDocumentDto,
  UiReducedMotionPreference,
  UiSpatialPromoteMode,
  UiThemePreference,
  UiWorkspaceDropAction,
  UiWorkspaceFolderTone,
  UiWorkspaceViewPreference,
}

export interface NormalizedUiAppearancePreferencesDto extends UiAppearancePreferencesDto {
  panel_preset: UiPanelPresetPreference
  panel_opacity: number
  panel_blur: number
  panel_saturation: number
}

type NamedUiPanelPreset = Exclude<UiPanelPresetPreference, 'custom'>

type BridgeablePanelStyle = PanelStyleSettings & {
  preset?: UiPanelPresetPreference
  saturation?: number
}

export const UI_PREFERENCES_CACHE_KEY = 'tinadec-ui-preferences-cache'
export const UI_PREFERENCES_MIGRATED_KEY = 'tinadec-ui-preferences-migrated-v1'
const LEGACY_THEME_KEY = 'tinadec-theme'
const LEGACY_ACCENT_KEY = 'tinadec-accent-color'
const LEGACY_CUSTOM_ACCENT_KEY = 'tinadec-custom-accent'
const LEGACY_PANEL_STYLE_KEY = 'tinadec-panel-style'
const SPATIAL_PROMOTE_STORAGE_KEY = 'tinadec.projectBoard.spatialPromotePref.v1'

export const UI_THEMES: readonly UiThemePreference[] = ['dark', 'light', 'system'] as const
export const UI_ACCENTS: readonly UiAccentPreference[] = [
  'monochrome',
  'warm',
  'dynamic',
  'custom',
  'blue',
  'green',
  'orange',
  'purple',
  'pink',
  'red',
  'cyan',
  'yellow',
] as const
export const UI_PANEL_EFFECTS: readonly UiPanelEffectPreference[] = ['solid', 'translucent', 'blur'] as const
export const UI_PANEL_PRESETS: readonly UiPanelPresetPreference[] = [
  'solid',
  'frosted',
  'crystal',
  'mica',
  'translucent',
  'custom',
] as const
export const UI_NAMED_PANEL_PRESETS: readonly NamedUiPanelPreset[] = [
  'solid',
  'frosted',
  'crystal',
  'mica',
  'translucent',
] as const
export const UI_WORKSPACE_VIEWS: readonly UiWorkspaceViewPreference[] = ['grid', 'list'] as const
export const UI_WORKSPACE_FOLDER_TONES: readonly UiWorkspaceFolderTone[] = [
  'amber',
  'teal',
  'sky',
  'coral',
  'frost',
] as const
export const UI_WORKSPACE_DROP_ACTIONS: readonly UiWorkspaceDropAction[] = ['link', 'copy', 'ask'] as const
export const UI_SPATIAL_PROMOTE_MODES: readonly UiSpatialPromoteMode[] = [
  'merge',
  'split',
  'parent_children',
] as const
export const UI_REDUCED_MOTION_MODES: readonly UiReducedMotionPreference[] = ['system', 'on', 'off'] as const

export const UI_PANEL_PRESET_DEFAULTS: Readonly<
  Record<
    NamedUiPanelPreset,
    {
      panel_effect: UiPanelEffectPreference
      panel_opacity: number
      panel_blur: number
      panel_saturation: number
    }
  >
> = {
  solid: { panel_effect: 'solid', panel_opacity: 100, panel_blur: 0, panel_saturation: 100 },
  frosted: { panel_effect: 'blur', panel_opacity: 68, panel_blur: 18, panel_saturation: 160 },
  crystal: { panel_effect: 'blur', panel_opacity: 48, panel_blur: 24, panel_saturation: 180 },
  mica: { panel_effect: 'blur', panel_opacity: 82, panel_blur: 14, panel_saturation: 130 },
  translucent: { panel_effect: 'translucent', panel_opacity: 76, panel_blur: 0, panel_saturation: 100 },
}

export const DEFAULT_UI_APPEARANCE_PREFERENCES: NormalizedUiAppearancePreferencesDto = {
  theme: 'dark',
  accent: 'monochrome',
  custom_accent_hex: '#18181b',
  panel_effect: 'translucent',
  panel_preset: 'translucent',
  panel_opacity: 76,
  panel_blur: 0,
  panel_saturation: 100,
  workspace_view: 'grid',
  workspace_folder_tone: 'amber',
}

export const DEFAULT_UI_INTERACTION_PREFERENCES: UiInteractionPreferencesDto = {
  workspace_drop_action: 'link',
  spatial_promote_mode: 'merge',
  reduced_motion: 'system',
}

export function panelEffectToLocal(effect: UiPanelEffectPreference): PanelEffect {
  if (effect === 'solid') return 'opaque'
  if (effect === 'blur') return 'blur'
  return 'translucent'
}

export function panelEffectFromLocal(effect: string | null | undefined): UiPanelEffectPreference {
  if (effect === 'opaque' || effect === 'solid') return 'solid'
  if (effect === 'blur') return 'blur'
  return 'translucent'
}

export function defaultPanelPresetForEffect(effect: UiPanelEffectPreference): NamedUiPanelPreset {
  if (effect === 'solid') return 'solid'
  if (effect === 'blur') return 'frosted'
  return 'translucent'
}

export function matchCanonicalPanelPreset(
  effect: UiPanelEffectPreference,
  opacity: number,
  blur: number,
  saturation: number,
): NamedUiPanelPreset | null {
  for (const preset of UI_NAMED_PANEL_PRESETS) {
    const spec = UI_PANEL_PRESET_DEFAULTS[preset]
    if (
      spec.panel_effect === effect
      && spec.panel_opacity === opacity
      && spec.panel_blur === blur
      && spec.panel_saturation === saturation
    ) {
      return preset
    }
  }
  return null
}

export function spatialPromoteToBoardMode(
  mode: UiSpatialPromoteMode,
): 'merge_single' | 'split_many' | 'parent_with_children' {
  if (mode === 'split') return 'split_many'
  if (mode === 'parent_children') return 'parent_with_children'
  return 'merge_single'
}

export function spatialPromoteFromBoardMode(mode: string | null | undefined): UiSpatialPromoteMode {
  if (mode === 'split_many' || mode === 'split') return 'split'
  if (mode === 'parent_with_children' || mode === 'parent_children') return 'parent_children'
  return 'merge'
}

function isOneOf<T extends string>(list: readonly T[], value: unknown): value is T {
  return typeof value === 'string' && (list as readonly string[]).includes(value)
}

function normalizeOptionalPanelEffect(value: unknown): UiPanelEffectPreference | undefined {
  if (value === 'opaque') return 'solid'
  return isOneOf(UI_PANEL_EFFECTS, value) ? value : undefined
}

function normalizeOptionalInt(value: unknown, min: number, max: number): number | undefined {
  if (typeof value !== 'number' || !Number.isFinite(value)) return undefined
  const rounded = Math.round(value)
  return Math.max(min, Math.min(max, rounded))
}

function resolveFallbackAppearance(
  fallback: UiAppearancePreferencesDto,
): NormalizedUiAppearancePreferencesDto {
  const fbEffect = normalizeOptionalPanelEffect(fallback.panel_effect) ?? 'translucent'
  const fbDerivedPreset = defaultPanelPresetForEffect(fbEffect)
  const fbSpec = UI_PANEL_PRESET_DEFAULTS[fbDerivedPreset]
  const fbPreset = isOneOf(UI_PANEL_PRESETS, fallback.panel_preset)
    ? fallback.panel_preset
    : fbDerivedPreset
  const presetSpec =
    fbPreset !== 'custom' ? UI_PANEL_PRESET_DEFAULTS[fbPreset] : fbSpec

  return {
    theme: isOneOf(UI_THEMES, fallback.theme) ? fallback.theme : DEFAULT_UI_APPEARANCE_PREFERENCES.theme,
    accent: isOneOf(UI_ACCENTS, fallback.accent) ? fallback.accent : DEFAULT_UI_APPEARANCE_PREFERENCES.accent,
    custom_accent_hex:
      typeof fallback.custom_accent_hex === 'string' && isValidHexColor(fallback.custom_accent_hex)
        ? fallback.custom_accent_hex.toLowerCase()
        : DEFAULT_UI_APPEARANCE_PREFERENCES.custom_accent_hex,
    panel_effect: fbEffect,
    panel_preset: fbPreset,
    panel_opacity: normalizeOptionalInt(fallback.panel_opacity, 0, 100) ?? presetSpec.panel_opacity,
    panel_blur: normalizeOptionalInt(fallback.panel_blur, 0, 32) ?? presetSpec.panel_blur,
    panel_saturation: normalizeOptionalInt(fallback.panel_saturation, 100, 200) ?? presetSpec.panel_saturation,
    workspace_view: isOneOf(UI_WORKSPACE_VIEWS, fallback.workspace_view)
      ? fallback.workspace_view
      : DEFAULT_UI_APPEARANCE_PREFERENCES.workspace_view,
    workspace_folder_tone: isOneOf(UI_WORKSPACE_FOLDER_TONES, fallback.workspace_folder_tone)
      ? fallback.workspace_folder_tone
      : DEFAULT_UI_APPEARANCE_PREFERENCES.workspace_folder_tone,
  }
}

export function normalizeAppearancePreferences(
  raw: Partial<UiAppearancePreferencesDto> | null | undefined,
  fallback: UiAppearancePreferencesDto = DEFAULT_UI_APPEARANCE_PREFERENCES,
): NormalizedUiAppearancePreferencesDto {
  const base = resolveFallbackAppearance(fallback)
  const candidate = raw && typeof raw === 'object' ? raw : {}

  const explicitEffect = normalizeOptionalPanelEffect(candidate.panel_effect)
  const explicitPreset = isOneOf(UI_PANEL_PRESETS, candidate.panel_preset)
    ? candidate.panel_preset
    : undefined
  const explicitOpacity = normalizeOptionalInt(candidate.panel_opacity, 0, 100)
  const explicitBlur = normalizeOptionalInt(candidate.panel_blur, 0, 32)
  const explicitSaturation = normalizeOptionalInt(candidate.panel_saturation, 100, 200)

  let panelEffect: UiPanelEffectPreference
  let panelPreset: UiPanelPresetPreference
  let panelOpacity: number
  let panelBlur: number
  let panelSaturation: number

  if (explicitPreset && explicitPreset !== 'custom') {
    const spec = UI_PANEL_PRESET_DEFAULTS[explicitPreset]
    panelPreset = explicitPreset
    panelEffect = explicitEffect ?? spec.panel_effect
    panelOpacity = explicitOpacity ?? spec.panel_opacity
    panelBlur = explicitBlur ?? spec.panel_blur
    panelSaturation = explicitSaturation ?? spec.panel_saturation
  } else if (explicitPreset === 'custom') {
    panelEffect = explicitEffect ?? base.panel_effect
    const effectSpec = UI_PANEL_PRESET_DEFAULTS[defaultPanelPresetForEffect(panelEffect)]
    panelPreset = 'custom'
    panelOpacity = explicitOpacity ?? effectSpec.panel_opacity
    panelBlur = explicitBlur ?? effectSpec.panel_blur
    panelSaturation = explicitSaturation ?? effectSpec.panel_saturation
  } else if (
    explicitEffect !== undefined
    || explicitOpacity !== undefined
    || explicitBlur !== undefined
    || explicitSaturation !== undefined
  ) {
    panelEffect = explicitEffect ?? base.panel_effect
    const effectSpec = UI_PANEL_PRESET_DEFAULTS[defaultPanelPresetForEffect(panelEffect)]
    panelOpacity = explicitOpacity ?? effectSpec.panel_opacity
    panelBlur = explicitBlur ?? effectSpec.panel_blur
    panelSaturation = explicitSaturation ?? effectSpec.panel_saturation
    panelPreset =
      matchCanonicalPanelPreset(panelEffect, panelOpacity, panelBlur, panelSaturation) ?? 'custom'
  } else {
    panelEffect = base.panel_effect
    panelPreset = base.panel_preset
    panelOpacity = base.panel_opacity
    panelBlur = base.panel_blur
    panelSaturation = base.panel_saturation
  }

  return {
    theme: isOneOf(UI_THEMES, candidate.theme) ? candidate.theme : base.theme,
    accent: isOneOf(UI_ACCENTS, candidate.accent) ? candidate.accent : base.accent,
    custom_accent_hex:
      typeof candidate.custom_accent_hex === 'string' && isValidHexColor(candidate.custom_accent_hex)
        ? candidate.custom_accent_hex.toLowerCase()
        : base.custom_accent_hex,
    panel_effect: panelEffect,
    panel_preset: panelPreset,
    panel_opacity: panelOpacity,
    panel_blur: panelBlur,
    panel_saturation: panelSaturation,
    workspace_view: isOneOf(UI_WORKSPACE_VIEWS, candidate.workspace_view)
      ? candidate.workspace_view
      : base.workspace_view,
    workspace_folder_tone: isOneOf(UI_WORKSPACE_FOLDER_TONES, candidate.workspace_folder_tone)
      ? candidate.workspace_folder_tone
      : base.workspace_folder_tone,
  }
}

export function mergeAppearancePreferences(
  current: UiAppearancePreferencesDto,
  patch: Partial<UiAppearancePreferencesDto> | null | undefined,
): NormalizedUiAppearancePreferencesDto {
  const base = resolveFallbackAppearance(current)
  if (!patch || typeof patch !== 'object') {
    return base
  }

  const explicitEffect = normalizeOptionalPanelEffect(patch.panel_effect)
  const explicitPreset = isOneOf(UI_PANEL_PRESETS, patch.panel_preset) ? patch.panel_preset : undefined
  const explicitOpacity = normalizeOptionalInt(patch.panel_opacity, 0, 100)
  const explicitBlur = normalizeOptionalInt(patch.panel_blur, 0, 32)
  const explicitSaturation = normalizeOptionalInt(patch.panel_saturation, 100, 200)

  let panelEffect = base.panel_effect
  let panelPreset = base.panel_preset
  let panelOpacity = base.panel_opacity
  let panelBlur = base.panel_blur
  let panelSaturation = base.panel_saturation

  if (explicitPreset && explicitPreset !== 'custom') {
    const spec = UI_PANEL_PRESET_DEFAULTS[explicitPreset]
    panelPreset = explicitPreset
    panelEffect = explicitEffect ?? spec.panel_effect
    panelOpacity = explicitOpacity ?? spec.panel_opacity
    panelBlur = explicitBlur ?? spec.panel_blur
    panelSaturation = explicitSaturation ?? spec.panel_saturation
  } else if (explicitPreset === 'custom') {
    panelEffect = explicitEffect ?? base.panel_effect
    const effectChanged = explicitEffect !== undefined && explicitEffect !== base.panel_effect
    const effectSpec = UI_PANEL_PRESET_DEFAULTS[defaultPanelPresetForEffect(panelEffect)]
    panelPreset = 'custom'
    panelOpacity = explicitOpacity ?? (effectChanged ? effectSpec.panel_opacity : base.panel_opacity)
    panelBlur = explicitBlur ?? (effectChanged ? effectSpec.panel_blur : base.panel_blur)
    panelSaturation = explicitSaturation ?? (effectChanged ? effectSpec.panel_saturation : base.panel_saturation)
  } else if (
    explicitEffect !== undefined
    && explicitOpacity === undefined
    && explicitBlur === undefined
    && explicitSaturation === undefined
  ) {
    const derived = defaultPanelPresetForEffect(explicitEffect)
    const spec = UI_PANEL_PRESET_DEFAULTS[derived]
    panelPreset = derived
    panelEffect = spec.panel_effect
    panelOpacity = spec.panel_opacity
    panelBlur = spec.panel_blur
    panelSaturation = spec.panel_saturation
  } else if (
    explicitEffect !== undefined
    || explicitOpacity !== undefined
    || explicitBlur !== undefined
    || explicitSaturation !== undefined
  ) {
    panelEffect = explicitEffect ?? base.panel_effect
    const effectChanged = explicitEffect !== undefined && explicitEffect !== base.panel_effect
    const effectSpec = UI_PANEL_PRESET_DEFAULTS[defaultPanelPresetForEffect(panelEffect)]
    panelOpacity = explicitOpacity ?? (effectChanged ? effectSpec.panel_opacity : base.panel_opacity)
    panelBlur = explicitBlur ?? (effectChanged ? effectSpec.panel_blur : base.panel_blur)
    panelSaturation = explicitSaturation ?? (effectChanged ? effectSpec.panel_saturation : base.panel_saturation)
    panelPreset =
      matchCanonicalPanelPreset(panelEffect, panelOpacity, panelBlur, panelSaturation) ?? 'custom'
  }

  return {
    theme: isOneOf(UI_THEMES, patch.theme) ? patch.theme : base.theme,
    accent: isOneOf(UI_ACCENTS, patch.accent) ? patch.accent : base.accent,
    custom_accent_hex:
      typeof patch.custom_accent_hex === 'string' && isValidHexColor(patch.custom_accent_hex)
        ? patch.custom_accent_hex.toLowerCase()
        : base.custom_accent_hex,
    panel_effect: panelEffect,
    panel_preset: panelPreset,
    panel_opacity: panelOpacity,
    panel_blur: panelBlur,
    panel_saturation: panelSaturation,
    workspace_view: isOneOf(UI_WORKSPACE_VIEWS, patch.workspace_view)
      ? patch.workspace_view
      : base.workspace_view,
    workspace_folder_tone: isOneOf(UI_WORKSPACE_FOLDER_TONES, patch.workspace_folder_tone)
      ? patch.workspace_folder_tone
      : base.workspace_folder_tone,
  }
}

export function normalizeInteractionPreferences(
  raw: Partial<UiInteractionPreferencesDto> | null | undefined,
  fallback: UiInteractionPreferencesDto = DEFAULT_UI_INTERACTION_PREFERENCES,
): UiInteractionPreferencesDto {
  const candidate = raw && typeof raw === 'object' ? raw : {}
  const rawSpatial = candidate.spatial_promote_mode as string | undefined
  let spatialMode: UiSpatialPromoteMode = fallback.spatial_promote_mode
  if (isOneOf(UI_SPATIAL_PROMOTE_MODES, rawSpatial)) {
    spatialMode = rawSpatial
  } else if (
    rawSpatial === 'merge_single'
    || rawSpatial === 'split_many'
    || rawSpatial === 'parent_with_children'
  ) {
    spatialMode = spatialPromoteFromBoardMode(rawSpatial)
  }

  return {
    workspace_drop_action: isOneOf(UI_WORKSPACE_DROP_ACTIONS, candidate.workspace_drop_action)
      ? candidate.workspace_drop_action
      : fallback.workspace_drop_action,
    spatial_promote_mode: spatialMode,
    reduced_motion: isOneOf(UI_REDUCED_MOTION_MODES, candidate.reduced_motion)
      ? candidate.reduced_motion
      : fallback.reduced_motion,
  }
}

export function normalizeUiPreferencesDocument(
  raw: unknown,
  fallbackAppearance: UiAppearancePreferencesDto = DEFAULT_UI_APPEARANCE_PREFERENCES,
  fallbackInteraction: UiInteractionPreferencesDto = DEFAULT_UI_INTERACTION_PREFERENCES,
): UiPreferencesDocumentDto & { appearance: NormalizedUiAppearancePreferencesDto } {
  const outer = raw && typeof raw === 'object' ? (raw as Record<string, unknown>) : {}
  const inner =
    outer.preferences && typeof outer.preferences === 'object'
      ? (outer.preferences as Record<string, unknown>)
      : outer

  const filePath =
    typeof inner.file_path === 'string'
      ? inner.file_path
      : typeof outer.file_path === 'string'
        ? outer.file_path
        : null
  const contentHash =
    typeof inner.content_hash === 'string'
      ? inner.content_hash
      : typeof outer.content_hash === 'string'
        ? outer.content_hash
        : null
  const version =
    typeof inner.version === 'number' && Number.isFinite(inner.version) ? inner.version : 1

  return {
    version,
    appearance: normalizeAppearancePreferences(
      inner.appearance as Partial<UiAppearancePreferencesDto> | undefined,
      fallbackAppearance,
    ),
    interaction: normalizeInteractionPreferences(
      inner.interaction as Partial<UiInteractionPreferencesDto> | undefined,
      fallbackInteraction,
    ),
    file_path: filePath,
    content_hash: contentHash,
  }
}

function readOsReducedMotion(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false
  try {
    return Boolean(window.matchMedia('(prefers-reduced-motion: reduce)')?.matches)
  } catch {
    return false
  }
}

export function resolveEffectiveReducedMotion(
  pref: UiReducedMotionPreference,
  osMatches: boolean = readOsReducedMotion(),
): boolean {
  if (pref === 'on') return true
  if (pref === 'off') return false
  return osMatches
}

interface LegacyMigrationSnapshot {
  patch: PatchUiPreferencesInput
  hasAny: boolean
}

function parseLegacyPanelStyleForMigration(rawPanel: string): Partial<UiAppearancePreferencesDto> | null {
  try {
    const parsed = JSON.parse(rawPanel) as {
      preset?: unknown
      effect?: unknown
      opacity?: unknown
      blur?: unknown
      saturation?: unknown
    }
    if (!parsed || typeof parsed !== 'object') return null

    const hasEffect = typeof parsed.effect === 'string'
    const hasPreset = isOneOf(UI_PANEL_PRESETS, parsed.preset)
    if (!hasEffect && !hasPreset) return null

    const mappedEffect = hasEffect
      ? panelEffectFromLocal(parsed.effect as string)
      : parsed.preset && parsed.preset !== 'custom'
        ? UI_PANEL_PRESET_DEFAULTS[parsed.preset as NamedUiPanelPreset].panel_effect
        : 'translucent'

    if (hasPreset) {
      const normalized = normalizeAppearancePreferences({
        panel_effect: mappedEffect,
        panel_preset: parsed.preset as UiPanelPresetPreference,
        panel_opacity: normalizeOptionalInt(parsed.opacity, 0, 100),
        panel_blur: normalizeOptionalInt(parsed.blur, 0, 32),
        panel_saturation: normalizeOptionalInt(parsed.saturation, 100, 200),
      })
      return {
        panel_effect: normalized.panel_effect,
        panel_preset: normalized.panel_preset,
        panel_opacity: normalized.panel_opacity,
        panel_blur: normalized.panel_blur,
        panel_saturation: normalized.panel_saturation,
      }
    }

    // Legacy localStorage { effect, opacity, blur } without preset:
    // In opaque mode, opacity/blur were inactive defaults (80/8) -> canonical 'solid' preset.
    if (mappedEffect === 'solid') {
      return {
        panel_effect: 'solid',
        panel_preset: 'solid',
        panel_opacity: 100,
        panel_blur: 0,
        panel_saturation: 100,
      }
    }

    if (mappedEffect === 'translucent') {
      const opacity = normalizeOptionalInt(parsed.opacity, 0, 100) ?? 76
      const preset = opacity === 76 ? 'translucent' : 'custom'
      return {
        panel_effect: 'translucent',
        panel_preset: preset,
        panel_opacity: opacity,
        panel_blur: 0,
        panel_saturation: 100,
      }
    }

    const opacity = normalizeOptionalInt(parsed.opacity, 0, 100) ?? 68
    const blur = normalizeOptionalInt(parsed.blur, 0, 32) ?? 18
    const saturation = normalizeOptionalInt(parsed.saturation, 100, 200) ?? 160
    const preset = matchCanonicalPanelPreset('blur', opacity, blur, saturation) ?? 'custom'
    return {
      panel_effect: 'blur',
      panel_preset: preset,
      panel_opacity: opacity,
      panel_blur: blur,
      panel_saturation: saturation,
    }
  } catch {
    return null
  }
}

function captureLegacyLocalMigration(): LegacyMigrationSnapshot {
  const appearance: Partial<UiAppearancePreferencesDto> = {}
  const interaction: Partial<UiInteractionPreferencesDto> = {}
  let hasAny = false

  try {
    if (typeof window === 'undefined' || !window.localStorage) {
      return { patch: {}, hasAny: false }
    }
    const rawTheme = window.localStorage.getItem(LEGACY_THEME_KEY)
    if (isOneOf(UI_THEMES, rawTheme)) {
      appearance.theme = rawTheme
      hasAny = true
    }

    const rawAccent = window.localStorage.getItem(LEGACY_ACCENT_KEY)
    if (isOneOf(UI_ACCENTS, rawAccent)) {
      appearance.accent = rawAccent
      hasAny = true
    }

    const rawHex = window.localStorage.getItem(LEGACY_CUSTOM_ACCENT_KEY)
    if (rawHex && isValidHexColor(rawHex)) {
      appearance.custom_accent_hex = rawHex.toLowerCase()
      hasAny = true
    }

    const rawPanel = window.localStorage.getItem(LEGACY_PANEL_STYLE_KEY)
    if (rawPanel) {
      const migratedPanel = parseLegacyPanelStyleForMigration(rawPanel)
      if (migratedPanel) {
        Object.assign(appearance, migratedPanel)
        hasAny = true
      }
    }

    const rawSpatial = window.localStorage.getItem(SPATIAL_PROMOTE_STORAGE_KEY)
    if (rawSpatial) {
      try {
        const parsed = JSON.parse(rawSpatial) as { mode?: string }
        if (
          parsed?.mode === 'merge_single'
          || parsed?.mode === 'split_many'
          || parsed?.mode === 'parent_with_children'
        ) {
          interaction.spatial_promote_mode = spatialPromoteFromBoardMode(parsed.mode)
          hasAny = true
        }
      } catch {
        // Ignore malformed spatial promotion JSON.
      }
    }
  } catch {
    return { patch: {}, hasAny: false }
  }

  const patch: PatchUiPreferencesInput = {}
  if (Object.keys(appearance).length > 0) patch.appearance = appearance
  if (Object.keys(interaction).length > 0) patch.interaction = interaction
  return { patch, hasAny }
}

function isDefaultServerPreferences(doc: UiPreferencesDocumentDto): boolean {
  const a = normalizeAppearancePreferences(doc.appearance)
  const i = doc.interaction
  const panelIsDefault =
    (a.panel_preset === 'translucent' && a.panel_effect === 'translucent')
    || (a.panel_preset === 'solid' && a.panel_effect === 'solid')
  return (
    a.theme === DEFAULT_UI_APPEARANCE_PREFERENCES.theme
    && a.accent === DEFAULT_UI_APPEARANCE_PREFERENCES.accent
    && a.custom_accent_hex.toLowerCase() === DEFAULT_UI_APPEARANCE_PREFERENCES.custom_accent_hex
    && panelIsDefault
    && a.workspace_view === DEFAULT_UI_APPEARANCE_PREFERENCES.workspace_view
    && a.workspace_folder_tone === DEFAULT_UI_APPEARANCE_PREFERENCES.workspace_folder_tone
    && i.workspace_drop_action === DEFAULT_UI_INTERACTION_PREFERENCES.workspace_drop_action
    && i.spatial_promote_mode === DEFAULT_UI_INTERACTION_PREFERENCES.spatial_promote_mode
    && i.reduced_motion === DEFAULT_UI_INTERACTION_PREFERENCES.reduced_motion
  )
}

function appearanceEquals(
  a: NormalizedUiAppearancePreferencesDto,
  b: NormalizedUiAppearancePreferencesDto,
): boolean {
  return (
    a.theme === b.theme
    && a.accent === b.accent
    && a.custom_accent_hex.toLowerCase() === b.custom_accent_hex.toLowerCase()
    && a.panel_effect === b.panel_effect
    && a.panel_preset === b.panel_preset
    && a.panel_opacity === b.panel_opacity
    && a.panel_blur === b.panel_blur
    && a.panel_saturation === b.panel_saturation
    && a.workspace_view === b.workspace_view
    && a.workspace_folder_tone === b.workspace_folder_tone
  )
}

function interactionEquals(a: UiInteractionPreferencesDto, b: UiInteractionPreferencesDto): boolean {
  return (
    a.workspace_drop_action === b.workspace_drop_action
    && a.spatial_promote_mode === b.spatial_promote_mode
    && a.reduced_motion === b.reduced_motion
  )
}

function readCachedDocument(): (UiPreferencesDocumentDto & { appearance: NormalizedUiAppearancePreferencesDto }) | null {
  try {
    if (typeof window === 'undefined' || !window.localStorage) return null
    const raw = window.localStorage.getItem(UI_PREFERENCES_CACHE_KEY)
    if (!raw) return null
    return normalizeUiPreferencesDocument(JSON.parse(raw))
  } catch {
    return null
  }
}

function writeCachedDocument(doc: UiPreferencesDocumentDto): void {
  try {
    if (typeof window !== 'undefined' && window.localStorage) {
      window.localStorage.setItem(UI_PREFERENCES_CACHE_KEY, JSON.stringify(doc))
    }
  } catch {
    // Ignore storage quota / private mode failures.
  }
}

function syncSpatialLocalStorage(mode: UiSpatialPromoteMode): void {
  try {
    if (typeof window === 'undefined' || !window.localStorage) return
    const nextBoardMode = spatialPromoteToBoardMode(mode)
    let targetStatus = 'ready'
    const existingRaw = window.localStorage.getItem(SPATIAL_PROMOTE_STORAGE_KEY)
    if (existingRaw) {
      try {
        const parsed = JSON.parse(existingRaw) as { mode?: string; target_status?: string }
        if (typeof parsed?.target_status === 'string' && parsed.target_status) {
          targetStatus = parsed.target_status
        }
        if (parsed?.mode === nextBoardMode) {
          window.dispatchEvent(
            new CustomEvent('tinadec:ui-preferences-spatial-changed', {
              detail: { mode: nextBoardMode, target_status: targetStatus },
            }),
          )
          return
        }
      } catch {
        // Overwrite malformed value below.
      }
    }
    const payload = { mode: nextBoardMode, target_status: targetStatus }
    window.localStorage.setItem(SPATIAL_PROMOTE_STORAGE_KEY, JSON.stringify(payload))
    window.dispatchEvent(
      new CustomEvent('tinadec:ui-preferences-spatial-changed', { detail: payload }),
    )
  } catch {
    // Ignore restricted environments.
  }
}

function applyReducedMotionDataset(effective: boolean): void {
  if (typeof document === 'undefined' || !document.documentElement) return
  document.documentElement.dataset.reducedMotion = effective ? 'true' : 'false'
}

export interface UiPreferencesStore {
  appearance: NormalizedUiAppearancePreferencesDto
  interaction: UiInteractionPreferencesDto
  filePath: Ref<string | null>
  contentHash: Ref<string | null>
  loaded: Ref<boolean>
  loading: Ref<boolean>
  saving: Ref<boolean>
  isReducedMotion: ComputedRef<boolean>
  refresh: () => Promise<UiPreferencesDocumentDto>
  updateAppearance: (patch: Partial<UiAppearancePreferencesDto>) => Promise<UiPreferencesDocumentDto>
  updateInteraction: (patch: Partial<UiInteractionPreferencesDto>) => Promise<UiPreferencesDocumentDto>
  patchPreferences: (patch: PatchUiPreferencesInput) => Promise<UiPreferencesDocumentDto>
  replacePreferences: (input: PutUiPreferencesInput) => Promise<UiPreferencesDocumentDto>
  resetPreferences: () => Promise<UiPreferencesDocumentDto>
}

let storeSingleton: UiPreferencesStore | null = null
let storeScope: EffectScope | null = null
let cleanupWindowListeners: (() => void) | null = null
let eventStream: ScopedEventSource | null = null

export function __resetUiPreferencesForTests(): void {
  eventStream?.close()
  eventStream = null
  cleanupWindowListeners?.()
  cleanupWindowListeners = null
  storeScope?.stop()
  storeScope = null
  storeSingleton = null
}

export interface UseUiPreferencesOptions {
  /** Open the `/api/v1/preferences/events` SSE stream in this window. Child windows disable it and sync over BroadcastChannel. */
  streamEvents?: boolean
}

export function useUiPreferences(options: UseUiPreferencesOptions = {}): UiPreferencesStore {
  if (storeSingleton) return storeSingleton
  const streamEvents = options.streamEvents ?? true

  const legacySnapshot = captureLegacyLocalMigration()
  const cached = readCachedDocument()
  const initialAppearance = cached
    ? cached.appearance
    : normalizeAppearancePreferences(legacySnapshot.patch.appearance)
  const initialInteraction = cached
    ? cached.interaction
    : normalizeInteractionPreferences(legacySnapshot.patch.interaction)

  const scope = effectScope(true)
  const created = scope.run(() => {
    const appearance = reactive<NormalizedUiAppearancePreferencesDto>({ ...initialAppearance })
    const interaction = reactive<UiInteractionPreferencesDto>({ ...initialInteraction })
    const filePath = ref<string | null>(cached?.file_path ?? null)
    const contentHash = ref<string | null>(cached?.content_hash ?? null)
    const loaded = ref(false)
    const loading = ref(false)
    const saving = ref(false)
    const osReducedMotion = ref<boolean>(readOsReducedMotion())

    const isReducedMotion = computed(() =>
      resolveEffectiveReducedMotion(interaction.reduced_motion, osReducedMotion.value),
    )

    const themeCtl = useTheme()
    const panelCtl = usePanelStyles()
    const { canAccessBackend } = useHostAccess()

    let syncingInternal = false

    function currentDocument(): UiPreferencesDocumentDto {
      return {
        version: 1,
        appearance: { ...appearance },
        interaction: { ...interaction },
        file_path: filePath.value,
        content_hash: contentHash.value,
      }
    }

    function applySideEffects(): void {
      syncingInternal = true
      try {
        if (themeCtl.theme.value !== appearance.theme) {
          themeCtl.setTheme(appearance.theme as Theme)
        }
        if (themeCtl.customAccent.value.toLowerCase() !== appearance.custom_accent_hex.toLowerCase()) {
          themeCtl.setCustomAccent(appearance.custom_accent_hex)
        }
        if (themeCtl.accentColor.value !== appearance.accent) {
          themeCtl.setAccentColor(appearance.accent)
        }

        const desiredPanelEffect = panelEffectToLocal(appearance.panel_effect)
        const currentPanel = panelCtl.panelStyle.value as BridgeablePanelStyle
        const hasExtendedFields = 'preset' in currentPanel || 'saturation' in currentPanel
        const needsPanelSync =
          currentPanel.effect !== desiredPanelEffect
          || currentPanel.opacity !== appearance.panel_opacity
          || currentPanel.blur !== appearance.panel_blur
          || (hasExtendedFields
            && (currentPanel.preset !== appearance.panel_preset
              || currentPanel.saturation !== appearance.panel_saturation))

        if (needsPanelSync) {
          const nextPanelPatch: Partial<BridgeablePanelStyle> = {
            preset: appearance.panel_preset,
            effect: desiredPanelEffect,
            opacity: appearance.panel_opacity,
            blur: appearance.panel_blur,
            saturation: appearance.panel_saturation,
          }
          panelCtl.updatePanelStyle(nextPanelPatch as Partial<PanelStyleSettings>)
        }

        syncSpatialLocalStorage(interaction.spatial_promote_mode)
        applyReducedMotionDataset(isReducedMotion.value)
      } finally {
        syncingInternal = false
      }
    }

    const channel = typeof BroadcastChannel !== 'undefined' ? new BroadcastChannel('tinadec:ui-preferences') : null
    if (channel) {
      channel.onmessage = (event: MessageEvent<{ type?: string; doc?: UiPreferencesDocumentDto }>) => {
        if (event.data?.type !== 'ui-preferences-sync' || !event.data.doc) return
        const incomingHash = event.data.doc.content_hash
        if (incomingHash && incomingHash === contentHash.value) return
        loaded.value = true
        applyDocument(normalizeUiPreferencesDocument(event.data.doc, appearance, interaction), true, false)
      }
      onScopeDispose(() => {
        channel.close()
      })
    }

    function applyDocument(doc: UiPreferencesDocumentDto, persistCache = true, broadcast = true): UiPreferencesDocumentDto {
      const normalizedAppearance = normalizeAppearancePreferences(doc.appearance, appearance)
      const normalizedInteraction = normalizeInteractionPreferences(doc.interaction, interaction)
      syncingInternal = true
      try {
        Object.assign(appearance, normalizedAppearance)
        Object.assign(interaction, normalizedInteraction)
        if (doc.file_path !== undefined) filePath.value = doc.file_path ?? null
        if (doc.content_hash !== undefined) contentHash.value = doc.content_hash ?? null
      } finally {
        syncingInternal = false
      }
      applySideEffects()
      const snapshot = currentDocument()
      if (persistCache) writeCachedDocument(snapshot)
      if (broadcast && channel) {
        try {
          channel.postMessage({ type: 'ui-preferences-sync', doc: snapshot })
        } catch {
          // ignore closed channel
        }
      }
      return snapshot
    }

    // Apply initial cached / migrated preferences synchronously on first setup.
    applySideEffects()

    watch(isReducedMotion, (val) => {
      applyReducedMotionDataset(val)
    }, { flush: 'sync' })

    // Bidirectional bridge: direct mutations on useTheme() -> preferences store & backend PATCH
    watch(
      () => [themeCtl.theme.value, themeCtl.accentColor.value, themeCtl.customAccent.value] as const,
      ([nextTheme, nextAccent, nextHex]) => {
        if (syncingInternal) return
        const patch: Partial<UiAppearancePreferencesDto> = {}
        if (isOneOf(UI_THEMES, nextTheme) && nextTheme !== appearance.theme) {
          patch.theme = nextTheme
        }
        if (isOneOf(UI_ACCENTS, nextAccent) && nextAccent !== appearance.accent) {
          patch.accent = nextAccent
        }
        if (
          isValidHexColor(nextHex)
          && nextHex.toLowerCase() !== appearance.custom_accent_hex.toLowerCase()
        ) {
          patch.custom_accent_hex = nextHex.toLowerCase()
        }
        if (Object.keys(patch).length > 0) {
          void patchPreferences({ appearance: patch })
        }
      },
      { flush: 'sync' },
    )

    // Bidirectional bridge: direct mutations on usePanelStyles() -> preferences store & backend PATCH
    watch(
      () => {
        const s = panelCtl.panelStyle.value as BridgeablePanelStyle
        return [s.preset, s.effect, s.opacity, s.blur, s.saturation] as const
      },
      ([nextPreset, nextLocalEffect, nextOpacity, nextBlur, nextSaturation]) => {
        if (syncingInternal) return
        const mappedEffect = panelEffectFromLocal(nextLocalEffect)
        const hasExtended = nextPreset !== undefined || nextSaturation !== undefined

        let desired: Pick<
          NormalizedUiAppearancePreferencesDto,
          'panel_effect' | 'panel_preset' | 'panel_opacity' | 'panel_blur' | 'panel_saturation'
        >

        if (hasExtended) {
          const normalized = normalizeAppearancePreferences(
            {
              ...appearance,
              panel_effect: mappedEffect,
              panel_preset: isOneOf(UI_PANEL_PRESETS, nextPreset) ? nextPreset : undefined,
              panel_opacity: nextOpacity,
              panel_blur: nextBlur,
              panel_saturation: nextSaturation,
            },
            appearance,
          )
          desired = {
            panel_effect: normalized.panel_effect,
            panel_preset: normalized.panel_preset,
            panel_opacity: normalized.panel_opacity,
            panel_blur: normalized.panel_blur,
            panel_saturation: normalized.panel_saturation,
          }
        } else {
          // Compatibility when usePanelStyles only exposes { effect, opacity, blur }:
          const onlyEffectChanged =
            mappedEffect !== appearance.panel_effect
            && nextOpacity === appearance.panel_opacity
            && nextBlur === appearance.panel_blur
          if (onlyEffectChanged || mappedEffect === 'solid') {
            const derived = defaultPanelPresetForEffect(mappedEffect)
            const spec = UI_PANEL_PRESET_DEFAULTS[derived]
            desired = {
              panel_effect: spec.panel_effect,
              panel_preset: derived,
              panel_opacity: spec.panel_opacity,
              panel_blur: spec.panel_blur,
              panel_saturation: spec.panel_saturation,
            }
          } else {
            const opacity = normalizeOptionalInt(nextOpacity, 0, 100) ?? appearance.panel_opacity
            const blur =
              mappedEffect === 'translucent'
                ? 0
                : normalizeOptionalInt(nextBlur, 0, 32) ?? appearance.panel_blur
            const saturation = mappedEffect === 'blur' ? 160 : 100
            const matched = matchCanonicalPanelPreset(mappedEffect, opacity, blur, saturation)
            desired = {
              panel_effect: mappedEffect,
              panel_preset: matched ?? 'custom',
              panel_opacity: opacity,
              panel_blur: blur,
              panel_saturation: saturation,
            }
          }
        }

        const patch: Partial<UiAppearancePreferencesDto> = {}
        if (desired.panel_effect !== appearance.panel_effect) patch.panel_effect = desired.panel_effect
        if (desired.panel_preset !== appearance.panel_preset) patch.panel_preset = desired.panel_preset
        if (desired.panel_opacity !== appearance.panel_opacity) patch.panel_opacity = desired.panel_opacity
        if (desired.panel_blur !== appearance.panel_blur) patch.panel_blur = desired.panel_blur
        if (desired.panel_saturation !== appearance.panel_saturation) {
          patch.panel_saturation = desired.panel_saturation
        }

        if (Object.keys(patch).length > 0) {
          void patchPreferences({ appearance: patch })
        }
      },
      { flush: 'sync' },
    )

    // OS prefers-reduced-motion listener + SpatialSelectionBar/board event listener
    if (typeof window !== 'undefined') {
      const mql =
        typeof window.matchMedia === 'function'
          ? window.matchMedia('(prefers-reduced-motion: reduce)')
          : null
      const onMediaChange = (event: MediaQueryListEvent) => {
        osReducedMotion.value = Boolean(event.matches)
      }
      mql?.addEventListener?.('change', onMediaChange)

      const onSpatialPrefSaved = (event: Event) => {
        if (syncingInternal) return
        const detail = (event as CustomEvent<{ mode?: string }>).detail
        if (!detail?.mode) return
        const mapped = spatialPromoteFromBoardMode(detail.mode)
        if (mapped !== interaction.spatial_promote_mode) {
          void patchPreferences({ interaction: { spatial_promote_mode: mapped } })
        }
      }
      window.addEventListener('tinadec:spatial-promotion-pref-changed', onSpatialPrefSaved)

      cleanupWindowListeners = () => {
        mql?.removeEventListener?.('change', onMediaChange)
        window.removeEventListener('tinadec:spatial-promotion-pref-changed', onSpatialPrefSaved)
      }
    }

    async function refresh(): Promise<UiPreferencesDocumentDto> {
      if (!canAccessBackend.value || typeof api.getUiPreferences !== 'function') {
        return currentDocument()
      }
      loading.value = true
      try {
        let remote = normalizeUiPreferencesDocument(await api.getUiPreferences())
        let alreadyMigrated = false
        try {
          alreadyMigrated = Boolean(
            typeof window !== 'undefined'
              && window.localStorage?.getItem(UI_PREFERENCES_MIGRATED_KEY),
          )
        } catch {
          alreadyMigrated = true
        }

        if (!alreadyMigrated) {
          try {
            window.localStorage?.setItem(UI_PREFERENCES_MIGRATED_KEY, '1')
          } catch {
            // Ignore restricted storage.
          }
          if (
            legacySnapshot.hasAny
            && isDefaultServerPreferences(remote)
            && typeof api.patchUiPreferences === 'function'
          ) {
            try {
              remote = normalizeUiPreferencesDocument(
                await api.patchUiPreferences(legacySnapshot.patch),
                remote.appearance,
                remote.interaction,
              )
            } catch {
              // Keep remote document if migration write fails.
            }
          }
        }

        loaded.value = true
        return applyDocument(remote)
      } catch {
        // Offline / preview fallback keeps local state intact.
        return currentDocument()
      } finally {
        loading.value = false
      }
    }

    async function patchPreferences(patch: PatchUiPreferencesInput): Promise<UiPreferencesDocumentDto> {
      const nextAppearance = patch.appearance
        ? mergeAppearancePreferences(appearance, patch.appearance)
        : { ...appearance }
      const nextInteraction = patch.interaction
        ? normalizeInteractionPreferences({ ...interaction, ...patch.interaction }, interaction)
        : { ...interaction }

      // Coalesce identical back-to-back calls when usePanelStyles + updateAppearance are invoked together.
      if (
        saving.value
        && patch.expected_content_hash == null
        && appearanceEquals(nextAppearance, appearance)
        && interactionEquals(nextInteraction, interaction)
      ) {
        return currentDocument()
      }

      const optimistic = applyDocument({
        version: 1,
        appearance: nextAppearance,
        interaction: nextInteraction,
        file_path: filePath.value,
        content_hash: contentHash.value,
      })

      if (!canAccessBackend.value || typeof api.patchUiPreferences !== 'function') {
        return optimistic
      }

      saving.value = true
      try {
        const saved = await api.patchUiPreferences(patch)
        loaded.value = true
        return applyDocument(normalizeUiPreferencesDocument(saved, nextAppearance, nextInteraction))
      } catch {
        return optimistic
      } finally {
        saving.value = false
      }
    }

    async function replacePreferences(input: PutUiPreferencesInput): Promise<UiPreferencesDocumentDto> {
      const nextAppearance = normalizeAppearancePreferences(input.appearance)
      const nextInteraction = normalizeInteractionPreferences(input.interaction)
      const optimistic = applyDocument({
        version: 1,
        appearance: nextAppearance,
        interaction: nextInteraction,
        file_path: filePath.value,
        content_hash: contentHash.value,
      })

      if (!canAccessBackend.value || typeof api.putUiPreferences !== 'function') {
        return optimistic
      }

      saving.value = true
      try {
        const saved = await api.putUiPreferences({
          ...(input.expected_content_hash != null ? { expected_content_hash: input.expected_content_hash } : {}),
          ...(typeof input.reset_defaults === 'boolean' ? { reset_defaults: input.reset_defaults } : {}),
          appearance: nextAppearance,
          interaction: nextInteraction,
        })
        loaded.value = true
        return applyDocument(normalizeUiPreferencesDocument(saved, nextAppearance, nextInteraction))
      } catch {
        return optimistic
      } finally {
        saving.value = false
      }
    }

    function updateAppearance(patch: Partial<UiAppearancePreferencesDto>): Promise<UiPreferencesDocumentDto> {
      return patchPreferences({ appearance: patch })
    }

    function updateInteraction(patch: Partial<UiInteractionPreferencesDto>): Promise<UiPreferencesDocumentDto> {
      return patchPreferences({ interaction: patch })
    }

    function resetPreferences(): Promise<UiPreferencesDocumentDto> {
      return replacePreferences({
        reset_defaults: true,
        appearance: { ...DEFAULT_UI_APPEARANCE_PREFERENCES },
        interaction: { ...DEFAULT_UI_INTERACTION_PREFERENCES },
      })
    }

    function openSseStream(): void {
      if (eventStream || !canAccessBackend.value || typeof api.uiPreferencesEventsUrl !== 'function') {
        return
      }
      const source = new ScopedEventSource(api.uiPreferencesEventsUrl(), 'user')
      const onSseFrame = (event: Event) => {
        const msg = event as MessageEvent<string>
        if (!msg.data) return
        try {
          const parsed = JSON.parse(msg.data) as Record<string, unknown>
          const candidate =
            parsed.preferences && typeof parsed.preferences === 'object'
              ? (parsed.preferences as Record<string, unknown>)
              : parsed
          if (candidate.appearance || candidate.interaction) {
            const incomingHash =
              typeof candidate.content_hash === 'string'
                ? candidate.content_hash
                : typeof parsed.content_hash === 'string'
                  ? parsed.content_hash
                  : null
            if (incomingHash && incomingHash === contentHash.value) return
            loaded.value = true
            applyDocument(normalizeUiPreferencesDocument(parsed, appearance, interaction))
            return
          }
        } catch {
          // Non-JSON or signal-only event -> trigger GET refresh below.
        }
        void refresh()
      }
      source.addEventListener('preferences.updated', onSseFrame)
      eventStream = source
    }

    function closeSseStream(): void {
      eventStream?.close()
      eventStream = null
    }

    watch(
      canAccessBackend,
      (ready) => {
        if (ready) {
          void refresh()
          if (streamEvents) openSseStream()
        } else {
          closeSseStream()
        }
      },
      { immediate: true },
    )

    return {
      appearance,
      interaction,
      filePath,
      contentHash,
      loaded,
      loading,
      saving,
      isReducedMotion,
      refresh,
      updateAppearance,
      updateInteraction,
      patchPreferences,
      replacePreferences,
      resetPreferences,
    } satisfies UiPreferencesStore
  })

  if (!created) {
    scope.stop()
    throw new Error('Failed to initialize useUiPreferences store')
  }

  storeScope = scope
  storeSingleton = created
  return created
}
