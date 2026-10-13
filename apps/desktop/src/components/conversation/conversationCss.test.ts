import { describe, expect, it } from 'vitest'
import stylesCss from '../../styles.css?raw'
import conversationCss from './conversation.css?raw'

/**
 * CSS facts of the flat feed that no DOM test can see (APP-HOME-108 P7, found in screenshots).
 */
function keyframes(css: string, name: string): string {
  const start = css.indexOf(`@keyframes ${name}`)
  expect(start, `@keyframes ${name}`).toBeGreaterThanOrEqual(0)
  let depth = 0
  for (let i = css.indexOf('{', start); i < css.length; i += 1) {
    if (css[i] === '{') depth += 1
    else if (css[i] === '}' && --depth === 0) return css.slice(start, i + 1)
  }
  return ''
}

describe('feed CSS contract', () => {
  /**
   * Shimmering text is transparent and painted by a 200%-wide gradient. A background-position
   * outside 0%..100% moves that gradient off the text box, and the label disappears; the old
   * -200% -> 200% sweep did that for most of every cycle.
   */
  it('keeps the shimmer gradient over the text for the whole sweep', () => {
    for (const name of ['shimmer-sweep', 'shimmer-once']) {
      const positions = [...keyframes(stylesCss, name).matchAll(/background-position:\s*(-?\d+)%/g)].map((match) => Number(match[1]))
      expect(positions.length, name).toBeGreaterThanOrEqual(2)
      for (const position of positions) expect(position, `${name} ${position}%`).toBeGreaterThanOrEqual(0)
      for (const position of positions) expect(position, `${name} ${position}%`).toBeLessThanOrEqual(100)
    }
  })

  it('stills shimmer, folds and chevrons under reduced motion', () => {
    const reduced = (css: string) => [...css.matchAll(/@media \(prefers-reduced-motion: reduce\) \{([\s\S]*?)\n\}/g)].map((match) => match[1]).join('\n')
    expect(reduced(stylesCss)).toMatch(/\.shimmer,\s*\.shimmer-once\s*\{[^}]*animation:\s*none/)
    expect(reduced(conversationCss)).toMatch(/\.conversation-fold-content\[data-state\]\s*\{\s*animation:\s*none/)
    expect(reduced(conversationCss)).toMatch(/\.conversation-fold-chevron\s*\{\s*transition:\s*none/)
  })
})
