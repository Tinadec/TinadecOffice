/**
 * Thin snake_case passthrough for session interactions — validates dispatch_mode
 * only.
 *
 * Interaction identity is the published `mode_version_id`. The retired six-value
 * `agent_mode` selector is not part of the contract any more: Core rejects any
 * unknown body field with `400 unknown_field`, so this mapper does not model it
 * and a stale client gets a structured failure instead of a silently ignored
 * field.
 */

export type DispatchMode = 'queued' | 'insert' | 'parallel';

export interface InteractionExternalRequest {
  dispatch_mode: DispatchMode;
  target_run_id?: string | null;
  content?: string;
  client_message_id?: string;
  mode_version_id?: string | null;
  /**
   * Ids of rows already uploaded to this session. Declared, not validated: the
   * shape rules (array of guids, per-message ceiling, which dispatch modes may
   * carry them) belong to Core, and a proxy that re-decides them can only drift.
   */
  attachment_ids?: string[];
  [key: string]: unknown;
}

const ALLOWED: DispatchMode[] = ['queued', 'insert', 'parallel'];

export function validateInteractionBody(body: unknown): { ok: true; value: InteractionExternalRequest } | { ok: false; errors: string[] } {
  if (!body || typeof body !== 'object' || Array.isArray(body)) {
    return { ok: false, errors: ['Body must be a JSON object.'] };
  }
  const rec = body as Record<string, unknown>;
  const errors: string[] = [];
  const dmRaw = typeof rec.dispatch_mode === 'string' ? rec.dispatch_mode.trim() : typeof (rec as Record<string,unknown>).dispatchMode === 'string' ? String((rec as Record<string,unknown>).dispatchMode).trim() : '';
  if (!dmRaw) errors.push('dispatch_mode is required.');
  else if (!ALLOWED.includes(dmRaw as DispatchMode)) errors.push(`dispatch_mode must be one of: ${ALLOWED.join(', ')}.`);
  if (dmRaw === 'insert') {
    const target = typeof rec.target_run_id === 'string' ? rec.target_run_id.trim() : typeof (rec as Record<string,unknown>).targetRunId === 'string' ? String((rec as Record<string,unknown>).targetRunId).trim() : '';
    if (!target) errors.push('target_run_id is required when dispatch_mode is insert.');
  }
  // A stale client still sending agent_mode fails here with the same shape Core
  // would produce, so the client learns the field is gone as early as possible.
  if (typeof rec.agent_mode === 'string' || typeof (rec as Record<string,unknown>).agentMode === 'string') {
    errors.push('agent_mode is not part of the interaction contract; send mode_version_id instead.');
  }
  if (errors.length) return { ok: false, errors };
  return { ok: true, value: rec as InteractionExternalRequest };
}

export function toCoreInteractionBody(ext: InteractionExternalRequest): Record<string, unknown> {
  return { ...ext };
}
