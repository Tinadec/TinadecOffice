/** Session mapper */
export interface ExternalSessionDto {
  id: string;
  project_id: string;
  title: string | null;
  status: string | null;
  mode: string | null;
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
  return {
    id,
    project_id: String(core.project_id ?? core.projectId ?? ''),
    title: (core.title as string) ?? null,
    status: (core.status as string) ?? null,
    mode: (core.mode as string) ?? null,
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
