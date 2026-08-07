import { describe, expect, it } from 'vitest'
import settingsCss from './settings.css?raw'
import stylesCss from '../styles.css?raw'
import workbenchStackSource from '../workbench/components/WorkbenchStack.vue?raw'
import settingsPageSource from '../pages/SettingsPage.vue?raw'
import agentEvolutionPanelSource from '../components/AgentEvolutionPanel.vue?raw'
import promptEngineeringPanelSource from '../components/PromptEngineeringPanel.vue?raw'
import alertSource from '../components/ui/alert.vue?raw'
import backgroundPreviewSource from '../components/ui/background-preview.vue?raw'
import badgeSource from '../components/ui/badge.vue?raw'
import buttonSource from '../components/ui/button.vue?raw'
import cardSource from '../components/ui/card.vue?raw'
import chartSource from '../components/ui/chart.vue?raw'
import commandSource from '../components/ui/command.vue?raw'
import dropdownMenuSource from '../components/ui/dropdown-menu.vue?raw'
import inputSource from '../components/ui/input.vue?raw'
import menubarSource from '../components/ui/menubar.vue?raw'
import panelStyleControlSource from '../components/ui/panel-style-control.vue?raw'
import popoverSource from '../components/ui/popover.vue?raw'
import selectSource from '../components/ui/select.vue?raw'
import sheetSource from '../components/ui/sheet.vue?raw'
import switchSource from '../components/ui/switch.vue?raw'
import tabsSource from '../components/ui/tabs.vue?raw'
import textareaSource from '../components/ui/textarea.vue?raw'
import toggleGroupSource from '../components/ui/toggle-group.vue?raw'
import toggleSource from '../components/ui/toggle.vue?raw'

function assertCssBlock(css: string, pattern: RegExp): string {
  const m = css.match(pattern)
  expect(m).not.toBeNull()
  if (m === null) throw new Error(`CSS block matching ${pattern} not found`)
  return m[1]
}

function normalizeLineEndings(css: string): string {
  return css.replace(/\r\n/g, '\n')
}

function extractStyleBlocks(source: string): string {
  return Array.from(source.matchAll(/<style\b[^>]*>([\s\S]*?)<\/style>/g))
    .map((match) => match[1])
    .join('\n')
}

