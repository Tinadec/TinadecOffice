import type { Component, ComputedRef, Ref } from 'vue'

export const SETTINGS_IDS = [
  'model',
  'agents',
  'agentEvolution',
  'promptContext',
  'promptEngineering',
  'tools',
  'appearance',
  'language',
  'apiDocs',
  'about',
] as const

export type SettingsId = (typeof SETTINGS_IDS)[number]

export type SettingsModule = Component | Readonly<{ default: Component }>
export type SettingsLoader = () => Promise<SettingsModule>
export type SettingsLoaderMap = Readonly<Record<SettingsId, SettingsLoader>>

export type SettingsRegistryMetadata = Readonly<{
  id: SettingsId
  icon: Component
  labelKey: `settings.${string}`
}>

export type SettingsRegistryEntry = SettingsRegistryMetadata & Readonly<{
  loader: SettingsLoader
}>

export type SettingsMotionMode = 'native' | 'vue-fallback' | 'reduced'

export type SettingsMediaQueryResult = Readonly<{
  matches: boolean
}>

export type SettingsMatchMedia = (query: string) => SettingsMediaQueryResult

export type SettingsViewTransition = Readonly<{
  finished: Promise<void>
  skipTransition: () => void
}>

export type SettingsViewTransitionDocument = Readonly<{
  startViewTransition: (update: () => void) => SettingsViewTransition
}>

export type SettingsNavigationOptions = Readonly<{
  initialId: SettingsId
  matchMedia: SettingsMatchMedia
  viewTransitionDocument?: SettingsViewTransitionDocument
}>

export type SettingsNavigation = Readonly<{
  activeId: Readonly<Ref<SettingsId>>
  visitedIds: ComputedRef<readonly SettingsId[]>
  failedIds: ComputedRef<readonly SettingsId[]>
  revisions: ComputedRef<Readonly<Record<SettingsId, number>>>
  motionMode: Readonly<Ref<SettingsMotionMode>>
  switchSection: (id: SettingsId) => void
  markVisited: (id: SettingsId) => void
  markFailed: (id: SettingsId) => void
  retry: (id: SettingsId) => void
  cacheKey: (id: SettingsId) => string
}>
