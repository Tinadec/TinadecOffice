import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });

test('Phase 11 context-memory, documents, item ledger, and recall routes proxy verbatim with headers and query strings', async () => {
  const calls: { url: string; method: string; body: unknown; ifMatch: string | null }[] = [];
  globalThis.fetch = (async (input, init) => {
    calls.push({
      url: String(input),
      method: init?.method ?? 'GET',
      body: init?.body ? JSON.parse(String(init.body)) : null,
      ifMatch: new Headers(init?.headers).get('if-match'),
    });
    const status = init?.method === 'POST' && String(input).endsWith('/api/v1/memories/items') ? 201 : 200;
    return Response.json({ ok: true }, { status, headers: { etag: '"sha-hash-2"' } });
  }) as typeof fetch;

  const cases: readonly [string, string, Record<string, unknown> | undefined, number][] = [
    ['GET', '/api/v1/context-memory/policy', undefined, 200],
    ['PUT', '/api/v1/context-memory/policy', { strategy: 'deep_curator', core_markdown_max_tokens: 2048 }, 200],
    ['GET', '/api/v1/memories/documents/companion?companion_id=11111111-1111-1111-1111-111111111111', undefined, 200],
    ['PUT', '/api/v1/memories/documents/project?project_id=22222222-2222-2222-2222-222222222222', { markdown: '# Project Core' }, 200],
    ['GET', '/api/v1/memories/items?scope=project&include_versions=true&limit=25', undefined, 200],
    ['POST', '/api/v1/memories/items', { scope: 'principal', kind: 'preference', content: 'Prefer bun test.' }, 201],
    ['DELETE', '/api/v1/memories/items/33333333-3333-3333-3333-333333333333?reason=obsolete', undefined, 200],
    ['POST', '/api/v1/memories/items/33333333-3333-3333-3333-333333333333/revert', { target_version: 1, reason: 'undo' }, 200],
    ['POST', '/api/v1/sessions/44444444-4444-4444-4444-444444444444/recall', { query: 'OpenAPI snapshot', cross_session: true, fidelity: 'balanced' }, 200],
  ];

  for (const [method, path, body, expectedStatus] of cases) {
    const response = await app.handle(new Request(`http://gateway.local${path}`, {
      method,
      headers: { 'content-type': 'application/json', 'if-match': '"sha-hash-1"' },
      ...(body ? { body: JSON.stringify(body) } : {}),
    }));
    assert.equal(response.status, expectedStatus, `${method} ${path} -> ${response.status}`);
    assert.equal(response.headers.get('etag'), '"sha-hash-2"');
    const last = calls.at(-1)!;
    assert.equal(last.url, `http://127.0.0.1:48731${path}`);
    assert.equal(last.method, method);
    assert.equal(last.ifMatch, '"sha-hash-1"');
    assert.deepEqual(last.body, body ?? null);
  }
});
