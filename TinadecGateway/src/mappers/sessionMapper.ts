/** Optional per-session or per-mode context governance parameter overrides. */
export interface ContextPolicyParamsOverrideDto {
  recall_fidelity?: 'summary_only' | 'balanced' | 'verbatim_first' | null;
  compaction_trigger_ratio?: number | null;
  recent_message_limit?: number | null;
  allow_cross_session_recall?: boolean | null;
  evolution_mode?: 'auto_evolve' | 'candidate_only' | null;
  core_markdown_max_tokens?: number | null;
}

/** Core-owned, per-task spatial orchestration preferences. */
export interface SpaceOptionsDto {
  plan_first: boolean;
  spec_enabled: boolean;
  multi_agent: boolean;
  workflow_mode_version_id: string | null;
  bulletin_board: boolean;
  worktree: boolean;
  context_strategy?: 'direct' | 'managed' | 'deep_curator' | null;
  context_advanced?: ContextPolicyParamsOverrideDto | null;
}

/** Session mapper */
export interface ExternalSessionDto {
  storage_id?: string;
  id: string;
  /**
   * Null for a projectless (free-conversation) session. Core omits the field via
   * `WhenWritingNull`, and a coerced empty string would satisfy truthiness checks
   * meant to detect free conversations while failing strict equality against a
   * real project id.
   */
  project_id: string | null;
  title: string | null;
  status: string | null;
  mode: string | null;
  mode_version_id: string | null;
  view_mode: 'flat' | 'space';
  permission_mode: string;
  space_options: SpaceOptionsDto | null;
  settings_revision: number;
  /** ConversationIdentity (DmaEA graph orchestration), frozen at session creation; null on pre-identity rows. */
  conversation_node_key: string | null;
  conversation_template_slug: string | null;
  meeting_model_override: { provider_instance_id: string; model?: string | null } | null;
  summary: string | null;
  history_revision: number | null;
  created_at: string | null;
  updated_at: string | null;
  lifecycle_status: 'active' | 'archived' | 'trashed';
  trashed_at: string | null;
}

function isRecord(v: unknown): v is Record<string, unknown> { return typeof v === 'object' && v !== null && !Array.isArray(v); }

function nullableId(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

export function mapSession(core: unknown): ExternalSessionDto | null {
  if (!isRecord(core)) return null;
  const id = String(core.id ?? '');
  if (!id) return null;
  const override = isRecord(core.meeting_model_override) || isRecord(core.meetingModelOverride)
    ? (core.meeting_model_override ?? core.meetingModelOverride) as { provider_instance_id?: string; model?: string | null }
    : null;
  const rawLifecycle = core.lifecycle_status ?? core.lifecycleStatus;
  return {
    ...(typeof core.storage_id === 'string' ? { storage_id: core.storage_id } : {}),
    id,
    project_id: nullableId(core.project_id ?? core.projectId),
    title: (core.title as string) ?? null,
    status: (core.status as string) ?? null,
    mode: (core.mode as string) ?? null,
    mode_version_id: (core.mode_version_id as string) ?? (core.modeVersionId as string) ?? null,
    view_mode: (core.view_mode ?? core.viewMode) === 'space' ? 'space' : 'flat',
    permission_mode: typeof core.permission_mode === 'string' ? core.permission_mode : 'default',
    space_options: isRecord(core.space_options) ? core.space_options as unknown as SpaceOptionsDto : null,
    settings_revision: typeof core.settings_revision === 'number' ? core.settings_revision : 0,
    conversation_node_key: (core.conversation_node_key as string) ?? (core.conversationNodeKey as string) ?? null,
    conversation_template_slug: (core.conversation_template_slug as string) ?? (core.conversationTemplateSlug as string) ?? null,
    meeting_model_override: override && typeof override.provider_instance_id === 'string'
      ? { provider_instance_id: override.provider_instance_id, model: override.model ?? null }
      : null,
    summary: (core.summary as string) ?? null,
    history_revision: (core.history_revision as number) ?? (core.historyRevision as number) ?? null,
    created_at: (core.created_at as string) ?? null,
    updated_at: (core.updated_at as string) ?? null,
    lifecycle_status: rawLifecycle === 'archived' || rawLifecycle === 'trashed' ? rawLifecycle : 'active',
    trashed_at: (core.trashed_at as string) ?? (core.trashedAt as string) ?? null,
  };
}

export function mapSessions(core: unknown): ExternalSessionDto[] {
  if (Array.isArray(core)) return core.map(mapSession).filter((x): x is ExternalSessionDto => x !== null);
  return [];
}
