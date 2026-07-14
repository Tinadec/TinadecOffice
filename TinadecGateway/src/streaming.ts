/**
 * 流式 HTTP 代理：大文件传输与日志流。
 *
 * 用于大文件上传/下载和实时日志流的场景。
 * Gateway 透传请求和响应的 body 流，不做缓冲。
 */

import { coreEndpoint } from './coreClient.js';
import { toolRuntimeEndpoint } from './toolRuntimeClient.js';

export type StreamTarget = 'core' | 'tool_runtime';

export interface StreamProxyOptions {
  target: StreamTarget;
  path: string;
  method?: string;
  headers?: Record<string, string>;
  body?: ReadableStream<Uint8Array> | null;
}

/**
 * 代理流式 HTTP 请求到 Core 或 Tool Runtime。
 * 返回原始 Response，调用方可直接透传 body 流给客户端。
 */
export async function proxyStream(options: StreamProxyOptions): Promise<Response> {
  const url = options.target === 'core'
    ? coreEndpoint(options.path)
    : toolRuntimeEndpoint(options.path);

  return fetch(url, {
    method: options.method ?? 'GET',
    headers: options.headers ?? {},
    body: options.body ?? undefined,
    // @ts-expect-error: Bun 支持 duplex 选项用于流式请求体
    duplex: options.body ? 'half' : undefined,
  });
}

/**
 * 设置流式响应头。
 */
export function setStreamHeaders(
  set: { headers: Record<string, string> },
  sourceResponse: Response,
): void {
  const contentType = sourceResponse.headers.get('content-type');
  if (contentType) {
    set.headers['content-type'] = contentType;
  }
  set.headers['cache-control'] = 'no-cache';
  set.headers['connection'] = 'keep-alive';
  set.headers['x-accel-buffering'] = 'no';
}