const RAW_NEUTRAL_BACKGROUND = /\bbackground(?:-color)?\s*:\s*[^;{}]*var\(--bg-(?:primary|secondary|tertiary|hover|selected|input|button(?:-hover)?|elevated)\b[^;{}]*;/g

function rawNeutralBackgrounds(css: string): string[] {
  return Array.from(css.matchAll(RAW_NEUTRAL_BACKGROUND), (match) => match[0])
}

function expectSurfaceTokens(source: string, tokens: string[]): void {
  for (const token of tokens) {
    expect(source, `Expected source to consume ${token}`).toContain(`var(${token})`)
  }
}

interface CssRule {
  selectors: string[]
  declarations: string
}

function normalizeSelector(selector: string): string {
  return selector.replace(/\s+/g, ' ').trim()
}

function extractCssRules(css: string): CssRule[] {
  const withoutComments = css.replace(/\/\*[\s\S]*?\*\//g, '')

  return Array.from(withoutComments.matchAll(/([^{}]+)\{([^{}]*)\}/g), (match) => ({
    selectors: match[1]
      .split(',')
      .map(normalizeSelector)
      .filter((selector) => selector.length > 0 && !selector.startsWith('@')),
    declarations: match[2],
  })).filter((rule) => rule.selectors.length > 0)
}

function declarationsForSelector(css: string, selector: string): string[] {
  const normalizedSelector = normalizeSelector(selector)
  return extractCssRules(css)
    .filter((rule) => rule.selectors.includes(normalizedSelector))
    .map((rule) => rule.declarations)
}

function expectSelectorDeclarations(css: string, selector: string, expected: RegExp): void {
  const declarations = declarationsForSelector(css, selector)
  expect(declarations, `Expected CSS rule for ${selector}`).not.toHaveLength(0)
  expect(declarations.join('\n'), `Expected ${selector} to match ${expected}`).toMatch(expected)
}

describe('settings.css contract', () => {
  const css = normalizeLineEndings(settingsCss)

  it('contains the settings shell selectors', () => {
    expect(css).toContain('.settings-page')
    expect(css).toContain('.settings-shell')
    expect(css).toContain('.settings-nav')
    expect(css).toContain('.settings-content')
    expect(css).toContain('.settings-window-controls')
    expect(css).toContain('.settings-select')
    expect(css).toContain('.settings-textarea')
    expect(css).toContain('.settings-panel')
    expect(css).toContain('.settings-field')
  })

  it('contains the section-fade transition', () => {
    expect(css).toContain('.section-fade-enter-active')
    expect(css).toContain('.section-fade-leave-active')
    expect(css).toContain('.section-fade-enter-from')
    expect(css).toContain('.section-fade-leave-to')
    expect(css).toContain('.settings-section-wrapper')
  })

  it('contains model center sections', () => {
    expect(css).toContain('.model-center-heading')
    expect(css).toContain('.model-route-panel')
    expect(css).toContain('.model-provider-grid')
    expect(css).toContain('.model-provider-row')
    expect(css).toContain('.model-provider-modal')
    expect(css).toContain('.model-health-overview')
    expect(css).toContain('.model-diagnostics')
    expect(css).toContain('.center-message')
    expect(css).toContain('.center-workbench')
    expect(css).toContain('.center-command-bar')
    expect(css).toContain('.center-overview-receipt')
    expect(css).toContain('.center-receipt-item')
    expect(css).toContain('.model-center-tabs')
    expect(css).toContain('.model-section-header')
    expect(css).toContain('.model-provider-card')
    expect(css).toContain('.provider-detail-panel')
    expect(css).toContain('.provider-detail-head')
    expect(css).toContain('.provider-key-indicator')
  })

  it('contains provider modal styles', () => {
    expect(css).toContain('.model-provider-modal')
    expect(css).toContain('.model-provider-modal-content')
    expect(css).toContain('.modal-header-row')
    expect(css).toContain('.modal-form-section')
    expect(css).toContain('.modal-actions')
    expect(css).toContain('.modal-fade-enter-active')
  })

  it('contains agent center sections', () => {
    expect(css).toContain('.agent-workbench')
    expect(css).toContain('.agent-inspector')
    expect(css).toContain('.agent-column.compact')
    expect(css).toContain('.agent-candidate-row')
    expect(css).toContain('.agent-list-summary')
    expect(css).toContain('.inspector-provider-detail')
  })

  it('contains about page styles', () => {
    expect(css).toContain('.about-section')
    expect(css).toContain('.about-brand')
    expect(css).toContain('.about-status-card')
    expect(css).toContain('.about-layer')
    expect(css).toContain('.about-license')
  })

  it('contains appearance/background styles', () => {
    expect(css).toContain('.background-type-options')
    expect(css).toContain('.bg-type-option')
    expect(css).toContain('.source-input-row')
    expect(css).toContain('.param-slider')
    expect(css).toContain('.panel-styles-grid')
    expect(css).toContain('.performance-warning')
  })

  it('contains tool section styles', () => {
    expect(css).toContain('.tool-discovery-controls')
    expect(css).toContain('.tool-discovery-card')
    expect(css).toContain('.harness-manifest-panel')
    expect(css).toContain('.harness-registry-summary')
    expect(css).toContain('.harness-design-notes')
    expect(css).toContain('.tool-layer-readiness-panel')
    expect(css).toContain('.tool-layer-readiness-row')
  })

  it('contains lang/accent/theme styles', () => {
    expect(css).toContain('.theme-options')
    expect(css).toContain('.theme-option')
    expect(css).toContain('.accent-color-grid')
    expect(css).toContain('.accent-color-swatch')
    expect(css).toContain('.lang-options')
    expect(css).toContain('.lang-option')
    expect(css).toContain('.api-docs-frame')
  })

  it('contains responsive settings clauses', () => {
    expect(css).toContain('@media (max-width: 900px)')
    expect(css).toContain('@media (max-width: 480px)')
    expect(css).toContain('@media (max-width: 1100px)')
    expect(css).toContain('@media (max-width: 700px)')
    expect(css).toContain('@media (max-width: 980px)')
  })

  it('contains settings panel material-effect rules', () => {
    expect(css).toContain('.settings-nav[data-panel-effect="translucent"]')
    expect(css).toContain('.settings-nav[data-panel-effect="blur"]')
    expect(css).toContain('.settings-content[data-panel-effect="translucent"]')
    expect(css).toContain('.settings-content[data-panel-effect="blur"]')
  })

  it('contains supplier and runtime-source styles', () => {
    expect(css).toContain('.supplier-list')
    expect(css).toContain('.supplier-grid')
    expect(css).toContain('.runtime-source-grid')
    expect(css).toContain('.runtime-binding-warning')
    expect(css).toContain('.runtime-binding-readonly')
  })

  // ===== Motion contract =====

  it('settings-module-enter-active: 180ms, opacity/transform only, no layout', () => {
    const block = assertCssBlock(css, /\.settings-module-enter-active\s*\{([^}]+)\}/)
    expect(block).toContain('opacity')
    expect(block).toContain('transform')
    expect(block).toContain('180ms')
    expect(block).not.toMatch(/\b(?:width|height|margin|padding|top|left|grid|all)\b/)
  })

  it('settings-module-leave-active: 180ms, opacity/transform only, no layout', () => {
    const block = assertCssBlock(css, /\.settings-module-leave-active\s*\{([^}]+)\}/)
    expect(block).toContain('opacity')
    expect(block).toContain('transform')
    expect(block).toContain('180ms')
    expect(block).not.toMatch(/\b(?:width|height|margin|padding|top|left|grid|all)\b/)
  })

  it('defines View Transition pseudo-elements with 180ms', () => {
    expect(css).toContain('::view-transition-old(settings-module)')
    expect(css).toContain('::view-transition-new(settings-module)')
    expect(css).toContain('180ms')
  })

  it('View Transition keyframes use only opacity and transform', () => {
    const fo = assertCssBlock(css, /@keyframes\s+fade-out\s*\{([^}]+)\}/)
    expect(fo).toMatch(/opacity/)
    expect(fo).toMatch(/transform/)
    const fi = assertCssBlock(css, /@keyframes\s+fade-in\s*\{([^}]+)\}/)
    expect(fi).toMatch(/opacity/)
    expect(fi).toMatch(/transform/)
  })

  // ===== Reduced motion =====

  it('contains @media (prefers-reduced-motion: reduce)', () => {
    expect(css).toContain('@media (prefers-reduced-motion: reduce)')
  })

  it('neutralizes settings-nav animation under reduced motion', () => {
    expect(css).toContain('.settings-nav {\n    animation: none !important;')
    expect(css).toContain('.settings-content {\n    animation: none !important;')
  })

  it('neutralizes section-fade under reduced motion', () => {
    expect(css).toContain('.section-fade-enter-active,')
    expect(css).toContain('.section-fade-leave-active {')
    expect(css).toContain('transition: none !important;')
  })

  it('neutralizes settings-module under reduced motion', () => {
    expect(css).toContain('.settings-module-enter-active,')
    expect(css).toContain('.settings-module-leave-active {')
    expect(css).toContain('transition-duration: 0s !important;')
  })

  it('neutralizes View Transitions under reduced motion', () => {
    expect(css).toContain('::view-transition-old(settings-module),')
    expect(css).toContain('::view-transition-new(settings-module) {')
    expect(css).toContain('animation: none !important;')
  })

  it('neutralizes entrance keyframes under reduced motion', () => {
    const rs = css.split('@media (prefers-reduced-motion: reduce)')[1]
    expect(rs).toContain('@keyframes settings-nav-enter')
    expect(rs).toContain('@keyframes settings-content-enter')
  })

  it('neutralizes interactive element transitions under reduced motion', () => {
    const rs = css.split('@media (prefers-reduced-motion: reduce)')[1]
    expect(rs).toContain('.bg-type-option')
    expect(rs).toContain('.theme-option')
    expect(rs).toContain('.accent-color-swatch')
  })
})

