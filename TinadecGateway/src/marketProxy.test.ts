import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = originalFetch;
});

function captureFetch(body: unknown, status = 200) {
  const calls: string[] = [];
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    calls.push(String(input));
    return new Response(JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    });
  }) as typeof fetch;
  return calls;
}

/**
 * These six routes were proxies to Core stubs, so every one of them answered what the stub
 * answered — `[]`, 501, or 404. Core now has a real market surface, and the part worth pinning
 * here is the parameter names: the catalog proxy used to send `query` and `sourceId`, which Core
 * has never read, so a search box that looked fine against a mocked client narrowed nothing at all
 * over the wire.
 */
test('catalog filters reach Core under the names Core reads', async () => {
  const calls = captureFetch({ items: [], total_available: 0, has_more: false });

  const response = await app.handle(new Request(
    'http://gateway.local/api/v1/market/catalog?kind=mcp-server&q=github&source_id=abc&limit=10&offset=20',
  ));
  assert.equal(response.status, 200);

  assert.equal(calls.length, 1, 'the catalog read is one hop to Core');
  const url = calls[0]!;
  assert.ok(url.includes('kind=mcp-server'), url);
  assert.ok(url.includes('q=github'), `the search term must arrive as q, saw ${url}`);
  assert.ok(url.includes('source_id=abc'), `the source filter must arrive as source_id, saw ${url}`);
  assert.ok(url.includes('limit=10') && url.includes('offset=20'), `paging must survive the hop, saw ${url}`);
  assert.ok(!url.includes('query='), `Core reads no 'query' parameter, saw ${url}`);
  assert.ok(!url.includes('sourceId'), `Core reads no 'sourceId' parameter, saw ${url}`);
});

test('a catalog read with no filters does not send an empty query string', async () => {
  const calls = captureFetch({ items: [], total_available: 0, has_more: false });

  await app.handle(new Request('http://gateway.local/api/v1/market/catalog'));
  assert.ok(calls[0]!.endsWith('/api/v1/market/catalog'), `expected no bare ?, saw ${calls[0]}`);
});

test('a refresh reports which of the three things happened', async () => {
  const calls = captureFetch({
    source_id: 'a1b2',
    outcome: 'blocked',
    fetched_rows: 0,
    refused_rows: 0,
    removed_rows: 0,
    pages_fetched: 0,
    truncated_pages: false,
    reason: 'the target address is not allowed',
  });

  const response = await app.handle(new Request(
    'http://gateway.local/api/v1/market/sources/a1b2/refresh',
    { method: 'POST' },
  ));
  assert.equal(response.status, 200);
  const body = (await response.json()) as Record<string, unknown>;
  assert.equal(body.outcome, 'blocked');
  assert.equal(body.reason, 'the target address is not allowed');
  assert.ok(calls.some((url) => url.endsWith('/api/v1/market/sources/a1b2/refresh')));
});

test('enable, disable, and delete are forwarded rather than answered locally', async () => {
  const calls = captureFetch({
    id: 'a1b2',
    name: 'registry',
    kind: 'mcp_registry',
    location: 'https://registry.example.com/v0/servers',
    enabled: false,
    revision: 2,
    entry_count: 0,
  });

  const patch = await app.handle(new Request('http://gateway.local/api/v1/market/sources/a1b2', {
    method: 'PATCH',
    body: JSON.stringify({ enabled: false }),
    headers: { 'content-type': 'application/json' },
  }));
  assert.equal(patch.status, 200);
  assert.equal((await patch.json() as { enabled: boolean }).enabled, false);

  globalThis.fetch = (async (input: RequestInfo | URL) => {
    calls.push(String(input));
    return new Response(null, { status: 204 });
  }) as typeof fetch;

  const del = await app.handle(new Request('http://gateway.local/api/v1/market/sources/a1b2', {
    method: 'DELETE',
  }));
  assert.equal(del.status, 204);
  assert.ok(calls.some((url) => url.endsWith('/api/v1/market/sources/a1b2')));
});

test('a market refusal keeps its own code instead of becoming conflict', async () => {
  globalThis.fetch = (async () => new Response(
    JSON.stringify({ code: 'unsupported_market_source_kind', message: 'No market adapter reads kind ' + "'cli_runtime'." }),
    { status: 400, headers: { 'content-type': 'application/json' } },
  )) as typeof fetch;

  const response = await app.handle(new Request('http://gateway.local/api/v1/market/sources', {
    method: 'POST',
    body: JSON.stringify({ name: 'x', kind: 'cli_runtime', location: 'https://example.com' }),
    headers: { 'content-type': 'application/json' },
  }));
  assert.equal(response.status, 400);

  // Outside ALLOWED_CODES a proxy would rewrite this into `conflict`, which tells the client to
  // retry something that will never succeed.
  assert.equal((await response.json() as { code: string }).code, 'unsupported_market_source_kind');
});

