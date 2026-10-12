/**
 * 上游不可达时的统一失败响应。
 *
 * `proxyJson` / `proxyToolRuntimeJson` 早就会在 fetch 抛错时回 502，但 SSE 与流式代理一直
 * 把 fetch 的 promise 原样返回：连接被拒会逃逸进全局 `onError`，而 `onError` 当时会沿用
 * 路由已经暂存的状态码——附件上传就是这样变成 "HTTP 201 + 错误体" 的假成功。
 */

const UPSTREAM = {
  core: { label: 'Core', code: 'CORE_UNREACHABLE' },
  toolRuntime: { label: 'Tool Runtime', code: 'TOOL_RUNTIME_UNREACHABLE' },
} as const;

export type Upstream = keyof typeof UPSTREAM;

/** Caller cancellation is not an upstream availability failure. */
export function rethrowCancellation(error: unknown, signal?: AbortSignal | null): void {
  if (signal?.aborted || (error !== null && typeof error === 'object' && 'name' in error
    && error.name === 'AbortError')) throw error;
}

/**
 * 构造 502 ProblemDetails 响应；调用方负责把它交给 `setStatus`。
 */
export function upstreamUnreachableResponse(upstream: Upstream, url: string, error: unknown): Response {
  const { label, code } = UPSTREAM[upstream];
  const message = error instanceof Error ? error.message : 'Network request failed';
  const detail = `Cannot reach ${label} at ${url}: ${message}`;
  // 这里刻意不走 `toProblemDetails`：它会用 `normalizeCode` 把未知 code 重写成 `conflict`，
  // 而 `CORE_UNREACHABLE` / `TOOL_RUNTIME_UNREACHABLE` 是调用方（含桌面端服务发现探针读
  // 网关健康体）用来分辨"网关在、上游不在"的指纹，必须原样保留、由路由决定是否再映射。
  // `message` 与 `proxyRaw` 的既有 502 体保持一致，便于只读 `message` 的调用方继续工作。
  const body = {
    type: `https://tinadec.dev/errors/${code}`,
    title: code,
    status: 502,
    detail,
    code,
    message: detail,
  };
  return new Response(JSON.stringify(body), {
    status: 502,
    headers: { 'content-type': 'application/problem+json' },
  });
}
