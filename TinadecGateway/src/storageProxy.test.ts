import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';
import { proxyJson, proxyRaw, proxySse, proxySseWithCursor, proxyStream } from './coreClient.js';
const original = globalThis.fetch;
afterEach(() => { globalThis.fetch = original; });

test('all upstream transports return redirects without sending host credentials to another endpoint', async () => {
  const calls: RequestInit[] = [];
  globalThis.fetch = (async (_input, init) => {
    calls.push(init ?? {});
    assert.equal(init?.redirect, 'manual');
    assert.equal(new Headers(init?.headers).get('x-tinadec-host-control'), 'supplied-host');
    return new Response(null, { status: 302, headers: { location: 'https://unverified.example/api/v1/sessions' } });
  }) as typeof fetch;
  const headers = { 'x-tinadec-host-control': 'supplied-host' };
  assert.equal((await proxyJson('/api/v1/sessions', { headers })).status, 302);
  assert.equal((await proxyRaw('/api/v1/sessions', { headers })).status, 302);
  assert.equal((await proxySse('/api/v1/events', { headers, redirect: 'follow' })).status, 302);
  assert.equal((await proxySseWithCursor('/api/v1/events', '7', headers, { redirect: 'follow' })).status, 302);
  assert.equal((await proxyStream('/api/v1/files', { headers, redirect: 'follow' })).status, 302);
  assert.equal(calls.length, 5);
});

test('storage and TOML routes preserve scope, CAS, Core errors, and bytes', async () => {
  const calls: { url: string; init?: RequestInit }[] = [];
  globalThis.fetch = (async (input, init) => { calls.push({ url: String(input), init }); return new Response('{"code":"configuration_conflict","diagnostics":[]}', { status: 412, headers: { 'content-type': 'application/problem+json', etag: '"new-hash"' } }); }) as typeof fetch;
  for (const [method, path, body] of [
    ['POST', '/api/v1/storage/scopes/open', { project_path: 'C:/project' }],
    ['POST', '/api/v1/storage/scopes/preview', { project_path: 'C:/project' }],
    ['GET', '/api/v1/storage/scopes/project-scope/workspace', undefined],
    ['PUT', '/api/v1/storage/scopes/project-scope/workspace', { name: 'workspace', roots: [{ id: 'main', path: 'C:/main' }, { id: 'extra', path: 'C:/extra' }], primary_root_id: 'main' }],
    ['GET', '/api/v1/storage/scopes/project-scope/stats', undefined],
    ['POST', '/api/v1/storage/scopes/project-scope/cleanup-preview', { category: 'cache' }],
    ['POST', '/api/v1/storage/scopes/project-scope/cleanup', { preview_id: 'preview' }],
    ['POST', '/api/v1/storage/scopes/project-scope/content-preview', undefined],
    ['POST', '/api/v1/storage/scopes/project-scope/content-collect', { preview_id: 'content-preview' }],
    ['POST', '/api/v1/storage/scopes/project-scope/storage-delete-preview', undefined],
    ['POST', '/api/v1/storage/scopes/project-scope/storage-delete', { preview_id: 'delete-preview' }],
    ['POST', '/api/v1/storage/scopes/project-scope/model-source', { follow_user: true }],
    ['GET', '/api/v1/session-transfers/transfer-id', undefined],
    ['PUT', '/api/v1/configuration/documents/tools', { text: 'enabled = true\n' }],
    ['POST', '/api/v1/configuration/documents/tools/validate', { text: 'enabled = true\n' }],
  ] as const) {
    const response = await app.handle(new Request(`http://gateway.local${path}`, { method, headers: { 'content-type': 'application/json', 'x-tinadec-storage-id': 'project-scope', 'if-match': '"old-hash"' }, ...(body ? { body: JSON.stringify(body) } : {}) }));
    assert.equal(response.status, 412); assert.equal(response.headers.get('etag'), '"new-hash"');
    assert.equal(await response.text(), '{"code":"configuration_conflict","diagnostics":[]}');
    const call = calls.at(-1)!; assert.equal(call.url, `http://127.0.0.1:48731${path}`);
    assert.equal(new Headers(call.init?.headers).get('x-tinadec-storage-id'), 'project-scope');
    assert.equal(new Headers(call.init?.headers).get('if-match'), '"old-hash"');
    assert.deepEqual(call.init?.body ? JSON.parse(String(call.init.body)) : undefined, body);
  }
});
test('project/session aggregate mappers retain the original scope identity', async () => {
  globalThis.fetch = (async () => Response.json([{ id: 'id', name: 'project', path: '/project', project_id: 'project', storage_id: 'project-scope' }])) as unknown as typeof fetch;
  for (const path of ['/api/v1/projects', '/api/v1/sessions']) {
    const response = await app.handle(new Request(`http://gateway.local${path}`));
    assert.equal((await response.json() as { storage_id: string }[])[0]?.storage_id, 'project-scope');
  }
});
test('SSE cursor proxy and CORS retain scope headers', async () => {
  let headers: Headers | undefined;
  globalThis.fetch = (async (_input, init) => { headers = new Headers(init?.headers); return new Response('event: message\ndata: {}\n\n', { headers: { 'content-type': 'text/event-stream' } }); }) as typeof fetch;
  await proxySseWithCursor('/api/v1/events', '7', new Headers({ 'x-tinadec-storage-id': 'old-project' }));
  assert.equal(headers?.get('x-tinadec-storage-id'), 'old-project');
  assert.equal(headers?.get('last-event-id'), '7');
  const response = await app.handle(new Request('http://gateway.local/api/v1/storage/scopes', { method: 'OPTIONS', headers: { origin: 'app://bundle', 'access-control-request-method': 'GET' } }));
  assert.equal(response.status, 204);
  assert.match(response.headers.get('access-control-allow-headers') ?? '', /x-tinadec-storage-id/);
  assert.match(response.headers.get('access-control-expose-headers') ?? '', /etag/);
});

