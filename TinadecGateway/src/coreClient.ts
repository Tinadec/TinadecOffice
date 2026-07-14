/**
 * Core 客户端：Gateway 到 Core 的 HTTP/SSE 代理。
 *
 * 从 config.ts 读取 Core URL，支持 JSON 代理和 SSE 流式代理。
 */

import { getConfig } from './config.js';

export type ProxyBody = Record<string, unknown> | string | undefined;

export interface ProxyOptions {
  method?: string;
  body?: ProxyBody;
  headers?: HeadersInit;
}

export interface ProxyResult {
  status: number;
  data: unknown;
}

/** Core 服务 URL（从配置读取） */
export function coreUrl(): string {
  return getConfig().coreUrl;
}

/** 构建 Core 完整端点 URL */
export function coreEndpoint(path: string): string {
  return new URL(path, coreUrl()).toString();
}

/**
 * 代理 JSON 请求到 Core。
 *
 * 返回 ProxyResult，包含 HTTP 状态和解析后的 JSON 数据。
 * 如果 Core 不可达或返回非 JSON 响应，返回 502 错误。
 */
export async function proxyJson(path: string, options: ProxyOptions = {}): Promise<ProxyResult> {
  const body = typeof options.body === 'string'
    ? options.body
    : options.body === undefined
      ? undefined
      : JSON.stringify(options.body);

  let response: Response;
  try {
    response = await fetch(coreEndpoint(path), {
      method: options.method ?? 'GET',
      headers: {
        accept: 'application/json',
        ...(body ? { 'content-type': 'application/json' } : {}),
        ...options.headers
      },
      body
    });
  } catch (err) {
    const msg = err instanceof Error ? err.message : 'Network request failed';
    return {
      status: 502,
      data: {
        code: 'CORE_UNREACHABLE',
        message: `Cannot reach Core at ${coreUrl()}: ${msg}`
      }
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
          code: 'CORE_INVALID_RESPONSE',
          message: `Core returned a non-JSON response: ${text.substring(0, 200)}`
        }
      };
    }
  }

  return {
    status: response.status,
    data
  };
}

/**
 * 代理 SSE 请求到 Core。
 * 返回原始 Response，调用方可读取 body 流。
 */
export async function proxySse(path: string, init?: RequestInit): Promise<Response> {
  return fetch(coreEndpoint(path), {
    ...init,
    headers: {
      accept: 'text/event-stream',
      ...(init?.headers ?? {})
    }
  });
}

/**
 * 代理流式 HTTP 请求到 Core。
 * 用于大文件和日志的流式传输。
 */
export async function proxyStream(path: string, init?: RequestInit): Promise<Response> {
  return fetch(coreEndpoint(path), init);
}