describe('settings integrated material contract', () => {
  const css = normalizeLineEndings(settingsCss)

  it('does not use directional borders as structural separators', () => {
    const structuralSelectors = [
      '.center-pane-heading',
      '.center-resource-heading',
      '.center-overview-receipt',
      '.center-receipt-item',
      '.model-provider-table-head',
      '.model-provider-row',
      '.model-health-metrics > div',
      '.modal-header-row',
      '.modal-actions',
      '.general-settings-group',
      '.about-row',
      '.about-brand',
      '.about-license',
    ]
    const directionalBorder = /\bborder-(?:top|right|bottom|left|block(?:-(?:start|end))?)\s*:/
    const rules = extractCssRules(css)

    for (const target of structuralSelectors) {
      const matchingRules = rules.filter((rule) => rule.selectors.some((selector) => (
        selector === target
        || selector.startsWith(`${target}:`)
        || selector.startsWith(`${target}.`)
      )))

      expect(matchingRules, `Expected structural selector ${target}`).not.toHaveLength(0)
      for (const rule of matchingRules) {
        expect(
          rule.declarations,
          `${rule.selectors.join(', ')} must use spacing or surface contrast instead of a directional border`,
        ).not.toMatch(directionalBorder)
      }
    }
  })

  it('uses the integrated workbench surface hierarchy and local blur tiers', () => {
    expectSelectorDeclarations(css, '.center-workbench', /background\s*:\s*var\(--surface-section\)\s*;/)
    expectSelectorDeclarations(css, '.center-workbench', /backdrop-filter\s*:\s*var\(--material-filter-section, none\)\s*;/)
    expectSelectorDeclarations(css, '.center-resource-rail', /background\s*:\s*var\(--surface-chrome\)\s*;/)
    expectSelectorDeclarations(css, '.center-resource-stage', /background\s*:\s*transparent\s*;/)
    expectSelectorDeclarations(css, '.center-inspector', /background\s*:\s*var\(--surface-raised\)\s*;/)
    expectSelectorDeclarations(css, '.center-inspector', /backdrop-filter\s*:\s*var\(--material-filter-raised, none\)\s*;/)

    for (const pane of ['.center-resource-rail', '.center-resource-stage', '.center-inspector']) {
      expectSelectorDeclarations(css, pane, /\bborder\s*:\s*0\s*;/)
    }
  })

  it('renders overview and provider tables as continuous material bands', () => {
    expectSelectorDeclarations(css, '.center-overview-receipt', /background\s*:\s*var\(--surface-section\)\s*;/)
    expectSelectorDeclarations(css, '.center-overview-receipt', /backdrop-filter\s*:\s*var\(--material-filter-section, none\)\s*;/)
    expectSelectorDeclarations(css, '.center-receipt-item', /background\s*:\s*transparent\s*;/)

    expectSelectorDeclarations(css, '.model-provider-table', /background\s*:\s*var\(--surface-raised\)\s*;/)
    expectSelectorDeclarations(css, '.model-provider-table-head', /background\s*:\s*var\(--surface-chrome\)\s*;/)
    expectSelectorDeclarations(css, '.model-provider-row', /background\s*:\s*transparent\s*;/)
    expectSelectorDeclarations(css, '.model-provider-row:hover', /background\s*:\s*var\(--surface-hover\)\s*;/)
  })

  it.each([
    '.model-provider-card',
    '.center-resource-card',
    '.center-resource-list-row',
    '.tool-discovery-card',
    '.tool-layer-readiness-row',
    '.harness-manifest-panel',
    '.pet-gallery-card',
  ])('%s uses a raised fill without a decorative outline', (selector) => {
    expectSelectorDeclarations(css, selector, /background\s*:\s*var\(--surface-raised\)\s*;/)
    expectSelectorDeclarations(css, selector, /\bborder\s*:\s*0\s*;/)
  })

  it('keeps shared Agent and UiCard changes scoped to settings', () => {
    const sharedAgentSelector = /(?:\.agent-(?:card|config-section|mode-card)(?=[^\w-]|$)|\.agent-topology-[\w-]+)/
    const agentSelectors = extractCssRules(css)
      .flatMap((rule) => rule.selectors)
      .filter((selector) => sharedAgentSelector.test(selector))

    expect(agentSelectors).not.toHaveLength(0)
    for (const selector of agentSelectors) {
      expect(
        selector,
        `Shared Agent selector must be settings-scoped: ${selector}`,
      ).toMatch(/^\.settings-(?:page|content)\b/)
    }

    expectSelectorDeclarations(css, '.settings-page .settings-content .agent-detail-panel', /\bborder\s*:\s*0\s*;/)
    expectSelectorDeclarations(css, '.settings-page .settings-content .agent-detail-panel', /background\s*:\s*var\(--surface-raised\)\s*;/)
    expectSelectorDeclarations(css, '.settings-page .model-provider-modal-content .card-header', /\bborder\s*:\s*0\s*;/)
    expectSelectorDeclarations(css, '.settings-page .model-provider-modal-content .card-footer', /\bborder\s*:\s*0\s*;/)
  })

  it('retains functional focus, semantic risk accents, and modal elevation', () => {
    expectSelectorDeclarations(css, '.settings-select:focus', /border-color\s*:\s*var\(--border-input-focus\)\s*;/)
    expectSelectorDeclarations(css, '.settings-textarea:focus', /border-color\s*:\s*var\(--border-input-focus\)\s*;/)
    expect(inputSource).toContain('focus-visible:ring-1')

    expectSelectorDeclarations(css, '.tool-discovery-card.risky', /box-shadow\s*:\s*inset\s+3px\s+0[^;]*var\(--accent-danger\)/)
    expectSelectorDeclarations(css, '.model-provider-row.issue', /box-shadow\s*:\s*inset\s+3px\s+0[^;]*var\(--accent-danger\)/)
    expectSelectorDeclarations(css, '.runtime-binding-warning', /box-shadow\s*:\s*inset\s+3px\s+0[^;]*var\(--accent-warning\)/)
    expectSelectorDeclarations(css, '.model-health-alert', /border\s*:\s*1px\s+solid[^;]*var\(--accent-danger\)/)

    expectSelectorDeclarations(css, '.model-provider-modal-content', /\bborder\s*:\s*0\s*;/)
    expectSelectorDeclarations(css, '.model-provider-modal-content', /background\s*:\s*var\(--surface-raised\)\s*;/)
    expectSelectorDeclarations(css, '.model-provider-modal-content', /box-shadow\s*:\s*0\s+20px\s+60px/)
    expectSelectorDeclarations(css, '.model-provider-modal-content', /backdrop-filter\s*:\s*var\(--material-filter-raised, none\)\s*;/)
  })
})

