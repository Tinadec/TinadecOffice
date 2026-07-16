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

export const SETTINGS_METADATA = [
  { id: 'model', icon: KeyRound, labelKey: 'settings.model' },
  { id: 'agents', icon: Workflow, labelKey: 'settings.agents' },
  { id: 'agentEvolution', icon: Dna, labelKey: 'settings.agentEvolution' },
  { id: 'promptContext', icon: Bot, labelKey: 'settings.promptContext' },
  { id: 'promptEngineering', icon: GitBranch, labelKey: 'settings.promptEngineering' },
  { id: 'tools', icon: Terminal, labelKey: 'settings.toolLayer' },
  { id: 'appearance', icon: Palette, labelKey: 'settings.appearance' },
  { id: 'language', icon: Globe, labelKey: 'settings.language' },
  { id: 'apiDocs', icon: FileText, labelKey: 'settings.apiDocs' },
  { id: 'about', icon: Info, labelKey: 'settings.about' },
] as const satisfies readonly SettingsRegistryMetadata[]

export function createSettingsRegistry(
  loaders: SettingsLoaderMap,
): readonly SettingsRegistryEntry[] {
  return SETTINGS_METADATA.map((metadata) => ({
    ...metadata,
    loader: loaders[metadata.id],
  }))
}
