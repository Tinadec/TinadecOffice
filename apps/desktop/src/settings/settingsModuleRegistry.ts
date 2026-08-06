import { defineAsyncComponent, type Component } from 'vue'
import SettingsModuleLoading from './SettingsModuleLoading.vue'
import { SETTINGS_SECTION_IDS, type SettingsSection } from '@/workbench/settingsController'

export interface SettingsModuleDescriptor {
  id: SettingsSection
  component: Component
}

function asyncSettingsModule(loader: () => Promise<{ default: Component }>): Component {
  return defineAsyncComponent({
    loader,
    loadingComponent: SettingsModuleLoading,
    delay: 80,
    timeout: 20_000,
  })
}

const moduleLoaders: Record<SettingsSection, () => Promise<{ default: Component }>> = {
  general: () => import('./modules/SettingsGeneralModule.vue'),
  model: () => import('./modules/SettingsModelModule.vue'),
  agents: () => import('./modules/SettingsAgentsModule.vue'),
  agentEvolution: () => import('./modules/SettingsAgentEvolutionModule.vue'),
  promptContext: () => import('./modules/SettingsPromptContextModule.vue'),
  promptEngineering: () => import('./modules/SettingsPromptEngineeringModule.vue'),
  tools: () => import('./modules/SettingsToolsModule.vue'),
  appearance: () => import('./modules/SettingsAppearanceModule.vue'),
  pets: () => import('./modules/SettingsPetsModule.vue'),
  language: () => import('./modules/SettingsLanguageModule.vue'),
  apiDocs: () => import('./modules/SettingsApiDocsModule.vue'),
  about: () => import('./modules/SettingsAboutModule.vue'),
}

export const settingsModuleDescriptors: readonly SettingsModuleDescriptor[] = SETTINGS_SECTION_IDS.map((id) => ({
  id,
  component: asyncSettingsModule(moduleLoaders[id]),
}))
