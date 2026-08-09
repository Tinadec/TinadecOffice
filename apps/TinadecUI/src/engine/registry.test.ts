import { describe, it, expect } from 'vitest'
import { createCardRegistry } from './registry'
import { makeCard } from './__testUtils'

describe('card registry', () => {
  it('registers and retrieves descriptors', () => {
    const reg = createCardRegistry()
    reg.register(makeCard('git'))
    expect(reg.has('git')).toBe(true)
    expect(reg.get('git')?.defaultTitle).toBe('git')
  })

  it('rejects duplicate descriptor types', () => {
    const reg = createCardRegistry()
    reg.register(makeCard('git'))
    expect(() => reg.register(makeCard('git'))).toThrow(/duplicate/)
  })

  it('rejects descriptors without a type', () => {
    const reg = createCardRegistry()
    expect(() => reg.register(makeCard(''))).toThrow()
  })

  it('lists all and singleton ids', () => {
    const reg = createCardRegistry()
    reg.register(makeCard('nav', { singleton: true }))
    reg.register(makeCard('chat', { singleton: true }))
    reg.register(makeCard('git'))
    expect(reg.list().length).toBe(3)
    expect([...reg.singletonIds()].sort()).toEqual(['chat', 'nav'])
  })
})
