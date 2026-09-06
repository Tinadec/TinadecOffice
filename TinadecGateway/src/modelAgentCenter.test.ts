import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });

function mockFetch(handler: (input: RequestInfo | URL, init?: RequestInit) => Response | Promise<Response>) {
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => handler(input, init)) as typeof fetch;
}

test('removed BFF routes return 404 (no dual-track)', async () => {
  const res1 = await app.handle(new Request('http://gateway.local/api/v1/model-center/overview'));
  assert.equal(res1.status, 404);
  const body1 = await res1.json() as Record<string, unknown>;
  assert.ok(String(body1.code ?? body1.title ?? '').includes('not_found') || String(body1.code).includes('run_not_found'));

  const res2 = await app.handle(new Request('http://gateway.local/api/v1/agent-center/overview'));
  assert.equal(res2.status, 404);

  // PUT /agents/:id/runtime-binding was a ghost 404 route; it is now a real
  // thin proxy (plan 配置体验改造 A). With Core unreachable in unit tests the
  // proxy fails with 502/bad-gateway, not 404 — proving the route exists.
  const res3 = await app.handle(new Request('http://gateway.local/api/v1/agents/agent-1/runtime-binding', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ selection_kind: 'inherit' }) }));
  assert.notEqual(res3.status, 404);

  // Deleted model-center refresh alias; canonical path is POST /model-providers/{id}/models/refresh.
  const res4 = await app.handle(new Request('http://gateway.local/api/v1/model-center/provider-instances/p1/models/refresh', { method: 'POST' }));
  assert.equal(res4.status, 404);
});

test('agents thin proxy: CRUD + draft/publish/archive/versions forward path/method/body/query and error mapping', async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined; headers: Record<string,string> }> = [];
  mockFetch((input, init) => {
    const url = typeof input === 'string' ? input : input.toString();
    const headers: Record<string,string> = {};
    if (init?.headers) {
      const h = new Headers(init.headers as HeadersInit);
      h.forEach((v,k) => headers[k]=v);
    }
    requests.push({ url, method: init?.method ?? 'GET', body: typeof init?.body === 'string' ? init.body : undefined, headers });
    if (url.includes('/agents/agent-1/draft') || url.includes('/agents/agent-1/publish')) {
      return new Response(JSON.stringify({ id: 'agent-1', status: 'draft' }), { status: 200, headers: { 'content-type': 'application/json' } });
    }
    if (url.includes('/agents/agent-1/versions/ver-1')) {
      return new Response(JSON.stringify({ id: 'ver-1', version: 1 }), { status: 200, headers: { 'content-type': 'application/json' } });
    }
    if (url.includes('/agents/agent-1/versions')) {
      return new Response(JSON.stringify([{ id: 'ver-1' }]), { status: 200, headers: { 'content-type': 'application/json' } });
    }
    if (url.includes('/agents/agent-1')) {
      return new Response(JSON.stringify({ id: 'agent-1', name: 'Agent One' }), { status: 200, headers: { 'content-type': 'application/json' } });
    }
    if (url.includes('/agents') && (init?.method === 'POST')) {
      return new Response(JSON.stringify({ id: 'new-agent' }), { status: 201, headers: { 'content-type': 'application/json' } });
    }
    if (url.includes('/agents')) {
      return new Response(JSON.stringify([{ id: 'agent-1' }]), { status: 200, headers: { 'content-type': 'application/json' } });
    }
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'content-type': 'application/json' } });
  });

  const rList = await app.handle(new Request('http://gateway.local/api/v1/agents?workspace_id=ws-1'));
  assert.equal(rList.status, 200);
  const rCreate = await app.handle(new Request('http://gateway.local/api/v1/agents', { method: 'POST', headers: { 'content-type': 'application/json', 'x-request-id': 'req-1', 'if-match': 'etag-1' }, body: JSON.stringify({ name: 'New Agent', description: 'test' }) }));
  assert.equal(rCreate.status, 201);
  const rGet = await app.handle(new Request('http://gateway.local/api/v1/agents/agent-1'));
  assert.equal(rGet.status, 200);
  const rDraft = await app.handle(new Request('http://gateway.local/api/v1/agents/agent-1/draft', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name: 'Draft' }) }));
  assert.equal(rDraft.status, 200);
  const rPublish = await app.handle(new Request('http://gateway.local/api/v1/agents/agent-1/publish', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({}) }));
  assert.equal(rPublish.status, 200);
  const rArchive = await app.handle(new Request('http://gateway.local/api/v1/agents/agent-1/archive', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({}) }));
  assert.equal(rArchive.status, 200);
  const rVersions = await app.handle(new Request('http://gateway.local/api/v1/agents/agent-1/versions'));
  assert.equal(rVersions.status, 200);
  const rVersion = await app.handle(new Request('http://gateway.local/api/v1/agents/agent-1/versions/ver-1'));
  assert.equal(rVersion.status, 200);

  // verify proxy paths
  const urls = requests.map(r => [r.method, r.url]);
  assert.ok(urls.some(([m,u]) => m==='GET' && u==='http://127.0.0.1:48731/api/v1/agents?workspace_id=ws-1'));
  assert.ok(urls.some(([m,u]) => m==='POST' && u==='http://127.0.0.1:48731/api/v1/agents'));
  assert.ok(urls.some(([m,u]) => m==='GET' && u==='http://127.0.0.1:48731/api/v1/agents/agent-1'));
  assert.ok(urls.some(([m,u]) => m==='PUT' && u==='http://127.0.0.1:48731/api/v1/agents/agent-1/draft'));
  assert.ok(urls.some(([m,u]) => m==='POST' && u==='http://127.0.0.1:48731/api/v1/agents/agent-1/publish'));
  assert.ok(urls.some(([m,u]) => m==='GET' && u.includes('/agents/agent-1/versions')));
  // forwardHeaders: x-request-id and if-match preserved
  const createReq = requests.find(r => r.url.endsWith('/api/v1/agents') && r.method==='POST')!;
  assert.equal(createReq.headers['x-request-id'], 'req-1');
  assert.equal(createReq.headers['if-match'], 'etag-1');
  assert.equal(createReq.headers['x-tinadec-principal'], 'dev@local');
});

