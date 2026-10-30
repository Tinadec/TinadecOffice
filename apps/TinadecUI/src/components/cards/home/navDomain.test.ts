import { describe, expect, it } from 'vitest'
import { isCompanionDomainPage } from './navDomain'

describe('nav card domain', () => {
  it('turns the shared sidebar into its companion domain only on the companion centre', () => {
    expect(isCompanionDomainPage('companionCenter')).toBe(true)
    // Every work page keeps projects, sessions and the work footer.
    for (const pageId of ['home', 'settings', 'market', 'code', 'debug'] as const) {
      expect(isCompanionDomainPage(pageId)).toBe(false)
    }
    // A store that has not switched pages yet must not flip the sidebar.
    expect(isCompanionDomainPage(undefined)).toBe(false)
  })
})
