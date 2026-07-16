export type SettingsRouteName =
  | 'settings-model'
  | 'settings-agents'
  | 'settings-agent-evolution'
  | 'settings-prompt-context'
  | 'settings-prompt-engineering'
  | 'settings-tools'
  | 'settings-appearance'
  | 'settings-language'
  | 'settings-api-docs'
  | 'settings-about'

export type SettingsNavigationItem = {
  readonly name: SettingsRouteName
  readonly labelKey: string
  readonly descriptionKey: string
  readonly icon: 'key' | 'workflow' | 'dna' | 'bot' | 'branch' | 'terminal' | 'palette' | 'globe' | 'file' | 'info'
}

export type SettingsNavigationGroup = {
  readonly labelKey: string
  readonly items: readonly SettingsNavigationItem[]
}

export const settingsNavigation = [
  {
    labelKey: 'settings.groupIntelligence',
    items: [
      { name: 'settings-model', labelKey: 'settings.model', descriptionKey: 'settings.modelCenterSubtitle', icon: 'key' },
      { name: 'settings-agents', labelKey: 'settings.agents', descriptionKey: 'settings.agentCenterSubtitle', icon: 'workflow' },
      { name: 'settings-agent-evolution', labelKey: 'settings.agentEvolution', descriptionKey: 'settings.agentEvolutionDescription', icon: 'dna' },
      { name: 'settings-prompt-context', labelKey: 'settings.promptContext', descriptionKey: 'settings.promptContextDescription', icon: 'bot' },
      { name: 'settings-prompt-engineering', labelKey: 'settings.promptEngineering', descriptionKey: 'settings.promptEngineeringDescription', icon: 'branch' },
    ],
  },
  {
    labelKey: 'settings.groupWorkspace',
    items: [
      { name: 'settings-tools', labelKey: 'settings.toolLayer', descriptionKey: 'settings.toolLayerSubtitle', icon: 'terminal' },
      { name: 'settings-appearance', labelKey: 'settings.appearance', descriptionKey: 'settings.appearanceDescription', icon: 'palette' },
      { name: 'settings-language', labelKey: 'settings.language', descriptionKey: 'settings.languageDescription', icon: 'globe' },
    ],
  },
  {
    labelKey: 'settings.groupSystem',
    items: [
      { name: 'settings-api-docs', labelKey: 'settings.apiDocs', descriptionKey: 'settings.apiDocsDescription', icon: 'file' },
      { name: 'settings-about', labelKey: 'settings.about', descriptionKey: 'settings.aboutDescription', icon: 'info' },
    ],
  },
] as const satisfies readonly SettingsNavigationGroup[]

export const settingsNavigationItems: readonly SettingsNavigationItem[] = settingsNavigation.flatMap<SettingsNavigationItem>(
  (group) => group.items,
)
