/**
 * MCP 路由插件：Gateway 侧的 MCP 协调端点（纯代理模式）。
 *
 * Gateway 不再管理 MCP 连接生命周期，所有 MCP 操作代理到 Tool Runtime。
 *
 * 端点：
 * - POST /api/v1/mcp/servers/:serverId/connect    — 代理到 Tool Runtime 连接 MCP server
 * - POST /api/v1/mcp/servers/:serverId/disconnect — 代理到 Tool Runtime 断开 MCP server
 * - GET  /api/v1/mcp/servers/:serverId/status     — 代理到 Tool Runtime 查询状态
 * - POST /api/v1/mcp/servers/:serverId/tools/:toolName/call — 代理到 Tool Runtime 调用 MCP 工具
 *
 * Gateway 保持薄代理模式，不存储 MCP 连接状态。
 */

import { Elysia, t } from 'elysia';
import { proxyToolRuntimeJson } from '../toolRuntimeClient.js';

function setStatus(set: { status?: number | string }, status: number): void {
  set.status = status;
}

export const mcpRoutes = new Elysia({ name: 'mcp-routes' })
  .post('/api/v1/mcp/servers/:serverId/connect', async ({ params, set }) => {
    const { serverId } = params as { serverId: string };
    const result = await proxyToolRuntimeJson(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/connect`, {
      method: 'POST',
    });
    setStatus(set, result.status);
    return result.data;
  })
  .post('/api/v1/mcp/servers/:serverId/disconnect', async ({ params, set }) => {
    const { serverId } = params as { serverId: string };
    const result = await proxyToolRuntimeJson(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/disconnect`, {
      method: 'POST',
    });
    setStatus(set, result.status);
    return result.data;
  })
  .get('/api/v1/mcp/servers/:serverId/status', async ({ params, set }) => {
    const { serverId } = params as { serverId: string };
    const result = await proxyToolRuntimeJson(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/status`);
    setStatus(set, result.status);
    return result.data;
  })
  .post(
    '/api/v1/mcp/servers/:serverId/tools/:toolName/call',
    async ({ params, body, set }) => {
      const { serverId, toolName } = params as { serverId: string; toolName: string };
      const requestBody = (body ?? {}) as { arguments?: Record<string, unknown> };
      const result = await proxyToolRuntimeJson(
        `/api/v1/mcp/servers/${encodeURIComponent(serverId)}/tools/${encodeURIComponent(toolName)}/call`,
        {
          method: 'POST',
          body: requestBody,
        },
      );
      setStatus(set, result.status);
      return result.data;
    },
    {
      body: t.Object({
        arguments: t.Optional(t.Record(t.String(), t.Unknown())),
      }),
    },
  );
