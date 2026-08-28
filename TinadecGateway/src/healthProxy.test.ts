import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = originalFetch;
});

test('health merges Core payload with gateway identity and core_status ready', { concurrency: false }, async () => {
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({ name: 'tinadec-core', status: 'ok', version: '0.1.0', time: '2026-08-28T00:00:00Z' }),
      { status: 200, headers: { 'content-type': 'application/json' } }
    )) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/health'));
  assert.equal(response.status, 200);
  const body = await response.json() as Record<string, unknown>;
  assert.equal(body.gateway, 'ok');
  assert.equal(body.core_status, 'ready');
  assert.equal(body.name, 'tinadec-core');
  assert.equal(body.status, 'ok');
  assert.equal(body.mode, 'local');
});

test('health degrades to 503 with gateway fingerprint when Core is unreachable', { concurrency: false }, async () => {
  globalThis.fetch = (async () => {
    throw new Error('connect ECONNREFUSED 127.0.0.1:48731');
  }) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/health'));
  assert.equal(response.status, 503);
  assert.equal(response.headers.get('content-type'), 'application/json');
  const body = await response.json() as Record<string, unknown>;
  assert.equal(body.gateway, 'ok');
  assert.equal(body.core_status, 'unreachable');
  assert.equal(body.core_url, 'http://127.0.0.1:48731');
  assert.equal(body.mode, 'local');
});

test('health keeps ProblemDetails mapping for non-network Core errors', { concurrency: false }, async () => {
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ code: 'internal_error', message: 'boom' }), {
      status: 500,
      headers: { 'content-type': 'application/problem+json' }
    })) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/health'));
  assert.equal(response.status, 500);
  const body = await response.json() as Record<string, unknown>;
  assert.notEqual(body.gateway, 'ok');
});
