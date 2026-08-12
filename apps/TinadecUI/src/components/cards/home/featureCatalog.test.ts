import { describe, expect, it } from 'vitest'
import { FEATURE_CATALOG } from './featureCatalog'

describe('FEATURE_CATALOG', () => {
  it('lists all eight openable feature pages', () => {
    expect(FEATURE_CATALOG).toHaveLength(8)
  })

  it('keys every entry by a known UIE card descriptor id', () => {
    const ids = FEATURE_CATALOG.map((f) => f.descriptorId)
    expect(ids).toEqual([
      'agent',
      'terminal',
      'git',
      'approval',
      'orchestration',
      'browser',
      'events',
      'doctor',
    ])
  })

  it('has no duplicate descriptor ids', () => {
    const ids = FEATURE_CATALOG.map((f) => f.descriptorId)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('provides title/desc keys, an icon and an accent color for every entry', () => {
    for (const f of FEATURE_CATALOG) {
      expect(f.titleKey).toBeTruthy()
      expect(f.descKey).toBeTruthy()
      expect(f.icon).toBeTruthy()
      expect(f.color).toMatch(/^#[0-9a-f]{6}$/i)
    }
  })
})
