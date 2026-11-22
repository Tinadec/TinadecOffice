/**
 * The seam between "which avatar does this companion have" and "can this runtime draw it".
 *
 * Sprite atlases render immediately via CSS frame stepping. Imported Live2D models render via
 * WebGL when the user-imported BYO `live2dcubismcore.min.js` runtime is loaded, and resolve to an
 * honest `sdk_not_loaded` state otherwise (never masquerading as a sprite).
 */

export type AvatarKind = 'sprite' | 'live2d'

/** One imported Live2D model, reduced to what the kind decision needs. */
export interface Live2dAvatarRef {
  id: string
  name?: string | null
}

export interface PlanAvatarOptions {
  /** Whether BYO `Live2DCubismCore` is currently available in this runtime. */
  live2dSdkReady?: boolean
}

export type AvatarPlan =
  | { kind: 'sprite'; slug: string; ready: true }
  | { kind: 'live2d'; modelId: string; modelName: string; ready: true }
  | { kind: 'live2d'; modelId: string; modelName: string; ready: false; reason: 'sdk_not_loaded' }
  | { kind: 'none'; ready: false; reason: 'no_avatar' }

export function hasCubismCoreGlobal(): boolean {
  if (typeof window === 'undefined') return false
  const win = window as unknown as { Live2DCubismCore?: { Model?: unknown; Moc?: unknown } }
  return Boolean(win.Live2DCubismCore && win.Live2DCubismCore.Moc && win.Live2DCubismCore.Model)
}

/**
 * Which kind a persisted avatar value names.
 *
 * A value that matches an imported Live2D model is a Live2D avatar; everything else is a sprite
 * slug. `null` or blank means "no avatar at all".
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
  options?: PlanAvatarOptions,
): AvatarPlan {
  const value = id?.trim()
  if (!value) return { kind: 'none', ready: false, reason: 'no_avatar' }
  if (kind === 'live2d') {
    const modelName = name?.trim() || value
    const sdkReady = options?.live2dSdkReady ?? hasCubismCoreGlobal()
    if (sdkReady) {
      return {
        kind: 'live2d',
        modelId: value,
        modelName,
        ready: true,
      }
    }
    return {
      kind: 'live2d',
      modelId: value,
      modelName,
      ready: false,
      reason: 'sdk_not_loaded',
    }
  }
  return { kind: 'sprite', slug: value, ready: true }
}

/** Decide what to draw for a companion's stored avatar value. */
export function planCompanionAvatar(
  avatar: string | null | undefined,
  live2dModels: readonly Live2dAvatarRef[] = [],
  options?: PlanAvatarOptions,
): AvatarPlan {
  const id = avatar?.trim()
  if (!id) return { kind: 'none', ready: false, reason: 'no_avatar' }
  const model = live2dModels.find((item) => item.id === id)
  return planAvatar(model ? 'live2d' : 'sprite', id, model?.name, options)
}
