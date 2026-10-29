import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });

const id = '11111111-1111-1111-1111-111111111111';

interface RouteCase {
  method: 'GET' | 'POST' | 'PATCH' | 'DELETE';
  template: string;
  /** Query string exactly as a client sends it; the Gateway must forward it untouched. */
  query?: string;
  body?: unknown;
  status: number;
}

// The complete companion surface Core serves today. Pinned as an explicit list (not a lower
// bound) so a dropped operation fails here and a route Core never implements cannot appear as a
// ghost entry. Core has no `DELETE …/members` without a companion id; the two real membership
// operations are the invite POST and the per-companion DELETE below.
const routes: RouteCase[] = [
  { method: 'GET', template: '/api/v1/companion-settings', status: 200 },
  { method: 'PATCH', template: '/api/v1/companion-settings', body: { main_companion_id: id, autonomy_paused: true, expected_revision: 3 }, status: 200 },
  { method: 'GET', template: '/api/v1/companion-connections', status: 200 },
  // Static companion path, deliberately before the companion-id routes in the implementation.
  { method: 'GET', template: '/api/v1/companions/activity', query: 'limit=5', status: 200 },
  { method: 'GET', template: '/api/v1/companions', query: 'include_archived=true', status: 200 },
  { method: 'POST', template: '/api/v1/companions', body: { name: 'Nova', persona_document: 'Calm and precise.', model_purpose: 'chat' }, status: 200 },
  { method: 'GET', template: '/api/v1/companions/{companionId}', status: 200 },
  { method: 'PATCH', template: '/api/v1/companions/{companionId}', body: { name: 'Nova II', autonomy: { enabled: true, interval_minutes: 30, daily_action_budget: 4 } }, status: 200 },
  { method: 'POST', template: '/api/v1/companions/{companionId}/archive', status: 200 },
  { method: 'POST', template: '/api/v1/companions/{companionId}/autonomy/tick', status: 200 },
  { method: 'GET', template: '/api/v1/companions/{companionId}/memories', query: 'q=shared&limit=12', status: 200 },
  { method: 'POST', template: '/api/v1/companions/{companionId}/memories', body: { content: 'Prefers short answers.', source_kind: 'user' }, status: 200 },
  { method: 'PATCH', template: '/api/v1/companions/{companionId}/memories/{memoryId}', body: { content: 'Prefers terse answers.' }, status: 200 },
  { method: 'DELETE', template: '/api/v1/companions/{companionId}/memories/{memoryId}', status: 204 },
  { method: 'GET', template: '/api/v1/companions/{companionId}/skills', status: 200 },
  { method: 'POST', template: '/api/v1/companions/{companionId}/skills', body: { name: 'Summarize', source_kind: 'manual', instructions: 'Summarize.' }, status: 200 },
  { method: 'PATCH', template: '/api/v1/companions/{companionId}/skills/{bindingId}', body: { enabled: false }, status: 200 },
  { method: 'DELETE', template: '/api/v1/companions/{companionId}/skills/{bindingId}', status: 204 },
  { method: 'GET', template: '/api/v1/companions/{companionId}/connections', status: 200 },
  { method: 'POST', template: '/api/v1/companions/{companionId}/connections', body: { kind: 'mcp', name: 'Local MCP', endpoint: 'stdio://x', authorization: 'configured' }, status: 200 },
  { method: 'PATCH', template: '/api/v1/companions/{companionId}/connections/{bindingId}', body: { authorization: 'revoked', enabled: false }, status: 200 },
  { method: 'DELETE', template: '/api/v1/companions/{companionId}/connections/{bindingId}', status: 204 },
  { method: 'GET', template: '/api/v1/companions/{companionId}/conversations', query: 'include_groups=false', status: 200 },
  { method: 'POST', template: '/api/v1/companions/{companionId}/conversations', body: { kind: 'private' }, status: 200 },
  { method: 'GET', template: '/api/v1/companion-conversations/{conversationId}', status: 200 },
  { method: 'GET', template: '/api/v1/companion-conversations', query: 'kind=group&origin=companion&limit=20', status: 200 },
  { method: 'POST', template: '/api/v1/companion-conversations/{conversationId}/members', body: { companion_id: id, share_history: false }, status: 200 },
  { method: 'DELETE', template: '/api/v1/companion-conversations/{conversationId}/members/{companionId}', status: 200 },
  { method: 'GET', template: '/api/v1/companion-conversations/{conversationId}/messages', query: 'after_seq=4&limit=12', status: 200 },
  { method: 'POST', template: '/api/v1/companion-conversations/{conversationId}/messages', body: { body: 'Hello Nova', addressed_to: [id] }, status: 200 },
  { method: 'POST', template: '/api/v1/companion-conversations/{conversationId}/pause', body: { paused: true }, status: 200 },
  { method: 'GET', template: '/api/v1/companion-runs/{runId}', status: 200 },
];

