import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });

test('bounded history and activity preserve query, scope, DTO and history conflict', { concurrency: false }, async () => {
  const requests: Array<{ url: URL; headers: Headers }> = [];
  const page = { items: [{ id: 'm', sequence: 8, attachments: [] }], runs: [], has_more: true,
    next_before_sequence: 8, history_revision: 20, revert_epoch: 2 };
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    requests.push({ url, headers: new Headers(init?.headers) });
    return Response.json(url.pathname.endsWith('/activity') ? { events: [], tool_executions: [], runs: [] } : page);
  }) as typeof fetch;
  const headers = { 'X-Tinadec-Storage-Id': 'scope-7' };
  const result = await app.handle(new Request('http://gateway/api/v1/sessions/s/messages/page?limit=100&before_sequence=9&expected_revert_epoch=2', { headers }));
  assert.equal(result.status, 200);
  assert.deepEqual(await result.json(), page);
  assert.equal(requests[0]!.url.searchParams.get('before_sequence'), '9');
  assert.equal(requests[0]!.url.searchParams.get('expected_revert_epoch'), '2');
  assert.equal(requests[0]!.headers.get('x-tinadec-storage-id'), 'scope-7');
  await app.handle(new Request('http://gateway/api/v1/sessions/s/activity?run_ids=r1,r2', { headers }));
  assert.equal(requests[1]!.url.searchParams.get('run_ids'), 'r1,r2');
  await app.handle(new Request('http://gateway/api/v1/sessions/s/tool-executions?run_id=r1&limit=12', { headers }));
  assert.equal(requests[2]!.url.searchParams.get('run_id'), 'r1');
  assert.equal(requests[2]!.url.searchParams.get('limit'), '12');
  globalThis.fetch = (async () => Response.json({ code: 'history_changed', detail: 'History was edited.' }, { status: 409 })) as unknown as typeof fetch;
  const conflict = await app.handle(new Request('http://gateway/api/v1/sessions/s/messages/page?before_sequence=9'));
  assert.equal(conflict.status, 409);
  assert.equal((await conflict.json() as { code: string }).code, 'history_changed');
});

test('live-only events forward replay and control frames without rewriting cursor', { concurrency: false }, async () => {
  let requested: URL | undefined;
  const frames = 'event: stream.ready\ndata: {"session_id":"s","history_revision":2,"revert_epoch":0}\n\n';
  globalThis.fetch = (async (input: RequestInfo | URL) => {
    requested = new URL(String(input));
    return new Response(frames, { headers: { 'content-type': 'text/event-stream' } });
  }) as typeof fetch;
  const response = await app.handle(new Request('http://gateway/api/v1/events?session_id=s&replay=false'));
  assert.equal(requested?.searchParams.get('replay'), 'false');
  assert.equal(requested?.searchParams.get('sessionId'), 's');
  assert.equal(await response.text(), frames);
});

test('normal admission receipt preserves exact message identity', { concurrency: false }, async () => {
  const receipt = { interaction_id: 'turn', run_id: 'run', message_id: 'persisted-message', client_message_id: 'pending-message', status: 'admitted' };
  globalThis.fetch = (async () => Response.json(receipt, { status: 201 })) as unknown as typeof fetch;
  const response = await app.handle(new Request('http://gateway/api/v1/sessions/s/interactions', {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ content: 'same words', client_message_id: 'pending-message', dispatch_mode: 'queued' }),
  }));
  assert.equal(response.status, 201);
  assert.deepEqual(await response.json(), receipt);
});
