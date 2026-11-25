import { computed, effectScope, reactive, ref, watch, type ComputedRef, type EffectScope, type Ref } from 'vue'
import {
  api,
  type PatchUiPreferencesInput,
  type PutUiPreferencesInput,
  type UiAccentPreference,
  type UiAppearancePreferencesDto,
  type UiInteractionPreferencesDto,
  type UiPanelEffectPreference,
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
import type { PanelEffect } from '@/types/background'

export type {
  PatchUiPreferencesInput,
  PutUiPreferencesInput,
  UiAccentPreference,
  UiAppearancePreferencesDto,
  UiInteractionPreferencesDto,
  UiPanelEffectPreference,
  UiPreferencesDocumentDto,
  UiReducedMotionPreference,
  UiSpatialPromoteMode,
  UiThemePreference,
  UiWorkspaceDropAction,
  UiWorkspaceFolderTone,
  UiWorkspaceViewPreference,
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

export const DEFAULT_UI_APPEARANCE_PREFERENCES: UiAppearancePreferencesDto = {
  theme: 'dark',
  accent: 'monochrome',
  custom_accent_hex: '#18181b',
  panel_effect: 'translucent',
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

export function normalizeAppearancePreferences(
  raw: Partial<UiAppearancePreferencesDto> | null | undefined,
  fallback: UiAppearancePreferencesDto = DEFAULT_UI_APPEARANCE_PREFERENCES,
): UiAppearancePreferencesDto {
  const candidate = raw && typeof raw === 'object' ? raw : {}
  const rawPanel = candidate.panel_effect as string | undefined
  const panelEffect: UiPanelEffectPreference =
    rawPanel === 'opaque'
      ? 'solid'
      : isOneOf(UI_PANEL_EFFECTS, rawPanel)
        ? rawPanel
        : fallback.panel_effect

  return {
    theme: isOneOf(UI_THEMES, candidate.theme) ? candidate.theme : fallback.theme,
    accent: isOneOf(UI_ACCENTS, candidate.accent) ? candidate.accent : fallback.accent,
    custom_accent_hex:
      typeof candidate.custom_accent_hex === 'string' && isValidHexColor(candidate.custom_accent_hex)
        ? candidate.custom_accent_hex.toLowerCase()
        : fallback.custom_accent_hex,
    panel_effect: panelEffect,
    workspace_view: isOneOf(UI_WORKSPACE_VIEWS, candidate.workspace_view)
      ? candidate.workspace_view
      : fallback.workspace_view,
    workspace_folder_tone: isOneOf(UI_WORKSPACE_FOLDER_TONES, candidate.workspace_folder_tone)
      ? candidate.workspace_folder_tone
      : fallback.workspace_folder_tone,
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
): UiPreferencesDocumentDto {
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
      try {
        const parsed = JSON.parse(rawPanel) as { effect?: string }
        if (parsed && typeof parsed.effect === 'string') {
          appearance.panel_effect = panelEffectFromLocal(parsed.effect)
          hasAny = true
        }
      } catch {
        // Ignore malformed legacy panel JSON.
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
  const a = doc.appearance
  const i = doc.interaction
  const panelIsDefault = a.panel_effect === 'translucent' || a.panel_effect === 'solid'
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

function readCachedDocument(): UiPreferencesDocumentDto | null {
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
  appearance: UiAppearancePreferencesDto
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

export function useUiPreferences(): UiPreferencesStore {
  if (storeSingleton) return storeSingleton

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
    const appearance = reactive<UiAppearancePreferencesDto>({ ...initialAppearance })
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
        if (panelCtl.panelStyle.value.effect !== desiredPanelEffect) {
          panelCtl.setPanelEffect(desiredPanelEffect)
        }
        syncSpatialLocalStorage(interaction.spatial_promote_mode)
        applyReducedMotionDataset(isReducedMotion.value)
      } finally {
        syncingInternal = false
      }
    }

    function applyDocument(doc: UiPreferencesDocumentDto, persistCache = true): UiPreferencesDocumentDto {
      syncingInternal = true
      try {
        Object.assign(appearance, doc.appearance)
        Object.assign(interaction, doc.interaction)
        if (doc.file_path !== undefined) filePath.value = doc.file_path ?? null
        if (doc.content_hash !== undefined) contentHash.value = doc.content_hash ?? null
      } finally {
        syncingInternal = false
      }
      applySideEffects()
      const snapshot = currentDocument()
      if (persistCache) writeCachedDocument(snapshot)
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
      () => panelCtl.panelStyle.value.effect,
      (nextLocalEffect) => {
        if (syncingInternal) return
        const mapped = panelEffectFromLocal(nextLocalEffect)
        if (mapped !== appearance.panel_effect) {
          void patchPreferences({ appearance: { panel_effect: mapped } })
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
        ? normalizeAppearancePreferences({ ...appearance, ...patch.appearance }, appearance)
        : { ...appearance }
      const nextInteraction = patch.interaction
        ? normalizeInteractionPreferences({ ...interaction, ...patch.interaction }, interaction)
        : { ...interaction }

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
          openSseStream()
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
