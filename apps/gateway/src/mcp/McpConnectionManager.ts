/**
 * MCP 连接管理器：按 server id 索引管理多个 MCP 客户端连接。
 *
 * 借鉴：
 * - codex McpConnectionManager 的 HashMap<String, AsyncManagedClient> 按 name 索引
 * - codex AsyncManagedClient 的 Shared<BoxFuture> 模式——可共享 Promise，避免重复启动
 * - codex JoinSet 的并发启动（Promise.all 不串行）
 * - vscode McpServerConnection 的状态机
 */

import { McpClient, type McpClientState } from './McpClient.js';
import {
  parseServerConfig,
  type McpServerInfo,
  type McpToolDefinition,
  type ReportBody,
} from './types.js';
import { coreEndpoint, proxyJson } from '../coreClient.js';

interface ManagedConnection {
  client: McpClient;
  /** Shared<Promise>：正在进行的 start 操作可共享，避免重复 spawn（借鉴 codex AsyncManagedClient） */
  startPromise: Promise<void> | null;
  tools: McpToolDefinition[];
  lastExitCode: number | null;
}

export class McpConnectionManager {
  private readonly connections = new Map<string, ManagedConnection>();

  /** 连接指定 MCP server（spawn + 握手 + tools/list） */
  async connect(server: McpServerInfo): Promise<{ status: string; tools: string[] }> {
    const existing = this.connections.get(server.id);
    if (existing && existing.startPromise) {
      // Shared<Promise>：复用正在进行的启动（借鉴 codex AsyncManagedClient）
      await existing.startPromise;
      return this.toStatusResult(existing);
    }

    if (existing && existing.client.getState() === 'running') {
      return this.toStatusResult(existing);
    }

    const config = parseServerConfig(server.manifestJson);
    if (!config) {
      await this.reportToCore(server.id, {
        status: 'error',
        statusMessage: `Failed to parse server config from manifest`,
      });
      throw new Error(`Failed to parse server config for ${server.id}`);
    }

    // 创建新连接
    const client = new McpClient(config, {
      onStateChange: (state, detail) => this.handleStateChange(server.id, state, detail),
      onToolsChange: (tools) => this.handleToolsChange(server.id, tools),
      onExit: (code) => this.handleExit(server.id, code),
    });

    const connection: ManagedConnection = {
      client,
      startPromise: null,
      tools: [],
      lastExitCode: null,
    };
    this.connections.set(server.id, connection);

    // Shared<Promise>：存储 start promise，并发调用可复用
    connection.startPromise = client.start().finally(() => {
      connection.startPromise = null;
    });

    try {
      await connection.startPromise;
      return this.toStatusResult(connection);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      await this.reportToCore(server.id, {
        status: 'error',
        statusMessage: message,
      });
      throw error;
    }
  }

  /** 优雅关闭指定 MCP server */
  async disconnect(serverId: string): Promise<{ status: string }> {
    const connection = this.connections.get(serverId);
    if (!connection) {
      return { status: 'disconnected' };
    }
    await connection.client.stop();
    this.connections.delete(serverId);
    return { status: 'disconnected' };
  }

  /** 查询连接状态 */
  getStatus(serverId: string): { state: McpClientState; tools: string[] } | null {
    const connection = this.connections.get(serverId);
    if (!connection) return null;
    return {
      state: connection.client.getState(),
      tools: connection.tools.map((t) => t.name),
    };
  }

  /** 调用 MCP 工具 */
  async callTool(
    serverId: string,
    toolName: string,
    args: Record<string, unknown> | undefined,
  ): Promise<unknown> {
    const connection = this.connections.get(serverId);
    if (!connection || connection.client.getState() !== 'running') {
      throw new Error(`MCP server ${serverId} is not running`);
    }
    return connection.client.callTool({ name: toolName, arguments: args });
  }

  /** 获取指定 server 的工具列表 */
  getTools(serverId: string): McpToolDefinition[] {
    return this.connections.get(serverId)?.tools ?? [];
  }

  /** 获取所有已连接 server 的工具（用于工具注入） */
  getAllTools(): Array<{ serverId: string; serverName: string; tools: McpToolDefinition[] }> {
    const result: Array<{ serverId: string; serverName: string; tools: McpToolDefinition[] }> = [];
    for (const [serverId, connection] of this.connections) {
      if (connection.client.getState() === 'running') {
        result.push({
          serverId,
          serverName: serverId,
          tools: connection.tools,
        });
      }
    }
    return result;
  }

  private handleStateChange = async (
    serverId: string,
    state: McpClientState,
    detail?: string,
  ): Promise<void> => {
    if (state === 'running') {
      // running 状态由 handleToolsChange 触发 report（含 tools 列表）
      return;
    }
    if (state === 'error') {
      await this.reportToCore(serverId, {
        status: 'error',
        statusMessage: detail,
      });
    }
  };

  private handleToolsChange = async (
    serverId: string,
    tools: McpToolDefinition[],
  ): Promise<void> => {
    const connection = this.connections.get(serverId);
    if (connection) {
      connection.tools = tools;
    }
    await this.reportToCore(serverId, {
      status: 'connected',
      tools: tools.map((t) => t.name),
    });
  };

  private handleExit = async (serverId: string, code: number | null): Promise<void> => {
    const connection = this.connections.get(serverId);
    if (connection) {
      connection.lastExitCode = code;
    }
    await this.reportToCore(serverId, {
      status: 'disconnected',
      exitCode: code ?? undefined,
      statusMessage: `Process exited with code ${code}`,
    });
  };

  /** 回调 Core /api/v1/mcp/servers/{id}/report 上报状态和 tools */
  private async reportToCore(serverId: string, body: ReportBody): Promise<void> {
    try {
      await proxyJson(`/api/v1/mcp/servers/${encodeURIComponent(serverId)}/report`, {
        method: 'POST',
        body: body as unknown as Record<string, unknown>,
      });
    } catch (error) {
      // 上报失败不应影响连接本身
      console.error(`[mcp] Failed to report to Core for ${serverId}:`, error);
    }
  }

  private toStatusResult(connection: ManagedConnection): { status: string; tools: string[] } {
    const state = connection.client.getState();
    const statusMap: Record<McpClientState, string> = {
      stopped: 'disconnected',
      starting: 'pending_connect',
      running: 'connected',
      error: 'error',
    };
    return {
      status: statusMap[state],
      tools: connection.tools.map((t) => t.name),
    };
  }
}

/** 全局单例（Gateway 进程内共享） */
export const mcpConnectionManager = new McpConnectionManager();
