/**
 * TinadecGateway — 独立 Bun 包，薄代理 BFF/API 层。
 * 外部契约：全量 snake_case + RFC9457 ProblemDetails + 薄代理透传
 */

import { Elysia, t } from 'elysia';
import { swagger } from '@elysiajs/swagger';
import { getConfig } from './config.js';
import { coreUrl, proxyJson, proxySse } from './coreClient.js';
import { proxyToolRuntimeJson, toolRuntimeUrl } from './toolRuntimeClient.js';
import {
  authenticate,
  isPublicPath,
} from './auth.js';
import {
  codeToolApprovalBlockFor,
  codeToolApprovalUnavailableBlock,
  codeToolRequiresApproval,
  executeCodeToolViaRuntime,
  listCodeToolIds,
  listCodeToolSpecs,
  type ApprovalSnapshot,
  type CodeToolExecuteRequest,
} from './codeTools.js';
import { mcpRoutes } from './mcp/mcpRoutes.js';
import { findWsRoute, buildTargetWsUrl } from './websocket.js';
import { proxyStream, setStreamHeaders } from './streaming.js';
import { ensureRequestId, PRINCIPAL_VALUE } from './headers.js';
import { validateInteractionBody } from './mappers/interactionsMapper.js';
import { mapProjects } from './mappers/projectMapper.js';
import { mapSessions } from './mappers/sessionMapper.js';
import { mapMessages } from './mappers/messageMapper.js';
import { mapRuns } from './mappers/runMapper.js';
import { mapHealth } from './mappers/healthMapper.js';
import { mapReadiness, mapModelReadiness } from './mappers/readinessMapper.js';
import { mapOrchestration } from './mappers/orchestrationMapper.js';
import { mapTaskNodes } from './mappers/taskNodeMapper.js';
import { mapContextVersions } from './mappers/contextVersionMapper.js';
import { validateInvokeStreamBody, toCoreInvokeStreamBody } from './mappers/invokeStreamMapper.js';
import { mapCoreErrorToExternal, toProblemDetails } from './mappers/errorMapper.js';

const config = getConfig();

function setStatus(set: { status?: number | string }, status: number) {
  set.status = status;
}

function corsHeadersFor(origin: string | null): Record<string, string> {
  const headers: Record<string, string> = {};
  if (origin && isOriginAllowed(origin)) {
    headers['access-control-allow-origin'] = origin;
    headers['access-control-allow-credentials'] = 'true';
    headers['vary'] = 'Origin';
  }
  return headers;
}

const ALLOWED_ORIGINS: (string | RegExp)[] = [
  /^http:\/\/127\.0\.0\.1:\d+$/,
  /^http:\/\/localhost:\d+$/,
  'file://',
  'tauri://localhost',
  'https://tauri.localhost',
  ...config.corsExtraOrigins.map((origin) => {
    if (origin.startsWith('*.')) {
      const suffix = origin.slice(1);
      return new RegExp(`https?://[^/]*${suffix.replace(/\./g, '\\.')}$`);
    }
    return origin;
  }),
];

function isOriginAllowed(origin: string): boolean {
  return ALLOWED_ORIGINS.some((pattern) => {
    if (typeof pattern === 'string') return pattern === origin;
    return pattern.test(origin);
  });
}

function forwardHeaders(request: Request): Record<string, string> {
  const requestId = ensureRequestId(request.headers.get('x-request-id') ?? request.headers.get('X-Request-Id'));
  const headers: Record<string, string> = {
    'x-request-id': requestId,
    'x-tinadec-principal': PRINCIPAL_VALUE,
  };
  const ifMatch = request.headers.get('if-match') ?? request.headers.get('If-Match');
  if (ifMatch) headers['if-match'] = ifMatch;
  return headers;
}

function setProxyResponseHeaders(set: { headers: Record<string, string | number> }, requestId: string) {
  set.headers['x-request-id'] = requestId;
  set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
}

async function verifyCodeToolApproval(toolId: string, request: CodeToolExecuteRequest) {
  const params = new URLSearchParams();
  if (request.session_id) params.set('sessionId', request.session_id);
  const suffix = params.toString() ? `?${params.toString()}` : '';
  try {
    const approvalResult = await proxyJson(`/api/v1/approvals${suffix}`);
    if (approvalResult.status < 200 || approvalResult.status >= 300 || !Array.isArray(approvalResult.data)) {
      return codeToolApprovalUnavailableBlock(toolId, request);
    }
    return codeToolApprovalBlockFor(toolId, request, approvalResult.data as ApprovalSnapshot[]);
  } catch {
    return codeToolApprovalUnavailableBlock(toolId, request);
  }
}

