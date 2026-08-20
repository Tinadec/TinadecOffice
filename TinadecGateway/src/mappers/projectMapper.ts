/** Explicit CoreDto -> ExternalDto for Project, no AutoMapper */
export interface CoreProjectDto {
  id: string;
  name: string;
  path: string;
  kind?: string;
  created_at?: string;
  updated_at?: string;
  archived?: boolean;
}

export interface ExternalProjectDto {
  id: string;
  name: string;
  path: string;
  kind: string | null;
  created_at: string | null;
  updated_at: string | null;
  archived: boolean;
}

function isRecord(v: unknown): v is Record<string, unknown> { return typeof v === 'object' && v !== null && !Array.isArray(v); }

export function mapProject(core: unknown): ExternalProjectDto | null {
  if (!isRecord(core)) return null;
  const id = String(core.id ?? '');
  if (!id) return null;
  return {
    id,
    name: String(core.name ?? ''),
    path: String(core.path ?? ''),
    kind: (core.kind as string) ?? null,
    created_at: (core.created_at as string) ?? (core.createdAt as string) ?? null,
    updated_at: (core.updated_at as string) ?? (core.updatedAt as string) ?? null,
    archived: Boolean(core.archived),
  };
}

export function mapProjects(core: unknown): ExternalProjectDto[] {
  if (Array.isArray(core)) return core.map(mapProject).filter((x): x is ExternalProjectDto => x !== null);
  return [];
}
