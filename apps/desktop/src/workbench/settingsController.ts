import { inject, provide, ref, type InjectionKey, type Ref } from 'vue'

export const SETTINGS_SECTION_IDS = [
  'general',
  'model',
  'agents',
  'agentEvolution',
  'promptContext',
  'promptEngineering',
  'tools',
  'appearance',
  'pets',
  'language',
  'apiDocs',
  'about',
] as const

export type SettingsSection = (typeof SETTINGS_SECTION_IDS)[number]

export interface SettingsWorkbenchController {
  activeSection: Ref<SettingsSection>
  visitedSections: Readonly<Ref<ReadonlySet<SettingsSection>>>
  selectSection: (section: SettingsSection) => void
}

const SETTINGS_KEY: InjectionKey<SettingsWorkbenchController> = Symbol('settings-workbench')

export function createSettingsWorkbenchController(): SettingsWorkbenchController {
  const activeSection = ref<SettingsSection>('general')
  const visited = ref<Set<SettingsSection>>(new Set(['general']))
  function selectSection(section: SettingsSection): void {
    if (!SETTINGS_SECTION_IDS.includes(section)) return
    activeSection.value = section
    visited.value = new Set([...visited.value, section])
  }
  return { activeSection, visitedSections: visited, selectSection }
}

export function provideSettingsWorkbench(controller: SettingsWorkbenchController): void {
  provide(SETTINGS_KEY, controller)
}

export function useOptionalSettingsWorkbench(): SettingsWorkbenchController | null {
  return inject(SETTINGS_KEY, null)
}

export function useSettingsWorkbench(): SettingsWorkbenchController {
  const controller = useOptionalSettingsWorkbench()
  if (!controller) throw new Error('Settings workbench was not provided.')
  return controller
}

