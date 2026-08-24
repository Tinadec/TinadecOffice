/**
 * MCP 路由插件：Gateway 侧的 MCP 协调端点（纯代理到 Core）。
 * 保留多协议语义，薄代理透传，不存状态，不实现执行判断。
 */

import { Elysia, t } from 'elysia';
import { proxyJson } from '../coreClient.js';
import { ensureRequestId, PRINCIPAL_VALUE } from '../headers.js';

function setStatus(set: { status?: number | string }, status: number): void {
  set.status = status;
}

function proxyHeaders(request?: Request): Record<string, string> {
  const rid = ensureRequestId(request?.headers.get('x-request-id') ?? request?.headers.get('X-Request-Id') ?? undefined);
  return { 'x-request-id': rid, 'x-tinadec-principal': PRINCIPAL_VALUE };
}

export const mcpRoutes = new Elysia({ name: 'mcp-routes' })
  .post('/api/v1/mcp/servers/:serverId/connect', async ({ params, set, request }) => {
    const { serverId } = params as { serverId: string };
    const result = await proxyJson(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/connect`, {
      method: 'POST',
      headers: proxyHeaders(request),
    });
    setStatus(set, result.status);
    (set.headers as Record<string,string>)['x-request-id'] = proxyHeaders(request)['x-request-id'];
    return result.data;
  }, { detail: { summary: 'MCP connect (proxy to Core)', tags: ['System'] } })
  .post('/api/v1/mcp/servers/:serverId/disconnect', async ({ params, set, request }) => {
    const { serverId } = params as { serverId: string };
    const result = await proxyJson(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/disconnect`, {
      method: 'POST',
      headers: proxyHeaders(request),
    });
    setStatus(set, result.status);
    (set.headers as Record<string,string>)['x-request-id'] = proxyHeaders(request)['x-request-id'];
    return result.data;
  }, { detail: { summary: 'MCP disconnect (proxy to Core)', tags: ['System'] } })
  .get('/api/v1/mcp/servers/:serverId/status', async ({ params, set, request }) => {
    const { serverId } = params as { serverId: string };
    const result = await proxyJson(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/status`, { headers: proxyHeaders(request) });
    setStatus(set, result.status);
    (set.headers as Record<string,string>)['x-request-id'] = proxyHeaders(request)['x-request-id'];
    return result.data;
  }, { detail: { summary: 'MCP status (proxy to Core)', tags: ['System'] } })
  .post(
    '/api/v1/mcp/servers/:serverId/tools/:toolName/call',
    async ({ params, body, set, request }) => {
      const { serverId, toolName } = params as { serverId: string; toolName: string };
      const requestBody = (body ?? {}) as { arguments?: Record<string, unknown> };
      const result = await proxyJson(
        `/api/v1/mcp/servers/${encodeURIComponent(serverId)}/tools/${encodeURIComponent(toolName)}/call`,
        {
          method: 'POST',
          body: requestBody,
          headers: proxyHeaders(request),
        },
      );
      setStatus(set, result.status);
      (set.headers as Record<string,string>)['x-request-id'] = proxyHeaders(request)['x-request-id'];
      return result.data;
    },
    {
      body: t.Object({
        arguments: t.Optional(t.Record(t.String(), t.Unknown())),
      }),
      detail: { summary: 'MCP tool call (proxy to Core)', tags: ['System'] }
    },
  );
