import { describe, expect, it } from 'vitest'
import settingsCss from './settings.css?raw'
import stylesCss from '../styles.css?raw'

function assertCssBlock(css: string, pattern: RegExp): string {
  const m = css.match(pattern)
  expect(m).not.toBeNull()
  if (m === null) throw new Error(`CSS block matching ${pattern} not found`)
  return m[1]
}

describe('settings.css contract', () => {
  const css = settingsCss

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

describe('styles.css extraction contract', () => {
  const css = stylesCss

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

  it('still contains panel material-effect generic rules', () => {
    expect(css).toContain('.float-panel[data-panel-effect="translucent"]')
    expect(css).toContain('.float-panel[data-panel-effect="blur"]')
  })
})
