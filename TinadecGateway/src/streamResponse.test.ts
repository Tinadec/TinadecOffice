import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';
import { getConfig } from './config.js';
import { proxySse, proxySseWithCursor, proxyStream as proxyCoreStream } from './coreClient.js';
import { proxyToolRuntimeSse, proxyToolRuntimeStream } from './toolRuntimeClient.js';
import { proxyStream } from './streaming.js';
import { createStreamProxyResponse } from './streamResponse.js';

const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });
const frames = 'event: stream.ready\ndata: {}\n\nid: 7\nevent: delta\ndata: {"text":"你好"}\n\n';
const streamHeaders = {
  'content-type': 'text/event-stream; charset=utf-8', 'transfer-encoding': 'chunked',
  connection: 'keep-alive, X-Upstream-Only', 'keep-alive': 'timeout=30',
  'x-upstream-only': 'remove', 'x-history-cursor': '7', 'x-tinadec-storage-id': 'scope-7',
};

test('stream response removes hop-by-hop and connection-declared headers while retaining metadata and bytes', async () => {
  const source = new Response(frames, { status: 202, statusText: 'Accepted', headers: {
    ...streamHeaders, te: 'trailers', trailer: 'x-checksum', upgrade: 'h2c',
    'proxy-authenticate': 'Basic', 'proxy-authorization': 'Basic private',
  } });
  const response = createStreamProxyResponse(source);
  assert.equal(response.status, 202);
  assert.equal(response.statusText, 'Accepted');
  for (const name of ['connection', 'keep-alive', 'transfer-encoding', 'x-upstream-only',
    'te', 'trailer', 'upgrade', 'proxy-authenticate', 'proxy-authorization']) assert.equal(response.headers.has(name), false, name);
  assert.equal(source.headers.get('transfer-encoding'), 'chunked');
  assert.equal(response.headers.get('content-type'), streamHeaders['content-type']);
  assert.equal(response.headers.get('x-history-cursor'), '7');
  assert.equal(response.headers.get('x-tinadec-storage-id'), 'scope-7');
  assert.equal(await response.text(), frames);
});

test('stream response forwards before EOF, propagates cancellation and preserves body failures', async () => {
  let cancellation: unknown;
  const source = new Response(new ReadableStream<Uint8Array>({
    start(controller) { controller.enqueue(new TextEncoder().encode(frames)); },
    cancel(reason) { cancellation = reason; },
  }), { headers: streamHeaders });
  const reader = createStreamProxyResponse(source).body!.getReader();
  assert.equal(new TextDecoder().decode((await reader.read()).value), frames);
  const reason = new Error('downstream closed');
  await reader.cancel(reason);
  assert.equal(cancellation, reason);
  const failure = new Error('upstream body failed');
  const failed = new Response(new ReadableStream<Uint8Array>({ start(controller) { controller.error(failure); } }));
  await assert.rejects(createStreamProxyResponse(failed).body!.getReader().read(), error => error === failure);
});

test('Core and Tool Runtime stream transports share response framing removal', { concurrency: false }, async () => {
  const config = getConfig(), previous = config.toolRuntimeUrl;
  config.toolRuntimeUrl = 'http://tool-runtime.test';
  try {
    globalThis.fetch = (async () => new Response(frames, { headers: streamHeaders })) as unknown as typeof fetch;
    for (const call of [
      () => proxySse('/api/v1/events'), () => proxySseWithCursor('/api/v1/events', '7'),
      () => proxyCoreStream('/api/v1/sessions/s/logs/stream'),
      () => proxyToolRuntimeSse('/events'), () => proxyToolRuntimeStream('/stream'),
      () => proxyStream({ target: 'core', path: '/api/v1/sessions/s/logs/stream' }),
      () => proxyStream({ target: 'tool_runtime', path: '/stream' }),
    ]) {
      const response = await call();
      assert.equal(response.headers.has('transfer-encoding'), false);
      assert.equal(response.headers.has('connection'), false);
      assert.equal(response.headers.get('x-history-cursor'), '7');
      assert.equal(await response.text(), frames);
    }
  } finally { config.toolRuntimeUrl = previous; }
});

