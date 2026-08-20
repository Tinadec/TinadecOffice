export interface ExternalTaskNodeDto {
  id: string;
  graph_id: string | null;
  run_id: string;
  session_id: string;
  title: string;
  description: string;
  status: string;
  priority: number;
  risk: string;
  success_criteria: string[];
  dependencies: string[];
  required_capabilities: string[];
  created_at: string | null;
  updated_at: string | null;
}

function isRecord(v: unknown): v is Record<string, unknown> { return typeof v === 'object' && v !== null && !Array.isArray(v); }

export function mapTaskNode(core: unknown): ExternalTaskNodeDto | null {
  if (!isRecord(core)) return null;
  const id = String(core.id ?? '');
  if (!id) return null;
  return {
    id,
    graph_id: (core.graph_id as string) ?? (core.graphId as string) ?? null,
    run_id: String(core.run_id ?? core.runId ?? ''),
    session_id: String(core.session_id ?? core.sessionId ?? ''),
    title: String(core.title ?? ''),
    description: String(core.description ?? ''),
    status: String(core.status ?? 'unknown'),
    priority: Number(core.priority ?? 1),
    risk: String(core.risk ?? 'medium'),
    success_criteria: (core.success_criteria as string[]) ?? (core.successCriteria as string[]) ?? [],
    dependencies: (core.dependencies as string[]) ?? [],
    required_capabilities: (core.required_capabilities as string[]) ?? (core.requiredCapabilities as string[]) ?? [],
    created_at: (core.created_at as string) ?? null,
    updated_at: (core.updated_at as string) ?? null,
  };
}

export function mapTaskNodes(core: unknown): ExternalTaskNodeDto[] | unknown {
  if (core && typeof core === 'object' && !Array.isArray(core) && 'proxied' in (core as Record<string, unknown>)) return core;
  if (Array.isArray(core)) return core.map(mapTaskNode).filter((x): x is ExternalTaskNodeDto => x !== null);
  return [];
}
