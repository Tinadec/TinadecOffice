import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';
import { wrapSseWithKeepalive } from './streaming.js';

const originalFetch = globalThis.fetch;
afterEach(() => {
  globalThis.fetch = originalFetch;
});

function samplePreferencesDoc(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    version: 1,
    file_path: 'C:/Users/tester/.tinadec/preferences.toml',
    content_hash: 'sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef',
    updated_at: '2026-11-25T05:00:00Z',
    appearance: {
      theme: 'dark',
      accent: 'monochrome',
      custom_accent_hex: '#18181b',
      panel_effect: 'translucent',
      panel_preset: 'translucent',
      panel_opacity: 76,
      panel_blur: 0,
      panel_saturation: 100,
      workspace_view: 'grid',
      workspace_folder_tone: 'amber',
    },
    interaction: {
      workspace_drop_action: 'link',
      spatial_promote_mode: 'merge',
      reduced_motion: 'system',
    },
    ...overrides,
  };
}

test('GET, PUT, and PATCH /api/v1/preferences proxy cleanly and normalize input aliases', { concurrency: false }, async () => {
  const calls: Array<{ url: string; method: string; body: Record<string, unknown> | undefined; headers: Headers }> = [];

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? 'GET';
    calls.push({
      url,
      method,
      body: typeof init?.body === 'string' ? (JSON.parse(init.body) as Record<string, unknown>) : undefined,
      headers: new Headers(init?.headers),
    });

    return new Response(JSON.stringify(samplePreferencesDoc()), {
      status: 200,
      headers: {
        'content-type': 'application/json',
        etag: '"sha256:0123"',
      },
    });
  }) as unknown as typeof fetch;

  // 1. GET /api/v1/preferences
  const getRes = await app.handle(
    new Request('http://gateway.local/api/v1/preferences', {
      headers: {
        'x-tinadec-storage-id': 'user-scope',
        'x-request-id': 'req-prefs-get-1',
      },
    }),
  );
  assert.equal(getRes.status, 200);
  assert.equal(getRes.headers.get('etag'), '"sha256:0123"');
  assert.equal(getRes.headers.get('x-request-id'), 'req-prefs-get-1');
  const getJson = (await getRes.json()) as Record<string, unknown>;
  assert.equal(getJson.version, 1);
  assert.equal(calls[0]?.method, 'GET');
  assert.match(calls[0]?.url ?? '', /\/api\/v1\/preferences$/);
  assert.equal(calls[0]?.headers.get('x-tinadec-storage-id'), 'user-scope');

  // 2. PUT /api/v1/preferences (full replace + alias normalization)
  const putRes = await app.handle(
    new Request('http://gateway.local/api/v1/preferences', {
      method: 'PUT',
      headers: {
        'content-type': 'application/json',
        'if-match': '"sha256:prev"',
      },
      body: JSON.stringify({
        expected_content_hash: 'sha256:prev',
        appearance: {
          theme: 'light',
          accent: 'blue',
          custom_accent_hex: '#3B82F6',
          panel_effect: 'opaque',
          workspace_view: 'list',
          workspace_folder_tone: 'teal',
        },
        interaction: {
          workspace_drop_action: 'ask',
          spatial_promote_mode: 'parent_with_children',
          reduced_motion: 'on',
        },
      }),
    }),
  );
  assert.equal(putRes.status, 200);
  assert.equal(calls[1]?.method, 'PUT');
  assert.equal(calls[1]?.headers.get('if-match'), '"sha256:prev"');
  assert.deepEqual(calls[1]?.body, {
    expected_content_hash: 'sha256:prev',
    appearance: {
      theme: 'light',
      accent: 'blue',
      custom_accent_hex: '#3b82f6',
      panel_effect: 'solid',
      workspace_view: 'list',
      workspace_folder_tone: 'teal',
    },
    interaction: {
      workspace_drop_action: 'ask',
      spatial_promote_mode: 'parent_children',
      reduced_motion: 'on',
    },
  });

  // 3. PUT /api/v1/preferences with reset_defaults: true (sections omitted)
  const resetRes = await app.handle(
    new Request('http://gateway.local/api/v1/preferences', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ reset_defaults: true, expected_content_hash: null }),
    }),
  );
  assert.equal(resetRes.status, 200);
  assert.deepEqual(calls[2]?.body, { reset_defaults: true, expected_content_hash: null });

  // 4. PATCH /api/v1/preferences with partial sections and legacy input aliases
  for (const [alias, expected] of [
    ['merge_single', 'merge'],
    ['split_many', 'split'],
    ['parent_with_children', 'parent_children'],
  ] as const) {
    const patchRes = await app.handle(
      new Request('http://gateway.local/api/v1/preferences', {
        method: 'PATCH',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          expected_content_hash: 'sha256:abc',
          appearance: {
            panel_effect: 'opaque',
            workspace_folder_tone: 'coral',
          },
          interaction: {
            spatial_promote_mode: alias,
          },
        }),
      }),
    );
    assert.equal(patchRes.status, 200);
    const forwarded = calls.at(-1)?.body;
    assert.deepEqual(forwarded, {
      expected_content_hash: 'sha256:abc',
      appearance: {
        panel_effect: 'solid',
        workspace_folder_tone: 'coral',
      },
      interaction: {
        spatial_promote_mode: expected,
      },
    });
  }

  // 5. PUT and PATCH with #61 material preset & custom slider fields (0..100, 0..32, 100..200)
  const putMaterialRes = await app.handle(
    new Request('http://gateway.local/api/v1/preferences', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        appearance: {
          theme: 'dark',
          accent: 'monochrome',
          custom_accent_hex: '#18181b',
          panel_preset: 'frosted',
          panel_opacity: 68,
          panel_blur: 18,
          panel_saturation: 160,
          workspace_view: 'grid',
          workspace_folder_tone: 'amber',
        },
        interaction: {
          workspace_drop_action: 'link',
          spatial_promote_mode: 'merge',
          reduced_motion: 'system',
        },
      }),
    }),
  );
  assert.equal(putMaterialRes.status, 200);
  assert.deepEqual(calls.at(-1)?.body, {
    appearance: {
      theme: 'dark',
      accent: 'monochrome',
      custom_accent_hex: '#18181b',
      panel_effect: 'blur',
      panel_preset: 'frosted',
      panel_opacity: 68,
      panel_blur: 18,
      panel_saturation: 160,
      workspace_view: 'grid',
      workspace_folder_tone: 'amber',
    },
    interaction: {
      workspace_drop_action: 'link',
      spatial_promote_mode: 'merge',
      reduced_motion: 'system',
    },
  });

  const patchCustomRes = await app.handle(
    new Request('http://gateway.local/api/v1/preferences', {
      method: 'PATCH',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({
        appearance: {
          panel_preset: 'custom',
          panel_effect: 'blur',
          panel_opacity: 0,
          panel_blur: 32,
          panel_saturation: 200,
        },
      }),
    }),
  );
  assert.equal(patchCustomRes.status, 200);
  assert.deepEqual(calls.at(-1)?.body, {
    appearance: {
      panel_preset: 'custom',
      panel_effect: 'blur',
      panel_opacity: 0,
      panel_blur: 32,
      panel_saturation: 200,
    },
  });
});

