import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { app } from './index.js';

const originalFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = originalFetch;
});

test('full-duplex runtime routes preserve Core paths, query names, and command bodies', async () => {
  const requests: Array<{ url: string; method: string; body: string | undefined }> = [];
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    requests.push({
      url: typeof input === 'string' ? input : input.toString(),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined
    });
    return new Response(JSON.stringify({ proxied: true }), {
      status: 202,
      headers: { 'content-type': 'application/json' }
    });
  }) as typeof fetch;

  const calls = [
    new Request('http://gateway.local/api/v1/application-modes'),
    new Request('http://gateway.local/api/v1/agent-modes?application_mode=space'),
    new Request('http://gateway.local/api/v1/runs/run-1/orchestration'),
    new Request('http://gateway.local/api/v1/runs/run-1/agent-lineage'),
    new Request('http://gateway.local/api/v1/sessions/session-1/context-versions?run_id=run-1&limit=12'),
    new Request('http://gateway.local/api/v1/memory-candidates?status=proposed&run_id=run-1'),
    new Request('http://gateway.local/api/v1/agent-candidates?status=proposed&run_id=run-1'),
    new Request('http://gateway.local/api/v1/runs/run-1/control', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ command: 'pause', expected_context_revision: 7 })
    }),
    new Request('http://gateway.local/api/v1/memory-candidates/memory-1/promote', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ reason: 'confirmed by reviewer' })
    }),
    new Request('http://gateway.local/api/v1/agent-candidates/agent-1/reject', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ reason: 'insufficient evidence' })
    })
  ];

  const responses: Response[] = [];
  for (const request of calls) responses.push(await app.handle(request));
  for (const response of responses) {
    assert.equal(response.status, 202);
    assert.deepEqual(await response.json(), { proxied: true });
  }

  assert.deepEqual(requests.map((request) => [request.method, request.url]), [
    ['GET', 'http://127.0.0.1:48731/api/v1/application-modes'],
    ['GET', 'http://127.0.0.1:48731/api/v1/agent-modes?application_mode=space'],
    ['GET', 'http://127.0.0.1:48731/api/v1/runs/run-1/orchestration'],
    ['GET', 'http://127.0.0.1:48731/api/v1/runs/run-1/agent-lineage'],
    ['GET', 'http://127.0.0.1:48731/api/v1/sessions/session-1/context-versions?run_id=run-1&limit=12'],
    ['GET', 'http://127.0.0.1:48731/api/v1/memory-candidates?status=proposed&run_id=run-1'],
    ['GET', 'http://127.0.0.1:48731/api/v1/agent-candidates?status=proposed&run_id=run-1'],
    ['POST', 'http://127.0.0.1:48731/api/v1/runs/run-1/control'],
    ['POST', 'http://127.0.0.1:48731/api/v1/memory-candidates/memory-1/promote'],
    ['POST', 'http://127.0.0.1:48731/api/v1/agent-candidates/agent-1/reject']
  ]);
  assert.deepEqual(JSON.parse(requests[7]!.body ?? ''), { command: 'pause', expected_context_revision: 7 });
  assert.deepEqual(JSON.parse(requests[8]!.body ?? ''), { reason: 'confirmed by reviewer' });
  assert.deepEqual(JSON.parse(requests[9]!.body ?? ''), { reason: 'insufficient evidence' });
});

test('invoke-stream preserves the full-duplex request envelope and Core SSE response', async () => {
  let forwarded: { url: string; method: string; body: string | undefined } | undefined;
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    forwarded = {
      url: typeof input === 'string' ? input : input.toString(),
      method: init?.method ?? 'GET',
      body: typeof init?.body === 'string' ? init.body : undefined
    };
    return new Response('data: {"kind":"ack","seq":1}\n\n', {
      status: 200,
      headers: { 'content-type': 'text/event-stream' }
    });
  }) as typeof fetch;

  const envelope = {
    content: 'Implement the approved change.',
    client_message_id: 'client-message-1',
    application_mode: 'space',
    agent_mode: 'agent',
    permission_mode: 'ask',
    target_run_id: 'run-1',
    expected_context_revision: 7
  };
  const response = await app.handle(new Request('http://gateway.local/api/v1/sessions/session-1/invoke-stream', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(envelope)
  }));

  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type') ?? '', /^text\/event-stream/);
  assert.equal(await response.text(), 'data: {"kind":"ack","seq":1}\n\n');
  assert.deepEqual(forwarded, {
    url: 'http://127.0.0.1:48731/api/v1/sessions/session-1/invoke-stream',
    method: 'POST',
    body: JSON.stringify(envelope)
  });
});
