/** Validates invoke-stream envelope: 5 required + 2 optional, snake_case */
export interface InvokeStreamExternalRequest {
  content: string;
  client_message_id: string;
  application_mode: string;
  agent_mode: string;
  permission_mode: string;
  target_run_id?: string | null;
  expected_context_revision?: number | null;
}

export function validateInvokeStreamBody(body: unknown): { ok: true; value: InvokeStreamExternalRequest } | { ok: false; errors: string[] } {
  if (!body || typeof body !== 'object' || Array.isArray(body)) {
    return { ok: false, errors: ['Body must be a JSON object.'] };
  }
  const rec = body as Record<string, unknown>;
  const errors: string[] = [];
  const content = typeof rec.content === 'string' ? rec.content.trim() : '';
  if (!content) errors.push('content is required and must be non-empty string.');
  const clientMessageId = typeof rec.client_message_id === 'string' ? rec.client_message_id.trim() : typeof rec.clientMessageId === 'string' ? rec.clientMessageId.trim() : '';
  if (!clientMessageId) errors.push('client_message_id is required.');
  const applicationMode = typeof rec.application_mode === 'string' ? rec.application_mode.trim() : typeof rec.applicationMode === 'string' ? rec.applicationMode.trim() : '';
  if (!applicationMode) errors.push('application_mode is required.');
  const agentMode = typeof rec.agent_mode === 'string' ? rec.agent_mode.trim() : typeof rec.agentMode === 'string' ? rec.agentMode.trim() : '';
  if (!agentMode) errors.push('agent_mode is required.');
  const permissionMode = typeof rec.permission_mode === 'string' ? rec.permission_mode.trim() : typeof rec.permissionMode === 'string' ? rec.permissionMode.trim() : '';
  if (!permissionMode) errors.push('permission_mode is required.');

  if (errors.length) return { ok: false, errors };

  const targetRunId = (rec.target_run_id as string | null) ?? (rec.targetRunId as string | null) ?? null;
  const expectedContextRevision = (rec.expected_context_revision as number | null) ?? (rec.expectedContextRevision as number | null) ?? null;

  if (targetRunId !== null && typeof targetRunId === 'string' && targetRunId.trim() !== '' ) {
    // must be uuid/guid like, but Core will validate; just ensure non-empty
  }

  return {
    ok: true,
    value: {
      content,
      client_message_id: clientMessageId!,
      application_mode: applicationMode!,
      agent_mode: agentMode!,
      permission_mode: permissionMode!,
      target_run_id: targetRunId,
      expected_context_revision: expectedContextRevision != null ? Number(expectedContextRevision) : null,
    },
  };
}

export function toCoreInvokeStreamBody(ext: InvokeStreamExternalRequest): Record<string, unknown> {
  // Core expects snake_case via options, but we send snake_case explicitly
  const body: Record<string, unknown> = {
    content: ext.content,
    client_message_id: ext.client_message_id,
    application_mode: ext.application_mode,
    agent_mode: ext.agent_mode,
    permission_mode: ext.permission_mode,
  };
  if (ext.target_run_id) body.target_run_id = ext.target_run_id;
  if (ext.expected_context_revision != null) body.expected_context_revision = ext.expected_context_revision;
  return body;
}
