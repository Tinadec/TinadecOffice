/**
 * Typed fetch wrapper aligned to the Gateway external contract (snake_case).
 * DTOs are type aliases into src/generated/schema.d.ts, which is generated from
 * the Gateway external OpenAPI snapshot (TinadecGateway/tests/__snapshots__/openapi.external.json).
 * Regenerate both with: npm run generate:client (drift gate: npm run check:drift).
 * SSE transport types (SseChunk/InvokeStreamRequest) stay local: SSE frames are
 * not part of the JSON OpenAPI surface.
 */
import type { components } from './schema'

type Schemas = components['schemas']

export type RunStatus =
  | 'planning'
  | 'understanding'
  | 'executing'
  | 'replanning'
  | 'awaiting_approval'
  | 'paused'
  | 'reviewing'
  | 'completed'
  | 'failed'
  | 'cancelled'
export const RUN_STATUSES: readonly RunStatus[] = [
  'planning','understanding','executing','replanning','awaiting_approval','paused','reviewing','completed','failed','cancelled',
] as const

export type SseKind = 'ack'|'delta'|'done'|'error'|'heartbeat'|'task_node_update'|'supervision_update'|'context_version_update'|'queued'|'assigned'|'steering'|'context_conflict'|'model_selection'|'ephemeral_agent'|'control'
export const SSE_KINDS: ReadonlySet<string> = new Set(['ack','delta','done','error','heartbeat','task_node_update','supervision_update','context_version_update','queued','assigned','steering','context_conflict','model_selection','ephemeral_agent','control'])

export interface SseChunk {
  run_id: string
  turn_id: string | null
  message_id: string | null
  seq: number
  kind: SseKind | string
  occurred_at: string
  payload: Record<string, unknown>
}

export interface InvokeStreamRequest {
  content: string
  client_message_id: string
  application_mode: string
  agent_mode: string
  permission_mode: string
  target_run_id?: string | null
  expected_context_revision?: number | null
}

export type ProjectDto = Schemas['Project']
export type SessionDto = Schemas['Session']
export type MessageDto = Schemas['Message']
export type RunDto = Schemas['Run']
export type TaskNodeDto = Schemas['TaskNode']
export type SupervisionFindingDto = Schemas['SupervisionFinding']
export type ContextVersionDto = Schemas['ContextVersion']
export type OrchestrationSnapshotDto = Schemas['OrchestrationSnapshot']

export type LifecycleStatus = ProjectDto['lifecycle_status']

// Request-side envelope stays structurally loose: the App builds it from its
// bundled manifest literal, so a strict generated component type would fight
// the local JSON shape. Responses use the schema aliases below.
export interface AgentPackEnvelopeDto {
  manifest: unknown
  integrity: {
    algorithm: 'sha256' | string
    digest: string
  }
}

export type AgentPackPreviewAction = AgentPackInstallPreviewDto['action']
export type AgentPackResourceCountsDto = Schemas['AgentPackCounts']
export type AgentPackResourceBindingDto = Schemas['AgentPackResourceBinding']
export type AgentPackDto = Schemas['AgentPackInstallation']
export type AgentPackDetailDto = Schemas['AgentPackInstallationDetail']
export type AgentPackInstallPreviewDto = Schemas['AgentPackInstallPreview']
export type AgentPackInstallResultDto = Schemas['AgentPackApplyResult']

function gatewayUrl(): string {
  const w = window as unknown as { tinadec?: { gatewayUrl?: () => string } }
  return w.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
}

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const url = `${gatewayUrl()}${path}`
  let res: Response
  try {
    res = await fetch(url, { ...init, headers: { accept: 'application/json', ...(init?.body ? { 'content-type': 'application/json' } : {}), ...(init?.headers ?? {}) } })
  } catch (e) {
    throw new Error(`Cannot connect to backend (${gatewayUrl()}): ${e instanceof Error ? e.message : String(e)}`)
  }
  const text = await res.text()
  let data: unknown = null
  if (text) { try { data = JSON.parse(text) } catch { throw new Error(`Invalid JSON: ${text.slice(0,200)}`) } }
  if (!res.ok) {
    const rec = data as Record<string, unknown> | null
    const msg = rec?.message ?? (rec?.error as Record<string, unknown> | null)?.message ?? res.statusText
    throw new Error(typeof msg === 'string' && msg ? msg : String(msg ?? res.statusText))
  }
  return data as T
}

async function reqWithEtag<T>(path: string, init?: RequestInit): Promise<T & { etag: string | null }> {
  const url = `${gatewayUrl()}${path}`
  let res: Response
  try {
    res = await fetch(url, { ...init, headers: { accept: 'application/json', ...(init?.body ? { 'content-type': 'application/json' } : {}), ...(init?.headers ?? {}) } })
  } catch (e) {
    throw new Error(`Cannot connect to backend (${gatewayUrl()}): ${e instanceof Error ? e.message : String(e)}`)
  }
  const text = await res.text()
  let data: unknown = null
  if (text) { try { data = JSON.parse(text) } catch { throw new Error(`Invalid JSON: ${text.slice(0,200)}`) } }
  if (!res.ok) {
    const rec = data as Record<string, unknown> | null
    const msg = rec?.message ?? (rec?.error as Record<string, unknown> | null)?.message ?? res.statusText
    throw new Error(typeof msg === 'string' && msg ? msg : String(msg ?? res.statusText))
  }
  const result = data as T
  // The ETag travels in the response header, not the JSON body, so it is merged
  // into the returned value instead of being a schema component.
  const etag = res.headers.get('etag')
  return (etag ? { ...result, etag } : result) as T & { etag: string | null }
}