test('PUT and PATCH /api/v1/preferences fail-closed on unknown keys, invalid enums, and malformed bodies', { concurrency: false }, async () => {
  let upstreamCalled = false;
  globalThis.fetch = (async () => {
    upstreamCalled = true;
    return new Response('{}', { status: 200 });
  }) as unknown as typeof fetch;

  const invalidCases: Array<{ method: 'PUT' | 'PATCH'; rawBody: string; label: string }> = [
    {
      method: 'PATCH',
      rawBody: '{not-valid-json',
      label: 'malformed JSON',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ unknown_root_key: true }),
      label: 'unknown root key on PATCH',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ reset_defaults: true }),
      label: 'reset_defaults not permitted on PATCH',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { hacker_stripe: 'yes' } }),
      label: 'unknown appearance key',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ interaction: { auto_submit: true } }),
      label: 'unknown interaction key',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { workspace_folder_tone: 'neon' } }),
      label: 'invalid workspace_folder_tone enum',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { custom_accent_hex: 'blue' } }),
      label: 'invalid custom_accent_hex pattern',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { panel_preset: 'acrylic' } }),
      label: 'invalid panel_preset enum',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { panel_opacity: -1 } }),
      label: 'panel_opacity below 0',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { panel_opacity: 101 } }),
      label: 'panel_opacity above 100',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { panel_opacity: 68.5 } }),
      label: 'non-integer panel_opacity',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { panel_blur: -1 } }),
      label: 'panel_blur below 0',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { panel_blur: 33 } }),
      label: 'panel_blur above 32',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { panel_saturation: 99 } }),
      label: 'panel_saturation below 100',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ appearance: { panel_saturation: 201 } }),
      label: 'panel_saturation above 200',
    },
    {
      method: 'PATCH',
      rawBody: JSON.stringify({ interaction: { spatial_promote_mode: 'explode' } }),
      label: 'invalid spatial_promote_mode enum',
    },
    {
      method: 'PUT',
      rawBody: JSON.stringify({ appearance: { theme: 'dark' } }),
      label: 'PUT missing required fields without reset_defaults',
    },
  ];

  for (const item of invalidCases) {
    const res = await app.handle(
      new Request('http://gateway.local/api/v1/preferences', {
        method: item.method,
        headers: { 'content-type': 'application/json' },
        body: item.rawBody,
      }),
    );
    assert.equal(res.status, 400, `Expected 400 for ${item.label}`);
    assert.match(res.headers.get('content-type') ?? '', /application\/problem\+json/);
    const problem = (await res.json()) as Record<string, unknown>;
    assert.equal(problem.status, 400);
    assert.equal(problem.code, 'invalid_request');
  }

  assert.equal(upstreamCalled, false, 'Invalid payloads must fail-closed before calling Core');
});

