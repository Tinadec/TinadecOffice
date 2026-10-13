import { describe, expect, it } from 'vitest'
import settingsCss from './settings.css?raw'
import inputSource from '../components/ui/input.vue?raw'

function normalizeLineEndings(css: string): string {
  return css.replace(/\r\n/g, '\n')
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

  // ===== Motion contract =====

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

