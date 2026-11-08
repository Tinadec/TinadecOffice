// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  activeSessionPartners,
  isSessionPartnerActive,
  resetSessionPartners,
  toggleSessionPartner,
} from './sessionPartner'

afterEach(() => {
  resetSessionPartners()
  vi.unstubAllGlobals()
})

describe('sessionPartner', () => {
  it('is a no-op without a session or a desktop host', async () => {
    const toggle = vi.fn()
    vi.stubGlobal('window', { tinadec: { sessionPartner: { toggle } } })
    await toggleSessionPartner(null)
    expect(toggle).not.toHaveBeenCalled()
    expect(isSessionPartnerActive('s1')).toBe(false)
  })

  it('mirrors the toggle receipt into the active set', async () => {
    const toggle = vi.fn()
      .mockResolvedValueOnce({ active: true })
      .mockResolvedValueOnce({ active: false })
    vi.stubGlobal('window', { tinadec: { sessionPartner: { toggle } } })

    await toggleSessionPartner('s1')
    // The scope travels with the toggle: the partner window cannot observe it itself, and a
    // read that omits it lands in the user store where a project session does not exist.
    expect(toggle).toHaveBeenCalledWith('s1', 'user')
    expect(isSessionPartnerActive('s1')).toBe(true)
    expect(activeSessionPartners.value.has('s1')).toBe(true)

    await toggleSessionPartner('s1')
    expect(isSessionPartnerActive('s1')).toBe(false)
  })

  it('does nothing when the host bridge is absent (browser preview)', async () => {
    vi.stubGlobal('window', {})
    await toggleSessionPartner('s1')
    expect(isSessionPartnerActive('s1')).toBe(false)
  })
})