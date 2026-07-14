/**
 * Tool Runtime 客户端：Gateway 到 Tool Runtime 的 HTTP/SSE 代理。
 *
 * Gateway 不直接执行文件、Git、Shell、PTY 或 MCP 操作。
 * 所有工具执行请求都通过此客户端代理到 Tool Runtime 服务。
 *
 * 协议：
 *   - HTTP/JSON：普通命令与查询
 *   - SSE：工具执行事件流
 *   - 流式 HTTP：大文件传输与日志
 */

import { getConfig } from './config.js';

export type ToolRuntimeBody = Record<string, unknown> | string | undefined;

export interface ToolRuntimeOptions {
  method?: string;
  body?: ToolRuntimeBody;
  headers?: HeadersInit;
}

export interface ToolRuntimeResult {
  status: number;
  data: unknown;
}

/** Tool Runtime 基础 URL */
export function toolRuntimeUrl(): string {
  return getConfig().toolRuntimeUrl;
}

/** 构造 Tool Runtime 完整端点 URL */
export function toolRuntimeEndpoint(path: string): string {
  return new URL(path, toolRuntimeUrl()).toString();
}

/**
 * 代理 JSON 请求到 Tool Runtime。
 *
 * 返回 ToolRuntimeResult，包含 HTTP 状态和解析后的 JSON 数据。
 * 如果 Tool Runtime 不可达或返回非 JSON 响应，返回 502 错误。
 */
export async function proxyToolRuntimeJson(
  path: string,
  options: ToolRuntimeOptions = {},
): Promise<ToolRuntimeResult> {
  const body =
    typeof options.body === 'string'
      ? options.body
      : options.body === undefined
        ? undefined
        : JSON.stringify(options.body);

  let response: Response;
  try {
    response = await fetch(toolRuntimeEndpoint(path), {
      method: options.method ?? 'GET',
      headers: {
        accept: 'application/json',
        ...(body ? { 'content-type': 'application/json' } : {}),
        ...options.headers,
      },
      body,
    });
  } catch (err) {
    const msg = err instanceof Error ? err.message : 'Network request failed';
    return {
      status: 502,
      data: {
        code: 'TOOL_RUNTIME_UNREACHABLE',
        message: `Cannot reach Tool Runtime at ${toolRuntimeUrl()}: ${msg}`,
      },
    };
  }

  const text = await response.text();
  let data: unknown = null;
  if (text.length > 0) {
    try {
      data = JSON.parse(text);
    } catch {
      return {
        status: 502,
        data: {
          code: 'TOOL_RUNTIME_INVALID_RESPONSE',
          message: `Tool Runtime returned a non-JSON response: ${text.substring(0, 200)}`,
        },
      };
    }
  }

  return {
    status: response.status,
    data,
  };
}

/**
 * 代理 SSE 请求到 Tool Runtime。
 * 返回原始 Response，调用方可读取 body 流。
 */
export async function proxyToolRuntimeSse(
  path: string,
  init?: RequestInit,
): Promise<Response> {
  return fetch(toolRuntimeEndpoint(path), {
    ...init,
    headers: {
      accept: 'text/event-stream',
      ...(init?.headers ?? {}),
    },
  });
}

/**
 * 对流式 HTTP 请求到 Tool Runtime。
 * 返回原始 Response，调用方可透传 body 给客户端。
 */
export async function proxyToolRuntimeStream(
  path: string,
  init?: RequestInit,
): Promise<Response> {
  return fetch(toolRuntimeEndpoint(path), init);
}
