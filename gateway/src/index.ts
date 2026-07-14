import { node } from '@elysiajs/node';
import { swagger } from '@elysiajs/swagger';
import { Elysia } from 'elysia';
import {
  codeToolApprovalBlockFor,
  codeToolApprovalUnavailableBlock,
  codeToolRequiresApproval,
  executeCodeTool,
  listCodeToolIds,
  listCodeToolSpecs,
  type ApprovalSnapshot,
  type CodeToolExecuteRequest
} from './codeTools.js';
import { coreUrl, proxyJson, proxySse } from './coreClient.js';
import { proxyDebugJson, debugWsUrl } from './debugProxy.js';
import { mcpRoutes } from './mcp/mcpRoutes.js';
import {
  agentRuntimeBindingWriteResult,
  loadAgentCenterOverview,
  loadModelCenterOverview,
  modelDiscoveryRefreshResult
} from './modelAgentCenter.js';

const port = Number(process.env.TINADEC_GATEWAY_PORT ?? 48730);

function setStatus(set: { status?: number | string }, status: number) {
  set.status = status;
}

/** Allowed CORS origins (local dev + Electron file:// + tauri://) */
const ALLOWED_ORIGINS: (string | RegExp)[] = [
  /^http:\/\/127\.0\.0\.1:\d+$/,
  /^http:\/\/localhost:\d+$/,
  'file://',
  'tauri://localhost',
  'https://tauri.localhost',
];

function isOriginAllowed(origin: string): boolean {
  return ALLOWED_ORIGINS.some((pattern) => {
    if (typeof pattern === 'string') return pattern === origin;
    return pattern.test(origin);
  });
}

