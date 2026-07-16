import { describe, expect, it, vi } from 'vitest'
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
import { SETTINGS_IDS, type SettingsLoaderMap } from './types'
import { createSettingsRegistry, SETTINGS_METADATA } from './settingsRegistry'

describe('settings registry', () => {
  it('contains exactly the ten existing settings ids in navigation order', () => {
    // Given the metadata-only registry definition
    // When its ids are inspected
    const ids = SETTINGS_METADATA.map((entry) => entry.id)

    // Then the existing order is preserved without duplicates
    expect(ids).toEqual(SETTINGS_IDS)
    expect(new Set(ids).size).toBe(10)
  })

  it('preserves the existing icon and i18n label metadata', () => {
    // Given the existing SettingsPage navigation contract
    // When metadata is projected to machine-consumed fields
    const metadata = SETTINGS_METADATA.map(({ id, icon, labelKey }) => ({ id, icon, labelKey }))

    // Then all ten entries retain their current icon and translation key
    expect(metadata).toEqual([
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
    ])
  })

  it('exposes immutable metadata rather than a mutable registry singleton', () => {
    expect(Object.isFrozen(SETTINGS_METADATA)).toBe(true)
    expect(SETTINGS_METADATA.every(Object.isFrozen)).toBe(true)
  })

  it('does not invoke injected loaders while creating the registry', () => {
    // Given a complete loader map whose calls are observable
    const loader = vi.fn(async () => ({ name: 'FakeSettingsPage' }))
    const loaders: SettingsLoaderMap = {
      model: loader,
      agents: loader,
      agentEvolution: loader,
      promptContext: loader,
      promptEngineering: loader,
      tools: loader,
      appearance: loader,
      language: loader,
      apiDocs: loader,
      about: loader,
    }

    // When the future shell creates its registry
    const registry = createSettingsRegistry(loaders)

    // Then loaders remain lazy and are attached by id
    expect(loader).not.toHaveBeenCalled()
    expect(registry.map((entry) => entry.loader)).toEqual(SETTINGS_IDS.map(() => loader))
  })
})
