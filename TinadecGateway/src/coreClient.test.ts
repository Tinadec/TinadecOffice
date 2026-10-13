import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { coreEndpoint, proxyJson, proxyRaw, proxySse, proxySseWithCursor, proxyStream } from './coreClient.js';

const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });

const transports = [
  { name: 'raw', call: (signal?: AbortSignal) => proxyRaw('/api/v1/storage/scopes', { signal }) },
  { name: 'json', call: (signal?: AbortSignal) => proxyJson('/api/v1/projects', { signal }) },
  { name: 'sse', call: (signal?: AbortSignal) => proxySse('/api/v1/sessions/one/events', { signal }) },
  { name: 'sse cursor', call: (signal?: AbortSignal) => proxySseWithCursor('/api/v1/runs/one/stream', '2', undefined, { signal }) },
  { name: 'stream', call: (signal?: AbortSignal) => proxyStream('/api/v1/attachments/one', { signal }) },
];

test('Core transports preserve caller cancellation and AbortError without fabricating 502', { concurrency: false }, async () => {
  for (const transport of transports) {
    const controller = new AbortController();
    const reason = new Error('caller cancelled');
    controller.abort(reason);
    globalThis.fetch = (async () => { throw reason; }) as typeof fetch;
    await assert.rejects(transport.call(controller.signal), error => error === reason, transport.name);

    const aborted = new DOMException('cancelled fetch', 'AbortError');
    globalThis.fetch = (async () => { throw aborted; }) as typeof fetch;
    await assert.rejects(transport.call(), error => error === aborted, transport.name);
  }
});

test('Core transports still report unavailable upstreams as 502', { concurrency: false }, async () => {
  globalThis.fetch = (async () => { throw new TypeError('connection refused'); }) as typeof fetch;
  for (const transport of transports) {
    const result = await transport.call();
    assert.equal(result.status, 502, transport.name);
    const data = result instanceof Response ? await result.json() : result.data;
    assert.equal((data as { code: string }).code, 'CORE_UNREACHABLE', transport.name);
  }
});

test('coreEndpoint resolves API paths against the configured Core URL', () => {
  assert.equal(coreEndpoint('/api/v1/health'), 'http://127.0.0.1:48731/api/v1/health');
});