test('/api/v1/preferences preserves Core 400/409/412 ProblemDetails and 502 unreachable errors', { concurrency: false }, async () => {
  for (const status of [400, 409, 412] as const) {
    const code = status === 400 ? 'invalid_preferences' : 'preferences_conflict';
    globalThis.fetch = (async () =>
      new Response(
        JSON.stringify({
          type: `https://tinadec.dev/errors/${code}`,
          title: code,
          status,
          code,
          detail: `Simulated ${status} error from Core.`,
          trace_id: `trace-${status}`,
        }),
        {
          status,
          headers: { 'content-type': 'application/problem+json' },
        },
      )) as unknown as typeof fetch;

    const res = await app.handle(
      new Request('http://gateway.local/api/v1/preferences', {
        method: 'PATCH',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          expected_content_hash: 'sha256:stale',
          appearance: { theme: 'dark' },
        }),
      }),
    );

    assert.equal(res.status, status);
    assert.match(res.headers.get('content-type') ?? '', /application\/problem\+json/);
    const body = (await res.json()) as Record<string, unknown>;
    assert.equal(body.status, status);
    assert.equal(body.code, code);
    assert.equal(body.trace_id, `trace-${status}`);
  }

  globalThis.fetch = (async () => {
    throw new TypeError('connect ECONNREFUSED 127.0.0.1:48731');
  }) as unknown as typeof fetch;

  const unreachableGet = await app.handle(new Request('http://gateway.local/api/v1/preferences'));
  assert.equal(unreachableGet.status, 502);
  assert.match(unreachableGet.headers.get('content-type') ?? '', /application\/problem\+json/);
  assert.equal(((await unreachableGet.json()) as Record<string, unknown>).code, 'core_unreachable');
});

