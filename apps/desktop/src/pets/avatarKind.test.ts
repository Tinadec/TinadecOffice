import { describe, expect, it } from 'vitest'
import { avatarKindOf, planAvatar, planCompanionAvatar } from './avatarKind'

describe('avatar kind seam', () => {
  it('treats an ordinary companion avatar value as a sprite', () => {
    expect(avatarKindOf('panda')).toBe('sprite')
    expect(planCompanionAvatar('panda')).toEqual({ kind: 'sprite', slug: 'panda', ready: true })
  })

  it('recognizes an imported Live2D model and reports sdk_not_loaded until Cubism Core is ready', () => {
    const models = [{ id: 'model-1', name: 'Haru' }]
    expect(avatarKindOf('model-1', ['model-1'])).toBe('live2d')
    expect(planCompanionAvatar('model-1', models, { live2dSdkReady: false })).toEqual({
      kind: 'live2d',
      modelId: 'model-1',
      modelName: 'Haru',
      ready: false,
      reason: 'sdk_not_loaded',
    })
  })

  it('marks a Live2D model ready when BYO Cubism Core runtime is loaded', () => {
    const models = [{ id: 'model-1', name: 'Haru' }]
    expect(planCompanionAvatar('model-1', models, { live2dSdkReady: true })).toEqual({
      kind: 'live2d',
      modelId: 'model-1',
      modelName: 'Haru',
      ready: true,
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
    expect(planAvatar('live2d', 'model-1', '  ', { live2dSdkReady: false })).toMatchObject({ modelName: 'model-1' })
  })

  it('maps an explicit kind without consulting a list', () => {
    expect(planAvatar('sprite', 'panda')).toEqual({ kind: 'sprite', slug: 'panda', ready: true })
    expect(planAvatar(null, 'panda').kind).toBe('sprite')
    expect(planAvatar('live2d', 'x', null, { live2dSdkReady: true })).toEqual({
      kind: 'live2d',
      modelId: 'x',
      modelName: 'x',
      ready: true,
    })
  })
})
