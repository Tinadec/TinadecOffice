/**
 * MCP 运行时共享类型定义。
 *
 * 借鉴 codex rust 的 McpConnectionManager / AsyncManagedClient / ToolInfo 设计，
 * 以及 vscode 的 McpServerConnection 状态机。
 */

/** MCP server 连接状态机（借鉴 vscode mcpServerConnection.ts） */
export type McpConnectionState =
  | 'stopped'
  | 'starting'
  | 'running'
  | 'error';

/** 从 Core manifest 解析出的 stdio 启动配置 */
export interface McpStdioConfig {
  transport: 'stdio';
  command: string;
  args: string[];
  env?: Record<string, string>;
  cwd?: string;
}

/** 从 Core manifest 解析出的 HTTP 启动配置（P1 预留） */
export interface McpHttpConfig {
  transport: 'http';
  url: string;
  headers?: Record<string, string>;
}

export type McpServerConfig = McpStdioConfig | McpHttpConfig;

/** 从 manifest.json 解析启动配置 */
export function parseServerConfig(manifestJson: string): McpServerConfig | null {
  try {
    const manifest = JSON.parse(manifestJson) as {
      entrypoints?: {
        mcp?: {
          transport?: string;
          command?: string;
          args?: string[];
          env?: Record<string, string>;
          cwd?: string;
          url?: string;
          headers?: Record<string, string>;
        };
      };
    };
    const mcp = manifest.entrypoints?.mcp;
    if (!mcp) return null;

    if (mcp.transport === 'http' && mcp.url) {
      return {
        transport: 'http',
        url: mcp.url,
        headers: mcp.headers,
      };
    }

    if (mcp.command) {
      return {
        transport: 'stdio',
        command: mcp.command,
        args: mcp.args ?? [],
        env: mcp.env,
        cwd: mcp.cwd,
      };
    }

    return null;
  } catch {
    return null;
  }
}

/** MCP server 元信息（来自 Core DB） */
export interface McpServerInfo {
  id: string;
  extensionId: string;
  name: string;
  transport: string;
  status: string;
  tools: string[];
  manifestJson: string;
  updatedAt: string;
}

/** JSON-RPC 2.0 请求 */
export interface JsonRpcRequest {
  jsonrpc: '2.0';
  id: number | string;
  method: string;
  params?: unknown;
}

/** JSON-RPC 2.0 响应 */
export interface JsonRpcResponse {
  jsonrpc: '2.0';
  id: number | string;
  result?: unknown;
  error?: { code: number; message: string; data?: unknown };
}

/** JSON-RPC 2.0 通知（无 id） */
export interface JsonRpcNotification {
  jsonrpc: '2.0';
  method: string;
  params?: unknown;
}

/** MCP initialize 请求参数 */
export interface InitializeParams {
  protocolVersion: string;
  capabilities: Record<string, unknown>;
  clientInfo: { name: string; version: string };
}

/** MCP initialize 响应结果 */
export interface InitializeResult {
  protocolVersion: string;
  capabilities: Record<string, unknown>;
  serverInfo?: { name: string; version: string };
}

/** MCP tool 定义（来自 tools/list） */
export interface McpToolDefinition {
  name: string;
  description?: string;
  inputSchema?: Record<string, unknown>;
}

/** MCP tools/list 响应 */
export interface ToolsListResult {
  tools: McpToolDefinition[];
  nextCursor?: string;
}

/** MCP tools/call 请求参数 */
export interface ToolCallParams {
  name: string;
  arguments?: Record<string, unknown>;
}

/** MCP tools/call 响应结果 */
export interface ToolCallResult {
  content: Array<{ type: string; text?: string; data?: string; mimeType?: string }>;
  isError?: boolean;
}

/** Gateway 回调 Core /report 的 body */
export interface ReportBody {
  status: 'connected' | 'error' | 'disconnected';
  tools?: string[];
  statusMessage?: string;
  exitCode?: number;
}