test('GET /api/v1/preferences/events streams unbuffered SSE updates, emits keepalive, and forwards upstream errors', { concurrency: false, timeout: 5000 }, async () => {
  let upstreamHeaders = new Headers();
  let receivedSignal: AbortSignal | undefined;
  let releaseSecondEvent!: () => void;
  const secondEventGate = new Promise<void>((resolveDone) => {
    releaseSecondEvent = resolveDone;
  });

  const doc1 = samplePreferencesDoc();
  const doc2 = samplePreferencesDoc({
    appearance: {
      ...doc1.appearance,
      workspace_folder_tone: 'sky',
    },
  });

  globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
    upstreamHeaders = new Headers(init?.headers);
    receivedSignal = init?.signal as AbortSignal | undefined;
    const encoder = new TextEncoder();
    const stream = new ReadableStream<Uint8Array>({
      async start(controller) {
        controller.enqueue(
          encoder.encode(`event: preferences.updated\ndata: ${JSON.stringify(doc1)}\n\n`),
        );
        await secondEventGate;
        controller.enqueue(
          encoder.encode(`event: preferences.updated\ndata: ${JSON.stringify(doc2)}\n\n`),
        );
        controller.close();
      },
    });
    return new Response(stream, {
      status: 200,
      headers: { 'content-type': 'text/event-stream' },
    });
  }) as unknown as typeof fetch;

  const abortController = new AbortController();
  const res = await app.handle(
    new Request('http://gateway.local/api/v1/preferences/events?storage_id=user-scope', {
      headers: { 'Last-Event-ID': 'evt-1' },
      signal: abortController.signal,
    }),
  );

  assert.equal(res.status, 200);
  assert.match(res.headers.get('content-type') ?? '', /text\/event-stream/);
  assert.equal(res.headers.get('cache-control'), 'no-cache, no-transform');
  assert.equal(res.headers.get('connection'), 'keep-alive');
  assert.equal(res.headers.get('x-accel-buffering'), 'no');
  assert.equal(upstreamHeaders.get('x-tinadec-storage-id'), 'user-scope');
  assert.equal(upstreamHeaders.get('last-event-id'), 'evt-1');
  assert.equal(receivedSignal, abortController.signal);

  const reader = res.body!.getReader();
  const decoder = new TextDecoder();

  const firstChunk = await reader.read();
  assert.equal(firstChunk.done, false);
  const firstText = decoder.decode(firstChunk.value);
  assert.match(firstText, /event: preferences\.updated/);
  assert.match(firstText, /"workspace_folder_tone":"amber"/);

  releaseSecondEvent();
  let remaining = '';
  while (true) {
    const next = await reader.read();
    if (next.done) break;
    remaining += decoder.decode(next.value, { stream: true });
  }
  assert.match(remaining, /"workspace_folder_tone":"sky"/);

  // Idle keepalive frame emission (`: keepalive\n\n`)
  const idleSource = new ReadableStream<Uint8Array>({
    start() {
      // Stays silent until aborted.
    },
  });
  const keepaliveAbort = new AbortController();
  const keepaliveStream = wrapSseWithKeepalive(idleSource, 15, keepaliveAbort.signal)!;
  const keepaliveReader = keepaliveStream.getReader();
  const keepaliveChunk = await keepaliveReader.read();
  assert.equal(keepaliveChunk.done, false);
  assert.equal(decoder.decode(keepaliveChunk.value), ': keepalive\n\n');
  keepaliveAbort.abort();

  // Upstream SSE network failure forwards 502 CORE_UNREACHABLE
  globalThis.fetch = (async () => {
    throw new TypeError('connect ECONNREFUSED 127.0.0.1:48731');
  }) as unknown as typeof fetch;

  const sseErr = await app.handle(new Request('http://gateway.local/api/v1/preferences/events'));
  assert.equal(sseErr.status, 502);
  assert.equal(((await sseErr.json()) as Record<string, unknown>).code, 'CORE_UNREACHABLE');
});
