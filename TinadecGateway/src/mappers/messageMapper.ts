export interface ExternalMessageDto {
  id: string;
  session_id: string;
  run_id: string | null;
  role: string;
  content: string;
  created_at: string | null;
}

function isRecord(v: unknown): v is Record<string, unknown> { return typeof v === 'object' && v !== null && !Array.isArray(v); }

export function mapMessage(core: unknown): ExternalMessageDto | null {
  if (!isRecord(core)) return null;
  const id = String(core.id ?? '');
  if (!id) return null;
  return {
    id,
    session_id: String(core.session_id ?? core.sessionId ?? ''),
    run_id: (core.run_id as string) ?? (core.runId as string) ?? null,
    role: String(core.role ?? ''),
    content: String(core.content ?? ''),
    created_at: (core.created_at as string) ?? null,
  };
}

export function mapMessages(core: unknown): ExternalMessageDto[] {
  if (Array.isArray(core)) return core.map(mapMessage).filter((x): x is ExternalMessageDto => x !== null);
  return [];
}