describe('settings material scope contract', () => {
  it('binds the panel effect to the page root and both panel roots', () => {
    const pageTag = settingsPageSource.match(/<div\b[^>]*class="settings-page"[^>]*>/)?.[0]
    const navTag = settingsPageSource.match(/<nav\b[^>]*class="settings-nav"[^>]*>/)?.[0]
    const contentTag = settingsPageSource.match(/<div\b[^>]*class="settings-content"[^>]*>/)?.[0]

    expect(settingsPageSource).toContain('const settingsPageDataAttrs = computed(() => getPanelDataAttributes())')
    expect(settingsPageSource).toContain('const settingsPageMaterialStyle = computed(() => {')
    expect(settingsPageSource).toContain("'--material-filter-section': materialStyle['--material-filter-section'] ?? 'none'")
    expect(settingsPageSource).toContain("'--material-filter-raised': materialStyle['--material-filter-raised'] ?? 'none'")
    expect(pageTag).toContain('v-bind="settingsPageDataAttrs"')
    expect(pageTag).toContain(':style="settingsPageMaterialStyle"')
    expect(navTag).toContain('v-bind="settingsNavDataAttrs"')
    expect(navTag).toContain(':style="settingsNavStyle"')
    expect(contentTag).toContain('v-bind="settingsContentDataAttrs"')
    expect(contentTag).toContain(':style="settingsContentStyle"')
  })

  it.each([
    ['settings.css', settingsCss],
    ['AgentEvolutionPanel.vue', extractStyleBlocks(agentEvolutionPanelSource)],
    ['PromptEngineeringPanel.vue', extractStyleBlocks(promptEngineeringPanelSource)],
    ['panel-style-control.vue', extractStyleBlocks(panelStyleControlSource)],
  ])('%s does not bypass material surfaces with raw neutral backgrounds', (_name, source) => {
    expect(rawNeutralBackgrounds(source)).toEqual([])
  })

  it('keeps the background preview material-aware while preserving its transparency checkerboard', () => {
    expectSurfaceTokens(backgroundPreviewSource, ['--surface-section'])

    const rawBackgrounds = rawNeutralBackgrounds(extractStyleBlocks(backgroundPreviewSource))
    expect(rawBackgrounds).toHaveLength(1)
    expect(rawBackgrounds[0]).toContain('repeating-linear-gradient')
    expect(rawBackgrounds[0]).toContain('var(--bg-tertiary)')
  })

  it('uses a raw opaque color only inside the explicit opaque material preview', () => {
    expectSurfaceTokens(panelStyleControlSource, [
      '--surface-section',
      '--surface-hover',
      '--surface-button',
      '--surface-button-hover',
      '--surface-selected',
      '--surface-raised',
    ])
    expect(panelStyleControlSource).toContain("if (props.settings.effect === 'opaque')")
    expect(panelStyleControlSource.match(/var\(--bg-primary\)/g)).toHaveLength(1)
  })
})

