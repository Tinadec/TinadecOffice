import {
  Bot,
  Dna,
  FileText,
  GitBranch,
  Globe,
  Info,
  KeyRound,
  Palette,
  Terminal,
  Workflow,
} from '@lucide/vue'
import type {
  SettingsLoaderMap,
  SettingsRegistryEntry,
  SettingsRegistryMetadata,
} from './types'

export const SETTINGS_METADATA = Object.freeze([
  Object.freeze({ id: 'model', icon: KeyRound, labelKey: 'settings.model' } as const),
  Object.freeze({ id: 'agents', icon: Workflow, labelKey: 'settings.agents' } as const),
  Object.freeze({ id: 'agentEvolution', icon: Dna, labelKey: 'settings.agentEvolution' } as const),
  Object.freeze({ id: 'promptContext', icon: Bot, labelKey: 'settings.promptContext' } as const),
  Object.freeze({ id: 'promptEngineering', icon: GitBranch, labelKey: 'settings.promptEngineering' } as const),
  Object.freeze({ id: 'tools', icon: Terminal, labelKey: 'settings.toolLayer' } as const),
  Object.freeze({ id: 'appearance', icon: Palette, labelKey: 'settings.appearance' } as const),
  Object.freeze({ id: 'language', icon: Globe, labelKey: 'settings.language' } as const),
  Object.freeze({ id: 'apiDocs', icon: FileText, labelKey: 'settings.apiDocs' } as const),
  Object.freeze({ id: 'about', icon: Info, labelKey: 'settings.about' } as const),
] as const) satisfies readonly SettingsRegistryMetadata[]

export function createSettingsRegistry(
  loaders: SettingsLoaderMap,
): readonly SettingsRegistryEntry[] {
  return SETTINGS_METADATA.map((metadata) => ({
    ...metadata,
    loader: loaders[metadata.id],
  }))
}
