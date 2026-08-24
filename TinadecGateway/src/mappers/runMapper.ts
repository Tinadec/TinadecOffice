export interface ExternalRunDto {
  id: string;
  session_id: string;
  trigger_message_id: string | null;
  status: string;
  summary: string | null;
  task_revision: number | null;
  latest_event_sequence: number | null;
  latest_event_at: string | null;
  created_at: string | null;
  updated_at: string | null;
  completed_at: string | null;
}

function isRecord(v: unknown): v is Record<string, unknown> { return typeof v === 'object' && v !== null && !Array.isArray(v); }

export function mapRun(core: unknown): ExternalRunDto | null {
  if (!isRecord(core)) return null;
  const id = String(core.id ?? '');
  if (!id) return null;
  return {
    id,
    session_id: String(core.session_id ?? core.sessionId ?? ''),
    trigger_message_id: (core.trigger_message_id as string) ?? (core.triggerMessageId as string) ?? null,
    status: String(core.status ?? 'unknown'),
    summary: (core.summary as string) ?? null,
    task_revision: (core.task_revision as number) ?? (core.taskRevision as number) ?? null,
    latest_event_sequence: (core.latest_event_sequence as number) ?? (core.latestEventSequence as number) ?? null,
    latest_event_at: (core.latest_event_at as string) ?? (core.latestEventAt as string) ?? null,
    created_at: (core.created_at as string) ?? null,
    updated_at: (core.updated_at as string) ?? null,
    completed_at: (core.completed_at as string) ?? null,
  };
}

export function mapRuns(core: unknown): ExternalRunDto[] {
  if (Array.isArray(core)) return core.map(mapRun).filter((x): x is ExternalRunDto => x !== null);
  return [];
}

// 10-state validator
export const RUN_STATUSES = new Set(['planning','understanding','executing','replanning','awaiting_approval','awaiting_delegate','awaiting_user','paused','reviewing','completed','failed','cancelled']);

export function normalizeRunStatus(status: string): string {
  const s = status.toLowerCase();
  if (s === 'planning') return 'planning';
  return RUN_STATUSES.has(s) ? s : s;
}
