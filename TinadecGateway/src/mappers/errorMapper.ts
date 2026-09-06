/** RFC9457 ProblemDetails -> stable external code mapper */
const CODE_MAP: Record<string, string> = {
  INVALID_REQUEST: 'invalid_request',
  INVALID_PROJECT: 'invalid_request',
  INVALID_SESSION: 'invalid_request',
  INVALID_SESSION_ID: 'invalid_request',
  INVALID_PROJECT_ID: 'invalid_request',
  INVALID_MESSAGE: 'invalid_request',
  INVALID_RUN_ID: 'invalid_request',
  INVALID_EVENT_CURSOR: 'invalid_request',
  INVALID_STREAM_CURSOR: 'invalid_request',
  INVALID_RUN_CONTROL: 'invalid_request',
  CONTEXT_REVISION_CONFLICT: 'context_conflict',
  CONTEXT_CONFLICT: 'context_conflict',
  MODEL_NOT_CONFIGURED: 'model_not_configured',
  NOT_FOUND: 'not_found',
  RUN_NOT_FOUND: 'run_not_found',
  SESSION_NOT_FOUND: 'session_not_found',
  PROJECT_NOT_FOUND: 'project_not_found',
  TOOL_EXECUTION_NOT_FOUND: 'tool_execution_not_found',
  TOOL_PROVIDER_UNAVAILABLE: 'tool_provider_unavailable',
  TOOL_RUNTIME_NOT_CONFIGURED: 'tool_runtime_not_configured',
  MODE_UNAVAILABLE: 'mode_unavailable',
  TOOL_PROVIDER_NOT_CONFIGURED: 'tool_provider_not_configured',
  FORBIDDEN: 'forbidden',
  ACTIVE_RUN_LIMIT: 'conflict',
  ACTIVE_RUN_CONFLICT: 'conflict',
  INVALID_LIFECYCLE_TRANSITION: 'conflict',
  INVALID_LIFECYCLE_STATUS: 'invalid_request',
  IDEMPOTENCY_KEY_REUSE: 'conflict',
  RUN_NOT_ACTIVE: 'conflict',
};

const ALLOWED_CODES = new Set([
  'invalid_request',
  'context_conflict',
  'model_not_configured',
  'not_found',
  'run_not_found',
  'session_not_found',
  'project_not_found',
  'tool_execution_not_found',
  'tool_provider_unavailable',
  'mode_unavailable',
  'tool_runtime_not_configured',
  'tool_provider_not_configured',
  'forbidden',
  'conflict',
  'invalid_agent_pack_manifest',
  'agent_pack_management_forbidden',
  'agent_pack_version_hash_conflict',
  'agent_pack_resource_conflict',
  'agent_pack_revision_conflict',
  'agent_pack_incompatible',
  'agent_pack_not_found',
  'agent_pack_owner_conflict',
  'agent_pack_preview_stale',
  'managed_resource_read_only',
]);

function normalizeCode(raw?: string | null): string {
  if (!raw) return 'conflict';
  const upper = raw.toUpperCase();
  if (CODE_MAP[upper]) return CODE_MAP[upper]!;
  const lower = raw.toLowerCase();
  if (ALLOWED_CODES.has(lower)) return lower;
  // already snake lower?
  return 'conflict';
}

export interface ProblemDetails {
  type: string;
  title: string;
  status: number;
  detail?: string;
  code: string;
  trace_id?: string;
  instance?: string;
}

export function toProblemDetails(status: number, codeRaw: string, detail: string, instance?: string, traceId?: string): ProblemDetails {
  const code = normalizeCode(codeRaw);
  return {
    type: `https://tinadec.dev/errors/${code}`,
    title: code,
    status,
    detail,
    code,
    instance,
    trace_id: traceId,
  };
}

export function mapCoreErrorToExternal(status: number, data: unknown, instance?: string): ProblemDetails {
  if (data && typeof data === 'object') {
    const rec = data as Record<string, unknown>;
    const codeRaw = (rec.code as string) ?? (rec.title as string) ?? 'conflict';
    const detail = (rec.detail as string) ?? (rec.message as string) ?? (rec.title as string) ?? 'Request failed.';
    const traceId = rec.trace_id as string | undefined ?? rec.traceId as string | undefined;
    const mapped = normalizeCode(codeRaw);
    return {
      type: (rec.type as string) ?? `https://tinadec.dev/errors/${mapped}`,
      title: mapped,
      status,
      detail: String(detail),
      code: mapped,
      instance: (rec.instance as string) ?? instance,
      trace_id: traceId,
    };
  }
  return toProblemDetails(status, 'conflict', 'Request failed.', instance);
}

export function isProblemDetailsLike(data: unknown): boolean {
  return !!data && typeof data === 'object' && 'code' in (data as Record<string,unknown>);
}
