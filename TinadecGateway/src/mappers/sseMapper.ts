/** SSE event mapper: ensures Core chunk -> External chunk with fixed fields, id=seq */
export const SSE_KINDS = new Set(['ack','delta','done','error','heartbeat','task_node_update','supervision_update','context_version_update']);

export interface ExternalSseChunk {
  run_id: string;
  turn_id: string | null;
  message_id: string | null;
  seq: number;
  kind: string;
  occurred_at: string;
  payload: Record<string, unknown>;
}

// Core chunk shape from FullDuplexRunCoordinator follow: run_id, turn_id, message_id, seq, kind, delta, usage, finish_reason, error_category, safe_error_message
export function mapSseChunk(coreChunk: Record<string, unknown>): ExternalSseChunk | null {
  const runId = (coreChunk.run_id as string) ?? (coreChunk.runId as string);
  const seqRaw = coreChunk.seq ?? coreChunk.sequence;
  if (!runId || seqRaw == null) return null;
  const seq = Number(seqRaw);
  const kindRaw = String(coreChunk.kind ?? 'delta').toLowerCase();
  const kind = SSE_KINDS.has(kindRaw) ? kindRaw : kindRaw;
  const turnId = (coreChunk.turn_id as string) ?? (coreChunk.turnId as string) ?? null;
  const messageId = (coreChunk.message_id as string) ?? (coreChunk.messageId as string) ?? null;
  const occurredAt = (coreChunk.occurred_at as string) ?? (coreChunk.occurredAt as string) ?? new Date().toISOString();
  const payload: Record<string, unknown> = {};
  if (coreChunk.delta != null) payload.delta = coreChunk.delta;
  if (coreChunk.usage != null) payload.usage = coreChunk.usage;
  if (coreChunk.finish_reason != null) payload.finish_reason = coreChunk.finish_reason;
  if (coreChunk.error_category != null) payload.error_category = coreChunk.error_category;
  if (coreChunk.safe_error_message != null) payload.safe_error_message = coreChunk.safe_error_message;
  // forward any other known fields
  for (const k of ['purpose','error','message','data']) {
    if (coreChunk[k] != null) payload[k] = coreChunk[k];
  }
  return {
    run_id: String(runId),
    turn_id: turnId ? String(turnId) : null,
    message_id: messageId ? String(messageId) : null,
    seq,
    kind,
    occurred_at: occurredAt,
    payload,
  };
}

export function formatSseEvent(chunk: ExternalSseChunk): string {
  // id=seq, event=kind, data=json, retain order id/event/data
  const data = JSON.stringify(chunk);
  return `id: ${chunk.seq}\nevent: ${chunk.kind}\ndata: ${data}\n\n`;
}

export function formatHeartbeat(seq: number): string {
  const chunk: ExternalSseChunk = {
    run_id: 'heartbeat',
    turn_id: null,
    message_id: null,
    seq,
    kind: 'heartbeat',
    occurred_at: new Date().toISOString(),
    payload: {},
  };
  return formatSseEvent(chunk);
}
