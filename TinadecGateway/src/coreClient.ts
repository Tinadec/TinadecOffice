/**
 * Core 客户端：Gateway 到 Core 的 HTTP/SSE 代理。
 *
 * 从 config.ts 读取 Core URL，支持 JSON 代理和 SSE 流式代理。
 */

import { getConfig } from './config.js';
import { PRINCIPAL_VALUE, ensureRequestId } from './headers.js';
import { rethrowCancellation, upstreamUnreachableResponse } from './upstreamFailure.js';
import { createStreamProxyResponse } from './streamResponse.js';

export type ProxyBody = Record<string, unknown> | string | undefined;

export interface ProxyOptions {
  method?: string;
  body?: ProxyBody;
  headers?: HeadersInit;
  /** Abort the upstream request when the downstream client disconnects. */
  signal?: AbortSignal;
}

export interface ProxyResult {
  status: number;
  data: unknown;
  headers?: Headers;
}

/** For transport surfaces, preserve the Core status, headers, and bytes. */
export async function proxyRaw(path: string, options: ProxyOptions = {}): Promise<Response> {
  const body = typeof options.body === 'string'
    ? options.body
    : options.body === undefined
      ? undefined
      : JSON.stringify(options.body);
  const baseHeaders = proxyBaseHeaders(options.headers);
  try {
    return await fetch(coreEndpoint(path), {
      method: options.method ?? 'GET',
      headers: {
        ...(body ? { 'content-type': 'application/json' } : {}),
        ...baseHeaders,
        ...options.headers,
      },
      body,
      signal: options.signal,
      redirect: 'manual',
    });
  } catch (error) {
    rethrowCancellation(error, options.signal);
    const message = error instanceof Error ? error.message : 'Network request failed';
    return new Response(JSON.stringify({ code: 'CORE_UNREACHABLE', message: `Cannot reach Core at ${coreUrl()}: ${message}` }), {
      status: 502,
      headers: { 'content-type': 'application/problem+json' },
    });
  }
}

function proxyBaseHeaders(incoming?: HeadersInit): Record<string, string> {
  let incomingRequestId: string | null = null;
  let storageId: string | null = null;
  let hostControl: string | null = null;
  if (incoming) {
    const h = new Headers(incoming as HeadersInit);
    incomingRequestId = h.get('x-request-id') ?? h.get('X-Request-Id');
    storageId = h.get('x-tinadec-storage-id');
    hostControl = h.get('x-tinadec-host-control');
  }
  return {
    'x-request-id': ensureRequestId(incomingRequestId),
    'x-tinadec-principal': PRINCIPAL_VALUE,
    ...(storageId ? { 'x-tinadec-storage-id': storageId } : {}),
    ...(hostControl ? { 'x-tinadec-host-control': hostControl } : {}),
  };
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

  const baseHeaders = proxyBaseHeaders(options.headers);
  let response: Response;
  try {
    response = await fetch(coreEndpoint(path), {
      method: options.method ?? 'GET',
      headers: {
        accept: 'application/json',
        ...(body ? { 'content-type': 'application/json' } : {}),
        ...baseHeaders,
        ...options.headers
      },
      body,
      signal: options.signal,
      redirect: 'manual',
    });
  } catch (err) {
    rethrowCancellation(err, options.signal);
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
    data,
    headers: response.headers
  };
}

/**
 * 代理 SSE 请求到 Core。
 * 保留 id/event/retry/heartbeat 顺序；支持 Last-Event-ID / ?cursor= 续接
 * 注入 X-Request-Id + X-Tinadec-Principal
 */
export async function proxySse(path: string, init?: RequestInit): Promise<Response> {
  const baseHeaders = proxyBaseHeaders(init?.headers as HeadersInit | undefined);
  const url = coreEndpoint(path);
  try {
    return createStreamProxyResponse(await fetch(url, {
      ...init,
      redirect: 'manual',
      headers: {
        accept: 'text/event-stream',
        ...baseHeaders,
        ...(init?.headers ?? {})
      }
    }));
  } catch (error) {
    rethrowCancellation(error, init?.signal);
    return upstreamUnreachableResponse('core', url, error);
  }
}

export async function proxySseWithCursor(
  path: string,
  cursor: string | null,
  incomingHeaders?: HeadersInit,
  extraInit?: RequestInit
): Promise<Response> {
  const headers: Record<string, string> = {
    accept: 'text/event-stream',
    ...proxyBaseHeaders(incomingHeaders),
  };
  if (cursor) headers['last-event-id'] = cursor;
  // also forward as query if path doesn't already contain cursor
  let url = path;
  if (cursor && !path.includes('cursor=') && !path.includes('after_seq') && !path.includes('afterSeq')) {
    const sep = path.includes('?') ? '&' : '?';
    url = `${path}${sep}cursor=${encodeURIComponent(cursor)}`;
  }
  const endpoint = coreEndpoint(url);
  try {
    return createStreamProxyResponse(await fetch(endpoint, {
      ...extraInit,
      redirect: 'manual',
      headers: {
        ...headers,
        ...(extraInit?.headers as Record<string, string> | undefined)
      }
    }));
  } catch (error) {
    rethrowCancellation(error, extraInit?.signal);
    return upstreamUnreachableResponse('core', endpoint, error);
  }
}

/**
 * 代理流式 HTTP 请求到 Core。
 * 用于大文件和日志的流式传输。
 */
export async function proxyStream(path: string, init?: RequestInit): Promise<Response> {
  const baseHeaders = proxyBaseHeaders(init?.headers as HeadersInit | undefined);
  const url = coreEndpoint(path);
  try {
    return createStreamProxyResponse(await fetch(url, {
      ...init,
      redirect: 'manual',
      headers: {
        ...baseHeaders,
        ...(init?.headers as Record<string, string> | undefined)
      }
    }));
  } catch (error) {
    rethrowCancellation(error, init?.signal);
    return upstreamUnreachableResponse('core', url, error);
  }
}
