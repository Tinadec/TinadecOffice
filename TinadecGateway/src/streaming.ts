/**
 * 流式 HTTP 代理：大文件传输与日志流。
 *
 * 用于大文件上传/下载和实时日志流的场景。
 * Gateway 透传请求和响应的 body 流，不做缓冲。
 */

import { coreEndpoint, isCancellationError, rethrowCancellation } from './coreClient.js';
import { toolRuntimeEndpoint } from './toolRuntimeClient.js';
import { upstreamUnreachableResponse } from './upstreamFailure.js';

export type StreamTarget = 'core' | 'tool_runtime';

export interface StreamProxyOptions {
  target: StreamTarget;
  path: string;
  method?: string;
  headers?: Record<string, string>;
  body?: ReadableStream<Uint8Array> | null;
  /** Abort the upstream stream when the downstream client disconnects. */
  signal?: AbortSignal;
}

/**
 * 代理流式 HTTP 请求到 Core 或 Tool Runtime。
 * 返回原始 Response，调用方可直接透传 body 流给客户端。
 */
export async function proxyStream(options: StreamProxyOptions): Promise<Response> {
  const url = options.target === 'core'
    ? coreEndpoint(options.path)
    : toolRuntimeEndpoint(options.path);

  try {
    return await fetch(url, {
      method: options.method ?? 'GET',
      headers: options.headers ?? {},
      body: options.body ?? undefined,
      signal: options.signal,
      // @ts-expect-error: Bun 支持 duplex 选项用于流式请求体
      duplex: options.body ? 'half' : undefined,
    });
  } catch (error) {
    rethrowCancellation(error, options.signal);
    return upstreamUnreachableResponse(options.target === 'core' ? 'core' : 'toolRuntime', url, error);
  }
}

/**
 * 设置流式响应头。
 */
export function setStreamHeaders(
  set: { headers: Record<string, string | number> },
  sourceResponse: Response,
): void {
  const contentType = sourceResponse.headers.get('content-type');
  if (contentType) {
    set.headers['content-type'] = contentType;
  }
  set.headers['cache-control'] = 'no-cache, no-transform';
  set.headers['connection'] = 'keep-alive';
  set.headers['x-accel-buffering'] = 'no';
}

const HOP_BY_HOP_SSE_HEADERS = new Set([
  'connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'te',
  'trailer',
  'transfer-encoding',
  'upgrade',
  'content-length',
  'content-encoding',
]);

/**
 * Build headers for a proxied SSE Response, preserving CORS/telemetry headers
 * populated on Elysia's `set.headers` while stripping HTTP/1.1 hop-by-hop
 * headers (`transfer-encoding`, `connection`, etc.) from the upstream response.
 */
export function buildSseResponseHeaders(
  sourceResponse: Response,
  setHeaders?: Record<string, string | number>,
): Headers {
  const headers = new Headers();
  sourceResponse.headers.forEach((value, key) => {
    if (!HOP_BY_HOP_SSE_HEADERS.has(key.toLowerCase())) {
      headers.set(key, value);
    }
  });
  if (setHeaders) {
    for (const [key, value] of Object.entries(setHeaders)) {
      if (!HOP_BY_HOP_SSE_HEADERS.has(key.toLowerCase())) {
        headers.set(key, String(value));
      }
    }
  }
  headers.set('content-type', sourceResponse.headers.get('content-type') ?? 'text/event-stream');
  headers.set('cache-control', 'no-cache, no-transform');
  headers.set('x-accel-buffering', 'no');
  return headers;
}

/**
 * Wrap an upstream SSE response with periodic keepalive comments and clean headers.
 */
export function createSseResponse(
  sourceResponse: Response,
  signal: AbortSignal,
  setHeaders?: Record<string, string | number>,
  intervalMs = 15_000,
): Response {
  return new Response(wrapSseWithKeepalive(sourceResponse.body, intervalMs, signal), {
    status: sourceResponse.status,
    headers: buildSseResponseHeaders(sourceResponse, setHeaders),
  });
}

const SSE_KEEPALIVE_CHUNK = new TextEncoder().encode(': keepalive\n\n');

/**
 * Emit a `: keepalive\n\n` SSE comment whenever an upstream SSE stream stays silent
 * for `intervalMs` (default 15s, safely below Cloudflare's 100s idle HTTP timeout).
 */
export function wrapSseWithKeepalive(
  body: ReadableStream<Uint8Array> | null,
  intervalMs = 15_000,
  signal?: AbortSignal,
): ReadableStream<Uint8Array> | null {
  if (!body) return null;
  let reader: ReadableStreamDefaultReader<Uint8Array> | null = null;
  let timer: ReturnType<typeof setInterval> | null = null;
  let lastActivityAt = Date.now();
  let closed = false;
  let onAbort: (() => void) | null = null;

  const cleanup = () => {
    closed = true;
    if (signal && onAbort) {
      signal.removeEventListener('abort', onAbort);
      onAbort = null;
    }
    if (timer) {
      clearInterval(timer);
      timer = null;
    }
    try {
      void reader?.cancel().catch(() => {
        // Ignore AbortError / closed stream rejection when tearing down SSE readers.
      });
    } catch {
      // ignore
    }
  };

  return new ReadableStream<Uint8Array>({
    start(controller) {
      reader = body.getReader();
      if (signal) {
        if (signal.aborted) {
          cleanup();
          try { controller.close(); } catch { /* ignore */ }
          return;
        }
        onAbort = () => {
          cleanup();
          try { controller.close(); } catch { /* ignore */ }
        };
        signal.addEventListener('abort', onAbort, { once: true });
      }

      timer = setInterval(() => {
        if (closed) return;
        if (Date.now() - lastActivityAt >= intervalMs) {
          try {
            controller.enqueue(SSE_KEEPALIVE_CHUNK);
            lastActivityAt = Date.now();
          } catch {
            cleanup();
          }
        }
      }, Math.min(intervalMs, 5_000));

      void (async () => {
        try {
          while (!closed) {
            const { done, value } = await reader!.read();
            if (done) break;
            if (value && value.length > 0) {
              lastActivityAt = Date.now();
              controller.enqueue(value);
            }
          }
          cleanup();
          try { controller.close(); } catch { /* ignore */ }
        } catch (err) {
          const cancelled = closed || isCancellationError(err, signal);
          cleanup();
          if (cancelled) {
            try { controller.close(); } catch { /* ignore */ }
          } else {
            try { controller.error(err); } catch { /* ignore */ }
          }
        }
      })();
    },
    cancel() {
      cleanup();
    },
  });
}