test('Gateway forwards supplied host credentials for JSON, bytes and SSE but never signs anonymous requests', async () => {
  const previous = process.env.TINADEC_HOST_CONTROL_TOKEN;
  process.env.TINADEC_HOST_CONTROL_TOKEN = 'private-gateway-env-must-not-sign-anonymous';
  const calls: Headers[] = [];
  globalThis.fetch = (async (_input, init) => {
    const headers = new Headers(init?.headers); calls.push(headers);
    return headers.has('x-tinadec-host-control') ? Response.json([])
      : Response.json({ code: 'host_authorization_required', detail: 'This API requires a trusted host request.' }, { status: 403 });
  }) as typeof fetch;
  try {
    for (const path of ['/api/v1/storage/scopes', '/api/v1/projects', '/api/v1/sessions']) {
      const rejected = await app.handle(new Request(`http://gateway.local${path}`));
      assert.equal(rejected.status, 403);
      assert.equal((await rejected.json() as { code: string }).code, 'host_authorization_required');
      assert.equal(calls.at(-1)?.get('x-tinadec-host-control'), null);
      assert.equal((await app.handle(new Request(`http://gateway.local${path}`, { headers: { 'x-tinadec-host-control': 'supplied-host' } }))).status, 200);
      assert.equal(calls.at(-1)?.get('x-tinadec-host-control'), 'supplied-host');
    }
    await proxySseWithCursor('/api/v1/events', null, new Headers({ 'x-tinadec-host-control': 'supplied-host' }));
    assert.equal(calls.at(-1)?.get('x-tinadec-host-control'), 'supplied-host');
    await proxySseWithCursor('/api/v1/events', null, new Headers());
    assert.equal(calls.at(-1)?.get('x-tinadec-host-control'), null);
  } finally {
    if (previous === undefined) delete process.env.TINADEC_HOST_CONTROL_TOKEN;
    else process.env.TINADEC_HOST_CONTROL_TOKEN = previous;
  }
});


