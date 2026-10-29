const test = require('node:test');
const assert = require('node:assert/strict');

const { createCompanionApiBridge } = require('./companionApi.cjs');

const TOKEN = 'a'.repeat(64);
const BASE = 'http://127.0.0.1:48730';
const COMPANION = { id: 7 };
const OTHER = { id: 8 };
const COMPANION_ID = '11111111-1111-4111-8111-111111111111';
const CONVERSATION_ID = '22222222-2222-4222-8222-222222222222';

function upstream(status, text) {
  return { status, text: async () => text };
}

function makeBridge(overrides = {}) {
  const calls = [];
  const bridge = createCompanionApiBridge({
    baseUrl: BASE,
    token: TOKEN,
    isCompanionWindowSender: sender => sender === COMPANION,
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      return upstream(200, '{"ok":true}');
    },
    ...overrides,
  });
  return { bridge, calls };
}

test('refuses a sender that is not the companion window, before any network call', async () => {
  const { bridge, calls } = makeBridge();
  await assert.rejects(
    () => bridge({ sender: OTHER }, { method: 'GET', path: '/api/v1/companions' }),
    /only available to the companion window/,
  );
  assert.equal(calls.length, 0);
});

test('rejects paths outside the whitelist without a network request', async () => {
  const { bridge, calls } = makeBridge();
  const requests = [
    { method: 'GET', path: '/api/v1/sessions' },
    { method: 'GET', path: 'https://evil.example/api/v1/companions' },
    { method: 'GET', path: '/api/v1/companions/../sessions' },
    { method: 'GET', path: '/api/v1/companions/%2e%2e/sessions' },
    { method: 'DELETE', path: `/api/v1/companions/${COMPANION_ID}` },
    { method: 'POST', path: `/api/v1/companions/${COMPANION_ID}/memories`, body: { content: 'x' } },
    { method: 'PATCH', path: `/api/v1/companions/${COMPANION_ID}/connections/${COMPANION_ID}`, body: {} },
    { method: 'GET', path: '/api/v1/approvals' },
    { method: 'GET', path: '/api/v1/companions?unknown=1' },
    { method: 'GET', path: '/api/v1/companions#fragment' },
    { method: 'GET', path: '/api/v1/companions/not-a-guid' },
    { method: 'GET', path: '/api/v1/companions', body: { extra: true } },
  ];
  for (const payload of requests) {
    const result = await bridge({ sender: COMPANION }, payload);
    assert.equal(result.status, 400, `${payload.method} ${payload.path} must be refused`);
    assert.equal(result.body.code, 'invalid_request');
  }
  assert.equal(calls.length, 0);
});

test('does not call the gateway when the host credential is missing', async () => {
  const { bridge, calls } = makeBridge({ token: '' });
  const result = await bridge({ sender: COMPANION }, { method: 'GET', path: '/api/v1/companions' });
  assert.deepEqual(result, { status: 503, body: { code: 'host_not_ready' } });
  assert.equal(calls.length, 0);
});

test('injects the host credential and never a storage identity', async () => {
  const { bridge, calls } = makeBridge();
  const path = `/api/v1/companions/${COMPANION_ID}/conversations`;
  const result = await bridge({ sender: COMPANION }, { method: 'GET', path });
  assert.equal(result.status, 200);
  assert.deepEqual(result.body, { ok: true });

  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, `${BASE}${path}`);
  assert.equal(calls[0].init.headers['x-tinadec-host-control'], TOKEN);
  assert.equal(calls[0].init.headers.accept, 'application/json');
  assert.equal(calls[0].init.headers['content-type'], 'application/json');
  assert.match(calls[0].init.headers['x-request-id'], /^[0-9a-f-]{36}$/);
  assert.equal(
    Object.keys(calls[0].init.headers).some(key => key.toLowerCase() === 'x-tinadec-storage-id'),
    false,
    'companion routes are pinned to the user scope by Core; the proxy must not send a scope',
  );
  assert.equal(calls[0].init.redirect, 'error');
});

