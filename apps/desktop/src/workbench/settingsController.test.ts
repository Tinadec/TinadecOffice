import { describe, expect, it } from 'vitest'
import { settingsModuleDescriptors } from '@/settings/settingsModuleRegistry'
import { createSettingsWorkbenchController, SETTINGS_SECTION_IDS } from './settingsController'

describe('Settings workbench modules', () => {
  it('registers every settings section as an asynchronous module in navigation order', () => {
    expect(settingsModuleDescriptors.map((module) => module.id)).toEqual(SETTINGS_SECTION_IDS)
    expect(settingsModuleDescriptors.every((module) => module.component)).toBe(true)
  })

  it('keeps every visited module alive while changing the active section rapidly', () => {
    const controller = createSettingsWorkbenchController()
    for (const section of SETTINGS_SECTION_IDS) controller.selectSection(section)
    for (const section of [...SETTINGS_SECTION_IDS].reverse()) controller.selectSection(section)

    expect(controller.activeSection.value).toBe('general')
    expect([...controller.visitedSections.value]).toEqual(SETTINGS_SECTION_IDS)
    controller.selectSection('invalid' as never)
    expect(controller.activeSection.value).toBe('general')
  })
})
