import type { MeetingModelOverrideDto, SpaceOptionsDto, SessionSettingsUpdate } from '@/api'
import type { ContextStrategyTier, MemoryEvolutionMode, RecallFidelity } from '@/lib/contextMemoryApi'
import type { PermissionLevel } from '@/types/mode'

export type ContextPolicyParamsOverride = NonNullable<SpaceOptionsDto['context_advanced']>

export interface ModeRuntimePolicyLike {
  kind?: string
  settings?: object | null
}

export function normalizeContextStrategy(raw: unknown): ContextStrategyTier | null {
  return raw === 'direct' || raw === 'managed' || raw === 'deep_curator' ? raw : null
}

function normalizeRecallFidelity(raw: unknown): RecallFidelity | undefined {
  return raw === 'summary_only' || raw === 'balanced' || raw === 'verbatim_first' ? raw : undefined
}

function normalizeEvolutionMode(raw: unknown): MemoryEvolutionMode | undefined {
  return raw === 'auto_evolve' || raw === 'candidate_only' ? raw : undefined
}

function normalizeFiniteNumber(raw: unknown, integer = false): number | undefined {
  if (typeof raw !== 'number' || !Number.isFinite(raw)) return undefined
  return integer ? Math.round(raw) : raw
}

export function normalizeContextAdvanced(raw: unknown): ContextPolicyParamsOverride | null {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return null
  const rec = raw as Record<string, unknown>
  const recallFidelity = normalizeRecallFidelity(rec.recall_fidelity ?? rec.recall_policy)
  const compactionTriggerRatio = normalizeFiniteNumber(rec.compaction_trigger_ratio)
  const recentMessageLimit = normalizeFiniteNumber(rec.recent_message_limit, true)
  const allowCrossSessionRecall = typeof rec.allow_cross_session_recall === 'boolean'
    ? rec.allow_cross_session_recall
    : undefined
  const evolutionMode = normalizeEvolutionMode(rec.evolution_mode ?? rec.memory_evolution_mode)
  const coreMarkdownMaxTokens = normalizeFiniteNumber(rec.core_markdown_max_tokens, true)

  const out: ContextPolicyParamsOverride = {}
  let hasAny = false
  if (recallFidelity !== undefined) {
    out.recall_fidelity = recallFidelity
    hasAny = true
  }
  if (compactionTriggerRatio !== undefined) {
    out.compaction_trigger_ratio = compactionTriggerRatio
    hasAny = true
  }
  if (recentMessageLimit !== undefined) {
    out.recent_message_limit = recentMessageLimit
    hasAny = true
  }
  if (allowCrossSessionRecall !== undefined) {
    out.allow_cross_session_recall = allowCrossSessionRecall
    hasAny = true
  }
  if (evolutionMode !== undefined) {
    out.evolution_mode = evolutionMode
    hasAny = true
  }
  if (coreMarkdownMaxTokens !== undefined) {
    out.core_markdown_max_tokens = coreMarkdownMaxTokens
    hasAny = true
  }
  return hasAny ? out : null
}

export function copyContextAdvanced(raw?: ContextPolicyParamsOverride | null): ContextPolicyParamsOverride | null {
  const normalized = normalizeContextAdvanced(raw)
  return normalized ? { ...normalized } : null
}

export function copySpaceOptions(options: SpaceOptionsDto): SpaceOptionsDto {
  return {
    ...options,
    context_strategy: normalizeContextStrategy(options.context_strategy),
    context_advanced: copyContextAdvanced(options.context_advanced),
  }
}

export function contextAdvancedEqual(
  a?: ContextPolicyParamsOverride | null,
  b?: ContextPolicyParamsOverride | null,
): boolean {
  const left = normalizeContextAdvanced(a)
  const right = normalizeContextAdvanced(b)
  if (left === null && right === null) return true
  if (left === null || right === null) return false
  return (left.recall_fidelity ?? null) === (right.recall_fidelity ?? null)
    && (left.compaction_trigger_ratio ?? null) === (right.compaction_trigger_ratio ?? null)
    && (left.recent_message_limit ?? null) === (right.recent_message_limit ?? null)
    && (left.allow_cross_session_recall ?? null) === (right.allow_cross_session_recall ?? null)
    && (left.evolution_mode ?? null) === (right.evolution_mode ?? null)
    && (left.core_markdown_max_tokens ?? null) === (right.core_markdown_max_tokens ?? null)
}

export function defaultSpaceOptions(): SpaceOptionsDto {
  return {
    plan_first: false,
    spec_enabled: false,
    multi_agent: false,
    workflow_mode_version_id: null,
    bulletin_board: false,
    worktree: false,
    context_strategy: null,
    context_advanced: null,
  }
}

export function resolveModeDefaultSpaceOptions(
  defaults?: Partial<SpaceOptionsDto> | null,
  runtimePolicy?: ModeRuntimePolicyLike | null,
  legacyCanvasLayout?: Record<string, unknown> | null,
): SpaceOptionsDto {
  const base = defaultSpaceOptions()
  const policySettings = runtimePolicy?.settings && typeof runtimePolicy.settings === 'object' && !Array.isArray(runtimePolicy.settings)
    ? (runtimePolicy.settings as Record<string, unknown>)
    : null
  const policyStrategy = policySettings ? normalizeContextStrategy(policySettings.context_strategy) : null
  const policyAdvanced = policySettings ? normalizeContextAdvanced(policySettings) : null
  const legacyStrategy = legacyCanvasLayout ? normalizeContextStrategy(legacyCanvasLayout.context_strategy) : null
  const legacyAdvanced = legacyCanvasLayout ? normalizeContextAdvanced(legacyCanvasLayout.context_advanced) : null
  if (!defaults) {
    return {
      ...base,
      context_strategy: policyStrategy ?? legacyStrategy,
      context_advanced: policyAdvanced ?? legacyAdvanced,
    }
  }
  const hasExplicitStrategy = defaults.context_strategy !== undefined
  const hasExplicitAdvanced = defaults.context_advanced !== undefined
  const explicitAdvanced = normalizeContextAdvanced(defaults.context_advanced)
  const fallbackAdvanced = policyAdvanced ?? (hasExplicitAdvanced ? null : legacyAdvanced)
  const mergedAdvanced = fallbackAdvanced || explicitAdvanced
    ? normalizeContextAdvanced({
        ...(fallbackAdvanced ?? {}),
        ...(explicitAdvanced ?? {}),
      })
    : null
  return {
    plan_first: Boolean(defaults.plan_first),
    spec_enabled: Boolean(defaults.spec_enabled),
    multi_agent: Boolean(defaults.multi_agent),
    workflow_mode_version_id: defaults.workflow_mode_version_id ?? null,
    bulletin_board: Boolean(defaults.bulletin_board),
    worktree: Boolean(defaults.worktree),
    context_strategy: (hasExplicitStrategy ? normalizeContextStrategy(defaults.context_strategy) : null)
      ?? policyStrategy
      ?? (hasExplicitStrategy ? null : legacyStrategy),
    context_advanced: mergedAdvanced,
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
    && (left.context_strategy ?? null) === (right.context_strategy ?? null)
    && contextAdvancedEqual(left.context_advanced, right.context_advanced)
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
    space_options: settings.space_options ? copySpaceOptions(settings.space_options) : null,
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
