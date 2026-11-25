/**
 * Background customization types for TinadecOffice Desktop
 * Supports image, video, and HTML backgrounds with opacity/blur controls
 */

export type BackgroundType = 'none' | 'image' | 'video' | 'html'

export interface BackgroundSettings {
  type: BackgroundType
  source: string           // URL or local file path
  opacity: number          // 0-100
  blur: number             // 0-20px
  size: 'cover' | 'contain' | 'auto'
  position: 'center' | 'top' | 'bottom' | 'left' | 'right'
  repeat: 'no-repeat' | 'repeat' | 'repeat-x' | 'repeat-y'
}

/**
 * Panel material effect type — three optical modes:
 * - opaque:      Solid background, no transparency, no blur
 * - translucent: Semi-transparent background, adjustable opacity (0-100)
 * - blur:        Frosted glass effect, adjustable blur (0-32px), opacity (0-100), and saturation (100-200%)
 */
export type PanelEffect = 'opaque' | 'translucent' | 'blur'

export type PanelMaterialPresetId =
  | 'solid'
  | 'frosted'
  | 'crystal'
  | 'mica'
  | 'translucent'
  | 'custom'

export type NonCustomPanelMaterialPresetId = Exclude<PanelMaterialPresetId, 'custom'>

export interface PanelStyleSettings {
  preset: PanelMaterialPresetId
  effect: PanelEffect
  /** Background opacity 0-100 (used by translucent and blur) */
  opacity: number
  /** Backdrop blur strength in px 0-32 (used by blur) */
  blur: number
  /** Backdrop color saturation in % 100-200 (auto-tuned per preset; slider only in custom + blur) */
  saturation: number
}

export interface PanelMaterialPresetSpec {
  id: NonCustomPanelMaterialPresetId
  effect: PanelEffect
  opacity: number
  blur: number
  saturation: number
  recommended?: boolean
}

export const PANEL_MATERIAL_PRESET_ORDER: readonly NonCustomPanelMaterialPresetId[] = [
  'solid',
  'frosted',
  'crystal',
  'mica',
  'translucent',
] as const

export const PANEL_MATERIAL_PRESETS: Record<NonCustomPanelMaterialPresetId, PanelMaterialPresetSpec> = {
  solid: {
    id: 'solid',
    effect: 'opaque',
    opacity: 100,
    blur: 0,
    saturation: 100,
  },
  frosted: {
    id: 'frosted',
    effect: 'blur',
    opacity: 68,
    blur: 18,
    saturation: 160,
    recommended: true,
  },
  crystal: {
    id: 'crystal',
    effect: 'blur',
    opacity: 48,
    blur: 24,
    saturation: 180,
  },
  mica: {
    id: 'mica',
    effect: 'blur',
    opacity: 82,
    blur: 14,
    saturation: 130,
  },
  translucent: {
    id: 'translucent',
    effect: 'translucent',
    opacity: 76,
    blur: 0,
    saturation: 100,
  },
}

export interface AppearanceSettings {
  theme: 'dark' | 'light' | 'system'
  accentColor: string
  background: BackgroundSettings
  /** Global material effect applied to all panels */
  panelStyle: PanelStyleSettings
}

export const DEFAULT_BACKGROUND_SETTINGS: BackgroundSettings = {
  type: 'none',
  source: '',
  opacity: 100,
  blur: 0,
  size: 'cover',
  position: 'center',
  repeat: 'no-repeat',
}

export const DEFAULT_PANEL_STYLE_SETTINGS: PanelStyleSettings = {
  preset: 'frosted',
  effect: 'blur',
  opacity: 68,
  blur: 18,
  saturation: 160,
}