async function verifyCodeToolApproval(toolId: string, request: CodeToolExecuteRequest) {
  const params = new URLSearchParams();
  if (request.session_id) {
    params.set('sessionId', request.session_id);
  }
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

const app = new Elysia({ adapter: node() })
  // Manual CORS middleware – the @elysiajs/cors plugin returns 400 on
  // OPTIONS preflight when used with the Node.js adapter.
  .onRequest(({ request, set }) => {
    const origin = request.headers.get('origin');

    const corsHeaders: Record<string, string> = {};
    if (origin && isOriginAllowed(origin)) {
      corsHeaders['access-control-allow-origin'] = origin;
      corsHeaders['access-control-allow-credentials'] = 'true';
      corsHeaders['vary'] = 'Origin';
    }

    // Handle CORS preflight
    if (request.method === 'OPTIONS') {
      const requestMethod = request.headers.get('access-control-request-method');
      const requestHeaders = request.headers.get('access-control-request-headers');
      if (requestMethod) {
        corsHeaders['access-control-allow-methods'] = requestMethod;
      }
      if (requestHeaders) {
        corsHeaders['access-control-allow-headers'] = requestHeaders;
      } else {
        corsHeaders['access-control-allow-headers'] = 'accept, content-type, authorization';
      }
      corsHeaders['access-control-max-age'] = '86400';

      set.headers = { ...set.headers, ...corsHeaders };
      set.status = 204;
      return '';
    }

    // For non-preflight requests, just set CORS headers
    Object.assign(set.headers, corsHeaders);
  })
  .use(swagger({
    path: '/docs',
    documentation: {
      info: {
        title: 'TinadecOffice API',
        version: '0.1.0'
      }
    }
  }))
  .use(mcpRoutes)
  .get('/api/v1/health', async ({ set }) => {
    const result = await proxyJson('/api/v1/health');
    setStatus(set, result.status);
    const core = (result.data && typeof result.data === 'object' ? result.data : {}) as Record<string, unknown>;
    return { ...core, gateway: 'ok', core_url: coreUrl };
  })
  .get('/api/v1/doctor', async ({ set }) => {
    const result = await proxyJson('/api/v1/doctor');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/readiness', async ({ set }) => {
    const result = await proxyJson('/api/v1/readiness');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/model-readiness', async ({ set }) => {
    const result = await proxyJson('/api/v1/model-readiness');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/model-catalog-readiness', async ({ set }) => {
    const result = await proxyJson('/api/v1/model-catalog-readiness');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/tool-layer-readiness', async ({ set }) => {
    const result = await proxyJson('/api/v1/tool-layer-readiness');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/projects', async ({ set }) => {
    const result = await proxyJson('/api/v1/projects');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/projects', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/projects', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/sessions', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.project_id) params.set('projectId', String(query.project_id));
    const result = await proxyJson(`/api/v1/sessions?${params.toString()}`);
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/sessions', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/sessions', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/sessions/:sessionId/messages', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/messages`);
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/sessions/:sessionId/messages', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/messages`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/sessions/:sessionId/invoke-stream', async ({ params, body, set }) => {
    const response = await proxySse(`/api/v1/sessions/${params.sessionId}/invoke-stream`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body as Record<string, unknown>)
    });
    set.headers['content-type'] = 'text/event-stream';
    set.headers['cache-control'] = 'no-cache';
    set.headers['connection'] = 'keep-alive';
    set.headers['x-accel-buffering'] = 'no';
    return response.body;
  })
  .get('/api/v1/sessions/:sessionId/orchestration', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/orchestration`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/sessions/:sessionId/tool-executions', async ({ params, query, set }) => {
    const search = new URLSearchParams();
    if (query.run_id) search.set('runId', String(query.run_id));
    if (query.limit) search.set('limit', String(query.limit));
    const suffix = search.toString() ? `?${search.toString()}` : '';
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/tool-executions${suffix}`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/sessions/:sessionId/runs', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/runs`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/sessions/:sessionId/task-nodes', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/task-nodes`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/sessions/:sessionId/context-packs', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/context-packs`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/sessions/:sessionId/supervision-findings', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/sessions/${params.sessionId}/supervision-findings`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/events', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.session_id) params.set('sessionId', String(query.session_id));
    const response = await proxySse(`/api/v1/events?${params.toString()}`);
    set.headers['content-type'] = 'text/event-stream';
    set.headers['cache-control'] = 'no-cache';
    return response.body;
  })
  .get('/api/v1/approvals', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.status) params.set('status', String(query.status));
    if (query.session_id) params.set('sessionId', String(query.session_id));
    const result = await proxyJson(`/api/v1/approvals?${params.toString()}`);
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/approvals', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/approvals', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/approvals/:approvalId/decision', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/approvals/${params.approvalId}/decision`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/tools/shell', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/tools/shell', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/runs/:runId/tools/:toolId/execute', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/runs/${params.runId}/tools/${params.toolId}/execute`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/code/tools', () => ({
    tool_ids: listCodeToolIds(),
    tools: listCodeToolSpecs()
  }))
  .get('/api/v1/tools/search', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.query) params.set('query', String(query.query));
    if (query.domain) params.set('domain', String(query.domain));
    if (query.source) params.set('source', String(query.source));
    if (query.risk) params.set('risk', String(query.risk));
    if (query.limit) params.set('limit', String(query.limit));
    const suffix = params.toString() ? `?${params.toString()}` : '';
    const result = await proxyJson(`/api/v1/tools/search${suffix}`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/tools', async ({ set }) => {
    const result = await proxyJson('/api/v1/tools');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/harness/manifest', async ({ set }) => {
    const result = await proxyJson('/api/v1/harness/manifest');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/prompt-fragments', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.scope) params.set('scope', String(query.scope));
    if (query.target_agent_id) params.set('targetAgentId', String(query.target_agent_id));
    if (query.category) params.set('category', String(query.category));
    if (query.enabled !== undefined) params.set('enabled', String(query.enabled));
    const suffix = params.toString() ? `?${params.toString()}` : '';
    const result = await proxyJson(`/api/v1/prompt-fragments${suffix}`);
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/prompt-fragments', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/prompt-fragments', {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .put('/api/v1/prompt-fragments/:fragmentId', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}`, {
      method: 'PUT',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .delete('/api/v1/prompt-fragments/:fragmentId', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}`, {
      method: 'DELETE'
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/prompt-fragments/:fragmentId/clone', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/clone`, {
      method: 'POST'
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/prompt-context/preview', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/prompt-context/preview', {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/code/tools/:toolId/execute', async ({ params, body, set }) => {
    const request = (body ?? {}) as CodeToolExecuteRequest;
    const requiresApproval = codeToolRequiresApproval(params.toolId);
    if (requiresApproval === null) {
      setStatus(set, 404);
      return {
        code: 'CODE_TOOL_NOT_FOUND',
        message: 'Code tool was not found.'
      };
    }

    if (requiresApproval && request.approval_id) {
      const approvalBlock = await verifyCodeToolApproval(params.toolId, request);
      if (approvalBlock) {
        return approvalBlock;
      }
    }

    const result = await executeCodeTool(params.toolId, request);
    if (!result) {
      setStatus(set, 404);
      return {
        code: 'CODE_TOOL_NOT_FOUND',
        message: 'Code tool was not found.'
      };
    }

    return result;
  })
  .get('/api/v1/model-center/overview', async ({ set }) => {
    const result = await loadModelCenterOverview();
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/model-center/provider-instances/:providerInstanceId/models/refresh', ({ params, set }) => {
    const result = modelDiscoveryRefreshResult(params.providerInstanceId);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/model-provider-templates', async ({ set }) => {
    const result = await proxyJson('/api/v1/model-provider-templates');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/model-providers', async ({ set }) => {
    const result = await proxyJson('/api/v1/model-providers');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/model-providers', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/model-providers', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .put('/api/v1/model-providers/:providerInstanceId', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/model-providers/${params.providerInstanceId}`, {
      method: 'PUT',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .delete('/api/v1/model-providers/:providerInstanceId', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/model-providers/${params.providerInstanceId}`, {
      method: 'DELETE'
    });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/model-routes', async ({ set }) => {
    const result = await proxyJson('/api/v1/model-routes');
    setStatus(set, result.status);
    return result.data;
  })
  .put('/api/v1/model-routes/:purpose', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/model-routes/${params.purpose}`, {
      method: 'PUT',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/model-settings', async ({ set }) => {
    const result = await proxyJson('/api/v1/model-settings');
    setStatus(set, result.status);
    return result.data;
  })
  .put('/api/v1/model-settings', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/model-settings', { method: 'PUT', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/market/sources', async ({ set }) => {
    const result = await proxyJson('/api/v1/market/sources');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/market/sources', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/market/sources', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/market/sources/:sourceId/refresh', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/market/sources/${params.sourceId}/refresh`, { method: 'POST' });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/market/catalog', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.kind) params.set('kind', String(query.kind));
    if (query.query) params.set('query', String(query.query));
    if (query.source_id) params.set('sourceId', String(query.source_id));
    const result = await proxyJson(`/api/v1/market/catalog?${params.toString()}`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/market/catalog/:catalogId', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/market/catalog/${params.catalogId}`);
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/extensions/install-preview', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/extensions/install-preview', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/extensions/install', async ({ body, set }) => {
    const result = await proxyJson('/api/v1/extensions/install', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/extensions/installed', async ({ set }) => {
    const result = await proxyJson('/api/v1/extensions/installed');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/extensions/:extensionId/enable', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/extensions/${params.extensionId}/enable`, { method: 'POST' });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/extensions/:extensionId/disable', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/extensions/${params.extensionId}/disable`, { method: 'POST' });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/extensions/:extensionId/update', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/extensions/${params.extensionId}/update`, { method: 'POST' });
    setStatus(set, result.status);
    return result.data;
  })
  .delete('/api/v1/extensions/:extensionId', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/extensions/${params.extensionId}`, { method: 'DELETE' });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/mcp/servers', async ({ set }) => {
    const result = await proxyJson('/api/v1/mcp/servers');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/mcp/servers/:serverId/tools', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/mcp/servers/${params.serverId}/tools`);
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/mcp/servers/:serverId/reload', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/mcp/servers/${params.serverId}/reload`, { method: 'POST' });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/acp/adapters', async ({ set }) => {
    const result = await proxyJson('/api/v1/acp/adapters');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/acp/adapters/:adapterId/probe', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/acp/adapters/${params.adapterId}/probe`, { method: 'POST' });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/agent-center/overview', async ({ set }) => {
    const result = await loadAgentCenterOverview();
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/agent-modes', async ({ set }) => {
    const result = await proxyJson('/api/v1/agent-modes');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/agents', async ({ set }) => {
    const result = await proxyJson('/api/v1/agents');
    setStatus(set, result.status);
    return result.data;
  })
  .put('/api/v1/agents/:agentId', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/agents/${params.agentId}`, {
      method: 'PUT',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .put('/api/v1/agents/:agentId/runtime-binding', ({ params, body, set }) => {
    const result = agentRuntimeBindingWriteResult(params.agentId, body);
    setStatus(set, result.status);
    return result.data;
  })
  .put('/api/v1/agents/:agentId/mode', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/agents/${params.agentId}/mode`, {
      method: 'PUT',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/agent-candidates', async ({ set }) => {
    const result = await proxyJson('/api/v1/agent-candidates');
    setStatus(set, result.status);
    return result.data;
  })
  // --- Agent Evolution proxy routes ---
  .get('/api/v1/agent-evolution/proposals', async ({ set }) => {
    const result = await proxyJson('/api/v1/agent-evolution/proposals');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/agent-evolution/generate', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.session_id) params.set('sessionId', String(query.session_id));
    if (query.lookback_event_count) params.set('lookbackEventCount', String(query.lookback_event_count));
    const suffix = params.toString() ? `?${params.toString()}` : '';
    const result = await proxyJson(`/api/v1/agent-evolution/generate${suffix}`, { method: 'POST' });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/agent-evolution/proposals/:candidateId/promote', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/agent-evolution/proposals/${params.candidateId}/promote`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/agent-evolution/proposals/:candidateId/reject', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/agent-evolution/proposals/${params.candidateId}/reject`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  // --- Prompt Engineering proxy routes ---
  .get('/api/v1/prompt-fragments/:fragmentId/versions', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/versions`);
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/prompt-fragments/:fragmentId/versions', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/versions`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/prompt-fragments/:fragmentId/rollback', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/rollback`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/prompt-fragments/:fragmentId/effectiveness', async ({ params, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/effectiveness`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/prompt-fragments/effectiveness', async ({ set }) => {
    const result = await proxyJson('/api/v1/prompt-fragments/effectiveness');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/prompt-fragments/:fragmentId/signals', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/signals`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/prompt-fragments/:fragmentId/compare', async ({ params, body, set }) => {
    const result = await proxyJson(`/api/v1/prompt-fragments/${params.fragmentId}/compare`, {
      method: 'POST',
      body: body as Record<string, unknown>
    });
    setStatus(set, result.status);
    return result.data;
  })
  // --- Agent Debug Studio proxy routes ---
  .get('/api/v1/debug/traces', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.session_id) params.set('sessionId', String(query.session_id));
    if (query.run_id) params.set('runId', String(query.run_id));
    if (query.name) params.set('name', String(query.name));
    if (query.status) params.set('status', String(query.status));
    if (query.min_duration_ms) params.set('minDurationMs', String(query.min_duration_ms));
    if (query.limit) params.set('limit', String(query.limit));
    if (query.offset) params.set('offset', String(query.offset));
    const result = await proxyDebugJson(`/api/v1/debug/traces?${params.toString()}`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/debug/traces/:traceId', async ({ params, set }) => {
    const result = await proxyDebugJson(`/api/v1/debug/traces/${params.traceId}`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/debug/spans', async ({ query, set }) => {
    const params = new URLSearchParams();
    if (query.name) params.set('name', String(query.name));
    if (query.status) params.set('status', String(query.status));
    if (query.min_duration_ms) params.set('minDurationMs', String(query.min_duration_ms));
    if (query.limit) params.set('limit', String(query.limit));
    const result = await proxyDebugJson(`/api/v1/debug/spans?${params.toString()}`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/debug/metrics', async ({ query, set }) => {
    const params = new URLSearchParams();
    params.set('metricName', String(query.metric_name ?? ''));
    if (query.window_ms) params.set('windowMs', String(query.window_ms));
    if (query.bucket_ms) params.set('bucketMs', String(query.bucket_ms));
    const result = await proxyDebugJson(`/api/v1/debug/metrics?${params.toString()}`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/debug/snapshot/:sessionId', async ({ params, set }) => {
    const result = await proxyDebugJson(`/api/v1/debug/snapshot/${params.sessionId}`);
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/debug/diagnostics', async ({ set }) => {
    const result = await proxyDebugJson('/api/v1/debug/diagnostics');
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/debug/processes', async ({ set }) => {
    const result = await proxyDebugJson('/api/v1/debug/processes');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/debug/simulate/message', async ({ body, set }) => {
    const result = await proxyDebugJson('/api/v1/debug/simulate/message', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/debug/simulate/model-response', async ({ body, set }) => {
    const result = await proxyDebugJson('/api/v1/debug/simulate/model-response', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/debug/simulate/tool-result', async ({ body, set }) => {
    const result = await proxyDebugJson('/api/v1/debug/simulate/tool-result', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/debug/simulate/approval-decision', async ({ body, set }) => {
    const result = await proxyDebugJson('/api/v1/debug/simulate/approval-decision', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/debug/simulate/state-patch', async ({ body, set }) => {
    const result = await proxyDebugJson('/api/v1/debug/simulate/state-patch', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/debug/breakpoints', async ({ set }) => {
    const result = await proxyDebugJson('/api/v1/debug/breakpoints');
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/debug/breakpoints', async ({ body, set }) => {
    const result = await proxyDebugJson('/api/v1/debug/breakpoints', { method: 'POST', body: body as Record<string, unknown> });
    setStatus(set, result.status);
    return result.data;
  })
  .delete('/api/v1/debug/breakpoints/:id', async ({ params, set }) => {
    const result = await proxyDebugJson(`/api/v1/debug/breakpoints/${params.id}`, { method: 'DELETE' });
    setStatus(set, result.status);
    return result.data;
  })
  .listen({ port, hostname: '127.0.0.1' });

console.log(`TinadecOffice API listening on http://127.0.0.1:${port}`);
