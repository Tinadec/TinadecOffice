/** Session mapper */
export interface ExternalSessionDto {
  id: string;
  project_id: string;
  title: string | null;
  status: string | null;
  mode: string | null;
  mode_version_id: string | null;
  meeting_model_override: { provider_instance_id: string; model?: string | null } | null;
  summary: string | null;
  history_revision: number | null;
  created_at: string | null;
  updated_at: string | null;
  archived: boolean;
}

function isRecord(v: unknown): v is Record<string, unknown> { return typeof v === 'object' && v !== null && !Array.isArray(v); }

export function mapSession(core: unknown): ExternalSessionDto | null {
  if (!isRecord(core)) return null;
  const id = String(core.id ?? '');
  if (!id) return null;
  const override = isRecord(core.meeting_model_override) || isRecord(core.meetingModelOverride)
    ? (core.meeting_model_override ?? core.meetingModelOverride) as { provider_instance_id?: string; model?: string | null }
    : null;
  return {
    id,
    project_id: String(core.project_id ?? core.projectId ?? ''),
    title: (core.title as string) ?? null,
    status: (core.status as string) ?? null,
    mode: (core.mode as string) ?? null,
    mode_version_id: (core.mode_version_id as string) ?? (core.modeVersionId as string) ?? null,
    meeting_model_override: override && typeof override.provider_instance_id === 'string'
      ? { provider_instance_id: override.provider_instance_id, model: override.model ?? null }
      : null,
    summary: (core.summary as string) ?? null,
    history_revision: (core.history_revision as number) ?? (core.historyRevision as number) ?? null,
    created_at: (core.created_at as string) ?? null,
    updated_at: (core.updated_at as string) ?? null,
    archived: Boolean(core.archived),
  };
}

export function mapSessions(core: unknown): ExternalSessionDto[] {
  if (Array.isArray(core)) return core.map(mapSession).filter((x): x is ExternalSessionDto => x !== null);
  return [];
}