test('a disabled source answers 409, not an empty refresh', async () => {
  globalThis.fetch = (async () => new Response(
    JSON.stringify({ code: 'market_source_disabled', message: "'office' is disabled." }),
    { status: 409, headers: { 'content-type': 'application/json' } },
  )) as typeof fetch;

  const response = await app.handle(new Request(
    'http://gateway.local/api/v1/market/sources/a1b2/refresh',
    { method: 'POST' },
  ));
  assert.equal(response.status, 409);
  assert.equal((await response.json() as { code: string }).code, 'market_source_disabled');
});

test('an expired install proposal stays a 412 with its own code', async () => {
  // The desktop branches on this one: 412 means "preview again", anything narrower-sounding means
  // "give up". Collapsing it into conflict would tell a user to retry bytes that no longer exist.
  globalThis.fetch = (async () => new Response(
    JSON.stringify({ code: 'market_install_proposal_stale', message: 'This proposal is no longer available.' }),
    { status: 412, headers: { 'content-type': 'application/json' } },
  )) as typeof fetch;

  const response = await app.handle(new Request(
    'http://gateway.local/api/v1/market/install-proposals/p1/apply',
    { method: 'POST' },
  ));
  assert.equal(response.status, 412);
  assert.equal((await response.json() as { code: string }).code, 'market_install_proposal_stale');
});

test('an install refusal that names its reason keeps it through the proxy', async () => {
  const message = 'The tool provider reads its server config from outside the project root.';
  globalThis.fetch = (async () => new Response(
    JSON.stringify({ code: 'market_install_target_unresolved', message }),
    { status: 409, headers: { 'content-type': 'application/json' } },
  )) as typeof fetch;

  const response = await app.handle(new Request(
    'http://gateway.local/api/v1/market/catalog/c1/install-preview',
    { method: 'POST', body: JSON.stringify({ project_id: 'p1' }), headers: { 'content-type': 'application/json' } },
  ));
  assert.equal(response.status, 409);
  const body = await response.json() as { code: string, message?: string, detail?: string };
  assert.equal(body.code, 'market_install_target_unresolved');
  assert.equal(body.message ?? body.detail, message);
});

test('the external contract carries exactly the market surface Core implements', async () => {
  const response = await app.handle(new Request('http://gateway.local/docs/json'));
  const doc = (await response.json()) as {
    paths: Record<string, Record<string, { responses?: Record<string, { content?: unknown }> }>>;
  };

  // Deep-equal on the set, not a lower bound: the MCP lesson was five phantom routes living in a
  // contract Core never implemented, and a ">= 4 paths" assertion cannot catch a sixth appearing.
  assert.deepEqual(Object.keys(doc.paths).filter((path) => path.startsWith('/api/v1/market/')).sort(), [
    '/api/v1/market/catalog',
    '/api/v1/market/catalog/{catalogId}',
    '/api/v1/market/catalog/{catalogId}/install-preview',
    '/api/v1/market/install-proposals/{proposalId}/apply',
    '/api/v1/market/installations',
    '/api/v1/market/installations/{installationId}/uninstall-preview',
    '/api/v1/market/sources',
    '/api/v1/market/sources/{sourceId}',
    '/api/v1/market/sources/{sourceId}/refresh',
  ]);

  for (const [path, method] of [
    ['/api/v1/market/sources', 'get'],
    ['/api/v1/market/sources', 'post'],
    ['/api/v1/market/sources/{sourceId}', 'patch'],
    ['/api/v1/market/sources/{sourceId}/refresh', 'post'],
    ['/api/v1/market/catalog', 'get'],
    ['/api/v1/market/catalog/{catalogId}', 'get'],
    ['/api/v1/market/catalog/{catalogId}/install-preview', 'post'],
    ['/api/v1/market/installations/{installationId}/uninstall-preview', 'post'],
    ['/api/v1/market/install-proposals/{proposalId}/apply', 'post'],
    ['/api/v1/market/installations', 'get'],
  ] as const) {
    const op = doc.paths[path]![method]!;
    assert.ok(op.responses?.['200']?.content, `${method.toUpperCase()} ${path} must keep a typed 200`);
  }
});