test('classified Core failures survive mapped workspace and Agent Pack routes with response guards', async () => {
  const diagnostic = { code: 'configuration_parse', message: 'Malformed workspace configuration.', severity: 'error', line: 4, column: 2 };
  const problem = { code: 'storage_scope_unavailable', detail: 'The registered project directory is unavailable.',
    category: 'environment_unavailable', retryable: false, actions: ['unregister_workspace', 'retry', 'open_storage_settings'],
    trace_id: 'trace-workspace-route', diagnostics: [diagnostic], private_extension: 'must-not-forward' };
  const calls: Headers[] = [];
  globalThis.fetch = (async (_input, init) => { calls.push(new Headers(init?.headers)); return Response.json(problem, {
    status: 409, headers: { 'content-type': 'application/problem+json', etag: '"current-revision"' },
  }); }) as typeof fetch;
  for (const [method, path] of [
    ['GET', '/api/v1/projects'], ['GET', '/api/v1/agent-packs'], ['GET', '/api/v1/agent-packs/pack-id'],
    ['POST', '/api/v1/agent-packs/install-preview'], ['PUT', '/api/v1/agent-packs/pack-id'],
    ['DELETE', '/api/v1/agent-packs/pack-id'], ['POST', '/api/v1/agent-packs/pack-id/enable'],
    ['POST', '/api/v1/agent-packs/pack-id/disable'], ['POST', '/api/v1/agent-packs/pack-id/adopt-defaults'],
  ]) {
    const response = await app.handle(new Request('http://gateway.local' + path, { method,
      headers: { 'content-type': 'application/json', 'x-tinadec-storage-id': 'scope-fixed', 'x-tinadec-host-control': 'trusted-host', 'if-match': '"old-revision"', 'idempotency-key': 'one-install' },
      ...(method === 'POST' || method === 'PUT' ? { body: '{}' } : {}),
    }));
    assert.equal(response.status, 409, path);
    const body = await response.json() as Record<string, unknown>;
    for (const field of ['code', 'detail', 'category', 'retryable', 'actions', 'trace_id', 'diagnostics'])
      assert.deepEqual(body[field], problem[field as keyof typeof problem], path + ' ' + field);
    assert.equal(JSON.stringify(body).includes('must-not-forward'), false);
    if (path.includes('/agent-packs')) assert.equal(response.headers.get('etag'), '"current-revision"', path);
    assert.equal(calls.at(-1)?.get('x-tinadec-storage-id'), 'scope-fixed');
    assert.equal(calls.at(-1)?.get('x-tinadec-host-control'), 'trusted-host');
    assert.equal(calls.at(-1)?.get('if-match'), '"old-revision"');
    assert.equal(calls.at(-1)?.get('idempotency-key'), 'one-install');
  }
});

test('project roster keeps a broken workspace actionable without inventing its lifecycle', async () => {
  const diagnostic = { code: 'configuration_parse', message: 'Malformed workspace configuration.', severity: 'error', line: 4 };
  globalThis.fetch = (async () => Response.json([
    { id: 'broken-project', storage_id: 'broken-scope', name: 'Broken', path: '/missing', availability: 'error',
      availability_error: 'Folder missing', availability_code: 'storage_scope_unavailable', category: 'environment_unavailable',
      retryable: false, actions: ['unregister_workspace', 'retry', 'unknown_action'], trace_id: 'trace-roster',
      diagnostics: [{ ...diagnostic, private_extension: 'must-not-forward' }], private_extension: 'must-not-forward' },
    { id: 'ready-project', storage_id: 'ready-scope', name: 'Ready', path: '/ready', availability: 'ready', lifecycle_status: 'archived' },
  ])) as unknown as typeof fetch;
  const response = await app.handle(new Request('http://gateway.local/api/v1/projects'));
  assert.equal(response.status, 200);
  const rows = await response.json() as Record<string, unknown>[];
  assert.equal(rows[0]?.availability_code, 'storage_scope_unavailable');
  assert.equal(rows[0]?.category, 'environment_unavailable');
  assert.equal(rows[0]?.retryable, false);
  assert.deepEqual(rows[0]?.actions, ['unregister_workspace', 'retry']);
  assert.equal(rows[0]?.trace_id, 'trace-roster');
  assert.deepEqual(rows[0]?.diagnostics, [diagnostic]);
  assert.equal(rows[0]?.lifecycle_status, null);
  assert.equal(rows[1]?.lifecycle_status, 'archived');
  assert.equal(JSON.stringify(rows).includes('must-not-forward'), false);
});

test('scope roster transports each availability result without hiding healthy rows', async () => {
  const rows = [{ storage_id: 'user', scope_kind: 'user', workspace: null, availability: 'ready' },
    { storage_id: 'broken-scope', scope_kind: 'project', workspace: null, availability: 'error', availability_error: 'Malformed TOML',
      availability_code: 'configuration_invalid', category: 'user_action_required', retryable: false, actions: ['reload'], trace_id: 'trace-scope-roster', diagnostics: [] }];
  globalThis.fetch = (async () => Response.json(rows)) as unknown as typeof fetch;
  const response = await app.handle(new Request('http://gateway.local/api/v1/storage/scopes', { headers: { 'x-tinadec-storage-id': 'user' } }));
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), rows);
});