test('agent-modes thin proxy forwards draft/publish/archive and query', async () => {
  const requests: string[] = [];
  mockFetch((input, init) => {
    requests.push(typeof input === 'string' ? input : input.toString());
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'content-type': 'application/json' } });
  });
  const r = await app.handle(new Request('http://gateway.local/api/v1/agent-modes/mode-1/draft', { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ nodes: [], edges: [], layout: {} }) }));
  assert.equal(r.status, 200);
  assert.ok(requests.includes('http://127.0.0.1:48731/api/v1/agent-modes/mode-1/draft'));
  const rPub = await app.handle(new Request('http://gateway.local/api/v1/agent-modes/mode-1/publish', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({}) }));
  assert.equal(rPub.status, 200);
  const rGet = await app.handle(new Request('http://gateway.local/api/v1/agent-modes/mode-1'));
  assert.equal(rGet.status, 200);
});

test('prompt-pipelines thin proxy forwards CRUD and versions', async () => {
  const urls: string[] = [];
  mockFetch((input) => {
    urls.push(typeof input === 'string' ? input : input.toString());
    return new Response(JSON.stringify([{ id: 'pipe-1' }]), { status: 200, headers: { 'content-type': 'application/json' } });
  });
  const rList = await app.handle(new Request('http://gateway.local/api/v1/prompt-pipelines'));
  assert.equal(rList.status, 200);
  assert.ok(urls.includes('http://127.0.0.1:48731/api/v1/prompt-pipelines'));
  const rVer = await app.handle(new Request('http://gateway.local/api/v1/prompt-pipelines/pipe-1/versions/ver-2'));
  assert.equal(rVer.status, 200);
  assert.ok(urls.includes('http://127.0.0.1:48731/api/v1/prompt-pipelines/pipe-1/versions/ver-2'));
});

test('agent-runtime-instances thin proxy forwards run_id query', async () => {
  let captured = '';
  mockFetch((input) => {
    captured = typeof input === 'string' ? input : input.toString();
    return new Response(JSON.stringify([{ id: 'inst-1' }]), { status: 200, headers: { 'content-type': 'application/json' } });
  });
  const r = await app.handle(new Request('http://gateway.local/api/v1/agent-runtime-instances?run_id=run-1'));
  assert.equal(r.status, 200);
  assert.equal(captured, 'http://127.0.0.1:48731/api/v1/agent-runtime-instances?run_id=run-1');
});

