export interface ExternalContextVersionDto {
  id: string;
  session_id: string;
  run_id: string | null;
  revision: number;
  kind: string;
  status: string;
  base_revision: number | null;
  created_at: string | null;
}

function isRecord(v: unknown): v is Record<string, unknown> { return typeof v === 'object' && v !== null && !Array.isArray(v); }

export function mapContextVersion(core: unknown): ExternalContextVersionDto | null {
  if (!isRecord(core)) return null;
  const id = String(core.id ?? '');
  if (!id) return null;
  return {
    id,
    session_id: String(core.session_id ?? core.sessionId ?? ''),
    run_id: (core.run_id as string) ?? (core.runId as string) ?? null,
    revision: Number(core.revision ?? 0),
    kind: String(core.kind ?? ''),
    status: String(core.status ?? ''),
    base_revision: (core.base_revision as number) ?? (core.baseRevision as number) ?? null,
    created_at: (core.created_at as string) ?? null,
  };
}

export function mapContextVersions(core: unknown): ExternalContextVersionDto[] | unknown {
  if (core && typeof core === 'object' && !Array.isArray(core) && 'proxied' in (core as Record<string, unknown>)) return core;
  if (Array.isArray(core)) return core.map(mapContextVersion).filter((x): x is ExternalContextVersionDto => x !== null);
  return [];
}
