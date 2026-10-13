import { describe, expect, it } from 'vitest'
import { PROVIDER_TEMPLATES } from './providerTemplates'

describe('provider templates', () => {

  it('keeps provider drivers unique', () => {
    const drivers = PROVIDER_TEMPLATES.map((template) => template.driver)

    expect(new Set(drivers).size).toBe(drivers.length)
  })
})