describe('material-aware UI primitive contract', () => {
  const primitives: Array<[string, string, string[]]> = [
    ['input.vue', inputSource, ['--surface-input']],
    ['textarea.vue', textareaSource, ['--surface-input']],
    ['select.vue', selectSource, ['--surface-input', '--surface-raised']],
    ['button.vue', buttonSource, ['--surface-button', '--surface-button-hover', '--surface-hover']],
    ['switch.vue', switchSource, ['--surface-input']],
    ['toggle.vue', toggleSource, ['--surface-hover', '--surface-selected']],
    ['card.vue', cardSource, ['--surface-raised']],
    ['chart.vue', chartSource, ['--surface-raised']],
    ['alert.vue', alertSource, ['--surface-section']],
    ['popover.vue', popoverSource, ['--surface-raised']],
    ['dropdown-menu.vue', dropdownMenuSource, ['--surface-raised']],
    ['sheet.vue', sheetSource, ['--surface-raised']],
    ['command.vue', commandSource, ['--surface-raised', '--surface-input']],
    ['tabs.vue', tabsSource, ['--surface-section']],
    ['toggle-group.vue', toggleGroupSource, ['--surface-section']],
    ['menubar.vue', menubarSource, ['--surface-section']],
    ['badge.vue', badgeSource, ['--surface-button', '--surface-button-hover']],
  ]

  it.each(primitives)('%s consumes its material surface tokens', (_name, source, tokens) => {
    expectSurfaceTokens(source, tokens)
    expect(source).not.toMatch(/\bbg-(?:background|card|popover|input|secondary|muted|accent)(?:\b|\/)/)
  })
})

