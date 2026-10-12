/**
 * 回归：#30 —— 上游不可达必须报 502，且失败不得被报成 2xx。
 *
 * 已核实的版本事实（elysia 1.4.29，`dist/compose.js` 的路由 catch）：
 *   `if(!set.status||set.status<300)set.status=error?.status||500`
 * 路由暂存的 2xx/3xx 在抛错时会被 Elysia 自己钳成 500，所以 #30 描述的
 * "HTTP 201 + ProblemDetails 错误体" 在本版本上不可复现 —— 前两条用例是把这条
 * 平台行为钉住（lock-in），不是修复前会失败的复现用例。
 *
 * 真正被本次改动修掉的是后两条：SSE / 流式代理此前是裸 `fetch`，Core 不可达时异常
 * 逃逸进 catch-all，客户端收到的是 500（分不清"网关坏了"与"上游没起来"），而不是 502。
 */
import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = originalFetch;
});

test('lock-in: a route that staged 201 and then failed answers 500, not 201', { concurrency: false }, async () => {
  // Core answers 201 with an HTML error page, so `response.json()` throws after the route
  // has already staged the success status.
  globalThis.fetch = (async () =>
    new Response('<html><body>bad gateway from an intermediate layer</body></html>', {
      status: 201,
      headers: { 'content-type': 'text/html' },
    })) as unknown as typeof fetch;

  const response = await app.handle(
    new Request('http://gateway.local/api/v1/sessions/sess-1/attachments?filename=a.txt', {
      method: 'POST',
      headers: { 'content-type': 'application/octet-stream' },
      body: 'hello',
    }),
  );

  assert.notEqual(response.status, 201, 'a failed upload must never answer 201');
  assert.equal(response.status, 500);
  assert.equal(response.headers.get('content-type'), 'application/problem+json');
  const body = await response.json() as Record<string, unknown>;
  assert.equal(body.status, response.status, 'ProblemDetails.status must agree with the HTTP status');
});

test('lock-in: a route that staged 413 and then failed keeps 413', { concurrency: false }, async () => {
  globalThis.fetch = (async () =>
    new Response('<html>payload too large</html>', {
      status: 413,
      headers: { 'content-type': 'text/html' },
    })) as unknown as typeof fetch;

  const response = await app.handle(
    new Request('http://gateway.local/api/v1/sessions/sess-1/attachments', {
      method: 'POST',
      headers: { 'content-type': 'application/octet-stream' },
      body: 'hello',
    }),
  );

  assert.equal(response.status, 413);
  const body = await response.json() as Record<string, unknown>;
  assert.equal(body.status, 413);
});

test('an unreachable Core on an SSE route answers 502 ProblemDetails', { concurrency: false }, async () => {
  globalThis.fetch = (async () => {
    throw new Error('connect ECONNREFUSED 127.0.0.1:48731');
  }) as unknown as typeof fetch;

  const response = await app.handle(
    new Request('http://gateway.local/api/v1/runs/run-1/stream'),
  );

  assert.equal(response.status, 502);
  assert.equal(response.headers.get('content-type'), 'application/problem+json');
  const body = await response.json() as Record<string, unknown>;
  assert.equal(body.status, 502);
  assert.match(String(body.detail ?? body.message), /Cannot reach Core at/);
});

test('an unreachable Core on a streaming download answers 502, not 500', { concurrency: false }, async () => {
  globalThis.fetch = (async () => {
    throw new Error('connect ECONNREFUSED 127.0.0.1:48731');
  }) as unknown as typeof fetch;

  const response = await app.handle(
    new Request('http://gateway.local/api/v1/attachments/att-1/content'),
  );

  assert.equal(response.status, 502);
  const body = await response.json() as Record<string, unknown>;
  assert.equal(body.code, 'CORE_UNREACHABLE');
  assert.equal(body.status, 502);
  assert.match(String(body.message), /Cannot reach Core at/);
});