test('run, session events, logs and attachment routes retain native response cancellation', { concurrency: false }, async () => {
  for (const route of ['/api/v1/runs/r/stream?after_seq=7', '/api/v1/events?session_id=s&replay=false',
    '/api/v1/sessions/s/logs', '/api/v1/sessions/s/logs/stream', '/api/v1/attachments/a/content']) {
    const controller = new AbortController();
    let receivedSignal: AbortSignal | undefined, cancelled = false;
    globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
      receivedSignal = init?.signal ?? undefined;
      return new Response(new ReadableStream<Uint8Array>({
        start(stream) { stream.enqueue(new TextEncoder().encode(frames)); },
        cancel() { cancelled = true; },
      }), { headers: { ...streamHeaders, 'content-disposition': 'attachment; filename="result.txt"' } });
    }) as unknown as typeof fetch;
    const response = await app.handle(new Request('http://gateway.local' + route, { signal: controller.signal }));
    assert.equal(response.status, 200, route);
    assert.equal(receivedSignal, controller.signal, route);
    assert.equal(response.headers.has('transfer-encoding'), false, route);
    assert.equal(response.headers.has('connection'), false, route);
    assert.equal(response.headers.has('x-upstream-only'), false, route);
    assert.equal(response.headers.get('x-history-cursor'), '7', route);
    assert.equal(response.headers.get('content-disposition'), 'attachment; filename="result.txt"', route);
    const reader = response.body!.getReader();
    assert.equal(new TextDecoder().decode((await reader.read()).value), frames, route);
    await reader.cancel();
    assert.equal(cancelled, true, route);
    controller.abort();
  }
});

test('generic and Tool Runtime streams keep caller cancellation distinct from unavailable upstream', { concurrency: false }, async () => {
  const config = getConfig(), previous = config.toolRuntimeUrl;
  config.toolRuntimeUrl = 'http://tool-runtime.test';
  try {
    for (const transport of [
      { code: 'CORE_UNREACHABLE', call: (signal?: AbortSignal) => proxyStream({ target: 'core', path: '/stream', signal }) },
      { code: 'TOOL_RUNTIME_UNREACHABLE', call: (signal?: AbortSignal) => proxyStream({ target: 'tool_runtime', path: '/stream', signal }) },
      { code: 'TOOL_RUNTIME_UNREACHABLE', call: (signal?: AbortSignal) => proxyToolRuntimeSse('/events', { signal }) },
      { code: 'TOOL_RUNTIME_UNREACHABLE', call: (signal?: AbortSignal) => proxyToolRuntimeStream('/stream', { signal }) },
    ]) {
      const controller = new AbortController(), reason = new Error('cancelled before response headers');
      controller.abort(reason);
      globalThis.fetch = (async () => { throw reason; }) as unknown as typeof fetch;
      await assert.rejects(transport.call(controller.signal), error => error === reason);
      const aborted = new DOMException('connection closed', 'AbortError');
      globalThis.fetch = (async () => { throw aborted; }) as unknown as typeof fetch;
      await assert.rejects(transport.call(), error => error === aborted);
      globalThis.fetch = (async () => { throw new TypeError('connection refused'); }) as unknown as typeof fetch;
      const response = await transport.call();
      assert.equal(response.status, 502);
      assert.equal((await response.json() as { code: string }).code, transport.code);
    }
  } finally { config.toolRuntimeUrl = previous; }
});

test('stream redirects remain manual with supplied host credentials', { concurrency: false }, async () => {
  const config = getConfig(), previous = config.toolRuntimeUrl;
  config.toolRuntimeUrl = 'http://tool-runtime.test';
  try {
    let calls = 0;
    globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
      calls++;
      assert.equal(init?.redirect, 'manual');
      assert.equal(new Headers(init?.headers).get('x-tinadec-host-control'), 'supplied-host');
      return new Response(null, { status: 302, headers: { location: 'https://unverified.test/stream' } });
    }) as unknown as typeof fetch;
    const headers = { 'x-tinadec-host-control': 'supplied-host' };
    for (const call of [
      () => proxyStream({ target: 'core', path: '/stream', headers }),
      () => proxyStream({ target: 'tool_runtime', path: '/stream', headers }),
      () => proxyToolRuntimeSse('/events', { headers, redirect: 'follow' }),
      () => proxyToolRuntimeStream('/stream', { headers, redirect: 'follow' }),
    ]) {
      const response = await call();
      assert.equal(response.status, 302);
      assert.equal(response.headers.get('location'), 'https://unverified.test/stream');
    }
    assert.equal(calls, 4);
  } finally { config.toolRuntimeUrl = previous; }
});
