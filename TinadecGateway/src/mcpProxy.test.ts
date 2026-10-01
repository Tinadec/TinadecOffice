import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = originalFetch;
});

/**
 * The gateway used to expose seven `/api/v1/mcp/*` paths while Core had two stubs and a 501. Five
 * of them could only ever answer 404, and one of them (`tools/{toolName}/call`) would have been a
 * way to reach an MCP tool without Core's approval gate even if it had worked. These tests pin the
 * remaining surface to what Core actually answers.
 */
test('MCP inventory is forwarded verbatim, keeping the source that explains an empty list', async () => {
  const calls: string[] = [];
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    calls.push(String(input));
    return new Response(
      JSON.stringify({
        source: 'tool_provider',
        workspace_root: 'C:\\work\\demo',
        config_path: 'C:\\work\\demo\\mcp_servers.json',
        servers: [
          { id: 'github', name: 'GitHub', status: 'connected', tools: [{ id: 'create_issue', name: 'create_issue' }] },
          { id: 'ghost', name: 'Ghost', status: 'error', error: 'process exited', tools: [] },
        ],
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  }) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/mcp/servers'));
  assert.equal(response.status, 200);
  const body = (await response.json()) as Record<string, unknown>;
  assert.equal(body.source, 'tool_provider');
  assert.equal((body.servers as unknown[]).length, 2);
  assert.equal(body.config_path, 'C:\\work\\demo\\mcp_servers.json');
  assert.ok(calls.some((url) => url.endsWith('/api/v1/mcp/servers')), 'the inventory hop must go to Core');
});

test('MCP tool read forwards the server id without inventing a route for it', async () => {
  const calls: string[] = [];
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    calls.push(String(input));
    return new Response(
      JSON.stringify({
        source: 'tool_provider',
        server_id: 'git%20hub',
        server: { id: 'git hub', name: 'Git Hub', status: 'connected', tools: [] },
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  }) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/mcp/servers/git%20hub/tools'));
  assert.equal(response.status, 200);
  assert.ok(
    calls.some((url) => url.endsWith('/api/v1/mcp/servers/git%20hub/tools')),
    `expected the encoded id to survive the hop, saw: ${calls.join(', ')}`,
  );
});

test('a Core mcp_server_not_found reaches the client as itself, not as conflict', async () => {
  globalThis.fetch = (async () =>
    new Response(
      JSON.stringify({ code: 'mcp_server_not_found', message: 'No MCP server named ' + "'nope' is configured." }),
      { status: 404, headers: { 'content-type': 'application/json' } },
    )) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/mcp/servers/nope/tools'));
  assert.equal(response.status, 404);
  const body = (await response.json()) as Record<string, unknown>;
  // Outside the allow-list this would come back as `conflict`, turning "you named a server that is
  // not there" into "try again later".
  assert.equal(body.code, 'mcp_server_not_found');
  assert.equal(body.title, 'mcp_server_not_found');
});

test('the phantom MCP control surface is gone from the gateway itself', async () => {
  let forwarded = 0;
  globalThis.fetch = (async () => {
    forwarded += 1;
    return new Response('{}', { status: 200, headers: { 'content-type': 'application/json' } });
  }) as typeof fetch;

  const phantom: Array<[string, string]> = [
    ['POST', '/api/v1/mcp/servers/github/connect'],
    ['POST', '/api/v1/mcp/servers/github/disconnect'],
    ['GET', '/api/v1/mcp/servers/github/status'],
    ['POST', '/api/v1/mcp/servers/github/reload'],
    ['POST', '/api/v1/mcp/servers/github/tools/create_issue/call'],
  ];
  for (const [method, path] of phantom) {
    const response = await app.handle(new Request(`http://gateway.local${path}`, { method }));
    assert.equal(response.status, 404, `${method} ${path} must not be advertised by the gateway`);
  }
  assert.equal(forwarded, 0, 'a route the gateway does not have must not reach Core either');
});

test('the external contract declares only the MCP reads Core implements', async () => {
  const response = await app.handle(new Request('http://gateway.local/docs/json'));
  assert.equal(response.status, 200);
  const doc = (await response.json()) as { paths: Record<string, Record<string, unknown>> };
  const mcpPaths = Object.keys(doc.paths).filter((path) => path.startsWith('/api/v1/mcp/')).sort();

  assert.deepEqual(mcpPaths, [
    '/api/v1/mcp/servers',
    '/api/v1/mcp/servers/{serverId}/tools',
  ]);
  // Both are typed at the envelope: `source` is the field a client must branch on, and an untyped
  // 200 is how the desktop DTOs kept drifting from what Core sends.
  for (const path of mcpPaths) {
    const ok = doc.paths[path]!.get as { responses?: Record<string, { content?: unknown }> };
    assert.ok(ok.responses?.['200']?.content, `${path} must keep a typed 200 in the external contract`);
  }
});