const app = new Elysia()
  .use(swagger({
    path: '/docs',
    documentation: {
      info: { title: 'Tinadec Gateway External API', version: '0.2.0', description: 'Thin proxy BFF to Core – all snake_case, RFC9457 ProblemDetails, full-duplex SSE' },
      tags: [
        { name: 'Projects' }, { name: 'Sessions' }, { name: 'Messages' }, { name: 'Runs' }, { name: 'Health' }, { name: 'ModelCenter' }, { name: 'AgentCenter' }, { name: 'Agents' }, { name: 'Interactions' }, { name: 'PromptPipelines' }, { name: 'Tools' }, { name: 'System' }
      ]
    }
  }))
  .onError(({ code, error, set, request }) => {
    // ponytail: minimal RFC9457 catch-all — reuse toProblemDetails/CODE_MAP, keep X-Request-Id principal
    const rid = ensureRequestId(request.headers.get('x-request-id') ?? request.headers.get('X-Request-Id'));
    set.headers['x-request-id'] = rid;
    set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
    const path = new URL(request.url).pathname;
    if (code === 'NOT_FOUND') {
      set.status = 404;
      set.headers['content-type'] = 'application/problem+json';
      return toProblemDetails(404, 'NOT_FOUND', `Route ${path} not found.`, path, rid);
    }
    if (code === 'VALIDATION') {
      set.status = 400;
      set.headers['content-type'] = 'application/problem+json';
      const detail = (error as Error)?.message ?? 'Invalid request.';
      return toProblemDetails(400, 'invalid_request', detail, path, rid);
    }
    if (code === 'INTERNAL_SERVER_ERROR' || code === 'UNKNOWN' || (code as string) === 'ERROR') {
      const status = typeof set.status === 'number' ? set.status : 500;
      set.headers['content-type'] = 'application/problem+json';
      return toProblemDetails(status, 'conflict', (error as Error)?.message ?? 'Internal error.', path, rid);
    }
  })
  .onRequest(({ request, set }) => {
    const origin = request.headers.get('origin');
    const corsHeaders = corsHeadersFor(origin);
    if (request.method === 'OPTIONS') {
      const requestMethod = request.headers.get('access-control-request-method');
      const requestHeaders = request.headers.get('access-control-request-headers');
      if (requestMethod) corsHeaders['access-control-allow-methods'] = requestMethod;
      if (requestHeaders) corsHeaders['access-control-allow-headers'] = requestHeaders;
      else corsHeaders['access-control-allow-headers'] = 'accept, content-type, authorization, x-api-key, x-tenant-id, x-user-id, x-request-id, x-tinadec-principal, last-event-id';
      corsHeaders['access-control-max-age'] = '86400';
      set.headers = { ...set.headers, ...corsHeaders };
      set.status = 204;
      return '';
    }
    Object.assign(set.headers, corsHeaders);
    // ensure correlation id on every response
    const rid = ensureRequestId(request.headers.get('x-request-id') ?? request.headers.get('X-Request-Id'));
    set.headers['x-request-id'] = rid;
    set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
  })
  .onRequest(async ({ request, set }) => {
    if (config.mode === 'local') return;
    const path = new URL(request.url).pathname;
    if (isPublicPath(path)) return;
    const authResult = await authenticate(request.headers, config.auth);
    if (!authResult.ok) {
      setStatus(set, 401);
      set.headers['content-type'] = 'application/problem+json';
      return toProblemDetails(401, authResult.error?.code ?? 'forbidden', authResult.error?.message ?? 'Authentication failed.', path);
    }
  })
  .use(mcpRoutes)
  .get('/api/v1/health', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/health', { headers });
    setStatus(set, result.status);
    set.headers['content-type'] = result.status >= 400 ? 'application/problem+json' : 'application/json';
    if (result.status >= 400) {
      const rid = (headers as Record<string,string>)['x-request-id'];
      setProxyResponseHeaders(set as never, rid);
      return mapCoreErrorToExternal(result.status, result.data, '/api/v1/health');
    }
    const core = (result.data && typeof result.data === 'object' ? result.data : {}) as Record<string, unknown>;
    const mapped = mapHealth(core, { gateway: 'ok', mode: config.mode, core_url: coreUrl(), tool_runtime_url: toolRuntimeUrl() });
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapped;
  }, { detail: { summary: 'Health probe', tags: ['Health'] } })
  .get('/api/v1/doctor', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/doctor', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/doctor'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Doctor checks', tags: ['Health'] } })
  .get('/api/v1/readiness', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/readiness', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/readiness'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapReadiness(result.data);
  }, { detail: { summary: 'Readiness probe', tags: ['Health'] } })
  .get('/api/v1/model-readiness', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-readiness', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-readiness'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapModelReadiness(result.data);
  }, { detail: { summary: 'Model readiness', tags: ['Health'] } })
  .get('/api/v1/model-catalog-readiness', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-catalog-readiness', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-catalog-readiness'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapModelReadiness(result.data);
  }, { detail: { summary: 'Model catalog readiness', tags: ['Health'] } })
  .get('/api/v1/tool-layer-readiness', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/tool-layer-readiness', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/tool-layer-readiness'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapReadiness(result.data);
  }, { detail: { summary: 'Tool layer readiness', tags: ['Health'] } })
  .get('/api/v1/projects', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/projects', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/projects'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapProjects(result.data);
  }, { detail: { summary: 'List projects', tags: ['Projects'] } })
  .post('/api/v1/projects', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/projects', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/projects'); }
    const mapped = mapProjects([result.data]);
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapped[0] ?? result.data;
  }, { detail: { summary: 'Create project', tags: ['Projects'] }, body: t.Object({ name: t.String(), path: t.String() }, { additionalProperties: true }) })
  .get('/api/v1/sessions', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    if ((query as Record<string,unknown>).project_id) params.set('projectId', String((query as Record<string,unknown>).project_id));
    const result = await proxyJson(`/api/v1/sessions?${params.toString()}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/sessions'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapSessions(result.data);
  }, { detail: { summary: 'List sessions', tags: ['Sessions'] } })
  .post('/api/v1/sessions', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/sessions', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/sessions'); }
    const mapped = mapSessions([result.data]);
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapped[0] ?? result.data;
  }, { detail: { summary: 'Create session', tags: ['Sessions'] }, body: t.Object({ project_id: t.String(), title: t.Optional(t.String()) }, { additionalProperties: true }) })
  .patch('/api/v1/sessions/:sessionId', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}`, { method: 'PATCH', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update session title', tags: ['Sessions'] } })
  .get('/api/v1/sessions/:sessionId/messages', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/messages`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/messages`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapMessages(result.data);
  }, { detail: { summary: 'List messages', tags: ['Messages'] } })
  .post('/api/v1/sessions/:sessionId/messages', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/messages`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/messages`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create message (compat)', tags: ['Messages'] }, body: t.Object({ content: t.String() }) })
  .post('/api/v1/sessions/:sessionId/invoke-stream', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const validation = validateInvokeStreamBody(body);
    if (!validation.ok) {
      setStatus(set, 400);
      set.headers['content-type'] = 'application/problem+json';
      setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
      return toProblemDetails(400, 'invalid_request', validation.errors.join('; '), `/api/v1/sessions/${params.sessionId}/invoke-stream`, (headers as Record<string,string>)['x-request-id']);
    }
    const coreBody = toCoreInvokeStreamBody(validation.value);
    const response = await proxySse(`/api/v1/sessions/${params.sessionId}/invoke-stream`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', ...headers },
      body: JSON.stringify(coreBody)
    });
    if (response.status >= 400) {
      const text = await response.text();
      let data: unknown = null;
      try { data = JSON.parse(text); } catch { data = { message: text }; }
      setStatus(set, response.status);
      set.headers['content-type'] = 'application/problem+json';
      setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
      return mapCoreErrorToExternal(response.status, data, `/api/v1/sessions/${params.sessionId}/invoke-stream`);
    }
    setStatus(set, response.status);
    set.headers['content-type'] = response.headers.get('content-type') ?? 'text/event-stream';
    set.headers['cache-control'] = 'no-cache';
    set.headers['connection'] = 'keep-alive';
    set.headers['x-accel-buffering'] = 'no';
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return response.body;
  }, { detail: { summary: 'Full-duplex invoke-stream', tags: ['Runs'], description: '5 required: content, client_message_id, application_mode, agent_mode, permission_mode + 2 optional: target_run_id, expected_context_revision. SSE kinds: ack/delta/done/error/heartbeat/task_node_update/supervision_update/context_version_update, fixed fields run_id/turn_id/message_id/seq/kind/occurred_at/payload, id=seq' } })
  .get('/api/v1/sessions/:sessionId/orchestration', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/orchestration`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/orchestration`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapOrchestration(result.data);
  }, { detail: { summary: 'Session orchestration snapshot', tags: ['Runs'] } })
  .get('/api/v1/runs/:runId/orchestration', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/runs/${params.runId}/orchestration`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/runs/${params.runId}/orchestration`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapOrchestration(result.data);
  }, { detail: { summary: 'Run orchestration', tags: ['Runs'] } })
  .get('/api/v1/runs/:runId/agent-lineage', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/runs/${params.runId}/agent-lineage`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/runs/${params.runId}/agent-lineage`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Run agent lineage', tags: ['Runs'] } })
  .get('/api/v1/runs/:runId/stream', async ({ params, query, set, request }) => {
    const headers = forwardHeaders(request);
    const url = new URL(request.url);
    const cursorFromQuery = (query as Record<string,unknown>).cursor ? String((query as Record<string,unknown>).cursor) : url.searchParams.get('cursor');
    const afterSeq = (query as Record<string,unknown>).after_seq ? String((query as Record<string,unknown>).after_seq) : url.searchParams.get('after_seq');
    const headerCursor = request.headers.get('last-event-id') ?? request.headers.get('Last-Event-ID');
    const cursor = headerCursor ?? cursorFromQuery ?? afterSeq ?? null;
    const search = new URLSearchParams();
    const turnId = (query as Record<string,unknown>).turn_id ?? (query as Record<string,unknown>).turnId;
    if (turnId) search.set('turn_id', String(turnId));
    if (cursor) search.set('after_seq', String(cursor));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    const responseHeaders: Record<string,string> = { ...headers };
    if (cursor) responseHeaders['last-event-id'] = String(cursor);
    const response = await proxySse(`/api/v1/runs/${params.runId}/stream${suffix}`, { headers: responseHeaders });
    if (response.status >= 400) {
      const text = await response.text();
      let data: unknown = null; try { data = JSON.parse(text); } catch { data = { message: text }; }
      setStatus(set, response.status);
      set.headers['content-type'] = 'application/problem+json';
      setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
      return mapCoreErrorToExternal(response.status, data, `/api/v1/runs/${params.runId}/stream`);
    }
    setStatus(set, response.status);
    set.headers['content-type'] = response.headers.get('content-type') ?? 'text/event-stream';
    set.headers['cache-control'] = 'no-cache';
    set.headers['connection'] = 'keep-alive';
    set.headers['x-accel-buffering'] = 'no';
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return response.body;
  }, { detail: { summary: 'Run stream (SSE)', tags: ['Runs'], description: 'Durable SSE with id=seq, Last-Event-ID / ?cursor= & ?after_seq resume, kinds: ack/delta/done/error/heartbeat/task_node_update/supervision_update/context_version_update' } })
  .get('/api/v1/runs/:runId/task-nodes', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    // Core stores task nodes per session; runId alone insufficient. Try run-scoped first, fallback to session-derived via orchestration.
    const direct = await proxyJson(`/api/v1/runs/${params.runId}/task-nodes`, { headers });
    if (direct.status < 400) {
      setStatus(set, direct.status);
      setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
      return mapTaskNodes(direct.data);
    }
    // Fallback: derive session from run orchestration if Core has no direct route
    if (direct.status === 404) {
      const orch = await proxyJson(`/api/v1/runs/${params.runId}/orchestration`, { headers });
      if (orch.status >= 200 && orch.status < 300) {
        const orchData = orch.data as Record<string, unknown> | null;
        const run = orchData?.run as Record<string, unknown> | undefined;
        const sessionId = run?.session_id ?? run?.sessionId;
        if (sessionId) {
          const sessNodes = await proxyJson(`/api/v1/sessions/${String(sessionId)}/task-nodes`, { headers });
          setStatus(set, sessNodes.status);
          if (sessNodes.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(sessNodes.status, sessNodes.data, `/api/v1/runs/${params.runId}/task-nodes`); }
          const allRaw = mapTaskNodes(sessNodes.data);
          const all = Array.isArray(allRaw) ? allRaw as Array<{ run_id: string }> : [];
          const filtered = all.filter(n => n.run_id === params.runId);
          setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
          return filtered.length ? filtered : all;
        }
      }
    }
    setStatus(set, direct.status);
    set.headers['content-type'] = 'application/problem+json';
    return mapCoreErrorToExternal(direct.status, direct.data, `/api/v1/runs/${params.runId}/task-nodes`);
  }, { detail: { summary: 'Run task nodes', tags: ['Runs'] } })
  .get('/api/v1/runs/:runId/context-versions', async ({ params, query, set, request }) => {
    const headers = forwardHeaders(request);
    const direct = await proxyJson(`/api/v1/runs/${params.runId}/context-versions${(() => { const s = new URLSearchParams(); if ((query as Record<string,unknown>).limit) s.set('limit', String((query as Record<string,unknown>).limit)); return s.toString() ? `?${s.toString()}` : ''; })()}`, { headers });
    if (direct.status < 400) {
      setStatus(set, direct.status);
      setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
      return mapContextVersions(direct.data);
    }
    if (direct.status === 404) {
      const orch = await proxyJson(`/api/v1/runs/${params.runId}/orchestration`, { headers });
      if (orch.status >= 200 && orch.status < 300) {
        const orchData = orch.data as Record<string, unknown> | null;
        const run = orchData?.run as Record<string, unknown> | undefined;
        const sessionId = run?.session_id ?? run?.sessionId;
        if (sessionId) {
          const search = new URLSearchParams();
          search.set('run_id', params.runId);
          if ((query as Record<string,unknown>).limit) search.set('limit', String((query as Record<string,unknown>).limit));
          const sess = await proxyJson(`/api/v1/sessions/${String(sessionId)}/context-versions?${search.toString()}`, { headers });
          setStatus(set, sess.status);
          if (sess.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(sess.status, sess.data, `/api/v1/runs/${params.runId}/context-versions`); }
          setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
          return mapContextVersions(sess.data);
        }
      }
    }
    setStatus(set, direct.status);
    set.headers['content-type'] = 'application/problem+json';
    return mapCoreErrorToExternal(direct.status, direct.data, `/api/v1/runs/${params.runId}/context-versions`);
  }, { detail: { summary: 'Run context versions', tags: ['Runs'] } })
  .post('/api/v1/runs/:runId/control', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/runs/${params.runId}/control`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/runs/${params.runId}/control`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Run control (pause/resume/cancel)', tags: ['Runs'] }, body: t.Object({ command: t.Optional(t.String()), action: t.Optional(t.String()), expected_context_revision: t.Optional(t.Number()), expectedContextRevision: t.Optional(t.Number()), client_control_id: t.Optional(t.String()) }, { additionalProperties: true }) })
  .get('/api/v1/sessions/:sessionId/tool-executions', async ({ params, query, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URLSearchParams();
    if ((query as Record<string,unknown>).run_id) search.set('runId', String((query as Record<string,unknown>).run_id));
    if ((query as Record<string,unknown>).limit) search.set('limit', String((query as Record<string,unknown>).limit));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/tool-executions${suffix}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/tool-executions`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Session tool executions', tags: ['Runs'] } })
  .get('/api/v1/sessions/:sessionId/runs', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/runs`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/runs`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapRuns(result.data);
  }, { detail: { summary: 'List session runs', tags: ['Runs'] } })
  .get('/api/v1/sessions/:sessionId/task-nodes', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/task-nodes`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/task-nodes`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapTaskNodes(result.data);
  }, { detail: { summary: 'Session task nodes', tags: ['Runs'] } })
  .get('/api/v1/sessions/:sessionId/context-packs', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/context-packs`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/context-packs`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Session context packs', tags: ['Runs'] } })
  .get('/api/v1/sessions/:sessionId/context-versions', async ({ params, query, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URLSearchParams();
    if ((query as Record<string,unknown>).run_id) search.set('run_id', String((query as Record<string,unknown>).run_id));
    if ((query as Record<string,unknown>).limit) search.set('limit', String((query as Record<string,unknown>).limit));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/context-versions${suffix}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/context-versions`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return mapContextVersions(result.data);
  }, { detail: { summary: 'Session context versions', tags: ['Runs'] } })
  .get('/api/v1/sessions/:sessionId/supervision-findings', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/supervision-findings`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/supervision-findings`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Supervision findings', tags: ['Runs'] } })
  .get('/api/v1/events', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.session_id) params.set('sessionId', String(q.session_id));
    const cursor = (q.cursor as string) ?? (q.after_seq as string) ?? request.headers.get('last-event-id') ?? request.headers.get('Last-Event-ID');
    if (cursor) params.set('afterSeq', String(cursor));
    const response = await proxySse(`/api/v1/events?${params.toString()}`, { headers: { ...headers, ...(cursor ? { 'last-event-id': String(cursor) } : {}) } });
    set.headers['content-type'] = 'text/event-stream';
    set.headers['cache-control'] = 'no-cache';
    set.headers['x-accel-buffering'] = 'no';
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return response.body;
  }, { detail: { summary: 'Session events SSE', tags: ['System'], description: 'SSE kinds: ack/delta/done/error/heartbeat/task_node_update/supervision_update/context_version_update, id=seq, Last-Event-ID / ?cursor resume' } })
  .get('/api/v1/approvals', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.status) params.set('status', String(q.status));
    if (q.session_id) params.set('sessionId', String(q.session_id));
    const result = await proxyJson(`/api/v1/approvals?${params.toString()}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/approvals'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List approvals', tags: ['System'] } })
  .post('/api/v1/approvals', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/approvals', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/approvals'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create approval', tags: ['System'] } })
  .post('/api/v1/approvals/:approvalId/decision', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/approvals/${params.approvalId}/decision`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/approvals/${params.approvalId}/decision`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Decide approval', tags: ['System'] } })
  .get('/api/v1/memory-candidates', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URLSearchParams();
    for (const key of ['status', 'scope', 'kind', 'session_id', 'run_id', 'project_id', 'agent_profile_id', 'limit']) {
      const value = (query as Record<string,unknown>)[key];
      if (value !== undefined && value !== '') search.set(key, String(value));
    }
    const suffix = search.toString() ? `?${search.toString()}` : '';
    const result = await proxyJson(`/api/v1/memory-candidates${suffix}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/memory-candidates'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List memory candidates', tags: ['System'] } })
  .post('/api/v1/memory-candidates/:candidateId/promote', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/memory-candidates/${params.candidateId}/promote`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/memory-candidates/${params.candidateId}/promote`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Promote memory candidate', tags: ['System'] } })
  .post('/api/v1/memory-candidates/:candidateId/reject', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/memory-candidates/${params.candidateId}/reject`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/memory-candidates/${params.candidateId}/reject`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Reject memory candidate', tags: ['System'] } })
  .post('/api/v1/tools/shell', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/tools/shell', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/tools/shell'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Shell tool (blocked until implemented)', tags: ['Tools'] } })
  .post('/api/v1/runs/:runId/tools/:toolId/execute', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/runs/${params.runId}/tools/${params.toolId}/execute`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/runs/${params.runId}/tools/${params.toolId}/execute`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Run-scoped tool execution (Core-owned)', tags: ['Tools'] } })
  .get('/api/v1/code/tools', () => ({
    tool_ids: listCodeToolIds(),
    tools: listCodeToolSpecs()
  }), { detail: { summary: 'Code tool specs (legacy BFF)', tags: ['Tools'], description: 'Legacy: use /api/v1/tools/search proxied to Core. This endpoint preserves existing Desktop catalog.' } })
  .get('/api/v1/tools/search', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.query) params.set('query', String(q.query));
    if (q.domain) params.set('domain', String(q.domain));
    if (q.source) params.set('source', String(q.source));
    if (q.risk) params.set('risk', String(q.risk));
    if (q.limit) params.set('limit', String(q.limit));
    const suffix = params.toString() ? `?${params.toString()}` : '';
    const result = await proxyJson(`/api/v1/tools/search${suffix}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/tools/search'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Search tools (Core registry)', tags: ['Tools'] } })
  .get('/api/v1/tools', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/tools', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/tools'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List tools (Core registry)', tags: ['Tools'] } })
  .get('/api/v1/harness/manifest', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/harness/manifest', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/harness/manifest'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Harness manifest (Core-owned)', tags: ['System'] } })
  // Thin proxy: code tool execute no longer implements Gateway-side evaluateApproval; Core owns approval
  .post('/api/v1/code/tools/:toolId/execute', async ({ params, body, set, request }) => {
    const toolId = params.toolId;
    const req = (body ?? {}) as CodeToolExecuteRequest;
    const requiresApproval = codeToolRequiresApproval(toolId);
    if (requiresApproval === null) {
      setStatus(set, 404);
      set.headers['content-type'] = 'application/problem+json';
      return toProblemDetails(404, 'run_not_found', 'Code tool was not found.', `/api/v1/code/tools/${toolId}/execute`);
    }
    if (requiresApproval && req.approval_id) {
      const block = await verifyCodeToolApproval(toolId, req);
      if (block) {
        // CodeTool approval block is already in external shape (status: blocked)
        return block;
      }
    }
    // No Gateway-side approval strategy – just proxy to Tool Runtime via Core-owned path if possible
    // Keep Tool Runtime path for backwards compat; Core will still enforce approval gate.
    const result = await executeCodeToolViaRuntime(toolId, req as unknown as Record<string, unknown> as never);
    if (!result) {
      setStatus(set, 404);
      set.headers['content-type'] = 'application/problem+json';
      return toProblemDetails(404, 'run_not_found', 'Code tool was not found.', `/api/v1/code/tools/${toolId}/execute`);
    }
    return result;
  }, { detail: { summary: 'Execute code tool (thin proxy, Core approval)', tags: ['Tools'], description: 'Legacy code tool path – approval is Core-owned; no Gateway-side strategy. Prefer /api/v1/runs/{runId}/tools/{toolId}/execute' } })
  .get('/api/v1/prompt-fragments', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.scope) params.set('scope', String(q.scope));
    if (q.target_agent_id) params.set('targetAgentId', String(q.target_agent_id));
    if (q.category) params.set('category', String(q.category));
    if (q.enabled !== undefined) params.set('enabled', String(q.enabled));
    const suffix = params.toString() ? `?${params.toString()}` : '';
    const result = await proxyJson(`/api/v1/prompt-fragments${suffix}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/prompt-fragments'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List prompt fragments', tags: ['System'] } })
  .post('/api/v1/prompt-fragments', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/prompt-fragments', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/prompt-fragments'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create prompt fragment', tags: ['System'] } })
  .put('/api/v1/prompt-fragments/:fragmentId', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}`, { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update prompt fragment', tags: ['System'] } })
  .delete('/api/v1/prompt-fragments/:fragmentId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}`, { method: 'DELETE', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Delete prompt fragment', tags: ['System'] } })
  .post('/api/v1/prompt-fragments/:fragmentId/clone', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/clone`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}/clone`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Clone prompt fragment', tags: ['System'] } })
  .post('/api/v1/prompt-context/preview', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/prompt-context/preview', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/prompt-context/preview'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Preview prompt context', tags: ['System'] } })
  .post('/api/v1/model-center/provider-instances/:providerInstanceId/models/refresh', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/model-providers/${params.providerInstanceId}/models/refresh`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/model-center/provider-instances/${params.providerInstanceId}/models/refresh`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Refresh provider models (model center)', tags: ['ModelCenter'] } })
  .get('/api/v1/model-provider-templates', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-provider-templates', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-provider-templates'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List provider templates (incl. protocol)', tags: ['ModelCenter'] } })
  .get('/api/v1/model-providers', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-providers', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-providers'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List model providers', tags: ['ModelCenter'] } })
  .get('/api/v1/model-providers/cli/discover', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-providers/cli/discover', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-providers/cli/discover'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Discover CLI runtimes', tags: ['ModelCenter'] } })
  .post('/api/v1/model-providers/cli/connect', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-providers/cli/connect', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-providers/cli/connect'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Connect a discovered CLI runtime as a provider', tags: ['ModelCenter'] } })
  .post('/api/v1/model-providers', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-providers', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-providers'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create model provider', tags: ['ModelCenter'] } })
  .put('/api/v1/model-providers/:providerInstanceId', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/model-providers/${params.providerInstanceId}`, { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/model-providers/${params.providerInstanceId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update model provider', tags: ['ModelCenter'] } })
  .delete('/api/v1/model-providers/:providerInstanceId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/model-providers/${params.providerInstanceId}`, { method: 'DELETE', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/model-providers/${params.providerInstanceId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Delete model provider', tags: ['ModelCenter'] } })
  .get('/api/v1/model-routes', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-routes', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-routes'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List model routes', tags: ['ModelCenter'] } })
  .put('/api/v1/model-routes/:purpose', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/model-routes/${params.purpose}`, { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/model-routes/${params.purpose}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Upsert model route', tags: ['ModelCenter'] } })
  .get('/api/v1/model-settings', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-settings', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-settings'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get model settings', tags: ['ModelCenter'] } })
  .put('/api/v1/model-settings', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/model-settings', { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/model-settings'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update model settings', tags: ['ModelCenter'] } })
  .get('/api/v1/market/sources', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/market/sources', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/market/sources'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List market sources', tags: ['System'] } })
  .post('/api/v1/market/sources', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/market/sources', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/market/sources'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create market source', tags: ['System'] } })
  .post('/api/v1/market/sources/:sourceId/refresh', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/market/sources/${params.sourceId}/refresh`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/market/sources/${params.sourceId}/refresh`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Refresh market source', tags: ['System'] } })
  .get('/api/v1/market/catalog', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.kind) params.set('kind', String(q.kind));
    if (q.query) params.set('query', String(q.query));
    if (q.source_id) params.set('sourceId', String(q.source_id));
    const result = await proxyJson(`/api/v1/market/catalog?${params.toString()}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/market/catalog'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get market catalog', tags: ['System'] } })
  .get('/api/v1/market/catalog/:catalogId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/market/catalog/${params.catalogId}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/market/catalog/${params.catalogId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get catalog item', tags: ['System'] } })
  .post('/api/v1/extensions/install-preview', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/extensions/install-preview', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/extensions/install-preview'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Preview extension install', tags: ['System'] } })
  .post('/api/v1/extensions/install', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/extensions/install', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/extensions/install'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Install extension', tags: ['System'] } })
  .get('/api/v1/extensions/installed', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/extensions/installed', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/extensions/installed'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List installed extensions', tags: ['System'] } })
  .post('/api/v1/extensions/:extensionId/enable', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/extensions/${params.extensionId}/enable`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/extensions/${params.extensionId}/enable`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Enable extension', tags: ['System'] } })
  .post('/api/v1/extensions/:extensionId/disable', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/extensions/${params.extensionId}/disable`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/extensions/${params.extensionId}/disable`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Disable extension', tags: ['System'] } })
  .post('/api/v1/extensions/:extensionId/update', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/extensions/${params.extensionId}/update`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/extensions/${params.extensionId}/update`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update extension', tags: ['System'] } })
  .delete('/api/v1/extensions/:extensionId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/extensions/${params.extensionId}`, { method: 'DELETE', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/extensions/${params.extensionId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Delete extension', tags: ['System'] } })
  .get('/api/v1/mcp/servers', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/mcp/servers', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/mcp/servers'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List MCP servers (Core-owned)', tags: ['System'] } })
  .get('/api/v1/mcp/servers/:serverId/tools', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/mcp/servers/${params.serverId}/tools`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/mcp/servers/${params.serverId}/tools`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List MCP server tools', tags: ['System'] } })
  .post('/api/v1/mcp/servers/:serverId/reload', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/mcp/servers/${params.serverId}/reload`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/mcp/servers/${params.serverId}/reload`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Reload MCP server', tags: ['System'] } })
  .get('/api/v1/acp/adapters', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/acp/adapters', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/acp/adapters'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List ACP adapters', tags: ['System'] } })
  .post('/api/v1/acp/adapters/:adapterId/probe', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/acp/adapters/${params.adapterId}/probe`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/acp/adapters/${params.adapterId}/probe`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Probe ACP adapter', tags: ['System'] } })
  .get('/api/v1/application-modes', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/application-modes', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/application-modes'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List application modes (TOML baseline)', tags: ['AgentCenter'] } })
  .get('/api/v1/agent-modes', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.application_mode) search.set('application_mode', String(q.application_mode));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    const result = await proxyJson(`/api/v1/agent-modes${suffix}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agent-modes'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List agent modes for application mode', tags: ['AgentCenter'] } })
  .get('/api/v1/agents', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/agents${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agents'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List agents', tags: ['Agents'] } })
  .put('/api/v1/agents/:agentId', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agents/${params.agentId}`, { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agents/${params.agentId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update agent', tags: ['AgentCenter'] } })
  .put('/api/v1/agents/:agentId/mode', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agents/${params.agentId}/mode`, { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agents/${params.agentId}/mode`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update agent mode (501)', tags: ['AgentCenter'] } })
  // --- Thin proxy: agents CRUD + draft/publish/archive/versions (snake_case passthrough) ---
  .post('/api/v1/agents', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/agents', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agents'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create agent', tags: ['Agents'] } })
  .get('/api/v1/agents/:agentId', async ({ params, set, request }) => {
    if ((params as Record<string,string>).agentId === 'catalog') {
      const headers = forwardHeaders(request);
      const result = await proxyJson('/api/v1/agents/catalog', { headers });
      setStatus(set, result.status);
      if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agents/catalog'); }
      setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
      return result.data;
    }
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/agents/${params.agentId}${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agents/${params.agentId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get agent', tags: ['Agents'] } })
  .put('/api/v1/agents/:agentId/draft', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agents/${params.agentId}/draft`, { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agents/${params.agentId}/draft`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update agent draft', tags: ['Agents'] } })
  .post('/api/v1/agents/:agentId/publish', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agents/${params.agentId}/publish`, { method: 'POST', body: body as Record<string, unknown> ?? {}, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agents/${params.agentId}/publish`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Publish agent', tags: ['Agents'] } })
  .post('/api/v1/agents/:agentId/archive', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agents/${params.agentId}/archive`, { method: 'POST', body: body as Record<string, unknown> ?? {}, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agents/${params.agentId}/archive`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Archive agent', tags: ['Agents'] } })
  .get('/api/v1/agents/:agentId/versions', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/agents/${params.agentId}/versions${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agents/${params.agentId}/versions`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List agent versions', tags: ['Agents'] } })
  .get('/api/v1/agents/:agentId/versions/:versionId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agents/${params.agentId}/versions/${params.versionId}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agents/${params.agentId}/versions/${params.versionId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get agent version', tags: ['Agents'] } })
  // --- agent-modes thin proxy ---
  .post('/api/v1/agent-modes', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/agent-modes', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agent-modes'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create agent mode', tags: ['AgentCenter'] } })
  .get('/api/v1/agent-modes/:id', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/agent-modes/${params.id}${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-modes/${params.id}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get agent mode', tags: ['AgentCenter'] } })
  .put('/api/v1/agent-modes/:id/draft', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agent-modes/${params.id}/draft`, { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-modes/${params.id}/draft`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update agent mode draft (nodes/edges/layout in body)', tags: ['AgentCenter'] } })
  .post('/api/v1/agent-modes/:id/publish', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agent-modes/${params.id}/publish`, { method: 'POST', body: body as Record<string, unknown> ?? {}, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-modes/${params.id}/publish`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Publish agent mode', tags: ['AgentCenter'] } })
  .post('/api/v1/agent-modes/:id/archive', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agent-modes/${params.id}/archive`, { method: 'POST', body: body as Record<string, unknown> ?? {}, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-modes/${params.id}/archive`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Archive agent mode', tags: ['AgentCenter'] } })
  .get('/api/v1/agent-modes/:id/versions', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/agent-modes/${params.id}/versions${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-modes/${params.id}/versions`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List agent mode versions', tags: ['AgentCenter'] } })
  .get('/api/v1/agent-modes/:id/versions/:versionId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agent-modes/${params.id}/versions/${params.versionId}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-modes/${params.id}/versions/${params.versionId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get agent mode version', tags: ['AgentCenter'] } })
  // --- prompt-pipelines thin proxy ---
  .get('/api/v1/prompt-pipelines', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/prompt-pipelines${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/prompt-pipelines'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List prompt pipelines', tags: ['PromptPipelines'] } })
  .post('/api/v1/prompt-pipelines', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/prompt-pipelines', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/prompt-pipelines'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create prompt pipeline', tags: ['PromptPipelines'] } })
  .get('/api/v1/prompt-pipelines/:id', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/prompt-pipelines/${params.id}${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-pipelines/${params.id}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get prompt pipeline', tags: ['PromptPipelines'] } })
  .put('/api/v1/prompt-pipelines/:id/draft', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-pipelines/${params.id}/draft`, { method: 'PUT', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-pipelines/${params.id}/draft`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Update prompt pipeline draft', tags: ['PromptPipelines'] } })
  .post('/api/v1/prompt-pipelines/:id/publish', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-pipelines/${params.id}/publish`, { method: 'POST', body: body as Record<string, unknown> ?? {}, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-pipelines/${params.id}/publish`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Publish prompt pipeline', tags: ['PromptPipelines'] } })
  .post('/api/v1/prompt-pipelines/:id/archive', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-pipelines/${params.id}/archive`, { method: 'POST', body: body as Record<string, unknown> ?? {}, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-pipelines/${params.id}/archive`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Archive prompt pipeline', tags: ['PromptPipelines'] } })
  .get('/api/v1/prompt-pipelines/:id/versions', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/prompt-pipelines/${params.id}/versions${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-pipelines/${params.id}/versions`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List prompt pipeline versions', tags: ['PromptPipelines'] } })
  .get('/api/v1/prompt-pipelines/:id/versions/:versionId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-pipelines/${params.id}/versions/${params.versionId}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-pipelines/${params.id}/versions/${params.versionId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get prompt pipeline version', tags: ['PromptPipelines'] } })
  .get('/api/v1/agent-runtime-instances', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const result = await proxyJson(`/api/v1/agent-runtime-instances${search}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agent-runtime-instances'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List agent runtime instances', tags: ['Agents'] } })
  // --- interactions thin proxy (dispatch_mode queued|insert|parallel, insert requires target_run_id) ---
  .post('/api/v1/sessions/:sessionId/interactions', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const validation = validateInteractionBody(body);
    if (!validation.ok) {
      setStatus(set, 400);
      set.headers['content-type'] = 'application/problem+json';
      setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
      const rid = (headers as Record<string,string>)['x-request-id'];
      return toProblemDetails(400, 'invalid_request', validation.errors.join('; '), `/api/v1/sessions/${params.sessionId}/interactions`, rid);
    }
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/interactions`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/interactions`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create session interaction', tags: ['Interactions'] } })
  .post('/api/v1/sessions/:sessionId/interactions/:interactionId/reassign', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/interactions/${params.interactionId}/reassign`, { method: 'POST', body: body as Record<string, unknown> ?? {}, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/interactions/${params.interactionId}/reassign`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Reassign interaction', tags: ['Interactions'] } })
  .post('/api/v1/sessions/:sessionId/interactions/:interactionId/cancel', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/interactions/${params.interactionId}/cancel`, { method: 'POST', body: body as Record<string, unknown> ?? {}, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/sessions/${params.sessionId}/interactions/${params.interactionId}/cancel`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Cancel interaction', tags: ['Interactions'] } })
  .get('/api/v1/sessions/:sessionId/interactions/:interactionId/stream', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URL(request.url).search;
    const cursor = request.headers.get('last-event-id') ?? request.headers.get('Last-Event-ID') ?? new URL(request.url).searchParams.get('cursor') ?? new URL(request.url).searchParams.get('after_seq');
    const corePath = `/api/v1/sessions/${params.sessionId}/interactions/${params.interactionId}/stream${search}`;
    const responseHeaders: Record<string,string> = { ...headers } as Record<string,string>;
    if (cursor) responseHeaders['last-event-id'] = String(cursor);
    const response = await proxySse(corePath, { headers: responseHeaders });
    if (response.status >= 400) {
      const text = await response.text();
      let data: unknown = null; try { data = JSON.parse(text); } catch { data = { message: text }; }
      setStatus(set, response.status);
      set.headers['content-type'] = 'application/problem+json';
      setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
      return mapCoreErrorToExternal(response.status, data, `/api/v1/sessions/${params.sessionId}/interactions/${params.interactionId}/stream`);
    }
    setStatus(set, response.status);
    set.headers['content-type'] = response.headers.get('content-type') ?? 'text/event-stream';
    set.headers['cache-control'] = 'no-cache';
    set.headers['connection'] = 'keep-alive';
    set.headers['x-accel-buffering'] = 'no';
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return response.body;
  }, { detail: { summary: 'Stream interaction SSE', tags: ['Interactions'], description: 'Thin SSE proxy with Last-Event-ID / ?cursor resume, kinds: ack/delta/done/error/heartbeat/task_node_update/supervision_update/context_version_update' } })
  .get('/api/v1/agent-candidates', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const search = new URLSearchParams();
    const q = query as Record<string,unknown>;
    for (const key of ['status', 'run_id', 'project_id', 'parent_candidate_id', 'limit']) {
      const value = q[key];
      if (value !== undefined && value !== '') search.set(key, String(value));
    }
    const suffix = search.toString() ? `?${search.toString()}` : '';
    const result = await proxyJson(`/api/v1/agent-candidates${suffix}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agent-candidates'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List agent candidates', tags: ['AgentCenter'] } })
  .post('/api/v1/agent-candidates/:candidateId/promote', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agent-candidates/${params.candidateId}/promote`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-candidates/${params.candidateId}/promote`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Promote agent candidate', tags: ['AgentCenter'] } })
  .post('/api/v1/agent-candidates/:candidateId/reject', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agent-candidates/${params.candidateId}/reject`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-candidates/${params.candidateId}/reject`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Reject agent candidate', tags: ['AgentCenter'] } })
  .get('/api/v1/agent-evolution/proposals', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/agent-evolution/proposals', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agent-evolution/proposals'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List evolution proposals', tags: ['AgentCenter'] } })
  .post('/api/v1/agent-evolution/generate', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.session_id) params.set('sessionId', String(q.session_id));
    if (q.lookback_event_count) params.set('lookbackEventCount', String(q.lookback_event_count));
    const suffix = params.toString() ? `?${params.toString()}` : '';
    const result = await proxyJson(`/api/v1/agent-evolution/generate${suffix}`, { method: 'POST', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agent-evolution/generate'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Generate evolution proposals', tags: ['AgentCenter'] } })
  .post('/api/v1/agent-evolution/proposals/:candidateId/promote', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agent-evolution/proposals/${params.candidateId}/promote`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-evolution/proposals/${params.candidateId}/promote`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Promote evolution proposal', tags: ['AgentCenter'] } })
  .post('/api/v1/agent-evolution/proposals/:candidateId/reject', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/agent-evolution/proposals/${params.candidateId}/reject`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/agent-evolution/proposals/${params.candidateId}/reject`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Reject evolution proposal', tags: ['AgentCenter'] } })
  .get('/api/v1/prompt-fragments/:fragmentId/versions', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/versions`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}/versions`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List prompt versions', tags: ['System'] } })
  .post('/api/v1/prompt-fragments/:fragmentId/versions', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/versions`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}/versions`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create prompt version', tags: ['System'] } })
  .post('/api/v1/prompt-fragments/:fragmentId/rollback', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/rollback`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}/rollback`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Rollback prompt fragment', tags: ['System'] } })
  .get('/api/v1/prompt-fragments/:fragmentId/effectiveness', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/effectiveness`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}/effectiveness`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get prompt effectiveness', tags: ['System'] } })
  .get('/api/v1/prompt-fragments/effectiveness', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/prompt-fragments/effectiveness', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/prompt-fragments/effectiveness'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List prompt effectiveness', tags: ['System'] } })
  .post('/api/v1/prompt-fragments/:fragmentId/signals', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/signals`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}/signals`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Record prompt signal', tags: ['System'] } })
  .post('/api/v1/prompt-fragments/:fragmentId/compare', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/compare`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/prompt-fragments/${params.fragmentId}/compare`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Compare prompt versions', tags: ['System'] } })
  .get('/api/v1/debug/traces', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.session_id) params.set('sessionId', String(q.session_id));
    if (q.run_id) params.set('runId', String(q.run_id));
    if (q.name) params.set('name', String(q.name));
    if (q.status) params.set('status', String(q.status));
    if (q.min_duration_ms) params.set('minDurationMs', String(q.min_duration_ms));
    if (q.limit) params.set('limit', String(q.limit));
    if (q.offset) params.set('offset', String(q.offset));
    const result = await proxyJson(`/api/v1/debug/traces?${params.toString()}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/traces'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List debug traces', tags: ['System'] } })
  .get('/api/v1/debug/traces/:traceId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/debug/traces/${params.traceId}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/debug/traces/${params.traceId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get trace', tags: ['System'] } })
  .get('/api/v1/debug/spans', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    if (q.name) params.set('name', String(q.name));
    if (q.status) params.set('status', String(q.status));
    if (q.min_duration_ms) params.set('minDurationMs', String(q.min_duration_ms));
    if (q.limit) params.set('limit', String(q.limit));
    const result = await proxyJson(`/api/v1/debug/spans?${params.toString()}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/spans'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List spans', tags: ['System'] } })
  .get('/api/v1/debug/metrics', async ({ query, set, request }) => {
    const headers = forwardHeaders(request);
    const params = new URLSearchParams();
    const q = query as Record<string,unknown>;
    params.set('metricName', String(q.metric_name ?? ''));
    if (q.window_ms) params.set('windowMs', String(q.window_ms));
    if (q.bucket_ms) params.set('bucketMs', String(q.bucket_ms));
    const result = await proxyJson(`/api/v1/debug/metrics?${params.toString()}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/metrics'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get metrics', tags: ['System'] } })
  .get('/api/v1/debug/snapshot/:sessionId', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/debug/snapshot/${params.sessionId}`, { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/debug/snapshot/${params.sessionId}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get debug snapshot', tags: ['System'] } })
  .get('/api/v1/debug/diagnostics', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/diagnostics', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/diagnostics'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Get diagnostics', tags: ['System'] } })
  .get('/api/v1/debug/processes', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/processes', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/processes'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List processes', tags: ['System'] } })
  .post('/api/v1/debug/simulate/message', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/simulate/message', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/simulate/message'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Simulate message', tags: ['System'] } })
  .post('/api/v1/debug/simulate/model-response', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/simulate/model-response', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/simulate/model-response'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Simulate model response', tags: ['System'] } })
  .post('/api/v1/debug/simulate/tool-result', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/simulate/tool-result', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/simulate/tool-result'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Simulate tool result', tags: ['System'] } })
  .post('/api/v1/debug/simulate/approval-decision', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/simulate/approval-decision', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/simulate/approval-decision'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Simulate approval decision', tags: ['System'] } })
  .post('/api/v1/debug/simulate/state-patch', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/simulate/state-patch', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/simulate/state-patch'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Simulate state patch', tags: ['System'] } })
  .get('/api/v1/debug/breakpoints', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/breakpoints', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/breakpoints'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'List breakpoints', tags: ['System'] } })
  .post('/api/v1/debug/breakpoints', async ({ body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/debug/breakpoints', { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/debug/breakpoints'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Create breakpoint', tags: ['System'] } })
  .delete('/api/v1/debug/breakpoints/:id', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/debug/breakpoints/${params.id}`, { method: 'DELETE', headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/debug/breakpoints/${params.id}`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Delete breakpoint', tags: ['System'] } })
    .get('/api/v1/agents/catalog', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson('/api/v1/agents/catalog', { headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, '/api/v1/agents/catalog'); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Agent catalog (dual-layer)', tags: ['Agents'] } })
  .post('/api/v1/runs/:runId/agents/spawn', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyJson(`/api/v1/runs/${(params as {runId:string}).runId}/agents/spawn`, { method: 'POST', body: body as Record<string, unknown>, headers });
    setStatus(set, result.status);
    if (result.status >= 400) { set.headers['content-type'] = 'application/problem+json'; return mapCoreErrorToExternal(result.status, result.data, `/api/v1/runs/${(params as {runId:string}).runId}/agents/spawn`); }
    setProxyResponseHeaders(set as never, (headers as Record<string,string>)['x-request-id']);
    return result.data;
  }, { detail: { summary: 'Spawn run agent (temporary/persistent/profile)', tags: ['Agents'] } })
  .ws('/ws/terminal', {
    open(ws) {
      const route = findWsRoute('/ws/terminal');
      if (route) {
        const targetUrl = buildTargetWsUrl(route);
        ws.subscribe('terminal-proxy');
        void targetUrl;
      }
    },
    message(ws, message) {
      ws.publish('terminal-proxy', message);
    },
    close(ws) {
      ws.unsubscribe('terminal-proxy');
    },
  })
  .ws('/ws/debug', {
    open(ws) {
      const route = findWsRoute('/ws/debug');
      if (route) {
        const targetUrl = buildTargetWsUrl(route);
        ws.subscribe('debug-proxy');
        void targetUrl;
      }
    },
    message(ws, message) {
      ws.publish('debug-proxy', message);
    },
    close(ws) {
      ws.unsubscribe('debug-proxy');
    },
  })
  .ws('/ws/collaboration', {
    open(ws) {
      const route = findWsRoute('/ws/collaboration');
      if (route) {
        const targetUrl = buildTargetWsUrl(route);
        ws.subscribe('collaboration-proxy');
        void targetUrl;
      }
    },
    message(ws, message) {
      ws.publish('collaboration-proxy', message);
    },
    close(ws) {
      ws.unsubscribe('collaboration-proxy');
    },
  })
  .get('/api/v1/files/:sessionId/*', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const filePath = `/${(params as Record<string,string>)['*']}`;
    const response = await proxyStream({
      target: 'core',
      path: `/api/v1/files/${params.sessionId}/${filePath}`,
      headers: Object.fromEntries(new Headers(headers).entries()),
    });
    setStreamHeaders(set, response);
    setStatus(set, response.status);
    set.headers['x-request-id'] = (headers as Record<string,string>)['x-request-id'];
    set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
    return response.body;
  }, { detail: { summary: 'Stream file', tags: ['System'] } })
  .get('/api/v1/sessions/:sessionId/logs', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const response = await proxyStream({
      target: 'core',
      path: `/api/v1/sessions/${params.sessionId}/logs`,
      headers: Object.fromEntries(new Headers(headers).entries()),
    });
    setStreamHeaders(set, response);
    setStatus(set, response.status);
    set.headers['x-request-id'] = (headers as Record<string,string>)['x-request-id'];
    set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
    return response.body;
  }, { detail: { summary: 'Session logs', tags: ['System'] } })
  .get('/api/v1/sessions/:sessionId/logs/stream', async ({ params, set, request }) => {
    const headers = forwardHeaders(request);
    const response = await proxyStream({
      target: 'core',
      path: `/api/v1/sessions/${params.sessionId}/logs/stream`,
      headers: Object.fromEntries(new Headers(headers).entries()),
    });
    setStreamHeaders(set, response);
    setStatus(set, response.status);
    set.headers['x-request-id'] = (headers as Record<string,string>)['x-request-id'];
    set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
    return response.body;
  }, { detail: { summary: 'Stream session logs', tags: ['System'] } })
  .get('/api/v1/tool-runtime/health', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyToolRuntimeJson('/api/v1/health', { headers } as never);
    setStatus(set, result.status);
    set.headers['x-request-id'] = (headers as Record<string,string>)['x-request-id'];
    return result.data;
  }, { detail: { summary: 'Tool runtime health (legacy)', tags: ['System'] } })
  .get('/api/v1/tool-runtime/manifest', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyToolRuntimeJson('/api/v1/manifest', { headers } as never);
    setStatus(set, result.status);
    set.headers['x-request-id'] = (headers as Record<string,string>)['x-request-id'];
    return result.data;
  }, { detail: { summary: 'Tool runtime manifest (legacy)', tags: ['System'] } })
  .get('/api/v1/tool-runtime/tools', async ({ set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyToolRuntimeJson('/api/v1/tools', { headers } as never);
    setStatus(set, result.status);
    set.headers['x-request-id'] = (headers as Record<string,string>)['x-request-id'];
    return result.data;
  }, { detail: { summary: 'Tool runtime tools (legacy)', tags: ['System'] } })
  .post('/api/v1/tool-runtime/tools/:toolId/execute', async ({ params, body, set, request }) => {
    const headers = forwardHeaders(request);
    const result = await proxyToolRuntimeJson(`/api/v1/tools/${encodeURIComponent(params.toolId)}/execute`, {
      method: 'POST',
      body: body as Record<string, unknown>,
      headers: headers as never,
    });
    setStatus(set, result.status);
    set.headers['x-request-id'] = (headers as Record<string,string>)['x-request-id'];
    return result.data;
  }, { detail: { summary: 'Tool runtime execute (legacy, no Gateway approval)', tags: ['System'] } });

export { app };

if (import.meta.main) {
  app.listen({ port: config.port, hostname: config.hostname });
  console.log(`TinadecGateway listening on http://${config.hostname}:${config.port} (${config.mode} mode)`);
  console.log(`  Core:         ${coreUrl()}`);
  console.log(`  Tool Runtime: ${toolRuntimeUrl()}`);
}
