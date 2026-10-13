import { describe, expect, it } from 'vitest'
import { FEATURE_CATALOG } from './featureCatalog'

describe('FEATURE_CATALOG', () => {

  it('has no duplicate descriptor ids', () => {
    const ids = FEATURE_CATALOG.map((f) => f.descriptorId)
    expect(new Set(ids).size).toBe(ids.length)
  })

})
