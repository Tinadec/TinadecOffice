import { describe, expect, it } from 'vitest'
import { Globe } from '@lucide/vue'
import { FEATURE_CATALOG, catalogEntriesForPage, featureIconFor } from './featureCatalog'

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

  it('registers live-digest as the first-class leading feature panel entry', () => {
    expect(FEATURE_CATALOG[0]?.descriptorId).toBe('live-digest')
    const entry = FEATURE_CATALOG.find((f) => f.descriptorId === 'live-digest')
    expect(entry).toBeTruthy()
    expect(entry!.titleKey).toBe('context.homeLiveDigest')
    expect(entry!.descKey).toBe('context.homeLiveDigestDesc')
    expect(FEATURE_CATALOG.filter((f) => f.titleKey === entry!.titleKey)).toHaveLength(1)
  })

  it('registers trajectory right after live-digest as a first-class governance feature entry', () => {
    expect(FEATURE_CATALOG[1]?.descriptorId).toBe('trajectory')
    const entry = FEATURE_CATALOG[1]
    expect(entry).toBeTruthy()
    expect(entry!.titleKey).toBe('context.homeTrajectory')
    expect(entry!.descKey).toBe('context.homeTrajectoryDesc')
    expect(entry!.group).toBe('gov')
    expect(entry!.color).toBe('#5b9fe8')
    expect(FEATURE_CATALOG.filter((f) => f.titleKey === entry!.titleKey)).toHaveLength(1)
    expect(featureIconFor('trajectory', Globe)).not.toBe(Globe)
  })

  it('keeps home workspace tiles scoped to the home page', () => {
    for (const entry of FEATURE_CATALOG) {
      expect(entry.pages ?? ['home']).toContain('home')
    }
    const homeIds = catalogEntriesForPage('home').map((entry) => entry.descriptorId)
    expect(homeIds[0]).toBe('live-digest')
    expect(homeIds[1]).toBe('trajectory')
    expect(homeIds).toContain('workspace')
    expect(catalogEntriesForPage('companionCenter')).toEqual([])
    expect(catalogEntriesForPage('settings')).toEqual([])
    expect(catalogEntriesForPage('board')).toEqual([])
    expect(catalogEntriesForPage('projectHub')).toEqual([])
  })

  it('resolves secondary icons for board cards without polluting FEATURE_CATALOG', () => {
    expect(featureIconFor('boardTaskInspector', Globe)).not.toBe(Globe)
    expect(featureIconFor('globalBoard', Globe)).not.toBe(Globe)
    expect(featureIconFor('projectHub', Globe)).not.toBe(Globe)
    expect(featureIconFor('unknown_card', Globe)).toBe(Globe)
  })
})
