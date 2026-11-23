import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = originalFetch;
});

const runId = '11111111-2222-3333-4444-555555555555';
const proposalId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';

test('P1 context-diagnostics and audit-overlap routes proxy path, query, headers, body, and status verbatim', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: string | undefined; headers: Headers }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    calls.push({
      path: url.pathname + url.search,
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    });
    return new Response(JSON.stringify({ run_id: runId, status: 'completed' }), {
      status: 200,
      headers: { 'content-type': 'application/json' },
    });
  }) as typeof fetch;

  const diagRes = await app.handle(
    new Request(
      `http://gateway.local/api/v1/runs/${runId}/context-diagnostics?context_window_tokens=128000&selected_pack_id=pack-1`,
      { headers: { 'x-tinadec-storage-id': 'scope-diag' } },
    ),
  );
  assert.equal(diagRes.status, 200);
  assert.equal(
    calls.at(-1)!.path,
    `/api/v1/runs/${runId}/context-diagnostics?context_window_tokens=128000&selected_pack_id=pack-1`,
  );
  assert.equal(calls.at(-1)!.method, 'GET');
  assert.equal(calls.at(-1)!.headers.get('x-tinadec-storage-id'), 'scope-diag');

  const auditPayload = {
    project_id: '77777777-7777-7777-7777-777777777777',
    k_gram_size: 64,
    window_size: 16,
    extra_sources: [
      { id: 'custom:agents_md', label: 'AGENTS.md', kind: 'workspace_instructions', content: 'Rule line' },
    ],
  };
  const overlapRes = await app.handle(
    new Request('http://gateway.local/api/v1/context/audit-overlap', {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-tinadec-storage-id': 'scope-audit' },
      body: JSON.stringify(auditPayload),
    }),
  );
  assert.equal(overlapRes.status, 200);
  assert.equal(calls.at(-1)!.path, '/api/v1/context/audit-overlap');
  assert.equal(calls.at(-1)!.method, 'POST');
  assert.equal(calls.at(-1)!.headers.get('x-tinadec-storage-id'), 'scope-audit');
  assert.deepEqual(JSON.parse(calls.at(-1)!.body ?? '{}'), auditPayload);
});

test('P2 context-evolution proposal list, evaluation, apply, and reject routes proxy cleanly including 409/502 errors', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: string | undefined; headers: Headers }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    calls.push({
      path: url.pathname + url.search,
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    });
    return new Response(
      JSON.stringify({ items: [], total_count: 0, pending_count: 0, auto_tune_low_risk_context_policy: false }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  }) as typeof fetch;

  const listRes = await app.handle(
    new Request('http://gateway.local/api/v1/context-evolution/proposals?status=pending&kind=context_policy_proposal', {
      headers: { 'x-tinadec-storage-id': 'scope-evol' },
    }),
  );
  assert.equal(listRes.status, 200);
  assert.equal(calls.at(-1)!.path, '/api/v1/context-evolution/proposals?status=pending&kind=context_policy_proposal');
  assert.equal(calls.at(-1)!.method, 'GET');
  assert.equal(calls.at(-1)!.headers.get('x-tinadec-storage-id'), 'scope-evol');

  const getEvalRes = await app.handle(
    new Request(`http://gateway.local/api/v1/context-evolution/proposals/${proposalId}/evaluation`),
  );
  assert.equal(getEvalRes.status, 200);
  assert.equal(calls.at(-1)!.path, `/api/v1/context-evolution/proposals/${proposalId}/evaluation`);
  assert.equal(calls.at(-1)!.method, 'GET');

  const applyBody = { reason: 'Approved after reviewing 3/5 degraded runs', expected_content_hash: 'abc123' };
  const applyRes = await app.handle(
    new Request(`http://gateway.local/api/v1/context-evolution/proposals/${proposalId}/apply`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'if-match': '"abc123"' },
      body: JSON.stringify(applyBody),
    }),
  );
  assert.equal(applyRes.status, 200);
  assert.equal(calls.at(-1)!.path, `/api/v1/context-evolution/proposals/${proposalId}/apply`);
  assert.equal(calls.at(-1)!.method, 'POST');
  assert.equal(calls.at(-1)!.headers.get('if-match'), '"abc123"');
  assert.deepEqual(JSON.parse(calls.at(-1)!.body ?? '{}'), applyBody);

  const rejectRes = await app.handle(
    new Request(`http://gateway.local/api/v1/context-evolution/proposals/${proposalId}/reject`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ reason: 'False positive overlap' }),
    }),
  );
  assert.equal(rejectRes.status, 200);
  assert.equal(calls.at(-1)!.path, `/api/v1/context-evolution/proposals/${proposalId}/reject`);
  assert.equal(calls.at(-1)!.method, 'POST');
  assert.deepEqual(JSON.parse(calls.at(-1)!.body ?? '{}'), { reason: 'False positive overlap' });

  // Upstreamunreachable -> 502 CORE_UNREACHABLE
  globalThis.fetch = (async () => {
    throw new TypeError('connect ECONNREFUSED');
  }) as unknown as typeof fetch;
  const unreachable = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${runId}/context-diagnostics`),
  );
  assert.equal(unreachable.status, 502);
  const errBody = (await unreachable.json()) as Record<string, unknown>;
  assert.equal(errBody.code, 'CORE_UNREACHABLE');
});

test('P1 & P2 context diagnostics and evolution paths and schemas are exposed in /docs/json', { concurrency: false }, async () => {
  const res = await app.handle(new Request('http://gateway.local/docs/json'));
  assert.equal(res.status, 200);
  const doc = (await res.json()) as {
    paths: Record<string, Record<string, unknown>>;
    components: { schemas: Record<string, unknown> };
  };

  assert.ok(doc.paths['/api/v1/runs/{runId}/context-diagnostics']?.get);
  assert.ok(doc.paths['/api/v1/context/audit-overlap']?.post);
  assert.ok(doc.paths['/api/v1/context-evolution/proposals']?.get);
  assert.ok(doc.paths['/api/v1/context-evolution/proposals/{proposalId}']?.get);
  assert.ok(doc.paths['/api/v1/context-evolution/proposals/{proposalId}/evaluation']?.get);
  assert.ok(doc.paths['/api/v1/context-evolution/proposals/{proposalId}/apply']?.post);
  assert.ok(doc.paths['/api/v1/context-evolution/proposals/{proposalId}/reject']?.post);

  for (const schemaName of [
    'RunContextDiagnostics',
    'ContextWindowBucketReport',
    'ContextCompactionDiagnostics',
    'ContextCategoryNode',
    'CompactionReconciliation',
    'RunGovernanceDiagnostics',
    'ContextOverlapAuditRequest',
    'ContextOverlapAudit',
    'ContextEvolutionProposal',
    'ContextEvolutionProposalList',
    'ContextEvolutionApplyResult',
    'ContextEvolutionEvaluation',
  ]) {
    assert.ok(doc.components.schemas[schemaName], `Missing OpenAPI schema ${schemaName}`);
  }
});