test('interactions thin proxy validates dispatch_mode and insert target_run_id before proxy', async () => {
  let proxied = 0;
  mockFetch(() => {
    proxied++;
    return new Response(JSON.stringify({ id: 'inter-1' }), { status: 201, headers: { 'content-type': 'application/json' } });
  });
  const badMissing = await app.handle(new Request('http://gateway.local/api/v1/sessions/sess-1/interactions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ content: 'hi' }) }));
  assert.equal(badMissing.status, 400);
  const badInsert = await app.handle(new Request('http://gateway.local/api/v1/sessions/sess-1/interactions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ dispatch_mode: 'insert', content: 'hi' }) }));
  assert.equal(badInsert.status, 400);
  assert.equal(proxied, 0);
  const okQueued = await app.handle(new Request('http://gateway.local/api/v1/sessions/sess-1/interactions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ dispatch_mode: 'queued', content: 'hi' }) }));
  assert.equal(okQueued.status, 201);
  assert.equal(proxied, 1);
  const okInsert = await app.handle(new Request('http://gateway.local/api/v1/sessions/sess-1/interactions', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ dispatch_mode: 'insert', target_run_id: 'run-1', content: 'hi' }) }));
  assert.equal(okInsert.status, 201);
  assert.equal(proxied, 2);
});

test('interactions reassign/cancel thin proxy; per-interaction stream route removed', async () => {
  const requests: Array<{ url: string; headers: Record<string,string> }> = [];
  mockFetch((input, init) => {
    const url = typeof input === 'string' ? input : input.toString();
    const headers: Record<string,string> = {};
    if (init?.headers) new Headers(init.headers as HeadersInit).forEach((v,k)=>headers[k]=v);
    requests.push({ url, headers });
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'content-type': 'application/json' } });
  });
  const rReassign = await app.handle(new Request('http://gateway.local/api/v1/sessions/sess-1/interactions/inter-1/reassign', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ agent_id: 'agent-1' }) }));
  assert.equal(rReassign.status, 200);
  assert.ok(requests.some(r => r.url === 'http://127.0.0.1:48731/api/v1/sessions/sess-1/interactions/inter-1/reassign'));
  const rCancel = await app.handle(new Request('http://gateway.local/api/v1/sessions/sess-1/interactions/inter-1/cancel', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({}) }));
  assert.equal(rCancel.status, 200);
  // The dangling per-interaction stream proxy was removed: Core never implemented
  // GET /api/v1/sessions/{id}/interactions/{id}/stream. Results stream from
  // GET /api/v1/runs/{runId}/stream instead, so this path must 404 locally and
  // never reach Core.
  const rStream = await app.handle(new Request('http://gateway.local/api/v1/sessions/sess-1/interactions/inter-1/stream', { headers: { 'last-event-id': '42' } }));
  assert.equal(rStream.status, 404);
  assert.ok(!requests.some(r => r.url.includes('/stream')));
});

test('model-providers models/refresh thin proxy forwards POST and maps discovery errors', async () => {
  const requests: Array<{ url: string; method: string; headers: Record<string,string> }> = [];
  let refreshCalls = 0;
  mockFetch((input, init) => {
    const url = typeof input === 'string' ? input : input.toString();
    const headers: Record<string,string> = {};
    if (init?.headers) new Headers(init.headers as HeadersInit).forEach((v,k)=>headers[k]=v);
    requests.push({ url, method: init?.method ?? 'GET', headers });
    if (url.includes('/models/refresh')) {
      refreshCalls++;
      if (refreshCalls === 1) {
        return new Response(JSON.stringify({ models: [{ id: 'gpt-4o', display_name: 'gpt-4o' }] }), { status: 200, headers: { 'content-type': 'application/json' } });
      }
      // Core's MODEL_DISCOVERY_FAILED shape: { code, message, status }
      return new Response(JSON.stringify({ code: 'MODEL_DISCOVERY_FAILED', message: 'Provider returned HTTP 404 for GET /models.', status: 404 }), { status: 502, headers: { 'content-type': 'application/json' } });
    }
    return new Response(JSON.stringify({ ok: true }), { status: 200, headers: { 'content-type': 'application/json' } });
  });

  const ok = await app.handle(new Request('http://gateway.local/api/v1/model-providers/p1/models/refresh', { method: 'POST', headers: { 'x-request-id': 'req-refresh-1' } }));
  assert.equal(ok.status, 200);
  const body = await ok.json() as { models?: Array<{ id: string }> };
  assert.equal(body.models?.[0]?.id, 'gpt-4o');
  const fwd = requests.find(r => r.url === 'http://127.0.0.1:48731/api/v1/model-providers/p1/models/refresh')!;
  assert.equal(fwd.method, 'POST');
  assert.equal(fwd.headers['x-request-id'], 'req-refresh-1');

  const fail = await app.handle(new Request('http://gateway.local/api/v1/model-providers/p1/models/refresh', { method: 'POST' }));
  assert.equal(fail.status, 502);
  assert.ok(String(fail.headers.get('content-type') ?? '').includes('application/problem+json'));
  const problem = await fail.json() as Record<string, unknown>;
  assert.ok(String(problem.detail ?? '').includes('Provider returned HTTP 404'));
});
