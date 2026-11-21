import type { MeetingModelOverrideDto, SpaceOptionsDto, SessionSettingsUpdate } from '@/api'
import type { PermissionLevel } from '@/types/mode'

export function defaultSpaceOptions(): SpaceOptionsDto {
  return { plan_first: false, spec_enabled: false, multi_agent: false, workflow_mode_version_id: null, bulletin_board: false, worktree: false }
}

export function resolveModeDefaultSpaceOptions(defaults?: Partial<SpaceOptionsDto> | null): SpaceOptionsDto {
  const base = defaultSpaceOptions()
  if (!defaults) return base
  return {
    plan_first: Boolean(defaults.plan_first),
    spec_enabled: Boolean(defaults.spec_enabled),
    multi_agent: Boolean(defaults.multi_agent),
    workflow_mode_version_id: defaults.workflow_mode_version_id ?? null,
    bulletin_board: Boolean(defaults.bulletin_board),
    worktree: Boolean(defaults.worktree),
  }
}

export function spaceOptionsEqual(a?: Partial<SpaceOptionsDto> | null, b?: Partial<SpaceOptionsDto> | null): boolean {
  const left = resolveModeDefaultSpaceOptions(a)
  const right = resolveModeDefaultSpaceOptions(b)
  return left.plan_first === right.plan_first
    && left.spec_enabled === right.spec_enabled
    && left.multi_agent === right.multi_agent
    && left.bulletin_board === right.bulletin_board
    && left.worktree === right.worktree
    && (left.workflow_mode_version_id ?? null) === (right.workflow_mode_version_id ?? null)
}

export interface ComposerSettings {
  mode_version_id: string | null
  permission_mode: PermissionLevel
  meeting_model_override: MeetingModelOverrideDto | null
  space_options: SpaceOptionsDto | null
}

export function newComposerSettings(_view?: 'flat' | 'space'): ComposerSettings {
  return { mode_version_id: null, permission_mode: 'default', meeting_model_override: null, space_options: null }
}

/** Plain snapshots prevent a later switch (or reactive proxy) changing a queued request. */
export function copyComposerSettings(settings: ComposerSettings): ComposerSettings {
  return {
    ...settings,
    meeting_model_override: settings.meeting_model_override ? { ...settings.meeting_model_override } : null,
    space_options: settings.space_options ? { ...settings.space_options } : null,
  }
}

export function applyComposerSettings(settings: ComposerSettings, patch: SessionSettingsUpdate): ComposerSettings {
  return copyComposerSettings({
    mode_version_id: patch.clear_mode_version ? null : patch.mode_version_id !== undefined ? patch.mode_version_id : settings.mode_version_id,
    permission_mode: (patch.permission_mode ?? settings.permission_mode) as PermissionLevel,
    meeting_model_override: patch.clear_meeting_model_override ? null : patch.meeting_model_override ?? settings.meeting_model_override,
    space_options: patch.space_options !== undefined ? patch.space_options : settings.space_options,
  })
}
