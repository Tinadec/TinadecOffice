/**
 * MCP 路由插件：Gateway 侧的 MCP 协调端点。
 *
 * 端点：
 * - POST /api/v1/mcp/servers/:serverId/connect    — 从 Core 拉取 server info，spawn + 握手
 * - POST /api/v1/mcp/servers/:serverId/disconnect — 优雅关闭
 * - GET  /api/v1/mcp/servers/:serverId/status     — 查询运行时状态
 * - POST /api/v1/mcp/servers/:serverId/tools/:toolName/call — 调用 MCP 工具
 *
 * Gateway 回调 Core /api/v1/mcp/servers/:id/report 由 McpConnectionManager 内部触发。
 */

import { Elysia, t } from 'elysia';
import { mcpConnectionManager } from './McpConnectionManager.js';
import type { McpServerInfo } from './types.js';
import { proxyJson } from '../coreClient.js';

async function fetchServerInfoFromCore(serverId: string): Promise<McpServerInfo | null> {
  const result = await proxyJson('/api/v1/mcp/servers');
  if (result.status < 200 || result.status >= 300 || !Array.isArray(result.data)) {
    return null;
  }
  const server = (result.data as Array<Record<string, unknown>>).find(
    (item) => item.id === serverId,
  );
  if (!server) return null;
  return {
    id: String(server.id),
    extensionId: String(server.extensionId ?? ''),
    name: String(server.name ?? ''),
    transport: String(server.transport ?? 'stdio'),
    status: String(server.status ?? 'pending_connect'),
    tools: Array.isArray(server.tools) ? (server.tools as string[]) : [],
    manifestJson: typeof server.manifestJson === 'string' ? server.manifestJson : '{}',
    updatedAt: String(server.updatedAt ?? ''),
  };
}

export const mcpRoutes = new Elysia({ name: 'mcp-routes' })
  .post('/api/v1/mcp/servers/:serverId/connect', async ({ params, set }) => {
    const { serverId } = params as { serverId: string };
    const server = await fetchServerInfoFromCore(serverId);
    if (!server) {
      setStatus(set, 404);
      return { error: 'MCP_SERVER_NOT_FOUND', message: `MCP server ${serverId} was not found in Core.` };
    }
    try {
      const result = await mcpConnectionManager.connect(server);
      setStatus(set, 200);
      return result;
    } catch (error) {
      setStatus(set, 502);
      return {
        error: 'MCP_CONNECT_FAILED',
        message: error instanceof Error ? error.message : String(error),
      };
    }
  })
  .post('/api/v1/mcp/servers/:serverId/disconnect', async ({ params, set }) => {
    const { serverId } = params as { serverId: string };
    const result = await mcpConnectionManager.disconnect(serverId);
    setStatus(set, 200);
    return result;
  })
  .get('/api/v1/mcp/servers/:serverId/status', async ({ params, set }) => {
    const { serverId } = params as { serverId: string };
    const status = mcpConnectionManager.getStatus(serverId);
    if (!status) {
      setStatus(set, 200);
      return { state: 'stopped', tools: [] };
    }
    setStatus(set, 200);
    return status;
  })
  .post(
    '/api/v1/mcp/servers/:serverId/tools/:toolName/call',
    async ({ params, body, set }) => {
      const { serverId, toolName } = params as { serverId: string; toolName: string };
      const requestBody = (body ?? {}) as { arguments?: Record<string, unknown> };
      try {
        const result = await mcpConnectionManager.callTool(
          serverId,
          toolName,
          requestBody.arguments,
        );
        setStatus(set, 200);
        return { ok: true, result };
      } catch (error) {
        setStatus(set, 502);
        return {
          ok: false,
          error: 'MCP_TOOL_CALL_FAILED',
          message: error instanceof Error ? error.message : String(error),
        };
      }
    },
    {
      body: t.Object({
        arguments: t.Optional(t.Record(t.String(), t.Unknown())),
      }),
    },
  );

function setStatus(set: { status?: number | string }, status: number): void {
  set.status = status;
}