function materialize(route: RouteCase): string {
  const path = route.template.replace(/\{[^}]+\}/g, id);
  return route.query ? `${path}?${route.query}` : path;
}

test('every companion operation forwards the exact path, query, JSON body and status', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: string | undefined; headers: Headers }> = [];
  let upstreamStatus = 200;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    calls.push({
      path: url.pathname + url.search,
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    });
    return new Response(upstreamStatus === 204 ? null : JSON.stringify({ ok: true }), { status: upstreamStatus });
  }) as typeof fetch;

  for (const route of routes) {
    upstreamStatus = route.status;
    const url = materialize(route);
    const body = route.body === undefined ? undefined : JSON.stringify(route.body);
    const response = await app.handle(new Request(`http://gateway.local${url}`, {
      method: route.method,
      ...(body === undefined ? {} : { body, headers: { 'content-type': 'application/json' } }),
    }));
    assert.equal(response.status, route.status, `${route.method} ${url}`);
    assert.equal(await response.text(), route.status === 204 ? '' : '{"ok":true}');
    const call = calls.at(-1)!;
    assert.equal(call.path, url, `${route.method} ${url} forwarded a different upstream path`);
    assert.equal(call.method, route.method);
    assert.equal(call.body, body, `${route.method} ${url} rewrote the body`);
  }
  assert.equal(calls.length, routes.length);
  // A contract-size pin: 21 paths / 32 operations. Shrinking the proxied surface fails here.
  assert.equal(new Set(routes.map((route) => route.template)).size, 21);
  assert.equal(routes.length, 32);
});

test('If-Match and Idempotency-Key reach Core unchanged', { concurrency: false }, async () => {
  const seen: Headers[] = [];
  globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
    seen.push(new Headers(init?.headers));
    return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;

  await app.handle(new Request(`http://gateway.local/api/v1/companions/${id}`, {
    method: 'PATCH',
    body: JSON.stringify({ name: 'Nova' }),
    headers: { 'content-type': 'application/json', 'if-match': '"7"', 'idempotency-key': 'retry-1' },
  }));
  assert.equal(seen.at(-1)!.get('if-match'), '"7"');
  assert.equal(seen.at(-1)!.get('idempotency-key'), 'retry-1');

  await app.handle(new Request(`http://gateway.local/api/v1/companion-conversations/${id}/messages`, {
    method: 'POST',
    body: JSON.stringify({ body: 'Hello', idempotency_key: 'client-1' }),
    headers: { 'content-type': 'application/json', 'idempotency-key': 'header-1' },
  }));
  assert.equal(seen.at(-1)!.get('idempotency-key'), 'header-1');
});

