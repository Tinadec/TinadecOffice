import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = originalFetch;
});

const runId = '22222222-2222-2222-2222-222222222222';
const providerId = '33333333-3333-3333-3333-333333333333';

test('live-digest settings and snapshot routes forward path, headers, body and status verbatim', { concurrency: false }, async () => {
  const calls: Array<{ path: string; method: string; body: string | undefined; headers: Headers }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    calls.push({
      path: url.pathname + url.search,
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined,
      headers: new Headers(init?.headers),
    });
    return new Response(JSON.stringify({ ok: true }), {
      status: 200,
      headers: { 'content-type': 'application/json', etag: '"2"' },
    });
  }) as typeof fetch;

  const getSettings = await app.handle(
    new Request('http://gateway.local/api/v1/live-digest/settings', {
      headers: { 'x-tinadec-storage-id': 'scope-alpha' },
    }),
  );
  assert.equal(getSettings.status, 200);
  assert.deepEqual(await getSettings.json(), { ok: true });
  assert.equal(calls.at(-1)!.path, '/api/v1/live-digest/settings');
  assert.equal(calls.at(-1)!.method, 'GET');
  assert.equal(calls.at(-1)!.headers.get('x-tinadec-storage-id'), 'scope-alpha');

  const putBody = {
    mode: 'always',
    provider_instance_id: providerId,
    model: 'gemini-2.5-flash',
    clear_model_binding: false,
  };
  const putSettings = await app.handle(
    new Request('http://gateway.local/api/v1/live-digest/settings', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(putBody),
    }),
  );
  assert.equal(putSettings.status, 200);
  assert.equal(calls.at(-1)!.path, '/api/v1/live-digest/settings');
  assert.equal(calls.at(-1)!.method, 'PUT');
  assert.deepEqual(JSON.parse(calls.at(-1)!.body ?? '{}'), putBody);

  const getSnapshot = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${runId}/live-digest`),
  );
  assert.equal(getSnapshot.status, 200);
  assert.equal(calls.at(-1)!.path, `/api/v1/runs/${runId}/live-digest`);
  assert.equal(calls.at(-1)!.method, 'GET');
});

test('live-digest SSE stream forwards cursor and streams chunks unbuffered via proxySse', { concurrency: false }, async () => {
  let upstreamPath = '';
  let upstreamHeaders = new Headers();
  let releaseSecondChunk!: () => void;
  const secondChunkGate = new Promise<void>((resolve) => {
    releaseSecondChunk = resolve;
  });

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    upstreamPath = url.pathname + url.search;
    upstreamHeaders = new Headers(init?.headers);

    const encoder = new TextEncoder();
    const stream = new ReadableStream<Uint8Array>({
      async start(controller) {
        controller.enqueue(
          encoder.encode('id: 8\nevent: l0_delta\ndata: {"seq":8,"headline":"折叠工具输出"}\n\n'),
        );
        await secondChunkGate;
        controller.enqueue(
          encoder.encode('id: 9\nevent: done\ndata: {"seq":9,"status":"completed"}\n\n'),
        );
        controller.close();
      },
    });

    return new Response(stream, {
      status: 200,
      headers: { 'content-type': 'text/event-stream' },
    });
  }) as typeof fetch;

  const response = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${runId}/live-digest/stream`, {
      headers: { 'Last-Event-ID': '7', 'x-tinadec-storage-id': 'scope-stream' },
    }),
  );

  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type') ?? '', /text\/event-stream/);
  assert.equal(response.headers.get('x-accel-buffering'), 'no');
  assert.equal(upstreamPath, `/api/v1/runs/${runId}/live-digest/stream?after_seq=7`);
  assert.equal(upstreamHeaders.get('last-event-id'), '7');
  assert.equal(upstreamHeaders.get('x-tinadec-storage-id'), 'scope-stream');

  const reader = response.body!.getReader();
  const decoder = new TextDecoder();

  // Prove unbuffered streaming: first chunk arrives BEFORE secondChunkGate resolves!
  const first = await reader.read();
  assert.equal(first.done, false);
  const firstText = decoder.decode(first.value);
  assert.match(firstText, /event: l0_delta/);

  releaseSecondChunk();
  let remaining = '';
  while (true) {
    const next = await reader.read();
    if (next.done) break;
    remaining += decoder.decode(next.value, { stream: true });
  }
  assert.match(remaining, /event: done/);
});

test('live-digest stream preserves Core 404 refusal and 502 unreachable errors cleanly', { concurrency: false }, async () => {
  globalThis.fetch = (async () =>
    new Response(JSON.stringify({ code: 'NOT_FOUND', message: 'Run was not found.' }), {
      status: 404,
      headers: { 'content-type': 'application/json' },
    })) as unknown as typeof fetch;

  const notFound = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${runId}/live-digest/stream`),
  );
  assert.equal(notFound.status, 404);
  const body = (await notFound.json()) as Record<string, unknown>;
  assert.equal(body.code, 'NOT_FOUND');

  globalThis.fetch = (async () => {
    throw new TypeError('connect ECONNREFUSED');
  }) as unknown as typeof fetch;

  const unreachable = await app.handle(
    new Request(`http://gateway.local/api/v1/runs/${runId}/live-digest`),
  );
  assert.equal(unreachable.status, 502);
  const errBody = (await unreachable.json()) as Record<string, unknown>;
  assert.equal(errBody.code, 'CORE_UNREACHABLE');
});

test('live-digest paths and OpenAPI schemas are exposed in /docs/json', { concurrency: false }, async () => {
  const res = await app.handle(new Request('http://gateway.local/docs/json'));
  assert.equal(res.status, 200);
  const doc = (await res.json()) as {
    paths: Record<string, Record<string, unknown>>;
    components: { schemas: Record<string, unknown> };
  };

  assert.ok(doc.paths['/api/v1/live-digest/settings']?.get);
  assert.ok(doc.paths['/api/v1/live-digest/settings']?.put);
  assert.ok(doc.paths['/api/v1/runs/{runId}/live-digest']?.get);
  assert.ok(doc.paths['/api/v1/runs/{runId}/live-digest/stream']?.get);

  for (const schemaName of [
    'LiveDigestSettings',
    'UpdateLiveDigestSettingsRequest',
    'LiveDigestSnapshot',
    'LiveDigestChapter',
    'ContextWaterline',
    'GovernanceSignal',
    'TeammateThought',
    'SentinelAlert',
  ]) {
    assert.ok(doc.components.schemas[schemaName], `Missing OpenAPI schema ${schemaName}`);
  }
});
