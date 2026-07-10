/**
 * MCP 客户端：stdio 传输 + JSON-RPC 2.0 握手 + 生命周期管理。
 *
 * 融合：
 * - vscode McpServerRequestHandler 的 initialize → notifications/initialized → tools/list → tools/call 握手序列
 * - vscode McpStdioStateHandler 的优雅关闭时序（stdin end → 10s → SIGTERM → 10s → SIGKILL）
 * - codex CancellationToken 的取消传播（Node 用 AbortController 模拟）
 * - codex active_time_timeout 在 tools/call 期间暂停空闲超时
 * - codeTools.ts tryExecuteNativeTool 的 spawn 范式（windowsHide + stdio pipe）
 */

import { spawn, type ChildProcess } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import type {
  InitializeParams,
  InitializeResult,
  JsonRpcNotification,
  JsonRpcRequest,
  JsonRpcResponse,
  McpServerConfig,
  McpStdioConfig,
  McpToolDefinition,
  ToolCallParams,
  ToolCallResult,
  ToolsListResult,
} from './types.js';

const PROTOCOL_VERSION = '2025-06-18';
const CLIENT_INFO = { name: 'tinadec-gateway', version: '0.1.0' };
const GRACE_PERIOD_MS = 10_000;
const INITIALIZE_TIMEOUT_MS = 15_000;
const TOOL_CALL_TIMEOUT_MS = 60_000;

export type McpClientState = 'stopped' | 'starting' | 'running' | 'error';

interface PendingRequest {
  resolve: (value: JsonRpcResponse) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

export class McpClient {
  private child: ChildProcess | null = null;
  private state: McpClientState = 'stopped';
  private nextId = 1;
  private readonly pending = new Map<number, PendingRequest>();
  private buffer = '';
  private readonly config: McpStdioConfig;
  private readonly onStateChange: (state: McpClientState, detail?: string) => void;
  private readonly onToolsChange: (tools: McpToolDefinition[]) => void;
  private readonly onExit: (code: number | null) => void;
  private shutdownAbort: AbortController | null = null;
  private activeCalls = 0;

  constructor(
    config: McpServerConfig,
    callbacks: {
      onStateChange: (state: McpClientState, detail?: string) => void;
      onToolsChange: (tools: McpToolDefinition[]) => void;
      onExit: (code: number | null) => void;
    },
  ) {
    if (config.transport !== 'stdio') {
      throw new Error(`Unsupported transport for P0: ${config.transport}`);
    }
    this.config = config;
    this.onStateChange = callbacks.onStateChange;
    this.onToolsChange = callbacks.onToolsChange;
    this.onExit = callbacks.onExit;
  }

  getState(): McpClientState {
    return this.state;
  }

  private setState(state: McpClientState, detail?: string): void {
    this.state = state;
    this.onStateChange(state, detail);
  }

  /** 启动子进程并完成 initialize 握手 + tools/list 拉取 */
  async start(): Promise<void> {
    if (this.state === 'running' || this.state === 'starting') {
      return;
    }
    this.setState('starting');

    const env = { ...process.env, ...this.config.env };
    this.child = spawn(this.config.command, this.config.args, {
      cwd: this.config.cwd ?? process.cwd(),
      env,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });

    this.child.stdout?.setEncoding('utf-8');
    this.child.stdout?.on('data', this.onStdoutData);
    this.child.stderr?.on('data', this.onStderrData);
    this.child.on('exit', this.onChildExit);
    this.child.on('error', this.onChildError);

    try {
      await this.initialize();
      const tools = await this.listTools();
      this.onToolsChange(tools);
      this.setState('running');
    } catch (error) {
      this.setState('error', error instanceof Error ? error.message : String(error));
      throw error;
    }
  }

  /** 优雅关闭：stdin end → 10s → SIGTERM → 10s → SIGKILL（借鉴 vscode McpStdioStateHandler） */
  async stop(): Promise<void> {
    if (!this.child || this.state === 'stopped') {
      return;
    }

    this.shutdownAbort = new AbortController();
    const child = this.child;

    // 取消所有进行中的请求（借鉴 codex CancellationToken）
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(new Error('MCP client is shutting down'));
    }
    this.pending.clear();

    // Step 1: stdin end（通知子进程优雅退出）
    try {
      child.stdin?.end();
    } catch {
      // stdin may already be closed
    }

    // Step 2: 等待退出，10s 后 SIGTERM
    const exited = await this.waitForExit(child, GRACE_PERIOD_MS);
    if (exited) {
      this.setState('stopped');
      return;
    }

    // Step 3: SIGTERM
    try {
      child.kill('SIGTERM');
    } catch {
      // process may have exited
    }

    const exitedAfterTerm = await this.waitForExit(child, GRACE_PERIOD_MS);
    if (exitedAfterTerm) {
      this.setState('stopped');
      return;
    }

    // Step 4: SIGKILL
    try {
      child.kill('SIGKILL');
    } catch {
      // best effort
    }
    this.setState('stopped');
  }