test('companion refusal codes survive verbatim instead of collapsing into a generic conflict', { concurrency: false }, async () => {
  const cases = [
    { method: 'PATCH', path: `/api/v1/companions/${id}`, status: 412, code: 'companion_revision_conflict' },
    { method: 'POST', path: `/api/v1/companion-conversations/${id}/messages`, status: 400, code: 'companion_not_member' },
    { method: 'POST', path: `/api/v1/companion-conversations/${id}/messages`, status: 409, code: 'companion_turn_budget_exhausted' },
  ] as const;

  for (const item of cases) {
    globalThis.fetch = (async () => new Response(
      JSON.stringify({ code: item.code, message: 'Core refused.', trace_id: 'core-trace' }),
      { status: item.status, headers: { 'content-type': 'application/problem+json' } },
    )) as unknown as typeof fetch;
    const response = await app.handle(new Request(`http://gateway.local${item.path}`, {
      method: item.method,
      body: JSON.stringify({ body: 'hi' }),
      headers: { 'content-type': 'application/json' },
    }));
    assert.equal(response.status, item.status, item.code);
    const received = await response.json() as Record<string, unknown>;
    assert.equal(received.code, item.code);
    assert.equal(received.message, 'Core refused.');
    assert.equal(received.trace_id, 'core-trace');
  }
});

test('GET /api/v1/companions/activity is a static route, not a companion id', { concurrency: false }, async () => {
  const calls: string[] = [];
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    const url = new URL(String(input));
    calls.push(url.pathname + url.search);
    return new Response(JSON.stringify([{ id: id, kind: 'message_sent' }]), {
      status: 200, headers: { 'content-type': 'application/json' },
    });
  }) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/companions/activity?limit=5'));
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), [{ id, kind: 'message_sent' }]);
  assert.deepEqual(calls, ['/api/v1/companions/activity?limit=5']);
});

test('an unreachable Core stays an upstream failure (502), not a route or validation error', { concurrency: false }, async () => {
  globalThis.fetch = (async () => { throw new TypeError('fetch failed'); }) as unknown as typeof fetch;
  const response = await app.handle(new Request(`http://gateway.local/api/v1/companions/${id}`));
  assert.equal(response.status, 502);
  const body = await response.json() as Record<string, unknown>;
  assert.equal(body.code, 'CORE_UNREACHABLE');
});

test('companion paths and schemas are documented in the external contract', { concurrency: false }, async () => {
  const response = await app.handle(new Request('http://gateway.local/docs/json'));
  const doc = await response.json() as {
    paths: Record<string, Record<string, unknown>>;
    tags: Array<{ name: string }>;
    components: { schemas: Record<string, unknown> };
  };
  const documented = Object.keys(doc.paths).filter((path) => path.startsWith('/api/v1/companion')).sort();
  const expected = [...new Set(routes.map((route) => route.template))].sort();
  // Deep equality, not a floor: a route Core does not serve must not appear as a ghost entry.
  assert.deepEqual(documented, expected);
  for (const route of routes) {
    assert.ok(doc.paths[route.template]?.[route.method.toLowerCase()], `Missing ${route.method} ${route.template}`);
  }
  assert.ok(doc.tags.some((tag) => tag.name === 'Companions'));
  const responseRef = (path: string, method: string, status: number) =>
    (doc.paths[path]?.[method] as { responses?: Record<string, { content?: Record<string, { schema?: Record<string, unknown> }> }> })
      ?.responses?.[status]?.content?.['application/json']?.schema?.$ref;
  assert.equal(responseRef('/api/v1/companions', 'get', 200), '#/components/schemas/CompanionList');
  assert.equal(responseRef('/api/v1/companions/activity', 'get', 200), '#/components/schemas/CompanionActivityList');
  assert.equal(responseRef(`/api/v1/companion-conversations/{conversationId}/messages`, 'post', 200), '#/components/schemas/CompanionSendMessageResult');
  assert.equal(responseRef('/api/v1/companions/{companionId}/memories/{memoryId}', 'delete', 204), undefined);
  for (const name of [
    'CompanionSummary', 'CompanionDetail', 'CompanionAutonomy', 'CompanionMemory', 'CompanionSkill',
    'CompanionConnection', 'CompanionConversation', 'CompanionMember', 'CompanionMessage', 'CompanionRun',
    'CompanionActivity', 'CompanionSettings', 'CompanionSendMessageResult', 'CompanionAutonomyTickResult',
    'CompanionConnectionOverview',
  ]) {
    assert.ok(doc.components.schemas[name], `Missing schema ${name}`);
  }
});
