/**
 * The seam between "which avatar does this companion have" and "can this build draw it".
 *
 * A Petdex sprite atlas is the only avatar kind the desktop ships a renderer for. Live2D models
 * can be imported and their capabilities read, but Cubism Core is deliberately not bundled, so a
 * Live2D avatar resolves to an explicit unavailable state. It must never be drawn as a sprite:
 * that would look like a working avatar while showing a completely different picture.
 */

export type AvatarKind = 'sprite' | 'live2d'

/** One imported Live2D model, reduced to what the kind decision needs. */
export interface Live2dAvatarRef {
  id: string
  name?: string | null
}

export type AvatarPlan =
  | { kind: 'sprite'; slug: string; ready: true }
  | { kind: 'live2d'; modelId: string; modelName: string; ready: false; reason: 'renderer_not_bundled' }
  | { kind: 'none'; ready: false; reason: 'no_avatar' }

/**
 * Which kind a persisted avatar value names.
 *
 * A value that matches an imported Live2D model is a Live2D avatar; everything else is a sprite
 * slug, which is the only thing companions can currently store. `null` means "no avatar at all".
 */
export function avatarKindOf(
  avatar: string | null | undefined,
  live2dIds: readonly string[] = [],
): AvatarKind | null {
  const id = avatar?.trim()
  if (!id) return null
  return live2dIds.includes(id) ? 'live2d' : 'sprite'
}

/** Resolve a kind and id into what a surface should draw, including why it cannot draw it. */
export function planAvatar(
  kind: AvatarKind | null | undefined,
  id: string | null | undefined,
  name?: string | null,
): AvatarPlan {
  const value = id?.trim()
  if (!value) return { kind: 'none', ready: false, reason: 'no_avatar' }
  if (kind === 'live2d') {
    return {
      kind: 'live2d',
      modelId: value,
      modelName: name?.trim() || value,
      ready: false,
      reason: 'renderer_not_bundled',
    }
  }
  return { kind: 'sprite', slug: value, ready: true }
}

/** Decide what to draw for a companion's stored avatar value. */
export function planCompanionAvatar(
  avatar: string | null | undefined,
  live2dModels: readonly Live2dAvatarRef[] = [],
): AvatarPlan {
  const id = avatar?.trim()
  if (!id) return { kind: 'none', ready: false, reason: 'no_avatar' }
  const model = live2dModels.find((item) => item.id === id)
  return planAvatar(model ? 'live2d' : 'sprite', id, model?.name)
}
