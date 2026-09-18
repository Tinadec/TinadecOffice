import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';
import contract from './contracts/tina-chat.openapi.json';

const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });

test('every TinaChat operation preserves Core path, query, JSON, scope headers and errors', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: string | undefined; headers: Headers }> = [];
  const problem = { code: 'tina_chat_forbidden', detail: 'Recipient cannot read the original message.', trace_id: 'core-trace' };
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    calls.push({ path: url.pathname + url.search, method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : undefined, headers: new Headers(init?.headers) });
    return new Response(JSON.stringify(problem), { status: 403, headers: { 'content-type': 'application/problem+json', etag: '"5"', 'cache-control': 'no-store' } });
  }) as typeof fetch;
  let expectedCount = 0;
  for (const [template, operations] of Object.entries(contract.paths)) {
    for (const method of Object.keys(operations)) {
      if (!['get', 'post', 'put', 'patch'].includes(method)) continue;
      expectedCount++;
      const path = template.replace(/\{[^}]+\}/g, '11111111-1111-1111-1111-111111111111') + '?actor_id=actor-1&after_sequence=4&limit=12';
      const body = method === 'get' ? undefined : JSON.stringify({ actor_id: 'actor-1', expected_revision: 7, content: 'Ambiguous user wording', audience_participant_ids: ['receiver'] });
      const response = await app.handle(new Request('http://gateway.local' + path, {
        method: method.toUpperCase(), body,
        headers: { 'content-type': 'application/json', 'x-request-id': 'tina-request', 'x-tenant-id': 'tenant-1', 'if-match': '"7"', 'idempotency-key': 'retry-1' },
      }));
      assert.equal(response.status, 403);
      assert.deepEqual(await response.json(), problem);
      assert.equal(response.headers.get('etag'), '"5"');
      assert.equal(response.headers.get('cache-control'), 'no-store');
      const call = calls.at(-1)!;
      assert.equal(call.path, path);
      assert.equal(call.method, method.toUpperCase());
      assert.equal(call.body, body);
      assert.equal(call.headers.get('x-request-id'), 'tina-request');
      assert.equal(call.headers.get('if-match'), '"7"');
      assert.equal(call.headers.get('idempotency-key'), 'retry-1');
    }
  }
  assert.equal(calls.length, expectedCount);
  assert.ok(expectedCount >= 20);
});

test('TinaChat acknowledgement preserves an empty 204 response', { concurrency: false }, async () => {
  globalThis.fetch = (async () => new Response(null, { status: 204 })) as unknown as typeof fetch;
  const response = await app.handle(new Request('http://gateway.local/api/v1/tina-chat/participants/p/inbox/m/ack', { method: 'POST' }));
  assert.equal(response.status, 204);
  assert.equal(await response.text(), '');
});

test('TinaChat schemas and operations are included in the external contract', async () => {
  const response = await app.handle(new Request('http://gateway.local/docs/json'));
  const doc = await response.json() as { paths: Record<string, unknown>; components: { schemas: Record<string, unknown> } };
  for (const path of Object.keys(contract.paths)) assert.ok(doc.paths[path], `Missing ${path}`);
  for (const name of Object.keys(contract.components.schemas)) assert.ok(doc.components.schemas[name], `Missing ${name}`);
  // Generated Core 3.1 schemas must not leak type arrays or the null type
  // into Gateway's 3.0.3 document. Check every nested path and schema.
  function assertOpenApi30(value: unknown): void {
    if (!value || typeof value !== 'object') return;
    const record = value as Record<string, unknown>;
    assert.equal(Array.isArray(record.type), false, 'OpenAPI 3.0 type must be a string');
    assert.notEqual(record.type, 'null', 'OpenAPI 3.0 uses nullable');
    Object.values(record).forEach(assertOpenApi30);
  }
  assertOpenApi30(contract);
  assert.equal(contract.components.schemas.TinaChatParticipantDto.properties.job_title.nullable, true);
});