test('allows the documented query keys and forwards them verbatim', async () => {
  const { bridge, calls } = makeBridge();
  const path = `/api/v1/companion-conversations/${CONVERSATION_ID}/messages?after_seq=0&limit=5`;
  const result = await bridge({ sender: COMPANION }, { method: 'GET', path });
  assert.equal(result.status, 200);
  assert.equal(calls[0].url, `${BASE}${path}`);

  const archived = await bridge({ sender: COMPANION }, { method: 'GET', path: '/api/v1/companions?include_archived=true' });
  assert.equal(archived.status, 200);
  assert.equal(calls[1].url, `${BASE}/api/v1/companions?include_archived=true`);
});

test('passes upstream status and JSON through, including refusals', async () => {
  const { bridge } = makeBridge({
    fetchImpl: async () => upstream(412, '{"code":"companion_revision_conflict","message":"stale"}'),
  });
  const result = await bridge({ sender: COMPANION }, { method: 'PATCH', path: '/api/v1/companion-settings', body: { autonomy_paused: true } });
  assert.deepEqual(result, {
    status: 412,
    body: { code: 'companion_revision_conflict', message: 'stale' },
  });

  const empty = makeBridge({ fetchImpl: async () => upstream(204, '') });
  const paused = await empty.bridge({ sender: COMPANION }, {
    method: 'POST',
    path: `/api/v1/companion-conversations/${CONVERSATION_ID}/pause`,
    body: { paused: true },
  });
  assert.deepEqual(paused, { status: 204, body: null });
});

test('reports a non-JSON upstream body as an invalid_upstream_response', async () => {
  const { bridge } = makeBridge({ fetchImpl: async () => upstream(200, 'not json') });
  const result = await bridge({ sender: COMPANION }, { method: 'GET', path: '/api/v1/companions' });
  assert.deepEqual(result, { status: 200, body: { code: 'invalid_upstream_response' } });
});

test('sends write bodies as JSON and rejects bodies over 64 KiB', async () => {
  const { bridge, calls } = makeBridge();
  const body = { companion_id: COMPANION_ID, share_history: false };
  const result = await bridge({ sender: COMPANION }, {
    method: 'POST',
    path: `/api/v1/companion-conversations/${CONVERSATION_ID}/members`,
    body,
  });
  assert.equal(result.status, 200);
  assert.equal(calls[0].init.body, JSON.stringify(body));

  const oversized = { content: 'x'.repeat(64 * 1024) };
  const rejected = await bridge({ sender: COMPANION }, {
    method: 'POST',
    path: `/api/v1/companion-conversations/${CONVERSATION_ID}/messages`,
    body: oversized,
  });
  assert.equal(rejected.status, 400);
  assert.equal(rejected.body.code, 'invalid_request');
  assert.equal(calls.length, 1, 'an oversized body must never reach the gateway');
});

test('maps transport failures and timeouts to 502 without leaking the credential', async () => {
  const { bridge } = makeBridge({
    fetchImpl: async () => { throw new Error('connect ECONNREFUSED 127.0.0.1:48730'); },
  });
  const failed = await bridge({ sender: COMPANION }, { method: 'GET', path: '/api/v1/companions' });
  assert.deepEqual(failed, { status: 502, body: { code: 'gateway_unreachable' } });
  assert.equal(JSON.stringify(failed).includes(TOKEN), false);
  assert.equal(JSON.stringify(failed).includes('ECONNREFUSED'), false);

  const { bridge: timed } = makeBridge({
    timeoutMs: 10,
    fetchImpl: (_url, init) => new Promise((_resolve, reject) => {
      init.signal.addEventListener('abort', () => reject(new Error(`${TOKEN} aborted`)), { once: true });
    }),
  });
  const timeout = await timed({ sender: COMPANION }, { method: 'GET', path: '/api/v1/companions' });
  assert.deepEqual(timeout, { status: 502, body: { code: 'gateway_unreachable' } });
  assert.equal(JSON.stringify(timeout).includes(TOKEN), false);
});
