import { describe, expect, it } from 'vitest'
import { avatarKindOf, planAvatar, planCompanionAvatar } from './avatarKind'

describe('avatar kind seam', () => {
  it('treats an ordinary companion avatar value as a sprite', () => {
    expect(avatarKindOf('panda')).toBe('sprite')
    expect(planCompanionAvatar('panda')).toEqual({ kind: 'sprite', slug: 'panda', ready: true })
  })

  it('recognizes an imported Live2D model and refuses to pretend it is a sprite', () => {
    const models = [{ id: 'model-1', name: 'Haru' }]
    expect(avatarKindOf('model-1', ['model-1'])).toBe('live2d')
    expect(planCompanionAvatar('model-1', models)).toEqual({
      kind: 'live2d',
      modelId: 'model-1',
      modelName: 'Haru',
      ready: false,
      reason: 'renderer_not_bundled',
    })
  })

  it('keeps an unbound id a sprite even when other models exist', () => {
    const plan = planCompanionAvatar('panda', [{ id: 'model-1', name: 'Haru' }])
    expect(plan.kind).toBe('sprite')
  })

  it('reports no avatar for an empty value instead of inventing a default pet', () => {
    expect(avatarKindOf(null)).toBeNull()
    expect(avatarKindOf('  ')).toBeNull()
    expect(planCompanionAvatar(undefined)).toEqual({ kind: 'none', ready: false, reason: 'no_avatar' })
    expect(planAvatar('sprite', '')).toEqual({ kind: 'none', ready: false, reason: 'no_avatar' })
  })

  it('falls back to the model id when the imported name is blank', () => {
    expect(planAvatar('live2d', 'model-1', '  ')).toMatchObject({ modelName: 'model-1' })
  })

  it('maps an explicit kind without consulting a list', () => {
    expect(planAvatar('sprite', 'panda')).toEqual({ kind: 'sprite', slug: 'panda', ready: true })
    expect(planAvatar(null, 'panda').kind).toBe('sprite')
    expect(planAvatar('live2d', 'x').kind).toBe('live2d')
  })
})