  /** 调用 MCP 工具 */
  async callTool(params: ToolCallParams): Promise<ToolCallResult> {
    if (this.state !== 'running') {
      throw new Error(`MCP client is not running (state: ${this.state})`);
    }
    this.activeCalls++;
    try {
      const response = await this.sendRequest('tools/call', params, TOOL_CALL_TIMEOUT_MS);
      if (response.error) {
        throw new Error(`tools/call error: ${response.error.message}`);
      }
      return response.result as ToolCallResult;
    } finally {
      this.activeCalls--;
    }
  }

  /** 拉取工具列表（分页） */
  async listTools(): Promise<McpToolDefinition[]> {
    const allTools: McpToolDefinition[] = [];
    let cursor: string | undefined;
    do {
      const params = cursor ? { cursor } : {};
      const response = await this.sendRequest('tools/list', params, INITIALIZE_TIMEOUT_MS);
      if (response.error) {
        throw new Error(`tools/list error: ${response.error.message}`);
      }
      const result = response.result as ToolsListResult;
      allTools.push(...result.tools);
      cursor = result.nextCursor;
    } while (cursor);
    return allTools;
  }

  private async initialize(): Promise<void> {
    const params: InitializeParams = {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: CLIENT_INFO,
    };
    const response = await this.sendRequest('initialize', params, INITIALIZE_TIMEOUT_MS);
    if (response.error) {
      throw new Error(`initialize error: ${response.error.message}`);
    }
    const result = response.result as InitializeResult;
    // 发送 notifications/initialized 通知（借鉴 vscode McpServerRequestHandler）
    this.sendNotification('notifications/initialized', {});
    void result; // serverInfo 可选，P0 不校验 protocolVersion
  }

  private sendRequest(method: string, params: unknown, timeoutMs: number): Promise<JsonRpcResponse> {
    return new Promise((resolve, reject) => {
      if (!this.child?.stdin?.writable) {
        reject(new Error('MCP child process stdin is not writable'));
        return;
      }
      const id = this.nextId++;
      const request: JsonRpcRequest = {
        jsonrpc: '2.0',
        id,
        method,
        params,
      };
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`Request ${method} timed out after ${timeoutMs}ms`));
      }, timeoutMs);

      this.pending.set(id, { resolve, reject, timer });
      const line = JSON.stringify(request) + '\n';
      this.child.stdin.write(line, (err) => {
        if (err) {
          clearTimeout(timer);
          this.pending.delete(id);
          reject(new Error(`Failed to write request: ${err.message}`));
        }
      });
    });
  }

  private sendNotification(method: string, params: unknown): void {
    if (!this.child?.stdin?.writable) return;
    const notification: JsonRpcNotification = {
      jsonrpc: '2.0',
      method,
      params,
    };
    this.child.stdin.write(JSON.stringify(notification) + '\n');
  }

  private onStdoutData = (chunk: string): void => {
    this.buffer += chunk;
    let newlineIndex: number;
    while ((newlineIndex = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, newlineIndex).trim();
      this.buffer = this.buffer.slice(newlineIndex + 1);
      if (line.length === 0) continue;
      try {
        const message = JSON.parse(line) as JsonRpcResponse | JsonRpcNotification;
        if ('id' in message && message.id !== undefined) {
          // Response
          const pending = this.pending.get(Number(message.id));
          if (pending) {
            clearTimeout(pending.timer);
            this.pending.delete(Number(message.id));
            pending.resolve(message);
          }
        }
        // Notifications from server (e.g., notifications/tools/list_changed) — P0 忽略
      } catch {
        // 非 JSON 行，忽略（可能是 server 的 debug 输出）
      }
    }
  };

  private onStderrData = (chunk: Buffer): void => {
    // stderr 仅用于调试，不影响状态
    void chunk;
  };

  private onChildExit = (code: number | null): void => {
    // 取消所有进行中的请求
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(new Error(`MCP server exited with code ${code}`));
    }
    this.pending.clear();
    this.child = null;
    if (this.state !== 'stopped') {
      this.setState('error', `Process exited with code ${code}`);
    }
    this.onExit(code);
  };

  private onChildError = (error: Error): void => {
    this.setState('error', error.message);
  };

  private waitForExit(child: ChildProcess, timeoutMs: number): Promise<boolean> {
    return new Promise((resolve) => {
      if (child.exitCode !== null || child.signalCode !== null) {
        resolve(true);
        return;
      }
      const timer = setTimeout(() => {
        child.removeListener('exit', onExit);
        resolve(false);
      }, timeoutMs);
      const onExit = () => {
        clearTimeout(timer);
        resolve(true);
      };
      child.once('exit', onExit);
    });
  }
}

/** 生成唯一请求 ID 的备用方案（当前用自增数字，保留 UUID 以备未来需要） */
export function generateRequestId(): string {
  return randomUUID();
}
