import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';
const original = globalThis.fetch;
afterEach(() => { globalThis.fetch = original; });
test('tools settings/resource proxy forwards exact paths, project scope, sparse JSON and revision headers', async () => {
  const calls: { url: string; method: string; body: unknown; revision: string | null }[] = [];
  globalThis.fetch = (async (input, init) => {
    calls.push({ url: String(input), method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : null, revision: new Headers(init?.headers).get('if-match') });
    return Response.json({ revision: 9, settings: { skills: { resource_ids: [] } } }, { headers: { etag: '"9"' } });
  }) as typeof fetch;
  for (const [method, path, body] of [
    ['GET', '/api/v1/tools/settings/schema', undefined],
    ['PUT', '/api/v1/tools/settings/agents/agent-1?project_id=p1', { settings: { skills: { resource_ids: [] } } }],
    ['GET', '/api/v1/tools/settings/effective?agent_id=agent-1&project_id=p1', undefined],
    ['GET', '/api/v1/tools/settings/capabilities?agent_id=agent-1&project_id=p1', undefined],
    ['POST', '/api/v1/tools/mcp/servers?project_id=p1', { id: 'server', env: { API_KEY: 'secret input only' } }],
    ['DELETE', '/api/v1/tools/mcp/servers/resource-1', undefined],
    ['POST', '/api/v1/tools/skills/import', { scope: 'shared', content: '---\nname: a\n---' }],
    ['GET', '/api/v1/tools/skills/resource-1/files?project_id=p1&path=references%2Fguide.md', undefined],
    ['PUT', '/api/v1/tools/skills/resource-1?project_id=p1', { enabled: false, expected_file_hash: 'old' }],
    ['DELETE', '/api/v1/tools/skills/resource-1', undefined],
  ] as const) {
    const response = await app.handle(new Request(`http://gateway.local${path}`, { method, headers: { 'content-type': 'application/json', 'if-match': '"8"' }, ...(body ? { body: JSON.stringify(body) } : {}) }));
    assert.equal(response.status, 200); assert.equal(response.headers.get('etag'), '"9"');
    const call = calls.at(-1)!; assert.equal(call.url, `http://127.0.0.1:48731${path}`); assert.equal(call.method, method); assert.equal(call.revision, '"8"'); assert.deepEqual(call.body, body ?? null);
  }
});
test('Skills file preview and governed delete preserve package fields, binary bytes and action receipts', async () => {
  const payload = { resource_id: 'resource-1', scope: 'shared', package_hash: 'sha256:pack', package_files: [{ path: 'references/guide.md', content_hash: 'sha256:file', size_bytes: 4 }], source: 'skill_git', version: 'commit', commit: 'a'.repeat(40), availability: 'available', files: { 'references/guide.md': 'dGVzdA==' }, user_action_id: 'action-1', action_status: 'awaiting_user' };
  globalThis.fetch = (async () => Response.json(payload, { headers: { etag: '"3"' } })) as unknown as typeof fetch;
  for (const [method, path] of [['GET', '/api/v1/tools/skills/resource-1/files?path=references%2Fguide.md'], ['DELETE', '/api/v1/tools/skills/resource-1?project_id=p1']] as const) {
    const response = await app.handle(new Request(`http://gateway.local${path}`, { method, headers: { 'if-match': '"3"' } }));
    assert.equal(response.status, 200); assert.equal(response.headers.get('etag'), '"3"'); assert.deepEqual(await response.json(), payload);
  }
});
test('tools proxy preserves Core validation and concurrency failure details', async () => {
  globalThis.fetch = (async () => Response.json({ code: 'tool_settings_conflict', detail: 'Draft remains available.' }, { status: 412, headers: { 'content-type': 'application/problem+json' } })) as unknown as typeof fetch;
  const response = await app.handle(new Request('http://gateway.local/api/v1/tools/settings/defaults', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ settings: {} }) }));
  assert.equal(response.status, 412); assert.equal(response.headers.get('content-type'), 'application/problem+json'); assert.equal((await response.json() as { code: string }).code, 'tool_settings_conflict');
});