export const generatedApi = {
  gatewayUrl,
  listProjects: (lifecycleStatus?: LifecycleStatus) => req<ProjectDto[]>(`/api/v1/projects${lifecycleStatus ? `?lifecycle_status=${encodeURIComponent(lifecycleStatus)}` : ''}`),
  createProject: (name: string, path: string) => req<ProjectDto>('/api/v1/projects', { method: 'POST', body: JSON.stringify({ name, path }) }),
  renameProject: (projectId: string, name: string) => req<ProjectDto>(`/api/v1/projects/${encodeURIComponent(projectId)}`, { method: 'PATCH', body: JSON.stringify({ name }) }),
  archiveProject: (projectId: string) => req<void>(`/api/v1/projects/${encodeURIComponent(projectId)}/archive`, { method: 'POST' }),
  trashProject: (projectId: string) => req<void>(`/api/v1/projects/${encodeURIComponent(projectId)}/trash`, { method: 'POST' }),
  restoreProject: (projectId: string) => req<void>(`/api/v1/projects/${encodeURIComponent(projectId)}/restore`, { method: 'POST' }),
  purgeProject: (projectId: string) => req<void>(`/api/v1/projects/${encodeURIComponent(projectId)}`, { method: 'DELETE' }),
  listSessions: (projectId?: string, lifecycleStatus?: LifecycleStatus) => {
    const params = new URLSearchParams()
    if (projectId) params.set('project_id', projectId)
    if (lifecycleStatus) params.set('lifecycle_status', lifecycleStatus)
    const suffix = params.toString() ? `?${params.toString()}` : ''
    return req<SessionDto[]>(`/api/v1/sessions${suffix}`)
  },
  createSession: (projectId: string, title?: string) => req<SessionDto>('/api/v1/sessions', { method: 'POST', body: JSON.stringify({ project_id: projectId, title }) }),
  archiveSession: (sessionId: string) => req<void>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/archive`, { method: 'POST' }),
  trashSession: (sessionId: string) => req<void>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/trash`, { method: 'POST' }),
  restoreSession: (sessionId: string) => req<void>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/restore`, { method: 'POST' }),
  purgeSession: (sessionId: string) => req<void>(`/api/v1/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' }),
  listMessages: (sessionId: string) => req<MessageDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/messages`),
  listRuns: (sessionId: string) => req<RunDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/runs`),
  getOrchestration: (sessionId: string) => req<OrchestrationSnapshotDto>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/orchestration`),
  getRunOrchestration: (runId: string) => req<OrchestrationSnapshotDto>(`/api/v1/runs/${encodeURIComponent(runId)}/orchestration`),
  listTaskNodes: (sessionId: string) => req<TaskNodeDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/task-nodes`),
  listSupervisionFindings: (sessionId: string) => req<SupervisionFindingDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/supervision-findings`),
  listContextVersions: (sessionId: string, runId?: string) => {
    const p = new URLSearchParams(); if (runId) p.set('run_id', runId)
    const s = p.toString() ? `?${p.toString()}` : ''
    return req<ContextVersionDto[]>(`/api/v1/sessions/${encodeURIComponent(sessionId)}/context-versions${s}`)
  },
  listAgentPacks: () => req<AgentPackDto[]>('/api/v1/agent-packs'),
  getAgentPack: (packId: string) => reqWithEtag<AgentPackDetailDto>(`/api/v1/agent-packs/${encodeURIComponent(packId)}`),
  previewAgentPackInstall: (envelope: AgentPackEnvelopeDto) => reqWithEtag<AgentPackInstallPreviewDto>('/api/v1/agent-packs/install-preview', {
    method: 'POST',
    body: JSON.stringify(envelope),
  }),
  installAgentPack: (
    packId: string,
    input: { preview_id: string; envelope: AgentPackEnvelopeDto },
    options: { if_match?: string | null; idempotency_key: string },
  ) => reqWithEtag<AgentPackInstallResultDto>(`/api/v1/agent-packs/${encodeURIComponent(packId)}`, {
    method: 'PUT',
    headers: {
      ...(options.if_match ? { 'if-match': options.if_match } : {}),
      'idempotency-key': options.idempotency_key,
    },
    body: JSON.stringify(input),
  }),
  controlRun: (runId: string, body: Record<string, unknown>) => req<unknown>(`/api/v1/runs/${encodeURIComponent(runId)}/control`, { method: 'POST', body: JSON.stringify(body) }),
  health: () => req<Record<string, unknown>>('/api/v1/health'),
}
