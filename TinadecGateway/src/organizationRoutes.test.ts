import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';
import contract from './contracts/organization.openapi.json';

const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });

test('every organization, topology, approval-gate and environment operation forwards path, query and body, and keeps Core refusal codes', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: string | undefined; headers: Headers }> = [];
  // A Core refusal with a code a person can act on must reach the client unchanged, not as a generic conflict.
  const problem = { code: 'ambiguous_address', detail: 'Two members answer to search#1; address one by its handle.', trace_id: 'core-trace' };
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    calls.push({ path: url.pathname + url.search, method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : undefined, headers: new Headers(init?.headers) });
    return new Response(JSON.stringify(problem), { status: 409, headers: { 'content-type': 'application/problem+json' } });
  }) as typeof fetch;
  let expectedCount = 0;
  for (const [template, operations] of Object.entries(contract.paths)) {
    for (const method of Object.keys(operations)) {
      if (!['get', 'post', 'patch'].includes(method)) continue;
      expectedCount++;
      const path = template.replace(/\{[^}]+\}/g, '11111111-1111-1111-1111-111111111111') + '?after_sequence=4&limit=12';
      const body = method === 'get' ? undefined : JSON.stringify({ content: 'hello', client_message_id: 'c-1', mention: ['search#1'] });
      const response = await app.handle(new Request('http://gateway.local' + path, {
        method: method.toUpperCase(), body,
        headers: { 'content-type': 'application/json', 'x-request-id': 'org-request' },
      }));
      assert.equal(response.status, 409, `${method} ${template}`);
      // The Gateway shapes it as RFC 9457 but keeps what a person acts on: the code and the sentence.
      const received = await response.json() as Record<string, unknown>;
      assert.equal(received.code, problem.code);
      assert.equal(received.detail, problem.detail);
      assert.equal(received.trace_id, problem.trace_id);
      const call = calls.at(-1)!;
      assert.equal(call.path, path);
      assert.equal(call.method, method.toUpperCase());
      assert.equal(call.body, body);
      assert.equal(call.headers.get('x-request-id'), 'org-request');
    }
  }
  assert.equal(calls.length, expectedCount);
  // A contract-size pin: a projection that silently loses a Core operation fails here.
  assert.equal(Object.keys(contract.paths).length, 10);
  assert.equal(expectedCount, 12);
  assert.ok(contract.paths['/api/v1/approvals/{approvalId}/gates'], 'the delegated gates route is part of the contract');
  assert.ok(contract.paths['/api/v1/environments/{environmentId}'], 'the environment registry is part of the contract');
  assert.ok(contract.paths['/api/v1/sessions/{sessionId}/organization/members/{participantId}'], 'the per-member visibility route is part of the contract');
});

test('organization and approval-gate schemas are included in the external contract', async () => {
  const response = await app.handle(new Request('http://gateway.local/docs/json'));
  const doc = await response.json() as { paths: Record<string, unknown>; components: { schemas: Record<string, unknown> } };
  for (const path of Object.keys(contract.paths)) assert.ok(doc.paths[path], `Missing ${path}`);
  for (const name of Object.keys(contract.components.schemas)) assert.ok(doc.components.schemas[name], `Missing ${name}`);
  assert.ok(doc.components.schemas.ApprovalGatesDto);
  assert.ok(doc.components.schemas.EnvironmentDto);
});
