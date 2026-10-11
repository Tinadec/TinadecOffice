import type { MeetingModelOverrideDto, SpaceOptionsDto, SessionSettingsUpdate } from '@/api'
import type { PermissionLevel } from '@/types/mode'

export function defaultSpaceOptions(): SpaceOptionsDto {
  return { plan_first: false, spec_enabled: false, multi_agent: false, workflow_mode_version_id: null, bulletin_board: false, worktree: false }
}

export type TranscriptTemplate = 'concise' | 'standard' | 'detailed'

export interface ComposerSettings {
  mode_version_id: string | null
  permission_mode: PermissionLevel
  meeting_model_override: MeetingModelOverrideDto | null
  space_options: SpaceOptionsDto | null
  /** How much the conversation explains itself. Sent to Core, which puts it in the prompt. */
  transcript_template: TranscriptTemplate
}

export function newComposerSettings(view: 'flat' | 'space'): ComposerSettings {
  return { mode_version_id: null, permission_mode: 'default', meeting_model_override: null, space_options: view === 'space' ? defaultSpaceOptions() : null, transcript_template: 'standard' }
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
    transcript_template: patch.transcript_template ?? settings.transcript_template,
  })
}
