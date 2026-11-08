import { beforeEach, describe, expect, it } from 'vitest'
import { buddyViewActive, closeBuddyView, openBuddyView, resetBuddyView, toggleBuddyView } from './buddyView'

beforeEach(() => resetBuddyView())

describe('buddyView', () => {
  it('toggles on and off', () => {
    toggleBuddyView()
    expect(buddyViewActive.value).toBe(true)
    toggleBuddyView()
    expect(buddyViewActive.value).toBe(false)
  })

  it('opens idempotently, so a deep link cannot close an already open view', () => {
    openBuddyView()
    openBuddyView()
    expect(buddyViewActive.value).toBe(true)
  })

  it('closes without caring whether it was open', () => {
    closeBuddyView()
    expect(buddyViewActive.value).toBe(false)
    openBuddyView()
    closeBuddyView()
    expect(buddyViewActive.value).toBe(false)
  })
})