describe('styles.css extraction contract', () => {
  const css = normalizeLineEndings(stylesCss)

  it('still contains page-level route transitions', () => {
    expect(css).toContain('.page-slide-left-enter-active')
    expect(css).toContain('.page-slide-right-enter-active')
  })

  it('still contains shared layout styles', () => {
    expect(css).toContain('.shell')
    expect(css).toContain('.sidebar')
    expect(css).toContain('.float-panel')
    expect(css).toContain('.conversation')
    expect(css).toContain('.composer')
    expect(css).toContain('.welcome-screen')
    expect(css).toContain('.message-stream')
    expect(css).toContain('.background-layer')
    expect(css).toContain('.agent-card')
    expect(css).toContain('.agent-topology-node')
  })

  it('keeps the immersive conversation zone transparent with material-carrying objects', () => {
    // The conversation zone itself is transparent (page background shows through).
    const panel = assertCssBlock(css, /\.chat-active-panel\s*\{([^}]+)\}/)
    expect(panel).toContain('background: transparent;')

    // Composer + welcome dialog follow the material via the denser input token
    // so they stay readable in translucent/blur modes.
    const composer = assertCssBlock(css, /\.composer-box\s*\{([^}]+)\}/)
    expect(composer).toContain('background: var(--surface-input);')
    const welcome = assertCssBlock(css, /\.welcome-dialog\s*\{([^}]+)\}/)
    expect(welcome).toContain('background: var(--surface-input);')

    // Frosted-glass composer under the blur material.
    expect(css).toMatch(/\[data-panel-effect="blur"\] \.composer-box\s*\{[^}]*backdrop-filter:\s*var\(--material-filter-section[^}]*\}/)
    expect(css).toMatch(/\[data-panel-effect="blur"\] \.welcome-dialog\s*\{[^}]*backdrop-filter:\s*var\(--material-filter-section[^}]*\}/)

    // The welcome dialog stays adaptive to the conversation zone width (not a
    // fixed small card), capped at the readable 820px like the composer.
    const welcomeBlock = assertCssBlock(css, /\.welcome-dialog\s*\{([^}]+)\}/)
    expect(welcomeBlock).toMatch(/width:\s*100%;/)
    expect(welcomeBlock).toMatch(/max-width:\s*820px;/)
  })

  it('renders immersive stacks with a transparent material root', () => {
    const blocks = extractStyleBlocks(workbenchStackSource)
    expect(blocks).toMatch(/\.wb-stack--immersive\s*\{[^}]*background:\s*transparent[^}]*\}/)
    expect(blocks).toMatch(/\.wb-stack--immersive\s*\{[^}]*box-shadow:\s*none[^}]*\}/)
  })

  it('maps opaque surfaces to the solid theme tokens', () => {
    const block = assertCssBlock(css, /:root\s*\{([^}]*--surface-chrome[^}]*)\}/)

    expect(block).toContain('--surface-chrome: var(--bg-tertiary);')
    expect(block).toContain('--surface-section: var(--bg-secondary);')
    expect(block).toContain('--surface-raised: var(--bg-tertiary);')
    expect(block).toContain('--surface-hover: var(--bg-hover);')
    expect(block).toContain('--surface-active: var(--bg-primary);')
    expect(block).toContain('--surface-selected: var(--bg-selected);')
    expect(block).toContain('--surface-input: var(--bg-input);')
    expect(block).toContain('--surface-button: var(--bg-button);')
    expect(block).toContain('--surface-button-hover: var(--bg-button-hover);')
  })

  it('defines the translucent alpha tier independently from blur', () => {
    const block = assertCssBlock(css, /\[data-panel-effect="translucent"\]\s*\{([^}]+)\}/)

    expect(block).toContain('--surface-chrome: rgba(var(--bg-tertiary-rgb), 0.72);')
    expect(block).toContain('--surface-section: rgba(var(--bg-secondary-rgb), 0.70);')
    expect(block).toContain('--surface-raised: rgba(var(--bg-tertiary-rgb), 0.78);')
    expect(block).toContain('--surface-hover: rgba(var(--bg-hover-rgb), 0.82);')
    expect(block).toContain('--surface-active: rgba(var(--bg-primary-rgb), 0.86);')
    expect(block).toContain('--surface-selected: rgba(var(--bg-selected-rgb), 0.84);')
    expect(block).toContain('--surface-input: rgba(var(--bg-input-rgb), 0.88);')
    expect(block).toContain('--surface-button: rgba(var(--bg-button-rgb), 0.80);')
    expect(block).toContain('--surface-button-hover: rgba(var(--bg-button-hover-rgb), 0.90);')
  })

  it('defines the blur alpha tier with a 0.70 input surface', () => {
    const block = assertCssBlock(css, /\[data-panel-effect="blur"\]\s*\{([^}]+)\}/)

    expect(block).toContain('--surface-chrome: rgba(var(--bg-tertiary-rgb), 0.18);')
    expect(block).toContain('--surface-section: rgba(var(--bg-secondary-rgb), 0.38);')
    expect(block).toContain('--surface-raised: rgba(var(--bg-tertiary-rgb), 0.50);')
    expect(block).toContain('--surface-hover: rgba(var(--bg-hover-rgb), 0.62);')
    expect(block).toContain('--surface-active: rgba(var(--bg-primary-rgb), 0.68);')
    expect(block).toContain('--surface-selected: rgba(var(--bg-selected-rgb), 0.72);')
    expect(block).toContain('--surface-input: rgba(var(--bg-input-rgb), 0.70);')
    expect(block).toContain('--surface-button: rgba(var(--bg-button-rgb), 0.58);')
    expect(block).toContain('--surface-button-hover: rgba(var(--bg-button-hover-rgb), 0.72);')
  })

  it('defines RGB companions for material-aware input and button surfaces in both themes', () => {
    expect(css.match(/--bg-input-rgb:/g)).toHaveLength(2)
    expect(css.match(/--bg-button-rgb:/g)).toHaveLength(2)
    expect(css.match(/--bg-button-hover-rgb:/g)).toHaveLength(2)
  })
})
