import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = originalFetch;
});

// Regression: the catch-all onError branch computed the inherited status but
// never assigned set.status, so Elysia answered with the route's staged status
// (default 200). An upload route that staged 201 and then failed on a
// non-JSON upstream body used to answer 201 with a ProblemDetails body — the
// failure looked like a success.
test('a failure after a route staged 201 is answered 500, not 201', { concurrency: false }, async () => {
  globalThis.fetch = (async () => new Response('', { status: 201 })) as typeof fetch;
  const response = await app.handle(new Request('http://gateway.local/api/v1/sessions/s-1/attachments?filename=a.png', {
    method: 'POST',
    headers: { 'content-type': 'application/octet-stream' },
    body: 'bytes',
  }));
  assert.equal(response.status, 500);
  const body = await response.json() as { code: string; status?: number };
  assert.equal(body.code, 'conflict');
});

// The SSE/stream proxies call fetch without the try/catch proxyJson has, so a
// Core that refuses the connection used to escape into the catch-all handler
// instead of an error status. The error mapper normalizes the body code, but
// the status and the problem+json contract must survive the trip.
test('an unreachable Core answers 502 on the run SSE route', { concurrency: false }, async () => {
  globalThis.fetch = (async () => { throw new Error('connection refused'); }) as typeof fetch;
  const response = await app.handle(new Request('http://gateway.local/api/v1/runs/run-1/stream'));
  assert.equal(response.status, 502);
  const body = await response.json() as { code: string };
  assert.equal(typeof body.code, 'string');
  assert.match(response.headers.get('content-type') ?? '', /application\/problem\+json/);
});
