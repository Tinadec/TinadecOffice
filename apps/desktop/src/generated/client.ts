/**
 * Minimal typed fetch wrapper aligned to Gateway external DTO (snake_case).
 * Placeholder for openapi-typescript + openapi-fetch generation.
 * When Gateway is live, replace with:
 *   npx openapi-typescript http://127.0.0.1:48730/docs/json -o src/generated/schema.d.ts
 * and an openapi-fetch client over that schema. This file keeps the checked-in
 * src/generated/ contract and avoids adding a build-time network dependency.
 * drift: npm run generate:client && git diff --exit-code
 */
// ponytail: hand-written minimal wrapper; swap to openapi-fetch when external OpenAPI is live
// drift: npm run generate:client && git diff --exit-code
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

export interface ProjectDto { id: string; name: string; path: string; created_at: string }
export interface SessionDto { id: string; project_id: string; title: string; status: string; mode_version_id?: string | null; meeting_model?: string | null; meeting_provider_id?: string | null; created_at: string; updated_at: string }
export interface MessageDto { id: string; session_id: string; role: string; content: string; created_at: string }
export interface RunDto { id: string; session_id: string; trigger_message_id: string | null; status: RunStatus | string; summary: string | null; task_revision?: number | null; created_at: string | null; updated_at: string | null }
export interface TaskNodeDto { id: string; graph_id: string | null; run_id: string; session_id: string; title: string; description: string; status: string; priority: number; risk: string; success_criteria: string[]; dependencies: string[]; required_capabilities: string[]; created_at: string | null; updated_at: string | null }
export interface SupervisionFindingDto { id: string; run_id: string; session_id: string; severity: string; category: string; summary: string; recommendation: string; status: string; created_at: string }
export interface ContextVersionDto { id: string; session_id: string; run_id: string | null; revision: number; kind: string; status: string; base_revision: number | null; created_at: string | null }
export interface OrchestrationSnapshotDto {
  run: RunDto | null
  graph: { id: string; title: string } | null
  nodes: TaskNodeDto[]
  assignments: Array<{ id: string; run_id: string; task_node_id: string; agent_id: string; agent_name: string; agent_layer: string; status: string }>
  step_results: unknown[]
  context_packs: unknown[]
  supervision_findings: SupervisionFindingDto[]
  agent_instances?: unknown[]
}

export interface AgentPackEnvelopeDto {
  manifest: unknown
  integrity: {
    algorithm: 'sha256' | string
    digest: string
  }
}

export type AgentPackPreviewAction = 'install' | 'upgrade' | 'up_to_date' | 'newer_installed' | 'conflict' | string

export interface AgentPackResourceCountsDto {
  agents: number
  prompt_pipelines: number
  modes: number
  created?: number
  adopted?: number
  reused?: number
  updated?: number
}

export interface AgentPackResourceBindingDto {
  kind: 'agent' | 'prompt_pipeline' | 'mode' | string
  resource_key: string
  logical_entity_id: string | null
  version_id: string | null
  content_hash: string | null
  disposition: 'created' | 'adopted' | 'reused' | 'updated' | 'conflict' | string
}

export interface AgentPackDto {
  pack_id: string
  owner: string
  product_id?: string | null
  name?: string | null
  status: string
  active_version: string | null
  integrity_digest: string | null
  revision: number
  installed_at: string
  updated_at: string
  etag?: string | null
}

export interface AgentPackDetailDto extends AgentPackDto {
  versions: Array<Record<string, unknown>>
  resources: AgentPackResourceBindingDto[]
}

export interface AgentPackInstallPreviewDto {
  preview_id: string | null
  pack_id: string
  owner: string
  action: AgentPackPreviewAction
  bundled_version: string
  installed_version: string | null
  integrity_digest: string
  revision: number
  etag?: string | null
  expires_at: string | null
  counts: AgentPackResourceCountsDto
  resources?: AgentPackResourceBindingDto[]
  defaults_will_adopt?: boolean
  required_core_version: string | null
  current_core_version: string
  differences?: string[]
  warnings: string[]
}

export interface AgentPackInstallResultDto {
  status: 'installed' | 'updated' | 'up_to_date' | 'newer_installed' | string
  pack_id: string
  owner: string
  active_version: string
  integrity_digest: string
  revision: number
  counts: AgentPackResourceCountsDto
  resources?: AgentPackResourceBindingDto[]
  defaults_adopted?: boolean
  warnings?: string[]
  installed_at: string
  updated_at: string
  etag?: string | null
}

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

async function reqWithEtag<T extends { etag?: string | null }>(path: string, init?: RequestInit): Promise<T> {
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
  const etag = res.headers.get('etag')
  return etag ? { ...result, etag } : result
}

export const generatedApi = {
  gatewayUrl,
  listProjects: () => req<ProjectDto[]>('/api/v1/projects'),
  createProject: (name: string, path: string) => req<ProjectDto>('/api/v1/projects', { method: 'POST', body: JSON.stringify({ name, path }) }),
  listSessions: (projectId?: string) => req<SessionDto[]>(`/api/v1/sessions${projectId ? `?project_id=${encodeURIComponent(projectId)}` : ''}`),
  createSession: (projectId: string, title?: string) => req<SessionDto>('/api/v1/sessions', { method: 'POST', body: JSON.stringify({ project_id: projectId, title }) }),
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
