import { describe, expect, it } from 'vitest'
import { Sparkles, User, Workflow } from '@lucide/vue'
import { graphSeedPackManifest } from '@/agentPacks/GraphSeedPack'
import { KNOWN_MODE_SLUGS, modeIcon, sortModes } from './modePresentation'

describe('modePresentation', () => {
  const manifestSlugs = graphSeedPackManifest.resources.modes.map((mode) => mode.slug)

  it('only knows modes the bundled pack really declares, and knows all of them', () => {
    // Both directions: a key the pack does not declare would never match a real mode, and a
    // pack mode missing here would silently render with the generic icon.
    expect([...KNOWN_MODE_SLUGS].sort()).toEqual([...manifestSlugs].sort())
  })

  it('gives each known mode its own icon and anything else the generic one', () => {
    const icons = KNOWN_MODE_SLUGS.map((slug) => modeIcon(slug))
    expect(new Set(icons).size).toBe(KNOWN_MODE_SLUGS.length)
    expect(icons).not.toContain(Sparkles)
    expect(modeIcon('solo')).toBe(User)
    expect(modeIcon('fixed_pipeline')).toBe(Workflow)
    expect(modeIcon('my-own-mode')).toBe(Sparkles)
    expect(modeIcon(null)).toBe(Sparkles)
    // Map lookup, so inherited object keys are not mistaken for entries.
    expect(modeIcon('constructor')).toBe(Sparkles)
  })

  it('puts the familiar modes first and keeps unknown ones after, in their incoming order', () => {
    const rows = [
      { slug: 'custom-b' },
      { slug: 'fixed_pipeline' },
      { slug: null },
      { slug: 'solo' },
      { slug: 'custom-a' },
      { slug: 'plan' },
    ]
    expect(sortModes(rows).map((row) => row.slug)).toEqual(['solo', 'plan', 'fixed_pipeline', 'custom-b', null, 'custom-a'])
    expect(rows[0]!.slug).toBe('custom-b')
  })
})
