import { describe, expect, it } from 'vitest'
import { FEATURE_CATALOG, catalogEntriesForPage } from './featureCatalog'

const COMPANION_IDS = ['companionList', 'companionFeed', 'companionGroups', 'companionConnections']

describe('FEATURE_CATALOG', () => {

  it('has no duplicate descriptor ids', () => {
    const ids = FEATURE_CATALOG.map((f) => f.descriptorId)
    expect(new Set(ids).size).toBe(ids.length)
  })

  // The workspace card is the only entry whose tile changes what the agent is authorized on,
  // so its presence and translation keys are pinned here rather than left to a screenshot.
  it('catalogs the workspace card with its own title and description keys', () => {
    const entry = FEATURE_CATALOG.find((f) => f.descriptorId === 'workspace')
    expect(entry).toBeTruthy()
    expect(entry!.titleKey).toBe('context.homeWorkspace')
    expect(entry!.descKey).toBe('context.homeWorkspaceDesc')
    expect(FEATURE_CATALOG.filter((f) => f.titleKey === entry!.titleKey)).toHaveLength(1)
  })

  it('scopes the four companion cards to the companion center only', () => {
    for (const descriptorId of COMPANION_IDS) {
      const entry = FEATURE_CATALOG.find((f) => f.descriptorId === descriptorId)
      expect(entry, `${descriptorId} must be in the catalog`).toBeTruthy()
      expect(entry!.pages).toEqual(['companionCenter'])
      expect(entry!.titleKey).toBe(`context.${descriptorId}`)
      expect(entry!.descKey).toBe(`context.${descriptorId}Desc`)
    }
    // Every other entry stays a Home tile: an entry without `pages` belongs to Home.
    for (const entry of FEATURE_CATALOG) {
      if (COMPANION_IDS.includes(entry.descriptorId)) continue
      expect(entry.pages ?? ['home']).toContain('home')
    }
  })

  it('filters catalog entries by the page that may host them', () => {
    const homeIds = catalogEntriesForPage('home').map((entry) => entry.descriptorId)
    expect(homeIds).not.toContain('companionList')
    expect(homeIds).not.toContain('companionFeed')
    expect(homeIds).not.toContain('companionGroups')
    expect(homeIds).not.toContain('companionConnections')
    expect(homeIds).toContain('workspace')

    const centerIds = catalogEntriesForPage('companionCenter').map((entry) => entry.descriptorId)
    expect(centerIds.slice().sort()).toEqual(COMPANION_IDS.slice().sort())
    // Other pages have no catalog entries yet, so their "+" menu stays empty rather than Home's.
    expect(catalogEntriesForPage('settings')).toEqual([])
  })

})
