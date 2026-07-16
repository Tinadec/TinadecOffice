import { existsSync, readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const readSource = (path: string) => {
  const url = new URL(path, import.meta.url)
  return existsSync(url) ? readFileSync(url, 'utf8') : ''
}

const settingsPages = [
  ['model', 'ModelSettingsPage'],
  ['agents', 'AgentsSettingsPage'],
  ['agent-evolution', 'AgentEvolutionSettingsPage'],
  ['prompt-context', 'PromptContextSettingsPage'],
  ['prompt-engineering', 'PromptEngineeringSettingsPage'],
  ['tools', 'ToolsSettingsPage'],
  ['appearance', 'AppearanceSettingsPage'],
  ['language', 'LanguageSettingsPage'],
  ['api-docs', 'ApiDocsSettingsPage'],
  ['about', 'AboutSettingsPage'],
] as const

const settingsModules = [
  './pages/settings/aboutSettings.ts',
  './pages/settings/agentSettings.ts',
  './pages/settings/agentState.ts',
  './pages/settings/appearanceSettings.ts',
  './pages/settings/modelSettings.ts',
  './pages/settings/promptContextSettings.ts',
  './pages/settings/settingsLabels.ts',
  './pages/settings/toolSettings.ts',
] as const

function pureLoc(source: string) {
  return source.split('\n').filter((line) => {
    const trimmed = line.trim()
    return trimmed && !trimmed.startsWith('//') && !trimmed.startsWith('/*') && !trimmed.startsWith('*')
  }).length
}

describe('settings multi-page architecture', () => {
  it('routes every settings domain to a dedicated lazy page', () => {
    const router = readSource('./router.ts')
    const routes = readSource('./pages/settings/settingsRoutes.ts')
    const legacyPage = readSource('./pages/SettingsPage.vue')

    expect(router).toContain("path: '/settings'")
    expect(router).toContain("redirect: { name: 'settings-model' }")
    expect(router).toContain("./pages/settings/SettingsLayout.vue")
    expect(router).not.toContain("./pages/SettingsPage.vue")
    expect(legacyPage).not.toContain('type SettingsSection')

    for (const [path, page] of settingsPages) {
      expect(routes).toContain(`path: '${path}'`)
      expect(routes).toContain(`./${page}.vue`)
    }
  })

  it('keeps settings navigation discoverable at narrow widths', () => {
    const layout = readSource('./pages/settings/SettingsLayout.vue')
    const styles = readSource('./styles.css')

    expect(layout).toContain('class="settings-mobile-nav"')
    expect(layout).toContain('t(item.labelKey)')
    expect(layout).toContain('<RouterView')
    expect(styles).toContain('min-width: 320px')
    expect(styles).not.toContain('min-width: 1120px')
    expect(styles).toContain('@media (max-width: 900px)')
    expect(styles).toContain('.settings-mobile-nav')
  })

  it('keeps settings navigation and locale persistence bilingual', () => {
    const navigation = readSource('./pages/settings/settingsNavigation.ts')
    const languagePage = readSource('./pages/settings/LanguageSettingsPage.vue')
    const english = readSource('./locales/en.ts')
    const chinese = readSource('./locales/zh-CN.ts')

    for (const key of ['groupIntelligence', 'groupWorkspace', 'groupSystem', 'model', 'about']) {
      expect(navigation).toContain(`settings.${key}`)
      expect(english).toMatch(new RegExp(`${key}: ['\"]`))
      expect(chinese).toMatch(new RegExp(`${key}: ['\"]`))
    }

    expect(languagePage).toContain("localStorage.setItem('tinadec-locale', lang)")
    expect(languagePage).toContain("setLocale('zh-CN')")
    expect(languagePage).toContain("setLocale('en')")
  })

  it('preserves the existing settings entry contract', () => {
    const composer = readSource('./components/ComposerBar.vue')
    const welcome = readSource('./components/WelcomeScreen.vue')
    const home = readSource('./pages/HomePage.vue')

    expect(composer).toContain("router.push('/settings')")
    expect(welcome).toContain("router.push('/settings')")
    expect(home).toContain("router.push('/settings')")
  })

  it('keeps settings controller logic split into focused modules', () => {
    expect(readSource('./pages/settings/useSettingsCenter.ts')).toBe('')

    for (const modulePath of settingsModules) {
      expect(pureLoc(readSource(modulePath)), modulePath).toBeLessThanOrEqual(250)
    }
  })

  it('localizes advanced panels and recovers from narrow or missing docs surfaces', () => {
    const evolution = readSource('./components/AgentEvolutionPanel.vue')
    const promptEngineering = readSource('./components/PromptEngineeringPanel.vue')
    const apiDocs = readSource('./pages/settings/ApiDocsSettingsPage.vue')

    expect(evolution).toContain("t('settings.evolutionTitle')")
    expect(evolution).not.toContain('>Agent Evolution<')
    expect(promptEngineering).toContain("t('settings.promptEngineeringTitle')")
    expect(promptEngineering).toContain('@media (max-width: 640px)')
    expect(promptEngineering).toContain('grid-template-columns: 1fr;')
    expect(apiDocs).toContain('@load="handleLoad"')
    expect(apiDocs).toContain("t('settings.apiDocsUnavailable')")
    expect(apiDocs).toContain('openExternal')
  })

  it('keeps populated advanced controls keyboard and motion accessible', () => {
    const evolution = readSource('./components/AgentEvolutionPanel.vue')
    const promptEngineering = readSource('./components/PromptEngineeringPanel.vue')
    const tools = readSource('./pages/settings/ToolsSettingsPage.vue')
    const dialogFocus = readSource('./composables/useDialogFocus.ts')
    const modelSettings = readSource('./pages/settings/modelSettings.ts')
    const agentSettings = readSource('./pages/settings/agentSettings.ts')

    expect(evolution).toContain('role="dialog"')
    expect(evolution).toContain('aria-modal="true"')
    expect(promptEngineering).toContain('role="dialog"')
    expect(promptEngineering).toContain('<button\n            type="button"\n            v-for="eff in sortedEffectivenessList"')
    expect(promptEngineering).not.toContain("'Updated content'")
    expect(tools).toContain('<article\n                v-for="risk in manifestRiskPolicies"')
    expect(tools).not.toContain(' tools · ')
    expect(dialogFocus).toContain("event.key === 'Escape'")
    expect(dialogFocus).toContain("event.key !== 'Tab'")
    expect(dialogFocus).toContain('returnFocus?.focus()')
    expect(modelSettings).toContain("matchMedia('(prefers-reduced-motion: reduce)')")
    expect(agentSettings).toContain("matchMedia('(prefers-reduced-motion: reduce)')")
  })

  it('keeps degraded settings states localized, visible, and accessible', () => {
    const settingsLabels = readSource('./pages/settings/settingsLabels.ts')
    const toolSettings = readSource('./pages/settings/toolSettings.ts')
    const toolsPage = readSource('./pages/settings/ToolsSettingsPage.vue')
    const promptContext = readSource('./pages/settings/promptContextSettings.ts')
    const promptContextPage = readSource('./pages/settings/PromptContextSettingsPage.vue')
    const styles = readSource('./styles.css')
    const titleFiles = [
      './pages/settings/ModelSettingsPage.vue',
      './pages/settings/AgentsSettingsPage.vue',
      './components/AgentEvolutionPanel.vue',
      './pages/settings/PromptContextSettingsPage.vue',
      './components/PromptEngineeringPanel.vue',
      './pages/settings/ToolsSettingsPage.vue',
      './pages/settings/AppearanceSettingsPage.vue',
      './pages/settings/LanguageSettingsPage.vue',
      './pages/settings/ApiDocsSettingsPage.vue',
      './pages/settings/AboutSettingsPage.vue',
    ]

    expect(settingsLabels).toContain('settingsErrorLabel')
    expect(settingsLabels).toContain('settings.backendUnavailable')
    expect(toolSettings).toContain('toolCatalogError')
    expect(toolsPage).toContain('settingsErrorLabel(toolCatalogError)')
    expect(toolsPage).toContain('<template v-if="!toolCatalogError">')
    expect(promptContext).toContain('promptError')
    expect(promptContextPage).toContain('settingsErrorLabel(promptError)')
    expect(promptContextPage).toContain('promptCategoryLabel(category)')
    expect(styles).toContain('@media (prefers-reduced-motion: reduce)')

    for (const file of titleFiles) {
      expect(readSource(file), file).toContain('<h1')
    }

    const evolution = readSource('./components/AgentEvolutionPanel.vue')
    const promptEngineering = readSource('./components/PromptEngineeringPanel.vue')
    expect(evolution).toContain('class="center-message error"')
    expect(evolution).not.toContain('class="evolution-error"')
    expect(promptEngineering).toContain('class="center-message error"')
    expect(promptEngineering).toContain('v-if="!error || fragments.length > 0"')
    expect(promptEngineering).not.toContain('class="pe-error"')
    expect(styles).toMatch(/\.settings-content h1 \{[\s\S]*?font-size: 24px;[\s\S]*?font-weight: 600;/)
    expect(styles).toMatch(/\.settings-content h2 \{[\s\S]*?font-size: 15px;[\s\S]*?font-weight: 600;/)
  })
})
