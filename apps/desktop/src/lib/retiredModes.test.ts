import { describe, expect, it } from 'vitest'
import { isRetiredMode, withoutRetiredModes } from './retiredModes'

describe('retired modes', () => {
  it('retires the legacy conversation.auto slug', () => {
    expect(isRetiredMode({ slug: 'conversation.auto', display_name: 'Auto' })).toBe(true)
    expect(isRetiredMode({ slug: 'CONVERSATION.AUTO' })).toBe(true)
  })

  it('retires a slug-less legacy row only when its label is Auto', () => {
    expect(isRetiredMode({ slug: null, display_name: 'Auto' })).toBe(true)
    expect(isRetiredMode({ slug: null, display_name: '自动' })).toBe(false)
    expect(isRetiredMode({ slug: null, display_name: 'Team' })).toBe(false)
  })

  it('keeps every current mode', () => {
    const modes = [
      { slug: 'solo', display_name: 'Solo' },
      { slug: 'free_director', display_name: 'Team' },
      { slug: 'fixed_pipeline', display_name: 'Workflow' },
      { slug: 'conversation.auto', display_name: 'Auto' },
    ]
    expect(withoutRetiredModes(modes).map(mode => mode.slug)).toEqual(['solo', 'free_director', 'fixed_pipeline'])
  })
})
